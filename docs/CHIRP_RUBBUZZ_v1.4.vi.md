# Rè / Rub & Buzz bằng chirp — v1.4 (chirp-rattle-1.0)

Bổ sung cho bài pink v1.3. Pink cho biết rè **có lặp lại theo bass** hay không; chirp (sine quét bass–trung, kiểu đo cuối dây chuyền
của ngành) cho biết rè **ở tần số nào**. Cơ sở đo thật: artifact `20261106-085907-845Z-rattle-listening` (README.vi.md).

## Vì sao REW/đường line không thấy rè
Đáp tuyến từ log sweep dùng giải chập (Farina): mọi năng lượng ở tần số cao hơn tần số sweep tức thời (hài, rè) bị dồn về trước
xung chính và bị cửa sổ IR cắt bỏ. Tiếng rè lại thấp hơn bass ~50 dB. Vì vậy sửa xong rè, đường line vẫn y như cũ.

## Cách đo
1. Thu WASAPI Exclusive (như pink). Nền 3 s, cùng luật nền thật (−100…−50 dBFS, không tắt dần).
2. Phát chirp log `F1Hz→F2Hz` (mặc định 20→2000 Hz, 3 s, −12 dBFS đỉnh) × `Repeats` (mặc định 3), cùng dạng sóng.
3. Theo từng khung 43 ms: tần số chirp tức thời f(t); năng lượng trên **10·f** (hài bậc ≥ 10 + rè) so với cơ bản (kiểu HOHD) và với nền.
4. Lưới 1/12 octave tới 1,8 kHz. **Đỉnh rè** = HOHD trừ xu hướng trơn của chính nó (trung vị ±1/2 octave), chỉ tính nơi cao tần vượt nền
   ≥ 3 dB, rồi lấy **nhỏ nhất qua các lượt** (rè thật lặp lại; tiếng động phòng chỉ ở một lượt).
5. Loa rè thật: chirp 10 s **28,1 dB @ 232 Hz**, chirp 3 s **22,8 dB @ 246 Hz** (người đo nghe thấy); cùng loa dưới ngưỡng rè 0,0 dB.

## PASS/FAIL (Ctrl+Shift+M → Rè → "Pink + Chirp · v1.4" hoặc "Chirp · v1.4")
- Line chuẩn chirp: C360/Acoustic 3 bấm **Tìm line chuẩn** trên loa tốt → tự lưu; chẩn đoán thủ công: Ctrl+Shift+I → **Rè (Rub & Buzz)** → **Đo chuẩn** → xác nhận loa tốt → **Lưu line chuẩn rè (loa tốt)**.
- Có chuẩn: FAIL khi đỉnh rè cao hơn chuẩn tại cùng tần số > `BumpMarginDb` (+6 dB). Không có chuẩn: PASS ≤ `AbsolutePassDb` (6 dB),
  FAIL ≥ `AbsoluteFailDb` (10 dB), giữa là CẦN DUYỆT. INVALID: nền bất thường, clipping, mức chirp lệch giữa lượt > 1,5 dB hoặc lệch chuẩn > ±3 dB.
- Chế độ Pink + Chirp mặc định: một lần thu nền 3 s, 3 lượt pink rồi 3 lượt chirp (chưa tính đệm thu); kết luận chung FAIL > INVALID > REVIEW > PASS.

```json
"TestRubBuzz": true, "RubBuzzMode": "PinkNoiseChirp",
"RubBuzzChirp": { "F1Hz": 20, "F2Hz": 2000, "DurationSeconds": 3, "PeakDbFs": -12, "Repeats": 3 },
"RubBuzzChirpLimits": { "BumpMarginDb": 6, "AbsolutePassDb": 6, "AbsoluteFailDb": 10, "ReviewFraction": 0, "DriveMatchDb": 3 }
```
Ngưỡng là đề xuất ban đầu; hiệu chỉnh bằng loa tốt/lỗi cùng gá. Kiểm tra: `tools/PinkRubBuzzCheck` (25 kiểm tra chirp, gồm dữ liệu UMIK-1 thật).

## Một tab Rè chuẩn (từ 2026-11-17)

Ctrl+Shift+I có đúng một tab **Rè (Rub & Buzz)** cho mọi công cụ rè:
- Nút chính **▶ Đo chuẩn** (`RunRubBuzz`) chạy bài theo `RubBuzzMode` của model (mặc định Pink + Chirp, 3 lượt). Bên cạnh là hai nút chuẩn tách riêng: **▶ Chỉ Pink (3 lượt)** (`RunRubBuzzPink`) và **▶ Chỉ Chirp (3 lượt)** (`RunRubBuzzChirp`) để đo từng tín hiệu mà vẫn giữ nguyên quy tắc đo chuẩn (`AllowsStandard = true`), bảng so và quyền lưu line chuẩn hiện có.
  Pink + Chirp là cách đã kiểm chứng trên loa rè thật: Pink bắt rè lặp lại theo bass (kể cả buzz liên tục không có xung, như loa rè 06/11), Chirp chỉ tần số rè.
- Mục thu gọn **Chẩn đoán nâng cao** (không kết luận ĐẠT/KHÔNG ĐẠT): chọn âm thanh chẩn đoán (Chẩn đoán theo mức, Chirp chậm, Sweep 512K, Pink dải rộng, Pink/Chirp riêng) rồi **▶ Đo chẩn đoán**;
  bên dưới là các tab con **H10–H35 · từ sweep**, **Hạt rác · LP**, **Rò khí · ALD**, **Pink nghe thử · v1.1** (trước là tab riêng, nay gộp vào đây, cách đo không đổi).

Với UIAutomation, mở Expander `RubBuzzAdvanced` trước khi tìm combobox `RubBuzzSound` hoặc nút `RunRubBuzzDiagnostic`;
chọn tab con trong `RubBuzzToolTabs`. Nút chính giữ id `RunRubBuzz`, hai nút tách tín hiệu giữ id `RunRubBuzzPink` và `RunRubBuzzChirp`. Đổi lựa chọn chẩn đoán không đổi chế độ của nút chính hoặc quyền lưu của kết quả đã đo.

## Tab gộp và chẩn đoán thủ công (v1.4)

Tab Rè có kết luận chung và hai phần Pink/Chirp, bảng so chuẩn và đồ thị.
Thanh chỉ đọc phía trên mọi tab luôn hiển thị ngõ phát/thu, kênh và sample rate đang chọn tại Audio Routing; mở cửa sổ,
đổi tab hoặc đổi lựa chọn đường âm thanh sẽ làm mới thanh này. Route trong kết quả vẫn là route thực tế của lần thu.

Nút **Đo chuẩn** mặc định theo chế độ bài đang chọn (Pink/Chirp), còn lại là **Pink + Chirp**.
Combobox **Âm thanh chẩn đoán** trong **Chẩn đoán nâng cao** mặc định **Chẩn đoán theo mức**. Bảy lựa chọn:

- Pink + Chirp: dùng `RubBuzzPink` và `RubBuzzChirp` của bài đang chọn.
- Pink 25–150 Hz: rè lặp lại.
- Chirp 20→2000 Hz · 3 s: tần số rè (dùng thiết lập chirp của bài).
- Chirp chậm · 10 s: dùng thiết lập chirp của bài, kéo dài tới 10 s.
- Sweep 20 Hz–20 kHz · 512K: 524288/44100 s, −15 dBFS đỉnh.
- Pink dải rộng: thiết lập dải, crest, mức RMS và số lượt tại mục nâng cao.
- Chẩn đoán theo mức: Pink −30/−24/−18/−12 dBFS RMS, mỗi mức 3 lượt, rồi Chirp.

Bấm **▶ Đo chuẩn** hoặc **▶ Đo chẩn đoán** với các preset Pink/Chirp/Sweep: thu nền Exclusive 3 s trước, kiểm nền yên rồi phát Shared, mỗi tín hiệu đúng **3 lượt**, bất kể
`Repeats` trong config. **Auto Test vẫn theo config**. Dừng/hỏng/đổi route hoặc rate sẽ bỏ dữ liệu dở dang và reset chế độ thu.
Không hỗ trợ Exclusive sẽ báo lỗi rõ; không tự quay về Shared. C360 được đo thủ công theo ngõ/kênh đang chọn,
không đổi BLE/mixer; kết quả ghi **Chẩn đoán thủ công**.

Chirp chậm/Sweep chỉ so ngưỡng tuyệt đối; cả hai cùng Pink dải rộng/Chẩn đoán theo mức không nạp/lưu line chuẩn, nút lưu chuẩn bị khóa và có tooltip.
Ba preset Pink + Chirp/Pink/Chirp giữ quy tắc chuẩn hiện có kể cả khi chọn trong combobox nâng cao; nên dùng nút **Đo chuẩn** để đo theo model.
Kết quả chuẩn có một checkbox loa tốt và một nút lưu chuẩn Pink và/hoặc Chirp của lần đo. **Line chuẩn cho Auto Test
C360/Acoustic 3 lưu bằng Tìm line chuẩn**: context đó có fingerprint mixer, khác context chẩn đoán thủ công.

**Nghe lại** liệt kê tín hiệu phát, nền, mic từng lượt và chỉ phần rè từng lượt của cả hai bài, từ bộ nhớ; chưa có dữ liệu thì khóa.
Chọn một mục sẽ cuộn tới phần tương ứng, tô đậm lượt trên đồ thị. Vạch nghe chạy khoảng 30 fps: Pink theo thời gian đã căn lag;
Chirp theo log tần số tức thời sau khi trừ thời điểm bắt đầu chirp, ẩn ngoài đoạn chirp. Metrics JSON bổ sung căn lượt và đường từng lượt.
Pink chỉ phần rè lọc thông cao 1,5 kHz; Chirp dùng mặt nạ STFT giữ năng lượng ≥ max(10·f(t), 1 kHz), tới 18 kHz.
Mọi lượt cùng bài dùng một hệ số chuẩn hóa chung (RMS gộp ≈ −26 dBFS), giới hạn mềm `0.9·tanh(x/0.9)`; chỉ dùng để nghe,
không đưa lại vào phép phân tích hay line chuẩn.

**Nghe qua** liệt kê các thiết bị render active, mặc định ngõ khác loa test (ưu tiên Realtek/tai nghe nếu có). Chọn chính loa test
để nghe mic/phần rè sẽ hỏi xác nhận vì có thể làm loa rè lại và có thể to; tín hiệu phát được phát ngõ test để tái hiện.
Trình nghe độc lập dùng WASAPI Shared, đổi sample rate khi cần, không đổi AudioEngine đo hay âm lượng Windows; dừng khi đo/đóng cửa sổ.
**Lưu WAV / mở thư mục** xuất toàn bộ âm thanh trong dropdown cùng JSON kết quả. **Chi tiết** tự mở khi có INVALID.

Kiểm tra offline giữ 67 ca cũ, bổ sung kế hoạch a–e, điều phối thu giả lập, lọc nghe, ánh xạ/vạch phát, chọn ngõ nghe,
thanh route và PNG tab gộp (FAIL có chuẩn, trống, INVALID mở chi tiết). Chưa thay thế thử phần cứng phát/thu/nghe thực tế.
