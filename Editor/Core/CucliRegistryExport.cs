using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Writes the shipped static catalog as an index plus one JSON shard per Editor command module.
    ///
    /// <c>commands/registry.json</c> keeps aliases, offline groups and <c>controlActions</c>.
    /// Package groups live in <c>commands/modules/{module}.json</c> so a single file does not
    /// grow with every command. The PowerShell CLI merges the shards while Unity is closed.
    /// </summary>
    internal static class CucliRegistryExport
    {
        internal const string ModulesFolderName = "modules";
        internal const string IndexFileName = "registry.json";

        internal static JObject ToGroupObject(
            CucliCommandDescriptor descriptor,
            Func<string, JToken> schemaParser)
        {
            var actions = new JArray();
            foreach (CucliActionDescriptor action in descriptor.Actions ?? Array.Empty<CucliActionDescriptor>())
            {
                actions.Add(new JObject { ["name"] = action.Name, ["summary"] = action.Summary });
            }

            return new JObject
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
                ["parametersSchema"] = schemaParser == null
                    ? new JObject()
                    : schemaParser(descriptor.ParametersSchemaJson)
            };
        }

        /// <summary>
        /// Writes <paramref name="registryPath"/> as the index and, when the file is named
        /// <c>registry.json</c>, shards groups into sibling <c>modules/</c> files.
        /// A custom filename keeps the historical single-file layout so callers that pass an
        /// explicit dump path still get one document.
        /// </summary>
        internal static void Write(
            string registryPath,
            JObject root,
            IList<CucliCommandDescriptor> packageGroups,
            Func<string, JToken> schemaParser,
            out JArray includes,
            out int groupCount,
            out int actionCount)
        {
            includes = new JArray();
            groupCount = packageGroups == null ? 0 : packageGroups.Count;
            actionCount = 0;

            var grouped = new SortedDictionary<string, JArray>(StringComparer.Ordinal);
            if (packageGroups != null)
            {
                foreach (CucliCommandDescriptor descriptor in packageGroups)
                {
                    JObject group = ToGroupObject(descriptor, schemaParser);
                    JArray actions = group["actions"] as JArray;
                    actionCount += actions == null ? 0 : actions.Count;

                    string module = string.IsNullOrWhiteSpace(descriptor.RegistryModule)
                        ? "core"
                        : descriptor.RegistryModule.Trim().ToLowerInvariant();
                    JArray bucket;
                    if (!grouped.TryGetValue(module, out bucket))
                    {
                        bucket = new JArray();
                        grouped[module] = bucket;
                    }

                    bucket.Add(group);
                }
            }

            bool split = string.Equals(
                Path.GetFileName(registryPath),
                IndexFileName,
                StringComparison.OrdinalIgnoreCase);

            if (!split)
            {
                var groups = new JArray();
                foreach (JArray bucket in grouped.Values)
                {
                    foreach (JToken group in bucket) groups.Add(group);
                }

                root.Remove("includes");
                root["groups"] = groups;
                CucliPaths.WriteAtomic(registryPath, root.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
                return;
            }

            string commandsDir = Path.GetDirectoryName(registryPath);
            string modulesDir = Path.Combine(commandsDir, ModulesFolderName);
            Directory.CreateDirectory(modulesDir);
            EnsureFolderMeta(modulesDir);

            var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, JArray> pair in grouped)
            {
                string fileName = pair.Key + ".json";
                string absolute = Path.Combine(modulesDir, fileName);
                var moduleRoot = new JObject
                {
                    ["schemaVersion"] = "cucli.command-registry-module/v1",
                    ["module"] = pair.Key,
                    ["groups"] = pair.Value
                };
                CucliPaths.WriteAtomic(
                    absolute,
                    moduleRoot.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
                EnsureFileMeta(absolute, pair.Key);
                writtenFiles.Add(fileName);
                includes.Add(ModulesFolderName + "/" + fileName);
            }

            RemoveStaleModules(modulesDir, writtenFiles);

            root["includes"] = includes;
            root.Remove("groups");
            CucliPaths.WriteAtomic(registryPath, root.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
        }

        private static void RemoveStaleModules(string modulesDir, HashSet<string> writtenFiles)
        {
            if (!Directory.Exists(modulesDir)) return;

            foreach (string path in Directory.GetFiles(modulesDir, "*.json"))
            {
                string name = Path.GetFileName(path);
                if (writtenFiles.Contains(name)) continue;
                File.Delete(path);
                string meta = path + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
            }
        }

        private static void EnsureFolderMeta(string folder)
        {
            string metaPath = folder + ".meta";
            if (File.Exists(metaPath)) return;
            WriteMeta(metaPath, StableGuid("cucli.command-registry-modules-dir"), folderAsset: true);
        }

        private static void EnsureFileMeta(string jsonPath, string module)
        {
            string metaPath = jsonPath + ".meta";
            if (File.Exists(metaPath)) return;
            WriteMeta(
                metaPath,
                StableGuid("cucli.command-registry-module/" + module),
                folderAsset: false);
        }

        private static void WriteMeta(string path, string guid, bool folderAsset)
        {
            string body = folderAsset
                ? "fileFormatVersion: 2\nguid: " + guid + "\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
                : "fileFormatVersion: 2\nguid: " + guid + "\nTextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
            File.WriteAllText(path, body, CucliJson.Utf8NoBom);
        }

        private static string StableGuid(string key)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var builder = new StringBuilder(32);
                for (int index = 0; index < 16; index++) builder.Append(hash[index].ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
