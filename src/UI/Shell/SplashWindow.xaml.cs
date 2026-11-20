using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace SoncaAudioInspector
{
    public partial class SplashWindow : Window
    {
        private bool _diagnosticsRunning;
        public SplashWindow()
        {
            InitializeComponent();
            this.Loaded += SplashWindow_Loaded;

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
            {
                LblVersion.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        private async void SplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RunDiagnosticsAsync();
        }

        private async Task RunDiagnosticsAsync()
        {
            if (_diagnosticsRunning) return;
            _diagnosticsRunning = true;
            try
            {
            // Reset UI states
            PanelFailureButtons.Visibility = Visibility.Collapsed;
            PanelVerifyHelper.Visibility = Visibility.Collapsed;
            TxtCopyFeedback.Visibility = Visibility.Collapsed;
            LblStatus.Text = "Đang thực hiện kiểm tra chẩn đoán hệ thống...";
            LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170));

            IconSys001.Text = "○";
            IconSys001.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            TxtSys001.Text = "SYS001 - Xác thực ứng dụng (Đang kiểm tra...)";
            ErrorSys001.Visibility = Visibility.Collapsed;
            ErrorSys001.Text = "";

            IconSys002.Text = "○";
            IconSys002.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            TxtSys002.Text = "SYS002 - Kiểm tra và tải cấu hình hệ thống (Đang kiểm tra...)";
            ErrorSys002.Visibility = Visibility.Collapsed;
            ErrorSys002.Text = "";

            IconSys003.Text = "○";
            IconSys003.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            TxtSys003.Text = "SYS003 - Kiểm tra kết nối thiết bị phần cứng (Đang kiểm tra...)";
            ErrorSys003.Visibility = Visibility.Collapsed;
            ErrorSys003.Text = "";

            // ---------------------------------------------------------
            // Verify the app and fetch the mandatory signed station configuration
            // concurrently. Unlike app updates, the server config is always applied.
            // ---------------------------------------------------------
            using var configTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12));
            Task<Update.ServerConfigState> configTask = Update.ServerConfigSync.RunDefaultAsync(configTimeout.Token);
            Task<bool> appVerificationTask = ServerEngine.VerifyAppAsync(
                new Progress<string>(message => LblStatus.Text = message));
            bool appVerified = await appVerificationTask;

            if (appVerified)
            {
                IconSys001.Text = "✔";
                IconSys001.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Neon green
                TxtSys001.Text = "SYS001 - Xác thực ứng dụng (Đạt)";
            }
            else
            {
                IconSys001.Text = "✘";
                IconSys001.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Neon red
                TxtSys001.Text = "SYS001 - Xác thực ứng dụng (Không Đạt)";
                ErrorSys001.Text = ServerEngine.LastError ?? "Không thể xác thực ứng dụng.";
                ErrorSys001.Visibility = Visibility.Visible;
            }

            // ---------------------------------------------------------
            // SYS002 - Mandatory signed checking_config.json from the update server
            // ---------------------------------------------------------
            Update.ServerConfigState configState;
            try { configState = await configTask; }
            catch (Exception ex) { configState = new(Update.ServerConfigMode.Unavailable, null, "Lỗi đồng bộ config server: " + ex.Message); }
            Update.ServerConfigStatus.Current = configState;
            bool sys002Pass = configState.AllowsMeasurement;
            if (configState.Mode == Update.ServerConfigMode.Online)
            {
                IconSys002.Text = "✔";
                IconSys002.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Neon green
                TxtSys002.Text = "SYS002 - Config server (Đạt · " + configState.Manifest!.Describe() + ")";
            }
            else if (sys002Pass)
            {
                IconSys002.Text = "!";
                IconSys002.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Warning orange
                TxtSys002.Text = configState.Mode == Update.ServerConfigMode.AdminLocal
                    ? "SYS002 - Config server (ADMIN: giữ bản cục bộ)" : "SYS002 - Config server (OFFLINE: dùng bản server đã tải)";
                ErrorSys002.Text = configState.Message;
                ErrorSys002.Visibility = Visibility.Visible;
            }
            else
            {
                IconSys002.Text = "✘";
                IconSys002.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Neon red
                TxtSys002.Text = "SYS002 - Config server (Không Đạt)";
                ErrorSys002.Text = configState.Message;
                ErrorSys002.Visibility = Visibility.Visible;
            }

            // ---------------------------------------------------------
            // SYS003 - Audio hardware is initialized once by Audio Routing.
            // Avoid enumerating it again here so the login window appears faster.
            // ---------------------------------------------------------
            bool hardwarePass = true;
            IconSys003.Text = "○";
            IconSys003.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170));
            TxtSys003.Text = "SYS003 - Thiết bị âm thanh sẽ được tải tại Audio Routing";

            // ---------------------------------------------------------
            // Final Decision
            // ---------------------------------------------------------
            if (appVerified && sys002Pass && hardwarePass)
            {
                LblStatus.Text = "Tất cả các kiểm tra đều đạt! Đang tải giao diện đăng nhập...";
                LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));

                // Launch login window
                LoginWindow login = new LoginWindow();
                App.Current.MainWindow = login;
                login.Show();
                this.Close();

                // Launch main window
                /*MainWindow main = new MainWindow();
                App.Current.MainWindow = main;
                main.Show();
                this.Close();*/

            }
            else
            {
                LblStatus.Text = !sys002Pass && appVerified ? Update.ServerConfigStatus.Current.Message
                    : ServerEngine.LastError ?? "Kiểm tra chẩn đoán hệ thống không đạt!";
                LblStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                if (!appVerified)
                {
                    string expectedPath = ServerEngine.GetExpectedVerifyFilePath();
                    bool isVerifyMissing = (ServerEngine.LastError?.Contains("verify.txt") == true) || !File.Exists(expectedPath);
                    PanelVerifyHelper.Visibility = isVerifyMissing ? Visibility.Visible : Visibility.Collapsed;
                    if (isVerifyMissing) TxtVerifyPath.Text = expectedPath;
                }
                PanelFailureButtons.Visibility = Visibility.Visible;
            }
            }
            finally { _diagnosticsRunning = false; }
        }

        internal static string? ReadValidConfiguration(string path)
            => GetValidConfiguration(path)?.Json;

        internal static (string Json, string SourcePath)? GetValidConfiguration(string path)
        {
            try
            {
                foreach (string candidate in new[] { path, path + ".bak" })
                {
                    try
                    {
                        string json = File.ReadAllText(candidate);
                        using var document = System.Text.Json.JsonDocument.Parse(json);
                        if (document.RootElement.TryGetProperty("models", out var models)
                            && models.ValueKind == System.Text.Json.JsonValueKind.Array && models.GetArrayLength() > 0) return (json, candidate);
                    }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
                }
                return null;
            }
            catch { return null; }
        }

        private async void BtnRetry_Click(object sender, RoutedEventArgs e)
        {
            await RunDiagnosticsAsync();
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
        private void BtnCopyVerifyPath_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtVerifyPath.Text))
            {
                Clipboard.SetText(TxtVerifyPath.Text);
                TxtCopyFeedback.Text = "✓ Đã sao chép đường dẫn vào clipboard!";
                TxtCopyFeedback.Visibility = Visibility.Visible;
            }
        }

        private void BtnOpenVerifyFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string target = TxtVerifyPath.Text;
                if (File.Exists(target))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                }
                else
                {
                    string? dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                    }
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(this, "Không mở được thư mục: " + ex.Message, "Lỗi", ModernMessageBox.MessageBoxType.Error);
            }
        }

        private void BtnCreateTemplateVerify_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string target = TxtVerifyPath.Text;
                if (!File.Exists(target))
                {
                    string? dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    string template = "{\n  \"email\": \"admin@sonca.vn\",\n  \"password\": \"mat_khau_admin_o_day\"\n}\n";
                    File.WriteAllText(target, template, System.Text.Encoding.UTF8);
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{target}\"") { UseShellExecute = true });
                TxtCopyFeedback.Text = "✓ Đã mở Notepad! Hãy nhập email/mật khẩu quản trị rồi bấm 'Kiểm tra lại'.";
                TxtCopyFeedback.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(this, "Không tạo được file: " + ex.Message, "Lỗi", ModernMessageBox.MessageBoxType.Error);
            }
        }
    }
}
