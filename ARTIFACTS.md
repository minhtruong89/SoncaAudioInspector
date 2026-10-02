# Quy ước artifact

Mọi AI làm việc trong repo phải đọc quy ước này trước khi tạo build, package, log hoặc báo cáo.

- Thư mục chuẩn: `artifacts/YYYYMMDD-HHmmss-fffZ-mo-ta-ngan/`. Thời gian là UTC, không đổi khi cập nhật nội dung.
- Tạo bằng `scripts/new-artifact.ps1 -Description 'Mô tả cơ bản' -Slug 'mo-ta-ngan'`.
- Mỗi thư mục có `ARTIFACT.md` và `artifact.json`: thời gian ISO 8601, mô tả, commit nguồn, trạng thái working tree. Ghi thêm lệnh build, kết quả kiểm tra, kiến trúc, hash package và giới hạn xác minh vào Markdown.
- Output và intermediate phải nằm bên trong thư mục đó (`build/`, `obj/`, `logs/`, `package/`). Không ghi đè bản trước.
- `scripts/publish-portable.ps1` tự tạo thư mục và metadata theo quy ước này. `-Description` mô tả mục đích của bản phát hành.
- Chỉ giữ artifact được tạo trong 7 × 24 giờ gần nhất; metadata `createdUtc` là căn cứ chính. Không commit binary/build/cache/credential vào Git.
- Xem trước bằng `scripts/cleanup-artifacts.ps1`; sau khi đọc report, chạy `scripts/cleanup-artifacts.ps1 -Apply`. Script không tự chạy theo lịch và không tự xóa khi publish.
- Artifact cũ chưa có metadata: dùng thời điểm sửa file mới nhất trong mỗi phiên bản (kết hợp ngày tạo thư mục) để tránh xóa một thư mục cũ đang được tái sử dụng. Không suy diễn thời gian từ tên. Nếu cần thời gian tạo ban đầu chính xác, phải bổ sung metadata từ bằng chứng.
- Phạm vi legacy: con trực tiếp của `artifacts/`, `.artifacts/`, `.codex-artifacts/`, `demo-builds/`; các thư mục build tạm ở gốc có tên `.artifacts-*`, `artifacts-*`, `.codex-bin-*`, `.codex-obj*`, `.codex-build`, `bin-*`, `obj-*`, `hardware-channel-fix`.
- Không xóa source, driver gốc, `standards/`, `save standards/`, `calibrations/`, cấu hình hoặc thư mục `.git`. Script từ chối reparse point/junction và đường dẫn ngoài repo.
- Artifact legacy còn giữ lại vẫn dùng tên cũ; mọi artifact mới phải có timestamp và mô tả.
