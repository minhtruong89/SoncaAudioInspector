# Đánh giá tốc độ Auto Test — 2026-10-02

## Kết luận

Có ứng viên tăng tốc xử lý mà giữ nguyên dữ liệu thu, nhưng chưa có benchmark trước/sau hoặc phép đo đối chứng để chứng minh app tương đương hay chính xác hơn REW. Đợt này chỉ kiểm tra khả năng tối ưu; chưa thay đổi thuật toán đo, thời lượng sweep, settling, số lượt hoặc ngưỡng hợp lệ.

## Hiện trạng từ mã nguồn

- `AudioRouting.AutoTestRouteSettleMilliseconds`: 1000 ms; đã trừ thời gian chuyển route khỏi thời gian chờ còn lại.
- `ConfigureProductionMeasurement`: thời lượng sweep lấy từ UI, số lượt từ `LogSweepRuns` (1–3); Auto Test phát ở 44.1 kHz, phân tích bằng sample rate thu thực tế.
- `TestRunner.RunTestAsync`: dùng cùng bản thu log sweep cho FEQ và THD (Farina H2–H9), không phát thêm tone THD ở nhánh log sweep.
- Default sweep 64K/44.1 kHz = 1.486 s, có thêm 1 s thu đuôi mỗi lượt, chưa tính preflight, driver latency và CPU. Đây là thời gian tín hiệu trong mã, không phải benchmark toàn suite.
- Nhánh sine riêng vẫn thu 3 s/điểm, phân tích 2 s cuối; chờ 3 s giữa các điểm. Cắt các khoảng này có thể thay đổi ổn định DSP/driver, độ phân giải và noise floor.
- Có cơ chế tái sử dụng noise floor trong suite; không nên mở rộng phạm vi hoặc kéo dài tuổi dữ liệu nền nếu chưa kiểm tra thay đổi route và môi trường.

## Ứng viên tối ưu giữ nguyên phép đo

| Ưu tiên | Vị trí | Đề xuất | Điều kiện xác minh |
|---|---|---|---|
| 1 | `StandardAcousticMeasurement.AnalyzeLogSweep` | `outputSpectrum` đã là FFT của `recorded` sau bù clock; nhánh Farina lại tạo `farnaOutputSpectrum` và FFT cùng dữ liệu. Có thể đọc lại spectrum hiện hữu để bỏ một FFT và một allocation. | Bảo đảm không có mutation spectrum trước khi sử dụng; so sánh toàn bộ FEQ/IR/hài/validity trên cùng WAV, gồm clock drift và clipping. Chưa triển khai. |
| 2 | Sinh excitation/inverse filter giữa các lượt | Cache theo toàn bộ tham số ảnh hưởng waveform, sample rate và FFT size. | Không dùng chung buffer mutable; hash waveform phải trùng; vô hiệu hóa khi UI/route thay đổi. Chưa benchmark. |
| 3 | Vẽ graph, ghi file, upload | Tách công việc CPU/I/O khỏi đường chờ khi snapshot đã hoàn chỉnh. | Không dùng MMDevice hoặc UI từ thread khác; không công bố PASS/upload hoàn tất trước khi tác vụ thực sự xong. Cần đo thời gian từng giai đoạn. |

Không đưa ra phần trăm tăng tốc khi chưa đo. Tránh song song hóa phát/thu các route trên cùng interface.

## Ràng buộc so với REW

[Tài liệu REW về đo sweep](https://www.roomeqwizard.com/help/help_en-GB/html/makingmeasurements.html) nêu default 256K; mỗi lần nhân đôi chiều dài hoặc số sweep trung bình cải thiện SNR gần 3 dB, và clock khác nhau cần timing/clock correction. Vì vậy 64K không tự chứng minh ngang 256K: xét riêng lợi ích chiều dài trong điều kiện chung clock, chênh lệch danh nghĩa khoảng 6 dB SNR. Không thể kết luận chính xác chỉ từ mật độ điểm hoặc tên thuật toán.

Để chấp nhận một thay đổi tăng tốc theo yêu cầu “tương đương hoặc chính xác hơn REW”:

1. Đo baseline và phiên bản ứng viên bằng cùng mic/interface đã hiệu chuẩn, Windows endpoint volume, route/channel, geometry/fixture, mức phát, sample rate thực tế và băng thông hài.
2. Dùng cùng chiều dài sweep, số lượt, timing reference/clock correction, calibration và smoothing/window khi đối chiếu REW; đánh giá riêng cấu hình production 64K nếu muốn dùng nó thay cấu hình dài hơn.
3. Thu raw WAV và log thời gian từng giai đoạn; đo lặp đủ để so sánh bias và repeatability của FEQ, H2–H9/THD, SNR, clock fit và tỷ lệ INVALID. Chốt ngưỡng chấp nhận trước khi thử, dựa trên uncertainty và limit của fixture/model.
4. Với tối ưu CPU thuần túy, chạy baseline và ứng viên trên cùng dữ liệu; kết quả phải bằng nhau trong tolerance số đã định, verdict/validity phải giữ nguyên. Với thay đổi acquisition, bắt buộc đối chứng phần cứng.
5. Thử capture yếu, noise/hum, clipping, mất route, tail thiếu, lệch clock và điểm sát ngưỡng; không giảm thời gian bằng cách nới gate hay bỏ dữ liệu lỗi.

THD từ sweep không cung cấp THD+N/SINAD tương đương phép sine bằng cách suy diễn; phải giữ nguyên ý nghĩa chỉ số và băng thông có thể đo tại từng tần số.

## Phạm vi kiểm chứng lần này

Kiểm tra mã nguồn và chạy harness synthetic hiện có, log trong artifact `20261002-082734-165Z-validation`. Kết quả build/synthetic được ghi vào `ARTIFACT.md` sau khi hoàn thành. Không đo loa/mic trực tiếp và không chạy phép đo đối chứng REW trong lần này.
