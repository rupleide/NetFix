using System.IO;
using System.IO.Compression;
using System.Text.Json;
using NetFix.Models;

namespace NetFix.Services.Mods;

public static class ModPackager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<string> ExportAsync(ModEntry mod, string outputDir)
    {
        EnsureDirectory(outputDir);

        var zipName = SanitizeFileName(mod.Name) + ".netfix-mod";
        var zipPath = Path.Combine(outputDir, zipName);

        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var meta = new ModMeta(
            Name: mod.Name,
            Author: mod.Author,
            Version: mod.Version,
            Description: mod.Description,
            Type: mod.Type.ToString().ToLowerInvariant(),
            RequiredBuild: mod.RequiredBuild ?? ""
        );

        var metaJson = JsonSerializer.Serialize(meta, JsonOpts);
        var metaEntry = archive.CreateEntry("mod.json");
        using (var writer = new StreamWriter(metaEntry.Open()))
            await writer.WriteAsync(metaJson);

        if (mod.Type == ModType.Strategy)
        {
            var batPath = Path.Combine(mod.FolderPath, "strategy.bat");
            if (File.Exists(batPath))
                archive.CreateEntryFromFile(batPath, "strategy.bat");
        }
        else if (mod.Type == ModType.List)
        {
            var listPath = Path.Combine(mod.FolderPath, "list.txt");
            if (File.Exists(listPath))
                archive.CreateEntryFromFile(listPath, "list.txt");
        }

        return zipPath;
    }

    public static async Task<(ModMeta? Meta, string? Error)> ReadModMetaFromArchive(string zipPath)
    {
        try
        {
            using var stream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var metaEntry = archive.GetEntry("mod.json");
            if (metaEntry is null)
                return (null, "Архив не содержит mod.json");

            using var reader = new StreamReader(metaEntry.Open());
            var json = await reader.ReadToEndAsync();

            var meta = JsonSerializer.Deserialize<ModMeta>(json, JsonOpts);
            if (meta is null || string.IsNullOrEmpty(meta.Name))
                return (null, "Не удалось прочитать метаданные мода");

            return (meta, null);
        }
        catch (Exception ex)
        {
            return (null, $"Ошибка чтения архива: {ex.Message}");
        }
    }

    public static async Task<(ModEntry? Entry, string? Error)> ImportAsync(
        string zipPath, List<string> activeStrategyMods, List<string> activeListMods)
    {
        var (meta, error) = await ReadModMetaFromArchive(zipPath);
        if (meta is null || error is not null)
            return (null, error);

        var modType = meta.Type switch
        {
            "strategy" => ModType.Strategy,
            "list" => ModType.List,
            "build" => ModType.Build,
            _ => ModType.Strategy,
        };

        var targetDir = modType switch
        {
            ModType.Strategy => ModScanner.StrategiesRoot,
            ModType.List => ModScanner.ListsRoot,
            _ => ModScanner.StrategiesRoot,
        };

        var dirName = SanitizeFileName(meta.Name);
        var modDir = Path.Combine(targetDir, dirName);

        if (Directory.Exists(modDir))
            return (null, $"Мод '{meta.Name}' уже установлен");

        Directory.CreateDirectory(modDir);

        try
        {
            using var stream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (entry.Name == "mod.json" || entry.Name == "strategy.bat" || entry.Name == "list.txt")
                {
                    var destPath = Path.Combine(modDir, entry.Name);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            // cleanup on failure
            try { Directory.Delete(modDir, true); } catch { }
            return (null, $"Ошибка распаковки: {ex.Message}");
        }

        var activeNames = modType == ModType.Strategy ? activeStrategyMods : activeListMods;

        var entry_ = new ModEntry(
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
        };

        return (entry_, null);
    }

    public static ModEntry CreateNewMod(
        string name, string author, string version, string description, ModType type,
        string? batSourcePath, string? listContent,
        List<string> activeStrategyMods, List<string> activeListMods)
    {
        var dirName = SanitizeFileName(name);
        var targetDir = type switch
        {
            ModType.Strategy => ModScanner.StrategiesRoot,
            ModType.List => ModScanner.ListsRoot,
            _ => ModScanner.StrategiesRoot,
        };

        var modDir = Path.Combine(targetDir, dirName);
        Directory.CreateDirectory(modDir);

        var meta = new ModMeta(
            Name: name,
            Author: author,
            Version: version,
            Description: description,
            Type: type.ToString().ToLowerInvariant(),
            RequiredBuild: ""
        );

        var metaJson = JsonSerializer.Serialize(meta, JsonOpts);
        File.WriteAllText(Path.Combine(modDir, "mod.json"), metaJson);

        if (type == ModType.Strategy && batSourcePath is not null && File.Exists(batSourcePath))
            File.Copy(batSourcePath, Path.Combine(modDir, "strategy.bat"), overwrite: true);

        if (type == ModType.List && listContent is not null)
            File.WriteAllText(Path.Combine(modDir, "list.txt"), listContent);

        var activeNames = type == ModType.Strategy ? activeStrategyMods : activeListMods;

        return new ModEntry(
            Name: name,
            Author: author,
            Version: version,
            Description: description,
            Type: type,
            FolderPath: modDir,
            RequiredBuild: null
        )
        {
            IsActive = activeNames.Contains(dirName),
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "mod" : sanitized.Trim();
    }

    private static void EnsureDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}
