using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OnixBoost.Models;

namespace OnixBoost.Services;

public sealed class BlockCheckRunner
{
    private readonly Engine.OnixDpiEngine _native = new();
    private readonly WinwsEngine _winws = new();
    private readonly HealthChecker _health = new();

    public async Task<BoostProfile?> FindBestDiscordAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Blockcheck: IPv6 off + Yandex DNS...");
        await NetworkFix.PrepareForDpiAsync();
        await DohHelper.ApplyAsync();

        // winws first — native already failed for this user with hits>0
        var candidates = ProfileCatalog.All
            .OrderBy(p => p.Category == "winws" ? 0 : p.Category == "Native" ? 1 : 2)
            .Where(p => p.Category is "winws" or "Native")
            .ToList();

        foreach (var p in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Testing {p.Title}...");
            try
            {
                await _native.StopAsync();
                await _winws.StopAsync();

                if (p.IsNative)
                    await _native.StartAsync(p.NativeMode!.Value);
                else
                    await _winws.StartAsync(p.ArgsFile, autoHostList: false);

                await Task.Delay(3000, ct);
                var h = await _health.CheckAsync(ct, engineAlive: true);
                progress?.Report($"{p.Title}: {(h.DiscordReachable ? "OK" : "FAIL")} ({h.DiscordMs} ms)");
                if (h.DiscordReachable)
                {
                    progress?.Report($"Selected {p.Title}");
                    return p;
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"{p.Title}: error {ex.Message}");
            }
        }

        await _native.StopAsync();
        await _winws.StopAsync();
        return null;
    }
}
