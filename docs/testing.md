# Testing

## Commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1    # dotnet build WorkFlowSync.sln
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1     # dotnet test WorkFlowSync.sln
dotnet format WorkFlowSync.sln --verify-no-changes                        # lint (formatting)
dotnet build WorkFlowSync.sln -warnaserror                                # typecheck (warnings as errors)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1  # portable exe smoke: publish\portable\WorkFlowSync.exe version
```

Requires .NET SDK 8.0.x on PATH (`C:\Program Files\dotnet`). No other services.

## Test Layers

| Layer | Where | What |
|-------|-------|------|
| Unit | `tests/WorkFlowSync.Tests` | Config parsing/validation, GUI view-model ↔ config mapping (exist); planner decision table (F1), retention (F3), exclude matching (F6), ffs_batch parser (F9) — all pure, no I/O |
| E2E on temp folders | `tests/WorkFlowSync.Tests` (`[Trait("Category","E2E")]`) | Create source/target trees under `%TEMP%`, run scanner+planner+executor, assert files and state.db. Covers link cycles (junctions can be created by a normal user via `mklink /J`), hidden/system files, long paths, Ukrainian names |
| Manual against real shares | never from agent sessions unless asked | `sync --once --dry-run` on the real config, then review the log |

## Required Before Completion

- Build succeeds with 0 warnings introduced.
- `scripts\test.ps1` passes.
- For scanner/executor changes: run the E2E category.
- For any change to planner rules: add/adjust a decision-table test case for the changed row.

If a command cannot run, record the exact reason and the next step needed.

## Current Status (2026-09-14, Stage 4 + per-pair controls)

- `dotnet build -warnaserror`: OK.
- UI reviewed on screenshots in both themes (folders / activity / settings); `--page <0|1|2>` opens a page directly.
- `dotnet test`: 73 passed — adds `AutoCheckPersistenceTests` (4: checker follows the pair states, toggle persists and survives a restart, other unsaved edits defer it, legacy default), `PerPairControlTests` (5: paused pair skipped, explicit single-pair run overrides pause,
  unknown pair reported, `enabled` round-trip and legacy default, run-one-pair from the view-model), `TrayModeTests` (3: background loop start/pause/resume against real folders,
  Startup shortcut mode round-trip, app.ico structure). The tray icon itself needs a running Avalonia app, so it is
  verified by launching `WorkFlowSync.exe --tray` (process alive, hidden window, pass written to the log) and by eye.
  Stage 3 added `ResidentModeTests` (6: pass lock, loop with config reload, loop skip on held lock,
  log pruning, Startup shortcut in a temp folder via COM, schtasks argument shape). Real Task Scheduler is never touched by tests.
  Stage 2 added `RetentionTests` (7), `FfsImportTests` (4), e2e retention with the real Recycle Bin;
  previously `SyncPlannerTests` (17, one per decision-table row), `ExcludeMatcherTests` (14),
  `EndToEndTests` (6, temp folders incl. junction cycle via `mklink /J`, hidden/system, unavailable source, dry-run),
  `RunViewModelTests` (GUI run tab), `SyncConfigTests`, `PairViewModelTests`.
- Perf dry-run over `C:\Program Files` (206k files): scan 0.8–1.8 s, 0 warnings.
- `wfs import-excludes --dry-run` on the user's real `batch.ffs_batch`: 22,548 items → 16 patterns + 22,551 tombstones in 0.3 s.
- `wfs sync --loop --dry-run` smoke: loop start → pass (source unavailable → skipped) → "next pass in 30 min"; process stays alive; killed after 6 s.
- `wfs autostart status` / `wfs task status` read-only queries verified (both disabled on the dev machine).
- `WorkFlowSync.exe --tray` smoke: starts with no window, runs a pass (source unavailable → skipped), waits; no crash log.
- NB: `dotnet test` does not rebuild `WorkFlowSync.Cli` (not in the test dependency graph) — run `scripts\build.ps1`
  before using `wfs.exe` after Core changes.
- GUI launched with `config.example.json`: window opens on the «Папки» tab, pair list renders, pair dialog
  loads all fields (screenshot-verified once; do not automate mouse/screenshots on the user's desktop —
  see WorkFlowSync_SKILL.md).
- `publish.ps1` publishes both `WorkFlowSync.exe` (GUI) and `wfs.exe` (console).
