using OnixBoost.Engine;

namespace OnixBoost.Models;

public sealed class AppSettings
{
    public string ActiveProfile { get; set; } = "native_fakesplit";
    /// <summary>Last Flowseal strategy per target.</summary>
    public string? LastFlowsealDiscord { get; set; }
    public string? LastFlowsealYoutube { get; set; }
    public string? LastFlowsealSteam { get; set; }
    public string? LastFlowsealBoth { get; set; }
    /// <summary>Legacy; migrated to LastFlowsealDiscord.</summary>
    public string? LastFlowsealStrategy { get; set; }
    public string IspPreset { get; set; } = "auto";
    public bool AutoHostList { get; set; } = false;
    public bool EnableDoh { get; set; } = true;
    public bool AutostartService { get; set; } = false;
    public bool ShowAnticheatWarning { get; set; } = false;
    public string DohServer { get; set; } = "77.88.8.8";
    public string DohTemplate { get; set; } = "https://common.dot.dns.yandex.net/dns-query";
    /// <summary>Prefer native C# engine over winws.exe</summary>
    public bool UseNativeEngine { get; set; } = true;
}

public sealed class BoostProfile
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public string ArgsFile { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>If set — run OnixDpiEngine with this mode (no winws).</summary>
    public DesyncMode? NativeMode { get; init; }
    public bool IsNative => NativeMode.HasValue;
}
