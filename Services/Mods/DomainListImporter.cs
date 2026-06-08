using System.Net.Http;
using NetFix.Models;

namespace NetFix.Services.Mods;

public static class DomainListImporter
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>
    /// Downloads a domain list from URL and creates a list mod.
    /// Returns (domainListContent, error).
    /// </summary>
    public static async Task<(string? Content, string? Error)> DownloadFromUrlAsync(string url)
    {
        try
        {
            var response = await _http.GetStringAsync(url);

            if (string.IsNullOrWhiteSpace(response))
                return (null, "Список доменов пуст");

            // Parse lines, filter comments and empty
            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var domains = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !trimmed.StartsWith("//"))
                    domains.Add(trimmed);
            }

            if (domains.Count == 0)
                return (null, "Не найдено доменов в списке");

            return (string.Join("\n", domains), null);
        }
        catch (TaskCanceledException)
        {
            return (null, "Таймаут загрузки. Проверьте URL и подключение к интернету.");
        }
        catch (Exception ex)
        {
            return (null, $"Ошибка загрузки: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses pasted text content into domain list.
    /// </summary>
    public static string ParseText(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var domains = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !trimmed.StartsWith("//"))
                domains.Add(trimmed);
        }

        return string.Join("\n", domains);
    }

    /// <summary>
    /// Extracts a name suggestion from URL.
    /// </summary>
    public static string NameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var host = uri.Host;
            // Take first two parts of hostname
            var parts = host.Split('.');
            if (parts.Length >= 2)
                return parts[^2];
            return host;
        }
        catch
        {
            return "imported-list";
        }
    }
}
