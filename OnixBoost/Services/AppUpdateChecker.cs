using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    string? Notes,
    IReadOnlyList<string>? Mirrors = null);

/// <summary>
/// In-app update: multi-mirror download → install after exit.
/// Prefer ISP-friendly CDN (HTTP :80), then :8891, then GitHub (+ public proxies).
/// Users should never need to open a browser.
/// </summary>
public static class AppUpdateChecker
{
    public const string RepoOwner = "OnixV-1";
    public const string RepoName = "OnixBoost";
    public const string ReleasesLatest = "https://github.com/OnixV-1/OnixBoost/releases/latest";

    /// <summary>Primary CDN — port 80 path (most ISPs allow).</summary>
    public const string NlHttp80 = "http://85.137.63.156/onixboost";
    /// <summary>Legacy CDN port.</summary>
    public const string NlHttp8891 = "http://85.137.63.156:8891";

    private static readonly string[] BuiltInMirrors =
    {
        NlHttp80,
        NlHttp8891,
    };

    private const string NlManifestUrl = NlHttp80 + "/version.json";
    private const string NlManifestUrlLegacy = NlHttp8891 + "/version.json";
    private const string ApiLatest = "https://api.github.com/repos/OnixV-1/OnixBoost/releases/latest";
    private const string GhManifestUrl = "https://raw.githubusercontent.com/OnixV-1/OnixBoost/main/version.json";
    private const string SetupAssetName = "OnixBoost-Setup.exe";
    private const string ZipAssetName = "OnixBoost-Update.zip";
    private static readonly TimeSpan PerUrlTimeout = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan HeadProbeTimeout = TimeSpan.FromSeconds(6);

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
        foreach (var manifest in new[] { NlManifestUrl, NlManifestUrlLegacy })
        {
            try
            {
                var fromNl = await TryManifestAsync(manifest, ct);
                if (fromNl != null && IsNewer(fromNl.Version, CurrentVersion))
                {
                    LogLine("update available via " + HostOf(manifest) + " " + fromNl.Version);
                    return fromNl;
                }
            }
            catch (Exception ex) { LogLine("check NL " + HostOf(manifest) + ": " + ex.Message); }
        }

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

        Exception? last = null;
        // Two full passes over all mirrors — covers flaky mobile/ISP drops
        for (var pass = 1; pass <= 2; pass++)
        {
            ct.ThrowIfCancellationRequested();
            if (pass > 1)
            {
                progress?.Report("Повтор скачивания…");
                LogLine("apply pass " + pass);
                await Task.Delay(1200, ct);
            }

            // Prefer Update.zip (≈80 MB) before Setup (≈150 MB) — less to pull on bad links
            foreach (var zipUrl in await OrderUrlsAsync(ZipUrlCandidates(info), ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(LabelForUrl(zipUrl));
                    await ApplyZipViaStagingAsync(zipUrl, progress, ct);
                    LogLine("zip staging scheduled from " + HostOf(zipUrl));
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    LogLine("zip fail host=" + HostOf(zipUrl) + ": " + ex.Message);
                    progress?.Report("Другое зеркало…");
                }
            }

            var setupPath = Path.Combine(Path.GetTempPath(), SetupAssetName);
            foreach (var url in await OrderUrlsAsync(SetupUrlCandidates(info), ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(LabelForUrl(url));
                    await DownloadFileAsync(url, setupPath, progress, ct, minBytes: 5_000_000);
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
                    LogLine($"setup fail host={HostOf(url)}: {ex.Message}");
                    try { if (File.Exists(setupPath)) File.Delete(setupPath); } catch { }
                    progress?.Report("Другое зеркало…");
                }
            }
        }

        throw new InvalidOperationException(
            "Не удалось скачать обновление. Нажмите «Обновить» ещё раз — приложение само переберёт зеркала.",
            last);
    }

    private static void ScheduleSetupAfterExit(string setupPath)
    {
        var bat = Path.Combine(Path.GetTempPath(), "onixboost-run-setup.cmd");
        var log = Path.Combine(Path.GetTempPath(), "onixboost-update.log");
        var pid = Environment.ProcessId;
        static string Q(string p) => "\"" + p.Replace("\"", "") + "\"";

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
        await DownloadFileAsync(zipUrl, tmpZip, progress, ct, minBytes: 1_000_000);
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

    private static async Task DownloadFileAsync(
        string url, string dest, IProgress<string>? progress, CancellationToken ct, long minBytes)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (attempt > 1)
                    progress?.Report($"Повтор {attempt}/3…");
                LogLine($"download try={attempt} host={HostOf(url)}");

                long existing = 0;
                if (File.Exists(dest))
                {
                    try { existing = new FileInfo(dest).Length; } catch { existing = 0; }
                    if (existing > 0 && existing < minBytes)
                        LogLine($"resume from {existing} bytes");
                    else if (existing >= minBytes)
                    {
                        // leftover complete-looking file — start fresh
                        try { File.Delete(dest); } catch { }
                        existing = 0;
                    }
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(PerUrlTimeout);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/octet-stream,*/*");
                if (existing > 0)
                    req.Headers.Range = new RangeHeaderValue(existing, null);

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                LogLine($"http {(int)resp.StatusCode} len={resp.Content.Headers.ContentLength} host={HostOf(url)} resume={existing}");

                var partial = (int)resp.StatusCode == 206;
                if ((int)resp.StatusCode == 416)
                {
                    try { File.Delete(dest); } catch { }
                    existing = 0;
                    throw new InvalidOperationException("Range not satisfiable — restart");
                }

                if (!partial && existing > 0 && resp.IsSuccessStatusCode)
                {
                    // Server ignored Range — rewrite from scratch
                    existing = 0;
                    try { File.Delete(dest); } catch { }
                }

                resp.EnsureSuccessStatusCode();

                var contentLen = resp.Content.Headers.ContentLength ?? -1;
                var total = partial && contentLen >= 0 ? existing + contentLen
                    : contentLen >= 0 ? contentLen
                    : -1;

                await using var src = await resp.Content.ReadAsStreamAsync(linked.Token);
                await using var dst = new FileStream(
                    dest,
                    existing > 0 && partial ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    256 * 1024,
                    useAsync: true);

                var buf = new byte[256 * 1024];
                long read = existing;
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
                if (read < minBytes)
                    throw new InvalidOperationException($"Скачано только {read} байт");
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is not OperationCanceledException)
            {
                last = ex;
                LogLine($"download fail try={attempt}: {ex.Message}");
                await Task.Delay(700 * attempt, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException("Таймаут скачивания (" + PerUrlTimeout.TotalMinutes + " мин)");
                LogLine(last.Message + " host=" + HostOf(url));
                break;
            }
        }
        throw last ?? new InvalidOperationException("Скачивание не удалось");
    }

    /// <summary>HEAD-probe mirrors; reachable ones first (by latency).</summary>
    private static async Task<List<string>> OrderUrlsAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var list = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count <= 1) return list;

        var scored = new List<(string url, long ms, bool ok)>();
        foreach (var url in list)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(HeadProbeTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                sw.Stop();
                var ok = resp.IsSuccessStatusCode || (int)resp.StatusCode is 405 or 501;
                // Some CDNs reject HEAD — treat as soft-ok if not 4xx/5xx connection fail
                if (!ok && (int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500)
                    ok = false;
                scored.Add((url, sw.ElapsedMilliseconds, ok || (int)resp.StatusCode == 200));
                LogLine($"probe {(ok ? "ok" : "soft")} {HostOf(url)} {sw.ElapsedMilliseconds}ms {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                scored.Add((url, 60_000 + sw.ElapsedMilliseconds, false));
                LogLine($"probe fail {HostOf(url)}: {ex.GetType().Name}");
            }
        }

        return scored
            .OrderByDescending(x => x.ok)
            .ThenBy(x => x.ms)
            .Select(x => x.url)
            .ToList();
    }

    private static IEnumerable<string> SetupUrlCandidates(UpdateInfo info)
    {
        var list = new List<string>();
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            if (list.Exists(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(u!);
        }

        Add(info.SetupUrl);
        foreach (var m in MirrorBases(info))
            Add(m.TrimEnd('/') + "/" + SetupAssetName);

        var ver = Normalize(info.Version);
        var gh = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/v{ver}/{SetupAssetName}";
        Add(gh);
        Add($"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/{SetupAssetName}");
        // Public GitHub download proxies (help when github.com is throttled)
        Add("https://ghfast.top/" + gh);
        Add("https://mirror.ghproxy.com/" + gh);
        return list;
    }

    private static IEnumerable<string> ZipUrlCandidates(UpdateInfo info)
    {
        var list = new List<string>();
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            if (list.Exists(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(u!);
        }

        Add(info.ZipUrl);
        foreach (var m in MirrorBases(info))
            Add(m.TrimEnd('/') + "/" + ZipAssetName);

        var ver = Normalize(info.Version);
        var gh = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/v{ver}/{ZipAssetName}";
        Add(gh);
        Add($"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/{ZipAssetName}");
        Add("https://ghfast.top/" + gh);
        Add("https://mirror.ghproxy.com/" + gh);
        return list;
    }

    private static IEnumerable<string> MirrorBases(UpdateInfo info)
    {
        var set = new List<string>();
        void Add(string? m)
        {
            if (string.IsNullOrWhiteSpace(m)) return;
            m = m.Trim().TrimEnd('/');
            if (set.Exists(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase))) return;
            set.Add(m);
        }

        if (info.Mirrors != null)
            foreach (var m in info.Mirrors) Add(m);
        foreach (var m in BuiltInMirrors) Add(m);
        return set;
    }

    private static string LabelForUrl(string url)
    {
        if (url.Contains("github", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("ghproxy", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("ghfast", StringComparison.OrdinalIgnoreCase))
            return "Скачивание (зеркало)…";
        if (url.Contains(":8891", StringComparison.Ordinal))
            return "Скачивание…";
        return "Скачивание…";
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host + (new Uri(url).IsDefaultPort ? "" : ":" + new Uri(url).Port); }
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
        return new UpdateInfo(ver, setupUrl, zipUrl, notes, BuiltInMirrors);
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
        setupUrl ??= NlHttp80 + "/" + SetupAssetName;
        string? zipUrl = null;
        if (doc.RootElement.TryGetProperty("zip", out var z))
            zipUrl = z.GetString();
        zipUrl ??= NlHttp80 + "/" + ZipAssetName;
        var notes = doc.RootElement.TryGetProperty("notes", out var n) ? n.GetString() : null;

        var mirrors = new List<string>();
        if (doc.RootElement.TryGetProperty("mirrors", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in mArr.EnumerateArray())
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) mirrors.Add(s!);
            }
        }
        foreach (var b in BuiltInMirrors)
            if (!mirrors.Contains(b, StringComparer.OrdinalIgnoreCase))
                mirrors.Add(b);

        return new UpdateInfo(ver, setupUrl!, zipUrl, notes, mirrors);
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
            ConnectTimeout = TimeSpan.FromSeconds(25)
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OnixBoost-Updater/2.9");
        return c;
    }
}
