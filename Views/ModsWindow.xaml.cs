using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetFix.Models;
using NetFix.Services;
using NetFix.Services.Mods;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace NetFix.Views;

public partial class ModsWindow : Window
{
    private readonly AppSettings _settings;
    private List<ModEntry> _allMods = [];
    private bool _isStrategyTab = true;
    private bool _loaded;
    private string _searchText = "";

    public ModsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        ModScanner.EnsureDirectories();
        RefreshMods();
        UpdateStatus();
    }

    private void RefreshMods()
    {
        var activeStrategy = _settings.ActiveStrategyMods ?? [];
        var activeLists = _settings.ActiveListMods ?? [];
        _allMods = ModScanner.ScanAll(activeStrategy, activeLists);
        RefreshLists();
    }

    private void RefreshLists()
    {
        var activeNames = _isStrategyTab
            ? (_settings.ActiveStrategyMods ?? [])
            : (_settings.ActiveListMods ?? []);

        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? _allMods
            : _allMods.Where(m => m.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var activeMods = filtered
            .Where(m => (_isStrategyTab ? m.Type == ModType.Strategy : m.Type == ModType.List) && m.IsActive)
            .OrderBy(m => { var idx = activeNames.IndexOf(ModScanner.GetModDirName(m)); return idx < 0 ? 999 : idx; })
            .ToList();

        var availableMods = filtered
            .Where(m => (_isStrategyTab ? m.Type == ModType.Strategy : m.Type == ModType.List) && !m.IsActive)
            .ToList();

        AvailableList.ItemsSource = null;
        AvailableList.ItemsSource = availableMods;

        ActiveList.ItemsSource = null;
        ActiveList.ItemsSource = activeMods;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var isStrategy = _isStrategyTab;
        var typeName = isStrategy ? "Стратегий" : "Листов";
        var allCount = _allMods.Count(m => isStrategy ? m.Type == ModType.Strategy : m.Type == ModType.List);
        var activeCount = ActiveList.Items.Count;
        var availCount = AvailableList.Items.Count;

        StatusText.Text = $"{typeName}: {allCount} | Активных: {activeCount}";

        if (AvailableCount is not null)
            AvailableCount.Text = availCount.ToString();
        if (ActiveCount is not null)
            ActiveCount.Text = activeCount.ToString();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        _isStrategyTab = TabStrategies.IsChecked == true;
        if (_loaded)
            RefreshLists();
    }

    private void MoveRightBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AvailableList.SelectedItem is ModEntry mod)
            ToggleModActive(mod, true);
    }

    private void MoveLeftBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveList.SelectedItem is ModEntry mod)
            ToggleModActive(mod, false);
    }

    private void ToggleModActive(ModEntry mod, bool activate)
    {
        mod.IsActive = activate;

        var list = _isStrategyTab ? _settings.ActiveStrategyMods : _settings.ActiveListMods;
        var dirName = ModScanner.GetModDirName(mod);

        if (activate)
        {
            if (!list.Contains(dirName))
                list.Add(dirName);
        }
        else
        {
            list.Remove(dirName);
        }

        SaveSettings();
        RefreshLists();
    }

    private ModEntry? _dragMod;
    private bool _dragFromActive;

    private void ListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            var item = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (item?.DataContext is ModEntry mod)
            {
                _dragMod = mod;
                _dragFromActive = listBox == ActiveList;
                DragDrop.DoDragDrop(listBox, _dragMod, DragDropEffects.Move);
            }
        }
    }

    private void ListBox_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(ModEntry)) is ModEntry ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void AvailableList_Drop(object sender, DragEventArgs e)
    {
        if (_dragMod is null) return;
        if (_dragFromActive)
            ToggleModActive(_dragMod, false);
        _dragMod = null;
    }

    private void ActiveList_Drop(object sender, DragEventArgs e)
    {
        if (_dragMod is null) return;
        if (!_dragFromActive)
            ToggleModActive(_dragMod, true);
        _dragMod = null;
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        return element as ListBoxItem;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseBtn_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }

    private void CreateModBtn_Click(object sender, RoutedEventArgs e)
    {
        var type = _isStrategyTab ? ModType.Strategy : ModType.List;
        var dialog = new CreateModWindow(type);
        dialog.Owner = this;

        if (dialog.ShowDialog() == true && dialog.CreatedEntry is not null)
        {
            _allMods.Add(dialog.CreatedEntry);
            SaveSettings();
            RefreshLists();
        }
    }

    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            Filter = "NetFix Mod (*.netfix-mod)|*.netfix-mod|All files (*.*)|*.*",
            Title = "Выберите файл мода",
        };

        if (openDialog.ShowDialog() == true)
            await ImportModAsync(openDialog.FileName);
    }

    private async Task ImportModAsync(string zipPath)
    {
        var (meta, readError) = await ModPackager.ReadModMetaFromArchive(zipPath);
        if (meta is null || readError is not null)
        {
            ShowError(readError ?? "Не удалось прочитать файл мода");
            return;
        }

        var importDialog = new ImportModWindow(meta);
        importDialog.Owner = this;

        if (importDialog.ShowDialog() == true)
        {
            var activeStrategy = _settings.ActiveStrategyMods ?? [];
            var activeLists = _settings.ActiveListMods ?? [];
            var (entry, importError) = await ModPackager.ImportAsync(zipPath, activeStrategy, activeLists);

            if (entry is not null)
            {
                _allMods.Add(entry);
                SaveSettings();
                RefreshLists();
                ShowSuccess($"Мод '{meta.Name}' импортирован");
            }
            else
            {
                ShowError(importError ?? "Ошибка импорта");
            }
        }
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();

        if (!_isStrategyTab)
        {
            var activeLists = _allMods
                .Where(m => m.Type == ModType.List && m.IsActive)
                .ToList();

            var (success, error) = ModActivator.ApplyListMods(activeLists);
            if (!success)
                ShowError(error ?? "Ошибка применения");
            else
                ShowSuccess("Списки доменов применены");
        }
        else
        {
            ShowSuccess("Порядок стратегий сохранён");
        }

        UpdateStatus();
    }

    private void AnyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteModBtn.IsEnabled = AvailableList.SelectedItem is not null || ActiveList.SelectedItem is not null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text ?? "";
        if (_loaded)
            RefreshLists();
    }

    private void RefreshMods_Click(object sender, RoutedEventArgs e) => RefreshMods();

    private async void ExportSingleMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ModEntry mod)
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "Mods", "export");
            Directory.CreateDirectory(dir);
            var result = await ModPackager.ExportAsync(mod, dir);
            StatusText.Text = $"✅ Экспортировано: {System.IO.Path.GetFileName(result)}";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        }
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = AvailableList.SelectedItem as ModEntry;
        if (selected is null)
            selected = ActiveList.SelectedItem as ModEntry;
        if (selected is null) return;

        try
        {
            if (Directory.Exists(selected.FolderPath))
                Directory.Delete(selected.FolderPath, true);
        }
        catch { }

        _allMods.Remove(selected);
        SaveSettings();
        RefreshLists();
        ShowSuccess($"Мод '{selected.Name}' удалён");
    }

    private void SaveSettings() => SettingsService.Save(_settings);

    private void ShowError(string message)
    {
        StatusText.Text = $"❌ {message}";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
    }

    private void ShowSuccess(string message)
    {
        StatusText.Text = $"✅ {message}";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
    }
}


