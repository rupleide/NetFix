using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    public ZapretConfigWindow(string zapretPath, bool testMode)
    {
        InitializeComponent();
        _zapretPath = zapretPath;
        _testMode = testMode;
        Loaded += OnLoaded;
        Closing += OnClosing;
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
            
            // Показать прогресс-бар и скрыть StatusPanel
            StatusPanel.Visibility = Visibility.Collapsed;
            ProgressBarContainer.Visibility = Visibility.Visible;
            ProgressText.Visibility = Visibility.Visible;
            ProgressText.Text = "Тестирование конфигов: 0%";
            
            var (configs, testProcess) = await ZapretConfigService.TestAllConfigsAsync(
                _zapretPath,
                status => Dispatcher.Invoke(() => 
                {
                    // Игнорируем статусы, показываем только прогресс
                }),
                (current, total) => Dispatcher.Invoke(() => 
                {
                    // Обновляем прогресс-бар
                    var percentage = (current * 100 / total);
                    var progressWidth = (ProgressBarContainer.ActualWidth * current / total);
                    ProgressBar.Width = progressWidth;
                    ProgressText.Text = $"Тестирование конфигов: {current}/{total} ({percentage}%)";
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

                // Скрыть прогресс-бар и показать поздравление
                ProgressBarContainer.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
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
                // Скрыть прогресс-бар и показать ошибку
                ProgressBarContainer.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
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
            // Скрыть прогресс-бар и показать ошибку
            ProgressBarContainer.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
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
        // Запустить тестирование
        await StartTestingAsync();
    }

    private void StopIndeterminateAnimation()
    {
        ProgressBarContainer.Visibility = Visibility.Collapsed;
    }

    private void ShowConfigList()
    {
        if (_cache == null || _cache.ValidConfigs.Count == 0) return;

        StopIndeterminateAnimation();
        StatusPanel.Visibility = Visibility.Collapsed;
        ConfigListScroll.Visibility = Visibility.Visible;
        ConfigListPanel.Children.Clear();

        // Заголовок с поздравлением
        var congratsText = new TextBlock
        {
            Text = "✅ Тестирование завершено успешно!",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        ConfigListPanel.Children.Add(congratsText);

        var headerText = new TextBlock
        {
            Text = $"Текущий конфиг: {_cache.CurrentConfig}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        ConfigListPanel.Children.Add(headerText);

        var subText = new TextBlock
        {
            Text = $"Найдено {_cache.ValidConfigs.Count} идеальных конфигов (12/12 тестов):",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        ConfigListPanel.Children.Add(subText);

        // Список конфигов
        foreach (var config in _cache.ValidConfigs)
        {
            var isCurrent = config.Name == _cache.CurrentConfig;

            var border = new Border
            {
                Background = isCurrent 
                    ? new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
                    : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var stack = new StackPanel();

            var nameText = new TextBlock
            {
                Text = config.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            };

            var infoText = new TextBlock
            {
                Text = $"Пинг: {config.AveragePing} мс • Успешных тестов: {config.SuccessCount}/12",
                FontSize = 11,
                Foreground = isCurrent 
                    ? new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0))
                    : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Margin = new Thickness(0, 4, 0, 0)
            };

            stack.Children.Add(nameText);
            stack.Children.Add(infoText);
            border.Child = stack;

            border.MouseLeftButtonDown += (s, e) =>
            {
                _cache.CurrentConfig = config.Name;
                ZapretConfigService.SaveCache(_cache);
                ShowConfigList(); // Перерисовать список
            };

            ConfigListPanel.Children.Add(border);
        }

        SecondaryBtn.Content = "Закрыть";
        PrimaryBtn.Content = "Повторить тест";
        PrimaryBtn.Visibility = Visibility.Visible;
    }
}