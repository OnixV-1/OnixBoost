using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// Clears stuck WinDivert / winws handles so .sys updates and restarts succeed.
/// </summary>
public static class WinDivertCleanup
{
    /// <summary>
    /// Kill winws/WinDivert only. Must NOT kill dpi/core HY2 (YouTube/Telegram) —
    /// Discord connect used to call KillAllWorkers and race-kill a simultaneous YouTube tunnel.
    /// </summary>
    public static async Task KillWorkersAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                ProcessNames.KillDpiWorkers();
                ProcessLocker.KillByName("WinDivert");
                ProcessLocker.KillByName("goodbyedpi");
            }
            catch { }
        });
    }

    public static async Task RunAsync(IProgress<string>? progress = null)
    {
        progress?.Report("WinDivert cleanup…");
        await KillWorkersAsync();

        // Named services sometimes left by old packs — wait for STOPPED before delete
        foreach (var svc in new[] { "WinDivert", "WinDivert1.4", "WinDivert14" })
        {
            try
            {
                await Task.Run(() => RunSilent("sc.exe", $"stop {svc}"));
                for (var i = 0; i < 4; i++)
                {
                    await Task.Delay(150);
                    var q = await Task.Run(() => RunSilentCapture("sc.exe", $"query {svc}"));
                    if (q.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
                        q.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                        q.Contains("1060", StringComparison.Ordinal))
                        break;
                }
                await Task.Run(() => RunSilent("sc.exe", $"delete {svc}"));
            }
            catch { }
        }

        await Task.Delay(100);
        Logger.Info("WinDivert cleanup done");
    }

    private static string RunSilentCapture(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return "";
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return o;
        }
        catch { return ""; }
    }

    private static void RunSilent(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(5000);
        }
        catch { }
    }
}
