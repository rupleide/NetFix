using System.IO;
using NetFix.Models;

namespace NetFix.Services.Mods;

public static class ModActivator
{
    private const string ZapretDir = @"C:\Zapret";
    private const string ListGeneralFile = "list-general.txt";

    /// <summary>
    /// Merges active list mods into list-general.txt with backup.
    /// Returns (success, errorMessage).
    /// </summary>
    public static (bool Success, string? Error) ApplyListMods(List<ModEntry> activeLists)
    {
        try
        {
            var listPath = Path.Combine(ZapretDir, ListGeneralFile);

            // Backup original
            BackupOriginal(listPath);

            // Merge all active list mods
            var mergedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in activeLists.Where(m => m.Type == ModType.List && m.IsActive))
            {
                var listFile = ModScanner.FindListFile(mod);
                if (listFile is null) continue;

                foreach (var line in File.ReadAllLines(listFile))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                        mergedDomains.Add(trimmed);
                }
            }

            // Write merged list
            if (mergedDomains.Count > 0)
            {
                File.WriteAllLines(listPath, mergedDomains.OrderBy(d => d));
            }
            else if (File.Exists(listPath))
            {
                File.WriteAllText(listPath, "");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка применения списков доменов: {ex.Message}");
        }
    }

    /// <summary>
    /// Restore list-general.txt from backup.
    /// </summary>
    public static (bool Success, string? Error) RestoreListBackup()
    {
        try
        {
            var listPath = Path.Combine(ZapretDir, ListGeneralFile);
            return RestoreFromBackup(listPath);
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка восстановления: {ex.Message}");
        }
    }

    private static void BackupOriginal(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        var fileName = Path.GetFileName(filePath);
        var backupPath = Path.Combine(ModScanner.BackupRoot, fileName);

        // Only backup if not already backed up
        if (!File.Exists(backupPath))
            File.Copy(filePath, backupPath, overwrite: false);
    }

    private static (bool Success, string? Error) RestoreFromBackup(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var backupPath = Path.Combine(ModScanner.BackupRoot, fileName);

        if (!File.Exists(backupPath))
            return (false, "Резервная копия не найдена");

        File.Copy(backupPath, filePath, overwrite: true);
        return (true, null);
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
