using System.Diagnostics;
using System.IO;
using System.Net.Http;
using NetFix.Models;

namespace NetFix.Services;

public static class AutoSetupService
{
    private const string ZapretRepo    = "Flowseal/zapret-discord-youtube";
    private const string TgWsProxyRepo = "Flowseal/tg-ws-proxy";

    public static void Run(
        Action<string, string> logCb,
        Action<double> progressCb,
        Action<bool, string> doneCb,
        AppSettings settings)
    {
        Task.Run(async () =>
        {
            try { await RunAsync(logCb, progressCb, doneCb, settings); }
            catch (Exception e) { logCb($"Неожиданная ошибка: {e.Message}", "error"); doneCb(false, "exception"); }
        });
    }

    private static async Task RunAsync(
        Action<string, string> log,
        Action<double> progress,
        Action<bool, string> done,
        AppSettings settings)
    {
        // Step 1 — internet
        log("Проверяю подключение к интернету…", "info");
        progress(0.05);
        bool hasNet = await DiagnosticsEngine.CheckInternetAsync();
        if (!hasNet)
        {
            log("Нет интернета. Подключитесь и попробуйте снова.", "error");
            done(false, "no_internet");
            return;
        }
        log("Интернет есть", "ok");
        progress(0.10);
        await Task.Delay(200);

        // Step 2 — GitHub versions
        log("Проверяю последние версии инструментов…", "info");
        string zapretVer   = await GetLatestGitHubRelease(ZapretRepo);
        string tgWsVer     = await GetLatestGitHubRelease(TgWsProxyRepo);
        progress(0.20);

        // Step 3 — Zapret
        if (string.IsNullOrEmpty(settings.ZapretPath) || !File.Exists(settings.ZapretPath))
        {
            log($"Нахожу Zapret {(string.IsNullOrEmpty(zapretVer) ? "последнюю версию" : zapretVer)}…", "info");
            log($"Откройте в браузере и скачайте архив для Windows:\nhttps://github.com/{ZapretRepo}/releases/latest", "link");
            log("⏳ После скачивания укажите путь в Настройках → Пути к файлам", "warn");
        }
        else
        {
            log($"Zapret найден: {settings.ZapretPath}", "ok");
            if (!IsProcessRunning(["zapret", "nfqws", "winws"]))
                TryLaunch(settings.ZapretPath, log);
            else
                log("Zapret уже запущен", "ok");
        }
        progress(0.50);
        await Task.Delay(200);

        // Step 4 — tg-ws-proxy
        if (string.IsNullOrEmpty(settings.TgWsProxyPath) || !File.Exists(settings.TgWsProxyPath))
        {
            log($"Нахожу tg-ws-proxy {(string.IsNullOrEmpty(tgWsVer) ? "последнюю версию" : tgWsVer)}…", "info");
            log($"Откройте в браузере и скачайте для Windows:\nhttps://github.com/{TgWsProxyRepo}/releases/latest", "link");
            log("⏳ После скачивания укажите путь в Настройках → Пути к файлам", "warn");
        }
        else
        {
            log($"tg-ws-proxy найден: {settings.TgWsProxyPath}", "ok");
            if (!IsProcessRunning(["tg-ws-proxy", "tgwsproxy"]))
                TryLaunch(settings.TgWsProxyPath, log);
            else
                log("tg-ws-proxy уже запущен", "ok");
        }
        progress(0.75);
        await Task.Delay(200);

        // Step 5 — manual instructions
        log("", "spacer");
        progress(1.0);
        await Task.Delay(200);

        log("", "spacer");
        log("Готово! Telegram должен работать нормально.", "success");
        done(true, "ok");
    }

    private static async Task<string> GetLatestGitHubRelease(string repo)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        }
        catch { return ""; }
    }

    private static bool IsProcessRunning(string[] keywords)
    {
        try
        {
            return Process.GetProcesses()
                .Any(p => { try { return keywords.Any(k => p.ProcessName.ToLower().Contains(k)); } catch { return false; } });
        }
        catch { return false; }
    }

    private static void TryLaunch(string path, Action<string, string> log)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
            log($"Запускаю {Path.GetFileName(path)}…", "info");
        }
        catch (Exception e)
        {
            log($"Не удалось запустить {Path.GetFileName(path)}: {e.Message}", "warn");
        }
    }
}
