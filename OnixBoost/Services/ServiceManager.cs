using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>Installs OnixBoost DPI as a Windows service via sc.exe (like FuckDiscordPI).</summary>
public sealed class ServiceManager
{
    public const string ServiceName = "OnixBoost";

    public bool IsInstalled()
    {
        try
        {
            var (code, output) = Run("sc.exe", $"query {ServiceName}");
            return code == 0 && output.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task InstallAsync(string argsFileName, bool autoHostList)
    {
        var engine = new WinwsEngine();
        var args = engine.BuildArgs(argsFileName, autoHostList);
        // sc binPath= must be one string; quote exe and each arg with spaces
        // sc.exe syntax: binPath= "C:\path\winws.exe --args..."  (space after =)
        var quotedArgs = string.Join(" ", args.Select(Quote));
        var binPathInner = $"{AppPaths.WinwsExe} {quotedArgs}";

        await UninstallAsync();

        var create = Run("sc.exe", $"create {ServiceName} binPath= \"{binPathInner}\" DisplayName= \"OnixBoost DPI\" start= auto");
        if (create.code != 0)
            throw new InvalidOperationException("sc create failed:\n" + create.output);

        Run("sc.exe", $"description {ServiceName} \"OnixBoost Discord/YouTube/Steam DPI bypass\"");
        var start = Run("sc.exe", $"start {ServiceName}");
        if (start.code != 0)
            Logger.Error("sc start: " + start.output);
        await Task.CompletedTask;
    }

    public Task UninstallAsync()
    {
        Run("sc.exe", $"stop {ServiceName}");
        Run("sc.exe", $"delete {ServiceName}");
        return Task.CompletedTask;
    }

    private static string Quote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (s.Contains(' ') || s.Contains('"'))
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        return s;
    }

    private static (int code, string output) Run(string file, string args)
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
        p.WaitForExit(15000);
        return (p.ExitCode, o);
    }
}
