using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli editor ...</c> - the Editor control surface: state, Play Mode, menu items, undo/redo,
    /// the active tool, project tags and layers, and the selection.
    ///
    /// This file is the reference example for a command group. Read it before adding a new group:
    /// it shows the metadata contract, the action dispatch pattern, how to resolve targets, how to
    /// describe objects without flooding the caller, and how to report precise error codes.
    /// </summary>
    internal sealed class EditorCommand : CucliCommand
    {
        public override string Name => "editor";

        public override string Summary =>
            "Inspect and control the Editor: state, Play Mode, menu items, undo/redo, tool, tags, layers, selection.";

        public override CucliActionInfo[] Actions => Declare(
            "state", "Report project identity, compile/import/Play Mode flags, open scenes and worker session.",
            "ping", "Round-trip liveness probe; the cheapest way to prove the queue is wired up.",
            "play", "Enter Play Mode.",
            "stop", "Leave Play Mode.",
            "pause", "Toggle pause. Only valid in Play Mode.",
            "step", "Advance one frame. Only valid while paused in Play Mode.",
            "menu", "Execute a menu item by its exact path, e.g. 'GameObject/Create Empty'.",
            "undo", "Perform one Undo.",
            "redo", "Perform one Redo.",
            "tool", "Set the active Scene view tool.",
            "tool-list", "List the available Scene view tools.",
            "tag-list", "List every project tag.",
            "tag-add", "Create a project tag.",
            "tag-remove", "Delete a project tag.",
            "layer-list", "List all 32 layers, marking the built-in ones.",
            "layer-add", "Create a user layer in the first free slot (8-31).",
            "layer-remove", "Clear a user layer. Built-in layers cannot be removed.",
            "selection-get", "Report the current selection.",
            "selection-set", "Replace the selection with resolved objects.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "editor.state.read", "editor.state.write", "project.settings.write", "editor.selection.write"
        };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""state"", ""ping"", ""play"", ""stop"", ""pause"", ""step"", ""menu"", ""undo"", ""redo"",
                 ""tool"", ""tool-list"", ""tag-list"", ""tag-add"", ""tag-remove"",
                 ""layer-list"", ""layer-add"", ""layer-remove"", ""selection-get"", ""selection-set""]
    },
    ""menuPath"": { ""type"": ""string"", ""description"": ""action=menu: exact menu path, '/' separated."" },
    ""tool"": { ""type"": ""string"", ""description"": ""action=tool: View, Move, Rotate, Rect, Scale, Custom, None."" },
    ""tag"": { ""type"": ""string"", ""description"": ""action=tag-add / tag-remove."" },
    ""layer"": { ""type"": ""string"", ""description"": ""action=layer-add / layer-remove: layer name."" },
    ""target"": { ""type"": ""string"", ""description"": ""action=selection-set: name, hierarchy path or instance id."" },
    ""paths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""action=selection-set: hierarchy paths."" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "state");
            switch (action)
            {
                case "state": return State(context);
                case "ping": return Ping(context);
                case "play": return Play(context);
                case "stop": return Stop(context);
                case "pause": return Pause(context);
                case "step": return Step(context);
                case "menu": return Menu(context);
                case "undo": return UndoRedo(true);
                case "redo": return UndoRedo(false);
                case "tool": return SetTool(context);
                case "tool-list": return ListTools(context);
                case "tag-list": return ListTags();
                case "tag-add": return AddTag(context);
                case "tag-remove": return RemoveTag(context);
                case "layer-list": return ListLayers();
                case "layer-add": return AddLayer(context);
                case "layer-remove": return RemoveLayer(context);
                case "selection-get": return GetSelection(context);
                case "selection-set": return SetSelection(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        private static CucliCommandResult Ping(CucliCommandContext context)
        {
            return CucliCommandResult.Ok(new
            {
                pong = true,
                sessionId = CucliWorkerBootstrap.CurrentSessionId,
                timestampUtc = DateTime.UtcNow.ToString("O"),
                jobId = context.JobId,
                commandCount = CucliCommandRegistry.Count
            }, "The Editor worker answered.");
        }

        private static CucliCommandResult State(CucliCommandContext context)
        {
            if (EditorApplication.isCompiling) context.Warn("Script compilation is in progress.");
            if (EditorApplication.isUpdating) context.Warn("The asset database is importing.");
            if (CucliCompilationStatus.HasErrors)
            {
                context.Warn("The last compilation produced errors: " + CucliCompilationStatus.FirstError);
            }

            UnityEngine.SceneManagement.Scene active =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            var scenes = new JArray();
            for (int index = 0; index < UnityEngine.SceneManagement.SceneManager.sceneCount; index++)
            {
                UnityEngine.SceneManagement.Scene scene =
                    UnityEngine.SceneManagement.SceneManager.GetSceneAt(index);
                if (!scene.IsValid()) continue;
                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["buildIndex"] = scene.buildIndex,
                    ["isLoaded"] = scene.isLoaded,
                    ["isDirty"] = scene.isDirty,
                    ["rootCount"] = scene.isLoaded ? scene.rootCount : 0,
                    ["isActive"] = scene == active
                });
            }

            return CucliCommandResult.Ok(new
            {
                project = new
                {
                    path = CucliPaths.ProjectRoot,
                    name = Application.productName,
                    company = Application.companyName,
                    dataPath = Application.dataPath
                },
                unity = new
                {
                    version = Application.unityVersion,
                    buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    applicationPath = EditorApplication.applicationPath
                },
                worker = new
                {
                    version = CucliPaths.WorkerVersion,
                    protocolVersion = CucliPaths.ProtocolVersion,
                    sessionId = CucliWorkerBootstrap.CurrentSessionId,
                    commandCount = CucliCommandRegistry.Count,
                    completedJobs = CucliJobProcessor.CompletedCount
                },
                state = new
                {
                    isPlaying = EditorApplication.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                    isBatchMode = Application.isBatchMode,
                    // Application.isFocused, not InternalEditorUtility.isApplicationActive: the latter
                    // is not part of the public Editor API surface, so it does not compile from a
                    // package assembly.
                    isFocused = Application.isFocused,
                    hasCompilationErrors = CucliCompilationStatus.HasErrors,
                    firstCompilationError = CucliCompilationStatus.FirstError,
                    activeTool = Tools.current.ToString(),
                    selectionCount = Selection.objects == null ? 0 : Selection.objects.Length,
                    activeScene = new
                    {
                        name = active.IsValid() ? active.name : string.Empty,
                        path = active.IsValid() ? active.path : string.Empty,
                        isDirty = active.IsValid() && active.isDirty
                    }
                },
                openScenes = scenes
            });
        }

        private static CucliCommandResult Play(CucliCommandContext context)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CucliCommandResult.Ok(
                    new { isPlaying = true, changed = false },
                    "The Editor is already in Play Mode.");
            }

            EditorApplication.isPlaying = true;
            return CucliCommandResult.Ok(
                new { isPlaying = EditorApplication.isPlayingOrWillChangePlaymode, changed = true },
                "Play Mode was requested; poll 'cucli editor state' until isPlaying is true.");
        }

        private static CucliCommandResult Stop(CucliCommandContext context)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CucliCommandResult.Ok(
                    new { isPlaying = false, changed = false },
                    "The Editor is not in Play Mode.");
            }

            EditorApplication.isPlaying = false;
            return CucliCommandResult.Ok(
                new { isPlaying = EditorApplication.isPlayingOrWillChangePlaymode, changed = true },
                "Leaving Play Mode was requested.");
        }

        private static CucliCommandResult Pause(CucliCommandContext context)
        {
            if (!EditorApplication.isPlaying)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "Pausing requires Play Mode. Start it with 'cucli editor play'.");
            }

            EditorApplication.isPaused = !EditorApplication.isPaused;
            return CucliCommandResult.Ok(new { isPaused = EditorApplication.isPaused });
        }

        private static CucliCommandResult Step(CucliCommandContext context)
        {
            if (!EditorApplication.isPlaying)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "Stepping requires Play Mode. Start it with 'cucli editor play'.");
            }

            EditorApplication.Step();
            return CucliCommandResult.Ok(new { stepped = true, isPaused = EditorApplication.isPaused });
        }

        private static CucliCommandResult Menu(CucliCommandContext context)
        {
            string menuPath = context.Input.GetString("menuPath") ?? context.Input.GetString("menu_path");
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return CucliCommandResult.Missing(
                    "menuPath", "Example: --menu-path \"GameObject/Create Empty\"");
            }

            bool executed = EditorApplication.ExecuteMenuItem(menuPath.Trim());
            if (!executed)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "The menu item '" + menuPath + "' does not exist, is disabled, or is not a menu item. "
                    + "Menu paths are case sensitive and use '/' as the separator. "
                    + "Only items in the main menu bar can be executed; windows opened from a menu "
                    + "are better driven with 'cucli code run'.");
            }

            return CucliCommandResult.Ok(
                new { menuPath = menuPath.Trim(), executed = true },
                "The menu item executed.");
        }

        /// <summary>
        /// Undo/redo through the Editor menu rather than the scripting API.
        ///
        /// <c>Undo.PerformUndo()</c> calls directly into Unity's native undo stack, and that call
        /// dereferences state the CLI cannot see. It segfaulted the Editor for real here: the crash
        /// dump for 2026-09-19 17:55:55 ends its managed stack at
        /// <c>UnityEditor.Undo:PerformUndo -> EditorCommand:UndoRedo</c>, taking the whole project
        /// down with it. <c>Undo.GetCurrentGroup()</c> reads the same subsystem, so it is gone too.
        ///
        /// <c>EditorApplication.ExecuteMenuItem("Edit/Undo")</c> is the path a user takes. It is fully
        /// supported, it keeps the menu's own enabled/disabled logic authoritative (so an empty stack
        /// is a clean refusal instead of a crash), and it returns false rather than tearing down the
        /// process when undo is unavailable.
        /// </summary>
        private static CucliCommandResult UndoRedo(bool undo)
        {
            string menuPath = undo ? "Edit/Undo" : "Edit/Redo";

            if (!EditorApplication.ExecuteMenuItem(menuPath))
            {
                string verb = undo ? "undo" : "redo";
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "Nothing to " + verb + ": the " + verb + " stack is empty, or " + menuPath
                    + " is currently unavailable because the Editor is importing, compiling or in Play Mode.",
                    new { menuPath, available = false });
            }

            return CucliCommandResult.Ok(new
            {
                action = undo ? "undo" : "redo",
                performed = true,
                menuPath
            });
        }

        private static CucliCommandResult SetTool(CucliCommandContext context)
        {
            string requested = context.Input.GetString("tool");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing(
                    "tool", "One of: " + string.Join(", ", Enum.GetNames(typeof(Tool))));
            }

            Tool tool;
            try
            {
                tool = (Tool)Enum.Parse(typeof(Tool), requested.Trim(), true);
            }
            catch (ArgumentException)
            {
                return CucliCommandResult.Invalid(
                    "tool",
                    "'" + requested + "' is not one of: " + string.Join(", ", Enum.GetNames(typeof(Tool))) + ".");
            }

            Tools.current = tool;
            return CucliCommandResult.Ok(new { tool = Tools.current.ToString() });
        }

        private static CucliCommandResult ListTools(CucliCommandContext context)
        {
            return CucliCommandResult.Ok(new
            {
                current = Tools.current.ToString(),
                available = Enum.GetNames(typeof(Tool))
            });
        }

        /// <summary>
        /// Tags and layers both live in ProjectSettings/TagManager.asset, so they share the same
        /// SerializedObject plumbing. Editing it through SerializedObject - rather than the
        /// TagManager API - is what makes the change land in version control correctly.
        /// </summary>
        private static SerializedObject OpenTagManager()
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            return assets == null || assets.Length == 0 ? null : new SerializedObject(assets[0]);
        }

        private static CucliCommandResult ListTags()
        {
            SerializedObject serialized = OpenTagManager();
            if (serialized == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "ProjectSettings/TagManager.asset could not be opened.");
            }

            SerializedProperty tags = serialized.FindProperty("tags");
            var result = new JArray();
            for (int index = 0; index < tags.arraySize; index++)
            {
                result.Add(tags.GetArrayElementAtIndex(index).stringValue);
            }

            return CucliCommandResult.Ok(new { tags = result, count = result.Count });
        }

        private static CucliCommandResult AddTag(CucliCommandContext context)
        {
            string tag = context.Input.GetString("tag");
            if (string.IsNullOrWhiteSpace(tag)) return CucliCommandResult.Missing("tag");
            tag = tag.Trim();

            SerializedObject serialized = OpenTagManager();
            if (serialized == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "TagManager.asset could not be opened.");
            }

            SerializedProperty tags = serialized.FindProperty("tags");
            for (int index = 0; index < tags.arraySize; index++)
            {
                if (string.Equals(tags.GetArrayElementAtIndex(index).stringValue, tag, StringComparison.Ordinal))
                {
                    return CucliCommandResult.Ok(new { tag, created = false }, "The tag already exists.");
                }
            }

            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            return CucliCommandResult.Ok(new { tag, created = true });
        }

        private static CucliCommandResult RemoveTag(CucliCommandContext context)
        {
            string tag = context.Input.GetString("tag");
            if (string.IsNullOrWhiteSpace(tag)) return CucliCommandResult.Missing("tag");

            SerializedObject serialized = OpenTagManager();
            if (serialized == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "TagManager.asset could not be opened.");
            }

            SerializedProperty tags = serialized.FindProperty("tags");
            for (int index = 0; index < tags.arraySize; index++)
            {
                if (!string.Equals(
                        tags.GetArrayElementAtIndex(index).stringValue, tag.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                tags.DeleteArrayElementAtIndex(index);
                serialized.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                return CucliCommandResult.Ok(new { tag = tag.Trim(), removed = true });
            }

            return CucliCommandResult.Fail(
                CucliErrors.TargetNotFound, "The tag '" + tag + "' does not exist.");
        }

        private static CucliCommandResult ListLayers()
        {
            var result = new JArray();
            for (int index = 0; index < 32; index++)
            {
                result.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = LayerMask.LayerToName(index),
                    ["builtin"] = index < 8
                });
            }

            return CucliCommandResult.Ok(new { layers = result, count = result.Count });
        }

        private static CucliCommandResult AddLayer(CucliCommandContext context)
        {
            string layerName = context.Input.GetString("layer") ?? context.Input.GetString("layerName");
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return CucliCommandResult.Missing("layer", "Provide the layer name to create.");
            }

            layerName = layerName.Trim();
            int existing = LayerMask.NameToLayer(layerName);
            if (existing >= 0)
            {
                return CucliCommandResult.Ok(
                    new { layer = layerName, index = existing, created = false },
                    "The layer already exists.");
            }

            SerializedObject serialized = OpenTagManager();
            if (serialized == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "TagManager.asset could not be opened.");
            }

            SerializedProperty layers = serialized.FindProperty("layers");
            for (int index = 8; index < layers.arraySize; index++)
            {
                SerializedProperty element = layers.GetArrayElementAtIndex(index);
                if (!string.IsNullOrEmpty(element.stringValue)) continue;

                element.stringValue = layerName;
                serialized.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                return CucliCommandResult.Ok(new { layer = layerName, index, created = true });
            }

            return CucliCommandResult.Fail(
                CucliErrors.CommandFailed,
                "Layers 8-31 are all in use; Unity cannot create another user layer.");
        }

        private static CucliCommandResult RemoveLayer(CucliCommandContext context)
        {
            string layerName = context.Input.GetString("layer") ?? context.Input.GetString("layerName");
            if (string.IsNullOrWhiteSpace(layerName)) return CucliCommandResult.Missing("layer");

            layerName = layerName.Trim();
            int index = LayerMask.NameToLayer(layerName);
            if (index < 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, "No layer is named '" + layerName + "'.");
            }

            if (index < 8)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "Layer " + index + " is a built-in Unity layer and cannot be removed.");
            }

            SerializedObject serialized = OpenTagManager();
            if (serialized == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "TagManager.asset could not be opened.");
            }

            SerializedProperty layers = serialized.FindProperty("layers");
            layers.GetArrayElementAtIndex(index).stringValue = string.Empty;
            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            return CucliCommandResult.Ok(new { layer = layerName, index, removed = true });
        }

        private static CucliCommandResult GetSelection(CucliCommandContext context)
        {
            CucliDescribe.Options options = CucliDescribe.OptionsFrom(context.Input);

            var objects = new JArray();
            foreach (Object selected in Selection.objects)
            {
                objects.Add(CucliDescribe.UnityObject(selected, options, 0));
            }

            var gameObjects = new JArray();
            foreach (GameObject selected in Selection.gameObjects)
            {
                gameObjects.Add(new JObject
                {
                    ["name"] = selected.name,
                    ["path"] = CucliObjectResolver.HierarchyPath(selected, true),
                    ["instanceId"] = selected.GetInstanceID()
                });
            }

            return CucliCommandResult.Ok(new
            {
                activeObject = Selection.activeObject == null ? null : Selection.activeObject.name,
                activeGameObject = Selection.activeGameObject == null
                    ? null
                    : CucliObjectResolver.HierarchyPath(Selection.activeGameObject, true),
                count = Selection.objects.Length,
                transformCount = Selection.transforms.Length,
                objects,
                gameObjects
            });
        }

        private static CucliCommandResult SetSelection(CucliCommandContext context)
        {
            var resolved = new List<Object>();

            string[] paths = context.Input.GetStringList("paths");
            if (paths.Length > 0)
            {
                foreach (string path in paths)
                {
                    string pathError;
                    GameObject match = CucliObjectResolver.FindByHierarchyPath(path, out pathError);
                    if (match == null)
                    {
                        return CucliCommandResult.Fail(
                            CucliErrors.TargetNotFound,
                            pathError ?? ("Nothing is at hierarchy path '" + path + "'."));
                    }

                    resolved.Add(match);
                }
            }
            else
            {
                string target = context.Input.GetString("target");
                if (string.IsNullOrWhiteSpace(target))
                {
                    Selection.objects = Array.Empty<Object>();
                    return CucliCommandResult.Ok(new { count = 0 }, "The selection was cleared.");
                }

                string error;
                GameObject[] matches = CucliObjectResolver.ResolveGameObjects(
                    new CucliArgs(new JObject { ["target"] = target, ["allowMultiple"] = true }), out error);
                if (error != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
                }

                foreach (GameObject match in matches) resolved.Add(match);
            }

            Selection.objects = resolved.ToArray();
            Selection.activeObject = resolved.Count > 0 ? resolved[0] : null;

            return CucliCommandResult.Ok(new
            {
                count = resolved.Count,
                names = resolved.Select(item => item.name).ToArray(),
                paths = resolved.Select(item => CucliObjectResolver.HierarchyPath(item as GameObject, true)).ToArray()
            });
        }
    }
}
