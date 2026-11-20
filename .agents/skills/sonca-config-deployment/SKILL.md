---
name: sonca-config-deployment
description: Phát hành và triển khai cấu hình trạm đo (station config) và cấu hình model loa cho SoncaAudioInspector lên Cloudflare / VPS mà không gián đoạn dịch vụ. Dùng khi thay đổi limit, dung sai hoặc thêm model mới.
---

# Triển khai cấu hình SoncaAudioInspector (Config Deployment)

Tài liệu liên quan: [../../../docs/CONFIG_TESTS.vi.md](../../../docs/CONFIG_TESTS.vi.md), [../../../docs/UPDATE.vi.md](../../../docs/UPDATE.vi.md).

## Nguyên tắc
- Cấu hình trạm và model được ký số bất biến: `config/files/<sha>.json` + pointer có chữ ký số `config/latest.json(.sig)`.
- Khóa ký cấu hình là khóa riêng của trạm, được bảo vệ cẩn mật.
- Kiểm tra hợp lệ trước khi đẩy: hình dạng JSON, các giới hạn đo, tính toàn vẹn chữ ký số qua `tools/ServerConfigCheck` và `tools/UpdateFeedTool`.
- Triển khai theo nguyên tắc: đẩy file bất biến trước, sau đó mới cập nhật con trỏ `latest.json`.

## Quy trình

1. **Kiểm tra tính hợp lệ của file cấu hình local:**
```powershell
$root = git rev-parse --show-toplevel
powershell.exe -ExecutionPolicy Bypass -File "$root/scripts/run-offline-checks.ps1" -Only server-config,config-acceptance
```

2. **Đóng gói config feed (Publish):**
```powershell
# Chạy script publish-config.ps1 để tạo feed ký số trong thư mục artifact
powershell.exe -ExecutionPolicy Bypass -File "$root/scripts/publish-config.ps1" -ConfigPath "path/to/checking_config.json"
```

3. **Xác minh feed trước khi deploy:**
Feed tạo ra phải vượt qua xác thực chữ ký và kiểm tra schema.

4. **Triển khai lên server (Deploy):**
```powershell
powershell.exe -ExecutionPolicy Bypass -File "$root/scripts/deploy-config.ps1" -FeedDir "path/to/artifact/config-feed/config"
```
Script sẽ upload file bất biến qua SSH, kiểm tra SHA256, chuyển pointer và xác thực lại endpoint public HTTPS.
