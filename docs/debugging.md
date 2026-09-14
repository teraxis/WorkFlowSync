# Debugging

## Local

- GUI crash dump: `logs\crash-*.log` next to the executable.
- Application log: `logs\wfs-YYYY-MM-DD.log` next to the config (format in
  `docs/product/features/logging-and-diagnostics.md`). Add `--verbose` for per-directory `DEBUG` lines.
- State: `state.db` (SQLite). Inspect with any SQLite client, e.g. `sqlite3 state.db "select status,count(*) from entries group by status"`
  or DB Browser for SQLite. Do not edit while a pass is running.
- Dry run: `wfs sync --once --dry-run --config <path>` prints the planned actions without touching anything.
- Console: UTF-8 is forced by the app; if a terminal shows garbage, run `chcp 65001` or read the log file.

## Performance

- Timing per phase (scan source / scan target / plan / execute) is in the log for every pass.
- To confirm the scanner makes no per-file calls, watch `WorkFlowSync.exe` in Process Monitor filtered
  to `QueryDirectory` vs `CreateFile`/`QueryAttributes` on individual files.
- Tune `scanBufferSize` (256 KB → 1 MB) and `scanParallelism` (8 → 16) in config and compare `scan source took=`.

## Common Failures

| Symptom | Check |
|---------|-------|
| `config not found` | `--config` path or `config.json` next to the exe |
| Exit code 3 | Command not implemented yet — see stage plan in `docs/requirements.md` |
| `database is locked` | Second instance (mutex should prevent) or antivirus holding `state.db` |
| Source unreachable | Pass is skipped with a `WARN`; verify VPN/mapped drive; nothing in state changes |
| File keeps reappearing after local delete | Its entry is not `tombstone`: another pair with the same target, or it was deleted before the pass that recorded it |

## Debugger

Antigravity IDE / VS Code with `ms-dotnettools.csharp` (installed): open the folder
`D:\Projects\WorkFlowSync` (or `WorkFlowSync.code-workspace`) and press F5. `.vscode/launch.json`
provides `GUI (WorkFlowSync.exe, example config)`, `wfs config validate (example)`,
`wfs sync --once --dry-run (example)` and `wfs custom args (prompt)`; each runs the `build` task first. Breakpoints work in both Core and Cli.

Without an IDE: `dotnet run --project src/WorkFlowSync.App` (GUI) or
`dotnet run --project src/WorkFlowSync.Cli -- config validate --config config.example.json`
runs the Debug build; `dotnet watch --project src/WorkFlowSync.Cli -- <args>` rebuilds on save.

## Remote

No remote environment for this project.
