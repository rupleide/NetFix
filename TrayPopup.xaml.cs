using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Net.NetworkInformation;
using NetFix.Services;

using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Application    = System.Windows.Application;
using Color          = System.Windows.Media.Color;
using Cursors        = System.Windows.Input.Cursors;
using FontFamily     = System.Windows.Media.FontFamily;

namespace NetFix;

public partial class TrayPopup : Window
{
    private static readonly SolidColorBrush _brushGreen    = new(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush _brushRed      = new(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush _brushRunning  = new(Color.FromArgb(30, 0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush _brushStopped  = new(Color.FromArgb(25, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush _brushHover    = new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _brushGray     = new(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly SolidColorBrush _brushGreenDot = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush _brushTransparent = new(Colors.Transparent);

    public static DateTime LastClosedTime { get; private set; }

    public TrayPopup()
    {
        InitializeComponent();

        VersionLabel.Text = AppVersion.Display;

        Deactivated += (_, _) => SafeClose();

        Loaded += OnLoaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        LastClosedTime = DateTime.UtcNow;
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
        await UpdatePingAsync();
        await UpdateDnsSectionAsync();
    }

    private void SafeClose()
    {
        if (!IsMouseOver)
            Dispatcher.BeginInvoke(Close);
    }


    private void UpdateStatus()
    {
        var s = DiagnosticsEngine.CheckAppStatus();
        ApplyServiceState(
            s.ZapretRunning,
            ZapretDot, ZapretLabel,
            ZapretBtn, ZapretBtnIcon, ZapretBtnText);
        ApplyServiceState(
            s.TgWsProxyRunning,
            TgWsDot, TgWsLabel,
            TgWsBtn, TgWsBtnIcon, TgWsBtnText);
    }

    private static void ApplyServiceState(
        bool running,
        System.Windows.Shapes.Ellipse dot,
        System.Windows.Controls.TextBlock label,
        Border btn,
        System.Windows.Controls.TextBlock icon,
        System.Windows.Controls.TextBlock text)
    {
        if (running)
        {
            dot.Fill  = _brushGreenDot;
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD8));
            btn.Background   = _brushRunning;
            icon.Text        = "■";
            icon.Foreground  = _brushRed;
            text.Text        = "Остановить";
            text.Foreground  = _brushRed;
        }
        else
        {
            dot.Fill  = _brushGray;
            label.Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));
            btn.Background   = _brushStopped;
            icon.Text        = "▶";
            icon.Foreground  = _brushGreen;
            text.Text        = "Запустить";
            text.Foreground  = _brushGreen;
        }
    }

    private async Task UpdatePingAsync()
    {
        try
        {
            using var ping = new Ping();
            long total = 0; int count = 0;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var r = await ping.SendPingAsync("1.1.1.1", 1000);
                    if (r.Status == IPStatus.Success) { total += r.RoundtripTime; count++; }
                }
                catch { }
            }
            if (count > 0)
            {
                long avg = total / count;
                PingValue.Text = $"{avg} мс";
                PingValue.Foreground = avg < 50
                    ? _brushGreenDot
                    : avg < 100
                        ? new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08))
                        : _brushRed;
            }
        }
        catch { PingValue.Text = "— мс"; }
    }


    private void ZapretBtn_Enter(object s, MouseEventArgs e) => ZapretBtn.Background = _brushHover;
    private void ZapretBtn_Leave(object s, MouseEventArgs e)
    {
        var st = DiagnosticsEngine.CheckAppStatus();
        ZapretBtn.Background = st.ZapretRunning ? _brushRunning : _brushStopped;
    }

    private void TgWsBtn_Enter(object s, MouseEventArgs e) => TgWsBtn.Background = _brushHover;
    private void TgWsBtn_Leave(object s, MouseEventArgs e)
    {
        var st = DiagnosticsEngine.CheckAppStatus();
        TgWsBtn.Background = st.TgWsProxyRunning ? _brushRunning : _brushStopped;
    }


    private async Task UpdateDnsSectionAsync()
    {
        try
        {
            var settings = SettingsService.Load();
            if (!settings.EffectiveQuickDnsInTray)
            {
                DnsSectionBorder.Visibility = Visibility.Collapsed;
                return;
            }

            DnsSectionBorder.Visibility = Visibility.Visible;
            var (activeName, isDhcp, configuredDns) = await Task.Run(() =>
            {
                var dnsSettings = DnsManagerService.GetPhysicalDnsSettings();
                var name = DnsManagerService.DetectActiveDnsName(settings);
                return (name, dnsSettings.IsDhcp, dnsSettings.ConfiguredDns);
            });

            CurrentDnsLabel.Text = activeName;
            PopulateDnsServersList(isDhcp, configuredDns, settings);
        }
        catch
        {
            DnsSectionBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateDnsServersList(bool isDhcp, List<string> configuredDns, Models.AppSettings settings)
    {
        DnsServersList.Children.Clear();
        var allServers = DnsManagerService.GetAllServers(settings);

        foreach (var server in allServers)
        {
            bool isActive = server.Primary == "dhcp"
                ? (isDhcp || configuredDns.Count == 0)
                : (!isDhcp && configuredDns.Contains(server.Primary));

            var itemBorder = new Border
            {
                Background = isActive ? new SolidColorBrush(Color.FromArgb(20, 0x22, 0xC5, 0x5E)) : _brushTransparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Tag = server
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new TextBlock
            {
                Text = server.Name,
                Foreground = isActive ? _brushGreen : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD8)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var mark = new TextBlock
            {
                Text = isActive ? "✔" : "",
                Foreground = _brushGreen,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            Grid.SetColumn(titleBlock, 0);
            Grid.SetColumn(mark, 1);
            grid.Children.Add(titleBlock);
            grid.Children.Add(mark);
            itemBorder.Child = grid;

            itemBorder.MouseEnter += (_, _) =>
            {
                if (!isActive) itemBorder.Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
            };
            itemBorder.MouseLeave += (_, _) =>
            {
                if (!isActive) itemBorder.Background = _brushTransparent;
            };

            itemBorder.MouseLeftButtonUp += async (s, e) =>
            {
                e.Handled = true;
                itemBorder.IsHitTestVisible = false;
                titleBlock.Text = "Применение...";

                bool ok = await DnsManagerService.SetDnsServerAsync(server.Primary, server.Secondary, server.DohTemplate);
                await Task.Delay(300);

                await UpdateDnsSectionAsync();
                AnimateDnsList(false);

                if (Application.Current.MainWindow is MainWindow mainWin)
                {
                    mainWin.Dispatcher.Invoke(() => mainWin.RefreshDnsServersList());
                }
            };

            DnsServersList.Children.Add(itemBorder);
        }
    }

    private void DnsMenuToggleBtn_Enter(object s, MouseEventArgs e) =>
        DnsMenuToggleBtn.Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));

    private void DnsMenuToggleBtn_Leave(object s, MouseEventArgs e) =>
        DnsMenuToggleBtn.Background = _brushTransparent;

    private bool _isDnsAnimating;

    private void AnimateDnsList(bool expand)
    {
        if (_isDnsAnimating) return;
        _isDnsAnimating = true;

        double bottom = Top + ActualHeight;

        if (expand)
        {
            DnsServersScroll.Visibility = Visibility.Visible;
            DnsServersScroll.Height = 0;
            DnsExpandArrow.Text = "▲";

            DnsServersList.Measure(new System.Windows.Size(230, double.PositiveInfinity));
            double targetH = Math.Min(Math.Max(DnsServersList.DesiredSize.Height + 4, 36), 160);

            var anim = new DoubleAnimation(0, targetH, new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            EventHandler? renderHandler = null;
            renderHandler = (s, e) =>
            {
                Top = bottom - ActualHeight;
            };
            CompositionTarget.Rendering += renderHandler;

            anim.Completed += (s, e) =>
            {
                CompositionTarget.Rendering -= renderHandler;
                DnsServersScroll.BeginAnimation(HeightProperty, null);
                DnsServersScroll.Height = double.NaN;
                DnsServersScroll.MaxHeight = 160;
                UpdateLayout();
                Top = bottom - ActualHeight;
                _isDnsAnimating = false;
            };

            DnsServersScroll.BeginAnimation(HeightProperty, anim);
        }
        else
        {
            DnsExpandArrow.Text = "▼";
            double currentH = DnsServersScroll.ActualHeight;

            var anim = new DoubleAnimation(currentH, 0, new Duration(TimeSpan.FromMilliseconds(160)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            EventHandler? renderHandler = null;
            renderHandler = (s, e) =>
            {
                Top = bottom - ActualHeight;
            };
            CompositionTarget.Rendering += renderHandler;

            anim.Completed += (s, e) =>
            {
                CompositionTarget.Rendering -= renderHandler;
                DnsServersScroll.BeginAnimation(HeightProperty, null);
                DnsServersScroll.Visibility = Visibility.Collapsed;
                DnsServersScroll.Height = double.NaN;
                UpdateLayout();
                Top = bottom - ActualHeight;
                _isDnsAnimating = false;
            };

            DnsServersScroll.BeginAnimation(HeightProperty, anim);
        }
    }

    private void DnsMenuToggleBtn_Click(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool isShown = DnsServersScroll.Visibility == Visibility.Visible && DnsServersScroll.ActualHeight > 5;
        AnimateDnsList(!isShown);
    }

    private void ConfigBtn_Enter(object s, MouseEventArgs e) =>
        ConfigBtn.Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    private void ConfigBtn_Leave(object s, MouseEventArgs e) =>
        ConfigBtn.Background = _brushTransparent;

    private void OpenBtn_Enter(object s, MouseEventArgs e) =>
        OpenBtn.Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    private void OpenBtn_Leave(object s, MouseEventArgs e) =>
        OpenBtn.Background = _brushTransparent;

    private void ExitBtn_Enter(object s, MouseEventArgs e) =>
        ExitBtn.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xF8, 0x71, 0x71));
    private void ExitBtn_Leave(object s, MouseEventArgs e) =>
        ExitBtn.Background = _brushTransparent;


    private async void ZapretBtn_Click(object s, MouseButtonEventArgs e)
    {
        ZapretBtn.IsHitTestVisible = false;
        ZapretBtnText.Text = "...";

        var running = DiagnosticsEngine.CheckAppStatus().ZapretRunning;
        if (running)
        {
            foreach (var p in Process.GetProcessesByName("winws")) try { p.Kill(); } catch { }
            await Task.Delay(500);
        }
        else
        {
            var settings = SettingsService.Load();
            if (!string.IsNullOrEmpty(settings.ZapretPath) && System.IO.File.Exists(settings.ZapretPath))
            {
                var cache = ZapretConfigService.LoadCache();
                if (cache?.CurrentConfig is { Length: > 0 })
                    await ZapretConfigService.ApplyConfigAsync(settings.ZapretPath, cache.CurrentConfig);
                else
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{settings.ZapretPath}\"") { UseShellExecute = false, CreateNoWindow = true });
            }
            await Task.Delay(1000);
        }

        UpdateStatus();
        ZapretBtn.IsHitTestVisible = true;
    }

    private async void TgWsBtn_Click(object s, MouseButtonEventArgs e)
    {
        TgWsBtn.IsHitTestVisible = false;
        TgWsBtnText.Text = "...";

        var running = DiagnosticsEngine.CheckAppStatus().TgWsProxyRunning;
        if (running)
        {
            foreach (var p in Process.GetProcessesByName("TgWsProxy")) try { p.Kill(); } catch { }
            await Task.Delay(500);
        }
        else
        {
            var settings = SettingsService.Load();
            if (!string.IsNullOrEmpty(settings.TgWsProxyPath) && System.IO.File.Exists(settings.TgWsProxyPath))
                Process.Start(new ProcessStartInfo(settings.TgWsProxyPath) { UseShellExecute = true });
            await Task.Delay(1000);
        }

        UpdateStatus();
        TgWsBtn.IsHitTestVisible = true;
    }


    private void ConfigBtn_Click(object s, MouseButtonEventArgs e)
    {
        Close();
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var main = Application.Current.MainWindow as MainWindow;
            if (main == null) return;
            main.ShowFromTray();
            var settings = SettingsService.Load();
            if (!string.IsNullOrEmpty(settings.ZapretPath) && System.IO.File.Exists(settings.ZapretPath))
            {
                var w = new Views.ZapretConfigWindow(settings.ZapretPath, testMode: false) { Owner = main };
                w.ShowDialog();
            }
        });
    }

    private void OpenBtn_Click(object s, MouseButtonEventArgs e)
    {
        Close();
        Application.Current.Dispatcher.BeginInvoke(() =>
            (Application.Current.MainWindow as MainWindow)?.ShowFromTray());
    }

    private void ExitBtn_Click(object s, MouseButtonEventArgs e)
    {
        Close();
        (Application.Current.MainWindow as MainWindow)?.ForceExit();
    }
}
