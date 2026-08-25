using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using NetFix.Services.Mods;
using NetFix.Views;
using System.Runtime.InteropServices;

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
using ListBox      = System.Windows.Controls.ListBox;
using Size         = System.Windows.Size;
using Path         = System.IO.Path;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace NetFix;

public partial class MainWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_CLOAK = 13;

    private static bool SetCloak(IntPtr hwnd, bool cloak)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            int val = cloak ? 1 : 0;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref val, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

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

    private AppSettings _settings = SettingsService.Load();
    private bool _settingsOpen = false;
    private bool _onboardForceReserve = false;
    private bool _onboardIsManual = false;
    private bool _isDialogOpen = false;
    private bool _hostsWarningShown = false;
    private DispatcherTimer _monitorTimer = null!;
    private System.Windows.Forms.NotifyIcon _trayIcon = null!;
    private DispatcherTimer? _longCheckTimer = null;
    private bool _checkInProgress = false;
    private bool _autoFixRunning = false;
    private int _zapretToggleFails = 0;
    private int _mainZapretStartFails = 0;
    private Views.ZapretConfigWindow? _configWindow = null;

    private bool _isConnected;
    private DateTime _connectedSince;
    private DispatcherTimer? _connectedTimer;
    private bool _isInstalling = false;
    private DispatcherTimer? _successRingTimer;
    private SolidColorBrush? _successRingIconBrush;

    private DnsEtwMonitor? _dnsEtwMonitor;
    private DispatcherTimer? _connAnalysisTimer;
    private bool _connAnalysisActive = false;
    private bool _isSystemMode = false;
    private ProcessItemModel? _selectedConnApp;
    private List<ProcessItemModel> _allProcesses = [];
    private readonly HashSet<string> _expandedConnKeys = [];

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(parent, i));
            if (result is not null) return result;
        }
        return null;
    }

    private void SubscribeSmoothScroll(ListBox listBox)
    {
        listBox.PreviewMouseWheel += (_, e) =>
        {
            var sv = FindScrollViewer(listBox);
            if (sv is null) return;
            e.Handled = true;

            double delta = e.Delta > 0 ? -40 : 40;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
        };
    }

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
    private int _lastComboAuraLevel = 0;
    private Color _currentComboColor = Color.FromRgb(0xff, 0xd7, 0x00);

    private bool _halfwayTriggered = false;
    private bool _dangerModeActive = false;
    private DispatcherTimer? _dangerPulseTimer;
    private int _perfectStreak = 0;
    private readonly HashSet<int> _activeLanes = [];
    private readonly HashSet<int> _hitLanesThisFrame = [];

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _effectQueue = new();
    private DispatcherTimer? _effectTimer;

    private readonly DiscordRpcService _discord = new();
    private DateTime _gameStartDateTime;
    private DispatcherTimer? _discordGameTimer;
    private DispatcherTimer? _discordEditorTimer;
    private int _maxCombo = 0;
    private string _currentTrackTitle = "";
    private bool _isInGame = false;

    private readonly SolidColorBrush[] _laneBrushes = LaneColors
        .Select(c => new SolidColorBrush(c)).ToArray();
    private readonly LinearGradientBrush[] _noteGradients = LaneColors
        .Select(c => new LinearGradientBrush(
            Color.FromArgb(80, c.R, c.G, c.B),
            Color.FromArgb(20, c.R, c.G, c.B), 90))
        .ToArray();

    private DispatcherTimer? _starTimer;
    private int _starBurst = 0;
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

    private Border? _gameOverlayPanel = null;
    private bool _gameOverlayActive = false;

    private UIElement? _oszReturnView = null;

    private DispatcherTimer? _countdownTimer = null;

    private List<NoteEntry>? _lastGameNotes = null;
    private string? _lastGameMp3Path = null;
    private string? _lastGameTitle = null;
    private double _lastGameBpm = 0;
    private string? _pendingOszPath;

    private ICollectionView? _userTracksView;
    private ICollectionView? _osuTracksView;
    private string _userSearchText = string.Empty;
    private bool _isEntranceAnimating = false;
    private string _osuSearchText = string.Empty;
    private string _statsSearchText = string.Empty;
    private bool _wasClosedToTray = false;

    private bool _settingsLoaded;

    private List<ModEntry> _allMods = [];
    private ModType _currentModsTab = ModType.Strategy;
    private bool _modsLoaded;
    private bool _strategyDirty;
    private bool _listsDirty;
    private bool _hostsDirty;
    private DispatcherTimer? _savePosTimer;
    private bool _forceClose;
    private ModEntry? _dragMod;
    private bool _dragFromActive;
    private Point _dragStartPoint;
    private bool _isDragPending;
    private ModEntry? _pendingToggleMod;
    private DragAdorner? _currentDragAdorner;

    private DispatcherTimer _netTimer = null!;
    private DispatcherTimer _pingTimer = null!;
    private long _lastBytesReceived = 0;
    private long _lastBytesSent = 0;
    private bool _speedTestDone = false;

    private readonly List<double> _dlSamples = new();
    private readonly List<double> _ulSamples = new();
    private double _finalDownloadMbps = 0;
    private double _finalUploadMbps = 0;

    private DispatcherTimer _auroraTimer = null!;
    private double _t = 0;
    private double _splitProgress = 0;
    private double _splitTarget = 0;
    private double _colorProgress = 0;
    private double _colorTarget = 0;
    private bool _finalSuccess = true;

    private Color[] _baseColors = new Color[]
    {
        Color.FromRgb(59, 130, 246),
        Color.FromRgb(139, 92, 246),
        Color.FromRgb(79, 70, 229)
    };
    private Color _successColor = Color.FromRgb(34, 197, 94);
    private Color _errorColor = Color.FromRgb(239, 68, 68);

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        InitTray();

        _auroraTimer = new DispatcherTimer(DispatcherPriority.Render);
        _auroraTimer.Interval = TimeSpan.FromMilliseconds(33);
        _auroraTimer.Tick += (s, e) =>
        {
            _t += 0.05;

            _splitProgress += (_splitTarget - _splitProgress) * 0.05;
            _colorProgress += (_colorTarget - _colorProgress) * 0.03;

            UpdateBlob(AuroraRect1, 0, 0.50, 0.25, 0.15, 0.06, 0.56, 0.44, 0, 1.2, 0);
            UpdateBlob(AuroraRect2, 1, 0.0, 0.0, 0.10, 0.08, 1.30, 0.96, 2.1, 0.5, 0);
            UpdateBlob(AuroraRect3, 2, 1.0, 0.95, 0.09, 0.09, 1.10, 1.44, 4.2, 2.8, 0);
        };
        _auroraTimer.Start();

        this.StateChanged += (s, e) =>
        {
            if (this.WindowState == WindowState.Minimized)
                _auroraTimer.Stop();
            else
                _auroraTimer.Start();
        };
    }

    private void UpdateBlob(System.Windows.Shapes.Rectangle rect, int index, double bx, double by, double ampX, double ampY, double freqX, double freqY, double phX, double phY, byte baseAlpha)
    {
        var brush = (RadialGradientBrush)rect.Fill;
        double ease = EaseInOut(_splitProgress);
        double colorEase = EaseInOut(_colorProgress);

        double currentAmpX = Lerp(0.03, ampX, ease);
        double currentAmpY = Lerp(0.03, ampY, ease);

        double cx = bx + Math.Sin(_t * freqX + phX) * currentAmpX;
        double cy = by + Math.Cos(_t * freqY + phY) * currentAmpY;

        double baseRadius = index == 0 ? 0.32 : 0.22;

        Color targetColor = _finalSuccess ? _successColor : _errorColor;
        Color currentColor = LerpColor(_baseColors[index], targetColor, colorEase);

        brush.Center = new System.Windows.Point(cx, cy);
        brush.GradientOrigin = new System.Windows.Point(cx, cy);
        brush.RadiusX = baseRadius;
        brush.RadiusY = baseRadius;

        foreach (var stop in brush.GradientStops)
        {
            byte originalAlpha = stop.Color.A;
            stop.Color = Color.FromArgb(originalAlpha, currentColor.R, currentColor.G, currentColor.B);
        }
    }

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
        _settingsLoaded = true;

        AutoAppCB.Checked += (_, _) =>
        {
            StartMinimizedCB.IsEnabled = true;
        };
        AutoAppCB.Unchecked += (_, _) =>
        {
            StartMinimizedCB.IsEnabled = false;
            StartMinimizedCB.IsChecked = false;
        };

        if (_settings.RememberWindowSize)
        {
            if (!double.IsNaN(_settings.WindowWidth)) Width = _settings.WindowWidth;
            if (!double.IsNaN(_settings.WindowHeight)) Height = _settings.WindowHeight;
            if (!double.IsNaN(_settings.WindowLeft)) Left = _settings.WindowLeft;
            if (!double.IsNaN(_settings.WindowTop)) Top = _settings.WindowTop;
        }

        _savePosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _savePosTimer.Tick += (_, _) =>
        {
            _savePosTimer.Stop();
            SaveWindowPosition();
        };
        SizeChanged += (_, _) => { _savePosTimer?.Stop(); _savePosTimer?.Start(); };
        LocationChanged += (_, _) => { _savePosTimer?.Stop(); _savePosTimer?.Start(); };

        if (!SettingsService.IsOnboarded)
            ShowOnboarding();
        else
        {
            FadeIn();
            _ = WriteStartupLogAsync();
            CheckInternetOnStart();
            StartActiveAppsMonitor();
            CheckInitialServiceState();

            InitializeVersionFiles();

            if (_settings.AutoUpdates)
            {
                CheckForUpdatesBackgroundAsync();
            }
        }
        LoadFaqItems();
        InitNetworkMonitor();

        if (_settings.AutoEacBypass)
        {
            AntiCheatBypassService.StartWatcher(OnAntiCheatDetected);
        }

        if (_settings.AutostartTgWsProxy
            && !string.IsNullOrEmpty(_settings.TgWsProxyPath)
            && File.Exists(_settings.TgWsProxyPath)
            && Process.GetProcessesByName("TgWsProxy").Length == 0)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
                Dispatcher.Invoke(() => StartTgWsProxyWithActivation()),
                TaskScheduler.Default);
        }

        LogBox.PreviewMouseLeftButtonDown += LogBox_PreviewMouseLeftButtonDown;
        LogBox.PreviewMouseMove += LogBox_PreviewMouseMove;

        SubscribeSmoothScroll(AvailableList);
        SubscribeSmoothScroll(ActiveList);
        SubscribeSmoothScroll(ListsAvailableList);
        SubscribeSmoothScroll(ListsActiveList);

        if (_settings.StartMinimizedToTray && Environment.GetCommandLineArgs().Contains("--autostart"))
        {
            Hide();
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
        if ((DateTime.UtcNow - TrayPopup.LastClosedTime).TotalMilliseconds < 300)
            return;

        foreach (Window win in System.Windows.Application.Current.Windows)
        {
            if (win is TrayPopup popupWin)
            {
                popupWin.Close();
                return;
            }
        }

        var popup = new TrayPopup { Owner = this };

        popup.Left = -9999;
        popup.Top  = -9999;
        popup.Show();
        popup.UpdateLayout();

        var pos    = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(pos);

        double popupW = popup.ActualWidth;
        double popupH = popup.ActualHeight;

        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;

        double cursorX = pos.X / sx;
        double cursorY = pos.Y / sy;

        double workLeft   = screen.WorkingArea.Left   / sx;
        double workRight  = screen.WorkingArea.Right  / sx;
        double workTop    = screen.WorkingArea.Top    / sy;
        double workBottom = screen.WorkingArea.Bottom / sy;

        double left = cursorX - popupW;
        double top  = cursorY - popupH;

        if (left < workLeft) left = workLeft + 4;
        if (left + popupW > workRight) left = workRight - popupW - 4;
        if (top < workTop) top = workTop + 4;
        if (top + popupH > workBottom) top = workBottom - popupH;

        popup.Left = left;
        popup.Top  = top;
        popup.Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Normal)
        {
            _auroraTimer?.Start();
            _ = Task.Run(async () =>
            {
                await Task.Delay(350);
                Dispatcher.Invoke(() =>
                {
                    _monitorTimer?.Start();
                    _netTimer?.Start();
                    _pingTimer?.Start();
                });
            });
        }
    }

    public void ShowFromTray()
    {
        bool wasClosedToTray = _wasClosedToTray || !IsVisible || Visibility != Visibility.Visible;
        _wasClosedToTray = false;

        Show();
        WindowState = WindowState.Normal;
        Activate();

        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
            SetForegroundWindow(helper.Handle);

        _auroraTimer?.Start();

        if (wasClosedToTray && MainPage != null && MainPage.Visibility == Visibility.Visible && SettingsService.IsOnboarded)
        {
            if (EntranceCurtain != null)
            {
                EntranceCurtain.Visibility = Visibility.Visible;
                EntranceCurtain.Opacity = 1;
            }
            PlayEpicMainEntranceAnimation(0);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            Dispatcher.Invoke(() =>
            {
                _monitorTimer?.Start();
                _netTimer?.Start();
                _pingTimer?.Start();
            });
        });
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

    private async Task ActivateTgWsProxyAsync()
    {
        try
        {
            await Task.Delay(2500);

            string? proxyUrl = await Task.Run(() => GetTgWsProxyUrl());

            if (!string.IsNullOrEmpty(proxyUrl))
            {
                Process.Start(new ProcessStartInfo(proxyUrl) { UseShellExecute = true });
            }
            else
            {
                await Task.Run(() => ClickTrayIconByProcess("TgWsProxy"));
            }
        }
        catch
        {
        }
    }

    private string? GetTgWsProxyUrl()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configPath = Path.Combine(appData, "TgWsProxy", "config.json");

            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                string? host = ExtractJsonValue(json, "host");
                string? port = ExtractJsonValue(json, "port");
                string? secret = ExtractJsonValue(json, "secret");

                if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port) && !string.IsNullOrEmpty(secret))
                {
                    string linkHost = host == "0.0.0.0" ? "127.0.0.1" : host;
                    return $"tg://proxy?server={linkHost}&port={port}&secret=dd{secret}";
                }
            }

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
            IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
            if (taskbarHandle == IntPtr.Zero) return;

            IntPtr trayHandle = FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);
            if (trayHandle == IntPtr.Zero) return;

            IntPtr sysPagerHandle = FindWindowEx(trayHandle, IntPtr.Zero, "SysPager", null);
            if (sysPagerHandle == IntPtr.Zero) return;

            IntPtr notificationAreaHandle = FindWindowEx(sysPagerHandle, IntPtr.Zero, "ToolbarWindow32", null);
            if (notificationAreaHandle == IntPtr.Zero) return;

            int buttonCount = (int)SendMessage(notificationAreaHandle, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);

            if (buttonCount > 0)
            {
                if (GetClientRect(notificationAreaHandle, out RECT rect))
                {
                    int centerX = (rect.Right - rect.Left) / 2;
                    int centerY = (rect.Bottom - rect.Top) / 2;

                    POINT pt = new POINT { X = centerX, Y = centerY };
                    ClientToScreen(notificationAreaHandle, ref pt);

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
        }
    }

    private async void ServicesBtn_Click(object s, RoutedEventArgs e)
    {
        StopGame();
        StopEditorRecording();

        ServicesLayer.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(50, 0, TimeSpan.FromMilliseconds(280));
        anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
        ServicesTrans.BeginAnimation(TranslateTransform.XProperty, anim);
        ServicesPanel.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

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
        ZapretToggleProgress.Visibility = Visibility.Visible;

        try
        {
            var st = DiagnosticsEngine.CheckAppStatus();

            if (st.ZapretRunning)
            {
                foreach (var p in Process.GetProcessesByName("winws"))
                    try { p.Kill(); } catch { }
                foreach (var p in Process.GetProcessesByName("winws.exe"))
                    try { p.Kill(); } catch { }
                _zapretToggleFails = 0;
            }
            else
            {
                if (!string.IsNullOrEmpty(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
                {
                    var isServiceBat = System.IO.Path.GetFileName(_settings.ZapretPath).Equals("service.bat", StringComparison.OrdinalIgnoreCase);
                    var cache = ZapretConfigService.LoadCache();

                    if (isServiceBat)
                    {
                        if (cache == null || !cache.HasAnyConfigs)
                        {
                            ShowFullScanRequiredNotification(
                                "Конфиги Zapret не найдены",
                                "Приложение не смогло обнаружить рабочие конфиги для Zapret.\n\n" +
                                "Сначала пройдите полное сканирование, чтобы NetFix нашёл доступные конфиги и подготовил запуск сервиса.");
                            _zapretToggleFails = 0;
                            return;
                        }

                        if (string.IsNullOrEmpty(cache.CurrentConfig))
                        {
                            _zapretToggleFails = 0;
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
                            TrackZapretStartFail();
                            return;
                        }
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo(_settings.ZapretPath) { UseShellExecute = true });
                    }
                }
                else
                {
                    return;
                }
            }

            await Task.Delay(2000);
            UpdateActiveApps();

            var afterSt = DiagnosticsEngine.CheckAppStatus();
            if (afterSt.ZapretRunning)
                _zapretToggleFails = 0;
            else
                TrackZapretStartFail();
        }
        finally
        {
            ZapretToggleProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void TgWsToggle_Click(object s, RoutedEventArgs e)
    {
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

                    await ActivateTgWsProxyAsync();
                }
                else
                {
                    return;
                }
            }

            await Task.Delay(2000);
            UpdateActiveApps();
        }
        finally
        {
            TgWsToggleProgress.Visibility = Visibility.Collapsed;
        }
    }

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

        var cache = ZapretConfigService.LoadCache();
        if (cache == null || !cache.HasAnyConfigs)
        {
            ShowFullScanRequiredNotification();
            return;
        }

        ShowConfigWindow(testMode: false, onClosed: async (w) =>
        {
            UpdateSelectedConfigDisplay();

            if (w.ConfigWasApplied)
            {
                var status = DiagnosticsEngine.CheckAppStatus();
                if (!status.ZapretRunning)
                {
                    ZapretToggle_Click(this, new RoutedEventArgs());
                }
                else
                {

                }
            }

        });
    }

    private void ModsBtn_Click(object s, RoutedEventArgs e)
    {
        CloseServicesPanel();
        ShowModsPage();
    }

    private void ModsNavBtn_Click(object s, RoutedEventArgs e)
    {
        StopConnectionAnalysis();
        StopGame();
        StopEditorRecording();
        ShowModsPage();
    }

    private void ShowModsPage()
    {
        StopConnectionAnalysis();
        MainPage.Visibility = Visibility.Collapsed;
        GamePage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        ModsPage.Visibility = Visibility.Visible;

        ModsNavBtn.Foreground = Brushes.White;
        ServicesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        GameNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        ShowModsSubScreen(ModsHomeScreen, "Модификации");

        if (!_modsLoaded)
        {
            ModScanner.EnsureDirectories();
            AutoHostsModService.EnsureAutoMods();
            RefreshMods();
            _modsLoaded = true;
            UpdateModsStatus();
            ResetArrows();
        }
    }

    private void ShowModsSubScreen(Grid screen, string title)
    {
        ModsHomeScreen.Visibility = Visibility.Collapsed;
        ModsStrategiesScreen.Visibility = Visibility.Collapsed;
        ModsListsScreen.Visibility = Visibility.Collapsed;
        ModsHostsScreen.Visibility = Visibility.Collapsed;
        ModsListsChoiceScreen.Visibility = Visibility.Collapsed;
        ModsMyModsScreen.Visibility = Visibility.Collapsed;
        ModsEditorScreen.Visibility = Visibility.Collapsed;
        screen.Visibility = Visibility.Visible;
        ModsHeaderTitle.Text = title;

        var showStatus = screen == ModsStrategiesScreen || screen == ModsListsScreen || screen == ModsHostsScreen || screen == ModsMyModsScreen;
        ModsHeaderStatus.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        if (showStatus) ModsStatusText.Text = "";
    }

    private void ModsBackBtn_Click(object s, RoutedEventArgs e)
    {
        if (ModsHomeScreen.Visibility == Visibility.Visible)
        {
            ModsPage.Visibility = Visibility.Collapsed;
            MainPage.Visibility = Visibility.Visible;
            ModsNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
        else if (ModsListsScreen.Visibility == Visibility.Visible || ModsHostsScreen.Visibility == Visibility.Visible)
        {
            ShowModsSubScreen(ModsListsChoiceScreen, "Списки обхода");
        }
        else
        {
            ShowModsSubScreen(ModsHomeScreen, "Модификации");
        }
    }

    private void ModsCardStrategies_Click(object s, RoutedEventArgs e)
    {
        _currentModsTab = ModType.Strategy;
        ShowModsSubScreen(ModsStrategiesScreen, ".bat Стратегии");
        if (_modsLoaded) RefreshModsLists();
    }

    private void ModsCardLists_Click(object s, RoutedEventArgs e)
    {
        ShowModsSubScreen(ModsListsChoiceScreen, "Списки обхода");
    }

    private void ChoiceDomainLists_Click(object s, RoutedEventArgs e)
    {
        _currentModsTab = ModType.List;
        ShowModsSubScreen(ModsListsScreen, "Листы доменов");
        if (_modsLoaded) RefreshModsLists();
    }

    private void ChoiceHostsLists_Click(object s, RoutedEventArgs e)
    {
        _currentModsTab = ModType.Hosts;
        ShowModsSubScreen(ModsHostsScreen, "Hosts-файлы");
        if (_modsLoaded) RefreshModsLists();

        _ = AutoHostsModService.UpdateAutoModsMetadataAsync().ContinueWith(_ =>
            Dispatcher.Invoke(() => { if (_modsLoaded) RefreshModsLists(); }));
        _ = AutoHostsModService.RefreshAutoModFilesAsync();

        if (!_hostsWarningShown)
        {
            _hostsWarningShown = true;
            ShowHostsWarningDialog(() => { });
        }
    }

    private void ModsCardMyMods_Click(object s, RoutedEventArgs e)
    {
        ShowModsSubScreen(ModsMyModsScreen, "Ваши моды");
        if (_modsLoaded) RefreshMyMods();
    }

    private void ModsCardEditor_Click(object s, RoutedEventArgs e)
    {
        ShowModsSubScreen(ModsEditorScreen, "Редактор");
        LoadEditorFileLists();
    }

    private void RefreshMods()
    {
        var activeStrategy = _settings.ActiveStrategyMods ?? [];
        var activeLists = _settings.ActiveListMods ?? [];
        var activeHosts = _settings.ActiveHostsMods ?? [];
        _allMods = ModScanner.ScanAll(activeStrategy, activeLists, activeHosts);
        RefreshModsLists();
        SyncActiveStrategyMods();
    }

    private void SyncActiveStrategyMods()
    {
        if (string.IsNullOrEmpty(_settings.ZapretPath)) return;

        ZapretConfigService.RemoveAllModConfigs();

        foreach (var mod in _allMods.Where(m => m.Type == ModType.Strategy && m.IsActive))
        {
            var dirName = ModScanner.GetModDirName(mod);
            ZapretConfigService.InstallModBat(_settings.ZapretPath, mod.FolderPath, dirName);
            ZapretConfigService.InjectModConfig(_settings.ZapretPath, mod.Name, dirName);
        }
    }

    private void RefreshModsLists()
    {
        var activeNames = _currentModsTab switch
        {
            ModType.Strategy => _settings.ActiveStrategyMods ?? [],
            ModType.List => _settings.ActiveListMods ?? [],
            ModType.Hosts => _settings.ActiveHostsMods ?? [],
            _ => []
        };

        var activeMods = _allMods
            .Where(m => m.Type == _currentModsTab && m.IsActive)
            .OrderBy(m => { var idx = activeNames.IndexOf(ModScanner.GetModDirName(m)); return idx < 0 ? 999 : idx; })
            .ToList();

        var availableMods = _allMods
            .Where(m => m.Type == _currentModsTab && !m.IsActive)
            .ToList();

        if (_currentModsTab == ModType.Strategy)
        {
            AvailableList.ItemsSource = availableMods;
            ActiveList.ItemsSource = activeMods;
            AvailableList.SelectedItem = null;
            ActiveList.SelectedItem = null;

            AvailableCount.Text = availableMods.Count.ToString();
            ActiveCount.Text = activeMods.Count.ToString();
            ActiveCount.Foreground = new SolidColorBrush(activeMods.Count > 0
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0x88, 0x88, 0x88));

            ModsApplyBtn.IsEnabled = _strategyDirty;
            ModsApplyBtn.Style = (Style)FindResource(_strategyDirty ? "AccentBtn" : "OutlineBtn");
        }
        else if (_currentModsTab == ModType.List)
        {
            ListsAvailableList.ItemsSource = availableMods;
            ListsActiveList.ItemsSource = activeMods;
            ListsAvailableList.SelectedItem = null;
            ListsActiveList.SelectedItem = null;

            ListsAvailableCount.Text = availableMods.Count.ToString();
            ListsActiveCount.Text = activeMods.Count.ToString();
            ListsActiveCount.Foreground = new SolidColorBrush(activeMods.Count > 0
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0x88, 0x88, 0x88));

            ListsStatusText.Text = $"Листов: {availableMods.Count + activeMods.Count} | Активных: {activeMods.Count}";
            ListsApplyBtn.IsEnabled = _listsDirty;
            ListsApplyBtn.Style = (Style)FindResource(_listsDirty ? "AccentBtn" : "OutlineBtn");
            ResetListsArrows();
        }
        else if (_currentModsTab == ModType.Hosts)
        {
            HostsAvailableList.ItemsSource = availableMods;
            HostsActiveList.ItemsSource = activeMods;
            HostsAvailableList.SelectedItem = null;
            HostsActiveList.SelectedItem = null;

            HostsAvailableCount.Text = availableMods.Count.ToString();
            HostsActiveCount.Text = activeMods.Count.ToString();
            HostsActiveCount.Foreground = new SolidColorBrush(activeMods.Count > 0
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0x88, 0x88, 0x88));

            HostsStatusText.Text = $"Листов Hosts: {availableMods.Count + activeMods.Count} | Активных: {activeMods.Count}";
            HostsApplyBtn.IsEnabled = _hostsDirty;
            HostsApplyBtn.Style = (Style)FindResource(_hostsDirty ? "AccentBtn" : "OutlineBtn");
            ResetHostsArrows();
        }

        UpdateModsStatus();
    }

    private void UpdateModsStatus()
    {
        var currentTab = _currentModsTab;
        var allCount = _allMods.Count(m => m.Type == currentTab);

        var activeCount = currentTab switch
        {
            ModType.Strategy => ActiveList.Items.Count,
            ModType.List => ListsActiveList.Items.Count,
            ModType.Hosts => HostsActiveList.Items.Count,
            _ => 0
        };

        var availCount = currentTab switch
        {
            ModType.Strategy => AvailableList.Items.Count,
            ModType.List => ListsAvailableList.Items.Count,
            ModType.Hosts => HostsAvailableList.Items.Count,
            _ => 0
        };

        ModsHeaderStatus.Text = activeCount > 0
            ? $"ВКЛЮЧЕНО: {activeCount} модов"
            : "Нет активных";
        ModsHeaderStatus.Foreground = new SolidColorBrush(activeCount > 0
            ? Color.FromRgb(0x22, 0xc5, 0x5e)
            : Color.FromRgb(0x88, 0x88, 0x88));

        if (currentTab == ModType.Strategy)
        {
            AvailableCount.Text = availCount.ToString();
            ActiveCount.Text = activeCount.ToString();
            ActiveCount.Foreground = new SolidColorBrush(activeCount > 0
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0x88, 0x88, 0x88));
        }
        else if (currentTab == ModType.List)
        {
            ListsAvailableCount.Text = availCount.ToString();
            ListsActiveCount.Text = activeCount.ToString();
            ListsActiveCount.Foreground = new SolidColorBrush(activeCount > 0
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0x88, 0x88, 0x88));
        }
        else if (currentTab == ModType.Hosts)
        {
            HostsAvailableCount.Text = availCount.ToString();
            HostsActiveCount.Text = activeCount.ToString();
            HostsActiveCount.Foreground = new SolidColorBrush(activeCount > 0
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0x88, 0x88, 0x88));
        }
    }

    private void ResetArrows()
    {
        MoveRightBtn.IsEnabled = false;
        MoveLeftBtn.IsEnabled = false;
        if (MoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (MoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void MoveRightBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingToggleMod is not ModEntry mod)
        {
            return;
        }

        MoveRightBtn.IsEnabled = false;
        MoveLeftBtn.IsEnabled = false;
        if (MoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (MoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));

        _pendingToggleMod = null;
        ToggleModActive(mod, true);
    }

    private void MoveLeftBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingToggleMod is not ModEntry mod)
        {
            return;
        }

        MoveRightBtn.IsEnabled = false;
        MoveLeftBtn.IsEnabled = false;
        if (MoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (MoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));

        _pendingToggleMod = null;
        ToggleModActive(mod, false);
    }

    private void CardToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ModEntry mod)
        {
            ToggleModActive(mod, !mod.IsActive);
        }
    }

    private void ToggleModActive(ModEntry mod, bool activate)
    {
        if (activate && mod.Type == ModType.Hosts)
        {
            var listFile = ModScanner.FindListFile(mod);
            if (listFile != null && File.Exists(listFile))
            {
                try
                {
                    int lineCount = File.ReadLines(listFile).Count(l => !string.IsNullOrWhiteSpace(l));
                    if (lineCount > 10000)
                    {
                        ShowConfirmDialog(
                            "Внимание: Большой список",
                            $"Мод '{mod.Name}' содержит большое количество записей ({lineCount} строк).\n\nБольшое количество строк в файле hosts может сильно замедлить скорость интернета или даже полностью его отключить из-за ограничений службы DNS в Windows.\n\nВы действительно хотите активировать этот мод?",
                            ok =>
                            {
                                if (ok)
                                {
                                    ExecuteToggleModActive(mod, true);
                                }
                                else
                                {
                                    mod.IsActive = false;
                                    RefreshModsLists();
                                    if (ModsMyModsScreen.Visibility == Visibility.Visible)
                                        RefreshMyMods();
                                }
                            },
                            confirmText: "Активировать",
                            confirmIsDestructive: true
                        );
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Ошибка проверки размера мода: {ex.Message}", "error");
                }
            }
        }

        ExecuteToggleModActive(mod, activate);
    }

    private void ExecuteToggleModActive(ModEntry mod, bool activate)
    {
        mod.IsActive = activate;

        List<string> list;
        if (mod.Type == ModType.Strategy)
            list = _settings.ActiveStrategyMods;
        else if (mod.Type == ModType.List)
            list = _settings.ActiveListMods;
        else
            list = _settings.ActiveHostsMods;

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

        SaveModsSettings();
        if (mod.Type == ModType.List) _listsDirty = true;
        else if (mod.Type == ModType.Hosts) _hostsDirty = true;
        else _strategyDirty = true;

        if (mod.Type == ModType.Strategy && !string.IsNullOrEmpty(_settings.ZapretPath))
        {
            if (activate)
            {
                ZapretConfigService.InstallModBat(_settings.ZapretPath, mod.FolderPath, dirName);
                ZapretConfigService.InjectModConfig(_settings.ZapretPath, mod.Name, dirName);
            }
            else
            {
                ZapretConfigService.UninstallModBat(_settings.ZapretPath, dirName);
                ZapretConfigService.RemoveModConfig(_settings.ZapretPath, dirName);
            }
        }

        if (mod.Type == ModType.List)
        {
            var allLists = _allMods.Where(m => m.Type == ModType.List).ToList();
            ModActivator.ApplyListMods(allLists);
        }

        if (mod.Type == ModType.Hosts)
        {
            var allHosts = _allMods.Where(m => m.Type == ModType.Hosts).ToList();
            ModActivator.ApplyHostsMods(allHosts);
        }

        if (ModsMyModsScreen.Visibility == Visibility.Visible)
            RefreshMyMods();
        else
            RefreshModsLists();
    }

    private void RefreshMyMods()
    {
        var activeMods = _allMods.Where(m => m.IsActive).ToList();
        MyModsList.ItemsSource = null;
        MyModsList.ItemsSource = activeMods;
        MyModsEmptyState.Visibility = activeMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MyModsCount.Text = activeMods.Count.ToString();
        MyModsCount.Foreground = activeMods.Count > 0
            ? new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        ModsHeaderStatus.Text = activeMods.Count > 0
            ? $"ВКЛЮЧЕНО: {activeMods.Count} модов"
            : "Нет активных";
        ModsHeaderStatus.Foreground = new SolidColorBrush(activeMods.Count > 0
            ? Color.FromRgb(0x22, 0xc5, 0x5e)
            : Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void MyModsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
    }

    private void RecalcMyModsColumns()
    {
        if (!MyModsList.IsLoaded) return;
        double available = MyModsList.ActualWidth;
        if (available <= 0) return;

        const double targetTile = 280;
        const double gap = 12;
        int cols = Math.Max(1, (int)((available + gap) / (targetTile + gap)));
        var grid = FindVisualChild<UniformGrid>(MyModsList);
        if (grid is not null) grid.Columns = cols;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void ListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            var item = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (item?.DataContext is ModEntry mod)
            {
                _dragMod = mod;
                _dragFromActive = listBox == ActiveList;
                _dragStartPoint = e.GetPosition(null);
                _isDragPending = true;
            }
            else
            { }
        }
    }

    private void ListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragPending || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        var diff = _dragStartPoint - pos;

        if (Math.Abs(diff.X) > 4 || Math.Abs(diff.Y) > 4)
        {
            _isDragPending = false;
            if (_dragMod != null && sender is ListBox listBox)
            {
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
        RemoveDragAdorner();
    }

    private void ActiveList_Drop(object sender, DragEventArgs e)
    {
        if (_dragMod is null) return;
        if (!_dragFromActive)
            ToggleModActive(_dragMod, true);
        _dragMod = null;
        RemoveDragAdorner();
    }

    private void RemoveDragAdorner()
    {
        if (_currentDragAdorner is null) return;
        var layer = AdornerLayer.GetAdornerLayer(_currentDragAdorner.AdornedElement);
        layer?.Remove(_currentDragAdorner);
        _currentDragAdorner = null;
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem)
        {
            try { element = VisualTreeHelper.GetParent(element); }
            catch { break; }
        }
        return element as ListBoxItem;
    }

    private void CreateModBtn_Click(object sender, RoutedEventArgs e)
    {
        var type = _currentModsTab;
        var dialog = new CreateModWindow(type);
        dialog.Owner = this;

        if (dialog.ShowDialog() == true && dialog.CreatedEntry is not null)
        {
            _allMods.Add(dialog.CreatedEntry);
            SaveModsSettings();
            if (dialog.CreatedEntry.IsActive)
            {
                if (_currentModsTab == ModType.Strategy) _strategyDirty = true;
                else if (_currentModsTab == ModType.List) _listsDirty = true;
                else if (_currentModsTab == ModType.Hosts) _hostsDirty = true;
            }
            RefreshModsLists();
            if (ModsMyModsScreen.Visibility == Visibility.Visible)
                RefreshMyMods();
        }
    }

    private void MyModsCreateBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateModWindow(ModType.Strategy);
        dialog.Owner = this;

        if (dialog.ShowDialog() == true && dialog.CreatedEntry is not null)
        {
            _allMods.Add(dialog.CreatedEntry);
            SaveModsSettings();
            if (dialog.CreatedEntry.IsActive) _strategyDirty = true;
            RefreshModsLists();
            RefreshMyMods();
        }
    }

    private async void ModsImportBtn_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            Filter = "NetFix Mod (*.netfix-mod)|*.netfix-mod|All files (*.*)|*.*",
            Title = "Выберите файл мода",
        };

        if (openDialog.ShowDialog() == true)
            await ModsImportModAsync(openDialog.FileName);
    }

    private async Task ModsImportModAsync(string zipPath)
    {
        var (meta, readError) = await ModPackager.ReadModMetaFromArchive(zipPath);
        if (meta is null || readError is not null)
        {
            ShowModsError(readError ?? "Не удалось прочитать файл мода");
            return;
        }

        var importDialog = new ImportModWindow(meta);
        importDialog.Owner = this;

        if (importDialog.ShowDialog() == true)
        {
            var activeStrategy = _settings.ActiveStrategyMods ?? [];
            var activeLists = _settings.ActiveListMods ?? [];
            var activeHosts = _settings.ActiveHostsMods ?? [];
            var (entry, importError) = await ModPackager.ImportAsync(zipPath, activeStrategy, activeLists, activeHosts);

            if (entry is not null)
            {
                _allMods.Add(entry);
                SaveModsSettings();
                if (entry.IsActive)
                {
                    if (entry.Type == ModType.List) _listsDirty = true;
                    else if (entry.Type == ModType.Hosts) _hostsDirty = true;
                    else _strategyDirty = true;
                }
                RefreshModsLists();
                if (ModsMyModsScreen.Visibility == Visibility.Visible)
                    RefreshMyMods();
                ShowModsSuccess($"Мод '{meta.Name}' импортирован");
            }
            else
            {
                ShowModsError(importError ?? "Ошибка импорта");
            }
        }
    }

    private void ModsApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveModsSettings();

        var title = _currentModsTab switch
        {
            ModType.Strategy => "Применение стратегий",
            ModType.List => "Применение списков",
            ModType.Hosts => "Применение Hosts-файла",
            _ => "Применение изменений"
        };
        var message = _currentModsTab switch
        {
            ModType.Strategy => "Сохранить порядок .bat стратегий?",
            ModType.List => "Применить активные списки доменов?",
            ModType.Hosts => "Применить активные Hosts-моды к системному файлу hosts?",
            _ => "Применить изменения?"
        };

        ShowConfirmDialog(title, message, ok =>
        {
            if (!ok) return;

            if (_currentModsTab == ModType.List)
            {
                var allLists = _allMods.Where(m => m.Type == ModType.List).ToList();
                var (success, error) = ModActivator.ApplyListMods(allLists);
                if (!success)
                    ShowModsError(error ?? "Ошибка применения");
                else
                    ShowModsSuccess("Списки доменов применены");
                _listsDirty = false;
            }
            else if (_currentModsTab == ModType.Hosts)
            {
                var allHosts = _allMods.Where(m => m.Type == ModType.Hosts).ToList();
                var activeAutoMods = allHosts.Where(m => m.IsActive).ToList();

                ModsStatusText.Text = "Загрузка авто-списков...";
                ModsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

                _ = AutoHostsModService.DownloadAutoModsFilesAsync(activeAutoMods).ContinueWith(t =>
                    Dispatcher.Invoke(() =>
                    {
                        var (success, error) = ModActivator.ApplyHostsMods(allHosts);
                        if (!success)
                            ShowModsError(error ?? "Ошибка применения");
                        else
                            ShowModsSuccess("Hosts-файлы применены");
                        _hostsDirty = false;
                        RefreshModsLists();
                    }));
                return;
            }
            else
            {
                ShowModsSuccess("Порядок стратегий сохранён");
                _strategyDirty = false;
            }

            RefreshModsLists();
        }, confirmText: "Применить", confirmIsDestructive: false);
    }

    private void MyModsApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveModsSettings();
        ShowConfirmDialog("Применение модов", "Применить активные моды?", ok =>
        {
            if (!ok) return;

            var allLists = _allMods.Where(m => m.Type == ModType.List).ToList();
            ModActivator.ApplyListMods(allLists);
            _listsDirty = false;

            var allHosts = _allMods.Where(m => m.Type == ModType.Hosts).ToList();
            ModActivator.ApplyHostsMods(allHosts);
            _hostsDirty = false;
        }, confirmText: "Применить", confirmIsDestructive: false);
    }

    private void RefreshMods_Click(object sender, RoutedEventArgs e) => RefreshMods();

    private void AvailableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _isDragPending = false;
        _pendingToggleMod = e.AddedItems.Count > 0 ? e.AddedItems[0] as ModEntry : null;

        if (_pendingToggleMod is null) return;

        ActiveList.SelectedItem = null;
        MoveRightBtn.IsEnabled = true;
        MoveLeftBtn.IsEnabled = false;
        if (MoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        if (MoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void ActiveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _isDragPending = false;
        _pendingToggleMod = e.AddedItems.Count > 0 ? e.AddedItems[0] as ModEntry : null;

        if (_pendingToggleMod is null) return;

        AvailableList.SelectedItem = null;
        MoveRightBtn.IsEnabled = false;
        MoveLeftBtn.IsEnabled = true;
        if (MoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (MoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x50, 0x90, 0xd0));
    }

    private async void ExportSingleMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ModEntry mod) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = mod.Name,
            DefaultExt = ".netfix-mod",
            Filter = "NetFix Mod (*.netfix-mod)|*.netfix-mod|ZIP Archive (*.zip)|*.zip"
        };

        if (dlg.ShowDialog() != true) return;

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"NetFix_Export_{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = await ModPackager.ExportAsync(mod, dir);
            if (result is not null && File.Exists(result))
            {
                File.Copy(result, dlg.FileName!, overwrite: true);
                ModsStatusText.Text = $"✅ Экспортировано: {System.IO.Path.GetFileName(dlg.FileName)}";
                ModsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private void EditModCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ModEntry mod) return;
        ShowModsSubScreen(ModsEditorScreen, "Редактор");
        LoadEditorFileLists(mod.FolderPath);
    }

    private void OpenModLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ModEntry mod) return;
        if (Directory.Exists(mod.FolderPath))
            Process.Start("explorer.exe", mod.FolderPath);
    }

    private void MyModCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ModEntry mod) return;
        ShowModsSubScreen(ModsEditorScreen, "Редактор");
        LoadEditorFileLists(mod.FolderPath);
    }

    private void EditorOpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (ModsEditorFileList.SelectedItem is FileListItem item && !string.IsNullOrEmpty(item.FilePath))
        {
            var dir = System.IO.Path.GetDirectoryName(item.FilePath);
            if (Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
        }
    }

    private async void EditorAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Создать новый файл",
            Filter = "Все поддерживаемые (*.bat;*.txt)|*.bat;*.txt",
            InitialDirectory = @"C:\Zapret",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, "");
            LoadEditorFileLists();

            for (int i = 0; i < ModsEditorFileList.Items.Count; i++)
            {
                if (ModsEditorFileList.Items[i] is FileListItem fi && fi.FilePath == dialog.FileName)
                {
                    ModsEditorFileList.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void DeleteModCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ModEntry mod) return;
        if (mod.IsAutoMod) return;

        ShowConfirmDialog(
            "Удалить мод?",
            $"Мод «{mod.Name}» будет удалён безвозвратно.",
            confirmed =>
            {
                if (!confirmed) return;

                var removedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? targetPath = null;
                if (mod.Type == ModType.List)
                {
                    var listFile = ModScanner.FindListFile(mod);
                    if (listFile is not null && File.Exists(listFile))
                    {
                        foreach (var line in File.ReadAllLines(listFile))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                                removedDomains.Add(trimmed);
                        }
                    }
                    var name = mod.TargetFile;
                    if (string.IsNullOrEmpty(name)) name = "list-general.txt";
                    targetPath = Path.Combine(@"C:\Zapret", "lists", name);
                }

                try
                {
                    if (Directory.Exists(mod.FolderPath))
                        Directory.Delete(mod.FolderPath, true);
                }
                catch { }

                _allMods.Remove(mod);
                SaveModsSettings();
                if (mod.IsActive)
                {
                    if (mod.Type == ModType.List) _listsDirty = true;
                    else if (mod.Type == ModType.Hosts) _hostsDirty = true;
                    else _strategyDirty = true;
                }

                if (removedDomains.Count > 0 && targetPath is not null && File.Exists(targetPath))
                {
                    var lines = File.ReadAllLines(targetPath);
                    var result = new List<string>();
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith('#') || !removedDomains.Contains(trimmed))
                            result.Add(trimmed);
                    }
                    File.WriteAllLines(targetPath, result);
                }

                if (mod.Type == ModType.List)
                {
                    var allLists = _allMods.Where(m => m.Type == ModType.List).ToList();
                    ModActivator.ApplyListMods(allLists);
                }
                else if (mod.Type == ModType.Hosts)
                {
                    var allHosts = _allMods.Where(m => m.Type == ModType.Hosts).ToList();
                    ModActivator.ApplyHostsMods(allHosts);
                }

                RefreshModsLists();
                RefreshMyMods();
                ShowModsSuccess($"Мод '{mod.Name}' удалён");
            });
    }

    private void SaveModsSettings() => SettingsService.Save(_settings);

    private void ShowModsError(string message)
    {
        ModsStatusText.Text = $"❌ {message}";
        ModsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
    }

    private void ShowModsSuccess(string message)
    {
        ModsStatusText.Text = $"✅ {message}";
        ModsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
    }

    private void OnModCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid)
            grid.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 10, 10);
    }

    private string? _modsEditorFilePath;

    private ListBox? _editorFileList;
    private TextBox? _editorTextBox;
    private Button? _editorResetBtn;
    private Button? _editorSaveBtn;
    private TextBlock? _editorZoomLevel;
    private bool _editorLoaded;

    private sealed record FileListItem(
        string Display,
        string? FilePath,
        bool IsHeader,
        FontWeight FontWeight,
        Brush Foreground,
        Thickness Padding,
        string FontFamily = "Consolas",
        double FontSize = 12
    );

    private void EnsureEditorControls()
    {
        if (_editorLoaded) return;
        _editorLoaded = true;
        _editorFileList = FindName("ModsEditorFileList") as ListBox;
        _editorTextBox = FindName("ModsEditorTextBox") as TextBox;
        _editorResetBtn = FindName("ModsEditorResetBtn") as Button;
        _editorSaveBtn = FindName("ModsEditorSaveBtn") as Button;
        _editorZoomLevel = FindName("EditorZoomLevel") as TextBlock;
    }

    private void LoadEditorFileLists(string? folderPath = null)
    {
        EnsureEditorControls();
        if (_editorFileList is null) { ModsStatusText.Text = "❌ Ошибка инициализации редактора"; return; }

        _editorFileList.Items.Clear();

        if (folderPath is not null)
        {
            var items = new List<FileListItem>();
            items.Add(new FileListItem("← Назад", null, false,
                FontWeights.Normal, new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                new Thickness(12, 6, 12, 6), "Segoe UI", 12));
            AddFolderFiles(items, folderPath);
            foreach (var item in items)
                _editorFileList.Items.Add(item);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is FileListItem fi && !fi.IsHeader && fi.FilePath is not null)
                { _editorFileList.SelectedIndex = i; break; }
            }
            return;
        }

        AddFolderFiles(_editorFileList.Items, @"C:\Zapret\lists", "Листы:", "*.txt");
        AddFolderFiles(_editorFileList.Items, @"C:\Zapret", "Bat файлы:", "*.bat");

        AddModFolders(_editorFileList.Items);

        var hostsPath = Services.Mods.ModActivator.GetSystemHostsPath();
        if (File.Exists(hostsPath))
        {
            _editorFileList.Items.Add(MakeHeader("Системный hosts:"));
            _editorFileList.Items.Add(MakeFile("hosts", hostsPath));
        }

        if (_editorFileList.Items.Count > 0)
        {
            for (int i = 0; i < _editorFileList.Items.Count; i++)
            {
                if (_editorFileList.Items[i] is FileListItem fi && !fi.IsHeader)
                {
                    _editorFileList.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private static FileListItem MakeHeader(string text) => new(
        Display: text,
        FilePath: null,
        IsHeader: true,
        FontFamily: "Segoe UI",
        FontSize: 11,
        FontWeight: FontWeights.Bold,
        Foreground: new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x69)),
        Padding: new Thickness(12, 10, 12, 4)
    );

    private static FileListItem MakeSubHeader(string text) => new(
        Display: text,
        FilePath: null,
        IsHeader: true,
        FontFamily: "Segoe UI",
        FontSize: 10,
        FontWeight: FontWeights.SemiBold,
        Foreground: new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x7a)),
        Padding: new Thickness(20, 6, 12, 2)
    );

    private static FileListItem MakeFile(string fileName, string fullPath) => new(
        Display: fileName,
        FilePath: fullPath,
        IsHeader: false,
        FontFamily: "Consolas",
        FontSize: 12,
        FontWeight: FontWeights.Normal,
        Foreground: new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
        Padding: new Thickness(24, 6, 12, 6)
    );

    private static void AddFolderFiles(System.Collections.IList items, string dir, string? header = null, string? pattern = null)
    {
        if (!Directory.Exists(dir)) return;

        if (header is null && pattern is null)
        {
            foreach (var f in Directory.GetFiles(dir).OrderBy(f => System.IO.Path.GetFileName(f)))
                items.Add(MakeFile(System.IO.Path.GetFileName(f), f));
            return;
        }

        if (header is not null)
            items.Add(MakeHeader(header));
        if (pattern is not null)
        {
            foreach (var f in Directory.GetFiles(dir, pattern)
                .Where(f => !System.IO.Path.GetFileName(f).Equals("service.bat", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => System.IO.Path.GetFileName(f)))
                items.Add(MakeFile(System.IO.Path.GetFileName(f), f));
        }
    }

    private static void AddModFolders(System.Collections.IList items)
    {
        var modsDir = Path.Combine(AppContext.BaseDirectory, "Mods");
        if (!Directory.Exists(modsDir)) return;

        items.Add(MakeHeader("Моды:"));

        var strategiesDir = Path.Combine(modsDir, "strategies");
        if (Directory.Exists(strategiesDir))
        {
            items.Add(MakeSubHeader("strategies:"));
            foreach (var dir in Directory.GetDirectories(strategiesDir).OrderBy(d => System.IO.Path.GetFileName(d)))
            {
                var name = System.IO.Path.GetFileName(dir);
                items.Add(MakeFile(name, dir));
            }
        }

        var listsDir = Path.Combine(modsDir, "lists");
        if (Directory.Exists(listsDir))
        {
            items.Add(MakeSubHeader("lists:"));
            foreach (var dir in Directory.GetDirectories(listsDir).OrderBy(d => System.IO.Path.GetFileName(d)))
            {
                var name = System.IO.Path.GetFileName(dir);
                items.Add(MakeFile(name, dir));
            }
        }

        var hostsDir = Path.Combine(modsDir, "hosts");
        if (Directory.Exists(hostsDir))
        {
            items.Add(MakeSubHeader("hosts:"));
            foreach (var dir in Directory.GetDirectories(hostsDir).OrderBy(d => System.IO.Path.GetFileName(d)))
            {
                var name = System.IO.Path.GetFileName(dir);
                items.Add(MakeFile(name, dir));
            }
        }
    }

    private void ModsEditorFileList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        EnsureEditorControls();
        if (_editorFileList?.SelectedItem is not FileListItem fi || fi.IsHeader)
        {
            if (_editorTextBox is not null)
                _editorTextBox.Text = "";
            return;
        }

        if (fi.Display == "← Назад")
        {
            LoadEditorFileLists();
            return;
        }

        if (fi.FilePath is null)
        {
            if (_editorTextBox is not null)
                _editorTextBox.Text = "";
            return;
        }

        if (Directory.Exists(fi.FilePath))
        {
            LoadEditorFileLists(fi.FilePath);
            return;
        }

        _modsEditorFilePath = fi.FilePath;

        try
        {
            if (_editorTextBox is not null)
                _editorTextBox.Text = File.ReadAllText(_modsEditorFilePath, Encoding.UTF8);
        }
        catch
        {
            if (_editorTextBox is not null)
                _editorTextBox.Text = "Ошибка чтения файла";
        }
    }

    private void ModsEditorSaveBtn_Click(object s, RoutedEventArgs e)
    {
        EnsureEditorControls();
        if (_modsEditorFilePath is null || _editorTextBox is null) return;

        ShowConfirmDialog(
            "Сохранение",
            $"Сохранить изменения в «{System.IO.Path.GetFileName(_modsEditorFilePath)}»?",
            ok =>
            {
                if (!ok) return;
                try
                {
                    File.WriteAllText(_modsEditorFilePath, _editorTextBox.Text, Encoding.UTF8);

                    var editedMod = _allMods.FirstOrDefault(m =>
                        _modsEditorFilePath.StartsWith(m.FolderPath, StringComparison.OrdinalIgnoreCase));

                    if (editedMod is not null)
                    {
                        if (editedMod.Type == ModType.List)
                        {
                            var allLists = _allMods.Where(m => m.Type == ModType.List).ToList();
                            var (success, error) = ModActivator.ApplyListMods(allLists);
                            if (!success)
                                ModsStatusText.Text = $"⚠️ Сохранено, но ошибка применения: {error}";
                        }
                        else if (editedMod.Type == ModType.Hosts)
                        {
                            var allHosts = _allMods.Where(m => m.Type == ModType.Hosts).ToList();
                            var (success, error) = ModActivator.ApplyHostsMods(allHosts);
                            if (!success)
                                ModsStatusText.Text = $"⚠️ Сохранено, но ошибка применения: {error}";
                        }
                    }

                    var strategyNames = _settings.ActiveStrategyMods ?? [];
                    var listNames = _settings.ActiveListMods ?? [];
                    var hostsNames = _settings.ActiveHostsMods ?? [];
                    _allMods = ModScanner.ScanAll(strategyNames, listNames, hostsNames);
                    SyncActiveStrategyMods();
                    RefreshModsLists();
                    if (ModsMyModsScreen.Visibility == Visibility.Visible)
                        RefreshMyMods();

                    ModsStatusText.Text = $"✅ Сохранено и применено: {System.IO.Path.GetFileName(_modsEditorFilePath)}";
                    ModsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                }
                catch (Exception ex)
                {
                    ModsStatusText.Text = $"❌ Ошибка: {ex.Message}";
                    ModsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                }
            },
            confirmText: "Сохранить",
            confirmIsDestructive: false);
    }

    private void ModsEditorResetBtn_Click(object s, RoutedEventArgs e)
    {
        EnsureEditorControls();
        if (_modsEditorFilePath is null || _editorTextBox is null) return;

        ShowConfirmDialog(
            "Сброс",
            "Сбросить изменения? Все несохранённые изменения будут потеряны.",
            ok =>
            {
                if (!ok) return;
                try
                {
                    _editorTextBox.Text = File.ReadAllText(_modsEditorFilePath, Encoding.UTF8);
                }
                catch { }
            },
            confirmText: "Сбросить");
    }

    private void UpdateZoomDisplay()
    {
        if (_editorTextBox is not null && _editorZoomLevel is not null)
            _editorZoomLevel.Text = _editorTextBox.FontSize.ToString("0.0");
    }

    private void EditorZoomIn_Click(object sender, RoutedEventArgs e)
    {
        EnsureEditorControls();
        if (_editorTextBox is null) return;
        _editorTextBox.FontSize = Math.Min(40, _editorTextBox.FontSize + 1);
        UpdateZoomDisplay();
    }

    private void EditorZoomOut_Click(object sender, RoutedEventArgs e)
    {
        EnsureEditorControls();
        if (_editorTextBox is null) return;
        _editorTextBox.FontSize = Math.Max(6, _editorTextBox.FontSize - 1);
        UpdateZoomDisplay();
    }

    private void ModsEditorTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _editorTextBox is null) return;
        _editorTextBox.FontSize = Math.Clamp(
            _editorTextBox.FontSize + (e.Delta > 0 ? 1 : -1), 6, 40);
        UpdateZoomDisplay();
        e.Handled = true;
    }

    private void ResetListsArrows()
    {
        ListsMoveRightBtn.IsEnabled = false;
        ListsMoveLeftBtn.IsEnabled = false;
        if (ListsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (ListsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void ListsMoveRightBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingToggleMod is not ModEntry mod)
        {
            return;
        }

        ListsMoveRightBtn.IsEnabled = false;
        ListsMoveLeftBtn.IsEnabled = false;
        if (ListsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (ListsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));

        _pendingToggleMod = null;
        ToggleModActive(mod, true);
    }

    private void ListsMoveLeftBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingToggleMod is not ModEntry mod)
        {
            return;
        }

        ListsMoveRightBtn.IsEnabled = false;
        ListsMoveLeftBtn.IsEnabled = false;
        if (ListsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (ListsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));

        _pendingToggleMod = null;
        ToggleModActive(mod, false);
    }

    private void ListsAvailableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _isDragPending = false;
        _pendingToggleMod = e.AddedItems.Count > 0 ? e.AddedItems[0] as ModEntry : null;

        if (_pendingToggleMod is null) return;

        ListsActiveList.SelectedItem = null;
        ListsMoveRightBtn.IsEnabled = true;
        ListsMoveLeftBtn.IsEnabled = false;
        if (ListsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        if (ListsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void ListsActiveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _isDragPending = false;
        _pendingToggleMod = e.AddedItems.Count > 0 ? e.AddedItems[0] as ModEntry : null;

        if (_pendingToggleMod is null) return;

        ListsAvailableList.SelectedItem = null;
        ListsMoveRightBtn.IsEnabled = false;
        ListsMoveLeftBtn.IsEnabled = true;
        if (ListsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (ListsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x50, 0x90, 0xd0));
    }

    private void ListsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            var item = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (item?.DataContext is ModEntry mod)
            {
                _dragMod = mod;
                _dragFromActive = listBox == ListsActiveList;
                _dragStartPoint = e.GetPosition(null);
                _isDragPending = true;
            }
            else
            { }
        }
    }

    private void ListsAvailableList_Drop(object sender, DragEventArgs e)
    {
        if (_dragMod is null) return;
        if (_dragFromActive)
            ToggleModActive(_dragMod, false);
        _dragMod = null;
        RemoveDragAdorner();
    }

    private void ListsActiveList_Drop(object sender, DragEventArgs e)
    {
        if (_dragMod is null) return;
        if (!_dragFromActive)
            ToggleModActive(_dragMod, true);
        _dragMod = null;
        RemoveDragAdorner();
    }
    private void ListsCreateBtn_Click(object sender, RoutedEventArgs e)
    {
        _currentModsTab = ModType.List;
        CreateModBtn_Click(sender, e);
    }

    private void HostsCreateBtn_Click(object sender, RoutedEventArgs e)
    {
        _currentModsTab = ModType.Hosts;
        CreateModBtn_Click(sender, e);
    }

    private async Task ImportModInternalAsync(ModType defaultType)
    {
        var openDialog = new OpenFileDialog
        {
            Filter = "NetFix Mod (*.netfix-mod)|*.netfix-mod|All files (*.*)|*.*",
            Title = "Выберите файл мода",
        };

        var statusText = defaultType == ModType.Hosts ? HostsStatusText : ListsStatusText;

        if (openDialog.ShowDialog() == true)
        {
            var (meta, readError) = await ModPackager.ReadModMetaFromArchive(openDialog.FileName);
            if (meta is null || readError is not null)
            {
                statusText.Text = $"Ошибка: {readError ?? "Не удалось прочитать"}";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                return;
            }

            var importDialog = new ImportModWindow(meta);
            importDialog.Owner = this;

            if (importDialog.ShowDialog() == true)
            {
                var activeStrategy = _settings.ActiveStrategyMods ?? [];
                var activeLists = _settings.ActiveListMods ?? [];
                var activeHosts = _settings.ActiveHostsMods ?? [];
                var (entry, importError) = await ModPackager.ImportAsync(openDialog.FileName, activeStrategy, activeLists, activeHosts);

                if (entry is not null)
                {
                    _allMods.Add(entry);
                    SaveModsSettings();
                    if (entry.IsActive)
                    {
                        if (entry.Type == ModType.List) _listsDirty = true;
                        else if (entry.Type == ModType.Hosts) _hostsDirty = true;
                        else _strategyDirty = true;
                    }
                    RefreshModsLists();
                    statusText.Text = $"✅ Мод '{meta.Name}' импортирован";
                    statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                }
                else
                {
                    statusText.Text = $"❌ {importError ?? "Ошибка импорта"}";
                    statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                }
            }
        }
    }

    private async void ListsImportBtn_Click(object sender, RoutedEventArgs e)
    {
        await ImportModInternalAsync(ModType.List);
    }

    private async void HostsImportBtn_Click(object sender, RoutedEventArgs e)
    {
        await ImportModInternalAsync(ModType.Hosts);
    }

    private void HostsOpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        var dir = Path.GetDirectoryName(hostsPath);
        if (Directory.Exists(dir))
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{hostsPath}\"");
            }
            catch
            {
                Process.Start("explorer.exe", dir);
            }
        }
    }

    private void HostsResetBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowConfirmDialog(
            "Пересоздание Hosts-файла",
            "Вы уверены, что хотите пересоздать системный файл hosts? Все активные hosts-моды будут отключены и перенесены в доступные, а текущие записи в файле hosts стерты и заменены стандартным шаблоном.",
            ok =>
            {
                if (!ok) return;
                try
                {
                    var hostsPath = ModActivator.GetSystemHostsPath();
                    var dir = Path.GetDirectoryName(hostsPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var allHosts = _allMods.Where(m => m.Type == ModType.Hosts).ToList();
                    foreach (var mod in allHosts)
                    {
                        mod.IsActive = false;
                    }
                    _settings.ActiveHostsMods.Clear();
                    SaveModsSettings();

                    var sb = new StringBuilder();
                    sb.AppendLine("# Copyright (c) 1993-2009 Microsoft Corp.");
                    sb.AppendLine("#");
                    sb.AppendLine("# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.");
                    sb.AppendLine("#");
                    sb.AppendLine("# This file contains the mappings of IP addresses to host names. Each");
                    sb.AppendLine("# entry should be kept on an individual line. The IP address should");
                    sb.AppendLine("# be placed in the first column followed by the corresponding host name.");
                    sb.AppendLine("# The IP address and the host name should be separated by at least one");
                    sb.AppendLine("# space.");
                    sb.AppendLine("#");
                    sb.AppendLine("# Additionally, comments (such as these) may be inserted on individual");
                    sb.AppendLine("# lines or following the machine name denoted by a '#' symbol.");
                    sb.AppendLine("#");
                    sb.AppendLine("# For example:");
                    sb.AppendLine("#");
                    sb.AppendLine("#      102.54.94.97     rhino.acme.com          # source server");
                    sb.AppendLine("#       38.25.63.10     x.acme.com              # x client host");
                    sb.AppendLine();
                    sb.AppendLine("# localhost name resolution is handled within DNS itself.");
                    sb.AppendLine("#	127.0.0.1       localhost");
                    sb.AppendLine("#	::1             localhost");
                    sb.AppendLine();
                    sb.AppendLine("149.154.167.220 my.telegram.org");
                    sb.AppendLine("149.154.167.220 oauth.telegram.org");
                    sb.AppendLine("149.154.167.220 cdn.telesco.pe");
                    sb.AppendLine("149.154.167.220 cdn1.telesco.pe");
                    sb.AppendLine("149.154.167.220 cdn2.telesco.pe");
                    sb.AppendLine("149.154.167.220 cdn3.telesco.pe");
                    sb.AppendLine("149.154.167.220 cdn4.telesco.pe");
                    sb.AppendLine("149.154.167.220 cdn5.telesco.pe");
                    sb.AppendLine("149.154.167.220 core.telegram.org");
                    sb.AppendLine("149.154.167.220 zws4.web.telegram.org");
                    sb.AppendLine("149.154.167.220 vesta.web.telegram.org");
                    sb.AppendLine("149.154.167.220 vesta-1.web.telegram.org");
                    sb.AppendLine("149.154.167.220 venus-1.web.telegram.org");
                    sb.AppendLine("149.154.167.220 telegram.me");
                    sb.AppendLine("149.154.167.220 telegram.dog");
                    sb.AppendLine("149.154.167.220 telegram.space");
                    sb.AppendLine("149.154.167.220 telesco.pe");
                    sb.AppendLine("149.154.167.220 tg.dev");
                    sb.AppendLine("149.154.167.220 telegram.org");
                    sb.AppendLine("149.154.167.220 t.me");
                    sb.AppendLine("149.154.167.220 api.telegram.org");
                    sb.AppendLine("149.154.167.220 td.telegram.org");
                    sb.AppendLine("149.154.167.220 venus.web.telegram.org");
                    sb.AppendLine("149.154.167.220 web.telegram.org");
                    sb.AppendLine("149.154.167.220 kws2-1.web.telegram.org");
                    sb.AppendLine("149.154.167.220 kws2.web.telegram.org");
                    sb.AppendLine("149.154.167.220 kws4-1.web.telegram.org");
                    sb.AppendLine("149.154.167.220 kws4.web.telegram.org");
                    sb.AppendLine("149.154.167.220 zws2-1.web.telegram.org");
                    sb.AppendLine("149.154.167.220 zws2.web.telegram.org");
                    sb.AppendLine("149.154.167.220 zws4-1.web.telegram.org");
                    sb.AppendLine();
                    sb.AppendLine("185.199.109.133 raw.githubusercontent.com");
                    sb.AppendLine("185.199.109.133 release-assets.githubusercontent.com");
                    sb.AppendLine("185.199.108.133 private-user-images.githubusercontent.com");
                    sb.AppendLine("185.199.108.133 gist.githubusercontent.com");
                    sb.AppendLine("185.199.108.133 avatars.githubusercontent.com");
                    sb.AppendLine();
                    for (int i = 10000; i <= 10199; i++)
                    {
                        sb.AppendLine($"104.25.158.178 finland{i}.discord.media");
                    }

                    File.WriteAllText(hostsPath, sb.ToString(), Encoding.UTF8);

                    HostsStatusText.Text = "✅ Hosts-файл успешно пересоздан, все моды деактивированы";
                    HostsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    _hostsDirty = false;
                    RefreshModsLists();
                    if (ModsMyModsScreen.Visibility == Visibility.Visible)
                        RefreshMyMods();
                }
                catch (Exception ex)
                {
                    HostsStatusText.Text = "❌ Ошибка: " + ex.Message;
                    HostsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                }
            },
            confirmText: "Пересоздать",
            confirmIsDestructive: true
        );
    }

    private void ListsApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveModsSettings();

        ShowConfirmDialog("Применение списков", "Применить активные списки доменов?", ok =>
        {
            if (!ok) return;

            var allLists = _allMods.Where(m => m.Type == ModType.List).ToList();
            var (success, error) = ModActivator.ApplyListMods(allLists);
            if (!success)
            {
                ListsStatusText.Text = $"❌ {error ?? "Ошибка применения"}";
                ListsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            }
            else
            {
                ListsStatusText.Text = "✅ Списки доменов применены";
                ListsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            }
            _listsDirty = false;
            RefreshModsLists();
        }, confirmText: "Применить", confirmIsDestructive: false);
    }

    private void HostsApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveModsSettings();

        ShowConfirmDialog("Применение Hosts-файла", "Применить активные Hosts-моды к системному файлу hosts?", ok =>
        {
            if (!ok) return;

            var allHosts = _allMods.Where(m => m.Type == ModType.Hosts).ToList();
            var (success, error) = ModActivator.ApplyHostsMods(allHosts);
            if (!success)
            {
                HostsStatusText.Text = $"❌ {error ?? "Ошибка применения"}";
                HostsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            }
            else
            {
                HostsStatusText.Text = "✅ Hosts-файлы применены";
                HostsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            }
            _hostsDirty = false;
            RefreshModsLists();
        }, confirmText: "Применить", confirmIsDestructive: false);
    }

    private void ResetHostsArrows()
    {
        HostsMoveRightBtn.IsEnabled = false;
        HostsMoveLeftBtn.IsEnabled = false;
        if (HostsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (HostsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void HostsMoveRightBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingToggleMod is not ModEntry mod)
        {
            return;
        }

        HostsMoveRightBtn.IsEnabled = false;
        HostsMoveLeftBtn.IsEnabled = false;
        if (HostsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (HostsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));

        _pendingToggleMod = null;
        ToggleModActive(mod, true);
    }

    private void HostsMoveLeftBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingToggleMod is not ModEntry mod)
        {
            return;
        }

        HostsMoveRightBtn.IsEnabled = false;
        HostsMoveLeftBtn.IsEnabled = false;
        if (HostsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (HostsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));

        _pendingToggleMod = null;
        ToggleModActive(mod, false);
    }

    private void HostsAvailableList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _isDragPending = false;
        _pendingToggleMod = e.AddedItems.Count > 0 ? e.AddedItems[0] as ModEntry : null;

        if (_pendingToggleMod is null) return;

        HostsActiveList.SelectedItem = null;
        HostsMoveRightBtn.IsEnabled = true;
        HostsMoveLeftBtn.IsEnabled = false;
        if (HostsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        if (HostsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
    }

    private void HostsActiveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _isDragPending = false;
        _pendingToggleMod = e.AddedItems.Count > 0 ? e.AddedItems[0] as ModEntry : null;

        if (_pendingToggleMod is null) return;

        HostsAvailableList.SelectedItem = null;
        HostsMoveRightBtn.IsEnabled = false;
        HostsMoveLeftBtn.IsEnabled = true;
        if (HostsMoveRightBtn.Content is System.Windows.Shapes.Path rp)
            rp.Stroke = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58));
        if (HostsMoveLeftBtn.Content is System.Windows.Shapes.Path lp)
            lp.Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
    }

    private void HostsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            var item = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (item?.DataContext is ModEntry mod)
            {
                _dragMod = mod;
                _dragFromActive = listBox == HostsActiveList;
                _dragStartPoint = e.GetPosition(null);
                _isDragPending = true;
            }
            else
            { }
        }
    }

    private void HostsAvailableList_Drop(object sender, DragEventArgs e)
    {
        if (_dragMod is null) return;
        if (_dragFromActive)
            ToggleModActive(_dragMod, false);
        _dragMod = null;
        RemoveDragAdorner();
    }

    private void HostsActiveList_Drop(object sender, DragEventArgs e)
    {
        if (_dragMod is null) return;
        if (!_dragFromActive)
            ToggleModActive(_dragMod, true);
        _dragMod = null;
        RemoveDragAdorner();
    }

    private void UpdateComponentsBtn_Click(object s, RoutedEventArgs e)
    {
        ShowUpdateComponentsDialog();
    }

    private async void FixServiceBtn_Click(object s, RoutedEventArgs e)
    {
        var serviceBat = @"C:\Zapret\service.bat";
        if (!File.Exists(serviceBat))
        {
            AppendLog("service.bat не найден в C:\\Zapret", "error");
            return;
        }

        try
        {
            var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{serviceBat}\" admin")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardInput = true
            });

            await Task.Delay(3000);

            if (proc is { HasExited: false })
                await proc.StandardInput.WriteLineAsync("0");

            await Task.Delay(500);

            ZapretToggleBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка: {ex.Message}", "error");
        }
    }

    private void TrackZapretStartFail()
    {
        _zapretToggleFails++;
        if (_zapretToggleFails >= 2)
        {
            _zapretToggleFails = 0;
            AppendLog("2 неудачных попытки запуска — запускаю Исправить", "warn");
            FixServiceBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private void TrackMainZapretStartFail()
    {
        _mainZapretStartFails++;
        if (_mainZapretStartFails >= 2)
        {
            _mainZapretStartFails = 0;
            AppendLog("2 неудачных попытки запуска с главной кнопки — запускаю Исправить", "warn");
            FixServiceBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private void ShowUpdateComponentsDialog()
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        var dialogCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 560,
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

        cardContent.Children.Add(new TextBlock
        {
            Text = "Приложение скачает и установит последние версии Zapret и TgWsProxy.\n\n" +
                   "Это может занять несколько секунд. Существующие файлы будут обновлены.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 12)
        });

        cardContent.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, 251, 191, 36)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 251, 191, 36)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 24),
            Child = CreateWarningGrid()
        });

        var preserveListsCB = new System.Windows.Controls.CheckBox
        {
            Content = "Не обновлять файлы: ipset-exclude-user.txt и другие -user файлы",
            IsChecked = false,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 20),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        preserveListsCB.Style = (Style)FindResource("Toggle");
        cardContent.Children.Add(preserveListsCB);

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

            CloseServicesPanel();

            await RunAutoInstallAsync(preserveListsCB.IsChecked == true);
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

        overlay.MouseLeftButtonDown += (s, e) =>
        {
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
        };
    }

    private static System.Windows.Controls.Grid CreateWarningGrid()
    {
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M12,2 L22,20 L2,20 Z M12,9 L12,14 M12,16 L12,18"),
            Stroke = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)),
            StrokeThickness = 2,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)),
            LineHeight = 20,
            Text = "Если Zapret перестал работать, просто нажмите «Обновить».\n" +
                   "Если вы вручную редактировали списки блокировок в C:\\Zapret\\lists и не хотите их потерять, включите галочку ниже."
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return grid;
    }

    private void UpdateSelectedConfigDisplay()
    {
        var cache = ZapretConfigService.LoadCache();
        if (cache != null && !string.IsNullOrEmpty(cache.CurrentConfig))
        {
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

        if (zapretRunning && cache != null && !string.IsNullOrEmpty(cache.CurrentConfig))
        {
            ActiveConfigText.Visibility = Visibility.Visible;

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

    private async Task UpdateVersionStatusAsync()
    {
        try
        {
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

            var versionInfo = await GetDetailedVersionInfoAsync();

            if (versionInfo.allUpToDate)
            {
                VersionStatusIcon.Data = Geometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");
                VersionStatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                VersionStatusTitle.Text = "Компоненты обновлены!";
                VersionStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
            }
            else
            {
                VersionStatusIcon.Data = Geometry.Parse("M12 6v3l4-4-4-4v3c-4.42 0-8 3.58-8 8 0 1.57.46 3.03 1.24 4.26L6.7 14.8c-.45-.83-.7-1.79-.7-2.8 0-3.31 2.69-6 6-6zm6.76 1.74L17.3 9.2c.44.84.7 1.79.7 2.8 0 3.31-2.69 6-6 6v-3l-4 4 4 4v-3c4.42 0 8-3.58 8-8 0-1.57-.46-3.03-1.24-4.26z");
                VersionStatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
                VersionStatusTitle.Text = "Нужно обновить!";
                VersionStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
            }

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
            zapretCurrent = GetInstalledZapretVersion(_settings.ZapretPath) ?? "";
            zapretLatest = await GetLatestGitHubVersionAsync("Flowseal/zapret-discord-youtube") ?? "";

            if (!string.IsNullOrEmpty(zapretLatest) && !string.IsNullOrEmpty(zapretCurrent))
            {
                zapretNeedsUpdate = IsNewerVersion(zapretLatest, zapretCurrent);
            }
        }

        if (tgWsProxyInstalled)
        {
            tgWsProxyCurrent = GetInstalledTgWsProxyVersion(_settings.TgWsProxyPath) ?? "";
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

    private string? GetInstalledZapretVersion(string serviceBatPath)
    {
        try
        {
            var zapretDir = Path.GetDirectoryName(serviceBatPath);
            if (string.IsNullOrEmpty(zapretDir))
                return null;

            var versionFile = Path.Combine(zapretDir, "version.txt");
            if (File.Exists(versionFile))
            {
                var version = File.ReadAllText(versionFile).Trim();
                if (!string.IsNullOrEmpty(version))
                    return version;
            }

            var readmeFiles = Directory.GetFiles(zapretDir, "README*", SearchOption.TopDirectoryOnly);
            foreach (var readme in readmeFiles)
            {
                try
                {
                    var content = File.ReadAllText(readme);
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

            return "установлен";
        }
        catch
        {
            return null;
        }
    }

    private string? GetInstalledTgWsProxyVersion(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir))
                return null;

            var versionFile = Path.Combine(dir, "tgwsproxy_version.txt");
            if (File.Exists(versionFile))
            {
                var version = File.ReadAllText(versionFile).Trim();
                if (!string.IsNullOrEmpty(version))
                    return version;
            }

            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrEmpty(versionInfo.ProductVersion))
            {
                var version = versionInfo.ProductVersion.Trim();
                var parts = version.Split('.');

                int lastNonZero = parts.Length - 1;
                while (lastNonZero > 0 && parts[lastNonZero] == "0")
                {
                    lastNonZero--;
                }

                return string.Join(".", parts.Take(lastNonZero + 1));
            }

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

    private async Task<string?> GetLatestGitHubVersionAsync(string repo)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFix/1.0");

            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = root.GetProperty("tag_name").GetString() ?? "";
            return version;
        }
        catch
        {
            return null;
        }
    }

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

    private async void InitializeVersionFiles()
    {
        try
        {
            if (!string.IsNullOrEmpty(_settings.ZapretPath) && File.Exists(_settings.ZapretPath))
            {
                var zapretDir = Path.GetDirectoryName(_settings.ZapretPath);
                if (!string.IsNullOrEmpty(zapretDir))
                {
                    var versionFile = Path.Combine(zapretDir, "version.txt");

                    if (!File.Exists(versionFile))
                    {
                        try
                        {
                            var latestVersion = await GetLatestGitHubVersionAsync("Flowseal/zapret-discord-youtube");
                            if (!string.IsNullOrEmpty(latestVersion))
                            {
                                File.WriteAllText(versionFile, latestVersion);

                            }
                        }
                        catch { }
                    }
                }
            }

            if (!string.IsNullOrEmpty(_settings.TgWsProxyPath) && File.Exists(_settings.TgWsProxyPath))
            {
                var tgWsDir = Path.GetDirectoryName(_settings.TgWsProxyPath);
                if (!string.IsNullOrEmpty(tgWsDir))
                {
                    var versionFile = Path.Combine(tgWsDir, "tgwsproxy_version.txt");

                    if (!File.Exists(versionFile))
                    {
                        try
                        {
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

                            if (string.IsNullOrEmpty(version))
                            {
                                version = await GetLatestGitHubVersionAsync("Flowseal/tg-ws-proxy");
                            }

                            if (!string.IsNullOrEmpty(version))
                            {
                                File.WriteAllText(versionFile, version);

                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    private void StartLongCheckTimer()
    {
        StopLongCheckTimer();

        _longCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };

        _longCheckTimer.Tick += (s, e) =>
        {
            StopLongCheckTimer();

            if (_checkInProgress && _settings.ShowLongCheckDialog)
            {
                ShowLongCheckDialog();
            }
            else
            {

            }
        };

        _longCheckTimer.Start();
    }

    private void StopLongCheckTimer()
    {
        if (_longCheckTimer != null)
        {
            _longCheckTimer.Stop();
            _longCheckTimer = null;
        }
    }

    private void ShowLongCheckDialog()
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        var dialogCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 560,
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

        overlay.MouseLeftButtonDown += (s, e) =>
        {
            _settings.ShowLongCheckDialog = showAgainCb.IsChecked != true;
            SettingsService.Save(_settings);
            ShowServiceReminderCB.IsChecked = _settings.ShowLongCheckDialog;
            MainGrid.Children.Remove(overlay);
            MainGrid.Children.Remove(dialogCard);
        };
    }

    public enum FallbackDialogResult
    {
        InstallReserve,
        Retry,
        Cancel
    }

    private Task<FallbackDialogResult> ShowFallbackDialogAsync(string componentName, string version, string date, bool hasInternet = true)
    {
        TaskCompletionSource<FallbackDialogResult> tcs = new TaskCompletionSource<FallbackDialogResult>();

        Dispatcher.Invoke(() =>
        {
            Border overlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetRowSpan(overlay, 3);
            MainGrid.Children.Add(overlay);

            Border dialogCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)),
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

            StackPanel cardContent = new StackPanel
            {
                Margin = new Thickness(32, 28, 32, 28)
            };

            Border iconBorder = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(28),
                Background = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)) { Opacity = 0.15 },
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };

            System.Windows.Shapes.Path warningIcon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M12 2L2 22h20L12 2zm1 18h-2v-2h2v2zm0-4h-2v-6h2v6z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)),
                Width = 28,
                Height = 28,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = warningIcon;
            cardContent.Children.Add(iconBorder);

            string title = hasInternet ? "GitHub недоступен" : "Нет подключения к интернету";
            TextBlock titleText = new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            cardContent.Children.Add(titleText);

            string connectionStatusText = hasInternet
                ? $"NetFix не может подключиться к GitHub, чтобы скачать {componentName} напрямую. Это может быть временная блокировка или проблемы с сетью (хотя интернет на другие ресурсы есть).\n\n"
                : $"Отсутствует подключение к интернету. Проверьте ваше сетевое подключение.\n\n";

            TextBlock descText = new TextBlock
            {
                Text = connectionStatusText +
                       $"Можно установить резервную копию — она хранится внутри NetFix и работает так же, но может быть не самой последней версии (актуальна на {date}, версия {version}).\n\n" +
                       "Установить резервную копию?",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 24)
            };
            cardContent.Children.Add(descText);

            StackPanel buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            Button installBtn = new Button
            {
                Content = "Установить",
                MinWidth = 120,
                Padding = new Thickness(20, 0, 20, 0),
                Height = 40,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(6, 0, 6, 0),
                Style = (Style)FindResource("AccentBtn")
            };

            Button retryBtn = new Button
            {
                Content = "Повторить попытку",
                MinWidth = 120,
                Padding = new Thickness(20, 0, 20, 0),
                Height = 40,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(6, 0, 6, 0),
                Style = (Style)FindResource("OutlineBtn")
            };

            Button cancelBtn = new Button
            {
                Content = "Отмена",
                MinWidth = 100,
                Padding = new Thickness(20, 0, 20, 0),
                Height = 40,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(6, 0, 6, 0),
                Style = (Style)FindResource("OutlineBtn")
            };

            void CloseDialog(FallbackDialogResult result)
            {
                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                tcs.TrySetResult(result);
            }

            installBtn.Click += (s, e) => CloseDialog(FallbackDialogResult.InstallReserve);
            retryBtn.Click += (s, e) => CloseDialog(FallbackDialogResult.Retry);
            cancelBtn.Click += (s, e) => CloseDialog(FallbackDialogResult.Cancel);

            buttonPanel.Children.Add(installBtn);
            buttonPanel.Children.Add(retryBtn);
            buttonPanel.Children.Add(cancelBtn);

            cardContent.Children.Add(buttonPanel);

            dialogCard.Child = cardContent;
            MainGrid.Children.Add(dialogCard);

            overlay.Opacity = 0;
            dialogCard.Opacity = 0;
            dialogCard.RenderTransform = new ScaleTransform(0.9, 0.9);
            dialogCard.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            DoubleAnimation scaleIn = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            overlay.BeginAnimation(OpacityProperty, fadeIn);
            dialogCard.BeginAnimation(OpacityProperty, fadeIn);
            ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

            overlay.MouseLeftButtonDown += (s, e) => CloseDialog(FallbackDialogResult.Cancel);
        });

        return tcs.Task;
    }

    private Task<bool> ShowConfirmDialogAsync(string title, string text, string okBtnText = "Да", string cancelBtnText = "Отмена")
    {
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

        Dispatcher.Invoke(() =>
        {
            Border overlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetRowSpan(overlay, 3);
            MainGrid.Children.Add(overlay);

            Border dialogCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)),
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

            StackPanel cardContent = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };

            Border iconBorder = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(28),
                Background = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)) { Opacity = 0.15 },
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            };

            System.Windows.Shapes.Path warningIcon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M12 2L2 22h20L12 2zm1 18h-2v-2h2v2zm0-4h-2v-6h2v6z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)),
                Width = 28,
                Height = 28,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = warningIcon;
            cardContent.Children.Add(iconBorder);

            TextBlock titleText = new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            cardContent.Children.Add(titleText);

            TextBlock descText = new TextBlock
            {
                Text = text,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 24)
            };
            cardContent.Children.Add(descText);

            StackPanel buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            Button okBtn = new Button
            {
                Content = okBtnText,
                MinWidth = 120,
                Padding = new Thickness(20, 0, 20, 0),
                Height = 40,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(6, 0, 6, 0),
                Style = (Style)FindResource("RedAccentBtn")
            };

            Button cancelBtn = new Button
            {
                Content = cancelBtnText,
                MinWidth = 120,
                Padding = new Thickness(20, 0, 20, 0),
                Height = 40,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(6, 0, 6, 0),
                Style = (Style)FindResource("OutlineBtn")
            };

            void Close(bool result)
            {
                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                tcs.TrySetResult(result);
            }

            okBtn.Click += (s, e) => Close(true);
            cancelBtn.Click += (s, e) => Close(false);

            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            cardContent.Children.Add(buttonPanel);

            dialogCard.Child = cardContent;
            MainGrid.Children.Add(dialogCard);

            overlay.Opacity = 0;
            dialogCard.Opacity = 0;
            dialogCard.RenderTransform = new ScaleTransform(0.9, 0.9);
            dialogCard.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            DoubleAnimation scaleIn = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            overlay.BeginAnimation(OpacityProperty, fadeIn);
            dialogCard.BeginAnimation(OpacityProperty, fadeIn);
            ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

            overlay.MouseLeftButtonDown += (s, e) => Close(false);
        });

        return tcs.Task;
    }

    private void ShowFullScanRequiredNotification(
        string title = "Требуется полное сканирование",
        string description = "Пройдите сначала полное сканирование, чтобы менять конфиги.\n\n" +
                             "Это займёт около 10 минут, но зато приложение найдёт все рабочие конфиги " +
                             "и вы сможете легко переключаться между ними когда что-то перестанет работать.")
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        var notificationCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
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
        if (_forceClose)
        {
            StopConnectionAnalysis();
            _discord.Dispose();
            _ping.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        PerformCloseToTray();
    }

    private void PerformCloseToTray()
    {
        _wasClosedToTray = true;
        StopConnectionAnalysis();
        _auroraTimer?.Stop();
        _monitorTimer?.Stop();
        _netTimer?.Stop();
        _pingTimer?.Stop();

        if (MainPage != null && MainPage.Visibility == Visibility.Visible && SettingsService.IsOnboarded)
        {
            if (EntranceCurtain != null)
            {
                EntranceCurtain.Visibility = Visibility.Visible;
                EntranceCurtain.Opacity = 1;
            }
            PrepareMainEntranceState();
            UpdateLayout();
        }

        int frameCount = 0;
        void OnRenderFrame(object? sender, EventArgs e)
        {
            frameCount++;
            if (frameCount >= 2)
            {
                CompositionTarget.Rendering -= OnRenderFrame;
                Hide();
            }
        }

        CompositionTarget.Rendering += OnRenderFrame;
    }

    public void ForceExit()
    {
        _forceClose = true;
        System.Windows.Application.Current.Shutdown();
    }

    private void FadeIn()
    {
        PlayEpicMainEntranceAnimation(0);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseBtn_Click(object s, RoutedEventArgs e)
    {
        PerformCloseToTray();
    }

    private void DiagNavBtn_Click(object s, RoutedEventArgs e)
    {
        StopGame();
        StopEditorRecording();

        ShowDiagnosticsTab();
    }

    public void ShowDiagnosticsTab()
    {
        MainPage.Visibility = Visibility.Collapsed;
        GamePage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        ModsPage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Visible;

        DiagHomeScreen.Visibility = Visibility.Visible;
        DiagConnectionScreen.Visibility = Visibility.Collapsed;
        DiagAvailabilityScreen.Visibility = Visibility.Collapsed;

        DiagNavBtn.Foreground = Brushes.White;
        GameNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        ModsNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void GameNavBtn_Click(object s, RoutedEventArgs e)
    {
        if (GamePage.Visibility == Visibility.Visible)
        {
            if (GameEditorView.Visibility == Visibility.Visible)
            {
                StopEditorRecording();
                ShowGameView(GameMenuView);
                return;
            }

            if (GameTrackSelectView.Visibility == Visibility.Visible)
            {
                ShowGameView(GameMenuView);
                return;
            }

            if (GamePlayView.Visibility == Visibility.Visible)
            {
                StopGame();
                ShowGameView(GameTrackSelectView);
                return;
            }

            return;
        }

        StopGame();
        StopEditorRecording();

        MainPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        ModsPage.Visibility = Visibility.Collapsed;
        GamePage.Visibility = Visibility.Visible;

        GameNavBtn.Foreground = Brushes.White;
        ServicesBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        FaqNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        ModsNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

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
            _gameOverlayActive = false;
            StopGame();
            GamePage.Visibility = Visibility.Collapsed;
            Panel.SetZIndex(GamePage, 0);
            GamePage.Opacity = 1;
            GamePage.Background = new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0f));

            MainPage.Effect = null;
            MainPage.Opacity = 1.0;

            ServicesBtn.IsEnabled = true;
            GameNavBtn.IsEnabled = true;
            FaqNavBtn.IsEnabled = true;
            DiagNavBtn.IsEnabled = true;
            SettingsBtn.IsEnabled = true;
            ModsNavBtn.IsEnabled = true;

            EditorMenuBtn.Visibility = Visibility.Visible;
            return;
        }

        if (GameEditorView.Visibility == Visibility.Visible)
        {
            StopEditorRecording();
            ShowGameView(GameMenuView);
            return;
        }

        if (GameTrackSelectView.Visibility == Visibility.Visible)
        {
            ShowGameView(GameMenuView);
            return;
        }

        if (OsuModeView.Visibility == Visibility.Visible)
        {
            ShowGameView(GameTrackSelectView);
            return;
        }

        if (GamePlayView.Visibility == Visibility.Visible)
        {
            StopGame();
            ShowGameView(GameTrackSelectView);
            return;
        }

        if (GameMenuView.Visibility == Visibility.Visible)
        {
            GamePage.Visibility = Visibility.Collapsed;
            MainPage.Visibility = Visibility.Visible;
            GameNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            return;
        }
    }

    private string _currentFaqCategory = "";

    private void FaqNavBtn_Click(object s, RoutedEventArgs e)
    {
        StopGame();
        StopEditorRecording();

        MainPage.Visibility = Visibility.Collapsed;
        GamePage.Visibility = Visibility.Collapsed;
        DiagPage.Visibility = Visibility.Collapsed;
        SolutionPage.Visibility = Visibility.Collapsed;
        ModsPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Visible;
        FaqNavBtn.Foreground = Brushes.White;
        GameNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        ModsNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
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

        AddAndroidCard();

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
            Height = double.NaN,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        var card = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            CornerRadius = new CornerRadius(0, 12, 12, 0),
            Padding = new Thickness(16, 14, 16, 14),
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

        Geometry geometry = null;

        switch (iconKey)
        {
            case "TelegramIcon":
                geometry = Geometry.Parse("M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z");
                break;
            case "DiscordIcon":
                geometry = Geometry.Parse("M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z");
                break;
            case "SettingsIcon":
                geometry = Geometry.Parse("M19.0006 9.03002C19.0007 8.10058 18.8158 7.18037 18.4565 6.32317C18.0972 5.46598 17.5709 4.68895 16.9081 4.03734C16.2453 3.38574 15.4594 2.87265 14.5962 2.52801C13.7331 2.18336 12.8099 2.01409 11.8806 2.03002C10.0966 2.08307 8.39798 2.80604 7.12302 4.05504C5.84807 5.30405 5.0903 6.98746 5.00059 8.77001C4.95795 9.9595 5.21931 11.1402 5.75999 12.2006C6.30067 13.2609 7.10281 14.1659 8.09058 14.83C8.36897 15.011 8.59791 15.2584 8.75678 15.5499C8.91565 15.8415 8.99945 16.168 9.00059 16.5V18.03H15.0006V16.5C15.0006 16.1689 15.0829 15.843 15.24 15.5515C15.3971 15.26 15.6241 15.0121 15.9006 14.83C16.8528 14.1911 17.6336 13.328 18.1741 12.3167C18.7147 11.3054 18.9985 10.1767 19.0006 9.03002V9.03002Z M15 21.04C14.1345 21.6891 13.0819 22.04 12 22.04C10.9181 22.04 9.86548 21.6891 9 21.04");
                break;
            default:
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
            IsHitTestVisible = false
        };

        Grid.SetColumn(iconBg, 0); Grid.SetColumn(stack, 1); Grid.SetColumn(arrowBadge, 2);
        grid.Children.Add(iconBg); grid.Children.Add(stack); grid.Children.Add(arrowBadge);

        card.Child = grid; btn.Content = card;

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

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)) { Opacity = 0.2 },
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3)
        };

        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };

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
            Text = "NetFix Mobile уже вышел!",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 6)
        });

        stack.Children.Add(new TextBlock {
            Text = "YouTube и Telegram на смартфонах и Smart TV в один клик",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xdd, 0xaa)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        var arrowText = new TextBlock {
            Text = "Узнать подробнее и скачать →",
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
        FaqHeaderTitle.Text = "NetFix Mobile";
        FaqContainer.Children.Clear();

        var mainCard = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(mainCard, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(mainCard, TextRenderingMode.Grayscale);

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock {
            Text = "NetFix Mobile для Android & Smart TV",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });

        stack.Children.Add(new TextBlock {
            Text = "Одна кнопка, и интернет снова работает. Прокси Телеграм (Proxy Telegram) и обход блокировок на телефоне и телевизоре.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        });

        try
        {
            BitmapImage? bmp = null;
            try
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri("pack://application:,,,/Assets/Screenshots/hd_phototv.png", UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
            }
            catch
            {
                bmp = null;
            }

            if (bmp == null)
            {
                string[] candidatePaths = [
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Screenshots", "hd_phototv.png"),
                    Path.GetFullPath("Assets/Screenshots/hd_phototv.png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "Screenshots", "hd_phototv.png")
                ];
                foreach (var path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            var diskBmp = new BitmapImage();
                            diskBmp.BeginInit();
                            diskBmp.UriSource = new Uri(path, UriKind.Absolute);
                            diskBmp.CacheOption = BitmapCacheOption.OnLoad;
                            diskBmp.EndInit();
                            diskBmp.Freeze();
                            bmp = diskBmp;
                            break;
                        }
                        catch { }
                    }
                }
            }

            if (bmp != null)
            {
                var imgBorder = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    ClipToBounds = true,
                    Margin = new Thickness(0, 0, 0, 20),
                    Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2e)),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                var img = new System.Windows.Controls.Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 360
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                imgBorder.Child = img;
                stack.Children.Add(imgBorder);
            }
        }
        catch { }

        var aboutTitle = new TextBlock {
            Text = "💡 О проекте",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(aboutTitle);

        var infoText = "Блокировки давно перестали быть проблемой одного только компьютера. YouTube тормозит на телевизоре, Telegram не грузит фото и видео на телефоне в мобильной сети, и если на десктопе с этим уже давно разобрался NetFix, то на Android до сих пор приходилось вручную возиться со сложными утилитами и настройками.\n\n" +
            "NetFix Mobile - официальный мобильный клиент, созданный по принципу «одной кнопки». Внутри одного APK работает обход DPI и встроенный локальный TgWsProxy Android (прокси Телеграм / Proxy Telegram), работающие как два независимых сервиса. Приложение само тестирует сеть, подбирает рабочую конфигурацию под вашего провайдера и запускает всё в один клик, без танцев с бубном и настроек.\n\n" +
            "От автора: Мобильная версия переносит философию «просто нажми кнопку» на Android - и на телефоны, и на телевизоры. Интерфейс одинаково удобно управляется как пальцем на сенсорном экране, так и обычным пультом от Smart TV.";

        stack.Children.Add(new TextBlock {
            Text = infoText,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 18)
        });

        var ghBtn = new Button {
            Style = (Style)FindResource("GreenAccentBtn"),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 0, 0, 20),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Cursor = Cursors.Hand
        };
        ghBtn.Content = "🚀 Скачать на GitHub (APK)";
        ghBtn.Click += (_, _) => OpenUrl("https://github.com/rupleide/NetFixMobile/releases/latest");
        stack.Children.Add(ghBtn);

        var reqBorder = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x32)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14)
        };

        var reqText = "📋 Системные требования:\n" +
            "• ОС: Android 8.0 (API 26) и новее\n" +
            "• Архитектуры: arm64-v8a, armeabi-v7a, x86, x86_64\n" +
            "• Совместимость: Смартфоны, планшеты, ТВ-приставки и телевизоры на Android TV / Google TV\n" +
            "• Разрешения: При первом запуске система попросит подтвердить создание VPN-туннеля (стандартный диалог Android для работы обхода DPI).";

        reqBorder.Child = new TextBlock {
            Text = reqText,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        };
        stack.Children.Add(reqBorder);

        mainCard.Child = stack;
        FaqContainer.Children.Add(mainCard);

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
            AddQuestion("Как исключить приложение из VPN-туннеля в happ", "1. Откройте приложение Happ (happ-tun).\n\n2. Перейдите в раздел «Настройки правил» (Routing / Правила маршрутизации).\n\n3. В блоке правил прямого выхода (Direct / Прямой трафик) добавьте имя процесса приложения (например, msedge.exe, Sky.exe или Discord.exe).\n\n4. Сохраните настройки — трафик выбранного приложения начнёт идти напрямую через физический сетевой адаптер в обход VPN.");
        }
    }

    private void AddQuestion(string title, string answer)
    {
        var btn = new Button {
            Style = (Style)FindResource("FlatBtn"),
            Padding = new Thickness(0),
            Height = double.NaN,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var border = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
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
            IsHitTestVisible = false
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
            IsHitTestVisible = false
        });
        Grid.SetColumn(grid.Children[grid.Children.Count - 1], 2);

        grid.Children.Insert(0, dot);

        border.Child = grid;
        btn.Content = border;

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
            Text = "← Вернуться к вопросам",
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

        btnContainer.Click += (s, e) => ShowFaqQuestions(_currentFaqCategory);
        FaqContainer.Children.Add(btnContainer);
    }

    private void BackBtn_Click(object s, RoutedEventArgs e)
    {
        StopConnectionAnalysis();
        DiagPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        DiagNavBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    }

    private void BackFromSolution_Click(object s, RoutedEventArgs e)
    {
        SolutionPage.Visibility = Visibility.Collapsed;
        FaqPage.Visibility = Visibility.Visible;
    }

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
            ModsPage.Visibility = Visibility.Collapsed;
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

    private void CheckInternetOnStart()
    {
        Task.Run(async () =>
        {
            bool ok = await DiagnosticsEngine.CheckInternetAsync();
            if (_settings.ForceNetworkOk) ok = true;
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
                }
            });

            if (ok)
            {
                try
                {
                    var availability = await GitHubAvailabilityChecker.CheckAvailabilityAsync();
                    if (availability == GitHubAvailabilityResult.Available)
                    {
                        var (needsUpdate, reason) = await ComponentVersionService.CheckIfUpdateNeededAsync(_settings);
                        if (needsUpdate)
                        {
                            await Task.Delay(2000);
                            Dispatcher.Invoke(() =>
                            {
                                AppendLog($"⚡ На GitHub доступны новые версии компонентов. Нажмите «Починить интернет», чтобы обновиться.", "info");
                            });
                        }
                    }
                }
                catch { }
            }
        });
    }

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
            if (_settings.ForceNetworkOk) netOk = true;

            Dispatcher.Invoke(() =>
            {
                var greenBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                var grayBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                var redBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

                VpnDot.Fill = vpn ? greenBrush : grayBrush;
                ZapretDot.Fill = st.ZapretRunning ? greenBrush : grayBrush;
                TgWsDot.Fill = st.TgWsProxyRunning ? greenBrush : grayBrush;

                ZapretDot2.Fill = st.ZapretRunning ? greenBrush : grayBrush;
                ZapretStatusLbl.Text = st.ZapretRunning ? "Запущен" : "Не запущен";
                ZapretStatusLbl.Foreground = st.ZapretRunning ? greenBrush : grayBrush;
                if (st.ZapretRunning)
                {
                    ZapretToggleBtn.Style = (Style)FindResource("RedAccentBtn");
                    ZapretToggleBtn.Content = "■  Закрыть";
                }
                else
                {
                    ZapretToggleBtn.Style = (Style)FindResource("AccentBtn");
                    ZapretToggleBtn.Content = CreateButtonContentWithIcon("PlayIcon", "Запустить", Brushes.White);
                }

                UpdateActiveConfigDisplay(st.ZapretRunning);

                TgWsDot2.Fill = st.TgWsProxyRunning ? greenBrush : grayBrush;
                TgWsStatusLbl.Text = st.TgWsProxyRunning ? "Запущен" : "Не запущен";
                TgWsStatusLbl.Foreground = st.TgWsProxyRunning ? greenBrush : grayBrush;
                if (st.TgWsProxyRunning)
                {
                    TgWsToggleBtn.Style = (Style)FindResource("RedAccentBtn");
                    TgWsToggleBtn.Content = "■  Закрыть";
                }
                else
                {
                    TgWsToggleBtn.Style = (Style)FindResource("AccentBtn");
                    TgWsToggleBtn.Content = CreateButtonContentWithIcon("PlayIcon", "Запустить", Brushes.White);
                }

                if (netOk)
                {
                    NetDot.Fill = greenBrush;
                    NetLbl.Text = "Сеть";
                    NetLbl.Foreground = grayBrush;
                }
                else
                {
                    NetDot.Fill = redBrush;
                    NetLbl.Text = "Нет сети";
                    NetLbl.Foreground = redBrush;
                }

                if (!_isInGame && !_discord.IsScanning)
                    _discord.SetAllGood(st.ZapretRunning, st.TgWsProxyRunning);

                bool allEnabledRunning =
                    (!_settings.EnableZapret || st.ZapretRunning) &&
                    (!_settings.EnableTgWsProxy || st.TgWsProxyRunning);
                bool anyEnabled = _settings.EnableZapret || _settings.EnableTgWsProxy;

                if (!allEnabledRunning && _isConnected
                    && !_isInstalling && !_autoFixRunning && !_checkInProgress)
                    SetFixBtnDisconnected();

                if (allEnabledRunning && anyEnabled && !_isConnected
                    && !_isInstalling && !_autoFixRunning && !_checkInProgress)
                    SetConnectedFromStatus();
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

    private async Task WriteStartupLogAsync()
    {
        const int d = 60;
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

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

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

        var gamepadPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M511.27,331.566L511.27,331.566c0-0.009,0-0.018,0-0.026c-0.008-0.052,0-0.087-0.008-0.14h-0.009 c-6.682-88.65-27.159-154.403-55.948-198.846c-14.412-22.221-30.968-39.115-49.041-50.507 c-18.048-11.401-37.649-17.198-57.388-17.18c-14.551-0.009-26.985,2.629-37.527,6.611c-15.836,5.97-27.358,14.795-36.364,21.319 c-4.495,3.28-8.373,5.961-11.549,7.592c-3.211,1.658-5.475,2.239-7.436,2.239c-1.328-0.009-2.725-0.251-4.521-0.92 c-3.115-1.137-7.288-3.732-12.278-7.332c-7.531-5.354-16.885-12.764-29.223-18.846c-12.339-6.092-27.766-10.69-46.855-10.664 c-19.739-0.018-39.34,5.787-57.388,17.18c-27.115,17.119-50.794,46.481-69.008,87.887C18.542,211.332,5.743,264.92,0.746,331.401 H0.738c-0.009,0.052,0,0.096-0.009,0.14c0,0.008,0,0.017,0,0.026l0,0C0.243,336.981,0,342.247,0,347.358 c-0.009,25.058,5.77,46.455,16.651,63.141c10.846,16.694,26.863,28.347,45.614,33.822c6.43,1.892,13.068,2.811,19.757,2.811 c19.445-0.026,39.046-7.618,57.692-20.764c18.681-13.189,36.598-32.052,52.91-55.731c7.845-11.427,18.5-24.798,29.987-34.854 c5.736-5.032,11.662-9.214,17.362-12.026c5.71-2.82,11.09-4.244,16.027-4.235c4.936-0.009,10.317,1.414,16.026,4.235 c8.555,4.199,17.588,11.558,25.787,20.112c8.226,8.538,15.67,18.196,21.562,26.76c16.312,23.688,34.23,42.55,52.902,55.739 c18.655,13.146,38.255,20.738,57.7,20.764c6.69,0,13.328-0.92,19.749-2.811c18.759-5.475,34.776-17.128,45.614-33.822 C506.221,393.813,512,372.416,512,347.358C512,342.256,511.757,336.981,511.27,331.566z M476.737,398.36 c-8.104,12.356-19.236,20.469-33.284,24.651c-4.33,1.275-8.807,1.9-13.475,1.908c-13.484,0.026-28.902-5.414-44.894-16.703 c-15.974-11.254-32.312-28.225-47.418-50.177c-8.564-12.417-20.044-27.012-33.64-38.95c-6.812-5.97-14.169-11.297-22.16-15.245 c-7.975-3.94-16.677-6.534-25.866-6.534c-9.189,0-17.892,2.594-25.866,6.534c-11.974,5.943-22.577,14.906-31.957,24.616 c-9.353,9.726-17.432,20.268-23.843,29.579c-15.106,21.952-31.454,38.923-47.419,50.177 c-15.991,11.288-31.418,16.729-44.894,16.703c-4.677-0.009-9.145-0.633-13.484-1.908c-14.04-4.182-25.172-12.295-33.284-24.651 c-8.06-12.364-13.04-29.293-13.04-51.002c0-4.451,0.208-9.111,0.65-13.961v-0.052l0.009-0.113 c6.429-86.17,26.446-148.582,52.451-188.59c12.989-20.026,27.41-34.447,42.256-43.801c14.872-9.353,30.126-13.744,45.544-13.761 c11.896,0.009,21.424,2.091,29.675,5.189c12.356,4.65,21.883,11.756,31.158,18.507c4.652,3.367,9.233,6.655,14.378,9.336 c5.111,2.655,11.028,4.729,17.666,4.729c4.399,0,8.556-0.928,12.286-2.325c6.56-2.482,12-6.213,17.422-10.065 c8.113-5.831,16.208-12.14,26.091-16.981c9.883-4.833,21.449-8.364,37.076-8.39c15.418,0.017,30.672,4.408,45.545,13.761 c22.264,14.005,43.6,39.532,60.511,78.03c16.92,38.464,29.354,89.735,34.195,154.36v0.052l0.009,0.113 c0.434,4.842,0.652,9.502,0.652,13.961C489.778,369.067,484.806,386.004,476.737,398.36z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xf1))
        };
        iconCanvas.Children.Add(gamepadPath);

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

    private async void FixBtn_Click(object s, RoutedEventArgs e)
    {
        if (_checkInProgress || _autoFixRunning || _isInstalling) return;

        ShowPlayWhileScanDialog();

        if (!_settings.EnableZapret && !_settings.EnableTgWsProxy)
        {
            AppendLog("Оба компонента отключены в настройках. Включите хотя бы один.", "warn");
            return;
        }

        FixBtn.IsEnabled = false;
        _checkInProgress = true;
        StartLongCheckTimer();
        var (needsUpdate, reason) = await ComponentVersionService.CheckIfUpdateNeededAsync(_settings);

        if (needsUpdate)
        {
            StopLongCheckTimer();
            _checkInProgress = false;
            await RunAutoInstallAsync();
            return;
        }

        if (_settings.Mode == FixMode.Fast)
        {
            RunFastFix();
            return;
        }

        var st = DiagnosticsEngine.CheckAppStatus();

        if (_settings.EnableZapret
            && !st.ZapretRunning
            && !string.IsNullOrWhiteSpace(_settings.ZapretPath)
            && File.Exists(_settings.ZapretPath))
        {
            StopLongCheckTimer();
            _checkInProgress = false;
            ShowZapretWizard();
            return;
        }

        if (_settings.EnableTgWsProxy
            && !st.TgWsProxyRunning
            && !string.IsNullOrWhiteSpace(_settings.TgWsProxyPath)
            && File.Exists(_settings.TgWsProxyPath))
        {
            StartTgWsProxyWithActivation();
        }

        RunAutoFix();
    }

    private async void RunAutoFix()
    {
        if (_autoFixRunning)
        {
            return;
        }

        _autoFixRunning = true;

        EnterWorkingState("Подготовка...");
        LogBox.Document.Blocks.Clear();

        string timeStr = DateTime.Now.ToString("HH:mm:ss");
        AppendLog($"СИСТЕМНАЯ ДИАГНОСТИКА [ ВРЕМЯ: {timeStr} ]", "system");
        AppendLog("spacer");

        _discord.IsScanning = true;
        _discord.SetFixing();

        AppendLog("СЕТЕВАЯ СРЕДА", "system");
        bool netOk = await DiagnosticsEngine.CheckInternetAsync();
        AppendLog($"Интернет-соединение: {(netOk ? "[ ПОДКЛЮЧЕНО ]" : "[ ОШИБКА ]")}", netOk ? "ok" : "error");

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

        Dispatcher.Invoke(() => RenderDiagReport(report));

        AppendLog("Обнаружена блокировка протоколов (DPI/ТСПУ)", "dpi");
        AppendLog("spacer");

        AppendLog("СОСТОЯНИЕ СЕРВИСОВ", "system");
        AppendLog($"Telegram Desktop: {(report.AppStatus?.TelegramRunning == true ? "[ ЗАПУЩЕН ]" : "[ НЕ В СЕТИ ]")}", "net");
        AppendLog($"Discord App:      {(report.AppStatus?.DiscordRunning == true ? "[ ЗАПУЩЕН ]" : "[ НЕ В СЕТИ ]")}", "net");

        int srvOk = report.DcResults.Count(d => d.Ok);
        AppendLog($"Доступность серверов Telegram: {srvOk} из {report.DcResults.Count}", srvOk > 0 ? "ok" : "warn");
        AppendLog("spacer");

        AppendLog("ЗАПУСК ИСПРАВЛЕНИЙ", "system");
        AutoSetupService.Run(
            logCb: (msg, kind) => AppendLog(msg, kind == "step" ? "speed" : kind),
            progressCb: ratio => Dispatcher.Invoke(() => {
                SetupProg.Value = 50 + (ratio * 50);
                SetupProgLbl.Text = $"Настройка: {(int)(ratio * 100)}%";
            }),
            doneCb: (success, _) => Dispatcher.Invoke(async () => {
                StopGlow(success);

                _discord.IsScanning = false;

                if (success)
                {
                    SetFixBtnConnected();
                    _discord.SetAllGood(true, true);
                }
                else _discord.SetProblems("Ошибка автонастройки");

                FixBtn.IsEnabled = true;

                StopLongCheckTimer();
                _checkInProgress = false;

                _autoFixRunning = false;

                if (success) {
                    AnimateProgressBar(100, Color.FromRgb(34, 197, 94), "Готово", 0.5);
                    AppendLog("spacer");
                    AppendLog("Всё запущено и всё работает НОРМАЛЬНО!", "final");
                    AppendLog("Zapret включен. Discord и YouTube должны работать нормально.", "ok");
                    AppendLog("Прокси настроен. Telegram должен работать стабильно.", "ok");
                    AppendLog("Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».", "info");
                    PlaySuccessRing();
                } else {
                    AnimateProgressBar(100, Color.FromRgb(239, 68, 68), "Ошибка", 0.5);
                    AppendLog("Произошла ошибка при автоматической настройке. Проверьте пути в настройках.", "error");
                    PlayErrorRing();
                }

                await Task.Delay(2500);

                var appStatus = DiagnosticsEngine.CheckAppStatus();
                _discord.SetAllGood(appStatus.ZapretRunning, appStatus.TgWsProxyRunning);

                if (_settings.EnableZapret)
                {
                    if (!appStatus.ZapretRunning)
                    {
                        TrackMainZapretStartFail();
                    }
                    else
                    {
                        _mainZapretStartFails = 0;
                    }
                }
            }),
            settings: _settings);
    }

    private async void RunFastFix()
    {
        if (_autoFixRunning) return;
        _autoFixRunning = true;

        EnterWorkingState("Быстрый запуск...");
        LogBox.Document.Blocks.Clear();

        string timeStr = DateTime.Now.ToString("HH:mm:ss");
        AppendLog($"БЫСТРЫЙ ЗАПУСК [ {timeStr} ]", "system");
        AppendLog("spacer");

        _discord.IsScanning = true;
        _discord.SetFixing();

        var st = DiagnosticsEngine.CheckAppStatus();
        bool zapretNeeded = _settings.EnableZapret
            && !st.ZapretRunning
            && !string.IsNullOrWhiteSpace(_settings.ZapretPath)
            && File.Exists(_settings.ZapretPath);
        bool tgwsNeeded = _settings.EnableTgWsProxy
            && !st.TgWsProxyRunning
            && !string.IsNullOrWhiteSpace(_settings.TgWsProxyPath)
            && File.Exists(_settings.TgWsProxyPath);

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

        if (!zapretNeeded && !tgwsNeeded)
        {
            if (_settings.EnableZapret) AppendLog("Zapret уже запущен", "ok");
            if (_settings.EnableTgWsProxy) AppendLog("tg-ws-proxy уже запущен", "ok");

            AppendLog("spacer");
            AppendLog("Всё запущено и всё работает НОРМАЛЬНО!", "final");

            StopGlow(true);
            Dispatcher.Invoke(() => SetFixBtnConnected());
            _discord.IsScanning = false;
            _discord.SetAllGood(true, true);
            AnimateProgressBar(100, Color.FromRgb(34, 197, 94), "Всё уже работает", 0.5);
            PlaySuccessRing();
            StopLongCheckTimer();
            _checkInProgress = false;
            _autoFixRunning = false;
            FixBtn.IsEnabled = true;
            return;
        }

        bool zapretOk = !zapretNeeded;
        bool tgwsOk = !tgwsNeeded;

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
        AnimateProgressBar(100, Color.FromRgb(34, 197, 94), "Готово", 0.5);

        StopGlow(zapretOk || tgwsOk);
        _discord.IsScanning = false;

        bool anySuccess = zapretOk && tgwsOk;
        if (anySuccess)
        {
            Dispatcher.Invoke(() => SetFixBtnConnected());
            _discord.SetAllGood(true, true);
            AppendLog("spacer");
            AppendLog("Всё запущено и всё работает НОРМАЛЬНО!", "final");
            if (zapretOk) AppendLog("Zapret включен. Discord и YouTube должны работать нормально.", "ok");
            if (tgwsOk) AppendLog("Прокси настроен. Telegram должен работать стабильно.", "ok");
            AppendLog("Если что-то всё еще не грузит, перейдите во вкладку «Частые вопросы».", "info");
            PlaySuccessRing();
        }
        else
        {
            _discord.SetProblems("Ошибка запуска");
            AnimateProgressBar(100, Color.FromRgb(239, 68, 68), "Ошибка", 0.5);
            AppendLog("Не удалось запустить некоторые сервисы. Проверьте пути в настройках.", "error");
            PlayErrorRing();
        }

        StopLongCheckTimer();
        _checkInProgress = false;
        _autoFixRunning = false;
        FixBtn.IsEnabled = true;
    }

    private async Task RunAutoInstallAsync(bool preserveLists = false)
    {
        _isInstalling = true;
        EnterWorkingState("Подготовка к установке...");
        LogBox.Document.Blocks.Clear();

        string timeStr = DateTime.Now.ToString("HH:mm:ss");
        AppendLog($"АВТОМАТИЧЕСКАЯ УСТАНОВКА КОМПОНЕНТОВ [ ВРЕМЯ: {timeStr} ]", "system");
        AppendLog("spacer");

        AppendLog("Проверяю доступность GitHub...", "info");
        var availability = await GitHubAvailabilityChecker.CheckAvailabilityAsync();

        bool useReserve = false;

        if (availability != GitHubAvailabilityResult.Available)
        {
            string reason = availability == GitHubAvailabilityResult.Timeout ? "таймаут подключения" : "блокировка или проблемы с сетью";
            AppendLog($"GitHub недоступен ({reason}). Автоматически переключаюсь на встроенную резервную копию...", "warn");
            useReserve = true;
        }

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
            }),
            preserveLists: preserveLists,
            forceReserve: useReserve
        );

        Dispatcher.Invoke(() => {
            _isInstalling = false;
            StopGlow(success);
            FixBtn.IsEnabled = true;

            if (success)
            {
                _settings = SettingsService.Load();
                LoadSettingsToPanel();

                AnimateProgressBar(100, Color.FromRgb(34, 197, 94), "Установка завершена", 0.5);
                AppendLog("spacer");
                AppendLog("✓ Компоненты успешно установлены/обновлены!", "final");
                AppendLog("Теперь можно запустить сервисы через панель управления.", "ok");
                AppendLog("Или нажмите кнопку «Починить интернет» ещё раз для автоматического запуска.", "info");
                PlaySuccessRing();

                UpdateActiveApps();

                if ((_settings.ActiveStrategyMods is { Count: > 0 }) || (_settings.ActiveListMods is { Count: > 0 }))
                {
                    AppendLog("Переустанавливаю активные моды...", "info");
                    RefreshMods();

                    var listMods = _allMods.Where(m => m.Type == ModType.List).ToList();
                    if (listMods.Count > 0)
                        ModActivator.ApplyListMods(listMods);

                    AppendLog("✓ Моды применены к обновлённым компонентам", "ok");
                }
            }
            else
            {
                _isConnected = false;
                _connectedTimer?.Stop();
                _connectedTimer = null;
                var line1 = FixBtn.Template.FindName("BtnLine1", FixBtn) as TextBlock;
                var line2 = FixBtn.Template.FindName("BtnLine2", FixBtn) as TextBlock;
                if (line1 is not null) line1.Text = "Починить";
                if (line2 is not null) line2.Text = "интернет";

                AnimateProgressBar(100, Color.FromRgb(239, 68, 68), "Ошибка установки", 0.5);
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
        StopSuccessRing();

        HideAllRings();
        AnimateSuccessArc(0.6);
        AnimateIconColor(Color.FromRgb(0x22, 0xc5, 0x5e), 0.5);
    }

    private void StartGlow()
    {
        _splitTarget = 1;
        _colorTarget = 0;
        _finalSuccess = true;

        HideAllRings();

        FadeElementIn(SpinArc, 0.3);
        FadeElementIn(SpinArc2, 0.3);

        var spin1 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.4)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinOffset.BeginAnimation(RotateTransform.AngleProperty, spin1);

        var spin2 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.9)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinRotation2.BeginAnimation(RotateTransform.AngleProperty, spin2);

        if (GetFixButtonIcon() is System.Windows.Shapes.Path iconEl)
        {
            var animBrush = new SolidColorBrush(Color.FromRgb(0x7c, 0x6a, 0xf7));
            iconEl.Stroke = animBrush;

            var colorAnim = new ColorAnimation(
                Color.FromRgb(0x7c, 0x6a, 0xf7),
                Color.FromRgb(0x5b, 0x8d, 0xf5),
                new Duration(TimeSpan.FromSeconds(1.8)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            animBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        }
    }

    private void StopGlow(bool success)
    {
        _finalSuccess = success;
        _splitTarget = 0;
        _colorTarget = 1;

        SpinOffset.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinRotation2.BeginAnimation(RotateTransform.AngleProperty, null);

        FadeElementOut(SpinArc, 0.3);
        FadeElementOut(SpinArc2, 0.3);

        StopSuccessRing();

        if (GetFixButtonIcon() is System.Windows.Shapes.Path iconEl)
        {
            iconEl.BeginAnimation(System.Windows.Shapes.Path.StrokeProperty, null);
            iconEl.Stroke = new SolidColorBrush(success
                ? Color.FromRgb(0x22, 0xc5, 0x5e)
                : Color.FromRgb(0xef, 0x44, 0x44));
        }
    }

    private void StopAllBypassServices()
    {
        var st = DiagnosticsEngine.CheckAppStatus();
        if (_settings.EnableZapret && st.ZapretRunning)
        {
            foreach (var p in Process.GetProcessesByName("winws"))
                try { p.Kill(); } catch { }
        }
        if (_settings.EnableTgWsProxy && st.TgWsProxyRunning)
        {
            foreach (var p in Process.GetProcessesByName("TgWsProxy"))
                try { p.Kill(); } catch { }
        }

        StopGlow(false);
    }

    private void PlayErrorRing()
    {
        StopSuccessRing();

        HideAllRings();
        FadeElementIn(ErrorRing, 0.3);

        AnimateIconColor(Color.FromRgb(0xef, 0x44, 0x44), 0.4);

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

    private void SetFixBtnConnected()
    {
        _isConnected = true;
        _connectedSince = DateTime.Now;

        AnimateButtonLabel("Подключено", "00:00");

        _connectedTimer?.Stop();
        _connectedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _connectedTimer.Tick += (_, _) => UpdateConnectedTimer();
        _connectedTimer.Start();
    }

    private void SetFixBtnDisconnected()
    {
        _isConnected = false;
        _connectedTimer?.Stop();
        _connectedTimer = null;

        StopSuccessRing();

        AnimateButtonLabel("Починить", "интернет");

        AnimateProgressBar(0, Color.FromRgb(0x2e, 0x2e, 0x2e), "", 0.5);
        SetupProgLbl.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        HideAllRings();
        FadeElementIn(IdleRingOuter, 0.4);
        FadeElementIn(IdleRingInner, 0.5);

        AnimateIconColor(Color.FromRgb(0x7c, 0x6a, 0xf7), 0.4);

        _splitTarget = 0;
        _colorTarget = 0;
        _finalSuccess = true;
    }

    private void HideAllRings()
    {
        IdleRingOuter.Visibility = Visibility.Collapsed;
        IdleRingInner.Visibility = Visibility.Collapsed;
        SpinArc.Visibility = Visibility.Collapsed;
        SpinArc2.Visibility = Visibility.Collapsed;
        SuccessArc.Visibility = Visibility.Collapsed;
        ErrorRing.Visibility = Visibility.Collapsed;
        SuccessCheck.Visibility = Visibility.Collapsed;
    }

    private void StopSuccessRing()
    {
        _successRingTimer?.Stop();
        _successRingTimer = null;
        if (_successRingIconBrush is not null)
        {
            _successRingIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _successRingIconBrush = null;
        }
    }

    private void EnterWorkingState(string label)
    {
        _isConnected = false;
        _connectedTimer?.Stop();
        _connectedTimer = null;

        AnimateButtonLabel("Починить", "интернет");

        FixBtn.IsEnabled = false;

        SetupProg.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        SetupProg.BeginAnimation(System.Windows.Controls.ProgressBar.ForegroundProperty, null);
        SetupProg.Value = 0;

        var dur = new Duration(TimeSpan.FromSeconds(0.4));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fromColor = SetupProg.Foreground is SolidColorBrush bc ? bc.Color : Color.FromRgb(0x2e, 0x2e, 0x2e);
        var animBrush = new SolidColorBrush(fromColor);
        SetupProg.Foreground = animBrush;
        animBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(fromColor, Color.FromRgb(59, 130, 246), dur) { EasingFunction = ease });
        SetupProgLbl.Text = label;

        StopSuccessRing();

        _splitTarget = 1;
        _colorTarget = 0;
        _finalSuccess = true;

        HideAllRings();
        FadeElementIn(SpinArc, 0.3);
        FadeElementIn(SpinArc2, 0.3);

        var spin1 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.4)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinOffset.BeginAnimation(RotateTransform.AngleProperty, spin1);

        var spin2 = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.9)))
            { RepeatBehavior = RepeatBehavior.Forever };
        SpinRotation2.BeginAnimation(RotateTransform.AngleProperty, spin2);

        if (GetFixButtonIcon() is System.Windows.Shapes.Path iconEl)
        {
            var iconBrush = new SolidColorBrush(Color.FromRgb(0x7c, 0x6a, 0xf7));
            iconEl.Stroke = iconBrush;
            var colorAnim = new ColorAnimation(
                Color.FromRgb(0x7c, 0x6a, 0xf7),
                Color.FromRgb(0x5b, 0x8d, 0xf5),
                new Duration(TimeSpan.FromSeconds(1.8)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            iconBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        }
    }

    private void AnimateButtonLabel(string text1, string text2)
    {
        var line1 = FixBtn.Template.FindName("BtnLine1", FixBtn) as TextBlock;
        var line2 = FixBtn.Template.FindName("BtnLine2", FixBtn) as TextBlock;
        if (line1 is null || line2 is null) return;

        var dur = new Duration(TimeSpan.FromSeconds(0.2));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
        fadeOut.Completed += (_, _) =>
        {
            line1.Text = text1;
            line2.Text = text2;
            line1.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            line2.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        };
        line1.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        line2.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, dur) { EasingFunction = ease });
    }

    private void AnimateProgressBar(double targetValue, Color targetColor, string label, double durationSec = 0.6)
    {
        SetupProg.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        SetupProg.BeginAnimation(System.Windows.Controls.ProgressBar.ForegroundProperty, null);

        var dur = new Duration(TimeSpan.FromSeconds(durationSec));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var valAnim = new DoubleAnimation(SetupProg.Value, targetValue, dur) { EasingFunction = ease };
        SetupProg.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, valAnim);
        var fromColor = SetupProg.Foreground is SolidColorBrush bc ? bc.Color : Color.FromRgb(0x2e, 0x2e, 0x2e);
        var animBrush = new SolidColorBrush(fromColor);
        SetupProg.Foreground = animBrush;
        animBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(fromColor, targetColor, dur) { EasingFunction = ease });

        if (label is not null)
            SetupProgLbl.Text = label;
    }

    private void FadeElementIn(FrameworkElement element, double durationSec = 0.3)
    {
        element.Visibility = Visibility.Visible;
        element.Opacity = 0;
        var anim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(durationSec)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void FadeElementOut(FrameworkElement element, double durationSec = 0.2)
    {
        var anim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(durationSec)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => element.Visibility = Visibility.Collapsed;
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void AnimateSuccessArc(double durationSec = 0.6)
    {
        double circumference = 2 * Math.PI * 97;
        SuccessArc.StrokeDashArray = new DoubleCollection { 0, circumference };
        FadeElementIn(SuccessArc, 0.3);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(16) };
        _successRingTimer = timer;
        timer.Tick += (s, e) =>
        {
            double t = Math.Min(sw.Elapsed.TotalSeconds / durationSec, 1.0);
            double ease = t == 0 ? 0 : 1 - Math.Pow(2, -10 * t);
            SuccessArc.StrokeDashArray = new DoubleCollection { ease * circumference, circumference };
            if (t >= 1.0)
            {
                timer.Stop();
                _successRingTimer = null;
            }
        };
        timer.Start();
    }

    private void AnimateIconColor(Color targetColor, double durationSec = 0.5)
    {
        if (GetFixButtonIcon() is not System.Windows.Shapes.Path iconEl) return;
        iconEl.BeginAnimation(System.Windows.Shapes.Path.StrokeProperty, null);
        var fromColor = iconEl.Stroke is SolidColorBrush bc ? bc.Color : Color.FromRgb(0x7c, 0x6a, 0xf7);
        var brush = new SolidColorBrush(fromColor);
        iconEl.Stroke = brush;
        _successRingIconBrush = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(targetColor, new Duration(TimeSpan.FromSeconds(durationSec)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void CheckInitialServiceState()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var st = DiagnosticsEngine.CheckAppStatus();
            bool allRunning = (!_settings.EnableZapret || st.ZapretRunning)
                           && (!_settings.EnableTgWsProxy || st.TgWsProxyRunning);
            if (!allRunning) return;

            HideAllRings();
            AnimateSuccessArc(0.6);
            AnimateIconColor(Color.FromRgb(0x22, 0xc5, 0x5e), 0.5);

            _splitTarget = 0;
            _colorTarget = 1;
            _finalSuccess = true;

            AnimateProgressBar(100, Color.FromRgb(0x22, 0xc5, 0x5e), "Всё уже работает", 0.6);
            SetFixBtnConnected();
        };
        t.Start();
    }

    private void SetConnectedFromStatus()
    {
        StopSuccessRing();

        AnimateProgressBar(100, Color.FromRgb(34, 197, 94), "Всё уже работает", 0.6);

        HideAllRings();
        AnimateSuccessArc(0.6);

        AnimateIconColor(Color.FromRgb(0x22, 0xc5, 0x5e), 0.5);

        _splitTarget = 0;
        _colorTarget = 1;
        _finalSuccess = true;

        SetFixBtnConnected();
    }

    private void UpdateConnectedTimer()
    {
        if (!_isConnected) return;
        var elapsed = DateTime.Now - _connectedSince;
        var line2 = FixBtn.Template.FindName("BtnLine2", FixBtn) as TextBlock;
        if (line2 is not null)
            line2.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
    }

    private void DiagCardTestConnection_Click(object sender, RoutedEventArgs e)
    {
        DiagHomeScreen.Visibility = Visibility.Collapsed;
        DiagAvailabilityScreen.Visibility = Visibility.Collapsed;
        DiagConnectionScreen.Visibility = Visibility.Visible;
        StartConnectionAnalysis();
    }

    private void DiagCardAvailability_Click(object sender, RoutedEventArgs e)
    {
        StopConnectionAnalysis();
        DiagHomeScreen.Visibility = Visibility.Collapsed;
        DiagConnectionScreen.Visibility = Visibility.Collapsed;
        DiagAvailabilityScreen.Visibility = Visibility.Visible;
    }

    private void DiagSubScreenBack_Click(object sender, RoutedEventArgs e)
    {
        StopConnectionAnalysis();
        DiagConnectionScreen.Visibility = Visibility.Collapsed;
        DiagAvailabilityScreen.Visibility = Visibility.Collapsed;
        DiagHomeScreen.Visibility = Visibility.Visible;
    }

    #region Connection Analysis (Диагностика → Анализ соединений)

    private readonly object _connAnalysisLock = new();

    private void StartConnectionAnalysis()
    {
        lock (_connAnalysisLock)
        {
            if (_connAnalysisActive) return;
            _connAnalysisActive = true;

            try
            {
                _dnsEtwMonitor ??= new DnsEtwMonitor();
                _dnsEtwMonitor.Start();
            }
            catch
            {
            }

            if (_connAnalysisTimer is null)
            {
                _connAnalysisTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                _connAnalysisTimer.Tick += (_, _) =>
                {
                    if (_connAnalysisActive && DiagPage.Visibility == Visibility.Visible && DiagConnectionScreen.Visibility == Visibility.Visible)
                    {
                        RefreshConnectionAnalysis();
                    }
                };
            }
            _connAnalysisTimer.Start();

            Task.Run(() =>
            {
                var procs = ConnectionAnalysisService.GetRunningProcesses();
                Dispatcher.Invoke(() =>
                {
                    if (!_connAnalysisActive) return;
                    _allProcesses = procs;
                    UpdateProcessListUi();
                    RefreshConnectionAnalysis();
                });
            });
        }
    }

    private void StopConnectionAnalysis()
    {
        lock (_connAnalysisLock)
        {
            if (!_connAnalysisActive && _dnsEtwMonitor is null && _connAnalysisTimer is null) return;
            _connAnalysisActive = false;
            _connAnalysisTimer?.Stop();
            _connAnalysisTimer = null;
            ConnectionAnalysisService.ResetCpuHistory();

            try
            {
                _dnsEtwMonitor?.Stop();
                _dnsEtwMonitor?.Dispose();
                _dnsEtwMonitor = null;
            }
            catch { }
        }
    }

    private void UpdateProcessListUi(string filter = "")
    {
        var list = _allProcesses;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            list = _allProcesses
                .Where(p => p.AppName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            p.WindowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            p.ExePath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            p.ProcessIds.Any(pid => pid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        PopulateAppItemsList(list);

        if (_selectedConnApp is null && list.Count > 0)
        {
            var preferred = list.FirstOrDefault(p => p.IsCommonApp) ?? list[0];
            SelectApplication(preferred);
        }
    }

    private void PopulateAppItemsList(List<ProcessItemModel> list)
    {
        ConnAppItemsContainer.Children.Clear();

        if (list.Count == 0)
        {
            var noApps = new TextBlock
            {
                Text = "Приложения не найдены",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Padding = new Thickness(12, 20, 12, 20),
                TextAlignment = TextAlignment.Center
            };
            ConnAppItemsContainer.Children.Add(noApps);
            return;
        }

        for (int i = 0; i < list.Count; i++)
        {
            var app = list[i];
            bool isSelected = _selectedConnApp != null && _selectedConnApp.AppName == app.AppName;

            var itemGrid = new Grid { Margin = new Thickness(2, 1, 2, 1), Cursor = Cursors.Hand };
            var itemBg = new System.Windows.Shapes.Rectangle
            {
                Fill = isSelected ? new SolidColorBrush(Color.FromRgb(0x1e, 0x27, 0x3d)) : System.Windows.Media.Brushes.Transparent,
                Stroke = isSelected ? new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)) : System.Windows.Media.Brushes.Transparent,
                StrokeThickness = isSelected ? 1 : 0,
                RadiusX = 6,
                RadiusY = 6
            };
            itemGrid.Children.Add(itemBg);

            var grid = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

            var iconImg = new System.Windows.Controls.Image
            {
                Source = app.Icon,
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);
            Grid.SetColumn(iconImg, 0);
            grid.Children.Add(iconImg);

            var infoStack = new StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            infoStack.Children.Add(new TextBlock
            {
                Text = app.AppName,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (!string.IsNullOrWhiteSpace(app.WindowTitle) &&
                !app.WindowTitle.Equals(app.AppName, StringComparison.OrdinalIgnoreCase) &&
                !app.WindowTitle.Equals(app.ExePath, StringComparison.OrdinalIgnoreCase) &&
                !app.WindowTitle.StartsWith(app.AppName + " ", StringComparison.OrdinalIgnoreCase))
            {
                infoStack.Children.Add(new TextBlock
                {
                    Text = app.WindowTitle.Trim(),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x6e)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 340,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            Grid.SetColumn(infoStack, 1);
            grid.Children.Add(infoStack);

            var socketBadge = new Grid
            {
                Width = 88,
                Height = 25,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            socketBadge.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x22)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x36)),
                StrokeThickness = 1,
                RadiusX = 6,
                RadiusY = 6
            });
            socketBadge.Children.Add(new TextBlock
            {
                Text = $"{app.ConnectionCount} сокетов",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(socketBadge, 2);
            grid.Children.Add(socketBadge);

            var procBadge = new Grid
            {
                Width = 88,
                Height = 25,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            procBadge.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x22)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x36)),
                StrokeThickness = 1,
                RadiusX = 6,
                RadiusY = 6
            });
            procBadge.Children.Add(new TextBlock
            {
                Text = app.ProcessCountBadge,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xa3, 0xb8)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(procBadge, 3);
            grid.Children.Add(procBadge);

            itemGrid.Children.Add(grid);

            itemGrid.MouseEnter += (s, e) =>
            {
                if (_selectedConnApp?.AppName != app.AppName)
                    itemBg.Fill = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x28));
            };
            itemGrid.MouseLeave += (s, e) =>
            {
                if (_selectedConnApp?.AppName != app.AppName)
                    itemBg.Fill = System.Windows.Media.Brushes.Transparent;
            };

            itemGrid.MouseLeftButtonUp += (s, e) =>
            {
                SelectApplication(app);
                CloseConnAppDropdown();
            };

            ConnAppItemsContainer.Children.Add(itemGrid);

            if (i < list.Count - 1)
            {
                ConnAppItemsContainer.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x26)),
                    Margin = new Thickness(8, 2, 8, 2),
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true
                });
            }
        }
    }

    private void SelectApplication(ProcessItemModel app)
    {
        _selectedConnApp = app;
        ConnSelectedAppIcon.Source = app.Icon;
        ConnSelectedAppName.Text = app.AppName;
        ConnSelectedAppSocketsText.Text = $"{app.ConnectionCount} сокетов";
        ConnSelectedAppPidText.Text = app.ProcessCountBadge;
        RefreshAppConnections();
    }

    private bool _isConnDropdownOpen = false;
    private bool _isAnimatingConnDropdown = false;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (_isConnDropdownOpen && !_isAnimatingConnDropdown)
        {
            var posBtn = e.GetPosition(ConnAppSelectorBorder);
            var posDrop = e.GetPosition(ConnAppDropdownBorder);

            bool hitBtn = posBtn.X >= 0 && posBtn.X <= ConnAppSelectorBorder.ActualWidth &&
                         posBtn.Y >= 0 && posBtn.Y <= ConnAppSelectorBorder.ActualHeight;

            bool hitDrop = posDrop.X >= 0 && posDrop.X <= ConnAppDropdownBorder.ActualWidth &&
                          posDrop.Y >= 0 && posDrop.Y <= ConnAppDropdownBorder.ActualHeight;

            if (!hitBtn && !hitDrop)
            {
                CloseConnAppDropdown();
            }
        }
    }

    private void OpenConnAppDropdown()
    {
        if (_isConnDropdownOpen || _isAnimatingConnDropdown) return;
        _isAnimatingConnDropdown = true;
        _isConnDropdownOpen = true;

        ConnAppSelectorBorder.CornerRadius = new CornerRadius(8, 8, 0, 0);
        ConnAppSelectorBorder.BorderThickness = new Thickness(1, 1, 1, 0);
        ConnAppSelectorBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30));
        ConnAppDropdownArrow.Data = Geometry.Parse("M0,4 L4,0 L8,4");

        ConnAppDropdownBorder.Visibility = Visibility.Visible;
        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        opacityAnim.Completed += (_, _) =>
        {
            _isAnimatingConnDropdown = false;
            ConnProcessSearchBox.Focus();
            ConnProcessSearchBox.SelectAll();
        };

        ConnAppDropdownBorder.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
    }

    private void CloseConnAppDropdown(Action? onClosed = null)
    {
        if (!_isConnDropdownOpen || _isAnimatingConnDropdown) return;
        _isAnimatingConnDropdown = true;

        var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        opacityAnim.Completed += (_, _) =>
        {
            _isConnDropdownOpen = false;
            _isAnimatingConnDropdown = false;
            ConnAppDropdownBorder.Visibility = Visibility.Collapsed;
            ConnAppSelectorBorder.CornerRadius = new CornerRadius(8);
            ConnAppSelectorBorder.BorderThickness = new Thickness(1);
            ConnAppSelectorBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30));
            ConnAppDropdownArrow.Data = Geometry.Parse("M0,0 L4,4 L8,0");
            onClosed?.Invoke();
        };

        ConnAppDropdownBorder.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
    }

    private void ConnAppSelectorBtn_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isConnDropdownOpen)
        {
            CloseConnAppDropdown();
        }
        else
        {
            OpenConnAppDropdown();
        }
    }

    private void ConnProcessSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConnSearchPlaceholder != null)
            ConnSearchPlaceholder.Visibility = string.IsNullOrEmpty(ConnProcessSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateProcessListUi(ConnProcessSearchBox.Text);
    }

    private void ConnAppList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            e.Handled = true;
            double delta = e.Delta > 0 ? -30 : 30;
            sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
        }
    }

    private static readonly SolidColorBrush ConnTabActiveFg = new SolidColorBrush(Colors.White);
    private static readonly SolidColorBrush ConnTabInactiveFg = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90));
    private static readonly SolidColorBrush ConnTabHoverFg = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xe0));

    private void AnimateConnModeSwitch(bool toSystem, Action? onCompleted = null)
    {
        double targetX = toSystem ? 100 : 0;
        var anim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        if (onCompleted != null)
        {
            anim.Completed += (_, _) => onCompleted();
        }
        ConnModeIndicatorTrans.BeginAnimation(TranslateTransform.XProperty, anim);

        ConnModeAppText.Foreground = toSystem ? ConnTabInactiveFg : ConnTabActiveFg;
        ConnModeSystemText.Foreground = toSystem ? ConnTabActiveFg : ConnTabInactiveFg;
    }

    private void ConnModeAppBtn_Click(object sender, MouseButtonEventArgs? e)
    {
        if (!_isSystemMode) return;
        _isSystemMode = false;
        AnimateConnModeSwitch(false, () =>
        {
            ConnAppView.Visibility = Visibility.Visible;
            ConnSystemView.Visibility = Visibility.Collapsed;
            RefreshConnectionAnalysis();
        });
    }

    private void ConnModeSystemBtn_Click(object sender, MouseButtonEventArgs? e)
    {
        if (_isSystemMode) return;
        _isSystemMode = true;
        AnimateConnModeSwitch(true, () =>
        {
            ConnAppView.Visibility = Visibility.Collapsed;
            ConnSystemView.Visibility = Visibility.Visible;
            RefreshConnectionAnalysis();
        });
    }

    private void ConnModeTab_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
        {
            if (b == ConnModeAppBtn && _isSystemMode)
                ConnModeAppText.Foreground = ConnTabHoverFg;
            else if (b == ConnModeSystemBtn && !_isSystemMode)
                ConnModeSystemText.Foreground = ConnTabHoverFg;
        }
    }

    private void ConnModeTab_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
        {
            if (b == ConnModeAppBtn && _isSystemMode)
                ConnModeAppText.Foreground = ConnTabInactiveFg;
            else if (b == ConnModeSystemBtn && !_isSystemMode)
                ConnModeSystemText.Foreground = ConnTabInactiveFg;
        }
    }

    private void ConnRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ConnRefreshRotate.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
        RefreshConnectionAnalysis();
    }

    private void RefreshConnectionAnalysis()
    {
        if (_isSystemMode)
        {
            RefreshSystemOverview();
        }
        else
        {
            RefreshAppConnections();
        }
    }

    private string _activeFilter = "All";
    private string _lastPrimaryConnKey = "";
    private bool _isSecondaryListExpanded = false;
    private List<ConnectionDetailModel> _lastAppConnections = [];
    private ConnectionSummaryModel _lastAppSummary = new();

    private void FilterChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
        {
            _activeFilter = tag;
            UpdateFilterChipsUi();
            if (_lastAppConnections.Count > 0)
            {
                RenderAppConnections(_lastAppConnections, _lastAppSummary);
            }
        }
    }

    private void UpdateFilterChipsUi()
    {
        var chips = new (Border? Chip, string Tag, Color Color)[]
        {
            (FilterChipAll, "All", Color.FromRgb(0xee, 0xee, 0xee)),
            (FilterChipVpn, "VPN", Color.FromRgb(0xa8, 0x55, 0xf7)),
            (FilterChipDirect, "Direct", Color.FromRgb(0x22, 0xc5, 0x5e)),
            (FilterChipHosts, "Hosts", Color.FromRgb(0xea, 0xb3, 0x08)),
            (FilterChipProxy, "Proxy", Color.FromRgb(0x06, 0xb6, 0xd4)),
            (FilterChipZapret, "Zapret", Color.FromRgb(0xf9, 0x73, 0x16)),
        };

        foreach (var (chip, tag, color) in chips)
        {
            if (chip is null) continue;
            bool isSelected = _activeFilter.Equals(tag, StringComparison.OrdinalIgnoreCase);
            chip.Opacity = isSelected ? 1.0 : 0.55;
            chip.BorderBrush = isSelected ? new SolidColorBrush(color) : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3d));
            chip.BorderThickness = new Thickness(1);
        }
    }

    private void RefreshAppConnections()
    {
        if (_selectedConnApp is null || _selectedConnApp.ProcessIds.Count == 0) return;

        var pids = _selectedConnApp.ProcessIds.ToList();
        var etw = _dnsEtwMonitor;
        int tgWsPort = 1080;
        var cache = ZapretConfigService.LoadCache();
        string zapretConfig = cache?.CurrentConfig ?? "general (ALT2).bat";
        bool isTgWsRunning = Process.GetProcessesByName("TgWsProxy").Length > 0;
        bool isZapretRunning = Process.GetProcessesByName("winws").Length > 0;

        string appKey = _selectedConnApp?.AppKey ?? "";

        Task.Run(() =>
        {
            var (conns, summary) = ConnectionAnalysisService.GetConnectionsForProcess(
                pids, etw, tgWsPort, isTgWsRunning, isZapretRunning, zapretConfig, appKey);

            Dispatcher.Invoke(() =>
            {
                _lastAppConnections = conns;
                _lastAppSummary = summary;
                RenderAppConnections(conns, summary);
            });
        });
    }

    private void RefreshSystemOverview()
    {
        int tgWsPort = 1080;
        var cache = ZapretConfigService.LoadCache();
        string zapretConfig = cache?.CurrentConfig ?? "general (ALT2).bat";

        Task.Run(() =>
        {
            var overview = ConnectionAnalysisService.GetSystemOverview(tgWsPort, zapretConfig);
            Dispatcher.Invoke(() => RenderSystemOverview(overview));
        });
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _journeyScrollOffsets = new();
    private readonly HashSet<string> _expandedTechDetailsKeys = new();

    private static ScrollViewer? FindParentScrollViewer(DependencyObject? child)
    {
        while (child != null)
        {
            if (child is ScrollViewer sv) return sv;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void RenderAppConnections(List<ConnectionDetailModel> conns, ConnectionSummaryModel summary)
    {
        ConnSummaryText.Text = summary.SummaryText;
        BadgeAllText.Text = $"Все: {summary.TotalCount}";
        BadgeVpnText.Text = $"VPN: {summary.VpnCount}";
        BadgeDirectText.Text = $"Прямой: {summary.DirectCount}";
        BadgeHostsText.Text = $"Hosts: {summary.HostsCount}";
        BadgeProxyText.Text = $"TgWsProxy: {summary.ProxyCount}";
        BadgeZapretText.Text = $"Zapret: {summary.ZapretCount}";

        if (BadgeZapretText.Parent is FrameworkElement zapretBadgeBorder)
        {
            zapretBadgeBorder.Visibility = summary.ZapretCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateFilterChipsUi();

        var parentScrollViewer = FindParentScrollViewer(ConnListContainer);
        double savedVerticalOffset = parentScrollViewer?.VerticalOffset ?? 0;

        ConnListContainer.Children.Clear();

        if (conns.Count == 0)
        {
            var emptyGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            emptyGrid.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)),
                StrokeThickness = 1,
                RadiusX = 10,
                RadiusY = 10
            });
            var emptyStack = new StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(24, 28, 24, 28)
            };
            emptyStack.Children.Add(new TextBlock
            {
                Text = "Нет активных сетевых соединений",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            });
            emptyStack.Children.Add(new TextBlock
            {
                Text = "Откройте страницу, чат или медиа в выбранном приложении для появления трафика.",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x7e)),
                TextAlignment = TextAlignment.Center
            });
            emptyGrid.Children.Add(emptyStack);
            ConnListContainer.Children.Add(emptyGrid);
            return;
        }

        IEnumerable<ConnectionDetailModel> filtered = conns;
        switch (_activeFilter)
        {
            case "VPN":
                filtered = conns.Where(c => c.Routing.IsVpn);
                break;
            case "Direct":
                filtered = conns.Where(c => !c.Routing.IsVpn);
                break;
            case "Hosts":
                filtered = conns.Where(c => c.Dns.IsHosts);
                break;
            case "Proxy":
                filtered = conns.Where(c => c.Proxy.HasProxy);
                break;
            case "Zapret":
                filtered = conns.Where(c => c.PacketFilter.IsZapretActive);
                break;
        }

        var filteredList = filtered.ToList();

        if (filteredList.Count == 0)
        {
            var noFilterMatches = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20, 24, 20, 24),
                Margin = new Thickness(0, 4, 0, 0)
            };
            noFilterMatches.Child = new TextBlock
            {
                Text = $"Нет соединений, подходящих под фильтр «{_activeFilter}»",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            ConnListContainer.Children.Add(noFilterMatches);
            return;
        }

        var primaryConn = filteredList[0];
        string primaryKey = $"{primaryConn.Protocol}_{primaryConn.LocalAddress}:{primaryConn.LocalPort}->{primaryConn.RemoteAddress}:{primaryConn.RemotePort}";
        bool isPrimaryExpanded = _expandedConnKeys.Contains(primaryKey);

        var primaryCard = new Grid { Margin = new Thickness(0, 0, 0, 10), Cursor = Cursors.Hand };
        var primaryBg = new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)),
            StrokeThickness = 1,
            RadiusX = 10,
            RadiusY = 10
        };
        primaryCard.Children.Add(primaryBg);

        var primaryStack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        var primaryHeader = new Grid();
        primaryHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        primaryHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        primaryHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var primaryEndpointStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var primaryEndpointTitle = new TextBlock
        {
            Text = primaryConn.RemoteDisplay,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var primaryEndpointSub = new TextBlock
        {
            Text = $"Локальный: {primaryConn.LocalAddress}:{primaryConn.LocalPort}  •  PID: {primaryConn.ProcessId}",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x94)),
            Margin = new Thickness(0, 2, 0, 0)
        };
        primaryEndpointStack.Children.Add(primaryEndpointTitle);
        primaryEndpointStack.Children.Add(primaryEndpointSub);

        Grid.SetColumn(primaryEndpointStack, 0);
        primaryHeader.Children.Add(primaryEndpointStack);

        var primaryBadges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };

        var mainTrafficBadge = CreateBadge("★ Основной", Color.FromRgb(0x22, 0x22, 0x2c), Color.FromRgb(0x94, 0xa3, 0xb8));
        mainTrafficBadge.Margin = new Thickness(0, 0, 6, 0);
        primaryBadges.Children.Add(mainTrafficBadge);

        var primaryProtoBadge = CreateBadge(primaryConn.Protocol,
            primaryConn.Protocol.StartsWith("TCP") ? Color.FromRgb(0x16, 0x24, 0x3d) : Color.FromRgb(0x2e, 0x1e, 0x10),
            primaryConn.Protocol.StartsWith("TCP") ? Color.FromRgb(0x60, 0xa5, 0xfa) : Color.FromRgb(0xfb, 0x92, 0x3c));
        primaryProtoBadge.Margin = new Thickness(0, 0, 6, 0);
        primaryBadges.Children.Add(primaryProtoBadge);

        Color primaryRouteColor = primaryConn.Routing.IsVpn ? Color.FromRgb(0xd8, 0xb4, 0xfe) : Color.FromRgb(0x86, 0xef, 0xac);
        Color primaryRouteBg = primaryConn.Routing.IsVpn ? Color.FromRgb(0x22, 0x16, 0x38) : Color.FromRgb(0x11, 0x28, 0x1c);
        var primaryRouteBadge = CreateBadge(primaryConn.PrimaryRoute, primaryRouteBg, primaryRouteColor);
        primaryRouteBadge.Margin = new Thickness(0, 0, 6, 0);
        primaryBadges.Children.Add(primaryRouteBadge);

        foreach (var mod in primaryConn.RouteModifiers)
        {
            Color modColor = mod switch
            {
                "Zapret" => Color.FromRgb(0xfd, 0xba, 0x74),
                "TgWsProxy" => Color.FromRgb(0x67, 0xe8, 0xf9),
                "Hosts" => Color.FromRgb(0xfd, 0xe0, 0x47),
                _ => Color.FromRgb(0x94, 0xa3, 0xb8)
            };
            Color modBg = mod switch
            {
                "Zapret" => Color.FromRgb(0x2e, 0x1a, 0x0c),
                "TgWsProxy" => Color.FromRgb(0x0b, 0x24, 0x2e),
                "Hosts" => Color.FromRgb(0x2b, 0x20, 0x0c),
                _ => Color.FromRgb(0x1c, 0x1c, 0x22)
            };
            var modBadge = CreateBadge($"+{mod}", modBg, modColor);
            modBadge.Margin = new Thickness(0, 0, 6, 0);
            primaryBadges.Children.Add(modBadge);
        }

        Color primaryStateColor = primaryConn.State switch
        {
            "ESTABLISHED" => Color.FromRgb(0x4a, 0xde, 0x80),
            "LISTENING" => Color.FromRgb(0xc0, 0x84, 0xfc),
            "TIME_WAIT" or "CLOSE_WAIT" => Color.FromRgb(0xfa, 0xcc, 0x15),
            _ => Color.FromRgb(0x9c, 0xa3, 0xaf)
        };
        Color primaryStateBg = primaryConn.State switch
        {
            "ESTABLISHED" => Color.FromRgb(0x11, 0x26, 0x17),
            "LISTENING" => Color.FromRgb(0x22, 0x16, 0x33),
            _ => Color.FromRgb(0x1c, 0x1c, 0x22)
        };
        var primaryStateBadge = CreateBadge(primaryConn.State, primaryStateBg, primaryStateColor);
        primaryBadges.Children.Add(primaryStateBadge);

        Grid.SetColumn(primaryBadges, 1);
        primaryHeader.Children.Add(primaryBadges);

        var primaryExpandText = new TextBlock
        {
            Text = isPrimaryExpanded ? "▲ Скрыть" : "▼ Раскрыть",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(primaryExpandText, 2);
        primaryHeader.Children.Add(primaryExpandText);

        primaryStack.Children.Add(primaryHeader);

        var primaryDetailsDrawer = CreatePacketFlowDiagram(primaryConn);
        primaryDetailsDrawer.Visibility = isPrimaryExpanded ? Visibility.Visible : Visibility.Collapsed;
        primaryStack.Children.Add(primaryDetailsDrawer);

        primaryCard.Children.Add(primaryStack);

        primaryHeader.Cursor = Cursors.Hand;
        primaryHeader.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_expandedConnKeys.Contains(primaryKey))
            {
                _expandedConnKeys.Remove(primaryKey);
                AnimateCollapse(primaryDetailsDrawer);
                primaryExpandText.Text = "▼ Раскрыть";
                primaryExpandText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }
            else
            {
                _expandedConnKeys.Add(primaryKey);
                AnimateExpand(primaryDetailsDrawer);
                primaryExpandText.Text = "▲ Скрыть";
                primaryExpandText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }
        };

        if (primaryKey != _lastPrimaryConnKey)
        {
            var anim = new DoubleAnimation(0.4, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            primaryCard.BeginAnimation(UIElement.OpacityProperty, anim);
            _lastPrimaryConnKey = primaryKey;
        }

        ConnListContainer.Children.Add(primaryCard);

        if (filteredList.Count > 1)
        {
            var secondaryList = filteredList.Skip(1).ToList();
            int loopbackCount = secondaryList.Count(c => c.IsLoopback);
            int listeningCount = secondaryList.Count(c => c.State == "LISTENING");

            var subStats = new List<string>();
            if (loopbackCount > 0) subStats.Add($"{loopbackCount} loopback");
            if (listeningCount > 0) subStats.Add($"{listeningCount} listening");
            string subText = subStats.Count > 0 ? $" ({string.Join(", ", subStats)})" : "";

            var toggleCard = new Grid { Margin = new Thickness(0, 4, 0, 8), Cursor = Cursors.Hand, Height = 36 };
            var toggleBg = new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2e)),
                StrokeThickness = 1,
                RadiusX = 8,
                RadiusY = 8
            };
            toggleCard.Children.Add(toggleBg);

            var toggleText = new TextBlock
            {
                Text = _isSecondaryListExpanded
                    ? $"▲ Скрыть остальные {secondaryList.Count} соединений"
                    : $"Ещё {secondaryList.Count} соединений{subText} ▾",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xb4)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            toggleCard.Children.Add(toggleText);

            var secondaryContainer = new StackPanel
            {
                Visibility = _isSecondaryListExpanded ? Visibility.Visible : Visibility.Collapsed
            };

            toggleCard.MouseLeftButtonUp += (_, _) =>
            {
                _isSecondaryListExpanded = !_isSecondaryListExpanded;
                secondaryContainer.Visibility = _isSecondaryListExpanded ? Visibility.Visible : Visibility.Collapsed;
                toggleText.Text = _isSecondaryListExpanded
                    ? $"▲ Скрыть остальные {secondaryList.Count} соединений"
                    : $"Ещё {secondaryList.Count} соединений{subText} ▾";
                toggleBg.Stroke = _isSecondaryListExpanded
                    ? new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x44))
                    : new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2e));
            };

            foreach (var conn in secondaryList)
            {
                string secKey = $"{conn.Protocol}_{conn.LocalAddress}:{conn.LocalPort}->{conn.RemoteAddress}:{conn.RemotePort}";
                bool isSecExpanded = _expandedConnKeys.Contains(secKey);

                var secCard = new Grid { Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
                var secBg = new System.Windows.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x17)),
                    Stroke = isSecExpanded ? new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x44)) : new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28)),
                    StrokeThickness = 1,
                    RadiusX = 8,
                    RadiusY = 8
                };
                secCard.Children.Add(secBg);

                var secStack = new StackPanel { Margin = new Thickness(14, 9, 14, 9) };

                var secRow = new Grid();
                secRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                secRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                secRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

                var secEndpointStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                var secEndpoint = new TextBlock
                {
                    Text = $"{conn.RemoteDisplay}   (PID: {conn.ProcessId})",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                secEndpointStack.Children.Add(secEndpoint);

                Grid.SetColumn(secEndpointStack, 0);
                secRow.Children.Add(secEndpointStack);

                var secBadges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var secProtoBadge = CreateBadge(conn.Protocol,
                    conn.Protocol.StartsWith("TCP") ? Color.FromRgb(0x16, 0x24, 0x3d) : Color.FromRgb(0x2e, 0x1e, 0x10),
                    conn.Protocol.StartsWith("TCP") ? Color.FromRgb(0x60, 0xa5, 0xfa) : Color.FromRgb(0xfb, 0x92, 0x3c));
                secProtoBadge.Margin = new Thickness(0, 0, 4, 0);
                secBadges.Children.Add(secProtoBadge);

                Color secRouteColor = conn.Routing.IsVpn ? Color.FromRgb(0xd8, 0xb4, 0xfe) : Color.FromRgb(0x86, 0xef, 0xac);
                Color secRouteBg = conn.Routing.IsVpn ? Color.FromRgb(0x22, 0x16, 0x38) : Color.FromRgb(0x11, 0x28, 0x1c);
                var secRouteBadge = CreateBadge(conn.PrimaryRoute, secRouteBg, secRouteColor);
                secRouteBadge.Margin = new Thickness(0, 0, 4, 0);
                secBadges.Children.Add(secRouteBadge);

                foreach (var mod in conn.RouteModifiers)
                {
                    Color modColor = mod switch
                    {
                        "Zapret" => Color.FromRgb(0xfd, 0xba, 0x74),
                        "TgWsProxy" => Color.FromRgb(0x67, 0xe8, 0xf9),
                        "Hosts" => Color.FromRgb(0xfd, 0xe0, 0x47),
                        _ => Color.FromRgb(0x94, 0xa3, 0xb8)
                    };
                    Color modBg = mod switch
                    {
                        "Zapret" => Color.FromRgb(0x2e, 0x1a, 0x0c),
                        "TgWsProxy" => Color.FromRgb(0x0b, 0x24, 0x2e),
                        "Hosts" => Color.FromRgb(0x2b, 0x20, 0x0c),
                        _ => Color.FromRgb(0x1c, 0x1c, 0x22)
                    };
                    var modBadge = CreateBadge($"+{mod}", modBg, modColor);
                    modBadge.Margin = new Thickness(0, 0, 4, 0);
                    secBadges.Children.Add(modBadge);
                }

                Color secStateColor = conn.State switch
                {
                    "ESTABLISHED" => Color.FromRgb(0x4a, 0xde, 0x80),
                    "LISTENING" => Color.FromRgb(0xc0, 0x84, 0xfc),
                    "TIME_WAIT" or "CLOSE_WAIT" => Color.FromRgb(0xfa, 0xcc, 0x15),
                    _ => Color.FromRgb(0x9c, 0xa3, 0xaf)
                };
                Color secStateBg = conn.State switch
                {
                    "ESTABLISHED" => Color.FromRgb(0x11, 0x26, 0x17),
                    "LISTENING" => Color.FromRgb(0x22, 0x16, 0x33),
                    _ => Color.FromRgb(0x1c, 0x1c, 0x22)
                };
                var secStateBadge = CreateBadge(conn.State, secStateBg, secStateColor);
                secBadges.Children.Add(secStateBadge);

                Grid.SetColumn(secBadges, 1);
                secRow.Children.Add(secBadges);

                var secArrow = new TextBlock
                {
                    Text = isSecExpanded ? "▲" : "▼",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(isSecExpanded ? Color.FromRgb(0xee, 0xee, 0xee) : Color.FromRgb(0x77, 0x77, 0x7e)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(secArrow, 2);
                secRow.Children.Add(secArrow);

                secStack.Children.Add(secRow);

                var secDetailsDrawer = CreatePacketFlowDiagram(conn);
                secDetailsDrawer.Visibility = isSecExpanded ? Visibility.Visible : Visibility.Collapsed;
                secStack.Children.Add(secDetailsDrawer);

                secCard.Children.Add(secStack);

                secRow.Cursor = Cursors.Hand;
                secRow.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (_expandedConnKeys.Contains(secKey))
                    {
                        _expandedConnKeys.Remove(secKey);
                        AnimateCollapse(secDetailsDrawer);
                        secBg.Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28));
                        secArrow.Text = "▼";
                        secArrow.Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x7e));
                    }
                    else
                    {
                        _expandedConnKeys.Add(secKey);
                        AnimateExpand(secDetailsDrawer);
                        secBg.Stroke = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x44));
                        secArrow.Text = "▲";
                        secArrow.Foreground = new SolidColorBrush(Color.FromRgb(0xee, 0xee, 0xee));
                    }
                };

                secondaryContainer.Children.Add(secCard);
            }

            ConnListContainer.Children.Add(toggleCard);
            ConnListContainer.Children.Add(secondaryContainer);
        }

        if (parentScrollViewer != null && savedVerticalOffset > 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                parentScrollViewer.ScrollToVerticalOffset(savedVerticalOffset);
            }));
        }
    }

    private static readonly Geometry DnsStageGeom = Geometry.Parse("M 20,20 H 30 V 22 H 20 Z M 20,24 H 26 V 26 H 20 Z M30,17V16A13.9871,13.9871,0,1,0,19.23,29.625l-.46-1.9463A12.0419,12.0419,0,0,1,16,28c-.19,0-.375-.0186-.563-.0273A20.3044,20.3044,0,0,1,12.0259,17Zm-2.0415-2H21.9751A24.2838,24.2838,0,0,0,19.2014,4.4414,12.0228,12.0228,0,0,1,27.9585,15ZM16.563,4.0273A20.3044,20.3044,0,0,1,19.9741,15H12.0259A20.3044,20.3044,0,0,1,15.437,4.0273C15.625,4.0186,15.81,4,16,4S16.375,4.0186,16.563,4.0273Zm-3.7644.4141A24.2838,24.2838,0,0,0,10.0249,15H4.0415A12.0228,12.0228,0,0,1,12.7986,4.4414Zm0,23.1172A12.0228,12.0228,0,0,1,4.0415,17h5.9834A24.2838,24.2838,0,0,0,12.7986,27.5586Z");
    private static readonly Geometry RoutingStageGeom = Geometry.Parse("M48,60A12,12,0,1,0,60,72,12.0081,12.0081,0,0,0,48,60Z M22.6055,46.6289A5.9994,5.9994,0,1,0,31.1133,55.09a24.2258,24.2258,0,0,1,33.7734,0,5.9512,5.9512,0,0,0,4.2539,1.77,6,6,0,0,0,4.2539-10.23C59.7773,32.918,36.2227,32.918,22.6055,46.6289Z M90.27,29.7773a59.1412,59.1412,0,0,0-84.539,0,5.9994,5.9994,0,1,0,8.5312,8.4375c18.1172-18.3281,49.3594-18.3281,67.4766,0A5.9994,5.9994,0,1,0,90.27,29.7773Z");
    private static readonly Geometry ShieldStageGeom = Geometry.Parse("M12 12.5C13.1046 12.5 14 11.6046 14 10.5C14 9.39542 13.1046 8.49999 12 8.49999C10.8954 8.49999 10 9.39542 10 10.5C10 11.6046 10.8954 12.5 12 12.5ZM12 12.5V15.5M20 12C20 16.4611 14.54 19.6937 12.6414 20.683C12.4361 20.79 12.3334 20.8435 12.191 20.8712C12.08 20.8928 11.92 20.8928 11.809 20.8712C11.6666 20.8435 11.5639 20.79 11.3586 20.683C9.45996 19.6937 4 16.4611 4 12V8.21759C4 7.41808 4 7.01833 4.13076 6.6747C4.24627 6.37113 4.43398 6.10027 4.67766 5.88552C4.9535 5.64243 5.3278 5.50207 6.0764 5.22134L11.4382 3.21067C11.6461 3.13271 11.75 3.09373 11.857 3.07827C11.9518 3.06457 12.0482 3.06457 12.143 3.07827C12.25 3.09373 12.3539 3.13271 12.5618 3.21067L17.9236 5.22134C18.6722 5.50207 19.0465 5.64243 19.3223 5.88552C19.566 6.10027 19.7537 6.37113 19.8692 6.6747C20 7.01833 20 7.41808 20 8.21759V12Z");
    private static readonly Geometry ServerStageGeom = Geometry.Parse("M18 7H18.01M15 7H15.01M18 17H18.01M15 17H15.01M6 10H18C18.9319 10 19.3978 10 19.7654 9.84776C20.2554 9.64477 20.6448 9.25542 20.8478 8.76537C21 8.39782 21 7.93188 21 7C21 6.06812 21 5.60218 20.8478 5.23463C20.6448 4.74458 20.2554 4.35523 19.7654 4.15224C19.3978 4 18.9319 4 18 4H6C5.06812 4 4.60218 4 4.23463 4.15224C3.74458 4.35523 3.35523 4.74458 3.15224 5.23463C3 5.60218 3 6.06812 3 7C3 7.93188 3 8.39782 3.15224 8.76537C3.35523 9.25542 3.74458 9.64477 4.23463 9.84776C4.60218 10 5.06812 10 6 10ZM6 20H18C18.9319 20 19.3978 20 19.7654 19.8478C20.2554 19.6448 20.6448 19.2554 20.8478 18.7654C21 18.3978 21 17.9319 21 17C21 16.0681 21 15.6022 20.8478 15.2346C20.6448 14.7446 20.2554 14.3552 19.7654 14.1522C19.3978 14 18.9319 14 18 14H6C5.06812 14 4.60218 4 4.23463 14.1522C3.74458 14.3552 3.35523 14.7446 3.15224 15.2346C3 15.6022 3 16.0681 3 17C3 17.9319 3 18.3978 3.15224 18.7654C3.35523 19.2554 3.74458 19.6448 4.23463 19.8478C4.60218 20 5.06812 20 6 20Z");
    private static readonly Geometry ArrowDownGeom = Geometry.Parse("M12,2 L12,14 M7,9 L12,14 L17,9");
    private static readonly Geometry LockStageGeom = Geometry.Parse("M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z");
    private static readonly Geometry DragGripGeom = Geometry.Parse("M8 6a1.5 1.5 0 110-3 1.5 1.5 0 010 3zm8 0a1.5 1.5 0 110-3 1.5 1.5 0 010 3zm-8 6a1.5 1.5 0 110-3 1.5 1.5 0 010 3zm8 0a1.5 1.5 0 110-3 1.5 1.5 0 010 3zm-8 6a1.5 1.5 0 110-3 1.5 1.5 0 010 3zm8 0a1.5 1.5 0 110-3 1.5 1.5 0 010 3z");
    private static readonly Geometry AppStageGeom = Geometry.Parse("M10 20l4-16m4 16l4-16M6 9h14M4 15h14");
    private static readonly Geometry CheckmarkStageGeom = Geometry.Parse("M5 13l4 4L19 7");
    private static readonly Geometry WarningStageGeom = Geometry.Parse("M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z");
    private static readonly Geometry InternetStageGeom = Geometry.Parse("M40.5,5.5H7.5a2,2,0,0,0-2,2v33a2,2,0,0,0,2,2h33a2,2,0,0,0,2-2V7.5A2,2,0,0,0,40.5,5.5Z M32.1645,32.9456V15.1479 M38,22.9637l-5.8355-7.9093-5.8356,7.9093 M15.8355,15.0544V32.8521 M21.6711,25.0363l-5.8356,7.9093L10,25.0363");

    private static readonly Geometry SettingsStageGeom = Geometry.Parse("M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.06-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61 l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41 h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.73,8.87 C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.06,0.94l-2.03,1.58 c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54 c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.43-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96 c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.49-0.12-0.61L19.14,12.94z M12,15.6c-1.98,0-3.6-1.62-3.6-3.6 s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z");
    private static readonly Geometry ChevronDownGeom = Geometry.Parse("M6 9l6 6 6-6");

    static MainWindow()
    {
        ConnTabActiveFg.Freeze();
        ConnTabInactiveFg.Freeze();
        ConnTabHoverFg.Freeze();
        DnsStageGeom.Freeze();
        RoutingStageGeom.Freeze();
        ShieldStageGeom.Freeze();
        ServerStageGeom.Freeze();
        ArrowDownGeom.Freeze();
        LockStageGeom.Freeze();
        DragGripGeom.Freeze();
        AppStageGeom.Freeze();
        CheckmarkStageGeom.Freeze();
        WarningStageGeom.Freeze();
        InternetStageGeom.Freeze();
        SettingsStageGeom.Freeze();
        ChevronDownGeom.Freeze();
    }

    private FrameworkElement CreatePacketFlowDiagram(ConnectionDetailModel conn)
    {
        string currentApp = _selectedConnApp?.AppName ?? "";
        return CreateAppJourneyChainDiagram(currentApp, conn);
    }

    private FrameworkElement CreateAppJourneyChainDiagram(string appName, ConnectionDetailModel conn)
    {
        var outerContainer = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };

        var headerGrid = new Grid { Margin = new Thickness(12, 0, 12, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerTitle = new TextBlock
        {
            Text = "Путь соединения:",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White
        };
        Grid.SetColumn(headerTitle, 0);
        headerGrid.Children.Add(headerTitle);

        var headerHint = new TextBlock
        {
            Text = "Живой маршрут обработки пакетов",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x94)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(headerHint, 1);
        headerGrid.Children.Add(headerHint);

        outerContainer.Children.Add(headerGrid);

        string connKey = $"{conn.Protocol}_{conn.LocalAddress}:{conn.LocalPort}->{conn.RemoteAddress}:{conn.RemotePort}";

        var scrollContainer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 2, 0, 6),
            Padding = new Thickness(12, 4, 12, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };

        scrollContainer.ScrollChanged += (_, e) =>
        {
            if (e.HorizontalOffset > 0 || e.HorizontalChange != 0)
            {
                _journeyScrollOffsets[connKey] = e.HorizontalOffset;
            }
        };

        if (_journeyScrollOffsets.TryGetValue(connKey, out double savedHOffset) && savedHOffset > 0)
        {
            scrollContainer.Loaded += (_, _) =>
            {
                scrollContainer.ScrollToHorizontalOffset(savedHOffset);
            };
        }

        Point _scrollStartPoint = new Point();
        double _scrollStartOffset = 0;
        bool _isMouseDown = false;

        scrollContainer.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _isMouseDown = true;
            _scrollStartPoint = e.GetPosition(scrollContainer);
            _scrollStartOffset = scrollContainer.HorizontalOffset;
            scrollContainer.CaptureMouse();
            scrollContainer.Cursor = System.Windows.Input.Cursors.Hand;
        };

        scrollContainer.PreviewMouseMove += (s, e) =>
        {
            if (_isMouseDown)
            {
                Point currentPoint = e.GetPosition(scrollContainer);
                double deltaX = currentPoint.X - _scrollStartPoint.X;
                scrollContainer.ScrollToHorizontalOffset(_scrollStartOffset - deltaX);
            }
        };

        scrollContainer.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (_isMouseDown)
            {
                _isMouseDown = false;
                scrollContainer.ReleaseMouseCapture();
                scrollContainer.Cursor = System.Windows.Input.Cursors.Arrow;
            }
        };

        scrollContainer.PreviewMouseWheel += (s, e) =>
        {
            if (!e.Handled)
            {
                e.Handled = true;
                var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = s
                };
                var parent = (s as FrameworkElement)?.Parent as UIElement;
                parent?.RaiseEvent(eventArg);
            }
        };

        var journeyPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        scrollContainer.Content = journeyPanel;

        void AddJourneyCard(string title, string subtitle, string detailInfo, Color color, Geometry iconGeom, ImageSource? iconImage = null, bool isStroke = false)
        {
            var cardBorder = new Border
            {
                Width = 196,
                Height = 72,
                Background = new SolidColorBrush(Color.FromRgb(0x13, 0x18, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(140, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0)
            };

            var cardGrid = new Grid();
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (iconImage != null)
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = iconImage,
                    Width = 22, Height = 22,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(img, 0);
                cardGrid.Children.Add(img);
            }
            else
            {
                var pathIcon = new System.Windows.Shapes.Path
                {
                    Data = iconGeom,
                    Width = 17, Height = 17,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = Stretch.Uniform
                };
                if (isStroke)
                {
                    pathIcon.Stroke = new SolidColorBrush(color);
                    pathIcon.StrokeThickness = 1.4;
                }
                else
                {
                    pathIcon.Fill = new SolidColorBrush(color);
                }
                Grid.SetColumn(pathIcon, 0);
                cardGrid.Children.Add(pathIcon);
            }

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var titleBlock = new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = title
            };
            textStack.Children.Add(titleBlock);

            var subBlock = new TextBlock
            {
                Text = subtitle,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xa3, 0xb8)),
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = subtitle
            };
            textStack.Children.Add(subBlock);

            var detailBlock = new TextBlock
            {
                Text = detailInfo,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = detailInfo
            };
            textStack.Children.Add(detailBlock);

            Grid.SetColumn(textStack, 1);
            cardGrid.Children.Add(textStack);
            cardBorder.Child = cardGrid;
            journeyPanel.Children.Add(cardBorder);
        }

        void AddArrow()
        {
            var connectorGrid = new Grid
            {
                Width = 32,
                Height = 24,
                Margin = new Thickness(5, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var arrowLine = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2,12 L 26,12 M 20,7 L 27,12 L 20,17"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeEndLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            connectorGrid.Children.Add(arrowLine);
            journeyPanel.Children.Add(connectorGrid);
        }

        string appDisplayName = _selectedConnApp?.DisplayName ?? appName;
        if (string.IsNullOrWhiteSpace(appDisplayName)) appDisplayName = "Приложение";
        AddJourneyCard(
            appDisplayName,
            $"Процесс PID {conn.ProcessId}",
            $"Сокет: {conn.Protocol} ({conn.LocalPort})",
            Color.FromRgb(0x38, 0xbd, 0xf8),
            AppStageGeom,
            _selectedConnApp?.Icon);

        if (conn.Dns.IsHosts)
        {
            AddArrow();
            AddJourneyCard(
                "Hosts-файл",
                string.IsNullOrEmpty(conn.Dns.Domain) ? "Локальная запись" : conn.Dns.Domain,
                $"{conn.Dns.Domain} ➔ {conn.RemoteAddress}",
                Color.FromRgb(0xea, 0xb3, 0x08),
                LockStageGeom,
                isStroke: true);
        }

        bool isVpn = conn.Routing.IsVpn;
        bool isZapret = conn.PacketFilter.IsZapretActive;
        bool isProxy = conn.Proxy.HasProxy;

        if (isVpn)
        {
            AddArrow();
            AddJourneyCard(
                "VPN (happ-tun)",
                "Трафик зашифрован",
                $"Адаптер: {conn.Routing.AdapterName}",
                Color.FromRgb(0xc0, 0x84, 0xfc),
                RoutingStageGeom);
        }

        if (isZapret)
        {
            AddArrow();
            AddJourneyCard(
                "Zapret",
                "Обходит DPI",
                $"Конфиг: {(string.IsNullOrEmpty(conn.PacketFilter.ConfigName) ? "general" : conn.PacketFilter.ConfigName)}",
                Color.FromRgb(0xf9, 0x73, 0x16),
                ShieldStageGeom,
                isStroke: true);
        }

        if (isProxy)
        {
            AddArrow();
            AddJourneyCard(
                "TgWsProxy",
                "Прокси Telegram",
                $"Порт: {conn.Proxy.ProxyPort} (SOCKS5)",
                Color.FromRgb(0x06, 0xb6, 0xd4),
                ServerStageGeom,
                isStroke: true);
        }

        bool isFailed = conn.State is "CLOSED" or "CLOSE_WAIT" or "TIME_WAIT" or "RESET";
        bool isDirect = !isVpn && !isZapret && !isProxy;

        AddArrow();

        if (!isFailed)
        {
            string destTitle = isDirect ? "Интернет напрямую" : "Интернет";
            string destSub = string.IsNullOrEmpty(conn.Dns.Domain) ? $"IP: {conn.RemoteAddress}" : conn.Dns.Domain;
            string destDetail = $"Статус: {conn.State} • RTT: <5мс";
            AddJourneyCard(destTitle, destSub, destDetail, Color.FromRgb(0x22, 0xc5, 0x5e), InternetStageGeom, isStroke: true);
        }
        else
        {
            string destSub = $"Ошибка ({conn.State})";
            string destDetail = $"Пакеты отклонены";
            AddJourneyCard("Сбой подключения", destSub, destDetail, Color.FromRgb(0xef, 0x44, 0x44), WarningStageGeom, isStroke: true);
        }

        outerContainer.Children.Add(scrollContainer);

        var detailsExpanderBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(12, 8, 12, 4)
        };

        var detailsStack = new StackPanel();

        string techKey = $"tech_{conn.Protocol}_{conn.LocalAddress}:{conn.LocalPort}->{conn.RemoteAddress}:{conn.RemotePort}";
        bool isTechExpanded = _expandedTechDetailsKeys.Contains(techKey);

        var headerBtnBorder = new Border
        {
            Height = 42,
            Background = System.Windows.Media.Brushes.Transparent,
            CornerRadius = new CornerRadius(9),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var headerContentGrid = new Grid
        {
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Height = 42
        };
        headerContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        headerContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

        var settingsIcon = new System.Windows.Shapes.Path
        {
            Data = SettingsStageGeom,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Fill = new SolidColorBrush(isTechExpanded ? Color.FromRgb(0xdd, 0xdd, 0xdd) : Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(settingsIcon, 0);
        headerContentGrid.Children.Add(settingsIcon);

        var toggleBtnText = new TextBlock
        {
            Text = isTechExpanded ? "Скрыть технические детали" : "Показать технические детали",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(toggleBtnText, 1);
        headerContentGrid.Children.Add(toggleBtnText);

        var chevronIcon = new System.Windows.Shapes.Path
        {
            Data = ChevronDownGeom,
            Width = 10,
            Height = 10,
            Stretch = Stretch.Uniform,
            Stroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(isTechExpanded ? 180 : 0)
        };
        Grid.SetColumn(chevronIcon, 2);
        headerContentGrid.Children.Add(chevronIcon);

        headerBtnBorder.Child = headerContentGrid;

        headerBtnBorder.MouseEnter += (_, _) =>
        {
            headerBtnBorder.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xff, 0xff, 0xff));
        };
        headerBtnBorder.MouseLeave += (_, _) =>
        {
            headerBtnBorder.Background = System.Windows.Media.Brushes.Transparent;
        };

        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2a)),
            Margin = new Thickness(14, 0, 14, 10),
            Visibility = isTechExpanded ? Visibility.Visible : Visibility.Collapsed
        };

        var detailsContentPanel = new StackPanel
        {
            Visibility = isTechExpanded ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(12, 0, 12, 12)
        };

        FrameworkElement CreateTechRow(string label, string val, Color? highlightColor = null, string? customTooltip = null, Action? onClick = null)
        {
            var rowBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1f)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x2e)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);
            rowGrid.Children.Add(lbl);

            var valueColor = highlightColor ?? Color.FromRgb(0xdd, 0xdd, 0xdd);
            var valBlock = new TextBlock
            {
                Text = val,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.Normal,
                Foreground = new SolidColorBrush(valueColor),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = customTooltip ?? val,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(valBlock, 1);
            rowGrid.Children.Add(valBlock);

            if (onClick != null)
            {
                rowBorder.Cursor = Cursors.Hand;
                rowBorder.ToolTip = customTooltip;
                rowBorder.MouseEnter += (_, _) => rowBorder.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2a));
                rowBorder.MouseLeave += (_, _) => rowBorder.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1f));
                rowBorder.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    onClick();
                };
            }

            rowBorder.Child = rowGrid;
            return rowBorder;
        }

        string exePathVal = string.IsNullOrEmpty(conn.ExecutablePath) ? "—" : conn.ExecutablePath;
        Action? onExeClick = null;
        string? exeTooltip = null;
        if (!string.IsNullOrEmpty(conn.ExecutablePath))
        {
            exeTooltip = "Нажмите, чтобы открыть папку с файлом в Проводнике";
            onExeClick = () =>
            {
                try
                {
                    if (File.Exists(conn.ExecutablePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{conn.ExecutablePath}\"",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        string? dir = Path.GetDirectoryName(conn.ExecutablePath);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                Arguments = $"\"{dir}\"",
                                UseShellExecute = true
                            });
                        }
                    }
                }
                catch { }
            };
        }

        string dnsValue = string.IsNullOrEmpty(conn.Dns.Domain) ? conn.Dns.Source : $"{conn.Dns.Domain} ({conn.Dns.Source})";
        string routingValue = $"{conn.Routing.AdapterName} [{(conn.Routing.IsVpn ? "VPN туннель" : "Физический интерфейс")}]";
        string zapretValue = isZapret ? $"Zapret активен ({conn.PacketFilter.ConfigName})" : "Прямой трафик (без Zapret)";
        string proxyValue = isProxy ? $"TgWsProxy (порт {conn.Proxy.ProxyPort})" : "Прямой сокет (без прокси)";
        string socketValue = $"{conn.LocalAddress}:{conn.LocalPort} ➔ {conn.RemoteAddress}:{conn.RemotePort} [{conn.State}]";

        detailsContentPanel.Children.Add(CreateTechRow("Путь к файлу", exePathVal, string.IsNullOrEmpty(conn.ExecutablePath) ? Color.FromRgb(0x77, 0x77, 0x82) : null, exeTooltip, onExeClick));
        detailsContentPanel.Children.Add(CreateTechRow("DNS резолвинг", dnsValue, conn.Dns.IsHosts ? Color.FromRgb(0xea, 0xb3, 0x08) : null));
        detailsContentPanel.Children.Add(CreateTechRow("Маршрутизация", routingValue, conn.Routing.IsVpn ? Color.FromRgb(0xc0, 0x84, 0xfc) : null));
        detailsContentPanel.Children.Add(CreateTechRow("Пакетный фильтр", zapretValue, isZapret ? Color.FromRgb(0xf9, 0x73, 0x16) : null));
        detailsContentPanel.Children.Add(CreateTechRow("Проксирование", proxyValue, isProxy ? Color.FromRgb(0x06, 0xb6, 0xd4) : null));
        detailsContentPanel.Children.Add(CreateTechRow("Сокет соединения", socketValue, isFailed ? Color.FromRgb(0xef, 0x44, 0x44) : Color.FromRgb(0x22, 0xc5, 0x5e)));

        headerBtnBorder.MouseLeftButtonUp += (s, e) =>
        {
            e.Handled = true;
            if (_expandedTechDetailsKeys.Contains(techKey))
            {
                _expandedTechDetailsKeys.Remove(techKey);
                detailsContentPanel.Visibility = Visibility.Collapsed;
                separator.Visibility = Visibility.Collapsed;
                toggleBtnText.Text = "Показать технические детали";
                toggleBtnText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                settingsIcon.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                chevronIcon.Stroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                chevronIcon.RenderTransform = new RotateTransform(0);
            }
            else
            {
                _expandedTechDetailsKeys.Add(techKey);
                separator.Visibility = Visibility.Visible;
                detailsContentPanel.Visibility = Visibility.Visible;
                toggleBtnText.Text = "Скрыть технические детали";
                toggleBtnText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                settingsIcon.Fill = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                chevronIcon.Stroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                chevronIcon.RenderTransform = new RotateTransform(180);
            }
        };

        detailsStack.Children.Add(headerBtnBorder);
        detailsStack.Children.Add(separator);
        detailsStack.Children.Add(detailsContentPanel);
        detailsExpanderBorder.Child = detailsStack;
        outerContainer.Children.Add(detailsExpanderBorder);

        return outerContainer;
    }

    private static void AnimateExpand(FrameworkElement element)
    {
        element.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        if (element.RenderTransform is TranslateTransform tt)
        {
            var slide = new DoubleAnimation(-8.0, 0.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            tt.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private static void AnimateCollapse(FrameworkElement element)
    {
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void RenderSystemOverview(SystemOverviewModel model)
    {
        SysZapretDot.Fill = model.WinDivertLoaded
            ? new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
            : new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        SysZapretStatus.Text = model.ZapretStatus;
        SysZapretConfig.Text = $"Конфиг: {model.ZapretConfig}";

        SysTgWsDot.Fill = model.TgWsProxyRunning
            ? new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
            : new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        SysTgWsStatus.Text = model.TgWsProxyRunning ? $"Прокси запущен (127.0.0.1:{model.TgWsProxyPort})" : "Прокси не запущен";
        SysTgWsConns.Text = $"Активных клиентов: {model.TgWsProxyConnectionsCount}";

        SysDefaultRoute.Text = $"Основной: {model.DefaultRouteAdapter}";
        SysHostsCount.Text = $"Записей в hosts: {model.HostsCount}";
        SysDnsCount.Text = $"DNS серверов: {model.DnsServers.Count}";

        SysAdaptersContainer.Children.Clear();
        foreach (var adapter in model.Adapters)
        {
            var cardGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            cardGrid.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)),
                StrokeThickness = 1,
                RadiusX = 10,
                RadiusY = 10
            });

            var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var nameBlock = new TextBlock
            {
                Text = adapter.Name,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 8, 0)
            };
            titlePanel.Children.Add(nameBlock);

            Color typeBg = adapter.IsVpn ? Color.FromRgb(0x2d, 0x1f, 0x4a) : Color.FromRgb(0x14, 0x33, 0x22);
            Color typeFg = adapter.IsVpn ? Color.FromRgb(0xc4, 0xb5, 0xfd) : Color.FromRgb(0x86, 0xef, 0xac);
            titlePanel.Children.Add(CreateBadge(adapter.Type, typeBg, typeFg));

            if (adapter.IsDefaultGateway)
            {
                var defBadge = (FrameworkElement)CreateBadge("Основной маршрут", Color.FromRgb(0x1a, 0x27, 0x44), Color.FromRgb(0x60, 0xa5, 0xfa));
                defBadge.Margin = new Thickness(6, 0, 0, 0);
                titlePanel.Children.Add(defBadge);
            }

            Grid.SetColumn(titlePanel, 0);
            topRow.Children.Add(titlePanel);

            var statusBlock = new TextBlock
            {
                Text = adapter.Status,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(statusBlock, 1);
            topRow.Children.Add(statusBlock);

            stack.Children.Add(topRow);

            var descBlock = new TextBlock
            {
                Text = adapter.Description,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x6a)),
                Margin = new Thickness(0, 4, 0, 6)
            };
            stack.Children.Add(descBlock);

            var detailsText = new TextBlock
            {
                Text = $"IPv4: {adapter.IpAddresses}   |   Шлюз: {adapter.Gateways}   |   DNS: {adapter.DnsServers}",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa))
            };
            stack.Children.Add(detailsText);

            cardGrid.Children.Add(stack);
            SysAdaptersContainer.Children.Add(cardGrid);
        }

        RenderSystemProcesses(model.ActiveProcesses);

        SysHostsContainer.Children.Clear();
        if (model.HostsEntries.Count == 0)
        {
            var noHosts = new TextBlock
            {
                Text = "В файле hosts нет активных записей.",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
            };
            SysHostsContainer.Children.Add(noHosts);
        }
        else
        {
            var hostsGrid = new Grid();
            hostsGrid.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)),
                StrokeThickness = 1,
                RadiusX = 10,
                RadiusY = 10
            });
            var hostsStack = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };

            foreach (var h in model.HostsEntries.Take(30))
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var hostText = new TextBlock { Text = h.Hostname, Foreground = System.Windows.Media.Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
                var ipText = new TextBlock { Text = h.Ip, Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)), FontSize = 12 };

                Grid.SetColumn(hostText, 0);
                Grid.SetColumn(ipText, 1);
                row.Children.Add(hostText);
                row.Children.Add(ipText);

                if (h.IsNetFixManaged)
                {
                    var tag = CreateBadge("NetFix", Color.FromRgb(0x33, 0x28, 0x10), Color.FromRgb(0xfd, 0xe0, 0x47));
                    Grid.SetColumn(tag, 2);
                    row.Children.Add(tag);
                }

                hostsStack.Children.Add(row);
            }

            hostsGrid.Children.Add(hostsStack);
            SysHostsContainer.Children.Add(hostsGrid);
        }
    }

    private string _sysProcSortColumn = "Sockets";
    private bool _sysProcSortAscending = false;
    private bool _sysProcessesExpanded = false;
    private List<SystemProcessActivityModel> _lastSysProcesses = [];

    private void RenderSystemProcesses(List<SystemProcessActivityModel> processes)
    {
        _lastSysProcesses = processes;
        SysProcessesContainer.Children.Clear();

        if (processes.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "Нет активных процессов с открытыми сетевыми сокетами.",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
            };
            SysProcessesContainer.Children.Add(emptyText);
            return;
        }

        int totalSockets = processes.Sum(p => p.SocketsCount);
        var summaryText = new TextBlock
        {
            Text = $"{processes.Count} процессов · {totalSockets} сокетов активно",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)),
            Margin = new Thickness(2, 0, 0, 8)
        };
        SysProcessesContainer.Children.Add(summaryText);

        var sorted = _sysProcSortColumn switch
        {
            "Process" => _sysProcSortAscending ? processes.OrderBy(p => p.ProcessName).ToList() : processes.OrderByDescending(p => p.ProcessName).ToList(),
            "Cpu" => _sysProcSortAscending ? processes.OrderBy(p => p.CpuPercent ?? -1).ToList() : processes.OrderByDescending(p => p.CpuPercent ?? -1).ToList(),
            "Ram" => _sysProcSortAscending ? processes.OrderBy(p => p.RamBytes).ToList() : processes.OrderByDescending(p => p.RamBytes).ToList(),
            "Network" => _sysProcSortAscending ? processes.OrderBy(p => p.BytesPerSec).ThenBy(p => p.TotalBytes).ToList() : processes.OrderByDescending(p => p.BytesPerSec).ThenByDescending(p => p.TotalBytes).ToList(),
            "Sockets" => _sysProcSortAscending ? processes.OrderBy(p => p.SocketsCount).ToList() : processes.OrderByDescending(p => p.SocketsCount).ToList(),
            "Route" => _sysProcSortAscending ? processes.OrderBy(p => p.PrimaryRoute).ToList() : processes.OrderByDescending(p => p.PrimaryRoute).ToList(),
            _ => processes.OrderByDescending(p => p.SocketsCount).ToList()
        };

        var displayList = (!_sysProcessesExpanded && sorted.Count > 15) ? sorted.Take(15).ToList() : sorted;

        var tableCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x30)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0)
        };

        var tableStack = new StackPanel();

        var headerGrid = new Grid
        {
            Height = 36,
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x20))
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });

        FrameworkElement CreateProcHeaderCell(string title, string sortKey, int colIdx, System.Windows.HorizontalAlignment align = System.Windows.HorizontalAlignment.Left)
        {
            var btn = new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
                Padding = new Thickness(10, 0, 6, 0)
            };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
            var hText = new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_sysProcSortColumn == sortKey ? Color.FromRgb(0xee, 0xee, 0xee) : Color.FromRgb(0x88, 0x88, 0x90)),
                VerticalAlignment = VerticalAlignment.Center
            };
            hStack.Children.Add(hText);

            if (_sysProcSortColumn == sortKey)
            {
                var sortArrow = new TextBlock
                {
                    Text = _sysProcSortAscending ? " ▲" : " ▼",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xb4)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                hStack.Children.Add(sortArrow);
            }

            btn.Child = hStack;
            btn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_sysProcSortColumn == sortKey)
                {
                    _sysProcSortAscending = !_sysProcSortAscending;
                }
                else
                {
                    _sysProcSortColumn = sortKey;
                    _sysProcSortAscending = false;
                }
                RenderSystemProcesses(_lastSysProcesses);
            };

            Grid.SetColumn(btn, colIdx);
            return btn;
        }

        headerGrid.Children.Add(CreateProcHeaderCell("Процесс", "Process", 0));
        headerGrid.Children.Add(CreateProcHeaderCell("CPU", "Cpu", 1));
        headerGrid.Children.Add(CreateProcHeaderCell("Память", "Ram", 2));
        headerGrid.Children.Add(CreateProcHeaderCell("Сеть", "Network", 3));
        headerGrid.Children.Add(CreateProcHeaderCell("Сокеты", "Sockets", 4));
        headerGrid.Children.Add(CreateProcHeaderCell("Маршрут", "Route", 5));

        tableStack.Children.Add(headerGrid);

        tableStack.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x30)),
            SnapsToDevicePixels = true
        });

        var rowsStack = new StackPanel();

        for (int i = 0; i < displayList.Count; i++)
        {
            var proc = displayList[i];
            var rowBorder = new Border
            {
                Height = 36,
                Background = i % 2 == 1 ? new SolidColorBrush(Color.FromArgb(0x14, 0xff, 0xff, 0xff)) : System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            int rowIdx = i;
            rowBorder.MouseEnter += (_, _) => rowBorder.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xff, 0xff, 0xff));
            rowBorder.MouseLeave += (_, _) => rowBorder.Background = rowIdx % 2 == 1 ? new SolidColorBrush(Color.FromArgb(0x14, 0xff, 0xff, 0xff)) : System.Windows.Media.Brushes.Transparent;

            rowBorder.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                NavigateToAppAnalysis(proc.ProcessId, proc.ProcessName);
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });

            var procStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0)
            };

            if (proc.Icon != null)
            {
                var iconImg = new System.Windows.Controls.Image
                {
                    Source = proc.Icon,
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);
                procStack.Children.Add(iconImg);
            }

            var nameText = new TextBlock
            {
                Text = proc.ProcessName,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            procStack.Children.Add(nameText);

            var pidText = new TextBlock
            {
                Text = $" ({proc.ProcessId})",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x70)),
                VerticalAlignment = VerticalAlignment.Center
            };
            procStack.Children.Add(pidText);

            Grid.SetColumn(procStack, 0);
            rowGrid.Children.Add(procStack);

            var cpuText = new TextBlock
            {
                Text = proc.CpuDisplay,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                Foreground = proc.CpuPercent > 5.0 ? new SolidColorBrush(Color.FromRgb(0xfd, 0xba, 0x74)) : new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xb4)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(cpuText, 1);
            rowGrid.Children.Add(cpuText);

            var ramText = new TextBlock
            {
                Text = proc.RamDisplay,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xb4)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(ramText, 2);
            rowGrid.Children.Add(ramText);

            var speedText = new TextBlock
            {
                Text = proc.NetworkActivityDisplay,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = proc.BytesPerSec >= 50 ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = proc.BytesPerSec >= 50
                    ? new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80))
                    : (proc.TotalBytes > 0 ? new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xb4)) : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x70))),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            if (proc.NetworkActivityDisplay == "н/д")
            {
                speedText.ToolTip = "Сетевая статистика недоступна в этой версии Windows";
                ToolTipService.SetInitialShowDelay(speedText, 50);
            }
            Grid.SetColumn(speedText, 3);
            rowGrid.Children.Add(speedText);

            var socketsText = new TextBlock
            {
                Text = $"{proc.SocketsCount}",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(socketsText, 4);
            rowGrid.Children.Add(socketsText);

            var routeStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0)
            };

            Color routeColor = proc.PrimaryRoute == "VPN" ? Color.FromRgb(0xd8, 0xb4, 0xfe) : Color.FromRgb(0x86, 0xef, 0xac);
            Color routeBg = proc.PrimaryRoute == "VPN" ? Color.FromRgb(0x22, 0x16, 0x38) : Color.FromRgb(0x11, 0x28, 0x1c);
            var primaryRouteBadge = CreateBadge(proc.PrimaryRoute, routeBg, routeColor);
            primaryRouteBadge.Margin = new Thickness(0, 0, 4, 0);
            routeStack.Children.Add(primaryRouteBadge);

            foreach (var mod in proc.RouteModifiers)
            {
                Color modColor = mod switch
                {
                    "Zapret" => Color.FromRgb(0xfd, 0xba, 0x74),
                    "TgWsProxy" => Color.FromRgb(0x67, 0xe8, 0xf9),
                    "Hosts" => Color.FromRgb(0xfd, 0xe0, 0x47),
                    _ => Color.FromRgb(0x94, 0xa3, 0xb8)
                };
                Color modBg = mod switch
                {
                    "Zapret" => Color.FromRgb(0x2e, 0x1a, 0x0c),
                    "TgWsProxy" => Color.FromRgb(0x0b, 0x24, 0x2e),
                    "Hosts" => Color.FromRgb(0x2b, 0x20, 0x0c),
                    _ => Color.FromRgb(0x1c, 0x1c, 0x22)
                };
                var modBadge = CreateBadge($"+{mod}", modBg, modColor);
                modBadge.Margin = new Thickness(0, 0, 3, 0);
                routeStack.Children.Add(modBadge);
            }

            Grid.SetColumn(routeStack, 5);
            rowGrid.Children.Add(routeStack);

            rowBorder.Child = rowGrid;
            rowsStack.Children.Add(rowBorder);

            if (i < displayList.Count - 1)
            {
                rowsStack.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28)),
                    SnapsToDevicePixels = true
                });
            }
        }

        tableStack.Children.Add(rowsStack);

        if (sorted.Count > 15)
        {
            var toggleBtn = new Border
            {
                Height = 34,
                Background = new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x32)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Cursor = Cursors.Hand,
                CornerRadius = new CornerRadius(0, 0, 10, 10)
            };
            var toggleText = new TextBlock
            {
                Text = _sysProcessesExpanded ? "Свернуть список (показать топ-15)" : $"Показать все процессы ({sorted.Count})",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            toggleBtn.Child = toggleText;
            toggleBtn.MouseEnter += (_, _) => toggleBtn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2c));
            toggleBtn.MouseLeave += (_, _) => toggleBtn.Background = new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x20));
            toggleBtn.MouseLeftButtonUp += (_, _) =>
            {
                _sysProcessesExpanded = !_sysProcessesExpanded;
                RenderSystemProcesses(_lastSysProcesses);
            };
            tableStack.Children.Add(toggleBtn);
        }

        tableCard.Child = tableStack;
        SysProcessesContainer.Children.Add(tableCard);
    }

    private void NavigateToAppAnalysis(int pid, string processName)
    {
        if (_isSystemMode)
        {
            _isSystemMode = false;
            AnimateConnModeSwitch(false);
            ConnAppView.Visibility = Visibility.Visible;
            ConnSystemView.Visibility = Visibility.Collapsed;
        }

        var foundApp = _allProcesses.FirstOrDefault(p => p.ProcessIds.Contains(pid))
                    ?? _allProcesses.FirstOrDefault(p => p.AppName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    ?? _allProcesses.FirstOrDefault(p => p.DisplayName.Equals(processName, StringComparison.OrdinalIgnoreCase));

        if (foundApp != null)
        {
            SelectApplication(foundApp);
        }
        else
        {
            string exe = "";
            try { exe = Process.GetProcessById(pid).MainModule?.FileName ?? ""; } catch { }
            var newApp = new ProcessItemModel
            {
                AppName = processName,
                DisplayName = processName,
                ExePath = exe,
                ProcessIds = [pid],
                ConnectionCount = 1
            };
            SelectApplication(newApp);
        }
    }

    private static FrameworkElement CreateBadge(string text, Color bg, Color fg)
    {
        var grid = new Grid();
        grid.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(bg),
            RadiusX = 4,
            RadiusY = 4
        });
        grid.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(fg),
            Margin = new Thickness(6, 2, 6, 2)
        });

        if (text.Equals("VPN", StringComparison.OrdinalIgnoreCase))
        {
            grid.ToolTip = CreateVpnToolTip();
            grid.Cursor = Cursors.Help;
            ToolTipService.SetInitialShowDelay(grid, 50);
            ToolTipService.SetBetweenShowDelay(grid, 0);
            ToolTipService.SetShowDuration(grid, 30000);
        }

        return grid;
    }

    private static System.Windows.Controls.ToolTip CreateVpnToolTip()
    {
        var tip = new System.Windows.Controls.ToolTip
        {
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1b)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xf4, 0xf4, 0xf5)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x2a)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            MaxWidth = 340
        };

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "Маршрут: VPN",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd8, 0xb4, 0xfe)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        sp.Children.Add(new TextBlock
        {
            Text = "В NetFix нет встроенного VPN.\n\nЭтот статус лишь показывает, что соединение идёт через сторонний VPN-клиент на вашем ПК (например Happ, Amnezia, WireGuard и др.).\n\nЕсли отключить сторонний VPN, маршрут сменится на «Прямой» (с защитой Zapret).",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd8)),
            TextWrapping = TextWrapping.Wrap
        });

        tip.Content = sp;
        return tip;
    }

    private static FrameworkElement CreatePipelineChip(string text, Color bg, Color fg)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(bg),
            Stroke = new SolidColorBrush(Color.FromArgb(80, fg.R, fg.G, fg.B)),
            StrokeThickness = 1,
            RadiusX = 6,
            RadiusY = 6
        });
        grid.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(fg),
            Margin = new Thickness(8, 4, 8, 4)
        });
        return grid;
    }

    private static TextBlock CreateArrow()
    {
        return new TextBlock
        {
            Text = "➔",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x70)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
    }

    private static void AddDetailRow(Grid grid, int rowIdx, string title, string content, Color titleColor)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(titleColor),
            Width = 140
        });
        row.Children.Add(new TextBlock
        {
            Text = content,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetRow(row, rowIdx);
        grid.Children.Add(row);
    }

    #endregion

    private void DiagRunBtn_Click(object s, RoutedEventArgs e)
    {
        _discord.IsScanning = true;
        _discord.SetDiagnostics(0, 0);

        DiagRunBtn.IsEnabled = false;
        DiagRunBtn.Content = "⏳  Проверяю…";
        DiagProg.Value = 0;
        DiagProgLbl.Text = "Запускаю диагностику…";
        DiagProgLbl.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
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

        var (em, title, detail, ck) = DiagnosticsEngine.HumanVerdict(r);
        AddCard(DiagResults, $"{em}  {title}", detail, ColorFromKey(ck));

        var (dem, dtitle, ddetail, dck) = DiagnosticsEngine.DiscordVerdict(r);
        AddCard(DiagResults, $"{dem}  {dtitle}", ddetail, ColorFromKey(dck));

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

        string noteText = "Примечание! Отправка медиафайлов (именно отправка) даже с включённым TgWsProxy может работать нестабильно, файлы могут загружаться очень долго. К сожалению, это не решить без использования VPN. Но просмотр и загрузка видео, стикеров и любого другого контента в Telegram должны работать идеально!";
        AddCard(DiagResults, "Важное примечание", noteText, Color.FromRgb(0x3b, 0x82, 0xf6));

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

            if (r.AppStatus != null && r.AppStatus.TgWsProxyRunning)
            {
                var serverNoteText = new TextBlock
                {
                    Text = "Примечание: У вас включен TgWsProxy. Даже если выше указано, что сервера недоступны, не переживайте, на вашем ПК Telegram будет работать нормально.\n\n" +
                           "Связь с TG идет через этот прокси, а диагностика проверяет сервера прямой отправкой пакетов, которые блокируются. Поэтому они и помечаются как «недоступные».\n\n" +
                           "Важно: Сервера будут помечены как стабильные и пинг будет нормальным только в том случае, если у вас включен VPN, а без него они всегда будут «недоступны» :). Так что всё ок!",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)),
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

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        string iconKey = null;
        string titleText = title;

        if (title.Contains("tg-ws-proxy") || title.Contains("Telegram"))
        {
            iconKey = "TelegramIcon";
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }
        else if (title.Contains("Discord"))
        {
            iconKey = "DiscordIcon";
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }
        else
        {
            titleText = title.Replace("🟢", "").Replace("🔴", "").Replace("🟡", "").TrimStart();
        }

        if (iconKey != null)
        {
            Geometry iconGeometry = null;

            iconGeometry = System.Windows.Application.Current.TryFindResource(iconKey) as PathGeometry;

            if (iconGeometry == null)
            {
                if (iconKey == "TelegramIcon")
                {
                    iconGeometry = Geometry.Parse("M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z");
                }
                else if (iconKey == "DiscordIcon")
                {
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

    private void LoadSettingsToPanel()
    {
        _settingsLoaded = false;
        ZapretBox.Text   = _settings.ZapretPath;
        TgWsBox.Text     = _settings.TgWsProxyPath;
        AutoTgWsCB.IsChecked    = _settings.AutostartTgWsProxy;
        AutoAppCB.IsChecked     = _settings.AutostartApp;
        StartMinimizedCB.IsChecked = _settings.StartMinimizedToTray;
        StartMinimizedCB.IsEnabled = AutoAppCB.IsChecked == true;
        _settings.TgWsProxyCheckUpdates = TgWsProxySettingsService.GetCheckUpdates();
        TgWsCheckUpdatesCB.IsChecked    = _settings.TgWsProxyCheckUpdates;
        DiscordRpcCB.IsChecked      = _settings.DiscordRpcEnabled;
        AutoUpdatesCB.IsChecked     = _settings.AutoUpdates;
        UseZapretCB.IsChecked = _settings.EnableZapret;
        UseTgWsCB.IsChecked = _settings.EnableTgWsProxy;
        AutoEacBypassCB.IsChecked = _settings.AutoEacBypass;
        ShowGameOfferCB.IsChecked   = _settings.ShowGameOfferDialog;
        ShowServiceReminderCB.IsChecked = _settings.ShowLongCheckDialog;
        UpdateFixModeVisual(_settings.Mode);
        ComboEffectCB.IsChecked = _settings.DisableComboEffect;
        VolumeSlider.Value = _settings.GameVolume;
        double linear = Math.Pow(_settings.GameVolume, 3);
        _editorPlayer.Volume = linear;
        _previewPlayer.Volume = linear;
        if (VolumePercent != null)
            VolumePercent.Text = $"{(int)(_settings.GameVolume * 100)}%";
        RememberSizeCB.IsChecked = _settings.RememberWindowSize;
        ForceNetOkCB.IsChecked = _settings.ForceNetworkOk;
        LoadKeyLabels();
        _settingsLoaded = true;
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
        _settings.EnableZapret = UseZapretCB.IsChecked == true;
        _settings.EnableTgWsProxy = UseTgWsCB.IsChecked == true;
        _settings.AutoEacBypass = AutoEacBypassCB.IsChecked == true;
        _settings.RememberWindowSize = RememberSizeCB.IsChecked == true;
        _settings.ForceNetworkOk = ForceNetOkCB.IsChecked == true;
        SettingsService.Save(_settings);
        SetAutostart(_settings.AutostartApp);

        if (_settings.AutoEacBypass)
            AntiCheatBypassService.StartWatcher(OnAntiCheatDetected);
        else
            AntiCheatBypassService.StopWatcher();
    }

    private void OnAntiCheatDetected(string processName)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AppendLog($"🎮 Запуск игры ({processName}). Zapret перезапущен для обхода античита.", "info");
        });
    }

    private void EacInfoBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowNotification("Обход античитов (EAC / BattlEye)",
            "При запуске игр вроде Rust или Apex античит намертво закрывает Zapret. Сама игра при этом запускается, но Discord и YouTube перестают работать.\n\n" +
            "Включите эту функцию, и NetFix сделает всё за вас: автоматически перезапустит Zapret сразу после старта игры, и вы сможете пользоваться Discord как обычно.\n\n" +
            "⚠️ Внимание: античиты следят за любыми сетевыми драйверами в системе. Если вы переживаете за свой аккаунт, включайте эту функцию на свой страх и риск!",
            "#3b82f6");
    }

    private void SaveWindowPosition()
    {
        if (!_settings.RememberWindowSize) return;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            SettingsService.Save(_settings);
        }
    }

    private void ResetWindowSizeBtn_Click(object sender, RoutedEventArgs e)
    {
        Width = 880;
        Height = 680;
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is not null)
        {
            var bounds = screen.WorkingArea;
            Left = (bounds.Width - 880) / 2.0 + bounds.Left;
            Top = (bounds.Height - 680) / 2.0 + bounds.Top;
        }
        if (_settings.RememberWindowSize)
            SaveWindowPosition();
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
        if (!_settingsLoaded) return;
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

        double linear = Math.Pow(e.NewValue, 3);
        _editorPlayer.Volume = linear;
        _previewPlayer.Volume = linear;
        _settings.GameVolume = e.NewValue;
        if (VolumePercent != null)
            VolumePercent.Text = $"{(int)(e.NewValue * 100)}%";
        SettingsService.Save(_settings);
    }



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

    private void ExportSettings_Click(object s, RoutedEventArgs e)
    {
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
                {
                    Directory.CreateDirectory(cacheDir);
                }

                if (File.Exists(cacheFile))
                {
                    var backupFile = cacheFile + $".backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
                    File.Copy(cacheFile, backupFile, true);
                }

                File.Copy(dlg.FileName, cacheFile, true);

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

    private Border? _currentNotificationOverlay;
    private Border? _currentNotificationCard;

    private void CloseCurrentNotification()
    {
        if (_currentNotificationOverlay != null)
        {
            MainGrid.Children.Remove(_currentNotificationOverlay);
            _currentNotificationOverlay = null;
        }
        if (_currentNotificationCard != null)
        {
            MainGrid.Children.Remove(_currentNotificationCard);
            _currentNotificationCard = null;
        }
    }

    private void ShowNotification(string title, string message, string color)
    {
        CloseCurrentNotification();

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            Cursor = Cursors.Hand
        };
        Grid.SetRowSpan(overlay, 3);
        overlay.MouseLeftButtonUp += (_, _) => CloseCurrentNotification();
        MainGrid.Children.Add(overlay);
        _currentNotificationOverlay = overlay;

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
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                ShadowDepth = 0,
                Opacity = 0.6
            }
        };
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(card, TextRenderingMode.Grayscale);
        Grid.SetRowSpan(card, 3);

        var content = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            FontFamily = new FontFamily("Segoe UI")
        };
        content.Children.Add(titleText);

        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd8)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 20),
            FontFamily = new FontFamily("Segoe UI")
        };
        content.Children.Add(messageText);

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
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            }
        };

        okBorder.MouseEnter += (_, _) =>
        {
            okBorder.Background = new SolidColorBrush(Color.FromArgb(
                baseColor.A,
                (byte)(baseColor.R * 0.85),
                (byte)(baseColor.G * 0.85),
                (byte)(baseColor.B * 0.85)
            ));
        };

        okBorder.MouseLeave += (_, _) =>
        {
            okBorder.Background = new SolidColorBrush(baseColor);
        };

        okBorder.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseCurrentNotification();
        };

        content.Children.Add(okBorder);
        card.Child = content;

        MainGrid.Children.Add(card);
        _currentNotificationCard = card;
    }

    private void DonateBtn_Click(object s, RoutedEventArgs e)
    {
        var w = new DonateWindow { Owner = this };
        w.ShowDialog();
    }

    private void SupportBtn_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://t.me/sofirka_hanabi");
    private void LinkZapret_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");
    private void LinkTgWs_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Flowseal/tg-ws-proxy");
    private void LinkNetFix_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/rupleide/NetFix");
    private void LinkNetFixMobile_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/rupleide/NetFixMobile");

    private void OpenTelegramChannel_Click(object s, RoutedEventArgs e)
    {
        try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "https://t.me/NetFixRuBi",
                UseShellExecute = true
            });
        } catch { }
    }

    private void CheckUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        var window = new NetFix.Views.UpdateWindow();
        window.Owner = this;
        window.ShowDialog();
    }

    private void PlayMenuBtn_Click(object s, MouseButtonEventArgs e)
    {
        ServicesBtn.IsEnabled = false;
        GameNavBtn.IsEnabled = false;
        FaqNavBtn.IsEnabled = false;
        DiagNavBtn.IsEnabled = false;
        SettingsBtn.IsEnabled = false;
        ModsNavBtn.IsEnabled = false;

        LoadUserLevels();
        ShowGameView(GameTrackSelectView);

        ServicesBtn.IsEnabled = true;
        GameNavBtn.IsEnabled = true;
        FaqNavBtn.IsEnabled = true;
        DiagNavBtn.IsEnabled = true;
        SettingsBtn.IsEnabled = true;
        ModsNavBtn.IsEnabled = true;
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

        LoadBuiltInTracks();
    }

    private void LoadBuiltInTracks()
    {
        var builtInTracks = GetBuiltInTracks();

        BuiltInTracksList.Children.Clear();

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

        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetFix_Tracks", map.Title ?? "track");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var archive = ZipFile.OpenRead(map.LevelDir!);

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

            GamePage.Visibility = Visibility.Visible;
            ShowGameView(GamePlayView);
            StartGame(map.Notes, mp3Path, map.Title ?? "NetFix Track", bpm);
        }
        catch (Exception ex)
        {
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

        var zipFiles = Directory.GetFiles(BuiltInTracksDir, "*.zip");

        foreach (var zipPath in zipFiles)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);

                var notesEntry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals("notes.json", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith("notes.json", StringComparison.OrdinalIgnoreCase));

                if (notesEntry == null) continue;

                using var stream = notesEntry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();

                var map = JsonSerializer.Deserialize<NoteMap>(json);
                if (map != null)
                {
                    map.LevelDir = zipPath;
                    result.Add(map);
                }
            }
            catch
            {
            }
        }

        return result;
    }

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

    private const double FALL_SEC = 1.6;
    private const double REFERENCE_BPM = 140.0;
    private const double HIT_PERFECT = 0.06;
    private const double HIT_GOOD = 0.15;

    private const double LANE_WIDTH = 50;
    private const double LANE_SPACING = 60;
    private const double LANE_OFFSET = 85;
    private const double CANVAS_WIDTH = 400;
    private const double NOTE_SIZE = 50;
    private const double ARROW_FONT_SIZE = 20;

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

        ServicesBtn.IsEnabled = false;
        GameNavBtn.IsEnabled = false;
        FaqNavBtn.IsEnabled = false;
        DiagNavBtn.IsEnabled = false;
        SettingsBtn.IsEnabled = false;
        ModsNavBtn.IsEnabled = false;

        var blurEffect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 };
        MainPage.Effect = blurEffect;
        MainPage.Opacity = 0.45;

        _gameOverlayPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 0x0d, 0x0d, 0x18)),
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Opacity = 0
        };
        Panel.SetZIndex(_gameOverlayPanel, 8);

        var innerBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Margin = new Thickness(0)
        };

        var trackSelectGrid = BuildInlineTrackSelect();
        innerBorder.Child = trackSelectGrid;
        _gameOverlayPanel.Child = innerBorder;

        Grid.SetRow(_gameOverlayPanel, 1);
        ContentGrid.Children.Add(_gameOverlayPanel);

        _gameOverlayPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void HideGameOverlay()
    {
        if (_gameOverlayPanel == null) return;

        ServicesBtn.IsEnabled = true;
        GameNavBtn.IsEnabled = true;
        FaqNavBtn.IsEnabled = true;
        DiagNavBtn.IsEnabled = true;
        SettingsBtn.IsEnabled = true;
        ModsNavBtn.IsEnabled = true;

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

        var sep = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x33)),
            Margin = new Thickness(0, 12, 0, 0)
        };

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
                        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetFix_Tracks", capturedMap.Title ?? "track");
                        Directory.CreateDirectory(tempDir);

                        try
                        {
                            using var archive = ZipFile.OpenRead(capturedMap.LevelDir!);

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
        MainPage.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 5 };
        MainPage.Opacity = 0.35;

        ServicesBtn.IsEnabled = false;
        GameNavBtn.IsEnabled = false;
        FaqNavBtn.IsEnabled = false;
        DiagNavBtn.IsEnabled = false;
        SettingsBtn.IsEnabled = false;
        ModsNavBtn.IsEnabled = false;

        EditorMenuBtn.Visibility = Visibility.Collapsed;

        GamePage.Background = new SolidColorBrush(Colors.Transparent);
        GamePage.Visibility = Visibility.Visible;
        Panel.SetZIndex(GamePage, 9);

        ShowGameView(GamePlayView);

        _gameOverlayActive = true;

        GamePage.Opacity = 0;
        GamePage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        startGameAction();
    }

    private void StartGame(List<NoteEntry> notes, string? mp3Path, string title, double bpm)
    {
        StopGame();

        _lastGameNotes = notes.Select(n => new NoteEntry { Time = n.Time, Lane = n.Lane }).ToList();
        _lastGameMp3Path = mp3Path;
        _lastGameTitle = title;
        _lastGameBpm = bpm;

        if (!_gameOverlayActive)
        {
            ServicesBtn.IsEnabled = false;
            FaqNavBtn.IsEnabled = false;
            DiagNavBtn.IsEnabled = false;
            SettingsBtn.IsEnabled = false;
            ModsNavBtn.IsEnabled = false;
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

        _effectTimer?.Stop();
        _effectTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(16) };
        _effectTimer.Tick += (_, _) => {
            for (int i = 0; i < 3 && _effectQueue.TryDequeue(out var action); i++)
                action();
        };
        _effectTimer.Start();

        _countdownTimer.Start();

        _discordGameTimer?.Stop();
        _discordGameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _discordGameTimer.Tick += (_, _) => {
            int acc = _totalNotes > 0 ? (int)((double)_hitNotes / _totalNotes * 100) : 100;
            _discord.SetGamePlaying(_currentTrackTitle, _gameCombo, acc, _gameStartDateTime);
        };
        _discordGameTimer.Start();
        _discord.SetGamePlaying(_currentTrackTitle, 0, 100, _gameStartDateTime);
    }

    private static double GetFallSecondsForBpm(double bpm)
    {
        if (bpm <= 0) bpm = REFERENCE_BPM;
        double ratio = REFERENCE_BPM / bpm;
        double adjusted = Math.Pow(ratio, 1.2);
        return Math.Clamp(FALL_SEC * adjusted, 0.6, 2.6);
    }

    private void RebuildGameCanvasBase()
    {
        double canvasH = GameCanvas.ActualHeight > 0 ? GameCanvas.ActualHeight : 500;
        double hitY = canvasH - 70;
        GameCanvas.Children.Clear();

        for (int i = 0; i < 4; i++)
        {
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

        var toRemove = new List<NoteEntry>();
        foreach (var note in _activeNotes)
        {
            if (note.Visual == null) continue;
            double progress = (now - (note.Time - _currentFallSec)) / _currentFallSec;
            double top = -50 + progress * (hitY + 50);
            Canvas.SetTop(note.Visual, top);

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

        if (_judgeVisibleUntil > 0 && now >= _judgeVisibleUntil)
        {
            JudgeText.Opacity = 0;
            _judgeVisibleUntil = -1;
        }

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
        if (e.IsRepeat)
            return;

        int lane = GetGameLane(e.Key);
        if (lane < 0) return;
        e.Handled = true;

        _activeLanes.Add(lane);

        double now = _gameClock.Elapsed.TotalSeconds;

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

        if (judge == "PERFECT")
        {
            _perfectStreak++;
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

        CheckGameEvents();
    }

    private void UpdateComboAura()
    {
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

        var (mainColor, accentColor, announceText, alpha) = levels[newLevel - 1];
        Color c = mainColor;
        _currentComboColor = mainColor;

        var vignette = new System.Windows.Shapes.Rectangle
        {
            Tag = "combo_aura",
            IsHitTestVisible = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };

        var stops = new GradientStopCollection();

        if (newLevel >= 10 && accentColor.HasValue)
        {
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

        vignette.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        double pulse = 0;
        double speed = 0.025 + newLevel * 0.010;
        double ampli = 0.15 + newLevel * 0.010;

        _auroraGameTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(40) };
        _auroraGameTimer.Tick += (_, _) =>
        {
            pulse += speed;
            double p = 1.0 - ampli + Math.Sin(pulse) * ampli;
            vignette.Opacity = p;
        };
        _auroraGameTimer.Start();

        if (newLevel >= 4) StartStarBurst(c, Math.Min(newLevel - 3, 4));

        SpawnComboAnnounce(newLevel, c, announceText);
    }

    private void StartStarBurst(Color color, int level)
    {
        _starTimer?.Stop();

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
            double viewWidth  = GameCanvas.ActualWidth;
            double viewHeight = GameCanvas.ActualHeight;

            for (int i = 0; i < count; i++)
            {
                double startX = rng.NextDouble() * viewWidth;
                double startY = viewHeight + 10;
                double endX   = startX + rng.Next(-80, 80);
                double endY   = rng.Next((int)(viewHeight * 0.2), (int)(viewHeight * 0.7));
                double size   = rng.Next(6, level >= 3 ? 18 : 14);
                double dur    = 1200 + rng.Next(0, 600);

                Color starColor;
                if (color.R >= 250 && color.G >= 250 && color.B >= 250)
                {
                    var rainbowColors = new[]
                    {
                        Color.FromRgb(0xff, 0x6b, 0xb5),
                        Color.FromRgb(0xff, 0xd7, 0x00),
                        Color.FromRgb(0x00, 0xff, 0xff),
                        Color.FromRgb(0xff, 0x45, 0x00),
                        Color.FromRgb(0xec, 0x4e, 0xff),
                        Color.FromRgb(0x22, 0xc5, 0x5e),
                    };
                    starColor = rainbowColors[rng.Next(rainbowColors.Length)];
                }
                else
                {
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
                GameCanvas.Children.Add(star);

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
                fade.Completed += (_, _) => GameCanvas.Children.Remove(star);

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

        if (!_halfwayTriggered && _totalNotes > 0 && totalPlayed >= _totalNotes / 2)
        {
            _halfwayTriggered = true;
            SpawnMilestoneAnnounce("ПОЛПУТИ! 🎯", Color.FromRgb(0x06, 0xb6, 0xd4));
            FlashScreenOnce(Color.FromArgb(30, 0x06, 0xb6, 0xd4), 800);
        }

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

        var danger = new System.Windows.Shapes.Rectangle
        {
            Tag = "danger_vignette",
            IsHitTestVisible = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };

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

        _dangerPulseTimer?.Stop();
        double dp = 0;
        _dangerPulseTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(50) };
        _dangerPulseTimer.Tick += (_, _) =>
        {
            dp += 0.08;
            danger.Opacity = 0.5 + Math.Sin(dp) * 0.5;
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

        _discordGameTimer?.Stop();
        _discordGameTimer = null;
        _discord.SetGameResults(_currentTrackTitle, rank, _gameScore, acc, _maxCombo);

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

        var overlay = new Border
        {
            Tag = "game_results_overlay",
            Background = new SolidColorBrush(Color.FromArgb(220, 0x05, 0x05, 0x0f)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };

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

        int perfect = _hitNotes;
        int misses = _missCount;
        int maxCombo = _gameCombo;

        AddStat(0, 0, "СЧЁТ", _gameScore.ToString("N0"), Color.FromRgb(0xff, 0xff, 0xff));
        AddStat(1, 0, "ТОЧНОСТЬ", $"{acc}%", acc >= 90 ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0xf5, 0x9e, 0x0b));
        AddStat(2, 0, "КОМБО", $"{_gameCombo}x", Color.FromRgb(0x63, 0x66, 0xf1));
        AddStat(0, 1, "ВСЕГО НОТ", _totalNotes.ToString(), Color.FromRgb(0xaa, 0xaa, 0xaa));
        AddStat(1, 1, "ПОПАДАНИЙ", _hitNotes.ToString(), Color.FromRgb(0x22, 0xc5, 0x5e));
        AddStat(2, 1, "ПРОМАХОВ", misses.ToString(), Color.FromRgb(0xef, 0x44, 0x44));

        content.Children.Add(statsGrid);

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

            _isInGame = false;
            _discord.IsPriorityMode = false;

            if (_lastGameNotes != null && _lastGameTitle != null)
            {
                StartGame(_lastGameNotes, _lastGameMp3Path, _lastGameTitle, _lastGameBpm);
            }
            else
            {
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

            _isInGame = false;
            _discord.IsPriorityMode = false;

            StopGame();
            ShowGameView(GameMenuView);
        };

        btnPanel.Children.Add(retryBtn);
        btnPanel.Children.Add(menuBtn);
        content.Children.Add(btnPanel);

        var overlayGrid = new Grid();
        overlayGrid.Children.Add(auroraBg);
        overlayGrid.Children.Add(content);
        overlay.Child = overlayGrid;
        GamePlayView.Children.Add(overlay);

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

        _countdownTimer?.Stop();
        _countdownTimer = null;
        CountdownOverlay.Visibility = Visibility.Collapsed;

        _auroraGameTimer?.Stop();
        _auroraGameTimer = null;
        _lastComboAuraLevel = 0;

        _effectTimer?.Stop();
        _effectTimer = null;
        while (_effectQueue.TryDequeue(out _)) { }

        _starTimer?.Stop();
        _starTimer = null;

        _dangerPulseTimer?.Stop();
        _dangerPulseTimer = null;
        _halfwayTriggered = false;
        _dangerModeActive = false;
        _perfectStreak = 0;

        _discordGameTimer?.Stop();
        _discordGameTimer = null;
        _isInGame = false;
        _discord.IsPriorityMode = false;
        _discord.SetMainMenu();

        var vigs = GamePlayView.Children.OfType<System.Windows.Shapes.Rectangle>()
            .Where(r => r.Tag?.ToString() == "danger_vignette").ToList();
        foreach (var v in vigs) GamePlayView.Children.Remove(v);

        var auras = GamePlayView.Children.OfType<System.Windows.Shapes.Rectangle>()
            .Where(r => r.Tag?.ToString() == "combo_aura").ToList();
        foreach (var aura in auras)
        {
            var fadeOut = new DoubleAnimation(aura.Opacity, 0, TimeSpan.FromMilliseconds(600));
            fadeOut.Completed += (_, _) => GamePlayView.Children.Remove(aura);
            aura.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        var resultsOverlays = GamePlayView.Children.OfType<Border>()
            .Where(b => b.Tag?.ToString() == "game_results_overlay").ToList();
        foreach (var overlay in resultsOverlays)
        {
            GamePlayView.Children.Remove(overlay);
        }

        if (!_gameOverlayActive)
        {
            ServicesBtn.IsEnabled = true;
            FaqNavBtn.IsEnabled = true;
            DiagNavBtn.IsEnabled = true;
            SettingsBtn.IsEnabled = true;
            ModsNavBtn.IsEnabled = true;
        }

        GameHeaderTitle.Text = "Мини-игра";
        GameHUDPanel.Visibility = Visibility.Collapsed;
    }

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
        e.Handled = true;
        if ((s as Button)?.Tag is not NoteMap map) return;
        var dir = System.IO.Path.Combine(LevelsDir, map.Title ?? "level");
        var mp3 = System.IO.Path.Combine(dir, map.TrackFile ?? "track.mp3");
        var bpm = map.Bpm > 0 ? map.Bpm : REFERENCE_BPM;
        ShowGameView(GamePlayView);
        StartGame(map.Notes, File.Exists(mp3) ? mp3 : null, map.Title ?? "Custom Level", bpm);
    }

    private void ExportUserLevel_Click(object s, RoutedEventArgs e)
    {
        e.Handled = true;
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
                    JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

                if (isOsuMode)
                    File.WriteAllText(Path.Combine(tempDir, "source.osz.path"), oszPath);

            }

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

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

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
        e.Handled = true;
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

    private void ShowHostsWarningDialog(Action onClose)
    {
        var win = new NetFix.Views.HostsInfoWindow { Owner = this };
        win.ShowDialog();
        onClose();
    }

    private void _ShowHostsWarningDialog_UNUSED(Action onClose)
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

        var dialog = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2d)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 460,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            ClipToBounds = true
        };

        var dialogGrid = new Grid();

        var grad1 = new Border
        {
            CornerRadius = new CornerRadius(13),
            Opacity = 0.3,
            Background = new RadialGradientBrush
            {
                ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
                Center = new Point(0.5, 1.0),
                GradientOrigin = new Point(0.5, 1.0),
                RadiusX = 0.55,
                RadiusY = 0.45,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x50, 0x63, 0x66, 0xf1), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0x63, 0x66, 0xf1), 1.0)
                }
            }
        };
        dialogGrid.Children.Add(grad1);

        var grad2 = new Border
        {
            CornerRadius = new CornerRadius(13),
            Opacity = 0.25,
            Background = new RadialGradientBrush
            {
                ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
                Center = new Point(0.1, 0.1),
                GradientOrigin = new Point(0.1, 0.1),
                RadiusX = 0.35,
                RadiusY = 0.30,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x30, 0x8b, 0x5c, 0xf6), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0x8b, 0x5c, 0xf6), 1.0)
                }
            }
        };
        dialogGrid.Children.Add(grad2);

        var grad3 = new Border
        {
            CornerRadius = new CornerRadius(13),
            Opacity = 0.15,
            Background = new RadialGradientBrush
            {
                ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
                Center = new Point(0.9, 0.8),
                GradientOrigin = new Point(0.9, 0.8),
                RadiusX = 0.30,
                RadiusY = 0.25,
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x28, 0x4f, 0x46, 0xe5), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0x4f, 0x46, 0xe5), 1.0)
                }
            }
        };
        dialogGrid.Children.Add(grad3);

        try
        {
            var noiseBorder = new Border
            {
                CornerRadius = new CornerRadius(13),
                Opacity = 0.04,
                IsHitTestVisible = false,
                Background = new ImageBrush
                {
                    ImageSource = new BitmapImage(new Uri("pack://application:,,,/Assets/noise.png")),
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 512, 512),
                    Stretch = Stretch.None
                }
            };
            dialogGrid.Children.Add(noiseBorder);
        }
        catch { }

        var contentLayout = new Grid();
        contentLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerPanel = new Grid { Margin = new Thickness(28, 20, 28, 12) };
        var headerStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var iconBorder = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22)),
            Margin = new Thickness(0, 0, 12, 0)
        };
        var infoText = new TextBlock
        {
            Text = "i",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0)
        };
        iconBorder.Child = infoText;
        headerStack.Children.Add(iconBorder);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "СИСТЕМНЫЙ ФАЙЛ",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58)),
            FontWeight = FontWeights.Medium,
            FontFamily = new FontFamily("Segoe UI")
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "Раздел Hosts-файлов",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)),
            FontFamily = new FontFamily("Segoe UI")
        });
        headerStack.Children.Add(titleStack);
        headerPanel.Children.Add(headerStack);

        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2a)),
            Margin = new Thickness(0, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        headerPanel.Children.Add(separator);

        contentLayout.Children.Add(headerPanel);
        Grid.SetRow(headerPanel, 0);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(28, 16, 28, 0),
            MaxHeight = 280
        };

        var contentStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

        void AddHeading(string text)
        {
            contentStack.Children.Add(new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 14, 0, 8)
            });
        }

        void AddSeparator()
        {
            contentStack.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2a)),
                Margin = new Thickness(0, 14, 0, 14)
            });
        }

        TextBlock CreateParagraph()
        {
            var tb = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb)),
                Margin = new Thickness(0, 0, 0, 8)
            };
            contentStack.Children.Add(tb);
            return tb;
        }

        void AddSpan(TextBlock tb, string text, bool bold = false, Color? color = null)
        {
            var run = new Run(text);
            if (bold) run.FontWeight = FontWeights.Bold;
            if (color.HasValue)
            {
                run.Foreground = new SolidColorBrush(color.Value);
            }
            else
            {
                run.Foreground = bold ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xbb, 0xbb, 0xbb));
            }
            tb.Inlines.Add(run);
        }

        void AddCodeBlock(string code)
        {
            var codeBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2d)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 6, 0, 12),
                Cursor = Cursors.Hand
            };

            var codeGrid = new Grid();
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var codeText = new TextBlock
            {
                Text = code,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            codeGrid.Children.Add(codeText);
            Grid.SetColumn(codeText, 0);

            var hintText = new TextBlock
            {
                Text = "(нажмите для ввода в cmd)",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x69)),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            codeGrid.Children.Add(hintText);
            Grid.SetColumn(hintText, 1);

            codeBorder.Child = codeGrid;

            codeBorder.MouseLeftButtonDown += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(code);
                    var psi = new ProcessStartInfo("cmd.exe")
                    {
                        Arguments = $"/k \"{code}\"",
                        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    ShowNotification("Командная строка", "Команда выполнена в командной строке.", false);
                }
                catch (Exception ex)
                {
                    ShowNotification("Ошибка", "Не удалось открыть cmd: " + ex.Message, true);
                }
            };

            contentStack.Children.Add(codeBorder);
        }

        void AddLinkItem(string prefix, string linkText, string url, string suffix)
        {
            var tb = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 0, 0, 6)
            };

            AddSpan(tb, prefix);

            var hyper = new Hyperlink(new Run(linkText))
            {
                NavigateUri = new Uri(url),
                Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
                FontWeight = FontWeights.SemiBold
            };
            hyper.RequestNavigate += (sender, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                e.Handled = true;
            };
            tb.Inlines.Add(hyper);

            AddSpan(tb, suffix);
            contentStack.Children.Add(tb);
        }

        var intro = CreateParagraph();
        AddSpan(intro, "Этот раздел критически важен для ");
        AddSpan(intro, "разблокировки популярных нейросетей и сервисов", bold: true);
        AddSpan(intro, " (");
        AddSpan(intro, "ChatGPT, Gemini, Grok", bold: true);
        AddSpan(intro, " и многих других) напрямую, без потери скорости.");

        AddSeparator();

        AddHeading("💡 Как это работает?");
        var howItWorks = CreateParagraph();
        AddSpan(howItWorks, "Активация hosts-модов автоматически добавляет правила в системный файл Windows. Запросы к заблокированным доменам перенаправляются на альтернативные, рабочие IP-адреса (обратные прокси), минуя стандартные блокировки.");

        AddSeparator();

        AddHeading("⚠️ Почему моды могут не работать и как это исправить?");

        var bullet1 = CreateParagraph();
        AddSpan(bullet1, "• ");
        AddSpan(bullet1, "Права доступа", bold: true);
        AddSpan(bullet1, ": Приложение обязательно должно быть запущенно ");
        AddSpan(bullet1, "от имени Администратора", bold: true, color: Color.FromRgb(0xef, 0x44, 0x44));
        AddSpan(bullet1, ", иначе Windows просто не разрешит изменить системный файл.");

        var bullet2 = CreateParagraph();
        AddSpan(bullet2, "• ");
        AddSpan(bullet2, "Блокировка провайдером", bold: true);
        AddSpan(bullet2, ": Ваш интернет-провайдер может блокировать трафик по IP-адресу напрямую или использовать DPI. В таком случае hosts-мод не поможет - нужно комбинировать его с другими инструментами обхода.");

        var bullet3 = CreateParagraph();
        AddSpan(bullet3, "• ");
        AddSpan(bullet3, "Защита системы", bold: true);
        AddSpan(bullet3, ": Сторонние антивирусы или встроенный Windows Defender часто блокируют любые изменения в файле hosts, считая это действием вируса. Временно отключите защиту или добавьте приложение в исключения.");

        var bullet4 = CreateParagraph();
        AddSpan(bullet4, "• ");
        AddSpan(bullet4, "Кэш браузера (Важно)", bold: true);
        AddSpan(bullet4, ": После применения мода ");
        AddSpan(bullet4, "обязательно перезагрузите браузер", bold: true);
        AddSpan(bullet4, ". Ещё лучше - полностью очистить DNS-кэш в системе.");

        var bullet5 = CreateParagraph();
        AddSpan(bullet5, "• ");
        AddSpan(bullet5, "Динамические IP", bold: true);
        AddSpan(bullet5, ": Рабочие адреса платформ периодически меняются, из-за чего старые моды теряют актуальность.");

        AddSeparator();

        AddHeading("🚀 Полезные советы для стабильной работы");

        var tip1 = CreateParagraph();
        AddSpan(tip1, "• ");
        AddSpan(tip1, "Очистка DNS-кэша", bold: true);
        AddSpan(tip1, ": Если после применения мода ничего не изменилось, откройте командную строку (cmd) от админа и выполните:");

        AddCodeBlock("ipconfig /flushdns");

        var tip2 = CreateParagraph();
        AddSpan(tip2, "• ");
        AddSpan(tip2, "Проверка режима Инкогнито", bold: true);
        AddSpan(tip2, ": Проверяйте работу сервисов в режиме инкогнито, чтобы исключить влияние старых куки (cookies) и кэша браузера.");

        var tip3 = CreateParagraph();
        AddSpan(tip3, "• ");
        AddSpan(tip3, "Следите за обновлениями", bold: true);
        AddSpan(tip3, ": Свежие и актуальные моды вы всегда можете найти в комментариях моего Telegram-канала ");
        AddSpan(tip3, "NetFix", bold: true);
        AddSpan(tip3, ", либо собрать собственный рабочий вариант.");

        AddSeparator();

        AddHeading("🌐 Полезные ссылки и готовые DNS");
        var linksIntro = CreateParagraph();
        AddSpan(linksIntro, "Если вам нужны полностью готовые решения, вы можете взять рабочие адреса и настроить всё здесь:");

        AddLinkItem("• ", "Инфо и база хостов (dns.malw.link)", "https://info.dns.malw.link/hosts", " - готовые списки и правила для hosts.");
        AddLinkItem("• ", "Зеркало Geohide DNS", "https://dns.geohide.ru:8443/", " - рабочие адреса для настройки обхода.");

        scrollViewer.Content = contentStack;
        contentLayout.Children.Add(scrollViewer);
        Grid.SetRow(scrollViewer, 1);

        var footerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1c)),
            CornerRadius = new CornerRadius(0, 0, 14, 14),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x38)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(28, 12, 28, 12)
        };

        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var okBtn = new Button
        {
            Content = "Понятно",
            Style = (Style)FindResource("AccentBtn"),
            MinWidth = 130,
            MinHeight = 36,
            Cursor = Cursors.Hand
        };
        okBtn.Click += (_, _) =>
        {
            ContentGrid.Children.Remove(overlay);
            onClose();
        };

        footerGrid.Children.Add(okBtn);
        Grid.SetColumn(okBtn, 1);

        footerBorder.Child = footerGrid;
        contentLayout.Children.Add(footerBorder);
        Grid.SetRow(footerBorder, 2);

        dialogGrid.Children.Add(contentLayout);
        dialog.Child = dialogGrid;
        overlay.Child = dialog;
        ContentGrid.Children.Add(overlay);
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
            MaxWidth = 400,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        var stack = new StackPanel();

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

        var editorStartTime = DateTime.Now;
        var trackTitle = EditorTrackTitle.Text.Trim();
        _discordEditorTimer?.Stop();
        _discordEditorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _discordEditorTimer.Tick += (_, _) => {
            _discord.SetLevelEditor(trackTitle, _recordedNotes.Count, editorStartTime);
        };
        _discordEditorTimer.Start();

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

        EditorNoteCount.Text = $"{_recordedNotes.Count} нот записано";

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
            keyStr = keyStr[1..];

        if (keyStr.Equals(_settings.KeyLane0, StringComparison.OrdinalIgnoreCase)) return 0;
        if (keyStr.Equals(_settings.KeyLane1, StringComparison.OrdinalIgnoreCase)) return 1;
        if (keyStr.Equals(_settings.KeyLane2, StringComparison.OrdinalIgnoreCase)) return 2;
        if (keyStr.Equals(_settings.KeyLane3, StringComparison.OrdinalIgnoreCase)) return 3;

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
            JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

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

        var cache = ZapretConfigService.LoadCache();
        if (cache != null && cache.HasAnyConfigs)
        {
            RenderWizardConfigSelection(cache);
        }
        else
        {
            RenderWizardNoConfigs();
        }
    }

    private void RenderWizardConfigSelection(ZapretConfigCache cache)
    {
        WizardContent.Children.Clear();
        var title = FindChild<TextBlock>(WizardLayer);
        if (title != null) title.Text = "Мастер настройки Zapret";

        AddWizText("Выбери конфиг для запуска и нажми на кнопку «Применить»!");

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

            border.MouseLeftButtonDown += (s, e) =>
            {
                cache.CurrentConfig = config.Name;
                ZapretConfigService.SaveCache(cache);
                RenderWizardConfigSelection(cache);
            };

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

        AddWizBtn("Применить", "#22c55e", async () =>
        {
            if (!string.IsNullOrEmpty(cache.CurrentConfig))
            {
                WizardApplyProgress.Visibility = Visibility.Visible;

                bool success = await ZapretConfigService.ApplyConfigAsync(_settings.ZapretPath, cache.CurrentConfig);

                WizardApplyProgress.Visibility = Visibility.Collapsed;

                if (success)
                {
                    CloseWizard();
                    await Task.Delay(1500);
                    UpdateActiveApps();

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
                    ZapretConfigService.LoadCache();
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


    private Grid? _onboardRootGrid;
    private TextBlock? _onboardStepText;
    private StackPanel? _onboardSignalPanel;
    private ContentControl? _onboardContentHost;

    private void ShowOnboarding()
    {
        OnboardLayer.Visibility = Visibility.Visible;
        Opacity = 1;
        _onboardRootGrid = null;
        ShowOnboardScreen(0);
    }

    private void ShowOnboardScreen(int n)
    {
        var (curStep, totalSteps) = GetOnboardStepInfo(n);

        if (_onboardRootGrid == null || _onboardContentHost == null)
        {
            _onboardRootGrid = new Grid { Background = Brushes.Transparent };

            _onboardContentHost = new ContentControl
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch
            };
            _onboardRootGrid.Children.Add(_onboardContentHost);

            var header = CreateOnboardTopHeader();
            _onboardRootGrid.Children.Add(header);
            AnimateOnboardHeaderEntrance(header);

            OnboardContent.Content = _onboardRootGrid;
        }

        UpdateOnboardHeader(curStep, totalSteps);

        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            MaxWidth            = 520,
            Margin              = new Thickness(32)
        };

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

        _onboardContentHost.Content = stack;
        if (n != 0)
        {
            AnimateOnboardSlideCascade(stack);
        }
    }

    private static void AnimateOnboardSlideCascade(StackPanel stack)
    {
        var easeQuintic = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var easeCubic   = new CubicEase   { EasingMode = EasingMode.EaseOut };

        int contentIndex = 0;
        int buttonIndex = 0;

        for (int i = 0; i < stack.Children.Count; i++)
        {
            if (stack.Children[i] is FrameworkElement el)
            {
                TextOptions.SetTextFormattingMode(el, TextFormattingMode.Ideal);
                TextOptions.SetTextRenderingMode(el, TextRenderingMode.Grayscale);

                if (el is Viewbox || el.Tag as string == "custom_icon")
                {
                    el.Opacity = 0;
                    var iconFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700))
                    {
                        EasingFunction = easeCubic
                    };
                    el.BeginAnimation(OpacityProperty, iconFade);
                    contentIndex++;
                    continue;
                }

                el.Opacity = 0;

                bool isButton = el is Button;
                int delayMs;
                double fromY;
                int fadeDurationMs;
                int moveDurationMs;

                if (isButton)
                {
                    delayMs = 900 + (buttonIndex * 100);
                    fromY = 20;
                    fadeDurationMs = 1000;
                    moveDurationMs = 1100;
                    buttonIndex++;
                }
                else
                {
                    delayMs = contentIndex * 120;
                    fromY = 24;
                    fadeDurationMs = 1300;
                    moveDurationMs = 1400;
                    contentIndex++;
                }

                var tg = new TransformGroup();
                var trans = new TranslateTransform(0, fromY);
                var scale = new ScaleTransform(1.0, 1.0);
                tg.Children.Add(trans);
                tg.Children.Add(scale);
                el.RenderTransformOrigin = new Point(0.5, 0.5);
                el.RenderTransform = tg;

                var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeDurationMs))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                    EasingFunction = easeCubic
                };

                var moveAnim = new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(moveDurationMs))
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                    EasingFunction = easeQuintic
                };

                el.BeginAnimation(OpacityProperty, fadeAnim);
                trans.BeginAnimation(TranslateTransform.YProperty, moveAnim);
            }
        }
    }

    private static void AnimateOnboardHeaderEntrance(FrameworkElement header)
    {
        header.Opacity = 0;
        var headerTrans = new TranslateTransform(0, -18);
        header.RenderTransform = headerTrans;

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1000))
        {
            BeginTime = TimeSpan.FromMilliseconds(200),
            EasingFunction = ease
        };
        var moveAnim = new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(1100))
        {
            BeginTime = TimeSpan.FromMilliseconds(200),
            EasingFunction = ease
        };
        header.BeginAnimation(OpacityProperty, fadeAnim);
        headerTrans.BeginAnimation(TranslateTransform.YProperty, moveAnim);
    }

    private (int currentStep, int totalSteps) GetOnboardStepInfo(int n)
    {
        if (!_onboardIsManual)
        {
            return n switch
            {
                0 => (1, 6),
                1 => (2, 6),
                2 => (3, 6),
                3 => (4, 6),
                16 => (5, 6),
                15 => (6, 6),
                _ => (1, 6)
            };
        }
        else
        {
            return n switch
            {
                0 => (1, 8),
                1 => (2, 8),
                2 => (3, 8),
                3 => (4, 8),
                17 => (5, 8),
                4 or 5 or 6 or 7 or 8 or 9 => (6, 8),
                10 or 11 or 12 or 13 => (7, 8),
                15 => (8, 8),
                _ => (1, 8)
            };
        }
    }

    private FrameworkElement CreateOnboardTopHeader()
    {
        var badge = new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, 24, 0, 0),
            Background          = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1c)),
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x2e)),
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(14),
            Padding             = new Thickness(12, 5, 12, 5)
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        _onboardStepText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xaa)),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_onboardStepText);

        _onboardSignalPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 14,
            Margin = new Thickness(10, 0, 0, 0)
        };
        row.Children.Add(_onboardSignalPanel);

        badge.Child = row;
        return badge;
    }

    private void UpdateOnboardHeader(int currentStep, int totalSteps)
    {
        if (_onboardStepText == null || _onboardSignalPanel == null) return;

        bool isComplete = currentStep >= totalSteps;
        _onboardStepText.Text = $"Шаг {currentStep} из {totalSteps}";
        _onboardStepText.Foreground = isComplete
            ? new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
            : new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xaa));

        if (_onboardSignalPanel.Children.Count != totalSteps)
        {
            _onboardSignalPanel.Children.Clear();
            double minH = 4.5;
            double maxH = 13.5;
            for (int i = 1; i <= totalSteps; i++)
            {
                double h = totalSteps > 1
                    ? minH + (maxH - minH) * (i - 1) / (totalSteps - 1)
                    : maxH;

                var bar = new Border
                {
                    Width = 3.5,
                    Height = Math.Round(h, 1),
                    CornerRadius = new CornerRadius(1.75),
                    Margin = new Thickness(0, 0, i == totalSteps ? 0 : 2.5, 0),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                _onboardSignalPanel.Children.Add(bar);
            }
        }

        var activeBrush = isComplete
            ? new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
            : new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
        var inactiveBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x2e));

        for (int i = 0; i < _onboardSignalPanel.Children.Count; i++)
        {
            if (_onboardSignalPanel.Children[i] is Border bar)
            {
                bar.Background = (i + 1 <= currentStep) ? activeBrush : inactiveBrush;
            }
        }
    }

    private void BuildOnboard0(StackPanel p)
    {
        _onboardIsManual = false;

        var titleBlock = new TextBlock
        {
            Text = "Привет!",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 44,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 32),
            Opacity = 0
        };

        TextOptions.SetTextFormattingMode(titleBlock, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(titleBlock, TextRenderingMode.Grayscale);

        var titleTranslate = new TranslateTransform(0, 24);
        titleBlock.RenderTransform = titleTranslate;
        p.Children.Add(titleBlock);

        var bgBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
        var hoverBrush = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Min(255, 0x3b * 1.07 + 8),
            (byte)Math.Min(255, 0x82 * 1.07 + 8),
            (byte)Math.Min(255, 0xf6 * 1.07 + 8)));

        var btn = new Button
        {
            Content             = "Начать",
            Background          = bgBrush,
            Foreground          = Brushes.White,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 16,
            FontWeight          = FontWeights.SemiBold,
            Height              = 46,
            MinWidth            = 200,
            Padding             = new Thickness(24, 0, 24, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Cursor              = Cursors.Hand,
            BorderThickness     = new Thickness(0),
            Margin              = new Thickness(0, 0, 0, 0),
            Opacity             = 0,
            Template            = CreateSimpleBtnTemplate()
        };

        var tg = new TransformGroup();
        var btnTranslate = new TranslateTransform(0, 20);
        var btnScale = new ScaleTransform(1.0, 1.0);
        tg.Children.Add(btnTranslate);
        tg.Children.Add(btnScale);
        btn.RenderTransformOrigin = new Point(0.5, 0.5);
        btn.RenderTransform = tg;
        AttachModernButtonHoverEffect(btn, bgBrush, hoverBrush, Color.FromRgb(0x3b, 0x82, 0xf6));
        btn.Click += (_, _) => ShowOnboardScreen(1);
        p.Children.Add(btn);

        var easeQuintic = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var easeCubic   = new CubicEase   { EasingMode = EasingMode.EaseOut };

        var titleFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1300))
        {
            EasingFunction = easeCubic
        };
        var titleMove = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(1400))
        {
            EasingFunction = easeQuintic
        };

        titleBlock.BeginAnimation(OpacityProperty, titleFade);
        titleTranslate.BeginAnimation(TranslateTransform.YProperty, titleMove);

        var btnFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1000))
        {
            BeginTime = TimeSpan.FromMilliseconds(900),
            EasingFunction = easeCubic
        };
        var btnMove = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(1100))
        {
            BeginTime = TimeSpan.FromMilliseconds(900),
            EasingFunction = easeQuintic
        };

        btn.BeginAnimation(OpacityProperty, btnFade);
        btnTranslate.BeginAnimation(TranslateTransform.YProperty, btnMove);
    }

    private void BuildOnboard1(StackPanel p)
    {
        var viewBox = new Viewbox
        {
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var canvas = new Canvas { Width = 24, Height = 24 };

        var circlePath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 22C17.5 22 22 17.5 22 12C22 6.5 17.5 2 12 2C6.5 2 2 6.5 2 12C2 17.5 6.5 22 12 22Z"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        var questionGroup = new GeometryGroup();
        questionGroup.Children.Add(Geometry.Parse("M12 8V13"));
        questionGroup.Children.Add(Geometry.Parse("M11.9945 16H12.0035"));

        var questionPath = new System.Windows.Shapes.Path
        {
            Data = questionGroup,
            Stroke = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        canvas.Children.Add(circlePath);
        canvas.Children.Add(questionPath);
        viewBox.Child = canvas;

        var transGroup = new TransformGroup();
        var scaleTrans = new ScaleTransform(1.0, 1.0);
        var translateTrans = new TranslateTransform(0, 0);
        transGroup.Children.Add(scaleTrans);
        transGroup.Children.Add(translateTrans);
        viewBox.RenderTransform = transGroup;

        var popAnim = new DoubleAnimation(0.9, 1.0, TimeSpan.FromMilliseconds(500))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scaleTrans.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
        scaleTrans.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);

        var floatAnim = new DoubleAnimation(-1.5, 1.5, TimeSpan.FromMilliseconds(2400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        translateTrans.BeginAnimation(TranslateTransform.YProperty, floatAnim);

        p.Children.Add(viewBox);

        AddOnboardTitle(p, "Зачем нужен NetFix");
        AddOnboardSub(p, "NetFix автоматически восстанавливает доступ к YouTube, Discord, Telegram, а также к заблокированным сайтам и нейросетям (ChatGPT, Gemini и другим) без сложных настроек и сторонних VPN.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(2));
    }

    private void BuildOnboard2(StackPanel p)
    {
        var viewBox = new Viewbox
        {
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var canvas = new Canvas { Width = 24, Height = 24 };

        var shieldPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M20.91 11.12C20.91 16.01 17.36 20.59 12.51 21.93C12.18 22.02 11.82 22.02 11.49 21.93C6.63996 20.59 3.08997 16.01 3.08997 11.12V6.72997C3.08997 5.90997 3.70998 4.97998 4.47998 4.66998L10.05 2.39001C11.3 1.88001 12.71 1.88001 13.96 2.39001L19.53 4.66998C20.29 4.97998 20.92 5.90997 20.92 6.72997L20.91 11.12Z"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        var lockGroup = new GeometryGroup();
        lockGroup.Children.Add(Geometry.Parse("M12 12.5C13.1046 12.5 14 11.6046 14 10.5C14 9.39543 13.1046 8.5 12 8.5C10.8954 8.5 10 9.39543 10 10.5C10 11.6046 10.8954 12.5 12 12.5Z"));
        lockGroup.Children.Add(Geometry.Parse("M12 12.5V15.5"));

        var lockPath = new System.Windows.Shapes.Path
        {
            Data = lockGroup,
            Stroke = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        canvas.Children.Add(shieldPath);
        canvas.Children.Add(lockPath);
        viewBox.Child = canvas;

        var transGroup = new TransformGroup();
        var scaleTrans = new ScaleTransform(1.0, 1.0);
        var translateTrans = new TranslateTransform(0, 0);
        transGroup.Children.Add(scaleTrans);
        transGroup.Children.Add(translateTrans);
        viewBox.RenderTransform = transGroup;

        var popAnim = new DoubleAnimation(0.9, 1.0, TimeSpan.FromMilliseconds(500))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scaleTrans.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
        scaleTrans.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);

        var floatAnim = new DoubleAnimation(-1.5, 1.5, TimeSpan.FromMilliseconds(2400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        translateTrans.BeginAnimation(TranslateTransform.YProperty, floatAnim);

        p.Children.Add(viewBox);

        AddOnboardTitle(p, "Безопасность и приватность");

        var subText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        };

        subText.Inlines.Add(new System.Windows.Documents.Run("Приложение не собирает данные, не перехватывает личный трафик и не отправляет аналитику. Исходный код полностью открыт на "));

        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("GitHub"))
        {
            NavigateUri = new Uri("https://github.com/rupleide/NetFix"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            TextDecorations = null,
            Cursor = Cursors.Hand
        };
        link.RequestNavigate += Hyperlink_RequestNavigate;

        subText.Inlines.Add(link);
        subText.Inlines.Add(new System.Windows.Documents.Run(", вы можете проверить каждую строчку."));
        p.Children.Add(subText);

        AddOnboardBtn(p, "Понятно", "#3b82f6", () => ShowOnboardScreen(3));
    }

    private void BuildOnboard3(StackPanel p)
    {
        var viewBox = new Viewbox
        {
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };
        var canvas = new Canvas { Width = 24, Height = 24 };

        var trayPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M17 17H17.01M17.4 14H18C18.9319 14 19.3978 14 19.7654 14.1522C20.2554 14.3552 20.6448 14.7446 20.8478 15.2346C21 15.6022 21 16.0681 21 17C21 17.9319 21 18.3978 20.8478 18.7654C20.6448 19.2554 20.2554 19.6448 19.7654 19.8478C19.3978 20 18.9319 20 18 20H6C5.06812 20 4.60218 20 4.23463 19.8478C3.74458 19.6448 3.35523 19.2554 3.15224 18.7654C3 18.3978 3 17.9319 3 17C3 16.0681 3 15.6022 3.15224 15.2346C3.35523 14.7446 3.74458 14.3552 4.23463 14.1522C4.60218 14 5.06812 14 6 14H6.6"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        var arrowPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 15V4M12 15L9 12M12 15L15 12"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        canvas.Children.Add(trayPath);
        canvas.Children.Add(arrowPath);
        viewBox.Child = canvas;

        p.Children.Add(viewBox);

        AddOnboardTitle(p, "Способ установки");
        AddOnboardSub(p, "Программа может скачать и настроить все зависимости сама или дать вам указать файлы вручную.");

        AddOnboardBtn(p, "Автоматически (рекомендуется)", "#22c55e", () =>
        {
            _onboardForceReserve = false;
            _onboardIsManual = false;
            ShowOnboardScreen(16);
        }, stretch: true);

        AddOnboardBtn(p, "Автоматически (зеркало)", "#3b82f6", () =>
        {
            _onboardForceReserve = true;
            _onboardIsManual = false;
            ShowOnboardScreen(16);
        }, stretch: true);

        AddOnboardBtn(p, "Вручную", "#2e2e2e", () =>
        {
            _onboardIsManual = true;
            ShowOnboardScreen(17);
        }, foreground: "#888888", stretch: true);
    }

    private void BuildOnboardZapretChoice(StackPanel p)
    {
        AddOnboardTitle(p, "Компонент Zapret");
        AddOnboardSub(p, "У вас уже скачан архив zapret-discord-youtube?");
        AddOnboardBtn(p, "Указать папку с файлами", "#22c55e", () =>
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
        AddOnboardBtn(p, "Скачать архив", "#2e2e2e", () => ShowOnboardScreen(5), foreground: "#888888");
    }

    private void BuildOnboardLetsDoIt(StackPanel p)
    {
        AddOnboardTitle(p, "Скачивание Zapret");
        AddOnboardSub(p, "Откройте страницу релиза на GitHub и скачайте свежий архив из блока Assets.");
        AddOnboardBtn(p, "Открыть страницу загрузки", "#3b82f6", () =>
        {
            OpenUrl("https://github.com/Flowseal/zapret-discord-youtube/releases/latest");
            ShowOnboardScreen(6);
        });
    }

    private void BuildOnboardDownloadArchive(StackPanel p)
    {
        AddOnboardTitle(p, "Загрузка архива");
        AddOnboardSub(p, "Скачайте файл .zip или .rar в блоке Assets внизу страницы.");
        AddOnboardBtn(p, "Архив скачан", "#3b82f6", () => ShowOnboardScreen(7));
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
        }

        AddOnboardTitle(p, "Распаковка файлов");
        AddOnboardSub(p, "Мы открыли папку C:\\Zapret. Распакуйте всё содержимое скачанного архива прямо в неё.");
        AddOnboardBtn(p, "Файлы на месте", "#3b82f6", () => ShowOnboardScreen(8));
    }

    private void BuildOnboardZapretSelectBat(StackPanel p)
    {
        AddOnboardTitle(p, "Привязка компонента");
        AddOnboardSub(p, "Выберите файл service.bat внутри папки C:\\Zapret.");
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
        var likeIcon = new System.Windows.Shapes.Path
        {
            Tag = "custom_icon",
            Data = Geometry.Parse("M8,11.47A18.74,18.74,0,0,0,10.69,8.9a18.74,18.74,0,0,0,1.76-2.42A6.42,6.42,0,0,0,13,5.41l1.74-4.57a4.45,4.45,0,0,1,2.83,2A4,4,0,0,1,18,4.77a2.67,2.67,0,0,1-.09.55L16.72,9.05h5.22a2,2,0,0,1,2,1.85,19.32,19.32,0,0,1-.32,5.44,33.83,33.83,0,0,1-1.23,4.34,3.78,3.78,0,0,1-3.58,2.49,25.54,25.54,0,0,1-6.28-.66A45.85,45.85,0,0,1,8,21.26V11.47Z M5,9H1a1,1,0,0,0-1,1V22a1,1,0,0,0,1,1H5a1,1,0,0,0,1-1V10A1,1,0,0,0,5,9ZM3,21a1,1,0,1,1,1-1A1,1,0,0,1,3,21Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        var tg = new TransformGroup();
        var sc = new ScaleTransform(1, 1);
        var rot = new RotateTransform(0);
        var tt = new TranslateTransform(0, 0);
        tg.Children.Add(sc);
        tg.Children.Add(rot);
        tg.Children.Add(tt);
        likeIcon.RenderTransform = tg;

        var popAnim = new DoubleAnimation(0.1, 1.0, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }
        };
        var rotIntro = new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
        };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);
        rot.BeginAnimation(RotateTransform.AngleProperty, rotIntro);

        var wobbleAnim = new DoubleAnimation(-3, 3, TimeSpan.FromMilliseconds(1500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        rot.BeginAnimation(RotateTransform.AngleProperty, wobbleAnim);

        var floatAnim = new DoubleAnimation(-2.5, 2.5, TimeSpan.FromMilliseconds(1500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        tt.BeginAnimation(TranslateTransform.YProperty, floatAnim);

        p.Children.Add(likeIcon);

        AddOnboardTitle(p, "Zapret подключен");
        AddOnboardSub(p, "Файлы проверены, переходим ко второму компоненту.");
        AddOnboardBtn(p, "Далее", "#3b82f6", () => ShowOnboardScreen(10));
    }

    private void BuildOnboardTgWsChoice(StackPanel p)
    {
        AddOnboardTitle(p, "Компонент TgWsProxy");
        AddOnboardSub(p, "У вас уже скачан файл TgWsProxy.exe?");
        AddOnboardBtn(p, "Выбрать файл", "#22c55e", () =>
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
        AddOnboardBtn(p, "Скачать", "#2e2e2e", () => ShowOnboardScreen(11), foreground: "#888888");
    }

    private void BuildOnboardTgWsDownload(StackPanel p)
    {
        AddOnboardTitle(p, "Скачивание TgWsProxy");
        AddOnboardSub(p, "В блоке Assets на GitHub скачайте исполняемый файл TgWsProxy.exe (не архив).");
        AddOnboardBtn(p, "Скачать с GitHub", "#3b82f6", () =>
        {
            OpenUrl("https://github.com/Flowseal/tg-ws-proxy/releases/latest");
            ShowOnboardScreen(12);
        });
    }

    private void BuildOnboardTgWsMove(StackPanel p)
    {
        try { Process.Start("explorer.exe", @"C:\Zapret"); } catch {}

        AddOnboardTitle(p, "Размещение файла");
        AddOnboardSub(p, "Переместите скачанный TgWsProxy.exe в открытую папку C:\\Zapret.");
        AddOnboardBtn(p, "Файл перемещен", "#3b82f6", () => ShowOnboardScreen(13));
    }

    private void BuildOnboardTgWsSelectExe(StackPanel p)
    {
        AddOnboardTitle(p, "Привязка TgWsProxy");
        AddOnboardSub(p, "Укажите путь к TgWsProxy.exe в папке C:\\Zapret.");
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
        var likeIcon = new System.Windows.Shapes.Path
        {
            Tag = "custom_icon",
            Data = Geometry.Parse("M8,11.47A18.74,18.74,0,0,0,10.69,8.9a18.74,18.74,0,0,0,1.76-2.42A6.42,6.42,0,0,0,13,5.41l1.74-4.57a4.45,4.45,0,0,1,2.83,2A4,4,0,0,1,18,4.77a2.67,2.67,0,0,1-.09.55L16.72,9.05h5.22a2,2,0,0,1,2,1.85,19.32,19.32,0,0,1-.32,5.44,33.83,33.83,0,0,1-1.23,4.34,3.78,3.78,0,0,1-3.58,2.49,25.54,25.54,0,0,1-6.28-.66A45.85,45.85,0,0,1,8,21.26V11.47Z M5,9H1a1,1,0,0,0-1,1V22a1,1,0,0,0,1,1H5a1,1,0,0,0,1-1V10A1,1,0,0,0,5,9ZM3,21a1,1,0,1,1,1-1A1,1,0,0,1,3,21Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        var tg = new TransformGroup();
        var sc = new ScaleTransform(1, 1);
        var rot = new RotateTransform(0);
        var tt = new TranslateTransform(0, 0);
        tg.Children.Add(sc);
        tg.Children.Add(rot);
        tg.Children.Add(tt);
        likeIcon.RenderTransform = tg;

        var popAnim = new DoubleAnimation(0.1, 1.0, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }
        };
        var rotIntro = new DoubleAnimation(-18, 0, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
        };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);
        rot.BeginAnimation(RotateTransform.AngleProperty, rotIntro);

        var wobbleAnim = new DoubleAnimation(-3, 3, TimeSpan.FromMilliseconds(1500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        rot.BeginAnimation(RotateTransform.AngleProperty, wobbleAnim);

        var floatAnim = new DoubleAnimation(-2.5, 2.5, TimeSpan.FromMilliseconds(1500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        tt.BeginAnimation(TranslateTransform.YProperty, floatAnim);

        p.Children.Add(likeIcon);

        AddOnboardTitle(p, "Всё готово к работе");

        var subText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        if (_onboardIsManual)
        {
            subText.Inlines.Add(new System.Windows.Documents.Run("Все пути сохранены. Конфигурацию можно скорректировать в любое время в меню настроек "));
        }
        else
        {
            subText.Inlines.Add(new System.Windows.Documents.Run("Компоненты настроены. Изменить параметры или проверить статус всегда можно через настройки "));
        }

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

        var footnote = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        };
        footnote.Inlines.Add(new System.Windows.Documents.Run("Мобильная версия NetFix Mobile для Android уже доступна на "));
        var mobileLink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("GitHub"))
        {
            NavigateUri = new Uri("https://github.com/rupleide/NetFixMobile"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            TextDecorations = null,
            Cursor = Cursors.Hand
        };
        mobileLink.RequestNavigate += Hyperlink_RequestNavigate;
        footnote.Inlines.Add(mobileLink);
        p.Children.Add(footnote);

        AddOnboardBtn(p, "Открыть NetFix", "#22c55e", () =>
        {
            SettingsService.MarkOnboarded();
            OnboardLayer.Visibility = Visibility.Collapsed;
            OnboardLayer.Opacity = 1;
            OnboardContent.Content = null;
            _onboardRootGrid = null;
            _onboardContentHost = null;
            _isEntranceAnimating = false;
            PlayEpicMainEntranceAnimation(0);
        });
    }

    private void PlayEpicOnboardFinishAnimation()
    {
        var easeCubicIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var easeCubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (_onboardRootGrid != null)
        {
            var contentTrans = new TranslateTransform(0, 0);
            _onboardRootGrid.RenderTransform = contentTrans;

            var fadeOutRoot = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = easeCubicIn
            };
            var moveOutRoot = new DoubleAnimation(0, 16, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = easeCubicIn
            };

            _onboardRootGrid.BeginAnimation(OpacityProperty, fadeOutRoot);
            contentTrans.BeginAnimation(TranslateTransform.YProperty, moveOutRoot);
        }

        var fadeOutLayer = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = TimeSpan.FromMilliseconds(130),
            EasingFunction = easeCubicOut
        };

        fadeOutLayer.Completed += (_, _) =>
        {
            OnboardLayer.Visibility = Visibility.Collapsed;
            OnboardLayer.Opacity = 1;
            OnboardLayer.RenderTransform = null;
            OnboardContent.Content = null;
            _onboardRootGrid = null;
            _onboardContentHost = null;

            PlayEpicMainEntranceAnimation(20);
        };

        OnboardLayer.BeginAnimation(OpacityProperty, fadeOutLayer);
    }

    private void PrepareMainEntranceState()
    {
        if (AuroraLayer != null)
        {
            AuroraLayer.BeginAnimation(OpacityProperty, null);
            AuroraLayer.Opacity = 0;
        }
        if (MainPageContainer != null)
        {
            MainPageContainer.BeginAnimation(OpacityProperty, null);
            MainPageContainer.Opacity = 1;
            MainPageContainer.RenderTransform = null;
        }
        if (AppHeaderBar != null)
        {
            AppHeaderBar.BeginAnimation(OpacityProperty, null);
            AppHeaderBar.Opacity = 1;
            AppHeaderBar.RenderTransform = null;
        }
        if (AppBottomBar != null)
        {
            AppBottomBar.BeginAnimation(OpacityProperty, null);
            AppBottomBar.Opacity = 1;
            AppBottomBar.RenderTransform = null;
        }
        if (AppBottomContent != null)
        {
            AppBottomContent.BeginAnimation(OpacityProperty, null);
            AppBottomContent.Opacity = 0;
            AppBottomContent.RenderTransform = new TranslateTransform(0, 12);
        }
        if (AppLogoTitle != null)
        {
            AppLogoTitle.BeginAnimation(OpacityProperty, null);
            AppLogoTitle.Opacity = 0;
            AppLogoTitle.RenderTransform = new TranslateTransform(-10, 0);
        }
        if (AppAuthorTitle != null)
        {
            AppAuthorTitle.BeginAnimation(OpacityProperty, null);
            AppAuthorTitle.Opacity = 0;
            AppAuthorTitle.RenderTransform = new TranslateTransform(-8, 0);
        }
        if (MainTitleBlock != null)
        {
            MainTitleBlock.BeginAnimation(OpacityProperty, null);
            MainTitleBlock.Opacity = 0;
            MainTitleBlock.RenderTransform = new TranslateTransform(0, 20);
        }
        if (MainCoreBlock != null)
        {
            MainCoreBlock.BeginAnimation(OpacityProperty, null);
            MainCoreBlock.Opacity = 0;
            MainCoreBlock.RenderTransform = new TranslateTransform(0, 22);
        }
        if (MainLogBlock != null)
        {
            MainLogBlock.BeginAnimation(OpacityProperty, null);
            MainLogBlock.Opacity = 1.0;
            MainLogBlock.Clip = null;
            MainLogBlock.RenderTransform = null;
        }
        if (SetupProgContainer != null)
        {
            SetupProgContainer.BeginAnimation(OpacityProperty, null);
            SetupProgContainer.Opacity = 0;
            SetupProgContainer.RenderTransform = new TranslateTransform(0, 18);
        }
        if (LogHeaderContainer != null)
        {
            LogHeaderContainer.BeginAnimation(OpacityProperty, null);
            LogHeaderContainer.Opacity = 0;
            LogHeaderContainer.RenderTransform = new TranslateTransform(0, 14);
        }
        if (LogBoxClipGeom != null)
        {
            LogBoxClipGeom.BeginAnimation(RectangleGeometry.RectProperty, null);
            LogBoxClipGeom.Rect = new Rect(0, 0, 1200, 0);
        }
        if (LogBoxWrapper != null)
        {
            LogBoxWrapper.BeginAnimation(OpacityProperty, null);
            LogBoxWrapper.Opacity = 1.0;
            LogBoxWrapper.RenderTransform = new TranslateTransform(0, 14);
        }

        UIElement?[] navButtons = [ServicesBtn, ModsNavBtn, FaqNavBtn, DiagNavBtn, GameNavBtn, SettingsBtn, MinBtn, CloseBtn];
        foreach (var btn in navButtons)
        {
            if (btn == null) continue;
            btn.BeginAnimation(OpacityProperty, null);
            btn.Opacity = 0;
            btn.RenderTransform = new TranslateTransform(14, 0);
        }

        UIElement?[] statusBeads = [NetDot, NetLbl, VpnStatusPanel, ZapretDot, ZapretLbl, TgWsDot, TgWsLbl];
        foreach (var bead in statusBeads)
        {
            if (bead == null) continue;
            bead.BeginAnimation(OpacityProperty, null);
            bead.Opacity = 0;
            bead.RenderTransform = new TranslateTransform(-8, 0);
        }

        if (IdleRingOuter != null)
        {
            IdleRingOuter.BeginAnimation(OpacityProperty, null);
            IdleRingOuter.Opacity = 0;
            IdleRingOuter.RenderTransform = new ScaleTransform(0.92, 0.92);
        }
        if (IdleRingInner != null)
        {
            IdleRingInner.BeginAnimation(OpacityProperty, null);
            IdleRingInner.Opacity = 0;
            IdleRingInner.RenderTransform = new ScaleTransform(0.94, 0.94);
        }

        UIElement?[] speedElements = [DownloadLbl, UploadLbl, PingLbl, RescanBtn];
        foreach (var elem in speedElements)
        {
            if (elem == null) continue;
            elem.BeginAnimation(OpacityProperty, null);
            elem.Opacity = 0;
            elem.RenderTransform = new TranslateTransform(12, 0);
        }
    }

    private void PlayEpicMainEntranceAnimation(int baseDelayMs = 0)
    {
        PrepareMainEntranceState();
        _isEntranceAnimating = true;

        var easeQuint = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var easeCubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (EntranceCurtain != null && EntranceCurtain.Visibility == Visibility.Visible)
        {
            var curtainFade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs),
                EasingFunction = easeCubicOut
            };
            curtainFade.Completed += (_, _) =>
            {
                EntranceCurtain.Visibility = Visibility.Collapsed;
                EntranceCurtain.Opacity = 0;
            };
            EntranceCurtain.BeginAnimation(OpacityProperty, curtainFade);
        }

        if (AuroraLayer != null)
        {
            var auroraFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs),
                EasingFunction = easeCubicOut
            };
            AuroraLayer.BeginAnimation(OpacityProperty, auroraFade);
        }

        if (AppLogoTitle != null)
        {
            var logoTrans = new TranslateTransform(-10, 0);
            AppLogoTitle.RenderTransform = logoTrans;

            var logoFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(850))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 40),
                EasingFunction = easeCubicOut
            };
            var logoSlide = new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(950))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 40),
                EasingFunction = easeQuint
            };

            AppLogoTitle.BeginAnimation(OpacityProperty, logoFade);
            logoTrans.BeginAnimation(TranslateTransform.XProperty, logoSlide);
        }

        if (AppAuthorTitle != null)
        {
            var authorTrans = new TranslateTransform(-8, 0);
            AppAuthorTitle.RenderTransform = authorTrans;

            var authorFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(850))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 80),
                EasingFunction = easeCubicOut
            };
            var authorSlide = new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(950))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 80),
                EasingFunction = easeQuint
            };

            AppAuthorTitle.BeginAnimation(OpacityProperty, authorFade);
            authorTrans.BeginAnimation(TranslateTransform.XProperty, authorSlide);
        }

        UIElement?[] statusBeads = [NetDot, NetLbl, VpnStatusPanel, ZapretDot, ZapretLbl, TgWsDot, TgWsLbl];
        int statusDelay = baseDelayMs + 120;
        foreach (var bead in statusBeads)
        {
            if (bead == null) continue;
            bead.Opacity = 0;
            var beadTrans = new TranslateTransform(-8, 0);
            bead.RenderTransform = beadTrans;

            var beadFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(750))
            {
                BeginTime = TimeSpan.FromMilliseconds(statusDelay),
                EasingFunction = easeCubicOut
            };
            var beadSlide = new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(850))
            {
                BeginTime = TimeSpan.FromMilliseconds(statusDelay),
                EasingFunction = easeQuint
            };

            bead.BeginAnimation(OpacityProperty, beadFade);
            beadTrans.BeginAnimation(TranslateTransform.XProperty, beadSlide);
            statusDelay += 45;
        }

        UIElement?[] navButtons = [ServicesBtn, ModsNavBtn, FaqNavBtn, DiagNavBtn, GameNavBtn, SettingsBtn, MinBtn, CloseBtn];
        int navDelay = baseDelayMs + 140;
        foreach (var btn in navButtons)
        {
            if (btn == null) continue;
            btn.Opacity = 0;
            var btnTrans = new TranslateTransform(14, 0);
            btn.RenderTransform = btnTrans;

            var btnFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(950))
            {
                BeginTime = TimeSpan.FromMilliseconds(navDelay),
                EasingFunction = easeCubicOut
            };
            var btnSlide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(1050))
            {
                BeginTime = TimeSpan.FromMilliseconds(navDelay),
                EasingFunction = easeQuint
            };

            btn.BeginAnimation(OpacityProperty, btnFade);
            btnTrans.BeginAnimation(TranslateTransform.XProperty, btnSlide);
            navDelay += 35;
        }

        if (MainTitleBlock != null)
        {
            var titleTrans = new TranslateTransform(0, 20);
            MainTitleBlock.RenderTransform = titleTrans;

            var titleFadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(1300))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 180),
                EasingFunction = easeCubicOut
            };
            var titleMoveUp = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 180),
                EasingFunction = easeQuint
            };

            MainTitleBlock.BeginAnimation(OpacityProperty, titleFadeIn);
            titleTrans.BeginAnimation(TranslateTransform.YProperty, titleMoveUp);
        }

        if (MainCoreBlock != null)
        {
            var coreTrans = new TranslateTransform(0, 22);
            MainCoreBlock.RenderTransform = coreTrans;

            var coreFadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(1350))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 320),
                EasingFunction = easeCubicOut
            };
            var coreMoveUp = new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(1450))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 320),
                EasingFunction = easeQuint
            };

            MainCoreBlock.BeginAnimation(OpacityProperty, coreFadeIn);
            coreTrans.BeginAnimation(TranslateTransform.YProperty, coreMoveUp);
        }

        if (IdleRingOuter != null)
        {
            var ringScale = new ScaleTransform(0.92, 0.92);
            IdleRingOuter.RenderTransformOrigin = new Point(0.5, 0.5);
            IdleRingOuter.RenderTransform = ringScale;
            IdleRingOuter.Opacity = 0;

            var ringFade = new DoubleAnimation(0.0, 0.5, TimeSpan.FromMilliseconds(1300))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 360),
                EasingFunction = easeCubicOut
            };
            var ringPop = new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 360),
                EasingFunction = easeQuint
            };

            IdleRingOuter.BeginAnimation(OpacityProperty, ringFade);
            ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, ringPop);
            ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, ringPop);
        }

        if (IdleRingInner != null)
        {
            var ringScale = new ScaleTransform(0.94, 0.94);
            IdleRingInner.RenderTransformOrigin = new Point(0.5, 0.5);
            IdleRingInner.RenderTransform = ringScale;
            IdleRingInner.Opacity = 0;

            var ringFade = new DoubleAnimation(0.0, 0.2, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 400),
                EasingFunction = easeCubicOut
            };
            var ringPop = new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(1500))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 400),
                EasingFunction = easeQuint
            };

            IdleRingInner.BeginAnimation(OpacityProperty, ringFade);
            ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, ringPop);
            ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, ringPop);
        }

        UIElement?[] speedElements = [DownloadLbl, UploadLbl, PingLbl, RescanBtn];
        int speedDelay = baseDelayMs + 420;
        foreach (var elem in speedElements)
        {
            if (elem == null) continue;
            elem.Opacity = 0;
            var elemTrans = new TranslateTransform(12, 0);
            elem.RenderTransform = elemTrans;

            var elemFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(900))
            {
                BeginTime = TimeSpan.FromMilliseconds(speedDelay),
                EasingFunction = easeCubicOut
            };
            var elemSlide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(1000))
            {
                BeginTime = TimeSpan.FromMilliseconds(speedDelay),
                EasingFunction = easeQuint
            };

            elem.BeginAnimation(OpacityProperty, elemFade);
            elemTrans.BeginAnimation(TranslateTransform.XProperty, elemSlide);
            speedDelay += 50;
        }

        if (SetupProgContainer != null)
        {
            var progTrans = new TranslateTransform(0, 18);
            SetupProgContainer.RenderTransform = progTrans;

            var progFadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(1300))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 480),
                EasingFunction = easeCubicOut
            };
            var progMoveUp = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(1400))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 480),
                EasingFunction = easeQuint
            };

            SetupProgContainer.BeginAnimation(OpacityProperty, progFadeIn);
            progTrans.BeginAnimation(TranslateTransform.YProperty, progMoveUp);
        }

        if (LogHeaderContainer != null)
        {
            var headTrans = new TranslateTransform(0, 14);
            LogHeaderContainer.RenderTransform = headTrans;

            var headFadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(1100))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 520),
                EasingFunction = easeCubicOut
            };
            var headMoveUp = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(1200))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 520),
                EasingFunction = easeQuint
            };

            LogHeaderContainer.BeginAnimation(OpacityProperty, headFadeIn);
            headTrans.BeginAnimation(TranslateTransform.YProperty, headMoveUp);
        }

        if (LogBoxClipGeom != null && LogBoxWrapper != null)
        {
            var boxTrans = new TranslateTransform(0, 14);
            LogBoxWrapper.RenderTransform = boxTrans;

            LogBoxClipGeom.BeginAnimation(RectangleGeometry.RectProperty,
                new RectAnimation(new Rect(0, 0, 1200, 0), new Rect(0, 0, 1200, 270), TimeSpan.FromMilliseconds(800))
                {
                    BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 560),
                    EasingFunction = easeQuint
                });
            boxTrans.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(850))
                {
                    BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 560),
                    EasingFunction = easeQuint
                });
        }

        if (AppBottomContent != null)
        {
            var footerTrans = new TranslateTransform(0, 12);
            AppBottomContent.RenderTransform = footerTrans;

            var footerFadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(1100))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 550),
                EasingFunction = easeCubicOut
            };
            var footerSlideUp = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(1200))
            {
                BeginTime = TimeSpan.FromMilliseconds(baseDelayMs + 550),
                EasingFunction = easeQuint
            };

            footerFadeIn.Completed += (_, _) =>
            {
                _isEntranceAnimating = false;

                _ = Task.Run(async () =>
                {
                    await Task.Delay(120);
                    Dispatcher.Invoke(() =>
                    {
                        CheckInternetOnStart();
                        StartActiveAppsMonitor();
                    });
                });
            };

            AppBottomContent.BeginAnimation(OpacityProperty, footerFadeIn);
            footerTrans.BeginAnimation(TranslateTransform.YProperty, footerSlideUp);
        }
    }

    private void BuildOnboardManualStart(StackPanel p)
    {
        AddOnboardTitle(p, "Ручная настройка");
        AddOnboardSub(p, "Потребуется скачать два компонента (Zapret и TgWsProxy) и указать к ним путь.");
        AddOnboardBtn(p, "Начать", "#3b82f6", () => ShowOnboardScreen(4));
    }

    private void BuildOnboardAutoDownload(StackPanel p)
    {
        var viewBox = new Viewbox
        {
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var iconCanvas = new Canvas
        {
            Width = 24,
            Height = 24
        };
        var trayPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M17 17H17.01M17.4 14H18C18.9319 14 19.3978 14 19.7654 14.1522C20.2554 14.3552 20.6448 14.7446 20.8478 15.2346C21 15.6022 21 16.0681 21 17C21 17.9319 21 18.3978 20.8478 18.7654C20.6448 19.2554 20.2554 19.6448 19.7654 19.8478C19.3978 20 18.9319 20 18 20H6C5.06812 20 4.60218 20 4.23463 19.8478C3.74458 19.6448 3.35523 19.2554 3.15224 18.7654C3 18.3978 3 17.9319 3 17C3 16.0681 3 15.6022 3.15224 15.2346C3.35523 14.7446 3.74458 14.3552 4.23463 14.1522C4.60218 14 5.06812 14 6 14H6.6"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        var arrowTrans = new TranslateTransform(0, 0);
        var arrowPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 15V4M12 15L9 12M12 15L15 12"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)),
            StrokeThickness = 2.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = arrowTrans
        };
        iconCanvas.Children.Add(trayPath);
        iconCanvas.Children.Add(arrowPath);
        viewBox.Child = iconCanvas;

        var arrowAnim = new DoubleAnimation(-4.5, 2.5, TimeSpan.FromMilliseconds(750))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        arrowTrans.BeginAnimation(TranslateTransform.YProperty, arrowAnim);

        p.Children.Add(viewBox);

        AddOnboardTitle(p, "Установка компонентов");
        AddOnboardSub(p, "Загружаем и настраиваем компоненты. Обычно это занимает не больше минуты.");

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
            FontSize = 14,
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
            bool executeInstall = false;
            bool useReserve = false;

            if (_onboardForceReserve)
            {
                executeInstall = true;
                useReserve = true;
            }
            else
            {
                AppendLog("Проверяю доступность GitHub...");
                var availability = await GitHubAvailabilityChecker.CheckAvailabilityAsync();
                if (availability == GitHubAvailabilityResult.Available)
                {
                    AppendLog("GitHub доступен. Начинаю загрузку...");
                    executeInstall = true;
                    useReserve = false;
                }
                else
                {
                    string reason = availability == GitHubAvailabilityResult.Timeout ? "таймаут подключения" : "блокировка или проблемы с сетью";
                    AppendLog($"⚠️ GitHub недоступен ({reason}). Автоматически переключаюсь на встроенную резервную копию...");
                    executeInstall = true;
                    useReserve = true;
                }
            }

            bool success = false;
            if (executeInstall)
            {
                success = await AutoDownloadService.AutoInstallAllAsync(
                    msg => AppendLog(msg),
                    prog => Dispatcher.Invoke(() =>
                    {
                        progBar.Value = prog * 100;
                        progText.Text = $"Установка... {(int)(prog * 100)}%";
                    }),
                    err => AppendLog("ОШИБКА: " + err),
                    preserveLists: false,
                    forceReserve: useReserve
                );
            }

            Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    _settings = SettingsService.Load();
                    LoadSettingsToPanel();

                    progBar.Value = 100;
                    progBar.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    progText.Text = "Компоненты успешно установлены";
                    progText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));

                    AddOnboardBtn(actionsPanel, "Далее", "#3b82f6", () => ShowOnboardScreen(15));
                }
                else
                {
                    progBar.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    progText.Text = "Не удалось загрузить файлы";
                    progText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

                    AddOnboardBtn(actionsPanel, "Настроить вручную", "#ef4444", () =>
                    {
                        _onboardIsManual = true;
                        ShowOnboardScreen(17);
                    });
                }
            });
        });
    }

    private static void AddOnboardTitle(StackPanel p, string text) =>
        p.Children.Add(new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 24,
            FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        });

    private static void AddOnboardSub(StackPanel p, string text) =>
        p.Children.Add(new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 16.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });

    private void AddOnboardBtn(StackPanel p, string text, string bgHex, Action action,
        string foreground = "#ffffff", bool stretch = false)
    {
        var bgBrush = (SolidColorBrush)new BrushConverter().ConvertFrom(bgHex)!;
        var c = bgBrush.Color;
        var hoverBrush = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Min(255, c.R * 1.07 + 8),
            (byte)Math.Min(255, c.G * 1.07 + 8),
            (byte)Math.Min(255, c.B * 1.07 + 8)));

        var btn = new Button
        {
            Content             = text,
            Background          = bgBrush,
            Foreground          = (SolidColorBrush)new BrushConverter().ConvertFrom(foreground)!,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 16,
            FontWeight          = FontWeights.SemiBold,
            Height              = 48,
            MinWidth            = stretch ? 0 : 200,
            Padding             = new Thickness(24, 0, 24, 0),
            Cursor              = Cursors.Hand,
            BorderThickness     = new Thickness(0),
            HorizontalAlignment = stretch ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Center,
            Margin              = new Thickness(0, 0, 0, 12),
        };

        btn.Template = CreateSimpleBtnTemplate();
        AttachModernButtonHoverEffect(btn, bgBrush, hoverBrush, c);
        btn.Click += (_, _) => action();
        p.Children.Add(btn);
    }

    private static void AttachModernButtonHoverEffect(Button btn, Brush defaultBg, Brush hoverBg, Color glowColor)
    {
        TextOptions.SetTextFormattingMode(btn, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(btn, TextRenderingMode.Grayscale);
        btn.SnapsToDevicePixels = true;
        btn.UseLayoutRounding = true;

        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 0,
            ShadowDepth = 0,
            Color = glowColor,
            Opacity = 0
        };
        btn.Effect = glow;

        TranslateTransform GetOrCreateTranslate()
        {
            if (btn.RenderTransform is TransformGroup tg)
            {
                var tt = tg.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (tt == null)
                {
                    tt = new TranslateTransform(0, 0);
                    tg.Children.Add(tt);
                }
                return tt;
            }
            else if (btn.RenderTransform is TranslateTransform directTt)
            {
                return directTt;
            }
            else
            {
                var newTg = new TransformGroup();
                if (btn.RenderTransform != null) newTg.Children.Add(btn.RenderTransform);
                var tt = new TranslateTransform(0, 0);
                newTg.Children.Add(tt);
                btn.RenderTransform = newTg;
                return tt;
            }
        }

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var easeQuintic = new QuinticEase { EasingMode = EasingMode.EaseOut };

        btn.MouseEnter += (_, _) =>
        {
            btn.Background = hoverBg;

            glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0.12, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeOut });
            glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(6, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeOut });

            var tt = GetOrCreateTranslate();
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-2, TimeSpan.FromMilliseconds(180)) { EasingFunction = easeOut });

            if (btn.Template.FindName("Sheen", btn) is System.Windows.Shapes.Rectangle sheen)
            {
                double startX = -120;
                double endX = btn.ActualWidth > 0 ? btn.ActualWidth + 70 : 260;

                var sheenTg = new TransformGroup();
                sheenTg.Children.Add(new SkewTransform(22, 0));
                var sheenTrans = new TranslateTransform(startX, 0);
                sheenTg.Children.Add(sheenTrans);
                sheen.RenderTransform = sheenTg;

                sheen.Opacity = 0.85;
                var sheenAnim = new DoubleAnimation(startX, endX, TimeSpan.FromMilliseconds(480))
                {
                    EasingFunction = easeQuintic
                };
                sheenTrans.BeginAnimation(TranslateTransform.XProperty, sheenAnim);

                var fadeAnim = new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(480))
                {
                    EasingFunction = easeOut
                };
                sheen.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
        };

        btn.MouseLeave += (_, _) =>
        {
            btn.Background = defaultBg;

            glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { EasingFunction = easeOut });
            glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { EasingFunction = easeOut });

            var tt = GetOrCreateTranslate();
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easeOut });
        };

        btn.PreviewMouseDown += (_, _) =>
        {
            var tt = GetOrCreateTranslate();
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(80)) { EasingFunction = easeOut });
        };

        btn.PreviewMouseUp += (_, _) =>
        {
            var tt = GetOrCreateTranslate();
            double targetY = btn.IsMouseOver ? -2 : 0;
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(120)) { EasingFunction = easeOut });
        };
    }

    private static ControlTemplate CreateSimpleBtnTemplate()
    {
        var tmpl = new ControlTemplate(typeof(Button));
        var rootGrid = new FrameworkElementFactory(typeof(Grid));
        rootGrid.SetValue(Grid.ClipToBoundsProperty, true);

        var bd = new FrameworkElementFactory(typeof(Border));
        bd.Name = "BgBorder";
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        rootGrid.AppendChild(bd);

        var sheen = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
        sheen.Name = "Sheen";
        sheen.SetValue(UIElement.IsHitTestVisibleProperty, false);
        sheen.SetValue(UIElement.OpacityProperty, 0.0);
        sheen.SetValue(FrameworkElement.WidthProperty, 60.0);
        sheen.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
        sheen.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);

        var sheenBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0)
            }
        };
        sheenBrush.Freeze();
        sheen.SetValue(System.Windows.Shapes.Shape.FillProperty, sheenBrush);

        rootGrid.AppendChild(sheen);

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        rootGrid.AppendChild(cp);

        tmpl.VisualTree = rootGrid;
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
            string taskName = "NetFix";
            string path = Environment.ProcessPath;

            if (enable)
            {
                var psi = new ProcessStartInfo("schtasks.exe",
                    $"/create /tn \"{taskName}\" /tr \"\\\"{path}\\\" --autostart\" /sc onlogon /rl highest /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }
            else
            {
                var psi = new ProcessStartInfo("schtasks.exe",
                    $"/delete /tn \"{taskName}\" /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }

            try
            {
                using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                regKey?.DeleteValue("NetFix", false);
            }
            catch { }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Ошибка автозапуска: {ex.Message}");
        }
    }

    private static object CreateButtonContentWithIcon(string iconKey, string text, Brush iconBrush)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };

        var geometry = System.Windows.Application.Current.TryFindResource(iconKey) as PathGeometry;

        if (geometry == null && iconKey == "RefreshIcon")
        {
            geometry = Geometry.Parse("M21,11c-0.6,0-1,0.4-1,1c0,2.9-1.5,5.5-4,6.9c-3.8,2.2-8.7,0.9-10.9-2.9C2.9,12.2,4.2,7.3,8,5.1c3.3-1.9,7.3-1.2,9.8,1.4h-2.4c-0.6,0-1,0.4-1,1s0.4,1,1,1h4.5c0.6,0,1-0.4,1-1V3c0-0.6-0.4-1-1-1s-1,0.4-1,1v1.8C17,3,14.6,2,12,2C6.5,2,2,6.5,2,12s4.5,10,10,10c5.5,0,10-4.5,10-10C22,11.4,21.6,11,21,11z") as PathGeometry;
        }

        if (geometry == null)
        {
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

    private void InitNetworkMonitor()
    {
        var (rx, tx) = GetNetworkBytes();
        _lastBytesReceived = rx;
        _lastBytesSent = tx;

        DownloadLbl.Text = "—";
        UploadLbl.Text   = "—";
        PingLbl.Text     = "—";

        _netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _netTimer.Tick += NetTimer_Tick;
        _netTimer.Start();

        _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pingTimer.Tick += async (s, e) => await UpdatePingAsync();
        _pingTimer.Start();

        this.StateChanged += (s, e) =>
        {
            if (this.WindowState == WindowState.Minimized)
            {
                StopConnectionAnalysis();
                _monitorTimer?.Stop();
                _netTimer?.Stop();
                _pingTimer?.Stop();
            }
            else
            {
                _monitorTimer?.Start();
                _netTimer?.Start();
                _pingTimer?.Start();
                if (DiagPage.Visibility == Visibility.Visible && DiagConnectionScreen.Visibility == Visibility.Visible)
                {
                    StartConnectionAnalysis();
                }
            }
        };

        Task.Run(async () => await RunSpeedTestAsync());
        Task.Run(async () => await UpdatePingAsync());
    }

    private static readonly HttpClient _speedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(16)
    };

    private async Task RunSpeedTestAsync()
    {
        _speedTestDone = false;
        _dlSamples.Clear();
        _ulSamples.Clear();

        Dispatcher.Invoke(() =>
        {
            DownloadLbl.Text = "—";
            UploadLbl.Text   = "—";
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var urls = Enumerable.Repeat("https://speedtest.selectel.ru/100MB", 4).ToArray();
            long totalDlBytes = 0;
            var dlCancel = new CancellationTokenSource(TimeSpan.FromSeconds(14));

            var sampleCts = new CancellationTokenSource();
            long prevBytes = 0;
            var sampleTask = Task.Run(async () =>
            {
                while (!sampleCts.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, sampleCts.Token); }
                    catch { break; }
                    long now = Interlocked.Read(ref totalDlBytes);
                    double instantMbps = (now - prevBytes) * 8.0 / 1_000_000.0;
                    prevBytes = now;
                    if (instantMbps > 0.1)
                    {
                        lock (_dlSamples) _dlSamples.Add(instantMbps);
                        double speed = CalcFinalSpeed(_dlSamples);
                        Dispatcher.Invoke(() => DownloadLbl.Text = $"{speed:0.0}");
                    }
                }
            });

            var dlTasks = urls.Select(async url =>
            {
                try
                {
                    _speedHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                    using var resp = await _speedHttp.GetAsync(
                        url + "?nocache=" + Guid.NewGuid(),
                        HttpCompletionOption.ResponseHeadersRead,
                        dlCancel.Token);
                    using var stream = await resp.Content.ReadAsStreamAsync(dlCancel.Token);
                    var pool = System.Buffers.ArrayPool<byte>.Shared;
                    var buf = pool.Rent(131072);
                    try
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buf, 0, 131072, dlCancel.Token)) > 0)
                            Interlocked.Add(ref totalDlBytes, read);
                    }
                    finally { pool.Return(buf); }
                }
                catch (OperationCanceledException) { }
                catch { }
            }).ToArray();

            await Task.WhenAll(dlTasks);
            sampleCts.Cancel();
            try { await sampleTask; } catch { }

            sw.Stop();
            double dlSec = sw.Elapsed.TotalSeconds;
            _finalDownloadMbps = dlSec > 0
                ? totalDlBytes * 8.0 / dlSec / 1_000_000.0
                : CalcFinalSpeed(_dlSamples);
            Dispatcher.Invoke(() => DownloadLbl.Text = _finalDownloadMbps > 0
                ? $"{_finalDownloadMbps:0.0}" : "—");
        }
        catch { }

        try
        {
            sw.Restart();
            long totalUlBytes = 0;
            var ulCancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var ulSampleCts = new CancellationTokenSource();
            long prevUlBytes = 0;
            var ulSampleTask = Task.Run(async () =>
            {
                while (!ulSampleCts.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, ulSampleCts.Token); }
                    catch { break; }
                    long now = Interlocked.Read(ref totalUlBytes);
                    double instantMbps = (now - prevUlBytes) * 8.0 / 1_000_000.0;
                    prevUlBytes = now;
                    if (instantMbps > 0.1)
                    {
                        lock (_ulSamples) _ulSamples.Add(instantMbps);
                        double speed = CalcFinalSpeed(_ulSamples);
                        Dispatcher.Invoke(() => UploadLbl.Text = $"{speed:0.0}");
                    }
                }
            });

            var ulTasks = Enumerable.Range(0, 4).Select(async _ =>
            {
                try
                {
                    var pool = System.Buffers.ArrayPool<byte>.Shared;
                    int size = 4 * 1024 * 1024;
                    var data = pool.Rent(size);
                    try
                    {
                        Random.Shared.NextBytes(data);
                        while (!ulCancel.Token.IsCancellationRequested)
                        {
                            try
                            {
                                var content = new ByteArrayContent(data, 0, size);
                                var req = new HttpRequestMessage(HttpMethod.Post, "https://httpbin.org/post") { Content = content };
                                using var resp = await _speedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ulCancel.Token);
                                Interlocked.Add(ref totalUlBytes, size);
                            }
                            catch (OperationCanceledException) { break; }
                            catch { break; }
                        }
                    }
                    finally { pool.Return(data); }
                }
                catch { }
            }).ToArray();

            await Task.WhenAll(ulTasks);
            ulSampleCts.Cancel();
            try { await ulSampleTask; } catch { }

            sw.Stop();
            double ulSec = sw.Elapsed.TotalSeconds;
            _finalUploadMbps = ulSec > 0
                ? totalUlBytes * 8.0 / ulSec / 1_000_000.0
                : CalcFinalSpeed(_ulSamples);
        }
        catch { }

        _speedTestDone = true;
        Dispatcher.Invoke(() =>
        {
            DownloadLbl.Text = _finalDownloadMbps > 0 ? $"{_finalDownloadMbps:0.0}" : "—";
            UploadLbl.Text   = _finalUploadMbps > 0   ? $"{_finalUploadMbps:0.0}"   : "—";
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private async void NetTimer_Tick(object? sender, EventArgs e)
    {
        var (rx, tx) = await Task.Run(GetNetworkBytes);
        _lastBytesReceived = rx;
        _lastBytesSent = tx;

        if (_speedTestDone)
            _netTimer.Stop();
    }

    private static (long rx, long tx) GetNetworkBytes()
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
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

    private readonly System.Net.NetworkInformation.Ping _ping = new();

    private async Task UpdatePingAsync()
    {
        try
        {
            long total = 0;
            int count = 0;

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var reply = await _ping.SendPingAsync("1.1.1.1", 2000);
                    if (reply.Status == IPStatus.Success)
                    {
                        total += reply.RoundtripTime;
                        count++;
                    }
                    await Task.Delay(200);
                }
                catch { }
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

                PingLbl.Foreground = new SolidColorBrush(good
                    ? Color.FromRgb(0xf0, 0xf0, 0xf0)
                    : Color.FromRgb(0xf5, 0x9e, 0x0b));
            });
        }
        catch { }
    }

    private static double CalcFinalSpeed(List<double> samples)
    {
        if (samples.Count == 0) return 0;

        var stable = samples.Count > 2 ? samples.Skip(2).ToList() : samples;
        if (stable.Count == 0) return 0;

        var sorted = stable.OrderByDescending(x => x).ToList();
        int takeCount = Math.Max(1, (int)(sorted.Count * 0.2));

        return sorted.Take(takeCount).Average();
    }

    private async void RescanBtn_Click(object sender, RoutedEventArgs e)
    {
        RescanBtn.IsEnabled = false;

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

        transform.BeginAnimation(RotateTransform.AngleProperty, null);
        RescanBtn.RenderTransform = null;
        RescanBtn.IsEnabled = true;
    }

    private int _listeningLane = -1;

    private void GameSettingsMenuBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        GameMenuView.Visibility = Visibility.Collapsed;
        GameSettingsView.Visibility = Visibility.Visible;
        LoadKeyLabels();
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

        btns[_listeningLane].BorderThickness = new Thickness(1);
        btns[_listeningLane].BorderBrush = new SolidColorBrush(Colors.White);
        labels[_listeningLane].Text = "...";

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
        if (keyStr.Length > 1 && !keyStr.StartsWith("D") && keyStr != "Space") return;
        if (keyStr.StartsWith("D") && keyStr.Length == 2) keyStr = keyStr[1..];

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

        var query = history.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_statsSearchText))
            query = query.Where(t =>
                t.TrackTitle.Contains(_statsSearchText, StringComparison.OrdinalIgnoreCase));

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

        AddSectionHeader("ЗА ВСЕ ВРЕМЯ");

        AddDetailRow("Всего нажатий", t.TotalKeyPresses.ToString("N0"));
        AddDetailRow("Попаданий", $"{t.TotalHits:N0}", $"{hitRate:F1}% эффективность");
        AddDetailRow("Промахов", $"{t.TotalMisses:N0}", t.TotalMisses > 0 ? "#ef4444" : "#666");
        AddDetailRow("Lifetime Accuracy", $"{lifeAcc:F2}%", "отношение попаданий к нотам");

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
        var flashIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        var flashOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(2000))
            { BeginTime = TimeSpan.FromMilliseconds(120) };
        flashOut.Completed += (_, _) => overlay.Children.Remove(flash);
        flash.BeginAnimation(UIElement.OpacityProperty, flashIn);
        flash.BeginAnimation(UIElement.OpacityProperty, flashOut);

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
            var waveAnim = new DoubleAnimationUsingKeyFrames();
            waveAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            waveAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
            waveAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
            waveAnim.Completed += (_, _) => overlay.Children.Remove(wave);
            wave.BeginAnimation(UIElement.OpacityProperty, waveAnim);
        }

        for (int p = 0; p < 10; p++)
            SpawnDoubleStrikeParticle(overlay, canvasW, hitY, rng, palette);

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            for (int p = 0; p < 10; p++)
                SpawnDoubleStrikeParticle(overlay, canvasW, hitY, rng, palette);
        };
        t.Start();

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

    public record DnsServerInfo(string Name, string Description, string Primary, string Secondary, string DohTemplate = "");

    private static readonly IReadOnlyList<DnsServerInfo> PredefinedDnsServers = [
        new DnsServerInfo("Системный (DHCP)", "Использовать DNS-серверы, полученные от роутера или провайдера", "dhcp", "", ""),
        new DnsServerInfo("Xbox-DNS.ru", "Альтернативный DNS для восстановления доступа к сетевым службам Xbox Live в РФ", "111.88.96.50", "111.88.96.51", "https://xbox-dns.ru/dns-query"),
        new DnsServerInfo("Cloudflare DNS", "Высокопроизводительный публичный DNS-сервер с упором на скорость и приватность", "1.1.1.1", "1.0.0.1", "https://cloudflare-dns.com/dns-query"),
        new DnsServerInfo("Google Public DNS", "Надежный глобальный DNS-сервер с высокой стабильностью работы", "8.8.8.8", "8.8.4.4", "https://dns.google/dns-query"),
        new DnsServerInfo("Yandex.DNS", "Быстрый публичный DNS-сервер от Яндекса с минимальной задержкой в РФ", "77.88.8.8", "77.88.8.1", ""),
        new DnsServerInfo("AdGuard DNS", "Альтернативный DNS с функцией блокировки рекламы, трекеров и фишинга", "94.140.14.14", "94.140.15.15", "https://dns.adguard-dns.com/dns-query")
    ];

    private void DnsMenuBtn_Click(object s, RoutedEventArgs e)
    {
        HeaderMainView.Visibility = Visibility.Collapsed;
        HeaderDnsView.Visibility = Visibility.Visible;

        LoadDnsServers();

        var slideOut = new DoubleAnimation(0, -300, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slideIn = new DoubleAnimation(300, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        MainViewTrans.BeginAnimation(TranslateTransform.XProperty, slideOut);
        DnsViewTrans.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private void DnsBackBtn_Click(object s, RoutedEventArgs e)
    {
        HeaderDnsView.Visibility = Visibility.Collapsed;
        HeaderMainView.Visibility = Visibility.Visible;

        var slideIn = new DoubleAnimation(-300, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slideOut = new DoubleAnimation(0, 300, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        MainViewTrans.BeginAnimation(TranslateTransform.XProperty, slideIn);
        DnsViewTrans.BeginAnimation(TranslateTransform.XProperty, slideOut);
    }

    private static List<string> GetCurrentDnsAddresses()
    {
        try
        {
            var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                    ni.GetIPProperties().GatewayAddresses.Any());

            if (activeInterface is not null)
            {
                var props = activeInterface.GetIPProperties();
                return props.DnsAddresses.Select(addr => addr.ToString()).ToList();
            }
        }
        catch { }
        return [];
    }

    private void LoadDnsServers()
    {
        DnsListContainer.Children.Clear();

        var currentDnsList = GetCurrentDnsAddresses();

        foreach (var dns in PredefinedDnsServers)
        {
            bool isActive = false;
            if (dns.Primary == "dhcp")
            {
                isActive = !PredefinedDnsServers.Any(x => x.Primary != "dhcp" && currentDnsList.Contains(x.Primary));
            }
            else
            {
                isActive = currentDnsList.Contains(dns.Primary);
            }

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(isActive ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = isActive ? new Thickness(1.5) : new Thickness(1.0),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand,
                Height = 80,
                Tag = dns
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBorder = new Border
            {
                Width = 26, Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(isActive ? Color.FromRgb(0x05, 0x2e, 0x16) : Color.FromRgb(0x1a, 0x2a, 0x3a)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            iconBorder.Child = new TextBlock
            {
                Text = dns.Name.Substring(0, 1).ToUpper(),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(isActive ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0x3b, 0x82, 0xf6)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            var info = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            namePanel.Children.Add(new TextBlock
            {
                Text = dns.Name,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold
            });

            if (isActive)
            {
                var activeBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x05, 0x2e, 0x16)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1.5),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                activeBadge.Child = new TextBlock
                {
                    Text = "активен",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                    FontWeight = FontWeights.Bold
                };
                namePanel.Children.Add(activeBadge);
            }

            info.Children.Add(namePanel);

            info.Children.Add(new TextBlock
            {
                Text = dns.Description,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Margin = new Thickness(0, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 30,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (dns.Primary != "dhcp")
            {
                info.Children.Add(new TextBlock
                {
                    Text = $"{dns.Primary} | {dns.Secondary}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            Grid.SetColumn(iconBorder, 0);
            Grid.SetColumn(info, 1);
            grid.Children.Add(iconBorder);
            grid.Children.Add(info);

            card.Child = grid;

            card.MouseLeftButtonUp += DnsCard_Click;
            card.MouseEnter += (_, _) => {
                if (!isActive) card.Background = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x2d));
            };
            card.MouseLeave += (_, _) => {
                if (!isActive) card.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            };

            DnsListContainer.Children.Add(card);
        }

        DnsListContainer.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2d)),
            Margin = new Thickness(0, 5, 0, 8)
        });

        var resetCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1.0),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 0, 5),
            Cursor = Cursors.Hand,
            Height = 80
        };

        var resetGrid = new Grid();
        resetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        resetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var resetIconBorder = new Border
        {
            Width = 24, Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x45, 0x1a, 0x1a)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        resetIconBorder.Child = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
            Stroke = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)),
            StrokeThickness = 2,
            Width = 12, Height = 12,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        var resetInfo = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        resetInfo.Children.Add(new TextBlock
        {
            Text = "Сбросить все настройки DNS",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold
        });
        resetInfo.Children.Add(new TextBlock
        {
            Text = "Вернуть настройки сетевых адаптеров на автоматическое получение DNS (DHCP)",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 28,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        Grid.SetColumn(resetIconBorder, 0);
        Grid.SetColumn(resetInfo, 1);
        resetGrid.Children.Add(resetIconBorder);
        resetGrid.Children.Add(resetInfo);
        resetCard.Child = resetGrid;

        resetCard.MouseLeftButtonUp += (s, e) => ShowResetAllDnsConfirmDialog();
        resetCard.MouseEnter += (_, _) => {
            resetCard.Background = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x2d));
            resetCard.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        };
        resetCard.MouseLeave += (_, _) => {
            resetCard.Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            resetCard.BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        };

        DnsListContainer.Children.Add(resetCard);
    }

    private async void RunDnsTestInApp(DnsServerInfo dns)
    {
        if (_isDialogOpen)
        {
            return;
        }
        _isDialogOpen = true;

        var cts = new CancellationTokenSource();
        Color accentColor = Color.FromRgb(0xea, 0xb3, 0x08);

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        var dialogCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            Width = 480,
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

        var cardContent = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        var titleText = new TextBlock
        {
            Text = $"Диагностика: {dns.Name}",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        };
        cardContent.Children.Add(titleText);

        var logScroll = new ScrollViewer
        {
            Height = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 14)
        };
        var logBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0f, 0x0f, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x30)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10)
        };
        var logText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16
        };
        logBorder.Child = logText;
        logScroll.Content = logBorder;
        cardContent.Children.Add(logScroll);

        var progressBar = new System.Windows.Controls.ProgressBar
        {
            Height = 3,
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(accentColor),
            Background = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e)),
            Margin = new Thickness(0, 0, 0, 14)
        };
        cardContent.Children.Add(progressBar);

        var verdictText = new TextBlock
        {
            Text = "Выполняется диагностика DNS-серверов...",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        cardContent.Children.Add(verdictText);

        var closeBtn = new Button
        {
            Content = "Отмена",
            Width = 120, Height = 36,
            Foreground = Brushes.White,
            Cursor = Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        closeBtn.Style = (Style)FindResource("OutlineBtn");

        var btnTemplate = new ControlTemplate(typeof(Button));
        var btnBorder = new FrameworkElementFactory(typeof(Border));
        btnBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        btnBorder.SetValue(Border.PaddingProperty, new Thickness(0));
        var btnPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        btnPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        btnPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        btnBorder.AppendChild(btnPresenter);
        btnTemplate.VisualTree = btnBorder;
        closeBtn.Template = btnTemplate;

        cardContent.Children.Add(closeBtn);
        dialogCard.Child = cardContent;
        MainGrid.Children.Add(dialogCard);

        overlay.Opacity = 0;
        dialogCard.Opacity = 0;
        dialogCard.RenderTransform = new ScaleTransform(0.95, 0.95);
        dialogCard.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        var scaleIn = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        overlay.BeginAnimation(OpacityProperty, fadeIn);
        dialogCard.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

        bool isDone = false;

        closeBtn.Click += (s, e) =>
        {
            if (!isDone)
            {
                cts.Cancel();
            }
            bool completedHandled = false;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s2, e2) =>
            {
                if (completedHandled) return;
                completedHandled = true;

                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                _isDialogOpen = false;
            };
            overlay.BeginAnimation(OpacityProperty, fadeOut);
            dialogCard.BeginAnimation(OpacityProperty, fadeOut);
        };

        _ = Task.Run(async () =>
        {
            StringBuilder sb = new StringBuilder();
            void AppendLog(string text)
            {
                Dispatcher.Invoke(() =>
                {
                    sb.AppendLine(text);
                    logText.Text = sb.ToString();
                    logScroll.ScrollToEnd();
                });
            }

            AppendLog($"[{DateTime.Now:HH:mm:ss}] Начало проверки DNS: {dns.Name}");
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Основной IP: {dns.Primary}");
            if (!string.IsNullOrEmpty(dns.Secondary))
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] Резервный IP: {dns.Secondary}");
            }
            AppendLog("----------------------------------------");

            bool primaryGoogleOk = false;
            bool primaryGeminiOk = false;
            bool primaryChatGptOk = false;

            bool secondaryGoogleOk = false;
            bool secondaryGeminiOk = false;
            bool secondaryChatGptOk = false;

            if (cts.Token.IsCancellationRequested) return;
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [Primary] Тест резолва google.com...");
            var resGoogle = await PerformDnsResolveAsync(dns.Primary, "google.com", cts.Token);
            if (resGoogle.Success)
            {
                primaryGoogleOk = true;
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [Primary] Успешно! Время ответа: {resGoogle.ElapsedMs} мс");

                if (!cts.Token.IsCancellationRequested)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [Primary] Проверка доступа к Gemini...");
                    var resGemini = await TestServiceAccessAsync("Gemini", "gemini.google.com", dns.Primary, cts.Token);
                    primaryGeminiOk = resGemini.Success;
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [Primary] Gemini: {resGemini.Details}");
                }

                if (!cts.Token.IsCancellationRequested)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [Primary] Проверка доступа к ChatGPT...");
                    var resChatGPT = await TestServiceAccessAsync("ChatGPT", "chatgpt.com", dns.Primary, cts.Token);
                    primaryChatGptOk = resChatGPT.Success;
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [Primary] ChatGPT: {resChatGPT.Details}");
                }
            }
            else
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [Primary] Ошибка резолва: {resGoogle.Output}");
            }

            if (!string.IsNullOrEmpty(dns.Secondary) && !cts.Token.IsCancellationRequested)
            {
                AppendLog("----------------------------------------");
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [Secondary] Тест резолва google.com...");
                var resGoogleSec = await PerformDnsResolveAsync(dns.Secondary, "google.com", cts.Token);
                if (resGoogleSec.Success)
                {
                    secondaryGoogleOk = true;
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [Secondary] Успешно! Время ответа: {resGoogleSec.ElapsedMs} мс");

                    if (!cts.Token.IsCancellationRequested)
                    {
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] [Secondary] Проверка доступа к Gemini...");
                        var resGeminiSec = await TestServiceAccessAsync("Gemini", "gemini.google.com", dns.Secondary, cts.Token);
                        secondaryGeminiOk = resGeminiSec.Success;
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] [Secondary] Gemini: {resGeminiSec.Details}");
                    }

                    if (!cts.Token.IsCancellationRequested)
                    {
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] [Secondary] Проверка доступа к ChatGPT...");
                        var resChatGPTSec = await TestServiceAccessAsync("ChatGPT", "chatgpt.com", dns.Secondary, cts.Token);
                        secondaryChatGptOk = resChatGPTSec.Success;
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] [Secondary] ChatGPT: {resChatGPTSec.Details}");
                    }
                }
                else
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [Secondary] Ошибка резолва: {resGoogleSec.Output}");
                }
            }

            if (cts.Token.IsCancellationRequested) return;

            Dispatcher.Invoke(() =>
            {
                progressBar.Visibility = Visibility.Collapsed;
                isDone = true;
                closeBtn.Content = "Готово";
                closeBtn.Style = (Style)FindResource("AccentBtn");
                closeBtn.Template = btnTemplate;

                bool primaryFullyOk = primaryGoogleOk && primaryGeminiOk && primaryChatGptOk;
                bool secondaryFullyOk = string.IsNullOrEmpty(dns.Secondary) || (secondaryGoogleOk && secondaryGeminiOk && secondaryChatGptOk);

                bool anyGoogleOk = primaryGoogleOk || secondaryGoogleOk;
                bool allFullyOk = primaryFullyOk && secondaryFullyOk;

                if (allFullyOk)
                {
                    verdictText.Text = "DNS-сервер работает отлично! Доступ к Gemini и ChatGPT открыт.";
                    verdictText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    dialogCard.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                    logText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                }
                else if (anyGoogleOk)
                {
                    verdictText.Text = "DNS работает, но Gemini или ChatGPT заблокированы!";
                    verdictText.Foreground = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
                    dialogCard.BorderBrush = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
                    logText.Foreground = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
                }
                else
                {
                    verdictText.Text = "DNS-сервер не отвечает на запросы!";
                    verdictText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    dialogCard.BorderBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    logText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                }
            });
        });
    }

    private record DnsResolveResult(bool Success, string Output, long ElapsedMs, List<string> Ips);

    private static async Task<DnsResolveResult> PerformDnsResolveAsync(string dnsIp, string domain, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var localIp = GetPhysicalAdapterIP();
            var localEp = localIp is not null ? new IPEndPoint(localIp, 0) : new IPEndPoint(IPAddress.Any, 0);

            using var udp = new UdpClient(localEp);
            udp.Client.SendTimeout = 2500;
            udp.Client.ReceiveTimeout = 2500;

            var txId = (ushort)Random.Shared.Next(0, 65536);
            var query = BuildDnsQuery(domain, txId);
            var serverEp = new IPEndPoint(IPAddress.Parse(dnsIp), 53);

            await udp.SendAsync(query, serverEp, token);

            var receiveTask = udp.ReceiveAsync(token);
            var result = await receiveTask.AsTask().WaitAsync(TimeSpan.FromSeconds(2.5), token);

            var ips = ParseDnsResponse(result.Buffer);
            sw.Stop();

            if (ips.Count == 0)
            {
                return new DnsResolveResult(false, "DNS-сервер вернул пустой ответ или не содержит записей типа A.", sw.ElapsedMilliseconds, []);
            }

            return new DnsResolveResult(true, string.Join("\n", ips), sw.ElapsedMilliseconds, ips);
        }
        catch (OperationCanceledException)
        {
            return new DnsResolveResult(false, "Время ожидания запроса (2.5 сек) истекло.", sw.ElapsedMilliseconds, []);
        }
        catch (Exception ex)
        {
            return new DnsResolveResult(false, $"Ошибка DNS: {ex.Message}", sw.ElapsedMilliseconds, []);
        }
    }

    private static IPAddress? GetPhysicalAdapterIP()
    {
        try
        {
            var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                    ni.GetIPProperties().GatewayAddresses.Any());

            if (activeInterface is not null)
            {
                var ipProp = activeInterface.GetIPProperties();
                var ipv4 = ipProp.UnicastAddresses
                    .FirstOrDefault(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 is not null)
                {
                    return ipv4.Address;
                }
            }
        }
        catch { }
        return null;
    }

    private static byte[] BuildDnsQuery(string domain, ushort txId)
    {
        var buf = new List<byte>
        {
            (byte)(txId >> 8), (byte)(txId & 0xff),
            0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        foreach (var part in domain.Split('.'))
        {
            buf.Add((byte)part.Length);
            buf.AddRange(Encoding.ASCII.GetBytes(part));
        }
        buf.Add(0);
        buf.AddRange([0x00, 0x01, 0x00, 0x01]);
        return [.. buf];
    }

    private static List<string> ParseDnsResponse(byte[] data)
    {
        var ips = new List<string>();
        try
        {
            int ancount = (data[6] << 8) | data[7];
            if (ancount == 0) return ips;
            int i = 12;
            while (i < data.Length && data[i] != 0) i += data[i] + 1;
            i += 5;
            for (int a = 0; a < ancount && i + 10 < data.Length; a++)
            {
                if ((data[i] & 0xc0) == 0xc0)
                {
                    i += 2;
                }
                else
                {
                    while (i < data.Length && data[i] != 0) i += data[i] + 1;
                    i++;
                }
                int rtype = (data[i] << 8) | data[i + 1];
                int rdlen = (data[i + 8] << 8) | data[i + 9];
                i += 10;
                if (rtype == 1 && rdlen == 4 && i + 4 <= data.Length)
                    ips.Add($"{data[i]}.{data[i+1]}.{data[i+2]}.{data[i+3]}");
                i += rdlen;
            }
        }
        catch { }
        return ips;
    }

    private record ServiceTestResult(bool Success, string Details);

    private static async Task<ServiceTestResult> TestServiceAccessAsync(string serviceName, string domain, string dnsIp, CancellationToken token)
    {
        var resolveResult = await PerformDnsResolveAsync(dnsIp, domain, token);
        if (!resolveResult.Success)
        {
            return new ServiceTestResult(false, $"Не удалось разрешить домен {domain}: {resolveResult.Output}");
        }

        if (resolveResult.Ips.Count == 0)
        {
            return new ServiceTestResult(false, $"В ответе DNS для {domain} не найдено IP-адресов.");
        }

        string firstIp = resolveResult.Ips[0];
        string ipListStr = string.Join(", ", resolveResult.Ips);

        try
        {
            var localIp = GetPhysicalAdapterIP();
            var localEp = localIp is not null ? new IPEndPoint(localIp, 0) : new IPEndPoint(IPAddress.Any, 0);

            using var tcp = new TcpClient(localEp);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(2.5));

            await tcp.ConnectAsync(firstIp, 443, cts.Token);
            return new ServiceTestResult(true, $"Успешно! {domain} -> [{ipListStr}]. Подключение к {firstIp}:443 установлено.");
        }
        catch (OperationCanceledException)
        {
            return new ServiceTestResult(false, $"Таймаут подключения (2.5 сек) к {firstIp}:443 ({domain}). IP: [{ipListStr}].");
        }
        catch (Exception ex)
        {
            return new ServiceTestResult(false, $"Ошибка подключения к {firstIp}:443 ({domain}): {ex.Message}. IP: [{ipListStr}].");
        }
    }

    private void DnsCard_Click(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((s as Border)?.Tag is not DnsServerInfo dns) return;

        var currentDnsList = GetCurrentDnsAddresses();
        if (dns.Primary != "dhcp" && currentDnsList.Contains(dns.Primary))
        {
            return;
        }

        ShowDnsConfirmDialog(dns);
    }

    private void ShowDnsConfirmDialog(DnsServerInfo dns)
    {
        if (_isDialogOpen)
        {
            return;
        }
        _isDialogOpen = true;
        bool isDhcp = dns.Primary == "dhcp";
        Color accentColor = isDhcp ? Color.FromRgb(0x22, 0xc5, 0x5e) : Color.FromRgb(0x3b, 0x82, 0xf6);
        string themeBtnStyle = isDhcp ? "GreenAccentBtn" : "AccentBtn";

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        var dialogCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 460,
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

        var cardContent = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };

        var rotateTransform = new RotateTransform();

        var iconBorder = new Border
        {
            Width = 56, Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(accentColor) { Opacity = 0.15 },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            RenderTransform = rotateTransform,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(isDhcp
                ? "M21,11c-0.6,0-1,0.4-1,1c0,2.9-1.5,5.5-4,6.9c-3.8,2.2-8.7,0.9-10.9-2.9C2.9,12.2,4.2,7.3,8,5.1c3.3-1.9,7.3-1.2,9.8,1.4h-2.4c-0.6,0-1,0.4-1,1s0.4,1,1,1h4.5c0.6,0,1-0.4,1-1V3c0-0.6-0.4-1-1-1s-1,0.4-1,1v1.8C17,3,14.6,2,12,2C6.5,2,2,6.5,2,12s4.5,10,10,10c5.5,0,10-4.5,10-10C22,11.4,21.6,11,21,11z"
                : "M 20,20 H 30 V 22 H 20 Z M 20,24 H 26 V 26 H 20 Z M30,17V16A13.9871,13.9871,0,1,0,19.23,29.625l-.46-1.9463A12.0419,12.0419,0,0,1,16,28c-.19,0-.375-.0186-.563-.0273A20.3044,20.3044,0,0,1,12.0259,17Zm-2.0415-2H21.9751A24.2838,24.2838,0,0,0,19.2014,4.4414,12.0228,12.0228,0,0,1,27.9585,15ZM16.563,4.0273A20.3044,20.3044,0,0,1,19.9741,15H12.0259A20.3044,20.3044,0,0,1,15.437,4.0273C15.625,4.0186,15.81,4,16,4S16.375,4.0186,16.563,4.0273Zm-3.7644.4141A24.2838,24.2838,0,0,0,10.0249,15H4.0415A12.0228,12.0228,0,0,1,12.7986,4.4414Zm0,23.1172A12.0228,12.0228,0,0,1,4.0415,17h5.9834A24.2838,24.2838,0,0,0,12.7986,27.5586Z"),
            Fill = new SolidColorBrush(accentColor),
            Width = 28, Height = 28,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = icon;
        cardContent.Children.Add(iconBorder);

        var titleText = new TextBlock
        {
            Text = dns.Name,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        cardContent.Children.Add(titleText);

        string desc = isDhcp
            ? "Настройки DNS будут автоматически определяться вашим роутером или провайдером."
            : $"Вы действительно хотите подключить DNS-сервер «{dns.Name}»?\n\n" +
              $"Основной: {dns.Primary}\n" +
              $"Дополнительный: {dns.Secondary}" +
              (string.IsNullOrEmpty(dns.DohTemplate) ? "" : $"\nШифрование DoH: {dns.DohTemplate}");

        var descText = new TextBlock
        {
            Text = desc,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 24)
        };
        cardContent.Children.Add(descText);

        var loaderPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var loaderText = new TextBlock
        {
            Text = isDhcp ? "Сбрасываем настройки DNS..." : $"Подключаем {dns.Name}...",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var progressBar = new System.Windows.Controls.ProgressBar
        {
            Height = 4,
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(accentColor),
            Background = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e)),
            Margin = new Thickness(20, 0, 20, 0)
        };
        loaderPanel.Children.Add(loaderText);
        loaderPanel.Children.Add(progressBar);
        cardContent.Children.Add(loaderPanel);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var applyBtn = new Button
        {
            Content = "Применить",
            Width = 140, Height = 40,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
        applyBtn.Style = (Style)FindResource(themeBtnStyle);

        var cancelBtn = new Button
        {
            Content = "Отмена",
            Width = 100, Height = 40,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            Cursor = Cursors.Hand
        };
        cancelBtn.Style = (Style)FindResource("OutlineBtn");

        bool isApplying = false;
        applyBtn.Click += async (s, e) =>
        {
            if (isApplying)
            {
                return;
            }
            isApplying = true;

            descText.Visibility = Visibility.Collapsed;
            buttonsPanel.Visibility = Visibility.Collapsed;
            loaderPanel.Visibility = Visibility.Visible;

            var rotationAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            rotateTransform.BeginAnimation(RotateTransform.AngleProperty, rotationAnim);

            bool ok = await SetDnsServerAsync(dns.Primary, dns.Secondary, dns.DohTemplate);

            bool completedHandled = false;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (s2, e2) =>
            {
                if (completedHandled) return;
                completedHandled = true;

                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                _isDialogOpen = false;

                if (ok)
                {
                    LoadDnsServers();
                    ShowNotification("Успех", $"DNS-сервер «{dns.Name}» успешно установлен! Для применения изменений полностью перезагрузите браузер.", false);
                }
            };
            overlay.BeginAnimation(OpacityProperty, fadeOut);
            dialogCard.BeginAnimation(OpacityProperty, fadeOut);
        };

        cancelBtn.Click += (s, e) =>
        {
            bool completedHandled = false;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s2, e2) =>
            {
                if (completedHandled) return;
                completedHandled = true;

                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                _isDialogOpen = false;
            };
            overlay.BeginAnimation(OpacityProperty, fadeOut);
            dialogCard.BeginAnimation(OpacityProperty, fadeOut);
        };

        buttonsPanel.Children.Add(applyBtn);
        buttonsPanel.Children.Add(cancelBtn);
        cardContent.Children.Add(buttonsPanel);

        dialogCard.Child = cardContent;
        MainGrid.Children.Add(dialogCard);

        overlay.Opacity = 0;
        dialogCard.Opacity = 0;
        dialogCard.RenderTransform = new ScaleTransform(0.95, 0.95);
        dialogCard.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        var scaleIn = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        overlay.BeginAnimation(OpacityProperty, fadeIn);
        dialogCard.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

        overlay.MouseLeftButtonDown += (s, e) =>
        {
            if (loaderPanel.Visibility == Visibility.Visible)
            {
                return;
            }
            bool completedHandled = false;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s2, e2) =>
            {
                if (completedHandled) return;
                completedHandled = true;

                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                _isDialogOpen = false;
            };
            overlay.BeginAnimation(OpacityProperty, fadeOut);
            dialogCard.BeginAnimation(OpacityProperty, fadeOut);
        };
    }

    private void ShowResetAllDnsConfirmDialog()
    {
        if (_isDialogOpen)
        {
            return;
        }
        _isDialogOpen = true;

        Color accentColor = Color.FromRgb(0xef, 0x44, 0x44);

        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetRowSpan(overlay, 3);
        MainGrid.Children.Add(overlay);

        var dialogCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)),
            BorderBrush = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = 460,
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

        var cardContent = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };

        var rotateTransform = new RotateTransform();

        var iconBorder = new Border
        {
            Width = 56, Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(accentColor) { Opacity = 0.15 },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
            RenderTransform = rotateTransform,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12,2 L22,20 L2,20 Z M12,9 L12,14 M12,16 L12,18"),
            Stroke = new SolidColorBrush(accentColor),
            StrokeThickness = 2.5,
            Width = 28, Height = 28,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = icon;
        cardContent.Children.Add(iconBorder);

        var titleText = new TextBlock
        {
            Text = "Сбросить настройки DNS?",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        cardContent.Children.Add(titleText);

        var descText = new TextBlock
        {
            Text = "Это действие вернет настройки всех сетевых адаптеров на автоматическое получение DNS (DHCP), очистит системный кэш DNS и удалит все настройки DoH.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 24)
        };
        cardContent.Children.Add(descText);

        var loaderPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var loaderText = new TextBlock
        {
            Text = "Сбрасываем все настройки DNS...",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var progressBar = new System.Windows.Controls.ProgressBar
        {
            Height = 4,
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(accentColor),
            Background = new SolidColorBrush(Color.FromRgb(0x2e, 0x2e, 0x2e)),
            Margin = new Thickness(20, 0, 20, 0)
        };
        loaderPanel.Children.Add(loaderText);
        loaderPanel.Children.Add(progressBar);
        cardContent.Children.Add(loaderPanel);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var resetBtn = new Button
        {
            Content = "Сбросить",
            Width = 140, Height = 40,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };

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
        resetBtn.Template = btnTemplate;

        var cancelBtn = new Button
        {
            Content = "Отмена",
            Width = 100, Height = 40,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            Cursor = Cursors.Hand
        };
        cancelBtn.Style = (Style)FindResource("OutlineBtn");

        bool isResetting = false;
        resetBtn.Click += async (s, e) =>
        {
            if (isResetting)
            {
                return;
            }
            isResetting = true;

            descText.Visibility = Visibility.Collapsed;
            buttonsPanel.Visibility = Visibility.Collapsed;
            loaderPanel.Visibility = Visibility.Visible;



            bool ok = await ApplyFullDnsResetAsync();

            bool completedHandled = false;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (s2, e2) =>
            {
                if (completedHandled) return;
                completedHandled = true;

                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                _isDialogOpen = false;

                if (ok)
                {
                    LoadDnsServers();
                    ShowFullDnsResetSuccessNotification();
                }
            };
            overlay.BeginAnimation(OpacityProperty, fadeOut);
            dialogCard.BeginAnimation(OpacityProperty, fadeOut);
        };

        cancelBtn.Click += (s, e) =>
        {
            bool completedHandled = false;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s2, e2) =>
            {
                if (completedHandled) return;
                completedHandled = true;

                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                _isDialogOpen = false;
            };
            overlay.BeginAnimation(OpacityProperty, fadeOut);
            dialogCard.BeginAnimation(OpacityProperty, fadeOut);
        };

        buttonsPanel.Children.Add(resetBtn);
        buttonsPanel.Children.Add(cancelBtn);
        cardContent.Children.Add(buttonsPanel);

        dialogCard.Child = cardContent;
        MainGrid.Children.Add(dialogCard);

        overlay.Opacity = 0;
        dialogCard.Opacity = 0;
        dialogCard.RenderTransform = new ScaleTransform(0.95, 0.95);
        dialogCard.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        var scaleIn = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        overlay.BeginAnimation(OpacityProperty, fadeIn);
        dialogCard.BeginAnimation(OpacityProperty, fadeIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        ((ScaleTransform)dialogCard.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

        overlay.MouseLeftButtonDown += (s, e) =>
        {
            if (loaderPanel.Visibility == Visibility.Visible)
            {
                return;
            }
            bool completedHandled = false;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s2, e2) =>
            {
                if (completedHandled) return;
                completedHandled = true;

                MainGrid.Children.Remove(overlay);
                MainGrid.Children.Remove(dialogCard);
                _isDialogOpen = false;
            };
            overlay.BeginAnimation(OpacityProperty, fadeOut);
            dialogCard.BeginAnimation(OpacityProperty, fadeOut);
        };
    }

    private async Task<bool> ApplyFullDnsResetAsync()
    {
        try
        {
            string psCommand =
                "Get-NetIPInterface -AddressFamily IPv4 | ForEach-Object { Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ResetServerAddresses -ErrorAction SilentlyContinue }; " +
                "Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\InterfaceSpecificParameters' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -Path \"$($_.PsPath)\\DohInterfaceSettings\" -Recurse -Force -ErrorAction SilentlyContinue }; " +
                "Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue | Remove-DnsClientDohServerAddress -ErrorAction SilentlyContinue; " +
                "Clear-DnsClientCache";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                var stdErrTask = process.StandardError.ReadToEndAsync();
                var stdOutTask = process.StandardOutput.ReadToEndAsync();

                await process.WaitForExitAsync();
                var stdErr = await stdErrTask;
                var stdOut = await stdOutTask;

                if (process.ExitCode != 0)
                {
                    Dispatcher.Invoke(() => ShowNotification("Ошибка сброса", $"Не удалось сбросить настройки DNS (код {process.ExitCode}): {stdErr}", true));
                    return false;
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => ShowNotification("Ошибка сброса", $"Исключение при сбросе DNS: {ex.Message}", true));
        }
        return false;
    }

    private void ShowFullDnsResetSuccessNotification()
    {
        ShowNotification("Настройки сброшены", "Все настройки DNS успешно возвращены к значениям по умолчанию (DHCP). Системный DNS-кэш очищен.", false);
    }

    private async Task<bool> SetDnsServerAsync(string primary, string secondary, string dohTemplate = "")
    {
        try
        {
            var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) &&
                    ni.GetIPProperties().GatewayAddresses.Any());

            if (activeInterface is null)
            {
                Dispatcher.Invoke(() => ShowNotification("Ошибка", "Не найден активный сетевой адаптер.", true));
                return false;
            }

            string interfaceName = activeInterface.Name;
            string interfaceId = activeInterface.Id;
            string psCommand;

            if (primary == "dhcp")
            {
                psCommand = $"Set-DnsClientServerAddress -InterfaceAlias '{interfaceName}' -ResetServerAddresses";
                string regPath = $"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\InterfaceSpecificParameters\\{interfaceId}";
                psCommand += $"; if (Test-Path '{regPath}\\DohInterfaceSettings') {{ Remove-Item -Path '{regPath}\\DohInterfaceSettings' -Recurse -Force }}" +
                             $"; Clear-DnsClientCache";
            }
            else
            {
                string addresses = string.IsNullOrEmpty(secondary) ? $"'{primary}'" : $"'{primary}', '{secondary}'";
                psCommand = $"Set-DnsClientServerAddress -InterfaceAlias '{interfaceName}' -ServerAddresses ({addresses})";

                if (!string.IsNullOrEmpty(dohTemplate))
                {
                    psCommand += $"; if (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue) {{" +
                                 $" Add-DnsClientDohServerAddress -ServerAddress '{primary}' -DohTemplate '{dohTemplate}' -AllowFallbackToUdp $False -AutoUpgrade $True -ErrorAction SilentlyContinue";
                    if (!string.IsNullOrEmpty(secondary))
                    {
                        psCommand += $"; Add-DnsClientDohServerAddress -ServerAddress '{secondary}' -DohTemplate '{dohTemplate}' -AllowFallbackToUdp $False -AutoUpgrade $True -ErrorAction SilentlyContinue";
                    }
                    psCommand += " }";

                    string regPath = $"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\InterfaceSpecificParameters\\{interfaceId}";
                    psCommand += $"; if (!(Test-Path '{regPath}\\DohInterfaceSettings\\Doh\\{primary}')) {{ New-Item -Path '{regPath}\\DohInterfaceSettings\\Doh\\{primary}' -Force | Out-Null }}";
                    psCommand += $"; New-ItemProperty -Path '{regPath}\\DohInterfaceSettings\\Doh\\{primary}' -Name 'DohFlags' -Value 1 -PropertyType QWord -Force | Out-Null";

                    if (!string.IsNullOrEmpty(secondary))
                    {
                        psCommand += $"; if (!(Test-Path '{regPath}\\DohInterfaceSettings\\Doh\\{secondary}')) {{ New-Item -Path '{regPath}\\DohInterfaceSettings\\Doh\\{secondary}' -Force | Out-Null }}";
                        psCommand += $"; New-ItemProperty -Path '{regPath}\\DohInterfaceSettings\\Doh\\{secondary}' -Name 'DohFlags' -Value 1 -PropertyType QWord -Force | Out-Null";
                    }
                }

                psCommand += $"; Clear-DnsClientCache";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process is not null)
            {
                var stdErrTask = process.StandardError.ReadToEndAsync();
                var stdOutTask = process.StandardOutput.ReadToEndAsync();

                await process.WaitForExitAsync();
                var stdErr = await stdErrTask;
                var stdOut = await stdOutTask;

                if (process.ExitCode != 0)
                {
                    Dispatcher.Invoke(() => ShowNotification("Ошибка", $"Ошибка при настройке DNS (код {process.ExitCode}): {stdErr}", true));
                    return false;
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => ShowNotification("Ошибка", $"Ошибка при настройке DNS: {ex.Message}", true));
        }
        return false;
    }

    private class DragAdorner : Adorner
    {
        private readonly UIElement _child;
        private double _leftOffset;
        private double _topOffset;

        public DragAdorner(UIElement adornedElement, UIElement child)
            : base(adornedElement)
        {
            _child = child;
            AddVisualChild(child);
            IsHitTestVisible = false;
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _child;

        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(constraint);
            return _child.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(_leftOffset, _topOffset, finalSize.Width, finalSize.Height));
            return finalSize;
        }

        public void UpdatePosition(double left, double top)
        {
            _leftOffset = left;
            _topOffset = top;
            InvalidateArrange();
        }
    }
}

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
