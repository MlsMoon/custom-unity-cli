using System;
using System.Collections;
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
    /// <c>cucli capture ...</c> - turn what the Editor is showing into a PNG on disk.
    ///
    /// The honest part of this group is what it refuses to pretend. A camera render contains the 3D
    /// world but <em>not</em> a Screen Space Overlay canvas, because that UI is composited by the game
    /// view rather than by any camera. So the default <c>game</c> capture is a camera render and says
    /// <c>includesOverlayUI: false</c>; the overlay-inclusive path is opt-in via
    /// <c>--include-ui</c>, requires Play Mode, and is deferred to the end of a frame.
    /// </summary>
    internal sealed class CaptureCommand : CucliCommand, ICucliDeferredCommand
    {
        public override string Name => "capture";

        public override string Summary =>
            "Capture the game view, the Scene view, a render texture, or a multi-angle surround/orbit sheet to PNG files plus a manifest.";

        public override CucliActionInfo[] Actions => Declare(
            "game", "Capture what a camera renders. Add --include-ui for the overlay-composited game view image.",
            "scene", "Capture the Scene view camera's current state.",
            "multiview", "Frame the subject from 6 axis directions (surround) or N orbit angles, one PNG each.",
            "render-texture", "Read an existing RenderTexture and write it out as a PNG.",
            "info", "Report what is capturable: cameras, open Scene views, loaded render textures, output folder.",
            "list", "List previous capture folders with their frame counts and manifests.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        /// <summary>
        /// Captures only write PNGs and manifests under the ignored <c>Assets/Docs~</c> tree; they never
        /// import an asset or edit a scene, so they are not a project mutation in the sense the flag means.
        /// </summary>
        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "filesystem.write", "editor.frame.read" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""game"", ""scene"", ""multiview"", ""render-texture"", ""info"", ""list""],
      ""default"": ""game""
    },
    ""outputFolder"": { ""type"": ""string"", ""description"": ""Project-relative folder under Assets/. Default Assets/Docs~/cucli-captures."" },
    ""fileName"": { ""type"": ""string"", ""description"": ""Bare file name for a single capture; no path.""" },
    ""prefix"": { ""type"": ""string"", ""description"": ""File name prefix when no fileName is given."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Overwrite an existing file instead of disambiguating the name."" },
    ""target"": { ""type"": ""string"", ""description"": ""Camera name or instance id. Defaults to Camera.main, then the first enabled non-Scene-view camera."" },
    ""width"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192, ""description"": ""Render width. Defaults to the camera's pixelWidth."" },
    ""height"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192 },
    ""superSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8, ""default"": 1 },
    ""includeUI"": { ""type"": ""boolean"", ""default"": false, ""description"": ""game: composite the overlay UI too. Requires Play Mode and waits for end of frame."" },
    ""includeImage"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Inline a base64 PNG in the response."" },
    ""maxResolution"": { ""type"": ""integer"", ""default"": 640, ""description"": ""Cap for an inlined image.""" },
    ""batch"": { ""type"": ""string"", ""enum"": [""surround"", ""orbit""], ""default"": ""surround"" },
    ""views"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 64, ""default"": 8, ""description"": ""multiview orbit: views per elevation ring."" },
    ""elevations"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""multiview orbit: elevation angles in degrees."" },
    ""distance"": { ""type"": ""number"", ""default"": 2.2, ""description"": ""multiview: camera distance as a multiple of the subject radius."" },
    ""targetObject"": { ""type"": ""string"", ""description"": ""multiview: object whose bounds frame the shots."" },
    ""texture"": { ""type"": ""string"", ""description"": ""render-texture: RenderTexture asset path, or its loaded name."" },
    ""limit"": { ""type"": ""integer"", ""maximum"": 500, ""default"": 50 },
    ""cursor"": { ""type"": ""string"" },
    ""pageSize"": { ""type"": ""integer"", ""maximum"": 500, ""default"": 100 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "game");
            switch (action)
            {
                case "game": return Game(context);
                case "scene": return SceneCapture(context);
                case "multiview": return Multiview(context);
                case "render-texture": return RenderTextureCapture(context);
                case "info": return Info(context);
                case "list": return List(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        /// <summary>
        /// Only the overlay-composited game capture needs the end of a frame. Everything else renders
        /// immediately through <c>Camera.Render</c>, which keeps it working while paused or in Edit Mode.
        /// </summary>
        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string action = ResolveAction(context, "game");
            if (action != "game" || !context.Input.GetBool("includeUI", false))
            {
                complete(Execute(context));
                return;
            }

            if (!EditorApplication.isPlaying)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "--include-ui reads the composited game view, which only exists in Play Mode. "
                    + "Start it with 'cucli editor play', or drop --include-ui to capture a camera render "
                    + "(which excludes Screen Space Overlay UI)."));
                return;
            }

            GameWithOverlay(context, complete);
        }

        // ------------------------------------------------------------------ game

        private static CucliCommandResult Game(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveFolder(args, out folderRelative, out folderAbsolute, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, error);
            }

            Camera camera;
            if (!CaptureSupport.TryResolveCamera(args, out camera, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            int superSize = Mathf.Clamp(args.GetInt("superSize", 1), 1, 8);
            int width = Mathf.Clamp(args.GetInt("width", camera.pixelWidth > 0 ? camera.pixelWidth : 1280), 1, 8192) * superSize;
            int height = Mathf.Clamp(args.GetInt("height", camera.pixelHeight > 0 ? camera.pixelHeight : 720), 1, 8192) * superSize;

            return RenderCameraToFile(context, camera, width, height, folderAbsolute, folderRelative, "game", false, null);
        }

        /// <summary>
        /// Deferred overlay capture. <c>ScreenCapture.CaptureScreenshotIntoRenderTexture</c> only has
        /// valid contents at the end of a frame, so the work happens on the next Editor update tick.
        /// </summary>
        private static void GameWithOverlay(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;
            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveFolder(args, out folderRelative, out folderAbsolute, out error))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, error));
                return;
            }

            int superSize = Mathf.Clamp(args.GetInt("superSize", 1), 1, 8);
            int width = Mathf.Max(1, (args.GetInt("width", Screen.width) > 0 ? args.GetInt("width", Screen.width) : 1280)) * superSize;
            int height = Mathf.Max(1, (args.GetInt("height", Screen.height) > 0 ? args.GetInt("height", Screen.height) : 720)) * superSize;

            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
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

                // One tick to let the game view repaint into the capture path, then read it.
                if (ticks < 2) return;

                done = true;
                EditorApplication.update -= tick;

                Texture2D texture = null;
                try
                {
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(target);
                    texture = CaptureSupport.ReadRenderTexture(target);
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
                    RenderTexture.ReleaseTemporary(target);
                }

                CucliCommandResult result = WriteTexture(
                    context, texture, folderAbsolute, folderRelative, "game-ui", true);
                CaptureSupport.Destroy(texture);
                complete(result);
            };

            EditorApplication.update += tick;
        }

        // ------------------------------------------------------------------ scene

        private static CucliCommandResult SceneCapture(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveFolder(args, out folderRelative, out folderAbsolute, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, error);
            }

            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                var open = OpenSceneViews();
                if (open.Count > 0) view = open[0];
            }

            if (view == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "No Scene view is open. Open one with Window > General > Scene, or capture the game view instead.",
                    new { sceneViewAvailable = false });
            }

            // The Scene view camera needs a repaint to be current; rendering a stale state would make
            // the file look plausible while showing last week's framing.
            view.Repaint();

            Camera camera = view.camera;
            int superSize = Mathf.Clamp(args.GetInt("superSize", 1), 1, 8);
            int width = Mathf.Clamp(
                args.GetInt("width", camera != null && camera.pixelWidth > 0 ? camera.pixelWidth : 1280), 1, 8192) * superSize;
            int height = Mathf.Clamp(
                args.GetInt("height", camera != null && camera.pixelHeight > 0 ? camera.pixelHeight : 720), 1, 8192) * superSize;

            if (camera == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The Scene view has no camera yet. Wait a frame and retry, or repaint the view.",
                    new { sceneViewAvailable = true });
            }

            return RenderCameraToFile(context, camera, width, height, folderAbsolute, folderRelative, "scene", false, null);
        }

        internal static List<SceneView> OpenSceneViews()
        {
            var views = new List<SceneView>();
            try
            {
                foreach (object entry in SceneView.sceneViews)
                {
                    var view = entry as SceneView;
                    if (view != null) views.Add(view);
                }
            }
            catch (Exception)
            {
                // SceneView.sceneViews is a non-generic ArrayList and is empty during shutdown; treat
                // an enumeration failure as "no Scene views" rather than failing the command.
            }

            return views;
        }

        // ------------------------------------------------------------------ multiview

        private static CucliCommandResult Multiview(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveFolder(args, out folderRelative, out folderAbsolute, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, error);
            }

            GameObject subject = null;
            string targetObject = args.GetString("targetObject") ?? args.GetString("target_object");
            if (!string.IsNullOrWhiteSpace(targetObject))
            {
                subject = CucliObjectResolver.ResolveGameObject(
                    new CucliArgs(new JObject { ["target"] = targetObject }), out error);
                if (error != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
                }
            }

            Bounds bounds = CaptureSupport.ResolveBounds(args, subject);
            string batch = (args.GetString("batch") ?? "surround").Trim().ToLowerInvariant();
            Vector3[] directions;
            string[] labels;

            if (batch == "orbit")
            {
                directions = CaptureSupport.OrbitDirections(
                    args.GetInt("views", 8),
                    args.GetStringList("elevations")
                        .Select(part =>
                        {
                            float parsed;
                            return float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0f;
                        })
                        .ToArray());
                labels = new string[directions.Length];
                for (int index = 0; index < labels.Length; index++)
                {
                    labels[index] = "orbit" + index.ToString("D2", CultureInfo.InvariantCulture);
                }
            }
            else if (batch == "surround")
            {
                directions = CaptureSupport.SurroundDirections();
                labels = CaptureSupport.SurroundLabels();
            }
            else
            {
                return CucliCommandResult.Invalid("batch", "'" + batch + "' is not 'surround' or 'orbit'.");
            }

            int resolution = Mathf.Clamp(args.GetInt("resolution", args.GetInt("width", 512)), 1, 4096);
            float distance = args.GetFloat("distance", 2.2f);
            bool force = args.GetBool("force", false);

            var manifest = new JObject
            {
                ["action"] = "multiview",
                ["batch"] = batch,
                ["subjectBounds"] = CucliDescribe.Value(bounds, null, 0),
                ["resolution"] = resolution,
                ["distanceMultiplier"] = distance,
                ["pipeline"] = CaptureSupport.PipelineJson(),
                ["time"] = CaptureSupport.TimeJson()
            };

            var captures = new JArray();
            int written = 0;
            int failures = 0;

            for (int index = 0; index < directions.Length; index++)
            {
                context.ThrowIfCancelled();

                Camera camera = null;
                RenderTexture target = null;
                Texture2D texture = null;
                string cameraError;
                var entry = new JObject { ["index"] = index, ["label"] = labels[index] };

                try
                {
                    if (!CaptureSupport.TryCreateFramingCamera(
                            bounds, directions[index], distance, resolution, out camera, out cameraError))
                    {
                        entry["error"] = cameraError;
                        failures++;
                        captures.Add(entry);
                        continue;
                    }

                    target = RenderTexture.GetTemporary(resolution, resolution, 24, RenderTextureFormat.ARGB32);
                    if (!CaptureSupport.TryRenderCamera(camera, target, out cameraError))
                    {
                        entry["error"] = cameraError;
                        failures++;
                        captures.Add(entry);
                        continue;
                    }

                    texture = CaptureSupport.ReadRenderTexture(target);

                    string absolutePath;
                    string fileName;
                    string nameError;
                    if (!CaptureSupport.TryResolveFileName(
                            new CucliArgs(new JObject
                            {
                                ["prefix"] = args.GetString("prefix") ?? "multiview",
                                ["force"] = force
                            }),
                            folderAbsolute, "multiview", ".png", index, out absolutePath, out fileName, out nameError))
                    {
                        entry["error"] = nameError;
                        failures++;
                        captures.Add(entry);
                        continue;
                    }

                    // Name each angle after its label so a reviewer can read the sheet without the manifest.
                    string labelled = Path.Combine(
                        folderAbsolute,
                        Path.GetFileNameWithoutExtension(fileName) + "-" + labels[index] + ".png");
                    if (File.Exists(labelled) && !force)
                    {
                        labelled = absolutePath;
                    }

                    string writeError;
                    if (!CaptureSupport.TryWritePng(texture, labelled, out writeError))
                    {
                        entry["error"] = writeError;
                        failures++;
                        captures.Add(entry);
                        continue;
                    }

                    written++;
                    context.AddArtifact(labelled);
                    entry["path"] = CaptureSupport.ToProjectRelative(labelled);
                    entry["direction"] = CucliDescribe.Value(directions[index], null, 0);
                    entry["camera"] = CaptureSupport.CameraJson(camera);
                    entry["frameCount"] = Time.frameCount;
                    entry["time"] = Time.time;
                    entry["timestampUtc"] = DateTime.UtcNow.ToString("O");
                    captures.Add(entry);
                }
                catch (Exception exception)
                {
                    entry["error"] = exception.GetBaseException().Message;
                    failures++;
                    captures.Add(entry);
                }
                finally
                {
                    CaptureSupport.Destroy(texture);
                    if (target != null) RenderTexture.ReleaseTemporary(target);
                    if (camera != null) CaptureSupport.Destroy(camera.gameObject);
                }
            }

            manifest["captures"] = captures;
            manifest["requested"] = directions.Length;
            manifest["written"] = written;
            manifest["failures"] = failures;

            string manifestPath;
            string manifestError;
            if (!CaptureSupport.TryWriteManifest(folderAbsolute, manifest, out manifestPath, out manifestError))
            {
                context.Warn(manifestError);
            }
            else
            {
                context.AddArtifact(manifestPath);
                manifest["manifestPath"] = CaptureSupport.ToProjectRelative(manifestPath);
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
                batch,
                requested = directions.Length,
                written,
                failures,
                manifestPath = CaptureSupport.ToProjectRelative(manifestPath),
                capturedFrame = Time.frameCount,
                captures
            }, "已写出 " + written + " 张视角图与清单。");
        }

        // ------------------------------------------------------------------ render-texture

        private static CucliCommandResult RenderTextureCapture(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string requested = args.GetString("texture") ?? args.GetString("name");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing("texture", "Pass --texture with a RenderTexture asset path or loaded name.");
            }

            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveFolder(args, out folderRelative, out folderAbsolute, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, error);
            }

            RenderTexture target = null;

            // Prefer an explicit asset path, then a loaded texture by name. A loaded-only texture is
            // common for anything created at runtime, so both paths matter.
            string normalized = CucliAssets.NormalizeAssetPath(requested);
            if (normalized != null)
            {
                target = CucliAssets.Load<RenderTexture>(normalized);
            }

            if (target == null)
            {
                var matches = Resources.FindObjectsOfTypeAll<RenderTexture>()
                    .Where(texture => texture != null && string.Equals(texture.name, requested, StringComparison.Ordinal))
                    .ToArray();

                if (matches.Length > 1)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetAmbiguous,
                        matches.Length + " loaded RenderTextures are named '" + requested
                        + "'; pass the asset path instead.",
                        new { candidates = matches.Select(texture => texture.GetInstanceID()).ToArray() });
                }

                if (matches.Length == 1) target = matches[0];
            }

            if (target == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "No RenderTexture asset or loaded RenderTexture matches '" + requested + "'.");
            }

            if (!target.IsCreated())
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "'" + requested + "' exists but has not been created yet, so it holds no pixels. "
                    + "It is usually filled by a camera render or a Blit; capture after that has run.",
                    new { texture = requested, width = target.width, height = target.height });
            }

            Texture2D texture = CaptureSupport.ReadRenderTexture(target);
            CucliCommandResult result = WriteTexture(
                context, texture, folderAbsolute, folderRelative, "rt-" + Sanitize(target.name), false, target);
            CaptureSupport.Destroy(texture);
            return result;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "texture";
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char character in value)
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');
            }

            return builder.ToString();
        }

        // ------------------------------------------------------------------ info

        private static CucliCommandResult Info(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string folderRelative;
            string folderAbsolute;
            string error;
            if (!CaptureSupport.TryResolveFolder(args, out folderRelative, out folderAbsolute, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, error);
            }

            var cameras = new List<JObject>();
            foreach (Camera camera in Object.FindObjectsOfType<Camera>())
            {
                if (camera == null) continue;
                var entry = CaptureSupport.CameraJson(camera);
                entry["isMain"] = camera.CompareTag("MainCamera");
                entry["cameraType"] = camera.cameraType.ToString();
                entry["isActiveAndEnabled"] = camera.isActiveAndEnabled;
                cameras.Add(entry);
            }

            var scenes = new JArray();
            foreach (SceneView view in OpenSceneViews())
            {
                scenes.Add(new JObject
                {
                    ["title"] = view.titleContent == null ? null : view.titleContent.text,
                    ["hasCamera"] = view.camera != null,
                    ["position"] = new JObject
                    {
                        ["x"] = view.position.x,
                        ["y"] = view.position.y,
                        ["width"] = view.position.width,
                        ["height"] = view.position.height
                    },
                    ["is2D"] = view.in2DMode
                });
            }

            var renderTextures = new JArray();
            foreach (RenderTexture texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
            {
                if (texture == null) continue;
                renderTextures.Add(new JObject
                {
                    ["name"] = texture.name,
                    ["instanceId"] = texture.GetInstanceID(),
                    ["width"] = texture.width,
                    ["height"] = texture.height,
                    ["isCreated"] = texture.IsCreated(),
                    ["assetPath"] = string.IsNullOrEmpty(CucliAssets.GetAssetPath(texture))
                        ? null
                        : CucliAssets.GetAssetPath(texture)
                });
                if (renderTextures.Count >= CucliPaging.MaxPageSize) break;
            }

            var cameraPage = CucliPaging.Slice(cameras, args, 100);
            var cameraArray = new JArray();
            foreach (JObject entry in cameraPage.Items) cameraArray.Add(entry);

            return CucliCommandResult.Ok(new
            {
                outputFolder = folderRelative,
                pipeline = CaptureSupport.PipelineJson(),
                time = CaptureSupport.TimeJson(),
                screen = new { width = Screen.width, height = Screen.height, dpi = Screen.dpi },
                overlayUiAvailability = EditorApplication.isPlaying
                    ? "Play Mode is active, so 'capture game --include-ui' can read the composited game view."
                    : "Edit Mode: --include-ui is unavailable, so 'capture game' returns a camera render without Screen Space Overlay UI.",
                cameras = CucliPaging.Envelope(cameraPage, cameraArray, new JObject
                {
                    ["sceneViews"] = scenes,
                    ["loadedRenderTextures"] = renderTextures,
                    ["renderTextureCount"] = renderTextures.Count
                })
            });
        }

        private static CucliCommandResult List(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            int limit = Mathf.Clamp(args.GetInt("limit", 50), 1, 500);

            var folders = new JArray();
            foreach (string folder in CaptureSupport.ListCaptureFolders(limit))
            {
                string manifestPath = Path.Combine(folder, CaptureSupport.ManifestName);
                var entry = new JObject
                {
                    ["folder"] = CaptureSupport.ToProjectRelative(folder),
                    ["pngCount"] = CaptureSupport.CountPngs(folder),
                    ["lastWriteUtc"] = Directory.GetLastWriteTimeUtc(folder).ToString("O"),
                    ["hasManifest"] = File.Exists(manifestPath)
                };

                if (File.Exists(manifestPath))
                {
                    try
                    {
                        JObject manifest = CucliJson.ParseObject(File.ReadAllText(manifestPath, CucliJson.Utf8NoBom));
                        entry["action"] = manifest["action"];
                        entry["written"] = manifest["written"];
                        entry["failures"] = manifest["failures"];
                        entry["writtenAtUtc"] = manifest["writtenAtUtc"];
                    }
                    catch (Newtonsoft.Json.JsonException exception)
                    {
                        entry["manifestError"] = exception.Message;
                    }
                }

                folders.Add(entry);
            }

            return CucliCommandResult.Ok(new
            {
                root = CaptureSupport.DefaultFolder,
                folderCount = folders.Count,
                folders
            });
        }

        // ------------------------------------------------------------------ shared write path

        private static CucliCommandResult RenderCameraToFile(
            CucliCommandContext context,
            Camera camera,
            int width,
            int height,
            string folderAbsolute,
            string folderRelative,
            string prefix,
            bool includesOverlayUI,
            RenderTexture existing)
        {
            RenderTexture target = null;
            Texture2D texture = null;
            try
            {
                target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                string renderError;
                if (!CaptureSupport.TryRenderCamera(camera, target, out renderError))
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, renderError);
                }

                texture = CaptureSupport.ReadRenderTexture(target);
                return WriteTexture(context, texture, folderAbsolute, folderRelative, prefix, includesOverlayUI, camera);
            }
            finally
            {
                if (target != null) RenderTexture.ReleaseTemporary(target);
                CaptureSupport.Destroy(texture);
            }
        }

        private static CucliCommandResult WriteTexture(
            CucliCommandContext context,
            Texture2D texture,
            string folderAbsolute,
            string folderRelative,
            string prefix,
            bool includesOverlayUI,
            Object source = null)
        {
            if (texture == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "No pixels were produced. The camera may be culled, disabled, or rendering to a target "
                    + "texture that this render did not fill.");
            }

            CucliArgs args = context.Input;
            string absolutePath;
            string fileName;
            string nameError;
            if (!CaptureSupport.TryResolveFileName(
                    args, folderAbsolute, prefix, ".png", 0, out absolutePath, out fileName, out nameError))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, nameError);
            }

            string writeError;
            if (!CaptureSupport.TryWritePng(texture, absolutePath, out writeError))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, writeError);
            }

            context.AddArtifact(absolutePath);

            string inline = null;
            if (args.GetBool("includeImage", false))
            {
                Texture2D small = CaptureSupport.Downscale(texture, Mathf.Clamp(args.GetInt("maxResolution", 640), 16, 4096));
                inline = CaptureSupport.ToBase64Png(small);
                if (!ReferenceEquals(small, texture)) CaptureSupport.Destroy(small);
            }

            var data = new JObject
            {
                ["path"] = CaptureSupport.ToProjectRelative(absolutePath),
                ["fileName"] = fileName,
                ["folder"] = folderRelative,
                ["width"] = texture.width,
                ["height"] = texture.height,
                ["bytes"] = new FileInfo(absolutePath).Length,
                // Stated explicitly so a caller can never mistake a camera render for the composited
                // game view: Screen Space Overlay UI is drawn by the game view, not by a camera.
                ["includesOverlayUI"] = includesOverlayUI,
                ["capturedFrame"] = Time.frameCount,
                ["time"] = Time.time,
                ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                ["pipeline"] = CaptureSupport.PipelineJson()
            };

            if (source is Camera capturedCamera) data["camera"] = CaptureSupport.CameraJson(capturedCamera);
            else if (source is RenderTexture sourceTexture)
            {
                data["sourceTexture"] = new JObject
                {
                    ["name"] = sourceTexture.name,
                    ["width"] = sourceTexture.width,
                    ["height"] = sourceTexture.height
                };
            }

            if (inline != null) data["imageBase64"] = inline;

            string message = includesOverlayUI
                ? "已截取合成后的 Game 视图。"
                : "已截取相机渲染画面（不含 Screen Space Overlay UI）。";

            return CucliCommandResult.Ok(data, message);
        }
    }
}
