# Tổng hợp Chức năng Hiện tại — Sonca Audio Inspector

> **Phiên bản hiện tại:** `v1.5.7` (kênh `stable`, bản quyền `NGUYỄN VĂN LỘC`)  
> **Commit tham chiếu:** `d966d48` (`flagDeviceTab`)  
> **Ngày cập nhật:** `2026-11-20`  
> **Tài liệu liên quan:** [HANDOFF.md](../HANDOFF.md) · [note/claude/notes.md](../note/claude/notes.md) · [CONFIG_TESTS.vi.md](CONFIG_TESTS.vi.md) · [UPDATE.vi.md](UPDATE.vi.md)

### Quy ước cập nhật tài liệu này
Đây là tài liệu sống (**living document**) tổng hợp chức năng thực tế của ứng dụng theo mã nguồn, cấu hình và tài liệu dự án. Khi có tính năng mới, chỉnh sửa hành vi, bật/tắt cờ giao diện hoặc bỏ tính năng cũ:
1. Cập nhật **Phiên bản hiện tại**, **Commit tham chiếu** và **Ngày cập nhật** ở phần đầu tài liệu.
2. Bổ sung hoặc chỉnh sửa trực tiếp vào đúng **Mục 1–9** tương ứng (giữ nguyên đường dẫn tương đối trong repo).
3. Thêm một dòng tóm tắt vào bảng ở **Mục 10. Lịch sử cập nhật chức năng (Changelog)**.

---

## 1. Tổng quan ứng dụng & Trạng thái hiện tại

- **Tên ứng dụng:** **Sonca Audio Inspector** (`Sonca Audio Routing`), phiên bản **`v1.5.7`** ([SoncaAudioInspector.csproj](../SoncaAudioInspector.csproj)), build `stable`.
- **Nền tảng kỹ thuật:** C# / WPF trên `.NET 9` (`net9.0-windows10.0.19041.0`, self-contained `win-x64` cho Release Portable), dùng **NAudio 2.3.0** (WASAPI), **MathNet.Numerics + Intel MKL x64** (fallback FFT AVX/managed trong [SweepFourier.cs](../src/Measurements/Sweep/SweepFourier.cs)), **ScottPlot.WPF 5.1.58**, **ZXing.Net**, và **Windows DPAPI**.
- **Hệ sinh thái server:** Đồng bộ với `https://inventory.acnos.store` (web quản trị cấu hình model, duyệt bản thử/bản tạm, báo cáo QA/QC & quản lý kho sản phẩm).
- **Workspace tham chiếu ngoài (`AcnosEffect`):** Project WinForms cũ `AcnosMXApp\AcnosEffect` được dùng làm tham chiếu giao thức USB HID / Control Word cho tab `Device` (hiện đang ẩn sau cờ `flagDeviceTab`).

---

## 2. Luồng khởi động, Xác thực & Giao diện chính (Shell)

### 2.1. Khởi động & Chẩn đoán hệ thống ([SplashWindow.xaml.cs](../src/UI/Shell/SplashWindow.xaml.cs))
- **SYS001 — Xác thực máy trạm:** Đọc `verify.txt` một lần duy nhất, gọi `POST /api/app/verify`, mã hóa và lưu `appApiKey` + thông tin bootstrap bằng **Windows DPAPI** (`CurrentUser`) vào Registry `HKCU\Software\SoncaAudioInspector\Auth` (ACL riêng user hiện tại), sau đó ghi đè ngẫu nhiên và xóa an toàn `verify.txt`. Có giao diện hỗ trợ copy đường dẫn, mở thư mục hoặc tạo file `verify.txt` mẫu khi thiếu.
- **SYS002 — Cấu hình trạm có chữ ký:** Tải bắt buộc `checking_config.json` từ feed ký **ECDSA P-256** (`config/latest.json`). Chặn vào app nếu máy mới chạy lần đầu và ngoại tuyến; nếu đã từng xác minh, cho phép chạy cache với cảnh báo offline.
- **SYS003 — Thiết bị âm thanh:** Trì hoãn nạp thiết bị sang lúc mở `Audio Routing` để cửa sổ đăng nhập hiện nhanh hơn.

### 2.2. Đăng nhập ([LoginWindow.xaml.cs](../src/UI/Shell/LoginWindow.xaml.cs))
- Đăng nhập tài khoản nhân sự (`POST /api/app/login`), gọi `GET /api/app/me` để lấy vai trò (`STAFF` / `ADMIN`), rồi tải cấu hình **ACTIVE** có chữ ký từ server trước khi mở cửa sổ chính.
- Bật sẵn **"Ghi nhớ và tự đăng nhập trên máy này"** (`v1.5.5+`, lưu bằng DPAPI); nút **Đăng xuất** ở cửa sổ chính thu hồi phiên (`POST /api/auth/logout`) và xóa thông tin ghi nhớ.

### 2.3. Cửa sổ chính & Điều hướng ([MainWindow.xaml](../src/UI/Shell/MainWindow.xaml), [MainWindow.xaml.cs](../src/UI/Shell/MainWindow.xaml.cs), [MainWindow.PhaseA.cs](../src/UI/Shell/MainWindow.PhaseA.cs))
- **Tab hoạt động mặc định:** Chỉ hiển thị tab **`Audio Routing`**. Các tab **`Device`** (USB HID QC) và **`Quét Barcode`** mặc định bị ẩn (`Collapsed` / `flagDeviceTab = false`).
- **Chọn Model & Chuẩn bị phần cứng:** Chọn model từ `ComboModels` → làm mới cấu hình ACTIVE từ server (timeout 4 s), gộp cấu hình thử trong phiên nếu có, chuẩn bị kết nối BLE theo cấu hình model, đồng bộ ngõ thiết bị vào `AudioRouting`, theo dõi thiết bị âm thanh realtime qua `MMDeviceEnumerator` (`IMMNotificationClient`), hiện popup **"Chờ thiết bị"** (căn giữa cửa sổ) nếu thiếu thiết bị, cảnh báo khi phát hiện ứng dụng ngoài đang chiếm thiết bị thu (`AudioSessionDiagnostics`), và tự focus vào ô `Serial / quét QR`.
- **Quét Serial / QR & Xác nhận QA trước đo:**
  - Tự phân biệt đầu vào từ **máy quét barcode** (khoảng cách phím ≤ 100 ms từ 3 ký tự trở lên) vs nhập tay; hỗ trợ giải mã QR BOM (`ACNOS:BOM:1:...`) và đối khớp model.
  - Nhấn `Enter` hoặc bấm Auto Test tra trạng thái sản phẩm/QA từ server; mở popup [PreviousQaWindow.cs](../src/UI/Dialogs/PreviousQaWindow.cs) để chọn `P`/`F`/`N` và nhập ghi chú người đo (tối đa 4.000 ký tự).
- **Bảo vệ mất thiết bị & Tương thích màn hình:**
  - Tự chờ hồi phục **2 giây** (USB/wired) hoặc **6 giây** (Bluetooth) trước khi dừng phiên đo khi thiết bị tạm ngắt kết nối.
  - Co giãn vừa 100% vùng làm việc màn hình không che taskbar ([MainWindow.ScreenFit.cs](../src/UI/Shell/MainWindow.ScreenFit.cs)); mọi cửa sổ phụ được giới hạn tối đa **90% chiều cao màn hình** ([WindowHeightLimit.cs](../src/UI/Shell/WindowHeightLimit.cs)).
- **Nút dành cho Quản trị viên & Trạng thái thử:**
  - `Chờ duyệt (N)` (`BtnPendingConfigs`): chỉ hiện khi tài khoản có quyền `ADMIN`, mở [ConfigApprovalWindow.cs](../src/UI/Dialogs/ConfigApprovalWindow.cs).
  - `TrialConfigBanner`: thanh cảnh báo màu hổ phách hiện khi model đang dùng cấu hình thử nghiệm (RAM hoặc bản `PENDING` từ server), kèm nút bỏ cấu hình thử.

### 2.4. Bảng phím tắt ([AppShortcuts.cs](../src/UI/Shell/AppShortcuts.cs) & trong các màn hình con)
| Phím tắt | Chức năng | Phạm vi |
|---|---|---|
| `Ctrl + /` | Mở bảng tra cứu phím tắt ([ShortcutsHelpWindow.cs](../src/UI/Shell/ShortcutsHelpWindow.cs)) | Toàn ứng dụng |
| `Ctrl + Shift + H` | Mở bảng điều khiển phần cứng loa BLE ([SoncaBleMixerWindow.xaml.cs](../src/Ble/SoncaBleMixerWindow.xaml.cs)) | Toàn ứng dụng |
| `Ctrl + Shift + M` | Chỉnh sửa cấu hình model đang chọn ([ModelConfigEditorWindow.cs](../src/Configuration/ModelConfigEditorWindow.cs)) | Cửa sổ chính |
| `Ctrl + Shift + N` | Tạo mới cấu hình model từ mẫu | Cửa sổ chính |
| `Ctrl + Shift + I` | Mở cửa sổ phân tích chuyên sâu 10 tab ([MeasurementDetailsWindow.cs](../src/UI/Measurements/MeasurementDetailsWindow.cs)) | Audio Routing |
| `Ctrl + Shift + T` | Mở bài kiểm tra 5 bước núm/nút loa Subwoofer ([SubwooferControlsWindow.cs](../src/UI/Measurements/SubwooferControlsWindow.cs)) | Audio Routing |
| `Enter` | Xác nhận Serial / QR và tra QA | Ô Serial |
| `Esc` | Thu nhỏ đồ thị Freq / THD đang phóng to | Cửa sổ chính |
| `Ctrl + +` / `-` / `0` | Phóng to / thu nhỏ / đặt lại đồ thị | Đồ thị Freq / THD |

---

## 3. Chức năng Đo kiểm Âm thanh & Tự động hóa

### 3.1. Audio Engine & Bảo vệ phần cứng (`src/Audio/`)
- **Thu/Phát WASAPI ([AudioEngine.cs](../src/Audio/AudioEngine.cs)):**
  - **Phát:** Luôn dùng **WASAPI Shared** (`44.1 kHz` hoặc `48 kHz`), hỗ trợ chọn kênh phát (`L+R`, `L`, `R`) và chỉnh mức phát `-60…0 dBFS`.
  - **Thu:** Hỗ trợ **WASAPI Shared** (chế độ polling tránh leak wait-handle trên NAudio 2.3) và **WASAPI Exclusive** (bắt buộc cho bài Rè để bỏ qua Windows APO / Voice Clarity; nếu lỗi `0x8889000A` do thiết bị đang bị chiếm thì tự fallback sang Shared qua [RubBuzzCaptureFallback.cs](../src/Audio/RubBuzzCaptureFallback.cs) và ghi nhận cảnh báo). Hỗ trợ chọn kênh thu (`Trộn`, `Mic 1`, `Mic 2`).
- **Xử lý đặc thù FastTrack Pro & Môi trường Windows:**
  - [SharedCaptureSession.cs](../src/Audio/SharedCaptureSession.cs): Giữ luồng thu Shared liên tục cho **M-Audio FastTrack Pro** giữa các lượt đo nhằm tránh lỗi đảo byte PCM16 khi đóng/mở luồng; [CaptureDataIntegrity.cs](../src/Audio/CaptureDataIntegrity.cs) phát hiện và loại bỏ bản thu hỏng byte-order (không tự đảo byte giả PASS).
  - [FastTrackDeviceSetup.cs](../src/Audio/Devices/FastTrackDeviceSetup.cs) & [AudioSampleRateSetup.cs](../src/Audio/Devices/AudioSampleRateSetup.cs): Nhận diện cặp `Analog Connector 1/2` qua `DeviceTopology` bất kể đổi cổng USB, tự đồng bộ `DeviceFormat` / `MixFormat` về `44.1 kHz` (và xin xác nhận [MeasurementSampleRateConsent.cs](../src/Measurements/MeasurementSampleRateConsent.cs) khi thiết bị chạy ở tần số mẫu khác).
  - [CapturePathEnhancements.cs](../src/Measurements/Diagnostics/CapturePathEnhancements.cs): Kiểm tra Registry Windows (`FxProperties`) cảnh báo khi bật Audio Enhancements / APO (`VocaEffectPack`).
  - [AudioSessionDiagnostics.cs](../src/Audio/AudioSessionDiagnostics.cs): Quét phiên thu/phát ngoài đang chiếm thiết bị.
  - [WindowsAudioLevels.cs](../src/Audio/Devices/WindowsAudioLevels.cs): Áp đặt mức âm lượng Windows phát/thu theo cấu hình từng bài (`ModelTestVolume`) và tự khôi phục đúng giá trị ban đầu khi kết thúc.
  - **Headroom Ngõ thu ([RewRealtimeLevelMeter.xaml.cs](../src/UI/Dialogs/RewRealtimeLevelMeter.xaml.cs)):** Đồ thị cuộn thời gian thực ngay tại sidebar `Cấu hình đường âm thanh` (có nút `Tạm dừng` / tự phục hồi).
- **Hiệu chuẩn Micro ([MicrophoneCalibrationLibrary.cs](../src/Measurements/Calibration/MicrophoneCalibrationLibrary.cs), [TestMicrophoneCalibration.cs](../src/Measurements/Calibration/TestMicrophoneCalibration.cs)):**
  - Nạp file hiệu chuẩn `.txt` chuẩn REW/miniDSP/UMIK-1 (tự gắn [calibrations/99-00192.txt](../calibrations/99-00192.txt) cho KT USB), hỗ trợ lưu theo thiết bị thu cục bộ hoặc nhúng thẳng (Base64 + SHA-256) vào `TestConfig` của model. Không áp vào phép đo điện tử FastTrack / `RECORDING TYPE C`.

### 3.2. Đo Đáp tuyến tần số (FR), Méo hài (THD) & LINE CHUẨN
- **1 lượt Log-Sweep Farina ([StandardAcousticMeasurement.cs](../src/Measurements/Sweep/StandardAcousticMeasurement.cs), [TestRunner.cs](../src/Automation/TestRunner.cs)):**
  - Phát log-sweep `18 Hz–22 kHz` (mặc định `64K` mẫu ≈ 1,49 s @ 44.1 kHz; hỗ trợ `64K`, `128K`, `256K`, `512K`, `1M`), lọc subsonic 10 Hz, ước lượng & bù lệch xung nhịp (`≥ 20 ppm` bằng sinc resampling), giải chập **Wiener** (cho FR / IR / Decay / Waterfall) và **bộ lọc ngược Farina** (tách hài `H2–H9` & `H10–H35` từ cùng 1 bản thu).
  - Hiển thị đáp tuyến `20 Hz–20 kHz` làm mượt `1/24 octave` (chuột phải chuyển `No smoothing`), giữ mức quy đổi sine RMS `dBFS` tuyệt đối, hỗ trợ đo nhiều lượt đồng pha (`LogSweepRuns` 1–3).
  - Chấm THD tại **`1000 Hz`** (mặc định), tùy chọn thêm **`80 Hz`** (`THD_80` / `BassThdLimit`) và **`4000 Hz`** (`THD_4000` / `TrebleThdLimit`); khung `DISTORTION` cho phép kiểm tra 14 mốc tần số khác nhau (`ComboThdInspectFreq`), tùy chọn bù đáp tuyến mic và tham chiếu tần số hài.
  - Vẫn hỗ trợ chế độ **Multitone** (pha Schroeder/Newman, tích phân năng lượng 5-bin) và **Stepped Sine** (hồi quy sine-fit tính `THD`, `THD+N`, `SINAD`, `SNR`, `SFDR`, `DC offset`, `Crest Factor`, `% clipping` trong [AdvancedAudioMeasurement.cs](../src/Measurements/AdvancedAudioMeasurement.cs)).
- **Tìm & Lưu LINE CHUẨN ([ReferenceCurveSelector.cs](../src/Measurements/ReferenceCurveSelector.cs)):**
  - Đo 2–3 lượt, chọn cặp 2 lượt có độ lệch lớn nhất nhỏ hơn hoặc bằng `3 dB` (không trừ chênh lệch mức) để lấy trung bình; lưu `.csv` kèm file sidecar `.json` (dữ liệu thô, SHA-256, `impulseEffectsReference`, `polarityReference`, fingerprint cấu hình). Giữ nguyên khớp chuẩn khi đổi số thứ tự bài đo qua `ReferenceTestId`.
- **Chính sách chấm PASS/FAIL ([ResponseLimitPolicy.cs](../src/Measurements/ResponseLimitPolicy.cs), [LineOutlierPolicy.cs](../src/Measurements/LineOutlierPolicy.cs), [TestRunPolicy.cs](../src/Automation/TestRunPolicy.cs)):**
  - Chấm trong dải `freq_range` (mặc định `50–18000 Hz`), `CriticalZones` (chạm giới hạn là FAIL ngay), cho phép tỉ lệ ngoại lệ `AllowLineOutliers` (`x%`), tối đa **12 khoảng hõm `DipWindows`** (`LowerOnly` bỏ giới hạn dưới + nối cầu giới hạn trên, hoặc `Exclude`), trung bình theo dải `BandAverage` (Bass / Mid / Treble), bỏ phiếu nhiều lượt `MaximumRuns / RequiredPasses` (mặc định `2 / 1`), và cho phép đạt sớm sau lượt 1 khi dư biên độ `SingleRunPassMarginDb`.

### 3.3. Chuỗi Auto Test Điện tử & Bán tự động qua BLE (C360 / SYMPHONY / ACOUSTIC 3)
Triển khai trong [AudioRouting.C360.cs](../src/Routing/AudioRouting.C360.cs) và [C360ElectronicSuite.cs](../src/Automation/Electronic/C360ElectronicSuite.cs) (cùng `Acoustic3ModelDefinition`, `SymphonyModelDefinition`):
- **9 bài đo của model C360 (cấu hình server ACTIVE serial 12):**
  1. `IO001` (`MicEffects`): Đo **Echo & Reverb** đường MIC IN từ đáp ứng xung IR ([ImpulseEffects.cs](../src/Measurements/Sweep/ImpulseEffects.cs)); nếu reverb từ sweep chưa đủ rõ thì chạy bổ sung chuỗi burst nhiễu ngắt quãng so sánh dry/wet 2 dải tần ([ReverbDecayDiagnostics.cs](../src/Measurements/Diagnostics/ReverbDecayDiagnostics.cs)).
  2. `IO002` (`MicFrequencyShift`): Đo **chống hú di tần FBX** bằng 3 sóng mang đồng thời `700 / 1700 / 3700 Hz` ở trạng thái FBX TẮT và BẬT, tách lệch xung nhịp bằng hồi quy ([FrequencyShiftDiagnostics.cs](../src/Measurements/Diagnostics/FrequencyShiftDiagnostics.cs)).
  3. `IO003` (`MicDry`): Đáp tuyến & THD đường MIC IN không effect.
  4. `IO004` (`LineIn`): Đáp tuyến & THD ngõ LINE IN.
  5. `IO005` (`UsbA`): Đáp tuyến & THD ngõ USB A (`CS3600 MUSIC`).
  6. `IO006` (`AcousticMic`): Đáp tuyến & THD thu qua micro đo thực tế.
  7. `IO007` (`Bluetooth`): Tự ghép nối Bluetooth Classic Stereo A2DP, đo Đáp tuyến & THD qua BT, kết thúc phiên hỏi hoặc tự xóa BT Classic (giữ kết nối BLE).
  8. `IO008` (`AcousticRubBuzz`): Bài **Rè âm học riêng** — chạy **Pink 25–150 Hz** (3 lượt) + **Chirp 20–2000 Hz** (3 lượt), thu Exclusive (tự fallback Shared nếu bị chiếm), chấm hoàn toàn theo ngưỡng tuyệt đối (mặc định Pink < 30 %, Chirp < 10 dB, không dùng LINE CHUẨN), ảnh QA gửi server là đồ thị chirp.
  9. `IO009` (`MicFeedbackGain`, mặc định **Tùy chọn**): Tạo vòng lặp `mic đo → MIC IN → loa → mic đo`, tăng gain từng bước lúc FBX TẮT và BẬT để đo độ lợi thêm trước khi hú (`AddedDb`), phân loại mức hú nhẹ/vừa/mạnh ([FeedbackGainDiagnostics.cs](../src/Measurements/Diagnostics/FeedbackGainDiagnostics.cs)); dòng bài hiển thị màu xám, không làm đổi kết luận tổng.
- **Tự động hóa vận hành (`v1.5.5–v1.5.7`):**
  - **Tự nối BLE & xác minh ngõ:** Quét BLE 4 giây chọn loa RSSI mạnh nhất khớp `BleNamePrefix` + xác nhận `FirmwareModelId` (`C360 = 1075`, `SYMPHONY = 1062`, `ACOUSTIC 3 = 1071`), kiểm tra chuyển đủ ngõ trên loa, áp âm lượng BLE (`MASTER/MUSIC/MIC`) và Windows theo từng bài rồi tự khôi phục trong `finally`.
  - **Tự dò cặp dây sound card ([C360PortProbe.cs](../src/Automation/Electronic/C360PortProbe.cs)):** Đầu phiên phát burst ngắn 0,3 s ra từng cặp Analog của FastTrack, dùng matched filter (`≥ 24 dB`, `≥ -55 dBFS`) tự phát hiện và đổi cặp `LINE IN` / `MIC IN` khi cắm đảo dây.
  - **Tự lấy LINE CHUẨN khi thiếu:** Hỏi 1 lần đầu phiên nếu thiếu chuẩn cho các bài đáp tuyến (bài Effect, FBX, Rè IO008, Gain trước hú IO009 không dùng LINE CHUẨN).
  - **Quy tắc kết luận & Đo lại riêng bài lỗi (`v1.5.7`, [C360VerdictPolicy.cs](../src/Automation/Electronic/C360VerdictPolicy.cs), [RetestInvalidStepDialog.cs](../src/UI/Dialogs/RetestInvalidStepDialog.cs)):**
    - Có ≥ 1 bài bắt buộc `FAIL` ⇒ tổng là **KHÔNG ĐẠT** và gửi server ngay (các bài chưa kết luận gửi `PENDING` kèm `"CHƯA KẾT LUẬN — …"`, bài tùy chọn gửi `PENDING` kèm `"TÙY CHỌN — …"`).
    - Bấm vào bài `ĐO KHÔNG HỢP LỆ` (`INVALID` / `NO_SIGNAL` / `INCONCLUSIVE`) để đo lại riêng bài đó với mức ghi đè Windows/BLE tạm thời (gợi ý +20%), tính lại kết luận và cập nhật đúng bản ghi trên server qua `uploadSessionId`.
    - Ghi chú gửi server ngắn gọn chỉ liệt kê lỗi; thao tác gửi chạy nền không đơ UI; lưu bền `qa-completion-pending.json` để bấm **GỬI LẠI QA** khi mất mạng ([AudioRouting.QaCompletion.cs](../src/Routing/AudioRouting.QaCompletion.cs)).

---

## 4. Bộ công cụ Phân tích & Chẩn đoán Chuyên sâu

### 4.1. Cửa sổ Chi tiết 10 Tab (`Ctrl + Shift + I` — [MeasurementDetailsWindow.cs](../src/UI/Measurements/MeasurementDetailsWindow.cs))
| # | Tab | Chức năng chính |
|---|---|---|
| 1 | **Impulse / ETC / Schroeder** | Đồ thị IR biên độ log, đường bao ETC, cửa sổ `Tukey25`/`Half-Hann`, tích phân Schroeder, hồi quy `EDT / T20 / T30` (`R² ≥ 0,90`), chỉ số `C20 / C50 / C80 / D50 / Ts`, thanh trượt zoom ([ImpulseAnalysisWindow.cs](../src/UI/Measurements/ImpulseAnalysisWindow.cs)). |
| 2 | **Tổng quan / Thu / Effect** | Bảng thông số đầy đủ: route, định dạng, sample rate, mức thu, SNR, lệch clock, echo taps, kiểm tra reverb dry/wet, chẩn đoán lỗi. |
| 3 | **Cực tính** | Kiểm tra cực tính từ chính bản thu sweep và đối chiếu với LINE CHUẨN cùng tuyến đo ([SignalDiagnosticViews.cs](../src/UI/Measurements/SignalDiagnosticViews.cs)). |
| 4 | **IMD · Hai tone** | Đo méo xuyên điều chế SMPTE 4:1 (`60 Hz + 7 kHz`), tính tỉ lệ biên dải bậc 2 & 3. |
| 5 | **Compression / Limiter** | Phát tone `1 kHz` ở 2 mức liên tiếp (`-24` → `-18 dBFS`) để phát hiện nén / limiter hoặc sai lệch tăng gain. |
| 6 | **EDT / T20 / T30** | Bảng chi tiết thời gian vang và hệ số hồi quy `R²`. |
| 7 | **Phase / Group delay** | Bảng pha và trễ nhóm trong dải `50–18000 Hz`. |
| 8 | **Impedance · Ω** | Đo trở kháng loa 2 kênh qua điện trở mẫu `Rsense` (`20 Hz–20 kHz`, `-42 dBFS`), hiệu chuẩn `Open / Short / Known-R`, vẽ `|Z|`, `R`, `X`, `Phase`, xuất CSV, so sánh điện trở nối tiếp loa treble ([ImpedanceMeasurement.cs](../src/Measurements/Impedance/ImpedanceMeasurement.cs), [ImpedanceWindow.cs](../src/Measurements/Impedance/ImpedanceWindow.cs)). *(Mới kiểm chứng mô phỏng, chưa xác minh gá phần cứng thật.)* |
| 9 | **Rè (Rub & Buzz)** | Giao diện hợp nhất ([RubBuzzView.cs](../src/UI/Measurements/RubBuzzView.cs)): chọn route + nút **"Chuẩn bị loa như IO008"**; 3 nút đo chuẩn (**Đo chuẩn Pink+Chirp**, **Chỉ Pink**, **Chỉ Chirp**); chỉnh ngưỡng nhiễu nền trạm ([RubBuzzBackgroundDialog.cs](../src/UI/Measurements/RubBuzzBackgroundDialog.cs)); **nghe lại tách tiếng rè** ra loa kiểm âm riêng có con trỏ đồng bộ đồ thị ([RubBuzzAudioPlayer.cs](../src/Audio/RubBuzzAudioPlayer.cs)); lưu line chuẩn rè (khi tick xác nhận loa tốt) & xuất WAV/JSON; **chẩn đoán tự động 10 dấu hiệu S1–S10** ([RubBuzzDiagnosis.cs](../src/Measurements/Diagnostics/RubBuzzDiagnosis.cs)). Trong phần mở rộng **"Chẩn đoán nâng cao"** có thêm: **Chirp Impulsive (IEC 60268-21 tracking HP `N·f`)**, **Broadband Pink (IEC/AES)**, **Chẩn đoán theo mức (`-30…-12 dBFS`)**, và 4 tab con: **H10–H35 từ sweep**, **Hạt rác (Loose Particle)**, **Rò khí (Air Leak ALD)**, **Pink nghe thử v1.1** (xuất WAV PCM24). |
| 10 | **Phiên bản** | Xem phiên bản, ngày phát hành, release notes (`version-info.json`), chuyển kênh **Ổn định / Beta**, kiểm tra cập nhật, mở lịch sử `/update`, và **Cài phiên bản khác / Hạ cấp** ([VersionInfoView.cs](../src/UI/Measurements/VersionInfoView.cs)). |

### 4.2. Kiểm tra núm/nút loa Subwoofer (`Ctrl + Shift + T`)
- [SubwooferControlsWindow.cs](../src/UI/Measurements/SubwooferControlsWindow.cs) & [SubwooferControlAnalysis.cs](../src/Measurements/Diagnostics/SubwooferControlAnalysis.cs): Quy trình 5 bước có hình vẽ mặt sau loa tô sáng từng núm/nút (**Baseline 180 Hz · 0 ms · 0° → Crossover 40 Hz → Delay 20 ms → Phase 180° + hỏi trạng thái đèn LED → Trả về ban đầu**). Dùng tỉ số hàm truyền tương đối `Y_b / Y_a` nên không cần line chuẩn; tự phát hiện quên vặn trả núm; vẽ đồ thị biên độ + pha đã bù trễ; tự lưu WAV và `result.json` vào `captures/subwoofer-*`.

### 4.3. Oscilloscope & Sine Check 1 kHz
- [RoutingScopeWindow.xaml.cs](../src/UI/Scope/RoutingScopeWindow.xaml.cs), [ScopeWavPlayer.cs](../src/UI/Scope/ScopeWavPlayer.cs): Dao động ký ngõ thu có trigger cạnh lên (hysteresis), chỉnh `ms/div` & `V/div`, auto-fit `VỪA SÓNG`, phát độc lập file [sine-1000Hz-minus12dBFS-RMS.wav](../Assets/Audio/sine-1000Hz-minus12dBFS-RMS.wav) qua WASAPI Shared mà không ảnh hưởng trạng thái `AudioEngine`, hiển thị Vpp / RMS / tần số / headroom / cảnh báo clipping & click.

### 4.4. Các phép đo bổ sung theo cấu hình bài
- Qua [AdditionalTestRegistry.cs](../src/Automation/AdditionalTestRegistry.cs) & [ConfiguredAcousticTests.cs](../src/Automation/ConfiguredAcousticTests.cs): Từng bài đo hỗ trợ bật/tắt `TestEffect`, `TestAirLeak`, `TestLooseParticle` (dùng chung bản thu nếu cùng tần số/mức/thời lượng), `TestSoundLevel` (độ lệch mức `dBFS`), `TestRubBuzz` (`PinkNoiseChirp` / `PinkNoise` / `Chirp` / `SteppedSine`), và `NoiseDiagnostics` (thu đồng thời 2 mic DUT + Ambient đo nhiễu nền & hum 50/60 Hz).

---

## 5. Điều khiển phần cứng BLE & Bluetooth Classic

- **Sonca BLE Mixer (`Ctrl + Shift + H` — [SoncaBleMixerWindow.xaml.cs](../src/Ble/SoncaBleMixerWindow.xaml.cs), [SoncaBleManager.cs](../src/Ble/SoncaBleManager.cs)):**
  - Giao thức GATT service `0x5343` (fallback `0xFFF0`), đóng khung gói CRC-8 CCITT ([SoncaBleProtocol.cs](../src/Ble/SoncaBleProtocol.cs)), đọc toàn bộ snapshot thanh ghi DSP (`0x6209=1`), bắt tay `SCA=INFO` nhận diện mã model firmware từ [firmware-models.json](../Assets/firmware-models.json) (có nút tải cập nhật `model_profile.txt` trực tuyến hoặc import file).
  - Điều khiển 6 fader (**MIC, ECHO, REVERB, MUSIC, MASTER DSP, RECORD**) + Mute, 6 ngõ vào (**BT, USB A, OPTICAL, HDMI, LINE IN, THẺ NHỚ/KARAOKE**), 3 công tắc bitmask `0x6254` (**Mic FBX, Bass Boost, Enhancer** — [SoncaBleMixerProfile.cs](../src/Ble/SoncaBleMixerProfile.cs)), và 19 thanh ghi DSP Main48 (**Bass/Mid/Treble** cho MIC/MUSIC/RECORD; 5 tham số Echo; 6 tham số Reverb — [SoncaMixerControls.cs](../src/Ble/SoncaMixerControls.cs)). Mọi lệnh ghi chờ 100 ms rồi đọc lại snapshot xác nhận.
  - Hỗ trợ tìm & kết nối tự động theo **MAC ghép cặp** (`ble_addr ^ 0x010000000000`), theo **Model ID RSSI mạnh nhất**, hoặc theo **tiền tố tên BLE (`BleNamePrefix`) + xác minh Model ID**.
- **Bluetooth Classic Audio ([src/Bluetooth/README.md](../src/Bluetooth/README.md)):**
  - Tự ghép nối ([WindowsBluetoothPairing.cs](../src/Bluetooth/WindowsBluetoothPairing.cs)), kích hoạt dịch vụ A2DP AudioSink qua `bthprops.cpl`, đối khớp đúng endpoint **Stereo** theo `ContainerId`/MAC trên cây thiết bị PnP (`cfgmgr32.dll`, bỏ qua Hands-Free mono).
  - Kết thúc Auto Test có thể hỏi hoặc tự động xóa thiết bị Bluetooth Classic theo đúng MAC loa vừa đo mà **giữ nguyên phiên BLE**; sidebar có nút xóa nhanh toàn bộ BT cũ của model.

---

## 6. Quản trị Cấu hình, Tích hợp Server & Tự động Cập nhật

- **Kiến trúc cấu hình 3 lớp:**
  1. **Base Feed ký ECDSA P-256 (`SYS002`, [ServerConfig.cs](../src/Infrastructure/Update/ServerConfig.cs)):** Chống hạ `serial`, ghi đè bản cục bộ để đồng nhất trạm (máy đặt `SONCA_CONFIG_ADMIN=1` được giữ bản cục bộ).
  2. **Bản `ACTIVE` theo model ([ActiveAudioConfigurations.cs](../src/Configuration/ActiveAudioConfigurations.cs)):** Tải từ `GET /api/app/audio-config/active`, xác minh chữ ký **ECDSA P-256** (khóa [config-signing-public.pem](../Assets/config-signing-public.pem)), ghép đè model tương ứng lên base feed và dọn cache cũ.
  3. **Cấu hình thử nghiệm ([SessionModelConfigurations.cs](../src/Configuration/SessionModelConfigurations.cs)):** Lưu trên RAM hoặc bản `PENDING` từ server, tách riêng thư mục `trial standards/`, `test data/` và endpoint `POST /api/app/qa-audio/trial` để không lẫn dữ liệu sản xuất.
- **Trình sửa & duyệt cấu hình:**
  - [ModelConfigEditorWindow.cs](../src/Configuration/ModelConfigEditorWindow.cs) (`Ctrl+Shift+M` / `Ctrl+Shift+N`): 9 sub-tab cấu hình chi tiết + tab JSON nâng cao, kéo thả thứ tự bài + đánh số lại bảo toàn `ReferenceTestId`, preview đồ thị `DipWindows`, kiểm tra khớp tên thiết bị Windows ([ModelEndpointCheck.cs](../src/Configuration/ModelEndpointCheck.cs)), lưu bản thử lên server.
  - [ConfigApprovalWindow.cs](../src/UI/Dialogs/ConfigApprovalWindow.cs): Nút `Chờ duyệt (N)` cho `ADMIN`, xem diff từng trường, duyệt/từ chối có lý do và xử lý `STALE_BASE` (409 rebase).
- **Đồng bộ QA & Lưu trữ ([ServerEngine.cs](../src/Infrastructure/Server/ServerEngine.cs), [LocalQaStorage.cs](../src/Infrastructure/Server/LocalQaStorage.cs)):**
  - Gửi kết quả Auto Test kèm tối đa 16 bài/ảnh đồ thị PNG lên `/api/app/qa-audio` (hoặc `/trial`), kiểm tra biên nhận `logId` + danh sách ảnh trả về, lưu hàng đợi retry offline (`.pending-audio-qa.json`), tự dọn ảnh đã gửi sau 30 ngày, heartbeat giữ phiên 60 s, tự refresh JWT khi 401.
- **Tự động cập nhật Delta ([UpdateClient.cs](../src/Infrastructure/Update/UpdateClient.cs), [UpdateInstaller.cs](../src/Infrastructure/Update/UpdateInstaller.cs), [UpdateSignature.cs](../src/Infrastructure/Update/UpdateSignature.cs)):**
  - Ký số bằng **ECDSA P-256** (khóa chính cho `latest.json` / `beta.json` / `versions.json`, và khóa `TempReleasePublicKey` riêng cho bản tạm do ADMIN duyệt trên `/temp-updates`).
  - Hỗ trợ 3 luồng: **Ổn định (`latest.json`)**, **Beta (`beta.json`)**, và **Bản tạm** (chỉ máy chọn kênh Beta mới nhận); hỗ trợ **Hạ cấp / cài phiên bản khác** từ danh mục có chữ ký `versions.json`.
  - Chỉ tải blob thay đổi theo SHA-256 (~8–9 MB thay vì ~162 MB ZIP), cài giao dịch có sao lưu `.old-*` và rollback khi lỗi; tuyệt đối không ghi đè dữ liệu trạm (`checking_config*.json`, `save standards`, `trial standards`, `calibrations`, `standards`, `fail data`, `test data`, log).

---

## 7. Các chức năng Đang ẩn / Chưa kích hoạt / Legacy trong mã nguồn

| Thành phần | Vị trí mã nguồn | Hiện trạng chi tiết |
|---|---|---|
| **Tab `Device` (USB HID + QC A Lai `0xFD`)** | [DeviceView.xaml](../src/Device/DeviceView.xaml), [DeviceView.xaml.cs](../src/Device/DeviceView.xaml.cs), `src/DeviceUsb/*` | **Đang ẩn** bởi `private bool flagDeviceTab = false;` trong [MainWindow.xaml.cs](../src/UI/Shell/MainWindow.xaml.cs). Code đã hoàn chỉnh: quét USB HID (`VID` chứa `8888`, Usage `0x55AA`, [HIDDev.cs](../src/DeviceUsb/HIDDev.cs)), bắt tay `0xFF` (tự xác thực pass `77-55-44-88`), đọc `CW_FW_INFO`, gửi nhóm lệnh QC `0xFD` (Reset/Đọc/Cài 4-byte hex MIC 1 & MIC 2 ID; Đọc/Chuyển ngõ `0: BLUETOOTH`, `1: LINE IN`, `2: OPTICAL`) + khung log TX/RX hex. *(Lưu ý 1 lỗi nhỏ tại [DeviceView.xaml.cs#L314-L324](../src/Device/DeviceView.xaml.cs#L314-L324): nhánh `bFwType >= 0x31` trả `"Reserved"` trước khi tra từ điển, che mất `0x31 = BPxx Karaoke SDK Series`.)* |
| **Quét Barcode & BOM lắp ráp** | [QrScanWindow.xaml.cs](../src/UI/Shell/QrScanWindow.xaml.cs), [BomCsv.cs](../src/Infrastructure/BomCsv.cs) | **Đang ẩn** (`BtnScanQr`, `BtnAddProduct`, `BtnCheckStatus` đều `Collapsed` trong [MainWindow.xaml](../src/UI/Shell/MainWindow.xaml)). Code có sẵn: sơ đồ 2D tối đa 20 slot linh kiện, nhập 2 file Excel/CSV `BOM_MODELS + ITEMS`, liên kết linh kiện lên server, xuất tờ mã vạch `CODE_128` A4 300 DPI. |
| **Cửa sổ Đo Âm học kiểu REW** | [StandardMeasurementWindow.xaml.cs](../src/UI/Measurements/StandardMeasurementWindow.xaml.cs), `RewCheckLevelsDialog`, `RewLevelWarningDialog` | **Không khởi tạo khi chạy** (`_standardMeasurementView` luôn `null`). Bên trong có đủ 4 tab Measure, Generator, RTA, Scope nhưng hiện không mở được từ UI. |
| **Ngoại quan AI (YOLO ONNX)** | [VisualAI.xaml.cs](../legacy/VisualAI/VisualAI.xaml.cs), `models/visual-ai.onnx` | **Loại khỏi build** trong [SoncaAudioInspector.csproj](../SoncaAudioInspector.csproj) (`legacy\**`). API `POST /api/app/qa-visual` vẫn còn trong `ServerEngine`. |
| **Một số nút trên BLE Mixer** | [SoncaBleMixerWindow.Controls.cs](../src/Ble/SoncaBleMixerWindow.Controls.cs) | Các nút Preset (7 mục), Lưu/Reset cấu hình loa (`0x6001` / `0x6003`) cố ý khóa mờ vì chưa xác minh readback firmware. |

---

## 8. Hiện trạng thư mục `bin/Debug/` & Hệ sinh thái công cụ

### 8.1. Hiện trạng 5 thư mục trong `bin/Debug/`
| Thư mục trong `bin/Debug/` | Phiên bản EXE & Thời gian | Nội dung đáng chú ý |
|---|---|---|
| `net9.0-windows10.0.19041.0` | **v1.5.7** (`2026-11-20 13:48:57`) | **Bản Debug hiện tại** (build ngay trước commit `flagDeviceTab`). Chứa `checking_config.json` **30.078 byte** (bản feed server 4 model: `MI SAM`, `KBEATBOX CHILL`, `C360`, `SYMPHONY` — mới hơn bản test dự phòng 24.893 byte ở [checking_config.json](../checking_config.json)), 4 file WAV trong `Assets/Audio/`, [99-00192.txt](../bin/Debug/net9.0-windows10.0.19041.0/calibrations/99-00192.txt), khóa `config-signing-public.pem`, dữ liệu mẫu `save standards/` (`MI_SAM`, `D'AURIS_300`) và `fail data/20260915/`. |
| `SAVE_Dnet9.0-windows10.0.19041.0` | **v1.5.7** (`2026-11-20 13:32:08`) | Bản sao lưu Debug `v1.5.7`, có thêm `version-info.json` (release notes `v1.5.7`), `drivers/FastTrackPro_x64 Driver.rar`, `routing_value.json` và `checking_config.json` 24.893 byte. |
| `appQC_Lai` | **v1.0.0** (`+72944e9`, `2026-10-07`) | Bản build riêng từ nhánh **QC A Lai** (có tab `Device` gửi lệnh USB HID `0xFD`), cấu hình cũ 7 model (`MI SAM`, `ACOUSTIC 3`, `D'AURIS 200/300/500`, `KBEATBOX CHILL`, `CARRY ON`). |
| `appAudio_old` | **v1.0.0** (`+a0c00ef`, `2026-09-15`) | Bản cũ còn chứa model ONNX `models/visual-ai.onnx` (9,9 MB) của VisualAI. |
| `net9.0-windows` | **v1.0.0** (`+7cf8809`, `2026-07-21`) | Bản Debug cũ nhất (target chưa gắn SDK Windows 10), còn `visual-ai.onnx` và `fail data/` tháng 7/2026 (`DAURIS`, `CARRYON`, `CHILL`). |

### 8.2. Hệ sinh thái kiểm thử & phát hành (`tools/`, `scripts/`)
- **`tools/` (18 project riêng):** Bộ kiểm thử offline tự động (`AudioLifecycleCheck`, `StandardMeasurementCheck`, `PinkRubBuzzCheck` với 28 fixture thu thật từ UMIK-1, `FeedbackShiftCheck`, `FeedbackUiCheck` kiểm thử WPF headless, `ConfigAcceptanceCheck`, `ServerConfigCheck`, `MeasurementCheck`, `PinkNoiseCheck`, `CheckApp`), bộ audit cũ (`Audit20260909`, `Audit20260912TransientRew`), công cụ đồng bộ/đo trực tiếp (`AudioRoutingHardwareCheck`, `C360ConfigSync`), công cụ phát hành & kiểm tra updater live (`UpdateFeedTool`, `UpdateReleaseCheck`), và 2 công cụ độc lập giao tiếp REW REST API (`RewAudioInspector`, `RewNoiseBridge`).
- **`scripts/` (16 script PowerShell):** Chạy regression offline ([run-offline-checks.ps1](../scripts/run-offline-checks.ps1)), kiểm tra layout mã nguồn & skill, đóng gói portable, phát hành bản ổn định / vá nhanh / beta / bản tạm, deploy cấu hình ký & `agent-publish`, và quản lý/dọn `artifacts/` giữ 7 ngày.

---

## 9. Lưu ý kỹ thuật, Phạm vi chưa kiểm phần cứng & Tồn đọng

- **Ràng buộc đối chứng ([AUTO_TEST_PERFORMANCE.vi.md](../AUTO_TEST_PERFORMANCE.vi.md), [MEASUREMENT_FUNCTION_AUDIT.vi.md](MEASUREMENT_FUNCTION_AUDIT.vi.md)):** Không được suy diễn hoặc khẳng định ứng dụng cho kết quả tương đương hay chính xác hơn REW/Klippel chỉ từ việc build thành công hoặc pass các bài kiểm thử synthetic/offline.
- **Phần chưa đo kiểm trên trạm/phần cứng thật (theo [HANDOFF.md](../HANDOFF.md) & [notes.md](../note/claude/notes.md)):**
  - Bộ tính năng `v1.5.7` trên trạm thật: ngưỡng dò cặp dây FastTrack `C360PortProbe` (matched `≥ 24 dB`, `≥ -55 dBFS`), đo lại bài `INVALID` trên BLE thật và gửi cập nhật cùng `uploadSessionId`.
  - Mức `MIC IN` mới, `BLE MASTER 50%`, `IO008` và `IO009` (ngưỡng `6 dB` và thang hú nhẹ/vừa/mạnh là giá trị khởi điểm, chưa đo kiểm chuẩn hóa trên phần cứng thật).
  - Cần đo thêm ≥ 3 loa tốt cùng gá để chốt tiêu chí Rè `IO008`; bài đo trở kháng `Impedance` hiện mới xác minh qua mô phỏng.
- **Lỗi nền đã biết:** Check offline `ui-impedance` FAIL sẵn trên bàn đo của máy build từ trước 17/11/2026 (không chặn phát hành).
- **Hạng mục tồn đọng từ handoff 19/11:** Gửi nền **từng bài** ngay trong lúc đang đo (cần API mới phía web), bổ sung ảnh đồ thị + khoảng lặng TẮT/BẬT cho `IO009`, ô tick tạo bài IO mới trong `Ctrl+Shift+M`, THD `MIC IN` bằng sine, xử lý trường hợp `IO001` thiếu echo trên firmware, và hộp thoại hỏi *"Lưu mức này vào config?"* sau khi đo lại bài lỗi.

---

## 10. Lịch sử cập nhật chức năng (Changelog)

| Ngày | Phiên bản / Commit | Nội dung thay đổi chức năng |
|---|---|---|
| `2026-11-20` | `v1.5.7` (`d966d48`) | Khởi tạo tài liệu tổng hợp toàn bộ chức năng hiện tại từ mã nguồn `src/`, `docs/`, `note/claude/`, `checking_config.json`, `tools/`, `scripts/` và `bin/Debug/`. |
| *(Mẫu)* | *`v1.5.x` (`<sha>`)* | *Mô tả ngắn chức năng thêm/sửa/bỏ và cập nhật lại các mục liên quan ở trên.* |
