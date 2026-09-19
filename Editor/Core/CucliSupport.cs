using System;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The supported-Editor boundary, in code rather than only in the README.
    ///
    /// This package targets the **Unity 2022** generation. A few command implementations carry
    /// <c>#if UNITY_6000_0_OR_NEWER</c> guards so the assembly still *compiles* in a newer Editor,
    /// which has an unfortunate side effect: a Unity 6 user gets a package that builds, starts its
    /// worker and then behaves in ways nobody has ever tested - with no signal at all that they are
    /// outside the supported range.
    ///
    /// So the boundary is announced instead of merely documented: the worker logs one clear warning
    /// on startup, and the heartbeat carries the flag so 'cucli status' and 'cucli doctor' can
    /// report it. Compiling is not the same as being supported, and this type is the difference.
    /// </summary>
    internal static class CucliSupport
    {
        /// <summary>The only verified target: the Unity 2022.3 LTS line.</summary>
        internal const string SupportedTarget = "2022.3";

        internal const string SupportedTargetAdvice =
            "Projects on Unity 6 should use Unity CLI, the official command-line interface for that "
            + "generation: https://docs.unity.com/en-us/unity-cli/unity-cli-reference";

        internal static int SupportedMajor = 2022;
        internal static int SupportedMinor = 3;

        /// <summary>
        /// <see cref="SessionState"/> key for the boundary warning: survives domain reloads, which is
        /// what makes it once per Editor session, but is cleared when the Editor restarts.
        /// </summary>
        private const string WarnedKey = "CustomUnityCli.WarnedUnsupportedEditor";

        private static bool s_resolved;
        private static bool s_isSupported;
        private static string s_reason;

        /// <summary>True only on the verified 2022.3 line.</summary>
        internal static bool IsSupportedEditor
        {
            get
            {
                Resolve();
                return s_isSupported;
            }
        }

        /// <summary>
        /// Null when supported; otherwise one sentence naming the running version and why it is out
        /// of scope. Suitable for a log line, a heartbeat field or a doctor check detail.
        /// </summary>
        internal static string UnsupportedReason
        {
            get
            {
                Resolve();
                return s_reason;
            }
        }

        /// <summary>The running Editor version, e.g. <c>2022.3.62f2c1</c>.</summary>
        internal static string EditorVersion => Application.unityVersion ?? string.Empty;

        private static void Resolve()
        {
            if (s_resolved) return;
            s_resolved = true;

            int major;
            int minor;
            if (!TryParseMajorMinor(EditorVersion, out major, out minor))
            {
                s_isSupported = false;
                s_reason = "The Editor version '" + EditorVersion + "' could not be parsed, so this "
                    + "package cannot confirm it is running on its supported target (" + SupportedTarget + ").";
                return;
            }

            if (major == SupportedMajor && minor == SupportedMinor)
            {
                s_isSupported = true;
                s_reason = null;
                return;
            }

            s_isSupported = false;
            s_reason = major > SupportedMajor
                ? "This Editor is Unity " + major + "." + minor + ", which is newer than the verified "
                    + "target (" + SupportedTarget + "). Nothing in this package has been tested on it. "
                    + SupportedTargetAdvice
                : "This Editor is Unity " + major + "." + minor + ", which is older than the verified "
                    + "target (" + SupportedTarget + "). The 2021 LTS line is untested. "
                    + "Treat " + SupportedTarget + " as the supported target.";
        }

        /// <summary>
        /// Reads the leading <c>major.minor</c> out of a Unity version string. Unity reports versions
        /// like <c>2022.3.62f2c1</c> and, for some builds, <c>6000.0.23f1</c>.
        /// </summary>
        private static bool TryParseMajorMinor(string version, out int major, out int minor)
        {
            major = 0;
            minor = 0;
            if (string.IsNullOrWhiteSpace(version)) return false;

            string[] parts = version.Split('.');
            if (parts.Length < 2) return false;

            if (!int.TryParse(parts[0], out major)) return false;

            // The minor segment can carry a suffix on some builds ("3f1"); take the leading digits.
            string minorText = parts[1];
            int length = 0;
            while (length < minorText.Length && char.IsDigit(minorText[length])) length++;
            if (length == 0) return false;

            return int.TryParse(minorText.Substring(0, length), out minor);
        }

        /// <summary>
        /// Logs the boundary once per Editor session. Deliberately a warning rather than an error:
        /// the package still works well enough to be useful, it is simply not verified, and refusing
        /// to start would be a worse outcome than saying so plainly.
        ///
        /// Session-scoped rather than domain-scoped on purpose. A domain reload happens on every
        /// script compile, so warning per reload would repeat the message all day on an unsupported
        /// Editor and train the reader to ignore precisely the notice that matters. The README
        /// promises "a single warning", and this is what makes that true.
        /// </summary>
        internal static void WarnIfUnsupported()
        {
            if (SessionState.GetBool(WarnedKey, false)) return;

            string reason = UnsupportedReason;
            if (reason == null) return;

            SessionState.SetBool(WarnedKey, true);
            Debug.LogWarning("[cucli] Unsupported Editor for com.igp.custom-unity-cli. " + reason);
        }

        internal static void ResetCache()
        {
            s_resolved = false;
            s_isSupported = false;
            s_reason = null;
        }
    }
}
