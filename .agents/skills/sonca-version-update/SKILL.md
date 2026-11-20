---
name: sonca-version-update
description: Phát hành phiên bản SoncaAudioInspector bằng feed có chữ ký, tái sử dụng cache và build để giảm truyền tải, xác minh updater live. Dùng khi yêu cầu bump version hoặc update server Sonca; không dành cho build debug thông thường hay ứng dụng khác.
---

# Phát hành phiên bản SoncaAudioInspector

Chi tiết: [references/operations.md](references/operations.md). Runbook cho người: [../../../docs/UPDATE.vi.md](../../../docs/UPDATE.vi.md).

## Nguyên tắc
- Tìm repo root bằng `git rev-parse --show-toplevel`, không hardcode đường dẫn.
- Tuân thủ AGENTS.md và ARTIFACTS.md. Output/cache/log chỉ nằm trong artifact tạo bằng `scripts/new-artifact.ps1` (metadata SHA + thời gian, lưu giữ 7 ngày). Chỉ chạy cleanup Apply khi được cho phép.
- Có `.codegraph` và cần hiểu code: dùng CodeGraph trước.
- Giữ đúng phạm vi và mọi chỉnh sửa của người dùng. Skill này không cấp quyền deploy; nếu người dùng đã yêu cầu deploy thì không hỏi xác nhận lại.
- Bản phát hành nhỏ: không cần gọi Opus, không tái kiến trúc pipeline.
- Không hứa thời gian chưa đo. Không in nội dung secret/khóa.

Nếu yêu cầu chỉ soạn skill, giải thích cách đóng gói hoặc build local, chỉ thực hiện phạm vi đó; không tự bump version hay mutate server.

Chỉ sửa trang lịch sử/hướng dẫn: dùng luồng HTML-only ở operations §Trang cập nhật; không tăng version app, build lại app hoặc truyền ZIP.

## Quy trình
1. Root, SHA, trạng thái:
```powershell
$root = git rev-parse --show-toplevel
$sha = git -C $root rev-parse HEAD
git -C $root status --short
```
2. Version: cập nhật cả bốn trường version trong `SoncaAudioInspector.csproj` (AppVersion.Display suy ra từ assembly) và giữ `<SoncaReleaseChannel>stable</SoncaReleaseChannel>`. Version mới phải cao hơn mọi phiên bản live, kể cả bản beta trong `versions.json`. Không ghim số của bản cũ. **Bản beta (chưa kiểm tra kỹ) không dùng skill này: dùng [sonca-beta-release](../sonca-beta-release/SKILL.md), vì beta không được ghi `latest.json`.**
3. Artifact: `$art = & "$root/scripts/new-artifact.ps1" -Description "Version update" -Slug "version-update"`. Output mới nằm trong artifact có metadata; cache trước có thể ở artifact khác còn hạn. Các publisher tạo artifact riêng, ghi lại đường dẫn chúng in ra.
4. Feed trước (bước tối ưu, xem operations §Cache):
   - Chỉ tải latest manifest + signature, xác minh chữ ký (public key production nhúng, đúng byte UTF-8, P-256/P1363).
   - Nếu cache feed trong artifact còn hạn khớp byte latest và danh mục version đã lưu trữ, xác minh blob tham chiếu theo size/hash rồi tái sử dụng.
   - Thiếu/lệch: chỉ tải phần thiếu. Cache lạnh: hydrate đầy đủ, báo trước số byte. Khi server đã có lịch sử, không bỏ `-Previous` để chạy nhanh. Chỉ lần phát hành đầu tiên được xác nhận chưa có feed mới không cần previous.
5. Build một lần, tái sử dụng đúng artifact đó để ký/kiểm/deploy:
```powershell
# File ghi chú là UTF-8 không BOM: Windows PowerShell 5.1 mà thiếu -Encoding UTF8 sẽ đọc thành ANSI ("Duyá»‡t"),
# như tóm tắt 1.4.0 đã bị ký. UpdateFeedTool giờ từ chối chuỗi lỗi kiểu này.
$features = "$root/docs/release-notes/$ver.txt"
$notes = Get-Content -LiteralPath $features -Encoding UTF8 -TotalCount 1
& "$root/scripts/publish-portable.ps1" -Architecture x64 -SkipStationData
& "$root/scripts/publish-update.ps1" -PortableDir $portable -Version $ver `
  -Notes $notes -Previous $prevFeed -FeaturesFile $features
```
   `-Version` bắt buộc khớp csproj (mặc định script vẫn là 1.0.0). `$prevFeed` phải là feed TRƯỚC ĐẦY ĐỦ (archive version đã ký, mọi blob tham chiếu, ZIP cũ) vì FeedHistoryPage.CopyHistory cần nó. Nếu input liên quan không đổi, tái sử dụng build/test đã có.
6. Trước deploy: chạy test liên quan thay đổi và verify feed local bằng public key production; kiểm version của binary và các loại trừ dữ liệu trạm. Chưa chạy live-check với version chưa được publish.
7. Trước truyền tải, báo: SHA nguồn, version binary, số file đổi và tổng byte.
8. Deploy (chỉ khi đã được phép):
```powershell
& "$root/scripts/deploy-update.ps1" -FeedDir $newFeed -WhatIf
# xem kế hoạch, rồi chạy thật:
& "$root/scripts/deploy-update.ps1" -FeedDir $newFeed
```
   SSH/khóa ký lấy từ mặc định thực của script; known-host nghiêm ngặt, chỉ dùng đường dẫn khóa.
9. Sau khi deploy hoàn tất, xác minh bằng client thật trên station tổng hợp:
```powershell
dotnet run --project "$root/tools/UpdateReleaseCheck/UpdateReleaseCheck.csproj" `
  --artifacts-path "$art/build" "-p:AudioAssemblyPath=$publishedDll" `
  -- $prevFeed $newFeed "$art/check-out"
```
   Công cụ kiểm chữ ký live, tải delta, cài giao dịch, bảo toàn dữ liệu, bỏ qua version bằng nhau VÀ tải ZIP đầy đủ qua URL có hash. Output phải mới; đây không phải delta-only. Tải đủ rồi so hash; HEAD 200 không đủ. Không HEAD/GET ZIP cuối khi đang upload.
10. Báo cáo: phân biệt build / local / synthetic / live / station thật. Không khẳng định phần cứng hay tương đương REW.
11. Đẩy GitHub (chỉ sau khi bước 9 đạt; yêu cầu phát hành đã bao gồm việc này — xem operations §GitHub):
    - Commit **chỉ** file phát hành (bốn trường version trong csproj, `docs/release-notes/<ver>.txt` nếu có), message `release: v<ver>`; tag annotated `v<ver>` trên commit đó.
    - Kiểm tra remote `private` trỏ `github.com/zenith1379/SoncaAudioInspector-Private` **và** `gh repo view zenith1379/SoncaAudioInspector-Private --json visibility -q .visibility` = `PRIVATE`; sai một điều → dừng, không push, báo lại.
    - `git push private HEAD:main` rồi `git push private v<ver>`. Báo SHA và tag đã push.

## Bất biến
- Giữ checking_config*.json, calibration, standards/save standards, fail data, log, launcher của station.
- Blob và archive phiên bản đã phát hành là bất biến; latest/index được chuyển theo publisher. Rollback = phát hành version cao hơn.
- Không tái dùng DLL cũ theo tên/version; không đổi SDK/runtime/R2R ngầm.
- Không dùng cờ không tồn tại (DeltaOnly, NoZip, atomic, cache...). Kiểm tra: `Get-Command <script> -Syntax`; tham số không có thì dùng luồng hiện tại.
- GitHub: chỉ push lên remote `private` đã xác minh PRIVATE. **Không bao giờ** push lên `origin`, `upstream`, `minhtruong89` (public) hay repo public nào; không force-push; không đưa khóa, secret, `.env`, artifact vào commit.

## Định tuyến
- Bản vá nhỏ đã commit, kênh ổn định, số 4 phần (1.5.5 → 1.5.5.1): [sonca-quick-patch](../sonca-quick-patch/SKILL.md) (`scripts/quick-patch.ps1`, deploy giãn nhịp `deploy-update.ps1 -PauseSeconds 8`).
- Bản beta / thử nghiệm: [sonca-beta-release](../sonca-beta-release/SKILL.md). Chỉ ghi `beta.json`, giữ nguyên `latest.json`, có đường hạ cấp. Bản ổn định cao hơn bản beta tự bỏ con trỏ beta cũ.
- Muốn một file để cập nhật bằng tay: cân nhắc `Update.exe` online đọc cùng feed, hỗ trợ các phiên bản đã cài bằng hash thực tế và dùng transaction/backup/chữ ký/hash; hiện chưa có helper này, xem operations §Cập nhật một file.
- Phát hành EXE single-file thủ công có chủ ý: tách thành nhiệm vụ xác thực đóng gói riêng (operations §Single-file); bản cập nhật thường vẫn đi luồng trên.
- Yêu cầu tối ưu pipeline rõ ràng: xem operations §Chưa triển khai; phải implement + test trước khi dùng.
- CDN cache hỏng: purge có ủy quyền hoặc URL hash mới đã xác minh; không báo đã purge nếu chưa làm.
