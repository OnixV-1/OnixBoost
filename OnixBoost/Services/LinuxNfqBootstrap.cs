using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// nftables NFQUEUE for nfqws. Applies each queue at most once until remove —
/// avoids password spam during Discord strategy sweep.
/// </summary>
public static class LinuxNfqBootstrap
{
    private static readonly HashSet<int> AppliedQueues = new();
    private static bool _elevateFailed;

    public static int QueueFor(WinwsSlot slot) => slot switch
    {
        WinwsSlot.Games => 201,
        _ => 200
    };

    public static async Task EnsureAsync(WinwsSlot slot, IEnumerable<string> nfqArgs)
    {
        if (!PlatformOs.IsLinux) return;

        var q = QueueFor(slot);
        if (AppliedQueues.Contains(q))
        {
            Logger.Info($"nfq[{slot}] queue={q} already applied (cached)");
            return;
        }

        if (_elevateFailed)
            throw new InvalidOperationException(
                "NFQUEUE уже не удалось настроить в этой сессии.\n" + LinuxElevate.CapsHint);

        var (tcp, udp) = ExtractPorts(nfqArgs);
        if (tcp.Count == 0) tcp.AddRange(new[] { "80", "443", "2053", "2083", "2087", "2096", "8443" });
        if (udp.Count == 0) udp.AddRange(new[] { "443", "19294-19344", "50000-50100" });

        var tcpCsv = string.Join(",", tcp);
        var udpCsv = string.Join(",", udp);
        var (ok, output) = await LinuxElevate.RunNftHelperAsync($"apply {q} {tcpCsv} {udpCsv}");
        if (!ok)
        {
            _elevateFailed = true;
            throw new InvalidOperationException(
                "Не удалось настроить NFQUEUE (один запрос прав на сессию).\n" +
                LinuxElevate.CapsHint + "\n" +
                output[..Math.Min(400, output.Length)]);
        }

        AppliedQueues.Add(q);
        // udp 443 is dropped by onixboost-nft (force TCP); voice ranges still queued
        Logger.Info($"nfq[{slot}] queue={q} tcp={tcpCsv} udp={udpCsv} (QUIC/udp443 DROP→TCP)");
    }

    public static async Task RemoveAsync(WinwsSlot slot)
    {
        if (!PlatformOs.IsLinux) return;
        var q = QueueFor(slot);
        AppliedQueues.Remove(q);
        // Never pkexec on stop — hang / freeze exit
        await LinuxElevate.RunNftHelperAsync($"remove {q}", bestEffort: true);
    }

    public static async Task RemoveAllAsync()
    {
        if (!PlatformOs.IsLinux) return;
        AppliedQueues.Clear();
        _elevateFailed = false;
        await LinuxElevate.RunNftHelperAsync("remove-all", bestEffort: true);
    }

    /// <summary>Forget cached queues so next Ensure re-applies (e.g. after port-list fix).</summary>
    public static void InvalidateCache()
    {
        AppliedQueues.Clear();
        _elevateFailed = false;
    }

    private static (List<string> tcp, List<string> udp) ExtractPorts(IEnumerable<string> args)
    {
        var tcp = new List<string>();
        var udp = new List<string>();
        foreach (var a in args)
        {
            if (a.StartsWith("--filter-tcp=", StringComparison.OrdinalIgnoreCase))
                MergePorts(tcp, a[(a.IndexOf('=') + 1)..], fromWf: false);
            else if (a.StartsWith("--filter-udp=", StringComparison.OrdinalIgnoreCase))
                MergePorts(udp, a[(a.IndexOf('=') + 1)..], fromWf: false);
            else if (a.StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase))
                MergePorts(tcp, a[(a.IndexOf('=') + 1)..], fromWf: true);
            else if (a.StartsWith("--wf-udp=", StringComparison.OrdinalIgnoreCase))
                MergePorts(udp, a[(a.IndexOf('=') + 1)..], fromWf: true);
        }
        return (tcp, udp);
    }

    private static void MergePorts(List<string> dst, string csv, bool fromWf)
    {
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        // Flowseal Soft: --wf-tcp=80,443,...,12 — last lone 1..64 is WinDivert priority, not a port.
        if (fromWf && parts.Count >= 2 &&
            int.TryParse(parts[^1], out var pri) && pri is >= 1 and <= 64 &&
            !parts[^1].Contains('-', StringComparison.Ordinal))
            parts.RemoveAt(parts.Count - 1);

        foreach (var p in parts)
        {
            // Drop nonsense like bare "12" if it slipped in from older caches
            if (int.TryParse(p, out var n) && n is >= 1 and <= 32 && n is not (80 or 443))
                continue;
            if (!dst.Contains(p, StringComparer.OrdinalIgnoreCase))
                dst.Add(p);
        }
    }
}
