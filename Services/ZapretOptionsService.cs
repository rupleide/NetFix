using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace NetFix.Services;

public enum GameFilterMode
{
    Disabled,
    All,
    TcpOnly,
    UdpOnly
}

public enum IPSetMode
{
    None,
    Loaded,
    Any
}

public static class ZapretOptionsService
{
    private const string RemoteIPSetUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
    private const string RemoteHostsUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";


    public static GameFilterMode GetGameFilterMode(string zapretDir)
    {
        try
        {
            var flagFile = Path.Combine(zapretDir, "utils", "game_filter.enabled");
            if (!File.Exists(flagFile))
            {
                return GameFilterMode.Disabled;
            }

            var text = File.ReadAllText(flagFile).Trim().ToLowerInvariant();
            return text switch
            {
                "all" => GameFilterMode.All,
                "tcp" => GameFilterMode.TcpOnly,
                "udp" => GameFilterMode.UdpOnly,
                _ => GameFilterMode.Disabled
            };
        }
        catch
        {
            return GameFilterMode.Disabled;
        }
    }

    public static bool SetGameFilterMode(string zapretDir, GameFilterMode mode)
    {
        try
        {
            var utilsDir = Path.Combine(zapretDir, "utils");
            if (!Directory.Exists(utilsDir))
            {
                Directory.CreateDirectory(utilsDir);
            }

            var flagFile = Path.Combine(utilsDir, "game_filter.enabled");

            if (mode == GameFilterMode.Disabled)
            {
                if (File.Exists(flagFile))
                {
                    File.Delete(flagFile);
                }
                return true;
            }

            string content = mode switch
            {
                GameFilterMode.All => "all",
                GameFilterMode.TcpOnly => "tcp",
                GameFilterMode.UdpOnly => "udp",
                _ => ""
            };

            File.WriteAllText(flagFile, content, Encoding.ASCII);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static (string tcp, string udp) GetGameFilterPorts(string zapretDir)
    {
        var mode = GetGameFilterMode(zapretDir);
        return mode switch
        {
            GameFilterMode.All => ("1024-65535", "1024-65535"),
            GameFilterMode.TcpOnly => ("1024-65535", "12"),
            GameFilterMode.UdpOnly => ("12", "1024-65535"),
            _ => ("12", "12")
        };
    }


    public static IPSetMode GetIPSetMode(string zapretDir, out int lineCount)
    {
        lineCount = 0;
        try
        {
            var listFile = Path.Combine(zapretDir, "lists", "ipset-all.txt");
            if (!File.Exists(listFile))
            {
                return IPSetMode.None;
            }

            var lines = File.ReadAllLines(listFile);
            lineCount = lines.Length;

            if (lineCount == 0)
            {
                return IPSetMode.Any;
            }

            if (lines.Any(l => l.Contains("203.0.113.113/32")))
            {
                return IPSetMode.None;
            }

            return IPSetMode.Loaded;
        }
        catch
        {
            return IPSetMode.None;
        }
    }

    public static async Task<bool> SetIPSetModeAsync(string zapretDir, IPSetMode mode)
    {
        try
        {
            var listsDir = Path.Combine(zapretDir, "lists");
            if (!Directory.Exists(listsDir))
            {
                Directory.CreateDirectory(listsDir);
            }

            var listFile = Path.Combine(listsDir, "ipset-all.txt");
            var backupFile = Path.Combine(listsDir, "ipset-all.txt.backup");

            if (mode == IPSetMode.None)
            {
                if (File.Exists(listFile))
                {
                    var currentText = await File.ReadAllTextAsync(listFile);
                    if (!currentText.Contains("203.0.113.113/32") && !string.IsNullOrWhiteSpace(currentText))
                    {
                        await File.WriteAllTextAsync(backupFile, currentText, Encoding.UTF8);
                    }
                }
                await File.WriteAllTextAsync(listFile, "203.0.113.113/32\r\n", Encoding.ASCII);
                return true;
            }

            if (mode == IPSetMode.Any)
            {
                if (File.Exists(listFile))
                {
                    var currentText = await File.ReadAllTextAsync(listFile);
                    if (!currentText.Contains("203.0.113.113/32") && !string.IsNullOrWhiteSpace(currentText))
                    {
                        await File.WriteAllTextAsync(backupFile, currentText, Encoding.UTF8);
                    }
                }
                await File.WriteAllTextAsync(listFile, "", Encoding.ASCII);
                return true;
            }

            if (mode == IPSetMode.Loaded)
            {
                if (File.Exists(backupFile) && new FileInfo(backupFile).Length > 50)
                {
                    File.Copy(backupFile, listFile, overwrite: true);
                    return true;
                }

                return await DownloadLatestIPSetAsync(zapretDir);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }


    public static async Task<bool> DownloadLatestIPSetAsync(string zapretDir)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");

            var response = await client.GetAsync(RemoteIPSetUrl);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var listsDir = Path.Combine(zapretDir, "lists");
            if (!Directory.Exists(listsDir))
            {
                Directory.CreateDirectory(listsDir);
            }

            var listFile = Path.Combine(listsDir, "ipset-all.txt");
            var backupFile = Path.Combine(listsDir, "ipset-all.txt.backup");

            await File.WriteAllTextAsync(listFile, content, Encoding.UTF8);
            await File.WriteAllTextAsync(backupFile, content, Encoding.UTF8);

            return true;
        }
        catch
        {
            return false;
        }
    }


    public static string GetSystemHostsPath()
    {
        var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(sysRoot, "System32", "drivers", "etc", "hosts");
    }

    public static async Task<(bool needsUpdate, string remoteContent, string statusMessage)> CheckHostsStatusAsync()
    {
        try
        {
            var hostsPath = GetSystemHostsPath();
            if (!File.Exists(hostsPath))
            {
                return (true, "", "Файл hosts не найден");
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");

            var randomQuery = $"{RemoteHostsUrl}?t={Guid.NewGuid():N}";
            var response = await client.GetAsync(randomQuery);
            if (!response.IsSuccessStatusCode)
            {
                return (false, "", "Не удалось загрузить hosts с GitHub");
            }

            var remoteContent = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(remoteContent))
            {
                return (false, "", "Получен пустой hosts с GitHub");
            }

            var remoteLines = remoteContent.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
                .ToList();

            if (remoteLines.Count == 0)
            {
                return (false, remoteContent, "В репозитории нет активных записей");
            }

            var localText = await File.ReadAllTextAsync(hostsPath);

            var firstLine = remoteLines.First();
            var lastLine = remoteLines.Last();

            bool hasFirst = localText.Contains(firstLine, StringComparison.OrdinalIgnoreCase);
            bool hasLast = localText.Contains(lastLine, StringComparison.OrdinalIgnoreCase);

            if (!hasFirst || !hasLast)
            {
                return (true, remoteContent, "Требуется обновление записей в hosts");
            }

            return (false, remoteContent, "Hosts актуален");
        }
        catch (Exception ex)
        {
            return (false, "", $"Ошибка проверки hosts: {ex.Message}");
        }
    }

    public static async Task<(bool success, string error)> ApplyHostsUpdateAsync(string remoteContent)
    {
        try
        {
            var hostsPath = GetSystemHostsPath();
            if (!File.Exists(hostsPath))
            {
                return (false, "Файл hosts не найден в системе");
            }

            var backupPath = hostsPath + ".bak";
            File.Copy(hostsPath, backupPath, overwrite: true);

            var localText = await File.ReadAllTextAsync(hostsPath);

            const string HeaderMarker = "# --- NETFIX / ZAPRET HOSTS BLOCK START ---";
            const string FooterMarker = "# --- NETFIX / ZAPRET HOSTS BLOCK END ---";

            string updatedContent;

            if (localText.Contains(HeaderMarker) && localText.Contains(FooterMarker))
            {
                int startIdx = localText.IndexOf(HeaderMarker, StringComparison.Ordinal);
                int endIdx = localText.IndexOf(FooterMarker, StringComparison.Ordinal) + FooterMarker.Length;
                var before = localText.Substring(0, startIdx);
                var after = localText.Substring(endIdx);
                updatedContent = $"{before}{HeaderMarker}\r\n{remoteContent.Trim()}\r\n{FooterMarker}{after}";
            }
            else
            {
                var trimmed = localText.TrimEnd();
                updatedContent = $"{trimmed}\r\n\r\n{HeaderMarker}\r\n{remoteContent.Trim()}\r\n{FooterMarker}\r\n";
            }

            await File.WriteAllTextAsync(hostsPath, updatedContent, Encoding.UTF8);

            FlushDnsCache();

            return (true, "");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Нет прав администратора для записи в C:\\Windows\\System32\\drivers\\etc\\hosts");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void FlushDnsCache()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch
        {
        }
    }
}
