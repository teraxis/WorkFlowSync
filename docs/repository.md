# Repository

## Git

- Policy: `local-git-no-remote` (user decision, 2026-09-14). Registered in `D:\Projects\.maestro\projects.json`.
- Default branch: `main`
- Default local workflow: work directly in the project folder on the current branch.
- Feature/fix branches: optional, only when explicitly requested for a task.

Do not create worktrees, duplicate project copies, or switch branches unless the user explicitly asks for that in the current task.

## Ignored runtime data

`config.json`, `state.db`, `state.db-*`, `logs/`, `publish/`, `bin/`, `obj/` are ignored. They contain
real network paths, file names (personal data) and build output. Only `config.example.json` is tracked.

## Adding a remote later

If the user later asks for GitHub (private by default):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\Projects\tools\setup-git-provider-repo.ps1 -ProjectPath D:\Projects\WorkFlowSync -Provider github -Owner teraxis -Visibility private -CreateRemote -Push
```

`.github/workflows/ci.yml` already builds and tests on push/PR and is harmless while no remote exists.
