using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetFix.Models;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;

namespace NetFix.Views;

public partial class ImportModWindow : Window
{
    private readonly ModMeta _meta;

    public ImportModWindow(ModMeta meta)
    {
        _meta = meta;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = _meta.Name,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)),
            Margin = new Thickness(0, 0, 0, 2),
        });

        ContentPanel.Children.Add(new TextBlock
        {
            Text = $"v{_meta.Version} от {_meta.Author}",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 0, 0, 10),
        });

        ContentPanel.Children.Add(new TextBlock
        {
            Text = $"\"{_meta.Description}\"",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        var hasBat = _meta.Type == "strategy";
        var hasList = _meta.Type == "list";
        var hasHosts = _meta.Type == "hosts";

        var okColor = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        var dimColor = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

        ContentPanel.Children.Add(new TextBlock
        {
            Text = "Содержит:",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var items = new[]
        {
            (hasBat, "Стратегия запуска (.bat)"),
            (hasList, "Список доменов"),
            (hasHosts, "Список Hosts"),
            (false, "Бинарник"),
        };

        foreach (var (present, label) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = present ? "✓" : "✗",
                Foreground = present ? okColor : dimColor,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                Foreground = present ? new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)) : dimColor,
            });
            ContentPanel.Children.Add(row);
        }

        var warningColor = hasBat ? Color.FromRgb(0xf9, 0x7a, 0x2e) : Color.FromRgb(0xea, 0xb3, 0x08);
        var warningText = hasBat
            ? "Содержит .bat файл. Активируйте только если доверяете автору."
            : "Файл не из официального каталога. Будьте осторожны.";

        ContentPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(16, warningColor.R, warningColor.G, warningColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, warningColor.R, warningColor.G, warningColor.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock
            {
                Text = warningText,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                Foreground = new SolidColorBrush(warningColor),
                TextWrapping = TextWrapping.Wrap,
            }
        });
    }

    private void ImportBtn_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void CancelBtn_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragMove();
    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
