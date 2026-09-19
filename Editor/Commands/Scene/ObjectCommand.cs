using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli object ...</c> - GameObject creation and editing, searching, and component
    /// management. This is the CLI counterpart of <c>manage_gameobject</c> + <c>find_gameobjects</c>
    /// + <c>manage_components</c>.
    ///
    /// Every target is resolved through <see cref="CucliObjectResolver"/> so that instance ids,
    /// hierarchy paths, names, tags, layers and component types all work identically here and in
    /// the rest of the tool. Component reads always go through <see cref="CucliDescribe"/> and
    /// <see cref="CucliSerialized"/>, which bound depth and item counts: dumping a raw
    /// <c>Mesh</c> or <c>ParticleSystem</c> would produce megabytes of unusable output.
    /// </summary>
    internal sealed class ObjectCommand : CucliCommand
    {
        private const int DefaultFindPageSize = 50;

        private static readonly string[] SupportedSearchMethods =
        {
            "by_id_or_name_or_path", "by_id", "by_name", "by_path", "by_tag", "by_layer", "by_component"
        };

        public override string Name => "object";

        public override string Summary =>
            "GameObject 与组件：创建、修改、删除、复制、相对移动、朝向、查找，以及组件的增删改查与检视。";

        public override CucliActionInfo[] Actions => Declare(
            "create", "创建 GameObject（空物体 / primitive / prefab 实例），可设父节点、transform、tag、layer、组件。",
            "modify", "修改目标 GameObject：改名、父节点、transform、tag、layer、激活状态、静态标记、组件增删与属性。",
            "delete", "删除全部同名匹配的 GameObject（需要 force: true 确认，data 里报告删除数量）。",
            "duplicate", "复制目标 GameObject，可设置新名、世界位置、世界偏移与父节点。",
            "move_relative", "相对参考物体沿指定方向移动目标，支持世界轴或参考物体本地轴。",
            "look_at", "让目标朝向一个坐标或另一个 GameObject。",
            "find", "按 searchMethod 分页查找 GameObject（by_name / by_tag / by_layer / by_component / by_path / by_id）。",
            "list", "列出目标 GameObject 上的全部组件（受 maxItems / maxDepth 限制）。",
            "add", "给目标 GameObject 添加一个组件，可同时写入初始属性。",
            "remove", "移除目标 GameObject 上的一个组件（可按 componentIndex 指定同类型实例）。",
            "set-property", "写入组件属性：批量 properties 对象，或 property + value 单字段。",
            "get-property", "读取单个组件属性，输出受 CucliDescribe 约束。",
            "inspect", "检视组件（或 GameObject 本身）的成员清单，可选读取成员值。");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "editor.scene.read", "editor.scene.write", "project.assets.write"
        };

        public override bool MayReloadDomain => false;

        public override string OptionalPackage => null;

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""create"", ""modify"", ""delete"", ""duplicate"", ""move_relative"", ""look_at"", ""find"",
                 ""list"", ""add"", ""remove"", ""set-property"", ""get-property"", ""inspect""],
      ""default"": ""create""
    },
    ""target"": { ""type"": ""string"", ""description"": ""name / 层级路径 / instanceID；create 之外多数 action 需要。"" },
    ""name"": { ""type"": ""string"", ""description"": ""action=create：新物体名；action=modify：缺 target 时作为查找名。"" },
    ""newName"": { ""type"": ""string"", ""description"": ""action=modify / duplicate：改名后的新名。"" },
    ""searchMethod"": {
      ""type"": ""string"",
      ""enum"": [""by_id_or_name_or_path"", ""by_id"", ""by_name"", ""by_path"", ""by_tag"", ""by_layer"", ""by_component""],
      ""description"": ""寻址方式；find 默认 by_name。"" },
    ""searchTerm"": { ""type"": ""string"", ""description"": ""action=find：搜索词（target 为兼容别名）。"" },
    ""includeInactive"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=find：是否包含 inactive（resolver 始终包含）。"" },
    ""parent"": { ""description"": ""action=create / modify / duplicate：父物体引用；空串或 null 表示移到根。"" },
    ""position"": { ""description"": ""action=create / modify 写 localPosition；duplicate 写世界 position。"" },
    ""rotation"": { ""description"": ""action=create / modify：localEulerAngles [x,y,z]。"" },
    ""scale"": { ""description"": ""action=create / modify：localScale [x,y,z]。"" },
    ""tag"": { ""type"": ""string"" },
    ""layer"": { ""type"": ""string"", ""description"": ""层名或 0-31 的层索引。"" },
    ""setActive"": { ""type"": ""boolean"", ""description"": ""action=modify：设置 activeSelf。"" },
    ""isStatic"": { ""type"": ""boolean"", ""description"": ""action=modify：设置 StaticEditorFlags。"" },
    ""primitiveType"": { ""type"": ""string"", ""description"": ""action=create：PrimitiveType 枚举名（Sphere / Capsule / Cylinder / Cube / Plane / Quad）。"" },
    ""prefabPath"": { ""type"": ""string"", ""description"": ""action=create：实例化来源 prefab；saveAsPrefab 时作为保存目标。"" },
    ""saveAsPrefab"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=create：把新物体存为 prefab。"" },
    ""componentsToAdd"": { ""type"": ""array"", ""items"": {}, ""description"": ""元素为类型名，或 { typeName, properties }。"" },
    ""componentsToRemove"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""componentProperties"": { ""description"": ""{ 组件名: { 属性: 值 } }，也接受 JSON 字符串。"" },
    ""componentType"": { ""type"": ""string"", ""description"": ""组件类型名，支持全限定名。"" },
    ""componentIndex"": { ""type"": ""integer"", ""description"": ""同类型多实例时按 0 基索引操作。"" },
    ""properties"": { ""description"": ""action=add / set-property：{ 属性: 值 } 对象或 JSON 字符串。"" },
    ""property"": { ""type"": ""string"", ""description"": ""action=set-property / get-property：单个属性名或属性路径（如 a.b[0].c）。"" },
    ""value"": { ""description"": ""action=set-property：与 property 配对的单属性值。"" },
    ""offset"": { ""description"": ""action=duplicate：世界偏移；action=move_relative：相对偏移。"" },
    ""referenceObject"": { ""description"": ""action=move_relative：参考物体引用（必填）。"" },
    ""direction"": { ""type"": ""string"", ""enum"": [""right"", ""left"", ""up"", ""down"", ""forward"", ""front"", ""back"", ""backward"", ""behind""], ""default"": ""forward"" },
    ""distance"": { ""type"": ""number"", ""default"": 1 },
    ""worldSpace"": { ""type"": ""boolean"", ""default"": true, ""description"": ""action=move_relative：true 用世界轴，false 用参考物体本地轴。"" },
    ""lookAtTarget"": { ""description"": ""action=look_at：[x,y,z] 或 GameObject 引用。"" },
    ""lookAtUp"": { ""description"": ""action=look_at：上方向，默认 [0,1,0]。"" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=delete：确认删除。"" },
    ""pageSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50 },
    ""cursor"": { ""type"": [""string"", ""integer""], ""description"": ""0 基游标。"" },
    ""pageNumber"": { ""type"": ""integer"", ""minimum"": 1 },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64, ""description"": ""list / get-property / inspect 的输出上限。"" },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 },
    ""maxStringLength"": { ""type"": ""integer"", ""default"": 4096 },
    ""includeValues"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=inspect：是否读取成员值。"" },
    ""includeNoisy"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=inspect：是否包含 hideFlags 之类的噪声成员。"" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "create");
            switch (action)
            {
                case "create": return Create(context);
                case "modify": return Modify(context);
                case "delete": return Delete(context);
                case "duplicate": return Duplicate(context);
                case "move_relative": return MoveRelative(context);
                case "look_at": return LookAt(context);
                case "find": return Find(context);
                case "list": return ListComponents(context);
                case "add": return AddComponent(context);
                case "remove": return RemoveComponent(context);
                case "set-property": return SetProperty(context);
                case "get-property": return GetProperty(context);
                case "inspect": return Inspect(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ── create ─────────────────────────────────────────────────────────────

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string name = args.GetString("name");
            string primitiveName = args.GetString("primitiveType") ?? args.GetString("primitive_type");
            string prefabPath = args.GetString("prefabPath") ?? args.GetString("prefab_path");
            bool saveAsPrefab = args.GetBool("saveAsPrefab", args.GetBool("save_as_prefab", false));
            bool instantiateFromPrefab = !string.IsNullOrWhiteSpace(prefabPath) && !saveAsPrefab;

            GameObject gameObject = null;

            if (instantiateFromPrefab)
            {
                string normalized = CucliAssets.NormalizeAssetPath(prefabPath);
                GameObject prefab = normalized == null ? null : CucliAssets.Load<GameObject>(normalized);
                if (prefab == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound,
                        "找不到 prefab 资产：" + (normalized ?? prefabPath) + "。",
                        new JObject { ["prefabPath"] = normalized ?? prefabPath });
                }

                gameObject = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (gameObject == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "实例化 prefab '" + normalized + "' 失败。");
                }

                Undo.RegisterCreatedObjectUndo(gameObject, "cucli create");
                if (!string.IsNullOrWhiteSpace(name)) gameObject.name = name.Trim();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return CucliCommandResult.Missing(
                        "name", "示例：--name Ball --primitive-type Sphere；或提供 prefabPath。");
                }

                string trimmed = name.Trim();

                if (!string.IsNullOrWhiteSpace(primitiveName))
                {
                    PrimitiveType primitive;
                    if (!TryParsePrimitive(primitiveName, out primitive))
                    {
                        return CucliCommandResult.Invalid(
                            "primitiveType",
                            "'" + primitiveName + "' 不是合法的 PrimitiveType。可用："
                            + string.Join(", ", Enum.GetNames(typeof(PrimitiveType))) + "。");
                    }

                    gameObject = GameObject.CreatePrimitive(primitive);
                    gameObject.name = trimmed;
                }
                else
                {
                    gameObject = new GameObject(trimmed);
                }

                Undo.RegisterCreatedObjectUndo(gameObject, "cucli create");
            }

            Undo.RecordObject(gameObject.transform, "cucli create transform");

            JToken parentToken = args.Token("parent");
            if (parentToken != null && parentToken.Type != JTokenType.Null && !IsEmptyValue(parentToken))
            {
                string parentError;
                GameObject parent = ResolveObjectToken(parentToken, out parentError);
                if (parentError != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, parentError);
                }

                gameObject.transform.SetParent(parent.transform, false);
            }

            if (args.Token("position") != null)
            {
                gameObject.transform.localPosition = args.GetVector3("position");
            }

            if (args.Token("rotation") != null)
            {
                gameObject.transform.localEulerAngles = args.GetVector3("rotation");
            }

            if (args.Token("scale") != null)
            {
                gameObject.transform.localScale = args.GetVector3("scale");
            }

            string tag = args.GetString("tag");
            if (!string.IsNullOrWhiteSpace(tag))
            {
                string tagError;
                if (!TryApplyTag(gameObject, tag, out tagError))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter, tagError, new JObject { ["tag"] = tag });
                }
            }

            string layerName = args.GetString("layerName") ?? args.GetString("layer_name")
                ?? args.GetString("layer");
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                string layerError;
                int layer = TryResolveLayer(layerName, out layerError);
                if (layerError != null)
                {
                    // create 时无效层只警告，不阻断（与 MCP 语义一致）。
                    context.Warn("层 '" + layerName + "' 无效，已忽略：" + layerError);
                }
                else
                {
                    gameObject.layer = layer;
                }
            }

            var componentErrors = new JArray();
            TryApplyComponents(gameObject, args, componentErrors);

            JObject data = DescribeGameObject(gameObject);
            if (componentErrors.Count > 0)
            {
                context.Warn(componentErrors.Count + " 个组件未能添加，详见 data.componentErrors。");
                data["componentErrors"] = componentErrors;
            }

            if (saveAsPrefab)
            {
                if (string.IsNullOrWhiteSpace(prefabPath))
                {
                    return CucliCommandResult.Missing(
                        "prefabPath", "saveAsPrefab 需要 prefabPath 作为保存目标路径。");
                }

                string normalized = CucliAssets.NormalizeAssetPath(prefabPath);
                if (normalized == null || !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    return CucliCommandResult.Invalid(
                        "prefabPath", "prefab 路径必须位于项目 Assets/ 下，收到：" + prefabPath + "。");
                }

                if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) normalized += ".prefab";

                string folder = CucliAssets.ParentFolder(normalized);
                if (string.IsNullOrEmpty(folder) || !CucliAssets.EnsureFolder(folder))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "无法创建 prefab 目录：" + (folder ?? normalized) + "。");
                }

                GameObject saved = PrefabUtility.SaveAsPrefabAssetAndConnect(
                    gameObject, normalized, InteractionMode.AutomatedAction);
                if (saved == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "保存 prefab 失败：" + normalized + "。");
                }

                AssetDatabase.SaveAssets();
                data["prefabPath"] = normalized;
            }

            MarkDirty(gameObject);

            return CucliCommandResult.Ok(
                data, "已创建 GameObject " + gameObject.name + "（instanceID " + gameObject.GetInstanceID() + "）。");
        }

        private static bool TryParsePrimitive(string value, out PrimitiveType primitive)
        {
            primitive = PrimitiveType.Cube;
            string trimmed = value.Trim();
            foreach (string candidate in Enum.GetNames(typeof(PrimitiveType)))
            {
                if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    primitive = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), candidate);
                    return true;
                }
            }

            int numeric;
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric)
                && Enum.IsDefined(typeof(PrimitiveType), numeric))
            {
                primitive = (PrimitiveType)Enum.ToObject(typeof(PrimitiveType), numeric);
                return true;
            }

            return false;
        }

        // ── modify ─────────────────────────────────────────────────────────────

        private static CucliCommandResult Modify(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    resolveError ?? "需要 target（name / 层级路径 / instanceID）。");
            }

            var applied = new JArray();
            var errors = new JArray();

            Undo.RecordObject(gameObject, "cucli modify " + gameObject.name);
            Undo.RecordObject(gameObject.transform, "cucli modify " + gameObject.name);

            string newName = args.GetString("newName") ?? args.GetString("new_name");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                gameObject.name = newName.Trim();
                applied.Add("name");
            }

            JToken parentToken = args.Token("parent");
            if (parentToken != null)
            {
                if (parentToken.Type == JTokenType.Null || IsEmptyValue(parentToken))
                {
                    gameObject.transform.SetParent(null, true);
                    applied.Add("parent");
                }
                else
                {
                    string parentError;
                    GameObject parent = ResolveObjectToken(parentToken, out parentError);
                    if (parentError != null)
                    {
                        return CucliCommandResult.Fail(CucliErrors.TargetNotFound, parentError);
                    }

                    if (parent == gameObject || IsDescendantOf(parent.transform, gameObject.transform))
                    {
                        return CucliCommandResult.Invalid(
                            "parent",
                            "把 '" + gameObject.name + "' 挂到 '" + parent.name + "' 会造成层级环。");
                    }

                    gameObject.transform.SetParent(parent.transform, true);
                    applied.Add("parent");
                }
            }

            if (args.Token("position") != null)
            {
                gameObject.transform.localPosition = args.GetVector3("position");
                applied.Add("position");
            }

            if (args.Token("rotation") != null)
            {
                gameObject.transform.localEulerAngles = args.GetVector3("rotation");
                applied.Add("rotation");
            }

            if (args.Token("scale") != null)
            {
                gameObject.transform.localScale = args.GetVector3("scale");
                applied.Add("scale");
            }

            if (args.Token("setActive") != null || args.Token("set_active") != null)
            {
                bool active = args.GetBool("setActive", args.GetBool("set_active", true));
                gameObject.SetActive(active);
                applied.Add("setActive");
            }

            if (args.Token("isStatic") != null || args.Token("is_static") != null)
            {
                gameObject.isStatic = args.GetBool("isStatic", args.GetBool("is_static", false));
                applied.Add("isStatic");
            }

            string tag = args.GetString("tag");
            if (!string.IsNullOrWhiteSpace(tag))
            {
                string tagError;
                if (!TryApplyTag(gameObject, tag, out tagError))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter, tagError, new JObject { ["tag"] = tag });
                }

                applied.Add("tag");
            }

            string layerName = args.GetString("layerName") ?? args.GetString("layer_name")
                ?? args.GetString("layer");
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                string layerError;
                int layer = TryResolveLayer(layerName, out layerError);
                if (layerError != null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter, layerError, new JObject { ["layer"] = layerName });
                }

                gameObject.layer = layer;
                applied.Add("layer");
            }

            var componentErrors = new JArray();
            TryApplyComponents(gameObject, args, componentErrors);

            int removed = RemoveComponents(gameObject, args, componentErrors);
            if (removed > 0) applied.Add("componentsToRemove");

            int propertiesApplied = ApplyComponentProperties(gameObject, args, componentErrors);
            if (propertiesApplied > 0) applied.Add("componentProperties");

            MarkDirty(gameObject);

            JObject data = DescribeGameObject(gameObject);
            data["applied"] = applied;
            data["removedComponentCount"] = removed;

            if (componentErrors.Count > 0)
            {
                data["componentErrors"] = componentErrors;
                data["errors"] = componentErrors;
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "有 " + componentErrors.Count + " 项组件操作失败，其余修改已应用；详见 data.componentErrors。",
                    data);
            }

            return CucliCommandResult.Ok(
                data,
                applied.Count == 0
                    ? "没有可应用的改动，已返回 " + gameObject.name + " 的当前状态。"
                    : "已修改 GameObject " + gameObject.name + "（" + string.Join(", ", ToStrings(applied)) + "）。");
        }

        private static string[] ToStrings(JArray array)
        {
            var result = new string[array.Count];
            for (int index = 0; index < array.Count; index++) result[index] = array[index].ToString();
            return result;
        }

        // ── delete / duplicate ─────────────────────────────────────────────────

        private static CucliCommandResult Delete(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            var resolverPayload = (JObject)args.Raw.DeepClone();
            resolverPayload["allowMultiple"] = true;

            string resolveError;
            GameObject[] matches = CucliObjectResolver.ResolveGameObjects(
                new CucliArgs(resolverPayload), out resolveError, false);
            if (resolveError != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, resolveError);
            }

            if (matches.Length == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, "没有匹配的 GameObject 可删除。");
            }

            var victims = new JArray();
            foreach (GameObject match in matches)
            {
                victims.Add(new JObject
                {
                    ["name"] = match.name,
                    ["instanceID"] = match.GetInstanceID(),
                    ["path"] = CucliObjectResolver.HierarchyPath(match, true)
                });
            }

            if (!args.GetBool("force", false))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "删除 GameObject 属于破坏性操作：将删除 " + matches.Length
                    + " 个匹配对象。请确认后显式传 force: true 重试。",
                    new JObject { ["wouldDelete"] = victims, ["count"] = matches.Length });
            }

            var deleted = new JArray();
            foreach (GameObject match in matches)
            {
                context.ThrowIfCancelled();
                var entry = new JObject
                {
                    ["name"] = match.name,
                    ["instanceID"] = match.GetInstanceID()
                };

                Undo.DestroyObjectImmediate(match);
                deleted.Add(entry);
            }

            return CucliCommandResult.Ok(
                new JObject { ["deleted"] = deleted, ["deletedCount"] = deleted.Count },
                "已删除 " + deleted.Count + " 个 GameObject。");
        }

        private static CucliCommandResult Duplicate(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject source = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || source == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定要复制的物体。");
            }

            GameObject clone = Object.Instantiate(source);
            clone.name = source.name;
            Undo.RegisterCreatedObjectUndo(clone, "cucli duplicate");
            Undo.RecordObject(clone.transform, "cucli duplicate transform");

            string newName = args.GetString("newName") ?? args.GetString("new_name");
            if (!string.IsNullOrWhiteSpace(newName)) clone.name = newName.Trim();

            if (args.Token("offset") != null)
            {
                clone.transform.position = source.transform.position + args.GetVector3("offset");
            }

            if (args.Token("position") != null)
            {
                clone.transform.position = args.GetVector3("position");
            }

            JToken parentToken = args.Token("parent");
            if (parentToken != null && parentToken.Type != JTokenType.Null && !IsEmptyValue(parentToken))
            {
                string parentError;
                GameObject parent = ResolveObjectToken(parentToken, out parentError);
                if (parentError != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, parentError);
                }

                if (parent == clone || IsDescendantOf(parent.transform, clone.transform))
                {
                    Object.DestroyImmediate(clone);
                    return CucliCommandResult.Invalid(
                        "parent", "把副本挂到 '" + parent.name + "' 会造成层级环。");
                }

                clone.transform.SetParent(parent.transform, true);
            }

            MarkDirty(clone);

            return CucliCommandResult.Ok(
                new JObject
                {
                    ["originalName"] = source.name,
                    ["originalId"] = source.GetInstanceID(),
                    ["duplicatedObject"] = DescribeGameObject(clone)
                },
                "已复制 " + source.name + " 为 " + clone.name + "。");
        }

        // ── move_relative / look_at ────────────────────────────────────────────

        private static CucliCommandResult MoveRelative(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject target = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || target == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定要移动的物体。");
            }

            JToken referenceToken = args.Token("referenceObject") ?? args.Token("reference_object");
            if (referenceToken == null || referenceToken.Type == JTokenType.Null
                || IsEmptyValue(referenceToken))
            {
                return CucliCommandResult.Missing(
                    "referenceObject", "示例：--reference-object Anchor --direction up --distance 2");
            }

            string referenceError;
            GameObject reference = ResolveObjectToken(referenceToken, out referenceError);
            if (referenceError != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, referenceError);
            }

            string direction = (args.GetString("direction") ?? "forward").Trim().ToLowerInvariant();
            float distance = args.GetFloat("distance", 1f);
            bool worldSpace = args.GetBool("worldSpace", args.GetBool("world_space", true));

            Vector3 localAxis = DirectionToAxis(direction);
            Vector3 axis = worldSpace ? localAxis : reference.transform.TransformDirection(localAxis);
            Vector3 newPosition = reference.transform.position + axis * distance;

            if (args.Token("offset") != null) newPosition += args.GetVector3("offset");

            Undo.RecordObject(target.transform, "cucli move_relative");
            target.transform.position = newPosition;
            MarkDirty(target);

            return CucliCommandResult.Ok(
                new JObject
                {
                    ["movedObject"] = target.name,
                    ["referenceObject"] = reference.name,
                    ["direction"] = direction,
                    ["distance"] = distance,
                    ["worldSpace"] = worldSpace,
                    ["newPosition"] = new JArray(newPosition.x, newPosition.y, newPosition.z),
                    ["gameObject"] = DescribeGameObject(target)
                },
                "已把 " + target.name + " 相对 " + reference.name + " 沿 " + direction + " 移动 " + distance + "。");
        }

        private static Vector3 DirectionToAxis(string direction)
        {
            switch (direction)
            {
                case "right": return Vector3.right;
                case "left": return Vector3.left;
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                case "back":
                case "backward":
                case "behind": return Vector3.back;
                default: return Vector3.forward;
            }
        }

        private static CucliCommandResult LookAt(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject target = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || target == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定要转向的物体。");
            }

            JToken lookToken = args.Token("lookAtTarget") ?? args.Token("look_at_target");
            if (lookToken == null || lookToken.Type == JTokenType.Null || IsEmptyValue(lookToken))
            {
                return CucliCommandResult.Missing(
                    "lookAtTarget", "示例：--look-at-target \"[0,0,0]\" 或 --look-at-target Anchor");
            }

            Vector3 lookAtPosition;
            GameObject lookAtObject = null;

            if (!TryReadVector3(lookToken, out lookAtPosition))
            {
                string lookError;
                lookAtObject = ResolveObjectToken(lookToken, out lookError);
                if (lookError != null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        "lookAtTarget 既不是 [x,y,z] 坐标，也不是可解析的 GameObject：" + lookError);
                }

                lookAtPosition = lookAtObject.transform.position;
            }

            Vector3 up = args.GetVector3("lookAtUp", args.GetVector3("look_at_up", Vector3.up));

            Undo.RecordObject(target.transform, "cucli look_at");
            target.transform.LookAt(lookAtPosition, up);
            MarkDirty(target);

            Vector3 euler = target.transform.eulerAngles;

            var data = new JObject
            {
                ["name"] = target.name,
                ["instanceID"] = target.GetInstanceID(),
                ["lookAtPosition"] = new JArray(lookAtPosition.x, lookAtPosition.y, lookAtPosition.z),
                ["rotation"] = new JArray(euler.x, euler.y, euler.z)
            };

            if (lookAtObject != null) data["lookAtObject"] = lookAtObject.name;

            return CucliCommandResult.Ok(data, "已让 " + target.name + " 朝向目标点。");
        }

        // ── find ───────────────────────────────────────────────────────────────

        private static CucliCommandResult Find(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string method = (args.GetString("searchMethod") ?? args.GetString("search_method")
                ?? "by_name").Trim().ToLowerInvariant();

            if (Array.IndexOf(SupportedSearchMethods, method) < 0)
            {
                return CucliCommandResult.Invalid(
                    "searchMethod",
                    "'" + method + "' 不受支持。可用：" + string.Join(", ", SupportedSearchMethods) + "。");
            }

            string searchTerm = args.GetString("searchTerm") ?? args.GetString("search_term")
                ?? args.GetString("target");
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return CucliCommandResult.Missing(
                    "searchTerm", "searchTerm 与 target 至少提供一个。示例：--search-term Ball --search-method by_name");
            }

            searchTerm = searchTerm.Trim();

            if (method == "by_layer")
            {
                string layerError;
                CucliObjectResolver.ResolveLayer(
                    new CucliArgs(new JObject { ["layer"] = searchTerm }), out layerError);
                if (layerError != null)
                {
                    return CucliCommandResult.Invalid("searchTerm", layerError);
                }
            }
            else if (method == "by_component")
            {
                string typeError;
                Type componentType = CucliReflection.FindType(searchTerm, out typeError);
                if (componentType == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TypeNotFound, typeError, new JObject { ["searchTerm"] = searchTerm });
                }

                if (!typeof(Component).IsAssignableFrom(componentType))
                {
                    return CucliCommandResult.Invalid(
                        "searchTerm", "'" + searchTerm + "' 不是 Component 类型。");
                }
            }
            else if (method == "by_id")
            {
                int numeric;
                if (!int.TryParse(searchTerm, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                {
                    return CucliCommandResult.Invalid(
                        "searchTerm", "by_id 需要一个整数 instanceID，收到：'" + searchTerm + "'。");
                }
            }

            var resolverPayload = new JObject
            {
                ["searchMethod"] = method,
                ["target"] = searchTerm,
                ["allowMultiple"] = true
            };

            // The resolver reads the key that matches the search method, not 'searchTerm'.
            switch (method)
            {
                case "by_name": resolverPayload["name"] = searchTerm; break;
                case "by_path": resolverPayload["path"] = searchTerm; break;
                case "by_tag": resolverPayload["tag"] = searchTerm; break;
                case "by_layer": resolverPayload["layer"] = searchTerm; break;
                case "by_component": resolverPayload["component"] = searchTerm; break;
                case "by_id": resolverPayload["instanceId"] = searchTerm; break;
            }

            string resolveError;
            GameObject[] matches = CucliObjectResolver.ResolveGameObjects(
                new CucliArgs(resolverPayload), out resolveError, false);

            if (matches == null) matches = Array.Empty<GameObject>();
            if (!string.IsNullOrEmpty(resolveError))
            {
                // A search that matches nothing is a successful empty result, so the resolver's
                // diagnostic is reported as a warning rather than a failure.
                context.Warn(resolveError);
            }

            bool includeInactive = args.GetBool(
                "includeInactive",
                args.GetBool("include_inactive", args.GetBool("searchInactive", args.GetBool("search_inactive", false))));

            if (!includeInactive)
            {
                int inactiveCount = 0;
                foreach (GameObject match in matches)
                {
                    if (match != null && !match.activeInHierarchy) inactiveCount++;
                }

                if (inactiveCount > 0)
                {
                    context.Warn(
                        "结果中有 " + inactiveCount + " 个 inactive 物体；CucliObjectResolver 始终包含 inactive，"
                        + "如需隐藏请自行按 activeInHierarchy 过滤。");
                }
            }

            CucliPage<GameObject> page = CucliPaging.Slice(
                matches, WithNumericCursor(args), DefaultFindPageSize);

            var items = new JArray();
            var instanceIds = new JArray();
            foreach (GameObject match in page.Items)
            {
                context.ThrowIfCancelled();
                if (match == null) continue;
                instanceIds.Add(match.GetInstanceID());
                items.Add(DescribeGameObject(match));
            }

            var payload = new JObject
            {
                ["searchMethod"] = method,
                ["searchTerm"] = searchTerm,
                ["cursor"] = page.Offset,
                ["totalCount"] = page.Total
            };

            JObject envelope = CucliPaging.Envelope(page, items, payload);
            envelope["instanceIDs"] = instanceIds;
            if (page.HasMore) envelope["next_cursor"] = page.NextCursor;

            return CucliCommandResult.Ok(
                envelope,
                page.Total == 0
                    ? "没有匹配的 GameObject（searchMethod=" + method + "，searchTerm=" + searchTerm + "）。"
                    : "已返回 " + page.Items.Count + "/" + page.Total + " 个匹配的 GameObject。");
        }

        // ── components: list / add / remove ────────────────────────────────────

        private static CucliCommandResult ListComponents(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定 GameObject。");
            }

            CucliDescribe.Options options = CucliDescribe.OptionsFrom(args);
            JArray components = CucliDescribe.Components(gameObject, options, 0);

            return CucliCommandResult.Ok(
                new JObject
                {
                    ["name"] = gameObject.name,
                    ["instanceID"] = gameObject.GetInstanceID(),
                    ["path"] = CucliObjectResolver.HierarchyPath(gameObject, true),
                    ["count"] = components.Count,
                    ["components"] = components
                },
                gameObject.name + " 上有 " + components.Count + " 个组件。");
        }

        private static CucliCommandResult AddComponent(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定 GameObject。");
            }

            string typeName = args.GetString("componentType") ?? args.GetString("component_type")
                ?? args.GetString("type");
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return CucliCommandResult.Missing(
                    "componentType", "示例：--component-type BoxCollider");
            }

            string typeError;
            Type type = CucliReflection.FindType(typeName, out typeError);
            if (type == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound, typeError, new JObject { ["componentType"] = typeName });
            }

            if (!typeof(Component).IsAssignableFrom(type))
            {
                return CucliCommandResult.Invalid(
                    "componentType", "'" + typeName + "' 不是 Component 类型。");
            }

            if (type == typeof(Transform) || type == typeof(RectTransform))
            {
                return CucliCommandResult.Invalid(
                    "componentType", "Transform / RectTransform 由 Unity 管理，不能通过 add 添加。");
            }

            Component added;
            try
            {
                added = Undo.AddComponent(gameObject, type);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "添加组件 '" + type.Name + "' 失败：" + exception.GetBaseException().Message,
                    new JObject { ["componentType"] = type.FullName });
            }

            if (added == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Unity 拒绝添加组件 '" + type.FullName + "'（可能是抽象类型或被 DisallowMultipleComponent 限制）。");
            }

            var errors = new JArray();
            var applied = new JArray();

            JObject properties = ReadObjectArgument(args, out string argumentError, "properties", "componentProperties", "component_properties");
            if (properties == null && argumentError != null)
            {
                return CucliCommandResult.Invalid("properties", argumentError);
            }

            if (properties != null)
            {
                ApplyPropertiesToTarget(added, properties, applied, errors);
            }

            MarkDirty(gameObject);

            var data = new JObject
            {
                ["instanceID"] = gameObject.GetInstanceID(),
                ["componentType"] = type.FullName,
                ["componentInstanceID"] = added.GetInstanceID(),
                ["applied"] = applied
            };

            if (errors.Count > 0)
            {
                data["errors"] = errors;
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "组件已添加，但有 " + errors.Count + " 个属性写入失败；详见 data.errors。",
                    data);
            }

            return CucliCommandResult.Ok(data, "已给 " + gameObject.name + " 添加组件 " + type.Name + "。");
        }

        private static CucliCommandResult RemoveComponent(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定 GameObject。");
            }

            string typeName = args.GetString("componentType") ?? args.GetString("component_type")
                ?? args.GetString("type");
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return CucliCommandResult.Missing("componentType");
            }

            string typeError;
            Type type = CucliReflection.FindType(typeName, out typeError);
            if (type == null)
            {
                return CucliCommandResult.Fail(CucliErrors.TypeNotFound, typeError);
            }

            if (!typeof(Component).IsAssignableFrom(type))
            {
                return CucliCommandResult.Invalid(
                    "componentType", "'" + typeName + "' 不是 Component 类型。");
            }

            Component[] components;
            try
            {
                components = gameObject.GetComponents(type);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "读取组件失败：" + exception.GetBaseException().Message);
            }

            if (components.Length == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    gameObject.name + " 上没有 " + type.Name + " 组件。",
                    new JObject { ["instanceID"] = gameObject.GetInstanceID() });
            }

            JToken indexToken = args.Token("componentIndex") ?? args.Token("component_index");
            int index = indexToken == null ? 0 : args.GetInt("componentIndex", args.GetInt("component_index", 0));
            if (index < 0 || index >= components.Length)
            {
                return CucliCommandResult.Invalid(
                    "componentIndex",
                    "索引 " + index + " 越界；" + gameObject.name + " 上有 " + components.Length + " 个 "
                    + type.Name + "（0-" + (components.Length - 1) + "）。");
            }

            Component component = components[index];
            if (component is Transform)
            {
                return CucliCommandResult.Invalid(
                    "componentType", "不允许移除 Transform / RectTransform。");
            }

            Undo.DestroyObjectImmediate(component);
            MarkDirty(gameObject);

            var data = new JObject
            {
                ["instanceID"] = gameObject.GetInstanceID(),
                ["componentType"] = type.FullName
            };

            if (indexToken != null) data["componentIndex"] = index;

            return CucliCommandResult.Ok(data, "已移除 " + gameObject.name + " 上的 " + type.Name + "。");
        }

        // ── components: set-property / get-property / inspect ──────────────────

        private static CucliCommandResult SetProperty(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定 GameObject。");
            }

            string typeName = args.GetString("componentType") ?? args.GetString("component_type")
                ?? args.GetString("type");
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return CucliCommandResult.Missing("componentType");
            }

            string componentError;
            Component component = ResolveComponentByIndex(gameObject, typeName, args, out componentError);
            if (componentError != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, componentError);
            }

            JObject properties = ReadObjectArgument(
                args, out string argumentError, "properties", "componentProperties", "component_properties");
            if (properties == null && argumentError != null)
            {
                return CucliCommandResult.Invalid("properties", argumentError);
            }

            if (properties == null)
            {
                string propertyName = args.GetString("property") ?? args.GetString("propertyPath")
                    ?? args.GetString("property_path");
                if (string.IsNullOrWhiteSpace(propertyName))
                {
                    return CucliCommandResult.Missing(
                        "properties", "需要 properties 对象，或 property + value 单字段写入。");
                }

                if (!args.Has("value"))
                {
                    return CucliCommandResult.Missing(
                        "value", "使用 property 时必须同时提供 value（可以是 null）。");
                }

                properties = new JObject();
                JToken valueToken = args.Token("value");
                properties[propertyName.Trim()] = valueToken ?? JValue.CreateNull();
            }

            var applied = new JArray();
            var errors = new JArray();
            ApplyPropertiesToTarget(component, properties, applied, errors);

            MarkDirty(gameObject);

            var data = new JObject
            {
                ["instanceID"] = gameObject.GetInstanceID(),
                ["componentType"] = component.GetType().FullName,
                ["componentInstanceID"] = component.GetInstanceID(),
                ["applied"] = applied
            };

            if (errors.Count > 0)
            {
                data["errors"] = errors;
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "有 " + errors.Count + " 个属性写入失败（成功的 " + applied.Count + " 个已生效）；详见 data.errors。",
                    data);
            }

            return CucliCommandResult.Ok(
                data,
                "已写入 " + applied.Count + " 个属性到 " + gameObject.name + " 的 "
                + component.GetType().Name + "。");
        }

        private static CucliCommandResult GetProperty(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定 GameObject。");
            }

            string typeName = args.GetString("componentType") ?? args.GetString("component_type")
                ?? args.GetString("type");
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return CucliCommandResult.Missing("componentType");
            }

            string componentError;
            Component component = ResolveComponentByIndex(gameObject, typeName, args, out componentError);
            if (componentError != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, componentError);
            }

            string path = args.GetString("property") ?? args.GetString("propertyPath")
                ?? args.GetString("property_path");
            if (string.IsNullOrWhiteSpace(path))
            {
                return CucliCommandResult.Missing("property", "示例：--property m_Mass");
            }

            path = path.Trim();
            CucliDescribe.Options options = CucliDescribe.OptionsFrom(args);

            JToken value;
            string source;

            SerializedProperty serializedProperty;
            SerializedObject serializedObject;
            if (CucliSerialized.TryGet(component, path, out serializedProperty, out serializedObject))
            {
                value = CucliSerialized.Read(serializedProperty, options, 0);
                source = "serialized";
            }
            else
            {
                string readError;
                object raw = CucliReflection.GetPath(component, path, out readError);
                if (readError != null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.MemberNotFound,
                        readError + " " + component.GetType().Name + " 上没有该成员或属性路径。",
                        new JObject
                        {
                            ["componentType"] = component.GetType().FullName,
                            ["property"] = path
                        });
                }

                value = CucliDescribe.Value(raw, options, 0);
                source = "reflection";
            }

            return CucliCommandResult.Ok(
                new JObject
                {
                    ["name"] = gameObject.name,
                    ["instanceID"] = gameObject.GetInstanceID(),
                    ["componentType"] = component.GetType().FullName,
                    ["property"] = path,
                    ["source"] = source,
                    ["value"] = value
                },
                "已读取 " + component.GetType().Name + "." + path + "。");
        }

        private static CucliCommandResult Inspect(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string resolveError;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, resolveError ?? "需要 target 来指定 GameObject。");
            }

            string typeName = args.GetString("componentType") ?? args.GetString("component_type")
                ?? args.GetString("type");

            Object target;
            string typeLabel;

            if (string.IsNullOrWhiteSpace(typeName))
            {
                target = gameObject;
                typeLabel = typeof(GameObject).FullName;
            }
            else
            {
                string componentError;
                Component component = ResolveComponentByIndex(gameObject, typeName, args, out componentError);
                if (componentError != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, componentError);
                }

                target = component;
                typeLabel = component.GetType().FullName;
            }

            CucliDescribe.Options options = CucliDescribe.OptionsFrom(args);
            bool includeNoisy = args.GetBool("includeNoisy", args.GetBool("include_noisy", false));
            bool includeValues = args.GetBool("includeValues", args.GetBool("include_values", false));

            List<CucliMemberInfo> members = CucliReflection.Members(target.GetType(), includeNoisy);
            var memberArray = new JArray();
            var values = includeValues ? new JObject() : null;
            int emitted = 0;

            foreach (CucliMemberInfo member in members)
            {
                if (emitted++ >= options.MaxItems)
                {
                    memberArray.Add(new JObject { ["$truncated"] = "其余 " + (members.Count - options.MaxItems) + " 个成员已省略" });
                    break;
                }

                memberArray.Add(member.ToJson());

                if (values == null) continue;

                string memberError;
                object raw = CucliReflection.Get(target, member.Name, out memberError);
                values[member.Name] = memberError != null
                    ? new JObject { ["$error"] = memberError }
                    : CucliDescribe.Value(raw, options, 0);
            }

            var data = new JObject
            {
                ["name"] = gameObject.name,
                ["instanceID"] = gameObject.GetInstanceID(),
                ["path"] = CucliObjectResolver.HierarchyPath(gameObject, true),
                ["target"] = target.name,
                ["componentType"] = typeLabel,
                ["memberCount"] = members.Count,
                ["members"] = memberArray,
                ["includeValues"] = includeValues
            };

            var behaviour = target as Behaviour;
            if (behaviour != null) data["enabled"] = behaviour.enabled;
            var renderer = target as Renderer;
            if (renderer != null) data["enabled"] = renderer.enabled;

            if (values != null) data["values"] = values;

            return CucliCommandResult.Ok(
                data,
                "已检视 " + gameObject.name + " 的 " + typeLabel + "，共 " + members.Count + " 个成员。");
        }

        // ── shared component helpers ───────────────────────────────────────────

        private static Component ResolveComponentByIndex(
            GameObject gameObject, string typeName, CucliArgs args, out string error)
        {
            JToken indexToken = args.Token("componentIndex") ?? args.Token("component_index");
            if (indexToken == null)
            {
                return CucliObjectResolver.ResolveComponent(gameObject, typeName, out error);
            }

            error = null;
            string typeError;
            Type type = CucliReflection.FindType(typeName, out typeError);
            if (type == null)
            {
                error = typeError;
                return null;
            }

            if (!typeof(Component).IsAssignableFrom(type))
            {
                error = "'" + typeName + "' 不是 Component 类型。";
                return null;
            }

            Component[] components;
            try
            {
                components = gameObject.GetComponents(type);
            }
            catch (Exception exception)
            {
                error = "读取组件失败：" + exception.GetBaseException().Message;
                return null;
            }

            if (components.Length == 0)
            {
                error = gameObject.name + " 上没有 " + type.Name + " 组件。";
                return null;
            }

            int index = args.GetInt("componentIndex", args.GetInt("component_index", 0));
            if (index < 0 || index >= components.Length)
            {
                error = "componentIndex " + index + " 越界；" + gameObject.name + " 上有 " + components.Length
                    + " 个 " + type.Name + "（0-" + (components.Length - 1) + "）。";
                return null;
            }

            return components[index];
        }

        /// <summary>
        /// Writes each entry of <paramref name="properties"/> onto
        /// <paramref name="target"/>. Serialized fields go through
        /// <see cref="CucliSerialized.TrySet"/> so the write registers Undo and becomes a real Editor
        /// edit; anything Unity does not serialise falls back to reflection.
        /// </summary>
        private static void ApplyPropertiesToTarget(
            Object target, JObject properties, JArray applied, JArray errors)
        {
            foreach (JProperty property in properties.Properties())
            {
                string memberPath = property.Name;
                string error;
                bool handled;

                if (CucliSerialized.TrySet(
                        target, memberPath, property.Value,
                        "cucli set " + memberPath, out error, out handled))
                {
                    applied.Add(memberPath);
                    continue;
                }

                if (handled)
                {
                    errors.Add(new JObject { ["property"] = memberPath, ["error"] = error });
                    continue;
                }

                Undo.RecordObject(target, "cucli set " + memberPath);
                if (!CucliReflection.TrySetPath(target, memberPath, property.Value, out error))
                {
                    errors.Add(new JObject { ["property"] = memberPath, ["error"] = error });
                    continue;
                }

                EditorUtility.SetDirty(target);
                applied.Add(memberPath);
            }
        }

        /// <summary>Adds every entry of <c>componentsToAdd</c>; failures are collected, not thrown.</summary>
        private static void TryApplyComponents(GameObject gameObject, CucliArgs args, JArray errors)
        {
            JArray requested = args.GetArray("componentsToAdd") ?? args.GetArray("components_to_add");
            if (requested == null) return;

            foreach (JToken element in requested)
            {
                if (element == null || element.Type == JTokenType.Null) continue;

                string typeName = null;
                JObject properties = null;

                if (element.Type == JTokenType.String)
                {
                    typeName = element.Value<string>();
                }
                else if (element.Type == JTokenType.Object)
                {
                    var elementArgs = new CucliArgs((JObject)element);
                    typeName = elementArgs.GetString("typeName") ?? elementArgs.GetString("type_name")
                        ?? elementArgs.GetString("type") ?? elementArgs.GetString("componentType")
                        ?? elementArgs.GetString("component_type");
                    properties = elementArgs.GetObject("properties")
                        ?? elementArgs.GetObject("componentProperties");
                }

                if (string.IsNullOrWhiteSpace(typeName))
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = element.ToString(Newtonsoft.Json.Formatting.None),
                        ["error"] = "元素需要类型名字符串，或 { typeName, properties } 对象。"
                    });
                    continue;
                }

                string typeError;
                Type type = CucliReflection.FindType(typeName, out typeError);
                if (type == null)
                {
                    errors.Add(new JObject { ["componentType"] = typeName, ["error"] = typeError });
                    continue;
                }

                if (!typeof(Component).IsAssignableFrom(type))
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = typeName,
                        ["error"] = "'" + typeName + "' 不是 Component 类型。"
                    });
                    continue;
                }

                if (type == typeof(Transform) || type == typeof(RectTransform))
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = typeName,
                        ["error"] = "Transform / RectTransform 已存在，无法再次添加。"
                    });
                    continue;
                }

                Component added;
                try
                {
                    added = Undo.AddComponent(gameObject, type);
                }
                catch (Exception exception)
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = typeName,
                        ["error"] = exception.GetBaseException().Message
                    });
                    continue;
                }

                if (added == null)
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = typeName,
                        ["error"] = "Unity 拒绝添加该组件。"
                    });
                    continue;
                }

                if (properties == null) continue;

                var applied = new JArray();
                var propertyErrors = new JArray();
                ApplyPropertiesToTarget(added, properties, applied, propertyErrors);
                foreach (JToken propertyError in propertyErrors)
                {
                    var entry = propertyError as JObject;
                    if (entry == null) continue;
                    entry["componentType"] = typeName;
                    errors.Add(entry);
                }
            }
        }

        /// <summary>Removes the first matching component per requested type name.</summary>
        private static int RemoveComponents(GameObject gameObject, CucliArgs args, JArray errors)
        {
            string[] requested = args.GetStringList("componentsToRemove");
            if (requested.Length == 0) requested = args.GetStringList("components_to_remove");
            if (requested.Length == 0) return 0;

            int removed = 0;
            foreach (string typeName in requested)
            {
                if (string.IsNullOrWhiteSpace(typeName)) continue;

                string typeError;
                Type type = CucliReflection.FindType(typeName, out typeError);
                if (type == null)
                {
                    errors.Add(new JObject { ["componentType"] = typeName, ["error"] = typeError });
                    continue;
                }

                if (!typeof(Component).IsAssignableFrom(type))
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = typeName,
                        ["error"] = "'" + typeName + "' 不是 Component 类型。"
                    });
                    continue;
                }

                if (type == typeof(Transform) || type == typeof(RectTransform))
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = typeName,
                        ["error"] = "不允许移除 Transform / RectTransform。"
                    });
                    continue;
                }

                Component component = gameObject.GetComponent(type);
                if (component == null)
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = typeName,
                        ["error"] = gameObject.name + " 上没有 " + type.Name + " 组件。"
                    });
                    continue;
                }

                Undo.DestroyObjectImmediate(component);
                removed++;
            }

            return removed;
        }

        /// <summary>Applies <c>componentProperties</c> ({ componentType: { member: value } }).</summary>
        private static int ApplyComponentProperties(GameObject gameObject, CucliArgs args, JArray errors)
        {
            JObject requested = ReadObjectArgument(
                args, out string argumentError, "componentProperties", "component_properties");
            if (requested == null)
            {
                if (argumentError != null)
                {
                    errors.Add(new JObject { ["componentType"] = "componentProperties", ["error"] = argumentError });
                }

                return 0;
            }

            int appliedTotal = 0;
            foreach (JProperty componentEntry in requested.Properties())
            {
                JObject properties = componentEntry.Value as JObject;
                if (properties == null)
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = componentEntry.Name,
                        ["error"] = "值必须是 { 属性: 值 } 对象。"
                    });
                    continue;
                }

                string componentError;
                Component component = CucliObjectResolver.ResolveComponent(
                    gameObject, componentEntry.Name, out componentError);
                if (componentError != null)
                {
                    errors.Add(new JObject
                    {
                        ["componentType"] = componentEntry.Name,
                        ["error"] = componentError
                    });
                    continue;
                }

                var applied = new JArray();
                var propertyErrors = new JArray();
                ApplyPropertiesToTarget(component, properties, applied, propertyErrors);
                appliedTotal += applied.Count;

                foreach (JToken propertyError in propertyErrors)
                {
                    var entry = propertyError as JObject;
                    if (entry == null) continue;
                    entry["componentType"] = componentEntry.Name;
                    errors.Add(entry);
                }
            }

            return appliedTotal;
        }

        /// <summary>Reads an object argument that may also arrive as a JSON string.</summary>
        private static JObject ReadObjectArgument(
            CucliArgs args, out string error, params string[] names)
        {
            error = null;
            foreach (string name in names)
            {
                JToken token = args.Token(name);
                if (token == null || token.Type == JTokenType.Null) continue;

                if (token.Type == JTokenType.Object) return (JObject)token;

                if (token.Type == JTokenType.String)
                {
                    string text = token.Value<string>();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    try
                    {
                        return JObject.Parse(text);
                    }
                    catch (Newtonsoft.Json.JsonException exception)
                    {
                        error = "参数 '" + name + "' 不是合法 JSON：" + exception.Message;
                        return null;
                    }
                }

                error = "参数 '" + name + "' 必须是 JSON 对象或 JSON 字符串。";
                return null;
            }

            return null;
        }

        // ── shared helpers ─────────────────────────────────────────────────────

        private static JObject DescribeGameObject(GameObject gameObject)
        {
            Transform transform = gameObject.transform;

            var result = new JObject
            {
                ["name"] = gameObject.name,
                ["instanceID"] = gameObject.GetInstanceID(),
                ["tag"] = SafeTag(gameObject),
                ["layer"] = gameObject.layer,
                ["layerName"] = LayerMask.LayerToName(gameObject.layer),
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["isStatic"] = gameObject.isStatic,
                ["sceneName"] = gameObject.scene.IsValid() ? (gameObject.scene.name ?? string.Empty) : string.Empty,
                ["scenePath"] = gameObject.scene.IsValid() ? (gameObject.scene.path ?? string.Empty) : string.Empty,
                ["path"] = CucliObjectResolver.HierarchyPath(gameObject, true),
                ["transform"] = new JObject
                {
                    ["position"] = ToArray(transform.position),
                    ["localPosition"] = ToArray(transform.localPosition),
                    ["rotation"] = ToArray(transform.eulerAngles),
                    ["localRotation"] = ToArray(transform.localEulerAngles),
                    ["scale"] = ToArray(transform.localScale),
                    ["forward"] = ToArray(transform.forward),
                    ["up"] = ToArray(transform.up),
                    ["right"] = ToArray(transform.right)
                },
                ["componentNames"] = ComponentNames(gameObject)
            };

            if (transform.parent != null)
            {
                result["parentInstanceID"] = transform.parent.gameObject.GetInstanceID();
                result["parentName"] = transform.parent.name;
            }

            return result;
        }

        private static JArray ComponentNames(GameObject gameObject)
        {
            var names = new JArray();
            Component[] components;
            try
            {
                components = gameObject.GetComponents<Component>();
            }
            catch (Exception)
            {
                return names;
            }

            foreach (Component component in components)
            {
                names.Add(component == null ? "<missing script>" : component.GetType().Name);
            }

            return names;
        }

        private static JArray ToArray(Vector3 value)
        {
            return new JArray(value.x, value.y, value.z);
        }

        private static bool TryReadVector3(JToken token, out Vector3 value)
        {
            value = Vector3.zero;

            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                if (array.Count < 3) return false;

                var components = new float[3];
                for (int index = 0; index < 3; index++)
                {
                    if (!float.TryParse(
                            array[index].ToString(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out components[index]))
                    {
                        return false;
                    }
                }

                value = new Vector3(components[0], components[1], components[2]);
                return true;
            }

            if (token.Type == JTokenType.Object)
            {
                var args = new CucliArgs((JObject)token);
                if (!args.Has("x") || !args.Has("y") || !args.Has("z")) return false;
                value = new Vector3(args.GetFloat("x"), args.GetFloat("y"), args.GetFloat("z"));
                return true;
            }

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>();
                if (string.IsNullOrWhiteSpace(text)) return false;

                string[] parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) return false;

                var components = new float[3];
                for (int index = 0; index < 3; index++)
                {
                    if (!float.TryParse(
                            parts[index], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out components[index]))
                    {
                        return false;
                    }
                }

                value = new Vector3(components[0], components[1], components[2]);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves a payload value naming a GameObject: a string name / hierarchy path / instance
        /// id, or an object carrying instanceId / path / name. Null and "" mean "no object".
        /// </summary>
        private static GameObject ResolveObjectToken(JToken token, out string error)
        {
            error = null;
            if (token == null || token.Type == JTokenType.Null) return null;

            if (token.Type == JTokenType.Integer)
            {
                GameObject byId = EditorUtility.InstanceIDToObject(token.Value<int>()) as GameObject;
                if (byId == null) error = "instance id " + token.Value<int>() + " 不是已加载的 GameObject。";
                return byId;
            }

            if (token.Type == JTokenType.Object)
            {
                var args = new CucliArgs((JObject)token);
                if (args.Has("instanceId") || args.Has("instance_id"))
                {
                    int id = args.GetInt("instanceId", args.GetInt("instance_id", 0));
                    GameObject byId = EditorUtility.InstanceIDToObject(id) as GameObject;
                    if (byId == null) error = "instance id " + id + " 不是已加载的 GameObject。";
                    return byId;
                }

                string inner = args.GetString("path") ?? args.GetString("hierarchyPath")
                    ?? args.GetString("hierarchy_path") ?? args.GetString("name")
                    ?? args.GetString("target");
                if (string.IsNullOrWhiteSpace(inner))
                {
                    error = "对象引用需要 instanceId、path、hierarchyPath 或 name 之一。";
                    return null;
                }

                return ResolveObjectToken(new JValue(inner), out error);
            }

            string value = token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Newtonsoft.Json.Formatting.None);
            if (string.IsNullOrWhiteSpace(value)) return null;

            string resolveError;
            GameObject match = CucliObjectResolver.ResolveGameObject(
                new CucliArgs(new JObject { ["target"] = value.Trim() }), out resolveError);
            if (match == null) error = resolveError ?? ("找不到目标物体 '" + value + "'。");
            return match;
        }

        private static bool IsEmptyValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type != JTokenType.String) return false;
            return string.IsNullOrWhiteSpace(token.Value<string>());
        }

        private static bool IsDescendantOf(Transform candidate, Transform ancestor)
        {
            Transform current = candidate;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = current.parent;
            }

            return false;
        }

        private static bool TryApplyTag(GameObject gameObject, string tag, out string error)
        {
            error = null;
            string trimmed = tag.Trim();

            try
            {
                if (Array.IndexOf(UnityEditorInternal.InternalEditorUtility.tags, trimmed) < 0)
                {
                    UnityEditorInternal.InternalEditorUtility.AddTag(trimmed);
                }

                gameObject.tag = trimmed;
                return true;
            }
            catch (Exception exception)
            {
                error = "无法设置 tag '" + trimmed + "'：" + exception.GetBaseException().Message;
                return false;
            }
        }

        private static int TryResolveLayer(string value, out string error)
        {
            error = null;
            string trimmed = value.Trim();

            int numeric;
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                if (numeric < 0 || numeric > 31)
                {
                    error = "层索引必须在 0-31 之间，收到 " + numeric + "。";
                    return 0;
                }

                return numeric;
            }

            int byName = LayerMask.NameToLayer(trimmed);
            if (byName < 0)
            {
                error = "不存在名为 '" + trimmed + "' 的层。可用层名："
                    + string.Join(", ", UsableLayerNames()) + "。";
                return 0;
            }

            return byName;
        }

        private static List<string> UsableLayerNames()
        {
            var names = new List<string>();
            for (int index = 0; index < 32; index++)
            {
                string name = LayerMask.LayerToName(index);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }

            return names;
        }

        private static string SafeTag(GameObject gameObject)
        {
            try
            {
                return gameObject.tag;
            }
            catch (UnityException)
            {
                return string.Empty;
            }
        }

        private static void MarkDirty(GameObject gameObject)
        {
            if (gameObject == null) return;
            if (gameObject.scene.IsValid() && gameObject.scene.isLoaded)
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
        }

        /// <summary>
        /// MCP uses a plain integer cursor; <see cref="CucliPaging"/> uses an opaque base64 token.
        /// Integer cursors are rewritten into the opaque form so both spellings work.
        /// </summary>
        private static CucliArgs WithNumericCursor(CucliArgs args)
        {
            string cursor = args.GetString("cursor");
            if (string.IsNullOrWhiteSpace(cursor)) return args;

            int numeric;
            if (!int.TryParse(cursor.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                return args;
            }

            var clone = (JObject)args.Raw.DeepClone();
            clone["cursor"] = CucliPaging.EncodeCursor(numeric);
            return new CucliArgs(clone);
        }
    }
}
