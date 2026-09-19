using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
// ProfilerRecorderHandle lives in LowLevel.Unsafe and ProfilerRecorderDescription in LowLevel;
// Unity.Profiling alone does not expose them.
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Profiler session state, profiler areas and counter enumeration.
    ///
    /// Two things here are deliberate rather than incidental:
    ///
    /// 1. <b>A session is reversible.</b> <c>Profiler.logFile</c>, <c>enableBinaryLog</c> and
    ///    <c>enableAllocationCallstacks</c> persist for the rest of the Editor session once set, so a
    ///    CLI sampling run that does not put them back silently changes what the NEXT run records -
    ///    including a run a human starts from the Profiler window. The pre-start values are kept in
    ///    <see cref="SessionState"/> so they also survive a domain reload.
    ///
    /// 2. <b>Counter output is paged and scannable.</b> Enumerating a whole category is inherently
    ///    wide, so the scan is capped (<c>maxCounters</c>), the result is paged (<c>pageSize</c>, small
    ///    by default) and the response always states what was scanned versus what was returned.
    /// </summary>
    internal static class ProfilerSupport
    {
        private const string SavedStateKey = "CustomUnityCli.Profiler.SavedState";

        // ---- session state ----------------------------------------------------------------

        internal sealed class SessionStateSnapshot
        {
            internal bool Enabled;
            internal string LogFile;
            internal bool EnableBinaryLog;
            internal bool EnableAllocationCallstacks;
        }

        internal static SessionStateSnapshot CaptureState()
        {
            return new SessionStateSnapshot
            {
                Enabled = Profiler.enabled,
                LogFile = Profiler.logFile,
                EnableBinaryLog = Profiler.enableBinaryLog,
                EnableAllocationCallstacks = Profiler.enableAllocationCallstacks
            };
        }

        /// <summary>
        /// Records the pre-start state. Only the FIRST start records it, so repeated or nested starts
        /// still restore the settings the human actually had.
        /// </summary>
        internal static void SaveStateIfAbsent()
        {
            if (HasSavedState) return;
            SessionStateSnapshot state = CaptureState();
            var payload = new JObject
            {
                ["enabled"] = state.Enabled,
                ["logFile"] = state.LogFile ?? string.Empty,
                ["enableBinaryLog"] = state.EnableBinaryLog,
                ["enableAllocationCallstacks"] = state.EnableAllocationCallstacks
            };
            SessionState.SetString(SavedStateKey, payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        internal static bool HasSavedState
        {
            get { return !string.IsNullOrEmpty(SessionState.GetString(SavedStateKey, string.Empty)); }
        }

        /// <summary>Restores the recorded state and clears the slot. Null when nothing was recorded.</summary>
        internal static SessionStateSnapshot RestoreStateAndClear()
        {
            string raw = SessionState.GetString(SavedStateKey, string.Empty);
            SessionState.SetString(SavedStateKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return null;

            JObject payload;
            try
            {
                payload = JObject.Parse(raw);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }

            var state = new SessionStateSnapshot
            {
                Enabled = payload["enabled"] != null && payload["enabled"].Value<bool>(),
                LogFile = payload["logFile"] == null ? string.Empty : payload["logFile"].Value<string>(),
                EnableBinaryLog = payload["enableBinaryLog"] != null && payload["enableBinaryLog"].Value<bool>(),
                EnableAllocationCallstacks = payload["enableAllocationCallstacks"] != null
                    && payload["enableAllocationCallstacks"].Value<bool>()
            };

            // Restore in the same order start() applies them: log target first, then the flags that
            // make it active, then enabled last.
            Profiler.enableBinaryLog = state.EnableBinaryLog;
            Profiler.logFile = string.IsNullOrEmpty(state.LogFile) ? null : state.LogFile;
            Profiler.enableAllocationCallstacks = state.EnableAllocationCallstacks;
            Profiler.enabled = state.Enabled;
            return state;
        }

        /// <summary>True while a binary log is genuinely being written.</summary>
        internal static bool IsRecording
        {
            get
            {
                return Profiler.enabled
                    && Profiler.enableBinaryLog
                    && !string.IsNullOrEmpty(Profiler.logFile);
            }
        }

        internal static JObject DescribeSession()
        {
            return new JObject
            {
                ["enabled"] = Profiler.enabled,
                ["recording"] = IsRecording,
                ["logFile"] = Profiler.logFile ?? string.Empty,
                ["allocationCallstacks"] = Profiler.enableAllocationCallstacks,
                ["hasSavedState"] = HasSavedState
            };
        }

        // ---- areas (ProfilerArea, NOT ProfilerCategory) -------------------------------------

        internal static string[] AreaNames()
        {
            return Enum.GetNames(typeof(ProfilerArea));
        }

        internal static bool TryResolveArea(string name, out ProfilerArea area)
        {
            area = default(ProfilerArea);
            if (string.IsNullOrWhiteSpace(name)) return false;

            string trimmed = name.Trim();
            foreach (string candidate in AreaNames())
            {
                if (!string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase)) continue;
                area = (ProfilerArea)Enum.Parse(typeof(ProfilerArea), candidate);
                return true;
            }

            return false;
        }

        internal static JObject DescribeAreas()
        {
            var map = new JObject();
            foreach (string name in AreaNames())
            {
                map[name] = GetAreaEnabled((ProfilerArea)Enum.Parse(typeof(ProfilerArea), name));
            }

            return map;
        }

        // GetAreaEnabled / SetAreaEnabled are reached by reflection on purpose: they are the only two
        // members of UnityEngine.Profiling.Profiler whose availability has moved between Unity
        // versions, and a missing member would otherwise be a compile error for the whole assembly
        // rather than a graceful degradation for one action.
        internal static bool GetAreaEnabled(ProfilerArea area)
        {
            object value;
            string error;
            if (CucliOptional.TryInvokeStatic(typeof(Profiler), "GetAreaEnabled",
                    new object[] { area }, out value, out error) && value is bool)
            {
                return (bool)value;
            }

            return false;
        }

        internal static bool TrySetAreaEnabled(ProfilerArea area, bool enabled, out string error)
        {
            object ignored;
            return CucliOptional.TryInvokeStatic(typeof(Profiler), "SetAreaEnabled",
                new object[] { area, enabled }, out ignored, out error);
        }

        // ---- categories (ProfilerCategory, NOT ProfilerArea) --------------------------------

        /// <summary>
        /// Resolves a category name against the real statics of <see cref="ProfilerCategory"/>.
        ///
        /// Resolving by reflection rather than a hardcoded table is what keeps this compiling and
        /// working across Unity versions: categories have been added over time, and a name this
        /// Editor does not have should be reported as such instead of breaking the build.
        /// </summary>
        internal static bool TryResolveCategory(
            string name,
            out ProfilerCategory category,
            out string error)
        {
            category = default(ProfilerCategory);
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "a profiler category name is required.";
                return false;
            }

            string wanted = NormalizeCategoryName(name);
            var available = new List<string>();

            foreach (KeyValuePair<string, ProfilerCategory> pair in CategoryTable())
            {
                available.Add(pair.Key);
                if (!string.Equals(pair.Key, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                category = pair.Value;
                return true;
            }

            available.Sort(StringComparer.OrdinalIgnoreCase);
            error = "'" + name.Trim() + "' is not a profiler category in Unity " + Application.unityVersion
                + ". Valid: " + string.Join(", ", available) + ".";
            return false;
        }

        internal static string[] CategoryNames()
        {
            var names = CategoryTable().Keys.ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        /// <summary>Aliases the CLI accepts on top of the raw static names.</summary>
        private static string NormalizeCategoryName(string name)
        {
            string trimmed = name.Trim();
            if (string.Equals(trimmed, "render", StringComparison.OrdinalIgnoreCase)) return "Render";
            if (string.Equals(trimmed, "rendering", StringComparison.OrdinalIgnoreCase)) return "Render";
            if (string.Equals(trimmed, "ui", StringComparison.OrdinalIgnoreCase)) return "Gui";
            if (string.Equals(trimmed, "gui", StringComparison.OrdinalIgnoreCase)) return "Gui";
            if (string.Equals(trimmed, "ai", StringComparison.OrdinalIgnoreCase)) return "AI";
            if (string.Equals(trimmed, "vr", StringComparison.OrdinalIgnoreCase)) return "Vr";
            if (string.Equals(trimmed, "fileio", StringComparison.OrdinalIgnoreCase)) return "FileIO";
            if (string.Equals(trimmed, "physics2d", StringComparison.OrdinalIgnoreCase)) return "Physics2D";
            return trimmed;
        }

        private static Dictionary<string, ProfilerCategory> s_categoryTable;

        private static Dictionary<string, ProfilerCategory> CategoryTable()
        {
            if (s_categoryTable != null) return s_categoryTable;

            var table = new Dictionary<string, ProfilerCategory>(StringComparer.OrdinalIgnoreCase);
            Type type = typeof(ProfilerCategory);
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

            try
            {
                foreach (System.Reflection.PropertyInfo property in type.GetProperties(flags))
                {
                    if (property.PropertyType != type || property.GetIndexParameters().Length != 0) continue;
                    try
                    {
                        object value = property.GetValue(null, null);
                        if (value is ProfilerCategory)
                        {
                            table[property.Name] = (ProfilerCategory)value;
                        }
                    }
                    catch (Exception)
                    {
                        // A category that throws on read is simply not offered.
                    }
                }

                foreach (System.Reflection.FieldInfo field in type.GetFields(flags))
                {
                    if (field.FieldType != type) continue;
                    try
                    {
                        object value = field.GetValue(null);
                        if (value is ProfilerCategory && !table.ContainsKey(field.Name))
                        {
                            table[field.Name] = (ProfilerCategory)value;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }

            s_categoryTable = table;
            return s_categoryTable;
        }

        // ---- counters ----------------------------------------------------------------------

        internal sealed class CounterReading
        {
            internal string Name;
            internal long Value;
            internal bool Valid;
            internal string Unit;
            /// <summary>Carried from the enumeration so reading a value never re-scans the handle list.</summary>
            internal ProfilerRecorderHandle Handle;
        }

        internal sealed class CounterScan
        {
            internal List<CounterReading> Readings = new List<CounterReading>();
            internal int AvailableInCategory;
            internal int Matched;
            internal int Scanned;
            internal bool ScanTruncated;
        }

        /// <summary>
        /// Reads counters for one category.
        ///
        /// The work is bounded twice: <paramref name="nameFilters"/> narrows what is measured, and
        /// <paramref name="maxCounters"/> caps how many recorders are created even when the caller
        /// omits the filter. Both bounds are reported back, so a truncated answer never looks like a
        /// complete one.
        /// </summary>
        internal static CounterScan ReadCounters(
            ProfilerCategory category,
            string[] nameFilters,
            int maxCounters)
        {
            var scan = new CounterScan();
            var handles = new List<ProfilerRecorderHandle>();

            try
            {
                // The category-filtered overload does not exist on every supported Unity version
                // (2022.3 only exposes the parameterless one), so the filter happens below.
                ProfilerRecorderHandle.GetAvailable(handles);
            }
            catch (Exception)
            {
                return scan;
            }

            int availableInCategory = 0;

            var wanted = new List<CounterReading>();
            foreach (ProfilerRecorderHandle handle in handles)
            {
                string name;
                ProfilerCategory handleCategory;
                try
                {
                    ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                    name = description.Name;
                    handleCategory = description.Category;
                }
                catch (Exception)
                {
                    continue;
                }

                if (!string.Equals(
                        handleCategory.ToString(), category.ToString(), StringComparison.Ordinal))
                {
                    continue;
                }

                availableInCategory++;

                if (string.IsNullOrEmpty(name)) continue;
                if (nameFilters != null && nameFilters.Length > 0
                    && !nameFilters.Any(filter => name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                wanted.Add(new CounterReading { Name = name, Handle = handle });
            }

            scan.AvailableInCategory = availableInCategory;
            scan.Matched = wanted.Count;
            wanted.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));

            if (wanted.Count > maxCounters)
            {
                wanted = wanted.Take(maxCounters).ToList();
                scan.ScanTruncated = true;
            }

            // One recorder per handle: a recorder has to be created before the frame it reports on,
            // which is why the caller settles for a couple of player-loop updates first.
            foreach (CounterReading reading in wanted)
            {
                try
                {
                    // StartNew has no ProfilerRecorderHandle overload on every supported version, so the
                    // recorder is created from the category and stat name that GetDescription just gave us.
                    ProfilerRecorder recorder = ProfilerRecorder.StartNew(category, reading.Name, 1);
                    try
                    {
                        reading.Value = recorder.LastValue;
                        reading.Valid = recorder.Valid;
                        reading.Unit = recorder.UnitType.ToString();
                    }
                    finally
                    {
                        recorder.Dispose();
                    }
                }
                catch (Exception)
                {
                    reading.Valid = false;
                    reading.Unit = "unknown";
                }

                scan.Readings.Add(reading);
                scan.Scanned++;
            }

            return scan;
        }

        internal static JObject ReadingToJson(CounterReading reading)
        {
            return new JObject
            {
                ["name"] = reading.Name,
                ["value"] = reading.Value,
                ["valid"] = reading.Valid,
                ["unit"] = reading.Unit
            };
        }

        /// <summary>
        /// Queues a player-loop update and waits for two update ticks.
        ///
        /// A ProfilerRecorder only produces a value after the player loop has run at least once, so
        /// reading immediately returns zeros or invalid readings. The caller drives this from the
        /// deferred path rather than blocking the Editor.
        /// </summary>
        internal static void SettleForCounters(Action complete, Func<bool> cancelled)
        {
            EditorApplication.QueuePlayerLoopUpdate();
            int remaining = 2;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (cancelled != null && cancelled())
                {
                    EditorApplication.update -= tick;
                    return;
                }

                remaining--;
                if (remaining > 0) return;
                EditorApplication.update -= tick;
                complete();
            };
            EditorApplication.update += tick;
        }
    }
}
