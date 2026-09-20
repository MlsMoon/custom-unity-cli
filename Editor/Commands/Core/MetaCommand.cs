using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli meta ...</c> - self-description and self-maintenance of the tool itself.
    ///
    /// The important action is <c>export-registry</c>: it regenerates the shipped static
    /// <c>commands/registry.json</c> from the commands actually compiled in this Editor, so the CLI
    /// can list and validate commands while Unity is closed without anybody hand-maintaining a
    /// second copy of the command surface.
    /// </summary>
    internal sealed class MetaCommand : CucliCommand
    {
        public override string Name => "meta";

        public override string Summary =>
            "Inspect and refresh the command surface itself: export the static registry, scaffold project-local commands, report the worker environment.";

        // 'doctor' is first because it is the default action: running bare 'cucli meta' should report
        // the environment, and the contract requires the declared order to lead with the default.
        public override CucliActionInfo[] Actions => Declare(
            "doctor", "Worker-side environment report: command diagnostics, optional packages, versions.",
            "export-registry", "Regenerate the shipped commands/registry.json from the compiled package commands only.",
            "catalog", "Dump the runtime command catalog exactly as published to Library/CustomUnityCli.",
            "schema", "Return the parameter JSON schema of one command group.",
            "extensions", "Report the project-local extension area and the commands it contributes.",
            "new-command", "Scaffold a new project-local command group in the extension area.",
            "verify", "Self-check: validate the extension area and every command's action list against its JSON schema.",
            "environment", "Report Unity, platform, package versions and the optional-package probe results.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[] { "filesystem.write", "editor.state.read" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""export-registry"", ""catalog"", ""schema"", ""doctor"", ""extensions"", ""new-command"", ""verify"", ""environment""],
      ""default"": ""doctor""
    },
    ""path"": {
      ""type"": ""string"",
      ""description"": ""action=export-registry: destination for registry.json. Relative paths resolve against the project root.""
    },
    ""group"": { ""type"": ""string"", ""description"": ""action=schema / new-command: group name, e.g. object."" },
    ""summary"": { ""type"": ""string"", ""description"": ""action=new-command: one-line description of the group."" },
    ""actionNames"": {
      ""type"": ""array"", ""items"": { ""type"": ""string"" },
      ""description"": ""action=new-command: sub-action names to scaffold. Defaults to a single 'run' action.""
    },
    ""actionName"": {
      ""type"": ""string"",
      ""description"": ""action=new-command: a single sub-action to scaffold, as an alternative to actionNames. Note that the bare name 'action' is reserved by the CLI as the group action selector.""
    },
    ""namespace"": { ""type"": ""string"", ""description"": ""action=new-command: C# namespace for the generated file."" },
    ""overwrite"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=new-command: overwrite an existing file."" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "doctor");
            switch (action)
            {
                case "export-registry": return ExportRegistry(context);
                case "catalog": return Catalog();
                case "schema": return Schema(context);
                case "doctor": return Doctor(context);
                case "extensions": return Extensions(context);
                case "new-command": return NewCommand(context);
                case "verify": return Verify(context);
                case "environment": return CucliCommandResult.Ok(CucliOptional.DescribeEnvironment());
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        private static CucliCommandResult Catalog()
        {
            CucliCommandCatalog catalog = CucliCommandRegistry.BuildCatalog();
            return CucliCommandResult.Ok(new
            {
                catalogHash = catalog.CatalogHash,
                workerVersion = catalog.WorkerVersion,
                protocolVersion = catalog.ProtocolVersion,
                generatedAtUtc = catalog.GeneratedAtUtc,
                projectPath = catalog.ProjectPath,
                unityVersion = catalog.UnityVersion,
                commandCount = catalog.Commands.Length,
                actionCount = catalog.Commands.Sum(command => command.Actions == null ? 0 : command.Actions.Length),
                fullCatalogHash = catalog.FullCatalogHash,
                packageCommandCount = catalog.PackageCommandCount,
                projectCommandCount = catalog.ProjectCommandCount,
                commands = catalog.Commands,
                diagnostics = catalog.Diagnostics
            });
        }

        /// <summary>
        /// Reports the project-local extension area: where it is, whether it exists, and which
        /// commands it contributes. This is the answer to "what has this project grown that the
        /// package does not ship?".
        /// </summary>
        private static CucliCommandResult Extensions(CucliCommandContext context)
        {
            var projectCommands = new JArray();
            foreach (CucliCommandDescriptor descriptor in CucliCommandRegistry.Descriptors())
            {
                if (descriptor.Source != CucliCommandRegistry.SourceProject) continue;
                projectCommands.Add(new JObject
                {
                    ["name"] = descriptor.Name,
                    ["summary"] = descriptor.Summary,
                    ["actionCount"] = descriptor.Actions == null ? 0 : descriptor.Actions.Length,
                    ["requiredMode"] = descriptor.RequiredMode,
                    ["mutatesProject"] = descriptor.MutatesProject,
                    ["optionalPackage"] = descriptor.OptionalPackage == null
                        ? JValue.CreateNull()
                        : new JValue(descriptor.OptionalPackage)
                });
            }

            JObject manifest = CucliExtensionPaths.TryReadManifest();
            string root = CucliExtensionPaths.Root;

            if (!CucliExtensionPaths.Exists)
            {
                context.Warn(
                    "The extension area does not exist yet. Run 'cucli meta new-command --group <name>' "
                    + "to create it, or create the folder manually.");
            }

            if (projectCommands.Count > 0 && !CucliExtensionPaths.Exists)
            {
                context.Warn(
                    projectCommands.Count + " project command(s) are registered but the conventional "
                    + "extension area does not exist, so they probably live elsewhere in the project. "
                    + "That is fine - provenance is decided by the declaring assembly, not by location.");
            }

            return CucliCommandResult.Ok(new
            {
                root,
                relativeRoot = CucliExtensionPaths.RelativeRoot,
                exists = CucliExtensionPaths.Exists,
                commandsFolder = CucliExtensionPaths.CommandsFolder,
                notesFolder = CucliExtensionPaths.NotesFolder,
                manifestPath = CucliExtensionPaths.ManifestPath,
                manifestPresent = manifest != null,
                environmentVariable = CucliExtensionPaths.EnvironmentVariable,
                projectCommandCount = projectCommands.Count,
                projectCommands,
                packageCommandCount = CucliCommandRegistry.PackageCommandCount,
                excludedFromPublishedRegistry = projectCommands.Count,
                note =
                    "Project commands are discovered from any assembly that references CustomUnityCli.Editor. "
                    + "They are never written into the package's commands/registry.json."
            });
        }

        /// <summary>
        /// Scaffolds a project-local command group. This is the affordance that makes the tool
        /// self-extending: an agent can add a project-specific group in one call, and the generated
        /// file compiles as-is.
        /// </summary>
        private static CucliCommandResult NewCommand(CucliCommandContext context)
        {
            string group = context.Input.GetString("group");
            if (string.IsNullOrWhiteSpace(group))
            {
                return CucliCommandResult.Missing(
                    "group", "Example: --group buildchecks --summary \"本项目的构建前检查\" --action check");
            }

            group = group.Trim().ToLowerInvariant();
            if (!IsValidIdentifier(group))
            {
                return CucliCommandResult.Invalid(
                    "group",
                    "must start with a letter and contain only lower-case letters, digits, '-' or '_' "
                    + "(it becomes a CLI word and a C# identifier).");
            }

            string className = ToPascalCase(group) + "Command";
            if (CucliCommandRegistry.TryGet(group, out _))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandDuplicated,
                    "A command group named '" + group + "' is already registered. Pick another name, or edit "
                    + "the existing type - shadowing a package group is allowed but must be deliberate.",
                    new { group });
            }

            // 'actionNames' (plural) is the documented key. The singular fallback is 'actionName', NOT
            // 'action': 'action' is reserved by the CLI as the group's action selector, so a parameter
            // with that name could never be passed - the CLI would consume it and silently drop the
            // user's value.
            string[] actions = context.Input.GetStringList("actionNames");
            if (actions.Length == 0)
            {
                string single = context.Input.GetString("actionName")
                    ?? context.Input.GetString("action_name");
                actions = string.IsNullOrWhiteSpace(single) ? new[] { "run" } : new[] { single.Trim() };
            }

            for (int index = 0; index < actions.Length; index++)
            {
                actions[index] = actions[index].Trim().ToLowerInvariant();
                if (!IsValidIdentifier(actions[index]))
                {
                    return CucliCommandResult.Invalid(
                        "actionNames",
                        "'" + actions[index] + "' is not a valid action name.");
                }
            }

            string ns = context.Input.GetString("namespace");
            if (string.IsNullOrWhiteSpace(ns)) ns = "CustomUnityCli.Extensions";

            bool overwrite = context.Input.GetBool("overwrite", false);

            // A root outside Assets/ or Packages/ would produce files Unity never compiles, which is
            // the most confusing possible failure: the scaffold reports success and nothing registers.
            if (!CucliExtensionPaths.IsCompilableRoot(out string rootProblem))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, rootProblem,
                    new { extensionRoot = CucliExtensionPaths.Root });
            }

            bool scaffolded;
            CucliExtensionPaths.EnsureScaffolded(out scaffolded);

            string target = Path.Combine(CucliExtensionPaths.CommandsFolder, className + ".cs");
            if (File.Exists(target) && !overwrite)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "The file already exists. Pass overwrite: true to replace it, or choose another group name.",
                    new { path = target });
            }

            string summary = context.Input.GetString("summary");
            if (string.IsNullOrWhiteSpace(summary)) summary = "本项目专用的 " + group + " 命令组。";

            string source = BuildCommandTemplate(ns, group, className, summary, actions);

            try
            {
                CucliPaths.WriteAtomic(target, source);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Could not write " + target + ": " + exception.GetBaseException().Message);
            }

            context.AddArtifact(target);
            context.Warn(
                "Unity must recompile before the new group is usable. Run 'cucli refresh compile --wait', "
                + "then 'cucli " + group + " " + actions[0] + "'.");

            return CucliCommandResult.Ok(new
            {
                path = target,
                relativePath = CucliExtensionPaths.ToRelative(target),
                group,
                className,
                @namespace = ns,
                actions,
                scaffoldedExtensionArea = scaffolded,
                extensionRoot = CucliExtensionPaths.Root,
                overwritten = overwrite,
                requiresRecompile = true
            }, "已生成项目本地命令组 " + group + "，重编译后即可用。");
        }

        private static bool IsValidIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            char first = value[0];
            if (!char.IsLetter(first)) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_') continue;
                return false;
            }

            return true;
        }

        private static string ToPascalCase(string value)
        {
            var builder = new System.Text.StringBuilder();
            bool upper = true;
            foreach (char character in value)
            {
                if (character == '-' || character == '_')
                {
                    upper = true;
                    continue;
                }

                builder.Append(upper ? char.ToUpperInvariant(character) : character);
                upper = false;
            }

            return builder.Length == 0 ? "Project" : builder.ToString();
        }

        /// <summary>
        /// The scaffold deliberately demonstrates the helpers the contract asks for - paging,
        /// description bounds, confirm-to-mutate - so that a generated file is a working example
        /// rather than a bare skeleton.
        /// </summary>
        private static string BuildCommandTemplate(
            string ns, string group, string className, string summary, string[] actions)
        {
            var actionLines = new System.Text.StringBuilder();
            for (int index = 0; index < actions.Length; index++)
            {
                actionLines.Append("            \"").Append(actions[index]).Append("\", \"TODO: 说明 ")
                    .Append(actions[index]).Append(" 做什么。\"");
                actionLines.Append(index == actions.Length - 1 ? ");" : ",").Append('\n');
            }

            var enumValues = string.Join(", ", actions.Select(action => "\"" + action + "\""));
            string defaultAction = actions[0];
            string firstMethod = ToPascalCase(actions[0]);

            var cases = new System.Text.StringBuilder();
            for (int index = 0; index < actions.Length; index++)
            {
                cases.Append("                case \"").Append(actions[index]).Append("\": return ")
                    .Append(index == 0 ? firstMethod : ToPascalCase(actions[index]))
                    .Append("(context);\n");
            }

            var stubs = new System.Text.StringBuilder();
            for (int index = 0; index < actions.Length; index++)
            {
                string method = index == 0 ? firstMethod : ToPascalCase(actions[index]);
                stubs.Append(@"
        private static CucliCommandResult ").Append(method).Append(@"(CucliCommandContext context)
        {
            // TODO: 实现 ").Append(group).Append(' ').Append(actions[index]).Append(@"

            // 需要定位场景对象时用统一寻址，不要自己写名字查找：
            //   string error;
            //   GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            //   if (error != null) return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);

            // 需要读参数：
            //   string name = context.Input.GetString(""name"") ?? context.Input.GetString(""name"");

            // 需要写组件或 SO 字段时，优先 SerializedObject（自带 Undo / 预制体覆盖 / 标脏）：
            //   string error; bool handled;
            //   if (!CucliSerialized.TrySet(target, ""fieldName"", token, ""cucli set"", out error, out handled))
            //   {
            //       if (handled) return CucliCommandResult.Invalid(""value"", error);
            //       if (!CucliReflection.TrySetPath(target, ""fieldName"", token, out error))
            //           return CucliCommandResult.Invalid(""value"", error);
            //       EditorUtility.SetDirty(target);
            //   }

            // 需要返回对象内容时用 CucliDescribe 限深限量，绝不直接序列化 Mesh / TMP / ParticleSystem：
            //   CucliDescribe.Options options = CucliDescribe.OptionsFrom(context.Input);
            //   JToken described = CucliDescribe.Value(target, options, 0);

            // 需要返回多条结果时分页：
            //   CucliPage<GameObject> page = CucliPaging.Slice(items, context.Input);

            return CucliCommandResult.Ok(new
            {
                group = """).Append(group).Append(@""",
                action = """).Append(actions[index]).Append(@""",
                implemented = false
            }, ""TODO: ").Append(group).Append(' ').Append(actions[index]).Append(@" 尚未实现。"");
        }
");
            }

            return @"using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace " + ns + @"
{
    /// <summary>
    /// " + summary + @"
    ///
    /// This file lives in the project-local extension area, NOT in the custom-unity-cli package.
    /// It is discovered automatically by CustomUnityCli.Editor's registry, contributes only to this
    /// project's command surface, and is never written into the package's commands/registry.json.
    /// Keep it in your own version control; do not upstream it into the package repository.
    /// </summary>
    internal sealed class " + className + @" : CustomUnityCli.Editor.CucliCommand
    {
        public override string Name => """ + group + @""";

        public override string Summary =>
            """ + summary.Replace("\"", "'") + @""";

        public override CustomUnityCli.Editor.CucliActionInfo[] Actions => Declare(
" + actionLines + @"        public override bool RequiresEditor => true;

        // Narrow this when the group only reads: CucliMode.Edit avoids running in Play Mode.
        public override CustomUnityCli.Editor.CucliMode RequiredMode =>
            CustomUnityCli.Editor.CucliMode.Any;

        // Set true when the group writes assets, scenes or project settings.
        public override bool MutatesProject => false;

        public override string[] Effects => new[] { ""project.assets.read"" };

        public override string ParametersSchemaJson => @""{
  """"type"""": """"object"""",
  """"additionalProperties"""": false,
  """"properties"""": {
    """"action"""": { """"type"""": """"string"""", """"enum"""": [" + enumValues.Replace("\"", "\"\"") + @"""], """"default"""": """"" + defaultAction + @""""" },
    """"maxItems"""": { """"type"""": """"integer"""", """"default"""": 64 },
    """"maxDepth"""": { """"type"""": """"integer"""", """"default"""": 4 }
  }
}"";

        public override CustomUnityCli.Editor.CucliCommandResult Execute(
            CustomUnityCli.Editor.CucliCommandContext context)
        {
            string action = ResolveAction(context, """ + defaultAction + @""");
            switch (action)
            {
" + cases + @"                default:
                    return CustomUnityCli.Editor.CucliCommandResult.Unsupported(action, ActionNames());
            }
        }
" + stubs + @"    }
}
";
        }


        private static CucliCommandResult Schema(CucliCommandContext context)
        {
            string group = context.Input.GetString("group");
            if (string.IsNullOrWhiteSpace(group))
            {
                return CucliCommandResult.Missing("group", "Example: --group object");
            }

            ICucliCommand command;
            if (!CucliCommandRegistry.TryGet(group.Trim(), out command))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandNotFound,
                    "No command group named '" + group + "' is registered. Known groups: "
                    + string.Join(", ", CucliCommandRegistry.Names) + ".");
            }

            JToken schema;
            try
            {
                schema = JToken.Parse(command.ParametersSchemaJson);
            }
            catch (Newtonsoft.Json.JsonException exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "The schema declared by '" + group + "' is not valid JSON: " + exception.Message);
            }

            return CucliCommandResult.Ok(new
            {
                group = command.Name,
                summary = command.Summary,
                requiresEditor = command.RequiresEditor,
                requiredMode = CucliCommandRegistry.Describe(command).RequiredMode,
                mutatesProject = command.MutatesProject,
                mayReloadDomain = command.MayReloadDomain,
                optionalPackage = command.OptionalPackage,
                effects = command.Effects,
                actions = command.Actions.Select(item => new { name = item.Name, summary = item.Summary }).ToArray(),
                schema
            });
        }

        private static CucliCommandResult Doctor(CucliCommandContext context)
        {
            CucliCommandDiagnostic[] diagnostics = CucliCommandRegistry.DiagnosticList.ToArray();
            if (diagnostics.Length > 0)
            {
                context.Warn(diagnostics.Length + " command registration diagnostic(s) were reported. "
                    + "Run 'cucli meta catalog' for details.");
            }

            var byMode = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            int actionCount = 0;
            int mutating = 0;
            int reloadCapable = 0;

            foreach (ICucliCommand command in CucliCommandRegistry.All)
            {
                string mode = CucliCommandRegistry.Describe(command).RequiredMode;
                int current;
                byMode.TryGetValue(mode, out current);
                byMode[mode] = current + 1;

                if (command.Actions != null) actionCount += command.Actions.Length;
                if (command.MutatesProject) mutating++;
                if (command.MayReloadDomain) reloadCapable++;
            }

            return CucliCommandResult.Ok(new
            {
                workerVersion = CucliPaths.WorkerVersion,
                protocolVersion = CucliPaths.ProtocolVersion,
                unityVersion = Application.unityVersion,
                projectPath = CucliPaths.ProjectRoot,
                runtimeRoot = CucliPaths.Root,
                sessionId = CucliWorkerBootstrap.CurrentSessionId,
                commandCount = CucliCommandRegistry.Count,
                actionCount,
                mutatingCommandCount = mutating,
                reloadCapableCommandCount = reloadCapable,
                commandsByMode = byMode,
                catalogHash = CucliCommandRegistry.CatalogHash,
                fullCatalogHash = CucliCommandRegistry.FullCatalogHash,
                packageCommandCount = CucliCommandRegistry.PackageCommandCount,
                projectCommandCount = CucliCommandRegistry.ProjectCommandCount,
                extensionRoot = CucliExtensionPaths.Root,
                extensionExists = CucliExtensionPaths.Exists,
                completedJobs = CucliJobProcessor.CompletedCount,
                consoleSessionId = CucliConsoleCapture.SessionId,
                pendingConsoleEntries = CucliConsoleCapture.PendingCount,
                diagnostics,
                optionalPackages = CucliOptional.DescribeEnvironment()
            });
        }

        private static CucliCommandResult ExportRegistry(CucliCommandContext context)
        {
            // The destination defaults to the package's own commands/registry.json, resolved from the
            // package identity rather than assumed, so 'cucli commands export' needs no arguments and
            // a direct worker call behaves identically.
            string requested = context.Input.GetString("path");
            if (string.IsNullOrWhiteSpace(requested))
            {
                string packageRoot = CucliPackage.Root;
                if (string.IsNullOrEmpty(packageRoot))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "The package root could not be resolved (" + CucliPackage.RootResolution + "), so the "
                        + "registry destination is unknown. Pass an explicit 'path'.");
                }

                requested = Path.Combine(packageRoot, "commands", "registry.json");
            }

            string absolute = Path.IsPathRooted(requested)
                ? requested
                : Path.Combine(CucliPaths.ProjectRoot, requested);

            JObject root = null;
            if (File.Exists(absolute))
            {
                try
                {
                    root = CucliJson.ParseObject(File.ReadAllText(absolute, CucliJson.Utf8NoBom));
                }
                catch (Newtonsoft.Json.JsonException exception)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter,
                        "The existing registry at " + absolute + " is not valid JSON, refusing to overwrite it: "
                        + exception.Message);
                }
            }

            root = root ?? new JObject { ["schemaVersion"] = "cucli.command-registry/v1" };

            // Aliases, offline groups and the description are hand-maintained and must survive.
            var aliases = root["aliases"] as JObject;
            var offline = root["offline"] as JArray;
            var controlActions = root["controlActions"] as JArray;
            string description = root["description"] == null ? null : root["description"].Value<string>();

            // Only package commands are published.
            //
            // This is the single most important line in the file: the registry written here lives
            // inside the package repository, so including a project-local command would push
            // project-specific code upstream as a side effect of running a refresh command.
            var groups = new JArray();
            var excluded = new JArray();
            var descriptors = CucliCommandRegistry.Descriptors();
            foreach (CucliCommandDescriptor descriptor in descriptors)
            {
                if (descriptor.Source != CucliCommandRegistry.SourcePackage)
                {
                    excluded.Add(new JObject
                    {
                        ["name"] = descriptor.Name,
                        ["source"] = descriptor.Source,
                        ["summary"] = descriptor.Summary
                    });
                    continue;
                }
                var actions = new JArray();
                foreach (CucliActionDescriptor action in descriptor.Actions ?? Array.Empty<CucliActionDescriptor>())
                {
                    actions.Add(new JObject { ["name"] = action.Name, ["summary"] = action.Summary });
                }

                groups.Add(new JObject
                {
                    ["name"] = descriptor.Name,
                    ["summary"] = descriptor.Summary,
                    ["requiresEditor"] = descriptor.RequiresEditor,
                    ["requiredMode"] = descriptor.RequiredMode,
                    ["mutatesProject"] = descriptor.MutatesProject,
                    ["mayReloadDomain"] = descriptor.MayReloadDomain,
                    ["optionalPackage"] = descriptor.OptionalPackage == null
                        ? JValue.CreateNull()
                        : new JValue(descriptor.OptionalPackage),
                    ["effects"] = new JArray(descriptor.Effects ?? Array.Empty<string>()),
                    ["surface"] = string.IsNullOrWhiteSpace(descriptor.Surface)
                        ? "core"
                        : descriptor.Surface,
                    ["actions"] = actions,
                    ["parametersSchema"] = SafeSchema(descriptor.ParametersSchemaJson)
                });
            }

            root["name"] = "com.igp.custom-unity-cli";
            root["version"] = CucliPaths.WorkerVersion;
            root["protocolVersion"] = CucliPaths.ProtocolVersion;
            root["generatedAtUtc"] = DateTime.UtcNow.ToString("O");
            root["catalogHash"] = CucliCommandRegistry.CatalogHash;
            root["unityVersion"] = Application.unityVersion;
            if (description != null) root["description"] = description;
            root["aliases"] = aliases ?? new JObject();
            root["controlActions"] = controlActions ?? new JArray("editor/stop", "editor/pause");
            root["offline"] = offline ?? new JArray();
            root["groups"] = groups;

            try
            {
                CucliPaths.WriteAtomic(absolute, root.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Could not write " + absolute + ": " + exception.GetBaseException().Message);
            }

            context.AddArtifact(absolute);

            if (excluded.Count > 0)
            {
                context.Warn(
                    excluded.Count + " project-local command group(s) were deliberately excluded from the "
                    + "published registry: " + string.Join(", ",
                        excluded.Select(item => item["name"] == null ? "?" : item["name"].Value<string>()))
                    + ". Run 'cucli meta extensions' to list them.");
            }

            int actionCount = groups
                .Select(item => item["actions"] as JArray)
                .Sum(actions => actions == null ? 0 : actions.Count);

            string localRegistryPath = WriteLocalRegistry(descriptors, excluded, context);

            return CucliCommandResult.Ok(new
            {
                path = absolute,
                groupCount = groups.Count,
                actionCount,
                catalogHash = CucliCommandRegistry.CatalogHash,
                fullCatalogHash = CucliCommandRegistry.FullCatalogHash,
                excludedProjectCommands = excluded,
                excludedProjectCommandCount = excluded.Count,
                localRegistryPath = localRegistryPath,
                localRegistryRelativePath = localRegistryPath == null
                    ? null
                    : CucliExtensionPaths.RelativeRoot + "/" + CucliExtensionPaths.LocalRegistryFileName,
                preservedAliases = aliases == null ? 0 : aliases.Count,
                preservedOfflineGroups = offline == null ? 0 : offline.Count
            }, "Regenerated the published registry from the package command surface only.");
        }

        /// <summary>
        /// Writes the project-local command declaration to the extension area.
        ///
        /// The published registry must not contain project commands, but the CLI still needs to list
        /// and validate them while Unity is closed. Splitting the two files is what satisfies both:
        /// the package file stays publishable, and the project file lives outside the package so it
        /// is invisible to the package repository.
        /// </summary>
        private static string WriteLocalRegistry(
            CucliCommandDescriptor[] descriptors,
            JArray excluded,
            CucliCommandContext context)
        {
            if (excluded == null || excluded.Count == 0)
            {
                // Nothing project-local is compiled in any more. A leftover declaration would keep
                // advertising groups that no longer exist, and the CLI merges this file over the
                // package registry, so a stale entry would make 'commands list' lie. Remove it rather
                // than leave it behind - but only when it exists, and never touch a file the project
                // did not generate (we can tell from its scope marker).
                if (File.Exists(CucliExtensionPaths.LocalRegistryPath))
                {
                    try
                    {
                        JObject stale = CucliJson.ParseObject(
                            File.ReadAllText(CucliExtensionPaths.LocalRegistryPath, CucliJson.Utf8NoBom));
                        string scope = stale["scope"] == null ? null : stale["scope"].Value<string>();
                        if (string.Equals(scope, "project-local", StringComparison.Ordinal))
                        {
                            File.Delete(CucliExtensionPaths.LocalRegistryPath);
                            context.Warn("Removed the now-empty project-local command registry: "
                                + CucliExtensionPaths.RelativeRoot + "/"
                                + CucliExtensionPaths.LocalRegistryFileName);
                            return null;
                        }
                    }
                    catch (Exception)
                    {
                        // A file we cannot read or delete is left alone; it is project state.
                    }
                }

                return File.Exists(CucliExtensionPaths.LocalRegistryPath)
                    ? CucliExtensionPaths.LocalRegistryPath
                    : null;
            }

            var groups = new JArray();
            foreach (CucliCommandDescriptor descriptor in descriptors)
            {
                if (descriptor.Source == CucliCommandRegistry.SourcePackage) continue;

                var actions = new JArray();
                foreach (CucliActionDescriptor action in descriptor.Actions ?? Array.Empty<CucliActionDescriptor>())
                {
                    actions.Add(new JObject { ["name"] = action.Name, ["summary"] = action.Summary });
                }

                groups.Add(new JObject
                {
                    ["name"] = descriptor.Name,
                    ["summary"] = descriptor.Summary,
                    ["source"] = descriptor.Source,
                    ["requiresEditor"] = descriptor.RequiresEditor,
                    ["requiredMode"] = descriptor.RequiredMode,
                    ["mutatesProject"] = descriptor.MutatesProject,
                    ["mayReloadDomain"] = descriptor.MayReloadDomain,
                    ["optionalPackage"] = descriptor.OptionalPackage == null
                        ? JValue.CreateNull()
                        : new JValue(descriptor.OptionalPackage),
                    ["effects"] = new JArray(descriptor.Effects ?? Array.Empty<string>()),
                    ["surface"] = string.IsNullOrWhiteSpace(descriptor.Surface)
                        ? "core"
                        : descriptor.Surface,
                    ["actions"] = actions,
                    ["parametersSchema"] = SafeSchema(descriptor.ParametersSchemaJson)
                });
            }

            // Aliases and control actions are hand-maintained by the project, so they survive.
            JObject existing = null;
            if (File.Exists(CucliExtensionPaths.LocalRegistryPath))
            {
                try
                {
                    existing = CucliJson.ParseObject(
                        File.ReadAllText(CucliExtensionPaths.LocalRegistryPath, CucliJson.Utf8NoBom));
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    existing = null;
                }
            }

            var root = new JObject
            {
                ["schemaVersion"] = "cucli.command-registry/v1",
                ["scope"] = "project-local",
                ["description"] =
                    "Command groups compiled from project-local extension code. This file lives in the "
                    + "extension area, outside the custom-unity-cli package, and is never published. It "
                    + "exists so 'cucli commands list' can describe project commands while Unity is closed.",
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["catalogHash"] = CucliCommandRegistry.CatalogHash,
                ["fullCatalogHash"] = CucliCommandRegistry.FullCatalogHash,
                ["unityVersion"] = Application.unityVersion,
                ["aliases"] = (existing == null ? null : existing["aliases"] as JObject) ?? new JObject(),
                ["controlActions"] = (existing == null ? null : existing["controlActions"] as JArray)
                    ?? new JArray(),
                ["offline"] = (existing == null ? null : existing["offline"] as JArray) ?? new JArray(),
                ["groups"] = groups
            };

            try
            {
                CucliPaths.WriteAtomic(
                    CucliExtensionPaths.LocalRegistryPath,
                    root.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
            }
            catch (Exception exception)
            {
                context.Warn("Could not write the project-local registry: "
                    + exception.GetBaseException().Message);
                return null;
            }

            context.AddArtifact(CucliExtensionPaths.LocalRegistryPath);
            return CucliExtensionPaths.LocalRegistryPath;
        }

        /// <summary>
        /// The self-check that closes the self-iteration loop.
        ///
        /// An agent that generates a command group, recompiles and then calls this gets a precise
        /// verdict on the two things that actually go wrong: the extension area is somewhere Unity
        /// will not compile (or, worse, inside the package tree where it would leak upstream), and a
        /// command's declared <c>Actions</c> disagree with its own JSON schema, which makes
        /// <c>cucli commands list</c> advertise actions the dispatcher will reject.
        /// </summary>
        private static CucliCommandResult Verify(CucliCommandContext context)
        {
            var problems = new JArray();
            var notes = new JArray();

            bool compilable = CucliExtensionPaths.IsCompilableRoot(out string rootProblem);
            if (!compilable) problems.Add(rootProblem);

            string[] sources = CucliExtensionPaths.FindSources();
            var misplaced = new JArray();
            foreach (string source in sources)
            {
                if (CucliExtensionPaths.IsInsidePackage(source)) misplaced.Add(source);
            }

            if (misplaced.Count > 0)
            {
                problems.Add(misplaced.Count + " extension source file(s) live inside the package tree, "
                    + "which is how project-specific code would end up in the public repository.");
            }

            int projectCommandCount = 0;
            foreach (ICucliCommand command in CucliCommandRegistry.All)
            {
                if (!CucliCommandRegistry.IsProjectCommand(command.Name)) continue;
                projectCommandCount++;
            }

            if (sources.Length > 0 && projectCommandCount == 0)
            {
                notes.Add(sources.Length + " source file(s) exist under the extension area but no project "
                    + "command is registered yet. If the file was just written, recompile with "
                    + "'cucli refresh compile --wait'.");
            }

            if (projectCommandCount > 0 && sources.Length == 0)
            {
                notes.Add(projectCommandCount + " project command(s) are registered without any source under "
                    + "the conventional extension area. Provenance comes from the declaring assembly, not "
                    + "from location, so this is allowed - just make sure the code is where you expect.");
            }

            // Action/schema agreement, checked for every command so a package regression is caught too.
            foreach (ICucliCommand command in CucliCommandRegistry.All)
            {
                ValidateCommandSchema(command, problems);
            }

            foreach (CucliCommandDiagnostic diagnostic in CucliCommandRegistry.DiagnosticList)
            {
                problems.Add("registration diagnostic " + diagnostic.Code + " on "
                    + (diagnostic.TypeName ?? diagnostic.CommandName ?? "?") + ": " + diagnostic.Message);
            }

            bool ok = problems.Count == 0;
            return CucliCommandResult.Ok(new
            {
                ok,
                extensionRoot = CucliExtensionPaths.Root,
                relativeRoot = CucliExtensionPaths.RelativeRoot,
                compilable,
                extensionSourceCount = sources.Length,
                misplacedSources = misplaced,
                projectCommandCount,
                packageCommandCount = CucliCommandRegistry.PackageCommandCount,
                catalogHash = CucliCommandRegistry.CatalogHash,
                fullCatalogHash = CucliCommandRegistry.FullCatalogHash,
                problemCount = problems.Count,
                problems,
                notes
            }, ok
                ? "扩展区与命令声明自检通过。"
                : "自检发现 " + problems.Count + " 个问题。");
        }

        /// <summary>
        /// Confirms a command's declared <c>Actions</c> and its own schema agree, because a mismatch
        /// makes the catalog lie: the CLI would accept an action the dispatcher then rejects.
        /// </summary>
        private static void ValidateCommandSchema(ICucliCommand command, JArray problems)
        {
            string label = command.Name
                + (CucliCommandRegistry.IsProjectCommand(command.Name) ? " (project)" : " (package)");

            CucliActionInfo[] actions = command.Actions ?? Array.Empty<CucliActionInfo>();
            var declared = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (CucliActionInfo action in actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Name))
                {
                    problems.Add(label + ": an action has an empty name.");
                    continue;
                }

                if (!declared.Add(action.Name))
                {
                    problems.Add(label + ": action '" + action.Name + "' is declared twice.");
                }
            }

            JObject schema;
            try
            {
                schema = JObject.Parse(command.ParametersSchemaJson ?? "{}");
            }
            catch (Newtonsoft.Json.JsonException exception)
            {
                problems.Add(label + ": ParametersSchemaJson is not valid JSON: " + exception.Message);
                return;
            }

            JObject properties = schema["properties"] as JObject;
            JObject actionProperty = properties == null ? null : properties["action"] as JObject;
            JArray enumValues = actionProperty == null ? null : actionProperty["enum"] as JArray;

            if (actions.Length == 0)
            {
                if (enumValues != null && enumValues.Count > 0)
                {
                    problems.Add(label + ": the schema declares an action enum but the command declares no actions.");
                }

                return;
            }

            if (enumValues == null)
            {
                problems.Add(label + ": declares " + actions.Length
                    + " action(s) but its schema has no properties.action.enum, so callers cannot discover them.");
                return;
            }

            var advertised = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (JToken value in enumValues)
            {
                string name = value == null ? null : value.Value<string>();
                if (!string.IsNullOrWhiteSpace(name)) advertised.Add(name);
            }

            foreach (string name in declared)
            {
                if (!advertised.Contains(name))
                {
                    problems.Add(label + ": action '" + name + "' is missing from the schema enum.");
                }
            }

            foreach (string name in advertised)
            {
                if (!declared.Contains(name))
                {
                    problems.Add(label + ": the schema advertises action '" + name + "' but no such action is declared.");
                }
            }
        }

        private static JToken SafeSchema(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try
            {
                return JToken.Parse(json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return new JObject();
            }
        }
    }
}
