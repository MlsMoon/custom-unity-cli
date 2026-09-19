# The `cucli` file protocol

`cucli` has no socket, no server and no daemon. The CLI and the Unity Editor worker communicate by
exchanging JSON files in one directory:

```
<project>/Library/CustomUnityCli/
├── cli-log.ndjson                     the CLI's own append-only log
├── worker/
│   ├── heartbeat.json                 rewritten once a second while the Editor is alive
│   └── commands.json                  the catalog of commands compiled into this Editor
├── console/
│   ├── entries.ndjson                 captured console output
│   └── entries.1.ndjson               the previous 20 MB, after rotation
└── jobs/
    ├── control/                       jobs that bypass the "Editor is idle" gate
    ├── queued/                        accepted, not yet claimed
    ├── running/                       claimed and executing
    ├── waiting/                       parked across a domain reload
    ├── succeeded/  failed/  cancelled/ terminal results
    ├── cancel-requests/               "please stop this job" markers
    └── waiting/<id>.pending           sidecar: the real result of a reload-capable job
```

Nothing is ever written into the package folder. Deleting `Library/CustomUnityCli/` is always safe.

## Why files and not a socket

Four properties matter, and only the file queue gives all four at once.

1. **Domain reloads.** Compiling scripts replaces the loaded assemblies and destroys every managed
   object, including any in-flight socket handler and its callbacks. A file survives because it is
   not in memory. `cucli refresh compile --wait` depends on exactly this.
2. **No port, no handshake.** There is no listen address to configure, no client wiring step, and no
   chance of two projects fighting over a port. `cucli` works the moment the folder exists.
3. **Observability.** `cucli job list` and `cucli console` read the same files the worker writes, so
   they answer while the Editor is importing, compiling or paused at a breakpoint.
4. **Atomicity is cheap.** A same-directory rename is atomic on every platform Unity supports, so a
   reader either sees the whole previous file or the whole new one.

The cost is latency: a job is picked up on the worker's next update tick, which polls every 200 ms.
That is irrelevant for Editor operations, which take far longer than that anyway.

## Job lifecycle

```
                      ┌──────────────────────── cancel-requests/<id>.json
                      ▼
  CLI writes     ┌─────────┐   worker claims   ┌─────────┐
  ──────────────▶│ queued/ │──────────────────▶│ running/│
                 └─────────┘                   └────┬────┘
                                                    │
                       ┌────────────────────────────┼────────────────────────────┐
                       │                            │                            │
                       ▼                            ▼                            ▼
              reload-capable               synchronous outcome            cancelled
                       │                            │                            │
                       ▼                            ▼                            ▼
                 ┌─────────┐               ┌───────────┐  ┌────────┐     ┌───────────┐
                 │waiting/ │               │succeeded/ │  │failed/ │     │cancelled/ │
                 └────┬────┘               └───────────┘  └────────┘     └───────────┘
                      │
        reload happens│  or the Editor simply becomes idle
                      ▼
              publish the sidecar result to succeeded/ or failed/
```

### Request

`jobs/queued/<uuid>.json` — the file name **must** equal `id`, because the worker validates that.

```json
{
  "schemaVersion": "cucli.job-request/v1",
  "id": "9f1c…",
  "command": "object",
  "action": "create",
  "createdAtUtc": "2026-01-01T00:00:00.0000000Z",
  "payloadJson": "{\"action\":\"create\",\"name\":\"Ball\"}",
  "timeoutSeconds": 60,
  "detached": false
}
```

`payloadJson` is an embedded JSON **string**, not a nested object. That keeps the job file flat and
makes it impossible for a payload key to collide with an envelope key.

`action` is denormalised out of the payload purely so queue routing does not have to parse JSON.

### Result

`jobs/{succeeded,failed,cancelled}/<uuid>.json`

```json
{
  "schemaVersion": "cucli.job-result/v1",
  "id": "9f1c…",
  "command": "object",
  "state": "succeeded",
  "success": true,
  "errorCode": null,
  "message": "人类可读的中文结论",
  "data": { "…": "…" },
  "warnings": [],
  "artifacts": ["D:\\Project\\Assets\\Screenshots\\shot.png"],
  "createdAtUtc": "…",
  "completedAtUtc": "…",
  "durationMs": 42
}
```

`state` is `succeeded`, `failed` or `cancelled`. Always check `success` as well as the process exit
code: a job that lost a race, expired or was cancelled reports `success: false`.

Every file write uses this sequence, which is why a reader can open a file at any moment:

1. write `path.<guid>.tmp` in the **same** directory,
2. `MoveFileExW(tmp, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)` on Windows,
   `File.Replace` elsewhere,
3. if the replace fails, leave the previous file untouched and delete the temporary.

The worker never degrades to delete-then-write.

## Editor modes

A command declares `RequiredMode`, and the worker rejects a job whose mode does not match reality
**before** invoking the command, so every command reports the same `WRONG_MODE` error.

| Mode | Meaning |
| --- | --- |
| `any` | No Play Mode requirement. |
| `edit` | Refused while in Play Mode. |
| `play` | Refused outside Play Mode. |
| `offline` | Handled by the CLI itself without a worker. |

## The control queue

`jobs/control/` accepts only the commands in `commands/registry.json`'s `controlActions` list:

```json
"controlActions": ["editor/stop", "editor/pause"]
```

Normal jobs wait for `!EditorApplication.isCompiling && !EditorApplication.isUpdating`. That gate is
what makes asset writes safe, and it is also what creates the deadlock: if the Editor is compiling
and you want to leave Play Mode, the job that would stop Play Mode is itself blocked.

The control queue skips the gate for exactly the commands that break that interlock. Anything else
placed there fails with `CONTROL_COMMAND_NOT_ALLOWED`.

## Domain reload contract

Compiling scripts destroys managed state. `cucli` handles this explicitly instead of pretending it
does not happen.

**A command that may reload the domain sets `MayReloadDomain => true`** (or `override`). Before it
runs, the worker moves its job from `running/` to `waiting/`. Then:

- If the command finishes synchronously, its **real** result — data, message, warnings, artifacts —
  is written to `waiting/<id>.pending`. The job is *not* published yet.
- If a domain reload follows, the freshly loaded worker finds the job in `waiting/`, waits for the
  Editor to become idle, and publishes the sidecar content **verbatim**.
- If no reload happens, the next idle tick publishes the same content.

Either way the caller receives the command's own result rather than a synthetic "it survived"
placeholder. The `.pending` extension deliberately keeps the sidecar out of the `*.json` job scans.

**Interrupted jobs.** A job found in `running/` when the worker bootstraps means the Editor reloaded
mid-execution. The worker cannot know whether assets were half-written, so it **fails the job with
`WORKER_RELOADED`** instead of retrying. Callers must query the result before retrying anything that
writes assets.

**Deferred commands must not trigger a reload.** If an operation genuinely needs one, model it as a
recoverable `waiting` protocol rather than relying on an in-memory callback surviving the reload.

## Cancellation

`cucli job cancel <id>` writes `jobs/cancel-requests/<id>.json`. On its next tick the worker:

- cancels the live `CancellationToken` if the job is executing, or
- moves a job that has not started yet straight to `cancelled/`.

Delayed commands check `context.ThrowIfCancelled()` at every recoverable boundary and must stop
issuing steps, scene changes or capture files once cancelled. If a gesture has already begun, sending
only the necessary release events to clean up is allowed.

`cucli` sets `--timeout` on every wait. On expiry it writes a cancel request and reports `TIMEOUT`
(exit code 6) with the job id, so the operation is not left orphaned.

## Heartbeat and liveness

`worker/heartbeat.json` is rewritten every second:

```json
{
  "schemaVersion": "cucli.worker-heartbeat/v1",
  "protocolVersion": 1,
  "workerVersion": "0.1.0",
  "sessionId": "…",
  "timestampUtc": "…",
  "projectPath": "…",
  "projectName": "…",
  "unityVersion": "2022.3.62f2c1",
  "applicationPath": "…",
  "processId": 12345,
  "hasCompilationErrors": false,
  "isCompiling": false,
  "isUpdating": false,
  "isPlaying": false,
  "isPaused": false,
  "activeScenePath": "Assets/Scenes/Main.unity",
  "openSceneCount": 1,
  "commandCount": 34
}
```

The CLI treats a heartbeat older than 15 seconds as offline and fails Editor commands with
`WORKER_UNAVAILABLE` (exit code 7, retryable). The Editor deletes the heartbeat on quit, so a stale
file after a crash expires on its own.

## Command catalog

Two files describe the same surface, and `cucli doctor` compares them.

| File | Written by | Used for |
| --- | --- | --- |
| `worker/commands.json` | the Editor, on every domain reload | authoritative; dispatch validation and `commands list` while Unity is open |
| `commands/registry.json` | `cucli commands export` | offline `commands list` / `describe`, plus the hand-maintained `aliases`, `offline` and `controlActions` sections |

Both carry a `catalogHash`: a SHA-256 over the machine-relevant fields of each command (name, mode,
flags, optional package, effects, action names, normalised parameter schema — **not** prose
summaries). Reordering does not change it; rewording a summary does not either. `doctor` reports
`registry.drift` when the static and runtime hashes differ, and the fix is always
`cucli commands export`.

## Console capture

`console/entries.ndjson` receives one compact JSON object per line:

```json
{"sequence":42,"timestampUtc":"…","sessionId":"…","level":"error","message":"…","stackTrace":"…"}
```

- `level` is `log`, `warning`, `error`, `assert`, `exception` or the `clear` marker.
- The current user profile path is replaced with `<user>` and e-mail addresses with
  `<redacted-email>` **before** anything is written, so captured logs are safe to paste into an issue
  or a public repository.
- At 20 MB the file rotates to `entries.1.ndjson`, replacing any previous archive.
- `cucli console clear` appends a `clear` marker rather than truncating. Readers ignore everything
  before the newest marker, which keeps clearing cheap and crash-safe.
- `cucli console` reads only the tail of the file (4 MB) and then applies paging, so a multi-hundred-
  megabyte log cannot be pulled into memory.

## Versioning

`schemaVersion` values are independently versioned: `cucli.job-request/v1`,
`cucli.job-result/v1`, `cucli.worker-heartbeat/v1`, `cucli.command-catalog/v1`,
`cucli.command-registry/v1`, `cucli.result/v1`. The CLI and the worker also exchange
`protocolVersion` (currently `1`) through the heartbeat and the catalog; a mismatch is reported by
`cucli status` rather than failing silently.
