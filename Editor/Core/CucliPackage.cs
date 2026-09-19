using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Identity of the installed package: where it lives and whether a given path is inside it.
    ///
    /// This matters because the package and the host project have opposite lifecycles. The package is
    /// a published artifact replaced wholesale on upgrade; project-specific extension code must
    /// survive that and must never be published. The one arrangement that breaks the separation is
    /// project code dropped inside the package tree, so it is detectable rather than merely
    /// discouraged - see <see cref="CucliCommandRegistry"/>'s provenance, <c>cucli meta extensions</c>
    /// and <c>cucli extend list</c>.
    ///
    /// Resolution is deliberately layered, because a UPM install, an embedded package and a plain
    /// clone into <c>Assets/</c> all report differently through the Unity APIs.
    /// </summary>
    public static class CucliPackage
    {
        public const string PackageName = "com.igp.custom-unity-cli";
        public const string AssemblyName = "CustomUnityCli.Editor";
        public const string DefaultFolderName = "CustomUnityCli";

        private static string s_root;
        private static string s_rootResolution;

        /// <summary>Absolute path of the package root. Never null; may be empty if unresolvable.</summary>
        public static string Root
        {
            get
            {
                if (s_root == null) Resolve();
                return s_root;
            }
        }

        /// <summary>How <see cref="Root"/> was found, for diagnostics.</summary>
        public static string RootResolution
        {
            get
            {
                if (s_root == null) Resolve();
                return s_rootResolution;
            }
        }

        /// <summary>Project-relative package root with forward slashes, or empty if unresolvable.</summary>
        public static string RootAssetPath
        {
            get
            {
                string root = Root;
                if (string.IsNullOrEmpty(root)) return string.Empty;
                string projectRoot = CucliPaths.ProjectRoot;
                return root.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                    ? root.Substring(projectRoot.Length).TrimStart('\\', '/').Replace('\\', '/')
                    : root.Replace('\\', '/');
            }
        }

        /// <summary>True when the assembly is the package's own Editor assembly.</summary>
        public static bool IsPackageAssembly(Assembly assembly)
        {
            if (assembly == null) return false;
            return string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.Ordinal);
        }

        /// <summary>True when a command type ships with the package.</summary>
        public static bool IsPackageType(Type type)
        {
            return type != null && IsPackageAssembly(type.Assembly);
        }

        /// <summary>
        /// True when a file path or asset path sits inside the installed package tree. Accepts either
        /// form; a relative path is tried as-is and then against the project root.
        /// </summary>
        public static bool IsInsidePackage(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string root = Root;
            if (string.IsNullOrEmpty(root)) return false;

            string normalizedRoot = Normalize(root);
            string candidate = Normalize(path);

            if (!Path.IsPathRooted(candidate))
            {
                candidate = Normalize(Path.Combine(CucliPaths.ProjectRoot, path));
            }

            if (candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
            return candidate.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string result;
            try
            {
                result = Path.GetFullPath(value);
            }
            catch (Exception)
            {
                result = value;
            }

            return result.Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// Resolves the package root once, trying the three ways Unity reports an installed package.
        /// </summary>
        private static void Resolve()
        {
            // 1. The Package Manager knows, for both a UPM install and an embedded package.
            try
            {
                UnityEditor.PackageManager.PackageInfo info =
                    UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CucliPackage).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                {
                    s_root = Path.GetFullPath(info.resolvedPath);
                    s_rootResolution = "PackageInfo.FindForAssembly";
                    return;
                }
            }
            catch (Exception)
            {
                // Fall through: the package may be a plain clone under Assets, where this returns null.
            }

            // 2. Ask Unity where our own script file is, then walk up to the folder with package.json.
            //    This is the only method that works for a clone installed directly into Assets/.
            try
            {
                string[] guids = AssetDatabase.FindAssets(
                    typeof(CucliPackage).Name + " t:MonoScript");
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(assetPath)) continue;
                    if (!assetPath.EndsWith(
                            "/" + typeof(CucliPackage).Name + ".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string folder = Path.GetDirectoryName(Path.GetFullPath(assetPath));
                    for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(folder); depth++)
                    {
                        if (IsPackageManifest(folder))
                        {
                            s_root = folder;
                            s_rootResolution = "AssetDatabase walk-up from " + assetPath;
                            return;
                        }

                        folder = Path.GetDirectoryName(folder);
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the candidate list.
            }

            // 3. Well-known install locations, verified by their manifest rather than assumed.
            string projectRoot = CucliPaths.ProjectRoot;
            string[] candidates =
            {
                "Assets/Plugins/" + DefaultFolderName,
                "Assets/" + DefaultFolderName,
                "Packages/" + PackageName,
                "Assets/Plugins/CustomUnityCli"
            };

            foreach (string candidate in candidates)
            {
                string absolute = Path.Combine(
                    projectRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
                if (!IsPackageManifest(absolute)) continue;
                s_root = Path.GetFullPath(absolute);
                s_rootResolution = "candidate list (" + candidate + ")";
                return;
            }

            s_root = string.Empty;
            s_rootResolution = "unresolved";
        }

        private static bool IsPackageManifest(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
            string manifest = Path.Combine(folder, "package.json");
            if (!File.Exists(manifest)) return false;

            try
            {
                string text = File.ReadAllText(manifest, CucliJson.Utf8NoBom);
                return text.IndexOf("\"" + PackageName + "\"", StringComparison.Ordinal) >= 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static void ClearCache()
        {
            s_root = null;
            s_rootResolution = null;
        }
    }
}
