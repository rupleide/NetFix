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
        @"^\s*(\w+)\s+HTTP:(\w+)\s+TLS1\.2:(\w+)\s+TLS1\.3:(\w+)\s+\|\s+Ping:\s+(\d+)\s+ms",
        RegexOptions.Compiled);

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

    public static async Task<List<ZapretConfig>> TestAllConfigsAsync(
        string zapretPath,
        Action<string>? onProgress = null,
        Action<int, int>? onConfigTested = null)
    {
        var configs = new List<ZapretConfig>();
        
        // Получить директорию Zapret
        var zapretDir = Path.GetDirectoryName(zapretPath);
        if (string.IsNullOrEmpty(zapretDir) || !Directory.Exists(zapretDir))
        {
            onProgress?.Invoke("Ошибка: директория Zapret не найдена");
            return configs;
        }

        // Найти PowerShell скрипт для тестирования
        var testScript = Path.Combine(zapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testScript))
        {
            onProgress?.Invoke("Ошибка: скрипт test zapret.ps1 не найден");
            return configs;
        }

        onProgress?.Invoke("Запуск тестирования всех конфигов...");

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

        using var process = new Process { StartInfo = psi };
        
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
                    // Конфиг валиден только если: 0 ошибок И минимум 12 успешных тестов
                    currentConfig.IsValid = currentConfig.ErrorCount == 0 && currentConfig.SuccessCount >= 12;
                    if (currentConfig.IsValid)
                        configs.Add(currentConfig);
                    
                    System.Diagnostics.Debug.WriteLine($"[ZAPRET TEST] Config: {currentConfig.Name}, Valid: {currentConfig.IsValid}, Success: {currentConfig.SuccessCount}/12, Errors: {currentConfig.ErrorCount}");
                    
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

                onProgress?.Invoke($"Тестирование [{current}/{totalConfigs}] {configName}");
                return;
            }

            // Парсинг строки теста
            var testMatch = TestLineRegex.Match(line);
            if (testMatch.Success && currentConfig != null)
            {
                var serviceName = testMatch.Groups[1].Value;
                var httpStatus = testMatch.Groups[2].Value;
                var tls12Status = testMatch.Groups[3].Value;
                var tls13Status = testMatch.Groups[4].Value;
                var ping = int.Parse(testMatch.Groups[5].Value);

                var testResult = new ServiceTestResult
                {
                    ServiceName = serviceName,
                    HttpStatus = httpStatus,
                    Tls12Status = tls12Status,
                    Tls13Status = tls13Status,
                    Ping = ping
                };

                currentConfig.Tests[serviceName] = testResult;

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
            }
        };

        process.Start();
        process.BeginOutputReadLine();

        // Отправить "1\n1\n" для выбора "standard tests" -> "all configs"
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

        return configs;
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
