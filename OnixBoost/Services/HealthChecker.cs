using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

public sealed class HealthResult
{
    public bool WinwsAlive { get; init; }
    public bool DiscordReachable { get; init; }
    public bool YoutubeReachable { get; init; }
    public string Message { get; init; } = "";
    public long DiscordMs { get; init; }
    public long YoutubeMs { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class HealthChecker
{
    // Prefer Yandex first (RU); others in parallel as backup — old sequential 4×5s was ~20s DNS alone
    private static readonly (string ip, string sni, string path)[] DohBootstraps =
    {
        ("77.88.8.8", "common.dot.dns.yandex.net", "/dns-query?name={0}&type=A"),
        ("1.1.1.1", "cloudflare-dns.com", "/dns-query?name={0}&type=A"),
        ("8.8.8.8", "dns.google", "/dns-query?name={0}&type=A"),
    };

    private static readonly string[] DiscordBootstrapIps =
    {
        "162.159.128.233", "162.159.135.233"
    };

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan SweepFailTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan DohTimeout = TimeSpan.FromSeconds(2);

    public async Task<HealthResult> CheckAsync(CancellationToken ct = default, bool? engineAlive = null)
    {
        var winws = Process.GetProcessesByName("winws").Length > 0
                    || Process.GetProcessesByName("winws2").Length > 0;
        var alive = engineAlive ?? winws;

        // Discord + YouTube in parallel
        var discordTask = ProbeDiscordFastAsync(ct);
        var ytTask = ProbeHostAsync("www.youtube.com", "/generate_204", ct);
        await Task.WhenAll(discordTask, ytTask).ConfigureAwait(false);
        var (discordOk, discordMs, discordDetail) = await discordTask.ConfigureAwait(false);
        var (ytOk, ytMs, _) = await ytTask.ConfigureAwait(false);

        string msg;
        if (!alive)
            msg = "engine not running — press BOOST as Admin";
        else if (discordOk)
            msg = $"OK — Discord {discordMs} ms" + (ytOk ? $", YouTube {ytMs} ms" : "");
        else
            msg = $"Discord DOWN ({discordMs} ms): {discordDetail}";

        return new HealthResult
        {
            WinwsAlive = alive,
            DiscordReachable = discordOk,
            YoutubeReachable = ytOk,
            DiscordMs = discordMs,
            YoutubeMs = ytMs,
            Message = msg,
            Detail = discordDetail
        };
    }

    /// <summary>UnblockPro-style: Discord API must return real gateway JSON with wss://</summary>
    public async Task<(bool Ok, long Ms, string Detail)> CheckDiscordStrictAsync(CancellationToken ct = default)
    {
        var (ok, ms, detail) = await ProbeDiscordGatewayAsync(ct);
        return (ok, ms, detail);
    }

    /// <summary>
    /// Prefer googlevideo (real CDN); fall back to youtube.com 204 so HY2 tunnel accept
    /// is not a hard fail when only the redirector is flaky.
    /// </summary>
    public async Task<(bool Ok, long Ms, string Detail)> CheckYoutubeStrictAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? lastFail = null;
        foreach (var (url, label, okCodes) in new[]
                 {
                     ("https://redirector.googlevideo.com/", "googlevideo", new[] { 204, 404 }),
                     ("https://www.youtube.com/generate_204", "youtube", new[] { 204, 404 }),
                     ("https://i.ytimg.com/generate_204", "ytimg", new[] { 204, 404, 200 })
                 })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(HttpTimeout);
                using var http = new HttpClient { Timeout = HttpTimeout };
                using var resp = await http.GetAsync(url, cts.Token);
                var code = (int)resp.StatusCode;
                if (okCodes.Contains(code) || (code >= 200 && code < 400))
                    return (true, sw.ElapsedMilliseconds, label + " " + code);
                lastFail = label + " HTTP " + code;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastFail = label + " " + ex.GetType().Name;
            }
        }

        sw.Stop();
        return (false, sw.ElapsedMilliseconds, lastFail ?? "YouTube unreachable");
    }

    public async Task<(bool Ok, long Ms, string Detail)> CheckTwitchStrictAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? lastFail = null;
        foreach (var (url, label) in new[]
                 {
                     ("https://www.twitch.tv/", "twitch"),
                     ("https://gql.twitch.tv/gql", "gql"),
                 })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(HttpTimeout);
                using var http = new HttpClient { Timeout = HttpTimeout };
                using var resp = await http.GetAsync(url, cts.Token);
                var code = (int)resp.StatusCode;
                if (code >= 200 && code < 500)
                    return (true, sw.ElapsedMilliseconds, label + " " + code);
                lastFail = label + " HTTP " + code;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastFail = label + " " + ex.GetType().Name;
            }
        }
        sw.Stop();
        return (false, sw.ElapsedMilliseconds, lastFail ?? "Twitch unreachable");
    }

    /// <summary>Legacy Games probes (unused Soft card). Kept for StrategyAutoPicker cleanup paths.</summary>
    public async Task<(bool Ok, long Ms, string Detail)> CheckGamesStrictAsync(
        CancellationToken ct = default, bool fast = false)
    {
        var probes = fast
            ? new (string Host, string Label)[]
            {
                ("store.steampowered.com", "Steam"),
                ("www.epicgames.com", "Epic"),
            }
            : new (string Host, string Label)[]
            {
                ("store.steampowered.com", "Steam"),
                ("steamcommunity.com", "Steam community"),
                ("www.epicgames.com", "Epic"),
                ("auth.riotgames.com", "Riot"),
                ("battle.net", "Battle.net"),
            };
        var tasks = probes.Select(async p =>
        {
            var (ok, ms, detail) = await ProbeHostAsync(p.Host, "/", ct);
            return (ok, ms, detail, p.Label);
        }).ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { /* individual results below */ }

        (bool Ok, long Ms, string Detail, string Label)? bestFail = null;
        foreach (var t in tasks)
        {
            if (t.IsCanceled || t.IsFaulted) continue;
            var r = t.Result;
            if (r.ok) return (true, r.ms, r.Label + " OK");
            if (bestFail is null || r.ms > bestFail.Value.Ms)
                bestFail = (r.ok, r.ms, r.detail, r.Label);
        }
        if (bestFail is { } f)
            return (false, f.Ms, f.Detail);
        return (false, (long)SweepFailTimeout.TotalMilliseconds, "Games probe cancel");
    }

    // DC IPs (routed by telegram tunnel ip_cidr). web.telegram.org is often Cloudflare — bypasses TG CIDR.
    private static readonly string[] TelegramDcIps =
    {
        "149.154.167.51", "149.154.167.91", "91.108.56.100"
    };

    public async Task<(bool Ok, long Ms, string Detail)> CheckTelegramStrictAsync(CancellationToken ct = default)
    {
        // Desktop TG speaks binary protocol — HTTPS GET to DC often fails even when the tunnel is fine.
        // Prefer TCP:443 to DC CIDRs (same routes as client-telegram.json).
        var tcp = await CheckTelegramTcpAsync(ct);
        if (tcp.Ok) return tcp;

        foreach (var ip in TelegramDcIps)
        {
            ct.ThrowIfCancellationRequested();
            var (okIp, msIp, _) = await ProbeIpAsync(ip, "telegram.org", "/", ct);
            if (okIp) return (true, msIp, "DC HTTPS " + ip);
        }

        var (ok, ms, detail) = await ProbeHostAsync("web.telegram.org", "/", ct);
        if (ok) return (true, ms, "web OK");
        (ok, ms, detail) = await ProbeHostAsync("telegram.org", "/", ct);
        return (ok, ms, ok ? "telegram.org OK" : (tcp.Detail + "; " + detail));
    }

    /// <summary>TCP connect to Telegram DC — reliable with split TUN (no HTTPS needed).</summary>
    public async Task<(bool Ok, long Ms, string Detail)> CheckTelegramTcpAsync(CancellationToken ct = default)
    {
        foreach (var ip in TelegramDcIps)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                await socket.ConnectAsync(IPAddress.Parse(ip), 443, cts.Token);
                sw.Stop();
                return (true, sw.ElapsedMilliseconds, "TCP " + ip);
            }
            catch (Exception ex)
            {
                sw.Stop();
                if (ip == TelegramDcIps[^1])
                    return (false, sw.ElapsedMilliseconds, "TCP fail: " + ex.GetType().Name);
            }
        }
        return (false, 0, "TCP no DC");
    }

    /// <param name="fastFail">Shorter probe timeout for auto-sweep — skip dead strategies faster.</param>
    public async Task<(bool Ok, long Ms, string Detail)> CheckTargetAsync(
        BoostTarget target, CancellationToken ct = default, bool fastFail = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (fastFail)
            linked.CancelAfter(SweepFailTimeout);
        var token = linked.Token;
        try
        {
            return target switch
            {
                BoostTarget.Discord => await CheckDiscordStrictAsync(token),
                BoostTarget.Youtube => await CheckYoutubeStrictAsync(token),
                BoostTarget.Twitch => await CheckTwitchStrictAsync(token),
                BoostTarget.Telegram => await CheckTelegramStrictAsync(token),
                _ => await CheckDiscordStrictAsync(token)
            };
        }
        catch (OperationCanceledException) when (fastFail && !ct.IsCancellationRequested)
        {
            return (false, (long)SweepFailTimeout.TotalMilliseconds, "TaskCanceledException");
        }
    }

    private async Task<(bool Ok, long Ms, string Detail)> CheckDiscordAndGamesAsync(CancellationToken ct)
    {
        var dTask = CheckDiscordStrictAsync(ct);
        var gTask = CheckGamesStrictAsync(ct);
        await Task.WhenAll(dTask, gTask);
        var d = await dTask;
        var g = await gTask;
        var ms = Math.Max(d.Ms, g.Ms);
        if (d.Ok && g.Ok) return (true, ms, "Discord+Games OK");
        if (!d.Ok && !g.Ok) return (false, ms, $"D:{d.Detail}; G:{g.Detail}");
        if (!d.Ok) return (false, ms, "Discord fail: " + d.Detail);
        return (false, ms, "Games fail: " + g.Detail);
    }

    private static async Task<(bool ok, long ms, string detail)> ProbeDiscordFastAsync(CancellationToken ct)
    {
        // Prefer strict gateway (proves Discord, not ISP stub page)
        var (ok, ms, detail) = await ProbeDiscordGatewayAsync(ct);
        if (ok) return (ok, ms, detail);

        (ok, ms, detail) = await ProbeHostAsync("discord.com", "/", ct);
        if (ok) return (ok, ms, detail);

        foreach (var ip in DiscordBootstrapIps)
        {
            ct.ThrowIfCancellationRequested();
            (ok, ms, detail) = await ProbeIpAsync(ip, "discord.com", "/", ct);
            if (ok) return (true, ms, "via bootstrap IP " + ip);
            break;
        }

        return (false, ms, detail);
    }

    private static async Task<(bool ok, long ms, string detail)> ProbeDiscordGatewayAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        List<IPAddress> ips;
        try { ips = await ResolveAsync("discord.com", ct); }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, "DNS: " + ex.Message);
        }
        if (ips.Count == 0)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, "DNS empty");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HttpTimeout);
            var ip = ips[0];
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, token) =>
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(ip, 443, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            using var http = new HttpClient(handler) { Timeout = HttpTimeout };
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/gateway");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 OnixBoost/2.6");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();
            if ((int)resp.StatusCode != 200)
                return (false, sw.ElapsedMilliseconds, "HTTP " + (int)resp.StatusCode);
            // UnblockPro: real gateway returns {"url":"wss://gateway.discord.gg..."}
            if (body.Contains("\"url\"", StringComparison.Ordinal) &&
                body.Contains("wss://", StringComparison.OrdinalIgnoreCase))
                return (true, sw.ElapsedMilliseconds, "gateway OK");
            return (false, sw.ElapsedMilliseconds, "stub/blocked body");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private static async Task<(bool ok, long ms, string detail)> ProbeHostAsync(
        string host, string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        List<IPAddress> ips;
        try
        {
            ips = await ResolveAsync(host, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, "DNS fail: " + ex.Message);
        }

        if (ips.Count == 0)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, "DNS empty for " + host);
        }

        try
        {
            var (ok, _) = await HttpToIpAsync(ips[0], host, path, ct);
            sw.Stop();
            return (ok, sw.ElapsedMilliseconds, ok ? "ok via " + ips[0] : "TLS/HTTP fail to " + ips[0]);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, "connect fail: " + ex.GetType().Name);
        }
    }

    private static async Task<(bool ok, long ms, string detail)> ProbeIpAsync(
        string ip, string sni, string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (ok, _) = await HttpToIpAsync(IPAddress.Parse(ip), sni, path, ct);
            sw.Stop();
            return (ok, sw.ElapsedMilliseconds, ok ? "ok" : "fail " + ip);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private static async Task<(bool ok, long ms)> HttpToIpAsync(
        IPAddress ip, string sniHost, string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HttpTimeout);
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, token) =>
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(ip, 443, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var http = new HttpClient(handler) { Timeout = HttpTimeout };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{sniHost}{path}");
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 OnixBoost/2.5");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        sw.Stop();
        return ((int)resp.StatusCode < 500, sw.ElapsedMilliseconds);
    }

    private static async Task<List<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        // Parallel DoH — first success wins (was sequential 4×5s)
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(DohTimeout + TimeSpan.FromMilliseconds(500));

        var tasks = DohBootstraps.Select(async b =>
        {
            try
            {
                var path = string.Format(b.path, Uri.EscapeDataString(host));
                return await DohQueryViaIpAsync(b.ip, b.sni, path, linked.Token);
            }
            catch
            {
                return new List<IPAddress>();
            }
        }).ToList();

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var list = await done;
            if (list.Count > 0)
            {
                linked.Cancel();
                return list;
            }
        }

        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(DohTimeout);
            var addrs = await Dns.GetHostAddressesAsync(host, dnsCts.Token);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (v4.Count > 0) return v4;
        }
        catch { }

        return new List<IPAddress>();
    }

    private static async Task<List<IPAddress>> DohQueryViaIpAsync(
        string dohIp, string sni, string pathAndQuery, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Parse(dohIp), 443, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        using var http = new HttpClient(handler) { Timeout = DohTimeout };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{sni}{pathAndQuery}");
        req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new List<IPAddress>();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<IPAddress>();
        if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return list;
        foreach (var a in answers.EnumerateArray())
        {
            if (!a.TryGetProperty("type", out var t) || t.GetInt32() != 1) continue;
            if (!a.TryGetProperty("data", out var data)) continue;
            if (IPAddress.TryParse(data.GetString(), out var ip) &&
                ip.AddressFamily == AddressFamily.InterNetwork)
                list.Add(ip);
        }
        return list;
    }
}
