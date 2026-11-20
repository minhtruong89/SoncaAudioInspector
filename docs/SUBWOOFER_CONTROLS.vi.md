# Test núm/nút Subwoofer — Ctrl+Shift+T

Bài test này kiểm tra núm và nút ở mặt sau loa subwoofer: CROSSOVER FREQUENCY (40–180 Hz), TIME ALIGNING DELAY (0–20 ms) và nút PHASE kèm đèn 180°. Cổng OTG và ốc lục giác không đo được bằng âm thanh nên bài này không test chúng.

## Cách chạy

1. Ở Audio Routing, chọn ngõ phát nối vào subwoofer và ngõ thu là mic đo.
2. Đặt mic sát giữa màng loa, cách 1–2 cm. Giữ nguyên mic, âm lượng Windows và vị trí loa đến hết bài.
3. Nhấn **Ctrl+Shift+T**, bấm **Bắt đầu**. Hình mặt loa tô vàng điều khiển cần chỉnh, và kim chỉ vị trí cần đặt.
4. Chỉnh theo chỉ dẫn, rồi bấm **Đã chỉnh xong · Đo** (hoặc Enter). Mỗi bước phát sweep 10→1000 Hz, đo 3 lượt.

| Bước | Vị trí | Dùng để chấm |
|---|---|---|
| 1 | Crossover 180 Hz · Delay 0 ms · Phase 0° | Chuẩn so sánh |
| 2 | Crossover 40 Hz | Tần số cắt có dịch xuống không |
| 3 | Crossover 180 Hz · Delay 20 ms | Trễ thêm ≈ 20 ms |
| 4 | Delay 0 ms · bấm PHASE (đèn 180° sáng) | Pha đảo 180° |
| 5 | Bấm PHASE lần nữa (đèn tắt) | Trả về giống bước 1 |

## Cách chấm

Không cần line chuẩn. Mỗi điều khiển được chấm bằng cách so hai vị trí đo trên cùng route (Y_b/Y_a), nên phòng và mic tự triệt tiêu.

- **Crossover:** điểm −6 dB phía trên phải dịch ít nhất 2×. Ở 120 Hz, vị trí 40 Hz phải thấp hơn vị trí 180 Hz ít nhất 10 dB. Mức ở 30 Hz không được đổi quá 6 dB, nếu đổi nhiều thì núm đang hoạt động như núm âm lượng. Nếu tần số cắt đo được lệch nhãn quá 1,6×, kết quả là CẦN DUYỆT.
- **Delay:** lấy độ dốc pha của Y_b/Y_a trong dải 30–150 Hz. Trễ thêm phải bằng 20 ± 3 ms, pha phải giữ nguyên.
- **Phase:** lấy pha ngoại suy về 0 Hz. Giá trị từ 135° trở lên là đảo pha. Đèn 180° do người vận hành xác nhận; nếu loa không có đèn thì chọn "Loa không có đèn 180°".
- **Trả về:** so với bước 1, trễ phải trong ±2 ms và mức trong ±2 dB.

Bản thu bị loại, và bước đó phải đo lại, khi:

- mic không thấy sweep (mức < −65 dBFS hoặc SNR < 15 dB);
- mic bị clipping;
- các lượt lệch nhau quá 2 dB;
- route hoặc âm lượng Windows đổi so với bước 1.

Nếu trễ hệ thống dao động quá 1,5 ms giữa các lượt, kết quả delay là THU KHÔNG HỢP LỆ, không phải ĐẠT hay KHÔNG ĐẠT.

Các ngưỡng nằm trong `SubwooferControlLimits` (`src/Measurements/Diagnostics/SubwooferControlAnalysis.cs`). Đây là giá trị mặc định, nên chỉnh lại sau khi đo trên một loa tốt cùng fixture.

## Kiểm tra offline

`scripts/run-offline-checks.ps1 -Only std-subwoofer-controls` chạy bài kiểm tra trên một sub mô phỏng (HP 25 Hz + crossover LR4, delay số, đảo cực, phản xạ 7 ms, nhiễu mic, trễ hệ thống dao động). Kết quả này chỉ xác nhận logic phân tích, không thay cho việc đo trên loa thật.
