using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli console ...</c> - read and clear the captured Editor console, aligned with the MCP
    /// <c>read_console</c> tool.
    ///
    /// The data source is the NDJSON file the worker already maintains
    /// (<c>Library/CustomUnityCli/console/entries.ndjson</c>, written by
    /// <see cref="CucliConsoleCapture"/>). It is read from the tail only: a session can produce
    /// hundreds of megabytes of log, and a CLI that reads the whole file on every poll is unusable.
    ///
    /// <c>clear</c> does not truncate the file - it appends a marker line and every reader drops
    /// everything before the newest marker, which keeps clearing cheap and crash safe.
    /// </summary>
    internal sealed class ConsoleCommand : CucliCommand
    {
        /// <summary>Only this much of the end of the capture file is parsed.</summary>
        private const long TailBytes = 4L * 1024L * 1024L;

        private const int DefaultPageSize = 50;

        private static readonly string[] KnownLevels =
        {
            "log", "warning", "error", "assert", "exception"
        };

        public override string Name => "console";

        public override string Summary =>
            "Read the captured Editor console (filter by level, regex and time) or clear it.";

        public override CucliActionInfo[] Actions => Declare(
            "get", "Read console entries. Paged, newest-last, with a bounded default page size.",
            "clear", "Append a clear marker so every earlier entry is treated as cleared.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "editor.console.read" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""get"", ""clear""], ""default"": ""get"" },
    ""level"": { ""type"": ""string"", ""description"": ""log|warning|error|assert|exception (also repeatable/comma separated)."" },
    ""levels"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""types"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Alias of 'level' (MCP naming); 'all' expands to log/warning/error/assert/exception."" },
    ""count"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""description"": ""Upper bound for one response; used as the page size unless pageSize is given."" },
    ""pageSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50 },
    ""page_size"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
    ""cursor"": { ""type"": ""string"", ""description"": ""Opaque paging cursor from the previous response, or a plain offset."" },
    ""pageNumber"": { ""type"": ""integer"", ""minimum"": 1 },
    ""page_number"": { ""type"": ""integer"", ""minimum"": 1 },
    ""grep"": { ""type"": ""string"", ""description"": ""Regular expression matched against message and stack trace."" },
    ""since"": { ""type"": ""string"", ""description"": ""ISO-8601 time; entries older than this are dropped."" },
    ""includeStackTraces"": { ""type"": ""boolean"", ""default"": false },
    ""include_stacktraces"": { ""type"": ""boolean"", ""default"": false },
    ""include_stack_traces"": { ""type"": ""boolean"", ""default"": false }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "get");
            switch (action)
            {
                case "get": return Get(context);
                case "clear": return Clear();
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        private static CucliCommandResult Get(CucliCommandContext context)
        {
            bool includeStackTraces = context.Input.GetBool(
                "includeStackTraces",
                context.Input.GetBool("include_stacktraces", context.Input.GetBool("include_stack_traces", false)));

            HashSet<string> levels = ResolveLevels(context);
            Regex pattern = null;
            string grep = context.Input.GetString("grep");
            if (!string.IsNullOrWhiteSpace(grep))
            {
                try
                {
                    pattern = new Regex(grep, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException exception)
                {
                    return CucliCommandResult.Invalid("grep", "正则表达式无效：" + exception.Message);
                }
            }

            DateTime since = DateTime.MinValue;
            bool filterSince = false;
            string sinceText = context.Input.GetString("since");
            if (!string.IsNullOrWhiteSpace(sinceText))
            {
                if (!DateTime.TryParse(
                        sinceText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out since))
                {
                    return CucliCommandResult.Invalid(
                        "since", "'" + sinceText + "' 不是合法时间。示例：2026-01-31T08:00:00Z");
                }

                filterSince = true;
            }

            string path = CucliPaths.ConsoleEntries;
            bool archiveFallback = false;
            if (!File.Exists(path))
            {
                if (File.Exists(CucliPaths.ConsoleArchive))
                {
                    path = CucliPaths.ConsoleArchive;
                    archiveFallback = true;
                }
                else
                {
                    return CucliCommandResult.Ok(
                        CucliPaging.Envelope(
                            new CucliPage<JObject>(),
                            new JArray(),
                            new JObject
                            {
                                ["path"] = CucliPaths.ConsoleEntries,
                                ["exists"] = false,
                                ["levels"] = new JObject()
                            }),
                        "控制台捕获文件还不存在（尚未产生任何日志）。");
                }
            }

            string text;
            long bytesRead;
            bool windowed;
            string readError;
            if (!TryReadTail(path, TailBytes, out text, out bytesRead, out windowed, out readError))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    readError ?? "读取控制台捕获文件失败。",
                    new { path });
            }

            if (archiveFallback)
            {
                context.Warn("entries.ndjson 不存在，已回退读取轮转归档文件 " + path + "。");
            }

            string[] lines = text.Split('\n');
            int start = 0;
            if (windowed && lines.Length > 0) start = 1; // the first line is probably cut in half

            var parsed = new List<ConsoleEntry>();
            int scanned = 0;
            int clearIndex = -1;
            for (int index = start; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.Length == 0) continue;
                scanned++;

                JObject entry;
                try
                {
                    entry = JObject.Parse(line.Trim());
                }
                catch (JsonException)
                {
                    continue;
                }

                string level = entry["level"] == null ? "log" : entry["level"].ToString();
                if (string.Equals(level, "clear", StringComparison.OrdinalIgnoreCase))
                {
                    // Remember the newest marker; everything before it counts as cleared.
                    clearIndex = parsed.Count;
                    continue;
                }

                parsed.Add(new ConsoleEntry
                {
                    Sequence = entry["sequence"] == null ? 0L : entry["sequence"].ToObject<long>(),
                    Timestamp = entry["timestampUtc"] == null ? null : entry["timestampUtc"].ToString(),
                    SessionId = entry["sessionId"] == null ? null : entry["sessionId"].ToString(),
                    Level = string.IsNullOrWhiteSpace(level) ? "log" : level.ToLowerInvariant(),
                    Message = entry["message"] == null ? string.Empty : entry["message"].ToString(),
                    StackTrace = entry["stackTrace"] == null ? null : entry["stackTrace"].ToString()
                });
            }

            var kept = new List<ConsoleEntry>();
            for (int index = Math.Max(0, clearIndex); index < parsed.Count; index++) kept.Add(parsed[index]);

            var levelCounts = new JObject();
            foreach (string name in KnownLevels) levelCounts[name] = 0;
            foreach (ConsoleEntry entry in kept)
            {
                JToken current = levelCounts[entry.Level];
                levelCounts[entry.Level] = (current == null ? 0L : current.ToObject<long>()) + 1;
            }

            var items = new List<JObject>();
            var filtered = new List<ConsoleEntry>();
            foreach (ConsoleEntry entry in kept)
            {
                if (levels.Count > 0 && !LevelMatches(entry.Level, levels)) continue;

                if (filterSince)
                {
                    DateTime timestamp = ParseTimestamp(entry.Timestamp);
                    if (timestamp == DateTime.MinValue || timestamp < since) continue;
                }

                if (pattern != null
                    && !pattern.IsMatch(entry.Message ?? string.Empty)
                    && !pattern.IsMatch(entry.StackTrace ?? string.Empty))
                {
                    continue;
                }

                filtered.Add(entry);
            }

            foreach (ConsoleEntry entry in filtered)
            {
                var item = new JObject
                {
                    ["sequence"] = entry.Sequence,
                    ["timestamp"] = entry.Timestamp,
                    ["level"] = entry.Level,
                    ["message"] = includeStackTraces ? entry.Message : FirstLine(entry.Message),
                    ["sessionId"] = entry.SessionId
                };

                if (includeStackTraces) item["stackTrace"] = entry.StackTrace;
                items.Add(item);
            }

            int fallbackPageSize = ResolveFallbackPageSize(context);
            NormalizeCursor(context);
            CucliPage<JObject> page = CucliPaging.Slice(items, context.Input, fallbackPageSize);

            var source = new JObject
            {
                ["path"] = path,
                ["bytesRead"] = bytesRead,
                ["tailOnly"] = windowed,
                ["archiveFallback"] = archiveFallback,
                ["scannedLines"] = scanned,
                ["parsedEntries"] = parsed.Count,
                ["clearMarkerFound"] = clearIndex >= 0,
                ["entriesAfterClear"] = kept.Count,
                ["matchedEntries"] = filtered.Count
            };

            return CucliCommandResult.Ok(
                CucliPaging.Envelope(
                    page,
                    new JArray(page.Items.Cast<object>().ToArray()),
                    new JObject
                    {
                        ["source"] = source,
                        ["levelCounts"] = levelCounts,
                        ["levels"] = new JArray(levels.Cast<object>().ToArray()),
                        ["grep"] = grep,
                        ["since"] = filterSince ? since.ToString("O", CultureInfo.InvariantCulture) : null,
                        ["includeStackTraces"] = includeStackTraces,
                        ["tailBytes"] = TailBytes
                    }),
                "匹配 " + page.Total + " 条控制台日志（返回 " + page.Items.Count + " 条）。");
        }

        private static CucliCommandResult Clear()
        {
            CucliConsoleCapture.MarkCleared();
            return CucliCommandResult.Ok(new
            {
                cleared = true,
                markerPath = CucliPaths.ConsoleEntries,
                pendingBeforeClear = CucliConsoleCapture.PendingCount
            }, "已写入 clear marker；此前的日志条目对后续读取不再可见（文件本身没有被截断）。");
        }

        // ---------------------------------------------------------------- helpers

        private static bool TryReadTail(
            string path,
            long maxBytes,
            out string text,
            out long bytesRead,
            out bool windowed,
            out string error)
        {
            text = null;
            bytesRead = 0;
            windowed = false;
            error = null;

            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = stream.Length;
                    long start = Math.Max(0, length - maxBytes);
                    windowed = start > 0;
                    bytesRead = length - start;
                    if (bytesRead <= 0)
                    {
                        text = string.Empty;
                        return true;
                    }

                    stream.Seek(start, SeekOrigin.Begin);
                    var buffer = new byte[bytesRead];
                    int read = 0;
                    while (read < bytesRead)
                    {
                        int chunk = stream.Read(buffer, read, (int)(bytesRead - read));
                        if (chunk <= 0) break;
                        read += chunk;
                    }

                    text = Encoding.UTF8.GetString(buffer, 0, read);
                    return true;
                }
            }
            catch (IOException exception)
            {
                error = "读取控制台捕获文件失败：" + exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "读取控制台捕获文件失败：" + exception.Message;
                return false;
            }
        }

        private static HashSet<string> ResolveLevels(CucliCommandContext context)
        {
            var levels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddLevels(context, levels, context.Input.GetStringList("level"));
            AddLevels(context, levels, context.Input.GetStringList("levels"));
            AddLevels(context, levels, context.Input.GetStringList("types"));
            return levels;
        }

        private static void AddLevels(CucliCommandContext context, HashSet<string> levels, string[] values)
        {
            if (values == null) return;

            foreach (string raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string value = raw.Trim().ToLowerInvariant();
                switch (value)
                {
                    case "all":
                        foreach (string name in KnownLevels) levels.Add(name);
                        continue;
                    case "warn": value = "warning"; break;
                    case "info": value = "log"; break;
                    case "clear": continue;
                }

                if (Array.IndexOf(KnownLevels, value) < 0)
                {
                    context.Warn("未知日志级别 '" + raw + "' 已被忽略。可用："
                        + string.Join(", ", KnownLevels) + "。");
                    continue;
                }

                levels.Add(value);
            }
        }

        private static bool LevelMatches(string entryLevel, HashSet<string> filter)
        {
            if (filter.Count == 0) return true;
            if (filter.Contains(entryLevel)) return true;

            // Exceptions and asserts are also errors, exactly like the MCP reader treats them.
            if (string.Equals(entryLevel, "exception", StringComparison.OrdinalIgnoreCase)
                && filter.Contains("error"))
            {
                return true;
            }

            if (string.Equals(entryLevel, "assert", StringComparison.OrdinalIgnoreCase)
                && filter.Contains("error"))
            {
                return true;
            }

            return false;
        }

        private static int ResolveFallbackPageSize(CucliCommandContext context)
        {
            int count = context.Input.GetInt("count", 0);
            if (count <= 0) return DefaultPageSize;
            return Math.Min(count, CucliPaging.MaxPageSize);
        }

        /// <summary>
        /// The paging helper speaks opaque base64 cursors; callers of the console reader more often
        /// pass a plain offset, so both are accepted.
        /// </summary>
        private static void NormalizeCursor(CucliCommandContext context)
        {
            JToken token = context.Input.Token("cursor");
            if (token == null || token.Type == JTokenType.Null) return;

            int offset;
            if (token.Type == JTokenType.Integer)
            {
                offset = token.ToObject<int>();
            }
            else if (token.Type == JTokenType.String
                && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
            {
                // plain numeric cursor
            }
            else
            {
                return; // already an opaque cursor
            }

            context.Args["cursor"] = CucliPaging.EncodeCursor(Math.Max(0, offset));
        }

        private static DateTime ParseTimestamp(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DateTime.MinValue;
            DateTime parsed;
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out parsed)
                ? parsed
                : DateTime.MinValue;
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            int index = value.IndexOf('\n');
            return index < 0 ? value : value.Substring(0, index);
        }

        /// <summary>One parsed console line; kept tiny because the tail can hold thousands of them.</summary>
        private sealed class ConsoleEntry
        {
            public long Sequence;
            public string Timestamp;
            public string SessionId;
            public string Level;
            public string Message;
            public string StackTrace;
        }
    }
}
