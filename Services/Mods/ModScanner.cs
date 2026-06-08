using System.IO;
using System.Text.Json;
using NetFix.Models;

namespace NetFix.Services.Mods;

public static class ModScanner
{
    private static readonly string AppDir = AppContext.BaseDirectory;
    private static readonly string ModsDir = Path.Combine(AppDir, "Mods");
    private static readonly string StrategiesDir = Path.Combine(ModsDir, "strategies");
    private static readonly string ListsDir = Path.Combine(ModsDir, "lists");
    private static readonly string BuildsDir = Path.Combine(ModsDir, "builds");
    private static readonly string BackupDir = Path.Combine(ModsDir, "_backup");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string ModsRoot => ModsDir;
    public static string StrategiesRoot => StrategiesDir;
    public static string ListsRoot => ListsDir;
    public static string BackupRoot => BackupDir;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(StrategiesDir);
        Directory.CreateDirectory(ListsDir);
        Directory.CreateDirectory(BuildsDir);
        Directory.CreateDirectory(BackupDir);
    }

    public static List<ModEntry> ScanAll(List<string> activeStrategyMods, List<string> activeListMods)
    {
        var result = new List<ModEntry>();

        result.AddRange(ScanFolder(StrategiesDir, ModType.Strategy, activeStrategyMods));
        result.AddRange(ScanFolder(ListsDir, ModType.List, activeListMods));

        return result;
    }

    private static List<ModEntry> ScanFolder(string folder, ModType type, List<string> activeNames)
    {
        var entries = new List<ModEntry>();

        if (!Directory.Exists(folder))
            return entries;

        foreach (var modDir in Directory.GetDirectories(folder))
        {
            var metaPath = Path.Combine(modDir, "mod.json");
            if (!File.Exists(metaPath))
                continue;

            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<ModMeta>(json, JsonOpts);
                if (meta is null)
                    continue;

                var dirName = Path.GetFileName(modDir);
                var modType = meta.Type switch
                {
                    "strategy" => ModType.Strategy,
                    "list" => ModType.List,
                    "build" => ModType.Build,
                    _ => type,
                };

                var entry = new ModEntry(
                    Name: meta.Name,
                    Author: meta.Author,
                    Version: meta.Version,
                    Description: meta.Description,
                    Type: modType,
                    FolderPath: modDir,
                    RequiredBuild: string.IsNullOrEmpty(meta.RequiredBuild) ? null : meta.RequiredBuild
                )
                {
                    IsActive = activeNames.Contains(dirName),
                    SortOrder = 0,
                };

                entries.Add(entry);
            }
            catch
            {
                // skip invalid mod
            }
        }

        return entries;
    }

    public static string? FindStrategyBat(ModEntry mod)
    {
        if (mod.Type != ModType.Strategy)
            return null;

        var batPath = Path.Combine(mod.FolderPath, "strategy.bat");
        return File.Exists(batPath) ? batPath : null;
    }

    public static string? FindListFile(ModEntry mod)
    {
        if (mod.Type != ModType.List)
            return null;

        var listPath = Path.Combine(mod.FolderPath, "list.txt");
        return File.Exists(listPath) ? listPath : null;
    }

    public static string GetModDirName(ModEntry mod)
    {
        return Path.GetFileName(mod.FolderPath);
    }
}
