using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
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
            if (DateTime.UtcNow < s_nextRetryUtc) return;

            try
            {
                if (!s_initialized)
                {
                    CucliPaths.EnsureDirectories();
                    CucliCommandRegistry.Reload();
                    CucliCommandRegistry.WriteCatalog();
                    CucliConsoleCapture.Initialize(SessionId);
                    CucliJobProcessor.Initialize();
                    s_initialized = true;
                }

                CucliConsoleCapture.Flush();
                CucliJobProcessor.Tick();

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
                _ => PublishHeartbeat(),
                null,
                HeartbeatIntervalMilliseconds,
                HeartbeatIntervalMilliseconds);
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
                CommandCount = CucliCommandRegistry.Count
            };
        }

        /// <summary>
        /// Writes the current snapshot with a fresh timestamp. Safe to call from the timer thread:
        /// the snapshot is never mutated after publication, and <see cref="CucliPaths.WriteAtomic"/>
        /// needs no Unity API once <see cref="CucliPaths.WarmUp"/> has run.
        /// </summary>
        private static void PublishHeartbeat()
        {
            if (s_shuttingDown) return;
            CucliHeartbeat snapshot = s_snapshot;
            if (snapshot == null) return;

            try
            {
                var published = new CucliHeartbeat
                {
                    SchemaVersion = snapshot.SchemaVersion,
                    ProtocolVersion = snapshot.ProtocolVersion,
                    WorkerVersion = snapshot.WorkerVersion,
                    SessionId = snapshot.SessionId,
                    TimestampUtc = DateTime.UtcNow.ToString("O"),
                    StateSampledAtUtc = snapshot.StateSampledAtUtc,
                    ProjectPath = snapshot.ProjectPath,
                    ProjectName = snapshot.ProjectName,
                    UnityVersion = snapshot.UnityVersion,
                    ApplicationPath = snapshot.ApplicationPath,
                    ProcessId = snapshot.ProcessId,
                    HasCompilationErrors = snapshot.HasCompilationErrors,
                    IsCompiling = snapshot.IsCompiling,
                    IsUpdating = snapshot.IsUpdating,
                    IsPlaying = snapshot.IsPlaying,
                    IsPaused = snapshot.IsPaused,
                    IsPlayingOrWillChangePlaymode = snapshot.IsPlayingOrWillChangePlaymode,
                    IsBatchMode = snapshot.IsBatchMode,
                    ActiveScenePath = snapshot.ActiveScenePath,
                    OpenSceneCount = snapshot.OpenSceneCount,
                    CommandCount = snapshot.CommandCount
                };

                CucliPaths.WriteAtomic(CucliPaths.Heartbeat, CucliJson.Serialize(published));
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
