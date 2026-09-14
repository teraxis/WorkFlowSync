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
                         Execution/ (SyncExecutor, RecycleBin)  Logging/SyncLog  Ffs/FfsBatchImporter  SyncRunner (one pass over all pairs)
src/WorkFlowSync.Cli/    wfs.exe: Program.cs (commands), ConsoleOwner.cs (pause on double-click), app.manifest (asInvoker + longPathAware + UTF-8)
src/WorkFlowSync.App/    WorkFlowSync.exe: Avalonia 11.3 GUI - Views/ (MainWindow, PairDialog), ViewModels/ (Main, Pair; no Avalonia types), Converters.cs
tests/WorkFlowSync.Tests/
scripts/                 build.ps1, test.ps1, publish.ps1
config.example.json      reference config
```

Stage plan: `docs/requirements.md` §4. Stages 0, 0.2 (GUI), 1 (sync engine) and 2 (retention, FFS import, forget) done. Stage 3 = --loop, autostart, log rotation; Stage 4 = tray.

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
- Edit `.cs`/`.md`/`.json` with Edit/Write only; PowerShell 5.1 `Set-Content` without `-Encoding utf8` corrupts Cyrillic.
- Bash heredocs in the agent tool choke on C# raw strings / quotes — use Write for code files.
- `Microsoft.Data.Sqlite` 10.0.12 (bundle_e_sqlite3) is compatible with net8.0 and single-file publish (`IncludeNativeLibrariesForSelfExtract`).
- Publish size 34.6 MB with `EnableCompressionInSingleFile`; `DebugType=embedded` in `Directory.Build.props` keeps `.pdb` files out of `publish\`.
- The user's real FFS config: `D:\OneDrive\Робоча папка\App\freefilesync\batch.ffs_batch` (6.6 MB, 22,569 excludes; pair `E:\vrp` → `E:\OneDrive\Робоча папка`). Real paths for the pairs are still to be confirmed (requirements open question 1).
