namespace OnixBoost.Engine;

public enum DesyncMode
{
    /// <summary>Fake TLS (TTL=1) then original — classic DPI poison.</summary>
    FakeTls,
    /// <summary>Split ClientHello at byte 1.</summary>
    Split1,
    /// <summary>Fake (badseq) + split at mid-SNI-ish position.</summary>
    FakeSplit,
    /// <summary>Multi-split at positions 1 and ~50%.</summary>
    MultiSplit,
    /// <summary>Disorder-ish: second half first (seq adjusted), then first half.</summary>
    Disorder,
}

public static class DesyncModeCatalog
{
    public static (DesyncMode mode, string title, string desc)[] All { get; } =
    [
        (DesyncMode.FakeTls, "Native FakeTLS", "Own engine: fake ClientHello TTL=1"),
        (DesyncMode.Split1, "Native Split", "Own engine: TCP split pos=1"),
        (DesyncMode.FakeSplit, "Native Fake+Split", "Own engine: badseq fake + split"),
        (DesyncMode.MultiSplit, "Native MultiSplit", "Own engine: two-cut multisplit"),
        (DesyncMode.Disorder, "Native Disorder", "Own engine: reverse segment order"),
    ];
}
