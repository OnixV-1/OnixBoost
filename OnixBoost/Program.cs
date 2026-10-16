using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;

namespace OnixBoost;

internal sealed class Program
{
    private const string MutexName = @"Local\OnixBoost.SingleInstance.v1";
    private const string ShowEventName = @"Local\OnixBoost.ShowMain.v1";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static CancellationTokenSource? _showCts;

    [STAThread]
    public static void Main(string[] args)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running — ask the first instance to show the window
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch { /* first instance may be exiting */ }
            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try { _showCts?.Cancel(); } catch { }
            try { _showEvent?.Dispose(); } catch { }
            try
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            catch { }
        }
    }

    /// <summary>Listen for second-launch signals and bring MainWindow to front.</summary>
    public static void StartSingleInstanceWatcher()
    {
        if (_showEvent == null) return;
        _showCts = new CancellationTokenSource();
        var ct = _showCts.Token;
        var ev = _showEvent;
        _ = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!ev.WaitOne(500))
                        continue;
                    Dispatcher.UIThread.Post(() => App.ShowMainFromSecondInstance());
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch { /* keep watching */ }
            }
        }, ct);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
