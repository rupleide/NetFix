using System.IO;
using System.Text.Json;
using NetFix.Models;

namespace NetFix.Services;

public static class SettingsService
{
    private static readonly string AppDir = AppContext.BaseDirectory;
    private static readonly string SettingsFile = Path.Combine(AppDir, "settings.json");
    private static readonly string OnboardFile = Path.Combine(AppDir, ".onboarded");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsFile)) return new();
        try
        {
            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(AppSettings s)
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s, JsonOpts)); }
        catch { }
    }

    public static bool IsOnboarded => File.Exists(OnboardFile);

    public static void MarkOnboarded()
    {
        try { File.WriteAllText(OnboardFile, "1"); }
        catch { }
    }

    public static void ResetOnboarding()
    {
        try { if (File.Exists(OnboardFile)) File.Delete(OnboardFile); }
        catch { }
    }
}
