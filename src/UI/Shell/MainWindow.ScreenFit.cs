using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SoncaAudioInspector;

/// <summary>v1.5.3 (user request 2026-11-18): the main window always fits 100% of the screen it is on.
/// - opens filling the monitor work area (taskbar stays visible);
/// - a maximized borderless window is limited to the work area of its own monitor (WPF default overshoots and hides the taskbar);
/// - below the design size the whole layout scales down uniformly instead of clipping controls.</summary>
public partial class MainWindow
{
    private const double FitDesignWidth = 1200, FitDesignHeight = 720, FitMinimumScale = .55;
    private double _fitScale = 1;

    private void InitializeScreenFit()
    {
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(ScreenFitHook);
        };
        Loaded += (_, _) => FitToScreenOnStart();
        SizeChanged += (_, _) => UpdateFitScale();
        DpiChanged += (_, _) => UpdateFitScale();
    }

    private void FitToScreenOnStart()
    {
        var area = SystemParameters.WorkArea;
        // Restore bounds stay inside the work area, so leaving full screen never produces an off-screen window.
        MinWidth = Math.Min(MinWidth, area.Width); MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Math.Max(Width, MinWidth), area.Width); Height = Math.Min(Math.Max(Height, MinHeight), area.Height);
        Left = area.Left + (area.Width - Width) / 2; Top = area.Top + (area.Height - Height) / 2;
        if (!_isFullscreen && Environment.GetEnvironmentVariable("SONCA_START_WINDOWED") != "1")
            BtnToggleFullscreen_Click(this, new RoutedEventArgs());
        UpdateFitScale();
    }

    public static double FitScale(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return 1;
        double scale = Math.Min(1, Math.Min(width / FitDesignWidth, height / FitDesignHeight));
        return Math.Max(FitMinimumScale, Math.Round(scale, 3));
    }

    private void UpdateFitScale()
    {
        if (RootLayout == null) return;
        double scale = FitScale(ActualWidth, ActualHeight);
        if (Math.Abs(scale - _fitScale) < .005) return;
        _fitScale = scale;
        RootLayout.LayoutTransform = scale >= .999 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private IntPtr ScreenFitHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // Device pixels relative to the monitor: correct on every monitor and DPI.
        limits.MaxPosition = new(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        limits.MaxSize = new(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        Marshal.StructureToPtr(limits, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private const int MonitorDefaultToNearest = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
}
