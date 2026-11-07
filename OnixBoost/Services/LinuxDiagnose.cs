using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace OnixBoost.Services;

public static class LinuxDiagnose
{
    public static string Snapshot()
    {
        if (!PlatformOs.IsLinux) return "not-linux";
        var sb = new StringBuilder();
        sb.AppendLine("root=" + AppPaths.Root);
        sb.AppendLine("nfqws=" + File.Exists(AppPaths.NfqwsExe) + " " + AppPaths.NfqwsExe);
        sb.AppendLine("sing-box=" + File.Exists(AppPaths.CoreExe) + " " + AppPaths.CoreExe);
        sb.AppendLine("nft-helper=" + File.Exists(LinuxElevate.NftHelperPath));
        sb.AppendLine("getcap_nfq=" + RunGetCap(AppPaths.NfqwsExe));
        sb.AppendLine("getcap_sb=" + RunGetCap(AppPaths.CoreExe));
        sb.AppendLine("soft_caps=" + LinuxElevate.SoftCapsReady);
        sb.AppendLine("nft_sudo=" + LinuxElevate.NftSudoReady());
        sb.AppendLine("nfqws_procs=" + Process.GetProcessesByName("nfqws").Length);
        sb.AppendLine("singbox_procs=" + Process.GetProcessesByName("sing-box").Length);
        try
        {
            var ver = RunArgs(AppPaths.CoreExe, new[] { "version" }).Split('\n')[0];
            sb.AppendLine("sing_ver=" + ver);
        }
        catch { }
        var nftLog = Path.Combine(AppPaths.Root, "nfqws_discord.log");
        if (File.Exists(nftLog))
        {
            try
            {
                var t = File.ReadAllText(nftLog);
                sb.AppendLine("nfqws_log=" + t[..Math.Min(300, t.Length)].Replace('\n', ' '));
            }
            catch { }
        }
        return sb.ToString().TrimEnd();
    }

    public static async Task LogAsync(string prefix = "LinuxDiagnose")
    {
        await Task.Yield();
        Logger.Info(prefix + ":\n" + Snapshot());
    }

    private static string RunGetCap(string exe)
    {
        foreach (var getcap in new[] { "/usr/sbin/getcap", "/sbin/getcap", "getcap" })
        {
            if (getcap.StartsWith('/') && !File.Exists(getcap)) continue;
            var o = RunArgs(getcap, new[] { exe });
            if (!string.IsNullOrWhiteSpace(o) &&
                !o.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                return o;
        }
        return "(getcap failed)";
    }

    private static string RunArgs(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "fail-start";
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(true); } catch { }
                return "timeout";
            }
            Task.WaitAll(new Task[] { so, se }, 1500);
            return (so.Result + se.Result).Trim().Replace('\n', ' ');
        }
        catch (Exception ex) { return ex.Message; }
    }
}
