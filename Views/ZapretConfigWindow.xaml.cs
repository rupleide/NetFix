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
using Point = System.Windows.Point;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

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
    private string? _selectedConfig;
    private string? _activeDialogConfigName;
    private TextBlock? _activeConfigHeaderTextBlock;
    private readonly Dictionary<string, ConfigCardControls> _cardControls = new();
    private static readonly ImageBrush NoiseBrush = CreateNoiseBrush();

    private sealed class ConfigCardControls
    {
        public string ConfigName { get; set; } = "";
        public bool IsCurrent { get; set; }
        public bool IsSelected { get; set; }
        public Color DefaultAccentColor { get; set; }
        public Border OuterBorder { get; set; } = null!;
        public Border InnerBorder { get; set; } = null!;
        public Border StatusStrip { get; set; } = null!;
        public TextBlock NameText { get; set; } = null!;
        public Border ActiveBadge { get; set; } = null!;
        public Border SelectedBadge { get; set; } = null!;
        public TextBlock Arrow { get; set; } = null!;
        public System.Windows.Shapes.Rectangle BottomGlow { get; set; } = null!;
        public System.Windows.Shapes.Rectangle TopGlow { get; set; } = null!;
        public GradientStop BottomGlowStop { get; set; } = null!;
        public GradientStop TopGlowStop { get; set; } = null!;
    }

    private static ImageBrush CreateNoiseBrush()
    {
        var brush = new ImageBrush(new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/noise.png")))
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 512, 512),
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }

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
        RootGrid.Opacity = 1.0;
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

    private static void AnimateBrushColor(System.Windows.Media.Brush brush, Color targetColor, int durationMs)
    {
        if (brush is SolidColorBrush scb && !scb.IsFrozen)
        {
            var anim = new ColorAnimation(scb.Color, targetColor, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            scb.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }

    private static void AnimateGradientColor(GradientStop stop, Color targetColor, int durationMs)
    {
        if (!stop.IsFrozen)
        {
            var anim = new ColorAnimation(stop.Color, targetColor, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            stop.BeginAnimation(GradientStop.ColorProperty, anim);
        }
    }

    private async Task AnimateConfigActivationAsync(string oldConfigName, string newConfigName)
    {
        if (_activeConfigHeaderTextBlock != null && _cache != null)
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (_, _) =>
            {
                _activeConfigHeaderTextBlock.Text = _cache.GetDisplayName(newConfigName);
                var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                _activeConfigHeaderTextBlock.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            };
            _activeConfigHeaderTextBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        if (!string.IsNullOrEmpty(oldConfigName) &&
            oldConfigName != newConfigName &&
            _cardControls.TryGetValue(oldConfigName, out var oldCard))
        {
            oldCard.IsCurrent = false;
            oldCard.IsSelected = false;

            var fadeOutBadge = new DoubleAnimation(oldCard.ActiveBadge.Opacity, 0.0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = ease
            };
            fadeOutBadge.Completed += (_, _) => oldCard.ActiveBadge.Visibility = Visibility.Collapsed;
            oldCard.ActiveBadge.BeginAnimation(UIElement.OpacityProperty, fadeOutBadge);

            AnimateBrushColor(oldCard.OuterBorder.Background, Color.FromRgb(0x26, 0x26, 0x2a), 500);

            var targetInner = oldCard.OuterBorder.IsMouseOver
                ? Color.FromRgb(0x20, 0x20, 0x24)
                : Color.FromRgb(0x1a, 0x1a, 0x1c);
            AnimateBrushColor(oldCard.InnerBorder.Background, targetInner, 500);

            AnimateBrushColor(oldCard.StatusStrip.Background, oldCard.DefaultAccentColor, 500);
            AnimateBrushColor(oldCard.NameText.Foreground, oldCard.DefaultAccentColor, 500);

            oldCard.Arrow.Text = "→";
            AnimateBrushColor(oldCard.Arrow.Foreground, Color.FromRgb(0x44, 0x44, 0x48), 350);

            AnimateGradientColor(oldCard.BottomGlowStop, oldCard.DefaultAccentColor, 500);
            AnimateGradientColor(oldCard.TopGlowStop, oldCard.DefaultAccentColor, 500);
        }

        if (_cardControls.TryGetValue(newConfigName, out var newCard))
        {
            newCard.IsCurrent = true;
            newCard.IsSelected = false;

            if (newCard.SelectedBadge.Visibility == Visibility.Visible)
            {
                var fadeOutSelected = new DoubleAnimation(newCard.SelectedBadge.Opacity, 0.0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease
                };
                fadeOutSelected.Completed += (_, _) => newCard.SelectedBadge.Visibility = Visibility.Collapsed;
                newCard.SelectedBadge.BeginAnimation(UIElement.OpacityProperty, fadeOutSelected);
            }

            newCard.ActiveBadge.Visibility = Visibility.Visible;
            var fadeInActive = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = ease
            };
            newCard.ActiveBadge.BeginAnimation(UIElement.OpacityProperty, fadeInActive);

            var activeGreen = Color.FromRgb(0x22, 0xc5, 0x5e);
            var activeBgGreen = Color.FromRgb(0x14, 0x26, 0x1c);

            AnimateBrushColor(newCard.OuterBorder.Background, activeGreen, 500);
            AnimateBrushColor(newCard.InnerBorder.Background, activeBgGreen, 500);
            AnimateBrushColor(newCard.StatusStrip.Background, activeGreen, 500);
            AnimateBrushColor(newCard.NameText.Foreground, activeGreen, 500);

            newCard.Arrow.Text = "✓";
            AnimateBrushColor(newCard.Arrow.Foreground, activeGreen, 400);

            AnimateGradientColor(newCard.BottomGlowStop, activeGreen, 300);
            AnimateGradientColor(newCard.TopGlowStop, activeGreen, 300);

            var pulseGlow = new DoubleAnimation(0.22, 0.65, TimeSpan.FromMilliseconds(450))
            {
                AutoReverse = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            newCard.BottomGlow.BeginAnimation(UIElement.OpacityProperty, pulseGlow);
            newCard.TopGlow.BeginAnimation(UIElement.OpacityProperty, pulseGlow);
        }

        await Task.Delay(1300);
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
            var configToApply = !string.IsNullOrEmpty(_selectedConfig) ? _selectedConfig : _cache?.CurrentConfig;
            if (_cache != null && !string.IsNullOrEmpty(configToApply))
            {
                ApplyConfigProgress.Visibility = Visibility.Visible;

                SecondaryBtn.IsEnabled = false;
                PrimaryBtn.IsEnabled = false;
                var originalContent = SecondaryBtn.Content;
                SecondaryBtn.Content = "Применение...";

                bool success = await ZapretConfigService.ApplyConfigAsync(_zapretPath, configToApply);

                SecondaryBtn.IsEnabled = true;
                PrimaryBtn.IsEnabled = true;
                SecondaryBtn.Content = originalContent;

                ApplyConfigProgress.Visibility = Visibility.Collapsed;

                if (success)
                {
                    var oldConfigName = _cache.CurrentConfig;
                    _cache.CurrentConfig = configToApply;
                    ZapretConfigService.SaveCache(_cache);
                    ConfigWasApplied = true;

                    SecondaryBtn.Content = "Применено ✓";
                    SecondaryBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));

                    await AnimateConfigActivationAsync(oldConfigName, configToApply);
                    Close();
                }
                else
                {
                    bool isModConfig = _cache is not null && (
                        _cache.ValidConfigs?.Any(c => c.Name == configToApply && c.IsFromMod) == true ||
                        _cache.PartialConfigs?.Any(c => c.Name == configToApply && c.IsFromMod) == true);

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

        if (ConfigListScroll.Visibility == Visibility.Visible && _cache != null && (!string.IsNullOrEmpty(_selectedConfig) || !string.IsNullOrEmpty(_cache.CurrentConfig)))
        {
            await TestCurrentConfigAsync();
        }
        else
        {
            if (StatusPanel.Visibility == Visibility.Visible && PrimaryBtn.Content?.ToString() == "Выбрать конфиг")
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
        var configToTest = !string.IsNullOrEmpty(_selectedConfig) ? _selectedConfig : _cache?.CurrentConfig;
        if (_cache == null || string.IsNullOrEmpty(configToTest)) return;

        ConfigListScroll.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        ProgressBarContainer.Visibility = Visibility.Collapsed;
        LogContainer.Visibility = Visibility.Visible;

        PrimaryBtn.Visibility = Visibility.Collapsed;
        SecondaryBtn.Content = "Отмена";
        SecondaryBtn.Style = (Style)FindResource("OutlineBtn");

        LogTextBox.Document.Blocks.Clear();
        AppendColoredLog($"🔄 Тестирую конфиг: {configToTest}\n", Color.FromRgb(0x3b, 0x82, 0xf6));

        var (isWorking, message) = await ZapretConfigService.TestSingleConfigAsync(
            _zapretPath,
            configToTest,
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

    private static (Color baseBg, Color hoverBg, Color badgeBg) GetCardSelectionColors(Color accentColor)
    {
        if (accentColor == Color.FromRgb(0xa8, 0x55, 0xf7))
        {
            return (
                Color.FromRgb(0x20, 0x16, 0x2c),
                Color.FromRgb(0x28, 0x1c, 0x3a),
                Color.FromRgb(0x28, 0x18, 0x3d)
            );
        }
        if (accentColor == Color.FromRgb(0xea, 0xb3, 0x08))
        {
            return (
                Color.FromRgb(0x24, 0x20, 0x14),
                Color.FromRgb(0x2e, 0x28, 0x18),
                Color.FromRgb(0x33, 0x29, 0x10)
            );
        }
        return (
            Color.FromRgb(0x15, 0x1b, 0x26),
            Color.FromRgb(0x1a, 0x23, 0x36),
            Color.FromRgb(0x1a, 0x27, 0x40)
        );
    }

    private void ShowConfigList(bool animateEntrance = true)
    {
        if (_cache == null || !_cache.HasAnyConfigs) return;
        StopIndeterminateAnimation();
        StatusPanel.Visibility = Visibility.Collapsed;
        ProgressBarContainer.Visibility = Visibility.Collapsed;
        ConfigListScroll.Visibility = Visibility.Visible;
        ConfigListPanel.Children.Clear();
        _cardControls.Clear();
        var selectableConfigs = _cache.GetSelectableConfigs();
        var usingPartialConfigs = _cache.ValidConfigs.Count == 0 && _cache.PartialConfigs.Count > 0;

        if (_selectedConfig is null || !selectableConfigs.Any(c => c.Name == _selectedConfig))
        {
            _selectedConfig = _cache.CurrentConfig;
        }

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

        var activeConfig = selectableConfigs.FirstOrDefault(c => c.Name == _cache.CurrentConfig);
        var activeDisplayName = activeConfig?.DisplayName ?? _cache.CurrentConfig;

        var configNameText = new TextBlock
        {
            Text = activeDisplayName,
            FontSize = 15,
            Foreground = new SolidColorBrush(usingPartialConfigs
                ? Color.FromRgb(0xea, 0xb3, 0x08)
                : Color.FromRgb(0x22, 0xc5, 0x5e)),
            FontWeight = FontWeights.Bold
        };
        _activeConfigHeaderTextBlock = configNameText;

        var easeCubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        currentLabel.Children.Add(activeText);
        currentLabel.Children.Add(configNameText);

        if (animateEntrance)
        {
            currentLabel.Opacity = 0;
            var curFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(450))
            {
                BeginTime = TimeSpan.FromMilliseconds(30),
                EasingFunction = easeCubicOut
            };
            currentLabel.BeginAnimation(UIElement.OpacityProperty, curFade);
        }

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

        if (animateEntrance)
        {
            headerGrid.Opacity = 0;
            var headFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(450))
            {
                BeginTime = TimeSpan.FromMilliseconds(90),
                EasingFunction = easeCubicOut
            };
            headerGrid.BeginAnimation(UIElement.OpacityProperty, headFade);
        }

        ConfigListPanel.Children.Add(headerGrid);

        var cardUpdaters = new Dictionary<string, Action<bool>>();
        int cardIndex = 0;

        foreach (var config in selectableConfigs)
        {
            var isCurrent = config.Name == _cache.CurrentConfig;
            var isSelected = config.Name == _selectedConfig;

            var defaultAccentColor = config.IsFromMod
                ? Color.FromRgb(0xa8, 0x55, 0xf7)
                : config.IsValid
                    ? Color.FromRgb(0x3b, 0x82, 0xf6)
                    : Color.FromRgb(0xea, 0xb3, 0x08);

            var accentColor = isCurrent
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : defaultAccentColor;

            var (selInnerNormal, selInnerHover, selBadgeBg) = GetCardSelectionColors(defaultAccentColor);

            var border = new Border
            {
                MinHeight = 64,
                Background = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
                    : isSelected
                        ? new SolidColorBrush(defaultAccentColor)
                        : new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2a)),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                ClipToBounds = true,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            var innerBorder = new Border
            {
                Background = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x14, 0x26, 0x1c))
                    : isSelected
                        ? new SolidColorBrush(selInnerNormal)
                        : new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c)),
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(1),
                ClipToBounds = true,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            border.Child = innerBorder;

            var rootGrid = new Grid();

            var bottomGlow = new System.Windows.Shapes.Rectangle
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Width = 140,
                Height = 64,
                Opacity = 0.22,
                IsHitTestVisible = false
            };
            RenderOptions.SetEdgeMode(bottomGlow, EdgeMode.Aliased);
            var bgBrush = new RadialGradientBrush
            {
                ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
                Center = new Point(0.0, 1.0),
                GradientOrigin = new Point(0.0, 1.0),
                RadiusX = 0.6,
                RadiusY = 0.5
            };
            var bottomStop = new GradientStop(accentColor, 0.0);
            bgBrush.GradientStops.Add(bottomStop);
            bgBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0));
            bottomGlow.Fill = bgBrush;
            rootGrid.Children.Add(bottomGlow);

            var topGlow = new System.Windows.Shapes.Rectangle
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Width = 140,
                Height = 64,
                Opacity = 0.12,
                IsHitTestVisible = false
            };
            RenderOptions.SetEdgeMode(topGlow, EdgeMode.Aliased);
            var tgBrush = new RadialGradientBrush
            {
                ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
                Center = new Point(1.0, 0.0),
                GradientOrigin = new Point(1.0, 0.0),
                RadiusX = 0.6,
                RadiusY = 0.5
            };
            var topStop = new GradientStop(accentColor, 0.0);
            tgBrush.GradientStops.Add(topStop);
            tgBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0));
            topGlow.Fill = tgBrush;
            rootGrid.Children.Add(topGlow);

            const double innerRadius = 9;

            var noiseBorder = new Border
            {
                CornerRadius = new CornerRadius(innerRadius),
                Margin = new Thickness(0),
                IsHitTestVisible = false,
                Opacity = 0.04,
                Background = NoiseBrush
            };
            RenderOptions.SetBitmapScalingMode(noiseBorder, BitmapScalingMode.NearestNeighbor);
            rootGrid.Children.Add(noiseBorder);

            rootGrid.SizeChanged += (s, e) =>
            {
                if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    rootGrid.Clip = new RectangleGeometry(
                        new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), innerRadius, innerRadius);
                }
            };

            var contentBorder = new Border
            {
                Padding = new Thickness(14, 11, 14, 11)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var statusStrip = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(accentColor),
                Margin = new Thickness(0, 2, 10, 2)
            };
            Grid.SetColumn(statusStrip, 0);
            grid.Children.Add(statusStrip);

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };

            if (config.IsFromMod)
            {
                nameRow.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xa8, 0x55, 0xf7)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock
                    {
                        Text = "МОД",
                        Foreground = Brushes.White,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold
                    }
                });
            }

            var nameText = new TextBlock
            {
                Text = config.DisplayName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accentColor),
                VerticalAlignment = VerticalAlignment.Center
            };
            nameRow.Children.Add(nameText);

            var activeBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x33, 0x22)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(8, 0, 0, 0),
                Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed,
                Opacity = isCurrent ? 1.0 : 0.0,
                Child = new TextBlock
                {
                    Text = "активный",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
                }
            };
            nameRow.Children.Add(activeBadge);

            var selectedBadge = new Border
            {
                Background = new SolidColorBrush(selBadgeBg),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(8, 0, 0, 0),
                Visibility = !isCurrent && isSelected ? Visibility.Visible : Visibility.Collapsed,
                Opacity = !isCurrent && isSelected ? 1.0 : 0.0,
                Child = new TextBlock
                {
                    Text = "выбран",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(defaultAccentColor)
                }
            };
            nameRow.Children.Add(selectedBadge);

            string details = config.IsFromMod
                ? config.ModName ?? "?"
                : $"Пинг: {config.AveragePing} мс  •  Тесты: {config.SuccessCount}/12" + (config.IsPartiallyUsable ? "  •  частично" : "");

            if (!string.IsNullOrWhiteSpace(config.CustomName) && config.CustomName != config.Name)
            {
                details = $"{config.Name}  •  {details}";
            }

            var infoText = new TextBlock
            {
                Text = details,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x69)),
                Margin = new Thickness(0, 3, 0, 0)
            };

            left.Children.Add(nameRow);
            left.Children.Add(infoText);

            var arrow = new TextBlock
            {
                Text = isCurrent ? "✓" : "→",
                FontSize = 14,
                Foreground = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
                    : isSelected
                        ? new SolidColorBrush(defaultAccentColor)
                        : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x48)),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(left, 1);
            Grid.SetColumn(arrow, 2);
            grid.Children.Add(left);
            grid.Children.Add(arrow);

            contentBorder.Child = grid;
            rootGrid.Children.Add(contentBorder);

            var cardControls = new ConfigCardControls
            {
                ConfigName = config.Name,
                IsCurrent = isCurrent,
                IsSelected = isSelected,
                DefaultAccentColor = defaultAccentColor,
                OuterBorder = border,
                InnerBorder = innerBorder,
                StatusStrip = statusStrip,
                NameText = nameText,
                ActiveBadge = activeBadge,
                SelectedBadge = selectedBadge,
                Arrow = arrow,
                BottomGlow = bottomGlow,
                TopGlow = topGlow,
                BottomGlowStop = bottomStop,
                TopGlowStop = topStop
            };
            _cardControls[config.Name] = cardControls;

            var editOverlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xbb, 0x11, 0x11, 0x13)),
                CornerRadius = new CornerRadius(10),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Opacity = 0
            };
            rootGrid.Children.Add(editOverlay);

            var hoverActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Opacity = 0
            };

            var applyBtn = MakeConfigActionBtn(
                (Geometry)FindResource("CheckmarkIcon"),
                tooltip: "Применить",
                onClick: (_, _) =>
                {
                    _selectedConfig = config.Name;
                    SecondaryBtn_Click(border, new RoutedEventArgs());
                },
                isSuccess: true,
                margin: new Thickness(0));
            hoverActions.Children.Add(applyBtn);

            void ResetHover()
            {
                if (border.IsMouseOver || _activeDialogConfigName == config.Name) return;

                if (cardControls.IsCurrent)
                    innerBorder.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x26, 0x1c));
                else if (cardControls.IsSelected)
                    innerBorder.Background = new SolidColorBrush(selInnerNormal);
                else
                    innerBorder.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c));

                hoverActions.IsHitTestVisible = false;

                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                fadeOut.Completed += (_, _) =>
                {
                    if (!border.IsMouseOver && _activeDialogConfigName != config.Name)
                    {
                        editOverlay.Visibility = Visibility.Collapsed;
                        hoverActions.Visibility = Visibility.Collapsed;
                    }
                };
                editOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
                hoverActions.BeginAnimation(UIElement.OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);

                var glowOutBottom = new DoubleAnimation(0.22, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                bottomGlow.BeginAnimation(UIElement.OpacityProperty, glowOutBottom, HandoffBehavior.SnapshotAndReplace);

                var glowOutTop = new DoubleAnimation(0.12, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                topGlow.BeginAnimation(UIElement.OpacityProperty, glowOutTop, HandoffBehavior.SnapshotAndReplace);
            }

            var editBtn = MakeConfigActionBtn(
                (Geometry)FindResource("PencilIcon"),
                tooltip: "Переименовать",
                onClick: (_, _) =>
                {
                    _activeDialogConfigName = config.Name;
                    ShowConfigEditDialog(config, onClosed: () =>
                    {
                        _activeDialogConfigName = null;
                        ResetHover();
                    });
                },
                margin: new Thickness(10, 0, 0, 0));
            hoverActions.Children.Add(editBtn);

            var favBtn = MakeConfigStarBtn(config, margin: new Thickness(10, 0, 0, 0));
            hoverActions.Children.Add(favBtn);

            if (!config.IsFromMod)
            {
                var deleteBtn = MakeConfigActionBtn(
                    (Geometry)FindResource("TrashIcon"),
                    tooltip: "Удалить",
                    onClick: (_, _) =>
                    {
                        _activeDialogConfigName = config.Name;
                        ShowConfigConfirmDelete(config.Name, () =>
                        {
                            _activeDialogConfigName = null;
                            _cache.ValidConfigs.RemoveAll(c => c.Name == config.Name);
                            _cache.PartialConfigs.RemoveAll(c => c.Name == config.Name);
                            _cache.FavoriteConfigs.Remove(config.Name);
                            if (_cache.CurrentConfig == config.Name)
                                _cache.CurrentConfig = _cache.GetSelectableConfigs().FirstOrDefault(c => c.Name != config.Name)?.Name ?? "";
                            if (_selectedConfig == config.Name)
                                _selectedConfig = _cache.CurrentConfig;
                            ZapretConfigService.SaveCache(_cache);
                            ShowConfigList(animateEntrance: false);
                        },
                        onClosed: () =>
                        {
                            _activeDialogConfigName = null;
                            ResetHover();
                        });
                    },
                    isDestructive: true,
                    margin: new Thickness(10, 0, 0, 0));
                hoverActions.Children.Add(deleteBtn);
            }

            rootGrid.Children.Add(hoverActions);
            innerBorder.Child = rootGrid;

            border.MouseEnter += (s, e) =>
            {
                if (cardControls.IsCurrent)
                    innerBorder.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x2d, 0x21));
                else if (cardControls.IsSelected)
                    innerBorder.Background = new SolidColorBrush(selInnerHover);
                else
                    innerBorder.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));

                editOverlay.Visibility = Visibility.Visible;
                hoverActions.Visibility = Visibility.Visible;
                hoverActions.IsHitTestVisible = true;

                var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                editOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);
                hoverActions.BeginAnimation(UIElement.OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);

                var glowInBottom = new DoubleAnimation(0.42, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                bottomGlow.BeginAnimation(UIElement.OpacityProperty, glowInBottom, HandoffBehavior.SnapshotAndReplace);

                var glowInTop = new DoubleAnimation(0.26, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                topGlow.BeginAnimation(UIElement.OpacityProperty, glowInTop, HandoffBehavior.SnapshotAndReplace);
            };

            border.MouseLeave += (s, e) =>
            {
                ResetHover();
            };

            void SetSelected(bool selected)
            {
                cardControls.IsSelected = selected;
                isSelected = selected;
                if (!cardControls.IsCurrent)
                {
                    border.Background = selected
                        ? new SolidColorBrush(defaultAccentColor)
                        : new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2a));

                    if (border.IsMouseOver)
                    {
                        innerBorder.Background = selected
                            ? new SolidColorBrush(selInnerHover)
                            : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
                    }
                    else
                    {
                        innerBorder.Background = selected
                            ? new SolidColorBrush(selInnerNormal)
                            : new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c));
                    }

                    selectedBadge.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                    selectedBadge.Opacity = selected ? 1.0 : 0.0;

                    arrow.Foreground = selected
                        ? new SolidColorBrush(defaultAccentColor)
                        : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x48));
                }
            }

            cardUpdaters[config.Name] = SetSelected;

            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1)
                {
                    if (_selectedConfig != config.Name)
                    {
                        var oldSelected = _selectedConfig;
                        _selectedConfig = config.Name;
                        if (oldSelected != null && cardUpdaters.TryGetValue(oldSelected, out var updateOld))
                        {
                            updateOld(false);
                        }
                        SetSelected(true);
                    }
                }
                else if (e.ClickCount == 2)
                {
                    _selectedConfig = config.Name;
                    SecondaryBtn_Click(s, e);
                }
            };

            if (animateEntrance)
            {
                border.Opacity = 0;
                int delayMs = 130 + Math.Min(cardIndex, 14) * 65;
                var cardFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(500))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                    EasingFunction = easeCubicOut
                };
                border.BeginAnimation(UIElement.OpacityProperty, cardFade);
            }

            cardIndex++;
            ConfigListPanel.Children.Add(border);
        }

        ConfigListScroll.Opacity = 1.0;

        SecondaryBtn.Content = "Применить";
        PrimaryBtn.Content = "Проверить конфиг";
        PrimaryBtn.Visibility = Visibility.Visible;
    }

    private System.Windows.Controls.Button MakeConfigActionBtn(
        Geometry geometry, string tooltip, RoutedEventHandler onClick, bool isDestructive = false, bool isSuccess = false, Thickness? margin = null)
    {
        double iconSize = isSuccess ? 17.5 : 16;
        double strokeThick = isSuccess ? 2.3 : 1.8;

        var normalBrush = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc));
        var greenBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        var redBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Stroke = isDestructive ? redBrush : normalBrush,
            StrokeThickness = strokeThick,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        string styleKey = isDestructive
            ? "HoverActionDestructiveBtn"
            : (isSuccess ? "HoverActionSuccessBtn" : "HoverActionBtn");

        var btn = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource(styleKey),
            ToolTip = tooltip,
            Content = path,
            Margin = margin ?? new Thickness(10, 0, 0, 0)
        };

        if (isSuccess)
        {
            btn.MouseEnter += (_, _) => path.Stroke = greenBrush;
            btn.MouseLeave += (_, _) => path.Stroke = normalBrush;
        }

        btn.Click += (s, e) =>
        {
            e.Handled = true;
            onClick(s, e);
        };

        return btn;
    }

    private System.Windows.Controls.Button MakeConfigStarBtn(
        ZapretConfig config, Thickness margin)
    {
        var starPath = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("StarIcon"),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };

        void UpdateStarVisual(bool isFav)
        {
            if (isFav)
            {
                starPath.Fill = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24));
                starPath.Stroke = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24));
            }
            else
            {
                starPath.Fill = Brushes.Transparent;
                starPath.Stroke = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc));
            }
        }

        bool isFavorite = _cache?.FavoriteConfigs?.Contains(config.Name) == true;
        UpdateStarVisual(isFavorite);

        var btn = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("HoverActionBtn"),
            ToolTip = isFavorite ? "Убрать из избранного" : "В избранное",
            Content = starPath,
            Margin = margin
        };

        btn.Click += (s, e) =>
        {
            e.Handled = true;
            if (_cache == null) return;
            bool wasFav = _cache.FavoriteConfigs.Contains(config.Name);
            if (wasFav)
                _cache.FavoriteConfigs.Remove(config.Name);
            else
                _cache.FavoriteConfigs.Add(config.Name);

            ZapretConfigService.SaveCache(_cache);
            ShowConfigList(animateEntrance: false);
            if (!wasFav)
            {
                ConfigListScroll.ScrollToTop();
            }
        };

        return btn;
    }

    private void ShowConfigEditDialog(ZapretConfig config, Action? onClosed = null)
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

        var dialog = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Width = 400,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        var stack = new StackPanel();

        var titleBlock = new TextBlock
        {
            Text = "Редактировать конфиг",
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        stack.Children.Add(titleBlock);

        var messageBlock = new TextBlock
        {
            Text = $"Изменение локального названия для \u00ab{config.Name}\u00bb.",
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        stack.Children.Add(messageBlock);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = config.DisplayName,
            FontSize = 13,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Margin = new Thickness(0, 0, 0, 20)
        };

        var tbTpl = new ControlTemplate(typeof(System.Windows.Controls.TextBox));
        var tbFac = new FrameworkElementFactory(typeof(Border));
        tbFac.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.TextBox.BackgroundProperty));
        tbFac.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.TextBox.BorderBrushProperty));
        tbFac.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.TextBox.BorderThicknessProperty));
        tbFac.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var scrollViewerFac = new FrameworkElementFactory(typeof(ScrollViewer));
        scrollViewerFac.Name = "PART_ContentHost";
        scrollViewerFac.SetValue(ScrollViewer.MarginProperty, new Thickness(0));
        tbFac.AppendChild(scrollViewerFac);
        tbTpl.VisualTree = tbFac;
        textBox.Template = tbTpl;

        stack.Children.Add(textBox);

        var btnPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        void CloseDialog()
        {
            RootGrid.Children.Remove(overlay);
            onClosed?.Invoke();
        }

        void SaveAndClose()
        {
            var newName = textBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName) || newName == config.Name)
            {
                config.CustomName = null;
            }
            else
            {
                config.CustomName = newName;
            }

            if (_cache is not null)
                ZapretConfigService.SaveCache(_cache);
            CloseDialog();
            ShowConfigList(animateEntrance: false);
        }

        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                SaveAndClose();
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                CloseDialog();
            }
        };

        if (!string.IsNullOrWhiteSpace(config.CustomName))
        {
            var resetBtn = new System.Windows.Controls.Button
            {
                Content = "Сбросить",
                Style = (Style)FindResource("OutlineBtn"),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Сбросить к исходному имени файла"
            };
            resetBtn.Click += (_, _) =>
            {
                config.CustomName = null;
                if (_cache is not null)
                    ZapretConfigService.SaveCache(_cache);
                CloseDialog();
                ShowConfigList(animateEntrance: false);
            };
            btnPanel.Children.Add(resetBtn);
        }

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Отмена",
            Style = (Style)FindResource("OutlineBtn"),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => CloseDialog();

        var saveBg = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
        var saveBgHover = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xeb));
        var saveBtn = new System.Windows.Controls.Button
        {
            Content = "Сохранить",
            Padding = new Thickness(16, 8, 16, 8),
            Background = saveBg,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
        };
        var btnTpl = new ControlTemplate(typeof(System.Windows.Controls.Button));
        var btnFac = new FrameworkElementFactory(typeof(Border));
        btnFac.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Button.BackgroundProperty));
        btnFac.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        btnFac.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Button.PaddingProperty));
        var btnPres = new FrameworkElementFactory(typeof(ContentPresenter));
        btnPres.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        btnPres.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        btnFac.AppendChild(btnPres);
        btnTpl.VisualTree = btnFac;
        saveBtn.Template = btnTpl;
        saveBtn.MouseEnter += (_, _) => saveBtn.Background = saveBgHover;
        saveBtn.MouseLeave += (_, _) => saveBtn.Background = saveBg;
        saveBtn.Click += (_, _) => SaveAndClose();

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(saveBtn);
        stack.Children.Add(btnPanel);
        dialog.Child = stack;
        overlay.Child = dialog;

        overlay.MouseLeftButtonDown += (_, e) => { if (e.Source == overlay) CloseDialog(); };

        RootGrid.Children.Add(overlay);
        Grid.SetRowSpan(overlay, 4);

        textBox.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.Select(textBox.Text.Length, 0);
        };
        textBox.GotKeyboardFocus += (_, _) =>
        {
            textBox.Select(textBox.Text.Length, 0);
        };
    }

    private void ShowConfigConfirmDelete(string configName, Action onConfirmed, Action? onClosed = null)
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

        var dialog = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Width = 400,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        var stack = new StackPanel();

        var titleBlock = new TextBlock
        {
            Text = "Удалить конфиг?",
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        stack.Children.Add(titleBlock);

        var messageBlock = new TextBlock
        {
            Text = $"Конфиг \u00ab{configName}\u00bb будет удалён из списка. Вернуть можно только повторным тестированием.",
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20)
        };
        stack.Children.Add(messageBlock);

        var btnPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        void CloseDialog()
        {
            RootGrid.Children.Remove(overlay);
            onClosed?.Invoke();
        }

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Отмена",
            Style = (Style)FindResource("OutlineBtn"),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => CloseDialog();

        var confirmBg = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        var confirmBgHover = new SolidColorBrush(Color.FromRgb(0xdc, 0x26, 0x26));
        var confirmBtn = new System.Windows.Controls.Button
        {
            Content = "Удалить",
            Padding = new Thickness(16, 8, 16, 8),
            Background = confirmBg,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
        };
        var btnTpl = new ControlTemplate(typeof(System.Windows.Controls.Button));
        var btnFac = new FrameworkElementFactory(typeof(Border));
        btnFac.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Button.BackgroundProperty));
        btnFac.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        btnFac.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Button.PaddingProperty));
        var btnPres = new FrameworkElementFactory(typeof(ContentPresenter));
        btnPres.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        btnPres.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        btnFac.AppendChild(btnPres);
        btnTpl.VisualTree = btnFac;
        confirmBtn.Template = btnTpl;
        confirmBtn.MouseEnter += (_, _) => confirmBtn.Background = confirmBgHover;
        confirmBtn.MouseLeave += (_, _) => confirmBtn.Background = confirmBg;
        confirmBtn.Click += (_, _) => { CloseDialog(); onConfirmed(); };

        overlay.MouseLeftButtonDown += (_, e) => { if (e.Source == overlay) CloseDialog(); };

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(confirmBtn);
        stack.Children.Add(btnPanel);
        dialog.Child = stack;
        overlay.Child = dialog;

        RootGrid.Children.Add(overlay);
        Grid.SetRowSpan(overlay, 4);
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
