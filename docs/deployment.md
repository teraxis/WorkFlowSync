# Deployment

WorkFlowSync is a portable Windows executable. There is no server, container, or remote environment.

## Build the portable executable

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
```

Output: `publish\portable\WorkFlowSync.exe` (GUI, ~45 MB) and `wfs.exe` (console, ~35 MB), both
self-contained single-file win-x64. No .NET runtime installation is required on the target machine.

## Install for the user (no administrator rights)

1. Copy `WorkFlowSync.exe` and `wfs.exe` to a folder the user owns, e.g. `D:\Tools\WorkFlowSync\`.
   Everything the app writes (config.json, state.db, logs\) lands in that same folder, whatever the working directory.
   Do not put it inside OneDrive (state.db would be synced and locked).
2. Start `WorkFlowSync.exe`, add the folder pairs and settings (automatically creates `config.json`).
3. `wfs config validate`.
4. Migrate from FreeFileSync: `docs/product/features/ffs-migration.md`.
5. Choose the run mode (`docs/product/features/cli-and-scheduling.md`):
   - resident: shortcut in `shell:startup` running `wfs sync --loop`;
   - scheduled: `schtasks /Create /SC MINUTE /MO 30` running `wfs sync --once`.

Both work under a standard user account; never use `/RL HIGHEST` or "run with highest privileges".

## Upgrade

Replace `WorkFlowSync.exe` and `wfs.exe`; `config.json`, `state.db`, `logs\` stay. Schema migrations run
automatically on first start of a newer version (Stage 1+ records `schema_version` in `meta`).

## Release & Publishing

1. Bump `<Version>` in `Directory.Build.props`.
2. Move `docs/pending-release-notes.md` entries to `docs/release-history.md` under `vX.Y.Z (YYYY-MM-DD)`.
3. Create release document in `docs/releases/vX.Y.Z.md` and update `README.md` / `README.uk.md` download links.
4. `scripts\publish.ps1`, smoke-test `version` and `config validate`.
5. Compress `publish\portable\*` into `publish\WorkFlowSync-X.Y.Z-win-x64.zip`.
6. Tag locally (`git tag vX.Y.Z`) and push commits and tags when the user asks.
7. **GitHub Release Assets (Critical for Auto-Update)**: When creating a GitHub Release (`gh release create`), ALWAYS attach three assets:
   - `publish\WorkFlowSync-X.Y.Z-win-x64.zip` (portable archive)
   - `publish\portable\WorkFlowSync.exe` (main GUI binary)
   - `publish\portable\wfs.exe` (CLI binary)
   The built-in updater (`UpdateChecker.cs`) searches release assets specifically for `WorkFlowSync.exe`. If the raw `.exe` is missing, running applications will not detect the update.
