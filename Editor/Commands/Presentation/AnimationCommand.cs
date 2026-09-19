using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli animation ...</c> - animation clips, AnimatorControllers and Animator runtime control.
    ///
    /// The 28 actions are dispatched by prefix, exactly like the MCP <c>manage_animation</c> tool:
    /// <c>animator_*</c> (7), then <c>controller_*</c> (12), then <c>clip_*</c> (8, including
    /// <c>clip_create_preset</c>). Clip and controller work is Edit Mode asset work, so the group's
    /// <see cref="RequiredMode"/> stays <c>Any</c>; the animator actions that genuinely need a running
    /// player check <c>EditorApplication.isPlaying</c> themselves and return <c>WRONG_EDITOR_MODE</c>.
    ///
    /// Only built-in Mecanim types are used - <c>UnityEditor.Animations</c>, <c>AnimationUtility</c>
    /// and <c>Animator</c>. No optional package is involved.
    /// </summary>
    internal sealed class AnimationCommand : CucliCommand
    {
        // Named distinctly from the override below: a const and a property cannot share a name.
        public const string SchemaJson = @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""clip_create"", ""clip_get_info"", ""clip_add_curve"", ""clip_set_curve"", ""clip_set_vector_curve"",
                 ""clip_assign"", ""clip_add_event"", ""clip_remove_event"", ""clip_create_preset"",
                 ""controller_create"", ""controller_add_state"", ""controller_add_transition"",
                 ""controller_add_parameter"", ""controller_get_info"", ""controller_assign"",
                 ""controller_add_layer"", ""controller_remove_layer"", ""controller_set_layer_weight"",
                 ""controller_create_blend_tree_1d"", ""controller_create_blend_tree_2d"",
                 ""controller_add_blend_tree_child"",
                 ""animator_get_info"", ""animator_get_parameter"", ""animator_play"", ""animator_crossfade"",
                 ""animator_set_parameter"", ""animator_set_speed"", ""animator_set_enabled""],
      ""default"": ""clip_create""
    },
    ""properties"": { ""type"": ""object"", ""description"": ""Parameter envelope; its keys are merged with the outer keys, and outer keys win."" },
    ""clipPath"": { ""type"": ""string"", ""description"": ""AnimationClip asset path; '.anim' is appended when missing."" },
    ""controllerPath"": { ""type"": ""string"", ""description"": ""AnimatorController asset path; '.controller' is appended when missing."" },
    ""name"": { ""type"": ""string"", ""description"": ""action=clip_create: clip name, defaulting to the file name."" },
    ""length"": { ""type"": ""number"", ""default"": 1, ""description"": ""action=clip_create: stop time."" },
    ""frameRate"": { ""type"": ""number"", ""default"": 60, ""description"": ""action=clip_create: clip frame rate."" },
    ""loop"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=clip_create / clip_create_preset: circular clip."" },
    ""propertyPath"": { ""type"": ""string"", ""description"": ""action=clip_add_curve / clip_set_curve: e.g. localPosition.x."" },
    ""property"": { ""type"": ""string"", ""enum"": [""localPosition"", ""localEulerAngles"", ""localScale""], ""description"": ""action=clip_set_vector_curve."" },
    ""relativePath"": { ""type"": ""string"", ""default"": """" },
    ""type"": { ""type"": ""string"", ""default"": ""Transform"", ""description"": ""action=clip_* curve: the bound component type."" },
    ""keys"": { ""type"": ""array"", ""items"": {}, ""description"": ""action=clip_*: [time, value] pairs or {time, value, inTangent, outTangent, inWeight, outWeight} objects."" },
    ""target"": { ""type"": ""string"", ""description"": ""action=clip_assign / controller_assign / animator_*: GameObject name, path or instance id."" },
    ""searchMethod"": { ""type"": ""string"", ""enum"": [""by_id_or_name_or_path"", ""by_id"", ""by_name"", ""by_path""] },
    ""time"": { ""type"": ""number"", ""description"": ""action=clip_add_event: event time; clip_remove_event: optional filter."" },
    ""functionName"": { ""type"": ""string"", ""description"": ""action=clip_add_event / clip_remove_event: the event callback name."" },
    ""stringParameter"": { ""type"": ""string"", ""default"": """" },
    ""floatParameter"": { ""type"": ""number"", ""default"": 0 },
    ""intParameter"": { ""type"": ""integer"", ""default"": 0 },
    ""eventIndex"": { ""type"": ""integer"", ""description"": ""action=clip_remove_event: index, preferred over functionName."" },
    ""preset"": { ""type"": ""string"", ""enum"": [""bounce"", ""rotate"", ""pulse"", ""fade"", ""shake"", ""hover"", ""spin"", ""sway"", ""bob"", ""wiggle"", ""blink"", ""slide_in"", ""elastic"", ""grow"", ""shrink""], ""description"": ""action=clip_create_preset."" },
    ""duration"": { ""type"": ""number"", ""default"": 1 },
    ""amplitude"": { ""type"": ""number"", ""default"": 1 },
    ""offset"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""action=clip_create_preset: [x,y,z] base offset."" },
    ""stateName"": { ""type"": ""string"" },
    ""layerIndex"": { ""type"": ""integer"", ""default"": 0 },
    ""layerName"": { ""type"": ""string"" },
    ""speed"": { ""type"": ""number"", ""default"": 1 },
    ""isDefault"": { ""type"": ""boolean"", ""default"": false },
    ""fromState"": { ""type"": ""string"", ""description"": ""action=controller_add_transition; AnyState / Any enable an AnyState transition."" },
    ""toState"": { ""type"": ""string"" },
    ""hasExitTime"": { ""type"": ""boolean"", ""default"": true },
    ""exitTime"": { ""type"": ""number"", ""default"": 0.75 },
    ""conditions"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""parameterName"": { ""type"": ""string"" },
    ""parameterType"": { ""type"": ""string"", ""enum"": [""float"", ""int"", ""integer"", ""bool"", ""boolean"", ""trigger""] },
    ""defaultValue"": { ""description"": ""action=controller_add_parameter: number, integer or bool."" },
    ""weight"": { ""type"": ""number"", ""default"": 1 },
    ""blendingMode"": { ""type"": ""string"", ""enum"": [""override"", ""additive""] },
    ""blendParameter"": { ""type"": ""string"" },
    ""blendParameterX"": { ""type"": ""string"" },
    ""blendParameterY"": { ""type"": ""string"" },
    ""blendType"": { ""type"": ""string"" },
    ""threshold"": { ""type"": ""number"" },
    ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""layer"": { ""type"": ""integer"", ""default"": -1, ""description"": ""action=animator_play / animator_crossfade: Animator layer index; -1 means the current layer."" },
    ""value"": { ""description"": ""action=animator_set_parameter."" },
    ""enabled"": { ""type"": ""boolean"", ""default"": true },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 },
    ""pageSize"": { ""type"": ""integer"", ""default"": 100 },
    ""cursor"": { ""type"": ""string"" }
  }
}";

        public override string Name => "animation";

        public override string Summary =>
            "Animation clips (curves, events, presets), AnimatorControllers (states, transitions, parameters, layers, blend trees) and Animator runtime control.";

        public override CucliActionInfo[] Actions => Declare(
            "clip_create", "Create an AnimationClip asset.",
            "clip_get_info", "Read the clip settings, curve bindings and events with bounded keyframe counts.",
            "clip_add_curve", "Append keyframes to a float curve.",
            "clip_set_curve", "Replace a float curve.",
            "clip_set_vector_curve", "Write the three component curves of a vector property.",
            "clip_assign", "Assign the clip to an Animator.",
            "clip_add_event", "Append an AnimationEvent.",
            "clip_remove_event", "Remove AnimationEvents by index or by function name.",
            "clip_create_preset", "Generate a clip from one of the fifteen procedural presets.",
            "controller_create", "Create an AnimatorController asset.",
            "controller_add_state", "Add a state to a layer, optionally with a motion clip.",
            "controller_add_transition", "Add a transition, including from AnyState.",
            "controller_add_parameter", "Add a controller parameter.",
            "controller_get_info", "Read layers, states, transitions and parameters.",
            "controller_assign", "Assign the controller to an Animator.",
            "controller_add_layer", "Append a layer with a weight and blending mode.",
            "controller_remove_layer", "Remove a layer by index or name.",
            "controller_set_layer_weight", "Set one layer weight.",
            "controller_create_blend_tree_1d", "Create a 1D blend tree state.",
            "controller_create_blend_tree_2d", "Create a 2D blend tree state.",
            "controller_add_blend_tree_child", "Add a motion child to a blend tree.",
            "animator_get_info", "Read the Animator state, parameters, layers and clips.",
            "animator_get_parameter", "Read one Animator parameter.",
            "animator_play", "Play a state by name. Requires Play Mode.",
            "animator_crossfade", "Cross-fade to a state. Requires Play Mode.",
            "animator_set_parameter", "Write one Animator parameter.",
            "animator_set_speed", "Set the Animator speed.",
            "animator_set_enabled", "Enable or disable the Animator component.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "project.assets.write", "project.assets.read", "scene.animator.write"
        };

        public override string ParametersSchemaJson => SchemaJson;

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            CucliArgs args = MergeEnvelope(context);
            string action = args.GetString("action");
            if (string.IsNullOrWhiteSpace(action))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.MissingParameter,
                    "Required parameter 'action' is missing. One of: " + string.Join(", ", ActionNames()) + ".");
            }

            action = action.Trim().ToLowerInvariant();

            if (action.StartsWith("animator_", StringComparison.Ordinal))
            {
                return AnimationAnimatorCommand.Execute(context, args, action, ActionNames());
            }

            if (action.StartsWith("controller_", StringComparison.Ordinal))
            {
                return AnimationControllerCommand.Execute(context, args, action, ActionNames());
            }

            if (action.StartsWith("clip_", StringComparison.Ordinal))
            {
                return AnimationClipCommand.Execute(context, args, action, ActionNames());
            }

            return CucliCommandResult.Unsupported(action, ActionNames());
        }

        /// <summary>
        /// manage_animation accepts both an outer key set and a <c>properties</c> envelope whose keys
        /// are merged in, with the outer keys winning. The merge happens once here so every family
        /// reads a single flat view.
        /// </summary>
        internal static CucliArgs MergeEnvelope(CucliCommandContext context)
        {
            JObject envelope = context.Input.GetObject("properties");
            if (envelope == null || envelope.Count == 0) return context.Input;

            var merged = new JObject();
            foreach (JProperty property in envelope.Properties()) merged[property.Name] = property.Value;
            foreach (JProperty property in context.Args.Properties()) merged[property.Name] = property.Value;
            return new CucliArgs(merged);
        }

        // ---------------------------------------------------------------- shared helpers

        internal static string NormalizeClipPath(string raw, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "a clip path is required, e.g. Assets/Animations/Bounce.anim.";
                return null;
            }

            string value = raw.Trim().Replace('\\', '/');
            if (!value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) value = "Assets/" + value.TrimStart('/');
            if (!value.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) value += ".anim";

            string normalized = CucliAssets.NormalizeAssetPath(value);
            if (normalized == null) error = "the path escapes the project: " + raw;
            return normalized;
        }

        internal static string NormalizeControllerPath(string raw, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "a controller path is required, e.g. Assets/Animations/Player.controller.";
                return null;
            }

            string value = raw.Trim().Replace('\\', '/');
            if (!value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) value = "Assets/" + value.TrimStart('/');
            if (!value.EndsWith(".controller", StringComparison.OrdinalIgnoreCase)) value += ".controller";

            string normalized = CucliAssets.NormalizeAssetPath(value);
            if (normalized == null) error = "the path escapes the project: " + raw;
            return normalized;
        }

        internal static bool EnsureParentFolder(string assetPath)
        {
            string parent = CucliAssets.ParentFolder(assetPath);
            return parent == null || CucliAssets.EnsureFolder(parent);
        }

        internal static AnimationClip LoadClip(string raw, out string error)
        {
            error = null;
            string path = NormalizeClipPath(raw, out error);
            if (path == null) return null;

            AnimationClip clip = CucliAssets.Load<AnimationClip>(path);
            if (clip == null) error = "no AnimationClip asset is at " + path + ".";
            return clip;
        }

        internal static GameObject RequireTarget(CucliArgs args, out string error)
        {
            if (!args.Has("target") && !args.Has("path") && !args.Has("name")
                && !args.Has("instanceId") && !args.Has("instance_id") && !args.Has("id"))
            {
                error = "a 'target' (GameObject name, hierarchy path or instance id) is required.";
                return null;
            }

            return CucliObjectResolver.ResolveGameObject(args, out error);
        }

        internal static Animator RequireAnimator(CucliArgs args, out string error)
        {
            GameObject gameObject = RequireTarget(args, out error);
            if (gameObject == null) return null;

            Animator animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                error = gameObject.name + " has no Animator component.";
                return null;
            }

            return animator;
        }

        /// <summary>Converts a JToken into the component type named by a clip curve binding.</summary>
        internal static Type ResolveComponentType(string typeName, out string error)
        {
            error = null;
            string requested = string.IsNullOrWhiteSpace(typeName) ? "Transform" : typeName.Trim();
            Type type = CucliReflection.FindType(requested, out error);
            if (type == null)
            {
                error = "the component type '" + requested + "' was not found in any loaded assembly.";
                return null;
            }

            if (!typeof(Component).IsAssignableFrom(type))
            {
                error = "'" + requested + "' is not a Component type.";
                return null;
            }

            return type;
        }

        internal static bool IsPlaying(CucliCommandContext context)
        {
            if (EditorApplication.isPlaying) return true;
            return false;
        }
    }
}
