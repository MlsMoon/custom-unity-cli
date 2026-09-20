---
name: custom-unity-cli
description: >
  Maintain the custom-unity-cli package (Assets/Plugins/CustomUnityCli nested git).
  Use when committing to this repo, writing changelog entries, running verify.ps1 or
  cucli selftest, adding command groups, or changing the Editor worker / Tools/cucli.ps1.
  Triggers: cucli, custom-unity-cli, git commit, commit message, verify.ps1, selftest.
---

# Maintain custom-unity-cli

This directory is its own git repository (`com.igp.custom-unity-cli`). Do not use Plastic
`cm checkin` here. Host-project rules (Chinese Plastic messages, BilliardsBot skills) do
not apply to commits in this repo.

## Commit messages (English only)

Write every git commit **in English**. Conventional Commits, English subject and body:

```text
type(scope): imperative summary in English

Optional body: what changed and why, still English.
Optional footer: Verified: ...
```

- Allowed types: `feat`, `fix`, `docs`, `test`, `refactor`, `chore`, `perf`.
- Scope is optional (`cli`, `worker`, `selftest`, `docs`).
- Subject: imperative mood, ~72 characters, no trailing period.
- **No Chinese** (or other non-English copy) in the subject or body. Runtime user-facing
  command `message` strings in C# may stay bilingual; commit messages may not.
- Do not copy BilliardsBot's `type(scope): 中文描述` convention into this repository.

Examples:

```text
fix(cli): accept --path as a materialPath alias
feat(cli): honour --force on scene load and close
docs: require English Conventional Commit messages
```

## Before you commit

1. Stay inside this package root. `git status` here, not at the Unity project root.
2. Do not commit host-project files, `Library/`, or `Assets/CucliSelfTest/`.
3. Keep project-specific commands out of this package. See `docs/self-iteration.md`.
4. Run `Tools/verify.ps1` (no `-WithUnity` unless asked). It must pass.
5. After Editor-facing behaviour changes, run `cucli selftest -Tier AB` against a live
   2022.3 worker when one is available.

## Package boundary

- Implementation lives in this folder. Host projects extend via
  `Assets/CustomUnityCli.Extensions`, never by editing this package for one game.
- Do not add `com.igp.world-environment` or other host-only packages as dependencies.
- Target is Unity **2022.3**. Do not re-scope the manifest floor without an explicit
  support decision.

## Verification

```powershell
Tools/verify.ps1
# optional, when an Editor worker is online:
./Tools/cucli.ps1 selftest -Tier AB
```
