<!-- CODEGRAPH_START -->
## CodeGraph

If `.codegraph/` exists, use `codegraph_explore` (or `codegraph explore`) before searching or reading code. If absent, skip CodeGraph; do not index automatically.
<!-- CODEGRAPH_END -->

## Notes (all AI agents)

Shared notes for every agent: [note/claude/notes.md](note/claude/notes.md). Read the relevant section before bench, Rè/Rub & Buzz, config server, release, VPS or agent-handoff work, and save every lasting note there (short, dated); artifacts are deleted after seven days. To summarize Claude chat sessions into it, use [sonca-session-notes](.agents/skills/sonca-session-notes/SKILL.md).

## Artifacts (all AI agents)

Read [ARTIFACTS.md](ARTIFACTS.md) before building, publishing, or generating reports.
Create each output under `artifacts/YYYYMMDD-HHmmss-fffZ-description/`, with a basic description, creation time and source SHA in `ARTIFACT.md` and `artifact.json`.
Use `scripts/new-artifact.ps1`; put build output, intermediate files, logs and packages inside that directory. Never overwrite a previous version or create new root-level `bin-*`, `.codex-*`, or `.artifacts-*` directories.
Retain only the last seven days. Preview `scripts/cleanup-artifacts.ps1`, inspect its report, then run with `-Apply` when cleanup is authorized. Preserve source, configuration, credentials, calibration and reference measurements outside artifact roots.
Do not claim REW-equivalent accuracy from a successful build or synthetic tests. See [AUTO_TEST_PERFORMANCE.vi.md](AUTO_TEST_PERFORMANCE.vi.md).


## Version updates

For Sonca version bumps, signed release feeds and server update requests, use [sonca-version-update](.agents/skills/sonca-version-update/SKILL.md). It selects the supported cache-reuse workflow and distinguishes pipeline improvements that still need implementation.

For a **quick stable patch** of an already-committed small fix (4-part version, e.g. 1.5.5 → 1.5.5.1), use [sonca-quick-patch](.agents/skills/sonca-quick-patch/SKILL.md): one script (`scripts/quick-patch.ps1`) checks, builds, signs, deploys with SSH pacing and runs UpdateReleaseCheck.

For **beta** releases, use [sonca-beta-release](.agents/skills/sonca-beta-release/SKILL.md). Every beta writes only `beta.json` and leaves `latest.json` byte-identical, so stable stations are never prompted to update. Stations that opt into the beta channel can downgrade from the app (Phiên bản → Cài phiên bản khác / Hạ cấp).

## Web server deploy (inventory.acnos.store)

The website and API at inventory.acnos.store are a separate repo: `D:\PROJECT\Speaker Inventory System`. Deploy it with that repo's skill [speaker-inventory-deploy](../../Speaker%20Inventory%20System/.agents/skills/speaker-inventory-deploy/SKILL.md) (`D:\PROJECT\Speaker Inventory System\.agents\skills\speaker-inventory-deploy\SKILL.md`), also when the deploy is handed to agy or Codex. The Sonca skills above publish only the desktop update feed.
