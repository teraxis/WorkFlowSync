# WorkFlowSync — Project Skill (operational guide for agents)

Read this before changing project behavior. Product rules live in `docs/product/features/`; this
file is the "how to work here" companion.

## What the product is

One-way mirror with memory: network share(s) → local destination folder(s), commonly inside OneDrive. Three customer rules
(source deletions ignored; source additions copied; local deletions/changes final and never re-pulled),
plus `first_seen` timestamps, an intake window (maxAge) and auto-clean. Replaces FreeFileSync + a 22,569-line exclude list.

## Layout

```
src/WorkFlowSync.Core/   Config/ (SyncConfig, FolderPair, LinkMode, ConfigFile)  Model/ (StateEntry, EntryStatus, EntryKind)
                         Scanning/ (TreeScanner, ExcludeMatcher, ScanEntry)  State/StateStore  Planning/ (SyncPlanner, SyncPlan)
                         Execution/ (SyncExecutor, RecycleBin)  Logging/SyncLog (+Prune)
                         SyncRunner (one pass)  LoopRunner (resident)  PassLock (cross-process)  Autostart (Startup .lnk, schtasks)
                         RootProbe (RootKind Local/Removable/LanShare/RemoteShare, WNetGetConnection, latency, notify probe)
src/WorkFlowSync.Cli/    wfs.exe: Program.cs (commands), ConsoleOwner.cs (pause on double-click), app.manifest (asInvoker + longPathAware + UTF-8)
src/WorkFlowSync.App/    WorkFlowSync.exe: Avalonia 11.3 GUI - Styles/ (Palette, Controls, Icons = design system, docs F12),
                         Views/ (MainWindow, PairDialog, ConfirmDialog), ViewModels/ (Main, Pair, Run, Autostart; no Avalonia types),
                         Services/BackgroundLoop.cs (tray loop), App.axaml.cs (TrayIcon+NativeMenu), Assets/app.ico, Converters.cs
tests/WorkFlowSync.Tests/
scripts/                 build.ps1, test.ps1, publish.ps1
config.example.json      reference config
```

Stage plan: `docs/requirements.md` §4. Stages 0–4 all done (scaffold, GUI, sync engine, age windows, resident mode + autostart, tray). FFS import was REMOVED on 2026-09-15 at the customer's request (docs F9 kept as history).
Stage 5 (instant sync via watchers + partial passes, DB foundation for millions of rows, notifications) is planned in `docs/plan-etap5.md`;
5.0 (measurement base: `wfs probe`, `cpu=`/`rss=`/`alloc=` in `pass end`) is done. Read that plan before touching change detection —
it records WHY the language and the database stay as they are, and the safety rules a partial pass must obey.

## Commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1     # -> publish\portable\WorkFlowSync.exe
src\WorkFlowSync.Cli\bin\Debug\net8.0\win-x64\wfs.exe config validate --config config.example.json
src\WorkFlowSync.App\bin\Debug\net8.0\win-x64\WorkFlowSync.exe --config config.example.json
```

## Non-negotiables (from docs/agent-playbook.md)

- Source is read-only. Local deletes go to Recycle Bin. `tombstone`/`local_modified` are never overwritten from source. `first_seen` never changes.
- Target scanner reads attributes only (OneDrive placeholders must not hydrate).
- `app.manifest` stays `asInvoker`; everything must work as a normal user.
- Planner has no file-system I/O; put rules there and unit-test them with the F1 decision table.
- Never run a real (non-dry-run) `sync` on the user's pairs from an agent session unless asked.
- Bilingual requirement: The application must support Ukrainian (uk) and English (en). Every new or modified user-facing string must be added to both locale configuration files (`src/WorkFlowSync.Core/Locales/uk.json` and `en.json`). No hardcoded UI strings.
- Avalonia pinned to 11.3.0 (12.x = different API). `dotnet new avalonia.app` emits 12.x - rewrite the csproj.
- GUI verification: launch the exe, check the process stays alive and no `logs\crash-*.log` appears, then Stop-Process. Do NOT drive the
  window with synthetic mouse clicks or `CopyFromScreen` captures while the user is at the machine: on 2026-09-14 a capture grabbed
  unrelated windows (mail, credentials) because the app was not in the foreground. Ask the user before any screenshot.
  `PrintWindow` does not capture Avalonia (known from PathShortener).

## Bilingual Support & Localization Standard (I18n)

WorkFlowSync is fully bilingual (Ukrainian and English) with dynamic runtime language switching:
- **Locale config files**: Standard JSON key-value files located in `src/WorkFlowSync.Core/Locales/{lang}.json` (`uk.json`, `en.json`).
- **Resource packaging**: Files are embedded resources (`<EmbeddedResource Include="Locales\*.json" />` in `WorkFlowSync.Core.csproj`). External override files placed under `locales/{lang}.json` next to the executable are loaded first if present.
- **Key naming standard**: Structured hierarchical keys: `section.feature_detail` (e.g. `nav.*`, `tasks.*`, `pair.*`, `approval.*`, `recent.*`, `tray.*`, `settings.*`, `enum.*`, `loop.*`, `common.*`).
- **Key parity**: Both `uk.json` and `en.json` must contain identical keys. Automated unit tests (`LocalizationTests`) verify 100% key parity.
- **XAML usage**: Use the `{loc:Loc KeyName}` markup extension (`xmlns:loc="using:WorkFlowSync.App.Localization"`). This binds reactively to `I18n.Instance[KeyName]`, enabling instant language switching across open windows without app restart.
- **Code-behind / ViewModel usage**: Call `I18n.T("key")` or `I18n.T("key", args...)`. Subscribe to `I18n.Instance.LanguageChanged` if dynamic view-model properties need refresh.
- **Human-friendly copy quality**: Strings must NOT sound like literal machine translations. Ukrainian copy must be natural, respectful, and crystal-clear to non-technical users. English copy must use established, idiomatic file-sync terminology (e.g., "One-way mirror", "Two-way sync", "Recycle Bin", "Approve", "Dismiss", "Revert").
- **Configuration**: User selection is stored in `config.json` via `Language` (`"uk"`, `"en"`, or `"system"`).

## Scanner rules of thumb (F5)

- `FileSystemEnumerable<T>` with `EnumerationOptions { BufferSize = config.ScanBufferSize, AttributesToSkip = 0, RecurseSubdirectories = false, IgnoreInaccessible = true }`.
- Own recursion with a work queue and `ScanParallelism` workers; decide per entry on `ReparsePoint` (F4), excludes (F6), tombstone dirs (F2).
- No per-file API calls; compare size + mtime (2 s tolerance).

## Gotchas

- .NET SDK 8.0.425 at `C:\Program Files\dotnet` is on PATH; `~\.dotnet` has 8.0.422 too — do not mix `DOTNET_ROOT`.
- `Watching/`: `ChangeQueue` (coalesce to folders, per-path quiet period, patience ceiling, folder ceiling, temp
  patterns) is deliberately I/O-free and clock-injected — test timing exactly, never with Thread.Sleep.
  `FolderWatcher` handlers must stay one-liners (the kernel buffer fills while they run); `Error` always means
  "ask for a full pass", never "retry quietly". `WatchSet` keeps watchers in step with the config (one per ROOT:
  mirror watches the source only, two-way watches both); `LoopRunner` polls it between scheduled passes.
- Two traps the integration tests caught and review did not: (1) `FolderWatcher.Start()` must NOT request a full
  pass — the loop starts watching right after a full pass, and asking made it run a second one that did the
  watcher's job; (2) `ChangeQueue.DirectoryOf` returns a top-level name AS IS, not "" — writing a file into a
  folder also raises an event for the folder, and collapsing that to the root turned every save into a full pass.
  The loop resolves the candidate with `Directory.Exists`.
- Integration tests on the loop must WAIT for `ScopedPassesRun`, not assert it: the file lands on disk during the
  pass, the counter rises after it returns. That difference is why they passed alone and failed in the full suite.
- `SelfWriteLog` must NOT consume its note on first use: one write raises several notifications (six measured on
  SMB for one creation), so consuming it lets the rest through and the ping-pong starts. The note lives for a
  short window instead; the blind spot that creates is bounded by the window and covered by the full pass.
- Deletions have three guards, none of which may be "simplified" (playbook invariant 2 exception, docs §4.5):
  the executor re-checks the exact path on the other side before removing; `DeletionGuard` strips removals from
  a plan that wants >100 AND >20% of the pair; every removal on a root without a Recycle Bin logs WARN with the
  full path. `RecycleBin.IsAvailableFor` treats anything not Local/Removable as having no bin — erring toward a
  needless warning is the cheap direction.
- `CloudFiles.Possible(root)` gates placeholder checks to local roots: asking a share costs a round trip per file
  for an answer that is always "no". `FolderPair.CloudFiles` (skip|hydrate) only matters in two-way mode.
- `PlanScope` = partial pass. Both planners must REFUSE every decision inferred from absence (tombstone, delete,
  auto-clean, Forget) and count it as `DeferredToFullPass`; the full pass is the only source of truth. Also: never
  backfill `first_seen` in a scoped pass — an empty scope looks exactly like an empty pair and would rewrite dates.
- A scan reports CHILDREN, never the directory it starts from. Harmless for a full pass (the pair root has no state
  row), but a scoped scan starts at a folder that DOES have one — without `TreeScanner` adding that entry back, every
  scoped pass reads as "the watched folder vanished". Found by running it, not by tests.
- Two named locks, do not confuse them: `PassLock` (`Local\WorkFlowSync.<hash>`) is held only while a pass runs;
  `SingleInstance` (`Local\WorkFlowSync.app.<hash>`) is held for the whole life of the GUI process. Same names would
  make an open window look like a pass in progress and the scheduler would skip every pass. A test asserts they differ.
- Single instance is checked in `App/Program.cs` BEFORE `StartWithClassicDesktopLifetime`, so a second launch paints
  nothing. Both the entry point and `App.axaml.cs` must resolve the config path the same way — hence `ConfigFile.FromArgs`.
  `wfs.exe` is deliberately NOT single-instance (Task Scheduler must be able to start `sync --once`).
- `RootProbe.QuickKind` (path + drive table only, no I/O) is what the GUI may call; full `Probe` does DNS and a listing
  and must never run on the UI thread. QuickKind returns null for any network share — LAN vs internet needs the probe.
- `SyncConfig.ToJson()` writes PROPERTY names in PascalCase (`"Watch"`); only enum VALUES are camel-cased (`"off"`).
  Reading is case-insensitive, so hand-edited configs may use either. A test pins this.
- `RootProbe`: a UNC path has no drive letter, so `DriveType` stays `Unknown` — the path SHAPE must decide, not the drive type.
  A mapped letter needs `WNetGetConnection` (mpr.dll) to become a UNC, otherwise `S:` is indistinguishable from a local disk.
  A known private address beats latency (a LAN share behind a slow VPN is still LAN); with no address at all, assume RemoteShare.
- `Process.TotalProcessorTime` is cached — call `proc.Refresh()` before reading it a second time, or the pass always reports cpu=0.
- The probe must never write into a root it is watching (source is read-only): a passive listen that sees nothing is reported as
  INCONCLUSIVE, not as "notifications do not work". Only the user can create the change that proves it.
- Cli and App csproj set `RuntimeIdentifier=win-x64`, so Debug output is under `bin\Debug\net8.0\win-x64\` (`wfs.exe`, `WorkFlowSync.exe`).
- Tests reference the App project (WinExe) to test view-models; fine for xUnit.
- `Path.GetFileName(@"\\srv\share")` returns "" - split segments by hand when naming pairs (bug caught by test).
- `dotnet test` builds only the test dependency graph: `wfs.exe` keeps a stale Core.dll until `scripts\build.ps1` runs.
- Cycle detection in TreeScanner must be per-path (target is ancestor of current physical dir or of any dir a link was
  followed from), NOT a global visited set — the global set was order-dependent under parallel scanning and dropped real folders.
- OneDrive placeholders carry ReparsePoint + RecallOnDataAccess; only reparse points WITHOUT recall/offline flags are treated as links.
- F22 has three separate target-only settings: `DiskQuotaEnabled` protects `MinFreeSpaceGb` on any
  destination whose filesystem reports capacity; `FreeUpSpaceAfterCopy` verifies the target with `CfGetSyncRootInfoByPath`
  and then sets Unpinned/clears Pinned; `RotateOnLowSpace` is the sole local permanent-delete exception.
  Rotation is allowed on every destination storage kind, including cloud roots, only for active ordinary
  files or confirmed cloud placeholders with non-null `copied_at` whose size+mtime still match, WARN-logs the full path, persists a tombstone
  at once, and sorts by copied_at → mtime → ctime. It never touches the source; on a cloud root its permanent
  deletion may be synchronised to the cloud. No OAuth/Graph token is involved.
- Dry-run over a huge tree spends most time printing the plan (226k lines ≈ 6 s); the scan itself is ~1-2 s.
- `SHFILEOPSTRUCT`: no `Pack=1` on x64 (AccessViolation). `RecycleBin.Send` throws if the item still exists afterwards.
- `StateStore` uses `Pooling=false` so `state.db` is released on Dispose (tests delete the file; GUI/CLI alternate).
- State schema is v4 (integer Unix-ms times, STRICT, `remote_id`/`remote_version` reserved, nullable
  `copied_at` for F22 rotation). Older databases migrate on open with a `VACUUM INTO` backup; v1 uses
  streaming row copy + `VACUUM`. A newer-than-known schema throws instead of being rewritten.
- SQLite: an `OR` over the primary key drops the index and full-scans the table — `LoadScope` needs TWO range queries
  joined by `UNION ALL` (measured 394 ms → 4.1 ms on a million rows). And `DROP TABLE` only frees pages inside the file:
  without a final `VACUUM` the migrated database came out BIGGER than the one it replaced (409 MB vs 279 MB).
- `LoadScope` walks the PK, which SQLite compares byte-wise, so a folder whose CASE changed is invisible to a scoped read
  (the full pass catches it). `forget`/`DeleteSubtree` must therefore keep using the full `Load` — neither BINARY nor
  NOCASE folds Cyrillic, and the caller types that path by hand. A test already guards this; it caught the regression.
- Load time on a million rows is dominated by materialising the objects (347 MB allocated), not by parsing: dropping text
  timestamps only bought 1.3x. Do not promise more without compacting `StateEntry` itself.
- Two independent age windows, and mixing them up is the bug this design exists to prevent: `maxAge` decides what is COPIED
  (before any bytes move; refusals are recorded as `too_old` rows, never as tombstones), `autoClean` decides what is REMOVED
  from the target. Only files expire; empty dirs are recycled and their rows DELETED (not tombstoned) so a new file brings
  the folder back. A `maxAge` refusal MUST be persisted — without the row the next pass dates the file "today" and copies it.
- Windows Mutex is re-entrant per thread: `PassLock` keeps an in-process HashSet of held names, otherwise a second
  TryAcquire on the same thread "succeeds". Acquire and Dispose must happen on the same thread (no await in between).
- CA1416: assembly-level `SupportedOSPlatform("windows")` via Directory.Build.props (SupportedPlatform items in
  Directory.Build.props do NOT work — the SDK adds its list later).
- Per-pair: `FolderPair.Enabled` (default true, absent in old configs = enabled); automatic passes skip paused pairs,
  `SyncRunner.Run(..., onlyPair:)` / `wfs sync --pair` runs one regardless. GUI row buttons call `MainViewModel.RunPairAsync/TogglePair`.
- There is NO global auto-check switch: `MainViewModel.FollowPairStates()` starts/stops the shared `BackgroundLoop`
  from the pair states (`FolderPair.Enabled`). `TogglePairAsync` writes config.json at once (when nothing else is dirty),
  re-evaluates the loop and runs that pair immediately. The tray's pause is a separate global pause (LoopState.Paused).
- Autosave: `MainViewModel.SaveNow()` runs on every edit (MarkDirty → SaveNow); there is no IsDirty/SaveCommand any more.
  Tests that construct MainViewModel with an ENABLED pair start the background checker immediately — write the pair paused
  on disk (and flip `Enabled` in memory if a manual run must include it) to keep tests deterministic.
- A button's Foreground is only INHERITED by its content, and the global `Selector="TextBlock"` rule hits an explicit
  `<TextBlock>` inside a button directly — a setter on the element beats inheritance. So every button class that
  repaints itself needs BOTH `Button.<class> Path.ico/.icof` (icon) AND `Button.<class> TextBlock` (label), or you get
  a white icon next to near-black text on a blue button. Avalonia picks the LAST matching style, so label rules go
  after the global `TextBlock` and `:disabled` goes after the class rules. Guarded by `ButtonLabelStyleTests`.
- Rounded borderless windows: the WINDOW must be `Background="Transparent"` + `TransparencyLevelHint="Transparent"`,
  with a `Border` (CornerRadius + ClipToBounds) as the visible surface. Painting the window and rounding only the
  inner border leaves square corners behind it (RecentChangesWindow shipped that way once).
- Design system: colours ONLY from Styles/Palette.axaml (ThemeDictionaries Light/Dark); style classes in Controls.axaml.
  Icons: `<Path Classes="ico">` (stroked outline) or `Classes="icof"` (filled); PathIcon FILLS its geometry and turns
  outline icons into black blobs — do not use it. Icon colour comes from `Button.<class> Path.ico` rules.
- Nav rail writes its own SelectedIndex into the binding at init: set `SelectedPage` AFTER the window is shown
  (that is why `--page` posts to the dispatcher).
- Long paths need `Classes="oneline"` (NoWrap + ellipsis); a ListBox inside a ScrollViewer is measured unbounded and
  columns stop constraining — let the ListBox scroll itself.
- Autostart gating: the Startup shortcut needs WorkFlowSync.exe (tray mode) OR wfs.exe (windowless); the scheduled task needs wfs.exe.
  In the App Debug folder only the GUI exists — tray autostart must stay available there.
- A running GUI locks WorkFlowSync.Core.dll: `dotnet build` fails with MSB3026. Ask the user before killing their app; a compile check
  can go to a temp folder via `dotnet build src\WorkFlowSync.App -o <tmp>`.
- Autostart needs `wfs.exe` next to the running exe; the App Debug folder has none, so GUI switches are disabled there
  (test via publish\portable). Tests never call schtasks for real — only argument building.
- PowerShell `Get-Content` without `-Encoding UTF8` shows Cyrillic log lines as mojibake; the files are fine.
  NEVER round-trip a source file through `Get-Content | Set-Content` — it double-encodes Cyrillic (did it to a test file; restored via git).
- View-model updates that go through `SynchronizationContext.Post` are invisible to the caller (and to tests) until the next
  message pump turn: `RunViewModel.OnUi` applies straight away when already on the captured context.
- Tray: `ShutdownMode.OnExplicitShutdown` + window Closing cancelled → hidden. Exit only via the tray menu, else the process lingers.
- `BackgroundLoop.Pause/Stop` must cancel AND wait for the worker task; otherwise `Start()` sees a live task and no-ops (test caught it).
- `AssetLoader.Open` needs a running Avalonia app — unit-test the .ico file on disk instead (path via [CallerFilePath]).
- app.ico is generated by scratchpad/make_icon.py (hand-written PNG+ICO bytes; no image libs on this machine).
- Edit `.cs`/`.md`/`.json` with Edit/Write only; PowerShell 5.1 `Set-Content` without `-Encoding utf8` corrupts Cyrillic.
- Bash heredocs in the agent tool choke on C# raw strings / quotes — use Write for code files.
- `Microsoft.Data.Sqlite` 10.0.12 (bundle_e_sqlite3) is compatible with net8.0 and single-file publish (`IncludeNativeLibrariesForSelfExtract`).
- (historical) FFS migration benchmark was tested against a 6.6 MB batch.ffs_batch file with 22,569 excludes.
