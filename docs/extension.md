# Writing a command group

A command group is one public class with a public parameterless constructor. The registry finds it
by reflection on the next domain reload, publishes it to `worker/commands.json`, and
`cucli <group> <action>` works immediately. There is no registration call, no manifest edit and no
change to `cucli.ps1`.

## Two places a group can live

| Where | Belongs to | Published with the package? |
| --- | --- | --- |
| `Editor/Commands/**` inside this package | everyone using the tool | yes — it is reviewed and versioned with the package |
| `<project>/Assets/CustomUnityCliExtensions/Commands/**` | the host project only | **no** |

Both are discovered identically. The difference is ownership, and it matters: the package is a
published artifact that is replaced wholesale on upgrade, so project-specific commands placed inside
it would be destroyed on the next pull — and would leak into the public repository on the way.

### The project-local extension area

```text
<project>/Assets/CustomUnityCliExtensions/     default; outside the package
├── cucli.extension.json                       manifest (schemaVersion cucli.extension/v1)
├── README.md                                  explains the area to whoever finds it
├── Commands/                                  project command groups
├── Notes/                                     project invariants worth rediscovering
└── registry.local.json                        generated; project commands for offline listing
```

```powershell
./cucli.ps1 extend init                                  # create the area; Unity may be closed
./cucli.ps1 meta new-command --group assetpolicy --summary "本项目的资产命名约定" --action check
./cucli.ps1 refresh compile --wait
./cucli.ps1 assetpolicy check
```

Set `$env:CUCLI_EXTENSIONS` to relocate the area (absolute, or project-relative). The location is an
environment variable rather than a settings file because it is a machine-or-team choice, not
per-command configuration.

**No assembly definition is needed.** Any `Editor/` folder under `Assets/` compiles into
`Assembly-CSharp-Editor`, which automatically references every assembly definition marked
`autoReferenced` — and `CustomUnityCli.Editor` is one. So a plain `.cs` file is the whole ceremony.

### How the package boundary is enforced

Four independent mechanisms, because "please do not commit that" is not an enforcement strategy:

1. **Provenance tagging.** `CucliCommandRegistry` records whether each command came from the package
   assembly or from the project, and exposes it as `source: package` / `source: project` on every
   descriptor.
2. **Split export.** `cucli commands export` writes `commands/registry.json` with **package commands
   only** and routes project commands to `registry.local.json` inside the extension area. A project
   command therefore cannot be published by running a refresh.
3. **Dual hashes.** `catalogHash` covers package commands and is what the shipped registry records,
   so drift detection keeps working in a project that has grown its own commands.
   `fullCatalogHash` covers everything and is reported for diagnostics.
4. **A leak check.** `cucli meta extensions` reports the extension root, the source files it found,
   and any file sitting **inside** the package tree — the one arrangement that would publish project
   code. The package's own `.gitignore` carries matching safety-net ignores.

`cucli commands list` shows which commands are project-local, and `cucli doctor` reports registry
drift plus the worker's command counts per source.

### Shadowing a package group

A project command may register the **same name** as a package group. The project wins, and the
registry reports it as a `COMMAND_SHADOWED` diagnostic rather than applying it silently:

```text
diagnostics[]: { code: "COMMAND_SHADOWED", commandName: "build", message: "Project command overrides the package command registered by CustomUnityCli.Editor.BuildCommand." }
```

This is the supported way to adapt the tool's behaviour for one project without forking it. Two
projects doing this to the same group usually means the behaviour belongs upstream.

### Upstreaming

Project commands are not second-class: the same contract, the same helpers, the same tests. When one
turns out to be generally useful, move the file into `Editor/Commands/<Domain>/` and run
`cucli commands export` — as a reviewed change, not as a side effect of running a command.

## The contract

```csharp
public interface ICucliCommand
{
    string Name { get; }                        // group name, lower case, globally unique
    string Summary { get; }                     // one line, shown by `cucli commands list`
    CucliActionInfo[] Actions { get; }           // the sub-actions this group accepts
    string ParametersSchemaJson { get; }         // JSON Schema for the payload
    bool RequiresEditor { get; }
    CucliMode RequiredMode { get; }              // Any | Edit | Play | Offline
    bool MutatesProject { get; }
    string[] Effects { get; }                    // auditable side-effect classes
    bool MayReloadDomain { get; }
    string OptionalPackage { get; }              // package name, or null
    CucliCommandResult Execute(CucliCommandContext context);
}
```

`CucliCommand` is an abstract base that supplies sane defaults, so you normally override `Name`,
`Summary`, `Actions`, `ParametersSchemaJson` and `Execute`, and only override the rest when they
differ.

### One class per group, not per action

`cucli object create` maps onto the `object` command with `{ "action": "create", … }`. Keeping the
compiled surface at group granularity means a few dozen auditable classes cover hundreds of
operations, and adding an action is a `case` rather than a new type. The action list is data
(`Actions`), so the CLI, the catalog and the help text all stay in sync automatically.

## A complete example

```csharp
using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    internal sealed class LightingCommand : CucliCommand
    {
        public override string Name => "lighting";

        public override string Summary =>
            "Inspect and configure scene lighting: ambient, fog and light probes.";

        public override CucliActionInfo[] Actions => Declare(
            "get", "Report the current scene lighting settings.",
            "set-ambient", "Set ambient mode and colour.",
            "probe-list", "List light probes in the loaded scenes.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Edit;

        public override bool MutatesProject => true;

        public override string[] Effects => new[] { "editor.scene.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""get"", ""set-ambient"", ""probe-list""], ""default"": ""get"" },
    ""mode"": { ""type"": ""string"", ""enum"": [""Skybox"", ""Trilight"", ""Flat"", ""Custom""] },
    ""color"": { ""type"": ""string"", ""description"": ""#RRGGBB or #RRGGBBAA"" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "get"))
            {
                case "get": return Get();
                case "set-ambient": return SetAmbient(context);
                case "probe-list": return ListProbes(context);
                default: return CucliCommandResult.Unsupported(ResolveAction(context, "get"), ActionNames());
            }
        }

        private static CucliCommandResult Get()
        {
            return CucliCommandResult.Ok(new
            {
                ambientMode = RenderSettings.ambientMode.ToString(),
                ambientLight = CucliDescribe.Value(RenderSettings.ambientLight, null, 0),
                fog = RenderSettings.fog,
                fogColor = CucliDescribe.Value(RenderSettings.fogColor, null, 0),
                fogDensity = RenderSettings.fogDensity
            });
        }

        private static CucliCommandResult SetAmbient(CucliCommandContext context)
        {
            UnityEngine.Rendering.AmbientMode mode =
                context.Input.GetEnum("mode", RenderSettings.ambientMode);
            Color color = context.Input.GetColor("color", RenderSettings.ambientLight);

            Undo.RecordObject(null, "cucli lighting set-ambient");
            RenderSettings.ambientMode = mode;
            RenderSettings.ambientLight = color;

            return CucliCommandResult.Ok(new
            {
                ambientMode = RenderSettings.ambientMode.ToString(),
                ambientLight = CucliDescribe.Value(RenderSettings.ambientLight, null, 0)
            }, "环境光已更新。");
        }

        private static CucliCommandResult ListProbes(CucliCommandContext context)
        {
            var probes = new JArray();
            foreach (LightProbeGroup group in CucliAssets.FindAll<LightProbeGroup>())
            {
                if (group == null) continue;
                probes.Add(new JObject
                {
                    ["name"] = group.name,
                    ["path"] = CucliObjectResolver.HierarchyPath(group.gameObject, true),
                    ["probeCount"] = group.probePositions == null ? 0 : group.probePositions.Length
                });
            }

            return CucliCommandResult.Ok(new { count = probes.Count, groups = probes });
        }
    }
}
```

Then:

```powershell
./cucli.ps1 lighting get
./cucli.ps1 lighting set-ambient --mode Flat --color '#404040'
./cucli.ps1 lighting probe-list --format json
./cucli.ps1 commands describe lighting
```

## Helpers you should use instead of writing your own

### Reading parameters — `CucliArgs`

`context.Input` returns a `CucliArgs`. The CLI normalises `--page-size 20` and `--page_size 20` to
the same payload key, so **read camelCase in C#** and additionally try snake_case because a caller
may hand you a raw JSON payload:

```csharp
string path = context.Input.GetString("assetPath") ?? context.Input.GetString("asset_path");
```

Available: `Has`, `Token`, `GetString`, `RequireString`, `GetBool`, `GetInt`, `GetLong`, `GetFloat`,
`GetDouble`, `GetStringList`, `GetIntList`, `GetVector2/3/4`, `GetRotation`, `GetColor`, `GetEnum<T>`,
`GetObject`, `GetArray`, `GetObjectOrFile` (resolves `@payload.json`).

`GetVector3` accepts `[1,2,3]`, `{"x":1,"y":2,"z":3}` and `"1,2,3"`. `GetColor` accepts
`"#RRGGBB"`, `"#RRGGBBAA"`, `"RRGGBB"`, `[r,g,b,a]` and `{"r":…}`. Forgiving readers here remove the
single most common class of caller mistake.

### Finding objects — `CucliObjectResolver`

Never hand-roll a name lookup. The resolver handles instance ids, hierarchy paths, names, tags,
layers and component types, and it searches **inactive** objects too, because "it is not in my
hierarchy" is the most common false negative when driving Unity from outside.

```csharp
string error;
GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
if (error != null) return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);

GameObject required = CucliObjectResolver.RequireGameObject(context.Input);   // throws CucliUsageException
Transform transform = CucliObjectResolver.RequireTransform(context.Input);
Component component = CucliObjectResolver.RequireComponent(context.Input, "Rigidbody");
```

### Writing properties — `CucliSerialized` first, then reflection

`SerializedObject` gives you Undo, prefab overrides and dirty marking. Use it first; fall back to
reflection only when the member is not serialised.

```csharp
string error;
bool handled;
if (CucliSerialized.TrySet(target, memberPath, token, "cucli set " + memberPath, out error, out handled))
{
    // done - Undo, ApplyModifiedProperties and SetDirty already happened
}
else if (handled)
{
    return CucliCommandResult.Invalid("value", error);      // serialised, but the value was rejected
}
else
{
    if (!CucliReflection.TrySetPath(target, memberPath, token, out error))
        return CucliCommandResult.Invalid("value", error);

    EditorUtility.SetDirty(target);                          // reflection does not mark dirty for you
}
```

`CucliReflection` also gives you `FindType`, `FindMember`, `Members`, `Get`, `GetPath`,
`TrySetMember`, `TrySetPath` and `TryInvoke`.

### Bounding output — `CucliDescribe`

This is not optional. A raw dump of a `TextMeshProUGUI` carries `textInfo`, character info, mesh
vertices, UVs and material instances; a `Mesh` is worse. `CucliDescribe` reduces any
`UnityEngine.Object` to `{name, type, assetPath, guid, instanceId}` and truncates collections with an
explicit `count` / `$truncated` marker.

```csharp
CucliDescribe.Options options = CucliDescribe.OptionsFrom(context.Input);   // maxItems/maxDepth/maxStringLength
JToken value = CucliDescribe.Value(anything, options, 0);
JArray components = CucliDescribe.Components(gameObject, options, 0);
```

Expose `maxItems`, `maxDepth` and `maxStringLength` on any action that returns a structure, and
default them low.

### Paging — `CucliPaging`

Any action that can return more than a handful of rows must page.

```csharp
CucliPage<string> page = CucliPaging.Slice(paths, context.Input);
var items = new JArray();
foreach (string path in page.Items) items.Add(path);
return CucliCommandResult.Ok(CucliPaging.Envelope(page, items, new JObject { ["root"] = root }));
```

### Optional packages — `CucliOptional`

Reach ProBuilder, VFX Graph, Cinemachine, the Test Framework and URP through runtime reflection.
**Do not** add an assembly reference: the core assembly must keep compiling in a project that has
none of them.

```csharp
public override string OptionalPackage => "com.unity.probuilder";

public override CucliCommandResult Execute(CucliCommandContext context)
{
    CucliCommandResult missing = CucliOptional.RequirePackage("com.unity.probuilder", "ProBuilder 网格编辑");
    if (missing != null) return missing;

    Type mesh = CucliOptional.FindType("Unity.ProBuilder", "UnityEngine.ProBuilder.ProBuilderMesh");
    object result;
    string error;
    if (!CucliOptional.TryInvokeStatic(mesh, "Create", new object[] { gameObject }, out result, out error))
    {
        return CucliCommandResult.Fail(CucliErrors.CommandFailed,
            "ProBuilder 调用失败：" + error);
    }

    return CucliCommandResult.Ok(new { created = true });
}
```

Setting `OptionalPackage` also lets the CLI refuse the command before it is queued, with a clear
install hint, in a project that does not list the package.

### Errors — `CucliErrors`

Return a stable code so callers can branch without parsing prose:

```csharp
return CucliCommandResult.Missing("assetPath", "Example: --asset-path Assets/Foo.prefab");
return CucliCommandResult.Invalid("pageSize", "must be between 1 and 500");
return CucliCommandResult.Fail(CucliErrors.AssetNotFound, "没有这个资产：" + path, new { path });
return CucliCommandResult.Unsupported(action, ActionNames());
```

Use `TARGET_NOT_FOUND`, `TARGET_AMBIGUOUS`, `ASSET_NOT_FOUND`, `ASSET_EXISTS`, `MEMBER_NOT_FOUND`,
`TYPE_NOT_FOUND`, `WRONG_MODE`, `OPTIONAL_PACKAGE_MISSING`, `CONFIRMATION_REQUIRED`,
`COMPILATION_IN_PROGRESS`, `TIMEOUT` and `CANCELLED` where they apply — the CLI derives its exit
code from them.

## Deferred and reload-capable commands

Implement `ICucliDeferredCommand` when the work spans Editor frames: screenshots, capture sequences,
simulation, builds, waiting for a compile.

```csharp
internal sealed class SlowCommand : CucliCommand, ICucliDeferredCommand
{
    public override bool MayReloadDomain => true;      // park in waiting/ before running

    public override CucliCommandResult Execute(CucliCommandContext context) =>
        CucliCommandResult.Fail(CucliErrors.CommandFailed, "must be dispatched through the deferred path");

    public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
    {
        bool completed = false;
        EditorApplication.CallbackFunction tick = null;
        tick = () =>
        {
            context.ThrowIfCancelled();
            if (!Ready()) return;                       // keep polling
            if (completed) return;
            completed = true;
            EditorApplication.update -= tick;
            complete(CucliCommandResult.Ok(new { done = true }));
        };
        EditorApplication.update += tick;
    }
}
```

Rules the worker relies on:

- Call `complete` **exactly once**. The worker ignores later calls, but a double completion is a bug.
- On success, a `MayReloadDomain` command's real result is written to a sidecar and published after
  the reload. Do not try to publish it yourself.
- Throw `OperationCanceledException`, or return a `CANCELLED` failure, when
  `context.Cancellation` is signalled. Do not leave gesture state half-applied: send the release
  events needed to clean up.
- Measure timeouts with `EditorApplication.timeSinceStartup`, not the wall clock: the Editor's own
  clock is what advances the update loop, so a modal dialog cannot masquerade as progress.

## Conventions

- One file per group, named `<Group>Command.cs`. Split by sub-domain (`AnimationClipCommand`,
  `AnimationControllerCommand`) when a group grows past a few hundred lines.
- `internal sealed class`, name ending in `Command`, no explicit constructor.
- `Actions` must match the `switch` exactly, the default action must be the first declared action,
  and every action must appear in `ParametersSchemaJson`'s `enum`.
- `message` is prose for a human and may be Chinese; machine identifiers — action names, parameter
  names, type names, asset paths, error codes — stay ASCII English.
- Report non-fatal problems with `context.Warn(...)`, never `Debug.Log`.
- Register produced file paths with `context.AddArtifact(absolutePath)` so callers can find them.
- Destructive actions take a `force` flag and fail with `CONFIRMATION_REQUIRED` when it is absent.

## Regenerating the static registry

```powershell
./cucli.ps1 commands export
```

This reads `worker/commands.json` and rewrites `commands/registry.json`, preserving the
hand-maintained `aliases`, `offline`, `controlActions` and `description` sections. Run it after
adding or renaming a command, then `./cucli.ps1 doctor` should report no `registry.drift`.
