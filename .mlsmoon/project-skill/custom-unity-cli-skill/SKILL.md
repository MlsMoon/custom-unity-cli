---
name: custom-unity-cli-skill
description: Route Custom Unity CLI host agents to the packaged source skills. Covers install vs package maintenance. Use when choosing which skill to read; edit Skills~/ for cucli usage, extensions, or Editor worker changes.
---

# Custom Unity CLI: Host Router

> **This file only routes. Keep it thin.** Do not put host-project facts, paths, or
> verification logs here.
>
> Durable contracts belong in the **source skills** (edit those):
> - Drive Unity from a host project: [`Skills~/cucli-use-plugin/SKILL.md`](../../../Skills~/cucli-use-plugin/SKILL.md)
> - Edit this package: [`Skills~/cucli-develop-plugin/SKILL.md`](../../../Skills~/cucli-develop-plugin/SKILL.md)
> - Current contract overrides older docs: [`AGENTS.md`](../../../AGENTS.md)
>
> Do not copy `Skills~/` into the host `.agents/skills` as standalone skills.

## Which skill to read

| Task | Read |
|---|---|
| Install, run `cucli`, doctor, host extensions | `cucli-use-plugin` |
| Edit `Editor/` / `Tools/cucli.ps1`, add a command group, changelog, verify | `cucli-develop-plugin` |

Paths are relative to the plugin root `CustomUnityCli/`. Use the host install path.

## xx cases live in the source skills

Fictional host **xx** cases are split under source `references/`:

| Topic | File |
|---|---|
| Host install and first `cucli` calls | `Skills~/cucli-use-plugin/references/host-integration-xx.md` |

Do not write real scene / prefab / command-group names from a host project back into this router
or into the public source skills.

## Checklist

- [ ] Read the matching source skill and `AGENTS.md` before writing code
- [ ] Verify APIs in this repo; do not trust an old README
- [ ] Do not add Unity Test scripts
- [ ] Put new reusable patterns in source-skill `references/`, not here
