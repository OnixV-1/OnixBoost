using System.Collections.Generic;
using OnixBoost.Engine;
using OnixBoost.Models;

namespace OnixBoost.Services;

public static class ProfileCatalog
{
    public static IReadOnlyList<BoostProfile> All { get; } = Build();

    private static List<BoostProfile> Build()
    {
        var list = new List<BoostProfile>
        {
            new()
            {
                Id = "native_fakesplit", Title = "Native Fake+Split", Category = "Native",
                NativeMode = DesyncMode.FakeSplit,
                Description = "Own C# engine — fake TLS + TCP split (recommended)"
            },
            new()
            {
                Id = "native_fake", Title = "Native FakeTLS", Category = "Native",
                NativeMode = DesyncMode.FakeTls,
                Description = "Own C# engine — TTL=1 fake ClientHello"
            },
            new()
            {
                Id = "native_split", Title = "Native Split", Category = "Native",
                NativeMode = DesyncMode.Split1,
                Description = "Own C# engine — split pos=1"
            },
            new()
            {
                Id = "native_multi", Title = "Native MultiSplit", Category = "Native",
                NativeMode = DesyncMode.MultiSplit,
                Description = "Own C# engine — multisplit"
            },
            new()
            {
                Id = "native_disorder", Title = "Native Disorder", Category = "Native",
                NativeMode = DesyncMode.Disorder,
                Description = "Own C# engine — reverse segments"
            },

            // Legacy winws/Flowseal profiles kept as fallback
            new() { Id = "discord_main", Title = "winws General", Category = "winws", ArgsFile = "discord_main.txt", Description = "Flowseal winws fallback" },
            new() { Id = "discord_simple", Title = "winws Simple", Category = "winws", ArgsFile = "discord_simple.txt", Description = "winws SIMPLE FAKE" },
            new() { Id = "discord_fake_tls", Title = "winws FakeTLS", Category = "winws", ArgsFile = "discord_fake_tls.txt", Description = "winws FAKE TLS" },
            new() { Id = "discord_alternative_1", Title = "winws Alt1", Category = "winws", ArgsFile = "discord_alternative_1.txt", Description = "winws ALT" },
            new() { Id = "discord_alternative_9", Title = "winws Alt9", Category = "winws", ArgsFile = "discord_alternative_9.txt", Description = "winws hostfakesplit" },
            new() { Id = "youtube_main", Title = "YouTube (winws)", Category = "YouTube", ArgsFile = "youtube_main.txt", Description = "YouTube via winws" },
            new() { Id = "steam_main", Title = "Steam (winws)", Category = "Games", ArgsFile = "steam_main.txt", Description = "Steam via winws" },
            new() { Id = "discord_steam", Title = "Discord+Steam (winws)", Category = "Games", ArgsFile = "discord_steam.txt", Description = "winws combo" },
        };
        return list;
    }

    public static string ResolveIspProfile(string isp) => isp switch
    {
        "mts" or "mgts" => "native_fakesplit",
        "rostelecom" => "native_disorder",
        "megafon" => "native_multi",
        "tele2" => "native_split",
        "beeline" => "native_fake",
        _ => "native_fakesplit"
    };
}
