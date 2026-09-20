using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The list of images this Editor session has produced, kept on disk rather than in memory.
    ///
    /// <c>cucli capture list</c> has to answer "what did I just make?" and the answer must survive the
    /// domain reload that a script edit triggers - otherwise the listing goes empty exactly when a
    /// caller is iterating. So the index is a file under <c>Library/CustomUnityCli/</c>, capped and
    /// rewritten atomically after every append.
    /// </summary>
    internal static class SessionImageIndex
    {
        private const int MaxEntries = 500;

        internal static string Path =>
            System.IO.Path.Combine(CucliPaths.Root, "capture", "session-images.json");

        /// <summary>Appends one produced file and returns the entry that was recorded.</summary>
        internal static JObject Append(string absolutePath, string kind)
        {
            var entry = new JObject
            {
                ["path"] = CaptureSupport.ToProjectRelative(absolutePath).Replace('\\', '/'),
                ["kind"] = kind,
                ["frame"] = Time.frameCount,
                ["time"] = Time.time,
                ["atUtc"] = DateTime.UtcNow.ToString("O"),
                ["bytes"] = SafeLength(absolutePath)
            };

            JArray entries = Read();
            entries.Add(entry);
            while (entries.Count > MaxEntries) entries.RemoveAt(0);

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                CucliPaths.WriteAtomic(
                    Path,
                    new JObject
                    {
                        ["schemaVersion"] = "cucli.session-images/v1",
                        ["updatedAtUtc"] = DateTime.UtcNow.ToString("O"),
                        ["count"] = entries.Count,
                        ["entries"] = entries
                    }.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine);
            }
            catch (Exception)
            {
                // A session index that cannot be written must never fail the capture that produced the file.
            }

            return entry;
        }

        /// <summary>Recorded entries, oldest first. Never throws: a corrupt index reads as empty.</summary>
        internal static JArray Read()
        {
            try
            {
                if (!File.Exists(Path)) return new JArray();
                JObject root = CucliJson.ParseObject(File.ReadAllText(Path, CucliJson.Utf8NoBom));
                return root["entries"] as JArray ?? new JArray();
            }
            catch (IOException)
            {
                return new JArray();
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return new JArray();
            }
            catch (UnauthorizedAccessException)
            {
                return new JArray();
            }
        }

        /// <summary>
        /// Entries whose file still exists, newest first. A capture folder that was deleted by hand
        /// should not keep showing up as if the evidence were still there.
        /// </summary>
        internal static JArray ReadExisting(int limit)
        {
            var existing = new JArray();
            string projectRoot = CucliPaths.ProjectRoot;

            foreach (JToken token in Read().Reverse())
            {
                var entry = token as JObject;
                if (entry == null) continue;
                string relative = entry["path"] == null ? null : entry["path"].Value<string>();
                if (string.IsNullOrWhiteSpace(relative)) continue;

                string absolute = System.IO.Path.Combine(
                    projectRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(absolute)) continue;

                existing.Add(entry);
                if (existing.Count >= Mathf.Max(1, limit)) break;
            }

            return existing;
        }

        private static long SafeLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
            catch (IOException)
            {
                return 0L;
            }
        }
    }
}
