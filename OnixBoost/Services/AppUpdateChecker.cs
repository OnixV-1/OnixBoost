using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

public sealed record UpdateInfo(
    string Version,
    string SetupUrl,
    string? ZipUrl,
    string? Notes);

/// <summary>
/// In-app update: download Setup → schedule install AFTER this process exits → quit.
/// Never runs Setup while OnixBoost.exe still holds Program Files locks.
/// Tries several download mirrors; GitHub is always a fallback.
/// </summary>
public static class AppUpdateChecker
{
    public const string RepoOwner = "OnixV-1";
    public const string RepoName = "OnixBoost";
    public const string ReleasesLatest = "https://github.com/OnixV-1/OnixBoost/releases/latest";
    /// <summary>Private NL mirror omitted from public source; updates use GitHub.</summary>
    public const string NlBase = "";
    private const string NlManifestUrl = GhManifestUrl;
    private const string ApiLatest = "https://api.github.com/repos/OnixV-1/OnixBoost/releases/latest";
    private const string GhManifestUrl = "https://raw.githubusercontent.com/OnixV-1/OnixBoost/main/version.json";
    private const string SetupAssetName = "OnixBoost-Setup.exe";
    private const string ZipAssetName = "OnixBoost-Update.zip";
    private static readonly TimeSpan PerUrlTimeout = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = CreateHttp();

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.0.0";
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string UpdateLogPath => Path.Combine(AppPaths.Root, "update.log");

    public static void CleanupAfterUpdate()
    {
        try
        {
            var old = Path.Combine(AppPaths.Root, "OnixBoost.exe.old");
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }

        try
        {
            var staging = Path.Combine(AppPaths.Root, ".update-staging");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        catch { }

        // leftover *.old from Setup overwrite
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Root, "*.old", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var fromNl = await TryManifestAsync(NlManifestUrl, ct);
            if (fromNl != null && IsNewer(fromNl.Version, CurrentVersion))
            {
                LogLine("update available via NL " + fromNl.Version);
                return fromNl;
            }
        }
        catch (Exception ex) { LogLine("check NL: " + ex.Message); }

        try
        {
            var fromApi = await TryGitHubReleaseAsync(ct);
            if (fromApi != null && IsNewer(fromApi.Version, CurrentVersion))
                return fromApi;
        }
        catch (Exception ex) { LogLine("check github: " + ex.Message); }

        try
        {
            var fromManifest = await TryManifestAsync(GhManifestUrl, ct);
            if (fromManifest != null && IsNewer(fromManifest.Version, CurrentVersion))
                return fromManifest;
        }
        catch (Exception ex) { LogLine("check gh manifest: " + ex.Message); }

        return null;
    }

    public static async Task ApplyUpdateAsync(
        UpdateInfo info,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        LogLine($"apply start cur={CurrentVersion} dst={info.Version} setup={info.SetupUrl} zip={info.ZipUrl}");
        progress?.Report("Скачивание…");

        var setupPath = Path.Combine(Path.GetTempPath(), SetupAssetName);
        Exception? last = null;

        foreach (var url in SetupUrlCandidates(info))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(LabelForUrl(url));
                await DownloadFileAsync(url, setupPath, progress, ct);
                var len = new FileInfo(setupPath).Length;
                LogLine($"setup ok bytes={len} from={HostOf(url)}");
                if (len < 5_000_000)
                    throw new InvalidOperationException($"Setup битый ({len} байт)");

                TryUnblockFile(setupPath);
                progress?.Report("Подготовка…");
                await Task.Yield();
                await Task.Run(() => ScheduleSetupAfterExit(setupPath), ct);
                LogLine("scheduled setup after exit");
                progress?.Report("Готово — закрытие…");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                LogLine($"setup download/schedule fail host={HostOf(url)}: {ex.Message}");
                try { if (File.Exists(setupPath)) File.Delete(setupPath); } catch { }
            }
        }

        // Last resort: zip staging (also multi-URL), still applied after exit
        progress?.Report("Запасной вариант…");
        foreach (var zipUrl in ZipUrlCandidates(info))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ApplyZipViaStagingAsync(zipUrl, progress, ct);
                LogLine("zip staging scheduled from " + HostOf(zipUrl));
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                LogLine("zip fail host=" + HostOf(zipUrl) + ": " + ex.Message);
            }
        }

        throw new InvalidOperationException(
            "Не удалось скачать обновление. Откройте Releases на GitHub и поставьте Setup вручную.",
            last);
    }

    /// <summary>
    /// Detached watcher: wait until OnixBoost.exe is gone, then run Setup /silent.
    /// Must be started BEFORE Shutdown() so it inherits admin token.
    /// </summary>
    private static void ScheduleSetupAfterExit(string setupPath)
    {
        var bat = Path.Combine(Path.GetTempPath(), "onixboost-run-setup.cmd");
        var log = Path.Combine(Path.GetTempPath(), "onixboost-update.log");
        var pid = Environment.ProcessId;
        static string Q(string p) => "\"" + p.Replace("\"", "") + "\"";

        // Wait by PID only — dpi/core workers are also named OnixBoost.exe and must not block Setup
        File.WriteAllText(bat, $"""
            @echo off
            setlocal EnableExtensions
            echo %date% %time% wait pid={pid} exit>>{Q(log)}
            :wait
            timeout /t 1 /nobreak >nul
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
            if not errorlevel 1 goto wait
            timeout /t 2 /nobreak >nul
            echo %date% %time% starting setup>>{Q(log)}
            start /wait "" {Q(setupPath)} /silent
            set EC=%ERRORLEVEL%
            echo %date% %time% setup exit=%EC%>>{Q(log)}
            if not "%EC%"=="0" (
              echo Setup failed code %EC%>>{Q(log)}
            )
            del /F /Q "%~f0" >nul 2>&1
            """, Encoding.ASCII);

        // Detach watcher: `start` returns immediately so we never block on the wait-loop bat.
        // Child still inherits admin token from elevated OnixBoost (no second UAC).
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c start \"onix-upd\" /min cmd.exe /c " + Q(bat),
            WorkingDirectory = Path.GetTempPath(),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        var p = Process.Start(psi);
        if (p == null)
            throw new InvalidOperationException("Не удалось запланировать установку");
        try { p.WaitForExit(3000); } catch { }
        LogLine($"watcher launched setup={setupPath}");
    }

    private static async Task ApplyZipViaStagingAsync(string zipUrl, IProgress<string>? progress, CancellationToken ct)
    {
        var tmpZip = Path.Combine(Path.GetTempPath(), ZipAssetName);
        var staging = Path.Combine(AppPaths.Root, ".update-staging");
        var root = AppPaths.Root;
        var exePath = Path.Combine(root, "OnixBoost.exe");

        progress?.Report("Скачивание…");
        await DownloadFileAsync(zipUrl, tmpZip, progress, ct);
        var zipLen = new FileInfo(tmpZip).Length;
        LogLine($"zip downloaded bytes={zipLen}");
        if (zipLen < 1_000_000)
            throw new InvalidOperationException($"Zip битый ({zipLen} байт)");

        progress?.Report("Распаковка…");
        try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(tmpZip, staging, overwriteFiles: true);
        if (!File.Exists(Path.Combine(staging, "OnixBoost.exe")))
            throw new InvalidOperationException("В zip нет OnixBoost.exe");

        progress?.Report("Подготовка…");
        await Task.Yield();
        await Task.Run(() =>
        {
            try { ProcessNames.KillAllWorkers(); } catch { }
            ScheduleStagingSwap(root, staging, exePath);
            try { File.Delete(tmpZip); } catch { }
        }, ct);
        progress?.Report("Готово — закрытие…");
    }

    private static void ScheduleStagingSwap(string root, string staging, string exePath)
    {
        var bat = Path.Combine(Path.GetTempPath(), "onixboost-apply-update.cmd");
        static string Q(string p) => "\"" + p.Replace("\"", "") + "\"";
        var pid = Environment.ProcessId;
        File.WriteAllText(bat, $"""
            @echo off
            setlocal EnableExtensions
            set "ROOT={root.TrimEnd('\\')}"
            set "STAGING={staging.TrimEnd('\\')}"
            set "EXE={exePath}"
            :wait
            timeout /t 1 /nobreak >nul
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
            if not errorlevel 1 goto wait
            timeout /t 2 /nobreak >nul
            robocopy "%STAGING%" "%ROOT%" /E /IS /IT /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul
            if exist "%ROOT%\.update-staging" rmdir /S /Q "%ROOT%\.update-staging" >nul 2>&1
            start "" "%EXE%"
            del /F /Q "%~f0" >nul 2>&1
            """);
        // Detach: outer cmd exits immediately; watcher keeps running
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c start \"onix-upd\" /min cmd.exe /c " + Q(bat),
            WorkingDirectory = root,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (Process.Start(psi) == null)
            throw new InvalidOperationException("Не удалось запустить скрипт обновления");
    }

    private static async Task DownloadFileAsync(string url, string dest, IProgress<string>? progress, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (attempt > 1)
                    progress?.Report("Повтор…");
                LogLine($"download try={attempt} host={HostOf(url)}");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(PerUrlTimeout);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/octet-stream,*/*");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                LogLine($"http {(int)resp.StatusCode} len={resp.Content.Headers.ContentLength} host={HostOf(url)}");
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(linked.Token);
                await using var dst = File.Create(dest);
                var buf = new byte[256 * 1024];
                long read = 0;
                int n;
                var lastPct = -1;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), linked.Token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), linked.Token);
                    read += n;
                    if (total > 0)
                    {
                        var pct = (int)(read * 100 / total);
                        if (pct != lastPct && (pct % 2 == 0 || pct >= 99))
                        {
                            lastPct = pct;
                            progress?.Report($"Скачивание {pct}%");
                        }
                    }
                    else if (read % (8 * 1024 * 1024) < buf.Length)
                        progress?.Report($"Скачивание {read / (1024 * 1024)} МБ");
                }
                await dst.FlushAsync(linked.Token);
                progress?.Report("Скачивание 100%");
                if (read < 1_000_000)
                    throw new InvalidOperationException($"Скачано только {read} байт");
                return;
            }
            catch (Exception ex) when (attempt < 2 && ex is not OperationCanceledException)
            {
                last = ex;
                LogLine($"download fail try={attempt}: {ex.Message}");
                await Task.Delay(800 * attempt, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException("Таймаут скачивания (" + PerUrlTimeout.TotalMinutes + " мин)");
                LogLine(last.Message + " host=" + HostOf(url));
                break; // next URL
            }
        }
        throw last ?? new InvalidOperationException("Скачивание не удалось");
    }

    private static IEnumerable<string> SetupUrlCandidates(UpdateInfo info)
    {
        var list = new List<string>();
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            if (list.Exists(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(u);
        }
        Add(info.SetupUrl);
        if (!string.IsNullOrWhiteSpace(NlBase))
            Add(NlBase.TrimEnd('/') + "/" + SetupAssetName);
        Add($"https://github.com/{RepoOwner}/{RepoName}/releases/download/v{Normalize(info.Version)}/{SetupAssetName}");
        Add($"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/{SetupAssetName}");
        return list;
    }

    private static IEnumerable<string> ZipUrlCandidates(UpdateInfo info)
    {
        var list = new List<string>();
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            if (list.Exists(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(u);
        }
        Add(info.ZipUrl);
        if (!string.IsNullOrWhiteSpace(NlBase))
            Add(NlBase.TrimEnd('/') + "/" + ZipAssetName);
        Add($"https://github.com/{RepoOwner}/{RepoName}/releases/download/v{Normalize(info.Version)}/{ZipAssetName}");
        Add($"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/{ZipAssetName}");
        return list;
    }

    private static string LabelForUrl(string url)
    {
        if (url.Contains("github", StringComparison.OrdinalIgnoreCase))
            return "Скачивание (GitHub)…";
        return "Скачивание…";
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return "?"; }
    }

    private static void TryUnblockFile(string path)
    {
        try
        {
            var zone = path + ":Zone.Identifier";
            if (File.Exists(zone)) File.Delete(zone);
        }
        catch { }
    }

    public static bool IsNewer(string remote, string local)
    {
        if (!TryParse(remote, out var r) || !TryParse(local, out var l))
            return !string.Equals(Normalize(remote), Normalize(local), StringComparison.OrdinalIgnoreCase);
        return r.CompareTo(l) > 0;
    }

    public static void OpenReleasesInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ReleasesLatest, UseShellExecute = true });
        }
        catch { }
    }

    private static async Task<UpdateInfo?> TryGitHubReleaseAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiLatest);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var ver = Normalize(tag);
        string? setupUrl = null;
        string? zipUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (name.Equals(ZipAssetName, StringComparison.OrdinalIgnoreCase))
                    zipUrl = url;
                else if (name.Equals(SetupAssetName, StringComparison.OrdinalIgnoreCase))
                    setupUrl = url;
            }
        }
        setupUrl ??= $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/{SetupAssetName}";
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
        return new UpdateInfo(ver, setupUrl, zipUrl, notes);
    }

    private static async Task<UpdateInfo?> TryManifestAsync(string manifestUrl, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(manifestUrl, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var ver = Normalize(doc.RootElement.GetProperty("version").GetString() ?? "");
        var setupUrl = doc.RootElement.TryGetProperty("url", out var u)
            ? u.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(setupUrl) && !string.IsNullOrWhiteSpace(NlBase))
            setupUrl = NlBase.TrimEnd('/') + "/" + SetupAssetName;
        setupUrl ??= $"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/{SetupAssetName}";
        string? zipUrl = null;
        if (doc.RootElement.TryGetProperty("zip", out var z))
            zipUrl = z.GetString();
        if (string.IsNullOrWhiteSpace(zipUrl) && !string.IsNullOrWhiteSpace(NlBase))
            zipUrl = NlBase.TrimEnd('/') + "/" + ZipAssetName;
        zipUrl ??= $"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/{ZipAssetName}";
        var notes = doc.RootElement.TryGetProperty("notes", out var n) ? n.GetString() : null;
        return new UpdateInfo(ver, setupUrl!, zipUrl, notes);
    }

    private static void LogLine(string msg)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}";
            File.AppendAllText(UpdateLogPath, line, Encoding.UTF8);
            Logger.Info("update: " + msg);
        }
        catch { }
    }

    private static string Normalize(string v)
    {
        v = v.Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            v = v[1..];
        return v;
    }

    private static bool TryParse(string s, out Version ver)
    {
        s = Normalize(s);
        return Version.TryParse(s, out ver!);
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 15,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // per-URL CTS
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OnixBoost-Updater/2.8");
        return c;
    }
}
