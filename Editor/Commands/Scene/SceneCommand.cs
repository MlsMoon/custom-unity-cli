using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli scene ...</c> - scene lifecycle (create / load / save / close), hierarchy browsing,
    /// build settings, screenshots, SceneView framing and scene validation.
    ///
    /// This group implements both <see cref="ICucliCommand"/> and <see cref="ICucliDeferredCommand"/>:
    /// every action is reachable through the deferred entry point, and <c>screenshot</c> is the only
    /// one that genuinely needs it, because a captured frame only exists after the Editor has
    /// rendered at least one update tick.
    /// </summary>
    internal sealed class SceneCommand : CucliCommand, ICucliDeferredCommand
    {
        private const string DefaultSceneFolder = "Assets/Scenes";
        private const string DefaultScreenshotFolder = "Assets/Screenshots";
        private const int DefaultInlineImageLongEdge = 640;
        private const int DefaultHierarchyPageSize = 50;

        public override string Name => "scene";

        public override string Summary =>
            "场景管理：创建、加载、保存、关闭、切换激活、层级浏览、Build Settings、截图、框选与校验。";

        public override CucliActionInfo[] Actions => Declare(
            "create", "新建场景（empty / default / 3d_basic / 2d_basic 模板，默认 Assets/Scenes/<name>.unity）。",
            "load", "按 path / name / buildIndex 加载场景；当前场景有未保存改动时按 MCP 语义拒绝加载。",
            "save", "保存指定或当前场景，可另存到新的 path。",
            "get_hierarchy", "分页列出场景根节点或某个父物体的直接子级（含 inactive、组件类型、可选 transform）。",
            "get_active", "报告当前活动场景的名称、路径、buildIndex、脏标记与根节点数量。",
            "get_build_settings", "列出 EditorBuildSettings 中的场景（path / guid / enabled / buildIndex）。",
            "screenshot", "截图（game_view 或 scene_view、相机/定位/包围盒批量环绕），写出 PNG 资产，可选内联 base64。",
            "scene_view_frame", "在 SceneView 中框选目标物体或整个场景。",
            "close_scene", "关闭已加载场景；只剩一个场景或场景有未保存改动时拒绝。",
            "set_active_scene", "把某个已加载场景设为活动场景。",
            "get_loaded_scenes", "列出全部已加载场景及其状态。",
            "move_to_scene", "把一个根 GameObject 移动到另一个已加载场景。",
            "validate", "校验活动场景中的 Missing Script 与丢失引用的 prefab，可选自动修复 Missing Script。");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "project.assets.read", "project.assets.write", "editor.scene.read", "editor.scene.write"
        };

        public override bool MayReloadDomain => false;

        public override string OptionalPackage => null;

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""create"", ""load"", ""save"", ""get_hierarchy"", ""get_active"", ""get_build_settings"",
                 ""screenshot"", ""scene_view_frame"", ""close_scene"", ""set_active_scene"",
                 ""get_loaded_scenes"", ""move_to_scene"", ""validate""],
      ""default"": ""create""
    },
    ""name"": { ""type"": ""string"", ""description"": ""action=create / save / load / close_scene / set_active_scene / move_to_scene：场景名。"" },
    ""path"": { ""type"": ""string"", ""description"": ""action=create / load / save：目录或 .unity 资产路径（Assets/ 下）。"" },
    ""buildIndex"": { ""type"": ""integer"", ""description"": ""action=load：Build Settings 索引，与 path/name 二选一。"" },
    ""additive"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=load：以 Additive 方式打开。"" },
    ""template"": { ""type"": ""string"", ""enum"": [""empty"", ""default"", ""3d_basic"", ""2d_basic""], ""description"": ""action=create：场景模板。"" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=create / load / close_scene：覆盖已有场景或丢弃未保存改动。"" },
    ""captureSource"": { ""type"": ""string"", ""enum"": [""game_view"", ""scene_view""], ""default"": ""game_view"" },
    ""fileName"": { ""type"": ""string"", ""description"": ""action=screenshot：输出文件名（自动补 .png）。"" },
    ""superSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8, ""default"": 1 },
    ""includeImage"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=screenshot：是否内联 base64 图片。"" },
    ""maxResolution"": { ""type"": ""integer"", ""default"": 640, ""description"": ""action=screenshot：内联图片长边上限，0 表示默认 640。"" },
    ""outputFolder"": { ""type"": ""string"", ""description"": ""action=screenshot：输出目录，默认 Assets/Screenshots。"" },
    ""camera"": { ""type"": ""string"", ""description"": ""action=screenshot：Camera 的 name / hierarchy path / instanceID（scene_view 下禁止）。"" },
    ""batch"": { ""type"": ""string"", ""enum"": [""surround"", ""orbit""], ""description"": ""action=screenshot：包围盒批量环绕截图。"" },
    ""viewTarget"": { ""description"": ""action=screenshot：聚焦目标（GO 引用或 [x,y,z]）。"" },
    ""viewPosition"": { ""description"": ""action=screenshot：临时相机位置 [x,y,z]。"" },
    ""viewRotation"": { ""description"": ""action=screenshot：临时相机欧拉角 [x,y,z]。"" },
    ""orbitAngles"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 36, ""default"": 8 },
    ""orbitElevations"": { ""description"": ""action=screenshot(batch=orbit)：仰角列表，默认 [0,30,-15]。"" },
    ""orbitDistance"": { ""type"": ""number"", ""description"": ""action=screenshot(batch=orbit)：相机距中心距离。"" },
    ""orbitFov"": { ""type"": ""number"", ""minimum"": 10, ""maximum"": 120, ""default"": 60 },
    ""sceneViewTarget"": { ""description"": ""action=scene_view_frame：要框选的物体（GO 引用）。"" },
    ""parent"": { ""type"": ""string"", ""description"": ""action=get_hierarchy：列出该物体的直接子级；省略则列出场景根节点。"" },
    ""pageSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50 },
    ""cursor"": { ""type"": [""string"", ""integer""], ""description"": ""action=get_hierarchy / get_build_settings / get_loaded_scenes：0 基游标。"" },
    ""pageNumber"": { ""type"": ""integer"", ""minimum"": 1 },
    ""maxNodes"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 5000, ""default"": 1000 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4, ""description"": ""action=get_hierarchy：当前分页模式只返回一层，仅为兼容回显。"" },
    ""maxChildrenPerNode"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 2000, ""default"": 200 },
    ""includeTransform"": { ""type"": ""boolean"", ""default"": false },
    ""sceneName"": { ""type"": ""string"", ""description"": ""action=close_scene / set_active_scene / move_to_scene：已加载场景名。"" },
    ""scenePath"": { ""type"": ""string"", ""description"": ""action=close_scene / set_active_scene / move_to_scene：已加载场景路径。"" },
    ""removeScene"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=close_scene：true 从内存移除，false 仅卸载。"" },
    ""target"": { ""type"": ""string"", ""description"": ""action=move_to_scene：根 GameObject 的 name / path / instanceID。"" },
    ""autoRepair"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=validate：自动移除 Missing Script。"" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "create");
            switch (action)
            {
                case "create": return Create(context);
                case "load": return Load(context);
                case "save": return Save(context);
                case "get_hierarchy": return GetHierarchy(context);
                case "get_active": return GetActive(context);
                case "get_build_settings": return GetBuildSettings(context);
                case "screenshot":
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "screenshot 需要跨帧渲染，必须走延迟执行路径。请改用 'cucli scene screenshot' 正常调用。");
                case "scene_view_frame": return SceneViewFrame(context);
                case "close_scene": return CloseScene(context);
                case "set_active_scene": return SetActiveScene(context);
                case "get_loaded_scenes": return GetLoadedScenes(context);
                case "move_to_scene": return MoveToScene(context);
                case "validate": return Validate(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        /// <summary>
        /// Deferred entry point. Every action is served here so the dispatcher always has one path;
        /// only <c>screenshot</c> actually yields for frames.
        /// </summary>
        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string action = ResolveAction(context, "create");
            switch (action)
            {
                case "screenshot":
                    ScreenshotDeferred(context, complete);
                    return;
                default:
                    complete(Execute(context));
                    return;
            }
        }

        // ── create ─────────────────────────────────────────────────────────────

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string name = args.GetString("name");
            string requestedPath = args.GetString("path");
            string template = (args.GetString("template") ?? string.Empty).Trim().ToLowerInvariant();
            bool force = args.GetBool("force", false);

            string pathError;
            string relativePath = BuildScenePath(requestedPath, name, out pathError);
            if (pathError != null) return CucliCommandResult.Invalid("path", pathError);
            if (relativePath == null)
            {
                return CucliCommandResult.Missing("name", "示例：--name Battle --path Assets/Scenes");
            }

            bool exists = CucliAssets.Exists(relativePath);
            if (exists && !force)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "场景已存在：" + relativePath + "。请换一个名称，或显式传 force: true 覆盖。",
                    new JObject { ["path"] = relativePath });
            }

            if (AnyLoadedSceneDirty() && !force)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "存在未保存的场景改动，创建新场景（Single 模式）会丢弃这些改动。请先保存，或显式传 force: true。",
                    new JObject { ["dirtyScenes"] = DirtySceneNames() });
            }

            string folder = CucliAssets.ParentFolder(relativePath);
            if (string.IsNullOrEmpty(folder) || !CucliAssets.EnsureFolder(folder))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "无法创建目录：" + (folder ?? relativePath) + "。");
            }

            NewSceneSetup setup = NewSceneSetup.EmptyScene;
            bool fromTemplate = !string.IsNullOrEmpty(template);
            if (fromTemplate)
            {
                switch (template)
                {
                    case "empty":
                        setup = NewSceneSetup.EmptyScene;
                        break;
                    case "default":
                    case "3d_basic":
                    case "2d_basic":
                        setup = NewSceneSetup.DefaultGameObjects;
                        break;
                    default:
                        return CucliCommandResult.Invalid(
                            "template", "'" + template + "' 不是可用模板。可用：empty, default, 3d_basic, 2d_basic。");
                }
            }

            Scene scene = EditorSceneManager.NewScene(setup, NewSceneMode.Single);

            if (template == "3d_basic")
            {
                GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                plane.name = "Ground";
                plane.transform.position = Vector3.zero;
            }
            else if (template == "2d_basic")
            {
                Camera main = Camera.main;
                if (main != null) main.orthographic = true;
            }

            if (!EditorSceneManager.SaveScene(scene, relativePath))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "场景已在内存中创建，但保存到 '" + relativePath + "' 失败。",
                    new JObject { ["path"] = relativePath });
            }

            AssetDatabase.Refresh();

            var data = new JObject
            {
                ["path"] = relativePath,
                ["sceneName"] = scene.name ?? string.Empty,
                ["scenePath"] = scene.path ?? string.Empty,
                ["template"] = fromTemplate ? template : "none",
                ["rootObjectCount"] = scene.rootCount,
                ["overwritten"] = exists
            };

            return CucliCommandResult.Ok(data, "已创建场景 " + relativePath + "。");
        }

        /// <summary>Builds <c>Assets/.../&lt;name&gt;.unity</c> from the create/load path semantics.</summary>
        private static string BuildScenePath(string requestedPath, string name, out string pathError)
        {
            pathError = null;
            string directory = DefaultSceneFolder;
            string fileName = name == null ? null : name.Trim();

            if (fileName != null && fileName.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileName.Substring(0, fileName.Length - ".unity".Length);
            }

            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                string normalized = CucliAssets.NormalizeAssetPath(requestedPath);
                if (normalized == null)
                {
                    pathError = "路径越界或非法：'" + requestedPath + "'。路径必须位于项目 Assets/ 或 Packages/ 下。";
                    return null;
                }

                if (normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    directory = CucliAssets.ParentFolder(normalized) ?? "Assets";
                    fileName = CucliAssets.FileNameWithoutExtension(normalized);
                }
                else
                {
                    directory = normalized;
                }
            }

            if (string.IsNullOrWhiteSpace(fileName)) return null;
            return directory.TrimEnd('/') + "/" + fileName + ".unity";
        }

        // ── load ───────────────────────────────────────────────────────────────

        private static CucliCommandResult Load(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            bool additive = args.GetBool("additive", false);
            string requestedPath = args.GetString("path");
            string name = args.GetString("name");
            JToken buildIndexToken = args.Token("buildIndex") ?? args.Token("build_index");

            string scenePath = null;
            int buildIndex = -1;

            if (buildIndexToken != null && buildIndexToken.Type != JTokenType.Null)
            {
                buildIndex = args.GetInt("buildIndex", args.GetInt("build_index", -1));
                EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;
                if (buildIndex < 0 || buildIndex >= buildScenes.Length)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter,
                        "buildIndex " + buildIndex + " 越界，Build Settings 中共有 " + buildScenes.Length + " 个场景。",
                        new JObject { ["sceneCount"] = buildScenes.Length });
                }

                scenePath = buildScenes[buildIndex].path;
                if (string.IsNullOrWhiteSpace(scenePath))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound,
                        "Build Settings 第 " + buildIndex + " 项没有有效路径。",
                        new JObject { ["buildIndex"] = buildIndex });
                }
            }
            else if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                string normalized = CucliAssets.NormalizeAssetPath(requestedPath);
                if (normalized == null)
                {
                    return CucliCommandResult.Invalid(
                        "path", "路径越界或非法：'" + requestedPath + "'。");
                }

                if (!normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return CucliCommandResult.Missing(
                            "name", "path 指向目录时必须同时提供场景 name。");
                    }

                    normalized = normalized.TrimEnd('/') + "/" + name.Trim() + ".unity";
                }

                scenePath = normalized;
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                string findError;
                bool ambiguous;
                string[] matches = FindSceneAssetsByName(name.Trim(), out findError, out ambiguous);
                if (findError != null)
                {
                    return CucliCommandResult.Fail(
                        ambiguous ? CucliErrors.TargetAmbiguous : CucliErrors.AssetNotFound,
                        findError,
                        new JObject { ["matches"] = new JArray(matches ?? Array.Empty<string>()) });
                }

                scenePath = matches[0];
            }
            else
            {
                return CucliCommandResult.Missing(
                    "path", "需要 path、name 或 buildIndex 之一来定位场景。");
            }

            if (!CucliAssets.Exists(scenePath))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "场景文件不存在：" + scenePath + "。",
                    new JObject { ["path"] = scenePath });
            }

            bool force = args.GetBool("force", false);
            if (AnyLoadedSceneDirty() && !force)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "当前存在未保存的场景改动，按 MCP 语义拒绝加载。请先执行 'cucli scene save' 保存，"
                    + "或显式传 --force 丢弃这些改动后再加载。",
                    new JObject { ["dirtyScenes"] = DirtySceneNames(), ["requestedPath"] = scenePath });
            }

            Scene opened = EditorSceneManager.OpenScene(
                scenePath, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);

            if (!opened.IsValid() || !opened.isLoaded)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Unity 未能加载场景 " + scenePath + "。请检查场景文件是否损坏，"
                    + "additive 模式下同一场景是否已经被加载。",
                    new JObject { ["path"] = scenePath, ["additive"] = additive });
            }

            var data = new JObject
            {
                ["path"] = scenePath,
                ["name"] = opened.IsValid()
                    ? (opened.name ?? CucliAssets.FileNameWithoutExtension(scenePath))
                    : CucliAssets.FileNameWithoutExtension(scenePath),
                ["additive"] = additive,
                ["loadedSceneCount"] = SceneManager.sceneCount,
                ["isActive"] = opened.IsValid() && opened == SceneManager.GetActiveScene()
            };

            if (buildIndex >= 0) data["buildIndex"] = buildIndex;

            return CucliCommandResult.Ok(
                data, (additive ? "已以 Additive 方式加载场景 " : "已加载场景 ") + scenePath + "。");
        }

        private static string[] FindSceneAssetsByName(string name, out string error, out bool ambiguous)
        {
            error = null;
            ambiguous = false;

            var matches = new List<string>();
            foreach (string candidate in CucliAssets.FindAssets("Scene", null, name))
            {
                if (string.Equals(
                        CucliAssets.FileNameWithoutExtension(candidate), name, StringComparison.Ordinal))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                error = "没有找到名为 '" + name + "' 的场景资产。请改用完整 path，或先用 "
                    + "'cucli asset find --type Scene' 查询。";
                return null;
            }

            if (matches.Count > 1)
            {
                ambiguous = true;
                error = "有 " + matches.Count + " 个场景叫 '" + name + "'：" + string.Join(", ", matches)
                    + "。请改用完整 path。";
                return matches.ToArray();
            }

            return matches.ToArray();
        }

        // ── save ───────────────────────────────────────────────────────────────

        private static CucliCommandResult Save(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string sceneError;
            Scene scene = ResolveLoadedScene(args, out sceneError);
            if (sceneError != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, sceneError, new JObject { ["loadedScenes"] = LoadedSceneNames() });
            }

            string requestedPath = args.GetString("path");
            string name = args.GetString("name");
            string targetPath = scene.path;

            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                string normalized = CucliAssets.NormalizeAssetPath(requestedPath);
                if (normalized == null)
                {
                    return CucliCommandResult.Invalid("path", "路径越界或非法：'" + requestedPath + "'。");
                }

                if (normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    targetPath = normalized;
                }
                else
                {
                    string fileName = string.IsNullOrWhiteSpace(name)
                        ? CucliAssets.FileNameWithoutExtension(scene.path)
                        : name.Trim();
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        return CucliCommandResult.Missing(
                            "name", "path 指向目录时必须同时提供场景 name。");
                    }

                    targetPath = normalized.TrimEnd('/') + "/" + fileName + ".unity";
                }
            }
            else if (string.IsNullOrWhiteSpace(targetPath))
            {
                string fileName = string.IsNullOrWhiteSpace(name) ? scene.name : name.Trim();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    return CucliCommandResult.Missing(
                        "path", "该场景从未保存过，需要显式提供 path（例如 Assets/Scenes/New.unity）。");
                }

                targetPath = DefaultSceneFolder + "/" + fileName + ".unity";
            }

            string folder = CucliAssets.ParentFolder(targetPath);
            if (string.IsNullOrEmpty(folder) || !CucliAssets.EnsureFolder(folder))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "无法创建目录：" + (folder ?? targetPath) + "。");
            }

            if (!EditorSceneManager.SaveScene(scene, targetPath))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "保存场景到 '" + targetPath + "' 失败，请检查文件是否只读或磁盘是否可写。",
                    new JObject { ["path"] = targetPath });
            }

            AssetDatabase.SaveAssets();

            var data = new JObject
            {
                ["path"] = targetPath,
                ["name"] = scene.name ?? string.Empty,
                ["buildIndex"] = scene.buildIndex,
                ["isDirty"] = scene.isDirty
            };

            return CucliCommandResult.Ok(data, "已保存场景 " + targetPath + "。");
        }

        /// <summary>Resolves the scene a command operates on: scenePath/sceneName, then name/path, then active.</summary>
        private static Scene ResolveLoadedScene(CucliArgs args, out string error)
        {
            error = null;
            string scenePath = args.GetString("scenePath") ?? args.GetString("scene_path");
            string sceneName = args.GetString("sceneName") ?? args.GetString("scene_name");

            if (string.IsNullOrWhiteSpace(scenePath)) scenePath = args.GetString("path");
            if (string.IsNullOrWhiteSpace(sceneName)) sceneName = args.GetString("name");

            if (!string.IsNullOrWhiteSpace(scenePath))
            {
                string normalized = CucliAssets.NormalizeAssetPath(scenePath);
                if (normalized != null)
                {
                    Scene byPath = SceneManager.GetSceneByPath(normalized);
                    if (byPath.IsValid() && byPath.isLoaded) return byPath;
                }

                error = "没有已加载的场景位于路径 '" + scenePath + "'。";
                return default(Scene);
            }

            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                for (int index = 0; index < SceneManager.sceneCount; index++)
                {
                    Scene candidate = SceneManager.GetSceneAt(index);
                    if (candidate.IsValid() && candidate.isLoaded
                        && string.Equals(candidate.name, sceneName, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }

                error = "没有名为 '" + sceneName + "' 的已加载场景。";
                return default(Scene);
            }

            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid() || !active.isLoaded)
            {
                error = "当前没有活动的已加载场景。";
                return default(Scene);
            }

            return active;
        }

        // ── get_hierarchy ──────────────────────────────────────────────────────

        private static CucliCommandResult GetHierarchy(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            GameObject parent = null;
            JToken parentToken = args.Token("parent");
            if (parentToken != null && parentToken.Type != JTokenType.Null
                && !string.IsNullOrWhiteSpace(parentToken.ToString()))
            {
                string parentError;
                parent = ResolveObjectToken(parentToken, out parentError);
                if (parentError != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, parentError);
                }
            }

            var nodes = new List<GameObject>();
            string scope = parent == null ? "roots" : "children";

            if (parent == null)
            {
                Scene active = SceneManager.GetActiveScene();
                IEnumerable<GameObject> roots;
                if (active.IsValid() && active.isLoaded)
                {
                    roots = active.GetRootGameObjects();
                }
                else
                {
                    roots = AllRoots();
                }

                foreach (GameObject root in roots)
                {
                    if (root != null) nodes.Add(root);
                }
            }
            else
            {
                Transform transform = parent.transform;
                for (int index = 0; index < transform.childCount; index++)
                {
                    nodes.Add(transform.GetChild(index).gameObject);
                }
            }

            int maxNodes = Mathf.Clamp(
                args.GetInt("maxNodes", args.GetInt("max_nodes", 1000)), 1, 5000);
            int total = nodes.Count;
            bool truncated = total > maxNodes;
            if (truncated)
            {
                context.Warn("节点总数 " + total + " 超过 maxNodes=" + maxNodes + "，只扫描前 " + maxNodes + " 个。");
                nodes = nodes.Take(maxNodes).ToList();
            }

            int maxChildrenPerNode = Mathf.Clamp(
                args.GetInt("maxChildrenPerNode", args.GetInt("max_children_per_node", 200)), 0, 2000);
            bool includeTransform = args.GetBool("includeTransform", args.GetBool("include_transform", false));
            int maxDepth = Mathf.Clamp(args.GetInt("maxDepth", args.GetInt("max_depth", 4)), 1, 12);
            CucliDescribe.Options options = CucliDescribe.OptionsFrom(args);

            CucliPage<GameObject> page = CucliPaging.Slice(nodes, WithNumericCursor(args), DefaultHierarchyPageSize);

            var items = new JArray();
            foreach (GameObject node in page.Items)
            {
                context.ThrowIfCancelled();
                items.Add(DescribeHierarchyNode(node, options, maxChildrenPerNode, includeTransform));
            }

            var payload = new JObject
            {
                ["scope"] = scope,
                ["cursor"] = page.Offset,
                ["maxNodes"] = maxNodes,
                ["maxDepth"] = maxDepth,
                ["maxChildrenPerNode"] = maxChildrenPerNode,
                ["truncated"] = truncated,
                ["$truncated"] = truncated ? total - maxNodes : 0,
                ["scannedTotal"] = total,
                ["loadedSceneCount"] = SceneManager.sceneCount
            };

            if (parent != null)
            {
                payload["parentName"] = parent.name;
                payload["parentPath"] = CucliObjectResolver.HierarchyPath(parent, true);
                payload["parentInstanceID"] = parent.GetInstanceID();
            }

            JObject envelope = CucliPaging.Envelope(page, items, payload);
            if (page.HasMore) envelope["next_cursor"] = page.NextCursor;

            return CucliCommandResult.Ok(
                envelope,
                "已返回 " + page.Items.Count + "/" + page.Total + " 个" + (parent == null ? "场景根节点" : "子节点") + "。");
        }

        private static JObject DescribeHierarchyNode(
            GameObject gameObject, CucliDescribe.Options options, int maxChildrenPerNode, bool includeTransform)
        {
            Transform transform = gameObject.transform;
            var item = new JObject
            {
                ["name"] = gameObject.name,
                ["instanceID"] = gameObject.GetInstanceID(),
                ["activeSelf"] = gameObject.activeSelf,
                ["activeInHierarchy"] = gameObject.activeInHierarchy,
                ["tag"] = SafeTag(gameObject),
                ["layer"] = gameObject.layer,
                ["layerName"] = LayerMask.LayerToName(gameObject.layer),
                ["isStatic"] = gameObject.isStatic,
                ["path"] = CucliObjectResolver.HierarchyPath(gameObject, true),
                ["childCount"] = transform.childCount,
                ["childrenTruncated"] = maxChildrenPerNode > 0 && transform.childCount > maxChildrenPerNode,
                ["childrenCursor"] = 0,
                ["childrenPageSizeDefault"] = DefaultHierarchyPageSize,
                ["componentTypes"] = ComponentTypeNames(gameObject, options)
            };

            if (includeTransform)
            {
                Vector3 position = transform.localPosition;
                Vector3 rotation = transform.localEulerAngles;
                Vector3 scale = transform.localScale;
                item["transform"] = new JObject
                {
                    ["position"] = new JArray(position.x, position.y, position.z),
                    ["rotation"] = new JArray(rotation.x, rotation.y, rotation.z),
                    ["scale"] = new JArray(scale.x, scale.y, scale.z)
                };
            }

            return item;
        }

        private static JArray ComponentTypeNames(GameObject gameObject, CucliDescribe.Options options)
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

            int limit = Math.Min(components.Length, options.MaxItems);
            for (int index = 0; index < limit; index++)
            {
                Component component = components[index];
                names.Add(component == null ? "<missing script>" : component.GetType().Name);
            }

            return names;
        }

        // ── get_active / get_build_settings / get_loaded_scenes ────────────────

        private static CucliCommandResult GetActive(CucliCommandContext context)
        {
            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid())
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "当前没有活动的已加载场景。请先用 'cucli scene load' 打开一个场景。");
            }

            return CucliCommandResult.Ok(SceneSummary(active, true), "当前活动场景：" + active.name + "。");
        }

        private static CucliCommandResult GetBuildSettings(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;

            var source = new List<JObject>(buildScenes.Length);
            for (int index = 0; index < buildScenes.Length; index++)
            {
                EditorBuildSettingsScene entry = buildScenes[index];
                source.Add(new JObject
                {
                    ["path"] = entry.path ?? string.Empty,
                    ["guid"] = CucliAssets.AssetPathToGuid(entry.path),
                    ["enabled"] = entry.enabled,
                    ["buildIndex"] = index
                });
            }

            CucliPage<JObject> page = CucliPaging.Slice(source, WithNumericCursor(args), 100);
            var items = new JArray();
            foreach (JObject entry in page.Items) items.Add(entry);

            JObject envelope = CucliPaging.Envelope(page, items, new JObject());
            envelope["scenes"] = items;
            if (page.HasMore) envelope["next_cursor"] = page.NextCursor;

            return CucliCommandResult.Ok(
                envelope, "Build Settings 中共有 " + page.Total + " 个场景。");
        }

        private static CucliCommandResult GetLoadedScenes(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            Scene active = SceneManager.GetActiveScene();

            var source = new List<JObject>(SceneManager.sceneCount);
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid()) continue;
                source.Add(SceneSummary(scene, scene == active));
            }

            CucliPage<JObject> page = CucliPaging.Slice(source, WithNumericCursor(args), 100);
            var items = new JArray();
            foreach (JObject entry in page.Items) items.Add(entry);

            JObject envelope = CucliPaging.Envelope(page, items, new JObject());
            envelope["scenes"] = items;
            if (page.HasMore) envelope["next_cursor"] = page.NextCursor;

            return CucliCommandResult.Ok(
                envelope, "当前已加载 " + source.Count + " 个场景。");
        }

        private static JObject SceneSummary(Scene scene, bool isActive)
        {
            return new JObject
            {
                ["name"] = scene.IsValid() ? (scene.name ?? string.Empty) : string.Empty,
                ["path"] = scene.IsValid() ? (scene.path ?? string.Empty) : string.Empty,
                ["buildIndex"] = scene.IsValid() ? scene.buildIndex : -1,
                ["isLoaded"] = scene.IsValid() && scene.isLoaded,
                ["isDirty"] = scene.IsValid() && scene.isDirty,
                ["isActive"] = isActive,
                ["rootCount"] = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0
            };
        }

        // ── close_scene / set_active_scene / move_to_scene ─────────────────────

        private static CucliCommandResult CloseScene(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string sceneError;
            Scene scene = ResolveLoadedScene(args, out sceneError);
            if (sceneError != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, sceneError, new JObject { ["loadedScenes"] = LoadedSceneNames() });
            }

            if (SceneManager.sceneCount <= 1)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "当前只剩 1 个已加载场景，Unity 不允许关闭最后一个场景。");
            }

            bool force = args.GetBool("force", false);
            if (scene.isDirty && !force)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "场景 '" + scene.name + "' 有未保存改动，拒绝关闭。请先 'cucli scene save' 保存，或传 --force 丢弃改动。",
                    new JObject
                    {
                        ["sceneName"] = scene.name ?? string.Empty,
                        ["scenePath"] = scene.path ?? string.Empty
                    });
            }

            bool remove = args.GetBool("removeScene", args.GetBool("remove_scene", false));
            string sceneName = scene.name ?? string.Empty;
            string scenePath = scene.path ?? string.Empty;

            if (!EditorSceneManager.CloseScene(scene, remove))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "关闭场景 '" + sceneName + "' 失败。");
            }

            var data = new JObject
            {
                ["sceneName"] = sceneName,
                ["scenePath"] = scenePath,
                ["removed"] = remove,
                ["loadedSceneCount"] = SceneManager.sceneCount
            };

            return CucliCommandResult.Ok(
                data, (remove ? "已从内存移除场景 " : "已卸载场景 ") + sceneName + "。");
        }

        private static CucliCommandResult SetActiveScene(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string sceneError;
            Scene scene = ResolveLoadedScene(args, out sceneError);
            if (sceneError != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, sceneError, new JObject { ["loadedScenes"] = LoadedSceneNames() });
            }

            if (!SceneManager.SetActiveScene(scene))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "把 '" + scene.name + "' 设为活动场景失败。");
            }

            return CucliCommandResult.Ok(
                new JObject
                {
                    ["sceneName"] = scene.name ?? string.Empty,
                    ["scenePath"] = scene.path ?? string.Empty,
                    ["isActive"] = true
                },
                "已把 " + scene.name + " 设为活动场景。");
        }

        private static CucliCommandResult MoveToScene(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string targetError;
            GameObject gameObject = ResolveObjectToken(args.Token("target"), out targetError);
            if (targetError != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, targetError);
            }

            string sceneError;
            Scene scene = ResolveLoadedScene(args, out sceneError);
            if (sceneError != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, sceneError, new JObject { ["loadedScenes"] = LoadedSceneNames() });
            }

            if (gameObject.transform.parent != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "'" + gameObject.name + "' 不是根节点，Unity 只能移动根 GameObject 到其他场景。"
                    + "请先用 'cucli object modify' 把它移到根。",
                    new JObject
                    {
                        ["path"] = CucliObjectResolver.HierarchyPath(gameObject, true),
                        ["parent"] = gameObject.transform.parent.name
                    });
            }

            SceneManager.MoveGameObjectToScene(gameObject, scene);
            EditorSceneManager.MarkSceneDirty(scene);

            return CucliCommandResult.Ok(
                new JObject
                {
                    ["name"] = gameObject.name,
                    ["instanceID"] = gameObject.GetInstanceID(),
                    ["sceneName"] = scene.name ?? string.Empty,
                    ["scenePath"] = scene.path ?? string.Empty
                },
                "已把 " + gameObject.name + " 移动到场景 " + scene.name + "。");
        }

        // ── validate ───────────────────────────────────────────────────────────

        private static CucliCommandResult Validate(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid() || !active.isLoaded)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "没有活动的已加载场景，无法校验。请先用 'cucli scene load' 打开一个场景。");
            }

            bool autoRepair = args.GetBool("autoRepair", args.GetBool("auto_repair", false));
            const int issueLimit = 200;

            var issues = new JArray();
            int missingScripts = 0;
            int brokenPrefabs = 0;
            int repaired = 0;
            bool truncated = false;

            GameObject[] roots = active.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                GameObject root = roots[rootIndex];
                if (root == null) continue;

                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                foreach (Transform transform in transforms)
                {
                    context.ThrowIfCancelled();
                    GameObject candidate = transform.gameObject;

                    int missingHere = CountMissingScripts(candidate);
                    if (missingHere > 0)
                    {
                        missingScripts += missingHere;
                        if (issues.Count < issueLimit)
                        {
                            issues.Add(new JObject
                            {
                                ["type"] = "missing_script",
                                ["gameObject"] = candidate.name,
                                ["path"] = CucliObjectResolver.HierarchyPath(candidate, true),
                                ["count"] = missingHere
                            });
                        }
                        else
                        {
                            truncated = true;
                        }

                        if (autoRepair)
                        {
                            repaired += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(candidate);
                        }
                    }

                    PrefabInstanceStatus status = PrefabInstanceStatus.NotAPrefab;
                    try
                    {
                        status = PrefabUtility.GetPrefabInstanceStatus(candidate);
                    }
                    catch (Exception)
                    {
                        status = PrefabInstanceStatus.NotAPrefab;
                    }

                    if (status == PrefabInstanceStatus.MissingAsset)
                    {
                        brokenPrefabs++;
                        if (issues.Count < issueLimit)
                        {
                            issues.Add(new JObject
                            {
                                ["type"] = "broken_prefab",
                                ["gameObject"] = candidate.name,
                                ["path"] = CucliObjectResolver.HierarchyPath(candidate, true),
                                ["status"] = "missing_asset"
                            });
                        }
                        else
                        {
                            truncated = true;
                        }
                    }
                }
            }

            if (repaired > 0) EditorSceneManager.MarkSceneDirty(active);

            var data = new JObject
            {
                ["sceneName"] = active.name ?? string.Empty,
                ["scenePath"] = active.path ?? string.Empty,
                ["totalIssues"] = missingScripts + brokenPrefabs,
                ["missingScripts"] = missingScripts,
                ["brokenPrefabs"] = brokenPrefabs,
                ["repaired"] = repaired,
                ["issues"] = issues,
                ["truncated"] = truncated,
                ["note"] = autoRepair
                    ? "autoRepair 只移除 Missing Script，不会修复 broken prefab。"
                    : "只读校验；传 autoRepair: true 可移除 Missing Script。"
            };

            return CucliCommandResult.Ok(
                data,
                missingScripts + brokenPrefabs == 0
                    ? "场景 " + active.name + " 未发现问题。"
                    : "场景 " + active.name + " 发现 " + (missingScripts + brokenPrefabs)
                      + " 个问题（missing script " + missingScripts + "，broken prefab " + brokenPrefabs + "）。");
        }

        private static int CountMissingScripts(GameObject gameObject)
        {
            try
            {
                Component[] components = gameObject.GetComponents<Component>();
                int count = 0;
                for (int index = 0; index < components.Length; index++)
                {
                    if (components[index] == null) count++;
                }

                return count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ── scene_view_frame ───────────────────────────────────────────────────

        private static CucliCommandResult SceneViewFrame(CucliCommandContext context)
        {
            if (Application.isBatchMode)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "batchmode 下不存在 SceneView，无法框选。请在带 Editor 窗口的会话中调用。",
                    new JObject { ["isBatchMode"] = true });
            }

            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "找不到 SceneView.lastActiveSceneView。请先打开一个 Scene 视图窗口再重试。",
                    new JObject { ["sceneViewAvailable"] = false });
            }

            CucliArgs args = context.Input;
            JToken targetToken = args.Token("sceneViewTarget") ?? args.Token("scene_view_target");
            if (targetToken == null || targetToken.Type == JTokenType.Null
                || string.IsNullOrWhiteSpace(targetToken.ToString()))
            {
                SceneView.FrameLastActiveSceneView();
                view.Repaint();
                return CucliCommandResult.Ok(null, "已在 SceneView 中框选当前场景。");
            }

            string targetError;
            GameObject target = ResolveObjectToken(targetToken, out targetError);
            if (targetError != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, targetError);
            }

            Bounds bounds = CalculateBounds(target);
            view.Frame(bounds, true);
            view.Repaint();

            return CucliCommandResult.Ok(
                new JObject
                {
                    ["target"] = target.name,
                    ["instanceID"] = target.GetInstanceID(),
                    ["path"] = CucliObjectResolver.HierarchyPath(target, true)
                },
                "已在 SceneView 中框选 " + target.name + "。");
        }

        private static Bounds CalculateBounds(GameObject gameObject)
        {
            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            Bounds bounds = new Bounds(gameObject.transform.position, Vector3.zero);

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
            {
                bounds = new Bounds(gameObject.transform.position, Vector3.one);
            }

            if (bounds.size.sqrMagnitude < 0.0001f)
            {
                bounds.size = Vector3.one;
            }

            return bounds;
        }

        // ── screenshot ─────────────────────────────────────────────────────────

        /// <summary>Everything a single screenshot request needs, parsed once and validated up front.</summary>
        private sealed class ShotRequest
        {
            internal string CaptureSource = "game_view";
            internal string Batch;
            internal string CameraSpec;
            internal int SuperSize = 1;
            internal string OutputFolder;
            internal string RelativePath;
            internal string AbsolutePath;
            internal bool IncludeImage;
            internal int MaxResolution = DefaultInlineImageLongEdge;
            internal bool HasViewTarget;
            internal GameObject ViewTarget;
            internal bool HasViewPosition;
            internal Vector3 ViewPosition;
            internal bool HasViewRotation;
            internal Vector3 ViewRotation;
            internal int OrbitAngles = 8;
            internal float[] OrbitElevations = { 0f, 30f, -15f };
            internal bool HasOrbitDistance;
            internal float OrbitDistance = 10f;
            internal float OrbitFov = 60f;
        }

        private static void ScreenshotDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            ShotRequest request;
            CucliCommandResult failure;
            if (!TryParseShot(context, out request, out failure))
            {
                complete(failure);
                return;
            }

            bool screenCaptureMode = request.CaptureSource == "game_view"
                && string.IsNullOrWhiteSpace(request.Batch)
                && string.IsNullOrWhiteSpace(request.CameraSpec)
                && !request.HasViewPosition;

            if (screenCaptureMode)
            {
                // The Game View is rendered by the Editor loop, so wait for two ticks before asking
                // ScreenCapture for the frame.
                AfterFrames(
                    context,
                    2,
                    delegate { complete(CaptureWithScreenCapture(context, request)); });
                return;
            }

            complete(CaptureRendered(context, request));
        }

        private static bool TryParseShot(
            CucliCommandContext context, out ShotRequest request, out CucliCommandResult failure)
        {
            CucliArgs args = context.Input;
            request = new ShotRequest();
            failure = null;

            string source = (args.GetString("captureSource") ?? args.GetString("capture_source")
                ?? "game_view").Trim().ToLowerInvariant();
            if (source != "game_view" && source != "scene_view")
            {
                failure = CucliCommandResult.Invalid(
                    "captureSource", "'" + source + "' 不是可用取值。可用：game_view, scene_view。");
                return false;
            }

            request.CaptureSource = source;
            request.Batch = (args.GetString("batch") ?? string.Empty).Trim().ToLowerInvariant();
            if (request.Batch.Length > 0 && request.Batch != "surround" && request.Batch != "orbit")
            {
                failure = CucliCommandResult.Invalid(
                    "batch", "'" + request.Batch + "' 不是可用取值。可用：surround, orbit。");
                return false;
            }

            request.CameraSpec = args.GetString("camera");
            request.SuperSize = Mathf.Clamp(
                args.GetInt("superSize", args.GetInt("super_size", args.GetInt("supersize", 1))), 1, 8);
            request.IncludeImage = args.GetBool("includeImage", args.GetBool("include_image", false));
            request.MaxResolution = args.GetInt("maxResolution", args.GetInt("max_resolution", 0));
            if (request.MaxResolution <= 0) request.MaxResolution = DefaultInlineImageLongEdge;

            JToken viewPositionToken = args.Token("viewPosition") ?? args.Token("view_position");
            if (viewPositionToken != null && viewPositionToken.Type != JTokenType.Null)
            {
                request.HasViewPosition = true;
                request.ViewPosition = args.GetVector3("viewPosition", args.GetVector3("view_position"));
            }

            JToken viewRotationToken = args.Token("viewRotation") ?? args.Token("view_rotation");
            if (viewRotationToken != null && viewRotationToken.Type != JTokenType.Null)
            {
                request.HasViewRotation = true;
                request.ViewRotation = args.GetVector3("viewRotation", args.GetVector3("view_rotation"));
            }

            JToken viewTargetToken = args.Token("viewTarget") ?? args.Token("view_target");
            if (viewTargetToken != null && viewTargetToken.Type != JTokenType.Null
                && !string.IsNullOrWhiteSpace(viewTargetToken.ToString()))
            {
                string targetError;
                request.ViewTarget = ResolveObjectToken(viewTargetToken, out targetError);
                if (targetError != null)
                {
                    failure = CucliCommandResult.Fail(CucliErrors.TargetNotFound, targetError);
                    return false;
                }

                request.HasViewTarget = true;
            }

            request.OrbitAngles = Mathf.Clamp(
                args.GetInt("orbitAngles", args.GetInt("orbit_angles", 8)), 1, 36);
            request.OrbitFov = Mathf.Clamp(
                args.GetFloat("orbitFov", args.GetFloat("orbit_fov", 60f)), 10f, 120f);

            JToken elevationsToken = args.Token("orbitElevations") ?? args.Token("orbit_elevations");
            if (elevationsToken != null && elevationsToken.Type == JTokenType.Array)
            {
                var elevations = new List<float>();
                foreach (JToken item in (JArray)elevationsToken)
                {
                    float value;
                    if (float.TryParse(
                            item.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        elevations.Add(value);
                    }
                }

                if (elevations.Count > 0) request.OrbitElevations = elevations.ToArray();
            }
            else if (elevationsToken != null && elevationsToken.Type != JTokenType.Null)
            {
                request.OrbitElevations = new[]
                {
                    args.GetFloat("orbitElevations", args.GetFloat("orbit_elevations", 0f))
                };
            }

            JToken distanceToken = args.Token("orbitDistance") ?? args.Token("orbit_distance");
            if (distanceToken != null && distanceToken.Type != JTokenType.Null)
            {
                request.HasOrbitDistance = true;
                request.OrbitDistance = args.GetFloat("orbitDistance", args.GetFloat("orbit_distance", 10f));
                if (request.OrbitDistance <= 0f) request.OrbitDistance = 10f;
            }

            if (source == "scene_view")
            {
                if (Application.isBatchMode)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.WrongMode,
                        "batchmode 下不存在 SceneView，无法用 captureSource=scene_view 截图。"
                        + "请改用 capture_source=game_view 或 camera / viewPosition / batch 参数。",
                        new JObject { ["isBatchMode"] = true, ["captureSource"] = source });
                    return false;
                }

                if (SceneView.lastActiveSceneView == null)
                {
                    failure = CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        "找不到 SceneView.lastActiveSceneView，无法用 captureSource=scene_view 截图。"
                        + "请先打开一个 Scene 视图窗口。",
                        new JObject { ["sceneViewAvailable"] = false, ["captureSource"] = source });
                    return false;
                }

                if (request.SuperSize > 1)
                {
                    failure = CucliCommandResult.Invalid("superSize", "scene_view 截图不支持 superSize > 1。");
                    return false;
                }

                if (request.Batch.Length > 0)
                {
                    failure = CucliCommandResult.Invalid("batch", "scene_view 截图不支持 batch 模式。");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(request.CameraSpec))
                {
                    failure = CucliCommandResult.Invalid("camera", "scene_view 截图不接受 camera 参数。");
                    return false;
                }

                if (request.HasViewPosition)
                {
                    failure = CucliCommandResult.Invalid("viewPosition", "scene_view 截图不接受 viewPosition。");
                    return false;
                }
            }

            string folder = args.GetString("outputFolder") ?? args.GetString("output_folder");
            if (string.IsNullOrWhiteSpace(folder)) folder = DefaultScreenshotFolder;

            string normalizedFolder = CucliAssets.NormalizeAssetPath(folder);
            if (normalizedFolder == null || !normalizedFolder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                failure = CucliCommandResult.Invalid(
                    "outputFolder", "输出目录必须位于项目 Assets/ 下，收到：'" + folder + "'。");
                return false;
            }

            string fileName = args.GetString("fileName") ?? args.GetString("filename");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "cucli_screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            }

            fileName = SanitizeFileName(fileName.Trim());
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";

            request.OutputFolder = normalizedFolder;
            request.RelativePath = normalizedFolder.TrimEnd('/') + "/" + fileName;
            request.AbsolutePath = CucliAssets.ToAbsolute(request.RelativePath);
            if (string.IsNullOrEmpty(request.AbsolutePath))
            {
                failure = CucliCommandResult.Invalid("outputFolder", "无法解析输出绝对路径。");
                return false;
            }

            return true;
        }

        private static string SanitizeFileName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char character in value)
            {
                builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
            }

            return builder.ToString();
        }

        private static CucliCommandResult CaptureWithScreenCapture(
            CucliCommandContext context, ShotRequest request)
        {
            Texture2D texture = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture(request.SuperSize);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "ScreenCapture 失败：" + exception.GetBaseException().Message);
            }

            if (texture == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "ScreenCapture 没有返回图像。当前环境（batchmode 或没有 Game View）无法捕获 game_view 截图；"
                    + "请改用 camera / viewPosition / batch 参数，或使用 capture_source=scene_view。",
                    new JObject
                    {
                        ["captureSource"] = "game_view",
                        ["isBatchMode"] = Application.isBatchMode,
                        ["hasGameView"] = !Application.isBatchMode
                    });
            }

            byte[] png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);

            string writeError;
            if (!TryWriteScreenshot(request, png, out writeError))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "写入截图失败：" + writeError);
            }

            context.AddArtifact(request.AbsolutePath);

            var data = new JObject
            {
                ["path"] = request.RelativePath,
                ["fullPath"] = request.AbsolutePath,
                ["superSize"] = request.SuperSize,
                ["isAsync"] = false,
                ["captureSource"] = "game_view"
            };

            if (request.IncludeImage) AddInlineImage(data, request.AbsolutePath, request.MaxResolution);

            return CucliCommandResult.Ok(data, "已保存 game_view 截图：" + request.RelativePath + "。");
        }

        private static CucliCommandResult CaptureRendered(CucliCommandContext context, ShotRequest request)
        {
            if (request.CaptureSource == "scene_view")
            {
                SceneView view = SceneView.lastActiveSceneView;
                if (view == null || view.camera == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        "SceneView 不可用（没有 lastActiveSceneView 或其 camera 为 null），无法截图。",
                        new JObject { ["sceneViewAvailable"] = false });
                }

                int width = Mathf.Max(1, (int)view.position.width);
                int height = Mathf.Max(1, (int)view.position.height);

                string renderError;
                if (!RenderCameraToFile(view.camera, width, height, request.AbsolutePath, out renderError))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "scene_view 渲染失败：" + renderError);
                }

                context.AddArtifact(request.AbsolutePath);

                var sceneData = new JObject
                {
                    ["path"] = request.RelativePath,
                    ["fullPath"] = request.AbsolutePath,
                    ["superSize"] = request.SuperSize,
                    ["isAsync"] = false,
                    ["camera"] = view.camera.name,
                    ["captureSource"] = "scene_view",
                    ["captureMode"] = "scene_view_viewport",
                    ["sceneViewName"] = "SceneView",
                    ["viewportWidth"] = width,
                    ["viewportHeight"] = height
                };

                if (request.HasViewTarget && request.ViewTarget != null)
                {
                    sceneData["viewTarget"] = request.ViewTarget.name;
                }

                if (request.IncludeImage) AddInlineImage(sceneData, request.AbsolutePath, request.MaxResolution);
                return CucliCommandResult.Ok(sceneData, "已保存 scene_view 截图：" + request.RelativePath + "。");
            }

            if (request.Batch.Length == 0)
            {
                return CaptureFromCameraOrPosition(context, request);
            }

            return CaptureBatch(context, request);
        }

        private static CucliCommandResult CaptureFromCameraOrPosition(
            CucliCommandContext context, ShotRequest request)
        {
            int width = Mathf.Max(1, (Screen.width > 0 ? Screen.width : 1280) * request.SuperSize);
            int height = Mathf.Max(1, (Screen.height > 0 ? Screen.height : 720) * request.SuperSize);

            Camera camera = null;
            GameObject temporary = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(request.CameraSpec))
                {
                    string cameraError;
                    camera = FindCamera(request.CameraSpec, out cameraError);
                    if (cameraError != null)
                    {
                        return CucliCommandResult.Fail(
                            CucliErrors.TargetNotFound, cameraError,
                            new JObject { ["camera"] = request.CameraSpec });
                    }
                }
                else
                {
                    Camera created;
                    temporary = CreateTemporaryCamera("cucli-screenshot-camera", out created);
                    camera = created;
                    camera.transform.position = request.ViewPosition;
                    if (request.HasViewRotation)
                    {
                        camera.transform.rotation = Quaternion.Euler(request.ViewRotation);
                    }
                    else if (request.HasViewTarget && request.ViewTarget != null)
                    {
                        camera.transform.LookAt(request.ViewTarget.transform.position);
                    }
                }

                string renderError;
                if (!RenderCameraToFile(camera, width, height, request.AbsolutePath, out renderError))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "相机渲染失败：" + renderError);
                }

                context.AddArtifact(request.AbsolutePath);

                var data = new JObject
                {
                    ["path"] = request.RelativePath,
                    ["fullPath"] = request.AbsolutePath,
                    ["superSize"] = request.SuperSize,
                    ["isAsync"] = false,
                    ["camera"] = camera.name,
                    ["captureSource"] = "game_view",
                    ["width"] = width,
                    ["height"] = height
                };

                if (request.HasViewPosition)
                {
                    data["viewPosition"] = new JArray(
                        request.ViewPosition.x, request.ViewPosition.y, request.ViewPosition.z);
                }

                if (request.HasViewTarget && request.ViewTarget != null)
                {
                    Vector3 targetPosition = request.ViewTarget.transform.position;
                    data["viewTarget"] = new JArray(targetPosition.x, targetPosition.y, targetPosition.z);
                }

                if (request.IncludeImage) AddInlineImage(data, request.AbsolutePath, request.MaxResolution);

                return CucliCommandResult.Ok(data, "已保存相机截图：" + request.RelativePath + "。");
            }
            finally
            {
                if (temporary != null) Object.DestroyImmediate(temporary);
            }
        }

        private static CucliCommandResult CaptureBatch(CucliCommandContext context, ShotRequest request)
        {
            Bounds bounds;
            if (!TrySceneBounds(out bounds))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "batch 截图需要至少一个带 Renderer 的物体来推算场景包围盒，当前场景中没有找到。",
                    new JObject { ["batch"] = request.Batch });
            }

            if (request.HasViewTarget && request.ViewTarget != null)
            {
                bounds = CalculateBounds(request.ViewTarget);
            }

            Vector3 center = bounds.center;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
            float distance = request.HasOrbitDistance
                ? request.OrbitDistance
                : radius / Mathf.Tan(request.OrbitFov * 0.5f * Mathf.Deg2Rad) * 1.6f;
            if (distance < 0.5f) distance = 0.5f;

            var angles = new List<float>();
            if (request.Batch == "surround")
            {
                for (int index = 0; index < 6; index++) angles.Add(index * 60f);
            }
            else
            {
                float step = 360f / request.OrbitAngles;
                for (int index = 0; index < request.OrbitAngles; index++) angles.Add(index * step);
            }

            float[] elevations = request.Batch == "surround"
                ? new[] { 15f }
                : request.OrbitElevations;

            int width = Mathf.Max(1, (Screen.width > 0 ? Screen.width : 1280) * request.SuperSize);
            int height = Mathf.Max(1, (Screen.height > 0 ? Screen.height : 720) * request.SuperSize);

            string fileName = CucliAssets.FileNameWithoutExtension(request.RelativePath);
            string folder = request.OutputFolder;

            Camera camera = null;
            GameObject temporary = null;
            var shots = new JArray();
            string firstPath = null;

            try
            {
                Camera created;
                temporary = CreateTemporaryCamera("cucli-batch-camera", out created);
                camera = created;
                camera.fieldOfView = request.OrbitFov;

                foreach (float elevation in elevations)
                {
                    foreach (float angle in angles)
                    {
                        context.ThrowIfCancelled();

                        Quaternion rotation = Quaternion.Euler(-elevation, angle, 0f);
                        Vector3 position = center - (rotation * Vector3.forward) * distance;

                        string shotName = fileName + "_a" + Mathf.RoundToInt(angle)
                            + "_e" + Mathf.RoundToInt(elevation) + ".png";
                        string shotRelative = folder.TrimEnd('/') + "/" + shotName;
                        string shotAbsolute = CucliAssets.ToAbsolute(shotRelative);

                        camera.transform.position = position;
                        camera.transform.rotation = rotation;

                        string renderError;
                        if (!RenderCameraToFile(camera, width, height, shotAbsolute, out renderError))
                        {
                            return CucliCommandResult.Fail(
                                CucliErrors.CommandFailed,
                                "批量截图在 angle=" + angle + ", elevation=" + elevation + " 失败：" + renderError);
                        }

                        context.AddArtifact(shotAbsolute);
                        if (firstPath == null) firstPath = shotAbsolute;

                        shots.Add(new JObject
                        {
                            ["angle"] = angle,
                            ["azimuth"] = angle,
                            ["elevation"] = elevation,
                            ["position"] = new JArray(position.x, position.y, position.z),
                            ["path"] = shotRelative,
                            ["fullPath"] = shotAbsolute
                        });
                    }
                }
            }
            finally
            {
                if (temporary != null) Object.DestroyImmediate(temporary);
            }

            var data = new JObject
            {
                ["batch"] = request.Batch,
                ["sceneCenter"] = new JArray(center.x, center.y, center.z),
                ["sceneRadius"] = radius,
                ["orbitRadius"] = distance,
                ["orbitAngles"] = request.Batch == "orbit" ? request.OrbitAngles : angles.Count,
                ["orbitFov"] = request.OrbitFov,
                ["outputFolder"] = folder,
                ["path"] = request.RelativePath,
                ["fullPath"] = request.AbsolutePath,
                ["shots"] = shots,
                ["shotCount"] = shots.Count
            };

            if (request.Batch == "orbit")
            {
                var elevationArray = new JArray();
                foreach (float elevation in elevations) elevationArray.Add(elevation);
                data["orbitElevations"] = elevationArray;
            }

            if (request.IncludeImage && firstPath != null)
            {
                AddInlineImage(data, firstPath, request.MaxResolution);
            }

            return CucliCommandResult.Ok(
                data, "批量截图完成，共写出 " + shots.Count + " 张 PNG 到 " + folder + "。");
        }

        private static bool TrySceneBounds(out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool found = false;

            foreach (GameObject gameObject in CucliObjectResolver.FindAllInLoadedScenes())
            {
                if (gameObject == null) continue;
                Renderer[] renderers = gameObject.GetComponents<Renderer>();
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null) continue;
                    if (!found)
                    {
                        bounds = renderer.bounds;
                        found = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }
            }

            return found;
        }

        private static Camera FindCamera(string spec, out string error)
        {
            error = null;
            string value = spec.Trim();

            int instanceId;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
            {
                Camera byId = EditorUtility.InstanceIDToObject(instanceId) as Camera;
                if (byId != null) return byId;
                error = "instance id " + instanceId + " 不是已加载的 Camera。";
                return null;
            }

            if (value.IndexOf('/') >= 0)
            {
                string pathError;
                GameObject byPath = CucliObjectResolver.FindByHierarchyPath(value, out pathError);
                if (byPath != null)
                {
                    Camera byPathCamera = byPath.GetComponent<Camera>();
                    if (byPathCamera != null) return byPathCamera;
                    error = "层级路径 '" + value + "' 上的物体没有 Camera 组件。";
                    return null;
                }

                error = pathError ?? ("找不到层级路径 '" + value + "'。");
                return null;
            }

            Camera match = null;
            int matches = 0;
            foreach (GameObject candidate in CucliObjectResolver.FindAllInLoadedScenes())
            {
                if (candidate == null || !string.Equals(candidate.name, value, StringComparison.Ordinal)) continue;
                Camera candidateCamera = candidate.GetComponent<Camera>();
                if (candidateCamera == null) continue;

                match = candidateCamera;
                matches++;
            }

            if (matches == 0)
            {
                error = "场景中找不到名为 '" + value + "' 的 Camera。";
                return null;
            }

            if (matches > 1)
            {
                error = "有 " + matches + " 个 Camera 名为 '" + value + "'，请改用层级路径或 instanceID。";
                return null;
            }

            return match;
        }

        private static GameObject CreateTemporaryCamera(string name, out Camera camera)
        {
            GameObject gameObject = EditorUtility.CreateGameObjectWithHideFlags(
                name, HideFlags.HideAndDontSave, typeof(Camera));
            camera = gameObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 5000f;
            camera.fieldOfView = 60f;
            return gameObject;
        }

        private static bool RenderCameraToFile(
            Camera camera, int width, int height, string absolutePath, out string error)
        {
            error = null;
            if (camera == null)
            {
                error = "相机为 null。";
                return false;
            }

            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D texture = null;

            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;

                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                byte[] png = texture.EncodeToPNG();

                string directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(absolutePath, png);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
            finally
            {
                if (texture != null) Object.DestroyImmediate(texture);
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);

                string relative = CucliAssets.NormalizeAssetPath(absolutePath);
                if (error == null && relative != null
                    && relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceUpdate);
                }
            }
        }

        private static bool TryWriteScreenshot(ShotRequest request, byte[] png, out string error)
        {
            error = null;
            try
            {
                string directory = Path.GetDirectoryName(request.AbsolutePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(request.AbsolutePath, png);
                if (request.RelativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    AssetDatabase.ImportAsset(request.RelativePath, ImportAssetOptions.ForceUpdate);
                }

                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>
        /// Adds a bounded base64 preview. The long edge is clamped to <paramref name="maxResolution"/>
        /// so a 4K screenshot cannot blow up the CLI response.
        /// </summary>
        private static void AddInlineImage(JObject data, string absolutePath, int maxResolution)
        {
            Texture2D source = null;
            Texture2D scaled = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(absolutePath);
                source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!source.LoadImage(bytes))
                {
                    data["imageError"] = "无法解析 PNG：" + absolutePath;
                    return;
                }

                int width = source.width;
                int height = source.height;
                int longest = Math.Max(width, height);
                byte[] png;

                if (maxResolution > 0 && longest > maxResolution)
                {
                    float scale = (float)maxResolution / longest;
                    int targetWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
                    int targetHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));

                    RenderTexture temporary = RenderTexture.GetTemporary(
                        targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
                    RenderTexture previousActive = RenderTexture.active;
                    try
                    {
                        Graphics.Blit(source, temporary);
                        RenderTexture.active = temporary;
                        scaled = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
                        scaled.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                        scaled.Apply();
                        png = scaled.EncodeToPNG();
                    }
                    finally
                    {
                        RenderTexture.active = previousActive;
                        RenderTexture.ReleaseTemporary(temporary);
                    }

                    width = targetWidth;
                    height = targetHeight;
                }
                else
                {
                    png = bytes;
                }

                data["imageBase64"] = Convert.ToBase64String(png);
                data["imageWidth"] = width;
                data["imageHeight"] = height;
                data["imageTruncated"] = longest > maxResolution && maxResolution > 0;
            }
            catch (Exception exception)
            {
                data["imageError"] = exception.GetBaseException().Message;
            }
            finally
            {
                if (scaled != null) Object.DestroyImmediate(scaled);
                if (source != null) Object.DestroyImmediate(source);
            }
        }

        private static void AfterFrames(CucliCommandContext context, int frames, Action then)
        {
            int remaining = Mathf.Max(1, frames);
            EditorApplication.CallbackFunction tick = null;
            tick = delegate
            {
                if (context.Cancellation.IsCancellationRequested)
                {
                    EditorApplication.update -= tick;
                    then();
                    return;
                }

                remaining--;
                if (remaining > 0) return;

                EditorApplication.update -= tick;
                then();
            };

            EditorApplication.update += tick;
        }

        // ── shared helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a payload value that names a GameObject: a string name / hierarchy path /
        /// instance id, or an object with instanceId / path / name. Null and "" mean "no object".
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
            value = value.Trim();

            string resolveError;
            var resolverArgs = new CucliArgs(new JObject { ["target"] = value });
            GameObject match = CucliObjectResolver.ResolveGameObject(resolverArgs, out resolveError);
            if (match == null) error = resolveError ?? ("找不到目标物体 '" + value + "'。");
            return match;
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

        private static bool AnyLoadedSceneDirty()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty) return true;
            }

            return false;
        }

        private static JArray DirtySceneNames()
        {
            var names = new JArray();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty) names.Add(scene.name);
            }

            return names;
        }

        private static JArray LoadedSceneNames()
        {
            var names = new JArray();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid()) names.Add(scene.name);
            }

            return names;
        }

        private static IEnumerable<GameObject> AllRoots()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                GameObject[] roots;
                try
                {
                    roots = scene.GetRootGameObjects();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (GameObject root in roots) yield return root;
            }
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
    }
}
