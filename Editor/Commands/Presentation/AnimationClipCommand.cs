using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>clip_*</c> actions of the <c>animation</c> group: AnimationClip creation, curve and
    /// event editing, and the procedural preset generator.
    ///
    /// Curves are written with <c>AnimationUtility.SetEditorCurve</c> and read with
    /// <c>AnimationUtility.GetCurveBindings</c> + <c>GetEditorCurve</c>. A clip can hold thousands of
    /// keyframes, so every read is bounded by <c>maxItems</c> and reports the true key count.
    /// </summary>
    internal static class AnimationClipCommand
    {
        internal static CucliCommandResult Execute(
            CucliCommandContext context, CucliArgs args, string action, string[] supported)
        {
            switch (action)
            {
                case "clip_create": return Create(context, args);
                case "clip_get_info": return GetInfo(context, args);
                case "clip_add_curve": return AddCurve(context, args);
                case "clip_set_curve": return SetCurve(context, args);
                case "clip_set_vector_curve": return SetVectorCurve(context, args);
                case "clip_assign": return Assign(context, args);
                case "clip_add_event": return AddEvent(context, args);
                case "clip_remove_event": return RemoveEvent(context, args);
                case "clip_create_preset": return AnimationPresetCommand.CreatePreset(context, args);
                default: return CucliCommandResult.Unsupported(action, supported);
            }
        }

        // ---------------------------------------------------------------- clip_create

        private static CucliCommandResult Create(CucliCommandContext context, CucliArgs args)
        {
            string error;
            string path = AnimationCommand.NormalizeClipPath(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (path == null) return CucliCommandResult.Invalid("clipPath", error);

            if (CucliAssets.Exists(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    path + " already exists. Delete it, or edit it with 'cucli animation clip_set_curve'.",
                    new { path });
            }

            if (!AnimationCommand.EnsureParentFolder(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The folder for " + path + " could not be created.");
            }

            string name = args.GetString("name");
            if (string.IsNullOrWhiteSpace(name)) name = CucliAssets.FileNameWithoutExtension(path);

            float length = args.GetFloat("length", 1f);
            float frameRate = args.GetFloat("frameRate", args.GetFloat("frame_rate", 60f));
            bool loop = args.GetBool("loop", false);

            var clip = new AnimationClip { name = name, frameRate = frameRate };

            try
            {
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.stopTime = length;
                settings.loopTime = loop;
                AnimationUtility.SetAnimationClipSettings(clip, settings);

                if (loop)
                {
                    SerializedObject serialized = new SerializedObject(clip);
                    SerializedProperty wrap = serialized.FindProperty("m_WrapMode");
                    if (wrap != null && wrap.propertyType == SerializedPropertyType.Enum)
                    {
                        wrap.intValue = (int)WrapMode.Loop;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The clip settings could not be written: " + exception.GetBaseException().Message);
            }

            AssetDatabase.CreateAsset(clip, path);
            CucliAssets.SaveAssets();
            CucliAssets.Refresh();

            return CucliCommandResult.Ok(new
            {
                path,
                name,
                length,
                frameRate,
                isLooping = loop
            }, "Created the animation clip " + path + ".");
        }

        // ---------------------------------------------------------------- clip_get_info

        private static CucliCommandResult GetInfo(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimationClip clip = AnimationCommand.LoadClip(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (clip == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            int maxKeyframes = Mathf.Clamp(args.GetInt("maxItems", args.GetInt("max_items", 32)), 1, 500);

            var curves = new JArray();
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            foreach (EditorCurveBinding binding in bindings)
            {
                context.ThrowIfCancelled();

                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                var entry = new JObject
                {
                    ["path"] = binding.path,
                    ["propertyName"] = binding.propertyName,
                    ["type"] = binding.type == null ? null : binding.type.Name,
                    ["keyCount"] = curve == null ? 0 : curve.length
                };

                if (curve != null && curve.length > 0)
                {
                    var keys = new JArray();
                    int limit = Math.Min(curve.length, maxKeyframes);
                    for (int index = 0; index < limit; index++)
                    {
                        Keyframe key = curve.keys[index];
                        keys.Add(new JObject
                        {
                            ["time"] = Math.Round(key.time, 6),
                            ["value"] = Math.Round(key.value, 6),
                            ["inTangent"] = Math.Round(key.inTangent, 6),
                            ["outTangent"] = Math.Round(key.outTangent, 6)
                        });
                    }

                    if (curve.length > limit) entry["keysTruncated"] = curve.length - limit;
                    entry["keys"] = keys;
                }

                curves.Add(entry);
            }

            var events = new JArray();
            AnimationEvent[] clipEvents = AnimationUtility.GetAnimationEvents(clip);
            foreach (AnimationEvent clipEvent in clipEvents)
            {
                events.Add(new JObject
                {
                    ["time"] = clipEvent.time,
                    ["functionName"] = clipEvent.functionName,
                    ["stringParameter"] = clipEvent.stringParameter,
                    ["floatParameter"] = clipEvent.floatParameter,
                    ["intParameter"] = clipEvent.intParameter
                });
            }

            object wrapMode = null;
            var serializedClip = new SerializedObject(clip);
            SerializedProperty wrapProperty = serializedClip.FindProperty("m_WrapMode");
            if (wrapProperty != null && wrapProperty.propertyType == SerializedPropertyType.Enum)
            {
                wrapMode = wrapProperty.enumNames[wrapProperty.enumValueIndex];
            }

            return CucliCommandResult.Ok(new
            {
                path = CucliAssets.GetAssetPath(clip),
                name = clip.name,
                length = clip.length,
                frameRate = clip.frameRate,
                isLooping = settings.loopTime,
                wrapMode,
                curveCount = bindings.Length,
                curves,
                eventCount = clipEvents.Length,
                events
            });
        }

        // ---------------------------------------------------------------- curves

        private static CucliCommandResult AddCurve(CucliCommandContext context, CucliArgs args)
        {
            return WriteFloatCurve(context, args, true);
        }

        private static CucliCommandResult SetCurve(CucliCommandContext context, CucliArgs args)
        {
            return WriteFloatCurve(context, args, false);
        }

        private static CucliCommandResult WriteFloatCurve(
            CucliCommandContext context, CucliArgs args, bool append)
        {
            string error;
            AnimationClip clip = AnimationCommand.LoadClip(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (clip == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string propertyPath = args.GetString("propertyPath") ?? args.GetString("property_path");
            if (string.IsNullOrWhiteSpace(propertyPath))
            {
                return CucliCommandResult.Missing("propertyPath", "Example: --property-path localPosition.x");
            }

            Type componentType = AnimationCommand.ResolveComponentType(args.GetString("type"), out error);
            if (componentType == null) return CucliCommandResult.Fail(CucliErrors.TypeNotFound, error);

            JArray keys = args.GetArray("keys");
            if (keys == null || keys.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "keys", "Provide [[time, value], ...] or [{time, value, inTangent, outTangent}, ...].");
            }

            var binding = new EditorCurveBinding
            {
                path = args.GetString("relativePath") ?? args.GetString("relative_path") ?? string.Empty,
                type = componentType,
                propertyName = propertyPath.Trim()
            };

            AnimationCurve curve = append ? AnimationUtility.GetEditorCurve(clip, binding) : null;
            if (curve == null) curve = new AnimationCurve();

            var transform = curve.keys.ToList();
            for (int index = 0; index < keys.Count; index++)
            {
                context.ThrowIfCancelled();

                Keyframe key;
                if (!TryReadKeyframe(keys[index], out key, out string keyError))
                {
                    return CucliCommandResult.Invalid("keys", "entry " + index + ": " + keyError);
                }

                transform.Add(key);
            }

            curve.keys = transform.ToArray();
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            EditorUtility.SetDirty(clip);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                clipPath = CucliAssets.GetAssetPath(clip),
                propertyPath = binding.propertyName,
                type = componentType.Name,
                relativePath = binding.path,
                keyframeCount = curve.length
            }, (append ? "Appended to " : "Replaced ") + binding.propertyName + " on " + clip.name + ".");
        }

        internal static bool TryReadKeyframe(JToken token, out Keyframe key, out string error)
        {
            key = new Keyframe();
            error = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                error = "the keyframe is empty.";
                return false;
            }

            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                float time = 0f;
                float value = 0f;
                if (array.Count > 0 && !TryFloat(array[0], out time))
                {
                    error = "the time is not a number.";
                    return false;
                }

                if (array.Count > 1 && !TryFloat(array[1], out value))
                {
                    error = "the value is not a number.";
                    return false;
                }

                key = new Keyframe(time, value);
                // Keyframe exposes these as properties, so they cannot be passed by out. Read into a
                // local first and assign only on a successful parse.
                float scratch;
                if (array.Count > 2) { TryFloat(array[2], out scratch); key.inTangent = scratch; }
                if (array.Count > 3) { TryFloat(array[3], out scratch); key.outTangent = scratch; }
                if (array.Count > 4) { TryFloat(array[4], out scratch); key.inWeight = scratch; }
                if (array.Count > 5) { TryFloat(array[5], out scratch); key.outWeight = scratch; }
                return true;
            }

            if (token.Type == JTokenType.Object)
            {
                var element = (JObject)token;
                float time = element["time"] == null ? 0f : element.Value<float>("time");
                float value = element["value"] == null ? 0f : element.Value<float>("value");

                if (element["value"] != null && element["value"].Type == JTokenType.Array)
                {
                    error = "a float curve keyframe 'value' must be a number; use clip_set_vector_curve instead.";
                    return false;
                }

                key = new Keyframe(time, value);
                if (element["inTangent"] != null) key.inTangent = element.Value<float>("inTangent");
                if (element["outTangent"] != null) key.outTangent = element.Value<float>("outTangent");
                if (element["inWeight"] != null) key.inWeight = element.Value<float>("inWeight");
                if (element["outWeight"] != null) key.outWeight = element.Value<float>("outWeight");
                return true;
            }

            error = "a keyframe must be [time, value] or an object with time and value.";
            return false;
        }

        private static bool TryFloat(JToken token, out float value)
        {
            value = 0f;
            if (token == null || token.Type == JTokenType.Null) return false;
            try
            {
                value = token.Value<float>();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- clip_set_vector_curve

        private static readonly Dictionary<string, string[]> VectorAxes = new Dictionary<string, string[]>
        {
            { "localposition", new[] { "localPosition.x", "localPosition.y", "localPosition.z" } },
            { "localposition.x", new[] { "localPosition.x" } },
            { "localeulerangles", new[] { "localEulerAnglesRaw.x", "localEulerAnglesRaw.y", "localEulerAnglesRaw.z" } },
            { "localrotation", new[] { "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w" } },
            { "localscale", new[] { "localScale.x", "localScale.y", "localScale.z" } }
        };

        private static CucliCommandResult SetVectorCurve(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimationClip clip = AnimationCommand.LoadClip(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (clip == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string property = args.GetString("property") ?? args.GetString("propertyPath")
                ?? args.GetString("property_path");
            if (string.IsNullOrWhiteSpace(property))
            {
                return CucliCommandResult.Missing(
                    "property", "One of: localPosition, localEulerAngles, localScale.");
            }

            string key = UiCommand.NormalizeStyleKey(property);
            string[] axes;
            if (!VectorAxes.TryGetValue(key, out axes))
            {
                return CucliCommandResult.Invalid(
                    "property", "'" + property + "' is not one of: localPosition, localEulerAngles, localScale.");
            }

            Type componentType = AnimationCommand.ResolveComponentType(args.GetString("type"), out error);
            if (componentType == null) return CucliCommandResult.Fail(CucliErrors.TypeNotFound, error);

            JArray keys = args.GetArray("keys");
            if (keys == null || keys.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "keys", "Provide [{time, value: [x, y, z]}, ...].");
            }

            string relativePath = args.GetString("relativePath") ?? args.GetString("relative_path")
                ?? string.Empty;

            var perAxis = new List<Keyframe>[axes.Length];
            for (int index = 0; index < axes.Length; index++) perAxis[index] = new List<Keyframe>();

            for (int index = 0; index < keys.Count; index++)
            {
                context.ThrowIfCancelled();

                JToken token = keys[index];
                if (token == null || token.Type != JTokenType.Object)
                {
                    return CucliCommandResult.Invalid("keys", "entry " + index + " must be an object.");
                }

                var element = (JObject)token;
                float time = element["time"] == null ? 0f : element.Value<float>("time");
                JToken valueToken = element["value"];
                if (valueToken == null || valueToken.Type != JTokenType.Array || ((JArray)valueToken).Count < 3)
                {
                    return CucliCommandResult.Invalid(
                        "keys", "entry " + index + " needs a 'value' array with at least 3 numbers.");
                }

                var value = (JArray)valueToken;
                for (int axis = 0; axis < axes.Length; axis++)
                {
                    // A 4-component rotation binding repeats the w value; only x/y/z are required.
                    float component = axis < value.Count ? value[axis].Value<float>() : 0f;
                    perAxis[axis].Add(new Keyframe(time, component));
                }
            }

            var written = new JArray();
            for (int axis = 0; axis < axes.Length; axis++)
            {
                var binding = new EditorCurveBinding
                {
                    path = relativePath,
                    type = componentType,
                    propertyName = axes[axis]
                };

                var curve = new AnimationCurve(perAxis[axis].ToArray());
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                written.Add(axes[axis]);
            }

            EditorUtility.SetDirty(clip);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                clipPath = CucliAssets.GetAssetPath(clip),
                property,
                type = componentType.Name,
                relativePath,
                curves = written,
                keyframeCount = keys.Count
            }, "Wrote " + written.Count + " curve(s) for " + property + " on " + clip.name + ".");
        }

        // ---------------------------------------------------------------- clip_assign

        private static CucliCommandResult Assign(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimationClip clip = AnimationCommand.LoadClip(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (clip == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            GameObject gameObject = AnimationCommand.RequireTarget(args, out error);
            if (gameObject == null) return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);

            Animator animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    gameObject.name + " has no Animator component, so a clip cannot be assigned to it.");
            }

            string clipAssetPath = CucliAssets.GetAssetPath(clip);
            RuntimeAnimatorController existing = animator.runtimeAnimatorController;
            if (existing != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    gameObject.name + " already runs the controller '" + existing.name
                    + "'. A clip cannot be assigned while a controller is in place; use "
                    + "'cucli animation controller_assign' instead.",
                    new { controller = existing.name, controllerPath = CucliAssets.GetAssetPath(existing) });
            }

            // An AnimationClip derives from Motion, not from RuntimeAnimatorController, so
            // Animator.runtimeAnimatorController cannot hold one. The Inspector appears to allow it
            // because it silently authors a controller for you; doing that here would write an asset
            // the caller never asked for, so we refuse and point at the honest alternatives instead.
            return CucliCommandResult.Fail(
                CucliErrors.InvalidParameter,
                gameObject.name + " has an Animator but no controller. A bare AnimationClip cannot be "
                + "assigned to Animator.runtimeAnimatorController - create an AnimatorController and "
                + "assign it with 'cucli animation controller_assign', or drive the clip from Timeline "
                + "or an Animation component instead.",
                new { clip = clip.name, clipPath = clipAssetPath });
        }

        // ---------------------------------------------------------------- events

        private static CucliCommandResult AddEvent(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimationClip clip = AnimationCommand.LoadClip(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (clip == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string functionName = args.GetString("functionName") ?? args.GetString("function_name");
            if (string.IsNullOrWhiteSpace(functionName))
            {
                return CucliCommandResult.Missing("functionName", "Example: --function-name OnFootstep");
            }

            var clipEvent = new AnimationEvent
            {
                time = args.GetFloat("time", 0f),
                functionName = functionName.Trim(),
                stringParameter = args.GetString("stringParameter") ?? args.GetString("string_parameter")
                    ?? string.Empty,
                floatParameter = args.GetFloat("floatParameter", args.GetFloat("float_parameter", 0f)),
                intParameter = args.GetInt("intParameter", args.GetInt("int_parameter", 0))
            };

            List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(clip).ToList();
            events.Add(clipEvent);
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            EditorUtility.SetDirty(clip);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                clipPath = CucliAssets.GetAssetPath(clip),
                time = clipEvent.time,
                functionName = clipEvent.functionName,
                stringParameter = clipEvent.stringParameter,
                floatParameter = clipEvent.floatParameter,
                intParameter = clipEvent.intParameter,
                eventCount = events.Count
            }, "Added the AnimationEvent " + clipEvent.functionName + " to " + clip.name + ".");
        }

        private static CucliCommandResult RemoveEvent(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimationClip clip = AnimationCommand.LoadClip(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (clip == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            List<AnimationEvent> events = AnimationUtility.GetAnimationEvents(clip).ToList();
            if (events.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, clip.name + " has no AnimationEvents to remove.");
            }

            var remove = new List<AnimationEvent>();
            if (args.Has("eventIndex") || args.Has("event_index"))
            {
                int index = args.GetInt("eventIndex", args.GetInt("event_index", -1));
                if (index < 0 || index >= events.Count)
                {
                    return CucliCommandResult.Invalid(
                        "eventIndex", index + " is out of range; the clip has " + events.Count + " event(s).");
                }

                remove.Add(events[index]);
            }
            else
            {
                string functionName = args.GetString("functionName") ?? args.GetString("function_name");
                if (string.IsNullOrWhiteSpace(functionName))
                {
                    return CucliCommandResult.Missing(
                        "functionName", "Provide 'eventIndex' or 'functionName' to identify the event.");
                }

                bool hasTime = args.Has("time");
                float time = args.GetFloat("time", 0f);

                foreach (AnimationEvent candidate in events)
                {
                    if (!string.Equals(candidate.functionName, functionName.Trim(), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (hasTime && Mathf.Abs(candidate.time - time) > 0.0001f) continue;
                    remove.Add(candidate);
                }
            }

            if (remove.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, "No AnimationEvent matched the request.");
            }

            events.RemoveAll(candidate => remove.Contains(candidate));
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            EditorUtility.SetDirty(clip);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                clipPath = CucliAssets.GetAssetPath(clip),
                removedCount = remove.Count,
                remainingCount = events.Count
            }, "Removed " + remove.Count + " AnimationEvent(s) from " + clip.name + ".");
        }

        // ---------------------------------------------------------------- shared write path

        internal static void WriteCurves(AnimationClip clip, string relativePath, Type componentType,
            Dictionary<string, AnimationCurve> curves)
        {
            foreach (KeyValuePair<string, AnimationCurve> pair in curves)
            {
                var binding = new EditorCurveBinding
                {
                    path = relativePath,
                    type = componentType,
                    propertyName = pair.Key
                };

                AnimationUtility.SetEditorCurve(clip, binding, pair.Value);
            }

            EditorUtility.SetDirty(clip);
        }
    }
}
