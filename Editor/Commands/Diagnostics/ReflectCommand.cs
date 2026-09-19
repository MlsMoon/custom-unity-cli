using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli reflect ...</c> - runtime type and member lookup, equivalent to MCP's
    /// <c>unity_reflect</c>.
    ///
    /// This exists because guessing an API from memory is the most common way an agent breaks a
    /// Unity project. Every answer here is read from the assemblies actually loaded in this Editor,
    /// so a version-specific signature can be confirmed instead of assumed.
    /// </summary>
    internal sealed class ReflectCommand : CucliCommand
    {
        private const int SearchTypeLimit = 25;

        public override string Name => "reflect";

        public override string Summary =>
            "Look up loaded types, their members and their signatures. Answers come from this Editor's real assemblies.";

        public override CucliActionInfo[] Actions => Declare(
            "get-type", "Describe one type: kind, namespace, assembly and the members it declares itself.",
            "get-member", "Resolve a single member by name, searching base types.",
            "search", "Find types and members whose name contains a fragment (capped, paged).");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "editor.state.read" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""get-type"", ""get-member"", ""search""], ""default"": ""get-type"" },
    ""typeName"": { ""type"": ""string"", ""description"": ""get-type / get-member: short or fully qualified type name."" },
    ""memberName"": { ""type"": ""string"", ""description"": ""get-member: member to resolve, searched up the base chain."" },
    ""query"": { ""type"": ""string"", ""description"": ""search: case-insensitive name fragment."" },
    ""kind"": { ""type"": ""string"", ""enum"": [""any"", ""type"", ""method"", ""property"", ""field"", ""event""], ""default"": ""any"" },
    ""includeInherited"": { ""type"": ""boolean"", ""default"": false, ""description"": ""get-type: include members inherited from base types. False by default, matching MCP; pass true (or declaredOnly: false) for the full callable surface."" },
    ""declaredOnly"": { ""type"": ""boolean"", ""default"": true, ""description"": ""get-type: list only the members this type declares itself. True by default, matching MCP's get_type."" },
    ""includeNoisy"": { ""type"": ""boolean"", ""default"": false, ""description"": ""get-type: include hideFlags/runInEditMode and friends."" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 1, ""maximum"": 500 },
    ""pageSize"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 1, ""maximum"": 500 },
    ""cursor"": { ""type"": ""string"", ""description"": ""Opaque cursor from a previous page."" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            // Reflecting mid-compile reads a half-built assembly graph and returns answers that are
            // wrong in ways that are hard to notice, so refuse instead.
            if (EditorApplication.isCompiling)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CompilationInProgress,
                    "Scripts are compiling. Reflection would read a partially rebuilt assembly graph; "
                    + "run 'cucli refresh wait' first.");
            }

            switch (ResolveAction(context, "get-type"))
            {
                case "get-type": return GetType(context);
                case "get-member": return GetMember(context);
                case "search": return Search(context);
                default: return CucliCommandResult.Unsupported(ResolveAction(context, "get-type"), ActionNames());
            }
        }

        private static CucliCommandResult GetType(CucliCommandContext context)
        {
            string requested = context.Input.GetString("typeName") ?? context.Input.GetString("type_name");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing("typeName", "Example: --type-name UnityEngine.Rigidbody");
            }

            // Type resolution is cached and cleared on every reload, so a stale answer is impossible.
            CucliReflection.ClearCache();
            string error;
            Type type = CucliReflection.FindType(requested.Trim(), out error);
            if (type == null)
            {
                // A short name can be ambiguous rather than absent, and the two call for different
                // fixes: one is a typo, the other is a namespace question. MCP reports this case as
                // "ambiguous" plus the candidate list instead of guessing, so the same distinction is
                // made here rather than collapsing both into "not found".
                List<string> matches = AmbiguousTypeMatches(requested);
                if (matches.Count > 1)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetAmbiguous,
                        "'" + requested + "' matches " + matches.Count + " loaded types. Retry with a "
                        + "namespace-qualified name; the candidates are in 'matches'.",
                        new { typeName = requested, ambiguous = true, matches });
                }

                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound, error ?? ("Type '" + requested + "' was not found."),
                    new { typeName = requested });
            }

            // DeclaredOnly by default, matching MCP's get_type: the question it answers is "what does
            // this type declare", and on a Unity type the inherited list is long enough to bury that
            // answer. includeInherited: true (or declaredOnly: false) widens it to the full callable
            // surface, and the response reports which view it used either way.
            //
            // Either spelling is honoured on its own: reading "declaredOnly defaults to true" and then
            // requiring the caller to also negate it would make includeInherited: true silently do
            // nothing, which is the sort of flag interaction nobody debugs twice.
            bool declaredOnlySpecified =
                context.Input.Has("declaredOnly") || context.Input.Has("declared_only");
            bool includeInheritedSpecified =
                context.Input.Has("includeInherited") || context.Input.Has("include_inherited");

            bool includeInherited;
            if (declaredOnlySpecified)
            {
                includeInherited = !context.Input.GetBool(
                    "declaredOnly", context.Input.GetBool("declared_only", true));
            }
            else if (includeInheritedSpecified)
            {
                includeInherited = context.Input.GetBool(
                    "includeInherited", context.Input.GetBool("include_inherited", false));
            }
            else
            {
                includeInherited = false;
            }

            bool includeNoisy = context.Input.GetBool(
                "includeNoisy", context.Input.GetBool("include_noisy", false));
            int maxItems = Clamp(context.Input.GetInt("maxItems", 100), 1, 500);

            List<CucliMemberInfo> members = includeInherited
                ? CucliReflection.Members(type, includeNoisy)
                : DeclaredMembers(type, includeNoisy);

            int total = members.Count;

            // Cursor paging on top of the maxItems cap, so a type with hundreds of members can be
            // walked in full instead of only ever showing the first page.
            CucliPage<CucliMemberInfo> page = CucliPaging.Slice(members, context.Input, maxItems);
            var items = new JArray();
            foreach (CucliMemberInfo member in page.Items)
            {
                items.Add(member.ToJson());
            }

            if (page.HasMore)
            {
                context.Warn(page.Total + " members match; showing " + page.Items.Count
                    + " from offset " + page.Offset
                    + (page.NextCursor == null
                        ? "."
                        : ". Pass --cursor " + page.NextCursor + " for the next page."));
            }

            return CucliCommandResult.Ok(
                new
                {
                    typeName = type.FullName ?? type.Name,
                    name = type.Name,
                    @namespace = type.Namespace,
                    assembly = type.Assembly.GetName().Name,
                    kind = TypeKind(type),
                    isAbstract = type.IsAbstract,
                    isSealed = type.IsSealed,
                    isEnum = type.IsEnum,
                    isInterface = type.IsInterface,
                    baseType = type.BaseType == null ? null : CucliReflection.TypeName(type.BaseType),
                    interfaces = type.GetInterfaces().Select(CucliReflection.TypeName).Take(maxItems).ToArray(),
                    enumValues = type.IsEnum ? Enum.GetNames(type) : null,
                    includeInherited,
                    declaredOnly = !includeInherited,
                    memberCount = total,
                    returned = items.Count,
                    offset = page.Offset,
                    pageSize = page.PageSize,
                    hasMore = page.HasMore,
                    nextCursor = page.HasMore ? page.NextCursor : null,
                    members = items
                },
                "已返回 " + items.Count + " / " + total + " 个成员（"
                + (includeInherited
                    ? "含继承成员；这是有别于 MCP 的扩展视图，传 declaredOnly: true 可切回 MCP 语义"
                    : "只列本类型声明的成员，等同 MCP 的 get_type；传 includeInherited: true 可扩展到继承链")
                + "）。");
        }

        /// <summary>
        /// Members declared directly on the type. Deliberately excludes inherited members, matching
        /// MCP's <c>get_type</c>: "where is this actually declared" is the question being asked.
        /// </summary>
        private static List<CucliMemberInfo> DeclaredMembers(Type type, bool includeNoisy)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;

            var result = new List<CucliMemberInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (!seen.Add(field.Name)) continue;
                if (!includeNoisy && field.Name == "hideFlags") continue;
                result.Add(new CucliMemberInfo
                {
                    Name = field.Name,
                    TypeName = CucliValues.FriendlyName(field.FieldType),
                    Kind = field.IsStatic ? "static-field" : "field",
                    CanWrite = !field.IsInitOnly && !field.IsLiteral,
                    DeclaringType = type.Name
                });
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                if (!seen.Add(property.Name)) continue;
                if (!includeNoisy && property.Name == "runInEditMode") continue;
                result.Add(new CucliMemberInfo
                {
                    Name = property.Name,
                    TypeName = CucliValues.FriendlyName(property.PropertyType),
                    Kind = property.GetGetMethod() != null && property.GetGetMethod().IsStatic
                        ? "static-property"
                        : "property",
                    CanWrite = property.CanWrite && property.GetSetMethod() != null,
                    DeclaringType = type.Name
                });
            }

            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (method.IsSpecialName) continue;
                string signature = Signature(method);
                result.Add(new CucliMemberInfo
                {
                    Name = signature,
                    TypeName = CucliValues.FriendlyName(method.ReturnType),
                    Kind = method.IsStatic ? "static-method" : "method",
                    CanWrite = false,
                    DeclaringType = type.Name
                });
            }

            foreach (EventInfo eventInfo in type.GetEvents(flags))
            {
                result.Add(new CucliMemberInfo
                {
                    Name = eventInfo.Name,
                    TypeName = CucliValues.FriendlyName(eventInfo.EventHandlerType),
                    Kind = "event",
                    CanWrite = false,
                    DeclaringType = type.Name
                });
            }

            result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            return result;
        }

        private static string Signature(MethodInfo method)
        {
            string arguments = string.Join(", ", method.GetParameters()
                .Select(parameter => CucliValues.FriendlyName(parameter.ParameterType) + " " + parameter.Name));
            return method.Name + "(" + arguments + ")";
        }

        private static string TypeKind(Type type)
        {
            if (type.IsEnum) return "enum";
            if (type.IsInterface) return "interface";
            if (type.IsValueType) return "struct";
            if (typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type)) return "scriptableobject";
            if (typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type)) return "monobehaviour";
            if (typeof(UnityEngine.Component).IsAssignableFrom(type)) return "component";
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return "unityobject";
            if (type.IsAbstract && type.IsSealed) return "static-class";
            return "class";
        }

        /// <summary>
        /// Full names of every loaded type whose short name equals <paramref name="requested"/>.
        ///
        /// Only consulted after a lookup has already failed, and only for a namespace-less name: it is
        /// what turns an unhelpful "type not found" into the actionable "this name is ambiguous".
        /// Capped, because a name such as <c>Object</c> would otherwise pull thousands of rows into a
        /// single response.
        /// </summary>
        private static List<string> AmbiguousTypeMatches(string requested)
        {
            const int limit = 25;
            var matches = new List<string>();
            if (string.IsNullOrWhiteSpace(requested) || requested.IndexOf('.') >= 0) return matches;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    var loaded = new List<Type>();
                    foreach (Type candidate in exception.Types)
                    {
                        if (candidate != null) loaded.Add(candidate);
                    }

                    types = loaded.ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || type.IsGenericTypeDefinition) continue;
                    if (!string.Equals(type.Name, requested, StringComparison.OrdinalIgnoreCase)) continue;
                    string full = type.FullName;
                    if (string.IsNullOrEmpty(full) || matches.Contains(full)) continue;
                    matches.Add(full);
                    if (matches.Count >= limit) return matches;
                }
            }

            return matches;
        }

        private static CucliCommandResult GetMember(CucliCommandContext context)
        {
            string typeName = context.Input.GetString("typeName") ?? context.Input.GetString("type_name");
            string memberName = context.Input.GetString("memberName") ?? context.Input.GetString("member_name");
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return CucliCommandResult.Missing("typeName");
            }

            if (string.IsNullOrWhiteSpace(memberName))
            {
                return CucliCommandResult.Missing("memberName", "Example: --member-name velocity");
            }

            CucliReflection.ClearCache();
            string error;
            Type type = CucliReflection.FindType(typeName.Trim(), out error);
            if (type == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TypeNotFound, error ?? ("Type '" + typeName + "' was not found."));
            }

            string wanted = memberName.Trim();
            // Order matters: a name can exist as both a method and a property, and callers almost
            // always mean the method when they pass a bare identifier that matches one.
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                MethodInfo method = current.GetMethod(
                    wanted,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (method != null && !method.IsSpecialName)
                {
                    return CucliCommandResult.Ok(new
                    {
                        kind = "method",
                        name = wanted,
                        type = CucliReflection.TypeName(type),
                        declaredBy = CucliReflection.TypeName(current),
                        isStatic = method.IsStatic,
                        returnType = CucliValues.FriendlyName(method.ReturnType),
                        signature = Signature(method),
                        parameters = method.GetParameters().Select(parameter => new
                        {
                            name = parameter.Name,
                            type = CucliValues.FriendlyName(parameter.ParameterType),
                            optional = parameter.IsOptional,
                            hasDefault = parameter.HasDefaultValue,
                            defaultValue = parameter.HasDefaultValue
                                ? CucliDescribe.SafeToString(parameter.DefaultValue)
                                : null
                        }).ToArray()
                    });
                }

                PropertyInfo property = current.GetProperty(
                    wanted,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                        | BindingFlags.FlattenHierarchy);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return CucliCommandResult.Ok(new
                    {
                        kind = "property",
                        name = wanted,
                        type = CucliReflection.TypeName(type),
                        declaredBy = CucliReflection.TypeName(current),
                        propertyType = CucliValues.FriendlyName(property.PropertyType),
                        canRead = property.CanRead,
                        canWrite = property.CanWrite,
                        isStatic = (property.GetGetMethod() ?? property.GetSetMethod()) != null
                            && (property.GetGetMethod() ?? property.GetSetMethod()).IsStatic
                    });
                }

                FieldInfo field = current.GetField(
                    wanted,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (field != null)
                {
                    return CucliCommandResult.Ok(new
                    {
                        kind = "field",
                        name = wanted,
                        type = CucliReflection.TypeName(type),
                        declaredBy = CucliReflection.TypeName(current),
                        fieldType = CucliValues.FriendlyName(field.FieldType),
                        isStatic = field.IsStatic,
                        canWrite = !field.IsInitOnly && !field.IsLiteral
                    });
                }

                EventInfo eventInfo = current.GetEvent(
                    wanted,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (eventInfo != null)
                {
                    return CucliCommandResult.Ok(new
                    {
                        kind = "event",
                        name = wanted,
                        type = CucliReflection.TypeName(type),
                        declaredBy = CucliReflection.TypeName(current),
                        handlerType = CucliValues.FriendlyName(eventInfo.EventHandlerType)
                    });
                }
            }

            // Extension methods are deliberately not members of the type, which is exactly why a caller
            // asking for one by bare name is surprised by a plain member miss. Resolve them before
            // giving up, and label the hit so nobody mistakes it for an instance member.
            MethodInfo extension = FindExtensionMethod(type, wanted);
            if (extension != null)
            {
                return CucliCommandResult.Ok(new
                {
                    kind = "extensionMethod",
                    name = wanted,
                    type = CucliReflection.TypeName(type),
                    declaredBy = CucliReflection.TypeName(extension.DeclaringType),
                    isStatic = true,
                    returnType = CucliValues.FriendlyName(extension.ReturnType),
                    signature = Signature(extension),
                    parameters = extension.GetParameters().Select(parameter => new
                    {
                        name = parameter.Name,
                        type = CucliValues.FriendlyName(parameter.ParameterType),
                        optional = parameter.IsOptional,
                        hasDefault = parameter.HasDefaultValue,
                        defaultValue = parameter.HasDefaultValue
                            ? CucliDescribe.SafeToString(parameter.DefaultValue)
                            : null
                    }).ToArray()
                });
            }

            return CucliCommandResult.Fail(
                CucliErrors.MemberNotFound,
                "'" + wanted + "' is not a member of " + CucliReflection.TypeName(type)
                + " or any of its base types, and no extension method callable on it matches either. "
                + "Use 'cucli reflect get-type --type-name " + type.Name + "' to see what it declares.");
        }

        /// <summary>
        /// Finds a static extension method callable on <paramref name="target"/>: declared in a static
        /// class carrying <see cref="System.Runtime.CompilerServices.ExtensionAttribute"/>, named
        /// <paramref name="wanted"/>, with a first parameter the target is assignable to.
        ///
        /// Scans loaded assemblies rather than a curated list because extension methods on Unity types
        /// arrive from whichever packages a project happens to include.
        /// </summary>
        private static MethodInfo FindExtensionMethod(Type target, string wanted)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    var loaded = new List<Type>();
                    foreach (Type candidate in exception.Types)
                    {
                        if (candidate != null) loaded.Add(candidate);
                    }

                    types = loaded.ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    // Extension methods can only live in a non-generic static class.
                    if (type == null || !type.IsAbstract || !type.IsSealed) continue;
                    if (!type.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
                    {
                        continue;
                    }

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(
                            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (MethodInfo method in methods)
                    {
                        if (!string.Equals(method.Name, wanted, StringComparison.Ordinal)) continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length == 0) continue;
                        if (!parameters[0].ParameterType.IsAssignableFrom(target)) continue;
                        return method;
                    }
                }
            }

            return null;
        }

        private static CucliCommandResult Search(CucliCommandContext context)
        {
            string query = context.Input.GetString("query");
            if (string.IsNullOrWhiteSpace(query))
            {
                return CucliCommandResult.Missing("query", "Example: --query playbackSpeed");
            }

            string kind = (context.Input.GetString("kind") ?? "any").Trim().ToLowerInvariant();
            string needle = query.Trim();
            int maxItems = Clamp(context.Input.GetInt("maxItems", SearchTypeLimit), 1, 200);

            var found = new List<JObject>();
            bool truncated = false;
            int scanned = 0;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (found.Count >= maxItems) { truncated = true; break; }

                string assemblyName = assembly.GetName().Name ?? string.Empty;
                // Framework and tooling assemblies match constantly and are never what a Unity caller
                // is looking for, so they are skipped rather than paginated through.
                if (assemblyName.StartsWith("System", StringComparison.Ordinal)
                    || assemblyName.StartsWith("Microsoft", StringComparison.Ordinal)
                    || assemblyName.StartsWith("mscorlib", StringComparison.Ordinal)
                    || assemblyName.StartsWith("netstandard", StringComparison.Ordinal)
                    || assemblyName.StartsWith("Mono.", StringComparison.Ordinal)
                    || assemblyName.StartsWith("nunit", StringComparison.Ordinal))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types.Where(item => item != null).ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null) continue;
                    scanned++;
                    if (found.Count >= maxItems) { truncated = true; break; }

                    if (kind == "any" || kind == "type")
                    {
                        if (type.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found.Add(new JObject
                            {
                                ["kind"] = "type",
                                ["name"] = type.FullName ?? type.Name,
                                ["assembly"] = assemblyName,
                                ["typeKind"] = TypeKind(type)
                            });
                            continue;
                        }
                    }

                    if (kind != "any" && kind != "type" && !TypeMatches(type, needle)) continue;

                    foreach (MemberInfo member in type.GetMembers(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (found.Count >= maxItems) { truncated = true; break; }

                        string memberKind = MemberKind(member);
                        if (kind != "any" && kind != "type" && kind != memberKind) continue;

                        string name = member is MethodInfo ? Signature((MethodInfo)member) : member.Name;
                        if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        found.Add(new JObject
                        {
                            ["kind"] = memberKind,
                            ["name"] = name,
                            ["type"] = type.FullName ?? type.Name,
                            ["assembly"] = assemblyName
                        });
                    }
                }
            }

            if (truncated) context.Warn("Search stopped at " + maxItems + " matches; narrow the query for a complete answer.");

            return CucliCommandResult.Ok(new
            {
                query = needle,
                kind,
                resultLimit = maxItems,
                truncated,
                scannedTypeCount = scanned,
                count = found.Count,
                matches = new JArray(found.Cast<object>().ToArray())
            });
        }

        private static bool TypeMatches(Type type, string needle)
        {
            return type.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string MemberKind(MemberInfo member)
        {
            if (member is MethodInfo) return "method";
            if (member is PropertyInfo) return "property";
            if (member is FieldInfo) return "field";
            if (member is EventInfo) return "event";
            return "member";
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            return value > maximum ? maximum : value;
        }
    }
}
