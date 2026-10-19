using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OnixBoost.Engine;

namespace OnixBoost.Services;

/// <summary>
/// Discord = Flowseal on WinwsSlot.Discord. Media (YouTube/Twitch) = shared winws on
/// WinwsSlot.Games — never merge Media into Discord. Telegram = HY2 (TunnelEngine).
/// </summary>
public sealed class StrategyAutoPicker
{
    private readonly WinwsEngine _discord;
    private readonly WinwsEngine _games;
    private readonly OnixDpiEngine _gamesDivert;
    private readonly HealthChecker _health;
    private readonly Zapret2Engine? _zapret2;
    private readonly StrategyGenerator _generator;
    private string? _discordArgsFile;

    public StrategyAutoPicker(
        WinwsEngine discordWinws,
        WinwsEngine gamesWinws,
        OnixDpiEngine gamesDivert,
        HealthChecker health,
        Zapret2Engine? zapret2 = null)
    {
        _discord = discordWinws;
        _games = gamesWinws;
        _gamesDivert = gamesDivert;
        _health = health;
        _zapret2 = zapret2;
        _generator = new StrategyGenerator(discordWinws, health);
    }

    /// <summary>Back-compat: shared winws + new divert engine.</summary>
    public StrategyAutoPicker(WinwsEngine winws, HealthChecker health, Zapret2Engine? zapret2 = null)
        : this(winws, winws, new OnixDpiEngine(), health, zapret2)
    {
    }

    public static IReadOnlyList<(string Id, string Title, string ArgsFile)> LoadOrder()
    {
        var meta = Path.Combine(AppPaths.ArgsDir, "flowseal_auto_order.json");
        if (!File.Exists(meta))
            return Array.Empty<(string, string, string)>();

        using var doc = JsonDocument.Parse(File.ReadAllText(meta));
        var list = new List<(string, string, string)>();
        foreach (var s in doc.RootElement.GetProperty("strategies").EnumerateArray())
        {
            var id = s.GetProperty("id").GetString()!;
            var title = s.GetProperty("title").GetString() ?? id;
            var file = s.GetProperty("file").GetString()!;
            if (File.Exists(Path.Combine(AppPaths.ArgsDir, file)))
                list.Add((id, title, file));
        }
        return list;
    }

    public static string TargetLabel(BoostTarget t) => t switch
    {
        BoostTarget.Discord => "Discord",
        BoostTarget.Youtube => "YouTube",
        BoostTarget.Twitch => "Twitch",
        BoostTarget.Telegram => "Telegram",
        _ => t.ToString()
    };

    public static string? ResolveArgsFile(string strategyId)
    {
        var hit = LoadOrder().FirstOrDefault(x => x.Id == strategyId);
        return string.IsNullOrEmpty(hit.ArgsFile) ? null : hit.ArgsFile;
    }

    public string[] BuildAppends(bool games, bool youtube, bool withDiscord = false, bool withTelegram = false)
    {
        var list = new List<string>();
        if (games)
        {
            // Do NOT append steam_main: multisplit on Steam CDN breaks the in-client store.
            // Steam stays ISP-direct; Games card only boosts Epic/Riot/etc. (games_main).
            if (File.Exists(Path.Combine(AppPaths.ArgsDir, "games_main.txt")))
                list.Add("games_main.txt");
        }
        if (youtube && File.Exists(Path.Combine(AppPaths.ArgsDir, "youtube_main.txt")))
            list.Add("youtube_main.txt");
        if (withTelegram && File.Exists(Path.Combine(AppPaths.ArgsDir, "telegram_main.txt")))
            list.Add("telegram_main.txt");
        return list.ToArray();
    }

    public string[] BuildAppends(BoostTarget target, bool withTelegram = false)
    {
        var youtube = target is BoostTarget.Youtube;
        var withDiscord = target is BoostTarget.Discord;
        return BuildAppends(games: false, youtube, withDiscord, withTelegram);
    }

    /// <summary>Discord = solo Flowseal winws. Never merge Media (YT/Twitch) into Discord.</summary>
    public async Task StartDiscordAsync(string argsFile, bool gamesAlsoOn)
    {
        _ = gamesAlsoOn; // ignored — Media owns Games slot; Discord stays solo
        _discordArgsFile = argsFile;
        await _discord.StartAsync(
            argsFile,
            autoHostList: false,
            discordStreamSafe: true, // identity — vanilla Flowseal args
            preserveYoutubeQuic: false,
            exclusiveWf: null);
        Logger.Info("Discord winws up (vanilla Flowseal " + argsFile + "; Media untouched)");
    }

    /// <summary>
    /// Media DPI on Games slot: YouTube and/or Twitch hostlists. Both OFF → stop Media.
    /// Does not touch Discord winws.
    /// </summary>
    public async Task StartMediaAsync(bool youtube, bool twitch)
    {
        try { await _gamesDivert.StopAsync(); } catch { }

        if (!youtube && !twitch)
        {
            try { await _games.StopAsync(killAllWinws: false); } catch { }
            Logger.Info("Media DPI stopped (YT/Twitch off)");
            return;
        }

        if (youtube && twitch)
        {
            await _games.StartAsync(
                "youtube_main.txt",
                autoHostList: false,
                discordStreamSafe: false,
                preserveYoutubeQuic: true,
                exclusiveWf: null,
                "twitch_main.txt");
        }
        else if (youtube)
        {
            await _games.StartAsync(
                "youtube_main.txt",
                autoHostList: false,
                discordStreamSafe: false,
                preserveYoutubeQuic: true);
        }
        else
        {
            await _games.StartAsync(
                "twitch_main.txt",
                autoHostList: false,
                discordStreamSafe: false);
        }

        Logger.Info($"Media DPI up yt={youtube} tw={twitch}");
    }

    /// <summary>Legacy name — clears Media leftovers only.</summary>
    public async Task StartGamesAsync(bool discordAlsoOn)
    {
        _ = discordAlsoOn;
        await StartMediaAsync(youtube: false, twitch: false);
    }

    public async Task StartKnownAsync(string argsFile, bool games, bool youtube, bool withDiscord = false, bool withTelegram = false)
    {
        if (withDiscord)
            await StartDiscordAsync(argsFile, gamesAlsoOn: games);
        if (games)
            await StartGamesAsync(discordAlsoOn: withDiscord);
        if (!withDiscord && !games && withTelegram)
            await _discord.StartAsync("telegram_main.txt", autoHostList: false, discordStreamSafe: false);
    }

    public async Task StartKnownAsync(string argsFile, BoostTarget target, bool withTelegram)
    {
        await StartKnownAsync(argsFile,
            games: false,
            youtube: target is BoostTarget.Youtube,
            withDiscord: target is BoostTarget.Discord,
            withTelegram: withTelegram || target is BoostTarget.Telegram);
    }

    public async Task StartTelegramWebDpiAsync()
    {
        await _discord.StartAsync("telegram_main.txt", autoHostList: false, discordStreamSafe: false);
    }

    public async Task StopDiscordAsync() => await _discord.StopAsync(killAllWinws: false);

    /// <summary>Stop Media (Games slot) only — never touches Discord.</summary>
    /// <param name="discordStillOn">Ignored (kept for call-site compatibility).</param>
    public async Task StopGamesAsync(bool discordStillOn = false)
    {
        _ = discordStillOn;
        await StartMediaAsync(youtube: false, twitch: false);
    }

    public async Task StopAllWinwsAsync()
    {
        try { await _gamesDivert.StopAsync(); } catch { }
        try { await _discord.StopAsync(killAllWinws: false); } catch { }
        try { await _games.StopAsync(killAllWinws: false); } catch { }
        ProcessLocker.KillByName("winws");
        await Task.CompletedTask;
    }

    public bool GamesDivertRunning => _gamesDivert.IsRunning;

    public async Task<(string? Id, string? Title, string? ArgsFile)> FindWorkingDiscordAsync(
        Action<string> log,
        CancellationToken ct,
        string? preferId = null,
        int maxStrategies = 8,
        Action<string>? status = null,
        bool gamesAlsoOn = false)
    {
        var order = LoadOrder().ToList();
        if (order.Count == 0)
        {
            log("Flowseal strategies missing (dpi/args/flowseal_*.txt)");
            return (null, null, null);
        }

        if (!string.IsNullOrEmpty(preferId))
        {
            var idx = order.FindIndex(x => x.Id == preferId);
            if (idx > 0)
            {
                var pref = order[idx];
                order.RemoveAt(idx);
                order.Insert(0, pref);
            }
        }

        if (maxStrategies > 0 && order.Count > maxStrategies)
            order = order.Take(maxStrategies).ToList();

        if (!string.IsNullOrEmpty(preferId) && order[0].Id == preferId)
            log($"Сначала последняя OK: {order[0].Title}");

        log(gamesAlsoOn
            ? $"Auto-sweep Discord (Flowseal; Games appends after OK): {order.Count}×…"
            : $"Auto-sweep Discord: {order.Count}× Flowseal...");

        for (var i = 0; i < order.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (id, title, file) = order[i];
            var preferred = i == 0 && !string.IsNullOrEmpty(preferId) && id == preferId;
            log($"[{i + 1}/{order.Count}] {title}...");
            status?.Invoke($"Подключение…");
            try
            {
                await StartDiscordAsync(file, gamesAlsoOn: false);
                await Task.Delay(preferred ? 280 : 160, ct);
                var h = await _health.CheckTargetAsync(BoostTarget.Discord, ct, fastFail: true);
                if (h.Ok)
                {
                    log($"OK: {title} ({h.Ms} ms) — {h.Detail}");
                    return (id, title, file);
                }
                log($"  fail ({h.Ms} ms): {h.Detail}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log($"  error: {ex.Message}");
            }

            try { await _discord.StopAsync(killAllWinws: false); } catch { }
        }

        return (null, null, null);
    }

    public async Task<(string? Id, string? Title, string? ArgsFile)> FindWorkingGamesAsync(
        Action<string> log,
        CancellationToken ct,
        bool discordAlsoOn,
        Action<string>? status = null)
    {
        log(discordAlsoOn
            ? "Games sweep (games_main via Discord appends; Steam passthrough)…"
            : "Games sweep (games_main winws; Steam passthrough)…");
        status?.Invoke("Подключение…");
        try
        {
            await StartGamesAsync(discordAlsoOn);
            await Task.Delay(320, ct);
            var h = await _health.CheckTargetAsync(BoostTarget.Twitch, ct, fastFail: true);
            if (h.Ok)
            {
                log($"OK: Games ({h.Ms} ms) — {h.Detail}");
                return ("games_winws", "Games winws launchers", "games_main.txt");
            }
            log($"  Games soft ({h.Ms} ms): {h.Detail}");
            // Soft-OK: winws is up; storefront probe can be flaky on some ISPs.
            return ("games_winws", "Games winws launchers", "games_main.txt");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log($"  Games error: {ex.Message}");
            try { await StopGamesAsync(); } catch { }
            return (null, null, null);
        }
    }

    /// <summary>Legacy combined sweep — Discord path only (Games has its own slot).</summary>
    public async Task<(string? Id, string? Title, string? ArgsFile)> FindWorkingAsync(
        BoostTarget healthTarget,
        Action<string> log,
        CancellationToken ct,
        string? preferId = null,
        bool games = false,
        bool youtube = false,
        bool withDiscord = false,
        bool withTelegram = false,
        int maxStrategies = 8,
        Action<string>? status = null)
    {
        if (healthTarget is BoostTarget.Discord)
            withDiscord = true;

        if (withDiscord || healthTarget is BoostTarget.Discord)
        {
            return await FindWorkingDiscordAsync(log, ct, preferId, maxStrategies, status,
                gamesAlsoOn: false);
        }

        // Twitch Soft card is Media DPI — no Flowseal strategy sweep
        if (healthTarget is BoostTarget.Twitch or BoostTarget.Youtube)
            return (null, null, null);

        if (games)
            return await FindWorkingGamesAsync(log, ct, discordAlsoOn: withDiscord, status);

        return (null, null, null);
    }

    public async Task<(string? Id, string? Title, string? ArgsFile)> FindWorkingExtendedAsync(
        BoostTarget healthTarget,
        Action<string> log,
        CancellationToken ct,
        string? preferId = null,
        bool games = false,
        bool youtube = false,
        bool withDiscord = false,
        bool withTelegram = false,
        bool skipZapret2 = false,
        bool skipGenerate = false,
        int maxStrategies = 8,
        Action<string>? status = null)
    {
        var flow = await FindWorkingAsync(healthTarget, log, ct, preferId, games, youtube, withDiscord,
            withTelegram, maxStrategies, status);
        if (flow.Id != null) return flow;

        if (skipZapret2 || skipGenerate)
            return (null, null, null);

        return await _generator.FindWorkingAsync(
            healthTarget, log, ct, games, youtube, withDiscord, BuildAppends);
    }

    public Task<(string? Id, string? Title, string? ArgsFile)> FindWorkingAsync(
        BoostTarget target,
        Action<string> log,
        CancellationToken ct,
        string? preferId,
        bool withTelegram) =>
        FindWorkingAsync(target, log, ct, preferId,
            games: false,
            youtube: target is BoostTarget.Youtube,
            withDiscord: target is BoostTarget.Discord,
            withTelegram: withTelegram);
}
