---
name: sonca-beta-release
description: Phát hành bản BETA SoncaAudioInspector (vd v1.4.4 beta) qua kênh beta có chữ ký. Chỉ ghi beta.json, không bao giờ đổi latest.json, nên máy kênh Ổn định không bị mời cập nhật; máy cài beta hạ cấp được trong app. Dùng khi yêu cầu phát hành/deploy bản beta hoặc bản "chưa kiểm tra kỹ". Bản ổn định dùng sonca-version-update.
---

# Phát hành bản beta SoncaAudioInspector

Skill này mở rộng [sonca-version-update](../sonca-version-update/SKILL.md). Dùng chung artifact, feed cache, ký và GitHub; các bước khác nhau ghi bên dưới.
Chi tiết vận hành: [../sonca-version-update/references/operations.md](../sonca-version-update/references/operations.md). Runbook cho người: [../../../docs/UPDATE.vi.md](../../../docs/UPDATE.vi.md).

## Quy tắc cho MỌI bản beta (chủ sở hữu quyết định 2026-11-14)

1. **Không ép máy khác cập nhật.**
   - Beta chỉ ghi `beta.json(.sig)`. `latest.json(.sig)` live phải giữ nguyên từng byte trước và sau deploy.
   - App đọc `latest.json` khi khởi động và mời mọi máy cập nhật. Nếu beta lọt vào đó, máy nào cũng bị mời.
2. **Ai nhận beta.**
   - Máy đã chọn Ctrl+Shift+I → Phiên bản → Kênh cập nhật → Beta.
   - Máy đang chạy bản beta mà chưa chọn kênh (mặc định của bản beta là kênh Beta).
   - Máy cài tay ZIP của thẻ BETA trên trang `/update`. Máy ≤ 1.4.3 chưa có ô chọn kênh nên chỉ thử beta bằng cách này.
3. **Luôn hạ cấp được.**
   - `versions.json(.sig)`, mọi `versions/<v>.json(.sig)` và blob của bản ổn định trước phải còn đủ.
   - Trên máy: Phiên bản → Cài phiên bản khác / Hạ cấp → chọn bản ổn định → Hạ cấp ngay. Dữ liệu trạm giữ nguyên.
   - Bước live số 9 bắt buộc kiểm đường hạ cấp này.
4. **Số phiên bản.**
   - Beta dùng số mới, cao hơn mọi phiên bản đã có, kể cả beta cũ.
   - Archive và blob là bất biến: không phát hành lại cùng số.
   - Đưa beta lên ổn định bằng một số cao hơn (vd 1.4.5) theo sonca-version-update, với `SoncaReleaseChannel=stable`. Bản ổn định cao hơn tự bỏ con trỏ beta cũ hơn nó.
5. **Phạm vi.** Skill này không cấp quyền deploy. Nếu người dùng đã yêu cầu deploy thì không hỏi lại. Không hứa thời gian chưa đo, không in secret.

## Quy trình

1. Root, SHA, trạng thái:
```powershell
$root = git rev-parse --show-toplevel
$sha = git -C $root rev-parse HEAD
git -C $root status --short
```
   Code tính năng phải đã commit. Chỉ được còn chưa commit hai thứ:
   - `SoncaAudioInspector.csproj`: bốn trường version **và** `<SoncaReleaseChannel>beta</SoncaReleaseChannel>`;
   - `docs/release-notes/<ver>.txt`: dòng đầu là tóm tắt, nên bắt đầu bằng "BETA".

   Có thay đổi khác thì dừng và báo lại.
2. Version: `<ver>` phải cao hơn mọi phiên bản live. Đọc `versions.json` live nếu đã có; lần beta đầu tiên thì đọc `latest.json` và trang `/update`. `publish-update.ps1 -Channel beta` từ chối khi csproj không phải beta.
3. Artifact: `$art = & "$root/scripts/new-artifact.ps1" -Description "Beta release <ver>" -Slug "beta-release"`.
4. Feed trước: giống sonca-version-update bước 4 (cache có hạn, xác minh chữ ký, chỉ tải phần thiếu).
   - `$prevFeed` phải là feed **live hiện tại đầy đủ**: latest, mọi archive, blob, ZIP, và `beta.json` / `versions.json` nếu đã có.
   - `latest.json` của nó được chép nguyên vào feed beta.
5. Build một lần:
```powershell
$features = "$root/docs/release-notes/$ver.txt"
$notes = Get-Content -LiteralPath $features -Encoding UTF8 -TotalCount 1
& "$root/scripts/publish-portable.ps1" -Architecture x64 -SkipStationData
& "$root/scripts/publish-update.ps1" -PortableDir $portable -Version $ver -Channel beta `
  -Notes $notes -Previous $prevFeed -FeaturesFile $features
```
   Script tự chạy `UpdateFeedTool verify --channel beta --previous $prevFeed`. Lệnh này kiểm:
   - `latest.json` và `.sig` giống feed trước từng byte;
   - `beta.json` mới hơn `latest.json`;
   - `versions.json` khớp các archive.
6. Trước deploy:
   - Offline: `scripts/run-offline-checks.ps1 -Only update-logic,rubbuzz,source-layout,skills,ui-version,ui-version-header`. Tất cả phải PASS.
   - Kiểm binary:
     - FileVersion bằng `<ver>`;
     - `version-info.json` trong portable có `"channel": "beta"`;
     - feed có `beta.json` với version `<ver>`;
     - hash `latest.json` bằng hash của `$prevFeed/latest.json`.
7. Trước truyền tải, báo:
   - SHA nguồn, version binary, số file đổi và tổng byte;
   - xác nhận "latest.json giữ nguyên <bản ổn định>".
8. Deploy, chỉ khi đã được phép. **Luôn có `-Channel beta`:**
```powershell
& "$root/scripts/deploy-update.ps1" -FeedDir $newFeed -Channel beta -WhatIf
& "$root/scripts/deploy-update.ps1" -FeedDir $newFeed -Channel beta
```
   - Trước khi tải: script so `latest.json` live với feed. Lệch (đã có bản ổn định mới) → dừng; build lại với `$prevFeed` mới.
   - Thứ tự tải: blob/archive/ZIP → `versions.json` → `beta.json` → `index.html`. Không bao giờ ghi `latest.json`.
   - Sau khi tải, script kiểm hash live của `latest.json` (không đổi), `beta.json`, `versions.json`, và trang `/update` có `data-channel="beta"`.
9. Xác minh live trên station tổng hợp, ở chế độ beta:
```powershell
dotnet run --project "$root/tools/UpdateReleaseCheck/UpdateReleaseCheck.csproj" `
  --artifacts-path "$art/build" "-p:AudioAssemblyPath=$publishedDll" `
  -- $prevFeed $newFeed "$art/check-out" beta
```
   Phải PASS đủ:
   - máy kênh Ổn định **không** được mời;
   - máy kênh Beta cài beta (delta, hash, transaction, giữ dữ liệu trạm);
   - danh mục live đúng con trỏ;
   - **hạ cấp về bản ổn định trước** thành công;
   - ZIP beta tải đủ, đúng hash.

   `check-out` phải là thư mục mới.
10. Báo cáo:
    - Phân biệt mức bằng chứng: build / local / synthetic / live / station thật.
    - Ghi rõ đây là BETA và cách hạ cấp.
    - Không khẳng định phần cứng hay tương đương REW.
11. GitHub, chỉ sau khi bước 9 đạt. Kiểm remote `private` giống sonca-version-update bước 11.
    - Commit **chỉ** csproj và `docs/release-notes/<ver>.txt`, message `release: v<ver> beta`.
    - Tag annotated `v<ver>-beta`.
    - `git push private HEAD:main` rồi `git push private v<ver>-beta`. Không force, không push remote public.

## Khi bản beta không ổn
- **Máy bị ảnh hưởng:** Phiên bản → Cài phiên bản khác / Hạ cấp → bản ổn định. Hoặc chuyển Kênh cập nhật về Ổn định rồi đợi bản ổn định cao hơn.
- **Rút beta khỏi kênh:** phát hành beta mới hơn, hoặc bản ổn định cao hơn (bản này tự bỏ con trỏ beta cũ).
- Không xóa blob, archive hay `versions.json`: đó là đường hạ cấp của các máy khác.
- Lỗi updater của chính bản beta, khiến máy không cập nhật hay hạ cấp được: dùng ZIP portable của bản ổn định trên `/update` (mục "Gói portable · Cài mới / khôi phục"). Dữ liệu trạm nằm ngoài ZIP.

## Bất biến (giống sonca-version-update)
- Giữ `checking_config*.json`, calibration, standards/save standards, fail data, log, launcher của station.
- Không dùng cờ không tồn tại. Kiểm bằng `Get-Command "$root/scripts/deploy-update.ps1" -Syntax`; tham số hợp lệ của beta là `-Channel beta`.
- GitHub chỉ push remote `private` đã xác minh PRIVATE.
