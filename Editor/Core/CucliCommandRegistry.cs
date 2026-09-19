using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Discovers every <see cref="ICucliCommand"/> in the compiled Editor assemblies and publishes
    /// the resulting catalog to <c>Library/CustomUnityCli/worker/commands.json</c>.
    ///
    /// Two things make this the extension point as well as the registry:
    ///
    /// <list type="bullet">
    /// <item>Discovery is by <see cref="TypeCache"/>, so a command added anywhere in the project -
    /// no registration call, no manifest edit - appears after the next domain reload.</item>
    /// <item>Each command carries its <em>provenance</em>: <c>package</c> for commands compiled into
    /// this package, <c>project</c> for everything else. That tag is what keeps project-specific
    /// commands out of the published registry.</item>
    /// </list>
    ///
    /// Discovery problems are never swallowed: a missing constructor, an empty name or a duplicate
    /// name lands in <c>diagnostics[]</c>, which <c>cucli doctor</c> and the CLI both surface.
    /// </summary>
    internal static class CucliCommandRegistry
    {
        internal const string SourcePackage = "package";
        internal const string SourceProject = "project";

        private sealed class Registration
        {
            internal ICucliCommand Command;
            internal bool FromPackage;
            /// <summary>Kept so the descriptor can report where a project command came from.</summary>
            internal Type Type;
        }

        private static readonly Dictionary<string, Registration> Commands =
            new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<CucliCommandDiagnostic> Diagnostics =
            new List<CucliCommandDiagnostic>();

        internal static int Count => Commands.Count;

        internal static IReadOnlyList<CucliCommandDiagnostic> DiagnosticList => Diagnostics;

        /// <summary>
        /// Fingerprint of the <em>package</em> command surface only. This is what the shipped
        /// <c>commands/registry.json</c> records, so drift detection keeps working in a project that
        /// has grown its own commands.
        /// </summary>
        internal static string CatalogHash { get; private set; } = string.Empty;

        /// <summary>Fingerprint of every registered command, package and project alike.</summary>
        internal static string FullCatalogHash { get; private set; } = string.Empty;

        internal static int PackageCommandCount =>
            Commands.Values.Count(registration => registration.FromPackage);

        internal static int ProjectCommandCount =>
            Commands.Values.Count(registration => !registration.FromPackage);

        internal static void Reload()
        {
            Commands.Clear();
            Diagnostics.Clear();

            // Package commands are visited first so that diagnostics are deterministic across machines
            // and so that a project/package name collision reports the package command as the one
            // already holding the name. A collision is refused below, never resolved by shadowing.
            foreach (Type type in TypeCache.GetTypesDerivedFrom<ICucliCommand>()
                .OrderByDescending(IsFromPackage)
                .ThenBy(candidate => candidate.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;

                // A command built from a project-side JSON declaration is instantiated by its loader
                // with the definition and origin, so it deliberately has no parameterless constructor.
                // It is derived from ICucliCommand, so TypeCache finds it, and without this exclusion
                // every project would report a spurious COMMAND_CONSTRUCTOR_MISSING in `cucli doctor`.
                if (typeof(CucliLocalCommand).IsAssignableFrom(type)) continue;

                // Deliberately no accessibility check. Every command in this package is
                // `internal sealed class`, and project extensions are usually internal too; the
                // implicit parameterless constructor of a non-public class is still public in
                // metadata, so Activator.CreateInstance constructs it fine. Requiring `public` here
                // would silently register nothing at all.
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    AddDiagnostic("COMMAND_CONSTRUCTOR_MISSING", type, null,
                        "Command type requires a parameterless constructor.");
                    continue;
                }

                ICucliCommand command;
                try
                {
                    command = (ICucliCommand)Activator.CreateInstance(type);
                }
                catch (Exception exception)
                {
                    AddDiagnostic("COMMAND_CONSTRUCTION_FAILED", type, null,
                        exception.GetBaseException().Message);
                    continue;
                }

                string name;
                try
                {
                    name = command.Name;
                }
                catch (Exception exception)
                {
                    AddDiagnostic("COMMAND_NAME_THREW", type, null, exception.GetBaseException().Message);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    AddDiagnostic("COMMAND_NAME_EMPTY", type, null, "Command name must not be empty.");
                    continue;
                }

                bool fromPackage = IsFromPackage(type);
                Registration existing;
                if (Commands.TryGetValue(name, out existing))
                {
                    // Shadowing is refused on purpose.
                    //
                    // A project extension is meant to add capability, and letting it silently
                    // redefine a shipped group would make the same `cucli object create` mean
                    // different things in different projects, and would also drop that command out of
                    // the package-only catalog hash that drift detection relies on. Extensions should
                    // pick their own group name; a genuinely general improvement belongs upstream.
                    AddDiagnostic("COMMAND_DUPLICATE", type, name,
                        "Already registered by " + existing.Command.GetType().FullName + " ("
                        + (existing.FromPackage ? "package" : "project") + "). "
                        + "Project extensions must use a group name of their own; upstream a genuinely "
                        + "general change instead of shadowing a shipped group.");
                    continue;
                }

                Commands.Add(name, new Registration
                {
                    Command = command,
                    FromPackage = fromPackage,
                    Type = type
                });
            }

            CucliCommandDescriptor[] all = Descriptors();
            FullCatalogHash = ComputeHash(all);
            CatalogHash = ComputeHash(
                all.Where(item => item.Source == SourcePackage).ToArray());
        }

        /// <summary>
        /// True when the command was compiled into this package. Everything else - project scripts,
        /// third-party editor tooling, test assemblies - counts as a project extension.
        /// </summary>
        private static bool IsFromPackage(Type type)
        {
            return CucliPackage.IsPackageAssembly(type.Assembly);
        }

        internal static bool TryGet(string name, out ICucliCommand command)
        {
            Registration registration;
            if (Commands.TryGetValue(name ?? string.Empty, out registration))
            {
                command = registration.Command;
                return true;
            }

            command = null;
            return false;
        }

        internal static bool IsProjectCommand(string name)
        {
            Registration registration;
            return Commands.TryGetValue(name ?? string.Empty, out registration) && !registration.FromPackage;
        }

        internal static IEnumerable<ICucliCommand> All =>
            Commands.Values.Select(registration => registration.Command);

        internal static IEnumerable<string> Names =>
            Commands.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        internal static CucliCommandDescriptor[] Descriptors()
        {
            return Commands.Values
                .Select(registration => Describe(
                    registration.Command, registration.FromPackage, registration.Type))
                .OrderBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static CucliCommandDescriptor Describe(ICucliCommand command)
        {
            Registration registration;
            bool registered = Commands.TryGetValue(command.Name ?? string.Empty, out registration);
            return Describe(
                command,
                registered && registration.FromPackage,
                registered ? registration.Type : command.GetType());
        }

        private static CucliCommandDescriptor Describe(ICucliCommand command, bool fromPackage, Type type)
        {
            CucliActionInfo[] actions = SafeActions(command);
            var descriptors = new CucliActionDescriptor[actions.Length];
            for (int index = 0; index < actions.Length; index++)
            {
                descriptors[index] = new CucliActionDescriptor
                {
                    Name = actions[index].Name,
                    Summary = actions[index].Summary
                };
            }

            return new CucliCommandDescriptor
            {
                Name = command.Name,
                Source = fromPackage ? SourcePackage : SourceProject,
                SourcePath = fromPackage ? null : SourcePathOf(command, type),
                Summary = SafeSummary(command),
                Actions = descriptors,
                ParametersSchemaJson = SafeSchema(command),
                RequiresEditor = command.RequiresEditor,
                RequiredMode = ModeName(command.RequiredMode),
                MutatesProject = command.MutatesProject,
                Effects = command.Effects ?? Array.Empty<string>(),
                MayReloadDomain = command.MayReloadDomain,
                OptionalPackage = string.IsNullOrWhiteSpace(command.OptionalPackage)
                    ? null
                    : command.OptionalPackage
            };
        }

        /// <summary>
        /// Best-effort source of a project command. A declarative command knows the JSON file that
        /// declared it; a compiled one is resolved through the compilation pipeline's own source list.
        /// Null means "unknown", never a guess.
        /// </summary>
        private static string SourcePathOf(ICucliCommand command, Type type)
        {
            var local = command as CucliLocalCommand;
            if (local != null) return local.Origin;
            return type == null ? null : CucliExtensions.SourcePathOf(type);
        }

        private static CucliActionInfo[] SafeActions(ICucliCommand command)
        {
            try
            {
                return command.Actions ?? Array.Empty<CucliActionInfo>();
            }
            catch (Exception)
            {
                return Array.Empty<CucliActionInfo>();
            }
        }

        internal static CucliCommandCatalog BuildCatalog()
        {
            CucliCommandDescriptor[] descriptors = Descriptors();
            return new CucliCommandCatalog
            {
                GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
                ProjectPath = CucliPaths.ProjectRoot,
                UnityVersion = Application.unityVersion,
                CatalogHash = ComputeHash(
                    descriptors.Where(item => item.Source == SourcePackage).ToArray()),
                FullCatalogHash = ComputeHash(descriptors),
                PackageCommandCount = descriptors.Count(item => item.Source == SourcePackage),
                ProjectCommandCount = descriptors.Count(item => item.Source == SourceProject),
                Commands = descriptors,
                Diagnostics = Diagnostics.ToArray()
            };
        }

        internal static void WriteCatalog()
        {
            CucliPaths.WriteAtomic(CucliPaths.Commands, CucliJson.Serialize(BuildCatalog()));
        }

        private static string SafeSummary(ICucliCommand command)
        {
            try
            {
                return command.Summary ?? command.Name;
            }
            catch (Exception)
            {
                return command.Name;
            }
        }

        private static string SafeSchema(ICucliCommand command)
        {
            try
            {
                string schema = command.ParametersSchemaJson;
                return string.IsNullOrWhiteSpace(schema)
                    ? "{\"type\":\"object\",\"additionalProperties\":true}"
                    : schema;
            }
            catch (Exception)
            {
                return "{\"type\":\"object\",\"additionalProperties\":true}";
            }
        }

        private static string ModeName(CucliMode mode)
        {
            switch (mode)
            {
                case CucliMode.Edit: return "edit";
                case CucliMode.Play: return "play";
                case CucliMode.Offline: return "offline";
                default: return "any";
            }
        }

        private static void AddDiagnostic(string code, Type type, string commandName, string message)
        {
            Diagnostics.Add(new CucliCommandDiagnostic
            {
                Code = code,
                TypeName = type == null ? null : type.FullName,
                CommandName = commandName,
                Message = message
            });
        }

        /// <summary>
        /// Order-independent fingerprint of a command surface. Only machine-relevant fields are
        /// hashed, so rewording a summary still counts as a change while reordering does not.
        /// <see cref="CucliCommandDescriptor.Source"/> is deliberately excluded, so that tagging a
        /// command cannot by itself change the published hash.
        /// </summary>
        internal static string ComputeHash(CucliCommandDescriptor[] descriptors)
        {
            var builder = new StringBuilder();
            foreach (CucliCommandDescriptor descriptor in (descriptors ?? Array.Empty<CucliCommandDescriptor>())
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(descriptor.Name).Append('\u001f')
                    .Append(descriptor.RequiredMode).Append('\u001f')
                    .Append(descriptor.RequiresEditor ? '1' : '0')
                    .Append(descriptor.MutatesProject ? '1' : '0')
                    .Append(descriptor.MayReloadDomain ? '1' : '0')
                    .Append('\u001f')
                    .Append(descriptor.OptionalPackage ?? string.Empty).Append('\u001f')
                    .Append(string.Join(",", descriptor.Effects ?? Array.Empty<string>()))
                    .Append('\u001f')
                    .Append(string.Join(",",
                        (descriptor.Actions ?? Array.Empty<CucliActionDescriptor>())
                            .Select(action => action.Name)))
                    .Append('\u001f')
                    .Append(Normalize(descriptor.ParametersSchemaJson))
                    .Append('\u001e');
            }

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        private static string Normalize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return string.Empty;
            try
            {
                return Newtonsoft.Json.Linq.JToken
                    .Parse(json)
                    .ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return json;
            }
        }
    }
}
