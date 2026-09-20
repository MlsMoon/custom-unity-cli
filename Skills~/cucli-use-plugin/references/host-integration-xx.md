# Custom Unity CLI: Host Integration (xx)

Fictional host **xx**. Replace names with the real project. Do not write a real
host's scene, prefab, or extension-group names back into this plugin.

Plugin contracts stay in the parent skill. This file is only the host wiring shape.

## Host call sites

1. **Install path** — xx keeps the package at `Assets/Plugins/CustomUnityCli`.
   Do not move `Tools/cucli.ps1` out of that folder.
2. **First check** — After Unity compiles, xx runs
   `./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 doctor --deep`.
3. **Agent entry** — Host agents load the routing skill `custom-unity-cli-skill`
   (Moon Game Dev Tool Manager) or `AGENTS.md` in a standalone clone, then this
   use skill. They do not copy `Skills~/` into `.agents/skills`.
4. **Host commands** — xx-only groups live in
   `Assets/Plugins/CustomUnityCli.Extensions/Editor/Commands/`. Never inside the
   published package folder.

## Host glue

- Keep newtonsoft-json in the host manifest when this is a plugin-folder install.
- Runtime state stays in `Library/CustomUnityCli/`. Deleting that folder is safe.
- Unity 6 hosts should not install this plugin; use Unity CLI instead.

## Hard rules

- Unity **2022.3** only.
- Do not add Unity Test scripts for cucli in the host.
- Plugin git commits are English Conventional Commits (`AGENTS.md`).
