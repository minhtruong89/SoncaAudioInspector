---
name: sonca-session-notes
description: Tổng hợp và cập nhật các phiên chat Claude Code của repo vào note/claude (xuất Hỏi/Đáp, thêm dòng vào bảng phiên, rút ghi chú lâu dài vào notes.md). Dùng khi người dùng bảo "agy tổng hợp/cập nhật session", cuối ngày, hoặc sau khi Claude làm xong một việc lớn.
---

# Tổng hợp phiên chat vào note/claude

Sổ ghi chú chung: [notes.md](../../../note/claude/notes.md). Hội thoại từng phiên: `note/claude/sessions/<ngày>-<id8>.md`.
Công cụ xuất: `note/claude/export-sessions.py` (Python 3, không cần cài thêm gói).

## Luật

- Chỉ sửa trong `note/claude/`. Không sửa code, không commit/push, không deploy, không xóa bản ghi gốc `%USERPROFILE%\.claude\projects\...\*.jsonl`.
- Không sửa tay file trong `note/claude/sessions/`: chúng được sinh lại mỗi lần chạy công cụ.
- Viết tiếng Việt, ngắn, ngày tuyệt đối (2026-11-17), đường dẫn bằng chứng dạng `code`. Không ghi mật khẩu, token, khóa, email.
- Ghi chú là sự thật đã xảy ra trong phiên (người dùng nói gì, đo ra gì, quyết định gì). Không tự suy diễn kết luận mới.

## Các bước

1. **Xuất lại hội thoại** (chạy lại an toàn, ghi đè file cùng tên):
   ```powershell
   py -I note/claude/export-sessions.py "$env:USERPROFILE\.claude\projects\D--PROJECT-Git-Trending-SoncaAudioInspector" note/claude/sessions
   ```
   Lệnh in mỗi phiên một dòng: ngày, id8, tên file, số câu hỏi.

2. **Tìm phiên mới hoặc đã dài thêm**: so danh sách vừa in với bảng ở mục "8. Các phiên chat" trong `notes.md`.
   Phiên chưa có dòng → thêm. Phiên đã có nhưng số câu hỏi tăng → cập nhật dòng đó.

3. **Đọc file Hỏi/Đáp của từng phiên đó** và viết một dòng bảng:
   `| Ngày | [id8] (sessions/<file>.md, viết liền dấu ngoặc) | Hỏi chính | Kết quả |` — xem các dòng có sẵn trong bảng làm mẫu.
   - Hỏi chính: các yêu cầu của người dùng, gộp lại, khoảng 25 chữ.
   - Kết quả: đã làm/kết luận gì, còn treo gì, khoảng 25 chữ.
   - Phiên kéo dài nhiều ngày ghi `2026-11-14 → 16`.

4. **Rút ghi chú lâu dài** từ các phiên đó vào đúng mục 1–6 của `notes.md` (Bàn đo, Rè, Config, Phát hành, Server, Giao việc):
   - quyết định/luật người dùng đặt ("rule này sau sẽ…", "không …", chọn phương án nào);
   - bẫy đã gặp và cách tránh; số liệu đo thật kèm đường dẫn bằng chứng;
   - việc còn treo chờ người dùng quyết.
   Sửa dòng cũ nếu đã lỗi thời; không để hai dòng mâu thuẫn. Thêm một dòng vào mục "7. Lịch sử" cho mỗi ngày có việc lớn.

5. **Căn bảng cho Notepad++**:
   ```powershell
   py -I note/claude/export-sessions.py --tables note/claude/notes.md
   ```

6. **Kiểm tra trước khi xong**:
   - Mọi link `sessions/...` trong `notes.md` trỏ tới file có thật.
   - Không có mật khẩu/token/email: `Select-String -Path note\claude\*.md,note\claude\sessions\*.md -Pattern 'password|mật khẩu|Bearer |@[A-Za-z0-9.-]+\.[a-z]{2,}'` (chỉ chấp nhận chỗ đã che `[ĐÃ ẨN]`/`[email]` hoặc chữ trong ngữ cảnh không phải giá trị bí mật).
   - `scripts/check-skills.ps1` vẫn PASS.

7. **Báo lại** ngắn gọn: phiên nào được thêm/cập nhật, ghi chú nào được thêm/sửa trong mục nào.

## Phiên của Codex / agy

Bản ghi của Codex và agy không nằm trong thư mục trên. Kết luận của họ lấy từ báo cáo trong artifact (`REPORT*.md`, `ARTIFACT.md`) và ghi vào mục chủ đề tương ứng, trước khi artifact bị dọn sau 7 ngày.
