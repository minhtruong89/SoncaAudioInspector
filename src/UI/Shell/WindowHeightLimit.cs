using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SoncaAudioInspector;

/// <summary>Owner 2026-11-19: every window except the main shell (Ctrl+Shift+M model editor, BLE mixer, approval, Rè,
/// impedance, dialogs…) is at most 90 % of the height of the work area of the monitor it is on, so the bottom buttons
/// never fall off a 1366×768 station screen or a 125 % Windows scale. Registered once for the Window class, so windows built
/// in code are covered too. The main window keeps its own 100 % fit (MainWindow.ScreenFit).</summary>
public static class WindowHeightLimit
{
    public const double Fraction = .9;
    private static bool _registered;
    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("HeightLimitState", typeof(State), typeof(WindowHeightLimit));
    private sealed class State { public double OwnMaxHeight, OwnMinHeight; public bool Applying; }

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window window) Attach(window); }));
    }

    public static bool IsExempt(Window window) => window is MainWindow;

    /// <summary>Height limit in DIPs for a work area height in DIPs.</summary>
    public static double Limit(double workAreaHeight) => Math.Floor(workAreaHeight * Fraction);

    /// <summary>Pure placement rule: MaxHeight/MinHeight inside the limit (a smaller limit set by the window itself is kept),
    /// and the top so the window stays on screen — vertically centred when the window opened centred.</summary>
    public static (double MaxHeight, double MinHeight, double? Top) Plan(Rect workArea, double ownMaxHeight, double ownMinHeight,
        double top, double actualHeight, bool centred)
    {
        double limit = Limit(workArea.Height);
        double max = double.IsFinite(ownMaxHeight) && ownMaxHeight > 0 ? Math.Min(ownMaxHeight, limit) : limit;
        double min = Math.Min(Math.Max(0, ownMinHeight), max);
        double height = Math.Min(actualHeight > 0 ? actualHeight : max, max);
        double wanted = centred ? workArea.Top + (workArea.Height - height) / 2 : top;
        double clamped = Math.Max(workArea.Top, Math.Min(wanted, workArea.Bottom - height));
        return (max, min, double.IsFinite(top) && Math.Abs(clamped - top) < .5 ? null : clamped);
    }

    private static void Attach(Window window)
    {
        if (IsExempt(window) || window.GetValue(StateProperty) != null) return;
        var state = new State { OwnMaxHeight = window.MaxHeight, OwnMinHeight = window.MinHeight };
        window.SetValue(StateProperty, state); AttachedWindows++;
        Apply(window, state, window.WindowStartupLocation != WindowStartupLocation.Manual, move: true);
        // A maximized window ignores MaxHeight: limit the maximized size itself (WM_GETMINMAXINFO), then re-maximize once.
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => MinMaxHook(window, hwnd, message, lParam, ref handled));
            if (window.WindowState == WindowState.Maximized) { window.WindowState = WindowState.Normal; window.WindowState = WindowState.Maximized; }
        }
        window.DpiChanged += (_, _) => Apply(window, state, false, move: true);
        // A drag onto another monitor changes the limit; do not move the window while the user drags it.
        window.LocationChanged += (_, _) => Apply(window, state, false, move: false);
        window.SizeChanged += (_, e) => { if (e.HeightChanged) Apply(window, state, false, move: true); };
    }

    private static void Apply(Window window, State state, bool centred, bool move)
    {
        if (state.Applying || WorkArea(window) is not Rect area) return;
        state.Applying = true;
        try
        {
            var plan = Plan(area, state.OwnMaxHeight, state.OwnMinHeight, window.Top, window.ActualHeight, centred);
            if (window.MinHeight != plan.MinHeight) window.MinHeight = plan.MinHeight;
            if (window.MaxHeight != plan.MaxHeight) window.MaxHeight = plan.MaxHeight;
            if (move && plan.Top is double top && window.WindowState == WindowState.Normal) window.Top = top;
        }
        finally { state.Applying = false; }
    }

    private const int WmGetMinMaxInfo = 0x0024;
    /// <summary>Diagnostics for the offline window check: WM_GETMINMAXINFO messages answered, windows attached.</summary>
    public static int MinMaxCalls { get; private set; }
    public static int AttachedWindows { get; private set; }
    /// <summary>Maximized = full work-area width × 90 % height, vertically centred. Handled here (WPF would otherwise answer first),
    /// so the tracking sizes are filled from the window's Min/Max Width/Height exactly as WPF does.</summary>
    private static IntPtr MinMaxHook(Window window, IntPtr hwnd, int message, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        MinMaxCalls++;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        double scale = Math.Max(.1, dpi.DpiScaleY), scaleX = Math.Max(.1, dpi.DpiScaleX);
        int workHeight = info.Work.Bottom - info.Work.Top, workWidth = info.Work.Right - info.Work.Left;
        int height = Math.Min(workHeight, (int)Math.Floor(Limit(workHeight / scale) * scale));
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxSize = new(workWidth, height);
        limits.MaxPosition = new(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top + (workHeight - height) / 2);
        static int Px(double dips, double factor, int fallback) => double.IsFinite(dips) && dips > 0 ? (int)Math.Ceiling(dips * factor) : fallback;
        limits.MinTrackSize = new(Math.Max(limits.MinTrackSize.X, Px(window.MinWidth, scaleX, 0)), Math.Max(limits.MinTrackSize.Y, Math.Min(height, Px(window.MinHeight, scale, 0))));
        limits.MaxTrackSize = new(Math.Min(limits.MaxTrackSize.X, Px(window.MaxWidth, scaleX, int.MaxValue)),
            Math.Min(Math.Min(limits.MaxTrackSize.Y, height), Px(window.MaxHeight, scale, int.MaxValue)));
        Marshal.StructureToPtr(limits, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Work area (DIPs) of the monitor that holds the window; the primary work area before the window has a handle.</summary>
    public static Rect? WorkArea(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var pixels = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        if (PresentationSource.FromVisual(window)?.CompositionTarget is not { } target) return SystemParameters.WorkArea;
        var toDips = target.TransformFromDevice;
        return new Rect(toDips.Transform(pixels.TopLeft), toDips.Transform(pixels.BottomRight));
    }

    private const int MonitorDefaultToNearest = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
}
