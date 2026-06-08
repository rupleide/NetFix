using System.Diagnostics;
using System.Windows;

namespace NetFix.Views;

public partial class DonateWindow : Window
{
    public DonateWindow()
    {
        InitializeComponent();
    }

    private void CloseBtn_Click(object s, RoutedEventArgs e) => Close();

    private void SbpBtn_Click(object s, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(
            "https://www.tinkoff.ru/rm/r_eELpDmupvc.SCiWRkVJON/bgKkD30493")
            { UseShellExecute = true });
        Close();
    }

    private void TonBtn_Click(object s, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(
            "https://app.tonkeeper.com/transfer/UQCx8X4z86Jej2hc8l_IVni8e0Q8uDHhC8_PJ2zymxngVc2Q")
            { UseShellExecute = true });
        Close();
    }
}
