using System;
using System.IO;

namespace OnixBoost.Services;

public static class AppPaths
{
    /// <summary>Folder that contains OnixBoost (not the single-file extract cache).</summary>
    public static string Root { get; } = ResolveRoot();

    public static string DpiRoot => Path.Combine(Root, "dpi");
    public static string BinDir => Path.Combine(DpiRoot, "bin");
    public static string ArgsDir => Path.Combine(DpiRoot, "args");
    public static string ListsDir => Path.Combine(DpiRoot, "lists");
    public static string CoreDir => Path.Combine(DpiRoot, "core");

    /// <summary>Windows Soft binary.</summary>
    public static string WinwsExe => Path.Combine(BinDir, "winws.exe");

    /// <summary>Linux Soft binary (bol-van/zapret nfqws).</summary>
    public static string NfqwsExe => Path.Combine(BinDir, "nfqws");

    /// <summary>Platform Soft executable.</summary>
    public static string SoftExe => PlatformOs.IsLinux ? NfqwsExe : WinwsExe;

    /// <summary>HY2 / sing-box core.</summary>
    public static string CoreExe => Path.Combine(CoreDir,
        PlatformOs.IsLinux ? "sing-box" : "OnixBoost.exe");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "onixboost.log");
    public static string AutoHostListFile => Path.Combine(DpiRoot, "list-auto.txt");
    public static string BlockcheckDir => Path.Combine(DpiRoot, "blockcheck");

    /// <summary>Hide dpi/ from casual Explorer browsing (Hidden attribute). No-op on Linux.</summary>
    public static void HideDpiFolder()
    {
        if (!PlatformOs.IsWindows) return;
        try
        {
            if (!Directory.Exists(DpiRoot)) return;
            var di = new DirectoryInfo(DpiRoot);
            if ((di.Attributes & FileAttributes.Hidden) == 0)
                di.Attributes |= FileAttributes.Hidden;
        }
        catch { }
    }

    private static bool LooksLikeAppRoot(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        if (File.Exists(Path.Combine(dir, "dpi", "bin", "winws.exe"))) return true;
        if (File.Exists(Path.Combine(dir, "dpi", "bin", "nfqws"))) return true;
        if (Directory.Exists(Path.Combine(dir, "dpi", "args")) &&
            Directory.Exists(Path.Combine(dir, "dpi", "lists")))
            return true;
        return false;
    }

    private static string ResolveRoot()
    {
        try
        {
            var proc = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(proc))
            {
                var dir = Path.GetDirectoryName(proc);
                if (!string.IsNullOrEmpty(dir) && LooksLikeAppRoot(dir))
                    return dir;
            }
        }
        catch { }

        var bas = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (LooksLikeAppRoot(bas))
            return bas;

        try
        {
            var cur = new DirectoryInfo(bas);
            for (var i = 0; i < 5 && cur != null; i++, cur = cur.Parent)
            {
                if (LooksLikeAppRoot(cur.FullName))
                    return cur.FullName;
            }
        }
        catch { }

        return bas;
    }
}
