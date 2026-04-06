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
    private ZapretConfigCache? _cache;
    private bool _isTesting = false;

    public ZapretConfigWindow(string zapretPath)
    {
        InitializeComponent();
        _zapretPath = zapretPath;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartIndeterminateAnimation();

        // Загрузить кэш
        _cache = ZapretConfigService.LoadCache();

        if (_cache == null || _cache.ValidConfigs.Count == 0)
        {
            // Первый запуск - начать тестирование
            StatusText.Text = "Первый запуск. Начинаем тестирование всех конфигов...";
            await Task.Delay(1000);
            await StartTestingAsync();
        }
        else
        {
            // Показать список конфигов
            ShowConfigList();
        }
    }

    private void StartIndeterminateAnimation()
    {
        var anim = new DoubleAnimation
        {
            From = -80,
            To = ProgressBarContainer.ActualWidth,
            Duration = TimeSpan.FromSeconds(1.5),
            RepeatBehavior = RepeatBehavior.Forever
        };
        BarTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void StopIndeterminateAnimation()
    {
        BarTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ProgressBarContainer.Visibility = Visibility.Collapsed;
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
            var configs = await ZapretConfigService.TestAllConfigsAsync(
                _zapretPath,
                status => Dispatcher.Invoke(() => StatusText.Text = status),
                (current, total) => Dispatcher.Invoke(() => 
                {
                    StatusText.Text = $"Тестирование конфигов: {current}/{total}";
                })
            );

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

                StatusText.Text = $"Найдено {configs.Count} рабочих конфигов!";
                StopIndeterminateAnimation();

                await Task.Delay(1000);
                ShowConfigList();
            }
            else
            {
                StatusText.Text = "Не найдено рабочих конфигов";
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

    private void ShowConfigList()
    {
        if (_cache == null || _cache.ValidConfigs.Count == 0) return;

        StopIndeterminateAnimation();
        StatusPanel.Visibility = Visibility.Collapsed;
        ConfigListScroll.Visibility = Visibility.Visible;
        ConfigListPanel.Children.Clear();

        // Заголовок
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
            Text = "Выберите конфиг из списка:",
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
                Text = $"Пинг: {config.AveragePing} мс • Успешных тестов: {config.SuccessCount}",
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

    private void SecondaryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting)
        {
            _isTesting = false;
            StatusText.Text = "Тестирование отменено";
        }
        Close();
    }

    private async void PrimaryBtn_Click(object sender, RoutedEventArgs e)
    {
        // Повторить тестирование
        ConfigListScroll.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        ProgressBarContainer.Visibility = Visibility.Visible;
        StartIndeterminateAnimation();
        
        await StartTestingAsync();
    }
}
