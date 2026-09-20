# Agent Playbook

This file is the shared policy for all agents working on this repository.

## Project

- Name: `WorkFlowSync`
- Slug: `workflowsync`
- Path: `D:\Projects\WorkFlowSync`
- Primary stack: C# / .NET 8 LTS (console, self-contained single-file, win-x64)
- Runtime target: local Windows only. No Docker, WSL2, or k3s for this project (override of the
  `D:\Projects\startup` template; `infra/` intentionally removed).
- Default dev namespace: none (no remote environment)

## Project Philosophy

- The repository is the single source of truth for requirements, architecture, code, tests,
  project skills, and release history.
- Document product behavior before or alongside implementation, and keep documentation synchronized
  with code and verification.
- Prefer small, reviewable changes; preserve unrelated and user-owned work.
- Protect secrets and personal data. Require explicit approval for external or consequential changes.
- Complete tasks with proportionate verification and report changes, tests, risks, and unverified areas.

## Product Documentation Capture (mandatory)

Whenever the user describes new or changed product functionality, update `docs/requirements.md` and
the applicable file under `docs/product/features/`, not just the chat summary. Keep
`docs/product/README.md` as the navigation entry point linking every feature area. Describe scenarios,
requirements, data, dependencies, failure modes, security/privacy constraints, acceptance criteria,
status, and open questions so another agent can implement the feature without chat history. Update
`docs/architecture.md` and `WorkFlowSync_SKILL.md` when technical knowledge changes.

Product documents (`docs/requirements.md`, `docs/product/**`, `docs/architecture.md`) are written in
Ukrainian; agent-operational documents (this file, `AGENTS.md`, `docs/testing.md`,
`docs/debugging.md`, `docs/deployment.md`, `docs/repository.md`) in English. Code identifiers,
comments and log messages are English.

## Release Change Tracking (mandatory)

- Add every completed user-visible fix, addition, behavior change, or removal to the matching section
  of `docs/pending-release-notes.md` in the same task. Use the product's user-facing language (Ukrainian)
  and omit implementation details, file/class names, commits, and agent attribution.
- Do not add release notes for internal refactoring or documentation-only planning with no delivered
  product behavior.
- During release, use the non-empty pending sections as the human-facing changelog.
- After successful publication, move used entries under `vX.Y.Z (YYYY-MM-DD)` in
  `docs/release-history.md`, then reset the pending sections for the next cycle.
- Treat published history as immutable except for a clearly identified documentary correction.

## Domain Invariants (never break)

These come from the customer's three rules (`docs/product/features/mirror-rules.md`):

1. The source is opened read-only. No code path may write to, rename in, or delete from a source root.
2. Local deletions are always Recycle Bin, never permanent — **on a root that has one**. Network shares do
   not: `SHFileOperation` with `FOF_ALLOWUNDO` deletes permanently there and still reports success. The
   customer decided on 2026-09-15 to keep that behaviour rather than add a `.wfs-trash` folder, so this is
   a documented exception, not a bug to fix. What it obliges the code to do instead (docs/plan-etap5.md §4.5):
   every such removal is logged as `WARN` with the full path, a removal is only carried out after the path
   itself is re-checked, and a pass that wants to remove a suspicious share of the pair is held back
   (`DeletionGuard`). None of those three may be "simplified" later.
3. A `tombstone` entry is never re-copied from the source, whatever the source contains.
4. A `local_modified` entry is never overwritten from the source.
5. `first_seen` is written once and never updated.
6. The target scanner reads attributes only (never file content) so OneDrive placeholders are not hydrated.
7. The executable must run as a normal user: `app.manifest` stays `asInvoker`.

The planner (`Planning/`) must stay free of file-system I/O so these invariants are unit-testable.

## Working Rules

- Work directly in this project folder by default.
- Keep changes scoped to the requested task.
- Preserve user changes and inspect the current diff before editing files that may already be modified.
- Do not create worktrees, duplicate project copies, agent-specific folders, or switch branches unless the user explicitly asks for that in the current task.
- Do not run `git add .`; stage only explicit files when staging or committing is requested.
- Do not commit, push, deploy, rotate secrets, or change external infrastructure without explicit approval.
- Do not store real credentials in the repository. `config.json`, `state.db`, `logs/` are ignored — they contain real paths and personal data.
- Never run `sync` (without `--dry-run`) against the user's real folder pairs from an agent session unless explicitly asked; use temp folders for e2e checks.
- Edit `.cs`/`.md`/`.json` files only with tools that preserve UTF-8 (Edit/Write); do not rewrite them via PowerShell 5.1 `Set-Content` without `-Encoding utf8`.
- Prefer small, reviewable changes.

## Parallel Agents

- Multiple agents may work in the same project folder only when the user assigns non-overlapping files or areas.
- Before editing, run `git status --short` and identify the files you plan to change.
- Avoid simultaneous edits to the same files.
- Never revert, overwrite, or normalize changes made by another agent or the user.
- Use branches, worktrees, or pull requests only after explicit user approval for that project or task.

## Completion Report

Every agent must report:

- Files changed.
- Commands run.
- Test results.
- Risks or unverified areas.
- Required human approvals.

## Repository

Follow `docs/repository.md`. Policy: local Git, no remote (`local-git-no-remote`). Do not create
remotes, public repositories, or branches unless the user explicitly asks.
