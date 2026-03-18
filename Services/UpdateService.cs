using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using NetFix.Models;

namespace NetFix.Services;

public class UpdateService
{
    private const string GitHubRepo = "rupleide/NetFix"; // замени на твой репозиторий
    private const string ApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";

    // Проверяет есть ли новая версия. Возвращает (есть ли обновление, новая версия, url установщика, ошибка)
    public static async Task<(bool hasUpdate, string newVersion, string downloadUrl, string error)> CheckAsync()
    {
        try
        {
            using var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");

            var json = await http.GetStringAsync(ApiUrl);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            string latestTag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string latestVersion = latestTag.TrimStart('v');

            // Ищем setup.exe в assets
            string downloadUrl = "";
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    break;
                }
            }

            // Текущая версия из Assembly
            string currentVersion = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetName()
                .Version?.ToString(3) ?? "0.0.0";

            bool hasUpdate = new Version(latestVersion) > new Version(currentVersion);
            return (hasUpdate, latestVersion, downloadUrl, "");
        }
        catch (Exception ex)
        {
            return (false, "", "", ex.Message);
        }
    }

    // Скачивает и запускает установщик
    public static async Task DownloadAndInstallAsync(string downloadUrl, Action<int>? onProgress = null)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "NetFix_Setup.exe");

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");

        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = File.Create(tempPath);

        var buffer = new byte[8192];
        long downloaded = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;
            if (totalBytes > 0)
                onProgress?.Invoke((int)(downloaded * 100 / totalBytes));
        }

        fileStream.Close();

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = tempPath,
            UseShellExecute = true
        });

        System.Windows.Application.Current.Shutdown();
    }
}
