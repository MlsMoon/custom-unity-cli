---
name: cucli-develop-plugin
description: Maintain the Custom Unity CLI package (Assets/Plugins/CustomUnityCli nested git). Covers commits, changelog, verify.ps1, cucli selftest, command groups, and the Editor worker. Use when committing to this repo, adding command groups, or changing Tools/cucli.ps1.
---

# Custom Unity CLI: Develop the Plugin

Maintenance guide for edits under this plugin root.

Integration contracts live in the sibling skill `cucli-use-plugin`.
Current-contract overrides: [`../../AGENTS.md`](../../AGENTS.md).

This directory is its own git repository (`com.igp.custom-unity-cli`). Do not use Plastic
`cm checkin` here. Host-project rules do not apply to commits in this repo.

## Commit messages (English only)

Write every git commit **in English**. Conventional Commits:

```text
type(scope): imperative summary in English
```

- Allowed types: `feat`, `fix`, `docs`, `test`, `refactor`, `chore`, `perf`.
- Scope is optional (`cli`, `worker`, `selftest`, `docs`).
- Subject: imperative mood, ~72 characters, no trailing period.
- No Chinese in the subject or body. Runtime user-facing command `message`
  strings in C# may stay bilingual; commit messages may not.

## Before you commit

1. Stay inside this package root. `git status` here, not at the Unity project root.
2. Do not commit host-project files, `Library/`, or `Assets/CucliSelfTest/`.
3. Keep project-specific commands out of this package. See `docs~/self-iteration.md`.
4. Run `Tools/verify.ps1` (no `-WithUnity` unless asked). It must pass.
5. After Editor-facing behaviour changes, run `cucli selftest -Tier AB` against a live
   2022.3 worker when one is available.

## Package boundary

- Implementation lives in this folder. Host projects extend via
  `Assets/Plugins/CustomUnityCli.Extensions` (sibling of this package folder, never
  inside it). Do not edit this package for one game.
- Package commands are Core (`Editor/Commands/Core/`) or Extended
  (`Editor/Commands/Extended/`). Do not grow the Core surface without an explicit
  contract decision. Never call Unity Editor APIs from a background thread.
- Do not add `com.igp.world-environment` or other host-only packages as dependencies.
- Target is Unity **2022.3**. Do not re-scope the manifest floor without an explicit
  support decision.
- Console callbacks run on the log-emitting thread. Skip email matching when no `@`
  is present, cache the process user-profile path, retain path redaction, and verify real trace costs and unchanged
  email sanitization before changing the capture path.

## Architecture (where to look)

```text
Tools/cucli.ps1          CLI: parse, offline groups, job files
Editor/Core/             protocol, queue, heartbeat, console capture
Editor/Commands/Core/    stable command groups
Editor/Commands/Extended extra groups (still shipped)
commands/registry.json   catalog index (aliases, offline, controlActions)
commands/modules/        exported Editor groups, one JSON per Commands folder
docs~/                   protocol, extension contract, self-iteration
```

Writing a new group: `docs~/extension.md`. File protocol: `docs~/protocol.md`.

## Verification

```powershell
Tools/verify.ps1
# optional, when an Editor worker is online (Core surface by default):
./Tools/cucli.ps1 selftest -Tier AB
# include Extended groups:
./Tools/cucli.ps1 selftest -Tier AB -IncludeExtended
```

Do not add Unity Test scripts or test asmdefs in this package.
`verify.ps1` is the build gate. `cucli selftest` is the behaviour gate.
Do not start, focus, or disturb a running Editor to run the build gate.
