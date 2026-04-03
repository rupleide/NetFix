using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;

namespace NetFix.Services;

public static class AutoDownloadService
{
    private const string ZapretRepo = "Flowseal/zapret-discord-youtube";
    private const string TgWsProxyRepo = "Flowseal/tg-ws-proxy";
    private static readonly string InstallDir = @"C:\Zapret";

    public static async Task<bool> AutoInstallAllAsync(
        Action<string> onLog,
        Action<double> onProgress,
        Action<string> onError)
    {
        try
        {
            onLog("=== Запуск автоматической установки ===");
            onLog("Подготовка папки установки...");
            
            // Используем временную папку для избежания конфликта с файлами
            string mainInstallDir = @"C:\Zapret";
            string tempInstallDir = Path.Combine(Path.GetTempPath(), $"NetFix_Zapret_Temp_{Guid.NewGuid()}");
            
            try
            {
                // Проверяем существование старой папки
                bool hasExistingZapret = false;
                bool hasExistingTgWsProxy = false;
                
                if (Directory.Exists(mainInstallDir))
                {
                    // Проверяем, есть ли в папке ключевые файлы
                    var existingServiceBat = FindFile(mainInstallDir, "service.bat");
                    var existingTgWsProxy = FindFile(mainInstallDir, "TgWsProxy.exe");
                    
                    hasExistingZapret = !string.IsNullOrEmpty(existingServiceBat);
                    hasExistingTgWsProxy = !string.IsNullOrEmpty(existingTgWsProxy);
                    
                    if (hasExistingZapret || hasExistingTgWsProxy)
                    {
                        onLog("⚠️ Найдена существующая установка:");
                        if (hasExistingZapret)
                            onLog($"   • Zapret (service.bat)");
                        if (hasExistingTgWsProxy)
                            onLog($"   • TgWsProxy (TgWsProxy.exe)");
                        
                        onLog("🔄 Обновляю файлы (с заменой существующих)...");
                    }
                    else
                    {
                        onLog("📌 Обнаружена папка C:\\Zapret, обновляю файлы...");
                    }
                }
                else
                {
                    onLog("📂 Создаю папку C:\\Zapret...");
                }
                
                // Создаём временную папку для установки
                onLog("Создаю временную папку для установки...");
                Directory.CreateDirectory(tempInstallDir);
                
                // Скачиваем и устанавливаем Zapret
                onLog("Получаю информацию о последней версии Zapret...");
                var zapretInfo = await GetLatestReleaseInfoAsync(ZapretRepo);
                if (zapretInfo == null)
                {
                    onError("❌ Не удалось получить информацию о Zapret с GitHub.\n\nВозможные причины:\n• Нет подключения к интернету\n• GitHub заблокирован\n• Неверный адрес репозитория");
                    return false;
                }
                onProgress(0.10);

                onLog($"✅ Найдена версия Zapret: {zapretInfo.Version}");
                onLog($"Загружаю архив по ссылке: {zapretInfo.DownloadUrl}");
                
                var zapretArchive = await DownloadFileAsync(
                    zapretInfo.DownloadUrl, 
                    Path.Combine(Path.GetTempPath(), "zapret_autoinstall.zip"),
                    p => onProgress(0.10 + p * 0.30));

                if (zapretArchive == null)
                {
                    onError("❌ Не удалось скачать Zapret.\n\nВозможные причины:\n• Нет подключения к интернету\n• GitHub заблокирован\n• Ссылка на скачивание недействительна");
                    return false;
                }

                onLog("✅ Zapret успешно скачан");
                onLog("Распаковываю Zapret в TEMP папку...");
                
                var zapretPath = await ExtractArchiveAsync(zapretArchive, tempInstallDir);
                onProgress(0.50);

                // Копируем содержимое из TEMP в C:\Zapret с заменой
                onLog("Копирую файлы в C:\\Zapret (с заменой существующих)...");
                MoveDirectoryContents(tempInstallDir, mainInstallDir);

                // Проверяем успешность установки
                onLog("Проверяю результат установки...");
                if (!IsInstallationSuccessful(mainInstallDir))
                {
                    onError($"❌ Установка завершена, но не найдены необходимые файлы в {mainInstallDir}\n\nВозможные причины:\n• Архив поврежден\n• Неправильная структура архива\n• Защита системы от перезаписи важных файлов");
                    return false;
                }

                // Ищем service.bat
                onLog("Ищу файл service.bat...");
                var serviceBat = FindFile(mainInstallDir, "service.bat");
                
                onLog($"✅ service.bat найден: {serviceBat}");
                onProgress(0.60);

                // Объявляем переменную для TgWsProxy здесь
                string? tgWsExe = null;

                // Скачиваем TgWsProxy
                onLog("Получаю информацию о последней версии TgWsProxy...");
                var tgWsInfo = await GetLatestReleaseInfoAsync(TgWsProxyRepo);
                if (tgWsInfo == null)
                {
                    // Проверяем, есть ли старый TgWsProxy
                    var existingTgWsProxy = FindFile(mainInstallDir, "TgWsProxy.exe");
                    if (!string.IsNullOrEmpty(existingTgWsProxy))
                    {
                        onLog($"⚠️ Не удалось получить информацию о TgWsProxy с GitHub, использую существующую версию");
                        onLog($"✅ TgWsProxy найден: {existingTgWsProxy}");
                        tgWsExe = existingTgWsProxy;
                        onProgress(0.95);
                    }
                    else
                    {
                        onError("❌ Не удалось получить информацию о TgWsProxy с GitHub.\n\nВозможные причины:\n• Нет подключения к интернету\n• GitHub заблокирован\n• Неверный адрес репозитория");
                        return false;
                    }
                }
                else
                {
                    onProgress(0.65);

                    onLog($"✅ Найдена версия TgWsProxy: {tgWsInfo.Version}");
                    onLog("Скачиваю TgWsProxy...");
                    
                    tgWsExe = await DownloadFileAsync(
                        tgWsInfo.DownloadUrl,
                        Path.Combine(mainInstallDir, "TgWsProxy.exe"),
                        p => onProgress(0.65 + p * 0.30));

                    if (tgWsExe == null)
                    {
                        // Проверяем, есть ли старый TgWsProxy
                        var existingTgWsProxy = FindFile(mainInstallDir, "TgWsProxy.exe");
                        if (!string.IsNullOrEmpty(existingTgWsProxy))
                        {
                            onLog($"⚠️ Не удалось скачать новую версию TgWsProxy, использую существующую");
                            onLog($"✅ TgWsProxy найден: {existingTgWsProxy}");
                            tgWsExe = existingTgWsProxy;
                            onProgress(0.95);
                        }
                        else
                        {
                            onError("❌ Не удалось скачать TgWsProxy.\n\nВозможные причины:\n• Нет подключения к интернету\n• GitHub заблокирован\n• Ссылка на скачивание недействительна");
                            return false;
                        }
                    }
                    else
                    {
                        onLog($"✅ TgWsProxy успешно скачан: {tgWsExe}");
                        onProgress(0.95);
                    }
                }

                // Сохраняем пути в настройках
                onLog("Сохраняю настройки в приложении...");
                var settings = SettingsService.Load();
                settings.ZapretPath = serviceBat;
                settings.TgWsProxyPath = tgWsExe;
                SettingsService.Save(settings);
                onProgress(1.0);

                onLog("🎉 Установка завершена успешно!");
                onLog("Можно закрыть окно и начать использовать приложение.");
                return true;
            }
            finally
            {
                // Очищаем временную папку если она создана
                if (Directory.Exists(tempInstallDir))
                {
                    try { Directory.Delete(tempInstallDir, true); }
                    catch { /* Игнорируем ошибки очистки временной папки */ }
                }
            }
        }
        catch (NotSupportedException ex)
        {
            onError($"❌ Ошибка формата архива: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            onError($"❌ Ошибка доступа: {ex.Message}\n\nПопробуйте запустить приложение от имени администратора.");
            return false;
        }
        catch (Exception ex)
        {
            onError($"❌ Неизвестная ошибка: {ex.Message}");
            return false;
        }
    }
    
    // Метод для перемещения содержимого из одной папки в другую
    private static void MoveDirectoryContents(string sourceDir, string targetDir)
    {
        // Создаем целевую папку если не существует
        Directory.CreateDirectory(targetDir);
        
        // Перемещаем все файлы из исходной в целевую папку
        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, filePath);
            var targetFilePath = Path.Combine(targetDir, relativePath);
            
            // Создаем подкаталог если нужно
            var targetDirPath = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(targetDirPath))
            {
                Directory.CreateDirectory(targetDirPath);
            }
            
            try
            {
                // Копируем файл (перезаписываем если существует)
                // Используем более безопасное копирование для защищенных файлов
                File.Copy(filePath, targetFilePath, true);
            }
            catch
            {
                // Если не удалось скопировать - игнорируем (это могут быть защищенные системные файлы)
                // Они могут быть не важны для работы Zapret
            }
        }
    }
    
    // Метод для проверки успешности установки
    private static bool IsInstallationSuccessful(string targetDir)
    {
        // Проверяем наличие ключевых файлов
        var serviceBat = FindFile(targetDir, "service.bat");
        var tgwsProxy = FindFile(targetDir, "TgWsProxy.exe");
        
        // Успешно, если найден хотя бы один из ключевых файлов
        return !string.IsNullOrEmpty(serviceBat) || !string.IsNullOrEmpty(tgwsProxy);
    }

    private static async Task<ReleaseInfo?> GetLatestReleaseInfoAsync(string repo)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");
            
            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = root.GetProperty("tag_name").GetString() ?? "unknown";
            
            // Ищем нужный файл в assets
            if (root.TryGetProperty("assets", out var assets))
            {
                // Сначала ищем ZIP архивы для Zapret (они более совместимы)
                if (repo.Contains("zapret"))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString()?.ToLower() ?? "";
                        var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        
                        if (name.Contains("zapret-discord-youtube") && name.EndsWith(".zip"))
                        {
                            return new ReleaseInfo { Version = version, DownloadUrl = downloadUrl };
                        }
                    }
                    
                    // Если ZIP нет, тогда ищем RAR
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString()?.ToLower() ?? "";
                        var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        
                        if (name.Contains("zapret-discord-youtube") && name.EndsWith(".rar"))
                        {
                            return new ReleaseInfo { Version = version, DownloadUrl = downloadUrl };
                        }
                    }
                }
                
                // Для TgWsProxy ищем специфичный exe-файл
                if (repo.Contains("tg-ws-proxy"))
                {
                    // Сначала ищем TgWsProxy_windows.exe
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString()?.ToLower() ?? "";
                        var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        
                        if (name.Contains("tgwsproxy") && name.Contains("windows") && name.EndsWith(".exe"))
                        {
                            return new ReleaseInfo { Version = version, DownloadUrl = downloadUrl };
                        }
                    }
                    
                    // Если не нашли специфичный файл, ищем любой TgWsProxy.exe
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString()?.ToLower() ?? "";
                        var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        
                        if (name.Contains("tgwsproxy") && name.EndsWith(".exe"))
                        {
                            return new ReleaseInfo { Version = version, DownloadUrl = downloadUrl };
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка получения информации о релизе: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> DownloadFileAsync(
        string url, 
        string destinationPath, 
        Action<double> onProgress)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[8192];
            long downloaded = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                downloaded += bytesRead;

                if (totalBytes > 0)
                {
                    onProgress((double)downloaded / totalBytes);
                }
            }

            return destinationPath;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ExtractArchiveAsync(string archivePath, string destinationDir)
    {
        try
        {
            await Task.Run(() =>
            {
                // Проверяем расширение
                if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
                }
                else
                {
                    // Для RAR архивов показываем ошибку с предложением скачать ZIP
                    throw new NotSupportedException(
                        "RAR архивы не поддерживаются автоматической установкой.\n" +
                        "Пожалуйста, скачайте ZIP версию Zapret с GitHub или установите вручную.");
                }
            });
        }
        catch (NotSupportedException)
        {
            // Перебрасываем исключение как есть
            throw;
        }
        catch (Exception ex) when (ex.Message.Contains("Central Directory") || ex.Message.Contains("ZIP") || ex.Message.Contains("archive"))
        {
            // Конкретная ошибка для RAR архивов
            throw new NotSupportedException(
                "RAR архивы не поддерживаются автоматической установкой.\n" +
                "Пожалуйста, скачайте ZIP версию Zapret с GitHub или установите вручную.");
        }
        catch (Exception ex)
        {
            // Любая другая ошибка
            throw new Exception($"Ошибка распаковки архива: {ex.Message}");
        }

        return destinationDir;
    }

    private static string? FindFile(string directory, string fileName)
    {
        try
        {
            var files = Directory.GetFiles(directory, fileName, SearchOption.AllDirectories);
            return files.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private class ReleaseInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
    }
}