using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetFix.Models;
using NetFix.Services;

using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace NetFix.Views;

public partial class ZapretConfigWindow : Window
{
    private readonly string _zapretPath;
    private readonly bool _testMode;
    private ZapretConfigCache? _cache;
    private bool _isTesting = false;
    private Process? _testProcess = null;
    private DateTime _testStartTime;
    private int _totalConfigs = 0;
    
    public bool ConfigWasApplied { get; private set; } = false;

    public ZapretConfigWindow(string zapretPath, bool testMode)
    {
        InitializeComponent();
        _zapretPath = zapretPath;
        _testMode = testMode;
        Loaded += OnLoaded;
        Closing += OnClosing;
        
        // Р”РѕР±Р°РІРёС‚СЊ СЌС„С„РµРєС‚ РїСЂРё РЅР°РІРµРґРµРЅРёРё РґР»СЏ PrimaryBtn
        PrimaryBtn.MouseEnter += (s, e) =>
        {
            PrimaryBtn.Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2d));
            PrimaryBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4a, 0x4a, 0x4d));
            PrimaryBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x89));
        };
        PrimaryBtn.MouseLeave += (s, e) =>
        {
            PrimaryBtn.Background = Brushes.Transparent;
            PrimaryBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2d));
            PrimaryBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x69));
        };
    }

    private void AppendColoredLog(string text, Color color)
    {
        var paragraph = LogTextBox.Document.Blocks.LastBlock as Paragraph;
        if (paragraph == null)
        {
            paragraph = new Paragraph();
            LogTextBox.Document.Blocks.Add(paragraph);
        }

        // РџСЂРѕРІРµСЂСЏРµРј, СЏРІР»СЏРµС‚СЃСЏ Р»Рё СЌС‚Рѕ Р·Р°РіРѕР»РѕРІРєРѕРј
        bool isHeader = text.Contains("[HEADER]");
        if (isHeader)
        {
            text = text.Replace("[HEADER]", "").Replace("[/HEADER]", "");
        }

        var run = new Run(text + "\n")
        {
            Foreground = new SolidColorBrush(color),
            FontSize = isHeader ? 16 : 12,  // Р•С‰С‘ РєСЂСѓРїРЅРµРµ РґР»СЏ Р·Р°РіРѕР»РѕРІРєРѕРІ (Р±С‹Р»Рѕ 14)
            FontWeight = isHeader ? FontWeights.ExtraBold : FontWeights.Normal  // ExtraBold РІРјРµСЃС‚Рѕ Bold
        };
        paragraph.Inlines.Add(run);
        
        LogScrollViewer.ScrollToEnd();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // РћСЃС‚Р°РЅРѕРІРёС‚СЊ С‚РµСЃС‚РёСЂРѕРІР°РЅРёРµ РїСЂРё Р·Р°РєСЂС‹С‚РёРё РѕРєРЅР°
        _isTesting = false;
        
        if (_testProcess != null && !_testProcess.HasExited)
        {
            try
            {
                ForceKillProcessTree(_testProcess.Id);
                _testProcess.Dispose();
            }
            catch { }
        }
        
        // РЈР±РёС‚СЊ РІСЃРµ winws.exe Рё powershell.exe РїСЂРѕС†РµСЃСЃС‹ РўРћР›Р¬РљРћ РµСЃР»Рё СЌС‚Рѕ СЂРµР¶РёРј С‚РµСЃС‚РёСЂРѕРІР°РЅРёСЏ
        // Р’ СЂРµР¶РёРјРµ РІС‹Р±РѕСЂР° РєРѕРЅС„РёРіР° РќР• С‚СЂРѕРіР°РµРј Р·Р°РїСѓС‰РµРЅРЅС‹Рµ РїСЂРѕС†РµСЃСЃС‹
        if (_testMode)
        {
            try
            {
                var processes = Process.GetProcessesByName("winws");
                foreach (var proc in processes)
                {
                    try
                    {
                        ForceKillProcessTree(proc.Id);
                        proc.Dispose();
                    }
                    catch { }
                }
                
                // РўР°РєР¶Рµ СѓР±РёС‚СЊ Р»СЋР±С‹Рµ PowerShell РїСЂРѕС†РµСЃСЃС‹, Р·Р°РїСѓС‰РµРЅРЅС‹Рµ РѕС‚ РЅР°С€РµРіРѕ РїСЂРѕС†РµСЃСЃР°
                var powerShellProcs = Process.GetProcessesByName("powershell");
                foreach (var proc in powerShellProcs)
                {
                    try
                    {
                        proc.Kill(true);
                        proc.Dispose();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static void ForceKillProcessTree(int pid)
    {
        try
        {
            // РЈР±РёС‚СЊ РѕСЃРЅРѕРІРЅРѕР№ РїСЂРѕС†РµСЃСЃ
            var process = Process.GetProcessById(pid);
            process.Kill(true);
            process.WaitForExit(2000); // Р–РґРµРј 2 СЃРµРєСѓРЅРґС‹
        }
        catch (ArgumentException)
        {
            // РџСЂРѕС†РµСЃСЃ СѓР¶Рµ Р·Р°РІРµСЂС€С‘РЅ
        }
        catch (Exception)
        {
            // Р’ СЃР»СѓС‡Р°Рµ РѕС€РёР±РєРё РёСЃРїРѕР»СЊР·СѓРµРј РєРѕРјР°РЅРґСѓ taskkill РґР»СЏ РїРѕР»РЅРѕРіРѕ СѓРЅРёС‡С‚РѕР¶РµРЅРёСЏ
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /PID {pid} /T", // /T - СѓР±РёС‚СЊ РґРµСЂРµРІРѕ РїСЂРѕС†РµСЃСЃРѕРІ
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(2000);
            }
            catch { }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Р—Р°РіСЂСѓР·РёС‚СЊ РєСЌС€
        _cache = ZapretConfigService.LoadCache();

        if (_testMode)
        {
            // Р РµР¶РёРј С‚РµСЃС‚РёСЂРѕРІР°РЅРёСЏ - РїРѕРєР°Р·Р°С‚СЊ СЃРѕРѕР±С‰РµРЅРёРµ Рѕ РїРѕРґС‚РІРµСЂР¶РґРµРЅРёРё РІ С‚РµРєСѓС‰РµРј РѕРєРЅРµ
            StatusPanel.Visibility = Visibility.Visible;
            ProgressBarContainer.Visibility = Visibility.Collapsed;
            
            StatusIcon.Visibility = Visibility.Visible;
            StatusIcon.Data = (Geometry)FindResource("WarningIcon");
            StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
            
            StatusText.Text = "РџРµСЂРµРґ Р·Р°РїСѓСЃРєРѕРј - РІР°Р¶РЅР°СЏ РІРµС‰СЊ!\n\n" +
                             "РџСЂРёР»РѕР¶РµРЅРёРµ РјРѕР¶РµС‚ СЃР°РјРѕ РїСЂРѕС‚РµСЃС‚РёСЂРѕРІР°С‚СЊ РІСЃРµ РєРѕРЅС„РёРіРё Рё Р·Р°РїРѕРјРЅРёС‚СЊ Р»СѓС‡С€РёРµ. " +
                             "Р—Р°Р№РјС‘С‚ РјРёРЅСѓС‚ 10, Р·Р°С‚Рѕ РїРѕС‚РѕРј РЅРµ РїСЂРёРґС‘С‚СЃСЏ РІСЂСѓС‡РЅСѓСЋ РїРµСЂРµР±РёСЂР°С‚СЊ РёС… РєРѕРіРґР° С‡С‚Рѕ-С‚Рѕ РїРµСЂРµСЃС‚Р°С‘С‚ СЂР°Р±РѕС‚Р°С‚СЊ.\n\n" +
                             "Рђ Р»РѕРјР°РµС‚СЃСЏ, РєСЃС‚Р°С‚Рё, РїРѕ-СЂР°Р·РЅРѕРјСѓ. РРЅРѕРіРґР° РєРѕРЅС„РёРі РІСЂРѕРґРµ СЂР°Р±РѕС‚Р°РµС‚, Discord РѕС‚РєСЂС‹Р»СЃСЏ, РІСЃС‘ С…РѕСЂРѕС€Рѕ. " +
                             "РќРѕ СЃС‚РѕРёС‚ Р·Р°Р№С‚Рё РЅР° РєР°РєРѕР№-РЅРёР±СѓРґСЊ СЃР°Р№С‚, Рё РѕРЅ Р»РёР±Рѕ РІРѕРѕР±С‰Рµ РЅРµ Р·Р°РіСЂСѓР¶Р°РµС‚СЃСЏ, " +
                             "Р»РёР±Рѕ РѕС‚РєСЂС‹РІР°РµС‚СЃСЏ СЃР»РѕРјР°РЅРЅС‹Рј, Р±РµР· СЃС‚РёР»РµР№, РІСЃС‘ СЃСЉРµС…Р°Р»Рѕ, РєРЅРѕРїРєРё РЅРµ СЂР°Р±РѕС‚Р°СЋС‚. " +
                             "Р­С‚Рѕ РЅРµ Р±СЂР°СѓР·РµСЂ РІРёРЅРѕРІР°С‚, РїСЂРѕСЃС‚Рѕ РєРѕРЅС„РёРі РѕР±СЂР°Р±Р°С‚С‹РІР°РµС‚ С‚СЂР°С„РёРє РЅРµ С‚Р°Рє, РєР°Рє РЅСѓР¶РЅРѕ, Рё С‡Р°СЃС‚СЊ СЃР°Р№С‚РѕРІ Р»РѕРјР°РµС‚СЃСЏ.\n\n" +
                             "РРјРµРЅРЅРѕ РїРѕСЌС‚РѕРјСѓ РІР°Р¶РЅРѕ РёРјРµС‚СЊ РЅРµСЃРєРѕР»СЊРєРѕ РїСЂРѕРІРµСЂРµРЅРЅС‹С… РєРѕРЅС„РёРіРѕРІ РїРѕРґ СЂСѓРєРѕР№, " +
                             "РµСЃР»Рё РѕРґРёРЅ РїРµСЂРµСЃС‚Р°Р» СЂР°Р±РѕС‚Р°С‚СЊ РїСЂР°РІРёР»СЊРЅРѕ, РїРµСЂРµРєР»СЋС‡РёР»РёСЃСЊ РЅР° РґСЂСѓРіРѕР№ Рё РІСЃС‘.\n\n" +
                             "РџСЂРѕР№РґРёС‚Рµ С‚РµСЃС‚ РѕРґРёРЅ СЂР°Р·, Рё РїСЂРёР»РѕР¶РµРЅРёРµ СЃР°РјРѕ СЂР°Р·Р±РµСЂС‘С‚СЃСЏ С‡С‚Рѕ Рє С‡РµРјСѓ. Р—Р°РїСѓСЃРєР°РµРј?";
            
            SecondaryBtn.Content = "Р”Р°, РЅР°С‡Р°С‚СЊ";
            PrimaryBtn.Content = "РќРµС‚, РІС‹Р№С‚Рё";
            PrimaryBtn.Visibility = Visibility.Visible;
        }
        else
        {
            // Р РµР¶РёРј РІС‹Р±РѕСЂР° РєРѕРЅС„РёРіР°
            if (_cache == null || !_cache.HasAnyConfigs)
            {
                // РќРµС‚ РєСЌС€Р° - РїРѕРєР°Р·Р°С‚СЊ РїСЂРµРґСѓРїСЂРµР¶РґРµРЅРёРµ
                ShowWarningNoCache();
            }
            else
            {
                StopIndeterminateAnimation();
                ShowConfigList();
            }
        }
    }

    private void ShowWarningNoCache()
    {
        StopIndeterminateAnimation();
        StatusPanel.Visibility = Visibility.Visible;
        ProgressBarContainer.Visibility = Visibility.Collapsed;
        
        StatusIcon.Visibility = Visibility.Visible;
        StatusIcon.Data = (Geometry)FindResource("WarningIcon");
        StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
        
        StatusText.Text = "РћР‘РЇР—РђРўР•Р›Р¬РќРћ РџР РћР™Р”РРўР• РїРѕР»РЅС‹Р№ С‚РµСЃС‚ РєРѕРЅС„РёРіРѕРІ!\n\n" +
                         "Р­С‚Рѕ РїРѕРјРѕР¶РµС‚ РІР°Рј РІ Р±СѓРґСѓС‰РµРј Рё СЃСЌРєРѕРЅРѕРјРёС‚ РєСѓС‡Сѓ РІСЂРµРјРµРЅРё! " +
                         "РџСЂРёР»РѕР¶РµРЅРёРµ РЅР°Р№РґС‘С‚ РІСЃРµ СЂР°Р±РѕС‡РёРµ РєРѕРЅС„РёРіРё Рё РІС‹Р±РµСЂРµС‚ Р»СѓС‡С€РёР№ РґР»СЏ РІР°С€РµР№ СЃРµС‚Рё.";
        
        SecondaryBtn.Content = "Р—Р°РєСЂС‹С‚СЊ";
        PrimaryBtn.Content = "РџСЂРѕР№С‚Рё С‚РµСЃС‚";
        PrimaryBtn.Visibility = Visibility.Visible;
    }

    private async Task StartTestingAsync()
    {
        _isTesting = true;
        SecondaryBtn.Content = "РћС‚РјРµРЅР°";
        PrimaryBtn.Visibility = Visibility.Collapsed;

        // РћСЃС‚Р°РЅРѕРІРёС‚СЊ Рё СѓРґР°Р»РёС‚СЊ СЃРµСЂРІРёСЃ Zapret РµСЃР»Рё СѓСЃС‚Р°РЅРѕРІР»РµРЅ
        StatusText.Text = "РџРѕРґРіРѕС‚РѕРІРєР° Рє С‚РµСЃС‚РёСЂРѕРІР°РЅРёСЋ...";
        
        var st = DiagnosticsEngine.CheckAppStatus();
        if (st.ZapretRunning)
        {
            StatusText.Text = "РћСЃС‚Р°РЅРѕРІРєР° Zapret...";
            foreach (var p in Process.GetProcessesByName("winws"))
                try { p.Kill(); } catch { }
            foreach (var p in Process.GetProcessesByName("winws.exe"))
                try { p.Kill(); } catch { }

            await Task.Delay(1000);
        }

        // РЈРґР°Р»РёС‚СЊ СЃРµСЂРІРёСЃ Zapret РµСЃР»Рё СѓСЃС‚Р°РЅРѕРІР»РµРЅ
        try
        {
            StatusText.Text = "РЈРґР°Р»РµРЅРёРµ СЃРµСЂРІРёСЃР° Zapret...";
            
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "query zapret",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var checkProcess = Process.Start(psi);
            if (checkProcess != null)
            {
                await checkProcess.WaitForExitAsync();
                
                // Р•СЃР»Рё СЃРµСЂРІРёСЃ СЃСѓС‰РµСЃС‚РІСѓРµС‚ (РєРѕРґ РІРѕР·РІСЂР°С‚Р° 0), СѓРґР°Р»РёС‚СЊ РµРіРѕ
                if (checkProcess.ExitCode == 0)
                {
                    var stopPsi = new ProcessStartInfo
                    {
                        FileName = "net.exe",
                        Arguments = "stop zapret",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var stopProcess = Process.Start(stopPsi);
                    if (stopProcess != null)
                        await stopProcess.WaitForExitAsync();
                    
                    await Task.Delay(500);
                    
                    var deletePsi = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = "delete zapret",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var deleteProcess = Process.Start(deletePsi);
                    if (deleteProcess != null)
                        await deleteProcess.WaitForExitAsync();
                    
                    await Task.Delay(500);
                }
            }
        }
        catch
        {
            // РРіРЅРѕСЂРёСЂСѓРµРј РѕС€РёР±РєРё СѓРґР°Р»РµРЅРёСЏ СЃРµСЂРІРёСЃР°
        }

        try
        {
            StatusText.Text = "Р—Р°РїСѓСЃРє РїРѕР»РЅРѕРіРѕ С‚РµСЃС‚РёСЂРѕРІР°РЅРёСЏ РєРѕРЅС„РёРіРѕРІ...\n\n" +
                             "рџ’Ў РЎРѕРІРµС‚СѓРµРј РІР°Рј РїРѕРґРѕР¶РґР°С‚СЊ 10 РјРёРЅСѓС‚РѕРє РЅР° РїРѕР»РЅРѕРµ СЃРєР°РЅРёСЂРѕРІР°РЅРёРµ.\n" +
                             "Р’ РґР°Р»СЊРЅРµР№С€РµРј СЌС‚Рѕ СЃСЌРєРѕРЅРѕРјРёС‚ РІР°Рј РєСѓС‡Сѓ РІСЂРµРјРµРЅРё Рё РЅРµСЂРІРѕРІ!\n\n" +
                             "РџСЂРёР»РѕР¶РµРЅРёРµ РЅР°Р№РґС‘С‚ РІСЃРµ РёРґРµР°Р»СЊРЅС‹Рµ РєРѕРЅС„РёРіРё (12/12 С‚РµСЃС‚РѕРІ) Рё РІС‹Р±РµСЂРµС‚ Р»СѓС‡С€РёР№.";
            
            await Task.Delay(3000);
            
            // РџРѕРєР°Р·Р°С‚СЊ РїСЂРѕРіСЂРµСЃСЃ-Р±Р°СЂ, Р»РѕРі Рё СЃРєСЂС‹С‚СЊ StatusPanel
            StatusPanel.Visibility = Visibility.Collapsed;
            ProgressBarContainer.Visibility = Visibility.Visible;
            ProgressText.Visibility = Visibility.Visible;
            TimeRemainingText.Visibility = Visibility.Visible;
            ProgressText.Text = "РўРµСЃС‚РёСЂРѕРІР°РЅРёРµ РєРѕРЅС„РёРіРѕРІ: 0%";
            TimeRemainingText.Text = "РћСЃС‚Р°Р»РѕСЃСЊ: ~10 РјРёРЅ";
            LogContainer.Visibility = Visibility.Visible;
            
            // Р—Р°РїРѕРјРЅРёС‚СЊ РІСЂРµРјСЏ РЅР°С‡Р°Р»Р°
            _testStartTime = DateTime.Now;
            
            // РћС‡РёСЃС‚РёС‚СЊ Р»РѕРі Рё РґРѕР±Р°РІРёС‚СЊ РЅР°С‡Р°Р»СЊРЅРѕРµ СЃРѕРѕР±С‰РµРЅРёРµ
            LogTextBox.Document.Blocks.Clear();
            AppendColoredLog("рџ’Ў РЎРѕРІРµС‚СѓРµРј РІР°Рј РїРѕРґРѕР¶РґР°С‚СЊ 10 РјРёРЅСѓС‚РѕРє РЅР° РїРѕР»РЅРѕРµ СЃРєР°РЅРёСЂРѕРІР°РЅРёРµ.", Color.FromRgb(0xf0, 0xf0, 0xf0));
            AppendColoredLog("Р’ РґР°Р»СЊРЅРµР№С€РµРј СЌС‚Рѕ СЃСЌРєРѕРЅРѕРјРёС‚ РІР°Рј РєСѓС‡Сѓ РІСЂРµРјРµРЅРё Рё РЅРµСЂРІРѕРІ!\n", Color.FromRgb(0xf0, 0xf0, 0xf0));
            AppendColoredLog("Р—Р°РїСѓСЃРє С‚РµСЃС‚РёСЂРѕРІР°РЅРёСЏ...\n", Color.FromRgb(0x88, 0x88, 0x88));
            
            var (configs, testProcess) = await ZapretConfigService.TestAllConfigsAsync(
                _zapretPath,
                status => Dispatcher.Invoke(() => 
                {
                    // Р”РѕР±Р°РІР»СЏРµРј РІ Р»РѕРі СЃ С†РІРµС‚РѕРј РІ Р·Р°РІРёСЃРёРјРѕСЃС‚Рё РѕС‚ СЃРѕРґРµСЂР¶РёРјРѕРіРѕ
                    Color logColor;
                    if (status.Contains("вќЊ") || status.Contains("РќР• Р РђР‘РћРўРђР•Рў") || status.Contains("РќР•Р РђР‘РћР§РР™"))
                        logColor = Color.FromRgb(0xef, 0x44, 0x44); // РљСЂР°СЃРЅС‹Р№
                    else if (status.Contains("вњ…") || status.Contains("Р РђР‘РћРўРђР•Рў") || status.Contains("Р РђР‘РћР§РР™"))
                        logColor = Color.FromRgb(0x22, 0xc5, 0x5e); // Р—РµР»С‘РЅС‹Р№
                    else if (status.Contains("рџ”„") || status.Contains("РўРµСЃС‚РёСЂСѓСЋ"))
                        logColor = Color.FromRgb(0x3b, 0x82, 0xf6); // РЎРёРЅРёР№
                    else if (status.Contains("вљ пёЏ") || status.Contains("Р§РђРЎРўРР§РќРћ"))
                        logColor = Color.FromRgb(0xea, 0xb3, 0x08); // Р–С‘Р»С‚С‹Р№
                    else
                        logColor = Color.FromRgb(0xf0, 0xf0, 0xf0); // Р‘РµР»С‹Р№ РїРѕ СѓРјРѕР»С‡Р°РЅРёСЋ
                    
                    AppendColoredLog(status, logColor);
                }),
                (current, total) => Dispatcher.Invoke(() => 
                {
                    // РЎРѕС…СЂР°РЅРёС‚СЊ РѕР±С‰РµРµ РєРѕР»РёС‡РµСЃС‚РІРѕ РєРѕРЅС„РёРіРѕРІ
                    if (_totalConfigs == 0)
                        _totalConfigs = total;
                    
                    // РћР±РЅРѕРІР»СЏРµРј РїСЂРѕРіСЂРµСЃСЃ-Р±Р°СЂ
                    var percentage = (current * 100 / total);
                    var progressWidth = (ProgressBarContainer.ActualWidth * current / total);
                    ProgressBar.Width = progressWidth;
                    ProgressText.Text = $"РўРµСЃС‚РёСЂРѕРІР°РЅРёРµ РєРѕРЅС„РёРіРѕРІ: {current}/{total} ({percentage}%)";
                    
                    // Р Р°СЃСЃС‡РёС‚Р°С‚СЊ РѕСЃС‚Р°РІС€РµРµСЃСЏ РІСЂРµРјСЏ
                    if (current > 0)
                    {
                        var elapsed = DateTime.Now - _testStartTime;
                        var avgTimePerConfig = elapsed.TotalSeconds / current;
                        var remainingConfigs = total - current;
                        var estimatedSecondsRemaining = avgTimePerConfig * remainingConfigs;
                        
                        if (estimatedSecondsRemaining < 60)
                            TimeRemainingText.Text = $"РћСЃС‚Р°Р»РѕСЃСЊ: ~{(int)estimatedSecondsRemaining} СЃРµРє";
                        else
                            TimeRemainingText.Text = $"РћСЃС‚Р°Р»РѕСЃСЊ: ~{(int)(estimatedSecondsRemaining / 60)} РјРёРЅ";
                    }
                })
            );
            
            _testProcess = testProcess;

            if (!_isTesting) return; // РћС‚РјРµРЅРµРЅРѕ

            var idealConfigs = configs.Where(c => c.IsValid).OrderBy(c => c.AveragePing).ToList();
            var partialConfigs = configs.Where(c => c.IsPartiallyUsable).OrderBy(c => c.AveragePing).ToList();

            if (idealConfigs.Count > 0)
            {
                // РЎРѕС…СЂР°РЅРёС‚СЊ СЂРµР·СѓР»СЊС‚Р°С‚С‹
                _cache = new ZapretConfigCache
                {
                    LastTested = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    CurrentConfig = idealConfigs[0].Name,
                    ValidConfigs = idealConfigs,
                    PartialConfigs = partialConfigs
                };
                ZapretConfigService.SaveCache(_cache);

                // РЎРєСЂС‹С‚СЊ РїСЂРѕРіСЂРµСЃСЃ-Р±Р°СЂ Рё Р»РѕРі, РїРѕРєР°Р·Р°С‚СЊ РїРѕР·РґСЂР°РІР»РµРЅРёРµ
                ProgressBarContainer.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                TimeRemainingText.Visibility = Visibility.Collapsed;
                LogContainer.Visibility = Visibility.Collapsed;
                StopIndeterminateAnimation();
                StatusPanel.Visibility = Visibility.Visible;
                StatusIcon.Visibility = Visibility.Visible;
                StatusIcon.Data = (Geometry)FindResource("CheckmarkIcon");
                StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                
                var topConfigs = string.Join("\n", idealConfigs.Take(5).Select((c, i) => 
                    $"{i + 1}. {c.Name} (РїРёРЅРі: {c.AveragePing} РјСЃ, С‚РµСЃС‚РѕРІ: {c.SuccessCount}/12)"));
                
                StatusText.Text = $"рџЋ‰ РџРѕР·РґСЂР°РІР»СЏСЋ СЃ РїРѕР»РЅС‹Рј С‚РµСЃС‚РёСЂРѕРІР°РЅРёРµРј!\n\n" +
                                 $"РќР°Р№РґРµРЅРѕ {idealConfigs.Count} РёРґРµР°Р»СЊРЅС‹С… РєРѕРЅС„РёРіРѕРІ.\n" +
                                 $"Р’СЃРµ РѕРЅРё РїСЂРѕС€Р»Рё 12/12 С‚РµСЃС‚РѕРІ Р±РµР· РѕС€РёР±РѕРє!\n\n" +
                                 $"Р’Р°С€ С‚РѕРї РєРѕРЅС„РёРіРѕРІ РЅР° СЃР»РµРґСѓСЋС‰РёРµ СЂР°Р·С‹:\n\n{topConfigs}";

                // РћСЃС‚Р°РІРёС‚СЊ СЌРєСЂР°РЅ РїРѕР·РґСЂР°РІР»РµРЅРёСЏ, РЅРµ СЃРєСЂС‹РІР°С‚СЊ Р°РІС‚РѕРјР°С‚РёС‡РµСЃРєРё
                // РџРѕР»СЊР·РѕРІР°С‚РµР»СЊ СЃР°Рј РЅР°Р¶РјРµС‚ РЅР° РєРЅРѕРїРєСѓ С‡С‚РѕР±С‹ РїРµСЂРµР№С‚Рё Рє РІС‹Р±РѕСЂСѓ РєРѕРЅС„РёРіРѕРІ
                PrimaryBtn.Visibility = Visibility.Visible;
                PrimaryBtn.Content = "Р’С‹Р±СЂР°С‚СЊ РєРѕРЅС„РёРі";
            }
            else
            {
                // РЎРєСЂС‹С‚СЊ РїСЂРѕРіСЂРµСЃСЃ-Р±Р°СЂ Рё Р»РѕРі, РїРѕРєР°Р·Р°С‚СЊ РѕС€РёР±РєСѓ
                ProgressBarContainer.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                TimeRemainingText.Visibility = Visibility.Collapsed;
                LogContainer.Visibility = Visibility.Collapsed;
                StatusPanel.Visibility = Visibility.Visible;
                StatusText.Text = "РќРµ РЅР°Р№РґРµРЅРѕ СЂР°Р±РѕС‡РёС… РєРѕРЅС„РёРіРѕРІ СЃ 12/12 СѓСЃРїРµС€РЅС‹РјРё С‚РµСЃС‚Р°РјРё.\n\n" +
                                 "Р’РѕР·РјРѕР¶РЅРѕ, РІР°С€Р° СЃРµС‚СЊ РёРјРµРµС‚ РѕСЃРѕР±С‹Рµ РѕРіСЂР°РЅРёС‡РµРЅРёСЏ. РџРѕРїСЂРѕР±СѓР№С‚Рµ РїРѕРІС‚РѕСЂРёС‚СЊ С‚РµСЃС‚ РїРѕР·Р¶Рµ.";
                StopIndeterminateAnimation();
                StatusIcon.Visibility = Visibility.Visible;
                StatusIcon.Data = (Geometry)FindResource("WarningIcon");
                StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                
                SecondaryBtn.Content = "Р—Р°РєСЂС‹С‚СЊ";
                PrimaryBtn.Content = "РџРѕРІС‚РѕСЂРёС‚СЊ С‚РµСЃС‚";
                PrimaryBtn.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            // РЎРєСЂС‹С‚СЊ РїСЂРѕРіСЂРµСЃСЃ-Р±Р°СЂ Рё Р»РѕРі, РїРѕРєР°Р·Р°С‚СЊ РѕС€РёР±РєСѓ
            ProgressBarContainer.Visibility = Visibility.Collapsed;
            ProgressText.Visibility = Visibility.Collapsed;
            TimeRemainingText.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = $"РћС€РёР±РєР°: {ex.Message}";
            StopIndeterminateAnimation();
            StatusIcon.Visibility = Visibility.Visible;
            StatusIcon.Data = (Geometry)FindResource("WarningIcon");
            StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            
            SecondaryBtn.Content = "Р—Р°РєСЂС‹С‚СЊ";
            PrimaryBtn.Content = "РџРѕРІС‚РѕСЂРёС‚СЊ С‚РµСЃС‚";
            PrimaryBtn.Visibility = Visibility.Visible;
        }

        _isTesting = false;
    }

    private async void SecondaryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SecondaryBtn.Content?.ToString() == "РќР°Р·Р°Рґ Рє СЃРїРёСЃРєСѓ")
        {
            LogContainer.Visibility = Visibility.Collapsed;
            ConfigListScroll.Visibility = Visibility.Visible;
            PrimaryBtn.Visibility = Visibility.Collapsed;
            PrimaryBtn.Content = "РџСЂРѕРІРµСЂРёС‚СЊ РєРѕРЅС„РёРі";
            SecondaryBtn.Content = "Р—Р°РєСЂС‹С‚СЊ";
            
            // Р’РѕСЃСЃС‚Р°РЅРѕРІРёС‚СЊ РѕР±СЂР°Р±РѕС‚С‡РёРє СЃРѕР±С‹С‚РёСЏ РґР»СЏ PrimaryBtn
            PrimaryBtn.Click -= PrimaryBtn_Click;
            PrimaryBtn.Click += PrimaryBtn_Click;
            return;
        }
        else if (SecondaryBtn.Content?.ToString() == "Р”Р°, РЅР°С‡Р°С‚СЊ")
        {
            await StartTestingAsync();
            return;
        }
        else if (SecondaryBtn.Content?.ToString() == "РџСЂРёРјРµРЅРёС‚СЊ")
        {
            // РџСЂРёРјРµРЅРёС‚СЊ РІС‹Р±СЂР°РЅРЅС‹Р№ РєРѕРЅС„РёРі Рё Р’РЎР•Р“Р”Рђ Р·Р°РїСѓСЃС‚РёС‚СЊ СЃРµСЂРІРёСЃ
            if (_cache != null && !string.IsNullOrEmpty(_cache.CurrentConfig))
            {
                Console.WriteLine($"[ZapretConfigWindow] Applying config: {_cache.CurrentConfig}");
                Console.WriteLine($"[ZapretConfigWindow] Zapret path: {_zapretPath}");
                
                // РџРѕРєР°Р·Р°С‚СЊ РїСЂРѕРіСЂРµСЃСЃ-Р±Р°СЂ
                ApplyConfigProgress.Visibility = Visibility.Visible;
                
                SecondaryBtn.IsEnabled = false;
                PrimaryBtn.IsEnabled = false;
                var originalContent = SecondaryBtn.Content;
                SecondaryBtn.Content = "РџСЂРёРјРµРЅРµРЅРёРµ...";

                // РџСЂРёРјРµРЅСЏРµРј РєРѕРЅС„РёРі (ApplyConfigAsync Р°РІС‚РѕРјР°С‚РёС‡РµСЃРєРё РѕСЃС‚Р°РЅР°РІР»РёРІР°РµС‚ СЃС‚Р°СЂС‹Р№ СЃРµСЂРІРёСЃ РµСЃР»Рё Р·Р°РїСѓС‰РµРЅ Рё Р·Р°РїСѓСЃРєР°РµС‚ РЅРѕРІС‹Р№)
                bool success = await ZapretConfigService.ApplyConfigAsync(_zapretPath, _cache.CurrentConfig);
                
                Console.WriteLine($"[ZapretConfigWindow] ApplyConfigAsync result: {success}");

                SecondaryBtn.IsEnabled = true;
                PrimaryBtn.IsEnabled = true;
                SecondaryBtn.Content = originalContent;
                
                // РЎРєСЂС‹С‚СЊ РїСЂРѕРіСЂРµСЃСЃ-Р±Р°СЂ
                ApplyConfigProgress.Visibility = Visibility.Collapsed;

                if (success)
                {
                    // РЈСЃРїРµС€РЅРѕ РїСЂРёРјРµРЅРёР»Рё РєРѕРЅС„РёРі Рё Р·Р°РїСѓСЃС‚РёР»Рё СЃРµСЂРІРёСЃ
                    ConfigWasApplied = true;
                    // РџРѕРґРѕР¶РґРµРј РЅРµРјРЅРѕРіРѕ С‡С‚РѕР±С‹ СЃРµСЂРІРёСЃ СѓСЃРїРµР» Р·Р°РїСѓСЃС‚РёС‚СЊСЃСЏ
                    await Task.Delay(1000);
                    Close();
                }
                else
                {
                    // РџРѕРєР°Р·Р°С‚СЊ РѕС€РёР±РєСѓ
                    StatusPanel.Visibility = Visibility.Visible;
                    ConfigListScroll.Visibility = Visibility.Collapsed;
                    StatusIcon.Visibility = Visibility.Visible;
                    StatusIcon.Data = (Geometry)FindResource("WarningIcon");
                    StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                    StatusText.Text = "РќРµ СѓРґР°Р»РѕСЃСЊ РїСЂРёРјРµРЅРёС‚СЊ РєРѕРЅС„РёРі. РџСЂРѕРІРµСЂСЊС‚Рµ:\n1. Р—Р°РїСѓС‰РµРЅРѕ Р»Рё РїСЂРёР»РѕР¶РµРЅРёРµ РѕС‚ Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР°\n2. РџСЂР°РІРёР»СЊРЅРѕ Р»Рё СѓРєР°Р·Р°РЅ РїСѓС‚СЊ Рє Zapret\n3. Р›РѕРіРё РІ РєРѕРЅСЃРѕР»Рё РґР»СЏ РґРµС‚Р°Р»РµР№";
                    SecondaryBtn.Content = "Р—Р°РєСЂС‹С‚СЊ";
                    PrimaryBtn.Visibility = Visibility.Collapsed;
                }
            }
            return;
        }
        else
        {
            _isTesting = false;
            
            // РЈР±РёС‚СЊ РїСЂРѕС†РµСЃСЃ С‚РµСЃС‚РёСЂРѕРІР°РЅРёСЏ
            if (_testProcess != null && !_testProcess.HasExited)
            {
                try
                {
                    ForceKillProcessTree(_testProcess.Id);
                    _testProcess.Dispose();
                }
                catch { }
            }
            
            // РЈР±РёС‚СЊ РІСЃРµ winws.exe Рё powershell.exe РїСЂРѕС†РµСЃСЃС‹
            try
            {
                var processes = Process.GetProcessesByName("winws");
                foreach (var proc in processes)
                {
                    try
                    {
                        ForceKillProcessTree(proc.Id);
                        proc.Dispose();
                    }
                    catch { }
                }
                
                // РўР°РєР¶Рµ СѓР±РёС‚СЊ Р»СЋР±С‹Рµ PowerShell РїСЂРѕС†РµСЃСЃС‹
                var powerShellProcs = Process.GetProcessesByName("powershell");
                foreach (var proc in powerShellProcs)
                {
                    try
                    {
                        proc.Kill(true);
                        proc.Dispose();
                    }
                    catch { }
                }
            }
            catch { }
            
            Close();
        }
    }

    private async void PrimaryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PrimaryBtn.Content?.ToString() == "РќРµС‚, РІС‹Р№С‚Рё")
        {
            Close();
            return;
        }

        // Р•СЃР»Рё РїРѕРєР°Р·Р°РЅ СЃРїРёСЃРѕРє РєРѕРЅС„РёРіРѕРІ Рё РµСЃС‚СЊ РІС‹Р±СЂР°РЅРЅС‹Р№ РєРѕРЅС„РёРі - С‚РµСЃС‚РёСЂРѕРІР°С‚СЊ С‚РѕР»СЊРєРѕ РµРіРѕ
        if (ConfigListScroll.Visibility == Visibility.Visible && _cache != null && !string.IsNullOrEmpty(_cache.CurrentConfig))
        {
            await TestCurrentConfigAsync();
        }
        else
        {
            // Р•СЃР»Рё РЅР° СЌРєСЂР°РЅРµ РїРѕР·РґСЂР°РІР»РµРЅРёСЏ, РїРµСЂРµР№С‚Рё Рє СЃРїРёСЃРєСѓ РєРѕРЅС„РёРіРѕРІ
            if (StatusPanel.Visibility == Visibility.Visible && PrimaryBtn.Content.ToString() == "Р’С‹Р±СЂР°С‚СЊ РєРѕРЅС„РёРі")
            {
                ShowConfigList();
            }
            else
            {
                // Р—Р°РїСѓСЃС‚РёС‚СЊ РїРѕР»РЅРѕРµ С‚РµСЃС‚РёСЂРѕРІР°РЅРёРµ
                await StartTestingAsync();
            }
        }
    }

    private async Task TestCurrentConfigAsync()
    {
        if (_cache == null || string.IsNullOrEmpty(_cache.CurrentConfig)) return;

        // РЎРєСЂС‹С‚СЊ СЃРїРёСЃРѕРє РєРѕРЅС„РёРіРѕРІ Рё РїРѕРєР°Р·Р°С‚СЊ Р»РѕРі
        ConfigListScroll.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        ProgressBarContainer.Visibility = Visibility.Collapsed;
        LogContainer.Visibility = Visibility.Visible;
        
        PrimaryBtn.Visibility = Visibility.Collapsed;
        SecondaryBtn.Content = "РћС‚РјРµРЅР°";

        // РћС‡РёСЃС‚РёС‚СЊ Р»РѕРі
        LogTextBox.Document.Blocks.Clear();
        AppendColoredLog($"рџ”„ РўРµСЃС‚РёСЂСѓСЋ РєРѕРЅС„РёРі: {_cache.CurrentConfig}\n", Color.FromRgb(0x3b, 0x82, 0xf6));

        var (isWorking, message) = await ZapretConfigService.TestSingleConfigAsync(
            _zapretPath,
            _cache.CurrentConfig,
            status => Dispatcher.Invoke(() => 
            {
                Color logColor;
                if (status.Contains("вњ…") || status.Contains("СЂР°Р±РѕС‚Р°РµС‚") || status.Contains("РґРѕСЃС‚СѓРїРµРЅ"))
                    logColor = Color.FromRgb(0x22, 0xc5, 0x5e); // Р—РµР»С‘РЅС‹Р№
                else if (status.Contains("вќЊ") || status.Contains("РЅРµ СЂР°Р±РѕС‚Р°РµС‚") || status.Contains("РЅРµРґРѕСЃС‚СѓРїРµРЅ"))
                    logColor = Color.FromRgb(0xef, 0x44, 0x44); // РљСЂР°СЃРЅС‹Р№
                else if (status.Contains("рџ”„") || status.Contains("РўРµСЃС‚РёСЂСѓСЋ"))
                    logColor = Color.FromRgb(0x3b, 0x82, 0xf6); // РЎРёРЅРёР№
                else
                    logColor = Color.FromRgb(0xf0, 0xf0, 0xf0); // Р‘РµР»С‹Р№
                
                AppendColoredLog(status, logColor);
            })
        );

        // РџРѕРєР°Р·Р°С‚СЊ СЂРµР·СѓР»СЊС‚Р°С‚
        if (isWorking)
        {
            AppendColoredLog($"\nвњ… {message}", Color.FromRgb(0x22, 0xc5, 0x5e));
        }
        else
        {
            AppendColoredLog($"\nвќЊ {message}", Color.FromRgb(0xef, 0x44, 0x44));
        }
        
        // РћСЃС‚Р°РІРёС‚СЊ Р»РѕРі РѕС‚РєСЂС‹С‚С‹Рј, РїРѕРєР°Р·Р°С‚СЊ РєРЅРѕРїРєСѓ РґР»СЏ РІРѕР·РІСЂР°С‚Р° Рє СЃРїРёСЃРєСѓ
        PrimaryBtn.Visibility = Visibility.Visible;
        PrimaryBtn.Content = "Р—Р°РєСЂС‹С‚СЊ";
        PrimaryBtn.Click -= PrimaryBtn_Click;
        PrimaryBtn.Click += (s, e) => Close();
        SecondaryBtn.Content = "РќР°Р·Р°Рґ Рє СЃРїРёСЃРєСѓ";
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowConfigList()
    {
        if (_cache == null || !_cache.HasAnyConfigs) return;
        StopIndeterminateAnimation();
        StatusPanel.Visibility = Visibility.Collapsed;
        ProgressBarContainer.Visibility = Visibility.Collapsed;
        ConfigListScroll.Visibility = Visibility.Visible;
        ConfigListPanel.Children.Clear();
        var selectableConfigs = _cache.GetSelectableConfigs();
        var usingPartialConfigs = _cache.ValidConfigs.Count == 0 && _cache.PartialConfigs.Count > 0;

        // РђРєС‚РёРІРЅС‹Р№ РєРѕРЅС„РёРі (СЃРЅР°С‡Р°Р»Р°)
        var currentLabel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 16)
        };
        
        var activeText = new TextBlock
        {
            Text = "РђРєС‚РёРІРЅС‹Р№ РєРѕРЅС„РёРі: ",
            FontSize = 15,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold
        };
        
        var configNameText = new TextBlock
        {
            Text = _cache.CurrentConfig,
            FontSize = 15,
            Foreground = new SolidColorBrush(usingPartialConfigs
                ? Color.FromRgb(0xea, 0xb3, 0x08)
                : Color.FromRgb(0x22, 0xc5, 0x5e)),
            FontWeight = FontWeights.Bold
        };
        
        currentLabel.Children.Add(activeText);
        currentLabel.Children.Add(configNameText);
        ConfigListPanel.Children.Add(currentLabel);

        if (usingPartialConfigs)
        {
            ConfigListPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x20, 0x08)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = "Идеальных конфигов не найдено. Ниже показаны частично рабочие варианты без ошибок и недоступных сервисов. Если что-то будет работать нестабильно, переключитесь на другой конфиг.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0xf3, 0xc4)),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        // Р—Р°РіРѕР»РѕРІРѕРє (РїРѕС‚РѕРј)
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = "Р”РѕСЃС‚СѓРїРЅС‹Рµ РєРѕРЅС„РёРіРё",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x2a, 0x3a)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = $"{selectableConfigs.Count} РєРѕРЅС„РёРіРѕРІ",
                FontSize = 11,
                Foreground = new SolidColorBrush(usingPartialConfigs
                    ? Color.FromRgb(0xea, 0xb3, 0x08)
                    : Color.FromRgb(0x3b, 0x82, 0xf6))
            }
        };

        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(badge, 1);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(badge);
        ConfigListPanel.Children.Add(headerGrid);

        // РЎРїРёСЃРѕРє РєРѕРЅС„РёРіРѕРІ
        foreach (var config in selectableConfigs)
        {
            var isCurrent = config.Name == _cache.CurrentConfig;

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
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();

            var nameRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var nameText = new TextBlock
            {
                Text = config.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
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
                        Text = "Р°РєС‚РёРІРЅС‹Р№",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6))
                    }
                };
                nameRow.Children.Add(activeBadge);
            }

            var infoText = new TextBlock
            {
                Text = $"РџРёРЅРі: {config.AveragePing} РјСЃ  вЂў  РўРµСЃС‚С‹: {config.SuccessCount}/12" + (config.IsPartiallyUsable ? "  вЂў  С‡Р°СЃС‚РёС‡РЅРѕ" : ""),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x58)),
                Margin = new Thickness(0, 4, 0, 0)
            };

            left.Children.Add(nameRow);
            left.Children.Add(infoText);

            // РЎС‚СЂРµР»РєР° СЃРїСЂР°РІР°
            var arrow = new TextBlock
            {
                Text = isCurrent ? "вњ“" : "в†’",
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

            // РћРґРёРЅР°СЂРЅС‹Р№ РєР»РёРє - РІС‹Р±СЂР°С‚СЊ РєРѕРЅС„РёРі
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1)
                {
                    _cache.CurrentConfig = config.Name;
                    ZapretConfigService.SaveCache(_cache);
                    ShowConfigList();
                }
            };
            
            // Р”РІРѕР№РЅРѕР№ РєР»РёРє - РїСЂРёРјРµРЅРёС‚СЊ РєРѕРЅС„РёРі
            border.MouseLeftButtonDown += async (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    _cache.CurrentConfig = config.Name;
                    ZapretConfigService.SaveCache(_cache);
                    
                    // РџСЂРёРјРµРЅРёС‚СЊ РєРѕРЅС„РёРі (РІС‹Р·РІР°С‚СЊ С‚РѕС‚ Р¶Рµ РєРѕРґ С‡С‚Рѕ Рё РєРЅРѕРїРєР° "РџСЂРёРјРµРЅРёС‚СЊ")
                    SecondaryBtn_Click(s, e);
                }
            };

            // Hover СЌС„С„РµРєС‚
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

            ConfigListPanel.Children.Add(border);
        }

        SecondaryBtn.Content = "РџСЂРёРјРµРЅРёС‚СЊ";
        PrimaryBtn.Content = "РџСЂРѕРІРµСЂРёС‚СЊ РєРѕРЅС„РёРі";
        PrimaryBtn.Visibility = Visibility.Visible;
    }

    private void StopIndeterminateAnimation()
    {
        ProgressBarContainer.Visibility = Visibility.Collapsed;
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
