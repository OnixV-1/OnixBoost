using System;
using System.Threading.Tasks;
using OnixBoost.Services;

namespace OnixBoost.Engine;

/// <summary>Linux stub — Games divert is Windows/WinDivert-only. Soft uses nfqws.</summary>
public sealed class OnixDpiEngine : IAsyncDisposable
{
    public bool IsRunning => false;
    public long HitCount => 0;

    public Task StartGamesAsync(bool discordAlsoOn, int repeats = 6)
    {
        Logger.Info("OnixDpiEngine: Games divert not available on Linux (use Soft/nfqws)");
        return Task.CompletedTask;
    }

    public Task StartAsync(DesyncMode mode, int repeats = 6) =>
        StartGamesAsync(false, repeats);

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
