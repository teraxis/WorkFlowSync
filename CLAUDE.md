# Claude Code Instructions

Follow `docs/agent-playbook.md` as the shared source of truth. Read `WorkFlowSync_SKILL.md` before
changing project behavior.

Work directly in `D:\Projects\WorkFlowSync` by default. Do not create worktrees, duplicate project
copies, or switch branches unless the user explicitly asks for that in the current task.

Do not modify unrelated files. Before reporting completion, run the commands in `docs/testing.md`
(`scripts\build.ps1`, `scripts\test.ps1`) or state exactly why they could not run.

Never run `WorkFlowSync sync` without `--dry-run` against the user's real folder pairs from an agent
session unless explicitly asked; e2e checks use temp folders.

## Default user-facing language

Use Ukrainian as the default language for all user-facing communication and for product documents
(`docs/requirements.md`, `docs/product/**`, `docs/architecture.md`, release notes).

Use English only for:
- code identifiers, file paths, command names, and shell output;
- logs, error messages, stack traces, and exact tool output;
- source comments and agent-operational documents;
- direct quotes from external documentation.

If the user explicitly asks to use another language, follow that request for the current task.
