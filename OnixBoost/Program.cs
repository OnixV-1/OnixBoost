using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;

namespace OnixBoost;

internal sealed class Program
{
    private const string MutexName = "OnixBoost.SingleInstance.v1";
    private const string ShowEventName = "OnixBoost.ShowMain.v1";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static CancellationTokenSource? _showCts;

    [STAThread]
    public static void Main(string[] args)
    {
#if !WINDOWS
        // Desktop launchers often omit /usr/sbin — getcap/setcap must still be findable.
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Contains("/usr/sbin", StringComparison.Ordinal))
                Environment.SetEnvironmentVariable("PATH",
                    "/usr/sbin:/sbin:/usr/local/sbin:" + path);
        }
        catch { }
#endif
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
#if WINDOWS
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch { /* first instance may be exiting */ }
#endif
            return;
        }

        try
        {
#if WINDOWS
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
#endif
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
