using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NetFix.Models;
using NetFix.Services;
using NetFix.Services.Mods;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace NetFix.Views;

public partial class CreateModWindow : Window
{
    public ModEntry? CreatedEntry { get; private set; }

    private readonly ModType _modType;
    private string? _selectedBatPath;

    public CreateModWindow(ModType modType)
    {
        _modType = modType;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_modType == ModType.Strategy)
        {
            SubtitleText.Text = ".bat стратегия";
            StrategySection.Visibility = Visibility.Visible;
        }
        else
        {
            SubtitleText.Text = "Список доменов";
            ListSection.Visibility = Visibility.Visible;
        }

        AuthorBox.Text = Environment.UserName;
        VersionBox.Text = "1.0";
    }

    private void VersionBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
            if (!char.IsAsciiDigit(c))
                e.Handled = true;
    }

    private void VersionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var digits = new string(VersionBox.Text.Where(char.IsAsciiDigit).Take(2).ToArray());
        var rebuilt = digits.Length switch
        {
            0 => "",
            1 => "v" + digits,
            _ => $"v{digits[0]}.{digits[1]}",
        };

        if (VersionBox.Text != rebuilt)
        {
            int caret = VersionBox.CaretIndex;
            VersionBox.Text = rebuilt;
            VersionBox.CaretIndex = Math.Clamp(caret, 0, rebuilt.Length);
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        int len = NameBox.Text.Length;
        NameCounter.Text = $"{len} / 20";
        NameCounter.Foreground = len >= 18
            ? new System.Windows.Media.SolidColorBrush(Color.FromRgb(0xf9, 0x7a, 0x2e))
            : new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void AuthorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        int len = AuthorBox.Text.Length;
        AuthorCounter.Text = $"{len} / 10";
        AuthorCounter.Foreground = len >= 9
            ? new System.Windows.Media.SolidColorBrush(Color.FromRgb(0xf9, 0x7a, 0x2e))
            : new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void DescBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        int len = DescBox.Text.Length;
        DescCounter.Text = $"{len} / 100";

        DescCounter.Foreground = len >= 90
            ? new System.Windows.Media.SolidColorBrush(Color.FromRgb(0xf9, 0x7a, 0x2e))
            : new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void SelectBat_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Batch files (*.bat)|*.bat",
            Title = "Выберите .bat файл стратегии",
        };

        if (dialog.ShowDialog() == true)
        {
            _selectedBatPath = dialog.FileName;
            BatPathDisplay.Text = Path.GetFileName(dialog.FileName);
            BatPathDisplay.Foreground = new System.Windows.Media.SolidColorBrush(
                Color.FromRgb(0xcc, 0xcc, 0xcc));
        }
    }

    private void CreateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowValidationHint("Введите название мода");
            NameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(VersionBox.Text))
        {
            ShowValidationHint("Введите версию");
            VersionBox.Focus();
            return;
        }

        ValidationHint.Visibility = Visibility.Collapsed;

        var description = DescBox.Text;
        if (description.Length > 100)
            description = description[..100];

        var settings = SettingsService.Load();
        var activeStrategy = settings.ActiveStrategyMods ?? [];
        var activeLists = settings.ActiveListMods ?? [];

        string? listContent = null;
        if (_modType == ModType.List)
        {
            var raw = ListTextBox.Text;
            if (!string.IsNullOrWhiteSpace(raw))
                listContent = DomainListImporter.ParseText(raw);
        }

        var entry = ModPackager.CreateNewMod(
            NameBox.Text.Trim(),
            AuthorBox.Text.Trim(),
            VersionBox.Text.Trim(),
            description,
            _modType,
            _selectedBatPath,
            listContent,
            activeStrategy,
            activeLists
        );

        CreatedEntry = entry;
        DialogResult = true;
        Close();
    }

    private void ShowValidationHint(string message)
    {
        ValidationHint.Text = message;
        ValidationHint.Visibility = Visibility.Visible;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragMove();
    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
