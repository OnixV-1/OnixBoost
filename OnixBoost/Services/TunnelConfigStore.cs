using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnixBoost.Services;

/// <summary>
/// YouTube/Twitch: local mixed SOCKS/HTTP (127.0.0.1:17890) + system PAC — no TUN, no game routes.
/// Telegram-only: OnixBoostTG route_address TUN.
/// YT/TW + TG: proxy inbound + optional TG TUN in the same core process.
/// </summary>
public static class TunnelConfigStore
{
    private static readonly byte[] XorKey = Encoding.UTF8.GetBytes("OnixBoost.Tunnel.v1");

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        TypeInfoResolver = JsonSerializerOptions.Default.TypeInfoResolver
    };

    /// <summary>
    /// Full YouTube surface: SPA dies without gstatic/ggpht/googleusercontent;
    /// video dies without googlevideo/gvt1. Keep suffixes broad, not host-exact.
    /// </summary>
    private static readonly string[] YoutubeDomains =
    {
        "youtube.com", "youtu.be", "yt.be", "youtubekids.com", "youtube-nocookie.com",
        "youtube.googleapis.com", "youtubei.googleapis.com",
        "youtubeembeddedplayer.googleapis.com", "googlevideo.com",
        "redirector.googlevideo.com", "ytimg.com", "ggpht.com",
        "googleusercontent.com", "gvt1.com", "gstatic.com",
        "wide-youtube.l.google.com", "youtube-ui.l.google.com",
        "yt-video-upload.l.google.com", "ytimg.l.google.com",
        "jnn-pa.googleapis.com", "googleapis.com"
    };

    /// <summary>Cloudflare-ish Discord edges only — never 34.x/35.x (GCP overlaps YouTube).</summary>
    private static readonly string[] SafeDiscordCidrs =
    {
        "162.159.128.0/17", "104.16.0.0/13", "172.64.0.0/13", "5.200.14.0/24"
    };

    /// <summary>
    /// Discord voice/RTC fallbacks (own AS + GCP slices used for WebRTC).
    /// CF-only TUN mutes when Ready hands a 34/35/66.22 UDP endpoint.
    /// Curated /16s — not GCP /12 dumps (those steal half of Google into OnixBoostDC).
    /// </summary>
    private static readonly string[] DiscordVoiceCidrs =
    {
        "66.22.0.0/16",
        "34.0.0.0/16",
        "34.1.0.0/16",
        "34.2.0.0/16",
        "35.207.0.0/16",
        "35.212.0.0/16",
        "35.213.0.0/16",
        "35.214.0.0/16",
        "35.215.0.0/16",
        "35.217.0.0/16",
        "35.219.0.0/16"
    };

    private static readonly string[] TwitchDomains =
    {
        "twitch.tv", "twitchcdn.net", "ext-twitch.tv", "assets.twitch.tv",
        "gql.twitch.tv", "jtvnw.net", "ttvnw.net", "ttcdn.net",
        "live-video.net"
    };

    /// <summary>Linux Soft fallback (HY2+PAC) when nfqws/nft privileges are missing.</summary>
    private static readonly string[] DiscordDomains =
    {
        "discord.com", "discord.gg", "discordapp.com", "discordapp.net",
        "discord.media", "discordcdn.com", "discord.gift", "discord.gifts",
        "discordactivities.com", "discordsays.com", "discordstatus.com",
        "discord.co", "discord.app", "dis.gd", "gateway.discord.gg",
        "media.discordapp.net", "images-ext-1.discordapp.net",
        "discord-activities.com", "discordpartygames.com", "discordmerch.com"
    };

    private static readonly string[] TelegramDomains =
    {
        "t.me", "telegram.me", "telegram.org", "telegram.dog", "telegram.space",
        "tdesktop.com", "telegra.ph", "telegram-cdn.org", "cdn-telegram.org",
        "stel.com", "telesco.pe"
    };

    private static readonly string[] TelegramCidrs =
    {
        // Broad Telegram DC space — narrow /22s miss new pops → HY2 "online" but TG stays direct.
        // IPv6 required: Telegram Desktop prefers v6; v4-only TUN = ONLINE but "Загрузка...".
        "91.108.0.0/16",
        "149.154.160.0/20",
        "185.76.151.0/24",
        "95.161.64.0/20",
        "2001:b28:f23c::/48",
        "2001:b28:f23d::/48",
        "2001:b28:f23f::/48",
        "2001:67c:4e8::/48",
    };

    private static readonly string[] Hy2ServerCidrs = { "194.76.154.176/32" };
    /// <summary>Canonical HY2 host (FR). Always pinned — stale dpi/core/*.json must not keep dead VSYS/NL2 IPs.</summary>
    private const string Hy2ServerHost = "194.76.154.176";

    /// <summary>
    /// Split HY2 listen ports so Discord + Telegram TUNs do not share one QUIC inbound
    /// (same password + same port under load → voice mute / session flap).
    /// </summary>
    // Same split as stable 2.9.4 on NL: Telegram → 3443, media/Discord → 443.
    private const int Hy2PortTelegram = 3443;
    private const int Hy2PortDiscord = 443;

    /// <summary>Telegram-only TUN: route_address CIDRs, no default-route dump.</summary>
    public static string MaterializeTelegram(string runtimeLeaf)
    {
        var json = ReadEmbeddedOrDisk("client-telegram.json");
        json = PinTunHy2Outbound(json, Hy2PortTelegram);
        json = HardenWindowsTelegramTun(json);
        Logger.Info("telegram TUN: client-telegram.json (OnixBoostTG route_address)");
        return WriteRuntime(json, runtimeLeaf);
    }

    /// <summary>
    /// Windows Wintun: match stable 2.9.4 stack=system, but drop IPv6 TUN addresses.
    /// Many Win setups (IPv6 off / broken) die with FATAL start inbound/tun when the
    /// profile asks for fdfe:…/126 — NL vs FR is irrelevant, create fails locally first.
    /// </summary>
    private static string HardenWindowsTelegramTun(string json)
    {
        if (!PlatformOs.IsWindows) return json;
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var inbounds = root["inbounds"]?.AsArray();
            if (inbounds is null) return json;
            foreach (var n in inbounds)
            {
                if (n is not JsonObject tun) continue;
                if (!string.Equals(tun["type"]?.GetValue<string>(), "tun", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Same as 2.9.4
                tun["stack"] = "system";
                tun["strict_route"] = false;
                tun["interface_name"] = "OnixBoostTG";

                // IPv4-only endpoint on the NIC — avoid fdfe:: create failures
                tun["address"] = new JsonArray("172.19.0.5/30");

                if (tun["route_address"] is JsonArray routes)
                {
                    var v4 = new JsonArray();
                    foreach (var r in routes)
                    {
                        var s = r?.GetValue<string>() ?? "";
                        if (s.Length == 0 || s.Contains(':')) continue;
                        v4.Add(s);
                    }
                    if (v4.Count > 0) tun["route_address"] = v4;
                }

                var excludes = new JsonArray();
                foreach (var c in Hy2ServerCidrs) excludes.Add(c);
                // Keep Discord Soft/probe off the TG TUN even if CF ranges overlap oddly
                foreach (var c in SafeDiscordCidrs) excludes.Add(c);
                excludes.Add("162.159.0.0/16");
                foreach (var c in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8" })
                    excludes.Add(c);
                tun["route_exclude_address"] = excludes;
            }

            // Route rules: drop IPv6 telegram CIDRs on Windows (no v6 TUN)
            if (root["route"]?["rules"] is JsonArray rules)
            {
                foreach (var rule in rules)
                {
                    if (rule is not JsonObject o) continue;
                    if (o["ip_cidr"] is not JsonArray cidrs) continue;
                    var v4 = new JsonArray();
                    foreach (var c in cidrs)
                    {
                        var s = c?.GetValue<string>() ?? "";
                        if (s.Length == 0 || s.Contains(':')) continue;
                        v4.Add(s);
                    }
                    o["ip_cidr"] = v4;
                }
            }

            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("HardenWindowsTelegramTun: " + ex.Message);
            return json;
        }
    }

    /// <summary>Discord TUN + HY2 (Linux Electron Soft miss / Windows Soft gateway miss).</summary>
    public static string MaterializeDiscord(string runtimeLeaf)
    {
        var json = ReadEmbeddedOrDisk("client-discord.json");
        json = InjectDiscordTunRouteAddresses(json);
        json = PinTunHy2Outbound(json, Hy2PortDiscord);
        json = HardenWindowsDiscordTun(json);
        Logger.Info("discord TUN: client-discord.json (OnixBoostDC route_address)");
        return WriteRuntime(json, runtimeLeaf);
    }

    /// <summary>Windows: keep IPv4 + system stack (same lessons as Telegram TUN).</summary>
    private static string HardenWindowsDiscordTun(string json)
    {
        if (!PlatformOs.IsWindows) return json;
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var inbounds = root["inbounds"]?.AsArray();
            if (inbounds is null) return json;
            foreach (var n in inbounds)
            {
                if (n is not JsonObject tun) continue;
                if (!string.Equals(tun["type"]?.GetValue<string>(), "tun", StringComparison.OrdinalIgnoreCase))
                    continue;
                tun["stack"] = "system";
                tun["strict_route"] = false;
                tun["interface_name"] = "OnixBoostDC";
                tun["address"] = new JsonArray("172.19.0.9/30");
            }
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("HardenWindowsDiscordTun: " + ex.Message);
            return json;
        }
    }

    /// <summary>
    /// Discord TUN catch = Cloudflare + curated voice /16s (GCP + 66.22).
    /// Do NOT dump 600+ Amnezia rows into FIB — dual TUN with Telegram then flaps.
    /// Skip fat GCP /12–/15 dumps that steal half of Google into OnixBoostDC.
    /// </summary>
    private static string InjectDiscordTunRouteAddresses(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var inbounds = root["inbounds"]?.AsArray();
            if (inbounds is null) return json;
            JsonObject? tun = null;
            foreach (var n in inbounds)
            {
                if (n is JsonObject o &&
                    string.Equals(o["type"]?.GetValue<string>(), "tun", StringComparison.OrdinalIgnoreCase))
                {
                    tun = o;
                    break;
                }
            }
            if (tun is null) return json;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arr = new JsonArray();
            void Add(string c)
            {
                c = (c ?? "").Trim();
                if (c.Length == 0 || !seen.Add(c)) return;
                arr.Add(c);
            }

            foreach (var c in SafeDiscordCidrs) Add(c);
            Add("162.159.0.0/16");
            Add("162.158.0.0/15");
            Add("104.16.0.0/13");
            Add("172.64.0.0/13");
            Add("5.200.14.0/24");
            Add("188.114.96.0/20");
            foreach (var c in DiscordVoiceCidrs) Add(c);

            tun["route_address"] = arr;

            // Never tunnel the HY2 server or LAN into itself
            var excludes = new JsonArray();
            foreach (var c in Hy2ServerCidrs) excludes.Add(c);
            foreach (var c in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8" })
                excludes.Add(c);
            tun["route_exclude_address"] = excludes;

            // WebRTC UDP has no SNI — sniff only burns 800ms and drops early voice packets
            StripDiscordSniff(root);

            // UDP voice must hit ip_cidr → hy2, not final=direct
            PatchDiscordRouteIpCidrs(root, seen);

            Logger.Info(
                $"discord TUN route_address={seen.Count} excludes={excludes.Count} (CF+GCP /16s, no sniff)");
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("InjectDiscordTunRouteAddresses: " + ex.Message);
            return json;
        }
    }

    /// <summary>
    /// Pin HY2 to a physical NIC + dedicated port so a sibling TUN (TG/DC) cannot
    /// steal the QUIC path via auto_detect_interface.
    /// </summary>
    private static string PinTunHy2Outbound(string json, int hy2Port)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var iface = DetectPhysicalInterface();

            var outbounds = root["outbounds"]?.AsArray();
            if (outbounds != null)
            {
                foreach (var n in outbounds)
                {
                    if (n is not JsonObject o) continue;
                    if (!string.Equals(o["type"]?.GetValue<string>(), "hysteria2",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    o["server"] = Hy2ServerHost;
                    o["server_port"] = hy2Port;
                    if (!string.IsNullOrEmpty(iface))
                        o["bind_interface"] = iface;
                }
            }

            var route = root["route"] as JsonObject ?? new JsonObject();
            root["route"] = route;
            if (!string.IsNullOrEmpty(iface))
            {
                route["auto_detect_interface"] = false;
                route["default_interface"] = iface;
            }
            else
            {
                route["auto_detect_interface"] = true;
                route.Remove("default_interface");
            }

            Logger.Info($"TUN HY2 pin: server={Hy2ServerHost} port={hy2Port} iface={iface ?? "(auto)"}");
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("PinTunHy2Outbound: " + ex.Message);
            return json;
        }
    }

    /// <summary>Default route's physical device — skip OnixBoost*/tun/wg/lo.</summary>
    private static string? DetectPhysicalInterface()
    {
        if (!PlatformOs.IsLinux) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ip",
                Arguments = "-4 route show default",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i + 1 < parts.Length; i++)
                {
                    if (!parts[i].Equals("dev", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = parts[i + 1];
                    if (name.Equals("lo", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("OnixBoost", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("tun", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("wg", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("docker", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("br-", StringComparison.OrdinalIgnoreCase)) continue;
                    return name;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DetectPhysicalInterface: " + ex.Message);
        }
        return null;
    }

    private static void StripDiscordSniff(JsonNode root)
    {
        var rules = root["route"]?["rules"]?.AsArray();
        if (rules is null) return;
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is JsonObject o &&
                string.Equals(o["action"]?.GetValue<string>(), "sniff", StringComparison.OrdinalIgnoreCase))
                rules.RemoveAt(i);
        }
    }

    private static void PatchDiscordRouteIpCidrs(JsonNode root, HashSet<string> cidrs)
    {
        var rules = root["route"]?["rules"]?.AsArray();
        if (rules is null) return;
        foreach (var rule in rules)
        {
            if (rule is not JsonObject o) continue;
            if (o["ip_cidr"] is null) continue;
            var ipArr = new JsonArray();
            var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cidrs)
            {
                if (!local.Add(c)) continue;
                ipArr.Add(c);
            }
            o["ip_cidr"] = ipArr;
            return;
        }
    }

    /// <summary>
    /// YT/Twitch via mixed proxy (no Windows default route, no Telegram TUN).
    /// Telegram always uses <see cref="MaterializeTelegram"/> in a separate process.
    /// </summary>
    public static string MaterializeService(
        string runtimeLeaf,
        bool youtube,
        bool twitch,
        bool telegram,
        bool excludeDiscord,
        bool discord = false)
    {
        _ = excludeDiscord;
        // Legacy callers may pass telegram=true — ignore. TG is a separate HY2 process.
        _ = telegram;
        if (!youtube && !twitch && !discord)
            throw new InvalidOperationException("MaterializeService requires youtube, twitch, and/or discord");

        var json = ReadEmbeddedOrDisk("client-svc.json");
        // PAC already selects hosts; everything that hits mixed goes to HY2.
        json = AppendMixedInboundToHy2(json);
        json = PinTunHy2Outbound(json, Hy2PortDiscord);

        if (youtube)
            json = AppendDomainHy2(json, YoutubeDomains);
        if (twitch)
            json = AppendDomainHy2(json, TwitchDomains);
        if (discord)
            json = AppendDomainHy2(json, DiscordDomains);

        json = ForceFinalDirect(json);
        Logger.Info($"media PROXY+PAC: yt={youtube} tw={twitch} discord={discord} (tg=separate)");
        return WriteRuntime(json, runtimeLeaf);
    }

    /// <summary>Wintun only on Windows when Telegram TUN is in the profile (YT/TW proxy needs no TUN).</summary>
    public static bool NeedsWintun(bool telegram) =>
        telegram && PlatformOs.IsWindows;

    public static string Materialize(
        string logicalName,
        string runtimeLeaf,
        bool injectYoutube = false,
        bool injectTelegram = false,
        bool injectTwitch = false,
        bool excludeDiscordCidrs = false)
    {
        if (logicalName.Contains("telegram", StringComparison.OrdinalIgnoreCase) &&
            !injectYoutube && !injectTwitch)
            return MaterializeTelegram(runtimeLeaf);

        if (injectTelegram && !injectYoutube && !injectTwitch &&
            !logicalName.Contains("youtube", StringComparison.OrdinalIgnoreCase) &&
            !logicalName.Contains("twitch", StringComparison.OrdinalIgnoreCase) &&
            !logicalName.Contains("media", StringComparison.OrdinalIgnoreCase) &&
            !logicalName.Contains("svc", StringComparison.OrdinalIgnoreCase))
            return MaterializeTelegram(runtimeLeaf);

        if (logicalName.Contains("media", StringComparison.OrdinalIgnoreCase) ||
            logicalName.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            logicalName.Contains("twitch", StringComparison.OrdinalIgnoreCase) ||
            logicalName.Contains("svc", StringComparison.OrdinalIgnoreCase) ||
            injectYoutube || injectTwitch)
        {
            var yt = injectYoutube || logicalName.Contains("youtube", StringComparison.OrdinalIgnoreCase);
            var tw = injectTwitch || logicalName.Contains("twitch", StringComparison.OrdinalIgnoreCase);
            // Telegram never merges into media process.
            return MaterializeService(runtimeLeaf, yt, tw, telegram: false, excludeDiscordCidrs);
        }

        return WriteRuntime(ReadEmbeddedOrDisk(logicalName), runtimeLeaf);
    }

    /// <summary>Self-check for CI / pre-ship: twitch-only must not contain telegram domains.</summary>
    public static string BuildServiceJsonForTest(bool youtube, bool twitch, bool telegram, bool excludeDiscord)
    {
        _ = excludeDiscord;
        var json = ReadEmbeddedOrDisk("client-svc.json");
        if (youtube) json = AppendDomainHy2(json, YoutubeDomains);
        if (twitch) json = AppendDomainHy2(json, TwitchDomains);
        if (telegram)
        {
            json = AppendTelegramTunInbound(json);
            json = AppendDomainHy2(json, TelegramDomains);
            json = AppendTelegramCidrs(json);
        }
        return ForceFinalDirect(json);
    }

    private static string AppendMixedInboundToHy2(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var rules = root["route"]?["rules"]?.AsArray();
            if (rules is null) return json;
            rules.Add(new JsonObject
            {
                ["inbound"] = "mixed-media",
                ["outbound"] = "nl-hy2"
            });
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("AppendMixedInboundToHy2: " + ex.Message);
            return json;
        }
    }

    private static string AppendTelegramTunInbound(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var inbounds = root["inbounds"]?.AsArray();
            if (inbounds is null) return json;

            var cidrs = new JsonArray();
            foreach (var c in TelegramCidrs)
                cidrs.Add(c);

            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-tg",
                ["interface_name"] = "OnixBoostTG",
                ["address"] = new JsonArray("172.19.0.5/30"),
                ["mtu"] = 1400,
                ["auto_route"] = true,
                ["strict_route"] = false,
                ["stack"] = "system",
                ["route_address"] = cidrs
            });

            var rules = root["route"]?["rules"]?.AsArray();
            rules?.Add(new JsonObject
            {
                ["inbound"] = "tun-tg",
                ["action"] = "sniff",
                ["timeout"] = "800ms"
            });

            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("AppendTelegramTunInbound: " + ex.Message);
            return json;
        }
    }

    /// <summary>hijack-dns must run before sniff so domain routing sees real names.</summary>
    private static string FixRouteRuleOrder(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var rules = root["route"]?["rules"]?.AsArray();
            if (rules is null || rules.Count == 0) return json;

            JsonNode? dns = null;
            JsonNode? sniff = null;
            var rest = new List<JsonNode?>();
            foreach (var r in rules)
            {
                if (r is null) continue;
                if (r["action"]?.GetValue<string>() == "hijack-dns" ||
                    r["protocol"]?.GetValue<string>() == "dns")
                {
                    dns = r;
                    continue;
                }
                if (r["action"]?.GetValue<string>() == "sniff")
                {
                    sniff = r;
                    continue;
                }
                rest.Add(r);
            }

            var ordered = new JsonArray();
            if (dns != null) ordered.Add(dns);
            if (sniff != null) ordered.Add(sniff);
            foreach (var r in rest) ordered.Add(r);
            root["route"]!["rules"] = ordered;
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("FixRouteRuleOrder: " + ex.Message);
            return json;
        }
    }

    /// <summary>Resolve YouTube names via 8.8.8.8 through HY2 — ISP DNS often poisons them.</summary>
    private static string PatchYoutubeDnsViaHy2(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            if (root["dns"] is not JsonObject dns)
                return json;

            var servers = dns["servers"]?.AsArray() ?? new JsonArray();
            var hasHy2Dns = false;
            foreach (var s in servers)
            {
                if (s?["tag"]?.GetValue<string>() == "dns-hy2")
                {
                    hasHy2Dns = true;
                    break;
                }
            }

            if (!hasHy2Dns)
            {
                servers.Add(new JsonObject
                {
                    ["type"] = "udp",
                    ["tag"] = "dns-hy2",
                    ["server"] = "8.8.8.8",
                    ["detour"] = "nl-hy2"
                });
                dns["servers"] = servers;
            }

            var arr = new JsonArray();
            foreach (var d in YoutubeDomains)
                arr.Add(d);

            var rules = dns["rules"]?.AsArray() ?? new JsonArray();
            rules.Insert(0, new JsonObject
            {
                ["domain_suffix"] = arr,
                ["server"] = "dns-hy2"
            });
            dns["rules"] = rules;
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("PatchYoutubeDnsViaHy2: " + ex.Message);
            return json;
        }
    }

    private static string AppendDomainHy2(string json, string[] domains)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var rules = root["route"]?["rules"]?.AsArray();
            if (rules is null) return json;

            var arr = new JsonArray();
            foreach (var d in domains)
                arr.Add(d);

            rules.Add(new JsonObject
            {
                ["domain_suffix"] = arr,
                ["outbound"] = "nl-hy2"
            });
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("AppendDomainHy2: " + ex.Message);
            return json;
        }
    }

    private static string AppendTelegramCidrs(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var rules = root["route"]?["rules"]?.AsArray();
            if (rules is null) return json;

            var cidrs = new JsonArray();
            foreach (var c in TelegramCidrs)
                cidrs.Add(c);

            rules.Add(new JsonObject
            {
                ["ip_cidr"] = cidrs,
                ["outbound"] = "nl-hy2"
            });
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("AppendTelegramCidrs: " + ex.Message);
            return json;
        }
    }

    private static string ForceFinalDirect(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            if (root["route"] is JsonObject route)
            {
                route["final"] = "direct";
                route["auto_detect_interface"] = true;
            }

            if (root["outbounds"] is JsonArray outs && outs.Count > 1)
            {
                var di = -1;
                for (var i = 0; i < outs.Count; i++)
                {
                    if (outs[i]?["tag"]?.GetValue<string>() == "direct")
                    {
                        di = i;
                        break;
                    }
                }

                if (di > 0)
                {
                    var d = outs[di];
                    outs.RemoveAt(di);
                    outs.Insert(0, d);
                }
            }

            return root.ToJsonString(IndentedJson);
        }
        catch { return json; }
    }

    private static string InjectTunExcludes(string json, bool excludeDiscord, bool excludeTelegramCidrs)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var tun = root["inbounds"]?[0]?.AsObject();
            if (tun is null) return json;

            var excludes = new JsonArray();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string cidr)
            {
                if (existing.Add(cidr))
                    excludes.Add(cidr);
            }

            foreach (var priv in new[]
                     {
                         "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
                         "127.0.0.0/8", "169.254.0.0/16", "224.0.0.0/4"
                     })
                Add(priv);

            foreach (var s in Hy2ServerCidrs)
                Add(s);

            // Valve / Steam — never pull game UDP into media TUN even if ranges overlap.
            foreach (var c in LoadCidrFile("steam-exclude-cidrs.txt"))
                Add(c);

            if (excludeDiscord)
            {
                foreach (var c in SafeDiscordCidrs)
                    Add(c);
                foreach (var c in DiscordVoiceCidrs)
                    Add(c);
            }

            // When TG has its own card OFF, keep TG DC CIDRs off the media TUN
            // so Twitch/YouTube alone cannot pull Telegram through HY2 via IP.
            if (excludeTelegramCidrs)
            {
                foreach (var c in TelegramCidrs)
                    Add(c);
            }

            tun["route_exclude_address"] = excludes;
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("InjectTunExcludes: " + ex.Message);
            return json;
        }
    }

    /// <summary>
    /// Steer ONLY media/TG CIDRs into TUN (same model as OnixBoostTG).
    /// Without route_address, auto_route steals the default route → CS2 matchmaking dies.
    /// </summary>
    private static string ApplySelectiveRouteAddress(
        string json, bool youtube, bool twitch, bool telegram)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var tun = root["inbounds"]?[0]?.AsObject();
            if (tun is null) return json;

            var cidrs = new List<string>();
            if (youtube)
                cidrs.AddRange(LoadCidrFile("google-cidrs.txt"));
            if (twitch)
                cidrs.AddRange(LoadCidrFile("cloudflare-cidrs.txt"));
            if (telegram)
                cidrs.AddRange(TelegramCidrs);

            if (cidrs.Count == 0)
            {
                Logger.Error("ApplySelectiveRouteAddress: no CIDRs — refusing catch-all TUN");
                return json;
            }

            var arr = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cidrs)
            {
                if (seen.Add(c))
                    arr.Add(c);
            }

            tun["auto_route"] = true;
            tun["route_address"] = arr;
            tun["mtu"] = 1400;
            Logger.Info($"media TUN route_address count={seen.Count} (selective, not 0.0.0.0/0)");
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("ApplySelectiveRouteAddress: " + ex.Message);
            return json;
        }
    }

    private static string InjectGameProcessBypass(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            if (root["route"] is not JsonObject route) return json;
            route["find_process"] = true;
            var rules = route["rules"]?.AsArray();
            if (rules is null) return json;

            var procs = new JsonArray
            {
                "cs2.exe", "csgo.exe", "steam.exe", "steamwebhelper.exe",
                "GameOverlayUI.exe", "streaming_client.exe"
            };
            // Insert after dns/sniff (first two after FixRouteRuleOrder).
            var insertAt = Math.Min(2, rules.Count);
            rules.Insert(insertAt, new JsonObject
            {
                ["process_name"] = procs,
                ["outbound"] = "direct"
            });
            return root.ToJsonString(IndentedJson);
        }
        catch (Exception ex)
        {
            Logger.Error("InjectGameProcessBypass: " + ex.Message);
            return json;
        }
    }

    private static IEnumerable<string> LoadCidrFile(string fileName)
    {
        var path = Path.Combine(AppPaths.DpiRoot, "core", fileName);
        if (!File.Exists(path))
        {
            // Fallback: next to embedded configs during publish
            path = Path.Combine(AppContext.BaseDirectory, "dpi", "core", fileName);
        }
        if (!File.Exists(path))
        {
            Logger.Error("CIDR file missing: " + fileName);
            yield break;
        }

        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            yield return t;
        }
    }

    private static string WriteRuntime(string json, string runtimeLeaf)
    {
        var dir = Path.Combine(AppPaths.DpiRoot, "core", ".rt");
        Directory.CreateDirectory(dir);
        try
        {
            var di = new DirectoryInfo(dir);
            if ((di.Attributes & FileAttributes.Hidden) == 0)
                di.Attributes |= FileAttributes.Hidden;
        }
        catch { }

        var path = Path.Combine(dir, runtimeLeaf);
        var payload = Obfuscate(Encoding.UTF8.GetBytes(json));
        var live = path + ".run";
        File.WriteAllText(live, json, new UTF8Encoding(false));
        try { File.SetAttributes(live, FileAttributes.Normal); } catch { }
        File.WriteAllBytes(path, payload);
        try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
        return live;
    }

    public static void ScrubRuntime()
    {
        try
        {
            var dir = Path.Combine(AppPaths.DpiRoot, "core", ".rt");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir))
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Delete only one engine's runtime leaf — keep the sibling HY2 config intact.</summary>
    public static void ScrubRuntimeLeaf(string runtimeLeaf)
    {
        try
        {
            var dir = Path.Combine(AppPaths.DpiRoot, "core", ".rt");
            if (!Directory.Exists(dir)) return;
            foreach (var f in new[]
                     {
                         Path.Combine(dir, runtimeLeaf),
                         Path.Combine(dir, runtimeLeaf + ".run")
                     })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string ReadEmbeddedOrDisk(string logicalName)
    {
        // Prefer on-disk dpi/core so Update.zip can hotfix routing without full reship.
        var disk = Path.Combine(AppPaths.DpiRoot, "core", logicalName);
        if (File.Exists(disk))
            return File.ReadAllText(disk);

        var asm = Assembly.GetExecutingAssembly();
        var res = "OnixBoost.Tunnel." + logicalName;
        using (var s = asm.GetManifestResourceStream(res))
        {
            if (s != null)
            {
                using var r = new StreamReader(s, Encoding.UTF8);
                return r.ReadToEnd();
            }
        }

        throw new FileNotFoundException("Tunnel config missing: " + logicalName);
    }

    private static byte[] Obfuscate(byte[] data)
    {
        var outBuf = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            outBuf[i] = (byte)(data[i] ^ XorKey[i % XorKey.Length]);
        return outBuf;
    }
}
