using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>Network tweaks that zapret/Flowseal users usually need (IPv6 off + flush DNS).</summary>
public static class NetworkFix
{
    public static async Task<string> PrepareForDpiAsync()
    {
        var script = """
            $ErrorActionPreference = 'SilentlyContinue'
            # Prefer IPv4 — many DPI bypasses only handle IPv4; Discord often prefers AAAA
            Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object {
              Disable-NetAdapterBinding -Name $_.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue
            }
            Clear-DnsClientCache
            ipconfig /flushdns | Out-Null
            'ipv6_disabled_and_dns_flushed'
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "onixboost-netfix.ps1");
        await File.WriteAllTextAsync(tmp, script);
        var o = await RunAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"");
        Logger.Info("NetworkFix: " + o.Trim());
        return o.Contains("ipv6_disabled", StringComparison.OrdinalIgnoreCase)
            ? "IPv6 disabled on adapters + DNS cache flushed"
            : "NetworkFix ran (check Admin rights if IPv6 still on)";
    }

    private static async Task<string> RunAsync(string file, string args)
    {
        return await Task.Run(() =>
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
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            return o;
        });
    }
}
