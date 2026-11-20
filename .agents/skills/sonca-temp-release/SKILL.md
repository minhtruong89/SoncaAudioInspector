---
name: sonca-temp-release
description: Gửi BẢN TẠM SoncaAudioInspector từ một máy bất kỳ lên server inventory.acnos.store để ADMIN duyệt; sau khi duyệt chỉ máy kênh Beta được mời chạy thử. Không cần khóa ký hay SSH. Dùng khi Claude ở máy khác (không phải máy build chính) cần đưa bản sửa xuống trạm chạy trước. Bản ổn định vẫn dùng sonca-version-update.
---

# Gửi bản tạm (v1.5.3)

Liên quan: [../sonca-beta-release/SKILL.md](../sonca-beta-release/SKILL.md), [../sonca-version-update/SKILL.md](../sonca-version-update/SKILL.md).

## Luồng

1. Máy gửi (Claude ở máy khác) build portable và gửi lên server bằng token. Server chỉ nhận file nó chưa có, mỗi lần gửi tối đa 768 KiB, kiểm SHA-256 từng file.
2. ADMIN mở `https://inventory.acnos.store/temp-updates`, đọc ghi chú và danh sách file đổi, bấm **Duyệt** (hoặc Từ chối / Thu hồi).
3. Khi duyệt, server ký manifest beta bằng khóa bản tạm của server (`/opt/speaker-inventory/secrets/update-temp.private.pem`). App ≥ 1.5.3 ghim khóa công khai `UpdateSignature.TempReleasePublicKey`.
4. Chỉ máy đã chọn Ctrl+Shift+I → Phiên bản → Kênh cập nhật → **Beta** đọc `/api/app/updates/temp/latest.json`. Máy kênh Ổn định không bao giờ bị mời (luật beta 2026-11-14).
5. Trang `/temp-updates` (và link ở chân trang `/update`) ghi thay đổi để ai thấy ổn thì chuyển kênh Beta cập nhật theo. Không ổn: Phiên bản → Cài phiên bản khác → bản ổn định.
6. Lên Ổn định: máy build chính phát hành số phiên bản CAO HƠN bản tạm theo sonca-version-update; bản ổn định cao hơn tự thay bản tạm ở mọi máy.

## Gửi từ máy khác

Điều kiện: clone repo private, cài .NET SDK, có token do chủ cấp (không commit, không in ra log).

```powershell
$env:SONCA_UPDATE_SUBMIT_TOKEN = '<token chủ cấp>'
# csproj: 4 trường version = số mới (cao hơn bản ổn định và bản tạm đang chạy) và <SoncaReleaseChannel>beta</SoncaReleaseChannel>
# notes.txt (UTF-8): dòng 1 = tóm tắt ngắn, các dòng sau = chi tiết
powershell.exe -ExecutionPolicy Bypass -File scripts/submit-temp-update.ps1 -Version 1.5.3 -FeaturesFile notes.txt -Submitter "Claude máy trạm"
```

Script từ chối khi version csproj khác `-Version` hoặc kênh không phải beta. Server từ chối khi version không cao hơn bản ổn định đang phát hành và bản tạm đang chạy, khi thiếu `SoncaAudioInspector.exe`/`.dll`, hoặc khi có đường dẫn mà trạm cũng từ chối (dữ liệu trạm, `checking_config*.json`, `..`).

Sau khi ADMIN duyệt, kiểm như máy kênh Beta: `dotnet <artifact>/bin/UpdateFeedTool.dll verify-temp --version 1.5.3` (chữ ký khóa bản tạm + SHA-256 từng file).

## Quy tắc

- Không commit token, không gửi bản chưa build/test offline (`scripts/run-offline-checks.ps1`).
- Không dùng luồng này cho bản ổn định; không sửa `latest.json`/`beta.json` của feed tĩnh.
- `UpdateFeedTool prune` chỉ biết feed tĩnh; blob của bản tạm nằm riêng trong `/opt/speaker-inventory/data/update-submissions`.
