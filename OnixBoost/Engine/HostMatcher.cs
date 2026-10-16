using System;
using System.Collections.Generic;
using System.IO;
using OnixBoost.Services;

namespace OnixBoost.Engine;

internal sealed class HostMatcher
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffix = new();
    private readonly HashSet<string> _excludeExact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _excludeSuffix = new();

    public static HostMatcher LoadFromFile(string path)
    {
        var m = new HostMatcher();
        m.AddFile(path);
        // Legacy native DPI bootstrap (Discord / YT probes)
        foreach (var d in new[]
                 {
                     "discord.com", "discordapp.com", "discord.gg", "discord.media",
                     "discordcdn.com", "gateway.discord.gg", "cdn.discordapp.com",
                     "dns.google", "cloudflare-dns.com", "youtube.com", "googlevideo.com"
                 })
        {
            m._exact.Add(d);
            m._suffix.Add("." + d);
        }
        return m;
    }

    /// <summary>
    /// Legacy native Games hostlist. Production Games path uses winws steam_main/games_main
    /// — FakeSplit here broke Steam CM (error -100). Kept for non-Games native probes only.
    /// </summary>
    public static HostMatcher LoadGamesLists()
    {
        var m = new HostMatcher();
        // Prefer launchers only; Steam must not go through FakeSplit.
        m.AddFile(Path.Combine(AppPaths.ListsDir, "list-games.txt"));
        m.ExcludeFile(Path.Combine(AppPaths.ListsDir, "list-discord.txt"));
        m.ExcludeFile(Path.Combine(AppPaths.ListsDir, "list-steam.txt"));
        foreach (var d in new[]
                 {
                     "discord.com", "discordapp.com", "discord.gg", "discord.media",
                     "discordcdn.com", "gateway.discord.gg", "cdn.discordapp.com",
                     "discordapp.net", "discord.gdmf.nvidia.com",
                     "steampowered.com", "steamcommunity.com", "steamstatic.com",
                     "steamserver.net", "steamcontent.com"
                 })
        {
            m._excludeExact.Add(d);
            m._excludeSuffix.Add("." + d);
        }
        return m;
    }

    public void AddFile(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('^')) continue;
            if (line.StartsWith("*."))
            {
                _suffix.Add(line[1..]);
                _exact.Add(line[2..]);
            }
            else
            {
                _exact.Add(line);
                _suffix.Add("." + line);
            }
        }
    }

    public void ExcludeFile(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('^')) continue;
            if (line.StartsWith("*."))
            {
                _excludeSuffix.Add(line[1..]);
                _excludeExact.Add(line[2..]);
            }
            else
            {
                _excludeExact.Add(line);
                _excludeSuffix.Add("." + line);
            }
        }
    }

    public bool Matches(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (IsExcluded(host)) return false;
        if (_exact.Contains(host)) return true;
        foreach (var s in _suffix)
        {
            if (host.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsExcluded(string host)
    {
        if (_excludeExact.Contains(host)) return true;
        foreach (var s in _excludeSuffix)
        {
            if (host.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
