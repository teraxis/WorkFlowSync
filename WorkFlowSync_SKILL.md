# WorkFlowSync — Project Skill (operational guide for agents)

Read this before changing project behavior. Product rules live in `docs/product/features/`; this
file is the "how to work here" companion.

## What the product is

One-way mirror with memory: network share(s) → local folder(s) inside OneDrive. Three customer rules
(source deletions ignored; source additions copied; local deletions/changes final and never re-pulled),
plus `first_seen` timestamps and a retention window. Replaces FreeFileSync + a 22,569-line exclude list.

## Layout

```
src/WorkFlowSync.Core/   Config/ (SyncConfig, FolderPair, LinkMode, ConfigFile)  Model/ (StateEntry, EntryStatus, EntryKind)
                         Scanning/ (TreeScanner, ExcludeMatcher, ScanEntry)  State/StateStore  Planning/ (SyncPlanner, SyncPlan)
                         Execution/ (SyncExecutor, RecycleBin)  Logging/SyncLog (+Prune)
                         SyncRunner (one pass)  LoopRunner (resident)  PassLock (cross-process)  Autostart (Startup .lnk, schtasks)
src/WorkFlowSync.Cli/    wfs.exe: Program.cs (commands), ConsoleOwner.cs (pause on double-click), app.manifest (asInvoker + longPathAware + UTF-8)
src/WorkFlowSync.App/    WorkFlowSync.exe: Avalonia 11.3 GUI - Styles/ (Palette, Controls, Icons = design system, docs F12),
                         Views/ (MainWindow, PairDialog, ConfirmDialog), ViewModels/ (Main, Pair, Run, Autostart; no Avalonia types),
                         Services/BackgroundLoop.cs (tray loop), App.axaml.cs (TrayIcon+NativeMenu), Assets/app.ico, Converters.cs
tests/WorkFlowSync.Tests/
scripts/                 build.ps1, test.ps1, publish.ps1
config.example.json      reference config
```

Stage plan: `docs/requirements.md` §4. Stages 0–4 all done (scaffold, GUI, sync engine, retention, resident mode + autostart, tray). FFS import was REMOVED on 2026-09-15 at the customer's request (docs F9 kept as history).

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
- Avalonia pinned to 11.3.0 (12.x = different API). `dotnet new avalonia.app` emits 12.x - rewrite the csproj.
- GUI verification: launch the exe, check the process stays alive and no `logs\crash-*.log` appears, then Stop-Process. Do NOT drive the
  window with synthetic mouse clicks or `CopyFromScreen` captures while the user is at the machine: on 2026-09-14 a capture grabbed
  unrelated windows (mail, credentials) because the app was not in the foreground. Ask the user before any screenshot.
  `PrintWindow` does not capture Avalonia (known from PathShortener).

## Scanner rules of thumb (F5)

- `FileSystemEnumerable<T>` with `EnumerationOptions { BufferSize = config.ScanBufferSize, AttributesToSkip = 0, RecurseSubdirectories = false, IgnoreInaccessible = true }`.
- Own recursion with a work queue and `ScanParallelism` workers; decide per entry on `ReparsePoint` (F4), excludes (F6), tombstone dirs (F2).
- No per-file API calls; compare size + mtime (2 s tolerance).

## Gotchas

- .NET SDK 8.0.425 at `C:\Program Files\dotnet` is on PATH; `~\.dotnet` has 8.0.422 too — do not mix `DOTNET_ROOT`.
- Cli and App csproj set `RuntimeIdentifier=win-x64`, so Debug output is under `bin\Debug\net8.0\win-x64\` (`wfs.exe`, `WorkFlowSync.exe`).
- Tests reference the App project (WinExe) to test view-models; fine for xUnit.
- `Path.GetFileName(@"\\srv\share")` returns "" - split segments by hand when naming pairs (bug caught by test).
- `dotnet test` builds only the test dependency graph: `wfs.exe` keeps a stale Core.dll until `scripts\build.ps1` runs.
- Cycle detection in TreeScanner must be per-path (target is ancestor of current physical dir or of any dir a link was
  followed from), NOT a global visited set — the global set was order-dependent under parallel scanning and dropped real folders.
- OneDrive placeholders carry ReparsePoint + RecallOnDataAccess; only reparse points WITHOUT recall/offline flags are treated as links.
- Dry-run over a huge tree spends most time printing the plan (226k lines ≈ 6 s); the scan itself is ~1-2 s.
- `SHFILEOPSTRUCT`: no `Pack=1` on x64 (AccessViolation). `RecycleBin.Send` throws if the item still exists afterwards.
- `StateStore` uses `Pooling=false` so `state.db` is released on Dispose (tests delete the file; GUI/CLI alternate).
- Retention: only files expire; empty dirs are recycled and their rows DELETED (not tombstoned) so a new file brings the folder back.
- Expired-at-first-sight files are copied on pass 1 and recycled on pass 2 (open question in F3).
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
- Publish size 34.6 MB with `EnableCompressionInSingleFile`; `DebugType=embedded` in `Directory.Build.props` keeps `.pdb` files out of `publish\`.
- (historical) The user's real FFS config: `D:\OneDrive\Робоча папка\App\freefilesync\batch.ffs_batch` (6.6 MB, 22,569 excludes; pair `E:\vrp` → `E:\OneDrive\Робоча папка`). Real paths for the pairs are still to be confirmed (requirements open question 1).
