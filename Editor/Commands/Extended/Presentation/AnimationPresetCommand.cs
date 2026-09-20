using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli animation clip_create_preset ...</c> - the procedural clip generator.
    ///
    /// Each preset writes a small set of <c>Transform</c> curves sampled from a closed-form function
    /// over <c>duration</c>. The curve keys use auto tangents, which is what makes them behave like
    /// hand-authored ease-in/ease-out curves rather than linear ramps; a couple of presets are
    /// genuinely piecewise and are built from explicit keyframe tables.
    /// </summary>
    internal static class AnimationPresetCommand
    {
        // Internal, not private: Sample() below is internal and takes this type as a parameter, and a
        // member cannot be more accessible than its parameter type.
        internal delegate float WaveFunction(float t, float a);

        private const string TransformTypeName = "Transform";

        private static readonly Dictionary<string, string> Presets = new Dictionary<string, string>
        {
            { "bounce", "localPosition" },
            { "rotate", "localEulerAngles" },
            { "pulse", "localScale" },
            { "fade", "localScale" },
            { "shake", "localPosition" },
            { "hover", "localPosition" },
            { "spin", "localEulerAngles" },
            { "sway", "localEulerAngles" },
            { "bob", "localPosition" },
            { "wiggle", "localEulerAngles" },
            { "blink", "localScale" },
            { "slide_in", "localPosition" },
            { "elastic", "localScale" },
            { "grow", "localScale" },
            { "shrink", "localScale" }
        };

        /// <summary>The curve bindings each preset owns, used by clip_get_info-style reporting.</summary>
        private static string[] CurvesOf(string preset)
        {
            switch (preset)
            {
                case "bounce": return new[] { "localPosition.y" };
                case "rotate":
                case "spin":
                case "sway":
                case "wiggle":
                    return new[] { "localEulerAnglesRaw.x", "localEulerAnglesRaw.y", "localEulerAnglesRaw.z" };
                case "pulse":
                case "fade":
                case "elastic": return new[] { "localScale.x", "localScale.y", "localScale.z" };
                case "shake": return new[] { "localPosition.x", "localPosition.y", "localPosition.z" };
                case "hover": return new[] { "localPosition.y" };
                case "bob": return new[] { "localPosition.x", "localPosition.y" };
                case "blink": return new[] { "localScale.x", "localScale.y" };
                case "slide_in": return new[] { "localPosition.x" };
                case "grow":
                case "shrink": return new[] { "localScale.x", "localScale.y", "localScale.z" };
                default: return new[] { "localPosition.y" };
            }
        }

        internal static CucliCommandResult CreatePreset(CucliCommandContext context, CucliArgs args)
        {
            string error;
            string path = AnimationCommand.NormalizeClipPath(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (path == null) return CucliCommandResult.Invalid("clipPath", error);

            string presetName = args.GetString("preset");
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return CucliCommandResult.Missing(
                    "preset", "One of: " + string.Join(", ", Presets.Keys) + ".");
            }

            presetName = presetName.Trim().ToLowerInvariant();
            if (!Presets.ContainsKey(presetName))
            {
                return CucliCommandResult.Invalid(
                    "preset", "'" + presetName + "' is not one of: " + string.Join(", ", Presets.Keys) + ".");
            }

            if (CucliAssets.Exists(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists, path + " already exists. Delete it first, or pick another path.",
                    new { path });
            }

            if (!AnimationCommand.EnsureParentFolder(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The folder for " + path + " could not be created.");
            }

            float duration = args.GetFloat("duration", 1f);
            if (duration <= 0f) duration = 1f;
            float amplitude = args.GetFloat("amplitude", 1f);
            bool loop = args.GetBool("loop", true);

            // The base offset comes from an explicit offset, then from the target's localPosition.
            Vector3 offset = Vector3.zero;
            if (args.Has("offset"))
            {
                offset = args.GetVector3("offset", Vector3.zero);
            }
            else
            {
                string targetError;
                GameObject target = AnimationCommand.RequireTarget(args, out targetError);
                if (target != null) offset = target.transform.localPosition;
            }

            Type componentType = AnimationCommand.ResolveComponentType(TransformTypeName, out error);
            if (componentType == null) return CucliCommandResult.Fail(CucliErrors.TypeNotFound, error);

            var clip = new AnimationClip { name = CucliAssets.FileNameWithoutExtension(path), frameRate = 60f };

            try
            {
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.stopTime = duration;
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

            var curves = new Dictionary<string, AnimationCurve>();
            BuildCurves(presetName, duration, amplitude, offset, curves);

            foreach (KeyValuePair<string, AnimationCurve> pair in curves)
            {
                var binding = new EditorCurveBinding
                {
                    path = string.Empty,
                    type = componentType,
                    propertyName = pair.Key
                };

                AnimationUtility.SetEditorCurve(clip, binding, pair.Value);
            }

            AssetDatabase.CreateAsset(clip, path);
            EditorUtility.SetDirty(clip);
            CucliAssets.SaveAssets();
            CucliAssets.Refresh();

            return CucliCommandResult.Ok(new
            {
                path,
                name = clip.name,
                preset = presetName,
                duration,
                amplitude,
                isLooping = loop,
                offset = new { x = offset.x, y = offset.y, z = offset.z },
                curveCount = curves.Count,
                curves = CurvesOf(presetName)
            }, "Created the '" + presetName + "' preset clip at " + path + ".");
        }

        // ---------------------------------------------------------------- curve construction

        private static void BuildCurves(
            string preset, float duration, float amplitude, Vector3 offset,
            Dictionary<string, AnimationCurve> curves)
        {
            switch (preset)
            {
                case "bounce":
                    // The bounce starts and ends at the object's own height, so the offset is the baseline.
                    curves["localPosition.y"] = Sample(duration, 48, (t, a) => offset.y + Bounce(t, a));
                    break;

                case "rotate":
                    curves["localEulerAnglesRaw.x"] = Constant(duration, 0f);
                    curves["localEulerAnglesRaw.y"] = Sample(duration, 48, (t, a) => 360f * a * t);
                    curves["localEulerAnglesRaw.z"] = Constant(duration, 0f);
                    break;

                case "spin":
                    curves["localEulerAnglesRaw.x"] = Constant(duration, 0f);
                    curves["localEulerAnglesRaw.y"] = Constant(duration, 0f);
                    curves["localEulerAnglesRaw.z"] = Sample(duration, 48, (t, a) => 720f * a * t);
                    break;

                case "pulse":
                {
                    AnimationCurve scale = Sample(duration, 48, (t, a) => 1f + Pulse(t, a));
                    curves["localScale.x"] = scale;
                    curves["localScale.y"] = scale;
                    curves["localScale.z"] = scale;
                    break;
                }

                case "fade":
                {
                    var fade = new AnimationCurve(
                        new Keyframe(0f, 1f),
                        new Keyframe(Mathf.Min(0.35f, duration * 0.35f), 0f),
                        new Keyframe(Mathf.Min(0.65f, duration * 0.65f), 0f),
                        new Keyframe(duration, 1f));
                    curves["localScale.x"] = fade;
                    curves["localScale.y"] = fade;
                    curves["localScale.z"] = fade;
                    break;
                }

                case "shake":
                    curves["localPosition.x"] = Sample(duration, 60, (t, a) => Shake(t, a));
                    curves["localPosition.y"] = Sample(duration, 60, (t, a) => Shake(t + 0.31f, a));
                    curves["localPosition.z"] = Sample(duration, 60, (t, a) => Shake(t + 0.62f, a));
                    break;

                case "hover":
                    curves["localPosition.y"] = Sample(duration, 48, (t, a) => offset.y + Hover(t, a));
                    break;

                case "bob":
                    curves["localPosition.x"] = Sample(duration, 48, (t, a) => offset.x + BobX(t, a));
                    curves["localPosition.y"] = Sample(duration, 48, (t, a) => offset.y + Mathf.Abs(BobX(t, a)));
                    break;

                case "sway":
                    curves["localEulerAnglesRaw.x"] = Constant(duration, 0f);
                    curves["localEulerAnglesRaw.y"] = Constant(duration, 0f);
                    curves["localEulerAnglesRaw.z"] = Sample(duration, 48, (t, a) => Sway(t, a));
                    break;

                case "wiggle":
                    curves["localEulerAnglesRaw.x"] = Sample(duration, 48, (t, a) => Wiggle(t, a));
                    curves["localEulerAnglesRaw.y"] = Constant(duration, 0f);
                    curves["localEulerAnglesRaw.z"] = Sample(duration, 48, (t, a) => Wiggle(t + 0.25f, a * 0.5f));
                    break;

                case "blink":
                {
                    var blink = new AnimationCurve(
                        new Keyframe(0f, 1f),
                        new Keyframe(duration * 0.45f, 1f),
                        new Keyframe(duration * 0.5f, 0.05f),
                        new Keyframe(duration * 0.55f, 1f),
                        new Keyframe(duration, 1f));
                    curves["localScale.x"] = blink;
                    curves["localScale.y"] = blink;
                    break;
                }

                case "slide_in":
                    curves["localPosition.x"] = new AnimationCurve(
                        new Keyframe(0f, offset.x - 6f * amplitude),
                        new Keyframe(duration, offset.x));
                    break;

                case "elastic":
                {
                    AnimationCurve elastic = Sample(duration, 60, (t, a) => 1f + Elastic(t, a));
                    curves["localScale.x"] = elastic;
                    curves["localScale.y"] = elastic;
                    curves["localScale.z"] = elastic;
                    break;
                }

                case "grow":
                {
                    AnimationCurve grow = Sample(duration, 32, (t, a) => t * a);
                    curves["localScale.x"] = grow;
                    curves["localScale.y"] = grow;
                    curves["localScale.z"] = grow;
                    break;
                }

                case "shrink":
                {
                    AnimationCurve shrink = Sample(duration, 32, (t, a) => (1f - t) * a);
                    curves["localScale.x"] = shrink;
                    curves["localScale.y"] = shrink;
                    curves["localScale.z"] = shrink;
                    break;
                }

                default:
                    curves["localPosition.y"] = Sample(duration, 48, (t, a) => offset.y + Hover(t, a));
                    break;
            }
        }

        /// <summary>Uniformly samples a closed-form function and builds a smooth curve from it.</summary>
        internal static AnimationCurve Sample(float duration, int sampleCount, WaveFunction wave)
        {
            return Sample(duration, sampleCount, 1f, wave);
        }

        internal static AnimationCurve Sample(float duration, int sampleCount, float amplitude, WaveFunction wave)
        {
            int count = Mathf.Clamp(sampleCount, 2, 600);
            if (duration <= 0f) duration = 1f;

            var keys = new Keyframe[count + 1];
            for (int index = 0; index <= count; index++)
            {
                float t = (float)index / count;
                keys[index] = new Keyframe(t * duration, wave(t, amplitude));
            }

            var curve = new AnimationCurve(keys);
            for (int index = 0; index < curve.length; index++)
            {
                curve.SmoothTangents(index, 0f);
            }

            return curve;
        }

        internal static AnimationCurve Constant(float duration, float value)
        {
            return new AnimationCurve(
                new Keyframe(0f, value),
                new Keyframe(Mathf.Max(duration, 0.0001f), value));
        }

        // ---------------------------------------------------------------- shape functions

        internal static float Bounce(float t, float amplitude)
        {
            float decay = 1f - t;
            float height = 1f - 4f * (t - 0.5f) * (t - 0.5f);
            return amplitude * 2f * Mathf.Max(0f, decay * decay * height);
        }

        internal static float Pulse(float t, float amplitude)
        {
            return amplitude * 0.25f * Mathf.Sin(t * Mathf.PI * 2f);
        }

        internal static float Shake(float t, float amplitude)
        {
            return amplitude * 0.1f * Mathf.Sin(t * Mathf.PI * 12f);
        }

        internal static float Hover(float t, float amplitude)
        {
            return amplitude * 0.25f * Mathf.Sin(t * Mathf.PI * 2f);
        }

        internal static float BobX(float t, float amplitude)
        {
            return amplitude * 0.2f * Mathf.Cos(t * Mathf.PI * 2f);
        }

        internal static float Sway(float t, float amplitude)
        {
            return amplitude * 3f * Mathf.Sin(t * Mathf.PI * 2f);
        }

        internal static float Wiggle(float t, float amplitude)
        {
            return amplitude * 5f * Mathf.Sin(t * Mathf.PI * 4f);
        }

        internal static float Elastic(float t, float amplitude)
        {
            return amplitude * 0.5f * Mathf.Exp(-6f * t) * Mathf.Sin(t * Mathf.PI * 6f);
        }
    }
}
