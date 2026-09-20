# Custom Unity CLI

**A zero-configuration command line interface for the Unity Editor.**

`cucli` drives Unity from a terminal, a CI job or an AI agent without a socket, a server, an SDK
download or a single line of per-project configuration. Drop one folder into your project and the
whole Editor becomes scriptable.

```powershell
# No install step, no config file, no server to start.
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 status
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 object create --name Ball --primitive sphere
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 scene get-hierarchy --page-size 50 --format json
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 refresh compile --wait
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 console --level error --tail 20
```

[中文说明见 `README.zh-CN.md`](README.zh-CN.md)

---

## Compatibility — read this first

**This tool is for Unity 2022 projects.** It is developed and verified against **Unity 2022.3**,
and everything here — the Editor worker, the command surface, the file protocol — assumes that
generation of the Editor API.

> ### Unity 6: use Unity's own CLI instead
>
> **This plugin does not target Unity 6 for now, and that is a scope decision rather than an
> oversight.**
>
> If your project is on Unity 6, use **Unity CLI** — the official command-line interface for that
> generation. It is the supported path there, it is maintained alongside the Editor, and it does not
> need a bridge package at all:
>
> - [Unity CLI reference](https://docs.unity.com/en-us/unity-cli/unity-cli-reference)
> - [Meet the Unity CLI](https://unity.com/blog/meet-the-unity-cli)
> - [Installing and using Unity CLI](https://unity.com/resources/unity-pipeline-cli-technical-walkthrough)
>
> Unity 6 has not been considered in this design. The file-queue transport, the domain-reload
> contract and the command surface were all built and tested against 2022.3 only, so nothing here
> should be assumed to work on it.
>
> The same applies below 2022: the 2021.3 LTS line is untested. Treat **2022.3 as the supported
> target** and anything else as unsupported.

One thing that can look contradictory: a few command implementations carry forward-compatibility
guards — a `PhysicMaterial`/`PhysicsMaterial` alias, a reflection path for Unity 6 Build Profiles.
Those exist so the assembly still *compiles* if someone opens the package in a newer Editor. They are
not a claim of support, and nothing about the behaviour on those versions has been tested.

Because compiling is not the same as being supported, the boundary is enforced rather than merely
stated: on first load the worker checks the running Editor version and, when it is not 2022.3, logs a
single warning naming the version and pointing at Unity CLI. You will see it in the Console the moment
the package is imported, so nobody can install this on Unity 6 and mistake silence for a green light.

## Why another Unity CLI?

Most Unity automation today means one of three things: an MCP server with a port and a client
wiring step, a bespoke `-executeMethod` batch script per task, or a pile of Editor menu items that
only a human can click. Each works, and each costs you setup time on every new machine and every
new project.

`cucli` takes a different transport: **a directory of JSON files**.

```
<project>/Library/CustomUnityCli/
├── worker/heartbeat.json          # the Editor publishes liveness + state here once a second
├── worker/commands.json           # the command catalog actually compiled in this Editor
├── console/entries.ndjson         # captured console output, readable while Unity is busy
└── jobs/
    ├── control/                   # jobs allowed to bypass the "Editor is idle" gate
    ├── queued/   running/   waiting/
    ├── succeeded/ failed/ cancelled/
    └── cancel-requests/
```

The CLI writes a job file; the Editor worker picks it up on its next update tick and writes a
result file. That single design choice buys most of the properties below.

| Property | Why it falls out of the file queue |
| --- | --- |
| **No port, no server** | There is nothing to bind, so nothing to firewall, collide or leak. |
| **Survives domain reload** | A job can be parked in `waiting/` across a script recompile and resolved on the far side. A socket cannot do that. |
| **Cannot deadlock the Editor** | A tiny second queue (`control/`) carries the handful of commands that must run even while the Editor is compiling, which is what breaks the Play Mode / compile interlock. |
| **Works while Unity is busy** | Console output and job state are files, so `cucli console` and `cucli job list` never wait for the Editor. |
| **Auditable** | Every request and result is a timestamped file you can read, diff and keep. |
| **Crash-safe** | Every write is a same-directory atomic replace; a half-written file is never observable. |

## What it can do

**32 Editor command groups carrying 402 actions**, plus 9 offline groups that need no Editor at all.
Run `cucli commands list` for the exact surface of your build, and `cucli commands describe <group>`
for one group's parameter schema.

**Offline — works with Unity closed**

| Group | Purpose |
| --- | --- |
| `project` | Where the CLI thinks the project is, and which Unity version it needs. |
| `status` | Worker heartbeat, queue depths, catalog source. |
| `doctor` | Diagnose project, package, worker, Unity install and registry drift. `--deep` adds a full round trip. |
| `logs` | Tail `log.txt`, the CLI log or the captured console. |
| `commands` | List, describe and re-export the command catalog. |
| `job` | List, inspect, wait on, cancel and prune queued work. |
| `open` | Launch Unity for this project (prefers an existing Unity Hub instance). |
| `version` | Versions of CLI, package and PowerShell. |

**Editor — needs a live worker**

| Group | Covers |
| --- | --- |
| `editor` | State, Play Mode, menu items, undo/redo, tool, tags, layers, selection. |
| `refresh` | Asset refresh, script compilation (waits across the reload), reimport, wait-for-idle. |
| `scene` | Scene create/load/save/close, hierarchy dumps, build settings, screenshots. |
| `object` | GameObject and component find/create/modify/delete/duplicate, property get/set by reflection or SerializedProperty. |
| `prefab` | Prefab create/inspect/modify, prefab stages. |
| `asset` | Asset import/create/modify/delete/move/duplicate, dependency search, folders. |
| `script` | Read/write/validate C# files, precise text edits, content hashes. |
| `so` | ScriptableObject create/modify with `dryRun` validation. |
| `material`, `shader`, `texture` | Material properties, shader source, procedural textures and import settings. |
| `ui` | UI Toolkit documents, elements, inline styles and USS classes. |
| `graphics` | Environment, skybox, fog, reflection, sun, lightmap baking, quality levels, URP render-pipeline settings and volume profiles. |
| `camera` | Camera inspection and configuration; Cinemachine when installed. |
| `animation` | Animation clips, curves, AnimatorControllers, layers, parameters, presets. |
| `physics` | Settings, collision matrix, rigidbodies, forces, materials, joints, queries, stepping. |
| `probuilder` | ProBuilder mesh operations (when `com.unity.probuilder` is installed). |
| `vfx` | VFX Graph, particle systems, trails and lines (when `com.unity.visualeffectgraph` is installed). |
| `profiler` | Profiler sessions, sampling, counters, object memory, exports. |
| `console` | Read and clear captured console output with level, regex and time filters. |
| `test` | Run Unity Test Framework suites as an async job and poll for results. |
| `memory` | Asset-reference and object-memory audits. |
| `frame` | Frame inspection and multi-frame capture sequences. |
| `input` | Synthesise clicks, drags and key events into the Editor; inspect UI under the pointer. |
| `recorder` | Screenshots and recording sessions. |
| `capture` | Game view, Scene view and multiview screenshots. |
| `build` | Player builds as async jobs, build targets, scenes, settings. |
| `package` | Package Manager add/remove/embed/search, scoped registries. |
| `code` | Execute a C# snippet and batch several operations in one round trip. |
| `reflect` | Look up Unity types, members and signatures at runtime. |
| `meta` | Dump the runtime catalog, export the static registry, report the environment, scaffold project-local commands. |
| `selftest` | The tool testing itself: introspection assertions, an action safety surface, a sample report. |

## Installation

The repository root **is** the Unity package root, so it can be installed either way.

> **Check the target version first.** This package supports **Unity 2022.3**. If your project is on
> **Unity 6**, use [Unity CLI](https://docs.unity.com/en-us/unity-cli/unity-cli-reference) instead —
> see the *Compatibility* section at the top of this file.

**As a plugin folder (recommended — one directory, no manifest change):**

```powershell
git clone https://github.com/igp/custom-unity-cli.git Assets/Plugins/CustomUnityCli
```

**As a UPM package** (add to `Packages/manifest.json`):

```json
{
  "dependencies": {
    "com.igp.custom-unity-cli": "https://github.com/igp/custom-unity-cli.git"
  }
}
```

Then open Unity once. The Editor worker starts by itself through `[InitializeOnLoad]` and writes its
first heartbeat within a second. Confirm with:

```powershell
./Assets/Plugins/CustomUnityCli/Tools/cucli.ps1 doctor --deep
```

### Requirements

- **Unity 2022.3** — the supported and verified target. Unity 6 is out of scope for now; use
  [Unity CLI](https://docs.unity.com/en-us/unity-cli/unity-cli-reference) there. The 2021.3 LTS
  line is untested.
- `com.unity.nuget.newtonsoft-json` — installed automatically by the Package Manager when you
  install this as a git package. If you install it as a plain plugin folder instead, add that
  package yourself; it is the only dependency.
- PowerShell 5.1 or PowerShell 7+. Nothing else: no .NET SDK, no Node, no Python, no MCP client.
- Optional packages are detected at runtime. ProBuilder, VFX Graph, Cinemachine and the Test
  Framework are only needed for the commands that use them; everything else compiles and runs
  without them.

## Everyday usage

```powershell
# What is going on?
./cucli.ps1 status
./cucli.ps1 editor state

# Build something in a scene.
./cucli.ps1 object create --name Ball --primitive sphere --position 0,1,0
./cucli.ps1 object add-component --target Ball --component Rigidbody
./cucli.ps1 object set-property --target Ball --component Rigidbody --property mass --value 2.5

# Inspect without flooding your terminal.
./cucli.ps1 object inspect --target Ball --max-depth 2 --max-items 20
./cucli.ps1 scene get-hierarchy --page-size 50 --format json

# Force a recompile and wait for it, across the domain reload.
./cucli.ps1 refresh compile --wait --timeout 300

# Long operations become jobs you can watch.
./cucli.ps1 build build --target StandaloneWindows64 --detach
./cucli.ps1 job wait <jobId>

# Errors only, newest 20.
./cucli.ps1 console --level error --tail 20
```

### Output and exit codes

`--format human|json|ndjson`. Human output is a compact greppable summary; `json` returns the
`cucli.result/v1` envelope:

```json
{
  "schemaVersion": "cucli.result/v1",
  "success": true,
  "command": "object",
  "action": "create",
  "requestId": "…",
  "jobId": "…",
  "data": { "…": "…" },
  "errors": [],
  "warnings": [],
  "artifacts": [],
  "durationMs": 42,
  "exitCode": 0
}
```

When piping into another tool the format defaults to `json` automatically.

| Exit code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Unclassified host or command error |
| `2` | Usage error |
| `4` | Missing project, missing optional package, or confirmation required |
| `6` | Operation, validation or timeout failure |
| `7` | Editor or worker unavailable — retryable |
| `8` | Invalid input or unknown command/action |
| `130` | Cancelled |

Always check both the process exit code and `success`. For long operations keep the `jobId` so you
can query the result later.

## Design rules this tool follows

1. **No per-project configuration.** Aliases, offline groups and safety metadata ship in
   `commands/registry.json`; everything else is discovered from the compiled Editor. There is no
   settings dialog and no `.cuclirc` to write.
2. **Configure once, in static files.** `commands/registry.json` is regenerated from the compiled
   worker by `cucli commands export`, so the offline catalog can never drift silently —
   `cucli doctor` compares the two hashes and tells you when they differ.
3. **Bounded output by default.** Every listing is paged (`pageSize` ≤ 500, opaque `nextCursor`) and
   every object dump is depth- and item-limited. A raw `TextMeshProUGUI` or `Mesh` dump would emit
   megabytes; this tool refuses to.
4. **Optional means optional.** ProBuilder, VFX Graph, Cinemachine and the Test Framework are
   reached through runtime reflection, so the core assembly references nothing optional and cannot
   break a project that lacks them. A missing package produces `OPTIONAL_PACKAGE_MISSING` with the
   install hint, not a compile error.
5. **Writes behave like Editor writes.** Property writes go through `SerializedObject` first, so
   they register Undo, become prefab overrides and mark assets dirty. Reflection is the fallback.
6. **Destructive actions need consent.** Deletes, overwrites and collection clears require
   `--force` and otherwise fail with `CONFIRMATION_REQUIRED`.
7. **Failures are machine-readable.** Every failure carries a stable `errorCode`; the CLI's exit
   code is derived from it, so `if ($LASTEXITCODE -eq 7) { retry }` is safe.

## Extending it

Add a command group by writing one class with a parameterless constructor:

```csharp
namespace CustomUnityCli.Editor
{
    internal sealed class HelloCommand : CucliCommand
    {
        public override string Name => "hello";
        public override string Summary => "Say hello from the Editor.";
        public override CucliActionInfo[] Actions => Declare("say", "Return a greeting.");
        public override bool MutatesProject => false;

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string who = context.Input.GetString("who") ?? "world";
            return CucliCommandResult.Ok(new { greeting = "hello " + who });
        }
    }
}
```

The registry discovers it by reflection on the next domain reload, publishes it to
`worker/commands.json`, and `cucli hello say --who unity` works immediately. Nothing else to
register, no CLI change, no manifest edit.

See [`docs/extension.md`](docs/extension.md) for the full contract and
[`docs/protocol.md`](docs/protocol.md) for the file protocol.

### Growing the tool per project — without ever publishing it

A project always ends up wanting a few commands that only make sense there: an asset-naming check, a
content-validation pass, a build step, a convention the team keeps re-explaining. `cucli` is built to
grow that way, and built so the result **cannot end up in this repository**.

The extension area is a folder in the *project*, outside the package:

```text
<project>/Assets/CustomUnityCli.Extensions/
├── extensions.json           project manifest: aliases and offline groups
├── registry.local.json       generated: this project's own command catalog
├── README.md                 explains the area to whoever finds it
├── Notes/                    project invariants an agent can rediscover
└── Editor/                   MUST be called Editor, or Unity will not compile it
    └── Commands/             project command groups live here
```

`Editor/` is not cosmetic. Sources under a folder named `Editor` compile into the predefined
`Assembly-CSharp-Editor`, which already references this package's assembly — so no assembly
definition and no reference wiring are needed. The same file placed outside an `Editor` folder would
land in the runtime assembly, which cannot see `UnityEditor` at all.

Scaffold a group, let it compile, use it:

```powershell
./cucli.ps1 meta new-command --group assetpolicy --summary "本项目的资产命名与目录约定" --action check --action fix
./cucli.ps1 refresh compile --wait
./cucli.ps1 console --level error --tail 20
./cucli.ps1 assetpolicy check
```

Nothing is registered and no config file has to be written. `cucli extend path` prints the resolved
root; `cucli extend init` creates the layout with Unity closed; `$env:CUCLI_EXTENSIONS` overrides the
location, and a handful of alternative folder names are discovered if the first choice is absent.

**The repository boundary is enforced, not merely documented:**

| Mechanism | What it guarantees |
| --- | --- |
| Provenance tagging | Every command is tagged `source: package` or `source: project`, derived from the assembly it was compiled into. `cucli commands list` shows which is which. |
| `cucli commands export` | Writes **package commands only** into `commands/registry.json`. Project commands go to `registry.local.json` beside the extension area instead, where they belong to the project's own repository. |
| Two catalog hashes | `catalogHash` covers package commands, `fullCatalogHash` covers everything. Drift detection therefore keeps working in a project that has grown its own commands. |
| `cucli meta verify` | Validates the extension area, checks every command's action list against its declared JSON schema, and reports any source dropped **inside** the package tree — the one arrangement that would leak. |
| `cucli meta extensions` | Reconciliation report: which groups came from the package, which from the project, and whether the local registry is stale. |
| `cucli doctor` | Adds `extensions.placement`, `extensions.group-conflicts` and `package.checkout`, so a leak is caught by the routine health check rather than at review time. |
| No shadowing | A project extension may not reuse a shipped group name; the collision is refused and reported. Extend by adding (`buildchecks`, `assetrules`), never by redefining `object` or `asset`. |
| `cucli extend init\|list\|path` | Creates and inspects the extension area **with Unity closed**, so a project can start before the Editor has ever been opened. |

`package.checkout` runs `git status --porcelain` inside the package when it is a git checkout, so a
dirty package — someone editing the published artifact in place — is reported rather than discovered
at the next merge conflict.

The intended workflow: a project iterates on its own commands freely. When one turns out to be useful
everywhere, it gets upstreamed deliberately as a reviewed change — never as a side effect of running
a command. [`docs/self-iteration.md`](docs/self-iteration.md) covers the whole arrangement, including
the `Notes/` folder that lets the next iteration start from what the last one learned.

## Layout

```text
Assets/Plugins/CustomUnityCli/          the repository root is the package root
├── package.json                        UPM metadata
├── commands/registry.json              static command declaration (offline catalog + aliases)
├── Editor/
│   ├── CustomUnityCli.Editor.asmdef
│   ├── Core/                           protocol, job queue, console capture, reflection helpers
│   └── Commands/                       command groups, one file per group
├── Tools/cucli.ps1                     the entire CLI: parser, offline commands, job transport
└── docs/
```

Runtime state lives only in `Library/CustomUnityCli/`. Nothing is written back into the package, so
the folder stays read-only and upgradeable, and deleting `Library/CustomUnityCli/` is always safe.

## Documentation

- [`docs/protocol.md`](docs/protocol.md) — the file protocol in full: job lifecycle, domain reload
  contract, cancellation, atomic writes.
- [`docs/extension.md`](docs/extension.md) — writing a command group.
- [`docs/self-iteration.md`](docs/self-iteration.md) — how a project grows its own commands without
  ever publishing them.
- [`docs/mcp-parity.md`](docs/mcp-parity.md) — tool-by-tool mapping from MCP for Unity, and the
  deliberate differences.
- [`CHANGELOG.md`](CHANGELOG.md)

## Developing this package

Agent instructions for this repository live in
[`.agents/skills/custom-unity-cli/SKILL.md`](.agents/skills/custom-unity-cli/SKILL.md).

**Commit messages are English only.** Use Conventional Commits
(`type(scope): imperative summary`) with an English subject and body. Do not write
Chinese (or mixed-language) git messages in this repository.

```powershell
Tools/verify.ps1              # manifest, .meta coverage, command contract, offline Roslyn compile
Tools/verify.ps1 -WithUnity   # adds the authoritative batchmode import into a throwaway project

cucli selftest                # black-box self test: drives the CLI through the CLI
cucli selftest -Tier AB       # contract probes plus behaviour flows
cucli selftest -Group object,asset -Action object/create
```

Two different questions, two tools.

`verify.ps1` is a **build** gate. It compiles the Editor sources with Unity's own Roslyn against
Unity's own reference assemblies, so it catches real type errors in seconds with no Editor process,
no project lock and no domain reload. `-WithUnity` imports the package into a scratch project whose
manifest declares **no optional package** — which makes it proof of the "no optional package is
required" promise rather than an assumption. Neither mode starts, focuses or otherwise touches a
running Editor.

`cucli selftest` is a **behaviour** gate, and it is the answer to "does this tool actually work?".
It reaches into nothing: it reads the command surface from the live worker catalog and then talks to
the Editor exclusively through `Tools/cucli.ps1`, so a green run proves the entire chain —
PowerShell parser → job file → Editor worker → command → result file → envelope → exit code.

| Tier | What it does |
| --- | --- |
| A | Contract probe. Invokes every read-only action with no arguments. A correctly wired command answers with a real payload or a validation error; an unknown command, a dispatch mismatch or an unhandled exception is a defect. This is what catches registry drift, schema gaps and mode-gate bugs across the whole surface. |
| B | Behaviour. Creates a sandbox under `Assets/CucliSelfTest`, exercises real flows with real payloads, then removes it. |
| heavy | Opt-in with `-IncludeHeavy`: compilation, package resolve, test runs. Never a real player build, light baking or a manifest write. |

The report lands in `Library/CustomUnityCli/selftest-report.md`; `-KeepSandbox` leaves the sandbox
behind for inspection. Every declared action belongs to exactly one tier, so "all of it" stays a
meaningful statement as the surface grows.

### `cucli` never steals focus

This is a design rule, not an accident. Unity defers its `AssetDatabase` refresh until the Editor
window regains focus, which makes "just bring the window to the front" a tempting shortcut. `cucli`
does not do it, and does not require it:

- Before the worker has ever been compiled, no out-of-process tool can make the Editor recompile.
  Use `verify.ps1` above; the Editor will pick the change up whenever it is next focused, and nothing
  about your session is disturbed in the meantime.
- Once the worker is compiled, it polls its own job queue from `EditorApplication.update` and calls
  `RequestScriptCompilation()` itself, so `cucli refresh compile --wait` works with Unity in the
  background.
- `cucli open` starts Unity, but only because you asked it to.

## License

[MIT](LICENSE).
