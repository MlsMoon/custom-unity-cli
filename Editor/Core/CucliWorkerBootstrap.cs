using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Starts the worker as soon as the Editor loads and keeps it alive across domain reloads.
    ///
    /// Nothing here opens a socket, spawns a process or writes outside
    /// <c>&lt;project&gt;/Library/CustomUnityCli</c>, which is what makes the whole tool safe to
    /// install in a shared project: the worst case is a few files in an already-ignored folder.
    ///
    /// <para><b>Liveness is not the same as state.</b> The job pump and the console flush must run on
    /// <see cref="EditorApplication.update"/>, because they touch Unity APIs. The heartbeat must not:
    /// Unity throttles the update loop while the Editor is unfocused or busy importing, which used to
    /// make <c>cucli</c> report a perfectly healthy Editor as offline exactly when a terminal user
    /// needed it.</para>
    ///
    /// <para>So the main thread publishes an immutable <see cref="CucliHeartbeat"/> <em>snapshot</em>
    /// once a second, and a background timer re-publishes that snapshot with a fresh
    /// <c>timestampUtc</c>. Liveness therefore stays current no matter what the Editor is doing, while
    /// <c>stateSampledAtUtc</c> tells a caller how old the Unity state fields really are - the
    /// difference between "Unity is gone" and "Unity is busy".</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class CucliWorkerBootstrap
    {
        private const string SessionKey = "CustomUnityCli.Editor.SessionId";
        private const int HeartbeatIntervalMilliseconds = 1000;

        private static readonly string SessionId;

        /// <summary>Published by the main thread, read by the timer thread. Replaced, never mutated.</summary>
        private static volatile CucliHeartbeat s_snapshot;

        private static volatile bool s_initialized;
        private static volatile bool s_shuttingDown;

        private static Timer s_timer;
        private static DateTime s_nextRetryUtc;
        private static DateTime s_nextWarningUtc;
        private static DateTime s_nextStateSampleUtc;

        /// <summary>Last time the editor loop turned. Written by the main thread, read by the timer.</summary>
        private static volatile string s_mainThreadTickUtc;

        /// <summary>Set by <see cref="PendingWorkExists"/> on the timer thread, consumed by the main thread.</summary>
        private static volatile bool s_workPending;

        static CucliWorkerBootstrap()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;

            // Cache main-thread-only state before any background writer can exist.
            CucliPaths.WarmUp();

            SessionId = SessionState.GetString(SessionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(SessionId))
            {
                SessionId = Guid.NewGuid().ToString("N");
                SessionState.SetString(SessionKey, SessionId);
            }

            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            EditorApplication.quitting -= OnQuitting;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static string CurrentSessionId => SessionId;

        private static void Update()
        {
            s_mainThreadTickUtc = DateTime.UtcNow.ToString("O");
            if (DateTime.UtcNow < s_nextRetryUtc) return;

            try
            {
                if (!s_initialized)
                {
                    CucliPaths.EnsureDirectories();

                    // Say it out loud rather than let a Unity 6 user assume support: the assembly
                    // still compiles there thanks to forward-compatibility guards, so silence would
                    // read as a green light. See CucliSupport for the boundary itself.
                    CucliSupport.WarnIfUnsupported();

                    CucliCommandRegistry.Reload();
                    CucliCommandRegistry.WriteCatalog();
                    CucliConsoleCapture.Initialize(SessionId);
                    CucliJobProcessor.Initialize();
                    s_initialized = true;
                }

                CucliConsoleCapture.Flush();
                CucliJobProcessor.Tick();
                // The main thread has just drained whatever the timer thread was advertising, so the
                // next pending-work sighting is allowed to nudge again.
                s_workPending = false;

                if (DateTime.UtcNow >= s_nextStateSampleUtc)
                {
                    s_nextStateSampleUtc = DateTime.UtcNow.AddSeconds(1);
                    s_snapshot = SampleHeartbeat();
                    // Publish immediately on the main thread too, so a state transition is visible
                    // without waiting for the next timer tick.
                    PublishHeartbeat();
                }

                EnsureTimer();
            }
            catch (Exception exception)
            {
                // Back off, and rate-limit the warning so an intermittent IO fault cannot flood the
                // console - a successful frame must not reset the limiter.
                s_nextRetryUtc = DateTime.UtcNow.AddSeconds(5);
                if (DateTime.UtcNow < s_nextWarningUtc) return;
                s_nextWarningUtc = DateTime.UtcNow.AddSeconds(30);
                UnityEngine.Debug.LogWarning(
                    "[cucli] The Editor worker is unavailable and will retry in 5 seconds: " +
                    exception.GetBaseException().Message);
            }
        }

        private static void EnsureTimer()
        {
            if (s_timer != null || s_snapshot == null) return;
            s_timer = new Timer(
                _ => OnTimerTick(),
                null,
                HeartbeatIntervalMilliseconds,
                HeartbeatIntervalMilliseconds);
        }

        /// <summary>
        /// Runs on a background thread every heartbeat interval. Two jobs, in this order:
        ///
        /// 1. Publish a liveness heartbeat, which must not depend on the editor loop turning - that
        ///    is what stops "Unity is in the background" from looking like "Unity is gone".
        /// 2. If work is queued, wake the editor loop.
        ///
        /// Without step 2 the job pump only runs from <see cref="EditorApplication.update"/>, and
        /// Unity throttles that loop hard whenever the window is not focused. A CLI is used from CI,
        /// from a script and from an agent- exactly the situations where nobody is looking at the
        /// Editor - so a queued job could otherwise sit untouched until the user happened to click
        /// back into Unity, and the caller would see only a bare timeout.
        /// </summary>
        private static void OnTimerTick()
        {
            // The timer thread publishes the heartbeat and does nothing else.
            //
            // It must never touch a Unity API. An earlier revision called
            // EditorApplication.QueuePlayerLoopUpdate() from here to nudge a throttled Editor into
            // draining the queue, and that off-main-thread Editor call wedged the Editor's main loop
            // permanently: state sampling and Editor.log both stopped at the same instant, CPU stayed
            // at exactly 0% for minutes, and the Win32 message pump kept answering - a blocked main
            // thread, not a modal dialog and not focus throttling. A try/catch cannot make such a call
            // safe, because the failure mode is a corrupted native state, not a managed exception.
            //
            // Losing the nudge costs nothing that matters: the main thread drains the queue on its next
            // natural tick, and `stalled` in the heartbeat is what tells a caller that no tick is coming.
            PublishHeartbeat();
        }

        /// <summary>Cheap directory probe. Runs off the main thread, so it touches only the filesystem.</summary>
        private static bool PendingWorkExists()
        {
            try
            {
                return HasAnyJson(CucliPaths.Queued)
                    || HasAnyJson(CucliPaths.Control)
                    || HasAnyJson(CucliPaths.CancelRequests)
                    || HasAnyJson(CucliPaths.Waiting);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HasAnyJson(string directory)
        {
            try
            {
                return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// DO NOT CALL THIS. Kept only so the hazard is documented rather than rediscovered.
        ///
        /// It exists to ask the editor loop to turn once so a pending job is picked up without the
        /// user focusing the window - a real problem worth solving. This implementation solved it by
        /// calling <c>EditorApplication.QueuePlayerLoopUpdate()</c> off the main thread, which is not
        /// safe and did wedge the Editor permanently. Any future attempt must use a mechanism Unity
        /// documents as main-thread-only reachable, or must accept the wait.
        /// </summary>
        private static void NudgeMainLoop()
        {
            if (s_workPending) return;
            s_workPending = true;
            try
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception)
            {
                // Off-main-thread refusal is acceptable: the next natural tick still drains the queue.
                s_workPending = false;
            }
        }

        /// <summary>Builds the immutable state snapshot. Main thread only.</summary>
        private static CucliHeartbeat SampleHeartbeat()
        {
            Scene active = SceneManager.GetActiveScene();
            string sampledAt = DateTime.UtcNow.ToString("O");
            return new CucliHeartbeat
            {
                SessionId = SessionId,
                TimestampUtc = sampledAt,
                StateSampledAtUtc = sampledAt,
                ProjectPath = CucliPaths.ProjectRoot,
                ProjectName = Application.productName,
                UnityVersion = Application.unityVersion,
                ApplicationPath = EditorApplication.applicationPath,
                ProcessId = Process.GetCurrentProcess().Id,
                HasCompilationErrors = CucliCompilationStatus.HasErrors,
                IsCompiling = EditorApplication.isCompiling,
                IsUpdating = EditorApplication.isUpdating,
                IsPlaying = EditorApplication.isPlaying,
                IsPaused = EditorApplication.isPaused,
                IsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                IsBatchMode = Application.isBatchMode,
                ActiveScenePath = active.IsValid() ? active.path : string.Empty,
                OpenSceneCount = SceneManager.sceneCount,
                CommandCount = CucliCommandRegistry.Count,
                MainThreadTickAtUtc = s_mainThreadTickUtc,
                PendingJobCount = CountPendingJobs(),
                SupportedEditor = CucliSupport.IsSupportedEditor,
                UnsupportedEditorReason = CucliSupport.UnsupportedReason
            };
        }

        /// <summary>Cheap enough to run once a second, and it lets callers distinguish busy from idle.</summary>
        private static int CountPendingJobs()
        {
            return CountJson(CucliPaths.Queued)
                + CountJson(CucliPaths.Control)
                + CountJson(CucliPaths.CancelRequests)
                + CountJson(CucliPaths.Waiting);
        }

        private static int CountJson(string directory)
        {
            try
            {
                return Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, "*.json").Count()
                    : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Writes the current snapshot with a fresh timestamp. Safe to call from the timer thread:
        /// the snapshot is never mutated after publication, and <see cref="CucliPaths.WriteAtomic"/>
        /// needs no Unity API once <see cref="CucliPaths.WarmUp"/> has run.
        ///
        /// The snapshot is cloned through JSON and only the heartbeat's own timestamp is refreshed,
        /// rather than copying field by field. A field-by-field copy silently drops whatever was added
        /// to the model later - which is exactly how <c>MainThreadTickAtUtc</c>, the one honest
        /// liveness signal in this file, was missing from the published heartbeat while the main thread
        /// still looked perfectly healthy. Refreshing the timestamp is the only field this method is
        /// allowed to touch: everything else must keep the main thread's value, including its age.
        /// </summary>
        private static void PublishHeartbeat()
        {
            if (s_shuttingDown) return;
            CucliHeartbeat snapshot = s_snapshot;
            if (snapshot == null) return;

            try
            {
                JObject published = JObject.FromObject(
                    snapshot, JsonSerializer.Create(CucliJson.CompactSettings));
                published["timestampUtc"] = DateTime.UtcNow.ToString("O");

                CucliPaths.WriteAtomic(
                    CucliPaths.Heartbeat,
                    published.ToString(Formatting.Indented) + Environment.NewLine);
            }
            catch (Exception)
            {
                // An unhandled exception on a timer callback tears down the whole process, so this
                // catch is load-bearing, not defensive noise. A missed heartbeat expires on its own.
            }
        }

        /// <summary>Re-samples state immediately on a Play Mode transition, so the CLI observes the
        /// change it just requested without waiting up to a second.</summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            s_nextStateSampleUtc = DateTime.UtcNow;
        }

        private static void OnQuitting()
        {
            s_shuttingDown = true;

            Timer timer = s_timer;
            s_timer = null;
            if (timer != null)
            {
                try { timer.Dispose(); }
                catch (ObjectDisposedException) { }
            }

            CucliConsoleCapture.Shutdown();
            CucliJobProcessor.Shutdown();

            try
            {
                if (File.Exists(CucliPaths.Heartbeat)) File.Delete(CucliPaths.Heartbeat);
            }
            catch (IOException)
            {
                // A stale heartbeat expires automatically on the CLI side.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
