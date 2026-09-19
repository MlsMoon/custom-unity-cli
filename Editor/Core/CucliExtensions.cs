using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace CustomUnityCli.Editor
{
    /// <summary>Where a registered command came from.</summary>
    internal enum CucliCommandSource
    {
        /// <summary>Shipped with the package. Contributes to the public repository.</summary>
        Package = 0,

        /// <summary>Written for this project only. Never exported into the package's static registry.</summary>
        Project = 1
    }

    /// <summary>
    /// Makes the tool self-extending.
    ///
    /// The registry already discovers any <see cref="ICucliCommand"/> in any loaded assembly, and the
    /// package assembly is <c>autoReferenced</c>, so project code can implement the interface with no
    /// asmdef, no manifest entry and no registration call. What this type adds is the missing half:
    ///
    /// <list type="bullet">
    /// <item>a conventional, discoverable home for project-local commands,</item>
    /// <item>a scaffold that writes a compiling command group there,</item>
    /// <item>provenance, so a project-local command is never mistaken for a shipped one.</item>
    /// </list>
    ///
    /// The extension root deliberately sits <em>outside</em> the package folder. A project that grows
    /// its own commands therefore cannot pollute the package's repository: those files belong to the
    /// host project and are committed to the host project's version control, not to this repo.
    /// </summary>
    internal static class CucliExtensions
    {
        /// <summary>
        /// Conventional extension root. It sits under <c>Assets/Editor/</c> so the files compile into
        /// <c>Assembly-CSharp-Editor</c> automatically, with no asmdef for the project to author.
        /// </summary>
        internal const string DefaultFolder = "Assets/Editor/CustomUnityCliExtensions";

        private const string TemplatePath = "Editor/Core/Templates/ProjectCommandTemplate.cs.txt";

        private static Dictionary<string, string> s_sourceFilesByAssemblyAndType;
        private static string s_resolvedRoot;

        internal static string PackageAssemblyName
        {
            get { return typeof(CucliExtensions).Assembly.GetName().Name; }
        }

        /// <summary>Extension root for this call: the <c>folder</c> argument, else the convention.</summary>
        internal static string ResolveRoot(CucliArgs args)
        {
            string requested = args.GetString("folder");
            if (string.IsNullOrWhiteSpace(requested)) requested = args.GetString("path");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string normalized = CucliAssets.NormalizeAssetPath(requested);
                if (normalized != null) return normalized;
            }

            return s_resolvedRoot ?? DefaultFolder;
        }

        internal static void RememberRoot(string folder)
        {
            if (!string.IsNullOrWhiteSpace(folder)) s_resolvedRoot = folder;
        }

        internal static bool IsProjectLocal(Type type)
        {
            if (type == null) return false;
            return !string.Equals(
                type.Assembly.GetName().Name, PackageAssemblyName, StringComparison.Ordinal);
        }

        internal static string SourceNameOf(Type type)
        {
            return IsProjectLocal(type) ? "project" : "package";
        }

        /// <summary>
        /// Best-effort path of the <c>.cs</c> file declaring a type, resolved from the compilation
        /// pipeline's own source list. Returns null rather than guessing: a wrong path is worse than
        /// no path.
        /// </summary>
        internal static string SourcePathOf(Type type)
        {
            if (type == null) return null;
            try
            {
                if (s_sourceFilesByAssemblyAndType == null)
                {
                    s_sourceFilesByAssemblyAndType = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (Assembly assembly in CompilationPipeline.GetAssemblies())
                    {
                        if (assembly == null || assembly.sourceFiles == null) continue;
                        foreach (string file in assembly.sourceFiles)
                        {
                            if (string.IsNullOrEmpty(file)) continue;
                            string key = assembly.name + "|" + Path.GetFileNameWithoutExtension(file);
                            if (!s_sourceFilesByAssemblyAndType.ContainsKey(key))
                            {
                                s_sourceFilesByAssemblyAndType[key] = file;
                            }
                        }
                    }
                }

                string path;
                return s_sourceFilesByAssemblyAndType.TryGetValue(
                    type.Assembly.GetName().Name + "|" + type.Name, out path)
                    ? path
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static void InvalidateSourceIndex()
        {
            s_sourceFilesByAssemblyAndType = null;
        }

        /// <summary>
        /// Files under the extension root that look like command groups but never registered. This is
        /// how a silently broken extension stops being silent.
        /// </summary>
        internal static List<string> UnregisteredFiles(string root)
        {
            var result = new List<string>();
            string absolute = CucliAssets.ToAbsolute(root);
            if (absolute == null || !Directory.Exists(absolute)) return result;

            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CucliCommandDescriptor descriptor in CucliCommandRegistry.Descriptors())
            {
                if (!string.IsNullOrEmpty(descriptor.SourcePath))
                {
                    registered.Add(Path.GetFullPath(descriptor.SourcePath));
                }
            }

            foreach (string file in Directory.GetFiles(absolute, "*.cs", SearchOption.AllDirectories))
            {
                if (registered.Contains(Path.GetFullPath(file))) continue;
                if (Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal)) continue;
                result.Add(file);
            }

            return result;
        }

        /// <summary>Sanitises a caller supplied group name into a valid lower-case identifier.</summary>
        internal static string NormalizeGroupName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var builder = new StringBuilder();
            foreach (char character in name.Trim())
            {
                if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            }

            string result = builder.ToString();
            if (result.Length == 0) return null;
            if (char.IsDigit(result[0])) result = "g" + result;
            return result;
        }

        internal static string ClassNameFor(string group)
        {
            if (string.IsNullOrEmpty(group)) return "CustomCommand";
            return char.ToUpperInvariant(group[0]) + group.Substring(1) + "Command";
        }

        /// <summary>
        /// Emits a compiling command group.
        ///
        /// The output is a working starting point rather than a stub: it declares a real action, reads
        /// a real parameter, resolves targets through the shared resolver, bounds its output through
        /// <see cref="CucliDescribe"/> and carries a schema that already matches its action list.
        ///
        /// Substitution is done with named tokens rather than nested string escaping. The generated
        /// file contains C# verbatim strings, and hand-counting quotes for those is exactly the kind of
        /// bug that wastes an afternoon.
        /// </summary>
        internal static string BuildTemplate(string group, string summary, string[] actions)
        {
            string className = ClassNameFor(group);
            string[] declared = actions == null || actions.Length == 0 ? new[] { "show" } : actions;
            string firstAction = declared[0];
            string method = Pascal(firstAction);

            var actionDeclarations = new StringBuilder();
            for (int index = 0; index < declared.Length; index++)
            {
                if (index > 0) actionDeclarations.Append(",\n");
                actionDeclarations.Append("            \"")
                    .Append(declared[index])
                    .Append("\", \"TODO: describe ")
                    .Append(declared[index])
                    .Append(".\"");
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["action"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray(declared),
                        ["default"] = firstAction
                    },
                    ["target"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name, hierarchy path, tag, layer or instance id."
                    },
                    ["maxItems"] = new JObject { ["type"] = "integer", ["default"] = 64 },
                    ["maxDepth"] = new JObject { ["type"] = "integer", ["default"] = 4 }
                }
            };

            // The schema becomes a C# verbatim string in the generated file, so every double quote is
            // doubled once, here, and never again.
            string schemaLiteral = schema.ToString(Formatting.Indented).Replace("\"", "\"\"");

            var actionList = new JArray(declared);
            string template = LoadTemplateText();

            return template
                .Replace("__CLASS__", className)
                .Replace("__GROUP__", group)
                .Replace("__SUMMARY__", EscapeForRegularString(summary ?? ("Project-local " + group + " commands.")))
                .Replace("__ACTIONS__", actionDeclarations.ToString())
                .Replace("__ACTION_NAMES__", actionList.ToString(Formatting.None))
                .Replace("__FIRST_ACTION__", firstAction)
                .Replace("__METHOD__", method)
                .Replace("__SCHEMA__", schemaLiteral);
        }

        /// <summary>
        /// Loads the shipped template. It lives next to the Core sources as a <c>.txt</c> asset so the
        /// generated shape can be reviewed and edited as prose, not as escaped string literals.
        ///
        /// The location is derived from this file's own source path rather than from a hard-coded
        /// folder, so it works whether the package is a plugin folder, an embedded package or a
        /// Package Manager cache entry.
        /// </summary>
        private static string LoadTemplateText()
        {
            try
            {
                string self = SourcePathOf(typeof(CucliExtensions));
                if (!string.IsNullOrEmpty(self))
                {
                    string directory = Path.GetDirectoryName(self);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        string candidate = Path.Combine(directory, "Templates", TemplateFileName);
                        if (File.Exists(candidate))
                        {
                            return File.ReadAllText(candidate, CucliJson.Utf8NoBom);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the embedded template.
            }

            return DefaultTemplate;
        }

        internal const string TemplateFileName = "ProjectCommandTemplate.cs.txt";

        internal static string TemplateAssetPath => TemplatePath;

        private static string EscapeForRegularString(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", string.Empty)
                .Replace("\n", "\\n");
        }

        private static string Pascal(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Run";
            var parts = value.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder();
            foreach (string part in parts)
            {
                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1) builder.Append(part.Substring(1));
            }

            return builder.Length == 0 ? "Run" : builder.ToString();
        }

        /// <summary>Used only when the template asset cannot be read.</summary>
        private const string DefaultTemplate = @"using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    internal sealed class __CLASS__ : CucliCommand
    {
        public override string Name => ""__GROUP__"";

        public override string Summary => ""__SUMMARY__"";

        public override CucliActionInfo[] Actions => Declare(
__ACTIONS__);

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        public override string[] Effects => new[] { ""editor.state.read"" };

        public override string MayReloadDomain => false;

        public override string ParametersSchemaJson => @""__SCHEMA__"";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, ""__FIRST_ACTION__"");
            switch (action)
            {
                case ""__FIRST_ACTION__"": return __METHOD__(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        private static CucliCommandResult __METHOD__(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (context.Input.Has(""target"") && error != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliDescribe.Options options = CucliDescribe.OptionsFrom(context.Input);

            return CucliCommandResult.Ok(new
            {
                group = Name,
                action,
                target = target == null ? null : CucliObjectResolver.HierarchyPath(target, true),
                detail = target == null ? null : CucliDescribe.Components(target, options, 0)
            }, ""TODO: 用一句中文结论替换这里。"");
        }
    }
}
";
    }
}
