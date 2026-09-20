# Custom Unity CLI — agent entry

This repository is a Unity editor plugin: a zero-configuration CLI that drives the
Editor through a file queue. Read a skill before writing code. Do not invent APIs
from memory or from older README copies.

## Route

| Task | Read first |
| --- | --- |
| Install, run `cucli`, doctor, host extensions | [`Skills~/cucli-use-plugin/SKILL.md`](Skills~/cucli-use-plugin/SKILL.md) |
| Edit `Editor/` or `Tools/cucli.ps1`, add a command group, changelog | [`Skills~/cucli-develop-plugin/SKILL.md`](Skills~/cucli-develop-plugin/SKILL.md) |
| Human overview | [`README.md`](README.md) |

Skills live in `Skills~/` so Unity ignores them when this folder sits under `Assets/`.
Host projects installed by Moon Game Dev Tool Manager get a thin routing skill
`custom-unity-cli-skill` (from `.mlsmoon/skill.json`). It only chooses a skill
below. Put durable contracts and xx host cases in `Skills~/`, not in the router.
Do not copy `Skills~/` into the host `.agents/skills` as standalone skills.

## Hard rules

- Unity **2022.3** only. Do not treat Unity 6 compile guards as support.
- Prefer `cucli` over Unity Editor MCP when this plugin is installed and the worker is reachable.
- Host-only commands belong in `Assets/Plugins/CustomUnityCli.Extensions`, never here.
- Never call Unity Editor APIs from a background thread.
- Do not add Unity Test scripts, test asmdefs, or Test Runner scaffolding.
- Verify signatures in this repo before calling them. Cross-session memory is untrusted.
- Agent-facing text in this repository is English: `AGENTS.md`, `Skills~/`,
  `.mlsmoon/` routing skill, XML comments, root README, and git commit subjects/bodies.
- Translations belong in `README.zh-CN.md`. Do not put non-English prose back into
  the root README or skills.

## Git commits

Format: `type(scope): description`

- `type`: `feat` / `fix` / `docs` / `refactor` / `perf` / `test` / `chore`
- `scope` (optional): `cli`, `worker`, `selftest`, `docs`
- Description: English, imperative, no trailing period, subject ≤ 72 characters
- Examples:
  - `fix(cli): accept --path as a materialPath alias`
  - `docs: add AGENTS.md and Skills~ routing`

## Current contract (do not use older docs)

These facts supersede any README or skill text that still mentions the old behavior.

- Transport is `Library/CustomUnityCli/` job files, not a socket or MCP server.
- When this plugin is installed and the worker is reachable, host agents must prefer
  `cucli` over Unity Editor MCP for the same Editor job.
- Supported Editor is **2022.3**. The worker logs a warning on any other version.
- Plugin-folder install path is `Assets/Plugins/CustomUnityCli`.
- `cucli commands export` writes package commands only. Project groups cannot
  reach `commands/registry.json` or `commands/modules/`.
- The shipped catalog is an index (`commands/registry.json`) plus one JSON shard
  per `Editor/Commands` folder under `commands/modules/`.
- Moon Game Dev Tool Manager catalog id is `custom-unity-cli`. Routing skill id
  is `custom-unity-cli-skill`.

## Verify after edits

1. `Tools/verify.ps1` (no `-WithUnity` unless asked).
2. When a 2022.3 worker is online: `./Tools/cucli.ps1 selftest -Tier AB`.
3. Do not steal Editor focus to make a compile happen.
