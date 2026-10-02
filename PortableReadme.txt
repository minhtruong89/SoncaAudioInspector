SONCA AUDIO ROUTING - BẢN PORTABLE WINDOWS X64

Đây là bản chuyên dụng cho Audio Routing của model MI SAM.

Bao gồm:
- Chọn thiết bị phát/thu và kênh IN/OUT.
- Tìm và lưu Line chuẩn.
- Auto Test, Scope, đo đáp tuyến/FFT và THD trong Audio Routing.
- Cấu hình mặc định 64K, chuẩn hóa 1 kHz và lưới FFT 1/48 octave.

Không bao gồm giao diện Ngoại quan AI, Quét Barcode hoặc cửa sổ Đo âm học riêng.

1. Giải nén toàn bộ file ZIP vào một thư mục trên Windows 10/11 64-bit.
2. Chạy SoncaAudioInspector.exe.
3. Không di chuyển riêng file EXE ra khỏi thư mục này.

Gói portable đã kèm .NET Desktop Runtime 9, checking_config.json và driver FastTrack Pro x64.
Ứng dụng vẫn cần kết nối server để xác thực/đăng nhập và gửi kết quả khi tùy chọn gửi server được bật.

Line chuẩn và cấu hình Audio Routing được giữ tại %LOCALAPPDATA%\SoncaAudioInspector,
nên vẫn còn khi thay thư mục hoặc cập nhật bản portable. Ứng dụng tự sao chép
line chuẩn CSV nằm cạnh EXE đang chạy khi chạy lần đầu. Nếu cài bản mới ở thư
mục khác, hãy chép "save standards" từ bản cũ vào thư mục mới trước lần chạy đầu.
Mỗi line mới có thêm file JSON
cùng tên để chuyển sang ứng dụng khác; JSON ghi rõ đơn vị, sample rate, 64K
và từng điểm tần số/giới hạn.
