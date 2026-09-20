using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli capture ...</c> - one screenshot at a time.
    ///
    /// The distinction that matters is stated in every result. A camera render contains the 3D world but
    /// <b>not</b> a Screen Space Overlay canvas, because that UI is composited by the game view rather
    /// than by any camera. So <c>game</c> renders a camera and reports <c>includesOverlayUI: false</c>;
    /// the composited image is opt-in through <c>--include-ui</c>, which needs Play Mode and waits for the
    /// end of a frame. A caller that does not know the difference will spend a long time wondering where
    /// the UI went.
    /// </summary>
    internal sealed class CaptureCommand : CucliCommand, ICucliDeferredCommand
    {
        public override string Name => "capture";

        public override string Summary =>
            "Capture the game view, the Scene view or a six-angle surround sheet to PNG, with an optional inlined image.";

        public override CucliActionInfo[] Actions => Declare(
            "game", "Capture what a camera renders. Add --include-ui for the overlay-composited game view image.",
            "scene-view", "Render the Scene view camera on demand and restore its render target afterwards.",
            "multiview", "Frame the subject from six axis directions (surround), one PNG per angle.",
            "list", "List the images this session produced, plus capture folders under Assets/Docs~.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        /// <summary>
        /// Captures write PNGs and manifests under the ignored <c>Assets/Docs~</c> tree; they never import
        /// an asset or edit a scene, so they are not a project mutation in the sense the flag means.
        /// </summary>
        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "filesystem.write", "editor.frame.read" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""game"", ""scene-view"", ""multiview"", ""list""], ""default"": ""game"" },
    ""outputFolder"": { ""type"": ""string"", ""description"": ""Project-relative folder under Assets/. Default Assets/Docs~."" },
    ""filename"": { ""type"": ""string"", ""description"": ""Bare file name for a single capture; must not contain a path."" },
    ""prefix"": { ""type"": ""string"", ""description"": ""File name prefix when no filename is given."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Overwrite an existing file instead of failing with ASSET_EXISTS."" },
    ""target"": { ""type"": ""string"", ""description"": ""Camera name or instance id. Defaults to Camera.main, then the first enabled non-Scene-view camera."" },
    ""width"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192 },
    ""height"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192 },
    ""superSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 4, ""default"": 1 },
    ""includeUI"": {
      ""type"": ""boolean"", ""default"": false,
      ""description"": ""game: composite the overlay UI too. Requires Play Mode and waits for the end of a frame.""
    },
    ""includeImage"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Inline a base64 PNG in the response."" },
    ""maxResolution"": { ""type"": ""integer"", ""minimum"": 16, ""maximum"": 4096, ""default"": 640, ""description"": ""Cap applied before an image is inlined."" },
    ""batch"": { ""type"": ""string"", ""enum"": [""surround"", ""orbit""], ""default"": ""surround"", ""description"": ""multiview: orbit is not implemented and reports so."" },
    ""targetObject"": { ""type"": ""string"", ""description"": ""multiview: object whose bounds frame the shots. Defaults to all enabled renderers."" },
    ""distance"": { ""type"": ""number"", ""default"": 2.2, ""description"": ""multiview: camera distance as a multiple of the subject radius."" },
    ""resolution"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096, ""default"": 512, ""description"": ""multiview: square resolution per angle."" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "game"))
            {
                case "multiview": return Multiview(context);
                case "list": return ListCaptures(context);
                case "game":
                case "scene-view":
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "capture '" + ResolveAction(context, "game")
                        + "' needs an Editor frame and must be dispatched through the deferred path.");
                default: return CucliCommandResult.Unsupported(ResolveAction(context, "game"), ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            switch (ResolveAction(context, "game"))
            {
                case "game":
                    Game(context, complete);
                    return;
                case "scene-view":
                    SceneViewCapture(context, complete);
                    return;
                default:
                    complete(Execute(context));
                    return;
            }
        }

        // ------------------------------------------------------------------ game

        private static void Game(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;

            if (args.GetBool("includeUI", false))
            {
                GameWithOverlay(context, complete);
                return;
            }

            Camera camera;
            string error;
            if (!TryResolveDefaultCamera(args, out camera, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.TargetNotFound, error));
                return;
            }

            int superSize = Mathf.Clamp(args.GetInt("superSize", args.GetInt("super_size", 1)), 1, 4);
            int width = Mathf.Clamp(args.GetInt("width", camera.pixelWidth > 0 ? camera.pixelWidth : 1280), 1, 8192) * superSize;
            int height = Mathf.Clamp(args.GetInt("height", camera.pixelHeight > 0 ? camera.pixelHeight : 720), 1, 8192) * superSize;

            string folderRelative;
            string folderAbsolute;
            if (!CaptureSupport.TryResolveOutputFolder(
                    args, CaptureSupport.DefaultCaptureFolder, out folderRelative, out folderAbsolute, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            string absolutePath;
            string relativePath;
            CucliCommandResult failure;
            if (!TryResolveTargetFile(
                    args, folderAbsolute, "game", ".png", out absolutePath, out relativePath, out failure))
            {
                complete(failure);
                return;
            }

            if (!CaptureSupport.TryRenderCameraToPng(camera, width, height, absolutePath, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.CommandFailed, error));
                return;
            }

            SessionImageIndex.Append(absolutePath, "capture.game");
            context.AddArtifact(absolutePath);
            complete(BuildSingleResult(context, absolutePath, relativePath, folderRelative, camera, false));
        }

        /// <summary>
        /// The composited game view. <c>ScreenCapture</c> only has valid contents at the end of a frame,
        /// so this waits for a couple of Editor ticks rather than reading a half-filled buffer.
        /// </summary>
        private static void GameWithOverlay(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;

            if (!EditorApplication.isPlaying)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "--include-ui reads the composited game view, which only exists in Play Mode. Start it with "
                    + "'cucli editor play', or drop --include-ui to capture a camera render instead - note that a "
                    + "camera render excludes Screen Space Overlay UI."));
                return;
            }

            string error;
            string folderRelative;
            string folderAbsolute;
            if (!CaptureSupport.TryResolveOutputFolder(
                    args, CaptureSupport.DefaultCaptureFolder, out folderRelative, out folderAbsolute, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            string absolutePath;
            string relativePath;
            CucliCommandResult failure;
            if (!TryResolveTargetFile(
                    args, folderAbsolute, "game-ui", ".png", out absolutePath, out relativePath, out failure))
            {
                complete(failure);
                return;
            }

            int superSize = Mathf.Clamp(args.GetInt("superSize", args.GetInt("super_size", 1)), 1, 4);
            int sourceWidth = args.GetInt("width", Screen.width);
            int sourceHeight = args.GetInt("height", Screen.height);
            if (sourceWidth <= 0) sourceWidth = 1280;
            if (sourceHeight <= 0) sourceHeight = 720;

            RenderTexture target = RenderTexture.GetTemporary(
                Mathf.Clamp(sourceWidth * superSize, 1, 8192),
                Mathf.Clamp(sourceHeight * superSize, 1, 8192),
                24,
                RenderTextureFormat.ARGB32);

            bool done = false;
            int ticks = 0;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (done) return;
                ticks++;

                if (context.Cancellation.IsCancellationRequested)
                {
                    done = true;
                    EditorApplication.update -= tick;
                    RenderTexture.ReleaseTemporary(target);
                    complete(CucliCommandResult.Fail(CucliErrors.Cancelled, "The overlay capture was cancelled."));
                    return;
                }

                // Tick one lets the game view repaint through the capture path; tick two reads it.
                if (ticks < 2) return;

                done = true;
                EditorApplication.update -= tick;

                Texture2D texture = null;
                try
                {
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(target);
                    texture = CaptureSupport.ReadRenderTexture(target);
                    if (!CaptureSupport.TryWritePng(texture, absolutePath, out error))
                    {
                        complete(CucliCommandResult.Fail(CucliErrors.CommandFailed, error));
                        return;
                    }
                }
                catch (Exception exception)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "The composited game view could not be read: " + exception.GetBaseException().Message));
                    return;
                }
                finally
                {
                    CaptureSupport.Destroy(texture);
                    RenderTexture.ReleaseTemporary(target);
                }

                SessionImageIndex.Append(absolutePath, "capture.game.overlay");
                context.AddArtifact(absolutePath);
                complete(BuildSingleResult(context, absolutePath, relativePath, folderRelative, null, true));
            };

            EditorApplication.update += tick;
        }

        // ------------------------------------------------------------------ scene view

        private static void SceneViewCapture(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;

            if (Application.isBatchMode)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "The Scene view does not exist in batch mode, so 'capture scene-view' cannot run. "
                    + "Use 'capture game' there.",
                    new { batchMode = true }));
                return;
            }

            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                var open = CaptureSupport.OpenSceneViews();
                if (open.Count > 0) view = open[0];
            }

            if (view == null)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "No Scene view is open. Open one with Window > General > Scene, or capture the game view instead.",
                    new { sceneViewAvailable = false }));
                return;
            }

            if (view.camera == null)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The Scene view has no camera yet; wait a frame and retry.",
                    new { sceneViewAvailable = true }));
                return;
            }

            string error;
            string folderRelative;
            string folderAbsolute;
            if (!CaptureSupport.TryResolveOutputFolder(
                    args, CaptureSupport.DefaultCaptureFolder, out folderRelative, out folderAbsolute, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            string absolutePath;
            string relativePath;
            CucliCommandResult failure;
            if (!TryResolveTargetFile(
                    args, folderAbsolute, "scene-view", ".png", out absolutePath, out relativePath, out failure))
            {
                complete(failure);
                return;
            }

            int superSize = Mathf.Clamp(args.GetInt("superSize", args.GetInt("super_size", 1)), 1, 4);
            int width = Mathf.Clamp(
                args.GetInt("width", view.camera.pixelWidth > 0 ? view.camera.pixelWidth : 1280), 1, 8192) * superSize;
            int height = Mathf.Clamp(
                args.GetInt("height", view.camera.pixelHeight > 0 ? view.camera.pixelHeight : 720), 1, 8192) * superSize;

            if (!CaptureSupport.TryCaptureSceneView(width, height, absolutePath, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.CommandFailed, error));
                return;
            }

            SessionImageIndex.Append(absolutePath, "capture.scene-view");
            context.AddArtifact(absolutePath);
            complete(BuildSingleResult(context, absolutePath, relativePath, folderRelative, view.camera, false));
        }

        // ------------------------------------------------------------------ multiview

        private static CucliCommandResult Multiview(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string batch = (args.GetString("batch") ?? "surround").Trim().ToLowerInvariant();
            if (batch == "orbit")
            {
                return CucliCommandResult.Fail(
                    CucliErrors.UnsupportedAction,
                    "batch=orbit is not implemented. Only the six axis views of batch=surround are available; "
                    + "for arbitrary angles, place a camera and use 'capture game --target <name>' instead.",
                    new { requestedBatch = batch, implementedBatch = "surround" });
            }

            if (batch != "surround")
            {
                return CucliCommandResult.Invalid("batch", "'" + batch + "' is not 'surround' or 'orbit'.");
            }

            string error;
            string folderRelative;
            string folderAbsolute;
            if (!CaptureSupport.TryResolveOutputFolder(
                    args, CaptureSupport.DefaultCaptureFolder, out folderRelative, out folderAbsolute, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, error);
            }

            GameObject subject = null;
            string targetObject = args.GetString("targetObject") ?? args.GetString("target_object");
            if (!string.IsNullOrWhiteSpace(targetObject))
            {
                subject = CucliObjectResolver.ResolveGameObject(
                    new CucliArgs(new JObject { ["target"] = targetObject }), out error);
                if (error != null) return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            Bounds bounds = CaptureSupport.ResolveBounds(subject);
            Vector3[] directions = CaptureSupport.SurroundDirections();
            string[] labels = CaptureSupport.SurroundLabels();

            int resolution = Mathf.Clamp(args.GetInt("resolution", 512), 64, 4096);
            float distance = args.GetFloat("distance", 2.2f);
            bool force = args.GetBool("force", false);
            string prefix = args.GetString("prefix") ?? "multiview";

            if (!CaptureSupport.TryValidatePrefix(prefix, out error))
            {
                return CucliCommandResult.Invalid("prefix", error);
            }

            var manifest = new JObject
            {
                ["action"] = "capture.multiview",
                ["batch"] = "surround",
                ["folder"] = folderRelative,
                ["subjectBounds"] = CucliDescribe.Value(bounds, null, 0),
                ["resolution"] = resolution,
                ["distanceMultiplier"] = distance,
                ["pipeline"] = CaptureSupport.PipelineJson(),
                ["startedAtUtc"] = DateTime.UtcNow.ToString("O")
            };

            var captures = new JArray();
            var files = new JArray();
            int written = 0;
            int failures = 0;

            for (int index = 0; index < directions.Length; index++)
            {
                context.ThrowIfCancelled();

                string absolutePath;
                string relativePath;
                if (!CaptureSupport.TryResolveFilePath(
                        folderAbsolute, prefix, labels[index], ".png", 0, force,
                        out absolutePath, out relativePath, out error))
                {
                    failures++;
                    captures.Add(new JObject { ["label"] = labels[index], ["error"] = error });
                    continue;
                }

                var entry = new JObject
                {
                    ["label"] = labels[index],
                    ["direction"] = CucliDescribe.Value(directions[index], null, 0)
                };

                Camera camera = null;
                try
                {
                    if (!CaptureSupport.TryCreateFramingCamera(
                            labels[index], bounds, directions[index], distance, out camera, out error))
                    {
                        failures++;
                        entry["error"] = error;
                        captures.Add(entry);
                        continue;
                    }

                    if (!CaptureSupport.TryRenderCameraToPng(camera, resolution, resolution, absolutePath, out error))
                    {
                        failures++;
                        entry["error"] = error;
                        captures.Add(entry);
                        continue;
                    }

                    written++;
                    entry["path"] = relativePath;
                    entry["bytes"] = File.Exists(absolutePath) ? new FileInfo(absolutePath).Length : 0;
                    entry["frameCount"] = Time.frameCount;
                    entry["camera"] = CaptureSupport.CameraJson(camera);
                    captures.Add(entry);

                    context.AddArtifact(absolutePath);
                    files.Add(relativePath);
                    SessionImageIndex.Append(absolutePath, "capture.multiview." + labels[index]);
                }
                catch (Exception exception)
                {
                    failures++;
                    entry["error"] = exception.GetBaseException().Message;
                    captures.Add(entry);
                }
                finally
                {
                    // The framing camera is temporary by definition and must never outlive this iteration.
                    if (camera != null) CaptureSupport.Destroy(camera.gameObject);
                }
            }

            manifest["captures"] = captures;
            manifest["files"] = files;
            manifest["requested"] = directions.Length;
            manifest["written"] = written;
            manifest["failures"] = failures;
            manifest["completedAtUtc"] = DateTime.UtcNow.ToString("O");

            string writtenPath;
            string manifestError;
            if (CaptureSupport.TryWriteManifest(
                    folderAbsolute, prefix.Trim().Trim('-') + "-multiview.json", manifest, force,
                    out writtenPath, out manifestError))
            {
                context.AddArtifact(writtenPath);
            }
            else
            {
                context.Warn(manifestError);
            }

            if (written == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "No angle could be captured; see captures[].error.",
                    new { folder = folderRelative, captures, failures });
            }

            if (failures > 0)
            {
                context.Warn(failures + " of " + directions.Length + " angle(s) failed; successful files were kept.");
            }

            return CucliCommandResult.Ok(new
            {
                folder = folderRelative,
                batch = "surround",
                requested = directions.Length,
                written,
                failures,
                files,
                manifestPath = CaptureSupport.ToProjectRelative(writtenPath),
                captures
            }, "已写出 " + written + " 张视角图与清单。");
        }

        // ------------------------------------------------------------------ list

        private static CucliCommandResult ListCaptures(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            int limit = Mathf.Clamp(args.GetInt("limit", 50), 1, 500);

            JArray sessionImages = SessionImageIndex.ReadExisting(limit);

            var folders = new JArray();
            foreach (string folder in CaptureSupport.ListCaptureFolders(limit))
            {
                folders.Add(new JObject
                {
                    ["folder"] = CaptureSupport.ToProjectRelative(folder).Replace('\\', '/'),
                    ["pngCount"] = CaptureSupport.CountPngs(folder),
                    ["lastWriteUtc"] = Directory.GetLastWriteTimeUtc(folder).ToString("O")
                });
            }

            return CucliCommandResult.Ok(new
            {
                sessionImages,
                sessionImageCount = sessionImages.Count,
                sessionIndexPath = CaptureSupport.ToProjectRelative(SessionImageIndex.Path),
                folders,
                folderCount = folders.Count,
                note = "sessionImages lists what this Editor session wrote and still exists on disk; the index lives "
                    + "under Library/CustomUnityCli so it survives a domain reload. folders lists every run folder "
                    + "under " + CaptureSupport.DefaultCaptureFolder + "."
            });
        }

        // ------------------------------------------------------------------ shared

        /// <summary>
        /// Resolves the camera a <c>game</c> capture should use: an explicit target wins, then
        /// <c>Camera.main</c>, then the first enabled camera that is neither a Scene view nor a preview.
        /// </summary>
        private static bool TryResolveDefaultCamera(CucliArgs args, out Camera camera, out string error)
        {
            camera = null;
            error = null;

            string identity = args.GetString("target") ?? args.GetString("camera");
            if (!string.IsNullOrWhiteSpace(identity))
            {
                return CaptureSupport.TryResolveCamera(identity, out camera, out error);
            }

            camera = Camera.main;
            if (camera != null) return true;

            foreach (Camera candidate in CaptureSupport.Cameras())
            {
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                if (candidate.cameraType == CameraType.SceneView) continue;
                if (candidate.cameraType == CameraType.Preview) continue;
                camera = candidate;
                return true;
            }

            error = "No enabled camera in the loaded scenes. Pass --target <name|instanceId>, or create one first. "
                + "'cucli frame inspect' lists the cameras.";
            return false;
        }

        /// <summary>
        /// Resolves the destination file, honouring an explicit <c>--filename</c> and otherwise building
        /// one from the prefix. An existing file is an <c>ASSET_EXISTS</c> refusal unless <c>--force</c>.
        ///
        /// Failures come back as a ready-made result rather than a bare string, so the caller never has
        /// to infer the error code from the message.
        /// </summary>
        private static bool TryResolveTargetFile(
            CucliArgs args,
            string folderAbsolute,
            string defaultSource,
            string extension,
            out string absolutePath,
            out string relativePath,
            out CucliCommandResult failure)
        {
            failure = null;
            absolutePath = null;
            relativePath = null;
            bool force = args.GetBool("force", false);
            string requested = args.GetString("filename") ?? args.GetString("fileName")
                ?? args.GetString("file_name") ?? args.GetString("outputFile");

            if (!string.IsNullOrWhiteSpace(requested))
            {
                requested = requested.Trim();
                if (requested.IndexOf('/') >= 0 || requested.IndexOf('\\') >= 0)
                {
                    failure = CucliCommandResult.Invalid(
                        "filename", "must be a bare file name without a path; use '--output-folder' for the folder.");
                    return false;
                }

                string fileName = Path.HasExtension(requested) ? requested : requested + extension;
                absolutePath = Path.Combine(folderAbsolute, fileName);
                relativePath = CaptureSupport.ToProjectRelative(absolutePath).Replace('\\', '/');

                if (File.Exists(absolutePath) && !force)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.AssetExists,
                        "Refusing to overwrite " + relativePath + ". Pass --force to replace it.",
                        new { path = relativePath });
                    return false;
                }

                return true;
            }

            string prefix = args.GetString("prefix");
            string prefixError;
            if (!CaptureSupport.TryValidatePrefix(prefix, out prefixError))
            {
                failure = CucliCommandResult.Invalid("prefix", prefixError);
                return false;
            }

            string error;
            if (!CaptureSupport.TryResolveFilePath(
                    folderAbsolute, prefix, defaultSource, extension, 0, force,
                    out absolutePath, out relativePath, out error))
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.AssetExists, error, new { path = relativePath });
                return false;
            }

            return true;
        }

        private static CucliCommandResult BuildSingleResult(
            CucliCommandContext context,
            string absolutePath,
            string relativePath,
            string folderRelative,
            Camera camera,
            bool includesOverlayUI)
        {
            var data = new JObject
            {
                ["path"] = relativePath,
                ["fileName"] = Path.GetFileName(absolutePath),
                ["folder"] = folderRelative,
                ["bytes"] = File.Exists(absolutePath) ? new FileInfo(absolutePath).Length : 0,
                // Stated explicitly so a camera render is never mistaken for the composited game view:
                // Screen Space Overlay UI is drawn by the game view, not by any camera.
                ["includesOverlayUI"] = includesOverlayUI,
                ["capturedFrame"] = Time.frameCount,
                ["time"] = Time.time,
                ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                ["pipeline"] = CaptureSupport.PipelineJson()
            };

            if (camera != null) data["camera"] = CaptureSupport.CameraJson(camera);

            if (context.Input.GetBool("includeImage", false))
            {
                int maxResolution = Mathf.Clamp(context.Input.GetInt("maxResolution", 640), 16, 4096);
                var loaded = LoadPng(absolutePath);
                if (loaded != null)
                {
                    try
                    {
                        Texture2D small = CaptureSupport.Downscale(loaded, maxResolution);
                        data["imageBase64"] = CaptureSupport.ToBase64Png(small);
                        data["imageInlinedAt"] = Mathf.Max(small.width, small.height);
                        if (!ReferenceEquals(small, loaded)) CaptureSupport.Destroy(small);
                    }
                    finally
                    {
                        CaptureSupport.Destroy(loaded);
                    }
                }
                else
                {
                    context.Warn("Could not re-read the written PNG, so no inline image was produced.");
                }
            }

            string message = includesOverlayUI
                ? "已截取合成后的 Game 视图。"
                : "已截取相机渲染画面（不含 Screen Space Overlay UI）。";

            return CucliCommandResult.Ok(data, message);
        }

        /// <summary>
        /// Loads the PNG that was just written so an inlined image is the actual artefact rather than a
        /// second render, which could differ from the file on disk.
        /// </summary>
        private static Texture2D LoadPng(string absolutePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(absolutePath);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(bytes, false)) return texture;
                CaptureSupport.Destroy(texture);
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
