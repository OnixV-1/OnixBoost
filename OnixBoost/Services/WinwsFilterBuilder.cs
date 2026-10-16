using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OnixBoost.Services;

/// <summary>
/// Mutually exclusive WinDivert filters so Discord and Games can run as two winws
/// without overlapping --wf-* (bol-van). Used only when BOTH cards are ON.
/// Solo cards keep native Flowseal / profile --wf-*.
/// </summary>
public static class WinwsFilterBuilder
{
    public const string DiscordFilterFileName = "wf-discord.excl.txt";
    public const string GamesFilterFileName = "wf-games.excl.txt";

    private const int MaxIpClauses = 220;

    /// <summary>Hosts we resolve at filter-build time so Cloudflare edges are not missing from Amnezia CIDRs.</summary>
    private static readonly string[] DiscordResolveHosts =
    {
        "discord.com",
        "gateway.discord.gg",
        "cdn.discordapp.com",
        "media.discordapp.net",
        "discordapp.com",
        "discord.gg",
        "status.discord.com",
        "latency.discord.media",
    };

    public static string DiscordFilterPath => Path.Combine(AppPaths.BinDir, DiscordFilterFileName);
    public static string GamesFilterPath => Path.Combine(AppPaths.BinDir, GamesFilterFileName);

    public static (bool Ok, int CidrCount, string Message) WriteExclusiveFilters()
    {
        Directory.CreateDirectory(AppPaths.BinDir);
        var ranges = ParseCidrs(DiscordIpSync.LoadCachedCidrs());
        ranges.AddRange(ResolveDiscordHostRanges());
        if (ranges.Count == 0)
            return (false, 0, "no Discord CIDRs — sync first");

        ranges = MergeRanges(ranges);
        if (ranges.Count > MaxIpClauses)
            ranges = ranges
                .OrderByDescending(r => r.Hi - r.Lo)
                .ThenBy(r => r.Lo)
                .Take(MaxIpClauses)
                .OrderBy(r => r.Lo)
                .ToList();

        var dstOr = BuildIpOrClause(ranges, "ip.DstAddr");
        var srcOr = BuildIpOrClause(ranges, "ip.SrcAddr");

        // Full --wf-raw replaces zapret constructor — include outbound + inbound synack/rst.
        // Discord: voice UDP (port-unique) + TCP/UDP 443 etc. only to Discord IP ranges.
        var discord =
            "(!loopback and !impostor) and (" +
            "(outbound and (" +
            "(udp and ((udp.DstPort>=19294 and udp.DstPort<=19344) or (udp.DstPort>=50000 and udp.DstPort<=50100))) or " +
            "(udp and udp.DstPort==443 and (" + dstOr + ")) or " +
            "(tcp and (tcp.DstPort==80 or tcp.DstPort==443 or tcp.DstPort==2053 or tcp.DstPort==2083 or " +
            "tcp.DstPort==2087 or tcp.DstPort==2096 or tcp.DstPort==8443) and (" + dstOr + "))" +
            ")) or " +
            "(inbound and tcp and (tcp.Syn or tcp.Rst) and (" + srcOr + "))" +
            ")";

        // Games: 80/443 except Discord IP ranges (no overlap with Discord slot).
        var games =
            "(!loopback and !impostor) and (" +
            "(outbound and ((tcp and (tcp.DstPort==80 or tcp.DstPort==443)) or (udp and udp.DstPort==443)) " +
            "and not (" + dstOr + ")) or " +
            "(inbound and tcp and (tcp.Syn or tcp.Rst) and not (" + srcOr + ") and " +
            "(tcp.SrcPort==80 or tcp.SrcPort==443))" +
            ")";

        File.WriteAllText(DiscordFilterPath, discord, Encoding.ASCII);
        File.WriteAllText(GamesFilterPath, games, Encoding.ASCII);

        return (true, ranges.Count, $"Discord/Games wf exclusive ({ranges.Count} ranges)");
    }

    private static List<(uint Lo, uint Hi)> ResolveDiscordHostRanges()
    {
        var list = new List<(uint, uint)>();
        foreach (var host in DiscordResolveHosts)
        {
            try
            {
                var addrs = Dns.GetHostAddresses(host);
                foreach (var a in addrs)
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.GetAddressBytes();
                    var v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
                    list.Add((v, v));
                }
            }
            catch
            {
                // DNS may fail offline — Amnezia CIDRs still apply
            }
        }
        return list;
    }

    private static string BuildIpOrClause(List<(uint Lo, uint Hi)> ranges, string field)
    {
        var sb = new StringBuilder(ranges.Count * 56);
        for (var i = 0; i < ranges.Count; i++)
        {
            if (i > 0) sb.Append(" or ");
            var (lo, hi) = ranges[i];
            if (lo == hi)
                sb.Append(field).Append("==").Append(ToIp(lo));
            else
                sb.Append('(').Append(field).Append(">=").Append(ToIp(lo))
                    .Append(" and ").Append(field).Append("<=").Append(ToIp(hi)).Append(')');
        }
        return sb.ToString();
    }

    private static List<(uint Lo, uint Hi)> ParseCidrs(IReadOnlyList<string> cidrs)
    {
        var list = new List<(uint, uint)>(cidrs.Count);
        foreach (var raw in cidrs)
        {
            if (!TryParseCidr(raw, out var lo, out var hi)) continue;
            list.Add((lo, hi));
        }
        return list;
    }

    private static List<(uint Lo, uint Hi)> MergeRanges(List<(uint Lo, uint Hi)> input)
    {
        if (input.Count == 0) return input;
        var sorted = input.OrderBy(x => x.Lo).ThenBy(x => x.Hi).ToList();
        var merged = new List<(uint Lo, uint Hi)> { sorted[0] };
        for (var i = 1; i < sorted.Count; i++)
        {
            var (lo, hi) = sorted[i];
            var last = merged[^1];
            if (lo <= last.Hi + 1)
                merged[^1] = (last.Lo, Math.Max(last.Hi, hi));
            else
                merged.Add((lo, hi));
        }
        return merged;
    }

    private static bool TryParseCidr(string raw, out uint lo, out uint hi)
    {
        lo = hi = 0;
        var s = raw.Trim();
        if (s.Length == 0 || s.Contains(':')) return false;
        var slash = s.IndexOf('/');
        var ipPart = slash >= 0 ? s[..slash] : s;
        if (!IPAddress.TryParse(ipPart, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = ip.GetAddressBytes();
        var addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var prefix = 32;
        if (slash >= 0 &&
            (!int.TryParse(s[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix) ||
             prefix is < 0 or > 32))
            return false;
        if (prefix == 32)
        {
            lo = hi = addr;
            return true;
        }
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        lo = addr & mask;
        hi = lo | ~mask;
        return true;
    }

    private static string ToIp(uint v) =>
        string.Create(CultureInfo.InvariantCulture, $"{(v >> 24) & 0xff}.{(v >> 16) & 0xff}.{(v >> 8) & 0xff}.{v & 0xff}");
}
