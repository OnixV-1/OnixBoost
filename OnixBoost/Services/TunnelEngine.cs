using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// NL Hysteria2 via TUN (dpi/core/OnixBoost.exe — formerly sing-box).
/// Profiles embedded as JSON resources; materialized under dpi/core/.rt
/// </summary>
public sealed class TunnelEngine
{
    private Process? _proc;
    private string? _activeProfile; // "full" | "telegram" | "games" | "youtube" | "youtube-tg" | "games-yt"
    private string? _liveConfigPath;

    public bool IsRunning =>
        (_proc is { HasExited: false }) || ProcessNames.CoreWorkerRunning();

    public bool IsTelegramOnly =>
        IsRunning && string.Equals(_activeProfile, "telegram", StringComparison.OrdinalIgnoreCase);

    public bool IsGamesProfile =>
        IsRunning && (_activeProfile?.StartsWith("games", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool IsYoutubeProfile =>
        IsRunning && (_activeProfile?.Contains("youtube", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string CoreDir => Path.Combine(AppPaths.DpiRoot, "core");
    private static string CoreExe => Path.Combine(CoreDir, "OnixBoost.exe");

    public Task StartAsync() => StartProfileAsync("full", "client.json", "f1");

    public Task StartTelegramAsync() => StartProfileAsync("telegram", "client-telegram.json", "t1");

    public Task StartGamesAsync() => StartProfileAsync("games", "client-games.json", "g1");

    public Task StartYoutubeAsync() =>
        StartProfileAsync("youtube", "client-youtube.json", "y1");

    /// <summary>
    /// One HY2 process for the active tunnel cards (YouTube / Telegram / Games).
    /// Games profile already includes Telegram; YouTube domains are injected when needed.
    /// </summary>
    public Task StartServiceTunnelAsync(bool youtube, bool telegram, bool games)
    {
        if (games)
            return StartProfileAsync(
                youtube ? "games-yt" : "games",
                "client-games.json",
                youtube ? "gy1" : "g1",
                injectYoutube: youtube);

        if (youtube && telegram)
            return StartProfileAsync(
                "youtube-tg", "client-youtube.json", "yt1",
                injectTelegram: true);

        if (youtube)
            return StartYoutubeAsync();

        if (telegram)
            return StartTelegramAsync();

        return StopAsync();
    }

    private async Task StartProfileAsync(
        string profile,
        string logicalJson,
        string runtimeLeaf,
        bool injectYoutube = false,
        bool injectTelegram = false)
    {
        await StopAsync();

        if (!File.Exists(CoreExe))
            throw new FileNotFoundException("OnixBoost core missing (dpi/core/OnixBoost.exe)", CoreExe);
        var wintun = Path.Combine(CoreDir, "wintun.dll");
        if (!File.Exists(wintun))
            throw new FileNotFoundException("wintun.dll missing in dpi/core/", wintun);

        var live = TunnelConfigStore.Materialize(logicalJson, runtimeLeaf, injectYoutube, injectTelegram);
        _liveConfigPath = live;
        if (!File.Exists(live))
            throw new FileNotFoundException("Failed to materialize tunnel config", live);

        var rel = Path.GetRelativePath(CoreDir, live).Replace('/', '\\');
        var errLog = Path.Combine(CoreDir, ".rt", "core-err.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(errLog)!); } catch { }
        try { File.Delete(errLog); } catch { }

        // Validate config first (captures parse errors without TUN)
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
                        "Tunnel config error.\n" + tip[..Math.Min(700, tip.Length)]);
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

        await Task.Delay(1800);

        if (proc.HasExited || !IsRunning)
        {
            await Task.Delay(200);
            string tip;
            lock (logBuf) tip = logBuf.ToString();
            if (string.IsNullOrWhiteSpace(tip))
                tip = TryReadLog(profile);
            if (string.IsNullOrWhiteSpace(tip))
                tip = "process exited code=" + (proc.HasExited ? proc.ExitCode.ToString() : "?") +
                      " — run OnixBoost as Admin; allow firewall for OnixBoost";
            try { await File.WriteAllTextAsync(errLog, tip); } catch { }
            if (ReferenceEquals(_proc, proc))
            {
                _proc = null;
                _activeProfile = null;
            }
            TunnelConfigStore.ScrubRuntime();
            _liveConfigPath = null;
            throw new InvalidOperationException(
                "Tunnel failed to start.\n" + tip[..Math.Min(700, tip.Length)]);
        }

        Logger.Info(profile.StartsWith("games", StringComparison.OrdinalIgnoreCase)
            ? "Tunnel Games HY2 (DE) up (" + profile + ")"
            : "Tunnel NL HY2 up (" + profile + ")");
    }

    private static string TryReadLog(string profile)
    {
        var name = profile switch
        {
            "telegram" => "core-telegram.log",
            "games" or "games-yt" => "core-games.log",
            "youtube" or "youtube-tg" => "core-youtube.log",
            _ => "core.log"
        };
        foreach (var log in new[]
                 {
                     Path.Combine(CoreDir, name),
                     Path.Combine(CoreDir, ".rt", name),
                     Path.Combine(CoreDir, ".rt", "core-err.log")
                 })
        {
            if (File.Exists(log))
            {
                try { return File.ReadAllText(log); } catch { }
            }
        }
        return "";
    }

    public Task StopAsync()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(true);
                _proc.WaitForExit(3000);
            }
        }
        catch { }

        ProcessNames.KillCoreWorkers();

        _proc = null;
        _activeProfile = null;
        _liveConfigPath = null;
        TunnelConfigStore.ScrubRuntime();
        return Task.CompletedTask;
    }
}
