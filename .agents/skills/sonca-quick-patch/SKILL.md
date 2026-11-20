---
name: sonca-quick-patch
description: Fastest path to ship an already-committed small fix of SoncaAudioInspector to the STABLE channel as a 4-part patch (1.5.5 -> 1.5.5.1 -> 1.5.5.2) or the next third number (1.5.5.x -> 1.5.6) on the main build machine. One script does live check, cached previous feed, csproj bump, build, sign, verify, paced deploy and UpdateReleaseCheck; then commit/tag/push private. Use for "vá nhanh", "release nhanh", "1.5.5.1", "deploy bản vá". Not for beta (sonca-beta-release), other machines (sonca-temp-release), or a new x.y.z (sonca-version-update).
---

# Quick patch — agent runbook

Audience: agy / Codex / Claude on the MAIN BUILD MACHINE. Execute top to bottom. Do not improvise steps. Report in Vietnamese.
Background (read only if a step fails): [../sonca-version-update/SKILL.md](../sonca-version-update/SKILL.md), [../sonca-version-update/references/operations.md](../sonca-version-update/references/operations.md).

## 0. Preconditions — check, never fix silently
| Check | How | If false |
|---|---|---|
| Owner asked to release/deploy | the request | STOP, ask |
| Fix is committed by the coordinator | `git status --porcelain -- src tools scripts Assets` is empty | STOP: "chưa commit bản sửa" (agents do not commit code) |
| Offline checks were run on HEAD | coordinator's report, or run `scripts/run-offline-checks.ps1 -Only <touched groups>,update-logic,ui-version,ui-version-header` | any FAIL other than `ui-impedance` -> STOP |
| Signing key exists | `Test-Path $env:USERPROFILE\.sonca-update\update-signing-key.pem` | STOP (wrong machine -> sonca-temp-release) |
| Release notes | `docs/release-notes/<ver>.txt`, UTF-8 **without BOM**, line 1 = one-line summary in Vietnamese starting `v<ver>:` | write it (only this file) |

Version rule: `<ver>` = live stable `x.y.z` + 4th number +1 (`1.5.5` -> `1.5.5.1`; `1.5.5.1` -> `1.5.5.2`), or the next third number without a 4th (`1.5.5.1` -> `1.5.6`). Script refuses anything else (new minor/major: sonca-version-update).

## 1. Dry check (read-only, ~5 s)
```powershell
$root = git rev-parse --show-toplevel; $ver = '1.5.5.1'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root/scripts/quick-patch.ps1" -Version $ver -CheckOnly
```
Expect: `Live stable …`, `Feed trước (cache khớp live): …`, `Mã nguồn HEAD …`. Error "Không có feed cache khớp" -> STOP, switch to sonca-version-update (cache older than 7 days).

## 2. Build + sign + deploy + live check (one call, ~6–10 min)
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$root/scripts/quick-patch.ps1" -Version $ver -Deploy
```
- Pacing `-PauseSeconds 8` (default) keeps SSH under the VPS UFW limit. Never lower it; never add SSH calls of your own.
- Any thrown error = STOP. Run nothing twice before you know the cause. If it threw AFTER `deploy-update` started, report "production state unknown" with the last 30 lines and do not retry.
- Success ends with `XONG <ver> live` and lines `PASS live signature…`, `PASS actual updater…`, `PASS live signed catalog…`.
- Summary JSON: `<feed artifact>/quick-patch.json` (paths, delta, checks).

## 3. Git (only after step 2 succeeded)
```powershell
git -C $root remote get-url private            # must be https://github.com/zenith1379/SoncaAudioInspector-Private.git
gh repo view zenith1379/SoncaAudioInspector-Private --json visibility -q .visibility   # must print PRIVATE
git -C $root add SoncaAudioInspector.csproj "docs/release-notes/$ver.txt"
git -C $root commit -m "release: v$ver"
git -C $root tag -a "v$ver" -m "v$ver"
git -C $root push private HEAD:main; git -C $root push private "v$ver"
git -C $root ls-remote private refs/heads/main "refs/tags/v$ver"
```
Either check false -> STOP before pushing. Never push `origin`/`upstream`/`minhtruong89`. Never force-push. Commit only those two files.

## 4. Notes (2 lines, then commit `docs: update HANDOFF.md`, push private)
- `HANDOFF.md` §1 row "App live": `**v<ver>** — commit phát hành <sha>, tag v<ver>, code <source sha>`.
- `note/claude/notes.md` §4: one dated line `- <date>: v<ver> live stable (quick patch): <one-line change>, delta <n> file / <bytes> byte, UpdateReleaseCheck PASS.`

## 5. Report (Vietnamese, ≤ 10 lines)
Version · source SHA · release commit + tag · delta files/bytes · deploy exit · the three UpdateReleaseCheck PASS lines · what was NOT verified (real station, hardware, signed-in web pages).

## Invariants
- One build per release; the script reuses it for feed, check and deploy. Never reuse an older DLL.
- Released blobs/archives are immutable; rollback = a higher patch number, never edit the feed by hand.
- Station data stays out of the package (script checks `save standards`, `checking_config_realRoute.json`, `verify.txt`, `.env`).
- Do not change code, logic, thresholds or config in a quick patch. Do not touch the web repo or `/config`. Do not play/record audio or use BLE.
- Do not print key or token contents. Do not run `cleanup-artifacts.ps1 -Apply`.
