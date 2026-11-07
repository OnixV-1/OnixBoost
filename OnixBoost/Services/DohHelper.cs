using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// RU whitelist-friendly DNS: probe Yandex / other RU resolvers / current ISP DNS,
/// apply best pair, always restore on stop.
/// Never force Google/Cloudflare as system DNS (often blocked → 1ms Health fail).
/// </summary>
public static class DohHelper
{
    public const string SafeDns = "77.88.8.8";
    public const string SafeDns2 = "77.88.8.1";

    private static readonly string[] ProbeHosts = { "ya.ru", "vk.com", "mail.ru" };

    private static readonly DnsCandidate[] Pool =
    {
        new("Yandex", "77.88.8.8", "77.88.8.1", "https://common.dot.dns.yandex.net/dns-query"),
        new("Yandex Safe", "77.88.8.88", "77.88.8.2", "https://safe.dot.dns.yandex.net/dns-query"),
        new("RU MSK-IX", "195.208.4.1", "195.208.5.1", null),
        new("RU DNS2", "195.34.32.24", "195.34.32.25", null),
    };

    private sealed record DnsCandidate(string Name, string Primary, string Secondary, string? DohTemplate);

    private static string BackupPath => Path.Combine(AppPaths.Root, "dns-backup.json");

    public static async Task<(bool ok, string message)> ApplyAsync(
        string? dnsIp = null,
        string? template = null)
    {
        try
        {
            await BackupCurrentAsync();

            DnsCandidate chosen;
            if (!string.IsNullOrWhiteSpace(dnsIp) && IsSafeIpv4(dnsIp))
            {
                var sec = !string.IsNullOrWhiteSpace(template) && IsSafeIpv4(template) ? template! : SafeDns2;
                chosen = new DnsCandidate("manual", dnsIp!, sec, null);
            }
            else
            {
                chosen = await PickBestAsync() ?? Pool[0];
            }

            if (!IsSafeIpv4(chosen.Primary) || !IsSafeIpv4(chosen.Secondary))
                chosen = Pool[0];

            var primary = chosen.Primary;
            var secondary = chosen.Secondary;
            var doh = chosen.DohTemplate;
            if (doh != null && !doh.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                doh = null;

            if (PlatformOs.IsLinux)
            {
                // Do not touch system DNS via pkexec — Soft works without it.
                var linuxMsg = $"DNS left unchanged (Linux). Soft uses resolver probe only · prefer {chosen.Name} {primary}";
                Logger.Info(linuxMsg);
                return (true, linuxMsg);
            }

            if (!string.IsNullOrEmpty(doh))
            {
                try
                {
                    await RunAsync("netsh",
                        $"dns add encryption server={primary} dohtemplate={doh} autoupgrade=yes udpfallback=yes");
                }
                catch { }
            }

            var script = $$"""
                $ErrorActionPreference = 'SilentlyContinue'
                $dns = @('{{primary}}','{{secondary}}')
                Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object {
                  Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses $dns
                }
                """;
            if (!string.IsNullOrEmpty(doh))
            {
                script += $$"""
                    try {
                      Add-DnsClientDohServerAddress -ServerAddress '{{primary}}' -DohTemplate '{{doh}}' -AllowFallbackToUdp $true -AutoUpgrade $true
                    } catch {}
                    """;
            }

            var tmp = Path.Combine(Path.GetTempPath(), "onixboost-doh.ps1");
            await File.WriteAllTextAsync(tmp, script);
            var set = await RunAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"");
            Logger.Info("DNS set: " + set[..Math.Min(400, set.Length)]);

            foreach (var name in new[] { "Ethernet", "Wi-Fi", "WLAN", "Ethernet 2", "Ethernet 3" })
            {
                await RunAsync("netsh", $"interface ip set dns name=\"{name}\" static {primary} primary");
                await RunAsync("netsh", $"interface ip add dns name=\"{name}\" {secondary} index=2");
            }

            await RunAsync("ipconfig", "/flushdns");

            var msg = $"DNS → {chosen.Name} {primary}" + (doh != null ? " (DoH)" : "");
            Logger.Info(msg);
            return (true, msg);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool ok, string message)> ApplyLinuxAsync(
        DnsCandidate chosen, string primary, string secondary)
    {
        // Prefer NetworkManager if present; else resolvectl per-link.
        var script =
            "set +e\n" +
            "if command -v nmcli >/dev/null 2>&1; then\n" +
            "  CON=$(nmcli -t -f NAME,DEVICE connection show --active | head -1 | cut -d: -f1)\n" +
            "  if [ -n \"$CON\" ]; then\n" +
            $"    nmcli connection modify \"$CON\" ipv4.dns \"{primary} {secondary}\"\n" +
            "    nmcli connection modify \"$CON\" ipv4.ignore-auto-dns yes\n" +
            "    nmcli connection up \"$CON\" >/dev/null 2>&1 || true\n" +
            "  fi\n" +
            "elif command -v resolvectl >/dev/null 2>&1; then\n" +
            "  IF=$(ip -o route get 1.1.1.1 2>/dev/null | sed -n 's/.*dev \\([^ ]*\\).*/\\1/p')\n" +
            "  if [ -n \"$IF\" ]; then\n" +
            $"    resolvectl dns \"$IF\" {primary} {secondary} || true\n" +
            "  fi\n" +
            "fi\n" +
            "resolvectl flush-caches >/dev/null 2>&1 || true\n" +
            "echo ok\n";
        var tmp = Path.Combine(Path.GetTempPath(), $"onixboost-doh-{Environment.ProcessId}.sh");
        await File.WriteAllTextAsync(tmp, "#!/bin/bash\n" + script);
        try { Process.Start("chmod", "+x " + tmp)?.WaitForExit(2000); } catch { }
        var o = await RunAsync("pkexec", "/bin/bash " + tmp);
        if (!o.Contains("ok", StringComparison.OrdinalIgnoreCase))
            o = await RunAsync("/bin/bash", tmp);
        try { File.Delete(tmp); } catch { }
        var msg = $"DNS → {chosen.Name} {primary}";
        Logger.Info(msg + " linux=" + o[..Math.Min(120, o.Length)]);
        return (true, msg);
    }

    public static async Task<(bool ok, string message)> RestoreAsync()
    {
        try
        {
            if (PlatformOs.IsLinux)
            {
                try { File.Delete(BackupPath); } catch { }
                return (true, "DNS unchanged (Linux)");
            }

            if (!File.Exists(BackupPath))
            {
                var dhcp = """
                    $ErrorActionPreference = 'SilentlyContinue'
                    Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object {
                      Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses
                    }
                    """;
                var tmp = Path.Combine(Path.GetTempPath(), "onixboost-dns-restore.ps1");
                await File.WriteAllTextAsync(tmp, dhcp);
                var (dhcpOk, _) = await RunCheckedAsync("powershell",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"");
                await RunAsync("ipconfig", "/flushdns");
                Logger.Info("DNS restore → DHCP (no backup)");
                return (dhcpOk, "DNS → DHCP");
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(BackupPath));
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$ok = $true");
            foreach (var a in doc.RootElement.GetProperty("adapters").EnumerateArray())
            {
                var idx = a.GetProperty("ifIndex").GetInt32();
                if (idx <= 0) continue;

                var servers = a.TryGetProperty("servers", out var sArr)
                    ? sArr.EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(s => IsSafeIpv4(s))
                        .Cast<string>()
                        .ToArray()
                    : Array.Empty<string>();

                if (servers.Length == 0)
                {
                    sb.AppendLine(
                        $"try {{ Set-DnsClientServerAddress -InterfaceIndex {idx} -ResetServerAddresses }} catch {{ $ok = $false }}");
                }
                else
                {
                    var list = string.Join(",", servers.Select(s => "'" + s + "'"));
                    sb.AppendLine(
                        $"try {{ Set-DnsClientServerAddress -InterfaceIndex {idx} -ServerAddresses @({list}) }} catch {{ $ok = $false }}");
                }
            }
            sb.AppendLine("if (-not $ok) { exit 1 } else { exit 0 }");

            var tmp2 = Path.Combine(Path.GetTempPath(), "onixboost-dns-restore.ps1");
            await File.WriteAllTextAsync(tmp2, sb.ToString());
            var (ok, output) = await RunCheckedAsync("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp2}\"");
            await RunAsync("ipconfig", "/flushdns");

            if (!ok)
            {
                Logger.Error("DNS restore failed, keeping backup: " + output[..Math.Min(200, output.Length)]);
                return (false, "DNS restore failed — backup kept");
            }

            try { File.Delete(BackupPath); } catch { }
            Logger.Info("DNS restored from backup");
            return (true, "DNS восстановлен");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool ok, string message)> RestoreLinuxAsync()
    {
        var script =
            "set +e\n" +
            "if command -v nmcli >/dev/null 2>&1; then\n" +
            "  CON=$(nmcli -t -f NAME,DEVICE connection show --active | head -1 | cut -d: -f1)\n" +
            "  if [ -n \"$CON\" ]; then\n" +
            "    nmcli connection modify \"$CON\" ipv4.dns \"\"\n" +
            "    nmcli connection modify \"$CON\" ipv4.ignore-auto-dns no\n" +
            "    nmcli connection up \"$CON\" >/dev/null 2>&1 || true\n" +
            "  fi\n" +
            "fi\n" +
            "IF=$(ip -o route get 1.1.1.1 2>/dev/null | sed -n 's/.*dev \\([^ ]*\\).*/\\1/p')\n" +
            "resolvectl revert \"$IF\" 2>/dev/null || true\n" +
            "resolvectl flush-caches >/dev/null 2>&1 || true\n" +
            "echo ok\n";
        var tmp = Path.Combine(Path.GetTempPath(), $"onixboost-dns-restore-{Environment.ProcessId}.sh");
        await File.WriteAllTextAsync(tmp, "#!/bin/bash\n" + script);
        try { Process.Start("chmod", "+x " + tmp)?.WaitForExit(2000); } catch { }
        await RunAsync("pkexec", "/bin/bash " + tmp);
        try { File.Delete(tmp); } catch { }
        try { File.Delete(BackupPath); } catch { }
        Logger.Info("DNS restore → DHCP (linux)");
        return (true, "DNS → DHCP");
    }

    private static async Task BackupCurrentAsync()
    {
        if (File.Exists(BackupPath)) return;

        if (PlatformOs.IsLinux)
        {
            var wrapped = "{\"adapters\":[],\"os\":\"linux\"}";
            var tmpBak = BackupPath + ".tmp";
            await File.WriteAllTextAsync(tmpBak, wrapped);
            File.Move(tmpBak, BackupPath, overwrite: true);
            Logger.Info("DNS backup saved (linux stub)");
            return;
        }

        var script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object {
              $dns = (Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4).ServerAddresses
              [pscustomobject]@{ ifIndex = $_.ifIndex; name = $_.Name; servers = @($dns) }
            } | ConvertTo-Json -Compress
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "onixboost-dns-backup.ps1");
        await File.WriteAllTextAsync(tmp, script);
        var json = (await RunAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"")).Trim();
        if (string.IsNullOrWhiteSpace(json)) return;

        if (json.StartsWith('{'))
            json = "[" + json + "]";
        var wrappedWin = "{\"adapters\":" + json + "}";

        var tmpBakWin = BackupPath + ".tmp";
        await File.WriteAllTextAsync(tmpBakWin, wrappedWin);
        File.Move(tmpBakWin, BackupPath, overwrite: true);
        Logger.Info("DNS backup saved");
    }

    private static async Task<DnsCandidate?> PickBestAsync()
    {
        var candidates = new List<DnsCandidate>(Pool);

        try
        {
            var cur = await ReadCurrentDnsAsync();
            foreach (var s in cur.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (s is "8.8.8.8" or "8.8.4.4" or "1.1.1.1" or "1.0.0.1") continue;
                if (!IsSafeIpv4(s)) continue;
                if (candidates.Any(c => c.Primary == s)) continue;
                candidates.Add(new DnsCandidate("ISP " + s, s, s, null));
            }
        }
        catch { }

        var tasks = candidates.Select(async c =>
        {
            var (ok, ms) = await ProbeResolverAsync(c.Primary);
            return (c, ok, ms);
        });
        var scored = (await Task.WhenAll(tasks)).Where(x => x.ok > 0).ToList();

        if (scored.Count == 0) return Pool[0];
        return scored
            .OrderByDescending(x => x.ok)
            .ThenBy(x => x.ms)
            .First().c;
    }

    private static async Task<List<string>> ReadCurrentDnsAsync()
    {
        if (PlatformOs.IsLinux)
        {
            var o = await RunAsync("/bin/bash",
                "-c \"resolvectl status 2>/dev/null | awk '/DNS Servers:/{for(i=3;i<=NF;i++) print $i}'\"");
            return o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsSafeIpv4)
                .Cast<string>()
                .ToList();
        }

        var script = """
            $ErrorActionPreference = 'SilentlyContinue'
            (Get-DnsClientServerAddress -AddressFamily IPv4 |
              Where-Object { $_.ServerAddresses } |
              Select-Object -ExpandProperty ServerAddresses) -join "`n"
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "onixboost-dns-cur.ps1");
        await File.WriteAllTextAsync(tmp, script);
        var oWin = await RunAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"");
        return oWin.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsSafeIpv4)
            .Cast<string>()
            .ToList();
    }

    private static async Task<(int okCount, long ms)> ProbeResolverAsync(string dnsIp)
    {
        var sw = Stopwatch.StartNew();
        var ok = 0;
        if (!IsSafeIpv4(dnsIp)) return (0, 0);

        Process? p = null;
        try
        {
            if (PlatformOs.IsLinux)
            {
                var names = string.Join(" ", ProbeHosts);
                p = Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments =
                        $"-c \"n=0; for h in {names}; do dig @{dnsIp} +time=1 +tries=1 +short \"$h\" A >/dev/null 2>&1 && n=$((n+1)); done; echo $n\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
            }
            else
            {
                var names = string.Join("','", ProbeHosts);
                var script = $$"""
                    $ErrorActionPreference = 'SilentlyContinue'
                    $n = 0
                    foreach ($h in @('{{names}}')) {
                      $r = Resolve-DnsName -Name $h -Server '{{dnsIp}}' -Type A -DnsOnly -ErrorAction SilentlyContinue |
                           Select-Object -First 1
                      if ($r) { $n++ }
                    }
                    Write-Output $n
                    """;
                var tmp = Path.Combine(Path.GetTempPath(), $"onixboost-dns-probe-{dnsIp.Replace('.', '-')}.ps1");
                await File.WriteAllTextAsync(tmp, script);
                p = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
            }

            if (p == null) return (0, sw.ElapsedMilliseconds);

            var finished = await Task.Run(() => p.WaitForExit(3500));
            if (finished)
            {
                var o = (await p.StandardOutput.ReadToEndAsync()).Trim();
                if (int.TryParse(o, out var n)) ok = n;
            }
            else
            {
                try { p.Kill(true); } catch { }
            }
        }
        catch { }
        finally
        {
            try { p?.Dispose(); } catch { }
        }
        sw.Stop();
        return (ok, sw.ElapsedMilliseconds);
    }

    private static bool IsSafeIpv4(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length > 15) return false;
        if (!IPAddress.TryParse(s, out var ip)) return false;
        return ip.AddressFamily == AddressFamily.InterNetwork;
    }

    private static async Task<string> RunAsync(string file, string args)
    {
        var (ok, output) = await RunCheckedAsync(file, args);
        _ = ok;
        return output;
    }

    private static async Task<(bool ok, string output)> RunCheckedAsync(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        if (p == null) return (false, "failed to start");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        // Never hang DNS setup forever — was WaitForExitAsync() with no deadline
        if (!await Task.Run(() => p.WaitForExit(8000)))
        {
            try { p.Kill(true); } catch { }
            return (false, "timeout");
        }
        var o = await stdoutTask + await stderrTask;
        return (p.ExitCode == 0, o);
    }
}
