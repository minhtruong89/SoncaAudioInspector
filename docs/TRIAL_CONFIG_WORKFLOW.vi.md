# Cấu hình thử và dữ liệu thử

1. Mở Cài đặt model (Ctrl+Shift+M), bật các bài cần đo và chọn đúng thiết bị.
2. **Gửi cấu hình thử** dùng nội dung trên form trong phiên app. **Lưu lên server** lưu bản PENDING chờ duyệt và dùng thử trong phiên app. Người có quyền duyệt xác nhận riêng trước khi cấu hình trở thành ACTIVE.
3. Muốn dùng lại bản đã lưu: **Lưu danh sách thử** lấy danh sách từ server, chọn bản, bấm **Dùng bản thử đã chọn**. Thao tác này thay nội dung đang sửa bằng bản được chọn. Lưu mới lên server đã vào PENDING nên không có nút gửi duyệt riêng.
   - Dưới **Tên thiết bị Windows (phát/thu)** có nút **↧ Lấy ngõ PHÁT/THU đang chọn ở Cấu hình đường âm thanh**: chọn đúng thiết bị ở màn hình chính rồi bấm để điền tên đó vào cấu hình (áp dụng cho mọi bài cùng tên ngõ). Dòng trạng thái báo ✓ khớp thiết bị Windows, hoặc ⚠ sai tên / thiết bị chưa cắm kèm danh sách tên Windows đang có. Ngõ BT tự nhận theo địa chỉ loa BLE nên không cần khớp tên.
   - Khi bấm **Gửi cấu hình thử** hoặc **Lưu lên server**, tên thiết bị của các bài đang bật không khớp Windows sẽ bị chặn lần đầu và liệt kê từng ngõ; bấm lại lần nữa nếu cố ý dùng khi thiết bị đang rút.
4. Popup kết nối và theo dõi thiết bị chỉ xét ngõ phát/thu của những bài đang bật. Popup liệt kê các bài đó và **mọi** thiết bị còn thiếu (C360/ACOUSTIC 3 không còn chỉ báo thiết bị đầu tiên), kèm tên Windows đang có để phân biệt sai tên với rớt USB/hub.
5. Bấm **TÌM LINE CHUẨN THỬ**. Chuẩn thử lưu riêng theo dấu vân tay toàn cấu hình; thay cấu hình cần chuẩn thử khớp cấu hình mới. Chuẩn chính thức không được dùng thay chuẩn thử.
   - C360/ACOUSTIC 3: trước khi phát, app tự đặt Default Format các ngõ của model về 44,1 kHz. Nếu ngõ thu vẫn khác 44,1 kHz (ví dụ 48 kHz), app **cảnh báo nhưng vẫn cho đo và lưu LINE CHUẨN** khi hai lượt lặp lại trong 3 dB. Cảnh báo kèm cách đổi trong Sound → Recording → Properties → Advanced. Chuẩn lưu ở 48 kHz chỉ khớp khi đo lại ở 48 kHz. Default Format đã là 44,1 kHz mà luồng thu vẫn 48 kHz nghĩa là Windows **Audio enhancements** đang xử lý ngõ thu. Ngày 12/11/2026, Intel Smart Sound đã gắn VocaEffectPack vào "Headset Microphone (C360 usb audio)" và ép khoảng lặng về 0. Cần tắt ở Settings → System → Sound → thiết bị → Audio enhancements: Off. Windows tạo một bản thiết bị riêng cho mỗi cổng/hub USB, nên chuyển C360 sang hub khác có thể đưa ngõ thu về 48 kHz. Ngày 12/11/2026, BT thu ở 48 kHz không có cặp lượt nào lặp lại trong 3 dB. Trước đó, mọi cặp BT lặp lại tốt đều thu ở 44,1 kHz.
   - **BẮT ĐẦU ĐO LINE** ở cấu hình thử vẫn đo và vẽ đáp tuyến khi chưa có chuẩn thử để kiểm tra đường tín hiệu, nhưng kết quả là INVALID (không kết luận). Auto Test thử vẫn yêu cầu chuẩn thử trước khi phát.
6. Sau khi có chuẩn thử hợp lệ, chạy Auto Test. Bật **Gửi kết quả lên Server** để lưu kết quả Auto Test hoàn tất (`PASS` hoặc `FAIL`) vào dữ liệu thử (`api/app/qa-audio/trial`).

### Ghi chú nhân viên đo (`operatorNote`) trên popup QA khi bấm Auto Test

Khi bấm **BẮT ĐẦU AUTO TEST** hoặc **TIẾP TỤC ĐO TỰ ĐỘNG**, ứng dụng mở hộp thoại **Kết quả QA** để nhân viên xác nhận kết quả QA trên loa (`PASS`, `FAIL`, hoặc `CHƯA CÓ QA`) và nhập thêm **Ghi chú (tùy chọn)** (tối đa 4000 ký tự, cho phép xuống dòng bằng `Shift+Enter`; bấm `Enter` để xác nhận bắt đầu đo). Ghi chú này được lưu vào `summary.json`, phiếu chờ `.pending-audio-qa.json` và gửi lên server trong trường `operatorNote` (tách biệt với `note` tự động của hệ thống).

### Quy tắc gửi kết quả lên Server

- **TÌM LINE CHUẨN** / **TÌM LINE CHUẨN THỬ** chỉ lưu đường chuẩn cục bộ (`save standards/` hoặc `trial standards/<hash>/`), **không** gửi kết quả lên server.
- Chỉ phiên **Auto Test hoàn tất** (đo xong toàn bộ bài đang bật và mỗi bài có kết luận `PASS` hoặc `FAIL`) mới được gửi lên `api/app/qa-audio` (chính thức) hoặc `api/app/qa-audio/trial` (cấu hình thử) khi bật **Gửi kết quả lên Server**. Các phiên tìm chuẩn (`REFERENCE`) hoặc phiên lỗi/chưa đủ (`INVALID`, `NO_SIGNAL`, hủy giữa chừng) không tạo phiếu chờ và không gửi lên server.

Kết quả, ảnh và phiếu gửi lại của phiên thử nằm trong `test data`. LINE CHUẨN thử nằm trong `trial standards` ở thư mục dữ liệu trạm. Bỏ cấu hình thử không đổi nơi lưu của phiếu chờ gửi: gửi lại vẫn vào dữ liệu thử.

Trên báo cáo server, bật **Dữ liệu thử** để xem các phiên Auto Test thử đã hoàn tất (`PASS`/`FAIL`) được gửi lên. Phần minh họa **Dữ liệu mẫu** được thay bằng bộ lọc này. Bấm ID hoặc ô kết quả để mở chi tiết bài đo, ghi chú nhân viên đo (`operatorNote`), ảnh và snapshot cấu hình. Bản ghi thử lưu trong bảng riêng `audio_trial_runs`, kèm revision và serial. Không cập nhật sản phẩm hay log QA sản xuất.

ID trên app được chốt khi bắt đầu lấy chuẩn hoặc Auto Test và giữ nguyên khi lưu/gửi lại. Mã lưu nội bộ trên server nằm trong phần thông tin phiên, tách khỏi ID hiển thị. Phiên đo từ công cụ độc lập giữ ID riêng đã ghi lúc đo; không gắn lại vào ID của một phiên app khác.

Phiên lấy chuẩn chưa ổn định có thể lưu với nhãn **INVALID – chưa có kết luận**. Nó không phải kết luận PASS/FAIL của sản phẩm, không phải một lượt Auto Test hoàn tất và không mở khóa Auto Test khi chưa có chuẩn thử hợp lệ. WAITING/RUNNING hoặc trạng thái chưa xác định không được gửi như phiên đã hoàn tất.

Ngõ thu điện tử C360 hiển thị **RECORDING TYPE C**. Các tên cũ `LINE OUT` và `TYPE C RECORDING` vẫn được quy về cùng identity khi đọc chuẩn cũ; ACOUSTIC 3 giữ ngõ LINE OUT riêng.
