using System.Text.Json;
using Sonca.RewAudioInspector.Models;
using Sonca.RewAudioInspector.Services;

namespace Sonca.RewAudioInspector;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "Sonca REW Audio Inspector Bridge (44.1 kHz & 64k FFT)";

        using var client = new RewApiClient();

        // 1. Process Command-Line Arguments if provided (for CI/CD or automation)
        if (args.Length > 0)
        {
            return await HandleCliArgumentsAsync(client, args);
        }

        // 2. Interactive Console Mode
        await RunInteractiveModeAsync(client);
        return 0;
    }

    private static async Task<int> HandleCliArgumentsAsync(RewApiClient client, string[] args)
    {
        bool isAuto = args.Contains("--auto", StringComparer.OrdinalIgnoreCase);
        bool isJson = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        string? outFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outFile = args[i + 1];
            }
        }

        if (isAuto)
        {
            bool alive = await client.IsApiAliveAsync();
            if (!alive)
            {
                alive = await client.EnsureRewRunningAsync(TimeSpan.FromSeconds(15));
                if (!alive)
                {
                    Console.Error.WriteLine("[ERROR] Could not connect to REW REST API on " + client.BaseUrl);
                    return 1;
                }
            }

            Console.WriteLine("[INFO] Running standardized REW measurement (44.1 kHz, 64k FFT, 1000 Hz Sine)...");
            var report = await client.RunStandardMeasurementAsync(sineFreqHz: 1000.0, sineLevelDbfs: -12.0, settleTimeMs: 1500, useGenerator: true);

            string json = JsonSerializer.Serialize(report, JsonOptions);
            if (outFile != null)
            {
                await File.WriteAllTextAsync(outFile, json);
                Console.WriteLine($"[INFO] Report saved to {outFile}");
            }

            if (isJson)
            {
                Console.WriteLine(json);
            }
            else
            {
                PrintMeasurementReport(report);
            }

            return 0;
        }

        Console.WriteLine("Usage: SoncaRewAudioInspector [--auto] [--json] [--out <output_path.json>]");
        return 0;
    }

    private static async Task RunInteractiveModeAsync(RewApiClient client)
    {
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n================================================================================");
            Console.WriteLine("         SONCA AUDIO INSPECTOR - REW REST API BRIDGE (44.1 kHz / 64k FFT)       ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            bool isAlive = await client.IsApiAliveAsync();
            string statusStr = isAlive ? "[ONLINE]" : "[OFFLINE]";
            ConsoleColor statusCol = isAlive ? ConsoleColor.Green : ConsoleColor.Red;

            Console.Write("Trạng thái REW API (port 4735): ");
            Console.ForegroundColor = statusCol;
            Console.WriteLine(statusStr);
            Console.ResetColor();

            if (isAlive)
            {
                var ver = await client.GetVersionAsync();
                var rate = await client.GetSampleRateAsync();
                Console.WriteLine($"  - Phiên bản REW: {ver}");
                Console.WriteLine($"  - Sample Rate hiện tại: {rate?.Value} {rate?.Unit}");
            }

            Console.WriteLine("\nChọn thao tác:");
            Console.WriteLine(" [1] Kiểm tra kết nối REW API / Tự động khởi chạy REW với -api");
            Console.WriteLine(" [2] Thiết lập chuẩn hóa REW (Sample Rate 44.1 kHz, FFT 64k, Hann Window)");
            Console.WriteLine(" [3] Chạy chu trình đo THD & FFT chuẩn REW (1 kHz Sine @ -12 dBFS)");
            Console.WriteLine(" [4] Giám sát THD & Harmonic Distortion thời gian thực (Live Monitor)");
            Console.WriteLine(" [5] Đọc và xuất phổ tần số FFT Spectrum (Magnitudes)");
            Console.WriteLine(" [6] Tùy chỉnh thông số phát sóng Generator (Freq, dBFS)");
            Console.WriteLine(" [0] Thoát");
            Console.Write("\nLựa chọn của bạn: ");

            string? choice = Console.ReadLine()?.Trim();
            if (choice == "0") break;

            switch (choice)
            {
                case "1":
                    await CheckOrLaunchRewAsync(client);
                    break;
                case "2":
                    await ApplyStandardRewSettingsAsync(client);
                    break;
                case "3":
                    await RunMeasurementCycleAsync(client);
                    break;
                case "4":
                    await MonitorLiveDistortionAsync(client);
                    break;
                case "5":
                    await FetchAndExportSpectrumAsync(client);
                    break;
                case "6":
                    await ConfigureGeneratorInteractiveAsync(client);
                    break;
                default:
                    Console.WriteLine("Lựa chọn không hợp lệ!");
                    break;
            }
        }
    }

    private static async Task CheckOrLaunchRewAsync(RewApiClient client)
    {
        Console.WriteLine("\n--- KIỂM TRA KẾT NỐI REW REST API ---");
        bool alive = await client.IsApiAliveAsync();
        if (alive)
        {
            string? ver = await client.GetVersionAsync();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[THÀNH CÔNG] Đã kết nối tới REW API! Phiên bản: {ver}");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[CẢNH BÁO] Chưa kết nối được tới REW API trên port 4735. Đang thử khởi chạy REW...");
            Console.ResetColor();

            bool started = await client.EnsureRewRunningAsync(TimeSpan.FromSeconds(15));
            if (started)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[THÀNH CÔNG] REW đã khởi động và API đã sẵn sàng!");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[THẤT BẠI] Không thể kết nối. Vui lòng mở REW bằng lệnh: roomeqwizard.exe -api");
                Console.ResetColor();
            }
        }
    }

    private static async Task ApplyStandardRewSettingsAsync(RewApiClient client)
    {
        Console.WriteLine("\n--- THIẾT LẬP CHUẨN HÓA REW: 44.1 kHz & 64k FFT ---");

        // 1. Enforce Sample Rate 44.1 kHz
        Console.Write("1. Thiết lập Sample Rate = 44,100 Hz (44.1 kHz)... ");
        var (rateOk, rateMsg) = await client.SetSampleRateAsync(44100.0);
        if (rateOk)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[OK]");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CẢNH BÁO] {rateMsg}");
        }
        Console.ResetColor();

        // 2. Enforce FFT 64k
        Console.Write("2. Thiết lập RTA FFT = 64k (65,536 điểm), Window = Hann, Mode = Spectrum... ");
        var (rtaOk, rtaMsg) = await client.ConfigureRtaAsync(fftLength: "64k", window: "Hann", mode: "Spectrum", calcDistortion: true);
        if (rtaOk)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[OK]");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CẢNH BÁO] {rtaMsg}");
        }
        Console.ResetColor();

        // 3. Distortion unit
        Console.Write("3. Thiết lập đơn vị Distortion = percent (%)... ");
        var (distOk, distMsg) = await client.ConfigureDistortionAsync("percent");
        if (distOk)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[OK]");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CẢNH BÁO] {distMsg}");
        }
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=> Đã hoàn tất chuẩn hóa tham số REW theo yêu cầu!");
        Console.ResetColor();
    }

    private static async Task RunMeasurementCycleAsync(RewApiClient client)
    {
        Console.WriteLine("\n--- CHẠY CHU TRÌNH ĐO CHUẨN THD & FFT VỚI REW API ---");
        Console.WriteLine("Cấu hình đo: 44.1 kHz Sample Rate | 64k FFT Length | 1000 Hz Sine @ -12 dBFS");
        Console.Write("Bắt đầu đo... ");

        var report = await client.RunStandardMeasurementAsync(sineFreqHz: 1000.0, sineLevelDbfs: -12.0, settleTimeMs: 1500, useGenerator: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[HOÀN TẤT]\n");
        Console.ResetColor();

        PrintMeasurementReport(report);

        Console.Write("\nBạn có muốn lưu kết quả đo ra file JSON không? (y/n): ");
        string? ans = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (ans == "y" || ans == "yes")
        {
            string filename = $"rew_measurement_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string json = JsonSerializer.Serialize(report, JsonOptions);
            await File.WriteAllTextAsync(filename, json);
            Console.WriteLine($"Đã lưu kết quả đo vào: {Path.GetFullPath(filename)}");
        }
    }

    private static async Task MonitorLiveDistortionAsync(RewApiClient client)
    {
        Console.WriteLine("\n--- GIÁM SÁT THD & DISTORTION THỜI GIAN THỰC (Nhấn phím bất kỳ để dừng) ---");

        // Ensure RTA is running
        await client.StartRtaAsync();

        try
        {
            while (!Console.KeyAvailable)
            {
                var dist = await client.GetDistortionAsync();
                if (dist != null)
                {
                    if (dist.Message == "No data")
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] REW RTA: Chờ tín hiệu đầu vào (No data)...");
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Freq: {dist.FundamentalFrequency:F1} Hz | Level: {dist.FundamentaldBFS:F2} dBFS | THD: {dist.Thd:F4}% | THD+N: {dist.ThdPlusN:F4}% | SNR: {dist.SnrdB:F1} dB");
                        if (dist.ThdHarmonics != null && dist.ThdHarmonics.Count > 0)
                        {
                            var hStrs = dist.ThdHarmonics.Select((h, i) => $"H{i + 2}: {h:F4}%");
                            Console.WriteLine("    Harmonics -> " + string.Join(" | ", hStrs));
                        }
                    }
                }
                await Task.Delay(500);
            }
            Console.ReadKey(true); // consume key
        }
        finally
        {
            await client.StopRtaAsync();
            Console.WriteLine("Đã dừng RTA.");
        }
    }

    private static async Task FetchAndExportSpectrumAsync(RewApiClient client)
    {
        Console.WriteLine("\n--- ĐỌC VÀ XUẤT PHỔ TẦN SỐ FFT SPECTRUM TỪ REW API ---");
        var spectrum = await client.GetCapturedSpectrumAsync();
        if (spectrum == null || spectrum.Magnitude == null || spectrum.Magnitude.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[THÔNG BÁO] Hiện chưa có dữ liệu phổ FFT trong REW RTA (cần bật RTA để tích lũy phổ).");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"Số điểm phổ (Bins): {spectrum.Magnitude.Count}");
        Console.WriteLine($"Tần số bắt đầu: {spectrum.StartFreq} Hz");
        Console.WriteLine($"Bước tần số (Delta Freq): {spectrum.FreqStep:F4} Hz");

        string csvPath = $"rew_fft_spectrum_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        using (var sw = new StreamWriter(csvPath))
        {
            await sw.WriteLineAsync("Frequency_Hz,Magnitude_dBFS");
            for (int i = 0; i < spectrum.Magnitude.Count; i++)
            {
                double f = spectrum.StartFreq + (i * spectrum.FreqStep);
                await sw.WriteLineAsync($"{f:F4},{spectrum.Magnitude[i]:F3}");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[THÀNH CÔNG] Đã xuất {spectrum.Magnitude.Count} điểm phổ FFT ra file: {Path.GetFullPath(csvPath)}");
        Console.ResetColor();
    }

    private static async Task ConfigureGeneratorInteractiveAsync(RewApiClient client)
    {
        Console.WriteLine("\n--- CẤU HÌNH SÓNG PHÁT GENERATOR ---");
        Console.Write("Nhập tần số Sine (Hz) [Mặc định 1000]: ");
        string? freqStr = Console.ReadLine()?.Trim();
        double freq = double.TryParse(freqStr, out var fVal) ? fVal : 1000.0;

        Console.Write("Nhập biên độ level (dBFS) [Mặc định -12]: ");
        string? levelStr = Console.ReadLine()?.Trim();
        double level = double.TryParse(levelStr, out var lVal) ? lVal : -12.0;

        Console.Write($"Đang thiết lập Generator Sine {freq} Hz @ {level} dBFS... ");
        var (ok, msg) = await client.ConfigureSineGeneratorAsync(freq, level);
        if (ok)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[OK]");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CẢNH BÁO] {msg}");
        }
        Console.ResetColor();

        Console.Write("Bạn có muốn phát sóng thử không? (y/n): ");
        string? play = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (play == "y" || play == "yes")
        {
            await client.StartGeneratorAsync();
            Console.WriteLine("Đang phát sóng... Nhấn phím bất kỳ để dừng.");
            Console.ReadKey(true);
            await client.StopGeneratorAsync();
            Console.WriteLine("Đã dừng phát sóng.");
        }
    }

    private static void PrintMeasurementReport(RewStandardMeasurementReport report)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("                    KẾT QUẢ ĐO CHUẨN HÓA TỪ REW REST API                        ");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.ResetColor();

        Console.WriteLine($"Thời điểm đo:            {report.Timestamp:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Tần số lấy mẫu (Fs):     {report.SampleRateHz} Hz (44.1 kHz)");
        Console.WriteLine($"Độ dài FFT (Bins):       {report.FftLength} (65,536 điểm)");
        Console.WriteLine($"Cửa sổ phân tích:        {report.Window}");
        Console.WriteLine($"Chế độ phân tích:        {report.Mode}");
        Console.WriteLine($"Trạng thái REW Engine:   {(report.RewEngineActive ? "Đang kết nối (Active)" : "Không hoạt động")}");
        Console.WriteLine($"Ghi chú từ REW:          {report.StatusSummary}");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[CHỈ SỐ BIẾN DẠNG HÀI - THD & DISTORTION]");
        Console.ResetColor();

        if (report.FundamentalFreqHz.HasValue)
        {
            Console.WriteLine($"  - Tần số cơ bản (F0):   {report.FundamentalFreqHz.Value:F2} Hz");
            Console.WriteLine($"  - Mức cơ bản (F0 dBFS):  {report.FundamentaldBFS.GetValueOrDefault():F2} dBFS");
            Console.WriteLine($"  - Tổng độ méo THD:       {report.ThdPercent.GetValueOrDefault():F4} %");
            Console.WriteLine($"  - THD + Noise:           {report.ThdPlusNPercent.GetValueOrDefault():F4} %");
            Console.WriteLine($"  - Tỷ số tín hiệu/nhiễu:  {report.SnrdB.GetValueOrDefault():F2} dB");
            Console.WriteLine($"  - Số bit hiệu dụng ENOB: {report.Enob.GetValueOrDefault():F2} bits");

            if (report.HarmonicsPercent != null && report.HarmonicsPercent.Count > 0)
            {
                Console.WriteLine("  - Chi tiết các sóng hài:");
                for (int i = 0; i < report.HarmonicsPercent.Count; i++)
                {
                    Console.WriteLine($"      + Hài bậc {i + 2} (H{i + 2}): {report.HarmonicsPercent[i]:F5} %");
                }
            }
        }
        else
        {
            Console.WriteLine("  (Chưa có tín hiệu âm thanh hợp lệ hoặc RTA đang ở trạng thái 'No data')");
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[PHỔ TẦN SỐ FFT]");
        Console.ResetColor();
        Console.WriteLine($"  - Số bin phổ FFT:       {report.SpectrumPoints}");
        if (report.SpectrumPoints > 0)
        {
            Console.WriteLine($"  - Bước phân giải bin:   {report.FreqStepHz:F4} Hz");
            Console.WriteLine($"  - Đỉnh phổ lớn nhất:    {report.PeakMagnitudeDbfs:F2} dBFS tại {report.PeakFrequencyHz:F2} Hz");
        }
        Console.WriteLine("--------------------------------------------------------------------------------");
    }
}
