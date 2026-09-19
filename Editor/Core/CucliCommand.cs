using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace CustomUnityCli.Editor
{
    /// <summary>Editor state a command declares it needs before it is dispatched.</summary>
    public enum CucliMode
    {
        /// <summary>No Editor state requirement at all.</summary>
        Any = 0,

        /// <summary>May run while the Editor is in Edit Mode.</summary>
        Edit = 1,

        /// <summary>Requires Play Mode.</summary>
        Play = 2,

        /// <summary>Does not touch the Editor at all; can be served while Unity is closed.</summary>
        Offline = 3
    }

    /// <summary>One sub-action of a command group, as advertised by <c>cucli commands list</c>.</summary>
    public sealed class CucliActionInfo
    {
        public CucliActionInfo(string name, string summary)
        {
            Name = name;
            Summary = summary;
        }

        /// <summary>Sub-action name, e.g. <c>create</c> in <c>cucli object create</c>.</summary>
        public string Name { get; private set; }

        /// <summary>One line describing what the action does.</summary>
        public string Summary { get; private set; }
    }

    /// <summary>
    /// Contract implemented by every worker command group.
    ///
    /// A command is a <em>group</em> - <c>scene</c>, <c>object</c>, <c>asset</c> - and it declares
    /// the sub-actions it accepts. The CLI maps <c>cucli object create --name Ball</c> onto the
    /// <c>object</c> command with <c>{ "action": "create", "name": "Ball" }</c>. This keeps the
    /// compiled surface small enough to audit while still exposing hundreds of operations.
    ///
    /// Implementations must be public, non-abstract and have a public parameterless constructor:
    /// the registry discovers them by reflection.
    /// </summary>
    public interface ICucliCommand
    {
        /// <summary>Group name, e.g. <c>scene</c>. Must be globally unique and lower case.</summary>
        string Name { get; }

        /// <summary>One line a human or agent reads in <c>cucli commands list</c>.</summary>
        string Summary { get; }

        /// <summary>Sub-actions this group accepts. Empty for single-purpose commands.</summary>
        CucliActionInfo[] Actions { get; }

        /// <summary>JSON Schema for the payload accepted by <see cref="Execute"/>.</summary>
        string ParametersSchemaJson { get; }

        bool RequiresEditor { get; }

        CucliMode RequiredMode { get; }

        bool MutatesProject { get; }

        /// <summary>Auditable side-effect classes, e.g. <c>project.assets.write</c>.</summary>
        string[] Effects { get; }

        /// <summary>
        /// True when the command may trigger a script recompile or domain reload. Such jobs move to
        /// <c>jobs/waiting</c> before execution and are finished by the reloaded worker.
        /// </summary>
        bool MayReloadDomain { get; }

        /// <summary>Package that must be installed for this command to exist, or null.</summary>
        string OptionalPackage { get; }

        CucliCommandResult Execute(CucliCommandContext context);
    }

    /// <summary>
    /// Optional contract for operations that span Editor frames (screenshots, sequences,
    /// simulation, builds). <see cref="ICucliCommand.Execute"/> must fail fast and
    /// <c>ExecuteDeferred</c> must invoke <c>complete</c> exactly once.
    /// </summary>
    public interface ICucliDeferredCommand : ICucliCommand
    {
        void ExecuteDeferred(
            CucliCommandContext context,
            Action<CucliCommandResult> complete);
    }

    /// <summary>
    /// Registers a set of commands that does not map one-to-one onto types. The project-local
    /// declarative commands use this: one JSON file on disk becomes one command. The registry
    /// discovers implementors exactly the way it discovers commands.
    /// </summary>
    public interface ICucliCommandProvider
    {
        IEnumerable<ICucliCommand> CreateCommands();
    }

    /// <summary>
    /// Everything a command needs: the parsed payload, an identity for logging and a cancellation
    /// token that is checked at every recoverable boundary.
    /// </summary>
    public sealed class CucliCommandContext
    {
        private readonly List<string> m_warnings = new List<string>();
        private readonly List<string> m_artifacts = new List<string>();

        internal CucliCommandContext(string commandName, string jobId, JObject args, CancellationToken cancellation)
        {
            CommandName = commandName;
            JobId = jobId;
            Args = args ?? new JObject();
            Cancellation = cancellation;
        }

        public string CommandName { get; }

        public string JobId { get; }

        public JObject Args { get; }

        public CancellationToken Cancellation { get; }

        /// <summary>Shortcut around <see cref="CucliArgs"/> so commands read fields directly.</summary>
        public CucliArgs Input => new CucliArgs(Args);

        public IReadOnlyList<string> Warnings => m_warnings;

        public IReadOnlyList<string> Artifacts => m_artifacts;

        public void Warn(string message)
        {
            if (!string.IsNullOrWhiteSpace(message)) m_warnings.Add(message);
        }

        public void AddArtifact(string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) m_artifacts.Add(path);
        }

        public void ThrowIfCancelled()
        {
            if (Cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException("The job was cancelled before it completed.");
            }
        }
    }

    /// <summary>Outcome of a single command execution.</summary>
    public sealed class CucliCommandResult
    {
        public bool Success { get; set; } = true;
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }

        public static CucliCommandResult Ok(object data = null, string message = null)
        {
            return new CucliCommandResult { Success = true, Data = data, Message = message };
        }

        public static CucliCommandResult Fail(string errorCode, string message, object data = null)
        {
            return new CucliCommandResult
            {
                Success = false,
                ErrorCode = errorCode,
                Message = message,
                Data = data
            };
        }

        public static CucliCommandResult Missing(string parameter, string hint = null)
        {
            return Fail(
                CucliErrors.MissingParameter,
                "Required parameter '" + parameter + "' is missing." + (hint == null ? "" : " " + hint));
        }

        public static CucliCommandResult Invalid(string parameter, string reason)
        {
            return Fail(
                CucliErrors.InvalidParameter,
                "Parameter '" + parameter + "' is invalid: " + reason);
        }

        public static CucliCommandResult Unsupported(string action, string[] supported)
        {
            return Fail(
                CucliErrors.UnsupportedAction,
                "Unsupported action '" + action + "'. Supported: " + string.Join(", ", supported) + ".");
        }
    }

    /// <summary>
    /// Default metadata for command groups. Concrete groups override only what differs, which keeps
    /// each file focused on the actual Editor work.
    /// </summary>
    public abstract class CucliCommand : ICucliCommand
    {
        public abstract string Name { get; }

        public abstract string Summary { get; }

        public virtual CucliActionInfo[] Actions => Array.Empty<CucliActionInfo>();

        public virtual string ParametersSchemaJson =>
            "{\"type\":\"object\",\"additionalProperties\":true}";

        public virtual bool RequiresEditor => true;

        public virtual CucliMode RequiredMode => CucliMode.Any;

        public virtual bool MutatesProject => false;

        public virtual string[] Effects => Array.Empty<string>();

        public virtual bool MayReloadDomain => false;

        public virtual string OptionalPackage => null;

        public abstract CucliCommandResult Execute(CucliCommandContext context);

        /// <summary>Reads <c>action</c> from the payload, falling back to the first declared action.</summary>
        protected string ResolveAction(CucliCommandContext context, string defaultAction = null)
        {
            string action = context.Input.GetString("action");
            if (!string.IsNullOrWhiteSpace(action)) return action.Trim().ToLowerInvariant();
            if (defaultAction != null) return defaultAction;
            return Actions.Length > 0 ? Actions[0].Name : string.Empty;
        }

        /// <summary>Action names, for building the "supported actions" part of an error message.</summary>
        protected string[] ActionNames()
        {
            var names = new string[Actions.Length];
            for (int index = 0; index < Actions.Length; index++) names[index] = Actions[index].Name;
            return names;
        }

        /// <summary>
        /// The <c>enum</c> array body for a schema's <c>action</c> property, generated from
        /// <see cref="Actions"/> so the schema can never disagree with the declarations.
        ///
        /// Groups with more than a handful of actions should build their schema with this instead of
        /// hand-listing the names: a schema that omits an action is invisible to anything reading
        /// <c>commands describe</c> rather than <c>commands list</c>, and a schema listing a removed
        /// action advertises something that will fail.
        /// </summary>
        protected string ActionEnumJson()
        {
            return "\"" + string.Join("\", \"", ActionNames()) + "\"";
        }

        /// <summary>Convenience for declaring actions inline.</summary>
        protected static CucliActionInfo[] Declare(params string[] nameSummaryPairs)
        {
            if (nameSummaryPairs.Length % 2 != 0)
            {
                throw new ArgumentException("Actions are declared as name/summary pairs.");
            }

            var result = new CucliActionInfo[nameSummaryPairs.Length / 2];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = new CucliActionInfo(
                    nameSummaryPairs[index * 2],
                    nameSummaryPairs[index * 2 + 1]);
            }

            return result;
        }

        protected static string ModeName(CucliMode mode)
        {
            switch (mode)
            {
                case CucliMode.Edit: return "edit";
                case CucliMode.Play: return "play";
                case CucliMode.Offline: return "offline";
                default: return "any";
            }
        }
    }
}
