# Sonca REW Audio Inspector Bridge (44.1 kHz / 64k FFT)

Ứng dụng độc lập giao tiếp trực tiếp với **Room EQ Wizard (REW) REST API**, chuẩn hóa toàn diện quy trình đo kiểm âm thanh, lấy chỉ số méo điều hòa **THD** và phổ tần số **FFT** trực tiếp từ lõi tính toán của REW.

---

## 1. Tính khả thi khi dùng REW API

- **REW có REST API chính thức:** Kể từ phiên bản V5.20+ (phiên bản trên máy hiện tại là `REW V5.40 Beta 135`), REW tích hợp sẵn máy chủ HTTP REST API (Jetty/Spark) chạy tại cổng mặc định `http://localhost:4735`.
- **Cách kích hoạt:** Chạy lệnh `roomeqwizard.exe -api` hoặc bật tùy chọn *“Start the API when REW starts”* trong mục **Preferences -> API** của REW.
- **Tính toán THD và FFT:** REW RTA (Real-Time Analyzer) tính toán phổ FFT 64-bit và phân rã các sóng hài (H2, H3, H4, H5... THD, THD+N, SNR) thời gian thực.
- **Lưu ý về bản quyền:** 
  - Đọc dữ liệu, cấu hình RTA, FFT Spectrum, THD Distortion và Generator qua API là **hoàn toàn miễn phí**.
  - Lệnh kích hoạt Sweep tự động (`/measure`) yêu cầu bản quyền REW Pro Upgrade. Do đó ứng dụng này chuẩn hóa theo phương pháp **RTA FFT + Sine Generator** (hoặc Sweep RTA) đảm bảo chạy mượt mà 100% không yêu cầu Pro License.

---

## 2. Chuẩn hóa API REW cho 44.1 kHz và 64k FFT

Trong âm thanh số và REW:
- **Sample Rate chuẩn:** `44.1 kHz` (44,100 Hz).
  - API gọi: `POST /audio/samplerate` với payload `{"value": 44100.0, "unit": "Hz"}`.
- **Độ dài FFT (FFT Length):** `64k` (65,536 bins) – bước phân giải $\Delta f \approx 0.6729\text{ Hz/bin}$.
  - API gọi: `POST /rta/configuration` với:
    ```json
    {
      "mode": "Spectrum",
      "smoothing": "None",
      "fftLength": "64k",
      "window": "Hann",
      "maximumOverlap": "50%",
      "calcDistortionEnabled": true,
      "use64BitFFT": true
    }
    ```
- **Chỉ số THD & Sóng hài:**
  - API gọi: `GET /rta/distortion`.
  - Trả về: `fundamentalFrequency`, `fundamentaldBFS`, `thd` (%), `thdHarmonics` (d2..d9), `thdPlusN` (%), `snrdB`, `enob`.
- **Phổ tần số FFT:**
  - API gọi: `GET /rta/captured-data`.
  - Trả về: `startFreq`, `freqStep`, `magnitude` (mảng dBFS của 65,536 điểm).

---

## 3. Cấu trúc độc lập không ảnh hưởng Solution gốc (`.sln`)

Ứng dụng được thiết kế hoàn toàn tách biệt:
- **Solution riêng:** `tools/RewAudioInspector/RewAudioInspector.sln`
- **Project riêng:** `tools/RewAudioInspector/RewAudioInspector.csproj`
- **Vị trí:** Nằm trong thư mục `tools/` (đã được cấu hình loại trừ mặc định `<Compile Remove="tools\**\*.cs" />` trong `SoncaAudioInspector.csproj`).
- **File `.sln` và `.csproj` gốc:** Giữ nguyên 100%, không bị sửa đổi hay xung đột.

---

## 4. Cách sử dụng

### Chạy trực tiếp qua mã nguồn .NET:
```powershell
dotnet run --project tools/RewAudioInspector/RewAudioInspector.csproj
```

### Chạy ở chế độ tự động (Automation / CI / Subprocess):
```powershell
# Đo tự động và in báo cáo
dotnet run --project tools/RewAudioInspector/RewAudioInspector.csproj -- --auto

# Đo tự động và xuất định dạng JSON
dotnet run --project tools/RewAudioInspector/RewAudioInspector.csproj -- --auto --json

# Đo và lưu trực tiếp ra file kết quả JSON
dotnet run --project tools/RewAudioInspector/RewAudioInspector.csproj -- --auto --out result.json
```

### Đóng gói ứng dụng (Publish Standalone):
Chạy script đóng gói tự động:
```powershell
powershell -ExecutionPolicy Bypass -File tools/RewAudioInspector/build_package.ps1
```
Kết quả được xuất ra thư mục `tools/RewAudioInspector/dist/`:
- `SoncaRewAudioInspector.exe`
- `run.bat` (nhấp đúp để chạy giao diện đo)
