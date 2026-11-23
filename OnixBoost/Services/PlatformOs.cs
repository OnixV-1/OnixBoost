using System;
using System.Runtime.InteropServices;

namespace OnixBoost.Services;

/// <summary>OS gate used across Soft DPI / tunnel / proxy.</summary>
public static class PlatformOs
{
    public static bool IsWindows =>
#if WINDOWS
        true;
#else
        OperatingSystem.IsWindows();
#endif

    public static bool IsLinux =>
#if LINUX
        true;
#else
        OperatingSystem.IsLinux();
#endif

    public static string SoftLabel => IsWindows ? "winws" : "nfqws";

    public static string TunnelCoreName => IsWindows ? "OnixBoost.exe" : "sing-box";
}
