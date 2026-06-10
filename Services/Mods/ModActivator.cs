using System.IO;
using System.Linq;
using NetFix.Models;

namespace NetFix.Services.Mods;

public static class ModActivator
{
    private const string ZapretDir = @"C:\Zapret";

    /// <summary>
    /// Applies list mods to their target files.
    /// Pass ALL list mods (active + inactive). Active mods' domains are added;
    /// inactive mods' domains are removed from the file (if they were previously applied).
    /// No backup needed — the current file state is the source of truth.
    /// Returns (success, errorMessage).
    /// </summary>
    public static (bool Success, string? Error) ApplyListMods(List<ModEntry> allListMods)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[ApplyListMods] total mods passed: {allListMods.Count}");
            foreach (var m in allListMods)
                System.Diagnostics.Debug.WriteLine($"[ApplyListMods] mod={m.Name} isActive={m.IsActive} targetFile={m.TargetFile} folder={m.FolderPath}");

            var byTarget = allListMods
                .Where(m => m.Type == ModType.List)
                .GroupBy(m => ResolveTargetFile(m.TargetFile))
                .ToList();

            System.Diagnostics.Debug.WriteLine($"[ApplyListMods] groups by target: {byTarget.Count}");

            if (byTarget.Count == 0)
                return (true, null);

            foreach (var group in byTarget)
            {
                var targetPath = group.Key;
                System.Diagnostics.Debug.WriteLine($"[ApplyListMods] group targetPath={targetPath}");
                if (targetPath is null) continue;

                // собираем домены всех листов (активных и нет)
                var allModDomains = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in group)
                {
                    var dirName = ModScanner.GetModDirName(mod);
                    var listFile = ModScanner.FindListFile(mod);
                    System.Diagnostics.Debug.WriteLine($"[ApplyListMods] mod={mod.Name} dirName={dirName} listFile={listFile} fileExists={listFile != null && File.Exists(listFile)}");
                    if (listFile != null && File.Exists(listFile))
                        System.Diagnostics.Debug.WriteLine($"[ApplyListMods] list.txt raw: '{File.ReadAllText(listFile)}'");

                    var domains = ReadModDomains(mod);
                    System.Diagnostics.Debug.WriteLine($"[ApplyListMods] domains read: {domains.Count} values=[{string.Join(",", domains)}]");
                    if (domains.Count > 0)
                        allModDomains[dirName] = domains;
                }

                var allDomainSet = new HashSet<string>(
                    allModDomains.Values.SelectMany(d => d),
                    StringComparer.OrdinalIgnoreCase);

                // читаем текущий файл
                var currentLines = File.Exists(targetPath)
                    ? File.ReadAllLines(targetPath)
                    : [];

                // собираем результат: всё из файла КРОМЕ строк совпадающих с ЛЮБЫМ модом
                var result = new List<string>();
                foreach (var line in currentLines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !allDomainSet.Contains(trimmed))
                        result.Add(trimmed);
                }

                // добавляем домены только АКТИВНЫХ модов
                foreach (var mod in group.Where(m => m.IsActive))
                {
                    var dirName = ModScanner.GetModDirName(mod);
                    System.Diagnostics.Debug.WriteLine($"[ApplyListMods] active mod dirName={dirName} hasEntry={allModDomains.ContainsKey(dirName)}");
                    if (allModDomains.TryGetValue(dirName, out var domains))
                    {
                        foreach (var d in domains.OrderBy(d => d))
                        {
                            System.Diagnostics.Debug.WriteLine($"[ApplyListMods] adding domain: {d}");
                            result.Add(d);
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[ApplyListMods] writing {result.Count} lines to {targetPath}");
                System.Diagnostics.Debug.WriteLine($"[ApplyListMods] result content:\n{string.Join("\n", result)}");
                File.WriteAllLines(targetPath, result);
                System.Diagnostics.Debug.WriteLine($"[ApplyListMods] write complete. File exists={File.Exists(targetPath)}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка применения списков доменов: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a mod's list.txt and returns unique non-empty, non-comment lines.
    /// Returns empty set if mod has no list file.
    /// </summary>
    private static HashSet<string> ReadModDomains(ModEntry mod)
    {
        var listFile = ModScanner.FindListFile(mod);
        if (listFile is null)
            return [];

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(listFile))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                domains.Add(trimmed);
        }

        return domains;
    }

    private static string? ResolveTargetFile(string? targetFile)
    {
        var name = targetFile;
        if (string.IsNullOrEmpty(name))
            name = "list-general.txt";

        return Path.Combine(ZapretDir, "lists", name);
    }

    /// <summary>
    /// Static analysis of bat file for dangerous patterns.
    /// Returns a list of found dangerous patterns (empty = safe).
    /// </summary>
    public static List<string> AnalyzeBatFile(string batPath)
    {
        var dangerous = new List<string>();
        if (!File.Exists(batPath))
            return dangerous;

        var content = File.ReadAllText(batPath);
        var patterns = new[]
        {
            "powershell", "curl", "wget", "certutil",
            "reg add", "schtasks", "net user",
            "http://", "https://", "ftp://"
        };

        foreach (var pattern in patterns)
        {
            if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                dangerous.Add(pattern);
        }

        return dangerous;
    }
}
