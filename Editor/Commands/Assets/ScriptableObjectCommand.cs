using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli so ...</c> - the ScriptableObject surface: create an instance asset, patch serialized
    /// properties on an existing instance, and validate a patch set without writing anything.
    ///
    /// Alignment target is MCP for Unity's <c>manage_scriptable_object</c>. Writes go through
    /// <see cref="CucliSerialized"/> first so Undo, <c>ApplyModifiedProperties</c> and
    /// <c>SetDirty</c> happen exactly once; reflection is only the fallback for non-serialized members.
    /// </summary>
    internal sealed class ScriptableObjectCommand : CucliCommand
    {
        public override string Name => "so";

        public override string Summary =>
            "Create and patch ScriptableObject assets: create/create_so, modify/modify_so, with dryRun validation.";

        public override CucliActionInfo[] Actions => Declare(
            "create", "Create a ScriptableObject asset from a type name plus optional patches.",
            "create_so", "Alias of create (MCP spelling).",
            "modify", "Patch serialized properties on an existing ScriptableObject asset.",
            "modify_so", "Alias of modify (MCP spelling).");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Edit;

        /// <summary>Only modify can be a pure dry run; the command as a whole writes assets.</summary>
        public override bool MutatesProject => true;

        public override string[] Effects => new[] { "project.assets.read", "project.assets.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""create"", ""create_so"", ""modify"", ""modify_so""],
      ""default"": ""create""
    },
    ""typeName"": { ""type"": ""string"", ""aliases"": [""type_name""], ""description"": ""action=create: ScriptableObject type name or full name."" },
    ""folderPath"": { ""type"": ""string"", ""aliases"": [""folder_path""], ""description"": ""action=create: Assets/ 下的目标目录."" },
    ""assetName"": { ""type"": ""string"", ""aliases"": [""asset_name""], ""description"": ""action=create: 文件名，可省略 .asset 后缀."" },
    ""overwrite"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=create: 已存在时原地覆盖（保留 GUID）."" },
    ""target"": { ""type"": ""object"", ""description"": ""action=modify: {guid|path} 定位目标资产，guid 优先."" },
    ""targetGuid"": { ""type"": ""string"", ""aliases"": [""target_guid"", ""guid""] },
    ""targetPath"": { ""type"": ""string"", ""aliases"": [""target_path""] },
    ""dryRun"": { ""type"": ""boolean"", ""default"": false, ""aliases"": [""dry_run""], ""description"": ""action=modify: 只校验不写盘."" },
    ""patches"": {
      ""type"": ""array"",
      ""description"": ""补丁数组，每项含 propertyPath、op 与 value。"",
      ""items"": { ""type"": ""object"" }
    },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = Normalize(ResolveAction(context, "create"));
            switch (action)
            {
                case "create": return Create(context);
                case "modify": return Modify(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        /// <summary>
        /// MCP normalises the action by trimming and dropping '-' and '_' before comparing, which is
        /// why <c>create_so</c>, <c>create-so</c> and <c>CreateSO</c> all denote the same action.
        /// </summary>
        private static string Normalize(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return "create";
            string value = action.Trim().ToLowerInvariant();
            value = value.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            switch (value)
            {
                case "create": case "createso": case "new": case "newso": return "create";
                case "modify": case "modifyso": case "patch": case "set": case "update": return "modify";
                default: return action.Trim().ToLowerInvariant();
            }
        }

        // ---------------------------------------------------------------- create

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string typeName = args.GetString("typeName") ?? args.GetString("type_name");
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return CucliCommandResult.Missing("typeName", "例如 --type-name MyGameConfig。");
            }

            string assetName = args.GetString("assetName") ?? args.GetString("asset_name");
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return CucliCommandResult.Missing("assetName", "资产文件名不能包含 '/' 或 '\\'。");
            }

            assetName = assetName.Trim();
            if (assetName.IndexOf('/') >= 0 || assetName.IndexOf('\\') >= 0)
            {
                return CucliCommandResult.Invalid("assetName", "文件名不能包含路径分隔符：" + assetName);
            }

            if (!assetName.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) assetName += ".asset";

            string folderArgument = args.GetString("folderPath") ?? args.GetString("folder_path");
            if (string.IsNullOrWhiteSpace(folderArgument)) folderArgument = "Assets";
            if (folderArgument.IndexOf(':') >= 0 || folderArgument.StartsWith("/", StringComparison.Ordinal))
            {
                return CucliCommandResult.Invalid(
                    "folderPath", "只接受 Assets/ 下的工程相对路径，收到 '" + folderArgument + "'。");
            }

            string folder = CucliAssets.NormalizeAssetPath(folderArgument);
            if (folder == null || !folder.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CucliCommandResult.Invalid(
                    "folderPath", "目录必须位于 Assets/ 之下，收到 '" + folderArgument + "'。");
            }

            string error;
            Type type = ResolveScriptableObjectType(typeName.Trim(), out error);
            if (type == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound, error, new { typeName = typeName.Trim() });
            }

            if (!typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound,
                    "类型 '" + type.FullName + "' 不是可实例化的 ScriptableObject。",
                    new { typeName = type.FullName });
            }

            JArray patches = ReadPatches(args, false);
            if (patches == null)
            {
                return CucliCommandResult.Invalid("patches", "patches 必须是数组或 JSON 数组字符串。");
            }

            if (!CucliAssets.EnsureFolder(folder))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "无法创建目录 " + folder + "。", new { folderPath = folder });
            }

            string path = folder + "/" + assetName;
            bool overwrite = args.GetBool("overwrite", false);
            Object existing = AssetDatabase.LoadMainAssetAtPath(path);

            if (existing != null && !overwrite)
            {
                string unique = AssetDatabase.GenerateUniqueAssetPath(path);
                context.Warn("目标已存在，overwrite=false，已改用唯一路径 " + unique + "。");
                path = unique;
                existing = null;
            }

            ScriptableObject instance = existing as ScriptableObject;
            bool reused = instance != null && instance.GetType() == type;

            if (reused)
            {
                Undo.RecordObject(instance, "cucli so create (overwrite)");
            }
            else
            {
                if (existing != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }

                try
                {
                    instance = ScriptableObject.CreateInstance(type);
                }
                catch (Exception exception)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "CreateInstance(" + type.FullName + ") 失败：" + exception.GetBaseException().Message,
                        new { typeName = type.FullName });
                }

                if (instance == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "CreateInstance(" + type.FullName + ") 返回 null。",
                        new { typeName = type.FullName });
                }

                instance.name = CucliAssets.FileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(instance, path);
                instance = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (instance == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "CreateAsset 之后无法加载 " + path + "。", new { path });
                }
            }

            var results = new JArray();
            string createPatchError = null;
            if (patches != null && patches.Count > 0)
            {
                if (!ApplyPatches(context, instance, patches, false, results, out createPatchError))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter, createPatchError, new { path, patchResults = results });
                }
            }

            EditorUtility.SetDirty(instance);
            AssetDatabase.SaveAssets();
            context.AddArtifact(CucliAssets.ToAbsolute(path));

            var data = new JObject
            {
                ["guid"] = CucliAssets.AssetPathToGuid(path),
                ["path"] = path,
                ["typeNameResolved"] = type.FullName,
                ["overwritten"] = reused,
                ["patchResults"] = results.Count == 0 ? null : results,
                ["patchCount"] = results.Count
            };

            return CucliCommandResult.Ok(
                data,
                "已创建 ScriptableObject " + path + "（类型 " + type.FullName + "，补丁 " + results.Count + " 条）。");
        }

        // ---------------------------------------------------------------- modify

        private static CucliCommandResult Modify(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string path;
            string targetError;
            if (!TryResolveTarget(args, out path, out targetError))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    targetError,
                    new { validActions = new[] { "create", "create_so", "modify", "modify_so" } });
            }

            ScriptableObject target = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (target == null)
            {
                Object any = AssetDatabase.LoadMainAssetAtPath(path);
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    any == null
                        ? "找不到资产：" + path + "。"
                        : "资产 " + path + " 的主对象是 " + any.GetType().FullName + "，不是 ScriptableObject。",
                    new { path });
            }

            JArray patches = ReadPatches(args, true);
            if (patches == null)
            {
                return CucliCommandResult.Invalid("patches", "modify 的 patches 必须是数组或 JSON 数组字符串。");
            }

            bool dryRun = args.GetBool("dryRun", args.GetBool("dry_run", false));

            var results = new JArray();
            bool allOk = ApplyPatches(context, target, patches, dryRun, results, out string patchError);

            var data = new JObject
            {
                ["targetGuid"] = CucliAssets.AssetPathToGuid(path),
                ["targetPath"] = path,
                ["targetTypeName"] = target.GetType().FullName,
                ["patchCount"] = patches.Count,
                ["results"] = results
            };

            if (dryRun)
            {
                data["dryRun"] = true;
                data["valid"] = allOk;
                data["validationResults"] = results;
                return CucliCommandResult.Ok(
                    data,
                    allOk
                        ? "dryRun 校验通过：" + patches.Count + " 条补丁均可写入（未写盘）。"
                        : "dryRun 校验未通过：" + results.Count + " 条补丁中有失败项（未写盘）。");
            }

            if (!allOk)
            {
                EditorUtility.SetDirty(target);
                AssetDatabase.SaveAssets();
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    patchError ?? "部分补丁写入失败。",
                    data);
            }

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();

            return CucliCommandResult.Ok(data, "已写入 " + patches.Count + " 条补丁到 " + path + "。");
        }

        /// <summary>
        /// Resolves a ScriptableObject type, preferring the regular name lookup and falling back to
        /// <see cref="TypeCache"/> so short names stay unambiguous when several assemblies define them.
        /// </summary>
        private static Type ResolveScriptableObjectType(string typeName, out string error)
        {
            error = null;

            Type type = CucliReflection.FindType(typeName, out error);
            if (type != null) return type;

            if (typeName.IndexOf('.') < 0)
            {
                Type best = null;
                int matches = 0;
                foreach (Type candidate in TypeCache.GetTypesDerivedFrom<ScriptableObject>())
                {
                    if (candidate == null || candidate.IsAbstract) continue;
                    if (!string.Equals(candidate.Name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
                    best = candidate;
                    matches++;
                }

                if (matches == 1)
                {
                    error = null;
                    return best;
                }
            }

            if (string.IsNullOrWhiteSpace(error))
            {
                error = "找不到类型 '" + typeName + "'。";
            }

            return null;
        }

        /// <summary>Resolves <c>target.guid</c> first, then <c>target.path</c>, then the flat aliases.</summary>
        private static bool TryResolveTarget(CucliArgs args, out string path, out string error)
        {
            path = null;
            error = null;

            string guid = null;
            string rawPath = null;

            JObject target = args.GetObject("target");
            if (target != null)
            {
                var targetArgs = new CucliArgs(target);
                guid = targetArgs.GetString("guid");
                rawPath = targetArgs.GetString("path");
            }
            else if (args.Token("target") != null && args.Token("target").Type == JTokenType.String)
            {
                rawPath = args.GetString("target");
            }

            if (string.IsNullOrWhiteSpace(guid)) guid = args.GetString("targetGuid") ?? args.GetString("target_guid");
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                rawPath = args.GetString("targetPath") ?? args.GetString("target_path");
            }

            if (string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(rawPath))
            {
                error = "modify 需要 target：{guid} 或 {path} 至少给一个（guid 优先）。";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(guid))
            {
                path = CucliAssets.GuidToAssetPath(guid.Trim());
                if (string.IsNullOrEmpty(path))
                {
                    error = "GUID '" + guid + "' 无法解析成资产路径。";
                    path = null;
                    return false;
                }

                return true;
            }

            path = CucliAssets.NormalizeAssetPath(rawPath);
            if (path == null)
            {
                error = "路径 '" + rawPath + "' 无法归一化成 Assets/ 下的资产路径。";
                return false;
            }

            return true;
        }

        private static JArray ReadPatches(CucliArgs args, bool required)
        {
            JToken token = args.Token("patches");
            if (token == null || token.Type == JTokenType.Null)
            {
                if (required) return null;
                return new JArray();
            }

            if (token.Type == JTokenType.Array) return (JArray)token;

            if (token.Type == JTokenType.String)
            {
                try
                {
                    JToken parsed = JToken.Parse(token.Value<string>() ?? string.Empty);
                    return parsed as JArray;
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Applies every patch. In dry-run mode nothing is written: each patch is validated against the
        /// serialized representation only, which is exactly what the caller needs before committing.
        /// </summary>
        private static bool ApplyPatches(
            CucliCommandContext context,
            ScriptableObject target,
            JArray patches,
            bool dryRun,
            JArray results,
            out string error)
        {
            error = null;
            bool allOk = true;

            // A dry run must not observe its own earlier writes, so it works on a throwaway clone.
            ScriptableObject working = target;
            if (dryRun)
            {
                working = Object.Instantiate(target);
                working.hideFlags = HideFlags.HideAndDontSave;
            }

            try
            {
                for (int index = 0; index < patches.Count; index++)
                {
                    context.ThrowIfCancelled();

                    var patch = patches[index] as JObject;
                    if (patch == null)
                    {
                        allOk = false;
                        results.Add(new JObject
                        {
                            ["index"] = index,
                            ["propertyPath"] = null,
                            ["op"] = "set",
                            ["ok"] = false,
                            ["message"] = "补丁必须是对象。"
                        });
                        continue;
                    }

                    var entry = new JObject { ["index"] = index };
                    bool ok = ApplySinglePatch(working, patch, dryRun, entry);
                    if (!ok)
                    {
                        allOk = false;
                        if (error == null) error = "补丁 " + index + " 失败：" + entry["message"];
                    }

                    results.Add(entry);
                }
            }
            finally
            {
                if (dryRun && working != null) Object.DestroyImmediate(working);
            }

            return allOk;
        }

        private static bool ApplySinglePatch(
            ScriptableObject target,
            JObject patch,
            bool dryRun,
            JObject entry)
        {
            var args = new CucliArgs(patch);

            string propertyPath = args.GetString("propertyPath") ?? args.GetString("property_path")
                ?? args.GetString("path");
            string op = (args.GetString("op") ?? "set").Trim().ToLowerInvariant();
            if (!string.Equals(op, "array_resize", StringComparison.Ordinal)) op = "set";

            entry["propertyPath"] = propertyPath;
            entry["op"] = op;

            if (string.IsNullOrWhiteSpace(propertyPath))
            {
                entry["ok"] = false;
                entry["message"] = "补丁缺少 propertyPath（或 property_path / path）。";
                return false;
            }

            string normalized = NormalizeArrayPath(propertyPath.Trim());

            SerializedObject serialized = null;
            SerializedProperty property = null;
            try
            {
                serialized = new SerializedObject(target);
                property = serialized.FindProperty(normalized);
            }
            catch (Exception exception)
            {
                entry["ok"] = false;
                entry["message"] = exception.GetBaseException().Message;
                return false;
            }

            if (property == null)
            {
                entry["ok"] = false;
                entry["message"] = "找不到序列化属性 '" + propertyPath + "'（归一化后 '" + normalized + "'）。";
                return false;
            }

            entry["resolvedPropertyType"] = property.propertyType.ToString();
            entry["isArray"] = property.isArray;

            if (string.Equals(op, "array_resize", StringComparison.Ordinal))
            {
                JToken sizeToken = patch["value"];
                if (sizeToken == null || sizeToken.Type != JTokenType.Integer)
                {
                    entry["ok"] = false;
                    entry["message"] = "array_resize 需要整数 'value'。";
                    return false;
                }

                int size = sizeToken.Value<int>();
                if (size < 0)
                {
                    entry["ok"] = false;
                    entry["message"] = "array_resize 的 'value' 不能为负数。";
                    return false;
                }

                if (!property.isArray)
                {
                    entry["ok"] = false;
                    entry["message"] = "属性 '" + propertyPath + "' 不是数组。";
                    return false;
                }

                entry["currentSize"] = property.arraySize;
                entry["elementType"] = property.arrayElementType;
                if (!dryRun)
                {
                    property.arraySize = size;
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target);
                }

                entry["ok"] = true;
                entry["message"] = "数组大小将设为 " + size + "。";
                return true;
            }

            // ObjectReference patches may carry 'ref' instead of 'value'.
            JToken value = patch["value"];
            JToken reference = patch["ref"];
            if (reference != null && reference.Type != JTokenType.Null && property.propertyType == SerializedPropertyType.ObjectReference)
            {
                value = reference;
            }

            if (value == null && reference == null)
            {
                entry["ok"] = false;
                entry["message"] = "补丁缺少 'value'（ObjectReference 可用 'ref'）。";
                return false;
            }

            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                JToken referenceToken = reference ?? value;
                string referenceError;
                Object resolved;
                if (!TryResolveObjectReference(referenceToken, out resolved, out referenceError))
                {
                    entry["ok"] = false;
                    entry["message"] = referenceError;
                    return false;
                }

                if (!dryRun)
                {
                    Undo.RecordObject(target, "cucli so " + propertyPath);
                    property.objectReferenceValue = resolved;
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target);
                }

                entry["ok"] = true;
                entry["message"] = resolved == null ? "引用将置空。" : "引用将指向 " + resolved.name + "。";
                return true;
            }

            if (property.propertyType == SerializedPropertyType.Quaternion && value != null && value.Type == JTokenType.Object)
            {
                var quaternionObject = (JObject)value;
                Quaternion quaternion;
                if (quaternionObject["euler"] != null)
                {
                    Vector3 euler = new CucliArgs(quaternionObject).GetVector3("euler");
                    quaternion = Quaternion.Euler(euler);
                }
                else if (quaternionObject["x"] != null)
                {
                    var quaternionArgs = new CucliArgs(quaternionObject);
                    quaternion = new Quaternion(
                        quaternionArgs.GetFloat("x"), quaternionArgs.GetFloat("y"),
                        quaternionArgs.GetFloat("z"), quaternionArgs.GetFloat("w", 1f));
                }
                else
                {
                    entry["ok"] = false;
                    entry["message"] = "Quaternion 需要 {euler:[x,y,z]} 或 {x,y,z,w}。";
                    return false;
                }

                if (!dryRun)
                {
                    Undo.RecordObject(target, "cucli so " + propertyPath);
                    property.quaternionValue = quaternion;
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target);
                }

                entry["ok"] = true;
                entry["message"] = "四元数将写入。";
                return true;
            }

            string assignError;
            bool assigned;
            try
            {
                assigned = CucliSerialized.Assign(property, value, out assignError);
            }
            catch (Exception exception)
            {
                assigned = false;
                assignError = exception.GetBaseException().Message;
            }

            if (!assigned)
            {
                // Reflection only proves the member exists; the getter/setter may still reject the
                // value, so a dry run reports it as unverified instead of touching the object.
                string reflectionError;
                bool exists;
                try
                {
                    exists = CucliReflection.TrySetPath(target, propertyPath.Trim(), value, out reflectionError);
                }
                catch (Exception exception)
                {
                    exists = false;
                    reflectionError = exception.GetBaseException().Message;
                }

                if (!exists)
                {
                    entry["ok"] = false;
                    entry["message"] = assignError ?? reflectionError ?? "无法写入该属性。";
                    return false;
                }

                if (dryRun)
                {
                    entry["ok"] = false;
                    entry["message"] = "dryRun：该成员不是序列化字段，只能在真实写入时验证（未写盘）。";
                    return false;
                }

                EditorUtility.SetDirty(target);
                entry["ok"] = true;
                entry["message"] = "非序列化成员已通过反射写入。";
                return true;
            }

            if (!dryRun)
            {
                Undo.RecordObject(target, "cucli so " + propertyPath);
                try
                {
                    serialized.ApplyModifiedProperties();
                }
                catch (Exception exception)
                {
                    entry["ok"] = false;
                    entry["message"] = "Unity 拒绝了该值：" + exception.GetBaseException().Message;
                    return false;
                }

                EditorUtility.SetDirty(target);
            }

            entry["ok"] = true;
            entry["message"] = dryRun ? "值格式校验通过。" : "属性已写入。";
            return true;
        }

        /// <summary>Turns <c>myList[5]</c> into the <c>myList.Array.data[5]</c> spelling Unity expects.</summary>
        private static string NormalizeArrayPath(string path)
        {
            if (path.IndexOf('[') < 0) return path;
            if (path.IndexOf(".Array.data[", StringComparison.Ordinal) >= 0) return path;

            var builder = new System.Text.StringBuilder(path.Length + 16);
            for (int index = 0; index < path.Length; index++)
            {
                char current = path[index];
                if (current == '[')
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] != '.') builder.Append(".Array.data");
                    else builder.Append("Array.data");
                }

                builder.Append(current);
            }

            return builder.ToString();
        }

        private static bool TryResolveObjectReference(JToken token, out Object resolved, out string error)
        {
            resolved = null;
            error = null;

            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type == JTokenType.Integer)
            {
                int instanceId = token.Value<int>();
                resolved = EditorUtility.InstanceIDToObject(instanceId);
                if (resolved == null)
                {
                    error = "找不到 instanceID " + instanceId + " 对应的对象。";
                    return false;
                }

                return true;
            }

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>() ?? string.Empty;
                string path = CucliAssets.NormalizeAssetPath(text);
                if (path != null)
                {
                    resolved = AssetDatabase.LoadMainAssetAtPath(path);
                    if (resolved != null) return true;
                }

                GameObject sceneObject;
                string sceneError;
                sceneObject = CucliObjectResolver.FindByHierarchyPath(text, out sceneError);
                if (sceneObject != null)
                {
                    resolved = sceneObject;
                    return true;
                }

                error = "无法把 '" + text + "' 解析成资产或场景对象。";
                return false;
            }

            var reference = token as JObject;
            if (reference != null)
            {
                var args = new CucliArgs(reference);

                int instanceId = args.GetInt("instanceID", args.GetInt("instance_id", 0));
                if (instanceId != 0)
                {
                    resolved = EditorUtility.InstanceIDToObject(instanceId);
                    if (resolved == null)
                    {
                        error = "找不到 instanceID " + instanceId + " 对应的对象。";
                        return false;
                    }

                    return true;
                }

                string guid = args.GetString("guid");
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    string guidPath = CucliAssets.GuidToAssetPath(guid.Trim());
                    if (string.IsNullOrEmpty(guidPath))
                    {
                        error = "GUID '" + guid + "' 无法解析成资产路径。";
                        return false;
                    }

                    string spriteName = args.GetString("spriteName") ?? args.GetString("sprite_name");
                    int fileId = args.GetInt("fileID", args.GetInt("file_id", 0));
                    Object[] all = AssetDatabase.LoadAllAssetsAtPath(guidPath);
                    resolved = PickSubAsset(all, spriteName, fileId);
                    if (resolved == null)
                    {
                        error = "在 " + guidPath + " 中找不到匹配的子资产（spriteName/fileID）。";
                        return false;
                    }

                    return true;
                }

                string path = args.GetString("path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    string normalized = CucliAssets.NormalizeAssetPath(path);
                    if (normalized == null)
                    {
                        error = "引用路径 '" + path + "' 无法归一化。";
                        return false;
                    }

                    string componentName = args.GetString("component");
                    Object main = AssetDatabase.LoadMainAssetAtPath(normalized);
                    if (main == null)
                    {
                        error = "找不到资产：" + normalized + "。";
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(componentName))
                    {
                        var gameObject = main as GameObject;
                        if (gameObject == null)
                        {
                            error = "资产 " + normalized + " 不是 GameObject，无法按 component 过滤。";
                            return false;
                        }

                        Component component = CucliObjectResolver.ResolveComponent(
                            gameObject, componentName.Trim(), out string componentError);
                        if (component == null)
                        {
                            error = componentError;
                            return false;
                        }

                        resolved = component;
                        return true;
                    }

                    resolved = main;
                    return true;
                }

                string name = args.GetString("name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    GameObject found = null;
                    foreach (Object candidate in Resources.FindObjectsOfTypeAll(typeof(GameObject)))
                    {
                        var gameObject = candidate as GameObject;
                        if (gameObject == null) continue;
                        if (!string.Equals(gameObject.name, name.Trim(), StringComparison.Ordinal)) continue;
                        if (EditorUtility.IsPersistent(gameObject)) continue;
                        found = gameObject;
                        break;
                    }

                    if (found == null)
                    {
                        error = "已加载场景中找不到名为 '" + name + "' 的对象。";
                        return false;
                    }

                    resolved = found;
                    return true;
                }
            }

            error = "ref 需要 instanceID、guid、path 或 name 之一。";
            return false;
        }

        private static Object PickSubAsset(Object[] all, string spriteName, int fileId)
        {
            if (all == null) return null;

            for (int index = 0; index < all.Length; index++)
            {
                Object candidate = all[index];
                if (candidate == null) continue;
                if (fileId != 0)
                {
                    int id;
                    try
                    {
                        id = candidate.GetInstanceID();
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (id == fileId) return candidate;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(spriteName))
                {
                    var sprite = candidate as Sprite;
                    if (sprite != null && string.Equals(sprite.name, spriteName.Trim(), StringComparison.Ordinal))
                    {
                        return candidate;
                    }

                    continue;
                }

                if (index == 0) return candidate;
            }

            return null;
        }
    }
}
