using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NetFix.Models;

namespace NetFix.Services;

public class ZapretConfigService
{
    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetFix", "zapret_configs.json");

    private static readonly Regex ConfigRegex = new Regex(@"\[(\d+)/(\d+)\]\s+(.+\.bat)", RegexOptions.Compiled);
    private static readonly Regex TestLineRegex = new Regex(
        @"^\s*(\w+)\s+HTTP:(\w+)\s+TLS1\.2:(\w+)\s+TLS1\.3:(\w+)\s+\|\s+Ping:\s*(\d+)\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PingOnlyRegex = new Regex(
        @"^\s*(\w+)\s+Ping:\s*(\d+)\s*ms",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ZapretConfigCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var json = File.ReadAllText(CacheFile);
            return JsonSerializer.Deserialize<ZapretConfigCache>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveCache(ZapretConfigCache cache)
    {
        try
        {
            var dir = Path.GetDirectoryName(CacheFile);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CacheFile, json);
        }
        catch { }
    }

    public static async Task<(List<ZapretConfig> configs, Process? process)> TestAllConfigsAsync(
        string zapretPath,
        Action<string>? onProgress = null,
        Action<int, int>? onConfigTested = null)
    {
        var configs = new List<ZapretConfig>();
        Process? process = null;

        var zapretDir = Path.GetDirectoryName(zapretPath);
        if (string.IsNullOrEmpty(zapretDir) || !Directory.Exists(zapretDir))
        {
            onProgress?.Invoke("❌ Ошибка: директория Zapret не найдена");
            return (configs, null);
        }

        var testScript = Path.Combine(zapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testScript))
        {
            onProgress?.Invoke("❌ Ошибка: скрипт test zapret.ps1 не найден");
            return (configs, null);
        }

        onProgress?.Invoke("🚀 Начинаем полное тестирование конфигов...");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{testScript}\"",
            WorkingDirectory = zapretDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        process = new Process { StartInfo = psi };

        ZapretConfig? currentConfig = null;
        int totalConfigs = 0;
        int testedConfigs = 0;

        process.OutputDataReceived += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var line = e.Data;

            System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] {line}");

            var configMatch = ConfigRegex.Match(line);
            if (configMatch.Success)
            {
                if (currentConfig != null)
                {
                    var successCount = currentConfig.SuccessCount;
                    var totalCount = currentConfig.Tests.Count;
                    var failedTests = totalCount - successCount;

                    currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount == totalCount && totalCount > 0;

                    if (currentConfig.IsValid)
                    {
                        configs.Add(currentConfig);
                        onProgress?.Invoke($"[HEADER]✅ {currentConfig.Name} - ИДЕАЛЬНЫЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Пинг: {currentConfig.AveragePing}мс");
                        onProgress?.Invoke("");
                    }
                    else if (currentConfig.IsPartiallyUsable)
                    {
                        configs.Add(currentConfig);
                        onProgress?.Invoke($"[HEADER]⚠️ {currentConfig.Name} - ЧАСТИЧНО РАБОЧИЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Пинг: {currentConfig.AveragePing}мс");
                        onProgress?.Invoke("");
                    }
                    else
                    {
                        onProgress?.Invoke($"[HEADER]❌ {currentConfig.Name} - НЕРАБОЧИЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Не работает: {failedTests} сайтов");
                        onProgress?.Invoke("");
                    }

                    System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Config: {currentConfig.Name}, Valid: {currentConfig.IsValid}, Success: {successCount}/{totalCount}, Errors: {currentConfig.ErrorCount}");

                    testedConfigs++;
                    onConfigTested?.Invoke(testedConfigs, totalConfigs);
                }

                var current = int.Parse(configMatch.Groups[1].Value);
                totalConfigs = int.Parse(configMatch.Groups[2].Value);
                var configName = configMatch.Groups[3].Value;

                currentConfig = new ZapretConfig
                {
                    Name = configName,
                    Tests = new Dictionary<string, ServiceTestResult>()
                };

                onProgress?.Invoke("");
                onProgress?.Invoke($"[HEADER]🔄 Тестирую конфиг [{current}/{totalConfigs}]: {configName}[/HEADER]");
                return;
            }

            var testMatch = TestLineRegex.Match(line);
            if (testMatch.Success && currentConfig != null)
            {
                var serviceName = testMatch.Groups[1].Value;
                var httpStatus = testMatch.Groups[2].Value;
                var tls12Status = testMatch.Groups[3].Value;
                var tls13Status = testMatch.Groups[4].Value;
                var pingStr = testMatch.Groups[5].Value;
                var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Parsed: {serviceName}, Ping: {ping}ms (raw: '{pingStr}')");

                var testResult = new ServiceTestResult
                {
                    ServiceName = serviceName,
                    HttpStatus = httpStatus,
                    Tls12Status = tls12Status,
                    Tls13Status = tls13Status,
                    Ping = ping
                };

                currentConfig.Tests[serviceName] = testResult;

                if (serviceName.StartsWith("Discord") || serviceName.StartsWith("YouTube") || serviceName.StartsWith("Google"))
                {
                    var statusText = httpStatus == "OK" && (tls12Status == "OK" || tls13Status == "OK")
                        ? "РАБОТАЕТ"
                        : (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR"
                            ? "НЕ РАБОТАЕТ"
                            : "ЧАСТИЧНО");

                    onProgress?.Invoke($"   🟢 {serviceName}: {statusText} | {ping}мс");
                }

                if (testResult.IsSuccess)
                    currentConfig.SuccessCount++;

                if (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR")
                    currentConfig.ErrorCount++;

                if (currentConfig.Tests.Count > 0)
                    currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);

                System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Average ping for {currentConfig.Name}: {currentConfig.AveragePing}ms");
                return;
            }

            var pingOnlyMatch = PingOnlyRegex.Match(line);
            if (pingOnlyMatch.Success && currentConfig != null)
            {
                var serviceName = pingOnlyMatch.Groups[1].Value;
                var pingStr = pingOnlyMatch.Groups[2].Value;
                var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Parsed Ping-only: {serviceName}, Ping: {ping}ms");

                var testResult = new ServiceTestResult
                {
                    ServiceName = serviceName,
                    HttpStatus = "OK",
                    Tls12Status = "N/A",
                    Tls13Status = "N/A",
                    Ping = ping
                };

                currentConfig.Tests[serviceName] = testResult;

                if (ping > 0)
                    currentConfig.SuccessCount++;

                if (currentConfig.Tests.Count > 0)
                    currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);

                System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Average ping for {currentConfig.Name}: {currentConfig.AveragePing}ms");
            }
        };

        process.Start();
        process.BeginOutputReadLine();

        try
        {
            await Task.Delay(1000);
            await process.StandardInput.WriteLineAsync("1");
            await Task.Delay(500);
            await process.StandardInput.WriteLineAsync("1");
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        await process.WaitForExitAsync();

        if (currentConfig != null)
        {
            var totalCountEnd = currentConfig.Tests.Count;
            currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount == totalCountEnd && totalCountEnd > 0;
            if (currentConfig.IsValid || currentConfig.IsPartiallyUsable)
                configs.Add(currentConfig);

            System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Last config: {currentConfig.Name}, Valid: {currentConfig.IsValid}, Success: {currentConfig.SuccessCount}/12, Errors: {currentConfig.ErrorCount}");
        }

        System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Total configs found: {configs.Count}");
        foreach (var cfg in configs)
        {
            System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Config: {cfg.Name}, Ping: {cfg.AveragePing}, Success: {cfg.SuccessCount}/12");
        }

        configs = configs
            .OrderByDescending(c => c.IsValid)
            .ThenBy(c => c.AveragePing)
            .ToList();

        var idealCount = configs.Count(c => c.IsValid);
        var partialCount = configs.Count(c => c.IsPartiallyUsable);
        onProgress?.Invoke($"Тестирование завершено. Найдено {idealCount} идеальных и {partialCount} частично рабочих конфигов");

        return (configs, process);
    }

    public static async Task<(bool isWorking, string message)> TestSingleConfigAsync(
        string zapretPath,
        string configName,
        Action<string>? onProgress = null)
    {
        var zapretDir = Path.GetDirectoryName(zapretPath);
        if (string.IsNullOrEmpty(zapretDir) || !Directory.Exists(zapretDir))
        {
            onProgress?.Invoke("❌ Ошибка: директория Zapret не найдена");
            return (false, "Ошибка: директория Zapret не найдена");
        }

        var testScript = Path.Combine(zapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testScript))
        {
            onProgress?.Invoke("❌ Ошибка: скрипт test zapret.ps1 не найден");
            return (false, "Ошибка: скрипт test zapret.ps1 не найден");
        }

        onProgress?.Invoke("🚀 Начинаем тестирование конфига...");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{testScript}\"",
            WorkingDirectory = zapretDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        var process = new Process { StartInfo = psi };

        ZapretConfig? currentConfig = null;
        bool foundTargetConfig = false;
        bool configTestComplete = false;

        process.OutputDataReceived += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var line = e.Data;

            System.Diagnostics.Debug.WriteLine($"[TEST OUTPUT] {line}");

            var configMatch = ConfigRegex.Match(line);
            if (configMatch.Success)
            {
                var configNameFromTest = configMatch.Groups[3].Value;

                System.Diagnostics.Debug.WriteLine($"[CONFIG MATCH] Found: {configNameFromTest}, Looking for: {configName}");

                if (configNameFromTest == configName)
                {
                    foundTargetConfig = true;
                    currentConfig = new ZapretConfig
                    {
                        Name = configNameFromTest,
                        Tests = new Dictionary<string, ServiceTestResult>()
                    };

                    onProgress?.Invoke("");
                    onProgress?.Invoke($"[HEADER]🔄 Тестирую конфиг: {configName}[/HEADER]");
                }
                else if (foundTargetConfig)
                {
                    configTestComplete = true;
                    System.Diagnostics.Debug.WriteLine($"[CONFIG COMPLETE] Test complete for {configName}");
                }

                return;
            }

            if (foundTargetConfig && !configTestComplete && currentConfig != null)
            {
                var testMatch = TestLineRegex.Match(line);
                if (testMatch.Success)
                {
                    var serviceName = testMatch.Groups[1].Value;
                    var httpStatus = testMatch.Groups[2].Value;
                    var tls12Status = testMatch.Groups[3].Value;
                    var tls13Status = testMatch.Groups[4].Value;
                    var pingStr = testMatch.Groups[5].Value;
                    var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                    var testResult = new ServiceTestResult
                    {
                        ServiceName = serviceName,
                        HttpStatus = httpStatus,
                        Tls12Status = tls12Status,
                        Tls13Status = tls13Status,
                        Ping = ping
                    };

                    currentConfig.Tests[serviceName] = testResult;

                    var statusText = httpStatus == "OK" && (tls12Status == "OK" || tls13Status == "OK")
                        ? "РАБОТАЕТ"
                        : (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR"
                            ? "НЕ РАБОТАЕТ"
                            : "ЧАСТИЧНО");

                    onProgress?.Invoke($"   🟢 {serviceName}: {statusText} | {ping}мс");

                    if (testResult.IsSuccess)
                        currentConfig.SuccessCount++;

                    if (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR")
                        currentConfig.ErrorCount++;

                    if (currentConfig.Tests.Count > 0)
                        currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);
                    return;
                }

                var pingOnlyMatch = PingOnlyRegex.Match(line);
                if (pingOnlyMatch.Success)
                {
                    var serviceName = pingOnlyMatch.Groups[1].Value;
                    var pingStr = pingOnlyMatch.Groups[2].Value;
                    var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                    var testResult = new ServiceTestResult
                    {
                        ServiceName = serviceName,
                        HttpStatus = "OK",
                        Tls12Status = "N/A",
                        Tls13Status = "N/A",
                        Ping = ping
                    };

                    currentConfig.Tests[serviceName] = testResult;

                    if (ping > 0)
                        currentConfig.SuccessCount++;

                    if (currentConfig.Tests.Count > 0)
                        currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();

        try
        {
            await Task.Delay(2000);
            await process.StandardInput.WriteLineAsync("1");
            await Task.Delay(1000);
            await process.StandardInput.WriteLineAsync("2");
            await Task.Delay(1000);

            var configFiles = Directory.GetFiles(zapretDir, "*.bat")
                .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(f => f, new NaturalStringComparer())
                .ToList();

            int configIndex = configFiles.IndexOf(configName) + 1;

            System.Diagnostics.Debug.WriteLine($"[CONFIG SEARCH] Looking for: {configName}");
            System.Diagnostics.Debug.WriteLine($"[CONFIG SEARCH] Found at index: {configIndex - 1}, sending: {configIndex}");
            System.Diagnostics.Debug.WriteLine($"[CONFIG LIST] Total configs: {configFiles.Count}");
            for (int i = 0; i < configFiles.Count; i++)
            {
                System.Diagnostics.Debug.WriteLine($"[CONFIG LIST] [{i + 1}] {configFiles[i]}");
            }

            if (configIndex > 0)
            {
                await process.StandardInput.WriteLineAsync(configIndex.ToString());
                await Task.Delay(1000);
            }
            else
            {
                onProgress?.Invoke($"❌ Ошибка: не удалось найти конфиг {configName} в списке");
                process.StandardInput.Close();
                return (false, $"Не удалось найти конфиг {configName} в списке");
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        await process.WaitForExitAsync();

        if (currentConfig != null && foundTargetConfig)
        {
            currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount == currentConfig.Tests.Count && currentConfig.Tests.Count > 0;

            var successCount = currentConfig.SuccessCount;
            var totalCount = currentConfig.Tests.Count;
            var failedTests = totalCount - successCount;

            if (currentConfig.IsValid)
            {
                onProgress?.Invoke($"[HEADER]✅ {currentConfig.Name} - РАБОЧИЙ[/HEADER]");
                onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Пинг: {currentConfig.AveragePing}мс");
                return (true, $"Конфиг работает! Пройдено {successCount}/{totalCount} тестов");
            }
            else
            {
                onProgress?.Invoke($"[HEADER]❌ {currentConfig.Name} - НЕРАБОЧИЙ[/HEADER]");
                onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Не работает: {failedTests} сайтов");
                return (false, $"Конфиг не проходит все тесты. Пройдено {successCount}/{totalCount}");
            }
        }
        else
        {
            onProgress?.Invoke($"❌ Ошибка: конфиг {configName} не был протестирован");
            onProgress?.Invoke($"⚠️ ВОЗМОЖНО У ВАС ЗАПУЩЕН VPN! Закройте VPN и попробуйте ещё раз!");
            return (false, $"Конфиг {configName} не был протестирован. Возможно, у вас запущен VPN - закройте его и попробуйте снова.");
        }
    }

    private static async Task<bool> TestDiscordConnection()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync("https://discord.com");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string GetStatusEmojis(string http, string tls12, string tls13)
    {
        var httpEmoji = http switch
        {
            "OK" => "✅",
            "ERROR" => "❌",
            "UNSUP" => "⚠️",
            _ => "❓"
        };

        var tls12Emoji = tls12 switch
        {
            "OK" => "✅",
            "ERROR" => "❌",
            "UNSUP" => "⚠️",
            _ => "❓"
        };

        var tls13Emoji = tls13 switch
        {
            "OK" => "✅",
            "ERROR" => "❌",
            "UNSUP" => "⚠️",
            _ => "❓"
        };

        return $"{httpEmoji} {http} | {tls12Emoji} {tls12} | {tls13Emoji} {tls13}";
    }

    public static async Task<bool> ApplyConfigAsync(string zapretPath, string configName)
    {
        try
        {
            var zapretDir = Path.GetDirectoryName(zapretPath);
            if (string.IsNullOrEmpty(zapretDir))
            {
                return false;
            }

            var configPath = Path.Combine(zapretDir, configName);
            if (!File.Exists(configPath))
            {
                return false;
            }

            var binPath = Path.Combine(zapretDir, "bin");
            var winwsExe = Path.Combine(binPath, "winws.exe");
            if (!File.Exists(winwsExe))
            {
                return false;
            }

            var args = await ParseConfigArgsAsync(configPath, zapretDir, binPath);
            if (string.IsNullOrEmpty(args))
            {
                return false;
            }

            await StopAndRemoveServiceAsync("zapret");

            EnableTcpTimestamps();

            var success = await CreateServiceAsync("zapret", winwsExe, args, configName);

            if (success)
            {
                await StartServiceAsync("zapret");
            }

            return success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ParseConfigArgsAsync(string configPath, string zapretDir, string binPath)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(configPath);

            var listsPath = Path.Combine(zapretDir, "lists");
            var fullText = "";
            bool capture = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.Contains("winws.exe"))
                {
                    capture = true;
                    var idx = trimmed.IndexOf("winws.exe");
                    if (idx >= 0)
                    {
                        trimmed = trimmed.Substring(idx + "winws.exe".Length).Trim();
                    }
                }

                if (!capture) continue;

                if (trimmed.EndsWith("^"))
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - 1).Trim();
                }

                fullText += " " + trimmed;
            }

            fullText = fullText.Replace("%BIN%", binPath + "\\");
            fullText = fullText.Replace("%LISTS%", listsPath + "\\");
            fullText = fullText.Replace("%GameFilter%", "12");
            fullText = fullText.Replace("%GameFilterTCP%", "12");
            fullText = fullText.Replace("%GameFilterUDP%", "12");

            fullText = fullText.Replace("\"", "");

            fullText = System.Text.RegularExpressions.Regex.Replace(fullText, @"\s+", " ").Trim();

            return fullText;
        }
        catch
        {
            return "";
        }
    }

    private static List<string> SplitArgs(string line)
    {
        var result = new List<string>();
        var current = "";
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                current += c;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (!string.IsNullOrWhiteSpace(current))
                {
                    result.Add(current);
                    current = "";
                }
            }
            else
            {
                current += c;
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            result.Add(current);
        }

        return result;
    }

    private static async Task StopAndRemoveServiceAsync(string serviceName)
    {
        try
        {
            var stopPsi = new ProcessStartInfo
            {
                FileName = "net",
                Arguments = $"stop {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var stopProc = Process.Start(stopPsi))
            {
                if (stopProc != null)
                {
                    await stopProc.WaitForExitAsync();
                }
            }

            await Task.Delay(500);

            var deletePsi = new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"delete {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var deleteProc = Process.Start(deletePsi))
            {
                if (deleteProc != null)
                {
                    await deleteProc.WaitForExitAsync();
                }
            }

            await Task.Delay(500);

            foreach (var proc in Process.GetProcessesByName("winws"))
            {
                try { proc.Kill(); proc.Dispose(); } catch { }
            }
        }
        catch { }
    }

    private static void EnableTcpTimestamps()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "interface tcp set global timestamps=enabled",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
    }

    private static async Task<bool> CreateServiceAsync(string serviceName, string exePath, string args, string configName)
    {
        try
        {
            var binPathValue = $"\"{exePath}\" {args}";

            var createPsi = new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"create {serviceName} binPath= \"{binPathValue}\" DisplayName= \"zapret\" start= auto",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var createProc = Process.Start(createPsi))
            {
                if (createProc != null)
                {
                    var output = await createProc.StandardOutput.ReadToEndAsync();
                    var error = await createProc.StandardError.ReadToEndAsync();
                    await createProc.WaitForExitAsync();

                    if (createProc.ExitCode != 0) return false;
                }
            }

            var descPsi = new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"description {serviceName} \"Zapret DPI bypass software\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var descProc = Process.Start(descPsi))
            {
                if (descProc != null)
                {
                    await descProc.WaitForExitAsync();
                }
            }

            var regPsi = new ProcessStartInfo
            {
                FileName = "reg",
                Arguments = $"add \"HKLM\\System\\CurrentControlSet\\Services\\{serviceName}\" /v zapret-discord-youtube /t REG_SZ /d \"{configName.Replace(".bat", "")}\" /f",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var regProc = Process.Start(regPsi))
            {
                if (regProc != null)
                {
                    await regProc.WaitForExitAsync();
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StartServiceAsync(string serviceName)
    {
        try
        {
            var startPsi = new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"start {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var startProc = Process.Start(startPsi);
            if (startProc != null)
            {
                var output = await startProc.StandardOutput.ReadToEndAsync();
                var error = await startProc.StandardError.ReadToEndAsync();
                await startProc.WaitForExitAsync();
            }
        }
        catch { }
    }

    public static string GetModBatName(string modFolderName)
        => $"mod_{modFolderName}.bat";

    public static void InstallModBat(string zapretServicePath, string modFolderPath, string modFolderName)
    {
        var zapretDir = Path.GetDirectoryName(zapretServicePath) ?? @"C:\Zapret";
        var src = Path.Combine(modFolderPath, "strategy.bat");
        var dst = Path.Combine(zapretDir, GetModBatName(modFolderName));
        if (File.Exists(src))
            File.Copy(src, dst, overwrite: true);
    }

    public static void UninstallModBat(string zapretServicePath, string modFolderName)
    {
        var zapretDir = Path.GetDirectoryName(zapretServicePath) ?? @"C:\Zapret";
        var dst = Path.Combine(zapretDir, GetModBatName(modFolderName));
        if (File.Exists(dst))
            File.Delete(dst);
    }

    public static void InjectModConfig(string zapretServicePath, string modName, string modFolderName)
    {
        var cache = LoadCache() ?? new ZapretConfigCache
        {
            ValidConfigs = [],
            PartialConfigs = [],
        };

        cache.ValidConfigs ??= [];
        cache.PartialConfigs ??= [];

        var batName = GetModBatName(modFolderName);
        cache.ValidConfigs.RemoveAll(c => c.Name == batName);
        cache.PartialConfigs.RemoveAll(c => c.Name == batName);

        cache.ValidConfigs.Insert(0, new ZapretConfig
        {
            Name = batName,
            IsValid = true,
            IsFromMod = true,
            ModName = modName
        });

        SaveCache(cache);
    }

    public static void RemoveAllModConfigs()
    {
        var cache = LoadCache();
        if (cache is null) return;

        cache.ValidConfigs?.RemoveAll(c => c.IsFromMod);
        cache.PartialConfigs?.RemoveAll(c => c.IsFromMod);

        SaveCache(cache);
    }

    public static void RemoveModConfig(string zapretServicePath, string modFolderName)
    {
        var cache = LoadCache();
        if (cache is null) return;

        var batName = GetModBatName(modFolderName);
        cache.ValidConfigs?.RemoveAll(c => c.Name == batName);
        cache.PartialConfigs?.RemoveAll(c => c.Name == batName);

        if (cache.CurrentConfig == batName)
            cache.CurrentConfig = null;

        SaveCache(cache);
    }
}

public class NaturalStringComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var numX = GetNumber(x, ref ix);
                var numY = GetNumber(y, ref iy);

                var result = numX.CompareTo(numY);
                if (result != 0) return result;
            }
            else
            {
                var result = string.Compare(x, ix, y, iy, 1, StringComparison.OrdinalIgnoreCase);
                if (result != 0) return result;
                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    private static int GetNumber(string s, ref int index)
    {
        int start = index;
        while (index < s.Length && char.IsDigit(s[index]))
            index++;

        return int.Parse(s.Substring(start, index - start));
    }
}
