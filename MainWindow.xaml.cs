using System;
using System.Collections.Generic;
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

    private bool _halfwayTriggered = false;
    private bool _dangerModeActive = false;
    private DispatcherTimer? _dangerPulseTimer;
    private int _perfectStreak = 0; // подряд PERFECT

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
    private List<NoteEntry> _recordedNotes = new();
    private string? _editorMp3Path;
    private bool _editorRecording = false;

    private static readonly string LevelsDir =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetFix", "levels");
    
    private static readonly string BuiltInTracksDir =
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tracks");
    
    // Игровой оверлей поверх главного экрана
    private Border? _gameOverlayPanel = null;
    private bool _gameOverlayActive = false;
    
    // Таймер обратного отсчёта перед игрой
    private DispatcherTimer? _countdownTimer = null;
    
    // Параметры последней игры для перезапуска
    private List<NoteEntry>? _lastGameNotes = null;
    private string? _lastGameMp3Path = null;
    private string? _lastGameTitle = null;
    private double _lastGameBpm = 0;
    
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
        
        // Aurora animation — 30fps, синхронизировано с рендером
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
        
        // Цвет — меняем ВСЕ GradientStop'ы плавно от базового к результату
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _discord.Initialize();
        UpdateMainGridClip();
        LoadSettingsToPanel();

        if (!SettingsService.IsOnboarded)
            ShowOnboarding();
        else
        {
            FadeIn();
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

        // Позиция: правый край у курсора, снизу над панелью задач
        double left = pos.X - popupW;
        double top  = screen.WorkingArea.Bottom - popupH;

        if (left < screen.WorkingArea.Left) left = screen.WorkingArea.Left + 4;
        if (left + popupW > screen.WorkingArea.Right) left = screen.WorkingArea.Right - popupW - 4;
        if (top < screen.WorkingArea.Top) top = screen.WorkingArea.Top + 4;

        popup.Left = left;
        popup.Top  = top;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _auroraTimer?.Start(); // Запускаем Aurora анимацию при показе окна
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
    private void TestConfigsBtn_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.ZapretPath) || !File.Exists(_settings.ZapretPath))
        {
            return;
        }

        var configWindow = new Views.ZapretConfigWindow(_settings.ZapretPath, testMode: true);
        configWindow.Owner = this;
        configWindow.ShowDialog();
    }

    private async void SelectConfigBtn_Click(object s, RoutedEventArgs e)
    {
        Console.WriteLine("[MainWindow] SelectConfigBtn_Click started");
        
        if (string.IsNullOrEmpty(_settings.ZapretPath) || !File.Exists(_settings.ZapretPath))
        {
            return;
        }

        // Проверить есть ли кэш с тестами
        var cache = ZapretConfigService.LoadCache();
        if (cache == null || !cache.HasAnyConfigs)
        {
            // Показать стильное уведомление о необходимости полного сканирования
            ShowFullScanRequiredNotification();
        }
        else
        {
            Console.WriteLine("[MainWindow] Opening config window");
            // Показать окно выбора конфига
            var configWindow = new Views.ZapretConfigWindow(_settings.ZapretPath, testMode: false);
            configWindow.Owner = this;
            configWindow.ShowDialog();
            
            Console.WriteLine("[MainWindow] Config window closed");
            
            // Обновить отображение выбранного конфига после закрытия окна
            UpdateSelectedConfigDisplay();
            
            // Запустить сервис только если конфиг был ПРИМЕНЕН через кнопку "Применить"
            if (configWindow.ConfigWasApplied)
            {
                Console.WriteLine("[MainWindow] Config was applied, checking if service needs to be started");
                
                // Проверить текущее состояние Zapret
                var status = DiagnosticsEngine.CheckAppStatus();
                
                // Запустить только если Zapret НЕ запущен
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
        }
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

        // Чекбокс "Показывать это окно в будущем"
        var checkboxPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        // Создаем кастомный чекбокс
        var checkboxBorder = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Иконка галочки внутри чекбокса
        var checkIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Visible // По умолчанию включен
        };
        checkboxBorder.Child = checkIcon;

        var checkboxLabel = new TextBlock
        {
            Text = "Показывать это окно в будущем",
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Состояние чекбокса
        bool isChecked = true;

        // Обработчик клика на весь panel
        checkboxPanel.MouseLeftButtonDown += (s, e) =>
        {
            isChecked = !isChecked;
            checkIcon.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
            checkboxBorder.Background = isChecked 
                ? new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e))
                : new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18));
        };

        // Hover эффект
        checkboxPanel.MouseEnter += (s, e) =>
        {
            checkboxBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5b, 0xa2, 0xf6));
        };
        checkboxPanel.MouseLeave += (s, e) =>
        {
            checkboxBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
        };

        checkboxPanel.Children.Add(checkboxBorder);
        checkboxPanel.Children.Add(checkboxLabel);
        cardContent.Children.Add(checkboxPanel);

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
            // Сохраняем настройку
            _settings.ShowLongCheckDialog = isChecked;
            SettingsService.Save(_settings);
            
            // Закрываем диалог
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
            // Сохраняем настройку
            _settings.ShowLongCheckDialog = isChecked;
            SettingsService.Save(_settings);
            
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
            
            // Открыть окно тестирования
            var configWindow = new Views.ZapretConfigWindow(_settings.ZapretPath, testMode: true);
            configWindow.Owner = this;
            configWindow.ShowDialog();
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
        _auroraTimer?.Stop(); // Останавливаем Aurora анимацию при скрытии
        Hide();
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
            Text = "Обращение ко мне: Если ничего не помогло, вы можете описать свою проблему в разделе Issues на моём GitHub-репозитории. Я постараюсь ответить по мере возможности.", 
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
            // Не возвращаемся, продолжаем RunAutoFix
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
        AutoUpdatesCB.IsChecked = _settings.AutoUpdates;
    }

    private void SaveSettings_Click(object s, RoutedEventArgs e)
    {
        _settings.ZapretPath       = ZapretBox.Text.Trim();
        _settings.TgWsProxyPath    = TgWsBox.Text.Trim();
        _settings.AutostartZapret  = false; // Zapret убран из автозапуска
        _settings.AutostartTgWsProxy = AutoTgWsCB.IsChecked == true;
        _settings.AutostartApp     = AutoAppCB.IsChecked == true;
        _settings.AutoUpdates      = AutoUpdatesCB.IsChecked == true;
        SettingsService.Save(_settings);
        
        // Автозапуск через Task Scheduler
        SetAutostart(_settings.AutostartApp);
        
        CloseSettings();
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
        OpenUrl("https://www.tinkoff.ru/rm/kononenko.nikolay30/XeyPE87770");
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
                    return JsonSerializer.Deserialize<NoteMap>(File.ReadAllText(notesFile));
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
        }
        else
        {
            UserLevelsEmpty.Visibility = Visibility.Collapsed;
            UserLevelsList.Visibility = Visibility.Visible;
            UserLevelsList.ItemsSource = levels;
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

        StartGame(notes, null, "NetFix — Default Beat", REFERENCE_BPM);
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

            if (mp3Path != null) { _editorPlayer.Open(new Uri(mp3Path)); _editorPlayer.Play(); }

            _gameClock.Restart();
            CompositionTarget.Rendering -= GameTick;
            CompositionTarget.Rendering += GameTick;
            PreviewKeyDown += Game_KeyDown;
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

        // Спавн новых нот — используем кэшированные кисти
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
                // Используем кэшированные кисти — не создаём новые каждый раз
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
            note.Effect = effect; // сохраняем ссылку на эффект в NoteEntry
        }

        // Обновляем позиции — никаких new объектов
        var toRemove = new List<NoteEntry>();
        foreach (var note in _activeNotes)
        {
            if (note.Visual == null) continue;
            double progress = (now - (note.Time - _currentFallSec)) / _currentFallSec;
            double top = -50 + progress * (hitY + 50);
            Canvas.SetTop(note.Visual, top);

            // Свечение при приближении — обновляем существующий effect
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
                // Эффекты в очередь — не в game loop
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

        // Judge fade — просто Opacity, без аллокаций
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

    private void HitNote(NoteEntry note, int lane, int baseScore, string judge, Color color)
    {
        note.Hit = true;
        _gameCombo++;
        if (_gameCombo > _maxCombo) _maxCombo = _gameCombo;
        _hitNotes++;
        _consecutiveMisses = 0;
        _gameScore += baseScore * _gameCombo;

        // Считаем серию PERFECT
        if (judge == "PERFECT")
        {
            _perfectStreak++;
            // Каждые 10 PERFECT подряд — спецэффект
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

        // Тяжёлые эффекты — в очередь, не блокируем GameTick
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
        // Уровень каждые 7-8 комбо (округляем), максимум 15 уровней (до комбо ~112)
        int newLevel = Math.Min(15, (_gameCombo + 3) / 7);
        if (newLevel == _lastComboAuraLevel) return;
        _lastComboAuraLevel = newLevel;

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

        if (newLevel == 0) return;

        // ── Таблица уровней ─────────────────────────────────────────────
        var levels = new (Color main, Color? accent, string announce, byte alpha)[]
        {
            // 1  x7-8   — холодный синий
            (Color.FromRgb(0x38, 0xbf, 0xf8), null, "COMBO ×7", 60),
            // 2  x14-15 — индиго
            (Color.FromRgb(0x63, 0x66, 0xf1), null, "COMBO ×15!", 70),
            // 3  x21-22 — мятный
            (Color.FromRgb(0x10, 0xb9, 0x81), null, "COMBO ×22!", 75),
            // 4  x28-30 — золотой, первые звёздочки
            (Color.FromRgb(0xf5, 0x9e, 0x0b), null, "COMBO ×30! ⚡", 80),
            // 5  x35-37 — оранжевый
            (Color.FromRgb(0xf9, 0x73, 0x16), null, "COMBO ×37! 🔥", 85),
            // 6  x42-45 — красно-розовый
            (Color.FromRgb(0xf4, 0x3f, 0x5e), null, "COMBO ×45! 💥", 90),
            // 7  x49-52 — алый
            (Color.FromRgb(0xef, 0x44, 0x44), null, "COMBO ×52!", 95),
            // 8  x56-60 — пурпурный
            (Color.FromRgb(0xa8, 0x55, 0xf7), null, "COMBO ×60!", 100),
            // 9  x63-67 — неоново-розовый
            (Color.FromRgb(0xec, 0x4e, 0xff), null, "COMBO ×67! 🌸", 105),
            // 10 x70-75 — двухцветный: розовый + синий акцент
            (Color.FromRgb(0xff, 0x6b, 0xb5), Color.FromRgb(0x38, 0xbf, 0xf8), "COMBO ×75! 🌈", 110),
            // 11 x77-82 — белое золото
            (Color.FromRgb(0xff, 0xeb, 0x3b), Color.FromRgb(0xff, 0xa0, 0x00), "COMBO ×82! 👑", 115),
            // 12 x84-90 — ультрафиолет
            (Color.FromRgb(0x7c, 0x3a, 0xed), Color.FromRgb(0xec, 0x4e, 0xff), "COMBO ×90! ⚡💜", 120),
            // 13 x91-97 — огненный градиент
            (Color.FromRgb(0xff, 0x45, 0x00), Color.FromRgb(0xff, 0xd7, 0x00), "COMBO ×97! 🔥👑", 125),
            // 14 x98-105 — ледяной cyan
            (Color.FromRgb(0x00, 0xff, 0xff), Color.FromRgb(0x00, 0x80, 0xff), "COMBO ×105! ❄️", 130),
            // 15 x106-112+ — RAINBOW
            (Color.FromRgb(0xff, 0xff, 0xff), Color.FromRgb(0xff, 0x6b, 0xb5), "MAX COMBO!! 🌟✨🔥", 140),
        };

        var (mainColor, accentColor, announceText, alpha) = levels[newLevel - 1];
        Color c = mainColor;

        // Создаём виньетку (как danger mode, но цветную)
        var vignette = new System.Windows.Shapes.Rectangle
        {
            Tag = "combo_aura",
            IsHitTestVisible = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };

        // Виньетка — цветная по краям, прозрачная в центре
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

        // Пульсация — скорость и амплитуда растут с уровнем
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
        
        // На максимальном уровне (15) — НАМНОГО больше звёзд
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
            double viewWidth  = GameCanvas.ActualWidth;   // 240px — только игровое поле
            double viewHeight = GameCanvas.ActualHeight;  // без нижней панели

            for (int i = 0; i < count; i++)
            {
                double startX = rng.NextDouble() * viewWidth;
                double startY = viewHeight + 10;
                double endX   = startX + rng.Next(-80, 80);
                double endY   = rng.Next((int)(viewHeight * 0.2), (int)(viewHeight * 0.7)); // в пределах канваса
                double size   = rng.Next(6, level >= 3 ? 18 : 14);
                double dur    = 1200 + rng.Next(0, 600);

                // Для белого цвета (максимальное комбо) — используем радужные цвета
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
                    // Обычные звёзды — светлее основного цвета
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

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
            { BeginTime = TimeSpan.FromMilliseconds(1400) };
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

        // Виньетка — красная по краям, прозрачная в центре
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

        int holdMs = 800 + Math.Min(level * 60, 600);
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350))
            { BeginTime = TimeSpan.FromMilliseconds(holdMs) };
        fadeOut.Completed += (_, _) => GamePlayView.Children.Remove(announce);
        announce.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void ShowJudge(string text, Color color)
    {
        // MISS показываем статично, PERFECT/GOOD — через SpawnHitEffect
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

        // 3. Частицы — разлетаются во все стороны
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
        _editorPlayer.Stop();
        _gameClock.Stop();
        _auroraGameTimer?.Stop();

        int acc = _totalNotes > 0 ? (int)((double)_hitNotes / _totalNotes * 100) : 100;
        string rank = failed ? "F" : acc >= 95 ? "S" : acc >= 85 ? "A" : acc >= 70 ? "B" : acc >= 50 ? "C" : "D";

        // Discord - обновляем статус с результатами
        _discordGameTimer?.Stop();
        _discordGameTimer = null;
        _discord.SetGameResults(_currentTrackTitle, rank, _gameScore, acc, _maxCombo);
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
            Title = "Выбрать архив с треком",
            Filter = "ZIP Archive|*.zip",
            DefaultExt = ".zip"
        };
        
        if (dlg.ShowDialog() != true) return;
        
        try
        {
            // Создаём временную директорию для распаковки
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            
            // Распаковываем архив
            ZipFile.ExtractToDirectory(dlg.FileName, tempDir);
            
            // Ищем notes.json в распакованной директории
            var notesJsonPath = System.IO.Path.Combine(tempDir, "notes.json");
            if (!File.Exists(notesJsonPath))
            {
                ShowNotification("Ошибка импорта", "Архив не содержит файл notes.json", isError: true);
                Directory.Delete(tempDir, true);
                return;
            }
            
            // Читаем метаданные уровня
            var json = File.ReadAllText(notesJsonPath);
            var map = JsonSerializer.Deserialize<NoteMap>(json);
            if (map == null || string.IsNullOrEmpty(map.Title))
            {
                ShowNotification("Ошибка импорта", "Некорректный формат файла notes.json", isError: true);
                Directory.Delete(tempDir, true);
                return;
            }
            
            // Проверяем, не существует ли уже уровень с таким названием
            var targetDir = System.IO.Path.Combine(LevelsDir, map.Title);
            if (Directory.Exists(targetDir))
            {
                ShowConfirmDialog(
                    "Уровень уже существует",
                    $"Уровень «{map.Title}» уже существует. Заменить его?",
                    confirmed =>
                    {
                        if (!confirmed)
                        {
                            Directory.Delete(tempDir, true);
                            return;
                        }
                        
                        // Удаляем старый уровень
                        Directory.Delete(targetDir, true);
                        
                        // Перемещаем новый уровень
                        Directory.Move(tempDir, targetDir);
                        
                        // Обновляем список
                        LoadUserLevels();
                        ShowNotification("Успешно", $"Трек «{map.Title}» импортирован", isError: false);
                    });
            }
            else
            {
                // Создаём директорию для уровней если её нет
                if (!Directory.Exists(LevelsDir))
                    Directory.CreateDirectory(LevelsDir);
                
                // Перемещаем уровень
                Directory.Move(tempDir, targetDir);
                
                // Обновляем список
                LoadUserLevels();
                ShowNotification("Успешно", $"Трек «{map.Title}» импортирован", isError: false);
            }
        }
        catch (Exception ex)
        {
            ShowNotification("Ошибка импорта", $"Не удалось импортировать трек: {ex.Message}", isError: true);
        }
    }
    
    private void ShowNotification(string title, string message, bool isError)
    {
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
            BorderBrush = new SolidColorBrush(isError 
                ? Color.FromRgb(0xef, 0x44, 0x44) 
                : Color.FromRgb(0x22, 0xc5, 0x5e)),
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
            Background = new SolidColorBrush(isError 
                ? Color.FromRgb(0xef, 0x44, 0x44) 
                : Color.FromRgb(0x22, 0xc5, 0x5e)) { Opacity = 0.15 },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };

        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(isError 
                ? "M6,6 L18,18 M18,6 L6,18" // Крестик для ошибки
                : "M4,12 L9,17 L20,6"), // Галочка для успеха
            Stroke = new SolidColorBrush(isError 
                ? Color.FromRgb(0xef, 0x44, 0x44) 
                : Color.FromRgb(0x22, 0xc5, 0x5e)),
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
            Background = new SolidColorBrush(isError 
                ? Color.FromRgb(0xef, 0x44, 0x44) 
                : Color.FromRgb(0x22, 0xc5, 0x5e)),
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
    
    private void ShowConfirmDialog(string title, string message, Action<bool> callback)
    {
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
            Content = "Удалить",
            Padding = new Thickness(16, 8, 16, 8),
            Background = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)),
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
            new SolidColorBrush(Color.FromRgb(0xdc, 0x26, 0x26))));
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

    private static int GetGameLane(Key key) => key switch
    {
        Key.A or Key.Left => 0,
        Key.S or Key.Down => 1,
        Key.W or Key.Up => 2,
        Key.D or Key.Right => 3,
        _ => -1
    };

    private void EditorPlayer_Ended(object? s, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(StopEditorRecording));
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
                   "Для поиска оптимальных настроек рекомендую пройти полное тестирование. " +
                   "Это займёт около 10 минут, но зато в следующий раз, когда что-то сломается, " +
                   "ты сможешь быстро переключиться на другой конфиг и всё заработает!\n\n" +
                   "Тебе ничего не нужно делать — приложение само всё протестирует. " +
                   "Просто подожди 10 минут!");

        AddWizBtn("Пройти тестирование", "#22c55e", () =>
        {
            CloseWizard();
            // Открываем окно тестирования конфигов
            var configWindow = new ZapretConfigWindow(_settings.ZapretPath, true);
            configWindow.Owner = this;
            configWindow.ShowDialog();
        });

        AddWizBtn("Отмена", "#ef4444", CloseWizard);
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

    private void AddWizBtn(string txt, string hex, Action act, string fgHex = "#ffffff")
    {
        var btn = new Button {
            Content = txt,
            Background = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!,
            Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(fgHex)!,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Height = 40,
            Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 10),
            Template = CreateSimpleBtnTemplate(hex)
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
        AddOnboardSub(p, "Приложение НЕ СОБИРАЕТ ВАШИ ДАННЫЕ.\nКак разработчик пишу: ОНИ МНЕ НАХУЙ НЕ НУЖНЫ!\nЕсли вы беспокоитесь за свою безопасность, то нахуя вы скачали это с GitHub?\nВ любом случае, исходный код доступен на GitHub.");
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
        var btn = new Button
        {
            Content             = text,
            Background          = (SolidColorBrush)new BrushConverter().ConvertFrom(bgHex)!,
            Foreground          = (SolidColorBrush)new BrushConverter().ConvertFrom(foreground)!,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 14,
            Height              = 44,
            Cursor              = Cursors.Hand,
            BorderThickness     = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin              = new Thickness(0, 0, 0, 10),
        };

        // Simple style inline
        btn.Template = CreateSimpleBtnTemplate(bgHex);
        btn.Click += (_, _) => action();
        p.Children.Add(btn);
    }

    private static ControlTemplate CreateSimpleBtnTemplate(string bgHex)
    {
        var tmpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty,
            (SolidColorBrush)new BrushConverter().ConvertFrom(bgHex)!);
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
            { _netTimer?.Stop(); _pingTimer?.Stop(); }
            else
            { _netTimer?.Start(); _pingTimer?.Start(); }
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

    private void NetTimer_Tick(object? sender, EventArgs e)
    {
        var (rx, tx) = GetNetworkBytes();
        double dlNow = Math.Max(0, rx - _lastBytesReceived);
        double ulNow = Math.Max(0, tx - _lastBytesSent);
        _lastBytesReceived = rx;
        _lastBytesSent = tx;

        // Пока тест не закончен — показываем анимацию, после — результат теста
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
            // Только физические интерфейсы — без loopback и виртуальных
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
                    ? Color.FromRgb(0xf0, 0xf0, 0xf0)   // белый — хороший
                    : Color.FromRgb(0xf5, 0x9e, 0x0b)); // жёлтый — высокий
            });
        }
        catch (Exception ex) { Console.WriteLine($"[Ping] FATAL: {ex.Message}"); }
    }

    // Расчёт финальной скорости с компенсацией прогрева TCP
    private static double CalcFinalSpeed(List<double> samples)
    {
        if (samples.Count == 0) return 0;
        
        // Отбрасываем первые 2 сэмпла — TCP ещё разгоняется
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
