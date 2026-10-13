using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Persists Editor console output as newline-delimited JSON so <c>cucli console</c> can read it
    /// with the Editor busy, mid-compile or even after Unity restarted.
    ///
    /// A <c>clear</c> marker is appended instead of truncating the file: the reader ignores
    /// everything before the newest marker, which keeps <c>clear</c> cheap and crash-safe.
    /// </summary>
    internal static class CucliConsoleCapture
    {
        private const long MaximumFileBytes = 20L * 1024L * 1024L;
        private static readonly string UserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static readonly ConcurrentQueue<CucliConsoleEntry> Pending =
            new ConcurrentQueue<CucliConsoleEntry>();

        private static string s_sessionId;
        private static long s_sequence;

        internal static void Initialize(string sessionId)
        {
            s_sessionId = sessionId;
            Application.logMessageReceivedThreaded -= OnLogMessage;
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        internal static void Shutdown()
        {
            Application.logMessageReceivedThreaded -= OnLogMessage;
            Flush();
        }

        internal static void Flush()
        {
            if (Pending.IsEmpty) return;
            try
            {
                RotateIfNeeded();
                using (var writer = new StreamWriter(CucliPaths.ConsoleEntries, true, CucliJson.Utf8NoBom))
                {
                    CucliConsoleEntry entry;
                    while (Pending.TryDequeue(out entry))
                    {
                        writer.WriteLine(CucliJson.SerializeCompact(entry));
                    }
                }
            }
            catch (IOException)
            {
                // Console capture must never destabilise the Editor.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>Appends a marker so every earlier entry is treated as cleared.</summary>
        internal static void MarkCleared()
        {
            Flush();
            var entry = new CucliConsoleEntry
            {
                Sequence = System.Threading.Interlocked.Increment(ref s_sequence),
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                SessionId = s_sessionId,
                Level = "clear",
                Message = string.Empty,
                StackTrace = string.Empty
            };

            try
            {
                using (var writer = new StreamWriter(CucliPaths.ConsoleEntries, true, CucliJson.Utf8NoBom))
                {
                    writer.WriteLine(CucliJson.SerializeCompact(entry));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        internal static int PendingCount => Pending.Count;

        internal static string SessionId => s_sessionId;

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            Pending.Enqueue(new CucliConsoleEntry
            {
                Sequence = System.Threading.Interlocked.Increment(ref s_sequence),
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                SessionId = s_sessionId,
                Level = NormalizeLevel(type),
                Message = Redact(message),
                StackTrace = Redact(stackTrace)
            });
        }

        private static string NormalizeLevel(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "error";
                case LogType.Assert: return "assert";
                case LogType.Warning: return "warning";
                case LogType.Exception: return "exception";
                default: return "log";
            }
        }

        private static void RotateIfNeeded()
        {
            var file = new FileInfo(CucliPaths.ConsoleEntries);
            if (!file.Exists || file.Length < MaximumFileBytes) return;
            if (File.Exists(CucliPaths.ConsoleArchive)) File.Delete(CucliPaths.ConsoleArchive);
            File.Move(CucliPaths.ConsoleEntries, CucliPaths.ConsoleArchive);
        }

        /// <summary>
        /// Removes the current user profile path and e-mail addresses before anything is written to
        /// disk, so captured logs stay safe to paste into an issue or a public repository.
        /// </summary>
        private static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string result = string.IsNullOrWhiteSpace(UserProfilePath)
                ? value
                : value.Replace(UserProfilePath, "<user>");
            // Avoid regex backtracking over long qualified names on the log-emitting thread.
            return result.IndexOf('@') < 0
                ? result
                : Regex.Replace(result, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "<redacted-email>");
        }
    }

    /// <summary>One console line as stored in <c>console/entries.ndjson</c>.</summary>
    internal sealed class CucliConsoleEntry
    {
        public long Sequence { get; set; }
        public string TimestampUtc { get; set; }
        public string SessionId { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
        public string StackTrace { get; set; }

        internal bool IsClearMarker
        {
            get { return string.Equals(Level, "clear", StringComparison.OrdinalIgnoreCase); }
        }

        internal DateTime ParsedTimestamp
        {
            get
            {
                DateTime parsed;
                return DateTime.TryParse(
                    TimestampUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out parsed)
                    ? parsed
                    : DateTime.MinValue;
            }
        }
    }
}
