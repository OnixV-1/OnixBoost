using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// Light Generate: small grid of known-good winws argument variants (not full zapret2 combinator).
/// Writes temp args under dpi/args/.gen/ and health-checks via WinwsEngine.
/// </summary>
public sealed class StrategyGenerator
{
    private readonly WinwsEngine _winws;
    private readonly HealthChecker _health;

    public StrategyGenerator(WinwsEngine winws, HealthChecker health)
    {
        _winws = winws;
        _health = health;
    }

    private static string GenDir => Path.Combine(AppPaths.ArgsDir, ".gen");

    public IReadOnlyList<(string Id, string Title, string RelFile)> BuildCandidates()
    {
        Directory.CreateDirectory(GenDir);
        var list = new List<(string, string, string)>();
        var i = 0;

        // Voice UDP fake variants (Discord Go Live / voice)
        foreach (var repeats in new[] { 4, 6, 8, 11 })
        {
            i++;
            var id = $"gen_voice_{repeats}";
            var file = $"gen_voice_{repeats}.txt";
            var body = $"""
                --wf-tcp=80,443,2053,2083,2087,2096,8443
                --wf-udp=443,19294-19344,50000-50100
                --filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-repeats={repeats} --new
                --filter-tcp=443 --hostlist=%ROOT%lists\list-general.txt --hostlist-exclude=%ROOT%lists\list-exclude.txt --dpi-desync=fake,multisplit --dpi-desync-split-pos=1,midsld --dpi-desync-fake-tls-mod=rnd,dupsid --dpi-desync-repeats={repeats} --new
                --filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media --dpi-desync=fake,multisplit --dpi-desync-repeats={repeats} --dpi-desync-fake-tls-mod=rnd,dupsid --new
                --filter-udp=443 --hostlist=%ROOT%lists\list-general.txt --dpi-desync=fake --dpi-desync-repeats={Math.Max(6, repeats)} --dpi-desync-fake-quic=%BIN%quic_initial_www_google_com.bin
                """;
            File.WriteAllText(Path.Combine(GenDir, file), body);
            list.Add((id, $"Gen voice×{repeats}", Path.Combine(".gen", file)));
        }

        // TLS fake + disorder / split grid
        foreach (var (mode, split) in new[]
                 {
                     ("fake,multisplit", "1,midsld"),
                     ("fake,multidisorder", "method+2,midsld"),
                     ("fake", "midsld"),
                     ("multisplit", "1,midsld")
                 })
        foreach (var fool in new[] { "badseq", "md5sig,badseq", "ts", "badsum,badseq" })
        {
            i++;
            if (i > 36) break;
            var id = $"gen_tls_{i}";
            var file = $"gen_tls_{i}.txt";
            var body = $"""
                --wf-tcp=80,443,2053,2083,2087,2096,8443
                --wf-udp=443,19294-19344,50000-50100
                --filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-repeats=6 --new
                --filter-tcp=443 --hostlist=%ROOT%lists\list-general.txt --hostlist-exclude=%ROOT%lists\list-exclude.txt --dpi-desync={mode} --dpi-desync-split-pos={split} --dpi-desync-fooling={fool} --dpi-desync-repeats=6 --dpi-desync-fake-tls=%BIN%tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid --new
                --filter-udp=443 --hostlist=%ROOT%lists\list-general.txt --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic=%BIN%quic_initial_www_google_com.bin
                """;
            File.WriteAllText(Path.Combine(GenDir, file), body);
            list.Add((id, $"Gen TLS {mode}/{fool}", Path.Combine(".gen", file)));
        }

        // IP-set voice (when hostlist alone fails)
        i++;
        var ipFile = "gen_ipset_voice.txt";
        File.WriteAllText(Path.Combine(GenDir, ipFile), """
            --wf-tcp=80,443,2053,2083,2087,2096,8443
            --wf-udp=443,19294-19344,50000-50100
            --filter-udp=50000-50100 --ipset=%ROOT%list-discord_ips.txt --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-any-protocol --dpi-desync-cutoff=n4 --new
            --filter-tcp=443 --hostlist=%ROOT%lists\list-general.txt --dpi-desync=fake,multisplit --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=6 --dpi-desync-fake-tls-mod=rnd,dupsid
            """);
        list.Add(($"gen_ipset_voice", "Gen IP-set voice", Path.Combine(".gen", ipFile)));

        return list;
    }

    public async Task<(string? Id, string? Title, string? ArgsFile)> FindWorkingAsync(
        BoostTarget healthTarget,
        Action<string> log,
        CancellationToken ct,
        bool games,
        bool youtube,
        bool withDiscord,
        Func<bool, bool, bool, bool, string[]> buildAppends)
    {
        var cands = BuildCandidates();
        log($"Generate: {cands.Count} вариантов…");
        var appends = buildAppends(games, youtube, withDiscord, false);

        for (var i = 0; i < cands.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (id, title, rel) = cands[i];
            log($"[G {i + 1}/{cands.Count}] {title}…");
            try
            {
                await WinDivertCleanup.RunAsync();
                await _winws.StartAsync(rel, autoHostList: false, discordStreamSafe: withDiscord,
                    preserveYoutubeQuic: youtube && withDiscord, appends);
                await Task.Delay(280, ct);
                var h = await _health.CheckTargetAsync(healthTarget, ct, fastFail: true);
                if (!h.Ok)
                {
                    log($"  fail ({h.Ms} ms): {h.Detail}");
                    try { await _winws.StopAsync(); } catch { }
                    continue;
                }
                await Task.Delay(350, ct);
                var confirm = await _health.CheckTargetAsync(healthTarget, ct, fastFail: false);
                if (confirm.Ok)
                {
                    log($"OK Generate: {title} ({confirm.Ms} ms)");
                    return (id, title, rel);
                }
                log($"  flaky: {confirm.Detail}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log($"  error: {ex.Message}");
            }
            try { await _winws.StopAsync(); } catch { }
        }

        return (null, null, null);
    }
}
