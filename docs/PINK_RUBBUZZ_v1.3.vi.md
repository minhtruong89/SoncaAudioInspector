# Rè / Rub & Buzz bằng pink noise — v1.3 (pink-rattle-2.0)

Thay thế v1.2. Cơ sở đo thật: artifact `20261106-070750-775Z-claude-rattle-remeasure` (REPORT.vi.md). Loa lỏng ốc nghe rè ở −18 dBFS.

## Vì sao v1.2 không bắt được rè
- Thu WASAPI **Shared** qua hiệu ứng Windows (Intel SST + VocaEffectPack): nền tắt dần tới −167 dBFS, SNR "104 dB" là giả, lượt mức thấp bị chặn trắng.
- File pink "30–80 Hz" thực ra là full-band (HF/LF −46 dBr): chỉ số HF 1–8 kHz đo đáp tuyến tuyến tính của loa.
- Bộ đếm xung so median cục bộ, chỉ 1 lượt → rè đều không vượt median.

## Cách đo v1.3
1. Thu **WASAPI Exclusive** (chỉ phía thu; phát vẫn Shared). Không mở được Exclusive → INVALID, không âm thầm quay lại Shared.
2. Nền 3 s; nền thật phải −100…−50 dBFS, không tắt dần (>6 dB đầu/cuối), không 0 tuyệt đối — sai → INVALID "đường thu bị Windows xử lý".
3. Phát pink **25–150 Hz**, phổ FFT chính xác, seed cố định, **cùng một dạng sóng** × 3 lượt (mặc định −18 dBFS RMS, 6 s/lượt). Không có năng lượng >1 kHz (≤ −100 dBr): cao tần tại mic là do loa/phòng tạo ra.
   - Mức cho phép −60…**−12** dBFS RMS. Trên −18 app chỉ nhân biên độ dạng sóng −18 (đỉnh −1,8 dBFS ở −12), nên dạng sóng, hash và line chuẩn ở mức ≤ −18 giữ nguyên. Đổi mức của bài thì phải lưu lại line chuẩn.
   - Chọn mức bằng **Chẩn đoán theo mức** (−30/−24/−18/−12 dBFS RMS, mỗi mức 3 lượt rồi chirp) trên loa tốt và loa lỗi cùng gá: lấy mức cao hơn ngưỡng khởi phát của loa lỗi khoảng 6 dB, miễn loa tốt vẫn sạch và chưa có nén (limiter). Bench 16/11: xung bắt đầu ở −18, rõ ở −15/−12, chưa nén; bench 14/11 đã nén từ −15, tăng mức không có ích.
   - App không đọc WAV nào để đo Rè. `Assets/Audio/pink-rattle-25-150Hz-6s-44100Hz-minus18dBFS-RMS.wav` (float32, 44,1 kHz) là bản để nghe/đối chiếu, giống từng bit tín hiệu app phát ở mặc định; `tools/PinkRubBuzzCheck` kiểm tra điều đó. File cũ `pink-rattle-30-80Hz-…` của v1.2 đã bỏ.
4. Phân tích dải **4–12 kHz**: envelope 2 ms, căn các lượt theo bass (chịu được lệch clock DAC/ADC), loại khung có tiếng động lạ (một lượt cao hơn trung vị các lượt khác >10 dB, nới ±10 ms).
5. **Tỷ lệ rè lặp lại (RC, %)** = tương quan trung bình giữa các lượt = phần dao động cao tần lặp lại theo bass. Nhiễu phòng/hiss ≈ 0 %; rè cơ khí cao. Loa thật: −18 dBFS 60 % (phiên ồn hơn 32 %), −30 dBFS 2 % (dưới ngưỡng rè).

## PASS/FAIL — line chuẩn + tỷ lệ cho phép (Ctrl+Shift+M → Rè → Pink noise · v1.3)
- **Line chuẩn rè**: C360/Acoustic 3 bấm **Tìm line chuẩn** trên loa tốt → tự lưu. Chẩn đoán thủ công: Ctrl+Shift+I → **Rè (Rub & Buzz)** → **Đo chuẩn** → tick xác nhận loa tốt → **Lưu line chuẩn rè (loa tốt)**. Lưu ở `standards/pink-rubbuzz` (dữ liệu trạm, giữ khi cập nhật), khóa theo route/model/gain/âm lượng/kích thích.
- So với line chuẩn: **FAIL** khi rè lặp lại vượt chuẩn > `RepeatMarginPct` (mặc định +15 điểm) **hoặc** cao tần 4–12 kHz vượt chuẩn > `HfMarginDb` (+3 dB). `ReviewFraction` > 0 tạo vùng CẦN DUYỆT sát giới hạn (mặc định 0 = chỉ PASS/FAIL).
- Rè không lặp (va lung tung): tiếng động lạ > `MaxForeignPct` (35 %) mà cao tần mọi khung vượt chuẩn > +3 dB → FAIL "rè không lặp"; ngược lại INVALID (đo lại).
- Chưa có line chuẩn: PASS khi RC ≤ `AbsolutePassPct` (15 %), FAIL khi ≥ `AbsoluteFailPct` (30 %), giữa là CẦN DUYỆT.
- INVALID (đo lại): bass tại mic lệch chuẩn > ±3 dB (mic/loa xê dịch), nền cao tần cao hơn lúc lưu chuẩn > 3 dB, độ nhạy (bass − nền 4–12 kHz) < 45 dB, bass giữa các lượt lệch > 1 dB, clipping.
- Ngưỡng mặc định là đề xuất ban đầu; hiệu chỉnh bằng ≥ 3 loa tốt và ≥ 3 loa lỗi cùng gá. Gá mic cố định; mic gần (2–10 cm) tăng độ nhạy 15–25 dB.

## JSON (`models[].testItems.InOut.Tests[]`)
```json
"TestRubBuzz": true, "RubBuzzMode": "PinkNoise",
"RubBuzzPink": { "LevelRmsDbFs": -18, "DurationSeconds": 6, "Repeats": 3 },
"RubBuzzPinkLimits": { "RepeatMarginPct": 15, "HfMarginDb": 3, "ReviewFraction": 0, "AbsolutePassPct": 15, "AbsoluteFailPct": 30,
                       "DriveMatchDb": 3, "MinSensitivityDb": 45, "MaxForeignPct": 35 }
```
Config v1.2 vẫn đọc được (khóa P95/Events cũ bị bỏ qua, dùng mặc định mới). Kiểm tra: `tools/PinkRubBuzzCheck` (42 kiểm tra gồm dữ liệu UMIK-1 thật).

## Giao diện chung từ v1.4 (chưa phát hành)

Tab Pink và Chirp đã gộp thành **Rè (Rub & Buzz)**; từ 2026-11-17 tab này còn chứa H10–H35, Hạt rác, Rò khí và Pink nghe thử trong mục **Chẩn đoán nâng cao** (xem [CHIRP_RUBBUZZ_v1.4.vi.md](CHIRP_RUBBUZZ_v1.4.vi.md)). Phần Pink giữ bảng so chuẩn và đồ thị envelope 4–12 kHz;
phần Chirp cho biết tần số rè. Kết luận chung ưu tiên FAIL > INVALID > REVIEW > PASS; **Chi tiết** tự mở khi INVALID.
Một thanh phía trên mọi tab Ctrl+Shift+I hiển thị ngõ phát/thu, kênh, sample rate đang chọn tại Audio Routing;
kết quả từng bài vẫn ghi route thực tế của lần thu.

Chọn **Âm thanh đo** (Pink + Chirp / Pink / Chirp / Chirp chậm 10 s / Sweep 512K), bấm **▶ Đo (3 lượt)**: nền Exclusive 3 s,
kiểm nền yên rồi mỗi tín hiệu đúng ba lượt, phát Shared. Auto Test vẫn dùng số lượt config. Runner thủ công cho phép C360,
dùng ngõ/kênh đang chọn và không đổi BLE/mixer; kết quả có nhãn **Chẩn đoán thủ công**. Line chuẩn cho Auto Test C360/Acoustic 3
vẫn lưu bằng **Tìm line chuẩn** vì context có fingerprint mixer. Chậm/Sweep chỉ so tuyệt đối, không nạp/lưu chuẩn.

**Nghe lại** có tín hiệu phát, nền, mic từng lượt và chỉ phần rè từng lượt. Chọn mục sẽ cuộn tới Pink/Chirp và tô đậm lượt;
vạch chạy theo thời gian Pink (có lag), hoặc tần số log Chirp sau căn đầu lượt. Pink chỉ phần rè lọc thông cao 1,5 kHz;
Chirp giữ ≥ max(10·f(t), 1 kHz) bằng STFT. Một hệ số chung cho mọi lượt cùng bài, RMS gộp ≈ −26 dBFS, giới hạn mềm 0,9.
**Nghe qua** chọn mọi render active, ưu tiên ngõ khác loa test (Realtek/tai nghe); nghe mic/phần rè trên chính loa test phải xác nhận.
Trình nghe Shared độc lập, không đổi âm lượng Windows/AudioEngine đo, dừng khi đo hoặc đóng cửa sổ.
Một nút lưu chuẩn và checkbox loa tốt áp dụng cả hai bài; **Lưu WAV / mở thư mục** xuất tất cả âm thanh cùng JSON.
Chi tiết luồng dùng và kiểm tra bổ sung: [CHIRP_RUBBUZZ_v1.4.vi.md](CHIRP_RUBBUZZ_v1.4.vi.md).
