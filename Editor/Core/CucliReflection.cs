using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>Flattened description of one readable/writable member, used by every <c>inspect</c> command.</summary>
    public sealed class CucliMemberInfo
    {
        public string Name;
        public string TypeName;
        public string Kind;
        public bool CanWrite;
        public string DeclaringType;
        public string Tooltip;

        public JObject ToJson()
        {
            var result = new JObject
            {
                ["name"] = Name,
                ["type"] = TypeName,
                ["kind"] = Kind,
                ["writable"] = CanWrite
            };

            if (!string.IsNullOrEmpty(DeclaringType) && DeclaringType != "Object")
            {
                result["declaredBy"] = DeclaringType;
            }

            if (!string.IsNullOrEmpty(Tooltip)) result["tooltip"] = Tooltip;
            return result;
        }
    }

    /// <summary>
    /// Reflection layer behind every generic <c>get</c> / <c>set</c> / <c>inspect</c> command.
    ///
    /// Reading is deliberately tolerant - a property that throws becomes a readable error string
    /// instead of failing the whole inspection. Writing is strict: unknown members, read-only
    /// members and unconvertible values all report exactly why they were rejected.
    /// </summary>
    public static class CucliReflection
    {
        private static readonly Dictionary<string, Type> TypeCache =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        private const BindingFlags InstanceFlags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        /// <summary>Member names that exist on every UnityEngine.Object but are never useful to inspect.</summary>
        private static readonly HashSet<string> NoisyMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "hideFlags", "runInEditMode", "isActiveAndEnabled"
        };

        public static Type FindType(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "a type name is required.";
                return null;
            }

            string key = name.Trim();
            Type cached;
            if (TypeCache.TryGetValue(key, out cached))
            {
                if (cached == null) error = "type '" + key + "' was not found in any loaded assembly.";
                return cached;
            }

            Type resolved = ResolveType(key);
            TypeCache[key] = resolved;
            if (resolved == null)
            {
                error = "type '" + key + "' was not found in any loaded assembly. "
                    + "Use 'cucli catalog --search <name>' to look up Unity types.";
            }

            return resolved;
        }

        private static Type ResolveType(string name)
        {
            Type direct = Type.GetType(name, false);
            if (direct != null) return direct;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type exact;
                try
                {
                    exact = assembly.GetType(name, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (exact != null) return exact;
            }

            if (name.IndexOf('.') >= 0) return null;

            // Short name: prefer UnityEngine and UnityEditor, then anything else, and treat an
            // ambiguous match as unresolved rather than guessing.
            Type best = null;
            int matches = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types.Where(type => type != null).ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || !string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (type.IsGenericTypeDefinition) continue;

                    string assemblyName = assembly.GetName().Name ?? string.Empty;
                    bool preferred = assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal)
                        || assemblyName.StartsWith("UnityEditor", StringComparison.Ordinal);
                    if (best == null || preferred)
                    {
                        if (best == null || !preferred) matches++;
                        best = type;
                        if (preferred) matches = 1;
                    }
                    else
                    {
                        matches++;
                    }

                    if (preferred && matches == 1 && type.Namespace != null
                        && type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
                    {
                        return type;
                    }
                }
            }

            return matches == 1 ? best : null;
        }

        public static MemberInfo FindMember(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;

            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, InstanceFlags);
                if (field != null && !field.IsStatic) return field;

                PropertyInfo property = current.GetProperty(name, InstanceFlags);
                if (property != null && property.GetIndexParameters().Length == 0) return property;
            }

            return null;
        }

        /// <summary>Every readable public instance member, alphabetically, with duplicates collapsed.</summary>
        public static List<CucliMemberInfo> Members(Type type, bool includeNoisy = false)
        {
            var result = new List<CucliMemberInfo>();
            if (type == null) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsStatic || field.IsInitOnly && false) continue;
                if (!includeNoisy && NoisyMembers.Contains(field.Name)) continue;
                if (!seen.Add(field.Name)) continue;

                result.Add(new CucliMemberInfo
                {
                    Name = field.Name,
                    TypeName = CucliValues.FriendlyName(field.FieldType),
                    Kind = "field",
                    CanWrite = !field.IsInitOnly && !field.IsLiteral,
                    DeclaringType = field.DeclaringType == null ? null : field.DeclaringType.Name
                });
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                if (property.GetGetMethod() == null) continue;
                if (!includeNoisy && NoisyMembers.Contains(property.Name)) continue;
                if (!seen.Add(property.Name)) continue;

                result.Add(new CucliMemberInfo
                {
                    Name = property.Name,
                    TypeName = CucliValues.FriendlyName(property.PropertyType),
                    Kind = "property",
                    CanWrite = property.CanWrite && property.GetSetMethod() != null,
                    DeclaringType = property.DeclaringType == null ? null : property.DeclaringType.Name
                });
            }

            result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            return result;
        }

        /// <summary>Reads a member, converting failures into strings so inspection never aborts.</summary>
        public static object Get(object target, string name, out string error)
        {
            error = null;
            if (target == null)
            {
                error = "cannot read '" + name + "' from a null target.";
                return null;
            }

            MemberInfo member = FindMember(target.GetType(), name);
            if (member == null)
            {
                error = "'" + name + "' is not a public member of " + target.GetType().Name + ".";
                return null;
            }

            try
            {
                var field = member as FieldInfo;
                if (field != null) return field.GetValue(target);

                var property = member as PropertyInfo;
                if (property != null) return property.GetValue(target, null);
            }
            catch (TargetInvocationException exception)
            {
                error = "'" + name + "' threw while being read: "
                    + (exception.InnerException ?? exception).GetBaseException().Message;
                return null;
            }
            catch (Exception exception)
            {
                error = "'" + name + "' could not be read: " + exception.GetBaseException().Message;
                return null;
            }

            error = "'" + name + "' is not readable.";
            return null;
        }

        /// <summary>Walks a dotted path such as <c>material.color</c> or <c>items[0].name</c>.</summary>
        public static object GetPath(object target, string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "a member path is required.";
                return null;
            }

            object current = target;
            foreach (string segment in CucliPath.Split(path))
            {
                string memberError;
                current = Step(current, segment, out memberError);
                if (memberError != null)
                {
                    error = memberError;
                    return null;
                }
            }

            return current;
        }

        private static object Step(object current, string segment, out string error)
        {
            error = null;
            if (current == null)
            {
                error = "cannot resolve '" + segment + "' on a null value.";
                return null;
            }

            int open = segment.IndexOf('[');
            string memberName = open < 0 ? segment : segment.Substring(0, open);
            if (memberName.Length > 0)
            {
                current = Get(current, memberName, out error);
                if (error != null) return null;
            }

            while (open >= 0)
            {
                int close = segment.IndexOf(']', open);
                if (close < 0)
                {
                    error = "unbalanced '[' in path segment '" + segment + "'.";
                    return null;
                }

                string indexText = segment.Substring(open + 1, close - open - 1).Trim();
                int index;
                if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                {
                    error = "'" + indexText + "' is not a valid index.";
                    return null;
                }

                var list = current as IList;
                if (list != null)
                {
                    if (index < 0 || index >= list.Count)
                    {
                        error = "index " + index + " is out of range (count " + list.Count + ").";
                        return null;
                    }

                    current = list[index];
                }
                else
                {
                    var array = current as Array;
                    if (array == null)
                    {
                        error = "the value before [" + index + "] is not an array or list.";
                        return null;
                    }

                    if (index < 0 || index >= array.Length)
                    {
                        error = "index " + index + " is out of range (length " + array.Length + ").";
                        return null;
                    }

                    current = array.GetValue(index);
                }

                open = close + 1 < segment.Length && segment[close + 1] == '['
                    ? close + 1
                    : -1;
            }

            return current;
        }

        /// <summary>Writes a member through reflection. Callers must have registered Undo first.</summary>
        public static bool TrySetMember(object target, string name, JToken value, out string error)
        {
            error = null;
            if (target == null)
            {
                error = "cannot write '" + name + "' on a null target.";
                return false;
            }

            MemberInfo member = FindMember(target.GetType(), name);
            if (member == null)
            {
                error = "'" + name + "' is not a public member of " + target.GetType().Name + ".";
                return false;
            }

            Type memberType = member is FieldInfo
                ? ((FieldInfo)member).FieldType
                : ((PropertyInfo)member).PropertyType;

            object converted;
            string convertError;
            try
            {
                converted = CucliValues.Convert(value, memberType, out convertError);
            }
            catch (Exception exception)
            {
                error = "'" + name + "' could not be converted: " + exception.GetBaseException().Message;
                return false;
            }

            if (convertError != null)
            {
                error = "'" + name + "': " + convertError;
                return false;
            }

            try
            {
                var field = member as FieldInfo;
                if (field != null)
                {
                    if (field.IsInitOnly || field.IsLiteral)
                    {
                        error = "'" + name + "' is read-only.";
                        return false;
                    }

                    field.SetValue(target, converted);
                    return true;
                }

                var property = member as PropertyInfo;
                if (property == null || !property.CanWrite || property.GetSetMethod() == null)
                {
                    error = "'" + name + "' is read-only.";
                    return false;
                }

                property.SetValue(target, converted, null);
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = "'" + name + "' threw while being written: "
                    + (exception.InnerException ?? exception).GetBaseException().Message;
                return false;
            }
            catch (Exception exception)
            {
                error = "'" + name + "' could not be written: " + exception.GetBaseException().Message;
                return false;
            }
        }

        public static bool TrySetPath(object target, string path, JToken value, out string error)
        {
            error = null;
            string[] segments = CucliPath.Split(path).ToArray();
            if (segments.Length == 0)
            {
                error = "a member path is required.";
                return false;
            }

            if (segments.Length == 1) return TrySetMember(target, segments[0], value, out error);

            object owner = target;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                owner = Step(owner, segments[index], out error);
                if (error != null) return false;
            }

            return TrySetMember(owner, segments[segments.Length - 1], value, out error);
        }

        /// <summary>Invokes a public method by name, matching parameters by name or by position.</summary>
        public static bool TryInvoke(
            object target,
            string methodName,
            JObject arguments,
            out object result,
            out string error)
        {
            result = null;
            error = null;
            if (target == null)
            {
                error = "cannot invoke '" + methodName + "' on a null target.";
                return false;
            }

            MethodInfo[] candidates = target.GetType()
                .GetMethods(InstanceFlags)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .Where(method => !method.IsGenericMethodDefinition)
                .ToArray();

            if (candidates.Length == 0)
            {
                error = "'" + methodName + "' is not a public method of " + target.GetType().Name + ".";
                return false;
            }

            var args = new CucliArgs(arguments ?? new JObject());
            var failures = new List<string>();

            Array.Sort(candidates, (left, right) =>
                left.GetParameters().Length.CompareTo(right.GetParameters().Length));

            foreach (MethodInfo candidate in candidates)
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                var values = new object[parameters.Length];
                bool usable = true;

                for (int index = 0; index < parameters.Length; index++)
                {
                    ParameterInfo parameter = parameters[index];
                    JToken token = args.Token(parameter.Name);
                    if (token == null) token = args.Token("arg" + index);
                    if (token == null)
                    {
                        if (parameter.HasDefaultValue)
                        {
                            values[index] = parameter.DefaultValue;
                            continue;
                        }

                        usable = false;
                        failures.Add(candidate.Name + ": missing argument '" + parameter.Name + "'");
                        break;
                    }

                    string convertError;
                    values[index] = CucliValues.Convert(token, parameter.ParameterType, out convertError);
                    if (convertError != null)
                    {
                        usable = false;
                        failures.Add(candidate.Name + ": '" + parameter.Name + "' " + convertError);
                        break;
                    }
                }

                if (!usable) continue;

                try
                {
                    result = candidate.Invoke(target, values);
                    return true;
                }
                catch (TargetInvocationException exception)
                {
                    error = "'" + methodName + "' threw: "
                        + (exception.InnerException ?? exception).GetBaseException().Message;
                    return false;
                }
                catch (Exception exception)
                {
                    failures.Add(candidate.Name + ": " + exception.GetBaseException().Message);
                }
            }

            error = failures.Count == 0
                ? "'" + methodName + "' exists but no overload matched the supplied arguments."
                : string.Join("; ", failures);
            return false;
        }

        /// <summary>Human-readable type name; used in listings and error messages.</summary>
        public static string TypeName(Type type)
        {
            return type == null ? "null" : type.FullName ?? type.Name;
        }

        public static void ClearCache()
        {
            TypeCache.Clear();
        }
    }

    /// <summary>Splits member paths such as <c>rendering.fog.color</c> or <c>items[0].name</c>.</summary>
    public static class CucliPath
    {
        public static IEnumerable<string> Split(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) yield break;

            var builder = new System.Text.StringBuilder();
            foreach (char character in path)
            {
                if (character == '.' && builder.Length > 0 && builder[builder.Length - 1] != ']')
                {
                    yield return builder.ToString();
                    builder.Length = 0;
                    continue;
                }

                if (character == '.' && builder.Length == 0) continue;
                builder.Append(character);
            }

            if (builder.Length > 0) yield return builder.ToString();
        }
    }
}
