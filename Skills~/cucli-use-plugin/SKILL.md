---
name: cucli-use-plugin
description: Drive a Unity 2022.3 project with Custom Unity CLI (cucli). Covers plugin-folder install, doctor, everyday commands, host extensions, and Unity 6 out of scope. Use when installing CustomUnityCli, running Tools/cucli.ps1, scaffolding CustomUnityCli.Extensions, or diagnosing a missing worker.
---

# Custom Unity CLI: Use the Plugin

Integration guide for the consuming project. Paths are relative to the plugin root
(`CustomUnityCli/`). Before coding, also read [`../../AGENTS.md`](../../AGENTS.md).

## What the plugin does

- Drops an Editor worker under `Assets/Plugins/CustomUnityCli`.
- Exposes `Tools/cucli.ps1` so a terminal, CI job, or agent can drive the Editor
  through a file queue (no port, no MCP server, no per-project config file).
- Host-only commands live in a sibling folder `Assets/Plugins/CustomUnityCli.Extensions`,
  never inside this package.

## Requirements

- Unity **2022.3** only. Unity 6 projects must use Unity's own CLI instead.
- PowerShell 5.1 or 7+.
- Plugin-folder install also needs `com.unity.nuget.newtonsoft-json` in the host manifest.

## 1. Install

Recommended: copy or clone the folder.

```text
Assets/Plugins/CustomUnityCli
```

Moon Game Dev Tool Manager can install the same path from catalog id `custom-unity-cli`.

Then open Unity once. Confirm:

```powershell
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 doctor --deep
```

Correct:

- Worker heartbeat exists under `Library/CustomUnityCli/worker/heartbeat.json`.
- `cucli status` reports the Editor is reachable.

Common mistakes:

- Installing on Unity 6 and treating a compile as support. The worker logs a warning on purpose.
- Looking for a settings dialog or `.cuclirc`. There is none.
- Putting host commands inside this package folder.

Fictional host wiring: `references/host-integration-xx.md`.

## 2. Everyday commands

Entry point: `Tools/cucli.ps1` (or `./cucli.ps1` if that folder is on PATH).

```powershell
./cucli.ps1 status
./cucli.ps1 editor state
./cucli.ps1 object create --name Ball --primitive sphere
./cucli.ps1 scene get-hierarchy --page-size 50 --format json
./cucli.ps1 refresh compile --wait --timeout 300
./cucli.ps1 console --level error --tail 20
./cucli.ps1 commands list --surface core
```

`--format human|json|ndjson`. Destructive actions need `--force`.

## 3. Host extensions

Project-specific groups belong here, not in this plugin:

```text
<project>/Assets/Plugins/CustomUnityCli.Extensions/Editor/Commands/
```

```powershell
./cucli.ps1 extend init
./cucli.ps1 meta new-command --group assetpolicy --summary "Host asset rules" --action check
./cucli.ps1 refresh compile --wait
```

Do not reuse shipped group names (`object`, `asset`, …). Full rule: `docs~/self-iteration.md`.

## Hard rules

- Prefer `cucli` over Unity Editor MCP / `execute_code` whenever this plugin is installed
  and the worker is reachable. MCP is a fallback, not the default way to drive the Editor.
- Do not add Unity Test scripts or test asmdefs to the host for this plugin.
- Do not call Unity Editor APIs from a background thread via `cucli`.
- Do not write real host scene / prefab names back into this plugin's skills.
