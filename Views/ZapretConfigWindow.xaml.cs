using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetFix.Models;
using NetFix.Services;

using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace NetFix.Views;

public partial class ZapretConfigWindow : Window
{
    private readonly string _zapretPath;
    private readonly bool _testMode;
    private ZapretConfigCache? _cache;
    private bool _isTesting = false;
    private Process? _testProcess = null;
    private DateTime _testStartTime;
    private int _totalConfigs = 0;

    public bool ConfigWasApplied { get; private set; } = false;

    public ZapretConfigWindow(string zapretPath, bool testMode)
    {
        InitializeComponent();
        _zapretPath = zapretPath;
        _testMode = testMode;
        Loaded += OnLoaded;
        Closing += OnClosing;

        PrimaryBtn.MouseEnter += (s, e) =>
        {
            PrimaryBtn.Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2d));
            PrimaryBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4a, 0x4a, 0x4d));
            PrimaryBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x89));
        };
        PrimaryBtn.MouseLeave += (s, e) =>
        {
            PrimaryBtn.Background = Brushes.Transparent;
            PrimaryBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2d));
            PrimaryBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x69));
        };
    }

    private void AppendColoredLog(string text, Color color)
    {
        var paragraph = LogTextBox.Document.Blocks.LastBlock as Paragraph;
        if (paragraph == null)
        {
            paragraph = new Paragraph();
            LogTextBox.Document.Blocks.Add(paragraph);
        }

        bool isHeader = text.Contains("[HEADER]");
        if (isHeader)
        {
            text = text.Replace("[HEADER]", "").Replace("[/HEADER]", "");
        }

        var run = new Run(text + "\n")
        {
            Foreground = new SolidColorBrush(color),
            FontSize = isHeader ? 16 : 12,
            FontWeight = isHeader ? FontWeights.ExtraBold : FontWeights.Normal
        };
        paragraph.Inlines.Add(run);

        LogScrollViewer.ScrollToEnd();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isTesting = false;

        if (_testProcess != null && !_testProcess.HasExited)
        {
            try
            {
                ForceKillProcessTree(_testProcess.Id);
                _testProcess.Dispose();
            }
            catch { }
        }

        if (_testMode)
        {
            try
            {
                var processes = Process.GetProcessesByName("winws");
                foreach (var proc in processes)
                {
                    try
                    {
                        ForceKillProcessTree(proc.Id);
                        proc.Dispose();
                    }
                    catch { }
                }

                var powerShellProcs = Process.GetProcessesByName("powershell");
                foreach (var proc in powerShellProcs)
                {
                    try
                    {
                        proc.Kill(true);
                        proc.Dispose();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static void ForceKillProcessTree(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.Kill(true);
            process.WaitForExit(2000);
        }
        catch (ArgumentException)
        {
        }
        catch (Exception)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /PID {pid} /T",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(2000);
            }
            catch { }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cache = ZapretConfigService.LoadCache();

        if (_testMode)
        {
            StatusPanel.Visibility = Visibility.Visible;
            ProgressBarContainer.Visibility = Visibility.Collapsed;

            StatusIcon.Visibility = Visibility.Visible;
            StatusIcon.Data = (Geometry)FindResource("WarningIcon");
            StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));

            StatusText.Text = "Перед запуском - важная вещь!\n\n" +
                             "Приложение может само протестировать все конфиги и запомнить лучшие. " +
                             "Займёт минут 10, зато потом не придётся вручную перебирать их когда что-то перестаёт работать.\n\n" +
                             "А ломается, кстати, по-разному. Иногда конфиг вроде работает, Discord открылся, всё хорошо. " +
                             "Но стоит зайти на какой-нибудь сайт, и он либо вообще не загружается, " +
                             "либо открывается сломанным, без стилей, всё съехало, кнопки не работают. " +
                             "Это не браузер виноват, просто конфиг обрабатывает трафик не так, как нужно, и часть сайтов ломается.\n\n" +
                             "Именно поэтому важно иметь несколько проверенных конфигов под рукой, " +
                             "если один перестал работать правильно, переключились на другой и всё.\n\n" +
                             "Пройдите тест один раз, и приложение само разберётся что к чему. Запускаем?";

            SecondaryBtn.Content = "Да, начать";
            PrimaryBtn.Content = "Нет, выйти";
            PrimaryBtn.Visibility = Visibility.Visible;
        }
        else
        {
            if (_cache == null || !_cache.HasAnyConfigs)
            {
                ShowWarningNoCache();
            }
            else
            {
                StopIndeterminateAnimation();
                ShowConfigList();
            }
        }

    }

    private void ShowWarningNoCache()
    {
        StopIndeterminateAnimation();
        StatusPanel.Visibility = Visibility.Visible;
        ProgressBarContainer.Visibility = Visibility.Collapsed;

        StatusIcon.Visibility = Visibility.Visible;
        StatusIcon.Data = (Geometry)FindResource("WarningIcon");
        StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));

        StatusText.Text = "ОБЯЗАТЕЛЬНО ПРОЙДИТЕ полный тест конфигов!\n\n" +
                         "Это поможет вам в будущем и сэкономит кучу времени! " +
                         "Приложение найдёт все рабочие конфиги и выберет лучший для вашей сети.";

        SecondaryBtn.Content = "Закрыть";
        PrimaryBtn.Content = "Пройти тест";
        PrimaryBtn.Visibility = Visibility.Visible;
    }

    private async Task StartTestingAsync()
    {
        _isTesting = true;
        SecondaryBtn.Content = "Отмена";
        SecondaryBtn.Style = (Style)FindResource("OutlineBtn");
        PrimaryBtn.Visibility = Visibility.Collapsed;

        StatusText.Text = "Подготовка к тестированию...";

        var st = DiagnosticsEngine.CheckAppStatus();
        if (st.ZapretRunning)
        {
            StatusText.Text = "Остановка Zapret...";
            foreach (var p in Process.GetProcessesByName("winws"))
                try { p.Kill(); } catch { }
            foreach (var p in Process.GetProcessesByName("winws.exe"))
                try { p.Kill(); } catch { }

            await Task.Delay(1000);
        }

        try
        {
            StatusText.Text = "Удаление сервиса Zapret...";

            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query zapret",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var checkProcess = Process.Start(psi);
            if (checkProcess != null)
            {
                await checkProcess.WaitForExitAsync();

                if (checkProcess.ExitCode == 0)
                {
                    var stopPsi = new ProcessStartInfo
                    {
                        FileName = "net.exe",
                        Arguments = "stop zapret",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var stopProcess = Process.Start(stopPsi);
                    if (stopProcess != null)
                        await stopProcess.WaitForExitAsync();

                    await Task.Delay(500);

                    var deletePsi = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = "delete zapret",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var deleteProcess = Process.Start(deletePsi);
                    if (deleteProcess != null)
                        await deleteProcess.WaitForExitAsync();

                    await Task.Delay(500);
                }
            }
        }
        catch
        {
        }

        try
        {
            StatusText.Text = "Запуск полного тестирования конфигов...\n\n" +
                             "💡 Советуем вам подождать 10 минуток на полное сканирование.\n" +
                             "В дальнейшем это сэкономит вам кучу времени и нервов!\n\n" +
                             "Приложение найдёт все идеальные конфиги (12/12 тестов) и выберет лучший.";

            await Task.Delay(3000);

            StatusPanel.Visibility = Visibility.Collapsed;
            ProgressBarContainer.Visibility = Visibility.Visible;
            ProgressText.Visibility = Visibility.Visible;
            TimeRemainingText.Visibility = Visibility.Visible;
            ProgressText.Text = "Тестирование конфигов: 0%";
            TimeRemainingText.Text = "Осталось: ~10 мин";
            LogContainer.Visibility = Visibility.Visible;

            _testStartTime = DateTime.Now;

            LogTextBox.Document.Blocks.Clear();
            AppendColoredLog("💡 Советуем вам подождать 10 минуток на полное сканирование.", Color.FromRgb(0xf0, 0xf0, 0xf0));
            AppendColoredLog("В дальнейшем это сэкономит вам кучу времени и нервов!\n", Color.FromRgb(0xf0, 0xf0, 0xf0));
            AppendColoredLog("Запуск тестирования...\n", Color.FromRgb(0x88, 0x88, 0x88));

            var (configs, testProcess) = await ZapretConfigService.TestAllConfigsAsync(
                _zapretPath,
                status => Dispatcher.Invoke(() =>
                {
                    Color logColor;
                    if (status.Contains("❌") || status.Contains("НЕ РАБОТАЕТ") || status.Contains("НЕРАБОЧИЙ"))
                        logColor = Color.FromRgb(0xef, 0x44, 0x44);
                    else if (status.Contains("✅") || status.Contains("РАБОТАЕТ") || status.Contains("РАБОЧИЙ"))
                        logColor = Color.FromRgb(0x22, 0xc5, 0x5e);
                    else if (status.Contains("🔄") || status.Contains("Тестирую"))
                        logColor = Color.FromRgb(0x3b, 0x82, 0xf6);
                    else if (status.Contains("⚠️") || status.Contains("ЧАСТИЧНО"))
                        logColor = Color.FromRgb(0xea, 0xb3, 0x08);
                    else
                        logColor = Color.FromRgb(0xf0, 0xf0, 0xf0);

                    AppendColoredLog(status, logColor);
                }),
                (current, total) => Dispatcher.Invoke(() =>
                {
                    if (_totalConfigs == 0)
                        _totalConfigs = total;

                    var percentage = (current * 100 / total);
                    var progressWidth = (ProgressBarContainer.ActualWidth * current / total);
                    ProgressBar.Width = progressWidth;
                    ProgressText.Text = $"Тестирование конфигов: {current}/{total} ({percentage}%)";

                    if (current > 0)
                    {
                        var elapsed = DateTime.Now - _testStartTime;
                        var avgTimePerConfig = elapsed.TotalSeconds / current;
                        var remainingConfigs = total - current;
                        var estimatedSecondsRemaining = avgTimePerConfig * remainingConfigs;

                        if (estimatedSecondsRemaining < 60)
                            TimeRemainingText.Text = $"Осталось: ~{(int)estimatedSecondsRemaining} сек";
                        else
                            TimeRemainingText.Text = $"Осталось: ~{(int)(estimatedSecondsRemaining / 60)} мин";
                    }
                })
            );

            _testProcess = testProcess;

            if (!_isTesting) return;

            var idealConfigs = configs.Where(c => c.IsValid).OrderBy(c => c.AveragePing).ToList();
            var partialConfigs = configs.Where(c => c.IsPartiallyUsable).OrderByDescending(c => c.SuccessCount).ThenBy(c => c.AveragePing).ToList();

            if (idealConfigs.Count > 0)
            {
                _cache = new ZapretConfigCache
                {
                    LastTested = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    CurrentConfig = idealConfigs[0].Name,
                    ValidConfigs = idealConfigs,
                    PartialConfigs = partialConfigs
                };
                ZapretConfigService.SaveCache(_cache);

                ProgressBarContainer.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                TimeRemainingText.Visibility = Visibility.Collapsed;
                LogContainer.Visibility = Visibility.Collapsed;
                StopIndeterminateAnimation();
                StatusPanel.Visibility = Visibility.Visible;
                StatusIcon.Visibility = Visibility.Visible;
                StatusIcon.Data = (Geometry)FindResource("CheckmarkIcon");
                StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));

                var topConfigs = string.Join("\n", idealConfigs.Take(5).Select((c, i) =>
                    $"{i + 1}. {c.Name} (пинг: {c.AveragePing} мс, тестов: {c.SuccessCount}/12)"));

                StatusText.Text = $"🎉 Поздравляю с полным тестированием!\n\n" +
                                 $"Найдено {idealConfigs.Count} идеальных конфигов.\n" +
                                 $"Все они прошли 12/12 тестов без ошибок!\n\n" +
                                 $"Ваш топ конфигов на следующие разы:\n\n{topConfigs}";

                SecondaryBtn.Content = "Выбрать конфиг";
                SecondaryBtn.Style = (Style)FindResource("AccentBtn");
                PrimaryBtn.Visibility = Visibility.Visible;
                PrimaryBtn.Content = "Отмена";
            }
            else if (partialConfigs.Count > 0)
            {
                _cache = new ZapretConfigCache
                {
                    LastTested = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    CurrentConfig = partialConfigs[0].Name,
                    ValidConfigs = new List<ZapretConfig>(),
                    PartialConfigs = partialConfigs
                };
                ZapretConfigService.SaveCache(_cache);

                ProgressBarContainer.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                TimeRemainingText.Visibility = Visibility.Collapsed;
                LogContainer.Visibility = Visibility.Collapsed;
                StopIndeterminateAnimation();
                StatusPanel.Visibility = Visibility.Visible;
                StatusIcon.Visibility = Visibility.Visible;
                StatusIcon.Data = (Geometry)FindResource("WarningIcon");
                StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));

                var topConfigs = string.Join("\n", partialConfigs.Take(5).Select((c, i) =>
                    $"{i + 1}. {c.Name} (пинг: {c.AveragePing} мс, тестов: {c.SuccessCount}/12)"));

                StatusText.Text = "Идеальных конфигов не обнаружено.\n\n" +
                                 $"Но найдено {partialConfigs.Count} частично рабочих конфигов.\n" +
                                 "Их можно использовать как запасной вариант.\n\n" +
                                 $"Топ по количеству рабочих сайтов и пингу:\n\n{topConfigs}\n\n" +
                                 "Важно: эти конфиги работают частично. Если что-то будет работать нестабильно, просто переключитесь на другой.";

                SecondaryBtn.Content = "Закрыть";
                SecondaryBtn.Style = (Style)FindResource("AccentBtn");
                PrimaryBtn.Content = "Выбрать частичный конфиг";
                PrimaryBtn.Visibility = Visibility.Visible;
            }
            else
            {
                ProgressBarContainer.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                TimeRemainingText.Visibility = Visibility.Collapsed;
                LogContainer.Visibility = Visibility.Collapsed;
                StatusPanel.Visibility = Visibility.Visible;
                StatusText.Text = "Не найдено рабочих конфигов с 12/12 успешными тестами.\n\n" +
                                 "Возможно, ваша сеть имеет особые ограничения. Попробуйте повторить тест позже.";
                StopIndeterminateAnimation();
                StatusIcon.Visibility = Visibility.Visible;
                StatusIcon.Data = (Geometry)FindResource("WarningIcon");
                StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

                SecondaryBtn.Content = "Закрыть";
                SecondaryBtn.Style = (Style)FindResource("AccentBtn");
                PrimaryBtn.Content = "Повторить тест";
                PrimaryBtn.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ProgressBarContainer.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
            TimeRemainingText.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = $"Ошибка: {ex.Message}";
            StopIndeterminateAnimation();
            StatusIcon.Visibility = Visibility.Visible;
            StatusIcon.Data = (Geometry)FindResource("WarningIcon");
            StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

            SecondaryBtn.Content = "Закрыть";
            SecondaryBtn.Style = (Style)FindResource("AccentBtn");
            PrimaryBtn.Content = "Повторить тест";
            PrimaryBtn.Visibility = Visibility.Visible;
        }

        _isTesting = false;
    }

    private async void SecondaryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SecondaryBtn.Content?.ToString() == "Назад к списку")
        {
            LogContainer.Visibility = Visibility.Collapsed;
            ConfigListScroll.Visibility = Visibility.Visible;

            SecondaryBtn.Content = "Применить";

            PrimaryBtn.Content = "Проверить конфиг";
            PrimaryBtn.Visibility = Visibility.Visible;

            PrimaryBtn.Click -= PrimaryBtn_Click;
            PrimaryBtn.Click += PrimaryBtn_Click;
            return;
        }
        else if (SecondaryBtn.Content?.ToString() == "Да, начать")
        {
            await StartTestingAsync();
            return;
        }
        else if (SecondaryBtn.Content?.ToString() == "Применить")
        {
            if (_cache != null && !string.IsNullOrEmpty(_cache.CurrentConfig))
            {
                ApplyConfigProgress.Visibility = Visibility.Visible;

                SecondaryBtn.IsEnabled = false;
                PrimaryBtn.IsEnabled = false;
                var originalContent = SecondaryBtn.Content;
                SecondaryBtn.Content = "Применение...";

                bool success = await ZapretConfigService.ApplyConfigAsync(_zapretPath, _cache.CurrentConfig);

                SecondaryBtn.IsEnabled = true;
                PrimaryBtn.IsEnabled = true;
                SecondaryBtn.Content = originalContent;

                ApplyConfigProgress.Visibility = Visibility.Collapsed;

                if (success)
                {
                    ConfigWasApplied = true;
                    await Task.Delay(1000);
                    Close();
                }
                else
                {
                    bool isModConfig = _cache is not null && (
                        _cache.ValidConfigs?.Any(c => c.Name == _cache.CurrentConfig && c.IsFromMod) == true ||
                        _cache.PartialConfigs?.Any(c => c.Name == _cache.CurrentConfig && c.IsFromMod) == true);

                    StatusPanel.Visibility = Visibility.Visible;
                    ConfigListScroll.Visibility = Visibility.Collapsed;
                    StatusIcon.Visibility = Visibility.Visible;
                    StatusIcon.Data = (Geometry)FindResource("WarningIcon");
                    StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

                    if (isModConfig)
                    {
                        StatusText.Text = "Не удалось применить конфиг.\n\n" +
                                         "Скорее всего в вашем моде ошибка, либо он вообще не подходит для стратегии. " +
                                         "Проверьте:\n" +
                                         "1. Запущено ли приложение от администратора\n" +
                                         "2. Корректность strategy.bat в редакторе\n" +
                                         "3. Логи в консоли для деталей";
                    }
                    else
                    {
                        StatusText.Text = "Не удалось применить конфиг. Проверьте:\n" +
                                         "1. Запущено ли приложение от администратора\n" +
                                         "2. Правильно ли указан путь к Zapret\n" +
                                         "3. Логи в консоли для деталей";
                    }

                    SecondaryBtn.Content = "Закрыть";
                    PrimaryBtn.Visibility = Visibility.Collapsed;
                }
            }
            return;
        }
        else if (SecondaryBtn.Content?.ToString() == "Выбрать конфиг")
        {
            ShowConfigList();
            return;
        }
        else
        {
            _isTesting = false;

            if (_testProcess != null && !_testProcess.HasExited)
            {
                try
                {
                    ForceKillProcessTree(_testProcess.Id);
                    _testProcess.Dispose();
                }
                catch { }
            }

            try
            {
                var processes = Process.GetProcessesByName("winws");
                foreach (var proc in processes)
                {
                    try
                    {
                        ForceKillProcessTree(proc.Id);
                        proc.Dispose();
                    }
                    catch { }
                }

                var powerShellProcs = Process.GetProcessesByName("powershell");
                foreach (var proc in powerShellProcs)
                {
                    try
                    {
                        proc.Kill(true);
                        proc.Dispose();
                    }
                    catch { }
                }
            }
            catch { }

            Close();
        }
    }

    private async void PrimaryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PrimaryBtn.Content?.ToString() == "Нет, выйти")
        {
            Close();
            return;
        }

        if (PrimaryBtn.Content?.ToString() == "Отмена")
        {
            Close();
            return;
        }

        if (ConfigListScroll.Visibility == Visibility.Visible && _cache != null && !string.IsNullOrEmpty(_cache.CurrentConfig))
        {
            await TestCurrentConfigAsync();
        }
        else
        {
            if (StatusPanel.Visibility == Visibility.Visible && PrimaryBtn.Content.ToString() == "Выбрать конфиг")
            {
                ShowConfigList();
            }
            else
            {
                await StartTestingAsync();
            }
        }
    }

    private async Task TestCurrentConfigAsync()
    {
        if (_cache == null || string.IsNullOrEmpty(_cache.CurrentConfig)) return;

        ConfigListScroll.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        ProgressBarContainer.Visibility = Visibility.Collapsed;
        LogContainer.Visibility = Visibility.Visible;

        PrimaryBtn.Visibility = Visibility.Collapsed;
        SecondaryBtn.Content = "Отмена";
        SecondaryBtn.Style = (Style)FindResource("OutlineBtn");

        LogTextBox.Document.Blocks.Clear();
        AppendColoredLog($"🔄 Тестирую конфиг: {_cache.CurrentConfig}\n", Color.FromRgb(0x3b, 0x82, 0xf6));

        var (isWorking, message) = await ZapretConfigService.TestSingleConfigAsync(
            _zapretPath,
            _cache.CurrentConfig,
            status => Dispatcher.Invoke(() =>
            {
                Color logColor;
                if (status.Contains("✅") || status.Contains("работает") || status.Contains("доступен"))
                    logColor = Color.FromRgb(0x22, 0xc5, 0x5e);
                else if (status.Contains("❌") || status.Contains("не работает") || status.Contains("недоступен"))
                    logColor = Color.FromRgb(0xef, 0x44, 0x44);
                else if (status.Contains("🔄") || status.Contains("Тестирую"))
                    logColor = Color.FromRgb(0x3b, 0x82, 0xf6);
                else
                    logColor = Color.FromRgb(0xf0, 0xf0, 0xf0);

                AppendColoredLog(status, logColor);
            })
        );

        if (isWorking)
        {
            AppendColoredLog($"\n✅ {message}", Color.FromRgb(0x22, 0xc5, 0x5e));
        }
        else
        {
            AppendColoredLog($"\n❌ {message}", Color.FromRgb(0xef, 0x44, 0x44));
        }

        PrimaryBtn.Visibility = Visibility.Visible;
        PrimaryBtn.Content = "Закрыть";
        PrimaryBtn.Click -= PrimaryBtn_Click;
        PrimaryBtn.Click += (s, e) => Close();
        SecondaryBtn.Content = "Назад к списку";
        SecondaryBtn.Style = (Style)FindResource("AccentBtn");
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowConfigList()
    {
        if (_cache == null || !_cache.HasAnyConfigs) return;
        StopIndeterminateAnimation();
        StatusPanel.Visibility = Visibility.Collapsed;
        ProgressBarContainer.Visibility = Visibility.Collapsed;
        ConfigListScroll.Visibility = Visibility.Visible;
        ConfigListPanel.Children.Clear();
        var selectableConfigs = _cache.GetSelectableConfigs();
        var usingPartialConfigs = _cache.ValidConfigs.Count == 0 && _cache.PartialConfigs.Count > 0;

        var currentLabel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var activeText = new TextBlock
        {
            Text = "Активный конфиг: ",
            FontSize = 15,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold
        };

        var configNameText = new TextBlock
        {
            Text = _cache.CurrentConfig,
            FontSize = 15,
            Foreground = new SolidColorBrush(usingPartialConfigs
                ? Color.FromRgb(0xea, 0xb3, 0x08)
                : Color.FromRgb(0x22, 0xc5, 0x5e)),
            FontWeight = FontWeights.Bold
        };

        currentLabel.Children.Add(activeText);
        currentLabel.Children.Add(configNameText);
        ConfigListPanel.Children.Add(currentLabel);

        if (usingPartialConfigs)
        {
            ConfigListPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x20, 0x08)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = "Идеальных конфигов не найдено. Ниже показаны частично рабочие варианты без ошибок и недоступных сервисов. Если что-то будет работать нестабильно, переключитесь на другой конфиг.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0xf3, 0xc4)),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = "Доступные конфиги",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x2a, 0x3a)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = $"{selectableConfigs.Count} конфигов",
                FontSize = 11,
                Foreground = new SolidColorBrush(usingPartialConfigs
                    ? Color.FromRgb(0xea, 0xb3, 0x08)
                    : Color.FromRgb(0x3b, 0x82, 0xf6))
            }
        };

        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(badge, 1);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(badge);
        ConfigListPanel.Children.Add(headerGrid);

        foreach (var config in selectableConfigs)
        {
            var isCurrent = config.Name == _cache.CurrentConfig;

            var border = new Border
            {
                Background = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x1a, 0x25, 0x3a))
                    : new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c)),
                BorderBrush = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
                    : new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2a)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();

            var nameRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            if (config.IsFromMod)
            {
                nameRow.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock
                    {
                        Text = "МОД",
                        Foreground = Brushes.Black,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold
                    }
                });
            }

            var nameText = new TextBlock
            {
                Text = config.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(config.IsValid ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0xea, 0xb3, 0x08)),
                VerticalAlignment = VerticalAlignment.Center
            };
            nameRow.Children.Add(nameText);

            if (isCurrent)
            {
                var activeBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x30, 0x4a)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "активный",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
                    }
                };
                nameRow.Children.Add(activeBadge);
            }

            var infoText = new TextBlock
            {
                Text = config.IsFromMod
                    ? config.ModName ?? "?"
                    : $"Пинг: {config.AveragePing} мс  •  Тесты: {config.SuccessCount}/12" + (config.IsPartiallyUsable ? "  •  частично" : ""),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58)),
                Margin = new Thickness(0, 4, 0, 0)
            };

            left.Children.Add(nameRow);
            left.Children.Add(infoText);

            var arrow = new TextBlock
            {
                Text = isCurrent ? "✓" : "→",
                FontSize = 14,
                Foreground = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
                    : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x36)),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(left, 0);
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(left);
            grid.Children.Add(arrow);

            border.Child = grid;

            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1)
                {
                    _cache.CurrentConfig = config.Name;
                    ZapretConfigService.SaveCache(_cache);
                    ShowConfigList();
                }
            };

            border.MouseLeftButtonDown += async (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    _cache.CurrentConfig = config.Name;
                    ZapretConfigService.SaveCache(_cache);

                    SecondaryBtn_Click(s, e);
                }
            };

            border.MouseEnter += (s, e) =>
            {
                if (!isCurrent)
                    border.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x23));
            };
            border.MouseLeave += (s, e) =>
            {
                if (!isCurrent)
                    border.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c));
            };

            ConfigListPanel.Children.Add(border);
        }

        SecondaryBtn.Content = "Применить";
        PrimaryBtn.Content = "Проверить конфиг";
        PrimaryBtn.Visibility = Visibility.Visible;
    }

    private void StopIndeterminateAnimation()
    {
        ProgressBarContainer.Visibility = Visibility.Collapsed;
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

}
