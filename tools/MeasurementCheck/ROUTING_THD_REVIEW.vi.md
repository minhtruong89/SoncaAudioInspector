# Kiểm tra routing và THD — 10/09/2026

Đã có điều chỉnh tiếp theo theo yêu cầu người dùng: bỏ lựa chọn kênh DUT mới thêm, bỏ THD khi tìm chuẩn và lưu từng AC ngay khi hoàn thành. Xem `REFERENCE_ACCURACY_REVIEW.vi.md` cho hành vi hiện tại.

## Kết quả xác nhận bằng code và tín hiệu tổng hợp

- Công thức THD là RSS của hài chia RMS tone gốc; các test méo chuẩn 1% chạy đúng.
- Cách dò tần số cũ cho THD+N 1,30533% và 1,26930% với tone 4007,3/8013,7 Hz có hài chuẩn 1%. Sau FFT định vị + sine-fit tinh: 1,00738% và 1,01342%. Không khẳng định đây là nguyên nhân THD cao của loa thực.
- Benchmark cùng 4 tone: trước 509 ms, sau 235–277 ms. Đây là CPU analysis, không phải thời gian thu trên thiết bị.
- Thu THD 1 kHz vẫn lấy 3 lượt hợp lệ, tối đa 4 lần thử. Mỗi lượt thu giảm 2,5 xuống 1,25 giây; khoảng nghỉ 300 xuống 50 ms. Giữ cửa sổ phân tích 1 giây. Giảm phần chờ cố định khoảng 4,25 giây cho 3 lượt; cần kiểm tra trên USB/Bluetooth thực, đặc biệt latency/AGC của thiết bị.
- Từ chối NaN và bản thu không có tone đủ mạnh. Không có H2 trong băng thông thì THD = NaN, UI chẩn đoán báo không khả dụng. Bài méo đa tần dừng ở 8 kHz; số hài thay đổi theo tần số/sample rate.
- Nâng cao giữ ID thiết bị khi refresh; không tự thay thiết bị đã chọn bị mất bằng thiết bị đầu danh sách. Có lựa chọn kênh L/R hoặc trộn, nhằm tránh triệt tiêu tone khi hai kênh ngược pha. Lựa chọn kênh hiện giữ trong phiên UI, chưa lưu cấu hình dài hạn. Bài chống hú dùng kênh riêng.
- LƯU LINE chụp dữ liệu hiện tại vào RAM và vẽ nét tím đứt; đo tiếp không thay dữ liệu đã lưu. Lưu lại thay đường tạm; tắt app thì mất. Chuột phải có lệnh lưu file chuẩn cũ.
- Chẩn đoán méo/bass có bằng chứng số, nguyên nhân khả dĩ và kiểm tra tiếp theo; INVALID không được dùng để kết luận loa. Ngưỡng bass 1,5%/compression 2 dB chỉ sàng lọc tạm. Có nút dừng cho méo và bass; bass dừng khi clipping.

## Kiểm chứng

- Build Release win-x64 thành công, 0 errors.
- MeasurementCheck: tone lệch tần số, THD/THD+N, noise, NaN, thiếu băng thông và chặn chẩn đoán khi clipping.
- StandardMeasurementCheck: passed.
- FeedbackShiftCheck: 37 checks passed, gồm giải mã PCM và lựa chọn kênh trước khi trộn.
- FeedbackUiCheck: WPF construction/render, snapshot đường so sánh độc lập với dữ liệu live, hủy phép đo nâng cao, và regression nút chống hú passed. Không phát âm thanh trong UI test.

## Giới hạn và EQ trên loa

Chưa chạy đo loa thực hoặc so với máy chuẩn. Trộn các kênh vẫn có thể che clipping của một kênh; chọn đúng kênh mic khi đo. Software recording gain không thay gain analog/ADC. THD đo toàn chuỗi phát–ampli–loa–mic–ADC, nên không xác định bộ phận hỏng chỉ từ một tỷ lệ méo.

Repo chưa có giao thức đặt bass/mid/treble trên phần cứng loa (không tìm thấy điều khiển EQ qua serial, HID, GATT/RFCOMM hoặc MIDI). Các BassMin/MidMin/TrebleMin chỉ chia dải phân tích. Muốn ép EQ 50% cần model, giao thức điều khiển và xác nhận mức 50% là trung tính của model; chưa thêm lệnh EQ giả hoặc bù EQ nguồn phát.
