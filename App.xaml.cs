using System.Configuration;
using System.Data;
using System.Windows;

namespace SoncaAudioInspector;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
    }

    public App()
    {
        // Owner 2026-11-19: every secondary window (Ctrl+Shift+M…) at most 90 % of the screen height.
        WindowHeightLimit.Register();
        this.DispatcherUnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[DispatcherUnhandledException] {e.Exception}");
            MessageBox.Show(
                $"Đã xảy ra sự cố không mong muốn:\n{e.Exception.Message}\n\nỨng dụng sẽ tiếp tục chạy.",
                "Cảnh báo hệ thống",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppDomain UnhandledException] {ex}");
            }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[UnobservedTaskException] {e.Exception}");
            e.SetObserved();
        };
    }
}

