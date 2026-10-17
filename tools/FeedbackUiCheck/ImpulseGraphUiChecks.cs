using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class ImpulseGraphUiChecks
{
    public static void Check(Assembly assembly, object result, string output)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        Directory.CreateDirectory(output);
        var window = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ImpulseAnalysisWindow")!, result)!;
        var view = (UserControl)window.Content;
        var type = view.GetType();
        dynamic plot = type.GetField("_plot", hidden)!.GetValue(view)!;
        foreach (string name in new[] { "_ir", "_etc", "_decay" })
        {
            dynamic trace = type.GetField(name, hidden)!.GetValue(view)!;
            if (!(bool)trace.IsVisible) throw new Exception(name + " is missing from the combined IR graph.");
        }
        var etcToggle = (CheckBox)type.GetField("_showEtc", hidden)!.GetValue(view)!;
        dynamic etc = type.GetField("_etc", hidden)!.GetValue(view)!;
        etcToggle.IsChecked = false; etcToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        if ((bool)etc.IsVisible) throw new Exception("ETC visibility control did not hide its trace.");
        etcToggle.IsChecked = true; etcToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var metricsToggle = (CheckBox)type.GetField("_showMetrics", hidden)!.GetValue(view)!;
        var metrics = (Border)type.GetField("_metrics", hidden)!.GetValue(view)!;
        metricsToggle.IsChecked = false; metricsToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        if (metrics.Visibility != Visibility.Collapsed) throw new Exception("The data panel cannot be hidden.");
        metricsToggle.IsChecked = true; metricsToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var regressionChoice = (ComboBox)type.GetField("_regressionChoice", hidden)!.GetValue(view)!;
        var regressionToggle = (CheckBox)type.GetField("_showRegression", hidden)!.GetValue(view)!;
        var fits = (System.Collections.IDictionary)type.GetField("_regressions", hidden)!.GetValue(view)!;
        if (fits.Count > 0)
        {
            regressionChoice.SelectedItem = fits.Keys.Cast<string>().First();
            regressionToggle.IsChecked = false; regressionToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            foreach (dynamic fit in fits.Values) if ((bool)fit.IsVisible) throw new Exception("Regression line cannot be hidden.");
            regressionToggle.IsChecked = true; regressionToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            int visible = fits.Values.Cast<object>().Count(fit => (bool)fit.GetType().GetProperty("IsVisible")!.GetValue(fit)!);
            if (visible != 1) throw new Exception("Regression selector did not select exactly one fitted line.");
            if (regressionChoice.Items.Contains("T20")) regressionChoice.SelectedItem = "T20";
        }
        plot.Plot.Axes.SetLimitsX(0.1, .8);
        type.GetMethod("SyncNavigator", hidden)!.Invoke(view, null);
        dynamic selected = type.GetField("_selection", hidden)!.GetValue(view)!;
        if (Math.Abs((double)selected.X1 - .1) > 1e-8 || Math.Abs((double)selected.X2 - .8) > 1e-8)
            throw new Exception("IR navigator does not track the visible time range.");
        type.GetMethod("ResetView", hidden)!.Invoke(view, null);
        Console.WriteLine("IR controls checked; rendering shown window.");
        // Render an actual shown WPF window so the overlay and native plot paint,
        // with no audio acquisition or device commands in this immutable view.
        window.Show(); window.UpdateLayout();
        Console.WriteLine("IR window shown; waiting for first paint.");
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "impulse-window.png"))) encoder.Save(file);
        using (dynamic image = plot.Plot.GetImage(1200, 650)) File.WriteAllBytes(Path.Combine(output, "impulse-plot.png"), (byte[])image.GetImageBytes());
        window.Close();
        Console.WriteLine("PASS combined IR/analytic ETC/Schroeder, trace and data toggles, regression selector and navigator; rendered WPF view.");
    }
}
