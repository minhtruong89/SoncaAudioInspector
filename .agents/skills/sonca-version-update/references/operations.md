# Vận hành chi tiết: sonca-version-update

## Bản đồ công cụ (hiện có)
- `SoncaAudioInspector.csproj`: bốn trường version; AppVersion.Display lấy từ assembly.
- `scripts/publish-portable.ps1 -Architecture x64 -SkipStationData`: build portable self-contained multi-file.
- `scripts/publish-update.ps1 -PortableDir -Version -Notes -Previous -FeaturesFile`:
  - Thiếu `-PortableDir` thì tự tạo portable (SkipStationData) -> tránh build hai lần.
  - `-Version` mặc định 1.0.0: luôn truyền rõ, khớp csproj.
  - `-Previous`: feed trước ĐẦY ĐỦ. Chỉ manifest là KHÔNG đủ.
- `scripts/deploy-update.ps1 -FeedDir [-WhatIf]`.
- `tools/UpdateReleaseCheck`: kiểm tra synthetic station.
- `scripts/new-artifact.ps1`: tạo artifact có SHA/thời gian, giữ 7 ngày.

Trước khi dùng tham số nào, xác nhận nó tồn tại:
```powershell
Get-Command "$root/scripts/publish-update.ps1" -Syntax
```

## Hành vi deploy hiện tại (để đánh giá rủi ro)
- Thứ tự: blob hash còn thiếu, archive, toàn bộ ZIP mới, rồi `versions.json` (danh mục có chữ ký), rồi latest/signature (và `beta.json` nếu feed còn giữ), cuối cùng index.
- `-Channel beta` (skill sonca-beta-release): kiểm `latest.json` live bằng feed trước khi tải; chỉ ghi `versions.json`, `beta.json`, index; KHÔNG ghi `latest.json`.
- Client từ 1.4.4: kênh Ổn định chỉ đọc `latest.json`; kênh Beta đọc thêm `beta.json`; "Cài phiên bản khác / Hạ cấp" đọc `versions.json` + `versions/<v>.json`.
- Tar KHÔNG nén, giải nén thẳng vào đường dẫn public.
- Chỉ latest/signature/index dùng tên tạm rồi rename.
- Cặp manifest/signature không phải một thao tác atomic; lệch nhau thì client fail closed.
- Updater tự động dùng blob thay đổi, KHÔNG dùng ZIP. ZIP phục vụ tải thủ công/lịch sử.

## Cache feed trước (tối ưu hỗ trợ NGAY)
Mục tiêu: không tải toàn bộ feed mỗi lần phát hành.
1. Tải riêng latest manifest + signature vào artifact mới.
2. Xác minh chữ ký: public key production nhúng, byte UTF-8 chính xác, ECDSA P-256 định dạng P1363. Sai -> dừng.
3. Tìm artifact cache feed còn trong hạn lưu giữ:
   - latest cache trùng byte với latest live;
   - danh mục version đã lưu trữ khớp;
   - mọi blob được tham chiếu tồn tại và đúng size + SHA-256;
   - Mỗi archive version được xác minh chữ ký; ZIP cũ có đủ và hash khớp bằng chứng phát hành tin cậy, hoặc đối chiếu từng entry với manifest đã ký. Chỉ có file tồn tại là chưa đủ.
4. Đủ -> dùng làm `-Previous`.
5. Remote đã đổi hoặc cache thiếu -> đối chiếu, chỉ tải dữ liệu bắt buộc còn thiếu.
6. Cache lạnh -> hydrate đầy đủ/snapshot theo hợp đồng công cụ hiện tại; báo số byte dự kiến trước. Không âm thầm bỏ lịch sử.
7. Cache nằm trong artifact được quản lý, không dùng cache global ngoài quản lý.

## Tỉa phiên bản cũ (chỉ khi chủ sở hữu yêu cầu rõ)
- 2026-11-14: theo yêu cầu, đã xóa 1.0.0–1.4.0. Feed live chỉ còn 1.4.1–1.4.4.
- Cache `-Previous` đúng sau lần tỉa là `artifacts/20261114-152454-176Z-vps-cleanup/feed-pruned`, hoặc hydrate mới.
- **Không** dùng cache cũ còn chứa các bản đã xóa: `CopyHistory` sẽ đưa chúng trở lại server. Danh sách version của cache phải trùng `versions.json` live.
- Cách làm:
  1. `UpdateFeedTool prune --feed <feed khớp live> --keep <các bản giữ, phải gồm latest.json và beta.json> --key <khóa ký> --out <mới>`. Lệnh ký lại `versions.json`, tạo lại `index.html` và ghi `prune-plan.txt`.
  2. `verify` trên feed sau khi tỉa.
  3. Trên server dùng **một** kết nối SSH: kiểm hash latest/beta live, hash file truyền và định dạng từng đường dẫn; chặn mọi blob còn được bản giữ dùng; đặt catalog/trang; rồi mới xóa đúng danh sách. Mẫu có chế độ dry-run: `artifacts/20261114-152454-176Z-vps-cleanup/transfer/run.sh`.
- VPS có UFW `22/tcp LIMIT` (6 kết nối / 30 s): gộp lệnh, tránh chuỗi scp/ssh dày.

## Tái sử dụng build/test
- Build một lần; ký, kiểm, deploy cùng đúng thư mục output đó.
- Input liên quan (source SHA, csproj, SDK/runtime, cờ publish) không đổi -> tái dùng build và kết quả test, ghi rõ trong báo cáo.
- Không chép DLL cũ chỉ vì trùng tên/version.
- DLL bên thứ ba thay đổi có THỂ do biến thiên ReadyToRun, chưa chứng minh. Không đổi SDK/runtime/R2R ngầm. Ghim/xác định tính cần so sánh có kiểm soát, không build hai lần mỗi bản vá.

## Báo cáo trước truyền tải
- SHA nguồn, version binary (từ assembly đã publish).
- Số file thay đổi và tổng byte; kích thước ZIP; byte snapshot/hydrate nếu có.
- Kết quả `-WhatIf` của deploy.

## Kiểm tra (tương xứng, đúng thời điểm)
Trước deploy: test phần đổi và verify feed local. Chỉ chạy lệnh bên dưới sau khi version dự kiến đã live; nếu chỉ build/local/soạn skill thì không gọi live harness.
```powershell
dotnet run --project "$root/tools/UpdateReleaseCheck/UpdateReleaseCheck.csproj" `
  --artifacts-path "$art/build" "-p:AudioAssemblyPath=$publishedDll" `
  -- $prevFeed $newFeed "$art/check-out"
```
- `check-out` phải là thư mục MỚI.
- Phạm vi: cài chỉ trong station giả lập; chữ ký; tải blob đổi qua HTTP; hash; bảo toàn dữ liệu station; bỏ qua khi cùng version; tải ZIP đầy đủ.
- Ghi rõ mức bằng chứng: build / local / synthetic / live / station thật.
- Không hardcode số lượng test hay version latest; dữ kiện lịch sử không chứng minh trạng thái live tương lai.
- Không khẳng định phần cứng hay tương đương REW.

## Xác minh sau deploy
- Không HEAD/GET URL ZIP cuối khi upload chưa xong (bài học: CDN cache bản thiếu của URL trần).
- Generator lịch sử và live harness dùng `?sha256=<package-content-hash>`.
- Tải trọn vẹn qua URL hash, so SHA-256. HEAD 200 không đủ.
- Kiểm latest + signature live xác minh được và trỏ đúng version mới.
- Cache CDN hỏng: purge có ủy quyền hoặc URL hash mới đã xác minh. Không báo đã purge nếu chưa làm.

## GitHub (sau khi live đạt)
Chủ sở hữu yêu cầu mỗi lần phát hành đều đẩy mã nguồn lên repo **private** (quyết định 2026-11-09). Các remote public cũ không được nhận push mới.
```powershell
$ver = "<version vừa phát hành>"
git -C $root remote get-url private          # phải là https://github.com/zenith1379/SoncaAudioInspector-Private.git
gh repo view zenith1379/SoncaAudioInspector-Private --json visibility -q .visibility   # phải in PRIVATE
git -C $root add SoncaAudioInspector.csproj "docs/release-notes/$ver.txt"
git -C $root commit -m "release: v$ver"
git -C $root tag -a "v$ver" -m "SoncaAudioInspector $ver"
git -C $root push private HEAD:main
git -C $root push private "v$ver"
```
- Working tree có thay đổi khác ngoài file phát hành → không commit chúng; báo lại.
- Push bị từ chối (non-fast-forward) → dừng, không force; báo lại để chủ sở hữu quyết định.

## Dữ liệu station phải giữ
checkingconfig `checking_config*.json`, calibration, standards/save standards, fail data, log, launcher.
Blob và archive phiên bản đã phát hành bất biến; latest/index là con trỏ được cập nhật. Rollback bằng version cao hơn.

## Tham chiếu hiệu năng (lịch sử, không phải cam kết)
Bản trước: snapshot feed trước nén ~278 MB; 34 file đổi/87.8 MB; ZIP đầy đủ 158 MB; ~28 phút gồm SSH chậm và viết guide. Không suy ra thời gian cho bản sau.

## Single-file (định tuyến riêng)
Chỉ khi người dùng chủ ý muốn phát hành EXE single-file thủ công:
- Tạo nhiệm vụ xác thực đóng gói riêng (PublishSingleFile), không thay luồng cập nhật thường.
- Rà soát native extraction/resource; config/calibration/asset station giữ bên ngoài.
- Test: ghi thư mục temp, antivirus, hành vi runtime, khôi phục khi lỗi, thay EXE đang chạy.
- Không tuyên bố đã hoạt động khi chưa test.

## Chưa triển khai (tương lai, KHÔNG chạy được)
- Publish delta độc lập ZIP, giữ link lịch sử tới khi package sẵn sàng.
- Staging remote ẩn, xác minh size/hash, promote atomic bất biến.
- Tar nén.
- Tái dùng lịch sử phía server / lazy cache.
- Điều tra build xác định / R2R.
Chỉ làm khi được yêu cầu tối ưu pipeline rõ ràng: implement + test trước, không dùng trong task version nhỏ.

## Cập nhật một file bằng tay
- Với build hiện tại, EXE chỉ là apphost; chép riêng nó không cập nhật mã tính năng trong DLL.
- `Update.exe` online đọc feed đã ký và tính delta theo file thực tế là hướng phù hợp nếu muốn người dùng chỉ chạy một file: kiểm đúng app/version nền, chờ đóng app, kiểm chữ ký/hash, backup, thay giao dịch bằng installer hiện có, rollback lỗi và khởi động lại. Chưa có gói/helper này trong repo; không mô tả là chức năng đã có.
- Đóng gói app thành single-file là lựa chọn khác, cần phân phối toàn binary lớn mỗi bản và xác minh native/resource/extraction. Không tự đổi format khi người dùng chỉ hỏi ý kiến.
- Tham khảo chính thức .NET: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview . Dữ liệu trạm tiếp tục nằm ngoài binary.
## Trang cập nhật (HTML-only, đã hỗ trợ)
- Giao diện chính hướng dẫn app → Ctrl+Shift+I → Phiên bản → Kiểm tra cập nhật. Trang web không có nút giả vờ mở/updater app khi chưa có protocol handler.
- ZIP nằm trong mục phụ “Gói portable · Cài mới / khôi phục”, nhắc giữ dữ liệu trạm. Không hứa app vận hành offline chỉ vì có thể chép ZIP qua USB.
- Chọn cache của release live và production public key; tạo HTML vào artifact mới, ngoài feed cũ:
```powershell
dotnet $feedTool history --feed $prevFeed --public-key $publicKey --out "$art/index.html"
& "$root/scripts/deploy-update.ps1" -FeedDir $prevFeed -PageOnly -PageFile "$art/index.html" -WhatIf
& "$root/scripts/deploy-update.ps1" -FeedDir $prevFeed -PageOnly -PageFile "$art/index.html"
```
- `history` kiểm chữ ký và render từ các archive/package có sẵn; không ký hay sửa feed nguồn. `$feedTool` là UpdateFeedTool.dll build trong artifact.
- `-PageOnly` đối chiếu manifest/chữ ký live trước và sau, kiểm hash origin ngay trước rename HTML, rồi đối chiếu nội dung public /update; chỉ chấp nhận beacon và JS-detection loader Cloudflare đã nhận diện cùng khác biệt khoảng trắng giữa thẻ. Hash HTML tại origin vẫn được kiểm chính xác. Cache stale thì dừng và render từ release hiện tại.
- Không bump version, build app hoặc truyền lại ZIP/blob cho thay đổi HTML. Kiểm giao diện và link tải, xác nhận bản manifest/chữ ký và app release được giữ nguyên.