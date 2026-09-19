using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>animator_*</c> actions of the <c>animation</c> group: reading and driving an
    /// <see cref="Animator"/> at runtime.
    ///
    /// Reading a controller and writing parameters is useful in Edit Mode too, so the group's
    /// <see cref="CucliMode"/> is <c>Any</c> and only <c>animator_play</c> / <c>animator_crossfade</c>
    /// - which call into the running state machine - require Play Mode and return
    /// <c>WRONG_EDITOR_MODE</c> when it is missing.
    /// </summary>
    internal static class AnimationAnimatorCommand
    {
        internal static CucliCommandResult Execute(
            CucliCommandContext context, CucliArgs args, string action, string[] supported)
        {
            switch (action)
            {
                case "animator_play": return Play(context, args);
                case "animator_crossfade": return CrossFade(context, args);
                case "animator_get_info": return GetInfo(context, args);
                case "animator_get_parameter": return GetParameter(context, args);
                case "animator_set_parameter": return SetParameter(context, args);
                case "animator_set_speed": return SetSpeed(context, args);
                case "animator_set_enabled": return SetEnabled(context, args);
                default: return CucliCommandResult.Unsupported(action, supported);
            }
        }

        private static CucliCommandResult RequireAnimator(CucliArgs args, out Animator animator)
        {
            animator = null;
            string error;
            animator = AnimationCommand.RequireAnimator(args, out error);
            if (animator != null) return null;
            return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
        }

        private static CucliCommandResult RequirePlayMode(string action)
        {
            if (EditorApplication.isPlaying) return null;
            return CucliCommandResult.Fail(
                CucliErrors.WrongMode,
                "'" + action + "' drives the running state machine, so it requires Play Mode. "
                + "Start it with 'cucli editor play'.");
        }

        // ---------------------------------------------------------------- animator_get_info

        private static CucliCommandResult GetInfo(CucliCommandContext context, CucliArgs args)
        {
            Animator animator;
            CucliCommandResult failure = RequireAnimator(args, out animator);
            if (failure != null) return failure;

            int maxItems = Mathf.Clamp(args.GetInt("maxItems", args.GetInt("max_items", 64)), 1, 500);
            RuntimeAnimatorController controller = animator.runtimeAnimatorController;

            var parameters = new JArray();
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                parameters.Add(new JObject
                {
                    ["name"] = parameter.name,
                    ["type"] = parameter.type.ToString().ToLowerInvariant(),
                    ["defaultFloat"] = parameter.defaultFloat,
                    ["defaultInt"] = parameter.defaultInt,
                    ["defaultBool"] = parameter.defaultBool
                });
            }

            var layers = new JArray();
            for (int index = 0; index < animator.layerCount; index++)
            {
                var entry = new JObject
                {
                    ["index"] = index,
                    ["name"] = animator.GetLayerName(index),
                    ["weight"] = animator.GetLayerWeight(index)
                };

                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(index);
                entry["currentStateHash"] = info.fullPathHash;
                entry["currentStateNormalizedTime"] = info.normalizedTime;
                entry["currentStateLength"] = info.length;
                entry["isInTransition"] = animator.IsInTransition(index);
                layers.Add(entry);
            }

            var clips = new JArray();
            int emitted = 0;
            foreach (AnimationClip clip in Clips(controller))
            {
                if (emitted++ >= maxItems) break;
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                clips.Add(new JObject
                {
                    ["name"] = clip.name,
                    ["length"] = clip.length,
                    ["frameRate"] = clip.frameRate,
                    ["isLooping"] = settings.loopTime,
                    ["wrapMode"] = settings.loopTime ? "Loop" : "Default"
                });
            }

            return CucliCommandResult.Ok(new
            {
                gameObject = CucliObjectResolver.HierarchyPath(animator.gameObject, true),
                instanceID = animator.gameObject.GetInstanceID(),
                enabled = animator.enabled,
                speed = animator.speed,
                hasController = controller != null,
                controllerName = controller == null ? null : controller.name,
                applyRootMotion = animator.applyRootMotion,
                updateMode = animator.updateMode.ToString(),
                cullingMode = animator.cullingMode.ToString(),
                parameterCount = animator.parameters.Length,
                layerCount = animator.layerCount,
                parameters,
                layers,
                clips,
                clipCount = clips.Count
            });
        }

        private static IEnumerable<AnimationClip> Clips(RuntimeAnimatorController controller)
        {
            var animatorController = controller as AnimatorController;
            if (animatorController == null) yield break;

            foreach (AnimationClip clip in animatorController.animationClips)
            {
                if (clip != null) yield return clip;
            }
        }

        // ---------------------------------------------------------------- parameters

        private static CucliCommandResult GetParameter(CucliCommandContext context, CucliArgs args)
        {
            Animator animator;
            CucliCommandResult failure = RequireAnimator(args, out animator);
            if (failure != null) return failure;

            string name = args.GetString("parameterName") ?? args.GetString("parameter_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return CucliCommandResult.Missing("parameterName", "Example: --parameter-name Speed");
            }

            AnimatorControllerParameter parameter = animator.parameters
                .FirstOrDefault(candidate => string.Equals(candidate.name, name.Trim(), StringComparison.Ordinal));
            if (parameter == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "the Animator has no parameter named '" + name + "'. Available: "
                    + string.Join(", ", animator.parameters.Select(candidate => candidate.name)) + ".");
            }

            object value;
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float:
                    value = animator.GetFloat(parameter.name);
                    break;
                case AnimatorControllerParameterType.Int:
                    value = animator.GetInteger(parameter.name);
                    break;
                case AnimatorControllerParameterType.Bool:
                    value = animator.GetBool(parameter.name);
                    break;
                default:
                    value = null;
                    break;
            }

            return CucliCommandResult.Ok(new
            {
                name = parameter.name,
                type = parameter.type.ToString().ToLowerInvariant(),
                value
            });
        }

        private static CucliCommandResult SetParameter(CucliCommandContext context, CucliArgs args)
        {
            Animator animator;
            CucliCommandResult failure = RequireAnimator(args, out animator);
            if (failure != null) return failure;

            string name = args.GetString("parameterName") ?? args.GetString("parameter_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return CucliCommandResult.Missing("parameterName", "Example: --parameter-name Speed --value 2");
            }

            name = name.Trim();
            AnimatorControllerParameter parameter = animator.parameters
                .FirstOrDefault(candidate => string.Equals(candidate.name, name, StringComparison.Ordinal));
            if (parameter == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "the Animator has no parameter named '" + name + "'. Available: "
                    + string.Join(", ", animator.parameters.Select(candidate => candidate.name)) + ".");
            }

            AnimatorControllerParameterType type = parameter.type;
            string requested = args.GetString("parameterType") ?? args.GetString("parameter_type");
            if (!string.IsNullOrWhiteSpace(requested)
                && !string.Equals(requested.Trim(), type.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                context.Warn("parameterType '" + requested + "' disagrees with the Animator's own type '"
                    + type + "'; the Animator's type was used.");
            }

            switch (type)
            {
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(name, args.GetFloat("value", 0f));
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(name, args.GetInt("value", 0));
                    break;
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(name, args.GetBool("value", false));
                    break;
                case AnimatorControllerParameterType.Trigger:
                    animator.SetTrigger(name);
                    break;
                default:
                    return CucliCommandResult.Invalid(
                        "parameterType", "the parameter type " + type + " cannot be written.");
            }

            EditorUtility.SetDirty(animator);
            return CucliCommandResult.Ok(
                new { name, type = type.ToString().ToLowerInvariant() },
                "Set the Animator parameter " + name + ".");
        }

        // ---------------------------------------------------------------- play / crossfade

        private static CucliCommandResult Play(CucliCommandContext context, CucliArgs args)
        {
            Animator animator;
            CucliCommandResult mode = RequirePlayMode("animator_play");
            if (mode != null) return mode;

            CucliCommandResult failure = RequireAnimator(args, out animator);
            if (failure != null) return failure;

            string stateName = args.GetString("stateName") ?? args.GetString("state_name");
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return CucliCommandResult.Missing("stateName", "Example: --state-name Run");
            }

            int layer = args.GetInt("layer", -1);
            if (layer >= 0 && layer >= animator.layerCount)
            {
                return CucliCommandResult.Invalid(
                    "layer", layer + " is out of range; the Animator has " + animator.layerCount + " layer(s).");
            }

            if (layer < 0) animator.Play(stateName.Trim());
            else animator.Play(stateName.Trim(), layer);

            return CucliCommandResult.Ok(
                new { stateName = stateName.Trim(), layer },
                "Played the state " + stateName + " on " + animator.gameObject.name + ".");
        }

        private static CucliCommandResult CrossFade(CucliCommandContext context, CucliArgs args)
        {
            Animator animator;
            CucliCommandResult mode = RequirePlayMode("animator_crossfade");
            if (mode != null) return mode;

            CucliCommandResult failure = RequireAnimator(args, out animator);
            if (failure != null) return failure;

            string stateName = args.GetString("stateName") ?? args.GetString("state_name");
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return CucliCommandResult.Missing("stateName", "Example: --state-name Attack");
            }

            float duration = args.GetFloat("duration", 0.25f);
            int layer = args.GetInt("layer", -1);
            if (layer >= 0 && layer >= animator.layerCount)
            {
                return CucliCommandResult.Invalid(
                    "layer", layer + " is out of range; the Animator has " + animator.layerCount + " layer(s).");
            }

            if (layer < 0) animator.CrossFade(stateName.Trim(), duration);
            else animator.CrossFade(stateName.Trim(), duration, layer);

            return CucliCommandResult.Ok(
                new { stateName = stateName.Trim(), layer, duration },
                "Cross-faded to " + stateName + " over " + duration + "s.");
        }

        // ---------------------------------------------------------------- speed / enabled

        private static CucliCommandResult SetSpeed(CucliCommandContext context, CucliArgs args)
        {
            Animator animator;
            CucliCommandResult failure = RequireAnimator(args, out animator);
            if (failure != null) return failure;

            float speed = args.GetFloat("speed", 1f);
            Undo.RecordObject(animator, "cucli animation animator_set_speed");
            animator.speed = speed;
            EditorUtility.SetDirty(animator);

            return CucliCommandResult.Ok(
                new { gameObject = CucliObjectResolver.HierarchyPath(animator.gameObject, true), speed },
                "The Animator speed is now " + speed + ".");
        }

        private static CucliCommandResult SetEnabled(CucliCommandContext context, CucliArgs args)
        {
            Animator animator;
            CucliCommandResult failure = RequireAnimator(args, out animator);
            if (failure != null) return failure;

            bool enabled = args.GetBool("enabled", true);
            Undo.RecordObject(animator, "cucli animation animator_set_enabled");
            animator.enabled = enabled;
            EditorUtility.SetDirty(animator);

            return CucliCommandResult.Ok(new
            {
                gameObject = CucliObjectResolver.HierarchyPath(animator.gameObject, true),
                enabled
            }, "The Animator is now " + (enabled ? "enabled" : "disabled") + ".");
        }
    }
}
