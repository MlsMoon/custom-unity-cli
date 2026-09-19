using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Resolves the many ways a caller can name a scene object into one canonical result.
    ///
    /// Every addressing mode from the MCP-for-Unity tools is supported, plus the sensible default
    /// used when only <c>target</c> is supplied: an instance id, then a hierarchy path, then a
    /// name. Activity is never a filter - inactive objects are found too, because "it is not in my
    /// hierarchy" is the single most common false negative when driving Unity from outside.
    /// </summary>
    public static class CucliObjectResolver
    {
        public const string DefaultSearchMethod = "by_id_or_name_or_path";

        private static readonly string[] SearchMethods =
        {
            "by_id_or_name_or_path", "by_id", "by_name", "by_path",
            "by_tag", "by_layer", "by_component"
        };

        public static GameObject RequireGameObject(CucliArgs args)
        {
            string error;
            GameObject gameObject = ResolveGameObject(args, out error);
            if (error != null) throw new CucliUsageException(CucliCommandResult.Fail(CucliErrors.TargetNotFound, error));
            return gameObject;
        }

        public static Transform RequireTransform(CucliArgs args)
        {
            GameObject gameObject = RequireGameObject(args);
            return gameObject == null ? null : gameObject.transform;
        }

        public static Component RequireComponent(CucliArgs args, string typeName)
        {
            GameObject gameObject = RequireGameObject(args);
            string error;
            Component component = ResolveComponent(gameObject, typeName, out error);
            if (error != null) throw new CucliUsageException(CucliCommandResult.Fail(CucliErrors.TargetNotFound, error));
            return component;
        }

        public static GameObject ResolveGameObject(CucliArgs args, out string error)
        {
            GameObject[] matches = ResolveGameObjects(args, out error, true);
            if (error != null) return null;
            return matches.Length == 0 ? null : matches[0];
        }

        /// <summary>
        /// Resolves every object matching the request. When <paramref name="single"/> is true a
        /// multi-match result is an <c>AMBIGUOUS</c> error rather than a silent first pick.
        /// </summary>
        public static GameObject[] ResolveGameObjects(CucliArgs args, out string error, bool single = false)
        {
            error = null;

            if (args.GetBool("selected") || args.GetBool("useSelection"))
            {
                GameObject active = Selection.activeGameObject;
                if (active == null)
                {
                    error = "nothing is selected in the Editor.";
                    return Array.Empty<GameObject>();
                }

                return new[] { active };
            }

            string searchMethod = (args.GetString("searchMethod") ?? args.GetString("search_method")
                ?? DefaultSearchMethod).Trim().ToLowerInvariant();

            switch (searchMethod)
            {
                case "by_id":
                    return ByInstanceId(args, out error);

                case "by_name":
                    return ByName(args, out error, single);

                case "by_path":
                    return ByPath(args, out error);

                case "by_tag":
                    return ByTag(args, out error, single);

                case "by_layer":
                    return ByLayer(args, out error, single);

                case "by_component":
                    return ByComponent(args, out error, single);

                case "by_id_or_name_or_path":
                    return ByIdNameOrPath(args, out error, single);

                default:
                    error = "searchMethod '" + searchMethod + "' is not supported. Supported: "
                        + string.Join(", ", SearchMethods) + ".";
                    return Array.Empty<GameObject>();
            }
        }

        private static GameObject[] ByIdNameOrPath(CucliArgs args, out string error, bool single)
        {
            // Assigned up front so every branch below is provably total for the out parameter.
            error = null;

            // 1. Explicit instance id.
            if (args.Has("instanceId") || args.Has("instance_id") || args.Has("id"))
            {
                return ByInstanceId(args, out error);
            }

            string target = Target(args);
            if (string.IsNullOrWhiteSpace(target))
            {
                error = "a target is required: pass 'target' (name, hierarchy path or instance id), "
                    + "'path', 'instanceId', or 'selected: true'.";
                return Array.Empty<GameObject>();
            }

            // 2. A numeric target is an instance id.
            int numeric;
            if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                GameObject byId = EditorUtility.InstanceIDToObject(numeric) as GameObject;
                if (byId != null)
                {
                    error = null;
                    return new[] { byId };
                }

                error = "no loaded GameObject has instance id " + numeric + ".";
                return Array.Empty<GameObject>();
            }

            // 3. A target containing a separator is a hierarchy path.
            if (target.IndexOf('/') >= 0)
            {
                GameObject byPath = FindByHierarchyPath(target, out error);
                if (error != null) return Array.Empty<GameObject>();
                if (byPath != null) return new[] { byPath };
                error = "no GameObject is at hierarchy path '" + target + "'.";
                return Array.Empty<GameObject>();
            }

            // 4. Otherwise a name.
            return ByName(args, out error, single);
        }

        private static string Target(CucliArgs args)
        {
            return args.GetString("target")
                ?? args.GetString("hierarchyPath")
                ?? args.GetString("hierarchy_path")
                ?? args.GetString("path")
                ?? args.GetString("name");
        }

        private static GameObject[] ByInstanceId(CucliArgs args, out string error)
        {
            error = null;
            int id = args.GetInt("instanceId", args.GetInt("instance_id", args.GetInt("id", 0)));
            if (id == 0)
            {
                string text = Target(args);
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    error = "an integer 'instanceId' is required.";
                    return Array.Empty<GameObject>();
                }
            }

            Object resolved = EditorUtility.InstanceIDToObject(id);
            var gameObject = resolved as GameObject;
            if (gameObject == null)
            {
                var component = resolved as Component;
                if (component != null) gameObject = component.gameObject;
            }

            if (gameObject == null)
            {
                error = "no loaded object has instance id " + id + ".";
                return Array.Empty<GameObject>();
            }

            return new[] { gameObject };
        }

        private static GameObject[] ByPath(CucliArgs args, out string error)
        {
            string path = args.GetString("path") ?? args.GetString("hierarchyPath") ?? Target(args);
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "a hierarchy 'path' is required.";
                return Array.Empty<GameObject>();
            }

            GameObject match = FindByHierarchyPath(path, out error);
            if (error != null) return Array.Empty<GameObject>();
            if (match == null)
            {
                error = "no GameObject is at hierarchy path '" + path + "'.";
                return Array.Empty<GameObject>();
            }

            return new[] { match };
        }

        private static GameObject[] ByName(CucliArgs args, out string error, bool single)
        {
            error = null;
            string name = args.GetString("name") ?? Target(args);
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "a GameObject 'name' is required.";
                return Array.Empty<GameObject>();
            }

            GameObject[] matches = FindAllInLoadedScenes()
                .Where(candidate => string.Equals(candidate.name, name, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length == 0)
            {
                error = "no GameObject named '" + name + "' exists in the loaded scenes.";
                return Array.Empty<GameObject>();
            }

            if (single && matches.Length > 1 && !args.GetBool("allowMultiple", false))
            {
                error = matches.Length + " GameObjects are named '" + name
                    + "'. Pass a hierarchy path or an instanceId, or set allowMultiple: true.";
                return Array.Empty<GameObject>();
            }

            return matches;
        }

        private static GameObject[] ByTag(CucliArgs args, out string error, bool single)
        {
            error = null;
            string tag = args.GetString("tag") ?? Target(args);
            if (string.IsNullOrWhiteSpace(tag))
            {
                error = "a 'tag' is required for by_tag.";
                return Array.Empty<GameObject>();
            }

            GameObject[] matches = FindAllInLoadedScenes()
                .Where(candidate => HasTag(candidate, tag))
                .ToArray();

            if (matches.Length == 0)
            {
                error = "no GameObject is tagged '" + tag + "' in the loaded scenes.";
                return Array.Empty<GameObject>();
            }

            if (single && matches.Length > 1 && !args.GetBool("allowMultiple", false))
            {
                error = matches.Length + " GameObjects are tagged '" + tag + "'. Set allowMultiple: true.";
                return Array.Empty<GameObject>();
            }

            return matches;
        }

        private static bool HasTag(GameObject candidate, string tag)
        {
            try
            {
                return string.Equals(candidate.tag, tag, StringComparison.Ordinal);
            }
            catch (UnityException)
            {
                // Throws for objects whose tag was removed from the project.
                return false;
            }
        }

        private static GameObject[] ByLayer(CucliArgs args, out string error, bool single)
        {
            error = null;
            int layer = ResolveLayer(args, out error);
            if (error != null) return Array.Empty<GameObject>();

            GameObject[] matches = FindAllInLoadedScenes()
                .Where(candidate => candidate.layer == layer)
                .ToArray();

            if (matches.Length == 0)
            {
                error = "no GameObject is on layer " + layer + " (" + LayerMask.LayerToName(layer) + ").";
                return Array.Empty<GameObject>();
            }

            if (single && matches.Length > 1 && !args.GetBool("allowMultiple", false))
            {
                error = matches.Length + " GameObjects are on layer " + layer + ". Set allowMultiple: true.";
                return Array.Empty<GameObject>();
            }

            return matches;
        }

        public static int ResolveLayer(CucliArgs args, out string error)
        {
            error = null;
            if (args.Has("layerName") || args.Has("layer_name"))
            {
                string layerName = args.GetString("layerName") ?? args.GetString("layer_name");
                int byName = LayerMask.NameToLayer(layerName);
                if (byName < 0)
                {
                    error = "no layer is named '" + layerName + "'.";
                    return 0;
                }

                return byName;
            }

            if (args.Has("layer"))
            {
                string value = args.GetString("layer");
                int numeric;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                {
                    if (numeric < 0 || numeric > 31)
                    {
                        error = "layer index must be between 0 and 31.";
                        return 0;
                    }

                    return numeric;
                }

                int byName = LayerMask.NameToLayer(value);
                if (byName < 0)
                {
                    error = "no layer is named '" + value + "'.";
                    return 0;
                }

                return byName;
            }

            error = "a 'layer' index or 'layerName' is required.";
            return 0;
        }

        private static GameObject[] ByComponent(CucliArgs args, out string error, bool single)
        {
            error = null;
            string typeName = args.GetString("component") ?? args.GetString("componentType") ?? Target(args);
            if (string.IsNullOrWhiteSpace(typeName))
            {
                error = "a component type name is required for by_component.";
                return Array.Empty<GameObject>();
            }

            Type type = CucliReflection.FindType(typeName, out error);
            if (error != null) return Array.Empty<GameObject>();
            if (!typeof(Component).IsAssignableFrom(type))
            {
                error = "'" + typeName + "' is not a Component type.";
                return Array.Empty<GameObject>();
            }

            var matches = new List<GameObject>();
            foreach (GameObject candidate in FindAllInLoadedScenes())
            {
                if (candidate.GetComponent(type) != null) matches.Add(candidate);
            }

            if (matches.Count == 0)
            {
                error = "no GameObject in the loaded scenes has a " + type.Name + " component.";
                return Array.Empty<GameObject>();
            }

            if (single && matches.Count > 1 && !args.GetBool("allowMultiple", false))
            {
                error = matches.Count + " GameObjects have a " + type.Name + ". Set allowMultiple: true.";
                return Array.Empty<GameObject>();
            }

            return matches.ToArray();
        }

        /// <summary>
        /// Every GameObject in every loaded scene, including inactive ones and objects inside a
        /// prefab stage. This is the workhorse behind name / tag / layer / component searches.
        /// </summary>
        public static IEnumerable<GameObject> FindAllInLoadedScenes()
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

                foreach (GameObject root in roots)
                {
                    foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        yield return transform.gameObject;
                    }
                }
            }
        }

        /// <summary>Walks a slash separated path from the scene roots, matching inactive objects.</summary>
        public static GameObject FindByHierarchyPath(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) return null;

            string normalized = path.Trim().Replace('\\', '/').Trim('/');
            string[] segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            int start = 0;
            var matches = new List<GameObject>();

            // An optional leading scene name lets callers disambiguate across loaded scenes.
            if (segments.Length > 1 && IsSceneName(segments[0]))
            {
                Scene scene = SceneManager.GetSceneByName(segments[0]);
                if (scene.IsValid() && scene.isLoaded)
                {
                    matches.AddRange(scene.GetRootGameObjects().Where(root => root.name == segments[1]));
                    start = 2;
                }
            }

            if (matches.Count == 0)
            {
                start = 0;
                foreach (GameObject root in AllRoots())
                {
                    if (root.name == segments[0]) matches.Add(root);
                }

                start = 1;
            }

            for (int index = start; index < segments.Length && matches.Count > 0; index++)
            {
                var next = new List<GameObject>();
                foreach (GameObject current in matches)
                {
                    Transform transform = current.transform;
                    for (int child = 0; child < transform.childCount; child++)
                    {
                        Transform candidate = transform.GetChild(child);
                        if (candidate.name == segments[index]) next.Add(candidate.gameObject);
                    }
                }

                matches = next;
            }

            if (matches.Count == 0)
            {
                error = "no GameObject matches the hierarchy path '" + path + "'.";
                return null;
            }

            if (matches.Count > 1)
            {
                error = "the hierarchy path '" + path + "' matches " + matches.Count
                    + " objects across the loaded scenes; prefix it with the scene name.";
                return null;
            }

            return matches[0];
        }

        private static bool IsSceneName(string value)
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && string.Equals(scene.name, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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

        /// <summary>Full hierarchy path of an object, prefixed with its scene name when ambiguous.</summary>
        public static string HierarchyPath(GameObject gameObject, bool includeScene = false)
        {
            if (gameObject == null) return string.Empty;

            var segments = new List<string>();
            Transform current = gameObject.transform;
            while (current != null)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            string path = string.Join("/", segments);
            if (!includeScene) return path;

            Scene scene = gameObject.scene;
            return scene.IsValid() ? scene.name + "/" + path : path;
        }

        /// <summary>Finds a component by type name or short name, with a helpful miss message.</summary>
        public static Component ResolveComponent(GameObject gameObject, string typeName, out string error)
        {
            error = null;
            if (gameObject == null)
            {
                error = "cannot resolve a component on a null GameObject.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(typeName))
            {
                error = "a component type name is required.";
                return null;
            }

            string requested = typeName.Trim();
            Component[] components;
            try
            {
                components = gameObject.GetComponents<Component>();
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return null;
            }

            var available = new List<string>();

            // 1. Exact full name.
            foreach (Component component in components)
            {
                if (component == null) continue;
                Type type = component.GetType();
                available.Add(type.Name);
                if (string.Equals(type.FullName, requested, StringComparison.Ordinal)) return component;
            }

            // 2. Short name.
            foreach (Component component in components)
            {
                if (component == null) continue;
                if (string.Equals(component.GetType().Name, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return component;
                }
            }

            // 3. Namespace-qualified suffix match, e.g. "UI.Image" for "UnityEngine.UI.Image".
            foreach (Component component in components)
            {
                if (component == null) continue;
                string full = component.GetType().FullName ?? string.Empty;
                if (full.EndsWith("." + requested, StringComparison.OrdinalIgnoreCase)) return component;
            }

            error = gameObject.name + " has no " + requested + " component. It has: "
                + string.Join(", ", available) + ".";
            return null;
        }

        public static T ResolveComponent<T>(GameObject gameObject, out string error) where T : Component
        {
            error = null;
            if (gameObject == null)
            {
                error = "cannot resolve a component on a null GameObject.";
                return null;
            }

            T component = gameObject.GetComponent<T>();
            if (component == null)
            {
                error = gameObject.name + " has no " + typeof(T).Name + " component.";
                return null;
            }

            return component;
        }
    }
}
