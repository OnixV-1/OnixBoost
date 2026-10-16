using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// Magila/zapret2 launcher: winws2.exe + Lua auto_strategy (strategy_super_c) + optional zapret_helper.
/// Strategies from unblock-youtube-discord v1.7 / configs/strategy/1.6.2.
/// </summary>
public sealed class Zapret2Engine
{
    private Process? _winws2;
    private Process? _helper;

    public bool IsRunning =>
        Process.GetProcessesByName("winws2").Length > 0 ||
        (_winws2 is { HasExited: false });

    private static string Z2Root => Path.Combine(AppPaths.DpiRoot, "stack");
    private static string Binaries => Path.Combine(Z2Root, "binaries");
    private static string Configs => Path.Combine(Z2Root, "configs");
    private static string UserDir => Path.Combine(Z2Root, "user");
    private static string Winws2Exe => Path.Combine(Binaries, "winws2.exe");
    private static string HelperExe => Path.Combine(Z2Root, "bin", "OnixBoost.exe");

    public static IReadOnlyList<string> ListStrategyFiles()
    {
        var dir = Path.Combine(Z2Root, "configs", "strategy", "1.6.2");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.config")
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task StartAsync(string strategyName = "strategy_super_c.config")
    {
        await StopAsync();
        await WinDivertCleanup.RunAsync();

        if (!File.Exists(Winws2Exe))
            throw new FileNotFoundException("winws2.exe missing — unpack full zip (dpi/stack)", Winws2Exe);

        EnsureUserLists();
        // Prefer synced Amnezia discord IP set if present
        try
        {
            var synced = DiscordIpSync.Zapret2DiscordIpSet;
            if (File.Exists(synced) && new FileInfo(synced).Length > 32)
            {
                var dst = Path.Combine(UserDir, "ip-set", "discord.list");
                File.Copy(synced, dst, true);
            }
        }
        catch { }

        var args = BuildArgs(strategyName);
        File.WriteAllLines(Path.Combine(AppPaths.Root, "last-stack-args.txt"), args);
        Logger.Info($"stack args count={args.Count}");

        // Helper first (auto_strategy helper_fails) — best-effort
        if (File.Exists(HelperExe))
        {
            try
            {
                _helper = Process.Start(new ProcessStartInfo
                {
                    FileName = HelperExe,
                    WorkingDirectory = Path.GetDirectoryName(HelperExe)!,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Logger.Info("OnixBoost helper started");
                await Task.Delay(400);
            }
            catch (Exception ex)
            {
                Logger.Error("OnixBoost helper: " + ex.Message);
            }
        }

        // Launch via cmd; cwd=binaries so all args stay relative (ASCII-safe under Cyrillic folders)
        var bat = Path.Combine(Binaries, "_onix_zapret2.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("cd /d \"%~dp0\"");
        sb.Append("winws2.exe");
        foreach (var a in args)
        {
            if (a.Contains('=') && a.Contains(' '))
            {
                var eq = a.IndexOf('=');
                sb.Append(' ').Append(a[..eq]).Append("=\"").Append(a[(eq + 1)..]).Append('"');
            }
            else if (a.Contains(' '))
                sb.Append(" \"").Append(a).Append('"');
            else
                sb.Append(' ').Append(a);
        }
        sb.AppendLine();
        await File.WriteAllTextAsync(bat, sb.ToString(), PathAscii.CmdEncoding);

        var log = Path.Combine(Binaries, "winws2.log");
        try { File.Delete(log); } catch { }
        // also copy tip to app root for UI
        var logRoot = Path.Combine(AppPaths.Root, "winws2.log");

        _winws2 = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // relative names only — absolute bat/log paths break under Cyrillic
            Arguments = "/c \"_onix_zapret2.cmd >winws2.log 2>&1\"",
            WorkingDirectory = Binaries,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start winws2");

        await Task.Delay(1200);
        if (Process.GetProcessesByName("winws2").Length == 0)
        {
            var tip = File.Exists(log) ? File.ReadAllText(log) : "(no winws2.log)";
            try { File.WriteAllText(logRoot, tip); } catch { }
            throw new InvalidOperationException(
                "winws2 did not stay up.\n" + tip[..Math.Min(800, tip.Length)]);
        }
        try { if (File.Exists(log)) File.Copy(log, logRoot, true); } catch { }

        Logger.Info("DPI stack RUNNING (strategy_super_c + auto_strategy)");
    }

    public async Task StopAsync()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("winws2"))
            {
                try { p.Kill(true); p.WaitForExit(2000); } catch { }
            }
            ProcessNames.KillStackWorkers();
        }
        catch { }

        try
        {
            if (_winws2 is { HasExited: false }) { _winws2.Kill(true); _winws2.WaitForExit(2000); }
        }
        catch { }

        try
        {
            if (_helper is { HasExited: false }) { _helper.Kill(true); _helper.WaitForExit(2000); }
        }
        catch { }

        _winws2 = null;
        _helper = null;
        await Task.CompletedTask;
    }

    private void EnsureUserLists()
    {
        Directory.CreateDirectory(Path.Combine(UserDir, "blacklist"));
        Directory.CreateDirectory(Path.Combine(UserDir, "ip-set"));

        var services = new[] { "cloudflare", "discord", "youtube", "game_mod", "amazon" };
        var allHosts = new List<string>();
        var allIps = new List<string>();

        foreach (var svc in services)
        {
            var hosts = ReadList(Path.Combine(Configs, "blacklist", svc + ".list")).ToList();
            File.WriteAllLines(Path.Combine(UserDir, "blacklist", svc + ".list"), hosts);
            allHosts.AddRange(hosts);

            var ips = ReadList(Path.Combine(Configs, "ip-set", svc + ".list")).ToList();
            File.WriteAllLines(Path.Combine(UserDir, "ip-set", svc + ".list"), ips);
            allIps.AddRange(ips);
        }

        // Magila use_in>>game_mod: merge cloudflare/amazon hosts+ips into game_mod lists
        foreach (var donor in new[] { "cloudflare", "amazon" })
        {
            AppendUnique(
                Path.Combine(UserDir, "blacklist", "game_mod.list"),
                ReadList(Path.Combine(UserDir, "blacklist", donor + ".list")));
            AppendUnique(
                Path.Combine(UserDir, "ip-set", "game_mod.list"),
                ReadList(Path.Combine(UserDir, "ip-set", donor + ".list")));
        }

        File.WriteAllLines(
            Path.Combine(UserDir, "blacklist", "all.list"),
            allHosts.Distinct(StringComparer.OrdinalIgnoreCase));
        File.WriteAllLines(
            Path.Combine(UserDir, "ip-set", "all.list"),
            allIps.Distinct(StringComparer.OrdinalIgnoreCase));

        foreach (var name in new[] { "domains_exclude.list", "ip-exclude.list" })
        {
            var src = Path.Combine(Configs, name);
            var dst = Path.Combine(UserDir, name);
            if (File.Exists(src)) File.Copy(src, dst, true);
            else if (!File.Exists(dst)) File.WriteAllText(dst, "");
        }
    }

    private static void AppendUnique(string path, IEnumerable<string> extra)
    {
        var existing = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var line in extra)
            if (set.Add(line)) existing.Add(line);
        File.WriteAllLines(path, existing);
    }

    /// <summary>Paths relative to binaries/ so .cmd stays ASCII even under Cyrillic install folders.</summary>
    private static string RelUser(params string[] parts) =>
        PathAscii.RelFrom(Binaries, Path.Combine(new[] { UserDir }.Concat(parts).ToArray()));

    private static string RelBin(string absolutePath) =>
        PathAscii.RelFrom(Binaries, absolutePath);

    private static IEnumerable<string> ReadList(string path)
    {
        if (!File.Exists(path)) yield break;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            yield return line;
        }
    }

    public List<string> BuildArgs(string strategyName)
    {
        // Leaf name only — no path separators / traversal
        var leaf = Path.GetFileName(strategyName);
        if (string.IsNullOrWhiteSpace(leaf) ||
            !leaf.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
            leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid strategy name", nameof(strategyName));

        var strategyDir = Path.GetFullPath(Path.Combine(Configs, "strategy", "1.6.2"));
        var strategyPath = Path.GetFullPath(Path.Combine(strategyDir, leaf));
        if (!strategyPath.StartsWith(strategyDir, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(strategyPath))
            throw new FileNotFoundException("strategy missing", leaf);

        var fakeMap = LoadFakeBinMap();
        var ports = LoadPorts();
        var sections = ParseSections(File.ReadAllLines(strategyPath));

        // Magila: START → services (use_in>>X pulls X) → END; emit by file order
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "START", "discord", "cloudflare", "youtube", "END" };
        // Resolve use_in>>target so e.g. cloudflare pulls game_mod filters
        foreach (var sec in wanted.ToList())
        {
            if (!sections.TryGetValue(sec, out var body)) continue;
            foreach (var line in body)
            {
                if (!line.StartsWith("use_in>>", StringComparison.Ordinal)) continue;
                wanted.Add(line["use_in>>".Length..].Trim());
            }
        }

        var fileLines = File.ReadAllLines(strategyPath);
        var ordered = sections.Keys
            .Where(wanted.Contains)
            .OrderBy(k =>
            {
                var idx = Array.FindIndex(fileLines,
                    l => l.Trim().Equals("[" + k + "]", StringComparison.OrdinalIgnoreCase));
                return idx < 0 ? int.MaxValue : idx;
            })
            .ToList();

        var lines = new List<string>();
        foreach (var sec in ordered)
        {
            if (!sections.TryGetValue(sec, out var body)) continue;
            foreach (var line in body)
            {
                if (line.StartsWith("use_in>>", StringComparison.Ordinal)) continue;
                lines.Add(ExpandLine(line, sec, ports, fakeMap));
            }
        }

        // Expand %INIT_LUA% / %INIT_BLOB%
        var final = new List<string>();
        foreach (var line in lines)
        {
            if (line == "%INIT_LUA%")
            {
                var luaInit = Path.Combine(Configs, "lua_init.config");
                var luaDir = Path.Combine(Binaries, "lua");
                if (File.Exists(luaInit))
                {
                    foreach (var name in File.ReadAllLines(luaInit))
                    {
                        var n = name.Trim();
                        if (n.Length == 0 || n.StartsWith("//")) continue;
                        var p = Path.Combine(luaDir, n);
                        if (File.Exists(p))
                            final.Add("--lua-init=@" + RelBin(p));
                    }
                }
                continue;
            }

            if (line == "%INIT_BLOB%")
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                var blobRx = new Regex(@":blob=([^:]+)|:seqovl_pattern=([^:]+)|:pattern=([^:]+)");
                foreach (var l in lines)
                {
                    foreach (Match m in blobRx.Matches(l))
                    {
                        for (var i = 1; i <= 3; i++)
                            if (m.Groups[i].Success)
                                used.Add(m.Groups[i].Value);
                    }
                }
                foreach (var key in used)
                {
                    if (key.StartsWith("fake_default", StringComparison.Ordinal)) continue;
                    if (fakeMap.TryGetValue(key, out var path) && File.Exists(path))
                        final.Add($"--blob={key}:@{RelBin(path)}");
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;
            if (line.StartsWith("use_in>>", StringComparison.Ordinal)) continue;

            // strip leftover %VARS% (e.g. disabled GamePort)
            var cleaned = Regex.Replace(line, "%[^%]+%", "");
            cleaned = cleaned.Replace("=,", "=").Replace(",,", ",");
            if (cleaned.EndsWith(',')) cleaned = cleaned[..^1];
            if (string.IsNullOrWhiteSpace(cleaned)) continue;
            final.Add(cleaned);
        }

        // drop trailing --new
        while (final.Count > 0 && final[^1] == "--new")
            final.RemoveAt(final.Count - 1);

        return final;
    }

    private string ExpandLine(string line, string section, Dictionary<string, string> ports, Dictionary<string, string> fakeMap)
    {
        foreach (var (k, v) in ports)
            line = line.Replace($"%{k}%", v, StringComparison.Ordinal);

        if (line.Contains("%BLOCKLIST%", StringComparison.Ordinal))
        {
            var name = section is "START" or "END" ? "all.list" : section + ".list";
            var rel = RelUser("blacklist", name);
            if (!File.Exists(Path.Combine(UserDir, "blacklist", name)))
                rel = RelUser("blacklist", "all.list");
            return "--hostlist=" + rel;
        }
        if (line.Contains("%IP-SETLIST%", StringComparison.Ordinal))
        {
            var name = section is "START" or "END" ? "all.list" : section + ".list";
            return "--ipset=" + RelUser("ip-set", name);
        }
        if (line.Contains("%DOMAINS-EXCLUDE%", StringComparison.Ordinal))
            return "--hostlist-exclude=" + RelUser("domains_exclude.list");
        if (line.Contains("%IP-EXCLUDE%", StringComparison.Ordinal))
            return "--ipset-exclude=" + RelUser("ip-exclude.list");

        return line;
    }

    private static Dictionary<string, string> LoadFakeBinMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var fakeCfg = Path.Combine(Configs, "fake_bin.config");
        var fakeDir = Path.Combine(Binaries, "fake");
        if (!File.Exists(fakeCfg)) return map;
        foreach (var raw in File.ReadAllLines(fakeCfg))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("[") || line.StartsWith("//")) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            map[key] = Path.Combine(fakeDir, val);
        }
        return map;
    }

    private static Dictionary<string, string> LoadPorts()
    {
        // DiscordPort:2053,...>>DiscordPortUPD:19294-...
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DiscordPort"] = "2053,2083,2087,2096,8443",
            ["DiscordPortUPD"] = "19294-19344,50000-50100",
            ["GamePort"] = "" // disabled — strip later
        };
        var svc = Path.Combine(Configs, "service_setting.config");
        if (!File.Exists(svc)) return map;
        foreach (var raw in File.ReadAllLines(svc))
        {
            var line = raw.Trim();
            if (!line.StartsWith("discord=", StringComparison.OrdinalIgnoreCase)) continue;
            var val = line["discord=".Length..];
            foreach (var part in val.Split(">>", StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = part.IndexOf(':');
                if (colon <= 0) continue;
                map[part[..colon].Trim()] = part[(colon + 1)..].Trim();
            }
        }
        return map;
    }

    private static Dictionary<string, List<string>> ParseSections(string[] fileLines)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? cur = null;
        foreach (var raw in fileLines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                cur = line[1..^1];
                map[cur] = new List<string>();
                continue;
            }
            if (cur == null) continue;
            if (line.Length == 0) continue;
            map[cur].Add(line);
        }
        return map;
    }
}
