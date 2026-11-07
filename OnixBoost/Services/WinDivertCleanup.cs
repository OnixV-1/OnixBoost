using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// Clears stuck Soft workers. On Windows also stops WinDivert services.
/// On Linux tears down nftables NFQUEUE + nfqws.
/// </summary>
public static class WinDivertCleanup
{
    /// <summary>
    /// Kill Soft DPI only. Must NOT kill dpi/core HY2 (YouTube/Telegram).
    /// </summary>
    public static async Task KillWorkersAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                ProcessNames.KillDpiWorkers();
                if (PlatformOs.IsWindows)
                {
                    ProcessLocker.KillByName("WinDivert");
                    ProcessLocker.KillByName("goodbyedpi");
                }
            }
            catch { }
        });
    }

    public static async Task RunAsync(IProgress<string>? progress = null)
    {
        progress?.Report(PlatformOs.IsLinux ? "nfqws cleanup…" : "WinDivert cleanup…");
        await KillWorkersAsync();

        if (PlatformOs.IsLinux)
        {
            try { await LinuxNfqBootstrap.RemoveAllAsync(); } catch { }
            Logger.Info("nfqws/nft cleanup done");
            return;
        }

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
