using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NetFix.Models;

namespace NetFix.Services;

/// <summary>
/// Сервис для проверки версий установленных компонентов и сравнения с актуальными версиями на GitHub
/// </summary>
public static class ComponentVersionService
{
    private const string ZapretRepo = "Flowseal/zapret-discord-youtube";
    private const string TgWsProxyRepo = "Flowseal/tg-ws-proxy";

    /// <summary>
    /// Проверяет, требуется ли обновление компонентов
    /// </summary>
    /// <returns>True если требуется обновление, False если всё актуально</returns>
    public static async Task<(bool needsUpdate, string reason)> CheckIfUpdateNeededAsync(AppSettings settings)
    {
        try
        {
            // Проверяем, установлены ли компоненты
            bool zapretInstalled = !string.IsNullOrEmpty(settings.ZapretPath) && File.Exists(settings.ZapretPath);
            bool tgWsProxyInstalled = !string.IsNullOrEmpty(settings.TgWsProxyPath) && File.Exists(settings.TgWsProxyPath);

            // Если ничего не установлено - требуется установка
            if (!zapretInstalled && !tgWsProxyInstalled)
            {
                return (true, "Компоненты не установлены");
            }

            // Проверяем версии установленных компонентов
            bool zapretNeedsUpdate = false;
            bool tgWsProxyNeedsUpdate = false;

            if (zapretInstalled)
            {
                var zapretVersion = GetInstalledZapretVersion(settings.ZapretPath);
                var latestZapretVersion = await GetLatestGitHubVersionAsync(ZapretRepo);
                
                if (!string.IsNullOrEmpty(latestZapretVersion) && 
                    !string.IsNullOrEmpty(zapretVersion) &&
                    IsNewerVersion(latestZapretVersion, zapretVersion))
                {
                    zapretNeedsUpdate = true;
                }
            }

            if (tgWsProxyInstalled)
            {
                var tgWsProxyVersion = GetInstalledTgWsProxyVersion(settings.TgWsProxyPath);
                var latestTgWsProxyVersion = await GetLatestGitHubVersionAsync(TgWsProxyRepo);
                
                if (!string.IsNullOrEmpty(latestTgWsProxyVersion) && 
                    !string.IsNullOrEmpty(tgWsProxyVersion) &&
                    IsNewerVersion(latestTgWsProxyVersion, tgWsProxyVersion))
                {
                    tgWsProxyNeedsUpdate = true;
                }
            }

            if (zapretNeedsUpdate || tgWsProxyNeedsUpdate)
            {
                string components = "";
                if (zapretNeedsUpdate && tgWsProxyNeedsUpdate)
                    components = "Zapret и TgWsProxy";
                else if (zapretNeedsUpdate)
                    components = "Zapret";
                else
                    components = "TgWsProxy";
                
                return (true, $"Доступно обновление для {components}");
            }

            return (false, "Все компоненты актуальны");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка проверки версий: {ex.Message}");
            // В случае ошибки проверки версий не блокируем работу
            return (false, "Не удалось проверить версии");
        }
    }

    /// <summary>
    /// Получает версию установленного Zapret из файла service.bat
    /// </summary>
    private static string? GetInstalledZapretVersion(string serviceBatPath)
    {
        try
        {
            // Ищем файл version.txt или README в папке с Zapret
            var zapretDir = Path.GetDirectoryName(serviceBatPath);
            if (string.IsNullOrEmpty(zapretDir))
                return null;

            // Пытаемся найти файл с версией
            var versionFile = Path.Combine(zapretDir, "version.txt");
            if (File.Exists(versionFile))
            {
                return File.ReadAllText(versionFile).Trim();
            }

            // Если файла версии нет, пытаемся извлечь из README
            var readmeFiles = Directory.GetFiles(zapretDir, "README*", SearchOption.TopDirectoryOnly);
            if (readmeFiles.Length > 0)
            {
                var content = File.ReadAllText(readmeFiles[0]);
                var versionMatch = System.Text.RegularExpressions.Regex.Match(content, @"version[:\s]+([0-9.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (versionMatch.Success)
                {
                    return versionMatch.Groups[1].Value;
                }
            }

            // Если не удалось определить версию, возвращаем дату модификации файла
            var fileInfo = new FileInfo(serviceBatPath);
            return fileInfo.LastWriteTime.ToString("yyyy.MM.dd");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Получает версию установленного TgWsProxy из метаданных файла
    /// </summary>
    private static string? GetInstalledTgWsProxyVersion(string exePath)
    {
        try
        {
            var fileInfo = new FileInfo(exePath);
            
            // Пытаемся получить версию из метаданных файла
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrEmpty(versionInfo.FileVersion))
            {
                return versionInfo.FileVersion;
            }

            // Если метаданных нет, используем дату модификации
            return fileInfo.LastWriteTime.ToString("yyyy.MM.dd");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Получает последнюю версию компонента с GitHub
    /// </summary>
    private static async Task<string?> GetLatestGitHubVersionAsync(string repo)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");
            
            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = root.GetProperty("tag_name").GetString() ?? "";
            return version.TrimStart('v');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Сравнивает две версии и определяет, является ли первая новее второй
    /// Поддерживает версии типа: 1.9.8b, 1.9.9, v1.6.5, 2024.01.15
    /// </summary>
    private static bool IsNewerVersion(string version1, string version2)
    {
        try
        {
            // Убираем префикс 'v' если есть
            version1 = version1.TrimStart('v').Trim();
            version2 = version2.TrimStart('v').Trim();

            // Разделяем версию на числовую часть и суффикс (например, "1.9.8b" -> "1.9.8" и "b")
            var (numPart1, suffix1) = SplitVersionAndSuffix(version1);
            var (numPart2, suffix2) = SplitVersionAndSuffix(version2);

            // Сравниваем числовые части
            if (Version.TryParse(numPart1, out var v1) && Version.TryParse(numPart2, out var v2))
            {
                int comparison = v1.CompareTo(v2);
                
                // Если числовые части разные, возвращаем результат
                if (comparison != 0)
                    return comparison > 0;
                
                // Если числовые части одинаковые, сравниваем суффиксы
                // Версия без суффикса считается новее версии с суффиксом
                // Например: 1.9.8 > 1.9.8b
                if (string.IsNullOrEmpty(suffix1) && !string.IsNullOrEmpty(suffix2))
                    return true;
                if (!string.IsNullOrEmpty(suffix1) && string.IsNullOrEmpty(suffix2))
                    return false;
                
                // Если оба суффикса есть, сравниваем их лексикографически
                return string.Compare(suffix1, suffix2, StringComparison.OrdinalIgnoreCase) > 0;
            }

            // Если не получилось распарсить как Version, сравниваем как строки
            return string.Compare(version1, version2, StringComparison.OrdinalIgnoreCase) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Разделяет версию на числовую часть и буквенный суффикс
    /// Например: "1.9.8b" -> ("1.9.8", "b")
    /// </summary>
    private static (string numericPart, string suffix) SplitVersionAndSuffix(string version)
    {
        // Ищем первую букву в версии
        int firstLetterIndex = -1;
        for (int i = 0; i < version.Length; i++)
        {
            if (char.IsLetter(version[i]))
            {
                firstLetterIndex = i;
                break;
            }
        }

        if (firstLetterIndex == -1)
        {
            // Нет букв, вся строка - числовая часть
            return (version, "");
        }

        // Разделяем на числовую часть и суффикс
        string numericPart = version.Substring(0, firstLetterIndex);
        string suffix = version.Substring(firstLetterIndex);
        
        return (numericPart, suffix);
    }
}
