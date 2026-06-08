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
        TypeLabel.Text = _modType == ModType.Strategy
            ? "Тип: .bat стратегия"
            : "Тип: лист доменов";

        AuthorBox.Text = Environment.UserName;

        if (_modType == ModType.Strategy)
            StrategySection.Visibility = Visibility.Visible;
        else
            ListSection.Visibility = Visibility.Visible;
    }

    private void SelectBat_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Batch files (*.bat)|*.bat|All files (*.*)|*.*",
            Title = "Выберите .bat файл стратегии",
        };

        if (dialog.ShowDialog() == true)
        {
            _selectedBatPath = dialog.FileName;
            BatPathDisplay.Text = Path.GetFileName(dialog.FileName);
        }
    }

    private void CreateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            System.Windows.MessageBox.Show("Введите название мода");
            return;
        }

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

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragMove();
    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
