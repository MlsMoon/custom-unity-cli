using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>feature_*</c> actions of the <c>graphics</c> group: the Renderer Feature list of the
    /// active URP renderer.
    ///
    /// URP is never referenced at compile time. The active pipeline is recognised by its type name
    /// (it must contain <c>Universal</c> or <c>URP</c>) and every type is resolved from
    /// <c>Unity.RenderPipelines.Universal.Runtime</c> through <see cref="CucliOptional"/>. A
    /// non-URP pipeline produces an explicit error rather than a silent no-op.
    /// </summary>
    internal static class GraphicsFeatureCommand
    {
        private const string UniversalAssembly = "Unity.RenderPipelines.Universal.Runtime";

        private static CucliCommandResult RequireUrpForFeatures()
        {
            return GraphicsSupport.RequireUrp("Renderer features");
        }

        private static Type RendererDataType()
        {
            return CucliOptional.FindType(UniversalAssembly, "UnityEngine.Rendering.Universal.ScriptableRendererData");
        }

        private static Type RendererFeatureType()
        {
            return CucliOptional.FindType(
                UniversalAssembly, "UnityEngine.Rendering.Universal.ScriptableRendererFeature");
        }

        // ---------------------------------------------------------------- renderer resolution

        /// <summary>Resolves one entry of <c>m_RendererDataList</c> on the active pipeline asset.</summary>
        private static UnityEngine.Object ResolveRendererData(
            CucliCommandContext context, out CucliCommandResult failure)
        {
            failure = null;

            if (GraphicsSupport.CurrentPipeline() == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "No render pipeline asset is active, so there is no URP renderer to read.");
                return null;
            }

            if (RendererDataType() == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound,
                    "Could not find URP ScriptableRendererData. Ensure URP is installed and active.",
                    new { assembly = UniversalAssembly });
                return null;
            }

            var serialized = new SerializedObject(GraphicsSupport.CurrentPipeline());
            SerializedProperty list = serialized.FindProperty("m_RendererDataList");
            if (list == null || list.arraySize == 0)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "The active pipeline asset has no m_RendererDataList; this is not a URP asset.",
                    new { pipeline = GraphicsSupport.ActivePipelineTypeName() });
                return null;
            }

            CucliArgs args = context.Input;
            int index = args.GetInt("rendererIndex", args.GetInt("renderer_index", -1));
            if (index < 0)
            {
                SerializedProperty defaultIndex = serialized.FindProperty("m_DefaultRendererIndex");
                index = defaultIndex == null ? 0 : defaultIndex.intValue;
            }

            if (index < 0 || index >= list.arraySize)
            {
                failure = CucliCommandResult.Invalid(
                    "rendererIndex",
                    index + " is out of range; the pipeline asset has " + list.arraySize + " renderer(s).");
                return null;
            }

            UnityEngine.Object data = list.GetArrayElementAtIndex(index).objectReferenceValue;
            if (data == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "Renderer entry " + index + " is empty in m_RendererDataList.");
                return null;
            }

            return data;
        }

        private static List<UnityEngine.Object> FeatureList(UnityEngine.Object rendererData)
        {
            var features = new List<UnityEngine.Object>();
            if (rendererData == null) return features;

            var serialized = new SerializedObject(rendererData);
            SerializedProperty list = serialized.FindProperty("m_RendererFeatures");
            if (list == null) return features;

            for (int index = 0; index < list.arraySize; index++)
            {
                UnityEngine.Object feature = list.GetArrayElementAtIndex(index).objectReferenceValue;
                if (feature != null) features.Add(feature);
            }

            return features;
        }

        private static bool TryFindFeature(
            List<UnityEngine.Object> features, string name, out int index, out string error)
        {
            index = -1;
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "a feature 'name' is required.";
                return false;
            }

            string requested = name.Trim();
            for (int candidate = 0; candidate < features.Count; candidate++)
            {
                if (!string.Equals(features[candidate].name, requested, StringComparison.OrdinalIgnoreCase)) continue;
                index = candidate;
                return true;
            }

            for (int candidate = 0; candidate < features.Count; candidate++)
            {
                string typeName = features[candidate].GetType().Name;
                if (!string.Equals(typeName, requested, StringComparison.OrdinalIgnoreCase)) continue;
                index = candidate;
                return true;
            }

            error = "no renderer feature is named or typed '" + requested + "'. Available: "
                + string.Join(", ", features.Select(feature => feature.name)) + ".";
            return false;
        }

        // ---------------------------------------------------------------- feature_list

        internal static CucliCommandResult List(CucliCommandContext context)
        {
            CucliCommandResult urp = RequireUrpForFeatures();
            if (urp != null) return urp;

            UnityEngine.Object rendererData = ResolveRendererData(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            List<UnityEngine.Object> features = FeatureList(rendererData);
            var options = CucliDescribe.OptionsFrom(context.Input);
            options.MaxDepth = Mathf.Min(options.MaxDepth, 2);
            options.MaxItems = Mathf.Min(options.MaxItems, 32);

            var entries = new JArray();
            for (int index = 0; index < features.Count; index++)
            {
                context.ThrowIfCancelled();
                UnityEngine.Object feature = features[index];
                entries.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = feature.name,
                    ["type"] = feature.GetType().Name,
                    ["fullType"] = feature.GetType().FullName,
                    ["isActive"] = ReadActive(feature),
                    ["assetPath"] = GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(feature)),
                    ["properties"] = SerializedSummary(feature, options)
                });
            }

            return CucliCommandResult.Ok(new
            {
                rendererDataName = rendererData.name,
                rendererDataPath = GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(rendererData)),
                rendererDataType = rendererData.GetType().Name,
                featureCount = features.Count,
                features = entries
            });
        }

        private static bool? ReadActive(UnityEngine.Object feature)
        {
            object active = CucliOptional.GetProperty(feature, "isActive");
            if (active is bool) return (bool)active;

            var serialized = new SerializedObject(feature);
            SerializedProperty property = serialized.FindProperty("m_Active");
            if (property == null) return null;
            return property.propertyType == SerializedPropertyType.Boolean ? property.boolValue : (bool?)null;
        }

        private static JObject SerializedSummary(UnityEngine.Object target, CucliDescribe.Options options)
        {
            var result = new JObject();
            if (target == null) return result;

            var serialized = new SerializedObject(target);
            SerializedProperty iterator = serialized.GetIterator();
            bool enterChildren = true;
            int count = 0;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.depth > 0) continue;
                if (iterator.name == "m_Script") continue;
                if (count++ >= options.MaxItems) break;
                result[iterator.name] = CucliSerialized.Read(iterator, options, 0);
            }

            return result;
        }

        // ---------------------------------------------------------------- feature_add

        internal static CucliCommandResult Add(CucliCommandContext context)
        {
            CucliCommandResult urp = RequireUrpForFeatures();
            if (urp != null) return urp;

            UnityEngine.Object rendererData = ResolveRendererData(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            string requested = context.Input.GetString("type");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing(
                    "type", "A ScriptableRendererFeature type name, e.g. ScreenSpaceAmbientOcclusion.");
            }

            Type featureType = ResolveFeatureType(requested.Trim());
            if (featureType == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound,
                    "'" + requested + "' is not a ScriptableRendererFeature type known to the loaded assemblies. "
                    + "Check the type name, or install the package that declares it.",
                    new { type = requested });
            }

            object created;
            try
            {
                created = ScriptableObject.CreateInstance(featureType);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The feature instance could not be created: " + exception.GetBaseException().Message);
            }

            var feature = created as UnityEngine.Object;
            if (feature == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "ScriptableObject.CreateInstance returned null for " + requested + ".");
            }

            string name = ResolveFeatureName(context.Input.GetString("name"), featureType.Name);
            feature.name = name;

            string assetPath = CucliAssets.GetAssetPath(rendererData);
            if (string.IsNullOrEmpty(assetPath))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "The renderer data asset has no path, so the feature cannot be stored inside it.");
            }

            AssetDatabase.AddObjectToAsset(feature, assetPath);

            var serialized = new SerializedObject(rendererData);
            SerializedProperty list = serialized.FindProperty("m_RendererFeatures");
            if (list == null)
            {
                AssetDatabase.RemoveObjectFromAsset(feature);
                UnityEngine.Object.DestroyImmediate(feature, true);
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The renderer data has no m_RendererFeatures list; this is not a URP renderer.");
            }

            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // URP keeps a parallel index map; keep it in step when this version has one.
            SerializedProperty map = serialized.FindProperty("m_RendererFeatureMap");
            if (map != null && map.isArray)
            {
                map.InsertArrayElementAtIndex(map.arraySize);
                SerializedProperty entry = map.GetArrayElementAtIndex(map.arraySize - 1);
                if (entry.propertyType == SerializedPropertyType.Integer) entry.longValue = list.arraySize - 1;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            var changed = new JArray();
            var failed = new JArray();
            JObject properties = context.Input.GetObject("properties");
            if (properties != null) ApplyProperties(feature, properties, changed, failed);

            string materialPath = context.Input.GetString("material");
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                Material material = CucliAssets.Load<Material>(materialPath);
                if (material == null)
                {
                    failed.Add(new JObject { ["key"] = "material", ["reason"] = "no material is at " + materialPath });
                }
                else if (SetMaterial(feature, material, out string materialError))
                {
                    changed.Add("material");
                }
                else
                {
                    failed.Add(new JObject { ["key"] = "material", ["reason"] = materialError });
                }
            }

            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(feature);
            CucliAssets.SaveAssets();

            var data = new JObject
            {
                ["name"] = feature.name,
                ["type"] = featureType.FullName,
                ["instanceId"] = feature.GetInstanceID(),
                ["rendererData"] = rendererData.name,
                ["changed"] = changed
            };

            if (failed.Count > 0)
            {
                data["failed"] = failed;
                context.Warn(failed.Count + " feature property(ies) were not applied.");
            }

            return CucliCommandResult.Ok(data, "Added " + feature.name + " to " + rendererData.name + ".");
        }

        private static string ResolveFeatureName(string requested, string fallback)
        {
            return string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        }

        /// <summary>Exact match first, then a suffix match on the short or full type name.</summary>
        private static Type ResolveFeatureType(string requested)
        {
            Type baseType = RendererFeatureType();
            if (baseType == null)
            {
                baseType = CucliReflection.FindType("ScriptableRendererFeature", out string ignored);
                if (baseType == null) return null;
            }

            Type prefixMatch = null;
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

                    if (string.Equals(type.Name, requested, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type.FullName, requested, StringComparison.OrdinalIgnoreCase))
                    {
                        return type;
                    }

                    if (prefixMatch == null
                        && type.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
                    {
                        prefixMatch = type;
                    }
                }
            }

            return prefixMatch;
        }

        private static bool SetMaterial(UnityEngine.Object feature, Material material, out string error)
        {
            error = null;
            var serialized = new SerializedObject(feature);
            foreach (string name in new[] { "m_PassMaterial", "passMaterial", "m_Material", "m_Settings" })
            {
                SerializedProperty property = serialized.FindProperty(name);
                if (property == null) continue;
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;

                property.objectReferenceValue = material;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(feature);
                return true;
            }

            error = "the feature has no material property (looked for m_PassMaterial, passMaterial, m_Material)";
            return false;
        }

        // ---------------------------------------------------------------- feature_remove

        internal static CucliCommandResult Remove(CucliCommandContext context)
        {
            CucliCommandResult urp = RequireUrpForFeatures();
            if (urp != null) return urp;

            UnityEngine.Object rendererData = ResolveRendererData(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            if (!TryResolveFeatureIndex(context, rendererData, out int index, out string error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            var serialized = new SerializedObject(rendererData);
            SerializedProperty list = serialized.FindProperty("m_RendererFeatures");
            UnityEngine.Object feature = list.GetArrayElementAtIndex(index).objectReferenceValue;

            list.DeleteArrayElementAtIndex(index);
            if (index < list.arraySize && list.GetArrayElementAtIndex(index).objectReferenceValue == feature)
            {
                // Unity only nulls the slot on the first delete for object references.
                list.DeleteArrayElementAtIndex(index);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rendererData);

            if (feature != null)
            {
                AssetDatabase.RemoveObjectFromAsset(feature);
                UnityEngine.Object.DestroyImmediate(feature, true);
            }

            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                removed = true,
                index,
                feature = feature == null ? null : feature.name
            }, "Removed renderer feature " + index + " from " + rendererData.name + ".");
        }

        private static bool TryResolveFeatureIndex(
            CucliCommandContext context, UnityEngine.Object rendererData, out int index, out string error)
        {
            error = null;
            List<UnityEngine.Object> features = FeatureList(rendererData);
            CucliArgs args = context.Input;

            if (args.Has("index"))
            {
                index = args.GetInt("index", -1);
                if (index < 0 || index >= features.Count)
                {
                    error = "'index' " + index + " is out of range; the renderer has " + features.Count + " feature(s).";
                    return false;
                }

                return true;
            }

            string name = args.GetString("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                // An out parameter must be assigned on every path, including this early exit.
                index = -1;
                error = "provide 'index' or 'name' to identify the renderer feature.";
                return false;
            }

            return TryFindFeature(features, name, out index, out error);
        }

        // ---------------------------------------------------------------- feature_configure

        internal static CucliCommandResult Configure(CucliCommandContext context)
        {
            CucliCommandResult urp = RequireUrpForFeatures();
            if (urp != null) return urp;

            UnityEngine.Object rendererData = ResolveRendererData(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            if (!TryResolveFeatureIndex(context, rendererData, out int index, out string error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            UnityEngine.Object feature = FeatureList(rendererData)[index];
            JObject properties = context.Input.GetObject("properties") ?? context.Input.GetObject("settings");
            if (properties == null || properties.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "properties", "Provide the serialized property dictionary to write.");
            }

            var changed = new JArray();
            var failed = new JArray();
            ApplyProperties(feature, properties, changed, failed);
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(rendererData);
            CucliAssets.SaveAssets();

            if (changed.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "No property of " + feature.GetType().Name + " was applied. Run "
                    + "'cucli graphics feature_list' to see the available property names.",
                    new { changed, failed });
            }

            if (failed.Count > 0) context.Warn(failed.Count + " feature property(ies) were not applied.");
            return CucliCommandResult.Ok(new { changed, failed },
                "Applied " + changed.Count + " property(ies) to " + feature.name + ".");
        }

        private static void ApplyProperties(
            UnityEngine.Object feature, JObject properties, JArray changed, JArray failed)
        {
            foreach (JProperty property in properties.Properties())
            {
                string error;
                bool handled;
                if (CucliSerialized.TrySet(
                        feature, property.Name, property.Value,
                        "cucli graphics feature_" + feature.GetType().Name, out error, out handled))
                {
                    changed.Add(property.Name);
                    continue;
                }

                if (handled)
                {
                    failed.Add(new JObject { ["key"] = property.Name, ["reason"] = error });
                    continue;
                }

                if (CucliReflection.TrySetPath(feature, property.Name, property.Value, out error))
                {
                    changed.Add(property.Name);
                    continue;
                }

                failed.Add(new JObject
                {
                    ["key"] = property.Name,
                    ["reason"] = error ?? "the property was not found on " + feature.GetType().Name
                });
            }
        }

        // ---------------------------------------------------------------- feature_toggle

        internal static CucliCommandResult Toggle(CucliCommandContext context)
        {
            CucliCommandResult urp = RequireUrpForFeatures();
            if (urp != null) return urp;

            UnityEngine.Object rendererData = ResolveRendererData(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            if (!TryResolveFeatureIndex(context, rendererData, out int index, out string error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            UnityEngine.Object feature = FeatureList(rendererData)[index];
            bool active = context.Input.GetBool("active", false);

            var serialized = new SerializedObject(feature);
            SerializedProperty property = serialized.FindProperty("m_Active");
            if (property != null && property.propertyType == SerializedPropertyType.Boolean)
            {
                property.boolValue = active;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else if (!CucliOptional.SetProperty(feature, "isActive", active)
                && !SetViaReflection(feature, "SetActive", active)
                && !SetViaReflection(feature, "set_isActive", active))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The active flag of " + feature.GetType().Name + " is not writable; this feature may not "
                    + "support being disabled.");
            }

            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(rendererData);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                index,
                feature = feature.name,
                active
            }, "Renderer feature " + feature.name + " is now " + (active ? "enabled" : "disabled") + ".");
        }

        private static bool SetViaReflection(UnityEngine.Object target, string method, bool value)
        {
            if (target == null) return false;
            try
            {
                MethodInfo info = target.GetType().GetMethod(
                    method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(bool) },
                    null);
                if (info == null) return false;
                info.Invoke(target, new object[] { value });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- feature_reorder

        internal static CucliCommandResult Reorder(CucliCommandContext context)
        {
            CucliCommandResult urp = RequireUrpForFeatures();
            if (urp != null) return urp;

            UnityEngine.Object rendererData = ResolveRendererData(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            int[] order = context.Input.GetIntList("order");
            List<UnityEngine.Object> features = FeatureList(rendererData);

            if (order.Length == 0)
            {
                return CucliCommandResult.Missing(
                    "order", "Provide a permutation of 0.." + (features.Count - 1) + " with one entry per feature.");
            }

            if (order.Length != features.Count)
            {
                return CucliCommandResult.Invalid(
                    "order",
                    "the order array length (" + order.Length + ") must match the feature count ("
                    + features.Count + ").");
            }

            var seen = new HashSet<int>();
            foreach (int value in order)
            {
                if (value < 0 || value >= features.Count || !seen.Add(value))
                {
                    return CucliCommandResult.Invalid(
                        "order",
                        "the order array must contain every index in 0.." + (features.Count - 1) + " exactly once.");
                }
            }

            var reordered = new List<UnityEngine.Object>(features.Count);
            foreach (int value in order) reordered.Add(features[value]);

            var serialized = new SerializedObject(rendererData);
            SerializedProperty list = serialized.FindProperty("m_RendererFeatures");
            for (int index = 0; index < reordered.Count; index++)
            {
                list.GetArrayElementAtIndex(index).objectReferenceValue = reordered[index];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rendererData);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                order,
                features = reordered.Select(feature => feature.name).ToArray()
            }, "Reordered " + reordered.Count + " renderer feature(s).");
        }
    }
}
