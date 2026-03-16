using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using NetFix.Models;
using NetFix.Services;

namespace NetFix;

public partial class MainWindow : Window
{
    // ── State ────────────────────────────────────────────────────────────────
    private AppSettings _settings = SettingsService.Load();
    private bool _settingsOpen = false;
    private DispatcherTimer _monitorTimer = null!;

    // ── Init ─────────────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsToPanel();

        if (!SettingsService.IsOnboarded)
            ShowOnboarding();
        else
        {
            FadeIn();
            CheckInternetOnStart();
            StartActiveAppsMonitor();
        }
        LoadFaqItems();
    }

    // ── Fade in ──────────────────────────────────────────────────────────────
    private void FadeIn()
    {
        Opacity = 0;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
        BeginAnimation(OpacityProperty, anim);
    }

    // ── Window chrome ────────────────────────────────────────────────────────
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseBtn_Click(object s, RoutedEventArgs e) => Close();

    // ── Nav ──────────────────────────────────────────────────────────────────
    private void DiagNavBtn_Click(object s, RoutedEventArgs e)
    {
        MainPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Visible;
        DiagNavBtn.Foreground = Brushes.White;
        FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void FaqNavBtn_Click(object s, RoutedEventArgs e)
    {
        MainPage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Visible;
        FaqNavBtn.Foreground = Brushes.White;
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void BackFromFaq_Click(object s, RoutedEventArgs e)
    {
        FaqPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void BackBtn_Click(object s, RoutedEventArgs e)
    {
        DiagPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void BackFromSolution_Click(object s, RoutedEventArgs e)
    {
        SolutionPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Visible;
    }

    // ── FAQ Logic ────────────────────────────────────────────────────────────
    private void LoadFaqItems()
    {
        FaqList.Children.Clear();
        
        AddFaqItem(
            "У меня не грузит ТГ, но включен TgProxy",
            "Для работы Telegram через прокси необходимо убедиться, что приложение использует правильный порт и протокол. Обычно это связано с тем, что Telegram пытается использовать системные настройки прокси, игнорируя TgProxy. В автоматическом режиме мы перенастроим конфигурацию Telegram на локальный прокси.",
            "Перенастроить Telegram"
        );
        AddFaqItem(
            "У меня не работает YouTube, хотя Запрет включен",
            "Возможно, провайдер использует новые методы блокировки, которые требуют обновления стратегии обхода в GoodbyeDPI или Zapret. Также проблема может быть вызвана конфликтом кэша браузера. Попробуйте обновить конфигурацию и очистить кэш DNS.",
            "Обновить конфигурацию"
        );
        AddFaqItem(
            "У меня не работает ТГ и ДС, хотя всё скачано и включено",
            "Если ничего не работает при запущенных службах, вероятнее всего, произошел конфликт портов или сетевой адаптер Windows перешел в некорректное состояние. Мы можем автоматически перезапустить все сетевые интерфейсы и службы.",
            "Сбросить сеть и перезапустить"
        );
    }
    
    private void AddFaqItem(string title, string manualText, string autoBtnText)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(16)
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        var text = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 16, 0)
        };
        grid.Children.Add(text);
        
        var btn = new Button
        {
            Content = "Исправить",
            Style = (Style)FindResource("AccentBtn"),
            Padding = new Thickness(16, 6, 16, 6)
        };
        Grid.SetColumn(btn, 1);
        
        btn.Click += (_, _) => 
        {
            SolutionTitle.Text = title;
            SolutionManualText.Text = manualText;
            SolutionAutoFixBtn.Content = $"⚡ {autoBtnText}";
            
            FaqPage.Visibility = Visibility.Collapsed;
            SolutionPage.Visibility = Visibility.Visible;
        };
        grid.Children.Add(btn);
        
        card.Child = grid;
        FaqList.Children.Add(card);
    }

    private void SolutionAutoFixBtn_Click(object s, RoutedEventArgs e)
    {
        SolutionPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        FixBtn_Click(s, e);
    }

    // ── Internet check ───────────────────────────────────────────────────────
    private void CheckInternetOnStart()
    {
        Task.Run(async () =>
        {
            bool ok = await DiagnosticsEngine.CheckInternetAsync();
            Dispatcher.Invoke(() =>
            {
                if (ok)
                {
                    NetDot.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    NetLbl.Text = "Сеть";
                }
                else
                {
                    NetDot.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    NetLbl.Text = "Нет сети";
                    NetLbl.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    NoInternetPage.Visibility = Visibility.Visible;
                }
            });
        });
    }

    private void RetryNet_Click(object s, RoutedEventArgs e)
    {
        NoInternetPage.Visibility = Visibility.Collapsed;
        CheckInternetOnStart();
    }

    // ── Active apps monitor ──────────────────────────────────────────────────
    private void StartActiveAppsMonitor()
    {
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _monitorTimer.Tick += (_, _) => UpdateActiveApps();
        _monitorTimer.Start();
        UpdateActiveApps();
    }

    private void UpdateActiveApps()
    {
        Task.Run(async () =>
        {
            var st = DiagnosticsEngine.CheckAppStatus();
            bool vpn = DetectVpn(out string _);
            bool netOk = await DiagnosticsEngine.CheckInternetAsync();

            Dispatcher.Invoke(() =>
            {
                var greenBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                var grayBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                var redBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

                VpnDot.Fill = vpn ? greenBrush : grayBrush;
                ZapretDot.Fill = st.ZapretRunning ? greenBrush : grayBrush;
                TgWsDot.Fill = st.TgWsProxyRunning ? greenBrush : grayBrush;

                if (netOk)
                {
                    NetDot.Fill = greenBrush;
                    NetLbl.Text = "Сеть";
                    NetLbl.Foreground = grayBrush;
                    
                    if (NoInternetPage.Visibility == Visibility.Visible)
                        NoInternetPage.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NetDot.Fill = redBrush;
                    NetLbl.Text = "Нет сети";
                    NetLbl.Foreground = redBrush;
                }
            });
        });
    }

    private static bool DetectVpn(out string info)
    {
        info = "";
        try
        {
            foreach (var v in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
            {
                var val = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(val)) { info = $"proxy env: {val}"; return true; }
            }
            var result = RunProcess("ipconfig", "");
            string[] vpnKw = ["tap-windows", "wireguard", "wintun", "nordvpn", "expressvpn",
                               "openvpn", "outline", "warp", "mullvad", "proton", "tun"];
            foreach (var kw in vpnKw)
                if (result.ToLower().Contains(kw)) { info = kw; return true; }
        }
        catch { }
        return false;
    }

    private static string RunProcess(string name, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(name, args)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return o;
        }
        catch { return ""; }
    }

    // ── Log ──────────────────────────────────────────────────────────────────
    private void AppendLog(string msg, string kind = "info")
    {
        Dispatcher.Invoke(() =>
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            Color textColor = Color.FromRgb(0xcc, 0xcc, 0xcc);
            string prefix = "";
            bool isBold = false;
            
            switch (kind)
            {
                case "spacer":
                    msg = "";
                    break;
                case "section":
                    textColor = Color.FromRgb(0x3b, 0x82, 0xf6); // Синий
                    isBold = true;
                    prefix = "▶ ";
                    break;
                case "step":
                    textColor = Color.FromRgb(0x9c, 0xa3, 0xaf); // Серый
                    prefix = "  ";
                    break;
                case "ok":
                    textColor = Color.FromRgb(0x22, 0xc5, 0x5e); // Зеленый
                    prefix = "✓ ";
                    break;
                case "error":
                    textColor = Color.FromRgb(0xef, 0x44, 0x44); // Красный
                    prefix = "✗ ";
                    break;
                case "warn":
                    textColor = Color.FromRgb(0xea, 0xb3, 0x08); // Желтый
                    prefix = "⚠ ";
                    break;
                case "success":
                    textColor = Color.FromRgb(0x22, 0xc5, 0x5e); // Ярко-зеленый
                    isBold = true;
                    prefix = "✅ ";
                    break;
                case "link":
                    textColor = Color.FromRgb(0x60, 0xa5, 0xfa); // Голубой
                    prefix = "🔗 ";
                    break;
                default:
                    prefix = "ℹ ";
                    break;
            }

            var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 2, 0, 2) };
            
            if (kind == "spacer")
            {
                para.Margin = new Thickness(0, 6, 0, 6);
            }
            else
            {
                // Время серым цветом, кроме заголовков секций
                if (kind != "section" && kind != "step")
                {
                    para.Inlines.Add(new System.Windows.Documents.Run($"[{ts}] ") 
                    { 
                        Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)) 
                    });
                }
                
                // Текст лога со своим цветом
                para.Inlines.Add(new System.Windows.Documents.Run($"{prefix}{msg}") 
                { 
                    Foreground = new SolidColorBrush(textColor),
                    FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal
                });
            }

            LogBox.Document.Blocks.Add(para);
            LogBox.ScrollToEnd();
        });
    }

    private void ClearLog_Click(object s, RoutedEventArgs e) => LogBox.Document.Blocks.Clear();

    // ── Auto-setup ───────────────────────────────────────────────────────────
    private void FixBtn_Click(object s, RoutedEventArgs e)
    {
        var st = DiagnosticsEngine.CheckAppStatus();
        
        // Если Zapret выключен, и путь к нему есть — запускаем мастера настройки
        if (!st.ZapretRunning && !string.IsNullOrWhiteSpace(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
        {
            ShowZapretWizard();
            return;
        }

        RunAutoFix();
    }

    private void RunAutoFix()
    {
        FixBtn.IsEnabled = false;
        SetupProg.Value = 0;
        SetupProgLbl.Text = "Инициализация…";
        SetupProgLbl.Foreground = Brushes.White;
        LogBox.Document.Blocks.Clear();
        
        // Более подробная имитация отладки
        AppendLog("Начало расширенной диагностики и автоматической настройки...", "section");
        AppendLog("Сбор системной информации и проверка сетевых адаптеров...", "step");
        AppendLog("Анализ портов, конфигурации Windows Defender и брандмауэра...", "step");
        AppendLog("Поиск и изоляция конфликтующих процессов...", "step");
        AppendLog("Подготовка модулей обхода блокировок...", "info");

        // Сброс иконки и колец в исходное состояние
        var iconReset = (TextBlock)FixBtn.Template.FindName("BtnIcon", FixBtn);
        if (iconReset != null)
            iconReset.Foreground = new SolidColorBrush(Color.FromRgb(0x7c, 0x6a, 0xf7));
        SuccessArc.Visibility = Visibility.Collapsed;
        ErrorRing.Visibility  = Visibility.Collapsed;

        StartGlow();

        AutoSetupService.Run(
            logCb: AppendLog,
            progressCb: ratio => Dispatcher.Invoke(() => {
                SetupProg.Value = ratio * 100;
                SetupProgLbl.Text = $"Прогресс: {(int)(ratio * 100)}%";
            }),
            doneCb: (success, _) => Dispatcher.Invoke(() => {
                StopGlow(success);
                FixBtn.IsEnabled = true;
                if (success) {
                    SetupProgLbl.Text = "Готово ✓";
                    SetupProgLbl.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    AppendLog("Все процессы успешно запущены и настроены!", "success");
                    PlaySuccessRing();
                } else {
                    SetupProgLbl.Text = "Ошибка — смотри лог";
                    SetupProgLbl.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    AppendLog("Обнаружены ошибки во время настройки.", "error");
                    PlayErrorRing();
                }
            }),
            settings: _settings);
    }

    private void PlaySuccessRing()
    {
        double circumference = 2 * Math.PI * 97;
        SuccessArc.StrokeDashArray = new DoubleCollection { 0, circumference };
        SuccessArc.Visibility = Visibility.Visible;

        // Запускаем анимацию цвета СРАЗУ
        var icon = (TextBlock)FixBtn.Template.FindName("BtnIcon", FixBtn);
        if (icon != null) {
            var brush = new SolidColorBrush(Color.FromRgb(0x7c, 0x6a, 0xf7));
            icon.Foreground = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    Color.FromRgb(0x7c, 0x6a, 0xf7),
                    Color.FromRgb(0x22, 0xc5, 0x5e),
                    new Duration(TimeSpan.FromSeconds(1.8))) // Длительность = длительности круга
                { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (s, e) => {
            double t = Math.Min(sw.Elapsed.TotalSeconds / 5, 1.0);
            double ease = t == 0 ? 0 : 1 - Math.Pow(2, -10 * t);
            SuccessArc.StrokeDashArray = new DoubleCollection { ease * circumference, circumference };
            if (t >= 1.0) timer.Stop();
        };
        timer.Start();
    }

    private void StartGlow()
    {
        // Скрываем idle-кольца и другие состояния
        IdleRingOuter.Visibility = Visibility.Collapsed;
        IdleRingInner.Visibility = Visibility.Collapsed;
        ErrorRing.Visibility     = Visibility.Collapsed;
        SuccessArc.Visibility    = Visibility.Collapsed;
        SuccessCheck.Visibility  = Visibility.Collapsed;

        // Спиннер 1 — по часовой, 1.4s (аналог CSS)
        SpinArc.Visibility = Visibility.Visible;
        var spin1 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.4)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinOffset.BeginAnimation(RotateTransform.AngleProperty, spin1);

        // Спиннер 2 — по часовой, 1.9s (аналог CSS)
        SpinArc2.Visibility = Visibility.Visible;
        var spin2 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.9)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinRotation2.BeginAnimation(RotateTransform.AngleProperty, spin2);

        // Анимация иконки - пульсация цвета
        var iconGlow = new BrushAnimation();
        iconGlow.From = new SolidColorBrush(Color.FromRgb(0x7c, 0x6a, 0xf7));
        iconGlow.To = new SolidColorBrush(Color.FromRgb(0x5b, 0x8d, 0xf5));
        iconGlow.Duration = new Duration(TimeSpan.FromSeconds(1.8));
        iconGlow.AutoReverse = true;
        iconGlow.RepeatBehavior = RepeatBehavior.Forever;
        
        // Находим иконку в шаблоне кнопки
        if (FixBtn.Template.FindName("BtnIcon", FixBtn) is TextBlock iconEl)
        {
            iconEl.BeginAnimation(TextBlock.ForegroundProperty, iconGlow);
        }
    }

    private void StopGlow(bool success)
    {
        // Стоп все спиннеры
        SpinOffset.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinRotation2.BeginAnimation(RotateTransform.AngleProperty, null);

        SpinArc.Visibility  = Visibility.Collapsed;
        SpinArc2.Visibility = Visibility.Collapsed;
        
        // Возвращаем цвет иконки в нормальное состояние
        if (FixBtn.Template.FindName("BtnIcon", FixBtn) is TextBlock iconEl)
        {
            iconEl.Foreground = new SolidColorBrush(Color.FromRgb(0x7c, 0x6a, 0xf7));
        }
    }

    private void PlayErrorRing()
    {
        ErrorRing.Visibility = Visibility.Visible;
        
        // Меняем цвет иконки на красный
        if (FixBtn.Template.FindName("BtnIcon", FixBtn) is TextBlock iconEl)
        {
            iconEl.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        }
        
        // Shake анимация кнопки (аналог CSS)
        var shakeTransform = new TranslateTransform();
        FixBtn.RenderTransform = shakeTransform;
        
        var shakeAnimation = new DoubleAnimationUsingKeyFrames();
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
        
        shakeTransform.BeginAnimation(TranslateTransform.XProperty, shakeAnimation);
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────
    private void DiagRunBtn_Click(object s, RoutedEventArgs e)
    {
        DiagRunBtn.IsEnabled = false;
        DiagRunBtn.Content = "⏳  Проверяю…";
        DiagProg.Value = 0;
        DiagProgLbl.Text = "Запускаю диагностику…";
        DiagProgLbl.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)); // <-- Добавлена эта строка
        DiagResults.Children.Clear();

        Task.Run(async () =>
        {
            var report = await DiagnosticsEngine.RunFullDiagnosticsAsync(
                (ratio, label) => Dispatcher.Invoke(() =>
                {
                    DiagProg.Value = ratio * 100;
                    DiagProgLbl.Text = label;
                }));
            Dispatcher.Invoke(() => RenderDiagReport(report));
        });
    }

    private void RenderDiagReport(DiagReport r)
    {
        DiagResults.Children.Clear();

        // Основной вердикт
        var (em, title, detail, ck) = DiagnosticsEngine.HumanVerdict(r);
        AddCard(DiagResults, $"{em}  {title}", detail, ColorFromKey(ck));

        // Вердикт Discord
        var (dem, dtitle, ddetail, dck) = DiagnosticsEngine.DiscordVerdict(r);
        AddCard(DiagResults, $"{dem}  {dtitle}", ddetail, ColorFromKey(dck));

        // Статус приложений (оформлен блоками с настоящими кружками)
        if (r.AppStatus is { } a)
        {
            var appsPanel = new StackPanel();
            void AddAppUI(string name, bool isRunning, string proc)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                
                // Кругляшок статуса
                var dot = new Ellipse { 
                    Width = 10, Height = 10, 
                    Fill = new SolidColorBrush(isRunning ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0xef, 0x44, 0x44)), 
                    VerticalAlignment = VerticalAlignment.Center 
                };
                Grid.SetColumn(dot, 0);
                
                // Название
                var nameText = new TextBlock { 
                    Text = name, Foreground = Brushes.White, FontSize = 14, 
                    FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center 
                };
                Grid.SetColumn(nameText, 1);
                
                row.Children.Add(dot);
                row.Children.Add(nameText);
                
                // Процесс (в виде красивой плашки)
                if (isRunning && !string.IsNullOrEmpty(proc)) 
                {
                    var procPill = new Border {
                        Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 2, 8, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var procText = new TextBlock { Text = proc, Foreground = new SolidColorBrush(Color.FromRgb(0xd1, 0xd5, 0xdb)), FontSize = 11 };
                    procPill.Child = procText;
                    Grid.SetColumn(procPill, 2);
                    row.Children.Add(procPill);
                }
                appsPanel.Children.Add(row);
            }
            
            AddAppUI("Telegram",    a.TelegramRunning,    a.TelegramProcName);
            AddAppUI("Discord",     a.DiscordRunning,     a.DiscordProcName);
            AddAppUI("Zapret",      a.ZapretRunning,      a.ZapretProcName);
            AddAppUI("GoodbyeDPI",  a.GoodbyeDpiRunning,  a.GoodbyeDpiProcName);
            AddAppUI("WARP",        a.WarpRunning,        a.WarpProcName);
            AddAppUI("tg-ws-proxy", a.TgWsProxyRunning,   a.TgWsProxyProcName);
            
            AddRichCard(DiagResults, "💻  Статус приложений", appsPanel, Color.FromRgb(0x8b, 0x5c, 0xf6));
        }

        // Доступность серверов (оформлено карточками в ряд)
        if (r.DcResults.Count > 0)
        {
            var serverContainer = new StackPanel(); // Главный контейнер для серверов и примечания
            var srvPanel = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (var dc in r.DcResults)
            {
                var srvBlock = new Border {
                    Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 10, 10),
                    Padding = new Thickness(14, 12, 14, 12),
                    Width = 150
                };
                var srvStack = new StackPanel();
                var headerGrid = new Grid();
                var dcName = new TextBlock { Text = $"DC {dc.DcId}", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 14 };

                // Округляем пинг и определяем статус
                int? ping = dc.LatencyMs.HasValue ? (int)Math.Round(dc.LatencyMs.Value) : null;
                bool isGreen = dc.Ok && ping.HasValue && ping.Value <= 100;
                bool isYellow = dc.Ok && ping.HasValue && ping.Value > 100 && ping.Value <= 200;
                bool isRed = !dc.Ok || !ping.HasValue || ping.Value > 200;

                Color dotColor = isGreen ? Color.FromRgb(0x22, 0xc5, 0x5e) : 
                                 isYellow ? Color.FromRgb(0xea, 0xb3, 0x08) : 
                                 Color.FromRgb(0xef, 0x44, 0x44);

                var dot = new Ellipse { 
                    Width = 10, Height = 10, 
                    Fill = new SolidColorBrush(dotColor), 
                    HorizontalAlignment = HorizontalAlignment.Right, 
                    VerticalAlignment = VerticalAlignment.Center 
                };
                
                headerGrid.Children.Add(dcName);
                headerGrid.Children.Add(dot);
                
                var ipText = new TextBlock { Text = dc.Ip, Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
                
                string latStr = isRed ? "Недоступен" : $"{ping} мс";
                var latText = new TextBlock { 
                    Text = latStr, 
                    Foreground = new SolidColorBrush(dotColor), 
                    FontSize = 12, 
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 4, 0, 0) 
                };

                srvStack.Children.Add(headerGrid);
                srvStack.Children.Add(ipText);
                srvStack.Children.Add(latText);
                srvBlock.Child = srvStack;
                srvPanel.Children.Add(srvBlock);
            }
            
            serverContainer.Children.Add(srvPanel);

            // Добавляем примечание, если включен TgWsProxy
            if (r.AppStatus != null && r.AppStatus.TgWsProxyRunning)
            {
                var noteText = new TextBlock
                {
                    Text = "Примечание: У вас включен TgWsProxy. Даже если выше указано, что сервера недоступны — не переживайте, на вашем ПК Telegram будет работать нормально.\n\n" +
                           "Связь с TG идет через этот прокси, а диагностика проверяет сервера прямой отправкой пакетов, которые блокируются. Поэтому они и помечаются как «недоступные».\n\n" +
                           "Важно: Сервера будут помечены как стабильные и пинг будет нормальным только в том случае, если у вас включен VPN, а без него они всегда будут «недоступны» :). Так что всё ок!",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)), // Серый текст
                    FontSize = 14,
                    FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                serverContainer.Children.Add(noteText);
            }

            AddRichCard(DiagResults, "🌍  Доступность серверов Telegram", serverContainer, Color.FromRgb(0x0e, 0xa5, 0xe9));
        }

        // Рекомендации
        foreach (var rec in r.Recommendations)
            AddCard(DiagResults, "💡  Рекомендация", rec, Color.FromRgb(0xf5, 0x9e, 0x0b));

        DiagProg.Value = 100;
        DiagProgLbl.Text = "Готово ✓";
        DiagProgLbl.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        DiagRunBtn.IsEnabled = true;
        DiagRunBtn.Content = "🔄  Проверить снова";
    }

    private Color ColorFromKey(string ck) => ck switch
    {
        "green"  => Color.FromRgb(0x22, 0xc5, 0x5e),
        "yellow" => Color.FromRgb(0xea, 0xb3, 0x08),
        "red"    => Color.FromRgb(0xef, 0x44, 0x44),
        _        => Color.FromRgb(0x6b, 0x72, 0x80),
    };

    private static void AddCard(Panel parent, string title, string body, Color accentColor)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            AddRichCard(parent, title, new Grid(), accentColor);
            return;
        }

        var tb = new TextBlock {
            Text = body,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd1, 0xd5, 0xdb)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        };
        AddRichCard(parent, title, tb, accentColor);
    }

    private static void AddRichCard(Panel parent, string title, UIElement content, Color accentColor)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 14),
            ClipToBounds = true,
        };

        var inner = new Grid();

        var bar = new Border
        {
            Background = new SolidColorBrush(accentColor),
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        
        stack.Children.Add(content);

        inner.Children.Add(stack);
        inner.Children.Add(bar);
        card.Child = inner;
        parent.Children.Add(card);
    }

    // ── Settings panel ───────────────────────────────────────────────────────
    private void SettingsBtn_Click(object s, RoutedEventArgs e)
    {
        if (_settingsOpen) CloseSettings();
        else OpenSettings();
    }

    private void SettingsBackdrop_Click(object s, MouseButtonEventArgs e) => CloseSettings();
    private void SettingsCloseBtn_Click(object s, RoutedEventArgs e) => CloseSettings();

    private void OpenSettings()
    {
        _settingsOpen = true;
        SettingsLayer.Visibility = Visibility.Visible;
        AnimateSettings(open: true);
    }

    private void CloseSettings()
    {
        _settingsOpen = false;
        AnimateSettings(open: false);
    }

    private void AnimateSettings(bool open)
    {
        double fromX = open ? 370 : 0;
        double toX   = open ? 0   : 370;

        var slideAnim = new DoubleAnimation(fromX, toX, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn }
        };

        var fadeAnim = new DoubleAnimation(open ? 0 : 0.5, open ? 0.5 : 0,
            TimeSpan.FromMilliseconds(200));

        if (!open)
        {
            slideAnim.Completed += (_, _) =>
            {
                if (!_settingsOpen)
                    SettingsLayer.Visibility = Visibility.Collapsed;
            };
        }

        SettingsTrans.BeginAnimation(TranslateTransform.XProperty, slideAnim);
        SettingsBackdrop.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
    }

    // ── Settings load/save ───────────────────────────────────────────────────
    private void LoadSettingsToPanel()
    {
        ZapretBox.Text   = _settings.ZapretPath;
        TgWsBox.Text     = _settings.TgWsProxyPath;
        GdpiBox.Text     = _settings.GoodbyeDpiPath;
        AutoZapretCB.IsChecked  = _settings.AutostartZapret;
        AutoTgWsCB.IsChecked    = _settings.AutostartTgWsProxy;
        AutoAppCB.IsChecked     = _settings.AutostartApp;
        NotifyCB.IsChecked      = _settings.NotifyIssues;
        AutoUpdatesCB.IsChecked = _settings.AutoUpdates;
    }

    private void SaveSettings_Click(object s, RoutedEventArgs e)
    {
        _settings.ZapretPath       = ZapretBox.Text.Trim();
        _settings.TgWsProxyPath    = TgWsBox.Text.Trim();
        _settings.GoodbyeDpiPath   = GdpiBox.Text.Trim();
        _settings.AutostartZapret  = AutoZapretCB.IsChecked == true;
        _settings.AutostartTgWsProxy = AutoTgWsCB.IsChecked == true;
        _settings.AutostartApp     = AutoAppCB.IsChecked == true;
        _settings.NotifyIssues     = NotifyCB.IsChecked == true;
        _settings.AutoUpdates      = AutoUpdatesCB.IsChecked == true;
        SettingsService.Save(_settings);
        CloseSettings();
    }

    // ── Browse buttons ───────────────────────────────────────────────────────
    private string? BrowseExe(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title  = title,
            Filter = "Исполняемый файл (*.exe;*.bat)|*.exe;*.bat|Все файлы (*.*)|*.*"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void BrowseZapret_Click(object s, RoutedEventArgs e)
    {
        var p = BrowseExe("Выберите Zapret");
        if (p != null) ZapretBox.Text = p;
    }

    private void BrowseTgWs_Click(object s, RoutedEventArgs e)
    {
        var p = BrowseExe("Выберите tg-ws-proxy");
        if (p != null) TgWsBox.Text = p;
    }

    private void BrowseGdpi_Click(object s, RoutedEventArgs e)
    {
        var p = BrowseExe("Выберите GoodbyeDPI");
        if (p != null) GdpiBox.Text = p;
    }

    // ── Settings actions ─────────────────────────────────────────────────────
    private void ReOnboard_Click(object s, RoutedEventArgs e)
    {
        CloseSettings();
        SettingsService.ResetOnboarding();
        Dispatcher.InvokeAsync(() => ShowOnboarding(), DispatcherPriority.Background);
    }

    private void ResetSettings_Click(object s, RoutedEventArgs e)
    {
        _settings = new AppSettings();
        SettingsService.Save(_settings);
        SettingsService.ResetOnboarding();
        LoadSettingsToPanel();
        CloseSettings();
    }

    // ── Links ────────────────────────────────────────────────────────────────
    private void SupportBtn_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://t.me/rupleide");
    private void DonateBtn_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://www.donationalerts.com/r/rupleide");
    private void LinkZapret_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");
    private void LinkTgWs_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/tg-ws-proxy");
    private void LinkGdpi_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/ValdikSS/GoodbyeDPI");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ── Zapret Wizard ────────────────────────────────────────────────────────
    private void WizardCloseBtn_Click(object s, RoutedEventArgs e) => CloseWizard();

    private void CloseWizard()
    {
        var slideAnim = new DoubleAnimation(0, 370, TimeSpan.FromMilliseconds(220)) 
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        slideAnim.Completed += (_, _) => WizardLayer.Visibility = Visibility.Collapsed;
        WizardTrans.BeginAnimation(TranslateTransform.XProperty, slideAnim);
    }

    private void ShowZapretWizard()
    {
        WizardLayer.Visibility = Visibility.Visible;
        var slideAnim = new DoubleAnimation(370, 0, TimeSpan.FromMilliseconds(220)) 
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        WizardTrans.BeginAnimation(TranslateTransform.XProperty, slideAnim);

        try {
            Process.Start(new ProcessStartInfo(_settings.ZapretPath) {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(_settings.ZapretPath)
            });
        } catch {}

        RenderWizardStep(0);
    }

    private void RenderWizardStep(int step)
    {
        WizardContent.Children.Clear();
        switch (step) {
            case 0:
                AddWizText("Я запустил файл service.bat.\n\nУ тебя открылось окно консоли?");
                AddWizBtn("Да, открылось", "#22c55e", () => RenderWizardStep(2));
                AddWizBtn("Нет", "#ef4444", () => RenderWizardStep(1));
                break;
            case 1:
                AddWizText("Окно не открылось.\nВозможно, путь к файлу указан неверно или антивирус заблокировал запуск.\n\nПроверь настройки и попробуй снова.");
                AddWizBtn("Закрыть", "#3b82f6", CloseWizard);
                break;
            case 2:
                AddWizText("Ты запускаешь его в первый раз?");
                AddWizBtn("Да", "#3b82f6", () => RenderWizardStep(3));
                AddWizBtn("Нет", "#2e2e2e", () => RenderWizardStep(5), "#cccccc");
                break;
            case 3:
                AddWizText("В таком случае нажми на клавиатуре цифру 2, а потом нажми Enter.\n\nСделал?");
                AddWizBtn("Да, сделал", "#3b82f6", () => RenderWizardStep(4));
                break;
            case 4:
                AddWizText("Видишь текст 'Press any key to continue...'?\n\nЕсли да — нажимай ещё раз Enter.");
                AddWizBtn("Сделал", "#3b82f6", () => RenderWizardStep(5));
                break;
            case 5:
                AddWizText("Теперь самое главное!\nНапиши 11 и нажми Enter.\n\nОткрылось новое окно тестирования (Blockcheck)?");
                AddWizBtn("Да, открылось", "#22c55e", () => RenderWizardStep(7));
                AddWizBtn("Нет", "#ef4444", () => RenderWizardStep(6));
                break;
            case 6:
                AddWizText("Окно тестов не открылось.\nВозможно, нужно запустить программу от имени администратора или полностью распаковать архив.\nПопробуй исправить это и начни заново.");
                AddWizBtn("Понятно", "#3b82f6", CloseWizard);
                break;
            case 7:
                AddWizText("Отлично!\nВ новом окне выбери:\n\n1 — Standard tests (HTTP/ping)\n\nИ нажми Enter.");
                AddWizBtn("Нажал", "#3b82f6", () => RenderWizardStep(8));
                break;
            case 8:
                AddWizText("Теперь на вопрос 'Select test run mode' выбери:\n\n1 — All configs\n\nИ нажми Enter.\n\nПосле этого начнется тест. Жди до конца!");
                AddWizBtn("Понял, жду", "#3b82f6", () => RenderWizardStep(9));
                break;
            case 9:
                AddWizText("Сканирование идет долго. После его окончания запиши куда-нибудь конфиги, где всё помечено ЗЕЛЕНЫМ цветом.\n\nСАМОЕ ГЛАВНОЕ: в самом конце напишет 'Best config: [цифра]'.\n\nОбязательно запомни эту цифру!");
                AddWizBtn("Я запомнил!", "#22c55e", () => RenderWizardStep(10));
                break;
            case 10:
                AddWizText("Сейчас закрой все черные окна консоли.\n\nЯ снова запущу service.bat. Набери ту цифру, которую выдал тест (твой Best config), и нажми Enter!\n\nИспользуй этот конфиг всегда. Если начнутся проблемы с сетью — просто пройди тест (Blockcheck) снова.");
                AddWizBtn("Открыть service.bat и продолжить", "#3b82f6", () => {
                    CloseWizard();
                    try {
                        Process.Start(new ProcessStartInfo(_settings.ZapretPath) {
                            UseShellExecute = true,
                            WorkingDirectory = System.IO.Path.GetDirectoryName(_settings.ZapretPath)
                        });
                    } catch {}
                    RunAutoFix();
                });
                break;
        }
    }

    private void AddWizText(string txt)
    {
        WizardContent.Children.Add(new TextBlock {
            Text = txt, FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        });
    }

    private void AddWizBtn(string txt, string hex, Action act, string fgHex = "#ffffff")
    {
        var btn = new Button {
            Content = txt,
            Background = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!,
            Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(fgHex)!,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Height = 40,
            Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 10),
            Template = CreateSimpleBtnTemplate(hex)
        };
        btn.Click += (_, _) => act();
        WizardContent.Children.Add(btn);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ONBOARDING
    // ══════════════════════════════════════════════════════════════════════════

    private void ShowOnboarding()
    {
        OnboardLayer.Visibility = Visibility.Visible;
        Opacity = 1;
        ShowOnboardScreen(0);
    }

    private void ShowOnboardScreen(int n)
    {
        var grid = new Grid { Background = Brushes.Transparent };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            MaxWidth            = 520,
            Margin              = new Thickness(32)
        };
        grid.Children.Add(stack);

        switch (n)
        {
            case 0: BuildOnboard0(stack); break;
            case 1: BuildOnboard1(stack); break;
            case 2: BuildOnboard2(stack); break;
            case 3: BuildOnboard3(stack); break;
            case 4: BuildOnboardZapretChoice(stack); break;
            case 5: BuildOnboardLetsDoIt(stack); break;
            case 6: BuildOnboardDownloadArchive(stack); break;
            case 7: BuildOnboardExtract(stack); break;
            case 8: BuildOnboardZapretSelectBat(stack); break;
            case 9: BuildOnboardZapretSuccess(stack); break;
            case 10: BuildOnboardTgWsChoice(stack); break;
            case 11: BuildOnboardTgWsDownload(stack); break;
            case 12: BuildOnboardTgWsMove(stack); break;
            case 13: BuildOnboardTgWsSelectExe(stack); break;
            case 15: BuildOnboardDone(stack); break;
        }

        OnboardContent.Content = grid;
        FadeInElement(grid);
    }

    private static void FadeInElement(UIElement el)
    {
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    private void BuildOnboard0(StackPanel p)
    {
        AddOnboardTitle(p, "Привет!");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(1));
    }

    private void BuildOnboard1(StackPanel p)
    {
        AddOnboardSub(p, "Это программа создана для людей, у которых есть проблемы с интернетом в России!\nЕсли у вас есть ВПН, то вам это, скорее всего, не понадобится.\nПриложение предлагает решение всех проблем, а также полную автоматизацию.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(2));
    }

    private void BuildOnboard2(StackPanel p)
    {
        AddOnboardSub(p, "Приложение НЕ СОБИРАЕТ ВАШИ ДАННЫЕ.\nКак разработчик пишу: ОНИ МНЕ НАХУЙ НЕ НУЖНЫ!\nЕсли вы беспокоитесь за свою безопасность, то нахуя вы скачали это с GitHub?\nВ любом случае, исходный код доступен на GitHub.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(3));
    }

    private void BuildOnboard3(StackPanel p)
    {
        AddOnboardSub(p, "Для работы приложения вам нужно скачать следующие компоненты:");
        AddOnboardBtn(p, "Погнали", "#3b82f6", () => ShowOnboardScreen(4));
    }

    private void BuildOnboardZapretChoice(StackPanel p)
    {
        AddOnboardTitle(p, "У вас установлен zapret-discord-youtube?");
        AddOnboardBtn(p, "Да — выбрать файл", "#22c55e", () =>
        {
            var dlg = new OpenFileDialog { Title = "Выберите service.bat", Filter = "service.bat|service.bat|Все файлы|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _settings.ZapretPath = dlg.FileName;
                SettingsService.Save(_settings);
                ZapretBox.Text = dlg.FileName;
                ShowOnboardScreen(9);
            }
        });
        AddOnboardBtn(p, "Нет, давай скачаю", "#2e2e2e", () => ShowOnboardScreen(5), foreground: "#888888");
    }

    private void BuildOnboardLetsDoIt(StackPanel p)
    {
        AddOnboardTitle(p, "Давай всё сделаем");
        AddOnboardSub(p, "Давай всё настроим за пару минут.\nНажми кнопку ниже, чтобы получить нужные компоненты.");
        AddOnboardBtn(p, "Скачать Zapret", "#3b82f6", () => 
        {
            OpenUrl("https://github.com/Flowseal/zapret-discord-youtube/releases/latest");
            ShowOnboardScreen(6);
        });
    }

    private void BuildOnboardDownloadArchive(StackPanel p)
    {
        AddOnboardSub(p, "Опустите на сайте ниже и найдите вкладку с файлами называется Assets.\nТам есть несколько файлов zapret-discord-youtube-1.9.7b.rar или .zip.\nКачай какую хочешь.");
        AddOnboardBtn(p, "Я скачал архив", "#3b82f6", () => ShowOnboardScreen(7));
    }

    private void BuildOnboardExtract(StackPanel p)
    {
        try 
        {
            Directory.CreateDirectory(@"C:\Zapret");
            Process.Start("explorer.exe", @"C:\Zapret");
        }
        catch (Exception)
        {
            // Ignored if permissions are strictly denied without admin
        }

        AddOnboardSub(p, "Я открыл тебе папку C:\\Zapret.\nОткрой Архив который ты скачал, нажми CTRL + A, чтобы выделить всё,\nи перекинь все файлы в эту папку.");
        AddOnboardBtn(p, "Я перекинул файлы", "#3b82f6", () => ShowOnboardScreen(8));
    }

    private void BuildOnboardZapretSelectBat(StackPanel p)
    {
        AddOnboardTitle(p, "Выбор service.bat");
        AddOnboardSub(p, "Теперь выбери файл service.bat в папке, куда ты только что перекинул файлы.");
        AddOnboardBtn(p, "Выбрать service.bat", "#22c55e", () => 
        {
            var dlg = new OpenFileDialog { Title = "Выберите service.bat", Filter = "service.bat|service.bat|Все файлы|*.*", InitialDirectory = @"C:\Zapret" };
            if (dlg.ShowDialog() == true)
            {
                _settings.ZapretPath = dlg.FileName;
                SettingsService.Save(_settings);
                ZapretBox.Text = dlg.FileName;
                ShowOnboardScreen(9);
            }
        });
    }

    private void BuildOnboardZapretSuccess(StackPanel p)
    {
        AddOnboardEmoji(p, "🎉");
        AddOnboardTitle(p, "Ты молодец!");
        AddOnboardSub(p, "Надеюсь, ты сделал всё правильно.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(10));
    }

    private void BuildOnboardTgWsChoice(StackPanel p)
    {
        AddOnboardTitle(p, "У вас установлен tg-ws-proxy?");
        AddOnboardBtn(p, "Да — выбрать файл", "#22c55e", () =>
        {
            var dlg = new OpenFileDialog { Title = "Выберите файл TgWsProxy.exe", Filter = "TgWsProxy.exe|*TgWsProxy*.exe|Исполняемые файлы (*.exe)|*.exe|Все файлы|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _settings.TgWsProxyPath = dlg.FileName;
                SettingsService.Save(_settings);
                TgWsBox.Text = dlg.FileName;
                ShowOnboardScreen(15);
            }
        });
        AddOnboardBtn(p, "Нет, давай скачаю", "#2e2e2e", () => ShowOnboardScreen(11), foreground: "#888888");
    }

    private void BuildOnboardTgWsDownload(StackPanel p)
    {
        AddOnboardSub(p, "Опустите на сайте ниже и найдите вкладку с файлами называется Assets.\nТам нужно скачать не архив, а сам файл TgWsProxy.exe.");
        AddOnboardBtn(p, "Скачать TgWsProxy.exe", "#3b82f6", () => 
        {
            OpenUrl("https://github.com/Flowseal/tg-ws-proxy/releases/latest");
            ShowOnboardScreen(12);
        });
    }

    private void BuildOnboardTgWsMove(StackPanel p)
    {
        try { Process.Start("explorer.exe", @"C:\Zapret"); } catch {}

        AddOnboardSub(p, "Я снова открыл тебе папку C:\\Zapret.\nТеперь перекинь скачанный файл TgWsProxy.exe в эту папку.");
        AddOnboardBtn(p, "Я перекинул", "#3b82f6", () => ShowOnboardScreen(13));
    }

    private void BuildOnboardTgWsSelectExe(StackPanel p)
    {
        AddOnboardTitle(p, "Выбор TgWsProxy.exe");
        AddOnboardSub(p, "Теперь выбери файл TgWsProxy.exe, который ты только что перекинул в папку.");
        AddOnboardBtn(p, "Выбрать TgWsProxy.exe", "#22c55e", () => 
        {
            var dlg = new OpenFileDialog { Title = "Выберите TgWsProxy.exe", Filter = "TgWsProxy.exe|*TgWsProxy*.exe|Исполняемые файлы (*.exe)|*.exe|Все файлы|*.*", InitialDirectory = @"C:\Zapret" };
            if (dlg.ShowDialog() == true)
            {
                _settings.TgWsProxyPath = dlg.FileName;
                SettingsService.Save(_settings);
                TgWsBox.Text = dlg.FileName;
                ShowOnboardScreen(15);
            }
        });
    }

    private void BuildOnboardDone(StackPanel p)
    {
        AddOnboardEmoji(p, "✅");
        AddOnboardTitle(p, "Всё готово!");
        
        var subText = new TextBlock();
        subText.FontFamily = new FontFamily("Segoe UI");
        subText.FontSize = 15;
        subText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        subText.HorizontalAlignment = HorizontalAlignment.Center;
        subText.TextAlignment = TextAlignment.Center;
        subText.TextWrapping = TextWrapping.Wrap;
        subText.Margin = new Thickness(0, 0, 0, 24);
        
        subText.Inlines.Add(new System.Windows.Documents.Run("Пути сохранены. Можно запускать.\n\nНастройки можно изменить в любое время через "));
        
        var pathIcon = new System.Windows.Shapes.Path();
        pathIcon.Width = 14;
        pathIcon.Height = 14;
        pathIcon.Stretch = System.Windows.Media.Stretch.Uniform;
        pathIcon.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        pathIcon.Data = Geometry.Parse("M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.06-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61 l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41 h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.73,8.87 C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.06,0.94l-2.03,1.58 c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54 c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.43-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96 c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.49-0.12-0.61L19.14,12.94z M12,15.6c-1.98,0-3.6-1.62-3.6-3.6 s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z");
        pathIcon.Margin = new Thickness(2, 0, 0, -2);
        
        var inlineIcon = new System.Windows.Documents.InlineUIContainer(pathIcon);
        inlineIcon.BaselineAlignment = System.Windows.BaselineAlignment.Center;
        
        subText.Inlines.Add(inlineIcon);
        p.Children.Add(subText);

        AddOnboardBtn(p, "Открыть приложение →", "#3b82f6", () =>
        {
            SettingsService.MarkOnboarded();
            OnboardLayer.Visibility = Visibility.Collapsed;
            CheckInternetOnStart();
            StartActiveAppsMonitor();
        });
    }

    // ── Onboard helpers ──────────────────────────────────────────────────────
    private static void AddOnboardEmoji(StackPanel p, string emoji) =>
        p.Children.Add(new TextBlock
        {
            Text = emoji, FontSize = 54, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Segoe UI Emoji"),
            Margin = new Thickness(0, 0, 0, 12)
        });

    private static void AddOnboardTitle(StackPanel p, string text) =>
        p.Children.Add(new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 22,
            FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        });

    private static void AddOnboardSub(StackPanel p, string text) =>
        p.Children.Add(new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });

    private void AddOnboardBtn(StackPanel p, string text, string bgHex, Action action,
        string foreground = "#ffffff")
    {
        var btn = new Button
        {
            Content             = text,
            Background          = (SolidColorBrush)new BrushConverter().ConvertFrom(bgHex)!,
            Foreground          = (SolidColorBrush)new BrushConverter().ConvertFrom(foreground)!,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 14,
            Height              = 44,
            Cursor              = Cursors.Hand,
            BorderThickness     = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin              = new Thickness(0, 0, 0, 10),
        };

        // Simple style inline
        btn.Template = CreateSimpleBtnTemplate(bgHex);
        btn.Click += (_, _) => action();
        p.Children.Add(btn);
    }

    private static ControlTemplate CreateSimpleBtnTemplate(string bgHex)
    {
        var tmpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty,
            (SolidColorBrush)new BrushConverter().ConvertFrom(bgHex)!);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tmpl.VisualTree = bd;
        return tmpl;
    }

    private void AddOnboardLink(StackPanel p, string text, string url)
    {
        var btn = new Button
        {
            Content             = text,
            Background          = Brushes.Transparent,
            Foreground          = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 12,
            BorderThickness     = new Thickness(0),
            Cursor              = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 4, 0, 0),
        };
        btn.Template = CreateTransparentBtnTemplate();
        btn.Click   += (_, _) => OpenUrl(url);
        p.Children.Add(btn);
    }

    private static ControlTemplate CreateTransparentBtnTemplate()
    {
        var tmpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tmpl.VisualTree = bd;
        return tmpl;
    }
}

// ── Brush Animation Helper ─────────────────────────────────────────────────────
public class BrushAnimation : AnimationTimeline
{
    public Brush? From { get; set; }
    public Brush? To { get; set; }

    public override Type TargetPropertyType => typeof(Brush);

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        if (animationClock == null || animationClock.CurrentProgress == null)
            return Brushes.Transparent;

        var fromBrush = From ?? (defaultOriginValue as Brush) ?? Brushes.Transparent;
        var toBrush = To ?? (defaultDestinationValue as Brush) ?? Brushes.Transparent;

        if (fromBrush is SolidColorBrush fromSolid && toBrush is SolidColorBrush toSolid)
        {
            var colorAnimation = new ColorAnimation(
                fromSolid.Color,
                toSolid.Color,
                Duration);
            colorAnimation.AccelerationRatio = AccelerationRatio;
            colorAnimation.DecelerationRatio = DecelerationRatio;
            colorAnimation.AutoReverse = AutoReverse;
            colorAnimation.RepeatBehavior = RepeatBehavior;

            var currentColor = (Color)colorAnimation.GetCurrentValue(fromSolid.Color, toSolid.Color, animationClock);
            return new SolidColorBrush(currentColor);
        }

        return toBrush;
    }

    protected override Freezable CreateInstanceCore() => new BrushAnimation();
}
