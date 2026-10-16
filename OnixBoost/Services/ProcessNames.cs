using System;
using System.Diagnostics;
using System.IO;

namespace OnixBoost.Services;

/// <summary>
/// Worker exes under dpi/core and dpi/stack are also named OnixBoost.exe.
/// Never kill the UI process when stopping workers.
/// </summary>
public static class ProcessNames
{
    public static void KillCoreWorkers()
    {
        KillOnixBoostUnder("core");
        KillByName("sing-box"); // legacy
    }

    public static void KillStackWorkers()
    {
        KillOnixBoostUnder("stack");
        KillByName("zapret_helper"); // legacy
    }

    /// <summary>winws / zapret2 only — never HY2 core (YouTube/Telegram tunnel).</summary>
    public static void KillDpiWorkers()
    {
        KillStackWorkers();
        KillByName("winws");
        KillByName("winws2");
    }

    public static void KillAllWorkers()
    {
        KillCoreWorkers();
        KillDpiWorkers();
    }

    public static bool CoreWorkerRunning()
    {
        return OnixBoostUnderRunning("core");
    }

    private static void KillOnixBoostUnder(string folderName)
    {
        var main = "";
        try { main = Environment.ProcessPath ?? ""; } catch { }

        foreach (var p in Process.GetProcessesByName("OnixBoost"))
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(path)) continue;
                if (!string.IsNullOrEmpty(main) &&
                    string.Equals(path, main, StringComparison.OrdinalIgnoreCase))
                    continue;

                var needle1 = $"{Path.DirectorySeparatorChar}{folderName}{Path.DirectorySeparatorChar}";
                var needle2 = $"{Path.AltDirectorySeparatorChar}{folderName}{Path.AltDirectorySeparatorChar}";
                if (path.Contains(needle1, StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(needle2, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(true);
                    p.WaitForExit(800);
                }
            }
            catch { }
        }
    }

    private static bool OnixBoostUnderRunning(string folderName)
    {
        var main = "";
        try { main = Environment.ProcessPath ?? ""; } catch { }
        foreach (var p in Process.GetProcessesByName("OnixBoost"))
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(path)) continue;
                if (!string.IsNullOrEmpty(main) &&
                    string.Equals(path, main, StringComparison.OrdinalIgnoreCase))
                    continue;
                var needle1 = $"{Path.DirectorySeparatorChar}{folderName}{Path.DirectorySeparatorChar}";
                var needle2 = $"{Path.AltDirectorySeparatorChar}{folderName}{Path.AltDirectorySeparatorChar}";
                if (path.Contains(needle1, StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(needle2, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static void KillByName(string name)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(true); p.WaitForExit(500); } catch { }
            }
        }
        catch { }
    }
}
