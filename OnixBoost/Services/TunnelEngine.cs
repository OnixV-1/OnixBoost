using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// One HY2 worker process. Media and Telegram use separate instances so cards never
/// stop each other (no merged YT+TG config, no KillCoreWorkers blast).
/// </summary>
public enum TunnelKind
{
    /// <summary>YouTube / Twitch mixed :17890 + PAC (no TUN).</summary>
    Media,
    /// <summary>Telegram OnixBoostTG route_address TUN.</summary>
    Telegram
}

public sealed class TunnelEngine
{
    private readonly TunnelKind _kind;
    private Process? _proc;
    private string? _activeProfile;
    private string? _liveConfigPath;
    private bool _pacEnabled;

    public TunnelEngine(TunnelKind kind) => _kind = kind;

    public TunnelKind Kind => _kind;

    public bool IsRunning => _proc is { HasExited: false };

    public bool IsTelegramOnly =>
        _kind == TunnelKind.Telegram && IsRunning;

    public bool IsTwitchProfile =>
        _kind == TunnelKind.Media && IsRunning &&
        (_activeProfile?.Contains("twitch", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool IsYoutubeProfile =>
        _kind == TunnelKind.Media && IsRunning &&
        (_activeProfile?.Contains("youtube", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string CoreDir => Path.Combine(AppPaths.DpiRoot, "core");
    private static string CoreExe => Path.Combine(CoreDir, "OnixBoost.exe");

    private string RuntimeLeaf => _kind == TunnelKind.Telegram ? "tg1" : "media1";

    public Task StartAsync() =>
        StartMediaAsync(youtube: true, twitch: false);

    public Task StartTelegramAsync() =>
        StartTelegramCoreAsync();

    public Task StartTwitchAsync() =>
        StartMediaAsync(youtube: false, twitch: true);

    public Task StartYoutubeAsync() =>
        StartMediaAsync(youtube: true, twitch: false);

    /// <summary>Media card only — never touches Telegram process.</summary>
    public Task StartMediaAsync(bool youtube, bool twitch)
    {
        if (_kind != TunnelKind.Media)
            throw new InvalidOperationException("StartMediaAsync requires TunnelKind.Media");

        if (!youtube && !twitch)
            return StopAsync();

        var parts = new System.Collections.Generic.List<string>();
        if (youtube) parts.Add("youtube");
        if (twitch) parts.Add("twitch");
        return StartProfileAsync(string.Join("-", parts), youtube, twitch, telegram: false);
    }

    /// <summary>
    /// Legacy combined entry. Telegram flag is ignored on Media engines —
    /// use a separate TunnelKind.Telegram instance.
    /// </summary>
    public Task StartServiceTunnelAsync(
        bool youtube, bool telegram, bool twitch, bool excludeDiscordCidrs = false)
    {
        _ = excludeDiscordCidrs;
        if (_kind == TunnelKind.Telegram)
        {
            if (!telegram && !youtube && !twitch)
                return StopAsync();
            return StartTelegramCoreAsync();
        }

        // Media engine: never merge Telegram into this process.
        if (!youtube && !twitch)
            return StopAsync();
        return StartMediaAsync(youtube, twitch);
    }

    private Task StartTelegramCoreAsync()
    {
        if (_kind != TunnelKind.Telegram)
            throw new InvalidOperationException("StartTelegramAsync requires TunnelKind.Telegram");
        return StartProfileAsync("tg", youtube: false, twitch: false, telegram: true);
    }

    private async Task StartProfileAsync(
        string profile,
        bool youtube,
        bool twitch,
        bool telegram)
    {
        await StopAsync();
        await Task.Delay(600);

        if (!File.Exists(CoreExe))
            throw new FileNotFoundException("OnixBoost core missing (dpi/core/OnixBoost.exe)", CoreExe);

        if (TunnelConfigStore.NeedsWintun(telegram))
        {
            var wintun = Path.Combine(CoreDir, "wintun.dll");
            if (!File.Exists(wintun))
                throw new FileNotFoundException("wintun.dll missing in dpi/core/", wintun);
        }

        var live = telegram
            ? TunnelConfigStore.MaterializeTelegram(RuntimeLeaf)
            : TunnelConfigStore.MaterializeService(
                RuntimeLeaf, youtube, twitch, telegram: false, excludeDiscord: false);
        _liveConfigPath = live;
        if (!File.Exists(live))
            throw new FileNotFoundException("Failed to materialize tunnel config", live);

        var rel = Path.GetRelativePath(CoreDir, live).Replace('/', '\\');
        var errLog = Path.Combine(CoreDir, ".rt",
            _kind == TunnelKind.Telegram ? "core-tg-err.log" : "core-media-err.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(errLog)!); } catch { }
        try { File.Delete(errLog); } catch { }

        try
        {
            var check = Process.Start(new ProcessStartInfo
            {
                FileName = CoreExe,
                WorkingDirectory = CoreDir,
                Arguments = "check -c \"" + rel + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (check != null)
            {
                var so = await check.StandardOutput.ReadToEndAsync();
                var se = await check.StandardError.ReadToEndAsync();
                await check.WaitForExitAsync();
                if (check.ExitCode != 0)
                {
                    var tip = (se + "\n" + so).Trim();
                    try { await File.WriteAllTextAsync(errLog, tip); } catch { }
                    throw new InvalidOperationException(
                        "Tunnel config error.\n" + StripAnsi(tip)[..Math.Min(400, StripAnsi(tip).Length)]);
                }
            }
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            Logger.Error("core check: " + ex.Message);
        }

        var psi = new ProcessStartInfo
        {
            FileName = CoreExe,
            WorkingDirectory = CoreDir,
            Arguments = "run -c \"" + rel + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start OnixBoost core");
        _proc = proc;
        _activeProfile = profile;

        var logBuf = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (logBuf) logBuf.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (logBuf) logBuf.AppendLine(e.Data); };
        try { proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); } catch { }

        await Task.Delay(telegram ? 1800 : 900);

        if (proc.HasExited || !IsRunning)
        {
            await Task.Delay(200);
            string tip;
            lock (logBuf) tip = logBuf.ToString();
            if (string.IsNullOrWhiteSpace(tip))
                tip = TryReadLog();
            if (string.IsNullOrWhiteSpace(tip))
                tip = "process exited code=" + (proc.HasExited ? proc.ExitCode.ToString() : "?") +
                      " — run OnixBoost as Admin; allow firewall for OnixBoost";
            try { await File.WriteAllTextAsync(errLog, tip); } catch { }
            if (ReferenceEquals(_proc, proc))
            {
                _proc = null;
                _activeProfile = null;
            }
            TunnelConfigStore.ScrubRuntimeLeaf(RuntimeLeaf);
            _liveConfigPath = null;
            var clean = StripAnsi(tip);
            throw new InvalidOperationException(
                "Tunnel failed to start.\n" + clean[..Math.Min(400, clean.Length)]);
        }

        if (youtube || twitch)
        {
            try
            {
                SystemProxyHelper.EnableMediaPac(youtube, twitch);
                _pacEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.Error("PAC enable: " + ex.Message);
                try { await StopAsync(); } catch { }
                throw new InvalidOperationException("Не удалось включить системный PAC: " + ex.Message);
            }
        }

        Logger.Info("NL HY2 up (" + _kind + "/" + profile + ") pid=" + proc.Id + " pac=" + _pacEnabled);
    }

    private static string StripAnsi(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && s[i] != 'm') i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private string TryReadLog()
    {
        foreach (var log in new[]
                 {
                     Path.Combine(CoreDir, _kind == TunnelKind.Telegram ? "core-telegram.log" : "core-media.log"),
                     Path.Combine(CoreDir, ".rt",
                         _kind == TunnelKind.Telegram ? "core-tg-err.log" : "core-media-err.log")
                 })
        {
            if (File.Exists(log))
            {
                try { return File.ReadAllText(log); } catch { }
            }
        }
        return "";
    }

    /// <summary>Stop only this engine's process. Never kills the sibling HY2 worker.</summary>
    public Task StopAsync()
    {
        if (_pacEnabled)
        {
            try { SystemProxyHelper.DisableMediaPac(); } catch { }
            _pacEnabled = false;
        }

        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(true);
                _proc.WaitForExit(3000);
            }
        }
        catch { }

        // PID-scoped only — never blast-kill other OnixBoost.exe under dpi/core.
        _proc = null;
        _activeProfile = null;
        _liveConfigPath = null;
        TunnelConfigStore.ScrubRuntimeLeaf(RuntimeLeaf);
        return Task.CompletedTask;
    }
}
