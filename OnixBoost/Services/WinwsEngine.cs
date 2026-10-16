using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

public enum WinwsSlot
{
    /// <summary>Legacy single-process mode (kills all winws on stop).</summary>
    Shared,
    Discord,
    Games
}

public sealed class WinwsEngine
{
    /// <summary>Serializes start so dual Discord+Games slots never claim the same new PID.</summary>
    private static readonly SemaphoreSlim StartGate = new(1, 1);

    private readonly WinwsSlot _slot;
    private Process? _cmdProc;
    private int _winwsPid;

    public WinwsEngine(WinwsSlot slot = WinwsSlot.Shared) => _slot = slot;

    public WinwsSlot Slot => _slot;

    public bool IsRunning
    {
        get
        {
            if (_winwsPid > 0)
            {
                try
                {
                    using var p = Process.GetProcessById(_winwsPid);
                    if (!p.HasExited) return true;
                }
                catch { }
                _winwsPid = 0;
            }

            if (_slot == WinwsSlot.Shared)
                return Process.GetProcessesByName("winws").Length > 0;

            return false;
        }
    }

    public IReadOnlyList<string> BuildArgs(string argsFileName, bool autoHostList)
    {
        var file = Path.Combine(AppPaths.ArgsDir, argsFileName);
        if (!File.Exists(file))
            throw new FileNotFoundException("Profile args not found", file);

        // cwd will be dpi\bin — use relative paths so Cyrillic folders don't become ?????
        const string root = "..\\";
        const string vbin = "";
        var parts = new List<string>();

        foreach (var raw in File.ReadAllLines(file))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//")) continue;
            var line = raw.Replace("^", "");
            line = line.Replace("%ROOT%", root).Replace("%VBIN%", vbin).Replace("%BIN%", vbin);
            while (line.Contains("  ")) line = line.Replace("  ", " ");

            foreach (Match m in Regex.Matches(line.Trim(), "(\"([^\"]*)\"|\\S+)"))
            {
                var tok = m.Value;
                var eq = Regex.Match(tok, @"^([-+][^=]+)=(.*)$");
                if (eq.Success)
                {
                    var k = eq.Groups[1].Value;
                    var v = eq.Groups[2].Value;
                    if (v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"'))
                        v = v[1..^1];
                    parts.Add(k + "=" + v);
                }
                else
                {
                    if (tok.Length >= 2 && tok.StartsWith('"') && tok.EndsWith('"'))
                        tok = tok[1..^1];
                    parts.Add(tok);
                }
            }
        }

        if (autoHostList)
        {
            Directory.CreateDirectory(AppPaths.ListsDir);
            if (!File.Exists(AppPaths.AutoHostListFile))
                File.WriteAllText(AppPaths.AutoHostListFile, "# auto\n");
            var autoRel = PathAscii.RelFrom(AppPaths.BinDir, AppPaths.AutoHostListFile);
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i].StartsWith("--hostlist=", StringComparison.OrdinalIgnoreCase) &&
                    parts[i].Contains("list-general", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Insert(i + 1, $"--hostlist-auto={autoRel}");
                    break;
                }
            }
        }

        return parts;
    }

    public async Task StartAsync(string argsFileName, bool autoHostList, params string?[] appendArgsFiles)
        => await StartAsync(argsFileName, autoHostList, discordStreamSafe: false, preserveYoutubeQuic: false,
            exclusiveWf: null, appendArgsFiles);

    public async Task StartAsync(
        string argsFileName,
        bool autoHostList,
        bool discordStreamSafe,
        params string?[] appendArgsFiles)
        => await StartAsync(argsFileName, autoHostList, discordStreamSafe, preserveYoutubeQuic: false,
            exclusiveWf: null, appendArgsFiles);

    public async Task StartAsync(
        string argsFileName,
        bool autoHostList,
        bool discordStreamSafe,
        bool preserveYoutubeQuic,
        params string?[] appendArgsFiles)
        => await StartAsync(argsFileName, autoHostList, discordStreamSafe, preserveYoutubeQuic,
            exclusiveWf: null, appendArgsFiles);

    /// <param name="exclusiveWf">
    /// Relative path under dpi/bin for --wf-raw=@file. When set, all --wf-* from profiles are stripped
    /// so this instance does not overlap another winws.
    /// </param>
    public async Task StartAsync(
        string argsFileName,
        bool autoHostList,
        bool discordStreamSafe,
        bool preserveYoutubeQuic,
        string? exclusiveWf,
        params string?[] appendArgsFiles)
    {
        await StopAsync(killAllWinws: _slot == WinwsSlot.Shared);

        Logger.Info($"App root: {AppPaths.Root} · winws slot={_slot}");
        if (!File.Exists(AppPaths.WinwsExe))
            throw new FileNotFoundException("winws.exe missing", AppPaths.WinwsExe);

        var lists = Path.Combine(AppPaths.ListsDir, "list-general.txt");
        if (!File.Exists(lists))
            throw new FileNotFoundException("list-general.txt missing", lists);

        var args = BuildArgs(argsFileName, autoHostList).ToList();
        foreach (var appendArgsFile in appendArgsFiles)
        {
            if (string.IsNullOrEmpty(appendArgsFile)) continue;
            if (!File.Exists(Path.Combine(AppPaths.ArgsDir, appendArgsFile))) continue;
            var extra = BuildArgs(appendArgsFile, autoHostList: false);
            // skip --wf-* (base profile / exclusive filter owns win filters)
            var filters = extra.Where(a =>
                !a.StartsWith("--wf-", StringComparison.OrdinalIgnoreCase)).ToList();
            if (filters.Count == 0) continue;
            if (args.Count > 0 && args[^1] != "--new")
                args.Add("--new");
            args.AddRange(filters);
        }

        if (!string.IsNullOrEmpty(exclusiveWf))
        {
            args = args.Where(a => !a.StartsWith("--wf-", StringComparison.OrdinalIgnoreCase)).ToList();
            var wfPath = Path.IsPathRooted(exclusiveWf)
                ? exclusiveWf
                : Path.Combine(AppPaths.BinDir, exclusiveWf);
            if (!File.Exists(wfPath))
                throw new FileNotFoundException("exclusive WinDivert filter missing", wfPath);
            args.Insert(0, "--wf-raw=@" + Path.GetFileName(wfPath));
            Logger.Info($"winws[{_slot}]: exclusive --wf-raw=@{Path.GetFileName(wfPath)}");
        }

        if (discordStreamSafe)
        {
            args = SanitizeForDiscordStream(args, preserveYoutubeQuic);
            Logger.Info("winws: Discord stream-safe sanitize" +
                        (preserveYoutubeQuic ? " (keep YouTube QUIC)" : ""));
        }

        // Steam CM uses TCP/UDP 27015–27050; appends strip --wf-* so expand base filter.
        var steamProfile =
            argsFileName.Contains("steam_main", StringComparison.OrdinalIgnoreCase) ||
            appendArgsFiles.Any(f =>
                !string.IsNullOrEmpty(f) &&
                f.Contains("steam_main", StringComparison.OrdinalIgnoreCase));
        if (steamProfile)
        {
            args = ExpandSteamCmWfPorts(args);
            Logger.Info("winws: expanded --wf-tcp/udp with Steam CM 27015-27050");
        }

        foreach (var a in args)
        {
            var eq = a.IndexOf('=');
            if (eq <= 0) continue;
            var val = a[(eq + 1)..];
            if (!(val.EndsWith(".txt") || val.EndsWith(".bin"))) continue;
            var check = Path.IsPathRooted(val) ? val : Path.Combine(AppPaths.BinDir, val);
            if (!File.Exists(check))
                Logger.Error("MISSING: " + val);
        }

        var argsDump = Path.Combine(AppPaths.Root, $"last-winws-args-{_slot.ToString().ToLowerInvariant()}.txt");
        File.WriteAllText(argsDump, string.Join("\n", args));
        if (_slot == WinwsSlot.Shared)
            File.WriteAllText(Path.Combine(AppPaths.Root, "last-winws-args.txt"), string.Join("\n", args));

        var tag = _slot.ToString().ToLowerInvariant();
        var bat = Path.Combine(AppPaths.BinDir, $"_onix_start_{tag}.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("cd /d \"%~dp0\"");
        sb.Append("winws.exe");
        foreach (var a in args)
        {
            if (a.Contains('=') && a.Contains(' '))
            {
                var i = a.IndexOf('=');
                sb.Append(' ').Append(a[..i]).Append("=\"").Append(a[(i + 1)..]).Append('"');
            }
            else if (a.Contains(' '))
                sb.Append(" \"").Append(a).Append('"');
            else
                sb.Append(' ').Append(a);
        }
        sb.AppendLine();
        await File.WriteAllTextAsync(bat, sb.ToString(), PathAscii.CmdEncoding);

        var logBin = Path.Combine(AppPaths.BinDir, $"winws_{tag}.log");
        var logOut = Path.Combine(AppPaths.Root, $"winws_{tag}.log");
        try { File.Delete(logBin); } catch { }

        await StartGate.WaitAsync();
        try
        {
            var before = SnapshotWinwsPids();

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"_onix_start_{tag}.cmd >winws_{tag}.log 2>&1\"",
                WorkingDirectory = AppPaths.BinDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _cmdProc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start winws cmd");
            await Task.Delay(280);

            _winwsPid = FindNewWinwsPid(before);
            if (_winwsPid == 0 && Process.GetProcessesByName("winws").Length == 0)
            {
                var tip = File.Exists(logBin) ? File.ReadAllText(logBin) : "(no winws log)";
                try { File.WriteAllText(logOut, tip); } catch { }
                throw new InvalidOperationException(
                    $"winws[{_slot}] did not stay up.\n" + tip[..Math.Min(600, tip.Length)] +
                    "\nAdmin + AV exclusion for WinDivert required.");
            }

            try { if (File.Exists(logBin)) File.Copy(logBin, logOut, true); } catch { }
            Logger.Info($"winws[{_slot}] RUNNING pid={_winwsPid}");
        }
        finally
        {
            StartGate.Release();
        }
    }

    private static HashSet<int> SnapshotWinwsPids()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("winws"))
        {
            try { set.Add(p.Id); } catch { }
            try { p.Dispose(); } catch { }
        }
        return set;
    }

    private static int FindNewWinwsPid(HashSet<int> before)
    {
        foreach (var p in Process.GetProcessesByName("winws"))
        {
            try
            {
                if (!before.Contains(p.Id))
                {
                    var id = p.Id;
                    p.Dispose();
                    return id;
                }
            }
            catch { }
            try { p.Dispose(); } catch { }
        }

        // Fallback: if only one winws exists, claim it
        var all = Process.GetProcessesByName("winws");
        try
        {
            if (all.Length == 1) return all[0].Id;
        }
        finally
        {
            foreach (var p in all) try { p.Dispose(); } catch { }
        }
        return 0;
    }

    /// <summary>
    /// Drop Game-UDP / any-protocol tails that shred Discord WebRTC (Go Live / screen share).
    /// Soften Discord voice UDP fakes so media frames are not over-desynced.
    /// </summary>
    /// <summary>
    /// Valve CM servers (cmp*.steamserver.net) listen on 27015–27050, not only 443.
    /// Without these ports in --wf-*, steam_main filters never see CM handshakes → Steam -100.
    /// </summary>
    public static List<string> ExpandSteamCmWfPorts(List<string> args)
    {
        const string cm = "27015-27050";
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase))
            {
                var v = args[i][(args[i].IndexOf('=') + 1)..];
                if (!v.Contains("27015", StringComparison.Ordinal))
                    args[i] = "--wf-tcp=" + v + "," + cm;
            }
            else if (args[i].StartsWith("--wf-udp=", StringComparison.OrdinalIgnoreCase))
            {
                var v = args[i][(args[i].IndexOf('=') + 1)..];
                if (!v.Contains("27015", StringComparison.Ordinal))
                    args[i] = "--wf-udp=" + v + "," + cm;
            }
        }

        if (!args.Any(a => a.StartsWith("--wf-udp=", StringComparison.OrdinalIgnoreCase)) &&
            args.Any(a => a.StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase)))
        {
            var tcp = args.FindIndex(a => a.StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase));
            args.Insert(tcp + 1, "--wf-udp=443," + cm);
        }

        return args;
    }

    public static List<string> SanitizeForDiscordStream(List<string> args, bool preserveYoutubeQuic = false)
    {
        var blocks = SplitArgBlocks(args);
        var kept = new List<List<string>>();
        foreach (var block in blocks)
        {
            if (block.Count == 0) continue;

            var dropIpsetQuic =
                !preserveYoutubeQuic &&
                block.Any(a => a.StartsWith("--filter-udp=443", StringComparison.OrdinalIgnoreCase)) &&
                block.Any(a => a.StartsWith("--ipset=", StringComparison.OrdinalIgnoreCase) &&
                               !a.StartsWith("--ipset-exclude", StringComparison.OrdinalIgnoreCase));

            var drop =
                block.Any(a => a.StartsWith("--filter-udp=12", StringComparison.OrdinalIgnoreCase)) ||
                block.Any(a => a.StartsWith("--filter-tcp=12", StringComparison.OrdinalIgnoreCase)) ||
                block.Any(a => a.Contains("ACTIVE_GAME_UDP", StringComparison.OrdinalIgnoreCase)) ||
                block.Any(a => a.StartsWith("--dpi-desync-any-protocol", StringComparison.OrdinalIgnoreCase)) ||
                dropIpsetQuic;

            if (drop) continue;

            SoftenDiscordUdpBlock(block);
            kept.Add(block);
        }

        return JoinArgBlocks(kept);
    }

    private static void SoftenDiscordUdpBlock(List<string> block)
    {
        var isDiscordUdp =
            block.Any(a => a.Contains("filter-l7=discord", StringComparison.OrdinalIgnoreCase) ||
                           a.Contains("fake-discord", StringComparison.OrdinalIgnoreCase) ||
                           a.Contains("fake-stun", StringComparison.OrdinalIgnoreCase));
        if (!isDiscordUdp) return;

        for (var i = 0; i < block.Count; i++)
        {
            if (!block[i].StartsWith("--dpi-desync-repeats=", StringComparison.OrdinalIgnoreCase))
                continue;
            var eq = block[i].IndexOf('=');
            if (eq < 0 || !int.TryParse(block[i].AsSpan(eq + 1), out var n) || n > 4)
                block[i] = "--dpi-desync-repeats=4";
        }

        if (!block.Any(a => a.StartsWith("--dpi-desync-cutoff=", StringComparison.OrdinalIgnoreCase)))
            block.Add("--dpi-desync-cutoff=n3");
    }

    private static List<List<string>> SplitArgBlocks(List<string> args)
    {
        var blocks = new List<List<string>> { new() };
        foreach (var a in args)
        {
            if (a.Equals("--new", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(new List<string>());
                continue;
            }
            blocks[^1].Add(a);
        }
        return blocks;
    }

    private static List<string> JoinArgBlocks(List<List<string>> blocks)
    {
        var result = new List<string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Count == 0) continue;
            if (result.Count > 0)
                result.Add("--new");
            result.AddRange(blocks[i]);
        }
        return result;
    }

    public Task StopAsync() => StopAsync(killAllWinws: _slot == WinwsSlot.Shared);

    public async Task StopAsync(bool killAllWinws)
    {
        try
        {
            if (_cmdProc is { HasExited: false })
            {
                try { _cmdProc.Kill(true); _cmdProc.WaitForExit(1500); } catch { }
            }
        }
        catch { }

        _cmdProc = null;

        if (_winwsPid > 0)
        {
            try
            {
                using var p = Process.GetProcessById(_winwsPid);
                if (!p.HasExited)
                {
                    p.Kill(true);
                    p.WaitForExit(2000);
                }
            }
            catch { }
            _winwsPid = 0;
        }

        if (killAllWinws)
            ProcessLocker.KillByName("winws");

        await Task.CompletedTask;
    }
}
