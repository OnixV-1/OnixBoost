using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OnixBoost.Services;

namespace OnixBoost;

public partial class App : Application
{
    public static MainWindow? MainWnd { get; set; }
    public static bool ExitRequested { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            MainWnd = new MainWindow();
            desktop.MainWindow = MainWnd;
            Program.StartSingleInstanceWatcher();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowMain();

    private void OnTrayShow(object? sender, EventArgs e) => ShowMain();

    private async void OnTrayStop(object? sender, EventArgs e)
    {
        if (MainWnd == null) return;
        try
        {
            // Off UI thread — DNS/WinDivert stop must not freeze the tray menu
            await Task.Run(async () =>
            {
                var stop = MainWnd.StopFromTrayAsync();
                await Task.WhenAny(stop, Task.Delay(10000));
            }).ConfigureAwait(true);
        }
        catch { }
    }

    private void OnTrayExit(object? sender, EventArgs e)
    {
        // Never call Shutdown() here — OnClosing does StopAllAsync().Wait() on the UI
        // thread and freezes the tray menu. Hard-exit after a short background cleanup.
        ExitRequested = true;
        _ = ExitFromTrayAsync();
    }

    private static async Task ExitFromTrayAsync()
    {
        try
        {
            if (MainWnd != null)
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        var stop = MainWnd.StopFromTrayAsync();
                        await Task.WhenAny(stop, Task.Delay(6000));
                    }
                    catch { }
                    try { ProcessNames.KillAllWorkers(); } catch { }
                }).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            Environment.Exit(0);
        }
    }

    public static void ShowMainFromSecondInstance() => ShowMain();

    private static void ShowMain()
    {
        if (MainWnd == null) return;
        MainWnd.Show();
        MainWnd.WindowState = WindowState.Normal;
        MainWnd.Activate();
        MainWnd.Topmost = true;
        MainWnd.Topmost = false;
    }
}
