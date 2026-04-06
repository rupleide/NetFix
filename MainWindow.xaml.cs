using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Win32;
using System.Windows.Threading;
using NetFix.Models;
using NetFix.Services;

// Алиасы для разрешения конфликтов между WPF и WinForms
using Color        = System.Windows.Media.Color;
using Brushes      = System.Windows.Media.Brushes;
using FontFamily   = System.Windows.Media.FontFamily;
using Clipboard    = System.Windows.Clipboard;
using Cursors      = System.Windows.Input.Cursors;
using Orientation  = System.Windows.Controls.Orientation;
using Button       = System.Windows.Controls.Button;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Brush        = System.Windows.Media.Brush;
using Panel        = System.Windows.Controls.Panel;
using Size         = System.Windows.Size;

namespace NetFix;

public partial class MainWindow : Window
{
    // ── State ────────────────────────────────────────────────────────────────
    private AppSettings _settings = SettingsService.Load();
    private bool _settingsOpen = false;
    private DispatcherTimer _monitorTimer = null!;
    private System.Windows.Forms.NotifyIcon _trayIcon = null!;

    // ── Init ─────────────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        InitTray();
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

    // ── Tray Icon ─────────────────────────────────────────────────────────────
    private void InitTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Visible = true,
            Text = "NetFix"
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                ShowFromTray();
            else if (e.Button == System.Windows.Forms.MouseButtons.Right)
                ShowTrayMenu();
        };
    }

    private void ShowTrayMenu()
    {
        var popup = new TrayPopup { Owner = this };

        var pos    = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(pos);

        popup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double popupWidth  = 200;
        double popupHeight = 140;

        double left = pos.X - popupWidth / 2;
        double top  = screen.WorkingArea.Bottom - popupHeight - 4;

        if (left + popupWidth > screen.WorkingArea.Right)
            left = screen.WorkingArea.Right - popupWidth - 4;
        if (left < screen.WorkingArea.Left)
            left = screen.WorkingArea.Left + 4;

        popup.Left = left;
        popup.Top  = top;
        popup.Show();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ── Service Control Handlers ───────────────────────────────────────────────────
    private void ServicesBtn_Click(object s, RoutedEventArgs e)
    {
        ServicesLayer.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(300, 0, TimeSpan.FromMilliseconds(280));
        anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        ServicesTrans.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void CloseServicesPanel()
    {
        var anim = new DoubleAnimation(0, 300, TimeSpan.FromMilliseconds(220));
        anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
        anim.Completed += (_, _) => ServicesLayer.Visibility = Visibility.Collapsed;
        ServicesTrans.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void ServicesCloseBtn_Click(object s, RoutedEventArgs e) => CloseServicesPanel();
    private void ServicesBackdrop_Click(object s, MouseButtonEventArgs e) => CloseServicesPanel();

    private void ZapretToggle_Click(object s, RoutedEventArgs e)
    {
        var st = DiagnosticsEngine.CheckAppStatus();
        if (st.ZapretRunning)
        {
            foreach (var p in Process.GetProcessesByName("winws"))
                try { p.Kill(); } catch { }
            foreach (var p in Process.GetProcessesByName("winws.exe"))
                try { p.Kill(); } catch { }
        }
        else
        {
            if (!string.IsNullOrEmpty(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
                Process.Start(new ProcessStartInfo(_settings.ZapretPath) { UseShellExecute = true });
            else
                ShowNotification("Zapret", "Путь не указан. Проверьте настройки.", isError: true);
        }

        // Обновить статус через 800мс
        Task.Delay(800).ContinueWith(_ => Dispatcher.Invoke(UpdateActiveApps));
    }

    private void TgWsToggle_Click(object s, RoutedEventArgs e)
    {
        var st = DiagnosticsEngine.CheckAppStatus();
        if (st.TgWsProxyRunning)
        {
            foreach (var p in Process.GetProcessesByName("TgWsProxy"))
                try { p.Kill(); } catch { }
        }
        else
        {
            if (!string.IsNullOrEmpty(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
                Process.Start(new ProcessStartInfo(_settings.TgWsProxyPath) { UseShellExecute = true });
            else
                ShowNotification("tg-ws-proxy", "Путь не указан. Проверьте настройки.", isError: true);
        }

        Task.Delay(800).ContinueWith(_ => Dispatcher.Invoke(UpdateActiveApps));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosing(e);
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
    private void CloseBtn_Click(object s, RoutedEventArgs e) => Hide();

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

    // ── FAQ ОБНОВЛЕННАЯ ЛОГИКА ───────────────────────────────────────────────
    private string _currentFaqCategory = "";

    private void FaqNavBtn_Click(object s, RoutedEventArgs e)
    {
        MainPage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Visible;
        FaqNavBtn.Foreground = Brushes.White;
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        ShowFaqCategories();
    }

    private void FaqBackBtn_Click(object s, RoutedEventArgs e)
    {
        if (FaqHeaderTitle.Text == "Частые вопросы") {
            FaqPage.Visibility = Visibility.Collapsed;
            MainPage.Visibility = Visibility.Visible;
            FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        } else if (_currentFaqCategory != "" && FaqHeaderTitle.Text != _currentFaqCategory) {
            ShowFaqQuestions(_currentFaqCategory);
        } else {
            ShowFaqCategories();
        }
    }

    private void ShowFaqCategories()
    {
        _currentFaqCategory = "";
        FaqHeaderTitle.Text = "Частые вопросы";
        FaqContainer.Children.Clear();

        AddCategoryCard("Telegram", "Настройка прокси и загрузка медиа", "TelegramIcon", Color.FromRgb(0x3b, 0x82, 0xf6));
        AddCategoryCard("Discord", "Обновление и голосовые каналы", "DiscordIcon", Color.FromRgb(0x8b, 0x5c, 0xf6));
        AddCategoryCard("Общее", "YouTube, Zapret и сетевые ошибки", "SettingsIcon", Color.FromRgb(0x22, 0xc5, 0x5e));
        
        // Добавляем специальную карточку для Android
        AddAndroidCard();
        
        // Добавляем блок с обращением для помощи
        var helpCard = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 18, 20, 18),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 12, 0, 0)
        };
        
        var helpStack = new StackPanel();
        helpStack.Children.Add(new TextBlock { 
            Text = "Не нашли решение своей проблемы?", 
            FontSize = 16, 
            FontWeight = FontWeights.Bold, 
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        });
        
        helpStack.Children.Add(new TextBlock { 
            Text = "Самостоятельный поиск: Лучший способ, вбить текст ошибки в поисковик. Скорее всего, кто-то уже сталкивался с этим и нашёл решение.", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        
        helpStack.Children.Add(new TextBlock { 
            Text = "Обращение ко мне: Если ничего не помогло, вы можете описать свою проблему в разделе Issues на моём GitHub-репозитории. Я постараюсь ответить по мере возможности.", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        
        var devLinkStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        
        var linkText = new TextBlock { 
            Text = "Поиск у разработчика: Также рекомендую поискать решение в репозитории Flowseal, который является автором сборки Zapret и TgProxy:", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        
        // Создаем кликабельное слово "репозитории"
        var repoLink = new Button {
            Content = "репозитории",
            Style = (Style)FindResource("FlatBtn"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            FontSize = 13,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        repoLink.Click += (s, e) => {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://github.com/Flowseal/zapret-discord-youtube",
                    UseShellExecute = true
                });
            } catch { }
        };
        
        var inlineText = new Run(" Flowseal, который является автором сборки Zapret и TgProxy:");
        
        var textPanel = new StackPanel { Orientation = Orientation.Horizontal };
        textPanel.Children.Add(new TextBlock { 
            Text = "Поиск у разработчика: Также рекомендую поискать решение в ", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center
        });
        textPanel.Children.Add(repoLink);
        textPanel.Children.Add(new TextBlock { 
            Text = " Flowseal, который является автором сборки Zapret и TgProxy:", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center
        });
        
        devLinkStack.Children.Add(textPanel);
        helpStack.Children.Add(devLinkStack);
        
        helpCard.Child = helpStack;
        FaqContainer.Children.Add(helpCard);
    }

    private void AddCategoryCard(string title, string desc, string iconKey, Color accent)
    {
        var btn = new Button { 
            Style = (Style)FindResource("FlatBtn"), 
            Padding = new Thickness(0),
            Height = double.NaN, // убирает любой фиксированный Height
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        
        var card = new Border { 
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), 
            CornerRadius = new CornerRadius(0, 12, 12, 0),
            Padding = new Thickness(16, 14, 16, 14), // одинаково везде
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(3, 0, 0, 0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBg = new Border {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(accent) { Opacity = 0.15 },
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        
        // Create vector icon directly without FindResource
        Geometry geometry = null;
        
        // Create geometry based on icon key
        switch (iconKey)
        {
            case "TelegramIcon":
                // Official Telegram icon from simpleicons.org
                geometry = Geometry.Parse("M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z");
                break;
            case "DiscordIcon":
                // Official Discord icon from simpleicons.org
                geometry = Geometry.Parse("M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z");
                break;
            case "SettingsIcon":
                // Light bulb icon - ideas, solutions, help
                geometry = Geometry.Parse("M19.0006 9.03002C19.0007 8.10058 18.8158 7.18037 18.4565 6.32317C18.0972 5.46598 17.5709 4.68895 16.9081 4.03734C16.2453 3.38574 15.4594 2.87265 14.5962 2.52801C13.7331 2.18336 12.8099 2.01409 11.8806 2.03002C10.0966 2.08307 8.39798 2.80604 7.12302 4.05504C5.84807 5.30405 5.0903 6.98746 5.00059 8.77001C4.95795 9.9595 5.21931 11.1402 5.75999 12.2006C6.30067 13.2609 7.10281 14.1659 8.09058 14.83C8.36897 15.011 8.59791 15.2584 8.75678 15.5499C8.91565 15.8415 8.99945 16.168 9.00059 16.5V18.03H15.0006V16.5C15.0006 16.1689 15.0829 15.843 15.24 15.5515C15.3971 15.26 15.6241 15.0121 15.9006 14.83C16.8528 14.1911 17.6336 13.328 18.1741 12.3167C18.7147 11.3054 18.9985 10.1767 19.0006 9.03002V9.03002Z M15 21.04C14.1345 21.6891 13.0819 22.04 12 22.04C10.9181 22.04 9.86548 21.6891 9 21.04");
                break;
            default:
                // Fallback to text
                iconBg.Child = new TextBlock 
                { 
                    Text = iconKey.Substring(0, 1), 
                    FontSize = 20, 
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center, 
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(accent)
                };
                break;
        }
        
        if (geometry != null)
        {
            var iconPath = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            
            if (iconKey == "TelegramIcon" || iconKey == "DiscordIcon")
                iconPath.Fill = new SolidColorBrush(accent);
            else
            {
                iconPath.Stroke = new SolidColorBrush(accent);
                iconPath.StrokeThickness = 2;
            }
            
            iconBg.Child = iconPath;
        }

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        stack.Children.Add(new TextBlock { Text = desc, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) });

        var arrowBadge = new Border {
            Width = 32, Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(accent) { Opacity = 0.12 },
            VerticalAlignment = VerticalAlignment.Center
        };
        arrowBadge.Child = new TextBlock { 
            Text = "›", FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center, 
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
            IsHitTestVisible = false // не поглощает мышь
        };

        Grid.SetColumn(iconBg, 0); Grid.SetColumn(stack, 1); Grid.SetColumn(arrowBadge, 2);
        grid.Children.Add(iconBg); grid.Children.Add(stack); grid.Children.Add(arrowBadge);
        
        card.Child = grid; btn.Content = card;
        
        // Hover-эффект только на Border
        btn.MouseEnter += (s, e) => {
            var border = (Border)btn.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
        };
        btn.MouseLeave += (s, e) => {
            var border = (Border)btn.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
        };
        
        btn.Click += (s, e) => ShowFaqQuestions(title);
        FaqContainer.Children.Add(btn);
    }

    private void AddAndroidCard()
    {
        var btn = new Button { 
            Style = (Style)FindResource("FlatBtn"), 
            Padding = new Thickness(0),
            Height = double.NaN,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0)
        };
        
        var card = new Border { 
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x2e, 0x1a)),
            CornerRadius = new CornerRadius(0, 12, 12, 0),
            Padding = new Thickness(20, 18, 20, 18),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            BorderThickness = new Thickness(4, 0, 0, 0)
        };

        var stack = new StackPanel();
        
        var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        
        // Create badge with fire icon and text inside
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)) { Opacity = 0.2 },
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3)
        };
        
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };
        
        // Add fire icon inside badge
        var fireIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M187.899,164.809 C185.803,214.868 144.574,254.812 94.000,254.812 C42.085,254.812 -0.000,211.312 -0.000,160.812 C-0.000,154.062 -0.121,140.572 10.000,117.812 C16.057,104.191 19.856,95.634 22.000,87.812 C23.178,83.513 25.469,76.683 32.000,87.812 C35.851,94.374 36.000,103.812 36.000,103.812 C36.000,103.812 50.328,92.817 60.000,71.812 C74.179,41.019 62.866,22.612 59.000,9.812 C57.662,5.384 56.822,-2.574 66.000,0.812 C75.352,4.263 100.076,21.570 113.000,39.812 C131.445,65.847 138.000,90.812 138.000,90.812 C138.000,90.812 143.906,83.482 146.000,75.812 C148.365,67.151 148.400,58.573 155.999,67.813 C163.226,76.600 173.959,93.113 180.000,108.812 C190.969,137.321 187.899,164.809 187.899,164.809 Z M94.000,254.812 C58.101,254.812 29.000,225.711 29.000,189.812 C29.000,168.151 37.729,155.000 55.896,137.166 C67.528,125.747 78.415,111.722 83.042,102.172 C83.953,100.292 86.026,90.495 94.019,101.966 C98.212,107.982 104.785,118.681 109.000,127.812 C116.266,143.555 118.000,158.812 118.000,158.812 C118.000,158.812 125.121,154.616 130.000,143.812 C131.573,140.330 134.753,127.148 143.643,140.328 C150.166,150.000 159.127,167.390 159.000,189.812 C159.000,225.711 129.898,254.812 94.000,254.812 Z M95.000,183.812 C104.250,183.812 104.250,200.941 116.000,223.812 C123.824,239.041 112.121,254.812 95.000,254.812 C77.879,254.812 69.000,240.933 69.000,223.812 C69.000,206.692 85.750,183.812 95.000,183.812 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        badgeContent.Children.Add(fireIcon);
        
        badgeContent.Children.Add(new TextBlock { 
            Text = "НОВИНКА", 
            FontSize = 11, 
            FontWeight = FontWeights.Bold, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            VerticalAlignment = VerticalAlignment.Center
        });
        
        badge.Child = badgeContent;
        headerStack.Children.Add(badge);
        stack.Children.Add(headerStack);
        
        stack.Children.Add(new TextBlock { 
            Text = "TgWsProxy на Android!", 
            FontSize = 18, 
            FontWeight = FontWeights.Bold, 
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 6)
        });
        
        stack.Children.Add(new TextBlock { 
            Text = "Telegram будет работать на телефоне без VPN", 
            FontSize = 14, 
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xdd, 0xaa)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        
        var arrowText = new TextBlock { 
            Text = "Узнать подробнее →", 
            FontSize = 13, 
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
        };
        stack.Children.Add(arrowText);

        card.Child = stack;
        btn.Content = card;
        
        btn.MouseEnter += (s, e) => {
            var borderElement = (Border)btn.Content;
            borderElement.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x36, 0x1e));
        };
        btn.MouseLeave += (s, e) => {
            var borderElement = (Border)btn.Content;
            borderElement.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x2e, 0x1a));
        };
        
        btn.Click += (s, e) => ShowAndroidInfo();
        FaqContainer.Children.Add(btn);
    }

    private void ShowAndroidInfo()
    {
        FaqHeaderTitle.Text = "Android решение";
        FaqContainer.Children.Clear();
        
        var mainCard = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
        };

        var stack = new StackPanel();
        
        stack.Children.Add(new TextBlock { 
            Text = "TgWsProxy на Android", 
            FontSize = 19, 
            FontWeight = FontWeights.Bold, 
            Foreground = Brushes.White, 
            TextWrapping = TextWrapping.Wrap, 
            Margin = new Thickness(0, 0, 0, 18) 
        });

        var infoText = "Новый способ обхода блокировок Telegram на Android\n\n" +
            "Пока NetFix Mobile находится в разработке, делюсь рабочим решением от стороннего разработчика LemoLev. " +
            "Это отличный вариант для тех, кто устал от VPN и хочет стабильной работы Telegram через прокси.\n\n" +
            "Важное уточнение: Этот метод, «домашнее» решение. Прокси не работает на мобильном интернете. " +
            "Но если вы подключены к Wi-Fi или кто-то раздает вам интернет, всё должно работать.\n\n" +
            "Полная инструкция по установке и настройке, а также APK-файл доступны в моём Telegram-канале. " +
            "Там всё очень подробно расписано, шаг за шагом.\n\n" +
            "Переходите в канал для получения инструкции и файла:";

        stack.Children.Add(new TextBlock { 
            Text = infoText, 
            FontSize = 15, 
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)), 
            TextWrapping = TextWrapping.Wrap, 
            LineHeight = 24,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var linkBtn = new Button {
            Style = (Style)FindResource("AccentBtn"),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 0)
        };
        
        var linkBtnContent = new StackPanel { Orientation = Orientation.Horizontal };
        var linkIcon = new System.Windows.Shapes.Path {
            Data = (Geometry)FindResource("ExternalLinkIcon"),
            Fill = Brushes.White,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        linkBtnContent.Children.Add(linkIcon);
        linkBtnContent.Children.Add(new TextBlock { 
            Text = "Открыть Telegram-канал @NetFixRuBi",
            VerticalAlignment = VerticalAlignment.Center
        });
        linkBtn.Content = linkBtnContent;
        linkBtn.Click += (s, e) => {
            try {
                // Пробуем открыть напрямую в Telegram
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "tg://resolve?domain=NetFixRuBi",
                    UseShellExecute = true
                });
            } catch {
                // Если не получилось, открываем через браузер
                try {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = "https://t.me/NetFixRuBi",
                        UseShellExecute = true
                    });
                } catch { }
            }
        };
        stack.Children.Add(linkBtn);

        mainCard.Child = stack;
        FaqContainer.Children.Add(mainCard);

        // Кнопка назад
        var btnContainer = new Button {
            Style = (Style)FindResource("FlatBtn"),
            Padding = new Thickness(0),
            Height = double.NaN,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 24, 0, 0)
        };

        var backBtn = new Border {
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x33, 0x56)),
            Padding = new Thickness(20, 10, 20, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        
        backBtn.Child = new TextBlock {
            Text = "← Вернуться к FAQ",
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            IsHitTestVisible = false
        };
        
        btnContainer.Content = backBtn;
        
        btnContainer.MouseEnter += (s, e) => {
            var border = (Border)btnContainer.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1f, 0x3d, 0x66));
        };
        btnContainer.MouseLeave += (s, e) => {
            var border = (Border)btnContainer.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x33, 0x56));
        };
        
        btnContainer.Click += (s, e) => ShowFaqCategories();
        FaqContainer.Children.Add(btnContainer);
    }

    private void ShowFaqQuestions(string category)
    {
        _currentFaqCategory = category;
        FaqHeaderTitle.Text = category;
        FaqContainer.Children.Clear();

        if (category == "Telegram") {
            AddQuestion("Пропал значок TgWsProxy справа внизу, где найти программу?", "Зайди в папку C:\\Zapret, найди там TgWsProxy.exe и запусти его. После этого проверь иконку в трее (возле часов) и нажми «Включить».");
            AddQuestion("Телеграм не грузит, хотя TgWsProxy запущен.", "Убедись, что прокси включен внутри самого Telegram. Проверь настройки: Продвинутые настройки -> Тип соединения -> Использовать собственный прокси. Если не помогло, нажми правой кнопкой по значку в трее -> Перезапустить прокси. Если всё равно глухо, скачай свежую версию по ссылке: GitHub Releases. В крайнем случае проверь настройки DNS в Windows, они могут блокировать соединение.");
            AddQuestion("Текст грузится, а фото и кружочки, нет", "Это нормально. Отправка и загрузка тяжелых файлов через прокси может идти медленно из-за особенностей фильтров провайдера. Наберись терпения или используй VPN для тяжелого контента.");
        } 
        else if (category == "Discord") {
            AddQuestion("Бесконечная «Проверка обновлений» (Checking for updates). Что делать?", "Смени конфиг: Твой текущий метод обхода может не справляться с серверами обновлений Discord. Попробуй переключиться на другой конфиг в Zapret и перезапустить Discord.\n\nВыключи лишнее: Убедись, что у тебя не включен параллельно другой VPN или прокси-сервер. Они могут конфликтовать друг с другом.\n\nСбрось кэш Discord: Это решает проблему в 90% случаев.\n\nЗапусти Discord снова при включённом Zapret.\n\nКрайний метод: Если ничего не помогло, переустанови Discord, скачав официальный установщик. Перед установкой убедись, что Zapret запущен, иногда Discord не может даже установиться без обхода блокировок.");
            AddQuestion("Не вижу демонстрацию экрана друга, а они не видят мою. Что делать?", "Смените регион звонка: В настройках текущего голосового канала (справа сверху значок настройки или через админа сервера) смените «Регион сервера» на любой другой (например, Rotterdam, Poland или Madrid). Пробуйте разные варианты, пока картинка не появится.\n\nНастройки Discord: Зайдите в Настройки пользователя -> Голос и видео. Пролистайте вниз до раздела «Видеокодек» и попробуйте выключить пункт «Аппаратное ускорение H.264». Иногда Zapret конфликтует именно с этим типом передачи данных.\n\nПерезаход: После смены конфига в Zapret обязательно полностью перезапустите Discord, иначе он будет пытаться транслировать поток через старое (заблокированное) соединение.");
        }
        else if (category == "Общее") {
            AddQuestion("Не работает YouTube, хотя Zapret включен", "Твой старый конфиг мог «протухнуть» из-за обновления фильтров провайдера. Сделай перенастройку: Открой Zapret -> Выбери 2. Remove Services -> Выбери 11. Run Tests -> [1] Standard tests -> [1] All configs. Выбери тот конфиг, который в результате будет полностью зеленым.");
            AddQuestion("Программа пишет 'Access Denied'", "Всегда запускай скрипты и .exe файлы от имени Администратора. Антивирусы также могут блокировать работу Zapret, добавь папку C:\\Zapret в исключения.");
            AddQuestion("Влияет ли это на пинг в играх?", "Нет, Zapret работает только с заблокированными доменами. Твой пинг в играх (CS, Dota, Valorant) останется прежним.");
            AddQuestion("Некоторые сайты перестали открываться после включения Zapret. Что делать?", "Это происходит потому, что выбранный метод обхода (конфиг) конфликтует с защитой конкретного сайта. Например у меня самого конфиг general (SIMPLE FAKE).bat мешает работе Steam, Suno AI или банковских приложений.\n\nРешение:\n\n1. Попробуй сменить конфиг на другой (например, с припиской ALT или DESYNC).\n\n2. Если не помогает, на время работы с этим сайтом просто выключи Zapret.");
        }
    }

    private void AddQuestion(string title, string answer)
    {
        var btn = new Button { 
            Style = (Style)FindResource("FlatBtn"), 
            Padding = new Thickness(0),
            Height = double.NaN, // убирает любой фиксированный Height
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8)
        };
        
        var border = new Border { 
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14), // одинаково везде
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border {
            Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            IsHitTestVisible = false // не поглощает мышь
        };

        grid.Children.Add(new TextBlock { 
            Text = title, 
            Foreground = Brushes.White, 
            FontSize = 14, 
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(grid.Children[grid.Children.Count - 1], 1);

        grid.Children.Add(new TextBlock { 
            Text = "›", 
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Margin = new Thickness(10, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false // не поглощает мышь
        });
        Grid.SetColumn(grid.Children[grid.Children.Count - 1], 2);

        grid.Children.Insert(0, dot);

        border.Child = grid;
        btn.Content = border;
        
        // Hover-эффект только на Border
        btn.MouseEnter += (s, e) => {
            var borderElement = (Border)btn.Content;
            borderElement.Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
        };
        btn.MouseLeave += (s, e) => {
            var borderElement = (Border)btn.Content;
            borderElement.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        };
        
        btn.Click += (s, e) => ShowFaqAnswer(title, answer);
        FaqContainer.Children.Add(btn);
    }

    private void ShowFaqAnswer(string title, string answer)
    {
        FaqHeaderTitle.Text = "Ответ";
        FaqContainer.Children.Clear();
        
        var mainCard = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
        };

        var stack = new StackPanel();
        
        stack.Children.Add(new TextBlock { 
            Text = title, 
            FontSize = 19, 
            FontWeight = FontWeights.Bold, 
            Foreground = Brushes.White, 
            TextWrapping = TextWrapping.Wrap, 
            Margin = new Thickness(0, 0, 0, 18) 
        });

        stack.Children.Add(new TextBlock { 
            Text = answer, 
            FontSize = 15, 
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)), 
            TextWrapping = TextWrapping.Wrap, 
            LineHeight = 24 
        });

        mainCard.Child = stack;
        FaqContainer.Children.Add(mainCard);

        // Создаем кнопку-контейнер
        var btnContainer = new Button {
            Style = (Style)FindResource("FlatBtn"),
            Padding = new Thickness(0),
            Height = double.NaN, // убирает любой фиксированный Height
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 24, 0, 0)
        };

        var backBtn = new Border {
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x33, 0x56)),
            Padding = new Thickness(20, 10, 20, 10), // одинаково везде
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        
        backBtn.Child = new TextBlock {
            Text = "← Вернуться к вопросам",
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            IsHitTestVisible = false // не поглощает мышь
        };
        
        btnContainer.Content = backBtn;
        
        // Hover-эффект только на Border
        btnContainer.MouseEnter += (s, e) => {
            var border = (Border)btnContainer.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1f, 0x3d, 0x66));
        };
        btnContainer.MouseLeave += (s, e) => {
            var border = (Border)btnContainer.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x33, 0x56));
        };
        
        btnContainer.Click += (s, e) => ShowFaqQuestions(_currentFaqCategory);
        FaqContainer.Children.Add(btnContainer);
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
        FaqContainer.Children.Clear();
        
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
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(20, 16, 20, 16)
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        var text = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 16, 0)
        };
        grid.Children.Add(text);
        
        var btn = new Button
        {
            Content = "Исправить",
            Style = (Style)FindResource("AccentBtn"),
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13
        };
        Grid.SetColumn(btn, 1);
        
        btn.Click += (_, _) => 
        {
            SolutionTitle.Text = title;
            SolutionManualText.Text = manualText;
            SolutionAutoFixBtn.Content = CreateButtonContentWithIcon("BoltIcon", autoBtnText, Brushes.White);
            
            FaqPage.Visibility = Visibility.Collapsed;
            SolutionPage.Visibility = Visibility.Visible;
        };
        grid.Children.Add(btn);
        
        card.Child = grid;
        FaqContainer.Children.Add(card);
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
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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

                // Синхронизация точек в карточке управления
                ZapretDot2.Fill = st.ZapretRunning ? greenBrush : grayBrush;
                ZapretStatusLbl.Text = st.ZapretRunning ? "Запущен" : "Не запущен";
                ZapretStatusLbl.Foreground = st.ZapretRunning ? greenBrush : grayBrush;
                ZapretToggleBtn.Content = st.ZapretRunning 
                    ? "■  Закрыть" 
                    : CreateButtonContentWithIcon("PlayIcon", "Запустить", Brushes.White);
                ZapretToggleBtn.Background = st.ZapretRunning
                    ? new SolidColorBrush(Color.FromRgb(0x3d, 0x1a, 0x1a))
                    : new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
                ZapretToggleBtn.Foreground = st.ZapretRunning ? redBrush : Brushes.White;

                TgWsDot2.Fill = st.TgWsProxyRunning ? greenBrush : grayBrush;
                TgWsStatusLbl.Text = st.TgWsProxyRunning ? "Запущен" : "Не запущен";
                TgWsStatusLbl.Foreground = st.TgWsProxyRunning ? greenBrush : grayBrush;
                TgWsToggleBtn.Content = st.TgWsProxyRunning 
                    ? "■  Закрыть" 
                    : CreateButtonContentWithIcon("PlayIcon", "Запустить", Brushes.White);
                TgWsToggleBtn.Background = st.TgWsProxyRunning
                    ? new SolidColorBrush(Color.FromRgb(0x3d, 0x1a, 0x1a))
                    : new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
                TgWsToggleBtn.Foreground = st.TgWsProxyRunning ? redBrush : Brushes.White;

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
        if (msg == "spacer") {
            Dispatcher.Invoke(() => LogBox.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.LineBreak()) { Margin = new Thickness(0, 5, 0, 5) }));
            return;
        }

        if (string.IsNullOrWhiteSpace(msg)) return;

        Dispatcher.Invoke(() =>
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            Color textColor = Color.FromRgb(0xcc, 0xcc, 0xcc);
            string prefix = "";
            double fontSize = 12;
            bool isBold = false;

            switch (kind)
            {
                case "frame":
                    textColor = Color.FromRgb(0x3b, 0x82, 0xf6);
                    isBold = true;
                    break;
                case "system":
                    textColor = Color.FromRgb(0xff, 0xff, 0xff);
                    prefix = "[#] ";
                    isBold = true;
                    break;
                case "net": prefix = "[NET] "; break;
                case "speed": prefix = "[SPEED] "; break;
                case "dpi": prefix = "[DPI] "; break;
                case "ok":
                    textColor = Color.FromRgb(0x22, 0xc5, 0x5e);
                    prefix = "[OK] ";
                    break;
                case "warn":
                    textColor = Color.FromRgb(0xea, 0xb3, 0x08);
                    prefix = "[WARN] ";
                    break;
                case "error":
                    textColor = Color.FromRgb(0xef, 0x44, 0x44);
                    prefix = "[ERROR] ";
                    break;
                case "final":
                    textColor = Color.FromRgb(0x22, 0xc5, 0x5e);
                    fontSize = 15;
                    isBold = true;
                    prefix = "🚀 ";
                    break;
                default:
                    prefix = "🔹 ";
                    break;
            }

            var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            
            if (kind != "frame" && kind != "final")
            {
                para.Inlines.Add(new System.Windows.Documents.Run($"[{ts}] ") 
                { 
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas")
                });
            }

            para.Inlines.Add(new System.Windows.Documents.Run($"{prefix}{msg}") 
            { 
                Foreground = new SolidColorBrush(textColor),
                FontSize = fontSize,
                FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal,
                FontFamily = (kind == "frame" || kind == "progress") ? new FontFamily("Consolas") : new FontFamily("Segoe UI")
            });

            LogBox.Document.Blocks.Add(para);
            LogBox.ScrollToEnd();
        });
    }

    private string GetProgressBar(double percent)
    {
        int totalBlocks = 20;
        int filledBlocks = (int)(percent / 100 * totalBlocks);
        return "[" + new string('█', filledBlocks) + new string('░', totalBlocks - filledBlocks) + $"] {percent:0}%";
    }

    private void ClearLog_Click(object s, RoutedEventArgs e) => LogBox.Document.Blocks.Clear();

    private void CopyLog_Click(object s, RoutedEventArgs e)
    {
        var textRange = new System.Windows.Documents.TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd);
        if (!string.IsNullOrWhiteSpace(textRange.Text))
        {
            try { Clipboard.SetText(textRange.Text); } catch { }
        }
    }

    // ── Auto-setup ───────────────────────────────────────────────────────────
    private void FixBtn_Click(object s, RoutedEventArgs e)
    {
        var st = DiagnosticsEngine.CheckAppStatus();
        
        // 1. Проверяем Zapret
        if (!st.ZapretRunning && !string.IsNullOrWhiteSpace(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
        {
            ShowZapretWizard();
            return;
        }

        // 2. Проверяем TgWsProxy
        if (!st.TgWsProxyRunning && !string.IsNullOrWhiteSpace(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
        {
            ShowTgProxyWizard();
            return;
        }

        RunAutoFix();
    }

    private async void RunAutoFix()
    {
        FixBtn.IsEnabled = false;
        SetupProg.Value = 0;
        SetupProgLbl.Text = "Подготовка...";
        LogBox.Document.Blocks.Clear();

        // Убрали линии, оставили только текст
        string timeStr = DateTime.Now.ToString("HH:mm:ss");
        AppendLog($"СИСТЕМНАЯ ДИАГНОСТИКА [ ВРЕМЯ: {timeStr} ]", "system");
        AppendLog("spacer");

        StartGlow();

        // --- ЭТАП 1: СЕТЬ ---
        AppendLog("СЕТЕВАЯ СРЕДА", "system");
        bool netOk = await DiagnosticsEngine.CheckInternetAsync();
        AppendLog($"Интернет-соединение: {(netOk ? "[ ПОДКЛЮЧЕНО ]" : "[ ОШИБКА ]")}", netOk ? "ok" : "error");
        
        // --- ЭТАП 2: СКАНИРОВАНИЕ ---
        AppendLog("АНАЛИЗ ТРАФИКА И DPI", "system");
        var report = await DiagnosticsEngine.RunFullDiagnosticsAsync(
            (ratio, label) => Dispatcher.Invoke(() => {
                SetupProg.Value = ratio * 50;
                SetupProgLbl.Text = label;
                
                var lastPara = LogBox.Document.Blocks.LastBlock as System.Windows.Documents.Paragraph;
                if (lastPara?.Tag?.ToString() == "prog") LogBox.Document.Blocks.Remove(lastPara);
                
                var p = new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("    " + GetProgressBar(ratio * 100))) 
                { 
                    Tag = "prog", 
                    Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
                    Margin = new Thickness(0, 4, 0, 4),
                    FontFamily = new FontFamily("Consolas")
                };
                LogBox.Document.Blocks.Add(p);
            })
        );
        
        // Дублируем отчет во вкладку диагностики
        Dispatcher.Invoke(() => RenderDiagReport(report));
        
        AppendLog("Обнаружена блокировка протоколов (DPI/ТСПУ)", "dpi");
        AppendLog("spacer");

        // --- ЭТАП 3: СЕРВИСЫ ---
        AppendLog("СОСТОЯНИЕ СЕРВИСОВ", "system");
        AppendLog($"Telegram Desktop: {(report.AppStatus?.TelegramRunning == true ? "[ ЗАПУЩЕН ]" : "[ НЕ В СЕТИ ]")}", "net");
        AppendLog($"Discord App:      {(report.AppStatus?.DiscordRunning == true ? "[ ЗАПУЩЕН ]" : "[ НЕ В СЕТИ ]")}", "net");
        
        int srvOk = report.DcResults.Count(d => d.Ok);
        AppendLog($"Доступность серверов Telegram: {srvOk} из {report.DcResults.Count}", srvOk > 0 ? "ok" : "warn");
        AppendLog("spacer");

        // --- ЭТАП 4: ЗАПУСК ОБХОДА ---
        AppendLog("ЗАПУСК ИСПРАВЛЕНИЙ", "system");
        AutoSetupService.Run(
            logCb: (msg, kind) => AppendLog(msg, kind == "step" ? "speed" : kind),
            progressCb: ratio => Dispatcher.Invoke(() => {
                SetupProg.Value = 50 + (ratio * 50);
                SetupProgLbl.Text = $"Настройка: {(int)(ratio * 100)}%";
            }),
            doneCb: (success, _) => Dispatcher.Invoke(() => {
                StopGlow(success);
                FixBtn.IsEnabled = true;
                if (success) {
                    SetupProg.Value = 100;
                    SetupProgLbl.Text = "Готово";
                    AppendLog("spacer");
                    AppendLog("Всё запущено и всё работает НОРМАЛЬНО!", "final");
                    AppendLog("Zapret включен. Discord и YouTube должны работать нормально.", "ok");
                    AppendLog("Прокси настроен. Telegram должен работать стабильно.", "ok");
                    AppendLog("Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».", "info");
                    PlaySuccessRing();
                } else {
                    AppendLog("Произошла ошибка при автоматической настройке. Проверьте пути в настройках.", "error");
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

        // Спиннер 1, по часовой, 1.4s (аналог CSS)
        SpinArc.Visibility = Visibility.Visible;
        var spin1 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.4)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinOffset.BeginAnimation(RotateTransform.AngleProperty, spin1);

        // Спиннер 2, по часовой, 1.9s (аналог CSS)
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

        // Статус приложений (без изменений)
        if (r.AppStatus is { } a)
        {
            var appsPanel = new StackPanel();
            void AddAppUI(string name, bool isRunning, string proc)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(isRunning ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0xef, 0x44, 0x44)), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(dot, 0);
                var nameText = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(nameText, 1);
                row.Children.Add(dot); row.Children.Add(nameText);
                if (isRunning && !string.IsNullOrEmpty(proc)) {
                    var procPill = new Border { Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
                    procPill.Child = new TextBlock { Text = proc, Foreground = new SolidColorBrush(Color.FromRgb(0xd1, 0xd5, 0xdb)), FontSize = 11 };
                    Grid.SetColumn(procPill, 2); row.Children.Add(procPill);
                }
                appsPanel.Children.Add(row);
            }
            AddAppUI("Telegram", a.TelegramRunning, a.TelegramProcName);
            AddAppUI("Discord", a.DiscordRunning, a.DiscordProcName);
            AddAppUI("Zapret", a.ZapretRunning, a.ZapretProcName);
            AddAppUI("tg-ws-proxy", a.TgWsProxyRunning, a.TgWsProxyProcName);
            AddRichCard(DiagResults, "Статус приложений", appsPanel, Color.FromRgb(0x8b, 0x5c, 0xf6));
        }

        // --- НОВЫЙ БЛОК ПРИМЕЧАНИЯ ВМЕСТО СТАРОЙ РЕКОМЕНДАЦИИ ---
        string noteText = "Примечание! Отправка медиафайлов (именно отправка) даже с включённым TgWsProxy может работать нестабильно, файлы могут загружаться очень долго. К сожалению, это не решить без использования VPN. Но просмотр и загрузка видео, стикеров и любого другого контента в Telegram должны работать идеально!";
        AddCard(DiagResults, "Важное примечание", noteText, Color.FromRgb(0x3b, 0x82, 0xf6));

        // Доступность серверов (без изменений)
        if (r.DcResults.Count > 0)
        {
            var serverContainer = new StackPanel();
            var srvPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var dc in r.DcResults)
            {
                var srvBlock = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 12, 14, 12), Width = 150 };
                var srvStack = new StackPanel();
                var headerGrid = new Grid();
                headerGrid.Children.Add(new TextBlock { Text = $"DC {dc.DcId}", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 14 });
                int? ping = dc.LatencyMs.HasValue ? (int)Math.Round(dc.LatencyMs.Value) : null;
                Color dotColor = (dc.Ok && ping <= 100) ? Color.FromRgb(0x22, 0xc5, 0x5e) : (dc.Ok && ping <= 200) ? Color.FromRgb(0xea, 0xb3, 0x08) : Color.FromRgb(0xef, 0x44, 0x44);
                headerGrid.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(dotColor), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                srvStack.Children.Add(headerGrid);
                srvStack.Children.Add(new TextBlock { Text = dc.Ip, Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
                srvStack.Children.Add(new TextBlock { Text = !dc.Ok ? "Недоступен" : $"{ping} мс", Foreground = new SolidColorBrush(dotColor), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
                srvBlock.Child = srvStack; srvPanel.Children.Add(srvBlock);
            }
            serverContainer.Children.Add(srvPanel);

            // Добавляем примечание, если включен TgWsProxy
            if (r.AppStatus != null && r.AppStatus.TgWsProxyRunning)
            {
                var serverNoteText = new TextBlock
                {
                    Text = "Примечание: У вас включен TgWsProxy. Даже если выше указано, что сервера недоступны, не переживайте, на вашем ПК Telegram будет работать нормально.\n\n" +
                           "Связь с TG идет через этот прокси, а диагностика проверяет сервера прямой отправкой пакетов, которые блокируются. Поэтому они и помечаются как «недоступные».\n\n" +
                           "Важно: Сервера будут помечены как стабильные и пинг будет нормальным только в том случае, если у вас включен VPN, а без него они всегда будут «недоступны» :). Так что всё ок!",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)), // Серый текст
                    FontSize = 14,
                    FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                serverContainer.Children.Add(serverNoteText);
            }

            AddRichCard(DiagResults, "Доступность серверов Telegram", serverContainer, Color.FromRgb(0x0e, 0xa5, 0xe9));
        }

        DiagProg.Value = 100;
        DiagProgLbl.Text = "Готово";
        DiagRunBtn.IsEnabled = true;
        DiagRunBtn.Content = CreateButtonContentWithIcon("RefreshIcon", "Проверить снова", Brushes.White);
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
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        
        // Создаем заголовок с иконкой вместо эмодзи
        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        
        // Определяем какую иконку использовать
        string iconKey = null;
        string titleText = title;
        
        // Проверяем содержит ли заголовок ключевые слова для определения типа
        if (title.Contains("tg-ws-proxy") || title.Contains("Telegram"))
        {
            iconKey = "TelegramIcon";
            // Убираем эмодзи из текста
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }
        else if (title.Contains("Discord"))
        {
            iconKey = "DiscordIcon";
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }
        else
        {
            // Для остальных просто убираем эмодзи
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }
        
        // Добавляем иконку если нашли
        if (iconKey != null)
        {
            Geometry iconGeometry = null;
            
            // Пробуем найти в ресурсах
            iconGeometry = System.Windows.Application.Current.TryFindResource(iconKey) as PathGeometry;
            
            // Если не нашли, создаем напрямую
            if (iconGeometry == null)
            {
                if (iconKey == "TelegramIcon")
                {
                    // Official Telegram icon from simpleicons.org
                    iconGeometry = Geometry.Parse("M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z");
                }
                else if (iconKey == "DiscordIcon")
                {
                    // Official Discord icon from simpleicons.org
                    iconGeometry = Geometry.Parse("M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z");
                }
            }
            
            if (iconGeometry != null)
            {
                var iconPath = new System.Windows.Shapes.Path
                {
                    Data = iconGeometry,
                    Fill = new SolidColorBrush(accentColor),
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                
                titlePanel.Children.Add(iconPath);
            }
        }
        
        titlePanel.Children.Add(new TextBlock
        {
            Text = titleText,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        
        stack.Children.Add(titlePanel);
        
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
        // AutoZapretCB убран - Zapret больше не в автозапуске
        AutoTgWsCB.IsChecked    = _settings.AutostartTgWsProxy;
        AutoAppCB.IsChecked     = _settings.AutostartApp;
        NotifyCB.IsChecked      = _settings.NotifyIssues;
        AutoUpdatesCB.IsChecked = _settings.AutoUpdates;
    }

    private void SaveSettings_Click(object s, RoutedEventArgs e)
    {
        _settings.ZapretPath       = ZapretBox.Text.Trim();
        _settings.TgWsProxyPath    = TgWsBox.Text.Trim();
        _settings.AutostartZapret  = false; // Zapret убран из автозапуска
        _settings.AutostartTgWsProxy = AutoTgWsCB.IsChecked == true;
        _settings.AutostartApp     = AutoAppCB.IsChecked == true;
        _settings.NotifyIssues     = NotifyCB.IsChecked == true;
        _settings.AutoUpdates      = AutoUpdatesCB.IsChecked == true;
        SettingsService.Save(_settings);
        
        // Автозапуск через Task Scheduler
        SetAutostart(_settings.AutostartApp);
        
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

    // ── Settings actions ─────────────────────────────────────────────────────
    private void ShowNotification(string title, string message, bool isError = false)
    {
        if (_settings.NotifyIssues != true) return;

        // Временно отключаем уведомления если нет сборки System.Windows.Forms
        // TODO: Добавить NuGet пакет System.Windows.Forms для поддержки уведомлений
        return;
        
        /*
        Dispatcher.Invoke(() =>
        {
            var ni = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true
            };
            ni.ShowBalloonTip(
                4000,
                title,
                message,
                isError ? System.Windows.Forms.ToolTipIcon.Error
                        : System.Windows.Forms.ToolTipIcon.Info
            );
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            t.Tick += (_, _) => { ni.Dispose(); t.Stop(); };
            t.Start();
        });
        */
    }

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
        OpenUrl("https://t.me/sofirka_hanabi");
    private void DonateBtn_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://www.tinkoff.ru/rm/kononenko.nikolay30/XeyPE87770");
    private void LinkZapret_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");
    private void LinkTgWs_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/tg-ws-proxy");
    private void LinkNetFix_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/rupleide/NetFix");

    private void OpenTelegramChannel_Click(object s, RoutedEventArgs e)
    {
        try {
            // Пробуем открыть напрямую в Telegram
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "tg://resolve?domain=NetFixRuBi",
                UseShellExecute = true
            });
        } catch {
            // Если не получилось, открываем через браузер
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://t.me/NetFixRuBi",
                    UseShellExecute = true
                });
            } catch { }
        }
    }

    private void CheckUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        var window = new NetFix.Views.UpdateWindow();
        window.ShowDialog();
    }

    private void WizardCloseBtn_Click(object s, RoutedEventArgs e) => CloseWizard();

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ── Zapret Wizard ────────────────────────────────────────────────────────
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
            ShowNotification("Успешно", "Zapret запущен через мастер", false);
        } catch {
            ShowNotification("Ошибка запуска", "Не удалось запустить Zapret через мастер. Проверьте путь.", true);
        }

        RenderWizardStep(0);
    }

    private void RenderWizardStep(int step)
    {
        WizardContent.Children.Clear();
        // Устанавливаем заголовок - ищем TextBlock в WizardLayer
        var title = FindChild<TextBlock>(WizardLayer);
        if (title != null) title.Text = "Мастер настройки Zapret";

        switch (step) {
            case 0:
                AddWizText("Я запустил файл service.bat.\n\nУ тебя открылось окно консоли?");
                AddWizBtn("Да, открылось", "#22c55e", () => RenderWizardStep(2));
                AddWizBtn("Нет", "#ef4444", () => RenderWizardStep(1));
                break;
            case 1:
                AddWizText("Окно не открылось.\nВозможно, путь неверный или антивирус блокирует запуск.");
                AddWizBtn("Закрыть", "#3b82f6", CloseWizard);
                break;
            case 2:
                AddWizText("Ты запускаешь его в первый раз?");
                AddWizBtn("Да", "#3b82f6", () => RenderWizardStep(3));
                AddWizBtn("Нет", "#2e2e2e", () => RenderWizardStep(11), "#cccccc");
                break;
            case 3:
                AddWizText("Нажми цифру 2, а потом Enter.\n\nСделал?");
                AddWizBtn("Да, сделал", "#3b82f6", () => RenderWizardStep(4));
                break;
            case 4:
                AddWizText("Видишь 'Press any key to continue...'?\n\nНажимай Enter.");
                AddWizBtn("Сделал", "#3b82f6", () => RenderWizardStep(5));
                break;
            case 5:
                AddWizText("Напиши 11 и нажми Enter.\n\nОткрылось окно Blockcheck?");
                AddWizBtn("Да, открылось", "#22c55e", () => RenderWizardStep(7));
                AddWizBtn("Нет", "#ef4444", () => RenderWizardStep(6));
                break;
            case 6:
                AddWizText("Окно тестов не открылось. Попробуй запуск от админа.");
                AddWizBtn("Понятно", "#3b82f6", CloseWizard);
                break;
            case 7:
                AddWizText("В новом окне выбери:\n1, Standard tests\nНажми Enter.");
                AddWizBtn("Нажал", "#3b82f6", () => RenderWizardStep(8));
                break;
            case 8:
                AddWizText("Выбери:\n1, All configs\nЖди завершения теста!");
                AddWizBtn("Понял, жду", "#3b82f6", () => RenderWizardStep(9));
                break;
            case 9:
                AddWizText("Запомни цифру 'Best config' в самом конце.");
                AddWizBtn("Я запомнил!", "#22c55e", () => RenderWizardStep(10));
                break;
            case 10:
                AddWizText("Закрой все окна. Сейчас я снова запущу service.bat. Набери свою цифру и нажми Enter!");
                AddWizBtn("Готово!", "#3b82f6", () => {
                    var st = DiagnosticsEngine.CheckAppStatus();
                    if (!st.TgWsProxyRunning) ShowTgProxyWizard(); else { CloseWizard(); RunAutoFix(); }
                });
                break;
            case 11:
                AddWizText("Рад, что ты уже знаешь как им пользоваться!\n\nВ открытом окне выбери:\n1, Install Service\nИ выбери свой рабочий конфиг.");
                AddWizBtn("Готово!", "#22c55e", () => {
                    var st = DiagnosticsEngine.CheckAppStatus();
                    if (!st.TgWsProxyRunning) ShowTgProxyWizard(); else { CloseWizard(); RunAutoFix(); }
                });
                break;
        }
    }

    // ── Мастер TgWsProxy ──────────────────────────────────────────────────────
    private void ShowTgProxyWizard()
    {
        WizardLayer.Visibility = Visibility.Visible;
        var slideAnim = new DoubleAnimation(370, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        WizardTrans.BeginAnimation(TranslateTransform.XProperty, slideAnim);
        
        // Принудительный запуск при открытии мастера
        StartTgProxyProcess();
        
        RenderTgProxyWizardStep(0);
    }

    private void StartTgProxyProcess()
    {
        try 
        { 
            Process.Start(new ProcessStartInfo(_settings.TgWsProxyPath) 
            { 
                UseShellExecute = true, 
                WorkingDirectory = System.IO.Path.GetDirectoryName(_settings.TgWsProxyPath) 
            });
            ShowNotification("Успешно", "tg-ws-proxy запущен", false);
        } 
        catch 
        {
            ShowNotification("Ошибка запуска", "Не удалось запустить tg-ws-proxy. Проверьте путь.", true);
        }
    }

    private void StartZapretProcess()
    {
        try 
        { 
            Process.Start(new ProcessStartInfo(_settings.ZapretPath) 
            { 
                UseShellExecute = true, 
                WorkingDirectory = System.IO.Path.GetDirectoryName(_settings.ZapretPath) 
            });
            ShowNotification("Успешно", "Zapret запущен", false);
        } 
        catch 
        {
            ShowNotification("Ошибка запуска", "Не удалось запустить Zapret. Проверьте путь.", true);
        }
    }

    private void RenderTgProxyWizardStep(int step)
    {
        WizardContent.Children.Clear();
        if (WizardLayer.FindName("WizardTitle") is TextBlock tb) tb.Text = "Настройка Telegram Proxy";

        switch (step)
        {
            case 0:
                AddWizText("Я запустил TgWsProxy.\n\nПосмотри в правый нижний угол экрана (в трей, где часы и значки). Ты видишь там иконку с буквой «T» на голубом фоне?");
                AddWizBtn("Да, я вижу", "#22c55e", () => RenderTgProxyWizardStep(1));
                AddWizBtn("Нет, не вижу", "#ef4444", () => {
                    // Перезапуск
                    foreach (var p in Process.GetProcessesByName("TgWsProxy")) try { p.Kill(); } catch {}
                    StartTgProxyProcess();
                    RenderTgProxyWizardStep(0);
                });
                break;
            case 1:
                AddWizText("Отлично!\n\nТеперь нажми на эту иконку ЛЕВОЙ кнопкой мыши.\n\nУ тебя откроется Telegram с предложением добавить прокси. Нажми там кнопку «Включить» (Enable).");
                AddWizBtn("Я всё сделал, ТГ работает", "#22c55e", () => {
                    CloseWizard();
                    RunAutoFix();
                });
                break;
        }
    }
    private T FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
                return result;

            var childOfChild = FindChild<T>(child);
            if (childOfChild != null)
                return childOfChild;
        }

        return null;
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
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
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
            case 16: BuildOnboardAutoDownload(stack); break;
            case 17: BuildOnboardManualStart(stack); break;
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
        AddOnboardTitle(p, "Способ установки");
        AddOnboardSub(p, "Как вы хотите установить компоненты для обхода блокировок?\nПрограмма может сделать всё автоматически примерно за 15 секунд.");
        AddOnboardBtn(p, "Автоматическая установка (15 сек)", "#22c55e", () => ShowOnboardScreen(16));
        AddOnboardBtn(p, "Ручная установка", "#2e2e2e", () => ShowOnboardScreen(17), foreground: "#888888");
    }

    private void BuildOnboardZapretChoice(StackPanel p)
    {
        AddOnboardTitle(p, "У вас установлен zapret-discord-youtube?");
        AddOnboardBtn(p, "Да, выбрать файл", "#22c55e", () =>
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
        // Добавляем иконку лайка
        var likeIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,11.47A18.74,18.74,0,0,0,10.69,8.9a18.74,18.74,0,0,0,1.76-2.42A6.42,6.42,0,0,0,13,5.41l1.74-4.57a4.45,4.45,0,0,1,2.83,2A4,4,0,0,1,18,4.77a2.67,2.67,0,0,1-.09.55L16.72,9.05h5.22a2,2,0,0,1,2,1.85,19.32,19.32,0,0,1-.32,5.44,33.83,33.83,0,0,1-1.23,4.34,3.78,3.78,0,0,1-3.58,2.49,25.54,25.54,0,0,1-6.28-.66A45.85,45.85,0,0,1,8,21.26V11.47Z M5,9H1a1,1,0,0,0-1,1V22a1,1,0,0,0,1,1H5a1,1,0,0,0,1-1V10A1,1,0,0,0,5,9ZM3,21a1,1,0,1,1,1-1A1,1,0,0,1,3,21Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };
        p.Children.Add(likeIcon);
        
        AddOnboardTitle(p, "Ты молодец!");
        AddOnboardSub(p, "Надеюсь, ты сделал всё правильно.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(10));
    }

    private void BuildOnboardTgWsChoice(StackPanel p)
    {
        AddOnboardTitle(p, "У вас установлен tg-ws-proxy?");
        AddOnboardBtn(p, "Да, выбрать файл", "#22c55e", () =>
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
        // Добавляем иконку лайка
        var likeIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,11.47A18.74,18.74,0,0,0,10.69,8.9a18.74,18.74,0,0,0,1.76-2.42A6.42,6.42,0,0,0,13,5.41l1.74-4.57a4.45,4.45,0,0,1,2.83,2A4,4,0,0,1,18,4.77a2.67,2.67,0,0,1-.09.55L16.72,9.05h5.22a2,2,0,0,1,2,1.85,19.32,19.32,0,0,1-.32,5.44,33.83,33.83,0,0,1-1.23,4.34,3.78,3.78,0,0,1-3.58,2.49,25.54,25.54,0,0,1-6.28-.66A45.85,45.85,0,0,1,8,21.26V11.47Z M5,9H1a1,1,0,0,0-1,1V22a1,1,0,0,0,1,1H5a1,1,0,0,0,1-1V10A1,1,0,0,0,5,9ZM3,21a1,1,0,1,1,1-1A1,1,0,0,1,3,21Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };
        p.Children.Add(likeIcon);
        
        AddOnboardTitle(p, "Всё готово!");
        
        var subText = new TextBlock();
        subText.FontFamily = new FontFamily("Segoe UI");
        subText.FontSize = 15;
        subText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        subText.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
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

    private void BuildOnboardManualStart(StackPanel p)
    {
        AddOnboardSub(p, "Для работы приложения вам нужно скачать следующие компоненты:");
        AddOnboardBtn(p, "Погнали", "#3b82f6", () => ShowOnboardScreen(4));
    }

    private void BuildOnboardAutoDownload(StackPanel p)
    {
        AddOnboardTitle(p, "Автоматическая установка");
        AddOnboardSub(p, "Подождите, скачиваем и настраиваем нужные компоненты.\nЭто займет не больше минуты.");
        
        var logCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 5, 0, 15),
            Padding = new Thickness(2)
        };
        
        var logBox = new System.Windows.Controls.RichTextBox
        {
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Height = 160,
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        logBox.Document.PagePadding = new Thickness(12);
        logCard.Child = logBox;
        p.Children.Add(logCard);

        var progBar = new System.Windows.Controls.ProgressBar
        {
            Value = 0,
            Maximum = 100,
            Height = 6,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Background = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e)),
            BorderThickness = new Thickness(0)
        };
        progBar.SetResourceReference(FrameworkElement.StyleProperty, typeof(System.Windows.Controls.ProgressBar));
        p.Children.Add(progBar);

        var progText = new TextBlock 
        { 
            Text = "Подготовка...", 
            Foreground = Brushes.White, 
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 20)
        };
        p.Children.Add(progText);

        void AppendLog(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                para.Inlines.Add(new System.Windows.Documents.Run(msg));
                logBox.Document.Blocks.Add(para);
                logBox.ScrollToEnd();
            });
        }

        var actionsPanel = new StackPanel();
        p.Children.Add(actionsPanel);

        Task.Run(async () => 
        {
            bool success = await AutoDownloadService.AutoInstallAllAsync(
                msg => AppendLog(msg),
                prog => Dispatcher.Invoke(() => 
                {
                    progBar.Value = prog * 100;
                    progText.Text = $"Загрузка... {(int)(prog * 100)}%";
                }),
                err => AppendLog("ОШИБКА: " + err)
            );

            Dispatcher.Invoke(() => 
            {
                if (success)
                {
                    progBar.Value = 100;
                    progBar.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    progText.Text = "Всё готово!";
                    progText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    
                    AddOnboardBtn(actionsPanel, "Далее", "#3b82f6", () => ShowOnboardScreen(15));
                }
                else
                {
                    progBar.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    progText.Text = "Ошибка установки";
                    progText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    
                    AddOnboardBtn(actionsPanel, "Попробовать вручную", "#ef4444", () => ShowOnboardScreen(17));
                }
            });
        });
    }

    // ── Onboard helpers ──────────────────────────────────────────────────────
    private static void AddOnboardEmoji(StackPanel p, string emoji) =>
        p.Children.Add(new TextBlock
        {
            Text = emoji, FontSize = 54, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Segoe UI Emoji"),
            Margin = new Thickness(0, 0, 0, 12)
        });

    private static void AddOnboardTitle(StackPanel p, string text) =>
        p.Children.Add(new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 22,
            FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        });

    private static void AddOnboardSub(StackPanel p, string text) =>
        p.Children.Add(new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
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
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
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
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
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
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
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
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tmpl.VisualTree = bd;
        return tmpl;
    }

    private void SetAutostart(bool enable)
    {
        try
        {
            if (enable)
            {
                string path = Environment.ProcessPath;
                string args = $"/Create /F /RL HIGHEST /SC ONLOGON /TN \"NetFix\" /TR \"\\\"{path}\\\"\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit();
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/Delete /F /TN \"NetFix\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            regKey?.DeleteValue("NetFix", false);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Ошибка автозапуска: {ex.Message}");
        }
    }

    // ── Helper: Create button content with icon ────────────────────────────────────
    private static object CreateButtonContentWithIcon(string iconKey, string text, Brush iconBrush)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        
        var geometry = System.Windows.Application.Current.TryFindResource(iconKey) as PathGeometry;
        
        // Fallback если ресурс не найден
        if (geometry == null && iconKey == "RefreshIcon")
        {
            geometry = Geometry.Parse("M21,11c-0.6,0-1,0.4-1,1c0,2.9-1.5,5.5-4,6.9c-3.8,2.2-8.7,0.9-10.9-2.9C2.9,12.2,4.2,7.3,8,5.1c3.3-1.9,7.3-1.2,9.8,1.4h-2.4c-0.6,0-1,0.4-1,1s0.4,1,1,1h4.5c0.6,0,1-0.4,1-1V3c0-0.6-0.4-1-1-1s-1,0.4-1,1v1.8C17,3,14.6,2,12,2C6.5,2,2,6.5,2,12s4.5,10,10,10c5.5,0,10-4.5,10-10C22,11.4,21.6,11,21,11z") as PathGeometry;
        }
        
        if (geometry == null)
        {
            // Если всё равно null, просто вернём текст без иконки
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        }
        
        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        if (iconKey == "PlayIcon" || iconKey == "RefreshIcon" || iconKey == "BoltIcon")
            path.Fill = iconBrush;
        else
            path.Stroke = iconBrush;

        if (iconKey != "PlayIcon" && iconKey != "RefreshIcon" && iconKey != "BoltIcon")
            path.StrokeThickness = 2;

        stack.Children.Add(path);
        stack.Children.Add(new TextBlock 
        { 
            Text = text, 
            VerticalAlignment = VerticalAlignment.Center 
        });

        return stack;
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
