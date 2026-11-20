# Cập nhật Sonca Audio Inspector

Từ bản 1.0.0, ứng dụng có updater tích hợp. Feed tĩnh mặc định là `https://inventory.acnos.store/updates/audio-inspector/`. Biến `SONCA_UPDATE_FEED` chỉ dùng cho test/staging.

Tài liệu này là quy trình vận hành. Nó không phản ánh trạng thái hiện tại của server. Mỗi lần phát hành, hãy đọc trạng thái live ở bước preflight rồi mới build và deploy. Ví dụ bên dưới phát hành **1.1.0**.

## Trạng thái phát hành: build khác live

| Trạng thái | Điều kiện | Bằng chứng |
|---|---|---|
| Đã build | `publish-update.ps1` kết thúc bằng dòng `Feed đã build + verify: <artifact>/feed. Chưa deploy.` | `<artifact>\feed`, `logs\build-feed.log`, `logs\verify-feed.log` |
| Đã deploy | `deploy-update.ps1` in đủ hai dòng `PASS` | artifact `*-deploy-update` |
| Live đã xác minh | Bước 4 không lỗi và verify chữ ký trên bản tải qua HTTP PASS | thư mục `http-mirror`, output verify |
| Trạm đã nhận | Tab Phiên bản tại trạm hiện v1.1 | kiểm từng trạm |

Ở trạng thái "Đã build" hoặc "Đã deploy", không ghi là đã phát hành. Trạm chỉ nhận bản mới khi tự kiểm lúc khởi động hoặc khi bấm kiểm thủ công, và chỉ lúc trạm đang rảnh.

## Biến dùng trong hướng dẫn

Chạy trong PowerShell, đứng tại thư mục gốc repo. Các giá trị server là mặc định của `scripts/deploy-update.ps1`; sửa nếu môi trường khác.

```powershell
$Repo      = '<thư-mục-repo-SoncaAudioInspector>'
# $Work được tạo bằng new-artifact.ps1 bên dưới, giữ metadata/SHA cùng log.
$Version   = '1.1.0'
$KeyPath   = "$env:USERPROFILE\.sonca-update\update-signing-key.pem"   # chỉ truyền đường dẫn
$SshKey    = "$HOME\.ssh\tracking_shopee_codex"
$VpsHost   = 'root@178.18.249.39'
$RemoteDir = '/opt/speaker-inventory/updates/audio-inspector'
$FeedUrl   = 'https://inventory.acnos.store/updates/audio-inspector'
$Ssh       = "$env:WINDIR\System32\OpenSSH\ssh.exe"
$Scp       = "$env:WINDIR\System32\OpenSSH\scp.exe"
$SshOpts   = @('-i', $SshKey, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
               '-o', 'UpdateHostKeys=no', '-o', 'ConnectTimeout=15')
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest nhanh hơn trên Windows PowerShell 5.1
Set-Location -LiteralPath $Repo
$Work = & .\scripts\new-artifact.ps1 -Description "Server update $Version" -Slug "server-update"
```

Nếu lưu các lệnh này thành file `.ps1` để chạy bằng Windows PowerShell 5.1, hãy lưu file ở dạng UTF-8 có BOM. Nếu không, chuỗi tiếng Việt trong `-Notes` và trong file tính năng sẽ bị lỗi mã hóa.

## 1. Preflight

### 1a. Repo và số phiên bản

```powershell
git status --short     # nên rỗng: build từ commit đã chốt
git log -1 --oneline   # ghi lại commit phát hành
Select-String -LiteralPath .\SoncaAudioInspector.csproj -Pattern '<(Version|AssemblyVersion|FileVersion|InformationalVersion|IncludeSourceRevisionInInformationalVersion)>'
```

Kết quả cần có:

- `Version` và `InformationalVersion` là `1.1.0`.
- `AssemblyVersion` và `FileVersion` là `1.1.0` hoặc `1.1.0.0`.
- `IncludeSourceRevisionInInformationalVersion` là `false`.

Header và build identity đọc số phiên bản qua `AppVersion`. `publish-portable.ps1` lấy phiên bản từ csproj. `publish-update.ps1` có mặc định `-Version 1.0.0`, nên **luôn truyền `-Version 1.1.0`** và giá trị này phải trùng csproj. Nếu lệch, tab Phiên bản sẽ báo version không khớp, còn client sẽ bỏ qua feed có version bằng hoặc thấp hơn bản đang chạy.

### 1b. Khóa ký và công cụ

```powershell
Test-Path -LiteralPath $KeyPath -PathType Leaf   # True. Không mở, không in, không copy nội dung
icacls $KeyPath                                  # chỉ tài khoản phát hành được đọc
Get-Command dotnet, $Ssh, $Scp, "$env:WINDIR\System32\tar.exe", curl.exe | Select-Object Source
```

Không cần rsync. Deploy chỉ dùng `ssh.exe`/`scp.exe` của Windows OpenSSH và `tar.exe` có sẵn trong Windows.

### 1c. Server, known-hosts, nginx

```powershell
& "$env:WINDIR\System32\OpenSSH\ssh-keygen.exe" -F ($VpsHost -replace '^.*@')   # phải có dòng khớp trong known_hosts
& $Ssh @SshOpts $VpsHost "test -d '$RemoteDir' && ls -1 '$RemoteDir' && tar --version | head -n 1"
curl.exe -sS -o NUL -w '%{http_code} %{redirect_url}\n' https://inventory.acnos.store/update
curl.exe -sSI "$FeedUrl/latest.json"
```

**Known-hosts là bắt buộc.** Nếu host chưa có trong `known_hosts` hoặc host key đổi, SSH sẽ dừng. Script cũng không bao giờ tự thêm key. Khi đó:

1. Lấy fingerprint qua kênh tin cậy, ví dụ console VPS: `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`.
2. So khớp fingerprint đó.
3. Chỉ sau khi khớp mới thêm vào `known_hosts`.

Không dùng `accept-new` hoặc `StrictHostKeyChecking=no`.

Kết quả cần có từ các lệnh trên:

- Server dùng GNU tar, vì deploy cần cờ `--skip-old-files`.
- `/update` trả `302` về `https://inventory.acnos.store/updates/audio-inspector/`.
- `latest.json` trả `200` kèm header chống cache.

Nếu thiếu redirect hoặc thiếu header chống cache, cấu hình nginx:

1. Đưa nội dung `deploy/nginx-updates-audio-inspector.conf` vào **cả server block 80 và 443**, chèn trực tiếp hoặc dùng `include`.
2. Kiểm tra cú pháp rồi reload:

```powershell
& $Ssh @SshOpts $VpsHost "nginx -t && systemctl reload nginx"
```

### 1d. Trạng thái live và snapshot feed

```powershell
$LiveLatest = Join-Path $Work 'live-latest.json'
Invoke-WebRequest -Uri "$FeedUrl/latest.json" -OutFile $LiveLatest -UseBasicParsing
$Live = Get-Content -LiteralPath $LiveLatest -Raw -Encoding UTF8 | ConvertFrom-Json
"Live hiện tại: $($Live.version)"
if ([version]$Live.version -ge [version]$Version) { throw "Feed live đã ở $($Live.version); chọn số phiên bản cao hơn." }

$SnapRoot = Join-Path $Work 'snapshot'
New-Item -ItemType Directory -Path $SnapRoot | Out-Null
& $Scp @SshOpts -r "${VpsHost}:$RemoteDir" $SnapRoot
if ($LASTEXITCODE -ne 0) { throw 'Snapshot feed thất bại.' }
$Snapshot = Join-Path $SnapRoot ($RemoteDir.Split('/')[-1])
if ((Get-FileHash -LiteralPath $LiveLatest).Hash -ne (Get-FileHash -LiteralPath "$Snapshot\latest.json").Hash) { throw 'Snapshot không khớp latest.json đang phục vụ qua HTTP.' }
if (Test-Path -LiteralPath "$Snapshot\versions\$Version.json") { throw "Server đã có versions/$Version.json; không dùng lại số phiên bản." }
Get-ChildItem -LiteralPath "$Snapshot\versions" -Filter *.json | Select-Object Name
```

Snapshot là bản đầy đủ của feed đang live. Nó có hai vai trò:

- Làm đầu vào `-Previous`. Builder sẽ kiểm tra lại manifest, chữ ký, blob và package cũ, rồi giữ chúng trong feed mới để trang lịch sử không mất link.
- Làm bằng chứng trạng thái server trước khi deploy.

Giữ nguyên thư mục `$Work`. Chỉ khi server chưa từng có feed (lần phát hành đầu tiên, `latest.json` trả 404) mới bỏ `-Previous`.

## 2. Build feed 1.1.0

```powershell
$Features = Join-Path $Work "features-$Version.txt"
[IO.File]::WriteAllLines($Features, [string[]]@(
    'Tab Pink noise riêng trong cửa sổ Ctrl+Shift+I',
    'Mặc định Pink random, Sub Cal 30–80 Hz, Butterworth bậc 2, 44,1 kHz',
    'Tùy chọn Pink periodic, chu kỳ 128K mẫu',
    'Xuất WAV mono PCM 24-bit 44,1 kHz'
), [Text.UTF8Encoding]::new($false))

.\scripts\publish-update.ps1 -Version $Version -KeyPath $KeyPath -Previous $Snapshot -FeaturesFile $Features `
    -Notes 'Thêm tab Pink noise: mặc định Pink random Sub Cal 30–80 Hz, periodic 128K tùy chọn'
# Tùy chọn: -Released '2026-11-05T03:00:00Z'. Nếu bỏ, builder dùng giờ UTC hiện tại.
```

Khi không truyền `-PortableDir`, script tự gọi `publish-portable.ps1 -Architecture x64 -SkipStationData`. Cờ này bỏ copy cấu hình route/thư mục `save standards` từ máy phát hành, tránh cả lỗi đường dẫn Windows quá dài; bộ lọc feed vẫn loại trừ cấu hình/calibration/dữ liệu trạm. Bản portable được tạo self-contained win-x64, có `libMathNetNumericsMKL.dll`, và lấy version từ csproj; log nằm ở `logs\publish-portable.log`.

Nếu tự truyền `-PortableDir`, thư mục đó phải là portable vừa build từ đúng commit phát hành. Không bao giờ dùng thư mục ứng dụng đang chạy ở trạm.

Sau khi build, script làm tiếp ba việc:

1. Build `UpdateFeedTool`.
2. Ký feed bằng `-KeyPath`.
3. Verify feed bằng public key trích từ `src/Infrastructure/Update/UpdateSignature.cs`. Nếu private key không khớp public key nhúng trong app, script dừng tại bước này.

Script không tự sinh khóa.

Kiểm tra kết quả build:

```powershell
$Art      = '<artifact in ở dòng "Feed đã build + verify">'   # thư mục *-update-feed trong artifacts
$Feed     = (Resolve-Path -LiteralPath (Join-Path $Art 'feed')).Path
$Portable = Join-Path (Get-ChildItem -LiteralPath $Art -Directory -Filter '*-portable-x64' | Select-Object -Last 1).FullName 'SoncaAudioInspector-win-x64'

(Get-Item -LiteralPath "$Portable\SoncaAudioInspector.exe").VersionInfo.ProductVersion            # 1.1.0
Get-Content -LiteralPath "$Portable\version-info.json" -Raw -Encoding UTF8 | ConvertFrom-Json | Format-List version, releasedUtc, features
(Get-Content -LiteralPath "$Feed\latest.json" -Raw -Encoding UTF8 | ConvertFrom-Json).version     # 1.1.0
Test-Path -LiteralPath "$Portable\libMathNetNumericsMKL.dll"                                      # True
Get-ChildItem -LiteralPath "$Feed\packages\$Version" | Get-FileHash -Algorithm SHA256
Get-Content -LiteralPath "$Art\logs\verify-feed.log" -Tail 20

# ZIP không được chứa dữ liệu trạm. Lệnh dưới phải không in gì.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Zip = [IO.Compression.ZipFile]::OpenRead((Get-ChildItem -LiteralPath "$Feed\packages\$Version" -Filter *.zip | Select-Object -First 1).FullName)
try { $Zip.Entries.FullName -match '(^|/)(checking_config[^/]*\.json|save standards/|calibrations/|standards/|fail data/|update-staging/|Chay_SoncaAudioInspector\.bat$)|\.old-|\.log$|\.jsonl$' } finally { $Zip.Dispose() }

# Không có private key lọt vào artifact. Lệnh chỉ in đường dẫn file, không in nội dung. Phải không in gì.
Get-ChildItem -LiteralPath $Art -Recurse -File | Select-String -SimpleMatch 'PRIVATE KEY' -List | Select-Object -ExpandProperty Path
```

Có thể smoke test bản portable trong artifact trên một máy test, không phải trạm vận hành. Mở Ctrl+Shift+I và kiểm tra:

- Tab Phiên bản hiện v1.1 kèm danh sách tính năng.
- Tab Pink noise hoạt động như mô tả trong phần [Nội dung v1.1.0](#nội-dung-v110-tab-pink-noise).

Đến bước này trạng thái là **Đã build**. Server chưa thay đổi gì.

## 3. Deploy

Người vận hành đã được phép deploy. Chạy WhatIf để soát lệnh, sau đó chạy thật.

```powershell
.\scripts\deploy-update.ps1 -FeedDir $Feed -VpsHost $VpsHost -SshKey $SshKey -RemoteDir $RemoteDir -WhatIf
```

WhatIf không kết nối server mà chỉ in ra lệnh sẽ chạy:

1. `mkdir -p` cho `files`, `versions`, `packages`.
2. Với từng thư mục: lệnh `ls -1R -p` để lấy danh sách file trên server, một lệnh `tar -cf` cục bộ, và lệnh `ssh … tar -xf - -C <RemoteDir> --skip-old-files --no-same-owner --no-same-permissions` nhận archive qua binary stdin.
3. `scp` `latest.json.sig` rồi `latest.json` lên tên tạm `.latest.json*.<guid>`.
4. Một lệnh `ssh` chạy `mv` chữ ký trước, manifest sau.
5. `scp` và `mv` cho `index.html`.
6. Các lệnh GET để kiểm tra.

Dòng `WhatIf assumes all missing` là bình thường. Khi chạy thật, script mới tính chính xác những file còn thiếu trên server.

```powershell
.\scripts\deploy-update.ps1 -FeedDir $Feed -VpsHost $VpsHost -SshKey $SshKey -RemoteDir $RemoteDir
```

Khi thành công, script in hai dòng:

- `PASS remote latest.json SHA-256: …`
- `PASS /update followed redirect and contains version 1.1.0`

Artifact `*-deploy-update` chứa:

- `transfer\*-existing.txt`: danh sách file trên server trước khi upload.
- Danh sách file cần gửi và các file `.tar`.
- `logs\*-extract.log`.
- `latest-downloaded.json` và `history-downloaded.html`.

Cách deploy giữ an toàn cho feed:

- **Chỉ thêm file.** Dữ liệu đã có trong `files/`, `versions/`, `packages/` không bị ghi đè (`--skip-old-files`) và không bị xóa.
- **Chỉ ghi đè bằng rename nguyên tử.** Chỉ cặp `latest.json`/`latest.json.sig` và `index.html` được thay, bằng cách upload tên tạm rồi `mv` từng file.
- **Cửa sổ lệch chữ ký là an toàn.** Trong khoảnh khắc giữa hai lệnh `mv`, client có thể thấy chữ ký mới đi với manifest cũ. Client sẽ verify thất bại, bỏ qua và thử lại ở lần mở sau. Client không bao giờ áp dụng manifest chưa xác minh.
- **Chạy lại không gây hại.** Có thể chạy lại cùng lệnh deploy.

## 4. Kiểm tra HTTP sau deploy

Script deploy chỉ kiểm `latest.json` và trang `/update`. Phần kiểm tra dưới đây đầy đủ hơn:

1. Tải toàn bộ feed qua HTTP vào một thư mục mới.
2. So SHA-256 từng file với bản local. Việc này bao phủ manifest, chữ ký, `versions/`, blob, ZIP và `index.html`.
3. Verify chữ ký trên chính bản vừa tải về, bằng public key nhúng trong app.

```powershell
$Mirror = Join-Path $Work 'http-mirror'
$Fail = [Collections.Generic.List[string]]::new()
foreach ($f in Get-ChildItem -LiteralPath $Feed -Recurse -File) {
    $rel = $f.FullName.Substring($Feed.Length + 1).Replace('\', '/')
    $url = "$FeedUrl/" + (($rel -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
    if ($rel.StartsWith("packages/")) { $url += "?sha256=" + (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    $dst = Join-Path $Mirror $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
    try { Invoke-WebRequest -Uri $url -OutFile $dst -UseBasicParsing } catch { $Fail.Add("HTTP $rel"); continue }
    if ((Get-FileHash -LiteralPath $dst).Hash -ne (Get-FileHash -LiteralPath $f.FullName).Hash) { $Fail.Add("HASH $rel") }
}
if ($Fail.Count) { $Fail; throw 'Feed phục vụ qua HTTP chưa khớp bản local.' }
dotnet "$Art\bin\UpdateFeedTool.dll" verify --feed $Mirror --public-key "$Art\public-key.pem"
if ($LASTEXITCODE -ne 0) { throw 'Verify chữ ký trên bản tải qua HTTP thất bại.' }
Get-ChildItem -LiteralPath "$Mirror\packages\$Version" | Get-FileHash -Algorithm SHA256
curl.exe -sSI "$FeedUrl/latest.json" | Select-String 'HTTP/|Cache-Control'
curl.exe -sSI "$FeedUrl/" | Select-String 'HTTP/|Cache-Control'
```

Xử lý khi có lỗi:

- **Lỗi ở `latest.json`, `latest.json.sig` hoặc `index.html`:** chạy lại lệnh deploy.
- **Lỗi `HASH`/`HTTP` ở file trong `files/`, `versions/`, `packages/` không có trong snapshot:** đây là file mới của bản này, bị hỏng hoặc thiếu trên server. Vì `--skip-old-files`, chạy lại deploy sẽ không sửa được file đã tồn tại. Xóa đúng file đó rồi chạy lại deploy:

  ```powershell
  & $Ssh @SshOpts $VpsHost "rm -f -- '$RemoteDir/<đường-dẫn-tương-đối>'"
  ```

- **Lỗi ở file đã có trong snapshot:** dừng lại. Không ghi đè lịch sử. Đối chiếu file đó với snapshot và với `transfer\*-existing.txt`.

Trong lúc chưa sửa xong, client vẫn an toàn: chữ ký sai thì client bỏ qua, file tải sai size hoặc SHA-256 thì client hủy và chưa thay file nào.

## 5. Ghi nhận phát hành

Ghi vào sổ phát hành:

- commit;
- đường dẫn `$Art`, artifact deploy và `$Work`;
- SHA-256 của `latest.json` và ZIP;
- output verify trên mirror;
- thời điểm UTC.

Chỉ đánh dấu **Live đã xác minh** khi bước 4 PASS. Mỗi trạm được xác nhận riêng qua tab Phiên bản. Không suy ra "đã cập nhật" từ việc feed đã live.

## Nội dung v1.1.0: tab Pink noise

Cửa sổ Ctrl+Shift+I có thêm tab **Pink noise** riêng. Thông số mặc định và tùy chọn:

| Thông số | Giá trị |
|---|---|
| Loại tín hiệu mặc định | Pink random |
| Dải mặc định | Sub Cal 30–80 Hz, giới hạn bằng lọc Butterworth bậc 2 |
| Tần số lấy mẫu | 44,1 kHz |
| Tùy chọn | Pink periodic, chu kỳ 128K (131 072 mẫu, khoảng 2,97 s ở 44,1 kHz) |
| File xuất | WAV mono PCM 24-bit 44,1 kHz |

Các lựa chọn này bám theo thiết lập tương ứng của REW (Pink random/periodic, Sub cal), nhưng đây là bộ sinh tín hiệu riêng. **Không khẳng định tín hiệu giống hệt REW** theo từng mẫu hay theo phổ. Khi cần đối chiếu, hãy đo phổ thực tế.

Kiểm nhanh header WAV vừa xuất. Lệnh giả định header chuẩn, chunk `fmt` nằm ngay sau `RIFF/WAVE`:

```powershell
$b = [IO.File]::ReadAllBytes('<đường-dẫn-file.wav>')
'{0} ch, {1} Hz, {2}-bit' -f [BitConverter]::ToUInt16($b, 22), [BitConverter]::ToUInt32($b, 24), [BitConverter]::ToUInt16($b, 34)
# Kỳ vọng: 1 ch, 44100 Hz, 24-bit
```

## Feed, manifest và chữ ký

**Manifest**

- UTF-8, schema 1, app `SoncaAudioInspector`, architecture `win-x64`.
- Chữ ký là base64 ECDSA P-256/SHA-256 dạng IEEE P1363, ký trên đúng byte của manifest.

**Nội dung feed**

- `files/<sha256>` là blob bất biến.
- `versions/<version>.json[.sig]` lưu lịch sử.
- Không sửa blob hoặc version cũ, và không dùng lại số phiên bản.

**ZIP**

- Chỉ chứa các file được phép cập nhật.
- Máy cài mới tự khởi tạo dữ liệu trạm riêng. Không nhúng dữ liệu đo hoặc calibration của máy phát hành vào ZIP.

**Điểm tin cậy**

- Client không bao giờ áp dụng manifest chưa xác minh.
- Public key nhúng trong app là điểm tin cậy duy nhất. Client không nhận khóa thay thế từ manifest.

## Khóa ký

**Lưu giữ khóa**

- Chỉ người phát hành được đọc private key. Giới hạn ACL cho đúng tài khoản đó.
- Luôn tham chiếu khóa **bằng đường dẫn** (`-KeyPath`). Không mở, in, dán hay copy nội dung khóa vào Git, portable, feed, log, chat hoặc ticket.
- Sao lưu ít nhất hai bản đã mã hóa, đặt ở hai nơi tách biệt.
- Kiểm tra khả năng phục hồi bằng một feed test có kiểm soát.

**Mất khóa**

- Các bản app đã cài sẽ không tin khóa mới gửi qua feed này.
- Phân phối thủ công bản đầy đủ chứa public key mới qua kênh tin cậy. Sau đó mới tiếp tục dùng tự cập nhật.

**Lộ khóa**

- Ngừng phát feed.
- Thay khóa và phân phối thủ công bản có public key mới.
- Không dùng chữ ký của khóa đã lộ để đổi điểm tin cậy.

## Rollback và hành vi trạm

### Rollback

1. Lấy lại nội dung tốt trước đây và phát hành dưới **số phiên bản cao hơn**, ví dụ 1.1.1 chứa mã của bản tốt. Client bỏ qua phiên bản bằng hoặc thấp hơn bản đang chạy.
2. Tăng version trong csproj.
3. Build với `-Previous` là snapshot mới chụp.

Không hạ số phiên bản, không ghi `latest.json` cũ đè lên bản mới, không xóa `versions/` hay `files/`.

### Bản beta và hạ cấp trên trạm (từ 1.4.4)

- **Bản beta không ép máy khác cập nhật.** Phát hành bằng skill `sonca-beta-release`: `publish-update.ps1 -Channel beta` rồi `deploy-update.ps1 -Channel beta`.
  - Feed beta chỉ thêm `beta.json(.sig)` và giữ `latest.json` nguyên từng byte.
  - Máy kênh Ổn định (mặc định) vẫn chỉ đọc `latest.json`, nên không bị hỏi cập nhật.
- **Chọn kênh trên trạm:** Ctrl+Shift+I → **Phiên bản** → **Kênh cập nhật**.
  - *Beta*: nhận cả bản beta; bản beta mặc định ở kênh này.
  - Máy ≤ 1.4.3 chưa có ô chọn kênh. Muốn thử beta thì cài ZIP của thẻ BETA trên trang `/update`.
- **Hạ cấp / cài lại phiên bản bất kỳ:** Phiên bản → **Cài phiên bản khác / Hạ cấp** → **Tải danh sách phiên bản** → chọn bản → **Cài phiên bản đã chọn**.
  - App đọc `versions.json` có chữ ký, rồi `versions/<v>.json` có chữ ký, chỉ tải file khác và thay file có sao lưu/rollback.
  - Dữ liệu trạm luôn được loại trừ như mục dưới.
  - Bản cũ (≤ 1.4.3) sau khi hạ cấp không có ô chọn kênh/hạ cấp. Từ bản đó lên lại bằng **Kiểm tra cập nhật** hoặc ZIP.
- **Đưa beta lên ổn định:** phát hành số cao hơn bằng `sonca-version-update`, với `SoncaReleaseChannel=stable`. Không phát hành lại cùng số. Bản ổn định cao hơn tự bỏ con trỏ beta cũ.

### Khi nào trạm hỏi và thay file

- Trạm chỉ hỏi và thay file khi không đo, không sửa model, không chạy đo nâng cao. Điều kiện này được kiểm cả trước khi hỏi lẫn ngay trước khi thay file.
- Nếu người dùng chọn **Để sau**, trạm không hỏi lại trong phiên đó.
- Thư mục ứng dụng tại trạm phải có quyền ghi. Không đặt trong Program Files nếu tài khoản vận hành không có quyền ghi.

### Tải và thay file

- Trạm chỉ tải những file đổi hoặc thiếu, xác định theo size và SHA-256.
- Nếu tải sai, trạm hủy và chưa thay file nào.
- Nếu thay file lỗi giữa chừng, trạm khôi phục toàn bộ file đã đổi.
- Nếu lỗi hệ thống ngăn rollback, trạm giữ `.old-*` để phục hồi thủ công và không xóa bản sao cuối cùng.
- Lần mở sau, trạm dọn staging cũ và backup trong khả năng. Nếu thiếu file gốc, trạm phục hồi từ backup thay vì xóa.

### Dữ liệu luôn được loại trừ

Trạm không xóa file lạ ngoài manifest. Cả builder lẫn client luôn loại trừ:

- `checking_config*.json`
- `save standards/**`, `calibrations/**`, `standards/**`, `fail data/**`
- `update-staging/**`, `*.old-*`
- `*.log`, `*.jsonl`
- `Chay_SoncaAudioInspector.bat` (giữ launcher đang có)

### Đường dẫn bị từ chối

Client từ chối:

- đường dẫn tuyệt đối, có ổ đĩa hoặc UNC;
- đường dẫn chứa `..`, tên Windows không hợp lệ, đường dẫn trùng hoặc xung đột;
- symlink/junction, hoặc bất kỳ đường dẫn nào thoát ra ngoài thư mục app.

Bước cleanup cũng bỏ qua các thư mục dữ liệu trạm. Không dùng updater để phân phối cấu hình hoặc calibration.

## Ghi chú tính năng và trang lịch sử

### File tính năng

- File UTF-8, mỗi dòng một tính năng. Builder bỏ dòng trống.
- Tham số tương ứng của `UpdateFeedTool build` là `--features-file` và `--released`. Nếu bỏ `--released`, builder dùng giờ UTC hiện tại.
- `features` là trường tùy chọn trong manifest đã ký. Client cũ không biết trường này vẫn tương thích.
- Hộp thoại cập nhật hiển thị tối đa tám dòng tính năng, thêm "…" nếu còn nữa.

### version-info.json

- Builder **ghi `version-info.json` vào portable trước khi băm**, với các trường `version`, `releasedUtc`, `features`.
- File này thuộc manifest và ZIP, được cập nhật như file chương trình. Không đưa nó vào danh sách loại trừ.
- Chỉ chạy builder trên portable đã chuẩn bị để phát hành.

### Trang lịch sử (`index.html`)

- Một file tĩnh, CSS inline, không có JavaScript và không có tài nguyên ngoài. Mọi notes và features đều được escape HTML.
- Liệt kê các manifest trong `versions/` theo version giảm dần, làm nổi bật bản mới nhất.
- Mỗi bản có: ngày UTC, tính năng, link ZIP, dung lượng và SHA-256 của ZIP.
- Lịch sử chỉ đầy đủ khi các bản trước còn trong feed. Vì vậy luôn truyền `-Previous` là snapshot đầy đủ của feed live.

### nginx

- Snippet nằm trong **cả server block 80 và 443**.
- `/update` và `/update/` redirect 302 tới `/updates/audio-inspector/`, dùng `index index.html`.
- HTML và `latest.json` không được cache.

## Config bắt buộc (từ v1.3)

`checking_config.json` của mọi trạm là một file có chữ ký trong `config/` của feed:
`config/latest.json` (+ `.sig`, cùng khóa ký với bản cập nhật) trỏ tới `config/files/<sha256>.json` (bất biến).
App đọc ở mỗi lần mở (SYS002), ghi đúng từng byte, không cho bỏ qua; mất mạng thì dùng bản đã xác minh lần trước.
Client thêm `?t=` vào hai file con trỏ nên Cloudflare không trả bản cũ; nội dung theo hash nên cache an toàn.

```powershell
# Máy quản trị: config đang dùng = %LOCALAPPDATA%\SoncaAudioInspector\checking_config.json (sửa với SONCA_CONFIG_ADMIN=1)
& "$root/scripts/publish-config.ps1" -Notes 'Mô tả thay đổi'          # ký + verify, in CONFIG_FEED=<artifact>/config-feed/config
& "$root/scripts/deploy-config.ps1" -FeedDir $configFeed -WhatIf     # xem kế hoạch
& "$root/scripts/deploy-config.ps1" -FeedDir $configFeed             # upload content → pointer, rồi so byte qua URL công khai
```

- **Thứ tự khi phát hành bản app đầu tiên có tính năng này (1.3.0): deploy config TRƯỚC, app SAU.** App 1.3 mở lên mà chưa có
  config trên server và trạm chưa từng tải được bản nào → SYS002 Không Đạt, trạm không vào được.
- Serial tăng dần (mặc định Unix giây). Client từ chối serial thấp hơn bản đã xác minh; rollback = phát hành lại nội dung cũ với serial mới.
- nginx phục vụ cả thư mục feed, nên `config/` không cần cấu hình thêm. File tạm là dotfile (bị 403) cho tới khi `mv`.

## Tab Phiên bản trong ứng dụng

Nhấn **Ctrl+Shift+I** và chọn tab **Phiên bản** (tên lấy theo `AppVersion.Display`, ví dụ `Phiên bản — v1.1`).

### Thông tin hiển thị

- Version đang chạy.
- Ngày ghi assembly, theo giờ máy.
- Tính năng, đọc từ `version-info.json` cạnh file exe.
- Kênh cập nhật.
- Trạng thái lần kiểm gần nhất.

Nếu thiếu `version-info.json`, tab hiện ghi chú "bản phát triển". Nếu JSON sai hoặc version không khớp, tab hiện lỗi ngắn và ứng dụng vẫn chạy bình thường.

### Nút Kiểm tra cập nhật

- Gọi cùng luồng với lần kiểm khi khởi động, dùng chung trạng thái và quy tắc "đang chạy".
- Chỉ hỏi và thay file khi trạm không đo và không sửa model.
- Nếu đã chọn **Để sau**, phiên hiện tại không hỏi lại, kể cả khi kiểm thủ công. Trạng thái vẫn được cập nhật.

### Nút Lịch sử phiên bản

Mở `https://inventory.acnos.store/update` bằng trình duyệt mặc định.

## Kiểm thử offline

- `SONCA_UPDATE_DISABLE_AUTO_CHECK=1` tắt lần tự kiểm khi cửa sổ Loaded.
- FUC mở `MeasurementDetailsWindow` riêng, đọc release-info giả và dùng callback cục bộ. Nó không khởi tạo MainWindow, không gọi HTTP, audio hay BLE, và không mở trình duyệt.
- Các cờ này chỉ dùng để kiểm thử. Không bật chúng ở trạm vận hành.

## Kiểm tra updater trực tiếp sau deploy

Có thể chạy harness nhận feed live bằng client thật, tải delta và áp dụng lên trạm tổng hợp trong artifact mới; không sửa trạm đang vận hành:

```powershell
$Assembly = Join-Path $Portable 'SoncaAudioInspector.dll'
dotnet run --project tools/UpdateReleaseCheck/UpdateReleaseCheck.csproj --artifacts-path (Join-Path $Work 'live-check-build') "-p:AudioAssemblyPath=$Assembly" -- $Snapshot $Feed (Join-Path $Work 'live-check')
```

Harness xác minh chữ ký bằng public key nhúng, kiểm mọi file cài theo size/SHA-256, giữ dữ liệu trạm tổng hợp, bỏ qua phiên bản bằng hiện tại và tải ZIP public để đối chiếu hash.

Tài liệu được Claude Opus soạn; người triển khai rà lại theo script thực tế. Bằng chứng tác giả/model nằm trong artifact phát hành, không bao gồm nội dung khóa.


### Cache gói ZIP

Link ZIP trên trang lịch sử có query SHA-256 theo nội dung. Chỉ kiểm/tải gói sau khi upload hoàn tất và dùng đúng link có hash; CDN có thể cache phản hồi chưa đủ byte nếu một URL ZIP bị truy cập trong lúc truyền. Updater tự động tải blob theo SHA, không dùng ZIP. Hash cuối cùng phải khớp bản local, không suy ra upload hoàn tất từ HEAD bị cache.

## Chỉ cập nhật trang lịch sử / hướng dẫn

Trang `/update` ưu tiên hướng dẫn người đã cài app dùng **Ctrl+Shift+I → Phiên bản → Kiểm tra cập nhật**. ZIP là gói portable phụ cho cài mới/khôi phục, không phải thao tác cập nhật thường ngày. Chưa có `Update.exe` riêng.

Với thay đổi nội dung/trình bày trang, dùng `UpdateFeedTool history --feed <cached-live-feed> --public-key <production-public-key> --out <new-artifact/index.html>` để render mà không sửa feed nguồn. Sau đó:

```powershell
scripts/deploy-update.ps1 -FeedDir $CachedLiveFeed -PageOnly -PageFile $NewPage -WhatIf
scripts/deploy-update.ps1 -FeedDir $CachedLiveFeed -PageOnly -PageFile $NewPage
```

Luồng này chỉ upload HTML qua tên tạm, kiểm hash và rename. Script từ chối khi manifest/chữ ký live lệch cache, rồi đối chiếu chúng lần nữa sau deploy. Không cần tăng version app, ký lại manifest hay truyền ZIP. Cache cũ có thể tiếp tục dùng để kiểm release nếu vẫn khớp, nhưng khi cần HTML hiện hành hãy lấy bản vừa render/đã deploy.
Kiểm tra HTML-only tại origin dùng SHA-256 chính xác. CDN có thể thêm beacon hoặc JS-detection loader của Cloudflare vào HTML public; verifier chỉ bỏ hai kiểu script đã nhận diện và chuẩn hóa khoảng trắng giữa thẻ trước khi đối chiếu nội dung. Script/HTML khác vẫn làm kiểm tra thất bại. Cơ chế này chỉ áp dụng HTML, không áp dụng manifest/chữ ký/blob/ZIP.
