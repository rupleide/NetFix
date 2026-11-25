using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetFix.Services;

/// <summary>
/// Центрирует окно на основном мониторе Windows.
/// Координаты Win32 — физические пиксели, Left/Top у WPF — DIP (1/96").
/// При разном масштабе мониторов их нельзя смешивать: размер нужно считать
/// в DPI именно того монитора, на который окно ставится.
/// </summary>
public static class PrimaryMonitorPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SOk = 0;

    public static void Center(Window window)
    {
        if (!TryGetPrimary(out var work, out double scaleX, out double scaleY))
            return;

        double width = Dip(window.Width, window.ActualWidth, 880);
        double height = Dip(window.Height, window.ActualHeight, 680);

        int physW = (int)Math.Round(width * scaleX);
        int physH = (int)Math.Round(height * scaleY);

        int x = work.Left + (work.Width - physW) / 2;
        int y = work.Top + (work.Height - physH) / 2;

        if (physW >= work.Width)
            x = work.Left;
        else
            x = Math.Clamp(x, work.Left, work.Right - physW);

        if (physH >= work.Height)
            y = work.Top;
        else
            y = Math.Clamp(y, work.Top, work.Bottom - physH);

        window.WindowStartupLocation = WindowStartupLocation.Manual;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            // До создания HWND WPF переводит DIP в пиксели как logical * DPI монитора,
            // в чей масштабированный прямоугольник попала точка. Для основного монитора
            // это обратная операция к формуле выше.
            window.Left = x / scaleX;
            window.Top = y / scaleY;
            return;
        }

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public static bool IsOnPrimary(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var primary = MonitorFromPoint(new POINT(0, 0), MonitorDefaultToPrimary);
        var current = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return primary != IntPtr.Zero && primary == current;
    }

    private static double Dip(double length, double actual, double fallback)
    {
        if (!double.IsNaN(length) && length > 0)
            return length;
        if (!double.IsNaN(actual) && actual > 0)
            return actual;
        return fallback;
    }

    private static bool TryGetPrimary(out RECT work, out double scaleX, out double scaleY)
    {
        work = default;
        scaleX = 1;
        scaleY = 1;

        var monitor = MonitorFromPoint(new POINT(0, 0), MonitorDefaultToPrimary);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        if (GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) != SOk || dpiX == 0 || dpiY == 0)
            return false;

        work = info.rcWork;
        scaleX = dpiX / 96.0;
        scaleY = dpiY / 96.0;
        return work.Right > work.Left && work.Bottom > work.Top;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
