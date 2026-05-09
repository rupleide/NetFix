using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Net.NetworkInformation;
using NetFix.Services;

// Алиасы для разрешения конфликтов имен
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color       = System.Windows.Media.Color;
using Brushes     = System.Windows.Media.Brushes;
using Application = System.Windows.Application;

namespace NetFix;

public partial class TrayPopup : Window
{
    private bool _closing = false;

    public TrayPopup()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Обновляем статус сервисов
        UpdateServicesStatus();
        
        // Обновляем пинг
        await UpdatePingAsync();
    }

    private void UpdateServicesStatus()
    {
        var status = DiagnosticsEngine.CheckAppStatus();
        
        // Zapret
        if (status.ZapretRunning)
        {
            ZapretStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            ZapretStatusText.Text = "Zapret: Работает";
            ZapretStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        }
        else
        {
            ZapretStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            ZapretStatusText.Text = "Zapret: Остановлен";
            ZapretStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
        
        // TgWsProxy
        if (status.TgWsProxyRunning)
        {
            TgWsStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            TgWsStatusText.Text = "TgWsProxy: Работает";
            TgWsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        }
        else
        {
            TgWsStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            TgWsStatusText.Text = "TgWsProxy: Остановлен";
            TgWsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
    }

    private async Task UpdatePingAsync()
    {
        try
        {
            using var ping = new Ping();
            long total = 0;
            int count = 0;
            
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync("1.1.1.1", 1000);
                    if (reply.Status == IPStatus.Success)
                    {
                        total += reply.RoundtripTime;
                        count++;
                    }
                }
                catch { }
            }

            if (count > 0)
            {
                long avg = total / count;
                PingText.Text = $"Пинг: {avg} мс";
                PingText.Foreground = new SolidColorBrush(avg < 50
                    ? Color.FromRgb(0x22, 0xc5, 0x5e)
                    : avg < 100
                        ? Color.FromRgb(0xea, 0xb3, 0x08)
                        : Color.FromRgb(0xef, 0x44, 0x44));
            }
        }
        catch
        {
            PingText.Text = "Пинг: — мс";
        }
    }

    private void OnDeactivated(object s, EventArgs e)
    {
        if (!_closing) Close();
    }

    // ── Hover effects ────────────────────────────────────────────────────────
    private void RestartZapretBtn_Hover(object s, MouseEventArgs e) =>
        RestartZapretBtn.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    private void RestartZapretBtn_Leave(object s, MouseEventArgs e) =>
        RestartZapretBtn.Background = Brushes.Transparent;
    
    private void ChangeConfigBtn_Hover(object s, MouseEventArgs e) =>
        ChangeConfigBtn.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    private void ChangeConfigBtn_Leave(object s, MouseEventArgs e) =>
        ChangeConfigBtn.Background = Brushes.Transparent;
    
    private void QuickDiagBtn_Hover(object s, MouseEventArgs e) =>
        QuickDiagBtn.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    private void QuickDiagBtn_Leave(object s, MouseEventArgs e) =>
        QuickDiagBtn.Background = Brushes.Transparent;
    
    private void OpenBtn_Hover(object s, MouseEventArgs e) =>
        OpenBtn.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    private void OpenBtn_Leave(object s, MouseEventArgs e) =>
        OpenBtn.Background = Brushes.Transparent;
    
    private void ExitBtn_Hover(object s, MouseEventArgs e) =>
        ExitBtn.Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x1a, 0x1a));
    private void ExitBtn_Leave(object s, MouseEventArgs e) =>
        ExitBtn.Background = Brushes.Transparent;

    // ── Click handlers ───────────────────────────────────────────────────────
    private void RestartZapretBtn_Click(object s, MouseButtonEventArgs e)
    {
        _closing = true;
        Close();
        
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            var main = Application.Current.MainWindow as MainWindow;
            if (main == null) return;
            
            // Останавливаем Zapret
            var status = DiagnosticsEngine.CheckAppStatus();
            if (status.ZapretRunning)
            {
                foreach (var p in Process.GetProcessesByName("winws"))
                    try { p.Kill(); } catch { }
                foreach (var p in Process.GetProcessesByName("winws.exe"))
                    try { p.Kill(); } catch { }
                
                await Task.Delay(1000);
            }
            
            // Запускаем Zapret
            var settings = SettingsService.Load();
            if (!string.IsNullOrEmpty(settings.ZapretPath) && System.IO.File.Exists(settings.ZapretPath))
            {
                Process.Start(new ProcessStartInfo(settings.ZapretPath) { UseShellExecute = true });
            }
        });
    }

    private void ChangeConfigBtn_Click(object s, MouseButtonEventArgs e)
    {
        _closing = true;
        Close();
        
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var main = Application.Current.MainWindow as MainWindow;
            if (main == null) return;
            
            // Открываем главное окно
            if (!main.IsVisible) main.Show();
            if (main.WindowState == WindowState.Minimized)
                main.WindowState = WindowState.Normal;
            main.Activate();
            main.Focus();
            main.StartAuroraTimer();
            
            // Открываем окно выбора конфига
            var settings = SettingsService.Load();
            if (!string.IsNullOrEmpty(settings.ZapretPath) && System.IO.File.Exists(settings.ZapretPath))
            {
                var configWindow = new Views.ZapretConfigWindow(settings.ZapretPath, testMode: false);
                configWindow.Owner = main;
                configWindow.ShowDialog();
            }
        });
    }

    private void QuickDiagBtn_Click(object s, MouseButtonEventArgs e)
    {
        _closing = true;
        Close();
        
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var main = Application.Current.MainWindow as MainWindow;
            if (main == null) return;
            
            // Открываем главное окно и запускаем диагностику
            if (!main.IsVisible) main.Show();
            if (main.WindowState == WindowState.Minimized)
                main.WindowState = WindowState.Normal;
            main.Activate();
            main.Focus();
            main.StartAuroraTimer();
            
            // Переключаемся на вкладку диагностики
            main.ShowDiagnosticsTab();
        });
    }

    private void OpenBtn_Click(object s, MouseButtonEventArgs e)
    {
        _closing = true;
        Close();
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var main = Application.Current.MainWindow as MainWindow;
            if (main == null) return;
            if (!main.IsVisible) main.Show();
            if (main.WindowState == WindowState.Minimized)
                main.WindowState = WindowState.Normal;
            main.Activate();
            main.Focus();
            // Запускаем Aurora анимацию при показе окна из трея
            main.StartAuroraTimer();
        });
    }

    private void ExitBtn_Click(object s, MouseButtonEventArgs e)
    {
        _closing = true;
        Application.Current.Shutdown();
    }
}
