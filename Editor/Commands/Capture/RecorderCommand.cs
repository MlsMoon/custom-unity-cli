using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli recorder ...</c> - a recording session that outlives the frame it started in.
    ///
    /// The session is a <b>file</b>, not a static field: <c>Library/CustomUnityCli/capture/recorder.json</c>
    /// is rewritten after every captured frame and re-read by <see cref="CucliRecorderResume"/> when the
    /// Editor returns from a domain reload. That is the only way a recording survives the recompile a
    /// script edit triggers mid-session, and it is why the in-memory hook is a handle rather than state.
    ///
    /// <b>Two modes, and the results say which one ran.</b> <c>mode=video</c> drives the Unity Recorder
    /// package through reflection; when the package is absent that is an <c>OPTIONAL_PACKAGE_MISSING</c>
    /// failure. <c>mode=frames</c> is the always-available path: it samples frames to PNG through the same
    /// code the capture groups use. <c>mode=auto</c> picks frames and says so, naming what a real video
    /// would need, so a caller is never left thinking it recorded a movie.
    /// </summary>
    internal sealed class RecorderCommand : CucliCommand, ICucliDeferredCommand
    {
        internal const string SessionSchema = "cucli.recorder-session/v1";

        private const int MinIntervalMs = 16;
        private const int MaxIntervalMs = 10000;
        private const int MaxFrames = 600;
        private const string RecorderPackage = "com.unity.recorder";

        /// <summary>
        /// The live Recorder controller, when a video session is running.
        ///
        /// Deliberately a handle and not state: the session file remains the source of truth, and a managed
        /// controller cannot survive a domain reload, which is exactly why a video session says so up front.
        /// </summary>
        private static object s_videoController;

        public override string Name => "recorder";

        public override string Summary =>
            "Run a resumable capture session: record frames to PNG, or drive the Unity Recorder package for video when it is installed.";

        // 'status' is declared first so it is also the default action: the catalog's first action is what
        // the CLI sends when no action is named, and the only safe default for a recorder is the one that
        // cannot start writing frames by accident.
        public override CucliActionInfo[] Actions => Declare(
            "status", "Read the session record; works after a domain reload or an Editor restart.",
            "start", "Begin a session. Refuses if one is already recording.",
            "stop", "End the active session, write its manifest and report the summary. Idempotent.",
            "frame", "Capture exactly one frame now, inside the active session or on its own.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "filesystem.write", "editor.frame.read" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""status"", ""start"", ""stop"", ""frame""], ""default"": ""status"" },
    ""mode"": {
      ""type"": ""string"", ""enum"": [""auto"", ""frames"", ""video""], ""default"": ""auto"",
      ""description"": ""auto picks frames and reports why; video needs the Unity Recorder package.""
    },
    ""source"": { ""type"": ""string"", ""enum"": [""game"", ""sceneview"", ""scenecam""], ""default"": ""game"" },
    ""target"": { ""type"": ""string"", ""description"": ""Camera name or instance id for source=game."" },
    ""outputFolder"": { ""type"": ""string"", ""description"": ""Defaults to Assets/Docs~/Recordings."" },
    ""prefix"": { ""type"": ""string"", ""default"": ""rec"" },
    ""intervalMs"": { ""type"": ""integer"", ""minimum"": 16, ""maximum"": 10000, ""default"": 200 },
    ""maxFrames"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 600, ""default"": 120 },
    ""width"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
    ""height"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
    ""fps"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 120, ""default"": 30, ""description"": ""video: frame rate for the Recorder."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""frame: overwrite an existing PNG."" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "status"))
            {
                case "status": return Status(context);
                case "stop": return Stop(context);
                case "start":
                case "frame":
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "recorder '" + ResolveAction(context, "status")
                        + "' needs an Editor frame and must be dispatched through the deferred path.");
                default: return CucliCommandResult.Unsupported(ResolveAction(context, "status"), ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            switch (ResolveAction(context, "status"))
            {
                case "start": Start(context, complete); return;
                case "frame": SingleFrame(context, complete); return;
                default: complete(Execute(context)); return;
            }
        }

        // ------------------------------------------------------------------ session file

        internal static string SessionDirectory => Path.Combine(CucliPaths.Root, "capture");

        internal static string SessionFilePath => Path.Combine(SessionDirectory, "recorder.json");

        /// <summary>Reads the session record. The file is the single source of truth, never a static field.</summary>
        internal static JObject TryReadSession()
        {
            string path = SessionFilePath;
            if (!File.Exists(path)) return null;
            try
            {
                return CucliJson.ParseObject(File.ReadAllText(path, CucliJson.Utf8NoBom));
            }
            catch (IOException)
            {
                return null;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static void WriteSession(JObject session)
        {
            session["schemaVersion"] = SessionSchema;
            session["updatedAtUtc"] = DateTime.UtcNow.ToString("O");
            Directory.CreateDirectory(SessionDirectory);
            CucliPaths.WriteAtomic(
                SessionFilePath,
                session.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
        }

        internal static bool IsRecording(JObject session)
        {
            return session != null
                && string.Equals(
                    session["status"] == null ? null : session["status"].Value<string>(),
                    "recording",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static int CurrentProcessId()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ------------------------------------------------------------------ status

        private static CucliCommandResult Status(CucliCommandContext context)
        {
            JObject session = TryReadSession();
            if (session == null)
            {
                return CucliCommandResult.Ok(new
                {
                    hasSession = false,
                    sessionPath = CaptureSupport.ToProjectRelative(SessionFilePath),
                    recorderPackage = RecorderBridge.Describe(),
                    note = "No recorder session has been started in this project."
                }, "没有录制会话。");
            }

            bool recording = IsRecording(session);
            int recordedPid = session["unityPid"] == null ? 0 : session["unityPid"].Value<int>();
            bool ownPid = recordedPid == 0 || recordedPid == CurrentProcessId();

            if (recording && !ownPid)
            {
                context.Warn("The record belongs to Unity process " + recordedPid
                    + ", not this one, so it is not being resumed here.");
            }

            return CucliCommandResult.Ok(new
            {
                hasSession = true,
                sessionPath = CaptureSupport.ToProjectRelative(SessionFilePath),
                sessionId = session["sessionId"],
                status = session["status"],
                mode = session["mode"],
                folder = session["folder"],
                prefix = session["prefix"],
                source = session["source"],
                intervalMs = session["intervalMs"],
                maxFrames = session["maxFrames"],
                framesCaptured = session["framesCaptured"],
                startedAtUtc = session["startedAtUtc"],
                stoppedAtUtc = session["stoppedAtUtc"],
                updatedAtUtc = session["updatedAtUtc"],
                stopReason = session["stopReason"],
                unityPid = recordedPid,
                belongsToThisEditor = ownPid,
                resumeArmed = recording && ownPid,
                recorderPackage = RecorderBridge.Describe()
            }, recording ? "会话正在录制中。" : "会话已结束。");
        }

        // ------------------------------------------------------------------ start

        private static void Start(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;

            JObject existing = TryReadSession();
            if (IsRecording(existing))
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.WorkerBusy,
                    "A recording session is already active (started "
                    + (existing["startedAtUtc"] == null ? "unknown" : existing["startedAtUtc"].Value<string>())
                    + "). Stop it with 'cucli recorder stop' before starting another.",
                    new { sessionId = existing["sessionId"], folder = existing["folder"] }));
                return;
            }

            string mode = (args.GetString("mode") ?? "auto").Trim().ToLowerInvariant();
            if (mode != "auto" && mode != "frames" && mode != "video")
            {
                complete(CucliCommandResult.Invalid("mode", "'" + mode + "' is not auto, frames or video."));
                return;
            }

            bool recorderAvailable = RecorderBridge.IsAvailable;
            string degradationNote = null;

            if (mode == "video" && !recorderAvailable)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.OptionalPackageMissing,
                    "mode=video needs the Unity Recorder package ('" + RecorderPackage + "'), which is not installed. "
                    + "Install it from Window > Package Manager, or use mode=frames, which records a PNG sequence "
                    + "that cucli fully controls. " + RecorderBridge.Describe().ToString(Newtonsoft.Json.Formatting.None),
                    new { requestedMode = mode, availableMode = "frames", recorder = RecorderBridge.Describe() }));
                return;
            }

            if (mode == "auto")
            {
                mode = "frames";
                degradationNote = recorderAvailable
                    ? "mode=auto used the PNG frame sequence even though the Recorder package is installed; pass "
                      + "mode=video explicitly to drive the Recorder."
                    : "mode=auto used the PNG frame sequence because the Unity Recorder package ('" + RecorderPackage
                      + "') is not installed, so no video file can be produced. The frames are written as numbered "
                      + "PNGs plus a manifest and can be encoded afterwards.";
            }
            else if (mode == "frames")
            {
                degradationNote = recorderAvailable
                    ? null
                    : "Recording as a PNG frame sequence: the Unity Recorder package is not installed, so no video "
                      + "file is produced.";
            }

            string source = (args.GetString("source") ?? "game").Trim().ToLowerInvariant();
            if (source != "game" && source != "sceneview" && source != "scenecam")
            {
                complete(CucliCommandResult.Invalid("source", "'" + source + "' is not game, sceneview or scenecam."));
                return;
            }

            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveOutputFolder(
                    args, CaptureSupport.DefaultRecordingFolder,
                    out folderRelative, out folderAbsolute, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            int intervalMs = Mathf.Clamp(
                args.GetInt("intervalMs", args.GetInt("interval_ms", 200)), MinIntervalMs, MaxIntervalMs);
            int maxFrames = Mathf.Clamp(
                args.GetInt("maxFrames", args.GetInt("max_frames", 120)), 1, MaxFrames);

            // A session owns its own folder so two runs in one project can never interleave files.
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string sessionFolderRelative = folderRelative.TrimEnd('/') + "/run-" + stamp;
            string sessionFolderAbsolute = Path.Combine(folderAbsolute, "run-" + stamp);

            try
            {
                Directory.CreateDirectory(sessionFolderAbsolute);
            }
            catch (Exception exception)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Could not create the session folder: " + exception.GetBaseException().Message));
                return;
            }

            // Resolve and pin the capture target now, so a session started against a camera keeps recording
            // that camera even if the scene changes underneath it.
            string cameraName = null;
            int width = args.GetInt("width", 0);
            int height = args.GetInt("height", 0);

            if (source == "game")
            {
                Camera camera;
                if (!ResolveRecordingCamera(args, out camera, out error))
                {
                    complete(CucliCommandResult.Fail(CucliErrors.TargetNotFound, error));
                    return;
                }

                cameraName = camera.name;
                if (width <= 0) width = camera.pixelWidth > 0 ? camera.pixelWidth : 1280;
                if (height <= 0) height = camera.pixelHeight > 0 ? camera.pixelHeight : 720;
            }
            else if (source == "scenecam")
            {
                Camera camera;
                if (!CaptureSupport.TryResolveSceneCamera(out camera, out error))
                {
                    complete(CucliCommandResult.Fail(CucliErrors.TargetNotFound, error));
                    return;
                }

                cameraName = camera.name;
                if (width <= 0) width = camera.pixelWidth > 0 ? camera.pixelWidth : 1280;
                if (height <= 0) height = camera.pixelHeight > 0 ? camera.pixelHeight : 720;
            }
            else
            {
                SceneView view = SceneView.lastActiveSceneView;
                if (view == null)
                {
                    var open = CaptureSupport.OpenSceneViews();
                    if (open.Count > 0) view = open[0];
                }

                if (view == null || view.camera == null)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, "source=sceneview needs an open Scene view with a camera."));
                    return;
                }

                cameraName = view.camera.name;
                if (width <= 0) width = view.camera.pixelWidth > 0 ? view.camera.pixelWidth : 1280;
                if (height <= 0) height = view.camera.pixelHeight > 0 ? view.camera.pixelHeight : 720;
            }

            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);

            var session = new JObject
            {
                ["sessionId"] = Guid.NewGuid().ToString("N"),
                ["status"] = "recording",
                ["mode"] = mode,
                ["folder"] = sessionFolderRelative,
                ["prefix"] = args.GetString("prefix") ?? "rec",
                ["source"] = source,
                ["cameraName"] = cameraName,
                ["intervalMs"] = intervalMs,
                ["maxFrames"] = maxFrames,
                ["width"] = width,
                ["height"] = height,
                ["framesCaptured"] = 0,
                ["startedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["unityPid"] = CurrentProcessId(),
                ["editorSessionId"] = CucliWorkerBootstrap.CurrentSessionId,
                ["startFrame"] = Time.frameCount,
                ["startTime"] = Time.time,
                ["pipeline"] = CaptureSupport.PipelineJson(),
                ["captures"] = new JArray(),
                ["errors"] = new JArray()
            };

            if (mode == "video")
            {
                object controller;
                string videoError;
                if (!RecorderBridge.TryStart(sessionFolderAbsolute, args.GetString("prefix") ?? "rec",
                        width, height, Mathf.Clamp(args.GetInt("fps", 30), 1, 120), out controller, out videoError))
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "The Recorder package is installed but cucli could not drive it: " + videoError
                        + " Use mode=frames, which cucli controls directly.",
                        new { recorder = RecorderBridge.Describe() }));
                    return;
                }

                s_videoController = controller;
                session["videoDriven"] = true;
                session["fps"] = Mathf.Clamp(args.GetInt("fps", 30), 1, 120);
                WriteSession(session);

                context.AddArtifact(sessionFolderAbsolute);
                context.Warn("A video recording cannot survive a domain reload: the Recorder controller is a managed "
                    + "object and Unity destroys it when assemblies reload, so editing a script ends the recording. "
                    + "For a session that keeps running through recompiles, use mode=frames.");

                complete(CucliCommandResult.Ok(new
                {
                    sessionId = session["sessionId"],
                    mode = "video",
                    folder = sessionFolderRelative,
                    source,
                    fps = session["fps"],
                    resolution = new { width, height },
                    sessionPath = CaptureSupport.ToProjectRelative(SessionFilePath),
                    recorderAvailable = true,
                    resumableAcrossReload = false,
                    note = "'cucli recorder stop' finalises the file. A domain reload ends it early, which is a "
                        + "property of the Recorder rather than of this command."
                }, "视频录制会话已开始。"));
                return;
            }

            WriteSession(session);
            CucliRecorderResume.ArmFromSession(session);

            context.AddArtifact(sessionFolderAbsolute);

            if (!string.IsNullOrWhiteSpace(degradationNote)) context.Warn(degradationNote);

            complete(CucliCommandResult.Ok(new
            {
                sessionId = session["sessionId"],
                mode,
                folder = sessionFolderRelative,
                source,
                intervalMs,
                maxFrames,
                resolution = new { width, height },
                sessionPath = CaptureSupport.ToProjectRelative(SessionFilePath),
                recorderAvailable,
                degradation = degradationNote,
                note = "Sampling continues in the background and survives a domain reload. intervalMs is a minimum wait "
                    + "between grabs and excludes the pixel read and PNG encode cost, so read the timestamps in the "
                    + "manifest rather than assuming a fixed frame step."
            }, degradationNote == null ? "录制会话已开始。" : "录制会话已开始（已降级为 PNG 帧序列）。"));
        }

        private static bool ResolveRecordingCamera(CucliArgs args, out Camera camera, out string error)
        {
            string identity = args.GetString("target") ?? args.GetString("camera");
            if (!string.IsNullOrWhiteSpace(identity))
            {
                return CaptureSupport.TryResolveCamera(identity, out camera, out error);
            }

            camera = Camera.main;
            if (camera != null)
            {
                error = null;
                return true;
            }

            foreach (Camera candidate in CaptureSupport.Cameras())
            {
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                if (candidate.cameraType == CameraType.SceneView) continue;
                if (candidate.cameraType == CameraType.Preview) continue;
                camera = candidate;
                error = null;
                return true;
            }

            error = "No enabled camera in the loaded scenes. Pass --target <name|instanceId>, or use source=sceneview.";
            return false;
        }

        // ------------------------------------------------------------------ stop

        /// <summary>
        /// Ends the session. Idempotent by design: stopping something already stopped reports the current
        /// state as a success, because a retried stop is usually a caller that lost track, not an error.
        /// </summary>
        private static CucliCommandResult Stop(CucliCommandContext context)
        {
            JObject session = TryReadSession();
            if (session == null)
            {
                return CucliCommandResult.Ok(
                    new { stopped = false, hasSession = false },
                    "没有可停止的录制会话。");
            }

            bool wasRecording = IsRecording(session);
            CucliRecorderResume.Disarm();

            string videoStopNote = null;
            bool videoMode = string.Equals(
                session["mode"] == null ? null : session["mode"].Value<string>(),
                "video",
                StringComparison.OrdinalIgnoreCase);

            if (wasRecording && videoMode)
            {
                if (s_videoController != null)
                {
                    string videoError;
                    if (RecorderBridge.Stop(s_videoController, out videoError))
                    {
                        videoStopNote = "The Recorder was stopped and its file finalised.";
                    }
                    else
                    {
                        videoStopNote = "The Recorder could not be stopped cleanly: " + videoError;
                        context.Warn(videoStopNote);
                    }

                    s_videoController = null;
                }
                else
                {
                    // Expected after a domain reload: the controller is a managed object and did not survive it.
                    videoStopNote = "No live Recorder controller was attached, which is what happens when the Editor "
                        + "reloaded during the recording. Whatever the Recorder had written is still on disk, but cucli "
                        + "did not finalise it.";
                    context.Warn(videoStopNote);
                }
            }

            if (!wasRecording)
            {
                return CucliCommandResult.Ok(new
                {
                    stopped = false,
                    alreadyStopped = true,
                    sessionId = session["sessionId"],
                    status = session["status"],
                    framesCaptured = session["framesCaptured"]
                }, "会话此前已结束，未做改动。");
            }

            session["status"] = "stopped";
            session["stoppedAtUtc"] = DateTime.UtcNow.ToString("O");
            session["stopReason"] = "requested";
            session["endFrame"] = Time.frameCount;
            session["endTime"] = Time.time;
            WriteSession(session);

            string folderRelative = session["folder"] == null ? null : session["folder"].Value<string>();
            string manifestPath = null;

            if (!string.IsNullOrWhiteSpace(folderRelative))
            {
                string folderAbsolute = Path.Combine(
                    CucliPaths.ProjectRoot, folderRelative.Replace('/', Path.DirectorySeparatorChar));

                var manifest = new JObject
                {
                    ["action"] = "recorder.session",
                    ["sessionId"] = session["sessionId"],
                    ["mode"] = session["mode"],
                    ["source"] = session["source"],
                    ["intervalMs"] = session["intervalMs"],
                    ["requested"] = session["maxFrames"],
                    ["written"] = session["framesCaptured"],
                    ["failures"] = session["errors"] == null ? 0 : ((JArray)session["errors"]).Count,
                    ["startedAtUtc"] = session["startedAtUtc"],
                    ["stoppedAtUtc"] = session["stoppedAtUtc"],
                    ["startFrame"] = session["startFrame"],
                    ["endFrame"] = session["endFrame"],
                    ["startTime"] = session["startTime"],
                    ["endTime"] = session["endTime"],
                    ["cameraName"] = session["cameraName"],
                    ["pipeline"] = session["pipeline"],
                    ["captures"] = session["captures"] ?? new JArray(),
                    ["timingNote"] = "Timestamps come from the per-frame records below; the interval is a minimum wait "
                        + "and does not include read or encode cost."
                };

                string manifestError;
                if (CaptureSupport.TryWriteManifest(
                        folderAbsolute, null, manifest, true, out manifestPath, out manifestError))
                {
                    context.AddArtifact(manifestPath);
                }
                else
                {
                    context.Warn(manifestError);
                }
            }

            int frames = session["framesCaptured"] == null ? 0 : session["framesCaptured"].Value<int>();
            var errors = session["errors"] as JArray;
            if (errors != null && errors.Count > 0)
            {
                context.Warn(errors.Count + " frame(s) failed during the session; the manifest records each one.");
            }

            return CucliCommandResult.Ok(new
            {
                stopped = true,
                alreadyStopped = false,
                sessionId = session["sessionId"],
                mode = session["mode"],
                folder = folderRelative,
                framesCaptured = frames,
                failures = errors == null ? 0 : errors.Count,
                manifestPath = manifestPath == null ? null : CaptureSupport.ToProjectRelative(manifestPath),
                videoStopNote
            }, "录制会话已结束，共 " + frames + " 帧。");
        }

        // ------------------------------------------------------------------ single frame

        private static void SingleFrame(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            JObject session = TryReadSession();
            if (IsRecording(session))
            {
                string error;
                if (!CucliRecorderResume.CaptureOne(session, out error))
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, error, new { sessionId = session["sessionId"] }));
                    return;
                }

                // The session record now carries the new path; surface it as an artefact too.
                string last = session["lastFramePath"] == null ? null : session["lastFramePath"].Value<string>();
                if (!string.IsNullOrWhiteSpace(last))
                {
                    context.AddArtifact(Path.Combine(
                        CucliPaths.ProjectRoot, last.Replace('/', Path.DirectorySeparatorChar)));
                }

                complete(CucliCommandResult.Ok(new
                {
                    sessionId = session["sessionId"],
                    folder = session["folder"],
                    framesCaptured = session["framesCaptured"],
                    lastFramePath = last
                }, "已在会话中抓取一帧。"));
                return;
            }

            AdHocFrame(context, complete);
        }

        /// <summary>
        /// One frame with no session. Shares the capture groups' code path so "a single frame" cannot mean
        /// two different things depending on which group asked for it.
        /// </summary>
        private static void AdHocFrame(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;

            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveOutputFolder(
                    args, CaptureSupport.DefaultRecordingFolder,
                    out folderRelative, out folderAbsolute, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            string prefix = args.GetString("prefix") ?? "rec";
            if (!CaptureSupport.TryValidatePrefix(prefix, out error))
            {
                complete(CucliCommandResult.Invalid("prefix", error));
                return;
            }

            string source = (args.GetString("source") ?? "game").Trim().ToLowerInvariant();
            Camera camera = null;
            SceneView view = null;

            if (source == "sceneview")
            {
                view = SceneView.lastActiveSceneView;
                if (view == null)
                {
                    var open = CaptureSupport.OpenSceneViews();
                    if (open.Count > 0) view = open[0];
                }

                if (view == null || view.camera == null)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        "source=sceneview needs an open Scene view with a camera; open one or use source=game."));
                    return;
                }
            }
            else if (source == "scenecam")
            {
                if (!CaptureSupport.TryResolveSceneCamera(out camera, out error))
                {
                    complete(CucliCommandResult.Fail(CucliErrors.TargetNotFound, error));
                    return;
                }
            }
            else if (source == "game")
            {
                if (!ResolveRecordingCamera(args, out camera, out error))
                {
                    complete(CucliCommandResult.Fail(CucliErrors.TargetNotFound, error));
                    return;
                }
            }
            else
            {
                complete(CucliCommandResult.Invalid("source", "'" + source + "' is not game, sceneview or scenecam."));
                return;
            }

            Camera active = source == "sceneview" ? view.camera : camera;
            int width = Mathf.Clamp(args.GetInt("width", active.pixelWidth > 0 ? active.pixelWidth : 1280), 64, 4096);
            int height = Mathf.Clamp(args.GetInt("height", active.pixelHeight > 0 ? active.pixelHeight : 720), 64, 4096);

            string absolutePath;
            string relativePath;
            string resolveError;
            if (!CaptureSupport.TryResolveFilePath(
                    folderAbsolute, prefix, "frame", ".png", 0, args.GetBool("force", false),
                    out absolutePath, out relativePath, out resolveError))
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.AssetExists, resolveError, new { path = relativePath }));
                return;
            }

            bool ok = source == "sceneview"
                ? CaptureSupport.TryCaptureSceneView(width, height, absolutePath, out error)
                : (source == "scenecam"
                    ? CaptureFromExistingTexture(camera, absolutePath, out error)
                    : CaptureSupport.TryRenderCameraToPng(camera, width, height, absolutePath, out error));

            if (!ok)
            {
                complete(CucliCommandResult.Fail(CucliErrors.CommandFailed, error));
                return;
            }

            SessionImageIndex.Append(absolutePath, "recorder.frame." + source);
            context.AddArtifact(absolutePath);

            complete(CucliCommandResult.Ok(new
            {
                path = relativePath,
                folder = folderRelative,
                source,
                width,
                height,
                bytes = File.Exists(absolutePath) ? new FileInfo(absolutePath).Length : 0,
                capturedFrame = Time.frameCount,
                time = Time.time,
                camera = active == null ? null : CaptureSupport.CameraJson(active)
            }, "已抓取单帧（相机渲染，不含 Screen Space Overlay UI）。"));
        }

        private static bool CaptureFromExistingTexture(Camera camera, string absolutePath, out string error)
        {
            Texture2D texture;
            if (!CaptureSupport.TryReadCameraTexture(camera, out texture, out error)) return false;
            try
            {
                return CaptureSupport.TryWritePng(texture, absolutePath, out error);
            }
            finally
            {
                CaptureSupport.Destroy(texture);
            }
        }
    }

    /// <summary>
    /// Reflective access to the Unity Recorder package.
    ///
    /// The package is optional, so nothing here may be a compile-time reference; and its controller API is
    /// version specific, so every member is located by name and a miss is reported as the exact member that
    /// was absent rather than as a generic failure. That is what lets <c>mode=video</c> say something useful
    /// in a project where the package exists but has moved on.
    /// </summary>
    internal static class RecorderBridge
    {
        private const string PackageName = "com.unity.recorder";
        private const string SettingsType = "UnityEditor.Recorder.RecorderControllerSettings";
        private const string ControllerType = "UnityEditor.Recorder.RecorderController";
        private const string MovieType = "UnityEditor.Recorder.MovieRecorderSettings";

        internal static bool IsAvailable
        {
            get
            {
                return CucliOptional.IsPackageInstalled(PackageName)
                    && Resolve(SettingsType) != null
                    && Resolve(ControllerType) != null;
            }
        }

        internal static JObject Describe()
        {
            Type settings = Resolve(SettingsType);
            Type controller = Resolve(ControllerType);
            Type movie = Resolve(MovieType);

            return new JObject
            {
                ["package"] = PackageName,
                ["packageInstalled"] = CucliOptional.IsPackageInstalled(PackageName),
                ["packageVersion"] = CucliOptional.PackageVersion(PackageName),
                ["controllerSettingsType"] = settings == null ? null : settings.FullName,
                ["controllerType"] = controller == null ? null : controller.FullName,
                ["movieRecorderSettingsType"] = movie == null ? null : movie.FullName,
                ["drivable"] = IsAvailable,
                ["note"] = "Driving the Recorder from outside its window is version specific; this command verifies "
                    + "the API by name and reports the first member it could not find instead of assuming."
            };
        }

        internal static bool TryStart(
            string folderAbsolute,
            string prefix,
            int width,
            int height,
            int fps,
            out object controllerInstance,
            out string error)
        {
            controllerInstance = null;
            error = null;

            Type settingsType = Resolve(SettingsType);
            Type controllerType = Resolve(ControllerType);
            Type movieType = Resolve(MovieType);

            if (settingsType == null || controllerType == null || movieType == null)
            {
                error = "RecorderControllerSettings, RecorderController or MovieRecorderSettings was not found.";
                return false;
            }

            try
            {
                object settings = Activator.CreateInstance(settingsType);
                object movie = Activator.CreateInstance(movieType);

                string fileName = prefix.Trim().Trim('-') + ".mp4";
                string outputFile = Path.Combine(folderAbsolute, fileName).Replace('\\', '/');

                if (!TrySet(movie, "OutputFile", outputFile, out error)) return false;
                if (!TrySet(movie, "enabled", true, out error) && error != null) return false;

                // FrameRate lives on the settings object, not on the recorder; set whichever exists.
                string frameRateError;
                if (!TrySet(settings, "FrameRate", fps, out frameRateError) && frameRateError != null)
                {
                    error = frameRateError;
                    return false;
                }

                MethodInfo addRecorder = settingsType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "AddRecorder" && method.GetParameters().Length == 1);
                if (addRecorder == null)
                {
                    error = "RecorderControllerSettings.AddRecorder was not found; this Recorder version lays out its "
                        + "API differently.";
                    return false;
                }

                addRecorder.Invoke(settings, new[] { movie });

                controllerInstance = Activator.CreateInstance(controllerType, new[] { settings });

                MethodInfo prepare = controllerType.GetMethod("PrepareRecording", Type.EmptyTypes);
                MethodInfo start = controllerType.GetMethod("StartRecording", Type.EmptyTypes);
                if (prepare == null || start == null)
                {
                    error = "RecorderController.PrepareRecording or StartRecording was not found.";
                    controllerInstance = null;
                    return false;
                }

                prepare.Invoke(controllerInstance, null);
                start.Invoke(controllerInstance, null);
                return true;
            }
            catch (Exception exception)
            {
                controllerInstance = null;
                error = (exception as TargetInvocationException)?.InnerException != null
                    ? exception.InnerException.GetBaseException().Message
                    : exception.GetBaseException().Message;
                return false;
            }
        }

        internal static bool Stop(object controller, out string error)
        {
            error = null;
            if (controller == null) return false;

            try
            {
                MethodInfo stop = controller.GetType().GetMethod("StopRecording", Type.EmptyTypes);
                if (stop == null)
                {
                    error = "RecorderController.StopRecording was not found.";
                    return false;
                }

                stop.Invoke(controller, null);
                return true;
            }
            catch (Exception exception)
            {
                error = (exception as TargetInvocationException)?.InnerException != null
                    ? exception.InnerException.GetBaseException().Message
                    : exception.GetBaseException().Message;
                return false;
            }
        }

        private static bool TrySet(object target, string member, object value, out string error)
        {
            error = null;
            if (!CucliOptional.SetProperty(target, member, value))
            {
                FieldInfo field = target.GetType().GetField(member, BindingFlags.Public | BindingFlags.Instance);
                if (field == null)
                {
                    error = target.GetType().Name + "." + member + " was not found, so the Recorder could not be "
                        + "configured.";
                    return false;
                }

                try
                {
                    field.SetValue(target, value);
                    return true;
                }
                catch (Exception exception)
                {
                    error = "Could not set " + target.GetType().Name + "." + member + ": "
                        + exception.GetBaseException().Message;
                    return false;
                }
            }

            return true;
        }

        private static Type Resolve(string fullName)
        {
            return CucliOptional.FindTypesByFullName(fullName).FirstOrDefault();
        }
    }

    /// <summary>
    /// Re-arms an in-flight recording after a domain reload or an Editor restart.
    ///
    /// The session file decides; this class only attaches the tick handler. A session belonging to a
    /// different Unity process is left alone, so opening a second Editor on the same project cannot make it
    /// write frames into somebody else's run.
    /// </summary>
    [InitializeOnLoad]
    internal static class CucliRecorderResume
    {
        private static EditorApplication.CallbackFunction s_hook;
        private static string s_armedSessionId;
        private static double s_nextCaptureAtSeconds;

        static CucliRecorderResume()
        {
            EditorApplication.delayCall += TryResume;
            EditorApplication.quitting += Disarm;
        }

        internal static void TryResume()
        {
            JObject session = RecorderCommand.TryReadSession();
            if (!RecorderCommand.IsRecording(session)) return;

            // A video session must not be re-armed: the Recorder controller is a managed object, so after a
            // reload there is nothing left to resume. Marking it stopped with a reason is the honest outcome -
            // silently pretending to record would produce an empty session that looks successful.
            if (string.Equals(
                    session["mode"] == null ? null : session["mode"].Value<string>(),
                    "video",
                    StringComparison.OrdinalIgnoreCase))
            {
                session["status"] = "stopped";
                session["stoppedAtUtc"] = DateTime.UtcNow.ToString("O");
                session["stopReason"] = "recorderLostOnReload";
                RecorderCommand.WriteSession(session);
                return;
            }

            int recordedPid = session["unityPid"] == null ? 0 : session["unityPid"].Value<int>();
            if (recordedPid != 0 && recordedPid != RecorderCommand.CurrentProcessId())
            {
                // Another Editor owns this recording; leave it untouched.
                return;
            }

            if (!string.Equals(
                    session["editorSessionId"] == null ? null : session["editorSessionId"].Value<string>(),
                    CucliWorkerBootstrap.CurrentSessionId,
                    StringComparison.Ordinal))
            {
                session["editorSessionId"] = CucliWorkerBootstrap.CurrentSessionId;
                session["resumedAtUtc"] = DateTime.UtcNow.ToString("O");
                RecorderCommand.WriteSession(session);
            }

            ArmFromSession(session);
        }

        internal static void ArmFromSession(JObject session)
        {
            if (!RecorderCommand.IsRecording(session)) return;

            string sessionId = session["sessionId"] == null ? null : session["sessionId"].Value<string>();
            if (s_hook != null && string.Equals(s_armedSessionId, sessionId, StringComparison.Ordinal))
            {
                s_nextCaptureAtSeconds = EditorApplication.timeSinceStartup;
                return;
            }

            Disarm();
            s_armedSessionId = sessionId;
            s_nextCaptureAtSeconds = EditorApplication.timeSinceStartup;

            s_hook = () =>
            {
                // Re-read every tick: another process, or 'recorder stop', may have ended it.
                JObject current = RecorderCommand.TryReadSession();
                if (!RecorderCommand.IsRecording(current))
                {
                    Disarm();
                    return;
                }

                if (EditorApplication.timeSinceStartup < s_nextCaptureAtSeconds) return;

                int intervalMs = current["intervalMs"] == null ? 200 : current["intervalMs"].Value<int>();
                s_nextCaptureAtSeconds =
                    EditorApplication.timeSinceStartup + Mathf.Max(MinIntervalSeconds, intervalMs / 1000f);

                string error;
                if (!CaptureOne(current, out error))
                {
                    var errors = current["errors"] as JArray ?? new JArray();
                    errors.Add(new JObject
                    {
                        ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                        ["message"] = error
                    });
                    current["errors"] = errors;
                    RecorderCommand.WriteSession(current);
                }

                int maxFrames = current["maxFrames"] == null ? 120 : current["maxFrames"].Value<int>();
                int frames = current["framesCaptured"] == null ? 0 : current["framesCaptured"].Value<int>();
                if (frames < maxFrames) return;

                current["status"] = "stopped";
                current["stoppedAtUtc"] = DateTime.UtcNow.ToString("O");
                current["stopReason"] = "maxFramesReached";
                current["endFrame"] = Time.frameCount;
                current["endTime"] = Time.time;
                RecorderCommand.WriteSession(current);
                Disarm();
            };

            EditorApplication.update += s_hook;
        }

        private const float MinIntervalSeconds = 0.016f;

        internal static void Disarm()
        {
            if (s_hook != null)
            {
                EditorApplication.update -= s_hook;
                s_hook = null;
            }

            s_armedSessionId = null;
        }

        /// <summary>
        /// Captures one frame into the session folder and rewrites the session record.
        ///
        /// The record is written after every frame so an Editor crash loses at most one frame's bookkeeping,
        /// while the PNGs already on disk remain the evidence.
        /// </summary>
        internal static bool CaptureOne(JObject session, out string error)
        {
            error = null;
            string folderRelative = session["folder"] == null ? null : session["folder"].Value<string>();
            if (string.IsNullOrWhiteSpace(folderRelative))
            {
                error = "The session record has no folder, so the frame has nowhere to go.";
                return false;
            }

            string folderAbsolute = Path.Combine(
                CucliPaths.ProjectRoot, folderRelative.Replace('/', Path.DirectorySeparatorChar));
            string source = session["source"] == null ? "game" : session["source"].Value<string>();
            int width = session["width"] == null ? 1280 : session["width"].Value<int>();
            int height = session["height"] == null ? 720 : session["height"].Value<int>();
            string prefix = session["prefix"] == null ? "rec" : session["prefix"].Value<string>();
            int index = session["framesCaptured"] == null ? 0 : session["framesCaptured"].Value<int>();

            Camera camera = null;
            SceneView view = null;

            if (source == "sceneview")
            {
                view = SceneView.lastActiveSceneView;
                if (view == null)
                {
                    var open = CaptureSupport.OpenSceneViews();
                    if (open.Count > 0) view = open[0];
                }

                if (view == null || view.camera == null)
                {
                    error = "source=sceneview but no Scene view camera is available right now.";
                    return false;
                }
            }
            else
            {
                string name = session["cameraName"] == null ? null : session["cameraName"].Value<string>();
                camera = CaptureSupport.Cameras()
                    .FirstOrDefault(candidate => candidate != null && candidate.name == name)
                    ?? Camera.main;

                if (camera == null)
                {
                    error = "The recorded camera '" + name + "' is gone and there is no Camera.main to fall back to.";
                    return false;
                }
            }

            string absolute = Path.Combine(
                folderAbsolute, prefix.Trim().Trim('-') + "-" + index.ToString("D5", CultureInfo.InvariantCulture) + ".png");

            try
            {
                bool ok;
                if (source == "sceneview")
                {
                    ok = CaptureSupport.TryCaptureSceneView(width, height, absolute, out error);
                }
                else if (source == "scenecam")
                {
                    // Reads what the project already rendered rather than re-rendering it, so the sample is
                    // the real output of the game.
                    Texture2D texture;
                    ok = CaptureSupport.TryReadCameraTexture(camera, out texture, out error);
                    if (ok)
                    {
                        try
                        {
                            ok = CaptureSupport.TryWritePng(texture, absolute, out error);
                        }
                        finally
                        {
                            CaptureSupport.Destroy(texture);
                        }
                    }
                }
                else
                {
                    ok = CaptureSupport.TryRenderCameraToPng(camera, width, height, absolute, out error);
                }

                if (!ok) return false;

                var captures = session["captures"] as JArray ?? new JArray();
                captures.Add(new JObject
                {
                    ["index"] = index,
                    ["frameCount"] = Time.frameCount,
                    ["time"] = Time.time,
                    ["realtimeSinceStartup"] = Time.realtimeSinceStartup,
                    ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                    ["path"] = CaptureSupport.ToProjectRelative(absolute).Replace('\\', '/')
                });

                session["captures"] = captures;
                session["framesCaptured"] = index + 1;
                session["lastFramePath"] = CaptureSupport.ToProjectRelative(absolute).Replace('\\', '/');
                session["lastFrameAtUtc"] = DateTime.UtcNow.ToString("O");
                RecorderCommand.WriteSession(session);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }
    }
}
