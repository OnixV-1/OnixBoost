using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>Network tweaks that Soft users usually need (prefer IPv4 + flush DNS).</summary>
public static class NetworkFix
{
    public static async Task<string> PrepareForDpiAsync()
    {
        if (PlatformOs.IsLinux)
            return await PrepareLinuxAsync();

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

    private static async Task<string> PrepareLinuxAsync()
    {
        // Never pkexec here — GUI password dialogs often fail; Soft does not need sysctl.
        try
        {
            await RunAsync("resolvectl", "flush-caches");
        }
        catch { }
        Logger.Info("NetworkFix: linux light (dns flush only, no pkexec)");
        return "Linux: DNS cache flush (без pkexec). Soft: fix-caps.sh один раз";
    }

    private static async Task<string> RunAsync(string file, string args)
    {
        return await Task.Run(() =>
        {
            try
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
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });
    }
}
