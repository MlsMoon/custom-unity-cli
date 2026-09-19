using System;
using System.IO;
using System.Threading;

namespace CustomUnityCli.Editor
{
    internal static partial class CucliJobProcessor
    {
        private static void Execute(string runningPath)
        {
            CucliJobRequest request = TryReadRequest(runningPath);
            if (request == null)
            {
                request = FallbackRequest(runningPath);
                WriteTerminal(runningPath, CucliPaths.Failed, request, false,
                    CucliErrors.JobInvalid,
                    "The job file is missing an id or command, or its id does not match its file name.",
                    null, 0L);
                return;
            }

            if (IsExpired(request))
            {
                WriteTerminal(runningPath, CucliPaths.Failed, request, false,
                    CucliErrors.JobExpired,
                    "The job expired before the Editor could execute it.", null, 0L);
                return;
            }

            ICucliCommand command;
            if (!CucliCommandRegistry.TryGet(request.Command, out command))
            {
                WriteTerminal(runningPath, CucliPaths.Failed, request, false,
                    CucliErrors.CommandNotFound,
                    "Editor command '" + request.Command + "' is not registered in this Editor. "
                    + "Run 'cucli commands list' to see what is available.", null, 0L);
                return;
            }

            string modeFailure = ValidateMode(command);
            if (modeFailure != null)
            {
                WriteTerminal(runningPath, CucliPaths.Failed, request, false,
                    CucliErrors.WrongMode, modeFailure,
                    new { command = request.Command, isPlaying = UnityEditor.EditorApplication.isPlaying },
                    0L);
                return;
            }

            // A command that may reload the domain must survive that reload, so its job is parked in
            // "waiting" before execution and resolved by CompleteWaitingJobs on the far side.
            string executionPath = runningPath;
            if (command.MayReloadDomain)
            {
                string waitingPath = Path.Combine(CucliPaths.Waiting, Path.GetFileName(runningPath));
                if (!TryClaim(runningPath, waitingPath)) return;
                executionPath = waitingPath;
            }

            var context = new CucliCommandContext(
                request.Command,
                request.Id,
                CucliJson.TryParseObject(request.PayloadJson),
                CancellationToken.None);

            try
            {
                var deferred = command as ICucliDeferredCommand;
                if (deferred != null)
                {
                    var cancellation = new CancellationTokenSource();
                    ActiveCancellations[request.Id] = cancellation;
                    var deferredContext = new CucliCommandContext(
                        request.Command, request.Id, context.Args, cancellation.Token);

                    bool completed = false;
                    deferred.ExecuteDeferred(deferredContext, result =>
                    {
                        // A misbehaving command must not be able to complete the job twice.
                        if (completed) return;
                        completed = true;
                        Complete(executionPath, request, result, cancellation, deferredContext);
                    });
                    return;
                }

                Complete(executionPath, request, command.Execute(context), null, context);
            }
            catch (OperationCanceledException)
            {
                DisposeCancellation(request.Id);
                if (!File.Exists(executionPath)) return;
                WriteTerminal(executionPath, CucliPaths.Cancelled, request, false,
                    CucliErrors.Cancelled, "The job was cancelled while it was running.",
                    null, 0L,
                    Snapshot(context.Warnings), Snapshot(context.Artifacts));
            }
            catch (CucliUsageException exception)
            {
                DisposeCancellation(request.Id);
                Complete(executionPath, request, exception.Result, null, context);
            }
            catch (Exception exception)
            {
                DisposeCancellation(request.Id);
                if (!File.Exists(executionPath)) return;
                WriteTerminal(executionPath, CucliPaths.Failed, request, false,
                    CucliErrors.CommandException,
                    exception.GetBaseException().Message,
                    new { exceptionType = exception.GetType().FullName, detail = exception.ToString() },
                    0L,
                    Snapshot(context.Warnings), Snapshot(context.Artifacts));
            }
        }

        /// <summary>
        /// Single completion path for both synchronous and deferred execution.
        ///
        /// For a reload-capable command the real result is written to a sidecar file and the job
        /// stays in <c>waiting</c>. If a reload really happens, the reloaded worker emits the sidecar
        /// unchanged; if it does not, the next idle tick emits the same content. Either way the
        /// caller gets the command's own data instead of a synthetic "it survived" placeholder.
        /// </summary>
        private static void Complete(
            string executionPath,
            CucliJobRequest request,
            CucliCommandResult result,
            CancellationTokenSource cancellation,
            CucliCommandContext context)
        {
            bool cancelled = cancellation != null && cancellation.IsCancellationRequested;
            DisposeCancellation(request.Id);
            if (!File.Exists(executionPath)) return;

            string[] warnings = Snapshot(context.Warnings);
            string[] artifacts = Snapshot(context.Artifacts);

            if (cancelled)
            {
                WriteTerminal(executionPath, CucliPaths.Cancelled, request, false,
                    CucliErrors.Cancelled, "The job was cancelled while it was running.",
                    result == null ? null : result.Data, 0L, warnings, artifacts);
                return;
            }

            if (result == null)
            {
                WriteTerminal(executionPath, CucliPaths.Failed, request, false,
                    CucliErrors.CommandFailed,
                    "The command returned no result object.", null, 0L, warnings, artifacts);
                return;
            }

            if (!result.Success)
            {
                WriteTerminal(executionPath, CucliPaths.Failed, request, false,
                    string.IsNullOrWhiteSpace(result.ErrorCode) ? CucliErrors.CommandFailed : result.ErrorCode,
                    result.Message ?? "The command reported a failure.",
                    result.Data, 0L, warnings, artifacts);
                return;
            }

            ICucliCommand command;
            bool reloadCapable = CucliCommandRegistry.TryGet(request.Command, out command)
                && command.MayReloadDomain;
            if (!reloadCapable)
            {
                WriteTerminal(executionPath, CucliPaths.Succeeded, request, true,
                    null, result.Message, result.Data, 0L, warnings, artifacts);
                return;
            }

            WritePendingResult(executionPath, request, result, warnings, artifacts);
        }

        private static void DisposeCancellation(string id)
        {
            CancellationTokenSource source;
            if (!ActiveCancellations.TryGetValue(id, out source)) return;
            ActiveCancellations.Remove(id);
            try { source.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Rejects a job whose declared Editor mode does not match reality. Failing here - instead of
        /// inside the command - gives every command the same predictable error code.
        /// </summary>
        private static string ValidateMode(ICucliCommand command)
        {
            bool playing = UnityEditor.EditorApplication.isPlaying;
            switch (command.RequiredMode)
            {
                case CucliMode.Play:
                    return playing
                        ? null
                        : "This command requires Play Mode. Start it with 'cucli play start' first.";
                case CucliMode.Edit:
                    return playing
                        ? "This command requires Edit Mode. Stop Play Mode with 'cucli play stop' first."
                        : null;
                default:
                    return null;
            }
        }

        private static string[] Snapshot(System.Collections.Generic.IReadOnlyList<string> source)
        {
            if (source == null || source.Count == 0) return null;
            var result = new string[source.Count];
            for (int index = 0; index < source.Count; index++) result[index] = source[index];
            return result;
        }
    }
}
