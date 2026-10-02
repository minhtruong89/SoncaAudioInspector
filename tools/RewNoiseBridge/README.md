# REW Noise Bridge

Ứng dụng độc lập đọc SPL meter từ REW REST API tại `127.0.0.1:4735`.

- Không thuộc `SoncaAudioInspector.sln` và không sửa/chạy lại app chính.
- Không cần internet; cần REW 5.40+ đang chạy và bật API.
- REW phải chọn đúng input/channel, có mic/interface calibration và SPL calibration.
- App hiển thị SPL, Leq, LZ Peak và độ dao động 10 giây.
- API không loại bỏ ảnh hưởng của microphone, analog gain, driver, Windows audio processing hoặc tiếng phòng.

Trong REW bật API ở Preferences > API, hoặc chạy `roomeqwizard.exe -api`.
