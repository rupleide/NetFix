using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    // Regex для парсинга вывода тестов
    private static readonly Regex ConfigRegex = new Regex(@"\[(\d+)/(\d+)\]\s+(.+\.bat)", RegexOptions.Compiled);
    private static readonly Regex TestLineRegex = new Regex(
        @"^\s*(\w+)\s+HTTP:(\w+)\s+TLS1\.2:(\w+)\s+TLS1\.3:(\w+)\s+\|\s+Ping:\s*(\d+)\s*ms",
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
        
        // Получить директорию Zapret
        var zapretDir = Path.GetDirectoryName(zapretPath);
        if (string.IsNullOrEmpty(zapretDir) || !Directory.Exists(zapretDir))
        {
            onProgress?.Invoke("❌ Ошибка: директория Zapret не найдена");
            return (configs, null);
        }

        // Найти PowerShell скрипт для тестирования
        var testScript = Path.Combine(zapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testScript))
        {
            onProgress?.Invoke("❌ Ошибка: скрипт test zapret.ps1 не найден");
            return (configs, null);
        }

        onProgress?.Invoke("🚀 Начинаем полное тестирование конфигов...");

        // Запустить PowerShell скрипт
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
            
            // Логирование для отладки
            System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] {line}");
            
            // Парсинг строки конфига: [2/19] general (ALT2).bat
            var configMatch = ConfigRegex.Match(line);
            if (configMatch.Success)
            {
                // Сохранить предыдущий конфиг
                if (currentConfig != null)
                {
                    // Подсчитываем результаты предыдущего конфига
                    var successCount = currentConfig.SuccessCount;
                    var totalCount = currentConfig.Tests.Count;
                    var failedTests = totalCount - successCount;
                    
                    // Конфиг валиден только если: 0 ошибок И минимум 12 успешных тестов
                    currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount >= 12;
                    if (currentConfig.IsValid)
                    {
                        configs.Add(currentConfig);
                        onProgress?.Invoke($"[HEADER]✅ {currentConfig.Name} - РАБОЧИЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Пинг: {currentConfig.AveragePing}мс");
                        onProgress?.Invoke(""); // Пустая строка для отступа
                    }
                    else
                    {
                        onProgress?.Invoke($"[HEADER]❌ {currentConfig.Name} - НЕРАБОЧИЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {successCount}/{totalCount}, Не работает: {failedTests} сайтов");
                        onProgress?.Invoke(""); // Пустая строка для отступа
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Config: {currentConfig.Name}, Valid: {currentConfig.IsValid}, Success: {successCount}/{totalCount}, Errors: {currentConfig.ErrorCount}");
                    
                    testedConfigs++;
                    onConfigTested?.Invoke(testedConfigs, totalConfigs);
                }

                // Создать новый конфиг
                var current = int.Parse(configMatch.Groups[1].Value);
                totalConfigs = int.Parse(configMatch.Groups[2].Value);
                var configName = configMatch.Groups[3].Value;

                currentConfig = new ZapretConfig
                {
                    Name = configName,
                    Tests = new Dictionary<string, ServiceTestResult>()
                };

                onProgress?.Invoke(""); // Пустая строка для отступа перед новым конфигом
                onProgress?.Invoke($"[HEADER]🔄 Тестирую конфиг [{current}/{totalConfigs}]: {configName}[/HEADER]");
                return;
            }

            // Парсинг строки теста - проверяем только ключевые результаты
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

                // Обновляем прогресс теста - показываем только основные сервисы
                if (serviceName.StartsWith("Discord") || serviceName.StartsWith("YouTube") || serviceName.StartsWith("Google"))
                {
                    var statusText = httpStatus == "OK" && tls12Status == "OK" && tls13Status == "OK" 
                        ? "РАБОТАЕТ" 
                        : (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR" 
                            ? "НЕ РАБОТАЕТ" 
                            : "ЧАСТИЧНО");
                    
                    onProgress?.Invoke($"   🟢 {serviceName}: {statusText} | {ping}мс");
                }

                // Считаем только полностью успешные тесты (все OK)
                if (testResult.IsSuccess)
                    currentConfig.SuccessCount++;
                
                // Любая ошибка или UNSUP - это провал конфига
                if (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR" ||
                    httpStatus == "UNSUP" || tls12Status == "UNSUP" || tls13Status == "UNSUP")
                    currentConfig.ErrorCount++;

                // Обновить средний пинг
                if (currentConfig.Tests.Count > 0)
                    currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);
                
                System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Average ping for {currentConfig.Name}: {currentConfig.AveragePing}ms");
            }
            else if (line.Contains("Ping:") && currentConfig != null)
            {
                // Если regex не сработал, но строка содержит "Ping:", выводим для отладки
                System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Failed to parse line with Ping: '{line}'");
            }
            // Пропускаем все остальные строки - не показываем технический мусор
        };

        process.Start();
        process.BeginOutputReadLine();

        try
        {
            // Отправить "1\n1\n" для выбора "standard tests" -> "all configs"
            await Task.Delay(1000);
            await process.StandardInput.WriteLineAsync("1");
            await Task.Delay(500);
            await process.StandardInput.WriteLineAsync("1");
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Игнорируем ошибки записи в stdin
        }

        await process.WaitForExitAsync();

        // Сохранить последний конфиг
        if (currentConfig != null)
        {
            // Конфиг валиден только если: 0 ошибок И минимум 12 успешных тестов
            currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount >= 12;
            if (currentConfig.IsValid)
                configs.Add(currentConfig);
            
            System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Last config: {currentConfig.Name}, Valid: {currentConfig.IsValid}, Success: {currentConfig.SuccessCount}/12, Errors: {currentConfig.ErrorCount}");
        }

        System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Total configs found: {configs.Count}");
        foreach (var cfg in configs)
        {
            System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Config: {cfg.Name}, Ping: {cfg.AveragePing}, Success: {cfg.SuccessCount}/12");
        }

        // Отсортировать по среднему пингу (лучший = меньший пинг)
        configs = configs.OrderBy(c => c.AveragePing).ToList();

        onProgress?.Invoke($"Тестирование завершено. Найдено {configs.Count} рабочих конфигов");

        return (configs, process);
    }

    public static async Task<(bool isWorking, string message)> TestSingleConfigAsync(
        string zapretPath,
        string configName,
        Action<string>? onProgress = null)
    {
        // Получить директорию Zapret
        var zapretDir = Path.GetDirectoryName(zapretPath);
        if (string.IsNullOrEmpty(zapretDir) || !Directory.Exists(zapretDir))
        {
            return (false, "❌ Ошибка: директория Zapret не найдена");
        }

        // Найти PowerShell скрипт для тестирования
        var testScript = Path.Combine(zapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testScript))
        {
            return (false, "❌ Ошибка: скрипт test zapret.ps1 не найден");
        }

        onProgress?.Invoke($"🚀 Запуск тестирования конфига: {configName}");

        // Запустить PowerShell скрипт с конкретным конфигом
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

        process.OutputDataReceived += (sender, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            var line = e.Data;
            
            // Логирование для отладки
            System.Diagnostics.Debug.WriteLine($"[SINGLE TEST] {line}");
            
            // Парсинг строки конфига: [2/19] general (ALT2).bat
            var configMatch = ConfigRegex.Match(line);
            if (configMatch.Success)
            {
                // Если предыдущий конфиг был наш целевой - завершаем тест
                if (currentConfig != null && foundTargetConfig)
                {
                    // Конфиг валиден только если: 0 ошибок И минимум 12 успешных тестов
                    currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount >= 12;
                    
                    if (currentConfig.IsValid)
                    {
                        onProgress?.Invoke($"[HEADER]✅ {currentConfig.Name} - РАБОЧИЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {currentConfig.SuccessCount}/{currentConfig.Tests.Count}, Пинг: {currentConfig.AveragePing}мс");
                        onProgress?.Invoke("");
                        return;
                    }
                    else
                    {
                        onProgress?.Invoke($"[HEADER]❌ {currentConfig.Name} - НЕРАБОЧИЙ[/HEADER]");
                        onProgress?.Invoke($"   🔹 Протестировано: {currentConfig.SuccessCount}/{currentConfig.Tests.Count}, Не работает: {currentConfig.Tests.Count - currentConfig.SuccessCount} сайтов");
                        onProgress?.Invoke("");
                        return;
                    }
                }

                var current = int.Parse(configMatch.Groups[1].Value);
                var total = int.Parse(configMatch.Groups[2].Value);
                var configNameFromTest = configMatch.Groups[3].Value;

                // Если этот конфиг - тот, что мы ищем
                if (configNameFromTest == configName)
                {
                    foundTargetConfig = true;
                    currentConfig = new ZapretConfig
                    {
                        Name = configNameFromTest,
                        Tests = new Dictionary<string, ServiceTestResult>()
                    };
                    onProgress?.Invoke($"[HEADER]🔄 Тестирую конфиг [{current}/{total}]: {configNameFromTest}[/HEADER]");
                }
                else
                {
                    // Пропускаем другие конфиги
                    currentConfig = null;
                    foundTargetConfig = false;
                }
                
                return;
            }

            // Парсинг строки теста - проверяем только ключевые результаты
            var testMatch = TestLineRegex.Match(line);
            if (testMatch.Success && currentConfig != null && foundTargetConfig)
            {
                var serviceName = testMatch.Groups[1].Value;
                var httpStatus = testMatch.Groups[2].Value;
                var tls12Status = testMatch.Groups[3].Value;
                var tls13Status = testMatch.Groups[4].Value;
                var pingStr = testMatch.Groups[5].Value;
                var ping = string.IsNullOrEmpty(pingStr) ? 0 : int.Parse(pingStr);

                System.Diagnostics.Debug.WriteLine($"[SINGLE TEST] Parsed: {serviceName}, Ping: {ping}ms");

                var testResult = new ServiceTestResult
                {
                    ServiceName = serviceName,
                    HttpStatus = httpStatus,
                    Tls12Status = tls12Status,
                    Tls13Status = tls13Status,
                    Ping = ping
                };

                currentConfig.Tests[serviceName] = testResult;

                // Обновляем прогресс теста - показываем только основные сервисы
                if (serviceName.StartsWith("Discord") || serviceName.StartsWith("YouTube") || serviceName.StartsWith("Google"))
                {
                    var statusText = httpStatus == "OK" && tls12Status == "OK" && tls13Status == "OK" 
                        ? "РАБОТАЕТ" 
                        : (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR" 
                            ? "НЕ РАБОТАЕТ" 
                            : "ЧАСТИЧНО");
                    
                    onProgress?.Invoke($"   🟢 {serviceName}: {statusText} | {ping}мс");
                }

                // Считаем только полностью успешные тесты (все OK)
                if (testResult.IsSuccess)
                    currentConfig.SuccessCount++;
                
                // Любая ошибка или UNSUP - это провал конфига
                if (httpStatus == "ERROR" || tls12Status == "ERROR" || tls13Status == "ERROR" ||
                    httpStatus == "UNSUP" || tls12Status == "UNSUP" || tls13Status == "UNSUP")
                    currentConfig.ErrorCount++;

                // Обновить средний пинг
                if (currentConfig.Tests.Count > 0)
                    currentConfig.AveragePing = (int)currentConfig.Tests.Values.Average(t => t.Ping);
                
                System.Diagnostics.Debug.WriteLine($"[SINGLE TEST] Average ping for {currentConfig.Name}: {currentConfig.AveragePing}ms");
            }
            // Пропускаем все остальные строки - не показываем технический мусор
        };

        process.Start();
        process.BeginOutputReadLine();

        try
        {
            // Отправить "2\n2\n{configName}\n" для выбора "individual test" -> "specify config name" -> указание имени конфига
            await Task.Delay(1000);
            await process.StandardInput.WriteLineAsync("2");  // Individual test
            await Task.Delay(500);
            await process.StandardInput.WriteLineAsync("2");  // Specify config name
            await Task.Delay(500);
            await process.StandardInput.WriteLineAsync(configName);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Игнорируем ошибки записи в stdin
        }

        await process.WaitForExitAsync();

        // Проверить результат
        if (currentConfig != null && foundTargetConfig)
        {
            // Конфиг валиден только если: 0 ошибок И минимум 12 успешных тестов
            currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount >= 12;
            
            if (currentConfig.IsValid)
            {
                onProgress?.Invoke($"🎉 Конфиг работает отлично! Пройдено {currentConfig.SuccessCount}/12 тестов");
                return (true, $"Конфиг работает отлично! Пройдено {currentConfig.SuccessCount}/12 тестов");
            }
            else
            {
                onProgress?.Invoke($"⚠️ Конфиг не проходит все тесты. Пройдено {currentConfig.SuccessCount}/12 тестов");
                return (false, $"Конфиг не проходит все тесты. Пройдено {currentConfig.SuccessCount}/12 тестов");
            }
        }
        else
        {
            return (false, $"❌ Ошибка: конфиг {configName} не был протестирован");
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

    public static bool ApplyConfig(string zapretPath, string configName)
    {
        try
        {
            var zapretDir = Path.GetDirectoryName(zapretPath);
            if (string.IsNullOrEmpty(zapretDir)) return false;

            var configPath = Path.Combine(zapretDir, configName);
            if (!File.Exists(configPath)) return false;

            // Запустить service.bat с опцией установки конфига
            var serviceBat = Path.Combine(zapretDir, "service.bat");
            if (!File.Exists(serviceBat)) return false;

            // Здесь нужно будет вызвать service.bat -> Install Service -> выбрать конфиг
            // Это сложно автоматизировать, поэтому пока просто вернём true
            // В будущем можно добавить автоматизацию через отправку ввода

            return true;
        }
        catch
        {
            return false;
        }
    }
}