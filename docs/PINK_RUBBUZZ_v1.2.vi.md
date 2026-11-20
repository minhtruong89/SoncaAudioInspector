# v1.2 — Rè/rung vỏ và mối ghép bằng pink noise

Phép thử sàng lọc tiếng buzz/rattle phát ra khi loa hoạt động, như lưới/vỏ/mối ghép rung. Không xác định riêng thiếu ốc hay cọ coil chỉ từ số liệu mic.

## Cấu hình và đo

1. Ctrl+Shift+M → chọn bài thu bằng **mic âm học** → bật TestRubBuzz → tab Rè (Rub & Buzz) → chọn **Pink noise · v1.2**. Bài mới bật ưu tiên pink; JSON cũ chưa có RubBuzzMode vẫn dùng sine bước.
2. File nguồn `Assets/Audio/pink-rattle-30-80Hz-44100Hz-minus18dBFS-RMS.wav`: bản pink random 30–80 Hz từ v1.1, PCM24 mono 44,1 kHz. Không sinh lại tại runtime. *(Lịch sử: từ v1.3 app tự sinh pink 25–150 Hz và không đọc file này; file đã bị xóa khỏi gói vì thực ra là full-band, xem [PINK_RUBBUZZ_v1.3.vi.md](PINK_RUBBUZZ_v1.3.vi.md).)* Mức nhập là **dBFS RMS**, mặc định −18; khác dBFS peak của sine. WAV dài được cắt theo thời lượng và fade cuối, vì vậy bảng chi tiết ghi cả RMS/peak đo số của đoạn thực phát.
3. Chọn sample rate phát44100Hz. Auto Test thu nền0,6s và hai lượt pink, mặc định5s/lượt. Dùng ngõ/kênh của bài, không chuyển tab tự phát. Không đổi mic, khoảng cách, hướng loa, giá đỡ, gain/DSP giữa các phép so.
4. Ctrl+Shift+I → **Rè Pink · v1.2** để xem bảng, chọn lượt và đồ thị envelope. “Lưu WAV / mở thư mục” xuất mic từng lượt, nền, stimulus và JSON để nghe/xem lại. WAV xuất bản thu dùng float32; file nguồn đi kèm dùng PCM24.

Với C360/Acoustic3: chỉ bật Pink Rè tại bài **Mic thực tế / AcousticMic**, hoàn tất cấu hình mic trước khi bật. Không bật trên các bài LINE OUT điện tử. Chạy Auto Test để hệ thống đặt/khôi phục nguồn và BLE đúng chuỗi. Các nút “Đo riêng/Lấy3lượt” dùng cho luồng model thường; với C360 dùng Auto Test rồi “Lưu lượt đang xem · TỐT/LỖI”.

## Có cần loa tốt không?

Không cần loa tốt để thu và xem số liệu. **Cần tham chiếu loa tốt để diễn giải khác biệt một cách có ích**, và cần thêm dữ liệu loa lỗi để xác thực ngưỡng tự động. Một loa tốt không đủ đại diện sai khác của toàn model.

Cách làm ban đầu phù hợp trường hợp mối ghép: đo mẫu đang rè, lưu nhãn LỖI; sửa/bắt đủ đúng ốc theo quy định, xác nhận bằng tai, đo lại và lưu nhãn TỐT. Cùng mẫu trước/sau sửa giúp giảm ảnh hưởng sai khác giữa các loa. Lặp để có ít nhất3 lượt hợp lệ mỗi nhãn. Muốn triển khai dây chuyền cần mở rộng nhiều mẫu tốt/lỗi, kiểm định độc lập và độ lặp lại.

- Nhập mã loa/serial và xác nhận nhãn/fixture trước khi lưu.
- Model thường: “Lấy3lượt · LOA TỐT/LỖI” chủ động thu ba lượt rồi mới lưu; hủy giữa chừng không cập nhật profile.
- Mọi model: “Lưu lượt đang xem · TỐT/LỖI” lưu đúng các lượt đã hoàn tất, không phát âm thanh. Không lưu lặp cùng bản thu để tăng số lượng.
- Tham chiếu thử nghiệm cần ≥3 lượt tốt, HF và LF dao động không quá3dB. Nút xác thực ngưỡng chỉ khả dụng sau khi có thêm ≥3 lượt lỗi vượt ngưỡng ở mức bass tương đương.
- Sau khi tự đối chiếu thực tế, tích xác nhận và ghi cách xác thực để áp dụng ngưỡng. Đây là ghi nhận trách nhiệm xác thực của người vận hành, không phải chứng nhận thống kê do phần mềm tạo ra.

Tham chiếu/nhãn/bản thu lưu trong dữ liệu trạm `standards/pink-rubbuzz`, tách khỏi cấu hình model đồng bộ server. Updater giữ dữ liệu này. Đổi context, waveform, thuật toán hoặc ngưỡng không tự kế thừa PASS cũ. Vị trí mic, gá và Windows enhancements/AGC vẫn cần người vận hành kiểm tra.

## Số liệu và kết luận

- **HF:** RMS mic trong1–8kHz, hiển thị dBFS và dBr so với RMS mic30–80Hz.
- **P95:** phân vị95% envelope HF10ms, hop≈5ms; giúp quan sát buzz kéo dài.
- **Sự kiện/s:** envelope nổi bật trên median lân cận≈500ms thêm10dB và trên nềnP99 thêm6dB; các đỉnh gần nhau được gộp. Marker cho hoạt động xung, không tự chứng minh va chạm cơ khí.
- **Peak/crest:** thông tin hỗ trợ, không dùng riêng để FAIL.
- WAV có đuôi ngoài băng. Năng lượng HF mic còn gồm đáp ứng tuyến tính và méo bình thường; không gọi đây là phần dư cơ khí đã tách sạch. Không dùng THD/F0 hoặc ngưỡng1,5% của sine cho pink. dBFS/dBr không phải SPL.

| Trạng thái | Ý nghĩa |
|---|---|
| INVALID | Thiếu/clip/NaN, nền cao, thiếu bass hoặc SNR LF<20dB; không kết luận sản phẩm |
| CẦN DUYỆT | Thiếu hoặc chưa xác thực tham chiếu, sai context/LF, thiếu độ nhạy, hoặc bất thường chỉ có ở một lượt |
| PASS | Trong giới hạn tham chiếu đã được người vận hành xác thực, đủ lượt và chất lượng thu |
| FAIL | Ít nhất hai lượt có chỉ số vượt giới hạn của tham chiếu đã xác thực; không tự suy ra thiếu ốc |

Các lề khởi đầu HF/P95=6dB trên giá trị lớn nhất của loa tốt, sự kiện=+2/s; có thể chỉnh trong model. Đây là tham số kỹ thuật để bắt đầu khảo sát, **không phải ngưỡng đã chứng minh đúng với mọi loa**. Cần nền thấp hơn ngưỡng ít nhất6dB để có khả năng phát hiện. LF lệch chuẩn hơn3dB yêu cầu kiểm tra lại điều kiện. Trạng thái CẦN DUYỆT không được upload như PASS/FAIL tổng.

## Giới hạn kiểm chứng bản phát hành

Kiểm tra tổng hợp chỉ xác minh phép tính, xử lý trạng thái, tương thích JSON và render WPF. Cần đo mic trên chính loa tốt/lỗi để đánh giá khả năng phát hiện và tỷ lệ loại nhầm/lọt lỗi. Thuật toán này không phải triển khai eLP/PRB/MHT thương mại.
