using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli memory ...</c> - read-only audits over the project's assets and the Editor's loaded
    /// objects: dangling serialized references, what references a given asset, duplicate content,
    /// and assets nothing reaches from a scene or prefab.
    ///
    /// Everything here is generic. No path, table name, folder convention or asset name from any
    /// particular project appears in this file; a project that wants its own conventions grows them
    /// in the extension area.
    ///
    /// Output volume is the whole design problem for this group: on a real project <c>audit</c> and
    /// <c>unused</c> can match tens of thousands of assets. Every list is capped by <c>maxScan</c>
    /// while it is being built and paged by <c>pageSize</c> on the way out, and every response states
    /// its true <c>total</c> and whether the scan itself was cut short.
    /// </summary>
    internal sealed class MemoryCommand : CucliCommand
    {
        private const string GroupName = "memory";

        /// <summary>Small by design: a caller that wants more asks for more explicitly.</summary>
        private const int DefaultPageSize = 50;

        /// <summary>How many assets a single scan will examine before it stops and says so.</summary>
        private const int DefaultMaxScan = 500;

        private const int HardMaxScan = 20000;

        /// <summary>Files below this size are ignored by <c>duplicates</c>; tiny files collide by accident.</summary>
        private const long DefaultMinDuplicateBytes = 1024L;

        /// <summary>Upper bound on bytes fingerprinted per file, so one huge asset cannot stall a scan.</summary>
        private const int FingerprintByteLimit = 8 * 1024 * 1024;

        public override string Name => GroupName;

        public override string Summary =>
            "Read-only asset and object audits: dangling references, reverse references, duplicate content, unreachable assets.";

        public override CucliActionInfo[] Actions => Declare(
            "audit", "Scan assets for dangling serialized references and report counts by asset type.",
            "objects", "Count loaded objects by type and estimate their memory, largest first.",
            "references", "Find which assets reference a given asset, plus its own dependency count.",
            "deps", "List what a given asset depends on, transitively by default.",
            "duplicates", "Group assets whose content fingerprint is identical.",
            "unused", "List assets that no scene or prefab in the project reaches (candidates, not a verdict).");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        /// <summary>Pure inspection: nothing here writes, moves, imports or repairs anything.</summary>
        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "project.assets.read", "editor.memory.read" };

        public override string OptionalPackage => null;

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""audit"", ""objects"", ""references"", ""deps"", ""duplicates"", ""unused""],
      ""default"": ""audit""
    },
    ""path"": { ""type"": ""string"", ""description"": ""action=references: the asset to find referencers of."", ""aliases"": [""assetPath"", ""asset_path""] },
    ""folder"": { ""type"": ""string"", ""description"": ""Restrict the scan to this folder. Defaults to Assets."", ""aliases"": [""root"", ""scope""] },
    ""typeName"": { ""type"": ""string"", ""description"": ""action=objects: only count loaded objects of this type."", ""aliases"": [""type_name"", ""type""] },
    ""sortBy"": { ""type"": ""string"", ""enum"": [""memory"", ""count"", ""name""], ""default"": ""memory"", ""aliases"": [""sort_by""] },
    ""maxScan"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 20000, ""default"": 500, ""description"": ""How many assets a scan may examine before it reports truncation."", ""aliases"": [""max_scan""] },
    ""minSize"": { ""type"": ""integer"", ""default"": 1024, ""description"": ""action=duplicates: ignore files smaller than this many bytes."", ""aliases"": [""min_size""] },
    ""allScenes"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=unused: use every scene in the project, not just the enabled build scenes."", ""aliases"": [""all_scenes""] },
    ""includeEmpty"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=objects: include types with zero loaded instances."", ""aliases"": [""include_empty""] },
    ""pageSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50, ""aliases"": [""page_size"", ""limit""] },
    ""cursor"": { ""type"": ""string"" },
    ""pageNumber"": { ""type"": ""integer"", ""minimum"": 1, ""aliases"": [""page_number""] },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 },
    ""maxStringLength"": { ""type"": ""integer"", ""default"": 4096, ""aliases"": [""max_string_length""] }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "audit");
            switch (action)
            {
                case "audit": return Audit(context);
                case "objects": return Objects(context);
                case "references": return References(context);
                case "deps": return Deps(context);
                case "duplicates": return Duplicates(context);
                case "unused": return Unused(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ------------------------------------------------------------------ audit

        /// <summary>
        /// Scans assets for dangling serialized object references and reports the type mix.
        ///
        /// A dangling reference is the precise, low-noise signal: Unity keeps the target's instance id
        /// on the serialized property after the target is gone, so <c>objectReferenceValue == null</c>
        /// together with a non-zero <c>objectReferenceInstanceIDValue</c> means "this pointed at
        /// something that no longer exists" rather than "this field is intentionally empty".
        /// </summary>
        private static CucliCommandResult Audit(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            int maxScan = ClampMaxScan(args.GetInt("maxScan", DefaultMaxScan));

            string folder;
            var scan = new ScanResult();
            List<string> assets = CollectAssets(args, maxScan, scan, out folder);
            if (assets == null)
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, scan.Error);
            }

            var findings = new List<JObject>();
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            int scanned = 0;
            int dangling = 0;
            int missingScripts = 0;

            foreach (string path in assets)
            {
                context.ThrowIfCancelled();
                scanned++;

                string type = AssetDatabase.GetMainAssetTypeAtPath(path) == null
                    ? "Unknown"
                    : AssetDatabase.GetMainAssetTypeAtPath(path).Name;
                int current;
                byType.TryGetValue(type, out current);
                byType[type] = current + 1;

                Object main = CucliAssets.LoadMain(path);
                if (main == null) continue;

                int danglingHere = 0;
                var samples = new JArray();

                try
                {
                    var serialized = new SerializedObject(main);
                    SerializedProperty property = serialized.GetIterator();
                    bool enterChildren = true;
                    int guard = 0;

                    while (property.NextVisible(enterChildren) && guard++ < 20000)
                    {
                        enterChildren = false;
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (property.objectReferenceValue != null) continue;
                        if (property.objectReferenceInstanceIDValue == 0) continue;

                        danglingHere++;
                        if (samples.Count < 3)
                        {
                            samples.Add(new JObject
                            {
                                ["propertyPath"] = property.propertyPath,
                                ["missingInstanceId"] = property.objectReferenceInstanceIDValue
                            });
                        }
                    }

                    // A MonoBehaviour whose script asset is gone has no SerializedObject at all.
                    int missing = CountMissingScripts(main);
                    if (missing > 0)
                    {
                        missingScripts += missing;
                        samples.Add(new JObject { ["missingComponents"] = missing });
                    }
                }
                catch (Exception exception)
                {
                    // Some asset types refuse to build a SerializedObject; that is not a finding.
                    context.Warn("Could not inspect " + path + ": " + exception.GetBaseException().Message);
                }

                if (danglingHere == 0) continue;
                dangling += danglingHere;
                findings.Add(new JObject
                {
                    ["path"] = path,
                    ["type"] = type,
                    ["danglingReferences"] = danglingHere,
                    ["samples"] = samples
                });
            }

            var typeRows = new JArray();
            foreach (KeyValuePair<string, int> entry in byType
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                typeRows.Add(new JObject { ["type"] = entry.Key, ["count"] = entry.Value });
            }

            JObject payload = new JObject
            {
                ["folder"] = folder,
                ["scanned"] = scanned,
                ["scanTruncated"] = scan.Truncated,
                ["maxScan"] = maxScan,
                ["assetsWithDanglingReferences"] = findings.Count,
                ["danglingReferenceCount"] = dangling,
                ["missingScriptCount"] = missingScripts,
                ["assetsByType"] = typeRows
            };

            CucliPage<JObject> page = CucliPaging.Slice(findings, args, DefaultPageSize);
            JObject data = CucliPaging.Envelope(page, new JArray(page.Items.Cast<object>().ToArray()), payload);

            if (scan.Truncated)
            {
                context.Warn("Only the first " + maxScan + " assets were examined; raise maxScan for a deeper audit.");
            }

            return CucliCommandResult.Ok(
                data,
                "已扫描 " + scanned + " 个资产：" + findings.Count + " 个含悬空引用，共 " + dangling
                + " 处；缺失脚本 " + missingScripts + " 处。");
        }

        private static int CountMissingScripts(Object main)
        {
            var gameObject = main as GameObject;
            if (gameObject == null) return 0;

            int missing = 0;
            Component[] components;
            try
            {
                components = gameObject.GetComponents<Component>();
            }
            catch (Exception)
            {
                return 0;
            }

            foreach (Component component in components)
            {
                if (component == null) missing++;
            }

            return missing;
        }

        // ------------------------------------------------------------------ objects

        /// <summary>
        /// Counts loaded objects by type and sums their runtime memory.
        ///
        /// <c>Resources.FindObjectsOfTypeAll</c> deliberately includes assets, prefab contents and
        /// hidden objects, which is what makes this useful for finding what a domain actually holds.
        /// Memory is reported as null where the profiler refuses a type rather than as a fake zero.
        /// </summary>
        private static CucliCommandResult Objects(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string typeFilter = args.GetString("typeName") ?? args.GetString("type_name");
            string sortBy = (args.GetString("sortBy") ?? args.GetString("sort_by") ?? "memory").ToLowerInvariant();
            bool includeEmpty = args.GetBool("includeEmpty", args.GetBool("include_empty", false));
            CucliDescribe.Options options = CucliDescribe.OptionsFrom(args);

            Object[] loaded;
            try
            {
                if (!string.IsNullOrWhiteSpace(typeFilter))
                {
                    string resolveError;
                    Type type = CucliReflection.FindType(typeFilter, out resolveError);
                    if (type == null)
                    {
                        return CucliCommandResult.Fail(CucliErrors.TypeNotFound, resolveError);
                    }

                    loaded = Resources.FindObjectsOfTypeAll(type);
                }
                else
                {
                    loaded = Resources.FindObjectsOfTypeAll(typeof(Object));
                }
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "已加载对象枚举失败：" + exception.GetBaseException().Message);
            }

            var buckets = new Dictionary<string, ObjectBucket>(StringComparer.Ordinal);

            foreach (Object item in loaded)
            {
                context.ThrowIfCancelled();
                if (item == null) continue;

                Type type = item.GetType();
                string key = type.FullName ?? type.Name;

                ObjectBucket bucket;
                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new ObjectBucket { TypeName = type.Name, FullName = key };
                    buckets[key] = bucket;
                }

                bucket.Count++;

                long memory;
                if (TryGetMemory(item, out memory))
                {
                    bucket.Memory += memory;
                    bucket.MemoryKnown++;
                }
                else
                {
                    bucket.MemoryUnknown++;
                }

                if (bucket.Samples == null) bucket.Samples = new JArray();
                if (bucket.Samples.Count < 3)
                {
                    bucket.Samples.Add(CucliDescribe.UnityObject(item, options, 0));
                }
            }

            var rows = new List<ObjectBucket>();
            foreach (ObjectBucket bucket in buckets.Values)
            {
                if (!includeEmpty && bucket.Count == 0) continue;
                rows.Add(bucket);
            }

            switch (sortBy)
            {
                case "count":
                    rows.Sort((left, right) => right.Count.CompareTo(left.Count));
                    break;
                case "name":
                    rows.Sort((left, right) => string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));
                    break;
                default:
                    rows.Sort((left, right) => right.Memory.CompareTo(left.Memory));
                    break;
            }

            long totalMemory = 0;
            int totalObjects = 0;
            foreach (ObjectBucket bucket in rows)
            {
                totalMemory += bucket.Memory;
                totalObjects += bucket.Count;
            }

            CucliPage<ObjectBucket> page = CucliPaging.Slice(rows, args, DefaultPageSize);
            var items = new JArray();
            foreach (ObjectBucket bucket in page.Items)
            {
                items.Add(new JObject
                {
                    ["type"] = bucket.TypeName,
                    ["fullName"] = bucket.FullName,
                    ["count"] = bucket.Count,
                    ["memoryBytes"] = bucket.MemoryKnown == 0 ? JValue.CreateNull() : new JValue(bucket.Memory),
                    ["memoryUnknownCount"] = bucket.MemoryUnknown == 0 ? JValue.CreateNull() : new JValue(bucket.MemoryUnknown),
                    ["samples"] = bucket.Samples ?? new JArray()
                });
            }

            var payload = new JObject
            {
                ["typeCount"] = rows.Count,
                ["objectCount"] = totalObjects,
                ["memoryBytes"] = totalMemory,
                ["sortBy"] = sortBy,
                ["typeFilter"] = typeFilter
            };

            JObject data = CucliPaging.Envelope(page, items, payload);

            if (totalMemory == 0)
            {
                context.Warn("No object reported a memory size; memory figures are unavailable in this Editor state.");
            }

            return CucliCommandResult.Ok(
                data,
                "已加载对象 " + totalObjects + " 个，覆盖 " + rows.Count + " 个类型，估算内存 "
                + FormatBytes(totalMemory) + "。");
        }

        private static bool TryGetMemory(Object item, out long bytes)
        {
            bytes = 0L;
            try
            {
                bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(item);
                return true;
            }
            catch (Exception)
            {
                // Not every object type reports a size; callers must see "unknown", not zero.
                return false;
            }
        }

        private sealed class ObjectBucket
        {
            internal string TypeName;
            internal string FullName;
            internal int Count;
            internal long Memory;
            internal int MemoryKnown;
            internal int MemoryUnknown;
            internal JArray Samples;
        }

        // ------------------------------------------------------------------ references

        /// <summary>
        /// Finds the assets that reference <c>path</c>, by scanning candidates and asking the asset
        /// database for each one's dependencies. There is no reverse index in the public API, so this
        /// is inherently a bounded scan and says so.
        /// </summary>
        // ------------------------------------------------------------------ deps

        /// <summary>
        /// The forward direction: what this asset points at. The mirror image of
        /// <see cref="References"/>, which walks the project looking for who points at it. This one is
        /// a single AssetDatabase call and is therefore exact and cheap.
        /// </summary>
        private static CucliCommandResult Deps(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string requested = args.GetString("path") ?? args.GetString("assetPath") ?? args.GetString("asset_path");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing("path", "Example: --path Assets/Prefabs/Hero.prefab");
            }

            string target = CucliAssets.NormalizeAssetPath(requested);
            if (target == null || CucliAssets.LoadMain(target) == null)
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, "资产不存在：" + requested);
            }

            bool recursive = args.GetBool("recursive", true);
            string[] dependencies;
            try
            {
                dependencies = AssetDatabase.GetDependencies(target, recursive);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "读取依赖失败：" + exception.GetBaseException().Message);
            }

            var items = new List<JObject>();
            foreach (string dependency in dependencies)
            {
                // GetDependencies reports the asset itself; a self-edge is noise, not a dependency.
                if (string.Equals(dependency, target, StringComparison.OrdinalIgnoreCase)) continue;

                Type type = AssetDatabase.GetMainAssetTypeAtPath(dependency);
                items.Add(new JObject
                {
                    ["path"] = dependency,
                    ["type"] = type == null ? "Unknown" : type.Name,
                    ["extension"] = CucliAssets.Extension(dependency).ToLowerInvariant()
                });
            }

            items.Sort((left, right) => string.Compare(
                left["path"] == null ? string.Empty : left["path"].Value<string>(),
                right["path"] == null ? string.Empty : right["path"].Value<string>(),
                StringComparison.OrdinalIgnoreCase));

            CucliPage<JObject> page = CucliPaging.Slice(items, args, DefaultPageSize);
            JObject data = CucliPaging.Envelope(
                page,
                new JArray(page.Items.Cast<object>().ToArray()),
                new JObject
                {
                    ["path"] = target,
                    ["recursive"] = recursive,
                    ["dependencyCount"] = items.Count
                });

            return CucliCommandResult.Ok(
                data,
                target + " 依赖 " + items.Count + " 个资产（recursive=" + recursive + "）。");
        }

        private static CucliCommandResult References(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string requested = args.GetString("path") ?? args.GetString("assetPath") ?? args.GetString("asset_path");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing("path", "Example: --path Assets/Prefabs/Hero.prefab");
            }

            string target = CucliAssets.NormalizeAssetPath(requested);
            if (target == null || CucliAssets.LoadMain(target) == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "资产不存在：" + requested);
            }

            int maxScan = ClampMaxScan(args.GetInt("maxScan", DefaultMaxScan));
            var scan = new ScanResult();
            string folder;
            List<string> candidates = CollectAssets(args, maxScan, scan, out folder);
            if (candidates == null)
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, scan.Error);
            }

            string[] forward;
            try
            {
                forward = AssetDatabase.GetDependencies(target, true);
            }
            catch (Exception)
            {
                forward = Array.Empty<string>();
            }

            var referencers = new List<JObject>();
            foreach (string candidate in candidates)
            {
                context.ThrowIfCancelled();
                if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)) continue;

                string[] dependencies;
                try
                {
                    dependencies = AssetDatabase.GetDependencies(candidate, false);
                }
                catch (Exception)
                {
                    continue;
                }

                bool hit = false;
                foreach (string dependency in dependencies)
                {
                    if (!string.Equals(dependency, target, StringComparison.OrdinalIgnoreCase)) continue;
                    hit = true;
                    break;
                }

                if (!hit) continue;
                referencers.Add(new JObject
                {
                    ["path"] = candidate,
                    ["type"] = AssetDatabase.GetMainAssetTypeAtPath(candidate) == null
                        ? "Unknown"
                        : AssetDatabase.GetMainAssetTypeAtPath(candidate).Name
                });
            }

            CucliPage<JObject> page = CucliPaging.Slice(referencers, args, DefaultPageSize);
            JObject data = CucliPaging.Envelope(
                page,
                new JArray(page.Items.Cast<object>().ToArray()),
                new JObject
                {
                    ["target"] = target,
                    ["targetGuid"] = CucliAssets.AssetPathToGuid(target),
                    ["forwardDependencyCount"] = forward.Length,
                    ["referencerCount"] = referencers.Count,
                    ["scanned"] = candidates.Count,
                    ["scanTruncated"] = scan.Truncated,
                    ["scanFolder"] = folder
                });

            if (scan.Truncated)
            {
                context.Warn("Reverse lookup examined only " + maxScan
                    + " assets, so the referencer list may be incomplete. Raise maxScan or narrow folder.");
            }

            return CucliCommandResult.Ok(
                data,
                "已扫描 " + candidates.Count + " 个候选资产，" + referencers.Count
                + " 个引用它；它自身依赖 " + forward.Length + " 个资产。");
        }

        // ------------------------------------------------------------------ duplicates

        /// <summary>
        /// Groups files whose content fingerprint matches.
        ///
        /// The fingerprint is FNV-1a over the file bytes plus the file length - deliberately not a
        /// cryptographic digest, because it only has to be collision-resistant enough to group
        /// accidental copies, and it must not pull a crypto provider into this assembly. Large files
        /// fingerprint their head and tail rather than all bytes so one big asset cannot stall a scan.
        /// </summary>
        private static CucliCommandResult Duplicates(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            int maxScan = ClampMaxScan(args.GetInt("maxScan", DefaultMaxScan));
            long minSize = args.GetLong("minSize", args.GetLong("min_size", DefaultMinDuplicateBytes));
            if (minSize < 0) minSize = 0;

            var scan = new ScanResult();
            string folder;
            List<string> assets = CollectAssets(args, maxScan, scan, out folder);
            if (assets == null)
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, scan.Error);
            }

            var byFingerprint = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            int fingerprinted = 0;
            int skipped = 0;

            foreach (string path in assets)
            {
                context.ThrowIfCancelled();

                string absolute = CucliAssets.ToAbsolute(path);
                if (absolute == null || !File.Exists(absolute))
                {
                    skipped++;
                    continue;
                }

                long length;
                string fingerprint;
                try
                {
                    var info = new FileInfo(absolute);
                    length = info.Length;
                    if (length < minSize)
                    {
                        skipped++;
                        continue;
                    }

                    fingerprint = Fingerprint(absolute, length);
                }
                catch (Exception)
                {
                    skipped++;
                    continue;
                }

                fingerprinted++;
                List<string> bucket;
                if (!byFingerprint.TryGetValue(fingerprint, out bucket))
                {
                    bucket = new List<string>();
                    byFingerprint[fingerprint] = bucket;
                }

                bucket.Add(path);
            }

            var groups = new List<JObject>();
            long wastedBytes = 0L;
            foreach (KeyValuePair<string, List<string>> entry in byFingerprint)
            {
                if (entry.Value.Count < 2) continue;

                entry.Value.Sort(StringComparer.Ordinal);
                long size = 0L;
                try
                {
                    string first = CucliAssets.ToAbsolute(entry.Value[0]);
                    if (first != null && File.Exists(first)) size = new FileInfo(first).Length;
                }
                catch (Exception)
                {
                }

                wastedBytes += size * (entry.Value.Count - 1);
                groups.Add(new JObject
                {
                    ["fingerprint"] = entry.Key,
                    ["count"] = entry.Value.Count,
                    ["sizeBytes"] = size,
                    ["paths"] = new JArray(entry.Value.Cast<object>().ToArray())
                });
            }

            groups.Sort((left, right) =>
                right.Value<long>("sizeBytes").CompareTo(left.Value<long>("sizeBytes")));

            CucliPage<JObject> page = CucliPaging.Slice(groups, args, DefaultPageSize);
            JObject data = CucliPaging.Envelope(
                page,
                new JArray(page.Items.Cast<object>().ToArray()),
                new JObject
                {
                    ["folder"] = folder,
                    ["fingerprinted"] = fingerprinted,
                    ["skipped"] = skipped,
                    ["groupCount"] = groups.Count,
                    ["reclaimableBytes"] = wastedBytes,
                    ["minSize"] = minSize,
                    ["scanTruncated"] = scan.Truncated
                });

            context.Warn("指纹是 FNV-1a 内容摘要，不是密码学哈希，也不是逐字节比对；"
                + "它用于发现重复副本，不作为删除依据。");

            return CucliCommandResult.Ok(
                data,
                "已指纹 " + fingerprinted + " 个文件，发现 " + groups.Count + " 组重复，可回收约 "
                + FormatBytes(wastedBytes) + "。");
        }

        private static string Fingerprint(string absolute, long length)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offset;
            hash = Mix(hash, (ulong)length, prime);

            using (var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buffer = new byte[65536];

                if (length <= FingerprintByteLimit)
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        for (int index = 0; index < read; index++) hash = (hash ^ buffer[index]) * prime;
                    }
                }
                else
                {
                    // Head and tail only: enough to distinguish real copies without reading gigabytes.
                    int half = FingerprintByteLimit / 2;
                    int read = stream.Read(buffer, 0, Math.Min(buffer.Length, half));
                    for (int index = 0; index < read; index++) hash = (hash ^ buffer[index]) * prime;

                    stream.Seek(-half, SeekOrigin.End);
                    read = stream.Read(buffer, 0, Math.Min(buffer.Length, half));
                    for (int index = 0; index < read; index++) hash = (hash ^ buffer[index]) * prime;
                }
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static ulong Mix(ulong hash, ulong value, ulong prime)
        {
            for (int index = 0; index < 8; index++)
            {
                hash = (hash ^ ((value >> (index * 8)) & 0xFFUL)) * prime;
            }

            return hash;
        }

        // ------------------------------------------------------------------ unused

        /// <summary>
        /// Lists assets in <c>folder</c> that no scene or prefab reaches.
        ///
        /// This is a <em>candidate</em> list, not a verdict: assets loaded by name at runtime, by
        /// Addressables, by Resources, or from a scene that is not in the build list will all show up
        /// here while still being used. The response says so rather than implying they are safe to
        /// delete.
        /// </summary>
        private static CucliCommandResult Unused(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            int maxScan = ClampMaxScan(args.GetInt("maxScan", DefaultMaxScan));
            bool allScenes = args.GetBool("allScenes", args.GetBool("all_scenes", false));

            var scan = new ScanResult();
            string folder;
            List<string> assets = CollectAssets(args, maxScan, scan, out folder);
            if (assets == null)
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, scan.Error);
            }

            var roots = new List<string>();
            var rootScanTruncated = false;

            if (allScenes)
            {
                string[] sceneGuids = AssetDatabase.FindAssets("t:Scene");
                foreach (string guid in sceneGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path)) roots.Add(path);
                }
            }
            else
            {
                foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
                {
                    if (scene == null || !scene.enabled) continue;
                    if (!string.IsNullOrEmpty(scene.path)) roots.Add(scene.path);
                }
            }

            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            int prefabLimit = Math.Min(prefabGuids.Length, maxScan);
            if (prefabGuids.Length > prefabLimit) rootScanTruncated = true;
            for (int index = 0; index < prefabLimit; index++)
            {
                string path = AssetDatabase.GUIDToAssetPath(prefabGuids[index]);
                if (!string.IsNullOrEmpty(path)) roots.Add(path);
            }

            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                context.ThrowIfCancelled();
                try
                {
                    foreach (string dependency in AssetDatabase.GetDependencies(root, true))
                    {
                        reachable.Add(dependency);
                    }
                }
                catch (Exception)
                {
                }
            }

            var candidates = new List<JObject>();
            foreach (string path in assets)
            {
                context.ThrowIfCancelled();
                if (reachable.Contains(path)) continue;

                // Scenes and prefabs are roots themselves; "unreferenced" says nothing about them.
                string extension = CucliAssets.Extension(path).ToLowerInvariant();
                if (extension == ".unity" || extension == ".prefab") continue;

                candidates.Add(new JObject
                {
                    ["path"] = path,
                    ["type"] = AssetDatabase.GetMainAssetTypeAtPath(path) == null
                        ? "Unknown"
                        : AssetDatabase.GetMainAssetTypeAtPath(path).Name
                });
            }

            // When the reachability graph is incomplete, "not reached" is not evidence of "unused".
            // Listing those as candidates would invite deletions based on a partial scan, so they move
            // to "unresolved" instead - the honest answer when the scan cannot support a verdict.
            bool graphIncomplete = scan.Truncated || rootScanTruncated;
            var unresolved = new JArray();
            if (graphIncomplete)
            {
                foreach (JObject candidate in candidates) unresolved.Add(candidate);
                candidates.Clear();
            }

            CucliPage<JObject> page = CucliPaging.Slice(candidates, args, DefaultPageSize);
            JObject data = CucliPaging.Envelope(
                page,
                new JArray(page.Items.Cast<object>().ToArray()),
                new JObject
                {
                    ["folder"] = folder,
                    ["scanned"] = assets.Count,
                    ["scanTruncated"] = scan.Truncated,
                    ["rootCount"] = roots.Count,
                    ["rootScanTruncated"] = rootScanTruncated,
                    ["reachableCount"] = reachable.Count,
                    ["candidateCount"] = candidates.Count,
                    ["unresolved"] = unresolved,
                    ["unresolvedCount"] = unresolved.Count,
                    ["graphIncomplete"] = graphIncomplete,
                    ["allScenes"] = allScenes,
                    ["caveat"] = "Candidates only: runtime-loaded, Addressables, Resources and non-build "
                        + "scenes are not part of the reachability graph, so a listed asset may still be in use."
                });

            context.Warn("unused 只是候选清单：Resources 加载、Addressables、运行时按名加载以及未加入构建的场景"
                + "都不在可达图里，列出的资产可能仍在使用。不要据此直接删除。");
            if (rootScanTruncated)
            {
                context.Warn("Prefab roots were capped at maxScan, so the reachability graph is partial.");
            }

            if (graphIncomplete)
            {
                context.Warn("本次扫描不完整（scanTruncated=" + scan.Truncated + ", rootScanTruncated="
                    + rootScanTruncated + "），因此不给出任何 unused 结论：" + unresolved.Count
                    + " 个资产归入 unresolved。请提高 maxScan 后重跑再判断。");
            }

            string summary = graphIncomplete
                ? "已扫描 " + assets.Count + " 个资产（根 " + roots.Count + " 个），但可达图不完整，"
                  + "故不给出 unused 结论；" + unresolved.Count + " 个资产归入 unresolved。"
                : "已扫描 " + assets.Count + " 个资产（根 " + roots.Count + " 个），"
                  + candidates.Count + " 个候选未被场景/预制体引用。";

            return CucliCommandResult.Ok(data, summary);
        }

        // ------------------------------------------------------------------ shared scan

        private sealed class ScanResult
        {
            internal bool Truncated;
            internal string Error;
        }

        /// <summary>
        /// Resolves the folder to scan and collects at most <paramref name="maxScan"/> asset paths.
        /// Returns null with <see cref="ScanResult.Error"/> set when the folder is not usable.
        /// </summary>
        private static List<string> CollectAssets(
            CucliArgs args,
            int maxScan,
            ScanResult scan,
            out string folder)
        {
            string requested = args.GetString("folder") ?? args.GetString("root") ?? args.GetString("scope");
            folder = string.IsNullOrWhiteSpace(requested)
                ? "Assets"
                : CucliAssets.NormalizeAssetPath(requested);

            if (folder == null)
            {
                scan.Error = "文件夹必须在工程内（Assets/... 或 Packages/...）：" + requested;
                return null;
            }

            if (!CucliAssets.FolderExists(folder))
            {
                scan.Error = "文件夹不存在：" + folder;
                return null;
            }

            // Ask for one extra so truncation is detectable rather than guessed.
            string[] found = CucliAssets.FindAssets(null, folder, null, maxScan + 1);

            var paths = new List<string>(Math.Min(found.Length, maxScan));
            foreach (string path in found)
            {
                if (paths.Count >= maxScan)
                {
                    scan.Truncated = true;
                    break;
                }

                if (AssetDatabase.IsValidFolder(path)) continue;
                paths.Add(path);
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        private static int ClampMaxScan(int requested)
        {
            if (requested <= 0) return DefaultMaxScan;
            return requested > HardMaxScan ? HardMaxScan : requested;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024L) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (value >= 1024d && unit < units.Length - 1)
            {
                value /= 1024d;
                unit++;
            }

            return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
        }
    }
}
