# Migrating from MCP for Unity

`cucli` covers the capability surface of the 30 `McpForUnityTool` commands in
[CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp) (verified against **9.7.3**), plus the
Editor-level facilities that an MCP-only setup leaves out: Play Mode control that cannot deadlock,
job cancellation, captured console history, compile waiting across a domain reload, offline
diagnosis, and a stable exit-code contract.

The two can coexist in one project. `cucli` does not reference, wrap or depend on `unity-mcp`; it is
a parallel implementation, not a shim.

## Why the shape differs

MCP tools are named `manage_<domain>` with an `action` parameter, and every tool is a separate
entry in the client's tool list. `cucli` uses the same **group + action** idea, but expresses it in
the CLI's own grammar:

```
MCP:   manage_gameobject  { "action": "create", "name": "Ball" }
cucli: cucli object create --name Ball
```

Two consequences worth knowing:

- **One compiled class per group**, not per action. Adding an action is a `case` in an existing
  file, so a few dozen auditable classes cover several hundred operations.
- **`action` is required in MCP and optional in `cucli`.** Every `cucli` group declares a default
  action (the first one), so `cucli editor state` and `cucli status` need no action at all.

## Tool-by-tool mapping

| MCP tool | MCP actions | `cucli` group | Notes |
| --- | --- | --- | --- |
| `manage_scene` | 13 | `scene` | Hierarchy dumps are paged with `pageSize` / `nextCursor` instead of a `maxNodes` cap alone. |
| `manage_gameobject` | 6 | `object` | `delete` still removes every name match and reports the count. |
| `manage_components` | 3 | `object` | `add` / `remove` / `set-property` / `get-property`, plus `inspect`. |
| `find_gameobjects` | — | `object find` | All seven `searchMethod` values, plus tag, layer and component addressing. Paged. |
| `manage_prefabs` | 7 | `prefab` | Prefab stages use `PrefabUtility` load/save/unload transactions. |
| `manage_editor` | 12 | `editor` | `play` / `pause` / `stop` / `menu` / `undo` / `redo` / `tool` / `tag-*` / `layer-*` / `selection-*`. |
| `refresh_unity` | — | `refresh` | `assets` / `compile` / `reimport` / `wait` / `status`; `compile --wait` survives the reload. |
| `manage_asset` | 11 | `asset` | `import` / `create` / `modify` / `delete` / `duplicate` / `move` / `rename` / `search` / `get_info` / `create_folder` / `get_components`. |
| `manage_script` | 8 | `script` | `read` supports line ranges; `apply_text_edits` is 1-based and rejects overlaps with `stale_file` when a `preconditionSha256` is supplied. |
| `manage_scriptable_object` | 2 | `so` | `create` / `modify`, with `dryRun` validating on a throwaway clone and writing nothing. |
| `manage_material` | 7 | `material` | `ping` / `create` / `set_material_shader_property` / `set_material_color` / `assign_material_to_renderer` / `set_renderer_color` / `get_material_info`. |
| `manage_shader` | 4 | `shader` | `create` emits a self-contained URP or Built-in template with no package reference. |
| `manage_texture` | 8 | `texture` | `create` / `create_sprite` / `modify` / `delete` / `apply_pattern` / `apply_gradient` / `apply_noise` / `set_import_settings`. |
| `manage_packages` | 12 | `package` | Async add/remove/embed polled through `status`; job state survives domain reloads. |
| `manage_build` | 8 | `build` | `build` / `batch` return a `jobId` and are polled; job state is persisted, not in-memory. |
| `manage_ui` | 14 | `ui` | UI Toolkit (`UXML` / `USS` / `UIDocument`), matching MCP. There is no uGUI/RectTransform surface in either tool. |
| `manage_graphics` | 40 | `graphics` | Environment, skybox, ambient, fog, reflection, sun, lightmapping, quality, URP settings, volume profiles. |
| `manage_camera` | 18 | `camera` | Core camera actions need nothing extra; Cinemachine actions are reflection-guarded. |
| `manage_animation` | 28 | `animation` | Clip, controller, animator and preset sub-families. |
| `manage_physics` | 21 | `physics` | Settings, collision matrix, rigidbody, force, material, raycast, query, simulate, joints, validation. |
| `manage_probuilder` | 36 | `probuilder` | Requires `com.unity.probuilder`; reached by reflection so the core assembly never references it. |
| `manage_vfx` | 63 | `vfx` | Graph, particle, trail and line sub-families. Requires `com.unity.visualeffectgraph`. |
| `manage_profiler` | 14 | `profiler` | Sessions, sampling, counters, object memory, exports. Counter dumps are paged. |
| `read_console` | 2 | `console` | `get` / `clear`, with level, regex and time filters and cursor paging. |
| `run_tests` | — | `test run` | Returns a `jobId`; one suite at a time, with `retryAfterMs` on conflict. |
| `get_test_job` | — | `test status` | `failuresSoFar` is capped at 25; full detail is opt-in and paged. |
| `execute_code` | 4 | `code run` | C# snippet execution with the same `public static object Execute()` body contract. |
| `execute_menu_item` | — | `editor menu` | `--menu-path "GameObject/Create Empty"`. |
| `batch_execute` | — | `code batch` | Several operations in one round trip, with `failFast`. |
| `unity_reflect` | 3 | `reflect` | `get-type` / `get-member` / `search`. |

## What `cucli` adds on top

These have no MCP equivalent and come from the Editor-worker side of the design:

| Group | What it gives you |
| --- | --- |
| `job` | Inspect, wait on, cancel and prune queued work — including work that is queued while Unity is busy. |
| `frame` | Frame inspection and timed multi-frame capture sequences. |
| `input` | Synthesised clicks, drags and key events, and UI element inspection under the pointer. |
| `recorder` | Screenshot and recording sessions. |
| `capture` | Game view, Scene view and multiview screenshots with `artifacts[]` reporting. |
| `memory` | Asset-reference and object-memory audits. |
| `test` | Full async job control rather than a poll loop you have to write. |
| `doctor` | Offline project, package, worker, Unity-install and registry-drift diagnosis. |
| `logs` | Tail `log.txt`, the CLI log or the captured console without starting Unity. |
| `open` | Launch Unity for the project, optionally waiting for the worker. |
| `commands` | Self-describing surface with safety metadata and parameter schemas. |

## Deliberate differences

**Grouping is by domain, not by a fixed verb.** MCP's `manage_*` prefix means `manage_asset`,
`manage_material` and `manage_texture` are three tools; in `cucli` they are three groups with
parallel actions (`create`, `modify`, `delete`, `get-info`), which is easier to remember and easier
to script.

**Safety metadata is per group and published.** Every group declares `requiresEditor`,
`requiredMode`, `mutatesProject`, `effects[]`, `mayReloadDomain` and `optionalPackage`, and all of it
is visible in `cucli commands list` and `cucli commands describe`. Callers branch on the declared
metadata instead of inferring safety from a tool name.

**Destructive actions are opt-in.** Deletes, overwrites and clears require `--force` and otherwise
fail with `CONFIRMATION_REQUIRED` (exit code 4). MCP relies on its own confirmation layer.

**Output is bounded by default.** Every listing is paged and every object dump is depth- and
item-limited. `manage_vfx`'s `get_counters` and `line_get_info` have no truncation in MCP; in `cucli`
the equivalents page.

**Optional packages never break the build.** MCP guards ProBuilder and VFX Graph with runtime
reflection and `#if UNITY_VFX_GRAPH` respectively. `cucli` uses runtime reflection uniformly, so the
core assembly has no optional reference at all and every optional command degrades to
`OPTIONAL_PACKAGE_MISSING` with an install hint.

**No `_mcp_poll_interval`.** MCP-returned jobs embed a polling hint in the response. `cucli` exposes
polling as first-class commands (`cucli job wait <id>`, `cucli job status <id>`), so a shell script
can block on it, and the CLI already blocks for you unless you pass `--detach`.

## Known gaps

- `asset create` does not create prefabs or arbitrary Unity types; it covers folders, materials,
  physics materials, ScriptableObjects, TextAssets, animation clips, `Texture2D`, `RenderTexture`,
  scenes and JSON. Everything else returns `INVALID_PARAMETER` with the supported list.
- `script validate` performs structural checks (comment- and string-aware bracket balance, using and
  namespace ordering, naming hints). It is not a compile. Roslyn-based validation would need the
  editor to ship Roslyn, which the package deliberately does not require.
- `graphics` cannot set the active `RenderPipelineAsset` — neither can MCP. It reads
  `GraphicsSettings.currentRenderPipeline` and toggles quality levels.
- `material set-renderer-color` resolves its target through the general resolver, which is more
  permissive than MCP's name-only default. Pass an explicit hierarchy path or instance id when a
  scene contains duplicate names.
- MCP's per-tool group visibility (some tools start hidden and need `manage_tools` activation) has no
  equivalent: every `cucli` group is always present, and the catalog states what each one needs.
- **`profiler` canonicalises 13 of MCP's action names, and accepts both spellings.**
  `manage_profiler`'s actions carry a redundant `profiler_` / `memory_` / `frame_debugger_` prefix
  that the group name already provides, so the canonical `cucli` name drops it and uses kebab-case.
  Every MCP spelling below is registered as an **alias**, not a rejection, so a caller ported
  straight from MCP keeps working; only the error message and `--help` show the canonical name:

  | MCP | `cucli` canonical |
  | --- | --- |
  | `profiler_start` / `profiler_stop` / `profiler_status` | `start` / `stop` / `status` |
  | `profiler_set_areas` | `set-areas` |
  | `get_frame_timing` / `get_counters` / `get_object_memory` | `frame-timing` / `counters` / `object-memory` |
  | `memory_take_snapshot` / `memory_list_snapshots` / `memory_compare_snapshots` | `snapshot-take` / `snapshot-list` / `snapshot-compare` |
  | `frame_debugger_enable` / `_disable` / `_get_events` | `frame-debugger-enable` / `-disable` / `-events` |
  | `ping` | `ping` |

  Action matching folds case and ignores `-` / `_` / spaces, the way MCP compares actions, so
  `get_frame_timing`, `get-frame-timing` and `getFrameTiming` are one request. Aliases are declared
  in the group's `Actions` list and enumerated in its schema, which is what keeps them visible to
  `cucli commands` and to `verify.ps1` gate 3 rather than being an undocumented back door.

  `vfx` (63/63) and `scene` / `graphics` keep MCP's snake_case names verbatim, so the package is
  still **internally inconsistent about naming style**; the difference is that a name now works
  whether or not you know which style its group chose.
- **`profiler counters` returns four keys per row, not three.** MCP emits `<name>`, `<name>_valid`
  and `<name>_unit` fanned into one key space; `cucli` emits a row `{name, value, valid, unit}`, which
  is easier to parse. The extra `valid` is deliberate and must not be removed: a `ProfilerRecorder`
  returns zero or invalid readings until the player loop has settled, so dropping the flag would hide
  "this number is not trustworthy yet" and make the tool lie.
- `vfx` is gated per action, not per group: only the 23 `vfx_*` actions require
  `com.unity.visualeffectgraph`. The 39 `particle_*` / `line_*` / `trail_*` actions use built-in
  UnityEngine types and work in a project that has no VFX Graph at all.
