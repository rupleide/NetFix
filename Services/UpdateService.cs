using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using NetFix.Models;

namespace NetFix.Services;

public class UpdateService
{
    private const string GitHubRepo = "rupleide/NetFix";
    private const string ApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";

    public static async Task<(bool hasUpdate, string newVersion, string downloadUrl, string error)> CheckAsync(bool useWebFallback = false)
    {
        if (useWebFallback)
        {
            return await CheckViaWebFallbackAsync();
        }

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

            string currentVersion = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetName()
                .Version?.ToString(3) ?? "0.0.0";

            bool hasUpdate = new Version(latestVersion) > new Version(currentVersion);
            return (hasUpdate, latestVersion, downloadUrl, "");
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("403") || ex.Message.Contains("429"))
            {
                var fallback = await CheckViaWebFallbackAsync();
                if (string.IsNullOrEmpty(fallback.error))
                    return fallback;
            }
            return (false, "", "", ex.Message);
        }
    }

    public static async Task<(bool hasUpdate, string newVersion, string downloadUrl, string error)> CheckViaWebFallbackAsync()
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var request = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{GitHubRepo}/releases/latest");
            var response = await http.SendAsync(request);

            Uri? location = response.Headers.Location;
            if (location == null)
            {
                var getRequest = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{GitHubRepo}/releases/latest");
                var getResponse = await http.SendAsync(getRequest);
                location = getResponse.Headers.Location;
            }

            if (location != null)
            {
                string path = location.OriginalString;
                int tagIdx = path.LastIndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
                if (tagIdx >= 0)
                {
                    string latestTag = path.Substring(tagIdx + 5).Trim().TrimEnd('/');
                    string latestVersion = latestTag.TrimStart('v');

                    string downloadUrl = "";
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        string assetsHtml = await client.GetStringAsync($"https://github.com/{GitHubRepo}/releases/expanded_assets/{latestTag}");

                        var match = System.Text.RegularExpressions.Regex.Match(
                            assetsHtml,
                            @"href=""(/[^""]+/releases/download/[^""]+\.exe)""",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (match.Success)
                        {
                            downloadUrl = "https://github.com" + match.Groups[1].Value;
                        }
                    }
                    catch
                    {
                        downloadUrl = $"https://github.com/{GitHubRepo}/releases/download/{latestTag}/NetFix_Setup_{latestTag}.exe";
                    }

                    if (string.IsNullOrEmpty(downloadUrl))
                    {
                        downloadUrl = $"https://github.com/{GitHubRepo}/releases/download/{latestTag}/NetFix_Setup.exe";
                    }

                    string currentVersion = System.Reflection.Assembly
                        .GetExecutingAssembly()
                        .GetName()
                        .Version?.ToString(3) ?? "0.0.0";

                    bool hasUpdate = new Version(latestVersion) > new Version(currentVersion);
                    return (hasUpdate, latestVersion, downloadUrl, "");
                }
            }

            return (false, "", "", "Не удалось определить версию через прямой веб-запрос GitHub");
        }
        catch (Exception ex)
        {
            return (false, "", "", ex.Message);
        }
    }

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
