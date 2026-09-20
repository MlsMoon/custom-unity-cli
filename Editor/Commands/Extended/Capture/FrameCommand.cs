using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli frame ...</c> - frame state inspection and timed capture sequences.
    ///
    /// Both actions are deferred because both need real Editor frames: <c>inspect</c> has to watch
    /// render callbacks to say whether a camera rendered, and <c>capture</c> samples over time.
    ///
    /// The source distinction is the part worth understanding. <c>scenecam</c> and <c>camera</c> read a
    /// render texture the game already produced and never render anything - re-rendering would change
    /// the thing being measured. <c>sceneview</c> is the only source that renders on demand, and it puts
    /// the Scene view camera's target texture and the active render texture back exactly as it found them.
    /// </summary>
    internal sealed class FrameCommand : CucliCommand, ICucliDeferredCommand
    {
        private const int MaxCount = 60;
        private const int MaxIntervalMs = 10000;
        private const long MaxTotalIntervalMs = 60000;
        private const int DefaultObservationMs = 100;

        public override string Name => "frame";

        public override CucliSurface Surface => CucliSurface.Extended;

        public override string Summary =>
            "Inspect frame, scene and camera render state over a real observation window, and capture one or many frames to Assets/Docs~.";

        public override CucliActionInfo[] Actions => Declare(
            "inspect", "Scene, play/pause, time scale, cameras with projection and clip planes, active render textures, and which cameras actually rendered.",
            "capture", "Write one frame or a timed sequence from the project's Scene camera, a named camera, or the Scene view, plus a manifest.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "editor.frame.read", "filesystem.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""inspect"", ""capture""], ""default"": ""inspect"" },
    ""observeMs"": {
      ""type"": ""integer"", ""minimum"": 0, ""maximum"": 5000, ""default"": 100,
      ""description"": ""inspect: how long to watch real render callbacks. 0 skips observation entirely.""
    },
    ""folder"": {
      ""type"": ""string"",
      ""description"": ""capture: a single folder name under Assets/Docs~, given as <date>_<feature> (e.g. 0915_渲染分析). A bare feature name is auto-dated.""
    },
    ""source"": {
      ""type"": ""string"", ""enum"": [""all"", ""scenecam"", ""camera"", ""sceneview""], ""default"": ""all"",
      ""description"": ""capture: all = scenecam + sceneview. scenecam/camera read an existing render texture; sceneview renders on demand.""
    },
    ""camera"": { ""type"": ""string"", ""description"": ""capture: camera name or instance id, required when source=camera."" },
    ""prefix"": { ""type"": ""string"", ""description"": ""capture: file name prefix. Must not contain a path separator or '..'."" },
    ""count"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 60, ""default"": 1, ""description"": ""capture: number of frames. Above 1 each frame is numbered."" },
    ""intervalMs"": {
      ""type"": ""integer"", ""minimum"": 0, ""maximum"": 10000, ""default"": 250,
      ""description"": ""capture: minimum wait between grabs. count-1 times this must not exceed 60000.""
    },
    ""width"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096, ""default"": 1920, ""description"": ""capture: only used by source=sceneview."" },
    ""height"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096, ""default"": 1080, ""description"": ""capture: only used by source=sceneview."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""capture: overwrite existing PNGs and manifest instead of refusing."" },
    ""includeCameras"": { ""type"": ""boolean"", ""default"": true, ""description"": ""inspect: include the per-camera array."" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "inspect"))
            {
                case "inspect":
                    // Only the zero-window inspection can be answered without waiting for an Editor frame.
                    if (context.Input.GetInt("observeMs", DefaultObservationMs) <= 0) return Inspect(context, null);
                    break;
                case "capture":
                    break;
                default:
                    return CucliCommandResult.Unsupported(ResolveAction(context, "inspect"), ActionNames());
            }

            return CucliCommandResult.Fail(
                CucliErrors.CommandFailed,
                "frame '" + ResolveAction(context, "inspect") + "' spans Editor frames and must be dispatched through "
                + "the deferred path.");
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            switch (ResolveAction(context, "inspect"))
            {
                case "capture":
                    Capture(context, complete);
                    return;
                case "inspect":
                    InspectDeferred(context, complete);
                    return;
                default:
                    complete(Execute(context));
                    return;
            }
        }

        // ------------------------------------------------------------------ inspect

        private static void InspectDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            int observeMs = Mathf.Clamp(context.Input.GetInt("observeMs", DefaultObservationMs), 0, 5000);
            if (observeMs == 0)
            {
                complete(Inspect(context, null));
                return;
            }

            var observation = new RenderObservation();
            observation.Attach();
            double deadline = EditorApplication.timeSinceStartup + observeMs / 1000.0;
            bool done = false;

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (done) return;

                if (context.Cancellation.IsCancellationRequested)
                {
                    done = true;
                    EditorApplication.update -= tick;
                    observation.Dispose();
                    complete(CucliCommandResult.Fail(CucliErrors.Cancelled, "Frame inspection was cancelled."));
                    return;
                }

                observation.NoteTick();
                if (EditorApplication.timeSinceStartup < deadline) return;

                done = true;
                EditorApplication.update -= tick;
                try
                {
                    complete(Inspect(context, observation));
                }
                finally
                {
                    observation.Dispose();
                }
            };

            EditorApplication.update += tick;
        }

        private static CucliCommandResult Inspect(CucliCommandContext context, RenderObservation observation)
        {
            CucliArgs args = context.Input;
            bool includeCameras = args.GetBool("includeCameras", true);

            var scenes = new JArray();
            for (int index = 0; index < UnityEngine.SceneManagement.SceneManager.sceneCount; index++)
            {
                UnityEngine.SceneManagement.Scene scene =
                    UnityEngine.SceneManagement.SceneManager.GetSceneAt(index);
                if (!scene.IsValid()) continue;
                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["isLoaded"] = scene.isLoaded,
                    ["isDirty"] = scene.isDirty,
                    ["rootCount"] = scene.isLoaded ? scene.rootCount : 0,
                    ["buildIndex"] = scene.buildIndex
                });
            }

            UnityEngine.SceneManagement.Scene active =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            var result = new JObject
            {
                ["capturedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["scene"] = new JObject
                {
                    ["active"] = active.IsValid()
                        ? new JObject { ["name"] = active.name, ["path"] = active.path, ["isDirty"] = active.isDirty }
                        : null,
                    ["loaded"] = scenes
                },
                ["frame"] = Time.frameCount,
                ["editorTime"] = EditorApplication.timeSinceStartup,
                ["playing"] = EditorApplication.isPlaying,
                ["paused"] = EditorApplication.isPaused,
                ["timeScale"] = Time.timeScale,
                ["compiling"] = EditorApplication.isCompiling,
                ["updating"] = EditorApplication.isUpdating,
                ["pipeline"] = CaptureSupport.PipelineJson(),
                ["screen"] = CaptureSupport.ScreenJson(),
                ["activeRenderTexture"] = RenderTexture.active == null
                    ? null
                    : new JObject
                    {
                        ["name"] = RenderTexture.active.name,
                        ["instanceId"] = RenderTexture.active.GetInstanceID(),
                        ["width"] = RenderTexture.active.width,
                        ["height"] = RenderTexture.active.height,
                        ["isCreated"] = RenderTexture.active.IsCreated()
                    },
                ["sceneViews"] = new JArray(CaptureSupport.OpenSceneViews().Select(view => new JObject
                {
                    ["title"] = view.titleContent == null ? null : view.titleContent.text,
                    ["is2D"] = view.in2DMode,
                    ["hasCamera"] = view.camera != null,
                    ["cameraInstanceId"] = view.camera == null ? 0 : view.camera.GetInstanceID()
                }))
            };

            if (includeCameras)
            {
                var cameras = new JArray();
                foreach (Camera camera in CaptureSupport.Cameras())
                {
                    if (camera == null) continue;
                    JObject entry = CaptureSupport.CameraJson(camera);

                    // Projection and clip planes are reported explicitly because they are what a caller
                    // most often needs to explain an unexpected image.
                    entry["projection"] = camera.orthographic ? "Orthographic" : "Perspective";

                    if (observation != null)
                    {
                        int frame;
                        double editorTime;
                        bool rendered = observation.TryGetStamp(camera, out frame, out editorTime);
                        entry["renderedDuringObservation"] = rendered;
                        entry["lastRenderedFrame"] = rendered ? (JToken)frame : JValue.CreateNull();
                        entry["lastRenderedEditorTime"] = rendered ? (JToken)editorTime : JValue.CreateNull();
                    }

                    cameras.Add(entry);
                }

                result["cameras"] = cameras;
                result["cameraCount"] = cameras.Count;
            }

            if (observation == null)
            {
                result["observation"] = new JObject
                {
                    ["performed"] = false,
                    ["note"] = "Observation was skipped (observeMs=0), so renderedDuringObservation is absent rather "
                        + "than false. Absence of evidence is not evidence of absence."
                };
            }
            else
            {
                result["observation"] = new JObject
                {
                    ["performed"] = true,
                    ["windowMs"] = context.Input.GetInt("observeMs", DefaultObservationMs),
                    ["ticksObserved"] = observation.TicksObserved,
                    ["renderedCameraCount"] = observation.RenderedCameraCount,
                    ["note"] = "renderedDuringObservation=false means no render callback for that camera was seen "
                        + "during the window. It does NOT mean the camera is broken: it may be disabled, culled by "
                        + "its culling mask, rendered on demand only, or simply not shown. Equally, a rising frame "
                        + "count is not evidence that this camera rendered - which is why the real frame number is "
                        + "reported per camera instead of being assumed."
                };
            }

            if (observation != null && observation.RenderedCameraCount == 0 && CaptureSupport.Cameras().Length > 0)
            {
                context.Warn("No camera render callback was observed during the window. Read the observation note "
                    + "before concluding anything about the cameras.");
            }

            return CucliCommandResult.Ok(result);
        }

        // ------------------------------------------------------------------ capture

        private void Capture(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;

            int count = Mathf.Clamp(args.GetInt("count", 1), 1, MaxCount);
            int intervalMs = Mathf.Clamp(args.GetInt("intervalMs", args.GetInt("interval_ms", 250)), 0, MaxIntervalMs);
            if ((long)(count - 1) * intervalMs > MaxTotalIntervalMs)
            {
                complete(CucliCommandResult.Invalid(
                    "intervalMs",
                    "count-1 times intervalMs must not exceed 60000 ms; reduce count or intervalMs."));
                return;
            }

            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveFeatureFolder(
                    args.GetString("folder"), out folderRelative, out folderAbsolute, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            string prefix = args.GetString("prefix") ?? string.Empty;
            if (!CaptureSupport.TryValidatePrefix(prefix, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            List<string> sources;
            if (!TryResolveSources(args.GetString("source"), out sources, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            string cameraIdentity = args.GetString("camera");
            if (sources.Contains("camera") && string.IsNullOrWhiteSpace(cameraIdentity))
            {
                complete(CucliCommandResult.Missing(
                    "camera", "source=camera needs --camera <name|instanceId>; run 'cucli frame inspect' for the list."));
                return;
            }

            if (Application.isBatchMode && sources.Contains("sceneview"))
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "source=sceneview needs an open Scene view, which does not exist in batch mode. "
                    + "Use source=scenecam or source=camera there.",
                    new { batchMode = true, sources }));
                return;
            }

            bool force = args.GetBool("force", false);
            int width = Mathf.Clamp(args.GetInt("width", 1920), 64, 4096);
            int height = Mathf.Clamp(args.GetInt("height", 1080), 64, 4096);

            string manifestName = (string.IsNullOrWhiteSpace(prefix) ? "capture" : prefix.Trim().Trim('-') + "-capture")
                + ".json";
            string manifestPath = Path.Combine(folderAbsolute, manifestName).Replace('\\', '/');

            // Every target is checked before anything is written, so a run either produces its whole
            // expected set or refuses up front - it never half-overwrites an existing evidence folder.
            if (!force)
            {
                var conflicts = new List<string>();
                if (File.Exists(manifestPath)) conflicts.Add(ToRelative(manifestPath));

                for (int index = 0; index < count; index++)
                {
                    foreach (string source in sources)
                    {
                        string path;
                        string relative;
                        string resolveError;
                        if (CaptureSupport.TryResolveFilePath(
                                folderAbsolute, PrefixFor(prefix, count, index), source, ".png",
                                index + 1, true, out path, out relative, out resolveError))
                        {
                            if (File.Exists(path)) conflicts.Add(relative);
                        }
                    }
                }

                if (conflicts.Count > 0)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.AssetExists,
                        "Refusing to overwrite existing evidence in " + folderRelative + ": "
                        + string.Join(", ", conflicts.Take(5).ToArray())
                        + (conflicts.Count > 5 ? " (+" + (conflicts.Count - 5) + " more)" : string.Empty)
                        + ". Pass --force to replace, or --prefix to write a new run.",
                        new { folder = folderRelative, conflicts }));
                    return;
                }
            }

            var manifest = new JObject
            {
                ["action"] = "frame.capture",
                ["folder"] = folderRelative,
                ["source"] = args.GetString("source") ?? "all",
                ["sources"] = new JArray(sources),
                ["prefix"] = prefix,
                ["camera"] = cameraIdentity,
                ["requested"] = count,
                ["intervalMs"] = intervalMs,
                ["resolution"] = new JObject { ["width"] = width, ["height"] = height },
                ["startedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["startFrame"] = Time.frameCount,
                ["startTime"] = Time.time,
                ["pipeline"] = CaptureSupport.PipelineJson(),
                ["timingNote"] = "intervalMs is the minimum wait between grabs and does not include the pixel read "
                    + "or PNG encode cost, so this is a sampled sheet, not a lossless frame-accurate recording."
            };

            var captures = new JArray();
            var files = new JArray();
            int completed = 0;
            int index2 = 0;
            double nextCaptureAt = EditorApplication.timeSinceStartup;

            var observation = new RenderObservation();
            observation.Attach();

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (context.Cancellation.IsCancellationRequested)
                {
                    Finish(true);
                    return;
                }

                if (EditorApplication.timeSinceStartup < nextCaptureAt) return;
                nextCaptureAt = EditorApplication.timeSinceStartup + Mathf.Max(0, intervalMs) / 1000.0;

                var capture = new JObject
                {
                    ["index"] = index2 + 1,
                    ["frameCount"] = Time.frameCount,
                    ["time"] = Time.time,
                    ["editorTime"] = EditorApplication.timeSinceStartup,
                    ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                    ["playing"] = EditorApplication.isPlaying,
                    ["paused"] = EditorApplication.isPaused,
                    ["timeScale"] = Time.timeScale,
                    ["files"] = new JArray(),
                    ["errors"] = new JArray()
                };

                foreach (string source in sources)
                {
                    string path;
                    string relative;
                    string resolveError;
                    if (!CaptureSupport.TryResolveFilePath(
                            folderAbsolute, PrefixFor(prefix, count, index2), source, ".png",
                            index2 + 1, force, out path, out relative, out resolveError))
                    {
                        ((JArray)capture["errors"]).Add(new JObject { ["source"] = source, ["error"] = resolveError });
                        continue;
                    }

                    string captureError;
                    if (!TryCaptureSource(source, cameraIdentity, width, height, path, out captureError))
                    {
                        ((JArray)capture["errors"]).Add(new JObject { ["source"] = source, ["error"] = captureError });
                        continue;
                    }

                    context.AddArtifact(path);
                    ((JArray)capture["files"]).Add(new JObject
                    {
                        ["source"] = source,
                        ["path"] = relative,
                        ["bytes"] = File.Exists(path) ? new FileInfo(path).Length : 0
                    });
                    files.Add(relative);
                }

                // Per-frame render evidence, so a reviewer can tell a still image from a live one.
                var rendered = new JArray();
                foreach (Camera camera in CaptureSupport.Cameras())
                {
                    int frame;
                    double editorTime;
                    if (camera == null || !observation.TryGetStamp(camera, out frame, out editorTime)) continue;
                    rendered.Add(new JObject
                    {
                        ["name"] = camera.name,
                        ["instanceId"] = camera.GetInstanceID(),
                        ["frame"] = frame,
                        ["editorTime"] = editorTime
                    });
                }

                capture["renderedCameras"] = rendered;
                captures.Add(capture);
                if (((JArray)capture["files"]).Count > 0) completed++;

                index2++;
                if (index2 < count) return;
                Finish(false);
            };

            EditorApplication.update += tick;

            void Finish(bool cancelled)
            {
                EditorApplication.update -= tick;
                observation.Dispose();

                manifest["captures"] = captures;
                manifest["files"] = files;
                manifest["completedCaptures"] = completed;
                manifest["failures"] = captures.Count - completed;
                manifest["completedAtUtc"] = DateTime.UtcNow.ToString("O");
                manifest["endFrame"] = Time.frameCount;
                manifest["endTime"] = Time.time;
                manifest["cancelled"] = cancelled;

                string manifestError;
                string written;
                if (CaptureSupport.TryWriteManifest(
                        folderAbsolute, manifestName, manifest, force, out written, out manifestError))
                {
                    context.AddArtifact(written);
                    manifest["manifestPath"] = CaptureSupport.ToProjectRelative(written);
                }
                else
                {
                    context.Warn(manifestError);
                }

                if (cancelled)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.Cancelled,
                        "The capture sequence was cancelled after " + completed + " of " + count
                        + " frame(s). Completed frames and the manifest were kept.",
                        new { folder = folderRelative, completedCaptures = completed, requested = count, files, captures }));
                    return;
                }

                if (completed == 0)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "No frame could be captured. Every source failed; see captures[].errors. "
                        + "Run 'cucli frame inspect' to check which cameras have a created targetTexture.",
                        new { folder = folderRelative, captures }));
                    return;
                }

                if (completed < count)
                {
                    context.Warn((count - completed) + " of " + count
                        + " frame(s) had at least one failing source; successful files were kept.");
                }

                complete(CucliCommandResult.Ok(new
                {
                    folder = folderRelative,
                    requested = count,
                    completedCaptures = completed,
                    failures = count - completed,
                    intervalMs,
                    files,
                    manifestPath = CaptureSupport.ToProjectRelative(written),
                    captures
                }, "已抓取 " + completed + "/" + count + " 帧并写出清单。"));
            }
        }

        /// <summary>
        /// <c>count &gt; 1</c> gets a per-frame numeric suffix; a single frame keeps the bare prefix so the
        /// common case produces the obvious file name.
        /// </summary>
        private static string PrefixFor(string prefix, int count, int index)
        {
            if (count <= 1) return prefix;
            string stem = string.IsNullOrWhiteSpace(prefix) ? "frame" : prefix.Trim().Trim('-');
            return stem + "-" + (index + 1).ToString("D3", CultureInfo.InvariantCulture);
        }

        private static bool TryResolveSources(string requested, out List<string> sources, out string error)
        {
            sources = new List<string>();
            error = null;

            string normalized = (requested ?? "all").Trim().ToLowerInvariant();
            if (normalized == "all")
            {
                sources.Add("scenecam");
                sources.Add("sceneview");
                return true;
            }

            string[] parts = normalized.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (part != "scenecam" && part != "camera" && part != "sceneview")
                {
                    error = "'" + part + "' is not a capture source. Use all, scenecam, camera or sceneview.";
                    return false;
                }

                if (!sources.Contains(part)) sources.Add(part);
            }

            if (sources.Count == 0)
            {
                error = "source must not be empty.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Writes one source to one file. <c>scenecam</c> and <c>camera</c> only read; <c>sceneview</c>
        /// renders and restores.
        /// </summary>
        private static bool TryCaptureSource(
            string source, string cameraIdentity, int width, int height, string absolutePath, out string error)
        {
            error = null;

            if (source == "sceneview")
            {
                return CaptureSupport.TryCaptureSceneView(width, height, absolutePath, out error);
            }

            Camera camera;
            if (source == "scenecam")
            {
                if (!CaptureSupport.TryResolveSceneCamera(out camera, out error)) return false;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(cameraIdentity))
                {
                    error = "source=camera needs --camera <name|instanceId>.";
                    return false;
                }

                if (!CaptureSupport.TryResolveCamera(cameraIdentity, out camera, out error)) return false;
            }

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

        private static string ToRelative(string absolute)
        {
            return CaptureSupport.ToProjectRelative(absolute).Replace('\\', '/');
        }
    }
}
