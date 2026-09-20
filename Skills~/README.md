# Agent skills

Two skills ship with this plugin. The folder is named `Skills~` so Unity ignores it under `Assets/`.

| Skill | When to load |
| --- | --- |
| [cucli-use-plugin](cucli-use-plugin/SKILL.md) | Install, run `cucli`, doctor, host extensions |
| [cucli-develop-plugin](cucli-develop-plugin/SKILL.md) | Edit `Editor/` or `Tools/cucli.ps1` |

Moon Game Dev Tool Manager installs a thin host routing skill
(`custom-unity-cli-skill` from `../.mlsmoon/`). That router only points here.
Edit these source skills, not the router. Do not copy `Skills~/` into the host
`.agents/skills` as standalone skills.
Standalone clone of this repo: read [`../AGENTS.md`](../AGENTS.md) first; many agents already load that file.
Agent-facing text (`SKILL.md`, references, git commits) is English.
Translations belong in `README.zh-CN.md`.
