using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Shared machinery for the capture groups: folder policy, file naming, PNG encoding, off-screen
    /// rendering, render observation and manifests.
    ///
    /// Three rules shape everything here, and each was taken from a working implementation rather than
    /// invented.
    ///
    /// <b>Evidence is never overwritten.</b> An existing PNG or manifest is a refusal, not a
    /// suggestion: a capture run that silently replaces yesterday's frames destroys the very artefact
    /// it was run to produce. <c>--force</c> is the only way past it.
    ///
    /// <b>Reading a camera's render texture does not render.</b> Reading an already-created
    /// <c>targetTexture</c> observes what the game produced; re-rendering it would change the thing
    /// being measured. Only the Scene view path actively renders, and it restores the camera's target
    /// texture and the active render texture in a <c>finally</c>.
    ///
    /// <b>Nothing is left behind.</b> Temporary cameras, render textures and textures are destroyed on
    /// every exit path, so a capture cannot poison the next Play/Stop cycle.
    /// </summary>
    internal static class CaptureSupport
    {
        /// <summary>Unity ignores folders whose name ends in <c>~</c>, so captures never enter a build.</summary>
        internal const string DocsRoot = "Assets/Docs~/";

        /// <summary>Where <c>capture</c> writes when no folder is given.</summary>
        internal const string DefaultCaptureFolder = "Assets/Docs~";

        /// <summary>Where <c>recorder</c> writes when no folder is given.</summary>
        internal const string DefaultRecordingFolder = "Assets/Docs~/Recordings";

        internal const string ManifestName = "capture-manifest.json";
        internal const string SessionRelativePath = "capture/recorder.json";
        internal const string SessionIndexRelativePath = "capture/session-images.json";

        /// <summary><c>0915_渲染分析</c> or <c>20260915_渲染分析</c>.</summary>
        private static readonly Regex FeatureFolderPattern = new Regex(
            @"^(\d{4}|\d{8})_[^\\/]+$", RegexOptions.CultureInvariant);

        internal static string SessionPath =>
            Path.Combine(CucliPaths.Root, SessionRelativePath.Replace('/', Path.DirectorySeparatorChar));

        internal static string SessionIndexPath =>
            Path.Combine(CucliPaths.Root, SessionIndexRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // ------------------------------------------------------------------ folder policy

        /// <summary>
        /// Resolves <c>--output-folder</c> / <c>--folder</c> / <c>--output</c> to a project-relative
        /// folder under <c>Assets/</c>.
        ///
        /// The containment test is done on the fully resolved path, not on the string prefix, so
        /// <c>..</c> traversal cannot escape the project even if it survives the earlier checks.
        /// </summary>
        internal static bool TryResolveOutputFolder(
            CucliArgs args,
            string fallbackFolder,
            out string projectRelative,
            out string absolute,
            out string error)
        {
            error = null;
            string requested = args.GetString("outputFolder")
                ?? args.GetString("output_folder")
                ?? args.GetString("folder")
                ?? args.GetString("output");

            if (string.IsNullOrWhiteSpace(requested)) requested = fallbackFolder;
            if (string.IsNullOrWhiteSpace(requested)) requested = DefaultCaptureFolder;

            string normalized = requested.Trim().Replace('\\', '/').TrimEnd('/');
            string projectRoot = CucliPaths.ProjectRoot.Replace('\\', '/');

            if (Path.IsPathRooted(normalized))
            {
                if (!normalized.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Output must live inside the project. Use a project-relative folder, not '"
                        + requested + "'.";
                    projectRelative = null;
                    absolute = null;
                    return false;
                }

                normalized = normalized.Substring(projectRoot.Length + 1);
            }

            normalized = normalized.TrimStart('/');
            if (!normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                error = "Output must be under 'Assets/' so the Editor can reference it; got '" + requested + "'.";
                projectRelative = null;
                absolute = null;
                return false;
            }

            projectRelative = normalized;
            absolute = Path.GetFullPath(Path.Combine(
                CucliPaths.ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));

            string resolvedRoot = Path.GetFullPath(CucliPaths.ProjectRoot) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "The resolved path '" + absolute + "' escapes the project, so it was refused.";
                projectRelative = null;
                absolute = null;
                return false;
            }

            try
            {
                Directory.CreateDirectory(absolute);
            }
            catch (Exception exception)
            {
                error = "Could not create '" + projectRelative + "': " + exception.GetBaseException().Message;
                absolute = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Resolves the <c>Assets/Docs~/&lt;date&gt;_&lt;feature&gt;</c> analysis folder used by
        /// <c>frame capture</c>.
        ///
        /// The name is constrained to a single level under <c>Assets/Docs~/</c> and is auto-prefixed
        /// with <c>MMdd_</c> when it does not already start with a 4- or 8-digit date, so every capture
        /// run is dated without the caller having to remember.
        /// </summary>
        internal static bool TryResolveFeatureFolder(
            string requested,
            out string projectRelative,
            out string absolute,
            out string error)
        {
            projectRelative = null;
            absolute = null;
            error = null;

            if (string.IsNullOrWhiteSpace(requested))
            {
                error = "A folder is required: --folder <时间_功能>, for example --folder 0915_渲染分析. "
                    + "It lands under " + DocsRoot + ".";
                return false;
            }

            string name = requested.Trim().Replace('\\', '/');
            if (name.StartsWith(DocsRoot, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(DocsRoot.Length);
            }

            name = name.Trim('/');
            if (name.Contains('/') || name.Contains(".."))
            {
                error = "'" + requested + "' must be a single folder name under " + DocsRoot
                    + "; it cannot contain a path separator or '..'. Use --prefix to name the files.";
                return false;
            }

            if (!Regex.IsMatch(name, @"^\d{4}_") && !Regex.IsMatch(name, @"^\d{8}_"))
            {
                name = DateTime.Now.ToString("MMdd", CultureInfo.InvariantCulture) + "_" + name;
            }

            if (!FeatureFolderPattern.IsMatch(name))
            {
                error = "The folder name must read as <date>_<feature>, for example 0915_渲染分析; got '" + name + "'.";
                return false;
            }

            projectRelative = DocsRoot + name;
            absolute = Path.Combine(
                CucliPaths.ProjectRoot, projectRelative.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                Directory.CreateDirectory(absolute);
            }
            catch (Exception exception)
            {
                error = "Could not create " + projectRelative + ": " + exception.GetBaseException().Message;
                absolute = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates a file-name prefix. A prefix is a file name fragment, never a path: allowing a
        /// separator here would let a capture land outside the folder that was just validated.
        /// </summary>
        internal static bool TryValidatePrefix(string prefix, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(prefix)) return true;
            if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || prefix.Contains(".."))
            {
                error = "prefix must be a file name fragment, so it cannot contain a path separator, "
                    + "an invalid file-name character, or '..'. Got '" + prefix + "'.";
                return false;
            }

            return true;
        }

        /// <summary>Builds the output path for one file, refusing to overwrite unless forced.</summary>
        internal static bool TryResolveFilePath(
            string folderAbsolute,
            string prefix,
            string source,
            string extension,
            int index,
            bool force,
            out string absolutePath,
            out string relativePath,
            out string error)
        {
            error = null;
            string stem = string.IsNullOrWhiteSpace(prefix) ? source : prefix.Trim().Trim('-') + "-" + source;
            if (index > 0) stem += "-" + index.ToString("D3", CultureInfo.InvariantCulture);
            string fileName = stem + extension;

            absolutePath = Path.Combine(folderAbsolute, fileName);
            relativePath = ToProjectRelative(absolutePath).Replace('\\', '/');

            if (File.Exists(absolutePath) && !force)
            {
                error = "Refusing to overwrite an existing capture: " + relativePath
                    + ". Pass --force to replace it, or --prefix to write a new run.";
                return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ texture IO

        internal static Texture2D ReadRenderTexture(RenderTexture source, bool linear = false)
        {
            if (source == null) return null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, linear);
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                texture.Apply(false, false);
                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        /// <summary>
        /// Reads a camera's existing render texture into a CPU texture without rendering anything.
        ///
        /// This is the honest way to observe what the game produced: re-rendering would change the
        /// measurement, and a camera with no created target texture simply has nothing to read yet.
        /// </summary>
        internal static bool TryReadCameraTexture(Camera camera, out Texture2D texture, out string error)
        {
            texture = null;
            error = null;

            if (camera == null)
            {
                error = "The camera is null.";
                return false;
            }

            RenderTexture target = camera.targetTexture;
            if (target == null || !target.IsCreated())
            {
                error = "Camera '" + camera.name + "' has no created targetTexture, so there are no pixels to read. "
                    + "Run 'cucli frame inspect' to see which render textures exist, or use source=sceneview which "
                    + "renders on demand.";
                return false;
            }

            texture = ReadRenderTexture(target);
            if (texture == null)
            {
                error = "Reading the render texture of '" + camera.name + "' produced no texture.";
                return false;
            }

            return true;
        }

        internal static bool TryWritePng(Texture2D texture, string absolutePath, out string error)
        {
            error = null;
            if (texture == null)
            {
                error = "No texture was produced.";
                return false;
            }

            try
            {
                byte[] png = texture.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    error = "PNG encoding returned no data.";
                    return false;
                }

                string directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(absolutePath, png);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        internal static Texture2D Downscale(Texture2D source, int maxResolution)
        {
            if (source == null || maxResolution <= 0) return source;
            int longest = Mathf.Max(source.width, source.height);
            if (longest <= maxResolution) return source;

            float scale = (float)maxResolution / longest;
            int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply(false, false);
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        internal static string ToBase64Png(Texture2D texture)
        {
            if (texture == null) return null;
            byte[] png = texture.EncodeToPNG();
            return png == null || png.Length == 0 ? null : Convert.ToBase64String(png);
        }

        internal static void Destroy(Object value)
        {
            if (value == null) return;
            try
            {
                Object.DestroyImmediate(value);
            }
            catch (Exception)
            {
                // Something Unity already reclaimed is not worth failing a capture over.
            }
        }

        // ------------------------------------------------------------------ rendering

        /// <summary>
        /// Renders a camera into a temporary render texture, restoring the camera's target texture and
        /// the active render texture in a <c>finally</c> so a failure cannot leave the Editor altered.
        /// </summary>
        internal static bool TryRenderCameraToPng(
            Camera camera,
            int width,
            int height,
            string absolutePath,
            out string error)
        {
            error = null;
            if (camera == null)
            {
                error = "The camera is null.";
                return false;
            }

            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D texture = null;
            try
            {
                temporary = new RenderTexture(Mathf.Clamp(width, 64, 4096), Mathf.Clamp(height, 64, 4096), 24);
                camera.targetTexture = temporary;
                camera.Render();
                RenderTexture.active = temporary;

                texture = new Texture2D(temporary.width, temporary.height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, temporary.width, temporary.height), 0, 0);
                texture.Apply(false, false);
                return TryWritePng(texture, absolutePath, out error);
            }
            catch (Exception exception)
            {
                error = "Rendering '" + camera.name + "' failed: " + exception.GetBaseException().Message;
                return false;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Destroy(texture);
                if (temporary != null)
                {
                    temporary.Release();
                    Destroy(temporary);
                }
            }
        }

        /// <summary>
        /// Renders the Scene view, restoring its camera's target texture and the active render texture.
        /// The Scene view is the only source that renders on demand rather than observing existing output.
        /// </summary>
        internal static bool TryCaptureSceneView(int width, int height, string absolutePath, out string error)
        {
            error = null;
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                var open = OpenSceneViews();
                if (open.Count > 0) view = open[0];
            }

            if (view == null)
            {
                error = "No Scene view is open. Open one with Window > General > Scene, or use another source.";
                return false;
            }

            if (view.camera == null)
            {
                error = "The Scene view has no camera yet; wait a frame and retry.";
                return false;
            }

            view.Repaint();
            return TryRenderCameraToPng(view.camera, width, height, absolutePath, out error);
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
                // SceneView.sceneViews is a non-generic ArrayList and is empty during shutdown.
            }

            return views;
        }

        /// <summary>
        /// Resolves a camera by name or instance id, including inactive ones - a disabled camera is one
        /// of the most common reasons a capture comes back empty, so it has to be findable.
        /// </summary>
        internal static bool TryResolveCamera(string identity, out Camera camera, out string error)
        {
            camera = null;
            error = null;

            var matches = new List<Camera>();
            foreach (Camera candidate in Cameras())
            {
                if (candidate == null) continue;
                if (string.Equals(candidate.name, identity, StringComparison.Ordinal)) matches.Add(candidate);
            }

            if (matches.Count == 0)
            {
                int instanceId;
                if (int.TryParse(identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
                {
                    foreach (Camera candidate in Cameras())
                    {
                        if (candidate != null && candidate.GetInstanceID() == instanceId)
                        {
                            camera = candidate;
                            return true;
                        }
                    }
                }

                error = "No camera named or numbered '" + identity + "' exists. Run 'cucli frame inspect' for the list.";
                return false;
            }

            if (matches.Count > 1)
            {
                error = matches.Count + " cameras are named '" + identity
                    + "'; pass the instanceId from 'cucli frame inspect' instead.";
                return false;
            }

            camera = matches[0];
            return true;
        }

        /// <summary>
        /// The Scene camera: the render-to-texture camera the project uses for its world view.
        ///
        /// This is the generic replacement for what the reference implementation looked up through a
        /// project-specific camera manager: a camera whose target texture already exists is by definition
        /// the one being rendered into a texture on purpose.
        /// </summary>
        internal static bool TryResolveSceneCamera(out Camera camera, out string error)
        {
            camera = null;
            error = null;

            foreach (Camera candidate in Cameras())
            {
                if (candidate == null || candidate.cameraType == CameraType.SceneView) continue;
                if (candidate.cameraType == CameraType.Preview) continue;
                if (candidate.targetTexture == null || !candidate.targetTexture.IsCreated()) continue;
                camera = candidate;
                return true;
            }

            error = "No camera currently owns a created targetTexture, so there is no Scene camera to read. "
                + "Run 'cucli frame inspect' to see the cameras and their render targets.";
            return false;
        }

        /// <summary>Every camera in the loaded scenes, including inactive and Scene-view cameras.</summary>
        internal static Camera[] Cameras()
        {
            return Resources.FindObjectsOfTypeAll<Camera>()
                .Where(camera => camera != null && camera.gameObject.scene.IsValid())
                .ToArray();
        }

        // ------------------------------------------------------------------ bounds / multiview

        internal static Bounds ResolveBounds(GameObject explicitTarget)
        {
            if (explicitTarget != null)
            {
                Renderer[] renderers = explicitTarget.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length > 0) return Union(renderers.Select(renderer => renderer.bounds));
                return new Bounds(explicitTarget.transform.position, Vector3.one);
            }

            Renderer[] all = Object.FindObjectsOfType<Renderer>();
            var active = all.Where(renderer => renderer != null && renderer.enabled).ToArray();
            if (active.Length > 0) return Union(active.Select(renderer => renderer.bounds));
            return new Bounds(Vector3.zero, Vector3.one);
        }

        private static Bounds Union(IEnumerable<Bounds> bounds)
        {
            Bounds? accumulator = null;
            foreach (Bounds item in bounds)
            {
                if (!accumulator.HasValue) accumulator = item;
                else
                {
                    Bounds merged = accumulator.Value;
                    merged.Encapsulate(item);
                    accumulator = merged;
                }
            }

            return accumulator ?? new Bounds(Vector3.zero, Vector3.one);
        }

        internal static Vector3[] SurroundDirections()
        {
            return new[]
            {
                new Vector3(0f, 0f, -1f),
                new Vector3(0f, 0f, 1f),
                new Vector3(-1f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, -1f, 0f)
            };
        }

        internal static string[] SurroundLabels()
        {
            return new[] { "front", "back", "left", "right", "top", "bottom" };
        }

        /// <summary>
        /// Creates a temporary framing camera for one axis. Callers must destroy
        /// <c>camera.gameObject</c> in a <c>finally</c>.
        /// </summary>
        internal static bool TryCreateFramingCamera(
            string label,
            Bounds bounds,
            Vector3 direction,
            float distanceMultiplier,
            out Camera camera,
            out string error)
        {
            camera = null;
            error = null;
            try
            {
                var host = new GameObject("cucli-capture-" + label);
                host.hideFlags = HideFlags.HideAndDontSave;
                camera = host.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);

                float radius = Mathf.Max(bounds.extents.magnitude, 0.001f);
                camera.transform.position = bounds.center
                    + direction.normalized * radius * Mathf.Max(1f, distanceMultiplier);
                camera.transform.LookAt(bounds.center);
                camera.nearClipPlane = Mathf.Max(0.01f, radius * 0.01f);
                camera.farClipPlane = radius * 100f;
                camera.fieldOfView = 40f;
                camera.aspect = 1f;
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not create a framing camera: " + exception.GetBaseException().Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ json helpers

        internal static JObject CameraJson(Camera camera)
        {
            var result = new JObject
            {
                ["name"] = camera.name,
                ["instanceId"] = camera.GetInstanceID(),
                ["enabled"] = camera.enabled,
                ["active"] = camera.gameObject.activeInHierarchy,
                ["type"] = camera.cameraType.ToString(),
                ["orthographic"] = camera.orthographic,
                ["orthographicSize"] = camera.orthographicSize,
                ["fieldOfView"] = camera.fieldOfView,
                ["nearClipPlane"] = camera.nearClipPlane,
                ["farClipPlane"] = camera.farClipPlane,
                ["depth"] = camera.depth,
                ["cullingMask"] = camera.cullingMask,
                ["clearFlags"] = camera.clearFlags.ToString(),
                ["position"] = CucliDescribe.Value(camera.transform.position, null, 0),
                ["rotation"] = CucliDescribe.Value(camera.transform.eulerAngles, null, 0)
            };

            RenderTexture target = camera.targetTexture;
            if (target != null)
            {
                result["targetTexture"] = target.name;
                result["targetTextureInstanceId"] = target.GetInstanceID();
                result["targetCreated"] = target.IsCreated();
                result["width"] = target.width;
                result["height"] = target.height;
                if (target.IsCreated())
                {
                    result["depthFormat"] = target.depthStencilFormat.ToString();
                }
            }
            else
            {
                // No target texture means the camera draws to the screen, so its usable size is the viewport.
                result["targetTexture"] = null;
                result["width"] = camera.pixelWidth;
                result["height"] = camera.pixelHeight;
            }

            return result;
        }

        internal static JObject PipelineJson()
        {
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            return new JObject
            {
                ["asset"] = asset == null ? null : asset.name,
                ["assetType"] = asset == null ? "Built-in" : asset.GetType().Name,
                ["isScriptable"] = asset != null,
                ["colorSpace"] = PlayerSettings.colorSpace.ToString()
            };
        }

        internal static JObject ScreenJson()
        {
            return new JObject
            {
                ["width"] = Screen.width,
                ["height"] = Screen.height,
                ["dpi"] = Screen.dpi,
                ["fullScreen"] = Screen.fullScreen,
                ["originNote"] = "Screen coordinates have their origin at the bottom-left, which is also what "
                    + "Input.mousePosition and the Input System report."
            };
        }

        /// <summary>Project-relative form of an absolute path, with forward slashes.</summary>
        internal static string ToProjectRelative(string absolute)
        {
            if (string.IsNullOrWhiteSpace(absolute)) return string.Empty;
            string projectRoot = CucliPaths.ProjectRoot;
            if (absolute.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return absolute.Substring(projectRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
            }

            return absolute.Replace('\\', '/');
        }

        // ------------------------------------------------------------------ manifest

        /// <summary>
        /// Writes a run manifest.
        ///
        /// Uses <see cref="FileMode.CreateNew"/> deliberately: the manifest is the index that makes a
        /// capture run auditable, so silently replacing one would orphan the PNGs it describes. Pass
        /// <paramref name="force"/> to overwrite on purpose.
        /// </summary>
        internal static bool TryWriteManifest(
            string folderAbsolute,
            string fileNameOrNull,
            JObject manifest,
            bool force,
            out string path,
            out string error)
        {
            error = null;
            string fileName = string.IsNullOrWhiteSpace(fileNameOrNull) ? ManifestName : fileNameOrNull;
            path = Path.Combine(folderAbsolute, fileName);

            try
            {
                manifest["schemaVersion"] = "cucli.capture-manifest/v1";
                manifest["writtenAtUtc"] = DateTime.UtcNow.ToString("O");
                manifest["manifestPath"] = ToProjectRelative(path);

                string json = manifest.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine;
                Directory.CreateDirectory(folderAbsolute);

                FileMode mode = force ? FileMode.Create : FileMode.CreateNew;
                using (var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, CucliJson.Utf8NoBom))
                {
                    writer.Write(json);
                }

                return true;
            }
            catch (IOException exception)
            {
                error = File.Exists(path)
                    ? "A manifest already exists at " + ToProjectRelative(path)
                      + " and was not overwritten; pass --force or use a different --prefix."
                    : "Could not write the manifest: " + exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = "Could not write the manifest: " + exception.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>Enumerates capture folders under the Docs~ root, newest first.</summary>
        internal static List<string> ListCaptureFolders(int limit)
        {
            var folders = new List<string>();
            string root = Path.Combine(
                CucliPaths.ProjectRoot, DefaultCaptureFolder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root)) return folders;

            try
            {
                folders.AddRange(Directory.GetDirectories(root)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .Take(Mathf.Max(1, limit)));
            }
            catch (IOException)
            {
                // A folder being written by a concurrent capture is not a reason to fail a listing.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return folders;
        }

        internal static int CountPngs(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.png").Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Which cameras actually rendered during an observation window, and at which real frame.
    ///
    /// <c>renderedDuringObservation == false</c> is deliberately not phrased as a diagnosis. The camera
    /// may be disabled, culled, render-on-demand, or simply not shown; frame advancement is not evidence
    /// that anything rendered. Recording the real frame number rather than assuming the current one is
    /// what makes the difference between "we saw it" and "it might have happened".
    /// </summary>
    internal sealed class RenderObservation : IDisposable
    {
        private struct RenderStamp
        {
            internal int Frame;
            internal double EditorTime;
        }

        private readonly Dictionary<int, RenderStamp> m_rendered = new Dictionary<int, RenderStamp>();
        private bool m_hooked;

        internal int TicksObserved { get; private set; }

        internal void Attach()
        {
            if (m_hooked) return;
            // Both paths are hooked: endCameraRendering covers the Scriptable Render Pipeline, and
            // Camera.onPreRender covers the Built-in pipeline, so the observation does not silently
            // depend on which pipeline the project happens to use.
            RenderPipelineManager.endCameraRendering += OnScriptableCameraRendered;
            Camera.onPreRender += OnBuiltInCamera;
            m_hooked = true;
        }

        internal void NoteTick()
        {
            TicksObserved++;
        }

        internal bool TryGetStamp(Camera camera, out int frame, out double editorTime)
        {
            frame = 0;
            editorTime = 0;
            if (camera == null) return false;

            RenderStamp stamp;
            if (!m_rendered.TryGetValue(camera.GetInstanceID(), out stamp)) return false;
            frame = stamp.Frame;
            editorTime = stamp.EditorTime;
            return true;
        }

        internal int RenderedCameraCount => m_rendered.Count;

        public void Dispose()
        {
            if (!m_hooked) return;
            RenderPipelineManager.endCameraRendering -= OnScriptableCameraRendered;
            Camera.onPreRender -= OnBuiltInCamera;
            m_hooked = false;
        }

        private void OnScriptableCameraRendered(ScriptableRenderContext context, Camera camera)
        {
            Record(camera);
        }

        private void OnBuiltInCamera(Camera camera)
        {
            Record(camera);
        }

        private void Record(Camera camera)
        {
            if (camera == null) return;
            m_rendered[camera.GetInstanceID()] = new RenderStamp
            {
                Frame = Time.frameCount,
                EditorTime = EditorApplication.timeSinceStartup
            };
        }
    }
}
