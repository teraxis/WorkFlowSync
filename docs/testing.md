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
| Root classification | `tests/WorkFlowSync.Tests/RootProbeTests.cs` | Pure parts only: path shape (UNC / mapped letter / extended forms), private-address ranges, the `Classify` decision table. `DriveInfo`, DNS and real shares are deliberately out of scope here |
| Manual against real shares | never from agent sessions unless asked | `sync --once --dry-run` on the real config, then review the log; `wfs probe <path> [--watch <sec>]` for storage type, latency and notification support (read-only, safe to run any time) |

## Required Before Completion

- Build succeeds with 0 warnings introduced.
- `scripts\test.ps1` passes.
- For scanner/executor changes: run the E2E category.
- For any change to planner rules: add/adjust a decision-table test case for the changed row.

If a command cannot run, record the exact reason and the next step needed.

## Current Status (2026-09-16, Stage 5 complete)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **313 passed, 0 failed** (12 s).
- `NotificationSettingsTests` (3): the settings survive a round trip through config.json, the hint under
  the card follows what was chosen, and a config written before the feature reads as "talking, no quiet
  hours". GUI smoke on the settings page (`--page 2`): alive after 8 s, no crash log.
- Still unseen by eye, on purpose (no screenshots of the owner's desktop): the recent-changes window,
  the notification, and the new settings card. Ask them to look.

## Current Status (2026-09-16, Stage 5.6b — notifications)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **310 passed, 0 failed** (12 s).
- `NotificationPolicyTests` (24) pin the rules that are easy to get wrong by feel: one message per pass,
  silence when nothing changed, problems outranking good news, quiet hours that never hide a problem, and
  Ukrainian plurals including the 11–14 case. The policy is pure, so none of this needs a running app.
- GUI smoke with notifications configured: alive after 7 s, no crash log. **The notification and the
  recent-changes window were not seen** — no screenshots of the owner's desktop; ask them to look.
- A real Windows balloon was deliberately not used; the reason and what it costs are in
  `docs/product/features/tray.md` so it does not read as an oversight later.

## Current Status (2026-09-16, Stage 5.6a — the recent-changes window)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **286 passed, 0 failed** (12 s).
- `RecentChangesTests` (7) and `RecentChangesViewModelTests` (12) cover the query and what the window
  shows, including the human-readable times and the case of a clock moved backwards (no "-3 хв тому").
- GUI smoke: launched with a paused temp pair, alive after 7 s, no `crash-*.log`. **The window itself was
  not seen** — no screenshots of the owner's desktop. Ask them to click the tray icon.
- The planned `events` table turned out to be unnecessary: `first_seen` is written once and never changes,
  so "recently added" is a query over `entries`. One migration and one copy of the truth avoided.

## Current Status (2026-09-16, Stage 5.5b — instant sync is live)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **267 passed, 0 failed** (12 s), run twice
  back to back to check for flakiness.
- The «Файли з хмари» row was gated on "two-way only", which the owner noticed as a greyed-out control.
  Too crude: placeholders depend on whether the side being READ is local, so a mirror FROM a OneDrive
  folder needs the setting and was wrongly denied it. Now gated on `CloudFilesApplies`, with the hint
  left enabled so it can explain why — a control the user cannot use is when they most need to read why.
- `InstantSyncTests` (5, E2E) drive the real resident loop against real folders with the interval set to
  an hour, so anything that arrives can only have come from the watcher. They caught two product defects
  that review had missed — a watcher that asked for a full pass on startup (which then did the work the
  watcher existed for), and a folder event collapsing to "the whole pair" (so every save became a full
  pass and partial passes never ran). Both are written up in `docs/plan-etap5.md` §5.5b.
- They also caught a defect in themselves: the file appears on disk DURING the pass, while the counter
  that proves a partial pass ran is raised after it returns — so the assertion has to wait, not assert
  outright. That is why the tests passed alone and failed in the full suite.

## Current Status (2026-09-16, Stage 5.5a — the watching machinery)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **259 passed, 0 failed** (12 s).
- `ChangeQueueTests` (14) inject the clock, so debounce, the patience ceiling and the folder ceiling are
  asserted to the millisecond instead of being slept through. `SelfWriteLogTests` (8) and
  `FolderWatcherTests` (5, E2E against a real folder: events arrive, our own writes are filtered, an
  unwatchable root is reported rather than thrown).
- **Caught by a test, not by review**: `SelfWriteLog` first consumed its note on the first check. One
  `File.WriteAllText` raises several notifications (six were measured for one creation on SMB), so every
  event after the first slipped through and would have started a pass — the exact ping-pong the class
  exists to prevent. The note now lives for the whole window; the window was shortened to 5 s to bound
  the blind spot that creates, and that blind spot has its own test.
- **Not yet wired**: the watcher is not connected to `LoopRunner`, so product behaviour is unchanged —
  pairs are still checked on the interval. See `docs/plan-etap5.md` §5.5b for what remains.

## Current Status (2026-09-15, Stage 5.4 — deletion safeguards)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **227 passed, 0 failed** (12 s).
- `DeletionGuardTests` (8): thresholds are pure and I/O-free, so both sides of the boundary are pinned —
  100 removals always pass, 201 out of 1000 do not, 1000 out of a million do.
- `DeletionSafeguardsEndToEndTests` (8, E2E): 200 files vanishing from one side at once is held back and
  the mirror keeps them; once only five are really gone the removal goes through; a removal the other
  side contradicts is skipped; a genuine deletion still travels. Recycle-Bin availability and placeholder
  detection are asserted on the decision (a UNC path and the attribute flags) — real network shares and
  real OneDrive placeholders cannot be created in a test.

## Current Status (2026-09-15, Stage 5.3 — partial pass)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **208 passed, 0 failed** (12 s).
- `ScopedPlannerTests` (27): the F1 decision table re-run with a `PlanScope`. Every destructive row has
  a twin asserting the same input produces NOTHING, and several compare scoped against full side by
  side so the difference is provably the scope and not the data.
- `ScopedPassEndToEndTests` (10, E2E): real folders under `%TEMP%` with Cyrillic names — a file deleted
  outside the scope stays `Active` and is only tombstoned by the following full pass; retention does not
  fire; `first_seen` is not backdated; a scope missing from the source changes nothing; `--scope` needs
  exactly one pair.
- Verified by hand as well (temp folders, Cyrillic): scoped pass copied only the new file with
  `deferred=0`, state `active=5 tombstone=0`; the next full pass wrote `tombstone \Тека Б\другий.txt`.
  `--loop --scope` is rejected with exit code 2.
- **A defect that only a real run exposed**: a scan reports children, never the directory it starts from,
  so a scoped pass read its own watched folder as deleted (`deferred=1` where nothing had gone). Fixed in
  `TreeScanner`; guarded by `The_watched_folder_itself_is_not_mistaken_for_a_deletion`. Worth remembering
  that the planner unit tests could not have caught it — they are handed the scan, not the scanner.

## Current Status (2026-09-15, Stage 5.2 + single instance + contrast)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **171 passed, 0 failed** (12 s).
- `ButtonLabelStyleTests` (5) parse `Controls.axaml` and require a `Button.<class> TextBlock` rule for
  every button class with its own `Foreground`, plus the right declaration order. Written after a bug
  that no colour test could catch: the palette was correct, but the primary button's label never
  received the colour — a button's Foreground is only inherited, and the global `TextBlock` style
  overrode it. The icon was white, the text near-black. **Colour tests check the palette; this one
  checks that the colour actually reaches the text.**
- `SingleInstanceTests` (12): second claim refused, lock freed on exit, two different configs run
  side by side, the name differs from `PassLock`'s, the running copy is actually woken, a listener
  that throws does not kill it, and `ConfigFile.FromArgs` agrees with the entry point.
  Verified for real as well: launching the exe twice leaves **one** process, the second exits with
  code 0, no crash log; after the first exits the lock is free again; a `--tray` launch while a
  window is open also exits instead of adding a second tray icon.
- `PaletteContrastTests` (11) read `Palette.axaml` from disk and compute WCAG 2.1 contrast, so a
  colour edit can no longer quietly break readability. They caught two real defects:
  white on the dark theme's accent was 3.59:1, and `TextMuted` — which carries the folder paths and
  the facts line, i.e. most of what the folders page says — was **3.48:1 on the canvas**. Retuned:
  light accent `#1A47CC` + white = 7.45:1, dark `#4C82F7` + `#0B1220` = 5.21:1, `TextMuted`
  `#7A8496` → `#636C7B` = 4.7–5.3:1 on every surface.
  NB: the first attempt at `TextMuted` (`#666F7E`) failed on `SurfaceSunken` by 0.02 — check every
  surface, not just the card.
- `WatchModeTests` (9): legacy config reads as `auto`, round trip through file and view-model, the
  five `QuickKind` cases that cannot be decided from a path alone, and what the pair card says.
- GUI smoke: launched with a temp config whose pair is paused (so no real folders are touched),
  process alive after 6 s, no `crash-*.log`, then stopped. The new dialog row was NOT verified by
  eye — ask the owner to open «Папки → ⋯ → Редагувати…» and look; do not automate clicks or
  screenshots on their desktop (see WorkFlowSync_SKILL.md).

## Current Status (2026-09-22, F22 — disk quota, cloud offloading, rotation)

- `scripts\build.ps1`: OK; only NU1900 warnings because NuGet vulnerability metadata was unavailable.
- `scripts\test.ps1`: **443 passed, 0 failed** (50 s on the final full run).
- `DiskQuotaRotatorTests` cover permanent deletion of the oldest verified copy, copied-at/mtime/ctime
  ordering, availability on cloud and indeterminate roots, preservation of a locally modified file, and the
  end-to-end `twoWay` runner rule that immediately tombstones a rotated target copy, never pulls it
  from the first folder again, and never propagates the deletion back to that first folder.
- `CloudSpaceTests` cover independent quota/offloading switches, Windows capacity queries by folder,
  actual Windows non-cloud detection,
  Files On-Demand intent, provider back-pressure, non-cloud quota protection and pre-pass quota protection.
- `StateSchemaTests` cover v1/v2/v3 migration to schema v4 and `copied_at` round-trips.
- GUI smoke: process stayed alive for 6 s with a fresh temporary config and wrote no crash log; no
  screenshots or synthetic input were used.

## Current Status (2026-09-15, Stage 5.1 — state schema v2)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **129 passed, 0 failed** (12 s).
- `StateSchemaTests` (9) cover the v1→v2 migration from a hand-built legacy database, integer time
  round-trips, scoped reads, batched writes past the batch size, and executor checkpoints (including
  a checkpoint that throws — the rows must survive for the final write).
- Measured on a synthetic 1,000,000-row database: file **279 MB → 114.7 MB (−59%)**; scoped read of
  1000 rows **4.1 ms** versus 3.77 s for the full load; full `Load()` 4.85 s → 3.77 s (**1.3×, not the
  3× the plan asked for** — parsing was only ~20% of it, materialising a million entries is the rest,
  347 MB allocated). Recorded in `docs/plan-etap5.md` §5.1 with the reasoning for accepting it.
- Two defects were found by these measurements rather than by review: an `OR` over the primary key
  made the scoped read full-scan the table (394 ms → 4.1 ms once split into `UNION ALL`), and a
  migration without a closing `VACUUM` produced a database larger than the one it replaced.
- `ForgetTests` caught a real regression when `DeleteSubtree` was moved onto the scoped read: a
  hand-typed Cyrillic path does not match byte-wise. Keep `forget` on the full load.

## Current Status (2026-09-15, Stage 5.0 — measurement base)

- `scripts\build.ps1`: OK, 0 warnings. `scripts\test.ps1`: **120 passed, 0 failed** (12 s).
- New `RootProbeTests` cover the pure parts of root classification (path shape, private-address
  ranges, the decision table) plus two real-folder cases under `%TEMP%` (probe of a live folder,
  watcher seeing a file created while it listens).
- **LAN and internet shares cannot be reproduced in CI.** For those, `wfs probe <path> [--watch <sec>]`
  is the verification tool, run by hand against a real root. Measured 2026-09-15 on the owner's
  machine: local `D:` → `Local`, first entry 5,2 ms; `S:` → `RemoteShare`, mapped drive resolved to
  its UNC, host resolved to a public address, first entry 170–228 ms, root listing 80 ms for 25
  entries. The probe never writes into the root, by design.
- **SMB change notifications are confirmed to work on the Hetzner Storage Box** (2026-09-15): with
  `--watch 180` against the real source, the owner created one file and the probe reported
  `events=6 errors=0` — one `created` plus five `changed` for that single file. Two consequences:
  the `SmbWatcher` branch is viable over the internet, and per-path stabilisation is mandatory
  (one saved file must not become six partial passes). The event latency was NOT measured — the
  exact moment of creation is unknown; measure it in stage 5.5 with a test that creates the file itself.

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
