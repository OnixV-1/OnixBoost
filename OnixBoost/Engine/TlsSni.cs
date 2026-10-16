using System;
using System.Text;

namespace OnixBoost.Engine;

internal static class TlsSni
{
    public static bool TryGetSni(ReadOnlySpan<byte> payload, out string sni)
    {
        sni = "";
        // TLS record: 16 03 xx len...
        if (payload.Length < 5 || payload[0] != 0x16 || payload[1] != 0x03) return false;
        var recordLen = (payload[3] << 8) | payload[4];
        if (payload.Length < 5 + Math.Min(recordLen, 4)) return false;

        var hs = payload[5..];
        if (hs.Length < 4 || hs[0] != 0x01) return false; // ClientHello

        // Handshake length (3 bytes) + version(2) + random(32) + session id
        if (hs.Length < 39) return false;
        var sidLen = hs[38];
        var o = 39 + sidLen;
        if (hs.Length < o + 2) return false;
        var csLen = (hs[o] << 8) | hs[o + 1];
        o += 2 + csLen;
        if (hs.Length < o + 1) return false;
        var compLen = hs[o];
        o += 1 + compLen;
        if (hs.Length < o + 2) return false;
        var extLen = (hs[o] << 8) | hs[o + 1];
        o += 2;
        var extEnd = Math.Min(hs.Length, o + extLen);
        while (o + 4 <= extEnd)
        {
            var type = (hs[o] << 8) | hs[o + 1];
            var len = (hs[o + 2] << 8) | hs[o + 3];
            o += 4;
            if (o + len > extEnd) break;
            if (type == 0 && len >= 5) // server_name
            {
                var p = o;
                var listLen = (hs[p] << 8) | hs[p + 1];
                p += 2;
                if (p + listLen > o + len) break;
                if (p + 3 > o + len) break;
                var nameType = hs[p];
                var nameLen = (hs[p + 1] << 8) | hs[p + 2];
                p += 3;
                if (nameType == 0 && p + nameLen <= o + len)
                {
                    sni = Encoding.ASCII.GetString(hs.Slice(p, nameLen));
                    return sni.Length > 0;
                }
            }
            o += len;
        }
        return false;
    }

    public static bool LooksLikeHttp(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return false;
        return payload.StartsWith("GET "u8) || payload.StartsWith("POST "u8) ||
               payload.StartsWith("HEAD "u8) || payload.StartsWith("CONNECT "u8);
    }

    public static bool TryGetHttpHost(ReadOnlySpan<byte> payload, out string host)
    {
        host = "";
        var text = Encoding.ASCII.GetString(payload);
        var idx = text.IndexOf("\r\nHost:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = text.IndexOf("\nHost:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var lineStart = text.IndexOf(':', idx) + 1;
        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = text.Length;
        host = text[lineStart..lineEnd].Trim().TrimEnd('\r');
        var colon = host.IndexOf(':');
        if (colon > 0) host = host[..colon];
        return host.Length > 0;
    }
}
