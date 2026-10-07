<!-- CODEGRAPH_START -->
## CodeGraph

If `.codegraph/` exists, use `codegraph_explore` (or `codegraph explore`) before searching or reading code. If absent, skip CodeGraph; do not index automatically.
<!-- CODEGRAPH_END -->

## Artifacts (all AI agents)

Read [ARTIFACTS.md](ARTIFACTS.md) before building, publishing, or generating reports.
Create each output under `artifacts/YYYYMMDD-HHmmss-fffZ-description/`, with a basic description, creation time and source SHA in `ARTIFACT.md` and `artifact.json`.
Use `scripts/new-artifact.ps1`; put build output, intermediate files, logs and packages inside that directory. Never overwrite a previous version or create new root-level `bin-*`, `.codex-*`, or `.artifacts-*` directories.
Retain only the last seven days. Preview `scripts/cleanup-artifacts.ps1`, inspect its report, then run with `-Apply` when cleanup is authorized. Preserve source, configuration, credentials, calibration and reference measurements outside artifact roots.
Do not claim REW-equivalent accuracy from a successful build or synthetic tests. See [AUTO_TEST_PERFORMANCE.vi.md](AUTO_TEST_PERFORMANCE.vi.md).

## Antigravity Execution & Planning Rules

- **Full Control / Autonomous Mode**: Execute file edits, terminal commands, builds, and checks directly without asking for confirmation on routine or approved tasks.
- **Plan First When Needed**: Đối với các tác vụ phức tạp, thay đổi kiến trúc, tính năng mới hoặc quy trình cần lập kế hoạch (plan), BẮT BUỘC phải trình bày kế hoạch (plan) chi tiết để người dùng xem và duyệt trước khi thực thi.
- Chủ động kiểm tra, chẩn đoán và hoàn thiện các bước sau khi kế hoạch được thông qua.

