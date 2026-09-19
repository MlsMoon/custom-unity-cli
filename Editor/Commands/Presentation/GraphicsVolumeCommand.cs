using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
// RenderPipelineAsset lives in UnityEngine.Rendering, which is part of the core engine module -
// referencing it does NOT create a dependency on the render-pipelines.core package.
using UnityEngine.Rendering;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>volume_*</c> actions of the <c>graphics</c> group.
    ///
    /// Every type here lives in <c>com.unity.render-pipelines.core</c>
    /// (<c>UnityEngine.Rendering.Volume</c> / <c>VolumeProfile</c> / <c>VolumeComponent</c> /
    /// <c>VolumeParameter</c> in <c>Unity.RenderPipelines.Core.Runtime</c>), so nothing is referenced
    /// at compile time - the whole feature is reached through <see cref="CucliOptional"/> and plain
    /// reflection. When the package is absent every action fails with
    /// <c>OPTIONAL_PACKAGE_MISSING</c> instead of breaking the tool's build.
    /// </summary>
    internal static class GraphicsVolumeCommand
    {
        internal const string CorePackage = "com.unity.render-pipelines.core";

        /// <summary>The standard "the Volume system is not available" failure.</summary>
        private static CucliCommandResult RequireVolumeSystem()
        {
            if (GraphicsSupport.VolumeType() != null && GraphicsSupport.VolumeProfileType() != null) return null;
            return CucliOptional.Missing(CorePackage, "Volume 与 VolumeProfile");
        }

        private static CucliCommandResult RequireVolumeComponentSystem()
        {
            CucliCommandResult missing = RequireVolumeSystem();
            if (missing != null) return missing;
            if (GraphicsSupport.VolumeComponentType() != null && GraphicsSupport.VolumeParameterType() != null)
            {
                return null;
            }

            return CucliOptional.Missing(CorePackage, "VolumeComponent 与 VolumeParameter");
        }

        // ---------------------------------------------------------------- volume_create

        internal static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliCommandResult missing = RequireVolumeSystem();
            if (missing != null) return missing;

            CucliArgs args = context.Input;
            Type volumeType = GraphicsSupport.VolumeType();

            // Only write a VolumeProfile asset when a path was given; otherwise use the embedded one.
            string rawProfilePath = args.GetString("profilePath") ?? args.GetString("profile_path");
            string profilePath = null;
            UnityEngine.Object profile = null;

            if (!string.IsNullOrWhiteSpace(rawProfilePath))
            {
                string error;
                profilePath = NormalizeProfilePath(rawProfilePath, out error);
                if (error != null) return CucliCommandResult.Invalid("profilePath", error);

                CucliCommandResult created = CreateProfileAsset(context, profilePath, out profile);
                if (created != null && !created.Success) return created;
            }

            string name = args.GetString("name");
            if (string.IsNullOrWhiteSpace(name)) name = "Volume";

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "cucli graphics volume_create");

            Component volume = gameObject.AddComponent(volumeType);
            if (volume == null)
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The Volume component could not be added to a new GameObject.");
            }

            bool isGlobal = args.GetBool("isGlobal", args.GetBool("is_global", true));
            float weight = args.GetFloat("weight", 1f);
            float priority = args.GetFloat("priority", 0f);

            CucliOptional.SetProperty(volume, "isGlobal", isGlobal);
            CucliOptional.SetProperty(volume, "weight", weight);
            CucliOptional.SetProperty(volume, "priority", priority);

            if (profile != null) CucliOptional.SetProperty(volume, "profile", profile);

            EditorUtility.SetDirty(volume);
            Selection.activeGameObject = gameObject;

            // Optional effects applied straight after creation, matching manage_graphics.
            var applied = new JArray();
            var failed = new JArray();
            JArray effects = args.GetArray("effects");
            if (effects != null && profile != null)
            {
                foreach (JToken entry in effects)
                {
                    context.ThrowIfCancelled();
                    if (entry == null || entry.Type != JTokenType.Object) continue;

                    var element = (JObject)entry;
                    string typeName = element.Value<string>("type");
                    if (string.IsNullOrWhiteSpace(typeName)) continue;

                    string typeError;
                    Type effectType = GraphicsSupport.FindEffectType(typeName, out typeError);
                    if (effectType == null)
                    {
                        failed.Add(new JObject { ["effect"] = typeName, ["reason"] = typeError });
                        continue;
                    }

                    JToken parameters = element["parameters"];
                    var flat = new JObject();
                    foreach (JProperty property in element.Properties())
                    {
                        if (property.Name == "type" || property.Name == "parameters") continue;
                        if (parameters != null) continue;
                        flat[property.Name] = property.Value;
                    }

                    if (parameters == null) parameters = flat;

                    object component;
                    string addError;
                    if (!TryAddEffect(profile, effectType, out component, out addError))
                    {
                        failed.Add(new JObject { ["effect"] = typeName, ["reason"] = addError });
                        continue;
                    }

                    var set = new JArray();
                    var setFailed = new JArray();
                    ApplyEffectParameters(component, parameters as JObject, set, setFailed);
                    applied.Add(new JObject
                    {
                        ["type"] = effectType.Name,
                        ["set"] = set,
                        ["failed"] = setFailed
                    });
                }
            }
            else if (effects != null && effects.Count > 0 && profile == null)
            {
                context.Warn("'effects' was ignored because no profilePath was given and the embedded profile "
                    + "cannot be edited from here.");
            }

            EditorUtility.SetDirty(profile as UnityEngine.Object);
            CucliAssets.SaveAssets();
            UiCommand.MarkSceneDirty(gameObject);

            return CucliCommandResult.Ok(new
            {
                instanceID = gameObject.GetInstanceID(),
                name = gameObject.name,
                isGlobal,
                weight,
                priority,
                profilePath = profilePath ?? "(embedded)",
                effects = applied,
                failedEffects = failed
            }, "Created a Volume named " + gameObject.name + ".");
        }

        // ---------------------------------------------------------------- volume_create_profile

        internal static CucliCommandResult CreateProfile(CucliCommandContext context)
        {
            CucliCommandResult missing = RequireVolumeSystem();
            if (missing != null) return missing;

            string raw = context.Input.GetString("path") ?? context.Input.GetString("profilePath")
                ?? context.Input.GetString("profile_path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return CucliCommandResult.Missing("path", "Example: --path Assets/Settings/Gameplay Volume.asset");
            }

            string error;
            string path = NormalizeProfilePath(raw, out error);
            if (error != null) return CucliCommandResult.Invalid("path", error);

            UnityEngine.Object profile;
            CucliCommandResult created = CreateProfileAsset(context, path, out profile);
            if (created != null) return created;

            return CucliCommandResult.Ok(new { path }, "Created the VolumeProfile asset " + path + ".");
        }

        /// <summary>
        /// Creates the profile asset unless it already exists. Returns a non-null failure result only
        /// on error; when the asset already exists it is loaded and returned.
        /// </summary>
        private static CucliCommandResult CreateProfileAsset(
            CucliCommandContext context, string path, out UnityEngine.Object profile)
        {
            profile = null;
            Type profileType = GraphicsSupport.VolumeProfileType();
            if (profileType == null)
            {
                return CucliOptional.Missing(CorePackage, "VolumeProfile");
            }

            if (CucliAssets.Exists(path))
            {
                profile = AssetDatabase.LoadAssetAtPath(path, profileType);
                if (profile == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetExists,
                        path + " exists but is not a VolumeProfile asset. Delete it or pick another path.",
                        new { path });
                }

                context.Warn(path + " already existed; the existing profile was reused.");
                return null;
            }

            string parent = CucliAssets.ParentFolder(path);
            if (parent != null && !CucliAssets.EnsureFolder(parent))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The folder '" + parent + "' could not be created.");
            }

            object created = GraphicsSupport.CreateComponentInstance(profileType);
            profile = created as UnityEngine.Object;
            if (profile == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "A VolumeProfile instance could not be created.");
            }

            AssetDatabase.CreateAsset(profile, path);
            CucliAssets.SaveAssets();
            CucliAssets.Refresh();
            return null;
        }

        internal static string NormalizeProfilePath(string raw, out string error)
        {
            error = null;
            string value = raw.Trim().Replace('\\', '/');
            if (!value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                value = "Assets/" + value.TrimStart('/');
            }

            if (!value.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) value += ".asset";

            string normalized = CucliAssets.NormalizeAssetPath(value);
            if (normalized == null) error = "the path escapes the project: " + raw;
            return normalized;
        }

        // ---------------------------------------------------------------- volume_get_info

        internal static CucliCommandResult GetInfo(CucliCommandContext context)
        {
            CucliCommandResult missing = RequireVolumeSystem();
            if (missing != null) return missing;

            Component volume = GraphicsSupport.ResolveVolume(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            object profile = GraphicsSupport.VolumeProfileOf(volume);
            var data = (JObject)GraphicsSupport.DescribeVolume(volume);
            data["effects"] = DescribeProfileEffects(profile);

            return CucliCommandResult.Ok(data);
        }

        private static JArray DescribeProfileEffects(object profile)
        {
            var result = new JArray();
            foreach (object component in GraphicsSupport.ProfileComponents(profile))
            {
                var entry = new JObject
                {
                    ["type"] = component.GetType().Name,
                    ["fullName"] = component.GetType().FullName,
                    ["enabled"] = CucliJson.ToToken(GetActive(component))
                };

                var overridden = new JArray();
                foreach (KeyValuePair<string, object> pair in GraphicsSupport.ComponentParameters(component))
                {
                    object state = GraphicsSupport.OverrideStateOf(pair.Value);
                    bool overriddenState = state is bool && (bool)state;
                    if (!overriddenState) continue;

                    overridden.Add(GraphicsSupport.DisplayNameOf(component, pair.Key));
                }

                entry["overridden_params"] = overridden;
                result.Add(entry);
            }

            return result;
        }

        /// <summary>
        /// A VolumeComponent is a ScriptableObject with an <c>active</c> flag of its own, plus the
        /// Behaviour-style <c>enabled</c> flag; both are read defensively because the field moved
        /// between package versions.
        /// </summary>
        private static object GetActive(object component)
        {
            object enabled = CucliOptional.GetField(component, "m_Enabled")
                ?? CucliOptional.GetProperty(component, "enabled");
            if (enabled is bool) return (bool)enabled;

            object active = CucliOptional.GetField(component, "active")
                ?? CucliOptional.GetProperty(component, "active");
            return active is bool ? active : (object)true;
        }

        // ---------------------------------------------------------------- volume_add_effect

        internal static CucliCommandResult AddEffect(CucliCommandContext context)
        {
            CucliCommandResult missing = RequireVolumeComponentSystem();
            if (missing != null) return missing;

            Component volume = GraphicsSupport.ResolveVolume(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            object profile = GraphicsSupport.VolumeProfileOf(volume);
            if (profile == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "The volume '" + volume.gameObject.name + "' has no VolumeProfile. Assign one first, or create it "
                    + "with 'cucli graphics volume_create_profile' and set profilePath.",
                    new { volume = volume.gameObject.name });
            }

            string requested = context.Input.GetString("effect");
            string error;
            Type effectType = GraphicsSupport.FindEffectType(requested, out error);
            if (effectType == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound,
                    error ?? ("Effect type '" + requested + "' not found."),
                    new { effect = requested });
            }

            object component;
            string addError;
            if (!TryAddEffect(profile, effectType, out component, out addError))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The effect '" + effectType.Name + "' could not be added: " + addError);
            }

            EditorUtility.SetDirty(profile as UnityEngine.Object);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                effect = effectType.Name,
                fullName = effectType.FullName,
                volumeInstanceID = volume.gameObject.GetInstanceID()
            }, "Added " + effectType.Name + " to " + volume.gameObject.name + ".");
        }

        private static bool TryAddEffect(object profile, Type effectType, out object component, out string error)
        {
            component = null;
            error = null;
            if (profile == null)
            {
                error = "the profile is null";
                return false;
            }

            // Prefer VolumeProfile.Add<T>() so the profile's internal list stays consistent.
            foreach (MethodInfoShim candidate in GenericMethods(profile.GetType(), "Add"))
            {
                try
                {
                    component = candidate.Method.MakeGenericMethod(effectType).Invoke(profile, null);
                    if (component != null) return true;
                }
                catch (Exception exception)
                {
                    error = (exception.InnerException ?? exception).GetBaseException().Message;
                }
            }

            component = GraphicsSupport.CreateComponentInstance(effectType);
            if (component == null)
            {
                error = error ?? "a " + effectType.Name + " instance could not be created";
                return false;
            }

            // Fall back to the serialized list when the generic API was unavailable.
            var serialized = new SerializedObject(profile as UnityEngine.Object);
            SerializedProperty components = serialized.FindProperty("components");
            if (components == null)
            {
                error = error ?? "the profile has neither Add<T>() nor a 'components' list";
                return false;
            }

            int index = components.arraySize;
            components.InsertArrayElementAtIndex(index);
            components.GetArrayElementAtIndex(index).objectReferenceValue = component as UnityEngine.Object;
            serialized.ApplyModifiedProperties();
            return true;
        }

        private struct MethodInfoShim
        {
            internal System.Reflection.MethodInfo Method;
        }

        private static IEnumerable<MethodInfoShim> GenericMethods(Type type, string name)
        {
            foreach (System.Reflection.MethodInfo method in type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                if (!method.IsGenericMethodDefinition) continue;
                if (method.GetParameters().Length != 0) continue;
                yield return new MethodInfoShim { Method = method };
            }
        }

        // ---------------------------------------------------------------- volume_set_effect

        internal static CucliCommandResult SetEffect(CucliCommandContext context)
        {
            CucliCommandResult missing = RequireVolumeComponentSystem();
            if (missing != null) return missing;

            Component volume = GraphicsSupport.ResolveVolume(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            object profile = GraphicsSupport.VolumeProfileOf(volume);
            string requested = context.Input.GetString("effect");
            string error;
            Type effectType = GraphicsSupport.FindEffectType(requested, out error);
            if (effectType == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound,
                    error ?? ("Effect type '" + requested + "' not found."),
                    new { effect = requested });
            }

            object component = FindEffect(profile, effectType);
            if (component == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "The volume '" + volume.gameObject.name + "' has no " + effectType.Name
                    + " effect. Add it first with 'cucli graphics volume_add_effect'.",
                    new { effect = effectType.Name, volume = volume.gameObject.name });
            }

            JObject parameters = context.Input.GetObject("parameters");
            if (parameters == null)
            {
                return CucliCommandResult.Missing(
                    "parameters", "Provide a field name to value dictionary, e.g. {\"intensity\": 1.2}.");
            }

            var set = new JArray();
            var failed = new JArray();
            ApplyEffectParameters(component, parameters, set, failed);

            EditorUtility.SetDirty(profile as UnityEngine.Object);
            CucliAssets.SaveAssets();

            if (set.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "No parameter of " + effectType.Name + " matched. Valid names: "
                    + string.Join(", ", ParameterNames(component)) + ".",
                    new { effect = effectType.Name, set, failed });
            }

            if (failed.Count > 0) context.Warn(failed.Count + " parameter(s) were not applied.");
            return CucliCommandResult.Ok(new { effect = effectType.Name, set, failed },
                "Set " + set.Count + " parameter(s) on " + effectType.Name + ".");
        }

        private static void ApplyEffectParameters(object component, JObject parameters, JArray set, JArray failed)
        {
            if (component == null || parameters == null) return;

            List<KeyValuePair<string, object>> available = GraphicsSupport.ComponentParameters(component);
            foreach (JProperty property in parameters.Properties())
            {
                string key = property.Name;
                var match = available.FirstOrDefault(pair =>
                    string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GraphicsSupport.DisplayNameOf(component, pair.Key), key,
                        StringComparison.OrdinalIgnoreCase));

                if (match.Key == null)
                {
                    failed.Add(new JObject
                    {
                        ["key"] = key,
                        ["reason"] = "no parameter with that name; valid names: "
                            + string.Join(", ", ParameterNames(component))
                    });
                    continue;
                }

                string error;
                if (GraphicsSupport.TrySetParameterValue(match.Value, property.Value, out error))
                {
                    set.Add(match.Key);
                }
                else
                {
                    failed.Add(new JObject { ["key"] = key, ["reason"] = error });
                }
            }
        }

        private static IEnumerable<string> ParameterNames(object component)
        {
            foreach (KeyValuePair<string, object> pair in GraphicsSupport.ComponentParameters(component))
            {
                yield return GraphicsSupport.DisplayNameOf(component, pair.Key);
            }
        }

        // ---------------------------------------------------------------- volume_remove_effect

        internal static CucliCommandResult RemoveEffect(CucliCommandContext context)
        {
            CucliCommandResult missing = RequireVolumeComponentSystem();
            if (missing != null) return missing;

            Component volume = GraphicsSupport.ResolveVolume(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            object profile = GraphicsSupport.VolumeProfileOf(volume);
            string requested = context.Input.GetString("effect");
            string error;
            Type effectType = GraphicsSupport.FindEffectType(requested, out error);
            if (effectType == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound,
                    error ?? ("Effect type '" + requested + "' not found."),
                    new { effect = requested });
            }

            object component = FindEffect(profile, effectType);
            if (component == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    volume.gameObject.name + " has no " + effectType.Name + " effect to remove.",
                    new { effect = effectType.Name });
            }

            bool removed = false;
            string removeError = null;

            foreach (MethodInfoShim candidate in GenericMethods(profile.GetType(), "Remove"))
            {
                try
                {
                    candidate.Method.MakeGenericMethod(effectType).Invoke(profile, null);
                    removed = true;
                    break;
                }
                catch (Exception exception)
                {
                    removeError = (exception.InnerException ?? exception).GetBaseException().Message;
                }
            }

            if (!removed)
            {
                // VolumeProfile also removes a component when the component object itself is destroyed.
                var componentObject = component as UnityEngine.Object;
                if (componentObject == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "The " + effectType.Name + " effect could not be removed: " + (removeError ?? "no API matched"));
                }

                Undo.DestroyObjectImmediate(componentObject);
                removed = true;
            }

            EditorUtility.SetDirty(profile as UnityEngine.Object);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(
                new { effect = effectType.Name, volumeInstanceID = volume.gameObject.GetInstanceID() },
                "Removed " + effectType.Name + " from " + volume.gameObject.name + ".");
        }

        private static object FindEffect(object profile, Type effectType)
        {
            foreach (object component in GraphicsSupport.ProfileComponents(profile))
            {
                if (effectType.IsInstanceOfType(component)) return component;
                if (string.Equals(component.GetType().Name, effectType.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return component;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- volume_set_properties

        internal static CucliCommandResult SetProperties(CucliCommandContext context)
        {
            CucliCommandResult missing = RequireVolumeSystem();
            if (missing != null) return missing;

            Component volume = GraphicsSupport.ResolveVolume(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            CucliArgs args = context.Input;

            // manage_graphics copies 'properties' onto the envelope first; only four keys are read.
            var changed = new JArray();
            Undo.RecordObject(volume, "cucli graphics volume_set_properties");

            if (args.Has("weight") || HasProperty(args, "weight"))
            {
                float weight = ReadNumber(args, "weight", 1f);
                if (CucliOptional.SetProperty(volume, "weight", weight)) changed.Add("weight");
            }

            if (args.Has("priority") || HasProperty(args, "priority"))
            {
                float priority = ReadNumber(args, "priority", 0f);
                if (CucliOptional.SetProperty(volume, "priority", priority)) changed.Add("priority");
            }

            if (args.Has("isGlobal") || args.Has("is_global") || HasProperty(args, "isGlobal"))
            {
                bool isGlobal = ReadBool(args, "isGlobal", "is_global", true);
                if (CucliOptional.SetProperty(volume, "isGlobal", isGlobal)) changed.Add("isGlobal");
            }

            if (args.Has("blendDistance") || args.Has("blend_distance") || HasProperty(args, "blendDistance"))
            {
                float blend = ReadNumber(args, "blendDistance", 0f, "blend_distance");
                if (CucliOptional.SetProperty(volume, "blendDistance", blend)) changed.Add("blendDistance");
            }

            if (changed.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "No properties specified. Use: weight, priority, is_global, blend_distance.",
                    new { volume = volume.gameObject.name });
            }

            EditorUtility.SetDirty(volume);
            UiCommand.MarkSceneDirty(volume.gameObject);

            return CucliCommandResult.Ok(new { changed }, "Changed " + changed.Count + " volume property(ies).");
        }

        private static bool HasProperty(CucliArgs args, string name)
        {
            JObject properties = args.GetObject("properties");
            return properties != null && properties[name] != null;
        }

        private static float ReadNumber(CucliArgs args, string name, float fallback, string alternate = null)
        {
            float direct = args.GetFloat(name, float.NaN);
            if (!float.IsNaN(direct)) return direct;

            if (alternate != null)
            {
                float aliased = args.GetFloat(alternate, float.NaN);
                if (!float.IsNaN(aliased)) return aliased;
            }

            JObject properties = args.GetObject("properties");
            if (properties != null)
            {
                var nested = new CucliArgs(properties);
                float fromProperties = nested.GetFloat(name, float.NaN);
                if (!float.IsNaN(fromProperties)) return fromProperties;
            }

            return fallback;
        }

        private static bool ReadBool(CucliArgs args, string name, string alternate, bool fallback)
        {
            if (args.Has(name)) return args.GetBool(name, fallback);
            if (alternate != null && args.Has(alternate)) return args.GetBool(alternate, fallback);

            JObject properties = args.GetObject("properties");
            if (properties != null)
            {
                var nested = new CucliArgs(properties);
                if (nested.Has(name)) return nested.GetBool(name, fallback);
                if (alternate != null && nested.Has(alternate)) return nested.GetBool(alternate, fallback);
            }

            return fallback;
        }

        // ---------------------------------------------------------------- volume_list_effects

        internal static CucliCommandResult ListEffects(CucliCommandContext context)
        {
            CucliCommandResult missing = RequireVolumeComponentSystem();
            if (missing != null) return missing;

            Type baseType = GraphicsSupport.VolumeComponentType();
            var effects = new List<JObject>();
            foreach (Type type in GraphicsSupport.EffectTypes(baseType))
            {
                effects.Add(new JObject
                {
                    ["name"] = type.Name,
                    ["fullName"] = type.FullName,
                    ["ns"] = type.Namespace
                });
            }

            effects.Sort((left, right) => string.Compare(
                left.Value<string>("name"), right.Value<string>("name"), StringComparison.Ordinal));

            CucliPage<JObject> page = CucliPaging.Slice(effects, context.Input, 100);
            var items = new JArray();
            foreach (JObject entry in page.Items) items.Add(entry);

            UnityEngine.Rendering.RenderPipelineAsset pipeline = GraphicsSupport.CurrentPipeline();
            return CucliCommandResult.Ok(CucliPaging.Envelope(page, items, new JObject
            {
                ["pipeline"] = pipeline == null ? "Built-in Render Pipeline" : pipeline.GetType().Name,
                ["pipelinePath"] = pipeline == null
                    ? null
                    : GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(pipeline))
            }));
        }

        internal static List<string> ListEffectTypeNames()
        {
            var names = new List<string>();
            Type baseType = GraphicsSupport.VolumeComponentType();
            if (baseType == null) return names;

            foreach (Type type in GraphicsSupport.EffectTypes(baseType)) names.Add(type.Name);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
