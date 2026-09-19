using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Access to optional packages without compile-time dependencies.
    ///
    /// ProBuilder, VFX Graph, Cinemachine, URP-specific APIs and the Test Framework are all reached
    /// through reflection. The core assembly therefore references nothing optional and compiles in
    /// any Unity project; a missing package produces a precise
    /// <see cref="CucliErrors.OptionalPackageMissing"/> result naming what to install, instead of a
    /// compile error for the whole tool.
    ///
    /// The trade-off is deliberate: these commands run rarely, so a reflection lookup per call is
    /// irrelevant next to never breaking somebody's build.
    /// </summary>
    public static class CucliOptional
    {
        private static readonly Dictionary<string, Assembly> Assemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Type> Types =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        private static HashSet<string> s_installedPackages;

        /// <summary>Loads an assembly by simple name. Returns null when the package is absent.</summary>
        public static Assembly LoadAssembly(string simpleName)
        {
            if (string.IsNullOrWhiteSpace(simpleName)) return null;

            Assembly cached;
            if (Assemblies.TryGetValue(simpleName, out cached)) return cached;

            Assembly resolved = null;
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly candidate in loaded)
            {
                if (!string.Equals(candidate.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                resolved = candidate;
                break;
            }

            if (resolved == null)
            {
                try
                {
                    resolved = Assembly.Load(simpleName);
                }
                catch (Exception)
                {
                    resolved = null;
                }
            }

            Assemblies[simpleName] = resolved;
            return resolved;
        }

        /// <summary>Resolves a type inside a specific assembly. Returns null when either is absent.</summary>
        public static Type FindType(string assemblyName, string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            string key = assemblyName + "|" + typeName;
            Type cached;
            if (Types.TryGetValue(key, out cached)) return cached;

            Type resolved = null;
            Assembly assembly = string.IsNullOrWhiteSpace(assemblyName) ? null : LoadAssembly(assemblyName);
            if (assembly != null)
            {
                try
                {
                    resolved = assembly.GetType(typeName, false);
                }
                catch (Exception)
                {
                    resolved = null;
                }
            }

            if (resolved == null)
            {
                // Fall back to a full-name sweep so a renamed assembly still resolves.
                foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        resolved = candidate.GetType(typeName, false);
                    }
                    catch (Exception)
                    {
                        resolved = null;
                    }

                    if (resolved != null) break;
                }
            }

            Types[key] = resolved;
            return resolved;
        }

        public static bool HasType(string assemblyName, string typeName)
        {
            return FindType(assemblyName, typeName) != null;
        }

        public static void ClearCache()
        {
            Assemblies.Clear();
            Types.Clear();
            s_installedPackages = null;
        }

        /// <summary>True when the package is registered with the Package Manager.</summary>
        public static bool IsPackageInstalled(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return false;

            if (s_installedPackages == null)
            {
                s_installedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    UnityEditor.PackageManager.PackageInfo[] packages =
                        UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                    foreach (UnityEditor.PackageManager.PackageInfo package in packages)
                    {
                        if (package != null && !string.IsNullOrEmpty(package.name)) s_installedPackages.Add(package.name);
                    }
                }
                catch (Exception)
                {
                    // Older Editors: fall back to the assembly probes the callers already perform.
                }
            }

            return s_installedPackages.Contains(packageName);
        }

        /// <summary>Standard "install this" failure used by every optional-dependent command.</summary>
        public static CucliCommandResult Missing(string packageName, string capability)
        {
            string display = DisplayName(packageName);
            return CucliCommandResult.Fail(
                CucliErrors.OptionalPackageMissing,
                display + " is not installed, so " + capability + " is unavailable. "
                + "Install it from Window > Package Manager, or run: "
                + "cucli package add " + packageName,
                new { optionalPackage = packageName, capability });
        }

        public static string DisplayName(string packageName)
        {
            switch (packageName)
            {
                case "com.unity.probuilder": return "ProBuilder";
                case "com.unity.visualeffectgraph": return "VFX Graph";
                case "com.unity.cinemachine": return "Cinemachine";
                case "com.unity.test-framework": return "Unity Test Framework";
                case "com.unity.render-pipelines.universal": return "Universal Render Pipeline";
                case "com.unity.render-pipelines.core": return "Render Pipelines Core";
                case "com.unity.textmeshpro": return "TextMeshPro";
                case "com.unity.ugui": return "Unity UI (uGUI)";
                case "com.unity.addressables": return "Addressables";
                default: return packageName;
            }
        }

        /// <summary>Invokes a static method, flattening reflection exceptions into a message.</summary>
        public static bool TryInvokeStatic(
            Type type,
            string methodName,
            object[] arguments,
            out object result,
            out string error)
        {
            result = null;
            error = null;
            if (type == null)
            {
                error = "the target type is not available.";
                return false;
            }

            try
            {
                MethodInfo method = type.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    ArgumentTypes(arguments),
                    null);

                if (method == null)
                {
                    error = type.FullName + "." + methodName + " was not found; "
                        + "the installed package version may differ from the one this command expects.";
                    return false;
                }

                result = method.Invoke(null, arguments);
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = (exception.InnerException ?? exception).GetBaseException().Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        private static Type[] ArgumentTypes(object[] arguments)
        {
            if (arguments == null || arguments.Length == 0) return Type.EmptyTypes;
            var types = new Type[arguments.Length];
            for (int index = 0; index < arguments.Length; index++)
            {
                types[index] = arguments[index] == null ? typeof(object) : arguments[index].GetType();
            }

            return types;
        }

        public static object GetStaticProperty(Type type, string propertyName)
        {
            if (type == null) return null;
            try
            {
                PropertyInfo property = type.GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return property == null ? null : property.GetValue(null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static object GetProperty(object target, string propertyName)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return property == null ? null : property.GetValue(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool SetProperty(object target, string propertyName, object value)
        {
            if (target == null) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static object GetField(object target, string fieldName)
        {
            if (target == null) return null;
            try
            {
                FieldInfo field = target.GetType().GetField(
                    fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return field == null ? null : field.GetValue(target);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Enumerates every loaded type whose name matches, used by commands that must find a
        /// package type without knowing which assembly hosts it.
        /// </summary>
        public static IEnumerable<Type> FindTypesByFullName(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try
                {
                    type = assembly.GetType(fullName, false);
                }
                catch (Exception)
                {
                    type = null;
                }

                if (type != null) yield return type;
            }
        }

        /// <summary>Best-effort package version used in diagnostics.</summary>
        public static string PackageVersion(string packageName)
        {
            try
            {
                UnityEditor.PackageManager.PackageInfo[] packages =
                    UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                foreach (UnityEditor.PackageManager.PackageInfo package in packages)
                {
                    if (package != null && string.Equals(package.name, packageName, StringComparison.OrdinalIgnoreCase))
                    {
                        return package.version;
                    }
                }
            }
            catch (Exception)
            {
                // Diagnostics only.
            }

            return string.Empty;
        }

        /// <summary>Convenience for the "run this only when the package is there" preamble.</summary>
        public static CucliCommandResult RequirePackage(string packageName, string capability)
        {
            return IsPackageInstalled(packageName) ? null : Missing(packageName, capability);
        }

        /// <summary>Environment report used by <c>cucli meta doctor</c>.</summary>
        public static object DescribeEnvironment()
        {
            return new
            {
                probuilder = new
                {
                    installed = IsPackageInstalled("com.unity.probuilder"),
                    version = PackageVersion("com.unity.probuilder"),
                    typesVisible = HasType("Unity.ProBuilder", "UnityEngine.ProBuilder.ProBuilderMesh")
                },
                vfxGraph = new
                {
                    installed = IsPackageInstalled("com.unity.visualeffectgraph"),
                    version = PackageVersion("com.unity.visualeffectgraph"),
                    typesVisible = HasType("Unity.VisualEffectGraph.Runtime", "UnityEngine.VFX.VisualEffect")
                },
                cinemachine = new
                {
                    installed = IsPackageInstalled("com.unity.cinemachine"),
                    version = PackageVersion("com.unity.cinemachine")
                },
                testFramework = new
                {
                    installed = IsPackageInstalled("com.unity.test-framework"),
                    version = PackageVersion("com.unity.test-framework"),
                    typesVisible = HasType("UnityEditor.TestRunner", "UnityEditor.TestTools.TestRunner.Api.TestRunnerApi")
                },
                textMeshPro = new
                {
                    installed = IsPackageInstalled("com.unity.textmeshpro"),
                    version = PackageVersion("com.unity.textmeshpro")
                },
                newtonsoftJson = new
                {
                    version = PackageVersion("com.unity.nuget.newtonsoft-json")
                }
            };
        }
    }
}
