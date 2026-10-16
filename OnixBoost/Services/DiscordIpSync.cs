using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// Pulls fresh Discord CIDR/IP sets (Amnezia) into dpi lists used by winws + zapret2 + HY2 games.
/// </summary>
public static class DiscordIpSync
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly string[] Urls =
    {
        "https://raw.githubusercontent.com/iamkuper/amnezia-discord-config/main/configs/all.subnets.txt",
        "https://raw.githubusercontent.com/iamkuper/amnezia-discord-config/main/configs/all.ips.txt"
    };

    private static DateTime _lastOkUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string ListDiscordIpsPath => Path.Combine(AppPaths.DpiRoot, "list-discord_ips.txt");
    public static string Zapret2DiscordIpSet =>
        Path.Combine(AppPaths.DpiRoot, "stack", "user", "ip-set", "discord.list");
    public static string StampPath => Path.Combine(AppPaths.DpiRoot, ".discord-ips-stamp");

    public static IReadOnlyList<string> LoadCachedCidrs()
    {
        try
        {
            if (!File.Exists(ListDiscordIpsPath)) return Array.Empty<string>();
            return File.ReadAllLines(ListDiscordIpsPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("//"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Skip network if synced in the last 12h unless force.</summary>
    public static async Task<(bool ok, string message)> SyncAsync(
        bool force = false,
        CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct))
            return (true, "Discord IPs sync busy");

        try
        {
            if (!force && DateTime.UtcNow - _lastOkUtc < TimeSpan.FromHours(12))
                return (true, "Discord IPs cached");
            if (!force && File.Exists(StampPath))
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(StampPath);
                    if (DateTime.UtcNow - stamp < TimeSpan.FromHours(12))
                    {
                        _lastOkUtc = stamp;
                        return (true, "Discord IPs stamp OK");
                    }
                }
                catch { }
            }

            Exception? last = null;
            var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in Urls)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) continue;
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    foreach (var raw in text.Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
                        if (line.Contains('/') || LooksLikeIp(line))
                            merged.Add(line.Contains('/') ? line : line + "/32");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                }
            }

            if (merged.Count < 50)
            {
                foreach (var c in LoadCachedCidrs())
                    merged.Add(c.Contains('/') ? c : c + "/32");
            }

            if (merged.Count < 20)
                return (false, last?.Message ?? "Discord IP set too small");

            var lines = merged.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Directory.CreateDirectory(AppPaths.DpiRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(Zapret2DiscordIpSet)!);

            var tmpIps = ListDiscordIpsPath + ".tmp";
            var tmpSet = Zapret2DiscordIpSet + ".tmp";
            await File.WriteAllLinesAsync(tmpIps, lines.Select(StripHostBit), ct);
            await File.WriteAllLinesAsync(tmpSet, lines, ct);
            File.Move(tmpIps, ListDiscordIpsPath, overwrite: true);
            File.Move(tmpSet, Zapret2DiscordIpSet, overwrite: true);

            try
            {
                var cfg = Path.Combine(AppPaths.DpiRoot, "stack", "configs", "ip-set", "discord.list");
                Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
                await File.WriteAllLinesAsync(cfg, lines, ct);
            }
            catch { }

            try { await File.WriteAllTextAsync(StampPath, DateTime.UtcNow.ToString("o"), ct); } catch { }
            _lastOkUtc = DateTime.UtcNow;

            var msg = $"Discord IPs: {lines.Length} CIDR";
            Logger.Info(msg);
            return (true, msg);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string StripHostBit(string cidr)
    {
        // list-discord_ips historically mixed bare IPs and CIDRs — keep as-is without forcing /32 in file if was /32 from us
        if (cidr.EndsWith("/32", StringComparison.Ordinal))
            return cidr[..^3];
        return cidr;
    }

    private static bool LooksLikeIp(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
            if (!byte.TryParse(p, out _)) return false;
        return true;
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OnixBoost");
        return c;
    }
}
