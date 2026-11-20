using System.IO;
using System.Windows;
using System.Windows.Controls;
using SoncaAudioInspector;

/// <summary>Owner 2026-11-19: secondary windows (Ctrl+Shift+M…) are at most 90 % of the work area height of their monitor.
/// Runs real WPF windows on this machine's monitor; App() registers WindowHeightLimit exactly as the station app does.</summary>
internal static class WindowHeightChecks
{
    public static void Run(string output, Action<bool, string> check)
    {
        var work = SystemParameters.WorkArea;
        double limit = WindowHeightLimit.Limit(work.Height);
        var lines = new List<string> { $"Work area {work.Width:0}×{work.Height:0} DIP; limit {limit:0} DIP (90 %)." };

        // Pure rule.
        var area = new Rect(0, 0, 1366, 728);
        var plan = WindowHeightLimit.Plan(area, double.PositiveInfinity, 600, -61, 850, centred: true);
        check(plan.MaxHeight == 655 && plan.MinHeight == 600 && plan.Top is double top && Math.Abs(top - 36.5) < .01,
            $"Plan: 850 DIP window on a 728 DIP work area → max 655, centred at 36.5 ({plan.MaxHeight}/{plan.MinHeight}/{plan.Top})");
        var small = WindowHeightLimit.Plan(area, 300, 700, 100, 300, centred: false);
        check(small.MaxHeight == 300 && small.MinHeight == 300 && small.Top == null, "Plan keeps a smaller own MaxHeight, lowers MinHeight above it, does not move a fitting window");
        var low = WindowHeightLimit.Plan(area, double.PositiveInfinity, 0, 500, 400, centred: false);
        check(low.Top is double up && Math.Abs(up - 328) < .01, "Plan moves a window whose bottom leaves the screen back inside");

        Window Show(Window window)
        {
            // WPF refuses a maximized window that is not activated.
            window.ShowActivated = window.WindowState == WindowState.Maximized; window.ShowInTaskbar = false; window.Show(); window.UpdateLayout();
            Dispatch(); return window;
        }
        void Measure(Window window, string name)
        {
            double bottom = window.Top + window.ActualHeight;
            lines.Add($"[attached {WindowHeightLimit.AttachedWindows}, minmax {WindowHeightLimit.MinMaxCalls}] {name}: Height {window.Height:0}, ActualHeight {window.ActualHeight:0.#}, MaxHeight {window.MaxHeight:0}, Top {window.Top:0.#}, state {window.WindowState}");
            File.WriteAllLines(Path.Combine(output, "window-height.txt"), lines);
            check(window.ActualHeight <= limit + .5 && window.MaxHeight <= limit + .5, $"{name} at most 90 % of the screen height ({window.ActualHeight:0}/{work.Height:0} DIP)");
            if (window.WindowState == WindowState.Normal)
                check(window.Top >= work.Top - .5 && bottom <= work.Bottom + .5, $"{name} stays on screen (top {window.Top:0}, bottom {bottom:0})");
        }

        var editor = Show(new ModelConfigEditorWindow(C360ModelDefinition.Create()) { WindowStartupLocation = WindowStartupLocation.CenterScreen });
        try { Measure(editor, "Ctrl+Shift+M model editor"); } finally { editor.Close(); }

        var tall = Show(new Window { Title = "tall", Width = 400, Height = 5000, MinHeight = 4000, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new ScrollViewer { Content = new Border { Height = 6000 } } });
        try { Measure(tall, "5000 DIP window with MinHeight 4000"); } finally { tall.Close(); }

        var sized = Show(new Window { Title = "content", Width = 400, SizeToContent = SizeToContent.Height, Content = new Border { Height = 4000 } });
        try { Measure(sized, "SizeToContent window with 4000 DIP content"); } finally { sized.Close(); }

        var maximized = Show(new Window { Title = "max", WindowState = WindowState.Maximized, Content = new Border() });
        try { Measure(maximized, "Maximized window"); } finally { maximized.Close(); }

        File.WriteAllLines(Path.Combine(output, "window-height.txt"), lines);
    }

    private static void Dispatch()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
