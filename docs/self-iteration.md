# Self-iteration: how a project grows its own commands

`custom-unity-cli` is deliberately generic. Every real project, however, eventually needs a command
that only makes sense for *that* project: a build-time content check, an asset naming convention, a
data-export step, a scene validator. This document is how those get written, where they live, and how
the tool guarantees they never end up in the published repository.

## The rule in one sentence

**The package contains no project-specific content, ever. A project's own commands live in the
project's tree, under its own version control, and the tool reports it as an error if that line is
crossed.**

## Why this is a hard boundary and not a convention

The package and the extension area have opposite lifecycles:

| | Package | Extension area |
| --- | --- | --- |
| Replaced on upgrade | yes, wholesale | never |
| Published | yes, publicly | never |
| Owned by | upstream `igp/custom-unity-cli` | the host project |
| Versioned by | git | the project's own VCS (Plastic, git, whatever) |

Anything project-specific written *into* the package is therefore on a path to being published, and
anything generic written into the extension area can never be reused. Both mistakes are easy to make
by accident, so the tool detects them instead of relying on discipline:

- Every command carries its **provenance** (`package` or `project`), recorded in the runtime catalog.
- `cucli commands export` writes **package commands only** into the shipped `commands/registry.json`.
  A project command literally cannot reach that file.
- `cucli meta verify` reports extension sources that sit inside the package tree.
- `cucli doctor` checks `extensions.placement`, `extensions.group-conflicts` and `package.checkout`.
- A project extension may **not** reuse a shipped group name. See [No shadowing](#no-shadowing).

## Where project code lives

The extension area is a fixed, discoverable location — not a setting, because a setting is exactly
the mental overhead this tool exists to remove.

```text
<project>/Assets/CustomUnityCli.Extensions/         <- preferred, created on demand
├── README.md                                      explaining the boundary to the next reader
├── extensions.json                                project aliases and offline command groups
├── registry.local.json                            generated: the project's own command catalog
├── Notes/                                         free-form invariants an agent can rediscover
└── Editor/                                        MUST be called Editor (see below)
    └── Commands/                                  generated command groups
        └── BuildChecksCommand.cs
```

`cucli extend path` prints the resolved root. `cucli extend init` creates the layout above, including
the manifest and README.

### Choosing the location

The root is resolved in this order, and the first existing candidate wins so a project that already
put extensions somewhere is never silently given a second folder:

1. `$env:CUCLI_EXTENSIONS` — absolute, or relative to the project root.
2. `Assets/CustomUnityCli.Extensions` (preferred default)
3. `Assets/CustomUnityCliExtensions`
4. `Assets/Plugins/CustomUnityCli.Extensions`
5. `Assets/Editor/CustomUnityCli.Extensions`
6. `Packages/CustomUnityCli.Extensions`

The root **must** be inside `Assets/` or `Packages/` or Unity will never compile it; `cucli extend
init` refuses and explains rather than writing dead files.

### Why `Editor/` is mandatory and why no asmdef is needed

Scripts under any folder named `Editor` inside `Assets` compile into the predefined
`Assembly-CSharp-Editor`. That assembly automatically references every assembly definition with
`autoReferenced: true` — which includes the package's own `CustomUnityCli.Editor`. So dropping a file
into `Editor/Commands/` is the entire ceremony:

- no assembly definition to write,
- no reference to wire up,
- no GUID to chase when the package is reinstalled at a different path.

Conversely, a command source placed *outside* an `Editor` folder compiles into the runtime
`Assembly-CSharp`, which cannot see `UnityEditor` or the Editor-only package assembly at all. That is
why `Commands/` is nested under `Editor/` rather than sitting at the root.

Internal classes are fine. The registry constructs command types by reflection and does not require
them to be public.

## The iteration loop

```powershell
# 1. Scaffold a group. This writes a compiling file, not a stub.
./cucli.ps1 meta new-command --group buildchecks --summary "本项目的构建前检查" `
  --action check --action report

# 2. Fill in the generated file, then make Unity compile it and wait across the domain reload.
./cucli.ps1 refresh compile --wait --timeout 300

# 3. Read only the errors.
./cucli.ps1 console --level error --tail 20

# 4. Confirm the group registered, and that the package is still clean.
./cucli.ps1 commands describe buildchecks
./cucli.ps1 meta verify
./cucli.ps1 extend list

# 5. Use it.
./cucli.ps1 buildchecks check --strict
```

`cucli meta verify` is the self-check: it validates the extension area, checks every command's action
list against its declared JSON schema, and reports sources that were placed inside the package.
`cucli meta extensions` is the reconciliation report — which groups came from the package, which from
the project, and whether `registry.local.json` is stale.

## What a project command looks like

Identical to a package command, because it *is* one:

```csharp
using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Extensions
{
    internal sealed class BuildChecksCommand : CustomUnityCli.Editor.CucliCommand
    {
        public override string Name => "buildchecks";
        public override string Summary => "本项目的构建前检查。";
        public override CucliActionInfo[] Actions => Declare(
            "check", "运行全部构建前检查。",
            "report", "输出上次检查的报告。");
        public override bool MutatesProject => false;

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "check"))
            {
                case "check":  return Check(context);
                case "report": return Report(context);
                default:       return CucliCommandResult.Unsupported(ResolveAction(context, "check"), ActionNames());
            }
        }
        // ...
    }
}
```

The scaffolder writes a file with the correct namespace, metadata, schema and dispatch skeleton, so
the usual failure modes — a missing `action` in the schema enum, an `Actions` list that disagrees
with the `switch` — cannot happen by accident.

See [`extension.md`](extension.md) for the full API, and note that the helper surface
(`CucliAssets`, `CucliDescribe`, `CucliReflection`, `CucliSerialized`, `CucliObjectResolver`,
`CucliPaging`, `CucliOptional`, `CucliValues`, `CucliErrors`, …) is public precisely so project code
can use it.

## `Notes/`: durable project knowledge

`Notes/` exists so an agent iterating on this project can rediscover its invariants without being
retold: naming conventions, which assets are generated, ordering constraints, known-bad states,
decisions and their reasons.

It is plain files, read by a human or an agent, never executed. The point is that the next iteration
— by a different session, a different model or a different person — starts from what was already
learned rather than from scratch.

## No shadowing

A project extension may not take a group name that the package already ships. The registry refuses
the collision with `COMMAND_DUPLICATE`, and the CLI reports it as `extensions.group-conflicts`.

This is a deliberate constraint with three reasons:

1. **The same command must mean the same thing everywhere.** If `cucli object create` behaved
   differently per project, the tool would stop being something you can learn once.
2. **The published catalog hash would break.** `commands/registry.json` records a hash of the
   *package* command surface; a shadowed group would silently leave that set and make
   `registry.drift` fire permanently.
3. **It hides the real intent.** A project that wants different behaviour either wants a *new*
   command (name it that way) or has found a bug in the package (report it), and shadowing makes
   both harder to see.

Extend by adding, never by redefining: `buildchecks`, `assetrules`, `contentexport` — not
`object` or `asset`.

## Promotion: when a project command becomes generic

A command that turns out to be useful in more than one project should move upstream — deliberately,
as a reviewed change, never as a side effect:

1. Confirm it has no project-specific assumptions (no hard-coded paths, GUIDs, scene names or
   product names).
2. Add it to the package's `Editor/Commands/<Domain>/`, with `CucliCommand` metadata filled in
   properly (`Effects`, `RequiredMode`, `MutatesProject`).
3. `cucli commands export` to refresh the shipped registry, and `cucli doctor` to confirm no drift.
4. Delete it from the project's extension area in the same change, so there is exactly one copy.

## Verifying the boundary

| Command | What it proves |
| --- | --- |
| `cucli extend path` | Where the extension area is, and whether it exists. |
| `cucli extend list` | Which groups are project-local, with their files and action counts. |
| `cucli meta extensions` | Full reconciliation: package vs project groups, manifest, local registry staleness. |
| `cucli meta verify` | Extension area valid, schemas consistent, no sources inside the package. |
| `cucli doctor` | `extensions.placement`, `extensions.group-conflicts`, `package.checkout`, registry drift. |
| `cucli commands export` | Regenerates the shipped registry from **package commands only**. |

`package.checkout` runs `git status --porcelain` inside the package when it is a git checkout, so a
dirty package — someone editing the published artifact in place — is reported rather than discovered
at the next conflict.

## Version-controlling the extension area

The extension area belongs to the **host project's** version control. In a project using Plastic SCM
that means checking it in normally; in a project using git it means committing it to *that* project's
repository.

If a project wants the generated code excluded from its own history — some teams treat it as
build output — add the folder to that project's ignore rules. The tool does not require either
choice; it only requires that the code is **not inside the package**, which is what `doctor` and
`meta verify` enforce.

Never copy the extension area into a fork of `custom-unity-cli`. That is the one mistake the checks
exist to catch.
