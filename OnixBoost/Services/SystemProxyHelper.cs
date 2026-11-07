using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace OnixBoost.Services;

/// <summary>
/// System proxy for YT/Twitch without TUN:
/// 1) HTTP-served PAC on 127.0.0.1 (Chrome ignores file:// PAC)
/// 2) WinINET AutoConfigURL (Windows) or GNOME/KDE proxy (Linux)
/// Steam/CS2 UDP ignore system HTTP proxy — routing table untouched.
/// </summary>
public static class SystemProxyHelper
{
#if WINDOWS
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
#endif

    public static int MixedPort => 17890;
    public static int PacHttpPort => 17891;

    private static HttpListener? _pacListener;
    private static CancellationTokenSource? _pacCts;
    private static string _pacBody = "";
    private static bool _weEnabledProxy;
#if WINDOWS
    private static object? _prevProxyEnable;
    private static object? _prevProxyServer;
    private static object? _prevAutoConfig;
    private static object? _prevAutoDetect;
#endif
    private static string? _prevGnomeMode;
    private static string? _prevGnomeAutoconfig;

    public static void EnableMediaPac(bool youtube, bool twitch, bool discord = false)
    {
        _pacBody = BuildPac(youtube, twitch, discord);
        StartPacHttpServer();

        var pacUrl = $"http://127.0.0.1:{PacHttpPort}/onix.pac";
        // PAC-only: no ProxyEnable=1 — EnableWindowsPac sets ProxyEnable 0 + AutoConfigURL

        if (PlatformOs.IsLinux)
            EnableLinuxPac(pacUrl);
        else
            EnableWindowsPac(pacUrl);

        _weEnabledProxy = true;
        Logger.Info($"system PAC on (yt={youtube} tw={twitch} discord={discord}) pac={pacUrl}");
    }

    public static void DisableMediaPac()
    {
        StopPacHttpServer();

        try
        {
            if (_weEnabledProxy)
            {
                if (PlatformOs.IsLinux)
                    DisableLinuxPac();
                else
                    DisableWindowsPac();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DisableMediaPac: " + ex.Message);
        }

        _weEnabledProxy = false;
        Logger.Info("system proxy+PAC off");
    }

    /// <summary>
    /// Discord Soft (winws2) needs a clean path — kill Clash/v2rayN system proxy
    /// (127.0.0.1:10809), env HTTP(S)_PROXY, and process DefaultProxy.
    /// </summary>
    public static void ClearForeignProxyForSoft()
    {
        try
        {
            HttpClient.DefaultProxy = new WebProxy(); // no-proxy (empty)
        }
        catch { }

        foreach (var key in new[]
                 {
                     "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy",
                     "NO_PROXY", "no_proxy"
                 })
        {
            try { Environment.SetEnvironmentVariable(key, null); } catch { }
        }

        if (PlatformOs.IsLinux) return;
#if WINDOWS
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key == null)
            {
                Logger.Info("Soft: proxy registry missing");
                return;
            }

            var enable = 0;
            try { enable = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0); } catch { }
            var server = (key.GetValue("ProxyServer") as string) ?? "";
            var pac = (key.GetValue("AutoConfigURL") as string) ?? "";
            var ourPac = pac.Contains($":{PacHttpPort}/", StringComparison.Ordinal);

            // Keep our Media PAC if we own it
            if (_weEnabledProxy && ourPac)
            {
                // Still force ProxyEnable=0 (PAC-only mode)
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                try { key.DeleteValue("ProxyServer", false); } catch { }
                NotifyProxyChanged();
                Logger.Info("Soft: kept Onix PAC, cleared ProxyServer");
                return;
            }

            var changed = false;
            if (enable != 0)
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                changed = true;
            }
            if (!string.IsNullOrEmpty(server))
            {
                try { key.DeleteValue("ProxyServer", false); } catch { }
                changed = true;
            }
            if (!string.IsNullOrEmpty(pac) && !ourPac)
            {
                try { key.DeleteValue("AutoConfigURL", false); } catch { }
                changed = true;
            }
            try { key.DeleteValue("ProxyOverride", false); } catch { }

            if (changed) NotifyProxyChanged();
            Logger.Info(changed
                ? $"Soft: cleared foreign proxy enable={enable} server=«{server}» pac=«{pac}»"
                : "Soft: no WinINET proxy to clear");
        }
        catch (Exception ex)
        {
            Logger.Error("ClearForeignProxyForSoft: " + ex.Message);
        }
#endif
    }

#if WINDOWS
    private static void EnableWindowsPac(string pacUrl)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
        if (key == null) throw new InvalidOperationException("Internet Settings registry missing");

        _prevProxyEnable = key.GetValue("ProxyEnable");
        _prevProxyServer = key.GetValue("ProxyServer");
        _prevAutoConfig = key.GetValue("AutoConfigURL");
        _prevAutoDetect = key.GetValue("AutoDetect");

        key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
        key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        try { key.DeleteValue("ProxyServer", false); } catch { }
        try { key.DeleteValue("ProxyOverride", false); } catch { }
        NotifyProxyChanged();
    }

    private static void DisableWindowsPac()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
        if (key == null) return;
        Restore(key, "ProxyEnable", _prevProxyEnable, 0);
        Restore(key, "ProxyServer", _prevProxyServer, deleteIfNull: true);
        Restore(key, "AutoConfigURL", _prevAutoConfig, deleteIfNull: true);
        Restore(key, "AutoDetect", _prevAutoDetect, 0);
        try { key.DeleteValue("ProxyOverride", false); } catch { }
        _prevProxyEnable = _prevProxyServer = _prevAutoConfig = _prevAutoDetect = null;
        NotifyProxyChanged();
    }

    private static void Restore(RegistryKey key, string name, object? prev, object? defaultValue = null, bool deleteIfNull = false)
    {
        if (prev != null)
            key.SetValue(name, prev);
        else if (deleteIfNull)
            try { key.DeleteValue(name, false); } catch { }
        else if (defaultValue != null)
            key.SetValue(name, defaultValue);
    }

    private static void NotifyProxyChanged()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
#endif

    private static void EnableLinuxPac(string pacUrl)
    {
        try
        {
            _prevGnomeMode = RunCapture("gsettings", "get org.gnome.system.proxy mode")?.Trim();
            _prevGnomeAutoconfig = RunCapture("gsettings", "get org.gnome.system.proxy autoconfig-url")?.Trim();
            Run("gsettings", "set org.gnome.system.proxy mode 'auto'");
            Run("gsettings", $"set org.gnome.system.proxy autoconfig-url '{pacUrl}'");
        }
        catch (Exception ex)
        {
            Logger.Error("EnableLinuxPac gnome: " + ex.Message);
        }

        try
        {
            Run("kwriteconfig5",
                "--file kioslaverc --group Proxy Settings --key ProxyType 2");
            Run("kwriteconfig5",
                $"--file kioslaverc --group Proxy Settings --key Proxy Config Script {pacUrl}");
        }
        catch { }
    }

    private static void DisableLinuxPac()
    {
        try
        {
            if (!string.IsNullOrEmpty(_prevGnomeMode))
                Run("gsettings", "set org.gnome.system.proxy mode " + _prevGnomeMode);
            else
                Run("gsettings", "set org.gnome.system.proxy mode 'none'");
            if (!string.IsNullOrEmpty(_prevGnomeAutoconfig))
                Run("gsettings", "set org.gnome.system.proxy autoconfig-url " + _prevGnomeAutoconfig);
            else
                Run("gsettings", "set org.gnome.system.proxy autoconfig-url ''");
        }
        catch { }

        try
        {
            Run("kwriteconfig5",
                "--file kioslaverc --group Proxy Settings --key ProxyType 0");
        }
        catch { }

        _prevGnomeMode = _prevGnomeAutoconfig = null;
    }

    private static void StartPacHttpServer()
    {
        if (_pacListener != null) return;
        _pacCts = new CancellationTokenSource();
        _pacListener = new HttpListener();
        _pacListener.Prefixes.Add($"http://127.0.0.1:{PacHttpPort}/");
        _pacListener.Start();
        var ct = _pacCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _pacListener.GetContextAsync(); }
                catch { break; }
                try
                {
                    var buf = Encoding.UTF8.GetBytes(_pacBody);
                    ctx.Response.ContentType = "application/x-ns-proxy-autoconfig";
                    ctx.Response.ContentLength64 = buf.Length;
                    await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length, ct);
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        }, ct);
    }

    private static void StopPacHttpServer()
    {
        try { _pacCts?.Cancel(); } catch { }
        try { _pacListener?.Stop(); } catch { }
        try { _pacListener?.Close(); } catch { }
        _pacListener = null;
        _pacCts = null;
    }

    private static string BuildPac(bool youtube, bool twitch, bool discord)
    {
        var hosts = new List<string>();
        if (youtube)
        {
            hosts.AddRange(new[]
            {
                "youtube.com", "youtu.be", "ytimg.com", "googlevideo.com",
                "ggpht.com", "googleapis.com", "gvt1.com", "youtube-nocookie.com"
            });
        }
        if (twitch)
        {
            hosts.AddRange(new[]
            {
                "twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "ttvstatic.com"
            });
        }
        if (discord)
        {
            hosts.AddRange(new[]
            {
                "discord.com", "discord.gg", "discordapp.com", "discordapp.net",
                "discord.media", "discordcdn.com"
            });
        }

        var sb = new StringBuilder();
        sb.AppendLine("function FindProxyForURL(url, host) {");
        sb.AppendLine("  host = host.toLowerCase();");
        foreach (var h in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"  if (dnsDomainIs(host, \"{h}\") || host == \"{h}\" || shExpMatch(host, \"*.{h}\"))");
            sb.AppendLine($"    return \"PROXY 127.0.0.1:{MixedPort}\";");
        }
        sb.AppendLine("  return \"DIRECT\";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void Run(string file, string args)
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
            p?.WaitForExit(3000);
        }
        catch { }
    }

    private static string? RunCapture(string file, string args)
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
            if (p == null) return null;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return o;
        }
        catch { return null; }
    }
}
