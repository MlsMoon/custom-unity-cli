using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Shared machinery for the capture groups: where files go, how a texture becomes a PNG, how a
    /// camera or a Scene view is rendered off-screen, and how a capture run is described on disk.
    ///
    /// Two rules shape everything here.
    ///
    /// <b>Never mutate what we are photographing.</b> A camera's <c>targetTexture</c>, the active
    /// render texture, the Scene view's camera and the current render pipeline are saved and restored
    /// in a <c>finally</c>, and temporary cameras and textures are always destroyed. A screenshot
    /// command that leaves the Editor visibly altered is worse than one that fails.
    ///
    /// <b>Bounded output.</b> Every run writes PNG files plus one JSON manifest that records the real
    /// frame number, timestamp, camera state and any per-frame error. Nothing is written into the
    /// package, and nothing is written outside <c>Assets/</c>.
    /// </summary>
    internal static class CaptureSupport
    {
        /// <summary>
        /// Default output root. The trailing <c>~</c> is Unity's convention for a folder the asset
        /// pipeline ignores, so captures never trigger an import and never enter a build.
        /// </summary>
        internal const string DefaultFolder = "Assets/Docs~/cucli-captures";

        internal const string ManifestName = "capture-manifest.json";
        internal const string SessionRelativePath = "capture/recorder.json";

        internal static string SessionPath =>
            Path.Combine(CucliPaths.Root, SessionRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // ------------------------------------------------------------------ paths

        /// <summary>
        /// Resolves <c>--output-folder</c> (also accepted as <c>--folder</c> or <c>--output</c>) to a
        /// project-relative folder under <c>Assets/</c>, creating it on demand. Anything outside
        /// <c>Assets/</c> is rejected rather than silently written to an arbitrary absolute path.
        /// </summary>
        internal static bool TryResolveFolder(CucliArgs args, out string projectRelative, out string absolute, out string error)
        {
            error = null;
            string requested = args.GetString("outputFolder")
                ?? args.GetString("output_folder")
                ?? args.GetString("folder")
                ?? args.GetString("output");
            if (string.IsNullOrWhiteSpace(requested)) requested = DefaultFolder;

            string normalized = requested.Trim().Replace('\\', '/').TrimEnd('/');
            if (Path.IsPathRooted(normalized))
            {
                string projectRoot = CucliPaths.ProjectRoot.Replace('\\', '/');
                if (normalized.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(projectRoot.Length + 1);
                }
                else
                {
                    error = "Output must live inside the project. Use a project-relative folder such as '"
                        + DefaultFolder + "', not '" + requested + "'.";
                    projectRelative = null;
                    absolute = null;
                    return false;
                }
            }

            normalized = normalized.TrimStart('/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = "Output must be under 'Assets/' so the Editor can reference it; got '" + requested + "'.";
                projectRelative = null;
                absolute = null;
                return false;
            }

            projectRelative = "Assets/" + normalized.Substring("Assets/".Length);
            absolute = Path.Combine(
                CucliPaths.ProjectRoot, projectRelative.Replace('/', Path.DirectorySeparatorChar));
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
        /// Builds the file name for one capture and refuses to clobber an existing file unless
        /// <c>--force</c> is set, which is what keeps a capture run reproducible instead of silently
        /// overwriting yesterday's evidence.
        /// </summary>
        internal static bool TryResolveFileName(
            CucliArgs args,
            string folderAbsolute,
            string defaultPrefix,
            string extension,
            int index,
            out string absolutePath,
            out string fileName,
            out string error)
        {
            error = null;
            bool force = args.GetBool("force", false);

            string requested = args.GetString("fileName") ?? args.GetString("file_name")
                ?? args.GetString("filename") ?? args.GetString("outputFile");
            string prefix = args.GetString("prefix");
            if (string.IsNullOrWhiteSpace(prefix)) prefix = defaultPrefix;

            // A caller-supplied name must be a bare file name: a path would escape the folder we just
            // validated, which is exactly the mistake this guard exists to stop.
            if (!string.IsNullOrWhiteSpace(requested))
            {
                requested = requested.Trim();
                if (requested.IndexOf('/') >= 0 || requested.IndexOf('\\') >= 0)
                {
                    error = "'fileName' must be a bare file name without a path; use '--output-folder' for the folder.";
                    absolutePath = null;
                    fileName = null;
                    return false;
                }

                fileName = Path.HasExtension(requested) ? requested : requested + extension;
            }
            else
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                fileName = index > 0
                    ? prefix + "-" + stamp + "-" + index.ToString("D4", CultureInfo.InvariantCulture) + extension
                    : prefix + "-" + stamp + extension;
            }

            absolutePath = Path.Combine(folderAbsolute, fileName);
            if (File.Exists(absolutePath) && !force)
            {
                // A second-resolution timestamp can collide inside one sequence; disambiguate rather
                // than overwrite, unless the caller explicitly asked to overwrite.
                string stem = Path.GetFileNameWithoutExtension(fileName);
                string suffix = Path.GetExtension(fileName);
                int counter = 1;
                while (File.Exists(absolutePath) && counter < 10000)
                {
                    absolutePath = Path.Combine(folderAbsolute, stem + "-" + counter.ToString(CultureInfo.InvariantCulture) + suffix);
                    counter++;
                }

                fileName = Path.GetFileName(absolutePath);
            }

            return true;
        }

        // ------------------------------------------------------------------ texture IO

        /// <summary>Copies the pixels of a render texture into a readable CPU texture.</summary>
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

        internal static bool TryWritePng(Texture2D texture, string absolutePath, out string error)
        {
            error = null;
            if (texture == null)
            {
                error = "no texture was produced.";
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

        /// <summary>Downscales a texture so an inline base64 image cannot flood a response.</summary>
        internal static Texture2D Downscale(Texture2D source, int maxResolution)
        {
            if (source == null || maxResolution <= 0) return source;
            int longest = Mathf.Max(source.width, source.height);
            if (longest <= maxResolution) return source;

            float scale = (float)maxResolution / longest;
            int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            RenderTexture temporary = RenderTexture.GetTemporary(
                width, height, 0, RenderTextureFormat.ARGB32);
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
                // A texture or camera that Unity already reclaimed is not worth failing a capture over.
            }
        }

        // ------------------------------------------------------------------ rendering

        /// <summary>
        /// Renders a camera into <paramref name="target"/> and restores the camera's previous target
        /// texture. Uses the immediate <c>Camera.Render</c> path so a capture does not depend on the
        /// next frame arriving, which is what makes this usable while paused.
        /// </summary>
        internal static bool TryRenderCamera(Camera camera, RenderTexture target, out string error)
        {
            error = null;
            if (camera == null)
            {
                error = "the camera is null.";
                return false;
            }

            if (target == null)
            {
                error = "the render texture is null.";
                return false;
            }

            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                return true;
            }
            catch (Exception exception)
            {
                error = "Camera.Render failed: " + exception.GetBaseException().Message;
                return false;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
            }
        }

        /// <summary>Renders the Scene view's own camera, restoring its target texture afterwards.</summary>
        internal static bool TryRenderSceneView(SceneView view, RenderTexture target, out string error)
        {
            error = null;
            if (view == null)
            {
                error = "no Scene view is open. Open one with Window > General > Scene, or capture the game view instead.";
                return false;
            }

            Camera camera = view.camera;
            if (camera == null)
            {
                error = "the Scene view has no camera yet; give it a moment or repaint it first.";
                return false;
            }

            return TryRenderCamera(camera, target, out error);
        }

        /// <summary>
        /// Resolves the camera a capture should use: an explicit target wins, then <c>Camera.main</c>,
        /// then the first enabled camera that is not a Scene view camera.
        /// </summary>
        internal static bool TryResolveCamera(CucliArgs args, out Camera camera, out string error)
        {
            camera = null;
            error = null;

            if (args.Has("target") || args.Has("camera") || args.Has("cameraName"))
            {
                string requested = args.GetString("target") ?? args.GetString("camera") ?? args.GetString("cameraName");
                var matches = new List<Camera>();
                foreach (Camera candidate in Object.FindObjectsOfType<Camera>())
                {
                    if (candidate == null) continue;
                    if (!string.Equals(candidate.name, requested, StringComparison.Ordinal)) continue;
                    matches.Add(candidate);
                }

                if (matches.Count == 0)
                {
                    int instanceId;
                    if (int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
                    {
                        foreach (Camera candidate in Object.FindObjectsOfType<Camera>())
                        {
                            if (candidate != null && candidate.GetInstanceID() == instanceId)
                            {
                                camera = candidate;
                                return true;
                            }
                        }
                    }

                    error = "no camera named '" + requested + "' exists in the loaded scenes.";
                    return false;
                }

                if (matches.Count > 1)
                {
                    error = matches.Count + " cameras are named '" + requested
                        + "'; pass the instance id from 'cucli camera list' instead.";
                    return false;
                }

                camera = matches[0];
                return true;
            }

            camera = Camera.main;
            if (camera != null) return true;

            foreach (Camera candidate in Object.FindObjectsOfType<Camera>())
            {
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                if (candidate.cameraType == CameraType.SceneView) continue;
                if (candidate.cameraType == CameraType.Preview) continue;
                camera = candidate;
                return true;
            }

            error = "no enabled camera in the loaded scenes; pass --target <name|instanceId>, or create one first.";
            return false;
        }

        /// <summary>
        /// Frames a bounding box with a temporary camera for one axis. The camera is never left behind:
        /// callers destroy it in a <c>finally</c>.
        /// </summary>
        internal static bool TryCreateFramingCamera(
            Bounds bounds,
            Vector3 direction,
            float distanceMultiplier,
            int resolution,
            out Camera camera,
            out string error)
        {
            camera = null;
            error = null;

            try
            {
                var host = new GameObject("cucli-capture-camera");
                host.hideFlags = HideFlags.HideAndDontSave;
                camera = host.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);

                float radius = Mathf.Max(bounds.extents.magnitude, 0.001f);
                camera.transform.position = bounds.center + direction.normalized * radius * Mathf.Max(1f, distanceMultiplier);
                camera.transform.LookAt(bounds.center);
                camera.nearClipPlane = Mathf.Max(0.01f, radius * 0.01f);
                camera.farClipPlane = radius * 100f;
                camera.fieldOfView = 40f;
                camera.aspect = 1f;
                camera.targetTexture = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "could not create a framing camera: " + exception.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>Six axis-aligned directions, used by the <c>surround</c> batch mode.</summary>
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

        /// <summary>Divides a circle into evenly spaced directions at the requested elevations.</summary>
        internal static Vector3[] OrbitDirections(int views, float[] elevations)
        {
            views = Mathf.Clamp(views, 1, 64);
            if (elevations == null || elevations.Length == 0) elevations = new[] { 20f, -10f };

            var directions = new List<Vector3>();
            foreach (float elevation in elevations)
            {
                float pitch = elevation * Mathf.Deg2Rad;
                for (int index = 0; index < views; index++)
                {
                    float yaw = (360f * index / views) * Mathf.Deg2Rad;
                    float horizontal = Mathf.Cos(pitch);
                    directions.Add(new Vector3(
                        Mathf.Sin(yaw) * horizontal,
                        Mathf.Sin(pitch),
                        Mathf.Cos(yaw) * horizontal));
                }
            }

            return directions.ToArray();
        }

        /// <summary>
        /// Bounds of the capture subject: an explicit target object, else every renderer in the loaded
        /// scenes, else a unit box at the origin so a capture still produces something reviewable.
        /// </summary>
        internal static Bounds ResolveBounds(CucliArgs args, GameObject explicitTarget)
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

        // ------------------------------------------------------------------ manifests

        /// <summary>
        /// Writes the run manifest. It is the machine-readable half of a capture: which files, which
        /// real frame numbers, which camera, and every per-frame error - so a partial failure stays
        /// reviewable instead of looking like success.
        /// </summary>
        internal static bool TryWriteManifest(string folderAbsolute, JObject manifest, out string path, out string error)
        {
            error = null;
            path = Path.Combine(folderAbsolute, ManifestName);
            try
            {
                manifest["schemaVersion"] = "cucli.capture-manifest/v1";
                manifest["writtenAtUtc"] = DateTime.UtcNow.ToString("O");
                CucliPaths.WriteAtomic(path, manifest.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
                return true;
            }
            catch (Exception exception)
            {
                error = "could not write the manifest: " + exception.GetBaseException().Message;
                return false;
            }
        }

        internal static JObject CameraJson(Camera camera)
        {
            var result = new JObject
            {
                ["name"] = camera.name,
                ["instanceId"] = camera.GetInstanceID(),
                ["enabled"] = camera.enabled,
                ["orthographic"] = camera.orthographic,
                ["fieldOfView"] = camera.orthographic ? 0f : camera.fieldOfView,
                ["orthographicSize"] = camera.orthographic ? camera.orthographicSize : 0f,
                ["nearClipPlane"] = camera.nearClipPlane,
                ["farClipPlane"] = camera.farClipPlane,
                ["depth"] = camera.depth,
                ["cullingMask"] = camera.cullingMask,
                ["clearFlags"] = camera.clearFlags.ToString(),
                ["pixelWidth"] = camera.pixelWidth,
                ["pixelHeight"] = camera.pixelHeight,
                ["position"] = CucliDescribe.Value(camera.transform.position, null, 0),
                ["rotation"] = CucliDescribe.Value(camera.transform.rotation, null, 0)
            };

            if (camera.targetTexture != null)
            {
                result["targetTexture"] = camera.targetTexture.name;
                result["targetTextureInstanceId"] = camera.targetTexture.GetInstanceID();
            }

            return result;
        }

        /// <summary>Render-pipeline identity without referencing URP: the asset type name is enough.</summary>
        internal static JObject PipelineJson()
        {
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            return new JObject
            {
                ["asset"] = asset == null ? null : asset.name,
                ["assetType"] = asset == null ? "Built-in" : asset.GetType().Name,
                ["isScriptable"] = asset != null,
                ["qualityLevel"] = QualitySettings.names != null && QualitySettings.GetQualityLevel() < QualitySettings.names.Length
                    ? QualitySettings.names[QualitySettings.GetQualityLevel()]
                    : QualitySettings.GetQualityLevel().ToString(CultureInfo.InvariantCulture),
                ["colorSpace"] = PlayerSettings.colorSpace.ToString()
            };
        }

        /// <summary>Frames the current Editor time so a capture can be tied back to a timeline.</summary>
        internal static JObject TimeJson()
        {
            return new JObject
            {
                ["frameCount"] = Time.frameCount,
                ["renderedFrameCount"] = Time.renderedFrameCount,
                ["time"] = Time.time,
                ["unscaledTime"] = Time.unscaledTime,
                ["realtimeSinceStartup"] = Time.realtimeSinceStartup,
                ["deltaTime"] = Time.deltaTime,
                ["unscaledDeltaTime"] = Time.unscaledDeltaTime,
                ["timeScale"] = Time.timeScale,
                ["captureFramerate"] = Time.captureFramerate,
                ["targetFrameRate"] = Application.targetFrameRate,
                ["vSyncCount"] = QualitySettings.vSyncCount,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused
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

        /// <summary>Enumerates capture folders under the default root, newest first.</summary>
        internal static List<string> ListCaptureFolders(int limit)
        {
            var folders = new List<string>();
            string root = Path.Combine(
                CucliPaths.ProjectRoot, DefaultFolder.Replace('/', Path.DirectorySeparatorChar));
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
    /// Which cameras actually rendered during an observation window.
    ///
    /// <c>rendered == false</c> is not a diagnosis: the camera may be disabled, culled, on-demand, or
    /// simply not shown. It only means "no render event for this camera was observed", which is why the
    /// wording of the result matters as much as the value.
    /// </summary>
    internal sealed class RenderObservation
    {
        private readonly Dictionary<int, string> m_rendered = new Dictionary<int, string>();
        private bool m_hooked;

        internal int FramesObserved { get; private set; }
        internal int FirstFrameNumber { get; private set; } = -1;
        internal int LastFrameNumber { get; private set; } = -1;

        internal IReadOnlyDictionary<int, string> Rendered => m_rendered;

        internal void Attach()
        {
            if (m_hooked) return;
            Camera.onPreRender += OnBuiltInCamera;
            RenderPipelineManager.beginCameraRendering += OnScriptableCamera;
            m_hooked = true;
        }

        internal void Detach()
        {
            if (!m_hooked) return;
            Camera.onPreRender -= OnBuiltInCamera;
            RenderPipelineManager.beginCameraRendering -= OnScriptableCamera;
            m_hooked = false;
        }

        internal void NoteTick()
        {
            FramesObserved++;
            int frame = Time.frameCount;
            if (FirstFrameNumber < 0) FirstFrameNumber = frame;
            LastFrameNumber = frame;
        }

        private void OnBuiltInCamera(Camera camera)
        {
            Record(camera);
        }

        private void OnScriptableCamera(ScriptableRenderContext context, Camera camera)
        {
            Record(camera);
        }

        private void Record(Camera camera)
        {
            if (camera == null) return;
            int id = camera.GetInstanceID();
            if (!m_rendered.ContainsKey(id))
            {
                m_rendered[id] = camera.name + " (" + camera.cameraType + ")";
            }
        }
    }
}
