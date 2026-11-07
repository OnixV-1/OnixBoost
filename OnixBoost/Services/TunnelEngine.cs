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
    Telegram,
    /// <summary>Discord OnixBoostDC route_address TUN (Linux Soft fallback).</summary>
    Discord
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

    public bool IsDiscordTun =>
        _kind == TunnelKind.Discord && IsRunning;

    public bool IsTwitchProfile =>
        _kind == TunnelKind.Media && IsRunning &&
        (_activeProfile?.Contains("twitch", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool IsYoutubeProfile =>
        _kind == TunnelKind.Media && IsRunning &&
        (_activeProfile?.Contains("youtube", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string CoreDir => AppPaths.CoreDir;
    private static string CoreExe => AppPaths.CoreExe;

    private string RuntimeLeaf => _kind switch
    {
        TunnelKind.Telegram => "tg1",
        TunnelKind.Discord => "dc1",
        _ => "media1"
    };

    private bool NeedsTunCaps =>
        _kind is TunnelKind.Telegram or TunnelKind.Discord;

    public Task StartAsync() =>
        StartMediaAsync(youtube: true, twitch: false);

    public Task StartTelegramAsync() =>
        StartTelegramCoreAsync();

    public Task StartDiscordTunAsync() =>
        StartDiscordCoreAsync();

    public Task StartTwitchAsync() =>
        StartMediaAsync(youtube: false, twitch: true);

    public Task StartYoutubeAsync() =>
        StartMediaAsync(youtube: true, twitch: false);

    /// <summary>Media card only — never touches Telegram process.</summary>
    public Task StartMediaAsync(bool youtube, bool twitch, bool discord = false)
    {
        if (_kind != TunnelKind.Media)
            throw new InvalidOperationException("StartMediaAsync requires TunnelKind.Media");

        if (!youtube && !twitch && !discord)
            return StopAsync();

        var parts = new System.Collections.Generic.List<string>();
        if (youtube) parts.Add("youtube");
        if (twitch) parts.Add("twitch");
        if (discord) parts.Add("discord");
        return StartProfileAsync(string.Join("-", parts), youtube, twitch, telegram: false, discord);
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
        return StartMediaAsync(youtube, twitch, discord: false);
    }

    private Task StartTelegramCoreAsync()
    {
        if (_kind != TunnelKind.Telegram)
            throw new InvalidOperationException("StartTelegramAsync requires TunnelKind.Telegram");
        return StartProfileAsync("tg", youtube: false, twitch: false, telegram: true, discord: false);
    }

    private Task StartDiscordCoreAsync()
    {
        if (_kind != TunnelKind.Discord)
            throw new InvalidOperationException("StartDiscordTunAsync requires TunnelKind.Discord");
        return StartProfileAsync("discord", youtube: false, twitch: false, telegram: false, discord: true);
    }

    private async Task StartProfileAsync(
        string profile,
        bool youtube,
        bool twitch,
        bool telegram,
        bool discord = false)
    {
        await StopAsync();
        await Task.Delay(600);

        if (!File.Exists(CoreExe))
            throw new FileNotFoundException(
                PlatformOs.IsLinux
                    ? "sing-box missing (dpi/core/sing-box) — reinstall 2.10.2+ / run fix-caps.sh"
                    : "OnixBoost core missing (dpi/core/OnixBoost.exe)",
                CoreExe);

        if (PlatformOs.IsLinux)
        {
            // Fail fast with a clear tip if an old sing-box (1.11) was left in place.
            try
            {
                var verPsi = new ProcessStartInfo
                {
                    FileName = CoreExe,
                    Arguments = "version",
                    WorkingDirectory = CoreDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var vp = Process.Start(verPsi);
                if (vp != null)
                {
                    var vo = await vp.StandardOutput.ReadToEndAsync();
                    await Task.Run(() => vp.WaitForExit(3000));
                    if (vo.Contains("version 1.11.", StringComparison.Ordinal) ||
                        vo.Contains("version 1.10.", StringComparison.Ordinal) ||
                        vo.Contains("version 1.9.", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Устаревший sing-box (" + vo.Split('\n')[0].Trim() +
                            "). Нужен 1.12+. Обновите OnixBoost-linux-x64.zip (2.10.2+) и снова ./install.sh");
                    }
                }
            }
            catch (InvalidOperationException) { throw; }
            catch { /* version probe optional */ }
        }

        if (PlatformOs.IsWindows && TunnelConfigStore.NeedsWintun(telegram))
        {
            var wintun = Path.Combine(CoreDir, "wintun.dll");
            if (!File.Exists(wintun))
                throw new FileNotFoundException("wintun.dll missing in dpi/core/", wintun);
        }

        var live = _kind switch
        {
            TunnelKind.Telegram => TunnelConfigStore.MaterializeTelegram(RuntimeLeaf),
            TunnelKind.Discord => TunnelConfigStore.MaterializeDiscord(RuntimeLeaf),
            _ => TunnelConfigStore.MaterializeService(
                RuntimeLeaf, youtube, twitch, telegram: false, excludeDiscord: false, discord: false)
        };
        _liveConfigPath = live;
        if (!File.Exists(live))
            throw new FileNotFoundException("Failed to materialize tunnel config", live);

        var rel = Path.GetRelativePath(CoreDir, live);
        if (PlatformOs.IsWindows)
            rel = rel.Replace('/', '\\');
        else
            rel = rel.Replace('\\', '/');
        var errLog = Path.Combine(CoreDir, ".rt", _kind switch
        {
            TunnelKind.Telegram => "core-tg-err.log",
            TunnelKind.Discord => "core-dc-err.log",
            _ => "core-media-err.log"
        });
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

        if (PlatformOs.IsLinux)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    ArgumentList = { "+x", CoreExe },
                    UseShellExecute = false
                })?.WaitForExit(2000);
            }
            catch { }

            // YouTube/Twitch = mixed proxy on 127.0.0.1 — NO root/sudo.
            // Discord/Telegram TUN need CAP_NET_ADMIN (one-time fix-caps).
            if (NeedsTunCaps && !LinuxElevate.IsRoot && !LinuxElevate.TunnelCapsReady)
            {
                var (ok, msg) = await LinuxElevate.EnsureSoftPrivilegesAsync();
                if (!ok || !LinuxElevate.TunnelCapsReady)
                    throw new InvalidOperationException(
                        (_kind == TunnelKind.Discord ? "Discord" : "Telegram") +
                        " TUN: нужны права один раз.\n" + msg);
            }

            // Stale iface from a crashed previous run blocks "tun create"
            if (_kind is TunnelKind.Discord or TunnelKind.Telegram)
            {
                var iface = _kind == TunnelKind.Discord ? "OnixBoostDC" : "OnixBoostTG";
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "ip",
                        ArgumentList = { "link", "delete", iface },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    })?.WaitForExit(1500);
                }
                catch { /* iface absent = OK */ }
            }
        }
        else if (PlatformOs.IsWindows && (_kind is TunnelKind.Telegram or TunnelKind.Discord))
        {
            // Same class of failure as Linux: leftover Wintun "OnixBoostTG" →
            // FATAL start inbound/tun[…] configure tun interface / create TUN.
            var iface = _kind == TunnelKind.Discord ? "OnixBoostDC" : "OnixBoostTG";
            TryRemoveWindowsTunAdapter(iface);
        }

        var runArgs = "run -c \"" + rel + "\"";
        // Media never uses sudo -n (that was breaking YT/Twitch for everyone without fix-caps).
        var psi = PlatformOs.IsLinux
            ? LinuxElevate.TunnelStartInfo(CoreExe, CoreDir, runArgs, needsTun: NeedsTunCaps)
            : new ProcessStartInfo
            {
                FileName = CoreExe,
                WorkingDirectory = CoreDir,
                Arguments = runArgs,
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

        // Discord TUN + fib install needs a bit longer on Fedora than Telegram.
        // Windows Wintun create is also slower than a plain mixed-proxy start.
        await Task.Delay(NeedsTunCaps
            ? (_kind == TunnelKind.Discord ? 2800 : 1800)
            : (PlatformOs.IsWindows && (_kind is TunnelKind.Telegram or TunnelKind.Discord) ? 1600 : 900));

        if (proc.HasExited || !IsRunning)
        {
            await Task.Delay(300);
            string tip;
            lock (logBuf) tip = logBuf.ToString();
            if (string.IsNullOrWhiteSpace(tip))
                tip = TryReadLog();
            if (string.IsNullOrWhiteSpace(tip))
                    tip = "process exited code=" + (proc.HasExited ? proc.ExitCode.ToString() : "?") +
                      (PlatformOs.IsLinux
                          ? " — need CAP_NET_ADMIN (fix-caps.sh); allow firewall for sing-box"
                          : " — run OnixBoost as Admin; allow firewall for OnixBoost");
            try { await File.WriteAllTextAsync(errLog, tip); } catch { }
            if (ReferenceEquals(_proc, proc))
            {
                _proc = null;
                _activeProfile = null;
            }
            TunnelConfigStore.ScrubRuntimeLeaf(RuntimeLeaf);
            _liveConfigPath = null;
            var clean = StripAnsi(tip);
            Logger.Error($"tunnel[{_kind}] start fail: " + clean[..Math.Min(500, clean.Length)]);
            throw new InvalidOperationException(
                "Tunnel failed to start.\n" + clean[..Math.Min(400, clean.Length)]);
        }

        // PAC only for media mixed proxy — Discord/Telegram use TUN (Electron ignores PAC).
        if (_kind == TunnelKind.Media && (youtube || twitch))
        {
            try
            {
                SystemProxyHelper.EnableMediaPac(youtube, twitch, discord: false);
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
                     Path.Combine(CoreDir, _kind switch
                     {
                         TunnelKind.Telegram => "core-telegram.log",
                         TunnelKind.Discord => "core-discord.log",
                         _ => "core-media.log"
                     }),
                     Path.Combine(CoreDir, ".rt", _kind switch
                     {
                         TunnelKind.Telegram => "core-tg-err.log",
                         TunnelKind.Discord => "core-dc-err.log",
                         _ => "core-media-err.log"
                     })
                 })
        {
            if (File.Exists(log))
            {
                try { return File.ReadAllText(log); } catch { }
            }
        }
        return "";
    }

    /// <summary>
    /// Drop a leftover Wintun NIC. After a crash sing-box leaves "OnixBoostTG" and the
    /// next start dies with FATAL start inbound/tun[…].
    /// </summary>
    private static void TryRemoveWindowsTunAdapter(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var ps = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                    "Get-NetAdapter -Name '" + name.Replace("'", "") + "' -ErrorAction SilentlyContinue " +
                    "| Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(ps);
            p?.WaitForExit(4000);
        }
        catch { /* adapter absent = OK */ }
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
        var iface = _kind switch
        {
            TunnelKind.Telegram => "OnixBoostTG",
            TunnelKind.Discord => "OnixBoostDC",
            _ => null
        };
        if (PlatformOs.IsWindows && iface != null)
            TryRemoveWindowsTunAdapter(iface);

        _proc = null;
        _activeProfile = null;
        _liveConfigPath = null;
        TunnelConfigStore.ScrubRuntimeLeaf(RuntimeLeaf);
        return Task.CompletedTask;
    }
}
