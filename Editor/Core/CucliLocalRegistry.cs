using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Loads project-local declarative commands from <c>&lt;project&gt;/.cucli/commands/*.json</c>.
    ///
    /// The folder lives outside the package repository, so a project's private commands, presets and
    /// build gates can never be committed to the shared package by accident. Loading is tolerant:
    /// one malformed file produces a diagnostic and is skipped, and the rest still come up.
    /// </summary>
    internal static class CucliLocalRegistry
    {
        internal const string SchemaVersion = "cucli.local-command/v1";

        private static readonly HashSet<string> KnownKinds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "menu", "alias", "scenario" };

        private static readonly List<CucliCommandDiagnostic> Diagnostics =
            new List<CucliCommandDiagnostic>();

        internal static IReadOnlyList<CucliCommandDiagnostic> DiagnosticList => Diagnostics;

        internal static void Reset()
        {
            Diagnostics.Clear();
        }

        /// <summary>Every loadable local command, in file-name order. Disabled files are included.</summary>
        internal static List<CucliLocalCommand> Load()
        {
            var commands = new List<CucliLocalCommand>();
            string folder = CucliLocalPaths.CommandsFolder;
            if (!Directory.Exists(folder)) return commands;

            string[] files;
            try
            {
                // Top level only: proposed/ and disabled/ are deliberately not live.
                files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                return commands;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                string origin = CucliLocalPaths.ToProjectRelative(file);
                JObject definition;
                try
                {
                    definition = CucliJson.ParseObject(File.ReadAllText(file, CucliJson.Utf8NoBom));
                }
                catch (IOException exception)
                {
                    AddDiagnostic("LOCAL_COMMAND_UNREADABLE", null, origin, exception.Message);
                    continue;
                }
                catch (Newtonsoft.Json.JsonException exception)
                {
                    AddDiagnostic("LOCAL_COMMAND_INVALID_JSON", null, origin, exception.Message);
                    continue;
                }

                string problem = ValidateDefinition(definition);
                if (problem != null)
                {
                    AddDiagnostic("LOCAL_COMMAND_INVALID", definition["name"] == null
                        ? null
                        : definition["name"].Value<string>(), origin, problem);
                    continue;
                }

                commands.Add(new CucliLocalCommand(definition, origin));
            }

            return commands;
        }

        /// <summary>
        /// Validation shared by loading and by <c>cucli local validate</c>. Returns null when the
        /// definition is usable, or a human-readable reason why it is not.
        /// </summary>
        internal static string ValidateDefinition(JObject definition)
        {
            if (definition == null) return "文件不是 JSON 对象。";

            string schemaVersion = definition["schemaVersion"] == null
                ? null
                : definition["schemaVersion"].Value<string>();
            if (!string.IsNullOrWhiteSpace(schemaVersion)
                && !string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal))
            {
                return "schemaVersion 应为 '" + SchemaVersion + "'，实际为 '" + schemaVersion + "'。";
            }

            string name = definition["name"] == null ? null : definition["name"].Value<string>();
            if (string.IsNullOrWhiteSpace(name)) return "缺少 name。";
            name = name.Trim();

            if (!name.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_'))
            {
                return "name 只能包含字母、数字、'-' 与 '_'。";
            }

            if (name.Any(char.IsUpper))
            {
                return "name 必须是小写，否则与 CLI 的组名归一化不一致。";
            }

            string kind = definition["kind"] == null ? "alias" : definition["kind"].Value<string>();
            if (string.IsNullOrWhiteSpace(kind) || !KnownKinds.Contains(kind.Trim()))
            {
                return "kind 必须是 menu、alias 或 scenario 之一。";
            }

            switch (kind.Trim().ToLowerInvariant())
            {
                case "menu":
                    if (string.IsNullOrWhiteSpace(Text(definition, "menuPath")))
                    {
                        return "kind=menu 需要 menuPath。";
                    }

                    break;

                case "alias":
                {
                    var target = definition["target"] as JObject;
                    if (target == null) return "kind=alias 需要 target 对象。";
                    if (string.IsNullOrWhiteSpace(Text(target, "command")))
                    {
                        return "kind=alias 的 target.command 不能为空。";
                    }

                    break;
                }

                case "scenario":
                {
                    var steps = definition["steps"] as JArray;
                    if (steps == null || steps.Count == 0) return "kind=scenario 需要至少一个 steps 项。";
                    for (int index = 0; index < steps.Count; index++)
                    {
                        var step = steps[index] as JObject;
                        if (step == null) return "steps[" + index + "] 不是对象。";
                        if (string.IsNullOrWhiteSpace(Text(step, "command")))
                        {
                            return "steps[" + index + "] 缺少 command。";
                        }
                    }

                    break;
                }
            }

            return null;
        }

        private static string Text(JObject source, string key)
        {
            if (source == null) return null;
            JToken token = source[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static void AddDiagnostic(string code, string commandName, string origin, string message)
        {
            Diagnostics.Add(new CucliCommandDiagnostic
            {
                Code = code,
                TypeName = origin,
                CommandName = commandName,
                Message = message
            });
        }
    }

    /// <summary>
    /// Discovered by <see cref="CucliCommandRegistry"/> like any other provider. Exists as its own
    /// public type purely so reflection can find it.
    /// </summary>
    public sealed class CucliLocalCommandProvider : ICucliCommandProvider
    {
        public IEnumerable<ICucliCommand> CreateCommands()
        {
            return CucliLocalRegistry.Load().Cast<ICucliCommand>().ToArray();
        }
    }
}
