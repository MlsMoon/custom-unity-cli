using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <em>project-local extension area</em>: where a project grows its own command groups, and
    /// the single place that knows where that area is.
    ///
    /// It lives deliberately outside the package folder, because the two have opposite lifecycles.
    /// The package is a published artifact replaced wholesale on every upgrade; the extension area is
    /// project-specific code that must survive those upgrades and must never be published. Anything
    /// generated here belongs to the host project's version control, and
    /// <c>cucli meta export-registry</c> writes package commands only, so project code cannot leak
    /// upstream through the published <c>commands/registry.json</c>.
    ///
    /// <code>
    /// Assets/CustomUnityCli.Extensions/
    /// |-- README.md
    /// |-- extensions.json          optional: project aliases and offline groups
    /// |-- Notes/                   free-form project invariants an agent can rediscover
    /// `-- Editor/                  MUST be called Editor, see below
    ///     `-- Commands/            generated command groups
    /// </code>
    ///
    /// <b>Why no assembly definition is needed.</b> Scripts under any folder named <c>Editor</c> in
    /// <c>Assets</c> compile into the predefined <c>Assembly-CSharp-Editor</c>, and that assembly
    /// automatically references every assembly definition with <c>autoReferenced: true</c> - which
    /// includes the package's own <c>CustomUnityCli.Editor</c>. Dropping a file into
    /// <c>Editor/Commands/</c> is therefore the entire ceremony: no asmdef, no reference to wire, and
    /// no GUID to chase when the package is reinstalled at a different path.
    ///
    /// Conversely, a command source placed <em>outside</em> an <c>Editor</c> folder would compile into
    /// the runtime <c>Assembly-CSharp</c>, which cannot see <c>UnityEditor</c> or the Editor-only
    /// package assembly at all. That is why <see cref="CommandsFolder"/> is nested under
    /// <see cref="EditorSubfolder"/> rather than sitting at the root.
    ///
    /// Discovery uses a fixed candidate list rather than a setting, because a setting is exactly the
    /// mental overhead this tool exists to remove.
    /// </summary>
    public static class CucliExtensionPaths
    {
        /// <summary>Project-relative folders searched for extension code, most preferred first.</summary>
        public static readonly string[] CandidateFolders =
        {
            "Assets/CustomUnityCli.Extensions",
            "Assets/CustomUnityCliExtensions",
            "Assets/Plugins/CustomUnityCli.Extensions",
            "Assets/Editor/CustomUnityCli.Extensions",
            "Packages/CustomUnityCli.Extensions"
        };

        /// <summary>Where <c>cucli meta new-command</c> writes by default.</summary>
        public const string PreferredFolder = "Assets/CustomUnityCli.Extensions";

        /// <summary>Overrides <see cref="PreferredFolder"/>. Absolute or project-relative.</summary>
        public const string EnvironmentVariable = "CUCLI_EXTENSIONS";

        /// <summary>Subfolder that makes the sources compile into <c>Assembly-CSharp-Editor</c>.</summary>
        public const string EditorSubfolder = "Editor";

        /// <summary>Folder inside <see cref="EditorSubfolder"/> that holds generated groups.</summary>
        public const string CommandsFolderName = "Commands";

        /// <summary>Free-form notes an agent can rediscover: invariants, conventions, decisions.</summary>
        public const string NotesFolderName = "Notes";

        /// <summary>Optional project manifest: aliases and offline groups merged over the package registry.</summary>
        public const string ManifestFileName = "extensions.json";

        /// <summary>Generated offline catalog of the project's own groups.</summary>
        public const string LocalRegistryFileName = "registry.local.json";

        /// <summary>
        /// Absolute path of the extension root. Reading it creates nothing; call
        /// <see cref="EnsureScaffolded"/> to materialise the layout.
        /// </summary>
        public static string Root
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    string trimmed = configured.Trim();
                    return Path.IsPathRooted(trimmed)
                        ? Path.GetFullPath(trimmed)
                        : Path.GetFullPath(Path.Combine(CucliPaths.ProjectRoot, trimmed));
                }

                // An existing candidate wins over the preferred name, so a project that already put
                // its extensions somewhere is never silently given a second folder.
                string[] existing = Discover();
                if (existing.Length > 0)
                {
                    return Path.Combine(
                        CucliPaths.ProjectRoot, existing[0].Replace('/', Path.DirectorySeparatorChar));
                }

                return Path.Combine(
                    CucliPaths.ProjectRoot, PreferredFolder.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        /// <summary>Project-relative form of <see cref="Root"/>, with forward slashes.</summary>
        public static string RelativeRoot => ToRelative(Root);

        /// <summary>Where command sources go. Inside <c>Editor/</c> so they compile Editor-only.</summary>
        public static string CommandsFolder => Path.Combine(Root, EditorSubfolder, CommandsFolderName);

        public static string NotesFolder => Path.Combine(Root, NotesFolderName);

        public static string ManifestPath => Path.Combine(Root, ManifestFileName);

        public static string LocalRegistryPath => Path.Combine(Root, LocalRegistryFileName);

        public static bool Exists => Directory.Exists(Root);

        /// <summary>Command groups contributed by this package, as opposed to project-local ones.</summary>
        public static string PackageAssemblyName => typeof(CucliExtensionPaths).Assembly.GetName().Name;

        /// <summary>Existing extension folders, project-relative, in preference order.</summary>
        public static string[] Discover()
        {
            var found = new List<string>();
            foreach (string candidate in CandidateFolders)
            {
                string absolute = Path.Combine(
                    CucliPaths.ProjectRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(absolute)) found.Add(candidate);
            }

            return found.ToArray();
        }

        /// <summary>Extension source files, project-relative.</summary>
        public static string[] FindSources()
        {
            var files = new List<string>();
            foreach (string folder in Discover())
            {
                string absolute = Path.Combine(
                    CucliPaths.ProjectRoot, folder.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    foreach (string file in Directory.GetFiles(absolute, "*.cs", SearchOption.AllDirectories))
                    {
                        files.Add(ToRelative(file));
                    }
                }
                catch (IOException)
                {
                    // A locked or partially deleted folder is not worth failing a report over.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files.ToArray();
        }

        /// <summary>
        /// A command is project-local when it does not come from the package assembly. Anything
        /// compiled into <c>Assembly-CSharp-Editor</c> or another project assembly counts.
        /// </summary>
        public static bool IsProjectLocal(Type commandType)
        {
            if (commandType == null) return false;
            string assembly = commandType.Assembly.GetName().Name;
            return !string.Equals(assembly, PackageAssemblyName, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when a path sits inside the package tree. Project code there is the one arrangement
        /// that would end up in the public repository, so it is reported rather than accepted.
        ///
        /// Delegates to <see cref="CucliPackage.IsInsidePackage"/>, which resolves the real package
        /// root from this assembly's own manifest entry (or, for a plain clone, from the location of
        /// our own script) rather than assuming the folder is installed at its default path.
        /// </summary>
        public static bool IsInsidePackage(string assetPath)
        {
            return CucliPackage.IsInsidePackage(assetPath);
        }

        /// <summary>
        /// True when the extension root is somewhere Unity will compile. Checked before scaffolding so
        /// a misconfigured <c>CUCLI_EXTENSIONS</c> fails loudly instead of producing dead files that
        /// silently never register.
        /// </summary>
        public static bool IsCompilableRoot(out string reason)
        {
            reason = null;
            string relative = RelativeRoot;
            if (relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return true;
            if (relative.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return true;

            reason = "The extension root must be inside 'Assets/' or 'Packages/' for Unity to compile it, "
                + "but it resolves to '" + relative + "'. Fix or clear the " + EnvironmentVariable
                + " environment variable.";
            return false;
        }

        /// <summary>Creates the extension area, including a manifest and README that explain it.</summary>
        public static void EnsureScaffolded(out bool created)
        {
            created = !Directory.Exists(Root);
            Directory.CreateDirectory(CommandsFolder);
            Directory.CreateDirectory(NotesFolder);

            if (!File.Exists(ManifestPath))
            {
                var manifest = new Newtonsoft.Json.Linq.JObject
                {
                    ["schemaVersion"] = "cucli.extensions/v1",
                    ["description"] =
                        "Project-local custom-unity-cli extensions. This folder is intentionally outside "
                        + "the package: it holds command groups specific to THIS project and is never "
                        + "written into the custom-unity-cli repository.",
                    ["rootNamespace"] = "CustomUnityCli.Extensions",
                    ["commandsFolder"] = EditorSubfolder + "/" + CommandsFolderName,
                    ["notesFolder"] = NotesFolderName,
                    ["aliases"] = new Newtonsoft.Json.Linq.JObject(),
                    ["offline"] = new Newtonsoft.Json.Linq.JArray()
                };

                CucliPaths.WriteAtomic(
                    ManifestPath,
                    manifest.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
            }

            string readme = Path.Combine(Root, "README.md");
            if (!File.Exists(readme))
            {
                CucliPaths.WriteAtomic(readme, BuildReadme());
            }
        }

        private static string BuildReadme()
        {
            return "# Custom Unity CLI - project extensions\n"
                + "\n"
                + "Command groups in this folder belong to **this project only**. They are discovered\n"
                + "automatically: add a class implementing `CustomUnityCli.Editor.ICucliCommand`\n"
                + "(or deriving from `CucliCommand`) under `" + EditorSubfolder + "/" + CommandsFolderName
                + "/`, and `cucli <group> <action>` works after the next domain reload. Internal classes\n"
                + "are fine - the registry constructs them by reflection and does not require them public.\n"
                + "\n"
                + "No assembly definition is needed. Sources under an `" + EditorSubfolder + "` folder compile\n"
                + "into `Assembly-CSharp-Editor`, which already references the package assembly.\n"
                + "\n"
                + "## This folder is not part of the package\n"
                + "\n"
                + "`custom-unity-cli` lives in its own repository, and nothing here is ever written into\n"
                + "it. `cucli meta export-registry` exports **package commands only**, so project code\n"
                + "cannot leak upstream through `commands/registry.json`, and `cucli meta extensions`\n"
                + "verifies that.\n"
                + "\n"
                + "Version-control this folder in the project that owns it. Never copy it into a fork of\n"
                + "custom-unity-cli.\n"
                + "\n"
                + "## Scaffolding a new group\n"
                + "\n"
                + "```powershell\n"
                + "./Tools/cucli.ps1 meta new-command --group buildchecks --summary \"本项目的构建前检查\" `\n"
                + "  --action check --action report\n"
                + "```\n"
                + "\n"
                + "The generated file compiles as-is and registers on the next reload. Then iterate with\n"
                + "`cucli refresh compile --wait`, `cucli console --level error` and `cucli meta extensions`.\n"
                + "\n"
                + "## Recommended practice\n"
                + "\n"
                + "- Grow one group per project concern: asset conventions, build steps, content checks.\n"
                + "- Keep project invariants in `" + NotesFolderName + "/` so an agent can rediscover them.\n"
                + "- When a project command turns out to be useful everywhere, upstream it into the package\n"
                + "  deliberately, as a reviewed change rather than as a side effect of running a command.\n";
        }

        /// <summary>Manifest contents, or null when the area has not been scaffolded yet.</summary>
        public static Newtonsoft.Json.Linq.JObject TryReadManifest()
        {
            if (!File.Exists(ManifestPath)) return null;
            try
            {
                return CucliJson.ParseObject(File.ReadAllText(ManifestPath, CucliJson.Utf8NoBom));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Absolute path to a project-relative one with forward slashes.</summary>
        public static string ToRelative(string absolute)
        {
            if (string.IsNullOrWhiteSpace(absolute)) return string.Empty;
            string projectRoot = CucliPaths.ProjectRoot;
            if (absolute.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return absolute.Substring(projectRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
            }

            return absolute.Replace('\\', '/');
        }
    }
}
