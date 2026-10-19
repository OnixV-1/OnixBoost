using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>Downloads fresh winws + lists from Flowseal/zapret-discord-youtube.</summary>
public sealed class BinaryUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static DateTime _lastListsUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string ListsStampPath => Path.Combine(AppPaths.DpiRoot, ".flowseal-lists-stamp");

    public async Task<string> UpdateAsync(IProgress<string>? progress = null)
    {
        var (tag, srcBin, tmpZip, tmpDir) = await DownloadExtractAsync(progress);
        progress?.Report("Releasing locked files...");
        await WinDivertCleanup.RunAsync(progress);
        await ProcessLocker.ReleaseDpiFilesAsync(progress);

        progress?.Report("Installing binaries...");
        Directory.CreateDirectory(AppPaths.BinDir);
        foreach (var f in Directory.GetFiles(srcBin))
        {
            var name = Path.GetFileName(f);
            await ProcessLocker.CopyWithRetryAsync(f, Path.Combine(AppPaths.BinDir, name));
        }

        await CopyListsFromBinParentAsync(srcBin, progress);

        try { File.Delete(tmpZip); } catch { }
        try { Directory.Delete(tmpDir, true); } catch { }
        TouchListsStamp();

        var msg = $"Updated to {tag} (Flowseal)";
        Logger.Info(msg);
        return msg;
    }

    /// <summary>Background-friendly: refresh lists/ only. Caller must ensure DPI is stopped or accept deferred.</summary>
    public async Task<(bool ok, string message)> SyncListsAsync(
        bool force = false,
        CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct))
            return (false, "Flowseal sync busy");

        try
        {
            if (!force && DateTime.UtcNow - _lastListsUtc < TimeSpan.FromHours(8))
                return (true, "Flowseal lists cached");
            if (!force && File.Exists(ListsStampPath))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(ListsStampPath);
                if (age < TimeSpan.FromHours(8))
                {
                    _lastListsUtc = File.GetLastWriteTimeUtc(ListsStampPath);
                    return (true, "Flowseal lists stamp OK");
                }
            }

            ct.ThrowIfCancellationRequested();
            var (tag, srcBin, tmpZip, tmpDir) = await DownloadExtractAsync(null, ct);
            await CopyListsFromBinParentAsync(srcBin, null);
            ApplyOnixListPatches();
            try { File.Delete(tmpZip); } catch { }
            try { Directory.Delete(tmpDir, true); } catch { }
            TouchListsStamp();
            var msg = $"Flowseal lists ← {tag}";
            Logger.Info(msg);
            return (true, msg);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    private void TouchListsStamp()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DpiRoot);
            File.WriteAllText(ListsStampPath, DateTime.UtcNow.ToString("o"));
            _lastListsUtc = DateTime.UtcNow;
        }
        catch { }
    }

    private static async Task CopyListsFromBinParentAsync(string srcBin, IProgress<string>? progress)
    {
        var srcRoot = Directory.GetParent(srcBin)?.FullName;
        if (srcRoot == null) return;
        var lists = Path.Combine(srcRoot, "lists");
        if (!Directory.Exists(lists)) return;

        progress?.Report("Installing lists...");
        var dstLists = Path.Combine(AppPaths.DpiRoot, "lists");
        Directory.CreateDirectory(dstLists);
        foreach (var f in Directory.GetFiles(lists))
        {
            var name = Path.GetFileName(f);
            if (name.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(dstLists, name)) &&
                new FileInfo(Path.Combine(dstLists, name)).Length > 0)
                continue;
            await ProcessLocker.CopyWithRetryAsync(f, Path.Combine(dstLists, name));
        }
        ApplyOnixListPatches();
    }

    /// <summary>
    /// Twitch is its own HY2 card — strip Twitch domains from Discord DPI hostlists
    /// so winws does not fight the Twitch TUN. Discord Soft lists stay Flowseal-vanilla.
    /// </summary>
    public static void ApplyOnixListPatches()
    {
        try
        {
            var lists = AppPaths.ListsDir;
            Directory.CreateDirectory(lists);

            var twitchNeedles = new[]
            {
                "twitch", "twitchcdn", "ext-twitch", "live-video.net",
                "jtvnw", "ttvnw", "ttcdn"
            };

            foreach (var name in new[] { "list-general-user.txt", "list-general.txt" })
            {
                var path = Path.Combine(lists, name);
                if (!File.Exists(path)) continue;
                var kept = File.ReadAllLines(path)
                    .Where(l =>
                    {
                        var s = l.Trim();
                        if (s.Length == 0 || s.StartsWith('#'))
                            return !s.Contains("OnixBoost Twitch", StringComparison.OrdinalIgnoreCase);
                        var low = s.ToLowerInvariant();
                        return !twitchNeedles.Any(n => low.Contains(n, StringComparison.Ordinal));
                    });
                File.WriteAllLines(path, kept);
            }

            var exclude = Path.Combine(lists, "list-exclude.txt");
            if (File.Exists(exclude))
            {
                var kept = File.ReadAllLines(exclude)
                    .Where(l =>
                    {
                        var s = l.Trim();
                        if (s.Length == 0 || s.StartsWith('#')) return true;
                        var low = s.ToLowerInvariant();
                        return !twitchNeedles.Any(n => low.Contains(n, StringComparison.Ordinal));
                    })
                    .ToList();
                File.WriteAllLines(exclude, kept);
            }
        }
        catch (Exception ex)
        {
            Logger.Info("list patch: " + ex.Message);
        }
    }

    private async Task<(string tag, string srcBin, string tmpZip, string tmpDir)> DownloadExtractAsync(
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        progress?.Report("Checking GitHub releases...");
        var api = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        req.Headers.TryAddWithoutValidation("User-Agent", "OnixBoost");
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "?";
        string? url = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                url = a.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        url ??= doc.RootElement.GetProperty("zipball_url").GetString();
        if (url is null) throw new InvalidOperationException("No download URL");

        progress?.Report($"Downloading {tag}...");
        var tmpZip = Path.Combine(Path.GetTempPath(), "onixboost-dpi.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), "onixboost-dpi-extract");
        await using (var fs = File.Create(tmpZip))
        {
            await using var stream = await Http.GetStreamAsync(url, ct);
            await stream.CopyToAsync(fs, ct);
        }

        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        Directory.CreateDirectory(tmpDir);
        progress?.Report("Extracting...");
        SafeExtractZip(tmpZip, tmpDir);

        var winws = Directory.GetFiles(tmpDir, "winws.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("winws.exe not in archive");
        return (tag, Path.GetDirectoryName(winws)!, tmpZip, tmpDir);
    }

    private static void SafeExtractZip(string zipPath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                var dir = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!dir.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Zip path traversal blocked");
                Directory.CreateDirectory(dir);
                continue;
            }
            var outPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!outPath.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Zip path traversal blocked: " + entry.FullName);
            var parent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            entry.ExtractToFile(outPath, overwrite: true);
        }
    }
}
