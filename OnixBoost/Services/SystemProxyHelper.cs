using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OnixBoost.Services;

/// <summary>
/// System proxy for YT/Twitch without TUN:
/// 1) HTTP-served PAC on 127.0.0.1 (Chrome ignores file:// PAC)
/// 2) WinINET ProxyEnable → 127.0.0.1:17890 as fallback
/// Steam/CS2 UDP ignore WinINET — routing table untouched.
/// </summary>
public static class SystemProxyHelper
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static int MixedPort => 17890;
    public static int PacHttpPort => 17891;

    private static HttpListener? _pacListener;
    private static CancellationTokenSource? _pacCts;
    private static string _pacBody = "";
    private static bool _weEnabledProxy;
    private static object? _prevProxyEnable;
    private static object? _prevProxyServer;
    private static object? _prevAutoConfig;
    private static object? _prevAutoDetect;

    public static void EnableMediaPac(bool youtube, bool twitch)
    {
        _pacBody = BuildPac(youtube, twitch);
        StartPacHttpServer();

        var pacUrl = $"http://127.0.0.1:{PacHttpPort}/onix.pac";
        using (var key = Registry.CurrentUser.OpenSubKey(
                   @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
        {
            if (key == null) throw new InvalidOperationException("Internet Settings registry missing");

            _prevProxyEnable = key.GetValue("ProxyEnable");
            _prevProxyServer = key.GetValue("ProxyServer");
            _prevAutoConfig = key.GetValue("AutoConfigURL");
            _prevAutoDetect = key.GetValue("AutoDetect");

            // PAC only — no ProxyEnable. Global proxy would shove Telegram Desktop into
            // mixed :17890 and break the separate TG TUN process.
            key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
            key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
            // Ensure we are not leaving a previous ProxyEnable=1 from older builds.
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            try { key.DeleteValue("ProxyServer", false); } catch { }
            try { key.DeleteValue("ProxyOverride", false); } catch { }
            _weEnabledProxy = true;
        }

        NotifyProxyChanged();
        Logger.Info($"system PAC on (yt={youtube} tw={twitch}) pac={pacUrl} (no ProxyEnable)");
    }

    public static void DisableMediaPac()
    {
        StopPacHttpServer();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key != null && _weEnabledProxy)
            {
                Restore(key, "ProxyEnable", _prevProxyEnable, 0);
                Restore(key, "ProxyServer", _prevProxyServer, deleteIfNull: true);
                Restore(key, "AutoConfigURL", _prevAutoConfig, deleteIfNull: true);
                Restore(key, "AutoDetect", _prevAutoDetect, 0);
                try { key.DeleteValue("ProxyOverride", false); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DisableMediaPac registry: " + ex.Message);
        }

        _weEnabledProxy = false;
        _prevProxyEnable = _prevProxyServer = _prevAutoConfig = _prevAutoDetect = null;
        NotifyProxyChanged();
        Logger.Info("system proxy+PAC off");
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

    private static void StartPacHttpServer()
    {
        StopPacHttpServer();
        _pacCts = new CancellationTokenSource();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{PacHttpPort}/");
        listener.Start();
        _pacListener = listener;
        var ct = _pacCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ctx = await listener.GetContextAsync().WaitAsync(ct);
                    var bytes = Encoding.UTF8.GetBytes(_pacBody);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/x-ns-proxy-autoconfig";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                    ctx.Response.Close();
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex)
                {
                    Logger.Error("PAC http: " + ex.Message);
                }
            }
        }, ct);
        Logger.Info($"PAC http server :{PacHttpPort}");
    }

    private static void StopPacHttpServer()
    {
        try { _pacCts?.Cancel(); } catch { }
        try { _pacListener?.Stop(); } catch { }
        try { _pacListener?.Close(); } catch { }
        _pacListener = null;
        try { _pacCts?.Dispose(); } catch { }
        _pacCts = null;
    }

    private static void NotifyProxyChanged()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch { }
    }

    private static string BuildPac(bool youtube, bool twitch)
    {
        var hosts = new StringBuilder();
        void Add(params string[] domains)
        {
            foreach (var d in domains)
                hosts.Append('"').Append(d).Append("\",");
        }

        if (youtube)
        {
            Add(
                "youtube.com", "youtu.be", "yt.be", "youtubekids.com", "youtube-nocookie.com",
                "googlevideo.com", "ytimg.com", "ggpht.com", "gvt1.com",
                "googleusercontent.com", "gstatic.com", "googleapis.com",
                "youtube.googleapis.com", "youtubei.googleapis.com",
                "youtubeembeddedplayer.googleapis.com",
                "jnn-pa.googleapis.com", "wide-youtube.l.google.com",
                "youtube-ui.l.google.com", "ytimg.l.google.com",
                "yt-video-upload.l.google.com");
        }

        if (twitch)
        {
            Add(
                "twitch.tv", "twitchcdn.net", "ext-twitch.tv", "gql.twitch.tv",
                "jtvnw.net", "ttvnw.net", "ttcdn.net", "live-video.net",
                "assets.twitch.tv");
        }

        var list = hosts.ToString().TrimEnd(',');
        var port = MixedPort;
        // PROXY first — HTTP CONNECT is what Chrome uses with system proxy; SOCKS as fallback.
        return $$"""
function FindProxyForURL(url, host) {
  if (!host) return "DIRECT";
  host = host.toLowerCase();
  if (host == "localhost" || host == "127.0.0.1") return "DIRECT";
  var d = [{{list}}];
  for (var i = 0; i < d.length; i++) {
    if (host === d[i] || dnsDomainIs(host, "." + d[i]))
      return "PROXY 127.0.0.1:{{port}}; SOCKS5 127.0.0.1:{{port}}; DIRECT";
  }
  return "DIRECT";
}
""";
    }
}
