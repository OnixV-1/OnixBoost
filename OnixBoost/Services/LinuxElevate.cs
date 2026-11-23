using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnixBoost.Services;

/// <summary>
/// Linux privilege helper. Never blocks the UI on pkexec/terminal password prompts —
/// Soft needs a one-time <c>fix-caps.sh</c> run in a normal terminal.
/// GUI apps often lack /usr/sbin in PATH — always call getcap by absolute path.
/// </summary>
public static class LinuxElevate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static int Euid
    {
        get
        {
            try { return (int)Interop.geteuid(); }
            catch { return 1; }
        }
    }

    public static bool IsRoot => Euid == 0;

    public static string NftHelperPath => Path.Combine(AppPaths.BinDir, "onixboost-nft");
    public static string FixCapsPath => Path.Combine(AppPaths.Root, "fix-caps.sh");

    public static string CapsHint =>
        "Один раз в обычном терминале (не из окна OnixBoost):\n" +
        "  bash " + FixCapsPath + "\n" +
        "Затем трей → Выход и откройте OnixBoost снова.";

    private static readonly string[] GetCapBins =
    {
        "/usr/sbin/getcap", "/sbin/getcap", "getcap"
    };

    private static readonly string[] SudoBins =
    {
        "/usr/bin/sudo", "/bin/sudo", "sudo"
    };

    public static bool HasCapNetAdmin(string exe)
    {
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        EnsurePathHasSbin();
        foreach (var getcap in GetCapBins)
        {
            try
            {
                // ArgumentList only — NEVER shell-quote paths (GUI getcap saw literal 'path' → No such file).
                if (getcap.StartsWith('/') && !File.Exists(getcap)) continue;
                var o = RunCaptureArgs(getcap, new[] { exe }, timeoutMs: 3000);
                if (string.IsNullOrWhiteSpace(o)) continue;
                if (o.Contains("cap_net_admin", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!o.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                    !o.Contains("No such file", StringComparison.OrdinalIgnoreCase) &&
                    !o.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch { /* try next */ }
        }
        return false;
    }

    public static bool SoftCapsReady => HasCapNetAdmin(AppPaths.NfqwsExe);
    public static bool TunnelCapsReady => HasCapNetAdmin(AppPaths.CoreExe);

    private static bool? _nftSudoCache;

    /// <summary>Invalidate after fix-caps / install.</summary>
    public static void InvalidatePrivilegeCache() => _nftSudoCache = null;

    /// <summary>Non-destructive: sudo -n can invoke helper (usage → exit 2 still means auth OK).</summary>
    public static bool NftSudoReady()
    {
        if (_nftSudoCache.HasValue) return _nftSudoCache.Value;
        if (IsRoot)
        {
            _nftSudoCache = File.Exists(NftHelperPath);
            return _nftSudoCache.Value;
        }
        if (!File.Exists(NftHelperPath))
        {
            _nftSudoCache = false;
            return false;
        }
        try
        {
            var sudo = ResolveSudo();
            if (sudo == null)
            {
                _nftSudoCache = false;
                return false;
            }
            var (ok, output, code) = ExecSync(sudo, new[] { "-n", NftHelperPath }, timeoutMs: 2500);
            // exit 0 unexpected (no args → usage); exit 2 = usage printed = sudo NOPASSWD works
            _nftSudoCache = ok || code == 2 || output.Contains("usage", StringComparison.OrdinalIgnoreCase);
            return _nftSudoCache.Value;
        }
        catch
        {
            _nftSudoCache = false;
            return false;
        }
    }

    /// <summary>Soft needs setcap on nfqws AND passwordless sudo for onixboost-nft.</summary>
    public static bool SoftReady => IsRoot || (SoftCapsReady && NftSudoReady());

    /// <summary>
    /// Fast check only — never opens terminal/pkexec (that froze the GUI for minutes).
    /// Optionally tries passwordless <c>sudo -n bash fix-caps.sh</c> once.
    /// </summary>
    public static async Task<(bool ok, string message)> EnsureSoftPrivilegesAsync(
        IProgress<string>? progress = null)
    {
        if (!PlatformOs.IsLinux) return (true, "windows");
        EnsurePathHasSbin();

        if (SoftReady)
        {
            Logger.Info("LinuxElevate: SoftReady=true caps=" + SoftCapsReady + " nft_sudo=" + NftSudoReady());
            return (true, "already");
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsurePathHasSbin();
            InvalidatePrivilegeCache();
            if (SoftReady) return (true, "already");

            EnsureFixCapsScript();
            var script = FixCapsPath;
            if (!File.Exists(script))
                return (false, "нет fix-caps.sh — переустановите OnixBoost 2.10.8+\n" + CapsHint);

            // Passwordless sudo only — never block on interactive password / pkexec / terminal.
            progress?.Report("Проверка sudo -n…");
            var sudo = ResolveSudo();
            if (sudo != null)
            {
                Logger.Info("LinuxElevate: trying sudo -n fix-caps (no password prompt)");
                var (ok, output, _) = await ExecAsync(sudo, new[] { "-n", "/bin/bash", script }, timeoutMs: 8000)
                    .ConfigureAwait(false);
                EnsurePathHasSbin();
                InvalidatePrivilegeCache();
                if (SoftReady)
                {
                    Logger.Info("LinuxElevate: OK after sudo -n · " + output[..Math.Min(120, output.Length)]);
                    return (true, "sudo -n ok");
                }
                Logger.Info("LinuxElevate: sudo -n fix-caps failed · " + output[..Math.Min(200, output.Length)]);
            }

            Logger.Error("LinuxElevate: Soft not ready — caps=" + SoftCapsReady +
                         " nft_sudo=" + NftSudoReady() + " (HY2 Discord fallback available)");
            return (false, CapsHint);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Run onixboost-nft. Never uses pkexec (hangs UI / exit). Cleanup should pass bestEffort.
    /// </summary>
    public static async Task<(bool ok, string output)> RunNftHelperAsync(
        string helperArgs, bool bestEffort = false)
    {
        if (!File.Exists(NftHelperPath))
            return (false, "onixboost-nft missing\n" + CapsHint);

        var timeout = bestEffort ? 2500 : 8000;
        var argv = SplitArgs(helperArgs);

        if (IsRoot)
        {
            var (ok, o, _) = await ExecAsync(
                "/bin/bash", new[] { NftHelperPath }.Concat(argv).ToArray(), timeout).ConfigureAwait(false);
            return (ok, o);
        }

        var sudo = ResolveSudo();
        if (sudo != null)
        {
            var (ok, o, _) = await ExecAsync(
                sudo, new[] { "-n", NftHelperPath }.Concat(argv).ToArray(), timeout).ConfigureAwait(false);
            if (ok) return (true, o);
            if (bestEffort)
            {
                Logger.Info("LinuxElevate: nft best-effort sudo -n failed (ignored): " +
                            o[..Math.Min(120, o.Length)]);
                return (false, o);
            }
            return (false, "Не удалось настроить NFQUEUE (нужен fix-caps).\n" + CapsHint + "\n" + o);
        }

        if (bestEffort) return (false, "no sudo");
        return (false, "sudo не найден.\n" + CapsHint);
    }

    public static ProcessStartInfo SoftStartInfo(string scriptPath, string workDir) =>
        new()
        {
            FileName = "/bin/bash",
            ArgumentList = { scriptPath },
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };

    public static ProcessStartInfo TunnelStartInfo(string coreExe, string workDir, string arguments, bool needsTun)
    {
        _ = needsTun;
        return new ProcessStartInfo
        {
            FileName = coreExe,
            WorkingDirectory = workDir,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private static void EnsurePathHasSbin()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            const string extra = "/usr/sbin:/sbin:/usr/local/sbin";
            if (path.Contains("/usr/sbin", StringComparison.Ordinal)) return;
            Environment.SetEnvironmentVariable("PATH", extra + ":" + path);
        }
        catch { }
    }

    private static string? ResolveSudo()
    {
        foreach (var s in SudoBins)
        {
            if (s.StartsWith('/') && File.Exists(s)) return s;
            if (!s.StartsWith('/')) return s;
        }
        return null;
    }

    private static void EnsureFixCapsScript()
    {
        var path = FixCapsPath;
        if (File.Exists(path)) return;
        try
        {
            File.WriteAllText(path, """
                #!/bin/bash
                set -euo pipefail
                ROOT="$(cd "$(dirname "$0")" && pwd)"
                NFQ="$ROOT/dpi/bin/nfqws"
                SING="$ROOT/dpi/core/sing-box"
                HELPER="$ROOT/dpi/bin/onixboost-nft"
                if [[ -n "${PKEXEC_UID:-}" ]]; then USER_NAME="$(id -un "$PKEXEC_UID")"
                elif [[ -n "${SUDO_USER:-}" && "$SUDO_USER" != root ]]; then USER_NAME="$SUDO_USER"
                else USER_NAME="$(id -un)"; fi
                [[ "$USER_NAME" != root ]] || exit 1
                run(){ if [[ $(id -u) -eq 0 ]]; then "$@"; else sudo "$@"; fi; }
                chmod +x "$NFQ" "$HELPER" 2>/dev/null || true
                [[ -f "$SING" ]] && chmod +x "$SING" || true
                run /usr/sbin/setcap 'cap_net_admin,cap_net_raw+ep' "$NFQ"
                [[ -x "$SING" ]] && run /usr/sbin/setcap 'cap_net_admin,cap_net_raw+ep' "$SING" || true
                TMP=$(mktemp)
                printf '%s ALL=(root) NOPASSWD: %s\n' "$USER_NAME" "$HELPER" >"$TMP"
                run install -m 440 "$TMP" "/etc/sudoers.d/onixboost-${USER_NAME}"
                rm -f "$TMP"
                /usr/sbin/getcap "$NFQ" || true
                echo OK
                """);
            RunCapture("chmod", "+x " + Quote(path), 3000);
        }
        catch (Exception ex) { Logger.Error("EnsureFixCapsScript: " + ex.Message); }
    }

    private static string[] SplitArgs(string args)
    {
        var list = new System.Collections.Generic.List<string>();
        var sb = new StringBuilder();
        var inQ = false;
        foreach (var ch in args.Trim())
        {
            if (ch == '"') { inQ = !inQ; continue; }
            if (!inQ && char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list.ToArray();
    }

    private static string Quote(string p) => "'" + p.Replace("'", "'\\''") + "'";

    private static string RunCapture(string file, string args, int timeoutMs = 5000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null) return "";
            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return "timeout";
            }
            if (!Task.WaitAll(new Task[] { soTask, seTask }, Math.Min(2000, timeoutMs)))
                return "timeout";
            return (soTask.Result + seTask.Result).Trim();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string RunCaptureArgs(string file, string[] args, int timeoutMs = 5000)
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
            if (p == null) return "";
            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return "timeout";
            }
            if (!Task.WaitAll(new Task[] { soTask, seTask }, Math.Min(2000, timeoutMs)))
                return "timeout";
            return (soTask.Result + seTask.Result).Trim();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static (bool ok, string output, int code) ExecSync(string file, string[] args, int timeoutMs)
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
            if (p == null) return (false, "failed to start " + file, -1);
            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (false, "timeout", -1);
            }
            if (!Task.WaitAll(new Task[] { soTask, seTask }, Math.Min(2000, timeoutMs)))
                return (false, "timeout", -1);
            var o = (soTask.Result + seTask.Result).Trim();
            if (p.ExitCode != 0 && string.IsNullOrEmpty(o)) o = "exit " + p.ExitCode;
            return (p.ExitCode == 0, o, p.ExitCode);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, -1);
        }
    }

    private static async Task<(bool ok, string output, int code)> ExecAsync(
        string file, string[] args, int timeoutMs = 8000)
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
            if (p == null) return (false, "failed to start " + file, -1);

            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => p.WaitForExit(timeoutMs)).ConfigureAwait(false);
            if (!exited)
            {
                try { p.Kill(true); } catch { }
                return (false, "timeout", -1);
            }
            var so = await soTask.ConfigureAwait(false);
            var se = await seTask.ConfigureAwait(false);
            var o = (so + se).Trim();
            if (p.ExitCode != 0 && string.IsNullOrEmpty(o)) o = "exit " + p.ExitCode;
            return (p.ExitCode == 0, o, p.ExitCode);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, -1);
        }
    }

    private static class Interop
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
        public static extern uint geteuid();
    }
}
