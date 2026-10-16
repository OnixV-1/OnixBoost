using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>Stops winws / OnixBoost service so dpi binaries can be replaced.</summary>
public static class ProcessLocker
{
    public static async Task ReleaseDpiFilesAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Stopping OnixBoost service...");
        try
        {
            RunSilent("sc.exe", $"stop {ServiceManager.ServiceName}");
            await Task.Delay(400);
        }
        catch { }

        progress?.Report("Stopping winws...");
        await WinDivertCleanup.RunAsync(progress);
        await Task.Delay(300);
    }

    public static void KillByName(string name)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill(true);
                    p.WaitForExit(3000);
                }
                catch { }
            }
        }
        catch { }
    }

    public static async Task CopyWithRetryAsync(string src, string dst, int attempts = 8)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                // write to temp then replace — fewer lock races
                var tmp = dst + ".onixnew";
                File.Copy(src, tmp, true);
                if (File.Exists(dst))
                {
                    try { File.SetAttributes(dst, FileAttributes.Normal); } catch { }
                    File.Delete(dst);
                }
                File.Move(tmp, dst);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                KillByName("winws");
                await Task.Delay(350 + i * 200);
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
                KillByName("winws");
                await Task.Delay(350 + i * 200);
            }
        }

        throw new IOException(
            $"Файл занят другой программой:\n{dst}\n\nЗакрой OnixBoost / нажми STOP / заверши winws в Диспетчере задач, затем повтори.",
            last);
    }

    private static void RunSilent(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(8000);
        }
        catch { }
    }
}
