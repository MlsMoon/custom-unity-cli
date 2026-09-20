using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>screenshot</c> and <c>screenshot_multiview</c> actions of the <c>camera</c> group.
    ///
    /// The MCP surface forwards screenshots to <c>manage_scene</c>; this is the working subset of
    /// that contract that a camera command can honour on its own:
    ///
    /// * <c>captureSource=game_view</c> renders the addressed Camera into a RenderTexture, which does
    ///   not need Play Mode and is deterministic.
    /// * <c>captureSource=scene_view</c> renders the last active Scene view camera.
    /// * <c>screenshot_multiview</c> renders the six surround views around a target, which is the
    ///   <c>batch="surround"</c> mode of the scene contract.
    ///
    /// Output always lands under <c>Assets/Docs~/</c> unless <c>outputFolder</c> says otherwise, every
    /// produced path is registered with <c>context.AddArtifact</c>, and an existing file is only
    /// overwritten when <c>force</c> is true.
    /// </summary>
    internal static class CameraCapture
    {
        internal static void Execute(
            CucliCommandContext context, Action<CucliCommandResult> complete, bool multiview)
        {
            // Two frames: the first lets any pending layout settle, the second is captured.
            int framesRemaining = 2;
            bool done = false;
            int timeoutSeconds = Mathf.Clamp(
                context.Input.GetInt("timeoutSeconds", context.Input.GetInt("timeout_seconds", 60)), 1, 600);
            double deadline = EditorApplication.timeSinceStartup + timeoutSeconds;

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (done) return;

                if (context.Cancellation.IsCancellationRequested)
                {
                    done = true;
                    EditorApplication.update -= tick;
                    complete(CucliCommandResult.Fail(CucliErrors.Cancelled, "The screenshot was cancelled."));
                    return;
                }

                if (framesRemaining-- > 0)
                {
                    if (EditorApplication.timeSinceStartup > deadline)
                    {
                        done = true;
                        EditorApplication.update -= tick;
                        complete(CucliCommandResult.Fail(
                            CucliErrors.Timeout,
                            "The screenshot did not reach a rendered frame within " + timeoutSeconds + " seconds."));
                    }

                    return;
                }

                done = true;
                EditorApplication.update -= tick;
                try
                {
                    complete(multiview ? CaptureMultiview(context) : CaptureSingle(context));
                }
                catch (Exception exception)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "The screenshot failed: " + exception.GetBaseException().Message));
                }
            };

            EditorApplication.update += tick;
        }

        // ---------------------------------------------------------------- single

        private static CucliCommandResult CaptureSingle(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string source = args.GetString("captureSource") ?? args.GetString("capture_source") ?? "game_view";
            source = source.Trim().ToLowerInvariant();

            int superSize = Mathf.Clamp(args.GetInt("superSize", args.GetInt("super_size", 1)), 1, 8);
            int width = Mathf.Clamp(args.GetInt("width", 0), 0, 8192);
            int height = Mathf.Clamp(args.GetInt("height", 0), 0, 8192);

            if (args.Has("viewPosition") || args.Has("view_position"))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.UnsupportedAction,
                    "A positioned screenshot (viewPosition/viewRotation) is not implemented; this command renders "
                    + "an existing camera instead. Move the camera first with 'cucli camera' or use the scene "
                    + "view capture.",
                    new { hint = "Use captureSource=game_view with 'camera', or captureSource=scene_view." });
            }

            if (source == "scene_view")
            {
                if (superSize > 1)
                {
                    return CucliCommandResult.Invalid(
                        "superSize", "supersampling is not available for captureSource=scene_view.");
                }

                if (args.Has("camera") || args.Has("batch"))
                {
                    return CucliCommandResult.Invalid(
                        "captureSource", "'camera' and 'batch' cannot be combined with captureSource=scene_view.");
                }

                if (Application.isBatchMode)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "The Scene view cannot be captured in batch mode; there is no Scene view window.");
                }

                SceneView view = SceneView.lastActiveSceneView;
                if (view == null || view.camera == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, "No active Scene view found.");
                }

                int sceneWidth = view.camera.pixelWidth > 0 ? view.camera.pixelWidth : 1280;
                int sceneHeight = view.camera.pixelHeight > 0 ? view.camera.pixelHeight : 720;
                return WritePng(context, view.camera, sceneWidth, sceneHeight, 1, "scene_view");
            }

            if (source != "game_view")
            {
                return CucliCommandResult.Invalid(
                    "captureSource", "'" + source + "' is not one of: game_view, scene_view.");
            }

            Camera camera = CameraCommand.ResolveCamera(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            int targetWidth = width > 0 ? width : (camera.pixelWidth > 0 ? camera.pixelWidth : Screen.width);
            int targetHeight = height > 0 ? height : (camera.pixelHeight > 0 ? camera.pixelHeight : Screen.height);

            if (targetWidth <= 0) targetWidth = 1920;
            if (targetHeight <= 0) targetHeight = 1080;

            return WritePng(context, camera, targetWidth, targetHeight, superSize, "game_view");
        }

        // ---------------------------------------------------------------- multiview

        private static CucliCommandResult CaptureMultiview(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            int superSize = Mathf.Clamp(args.GetInt("superSize", args.GetInt("super_size", 1)), 1, 8);
            int width = Mathf.Clamp(args.GetInt("width", 0), 0, 8192);
            int height = Mathf.Clamp(args.GetInt("height", 0), 0, 8192);

            Camera source = null;
            if (args.Has("camera") || args.Has("target"))
            {
                source = CameraCommand.ResolveCamera(context, out CucliCommandResult failure);
                if (failure != null) return failure;
            }

            string targetName = args.GetString("viewTarget") ?? args.GetString("view_target");
            GameObject target = null;
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                string error;
                target = CucliObjectResolver.ResolveGameObject(
                    new CucliArgs(new JObject { ["target"] = targetName }), out error);
                if (target == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, "viewTarget: " + (error ?? "not found."));
                }
            }

            Vector3 centre = Vector3.zero;
            float radius = 6f;

            if (target != null)
            {
                Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    Bounds bounds = renderers[0].bounds;
                    for (int index = 1; index < renderers.Length; index++) bounds.Encapsulate(renderers[index].bounds);
                    centre = bounds.center;
                    radius = Mathf.Max(2f, bounds.extents.magnitude * 2.2f);
                }
                else
                {
                    centre = target.transform.position;
                }
            }
            else
            {
                bool any = false;
                foreach (Renderer renderer in CucliAssets.FindAll<Renderer>())
                {
                    if (renderer == null || EditorUtility.IsPersistent(renderer)) continue;
                    if (!any)
                    {
                        centre = renderer.bounds.center;
                        any = true;
                        continue;
                    }

                    centre = (centre + renderer.bounds.center) * 0.5f;
                }

                if (!any)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        "No renderer was found to frame; pass viewTarget to name the object to orbit.");
                }
            }

            int targetWidth = width > 0 ? width : 960;
            int targetHeight = height > 0 ? height : 960;

            GameObject temporary = null;
            try
            {
                temporary = new GameObject("~cucli-multiview-camera");
                temporary.hideFlags = HideFlags.HideAndDontSave;
                Camera camera = temporary.AddComponent<Camera>();
                camera.CopyFrom(source != null ? source : Camera.main);
                camera.enabled = true;

                var views = new List<KeyValuePair<string, Vector3>>
                {
                    new KeyValuePair<string, Vector3>("front", new Vector3(0f, 0f, -1f)),
                    new KeyValuePair<string, Vector3>("back", new Vector3(0f, 0f, 1f)),
                    new KeyValuePair<string, Vector3>("left", new Vector3(-1f, 0f, 0f)),
                    new KeyValuePair<string, Vector3>("right", new Vector3(1f, 0f, 0f)),
                    new KeyValuePair<string, Vector3>("top", new Vector3(0f, 1f, 0.001f)),
                    new KeyValuePair<string, Vector3>("bottom", new Vector3(0f, -1f, 0.001f))
                };

                var files = new JArray();

                foreach (KeyValuePair<string, Vector3> view in views)
                {
                    context.ThrowIfCancelled();

                    temporary.transform.position = centre + view.Value.normalized * radius;
                    temporary.transform.LookAt(centre);

                    CucliCommandResult written = WritePng(
                        context, camera, targetWidth, targetHeight, superSize, view.Key);
                    if (!written.Success) return written;

                    files.Add(JObject.FromObject(written.Data));
                }

                return CucliCommandResult.Ok(new
                {
                    batch = "surround",
                    viewCount = files.Count,
                    viewTarget = target == null ? null : CucliObjectResolver.HierarchyPath(target, true),
                    centre = new[] { centre.x, centre.y, centre.z },
                    distance = radius,
                    width = targetWidth,
                    height = targetHeight,
                    files
                }, "Rendered " + files.Count + " surround views.");
            }
            finally
            {
                if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary);
            }
        }

        // ---------------------------------------------------------------- capture + write

        private static CucliCommandResult WritePng(
            CucliCommandContext context,
            Camera camera,
            int width,
            int height,
            int superSize,
            string suffix)
        {
            if (camera == null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, "No camera is available to capture.");
            }

            int renderWidth = Mathf.Clamp(width * superSize, 1, 16384);
            int renderHeight = Mathf.Clamp(height * superSize, 1, 16384);

            RenderTexture destination = RenderTexture.GetTemporary(
                renderWidth, renderHeight, 24, RenderTextureFormat.ARGB32);
            if (destination == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "A " + renderWidth + "x" + renderHeight + " RenderTexture could not be allocated.");
            }

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            byte[] png = null;
            string renderError = null;

            try
            {
                camera.targetTexture = destination;
                camera.Render();
                camera.targetTexture = previousTarget;

                RenderTexture.active = destination;
                var readable = new Texture2D(renderWidth, renderHeight, TextureFormat.RGBA32, false);
                try
                {
                    readable.ReadPixels(new Rect(0, 0, renderWidth, renderHeight), 0, 0);
                    readable.Apply();
                    Texture2D final = readable;

                    if (renderWidth != width || renderHeight != height)
                    {
                        final = Resample(readable, width, height);
                    }

                    png = final.EncodeToPNG();
                    if (final != readable) UnityEngine.Object.DestroyImmediate(final);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(readable);
                }
            }
            catch (Exception exception)
            {
                renderError = exception.GetBaseException().Message;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(destination);
            }

            if (png == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Rendering camera '" + camera.name + "' failed: " + (renderError ?? "no pixels were produced"));
            }

            CucliCommandResult pathResult = ResolveOutputPath(context, suffix, out string relativePath,
                out string absolutePath);
            if (pathResult != null) return pathResult;

            try
            {
                File.WriteAllBytes(absolutePath, png);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Writing " + relativePath + " failed: " + exception.GetBaseException().Message);
            }

            context.AddArtifact(absolutePath);

            var data = new JObject
            {
                ["path"] = relativePath,
                ["fullPath"] = absolutePath,
                ["width"] = width,
                ["height"] = height,
                ["superSize"] = superSize,
                ["bytes"] = png.Length,
                ["camera"] = camera == null ? null : camera.name
            };

            CucliArgs args = context.Input;
            if (args.GetBool("includeImage", args.GetBool("include_image", false)))
            {
                int maxResolution = Mathf.Clamp(
                    args.GetInt("maxResolution", args.GetInt("max_resolution", 640)), 16, 8192);
                int inlineWidth = width;
                int inlineHeight = height;
                byte[] inlined = Downsample(png, width, height, maxResolution, ref inlineWidth, ref inlineHeight);
                if (inlined != null)
                {
                    data["imageBase64"] = Convert.ToBase64String(inlined);
                    data["imageWidth"] = inlineWidth;
                    data["imageHeight"] = inlineHeight;
                }
            }

            return CucliCommandResult.Ok(data, "Wrote " + relativePath + ".");
        }

        private static Texture2D Resample(Texture2D source, int width, int height)
        {
            int targetWidth = Mathf.Max(1, width);
            int targetHeight = Mathf.Max(1, height);
            var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    result.SetPixel(x, y, source.GetPixelBilinear(
                        (x + 0.5f) / targetWidth, (y + 0.5f) / targetHeight));
                }
            }

            result.Apply();
            return result;
        }

        private static byte[] Downsample(
            byte[] png, int width, int height, int maxResolution, ref int outWidth, ref int outHeight)
        {
            int longest = Mathf.Max(width, height);
            if (longest <= maxResolution) return png;

            float scale = (float)maxResolution / longest;
            outWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            outHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));

            var source = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var scaled = new Texture2D(outWidth, outHeight, TextureFormat.RGBA32, false);
            try
            {
                source.LoadImage(png);
                for (int y = 0; y < outHeight; y++)
                {
                    for (int x = 0; x < outWidth; x++)
                    {
                        scaled.SetPixel(x, y, source.GetPixelBilinear(
                            (x + 0.5f) / outWidth, (y + 0.5f) / outHeight));
                    }
                }

                scaled.Apply();
                return scaled.EncodeToPNG();
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(scaled);
            }
        }

        /// <summary>
        /// Screenshots are diagnostics, so the default destination is the Docs~ folder rather than an
        /// imported asset folder. Returns a non-null failure when the path is unusable or exists
        /// without <c>force</c>.
        /// </summary>
        private static CucliCommandResult ResolveOutputPath(
            CucliCommandContext context, string suffix, out string relativePath, out string absolutePath)
        {
            relativePath = null;
            absolutePath = null;
            CucliArgs args = context.Input;

            string folder = args.GetString("outputFolder") ?? args.GetString("output_folder") ?? "Assets/Docs~";
            if (ContainsTraversal(folder))
            {
                return CucliCommandResult.Invalid("outputFolder", "the folder must not contain '..': " + folder);
            }

            folder = folder.Replace('\\', '/').Trim('/');
            if (folder.Length == 0) folder = "Assets/Docs~";

            string fileName = args.GetString("filename") ?? args.GetString("fileName") ?? args.GetString("file_name");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                fileName = string.IsNullOrEmpty(suffix)
                    ? "screenshot-" + stamp + ".png"
                    : "screenshot-" + suffix + "-" + stamp + ".png";
            }
            else if (!string.IsNullOrEmpty(suffix))
            {
                int dot = fileName.LastIndexOf('.');
                fileName = dot < 0
                    ? fileName + "-" + suffix
                    : fileName.Substring(0, dot) + "-" + suffix + fileName.Substring(dot);
            }

            if (ContainsTraversal(fileName))
            {
                return CucliCommandResult.Invalid("filename", "the file name must not contain '..': " + fileName);
            }

            fileName = fileName.Replace('\\', '/').TrimStart('/');
            int lastSlash = fileName.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                folder = folder + "/" + fileName.Substring(0, lastSlash);
                fileName = fileName.Substring(lastSlash + 1);
            }

            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";

            string projectFolder = folder.StartsWith("Assets", StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith("Packages", StringComparison.OrdinalIgnoreCase)
                ? folder
                : "Assets/" + folder;

            relativePath = projectFolder + "/" + fileName;
            absolutePath = Path.Combine(
                CucliPaths.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(absolutePath) && !args.GetBool("force", false))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    relativePath + " already exists. Re-run with force: true to overwrite it.",
                    new { path = relativePath });
            }

            if (!CucliAssets.EnsureFolder(projectFolder))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The folder '" + projectFolder + "' could not be created.");
            }

            return null;
        }

        private static bool ContainsTraversal(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] segments = path.Replace('\\', '/').Split('/');
            for (int index = 0; index < segments.Length; index++)
            {
                if (segments[index] == "..") return true;
            }

            return false;
        }
    }
}
