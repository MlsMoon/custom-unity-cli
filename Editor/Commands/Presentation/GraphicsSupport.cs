using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Shared plumbing for the <c>graphics</c> group: the active pipeline, reflection helpers that
    /// never throw, the Volume / VolumeProfile type probes and the Profiler counter lookup.
    ///
    /// Nothing here references an optional package at compile time - URP and the Render Pipelines
    /// Core package are both reached through <see cref="CucliOptional"/>.
    /// </summary>
    internal static class GraphicsSupport
    {
        internal const string CoreAssembly = "Unity.RenderPipelines.Core.Runtime";
        internal const string UniversalAssembly = "Unity.RenderPipelines.Universal.Runtime";

        /// <summary>
        /// Parameter schema for the whole <c>graphics</c> group. JSON Schema cannot express
        /// "required for action X only", so the union of every parameter is declared here and each
        /// action validates what it reads.
        /// </summary>
        internal const string ParametersSchemaJson = @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""ping"", ""volume_create"", ""volume_add_effect"", ""volume_set_effect"", ""volume_remove_effect"",
                 ""volume_get_info"", ""volume_set_properties"", ""volume_list_effects"", ""volume_create_profile"",
                 ""bake_start"", ""bake_cancel"", ""bake_status"", ""bake_clear"", ""bake_reflection_probe"",
                 ""bake_get_settings"", ""bake_set_settings"", ""bake_create_light_probe_group"",
                 ""bake_create_reflection_probe"", ""bake_set_probe_positions"",
                 ""stats_get"", ""stats_list_counters"", ""stats_set_scene_debug"", ""stats_get_memory"",
                 ""pipeline_get_info"", ""pipeline_set_quality"", ""pipeline_get_settings"", ""pipeline_set_settings"",
                 ""feature_list"", ""feature_add"", ""feature_remove"", ""feature_configure"", ""feature_toggle"",
                 ""feature_reorder"",
                 ""skybox_get"", ""skybox_set_material"", ""skybox_set_properties"", ""skybox_set_ambient"",
                 ""skybox_set_fog"", ""skybox_set_reflection"", ""skybox_set_sun""],
      ""default"": ""ping""
    },
    ""name"": { ""type"": ""string"", ""description"": ""New object or feature name; also a target fallback for bake_probe and skybox_set_sun."" },
    ""target"": { ""type"": ""string"", ""description"": ""Volume or GameObject name / instance id."" },
    ""isGlobal"": { ""type"": ""boolean"", ""default"": true, ""description"": ""action=volume_create / volume_set_properties."" },
    ""weight"": { ""type"": ""number"", ""default"": 1.0, ""description"": ""action=volume_create / volume_set_properties."" },
    ""priority"": { ""type"": ""number"", ""default"": 0.0, ""description"": ""action=volume_create / volume_set_properties."" },
    ""blendDistance"": { ""type"": ""number"", ""description"": ""action=volume_set_properties."" },
    ""profilePath"": { ""type"": ""string"", ""description"": ""action=volume_create: VolumeProfile asset path; '(embedded)' when omitted."" },
    ""path"": { ""type"": ""string"", ""description"": ""action=volume_create_profile / skybox_set_material / skybox_set_reflection."" },
    ""materialPath"": { ""type"": ""string"", ""description"": ""action=skybox_set_material fallback key."" },
    ""cubemapPath"": { ""type"": ""string"", ""description"": ""action=skybox_set_reflection fallback key."" },
    ""effect"": { ""type"": ""string"", ""description"": ""action=volume_add_effect / volume_set_effect / volume_remove_effect: VolumeComponent type name."" },
    ""effects"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""action=volume_create: [{ type, parameters }] applied after creation."" },
    ""parameters"": { ""type"": ""object"", ""description"": ""action=volume_set_effect / skybox_set_properties: field name to value."" },
    ""properties"": { ""type"": ""object"", ""description"": ""action=volume_set_properties / feature_add / feature_configure / skybox_set_properties."" },
    ""settings"": { ""type"": ""object"", ""description"": ""action=bake_set_settings / pipeline_set_settings / feature_configure."" },
    ""async"": { ""type"": ""boolean"", ""default"": true, ""description"": ""action=bake_start: Lightmapping.BakeAsync versus Bake."" },
    ""timeoutSeconds"": { ""type"": ""integer"", ""minimum"": 1, ""description"": ""action=bake_start / bake_reflection_probe wait cap."" },
    ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""action=bake_create_*: [x,y,z]."" },
    ""gridSize"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""action=bake_create_light_probe_group: [x,y,z] grid, default [3,2,3]."" },
    ""spacing"": { ""type"": ""number"", ""default"": 2.0, ""description"": ""action=bake_create_light_probe_group."" },
    ""size"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""action=bake_create_reflection_probe: bounds size, default [10,10,10]."" },
    ""resolution"": { ""type"": ""integer"", ""default"": 256, ""description"": ""action=bake_create_reflection_probe / skybox_set_reflection."" },
    ""mode"": { ""type"": ""string"", ""description"": ""action=bake_create_reflection_probe / stats_set_scene_debug / skybox_set_*."" },
    ""hdr"": { ""type"": ""boolean"", ""default"": true, ""description"": ""action=bake_create_reflection_probe."" },
    ""boxProjection"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=bake_create_reflection_probe."" },
    ""positions"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""description"": ""action=bake_set_probe_positions: [[x,y,z], ...]."" },
    ""category"": { ""type"": ""string"", ""default"": ""Render"", ""description"": ""action=stats_list_counters: Profiler category name."" },
    ""level"": { ""type"": ""string"", ""description"": ""action=pipeline_set_quality: quality level name or index."" },
    ""rendererIndex"": { ""type"": ""integer"", ""description"": ""action=feature_*: index into the URP renderer data list."" },
    ""type"": { ""type"": ""string"", ""description"": ""action=feature_add: ScriptableRendererFeature type name."" },
    ""material"": { ""type"": ""string"", ""description"": ""action=feature_add / skybox_set_material: material asset path."" },
    ""index"": { ""type"": ""integer"", ""description"": ""action=feature_remove / feature_configure / feature_toggle."" },
    ""active"": { ""type"": ""boolean"", ""description"": ""action=feature_toggle."" },
    ""order"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""action=feature_reorder: a permutation of 0..N-1."" },
    ""color"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""action=skybox_set_ambient / skybox_set_fog."" },
    ""skyColor"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""equatorColor"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""groundColor"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""ambientMode"": { ""type"": ""string"", ""description"": ""action=skybox_set_ambient: Skybox / Trilight / Flat / Custom."" },
    ""intensity"": { ""type"": ""number"", ""description"": ""action=skybox_set_ambient / skybox_set_reflection."" },
    ""fogEnabled"": { ""type"": ""boolean"", ""description"": ""action=skybox_set_fog."" },
    ""fogMode"": { ""type"": ""string"", ""description"": ""action=skybox_set_fog: Linear / Exponential / ExponentialSquared."" },
    ""fogColor"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""fogDensity"": { ""type"": ""number"", ""description"": ""action=skybox_set_fog."" },
    ""fogStart"": { ""type"": ""number"", ""description"": ""action=skybox_set_fog."" },
    ""fogEnd"": { ""type"": ""number"", ""description"": ""action=skybox_set_fog."" },
    ""bounces"": { ""type"": ""integer"", ""description"": ""action=skybox_set_reflection."" },
    ""reflectionMode"": { ""type"": ""string"", ""description"": ""action=skybox_set_reflection: Skybox / Custom."" },
    ""enabled"": { ""type"": ""boolean"", ""description"": ""action=skybox_set_fog fallback key."" },
    ""start"": { ""type"": ""number"", ""description"": ""action=skybox_set_fog fallback key."" },
    ""end"": { ""type"": ""number"", ""description"": ""action=skybox_set_fog fallback key."" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 },
    ""pageSize"": { ""type"": ""integer"", ""default"": 100 },
    ""cursor"": { ""type"": ""string"" }
  }
}";

        // ------------------------------------------------------------ active pipeline

        internal static RenderPipelineAsset CurrentPipeline()
        {
            try
            {
                return GraphicsSettings.currentRenderPipeline;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string ActivePipelineTypeName()
        {
            RenderPipelineAsset pipeline = CurrentPipeline();
            return pipeline == null ? string.Empty : pipeline.GetType().FullName ?? pipeline.GetType().Name;
        }

        internal static bool IsUrpActive()
        {
            string name = ActivePipelineTypeName();
            return name.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("URP", StringComparison.Ordinal) >= 0;
        }

        internal static bool IsHdrpActive()
        {
            string name = ActivePipelineTypeName();
            return name.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("HDRP", StringComparison.Ordinal) >= 0;
        }

        /// <summary>The standard "this needs URP" failure, raised before any URP reflection happens.</summary>
        internal static CucliCommandResult RequireUrp(string capability)
        {
            if (IsUrpActive()) return null;

            RenderPipelineAsset pipeline = CurrentPipeline();
            string current = pipeline == null ? "Built-in Render Pipeline" : pipeline.GetType().Name;
            return CucliCommandResult.Fail(
                CucliErrors.CommandFailed,
                capability + " requires the Universal Render Pipeline to be active, but the current pipeline is "
                + current + ". Switch the pipeline in Project Settings > Graphics, then retry.",
                new { currentPipeline = current, requiresUrp = true });
        }

        // ------------------------------------------------------------ reflection helpers

        internal static object PropertyOrNull(object target, string name, CucliDescribe.Options options)
        {
            if (target == null) return null;

            string error;
            object value = CucliReflection.Get(target, name, out error);
            if (error != null) return null;
            return CucliDescribe.Value(value, options ?? CucliDescribe.Options.Default, 1);
        }

        /// <summary>
        /// Reads a number from a Unity editor statistic. Missing statistics in this Unity version
        /// produce null rather than an exception, because the available set changes between versions.
        /// </summary>
        internal static long? UnityStat(string name)
        {
            Type type = CucliReflection.FindType("UnityEditor.UnityStats", out string ignored);
            if (type == null) return null;

            try
            {
                PropertyInfo property = type.GetProperty(
                    name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null)
                {
                    object value = property.GetValue(null, null);
                    return value == null ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }

                FieldInfo field = type.GetField(
                    name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null)
                {
                    object value = field.GetValue(null);
                    return value == null ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        /// <summary>
        /// Reads one Profiler counter by category and name; <c>-1</c> when this Unity version or
        /// platform does not expose it. A genuine reading of <c>0</c> is valid, so callers must test
        /// for a negative value rather than falsiness.
        ///
        /// This deliberately uses the public
        /// <see cref="ProfilerRecorder.StartNew(ProfilerCategory, string, int)"/> API instead of
        /// enumerating counters. Enumeration needs
        /// <c>Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle</c>, which is not part of the
        /// public surface on every Unity version and is absent unless the project happens to pull in
        /// <c>com.unity.profiling.core</c>. A package that must compile anywhere cannot depend on it.
        /// </summary>
        internal static long ProfilerCounter(string category, string counterName)
        {
            if (string.IsNullOrWhiteSpace(counterName)) return -1L;

            try
            {
                using (ProfilerRecorder recorder =
                    ProfilerRecorder.StartNew(ResolveCategory(category), counterName.Trim(), 1))
                {
                    return recorder.Valid ? recorder.LastValue : -1L;
                }
            }
            catch (Exception)
            {
                // Some counters throw on platforms where the underlying statistic does not exist.
                return -1L;
            }
        }

        /// <summary>
        /// Maps a category name onto the stable public <see cref="ProfilerCategory"/> values.
        /// Unknown names fall back to Render, which is what the rest of this package reads.
        /// </summary>
        internal static ProfilerCategory ResolveCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return ProfilerCategory.Render;
            switch (category.Trim().ToLowerInvariant())
            {
                case "memory": return ProfilerCategory.Memory;
                case "scripts": return ProfilerCategory.Scripts;
                case "physics": return ProfilerCategory.Physics;
                case "animation": return ProfilerCategory.Animation;
                case "audio": return ProfilerCategory.Audio;
                case "lighting": return ProfilerCategory.Lighting;
                case "network": return ProfilerCategory.Network;
                case "gui": return ProfilerCategory.Gui;
                case "loading": return ProfilerCategory.Loading;
                case "video": return ProfilerCategory.Video;
                case "internal": return ProfilerCategory.Internal;
                default: return ProfilerCategory.Render;
            }
        }

        private static readonly string[] ProfilerCategories =
        {
            "Render", "Scripts", "Memory", "Physics", "Animation", "Audio", "Lighting", "Network",
            "Gui", "AI", "Video", "Loading", "Input", "Vr", "Internal"
        };

        /// <summary>
        /// The counters this package knows how to read. Enumeration is unavailable without the
        /// low-level API, so the catalog is declared here and each entry is probed for validity when
        /// it is listed.
        /// </summary>
        private static readonly string[] RenderCounters =
        {
            "Render Textures", "Render Textures Bytes", "Used Textures", "Used Textures Bytes",
            "Render Target Changes", "Texture Memory", "Mesh Memory", "Draw Calls Count",
            "Triangles Count", "Vertices Count", "SetPass Calls Count", "Batches Count"
        };

        private static readonly string[] MemoryCounters =
        {
            "Total Used Memory", "Total Reserved Memory", "GC Used Memory", "GC Reserved Memory",
            "System Used Memory", "Texture Memory", "Mesh Memory"
        };

        /// <summary>Counter names declared for a category; empty for a category we do not model.</summary>
        internal static string[] KnownCounters(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return RenderCounters;
            switch (category.Trim().ToLowerInvariant())
            {
                case "memory": return MemoryCounters;
                case "render": return RenderCounters;
                default: return Array.Empty<string>();
            }
        }

        internal static string NormalizeCategory(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return "Render";
            foreach (string category in ProfilerCategories)
            {
                if (string.Equals(category, requested.Trim(), StringComparison.OrdinalIgnoreCase)) return category;
            }

            return "Render";
        }

        // ------------------------------------------------------------ Volume reflection

        internal static Type VolumeType()
        {
            return CucliOptional.FindType(CoreAssembly, "UnityEngine.Rendering.Volume");
        }

        internal static Type VolumeProfileType()
        {
            return CucliOptional.FindType(CoreAssembly, "UnityEngine.Rendering.VolumeProfile");
        }

        internal static Type VolumeComponentType()
        {
            return CucliOptional.FindType(CoreAssembly, "UnityEngine.Rendering.VolumeComponent");
        }

        internal static Type VolumeParameterType()
        {
            return CucliOptional.FindType(CoreAssembly, "UnityEngine.Rendering.VolumeParameter");
        }

        /// <summary>Finds the Volume components in the loaded scenes, including inactive ones.</summary>
        internal static List<Component> FindVolumes()
        {
            var result = new List<Component>();
            Type volumeType = VolumeType();
            if (volumeType == null) return result;

            foreach (GameObject gameObject in CucliObjectResolver.FindAllInLoadedScenes())
            {
                Component component = gameObject.GetComponent(volumeType);
                if (component != null) result.Add(component);
            }

            return result;
        }

        /// <summary>
        /// Resolves the <c>target</c> Volume: instance id, GameObject name, or the first Volume in the
        /// scene when no target is given - matching manage_graphics.
        /// </summary>
        internal static Component ResolveVolume(CucliCommandContext context, out CucliCommandResult failure)
        {
            failure = null;
            Type volumeType = VolumeType();
            if (volumeType == null)
            {
                failure = CucliOptional.Missing("com.unity.render-pipelines.core", "Volume 系统");
                return null;
            }

            string target = context.Input.GetString("target");
            List<Component> volumes = FindVolumes();

            if (string.IsNullOrWhiteSpace(target))
            {
                if (volumes.Count == 0)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        "No Volume exists in the loaded scenes. Create one with 'cucli graphics volume_create', or "
                        + "pass 'target' (name or instance ID).");
                    return null;
                }

                return volumes[0];
            }

            string trimmed = target.Trim();
            int numeric;
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                UnityEngine.Object resolved = EditorUtility.InstanceIDToObject(numeric);
                var component = resolved as Component;
                if (component != null && volumeType.IsInstanceOfType(component)) return component;

                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, "No Volume has instance ID " + numeric + ".");
                return null;
            }

            List<Component> matches = volumes
                .Where(candidate => string.Equals(candidate.gameObject.name, trimmed, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "No Volume named '" + trimmed + "' exists in the loaded scenes.");
                return null;
            }

            if (matches.Count > 1)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetAmbiguous,
                    matches.Count + " GameObjects are named '" + trimmed + "'. Pass an instance ID instead.",
                    new { matches = matches.Select(match => match.gameObject.GetInstanceID()).ToArray() });
                return null;
            }

            return matches[0];
        }

        internal static object VolumeProfileOf(Component volume)
        {
            if (volume == null) return null;
            return CucliOptional.GetProperty(volume, "profile") ?? CucliOptional.GetField(volume, "m_Profile");
        }

        internal static JToken DescribeVolume(Component volume)
        {
            if (volume == null) return JValue.CreateNull();
            object profile = VolumeProfileOf(volume);

            return new JObject
            {
                ["name"] = volume.gameObject.name,
                ["instance_id"] = volume.gameObject.GetInstanceID(),
                ["is_global"] = SafeBool(volume, "isGlobal"),
                ["weight"] = SafeFloat(volume, "weight"),
                ["priority"] = SafeFloat(volume, "priority"),
                ["blend_distance"] = SafeFloat(volume, "blendDistance"),
                ["profile"] = profile == null ? null : CucliDescribe.SafeToString(CucliOptional.GetProperty(profile, "name")),
                ["profile_path"] = profile == null
                    ? null
                    : GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(profile as UnityEngine.Object))
            };
        }

        private static bool? SafeBool(Component component, string member)
        {
            object value = CucliOptional.GetProperty(component, member) ?? CucliOptional.GetField(component, member);
            return value is bool ? (bool?)value : null;
        }

        private static double? SafeFloat(Component component, string member)
        {
            object value = CucliOptional.GetProperty(component, member) ?? CucliOptional.GetField(component, member);
            if (value == null) return null;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Reads the <c>components</c> list of a VolumeProfile without a compile-time reference.</summary>
        internal static IList<object> ProfileComponents(object profile)
        {
            var result = new List<object>();
            if (profile == null) return result;

            object components = CucliOptional.GetProperty(profile, "components")
                ?? CucliOptional.GetField(profile, "components");
            var enumerable = components as System.Collections.IEnumerable;
            if (enumerable == null) return result;

            foreach (object component in enumerable)
            {
                if (component != null) result.Add(component);
            }

            return result;
        }

        /// <summary>Creates a VolumeComponent instance by type, without a compile-time reference.</summary>
        internal static object CreateComponentInstance(Type componentType)
        {
            if (componentType == null) return null;

            object instance = null;
            try
            {
                ConstructorInfo constructor = componentType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (constructor != null) instance = constructor.Invoke(null);
            }
            catch (Exception)
            {
                instance = null;
            }

            if (instance != null) return instance;

            try
            {
                // Mirrors VolumeProfile.Add<T>() which builds components with CreateInstance.
                MethodInfo createInstance = typeof(ScriptableObject).GetMethod(
                    "CreateInstance",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Type) },
                    null);
                if (createInstance != null) instance = createInstance.Invoke(null, new object[] { componentType });
            }
            catch (Exception)
            {
                instance = null;
            }

            return instance;
        }

        /// <summary>Finds a VolumeComponent type by short or full name, case-insensitively.</summary>
        internal static Type FindEffectType(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "an effect type name is required, e.g. 'Bloom'.";
                return null;
            }

            Type baseType = VolumeComponentType();
            if (baseType == null)
            {
                error = "the Render Pipelines Core package is not installed.";
                return null;
            }

            string requested = name.Trim();
            var candidates = new List<Type>();
            foreach (Type type in EffectTypes(baseType))
            {
                if (string.Equals(type.Name, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }

                if (string.Equals(type.FullName, requested, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Insert(0, type);
                }
            }

            if (candidates.Count > 0) return candidates[0];

            error = "no VolumeComponent type named '" + requested + "' is available. Run "
                + "'cucli graphics volume_list_effects' to see the names.";
            return null;
        }

        internal static IEnumerable<Type> EffectTypes(Type baseType)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types.Where(type => type != null).ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || type.IsAbstract || type.IsGenericTypeDefinition) continue;
                    if (!baseType.IsAssignableFrom(type)) continue;
                    yield return type;
                }
            }
        }

        /// <summary>Every <c>VolumeParameter</c> field of a VolumeComponent, keyed by field name.</summary>
        internal static List<KeyValuePair<string, object>> ComponentParameters(object component)
        {
            var result = new List<KeyValuePair<string, object>>();
            if (component == null) return result;

            Type parameterType = VolumeParameterType();
            Type volumeComponentType = VolumeComponentType();
            if (parameterType == null || volumeComponentType == null) return result;

            FieldInfo[] fields = component.GetType().GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (FieldInfo field in fields)
            {
                if (!parameterType.IsAssignableFrom(field.FieldType)) continue;

                object value = null;
                try
                {
                    value = field.GetValue(component);
                }
                catch (Exception)
                {
                    continue;
                }

                if (value == null) continue;
                result.Add(new KeyValuePair<string, object>(field.Name, value));
            }

            return result;
        }

        internal static object ParameterValue(object parameter)
        {
            if (parameter == null) return null;

            object value = CucliOptional.GetField(parameter, "m_Value")
                ?? CucliOptional.GetField(parameter, "value")
                ?? CucliOptional.GetProperty(parameter, "value");
            if (value != null) return value;

            try
            {
                MethodInfo getValue = parameter.GetType().GetMethod(
                    "GetValue", BindingFlags.Public | BindingFlags.Instance);
                return getValue == null ? null : getValue.Invoke(parameter, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool TrySetParameterValue(object parameter, JToken value, out string error)
        {
            error = null;
            if (parameter == null)
            {
                error = "the parameter instance is null";
                return false;
            }

            Type parameterType = parameter.GetType();
            FieldInfo field = parameterType.GetField(
                "m_Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Type targetType = field != null ? field.FieldType : null;
            if (targetType == null)
            {
                PropertyInfo property = parameterType.GetProperty(
                    "value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                targetType = property == null ? null : property.PropertyType;
            }

            if (targetType == null)
            {
                error = parameterType.Name + " has no writable value";
                return false;
            }

            object converted;
            string convertError;
            try
            {
                converted = CucliValues.Convert(value, targetType, out convertError);
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            if (convertError != null)
            {
                error = convertError;
                return false;
            }

            try
            {
                if (field != null)
                {
                    field.SetValue(parameter, converted);
                }
                else
                {
                    PropertyInfo property = parameterType.GetProperty(
                        "value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property == null || !property.CanWrite)
                    {
                        error = parameterType.Name + ".value is read-only";
                        return false;
                    }

                    property.SetValue(parameter, converted, null);
                }
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            FieldInfo overrideField = parameterType.GetField(
                "m_OverrideState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (overrideField != null && overrideField.FieldType == typeof(bool))
            {
                overrideField.SetValue(parameter, true);
            }
            else
            {
                CucliOptional.SetProperty(parameter, "overrideState", true);
            }

            return true;
        }

        /// <summary>VolumeComponent field name to the name Unity exposes in the inspector.</summary>
        internal static string DisplayNameOf(object component, string fieldName)
        {
            if (component == null) return fieldName;
            try
            {
                FieldInfo field = component.GetType().GetField(
                    fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) return fieldName;

                object attribute = field.GetCustomAttributes(false)
                    .FirstOrDefault(item => item.GetType().Name == "DisplayInfoAttribute");
                if (attribute == null) return fieldName;

                object name = CucliOptional.GetField(attribute, "name")
                    ?? CucliOptional.GetProperty(attribute, "name");
                string text = name as string;
                return string.IsNullOrEmpty(text) ? fieldName : text;
            }
            catch (Exception)
            {
                return fieldName;
            }
        }

        internal static object OverrideStateOf(object parameter)
        {
            if (parameter == null) return null;
            return CucliOptional.GetField(parameter, "m_OverrideState")
                ?? CucliOptional.GetProperty(parameter, "overrideState");
        }
    }
}
