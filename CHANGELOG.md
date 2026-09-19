# Changelog

All notable changes to this package are documented here.
This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] — unreleased

First public release.

### Compatibility

- **Supported target: Unity 2022.3.** The Editor worker, the command surface and the file protocol
  were designed and verified against that version, and the package manifest requires `2022.3`.
- **Unity 6 is out of scope by decision, not by oversight.** Projects on Unity 6 should use the
  command line tooling Unity provides for that generation instead. See the Compatibility section of
  the README.
- The 2021.3 LTS line is untested; it is no longer accepted by the package manifest.

### Added

**Transport and worker**

- File-queue job system under `Library/CustomUnityCli/` with `control`, `queued`, `running`,
  `waiting`, `succeeded`, `failed`, `cancelled` and `cancel-requests` states.
- `[InitializeOnLoad]` Editor worker that starts itself, publishes a heartbeat every second and
  keeps running across domain reloads.
- Same-directory atomic file replacement, so a reader can never observe a half-written job.
- Dedicated control queue for the handful of commands that must run while the Editor is compiling,
  which breaks the Play Mode / compile interlock.
- Domain reload contract: reload-capable jobs park in `waiting/` with a sidecar holding their real
  result, which the reloaded worker publishes verbatim. Jobs still in `running/` after a reload fail
  loudly with `WORKER_RELOADED` rather than being silently retried.
- Cooperative cancellation: a cancel request cancels the live token, or fails a job that has not
  started yet.
- Console capture to newline-delimited JSON with rotation, path and e-mail redaction, and a
  `clear` marker so clearing never truncates the file.
- Compile-status tracking that survives the reload the compilation itself causes.

**CLI**

- `Tools/cucli.ps1` — the whole CLI in one dependency-free PowerShell file: argument parser,
  offline commands and job transport.
- Offline commands that work with Unity closed: `project`, `status`, `doctor`, `logs`, `commands`,
  `job`, `open`, `version`.
- `open` launches Unity through an existing Unity Hub instance, or directly, and can wait for the
  worker to come up.
- `cucli.result/v1` envelope, `human` / `json` / `ndjson` output, automatic JSON when piped, and a
  documented exit-code table derived from stable error codes.
- `--detach` to queue and return a job id, `--timeout`, `--project`, and `$env:CUCLI_PROJECT`.
- Project root discovery by walking up from the script, so the same file works from a plugin folder,
  an embedded package or the Package Manager cache.

**Command surface**

- Editor control: state, Play Mode, menu items, undo/redo, active tool, project tags, layers and
  selection.
- Asset pipeline: refresh, compilation with wait-across-reload, reimport, wait-for-idle.
- Scenes, GameObjects, components and prefabs, with a unified resolver that accepts names,
  hierarchy paths, instance ids, tags, layers and component types.
- Assets, scripts, ScriptableObjects, materials, shaders and procedurally generated textures.
- UI Toolkit documents and elements; graphics, environment, lightmapping, quality, URP settings and
  volume profiles; cameras; animation clips, controllers and animators; physics.
- VFX Graph, particle systems, trails and lines; ProBuilder; profiler; memory audits; frame capture;
  synthesised input; recording; screenshots; player builds; Package Manager; C# execution; runtime
  reflection.
- Unity Test Framework suites as pollable async jobs.
- `selftest`: the tool testing itself through its own queue.

In total **32 Editor command groups carrying 402 actions**, plus 9 offline groups.

**Self-verification**

- `Tools/verify.ps1` — an offline build gate: manifest, `.meta` coverage, command contract, and a
  Roslyn compile of the Editor sources without opening Unity.
- `Tools/cucli-selftest.ps1`, driven by `cucli selftest` — a black-box behaviour gate that reads the
  tool's own catalog and exercises every group through the real CLI, so it also covers project-local
  extensions it has never seen.

**Project-local extensions (self-iteration)**

- An extension area outside the package, at `<project>/Assets/CustomUnityCli.Extensions` by default
  and relocatable with `CUCLI_EXTENSIONS`, where a project grows its own command groups. A handful of
  alternative folder names are discovered if the first choice is absent, and the root must be inside
  `Assets/` or `Packages/` or Unity would never compile it.
- `cucli extend init|list|path` creates and inspects that area **with Unity closed**, so a project
  can start before the Editor has ever been opened.
- `cucli meta new-command` scaffolds a compiling command group into the extension area.
- No assembly definition and no configuration are required: an `Editor/` folder under `Assets/`
  compiles into `Assembly-CSharp-Editor`, which already references this package.
- **The package boundary is enforced, not merely documented.** Every command is tagged
  `source: package` or `source: project`; `cucli commands export` writes package commands only into
  the published `commands/registry.json` and routes project commands to `registry.local.json` inside
  the extension area; `catalogHash` covers package commands while `fullCatalogHash` covers
  everything, so drift detection survives local growth; and `cucli meta extensions` reports any file
  found inside the package tree, which is the only arrangement that would publish project code.
- A project command may **not** reuse a package group name. The registry refuses the collision with
  `COMMAND_DUPLICATE` and the CLI reports it as `extensions.group-conflicts`, because the same command
  must mean the same thing in every project and because a shadowed group would silently leave the
  package-only catalog hash and make drift detection fire forever. Extend by adding a group of your
  own, never by redefining `object` or `asset`.
- The CLI merges `registry.local.json` over the package registry, so project commands stay listable
  and validatable while Unity is closed. Aliases merge with the project winning; group names do not.
- `cucli doctor` adds `extensions.placement`, `extensions.group-conflicts` and `package.checkout`
  (which runs `git status --porcelain` inside the package when it is a git checkout), so a leak is
  caught by the routine health check rather than at review time.

**Safety and ergonomics**

- `CucliDescribe` bounds every object dump by depth, item count and string length, so inspecting a
  `TextMeshProUGUI`, `Mesh` or `ParticleSystem` can never flood a caller.
- `CucliSerialized` writes through `SerializedObject` first, so property edits register Undo, become
  prefab overrides and mark assets dirty; reflection is the fallback.
- Cursor paging with an opaque token on every list-returning action.
- Runtime reflection for every optional package (ProBuilder, VFX Graph, Cinemachine, Test Framework,
  URP), so the core assembly has no optional reference and compiles anywhere.
- Destructive actions require `--force` and otherwise fail with `CONFIRMATION_REQUIRED`.
- Duplicate command names, missing constructors and empty names are reported in the catalog's
  `diagnostics[]` instead of being silently ignored.
- `cucli commands export` regenerates the shipped `commands/registry.json` from the compiled worker,
  and `cucli doctor` compares the two catalog hashes to detect drift.
- Command types no longer have to be public. The registry constructs them by reflection, so both the
  package's own `internal sealed class` commands and internal project extensions register correctly.

### Fixed

All four found by running `cucli selftest` against the CLI itself.

- **Negative numeric arguments were consumed as flags instead of values.** The parser decided that
  any token starting with `-` began a new option, so `cucli object inspect --instance-id -10844` set
  `instanceId` to `true` instead of to the id. Unity instance ids are routinely negative, so this
  silently broke every space-separated `--instance-id` call, and would have broken any negative
  `--position`, `--size`, `--value` or `--threshold` too. The visible symptom was a confusing
  `TARGET_NOT_FOUND`; for an optional parameter it was worse, because the command reported success
  while doing nothing. A leading `-` now starts an option only when what follows is not a number.
- **`--option=value` derived the payload key from the whole token.** `--instance-id=-10844` produced
  the key `instanceId=-10844`, so the real key was never set and the command saw a missing
  parameter. The key is now taken from the text before the `=`.
- **The self-test harness crashed while writing its report on PowerShell 7.6.** `@($list)` where
  `$list` is a `System.Collections.Generic.List[object]` holding `PSCustomObject` rows throws
  `ArgumentException: Argument types do not match`; `List[string]` and `List[psobject]` are
  unaffected, which is why the trap only appeared once the collection held result rows. A run that
  had completed all three tiers therefore reported nothing at all - no summary, no failure list, just
  exit code 1. The collections are now enumerated directly.
- **The self-test harness had no worker-readiness gate before Tier A.** Running it right after
  changing code - exactly when a developer runs it - produced one identical `WORKER_UNAVAILABLE`
  failure per action while a domain reload was in flight. One measured run reported 68 such failures
  against 3 passes, which buries whatever real defect is underneath. Both tiers now wait, bounded and
  with progress output, for the worker to answer, and report a single clear failure if it never does.

[0.1.0]: https://github.com/igp/custom-unity-cli/releases/tag/v0.1.0
