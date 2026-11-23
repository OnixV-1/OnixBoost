using System.IO;
using System.Text.Json;
using OnixBoost.Models;

namespace OnixBoost.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
            var json = File.ReadAllText(AppPaths.SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings s)
    {
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(s, Opts));
    }
}
