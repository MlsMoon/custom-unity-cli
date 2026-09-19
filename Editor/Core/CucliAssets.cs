using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// One place that knows how Unity addresses things on disk. Every command funnels its paths
    /// through here so that <c>Assets/Foo</c>, <c>assets/foo</c>, <c>.\Assets\Foo</c> and an
    /// absolute path all resolve identically - a very common source of agent mistakes.
    /// </summary>
    public static class CucliAssets
    {
        public const string AssetsPrefix = "Assets/";
        public const string PackagesPrefix = "Packages/";

        public static string GetAssetPath(Object value)
        {
            if (value == null) return string.Empty;
            try
            {
                return AssetDatabase.GetAssetPath(value) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static string GetGuid(Object value)
        {
            string path = GetAssetPath(value);
            return string.IsNullOrEmpty(path) ? string.Empty : AssetPathToGuid(path);
        }

        public static string AssetPathToGuid(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            return normalized == null ? string.Empty : AssetDatabase.AssetPathToGUID(normalized);
        }

        public static string GuidToAssetPath(string guid)
        {
            return string.IsNullOrWhiteSpace(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
        }

        /// <summary>Asset path of the <c>.cs</c> file that declares a component, when it has one.</summary>
        public static string GetScriptAssetPath(Component component)
        {
            var behaviour = component as MonoBehaviour;
            if (behaviour == null) return string.Empty;
            try
            {
                MonoScript script = MonoScript.FromMonoBehaviour(behaviour);
                return script == null ? string.Empty : AssetDatabase.GetAssetPath(script) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Canonicalises any spelling of a project path into <c>Assets/...</c> or
        /// <c>Packages/...</c>. Returns null when the path escapes the project, which lets callers
        /// reject rather than silently write outside the workspace.
        /// </summary>
        public static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string value = path.Trim().Replace('\\', '/');
            while (value.Contains("//")) value = value.Replace("//", "/");

            string projectRoot = CucliPaths.ProjectRoot.Replace('\\', '/');
            if (value.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(projectRoot.Length + 1);
            }

            if (value.Length > 1 && value.EndsWith("/", StringComparison.Ordinal))
            {
                value = value.TrimEnd('/');
            }

            if (value.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(PackagesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return CanonicalizePrefix(value);
            }

            if (value.StartsWith("./", StringComparison.Ordinal)) value = value.Substring(2);
            if (value.StartsWith("/", StringComparison.Ordinal)) return null;

            // A bare relative path is interpreted as project-relative, matching how a human would
            // read "Prefabs/Hero.prefab" while sitting at the project root.
            return CanonicalizePrefix(AssetsPrefix + value);
        }

        private static string CanonicalizePrefix(string value)
        {
            if (value.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            {
                return AssetsPrefix + value.Substring(AssetsPrefix.Length);
            }

            if (value.StartsWith("packages/", StringComparison.OrdinalIgnoreCase))
            {
                return PackagesPrefix + value.Substring(PackagesPrefix.Length);
            }

            return value;
        }

        public static string ToAbsolute(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            if (normalized == null) return null;
            return Path.Combine(CucliPaths.ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        }

        public static bool Exists(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            return normalized != null && AssetDatabase.LoadMainAssetAtPath(normalized) != null;
        }

        public static bool FolderExists(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            return normalized != null && AssetDatabase.IsValidFolder(normalized);
        }

        /// <summary>Creates every missing folder along <paramref name="assetFolder"/>.</summary>
        public static bool EnsureFolder(string assetFolder)
        {
            string normalized = NormalizeAssetPath(assetFolder);
            if (normalized == null) return false;
            if (AssetDatabase.IsValidFolder(normalized)) return true;

            string[] segments = normalized.Split('/');
            if (segments.Length < 2) return false;

            string current = segments[0];
            for (int index = 1; index < segments.Length; index++)
            {
                string next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, segments[index]);
                    if (string.IsNullOrEmpty(guid)) return false;
                }

                current = next;
            }

            return true;
        }

        /// <summary>Parent folder of an asset path, or null for a root-level asset.</summary>
        public static string ParentFolder(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            if (normalized == null) return null;
            int index = normalized.LastIndexOf('/');
            return index <= 0 ? null : normalized.Substring(0, index);
        }

        public static string FileName(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return string.Empty;
            int index = assetPath.Replace('\\', '/').LastIndexOf('/');
            return index < 0 ? assetPath : assetPath.Substring(index + 1);
        }

        public static string FileNameWithoutExtension(string assetPath)
        {
            return Path.GetFileNameWithoutExtension(FileName(assetPath));
        }

        public static string Extension(string assetPath)
        {
            return Path.GetExtension(FileName(assetPath));
        }

        public static string GenerateUniquePath(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            return normalized == null ? null : AssetDatabase.GenerateUniqueAssetPath(normalized);
        }

        public static void Refresh(ImportAssetOptions options = ImportAssetOptions.Default)
        {
            AssetDatabase.Refresh(options);
        }

        public static void SaveAssets()
        {
            AssetDatabase.SaveAssets();
        }

        /// <summary>Finds assets of a type under an optional folder, honouring an optional name filter.</summary>
        public static string[] FindAssets(
            string typeName,
            string folder = null,
            string nameFilter = null,
            int limit = 500)
        {
            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(nameFilter)) filters.Add(nameFilter.Trim());
            if (!string.IsNullOrWhiteSpace(typeName)) filters.Add("t:" + typeName.Trim());

            string[] searchFolders = null;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                string normalized = NormalizeAssetPath(folder);
                if (normalized != null && AssetDatabase.IsValidFolder(normalized))
                {
                    searchFolders = new[] { normalized };
                }
            }

            string[] guids = searchFolders == null
                ? AssetDatabase.FindAssets(string.Join(" ", filters))
                : AssetDatabase.FindAssets(string.Join(" ", filters), searchFolders);

            var result = new List<string>(Math.Min(guids.Length, limit));
            for (int index = 0; index < guids.Length && result.Count < limit; index++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[index]);
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }

            return result.ToArray();
        }

        /// <summary>Loads an asset at a path, or returns null. Never throws on a bad type.</summary>
        public static T Load<T>(string assetPath) where T : Object
        {
            string normalized = NormalizeAssetPath(assetPath);
            if (normalized == null) return null;
            return AssetDatabase.LoadAssetAtPath<T>(normalized);
        }

        public static Object LoadMain(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            return normalized == null ? null : AssetDatabase.LoadMainAssetAtPath(normalized);
        }

        /// <summary>All loaded objects of a type, optionally restricted to one scene or asset folder.</summary>
        public static T[] FindAll<T>() where T : Object
        {
            return Resources.FindObjectsOfTypeAll<T>();
        }
    }
}
