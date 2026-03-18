using System.Windows;
using System.Threading;

// Алиас для разрешения конфликта имен
using Application = System.Windows.Application;

namespace NetFix;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "NetFix_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show("Приложение NetFix уже запущено!", "NetFix",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
