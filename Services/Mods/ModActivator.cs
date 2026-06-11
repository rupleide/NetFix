using System.IO;
using System.Linq;
using NetFix.Models;

namespace NetFix.Services.Mods;

public static class ModActivator
{
    private const string ZapretDir = @"C:\Zapret";

    public static (bool Success, string? Error) ApplyListMods(List<ModEntry> allListMods)
    {
        try
        {
            var byTarget = allListMods
                .Where(m => m.Type == ModType.List)
                .GroupBy(m => ResolveTargetFile(m.TargetFile))
                .ToList();

            if (byTarget.Count == 0)
                return (true, null);

            foreach (var group in byTarget)
            {
                var targetPath = group.Key;
                if (targetPath is null) continue;

                var allModDomains = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in group)
                {
                    var dirName = ModScanner.GetModDirName(mod);
                    var listFile = ModScanner.FindListFile(mod);
                    if (listFile != null && File.Exists(listFile))
                    { /* bypassed */ }

                    var domains = ReadModDomains(mod);
                    if (domains.Count > 0)
                        allModDomains[dirName] = domains;
                }

                var allDomainSet = new HashSet<string>(
                    allModDomains.Values.SelectMany(d => d),
                    StringComparer.OrdinalIgnoreCase);

                var currentLines = File.Exists(targetPath)
                    ? File.ReadAllLines(targetPath)
                    : [];

                var result = new List<string>();
                foreach (var line in currentLines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !allDomainSet.Contains(trimmed))
                        result.Add(trimmed);
                }

                foreach (var mod in group.Where(m => m.IsActive))
                {
                    var dirName = ModScanner.GetModDirName(mod);
                    if (allModDomains.TryGetValue(dirName, out var domains))
                    {
                        foreach (var d in domains.OrderBy(d => d))
                        {
                            result.Add(d);
                        }
                    }
                }

                File.WriteAllLines(targetPath, result);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка применения списков доменов: {ex.Message}");
        }
    }

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
