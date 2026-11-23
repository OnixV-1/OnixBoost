using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace OnixBoost.Setup;

internal static class Program
{
    private const string AppName = "OnixBoost";
    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    private static readonly string SetupLog = Path.Combine(Path.GetTempPath(), "onixboost-setup.log");
    private static bool _silent;

    private static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.0.0";
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        _silent = args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)
                               || a.Equals("/S", StringComparison.OrdinalIgnoreCase)
                               || a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (!EnsureAdmin(args))
                return 0;

            if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)
                              || a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                return Uninstall();
            }

            Log($"Setup {AppVersion} silent={_silent} dest={InstallDir}");

            if (!_silent)
            {
                ConsoleAlloc();
                Console.WriteLine($"OnixBoost Setup {AppVersion}");
                Console.WriteLine($"Установка в: {InstallDir}");
                Console.WriteLine();
            }

            KillRunning();
            Thread.Sleep(1500);
            KillRunning();
            Thread.Sleep(500);

            ExtractPayload(InstallDir);
            CreateShortcuts();
            RegisterUninstall();
            if (!_silent)
            {
                Console.WriteLine();
                Console.WriteLine("Готово. Запускаю OnixBoost…");
            }
            Log("OK install complete");
            LaunchApp();
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAIL: " + ex);
            if (!_silent)
            {
                try
                {
                    ConsoleAlloc();
                    Console.Error.WriteLine("Ошибка установки: " + ex.Message);
                    Console.WriteLine("Нажмите Enter…");
                    Console.ReadLine();
                }
                catch { }
            }
            return 1;
        }
    }

    private static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool EnsureAdmin(string[] args)
    {
        if (IsAdmin()) return true;
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
            throw new InvalidOperationException("Нужны права администратора.");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = self,
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            if (!_silent)
            {
                ConsoleAlloc();
                Console.Error.WriteLine("Нужны права администратора (UAC). " + ex.Message);
                Console.WriteLine("Нажмите Enter…");
                Console.ReadLine();
            }
            Log("UAC cancelled: " + ex.Message);
        }
        return false;
    }

    private static void KillRunning()
    {
        foreach (var name in new[] { "OnixBoost", "winws", "sing-box", "zapret" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    if (p.Id == Environment.ProcessId) continue;
                    try
                    {
                        Log($"kill {name} pid={p.Id}");
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(4000);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static void ExtractPayload(string dest)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("OnixBoost.Setup.payload.zip")
            ?? throw new InvalidOperationException("payload.zip не встроен — пересоберите Setup.");

        Directory.CreateDirectory(dest);
        if (!_silent)
            Console.WriteLine("Распаковка…");
        Log("extract to " + dest);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var ok = 0;
        var fail = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
            {
                Directory.CreateDirectory(Path.Combine(dest, entry.FullName));
                continue;
            }

            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var outPath = Path.Combine(dest, rel);
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (TryExtractEntry(entry, outPath))
                ok++;
            else
                fail++;
        }

        Log($"extract done ok={ok} fail={fail}");
        if (!_silent)
            Console.WriteLine($"Файлов: {ok}" + (fail > 0 ? $", пропущено: {fail}" : ""));

        if (!File.Exists(Path.Combine(dest, "OnixBoost.exe")))
            throw new FileNotFoundException("OnixBoost.exe не найден после распаковки");
    }

    private static bool TryExtractEntry(ZipArchiveEntry entry, string outPath)
    {
        for (var i = 0; i < 4; i++)
        {
            try
            {
                entry.ExtractToFile(outPath, overwrite: true);
                return true;
            }
            catch (IOException) when (i < 3)
            {
                try
                {
                    var bak = outPath + ".old";
                    if (File.Exists(bak)) File.Delete(bak);
                    if (File.Exists(outPath)) File.Move(outPath, bak);
                }
                catch { }
                Thread.Sleep(400 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 3)
            {
                Thread.Sleep(400 * (i + 1));
            }
            catch (Exception ex)
            {
                Log("extract skip " + outPath + ": " + ex.Message);
                return false;
            }
        }
        Log("extract FAIL " + outPath);
        return false;
    }

    private static void CreateShortcuts()
    {
        var exe = Path.Combine(InstallDir, "OnixBoost.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("OnixBoost.exe не найден после распаковки", exe);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", AppName);
        Directory.CreateDirectory(startMenu);

        // Recreate shortcuts so Windows picks up the new .exe icon (avoids stale blue cache)
        try
        {
            var oldDesk = Path.Combine(desktop, "OnixBoost.lnk");
            if (File.Exists(oldDesk)) File.Delete(oldDesk);
        }
        catch { }
        try
        {
            var oldSm = Path.Combine(startMenu, "OnixBoost.lnk");
            if (File.Exists(oldSm)) File.Delete(oldSm);
        }
        catch { }
        WriteShortcut(Path.Combine(desktop, "OnixBoost.lnk"), exe, InstallDir);
        WriteShortcut(Path.Combine(startMenu, "OnixBoost.lnk"), exe, InstallDir);
        WriteShortcut(Path.Combine(startMenu, "Удалить OnixBoost.lnk"),
            Environment.ProcessPath ?? Path.Combine(InstallDir, "OnixBoost-Setup.exe"),
            InstallDir, "/uninstall");

        try
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) && File.Exists(self))
                File.Copy(self, Path.Combine(InstallDir, "OnixBoost-Setup.exe"), overwrite: true);
        }
        catch (Exception ex) { Log("copy setup: " + ex.Message); }

        if (!_silent)
            Console.WriteLine("Ярлыки: рабочий стол + меню Пуск");
    }

    private static void WriteShortcut(string lnkPath, string target, string workDir, string? args = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var sc = shell.CreateShortcut(lnkPath);
        sc.TargetPath = target;
        sc.WorkingDirectory = workDir;
        if (!string.IsNullOrEmpty(args))
            sc.Arguments = args;
        sc.Description = "OnixBoost";
        var ico = Path.Combine(workDir, "OnixBoost.exe");
        if (File.Exists(ico))
            sc.IconLocation = ico + ",0";
        sc.Save();
    }

    private static void RegisterUninstall()
    {
        var keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OnixBoost";
        using var key = Registry.LocalMachine.CreateSubKey(keyPath)
            ?? throw new InvalidOperationException("Не удалось записать Uninstall в реестр");
        var setup = Path.Combine(InstallDir, "OnixBoost-Setup.exe");
        key.SetValue("DisplayName", "OnixBoost");
        key.SetValue("DisplayVersion", AppVersion);
        key.SetValue("Publisher", "OnixBoost");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("DisplayIcon", Path.Combine(InstallDir, "OnixBoost.exe"));
        key.SetValue("UninstallString", $"\"{setup}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{setup}\" /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            long size = 0;
            foreach (var f in Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories))
                size += new FileInfo(f).Length;
            key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        }
        catch { }
        if (!_silent)
            Console.WriteLine("Запись в «Программы и компоненты»");
    }

    private static int Uninstall()
    {
        if (!_silent)
        {
            ConsoleAlloc();
            Console.WriteLine("Удаление OnixBoost…");
        }
        KillRunning();
        try
        {
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "OnixBoost.lnk");
            if (File.Exists(desktop)) File.Delete(desktop);
        }
        catch { }
        try
        {
            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", AppName);
            if (Directory.Exists(startMenu))
                Directory.Delete(startMenu, true);
        }
        catch { }
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OnixBoost", throwOnMissingSubKey: false);
        }
        catch { }

        var bat = Path.Combine(Path.GetTempPath(), "onixboost-uninstall.cmd");
        File.WriteAllText(bat, $"""
            @echo off
            timeout /t 2 /nobreak >nul
            rmdir /s /q "{InstallDir}"
            del "%~f0"
            """);
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{bat}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (!_silent)
            Console.WriteLine("Удалено.");
        return 0;
    }

    private static void LaunchApp()
    {
        var exe = Path.Combine(InstallDir, "OnixBoost.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = InstallDir,
            UseShellExecute = true
        });
    }

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(SetupLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private static void ConsoleAlloc()
    {
        try { AllocConsole(); } catch { }
    }
}
