using System;
using System.Buffers.Binary;

namespace OnixBoost.Engine;

internal static class PacketCodec
{
    public readonly struct Ipv4TcpView
    {
        public required int IpHeaderLength { get; init; }
        public required int TcpHeaderLength { get; init; }
        public required int PayloadOffset { get; init; }
        public required int PayloadLength { get; init; }
        public required ushort DstPort { get; init; }
        public required uint Seq { get; init; }
        public required byte TcpFlags { get; init; }
    }

    public readonly struct Ipv4UdpView
    {
        public required int IpHeaderLength { get; init; }
        public required int PayloadOffset { get; init; }
        public required int PayloadLength { get; init; }
        public required ushort DstPort { get; init; }
    }

    public static bool TryParseIpv4Tcp(ReadOnlySpan<byte> packet, out Ipv4TcpView view)
    {
        view = default!;
        if (packet.Length < 40) return false;
        if ((packet[0] >> 4) != 4) return false;
        var ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl + 20) return false;
        if (packet[9] != 6) return false; // TCP

        var dataOff = (packet[ihl + 12] >> 4) * 4;
        if (dataOff < 20 || packet.Length < ihl + dataOff) return false;

        var payloadOff = ihl + dataOff;
        var payloadLen = packet.Length - payloadOff;
        view = new Ipv4TcpView
        {
            IpHeaderLength = ihl,
            TcpHeaderLength = dataOff,
            PayloadOffset = payloadOff,
            PayloadLength = payloadLen,
            DstPort = BinaryPrimitives.ReadUInt16BigEndian(packet[(ihl + 2)..]),
            Seq = BinaryPrimitives.ReadUInt32BigEndian(packet[(ihl + 4)..]),
            TcpFlags = packet[ihl + 13]
        };
        return true;
    }

    public static bool TryParseIpv4Udp(ReadOnlySpan<byte> packet, out Ipv4UdpView view)
    {
        view = default!;
        if (packet.Length < 28) return false;
        if ((packet[0] >> 4) != 4) return false;
        var ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl + 8) return false;
        if (packet[9] != 17) return false; // UDP

        var payloadOff = ihl + 8;
        view = new Ipv4UdpView
        {
            IpHeaderLength = ihl,
            PayloadOffset = payloadOff,
            PayloadLength = packet.Length - payloadOff,
            DstPort = BinaryPrimitives.ReadUInt16BigEndian(packet[(ihl + 2)..])
        };
        return true;
    }

    public static void SetIpv4TotalLength(Span<byte> packet, int ipHeaderLength, int totalLength)
    {
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], (ushort)totalLength);
    }

    public static void SetTcpSeq(Span<byte> packet, int ipHeaderLength, uint seq)
    {
        BinaryPrimitives.WriteUInt32BigEndian(packet[(ipHeaderLength + 4)..], seq);
    }

    public static void SetUdpLength(Span<byte> packet, int ipHeaderLength, int udpLength)
    {
        BinaryPrimitives.WriteUInt16BigEndian(packet[(ipHeaderLength + 4)..], (ushort)udpLength);
    }

    public static void SetTtl(Span<byte> packet, byte ttl) => packet[8] = ttl;

    /// <summary>Build a TCP packet with same headers but a sliced/replaced payload.</summary>
    public static byte[] BuildTcpWithPayload(ReadOnlySpan<byte> original, in Ipv4TcpView view, ReadOnlySpan<byte> newPayload, uint? seqOverride = null)
    {
        var len = view.PayloadOffset + newPayload.Length;
        var buf = new byte[len];
        original[..view.PayloadOffset].CopyTo(buf);
        newPayload.CopyTo(buf.AsSpan(view.PayloadOffset));
        SetIpv4TotalLength(buf, view.IpHeaderLength, len);
        if (seqOverride.HasValue)
            SetTcpSeq(buf, view.IpHeaderLength, seqOverride.Value);
        // zero checksums before helper recalc
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10, 2), 0); // IP
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(view.IpHeaderLength + 16, 2), 0); // TCP
        return buf;
    }

    public static byte[] BuildUdpWithPayload(ReadOnlySpan<byte> original, in Ipv4UdpView view, ReadOnlySpan<byte> newPayload)
    {
        var len = view.PayloadOffset + newPayload.Length;
        var buf = new byte[len];
        original[..view.PayloadOffset].CopyTo(buf);
        newPayload.CopyTo(buf.AsSpan(view.PayloadOffset));
        SetIpv4TotalLength(buf, view.IpHeaderLength, len);
        SetUdpLength(buf, view.IpHeaderLength, 8 + newPayload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(view.IpHeaderLength + 6, 2), 0);
        return buf;
    }
}
