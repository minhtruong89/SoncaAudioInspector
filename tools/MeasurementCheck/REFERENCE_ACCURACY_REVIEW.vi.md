# Line chuẩn và treble — kiểm tra 10/09/2026

## Thay đổi

- Tìm line chuẩn kết thúc sau FEQ: THD được đánh dấu không áp dụng và không phát tone THD. Kiểm tra sản phẩm vẫn chạy FEQ rồi THD.
- Mỗi AC lưu file chuẩn và cập nhật trạng thái ngay sau đủ 5 lượt hợp lệ. AC kế tiếp lỗi/hủy không xóa chuẩn của AC đã hoàn thành. Ghi qua file tạm rồi thay file đích để tránh file chuẩn bị ghi dở.
- Bỏ lựa chọn kênh thu DUT ở Audio Routing và lựa chọn kênh mới thêm ở đo nâng cao. Khôi phục trộn kênh mặc định; lựa chọn riêng của bài chống hú giữ nguyên.
- File chuẩn phân biệt route logic và phương pháp sine/multitone, không phụ thuộc giới hạn THD. File cũ được giữ nguyên nhưng cần tìm lại line chuẩn theo khóa mới; không tự dùng file khác route/phương pháp.
- Chuẩn cũng kiểm tra SNR. Tỷ lệ điểm SNR thấp được xét riêng mỗi dải, tránh treble thiếu tín hiệu bị che bởi số điểm bass/mid tốt.
- Kiểm tra dao động max–min qua 5 lượt ở 50–15000 Hz. Nếu dao động vượt dung sai FEQ hiện tại thì báo CHUẨN CHƯA ỔN ĐỊNH và không lưu chuẩn mới. Không nới ngưỡng để biến kết quả lỗi thành đạt.
- FEQ clipping/không đủ lượt hợp lệ được báo INVALID. Thiếu băng thông sample rate, mẫu NaN/Infinity hoặc bản thu multitone thiếu 4 giây phân tích bị chặn.
- Điểm đỏ trên đồ thị dùng nội suy giới hạn giống bộ đánh giá, thay vì giả sử target 0 dB tại điểm không có khóa chính xác. Log ghi tần số treble, số đo, target và giới hạn khi vượt ngưỡng.

## Bằng chứng và giới hạn

Test multitone 23 điểm 4–20 kHz với cửa sổ 4 giây, thay đổi pha đầu và clock 200 ppm:

- 44100 Hz: sai lệch lớn nhất 0,00192 dB.
- 48000 Hz: sai lệch lớn nhất 0,00198 dB.
- Giảm biên độ treble còn một nửa: phát hiện đúng −6,02 dB (sai số dưới 0,05 dB).
- Sample rate 24000 Hz cho bài 20 kHz: bị từ chối.
- UI regression kiểm tra bỏ selector, snapshot line tím, file chuẩn không phụ thuộc THD, route khác có khóa khác, lưu AC001 độc lập lỗi AC002.

Chưa có bản thu thực/log của ca treble người dùng báo lỗi. Các test tổng hợp xác nhận lõi trích mức có độ lặp lại tốt trong điều kiện mô phỏng; không chứng minh setup mic/loa, DSP/AGC, Bluetooth hoặc vị trí đo thực đang ổn định. Cần tìm lại chuẩn và đọc log độ lặp lại/SNR/treble để xác định nguyên nhân thực.
