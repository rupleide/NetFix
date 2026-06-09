using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Net.NetworkInformation;
using NetFix.Services;

using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Application    = System.Windows.Application;
using Color          = System.Windows.Media.Color;

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

    public TrayPopup()
    {
        InitializeComponent();

        VersionLabel.Text = AppVersion.Display;

        Deactivated += (_, _) => SafeClose();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
        await UpdatePingAsync();
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

    private void ExitBtn_Click(object s, MouseButtonEventArgs e) =>
        Application.Current.Shutdown();
}
