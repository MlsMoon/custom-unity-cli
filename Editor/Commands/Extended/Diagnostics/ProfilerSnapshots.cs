using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Memory snapshot support: taking a snapshot through the optional Memory Profiler package, and
    /// listing/comparing <c>.snap</c> files.
    ///
    /// Taking a snapshot is the only action here that needs a package
    /// (<c>com.unity.memoryprofiler</c>), so it is reached purely by reflection and degrades to
    /// <c>OPTIONAL_PACKAGE_MISSING</c>. Listing and comparing are plain file operations and
    /// deliberately work without the package: a snapshot someone captured by hand is still worth
    /// being able to inspect from the CLI.
    /// </summary>
    internal static class ProfilerSnapshots
    {
        internal const string PackageName = "com.unity.memoryprofiler";

        /// <summary>Type names the Memory Profiler API has used, most likely first.</summary>
        private static readonly string[] CandidateTypeNames =
        {
            "Unity.Profiling.Memory.MemoryProfiler",
            "UnityEngine.Profiling.Memory.Experimental.MemoryProfiler",
            "Unity.MemoryProfiler.MemoryProfiler"
        };

        private static bool s_probed;
        private static Type s_type;

        internal static Type ResolveType()
        {
            if (s_probed) return s_type;
            s_probed = true;

            foreach (string candidate in CandidateTypeNames)
            {
                Type resolved = CucliOptional.FindType(null, candidate);
                if (resolved == null) continue;
                s_type = resolved;
                break;
            }

            return s_type;
        }

        internal static bool IsAvailable
        {
            get { return ResolveType() != null; }
        }

        internal static string DefaultSnapshotDirectory
        {
            get { return Path.Combine(Application.temporaryCachePath, "MemoryCaptures"); }
        }

        internal static string DefaultSnapshotPath()
        {
            return Path.Combine(
                DefaultSnapshotDirectory,
                "snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".snap");
        }

        // ---- taking a snapshot -------------------------------------------------------------

        /// <summary>
        /// Holds one in-flight snapshot. An instance (rather than statics) keeps two requests from
        /// overwriting each other's completion flag.
        /// </summary>
        internal sealed class SnapshotRequest
        {
            internal bool Finished;
            internal bool Success;
            internal string Path;

            // Signature must stay (string, bool): Unity's callback delegate is declared that way.
            internal void OnFinished(string path, bool result)
            {
                Path = path;
                Success = result;
                Finished = true;
            }
        }

        /// <summary>
        /// Starts a snapshot and returns the request to poll. Returns false with
        /// <paramref name="error"/> when the package is absent or its callback shape is unusable.
        /// </summary>
        internal static bool TryBeginTake(string path, out SnapshotRequest request, out string error)
        {
            request = null;
            error = null;

            Type type = ResolveType();
            if (type == null)
            {
                error = "package " + PackageName + " is not installed";
                return false;
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception exception)
                {
                    error = "could not create snapshot directory " + directory + ": "
                        + exception.GetBaseException().Message;
                    return false;
                }
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo chosen = null;
            Type callbackType = null;

            foreach (MethodInfo method in type.GetMethods(flags)
                .Where(candidate => string.Equals(candidate.Name, "TakeSnapshot", StringComparison.Ordinal))
                .OrderBy(candidate => candidate.GetParameters().Length))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2) continue;
                if (parameters[0].ParameterType != typeof(string)) continue;
                if (!typeof(Delegate).IsAssignableFrom(parameters[1].ParameterType)) continue;

                Type candidateType = parameters[1].ParameterType;
                MethodInfo signature = candidateType.GetMethod("Invoke");
                if (signature == null) continue;

                ParameterInfo[] callbackParameters = signature.GetParameters();
                if (callbackParameters.Length != 2) continue;
                if (callbackParameters[0].ParameterType != typeof(string)) continue;
                if (callbackParameters[1].ParameterType != typeof(bool)) continue;

                chosen = method;
                callbackType = candidateType;
                break;
            }

            if (chosen == null)
            {
                error = "the installed " + PackageName + " version exposes no TakeSnapshot(string, Action<string,bool>) "
                    + "overload; found on " + type.FullName + ". Capture the snapshot from Window > Analysis > Memory Profiler.";
                return false;
            }

            var pending = new SnapshotRequest();
            Delegate callback;
            try
            {
                callback = callbackType == typeof(Action<string, bool>)
                    ? (Delegate)new Action<string, bool>(pending.OnFinished)
                    : Delegate.CreateDelegate(
                        callbackType,
                        pending,
                        typeof(SnapshotRequest).GetMethod(
                            "OnFinished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
            }
            catch (Exception exception)
            {
                error = "could not bind the snapshot completion callback ("
                    + callbackType.Name + "): " + exception.GetBaseException().Message;
                return false;
            }

            ParameterInfo[] all = chosen.GetParameters();
            var arguments = new object[all.Length];
            arguments[0] = path;
            arguments[1] = callback;
            for (int index = 2; index < all.Length; index++)
            {
                // Let Unity's own defaults supply capture flags and the like.
                arguments[index] = Type.Missing;
            }

            try
            {
                chosen.Invoke(
                    null,
                    BindingFlags.OptionalParamBinding | BindingFlags.InvokeMethod
                        | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    arguments,
                    CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                error = "MemoryProfiler.TakeSnapshot threw: " + exception.GetBaseException().Message;
                return false;
            }

            request = pending;
            return true;
        }

        // ---- files -------------------------------------------------------------------------

        internal static JObject DescribeFile(string path)
        {
            var file = new FileInfo(path);
            long bytes = file.Exists ? file.Length : 0L;
            return new JObject
            {
                ["path"] = path,
                ["exists"] = file.Exists,
                ["sizeBytes"] = bytes,
                ["sizeMb"] = Math.Round(bytes / 1048576.0, 3),
                ["createdUtc"] = file.Exists
                    ? file.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture)
                    : null,
                ["modifiedUtc"] = file.Exists
                    ? file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)
                    : null
            };
        }

        /// <summary>Directories searched when the caller does not name one.</summary>
        internal static string[] DefaultSearchDirectories()
        {
            return new[]
            {
                DefaultSnapshotDirectory,
                Path.Combine(CucliPaths.ProjectRoot, "MemoryCaptures")
            };
        }

        internal static List<string> FindSnapshots(string searchPath, out JArray searchedDirectories)
        {
            var directories = new List<string>();
            if (string.IsNullOrWhiteSpace(searchPath))
            {
                directories.AddRange(DefaultSearchDirectories());
            }
            else
            {
                string candidate = searchPath.Trim();
                if (!Path.IsPathRooted(candidate))
                {
                    candidate = Path.Combine(CucliPaths.ProjectRoot, candidate);
                }

                if (File.Exists(candidate))
                {
                    directories.Add(Path.GetDirectoryName(candidate));
                }
                else
                {
                    directories.Add(candidate);
                }
            }

            searchedDirectories = new JArray();
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string directory in directories)
            {
                searchedDirectories.Add(directory);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;

                string[] found;
                try
                {
                    found = Directory.GetFiles(directory, "*.snap", SearchOption.TopDirectoryOnly);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string file in found)
                {
                    if (seen.Add(file)) files.Add(file);
                }
            }

            files.Sort((left, right) =>
                string.Compare(
                    new FileInfo(right).LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                    new FileInfo(left).LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                    StringComparison.Ordinal));
            return files;
        }

        /// <summary>
        /// Compares two snapshot files.
        ///
        /// This is a <b>file-level</b> comparison - size and timestamps only. A real object-level diff
        /// needs the Memory Profiler package's own analysis window, and pretending otherwise would be
        /// worse than saying so.
        /// </summary>
        internal static JObject Compare(string pathA, string pathB)
        {
            JObject a = DescribeFile(pathA);
            JObject b = DescribeFile(pathB);

            long deltaBytes = b["sizeBytes"].Value<long>() - a["sizeBytes"].Value<long>();

            double deltaSeconds = 0d;
            if (a["createdUtc"] != null && b["createdUtc"] != null)
            {
                DateTime createdA;
                DateTime createdB;
                if (DateTime.TryParse(a["createdUtc"].Value<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out createdA)
                    && DateTime.TryParse(b["createdUtc"].Value<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out createdB))
                {
                    deltaSeconds = Math.Round((createdB - createdA).TotalSeconds, 1);
                }
            }

            return new JObject
            {
                ["snapshotA"] = a,
                ["snapshotB"] = b,
                ["delta"] = new JObject
                {
                    ["sizeDeltaBytes"] = deltaBytes,
                    ["sizeDeltaMb"] = Math.Round(deltaBytes / 1048576.0, 3),
                    ["timeDeltaSeconds"] = deltaSeconds
                },
                ["comparisonLevel"] = "file_metadata_only",
                ["note"] = "只比较了文件级元数据（大小与时间戳），没有做对象级 diff。"
                    + "要看具体是哪类对象增长，请用 Window > Analysis > Memory Profiler 打开这两个 .snap 做对比。"
            };
        }
    }
}
