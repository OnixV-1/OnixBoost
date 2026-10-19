using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OnixBoost.Engine;
using OnixBoost.Models;
using OnixBoost.Services;

namespace OnixBoost;

public partial class MainWindow : Window
{
    private readonly OnixDpiEngine _native = new();
    private readonly WinwsEngine _winwsDiscord = new(WinwsSlot.Discord);
    private readonly WinwsEngine _winwsGames = new(WinwsSlot.Games);
    private readonly Zapret2Engine _zapret2 = new();
    private readonly TunnelEngine _tunnel = new();
    private readonly HealthChecker _health = new();
    private readonly StrategyAutoPicker _picker;
    private AppSettings _settings = SettingsStore.Load();
    // Discord / Games / TG / YT each have own CTS — dual winws must not cancel each other
    private CancellationTokenSource? _cts;
    private int _opGen;
    private CancellationTokenSource? _twitchCts;
    private int _twitchGen;
    private CancellationTokenSource? _tgCts;
    private int _tgGen;
    private CancellationTokenSource? _ytCts;
    private int _ytGen;
    // Independent cards — any combination can be ON at once
    private bool _discordOn;
    private bool _youtubeOn;
    private bool _twitchOn; // Twitch HY2 (shared media TUN with YouTube, per-card domains)
    private bool _telegramOn;
    private bool _intentDiscord;
    private bool _intentYoutube;
    private bool _intentTwitch;
    private string? _dpiStrategyId;
    private string? _dpiStrategyFile;
    private string? _dpiStrategyTitle;
    private string? _twitchStrategyFile;
    private bool _telegramTunnelOwned;
    private bool _twitchTunnelOwned;
    private bool _youtubeTunnelOwned;
    private BoostTarget? _busyTarget; // Discord connect spinner only
    private bool _twitchConnecting;
    private bool _youtubeConnecting;
    private bool _telegramConnecting;
    private CancellationTokenSource? _discordWatchCts;
    private int _discordWatchGen;
    private UpdateInfo? _pendingUpdate;
    private bool _updateBusy;
    private CancellationTokenSource? _updatePollCts;
    private readonly BinaryUpdater _binaryUpdater = new();
    private bool _dnsApplied;
    /// <summary>NetworkFix + full WinDivert once per process — reconnects use light kill only.</summary>
    private bool _sessionPrepared;
    private static readonly TimeSpan UpdatePollInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(6);
    // Discord = Flowseal; YT/Twitch/TG = HY2 (not Discord DPI helpers)
    private bool AnyDpiOn => _discordOn;
    private bool AnyDpiIntent => _intentDiscord;

    /// <summary>Hard cap so «Подключение» cannot hang for minutes (22×2 slow probes).</summary>
    private static readonly TimeSpan DpiOpDeadline = TimeSpan.FromSeconds(75);
    /// <summary>Discord may soft-retry several sweeps before giving up.</summary>
    private static readonly TimeSpan DiscordOpDeadline = TimeSpan.FromSeconds(140);

    /// <summary>Cancel previous Discord op (if any) and start a new CTS. Does not touch Telegram/Games/YT.</summary>
    private (CancellationToken Ct, int Gen) BeginOp()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = new CancellationTokenSource(DiscordOpDeadline);
        var gen = Interlocked.Increment(ref _opGen);
        return (_cts.Token, gen);
    }

    private void CancelDiscordWatch()
    {
        try { _discordWatchCts?.Cancel(); } catch { }
        try { _discordWatchCts?.Dispose(); } catch { }
        _discordWatchCts = null;
        Interlocked.Increment(ref _discordWatchGen);
    }

    private bool IsCurrentOp(int gen) => gen == Volatile.Read(ref _opGen);

    private void CancelCurrentOp()
    {
        try { _cts?.Cancel(); } catch { }
    }

    private (CancellationToken Ct, int Gen) BeginTgOp()
    {
        try { _tgCts?.Cancel(); } catch { }
        try { _tgCts?.Dispose(); } catch { }
        _tgCts = new CancellationTokenSource();
        var gen = Interlocked.Increment(ref _tgGen);
        return (_tgCts.Token, gen);
    }

    private bool IsCurrentTgOp(int gen) => gen == Volatile.Read(ref _tgGen);

    private void CancelTgOp()
    {
        try { _tgCts?.Cancel(); } catch { }
    }

    private (CancellationToken Ct, int Gen) BeginYtOp()
    {
        try { _ytCts?.Cancel(); } catch { }
        try { _ytCts?.Dispose(); } catch { }
        _ytCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var gen = Interlocked.Increment(ref _ytGen);
        return (_ytCts.Token, gen);
    }

    private bool IsCurrentYtOp(int gen) => gen == Volatile.Read(ref _ytGen);

    private void CancelYtOp()
    {
        try { _ytCts?.Cancel(); } catch { }
    }

    private (CancellationToken Ct, int Gen) BeginTwitchOp()
    {
        try { _twitchCts?.Cancel(); } catch { }
        try { _twitchCts?.Dispose(); } catch { }
        _twitchCts = new CancellationTokenSource(DpiOpDeadline);
        var gen = Interlocked.Increment(ref _twitchGen);
        return (_twitchCts.Token, gen);
    }

    private bool IsCurrentTwitchOp(int gen) => gen == Volatile.Read(ref _twitchGen);

    private void CancelTwitchOp()
    {
        try { _twitchCts?.Cancel(); } catch { }
    }

    private void SetDpiIntent(BoostTarget target, bool on)
    {
        switch (target)
        {
            case BoostTarget.Discord: _intentDiscord = on; break;
            case BoostTarget.Youtube: _intentYoutube = on; break;
            case BoostTarget.Twitch: _intentTwitch = on; break;
        }
    }

    private void ClearDpiIntents()
    {
        _intentDiscord = false;
        _intentYoutube = false;
        _intentTwitch = false;
    }

    private static readonly Color ColDiscord = Color.Parse("#5865F2");
    private static readonly Color ColYoutube = Color.Parse("#FF0000");
    private static readonly Color ColTwitch = Color.Parse("#9146FF");
    private static readonly Color ColTelegram = Color.Parse("#229ED9");
    private static readonly Color ColActive = Color.Parse("#12B76A");
    private static readonly Color ColActiveBorder = Color.Parse("#32DC8C");

    public MainWindow()
    {
        _picker = new StrategyAutoPicker(_winwsDiscord, _winwsGames, _native, _health, _zapret2);
        InitializeComponent();
        UpdateStatusUi(false);
        RefreshCards();
        if (string.IsNullOrEmpty(_settings.LastFlowsealDiscord) && !string.IsNullOrEmpty(_settings.LastFlowsealStrategy))
            _settings.LastFlowsealDiscord = _settings.LastFlowsealStrategy;
        ResetDiscordPreferIfFlowsealProfileChanged();
        VersionText.Text = "v" + AppUpdateChecker.CurrentVersion;
        Log("OnixBoost " + AppUpdateChecker.CurrentVersion);
        if (PathAscii.RootHasNonAscii)
            Log("Лучше путь C:\\OnixBoost (без кириллицы).");
        AppPaths.HideDpiFolder();
        AppUpdateChecker.CleanupAfterUpdate();
        try { BinaryUpdater.ApplyOnixListPatches(); } catch { }
        SetBusy(null);
        _ = RunBackgroundMaintenanceAsync();
    }

    /// <summary>
    /// New Flowseal import (vanilla 1.10.3) — drop saved Discord strategy so Soft re-sweeps
    /// from SIMPLE FAKE instead of a broken ALT13 prefer from older Soft rewrites.
    /// </summary>
    private void ResetDiscordPreferIfFlowsealProfileChanged()
    {
        try
        {
            var metaPath = Path.Combine(AppPaths.ArgsDir, "flowseal_auto_order.json");
            if (!File.Exists(metaPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var ver = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var profile = doc.RootElement.TryGetProperty("profile", out var p) ? p.GetString() ?? "" : "";
            var want = $"{ver}|{profile}";
            var stamp = Path.Combine(AppPaths.DpiRoot, ".discord-flowseal-profile");
            var prev = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
            if (prev == want) return;
            if (!string.IsNullOrEmpty(_settings.LastFlowsealDiscord) ||
                !string.IsNullOrEmpty(_settings.LastFlowsealStrategy))
            {
                Log($"Discord Soft: новый Flowseal {ver} — сброс стратегии, подбор заново");
                _settings.LastFlowsealDiscord = null;
                _settings.LastFlowsealStrategy = null;
                SettingsStore.Save(_settings);
            }
            Directory.CreateDirectory(AppPaths.DpiRoot);
            File.WriteAllText(stamp, want);
        }
        catch (Exception ex)
        {
            Log("Discord Soft profile stamp: " + ex.Message);
        }
    }

    /// <summary>Updates every 30m; Flowseal lists + Discord IPs every 6h when idle.</summary>
    private async Task RunBackgroundMaintenanceAsync()
    {
        _updatePollCts?.Cancel();
        _updatePollCts?.Dispose();
        _updatePollCts = new CancellationTokenSource();
        var ct = _updatePollCts.Token;
        await CheckForUpdatesAsync(ct);
        _ = Task.Run(() => QuietSyncAsync(force: false, ct), ct);

        var nextMaint = DateTime.UtcNow + MaintenanceInterval;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(UpdatePollInterval, ct); }
            catch (OperationCanceledException) { break; }
            if (_updateBusy) continue;
            await CheckForUpdatesAsync(ct);
            if (DateTime.UtcNow >= nextMaint)
            {
                nextMaint = DateTime.UtcNow + MaintenanceInterval;
                if (!EngineAlive)
                    await QuietSyncAsync(force: false, ct);
            }
        }
    }

    private async Task QuietSyncAsync(bool force, CancellationToken ct)
    {
        try
        {
            var (ipOk, ipMsg) = await DiscordIpSync.SyncAsync(force, ct);
            if (ipOk && !ipMsg.Contains("cached", StringComparison.OrdinalIgnoreCase) &&
                !ipMsg.Contains("stamp", StringComparison.OrdinalIgnoreCase))
                Log(ipMsg);

            if (EngineAlive) return; // lists copy needs idle
            var (listOk, listMsg) = await _binaryUpdater.SyncListsAsync(force, ct);
            if (listOk && listMsg.StartsWith("Flowseal lists ←", StringComparison.Ordinal))
                Log(listMsg);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("Sync: " + ex.Message);
        }
    }

    private async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var info = await AppUpdateChecker.CheckAsync(ct);
            if (info == null || ct.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_updateBusy) return;
                var same = _pendingUpdate != null &&
                           string.Equals(_pendingUpdate.Version, info.Version, StringComparison.OrdinalIgnoreCase);
                _pendingUpdate = info;
                UpdateBtn.Content = "Обновить " + info.Version;
                UpdateBtn.IsVisible = true;
                if (!same)
                    Log("Доступно обновление: " + info.Version);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("Update check: " + ex.Message);
        }
    }

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _pendingUpdate == null) return;
        _updateBusy = true;
        UpdateBtn.IsEnabled = false;
        UpdateBtn.Content = "Обновление…";
        StatusText.Text = "Остановка…";
        StatusText.Foreground = new SolidColorBrush(Color.Parse("#4DA3FF"));
        try
        {
            CancelCurrentOp();
            CancelTgOp();
            try { _updatePollCts?.Cancel(); } catch { }

            // Never block UI on DNS/WinDivert stop — cap wait so Update can't freeze here
            try
            {
                await Task.Run(async () =>
                {
                    var stop = StopAllAsync();
                    var done = await Task.WhenAny(stop, Task.Delay(12000));
                    if (done != stop)
                        Log("Update: stop timeout — продолжаем установку");
                }).ConfigureAwait(true);
            }
            catch (Exception ex) { Log("Update stop: " + ex.Message); }

            ResetAllBoostState();
            UpdateStatusUi(false);

            var progress = new Progress<string>(s =>
            {
                // Always marshal to UI — ApplyUpdate runs on threadpool
                Dispatcher.UIThread.Post(() =>
                {
                    var shortMsg = s.Length > 22 ? s[..22] + "…" : s;
                    UpdateBtn.Content = shortMsg;
                    StatusText.Text = s;
                    StatusText.Foreground = new SolidColorBrush(Color.Parse("#4DA3FF"));
                });
            });

            var info = _pendingUpdate;
            await Task.Run(() => AppUpdateChecker.ApplyUpdateAsync(info, progress)).ConfigureAwait(true);

            StatusText.Text = "Закрытие для установки…";
            UpdateBtn.Content = "Установка…";
            // Watcher already scheduled: waits for our PID exit, then runs Setup /silent.
            // Soft Shutdown often hangs with WinDivert/tray — hard-exit so Setup can start.
            App.ExitRequested = true;
            try { await Task.Run(() => ProcessNames.KillAllWorkers()); } catch { }
            await Task.Delay(200);
            try { Hide(); } catch { }
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log("Update ERROR: " + ex);
            StatusText.Text = "Ошибка обновления — ставьте Setup вручную";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#FF5A64"));
            UpdateBtn.Content = "Обновить " + (_pendingUpdate?.Version ?? "");
            UpdateBtn.IsEnabled = true;
            _updateBusy = false;
            try { AppUpdateChecker.OpenReleasesInBrowser(); } catch { }
        }
    }

    private void RefreshCards()
    {
        // During DPI rebuild already-ON cards are temporarily down — don't show green «ОНЛАЙН»
        bool discordConn = IsDpiConnecting(BoostTarget.Discord);
        bool youtubeConn = _youtubeConnecting && !_youtubeOn;
        bool twitchConn = _twitchConnecting && !_twitchOn;
        bool tgConn = _telegramConnecting && !_telegramOn;

        SetCardLooks(CardDiscord, ColDiscord, LabelDiscord,
            discordConn ? "Подключение" : "", _discordOn && !discordConn);
        SetCardLooks(CardYoutube, ColYoutube, LabelYoutube,
            youtubeConn ? "Подключение" : "", _youtubeOn && !youtubeConn);
        SetCardLooks(CardTwitch, ColTwitch, LabelTwitch,
            twitchConn ? "Подключение" : "", _twitchOn && !twitchConn);
        SetCardLooks(CardTelegram, ColTelegram, LabelTelegram,
            tgConn ? "Подключение" : "", _telegramOn && !tgConn);

        SpinDiscord.IsVisible = discordConn;
        SpinYoutube.IsVisible = youtubeConn;
        SpinTwitch.IsVisible = twitchConn;
        SpinTelegram.IsVisible = tgConn;
    }

    /// <summary>
    /// DPI card mid-connect. YouTube/Twitch/Telegram busy must NOT flash Discord as «Подключение».
    /// </summary>
    private bool IsDpiConnecting(BoostTarget t) => t switch
    {
        BoostTarget.Discord => _intentDiscord || _busyTarget == BoostTarget.Discord,
        _ => false
    };

    private void RefreshHealthSummary()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (_discordOn) parts.Add("Discord");
        if (_youtubeOn) parts.Add("YouTube");
        if (_twitchOn) parts.Add("Twitch");
        if (_telegramOn) parts.Add("Telegram");
        if (parts.Count == 0)
        {
            UpdateStatusUi(false);
            return;
        }
        StatusText.Text = string.Join(" · ", parts);
        StatusText.Foreground = new SolidColorBrush(ColActiveBorder);
    }

    private void SetBusy(BoostTarget? target)
    {
        // DPI busy only — never steal YouTube/Twitch/Telegram connecting flags
        if (target is BoostTarget.Youtube or BoostTarget.Twitch or BoostTarget.Telegram)
            return;
        _busyTarget = target;
        RefreshCards();
        if (target is BoostTarget.Discord)
            StatusText.Text = "Подключение";
        else if (!_youtubeConnecting && !_twitchConnecting && !_telegramConnecting)
        {
            var st = StatusText.Text ?? "";
            if (st == "Подключение" || st.StartsWith("Подключение", StringComparison.Ordinal) ||
                st.StartsWith("Подбор", StringComparison.Ordinal) ||
                st.StartsWith("Ищем", StringComparison.Ordinal))
                StatusText.Text = EngineAlive ? "Работает" : "Готов";
        }
    }

    private bool IsServiceOn(BoostTarget t) => t switch
    {
        BoostTarget.Discord => _discordOn,
        BoostTarget.Youtube => _youtubeOn,
        BoostTarget.Twitch => _twitchOn,
        BoostTarget.Telegram => _telegramOn,
        _ => false
    };

    private async Task ToggleOrBoostAsync(BoostTarget target)
    {
        // YouTube / Twitch = NL HY2 media TUN (per-card domains), not Discord DPI
        if (target == BoostTarget.Youtube)
        {
            await ToggleYoutubeTunnelAsync();
            return;
        }

        if (target == BoostTarget.Twitch)
        {
            await ToggleTwitchTunnelAsync();
            return;
        }

        // Discord
        if (_busyTarget == BoostTarget.Discord && _intentDiscord)
        {
            SetDpiIntent(BoostTarget.Discord, false);
            CancelCurrentOp();
            SetBusy(null);
            RefreshCards();
            return;
        }
        if (IsServiceOn(BoostTarget.Discord))
        {
            SetServiceOn(BoostTarget.Discord, false);
            SetDpiIntent(BoostTarget.Discord, false);
            RefreshCards();
            await TurnOffServiceAsync(BoostTarget.Discord, alreadyCleared: true);
            return;
        }
        SetDpiIntent(BoostTarget.Discord, true);
        SetBusy(BoostTarget.Discord);
        await Task.Yield();
        await BoostDiscordAsync();
    }

    private async Task ToggleTwitchTunnelAsync()
    {
        if (_twitchConnecting)
        {
            CancelTwitchOp();
            return;
        }

        if (_twitchOn)
        {
            _twitchOn = false;
            _twitchTunnelOwned = false;
            RefreshCards();
            try { await RebuildServiceTunnelAsync(CancellationToken.None); } catch { }
            UpdateStatusUi(EngineAlive);
            RefreshCards();
            RefreshHealthSummary();
            return;
        }

        _twitchConnecting = true;
        RefreshCards();
        SetStatusProgress("Подключение", "#4DA3FF");
        await Task.Yield();
        var (ct, gen) = BeginTwitchOp();
        try
        {
            if (_discordOn)
            {
                try { _ = DiscordIpSync.SyncAsync(force: false, ct); } catch { }
            }

            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RebuildServiceTunnelAsync(ct, forceTwitch: true);
                    if (_tunnel.IsRunning) { last = null; break; }
                    last = new InvalidOperationException("туннель не стартовал");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    Log($"Twitch tunnel try {attempt}/3: {ex.Message}");
                }
                await Task.Delay(400 * attempt, ct);
            }
            if (last != null || !_tunnel.IsRunning)
                throw last ?? new InvalidOperationException("туннель не стартовал");

            await Task.Delay(500, ct);
            var probe = await _health.CheckTargetAsync(BoostTarget.Twitch, ct, fastFail: true);
            Log(probe.Ok
                ? $"Twitch OK ({probe.Detail}, {probe.Ms} ms)"
                : $"Twitch tunnel up · probe soft: {probe.Detail}");

            _twitchOn = true;
            _twitchTunnelOwned = true;
            _twitchConnecting = false;
            _intentTwitch = false;

            if (_discordOn)
                _ = SoftRepairDiscordAfterTunnelAsync(CancellationToken.None);

            UpdateStatusUi(true);
            RefreshCards();
            RefreshHealthSummary();
        }
        catch (OperationCanceledException)
        {
            if (!IsCurrentTwitchOp(gen)) return;
            _twitchConnecting = false;
            if (!_twitchOn)
            {
                try { await RebuildServiceTunnelAsync(CancellationToken.None); } catch { }
            }
            RefreshCards();
            StatusText.Text = "Таймаут Twitch";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#F5A524"));
        }
        catch (Exception ex)
        {
            if (!IsCurrentTwitchOp(gen)) return;
            _twitchConnecting = false;
            _twitchOn = false;
            _twitchTunnelOwned = false;
            try { await RebuildServiceTunnelAsync(CancellationToken.None); } catch { }
            if (_discordOn)
                _ = SoftRepairDiscordAfterTunnelAsync(CancellationToken.None);
            RefreshCards();
            Log("Twitch ERROR: " + ex.Message);
            var tip = ex.Message.Replace("\u001b", "");
            if (tip.Length > 48) tip = tip[..48] + "…";
            StatusText.Text = "Twitch: " + tip;
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#FF5A64"));
        }
    }

    private bool GetDpiIntent(BoostTarget t) => t switch
    {
        BoostTarget.Discord => _intentDiscord,
        BoostTarget.Youtube => _intentYoutube,
        BoostTarget.Twitch => _intentTwitch,
        _ => false
    };

    private BoostTarget? FirstDpiIntentOrOn()
    {
        if (_intentDiscord || _discordOn) return BoostTarget.Discord;
        return null;
    }

    private void ResetAllBoostState()
    {
        CancelDiscordWatch();
        _discordOn = false;
        _youtubeOn = false;
        _twitchOn = false;
        _telegramOn = false;
        ClearDpiIntents();
        _dpiStrategyId = null;
        _dpiStrategyFile = null;
        _dpiStrategyTitle = null;
        _twitchStrategyFile = null;
        _telegramTunnelOwned = false;
        _twitchTunnelOwned = false;
        _youtubeTunnelOwned = false;
        _youtubeConnecting = false;
        _telegramConnecting = false;
        _twitchConnecting = false;
        RefreshCards();
    }

    private void SetServiceOn(BoostTarget target, bool on)
    {
        switch (target)
        {
            case BoostTarget.Discord:
                _discordOn = on;
                break;
            case BoostTarget.Youtube:
                _youtubeOn = on;
                break;
            case BoostTarget.Twitch:
                _twitchOn = on;
                break;
        }
    }

    private void RememberDpiStrategy(string id, string file, string title)
    {
        _dpiStrategyId = id;
        _dpiStrategyFile = file;
        _dpiStrategyTitle = title;
    }

    private async Task StopDpiOnlyAsync()
    {
        try { await _picker.StopAllWinwsAsync(); } catch { }
        try { await _zapret2.StopAsync(); } catch { }
        try { await _native.StopAsync(); } catch { }
    }

    private async Task TurnOffServiceAsync(BoostTarget target, bool alreadyCleared = false)
    {
        if (target == BoostTarget.Twitch)
            CancelTwitchOp();
        else if (target == BoostTarget.Discord)
            CancelCurrentOp();

        if (!alreadyCleared)
            SetServiceOn(target, false);
        SetDpiIntent(target, false);

        if (target == BoostTarget.Youtube)
        {
            _youtubeTunnelOwned = false;
            try { await RebuildServiceTunnelAsync(CancellationToken.None); } catch { }
        }

        if (target == BoostTarget.Twitch)
        {
            _twitchTunnelOwned = false;
            _twitchStrategyFile = null;
            try { await RebuildServiceTunnelAsync(CancellationToken.None); }
            catch (Exception ex) { Log("Twitch tunnel stop: " + ex.Message); }
        }
        else if (target == BoostTarget.Discord)
        {
            CancelDiscordWatch();
            _dpiStrategyFile = null;
            _dpiStrategyId = null;
            _dpiStrategyTitle = null;
            try { await _picker.StopDiscordAsync(); } catch { }
        }

        // WinDivert cleanup only when Discord DPI is fully off (HY2 TUN is unrelated).
        if (!_discordOn)
        {
            try { await _zapret2.StopAsync(); } catch { }
            try { await _native.StopAsync(); } catch { }
            if (_dnsApplied)
            {
                try
                {
                    var (ok, msg) = await DohHelper.RestoreAsync();
                    Log(ok ? msg : ("DNS restore: " + msg));
                }
                catch (Exception ex) { Log("DNS restore: " + ex.Message); }
                _dnsApplied = false;
            }
            try { await WinDivertCleanup.RunAsync(); } catch { }
        }

        _twitchConnecting = false;
        SetBusy(null);
        UpdateStatusUi(EngineAlive);
        RefreshCards();
        RefreshHealthSummary();
    }

    private static void SetCardLooks(Border card, Color bg, TextBlock label, string idleText, bool active)
    {
        card.Background = new SolidColorBrush(active ? ColActive : bg);
        card.BorderBrush = active
            ? new SolidColorBrush(ColActiveBorder)
            : new SolidColorBrush(Colors.Transparent);
        card.BorderThickness = active ? new Avalonia.Thickness(3) : new Avalonia.Thickness(0);
        // idleText may already be «ОНЛАЙН · TUN» for Games
        label.Text = active
            ? (string.IsNullOrEmpty(idleText) || idleText == "Подключение" ? "ОНЛАЙН" : idleText)
            : idleText;
        label.Foreground = new SolidColorBrush(active ? Colors.White : Color.Parse("#FFFFFFCC"));
    }

    private bool EngineAlive =>
        _zapret2.IsRunning || _native.IsRunning || _picker.GamesDivertRunning ||
        _winwsDiscord.IsRunning || _winwsGames.IsRunning || _tunnel.IsRunning;

    private void PersistUiSettings() => SettingsStore.Save(_settings);

    private static void Log(string msg) => Logger.Info(msg);

    private void UpdateStatusUi(bool on)
    {
        if (on)
        {
            StatusText.Text = "Работает";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#32DC8C"));
        }
        else
        {
            StatusText.Text = "Готов";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#7A8498"));
        }
    }

    private async Task StopAllAsync()
    {
        CancelDiscordWatch();
        await _tunnel.StopAsync();
        await _zapret2.StopAsync();
        await _native.StopAsync();
        await _picker.StopAllWinwsAsync();
        try { await WinDivertCleanup.RunAsync(); } catch { }
        if (_dnsApplied)
        {
            try
            {
                var (ok, msg) = await DohHelper.RestoreAsync();
                Log(ok ? msg : ("DNS restore: " + msg));
            }
            catch (Exception ex) { Log("DNS restore: " + ex.Message); }
            _dnsApplied = false;
        }
    }

    public async Task StopFromTrayAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _twitchCts?.Cancel(); } catch { }
        try { _tgCts?.Cancel(); } catch { }
        try { _ytCts?.Cancel(); } catch { }
        Interlocked.Increment(ref _opGen);
        Interlocked.Increment(ref _twitchGen);
        Interlocked.Increment(ref _tgGen);
        Interlocked.Increment(ref _ytGen);
        // Heavy stop off caller thread; UI updates marshal back
        await Task.Run(async () =>
        {
            try { await StopAllAsync(); }
            catch (Exception ex) { Log("Tray stop: " + ex.Message); }
        }).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ResetAllBoostState();
            UpdateStatusUi(false);
        });
        Log("Stopped (tray).");
    }

    private string? GetPrefer(BoostTarget t) => t switch
    {
        BoostTarget.Discord => _settings.LastFlowsealDiscord ?? _settings.LastFlowsealStrategy,
        BoostTarget.Youtube => _settings.LastFlowsealYoutube ?? _settings.LastFlowsealDiscord,
        BoostTarget.Twitch => _settings.LastFlowsealTwitch,
        _ => null
    };

    private void SavePrefer(BoostTarget t, string id)
    {
        switch (t)
        {
            case BoostTarget.Discord: _settings.LastFlowsealDiscord = id; break;
            case BoostTarget.Youtube: _settings.LastFlowsealYoutube = id; break;
            case BoostTarget.Twitch: _settings.LastFlowsealTwitch = id; break;
        }
        SettingsStore.Save(_settings);
    }

    private void SetStatusProgress(string text, string colorHex)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatusProgress(text, colorHex));
            return;
        }
        // UI: never show legacy «Подбор…» — always «Подключение…»
        if (string.IsNullOrWhiteSpace(text) ||
            text.StartsWith("Подбор", StringComparison.OrdinalIgnoreCase))
            text = "Подключение…";
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(colorHex));
    }

    private async Task EnsureSessionPreparedAsync(CancellationToken ct, bool killOtherWorkers)
    {
        if (!_sessionPrepared)
        {
            Log("NetworkFix...");
            Log(await NetworkFix.PrepareForDpiAsync());
            await WinDivertCleanup.RunAsync();
            if (_settings.EnableDoh)
            {
                Log("DNS (whitelist probe)…");
                var (dohOk, dohMsg) = await DohHelper.ApplyAsync();
                _dnsApplied = dohOk;
                Log(dohOk ? dohMsg : ("DNS: " + dohMsg));
            }
            _sessionPrepared = true;
        }
        else if (killOtherWorkers && !AnyDpiOn)
        {
            await WinDivertCleanup.KillWorkersAsync();
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task SyncDiscordIpsQuietAsync(CancellationToken ct)
    {
        try
        {
            var (ipOk, ipMsg) = await DiscordIpSync.SyncAsync(force: false, ct);
            if (ipOk && !ipMsg.Contains("cached", StringComparison.OrdinalIgnoreCase) &&
                !ipMsg.Contains("stamp", StringComparison.OrdinalIgnoreCase))
                Log(ipMsg);
        }
        catch (Exception ex) { Log("Discord IPs: " + ex.Message); }
    }

    private async Task BoostDiscordAsync()
    {
        var (ct, gen) = BeginOp();
        CancelDiscordWatch();
        string? lastSid = null;
        string? lastTitle = null;
        string? lastFile = null;
        try
        {
            SetBusy(BoostTarget.Discord);
            RefreshCards();
            SetStatusProgress("Подключение", "#4DA3FF");
            _ = Task.Run(PersistUiSettings);
            await Task.Yield();

            await EnsureSessionPreparedAsync(ct, killOtherWorkers: !_twitchOn && !_youtubeOn && !_telegramOn);
            await SyncDiscordIpsQuietAsync(ct);

            if (!_discordOn)
                await Task.Run(TryClearDiscordCache, ct);

            // Games is HY2 — never merge games_main into Discord winws.
            const bool withGames = false;

            const int maxRounds = 3;
            for (var round = 1; round <= maxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                if (round > 1)
                {
                    Log($"Discord: повтор {round}/{maxRounds}…");
                    SetStatusProgress($"Подключение · {round}", "#4DA3FF");
                    await Task.Delay(600 * round, ct);
                }

                // 1) Reuse last known strategy
                var reuseFile = !string.IsNullOrEmpty(_dpiStrategyFile)
                    ? _dpiStrategyFile
                    : StrategyAutoPicker.ResolveArgsFile(GetPrefer(BoostTarget.Discord) ?? "");
                if (!string.IsNullOrEmpty(reuseFile))
                {
                    try
                    {
                        Log($"Discord reuse (r{round})…");
                        await _picker.StartDiscordAsync(reuseFile, gamesAlsoOn: withGames);
                        lastFile = reuseFile;
                        lastSid = _dpiStrategyId ?? GetPrefer(BoostTarget.Discord) ?? "reuse";
                        lastTitle = _dpiStrategyTitle ?? lastSid;
                        await Task.Delay(450, ct);
                        if (await AcceptComboAsync(true, false, false, ct, retries: 2))
                        {
                            await FinishDiscordOnlineAsync(ct, lastSid, reuseFile, lastTitle, probeOk: true);
                            return;
                        }
                        Log("Discord reuse: probe soft — continue sweep");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log("Discord reuse: " + ex.Message); }
                }

                // 2) Flowseal sweep
                Log($"Discord: стратегии (r{round})…");
                var prefer = GetPrefer(BoostTarget.Discord);
                var (sid, stitle, sfile) = await _picker.FindWorkingDiscordAsync(
                    Log, ct, prefer, maxStrategies: 8,
                    status: _ => SetStatusProgress("Подключение…", "#4DA3FF"),
                    gamesAlsoOn: false);

                if (sid != null)
                {
                    lastSid = sid;
                    lastTitle = stitle ?? sid;
                    lastFile = sfile;
                    if (await AcceptComboAsync(true, false, false, ct, retries: 2))
                    {
                        SavePrefer(BoostTarget.Discord, sid);
                        await FinishDiscordOnlineAsync(ct, sid, sfile ?? "", stitle ?? sid, probeOk: true);
                        return;
                    }
                    // Strategy started but gateway probe flaky — keep winws, soft ONLINE
                    Log("Discord: winws up, probe soft — ONLINE + автопочинка");
                    SavePrefer(BoostTarget.Discord, sid);
                    await FinishDiscordOnlineAsync(ct, sid, sfile ?? "", stitle ?? sid, probeOk: false);
                    return;
                }
            }

            // Last resort: keep preferred strategy running if we had one this session
            if (!string.IsNullOrEmpty(lastFile))
            {
                try
                {
                    await _picker.StartDiscordAsync(lastFile, gamesAlsoOn: false);
                    Log("Discord: last strategy kept — ONLINE + автопочинка");
                    await FinishDiscordOnlineAsync(ct, lastSid ?? "retry", lastFile, lastTitle ?? "retry", probeOk: false);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log("Discord last-resort: " + ex.Message); }
            }

            await FailDpiConnectAsync(false, "Discord");
        }
        catch (OperationCanceledException)
        {
            if (!IsCurrentOp(gen)) return;
            // Deadline hit but we already have a strategy — soft ONLINE instead of «таймаут»
            if (!string.IsNullOrEmpty(lastFile) || !string.IsNullOrEmpty(_dpiStrategyFile))
            {
                var file = lastFile ?? _dpiStrategyFile!;
                var sid = lastSid ?? _dpiStrategyId ?? "timeout";
                var title = lastTitle ?? _dpiStrategyTitle ?? sid;
                try { await _picker.StartDiscordAsync(file, gamesAlsoOn: false); } catch { }
                Log("Discord: deadline — soft ONLINE + автопочинка");
                await FinishDiscordOnlineAsync(CancellationToken.None, sid, file, title, probeOk: false);
                return;
            }
            Log("Discord: таймаут/отмена");
            if (!_discordOn) _intentDiscord = false;
            SetBusy(null);
            RefreshCards();
            UpdateStatusUi(EngineAlive);
            StatusText.Text = "Таймаут — попробуй ещё";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#F5A524"));
        }
        catch (Exception ex)
        {
            if (!IsCurrentOp(gen)) return;
            // Prefer soft ONLINE over red error if winws strategy exists
            if (!string.IsNullOrEmpty(lastFile))
            {
                try
                {
                    await _picker.StartDiscordAsync(lastFile, gamesAlsoOn: false);
                    Log("Discord error soft → ONLINE: " + ex.Message);
                    await FinishDiscordOnlineAsync(CancellationToken.None,
                        lastSid ?? "err", lastFile, lastTitle ?? "err", probeOk: false);
                    return;
                }
                catch { }
            }
            if (!_discordOn) _intentDiscord = false;
            SetBusy(null);
            RefreshCards();
            UpdateStatusUi(EngineAlive);
            Log("ERROR: " + ex.Message);
            StatusText.Text = "Ошибка";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#FF5A64"));
        }
    }

    private async Task FinishDiscordOnlineAsync(
        CancellationToken ct, string sid, string sfile, string stitle, bool probeOk)
    {
        await FinishDpiSuccessAsync(true, false, false, ct, sid, sfile, stitle);
        // Media DPI stays on Games slot; Discord does not rebuild it.
        StartDiscordWatchdog();
        if (!probeOk)
            Log("Discord ONLINE (soft) — watchdog will recheck");
    }

    /// <summary>
    /// While Discord card is ON: if gateway dies, restart winws / re-sweep without user toggle.
    /// </summary>
    private void StartDiscordWatchdog()
    {
        CancelDiscordWatch();
        var cts = new CancellationTokenSource();
        _discordWatchCts = cts;
        var gen = Interlocked.Increment(ref _discordWatchGen);
        _ = DiscordWatchLoopAsync(cts.Token, gen);
    }

    private async Task DiscordWatchLoopAsync(CancellationToken ct, int gen)
    {
        try
        {
            while (!ct.IsCancellationRequested && gen == Volatile.Read(ref _discordWatchGen))
            {
                await Task.Delay(TimeSpan.FromSeconds(22), ct);
                if (!_discordOn || gen != Volatile.Read(ref _discordWatchGen))
                    break;

                var d = await _health.CheckTargetAsync(BoostTarget.Discord, ct, fastFail: true);
                if (d.Ok)
                    continue;

                Log($"Discord watchdog: down ({d.Detail}) — repair…");
                SetStatusProgress("Подключение", "#4DA3FF");

                var repaired = false;
                if (!string.IsNullOrEmpty(_dpiStrategyFile))
                {
                    try
                    {
                        await _picker.StartDiscordAsync(_dpiStrategyFile, gamesAlsoOn: false);
                        await Task.Delay(500, ct);
                        var again = await _health.CheckTargetAsync(BoostTarget.Discord, ct, fastFail: true);
                        if (again.Ok)
                        {
                            Log($"Discord watchdog OK reuse ({again.Ms} ms)");
                            repaired = true;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log("Discord watchdog reuse: " + ex.Message); }
                }

                if (!repaired && !ct.IsCancellationRequested && _discordOn)
                {
                    try
                    {
                        var prefer = GetPrefer(BoostTarget.Discord);
                        var (sid, stitle, sfile) = await _picker.FindWorkingDiscordAsync(
                            Log, ct, prefer, maxStrategies: 6,
                            status: _ => SetStatusProgress("Подключение…", "#4DA3FF"),
                            gamesAlsoOn: false);
                        if (sid != null && !string.IsNullOrEmpty(sfile))
                        {
                            SavePrefer(BoostTarget.Discord, sid);
                            RememberDpiStrategy(sid, sfile, stitle ?? sid);
                            Log($"Discord watchdog OK sweep: {stitle}");
                            repaired = true;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log("Discord watchdog sweep: " + ex.Message); }
                }

                if (_discordOn)
                {
                    UpdateStatusUi(true);
                    RefreshCards();
                    if (!repaired)
                        Log("Discord watchdog: still soft — will retry");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("Discord watchdog: " + ex.Message); }
    }


    private async Task FailDpiConnectAsync(bool extending, string label)
    {
        Log("Не удалось подключить: " + label);
        if (!_discordOn) _intentDiscord = false;
        SetBusy(null);
        RefreshCards();
        UpdateStatusUi(EngineAlive);
        RefreshHealthSummary();
        StatusText.Text = "Не удалось";
        StatusText.Foreground = new SolidColorBrush(Color.Parse("#F5A524"));
        await Task.CompletedTask;
    }

    /// <summary>
    /// Discord DPI + Media DPI are independent — only refresh Discord Flowseal.
    /// </summary>
    private async Task RefreshDualExclusiveAsync()
    {
        if (!_discordOn || string.IsNullOrEmpty(_dpiStrategyFile))
            return;
        try
        {
            Log("Discord Flowseal refresh (Media untouched)…");
            await _picker.StartDiscordAsync(_dpiStrategyFile, gamesAlsoOn: false);
        }
        catch (Exception ex)
        {
            Log("Dual refresh: " + ex.Message);
        }
    }

    /// <summary>Mark every requested DPI card ON and clear their connect intents.</summary>
    private void CommitDpiIntents(bool wantDiscord, bool wantYoutube, bool wantTwitch)
    {
        if (wantDiscord)
        {
            _discordOn = true;
            _intentDiscord = false;
        }
        if (wantYoutube)
        {
            _youtubeOn = true;
            _intentYoutube = false;
        }
        if (wantTwitch)
        {
            _twitchOn = true;
            _intentTwitch = false;
        }
    }

    private async Task FinishDpiSuccessAsync(
        bool wantDiscord, bool wantYoutube, bool wantTwitch, CancellationToken ct,
        string sid, string sfile, string stitle)
    {
        RememberDpiStrategy(sid, sfile, stitle);
        CommitDpiIntents(wantDiscord, wantYoutube, wantTwitch);
        Log($"OK via {stitle}");
        // Discord finish must not restart Media DPI or a healthy Telegram TUN.
        SetBusy(null);
        UpdateStatusUi(true);
        RefreshCards();
        RefreshHealthSummary();
        await Task.CompletedTask;
    }

    /// <summary>
    /// One HY2 process for YouTube / Twitch / Telegram.
    /// YT+TW → media TUN (per-card domains). TG alone → OnixBoostTG route_address.
    /// </summary>
    private async Task RebuildServiceTunnelAsync(
        CancellationToken ct,
        bool forceYoutube = false,
        bool forceTwitch = false)
    {
        var wantYt = forceYoutube || _youtubeOn;
        var wantTg = _telegramOn;
        var wantTwitch = forceTwitch || _twitchOn;

        // Drop leftover Media winws DPI from 2.8.89 experiments — HY2 owns YT/Twitch now.
        try { await _picker.StopGamesAsync(); } catch { }

        if (!wantYt && !wantTg && !wantTwitch)
        {
            if (_youtubeTunnelOwned || _telegramTunnelOwned || _twitchTunnelOwned || _tunnel.IsRunning)
            {
                try { await _tunnel.StopAsync(); } catch { }
                _youtubeTunnelOwned = false;
                _telegramTunnelOwned = false;
                _twitchTunnelOwned = false;
            }
            return;
        }

        ct.ThrowIfCancellationRequested();

        // Avoid bouncing a healthy TG-only tunnel when nothing media-related changed.
        if (!wantYt && !wantTwitch && wantTg &&
            _tunnel.IsRunning && _tunnel.IsTelegramOnly)
        {
            _telegramTunnelOwned = true;
            _youtubeTunnelOwned = false;
            _twitchTunnelOwned = false;
            return;
        }

        Log($"Tunnel: YT={wantYt} TG={wantTg} Twitch={wantTwitch} discordExcl={_discordOn}");
        await _tunnel.StartServiceTunnelAsync(wantYt, wantTg, wantTwitch, excludeDiscordCidrs: _discordOn);
        _youtubeTunnelOwned = wantYt;
        _twitchTunnelOwned = wantTwitch;
        _telegramTunnelOwned = wantTg;
        await Task.Delay(500, ct);
    }

    private async Task RebuildTelegramTunnelAsync(CancellationToken ct, bool force = false)
    {
        if (force)
            _telegramOn = true;
        await RebuildServiceTunnelAsync(ct);
    }

    private async Task ToggleYoutubeTunnelAsync()
    {
        if (_youtubeConnecting)
        {
            CancelYtOp();
            return;
        }

        if (_youtubeOn)
        {
            _youtubeOn = false;
            _youtubeTunnelOwned = false;
            RefreshCards();
            try { await RebuildServiceTunnelAsync(CancellationToken.None); } catch { }
            UpdateStatusUi(EngineAlive);
            RefreshCards();
            RefreshHealthSummary();
            return;
        }

        _youtubeConnecting = true;
        RefreshCards();
        SetStatusProgress("Подключение", "#4DA3FF");
        await Task.Yield();
        var (ct, gen) = BeginYtOp();
        try
        {
            if (_discordOn)
            {
                try { _ = DiscordIpSync.SyncAsync(force: false, ct); } catch { }
            }

            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RebuildServiceTunnelAsync(ct, forceYoutube: true);
                    if (_tunnel.IsRunning) { last = null; break; }
                    last = new InvalidOperationException("туннель не стартовал");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    Log($"YouTube tunnel try {attempt}/3: {ex.Message}");
                }
                await Task.Delay(400 * attempt, ct);
            }
            if (last != null || !_tunnel.IsRunning)
                throw last ?? new InvalidOperationException("туннель не стартовал");

            await Task.Delay(500, ct);
            var probe = await _health.CheckTargetAsync(BoostTarget.Youtube, ct, fastFail: true);
            Log(probe.Ok
                ? $"YouTube OK ({probe.Detail}, {probe.Ms} ms)"
                : $"YouTube tunnel up · probe soft: {probe.Detail}");

            _youtubeOn = true;
            _youtubeTunnelOwned = true;
            _youtubeConnecting = false;

            if (_discordOn)
                _ = SoftRepairDiscordAfterTunnelAsync(CancellationToken.None);

            UpdateStatusUi(true);
            RefreshCards();
            RefreshHealthSummary();
        }
        catch (OperationCanceledException)
        {
            if (!IsCurrentYtOp(gen)) return;
            _youtubeConnecting = false;
            if (!_youtubeOn)
            {
                try { await RebuildServiceTunnelAsync(CancellationToken.None); } catch { }
            }
            RefreshCards();
            StatusText.Text = "Таймаут YouTube";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#F5A524"));
        }
        catch (Exception ex)
        {
            if (!IsCurrentYtOp(gen)) return;
            _youtubeConnecting = false;
            _youtubeOn = false;
            _youtubeTunnelOwned = false;
            try { await RebuildServiceTunnelAsync(CancellationToken.None); } catch { }
            if (_discordOn)
                _ = SoftRepairDiscordAfterTunnelAsync(CancellationToken.None);
            RefreshCards();
            Log("YouTube ERROR: " + ex.Message);
            var tip = ex.Message.Replace("\u001b", "");
            if (tip.Length > 48) tip = tip[..48] + "…";
            StatusText.Text = "YouTube: " + tip;
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#FF5A64"));
        }
    }

    /// <summary>After HY2 media TUN comes up, re-check Discord and restart Discord winws only.</summary>
    private async Task SoftRepairDiscordAfterTunnelAsync(CancellationToken ct)
    {
        if (!_discordOn || string.IsNullOrEmpty(_dpiStrategyFile)) return;
        try
        {
            await Task.Delay(400, ct);
            var d = await _health.CheckTargetAsync(BoostTarget.Discord, ct, fastFail: true);
            if (d.Ok)
            {
                Log($"Discord after YT tunnel OK ({d.Ms} ms)");
                return;
            }
            Log($"Discord after YT soft — restart Discord winws: {d.Detail}");
            await _picker.StartDiscordAsync(_dpiStrategyFile, gamesAlsoOn: false);
            await Task.Delay(500, ct);
            d = await _health.CheckTargetAsync(BoostTarget.Discord, ct, fastFail: false);
            Log(d.Ok
                ? $"Discord repaired ({d.Ms} ms)"
                : $"Discord still down: {d.Detail}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("Discord repair: " + ex.Message);
        }
    }

    /// <summary>
    /// Discord and YouTube must really pass when requested (no fake ОНЛАЙН).
    /// Games stays soft — Steam/Epic/Riot probes are unreliable.
    /// </summary>
    private async Task<bool> AcceptComboAsync(
        bool wantDiscord, bool wantYoutube, bool wantTwitch, CancellationToken ct,
        int retries = 0)
    {
        if (wantTwitch && !wantDiscord && !wantYoutube)
            return true;

        for (var attempt = 0; attempt <= retries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(350, ct);

            Task<(bool Ok, long Ms, string Detail)>? dTask = null;
            Task<(bool Ok, long Ms, string Detail)>? yTask = null;
            // Last attempt: full probe (once). Sweep already used fastFail — don't stack slow confirms.
            var fast = attempt < retries;
            if (wantDiscord) dTask = _health.CheckTargetAsync(BoostTarget.Discord, ct, fastFail: fast);
            if (wantYoutube) yTask = _health.CheckTargetAsync(BoostTarget.Youtube, ct, fastFail: fast);
            if (dTask != null && yTask != null) await Task.WhenAll(dTask, yTask);
            else if (dTask != null) await dTask;
            else if (yTask != null) await yTask;

            var dOk = true;
            var yOk = true;
            if (dTask != null)
            {
                var d = await dTask;
                dOk = d.Ok;
                if (!dOk && attempt == retries) Log($"Discord fail: {d.Detail}");
            }
            if (yTask != null)
            {
                var y = await yTask;
                yOk = y.Ok;
                if (!yOk && attempt == retries) Log($"YouTube fail: {y.Detail}");
            }
            if (dOk && yOk)
            {
                if (wantTwitch)
                {
                    var g = await _health.CheckTargetAsync(BoostTarget.Twitch, ct, fastFail: true);
                    if (!g.Ok) Log($"Games soft: {g.Detail}");
                }
                return wantDiscord || wantYoutube || wantTwitch;
            }
        }
        return false;
    }

    /// <summary>Telegram HY2 OnixBoostTG — independent of Discord/Media DPI (own CTS).</summary>
    private async void OnBoostTelegram(object? sender, RoutedEventArgs e)
    {
        if (_telegramConnecting)
        {
            CancelTgOp();
            return;
        }

        if (_telegramOn)
        {
            _telegramOn = false;
            RefreshCards();
            await TurnOffTelegramAsync(alreadyCleared: true);
            return;
        }

        _telegramConnecting = true;
        RefreshCards();
        SetStatusProgress("Подключение", "#4DA3FF");
        await Task.Yield();
        var (ct, gen) = BeginTgOp();
        try
        {
            for (var round = 1; round <= 6; round++)
            {
                ct.ThrowIfCancellationRequested();

                _telegramOn = true;
                try
                {
                    await RebuildTelegramTunnelAsync(ct, force: true);
                }
                catch
                {
                    _telegramOn = false;
                    throw;
                }

                if (!_tunnel.IsRunning)
                {
                    _telegramOn = false;
                    Log($"Telegram tunnel down (round {round})");
                    StatusText.Text = $"Telegram… {round}";
                    StatusText.Foreground = new SolidColorBrush(Color.Parse("#F5A524"));
                    await Task.Delay(1000, ct);
                    continue;
                }

                var probe = await _health.CheckTelegramStrictAsync(ct);
                Log(probe.Ok
                    ? $"Telegram OK ({probe.Detail}, {probe.Ms} ms)"
                    : $"Telegram tunnel up · probe soft: {probe.Detail}");

                if (_discordOn)
                {
                    var d = await _health.CheckTargetAsync(BoostTarget.Discord, ct, fastFail: true);
                    if (!d.Ok)
                        Log($"Discord after TG: {d.Detail}");
                }

                MarkTelegramOnline(owned: _telegramTunnelOwned);
                return;
            }

            _telegramOn = false;
            _telegramConnecting = false;
            try { await RebuildTelegramTunnelAsync(CancellationToken.None); } catch { }
            RefreshCards();
            StatusText.Text = "Telegram не удалось";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#FF5A64"));
        }
        catch (OperationCanceledException)
        {
            if (!IsCurrentTgOp(gen)) return;
            _telegramConnecting = false;
            if (!_telegramOn)
            {
                try { await RebuildTelegramTunnelAsync(CancellationToken.None); } catch { }
            }
            RefreshCards();
        }
        catch (Exception ex)
        {
            if (!IsCurrentTgOp(gen)) return;
            _telegramConnecting = false;
            _telegramOn = false;
            try { await RebuildTelegramTunnelAsync(CancellationToken.None); } catch { }
            RefreshCards();
            Log("ERROR: " + ex.Message);
            StatusText.Text = "Telegram не удалось";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#FF5A64"));
        }
    }

    private void MarkTelegramOnline(bool owned)
    {
        _telegramOn = true;
        _telegramTunnelOwned = owned;
        _telegramConnecting = false;
        UpdateStatusUi(true);
        RefreshCards();
        RefreshHealthSummary();
    }

    private async Task EnsureGamesTunnelIfNeededAsync(CancellationToken ct) =>
        await RebuildServiceTunnelAsync(ct);

    private async Task EnsureTelegramTunnelIfNeededAsync(CancellationToken ct) =>
        await RebuildServiceTunnelAsync(ct);

    private async Task TurnOffTelegramAsync(bool alreadyCleared = false)
    {
        if (!alreadyCleared)
            _telegramOn = false;
        _telegramTunnelOwned = false;
        try { await RebuildServiceTunnelAsync(CancellationToken.None); } catch { }

        UpdateStatusUi(EngineAlive);
        RefreshCards();
        RefreshHealthSummary();
    }

    private void OnOpenOnixV(object? sender, RoutedEventArgs e)
    {
        // 1) Telegram desktop (works with TG boost via process route)
        // 2) https://t.me — also routed via HY2 when TG card is ON
        const string deep = "tg://resolve?domain=Onix_Vbot&start=onixboost";
        const string https = "https://t.me/Onix_Vbot?start=onixboost";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = deep, UseShellExecute = true });
            Log("OnixV → Telegram app @Onix_Vbot");
            return;
        }
        catch { /* fall through */ }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = https, UseShellExecute = true });
            Log("OnixV → " + https);
        }
        catch (Exception ex)
        {
            Log("OnixV link: " + ex.Message);
            StatusText.Text = "Откройте @Onix_Vbot в Telegram";
            StatusText.Foreground = new SolidColorBrush(Color.Parse("#4DA3FF"));
        }
    }

    private async void OnBoostDiscord(object? sender, RoutedEventArgs e) => await ToggleOrBoostAsync(BoostTarget.Discord);
    private async void OnBoostYoutube(object? sender, RoutedEventArgs e) => await ToggleYoutubeTunnelAsync();
    private async void OnBoostTwitch(object? sender, RoutedEventArgs e) => await ToggleOrBoostAsync(BoostTarget.Twitch);

    private async void OnStop(object? sender, RoutedEventArgs e)
    {
        try
        {
            CancelCurrentOp();
            CancelTwitchOp();
            CancelTgOp();
            CancelYtOp();
            Interlocked.Increment(ref _opGen);
            Interlocked.Increment(ref _twitchGen);
            Interlocked.Increment(ref _tgGen);
            Interlocked.Increment(ref _ytGen);
            await StopAllAsync();
            SetBusy(null);
            ResetAllBoostState();
            UpdateStatusUi(false);
            Log("Stopped.");
        }
        catch (Exception ex)
        {
            Log("STOP ERROR: " + ex.Message);
        }
    }

    private static void TryClearDiscordCache()
    {
        try
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var sub in new[] { @"discord\Cache", @"discord\Code Cache", @"discord\GPUCache" })
            {
                var p = Path.Combine(appdata, sub);
                if (Directory.Exists(p))
                    Directory.Delete(p, true);
            }
        }
        catch { }
    }

    private async Task MessageBox(string text)
    {
        var w = new Window
        {
            Title = "OnixBoost",
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Icon,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0,0,0,16) },
                    new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };
        if (w.Content is StackPanel sp && sp.Children[1] is Button b)
            b.Click += (_, _) => w.Close();
        await w.ShowDialog(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Minimize to tray instead of exit (exit only from tray menu)
        if (!App.ExitRequested)
        {
            e.Cancel = true;
            Hide();
            Log("Свёрнуто в трей (возле часов). Выход — ПКМ по иконке.");
            return;
        }

        // Tray «Выход» already cleans up off-UI and Environment.Exit — never Wait() on UI thread
        // (that froze the tray menu). Quick kill only; DNS/WinDivert may already be restoring.
        try { _updatePollCts?.Cancel(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _twitchCts?.Cancel(); } catch { }
        try { _tgCts?.Cancel(); } catch { }
        try { ProcessNames.KillAllWorkers(); } catch { }
        base.OnClosing(e);
    }
}
