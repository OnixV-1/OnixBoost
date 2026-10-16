using System;
using System.IO;

namespace OnixBoost.Services;

public static class AppPaths
{
    /// <summary>Folder that contains OnixBoost.exe (not the single-file extract cache).</summary>
    public static string Root { get; } = ResolveRoot();

    public static string DpiRoot => Path.Combine(Root, "dpi");
    public static string BinDir => Path.Combine(DpiRoot, "bin");
    public static string ArgsDir => Path.Combine(DpiRoot, "args");
    public static string ListsDir => Path.Combine(DpiRoot, "lists");
    public static string WinwsExe => Path.Combine(BinDir, "winws.exe");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "onixboost.log");
    public static string AutoHostListFile => Path.Combine(DpiRoot, "list-auto.txt");
    public static string BlockcheckDir => Path.Combine(DpiRoot, "blockcheck");

    /// <summary>Hide dpi/ from casual Explorer browsing (Hidden attribute).</summary>
    public static void HideDpiFolder()
    {
        try
        {
            if (!Directory.Exists(DpiRoot)) return;
            var di = new DirectoryInfo(DpiRoot);
            if ((di.Attributes & FileAttributes.Hidden) == 0)
                di.Attributes |= FileAttributes.Hidden;
        }
        catch { }
    }

    private static string ResolveRoot()
    {
        try
        {
            var proc = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(proc))
            {
                var dir = Path.GetDirectoryName(proc);
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "dpi", "bin", "winws.exe")))
                    return dir;
            }
        }
        catch { }

        var bas = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(Path.Combine(bas, "dpi", "bin", "winws.exe")))
            return bas;

        // walk up from base dir (single-file extract quirks)
        try
        {
            var cur = new DirectoryInfo(bas);
            for (var i = 0; i < 5 && cur != null; i++, cur = cur.Parent)
            {
                if (File.Exists(Path.Combine(cur.FullName, "dpi", "bin", "winws.exe")))
                    return cur.FullName;
            }
        }
        catch { }

        return bas;
    }
}
