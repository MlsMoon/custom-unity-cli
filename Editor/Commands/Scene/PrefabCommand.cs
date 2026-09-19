using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli prefab ...</c> - prefab assets from the CLI: creating a prefab from a scene object,
    /// inspecting it, and editing its contents.
    ///
    /// All content edits go through the <see cref="PrefabUtility"/> transaction
    /// (<c>LoadPrefabContents</c> / <c>SaveAsPrefabAsset</c> / <c>UnloadPrefabContents</c>) or the
    /// Prefab Stage. Nothing here reaches into a prefab with <c>AssetDatabase</c> directly, because
    /// that bypasses prefab semantics and corrupts nested prefabs and variants.
    ///
    /// Prefab-internal paths are always written as <c>A/B/C</c> relative to the prefab root.
    /// </summary>
    internal sealed class PrefabCommand : CucliCommand
    {
        private const int DefaultHierarchyPageSize = 100;

        public override string Name => "prefab";

        public override string Summary =>
            "Prefab 资产：从场景物体创建、读取信息与层级、在隔离上下文中修改内容，以及 Prefab Stage 的打开/保存/关闭。";

        public override CucliActionInfo[] Actions => Declare(
            "create_from_gameobject", "把场景或 Prefab Stage 中的物体保存为 prefab 资产（需要 force 才能覆盖已存在资产）。",
            "get_info", "读取 prefab 资产信息：guid、类型、根物体、组件类型、子物体数、是否 Variant。",
            "get_hierarchy", "分页列出 prefab 内部的全部节点（路径为 A/B/C 形式）。",
            "modify_contents", "在 LoadPrefabContents 事务中修改 prefab 内容：改名、transform、父节点、组件、增删子物体。",
            "open_prefab_stage", "在 Prefab Stage（可交互编辑态）中打开 prefab。",
            "save_prefab_stage", "保存当前 Prefab Stage 的内容。",
            "close_prefab_stage", "退出 Prefab Stage，可选先保存。");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "project.assets.read", "project.assets.write", "editor.scene.write"
        };

        public override bool MayReloadDomain => false;

        public override string OptionalPackage => null;

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""create_from_gameobject"", ""get_info"", ""get_hierarchy"", ""modify_contents"",
                 ""open_prefab_stage"", ""save_prefab_stage"", ""close_prefab_stage""],
      ""default"": ""create_from_gameobject""
    },
    ""prefabPath"": { ""type"": ""string"", ""description"": ""prefab 资产路径，必须位于 Assets/ 下；path 为别名。"" },
    ""target"": { ""type"": ""string"", ""description"": ""create 时是场景/Prefab Stage 内的源物体；modify 时是 prefab 内部 A/B/C 路径或名称。"" },
    ""name"": { ""type"": ""string"", ""description"": ""create 时是 target 的别名；modify 时是重命名的新名。"" },
    ""searchInactive"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=create_from_gameobject：源物体查找是否包含 inactive。"" },
    ""allowOverwrite"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=create_from_gameobject：force 的 MCP 别名。"" },
    ""unlinkIfInstance"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=create_from_gameobject：先 UnpackPrefabInstance(Completely)。"" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=create_from_gameobject：覆盖已存在的 prefab 资产。"" },
    ""setActive"": { ""type"": ""boolean"", ""description"": ""action=modify_contents：设置 activeSelf。"" },
    ""tag"": { ""type"": ""string"" },
    ""layer"": { ""type"": ""string"", ""description"": ""层名。"" },
    ""position"": { ""description"": ""action=modify_contents / createChild：localPosition [x,y,z]。"" },
    ""rotation"": { ""description"": ""action=modify_contents / createChild：localEulerAngles [x,y,z]。"" },
    ""scale"": { ""description"": ""action=modify_contents / createChild：localScale [x,y,z]。"" },
    ""parent"": { ""type"": ""string"", ""description"": ""action=modify_contents：prefab 内新父物体名/路径；空串表示置根。"" },
    ""componentsToAdd"": { ""type"": ""array"", ""items"": {}, ""description"": ""元素为类型名或 { typeName, properties }。"" },
    ""componentsToRemove"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""componentProperties"": { ""description"": ""{ 组件名: { 属性: 值 } }；任一失败则整体不保存。"" },
    ""createChild"": { ""description"": ""子物体定义对象或对象数组（name / parent / sourcePrefabPath / primitiveType / transform / componentsToAdd / tag / layer / setActive）。"" },
    ""deleteChild"": { ""description"": ""相对 target 的子物体路径：字符串、字符串数组或 { name }。"" },
    ""saveBeforeClose"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=close_prefab_stage：关闭前先保存。"" },
    ""pageSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""cursor"": { ""type"": [""string"", ""integer""], ""description"": ""0 基游标。"" },
    ""pageNumber"": { ""type"": ""integer"", ""minimum"": 1 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "create_from_gameobject");
            switch (action)
            {
                case "create_from_gameobject": return CreateFromGameObject(context);
                case "get_info": return GetInfo(context);
                case "get_hierarchy": return GetHierarchy(context);
                case "modify_contents": return ModifyContents(context);
                case "open_prefab_stage": return OpenPrefabStage(context);
                case "save_prefab_stage": return SavePrefabStage(context);
                case "close_prefab_stage": return ClosePrefabStage(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ── create_from_gameobject ─────────────────────────────────────────────

        private static CucliCommandResult CreateFromGameObject(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string requestedPath = args.GetString("prefabPath") ?? args.GetString("prefab_path")
                ?? args.GetString("path");
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return CucliCommandResult.Missing(
                    "prefabPath", "示例：--prefab-path Assets/Prefabs/Ball.prefab --target Ball");
            }

            string assetPath;
            string pathError;
            if (!TryResolvePrefabPath(requestedPath, true, out assetPath, out pathError))
            {
                return CucliCommandResult.Invalid("prefabPath", pathError);
            }

            string targetName = args.GetString("target") ?? args.GetString("name");
            if (string.IsNullOrWhiteSpace(targetName))
            {
                return CucliCommandResult.Missing(
                    "target", "需要 target（场景或 Prefab Stage 中源物体的名称 / 路径 / instanceID）。");
            }

            string resolveError;
            GameObject source = CucliObjectResolver.ResolveGameObject(args, out resolveError);
            if (resolveError != null || source == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    resolveError ?? ("找不到源物体 '" + targetName + "'。"));
            }

            if (args.Token("searchInactive") != null || args.Token("search_inactive") != null)
            {
                if (!args.GetBool("searchInactive", args.GetBool("search_inactive", false)))
                {
                    context.Warn(
                        "searchInactive=false 不会过滤 inactive：CucliObjectResolver 始终包含 inactive 物体，"
                        + "源物体查找结果可能包含未激活对象。");
                }
            }

            if (PrefabUtility.IsPartOfPrefabAsset(source))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "'" + source.name + "' 属于 prefab 资产本体，请用 open_prefab_stage + modify_contents 修改。",
                    new JObject { ["target"] = source.name });
            }

            bool unlinkIfInstance = args.GetBool(
                "unlinkIfInstance", args.GetBool("unlink_if_instance", false));
            bool wasUnlinked = false;

            PrefabInstanceStatus status = PrefabUtility.GetPrefabInstanceStatus(source);
            if (status != PrefabInstanceStatus.NotAPrefab)
            {
                string existingPrefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
                if (!unlinkIfInstance)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.ConfirmationRequired,
                        "'" + source.name + "' 已经是 prefab '" + existingPrefab + "' 的实例。"
                        + "请传 unlinkIfInstance: true 先解链，或直接修改原 prefab。",
                        new JObject { ["existingPrefabPath"] = existingPrefab });
                }

                PrefabUtility.UnpackPrefabInstance(
                    source, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                wasUnlinked = true;
            }

            bool force = args.GetBool("force", false)
                || args.GetBool("allowOverwrite", args.GetBool("allow_overwrite", false));
            bool exists = CucliAssets.Exists(assetPath);

            if (exists && !force)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "prefab 已存在：" + assetPath + "。请换一个路径，或显式传 force: true 覆盖。",
                    new JObject { ["prefabPath"] = assetPath });
            }

            string folder = CucliAssets.ParentFolder(assetPath);
            if (string.IsNullOrEmpty(folder) || !CucliAssets.EnsureFolder(folder))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "无法创建 prefab 目录：" + (folder ?? assetPath) + "。");
            }

            string instanceName = source.name;
            int instanceId = source.GetInstanceID();
            int componentCount = 0;
            Component[] components = source.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                if (components[index] != null) componentCount++;
            }

            int childCount = source.transform.childCount;

            GameObject created = PrefabUtility.SaveAsPrefabAssetAndConnect(
                source, assetPath, InteractionMode.AutomatedAction);
            if (created == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "保存 prefab 失败：" + assetPath + "。请检查资产目录是否可写。",
                    new JObject { ["prefabPath"] = assetPath });
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int runtimeOnlyMaterials = CountRuntimeOnlyMaterials(source);
            if (runtimeOnlyMaterials > 0)
            {
                context.Warn(
                    "源物体上有 " + runtimeOnlyMaterials + " 个材质槽引用的是运行时/内存材质（不属于项目资产），"
                    + "prefab 里这些槽位会丢失引用，请把它们先另存为材质资产。");
            }

            var data = new JObject
            {
                ["prefabPath"] = assetPath,
                ["instanceId"] = instanceId,
                ["instanceName"] = instanceName,
                ["wasUnlinked"] = wasUnlinked,
                ["wasReplaced"] = exists,
                ["componentCount"] = componentCount,
                ["childCount"] = childCount,
                ["materialsPersisted"] = CountPersistedMaterials(source),
                ["materialsNotPersisted"] = runtimeOnlyMaterials
            };

            return CucliCommandResult.Ok(
                data, (exists ? "已覆盖 prefab " : "已创建 prefab ") + assetPath + "。");
        }

        private static int CountRuntimeOnlyMaterials(GameObject gameObject)
        {
            int count = 0;
            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                if (materials == null) continue;

                foreach (Material material in materials)
                {
                    if (material != null && !AssetDatabase.Contains(material)) count++;
                }
            }

            return count;
        }

        private static int CountPersistedMaterials(GameObject gameObject)
        {
            int count = 0;
            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                if (materials == null) continue;

                foreach (Material material in materials)
                {
                    if (material != null && AssetDatabase.Contains(material)) count++;
                }
            }

            return count;
        }

        // ── get_info / get_hierarchy ───────────────────────────────────────────

        private static CucliCommandResult GetInfo(CucliCommandContext context)
        {
            string assetPath;
            CucliCommandResult failure;
            GameObject root;
            if (!TryLoadPrefabAsset(context, out assetPath, out root, out failure)) return failure;

            PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(root);
            string parentPrefab = null;

            try
            {
                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(root);
                if (source != null && source != root)
                {
                    string sourcePath = CucliAssets.GetAssetPath(source);
                    if (!string.IsNullOrEmpty(sourcePath)
                        && !string.Equals(sourcePath, assetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        parentPrefab = sourcePath;
                    }
                }
            }
            catch (Exception)
            {
                parentPrefab = null;
            }

            var componentTypes = new JArray();
            Component[] components = root.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                componentTypes.Add(components[index] == null ? "<missing script>" : components[index].GetType().Name);
            }

            var data = new JObject
            {
                ["assetPath"] = assetPath,
                ["guid"] = CucliAssets.AssetPathToGuid(assetPath),
                ["prefabType"] = prefabType.ToString(),
                ["rootObjectName"] = root.name,
                ["rootComponentTypes"] = componentTypes,
                ["childCount"] = root.transform.childCount,
                ["isVariant"] = prefabType == PrefabAssetType.Variant,
                ["isParentPrefabResolved"] = parentPrefab != null
            };

            if (parentPrefab != null) data["parentPrefab"] = parentPrefab;

            return CucliCommandResult.Ok(
                data, "已读取 prefab " + assetPath + "（类型 " + prefabType + "）。");
        }

        private static CucliCommandResult GetHierarchy(CucliCommandContext context)
        {
            string assetPath;
            CucliCommandResult failure;
            GameObject root;
            if (!TryLoadPrefabAsset(context, out assetPath, out root, out failure)) return failure;

            CucliArgs args = context.Input;

            // LoadPrefabContents puts the prefab in its own preview scene, so this walk never leaks
            // into the user's open scenes - and UnloadPrefabContents must always run.
            GameObject contents = null;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(assetPath);
                if (contents == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "LoadPrefabContents 失败：" + assetPath + "。",
                        new JObject { ["prefabPath"] = assetPath });
                }

                var nodes = new List<JObject>();
                Transform[] transforms = contents.GetComponentsInChildren<Transform>(true);
                foreach (Transform transform in transforms)
                {
                    context.ThrowIfCancelled();
                    nodes.Add(DescribePrefabNode(transform, contents, assetPath));
                }

                CucliPage<JObject> page = CucliPaging.Slice(
                    nodes, WithNumericCursor(args), DefaultHierarchyPageSize);

                var items = new JArray();
                foreach (JObject node in page.Items) items.Add(node);

                var payload = new JObject
                {
                    ["prefabPath"] = assetPath,
                    ["guid"] = CucliAssets.AssetPathToGuid(assetPath),
                    ["cursor"] = page.Offset
                };

                JObject envelope = CucliPaging.Envelope(page, items, payload);
                if (page.HasMore) envelope["next_cursor"] = page.NextCursor;

                return CucliCommandResult.Ok(
                    envelope,
                    "已返回 " + page.Items.Count + "/" + page.Total + " 个 prefab 节点。");
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "读取 prefab 层级失败：" + exception.GetBaseException().Message,
                    new JObject { ["prefabPath"] = assetPath });
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static JObject DescribePrefabNode(Transform transform, GameObject root, string assetPath)
        {
            GameObject gameObject = transform.gameObject;
            bool isRoot = transform == root.transform;

            var componentTypes = new JArray();
            Component[] components = gameObject.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                componentTypes.Add(components[index] == null ? "<missing script>" : components[index].GetType().Name);
            }

            bool isNestedRoot = false;
            string nestedAssetPath = null;
            string parentPath = null;
            int nestingDepth = 0;

            try
            {
                isNestedRoot = !isRoot && PrefabUtility.IsAnyPrefabInstanceRoot(gameObject);
                if (isNestedRoot)
                {
                    nestedAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);

                    Transform current = transform;
                    while (current != null && current != root.transform)
                    {
                        if (PrefabUtility.IsAnyPrefabInstanceRoot(current.gameObject)) nestingDepth++;
                        current = current.parent;
                    }

                    if (transform.parent != null)
                    {
                        parentPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(
                            transform.parent.gameObject);
                    }
                }
            }
            catch (Exception)
            {
                isNestedRoot = false;
            }

            var prefabInfo = new JObject
            {
                ["isRoot"] = isRoot,
                ["isNestedRoot"] = isNestedRoot,
                ["nestingDepth"] = nestingDepth
            };

            string ownerAssetPath = isRoot ? assetPath : nestedAssetPath;
            if (!string.IsNullOrEmpty(ownerAssetPath)) prefabInfo["assetPath"] = ownerAssetPath;

            if (parentPath != null) prefabInfo["parentPath"] = parentPath;

            var item = new JObject
            {
                ["name"] = gameObject.name,
                ["instanceId"] = gameObject.GetInstanceID(),
                ["path"] = PrefabNodePath(transform, root.transform),
                ["activeSelf"] = gameObject.activeSelf,
                ["tag"] = SafeTag(gameObject),
                ["layer"] = gameObject.layer,
                ["childCount"] = transform.childCount,
                ["componentTypes"] = componentTypes,
                ["prefab"] = prefabInfo
            };

            return item;
        }

        /// <summary>Prefab-internal path in <c>A/B/C</c> form, relative to the prefab root.</summary>
        private static string PrefabNodePath(Transform transform, Transform root)
        {
            if (transform == root) return root.name;

            var segments = new List<string>();
            Transform current = transform;
            while (current != null && current != root)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            return segments.Count == 0 ? root.name : string.Join("/", segments);
        }

        // ── modify_contents ────────────────────────────────────────────────────

        private static CucliCommandResult ModifyContents(CucliCommandContext context)
        {
            string assetPath;
            CucliCommandResult failure;
            GameObject asset;
            if (!TryLoadPrefabAsset(context, out assetPath, out asset, out failure)) return failure;

            CucliArgs args = context.Input;
            string targetSpec = args.GetString("target");

            GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
            if (contents == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "LoadPrefabContents 失败：" + assetPath + "。",
                    new JObject { ["prefabPath"] = assetPath });
            }

            try
            {
                string findError;
                GameObject target = FindInPrefabContents(contents, targetSpec, out findError);
                if (target == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        findError ?? ("在 prefab '" + assetPath + "' 中找不到 target。"),
                        new JObject
                        {
                            ["prefabPath"] = assetPath,
                            ["target"] = targetSpec ?? string.Empty
                        });
                }

                bool modified = false;
                var errors = new JArray();

                string newName = args.GetString("name");
                if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName.Trim(), target.name, StringComparison.Ordinal))
                {
                    target.name = newName.Trim();
                    modified = true;
                }

                if (args.Token("setActive") != null || args.Token("set_active") != null)
                {
                    target.SetActive(args.GetBool("setActive", args.GetBool("set_active", true)));
                    modified = true;
                }

                string tag = args.GetString("tag");
                if (args.Token("tag") != null)
                {
                    string tagError;
                    if (!TryApplyTag(target, tag, out tagError))
                    {
                        return CucliCommandResult.Fail(CucliErrors.InvalidParameter, tagError);
                    }

                    modified = true;
                }

                string layerName = args.GetString("layer") ?? args.GetString("layerName")
                    ?? args.GetString("layer_name");
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    string layerError;
                    int layer = TryResolveLayer(layerName, out layerError);
                    if (layerError != null)
                    {
                        return CucliCommandResult.Fail(CucliErrors.InvalidParameter, layerError);
                    }

                    target.layer = layer;
                    modified = true;
                }

                if (args.Token("position") != null)
                {
                    target.transform.localPosition = args.GetVector3("position");
                    modified = true;
                }

                if (args.Token("rotation") != null)
                {
                    target.transform.localEulerAngles = args.GetVector3("rotation");
                    modified = true;
                }

                if (args.Token("scale") != null)
                {
                    target.transform.localScale = args.GetVector3("scale");
                    modified = true;
                }

                JToken parentToken = args.Token("parent");
                if (parentToken != null)
                {
                    if (parentToken.Type == JTokenType.Null || string.IsNullOrWhiteSpace(args.GetString("parent")))
                    {
                        target.transform.SetParent(null, true);
                        modified = true;
                    }
                    else
                    {
                        string parentError;
                        GameObject parent = FindInPrefabContents(contents, args.GetString("parent"), out parentError);
                        if (parent == null)
                        {
                            return CucliCommandResult.Fail(
                                CucliErrors.TargetNotFound,
                                parentError ?? ("找不到 prefab 内的父物体 '" + args.GetString("parent") + "'。"));
                        }

                        if (parent == target || IsDescendantOf(parent.transform, target.transform))
                        {
                            return CucliCommandResult.Invalid(
                                "parent", "把 '" + target.name + "' 挂到 '" + parent.name + "' 会造成层级环。");
                        }

                        target.transform.SetParent(parent.transform, true);
                        modified = true;
                    }
                }

                JArray addRequests = args.GetArray("componentsToAdd") ?? args.GetArray("components_to_add");
                if (addRequests != null && addRequests.Count > 0)
                {
                    string addError;
                    if (!TryAddComponents(target, addRequests, out addError))
                    {
                        return CucliCommandResult.Fail(CucliErrors.InvalidParameter, addError);
                    }

                    modified = true;
                }

                string[] removeRequests = args.GetStringList("componentsToRemove");
                if (removeRequests.Length == 0) removeRequests = args.GetStringList("components_to_remove");
                if (removeRequests.Length > 0)
                {
                    string removeError;
                    if (!TryRemoveComponents(target, removeRequests, out removeError))
                    {
                        return CucliCommandResult.Fail(CucliErrors.InvalidParameter, removeError);
                    }

                    modified = true;
                }

                JObject componentProperties = ReadObjectArgument(
                    args, out string propertyArgumentError, "componentProperties", "component_properties");
                if (componentProperties == null && propertyArgumentError != null)
                {
                    return CucliCommandResult.Invalid("componentProperties", propertyArgumentError);
                }

                if (componentProperties != null)
                {
                    string propertyError;
                    if (!TryApplyComponentProperties(target, componentProperties, errors, out propertyError))
                    {
                        // MCP 语义：任一属性失败则整体不保存。
                        return CucliCommandResult.Fail(
                            CucliErrors.InvalidParameter,
                            "componentProperties 写入失败，已放弃保存：" + propertyError,
                            new JObject
                            {
                                ["prefabPath"] = assetPath,
                                ["errors"] = errors
                            });
                    }

                    modified = true;
                }

                JToken createChildToken = args.Token("createChild") ?? args.Token("create_child");
                if (createChildToken != null && createChildToken.Type != JTokenType.Null)
                {
                    string childError;
                    if (!TryCreateChildren(
                            contents, target, assetPath, createChildToken, errors, out childError))
                    {
                        return CucliCommandResult.Fail(
                            CucliErrors.InvalidParameter,
                            "createChild 失败，已放弃保存：" + childError,
                            new JObject { ["prefabPath"] = assetPath, ["errors"] = errors });
                    }

                    modified = true;
                }

                JToken deleteChildToken = args.Token("deleteChild") ?? args.Token("delete_child");
                if (deleteChildToken != null && deleteChildToken.Type != JTokenType.Null)
                {
                    string deleteError;
                    if (!TryDeleteChildren(target, deleteChildToken, out deleteError))
                    {
                        return CucliCommandResult.Fail(
                            CucliErrors.TargetNotFound,
                            "deleteChild 失败，已放弃保存：" + deleteError,
                            new JObject { ["prefabPath"] = assetPath });
                    }

                    modified = true;
                }

                if (!modified)
                {
                    return CucliCommandResult.Ok(
                        new JObject
                        {
                            ["prefabPath"] = assetPath,
                            ["targetName"] = target.name,
                            ["modified"] = false
                        },
                        "prefab " + assetPath + " 没有需要应用的改动。");
                }

                bool saved;
                PrefabUtility.SaveAsPrefabAsset(contents, assetPath, out saved);
                if (!saved)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "保存 prefab 失败：" + assetPath + "。请检查资产是否只读或磁盘是否可写。",
                        new JObject { ["prefabPath"] = assetPath });
                }

                AssetDatabase.Refresh();

                Vector3 position = target.transform.localPosition;
                Vector3 rotation = target.transform.localEulerAngles;
                Vector3 scale = target.transform.localScale;

                var componentTypes = new JArray();
                Component[] components = target.GetComponents<Component>();
                for (int index = 0; index < components.Length; index++)
                {
                    componentTypes.Add(components[index] == null ? "<missing script>" : components[index].GetType().Name);
                }

                var data = new JObject
                {
                    ["prefabPath"] = assetPath,
                    ["targetName"] = target.name,
                    ["targetPath"] = PrefabNodePath(target.transform, contents.transform),
                    ["modified"] = true,
                    ["transform"] = new JObject
                    {
                        ["position"] = new JArray(position.x, position.y, position.z),
                        ["rotation"] = new JArray(rotation.x, rotation.y, rotation.z),
                        ["scale"] = new JArray(scale.x, scale.y, scale.z)
                    },
                    ["componentTypes"] = componentTypes
                };

                return CucliCommandResult.Ok(
                    data, "已修改并保存 prefab " + assetPath + "（target " + data["targetPath"] + "）。");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // ── open / save / close prefab stage ───────────────────────────────────

        private static CucliCommandResult OpenPrefabStage(CucliCommandContext context)
        {
            if (EditorApplication.isPlaying)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "Prefab Stage 不能在 Play Mode 中打开。请先退出 Play Mode（cucli editor stop）再重试；"
                    + "只读取信息请用 get_info / get_hierarchy，它们不受模式限制。",
                    new JObject { ["isPlaying"] = true });
            }

            CucliArgs args = context.Input;
            string requested = args.GetString("prefabPath") ?? args.GetString("prefab_path")
                ?? args.GetString("path");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing("prefabPath");
            }

            if (!requested.Trim().EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return CucliCommandResult.Invalid(
                    "prefabPath", "open_prefab_stage 要求路径以 .prefab 结尾，收到：" + requested + "。");
            }

            string assetPath;
            string pathError;
            if (!TryResolvePrefabPath(requested, false, out assetPath, out pathError))
            {
                return CucliCommandResult.Invalid("prefabPath", pathError);
            }

            if (!CucliAssets.Exists(assetPath))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "prefab 不存在：" + assetPath + "。",
                    new JObject { ["prefabPath"] = assetPath });
            }

            PrefabStage stage;
            try
            {
                stage = PrefabStageUtility.OpenPrefab(assetPath);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "打开 prefab stage 失败：" + exception.GetBaseException().Message,
                    new JObject { ["prefabPath"] = assetPath });
            }

            if (stage == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "PrefabStageUtility.OpenPrefab 没有返回 stage：" + assetPath + "。",
                    new JObject { ["prefabPath"] = assetPath });
            }

            if (!string.Equals(stage.assetPath, assetPath, StringComparison.OrdinalIgnoreCase))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "打开的 prefab stage 与请求不一致：请求 " + assetPath + "，实际 " + stage.assetPath + "。",
                    new JObject { ["prefabPath"] = assetPath, ["openedPrefabPath"] = stage.assetPath });
            }

            var data = new JObject
            {
                ["prefabPath"] = assetPath,
                ["openedPrefabPath"] = stage.assetPath,
                ["enteredPrefabStage"] = true
            };

            if (stage.prefabContentsRoot != null)
            {
                data["rootName"] = stage.prefabContentsRoot.name;
            }

            return CucliCommandResult.Ok(data, "已在 Prefab Stage 中打开 " + assetPath + "。");
        }

        private static CucliCommandResult SavePrefabStage(CucliCommandContext context)
        {
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "当前不在 prefab 编辑态。请先执行 'cucli prefab open_prefab_stage'。");
            }

            string error;
            string prefabPath;
            if (!TrySaveStage(stage, out prefabPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
            }

            return CucliCommandResult.Ok(
                new JObject { ["prefabPath"] = prefabPath, ["saved"] = true },
                "已保存 Prefab Stage：" + prefabPath + "。");
        }

        private static CucliCommandResult ClosePrefabStage(CucliCommandContext context)
        {
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return CucliCommandResult.Ok(null, "当前不在 prefab 编辑态，无需关闭。");
            }

            string prefabPath = stage.assetPath;
            bool saveBeforeClose = context.Input.GetBool(
                "saveBeforeClose", context.Input.GetBool("save_before_close", false));

            if (saveBeforeClose)
            {
                string error;
                string savedPath;
                if (!TrySaveStage(stage, out savedPath, out error))
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
                }

                prefabPath = savedPath;
            }

            StageUtility.GoToMainStage();

            return CucliCommandResult.Ok(
                new JObject { ["prefabPath"] = prefabPath, ["savedBeforeClose"] = saveBeforeClose },
                "已退出 Prefab Stage（" + prefabPath + "）。");
        }

        private static bool TrySaveStage(PrefabStage stage, out string prefabPath, out string error)
        {
            prefabPath = stage.assetPath;
            error = null;

            if (stage.prefabContentsRoot == null)
            {
                error = "保存 Prefab Stage 失败：prefab contents root 缺失（" + prefabPath + "）。";
                return false;
            }

            bool saved;
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, prefabPath, out saved);
            if (!saved)
            {
                error = "保存 Prefab Stage 失败：" + prefabPath + "。请检查资产是否只读或磁盘是否可写。";
                return false;
            }

            stage.ClearDirtiness();
            AssetDatabase.Refresh();
            return true;
        }

        // ── prefab content helpers ─────────────────────────────────────────────

        /// <summary>
        /// Locates a node inside loaded prefab contents: empty means the root, a value containing
        /// '/' is a path from the root (an optional leading root name is accepted), anything else is
        /// a depth-first name match.
        /// </summary>
        private static GameObject FindInPrefabContents(GameObject root, string target, out string error)
        {
            error = null;
            if (root == null)
            {
                error = "prefab contents 为空。";
                return null;
            }

            if (string.IsNullOrWhiteSpace(target)) return root;

            string spec = target.Trim().Replace('\\', '/').Trim('/');
            string[] segments = spec.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return root;

            if (segments.Length == 1)
            {
                if (string.Equals(segments[0], root.name, StringComparison.Ordinal)) return root;
                return FindDescendantByName(root, segments[0]);
            }

            int start = string.Equals(segments[0], root.name, StringComparison.Ordinal) ? 1 : 0;
            Transform current = root.transform;

            for (int index = start; index < segments.Length; index++)
            {
                Transform next = null;
                for (int child = 0; child < current.childCount; child++)
                {
                    Transform candidate = current.GetChild(child);
                    if (string.Equals(candidate.name, segments[index], StringComparison.Ordinal))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next == null)
                {
                    error = "prefab 内没有路径 '" + string.Join("/", segments) + "'（在 '" + segments[index] + "' 处断开）。";
                    return null;
                }

                current = next;
            }

            return current.gameObject;
        }

        private static GameObject FindDescendantByName(GameObject root, string name)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform transform in transforms)
            {
                if (string.Equals(transform.name, name, StringComparison.Ordinal)) return transform.gameObject;
            }

            return null;
        }

        private static bool TryAddComponents(GameObject target, JArray requests, out string error)
        {
            error = null;
            foreach (JToken element in requests)
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
                    error = "componentsToAdd 的元素需要类型名，或 { typeName, properties } 对象。";
                    return false;
                }

                string typeError;
                Type type = CucliReflection.FindType(typeName, out typeError);
                if (type == null)
                {
                    error = typeError;
                    return false;
                }

                if (!typeof(Component).IsAssignableFrom(type))
                {
                    error = "'" + typeName + "' 不是 Component 类型。";
                    return false;
                }

                if (type == typeof(Transform) || type == typeof(RectTransform))
                {
                    error = "Transform / RectTransform 已存在，不能重复添加。";
                    return false;
                }

                Component added;
                try
                {
                    added = target.AddComponent(type);
                }
                catch (Exception exception)
                {
                    error = "添加组件 '" + typeName + "' 失败：" + exception.GetBaseException().Message;
                    return false;
                }

                if (added == null)
                {
                    error = "Unity 拒绝给 prefab 内容添加组件 '" + typeName + "'。";
                    return false;
                }

                if (properties == null) continue;

                var applied = new JArray();
                var errors = new JArray();
                ApplyProperties(added, properties, applied, errors);
                if (errors.Count > 0)
                {
                    error = "组件 '" + typeName + "' 的属性写入失败："
                        + ((JObject)errors[0])["error"];
                    return false;
                }
            }

            return true;
        }

        private static bool TryRemoveComponents(GameObject target, string[] typeNames, out string error)
        {
            error = null;
            foreach (string typeName in typeNames)
            {
                if (string.IsNullOrWhiteSpace(typeName)) continue;

                string typeError;
                Type type = CucliReflection.FindType(typeName, out typeError);
                if (type == null)
                {
                    error = typeError;
                    return false;
                }

                if (!typeof(Component).IsAssignableFrom(type))
                {
                    error = "'" + typeName + "' 不是 Component 类型。";
                    return false;
                }

                if (type == typeof(Transform) || type == typeof(RectTransform))
                {
                    error = "不允许移除 Transform / RectTransform。";
                    return false;
                }

                Component component = target.GetComponent(type);
                if (component == null)
                {
                    error = target.name + " 上没有 " + type.Name + " 组件。";
                    return false;
                }

                Object.DestroyImmediate(component);
            }

            return true;
        }

        private static bool TryApplyComponentProperties(
            GameObject target, JObject requested, JArray errors, out string error)
        {
            error = null;
            foreach (JProperty componentEntry in requested.Properties())
            {
                var properties = componentEntry.Value as JObject;
                if (properties == null)
                {
                    error = "'" + componentEntry.Name + "' 的值必须是 { 属性: 值 } 对象。";
                    errors.Add(new JObject { ["componentType"] = componentEntry.Name, ["error"] = error });
                    return false;
                }

                string componentError;
                Component component = CucliObjectResolver.ResolveComponent(
                    target, componentEntry.Name, out componentError);
                if (componentError != null)
                {
                    error = componentError;
                    errors.Add(new JObject { ["componentType"] = componentEntry.Name, ["error"] = error });
                    return false;
                }

                var applied = new JArray();
                var propertyErrors = new JArray();
                ApplyProperties(component, properties, applied, propertyErrors);

                if (propertyErrors.Count > 0)
                {
                    foreach (JToken propertyError in propertyErrors)
                    {
                        var entry = propertyError as JObject;
                        if (entry == null) continue;
                        entry["componentType"] = componentEntry.Name;
                        errors.Add(entry);
                    }

                    JToken firstError = ((JObject)propertyErrors[0])["error"];
                    error = firstError == null ? "属性写入失败。" : firstError.ToString();
                    return false;
                }
            }

            return true;
        }

        private static bool TryCreateChildren(
            GameObject contents,
            GameObject target,
            string assetPath,
            JToken token,
            JArray errors,
            out string error)
        {
            error = null;
            var elements = token.Type == JTokenType.Array ? (JArray)token : new JArray(token);

            foreach (JToken element in elements)
            {
                if (element == null || element.Type == JTokenType.Null) continue;

                var childArgs = new CucliArgs(element as JObject ?? new JObject());
                if (element.Type != JTokenType.Object)
                {
                    error = "createChild 的元素必须是对象。";
                    return false;
                }

                string name = childArgs.GetString("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "createChild 的元素缺少 name。";
                    return false;
                }

                string sourcePrefabPath = childArgs.GetString("sourcePrefabPath")
                    ?? childArgs.GetString("source_prefab_path");
                string primitiveName = childArgs.GetString("primitiveType")
                    ?? childArgs.GetString("primitive_type");

                if (!string.IsNullOrWhiteSpace(sourcePrefabPath) && !string.IsNullOrWhiteSpace(primitiveName))
                {
                    error = "createChild 的 sourcePrefabPath 与 primitiveType 互斥。";
                    return false;
                }

                GameObject created;

                if (!string.IsNullOrWhiteSpace(sourcePrefabPath))
                {
                    string normalizedSource;
                    string sourceError;
                    if (!TryResolvePrefabPath(sourcePrefabPath, false, out normalizedSource, out sourceError))
                    {
                        error = sourceError;
                        return false;
                    }

                    if (string.Equals(normalizedSource, assetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "createChild 不能把 prefab 嵌套到它自己（" + assetPath + "）。";
                        return false;
                    }

                    GameObject sourcePrefab = CucliAssets.Load<GameObject>(normalizedSource);
                    if (sourcePrefab == null)
                    {
                        error = "找不到 sourcePrefabPath：" + normalizedSource + "。";
                        return false;
                    }

                    created = PrefabUtility.InstantiatePrefab(sourcePrefab) as GameObject;
                    if (created == null)
                    {
                        error = "实例化 " + normalizedSource + " 失败。";
                        return false;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(primitiveName))
                {
                    PrimitiveType primitive;
                    if (!TryParsePrimitive(primitiveName, out primitive))
                    {
                        error = "'" + primitiveName + "' 不是合法的 PrimitiveType。";
                        return false;
                    }

                    created = GameObject.CreatePrimitive(primitive);
                }
                else
                {
                    created = new GameObject();
                }

                created.name = name.Trim();

                string parentSpec = childArgs.GetString("parent");
                GameObject parent = target;
                if (!string.IsNullOrWhiteSpace(parentSpec))
                {
                    string parentError;
                    parent = FindInPrefabContents(contents, parentSpec, out parentError);
                    if (parent == null)
                    {
                        error = parentError ?? ("找不到 createChild 的父物体 '" + parentSpec + "'。");
                        Object.DestroyImmediate(created);
                        return false;
                    }
                }

                created.transform.SetParent(parent.transform, false);

                if (childArgs.Token("position") != null)
                {
                    created.transform.localPosition = childArgs.GetVector3("position");
                }

                if (childArgs.Token("rotation") != null)
                {
                    created.transform.localEulerAngles = childArgs.GetVector3("rotation");
                }

                if (childArgs.Token("scale") != null)
                {
                    created.transform.localScale = childArgs.GetVector3("scale");
                }

                if (childArgs.Token("setActive") != null || childArgs.Token("set_active") != null)
                {
                    created.SetActive(childArgs.GetBool("setActive", childArgs.GetBool("set_active", true)));
                }

                JToken childTag = childArgs.Token("tag");
                if (childTag != null && childTag.Type != JTokenType.Null)
                {
                    string tagError;
                    if (!TryApplyTag(created, childArgs.GetString("tag"), out tagError))
                    {
                        error = tagError;
                        Object.DestroyImmediate(created);
                        return false;
                    }
                }

                string childLayer = childArgs.GetString("layer") ?? childArgs.GetString("layerName")
                    ?? childArgs.GetString("layer_name");
                if (!string.IsNullOrWhiteSpace(childLayer))
                {
                    string layerError;
                    int layer = TryResolveLayer(childLayer, out layerError);
                    if (layerError != null)
                    {
                        error = layerError;
                        Object.DestroyImmediate(created);
                        return false;
                    }

                    created.layer = layer;
                }

                JArray childComponents = childArgs.GetArray("componentsToAdd")
                    ?? childArgs.GetArray("components_to_add");
                if (childComponents != null && childComponents.Count > 0)
                {
                    string componentError;
                    if (!TryAddComponents(created, childComponents, out componentError))
                    {
                        error = componentError;
                        Object.DestroyImmediate(created);
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryDeleteChildren(GameObject target, JToken token, out string error)
        {
            error = null;
            var specs = new List<string>();

            if (token.Type == JTokenType.Array)
            {
                foreach (JToken element in (JArray)token)
                {
                    if (element.Type == JTokenType.String) specs.Add(element.Value<string>());
                    else if (element.Type == JTokenType.Object)
                    {
                        string name = new CucliArgs((JObject)element).GetString("name");
                        if (!string.IsNullOrWhiteSpace(name)) specs.Add(name);
                    }
                }
            }
            else if (token.Type == JTokenType.String)
            {
                specs.Add(token.Value<string>());
            }
            else if (token.Type == JTokenType.Object)
            {
                string name = new CucliArgs((JObject)token).GetString("name");
                if (!string.IsNullOrWhiteSpace(name)) specs.Add(name);
            }

            if (specs.Count == 0)
            {
                error = "deleteChild 需要字符串、字符串数组或 { name }。";
                return false;
            }

            foreach (string spec in specs)
            {
                GameObject child = FindInPrefabContents(target, spec, out string findError);
                if (child == null || child == target)
                {
                    error = findError ?? ("找不到要删除的子物体 '" + spec + "'。");
                    return false;
                }

                Object.DestroyImmediate(child);
            }

            return true;
        }

        private static void ApplyProperties(
            Object target, JObject properties, JArray applied, JArray errors)
        {
            foreach (JProperty property in properties.Properties())
            {
                string memberPath = property.Name;
                string error;
                bool handled;

                if (CucliSerialized.TrySet(
                        target, memberPath, property.Value,
                        "cucli prefab set " + memberPath, out error, out handled))
                {
                    applied.Add(memberPath);
                    continue;
                }

                if (handled)
                {
                    errors.Add(new JObject { ["property"] = memberPath, ["error"] = error });
                    continue;
                }

                if (!CucliReflection.TrySetPath(target, memberPath, property.Value, out error))
                {
                    errors.Add(new JObject { ["property"] = memberPath, ["error"] = error });
                    continue;
                }

                EditorUtility.SetDirty(target);
                applied.Add(memberPath);
            }
        }

        // ── shared helpers ─────────────────────────────────────────────────────

        private static bool TryLoadPrefabAsset(
            CucliCommandContext context,
            out string assetPath,
            out GameObject root,
            out CucliCommandResult failure)
        {
            assetPath = null;
            root = null;
            failure = null;

            CucliArgs args = context.Input;
            string requested = args.GetString("prefabPath") ?? args.GetString("prefab_path")
                ?? args.GetString("path");
            if (string.IsNullOrWhiteSpace(requested))
            {
                failure = CucliCommandResult.Missing(
                    "prefabPath", "示例：--prefab-path Assets/Prefabs/Ball.prefab");
                return false;
            }

            string pathError;
            if (!TryResolvePrefabPath(requested, true, out assetPath, out pathError))
            {
                failure = CucliCommandResult.Invalid("prefabPath", pathError);
                return false;
            }

            if (!CucliAssets.Exists(assetPath))
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "prefab 不存在：" + assetPath + "。",
                    new JObject { ["prefabPath"] = assetPath });
                return false;
            }

            root = CucliAssets.Load<GameObject>(assetPath);
            if (root == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    assetPath + " 不是可加载的 prefab GameObject 资产。",
                    new JObject { ["prefabPath"] = assetPath });
                return false;
            }

            return true;
        }

        /// <summary>
        /// Normalises a prefab asset path: must resolve inside the project and live under
        /// <c>Assets/</c> (a prefab cannot be written into a package), with a <c>.prefab</c> suffix.
        /// </summary>
        private static bool TryResolvePrefabPath(
            string requested, bool appendExtension, out string assetPath, out string error)
        {
            assetPath = null;
            error = null;

            string normalized = CucliAssets.NormalizeAssetPath(requested);
            if (normalized == null)
            {
                error = "路径非法或越界（不允许路径穿越）：'" + requested + "'。";
                return false;
            }

            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = "prefab 路径必须位于 Assets/ 下，收到：'" + normalized + "'。";
                return false;
            }

            if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (!appendExtension)
                {
                    error = "路径必须以 .prefab 结尾，收到：'" + normalized + "'。";
                    return false;
                }

                normalized += ".prefab";
            }

            assetPath = normalized;
            return true;
        }

        private static bool TryApplyTag(GameObject gameObject, string tag, out string error)
        {
            error = null;
            string trimmed = (tag ?? string.Empty).Trim();

            // MCP 语义：空串视为 Untagged。
            if (trimmed.Length == 0) trimmed = "Untagged";

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
                error = "不存在名为 '" + trimmed + "' 的层。";
                return 0;
            }

            return byName;
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

            return false;
        }

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
