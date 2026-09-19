using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Serial job pump driven by <see cref="EditorApplication.update"/>.
    ///
    /// Ordinary jobs run strictly one at a time and never while the Editor is compiling or
    /// importing. A tiny second queue (<c>jobs/control</c>) bypasses that gate on purpose so
    /// <c>cucli play stop</c> can break a Play Mode / compile interlock.
    /// </summary>
    internal static partial class CucliJobProcessor
    {
        /// <summary>
        /// Commands allowed to skip the normal "Editor is idle" gate, as <c>command/action</c>.
        /// These break the Play Mode / compile interlock, so they must run even while a normal job
        /// is waiting for the Editor to settle.
        /// </summary>
        private static readonly HashSet<string> ControlCommands =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "editor/stop",
                "editor/pause"
            };

        private const int PollIntervalMilliseconds = 200;

        private static readonly Dictionary<string, CancellationTokenSource> ActiveCancellations =
            new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);

        private static DateTime s_nextPollUtc;
        private static DateTime s_nextPruneUtc;
        private static int s_completed;

        internal static int CompletedCount => s_completed;

        internal static void Initialize()
        {
            CucliPaths.EnsureDirectories();
            RecoverInterruptedJobs();
            s_nextPollUtc = DateTime.MinValue;
            s_nextPruneUtc = DateTime.UtcNow.AddMinutes(5);
        }

        internal static void Shutdown()
        {
            foreach (CancellationTokenSource source in ActiveCancellations.Values.ToArray())
            {
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            ActiveCancellations.Clear();
        }

        internal static void Tick()
        {
            if (DateTime.UtcNow < s_nextPollUtc) return;
            s_nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollIntervalMilliseconds);

            ProcessCancellationRequests();
            ProcessNextControlJob();
            CompleteWaitingJobs();

            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                ProcessNextQueuedJob();
            }

            if (DateTime.UtcNow >= s_nextPruneUtc)
            {
                s_nextPruneUtc = DateTime.UtcNow.AddMinutes(5);
                CucliPaths.PruneTerminalJobs();
            }
        }

        /// <summary>
        /// A job left in <c>running</c> means the Editor reloaded mid-execution. We cannot know
        /// whether it wrote assets, so it fails loudly with <c>WORKER_RELOADED</c> instead of
        /// silently retrying.
        /// </summary>
        private static void RecoverInterruptedJobs()
        {
            string[] stale;
            try
            {
                stale = Directory.GetFiles(CucliPaths.Running, "*.json");
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            foreach (string path in stale)
            {
                CucliJobRequest request = TryReadRequest(path) ?? FallbackRequest(path);
                WriteTerminal(path, CucliPaths.Failed, request, false,
                    CucliErrors.WorkerReloaded,
                    "The Editor reloaded while this command was running. Query the result before retrying "
                    + "anything that writes assets.", null, 0L);
            }
        }

        private static void ProcessCancellationRequests()
        {
            string[] requests;
            try
            {
                requests = Directory.GetFiles(CucliPaths.CancelRequests, "*.json");
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            foreach (string cancellation in requests)
            {
                string id = Path.GetFileNameWithoutExtension(cancellation);
                string source = FindPendingJob(id);
                if (source != null)
                {
                    CancellationTokenSource active;
                    if (ActiveCancellations.TryGetValue(id, out active))
                    {
                        try { active.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }
                    else
                    {
                        CucliJobRequest request = TryReadRequest(source) ?? FallbackRequest(source);
                        WriteTerminal(source, CucliPaths.Cancelled, request, false,
                            CucliErrors.Cancelled, "The job was cancelled before it completed.", null, 0L);
                    }
                }

                TryDelete(cancellation);
            }
        }

        /// <summary>
        /// Jobs parked in <c>waiting</c> survive a domain reload. Once the Editor is idle again we
        /// resolve them from the freshly compiled state.
        /// </summary>
        private static void CompleteWaitingJobs()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            string[] waiting;
            try
            {
                waiting = Directory.GetFiles(CucliPaths.Waiting, "*.json");
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            foreach (string path in waiting)
            {
                CucliJobRequest request = TryReadRequest(path) ?? FallbackRequest(path);

                // Reload-capable jobs store their real result in a sidecar before parking here.
                // Publishing it verbatim is what keeps "cucli refresh compile" returnable.
                CucliJobResult pending = TryReadPendingResult(request.Id);
                if (pending != null)
                {
                    string[] warnings = pending.Warnings;
                    if (CucliCompilationStatus.HasErrors)
                    {
                        warnings = Append(warnings,
                            "Script compilation finished with errors after this command ran: "
                            + CucliCompilationStatus.FirstError);
                    }

                    WriteTerminal(path, CucliPaths.Succeeded, request, true, null,
                        string.IsNullOrWhiteSpace(pending.Message)
                            ? "The command completed and survived the Editor reload."
                            : pending.Message,
                        pending.Data, 0L, warnings, pending.Artifacts);
                    continue;
                }

                bool hasErrors = CucliCompilationStatus.HasErrors;
                WriteTerminal(
                    path,
                    hasErrors ? CucliPaths.Failed : CucliPaths.Succeeded,
                    request,
                    !hasErrors,
                    hasErrors ? CucliErrors.CompilationFailed : null,
                    hasErrors
                        ? "Script compilation finished with errors; the command was not confirmed."
                        : "The command survived a domain reload and the Editor is idle with no compile errors.",
                    new
                    {
                        editorIdle = true,
                        compilationErrors = hasErrors,
                        reloaded = true,
                        commandCompleted = false
                    },
                    0L);
            }
        }

        private static string[] Append(string[] source, string extra)
        {
            if (string.IsNullOrWhiteSpace(extra)) return source;
            if (source == null || source.Length == 0) return new[] { extra };
            var result = new string[source.Length + 1];
            Array.Copy(source, result, source.Length);
            result[source.Length] = extra;
            return result;
        }

        private static void ProcessNextQueuedJob()
        {
            if (Directory.EnumerateFiles(CucliPaths.Running, "*.json").Any()) return;
            if (Directory.EnumerateFiles(CucliPaths.Waiting, "*.json").Any()) return;

            string queued = OldestFile(CucliPaths.Queued);
            if (queued == null) return;

            string running = Path.Combine(CucliPaths.Running, Path.GetFileName(queued));
            if (!TryClaim(queued, running)) return;
            Execute(running);
        }

        private static void ProcessNextControlJob()
        {
            string queued = OldestFile(CucliPaths.Control);
            if (queued == null) return;

            CucliJobRequest request = TryReadRequest(queued);
            if (request == null)
            {
                request = FallbackRequest(queued);
                WriteTerminal(queued, CucliPaths.Failed, request, false,
                    CucliErrors.JobInvalid, "The control job file is not valid JSON.", null, 0L);
                return;
            }

            if (!IsControlCommand(request))
            {
                WriteTerminal(queued, CucliPaths.Failed, request, false,
                    CucliErrors.ControlCommandNotAllowed,
                    "The control queue only accepts Editor state-breaking commands ("
                    + string.Join(", ", ControlCommands) + ").", null, 0L);
                return;
            }

            string running = Path.Combine(CucliPaths.Running, Path.GetFileName(queued));
            if (!TryClaim(queued, running)) return;
            Execute(running);
        }
    }
}
