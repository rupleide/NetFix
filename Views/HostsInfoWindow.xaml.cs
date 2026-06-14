using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace NetFix.Views
{
    public partial class HostsInfoWindow : Window
    {
        public HostsInfoWindow()
        {
            InitializeComponent();
        }

        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private void MinBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
        }

        private void CodeBorder_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText("ipconfig /flushdns");
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = "/k \"ipconfig /flushdns\"",
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    UseShellExecute = true
                };
                Process.Start(psi);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Не удалось открыть cmd: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        }
    }
}
