using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

// Алиасы для разрешения конфликтов имен
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color       = System.Windows.Media.Color;
using Brushes     = System.Windows.Media.Brushes;
using Application = System.Windows.Application;

namespace NetFix;

public partial class TrayPopup : Window
{
    private bool _closing = false;

    public TrayPopup() => InitializeComponent();

    private void OnDeactivated(object s, EventArgs e)
    {
        if (!_closing) Close();
    }

    private void OpenBtn_Hover(object s, MouseEventArgs e) =>
        OpenBtn.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    private void OpenBtn_Leave(object s, MouseEventArgs e) =>
        OpenBtn.Background = Brushes.Transparent;
    private void ExitBtn_Hover(object s, MouseEventArgs e) =>
        ExitBtn.Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x1a, 0x1a));
    private void ExitBtn_Leave(object s, MouseEventArgs e) =>
        ExitBtn.Background = Brushes.Transparent;

    private void OpenBtn_Click(object s, MouseButtonEventArgs e)
    {
        _closing = true;
        Close();
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var main = Application.Current.MainWindow;
            if (main == null) return;
            if (!main.IsVisible) main.Show();
            if (main.WindowState == WindowState.Minimized)
                main.WindowState = WindowState.Normal;
            main.Activate();
            main.Focus();
        });
    }

    private void ExitBtn_Click(object s, MouseButtonEventArgs e)
    {
        _closing = true;
        Application.Current.Shutdown();
    }
}
