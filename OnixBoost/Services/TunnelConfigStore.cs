using System;
using System.Collections.Generic;
using System.IO;
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

    private static readonly string[] TwitchDomains =
    {
        "twitch.tv", "twitchcdn.net", "ext-twitch.tv", "assets.twitch.tv",
        "gql.twitch.tv", "jtvnw.net", "ttvnw.net", "ttcdn.net",
        "live-video.net"
    };

    private static readonly string[] TelegramDomains =
    {
        "t.me", "telegram.me", "telegram.org", "telegram.dog", "telegram.space",
        "tdesktop.com", "telegra.ph", "telegram-cdn.org", "cdn-telegram.org",
        "stel.com", "telesco.pe"
    };

    private static readonly string[] TelegramCidrs =
    {
        "91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22", "91.108.16.0/22",
        "91.108.56.0/22", "149.154.160.0/20", "185.76.151.0/24"
    };

    private static readonly string[] Hy2ServerCidrs = { "85.137.63.156/32" };

    /// <summary>Telegram-only TUN: route_address CIDRs, no default-route dump.</summary>
    public static string MaterializeTelegram(string runtimeLeaf)
    {
        var json = ReadEmbeddedOrDisk("client-telegram.json");
        Logger.Info("telegram TUN: client-telegram.json (OnixBoostTG route_address)");
        return WriteRuntime(json, runtimeLeaf);
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
        bool excludeDiscord)
    {
        _ = excludeDiscord;
        // Legacy callers may pass telegram=true — ignore. TG is a separate HY2 process.
        _ = telegram;
        if (!youtube && !twitch)
            throw new InvalidOperationException("MaterializeService requires youtube and/or twitch");

        var json = ReadEmbeddedOrDisk("client-svc.json");
        // PAC already selects hosts; everything that hits mixed goes to HY2.
        json = AppendMixedInboundToHy2(json);

        if (youtube)
            json = AppendDomainHy2(json, YoutubeDomains);
        if (twitch)
            json = AppendDomainHy2(json, TwitchDomains);

        json = ForceFinalDirect(json);
        Logger.Info($"media PROXY+PAC: yt={youtube} tw={twitch} (tg=separate process)");
        return WriteRuntime(json, runtimeLeaf);
    }

    /// <summary>Wintun only when Telegram TUN is in the profile (YT/TW proxy needs no TUN).</summary>
    public static bool NeedsWintun(bool telegram) => telegram;

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
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
            }

            // When TG has its own card OFF, keep TG DC CIDRs off the media TUN
            // so Twitch/YouTube alone cannot pull Telegram through HY2 via IP.
            if (excludeTelegramCidrs)
            {
                foreach (var c in TelegramCidrs)
                    Add(c);
            }

            tun["route_exclude_address"] = excludes;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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

        var disk = Path.Combine(AppPaths.DpiRoot, "core", logicalName);
        if (File.Exists(disk))
            return File.ReadAllText(disk);

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
