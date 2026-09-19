using Newtonsoft.Json.Linq;

namespace CustomUnityCli.Editor
{
    /// <summary>Wire models exchanged through <c>Library/CustomUnityCli</c>. Field names are camelCase on disk.</summary>
    internal sealed class CucliJobRequest
    {
        public string SchemaVersion { get; set; } = "cucli.job-request/v1";
        public string Id { get; set; }
        public string Command { get; set; }
        /// <summary>Sub-action, duplicated out of the payload so queue routing does not parse JSON twice.</summary>
        public string Action { get; set; }
        public string CreatedAtUtc { get; set; }
        public string PayloadJson { get; set; }
        public int TimeoutSeconds { get; set; }
        public bool Detached { get; set; }
    }

    internal sealed class CucliJobResult
    {
        public string SchemaVersion { get; set; } = "cucli.job-result/v1";
        public string Id { get; set; }
        public string Command { get; set; }
        public string State { get; set; }
        public bool Success { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public JToken Data { get; set; }
        public string[] Warnings { get; set; }
        public string[] Artifacts { get; set; }
        public string CreatedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public long DurationMs { get; set; }
    }

    internal sealed class CucliHeartbeat
    {
        public string SchemaVersion { get; set; } = "cucli.worker-heartbeat/v1";
        public int ProtocolVersion { get; set; } = CucliPaths.ProtocolVersion;
        public string WorkerVersion { get; set; } = CucliPaths.WorkerVersion;
        public string SessionId { get; set; }
        /// <summary>When the file was written. Refreshed by a background timer so it tracks
        /// <em>liveness</em> even while the Editor's own update loop is throttled or backgrounded.</summary>
        public string TimestampUtc { get; set; }
        /// <summary>When the Unity state fields below were actually sampled on the main thread. A
        /// liveness-fresh heartbeat can carry an older state sample, and callers must be able to tell
        /// the difference between "Unity is gone" and "Unity is busy".</summary>
        public string StateSampledAtUtc { get; set; }

        /// <summary>
        /// When <see cref="UnityEditor.EditorApplication.update"/> last ran.
        ///
        /// The background heartbeat writer keeps <see cref="TimestampUtc"/> fresh even while Unity's
        /// editor loop is throttled or stopped (window unfocused, a modal dialog, a long import).
        /// That makes the file a poor liveness signal on its own, and it makes a queued job sit
        /// unserviced with no visible reason. This stamp is the honest signal: if it is old while
        /// <see cref="TimestampUtc"/> is fresh, the worker is alive but its main loop is not
        /// turning, and every job will be delayed until it does.
        /// </summary>
        public string MainThreadTickAtUtc { get; set; }

        /// <summary>Number of jobs waiting to be serviced, so a caller can tell "busy" from "idle".</summary>
        public int PendingJobCount { get; set; }
        public string ProjectPath { get; set; }
        public string ProjectName { get; set; }
        public string UnityVersion { get; set; }
        public string ApplicationPath { get; set; }
        public int ProcessId { get; set; }
        public bool HasCompilationErrors { get; set; }
        public bool IsCompiling { get; set; }
        public bool IsUpdating { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public bool IsPlayingOrWillChangePlaymode { get; set; }
        public bool IsBatchMode { get; set; }
        public string ActiveScenePath { get; set; }
        public int OpenSceneCount { get; set; }
        public int CommandCount { get; set; }
    }

    internal sealed class CucliActionDescriptor
    {
        public string Name { get; set; }
        public string Summary { get; set; }
    }

    internal sealed class CucliCommandDescriptor
    {
        public string Name { get; set; }

        /// <summary>
        /// Provenance: <c>package</c> for commands compiled into custom-unity-cli, <c>project</c> for
        /// commands that live in the host project. Only <c>package</c> commands are ever written into
        /// the published <c>commands/registry.json</c>, so a project's own command names cannot leak
        /// into the public repository.
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Project-relative path of the file that declared a project command: the <c>.cs</c> source for
        /// a compiled extension, or the JSON file for a declarative one. Null for package commands, and
        /// null rather than guessed when the source cannot be resolved - a wrong path is worse than none.
        /// </summary>
        public string SourcePath { get; set; }

        public string Summary { get; set; }
        public CucliActionDescriptor[] Actions { get; set; }
        public string ParametersSchemaJson { get; set; }
        public bool RequiresEditor { get; set; }
        public string RequiredMode { get; set; }
        public bool MutatesProject { get; set; }
        public string[] Effects { get; set; }
        public bool MayReloadDomain { get; set; }
        public string OptionalPackage { get; set; }
    }

    internal sealed class CucliCommandDiagnostic
    {
        public string Code { get; set; }
        public string TypeName { get; set; }
        public string CommandName { get; set; }
        public string Message { get; set; }
    }

    internal sealed class CucliCommandCatalog
    {
        public string SchemaVersion { get; set; } = "cucli.command-catalog/v1";
        public string GeneratedAtUtc { get; set; }
        public string WorkerVersion { get; set; } = CucliPaths.WorkerVersion;
        public int ProtocolVersion { get; set; } = CucliPaths.ProtocolVersion;
        public string ProjectPath { get; set; }
        public string UnityVersion { get; set; }

        /// <summary>
        /// Fingerprint of the package commands in <see cref="Commands"/>. The shipped
        /// <c>commands/registry.json</c> records this exact value, so drift detection keeps working
        /// in a project that has grown project-local commands.
        /// </summary>
        public string CatalogHash { get; set; }

        /// <summary>Fingerprint of every command, including project-local extensions.</summary>
        public string FullCatalogHash { get; set; }

        public int PackageCommandCount { get; set; }
        public int ProjectCommandCount { get; set; }

        public CucliCommandDescriptor[] Commands { get; set; }
        public CucliCommandDiagnostic[] Diagnostics { get; set; }
    }
}
