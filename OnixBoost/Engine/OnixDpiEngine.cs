using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Divert.Windows;
using OnixBoost.Services;

namespace OnixBoost.Engine;

/// <summary>
/// Native DPI desync in C# + WinDivert.
/// Inspired by bol-van/zapret (fake / split / disorder) — our implementation, not winws.exe.
/// </summary>
public sealed class OnixDpiEngine : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DivertService? _divert;
    private DesyncMode _mode = DesyncMode.FakeSplit;
    private HostMatcher _hosts = HostMatcher.LoadFromFile("");
    private byte[] _fakeTls = Array.Empty<byte>();
    private byte[] _fakeDiscordUdp = Array.Empty<byte>();
    private byte[] _fakeQuic = Array.Empty<byte>();
    private int _repeats = 6;
    private long _hitCount;

    public bool IsRunning => _loop is { IsCompleted: false };
    public long HitCount => Interlocked.Read(ref _hitCount);

    /// <summary>
    /// Games DPI via our own WinDivert handle.
    /// When Discord winws is also on (priority 0), we open at a LOWER priority so packets
    /// pass Discord first, then reach us — no exclusive Discord IP filter needed.
    /// </summary>
    public Task StartGamesAsync(bool discordAlsoOn, int repeats = 6) =>
        StartInternalAsync(
            DesyncMode.FakeSplit,
            repeats,
            HostMatcher.LoadGamesLists(),
            gamesFilter: true,
            priority: discordAlsoOn ? (short)-1000 : (short)0,
            requireHostMatch: true);

    public async Task StartAsync(DesyncMode mode, int repeats = 6)
    {
        var hostsFile = Path.Combine(AppPaths.DpiRoot, "fakes", "hosts.txt");
        if (!File.Exists(hostsFile))
            hostsFile = Path.Combine(AppPaths.ListsDir, "list-general.txt");
        await StartInternalAsync(
            mode,
            repeats,
            HostMatcher.LoadFromFile(hostsFile),
            gamesFilter: false,
            priority: 0,
            requireHostMatch: false);
    }

    private bool _requireHostMatch;

    private async Task StartInternalAsync(
        DesyncMode mode,
        int repeats,
        HostMatcher hosts,
        bool gamesFilter,
        short priority,
        bool requireHostMatch)
    {
        await StopAsync();
        _mode = mode;
        _repeats = Math.Clamp(repeats, 1, 12);
        _hosts = hosts;
        _requireHostMatch = requireHostMatch;

        var fakes = Path.Combine(AppPaths.DpiRoot, "fakes");
        Directory.CreateDirectory(fakes);
        _fakeTls = ReadOrEmpty(Path.Combine(fakes, "tls_fake.bin"));
        _fakeDiscordUdp = ReadOrEmpty(Path.Combine(fakes, "discord_udp_fake.bin"));
        _fakeQuic = ReadOrEmpty(Path.Combine(fakes, "quic_fake.bin"));
        if (_fakeTls.Length == 0)
            throw new FileNotFoundException("Missing dpi/fakes/tls_fake.bin", Path.Combine(fakes, "tls_fake.bin"));

        Logger.Info($"Native DPI mode={mode} games={gamesFilter} priority={priority} root={AppPaths.Root}");

        EnsureWinDivertBesideExe();

        // Games: TCP/UDP web ports only — Discord voice UDP stays with winws Flowseal.
        DivertFilter filter = gamesFilter
            ? new DivertFilter(
                "outbound and (" +
                "(tcp and (tcp.DstPort == 80 or tcp.DstPort == 443 or tcp.DstPort == 8443)) or " +
                "(udp and udp.DstPort == 443)" +
                ")")
            : new DivertFilter(
                "outbound and (" +
                "(tcp and (tcp.DstPort == 80 or tcp.DstPort == 443 or tcp.DstPort == 8443)) or " +
                "(udp and (udp.DstPort == 443 or (udp.DstPort >= 19294 and udp.DstPort <= 19344) or " +
                "(udp.DstPort >= 50000 and udp.DstPort <= 50100)))" +
                ")");

        try
        {
            _divert = new DivertService(filter, DivertLayer.Network, priority, DivertFlags.None,
                runContinuationsAsynchronously: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "WinDivert open failed. Run as Admin + exclude WinDivert in AV.\n" + ex.Message, ex);
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
        await Task.Delay(500);
        if (_loop.IsFaulted)
            throw _loop.Exception?.GetBaseException() ?? new InvalidOperationException("Native DPI crashed");
        Logger.Info($"Native DPI RUNNING priority={priority}");
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_loop != null)
                await Task.WhenAny(_loop, Task.Delay(2500));
        }
        catch { }

        try { _divert?.Dispose(); } catch { }
        _divert = null;
        _loop = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task LoopAsync(CancellationToken ct)
    {
        var divert = _divert!;
        var buffer = new byte[0xFFFF];
        var addrs = new DivertAddress[1];

        while (!ct.IsCancellationRequested)
        {
            DivertReceiveResult recv;
            try
            {
                recv = await divert.ReceiveAsync(buffer, addrs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Error("recv: " + ex.Message);
                try { await Task.Delay(30, ct); } catch { }
                continue;
            }

            var len = recv.DataLength;
            if (len <= 0) continue;

            // Copy out of recv buffer so we never hold Span across awaits
            var packet = new byte[len];
            Buffer.BlockCopy(buffer, 0, packet, 0, len);
            var addr0 = addrs[0];

            try
            {
                if (PacketCodec.TryParseIpv4Tcp(packet, out var tcp) && tcp.PayloadLength > 0)
                    await HandleTcpAsync(divert, packet, addr0, tcp, ct);
                else if (PacketCodec.TryParseIpv4Udp(packet, out var udp) && udp.PayloadLength > 0)
                    await HandleUdpAsync(divert, packet, addr0, udp, ct);
                else
                    await SendPacketAsync(divert, packet, addr0, ct);
            }
            catch (Exception ex)
            {
                Logger.Error("handle: " + ex.Message);
                try { await SendPacketAsync(divert, packet, addr0, ct); } catch { }
            }
        }
    }

    private async Task HandleTcpAsync(
        DivertService divert, byte[] packet, DivertAddress addr,
        PacketCodec.Ipv4TcpView tcp, CancellationToken ct)
    {
        var payload = new byte[tcp.PayloadLength];
        Buffer.BlockCopy(packet, tcp.PayloadOffset, payload, 0, tcp.PayloadLength);

        // Discord/Chrome often send huge ClientHello (Kyber) split across TCP segments —
        // SNI may be missing in the first packet. Desync ANY TLS/HTTP start on 80/443/8443.
        var tlsStart = payload.Length >= 3 && payload[0] == 0x16 && payload[1] == 0x03;
        var httpStart = TlsSni.LooksLikeHttp(payload);
        var host = "";
        var hasSni = TlsSni.TryGetSni(payload, out host);
        var hasHttpHost = httpStart && TlsSni.TryGetHttpHost(payload, out host);
        // Games priority path: only desync when SNI/Host is on Steam/Games lists (never empty-host).
        // Legacy path: empty host still allowed (Discord/Chrome Kyber first segment).
        var hostOk = _requireHostMatch
            ? (!string.IsNullOrEmpty(host) && _hosts.Matches(host))
            : (string.IsNullOrEmpty(host) || _hosts.Matches(host));

        // Apply when: TLS record start, or HTTP, or hostlist hit. Skip only unrelated mid-stream data.
        var shouldDesync = (tlsStart || httpStart || hasSni || hasHttpHost) && hostOk;
        if (!shouldDesync)
        {
            await SendPacketAsync(divert, packet, addr, ct);
            return;
        }

        // Rate-limited trace
        Interlocked.Increment(ref _hitCount);
        if (_hitCount <= 8 || _hitCount % 50 == 0)
            Logger.Info($"DPI TCP hit#{_hitCount} port={tcp.DstPort} host={(host.Length == 0 ? "?" : host)} tls={tlsStart} len={tcp.PayloadLength}");

        switch (_mode)
        {
            case DesyncMode.FakeTls:
                await SendTcpFakesAsync(divert, packet, addr, tcp, badSeq: false, ct);
                await SendPacketAsync(divert, packet, addr, ct);
                break;

            case DesyncMode.Split1:
                await SendTcpSplitAsync(divert, packet, addr, tcp, 1, ct);
                break;

            case DesyncMode.FakeSplit:
                await SendTcpFakesAsync(divert, packet, addr, tcp, badSeq: true, ct);
                var pos = Math.Clamp(2, 1, Math.Max(1, tcp.PayloadLength - 1));
                await SendTcpSplitAsync(divert, packet, addr, tcp, pos, ct);
                break;

            case DesyncMode.MultiSplit:
                await SendTcpSplitAsync(divert, packet, addr, tcp, Math.Max(1, tcp.PayloadLength / 3), ct);
                break;

            case DesyncMode.Disorder:
                await SendTcpDisorderAsync(divert, packet, addr, tcp, ct);
                break;

            default:
                await SendPacketAsync(divert, packet, addr, ct);
                break;
        }
    }

    private async Task HandleUdpAsync(
        DivertService divert, byte[] packet, DivertAddress addr,
        PacketCodec.Ipv4UdpView udp, CancellationToken ct)
    {
        if (udp.DstPort == 443 && _fakeQuic.Length > 0 && !_requireHostMatch)
        {
            // Broad QUIC fakes only on legacy path; Games path needs host — skip blind QUIC
            for (var i = 0; i < _repeats; i++)
            {
                var fake = PacketCodec.BuildUdpWithPayload(packet, udp, _fakeQuic);
                PacketCodec.SetTtl(fake, 6);
                await SendPacketAsync(divert, fake, addr, ct);
            }
        }
        else if (!_requireHostMatch &&
                 (udp.DstPort is >= 19294 and <= 19344 || udp.DstPort is >= 50000 and <= 50100) &&
                 _fakeDiscordUdp.Length > 0)
        {
            for (var i = 0; i < _repeats; i++)
            {
                var n = Math.Min(_fakeDiscordUdp.Length, Math.Max(udp.PayloadLength, 32));
                var slice = new byte[n];
                Buffer.BlockCopy(_fakeDiscordUdp, 0, slice, 0, n);
                var fake = PacketCodec.BuildUdpWithPayload(packet, udp, slice);
                PacketCodec.SetTtl(fake, 6);
                await SendPacketAsync(divert, fake, addr, ct);
            }
        }
        else if (udp.DstPort == 443 && _fakeQuic.Length > 0 && _requireHostMatch)
        {
            // Games QUIC: light fake for steam/epic CDNs (no SNI on QUIC Initial easily) —
            // only when not requiring TCP host; keep pass-through to avoid Discord QUIC damage.
        }

        await SendPacketAsync(divert, packet, addr, ct);
    }

    private async Task SendTcpFakesAsync(
        DivertService divert, byte[] original, DivertAddress addr,
        PacketCodec.Ipv4TcpView tcp, bool badSeq, CancellationToken ct)
    {
        // Vary TTL — TTL=1 often never reaches DPI on Windows; 4..8 is what zapret users try
        var ttls = new byte[] { 4, 5, 6, 8, 3, 10 };
        for (var i = 0; i < _repeats; i++)
        {
            uint? seq = badSeq ? unchecked(tcp.Seq + 0x10000000u + (uint)(i * 7777)) : null;
            var fake = PacketCodec.BuildTcpWithPayload(original, tcp, _fakeTls, seq);
            PacketCodec.SetTtl(fake, ttls[i % ttls.Length]);
            await SendPacketAsync(divert, fake, addr, ct);
        }
    }

    private async Task SendTcpSplitAsync(
        DivertService divert, byte[] original, DivertAddress addr,
        PacketCodec.Ipv4TcpView tcp, int splitAt, CancellationToken ct)
    {
        if (splitAt <= 0 || splitAt >= tcp.PayloadLength)
        {
            await SendPacketAsync(divert, original, addr, ct);
            return;
        }

        var part1 = new byte[splitAt];
        var part2 = new byte[tcp.PayloadLength - splitAt];
        Buffer.BlockCopy(original, tcp.PayloadOffset, part1, 0, splitAt);
        Buffer.BlockCopy(original, tcp.PayloadOffset + splitAt, part2, 0, part2.Length);

        var pkt1 = PacketCodec.BuildTcpWithPayload(original, tcp, part1, tcp.Seq);
        await SendPacketAsync(divert, pkt1, addr, ct);

        var pkt2 = PacketCodec.BuildTcpWithPayload(original, tcp, part2, unchecked(tcp.Seq + (uint)splitAt));
        await SendPacketAsync(divert, pkt2, addr, ct);
    }

    private async Task SendTcpDisorderAsync(
        DivertService divert, byte[] original, DivertAddress addr,
        PacketCodec.Ipv4TcpView tcp, CancellationToken ct)
    {
        var splitAt = Math.Max(1, tcp.PayloadLength / 2);
        var part1 = new byte[splitAt];
        var part2 = new byte[tcp.PayloadLength - splitAt];
        Buffer.BlockCopy(original, tcp.PayloadOffset, part1, 0, splitAt);
        Buffer.BlockCopy(original, tcp.PayloadOffset + splitAt, part2, 0, part2.Length);

        var pkt2 = PacketCodec.BuildTcpWithPayload(original, tcp, part2, unchecked(tcp.Seq + (uint)splitAt));
        await SendPacketAsync(divert, pkt2, addr, ct);

        var pkt1 = PacketCodec.BuildTcpWithPayload(original, tcp, part1, tcp.Seq);
        await SendPacketAsync(divert, pkt1, addr, ct);
    }

    /// <summary>Checksums must use DivertAddress or WinDivert may emit invalid packets (silent drop).</summary>
    private static DivertAddress FixChecksums(byte[] packet, DivertAddress addr)
    {
        DivertHelper.CalculateChecksums(packet.AsSpan(), ref addr, DivertHelperFlags.None);
        return addr;
    }

    private static async Task SendPacketAsync(
        DivertService divert, byte[] packet, DivertAddress addr, CancellationToken ct)
    {
        addr = FixChecksums(packet, addr);
        var addrs = new[] { addr };
        await divert.SendAsync(packet, addrs, ct);
    }

    private static byte[] ReadOrEmpty(string path) =>
        File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();

    private static void EnsureWinDivertBesideExe()
    {
        var destDll = Path.Combine(AppPaths.Root, "WinDivert.dll");
        var destSys = Path.Combine(AppPaths.Root, "WinDivert64.sys");
        var srcDll = Path.Combine(AppPaths.BinDir, "WinDivert.dll");
        var srcSys = Path.Combine(AppPaths.BinDir, "WinDivert64.sys");
        try
        {
            if (File.Exists(srcDll)) File.Copy(srcDll, destDll, true);
            if (File.Exists(srcSys)) File.Copy(srcSys, destSys, true);
        }
        catch (Exception ex)
        {
            Logger.Error("WinDivert copy: " + ex.Message);
        }
    }
}
