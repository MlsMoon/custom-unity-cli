using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli camera ...</c> - the camera surface, in two tiers.
    ///
    /// <b>Tier 1</b> (ping, create_camera, set_target, set_lens, set_priority, list_cameras,
    /// screenshot, screenshot_multiview) uses nothing but the built-in <see cref="Camera"/> component.
    ///
    /// <b>Tier 2</b> (ensure_brain, get_brain_status, set_body, set_aim, set_noise, add_extension,
    /// remove_extension, set_blend, force_camera, release_override) is Cinemachine. The package is
    /// never referenced at compile time: every type comes from <see cref="CucliOptional"/>, and when
    /// <c>com.unity.cinemachine</c> is missing those ten actions - and only those - fail with
    /// <c>OPTIONAL_PACKAGE_MISSING</c>.
    ///
    /// Screenshots need at least one rendered frame, so the group implements
    /// <see cref="ICucliDeferredCommand"/>; the MCP surface forwards screenshots to
    /// <c>manage_scene</c>, and this is the working subset of that contract.
    /// </summary>
    internal sealed class CameraCommand : CucliCommand, ICucliDeferredCommand
    {
        internal const string CinemachinePackage = "com.unity.cinemachine";

        internal const string CameraParametersSchemaJson = @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""ping"", ""create_camera"", ""set_target"", ""set_lens"", ""set_priority"", ""list_cameras"",
                 ""screenshot"", ""screenshot_multiview"", ""ensure_brain"", ""get_brain_status"", ""set_body"",
                 ""set_aim"", ""set_noise"", ""add_extension"", ""remove_extension"", ""set_blend"",
                 ""force_camera"", ""release_override""],
      ""default"": ""ping""
    },
    ""target"": { ""type"": ""string"", ""description"": ""Camera or Cinemachine camera: name, hierarchy path or instance id; 'main' selects Camera.main."" },
    ""tag"": { ""type"": ""string"", ""description"": ""Select the camera by GameObject tag, e.g. MainCamera."" },
    ""searchMethod"": { ""type"": ""string"", ""enum"": [""by_id_or_name_or_path"", ""by_id"", ""by_name"", ""by_path"", ""by_tag""] },
    ""properties"": { ""type"": ""object"", ""description"": ""Operation payload; nested keys are read exactly as documented."" },
    ""name"": { ""type"": ""string"", ""description"": ""action=create_camera: new GameObject name."" },
    ""preset"": { ""type"": ""string"", ""enum"": [""follow"", ""third_person"", ""freelook"", ""dolly"", ""static"", ""top_down"", ""side_scroller""], ""description"": ""action=create_camera with Cinemachine."" },
    ""fieldOfView"": { ""type"": ""number"", ""description"": ""action=create_camera / set_lens."" },
    ""nearClipPlane"": { ""type"": ""number"", ""description"": ""action=create_camera / set_lens."" },
    ""farClipPlane"": { ""type"": ""number"", ""description"": ""action=create_camera / set_lens."" },
    ""orthographicSize"": { ""type"": ""number"", ""description"": ""action=set_lens."" },
    ""priority"": { ""type"": ""number"", ""description"": ""action=create_camera / set_priority."" },
    ""viewTarget"": { ""type"": ""string"", ""description"": ""action=create_camera / screenshot: look-at or orbit centre object."" },
    ""camera"": { ""type"": ""string"", ""description"": ""action=screenshot: camera name, path or instance id. Forbidden with captureSource=scene_view."" },
    ""captureSource"": { ""type"": ""string"", ""enum"": [""game_view"", ""scene_view""], ""default"": ""game_view"" },
    ""width"": { ""type"": ""integer"", ""default"": 1920, ""description"": ""action=screenshot: output width."" },
    ""height"": { ""type"": ""integer"", ""default"": 1080, ""description"": ""action=screenshot: output height."" },
    ""superSize"": { ""type"": ""integer"", ""default"": 1, ""description"": ""action=screenshot: supersampling multiplier."" },
    ""includeImage"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=screenshot: inline base64 PNG."" },
    ""maxResolution"": { ""type"": ""integer"", ""default"": 640, ""description"": ""action=screenshot: longest side of the inlined image."" },
    ""filename"": { ""type"": ""string"", ""description"": ""action=screenshot: output file name; '.png' is appended when missing."" },
    ""outputFolder"": { ""type"": ""string"", ""description"": ""action=screenshot: output folder; defaults to Assets/Docs~/."" },
    ""order"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""action=screenshot_multiview: unused, kept for parity with the surface."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=screenshot: overwrite an existing file."" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 32 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 2 },
    ""pageSize"": { ""type"": ""integer"", ""default"": 100 },
    ""cursor"": { ""type"": ""string"" },
    ""timeoutSeconds"": { ""type"": ""integer"", ""description"": ""action=screenshot: wait cap for the capture."" }
  }
}";

        public override string Name => "camera";

        public override string Summary =>
            "Inspect, create and drive Cameras; Cinemachine brains, bodies, aims, noise, extensions and blends; screenshots.";

        public override CucliActionInfo[] Actions => Declare(
            "ping", "Report whether Cinemachine is installed and how many cameras the scene has.",
            "create_camera", "Create a Camera; with Cinemachine installed the Cinemachine path is used.",
            "set_target", "Set the follow and look-at target of a Camera or Cinemachine camera.",
            "set_lens", "Set field of view, clip planes, orthographic size and Dutch.",
            "set_priority", "Set Cinemachine priority, or Camera.depth on a basic camera.",
            "list_cameras", "List the brain, the Cinemachine cameras and the Unity cameras.",
            "screenshot", "Render one camera view to a PNG under Assets/Docs~.",
            "screenshot_multiview", "Render the six surround views around a target as PNGs.",
            "ensure_brain", "Create a CinemachineBrain on a camera when it has none.",
            "get_brain_status", "Report the active Cinemachine camera and blend state.",
            "set_body", "Replace or configure the Cinemachine Body stage component.",
            "set_aim", "Replace or configure the Cinemachine Aim stage component.",
            "set_noise", "Add or configure the Cinemachine noise component.",
            "add_extension", "Attach a CinemachineExtension by type name.",
            "remove_extension", "Remove every CinemachineExtension of a type.",
            "set_blend", "Set the Cinemachine default blend style and duration.",
            "force_camera", "Force a Cinemachine camera live through an override or by priority.",
            "release_override", "Release the active Cinemachine camera override.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        // Deliberately null, not CinemachinePackage.
        //
        // Only the Tier 2 actions need Cinemachine, and every one of them independently calls
        // CucliOptional.RequirePackage before doing anything. Declaring the package here would make
        // the catalog advertise the whole group as unavailable, which would hide the Tier 1
        // inspect/create/configure/screenshot actions from a project that simply has no
        // Cinemachine installed - exactly the projects most likely to want them.
        public override string OptionalPackage => null;

        public override string[] Effects => new[]
        {
            "project.assets.write", "scene.camera.write", "scene.camera.read"
        };

        public override string ParametersSchemaJson => CameraParametersSchemaJson;

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "ping");
            switch (action)
            {
                case "ping": return Ping(context);
                case "create_camera": return CreateCamera(context);
                case "set_target": return SetTarget(context);
                case "set_lens": return SetLens(context);
                case "set_priority": return SetPriority(context);
                case "list_cameras": return ListCameras(context);
                case "ensure_brain": return EnsureBrain(context);
                case "get_brain_status": return GetBrainStatus(context);
                case "set_body": return SetBody(context);
                case "set_aim": return SetAim(context);
                case "set_noise": return SetNoise(context);
                case "add_extension": return AddExtension(context);
                case "remove_extension": return RemoveExtension(context);
                case "set_blend": return SetBlend(context);
                case "force_camera": return ForceCamera(context);
                case "release_override": return ReleaseOverride(context);
                case "screenshot":
                case "screenshot_multiview":
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "Action '" + action + "' needs a rendered frame and must be dispatched through the deferred path.",
                        new { action, deferred = true });
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string action = ResolveAction(context, "ping");
            switch (action)
            {
                case "screenshot":
                    CameraCapture.Execute(context, complete, false);
                    return;
                case "screenshot_multiview":
                    CameraCapture.Execute(context, complete, true);
                    return;
                default:
                    complete(Execute(context));
                    return;
            }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Resolves a Camera from <c>target</c> / <c>tag</c> / <c>camera</c>. Several cameras sharing
        /// one name is an <c>TARGET_AMBIGUOUS</c> error rather than a silent first pick.
        /// </summary>
        internal static Camera ResolveCamera(CucliCommandContext context, out CucliCommandResult failure)
        {
            failure = null;
            CucliArgs args = context.Input;

            string target = args.GetString("target") ?? args.GetString("camera");
            string tag = args.GetString("tag");

            if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(tag))
            {
                // manage_camera defaults to Camera.main; the GameObject resolver is tried first so a
                // camera that is not tagged MainCamera is still reachable by name or path.
                string ignored;
                GameObject byName = CucliObjectResolver.ResolveGameObject(args, out ignored);
                if (byName != null)
                {
                    Camera direct = byName.GetComponent<Camera>();
                    if (direct != null) return direct;
                }

                Camera main = Camera.main;
                if (main == null)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        "No 'target' was given and Camera.main is null (no enabled camera is tagged MainCamera).",
                        new { hint = "Pass target (name, hierarchy path or instance id) or tag." });
                    return null;
                }

                return main;
            }

            if (string.Equals(target, "main", StringComparison.OrdinalIgnoreCase))
            {
                Camera main = Camera.main;
                if (main == null)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, "Camera.main is null; no enabled camera is tagged MainCamera.");
                    return null;
                }

                return main;
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                List<Camera> byTag = new List<Camera>();
                foreach (GameObject candidate in CucliObjectResolver.FindAllInLoadedScenes())
                {
                    bool tagMatches;
                    try
                    {
                        tagMatches = string.Equals(candidate.tag, tag.Trim(), StringComparison.Ordinal);
                    }
                    catch (UnityException)
                    {
                        tagMatches = false;
                    }

                    if (!tagMatches) continue;
                    Camera camera = candidate.GetComponent<Camera>();
                    if (camera != null) byTag.Add(camera);
                }

                return Single(byTag, "tagged '" + tag + "'", out failure);
            }

            int numeric;
            if (int.TryParse(target.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                UnityEngine.Object resolved = EditorUtility.InstanceIDToObject(numeric);
                Camera direct = resolved as Camera;
                if (direct == null && resolved is GameObject) direct = ((GameObject)resolved).GetComponent<Camera>();
                if (direct == null && resolved is Component)
                {
                    direct = ((Component)resolved).GetComponent<Camera>();
                }

                if (direct == null)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, "No Camera has instance id " + numeric + ".");
                    return null;
                }

                return direct;
            }

            if (target.IndexOf('/') >= 0)
            {
                string pathError;
                GameObject byPath = CucliObjectResolver.FindByHierarchyPath(target, out pathError);
                if (byPath == null)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        pathError ?? ("No GameObject is at hierarchy path '" + target + "'."));
                    return null;
                }

                Camera camera = byPath.GetComponent<Camera>();
                if (camera == null)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, byPath.name + " has no Camera component.");
                    return null;
                }

                return camera;
            }

            List<Camera> matches = FindCamerasByName(target.Trim());
            return Single(matches, "named '" + target + "'", out failure);
        }

        internal static List<Camera> FindCamerasByName(string name)
        {
            var matches = new List<Camera>();
            foreach (Camera camera in CucliAssets.FindAll<Camera>())
            {
                if (camera == null) continue;
                if (EditorUtility.IsPersistent(camera)) continue;
                if (!string.Equals(camera.gameObject.name, name, StringComparison.Ordinal)) continue;
                matches.Add(camera);
            }

            return matches;
        }

        private static Camera Single(List<Camera> matches, string description, out CucliCommandResult failure)
        {
            failure = null;
            if (matches.Count == 0)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, "No Camera is " + description + " in the loaded scenes.");
                return null;
            }

            if (matches.Count > 1)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetAmbiguous,
                    matches.Count + " Cameras are " + description + ". Pass an instance id or a hierarchy path.",
                    new
                    {
                        matches = matches.Select(camera => new
                        {
                            instanceId = camera.gameObject.GetInstanceID(),
                            path = CucliObjectResolver.HierarchyPath(camera.gameObject, true)
                        }).ToArray()
                    });
                return null;
            }

            return matches[0];
        }

        /// <summary>The compact Camera field list this group reports, rather than every property.</summary>
        internal static JObject Describe(Camera camera)
        {
            if (camera == null) return null;

            Texture target = camera.targetTexture;
            return new JObject
            {
                ["instanceId"] = camera.gameObject.GetInstanceID(),
                ["name"] = camera.name,
                ["path"] = CucliObjectResolver.HierarchyPath(camera.gameObject, true),
                ["enabled"] = camera.enabled,
                ["projection"] = camera.orthographic ? "Orthographic" : "Perspective",
                ["fov"] = camera.orthographic ? (float?)null : camera.fieldOfView,
                ["orthographicSize"] = camera.orthographic ? (float?)camera.orthographicSize : null,
                ["near"] = camera.nearClipPlane,
                ["far"] = camera.farClipPlane,
                ["depth"] = camera.depth,
                ["cullingMask"] = camera.cullingMask,
                ["clearFlags"] = camera.clearFlags.ToString(),
                ["targetTexture"] = target == null ? null : target.name,
                ["targetTexturePath"] = target == null
                    ? null
                    : GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(target)),
                ["pixelWidth"] = camera.pixelWidth,
                ["pixelHeight"] = camera.pixelHeight,
                ["tag"] = SafeTag(camera.gameObject)
            };
        }

        internal static string SafeTag(GameObject gameObject)
        {
            if (gameObject == null) return null;
            try
            {
                return gameObject.tag;
            }
            catch (UnityException)
            {
                return null;
            }
        }

        /// <summary>Reads a property dictionary key, accepting the documented snake_case alias too.</summary>
        internal static JToken Property(JObject properties, string camel, string snake)
        {
            if (properties == null) return null;
            JToken value = properties[camel];
            if (value != null) return value;
            return snake == null ? null : properties[snake];
        }

        internal static JObject PropertyObject(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            JObject properties = args.GetObject("properties");
            if (properties != null) return properties;

            // manage_camera also accepts a JSON string.
            string raw = args.GetString("properties");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                return CucliJson.ParseObject(raw);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool TryResolveGameObject(JToken token, out GameObject gameObject, out string error)
        {
            gameObject = null;
            error = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                error = "the value is missing.";
                return false;
            }

            var args = new CucliArgs(new JObject { ["target"] = token });
            gameObject = CucliObjectResolver.ResolveGameObject(args, out error);
            return gameObject != null;
        }

        // ---------------------------------------------------------------- ping

        private static CucliCommandResult Ping(CucliCommandContext context)
        {
            bool installed = CucliOptional.IsPackageInstalled(CinemachinePackage);
            int cameras = CucliAssets.FindAll<Camera>().Count(camera => camera != null);

            return CucliCommandResult.Ok(new
            {
                cinemachine = installed,
                version = installed ? CucliOptional.PackageVersion(CinemachinePackage) : null,
                unityCameraCount = cameras,
                tier1Actions = 8,
                tier2Actions = 10,
                optionalPackage = installed ? null : CinemachinePackage
            }, installed
                ? "Cinemachine is installed; all 18 camera actions are available."
                : "Cinemachine is not installed; the ten Tier 2 actions will fail with OPTIONAL_PACKAGE_MISSING.");
        }

        // ---------------------------------------------------------------- create_camera

        private static CucliCommandResult CreateCamera(CucliCommandContext context)
        {
            JObject properties = PropertyObject(context) ?? new JObject();
            bool cinemachine = CucliOptional.IsPackageInstalled(CinemachinePackage)
                && CinemachineSupport.CameraType() != null;

            string name = properties.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) name = cinemachine ? "CM Camera" : "Camera";

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "cucli camera create_camera");

            Camera camera = gameObject.AddComponent<Camera>();
            float fov = properties["fieldOfView"] == null ? 60f : properties.Value<float>("fieldOfView");
            float near = properties["nearClipPlane"] == null ? 0.3f : properties.Value<float>("nearClipPlane");
            float far = properties["farClipPlane"] == null ? 1000f : properties.Value<float>("farClipPlane");

            camera.fieldOfView = fov;
            camera.nearClipPlane = near;
            camera.farClipPlane = far;

            GameObject target = null;
            JToken followToken = properties["follow"];
            if (followToken != null) TryResolveGameObject(followToken, out target, out string ignoredTarget);
            JToken lookToken = properties["lookAt"] ?? properties["look_at"];
            if (lookToken != null)
            {
                GameObject lookAt;
                if (TryResolveGameObject(lookToken, out lookAt, out string ignoredLook)) target = lookAt;
            }

            // The basic path places the camera so that it looks at the target from a short distance.
            if (target != null)
            {
                Vector3 direction = (target.transform.position - new Vector3(0f, 0f, 0f)).normalized;
                if (direction.sqrMagnitude < 0.001f) direction = Vector3.forward;
                gameObject.transform.position = target.transform.position - direction * 5f + Vector3.up * 2f;
                gameObject.transform.LookAt(target.transform);
            }

            EditorUtility.SetDirty(camera);
            Selection.activeGameObject = gameObject;
            UiCommand.MarkSceneDirty(gameObject);

            if (!cinemachine)
            {
                return CucliCommandResult.Ok(new
                {
                    instanceID = gameObject.GetInstanceID(),
                    cinemachine = false,
                    hint = CucliOptional.IsPackageInstalled(CinemachinePackage)
                        ? "The Cinemachine camera type could not be resolved; a plain Camera was created."
                        : "Install com.unity.cinemachine to create Cinemachine cameras with a preset."
                }, "Created a basic Camera named " + name + ".");
            }

            string preset = properties.Value<string>("preset");
            if (string.IsNullOrWhiteSpace(preset)) preset = "follow";
            preset = preset.Trim().ToLowerInvariant();

            string presetError;
            if (!CinemachineSupport.IsKnownPreset(preset, out presetError))
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                return CucliCommandResult.Invalid("preset", presetError);
            }

            object cinemachineCamera;
            string addError;
            if (!CinemachineSupport.TryAddCamera(gameObject, out cinemachineCamera, out addError))
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The Cinemachine camera could not be added: " + addError);
            }

            CinemachineSupport.ApplyPreset(cinemachineCamera, preset);

            float priority = properties["priority"] == null ? 10f : properties.Value<float>("priority");
            CinemachineSupport.SetPriority(cinemachineCamera, priority);

            if (target != null)
            {
                CinemachineSupport.SetFollow(cinemachineCamera, target);
                CinemachineSupport.SetLookAt(cinemachineCamera, target);
            }

            EditorUtility.SetDirty(cinemachineCamera as UnityEngine.Object);
            UiCommand.MarkSceneDirty(gameObject);

            return CucliCommandResult.Ok(new
            {
                instanceID = gameObject.GetInstanceID(),
                cinemachine = true,
                preset,
                priority,
                body = CinemachineSupport.StageTypeName(cinemachineCamera, "Body"),
                aim = CinemachineSupport.StageTypeName(cinemachineCamera, "Aim")
            }, "Created a Cinemachine camera named " + name + " with the '" + preset + "' preset.");
        }

        // ---------------------------------------------------------------- set_lens / set_target / set_priority

        private static CucliCommandResult SetLens(CucliCommandContext context)
        {
            Camera camera = ResolveCamera(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            JObject properties = PropertyObject(context);
            if (properties == null || properties.Count == 0)
            {
                return CucliCommandResult.Missing("properties", "Provide at least one lens property.");
            }

            bool cinemachine = CinemachineSupport.IsAvailable()
                && CinemachineSupport.FindCamera(camera.gameObject) != null;

            var changed = new JArray();
            Undo.RecordObject(camera, "cucli camera set_lens");

            if (properties["fieldOfView"] != null)
            {
                float fov = properties.Value<float>("fieldOfView");
                bool applied;
                if (cinemachine) applied = CinemachineSupport.SetLensField(camera.gameObject, "FieldOfView", fov);
                else
                {
                    camera.fieldOfView = fov;
                    applied = true;
                }

                if (applied) changed.Add("fieldOfView");
            }

            if (properties["nearClipPlane"] != null)
            {
                float value = properties.Value<float>("nearClipPlane");
                bool applied = cinemachine
                    ? CinemachineSupport.SetLensField(camera.gameObject, "NearClipPlane", value)
                    : SetCameraClip(camera, true, value);
                if (applied) changed.Add("nearClipPlane");
            }

            if (properties["farClipPlane"] != null)
            {
                float value = properties.Value<float>("farClipPlane");
                bool applied = cinemachine
                    ? CinemachineSupport.SetLensField(camera.gameObject, "FarClipPlane", value)
                    : SetCameraClip(camera, false, value);
                if (applied) changed.Add("farClipPlane");
            }

            if (properties["orthographicSize"] != null)
            {
                float value = properties.Value<float>("orthographicSize");
                if (cinemachine) CinemachineSupport.SetLensField(camera.gameObject, "OrthographicSize", value);
                else camera.orthographicSize = value;
                changed.Add("orthographicSize");
            }

            if (properties["dutch"] != null && cinemachine)
            {
                if (CinemachineSupport.SetLensField(camera.gameObject, "Dutch", properties.Value<float>("dutch")))
                {
                    changed.Add("dutch");
                }
            }

            EditorUtility.SetDirty(camera);
            UiCommand.MarkSceneDirty(camera.gameObject);

            if (changed.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "No lens property was applied. Supported keys: fieldOfView, nearClipPlane, farClipPlane, "
                    + "orthographicSize" + (cinemachine ? ", dutch" : " (dutch needs a Cinemachine camera)") + ".",
                    new { cinemachine });
            }

            return CucliCommandResult.Ok(new
            {
                instanceID = camera.gameObject.GetInstanceID(),
                cinemachine,
                changed
            }, "Updated " + changed.Count + " lens property(ies) on " + camera.name + ".");
        }

        private static bool SetCameraClip(Camera camera, bool near, float value)
        {
            if (near) camera.nearClipPlane = value;
            else camera.farClipPlane = value;
            return true;
        }

        private static CucliCommandResult SetTarget(CucliCommandContext context)
        {
            Camera camera = ResolveCamera(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            JObject properties = PropertyObject(context);
            JToken followToken = Property(properties, "follow", null);
            JToken lookToken = Property(properties, "lookAt", "look_at");
            if (lookToken == null) lookToken = followToken;

            if (followToken == null && lookToken == null)
            {
                return CucliCommandResult.Missing(
                    "properties", "Provide properties.follow and/or properties.lookAt.");
            }

            bool cinemachine = CinemachineSupport.IsAvailable()
                && CinemachineSupport.FindCamera(camera.gameObject) != null;

            GameObject follow = null;
            if (followToken != null && !TryResolveGameObject(followToken, out follow, out string followError))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, "follow: " + followError);
            }

            GameObject lookAt = null;
            if (lookToken != null && !TryResolveGameObject(lookToken, out lookAt, out string lookError))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, "lookAt: " + lookError);
            }

            if (cinemachine)
            {
                if (follow != null) CinemachineSupport.SetFollow(camera.gameObject, follow);
                if (lookAt != null) CinemachineSupport.SetLookAt(camera.gameObject, lookAt);
            }
            else
            {
                Undo.RecordObject(camera.transform, "cucli camera set_target");
                if (lookAt != null) camera.transform.LookAt(lookAt.transform);
                else if (follow != null) camera.transform.LookAt(follow.transform);
                UiCommand.MarkSceneDirty(camera.gameObject);
            }

            return CucliCommandResult.Ok(new
            {
                instanceID = camera.gameObject.GetInstanceID(),
                cinemachine,
                follow = follow == null ? null : CucliObjectResolver.HierarchyPath(follow, true),
                lookAt = lookAt == null ? null : CucliObjectResolver.HierarchyPath(lookAt, true)
            }, "Updated the target of " + camera.name + ".");
        }

        private static CucliCommandResult SetPriority(CucliCommandContext context)
        {
            Camera camera = ResolveCamera(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            JObject properties = PropertyObject(context);
            float priority = 10f;
            if (properties != null && properties["priority"] != null)
            {
                priority = properties.Value<float>("priority");
            }
            else if (context.Input.Has("priority"))
            {
                priority = context.Input.GetFloat("priority", 10f);
            }

            bool cinemachine = CinemachineSupport.IsAvailable()
                && CinemachineSupport.FindCamera(camera.gameObject) != null;

            if (cinemachine)
            {
                if (!CinemachineSupport.SetPriority(camera.gameObject, priority))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "The Cinemachine priority of " + camera.name + " could not be written.");
                }

                return CucliCommandResult.Ok(new
                {
                    instanceID = camera.gameObject.GetInstanceID(),
                    cinemachine = true,
                    priority
                }, "Cinemachine priority set to " + priority + ".");
            }

            Undo.RecordObject(camera, "cucli camera set_priority");
            camera.depth = priority;
            EditorUtility.SetDirty(camera);
            UiCommand.MarkSceneDirty(camera.gameObject);

            return CucliCommandResult.Ok(new
            {
                instanceID = camera.gameObject.GetInstanceID(),
                cinemachine = false,
                depth = camera.depth
            }, "Camera.depth set to " + camera.depth + " (a basic camera has no Cinemachine priority).");
        }

        // ---------------------------------------------------------------- list_cameras

        private static CucliCommandResult ListCameras(CucliCommandContext context)
        {
            bool installed = CucliOptional.IsPackageInstalled(CinemachinePackage);
            var options = CucliDescribe.OptionsFrom(context.Input);

            JToken brain = null;
            if (installed) brain = CinemachineSupport.DescribeBrains();

            var cinemachineCameras = new JArray();
            if (installed) cinemachineCameras = CinemachineSupport.DescribeCameras(options);

            var unityCameras = new JArray();
            foreach (Camera camera in CucliAssets.FindAll<Camera>())
            {
                if (camera == null || EditorUtility.IsPersistent(camera)) continue;
                unityCameras.Add(new JObject
                {
                    ["instanceID"] = camera.gameObject.GetInstanceID(),
                    ["name"] = camera.gameObject.name,
                    ["depth"] = camera.depth,
                    ["fieldOfView"] = camera.fieldOfView,
                    ["hasBrain"] = installed && CinemachineSupport.HasBrain(camera.gameObject)
                });
            }

            return CucliCommandResult.Ok(new
            {
                cinemachineInstalled = installed,
                brain,
                cinemachineCameras,
                unityCameras,
                unityCameraCount = unityCameras.Count,
                cinemachineCameraCount = cinemachineCameras.Count
            });
        }

        // ---------------------------------------------------------------- Tier 2: brain

        private static CucliCommandResult EnsureBrain(CucliCommandContext context)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            JObject properties = PropertyObject(context);
            Camera camera = ResolveCamera(context, out CucliCommandResult failure);
            if (failure != null)
            {
                // manage_camera falls back to Camera.main / the first Camera for ensure_brain.
                camera = Camera.main;
                if (camera == null)
                {
                    foreach (Camera candidate in CucliAssets.FindAll<Camera>())
                    {
                        if (candidate == null || EditorUtility.IsPersistent(candidate)) continue;
                        camera = candidate;
                        break;
                    }
                }

                if (camera == null) return failure;
            }

            bool existed = CinemachineSupport.HasBrain(camera.gameObject);
            if (!existed)
            {
                string error;
                if (!CinemachineSupport.TryAddBrain(camera.gameObject, out error))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "The CinemachineBrain could not be added: " + error);
                }

                UiCommand.MarkSceneDirty(camera.gameObject);
            }

            var changed = new JArray();
            if (properties != null)
            {
                JToken styleToken = Property(properties, "defaultBlendStyle", "default_blend_style");
                if (styleToken != null)
                {
                    string styleError;
                    if (!CinemachineSupport.SetDefaultBlendStyle(
                            camera.gameObject, styleToken.ToString(), out styleError))
                    {
                        return CucliCommandResult.Invalid("defaultBlendStyle", styleError);
                    }

                    changed.Add("defaultBlendStyle");
                }

                JToken durationToken = Property(properties, "defaultBlendDuration", "default_blend_duration");
                if (durationToken != null)
                {
                    float duration = durationToken.Value<float>();
                    if (duration >= 0f)
                    {
                        string durationError;
                        if (!CinemachineSupport.SetDefaultBlendDuration(
                                camera.gameObject, duration, out durationError))
                        {
                            return CucliCommandResult.Fail(CucliErrors.CommandFailed, durationError);
                        }

                        changed.Add("defaultBlendDuration");
                    }
                }
            }

            return CucliCommandResult.Ok(new
            {
                instanceID = camera.gameObject.GetInstanceID(),
                alreadyExisted = existed,
                changed
            }, existed
                ? "The camera already had a CinemachineBrain; its settings were updated."
                : "Added a CinemachineBrain to " + camera.name + ".");
        }

        private static CucliCommandResult GetBrainStatus(CucliCommandContext context)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            Camera camera = ResolveCamera(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            Component brain = CinemachineSupport.FindBrain(camera.gameObject);
            if (brain == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    camera.name + " has no CinemachineBrain. Add one with 'cucli camera ensure_brain'.",
                    new { gameObject = CucliObjectResolver.HierarchyPath(camera.gameObject, true) });
            }

            object active = CinemachineSupport.GetActiveVirtualCamera(brain);
            var activeObject = active as UnityEngine.Object;
            bool isBlending = CinemachineSupport.IsBlending(brain);

            return CucliCommandResult.Ok(new
            {
                gameObject = CucliObjectResolver.HierarchyPath(camera.gameObject, true),
                instanceID = camera.gameObject.GetInstanceID(),
                activeCameraName = activeObject == null ? null : activeObject.name,
                activeCameraID = activeObject == null ? (int?)null : activeObject.GetInstanceID(),
                isBlending,
                blendDescription = isBlending
                    ? CinemachineSupport.DescribeActiveBlend(brain)
                    : null
            });
        }

        // ---------------------------------------------------------------- Tier 2: body / aim / noise

        private static CucliCommandResult SetBody(CucliCommandContext context)
        {
            return SetStage(context, "Body");
        }

        private static CucliCommandResult SetAim(CucliCommandContext context)
        {
            return SetStage(context, "Aim");
        }

        private static CucliCommandResult SetStage(CucliCommandContext context, string stage)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            JObject properties = PropertyObject(context);
            if (properties == null) properties = new JObject();

            GameObject host = CinemachineSupport.ResolveCameraHost(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            string typeKey = stage == "Body" ? "bodyType" : "aimType";
            string typeKeySnake = stage == "Body" ? "body_type" : "aim_type";
            JToken typeToken = Property(properties, typeKey, typeKeySnake);

            object cinemachineCamera = CinemachineSupport.FindCamera(host);
            if (cinemachineCamera == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    host.name + " has no CinemachineCamera component.",
                    new { gameObject = CucliObjectResolver.HierarchyPath(host, true) });
            }

            string appliedTypeName;
            if (typeToken != null && !string.IsNullOrWhiteSpace(typeToken.ToString()))
            {
                object component;
                string error;
                if (!CinemachineSupport.TryReplaceStage(
                        cinemachineCamera, stage, typeToken.ToString().Trim(), out component, out error))
                {
                    return CucliCommandResult.Fail(CucliErrors.TypeNotFound, error);
                }

                appliedTypeName = component.GetType().Name;
            }
            else
            {
                object existing = CinemachineSupport.StageComponent(cinemachineCamera, stage);
                if (existing == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        host.name + " has no " + stage + " stage component. Provide properties." + typeKey
                        + " to attach one.",
                        new { gameObject = CucliObjectResolver.HierarchyPath(host, true) });
                }

                appliedTypeName = existing.GetType().Name;
            }

            object target = CinemachineSupport.StageComponent(cinemachineCamera, stage);
            var changed = new JArray();
            var failed = new JArray();
            foreach (JProperty property in properties.Properties())
            {
                if (property.Name == typeKey || property.Name == typeKeySnake) continue;
                context.ThrowIfCancelled();

                string error;
                if (CucliSerialized.TrySet(target as UnityEngine.Object, property.Name, property.Value,
                        "cucli camera set_" + stage.ToLowerInvariant(), out error, out bool handled))
                {
                    changed.Add(property.Name);
                }
                else if (handled)
                {
                    failed.Add(new JObject { ["key"] = property.Name, ["reason"] = error });
                }
                else if (SetComponentMember(target, property.Name, property.Value, out error))
                {
                    changed.Add(property.Name);
                }
                else
                {
                    failed.Add(new JObject { ["key"] = property.Name, ["reason"] = error });
                }
            }

            if (target is UnityEngine.Object) EditorUtility.SetDirty((UnityEngine.Object)target);
            if (failed.Count > 0) context.Warn(failed.Count + " " + stage + " property(ies) were not applied.");

            return CucliCommandResult.Ok(new
            {
                instanceID = host.GetInstanceID(),
                stage = stage.ToLowerInvariant(),
                component = appliedTypeName,
                changed,
                failed
            }, "Configured the " + stage + " stage of " + host.name + ".");
        }

        private static bool SetComponentMember(object target, string member, JToken value, out string error)
        {
            error = null;
            if (target == null)
            {
                error = "the component is null.";
                return false;
            }

            // Cinemachine stages expose their tunables as fields, including readonly struct fields.
            MemberInfo info = CucliReflection.FindMember(target.GetType(), member);
            if (info == null)
            {
                MemberInfo snake = CucliReflection.FindMember(target.GetType(), ToCamel(member));
                if (snake == null)
                {
                    error = "'" + member + "' is not a member of " + target.GetType().Name + ".";
                    return false;
                }

                info = snake;
            }

            if (!CucliReflection.TrySetMember(target, info.Name, value, out error))
            {
                FieldInfo field = info as FieldInfo;
                if (field == null)
                {
                    error = error ?? ("'" + member + "' is read-only.");
                    return false;
                }

                // Some Cinemachine tunables are exposed only through the underlying m_Xxx field.
                FieldInfo nested = target.GetType().GetField(
                    "m_" + info.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (nested == null)
                {
                    error = error ?? ("'" + member + "' is read-only.");
                    return false;
                }

                object converted = CucliValues.Convert(value, nested.FieldType, out string convertError);
                if (convertError != null)
                {
                    error = convertError;
                    return false;
                }

                nested.SetValue(target, converted);
                return true;
            }

            return true;
        }

        internal static string ToCamel(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            bool hasUnderscore = name.IndexOf('_') >= 0;
            if (!hasUnderscore) return name;

            var builder = new System.Text.StringBuilder(name.Length);
            bool upper = false;
            foreach (char character in name)
            {
                if (character == '_')
                {
                    upper = true;
                    continue;
                }

                builder.Append(upper ? char.ToUpperInvariant(character) : character);
                upper = false;
            }

            return builder.ToString();
        }

        private static CucliCommandResult SetNoise(CucliCommandContext context)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            GameObject host = CinemachineSupport.ResolveCameraHost(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            object cinemachineCamera = CinemachineSupport.FindCamera(host);
            if (cinemachineCamera == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, host.name + " has no CinemachineCamera component.");
            }

            object noise;
            bool added;
            string error;
            if (!CinemachineSupport.EnsureNoise(cinemachineCamera, out noise, out added, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
            }

            var changed = new JArray();
            var failed = new JArray();
            JObject properties = PropertyObject(context);
            if (properties != null)
            {
                foreach (JProperty property in properties.Properties())
                {
                    string propertyError;
                    if (CucliSerialized.TrySet(noise as UnityEngine.Object, property.Name, property.Value,
                            "cucli camera set_noise", out propertyError, out bool handled))
                    {
                        changed.Add(property.Name);
                    }
                    else if (handled)
                    {
                        failed.Add(new JObject { ["key"] = property.Name, ["reason"] = propertyError });
                    }
                    else if (SetComponentMember(noise, property.Name, property.Value, out propertyError))
                    {
                        changed.Add(property.Name);
                    }
                    else
                    {
                        failed.Add(new JObject { ["key"] = property.Name, ["reason"] = propertyError });
                    }
                }
            }

            if (noise is UnityEngine.Object) EditorUtility.SetDirty((UnityEngine.Object)noise);
            return CucliCommandResult.Ok(new
            {
                instanceID = host.GetInstanceID(),
                added,
                changed,
                failed
            }, added
                ? "Added the noise component to " + host.name + "."
                : "The noise component on " + host.name + " already existed and was updated.");
        }

        // ---------------------------------------------------------------- Tier 2: extensions

        private static CucliCommandResult AddExtension(CucliCommandContext context)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            JObject properties = PropertyObject(context) ?? new JObject();
            JToken typeToken = Property(properties, "extensionType", "extension_type");
            if (typeToken == null || string.IsNullOrWhiteSpace(typeToken.ToString()))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "'extensionType' property is required.",
                    new { hint = "Pass properties.extensionType, e.g. CinemachineDeoccluder." });
            }

            GameObject host = CinemachineSupport.ResolveCameraHost(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            object cinemachineCamera = CinemachineSupport.FindCamera(host);
            if (cinemachineCamera == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, host.name + " has no CinemachineCamera component.");
            }

            object extension;
            bool already;
            string error;
            if (!CinemachineSupport.TryAddExtension(
                    cinemachineCamera, typeToken.ToString().Trim(), out extension, out already, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TypeNotFound, error);
            }

            if (already)
            {
                return CucliCommandResult.Ok(
                    new { instanceID = host.GetInstanceID(), extensionType = extension.GetType().Name },
                    "The " + extension.GetType().Name + " extension was already attached.");
            }

            var changed = new JArray();
            var failed = new JArray();
            foreach (JProperty property in properties.Properties())
            {
                if (property.Name == "extensionType" || property.Name == "extension_type") continue;

                string propertyError;
                if (SetComponentMember(extension, property.Name, property.Value, out propertyError)
                    || CucliSerialized.TrySet(extension as UnityEngine.Object, property.Name, property.Value,
                        "cucli camera add_extension", out propertyError, out bool ignored))
                {
                    changed.Add(property.Name);
                }
                else
                {
                    failed.Add(new JObject { ["key"] = property.Name, ["reason"] = propertyError });
                }
            }

            if (extension is UnityEngine.Object) EditorUtility.SetDirty((UnityEngine.Object)extension);
            if (cinemachineCamera is UnityEngine.Object) EditorUtility.SetDirty((UnityEngine.Object)cinemachineCamera);

            return CucliCommandResult.Ok(new
            {
                instanceID = host.GetInstanceID(),
                extensionType = extension.GetType().Name,
                changed,
                failed
            }, "Attached " + extension.GetType().Name + " to " + host.name + ".");
        }

        private static CucliCommandResult RemoveExtension(CucliCommandContext context)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            JObject properties = PropertyObject(context) ?? new JObject();
            JToken typeToken = Property(properties, "extensionType", "extension_type");
            if (typeToken == null || string.IsNullOrWhiteSpace(typeToken.ToString()))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "'extensionType' property is required.");
            }

            GameObject host = CinemachineSupport.ResolveCameraHost(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            object cinemachineCamera = CinemachineSupport.FindCamera(host);
            if (cinemachineCamera == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, host.name + " has no CinemachineCamera component.");
            }

            int removed;
            string error;
            if (!CinemachineSupport.TryRemoveExtensions(
                    cinemachineCamera, typeToken.ToString().Trim(), out removed, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TypeNotFound, error);
            }

            return CucliCommandResult.Ok(new
            {
                instanceID = host.GetInstanceID(),
                extensionType = typeToken.ToString().Trim(),
                removed
            }, removed == 0
                ? "No extension of that type was attached."
                : "Removed " + removed + " extension(s) from " + host.name + ".");
        }

        // ---------------------------------------------------------------- Tier 2: blend / force

        private static CucliCommandResult SetBlend(CucliCommandContext context)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            GameObject host = CinemachineSupport.ResolveCameraHost(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            Component brain = CinemachineSupport.FindBrain(host);
            if (brain == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    host.name + " has no CinemachineBrain. Add one with 'cucli camera ensure_brain'.");
            }

            JObject properties = PropertyObject(context) ?? new JObject();
            var changed = new JArray();

            JToken styleToken = properties["style"];
            if (styleToken != null)
            {
                string error;
                if (!CinemachineSupport.SetDefaultBlendStyle(host, styleToken.ToString(), out error))
                {
                    return CucliCommandResult.Invalid("style", error);
                }

                changed.Add("style");
            }

            JToken durationToken = properties["duration"];
            if (durationToken != null)
            {
                float duration = durationToken.Value<float>();
                if (duration >= 0f)
                {
                    string error;
                    if (!CinemachineSupport.SetDefaultBlendDuration(host, duration, out error))
                    {
                        return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
                    }

                    changed.Add("duration");
                }
            }

            if (changed.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "properties", "Provide properties.style and/or properties.duration.");
            }

            return CucliCommandResult.Ok(new
            {
                instanceID = host.GetInstanceID(),
                changed
            }, "Updated the default Cinemachine blend on " + host.name + ".");
        }

        private static CucliCommandResult ForceCamera(CucliCommandContext context)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            GameObject host = CinemachineSupport.ResolveCameraHost(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            object cinemachineCamera = CinemachineSupport.FindCamera(host);
            if (cinemachineCamera == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, host.name + " has no CinemachineCamera component.");
            }

            string method;
            int overrideId;
            string error;
            if (!CinemachineSupport.TryForceCamera(cinemachineCamera, out overrideId, out method, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
            }

            return CucliCommandResult.Ok(new
            {
                instanceID = host.GetInstanceID(),
                overrideId,
                method
            }, method == "override"
                ? "Forced " + host.name + " live through a Cinemachine override."
                : "Raised the priority of " + host.name + " to force it live.");
        }

        private static CucliCommandResult ReleaseOverride(CucliCommandContext context)
        {
            CucliCommandResult missing = CinemachineSupport.RequireCinemachine("Cinemachine 相机能力");
            if (missing != null) return missing;

            GameObject host = CinemachineSupport.ResolveCameraHost(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            Component brain = CinemachineSupport.FindBrain(host);
            if (brain == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, host.name + " has no CinemachineBrain.");
            }

            int released;
            string error;
            if (!CinemachineSupport.TryReleaseOverride(brain, out released, out error))
            {
                return CucliCommandResult.Ok(
                    new { releasedOverrideId = (int?)null },
                    "No active Cinemachine override needed releasing.");
            }

            return CucliCommandResult.Ok(new { releasedOverrideId = released },
                "Released the Cinemachine override on " + host.name + ".");
        }
    }
}
