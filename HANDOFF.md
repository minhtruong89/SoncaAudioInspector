# HANDOFF — SoncaAudioInspector (cập nhật 2026-11-19)

Đọc file này trước khi làm việc trên repo, nhất là khi bạn là agent ở **máy khác** máy build chính.
Sau đó đọc `AGENTS.md`, `ARTIFACTS.md`, `note/claude/notes.md` (sổ ghi chú chung) và skill liên quan trong `.agents/skills/`.

## 1. Trạng thái hiện tại

| Mục | Giá trị |
|---|---|
| App live (kênh Ổn định) | **v1.5.6** — commit phát hành `c9c3f50`, tag `v1.5.6`, code `0cc69e2` (HEAD `5da9e78`) |
| Feed cập nhật | `https://inventory.acnos.store/updates/audio-inspector/` (lịch sử: `/update`) |
| Web + API | `https://inventory.acnos.store` — repo riêng `Speaker Inventory System` (không trên GitHub này), commit live `95cf33d` (release `20261119-032810`) |
| Cấu hình C360 ACTIVE | **serial 5** (rev 5, revision `cmwn08a4y000032mbf78apmyl`, sha256 `4987d62b…6c20`, agent duyệt 2026-11-19 qua `agent-publish`), cần app ≥ 1.5.5 |
| Bản tạm (kênh Beta) | trang `/temp-updates`, feed `/api/app/updates/temp/` — chưa có bản tạm nào |

### C360 (serial 5)
IO001 MIC Echo/Reverb −24 · IO002 FBX di tần −30 · IO003 MIC khô −24 · IO004 LINE IN · IO005 USB A · IO006 Mic KT đáp tuyến/THD ·
IO007 BLUETOOTH · IO008 Rè Pink + Chirp (ảnh QA là hình chirp; chỉ ngưỡng tuyệt đối Pink < 30 %, Chirp < 10 dB, không cần line chuẩn Rè) · IO009 Gain trước hú (tùy chọn, FAIL khi FBX thêm < 6 dB nhưng không đổi kết luận tổng).
IO002 phải có di tần (|di tần| ≥ 0,5 Hz khi FBX bật). BLE tự kết nối loa tên bắt đầu `KBEATBOX` RSSI mạnh nhất, INFO phải là model 1075.
Chung: BLE MASTER/MUSIC/MIC 50%, Windows phát 75% / thu 100% (ghi đè từng bài bằng `TestVolume` của bài), 2 lượt / 1 đạt.

## 2. Tính năng mới 1.5.3–1.5.6 (để không làm lại)
- Ngõ mới: `AcousticRubBuzz` (IO008), `MicFeedbackGain` (IO009) — `src/Automation/Electronic/`.
- Popup A/B khi ngõ analog không có tín hiệu (`C360NoSignalRetryBench`), kiểm ngầm Analog 1/2–3/4 lúc bắt đầu.
- Tự lấy LINE CHUẨN khi thiếu (hỏi một lần); riêng IO008 từ **1.5.5** chỉ chấm theo ngưỡng tuyệt đối (Pink < 30 %, Chirp < `AbsoluteFailDb`), không có `CẦN DUYỆT`, không đọc/hỏi/lưu chuẩn Rè.
- Hỏi xóa thiết bị BT khi đo xong; nhật ký `auto-test-log.txt` trong thư mục phiên; chờ 2 s (BT 6 s) trước khi dừng vì mất thiết bị.
- Ghi chú FBX khi config chưa đặt kỳ vọng; cửa sổ vừa 100% màn hình; tab Rè chỉ hiện đồ thị chirp; popup chờ thiết bị ngắn.
- **1.5.5 (`186f6c5`..`157e675`)**: `verify.txt` một lần theo user Windows (DPAPI) + tự đăng nhập tài khoản ghi nhớ; tiền tố tên BLE (`InOut.BleNamePrefix`, Ctrl+Shift+M) chọn loa RSSI mạnh nhất + kiểm `FirmwareModelId` (`C360 = 1075`); ô `FBX thêm tối thiểu (dB)` (`MinimumAddedDb`); IO009 mặc định tùy chọn (`TestConfig.Optional`, `PENDING` khi bỏ qua, không chặn gửi và không đổi kết luận tổng), popup `"đặt mic đo gần loa hơn"` khi FBX tắt chưa hú, phân loại độ mạnh tiếng hú (`nhẹ/vừa/mạnh`) + tự ghi hú mạnh vào note; script `scripts/new-config-agent-token.ps1` + `scripts/publish-config-agent.ps1` cho endpoint `/api/app/audio-config/agent-publish`.
- **1.5.6 (`0cc69e2`, `5da9e78`)**: Bấm Auto Test là app tự nối loa gần nhất, chuyển và báo đủ ngõ, tự đồng bộ ngõ sound card với cấu hình server (không còn popup bấm A/B), đo hết các bài rồi báo kết quả gửi server.
- **Web (`c8e1b07`..`95cf33d`)**: nhận tới 16 bài/ảnh QA mỗi phiên, giữ `details` 16000 ký tự, trường `BleNamePrefix` trên `/config`, bước tùy chọn ở phiên thử, endpoint `POST /api/app/audio-config/agent-publish` (đã bật 2026-11-19: `CONFIG_AGENT_TOKEN_SHA256` + `CONFIG_AGENT_STAFF_ID=STF-ADMIN` trong `shared/.env`; token chỉ ở máy build `%USERPROFILE%\.sonca-update\config-agent\token.txt`). Chủ cho phép agent tự gửi + duyệt cấu hình: `scripts/publish-config-agent.ps1 -File <model.json> -Note "…"` (có `-WhatIf`). Chỉ duyệt cấu hình dùng trường mới **sau khi** app đọc được trường đó đã live.
- Kiểm offline riêng: `scripts/run-offline-checks.ps1 -Only c360-v152` (fixture `tools/AudioLifecycleCheck/Fixtures/C360-rev3.json`, `-rev4.json`, `-rev5.json`).

## 3. Việc còn mở (cần người dùng quyết / đo thật)
1. **Chưa đo phần cứng** mức MIC IN mới, BLE MASTER 50%, IO008, IO009. Đo chỉ khi người dùng nói **"bắt đầu đo"**.
2. Ngưỡng IO009 6 dB và thang hú nhẹ/vừa/mạnh (nổi trên nền < 35 / < 45 / ≥ 45 dB) là giá trị khởi điểm, chưa đo kiểm.
3. **FBX (IO002) & IO009**: chốt lại tiêu chí `ExpectedDetected` / ngưỡng `MinimumAddedDb` sau khi đo vài loa thật.
4. File cấu hình dự phòng có chữ ký (`config/latest.json`, serial 1794906189) vẫn có C360 7 bài cũ; trạm online luôn dùng bản ACTIVE nên không ảnh hưởng. Muốn cập nhật: skill `sonca-config-deployment`.
5. Check `ui-impedance` FAIL trên bàn đo của máy build (có từ trước, không chặn phát hành).

## 4. Làm việc từ máy khác
- **Sửa code rồi muốn trạm chạy thử trước:** dùng skill `.agents/skills/sonca-temp-release` — `scripts/submit-temp-update.ps1` gửi bản build lên server bằng token `SONCA_UPDATE_SUBMIT_TOKEN` (xin chủ; không commit, không in). ADMIN duyệt ở `/temp-updates`; chỉ máy kênh Beta nhận. Không cần khóa ký hay SSH.
- **Vá nhanh kênh ổn định** (bản sửa nhỏ đã commit, số 4 phần 1.5.5.N): skill `sonca-quick-patch` — `scripts/quick-patch.ps1 -Version 1.5.5.N -Deploy` trên máy build chính.
- **Phát hành bản ổn định / deploy feed / sửa cấu hình server** chỉ làm trên máy build chính (khóa ký feed ở `%USERPROFILE%\.sonca-update\`, SSH VPS). Skill: `sonca-version-update`, `sonca-beta-release`, `sonca-config-deployment`.
- Cấu hình bài đo chính thức sửa ở web `/config` (ADMIN duyệt). App trạm tự tải bản ACTIVE khi đăng nhập/chọn model — không cần phát hành app để đổi cấu hình, trừ khi cấu hình dùng ngõ/trường mới.

## 5. Luật cứng
- Chỉ push remote **`private`** (`github.com/zenith1379/SoncaAudioInspector-Private`, phải là PRIVATE). Không push `origin`/`upstream`/`minhtruong89` (public), không force-push.
- Không commit khóa, token, `.env`, artifact, `.escouade/`. Bản quyền: NGUYỄN VĂN LỘC (`LICENSE.txt`).
- Không phát/thu âm thanh, không đụng BLE/loa thật khi chưa được phép. Không build đè `bin\Debug` (app đang chạy dưới Visual Studio).
- Agent khác (Codex/agy) không tự commit; người điều phối kiểm offline rồi mới commit.
- Báo cáo bằng tiếng Việt, phân biệt rõ: build / offline / trạm giả lập / live / phần cứng thật.

## 6. Cập nhật file này
Khi xong một việc lớn: sửa mục 1–3 cho đúng hiện trạng (ngày tuyệt đối, SHA), commit riêng `docs: update HANDOFF.md`, push `private`.
