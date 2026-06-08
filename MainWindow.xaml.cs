using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Interop;
using Microsoft.Win32;
using System.Windows.Threading;
using NetFix.Models;
using NetFix.Services;
using NetFix.Views;
using System.Runtime.InteropServices;

// Алиасы для разрешения конфликтов между WPF и WinForms
using Color        = System.Windows.Media.Color;
using Brushes      = System.Windows.Media.Brushes;
using FontFamily   = System.Windows.Media.FontFamily;
using Clipboard    = System.Windows.Clipboard;
using Cursors      = System.Windows.Input.Cursors;
using Orientation  = System.Windows.Controls.Orientation;
using RadioButton  = System.Windows.Controls.RadioButton;
using Button       = System.Windows.Controls.Button;
using TextBox      = System.Windows.Controls.TextBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Brush        = System.Windows.Media.Brush;
using Panel        = System.Windows.Controls.Panel;
using Size         = System.Windows.Size;
using Path         = System.IO.Path;

namespace NetFix;

public partial class MainWindow : Window
{
    // ── Windows API для работы с системным треем ─────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);
    
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);
    
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr handle, IntPtr min, IntPtr max);
    
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
    
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint TB_BUTTONCOUNT = 0x0418;
    
    // ── State ────────────────────────────────────────────────────────────────
    private AppSettings _settings = SettingsService.Load();
    private bool _settingsOpen = false;
    private DispatcherTimer _monitorTimer = null!;
    private System.Windows.Forms.NotifyIcon _trayIcon = null!;
    private DispatcherTimer? _longCheckTimer = null;
    private bool _checkInProgress = false;
    private bool _autoFixRunning = false;
    private Views.ZapretConfigWindow? _configWindow = null;

    // ── Игра: состояние ──────────────────────────────────────────────────────
    private DispatcherTimer? _gameTimer;
    private List<NoteEntry> _pendingNotes = new();
    private List<NoteEntry> _activeNotes = new();
    private int _gameScore, _gameCombo, _totalNotes, _hitNotes;
    private Stopwatch _gameClock = new();
    private double _currentFallSec = 1.6;
    private double _judgeVisibleUntil = -1;
    private readonly double[] _hitZoneFlashUntil = new double[4];
    private int _missCount = 0;
    private int _consecutiveMisses = 0;
    private bool _gameOverTriggered = false;
    private DispatcherTimer? _auroraGameTimer;
    private int _lastComboAuraLevel = 0; // 0=нет, 1=x5, 2=x10, 3=x20+
    private Color _currentComboColor = Color.FromRgb(0xff, 0xd7, 0x00);

    private bool _halfwayTriggered = false;
    private bool _dangerModeActive = false;
    private DispatcherTimer? _dangerPulseTimer;
    private int _perfectStreak = 0; // подряд PERFECT
    private readonly HashSet<int> _activeLanes = [];
    private readonly HashSet<int> _hitLanesThisFrame = [];

    // Перформанс: очередь эффектов вне GameTick
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _effectQueue = new();
    private DispatcherTimer? _effectTimer;

    // Discord Rich Presence
    private readonly DiscordRpcService _discord = new();
    private DateTime _gameStartDateTime;
    private DispatcherTimer? _discordGameTimer;
    private DispatcherTimer? _discordEditorTimer;
    private int _maxCombo = 0;
    private string _currentTrackTitle = "";
    private bool _isInGame = false; // Флаг для надежной проверки состояния игры

    // Перформанс: кэшируем кисти чтобы не создавать каждый кадр
    private readonly SolidColorBrush[] _laneBrushes = LaneColors
        .Select(c => new SolidColorBrush(c)).ToArray();
    private readonly LinearGradientBrush[] _noteGradients = LaneColors
        .Select(c => new LinearGradientBrush(
            Color.FromArgb(80, c.R, c.G, c.B),
            Color.FromArgb(20, c.R, c.G, c.B), 90))
        .ToArray();

    // Визуал: таймер для звёздочек
    private DispatcherTimer? _starTimer;
    private int _starBurst = 0; // сколько ещё burst-итераций осталось
    private static readonly string[] StarChars = { "★", "✦", "✧" };

    private System.Windows.Media.MediaPlayer _editorPlayer = new();
    private System.Windows.Media.MediaPlayer _previewPlayer = new();
    private bool _previewPlaying = false;
    private List<NoteEntry> _recordedNotes = new();
    private string? _editorMp3Path;
    private bool _editorRecording = false;

    private static readonly string LevelsDir =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetFix", "levels");

    private static readonly string OsuLevelsDir =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetFix", "osu_levels");

    private static readonly string BuiltInTracksDir =
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tracks");
    
    // Игровой оверлей поверх главного экрана
    private Border? _gameOverlayPanel = null;
    private bool _gameOverlayActive = false;

    private UIElement? _oszReturnView = null;
    
    // Таймер обратного отсчёта перед игрой
    private DispatcherTimer? _countdownTimer = null;
    
    // Параметры последней игры для перезапуска
    private List<NoteEntry>? _lastGameNotes = null;
    private string? _lastGameMp3Path = null;
    private string? _lastGameTitle = null;
    private double _lastGameBpm = 0;
    private string? _pendingOszPath;

    // ── Поиск и сортировка треков ────────────────────────────────────────────
    private ICollectionView? _userTracksView;
    private ICollectionView? _osuTracksView;
    private string _userSearchText = string.Empty;
    private string _osuSearchText = string.Empty;
    private string _statsSearchText = string.Empty;

    private bool _settingsLoaded; // защита от срабатывания событий при загрузке

    // ── Network Monitor ──────────────────────────────────────────────────────
    private DispatcherTimer _netTimer = null!;
    private DispatcherTimer _pingTimer = null!;
    private long _lastBytesReceived = 0;
    private long _lastBytesSent = 0;
    private bool _speedTestDone = false;
    
    // Для перцентильного расчёта
    private readonly List<double> _dlSamples = new();
    private readonly List<double> _ulSamples = new();
    private double _finalDownloadMbps = 0;
    private double _finalUploadMbps = 0;
    
    // Aurora state - математическая модель
    private DispatcherTimer _auroraTimer = null!;
    private double _t = 0;
    private double _splitProgress = 0; // 0 = покой, 1 = шторм
    private double _splitTarget = 0;
    private double _colorProgress = 0; // 0 = база, 1 = результат (успех/ошибка)
    private double _colorTarget = 0;
    private bool _finalSuccess = true;
    
    // Начальные цвета (Синий, Фиолетовый, Индиго)
    private Color[] _baseColors = new Color[] 
    {
        Color.FromRgb(59, 130, 246),   // Синий
        Color.FromRgb(139, 92, 246),   // Фиолетовый
        Color.FromRgb(79, 70, 229)     // Индиго
    };
    private Color _successColor = Color.FromRgb(34, 197, 94);   // Зелёный
    private Color _errorColor = Color.FromRgb(239, 68, 68);     // Красный

    // ── Init ─────────────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        InitTray();
        
        // Aurora animation, 30fps, синхронизировано с рендером
        _auroraTimer = new DispatcherTimer(DispatcherPriority.Render);
        _auroraTimer.Interval = TimeSpan.FromMilliseconds(33); // 30fps
        _auroraTimer.Tick += (s, e) =>
        {
            _t += 0.05; // Скорость течения времени
            
            // Плавное приближение текущих значений к целевым (интерполяция)
            _splitProgress += (_splitTarget - _splitProgress) * 0.05;
            _colorProgress += (_colorTarget - _colorProgress) * 0.03;
            
            // Обновляем каждый блоб с индивидуальными параметрами
            // AuroraRect1 (центральный синий) - больше движения по горизонтали
            UpdateBlob(AuroraRect1, 0, 0.50, 0.25, 0.15, 0.06, 0.56, 0.44, 0, 1.2, 0);
            UpdateBlob(AuroraRect2, 1, 0.0, 0.0, 0.10, 0.08, 1.30, 0.96, 2.1, 0.5, 0);
            UpdateBlob(AuroraRect3, 2, 1.0, 0.95, 0.09, 0.09, 1.10, 1.44, 4.2, 2.8, 0);
        };
        _auroraTimer.Start();
        
        // Стоп/старт при сворачивании
        this.StateChanged += (s, e) =>
        {
            if (this.WindowState == WindowState.Minimized)
                _auroraTimer.Stop();
            else
                _auroraTimer.Start();
        };
    }

    // ── Aurora Helper Methods ────────────────────────────────────────────────
    private void UpdateBlob(System.Windows.Shapes.Rectangle rect, int index, double bx, double by, double ampX, double ampY, double freqX, double freqY, double phX, double phY, byte baseAlpha)
    {
        var brush = (RadialGradientBrush)rect.Fill;
        double ease = EaseInOut(_splitProgress);
        double colorEase = EaseInOut(_colorProgress);
        
        // Амплитуда: в покое почти 0 (0.03), в активе - умеренная
        double currentAmpX = Lerp(0.03, ampX, ease);
        double currentAmpY = Lerp(0.03, ampY, ease);
        
        // Вычисляем новые координаты центра
        double cx = bx + Math.Sin(_t * freqX + phX) * currentAmpX;
        double cy = by + Math.Cos(_t * freqY + phY) * currentAmpY;
        
        // Радиус остаётся постоянным (без пульсации и изменения размера)
        double baseRadius = index == 0 ? 0.32 : 0.22; // Базовые размеры из XAML
        
        // Цвет, меняем ВСЕ GradientStop'ы плавно от базового к результату
        Color targetColor = _finalSuccess ? _successColor : _errorColor;
        Color currentColor = LerpColor(_baseColors[index], targetColor, colorEase);
        
        // Применяем
        brush.Center = new System.Windows.Point(cx, cy);
        brush.GradientOrigin = new System.Windows.Point(cx, cy);
        brush.RadiusX = baseRadius;
        brush.RadiusY = baseRadius;
        
        // Меняем цвет ВСЕХ GradientStop'ов, сохраняя их оригинальную прозрачность
        foreach (var stop in brush.GradientStops)
        {
            byte originalAlpha = stop.Color.A;
            stop.Color = Color.FromArgb(originalAlpha, currentColor.R, currentColor.G, currentColor.B);
        }
    }
    
    // Вспомогательные функции математики
    private double Lerp(double a, double b, double t) => a + (b - a) * t;
    
    private double EaseInOut(double t) => t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;
    
    private Color LerpColor(Color c1, Color c2, double t) => Color.FromRgb(
        (byte)(c1.R + (c2.R - c1.R) * t),
        (byte)(c1.G + (c2.G - c1.G) * t),
        (byte)(c1.B + (c2.B - c1.B) * t));

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.DiscordRpcEnabled)
            _discord.Initialize();
        UpdateMainGridClip();
        LoadSettingsToPanel();
        _settingsLoaded = true; // после этого можно обрабатывать события чекбоксов

        if (!SettingsService.IsOnboarded)
            ShowOnboarding();
        else
        {
            FadeIn();
            _ = WriteStartupLogAsync();
            CheckInternetOnStart();
            StartActiveAppsMonitor();
            
            // Инициализируем файлы версий для уже установленных компонентов
            InitializeVersionFiles();
            
            if (_settings.AutoUpdates)
            {
                CheckForUpdatesBackgroundAsync();
            }
        }
        LoadFaqItems();
        UpdateSelectedConfigDisplay();
        
        // Инициализируем монитор сети
        InitNetworkMonitor();

        // Автозапуск TgWsProxy при старте
        if (_settings.AutostartTgWsProxy
            && !string.IsNullOrEmpty(_settings.TgWsProxyPath)
            && File.Exists(_settings.TgWsProxyPath)
            && Process.GetProcessesByName("TgWsProxy").Length == 0)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
                Dispatcher.Invoke(() => StartTgWsProxyWithActivation()),
                TaskScheduler.Default);
        }
        
        // Обработчик кликов по ссылкам в логе
        LogBox.PreviewMouseLeftButtonDown += LogBox_PreviewMouseLeftButtonDown;
        LogBox.PreviewMouseMove += LogBox_PreviewMouseMove;

        if (_settings.StartMinimizedToTray)
        {
            // Показываем на 1 кадр чтобы WPF инициализировал GPU-композитор,
            // затем сразу прячем — иначе после Show() из трея будет software rendering
            Opacity = 0;
            Show();
            await Task.Delay(50);
            Hide();
            Opacity = 1;
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMainGridClip();
    }

    private void UpdateMainGridClip()
    {
        var rect = new RectangleGeometry(
            new Rect(0, 0, MainGrid.ActualWidth, MainGrid.ActualHeight),
            11, 11);
        MainGrid.Clip = rect;
    }

    // ── Tray Icon ─────────────────────────────────────────────────────────────
    private void InitTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Visible = true,
            Text = "NetFix"
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                ShowFromTray();
            else if (e.Button == System.Windows.Forms.MouseButtons.Right)
                ShowTrayMenu();
        };
    }

    private void ShowTrayMenu()
    {
        // Закрываем старое меню если открыто
        foreach (Window win in System.Windows.Application.Current.Windows)
            if (win is TrayPopup) { win.Close(); return; }

        var popup = new TrayPopup { Owner = this };

        // Показываем за экраном чтобы WPF успел посчитать размер
        popup.Left = -9999;
        popup.Top  = -9999;
        popup.Show();
        popup.UpdateLayout();

        var pos    = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(pos);

        double popupW = popup.ActualWidth;
        double popupH = popup.ActualHeight;

        // DPI-коэффициент: физические пиксели → WPF device-independent пиксели
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;

        double cursorX = pos.X / sx;
        double cursorY = pos.Y / sy;

        double workLeft   = screen.WorkingArea.Left   / sx;
        double workRight  = screen.WorkingArea.Right  / sx;
        double workTop    = screen.WorkingArea.Top    / sy;
        double workBottom = screen.WorkingArea.Bottom / sy;

        // Позиция: верхним правым углом у курсора
        double left = cursorX - popupW;
        double top  = cursorY - popupH;

        // Не даём вылезти за края экрана
        if (left < workLeft) left = workLeft + 4;
        if (left + popupW > workRight) left = workRight - popupW - 4;
        if (top < workTop) top = workTop + 4;
        if (top + popupH > workBottom) top = workBottom - popupH;

        popup.Left = left;
        popup.Top  = top;
    }

    public void ShowFromTray()
    {
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _auroraTimer?.Start();
        _monitorTimer?.Start();
        _netTimer?.Start();
        _pingTimer?.Start();
    }
    
    public void StartAuroraTimer()
    {
        _auroraTimer?.Start();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ── Автоматический клик по иконке TgWsProxy в трее ───────────────────────
    private async Task ActivateTgWsProxyAsync()
    {
        try
        {
            // Ждём, пока TgWsProxy создаст иконку в трее и запустит прокси
            await Task.Delay(2500);
            
            // Читаем конфигурацию TgWsProxy и формируем правильную ссылку
            string? proxyUrl = await Task.Run(() => GetTgWsProxyUrl());
            
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                // Открываем ссылку в Telegram
                Process.Start(new ProcessStartInfo(proxyUrl) { UseShellExecute = true });
            }
            else
            {
                // Если не удалось прочитать конфигурацию, пробуем кликнуть по трею
                await Task.Run(() => ClickTrayIconByProcess("TgWsProxy"));
            }
        }
        catch (Exception ex)
        {
            // Игнорируем ошибки - если не получилось активировать автоматически,
            // пользователь может кликнуть по иконке в трее вручную
            System.Diagnostics.Debug.WriteLine($"Failed to activate TgWsProxy: {ex.Message}");
        }
    }
    
    private string? GetTgWsProxyUrl()
    {
        try
        {
            // TgWsProxy хранит конфигурацию в %APPDATA%\TgWsProxy\config.json
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configPath = Path.Combine(appData, "TgWsProxy", "config.json");
            
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                // Простой парсинг JSON (без зависимостей)
                string? host = ExtractJsonValue(json, "host");
                string? port = ExtractJsonValue(json, "port");
                string? secret = ExtractJsonValue(json, "secret");
                
                if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port) && !string.IsNullOrEmpty(secret))
                {
                    // Формируем tg://proxy ссылку с префиксом dd (как в исходном коде TgWsProxy)
                    // Если host = 127.0.0.1 или localhost, используем 127.0.0.1
                    string linkHost = host == "0.0.0.0" ? "127.0.0.1" : host;
                    return $"tg://proxy?server={linkHost}&port={port}&secret=dd{secret}";
                }
            }
            
            // Если конфига нет, возвращаем null чтобы попробовать клик по трею
            return null;
        }
        catch
        {
            return null;
        }
    }
    
    private string? ExtractJsonValue(string json, string key)
    {
        try
        {
            // Простой парсинг: ищем "key": "value" или "key": number
            string pattern = $"\"{key}\"\\s*:\\s*\"?([^,\"}}]+)\"?";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }
        catch { }
        return null;
    }
    
    private void ClickTrayIconByProcess(string processName)
    {
        try
        {
            // Находим окно системного трея
            IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
            if (taskbarHandle == IntPtr.Zero) return;

            IntPtr trayHandle = FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);
            if (trayHandle == IntPtr.Zero) return;

            IntPtr sysPagerHandle = FindWindowEx(trayHandle, IntPtr.Zero, "SysPager", null);
            if (sysPagerHandle == IntPtr.Zero) return;

            IntPtr notificationAreaHandle = FindWindowEx(sysPagerHandle, IntPtr.Zero, "ToolbarWindow32", null);
            if (notificationAreaHandle == IntPtr.Zero) return;

            // Получаем количество кнопок в трее
            int buttonCount = (int)SendMessage(notificationAreaHandle, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            
            if (buttonCount > 0)
            {
                // Получаем координаты области трея
                if (GetClientRect(notificationAreaHandle, out RECT rect))
                {
                    // Кликаем в центр области трея (где обычно находится последняя добавленная иконка)
                    int centerX = (rect.Right - rect.Left) / 2;
                    int centerY = (rect.Bottom - rect.Top) / 2;
                    
                    // Преобразуем в экранные координаты
                    POINT pt = new POINT { X = centerX, Y = centerY };
                    ClientToScreen(notificationAreaHandle, ref pt);
                    
                    // Перемещаем курсор и кликаем
                    SetCursorPos(pt.X, pt.Y);
                    Thread.Sleep(100);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, pt.X, pt.Y, 0, UIntPtr.Zero);
                    Thread.Sleep(50);
                    mouse_event(MOUSEEVENTF_LEFTUP, pt.X, pt.Y, 0, UIntPtr.Zero);
                }
            }
        }
        catch
        {
            // Игнорируем ошибки
        }
    }

    // ── Service Control Handlers ───────────────────────────────────────────────────
    private async void ServicesBtn_Click(object s, RoutedEventArgs e)
    {
        // Останавливаем игру/редактор при открытии панели сервисов
        StopGame();
        StopEditorRecording();
        
        ServicesLayer.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(50, 0, TimeSpan.FromMilliseconds(280));
        anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
        ServicesTrans.BeginAnimation(TranslateTransform.XProperty, anim);
        ServicesPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        
        // Обновляем статус версий компонентов
        await UpdateVersionStatusAsync();
    }

    private void CloseServicesPanel()
    {
        var anim = new DoubleAnimation(0, 50, TimeSpan.FromMilliseconds(220));
        anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        anim.Completed += (_, _) => ServicesLayer.Visibility = Visibility.Collapsed;
        ServicesTrans.BeginAnimation(TranslateTransform.XProperty, anim);
        ServicesPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
    }

    private void ServicesCloseBtn_Click(object s, RoutedEventArgs e) => CloseServicesPanel();
    private void ServicesBackdrop_Click(object s, MouseButtonEventArgs e) => CloseServicesPanel();

    private async void ZapretToggle_Click(object s, RoutedEventArgs e)
    {
        Console.WriteLine("[MainWindow] ZapretToggle_Click called");
        
        // Показать прогресс-бар
        ZapretToggleProgress.Visibility = Visibility.Visible;
        
        try
        {
            var st = DiagnosticsEngine.CheckAppStatus();
            Console.WriteLine($"[MainWindow] ZapretRunning: {st.ZapretRunning}");
            
            if (st.ZapretRunning)
            {
                foreach (var p in Process.GetProcessesByName("winws"))
                    try { p.Kill(); } catch { }
                foreach (var p in Process.GetProcessesByName("winws.exe"))
                    try { p.Kill(); } catch { }
            }
            else
            {
                if (!string.IsNullOrEmpty(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
                {
                    var isServiceBat = System.IO.Path.GetFileName(_settings.ZapretPath).Equals("service.bat", StringComparison.OrdinalIgnoreCase);
                    var cache = ZapretConfigService.LoadCache();

                    if (isServiceBat)
                    {
                        // Для service.bat ОБЯЗАТЕЛЬНО нужен выбранный конфиг
                        if (cache == null || !cache.HasAnyConfigs)
                        {
                            ShowFullScanRequiredNotification(
                                "Конфиги Zapret не найдены",
                                "Приложение не смогло обнаружить рабочие конфиги для Zapret.\n\n" +
                                "Сначала пройдите полное сканирование, чтобы NetFix нашёл доступные конфиги и подготовил запуск сервиса.");
                            return;
                        }

                        if (string.IsNullOrEmpty(cache.CurrentConfig))
                        {
                            return;
                        }

                        ZapretToggleBtn.IsEnabled = false;
                        var originalContent = ZapretToggleBtn.Content;
                        ZapretToggleBtn.Content = "Запуск...";

                        bool success = await ZapretConfigService.ApplyConfigAsync(_settings.ZapretPath, cache.CurrentConfig);

                        ZapretToggleBtn.IsEnabled = true;
                        ZapretToggleBtn.Content = originalContent;

                        if (!success)
                        {
                            return;
                        }
                    }
                    else
                    {
                        // Для обычных .bat файлов просто запускаем
                        Process.Start(new ProcessStartInfo(_settings.ZapretPath) { UseShellExecute = true });
                    }
                }
                else
                {
                    return;
                }
            }

            // Обновить статус через 2000мс (увеличено для видимости анимации)
            await Task.Delay(2000);
            UpdateActiveApps();
        }
        finally
        {
            // Скрыть прогресс-бар в любом случае
            ZapretToggleProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void TgWsToggle_Click(object s, RoutedEventArgs e)
    {
        // Показать прогресс-бар
        TgWsToggleProgress.Visibility = Visibility.Visible;
        
        try
        {
            var st = DiagnosticsEngine.CheckAppStatus();
            if (st.TgWsProxyRunning)
            {
                foreach (var p in Process.GetProcessesByName("TgWsProxy"))
                    try { p.Kill(); } catch { }
            }
            else
            {
                if (!string.IsNullOrEmpty(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
                {
                    Process.Start(new ProcessStartInfo(_settings.TgWsProxyPath) { UseShellExecute = true });
                    
                    // Автоматически активируем прокси в Telegram
                    await ActivateTgWsProxyAsync();
                }
                else
                {
                    return;
                }
            }

            // Обновить статус через 2000мс (увеличено для видимости анимации)
            await Task.Delay(2000);
            UpdateActiveApps();
        }
        finally
        {
            // Скрыть прогресс-бар в любом случае
            TgWsToggleProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ── Zapret Config Testing ──────────────────────────────────────────────────
    private void ShowConfigWindow(bool testMode, Action<Views.ZapretConfigWindow>? onClosed = null)
    {
        if (_configWindow is not null && _configWindow.IsVisible)
        {
            _configWindow.Activate();
            return;
        }
        if (_configWindow is not null)
        {
            _configWindow.Close();
            _configWindow = null;
        }

        if (string.IsNullOrEmpty(_settings.ZapretPath) || !File.Exists(_settings.ZapretPath))
            return;

        var w = new Views.ZapretConfigWindow(_settings.ZapretPath, testMode);
        _configWindow = w;
        w.Owner = this;
        w.Closed += (_, _) =>
        {
            _configWindow = null;
            onClosed?.Invoke(w);
        };
        w.Show();
    }

    private void TestConfigsBtn_Click(object s, RoutedEventArgs e)
    {
        ShowConfigWindow(testMode: true);
    }

    private async void SelectConfigBtn_Click(object s, RoutedEventArgs e)
    {
        Console.WriteLine("[MainWindow] SelectConfigBtn_Click started");
        
        if (string.IsNullOrEmpty(_settings.ZapretPath) || !File.Exists(_settings.ZapretPath))
            return;

        if (_configWindow is not null && _configWindow.IsVisible)
        {
            _configWindow.Activate();
            return;
        }
        if (_configWindow is not null)
        {
            _configWindow.Close();
            _configWindow = null;
        }

        // Проверить есть ли кэш с тестами
        var cache = ZapretConfigService.LoadCache();
        if (cache == null || !cache.HasAnyConfigs)
        {
            ShowFullScanRequiredNotification();
            return;
        }

        Console.WriteLine("[MainWindow] Opening config window");
        ShowConfigWindow(testMode: false, onClosed: async (w) =>
        {
            Console.WriteLine("[MainWindow] Config window closed");
            UpdateSelectedConfigDisplay();

            if (w.ConfigWasApplied)
            {
                Console.WriteLine("[MainWindow] Config was applied, checking if service needs to be started");
                var status = DiagnosticsEngine.CheckAppStatus();
                if (!status.ZapretRunning)
                {
                    Console.WriteLine("[MainWindow] Zapret not running, starting service");
                    ZapretToggle_Click(this, new RoutedEventArgs());
                }
                else
                {
                    Console.WriteLine("[MainWindow] Zapret already running, skipping start");
                }
            }

            Console.WriteLine("[MainWindow] SelectConfigBtn_Click finished");
        });
    }

    private void UpdateComponentsBtn_Click(object s, RoutedEventArgs e)
    {
        // Показываем диалоговое окно подтверждения
        ShowUpdateComponentsDialog();
    }

    private void ShowUpdateComponentsDialog()
    {
        // Создаем overlay для затемнения фона
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        // Создаем карточку диалога
        var dialogCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 480,
            Margin = new Thickness(40),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.5
            }
        };
        Grid.SetRowSpan(dialogCard, 3);

        var cardContent = new StackPanel
        {
            Margin = new Thickness(32, 28, 32, 28)
        };

        // Иконка обновления
        var iconBorder = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)) { Opacity = 0.15 },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };

        var refreshIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M21,11c-0.6,0-1,0.4-1,1c0,2.9-1.5,5.5-4,6.9c-3.8,2.2-8.7,0.9-10.9-2.9C2.9,12.2,4.2,7.3,8,5.1c3.3-1.9,7.3-1.2,9.8,1.4h-2.4c-0.6,0-1,0.4-1,1s0.4,1,1,1h4.5c0.6,0,1-0.4,1-1V3c0-0.6-0.4-1-1-1s-1,0.4-1,1v1.8C17,3,14.6,2,12,2C6.5,2,2,6.5,2,12s4.5,10,10,10c5.5,0,10-4.5,10-10C22,11.4,21.6,11,21,11z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = refreshIcon;
        cardContent.Children.Add(iconBorder);

        // Заголовок
        var titleText = new TextBlock
        {
            Text = "Обновить компоненты?",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        cardContent.Children.Add(titleText);

        // Описание
        var descText = new TextBlock
        {
            Text = "Приложение скачает и установит последние версии Zapret и TgWsProxy.\n\n" +
                   "Это может занять несколько секунд. Существующие файлы будут обновлены.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 24)
        };
        cardContent.Children.Add(descText);

        // Кнопки
        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var updateBtn = new Button
        {
            Content = "Обновить",
            Width = 140,
            Height = 40,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        updateBtn.Style = (Style)FindResource("AccentBtn");
        updateBtn.Click += async (s, e) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
            
            // Закрываем панель сервисов
            CloseServicesPanel();
            
            // Запускаем обновление компонентов
            await RunAutoInstallAsync();
        };

        var cancelBtn = new Button
        {
            Content = "Отмена",
            Width = 100,
            Height = 40,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        cancelBtn.Style = (Style)FindResource("OutlineBtn");
        cancelBtn.Click += (s, e) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
        };

        buttonsPanel.Children.Add(updateBtn);
        buttonsPanel.Children.Add(cancelBtn);
        cardContent.Children.Add(buttonsPanel);

        dialogCard.Child = cardContent;
        MainGrid.Children.Add(dialogCard);

        // Анимация появления
        overlay.Opacity = 0;
        dialogCard.Opacity = 0;
        dialogCard.RenderTransform = new ScaleTransform(0.9, 0.9);
        dialogCard.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        var scaleIn = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        overlay.BeginAnimation(OpacityProperty, fadeIn);
        dialogCard.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

        // Закрытие по клику на overlay
        overlay.MouseLeftButtonDown += (s, e) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
        };
    }

    private void UpdateSelectedConfigDisplay()
    {
        var cache = ZapretConfigService.LoadCache();
        if (cache != null && !string.IsNullOrEmpty(cache.CurrentConfig))
        {
            // Создаем текст с зеленым цветом для названия конфига
            SelectedConfigText.Inlines.Clear();
            SelectedConfigText.Inlines.Add(new Run("Выбранный конфиг: ") { Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)) });
            SelectedConfigText.Inlines.Add(new Run(cache.CurrentConfig) { Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)) });
        }
        else
        {
            SelectedConfigText.Inlines.Clear();
            SelectedConfigText.Inlines.Add(new Run("Выбранный конфиг: не выбран") { Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)) });
        }
    }
    
    private void UpdateActiveConfigDisplay(bool zapretRunning)
    {
        var cache = ZapretConfigService.LoadCache();
        
        // Показывать активный конфиг только если Zapret запущен и есть выбранный конфиг
        if (zapretRunning && cache != null && !string.IsNullOrEmpty(cache.CurrentConfig))
        {
            ActiveConfigText.Visibility = Visibility.Visible;
            
            // Обрезать длинное название конфига
            string configName = cache.CurrentConfig;
            if (configName.Length > 25)
            {
                configName = configName.Substring(0, 22) + "...";
            }
            
            ActiveConfigName.Text = configName;
        }
        else
        {
            ActiveConfigText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Обновляет отображение статуса версий компонентов в панели сервисов
    /// </summary>
    private async Task UpdateVersionStatusAsync()
    {
        try
        {
            // Показываем состояние загрузки
            VersionStatusIcon.Data = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z");
            VersionStatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            VersionStatusTitle.Text = "Проверяем...";
            VersionStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            
            ZapretVersionText.Text = "...";
            ZapretVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            ZapretVersionIcon.Visibility = Visibility.Collapsed;
            
            TgWsProxyVersionText.Text = "...";
            TgWsProxyVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            TgWsProxyVersionIcon.Visibility = Visibility.Collapsed;
            
            // Получаем информацию о версиях
            var versionInfo = await GetDetailedVersionInfoAsync();
            
            // Обновляем иконку и заголовок статуса
            if (versionInfo.allUpToDate)
            {
                // Все актуально - зеленая галочка
                VersionStatusIcon.Data = Geometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");
                VersionStatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                VersionStatusTitle.Text = "Компоненты обновлены!";
                VersionStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            }
            else
            {
                // Требуется обновление - синяя иконка обновления
                VersionStatusIcon.Data = Geometry.Parse("M12 6v3l4-4-4-4v3c-4.42 0-8 3.58-8 8 0 1.57.46 3.03 1.24 4.26L6.7 14.8c-.45-.83-.7-1.79-.7-2.8 0-3.31 2.69-6 6-6zm6.76 1.74L17.3 9.2c.44.84.7 1.79.7 2.8 0 3.31-2.69 6-6 6v-3l-4 4 4 4v-3c4.42 0 8-3.58 8-8 0-1.57-.46-3.03-1.24-4.26z");
                VersionStatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
                VersionStatusTitle.Text = "Нужно обновить!";
                VersionStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
            }
            
            // Обновляем информацию о версиях Zapret
            if (!string.IsNullOrEmpty(versionInfo.zapretCurrent))
            {
                if (versionInfo.zapretNeedsUpdate && !string.IsNullOrEmpty(versionInfo.zapretLatest))
                {
                    ZapretVersionText.Text = $"{versionInfo.zapretCurrent} → {versionInfo.zapretLatest}";
                    ZapretVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
                    ZapretVersionIcon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ZapretVersionText.Text = versionInfo.zapretCurrent;
                    ZapretVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    ZapretVersionIcon.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ZapretVersionText.Text = "Не установлен";
                ZapretVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                ZapretVersionIcon.Visibility = Visibility.Collapsed;
            }
            
            // Обновляем информацию о версиях TgWsProxy
            if (!string.IsNullOrEmpty(versionInfo.tgWsProxyCurrent))
            {
                if (versionInfo.tgWsProxyNeedsUpdate && !string.IsNullOrEmpty(versionInfo.tgWsProxyLatest))
                {
                    TgWsProxyVersionText.Text = $"{versionInfo.tgWsProxyCurrent} → {versionInfo.tgWsProxyLatest}";
                    TgWsProxyVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
                    TgWsProxyVersionIcon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TgWsProxyVersionText.Text = versionInfo.tgWsProxyCurrent;
                    TgWsProxyVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    TgWsProxyVersionIcon.Visibility = Visibility.Visible;
                }
            }
            else
            {
                TgWsProxyVersionText.Text = "Не установлен";
                TgWsProxyVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                TgWsProxyVersionIcon.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обновления статуса версий: {ex.Message}");
            
            // В случае ошибки показываем нейтральное состояние
            VersionStatusIcon.Data = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z");
            VersionStatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            VersionStatusTitle.Text = "Не удалось проверить";
            VersionStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            
            ZapretVersionText.Text = "—";
            ZapretVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            ZapretVersionIcon.Visibility = Visibility.Collapsed;
            
            TgWsProxyVersionText.Text = "—";
            TgWsProxyVersionText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            TgWsProxyVersionIcon.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Получает детальную информацию о версиях компонентов
    /// </summary>
    private async Task<(bool allUpToDate, bool zapretNeedsUpdate, bool tgWsProxyNeedsUpdate, 
                        string zapretCurrent, string zapretLatest, string tgWsProxyCurrent, string tgWsProxyLatest)> 
        GetDetailedVersionInfoAsync()
    {
        bool zapretInstalled = !string.IsNullOrEmpty(_settings.ZapretPath) && File.Exists(_settings.ZapretPath);
        bool tgWsProxyInstalled = !string.IsNullOrEmpty(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath);

        string zapretCurrent = "";
        string zapretLatest = "";
        bool zapretNeedsUpdate = false;

        string tgWsProxyCurrent = "";
        string tgWsProxyLatest = "";
        bool tgWsProxyNeedsUpdate = false;

        if (zapretInstalled)
        {
            // Получаем текущую версию из сохраненного файла
            zapretCurrent = GetInstalledZapretVersion(_settings.ZapretPath) ?? "";
            // Получаем последнюю версию с GitHub
            zapretLatest = await GetLatestGitHubVersionAsync("Flowseal/zapret-discord-youtube") ?? "";
            
            if (!string.IsNullOrEmpty(zapretLatest) && !string.IsNullOrEmpty(zapretCurrent))
            {
                zapretNeedsUpdate = IsNewerVersion(zapretLatest, zapretCurrent);
            }
        }

        if (tgWsProxyInstalled)
        {
            // Получаем текущую версию из сохраненного файла
            tgWsProxyCurrent = GetInstalledTgWsProxyVersion(_settings.TgWsProxyPath) ?? "";
            // Получаем последнюю версию с GitHub
            tgWsProxyLatest = await GetLatestGitHubVersionAsync("Flowseal/tg-ws-proxy") ?? "";
            
            if (!string.IsNullOrEmpty(tgWsProxyLatest) && !string.IsNullOrEmpty(tgWsProxyCurrent))
            {
                tgWsProxyNeedsUpdate = IsNewerVersion(tgWsProxyLatest, tgWsProxyCurrent);
            }
        }

        bool allUpToDate = !zapretNeedsUpdate && !tgWsProxyNeedsUpdate && (zapretInstalled || tgWsProxyInstalled);

        return (allUpToDate, zapretNeedsUpdate, tgWsProxyNeedsUpdate, 
                zapretCurrent, zapretLatest, tgWsProxyCurrent, tgWsProxyLatest);
    }

    /// <summary>
    /// Получает версию установленного Zapret из сохраненного файла
    /// </summary>
    private string? GetInstalledZapretVersion(string serviceBatPath)
    {
        try
        {
            var zapretDir = Path.GetDirectoryName(serviceBatPath);
            if (string.IsNullOrEmpty(zapretDir))
                return null;

            // 1. Проверяем файл version.txt (создается при установке через приложение)
            var versionFile = Path.Combine(zapretDir, "version.txt");
            if (File.Exists(versionFile))
            {
                var version = File.ReadAllText(versionFile).Trim();
                if (!string.IsNullOrEmpty(version))
                    return version;
            }

            // 2. Ищем в README файлах
            var readmeFiles = Directory.GetFiles(zapretDir, "README*", SearchOption.TopDirectoryOnly);
            foreach (var readme in readmeFiles)
            {
                try
                {
                    var content = File.ReadAllText(readme);
                    // Ищем версию в разных форматах
                    var patterns = new[]
                    {
                        @"version[:\s]+([0-9]+\.[0-9]+\.[0-9]+[a-z]?)",
                        @"v([0-9]+\.[0-9]+\.[0-9]+[a-z]?)",
                        @"([0-9]+\.[0-9]+\.[0-9]+[a-z]?)"
                    };
                    
                    foreach (var pattern in patterns)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(content, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            return match.Groups[1].Value;
                        }
                    }
                }
                catch { }
            }

            // 3. Если ничего не нашли, возвращаем "установлен" без версии
            return "установлен";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Получает версию установленного TgWsProxy из метаданных файла
    /// </summary>
    private string? GetInstalledTgWsProxyVersion(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir))
                return null;
            
            // 1. Проверяем файл tgwsproxy_version.txt (создается при установке через приложение)
            var versionFile = Path.Combine(dir, "tgwsproxy_version.txt");
            if (File.Exists(versionFile))
            {
                var version = File.ReadAllText(versionFile).Trim();
                if (!string.IsNullOrEmpty(version))
                    return version;
            }

            // 2. Берем версию из метаданных файла (ProductVersion)
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrEmpty(versionInfo.ProductVersion))
            {
                // Убираем лишние нули и пробелы
                var version = versionInfo.ProductVersion.Trim();
                var parts = version.Split('.');
                
                // Убираем trailing zeros
                int lastNonZero = parts.Length - 1;
                while (lastNonZero > 0 && parts[lastNonZero] == "0")
                {
                    lastNonZero--;
                }
                
                return string.Join(".", parts.Take(lastNonZero + 1));
            }
            
            // 3. Если ProductVersion нет, пробуем FileVersion
            if (!string.IsNullOrEmpty(versionInfo.FileVersion))
            {
                var version = versionInfo.FileVersion.Trim();
                var parts = version.Split('.');
                
                int lastNonZero = parts.Length - 1;
                while (lastNonZero > 0 && parts[lastNonZero] == "0")
                {
                    lastNonZero--;
                }
                
                return string.Join(".", parts.Take(lastNonZero + 1));
            }

            return "установлен";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Получает последнюю версию компонента с GitHub (tag_name из latest release)
    /// </summary>
    private async Task<string?> GetLatestGitHubVersionAsync(string repo)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");
            
            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Берем tag_name напрямую, как в AutoDownloadService
            var version = root.GetProperty("tag_name").GetString() ?? "";
            return version; // Возвращаем как есть (с 'v' если есть)
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Сравнивает две версии
    /// </summary>
    private bool IsNewerVersion(string version1, string version2)
    {
        try
        {
            version1 = version1.TrimStart('v');
            version2 = version2.TrimStart('v');

            if (Version.TryParse(version1, out var v1) && Version.TryParse(version2, out var v2))
            {
                return v1 > v2;
            }

            return string.Compare(version1, version2, StringComparison.OrdinalIgnoreCase) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Инициализирует файлы версий для уже установленных компонентов
    /// </summary>
    private async void InitializeVersionFiles()
    {
        try
        {
            // Проверяем Zapret
            if (!string.IsNullOrEmpty(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
            {
                var zapretDir = Path.GetDirectoryName(_settings.ZapretPath);
                if (!string.IsNullOrEmpty(zapretDir))
                {
                    var versionFile = Path.Combine(zapretDir, "version.txt");
                    
                    // Если файла версии нет, пытаемся получить версию с GitHub и создать файл
                    if (!File.Exists(versionFile))
                    {
                        try
                        {
                            var latestVersion = await GetLatestGitHubVersionAsync("Flowseal/zapret-discord-youtube");
                            if (!string.IsNullOrEmpty(latestVersion))
                            {
                                File.WriteAllText(versionFile, latestVersion);
                                Console.WriteLine($"[InitVersionFiles] Создан файл версии Zapret: {latestVersion}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[InitVersionFiles] Не удалось создать файл версии Zapret: {ex.Message}");
                        }
                    }
                }
            }

            // Проверяем TgWsProxy
            if (!string.IsNullOrEmpty(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
            {
                var tgWsDir = Path.GetDirectoryName(_settings.TgWsProxyPath);
                if (!string.IsNullOrEmpty(tgWsDir))
                {
                    var versionFile = Path.Combine(tgWsDir, "tgwsproxy_version.txt");
                    
                    // Если файла версии нет, пытаемся получить версию из метаданных или с GitHub
                    if (!File.Exists(versionFile))
                    {
                        try
                        {
                            // Сначала пробуем из метаданных файла
                            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(_settings.TgWsProxyPath);
                            string? version = null;
                            
                            if (!string.IsNullOrEmpty(versionInfo.ProductVersion))
                            {
                                var parts = versionInfo.ProductVersion.Trim().Split('.');
                                int lastNonZero = parts.Length - 1;
                                while (lastNonZero > 0 && parts[lastNonZero] == "0")
                                {
                                    lastNonZero--;
                                }
                                version = string.Join(".", parts.Take(lastNonZero + 1));
                            }
                            
                            // Если не получилось из метаданных, берем с GitHub
                            if (string.IsNullOrEmpty(version))
                            {
                                version = await GetLatestGitHubVersionAsync("Flowseal/tg-ws-proxy");
                            }
                            
                            if (!string.IsNullOrEmpty(version))
                            {
                                File.WriteAllText(versionFile, version);
                                Console.WriteLine($"[InitVersionFiles] Создан файл версии TgWsProxy: {version}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[InitVersionFiles] Не удалось создать файл версии TgWsProxy: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[InitVersionFiles] Общая ошибка: {ex.Message}");
        }
    }

    /// <summary>
    /// Запускает таймер для показа диалога о долгой проверке
    /// </summary>
    private void StartLongCheckTimer()
    {
        // Останавливаем предыдущий таймер если есть
        StopLongCheckTimer();
        
        Console.WriteLine("[LongCheckTimer] Создаем таймер на 10 секунд");
        
        // Создаем новый таймер на 10 секунд
        _longCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        
        _longCheckTimer.Tick += (s, e) =>
        {
            Console.WriteLine($"[LongCheckTimer] Таймер сработал! _checkInProgress={_checkInProgress}, ShowLongCheckDialog={_settings.ShowLongCheckDialog}");
            StopLongCheckTimer();
            
            // Показываем диалог только если проверка все еще идет и настройка включена
            if (_checkInProgress && _settings.ShowLongCheckDialog)
            {
                Console.WriteLine("[LongCheckTimer] Показываем диалог");
                ShowLongCheckDialog();
            }
            else
            {
                Console.WriteLine("[LongCheckTimer] Диалог не показан");
            }
        };
        
        _longCheckTimer.Start();
        Console.WriteLine("[LongCheckTimer] Таймер запущен");
    }

    /// <summary>
    /// Останавливает таймер долгой проверки
    /// </summary>
    private void StopLongCheckTimer()
    {
        if (_longCheckTimer != null)
        {
            Console.WriteLine("[LongCheckTimer] Останавливаем таймер");
            _longCheckTimer.Stop();
            _longCheckTimer = null;
        }
    }

    /// <summary>
    /// Показывает диалоговое окно о долгой проверке
    /// </summary>
    private void ShowLongCheckDialog()
    {
        // Создаем overlay для затемнения фона
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        // Создаем карточку диалога
        var dialogCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 520,
            Margin = new Thickness(40),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.5
            }
        };
        Grid.SetRowSpan(dialogCard, 3);

        var cardContent = new StackPanel
        {
            Margin = new Thickness(32, 28, 32, 28)
        };

        // Иконка часов
        var iconBorder = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)) { Opacity = 0.15 },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };

        var clockIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10 10-4.5 10-10S17.5 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = clockIcon;
        cardContent.Children.Add(iconBorder);

        // Заголовок
        var titleText = new TextBlock
        {
            Text = "Проверка продолжается...",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        cardContent.Children.Add(titleText);

        // Описание
        var descText = new TextBlock
        {
            Text = "Проверка может длиться долго! Если вы нажимаете на эту кнопку не первый раз, " +
                   "вы можете решить свою проблему быстрее во вкладке \"Сервисы\", не ожидая завершения полной проверки.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 24)
        };
        cardContent.Children.Add(descText);

        // Текст про режимы кнопки
        var modeText = new TextBlock
        {
            Text = "А также в настройках вы можете выбрать режим работы кнопки. Если поставить «Быстрый», всё запускается буквально за 3 секунды и ждать больше не нужно.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 24)
        };
        cardContent.Children.Add(modeText);

        // Чекбокс "Показывать это окно в будущем"
        var showAgainCb = new System.Windows.Controls.CheckBox
        {
            Content = "Больше не показывать",
            Style = (Style)FindResource("Toggle"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            FontSize = 13,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            IsChecked = !_settings.ShowLongCheckDialog
        };
        cardContent.Children.Add(showAgainCb);

        // Кнопка "Понятно"
        var okBtn = new Button
        {
            Content = "Понятно",
            Width = 140,
            Height = 40,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        okBtn.Style = (Style)FindResource("AccentBtn");
        okBtn.Click += (s, e) =>
        {
            _settings.ShowLongCheckDialog = showAgainCb.IsChecked != true;
            SettingsService.Save(_settings);
            ShowServiceReminderCB.IsChecked = _settings.ShowLongCheckDialog;
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
        };
        cardContent.Children.Add(okBtn);

        dialogCard.Child = cardContent;
        MainGrid.Children.Add(dialogCard);

        // Анимация появления
        overlay.Opacity = 0;
        dialogCard.Opacity = 0;
        dialogCard.RenderTransform = new ScaleTransform(0.9, 0.9);
        dialogCard.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        var scaleIn = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        overlay.BeginAnimation(OpacityProperty, fadeIn);
        dialogCard.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

        // Закрытие по клику на overlay
        overlay.MouseLeftButtonDown += (s, e) =>
        {
            _settings.ShowLongCheckDialog = showAgainCb.IsChecked != true;
            SettingsService.Save(_settings);
            ShowServiceReminderCB.IsChecked = _settings.ShowLongCheckDialog;
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
        };
    }

    private void ShowFullScanRequiredNotification(
        string title = "Требуется полное сканирование",
        string description = "Пройдите сначала полное сканирование, чтобы менять конфиги.\n\n" +
                             "Это займёт около 10 минут, но зато приложение найдёт все рабочие конфиги " +
                             "и вы сможете легко переключаться между ними когда что-то перестанет работать.")
    {
        // Создаем overlay для затемнения фона
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        // Создаем карточку уведомления
        var notificationCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 480,
            Margin = new Thickness(40),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.5
            }
        };
        Grid.SetRowSpan(notificationCard, 3);

        var cardContent = new StackPanel
        {
            Margin = new Thickness(32, 28, 32, 28)
        };

        // Иконка предупреждения
        var iconBorder = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)) { Opacity = 0.15 },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };

        var warningIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12,2 L22,20 L2,20 Z M12,9 L12,13 M12,15 L12,17"),
            Stroke = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)),
            StrokeThickness = 2.5,
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = warningIcon;
        cardContent.Children.Add(iconBorder);

        // Заголовок
        var titleText = new TextBlock
        {
            Text = "Требуется полное сканирование",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        titleText.Text = title;
        cardContent.Children.Add(titleText);

        // Описание
        var descText = new TextBlock
        {
            Text = "Пройдите сначала полное сканирование, чтобы менять конфиги.\n\n" +
                   "Это займёт около 10 минут, но зато приложение найдёт все рабочие конфиги " +
                   "и вы сможете легко переключаться между ними когда что-то перестанет работать.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 24)
        };
        descText.Text = description;
        cardContent.Children.Add(descText);

        // Кнопки
        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var startScanBtn = new Button
        {
            Content = "Начать сканирование",
            Width = 180,
            Height = 40,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        startScanBtn.Style = (Style)FindResource("AccentBtn");
        startScanBtn.Click += (s, e) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(notificationCard);
            ShowConfigWindow(testMode: true);
        };

        var cancelBtn = new Button
        {
            Content = "Отмена",
            Width = 100,
            Height = 40,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        cancelBtn.Style = (Style)FindResource("OutlineBtn");
        cancelBtn.Click += (s, e) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(notificationCard);
        };

        buttonsPanel.Children.Add(startScanBtn);
        buttonsPanel.Children.Add(cancelBtn);
        cardContent.Children.Add(buttonsPanel);

        notificationCard.Child = cardContent;
        MainGrid.Children.Add(notificationCard);

        // Анимация появления
        overlay.Opacity = 0;
        notificationCard.Opacity = 0;
        notificationCard.RenderTransform = new ScaleTransform(0.9, 0.9);
        notificationCard.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        var scaleIn = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        overlay.BeginAnimation(OpacityProperty, fadeIn);
        notificationCard.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)notificationCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        ((ScaleTransform)notificationCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

        // Закрытие по клику на overlay
        overlay.MouseLeftButtonDown += (s, e) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(notificationCard);
        };
    }

    private void ShowScanRequiredForFastMode()
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 460,
            Margin = new Thickness(40),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.5
            }
        };
        Grid.SetRowSpan(card, 3);

        var content = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };

        var icon = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)) { Opacity = 0.15 },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M12,2 L22,20 L2,20 Z M12,9 L12,13 M12,15 L12,17"),
                Stroke = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)),
                StrokeThickness = 2.5, Width = 28, Height = 28, Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        content.Children.Add(icon);

        content.Children.Add(new TextBlock
        {
            Text = "Нет просканированных конфигов",
            FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        });

        content.Children.Add(new TextBlock
        {
            Text = "Приложение не обнаружило рабочие конфиги!\n\n" +
                   "Для работы приложения необходимо просканировать конфиги. " +
                   "Выделите примерно 10 минут, во время проверки вы можете поиграть " +
                   "в мини-игру или заняться своими делами.\n\n" +
                   "После сканирования у вас будет полный функционал приложения!",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            LineHeight = 22, Margin = new Thickness(0, 0, 0, 24)
        });

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var scanBtn = new Button
        {
            Content = "Пройти тестирование", Width = 180, Height = 40,
            Margin = new Thickness(0, 0, 10, 0), Foreground = Brushes.White,
            FontSize = 13, FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand
        };
        scanBtn.Style = (Style)FindResource("AccentBtn");
        scanBtn.Click += (_, _) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(card);
            ShowConfigWindow(testMode: true);
        };

        var cancelBtn = new Button
        {
            Content = "Отмена", Width = 100, Height = 40,
            Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1), FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand
        };
        cancelBtn.Style = (Style)FindResource("OutlineBtn");
        cancelBtn.Click += (_, _) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(card);
        };

        btns.Children.Add(scanBtn);
        btns.Children.Add(cancelBtn);
        content.Children.Add(btns);
        card.Child = content;
        MainGrid.Children.Add(card);

        overlay.Opacity = 0;
        card.Opacity = 0;
        card.RenderTransform = new ScaleTransform(0.9, 0.9);
        card.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        var scaleIn = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        overlay.BeginAnimation(OpacityProperty, fadeIn);
        card.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

        overlay.MouseLeftButtonDown += (_, _) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(card);
        };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _discord.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosing(e);
    }

    // ── Fade in ──────────────────────────────────────────────────────────────
    private void FadeIn()
    {
        Opacity = 0;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
        BeginAnimation(OpacityProperty, anim);
    }

    // ── Window chrome ────────────────────────────────────────────────────────
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    
    private void CloseBtn_Click(object s, RoutedEventArgs e)
    {
        _auroraTimer?.Stop();
        _monitorTimer?.Stop();
        _netTimer?.Stop();
        _pingTimer?.Stop();
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        Hide();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    // ── Nav ──────────────────────────────────────────────────────────────────
    private void DiagNavBtn_Click(object s, RoutedEventArgs e)
    {
        // Останавливаем игру/редактор при переходе на другую вкладку
        StopGame();
        StopEditorRecording();
        
        ShowDiagnosticsTab();
    }
    
    // Публичный метод для открытия вкладки диагностики (используется из TrayPopup)
    public void ShowDiagnosticsTab()
    {
        MainPage.Visibility = Visibility.Collapsed;
        GamePage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Visible;
        DiagNavBtn.Foreground = Brushes.White;
        GameNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void GameNavBtn_Click(object s, RoutedEventArgs e)
    {
        // Если страница игры открыта - работаем как НАЗАД
        if (GamePage.Visibility == Visibility.Visible)
        {
            // Из редактора -> в меню игры
            if (GameEditorView.Visibility == Visibility.Visible)
            {
                StopEditorRecording();
                ShowGameView(GameMenuView);
                return;
            }
            
            // Из выбора трека -> в меню игры
            if (GameTrackSelectView.Visibility == Visibility.Visible)
            {
                ShowGameView(GameMenuView);
                return;
            }
            
            // Из игры -> к выбору трека
            if (GamePlayView.Visibility == Visibility.Visible)
            {
                StopGame();
                ShowGameView(GameTrackSelectView);
                return;
            }
            
            // Из меню игры -> ничего не делаем (уже на месте)
            return;
        }
        
        // Если страница игры не открыта - открываем её
        StopGame();
        StopEditorRecording();
        
        MainPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        GamePage.Visibility = Visibility.Visible;

        GameNavBtn.Foreground = Brushes.White;
        ServicesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        ShowGameView(GameMenuView);
        LoadUserLevels();
    }

    private void GameBackBtn_Click(object s, RoutedEventArgs e)
    {
        _previewPlayer.Stop();
        _previewPlaying = false;

        if (OszDifficultyView.Visibility == Visibility.Visible)
        {
            ShowGameView(GameMenuView);
            _pendingOszPath = null;
            return;
        }

        if (GameStatsDetailView.Visibility == Visibility.Visible)
        {
            GameStatsDetailView.Visibility = Visibility.Collapsed;
            GameStatsView.Visibility = Visibility.Visible;
            GameStatsView.Focus();
            return;
        }
        if (GameStatsView.Visibility == Visibility.Visible)
        {
            GameStatsView.Visibility = Visibility.Collapsed;
            GameMenuView.Visibility = Visibility.Visible;
            return;
        }
        
        if (GameSettingsView.Visibility == Visibility.Visible)
        {
            GameSettingsView.Visibility = Visibility.Collapsed;
            GameMenuView.Visibility = Visibility.Visible;
            _listeningLane = -1;
            return;
        }
        
        if (_gameOverlayActive)
        {
            // Закрываем игровой оверлей, возвращаемся к главной
            _gameOverlayActive = false;
            StopGame();
            GamePage.Visibility = Visibility.Collapsed;
            Panel.SetZIndex(GamePage, 0);
            GamePage.Opacity = 1;
            GamePage.Background = new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0f));
            
            // Снимаем блюр
            MainPage.Effect = null;
            MainPage.Opacity = 1.0;
            
            // Включаем навигационные кнопки обратно
            ServicesBtn.IsEnabled = true;
            GameNavBtn.IsEnabled = true;
            FaqNavBtn.IsEnabled = true;
            DiagNavBtn.IsEnabled = true;
            SettingsBtn.IsEnabled = true;
            
            // Показываем кнопку "Создать уровень" обратно
            EditorMenuBtn.Visibility = Visibility.Visible;
            return;
        }
        
        // Из редактора -> в меню игры
        if (GameEditorView.Visibility == Visibility.Visible)
        {
            StopEditorRecording();
            ShowGameView(GameMenuView);
            return;
        }
        
        // Из выбора трека -> в меню игры
        if (GameTrackSelectView.Visibility == Visibility.Visible)
        {
            ShowGameView(GameMenuView);
            return;
        }
        
        // Из Osu режима -> в меню игры
        if (OsuModeView.Visibility == Visibility.Visible)
        {
            ShowGameView(GameTrackSelectView);
            return;
        }
        
        // Из игры -> к выбору трека
        if (GamePlayView.Visibility == Visibility.Visible)
        {
            StopGame();
            ShowGameView(GameTrackSelectView);
            return;
        }
        
        // Из меню игры -> на главный экран
        if (GameMenuView.Visibility == Visibility.Visible)
        {
            GamePage.Visibility = Visibility.Collapsed;
            MainPage.Visibility = Visibility.Visible;
            GameNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            return;
        }
    }

    // ── FAQ ОБНОВЛЕННАЯ ЛОГИКА ───────────────────────────────────────────────
    private string _currentFaqCategory = "";

    private void FaqNavBtn_Click(object s, RoutedEventArgs e)
    {
        // Останавливаем игру/редактор при переходе на другую вкладку
        StopGame();
        StopEditorRecording();
        
        MainPage.Visibility = Visibility.Collapsed;
        GamePage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Visible;
        FaqNavBtn.Foreground = Brushes.White;
        GameNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        ShowFaqCategories();
    }

    private void FaqBackBtn_Click(object s, RoutedEventArgs e)
    {
        if (FaqHeaderTitle.Text == "Частые вопросы") {
            FaqPage.Visibility = Visibility.Collapsed;
            MainPage.Visibility = Visibility.Visible;
            FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        } else if (_currentFaqCategory != "" && FaqHeaderTitle.Text != _currentFaqCategory) {
            ShowFaqQuestions(_currentFaqCategory);
        } else {
            ShowFaqCategories();
        }
    }

    private void ShowFaqCategories()
    {
        _currentFaqCategory = "";
        FaqHeaderTitle.Text = "Частые вопросы";
        FaqContainer.Children.Clear();

        AddCategoryCard("Telegram", "Настройка прокси и загрузка медиа", "TelegramIcon", Color.FromRgb(0x3b, 0x82, 0xf6));
        AddCategoryCard("Discord", "Обновление и голосовые каналы", "DiscordIcon", Color.FromRgb(0x8b, 0x5c, 0xf6));
        AddCategoryCard("Общее", "YouTube, Zapret и сетевые ошибки", "SettingsIcon", Color.FromRgb(0x22, 0xc5, 0x5e));
        
        // Добавляем специальную карточку для Android
        AddAndroidCard();
        
        // Добавляем блок с обращением для помощи
        var helpCard = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 18, 20, 18),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 12, 0, 0)
        };
        
        var helpStack = new StackPanel();
        helpStack.Children.Add(new TextBlock { 
            Text = "Не нашли решение своей проблемы?", 
            FontSize = 16, 
            FontWeight = FontWeights.Bold, 
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        });
        
        helpStack.Children.Add(new TextBlock { 
            Text = "Самостоятельный поиск: Лучший способ, вбить текст ошибки в поисковик. Скорее всего, кто-то уже сталкивался с этим и нашёл решение.", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        
        helpStack.Children.Add(new TextBlock { 
            Text = "Обращение ко мне: Если ничего не помогло, вы можете описать свою проблему в разделе Issues на моём GitHub-репозитории или написать мне напрямую в Telegram @sofirka_hanabi - я постараюсь помочь всем по мере возможности!", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        
        var devLinkStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        
        var linkText = new TextBlock { 
            Text = "Поиск у разработчика: Также рекомендую поискать решение в репозитории Flowseal, который является автором сборки Zapret и TgProxy:", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        
        // Создаем кликабельное слово "репозитории"
        var repoLink = new Button {
            Content = "репозитории",
            Style = (Style)FindResource("FlatBtn"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            FontSize = 13,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent
        };
        repoLink.Click += (s, e) => {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://github.com/Flowseal/zapret-discord-youtube",
                    UseShellExecute = true
                });
            } catch { }
        };
        
        var inlineText = new Run(" Flowseal, который является автором сборки Zapret и TgProxy:");
        
        var textPanel = new StackPanel { Orientation = Orientation.Horizontal };
        textPanel.Children.Add(new TextBlock { 
            Text = "Поиск у разработчика: Также рекомендую поискать решение в ", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center
        });
        textPanel.Children.Add(repoLink);
        textPanel.Children.Add(new TextBlock { 
            Text = " Flowseal, который является автором сборки Zapret и TgProxy:", 
            FontSize = 13, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center
        });
        
        devLinkStack.Children.Add(textPanel);
        helpStack.Children.Add(devLinkStack);
        
        helpCard.Child = helpStack;
        FaqContainer.Children.Add(helpCard);
    }

    private void AddCategoryCard(string title, string desc, string iconKey, Color accent)
    {
        var btn = new Button { 
            Style = (Style)FindResource("FlatBtn"), 
            Padding = new Thickness(0),
            Height = double.NaN, // убирает любой фиксированный Height
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        
        var card = new Border { 
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), 
            CornerRadius = new CornerRadius(0, 12, 12, 0),
            Padding = new Thickness(16, 14, 16, 14), // одинаково везде
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(3, 0, 0, 0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBg = new Border {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(accent) { Opacity = 0.15 },
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        
        // Create vector icon directly without FindResource
        Geometry geometry = null;
        
        // Create geometry based on icon key
        switch (iconKey)
        {
            case "TelegramIcon":
                // Official Telegram icon from simpleicons.org
                geometry = Geometry.Parse("M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z");
                break;
            case "DiscordIcon":
                // Official Discord icon from simpleicons.org
                geometry = Geometry.Parse("M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z");
                break;
            case "SettingsIcon":
                // Light bulb icon - ideas, solutions, help
                geometry = Geometry.Parse("M19.0006 9.03002C19.0007 8.10058 18.8158 7.18037 18.4565 6.32317C18.0972 5.46598 17.5709 4.68895 16.9081 4.03734C16.2453 3.38574 15.4594 2.87265 14.5962 2.52801C13.7331 2.18336 12.8099 2.01409 11.8806 2.03002C10.0966 2.08307 8.39798 2.80604 7.12302 4.05504C5.84807 5.30405 5.0903 6.98746 5.00059 8.77001C4.95795 9.9595 5.21931 11.1402 5.75999 12.2006C6.30067 13.2609 7.10281 14.1659 8.09058 14.83C8.36897 15.011 8.59791 15.2584 8.75678 15.5499C8.91565 15.8415 8.99945 16.168 9.00059 16.5V18.03H15.0006V16.5C15.0006 16.1689 15.0829 15.843 15.24 15.5515C15.3971 15.26 15.6241 15.0121 15.9006 14.83C16.8528 14.1911 17.6336 13.328 18.1741 12.3167C18.7147 11.3054 18.9985 10.1767 19.0006 9.03002V9.03002Z M15 21.04C14.1345 21.6891 13.0819 22.04 12 22.04C10.9181 22.04 9.86548 21.6891 9 21.04");
                break;
            default:
                // Fallback to text
                iconBg.Child = new TextBlock 
                { 
                    Text = iconKey.Substring(0, 1), 
                    FontSize = 20, 
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center, 
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(accent)
                };
                break;
        }
        
        if (geometry != null)
        {
            var iconPath = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            
            if (iconKey == "TelegramIcon" || iconKey == "DiscordIcon")
                iconPath.Fill = new SolidColorBrush(accent);
            else
            {
                iconPath.Stroke = new SolidColorBrush(accent);
                iconPath.StrokeThickness = 2;
            }
            
            iconBg.Child = iconPath;
        }

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        stack.Children.Add(new TextBlock { Text = desc, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) });

        var arrowBadge = new Border {
            Width = 32, Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(accent) { Opacity = 0.12 },
            VerticalAlignment = VerticalAlignment.Center
        };
        arrowBadge.Child = new TextBlock { 
            Text = "›", FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center, 
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
            IsHitTestVisible = false // не поглощает мышь
        };

        Grid.SetColumn(iconBg, 0); Grid.SetColumn(stack, 1); Grid.SetColumn(arrowBadge, 2);
        grid.Children.Add(iconBg); grid.Children.Add(stack); grid.Children.Add(arrowBadge);
        
        card.Child = grid; btn.Content = card;
        
        // Hover-эффект только на Border
        btn.MouseEnter += (s, e) => {
            var border = (Border)btn.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
        };
        btn.MouseLeave += (s, e) => {
            var border = (Border)btn.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
        };
        
        btn.Click += (s, e) => ShowFaqQuestions(title);
        FaqContainer.Children.Add(btn);
    }

    private void AddAndroidCard()
    {
        var btn = new Button { 
            Style = (Style)FindResource("FlatBtn"), 
            Padding = new Thickness(0),
            Height = double.NaN,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 12, 0, 0)
        };
        
        var card = new Border { 
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x2e, 0x1a)),
            CornerRadius = new CornerRadius(0, 12, 12, 0),
            Padding = new Thickness(20, 18, 20, 18),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            BorderThickness = new Thickness(4, 0, 0, 0)
        };

        var stack = new StackPanel();
        
        var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        
        // Create badge with fire icon and text inside
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)) { Opacity = 0.2 },
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3)
        };
        
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };
        
        // Add fire icon inside badge
        var fireIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M187.899,164.809 C185.803,214.868 144.574,254.812 94.000,254.812 C42.085,254.812 -0.000,211.312 -0.000,160.812 C-0.000,154.062 -0.121,140.572 10.000,117.812 C16.057,104.191 19.856,95.634 22.000,87.812 C23.178,83.513 25.469,76.683 32.000,87.812 C35.851,94.374 36.000,103.812 36.000,103.812 C36.000,103.812 50.328,92.817 60.000,71.812 C74.179,41.019 62.866,22.612 59.000,9.812 C57.662,5.384 56.822,-2.574 66.000,0.812 C75.352,4.263 100.076,21.570 113.000,39.812 C131.445,65.847 138.000,90.812 138.000,90.812 C138.000,90.812 143.906,83.482 146.000,75.812 C148.365,67.151 148.400,58.573 155.999,67.813 C163.226,76.600 173.959,93.113 180.000,108.812 C190.969,137.321 187.899,164.809 187.899,164.809 Z M94.000,254.812 C58.101,254.812 29.000,225.711 29.000,189.812 C29.000,168.151 37.729,155.000 55.896,137.166 C67.528,125.747 78.415,111.722 83.042,102.172 C83.953,100.292 86.026,90.495 94.019,101.966 C98.212,107.982 104.785,118.681 109.000,127.812 C116.266,143.555 118.000,158.812 118.000,158.812 C118.000,158.812 125.121,154.616 130.000,143.812 C131.573,140.330 134.753,127.148 143.643,140.328 C150.166,150.000 159.127,167.390 159.000,189.812 C159.000,225.711 129.898,254.812 94.000,254.812 Z M95.000,183.812 C104.250,183.812 104.250,200.941 116.000,223.812 C123.824,239.041 112.121,254.812 95.000,254.812 C77.879,254.812 69.000,240.933 69.000,223.812 C69.000,206.692 85.750,183.812 95.000,183.812 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        badgeContent.Children.Add(fireIcon);
        
        badgeContent.Children.Add(new TextBlock { 
            Text = "НОВИНКА", 
            FontSize = 11, 
            FontWeight = FontWeights.Bold, 
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            VerticalAlignment = VerticalAlignment.Center
        });
        
        badge.Child = badgeContent;
        headerStack.Children.Add(badge);
        stack.Children.Add(headerStack);
        
        stack.Children.Add(new TextBlock { 
            Text = "TgWsProxy на Android!", 
            FontSize = 18, 
            FontWeight = FontWeights.Bold, 
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 6)
        });
        
        stack.Children.Add(new TextBlock { 
            Text = "Telegram будет работать на телефоне без VPN", 
            FontSize = 14, 
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xdd, 0xaa)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        
        var arrowText = new TextBlock { 
            Text = "Узнать подробнее →", 
            FontSize = 13, 
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
        };
        stack.Children.Add(arrowText);

        card.Child = stack;
        btn.Content = card;
        
        btn.MouseEnter += (s, e) => {
            var borderElement = (Border)btn.Content;
            borderElement.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x36, 0x1e));
        };
        btn.MouseLeave += (s, e) => {
            var borderElement = (Border)btn.Content;
            borderElement.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x2e, 0x1a));
        };
        
        btn.Click += (s, e) => ShowAndroidInfo();
        FaqContainer.Children.Add(btn);
    }

    private void ShowAndroidInfo()
    {
        FaqHeaderTitle.Text = "Android решение";
        FaqContainer.Children.Clear();
        
        var mainCard = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
        };

        var stack = new StackPanel();
        
        stack.Children.Add(new TextBlock { 
            Text = "TgWsProxy на Android", 
            FontSize = 19, 
            FontWeight = FontWeights.Bold, 
            Foreground = Brushes.White, 
            TextWrapping = TextWrapping.Wrap, 
            Margin = new Thickness(0, 0, 0, 18) 
        });

        var infoText = "Новый способ обхода блокировок Telegram на Android\n\n" +
            "Пока NetFix Mobile находится в разработке, делюсь рабочим решением от стороннего разработчика LemoLev. " +
            "Это отличный вариант для тех, кто устал от VPN и хочет стабильной работы Telegram через прокси.\n\n" +
            "Важное уточнение: Этот метод, «домашнее» решение. Прокси не работает на мобильном интернете. " +
            "Но если вы подключены к Wi-Fi или кто-то раздает вам интернет, всё должно работать.\n\n" +
            "Полная инструкция по установке и настройке, а также APK-файл доступны в моём Telegram-канале. " +
            "Там всё очень подробно расписано, шаг за шагом.\n\n" +
            "Переходите в канал для получения инструкции и файла:";

        stack.Children.Add(new TextBlock { 
            Text = infoText, 
            FontSize = 15, 
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)), 
            TextWrapping = TextWrapping.Wrap, 
            LineHeight = 24,
            Margin = new Thickness(0, 0, 0, 16)
        });

        var linkBtn = new Button {
            Style = (Style)FindResource("AccentBtn"),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 0)
        };
        
        var linkBtnContent = new StackPanel { Orientation = Orientation.Horizontal };
        var linkIcon = new System.Windows.Shapes.Path {
            Data = (Geometry)FindResource("ExternalLinkIcon"),
            Fill = Brushes.White,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        linkBtnContent.Children.Add(linkIcon);
        linkBtnContent.Children.Add(new TextBlock { 
            Text = "Открыть Telegram-канал @NetFixRuBi",
            VerticalAlignment = VerticalAlignment.Center
        });
        linkBtn.Content = linkBtnContent;
        linkBtn.Click += (s, e) => {
            try {
                // Пробуем открыть напрямую в Telegram
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "tg://resolve?domain=NetFixRuBi",
                    UseShellExecute = true
                });
            } catch {
                // Если не получилось, открываем через браузер
                try {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                        FileName = "https://t.me/NetFixRuBi",
                        UseShellExecute = true
                    });
                } catch { }
            }
        };
        stack.Children.Add(linkBtn);

        mainCard.Child = stack;
        FaqContainer.Children.Add(mainCard);

        // Кнопка назад
        var btnContainer = new Button {
            Style = (Style)FindResource("FlatBtn"),
            Padding = new Thickness(0),
            Height = double.NaN,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 24, 0, 0)
        };

        var backBtn = new Border {
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x33, 0x56)),
            Padding = new Thickness(20, 10, 20, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        
        backBtn.Child = new TextBlock {
            Text = "← Вернуться к FAQ",
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            IsHitTestVisible = false
        };
        
        btnContainer.Content = backBtn;
        
        btnContainer.MouseEnter += (s, e) => {
            var border = (Border)btnContainer.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1f, 0x3d, 0x66));
        };
        btnContainer.MouseLeave += (s, e) => {
            var border = (Border)btnContainer.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x33, 0x56));
        };
        
        btnContainer.Click += (s, e) => ShowFaqCategories();
        FaqContainer.Children.Add(btnContainer);
    }

    private void ShowFaqQuestions(string category)
    {
        _currentFaqCategory = category;
        FaqHeaderTitle.Text = category;
        FaqContainer.Children.Clear();

        if (category == "Telegram") {
            AddQuestion("Пропал значок TgWsProxy справа внизу, где найти программу?", "Зайди в папку C:\\Zapret, найди там TgWsProxy.exe и запусти его. После этого проверь иконку в трее (возле часов) и нажми «Включить».");
            AddQuestion("Телеграм не грузит, хотя TgWsProxy запущен.", "Убедись, что прокси включен внутри самого Telegram. Проверь настройки: Продвинутые настройки -> Тип соединения -> Использовать собственный прокси. Если не помогло, нажми правой кнопкой по значку в трее -> Перезапустить прокси. Если всё равно глухо, скачай свежую версию по ссылке: GitHub Releases. В крайнем случае проверь настройки DNS в Windows, они могут блокировать соединение.");
            AddQuestion("Текст грузится, а фото и кружочки, нет", "Это нормально. Отправка и загрузка тяжелых файлов через прокси может идти медленно из-за особенностей фильтров провайдера. Наберись терпения или используй VPN для тяжелого контента.");
        } 
        else if (category == "Discord") {
            AddQuestion("Бесконечная «Проверка обновлений» (Checking for updates). Что делать?", "Смени конфиг: Твой текущий метод обхода может не справляться с серверами обновлений Discord. Попробуй переключиться на другой конфиг в Zapret и перезапустить Discord.\n\nВыключи лишнее: Убедись, что у тебя не включен параллельно другой VPN или прокси-сервер. Они могут конфликтовать друг с другом.\n\nСбрось кэш Discord: Это решает проблему в 90% случаев.\n\nЗапусти Discord снова при включённом Zapret.\n\nКрайний метод: Если ничего не помогло, переустанови Discord, скачав официальный установщик. Перед установкой убедись, что Zapret запущен, иногда Discord не может даже установиться без обхода блокировок.");
            AddQuestion("Не вижу демонстрацию экрана друга, а они не видят мою. Что делать?", "Смените регион звонка: В настройках текущего голосового канала (справа сверху значок настройки или через админа сервера) смените «Регион сервера» на любой другой (например, Rotterdam, Poland или Madrid). Пробуйте разные варианты, пока картинка не появится.\n\nНастройки Discord: Зайдите в Настройки пользователя -> Голос и видео. Пролистайте вниз до раздела «Видеокодек» и попробуйте выключить пункт «Аппаратное ускорение H.264». Иногда Zapret конфликтует именно с этим типом передачи данных.\n\nПерезаход: После смены конфига в Zapret обязательно полностью перезапустите Discord, иначе он будет пытаться транслировать поток через старое (заблокированное) соединение.");
        }
        else if (category == "Общее") {
            AddQuestion("Не работает YouTube, хотя Zapret включен", "Твой старый конфиг мог «протухнуть» из-за обновления фильтров провайдера. Сделай перенастройку: Открой Zapret -> Выбери 2. Remove Services -> Выбери 11. Run Tests -> [1] Standard tests -> [1] All configs. Выбери тот конфиг, который в результате будет полностью зеленым.");
            AddQuestion("Программа пишет 'Access Denied'", "Всегда запускай скрипты и .exe файлы от имени Администратора. Антивирусы также могут блокировать работу Zapret, добавь папку C:\\Zapret в исключения.");
            AddQuestion("Влияет ли это на пинг в играх?", "Нет, Zapret работает только с заблокированными доменами. Твой пинг в играх (CS, Dota, Valorant) останется прежним.");
            AddQuestion("Некоторые сайты перестали открываться после включения Zapret. Что делать?", "Это происходит потому, что выбранный метод обхода (конфиг) конфликтует с защитой конкретного сайта. Например у меня самого конфиг general (SIMPLE FAKE).bat мешает работе Steam, Suno AI или банковских приложений.\n\nРешение:\n\n1. Попробуй сменить конфиг на другой (например, с припиской ALT или DESYNC).\n\n2. Если не помогает, на время работы с этим сайтом просто выключи Zapret.");
        }
    }

    private void AddQuestion(string title, string answer)
    {
        var btn = new Button { 
            Style = (Style)FindResource("FlatBtn"), 
            Padding = new Thickness(0),
            Height = double.NaN, // убирает любой фиксированный Height
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8)
        };
        
        var border = new Border { 
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14), // одинаково везде
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border {
            Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            IsHitTestVisible = false // не поглощает мышь
        };

        grid.Children.Add(new TextBlock { 
            Text = title, 
            Foreground = Brushes.White, 
            FontSize = 14, 
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(grid.Children[grid.Children.Count - 1], 1);

        grid.Children.Add(new TextBlock { 
            Text = "›", 
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Margin = new Thickness(10, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false // не поглощает мышь
        });
        Grid.SetColumn(grid.Children[grid.Children.Count - 1], 2);

        grid.Children.Insert(0, dot);

        border.Child = grid;
        btn.Content = border;
        
        // Hover-эффект только на Border
        btn.MouseEnter += (s, e) => {
            var borderElement = (Border)btn.Content;
            borderElement.Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
        };
        btn.MouseLeave += (s, e) => {
            var borderElement = (Border)btn.Content;
            borderElement.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        };
        
        btn.Click += (s, e) => ShowFaqAnswer(title, answer);
        FaqContainer.Children.Add(btn);
    }

    private void ShowFaqAnswer(string title, string answer)
    {
        FaqHeaderTitle.Text = "Ответ";
        FaqContainer.Children.Clear();
        
        var mainCard = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
        };

        var stack = new StackPanel();
        
        stack.Children.Add(new TextBlock { 
            Text = title, 
            FontSize = 19, 
            FontWeight = FontWeights.Bold, 
            Foreground = Brushes.White, 
            TextWrapping = TextWrapping.Wrap, 
            Margin = new Thickness(0, 0, 0, 18) 
        });

        stack.Children.Add(new TextBlock { 
            Text = answer, 
            FontSize = 15, 
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)), 
            TextWrapping = TextWrapping.Wrap, 
            LineHeight = 24 
        });

        mainCard.Child = stack;
        FaqContainer.Children.Add(mainCard);

        // Создаем кнопку-контейнер
        var btnContainer = new Button {
            Style = (Style)FindResource("FlatBtn"),
            Padding = new Thickness(0),
            Height = double.NaN, // убирает любой фиксированный Height
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 24, 0, 0)
        };

        var backBtn = new Border {
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x33, 0x56)),
            Padding = new Thickness(20, 10, 20, 10), // одинаково везде
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        
        backBtn.Child = new TextBlock {
            Text = "← Вернуться к вопросам",
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            IsHitTestVisible = false // не поглощает мышь
        };
        
        btnContainer.Content = backBtn;
        
        // Hover-эффект только на Border
        btnContainer.MouseEnter += (s, e) => {
            var border = (Border)btnContainer.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1f, 0x3d, 0x66));
        };
        btnContainer.MouseLeave += (s, e) => {
            var border = (Border)btnContainer.Content;
            border.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x33, 0x56));
        };
        
        btnContainer.Click += (s, e) => ShowFaqQuestions(_currentFaqCategory);
        FaqContainer.Children.Add(btnContainer);
    }

    private void BackBtn_Click(object s, RoutedEventArgs e)
    {
        DiagPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void BackFromSolution_Click(object s, RoutedEventArgs e)
    {
        SolutionPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Visible;
    }

    // ── FAQ Logic ────────────────────────────────────────────────────────────
    private void LoadFaqItems()
    {
        FaqContainer.Children.Clear();
        
        AddFaqItem(
            "У меня не грузит ТГ, но включен TgProxy",
            "Для работы Telegram через прокси необходимо убедиться, что приложение использует правильный порт и протокол. Обычно это связано с тем, что Telegram пытается использовать системные настройки прокси, игнорируя TgProxy. В автоматическом режиме мы перенастроим конфигурацию Telegram на локальный прокси.",
            "Перенастроить Telegram"
        );
        AddFaqItem(
            "У меня не работает YouTube, хотя Запрет включен",
            "Возможно, провайдер использует новые методы блокировки, которые требуют обновления стратегии обхода в GoodbyeDPI или Zapret. Также проблема может быть вызвана конфликтом кэша браузера. Попробуйте обновить конфигурацию и очистить кэш DNS.",
            "Обновить конфигурацию"
        );
        AddFaqItem(
            "У меня не работает ТГ и ДС, хотя всё скачано и включено",
            "Если ничего не работает при запущенных службах, вероятнее всего, произошел конфликт портов или сетевой адаптер Windows перешел в некорректное состояние. Мы можем автоматически перезапустить все сетевые интерфейсы и службы.",
            "Сбросить сеть и перезапустить"
        );
    }
    
    private void AddFaqItem(string title, string manualText, string autoBtnText)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(20, 16, 20, 16)
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        var text = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 16, 0)
        };
        grid.Children.Add(text);
        
        var btn = new Button
        {
            Content = "Исправить",
            Style = (Style)FindResource("AccentBtn"),
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13
        };
        Grid.SetColumn(btn, 1);
        
        btn.Click += (_, _) => 
        {
            SolutionTitle.Text = title;
            SolutionManualText.Text = manualText;
            SolutionAutoFixBtn.Content = CreateButtonContentWithIcon("BoltIcon", autoBtnText, Brushes.White);
            
            FaqPage.Visibility = Visibility.Collapsed;
            SolutionPage.Visibility = Visibility.Visible;
        };
        grid.Children.Add(btn);
        
        card.Child = grid;
        FaqContainer.Children.Add(card);
    }

    private void SolutionAutoFixBtn_Click(object s, RoutedEventArgs e)
    {
        SolutionPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        FixBtn_Click(s, e);
    }

    // ── Internet check ───────────────────────────────────────────────────────
    private void CheckInternetOnStart()
    {
        Task.Run(async () =>
        {
            bool ok = await DiagnosticsEngine.CheckInternetAsync();
            Dispatcher.Invoke(() =>
            {
                if (ok)
                {
                    NetDot.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    NetLbl.Text = "Сеть";
                }
                else
                {
                    NetDot.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    NetLbl.Text = "Нет сети";
                    NetLbl.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    NoInternetPage.Visibility = Visibility.Visible;
                }
            });
        });
    }

    private void RetryNet_Click(object s, RoutedEventArgs e)
    {
        NoInternetPage.Visibility = Visibility.Collapsed;
        CheckInternetOnStart();
    }

    private void ForceOpenNet_Click(object s, RoutedEventArgs e)
    {
        NoInternetPage.Visibility = Visibility.Collapsed;
    }

    // ── Active apps monitor ──────────────────────────────────────────────────
    private void StartActiveAppsMonitor()
    {
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _monitorTimer.Tick += (_, _) => UpdateActiveApps();
        _monitorTimer.Start();
        UpdateActiveApps();
    }

    private void UpdateActiveApps()
    {
        Task.Run(async () =>
        {
            var st = DiagnosticsEngine.CheckAppStatus();
            bool vpn = DetectVpn(out string _);
            bool netOk = await DiagnosticsEngine.CheckInternetAsync();

            Dispatcher.Invoke(() =>
            {
                var greenBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                var grayBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                var redBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

                VpnDot.Fill = vpn ? greenBrush : grayBrush;
                ZapretDot.Fill = st.ZapretRunning ? greenBrush : grayBrush;
                TgWsDot.Fill = st.TgWsProxyRunning ? greenBrush : grayBrush;

                // Синхронизация точек в карточке управления
                ZapretDot2.Fill = st.ZapretRunning ? greenBrush : grayBrush;
                ZapretStatusLbl.Text = st.ZapretRunning ? "Запущен" : "Не запущен";
                ZapretStatusLbl.Foreground = st.ZapretRunning ? greenBrush : grayBrush;
                ZapretToggleBtn.Content = st.ZapretRunning 
                    ? "■  Закрыть" 
                    : CreateButtonContentWithIcon("PlayIcon", "Запустить", Brushes.White);
                ZapretToggleBtn.Background = st.ZapretRunning
                    ? new SolidColorBrush(Color.FromRgb(0x3d, 0x1a, 0x1a))
                    : new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
                ZapretToggleBtn.Foreground = st.ZapretRunning ? redBrush : Brushes.White;
                
                // Обновить отображение активного конфига
                UpdateActiveConfigDisplay(st.ZapretRunning);

                TgWsDot2.Fill = st.TgWsProxyRunning ? greenBrush : grayBrush;
                TgWsStatusLbl.Text = st.TgWsProxyRunning ? "Запущен" : "Не запущен";
                TgWsStatusLbl.Foreground = st.TgWsProxyRunning ? greenBrush : grayBrush;
                TgWsToggleBtn.Content = st.TgWsProxyRunning 
                    ? "■  Закрыть" 
                    : CreateButtonContentWithIcon("PlayIcon", "Запустить", Brushes.White);
                TgWsToggleBtn.Background = st.TgWsProxyRunning
                    ? new SolidColorBrush(Color.FromRgb(0x3d, 0x1a, 0x1a))
                    : new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
                TgWsToggleBtn.Foreground = st.TgWsProxyRunning ? redBrush : Brushes.White;

                if (netOk)
                {
                    NetDot.Fill = greenBrush;
                    NetLbl.Text = "Сеть";
                    NetLbl.Foreground = grayBrush;
                    
                    if (NoInternetPage.Visibility == Visibility.Visible)
                        NoInternetPage.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NetDot.Fill = redBrush;
                    NetLbl.Text = "Нет сети";
                    NetLbl.Foreground = redBrush;
                }
                
                // Обновляем Discord только если не в игре И не идет сканирование
                if (!_isInGame && !_discord.IsScanning)
                    _discord.SetAllGood(st.ZapretRunning, st.TgWsProxyRunning);
            });
        });
    }

    private static bool DetectVpn(out string info)
    {
        info = "";
        try
        {
            foreach (var v in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
            {
                var val = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(val)) { info = $"proxy env: {val}"; return true; }
            }
            var result = RunProcess("ipconfig", "");
            string[] vpnKw = ["tap-windows", "wireguard", "wintun", "nordvpn", "expressvpn",
                               "openvpn", "outline", "warp", "mullvad", "proton", "tun"];
            foreach (var kw in vpnKw)
                if (result.ToLower().Contains(kw)) { info = kw; return true; }
        }
        catch { }
        return false;
    }

    private static string RunProcess(string name, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(name, args)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return o;
        }
        catch { return ""; }
    }

    // ── Log ──────────────────────────────────────────────────────────────────
    private async Task WriteStartupLogAsync()
    {
        const int d = 60; // задержка между строками (мс)
        var hour = DateTime.Now.Hour;
        string greeting = hour >= 5 && hour < 12 ? "Доброе утро" :
                          hour >= 12 && hour < 18 ? "Добрый день" :
                          "Добрый вечер";

        await Task.Delay(d);
        AppendLog($"{greeting}! Добро пожаловать в NetFix 🚀", "final");
        await Task.Delay(d);
        AppendLog("Инициализация компонентов системы...", "info");
        await Task.Delay(d);
        AppendLink("Мой Telegram-канал: ", "t.me/NetFixRuBi", " - информация об обновлениях, новые способы обходов и гайды. Максимально полезная информация, советую подписаться", "https://t.me/NetFixRuBi");
        await Task.Delay(d);
        AppendLog("spacer");

        var status = DiagnosticsEngine.CheckAppStatus();
        bool admin;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            admin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { admin = false; }

        await Task.Delay(d);
        AppendLog("СТАТУС СЕРВИСОВ И ПРИЛОЖЕНИЙ", "system");
        await Task.Delay(d);
        AppendLog($"Обход блокировок (Zapret):    [ {(status.ZapretRunning ? "ЗАПУЩЕН" : "ВЫКЛЮЧЕН")} ]", status.ZapretRunning ? "ok" : "warn");
        await Task.Delay(d);
        AppendLog($"Прокси для Telegram:          [ {(status.TgWsProxyRunning ? "ЗАПУЩЕН" : "ВЫКЛЮЧЕН")} ]", status.TgWsProxyRunning ? "ok" : "warn");
        await Task.Delay(d);
        AppendLog($"Права администратора (UAC):   [ {(admin ? "ПОДТВЕРЖДЕНЫ" : "НЕ ПОЛУЧЕНЫ")} ]", admin ? "ok" : "warn");
        await Task.Delay(d);
        AppendLog("spacer");

        if (status.ZapretRunning && status.TgWsProxyRunning && admin)
        {
            await Task.Delay(d);
            AppendLog("Всё запущено и всё работает НОРМАЛЬНО!", "final");
            await Task.Delay(d);
            AppendLog("Zapret включен. Discord и YouTube должны работать нормально.", "ok");
            await Task.Delay(d);
            AppendLog("Прокси настроен. Telegram должен работать стабильно.", "ok");
        }
        else
        {
            await Task.Delay(d);
            AppendLog("Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».", "info");
        }
        await Task.Delay(d);
        AppendLog("spacer");
    }

    private void AppendLink(string prefix, string linkText, string suffix, string url)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss");
        Dispatcher.Invoke(() =>
        {
            var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            para.Inlines.Add(new System.Windows.Documents.Run($"[{ts}] [INFO] ")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize = 10,
                FontFamily = new FontFamily("Consolas")
            });
            para.Inlines.Add(new System.Windows.Documents.Run(prefix)
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI")
            });
            // только ссылка подчёркнута
            var linkRun = new System.Windows.Documents.Run(linkText)
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextDecorations = TextDecorations.Underline,
                Tag = url
            };
            para.Inlines.Add(linkRun);
            para.Inlines.Add(new System.Windows.Documents.Run(suffix)
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI")
            });
            LogBox.Document.Blocks.Add(para);
            LogBox.ScrollToEnd();
        });
    }

    private void LogBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(LogBox);
        var pointer = LogBox.GetPositionFromPoint(pos, true);
        if (pointer?.Parent is System.Windows.Documents.Run run && run.Tag is string url && !string.IsNullOrEmpty(url))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            e.Handled = true;
        }
    }

    private void LogBox_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(LogBox);
        var pointer = LogBox.GetPositionFromPoint(pos, true);
        LogBox.Cursor = pointer?.Parent is System.Windows.Documents.Run run && run.Tag is string url && !string.IsNullOrEmpty(url)
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
    }

    private void AppendLog(string msg, string kind = "info")
    {
        if (msg == "spacer") {
            Dispatcher.Invoke(() => LogBox.Document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.LineBreak()) { Margin = new Thickness(0, 5, 0, 5) }));
            return;
        }

        if (string.IsNullOrWhiteSpace(msg)) return;

        Dispatcher.Invoke(() =>
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            Color textColor = Color.FromRgb(0xcc, 0xcc, 0xcc);
            string prefix = "";
            double fontSize = 12;
            bool isBold = false;

            switch (kind)
            {
                case "frame":
                    textColor = Color.FromRgb(0x3b, 0x82, 0xf6);
                    isBold = true;
                    break;
                case "system":
                    textColor = Color.FromRgb(0xff, 0xff, 0xff);
                    prefix = "[#] ";
                    isBold = true;
                    break;
                case "net": prefix = "[NET] "; break;
                case "speed": prefix = "[SPEED] "; break;
                case "dpi": prefix = "[DPI] "; break;
                case "ok":
                    textColor = Color.FromRgb(0x22, 0xc5, 0x5e);
                    prefix = "[OK] ";
                    break;
                case "warn":
                    textColor = Color.FromRgb(0xea, 0xb3, 0x08);
                    prefix = "[WARN] ";
                    break;
                case "error":
                    textColor = Color.FromRgb(0xef, 0x44, 0x44);
                    prefix = "[ERROR] ";
                    break;
                case "final":
                    textColor = Color.FromRgb(0x22, 0xc5, 0x5e);
                    fontSize = 15;
                    isBold = true;
                    prefix = "🚀 ";
                    break;
                case "info":
                    prefix = "[INFO] ";
                    break;
                default:
                    prefix = "🔹 ";
                    break;
            }

            var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            
            if (kind != "frame" && kind != "final")
            {
                para.Inlines.Add(new System.Windows.Documents.Run($"[{ts}] ") 
                { 
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas")
                });
            }

            para.Inlines.Add(new System.Windows.Documents.Run($"{prefix}{msg}") 
            { 
                Foreground = new SolidColorBrush(textColor),
                FontSize = fontSize,
                FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal,
                FontFamily = (kind == "frame" || kind == "progress") ? new FontFamily("Consolas") : new FontFamily("Segoe UI")
            });

            LogBox.Document.Blocks.Add(para);
            LogBox.ScrollToEnd();
        });
    }

    private string GetProgressBar(double percent)
    {
        int totalBlocks = 20;
        int filledBlocks = (int)(percent / 100 * totalBlocks);
        return "[" + new string('█', filledBlocks) + new string('░', totalBlocks - filledBlocks) + $"] {percent:0}%";
    }

    private void ClearLog_Click(object s, RoutedEventArgs e) => LogBox.Document.Blocks.Clear();

    private void ShowPlayWhileScanDialog()
    {
        if (!_settings.ShowGameOfferDialog) return;

        // Создаем overlay
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        // Карточка диалога
        var dialogCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 400,
            Margin = new Thickness(40),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.6
            }
        };
        Grid.SetRowSpan(dialogCard, 3);

        var content = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        // Иконка геймпада
        var iconBorder = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(26),
            Background = new SolidColorBrush(Color.FromArgb(30, 0x63, 0x66, 0xf1)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        
        var iconViewbox = new Viewbox { Width = 28, Height = 28 };
        var iconCanvas = new Canvas { Width = 512, Height = 512 };
        
        // Основной контур геймпада
        var gamepadPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M511.27,331.566L511.27,331.566c0-0.009,0-0.018,0-0.026c-0.008-0.052,0-0.087-0.008-0.14h-0.009 c-6.682-88.65-27.159-154.403-55.948-198.846c-14.412-22.221-30.968-39.115-49.041-50.507 c-18.048-11.401-37.649-17.198-57.388-17.18c-14.551-0.009-26.985,2.629-37.527,6.611c-15.836,5.97-27.358,14.795-36.364,21.319 c-4.495,3.28-8.373,5.961-11.549,7.592c-3.211,1.658-5.475,2.239-7.436,2.239c-1.328-0.009-2.725-0.251-4.521-0.92 c-3.115-1.137-7.288-3.732-12.278-7.332c-7.531-5.354-16.885-12.764-29.223-18.846c-12.339-6.092-27.766-10.69-46.855-10.664 c-19.739-0.018-39.34,5.787-57.388,17.18c-27.115,17.119-50.794,46.481-69.008,87.887C18.542,211.332,5.743,264.92,0.746,331.401 H0.738c-0.009,0.052,0,0.096-0.009,0.14c0,0.008,0,0.017,0,0.026l0,0C0.243,336.981,0,342.247,0,347.358 c-0.009,25.058,5.77,46.455,16.651,63.141c10.846,16.694,26.863,28.347,45.614,33.822c6.43,1.892,13.068,2.811,19.757,2.811 c19.445-0.026,39.046-7.618,57.692-20.764c18.681-13.189,36.598-32.052,52.91-55.731c7.845-11.427,18.5-24.798,29.987-34.854 c5.736-5.032,11.662-9.214,17.362-12.026c5.71-2.82,11.09-4.244,16.027-4.235c4.936-0.009,10.317,1.414,16.026,4.235 c8.555,4.199,17.588,11.558,25.787,20.112c8.226,8.538,15.67,18.196,21.562,26.76c16.312,23.688,34.23,42.55,52.902,55.739 c18.655,13.146,38.255,20.738,57.7,20.764c6.69,0,13.328-0.92,19.749-2.811c18.759-5.475,34.776-17.128,45.614-33.822 C506.221,393.813,512,372.416,512,347.358C512,342.256,511.757,336.981,511.27,331.566z M476.737,398.36 c-8.104,12.356-19.236,20.469-33.284,24.651c-4.33,1.275-8.807,1.9-13.475,1.908c-13.484,0.026-28.902-5.414-44.894-16.703 c-15.974-11.254-32.312-28.225-47.418-50.177c-8.564-12.417-20.044-27.012-33.64-38.95c-6.812-5.97-14.169-11.297-22.16-15.245 c-7.975-3.94-16.677-6.534-25.866-6.534c-9.189,0-17.892,2.594-25.866,6.534c-11.974,5.943-22.577,14.906-31.957,24.616 c-9.353,9.726-17.432,20.268-23.843,29.579c-15.106,21.952-31.454,38.923-47.419,50.177 c-15.991,11.288-31.418,16.729-44.894,16.703c-4.677-0.009-9.145-0.633-13.484-1.908c-14.04-4.182-25.172-12.295-33.284-24.651 c-8.06-12.364-13.04-29.293-13.04-51.002c0-4.451,0.208-9.111,0.65-13.961v-0.052l0.009-0.113 c6.429-86.17,26.446-148.582,52.451-188.59c12.989-20.026,27.41-34.447,42.256-43.801c14.872-9.353,30.126-13.744,45.544-13.761 c11.896,0.009,21.424,2.091,29.675,5.189c12.356,4.65,21.883,11.756,31.158,18.507c4.652,3.367,9.233,6.655,14.378,9.336 c5.111,2.655,11.028,4.729,17.666,4.729c4.399,0,8.556-0.928,12.286-2.325c6.56-2.482,12-6.213,17.422-10.065 c8.113-5.831,16.208-12.14,26.091-16.981c9.883-4.833,21.449-8.364,37.076-8.39c15.418,0.017,30.672,4.408,45.545,13.761 c22.264,14.005,43.6,39.532,60.511,78.03c16.92,38.464,29.354,89.735,34.195,154.36v0.052l0.009,0.113 c0.434,4.842,0.652,9.502,0.652,13.961C489.778,369.067,484.806,386.004,476.737,398.36z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1))
        };
        iconCanvas.Children.Add(gamepadPath);
        
        // D-pad (крестовина)
        var dpadPath = new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection { 
                new System.Windows.Point(161.232,178.126), new System.Windows.Point(122.29,178.126), 
                new System.Windows.Point(122.29,213.631), new System.Windows.Point(86.785,213.631), 
                new System.Windows.Point(86.785,252.573), new System.Windows.Point(122.29,252.573), 
                new System.Windows.Point(122.29,288.079), new System.Windows.Point(161.232,288.079), 
                new System.Windows.Point(161.232,252.573), new System.Windows.Point(196.737,252.573), 
                new System.Windows.Point(196.737,213.631), new System.Windows.Point(161.232,213.631) 
            },
            Fill = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1))
        };
        iconCanvas.Children.Add(dpadPath);
        
        // Кнопки (4 круга)
        var button1 = new System.Windows.Shapes.Ellipse { Width = 41.076, Height = 41.076, Fill = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1)) };
        Canvas.SetLeft(button1, 348.139);
        Canvas.SetTop(button1, 167.002);
        iconCanvas.Children.Add(button1);
        
        var button2 = new System.Windows.Shapes.Ellipse { Width = 41.058, Height = 41.068, Fill = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1)) };
        Canvas.SetLeft(button2, 348.139);
        Canvas.SetTop(button2, 266.247);
        iconCanvas.Children.Add(button2);
        
        var button3 = new System.Windows.Shapes.Ellipse { Width = 41.076, Height = 41.059, Fill = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1)) };
        Canvas.SetLeft(button3, 397.744);
        Canvas.SetTop(button3, 216.633);
        iconCanvas.Children.Add(button3);
        
        var button4 = new System.Windows.Shapes.Ellipse { Width = 41.078, Height = 41.059, Fill = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1)) };
        Canvas.SetLeft(button4, 298.516);
        Canvas.SetTop(button4, 216.633);
        iconCanvas.Children.Add(button4);
        
        iconViewbox.Child = iconCanvas;
        iconBorder.Child = iconViewbox;
        content.Children.Add(iconBorder);

        content.Children.Add(new TextBlock
        {
            Text = "Скучно ждать?",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        });

        content.Children.Add(new TextBlock
        {
            Text = "Сканирование займёт время. Хочешь поиграть пока идёт проверка?",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 22)
        });

        var dontShowAgain = new System.Windows.Controls.CheckBox
        {
            Content = "Больше не показывать",
            Style = (Style)FindResource("Toggle"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 13,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            IsChecked = !_settings.ShowGameOfferDialog
        };
        content.Children.Add(dontShowAgain);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var yesBtn = new Button
        {
            Width = 130,
            Height = 40,
            Margin = new Thickness(0, 0, 10, 0),
            Style = (Style)FindResource("AccentBtn"),
            Background = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1)),
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        yesBtn.Content = "Поиграть!";
        yesBtn.Click += (_, _) =>
        {
            if (dontShowAgain.IsChecked == true)
            {
                _settings.ShowGameOfferDialog = false;
                SettingsService.Save(_settings);
            }
            ShowGameOfferCB.IsChecked = _settings.ShowGameOfferDialog;
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
            ShowGameOverlay();
        };

        var noBtn = new Button
        {
            Width = 90,
            Height = 40,
            Style = (Style)FindResource("OutlineBtn"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 13
        };
        noBtn.Content = "Нет";
        noBtn.Click += (_, _) =>
        {
            if (dontShowAgain.IsChecked == true)
            {
                _settings.ShowGameOfferDialog = false;
                SettingsService.Save(_settings);
            }
            ShowGameOfferCB.IsChecked = _settings.ShowGameOfferDialog;
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
        };

        btnPanel.Children.Add(yesBtn);
        btnPanel.Children.Add(noBtn);
        content.Children.Add(btnPanel);

        dialogCard.Child = content;
        MainGrid.Children.Add(dialogCard);

        // Анимация появления
        overlay.Opacity = 0;
        dialogCard.Opacity = 0;
        dialogCard.RenderTransform = new ScaleTransform(0.92, 0.92);
        dialogCard.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        dialogCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(280))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(280))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        overlay.MouseLeftButtonDown += (_, _) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
        };
    }

    private void CopyLog_Click(object s, RoutedEventArgs e)
    {
        var textRange = new System.Windows.Documents.TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd);
        if (!string.IsNullOrWhiteSpace(textRange.Text))
        {
            try { Clipboard.SetText(textRange.Text); } catch { }
        }
    }

    // ── Auto-setup ───────────────────────────────────────────────────────────
    private async void FixBtn_Click(object s, RoutedEventArgs e)
    {
        // Показываем предложение поиграть
        ShowPlayWhileScanDialog();
        
        // Запускаем таймер на 10 секунд
        _checkInProgress = true;
        StartLongCheckTimer();
        Console.WriteLine("[FixBtn] Таймер запущен, _checkInProgress = true");
        
        // Проверяем, требуется ли обновление компонентов
        var (needsUpdate, reason) = await ComponentVersionService.CheckIfUpdateNeededAsync(_settings);
        
        if (needsUpdate)
        {
            Console.WriteLine($"[FixBtn] Обнаружена необходимость обновления: {reason}");
            // Останавливаем таймер и запускаем автоматическую установку/обновление
            StopLongCheckTimer();
            _checkInProgress = false;
            await RunAutoInstallAsync();
            return;
        }
        
        Console.WriteLine("[FixBtn] Компоненты актуальны, запускаем стандартную логику");

        if (_settings.Mode == FixMode.Fast)
        {
            RunFastFix();
            return;
        }

        var st = DiagnosticsEngine.CheckAppStatus();

        // 1. Проверяем Zapret
        if (!st.ZapretRunning && !string.IsNullOrWhiteSpace(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
        {
            StopLongCheckTimer();
            _checkInProgress = false;
            ShowZapretWizard();
            return;
        }

        // 2. Проверяем TgWsProxy
        if (!st.TgWsProxyRunning && !string.IsNullOrWhiteSpace(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
        {
            StartTgWsProxyWithActivation();
        }

        // Таймер остановится в doneCb внутри RunAutoFix
        RunAutoFix();
    }

    private async void RunAutoFix()
    {
        // Предотвращаем повторный запуск
        if (_autoFixRunning)
        {
            Console.WriteLine("[RunAutoFix] Уже запущен, пропускаем");
            return;
        }
        
        _autoFixRunning = true;
        Console.WriteLine("[RunAutoFix] Начинаем выполнение");
        
        FixBtn.IsEnabled = false;
        SetupProg.Value = 0;
        SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Синий (начальный цвет)
        SetupProgLbl.Text = "Подготовка...";
        LogBox.Document.Blocks.Clear();

        // Убрали линии, оставили только текст
        string timeStr = DateTime.Now.ToString("HH:mm:ss");
        AppendLog($"СИСТЕМНАЯ ДИАГНОСТИКА [ ВРЕМЯ: {timeStr} ]", "system");
        AppendLog("spacer");

        StartGlow();
        
        // Блокируем авто-обновления Discord во время чинки
        _discord.IsScanning = true;
        _discord.SetFixing();

        // --- ЭТАП 1: СЕТЬ ---
        AppendLog("СЕТЕВАЯ СРЕДА", "system");
        bool netOk = await DiagnosticsEngine.CheckInternetAsync();
        AppendLog($"Интернет-соединение: {(netOk ? "[ ПОДКЛЮЧЕНО ]" : "[ ОШИБКА ]")}", netOk ? "ok" : "error");
        
        // --- ЭТАП 2: СКАНИРОВАНИЕ ---
        AppendLog("АНАЛИЗ ТРАФИКА И DPI", "system");
        var report = await DiagnosticsEngine.RunFullDiagnosticsAsync(
            (ratio, label) => Dispatcher.Invoke(() => {
                SetupProg.Value = ratio * 50;
                SetupProgLbl.Text = label;
                
                var lastPara = LogBox.Document.Blocks.LastBlock as System.Windows.Documents.Paragraph;
                if (lastPara?.Tag?.ToString() == "prog") LogBox.Document.Blocks.Remove(lastPara);
                
                var p = new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("    " + GetProgressBar(ratio * 100))) 
                { 
                    Tag = "prog", 
                    Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
                    Margin = new Thickness(0, 4, 0, 4),
                    FontFamily = new FontFamily("Consolas")
                };
                LogBox.Document.Blocks.Add(p);
            })
        );
        
        // Дублируем отчет во вкладку диагностики
        Dispatcher.Invoke(() => RenderDiagReport(report));
        
        AppendLog("Обнаружена блокировка протоколов (DPI/ТСПУ)", "dpi");
        AppendLog("spacer");

        // --- ЭТАП 3: СЕРВИСЫ ---
        AppendLog("СОСТОЯНИЕ СЕРВИСОВ", "system");
        AppendLog($"Telegram Desktop: {(report.AppStatus?.TelegramRunning == true ? "[ ЗАПУЩЕН ]" : "[ НЕ В СЕТИ ]")}", "net");
        AppendLog($"Discord App:      {(report.AppStatus?.DiscordRunning == true ? "[ ЗАПУЩЕН ]" : "[ НЕ В СЕТИ ]")}", "net");
        
        int srvOk = report.DcResults.Count(d => d.Ok);
        AppendLog($"Доступность серверов Telegram: {srvOk} из {report.DcResults.Count}", srvOk > 0 ? "ok" : "warn");
        AppendLog("spacer");

        // --- ЭТАП 4: ЗАПУСК ОБХОДА ---
        AppendLog("ЗАПУСК ИСПРАВЛЕНИЙ", "system");
        AutoSetupService.Run(
            logCb: (msg, kind) => AppendLog(msg, kind == "step" ? "speed" : kind),
            progressCb: ratio => Dispatcher.Invoke(() => {
                SetupProg.Value = 50 + (ratio * 50);
                SetupProgLbl.Text = $"Настройка: {(int)(ratio * 100)}%";
            }),
            doneCb: (success, _) => Dispatcher.Invoke(() => {
                StopGlow(success);
                
                // Разблокируем авто-обновления Discord
                _discord.IsScanning = false;
                
                if (success) _discord.SetAllGood(
                    DiagnosticsEngine.CheckAppStatus().ZapretRunning,
                    DiagnosticsEngine.CheckAppStatus().TgWsProxyRunning);
                else _discord.SetProblems("Ошибка автонастройки");
                FixBtn.IsEnabled = true;
                
                // Останавливаем таймер долгой проверки
                StopLongCheckTimer();
                _checkInProgress = false;
                
                // Сбрасываем флаг выполнения
                _autoFixRunning = false;
                Console.WriteLine("[RunAutoFix] Завершено");
                
                if (success) {
                    SetupProg.Value = 100;
                    SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Зеленый
                    SetupProgLbl.Text = "Готово";
                    AppendLog("spacer");
                    AppendLog("Всё запущено и всё работает НОРМАЛЬНО!", "final");
                    AppendLog("Zapret включен. Discord и YouTube должны работать нормально.", "ok");
                    AppendLog("Прокси настроен. Telegram должен работать стабильно.", "ok");
                    AppendLog("Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».", "info");
                    PlaySuccessRing();
                } else {
                    SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Красный
                    AppendLog("Произошла ошибка при автоматической настройке. Проверьте пути в настройках.", "error");
                    PlayErrorRing();
                }
            }),
            settings: _settings);
    }

    private async void RunFastFix()
    {
        if (_autoFixRunning) return;
        _autoFixRunning = true;

        FixBtn.IsEnabled = false;
        SetupProg.Value = 0;
        SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
        SetupProgLbl.Text = "Быстрый запуск...";
        LogBox.Document.Blocks.Clear();

        string timeStr = DateTime.Now.ToString("HH:mm:ss");
        AppendLog($"БЫСТРЫЙ ЗАПУСК [ {timeStr} ]", "system");
        AppendLog("spacer");

        StartGlow();
        _discord.IsScanning = true;
        _discord.SetFixing();

        var st = DiagnosticsEngine.CheckAppStatus();
        bool zapretNeeded = !st.ZapretRunning
            && !string.IsNullOrWhiteSpace(_settings.ZapretPath)
            && File.Exists(_settings.ZapretPath);
        bool tgwsNeeded = !st.TgWsProxyRunning
            && !string.IsNullOrWhiteSpace(_settings.TgWsProxyPath)
            && File.Exists(_settings.TgWsProxyPath);

        // ═══ ДИАГНОСТИКА — всегда ═══
        AppendLog("СОСТОЯНИЕ СЕРВИСОВ", "system");
        bool tgRunning = Process.GetProcessesByName("Telegram").Length > 0;
        AppendLog($"Telegram Desktop: [ {(tgRunning ? "ЗАПУЩЕН" : "НЕ ЗАПУЩЕН")} ]", "net");
        bool dcRunning = Process.GetProcessesByName("Discord").Length > 0;
        AppendLog($"Discord App:      [ {(dcRunning ? "ЗАПУЩЕН" : "НЕ ЗАПУЩЕН")} ]", "net");

        AppendLog("spacer");
        AppendLog("СЕТЕВАЯ СРЕДА", "system");
        bool netOk = await DiagnosticsEngine.CheckInternetAsync();
        AppendLog($"Интернет-соединение: [ {(netOk ? "ПОДКЛЮЧЕНО" : "НЕТ ПОДКЛЮЧЕНИЯ")} ]", "ok");

        AppendLog("spacer");
        AppendLog("ЗАПУСК ИСПРАВЛЕНИЙ", "system");
        AppendLog("Проверяю подключение к интернету…", "info");
        if (netOk)
            AppendLog("Интернет есть", "ok");
        else
            AppendLog("Нет подключения к интернету", "error");
        AppendLog("Проверяю последние версии инструментов…", "info");
        if (!string.IsNullOrWhiteSpace(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
            AppendLog($"Zapret найден: {_settings.ZapretPath}", "ok");
        else
            AppendLog("Zapret не найден", "warn");
        if (!string.IsNullOrWhiteSpace(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
            AppendLog($"tg-ws-proxy найден: {_settings.TgWsProxyPath}", "ok");
        else
            AppendLog("tg-ws-proxy не найден", "warn");

        // Всё уже запущено
        if (!zapretNeeded && !tgwsNeeded)
        {
            AppendLog("Zapret уже запущен", "ok");
            AppendLog("tg-ws-proxy уже запущен", "ok");

            AppendLog("spacer");
            AppendLog("Всё запущено и всё работает НОРМАЛЬНО!", "final");
            AppendLog("Zapret включен. Discord и YouTube должны работать нормально.", "ok");
            AppendLog("Прокси настроен. Telegram должен работать стабильно.", "ok");
            AppendLog("Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».", "info");

            StopGlow(true);
            _discord.IsScanning = false;
            _discord.SetAllGood(true, true);
            SetupProg.Value = 100;
            SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            SetupProgLbl.Text = "Всё уже работает";
            PlaySuccessRing();
            StopLongCheckTimer();
            _checkInProgress = false;
            _autoFixRunning = false;
            FixBtn.IsEnabled = true;
            return;
        }

        // ═══ ЗАПУСК НЕДОСТАЮЩИХ ═══
        bool zapretOk = !zapretNeeded;
        bool tgwsOk = !tgwsNeeded;

        // Zapret
        if (zapretNeeded)
        {
            AppendLog("Zapret не запущен — запускаю...", "info");
            var isServiceBat = Path.GetFileName(_settings.ZapretPath)
                .Equals("service.bat", StringComparison.OrdinalIgnoreCase);

            if (isServiceBat)
            {
                var cache = ZapretConfigService.LoadCache();
                bool success = await ZapretConfigService.ApplyConfigAsync(
                    _settings.ZapretPath, cache?.CurrentConfig ?? "");
                zapretOk = success;
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_settings.ZapretPath) { UseShellExecute = true });
                    zapretOk = true;
                }
                catch (Exception ex) { AppendLog($"Ошибка: {ex.Message}", "error"); }
            }

            if (zapretOk) AppendLog("✓ Zapret запущен", "ok");
            else AppendLog("✗ Не удалось запустить Zapret", "error");
        }
        else
        {
            AppendLog("Zapret уже работает", "ok");
        }

        SetupProg.Value = 50;
        SetupProgLbl.Text = "TgWsProxy...";
        await Task.Delay(300);

        // TgWsProxy
        if (tgwsNeeded)
        {
            AppendLog("TgWsProxy не запущен — запускаю...", "info");
            if (!string.IsNullOrWhiteSpace(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
            {
                try
                {
                    var psi = new ProcessStartInfo(_settings.TgWsProxyPath)
                    {
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    Process.Start(psi);
                    tgwsOk = true;
                    AppendLog("✓ TgWsProxy запущен", "ok");
                }
                catch (Exception ex) { AppendLog($"Ошибка: {ex.Message}", "error"); }
            }
            else
            {
                AppendLog("Путь к TgWsProxy не указан или файл не найден", "warn");
            }
        }
        else
        {
            AppendLog("TgWsProxy уже работает", "ok");
        }

        await Task.Delay(300);
        SetupProg.Value = 100;
        SetupProgLbl.Text = "Готово";

        StopGlow(zapretOk || tgwsOk);
        _discord.IsScanning = false;

        if (zapretOk && tgwsOk)
        {
            SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            _discord.SetAllGood(true, true);
            AppendLog("spacer");
            AppendLog("Всё запущено и всё работает НОРМАЛЬНО!", "final");
            AppendLog("Zapret включен. Discord и YouTube должны работать нормально.", "ok");
            AppendLog("Прокси настроен. Telegram должен работать стабильно.", "ok");
            AppendLog("Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».", "info");
            PlaySuccessRing();
        }
        else
        {
            _discord.SetProblems("Ошибка запуска");
            SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            AppendLog("Не удалось запустить некоторые сервисы. Проверьте пути в настройках.", "error");
            PlayErrorRing();
        }

        StopLongCheckTimer();
        _checkInProgress = false;
        _autoFixRunning = false;
        FixBtn.IsEnabled = true;
    }

    /// <summary>
    /// Запускает автоматическую установку/обновление компонентов
    /// </summary>
    private async Task RunAutoInstallAsync()
    {
        FixBtn.IsEnabled = false;
        SetupProg.Value = 0;
        SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Синий (начальный цвет)
        SetupProgLbl.Text = "Подготовка к установке...";
        LogBox.Document.Blocks.Clear();

        string timeStr = DateTime.Now.ToString("HH:mm:ss");
        AppendLog($"АВТОМАТИЧЕСКАЯ УСТАНОВКА КОМПОНЕНТОВ [ ВРЕМЯ: {timeStr} ]", "system");
        AppendLog("spacer");

        StartGlow();

        AppendLog("Запуск автоматической установки компонентов...", "info");
        AppendLog("Это может занять несколько минут.", "info");
        AppendLog("spacer");

        bool success = await AutoDownloadService.AutoInstallAllAsync(
            onLog: msg => Dispatcher.Invoke(() => AppendLog(msg, "info")),
            onProgress: ratio => Dispatcher.Invoke(() => {
                SetupProg.Value = ratio * 100;
                SetupProgLbl.Text = $"Установка: {(int)(ratio * 100)}%";
            }),
            onError: err => Dispatcher.Invoke(() => {
                AppendLog("spacer");
                AppendLog(err, "error");
            })
        );

        Dispatcher.Invoke(() => {
            StopGlow(success);
            FixBtn.IsEnabled = true;
            
            if (success)
            {
                // Перезагружаем настройки после успешной установки
                _settings = SettingsService.Load();
                LoadSettingsToPanel();
                
                SetupProg.Value = 100;
                SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Зеленый
                SetupProgLbl.Text = "Установка завершена";
                AppendLog("spacer");
                AppendLog("✓ Компоненты успешно установлены/обновлены!", "final");
                AppendLog("Теперь можно запустить сервисы через панель управления.", "ok");
                AppendLog("Или нажмите кнопку «Починить интернет» ещё раз для автоматического запуска.", "info");
                PlaySuccessRing();
                
                // Обновляем статус активных приложений
                UpdateActiveApps();
            }
            else
            {
                SetupProg.Value = 0;
                SetupProg.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Красный
                SetupProgLbl.Text = "Ошибка установки";
                AppendLog("spacer");
                AppendLog("Произошла ошибка при установке компонентов.", "error");
                AppendLog("Попробуйте установить компоненты вручную через настройки.", "error");
                AppendLog("spacer");
                AppendLog("Если вы хотите обновить компоненты, перейдите в настройки → «Пройти онбординг заново»,", "info");
                AppendLog("затем выберите «Ручная установка» и следуйте инструкциям.", "info");
                PlayErrorRing();
            }
        });
    }

    private void PlaySuccessRing()
    {
        double circumference = 2 * Math.PI * 97;
        SuccessArc.StrokeDashArray = new DoubleCollection { 0, circumference };
        SuccessArc.Visibility = Visibility.Visible;

        // Запускаем анимацию цвета СРАЗУ
        var icon = GetFixButtonIcon();
        if (icon != null) {
            var brush = new SolidColorBrush(Color.FromRgb(0x7c, 0x6a, 0xf7));
            icon.Stroke = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    Color.FromRgb(0x7c, 0x6a, 0xf7),
                    Color.FromRgb(0x22, 0xc5, 0x5e),
                    new Duration(TimeSpan.FromSeconds(1.8))) // Длительность = длительности круга
                { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (s, e) => {
            double t = Math.Min(sw.Elapsed.TotalSeconds / 5, 1.0);
            double ease = t == 0 ? 0 : 1 - Math.Pow(2, -10 * t);
            SuccessArc.StrokeDashArray = new DoubleCollection { ease * circumference, circumference };
            if (t >= 1.0) timer.Stop();
        };
        timer.Start();
    }

    private void StartGlow()
    {
        // Включаем "энергетический шторм"
        _splitTarget = 1;
        _colorTarget = 0;  // Возвращаем базовые цвета
        
        // Скрываем idle-кольца и другие состояния
        IdleRingOuter.Visibility = Visibility.Collapsed;
        IdleRingInner.Visibility = Visibility.Collapsed;
        ErrorRing.Visibility     = Visibility.Collapsed;
        SuccessArc.Visibility    = Visibility.Collapsed;
        SuccessCheck.Visibility  = Visibility.Collapsed;

        // Спиннер 1, по часовой, 1.4s (аналог CSS)
        SpinArc.Visibility = Visibility.Visible;
        var spin1 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.4)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinOffset.BeginAnimation(RotateTransform.AngleProperty, spin1);

        // Спиннер 2, по часовой, 1.9s (аналог CSS)
        SpinArc2.Visibility = Visibility.Visible;
        var spin2 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.9)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinRotation2.BeginAnimation(RotateTransform.AngleProperty, spin2);

        // ВМЕСТО BrushAnimation используем ColorAnimation на SolidColorBrush.ColorProperty
        if (GetFixButtonIcon() is System.Windows.Shapes.Path iconEl)
        {
            var animBrush = new SolidColorBrush(Color.FromRgb(0x7c, 0x6a, 0xf7));
            iconEl.Stroke = animBrush;  // сначала ставим кисть...
            
            var colorAnim = new ColorAnimation(
                Color.FromRgb(0x7c, 0x6a, 0xf7),
                Color.FromRgb(0x5b, 0x8d, 0xf5),
                new Duration(TimeSpan.FromSeconds(1.8)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            // ...и анимируем цвет внутри кисти, а не саму кисть
            animBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        }
    }

    private void StopGlow(bool success)
    {
        // Запоминаем результат и стягиваем пятна обратно
        _finalSuccess = success;
        _splitTarget = 0;  // Стягиваем пятна обратно в центр
        _colorTarget = 1;  // Перекрашиваем в результат (зеленый/красный)
        
        // Стоп все спиннеры
        SpinOffset.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinRotation2.BeginAnimation(RotateTransform.AngleProperty, null);

        SpinArc.Visibility  = Visibility.Collapsed;
        SpinArc2.Visibility = Visibility.Collapsed;
        
        if (GetFixButtonIcon() is System.Windows.Shapes.Path iconEl)
        {
            // Останавливаем анимацию на кисти если она SolidColorBrush
            if (iconEl.Stroke is SolidColorBrush brush)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            
            // Ставим новую статичную кисть
            iconEl.Stroke = new SolidColorBrush(success
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0xef, 0x44, 0x44));
        }
    }

    private void PlayErrorRing()
    {
        ErrorRing.Visibility = Visibility.Visible;
        
        // Меняем цвет иконки на красный
        SetFixButtonIconColor(Color.FromRgb(0xef, 0x44, 0x44));
        
        // Shake анимация кнопки (аналог CSS)
        var shakeTransform = new TranslateTransform();
        FixBtn.RenderTransform = shakeTransform;
        
        var shakeAnimation = new DoubleAnimationUsingKeyFrames();
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))));
        shakeAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
        
        shakeTransform.BeginAnimation(TranslateTransform.XProperty, shakeAnimation);
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────
    private System.Windows.Shapes.Path? GetFixButtonIcon()
    {
        FixBtn.ApplyTemplate();
        return FixBtn.Template.FindName("BtnIcon", FixBtn) as System.Windows.Shapes.Path;
    }

    private void SetFixButtonIconColor(Color color)
    {
        if (GetFixButtonIcon() is not System.Windows.Shapes.Path icon)
            return;

        icon.BeginAnimation(System.Windows.Shapes.Path.StrokeProperty, null);
        icon.Stroke = new SolidColorBrush(color);
    }

    private void DiagRunBtn_Click(object s, RoutedEventArgs e)
    {
        // Блокируем авто-обновления Discord во время сканирования
        _discord.IsScanning = true;
        _discord.SetDiagnostics(0, 0);
        
        DiagRunBtn.IsEnabled = false;
        DiagRunBtn.Content = "⏳  Проверяю…";
        DiagProg.Value = 0;
        DiagProgLbl.Text = "Запускаю диагностику…";
        DiagProgLbl.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)); // <-- Добавлена эта строка
        DiagResults.Children.Clear();

        Task.Run(async () =>
        {
            var report = await DiagnosticsEngine.RunFullDiagnosticsAsync(
                (ratio, label) => Dispatcher.Invoke(() =>
                {
                    DiagProg.Value = ratio * 100;
                    DiagProgLbl.Text = label;
                }));
            Dispatcher.Invoke(() => RenderDiagReport(report));
        });
    }

    private void RenderDiagReport(DiagReport r)
    {
        DiagResults.Children.Clear();

        // Основной вердикт
        var (em, title, detail, ck) = DiagnosticsEngine.HumanVerdict(r);
        AddCard(DiagResults, $"{em}  {title}", detail, ColorFromKey(ck));

        // Вердикт Discord
        var (dem, dtitle, ddetail, dck) = DiagnosticsEngine.DiscordVerdict(r);
        AddCard(DiagResults, $"{dem}  {dtitle}", ddetail, ColorFromKey(dck));

        // Статус приложений (без изменений)
        if (r.AppStatus is { } a)
        {
            var appsPanel = new StackPanel();
            void AddAppUI(string name, bool isRunning, string proc)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(isRunning ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0xef, 0x44, 0x44)), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(dot, 0);
                var nameText = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(nameText, 1);
                row.Children.Add(dot); row.Children.Add(nameText);
                if (isRunning && !string.IsNullOrEmpty(proc)) {
                    var procPill = new Border { Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
                    procPill.Child = new TextBlock { Text = proc, Foreground = new SolidColorBrush(Color.FromRgb(0xd1, 0xd5, 0xdb)), FontSize = 11 };
                    Grid.SetColumn(procPill, 2); row.Children.Add(procPill);
                }
                appsPanel.Children.Add(row);
            }
            AddAppUI("Telegram", a.TelegramRunning, a.TelegramProcName);
            AddAppUI("Discord", a.DiscordRunning, a.DiscordProcName);
            AddAppUI("Zapret", a.ZapretRunning, a.ZapretProcName);
            AddAppUI("tg-ws-proxy", a.TgWsProxyRunning, a.TgWsProxyProcName);
            AddRichCard(DiagResults, "Статус приложений", appsPanel, Color.FromRgb(0x8b, 0x5c, 0xf6));
        }

        // --- НОВЫЙ БЛОК ПРИМЕЧАНИЯ ВМЕСТО СТАРОЙ РЕКОМЕНДАЦИИ ---
        string noteText = "Примечание! Отправка медиафайлов (именно отправка) даже с включённым TgWsProxy может работать нестабильно, файлы могут загружаться очень долго. К сожалению, это не решить без использования VPN. Но просмотр и загрузка видео, стикеров и любого другого контента в Telegram должны работать идеально!";
        AddCard(DiagResults, "Важное примечание", noteText, Color.FromRgb(0x3b, 0x82, 0xf6));

        // Доступность серверов (без изменений)
        if (r.DcResults.Count > 0)
        {
            var serverContainer = new StackPanel();
            var srvPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var dc in r.DcResults)
            {
                var srvBlock = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)), CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 12, 14, 12), Width = 150 };
                var srvStack = new StackPanel();
                var headerGrid = new Grid();
                headerGrid.Children.Add(new TextBlock { Text = $"DC {dc.DcId}", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 14 });
                int? ping = dc.LatencyMs.HasValue ? (int)Math.Round(dc.LatencyMs.Value) : null;
                Color dotColor = (dc.Ok && ping <= 100) ? Color.FromRgb(0x22, 0xc5, 0x5e) : (dc.Ok && ping <= 200) ? Color.FromRgb(0xea, 0xb3, 0x08) : Color.FromRgb(0xef, 0x44, 0x44);
                headerGrid.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(dotColor), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                srvStack.Children.Add(headerGrid);
                srvStack.Children.Add(new TextBlock { Text = dc.Ip, Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
                srvStack.Children.Add(new TextBlock { Text = !dc.Ok ? "Недоступен" : $"{ping} мс", Foreground = new SolidColorBrush(dotColor), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
                srvBlock.Child = srvStack; srvPanel.Children.Add(srvBlock);
            }
            serverContainer.Children.Add(srvPanel);

            // Добавляем примечание, если включен TgWsProxy
            if (r.AppStatus != null && r.AppStatus.TgWsProxyRunning)
            {
                var serverNoteText = new TextBlock
                {
                    Text = "Примечание: У вас включен TgWsProxy. Даже если выше указано, что сервера недоступны, не переживайте, на вашем ПК Telegram будет работать нормально.\n\n" +
                           "Связь с TG идет через этот прокси, а диагностика проверяет сервера прямой отправкой пакетов, которые блокируются. Поэтому они и помечаются как «недоступные».\n\n" +
                           "Важно: Сервера будут помечены как стабильные и пинг будет нормальным только в том случае, если у вас включен VPN, а без него они всегда будут «недоступны» :). Так что всё ок!",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)), // Серый текст
                    FontSize = 14,
                    FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                serverContainer.Children.Add(serverNoteText);
            }

            AddRichCard(DiagResults, "Доступность серверов Telegram", serverContainer, Color.FromRgb(0x0e, 0xa5, 0xe9));
        }

        DiagProg.Value = 100;
        DiagProgLbl.Text = "Готово";
        DiagRunBtn.IsEnabled = true;
        DiagRunBtn.Content = CreateButtonContentWithIcon("RefreshIcon", "Проверить снова", Brushes.White);
        
        // Разблокируем авто-обновления и обновляем Discord статус
        _discord.IsScanning = false;
        _discord.SetAllGood(
            r.AppStatus?.ZapretRunning == true,
            r.AppStatus?.TgWsProxyRunning == true);
    }

    private Color ColorFromKey(string ck) => ck switch
    {
        "green"  => Color.FromRgb(0x22, 0xc5, 0x5e),
        "yellow" => Color.FromRgb(0xea, 0xb3, 0x08),
        "red"    => Color.FromRgb(0xef, 0x44, 0x44),
        _        => Color.FromRgb(0x6b, 0x72, 0x80),
    };

    private static void AddCard(Panel parent, string title, string body, Color accentColor)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            AddRichCard(parent, title, new Grid(), accentColor);
            return;
        }

        var tb = new TextBlock {
            Text = body,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd1, 0xd5, 0xdb)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        };
        AddRichCard(parent, title, tb, accentColor);
    }

    private static void AddRichCard(Panel parent, string title, UIElement content, Color accentColor)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 14),
            ClipToBounds = true,
        };

        var inner = new Grid();

        var bar = new Border
        {
            Background = new SolidColorBrush(accentColor),
            Width = 4,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        
        // Создаем заголовок с иконкой вместо эмодзи
        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        
        // Определяем какую иконку использовать
        string iconKey = null;
        string titleText = title;
        
        // Проверяем содержит ли заголовок ключевые слова для определения типа
        if (title.Contains("tg-ws-proxy") || title.Contains("Telegram"))
        {
            iconKey = "TelegramIcon";
            // Убираем эмодзи из текста
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }
        else if (title.Contains("Discord"))
        {
            iconKey = "DiscordIcon";
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }
        else
        {
            // Для остальных просто убираем эмодзи
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }
        
        // Добавляем иконку если нашли
        if (iconKey != null)
        {
            Geometry iconGeometry = null;
            
            // Пробуем найти в ресурсах
            iconGeometry = System.Windows.Application.Current.TryFindResource(iconKey) as PathGeometry;
            
            // Если не нашли, создаем напрямую
            if (iconGeometry == null)
            {
                if (iconKey == "TelegramIcon")
                {
                    // Official Telegram icon from simpleicons.org
                    iconGeometry = Geometry.Parse("M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z");
                }
                else if (iconKey == "DiscordIcon")
                {
                    // Official Discord icon from simpleicons.org
                    iconGeometry = Geometry.Parse("M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z");
                }
            }
            
            if (iconGeometry != null)
            {
                var iconPath = new System.Windows.Shapes.Path
                {
                    Data = iconGeometry,
                    Fill = new SolidColorBrush(accentColor),
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                
                titlePanel.Children.Add(iconPath);
            }
        }
        
        titlePanel.Children.Add(new TextBlock
        {
            Text = titleText,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        
        stack.Children.Add(titlePanel);
        
        stack.Children.Add(content);

        inner.Children.Add(stack);
        inner.Children.Add(bar);
        card.Child = inner;
        parent.Children.Add(card);
    }

    // ── Settings panel ───────────────────────────────────────────────────────
    private void SettingsBtn_Click(object s, RoutedEventArgs e)
    {
        if (_settingsOpen) CloseSettings();
        else OpenSettings();
    }

    private void SettingsBackdrop_Click(object s, MouseButtonEventArgs e) => CloseSettings();
    private void SettingsCloseBtn_Click(object s, RoutedEventArgs e) => CloseSettings();

    private void OpenSettings()
    {
        _settingsOpen = true;
        SettingsLayer.Visibility = Visibility.Visible;
        AnimateSettings(open: true);
    }

    private void CloseSettings()
    {
        _settingsOpen = false;
        AnimateSettings(open: false);
    }

    private void AnimateSettings(bool open)
    {
        double fromX = open ? 50 : 0;
        double toX   = open ? 0   : 50;

        var slideAnim = new DoubleAnimation(fromX, toX, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn }
        };

        var fadeAnim = new DoubleAnimation(open ? 0 : 0.5, open ? 0.5 : 0,
            TimeSpan.FromMilliseconds(200));
        
        var opacityAnim = new DoubleAnimation(open ? 0 : 1, open ? 1 : 0,
            TimeSpan.FromMilliseconds(220));

        if (!open)
        {
            slideAnim.Completed += (_, _) =>
            {
                if (!_settingsOpen)
                    SettingsLayer.Visibility = Visibility.Collapsed;
            };
        }

        SettingsTrans.BeginAnimation(TranslateTransform.XProperty, slideAnim);
        SettingsBackdrop.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        SettingsPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
    }

    // ── Settings load/save ───────────────────────────────────────────────────
    private void LoadSettingsToPanel()
    {
        ZapretBox.Text   = _settings.ZapretPath;
        TgWsBox.Text     = _settings.TgWsProxyPath;
        // AutoZapretCB убран - Zapret больше не в автозапуске
        AutoTgWsCB.IsChecked    = _settings.AutostartTgWsProxy;
        AutoAppCB.IsChecked     = _settings.AutostartApp;
        StartMinimizedCB.IsChecked = _settings.StartMinimizedToTray;
        _settings.TgWsProxyCheckUpdates = TgWsProxySettingsService.GetCheckUpdates();
        TgWsCheckUpdatesCB.IsChecked    = _settings.TgWsProxyCheckUpdates;
        DiscordRpcCB.IsChecked      = _settings.DiscordRpcEnabled;
        AutoUpdatesCB.IsChecked     = _settings.AutoUpdates;
        ShowGameOfferCB.IsChecked   = _settings.ShowGameOfferDialog;
        ShowServiceReminderCB.IsChecked = _settings.ShowLongCheckDialog;
        UpdateFixModeVisual(_settings.Mode);
        ComboEffectCB.IsChecked = _settings.DisableComboEffect;
        VolumeSlider.Value = _settings.GameVolume;
        // Применить логарифм сразу при загрузке
        double linear = Math.Pow(_settings.GameVolume, 3);
        _editorPlayer.Volume = linear;
        _previewPlayer.Volume = linear;
        if (VolumePercent != null)
            VolumePercent.Text = $"{(int)(_settings.GameVolume * 100)}%";
        LoadKeyLabels();
    }

    private void AutoSaveSettings()
    {
        _settings.ZapretPath       = ZapretBox.Text.Trim();
        _settings.TgWsProxyPath    = TgWsBox.Text.Trim();
        _settings.AutostartTgWsProxy = AutoTgWsCB.IsChecked == true;
        _settings.AutostartApp     = AutoAppCB.IsChecked == true;
        _settings.StartMinimizedToTray = StartMinimizedCB.IsChecked == true;
        _settings.AutoUpdates      = AutoUpdatesCB.IsChecked == true;
        _settings.ShowGameOfferDialog  = ShowGameOfferCB.IsChecked == true;
        _settings.ShowLongCheckDialog  = ShowServiceReminderCB.IsChecked == true;
        SettingsService.Save(_settings);
        SetAutostart(_settings.AutostartApp);
    }

    private void UpdateFixModeVisual(FixMode mode)
    {
        var fullBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        var fastBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        var fullBg = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));
        var fastBg = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));

        if (mode == FixMode.Full)
        {
            fullBrush = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1));
            fullBg = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e));
        }
        else
        {
            fastBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xc7, 0x59));
            fastBg = new SolidColorBrush(Color.FromRgb(0x1a, 0x2e, 0x1a));
        }

        FixModeFullCard.BorderBrush = fullBrush;
        FixModeFullCard.Background = fullBg;
        FixModeFastCard.BorderBrush = fastBrush;
        FixModeFastCard.Background = fastBg;
    }

    private void SelectFixMode(FixMode mode)
    {
        _settings.Mode = mode;
        SettingsService.Save(_settings);
        UpdateFixModeVisual(mode);
    }

    private void FixModeFullCard_Click(object s, MouseButtonEventArgs e) { SelectFixMode(FixMode.Full); }
    private void FixModeFastCard_Click(object s, MouseButtonEventArgs e)
    {
        var cache = ZapretConfigService.LoadCache();
        if (cache is null || !cache.HasAnyConfigs)
        {
            ShowScanRequiredForFastMode();
            return;
        }
        SelectFixMode(FixMode.Fast);
    }

    private void SettingCB_Checked(object sender, RoutedEventArgs e) { if (_settingsLoaded) AutoSaveSettings(); }
    private void SettingCB_Unchecked(object sender, RoutedEventArgs e) { if (_settingsLoaded) AutoSaveSettings(); }

    private void TgWsSetting_Checked(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        HandleTgWsToggle(sender, true);
    }

    private void TgWsSetting_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        HandleTgWsToggle(sender, false);
    }

    private void HandleTgWsToggle(object sender, bool newValue)
    {
        if (sender == TgWsCheckUpdatesCB)
        {
            TgWsProxySettingsService.SetCheckUpdates(newValue);
            bool actual = TgWsProxySettingsService.GetCheckUpdates();
            _settings.TgWsProxyCheckUpdates = actual;
            TgWsCheckUpdatesCB.IsChecked = actual;
        }
        SettingsService.Save(_settings);
    }

    private void ZapretBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _settings.ZapretPath = ZapretBox.Text.Trim();
        SettingsService.Save(_settings);
    }

    private void TgWsBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _settings.TgWsProxyPath = TgWsBox.Text.Trim();
        SettingsService.Save(_settings);
    }

    private void DiscordRpcCB_Checked(object sender, RoutedEventArgs e)
    {
        _settings.DiscordRpcEnabled = true;
        _discord.Enable();
        SettingsService.Save(_settings);
    }

    private void DiscordRpcCB_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded)
            return;

        _settings.DiscordRpcEnabled = false;
        _discord.Disable();
        SettingsService.Save(_settings);
    }

    private void ComboEffectCB_Checked(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return; // не показывать диалог при загрузке настроек
        ShowConfirmDialog(
            "Отключить комбо-эффект?",
            "Этот эффект создаёт яркую вспышку при одновременном нажатии трёх любых дорожек, " +
            "он усиливает эмоциональную отдачу от прохождения и делает игру более зрелищной.\n\n" +
            "Однако на очень сложных уровнях с высокой плотностью нот визуальный шум может " +
            "сбивать концентрацию. В таком случае отключение оправдано.",
            ok =>
            {
                if (ok)
                {
                    _settings.DisableComboEffect = true;
                    SettingsService.Save(_settings);
                }
                else
                {
                    ComboEffectCB.IsChecked = false;
                }
            },
            confirmText: "Отключить",
            confirmIsDestructive: true);
    }

    private void ComboEffectCB_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _settings.DisableComboEffect = false;
        SettingsService.Save(_settings);
    }

    private void VolumeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsLoaded) return;

        // Логарифмическая кривая: слух воспринимает громкость нелинейно
        double linear = Math.Pow(e.NewValue, 3);
        _editorPlayer.Volume = linear;
        _previewPlayer.Volume = linear;
        _settings.GameVolume = e.NewValue;
        if (VolumePercent != null)
            VolumePercent.Text = $"{(int)(e.NewValue * 100)}%";
        SettingsService.Save(_settings);
    }



    // ── Browse buttons ───────────────────────────────────────────────────────
    private string? BrowseExe(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title  = title,
            Filter = "Исполняемый файл (*.exe;*.bat)|*.exe;*.bat|Все файлы (*.*)|*.*"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void BrowseZapret_Click(object s, RoutedEventArgs e)
    {
        var p = BrowseExe("Выберите Zapret");
        if (p != null) ZapretBox.Text = p;
    }

    private void BrowseTgWs_Click(object s, RoutedEventArgs e)
    {
        var p = BrowseExe("Выберите tg-ws-proxy");
        if (p != null) TgWsBox.Text = p;
    }

    // ── Settings actions ─────────────────────────────────────────────────────
    private void ReOnboard_Click(object s, RoutedEventArgs e)
    {
        CloseSettings();
        SettingsService.ResetOnboarding();
        Dispatcher.InvokeAsync(() => ShowOnboarding(), DispatcherPriority.Background);
    }

    private void ResetSettings_Click(object s, RoutedEventArgs e)
    {
        _settings = new AppSettings();
        SettingsService.Save(_settings);
        SettingsService.ResetOnboarding();
        LoadSettingsToPanel();
        CloseSettings();
    }
    
    // ── Export/Import Settings ───────────────────────────────────────────────
    private void ExportSettings_Click(object s, RoutedEventArgs e)
    {
        // Проверяем наличие результатов тестирования
        var cache = ZapretConfigService.LoadCache();
        if (cache == null || !cache.HasAnyConfigs)
        {
            ShowNotification("❌ Нет данных для экспорта", 
                "Сначала выполните тестирование конфигов Zapret.\nПерейдите на вкладку 'Серверы' и нажмите 'Тест конфигов'.", 
                "#ef4444");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспортировать результаты тестирования",
            Filter = "JSON файл (*.json)|*.json",
            FileName = $"Zapret_TestResults_{DateTime.Now:yyyy-MM-dd_HH-mm}.json"
        };
        
        if (dlg.ShowDialog() == true)
        {
            try
            {
                // Получаем путь к файлу кэша
                var cacheFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NetFix", "zapret_configs.json");

                if (!File.Exists(cacheFile))
                {
                    ShowNotification("❌ Ошибка", 
                        "Файл с результатами тестирования не найден", 
                        "#ef4444");
                    return;
                }

                // Копируем файл
                File.Copy(cacheFile, dlg.FileName, true);

                var validCount = cache.ValidConfigs.Count;
                var partialCount = cache.PartialConfigs.Count;
                var currentConfig = string.IsNullOrEmpty(cache.CurrentConfig) ? "не выбран" : cache.CurrentConfig;

                ShowNotification("✅ Экспорт успешен", 
                    $"Результаты тестирования сохранены в:\n{dlg.FileName}\n\n" +
                    $"📊 Экспортировано:\n" +
                    $"• Идеальных конфигов: {validCount}\n" +
                    $"• Частично рабочих: {partialCount}\n" +
                    $"• Активный конфиг: {currentConfig}\n" +
                    $"• Дата тестирования: {cache.LastTested}", 
                    "#22c55e");
            }
            catch (Exception ex)
            {
                ShowNotification("❌ Ошибка экспорта", 
                    $"Не удалось сохранить результаты:\n{ex.Message}", 
                    "#ef4444");
            }
        }
    }
    
    private void ImportSettings_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импортировать результаты тестирования",
            Filter = "JSON файл (*.json)|*.json"
        };
        
        if (dlg.ShowDialog() == true)
        {
            try
            {
                // Проверяем валидность файла
                var json = File.ReadAllText(dlg.FileName);
                var importedCache = System.Text.Json.JsonSerializer.Deserialize<ZapretConfigCache>(json);
                
                if (importedCache == null || !importedCache.HasAnyConfigs)
                {
                    ShowNotification("❌ Ошибка импорта", 
                        "Файл не содержит валидных результатов тестирования", 
                        "#ef4444");
                    return;
                }

                // Получаем путь к файлу кэша
                var cacheFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NetFix", "zapret_configs.json");

                var cacheDir = Path.GetDirectoryName(cacheFile);
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }

                // Создаём бэкап текущего кэша если он есть
                if (File.Exists(cacheFile))
                {
                    var backupFile = cacheFile + $".backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
                    File.Copy(cacheFile, backupFile, true);
                }

                // Копируем импортированный файл
                File.Copy(dlg.FileName, cacheFile, true);

                // Обновляем отображение
                UpdateSelectedConfigDisplay();

                var validCount = importedCache.ValidConfigs.Count;
                var partialCount = importedCache.PartialConfigs.Count;
                var currentConfig = string.IsNullOrEmpty(importedCache.CurrentConfig) ? "не выбран" : importedCache.CurrentConfig;

                ShowNotification("✅ Импорт успешен", 
                    $"Результаты тестирования успешно загружены!\n\n" +
                    $"📊 Импортировано:\n" +
                    $"• Идеальных конфигов: {validCount}\n" +
                    $"• Частично рабочих: {partialCount}\n" +
                    $"• Активный конфиг: {currentConfig}\n" +
                    $"• Дата тестирования: {importedCache.LastTested}\n\n" +
                    $"Теперь можете выбрать конфиг на вкладке 'Серверы'", 
                    "#22c55e");
            }
            catch (Exception ex)
            {
                ShowNotification("❌ Ошибка импорта", 
                    $"Не удалось загрузить результаты:\n{ex.Message}", 
                    "#ef4444");
            }
        }
    }
    
    private void ShowNotification(string title, string message, string color)
    {
        // Создаём overlay
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        // Создаём карточку уведомления
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 420,
            Margin = new Thickness(40),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.5
            }
        };
        Grid.SetRowSpan(card, 3);

        var content = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        // Заголовок
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        content.Children.Add(titleText);

        // Сообщение
        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 20)
        };
        content.Children.Add(messageText);

        // Кнопка OK
        var baseColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!;
        var okBorder = new Border
        {
            Width = 120,
            Height = 36,
            Background = new SolidColorBrush(baseColor),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = "OK",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            }
        };
        
        // Hover эффекты
        okBorder.MouseEnter += (_, _) =>
        {
            okBorder.Background = new SolidColorBrush(Color.FromArgb(
                baseColor.A,
                (byte)(baseColor.R * 0.8),
                (byte)(baseColor.G * 0.8),
                (byte)(baseColor.B * 0.8)
            ));
        };
        
        okBorder.MouseLeave += (_, _) =>
        {
            okBorder.Background = new SolidColorBrush(baseColor);
        };
        
        okBorder.MouseLeftButtonUp += (_, _) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(card);
        };
        
        content.Children.Add(okBorder);
        card.Child = content;

        MainGrid.Children.Add(card);
    }

    // ── Links ────────────────────────────────────────────────────────────────
    private void SupportBtn_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://t.me/sofirka_hanabi");
    private void DonateBtn_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://www.tinkoff.ru/rm/r_eELpDmupvc.SCiWRkVJON/bgKkD30493");
    private void LinkZapret_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");
    private void LinkTgWs_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/tg-ws-proxy");
    private void LinkNetFix_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/rupleide/NetFix");

    private void OpenTelegramChannel_Click(object s, RoutedEventArgs e)
    {
        try {
            // Пробуем открыть напрямую в Telegram
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "tg://resolve?domain=NetFixRuBi",
                UseShellExecute = true
            });
        } catch {
            // Если не получилось, открываем через браузер
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://t.me/NetFixRuBi",
                    UseShellExecute = true
                });
            } catch { }
        }
    }

    private void CheckUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        var window = new NetFix.Views.UpdateWindow();
        window.Owner = this;
        window.ShowDialog();
    }

    // ── Игра: меню и навигация ───────────────────────────────────────────────
    private void PlayMenuBtn_Click(object s, MouseButtonEventArgs e)
    {
        // Блокируем навигационные кнопки во время загрузки треков (как во время игры)
        ServicesBtn.IsEnabled = false;
        GameNavBtn.IsEnabled = false;
        FaqNavBtn.IsEnabled = false;
        DiagNavBtn.IsEnabled = false;
        SettingsBtn.IsEnabled = false;
        
        LoadUserLevels();
        ShowGameView(GameTrackSelectView);
        
        // Разблокируем навигационные кнопки после загрузки
        ServicesBtn.IsEnabled = true;
        GameNavBtn.IsEnabled = true;
        FaqNavBtn.IsEnabled = true;
        DiagNavBtn.IsEnabled = true;
        SettingsBtn.IsEnabled = true;
    }

    private void PlayMenuBtn_MouseEnter(object s, System.Windows.Input.MouseEventArgs e)
    {
        PlayMenuBorder.Background = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0x1e, 0x24, 0x42), 0),
                new GradientStop(Color.FromRgb(0x25, 0x2a, 0x4a), 1)
            }
        };
    }

    private void PlayMenuBtn_MouseLeave(object s, System.Windows.Input.MouseEventArgs e)
    {
        PlayMenuBorder.Background = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0x17, 0x1b, 0x32), 0),
                new GradientStop(Color.FromRgb(0x20, 0x21, 0x3a), 1)
            }
        };
    }

    private void EditorMenuBtn_Click(object s, MouseButtonEventArgs e)
    {
        ShowGameView(GameEditorView);
    }

    private void EditorMenuBtn_MouseEnter(object s, System.Windows.Input.MouseEventArgs e)
    {
        EditorMenuBtn.Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
    }

    private void EditorMenuBtn_MouseLeave(object s, System.Windows.Input.MouseEventArgs e)
    {
        EditorMenuBtn.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
    }

    private void UserLevelCard_MouseEnter(object s, System.Windows.Input.MouseEventArgs e)
    {
        if (s is Border border)
            border.Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
    }

    private void UserLevelCard_MouseLeave(object s, System.Windows.Input.MouseEventArgs e)
    {
        if (s is Border border)
            border.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
    }

    private void ShowGameView(UIElement view)
    {
        GameMenuView.Visibility = Visibility.Collapsed;
        GameTrackSelectView.Visibility = Visibility.Collapsed;
        GamePlayView.Visibility = Visibility.Collapsed;
        GameEditorView.Visibility = Visibility.Collapsed;
        GameStatsView.Visibility = Visibility.Collapsed;
        GameStatsDetailView.Visibility = Visibility.Collapsed;
        OsuModeView.Visibility = Visibility.Collapsed;
        OszDifficultyView.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
    }

    private void DefaultLevelBtn_Click(object s, MouseButtonEventArgs e)
    {
        ShowGameView(GamePlayView);
        StartDefaultTrack();
    }

    private void LoadUserLevels()
    {
        Directory.CreateDirectory(LevelsDir);
        var levels = Directory.GetDirectories(LevelsDir)
            .Select(d =>
            {
                var notesFile = System.IO.Path.Combine(d, "notes.json");
                if (!File.Exists(notesFile)) return null;
                try
                {
                    var map = JsonSerializer.Deserialize<NoteMap>(File.ReadAllText(notesFile));
                    if (map != null)
                    {
                        map.LevelDir = d;
                        if (map.DateAdded == default)
                            map.DateAdded = Directory.GetCreationTime(d);
                    }
                    return map;
                }
                catch
                {
                    return null;
                }
            })
            .OfType<NoteMap>()
            .ToList();

        if (levels.Count == 0)
        {
            UserLevelsEmpty.Visibility = Visibility.Visible;
            UserLevelsList.Visibility = Visibility.Collapsed;
            UserLevelsList.ItemsSource = null;
            _userTracksView = null;
        }
        else
        {
            UserLevelsEmpty.Visibility = Visibility.Collapsed;
            UserLevelsList.Visibility = Visibility.Visible;
            UserLevelsList.ItemsSource = levels;
            _userTracksView = CollectionViewSource.GetDefaultView(UserLevelsList.ItemsSource);
            _userTracksView.Filter = UserTrackFilterPredicate;
            ApplyUserSorting();
        }
        
        // Загружаем встроенные треки NetFix
        LoadBuiltInTracks();
    }

    private void LoadBuiltInTracks()
    {
        var builtInTracks = GetBuiltInTracks();
        
        // Очищаем список
        BuiltInTracksList.Children.Clear();
        
        // Добавляем встроенные треки
        foreach (var map in builtInTracks)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1b, 0x32)),
                CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x34, 0x3a, 0x78)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                Tag = map
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBorder = new Border
            {
                Width = 32, Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.FromRgb(0x20, 0x27, 0x5a)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            iconBorder.Child = new TextBlock
            {
                Text = "♪",
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0x8c, 0xf8)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            var info = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = map.Title ?? "Без названия",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{map.Notes?.Count ?? 0} нот · {map.Bpm:0} BPM",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0x8c, 0xf8))
            });

            Grid.SetColumn(iconBorder, 0);
            Grid.SetColumn(info, 1);
            grid.Children.Add(iconBorder);
            grid.Children.Add(info);
            card.Child = grid;

            card.MouseLeftButtonUp += BuiltInTrackCard_Click;
            card.MouseEnter += (_, _) => card.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x24, 0x42));
            card.MouseLeave += (_, _) => card.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1b, 0x32));

            BuiltInTracksList.Children.Add(card);
        }
    }

    private void BuiltInTrackCard_Click(object s, MouseButtonEventArgs e)
    {
        if ((s as Border)?.Tag is not NoteMap map) return;
        
        // Извлекаем трек из zip во временную папку
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetFix_Tracks", map.Title ?? "track");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            using var archive = ZipFile.OpenRead(map.LevelDir!);
            
            // Извлекаем mp3 (ищем без учёта вложенности)
            var mp3Entry = archive.Entries.FirstOrDefault(e => 
                e.Name.Equals(map.TrackFile ?? "track.mp3", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(map.TrackFile ?? "track.mp3", StringComparison.OrdinalIgnoreCase));
            
            string? mp3Path = null;
            if (mp3Entry != null)
            {
                mp3Path = System.IO.Path.Combine(tempDir, mp3Entry.Name);
                mp3Entry.ExtractToFile(mp3Path, overwrite: true);
            }
            
            var bpm = map.Bpm > 0 ? map.Bpm : REFERENCE_BPM;
            
            // Переключаемся на игровой вид
            GamePage.Visibility = Visibility.Visible;
            ShowGameView(GamePlayView);
            StartGame(map.Notes, mp3Path, map.Title ?? "NetFix Track", bpm);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка запуска трека: {ex.Message}");
            // Если не удалось извлечь, запускаем без музыки
            var bpm = map.Bpm > 0 ? map.Bpm : REFERENCE_BPM;
            GamePage.Visibility = Visibility.Visible;
            ShowGameView(GamePlayView);
            StartGame(map.Notes, null, map.Title ?? "NetFix Track", bpm);
        }
    }

    private List<NoteMap> GetUserLevelMaps()
    {
        var result = new List<NoteMap>();
        if (!Directory.Exists(LevelsDir)) return result;
        foreach (var dir in Directory.GetDirectories(LevelsDir))
        {
            var f = System.IO.Path.Combine(dir, "notes.json");
            if (!File.Exists(f)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<NoteMap>(File.ReadAllText(f));
                if (m != null) result.Add(m);
            }
            catch { }
        }
        return result;
    }

    private List<NoteMap> GetBuiltInTracks()
    {
        var result = new List<NoteMap>();
        if (!Directory.Exists(BuiltInTracksDir)) return result;

        // Ищем все .zip файлы в папке Tracks
        var zipFiles = Directory.GetFiles(BuiltInTracksDir, "*.zip");
        
        foreach (var zipPath in zipFiles)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                
                // Ищем notes.json в архиве (без учёта регистра и вложенности)
                var notesEntry = archive.Entries.FirstOrDefault(e => 
                    e.Name.Equals("notes.json", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith("notes.json", StringComparison.OrdinalIgnoreCase));
                
                if (notesEntry == null) continue;

                // Читаем notes.json
                using var stream = notesEntry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                
                var map = JsonSerializer.Deserialize<NoteMap>(json);
                if (map != null)
                {
                    // Сохраняем путь к zip-архиву для последующего извлечения
                    map.LevelDir = zipPath;
                    result.Add(map);
                }
            }
            catch (Exception ex)
            {
                // Логируем ошибку для отладки
                Debug.WriteLine($"Ошибка загрузки трека {System.IO.Path.GetFileName(zipPath)}: {ex.Message}");
            }
        }
        
        return result;
    }

    // ── Поиск и сортировка треков ────────────────────────────────────────────
    private bool UserTrackFilterPredicate(object item)
    {
        if (string.IsNullOrWhiteSpace(_userSearchText)) return true;
        if (item is not NoteMap track) return false;
        return track.Title?.Contains(_userSearchText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool OsuTrackFilterPredicate(object item)
    {
        if (string.IsNullOrWhiteSpace(_osuSearchText)) return true;
        if (item is not NoteMap track) return false;
        return track.Title?.Contains(_osuSearchText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void TrackSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        var text = tb.Text;

        if (tb.Name == "OsuTrackSearchBox")
        {
            _osuSearchText = text;
            if (FindName("OsuSearchPlaceholder") is TextBlock ph)
                ph.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
            _osuTracksView?.Refresh();
        }
        else
        {
            _userSearchText = text;
            if (FindName("SearchPlaceholder") is TextBlock ph)
                ph.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
            _userTracksView?.Refresh();
        }
    }

    private void StatsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        _statsSearchText = tb.Text;
        if (FindName("StatsSearchPlaceholder") is TextBlock ph)
            ph.Visibility = string.IsNullOrEmpty(_statsSearchText) ? Visibility.Visible : Visibility.Collapsed;
        PopulateStatsList();
    }

    private void TrackSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        // TextBox → inner Grid → outer Grid → Border
        var border = (tb.Parent as FrameworkElement)?.Parent switch
        {
            Grid g => g.Parent as Border,
            Border b => b,
            _ => null
        };
        if (border != null)
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
    }

    private void TrackSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var border = (tb.Parent as FrameworkElement)?.Parent switch
        {
            Grid g => g.Parent as Border,
            Border b => b,
            _ => null
        };
        if (border != null)
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
    }

    private void SortMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var menu = btn.Tag as ContextMenu;
        if (menu != null)
        {
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        }
    }

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var tag = item.Tag?.ToString() ?? "DateAddedDesc";

        // Определяем какой вью и кнопка, по родительской цепочке ContextMenu
        var ctx = item.Parent as ContextMenu;
        var target = ctx?.PlacementTarget;
        if (target is Button btn && btn.Name == "OsuSortMenuBtn")
        {
            _settings.OsuSortMode = tag;
            SettingsService.Save(_settings);
            ApplyOsuSorting();
            _osuTracksView?.Refresh();
        }
        else if (target is Button btn2 && btn2.Name == "StatsSortMenuBtn")
        {
            _settings.StatsSortMode = tag;
            SettingsService.Save(_settings);
            PopulateStatsList();
        }
        else
        {
            _settings.UserSortMode = tag;
            SettingsService.Save(_settings);
            ApplyUserSorting();
            _userTracksView?.Refresh();
        }
    }

    private void ApplyUserSorting()
    {
        if (_userTracksView == null) return;
        ApplySortingToView(_userTracksView, _settings.UserSortMode);
    }

    private void ApplyOsuSorting()
    {
        if (_osuTracksView == null) return;
        ApplySortingToView(_osuTracksView, _settings.OsuSortMode);
    }

    private void ApplySortingToView(ICollectionView view, string sortMode)
    {
        view.SortDescriptions.Clear();
        switch (sortMode)
        {
            case "TitleAsc":
                view.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case "DateAddedDesc":
                view.SortDescriptions.Add(new SortDescription("DateAdded", ListSortDirection.Descending));
                break;
            case "DateAddedAsc":
                view.SortDescriptions.Add(new SortDescription("DateAdded", ListSortDirection.Ascending));
                break;
            case "LastPlayedDesc":
                view.SortDescriptions.Add(new SortDescription("LastPlayed", ListSortDirection.Descending));
                break;
            case "NotesAsc":
                view.SortDescriptions.Add(new SortDescription("NoteCount", ListSortDirection.Ascending));
                break;
            case "NotesDesc":
                view.SortDescriptions.Add(new SortDescription("NoteCount", ListSortDirection.Descending));
                break;
        }
    }

    // ── Игра: движок ─────────────────────────────────────────────────────────
    private const double FALL_SEC = 1.6;
    private const double REFERENCE_BPM = 140.0;
    private const double HIT_PERFECT = 0.06;
    private const double HIT_GOOD = 0.15;

    // Константы размеров игрового поля
    private const double LANE_WIDTH = 50;
    private const double LANE_SPACING = 60;
    private const double LANE_OFFSET = 85;  // (400 - 240) / 2 + 10 = 85 для центрирования дорожек
    private const double CANVAS_WIDTH = 400;
    private const double NOTE_SIZE = 50;
    private const double ARROW_FONT_SIZE = 20;

    // Вспомогательная функция для получения X координаты дорожки
    private static double GetLaneLeft(int lane) => LANE_OFFSET + lane * LANE_SPACING;
    private static double GetLaneCenterX(int lane) => GetLaneLeft(lane) + (LANE_WIDTH / 2);

    private static readonly string[] ArrowChars = { "◀", "▼", "▲", "▶" };
    private static readonly Color[] LaneColors =
    {
        Color.FromRgb(0xf4, 0x3f, 0x5e),
        Color.FromRgb(0xf5, 0x9e, 0x0b),
        Color.FromRgb(0x22, 0xc5, 0x5e),
        Color.FromRgb(0x63, 0x66, 0xf1),
    };

    private void StartDefaultTrack()
    {
        var notes = new List<NoteEntry>();
        int[] pattern = { 0, 2, 1, 3, 0, 1, 2, 3, 1, 0, 3, 2 };
        double beatSec = 60.0 / REFERENCE_BPM;

        for (int i = 0; i < 48; i++)
            notes.Add(new NoteEntry { Time = beatSec * (i + 2), Lane = pattern[i % pattern.Length] });

        StartGame(notes, null, "NetFix, Default Beat", REFERENCE_BPM);
    }

    private void ShowGameOverlay()
    {
        if (_gameOverlayPanel != null) return;

        // Блокируем навигационные кнопки во время оверлея
        ServicesBtn.IsEnabled = false;
        GameNavBtn.IsEnabled = false;
        FaqNavBtn.IsEnabled = false;
        DiagNavBtn.IsEnabled = false;
        SettingsBtn.IsEnabled = false;

        // Блюр + затемнение главного контента
        var blurEffect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 };
        MainPage.Effect = blurEffect;
        MainPage.Opacity = 0.45;

        // Полупрозрачный оверлей с игрой
        _gameOverlayPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 0x0d, 0x0d, 0x18)),
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Opacity = 0
        };
        Panel.SetZIndex(_gameOverlayPanel, 8);

        // Внутренний контейнер с закруглёнными краями
        var innerBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Margin = new Thickness(0)
        };

        // Создаём мини-версию выбора трека
        var trackSelectGrid = BuildInlineTrackSelect();
        innerBorder.Child = trackSelectGrid;
        _gameOverlayPanel.Child = innerBorder;

        Grid.SetRow(_gameOverlayPanel, 1);
        ContentGrid.Children.Add(_gameOverlayPanel);

        // Fade in
        _gameOverlayPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void HideGameOverlay()
    {
        if (_gameOverlayPanel == null) return;

        // Разблокируем навигационные кнопки
        ServicesBtn.IsEnabled = true;
        GameNavBtn.IsEnabled = true;
        FaqNavBtn.IsEnabled = true;
        DiagNavBtn.IsEnabled = true;
        SettingsBtn.IsEnabled = true;

        // Убираем блюр
        MainPage.Effect = null;
        MainPage.Opacity = 1.0;

        var panel = _gameOverlayPanel;
        _gameOverlayPanel = null;

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fadeOut.Completed += (_, _) => ContentGrid.Children.Remove(panel);
        panel.BeginAnimation(OpacityProperty, fadeOut);
    }

    private Grid BuildInlineTrackSelect()
    {
        var root = new Grid { Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Шапка с кнопкой закрытия
        var header = new Grid { Margin = new Thickness(24, 18, 24, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBlock = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        titleBlock.Children.Add(new TextBlock
        {
            Text = "🎮  Выбор трека",
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        titleBlock.Children.Add(new TextBlock
        {
            Text = "  · сканирование идёт в фоне",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77)),
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        });
        Grid.SetColumn(titleBlock, 0);
        header.Children.Add(titleBlock);

        var closeBtn = new Button
        {
            Style = (Style)FindResource("FlatBtnCentered"),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0)
        };
        closeBtn.Content = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("CloseIcon"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            StrokeThickness = 2,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform
        };
        closeBtn.Click += (_, _) =>
        {
            StopGame();
            HideGameOverlay();
        };
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);

        // Горизонтальный разделитель
        var sep = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x33)),
            Margin = new Thickness(0, 12, 0, 0)
        };

        // Объединяем header и separator в один StackPanel
        var headerStack = new StackPanel();
        headerStack.Children.Add(header);
        headerStack.Children.Add(sep);
        Grid.SetRow(headerStack, 0);
        root.Children.Add(headerStack);

        var bodyScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(24, 16, 24, 24)
        };

        var body = new StackPanel();

        // ── Предупреждение об эпилепсии ──
        var warnBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x17, 0x10)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 18),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0xf5, 0x9e, 0x0b)),
            BorderThickness = new Thickness(1)
        };

        var warnGrid = new Grid();
        warnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        warnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Иконка предупреждения (треугольник с восклицательным знаком)
        var warningIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12,2 L22,20 L2,20 Z M12,9 L12,14 M12,16 L12,18"),
            Stroke = new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)),
            StrokeThickness = 2,
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 4, 10, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        Grid.SetColumn(warningIcon, 0);
        warnGrid.Children.Add(warningIcon);

        // Текст предупреждения
        var warnText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x77, 0x55)),
            TextWrapping = TextWrapping.Wrap
        };
        warnText.Inlines.Add(new Run { Text = "ВНИМАНИЕ", Foreground = new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)), FontWeight = FontWeights.Bold });
        warnText.Inlines.Add(new LineBreak());
        warnText.Inlines.Add(new Run { Text = "Игра содержит мигающие эффекты и не рекомендуется лицам, подверженным эпилептическим припадкам." });
        warnText.Inlines.Add(new LineBreak());
        warnText.Inlines.Add(new Run { Text = "Продолжительное использование может привести к головокружению и возникновению зрительных иллюзий. Пожалуйста, соблюдайте режим отдыха и ограничьте время непрерывной игры." });
        Grid.SetColumn(warnText, 1);
        warnGrid.Children.Add(warnText);
        
        warnBorder.Child = warnGrid;
        body.Children.Add(warnBorder);

        // ── Встроенный трек ──
        var section1 = new TextBlock
        {
            Text = "ВСТРОЕННЫЕ ТРЕКИ",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x55)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        body.Children.Add(section1);

        // ── Встроенные треки NetFix ──
        var builtInTracks = GetBuiltInTracks();
        if (builtInTracks.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "ТРЕКИ NETFIX",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            foreach (var map in builtInTracks)
            {
                var bCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1b, 0x32)),
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand
                };
                var bGrid = new Grid();
                bGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                bGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var bIconBorder = new Border
                {
                    Width = 32, Height = 32,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromRgb(0x20, 0x27, 0x5a)),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                bIconBorder.Child = new TextBlock
                {
                    Text = "♪",
                    FontSize = 18,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0x8c, 0xf8)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };

                var bInfo = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
                bInfo.Children.Add(new TextBlock
                {
                    Text = map.Title ?? "Без названия",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                });
                bInfo.Children.Add(new TextBlock
                {
                    Text = $"{map.Notes?.Count ?? 0} нот · {map.Bpm:0} BPM · {map.Author ?? "NetFix"}",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0x8c, 0xf8))
                });

                var bPlayBtn = new Button
                {
                    Style = (Style)FindResource("AccentBtn"),
                    Padding = new Thickness(12, 6, 12, 6),
                    FontSize = 12,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                bPlayBtn.Content = "▶";

                Grid.SetColumn(bIconBorder, 0);
                Grid.SetColumn(bInfo, 1);
                Grid.SetColumn(bPlayBtn, 2);
                bGrid.Children.Add(bIconBorder);
                bGrid.Children.Add(bInfo);
                bGrid.Children.Add(bPlayBtn);
                bCard.Child = bGrid;

                var capturedMap = map;
                Action startBuiltIn = () =>
                {
                    HideGameOverlay();
                    ShowGameInOverlay(() =>
                    {
                        // Извлекаем трек из zip во временную папку
                        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetFix_Tracks", capturedMap.Title ?? "track");
                        Directory.CreateDirectory(tempDir);
                        
                        try
                        {
                            using var archive = ZipFile.OpenRead(capturedMap.LevelDir!);
                            
                            // Извлекаем mp3 (ищем без учёта вложенности)
                            var mp3Entry = archive.Entries.FirstOrDefault(e => 
                                e.Name.Equals(capturedMap.TrackFile ?? "track.mp3", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.EndsWith(capturedMap.TrackFile ?? "track.mp3", StringComparison.OrdinalIgnoreCase));
                            
                            string? mp3Path = null;
                            if (mp3Entry != null)
                            {
                                mp3Path = System.IO.Path.Combine(tempDir, mp3Entry.Name);
                                mp3Entry.ExtractToFile(mp3Path, overwrite: true);
                            }
                            
                            var bpm = capturedMap.Bpm > 0 ? capturedMap.Bpm : REFERENCE_BPM;
                            StartGame(capturedMap.Notes, mp3Path, capturedMap.Title ?? "NetFix Track", bpm);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Ошибка запуска трека: {ex.Message}");
                            // Если не удалось извлечь, запускаем без музыки
                            var bpm = capturedMap.Bpm > 0 ? capturedMap.Bpm : REFERENCE_BPM;
                            StartGame(capturedMap.Notes, null, capturedMap.Title ?? "NetFix Track", bpm);
                        }
                    });
                };
                bCard.MouseLeftButtonUp += (_, _) => startBuiltIn();
                bPlayBtn.Click += (_, _) => startBuiltIn();

                bCard.MouseEnter += (_, _) => bCard.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x24, 0x42));
                bCard.MouseLeave += (_, _) => bCard.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1b, 0x32));

                body.Children.Add(bCard);
            }
        }

        // ── Пользовательские треки ──
        var userLevels = GetUserLevelMaps();
        if (userLevels.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "ПОЛЬЗОВАТЕЛЬСКИЕ ТРЕКИ",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x55)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            foreach (var map in userLevels)
            {
                var uCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand
                };
                var uGrid = new Grid();
                uGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                uGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var uInfo = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
                uInfo.Children.Add(new TextBlock
                {
                    Text = map.Title ?? "Без названия",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                });
                uInfo.Children.Add(new TextBlock
                {
                    Text = $"{map.Notes?.Count ?? 0} нот · {map.Bpm:0} BPM",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))
                });

                var uPlayBtn = new Button
                {
                    Style = (Style)FindResource("OutlineBtn"),
                    Padding = new Thickness(12, 6, 12, 6),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                uPlayBtn.Content = "▶";

                Grid.SetColumn(uInfo, 0);
                Grid.SetColumn(uPlayBtn, 1);
                uGrid.Children.Add(uInfo);
                uGrid.Children.Add(uPlayBtn);
                uCard.Child = uGrid;

                var capturedMap = map;
                Action startUser = () =>
                {
                    HideGameOverlay();
                    ShowGameInOverlay(() =>
                    {
                        var dir = System.IO.Path.Combine(LevelsDir, capturedMap.Title ?? "level");
                        var mp3 = System.IO.Path.Combine(dir, capturedMap.TrackFile ?? "track.mp3");
                        var bpm = capturedMap.Bpm > 0 ? capturedMap.Bpm : REFERENCE_BPM;
                        StartGame(capturedMap.Notes, File.Exists(mp3) ? mp3 : null, capturedMap.Title ?? "Custom", bpm);
                    });
                };
                uCard.MouseLeftButtonUp += (_, _) => startUser();
                uPlayBtn.Click += (_, _) => startUser();

                uCard.MouseEnter += (_, _) => uCard.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
                uCard.MouseLeave += (_, _) => uCard.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));

                body.Children.Add(uCard);
            }
        }

        bodyScroll.Content = body;
        Grid.SetRow(bodyScroll, 1);
        root.Children.Add(bodyScroll);

        return root;
    }

    private void ShowGameInOverlay(Action startGameAction)
    {
        // Блюрим главную страницу
        MainPage.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 5 };
        MainPage.Opacity = 0.35;

        // Делаем навигационные кнопки неактивными (видимыми, но не кликабельными)
        ServicesBtn.IsEnabled = false;
        GameNavBtn.IsEnabled = false;
        FaqNavBtn.IsEnabled = false;
        DiagNavBtn.IsEnabled = false;
        SettingsBtn.IsEnabled = false;

        // Скрываем кнопку "Создать уровень" в оверлей режиме
        EditorMenuBtn.Visibility = Visibility.Collapsed;

        // Показываем GamePage как оверлей
        GamePage.Background = new SolidColorBrush(Colors.Transparent);
        GamePage.Visibility = Visibility.Visible;
        Panel.SetZIndex(GamePage, 9);

        // Показываем GamePlayView
        ShowGameView(GamePlayView);

        // Обновляем флаг оверлея
        _gameOverlayActive = true;

        // Fade-in
        GamePage.Opacity = 0;
        GamePage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        startGameAction();
    }

    private void StartGame(List<NoteEntry> notes, string? mp3Path, string title, double bpm)
    {
        StopGame();

        // Сохраняем параметры для перезапуска
        _lastGameNotes = notes.Select(n => new NoteEntry { Time = n.Time, Lane = n.Lane }).ToList();
        _lastGameMp3Path = mp3Path;
        _lastGameTitle = title;
        _lastGameBpm = bpm;

        // Отключаем навигационные кнопки во время игры (если не в оверлей режиме)
        if (!_gameOverlayActive)
        {
            ServicesBtn.IsEnabled = false;
            FaqNavBtn.IsEnabled = false;
            DiagNavBtn.IsEnabled = false;
            SettingsBtn.IsEnabled = false;
        }

        _currentFallSec = GetFallSecondsForBpm(bpm);
        _pendingNotes = notes
            .Select(n => new NoteEntry { Time = n.Time, Lane = n.Lane })
            .OrderBy(n => n.Time)
            .ToList();
        _activeNotes = new();
        _gameScore = 0;
        _gameCombo = 0;
        _totalNotes = notes.Count;
        _hitNotes = 0;
        _missCount = 0;
        _consecutiveMisses = 0;
        _gameOverTriggered = false;
        _lastComboAuraLevel = 0;
        _maxCombo = 0;
        _currentTrackTitle = title;
        _gameStartDateTime = DateTime.Now;
        _isInGame = true;
        _discord.IsPriorityMode = true;

        GameScore.Text = "0";
        GameCombo.Text = "0x";
        GameAccuracy.Text = "100%";
        GameBpm.Text = $"{bpm:0}";
        GameHeaderTitle.Text = $"{title} · {bpm:0} BPM";
        GameHUDPanel.Visibility = Visibility.Visible;
        JudgeText.Opacity = 0;
        _judgeVisibleUntil = -1;
        Array.Fill(_hitZoneFlashUntil, -1);
        RebuildGameCanvasBase();
        Dispatcher.BeginInvoke(new Action(RebuildGameCanvasBase), DispatcherPriority.Loaded);

        CountdownOverlay.Visibility = Visibility.Visible;
        CountdownText.Text = "3";
        int count = 3;
        _countdownTimer?.Stop();
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            count--;
            if (count > 0) { CountdownText.Text = count.ToString(); return; }
            _countdownTimer.Stop();
            CountdownOverlay.Visibility = Visibility.Collapsed;

            if (mp3Path != null) 
            { 
                _editorPlayer.Volume = Math.Pow(_settings.GameVolume, 3);
                _editorPlayer.Open(new Uri(mp3Path)); 
                _editorPlayer.Play(); 
            }

            _gameClock.Restart();
            CompositionTarget.Rendering -= GameTick;
            CompositionTarget.Rendering += GameTick;
            PreviewKeyDown += Game_KeyDown;
            PreviewKeyUp += Game_KeyUp;
        };

        // Запускаем диспетчер эффектов отдельно от GameTick
        _effectTimer?.Stop();
        _effectTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(16) };
        _effectTimer.Tick += (_, _) => {
            // Выполняем до 3 эффектов за тик чтобы не копились
            for (int i = 0; i < 3 && _effectQueue.TryDequeue(out var action); i++)
                action();
        };
        _effectTimer.Start();

        _countdownTimer.Start();
        
        // Discord Rich Presence - обновление статуса игры
        _discordGameTimer?.Stop();
        _discordGameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _discordGameTimer.Tick += (_, _) => {
            int acc = _totalNotes > 0 ? (int)((double)_hitNotes / _totalNotes * 100) : 100;
            _discord.SetGamePlaying(_currentTrackTitle, _gameCombo, acc, _gameStartDateTime);
        };
        _discordGameTimer.Start();
        // Первое обновление сразу
        _discord.SetGamePlaying(_currentTrackTitle, 0, 100, _gameStartDateTime);
    }

    private static double GetFallSecondsForBpm(double bpm)
    {
        if (bpm <= 0) bpm = REFERENCE_BPM;
        // Усиливаем влияние BPM: используем степень 1.2 вместо линейной зависимости
        double ratio = REFERENCE_BPM / bpm;
        double adjusted = Math.Pow(ratio, 1.2);
        return Math.Clamp(FALL_SEC * adjusted, 0.6, 2.6);
    }

    private void RebuildGameCanvasBase()
    {
        double canvasH = GameCanvas.ActualHeight > 0 ? GameCanvas.ActualHeight : 500;
        double hitY = canvasH - 70;
        GameCanvas.Children.Clear();

        // Aurora-фон игрового поля
        for (int i = 0; i < 4; i++)
        {
            // Вертикальная полоса дорожки
            var laneStrip = new System.Windows.Shapes.Rectangle
            {
                Width = LANE_WIDTH,
                Height = canvasH,
                IsHitTestVisible = false
            };
            laneStrip.Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B), 0),
                    new GradientStop(Color.FromArgb(18, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B), 0.5),
                    new GradientStop(Color.FromArgb(8, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B), 1),
                },
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
            Canvas.SetLeft(laneStrip, GetLaneLeft(i));
            Canvas.SetTop(laneStrip, 0);
            GameCanvas.Children.Add(laneStrip);

            // Левая граница дорожки
            var laneBorder = new System.Windows.Shapes.Rectangle
            {
                Width = 1,
                Height = canvasH,
                Fill = new SolidColorBrush(Color.FromArgb(25, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(laneBorder, GetLaneLeft(i));
            Canvas.SetTop(laneBorder, 0);
            GameCanvas.Children.Add(laneBorder);
        }

        // Горизонтальная линия хит-зоны
        var hitLine = new System.Windows.Shapes.Rectangle
        {
            Width = CANVAS_WIDTH,
            Height = 2,
            IsHitTestVisible = false
        };
        hitLine.Fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(50, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
            new System.Windows.Point(0, 0), new System.Windows.Point(1, 0));
        Canvas.SetLeft(hitLine, 0);
        Canvas.SetTop(hitLine, hitY + 24);
        GameCanvas.Children.Add(hitLine);

        // Кнопки хит-зоны
        for (int i = 0; i < 4; i++)
        {
            var hz = new Border
            {
                Width = NOTE_SIZE,
                Height = NOTE_SIZE,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B)),
                Background = new SolidColorBrush(Color.FromArgb(25, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B)),
                Child = new TextBlock
                {
                    Text = ArrowChars[i],
                    FontSize = ARROW_FONT_SIZE,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, LaneColors[i].R, LaneColors[i].G, LaneColors[i].B)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = LaneColors[i],
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = 0.3
                }
            };
            Canvas.SetLeft(hz, GetLaneLeft(i));
            Canvas.SetTop(hz, hitY);
            GameCanvas.Children.Add(hz);
        }
    }

    private void GameTick(object? s, EventArgs e)
    {
        double now = _gameClock.Elapsed.TotalSeconds;
        double canvasH = GameCanvas.ActualHeight > 0 ? GameCanvas.ActualHeight : 500;
        double hitY = canvasH - 70;

        // Спавн новых нот, используем кэшированные кисти
        while (_pendingNotes.Count > 0 && _pendingNotes[0].Time - now <= _currentFallSec)
        {
            var note = _pendingNotes[0];
            _pendingNotes.RemoveAt(0);

            var effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = LaneColors[note.Lane],
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.6
            };

            var arrow = new Border
            {
                Width = NOTE_SIZE, Height = NOTE_SIZE,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1.5),
                // Используем кэшированные кисти, не создаём новые каждый раз
                BorderBrush = _laneBrushes[note.Lane],
                Background = _noteGradients[note.Lane],
                Tag = note,
                Child = new TextBlock
                {
                    Text = ArrowChars[note.Lane], FontSize = ARROW_FONT_SIZE,
                    Foreground = _laneBrushes[note.Lane],
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Effect = effect
            };
            Canvas.SetLeft(arrow, GetLaneLeft(note.Lane));
            Canvas.SetTop(arrow, -50);
            GameCanvas.Children.Add(arrow);
            _activeNotes.Add(note);
            note.Visual = arrow;
            note.Effect = effect;
        }

        // Обновляем позиции, никаких new объектов
        var toRemove = new List<NoteEntry>();
        foreach (var note in _activeNotes)
        {
            if (note.Visual == null) continue;
            double progress = (now - (note.Time - _currentFallSec)) / _currentFallSec;
            double top = -50 + progress * (hitY + 50);
            Canvas.SetTop(note.Visual, top);

            // Свечение при приближении, обновляем существующий effect
            double distToHit = Math.Abs(top - hitY);
            if (distToHit < 90 && note.Effect != null)
            {
                double proximity = 1.0 - (distToHit / 90.0);
                note.Effect.BlurRadius = 12 + proximity * 22;
                note.Effect.Opacity = 0.6 + proximity * 0.35;
            }

            if (top > canvasH + 10)
            {
                GameCanvas.Children.Remove(note.Visual);
                toRemove.Add(note);
                _gameCombo = 0;
                _missCount++;
                _consecutiveMisses++;
                ShowJudge("MISS", Colors.Gray);
                // Эффекты в очередь, не в game loop
                _effectQueue.Enqueue(() => UpdateComboAura());
                UpdateHUD();

                if ((_missCount >= 10 || _consecutiveMisses >= 10) && !_gameOverTriggered)
                {
                    _gameOverTriggered = true;
                    GameOver(failed: true);
                    return;
                }
            }
        }
        foreach (var n in toRemove) _activeNotes.Remove(n);

        // Judge fade, просто Opacity, без аллокаций
        if (_judgeVisibleUntil > 0 && now >= _judgeVisibleUntil)
        {
            JudgeText.Opacity = 0;
            _judgeVisibleUntil = -1;
        }

        // HitZone restore
        for (int lane = 0; lane < _hitZoneFlashUntil.Length; lane++)
        {
            if (_hitZoneFlashUntil[lane] > 0 && now >= _hitZoneFlashUntil[lane])
            {
                SetHitZoneOpacity(lane, 1.0);
                _hitZoneFlashUntil[lane] = -1;
            }
        }

        if (!_gameOverTriggered && _pendingNotes.Count == 0 && _activeNotes.Count == 0)
            GameOver(failed: false);
    }

    private void Game_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        // Игнорируем автоповтор при удержании клавиши
        if (e.IsRepeat)
            return;

        int lane = GetGameLane(e.Key);
        if (lane < 0) return;
        e.Handled = true;

        _activeLanes.Add(lane);

        double now = _gameClock.Elapsed.TotalSeconds;
        
        // Берём первую нехитную ноту на дорожке по порядку времени
        var best = _activeNotes
            .Where(n => n.Lane == lane && !n.Hit)
            .OrderBy(n => n.Time)
            .FirstOrDefault();

        if (best == null) return;

        double bestDist = Math.Abs(best.Time - now);

        if (bestDist <= HIT_PERFECT)
        {
            HitNote(best, lane, 300, "PERFECT", LaneColors[lane]);
        }
        else if (bestDist <= HIT_GOOD)
        {
            HitNote(best, lane, 100, "GOOD", Color.FromRgb(0xa1, 0xa1, 0xaa));
        }
        else
        {
            _gameCombo = 0;
            ShowJudge("MISS", Colors.Gray);
        }

        UpdateHUD();
    }

    private void Game_KeyUp(object s, System.Windows.Input.KeyEventArgs e)
    {
        int lane = GetGameLane(e.Key);
        if (lane < 0) return;
        _activeLanes.Remove(lane);
        if (_activeLanes.Count == 0)
            _hitLanesThisFrame.Clear();
        e.Handled = true;
    }

    private void HitNote(NoteEntry note, int lane, int baseScore, string judge, Color color)
    {
        note.Hit = true;
        _gameCombo++;
        if (_gameCombo > _maxCombo) _maxCombo = _gameCombo;
        _hitNotes++;
        _consecutiveMisses = 0;
        _gameScore += baseScore * _gameCombo;
        _hitLanesThisFrame.Add(lane);

        // Супер-эффект: одновременно нажаты ← → с попаданием или 3+ любых с попаданием
        if (!_settings.DisableComboEffect)
        {
            bool extremeHit = _activeLanes.Contains(0) && _activeLanes.Contains(3) &&
                              _hitLanesThisFrame.Contains(0) && _hitLanesThisFrame.Contains(3);
            bool tripleHit = _activeLanes.Count >= 3 && _hitLanesThisFrame.Count >= 3;
            if (extremeHit || tripleHit)
            {
                _effectQueue.Enqueue(() => SpawnDoubleStrikeEffect(_currentComboColor));
                _hitLanesThisFrame.Clear();
            }
        }

        // Считаем серию PERFECT
        if (judge == "PERFECT")
        {
            _perfectStreak++;
            // Каждые 10 PERFECT подряд, спецэффект
            if (_perfectStreak > 0 && _perfectStreak % 10 == 0)
            {
                _effectQueue.Enqueue(() =>
                    SpawnMilestoneAnnounce($"PERFECT ×{_perfectStreak}! ✨",
                        Color.FromRgb(0xff, 0xd7, 0x00)));
            }
        }
        else
        {
            _perfectStreak = 0;
        }

        ShowJudge(judge, color);
        FlashHitZone(lane);

        // Тяжёлые эффекты, в очередь, не блокируем GameTick
        int capturedLane = lane;
        string capturedJudge = judge;
        _effectQueue.Enqueue(() =>
        {
            SpawnHitEffect(capturedLane, capturedJudge);
            UpdateComboAura();
        });

        if (note.Visual != null)
            GameCanvas.Children.Remove(note.Visual);

        _activeNotes.Remove(note);
    }

    private void UpdateHUD()
    {
        GameScore.Text = _gameScore.ToString("N0");
        GameCombo.Text = _gameCombo + "x";
        int acc = _totalNotes > 0 ? (int)((double)_hitNotes / _totalNotes * 100) : 100;
        GameAccuracy.Text = acc + "%";

        // Пульс комбо при росте
        if (_gameCombo > 1)
        {
            if (GameCombo.RenderTransform is not ScaleTransform)
            {
                GameCombo.RenderTransform = new ScaleTransform(1, 1);
                GameCombo.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            }
            var pulse = new DoubleAnimation(1.35, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 }
            };
            ((ScaleTransform)GameCombo.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            ((ScaleTransform)GameCombo.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.35, 1.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 }
                });
        }

        // Проверяем игровые события
        CheckGameEvents();
    }

    private void UpdateComboAura()
    {
        // Находим уровень по реальному значению комбо
        int newLevel = 0;
        int[] thresholds = [7, 15, 22, 30, 37, 45, 52, 60, 67, 75, 82, 90, 97, 105, 112, 120, 150, 200, 220, 250, 300];
        for (int i = thresholds.Length - 1; i >= 0; i--)
        {
            if (_gameCombo >= thresholds[i]) { newLevel = i + 1; break; }
        }
        if (newLevel == _lastComboAuraLevel) return;
        _lastComboAuraLevel = newLevel;

        var levels = new (Color main, Color? accent, string announce, byte alpha)[]
        {
            (Color.FromRgb(0x38, 0xbf, 0xf8), null, "COMBO ×7!", 60),
            (Color.FromRgb(0x63, 0x66, 0xf1), null, "COMBO ×15!", 70),
            (Color.FromRgb(0x10, 0xb9, 0x81), null, "COMBO ×22!", 75),
            (Color.FromRgb(0xf5, 0x9e, 0x0b), null, "COMBO ×30! ⚡", 80),
            (Color.FromRgb(0xf9, 0x73, 0x16), null, "COMBO ×37! 🔥", 85),
            (Color.FromRgb(0xf4, 0x3f, 0x5e), null, "COMBO ×45! 💥", 90),
            (Color.FromRgb(0xef, 0x44, 0x44), null, "COMBO ×52!", 95),
            (Color.FromRgb(0xa8, 0x55, 0xf7), null, "COMBO ×60!", 100),
            (Color.FromRgb(0xec, 0x4e, 0xff), null, "COMBO ×67! 🌸", 105),
            (Color.FromRgb(0xff, 0x6b, 0xb5), Color.FromRgb(0x38, 0xbf, 0xf8), "COMBO ×75! 🌈", 110),
            (Color.FromRgb(0xff, 0xeb, 0x3b), Color.FromRgb(0xff, 0xa0, 0x00), "COMBO ×82! 👑", 115),
            (Color.FromRgb(0x7c, 0x3a, 0xed), Color.FromRgb(0xec, 0x4e, 0xff), "COMBO ×90! ⚡💜", 120),
            (Color.FromRgb(0xff, 0x45, 0x00), Color.FromRgb(0xff, 0xd7, 0x00), "COMBO ×97! 🔥👑", 125),
            (Color.FromRgb(0x00, 0xff, 0xff), Color.FromRgb(0x00, 0x80, 0xff), "COMBO ×105! ❄️", 130),
            (Color.FromRgb(0xff, 0xff, 0xff), Color.FromRgb(0xff, 0x6b, 0xb5), "COMBO ×112! 🌟", 135),
            (Color.FromRgb(0xff, 0xd7, 0x00), Color.FromRgb(0xff, 0x45, 0x00), "COMBO ×120! 🔥🌟", 138),
            (Color.FromRgb(0x00, 0xff, 0x88), Color.FromRgb(0x00, 0xcc, 0xff), "COMBO ×150! 💎", 140),
            (Color.FromRgb(0xff, 0x00, 0xff), Color.FromRgb(0xff, 0xff, 0x00), "COMBO ×200! 👑⚡", 145),
            (Color.FromRgb(0xff, 0x45, 0x00), Color.FromRgb(0x7c, 0x3a, 0xed), "COMBO ×220! 🔥💜", 148),
            (Color.FromRgb(0x00, 0xff, 0xff), Color.FromRgb(0xff, 0x00, 0xff), "COMBO ×250! ❄️🌸", 150),
            (Color.FromRgb(0xff, 0xff, 0xff), Color.FromRgb(0xff, 0xd7, 0x00), "MAX COMBO ×300!! 🌟✨🔥", 155),
        };

        // Плавный fade-out старых
        var oldAuras = GamePlayView.Children
            .OfType<System.Windows.Shapes.Rectangle>()
            .Where(r => r.Tag?.ToString() == "combo_aura").ToList();
        _auroraGameTimer?.Stop();
        foreach (var old in oldAuras)
        {
            var fo = new DoubleAnimation(old.Opacity, 0, TimeSpan.FromMilliseconds(500))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fo.Completed += (_, _) => GamePlayView.Children.Remove(old);
            old.BeginAnimation(UIElement.OpacityProperty, fo);
        }

        if (newLevel == 0) return; // ниже первого порога, ничего не делаем

        var (mainColor, accentColor, announceText, alpha) = levels[newLevel - 1];
        Color c = mainColor;
        _currentComboColor = mainColor;

        // Создаём виньетку (как danger mode, но цветную)
        var vignette = new System.Windows.Shapes.Rectangle
        {
            Tag = "combo_aura",
            IsHitTestVisible = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };

        // Виньетка, цветная по краям, прозрачная в центре
        // Для высоких уровней (10+) добавляем акцентный цвет в центре
        var stops = new GradientStopCollection();
        
        if (newLevel >= 10 && accentColor.HasValue)
        {
            // Двухцветная виньетка: акцент в центре, основной по краям
            stops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.15), accentColor.Value.R, accentColor.Value.G, accentColor.Value.B), 0.00));
            stops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.08), accentColor.Value.R, accentColor.Value.G, accentColor.Value.B), 0.35));
            stops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.45));
            stops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.25), c.R, c.G, c.B), 0.60));
            stops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.50), c.R, c.G, c.B), 0.75));
            stops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.75), c.R, c.G, c.B), 0.88));
            stops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 1.00));
        }
        else
        {
            // Обычная виньетка: прозрачный центр, цвет по краям
            stops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.00));
            stops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.45));
            stops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.20), c.R, c.G, c.B), 0.60));
            stops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.45), c.R, c.G, c.B), 0.75));
            stops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.70), c.R, c.G, c.B), 0.88));
            stops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 1.00));
        }

        vignette.Fill = new RadialGradientBrush(stops)
        {
            ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
            Center = new System.Windows.Point(0.5, 0.5),
            GradientOrigin = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.85, RadiusY = 0.85
        };

        GamePlayView.Children.Add(vignette);

        // Fade-in виньетки
        vignette.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // Пульсация, скорость и амплитуда растут с уровнем
        double pulse = 0;
        double speed = 0.025 + newLevel * 0.010;
        double ampli = 0.15 + newLevel * 0.010; // max ~0.30

        _auroraGameTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(40) };
        _auroraGameTimer.Tick += (_, _) =>
        {
            pulse += speed;
            double p = 1.0 - ampli + Math.Sin(pulse) * ampli;
            vignette.Opacity = p;
        };
        _auroraGameTimer.Start();

        // Звёздочки с уровня 4+
        if (newLevel >= 4) StartStarBurst(c, Math.Min(newLevel - 3, 4));

        // Анонс
        SpawnComboAnnounce(newLevel, c, announceText);
    }

    private void StartStarBurst(Color color, int level)
    {
        _starTimer?.Stop();
        
        // На максимальном уровне (15), НАМНОГО больше звёзд
        _starBurst = level >= 12 ? 20 : (level >= 3 ? 12 : 8);

        var rng = new Random();
        _starTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(level >= 12 ? 80 : (level >= 3 ? 130 : 190)) };

        _starTimer.Tick += (_, _) =>
        {
            if (_starBurst <= 0 || _lastComboAuraLevel < 2)
            {
                _starTimer?.Stop();
                return;
            }
            _starBurst--;

            int count = level >= 12 ? 10 : (level >= 3 ? 6 : 3);
            double viewWidth  = GameCanvas.ActualWidth;   // 240px, только игровое поле
            double viewHeight = GameCanvas.ActualHeight;  // без нижней панели

            for (int i = 0; i < count; i++)
            {
                double startX = rng.NextDouble() * viewWidth;
                double startY = viewHeight + 10;
                double endX   = startX + rng.Next(-80, 80);
                double endY   = rng.Next((int)(viewHeight * 0.2), (int)(viewHeight * 0.7)); // в пределах канваса
                double size   = rng.Next(6, level >= 3 ? 18 : 14);
                double dur    = 1200 + rng.Next(0, 600);

                // Для белого цвета (максимальное комбо), используем радужные цвета
                Color starColor;
                if (color.R >= 250 && color.G >= 250 && color.B >= 250)
                {
                    // Радужные звёзды для белого комбо
                    var rainbowColors = new[]
                    {
                        Color.FromRgb(0xff, 0x6b, 0xb5), // розовый
                        Color.FromRgb(0xff, 0xd7, 0x00), // золотой
                        Color.FromRgb(0x00, 0xff, 0xff), // cyan
                        Color.FromRgb(0xff, 0x45, 0x00), // оранжевый
                        Color.FromRgb(0xec, 0x4e, 0xff), // фиолетовый
                        Color.FromRgb(0x22, 0xc5, 0x5e), // зелёный
                    };
                    starColor = rainbowColors[rng.Next(rainbowColors.Length)];
                }
                else
                {
                    // Обычные звёзды, светлее основного цвета
                    starColor = Color.FromArgb(
                        (byte)rng.Next(200, 255),
                        (byte)Math.Min(255, color.R + 40),
                        (byte)Math.Min(255, color.G + 40),
                        (byte)Math.Min(255, color.B + 40));
                }

                var star = new TextBlock
                {
                    Text = StarChars[rng.Next(StarChars.Length)],
                    FontSize = size,
                    Foreground = new SolidColorBrush(starColor),
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    RenderTransform = new TransformGroup
                    {
                        Children =
                        {
                            new TranslateTransform(),
                            new RotateTransform(),
                            new ScaleTransform(0.3, 0.3)
                        }
                    }
                };

                Canvas.SetLeft(star, startX);
                Canvas.SetTop(star, endY);
                GameCanvas.Children.Add(star); // <-- GameCanvas, не GamePlayView!

                var tg        = (TransformGroup)star.RenderTransform;
                var translate = (TranslateTransform)tg.Children[0];
                var rotate    = (RotateTransform)tg.Children[1];
                var scale     = (ScaleTransform)tg.Children[2];

                var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

                var moveX = new DoubleAnimation(endX - startX, 0,
                    TimeSpan.FromMilliseconds(dur)) { EasingFunction = easeOut };
                var moveY = new DoubleAnimation(startY - endY, 0,
                    TimeSpan.FromMilliseconds(dur)) { EasingFunction = easeOut };

                var spin = new DoubleAnimation(0, rng.Next(90, 270),
                    TimeSpan.FromMilliseconds(dur));

                var scaleAnim = new DoubleAnimationUsingKeyFrames();
                scaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.3, KeyTime.FromPercent(0)));
                scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromPercent(0.35),
                    new CubicEase { EasingMode = EasingMode.EaseOut }));
                scaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0),
                    new CubicEase { EasingMode = EasingMode.EaseIn }));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(dur);

                var fade = new DoubleAnimation(1, 0,
                    TimeSpan.FromMilliseconds(dur * 0.4))
                    { BeginTime = TimeSpan.FromMilliseconds(dur * 0.6) };
                fade.Completed += (_, _) => GameCanvas.Children.Remove(star); // <-- тоже GameCanvas

                translate.BeginAnimation(TranslateTransform.XProperty, moveX);
                translate.BeginAnimation(TranslateTransform.YProperty, moveY);
                rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim.Clone());
                star.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        };

        _starTimer.Start();
    }

    private void CheckGameEvents()
    {
        if (_gameOverTriggered) return;
        int totalPlayed = _hitNotes + _missCount;

        // ── Полтрека пройдено ────────────────────────────────────────────
        if (!_halfwayTriggered && _totalNotes > 0 && totalPlayed >= _totalNotes / 2)
        {
            _halfwayTriggered = true;
            SpawnMilestoneAnnounce("ПОЛПУТИ! 🎯", Color.FromRgb(0x06, 0xb6, 0xd4));
            FlashScreenOnce(Color.FromArgb(30, 0x06, 0xb6, 0xd4), 800);
        }

        // ── Опасность: осталось 3 или меньше жизней (из 10) ──────────────────────
        int livesLeft = 10 - _missCount;
        bool danger = livesLeft <= 3 && livesLeft > 0 && !_gameOverTriggered;
        if (danger && !_dangerModeActive)
        {
            _dangerModeActive = true;
            StartDangerMode();
        }
        else if (!danger && _dangerModeActive)
        {
            _dangerModeActive = false;
            StopDangerMode();
        }
    }

    private void SpawnMilestoneAnnounce(string text, Color color)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Opacity = 0,
            Margin = new Thickness(0, 120, 0, 0),
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.7, 0.7),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = color, BlurRadius = 16, ShadowDepth = 0, Opacity = 1 }
        };
        GamePlayView.Children.Add(tb);

        var st = (ScaleTransform)tb.RenderTransform;
        tb.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.7, 1.05, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.7, 1.05, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600))
            { BeginTime = TimeSpan.FromMilliseconds(2000) };
        fadeOut.Completed += (_, _) => GamePlayView.Children.Remove(tb);
        tb.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void FlashScreenOnce(Color color, int durationMs)
    {
        var flash = new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(color),
            IsHitTestVisible = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };
        GamePlayView.Children.Add(flash);

        var up = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs * 0.3));
        var down = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(durationMs * 0.7))
            { BeginTime = TimeSpan.FromMilliseconds(durationMs * 0.3) };
        down.Completed += (_, _) => GamePlayView.Children.Remove(flash);
        flash.BeginAnimation(UIElement.OpacityProperty, up);
        flash.BeginAnimation(UIElement.OpacityProperty, down);
    }

    private void StartDangerMode()
    {
        SpawnMilestoneAnnounce("⚠️ ОСТОРОЖНО!", Color.FromRgb(0xef, 0x44, 0x44));

        // Красная пульсирующая рамка по краям экрана
        var danger = new System.Windows.Shapes.Rectangle
        {
            Tag = "danger_vignette",
            IsHitTestVisible = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };

        // Виньетка, красная по краям, прозрачная в центре
        danger.Fill = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0,   0xef, 0x44, 0x44), 0.00),
            new GradientStop(Color.FromArgb(0,   0xef, 0x44, 0x44), 0.45),
            new GradientStop(Color.FromArgb(15,  0xef, 0x44, 0x44), 0.60),
            new GradientStop(Color.FromArgb(35,  0xef, 0x44, 0x44), 0.75),
            new GradientStop(Color.FromArgb(60,  0xef, 0x44, 0x44), 0.88),
            new GradientStop(Color.FromArgb(80,  0xef, 0x44, 0x44), 1.00),
        })
        {
            ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
            Center = new System.Windows.Point(0.5, 0.5),
            GradientOrigin = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.85, RadiusY = 0.85
        };

        GamePlayView.Children.Add(danger);

        // Пульсация рамки
        _dangerPulseTimer?.Stop();
        double dp = 0;
        _dangerPulseTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(50) };
        _dangerPulseTimer.Tick += (_, _) =>
        {
            dp += 0.08;
            danger.Opacity = 0.5 + Math.Sin(dp) * 0.5; // от 0 до 1
        };
        _dangerPulseTimer.Start();
    }

    private void StopDangerMode()
    {
        _dangerPulseTimer?.Stop();
        _dangerPulseTimer = null;

        var vigsToRemove = GamePlayView.Children.OfType<System.Windows.Shapes.Rectangle>()
            .Where(r => r.Tag?.ToString() == "danger_vignette").ToList();
        foreach (var v in vigsToRemove)
        {
            var fo = new DoubleAnimation(v.Opacity, 0, TimeSpan.FromMilliseconds(500));
            fo.Completed += (_, _) => GamePlayView.Children.Remove(v);
            v.BeginAnimation(UIElement.OpacityProperty, fo);
        }
    }

    private void SpawnComboAnnounce(int level, Color color, string text)
    {
        double fontSize = Math.Min(14 + level * 1.2, 32);

        var announce = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.5, 0.5),
            Margin = new Thickness(0, level >= 8 ? 55 : 75, 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = color,
                BlurRadius = 8 + level * 2,
                ShadowDepth = 0,
                Opacity = 0.9
            }
        };
        GamePlayView.Children.Add(announce);

        double amplitude = 0.25 + Math.Min(level * 0.05, 0.4);
        double peakScale = 1.0 + amplitude;
        var st = (ScaleTransform)announce.RenderTransform;

        announce.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.5, peakScale, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude } });
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.5, peakScale, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude } });

        // Отскок назад
        var scaleBack = new DoubleAnimation(peakScale, 1.0, TimeSpan.FromMilliseconds(200))
            { BeginTime = TimeSpan.FromMilliseconds(300) };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleBack);
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(peakScale, 1.0, TimeSpan.FromMilliseconds(200))
            { BeginTime = TimeSpan.FromMilliseconds(300) });

        int holdMs = 1400 + Math.Min(level * 60, 600);
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350))
            { BeginTime = TimeSpan.FromMilliseconds(holdMs) };
        fadeOut.Completed += (_, _) => GamePlayView.Children.Remove(announce);
        announce.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void ShowJudge(string text, Color color)
    {
        // MISS показываем статично, PERFECT/GOOD, через SpawnHitEffect
        if (text == "MISS")
        {
            JudgeText.Text = "MISS";
            JudgeText.Foreground = new SolidColorBrush(Color.FromArgb(180, 0x88, 0x88, 0x88));
            JudgeText.Opacity = 1;
            _judgeVisibleUntil = _gameClock.Elapsed.TotalSeconds + 0.35;
        }
    }

    private void FlashHitZone(int lane)
    {
        SetHitZoneOpacity(lane, 0.45);
        _hitZoneFlashUntil[lane] = _gameClock.Elapsed.TotalSeconds + 0.08;
    }

    private void SpawnHitEffect(int lane, string judge)
    {
        double canvasH = GameCanvas.ActualHeight > 0 ? GameCanvas.ActualHeight : 500;
        double hitY = canvasH - 70;
        double centerX = GetLaneCenterX(lane);
        double centerY = hitY + (NOTE_SIZE / 2);

        // 1. Вспышка-круг
        var flash = new Ellipse
        {
            Width = 60,
            Height = 60,
            Fill = new RadialGradientBrush(
                Color.FromArgb(200, LaneColors[lane].R, LaneColors[lane].G, LaneColors[lane].B),
                Color.FromArgb(0, LaneColors[lane].R, LaneColors[lane].G, LaneColors[lane].B)),
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.2, 0.2)
        };
        Canvas.SetLeft(flash, centerX - 30);
        Canvas.SetTop(flash, centerY - 30);
        GameCanvas.Children.Add(flash);

        var scaleX = new DoubleAnimation(0.2, 2.2, TimeSpan.FromMilliseconds(350))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var scaleY = new DoubleAnimation(0.2, 2.2, TimeSpan.FromMilliseconds(350))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fadeFlash = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
        fadeFlash.Completed += (_, _) => GameCanvas.Children.Remove(flash);
        ((ScaleTransform)flash.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        ((ScaleTransform)flash.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        flash.BeginAnimation(UIElement.OpacityProperty, fadeFlash);

        // 2. Расширяющееся кольцо
        var ring = new Ellipse
        {
            Width = 50,
            Height = 50,
            Stroke = new SolidColorBrush(Color.FromArgb(180, LaneColors[lane].R, LaneColors[lane].G, LaneColors[lane].B)),
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };
        Canvas.SetLeft(ring, centerX - 25);
        Canvas.SetTop(ring, centerY - 25);
        GameCanvas.Children.Add(ring);

        var ringScale = new DoubleAnimation(1, 2.8, TimeSpan.FromMilliseconds(450))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fadeRing = new DoubleAnimation(0.8, 0, TimeSpan.FromMilliseconds(450));
        fadeRing.Completed += (_, _) => GameCanvas.Children.Remove(ring);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, ringScale);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 2.8, TimeSpan.FromMilliseconds(450))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        ring.BeginAnimation(UIElement.OpacityProperty, fadeRing);

        // 3. Частицы, разлетаются во все стороны
        var rng = new Random();
        int particleCount = judge == "PERFECT" ? 8 : 5;
        for (int p = 0; p < particleCount; p++)
        {
            double angle = (p * 360.0 / particleCount) + rng.Next(-15, 15);
            double rad = angle * Math.PI / 180.0;
            double dist = 35 + rng.Next(15, 35);
            double size = judge == "PERFECT" ? rng.Next(5, 9) : rng.Next(3, 7);

            var particle = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(LaneColors[lane]),
                IsHitTestVisible = false,
                RenderTransform = new TranslateTransform()
            };
            Canvas.SetLeft(particle, centerX - size / 2);
            Canvas.SetTop(particle, centerY - size / 2);
            GameCanvas.Children.Add(particle);

            var tx = new DoubleAnimation(0, Math.Cos(rad) * dist, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var ty = new DoubleAnimation(0, Math.Sin(rad) * dist - 15, TimeSpan.FromMilliseconds(420))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fadeP = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(420));
            fadeP.Completed += (_, _) => GameCanvas.Children.Remove(particle);

            ((TranslateTransform)particle.RenderTransform).BeginAnimation(TranslateTransform.XProperty, tx);
            ((TranslateTransform)particle.RenderTransform).BeginAnimation(TranslateTransform.YProperty, ty);
            particle.BeginAnimation(UIElement.OpacityProperty, fadeP);
        }

        // 4. Всплывающий текст PERFECT / GOOD
        if (judge == "PERFECT" || judge == "GOOD")
        {
            var judgeColor = judge == "PERFECT" ? LaneColors[lane] : Color.FromRgb(0xa1, 0xa1, 0xaa);
            var floatText = new TextBlock
            {
                Text = judge,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = judge == "PERFECT" ? 16 : 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(judgeColor),
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = judgeColor,
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.9
                }
            };

            // Измеряем размер перед добавлением
            floatText.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(floatText, centerX - floatText.DesiredSize.Width / 2);
            Canvas.SetTop(floatText, hitY - 15);
            GameCanvas.Children.Add(floatText);

            var moveUp = new DoubleAnimation(hitY - 15, hitY - 65, TimeSpan.FromMilliseconds(650))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fadeText = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(650));
            fadeText.Completed += (_, _) => GameCanvas.Children.Remove(floatText);

            floatText.BeginAnimation(Canvas.TopProperty, moveUp);
            floatText.BeginAnimation(UIElement.OpacityProperty, fadeText);
        }
    }

    private void SetHitZoneOpacity(int lane, double opacity)
    {
        foreach (var child in GameCanvas.Children.OfType<Border>())
        {
            if (Math.Abs(Canvas.GetLeft(child) - GetLaneLeft(lane)) < 1 && child.Tag == null)
            {
                child.Opacity = opacity;
                break;
            }
        }
    }

    private void GameOver(bool failed = false)
    {
        CompositionTarget.Rendering -= GameTick;
        PreviewKeyDown -= Game_KeyDown;
        PreviewKeyUp -= Game_KeyUp;
        _activeLanes.Clear();
        _hitLanesThisFrame.Clear();
        _editorPlayer.Stop();
        _gameClock.Stop();
        _auroraGameTimer?.Stop();

        int acc = _totalNotes > 0 ? (int)((double)_hitNotes / _totalNotes * 100) : 100;
        string rank = failed ? "F" : acc >= 95 ? "S" : acc >= 85 ? "A" : acc >= 70 ? "B" : acc >= 50 ? "C" : "D";

        // Discord - обновляем статус с результатами
        _discordGameTimer?.Stop();
        _discordGameTimer = null;
        _discord.SetGameResults(_currentTrackTitle, rank, _gameScore, acc, _maxCombo);

        // Обновляем агрегированную статистику по треку
        var existing = _settings.TrackHistory
            .FirstOrDefault(x => x.TrackTitle == _currentTrackTitle);
        if (existing is null)
        {
            existing = new GameTrackStats
            {
                TrackTitle = _currentTrackTitle ?? "Unknown",
                FirstPlayed = DateTime.Now
            };
            _settings.TrackHistory.Add(existing);
        }

        existing.TimesPlayed++;
        existing.LastPlayed = DateTime.Now;
        existing.TotalHits += _hitNotes;
        existing.TotalMisses += _missCount;
        existing.TotalNotes += _totalNotes;
        existing.TotalKeyPresses += _hitNotes + _missCount;
        if (_gameScore > existing.BestScore) existing.BestScore = _gameScore;
        if (_gameScore < existing.MinScore) existing.MinScore = _gameScore;
        if (acc > existing.BestAccuracy) existing.BestAccuracy = Math.Round((double)acc, 1);
        if (acc < existing.WorstAccuracy) existing.WorstAccuracy = Math.Round((double)acc, 1);
        if (_maxCombo > existing.BestCombo) existing.BestCombo = _maxCombo;
        string[] rankOrder = ["S", "A", "B", "C", "D", "F"];
        if (Array.IndexOf(rankOrder, rank) < Array.IndexOf(rankOrder, existing.BestRank))
            existing.BestRank = rank;

        SettingsService.Save(_settings);

        // НЕ сбрасываем флаги здесь - пользователь все еще смотрит на результаты
        // _isInGame остается true, _discord.IsPriorityMode остается true

        // Цвет ранга
        Color rankColor = rank switch
        {
            "S" => Color.FromRgb(0xff, 0xd7, 0x00),
            "A" => Color.FromRgb(0x22, 0xc5, 0x5e),
            "B" => Color.FromRgb(0x3b, 0x82, 0xf6),
            "C" => Color.FromRgb(0xf5, 0x9e, 0x0b),
            "D" => Color.FromRgb(0x88, 0x88, 0x88),
            "F" => Color.FromRgb(0xef, 0x44, 0x44),
            _ => Color.FromRgb(0x88, 0x88, 0x88)
        };

        // Оверлей результатов
        var overlay = new Border
        {
            Tag = "game_results_overlay",
            Background = new SolidColorBrush(Color.FromArgb(220, 0x05, 0x05, 0x0f)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };

        // Aurora-фон результатов
        var auroraBg = new System.Windows.Shapes.Rectangle
        {
            IsHitTestVisible = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        auroraBg.Fill = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(60, rankColor.R, rankColor.G, rankColor.B), 0),
            new GradientStop(Color.FromArgb(20, rankColor.R, rankColor.G, rankColor.B), 0.5),
            new GradientStop(Color.FromArgb(0, rankColor.R, rankColor.G, rankColor.B), 1),
        })
        { Center = new System.Windows.Point(0.5, 0.4), GradientOrigin = new System.Windows.Point(0.5, 0.4), RadiusX = 0.7, RadiusY = 0.6 };

        var content = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new TranslateTransform(0, 30)
        };

        // Заголовок
        var headerText = new TextBlock
        {
            Text = failed ? "ПРОВАЛ" : "РЕЗУЛЬТАТЫ",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(150, 0xff, 0xff, 0xff)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        content.Children.Add(headerText);

        // Большой ранг
        var rankBorder = new Border
        {
            Width = 120, Height = 120,
            CornerRadius = new CornerRadius(60),
            BorderBrush = new SolidColorBrush(rankColor),
            BorderThickness = new Thickness(3),
            Background = new SolidColorBrush(Color.FromArgb(30, rankColor.R, rankColor.G, rankColor.B)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = rankColor, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.8
            },
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0, 0)
        };
        rankBorder.Child = new TextBlock
        {
            Text = rank,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 56,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(rankColor),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(rankBorder);

        // Карточки со статистикой
        var statsGrid = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        statsGrid.RowDefinitions.Add(new RowDefinition());
        statsGrid.RowDefinitions.Add(new RowDefinition());

        void AddStat(int col, int row, string label, string value, Color color)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(80, 0x25, 0x25, 0x25)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(4),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1)
            };
            var sp = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                FontWeight = FontWeights.Bold
            });
            sp.Children.Add(new TextBlock
            {
                Text = value, FontFamily = new FontFamily("Segoe UI"), FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6
                }
            });
            card.Child = sp;
            Grid.SetColumn(card, col);
            Grid.SetRow(card, row);
            statsGrid.Children.Add(card);
        }

        int perfect = _hitNotes; // упрощение
        int misses = _missCount;
        int maxCombo = _gameCombo; // не идеально но без отдельного поля

        AddStat(0, 0, "СЧЁТ", _gameScore.ToString("N0"), Color.FromRgb(0xff, 0xff, 0xff));
        AddStat(1, 0, "ТОЧНОСТЬ", $"{acc}%", acc >= 90 ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0xf5, 0x9e, 0x0b));
        AddStat(2, 0, "КОМБО", $"{_gameCombo}x", Color.FromRgb(0x63, 0x66, 0xf1));
        AddStat(0, 1, "ВСЕГО НОТ", _totalNotes.ToString(), Color.FromRgb(0xaa, 0xaa, 0xaa));
        AddStat(1, 1, "ПОПАДАНИЙ", _hitNotes.ToString(), Color.FromRgb(0x22, 0xc5, 0x5e));
        AddStat(2, 1, "ПРОМАХОВ", misses.ToString(), Color.FromRgb(0xef, 0x44, 0x44));

        content.Children.Add(statsGrid);

        // Мотивационный текст
        string motivation = rank switch
        {
            "S" => "Абсолютное мастерство! 🌟",
            "A" => "Отличная игра! 🔥",
            "B" => "Хороший результат! 👍",
            "C" => "Неплохо, но можно лучше",
            "D" => "Тренируйся ещё!",
            _ => failed ? "Слишком много промахов..." : "Продолжай пробовать!"
        };

        content.Children.Add(new TextBlock
        {
            Text = motivation,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 0xff, 0xff, 0xff)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 28)
        });

        // Кнопки
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };

        var retryBtn = new Button
        {
            Content = "▶  Ещё раз",
            Style = (Style)FindResource("AccentBtn"),
            Width = 130, Height = 42,
            Margin = new Thickness(0, 0, 10, 0),
            FontSize = 14
        };
        retryBtn.Click += (_, _) =>
        {
            GamePlayView.Children.Remove(overlay);
            
            // Сбрасываем Discord флаги перед перезапуском игры
            _isInGame = false;
            _discord.IsPriorityMode = false;
            
            // Перезапускаем ту же игру
            if (_lastGameNotes != null && _lastGameTitle != null)
            {
                StartGame(_lastGameNotes, _lastGameMp3Path, _lastGameTitle, _lastGameBpm);
            }
            else
            {
                // Если параметры не сохранены, возвращаемся к выбору
                ShowGameView(GameTrackSelectView);
                StopGame();
            }
        };

        var menuBtn = new Button
        {
            Content = "В меню",
            Style = (Style)FindResource("OutlineBtn"),
            Width = 110, Height = 42,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0))
        };
        menuBtn.Click += (_, _) =>
        {
            GamePlayView.Children.Remove(overlay);
            
            // Сбрасываем Discord флаги и возвращаем статус в главное меню
            _isInGame = false;
            _discord.IsPriorityMode = false;
            
            StopGame();
            ShowGameView(GameMenuView);
        };

        btnPanel.Children.Add(retryBtn);
        btnPanel.Children.Add(menuBtn);
        content.Children.Add(btnPanel);

        // Собираем оверлей
        var overlayGrid = new Grid();
        overlayGrid.Children.Add(auroraBg);
        overlayGrid.Children.Add(content);
        overlay.Child = overlayGrid;
        GamePlayView.Children.Add(overlay);

        // Анимация появления оверлея
        overlay.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
        content.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(500)))
            { BeginTime = TimeSpan.FromMilliseconds(200) });
        ((TranslateTransform)content.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(500))
            {
                BeginTime = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        // Ранг появляется с пружиной
        var rankScale = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
        {
            BeginTime = TimeSpan.FromMilliseconds(400),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
        };
        ((ScaleTransform)rankBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, rankScale);
        ((ScaleTransform)rankBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
            {
                BeginTime = TimeSpan.FromMilliseconds(400),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
            });
    }

    private void StopGame()
    {
        CompositionTarget.Rendering -= GameTick;
        _gameTimer?.Stop();
        _gameTimer = null;
        PreviewKeyDown -= Game_KeyDown;
        PreviewKeyUp -= Game_KeyUp;
        _activeLanes.Clear();
        _hitLanesThisFrame.Clear();
        _editorPlayer.Stop();
        _editorPlayer.SpeedRatio = 1.0;
        _gameClock.Stop();
        GameCanvas.Children.Clear();
        
        // Останавливаем countdown если он активен
        _countdownTimer?.Stop();
        _countdownTimer = null;
        CountdownOverlay.Visibility = Visibility.Collapsed;
        
        _auroraGameTimer?.Stop();
        _auroraGameTimer = null;
        _lastComboAuraLevel = 0;
        
        _effectTimer?.Stop();
        _effectTimer = null;
        while (_effectQueue.TryDequeue(out _)) { } // очищаем очередь
        
        _starTimer?.Stop();
        _starTimer = null;
        
        _dangerPulseTimer?.Stop();
        _dangerPulseTimer = null;
        _halfwayTriggered = false;
        _dangerModeActive = false;
        _perfectStreak = 0;
        
        // Discord - возвращаемся в главное меню
        _discordGameTimer?.Stop();
        _discordGameTimer = null;
        _isInGame = false;
        _discord.IsPriorityMode = false;
        _discord.SetMainMenu();
        
        // Убираем виньетку опасности
        var vigs = GamePlayView.Children.OfType<System.Windows.Shapes.Rectangle>()
            .Where(r => r.Tag?.ToString() == "danger_vignette").ToList();
        foreach (var v in vigs) GamePlayView.Children.Remove(v);
        
        // Плавно скрываем combo-ауры
        var auras = GamePlayView.Children.OfType<System.Windows.Shapes.Rectangle>()
            .Where(r => r.Tag?.ToString() == "combo_aura").ToList();
        foreach (var aura in auras)
        {
            var fadeOut = new DoubleAnimation(aura.Opacity, 0, TimeSpan.FromMilliseconds(600));
            fadeOut.Completed += (_, _) => GamePlayView.Children.Remove(aura);
            aura.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
        
        // Удаляем оверлей результатов если он есть
        var resultsOverlays = GamePlayView.Children.OfType<Border>()
            .Where(b => b.Tag?.ToString() == "game_results_overlay").ToList();
        foreach (var overlay in resultsOverlays)
        {
            GamePlayView.Children.Remove(overlay);
        }
        
        // Включаем навигационные кнопки обратно (если не в оверлей режиме)
        if (!_gameOverlayActive)
        {
            ServicesBtn.IsEnabled = true;
            FaqNavBtn.IsEnabled = true;
            DiagNavBtn.IsEnabled = true;
            SettingsBtn.IsEnabled = true;
        }
        
        // Сброс заголовка и HUD
        GameHeaderTitle.Text = "Мини-игра";
        GameHUDPanel.Visibility = Visibility.Collapsed;
    }

    // ── Игра: пользовательские уровни ────────────────────────────────────────
    private void UserLevelCard_Click(object s, MouseButtonEventArgs e)
    {
        if ((s as Border)?.Tag is not NoteMap map) return;
        var dir = System.IO.Path.Combine(LevelsDir, map.Title ?? "level");
        var mp3 = System.IO.Path.Combine(dir, map.TrackFile ?? "track.mp3");
        var bpm = map.Bpm > 0 ? map.Bpm : REFERENCE_BPM;
        ShowGameView(GamePlayView);
        StartGame(map.Notes, File.Exists(mp3) ? mp3 : null, map.Title ?? "Custom Level", bpm);
    }

    private void PlayUserLevel_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true; // Предотвращаем всплытие к Border
        if ((s as Button)?.Tag is not NoteMap map) return;
        var dir = System.IO.Path.Combine(LevelsDir, map.Title ?? "level");
        var mp3 = System.IO.Path.Combine(dir, map.TrackFile ?? "track.mp3");
        var bpm = map.Bpm > 0 ? map.Bpm : REFERENCE_BPM;
        ShowGameView(GamePlayView);
        StartGame(map.Notes, File.Exists(mp3) ? mp3 : null, map.Title ?? "Custom Level", bpm);
    }

    private void ExportUserLevel_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true; // Предотвращаем всплытие к Border
        if ((s as Button)?.Tag is not NoteMap map) return;
        var dir = System.IO.Path.Combine(LevelsDir, map.Title ?? "level");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = map.Title + "_export",
            DefaultExt = ".zip",
            Filter = "ZIP Archive|*.zip"
        };
        if (dlg.ShowDialog() != true) return;
        ZipFile.CreateFromDirectory(dir, dlg.FileName);
    }

    private void ImportLevel_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выбрать уровень",
            Filter = "Уровни|*.zip;*.osz|ZIP архив NetFix|*.zip|osu!mania архив|*.osz",
            DefaultExt = ".zip"
        };
        if (dlg.ShowDialog() != true) return;

        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        if (ext == ".osz")
        {
            ShowNotification("Osu! файлы", "Для импорта .osz файлов используй раздел «Osu! режим»", isError: false, isWarning: true);
            return;
        }

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(dlg.FileName, tempDir);

            var notesJsonPath = Path.Combine(tempDir, "notes.json");
            if (!File.Exists(notesJsonPath))
            {
                ShowNotification("Ошибка импорта", "Архив не содержит файл notes.json", isError: true);
                Directory.Delete(tempDir, true);
                return;
            }
            var json = File.ReadAllText(notesJsonPath);
            var map = JsonSerializer.Deserialize<NoteMap>(json);
            if (map is null || string.IsNullOrEmpty(map.Title))
            {
                ShowNotification("Ошибка импорта", "Некорректный формат notes.json", isError: true);
                Directory.Delete(tempDir, true);
                return;
            }
            FinishLevelImport(map, tempDir);
        }
        catch (Exception ex)
        {
            ShowNotification("Ошибка импорта", $"Не удалось импортировать трек: {ex.Message}", isError: true);
        }
    }

    private void StartOszImport(string oszPath, bool isOsuMode = false)
    {
        System.Diagnostics.Debug.WriteLine($"[OSZ] StartOszImport path='{oszPath}' isOsuMode={isOsuMode}");

        try
        {
            var difficulties = new List<(string Name, string FileName, int KeyCount)>();

            using (var archive = ZipFile.OpenRead(oszPath))
            {
                foreach (var entry in archive.Entries
                    .Where(e => e.Name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
                {
                    string diffName = entry.Name;
                    int keyCount = 4;
                    int mode = -1;

                    using var reader = new StreamReader(entry.Open());
                    string? line;
                    string section = "";
                    while ((line = reader.ReadLine()) is not null)
                    {
                        line = line.Trim();
                        if (line.StartsWith('[')) { section = line; continue; }
                        if (section == "[General]" && line.StartsWith("Mode:"))
                            int.TryParse(line["Mode:".Length..].Trim(), out mode);
                        if (section == "[Difficulty]" && line.StartsWith("CircleSize:"))
                            int.TryParse(line["CircleSize:".Length..].Trim(), out keyCount);
                        if (section == "[Metadata]" && line.StartsWith("Version:"))
                            diffName = line["Version:".Length..].Trim();
                    }

                    if (mode == 3)
                        difficulties.Add((diffName, entry.Name, keyCount));
                }
            }

            if (difficulties.Count == 0)
            {
                ShowNotification("Ошибка", "В архиве нет osu!mania карт (Mode=3)", isError: true);
                return;
            }

            if (difficulties.Count == 1)
            {
                ExecuteOszImport(oszPath, difficulties[0].FileName, difficulties[0].KeyCount, isOsuMode);
                return;
            }

            _pendingOszPath = oszPath;
            _oszReturnView = OsuModeView.Visibility == Visibility.Visible
                ? OsuModeView
                : (UIElement)GameTrackSelectView;
            ShowGameView(OszDifficultyView);
            ShowOszDifficultyPicker(difficulties, isOsuMode);
        }
        catch (Exception ex)
        {
            ShowNotification("Ошибка импорта", ex.Message, isError: true);
        }
    }

    private void ShowOszDifficultyPicker(List<(string Name, string FileName, int KeyCount)> difficulties, bool isOsuMode = false)
    {
        OszDifficultyPanel.Children.Clear();
        OszDifficultySubtext.Text = $"{difficulties.Count} сложностей, выбери одну для импорта";

        foreach (var diff in difficulties)
        {
            var captured = diff;
            var btn = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand
            };
            var inner = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14, 16, 14)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = captured.Name,
                FontSize = 15, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameBlock, 0);

            var keysBlock = new TextBlock
            {
                Text = $"{captured.KeyCount}K",
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(keysBlock, 1);

            grid.Children.Add(nameBlock);
            grid.Children.Add(keysBlock);
            inner.Child = grid;
            btn.Child = inner;

            btn.MouseEnter += (s, _) =>
                ((Border)s).Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
            btn.MouseLeave += (s, _) =>
                ((Border)s).Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            btn.MouseLeftButtonUp += (_, _) =>
            {
                OszDifficultyView.Visibility = Visibility.Collapsed;
                (_oszReturnView ?? GameTrackSelectView).Visibility = Visibility.Visible;
                if (_pendingOszPath is not null)
                    ExecuteOszImport(_pendingOszPath, captured.FileName, captured.KeyCount, isOsuMode);
            };

            OszDifficultyPanel.Children.Add(btn);
        }
    }

    private void ExecuteOszImport(string oszPath, string osuFileName, int keyCount, bool isOsuMode = false)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NetFix_osu_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        NoteMap? map = null;

        try
        {
            string audioFilename = "audio.mp3";
            string title = "Unknown", artist = "", version = "";
            var notes = new List<NoteEntry>();

            using (var archive = ZipFile.OpenRead(oszPath))
            {
                var osuEntry = archive.Entries.First(e =>
                    e.Name.Equals(osuFileName, StringComparison.OrdinalIgnoreCase));

                using (var reader = new StreamReader(osuEntry.Open()))
                {
                    string? line;
                    string section = "";
                    while ((line = reader.ReadLine()) is not null)
                    {
                        line = line.Trim();
                        if (line.StartsWith('[')) { section = line; continue; }

                        if (section == "[General]" && line.StartsWith("AudioFilename:"))
                            audioFilename = line["AudioFilename:".Length..].Trim();
                        else if (section == "[Metadata]")
                        {
                            if (line.StartsWith("Title:"))
                                title = line["Title:".Length..].Trim();
                            else if (line.StartsWith("Artist:"))
                                artist = line["Artist:".Length..].Trim();
                            else if (line.StartsWith("Version:"))
                                version = line["Version:".Length..].Trim();
                        }
                        else if (section == "[HitObjects]" &&
                                 line.Length > 0 && !line.StartsWith("//"))
                        {
                            var parts = line.Split(',');
                            if (parts.Length < 3) continue;
                            if (!int.TryParse(parts[0], out int x)) continue;
                            if (!int.TryParse(parts[2], out int timeMs)) continue;

                            int sourceLane = (int)Math.Floor((double)x * keyCount / 512.0);
                            sourceLane = Math.Clamp(sourceLane, 0, keyCount - 1);

                            int targetLane = keyCount > 1
                                ? (int)Math.Round((double)sourceLane * 3.0 / (keyCount - 1))
                                : 0;
                            targetLane = Math.Clamp(targetLane, 0, 3);

                            notes.Add(new NoteEntry { Time = timeMs / 1000.0, Lane = targetLane });
                        }
                    }
                }

                var audioEntry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals(audioFilename, StringComparison.OrdinalIgnoreCase));
                if (audioEntry is null)
                {
                    Directory.Delete(tempDir, true);
                    ShowNotification("Ошибка", $"Аудио '{audioFilename}' не найдено в архиве", isError: true);
                    return;
                }

                var audioExt = Path.GetExtension(audioFilename).ToLowerInvariant();
                var audioDestPath = Path.Combine(tempDir, "track" + audioExt);
                audioEntry.ExtractToFile(audioDestPath);

                if (audioExt == ".ogg")
                {
                    var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
                    System.Diagnostics.Debug.WriteLine($"[FFmpeg] looking at: '{ffmpegPath}' exists={File.Exists(ffmpegPath)}");
                    if (!File.Exists(ffmpegPath))
                    {
                        Directory.Delete(tempDir, true);
                        ShowNotification("FFmpeg не найден",
                            "Положи ffmpeg.exe рядом с NetFix.exe для поддержки .ogg аудио",
                            isError: true);
                        return;
                    }

                    var mp3DestPath = Path.Combine(tempDir, "track.mp3");
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = $"-i \"{audioDestPath}\" -q:a 2 \"{mp3DestPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };

                    using var proc = Process.Start(psi)!;
                    proc.WaitForExit(30_000);

                    if (!File.Exists(mp3DestPath) || new FileInfo(mp3DestPath).Length == 0)
                    {
                        Directory.Delete(tempDir, true);
                        ShowNotification("Ошибка конвертации",
                            "FFmpeg не смог конвертировать аудио", isError: true);
                        return;
                    }

                    File.Delete(audioDestPath);
                    audioDestPath = mp3DestPath;
                    audioExt = ".mp3";
                }

                var trackTitle = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
                if (!string.IsNullOrWhiteSpace(version))
                    trackTitle += $" [{version}]";
                trackTitle = string.Concat(trackTitle.Split(Path.GetInvalidFileNameChars()));

                map = new NoteMap
                {
                    Title = trackTitle,
                    Author = "osu!",
                    TrackFile = Path.GetFileName(audioDestPath),
                    Bpm = 160,
                    Notes = RemoveLaneDuplicates(notes.OrderBy(n => n.Time).ToList())
                };

                File.WriteAllText(Path.Combine(tempDir, "notes.json"),
                    JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));

                if (isOsuMode)
                    File.WriteAllText(Path.Combine(tempDir, "source.osz.path"), oszPath);

            } // using archive закрыт, файл разлочен

            // Проверка на слишком большое количество нот для osu! режима
            if (isOsuMode && map is not null && map.NoteCount > 1100)
            {
                var m = map;
                var tmp = tempDir;
                ShowHighNoteCountDialog(ok =>
                {
                    if (ok)
                        FinishLevelImport(m, tmp, isOsuMode);
                    else
                        try { Directory.Delete(tmp, true); } catch { }
                });
                return;
            }

            // Архив закрыт, можно безопасно вызывать FinishLevelImport
            FinishLevelImport(map, tempDir, isOsuMode);
        }
        catch (Exception ex)
        {
            if (Directory.Exists(tempDir))
                try { Directory.Delete(tempDir, true); } catch { }
            ShowNotification("Ошибка импорта", ex.Message, isError: true);
        }
    }

    private static List<NoteEntry> RemoveLaneDuplicates(List<NoteEntry> sorted)
    {
        const double minGap = 0.030;
        double[] lastTime = [-999, -999, -999, -999];
        var result = new List<NoteEntry>(sorted.Count);

        foreach (var note in sorted)
        {
            if (note.Time - lastTime[note.Lane] >= minGap)
            {
                result.Add(note);
                lastTime[note.Lane] = note.Time;
            }
        }
        return result;
    }

    private void FinishLevelImport(NoteMap map, string tempDir, bool isOsuMode = false)
    {
        System.Diagnostics.Debug.WriteLine($"[OSZ] FinishLevelImport called title='{map.Title}' tempDir='{tempDir}' isOsuMode={isOsuMode}");
        System.Diagnostics.Debug.WriteLine($"[OSZ] tempDirExists={Directory.Exists(tempDir)}");
        System.Diagnostics.Debug.WriteLine($"[OSZ] notes.json in tempDir exists={File.Exists(Path.Combine(tempDir, "notes.json"))}");

        var baseDir = isOsuMode ? OsuLevelsDir : LevelsDir;
        var targetDir = Path.Combine(baseDir, map.Title!);
        if (Directory.Exists(targetDir))
        {
            ShowConfirmDialog("Уровень уже существует",
                $"Уровень «{map.Title}» уже существует. Заменить его?",
                confirmed =>
                {
                    if (!confirmed) { Directory.Delete(tempDir, true); return; }
                    Directory.Delete(targetDir, true);
                    Directory.Move(tempDir, targetDir);
                    if (isOsuMode) LoadOsuLevelsList(); else LoadUserLevels();
                    ShowNotification("Успешно", $"Трек «{map.Title}» импортирован", isError: false);
                },
                confirmText: "Заменить",
            confirmIsDestructive: true);
        }
        else
        {
            if (!Directory.Exists(baseDir))
                Directory.CreateDirectory(baseDir);
            Directory.Move(tempDir, targetDir);
            if (isOsuMode) LoadOsuLevelsList(); else LoadUserLevels();
            ShowNotification("Успешно", $"Трек «{map.Title}» импортирован", isError: false);
        }
    }
    
    private void OsuModeBtn_Click(object s, RoutedEventArgs e)
    {
        ShowGameView(OsuModeView);
        CheckFfmpegStatus();
        LoadOsuLevelsList();
    }

    private void OsuModeBtn_Loaded(object s, RoutedEventArgs e)
    {
        if (s is not Button btn) return;
        if (btn.Template.FindName("GlowBorder", btn) is not Border border) return;

        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0.5),
            EndPoint = new System.Windows.Point(1, 0.5)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xe8, 0x4d, 0x8a), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xff, 0x9a, 0x5c), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xe8, 0x4d, 0x8a), 1.0));

        var transform = new RotateTransform(0);
        brush.RelativeTransform = transform;
        border.Background = brush;

        var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(4)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        transform.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private void OsuModeBackBtn_Click(object s, RoutedEventArgs e)
    {
        ShowGameView(GameTrackSelectView);
    }

    private void ImportOszBtn_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выбрать osu!mania карту",
            Filter = "osu! архив|*.osz",
            DefaultExt = ".osz"
        };
        if (dlg.ShowDialog() != true) return;
        StartOszImport(dlg.FileName, isOsuMode: true);
    }

    private void LoadOsuLevelsList()
    {
        if (!Directory.Exists(OsuLevelsDir))
        {
            OsuLevelsEmpty.Visibility = Visibility.Visible;
            OsuLevelsList.Visibility = Visibility.Collapsed;
            return;
        }

        var maps = new List<NoteMap>();
        foreach (var dir in Directory.GetDirectories(OsuLevelsDir))
        {
            var json = Path.Combine(dir, "notes.json");
            if (!File.Exists(json)) continue;
            try
            {
                var map = JsonSerializer.Deserialize<NoteMap>(File.ReadAllText(json));
                if (map != null)
                {
                    map.LevelDir = dir;
                    if (map.DateAdded == default)
                        map.DateAdded = Directory.GetCreationTime(dir);
                    var sourcePath = Path.Combine(dir, "source.osz.path");
                    if (File.Exists(sourcePath))
                        map.SourceOszPath = File.ReadAllText(sourcePath).Trim();
                    maps.Add(map);
                }
            }
            catch { }
        }

        if (maps.Count == 0)
        {
            OsuLevelsEmpty.Visibility = Visibility.Visible;
            OsuLevelsList.Visibility = Visibility.Collapsed;
            OsuLevelsList.ItemsSource = null;
            _osuTracksView = null;
        }
        else
        {
            OsuLevelsEmpty.Visibility = Visibility.Collapsed;
            OsuLevelsList.Visibility = Visibility.Visible;
            OsuLevelsList.ItemsSource = maps;
            _osuTracksView = CollectionViewSource.GetDefaultView(OsuLevelsList.ItemsSource);
            _osuTracksView.Filter = OsuTrackFilterPredicate;
            ApplyOsuSorting();
        }
    }

    private void OsuLevelCard_Click(object s, MouseButtonEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not NoteMap map) return;
        var mp3 = map.LevelDir != null
            ? Directory.GetFiles(map.LevelDir, "*.mp3").FirstOrDefault()
            : null;
        if (mp3 == null)
        {
            ShowNotification("Ошибка", "Аудио файл не найден", isError: true);
            return;
        }
        ShowGameView(GamePlayView);
        StartGame(map.Notes, mp3, map.Title ?? "Osu! трек", map.Bpm);
    }

    private void DeleteOsuLevel_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((s as Button)?.Tag is not NoteMap map) return;

        ShowConfirmDialog(
            "Удалить osu! уровень?",
            $"Уровень «{map.Title}» будет удалён безвозвратно.",
            confirmed =>
            {
                if (!confirmed) return;
                if (map.LevelDir != null && Directory.Exists(map.LevelDir))
                    Directory.Delete(map.LevelDir, recursive: true);
                LoadOsuLevelsList();
            });
    }

    private void ExportOsuLevel_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((s as Button)?.Tag is not NoteMap map) return;

        if (!string.IsNullOrEmpty(map.SourceOszPath) && File.Exists(map.SourceOszPath))
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = map.Title,
                DefaultExt = ".osz",
                Filter = "osu! архив|*.osz"
            };
            if (dlg.ShowDialog() != true) return;
            File.Copy(map.SourceOszPath, dlg.FileName!, overwrite: true);
            ShowNotification("Экспорт", $"Трек «{map.Title}» экспортирован", isError: false);
            return;
        }

        var dir = Path.Combine(OsuLevelsDir, map.Title ?? "level");
        var dlg2 = new Microsoft.Win32.SaveFileDialog
        {
            FileName = map.Title + "_export",
            DefaultExt = ".zip",
            Filter = "ZIP Archive|*.zip"
        };
        if (dlg2.ShowDialog() != true) return;
        ZipFile.CreateFromDirectory(dir, dlg2.FileName);
        ShowNotification("Экспорт", $"Трек «{map.Title}» экспортирован как ZIP", isError: false);
    }

    private async void DownloadFfmpegBtn_Click(object sender, RoutedEventArgs e)
    {
        DownloadFfmpegBtn.IsEnabled = false;
        FfmpegBtnText.Text = "Скачиваем...";

        try
        {
            var ffmpegDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NetFix", "ffmpeg");
            Directory.CreateDirectory(ffmpegDir);

            var zipPath = Path.Combine(ffmpegDir, "ffmpeg.zip");
            var ffmpegExe = Path.Combine(ffmpegDir, "ffmpeg.exe");

            const string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            FfmpegBtnText.Text = "Скачиваем... (это может занять минуту)";

            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(zipPath, bytes);

            FfmpegBtnText.Text = "Распаковываем...";
            await Task.Run(() =>
            {
                var extractDir = Path.Combine(ffmpegDir, "extracted");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                var found = Directory.GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null)
                    File.Copy(found, ffmpegExe, overwrite: true);

                Directory.Delete(extractDir, true);
                File.Delete(zipPath);
            });

            if (File.Exists(ffmpegExe))
            {
                _settings.FfmpegPath = ffmpegExe;
                SettingsService.Save(_settings);
                CheckFfmpegStatus();
                ShowNotification("ffmpeg", "ffmpeg успешно установлен!", isError: false);
            }
            else
            {
                throw new Exception("ffmpeg.exe не найден в архиве");
            }
        }
        catch (Exception ex)
        {
            FfmpegBtnText.Text = "Скачать ffmpeg";
            DownloadFfmpegBtn.IsEnabled = true;
            ShowNotification("Ошибка", $"Не удалось скачать ffmpeg: {ex.Message}", isError: true);
        }
    }

    private void CheckFfmpegStatus()
    {
        bool ok = !string.IsNullOrEmpty(_settings.FfmpegPath) && File.Exists(_settings.FfmpegPath);
        FfmpegOkBadge.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        DownloadFfmpegBtn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ManualFfmpegLink_Click(object sender, RoutedEventArgs e)
    {
        ManualInstallPanel.Visibility = ManualInstallPanel.Visibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CheckFfmpegStatus_Click(object sender, RoutedEventArgs e)
    {
        CheckFfmpegStatus();
        var btn = (Button)sender;
        var original = btn.Content.ToString();
        bool ok = !string.IsNullOrEmpty(_settings.FfmpegPath) && File.Exists(_settings.FfmpegPath);
        btn.Content = ok ? "✓ Файл найден!" : "✗ Файл не найден";
        Task.Delay(2000).ContinueWith(_ =>
            Dispatcher.Invoke(() => btn.Content = original));
    }

    private void ShowNotification(string title, string message, bool isError, bool isWarning = false)
    {
        Color accentColor = isError
            ? Color.FromRgb(0xef, 0x44, 0x44)
            : isWarning
                ? Color.FromRgb(0xf5, 0x9e, 0x0b)
                : Color.FromRgb(0x22, 0xc5, 0x5e);

        string iconPath = isError
            ? "M6,6 L18,18 M18,6 L6,18"
            : isWarning
                ? "M12,2 L22,20 L2,20 Z M12,9 L12,14 M12,16 L12,18"
                : "M4,12 L9,17 L20,6";

        // Создаём оверлей для затемнения фона
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        // Создаём карточку уведомления
        var notificationCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 480,
            Margin = new Thickness(40),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 0,
                Opacity = 0.5
            }
        };
        Grid.SetRowSpan(notificationCard, 3);

        var cardContent = new StackPanel
        {
            Margin = new Thickness(32, 28, 32, 28)
        };

        // Иконка (ошибка или успех)
        var iconBorder = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(accentColor) { Opacity = 0.15 },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };

        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(iconPath),
            Stroke = new SolidColorBrush(accentColor),
            StrokeThickness = 2.5,
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = icon;
        cardContent.Children.Add(iconBorder);

        // Заголовок
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        cardContent.Children.Add(titleText);

        // Описание
        var descText = new TextBlock
        {
            Text = message,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 24)
        };
        cardContent.Children.Add(descText);

        // Кнопка OK
        var okBtn = new Button
        {
            Content = "Понятно",
            Width = 140,
            Height = 40,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        // Шаблон для кнопки с закруглёнными углами
        var btnTemplate = new ControlTemplate(typeof(Button));
        var btnBorder = new FrameworkElementFactory(typeof(Border));
        btnBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        btnBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var btnPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        btnPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        btnPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        btnBorder.AppendChild(btnPresenter);
        btnTemplate.VisualTree = btnBorder;

        var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Button.OpacityProperty, 0.9));
        btnTemplate.Triggers.Add(hoverTrigger);

        okBtn.Template = btnTemplate;
        okBtn.Click += (_, _) =>
        {
            MainGrid.Children.Remove(overlay);
        };

        cardContent.Children.Add(okBtn);
        notificationCard.Child = cardContent;
        overlay.Child = notificationCard;
    }

    private void DeleteUserLevel_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true; // Предотвращаем всплытие к Border
        if ((s as Button)?.Tag is not NoteMap map) return;
        
        ShowConfirmDialog(
            "Удалить уровень?",
            $"Уровень «{map.Title}» будет удалён безвозвратно.",
            confirmed =>
            {
                if (!confirmed) return;
                var dir = System.IO.Path.Combine(LevelsDir, map.Title ?? "level");
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    LoadUserLevels();
                }
            });
    }
    
    private void ShowConfirmDialog(string title, string message, Action<bool> callback,
        string confirmText = "Удалить", bool confirmIsDestructive = true)
    {
        var confirmColor = confirmIsDestructive
            ? Color.FromRgb(0xef, 0x44, 0x44)
            : Color.FromRgb(0x22, 0xc5, 0x5e);
        var confirmHoverColor = confirmIsDestructive
            ? Color.FromRgb(0xdc, 0x26, 0x26)
            : Color.FromRgb(0x16, 0xa3, 0x4a);

        // Создаём оверлей
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        
        // Диалоговое окно
        var dialog = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            MaxWidth = 400,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        
        var stack = new StackPanel();
        
        // Заголовок
        var titleBlock = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        stack.Children.Add(titleBlock);
        
        // Сообщение
        var messageBlock = new TextBlock
        {
            Text = message,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20)
        };
        stack.Children.Add(messageBlock);
        
        // Кнопки
        var buttonPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        
        var cancelBtn = new Button
        {
            Content = "Отмена",
            Style = (Style)FindResource("OutlineBtn"),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) =>
        {
            ContentGrid.Children.Remove(overlay);
            callback(false);
        };
        
        var confirmBtn = new Button
        {
            Content = confirmText,
            Padding = new Thickness(16, 8, 16, 8),
            Background = new SolidColorBrush(confirmColor),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Cursor = Cursors.Hand
        };
        
        // Простой шаблон для кнопки
        var btnTemplate = new ControlTemplate(typeof(Button));
        var btnBorder = new FrameworkElementFactory(typeof(Border));
        btnBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        btnBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        
        var btnPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        btnPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        btnPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        btnBorder.AppendChild(btnPresenter);
        btnTemplate.VisualTree = btnBorder;
        
        var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, 
            new SolidColorBrush(confirmHoverColor)));
        btnTemplate.Triggers.Add(hoverTrigger);
        
        confirmBtn.Template = btnTemplate;
        confirmBtn.Click += (_, _) =>
        {
            ContentGrid.Children.Remove(overlay);
            callback(true);
        };
        
        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(confirmBtn);
        stack.Children.Add(buttonPanel);
        
        dialog.Child = stack;
        overlay.Child = dialog;
        
        ContentGrid.Children.Add(overlay);
        Grid.SetRowSpan(overlay, 10);
    }

    private void ShowHighNoteCountDialog(Action<bool> callback)
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

        var dialog = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            MaxWidth = 420,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "Слишком много нот!",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "В этом уровне 1100+ нот. Скорее всего, он не подходит для игры в NetFix из-за жёсткого спама кнопками. Всё равно добавить?",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20)
        });

        var buttonPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var yesBtn = new Button
        {
            Content = "Да",
            Style = (Style)FindResource("OutlineBtn"),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        yesBtn.Click += (_, _) =>
        {
            ContentGrid.Children.Remove(overlay);
            callback(true);
        };

        var noBtn = new Button
        {
            Content = "Нет",
            Padding = new Thickness(16, 8, 16, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Cursor = Cursors.Hand
        };

        var nt = new ControlTemplate(typeof(Button));
        var nb = new FrameworkElementFactory(typeof(Border));
        nb.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        nb.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        nb.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var np = new FrameworkElementFactory(typeof(ContentPresenter));
        np.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        np.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        nb.AppendChild(np);
        nt.VisualTree = nb;
        var ht = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        ht.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x16, 0xa3, 0x4a))));
        nt.Triggers.Add(ht);
        noBtn.Template = nt;

        noBtn.Click += (_, _) =>
        {
            ContentGrid.Children.Remove(overlay);
            callback(false);
        };

        buttonPanel.Children.Add(yesBtn);
        buttonPanel.Children.Add(noBtn);
        stack.Children.Add(buttonPanel);

        dialog.Child = stack;
        overlay.Child = dialog;

        ContentGrid.Children.Add(overlay);
        Grid.SetRowSpan(overlay, 10);
    }

    // ── Игра: редактор уровней ───────────────────────────────────────────────
    private void EditorBrowseTrack_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "MP3 файлы|*.mp3|Все файлы|*.*" };
        if (dlg.ShowDialog() != true) return;
        _editorMp3Path = dlg.FileName;
        EditorTrackPath.Text = System.IO.Path.GetFileName(_editorMp3Path);
    }

    private void BpmAnalyzerLink_Click(object s, RoutedEventArgs e)
    {
        OpenUrl("https://tunebat.com/Analyzer");
    }

    private bool TryGetEditorBpm(out double bpm)
    {
        var text = EditorBpmBox.Text.Trim().Replace(',', '.');
        if (double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out bpm) &&
            bpm >= 40 &&
            bpm <= 240)
        {
            return true;
        }

        bpm = REFERENCE_BPM;
        return false;
    }

    private async void EditorStartBtn_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_editorMp3Path) || !File.Exists(_editorMp3Path))
        {
            EditorStatusBox.Visibility = Visibility.Visible;
            EditorStatusText.Text = "Выбери MP3 файл.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditorTrackTitle.Text))
        {
            EditorStatusBox.Visibility = Visibility.Visible;
            EditorStatusText.Text = "Введи название трека.";
            return;
        }

        if (!TryGetEditorBpm(out _))
        {
            EditorStatusBox.Visibility = Visibility.Visible;
            EditorStatusText.Text = "Введите BPM числом от 40 до 240.";
            return;
        }

        EditorStartBtn.IsEnabled = false;
        EditorStatusBox.Visibility = Visibility.Visible;
        EditorCountdownBar.Visibility = Visibility.Visible;
        EditorResultBox.Visibility = Visibility.Collapsed;
        EditorRecordingBox.Visibility = Visibility.Collapsed;

        for (int i = 3; i > 0; i--)
        {
            EditorStatusText.Text = $"Запись начнётся через {i}...";
            EditorCountdownBar.Value = 3 - i + 1;
            await Task.Delay(1000);
        }

        EditorStatusBox.Visibility = Visibility.Collapsed;
        EditorRecordingBox.Visibility = Visibility.Visible;
        EditorNoteCount.Text = "0 нот записано";

        _recordedNotes = new();
        _editorRecording = true;
        _gameClock.Restart();

        _editorPlayer.Open(new Uri(_editorMp3Path));
        
        // Discord Rich Presence - обновление статуса редактора
        var editorStartTime = DateTime.Now;
        var trackTitle = EditorTrackTitle.Text.Trim();
        _discordEditorTimer?.Stop();
        _discordEditorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _discordEditorTimer.Tick += (_, _) => {
            _discord.SetLevelEditor(trackTitle, _recordedNotes.Count, editorStartTime);
        };
        _discordEditorTimer.Start();
        
        // Первое обновление сразу
        _discord.SetLevelEditor(trackTitle, 0, editorStartTime);
        
        _editorPlayer.Volume = Math.Pow(_settings.GameVolume, 3);
        _editorPlayer.Play();
        _editorPlayer.MediaEnded += EditorPlayer_Ended;

        EditorStopBtn.Visibility = Visibility.Visible;
        PreviewKeyDown += Editor_KeyDown;
    }

    private void Editor_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (!_editorRecording) return;
        int lane = GetGameLane(e.Key);
        if (lane < 0) return;
        e.Handled = true;

        _recordedNotes.Add(new NoteEntry
        {
            Time = _gameClock.Elapsed.TotalSeconds,
            Lane = lane,
        });
        
        // Обновляем счётчик
        EditorNoteCount.Text = $"{_recordedNotes.Count} нот записано";
        
        // Подсвечиваем клавишу
        Border? keyBorder = lane switch
        {
            0 => EditorKeyA,
            1 => EditorKeyS,
            2 => EditorKeyK,
            3 => EditorKeyL,
            _ => null
        };
        
        if (keyBorder != null)
        {
            var originalBg = keyBorder.Background;
            var flashColor = LaneColors[lane];
            keyBorder.Background = new SolidColorBrush(Color.FromArgb(80, flashColor.R, flashColor.G, flashColor.B));
            
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) =>
            {
                keyBorder.Background = originalBg;
                timer.Stop();
            };
            timer.Start();
        }
    }

    private int GetGameLane(Key key)
    {
        string keyStr = key.ToString();
        if (keyStr.StartsWith("D") && keyStr.Length == 2)
            keyStr = keyStr[1..]; // D1 -> 1
        
        if (keyStr.Equals(_settings.KeyLane0, StringComparison.OrdinalIgnoreCase)) return 0;
        if (keyStr.Equals(_settings.KeyLane1, StringComparison.OrdinalIgnoreCase)) return 1;
        if (keyStr.Equals(_settings.KeyLane2, StringComparison.OrdinalIgnoreCase)) return 2;
        if (keyStr.Equals(_settings.KeyLane3, StringComparison.OrdinalIgnoreCase)) return 3;

        // Стрелки как дублирующие
        return key switch
        {
            Key.Left => 0,
            Key.Down => 1,
            Key.Up => 2,
            Key.Right => 3,
            _ => -1
        };
    }

    private void EditorPlayer_Ended(object? s, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(StopEditorRecording));
    }

    private void AudioFaqToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        AudioFaqPanel.Visibility = AudioFaqPanel.Visibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenPowerShellCmd_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoExit -Command \"Enable-WindowsOptionalFeature -Online -FeatureName 'WindowsMediaPlayer'\"",
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    private void EditorStopBtn_Click(object s, RoutedEventArgs e) => StopEditorRecording();

    private void StopEditorRecording()
    {
        if (!_editorRecording && _editorPlayer.Source == null) return;

        // Останавливаем Discord таймер и возвращаем статус в главное меню
        _discordEditorTimer?.Stop();
        _discordEditorTimer = null;
        _discord.SetMainMenu();
        
        _editorRecording = false;
        _editorPlayer.Stop();
        _editorPlayer.MediaEnded -= EditorPlayer_Ended;
        PreviewKeyDown -= Editor_KeyDown;
        _gameClock.Stop();

        EditorStopBtn.Visibility = Visibility.Collapsed;
        EditorRecordingBox.Visibility = Visibility.Collapsed;

        if (_recordedNotes.Count == 0)
        {
            EditorStatusBox.Visibility = Visibility.Visible;
            EditorStatusText.Text = "Нот не записано. Попробуй ещё раз.";
            EditorStartBtn.IsEnabled = true;
            return;
        }

        EditorResultBox.Visibility = Visibility.Visible;
        EditorResultText.Text = $"Записано {_recordedNotes.Count} нот. Готово к сохранению!";
        EditorStartBtn.IsEnabled = true;
    }

    private void EditorSaveBtn_Click(object s, RoutedEventArgs e)
    {
        var title = EditorTrackTitle.Text.Trim();
        if (!TryGetEditorBpm(out var bpm))
        {
            EditorStatusBox.Visibility = Visibility.Visible;
            EditorStatusText.Text = "Введите BPM числом от 40 до 240.";
            return;
        }

        var dir = System.IO.Path.Combine(LevelsDir, title);
        Directory.CreateDirectory(dir);

        var destMp3 = System.IO.Path.Combine(dir, "track.mp3");
        File.Copy(_editorMp3Path!, destMp3, overwrite: true);

        var map = new NoteMap
        {
            Title = title,
            TrackFile = "track.mp3",
            Bpm = bpm,
            Notes = _recordedNotes,
        };

        File.WriteAllText(
            System.IO.Path.Combine(dir, "notes.json"),
            JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));

        EditorStatusText.Text = $"Уровень «{title}» сохранён!";
        LoadUserLevels();
    }

    private void EditorExportBtn_Click(object s, RoutedEventArgs e)
    {
        var title = EditorTrackTitle.Text.Trim();
        var dir = System.IO.Path.Combine(LevelsDir, title);
        if (!Directory.Exists(dir))
            EditorSaveBtn_Click(s, new RoutedEventArgs());

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = title + "_export",
            DefaultExt = ".zip",
            Filter = "ZIP Archive|*.zip"
        };
        if (dlg.ShowDialog() != true) return;
        ZipFile.CreateFromDirectory(dir, dlg.FileName);
    }

    private async void CheckForUpdatesBackgroundAsync()
    {
        try
        {
            var (hasUpdate, newVersion, downloadUrl, error) = await NetFix.Services.UpdateService.CheckAsync();
            if (hasUpdate && !string.IsNullOrEmpty(newVersion))
            {
                var window = new NetFix.Views.UpdateWindow();
                window.Owner = this;
                window.InitWithUpdate(newVersion, downloadUrl);
                window.ShowDialog();
            }
        }
        catch
        {
            // Игнорируем ошибки при фоновой проверке
        }
    }

    private void WizardCloseBtn_Click(object s, RoutedEventArgs e) => CloseWizard();

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    // ── Zapret Wizard ────────────────────────────────────────────────────────
    private void CloseWizard()
    {
        var slideAnim = new DoubleAnimation(0, 50, TimeSpan.FromMilliseconds(220)) 
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        slideAnim.Completed += (_, _) => WizardLayer.Visibility = Visibility.Collapsed;
        WizardTrans.BeginAnimation(TranslateTransform.XProperty, slideAnim);
        WizardLayer.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
    }

    private void ShowZapretWizard()
    {
        WizardLayer.Visibility = Visibility.Visible;
        var slideAnim = new DoubleAnimation(50, 0, TimeSpan.FromMilliseconds(220)) 
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
        WizardTrans.BeginAnimation(TranslateTransform.XProperty, slideAnim);
        WizardLayer.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

        // Проверяем наличие результатов тестирования
        var cache = ZapretConfigService.LoadCache();
        if (cache != null && cache.HasAnyConfigs)
        {
            // Случай 1: Есть результаты тестирования - показываем выбор конфига
            RenderWizardConfigSelection(cache);
        }
        else
        {
            // Случай 2: Нет результатов - предлагаем пройти тестирование
            RenderWizardNoConfigs();
        }
    }

    // Случай 1: Есть результаты тестирования - показываем выбор конфига
    private void RenderWizardConfigSelection(ZapretConfigCache cache)
    {
        WizardContent.Children.Clear();
        var title = FindChild<TextBlock>(WizardLayer);
        if (title != null) title.Text = "Мастер настройки Zapret";

        AddWizText("Выбери конфиг для запуска и нажми на кнопку «Применить»!");

        // Добавляем список конфигов (точно так же как в ZapretConfigWindow)
        var scrollViewer = new ScrollViewer
        {
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 20)
        };

        var configPanel = new StackPanel();
        
        var selectableConfigs = cache.GetSelectableConfigs();
        var usingPartialConfigs = cache.ValidConfigs.Count == 0 && cache.PartialConfigs.Count > 0;

        if (usingPartialConfigs)
            AddWizText("Идеальных конфигов не найдено. Ниже показаны частично рабочие варианты без ошибок и недоступных сервисов. Если что-то будет работать нестабильно, переключитесь на другой конфиг.");

        foreach (var config in selectableConfigs)
        {
            var isCurrent = config.Name == cache.CurrentConfig;

            var border = new Border
            {
                Background = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x1a, 0x25, 0x3a))
                    : new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c)),
                BorderBrush = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
                    : new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2a)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                Tag = config.Name
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            var nameText = new TextBlock
            {
                Text = config.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(config.IsValid ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0xea, 0xb3, 0x08)),
                VerticalAlignment = VerticalAlignment.Center
            };
            nameRow.Children.Add(nameText);

            if (isCurrent)
            {
                var activeBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x30, 0x4a)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "активный",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
                    }
                };
                nameRow.Children.Add(activeBadge);
            }

            var infoText = new TextBlock
            {
                Text = $"Пинг: {config.AveragePing} мс  •  Тесты: {config.SuccessCount}/12" + (config.IsPartiallyUsable ? "  •  частично" : ""),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58)),
                Margin = new Thickness(0, 4, 0, 0)
            };

            left.Children.Add(nameRow);
            left.Children.Add(infoText);

            // Стрелка справа
            var arrow = new TextBlock
            {
                Text = isCurrent ? "✓" : "→",
                FontSize = 14,
                Foreground = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
                    : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x36)),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(left, 0);
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(left);
            grid.Children.Add(arrow);

            border.Child = grid;

            // Клик - выбрать конфиг
            border.MouseLeftButtonDown += (s, e) =>
            {
                cache.CurrentConfig = config.Name;
                ZapretConfigService.SaveCache(cache);
                RenderWizardConfigSelection(cache); // Перерисовать для обновления активного
            };

            // Hover эффект
            border.MouseEnter += (s, e) =>
            {
                if (!isCurrent)
                    border.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x23));
            };
            border.MouseLeave += (s, e) =>
            {
                if (!isCurrent)
                    border.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c));
            };

            configPanel.Children.Add(border);
        }

        scrollViewer.Content = configPanel;
        WizardContent.Children.Add(scrollViewer);

        // Кнопка применить - использует ApplyConfigAsync как в ZapretConfigWindow
        AddWizBtn("Применить", "#22c55e", async () =>
        {
            if (!string.IsNullOrEmpty(cache.CurrentConfig))
            {
                // Показать прогресс-бар
                WizardApplyProgress.Visibility = Visibility.Visible;
                
                // Применяем конфиг (ApplyConfigAsync автоматически останавливает старый сервис и запускает новый)
                bool success = await ZapretConfigService.ApplyConfigAsync(_settings.ZapretPath, cache.CurrentConfig);
                
                // Скрыть прогресс-бар
                WizardApplyProgress.Visibility = Visibility.Collapsed;
                
                if (success)
                {
                    CloseWizard();
                    // Обновить статус через 1500мс
                    await Task.Delay(1500);
                    UpdateActiveApps();
                    
                    // Автоматически нажать кнопку "Починить интернет" ещё раз
                    // Используем FixBtn_Click чтобы снова прошла проверка TgWsProxy
                    await Task.Delay(500);
                    FixBtn_Click(null, null);
                }
                else
                {
                }
            }
        });

        AddWizBtn("Отмена", "#ef4444", CloseWizard);
    }

    // Случай 2: Нет результатов - предлагаем пройти тестирование
    private void RenderWizardNoConfigs()
    {
        WizardContent.Children.Clear();
        var title = FindChild<TextBlock>(WizardLayer);
        if (title != null) title.Text = "Мастер настройки Zapret";

        AddWizText("Приложение не обнаружило рабочие конфиги!\n\n" +
                   "Для работы приложения необходимо просканировать конфиги. " +
                   "Выделите примерно 10 минут, во время проверки вы можете поиграть " +
                   "в мини-игру или заняться своими делами.\n\n" +
                   "После сканирования у вас будет полный функционал приложения!");

        AddWizBtn("Пройти тестирование", "#22c55e", () =>
        {
            CloseWizard();
            ShowConfigWindow(testMode: true);
        });

        AddWizBtn("Отмена", "#ef4444", CloseWizard);

        // Разделитель
        WizardContent.Children.Add(new Border
        {
            Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(0, 0, 0, 10)
        });

        AddWizOutlineBtn("Импортировать конфиги", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Импортировать конфиги Zapret",
                Filter = "JSON файл (*.json)|*.json"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dlg.FileName);
                    var importedCache = System.Text.Json.JsonSerializer.Deserialize<ZapretConfigCache>(json);

                    if (importedCache == null || !importedCache.HasAnyConfigs)
                    {
                        ShowNotification("❌ Ошибка импорта",
                            "Файл не содержит валидных результатов тестирования",
                            "#ef4444");
                        return;
                    }

                    var cacheFile = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "NetFix", "zapret_configs.json");
                    var cacheDir = Path.GetDirectoryName(cacheFile);
                    if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                        Directory.CreateDirectory(cacheDir);

                    if (File.Exists(cacheFile))
                    {
                        var backupFile = cacheFile + $".backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
                        File.Copy(cacheFile, backupFile, true);
                    }

                    File.Copy(dlg.FileName, cacheFile, true);
                    ZapretConfigService.LoadCache(); // refresh
                    CloseWizard();
                    ShowNotification("✅ Конфиги импортированы",
                        $"Загружено {importedCache.ValidConfigs.Count} идеальных и " +
                        $"{importedCache.PartialConfigs.Count} частичных конфигов.\n" +
                        $"Теперь выберите конфиг на вкладке «Серверы».",
                        "#22c55e");
                }
                catch (Exception ex)
                {
                    ShowNotification("❌ Ошибка импорта",
                        $"Не удалось загрузить файл:\n{ex.Message}",
                        "#ef4444");
                }
            }
        });

        AddWizNote("Это кнопка для тех, у кого остался экспортированный файл конфигов Zapret, " +
                   "сделанный в этом приложении. Если вы запускаете приложение в первый раз, " +
                   "то нажимайте на кнопку «Пройти тестирование».");
    }

    private void RenderWizardStep(int step)
    {
        WizardContent.Children.Clear();
        // Устанавливаем заголовок - ищем TextBlock в WizardLayer
        var title = FindChild<TextBlock>(WizardLayer);
        if (title != null) title.Text = "Мастер настройки Zapret";

        switch (step) {
            case 0:
                AddWizText("Я запустил файл service.bat.\n\nУ тебя открылось окно консоли?");
                AddWizBtn("Да, открылось", "#22c55e", () => RenderWizardStep(2));
                AddWizBtn("Нет", "#ef4444", () => RenderWizardStep(1));
                break;
            case 1:
                AddWizText("Окно не открылось.\nВозможно, путь неверный или антивирус блокирует запуск.");
                AddWizBtn("Закрыть", "#3b82f6", CloseWizard);
                break;
            case 2:
                AddWizText("Ты запускаешь его в первый раз?");
                AddWizBtn("Да", "#3b82f6", () => RenderWizardStep(3));
                AddWizBtn("Нет", "#2e2e2e", () => RenderWizardStep(11), "#cccccc");
                break;
            case 3:
                AddWizText("Нажми цифру 2, а потом Enter.\n\nСделал?");
                AddWizBtn("Да, сделал", "#3b82f6", () => RenderWizardStep(4));
                break;
            case 4:
                AddWizText("Видишь 'Press any key to continue...'?\n\nНажимай Enter.");
                AddWizBtn("Сделал", "#3b82f6", () => RenderWizardStep(5));
                break;
            case 5:
                AddWizText("Напиши 11 и нажми Enter.\n\nОткрылось окно Blockcheck?");
                AddWizBtn("Да, открылось", "#22c55e", () => RenderWizardStep(7));
                AddWizBtn("Нет", "#ef4444", () => RenderWizardStep(6));
                break;
            case 6:
                AddWizText("Окно тестов не открылось. Попробуй запуск от админа.");
                AddWizBtn("Понятно", "#3b82f6", CloseWizard);
                break;
            case 7:
                AddWizText("В новом окне выбери:\n1, Standard tests\nНажми Enter.");
                AddWizBtn("Нажал", "#3b82f6", () => RenderWizardStep(8));
                break;
            case 8:
                AddWizText("Выбери:\n1, All configs\nЖди завершения теста!");
                AddWizBtn("Понял, жду", "#3b82f6", () => RenderWizardStep(9));
                break;
            case 9:
                AddWizText("Запомни цифру 'Best config' в самом конце.");
                AddWizBtn("Я запомнил!", "#22c55e", () => RenderWizardStep(10));
                break;
            case 10:
                AddWizText("Закрой все окна. Сейчас я снова запущу service.bat. Набери свою цифру и нажми Enter!");
                AddWizBtn("Готово!", "#3b82f6", () => {
                    var st = DiagnosticsEngine.CheckAppStatus();
                    if (!st.TgWsProxyRunning) StartTgWsProxyWithActivation(); 
                    CloseWizard(); 
                    RunAutoFix();
                });
                break;
            case 11:
                AddWizText("Рад, что ты уже знаешь как им пользоваться!\n\nВ открытом окне выбери:\n1, Install Service\nИ выбери свой рабочий конфиг.");
                AddWizBtn("Готово!", "#22c55e", () => {
                    var st = DiagnosticsEngine.CheckAppStatus();
                    if (!st.TgWsProxyRunning) StartTgWsProxyWithActivation();
                    CloseWizard(); 
                    RunAutoFix();
                });
                break;
        }
    }

    // ── Запуск TgWsProxy с автоматической активацией ──────────────────────────

    private async void StartTgWsProxyWithActivation()
    {
        try 
        { 
            if (!string.IsNullOrEmpty(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
            {
                Process.Start(new ProcessStartInfo(_settings.TgWsProxyPath) 
                { 
                    UseShellExecute = true, 
                    WorkingDirectory = System.IO.Path.GetDirectoryName(_settings.TgWsProxyPath) 
                });
                
                // Автоматически активируем прокси в Telegram
                await ActivateTgWsProxyAsync();
                
            }
        } 
        catch 
        {
        }
    }

    private void StartZapretProcess()
    {
        try 
        { 
            Process.Start(new ProcessStartInfo(_settings.ZapretPath) 
            { 
                UseShellExecute = true, 
                WorkingDirectory = System.IO.Path.GetDirectoryName(_settings.ZapretPath) 
            });
        } 
        catch 
        {
        }
    }

    private T FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
                return result;

            var childOfChild = FindChild<T>(child);
            if (childOfChild != null)
                return childOfChild;
        }

        return null;
    }

    private void AddWizText(string txt)
    {
        WizardContent.Children.Add(new TextBlock {
            Text = txt, FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        });
    }

    private void AddWizNote(string txt)
    {
        WizardContent.Children.Add(new TextBlock {
            Text = txt, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20)
        });
    }

    private void AddWizBtn(string txt, string hex, Action act, string fgHex = "#ffffff")
    {
        var bgBrush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        var c = bgBrush.Color;
        var hoverBrush = new SolidColorBrush(Color.FromRgb(
            (byte)(c.R > 30 ? c.R - 30 : 0),
            (byte)(c.G > 30 ? c.G - 30 : 0),
            (byte)(c.B > 30 ? c.B - 30 : 0)));

        var btn = new Button {
            Content = txt,
            Background = bgBrush,
            Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(fgHex)!,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Height = 40,
            Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 10),
            Template = CreateSimpleBtnTemplate()
        };
        btn.MouseEnter += (_, _) => btn.Background = hoverBrush;
        btn.MouseLeave += (_, _) => btn.Background = bgBrush;
        btn.Click += (_, _) => act();
        WizardContent.Children.Add(btn);
    }

    private void AddWizOutlineBtn(string txt, Action act)
    {
        var btn = new Button {
            Content = txt,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Height = 40,
            Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 10),
            Style = (Style)FindResource("OutlineBtn")
        };
        btn.Click += (_, _) => act();
        WizardContent.Children.Add(btn);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ONBOARDING
    // ══════════════════════════════════════════════════════════════════════════

    private void ShowOnboarding()
    {
        OnboardLayer.Visibility = Visibility.Visible;
        Opacity = 1;
        ShowOnboardScreen(0);
    }

    private void ShowOnboardScreen(int n)
    {
        var grid = new Grid { Background = Brushes.Transparent };

        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            MaxWidth            = 520,
            Margin              = new Thickness(32)
        };
        grid.Children.Add(stack);

        switch (n)
        {
            case 0: BuildOnboard0(stack); break;
            case 1: BuildOnboard1(stack); break;
            case 2: BuildOnboard2(stack); break;
            case 3: BuildOnboard3(stack); break;
            case 4: BuildOnboardZapretChoice(stack); break;
            case 5: BuildOnboardLetsDoIt(stack); break;
            case 6: BuildOnboardDownloadArchive(stack); break;
            case 7: BuildOnboardExtract(stack); break;
            case 8: BuildOnboardZapretSelectBat(stack); break;
            case 9: BuildOnboardZapretSuccess(stack); break;
            case 10: BuildOnboardTgWsChoice(stack); break;
            case 11: BuildOnboardTgWsDownload(stack); break;
            case 12: BuildOnboardTgWsMove(stack); break;
            case 13: BuildOnboardTgWsSelectExe(stack); break;
            case 15: BuildOnboardDone(stack); break;
            case 16: BuildOnboardAutoDownload(stack); break;
            case 17: BuildOnboardManualStart(stack); break;
        }

        OnboardContent.Content = grid;
        FadeInElement(grid);
    }

    private static void FadeInElement(UIElement el)
    {
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    private void BuildOnboard0(StackPanel p)
    {
        AddOnboardTitle(p, "Привет!");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(1));
    }

    private void BuildOnboard1(StackPanel p)
    {
        AddOnboardSub(p, "Это программа создана для людей, у которых есть проблемы с интернетом в России!\nЕсли у вас есть ВПН, то вам это, скорее всего, не понадобится.\nПриложение предлагает решение всех проблем, а также полную автоматизацию.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(2));
    }

    private void BuildOnboard2(StackPanel p)
    {
        AddOnboardSub(p, "Приложение НЕ СОБИРАЕТ ВАШИ ДАННЫЕ.\n\nКак разработчик заявляю: они мне абсолютно не нужны. Если вы всё же беспокоитесь о конфиденциальности и безопасности, напоминаю, что исходный код проекта полностью открыт и доступен на GitHub - вы всегда можете проверить его лично.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(3));
    }

    private void BuildOnboard3(StackPanel p)
    {
        AddOnboardTitle(p, "Способ установки");
        AddOnboardSub(p, "Как вы хотите установить компоненты для обхода блокировок?\nПрограмма может сделать всё автоматически примерно за 15 секунд.");
        AddOnboardBtn(p, "Автоматическая установка (15 сек)", "#22c55e", () => ShowOnboardScreen(16));
        AddOnboardBtn(p, "Ручная установка", "#2e2e2e", () => ShowOnboardScreen(17), foreground: "#888888");
    }

    private void BuildOnboardZapretChoice(StackPanel p)
    {
        AddOnboardTitle(p, "У вас установлен zapret-discord-youtube?");
        AddOnboardBtn(p, "Да, выбрать файл", "#22c55e", () =>
        {
            var dlg = new OpenFileDialog { Title = "Выберите service.bat", Filter = "service.bat|service.bat|Все файлы|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _settings.ZapretPath = dlg.FileName;
                SettingsService.Save(_settings);
                ZapretBox.Text = dlg.FileName;
                ShowOnboardScreen(9);
            }
        });
        AddOnboardBtn(p, "Нет, давай скачаю", "#2e2e2e", () => ShowOnboardScreen(5), foreground: "#888888");
    }

    private void BuildOnboardLetsDoIt(StackPanel p)
    {
        AddOnboardTitle(p, "Давай всё сделаем");
        AddOnboardSub(p, "Давай всё настроим за пару минут.\nНажми кнопку ниже, чтобы получить нужные компоненты.");
        AddOnboardBtn(p, "Скачать Zapret", "#3b82f6", () => 
        {
            OpenUrl("https://github.com/Flowseal/zapret-discord-youtube/releases/latest");
            ShowOnboardScreen(6);
        });
    }

    private void BuildOnboardDownloadArchive(StackPanel p)
    {
        AddOnboardSub(p, "Опустите на сайте ниже и найдите вкладку с файлами называется Assets.\nТам есть несколько файлов zapret-discord-youtube-1.9.7b.rar или .zip.\nКачай какую хочешь.");
        AddOnboardBtn(p, "Я скачал архив", "#3b82f6", () => ShowOnboardScreen(7));
    }

    private void BuildOnboardExtract(StackPanel p)
    {
        try 
        {
            Directory.CreateDirectory(@"C:\Zapret");
            Process.Start("explorer.exe", @"C:\Zapret");
        }
        catch (Exception)
        {
            // Ignored if permissions are strictly denied without admin
        }

        AddOnboardSub(p, "Я открыл тебе папку C:\\Zapret.\nОткрой Архив который ты скачал, нажми CTRL + A, чтобы выделить всё,\nи перекинь все файлы в эту папку.");
        AddOnboardBtn(p, "Я перекинул файлы", "#3b82f6", () => ShowOnboardScreen(8));
    }

    private void BuildOnboardZapretSelectBat(StackPanel p)
    {
        AddOnboardTitle(p, "Выбор service.bat");
        AddOnboardSub(p, "Теперь выбери файл service.bat в папке, куда ты только что перекинул файлы.");
        AddOnboardBtn(p, "Выбрать service.bat", "#22c55e", () => 
        {
            var dlg = new OpenFileDialog { Title = "Выберите service.bat", Filter = "service.bat|service.bat|Все файлы|*.*", InitialDirectory = @"C:\Zapret" };
            if (dlg.ShowDialog() == true)
            {
                _settings.ZapretPath = dlg.FileName;
                SettingsService.Save(_settings);
                ZapretBox.Text = dlg.FileName;
                ShowOnboardScreen(9);
            }
        });
    }

    private void BuildOnboardZapretSuccess(StackPanel p)
    {
        // Добавляем иконку лайка
        var likeIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,11.47A18.74,18.74,0,0,0,10.69,8.9a18.74,18.74,0,0,0,1.76-2.42A6.42,6.42,0,0,0,13,5.41l1.74-4.57a4.45,4.45,0,0,1,2.83,2A4,4,0,0,1,18,4.77a2.67,2.67,0,0,1-.09.55L16.72,9.05h5.22a2,2,0,0,1,2,1.85,19.32,19.32,0,0,1-.32,5.44,33.83,33.83,0,0,1-1.23,4.34,3.78,3.78,0,0,1-3.58,2.49,25.54,25.54,0,0,1-6.28-.66A45.85,45.85,0,0,1,8,21.26V11.47Z M5,9H1a1,1,0,0,0-1,1V22a1,1,0,0,0,1,1H5a1,1,0,0,0,1-1V10A1,1,0,0,0,5,9ZM3,21a1,1,0,1,1,1-1A1,1,0,0,1,3,21Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };
        p.Children.Add(likeIcon);
        
        AddOnboardTitle(p, "Ты молодец!");
        AddOnboardSub(p, "Надеюсь, ты сделал всё правильно.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(10));
    }

    private void BuildOnboardTgWsChoice(StackPanel p)
    {
        AddOnboardTitle(p, "У вас установлен tg-ws-proxy?");
        AddOnboardBtn(p, "Да, выбрать файл", "#22c55e", () =>
        {
            var dlg = new OpenFileDialog { Title = "Выберите файл TgWsProxy.exe", Filter = "TgWsProxy.exe|*TgWsProxy*.exe|Исполняемые файлы (*.exe)|*.exe|Все файлы|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _settings.TgWsProxyPath = dlg.FileName;
                SettingsService.Save(_settings);
                TgWsBox.Text = dlg.FileName;
                ShowOnboardScreen(15);
            }
        });
        AddOnboardBtn(p, "Нет, давай скачаю", "#2e2e2e", () => ShowOnboardScreen(11), foreground: "#888888");
    }

    private void BuildOnboardTgWsDownload(StackPanel p)
    {
        AddOnboardSub(p, "Опустите на сайте ниже и найдите вкладку с файлами называется Assets.\nТам нужно скачать не архив, а сам файл TgWsProxy.exe.");
        AddOnboardBtn(p, "Скачать TgWsProxy.exe", "#3b82f6", () => 
        {
            OpenUrl("https://github.com/Flowseal/tg-ws-proxy/releases/latest");
            ShowOnboardScreen(12);
        });
    }

    private void BuildOnboardTgWsMove(StackPanel p)
    {
        try { Process.Start("explorer.exe", @"C:\Zapret"); } catch {}

        AddOnboardSub(p, "Я снова открыл тебе папку C:\\Zapret.\nТеперь перекинь скачанный файл TgWsProxy.exe в эту папку.");
        AddOnboardBtn(p, "Я перекинул", "#3b82f6", () => ShowOnboardScreen(13));
    }

    private void BuildOnboardTgWsSelectExe(StackPanel p)
    {
        AddOnboardTitle(p, "Выбор TgWsProxy.exe");
        AddOnboardSub(p, "Теперь выбери файл TgWsProxy.exe, который ты только что перекинул в папку.");
        AddOnboardBtn(p, "Выбрать TgWsProxy.exe", "#22c55e", () => 
        {
            var dlg = new OpenFileDialog { Title = "Выберите TgWsProxy.exe", Filter = "TgWsProxy.exe|*TgWsProxy*.exe|Исполняемые файлы (*.exe)|*.exe|Все файлы|*.*", InitialDirectory = @"C:\Zapret" };
            if (dlg.ShowDialog() == true)
            {
                _settings.TgWsProxyPath = dlg.FileName;
                SettingsService.Save(_settings);
                TgWsBox.Text = dlg.FileName;
                ShowOnboardScreen(15);
            }
        });
    }

    private void BuildOnboardDone(StackPanel p)
    {
        // Добавляем иконку лайка
        var likeIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,11.47A18.74,18.74,0,0,0,10.69,8.9a18.74,18.74,0,0,0,1.76-2.42A6.42,6.42,0,0,0,13,5.41l1.74-4.57a4.45,4.45,0,0,1,2.83,2A4,4,0,0,1,18,4.77a2.67,2.67,0,0,1-.09.55L16.72,9.05h5.22a2,2,0,0,1,2,1.85,19.32,19.32,0,0,1-.32,5.44,33.83,33.83,0,0,1-1.23,4.34,3.78,3.78,0,0,1-3.58,2.49,25.54,25.54,0,0,1-6.28-.66A45.85,45.85,0,0,1,8,21.26V11.47Z M5,9H1a1,1,0,0,0-1,1V22a1,1,0,0,0,1,1H5a1,1,0,0,0,1-1V10A1,1,0,0,0,5,9ZM3,21a1,1,0,1,1,1-1A1,1,0,0,1,3,21Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };
        p.Children.Add(likeIcon);
        
        AddOnboardTitle(p, "Всё готово!");
        
        var subText = new TextBlock();
        subText.FontFamily = new FontFamily("Segoe UI");
        subText.FontSize = 15;
        subText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        subText.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        subText.TextAlignment = TextAlignment.Center;
        subText.TextWrapping = TextWrapping.Wrap;
        subText.Margin = new Thickness(0, 0, 0, 24);
        
        subText.Inlines.Add(new System.Windows.Documents.Run("Пути сохранены. Можно запускать.\n\nНастройки можно изменить в любое время через "));
        
        var pathIcon = new System.Windows.Shapes.Path();
        pathIcon.Width = 14;
        pathIcon.Height = 14;
        pathIcon.Stretch = System.Windows.Media.Stretch.Uniform;
        pathIcon.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        pathIcon.Data = Geometry.Parse("M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.06-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61 l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41 h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.73,8.87 C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.06,0.94l-2.03,1.58 c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54 c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.43-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96 c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.49-0.12-0.61L19.14,12.94z M12,15.6c-1.98,0-3.6-1.62-3.6-3.6 s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z");
        pathIcon.Margin = new Thickness(2, 0, 0, -2);
        
        var inlineIcon = new System.Windows.Documents.InlineUIContainer(pathIcon);
        inlineIcon.BaselineAlignment = System.Windows.BaselineAlignment.Center;
        
        subText.Inlines.Add(inlineIcon);
        p.Children.Add(subText);

        AddOnboardBtn(p, "Открыть приложение →", "#3b82f6", () =>
        {
            SettingsService.MarkOnboarded();
            OnboardLayer.Visibility = Visibility.Collapsed;
            CheckInternetOnStart();
            StartActiveAppsMonitor();
        });
    }

    private void BuildOnboardManualStart(StackPanel p)
    {
        AddOnboardSub(p, "Для работы приложения вам нужно скачать следующие компоненты:");
        AddOnboardBtn(p, "Погнали", "#3b82f6", () => ShowOnboardScreen(4));
    }

    private void BuildOnboardAutoDownload(StackPanel p)
    {
        AddOnboardTitle(p, "Автоматическая установка");
        AddOnboardSub(p, "Подождите, скачиваем и настраиваем нужные компоненты.\nЭто займет не больше минуты.");
        
        var logCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 5, 0, 15),
            Padding = new Thickness(2)
        };
        
        var logBox = new System.Windows.Controls.RichTextBox
        {
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Height = 160,
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        logBox.Document.PagePadding = new Thickness(12);
        logCard.Child = logBox;
        p.Children.Add(logCard);

        var progBar = new System.Windows.Controls.ProgressBar
        {
            Value = 0,
            Maximum = 100,
            Height = 6,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Background = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e)),
            BorderThickness = new Thickness(0)
        };
        progBar.SetResourceReference(FrameworkElement.StyleProperty, typeof(System.Windows.Controls.ProgressBar));
        p.Children.Add(progBar);

        var progText = new TextBlock 
        { 
            Text = "Подготовка...", 
            Foreground = Brushes.White, 
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 20)
        };
        p.Children.Add(progText);

        void AppendLog(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                para.Inlines.Add(new System.Windows.Documents.Run(msg));
                logBox.Document.Blocks.Add(para);
                logBox.ScrollToEnd();
            });
        }

        var actionsPanel = new StackPanel();
        p.Children.Add(actionsPanel);

        Task.Run(async () => 
        {
            bool success = await AutoDownloadService.AutoInstallAllAsync(
                msg => AppendLog(msg),
                prog => Dispatcher.Invoke(() => 
                {
                    progBar.Value = prog * 100;
                    progText.Text = $"Загрузка... {(int)(prog * 100)}%";
                }),
                err => AppendLog("ОШИБКА: " + err)
            );

            Dispatcher.Invoke(() => 
            {
                if (success)
                {
                    // Перезагружаем настройки после успешной установки
                    _settings = SettingsService.Load();
                    LoadSettingsToPanel();
                    
                    progBar.Value = 100;
                    progBar.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    progText.Text = "Всё готово!";
                    progText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    
                    AddOnboardBtn(actionsPanel, "Далее", "#3b82f6", () => ShowOnboardScreen(15));
                }
                else
                {
                    progBar.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    progText.Text = "Ошибка установки";
                    progText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    
                    AddOnboardBtn(actionsPanel, "Попробовать вручную", "#ef4444", () => ShowOnboardScreen(17));
                }
            });
        });
    }

    // ── Onboard helpers ──────────────────────────────────────────────────────
    private static void AddOnboardEmoji(StackPanel p, string emoji) =>
        p.Children.Add(new TextBlock
        {
            Text = emoji, FontSize = 54, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Segoe UI Emoji"),
            Margin = new Thickness(0, 0, 0, 12)
        });

    private static void AddOnboardTitle(StackPanel p, string text) =>
        p.Children.Add(new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 22,
            FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        });

    private static void AddOnboardSub(StackPanel p, string text) =>
        p.Children.Add(new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });

    private void AddOnboardBtn(StackPanel p, string text, string bgHex, Action action,
        string foreground = "#ffffff")
    {
        var bgBrush = (SolidColorBrush)new BrushConverter().ConvertFrom(bgHex)!;
        var c = bgBrush.Color;
        // Пропорциональное затемнение: умножаем на 0.75, но не ниже тёмных цветов
        var hoverBrush = new SolidColorBrush(Color.FromRgb(
            (byte)(c.R * 0.75),
            (byte)(c.G * 0.75),
            (byte)(c.B * 0.75)));

        var btn = new Button
        {
            Content             = text,
            Background          = bgBrush,
            Foreground          = (SolidColorBrush)new BrushConverter().ConvertFrom(foreground)!,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 14,
            Height              = 44,
            Cursor              = Cursors.Hand,
            BorderThickness     = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin              = new Thickness(0, 0, 0, 10),
        };

        btn.Template = CreateSimpleBtnTemplate();
        btn.MouseEnter += (_, _) => btn.Background = hoverBrush;
        btn.MouseLeave += (_, _) => btn.Background = bgBrush;
        btn.Click += (_, _) => action();
        p.Children.Add(btn);
    }

    private static ControlTemplate CreateSimpleBtnTemplate()
    {
        var tmpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tmpl.VisualTree = bd;
        return tmpl;
    }

    private void AddOnboardLink(StackPanel p, string text, string url)
    {
        var btn = new Button
        {
            Content             = text,
            Background          = Brushes.Transparent,
            Foreground          = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 12,
            BorderThickness     = new Thickness(0),
            Cursor              = Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin              = new Thickness(0, 4, 0, 0),
        };
        btn.Template = CreateTransparentBtnTemplate();
        btn.Click   += (_, _) => OpenUrl(url);
        p.Children.Add(btn);
    }

    private static ControlTemplate CreateTransparentBtnTemplate()
    {
        var tmpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tmpl.VisualTree = bd;
        return tmpl;
    }

    private void SetAutostart(bool enable)
    {
        try
        {
            if (enable)
            {
                string path = Environment.ProcessPath;
                string args = $"/Create /F /RL HIGHEST /SC ONLOGON /TN \"NetFix\" /TR \"\\\"{path}\\\"\"";
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit();
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/Delete /F /TN \"NetFix\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            regKey?.DeleteValue("NetFix", false);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Ошибка автозапуска: {ex.Message}");
        }
    }

    // ── Helper: Create button content with icon ────────────────────────────────────
    private static object CreateButtonContentWithIcon(string iconKey, string text, Brush iconBrush)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        
        var geometry = System.Windows.Application.Current.TryFindResource(iconKey) as PathGeometry;
        
        // Fallback если ресурс не найден
        if (geometry == null && iconKey == "RefreshIcon")
        {
            geometry = Geometry.Parse("M21,11c-0.6,0-1,0.4-1,1c0,2.9-1.5,5.5-4,6.9c-3.8,2.2-8.7,0.9-10.9-2.9C2.9,12.2,4.2,7.3,8,5.1c3.3-1.9,7.3-1.2,9.8,1.4h-2.4c-0.6,0-1,0.4-1,1s0.4,1,1,1h4.5c0.6,0,1-0.4,1-1V3c0-0.6-0.4-1-1-1s-1,0.4-1,1v1.8C17,3,14.6,2,12,2C6.5,2,2,6.5,2,12s4.5,10,10,10c5.5,0,10-4.5,10-10C22,11.4,21.6,11,21,11z") as PathGeometry;
        }
        
        if (geometry == null)
        {
            // Если всё равно null, просто вернём текст без иконки
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        }
        
        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        if (iconKey == "PlayIcon" || iconKey == "RefreshIcon" || iconKey == "BoltIcon")
            path.Fill = iconBrush;
        else
            path.Stroke = iconBrush;

        if (iconKey != "PlayIcon" && iconKey != "RefreshIcon" && iconKey != "BoltIcon")
            path.StrokeThickness = 2;

        stack.Children.Add(path);
        stack.Children.Add(new TextBlock 
        { 
            Text = text, 
            VerticalAlignment = VerticalAlignment.Center 
        });

        return stack;
    }

    // ── Network Monitor Methods ──────────────────────────────────────────────
    private void InitNetworkMonitor()
    {
        var (rx, tx) = GetNetworkBytes();
        _lastBytesReceived = rx;
        _lastBytesSent = tx;

        DownloadLbl.Text = "—";
        UploadLbl.Text   = "—";
        PingLbl.Text     = "—";

        // Текущий трафик каждую секунду
        _netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _netTimer.Tick += NetTimer_Tick;
        _netTimer.Start();

        // Пинг каждые 5 секунд
        _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pingTimer.Tick += async (s, e) => await UpdatePingAsync();
        _pingTimer.Start();

        this.StateChanged += (s, e) =>
        {
            if (this.WindowState == WindowState.Minimized)
            { _monitorTimer?.Stop(); _netTimer?.Stop(); _pingTimer?.Stop(); }
            else
            { _monitorTimer?.Start(); _netTimer?.Start(); _pingTimer?.Start(); }
        };

        // Запускаем тест и пинг сразу
        Task.Run(async () => await RunSpeedTestAsync());
        Task.Run(async () => await UpdatePingAsync());
    }

    private async Task RunSpeedTestAsync()
    {
        _dlSamples.Clear();
        _ulSamples.Clear();
        
        Dispatcher.Invoke(() =>
        {
            DownloadLbl.Text = "—";
            UploadLbl.Text   = "—";
        });

        // ── ШАГ 1: DOWNLOAD ──────────────────────────────────────────────
        try
        {
            var urls = Enumerable.Repeat("https://speedtest.selectel.ru/100MB", 4).ToArray();
            long totalDlBytes = 0;
            var dlSw = System.Diagnostics.Stopwatch.StartNew();
            var dlCancel = new CancellationTokenSource(TimeSpan.FromSeconds(14));

            // Таймер мгновенных сэмплов каждую секунду
            long prevBytes = 0;
            var sampleTimer = new System.Timers.Timer(1000);
            sampleTimer.Elapsed += (s, e) =>
            {
                long now = Interlocked.Read(ref totalDlBytes);
                double instantMbps = (now - prevBytes) * 8.0 / 1_000_000.0;
                prevBytes = now;
                if (instantMbps > 0.1) // отбрасываем нулевые сэмплы прогрева
                {
                    lock (_dlSamples) _dlSamples.Add(instantMbps);
                    double speed = CalcFinalSpeed(_dlSamples);
                    Dispatcher.Invoke(() => DownloadLbl.Text = $"{speed:0.0}");
                }
            };
            sampleTimer.Start();

            var dlTasks = urls.Select(async url =>
            {
                try
                {
                    using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(16) };
                    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                    using var resp = await c.GetAsync(
                        url + "?nocache=" + Guid.NewGuid(),
                        HttpCompletionOption.ResponseHeadersRead,
                        dlCancel.Token);
                    using var stream = await resp.Content.ReadAsStreamAsync(dlCancel.Token);
                    var buf = new byte[131072];
                    int read;
                    while ((read = await stream.ReadAsync(buf, dlCancel.Token)) > 0)
                        Interlocked.Add(ref totalDlBytes, read);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.WriteLine($"[DL] {ex.Message}"); }
            }).ToArray();

            await Task.WhenAll(dlTasks);
            sampleTimer.Stop();
            sampleTimer.Dispose();

            _finalDownloadMbps = CalcFinalSpeed(_dlSamples);
            Dispatcher.Invoke(() => DownloadLbl.Text = _finalDownloadMbps > 0
                ? $"{_finalDownloadMbps:0.0}" : "—");
        }
        catch (Exception ex) { Console.WriteLine($"[DL FATAL] {ex.Message}"); }

        // ── ШАГ 2: UPLOAD ─────────────────────────────────────────────────
        try
        {
            long totalUlBytes = 0;
            var ulSw = System.Diagnostics.Stopwatch.StartNew();
            var ulCancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            long prevUlBytes = 0;
            var ulSampleTimer = new System.Timers.Timer(1000);
            ulSampleTimer.Elapsed += (s, e) =>
            {
                long now = Interlocked.Read(ref totalUlBytes);
                double instantMbps = (now - prevUlBytes) * 8.0 / 1_000_000.0;
                prevUlBytes = now;
                if (instantMbps > 0.1)
                {
                    lock (_ulSamples) _ulSamples.Add(instantMbps);
                    double speed = CalcFinalSpeed(_ulSamples);
                    Dispatcher.Invoke(() => UploadLbl.Text = $"{speed:0.0}");
                }
            };
            ulSampleTimer.Start();

            var ulTasks = Enumerable.Range(0, 4).Select(async _ =>
            {
                try
                {
                    using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                    var data = new byte[20 * 1024 * 1024];
                    Random.Shared.NextBytes(data);
                    while (!ulCancel.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var content = new ByteArrayContent(data);
                            await c.PostAsync("https://httpbin.org/post", content, ulCancel.Token);
                            Interlocked.Add(ref totalUlBytes, data.Length);
                        }
                        catch (OperationCanceledException) { break; }
                        catch { break; }
                    }
                }
                catch { }
            }).ToArray();

            await Task.WhenAll(ulTasks);
            ulSampleTimer.Stop();
            ulSampleTimer.Dispose();

            _finalUploadMbps = CalcFinalSpeed(_ulSamples);
        }
        catch (Exception ex) { Console.WriteLine($"[UL FATAL] {ex.Message}"); }

        _speedTestDone = true;
        Dispatcher.Invoke(() =>
        {
            DownloadLbl.Text = _finalDownloadMbps > 0 ? $"{_finalDownloadMbps:0.0}" : "—";
            UploadLbl.Text   = _finalUploadMbps > 0   ? $"{_finalUploadMbps:0.0}"   : "—";
        });
    }

    private async void NetTimer_Tick(object? sender, EventArgs e)
    {
        // Переносим тяжёлый системный вызов в фоновый поток
        var (rx, tx) = await Task.Run(GetNetworkBytes);
        double dlNow = Math.Max(0, rx - _lastBytesReceived);
        double ulNow = Math.Max(0, tx - _lastBytesSent);
        _lastBytesReceived = rx;
        _lastBytesSent = tx;
 
        // Пока тест не закончен, показываем анимацию, после, результат теста
        if (_speedTestDone)
        {
            DownloadLbl.Text = $"{_finalDownloadMbps:0.0}";
            UploadLbl.Text   = $"{_finalUploadMbps:0.0}";
        }
    }

    private static (long rx, long tx) GetNetworkBytes()
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Только физические интерфейсы, без loopback и виртуальных
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            if (ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
            if (ni.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)) continue;

            var stats = ni.GetIPv4Statistics();
            rx += stats.BytesReceived;
            tx += stats.BytesSent;
        }
        return (rx, tx);
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        double mbps = (bytesPerSec * 8.0) / 1_000_000.0;
        return mbps >= 1 ? $"{mbps:0.0} Мбит/с" : $"{mbps * 1000:0} Кбит/с";
    }

    private async Task UpdatePingAsync()
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            long total = 0;
            int count = 0;
            
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync("1.1.1.1", 2000);
                    Console.WriteLine($"[Ping] attempt {i}: {reply.Status} {reply.RoundtripTime}ms");
                    if (reply.Status == IPStatus.Success)
                    {
                        total += reply.RoundtripTime;
                        count++;
                    }
                    await Task.Delay(200);
                }
                catch (Exception ex) { Console.WriteLine($"[Ping] error: {ex.Message}"); }
            }

            Dispatcher.Invoke(() =>
            {
                if (count == 0)
                {
                    PingLbl.Text = "—";
                    PingLbl.Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0));
                    return;
                }
                
                long avg = total / count;
                PingLbl.Text = avg.ToString();
                _discord.UpdatePing((int)avg);
                
                bool good = avg < 100;
                
                // Цвет цифры
                PingLbl.Foreground = new SolidColorBrush(good
                    ? Color.FromRgb(0xf0, 0xf0, 0xf0)   // белый, хороший
                    : Color.FromRgb(0xf5, 0x9e, 0x0b)); // жёлтый, высокий
            });
        }
        catch (Exception ex) { Console.WriteLine($"[Ping] FATAL: {ex.Message}"); }
    }

    // Расчёт финальной скорости с компенсацией прогрева TCP
    private static double CalcFinalSpeed(List<double> samples)
    {
        if (samples.Count == 0) return 0;
        
        // Отбрасываем первые 2 сэмпла, TCP ещё разгоняется
        var stable = samples.Count > 2 ? samples.Skip(2).ToList() : samples;
        if (stable.Count == 0) return 0;
        
        // Берём топ-20% самых высоких значений и считаем их среднее.
        // Это убирает случайные пики вверх, но держится у реального максимума канала.
        var sorted = stable.OrderByDescending(x => x).ToList();
        int takeCount = Math.Max(1, (int)(sorted.Count * 0.2));
        
        return sorted.Take(takeCount).Average();
    }

    // Кнопка повтора сканирования
    private async void RescanBtn_Click(object sender, RoutedEventArgs e)
    {
        RescanBtn.IsEnabled = false;

        // Анимация вращения иконки пока идёт скан
        var rotateAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        var transform = new RotateTransform();
        RescanBtn.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        RescanBtn.RenderTransform = transform;
        transform.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);

        PingLbl.Text = "…";
        DownloadLbl.Text = "…";
        UploadLbl.Text = "…";

        await Task.Run(async () => await RunSpeedTestAsync());
        await Task.Run(async () => await UpdatePingAsync());

        // Останавливаем вращение
        transform.BeginAnimation(RotateTransform.AngleProperty, null);
        RescanBtn.RenderTransform = null;
        RescanBtn.IsEnabled = true;
    }

    private int _listeningLane = -1; // какой лейн ждёт нажатия

    private void GameSettingsMenuBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        GameMenuView.Visibility = Visibility.Collapsed;
        GameSettingsView.Visibility = Visibility.Visible;
        LoadKeyLabels();
        // Инициализируем чекбокс комбо-эффекта без срабатывания событий
        var wasLoaded = _settingsLoaded;
        _settingsLoaded = false;
        ComboEffectCB.IsChecked = _settings.DisableComboEffect;
        _settingsLoaded = wasLoaded;
    }

    private void GameSettingsMenuBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => ((Border)sender).Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));

    private void GameSettingsMenuBtn_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => ((Border)sender).Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));

    private void LoadKeyLabels()
    {
        KeyLabelLane0.Text = _settings.KeyLane0;
        KeyLabelLane1.Text = _settings.KeyLane1;
        KeyLabelLane2.Text = _settings.KeyLane2;
        KeyLabelLane3.Text = _settings.KeyLane3;
        UpdateKeyHints();
    }

    private void UpdateKeyHints()
    {
        KeyHint0.Text = $"{_settings.KeyLane0} / ←";
        KeyHint1.Text = $"{_settings.KeyLane1} / ↓";
        KeyHint2.Text = $"{_settings.KeyLane2} / ↑";
        KeyHint3.Text = $"{_settings.KeyLane3} / →";
    }

    private void KeyBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _listeningLane = int.Parse((string)((Border)sender).Tag);

        TextBlock[] labels = [KeyLabelLane0, KeyLabelLane1, KeyLabelLane2, KeyLabelLane3];
        Border[] btns = [KeyBtnLane0, KeyBtnLane1, KeyBtnLane2, KeyBtnLane3];

        // Сбросить все кнопки
        for (int i = 0; i < btns.Length; i++)
        {
            btns[i].BorderThickness = new Thickness(0);
            labels[i].Text = i switch
            {
                0 => _settings.KeyLane0,
                1 => _settings.KeyLane1,
                2 => _settings.KeyLane2,
                3 => _settings.KeyLane3,
                _ => ""
            };
        }

        // Подсветить активную и показать "..."
        btns[_listeningLane].BorderThickness = new Thickness(1);
        btns[_listeningLane].BorderBrush = new SolidColorBrush(Colors.White);
        labels[_listeningLane].Text = "...";

        // Мигание через таймер
        var blinkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        bool visible = true;
        blinkTimer.Tick += (_, _) =>
        {
            if (_listeningLane < 0) { blinkTimer.Stop(); return; }
            visible = !visible;
            labels[_listeningLane].Opacity = visible ? 1.0 : 0.2;
        };
        blinkTimer.Start();

        GameSettingsView.Focus();
    }

    private void GameSettingsView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_listeningLane < 0) return;

        string keyStr = e.Key.ToString();
        // Фильтр, только одиночные буквы/цифры
        if (keyStr.Length > 1 && !keyStr.StartsWith("D") && keyStr != "Space") return;
        if (keyStr.StartsWith("D") && keyStr.Length == 2) keyStr = keyStr[1..]; // D1→1

        switch (_listeningLane)
        {
            case 0: _settings.KeyLane0 = keyStr; KeyLabelLane0.Text = keyStr; break;
            case 1: _settings.KeyLane1 = keyStr; KeyLabelLane1.Text = keyStr; break;
            case 2: _settings.KeyLane2 = keyStr; KeyLabelLane2.Text = keyStr; break;
            case 3: _settings.KeyLane3 = keyStr; KeyLabelLane3.Text = keyStr; break;
        }

        _listeningLane = -1;
        Border[] btns = [KeyBtnLane0, KeyBtnLane1, KeyBtnLane2, KeyBtnLane3];
        foreach (var b in btns) b.BorderThickness = new Thickness(0);

        SettingsService.Save(_settings);

        TextBlock[] labels = [KeyLabelLane0, KeyLabelLane1, KeyLabelLane2, KeyLabelLane3];
        foreach (var l in labels) l.Opacity = 1.0;

        UpdateKeyHints();
        e.Handled = true;
    }

    private void ResetKeysBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.KeyLane0 = "A";
        _settings.KeyLane1 = "S";
        _settings.KeyLane2 = "W";
        _settings.KeyLane3 = "D";
        SettingsService.Save(_settings);
        LoadKeyLabels();
    }

    private void GameStatsMenuBtn_Click(object sender, MouseButtonEventArgs e)
    {
        GameMenuView.Visibility = Visibility.Collapsed;
        GameStatsView.Visibility = Visibility.Visible;
        GameStatsView.Focus();
        PopulateStatsList();
    }

    private void StatsBackToMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        GameStatsView.Visibility = Visibility.Collapsed;
        GameMenuView.Visibility = Visibility.Visible;
    }

    private void StatsDetailBackBtn_Click(object sender, RoutedEventArgs e)
    {
        GameStatsDetailView.Visibility = Visibility.Collapsed;
        GameStatsView.Visibility = Visibility.Visible;
        GameStatsView.Focus();
    }

    private void GameStatsView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) StatsBackToMenuBtn_Click(sender, e);
    }

    private void GameStatsDetailView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) StatsDetailBackBtn_Click(sender, e);
    }

    private void PopulateStatsList()
    {
        StatsListPanel.Children.Clear();
        var history = _settings.TrackHistory;

        // Фильтрация
        var query = history.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_statsSearchText))
            query = query.Where(t =>
                t.TrackTitle.Contains(_statsSearchText, StringComparison.OrdinalIgnoreCase));

        // Сортировка
        query = _settings.StatsSortMode switch
        {
            "TitleAsc"        => query.OrderBy(t => t.TrackTitle),
            "LastPlayedDesc"  => query.OrderByDescending(t => t.LastPlayed),
            "LastPlayedAsc"   => query.OrderBy(t => t.LastPlayed),
            "TimesPlayedDesc" => query.OrderByDescending(t => t.TimesPlayed),
            "TimesPlayedAsc"  => query.OrderBy(t => t.TimesPlayed),
            "BestScoreDesc"   => query.OrderByDescending(t => t.BestScore),
            "BestAccuracyDesc"=> query.OrderByDescending(t => t.BestAccuracy),
            _                 => query.OrderByDescending(t => t.LastPlayed)
        };

        var filtered = query.ToList();

        if (filtered.Count == 0)
        {
            StatsEmptyState.Visibility = Visibility.Visible;
            return;
        }
        StatsEmptyState.Visibility = Visibility.Collapsed;

        foreach (var t in filtered)
        {
            var rankColor = GetRankColor(t.BestRank);

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                Padding = new Thickness(16, 14, 16, 14)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rankTb = new TextBlock
            {
                Text = t.BestRank,
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(rankColor),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                LineHeight = 28
            };
            Grid.SetColumn(rankTb, 0);

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

            var titleTb = new TextBlock
            {
                Text = t.TrackTitle,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var subStack = new StackPanel { Orientation = Orientation.Horizontal };
            subStack.Children.Add(CreateMiniStat($"{t.BestAccuracy:F1}%", "#a855f7"));
            subStack.Children.Add(CreateSeparator());
            subStack.Children.Add(CreateMiniStat($"×{t.BestCombo}", "#3b82f6"));
            subStack.Children.Add(CreateSeparator());
            subStack.Children.Add(CreateMiniStat($"{t.TimesPlayed} игр", "#666"));

            infoStack.Children.Add(titleTb);
            infoStack.Children.Add(subStack);
            Grid.SetColumn(infoStack, 1);

            var rightStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            rightStack.Children.Add(new TextBlock
            {
                Text = t.BestScore.ToString("N0"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            });

            rightStack.Children.Add(new TextBlock
            {
                Text = t.LastPlayed.ToString("dd.MM.yy"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(rightStack, 2);

            grid.Children.Add(rankTb);
            grid.Children.Add(infoStack);
            grid.Children.Add(rightStack);
            card.Child = grid;

            card.MouseEnter += (s, _) =>
            {
                card.Background = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x27));
                card.BorderBrush = new SolidColorBrush(rankColor);
            };
            card.MouseLeave += (s, _) =>
            {
                card.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            };

            var captured = t;
            card.MouseLeftButtonUp += (_, _) => OpenStatsDetail(captured);

            StatsListPanel.Children.Add(card);
        }
    }

    private static TextBlock CreateMiniStat(string text, string colorHex)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(colorHex)
        };
    }

    private static Border CreateSeparator()
    {
        return new Border
        {
            Width = 3, Height = 3, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Color GetRankColor(string rank) => rank switch
    {
        "S" => Color.FromRgb(0xFF, 0xD7, 0x00),
        "A" => Color.FromRgb(0x7C, 0xFC, 0x00),
        "B" => Color.FromRgb(0x00, 0xBF, 0xFF),
        "C" => Color.FromRgb(0xCC, 0xCC, 0xCC),
        "D" => Color.FromRgb(0xFF, 0xA5, 0x00),
        _   => Color.FromRgb(0xFF, 0x44, 0x44)
    };

    private void OpenStatsDetail(GameTrackStats t)
    {
        GameStatsView.Visibility = Visibility.Collapsed;
        GameStatsDetailView.Visibility = Visibility.Visible;
        GameStatsDetailView.Focus();

        StatsDetailTitle.Text = t.TrackTitle;
        StatsDetailPlays.Text = $"{t.TimesPlayed} ЗАПУСКОВ";
        StatsDetailDates.Text = $"Первая: {t.FirstPlayed:dd.MM.yyyy} · Последняя: {t.LastPlayed:dd.MM.yyyy}";

        StatsDetailPanel.Children.Clear();

        double lifeAcc = t.TotalNotes > 0 ? t.TotalHits * 100.0 / t.TotalNotes : 0;
        double hitRate = (t.TotalHits + t.TotalMisses) > 0
            ? t.TotalHits * 100.0 / (t.TotalHits + t.TotalMisses) : 0;

        // БЛОК 1: ГЛАВНЫЕ РЕКОРДЫ (Сетка 2x2)
        AddSectionHeader("ЛИЧНЫЕ РЕКОРДЫ");

        var recordsGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        recordsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        recordsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        recordsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        recordsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddRecordCard(recordsGrid, 0, 0, "ЛУЧШИЙ РАНГ", t.BestRank, GetRankColor(t.BestRank));
        AddRecordCard(recordsGrid, 0, 1, "МАКС. ТОЧНОСТЬ", $"{t.BestAccuracy:F2}%", Color.FromRgb(0xa8, 0x55, 0xf7));
        AddRecordCard(recordsGrid, 1, 0, "ЛУЧШЕЕ КОМБО", $"×{t.BestCombo}", Color.FromRgb(0x3b, 0x82, 0xf6));
        AddRecordCard(recordsGrid, 1, 1, "РЕКОРД ОЧКОВ", t.BestScore.ToString("N0"), Color.FromRgb(0xff, 0xff, 0xff));

        StatsDetailPanel.Children.Add(recordsGrid);

        // БЛОК 2: ОБЩАЯ СТАТИСТИКА (Список)
        AddSectionHeader("ЗА ВСЕ ВРЕМЯ");

        AddDetailRow("Всего нажатий", t.TotalKeyPresses.ToString("N0"));
        AddDetailRow("Попаданий", $"{t.TotalHits:N0}", $"{hitRate:F1}% эффективность");
        AddDetailRow("Промахов", $"{t.TotalMisses:N0}", t.TotalMisses > 0 ? "#ef4444" : "#666");
        AddDetailRow("Lifetime Accuracy", $"{lifeAcc:F2}%", "отношение попаданий к нотам");

        // БЛОК 3: ДОПОЛНИТЕЛЬНО
        AddSectionHeader("ПРОЧЕЕ");
        AddDetailRow("Худшая точность", $"{t.WorstAccuracy:F1}%", "#666");
        AddDetailRow("Мин. очки", t.MinScore == int.MaxValue ? "—" : t.MinScore.ToString("N0"), "#666");
    }

    private void AddSectionHeader(string text)
    {
        StatsDetailPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            Margin = new Thickness(2, 8, 0, 8)
        });
    }

    private void AddRecordCard(Grid parent, int row, int col, string label, string value, Color accent)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(col == 0 ? 0 : 4, row == 0 ? 0 : 4, col == 1 ? 0 : 4, row == 1 ? 0 : 4)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent)
        });

        card.Child = stack;
        Grid.SetRow(card, row);
        Grid.SetColumn(card, col);
        parent.Children.Add(card);
    }

    private void AddDetailRow(string label, string value, string? subOrColor = null)
    {
        bool isColor = subOrColor?.StartsWith("#") == true;

        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 4)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftStack = new StackPanel();
        leftStack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)) });

        if (!isColor && subOrColor != null)
        {
            leftStack.Children.Add(new TextBlock
            {
                Text = subOrColor,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55))
            });
        }

        var valTb = new TextBlock
        {
            Text = value,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = isColor
                ? (SolidColorBrush)new BrushConverter().ConvertFrom(subOrColor!)
                : Brushes.White
        };

        Grid.SetColumn(leftStack, 0);
        Grid.SetColumn(valTb, 1);
        grid.Children.Add(leftStack);
        grid.Children.Add(valTb);
        row.Child = grid;

        StatsDetailPanel.Children.Add(row);
    }

    private void SpawnDoubleStrikeEffect(Color comboColor)
    {
        double canvasW = GamePlayView.ActualWidth > 0 ? GamePlayView.ActualWidth : 800;
        double canvasH = GamePlayView.ActualHeight > 0 ? GamePlayView.ActualHeight : 500;
        double hitY = canvasH - 70;
        var rng = new Random();

        // Второй цвет, осветлённый вариант основного
        Color brightColor = Color.FromRgb(
            (byte)Math.Min(255, comboColor.R + 80),
            (byte)Math.Min(255, comboColor.G + 80),
            (byte)Math.Min(255, comboColor.B + 80));

        Color[] palette = [comboColor, brightColor, Color.FromRgb(0xff, 0xff, 0xff)];

        var overlay = new Canvas
        {
            Width = canvasW, Height = canvasH,
            IsHitTestVisible = false
        };
        GamePlayView.Children.Add(overlay);

        // 1. Вспышка цвета комбо
        var flash = new System.Windows.Shapes.Rectangle
        {
            Width = canvasW, Height = canvasH,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                Color.FromArgb(90, comboColor.R, comboColor.G, comboColor.B),
                Color.FromArgb(0, comboColor.R, comboColor.G, comboColor.B),
                90)
        };
        overlay.Children.Add(flash);
        // Появляется быстро, уходит медленно
        var flashIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        var flashOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(2000))
            { BeginTime = TimeSpan.FromMilliseconds(120) };
        flashOut.Completed += (_, _) => overlay.Children.Remove(flash);
        flash.BeginAnimation(UIElement.OpacityProperty, flashIn);
        flash.BeginAnimation(UIElement.OpacityProperty, flashOut);

        // 2. Две волны от краёв
        foreach (bool fromLeft in new[] { true, false })
        {
            var wave = new Ellipse
            {
                Width = 200, Height = 200,
                Stroke = new SolidColorBrush(Color.FromArgb(200, comboColor.R, comboColor.G, comboColor.B)),
                StrokeThickness = 3,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.3, 0.3)
            };
            double startX = fromLeft ? 0 : canvasW - 200;
            Canvas.SetLeft(wave, startX);
            Canvas.SetTop(wave, hitY - 100);
            overlay.Children.Add(wave);

            var sc = (ScaleTransform)wave.RenderTransform;
            sc.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.3, 3.0, TimeSpan.FromMilliseconds(900))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            sc.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.3, 3.0, TimeSpan.FromMilliseconds(900))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            // Волна появляется, держится, плавно уходит
            var waveAnim = new DoubleAnimationUsingKeyFrames();
            waveAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            waveAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
            waveAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
            waveAnim.Completed += (_, _) => overlay.Children.Remove(wave);
            wave.BeginAnimation(UIElement.OpacityProperty, waveAnim);
        }

        // 3. Партиклы, первая волна
        for (int p = 0; p < 10; p++)
            SpawnDoubleStrikeParticle(overlay, canvasW, hitY, rng, palette);

        // Вторая волна через 120мс
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            for (int p = 0; p < 10; p++)
                SpawnDoubleStrikeParticle(overlay, canvasW, hitY, rng, palette);
        };
        t.Start();

        // Cleanup, ждём пока все анимации завершатся
        var cleanup = new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(2800));
        cleanup.Completed += (_, _) => GamePlayView.Children.Remove(overlay);
        overlay.BeginAnimation(UIElement.OpacityProperty, cleanup);
    }

    private void SpawnDoubleStrikeParticle(System.Windows.Controls.Canvas overlay, double canvasW, double hitY, Random rng, Color[] palette)
    {
        double angle = rng.NextDouble() * 360;
        double rad = angle * Math.PI / 180.0;
        double dist = 60 + rng.Next(30, 120);
        double size = rng.Next(4, 11);
        double startX = canvasW * 0.2 + rng.NextDouble() * canvasW * 0.6;

        var particle = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size,
            Fill = new System.Windows.Media.SolidColorBrush(palette[rng.Next(palette.Length)]),
            IsHitTestVisible = false,
            RenderTransform = new TranslateTransform()
        };
        System.Windows.Controls.Canvas.SetLeft(particle, startX);
        System.Windows.Controls.Canvas.SetTop(particle, hitY);
        overlay.Children.Add(particle);

        int dur = 900 + rng.Next(0, 400);
        var tt = (TranslateTransform)particle.RenderTransform;
        tt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, Math.Cos(rad) * dist, TimeSpan.FromMilliseconds(dur))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, Math.Sin(rad) * dist - 40, TimeSpan.FromMilliseconds(dur))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var fadeAnim = new DoubleAnimationUsingKeyFrames();
        fadeAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fadeAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(dur))));
        fadeAnim.Completed += (_, _) => overlay.Children.Remove(particle);
        particle.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
    }
}

// ── Brush Animation Helper ─────────────────────────────────────────────────────
public class BrushAnimation : AnimationTimeline
{
    public Brush? From { get; set; }
    public Brush? To { get; set; }

    public override Type TargetPropertyType => typeof(Brush);

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        if (animationClock == null || animationClock.CurrentProgress == null)
            return Brushes.Transparent;

        var fromBrush = From ?? (defaultOriginValue as Brush) ?? Brushes.Transparent;
        var toBrush = To ?? (defaultDestinationValue as Brush) ?? Brushes.Transparent;

        if (fromBrush is SolidColorBrush fromSolid && toBrush is SolidColorBrush toSolid)
        {
            var colorAnimation = new ColorAnimation(
                fromSolid.Color,
                toSolid.Color,
                Duration);
            colorAnimation.AccelerationRatio = AccelerationRatio;
            colorAnimation.DecelerationRatio = DecelerationRatio;
            colorAnimation.AutoReverse = AutoReverse;
            colorAnimation.RepeatBehavior = RepeatBehavior;

            var currentColor = (Color)colorAnimation.GetCurrentValue(fromSolid.Color, toSolid.Color, animationClock);
            return new SolidColorBrush(currentColor);
        }

        return toBrush;
    }

    protected override Freezable CreateInstanceCore() => new BrushAnimation();
}
