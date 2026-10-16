using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnixBoost.Services;

/// <summary>
/// Keeps HY2 JSON out of dpi/core as plaintext in the install folder.
/// Embedded at build; materialized under hidden dpi/core/.rt for the core process.
/// </summary>
public static class TunnelConfigStore
{
    private static readonly byte[] XorKey = Encoding.UTF8.GetBytes("OnixBoost.Tunnel.v1");

    private static readonly string[] YoutubeDomains =
    {
        "youtube.com", "youtu.be", "youtubekids.com", "youtube-nocookie.com",
        "youtube.googleapis.com", "youtubei.googleapis.com",
        "youtubeembeddedplayer.googleapis.com", "googlevideo.com",
        "redirector.googlevideo.com", "ytimg.com", "yt3.ggpht.com", "yt4.ggpht.com",
        "ggpht.com", "wide-youtube.l.google.com", "youtube-ui.l.google.com",
        "yt-video-upload.l.google.com", "jnn-pa.googleapis.com"
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

    public static string Materialize(
        string logicalName,
        string runtimeLeaf,
        bool injectYoutube = false,
        bool injectTelegram = false)
    {
        var json = ReadEmbeddedOrDisk(logicalName);
        // Games: split-tunnel like YouTube (final=direct). Only game proc/domains → DE.
        // Discord stays on its own winws driver — we only exclude its CIDRs from this TUN.
        if (logicalName.Contains("games", StringComparison.OrdinalIgnoreCase))
        {
            json = InjectGamesRoutes(json);
            json = InjectTunDiscordExcludes(json);
            json = InjectDiscordDirectRule(json);
        }
        if (injectYoutube)
            json = InjectDomainRule(json, "onix_youtube", YoutubeDomains);
        if (injectTelegram)
            json = InjectTelegramRoutes(json);
        // YouTube TUN + Discord winws: keep Discord CIDRs off the TUN default route
        if (injectYoutube || logicalName.Contains("youtube", StringComparison.OrdinalIgnoreCase))
            json = InjectTunDiscordExcludes(json);

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
        // Plain JSON for the core binary — do NOT mark Hidden (some builds fail to open hidden configs)
        var live = path + ".run";
        File.WriteAllText(live, json, new UTF8Encoding(false));
        try { File.SetAttributes(live, FileAttributes.Normal); } catch { }
        File.WriteAllBytes(path, payload);
        try { File.SetAttributes(path, FileAttributes.Hidden); } catch { }
        return live;
    }

    /// <summary>
    /// Exclude Discord IP ranges from TUN auto_route so WinDivert DPI keeps Discord
    /// when YouTube HY2 is also up (WinTun otherwise steals those routes).
    /// </summary>
    private static string InjectTunDiscordExcludes(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var inbounds = root["inbounds"]?.AsArray();
            if (inbounds is null || inbounds.Count == 0) return json;
            var tun = inbounds[0]?.AsObject();
            if (tun is null) return json;

            var excludes = tun["route_exclude_address"]?.AsArray() ?? new JsonArray();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in excludes)
            {
                var s = n?.GetValue<string>();
                if (!string.IsNullOrEmpty(s)) existing.Add(s);
            }

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

            foreach (var c in DiscordIpSync.LoadCachedCidrs().Take(400))
            {
                var cidr = c.Contains('/') ? c : c + "/32";
                Add(cidr);
            }

            tun["route_exclude_address"] = excludes;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Logger.Error("InjectTunDiscordExcludes: " + ex.Message);
            return json;
        }
    }

    private static string InjectDomainRule(string json, string marker, string[] domains)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var rules = root["route"]?["rules"]?.AsArray();
            if (rules is null) return json;

            for (var i = rules.Count - 1; i >= 0; i--)
            {
                if (rules[i]?[marker]?.GetValue<bool>() == true)
                    rules.RemoveAt(i);
            }

            var arr = new JsonArray();
            foreach (var d in domains)
                arr.Add(d);

            rules.Insert(0, new JsonObject
            {
                [marker] = true,
                ["domain_suffix"] = arr,
                ["outbound"] = "nl-hy2"
            });
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Logger.Error("InjectDomainRule: " + ex.Message);
            return json;
        }
    }

    private static string InjectTelegramRoutes(string json)
    {
        json = InjectDomainRule(json, "onix_telegram_domains", TelegramDomains);
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var rules = root["route"]?["rules"]?.AsArray();
            if (rules is null) return json;

            for (var i = rules.Count - 1; i >= 0; i--)
            {
                if (rules[i]?["onix_telegram_ips"]?.GetValue<bool>() == true ||
                    rules[i]?["onix_telegram_proc"]?.GetValue<bool>() == true)
                    rules.RemoveAt(i);
            }

            var cidrs = new JsonArray();
            foreach (var c in TelegramCidrs)
                cidrs.Add(c);
            rules.Insert(0, new JsonObject
            {
                ["onix_telegram_ips"] = true,
                ["ip_cidr"] = cidrs,
                ["outbound"] = "nl-hy2"
            });
            rules.Insert(0, new JsonObject
            {
                ["onix_telegram_proc"] = true,
                ["process_name"] = new JsonArray("Telegram.exe", "Telegram"),
                ["outbound"] = "nl-hy2"
            });
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Logger.Error("InjectTelegramRoutes: " + ex.Message);
            return json;
        }
    }

    /// <summary>
    /// Games TUN: browsers + Discord → <c>direct</c> first; everything else (incl. game UDP
    /// attributed to System) → <c>de-hy2</c>. Discord CIDRs are also excluded from the TUN
    /// so WinDivert DPI still sees Discord on the physical NIC.
    /// </summary>
    private static string InjectGamesRoutes(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var rules = root["route"]?["rules"]?.AsArray();
            if (rules is null) return json;

            for (var i = rules.Count - 1; i >= 0; i--)
            {
                if (rules[i]?["onix_games_proc"]?.GetValue<bool>() == true ||
                    rules[i]?["onix_games_domains"]?.GetValue<bool>() == true ||
                    rules[i]?["onix_browser_direct"]?.GetValue<bool>() == true ||
                    rules[i]?["onix_web_direct"]?.GetValue<bool>() == true ||
                    rules[i]?["onix_discord_domains"]?.GetValue<bool>() == true ||
                    rules[i]?["onix_udp_games"]?.GetValue<bool>() == true)
                    rules.RemoveAt(i);
            }

            // Game UDP needs DE; unmatched traffic defaults to DE. Browsers/Discord carved out above.
            if (root["route"] is JsonObject routeObj)
                routeObj["final"] = "de-hy2";

            void Append(JsonObject rule) => rules.Add(rule);

            // Must be first after sniff/dns: keep desktop apps off the Games exit.
            Append(new JsonObject
            {
                ["onix_browser_direct"] = true,
                ["process_name"] = new JsonArray(
                    "chrome.exe", "chrome", "msedge.exe", "msedge", "firefox.exe", "firefox",
                    "opera.exe", "opera", "brave.exe", "brave", "yandex.exe", "browser.exe",
                    "vivaldi.exe", "vivaldi", "chromium.exe",
                    "Discord.exe", "Discord", "DiscordPTB.exe", "DiscordCanary.exe",
                    "DiscordDevelopment.exe", "Update.exe", "Squirrel.exe"),
                ["outbound"] = "direct"
            });

            Append(new JsonObject
            {
                ["onix_discord_domains"] = true,
                ["domain_suffix"] = new JsonArray(
                    "discord.com", "discord.gg", "discord.media", "discordapp.com",
                    "discordapp.net", "discord.co", "discord.gift", "discord.status",
                    "discord-attachments-uploads-prd.storage.googleapis.com"),
                ["outbound"] = "direct"
            });

            var domains = LoadGamesDomains();
            if (domains.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var d in domains)
                    arr.Add(d);
                Append(new JsonObject
                {
                    ["onix_games_domains"] = true,
                    ["domain_suffix"] = arr,
                    ["outbound"] = "de-hy2"
                });
            }

            var procs = LoadGamesProcessNames();
            if (procs.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var p in procs)
                    arr.Add(p);
                Append(new JsonObject
                {
                    ["onix_games_proc"] = true,
                    ["process_name"] = arr,
                    ["outbound"] = "de-hy2"
                });
            }

            Logger.Info(
                $"HY2 games: {procs.Count} procs, {domains.Count} domains → de-hy2; " +
                "browser/discord→direct; final=de-hy2 (game UDP)");
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Logger.Error("InjectGamesRoutes: " + ex.Message);
            return json;
        }
    }

    /// <summary>Discord CIDRs → direct so Flowseal DPI keeps them (not DE HY2).</summary>
    private static string InjectDiscordDirectRule(string json)
    {
        try
        {
            var cidrs = DiscordIpSync.LoadCachedCidrs()
                .Select(c => c.Contains('/') ? c : c + "/32")
                .Take(800)
                .ToList();
            if (cidrs.Count < 10) return json;

            var root = JsonNode.Parse(json);
            if (root is null) return json;
            var rules = root["route"]?["rules"]?.AsArray();
            if (rules is null) return json;

            for (var i = rules.Count - 1; i >= 0; i--)
            {
                if (rules[i]?["onix_discord_ips"]?.GetValue<bool>() == true)
                    rules.RemoveAt(i);
            }

            var arr = new JsonArray();
            foreach (var c in cidrs)
                arr.Add(c);

            rules.Insert(0, new JsonObject
            {
                ["onix_discord_ips"] = true,
                ["ip_cidr"] = arr,
                ["outbound"] = "direct"
            });
            Logger.Info($"HY2 games: Discord {cidrs.Count} CIDRs → direct");
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Logger.Error("InjectDiscordDirectRule: " + ex.Message);
            return json;
        }
    }

    private static List<string> LoadGamesProcessNames()
    {
        var paths = new[]
        {
            Path.Combine(AppPaths.DpiRoot, "core", "games-processes.txt"),
            Path.Combine(AppPaths.DpiRoot, "lists", "games-processes.txt"),
        };
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.Contains('*') || line.Contains('!')) continue; // no globs / odd names
                // Keep stable process names only
                if (!(line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                      line.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                    continue;
                set.Add(line);
                // Windows sometimes reports without extension
                var noExt = Path.GetFileNameWithoutExtension(line);
                if (!string.IsNullOrEmpty(noExt))
                    set.Add(noExt);
            }
        }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(500).ToList();
    }

    private static List<string> LoadGamesDomains()
    {
        var files = new[]
        {
            Path.Combine(AppPaths.DpiRoot, "lists", "list-games.txt"),
            Path.Combine(AppPaths.DpiRoot, "lists", "list-steam.txt"),
        };
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            if (!File.Exists(path)) continue;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("*."))
                    line = line[2..];
                line = line.TrimStart('.');
                if (line.Length < 3 || line.Contains(' ') || !line.Contains('.')) continue;
                set.Add(line);
            }
        }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(800).ToList();
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
