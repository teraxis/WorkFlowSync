# Agent Instructions

Read `docs/agent-playbook.md` before changing files. Read `WorkFlowSync_SKILL.md` before changing
project behavior.

## Required Behavior

- Work directly in `D:\Projects\WorkFlowSync` by default.
- Do not create worktrees, duplicate project copies, agent-specific folders, or switch branches unless the user explicitly asks for that in the current task.
- Inspect `git status --short` before editing.
- Keep changes scoped to the requested task and preserve unrelated work.
- Do not run `git add .`; stage only explicit files when the user asks for staging or commits.
- Respect the domain invariants in `docs/agent-playbook.md` (read-only source, Recycle Bin only, tombstones are final, `asInvoker`).
- For each new or changed product behavior, update `docs/requirements.md`, the applicable document
  under `docs/product/features/`, and the links in `docs/product/README.md`.
- For each completed user-visible change, update `docs/pending-release-notes.md` in the same task.
- After code changes, run the verification commands listed in `docs/testing.md` or state why they could not run.
- Report changed files, checks run, unverified areas, and required approvals.

## Project Commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1     # dotnet build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1      # dotnet test
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1   # portable single-file exe -> publish\portable
```

Equivalent `make` targets exist in `Makefile` for environments that have `make`.

## Default user-facing language

Use Ukrainian as the default language for all user-facing communication and for product documents
(`docs/requirements.md`, `docs/product/**`, `docs/architecture.md`, release notes).

Use English only for:
- code identifiers, file paths, command names, and shell output;
- logs, error messages, stack traces, and exact tool output;
- source comments and agent-operational documents (`AGENTS.md`, `docs/agent-playbook.md`, `docs/testing.md`, `docs/debugging.md`, `docs/deployment.md`, `docs/repository.md`);
- direct quotes from external documentation.

If the user explicitly asks to use another language, follow that request for the current task.
