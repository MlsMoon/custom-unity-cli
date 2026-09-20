using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Reflective access to Unity's internal Frame Debugger.
    ///
    /// The type is internal, so this cannot be a compile-time reference, and its home moved between
    /// versions (<c>UnityEditorInternal.FrameDebuggerUtility</c> in 2021-2022, a nested
    /// <c>FrameDebuggerInternal+FrameDebuggerUtility</c> in Unity 6). Everything is therefore probed
    /// once, cached, and reported honestly: when the shape is not there, the caller gets an error
    /// naming the types that were searched instead of a silently empty event list.
    /// </summary>
    internal static class ProfilerFrameDebugger
    {
        private static readonly string[] CandidateTypeNames =
        {
            "UnityEditorInternal.FrameDebuggerUtility",
            "UnityEditorInternal.FrameDebuggerInternal+FrameDebuggerUtility"
        };

        private static bool s_probed;
        private static Type s_type;
        private static string s_unavailableReason;

        /// <summary>Member names the frame debugger event struct has used across versions.</summary>
        private static readonly string[] EventMemberNames =
        {
            "name", "eventType", "gameObjectInstanceID", "shaderName", "passName",
            "rtName", "rtWidth", "rtHeight", "vertexCount", "indexCount",
            "instanceCount", "meshName", "eventName", "description"
        };

        private static Type ResolveType()
        {
            if (s_probed) return s_type;
            s_probed = true;

            foreach (string candidate in CandidateTypeNames)
            {
                Type resolved = CucliOptional.FindType(null, candidate);
                if (resolved == null) continue;

                // Touching the type forces its static constructor; a throw there means the whole
                // utility is unusable in this Editor, which is exactly what we want to report.
                try
                {
                    resolved.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    s_type = resolved;
                    return s_type;
                }
                catch (Exception exception)
                {
                    s_unavailableReason = "Frame Debugger utility type '" + candidate
                        + "' failed to initialise: " + exception.GetBaseException().Message;
                }
            }

            if (s_unavailableReason == null)
            {
                s_unavailableReason = "FrameDebuggerUtility was not found via reflection in Unity "
                    + Application.unityVersion + ". Searched: " + string.Join(", ", CandidateTypeNames)
                    + ". Use the Frame Debugger window directly (Window > Analysis > Frame Debugger).";
            }

            return null;
        }

        internal static bool Available
        {
            get { return ResolveType() != null; }
        }

        internal static string UnavailableReason
        {
            get
            {
                ResolveType();
                return s_unavailableReason;
            }
        }

        // ---- enabled -----------------------------------------------------------------------

        internal static bool TryGetEnabled(out bool enabled)
        {
            enabled = false;
            Type type = ResolveType();
            if (type == null) return false;

            foreach (string name in new[] { "enabled", "isEnabled", "Enabled" })
            {
                object value = CucliOptional.GetStaticProperty(type, name);
                if (value is bool)
                {
                    enabled = (bool)value;
                    return true;
                }
            }

            return false;
        }

        internal static bool TrySetEnabled(bool enabled, out string error)
        {
            error = null;
            Type type = ResolveType();
            if (type == null)
            {
                error = UnavailableReason;
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            foreach (string name in new[] { "enabled", "isEnabled", "Enabled" })
            {
                try
                {
                    PropertyInfo property = type.GetProperty(name, flags);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                    {
                        property.SetValue(null, enabled, null);
                        return true;
                    }
                }
                catch (Exception exception)
                {
                    error = "Setting FrameDebuggerUtility." + name + " failed: "
                        + exception.GetBaseException().Message;
                }
            }

            foreach (string name in new[] { "SetEnabled", "Enable", "Disable" })
            {
                try
                {
                    MethodInfo method = type.GetMethod(name, flags);
                    if (method == null) continue;

                    object[] arguments = method.GetParameters().Length == 1
                        ? new object[] { enabled }
                        : new object[0];
                    if (method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType != typeof(bool))
                    {
                        continue;
                    }

                    method.Invoke(null, arguments);
                    return true;
                }
                catch (Exception exception)
                {
                    error = "Calling FrameDebuggerUtility." + name + " failed: "
                        + exception.GetBaseException().Message;
                }
            }

            error = "FrameDebuggerUtility exposes no writable 'enabled' member in Unity "
                + Application.unityVersion + ". Use 'cucli editor menu --menu-path \"Window/Analysis/Frame Debugger\"' "
                + "and toggle it by hand.";
            return false;
        }

        // ---- events ------------------------------------------------------------------------

        internal static bool TryGetEventCollection(out IList events, out string error)
        {
            events = null;
            error = null;
            Type type = ResolveType();
            if (type == null)
            {
                error = UnavailableReason;
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            object raw = null;
            string source = null;

            try
            {
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    if (method.GetParameters().Length != 0) continue;
                    if (method.Name.IndexOf("Event", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!typeof(IEnumerable).IsAssignableFrom(method.ReturnType)) continue;

                    raw = method.Invoke(null, null);
                    source = method.Name + "()";
                    break;
                }

                if (raw == null)
                {
                    foreach (PropertyInfo property in type.GetProperties(flags))
                    {
                        if (property.GetIndexParameters().Length != 0) continue;
                        if (property.Name.IndexOf("Event", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType)) continue;

                        raw = property.GetValue(null, null);
                        source = property.Name;
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                error = "Reading the Frame Debugger event list failed: "
                    + exception.GetBaseException().Message;
                return false;
            }

            if (raw == null)
            {
                error = "FrameDebuggerUtility exposes no zero-argument event collection in Unity "
                    + Application.unityVersion + ". Enable the Frame Debugger and read the window instead.";
                return false;
            }

            var list = new List<object>();
            var enumerable = raw as IEnumerable;
            if (enumerable != null)
            {
                foreach (object item in enumerable) list.Add(item);
            }
            else
            {
                error = "FrameDebuggerUtility." + source + " did not return an enumerable collection.";
                return false;
            }

            events = list;
            return true;
        }

        internal static int EventCount(out string error)
        {
            IList events;
            if (!TryGetEventCollection(out events, out error)) return 0;
            return events.Count;
        }

        /// <summary>
        /// Describes one page of frame debugger events.
        ///
        /// Only the members this project knows about are read; anything else falls back to
        /// <see cref="CucliDescribe"/> so an unexpected shape still produces bounded output rather
        /// than a raw dump of an internal struct.
        /// </summary>
        internal static JObject DescribeEvent(object item, CucliDescribe.Options options)
        {
            if (item == null) return new JObject { ["$null"] = true };

            var result = new JObject();
            bool any = false;

            foreach (string member in EventMemberNames)
            {
                object value = CucliOptional.GetProperty(item, member);
                if (value == null) value = CucliOptional.GetField(item, member);
                if (value == null) continue;

                result[member] = CucliDescribe.Value(value, options, 1);
                any = true;
            }

            if (any) return result;

            JToken described = CucliDescribe.Value(item, options, 0);
            var wrapper = described as JObject;
            if (wrapper != null)
            {
                wrapper["$unmappedShape"] = item.GetType().FullName;
                return wrapper;
            }

            return new JObject
            {
                ["$unmappedShape"] = item.GetType().FullName,
                ["value"] = described
            };
        }
    }
}
