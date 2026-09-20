using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Shared plumbing for the <c>vfx</c> group.
    ///
    /// Two very different worlds live in this group and they are deliberately handled differently:
    ///
    /// <list type="bullet">
    /// <item><c>particle_*</c>, <c>line_*</c> and <c>trail_*</c> are plain Unity engine modules
    /// (<see cref="ParticleSystem"/>, <see cref="LineRenderer"/>, <see cref="TrailRenderer"/>). They
    /// are referenced directly and type-safely, because they are always present and always available.
    /// Making them reflective would cost readability for nothing.</item>
    /// <item><c>vfx_*</c> needs the VFX Graph package, so it is reached entirely through
    /// <see cref="CucliOptional"/> reflection. That is what keeps <c>vfx_*</c> from breaking the
    /// assembly in a project that has no VFX Graph.</item>
    /// </list>
    /// </summary>
    internal static class VfxSupport
    {
        internal const string VfxGraphAssembly = "Unity.VisualEffectGraph.Runtime";
        internal const string VfxComponentType = "UnityEngine.VFX.VisualEffect";
        internal const string VfxAssetTypeName = "UnityEngine.VFX.VisualEffectAsset";
        internal const string VfxPackage = "com.unity.visualeffectgraph";

        // ------------------------------------------------------------------ VFX Graph type access

        /// <summary>Resolves <c>UnityEngine.VFX.VisualEffect</c>, or null when the package is absent.</summary>
        internal static Type VfxComponent()
        {
            return CucliOptional.FindType(VfxGraphAssembly, VfxComponentType);
        }

        internal static Type VfxAssetType()
        {
            return CucliOptional.FindType(VfxGraphAssembly, VfxAssetTypeName);
        }

        /// <summary>The standard "install VFX Graph" failure, used by every <c>vfx_*</c> branch.</summary>
        internal static CucliCommandResult RequireVfx(string capability)
        {
            return CucliOptional.RequirePackage(VfxPackage, capability)
                ?? (VfxComponent() == null
                    ? CucliCommandResult.Fail(
                        CucliErrors.OptionalPackageMissing,
                        "VFX Graph 已安装，但反射不到 UnityEngine.VFX.VisualEffect（程序集 "
                        + VfxGraphAssembly + "）。已安装的包版本可能与本命令预期不同。",
                        new { assembly = VfxGraphAssembly, type = VfxComponentType })
                    : null);
        }

        // ------------------------------------------------------------------ target resolution

        internal static GameObject ResolveTarget(CucliArgs args, string capability, out string error)
        {
            error = null;
            if (!args.Has("target"))
            {
                error = "target is required for " + capability + "。"
                    + "可以传层级路径、名称、instanceId，或用 selected: true。";
                return null;
            }

            // The shared resolver owns every addressing mode, including searchMethod.
            GameObject target = CucliObjectResolver.ResolveGameObject(args, out error);
            if (target == null && error == null)
            {
                error = "target 解析失败：没有匹配的 GameObject。";
            }

            return target;
        }

        /// <summary>
        /// Finds the requested component on the target, honouring <c>componentIndex</c>. Mirrors the
        /// MCP behaviour of reporting the real component count when the index is out of range.
        /// </summary>
        internal static bool TryResolveComponent<T>(
            CucliArgs args, string capability, out T component, out string error) where T : Component
        {
            component = null;
            GameObject target = ResolveTarget(args, capability, out error);
            if (target == null) return false;

            T[] all = target.GetComponents<T>();
            if (all == null || all.Length == 0)
            {
                error = typeof(T).Name + " not found on '" + target.name + "'。"
                    + "该 action 只对已存在的组件写值，不会自动添加组件。";
                return false;
            }

            int index = args.GetInt("componentIndex", args.GetInt("component_index", 0));
            if (index < 0 || index >= all.Length)
            {
                error = "componentIndex " + index + " out of range. Found " + all.Length + " "
                    + typeof(T).Name + " component(s) on '" + target.name + "'.";
                return false;
            }

            component = all[index];
            return true;
        }

        /// <summary>Reflected-component variant, used by <c>vfx_*</c> where the type is not compile-time.</summary>
        internal static bool TryResolveReflectedComponent(
            CucliArgs args, string capability, out Component component, out string error)
        {
            component = null;
            Type type = VfxComponent();
            if (type == null)
            {
                error = "VFX Graph 不可用。";
                return false;
            }

            GameObject target = ResolveTarget(args, capability, out error);
            if (target == null) return false;

            Component[] all = target.GetComponents(type);
            if (all == null || all.Length == 0)
            {
                error = "VisualEffect not found on '" + target.name + "'。"
                    + "vfx_* 只对已存在的 VisualEffect 组件写值；先用 vfx_assign_asset 绑定一个 VFX 资产。";
                return false;
            }

            int index = args.GetInt("componentIndex", args.GetInt("component_index", 0));
            if (index < 0 || index >= all.Length)
            {
                error = "componentIndex " + index + " out of range. Found " + all.Length
                    + " VisualEffect component(s) on '" + target.name + "'.";
                return false;
            }

            component = all[index];
            return true;
        }

        // ------------------------------------------------------------------ instance reflection

        /// <summary>
        /// Invokes an instance method by name, choosing the overload whose parameters match the
        /// supplied arguments.
        ///
        /// The MCP ProBuilder/VFX tools fail with a bare "... method not found." when a reflected
        /// overload is missing. That message is useless to whoever has to fix it, so this reports the
        /// type, the method and the candidate signatures it did see.
        /// </summary>
        internal static bool TryInvokeInstance(
            object target, string method, object[] arguments, out object result, out string error)
        {
            result = null;
            error = null;
            if (target == null)
            {
                error = "无法在 null 上调用 " + method + "。";
                return false;
            }

            var types = new Type[arguments == null ? 0 : arguments.Length];
            for (int index = 0; index < types.Length; index++)
            {
                types[index] = arguments[index] == null ? typeof(object) : arguments[index].GetType();
            }

            Type owner = target.GetType();
            MethodInfo match = null;
            try
            {
                match = owner.GetMethod(
                    method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    types,
                    null);
            }
            catch (AmbiguousMatchException)
            {
                match = owner
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, method, StringComparison.Ordinal)
                        && candidate.GetParameters().Length == types.Length);
            }

            if (match == null)
            {
                string[] known = Array.Empty<string>();
                try
                {
                    known = owner
                        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(candidate => string.Equals(candidate.Name, method, StringComparison.Ordinal))
                        .Select(candidate => candidate.Name + "("
                            + string.Join(", ", candidate.GetParameters().Select(p => CucliValues.FriendlyName(p.ParameterType)))
                            + ")")
                        .Distinct()
                        .ToArray();
                }
                catch (Exception)
                {
                    // Diagnostics only.
                }

                error = owner.Name + "." + method + "(" + string.Join(", ", types.Select(CucliValues.FriendlyName))
                    + ") not found。已安装的包版本可能与本命令预期不同。"
                    + (known.Length == 0
                        ? "该类型上没有同名方法。"
                        : "同名重载：" + string.Join("; ", known));
                return false;
            }

            try
            {
                result = match.Invoke(target, arguments);
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = (exception.InnerException ?? exception).GetBaseException().Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ value parsing

        /// <summary>
        /// AnimationCurve from a number, a <c>{keys:[...]}</c> object, or a
        /// <c>{start,end}</c> / <c>{startValue,endValue}</c> pair. Falls back to a constant curve,
        /// matching the MCP behaviour of never failing on a malformed curve.
        /// </summary>
        internal static AnimationCurve ParseCurve(JToken token, float fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return AnimationCurve.Constant(0f, 1f, fallback);

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return AnimationCurve.Constant(0f, 1f, token.Value<float>());
            }

            if (token.Type == JTokenType.Array)
            {
                string convertError;
                object converted = CucliValues.Convert(token, typeof(AnimationCurve), out convertError);
                return convertError == null && converted != null
                    ? (AnimationCurve)converted
                    : AnimationCurve.Constant(0f, 1f, fallback);
            }

            var args = new CucliArgs(token as JObject ?? new JObject());
            JArray keys = args.GetArray("keys");
            if (keys != null && keys.Count > 0)
            {
                var frames = new List<Keyframe>(keys.Count);
                foreach (JToken item in keys)
                {
                    var entry = new CucliArgs(item as JObject ?? new JObject());
                    frames.Add(new Keyframe(
                        entry.GetFloat("time"), entry.GetFloat("value"),
                        entry.GetFloat("inTangent"), entry.GetFloat("outTangent")));
                }

                return new AnimationCurve(frames.ToArray());
            }

            float start = args.GetFloat("start", args.GetFloat("startValue", fallback));
            float end = args.GetFloat("end", args.GetFloat("endValue", start));
            return AnimationCurve.Linear(0f, start, 1f, end);
        }

        /// <summary>Gradient from <c>{startColor,endColor}</c> or explicit colour/alpha key arrays.</summary>
        internal static Gradient ParseGradient(JToken token)
        {
            var gradient = new Gradient();
            var args = new CucliArgs(token as JObject ?? new JObject());

            JArray colorKeys = args.GetArray("colorKeys");
            JArray alphaKeys = args.GetArray("alphaKeys");

            if ((colorKeys == null || colorKeys.Count == 0) && (alphaKeys == null || alphaKeys.Count == 0))
            {
                Color start = args.GetColor("startColor", Color.white);
                Color end = args.GetColor("endColor", Color.white);
                gradient.SetKeys(
                    new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) },
                    new[] { new GradientAlphaKey(start.a, 0f), new GradientAlphaKey(end.a, 1f) });
                return gradient;
            }

            var parsedColors = new List<GradientColorKey>();
            if (colorKeys != null)
            {
                foreach (JToken item in colorKeys)
                {
                    var entry = new CucliArgs(item as JObject ?? new JObject());
                    Color color = entry.GetColor("color", Color.white);
                    parsedColors.Add(new GradientColorKey(color, Mathf.Clamp01(entry.GetFloat("time"))));
                }
            }

            var parsedAlphas = new List<GradientAlphaKey>();
            if (alphaKeys != null)
            {
                foreach (JToken item in alphaKeys)
                {
                    var entry = new CucliArgs(item as JObject ?? new JObject());
                    parsedAlphas.Add(new GradientAlphaKey(
                        Mathf.Clamp01(entry.GetFloat("alpha", 1f)),
                        Mathf.Clamp01(entry.GetFloat("time"))));
                }
            }

            if (parsedColors.Count == 0)
            {
                parsedColors.Add(new GradientColorKey(Color.white, 0f));
                parsedColors.Add(new GradientColorKey(Color.white, 1f));
            }

            if (parsedAlphas.Count == 0)
            {
                parsedAlphas.Add(new GradientAlphaKey(1f, 0f));
                parsedAlphas.Add(new GradientAlphaKey(1f, 1f));
            }

            gradient.SetKeys(parsedColors.ToArray(), parsedAlphas.ToArray());
            return gradient;
        }

        /// <summary>
        /// <see cref="ParticleSystem.MinMaxCurve"/> from a number, a mode object, or a nested curve.
        /// Only the keys actually present are consumed, so an empty token keeps the caller's default.
        /// </summary>
        internal static ParticleSystem.MinMaxCurve ParseMinMaxCurve(JToken token, float fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new ParticleSystem.MinMaxCurve(fallback);
            }

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return new ParticleSystem.MinMaxCurve(token.Value<float>());
            }

            var args = new CucliArgs(token as JObject ?? new JObject());
            string mode = (args.GetString("mode") ?? "constant").Trim().ToLowerInvariant();

            switch (mode)
            {
                case "random_between_constants":
                case "two_constants":
                    return new ParticleSystem.MinMaxCurve(args.GetFloat("min"), args.GetFloat("max"));

                case "curve":
                {
                    float multiplier = args.GetFloat("multiplier", 1f);
                    return new ParticleSystem.MinMaxCurve(
                        multiplier, ParseCurve(args.Token("keys") ?? args.Token("curve"), fallback));
                }

                case "two_curves":
                {
                    float multiplier = args.GetFloat("multiplier", 1f);
                    return new ParticleSystem.MinMaxCurve(
                        multiplier,
                        ParseCurve(args.Token("curveMin"), fallback),
                        ParseCurve(args.Token("curveMax"), fallback));
                }

                default:
                    return new ParticleSystem.MinMaxCurve(args.GetFloat("value", fallback));
            }
        }

        /// <summary><see cref="ParticleSystem.MinMaxGradient"/> from a colour, a mode object or a gradient.</summary>
        internal static ParticleSystem.MinMaxGradient ParseMinMaxGradient(JToken token, Color fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new ParticleSystem.MinMaxGradient(fallback);
            }

            if (token.Type == JTokenType.Array)
            {
                return new ParticleSystem.MinMaxGradient(new CucliArgs(new JObject { ["c"] = token }).GetColor("c", fallback));
            }

            var args = new CucliArgs(token as JObject ?? new JObject());
            if (!args.Has("mode") && (args.Has("startColor") || args.Has("colorKeys")))
            {
                return new ParticleSystem.MinMaxGradient(ParseGradient(token));
            }

            switch ((args.GetString("mode") ?? "color").Trim().ToLowerInvariant())
            {
                case "two_colors":
                    return new ParticleSystem.MinMaxGradient(
                        args.GetColor("colorMin", fallback), args.GetColor("colorMax", fallback));

                case "gradient":
                    return new ParticleSystem.MinMaxGradient(ParseGradient(token));

                default:
                    return args.Has("color")
                        ? new ParticleSystem.MinMaxGradient(args.GetColor("color", fallback))
                        : new ParticleSystem.MinMaxGradient(ParseGradient(token));
            }
        }

        /// <summary>
        /// Enum parsing that matches the MCP contract: an unknown string is silently skipped rather
        /// than being an error. Callers use the return value to decide whether to record a change.
        /// </summary>
        internal static bool TryEnum<T>(CucliArgs args, string key, out T value) where T : struct
        {
            value = default(T);
            string text = args.GetString(key);
            if (string.IsNullOrWhiteSpace(text)) return false;

            try
            {
                value = (T)Enum.Parse(typeof(T), text.Trim(), true);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        /// <summary>Unlit-ish shaders that render a line or trail acceptably without extra setup.</summary>
        internal static readonly string[] DefaultSurfaceShaderNames =
        {
            "Sprites/Default",
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Particles/Unlit",
            "Particles/Standard Unlit",
            "Unlit/Color"
        };

        /// <summary>
        /// Resolves an <em>existing</em> project material for a caller who asked for a line or trail
        /// material without naming one.
        ///
        /// It deliberately returns an asset path instead of constructing a Material in memory. An
        /// unsaved Material assigned to a renderer looks fine until the scene is saved, at which point
        /// the reference is dropped and the caller is left wondering why the line went pink. Returning
        /// null and letting the command explain itself is the honest failure.
        /// </summary>
        internal static string FindDefaultSurfaceMaterial(CucliCommandContext context, string kind)
        {
            foreach (string name in new[] { "Default-Line", "Default-Trail", "Default-Particle", "Default-Material" })
            {
                string[] named = CucliAssets.FindAssets("Material", null, name, 1);
                if (named.Length > 0) return named[0];
            }

            foreach (string shaderName in DefaultSurfaceShaderNames)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader == null) continue;

                foreach (string path in CucliAssets.FindAssets("Material", null, null, 500))
                {
                    var candidate = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (candidate != null && candidate.shader == shader) return path;
                }
            }

            context.Warn("工程里没有可作为默认 " + kind + " 材质的资产；请在 " + kind
                + " 命令上显式传 materialPath。");
            return null;
        }

        internal static Material LoadMaterial(string materialPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(materialPath)) return null;

            string normalized = CucliAssets.NormalizeAssetPath(materialPath);
            if (normalized == null)
            {
                error = "材质路径越界或非法：" + materialPath;
                return null;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(normalized);
            if (material == null)
            {
                error = "Material not found at path: " + normalized;
            }

            return material;
        }

        /// <summary>
        /// Reads <c>materialPath</c> with the MCP alias fallback: a top-level or nested
        /// <c>material</c> key is treated as the same thing.
        /// </summary>
        internal static string MaterialPath(CucliArgs args)
        {
            return args.GetString("materialPath")
                ?? args.GetString("material_path")
                ?? args.GetString("material");
        }

        // ------------------------------------------------------------------ renderer properties

        /// <summary>
        /// Applies the renderer-level properties documented for both <c>line_set_properties</c> and
        /// <c>trail_set_properties</c>. Shared because the two lists are genuinely identical.
        /// </summary>
        internal static void ApplyCommonRendererProperties(CucliArgs args, Renderer renderer, ChangeSet changes)
        {
            if (renderer == null) return;

            var lineRenderer = renderer as LineRenderer;
            if (lineRenderer != null)
            {
                if (args.Has("loop")) { lineRenderer.loop = args.GetBool("loop"); changes.Add("loop"); }
                if (args.Has("useWorldSpace")) { lineRenderer.useWorldSpace = args.GetBool("useWorldSpace"); changes.Add("useWorldSpace"); }
            }

            if (args.Has("numCornerVertices"))
            {
                int value = args.GetInt("numCornerVertices");
                if (lineRenderer != null) lineRenderer.numCornerVertices = value;
                else if (renderer is TrailRenderer trailCorners) trailCorners.numCornerVertices = value;
                changes.Add("numCornerVertices");
            }

            if (args.Has("numCapVertices"))
            {
                int value = args.GetInt("numCapVertices");
                if (lineRenderer != null) lineRenderer.numCapVertices = value;
                else if (renderer is TrailRenderer trailCaps) trailCaps.numCapVertices = value;
                changes.Add("numCapVertices");
            }

            LineAlignment alignment;
            if (TryEnum(args, "alignment", out alignment))
            {
                if (lineRenderer != null) lineRenderer.alignment = alignment;
                else if (renderer is TrailRenderer trailAlign) trailAlign.alignment = alignment;
                changes.Add("alignment");
            }

            LineTextureMode textureMode;
            if (TryEnum(args, "textureMode", out textureMode))
            {
                if (lineRenderer != null) lineRenderer.textureMode = textureMode;
                else if (renderer is TrailRenderer trailTexture) trailTexture.textureMode = textureMode;
                changes.Add("textureMode");
            }

            if (args.Has("generateLightingData"))
            {
                bool value = args.GetBool("generateLightingData");
                if (lineRenderer != null) lineRenderer.generateLightingData = value;
                else if (renderer is TrailRenderer trailLighting) trailLighting.generateLightingData = value;
                changes.Add("generateLightingData");
            }

            ShadowCastingMode shadows;
            if (TryEnum(args, "shadowCastingMode", out shadows)) { renderer.shadowCastingMode = shadows; changes.Add("shadowCastingMode"); }
            if (args.Has("receiveShadows")) { renderer.receiveShadows = args.GetBool("receiveShadows"); changes.Add("receiveShadows"); }

            LightProbeUsage probeUsage;
            if (TryEnum(args, "lightProbeUsage", out probeUsage)) { renderer.lightProbeUsage = probeUsage; changes.Add("lightProbeUsage"); }

            ReflectionProbeUsage reflectionUsage;
            if (TryEnum(args, "reflectionProbeUsage", out reflectionUsage)) { renderer.reflectionProbeUsage = reflectionUsage; changes.Add("reflectionProbeUsage"); }

            MotionVectorGenerationMode motionVectors;
            if (TryEnum(args, "motionVectorGenerationMode", out motionVectors)) { renderer.motionVectorGenerationMode = motionVectors; changes.Add("motionVectorGenerationMode"); }

            if (args.Has("sortingOrder")) { renderer.sortingOrder = args.GetInt("sortingOrder"); changes.Add("sortingOrder"); }
            if (args.Has("sortingLayerName"))
            {
                string name = args.GetString("sortingLayerName");
                if (!string.IsNullOrWhiteSpace(name)) { renderer.sortingLayerName = name; changes.Add("sortingLayerName"); }
            }

            if (args.Has("sortingLayerID")) { renderer.sortingLayerID = args.GetInt("sortingLayerID"); changes.Add("sortingLayerID"); }
            if (args.Has("renderingLayerMask"))
            {
                renderer.renderingLayerMask = (uint)args.GetLong("renderingLayerMask");
                changes.Add("renderingLayerMask");
            }
        }

        internal static void ApplyWidth(
            CucliArgs args, LineRenderer line, TrailRenderer trail, ChangeSet changes)
        {
            float? single = args.Has("width") ? args.GetFloat("width") : (float?)null;
            if (single.HasValue)
            {
                if (line != null) { line.startWidth = single.Value; line.endWidth = single.Value; }
                else if (trail != null) { trail.startWidth = single.Value; trail.endWidth = single.Value; }
                changes.Add("width");
            }

            if (args.Has("startWidth"))
            {
                float value = args.GetFloat("startWidth");
                if (line != null) line.startWidth = value; else if (trail != null) trail.startWidth = value;
                changes.Add("startWidth");
            }

            if (args.Has("endWidth"))
            {
                float value = args.GetFloat("endWidth");
                if (line != null) line.endWidth = value; else if (trail != null) trail.endWidth = value;
                changes.Add("endWidth");
            }
        }

        /// <summary>
        /// Line and trail colours differ on purpose: a line keeps its end alpha, a trail fades it to
        /// zero so it dissolves. Getting this backwards is a visible bug, so the caller states which
        /// behaviour it wants rather than the helper guessing.
        /// </summary>
        internal static void ApplyColor(
            CucliArgs args, LineRenderer line, TrailRenderer trail, bool fadeEndAlpha, ChangeSet changes)
        {
            if (args.Has("gradient"))
            {
                Gradient gradient = ParseGradient(args.Token("gradient"));
                if (line != null) line.colorGradient = gradient;
                else if (trail != null) trail.colorGradient = gradient;
                changes.Add("gradient");
                return;
            }

            if (args.Has("color"))
            {
                Color single = args.GetColor("color", Color.white);
                Color end = fadeEndAlpha ? new Color(single.r, single.g, single.b, 0f) : single;
                if (line != null) { line.startColor = single; line.endColor = end; }
                else if (trail != null) { trail.startColor = single; trail.endColor = end; }
                changes.Add("color");
                return;
            }

            if (args.Has("startColor"))
            {
                Color value = args.GetColor("startColor", Color.white);
                if (line != null) line.startColor = value; else if (trail != null) trail.startColor = value;
                changes.Add("startColor");
            }

            if (args.Has("endColor"))
            {
                Color value = args.GetColor("endColor", Color.white);
                if (fadeEndAlpha) value.a = 0f;
                if (line != null) line.endColor = value; else if (trail != null) trail.endColor = value;
                changes.Add("endColor");
            }
        }

        internal static object DescribeRenderer(Renderer renderer)
        {
            if (renderer == null) return null;
            return new
            {
                material = renderer.sharedMaterial == null ? null : renderer.sharedMaterial.name,
                shadowCastingMode = renderer.shadowCastingMode.ToString(),
                receiveShadows = renderer.receiveShadows,
                lightProbeUsage = renderer.lightProbeUsage.ToString(),
                reflectionProbeUsage = renderer.reflectionProbeUsage.ToString(),
                sortingOrder = renderer.sortingOrder,
                sortingLayerName = renderer.sortingLayerName,
                renderingLayerMask = renderer.renderingLayerMask,
                enabled = renderer.enabled
            };
        }

        internal static string AssetsPath(CucliArgs args, string key)
        {
            string value = args.GetString(key);
            if (string.IsNullOrWhiteSpace(value)) return null;
            string normalized = CucliAssets.NormalizeAssetPath(value);
            return normalized ?? value;
        }

        internal static string FormatFloat(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Accumulates the names of the fields a write actually changed so the reply can say what happened.
    ///
    /// The MCP tools return <c>"Updated: a, b, c"</c>; keeping that shape means a caller migrating from
    /// MCP reads the same message, and a silently-skipped enum never shows up as if it had applied.
    /// </summary>
    internal sealed class ChangeSet
    {
        private readonly List<string> m_names = new List<string>();

        internal int Count => m_names.Count;

        internal void Add(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!m_names.Contains(name)) m_names.Add(name);
        }

        internal string[] Names => m_names.ToArray();

        internal string Message(string fallback)
        {
            return m_names.Count == 0 ? fallback : "Updated: " + string.Join(", ", m_names);
        }
    }
}
