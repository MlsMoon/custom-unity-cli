using System;
using System.IO;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Where project-specific extensions live.
    ///
    /// The package folder is a git repository; these roots are deliberately <em>siblings</em> of it,
    /// so anything generated for one project is outside the repository by construction. No ignore
    /// rule, no cleanup step, and no chance of a project's private behaviour leaking into the public
    /// package through a careless `git add`.
    ///
    ///     &lt;project&gt;/Assets/Plugins/CustomUnityCli/        the package (git repo, shared)
    ///     &lt;project&gt;/Assets/Plugins/CustomUnityCli.Local/  project-only C# (never committed)
    ///     &lt;project&gt;/.cucli/                               project-only declarations (never committed)
    /// </summary>
    internal static class CucliLocalPaths
    {
        internal const string LocalCodeFolderName = "CustomUnityCli.Local";
        internal const string LocalDataFolderName = ".cucli";

        /// <summary>Project-relative folder allowed to contain project-local command assemblies.</summary>
        internal static string LocalCodeRelative =>
            "Assets/Plugins/" + LocalCodeFolderName;

        internal static string LocalCodeRoot =>
            Path.Combine(CucliPaths.ProjectRoot, "Assets", "Plugins", LocalCodeFolderName);

        /// <summary>Project-relative data folder for declarative commands, history and proposals.</summary>
        internal static string LocalDataRelative => LocalDataFolderName;

        internal static string LocalDataRoot =>
            Path.Combine(CucliPaths.ProjectRoot, LocalDataFolderName);

        internal static string CommandsFolder => Path.Combine(LocalDataRoot, "commands");

        /// <summary>Generated candidates awaiting review. Never executed until promoted.</summary>
        internal static string ProposedFolder => Path.Combine(CommandsFolder, "proposed");

        internal static string DisabledFolder => Path.Combine(CommandsFolder, "disabled");

        internal static string LocalRegistry => Path.Combine(LocalDataRoot, "registry.json");

        internal static string HistoryFolder => Path.Combine(LocalDataRoot, "history");

        internal static string LocalCodeAsmdef =>
            Path.Combine(LocalCodeRoot, LocalCodeFolderName + ".asmdef");

        internal static void EnsureDataDirectories()
        {
            foreach (string path in new[] { LocalDataRoot, CommandsFolder, ProposedFolder, DisabledFolder })
            {
                Directory.CreateDirectory(path);
            }
        }

        internal static bool HasLocalCode => File.Exists(LocalCodeAsmdef);

        /// <summary>
        /// True when <paramref name="path"/> is inside the package repository. Used to refuse writes
        /// that would put project-specific content into the shared package.
        /// </summary>
        internal static bool IsInsidePackage(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) return false;

            string packageRoot = Path.GetFullPath(
                Path.Combine(CucliPaths.ProjectRoot, "Assets", "Plugins", "CustomUnityCli")) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(absolutePath) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ToProjectRelative(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) return string.Empty;
            string root = Path.GetFullPath(CucliPaths.ProjectRoot) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(absolutePath);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).Replace('\\', '/')
                : full.Replace('\\', '/');
        }
    }
}
