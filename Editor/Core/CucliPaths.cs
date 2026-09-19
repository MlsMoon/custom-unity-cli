using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Every file the worker touches lives under <c>&lt;project&gt;/Library/CustomUnityCli</c>.
    /// Nothing is written into the package folder at runtime, so the package stays read-only and
    /// upgradeable, and the whole runtime state can be deleted at any time without side effects.
    /// </summary>
    public static class CucliPaths
    {
        public const string WorkerVersion = "0.1.0";
        public const int ProtocolVersion = 1;

        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        public static string Root => Path.Combine(ProjectRoot, "Library", "CustomUnityCli");

        public static string Worker => Path.Combine(Root, "worker");
        public static string Heartbeat => Path.Combine(Worker, "heartbeat.json");
        public static string Commands => Path.Combine(Worker, "commands.json");

        public static string Console => Path.Combine(Root, "console");
        public static string ConsoleEntries => Path.Combine(Console, "entries.ndjson");
        public static string ConsoleArchive => Path.Combine(Console, "entries.1.ndjson");

        public static string Jobs => Path.Combine(Root, "jobs");
        public static string Control => Path.Combine(Jobs, "control");
        public static string Queued => Path.Combine(Jobs, "queued");
        public static string Running => Path.Combine(Jobs, "running");
        public static string Waiting => Path.Combine(Jobs, "waiting");
        public static string Succeeded => Path.Combine(Jobs, "succeeded");
        public static string Failed => Path.Combine(Jobs, "failed");
        public static string Cancelled => Path.Combine(Jobs, "cancelled");
        public static string CancelRequests => Path.Combine(Jobs, "cancel-requests");

        /// <summary>
        /// Sidecar holding the real result of a reload-capable job while it sits in <c>waiting</c>.
        /// The <c>.pending</c> extension keeps it out of the <c>*.json</c> job scans.
        /// </summary>
        public static string PendingResultPath(string jobId)
        {
            return Path.Combine(Waiting, jobId + ".pending");
        }

        public static string CliLog => Path.Combine(Root, "cli-log.ndjson");

        // There is deliberately no package-root resolver here. CucliPackage owns that question: it
        // resolves the root through three layered probes (PackageInfo, an AssetDatabase walk-up, then
        // a manifest-verified candidate list) and validates every answer against package.json. A
        // second, weaker resolver in this class used to exist and could disagree with it, which for a
        // "is this path inside the package?" guard would mean project code silently landing in the
        // published repository. Use CucliPackage.Root / CucliPackage.IsInsidePackage.

        public static void EnsureDirectories()
        {
            foreach (string path in new[]
            {
                Worker, Console, Jobs, Control, Queued, Running, Waiting,
                Succeeded, Failed, Cancelled, CancelRequests
            })
            {
                Directory.CreateDirectory(path);
            }
        }

        /// <summary>Trims stale terminal job results so long sessions do not grow without bound.</summary>
        public static void PruneTerminalJobs(int keepPerState = 200)
        {
            foreach (string directory in new[] { Succeeded, Cancelled })
            {
                Prune(directory, keepPerState);
            }
        }

        private static void Prune(string directory, int keep)
        {
            try
            {
                var files = new DirectoryInfo(directory).GetFiles("*.json");
                if (files.Length <= keep) return;
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int index = keep; index < files.Length; index++)
                {
                    try { files[index].Delete(); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (DirectoryNotFoundException) { }
        }

        /// <summary>
        /// Cached so that <see cref="WriteAtomic"/> can be called from the heartbeat timer thread.
        /// <c>Application.platform</c> is a main-thread API, so it is read exactly once, eagerly,
        /// from <see cref="WarmUp"/> during the worker's static constructor.
        /// </summary>
        private static bool s_isWindowsEditor;

        private static bool s_warmedUp;

        /// <summary>Must be called from the main thread before any background writer starts.</summary>
        public static void WarmUp()
        {
            if (s_warmedUp) return;
            s_isWindowsEditor = Application.platform == RuntimePlatform.WindowsEditor;
            s_warmedUp = true;
        }

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existing, string destination, int flags);

        /// <summary>
        /// Same-volume atomic replace. Readers that opened the previous file with
        /// <c>FileShare.Delete</c> keep reading it; nobody ever observes a half-written file.
        /// On failure the previous file is preserved - we never degrade to delete-then-write.
        /// </summary>
        public static void WriteAtomic(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, content, CucliJson.Utf8NoBom);
                if (s_isWindowsEditor)
                {
                    if (!MoveFileEx(temporary, path, 0x1 | 0x8))
                    {
                        int code = Marshal.GetLastWin32Error();
                        throw new IOException(
                            "Atomic replace failed for " + path + " (Win32=" + code + ": " +
                            new Win32Exception(code).Message + ").");
                    }
                }
                else if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
