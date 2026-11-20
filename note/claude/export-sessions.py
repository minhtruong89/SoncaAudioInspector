"""Xuất mỗi phiên chat Claude Code (JSONL) thành file Markdown dạng Hỏi / Đáp, bảng căn cột cho Notepad++.

Dùng:
  py -I note/claude/export-sessions.py "%USERPROFILE%\\.claude\\projects\\D--PROJECT-Git-Trending-SoncaAudioInspector" note/claude/sessions
  py -I note/claude/export-sessions.py --tables note/claude/notes.md     (chỉ căn lại bảng của một file .md)

Chỉ giữ câu hỏi của người dùng và phần trả lời bằng chữ; bỏ lệnh công cụ, kết quả kỹ thuật, suy nghĩ,
nhắc hệ thống, hook, thông báo tác vụ nền và lệnh /slash. Che email, mật khẩu, token nếu có.
"""
import glob, json, os, re, sys, unicodedata
from datetime import datetime, timedelta, timezone

VN = timezone(timedelta(hours=7))

# ---------- bảng căn cột ----------
def width(text):
    """Số cột hiển thị trong font đơn cách: ký tự rộng (CJK, emoji) = 2, dấu kết hợp = 0."""
    w = 0
    for ch in text:
        if unicodedata.combining(ch) or ch in "​‍️":
            continue
        w += 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
    return w

def split_row(line):
    body = line.strip()
    if body.startswith("|"): body = body[1:]
    if body.endswith("|") and not body.endswith("\\|"): body = body[:-1]
    cells, cur, i = [], "", 0
    while i < len(body):
        if body[i] == "\\" and i + 1 < len(body) and body[i + 1] == "|":
            cur += "\\|"; i += 2; continue
        if body[i] == "|":
            cells.append(cur.strip()); cur = ""
        else:
            cur += body[i]
        i += 1
    cells.append(cur.strip())
    return cells

SEP = re.compile(r"^:?-{1,}:?$")

def format_table(lines):
    indent = re.match(r"^\s*", lines[0]).group(0)
    rows = [split_row(l) for l in lines]
    n = max(len(r) for r in rows)
    rows = [r + [""] * (n - len(r)) for r in rows]
    is_sep = [all(SEP.match(c.replace(" ", "")) for c in r if c) and any(r) for r in rows]
    widths = [max([width(r[c]) for k, r in enumerate(rows) if not is_sep[k]] + [3]) for c in range(n)]
    out = []
    for k, r in enumerate(rows):
        if is_sep[k]:
            cells = []
            for c in range(n):
                s = r[c].replace(" ", "")
                left, right = s.startswith(":"), s.endswith(":") and len(s) > 1
                dash = "-" * (widths[c] - left - right)
                cells.append((":" if left else "") + dash + (":" if right else ""))
        else:
            cells = [r[c] + " " * (widths[c] - width(r[c])) for c in range(n)]
        out.append(indent + "| " + " | ".join(cells) + " |")
    return out

def align_tables(text):
    lines, out, i = text.split("\n"), [], 0
    in_code = False
    while i < len(lines):
        line = lines[i]
        if line.strip().startswith("```"):
            in_code = not in_code; out.append(line); i += 1; continue
        if not in_code and line.strip().startswith("|") and line.strip().count("|") >= 2:
            block = []
            while i < len(lines) and lines[i].strip().startswith("|") and lines[i].strip().count("|") >= 2:
                block.append(lines[i]); i += 1
            out.extend(format_table(block) if len(block) >= 2 else block)
            continue
        out.append(line); i += 1
    return "\n".join(out)

# ---------- che thông tin ----------
REDACT = [
    (re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"), "[email]"),
    (re.compile(r"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}"), "Bearer [ĐÃ ẨN]"),
    (re.compile(r"(?i)\b(password|mật khẩu|passwd|pwd|token|api[_-]?key|secret)(\s*[:=]\s*)(\"[^\"]+\"|'[^']+'|\S+)"), r"\1\2[ĐÃ ẨN]"),
    (re.compile(r"\b(sk|ghp|gho|github_pat|xox[abp])[-_][A-Za-z0-9_-]{16,}"), "[ĐÃ ẨN]"),
]
def redact(t):
    for p, r in REDACT:
        t = p.sub(r, t)
    return t

# ---------- đọc phiên ----------
def vn(ts):
    try:
        return datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone(VN)
    except Exception:
        return None

def user_text(content):
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts = []
        for c in content:
            if not isinstance(c, dict):
                continue
            if c.get("type") == "tool_result":
                return None
            if c.get("type") == "text":
                parts.append(c.get("text", ""))
            elif c.get("type") == "image":
                parts.append("[ảnh]")
        return "\n".join(parts)
    return None

def clean_user(t):
    if "<task-notification>" in t or "<command-name>" in t or "<local-command-" in t:
        return ""
    t = re.sub(r"<system-reminder>.*?</system-reminder>", "", t, flags=re.S)
    t = re.sub(r"<pasted_content[^>]*>", "\n> (nội dung dán)\n", t)
    t = re.sub(r"</pasted_content[^>]*>", "\n", t)
    t = t.strip()
    if t.startswith("[Request interrupted"):
        return "_(dừng thao tác)_"
    if t.startswith("Caveat:"):
        return ""
    return t

def assistant_text(content):
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "\n\n".join(c.get("text", "") for c in content if isinstance(c, dict) and c.get("type") == "text")
    return ""

def export(root, out):
    os.makedirs(out, exist_ok=True)
    for path in sorted(glob.glob(os.path.join(root, "*.jsonl")), key=os.path.getctime):
        sid = os.path.splitext(os.path.basename(path))[0]
        turns, first, last, seen = [], None, None, set()
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                try:
                    e = json.loads(line)
                except Exception:
                    continue
                ts = e.get("timestamp")
                if ts:
                    first = first or ts
                    last = ts
                if e.get("isMeta") or e.get("isSidechain"):
                    continue
                msg = e.get("message") or {}
                role = msg.get("role")
                if e.get("type") == "user" and role == "user":
                    t = user_text(msg.get("content"))
                    t = clean_user(t) if t is not None else ""
                    if t:
                        turns.append(["Hỏi", ts, t])
                elif e.get("type") == "assistant" and role == "assistant":
                    key = (msg.get("id"), json.dumps(msg.get("content"), ensure_ascii=False)[:200])
                    if key in seen:
                        continue
                    seen.add(key)
                    t = assistant_text(msg.get("content")).strip()
                    if not t:
                        continue
                    if turns and turns[-1][0] == "Đáp":
                        turns[-1][2] += "\n\n" + t          # gộp các đoạn đáp liền nhau
                    else:
                        turns.append(["Đáp", ts, t])
        if not turns:
            continue
        start, end = vn(first), vn(last)
        name = f"{start:%Y-%m-%d}-{sid[:8]}.md"
        parts = [f"# Phiên {start:%d/%m/%Y} · {sid[:8]}\n",
                 f"- Id: `{sid}` · mở lại bằng `claude --resume {sid}`",
                 f"- Thời gian (giờ VN): {start:%d/%m %H:%M} → {end:%d/%m %H:%M}",
                 "- Chỉ gồm hỏi và đáp; đã bỏ lệnh công cụ, kết quả kỹ thuật, thông báo hệ thống. Email/mật khẩu/token (nếu có) đã che.\n",
                 "---\n"]
        for who, ts, t in turns:
            d = vn(ts) if ts else None
            parts.append(f"### {who} · {d:%d/%m %H:%M}\n" if d else f"### {who}\n")
            parts.append(redact(t).rstrip() + "\n")
        text = unicodedata.normalize("NFC", align_tables("\n".join(parts)))
        with open(os.path.join(out, name), "w", encoding="utf-8", newline="\r\n") as w:
            w.write(text)
        asks = sum(1 for x in turns if x[0] == "Hỏi")
        print(f"{start:%Y-%m-%d} {sid[:8]} {name} ({asks} cau hoi)")

def tables_only(path):
    with open(path, encoding="utf-8") as f:
        text = f.read().replace("\r\n", "\n")
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(unicodedata.normalize("NFC", align_tables(text)))
    print("aligned tables in " + path.encode("ascii", "replace").decode())

if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--tables":
        tables_only(sys.argv[2])
    elif len(sys.argv) == 3:
        export(sys.argv[1], sys.argv[2])
    else:
        print(__doc__.encode("ascii", "replace").decode())
