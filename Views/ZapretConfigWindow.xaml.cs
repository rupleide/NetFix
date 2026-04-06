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

    public ZapretConfigWindow(string zapretPath, bool testMode)
    {
        InitializeComponent();
        _zapretPath = zapretPath;
        _testMode = testMode;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void AppendColoredLog(string text, Color color)
    {
        var paragraph = LogTextBox.Document.Blocks.LastBlock as Paragraph;
        if (paragraph == null)
        {
            paragraph = new Paragraph();
            LogTextBox.Document.Blocks.Add(paragraph);
        }

        // Проверяем, является ли это заголовком
        bool isHeader = text.Contains("[HEADER]");
        if (isHeader)
        {
            text = text.Replace("[HEADER]", "").Replace("[/HEADER]", "");
        }

        var run = new Run(text + "\n")
        {
            Foreground = new SolidColorBrush(color),
            FontSize = isHeader ? 16 : 12,  // Ещё крупнее для заголовков (было 14)
            FontWeight = isHeader ? FontWeights.ExtraBold : FontWeights.Normal  // ExtraBold вместо Bold
        };
        paragraph.Inlines.Add(run);
        
        LogScrollViewer.ScrollToEnd();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Остановить тестирование при закрытии окна
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
        
        // Убить все winws.exe и powershell.exe процессы которые могли запуститься во время теста
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
            
            // Также убить любые PowerShell процессы, запущенные от нашего процесса
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

    private static void ForceKillProcessTree(int pid)
    {
        try
        {
            // Убить основной процесс
            var process = Process.GetProcessById(pid);
            process.Kill(true);
            process.WaitForExit(2000); // Ждем 2 секунды
        }
        catch (ArgumentException)
        {
            // Процесс уже завершён
        }
        catch (Exception)
        {
            // В случае ошибки используем команду taskkill для полного уничтожения
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /PID {pid} /T", // /T - убить дерево процессов
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
        // Загрузить кэш
        _cache = ZapretConfigService.LoadCache();

        if (_testMode)
        {
            // Режим тестирования - показать сообщение о подтверждении в текущем окне
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
            
            SecondaryBtn.Content = "Нет, выйти";
            PrimaryBtn.Content = "Да, начать";
            PrimaryBtn.Visibility = Visibility.Visible;
        }
        else
        {
            // Режим выбора конфига
            if (_cache == null || _cache.ValidConfigs.Count == 0)
            {
                // Нет кэша - показать предупреждение
                ShowWarningNoCache();
            }
            else
            {
                // Показать список конфигов
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
        PrimaryBtn.Visibility = Visibility.Collapsed;

        // Остановить и удалить сервис Zapret если установлен
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

        // Удалить сервис Zapret если установлен
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
                
                // Если сервис существует (код возврата 0), удалить его
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
            // Игнорируем ошибки удаления сервиса
        }

        try
        {
            StatusText.Text = "Запуск полного тестирования конфигов...\n\n" +
                             "💡 Советуем вам подождать 10 минуток на полное сканирование.\n" +
                             "В дальнейшем это сэкономит вам кучу времени и нервов!\n\n" +
                             "Приложение найдёт все идеальные конфиги (12/12 тестов) и выберет лучший.";
            
            await Task.Delay(3000);
            
            // Показать прогресс-бар, лог и скрыть StatusPanel
            StatusPanel.Visibility = Visibility.Collapsed;
            ProgressBarContainer.Visibility = Visibility.Visible;
            ProgressText.Visibility = Visibility.Visible;
            TimeRemainingText.Visibility = Visibility.Visible;
            ProgressText.Text = "Тестирование конфигов: 0%";
            TimeRemainingText.Text = "Осталось: ~10 мин";
            LogContainer.Visibility = Visibility.Visible;
            
            // Запомнить время начала
            _testStartTime = DateTime.Now;
            
            // Очистить лог и добавить начальное сообщение
            LogTextBox.Document.Blocks.Clear();
            AppendColoredLog("💡 Советуем вам подождать 10 минуток на полное сканирование.", Color.FromRgb(0xf0, 0xf0, 0xf0));
            AppendColoredLog("В дальнейшем это сэкономит вам кучу времени и нервов!\n", Color.FromRgb(0xf0, 0xf0, 0xf0));
            AppendColoredLog("Запуск тестирования...\n", Color.FromRgb(0x88, 0x88, 0x88));
            
            var (configs, testProcess) = await ZapretConfigService.TestAllConfigsAsync(
                _zapretPath,
                status => Dispatcher.Invoke(() => 
                {
                    // Добавляем в лог с цветом в зависимости от содержимого
                    Color logColor;
                    if (status.Contains("❌") || status.Contains("НЕ РАБОТАЕТ") || status.Contains("НЕРАБОЧИЙ"))
                        logColor = Color.FromRgb(0xef, 0x44, 0x44); // Красный
                    else if (status.Contains("✅") || status.Contains("РАБОТАЕТ") || status.Contains("РАБОЧИЙ"))
                        logColor = Color.FromRgb(0x22, 0xc5, 0x5e); // Зелёный
                    else if (status.Contains("🔄") || status.Contains("Тестирую"))
                        logColor = Color.FromRgb(0x3b, 0x82, 0xf6); // Синий
                    else if (status.Contains("⚠️") || status.Contains("ЧАСТИЧНО"))
                        logColor = Color.FromRgb(0xea, 0xb3, 0x08); // Жёлтый
                    else
                        logColor = Color.FromRgb(0xf0, 0xf0, 0xf0); // Белый по умолчанию
                    
                    AppendColoredLog(status, logColor);
                }),
                (current, total) => Dispatcher.Invoke(() => 
                {
                    // Сохранить общее количество конфигов
                    if (_totalConfigs == 0)
                        _totalConfigs = total;
                    
                    // Обновляем прогресс-бар
                    var percentage = (current * 100 / total);
                    var progressWidth = (ProgressBarContainer.ActualWidth * current / total);
                    ProgressBar.Width = progressWidth;
                    ProgressText.Text = $"Тестирование конфигов: {current}/{total} ({percentage}%)";
                    
                    // Рассчитать оставшееся время
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

            if (!_isTesting) return; // Отменено

            if (configs.Count > 0)
            {
                // Сохранить результаты
                _cache = new ZapretConfigCache
                {
                    LastTested = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    CurrentConfig = configs[0].Name,
                    ValidConfigs = configs
                };
                ZapretConfigService.SaveCache(_cache);

                // Скрыть прогресс-бар и лог, показать поздравление
                ProgressBarContainer.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                TimeRemainingText.Visibility = Visibility.Collapsed;
                LogContainer.Visibility = Visibility.Collapsed;
                StopIndeterminateAnimation();
                StatusPanel.Visibility = Visibility.Visible;
                StatusIcon.Visibility = Visibility.Visible;
                StatusIcon.Data = (Geometry)FindResource("CheckmarkIcon");
                StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                
                var topConfigs = string.Join("\n", configs.Take(5).Select((c, i) => 
                    $"{i + 1}. {c.Name} (пинг: {c.AveragePing} мс, тестов: {c.SuccessCount}/12)"));
                
                StatusText.Text = $"🎉 Поздравляю с полным тестированием!\n\n" +
                                 $"Найдено {configs.Count} идеальных конфигов.\n" +
                                 $"Все они прошли 12/12 тестов без ошибок!\n\n" +
                                 $"Ваш топ конфигов на следующие разы:\n\n{topConfigs}";

                await Task.Delay(3000);
                ShowConfigList();
            }
            else
            {
                // Скрыть прогресс-бар и лог, показать ошибку
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
                PrimaryBtn.Content = "Повторить тест";
                PrimaryBtn.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            // Скрыть прогресс-бар и лог, показать ошибку
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
            PrimaryBtn.Content = "Повторить тест";
            PrimaryBtn.Visibility = Visibility.Visible;
        }

        _isTesting = false;
    }

    private void SecondaryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_testMode && _cache == null) // Если режим тестирования и ещё не начали
        {
            Close();
        }
        else
        {
            _isTesting = false;
            
            // Убить процесс тестирования
            if (_testProcess != null && !_testProcess.HasExited)
            {
                try
                {
                    ForceKillProcessTree(_testProcess.Id);
                    _testProcess.Dispose();
                }
                catch { }
            }
            
            // Убить все winws.exe и powershell.exe процессы
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
                
                // Также убить любые PowerShell процессы
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
        // Если показан список конфигов и есть выбранный конфиг - тестировать только его
        if (ConfigListScroll.Visibility == Visibility.Visible && _cache != null && !string.IsNullOrEmpty(_cache.CurrentConfig))
        {
            await TestCurrentConfigAsync();
        }
        else
        {
            // Запустить полное тестирование
            await StartTestingAsync();
        }
    }

    private async Task TestCurrentConfigAsync()
    {
        if (_cache == null || string.IsNullOrEmpty(_cache.CurrentConfig)) return;

        // Скрыть список конфигов и показать лог
        ConfigListScroll.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        ProgressBarContainer.Visibility = Visibility.Collapsed;
        LogContainer.Visibility = Visibility.Visible;
        
        PrimaryBtn.Visibility = Visibility.Collapsed;
        SecondaryBtn.Content = "Отмена";

        // Очистить лог
        LogTextBox.Document.Blocks.Clear();
        AppendColoredLog($"🔄 Тестирую конфиг: {_cache.CurrentConfig}\n", Color.FromRgb(0x3b, 0x82, 0xf6));

        var (isWorking, message) = await ZapretConfigService.TestSingleConfigAsync(
            _zapretPath,
            _cache.CurrentConfig,
            status => Dispatcher.Invoke(() => 
            {
                Color logColor;
                if (status.Contains("✅") || status.Contains("работает") || status.Contains("доступен"))
                    logColor = Color.FromRgb(0x22, 0xc5, 0x5e); // Зелёный
                else if (status.Contains("❌") || status.Contains("не работает") || status.Contains("недоступен"))
                    logColor = Color.FromRgb(0xef, 0x44, 0x44); // Красный
                else if (status.Contains("🔄") || status.Contains("Тестирую"))
                    logColor = Color.FromRgb(0x3b, 0x82, 0xf6); // Синий
                else
                    logColor = Color.FromRgb(0xf0, 0xf0, 0xf0); // Белый
                
                AppendColoredLog(status, logColor);
            })
        );

        // Показать результат
        if (isWorking)
        {
            AppendColoredLog($"\n✅ {message}", Color.FromRgb(0x22, 0xc5, 0x5e));
        }
        else
        {
            AppendColoredLog($"\n❌ {message}", Color.FromRgb(0xef, 0x44, 0x44));
        }

        await Task.Delay(3000);
        
        // Вернуться к списку конфигов
        LogContainer.Visibility = Visibility.Collapsed;
        ConfigListScroll.Visibility = Visibility.Visible;
        PrimaryBtn.Visibility = Visibility.Visible;
        SecondaryBtn.Content = "Закрыть";
    }

    private void StopIndeterminateAnimation()
    {
        ProgressBarContainer.Visibility = Visibility.Collapsed;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowConfigList()
    {
        if (_cache == null || _cache.ValidConfigs.Count == 0) return;
        StopIndeterminateAnimation();
        StatusPanel.Visibility = Visibility.Collapsed;
        ConfigListScroll.Visibility = Visibility.Visible;
        ConfigListPanel.Children.Clear();

        // Заголовок
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
                Text = $"{_cache.ValidConfigs.Count} конфигов",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
            }
        };

        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(badge, 1);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(badge);
        ConfigListPanel.Children.Add(headerGrid);

        var currentLabel = new TextBlock
        {
            Text = $"Активный: {_cache.CurrentConfig}",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58)),
            Margin = new Thickness(0, 2, 0, 14)
        };
        ConfigListPanel.Children.Add(currentLabel);

        // Список конфигов
        foreach (var config in _cache.ValidConfigs)
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
            var nameText = new TextBlock
            {
                Text = config.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
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
                Text = $"Пинг: {config.AveragePing} мс  •  Тесты: {config.SuccessCount}/12",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58)),
                Margin = new Thickness(0, 4, 0, 0)
            };

            left.Children.Add(nameRow);
            left.Children.Add(infoText);

            // Стрелка справа
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
                _cache.CurrentConfig = config.Name;
                ZapretConfigService.SaveCache(_cache);
                ShowConfigList();
            };

            // Hover эффект
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

        SecondaryBtn.Content = "Закрыть";
        PrimaryBtn.Content = "Проверить выбранный конфиг";
        PrimaryBtn.Visibility = Visibility.Visible;
    }
}