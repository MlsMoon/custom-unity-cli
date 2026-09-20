using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Cinemachine access for the <c>camera</c> group, entirely through <see cref="CucliOptional"/>.
    ///
    /// Cinemachine 2 (<c>CinemachineVirtualCamera</c> / <c>CinemachineFreeLook</c>) and
    /// Cinemachine 3 (<c>CinemachineCamera</c>) are both supported: the camera type is probed by name
    /// and the stage classification is done by looking for the members each stage exposes rather than
    /// by a hard-coded type list, so a package upgrade does not break this file.
    ///
    /// Nothing here adds a compile-time reference to <c>com.unity.cinemachine</c>.
    /// </summary>
    internal static class CinemachineSupport
    {
        private const string PackageName = "com.unity.cinemachine";

        private static readonly string[] CameraTypeNames =
        {
            "Unity.Cinemachine.CinemachineCamera",
            "Cinemachine.CinemachineVirtualCamera",
            "Cinemachine.CinemachineFreeLook"
        };

        private static readonly string[] BrainTypeNames =
        {
            "Unity.Cinemachine.CinemachineBrain",
            "Cinemachine.CinemachineBrain"
        };

        internal static CucliCommandResult RequireCinemachine(string capability)
        {
            return CucliOptional.RequirePackage(PackageName, capability);
        }

        internal static bool IsAvailable()
        {
            return CucliOptional.IsPackageInstalled(PackageName) && CameraType() != null;
        }

        internal static Type CameraType()
        {
            return FirstType(CameraTypeNames);
        }

        internal static Type BrainType()
        {
            return FirstType(BrainTypeNames);
        }

        internal static Type ExtensionBaseType()
        {
            return FirstType(new[]
            {
                "Unity.Cinemachine.CinemachineExtension",
                "Cinemachine.CinemachineExtension"
            });
        }

        internal static Type ComponentBaseType()
        {
            return FirstType(new[]
            {
                "Unity.Cinemachine.CinemachineComponentBase",
                "Cinemachine.CinemachineComponentBase"
            });
        }

        private static Type FirstType(string[] names)
        {
            foreach (string name in names)
            {
                Type type = CucliOptional.FindType(null, name);
                if (type != null) return type;
            }

            return null;
        }

        // ---------------------------------------------------------------- host resolution

        /// <summary>Resolves the GameObject that hosts a Cinemachine camera from the payload.</summary>
        internal static GameObject ResolveCameraHost(CucliCommandContext context, out CucliCommandResult failure)
        {
            failure = null;
            Camera camera = CameraCommand.ResolveCamera(context, out failure);
            if (camera != null) return camera.gameObject;

            // manage_camera also accepts an id/name without a Camera component present; retry that way.
            CucliArgs args = context.Input;
            string target = args.GetString("target");
            if (!string.IsNullOrWhiteSpace(target))
            {
                string error;
                GameObject gameObject = CucliObjectResolver.ResolveGameObject(
                    new CucliArgs(new JObject { ["target"] = target }), out error);
                if (gameObject != null)
                {
                    failure = null;
                    return gameObject;
                }
            }

            if (failure == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "Target CinemachineCamera not found. Pass 'target' (name, hierarchy path or instance id).");
            }

            return null;
        }

        internal static object FindCamera(GameObject gameObject)
        {
            if (gameObject == null) return null;
            Type type = CameraType();
            if (type == null) return null;
            return gameObject.GetComponent(type);
        }

        internal static Component FindBrain(GameObject gameObject)
        {
            if (gameObject == null) return null;
            Type type = BrainType();
            if (type == null) return null;
            return gameObject.GetComponent(type);
        }

        internal static bool HasBrain(GameObject gameObject)
        {
            return FindBrain(gameObject) != null;
        }

        internal static bool TryAddBrain(GameObject gameObject, out string error)
        {
            error = null;
            Type type = BrainType();
            if (type == null)
            {
                error = "the CinemachineBrain type was not found; the installed Cinemachine version may differ.";
                return false;
            }

            try
            {
                return gameObject.AddComponent(type) != null;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        internal static bool TryAddCamera(GameObject gameObject, out object camera, out string error)
        {
            camera = null;
            error = null;
            Type type = CameraType();
            if (type == null)
            {
                error = "the CinemachineCamera type was not found; the installed Cinemachine version may differ.";
                return false;
            }

            try
            {
                camera = gameObject.AddComponent(type);
                return camera != null;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- inspection

        internal static JToken DescribeBrains()
        {
            Type brainType = BrainType();
            if (brainType == null) return JValue.CreateNull();

            var entries = new JArray();
            foreach (Component component in CucliAssets.FindAll<Component>())
            {
                if (component == null) continue;
                if (!brainType.IsInstanceOfType(component)) continue;

                object active = GetActiveVirtualCamera(component);
                var activeObject = active as UnityEngine.Object;
                entries.Add(new JObject
                {
                    ["gameObject"] = component.gameObject.name,
                    ["instanceID"] = component.gameObject.GetInstanceID(),
                    ["activeCameraName"] = activeObject == null ? null : activeObject.name,
                    ["activeCameraID"] = activeObject == null ? (int?)null : activeObject.GetInstanceID(),
                    ["isBlending"] = IsBlending(component)
                });
            }

            if (entries.Count == 0) return JValue.CreateNull();
            return entries;
        }

        internal static JArray DescribeCameras(CucliDescribe.Options options)
        {
            var result = new JArray();
            Type cameraType = CameraType();
            if (cameraType == null) return result;

            foreach (Component component in CucliAssets.FindAll<Component>())
            {
                if (component == null || !cameraType.IsInstanceOfType(component)) continue;

                var entry = new JObject
                {
                    ["instanceID"] = component.gameObject.GetInstanceID(),
                    ["name"] = component.gameObject.name,
                    ["priority"] = ToDouble(CucliOptional.GetProperty(component, "Priority")),
                    ["body"] = StageTypeName(component, "Body"),
                    ["aim"] = StageTypeName(component, "Aim"),
                    ["noise"] = NoiseComponent(component) == null ? null : NoiseComponent(component).GetType().Name,
                    ["extensions"] = ExtensionNames(component)
                };

                entry["follow"] = DescribeTarget(CucliOptional.GetProperty(component, "Follow"));
                entry["lookAt"] = DescribeTarget(CucliOptional.GetProperty(component, "LookAt"));
                result.Add(entry);
            }

            return result;
        }

        private static JToken DescribeTarget(object target)
        {
            var transform = target as Transform;
            if (transform != null)
            {
                return new JObject
                {
                    ["name"] = transform.name,
                    ["instanceID"] = transform.gameObject.GetInstanceID()
                };
            }

            var gameObject = target as GameObject;
            if (gameObject != null)
            {
                return new JObject
                {
                    ["name"] = gameObject.name,
                    ["instanceID"] = gameObject.GetInstanceID()
                };
            }

            return JValue.CreateNull();
        }

        private static double? ToDouble(object value)
        {
            if (value == null) return null;
            try
            {
                return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static JArray ExtensionNames(object cinemachineCamera)
        {
            var names = new JArray();
            foreach (Component extension in Extensions(cinemachineCamera)) names.Add(extension.GetType().Name);
            return names;
        }

        internal static object GetActiveVirtualCamera(Component brain)
        {
            if (brain == null) return null;
            object value = CucliOptional.GetProperty(brain, "ActiveVirtualCamera");
            if (value != null) return value;
            return CucliOptional.GetProperty(brain, "ActiveVirtualCamera2");
        }

        internal static bool IsBlending(Component brain)
        {
            object value = CucliOptional.GetProperty(brain, "IsBlending");
            return value is bool && (bool)value;
        }

        internal static string DescribeActiveBlend(Component brain)
        {
            object blend = MemberOrNull(brain, "ActiveBlend");
            if (blend == null) return null;

            object description = CucliOptional.GetProperty(blend, "Description");
            string text = description as string;
            if (!string.IsNullOrEmpty(text)) return text;

            return CucliDescription.Describe(MemberOrNull(blend, "Blend"));
        }

        // ---------------------------------------------------------------- stages

        internal static object StageComponent(object cinemachineCamera, string stage)
        {
            Type baseType = ComponentBaseType();
            if (cinemachineCamera == null || baseType == null) return null;

            var component = cinemachineCamera as Component;
            if (component == null) return null;

            object match = null;
            foreach (Component candidate in component.GetComponents(baseType))
            {
                if (candidate == null) continue;
                bool isBody = ClassifyStage(candidate) == "Body";
                if (string.Equals(stage, "Body", StringComparison.Ordinal) && isBody) match = candidate;
                if (string.Equals(stage, "Aim", StringComparison.Ordinal) && !isBody) match = candidate;
                if (match != null) return match;
            }

            return match;
        }

        internal static string StageTypeName(object cinemachineCamera, string stage)
        {
            object component = StageComponent(cinemachineCamera, stage);
            return component == null ? null : component.GetType().Name;
        }

        /// <summary>
        /// Position stages expose a follow offset; the remaining pipeline components are treated as
        /// the Aim stage. This avoids hard-coding every body and aim type name in Cinemachine.
        /// </summary>
        private static string ClassifyStage(Component component)
        {
            Type type = component.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (type.GetField("m_FollowOffset", flags) != null) return "Body";
            if (type.GetField("m_TrackedObjectOffset", flags) != null) return "Body";
            if (type.GetProperty("FollowOffset") != null) return "Body";
            if (type.GetProperty("TrackedObjectOffset") != null) return "Body";
            return "Aim";
        }

        internal static bool TryReplaceStage(
            object cinemachineCamera, string stage, string typeName, out object component, out string error)
        {
            component = null;
            error = null;

            Type baseType = ComponentBaseType();
            var host = cinemachineCamera as Component;
            if (baseType == null || host == null)
            {
                error = "CinemachineComponentBase was not found; Cinemachine may not be installed correctly.";
                return false;
            }

            Type requested = null;
            Type prefixMatch = null;
            foreach (Type candidate in LoadedTypes())
            {
                if (candidate == null || candidate.IsAbstract || candidate.IsGenericTypeDefinition) continue;
                if (!baseType.IsAssignableFrom(candidate)) continue;

                if (string.Equals(candidate.Name, typeName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    requested = candidate;
                    break;
                }

                if (prefixMatch == null && candidate.Name.StartsWith(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    prefixMatch = candidate;
                }
            }

            if (requested == null) requested = prefixMatch;
            if (requested == null)
            {
                error = "no Cinemachine stage component matches '" + typeName + "'.";
                return false;
            }

            // Replace the whole stage: Cinemachine components are exclusive per stage.
            foreach (Component existing in host.GetComponents(baseType))
            {
                if (existing == null) continue;
                if (ClassifyStage(existing) != stage) continue;
                UnityEngine.Object.DestroyImmediate(existing, true);
            }

            try
            {
                component = host.gameObject.AddComponent(requested);
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            if (component == null)
            {
                error = "AddComponent returned null for " + requested.Name + ".";
                return false;
            }

            return true;
        }

        internal static object NoiseComponent(object cinemachineCamera)
        {
            var host = cinemachineCamera as Component;
            if (host == null) return null;

            foreach (Component candidate in host.GetComponents<Component>())
            {
                if (candidate == null) continue;
                Type type = candidate.GetType();
                if (type.GetProperty("AmplitudeGain") != null) return candidate;
                if (type.GetField("AmplitudeGain") != null) return candidate;
                if (type.GetField("m_AmplitudeGain") != null) return candidate;
                if (type.GetField("m_Noise", BindingFlags.Instance | BindingFlags.NonPublic) != null
                    && type.Name.IndexOf("Noise", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }

            return null;
        }

        internal static bool EnsureNoise(
            object cinemachineCamera, out object noise, out bool added, out string error)
        {
            noise = null;
            added = false;
            error = null;

            object existing = NoiseComponent(cinemachineCamera);
            if (existing != null)
            {
                noise = existing;
                return true;
            }

            var host = cinemachineCamera as Component;
            if (host == null)
            {
                error = "the Cinemachine camera component is null.";
                return false;
            }

            Type noiseType = null;
            foreach (Type candidate in LoadedTypes())
            {
                if (candidate == null || candidate.IsAbstract || candidate.IsGenericTypeDefinition) continue;
                if (!typeof(Component).IsAssignableFrom(candidate)) continue;
                if (candidate.Name.IndexOf("Noise", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (candidate.GetProperty("AmplitudeGain") == null && candidate.GetField("m_AmplitudeGain") == null)
                {
                    continue;
                }

                noiseType = candidate;
                break;
            }

            if (noiseType == null)
            {
                error = "no Cinemachine noise component type was found in the loaded assemblies.";
                return false;
            }

            try
            {
                noise = host.gameObject.AddComponent(noiseType);
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            if (noise == null)
            {
                error = "AddComponent returned null for " + noiseType.Name + ".";
                return false;
            }

            added = true;
            return true;
        }

        // ---------------------------------------------------------------- extensions

        internal static IEnumerable<Component> Extensions(object cinemachineCamera)
        {
            var host = cinemachineCamera as Component;
            Type baseType = ExtensionBaseType();
            if (host == null || baseType == null) return Array.Empty<Component>();
            return host.GetComponents(baseType).Where(candidate => candidate != null);
        }

        internal static bool TryAddExtension(
            object cinemachineCamera,
            string typeName,
            out object extension,
            out bool alreadyAttached,
            out string error)
        {
            extension = null;
            alreadyAttached = false;
            error = null;

            Type baseType = ExtensionBaseType();
            var host = cinemachineCamera as Component;
            if (baseType == null || host == null)
            {
                error = "CinemachineExtension was not found; Cinemachine may not be installed correctly.";
                return false;
            }

            Type requested = null;
            foreach (Type candidate in LoadedTypes())
            {
                if (candidate == null || candidate.IsAbstract || candidate.IsGenericTypeDefinition) continue;
                if (candidate == baseType) continue;
                if (!baseType.IsAssignableFrom(candidate)) continue;
                if (string.Equals(candidate.Name, typeName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    requested = candidate;
                    break;
                }
            }

            if (requested == null)
            {
                error = "no CinemachineExtension type matches '" + typeName + "'.";
                return false;
            }

            foreach (Component existing in host.GetComponents(baseType))
            {
                if (existing == null) continue;
                if (existing.GetType() != requested) continue;
                extension = existing;
                alreadyAttached = true;
                return true;
            }

            try
            {
                extension = host.gameObject.AddComponent(requested);
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            if (extension == null)
            {
                error = "AddComponent returned null for " + requested.Name + ".";
                return false;
            }

            return true;
        }

        internal static bool TryRemoveExtensions(
            object cinemachineCamera, string typeName, out int removed, out string error)
        {
            removed = 0;
            error = null;

            Type baseType = ExtensionBaseType();
            var host = cinemachineCamera as Component;
            if (baseType == null || host == null)
            {
                error = "CinemachineExtension was not found; Cinemachine may not be installed correctly.";
                return false;
            }

            var matches = host.GetComponents(baseType)
                .Where(candidate => candidate != null)
                .Where(candidate => string.Equals(
                    candidate.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (Component match in matches)
            {
                UnityEngine.Object.DestroyImmediate(match, true);
                removed++;
            }

            return true;
        }

        // ---------------------------------------------------------------- targets / priority / lens

        internal static bool SetFollow(object cinemachineCamera, GameObject target)
        {
            return SetTargetMember(cinemachineCamera, "Follow", target);
        }

        internal static bool SetLookAt(object cinemachineCamera, GameObject target)
        {
            return SetTargetMember(cinemachineCamera, "LookAt", target);
        }

        private static bool SetTargetMember(object cinemachineCamera, string member, GameObject target)
        {
            if (cinemachineCamera == null || target == null) return false;

            string error;
            if (CucliReflection.TrySetMember(
                    cinemachineCamera, member, new JValue(target.name), out error))
            {
                return true;
            }

            // Cinemachine declares Follow / LookAt as Transform fields on some versions.
            Type type = cinemachineCamera.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo info = type.GetField(member, flags) ?? (MemberInfo)type.GetProperty(member, flags);
            if (info == null) return false;

            try
            {
                var field = info as FieldInfo;
                if (field != null)
                {
                    field.SetValue(cinemachineCamera, target.transform);
                    return true;
                }

                var property = (PropertyInfo)info;
                if (!property.CanWrite) return false;
                property.SetValue(cinemachineCamera, target.transform, null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool SetPriority(object cinemachineCamera, float priority)
        {
            if (cinemachineCamera == null) return false;

            Type type = cinemachineCamera.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo property = type.GetProperty("Priority", flags);
            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(cinemachineCamera, priority, null);
                    MarkDirty(cinemachineCamera);
                    return true;
                }
                catch (Exception)
                {
                    // Fall through to the serialized m_Value path.
                }
            }

            if (!CucliOptional.SetProperty(cinemachineCamera, "Priority", priority))
            {
                object priorityField = CucliOptional.GetField(cinemachineCamera, "Priority")
                    ?? CucliOptional.GetField(cinemachineCamera, "m_Priority");
                if (priorityField == null) return false;

                FieldInfo field = type.GetField(
                    priorityField is FieldInfo ? ((FieldInfo)priorityField).Name : "Priority", flags);
                if (field == null) return false;

                object value = field.GetValue(cinemachineCamera);
                if (value == null) return false;

                // Cinemachine stores Priority as a PrioritySettings struct with an m_Value field.
                FieldInfo inner = value.GetType().GetField("m_Value", flags);
                if (inner != null && inner.FieldType == typeof(float))
                {
                    inner.SetValue(value, priority);
                    field.SetValue(cinemachineCamera, value);
                    MarkDirty(cinemachineCamera);
                    return true;
                }

                if (field.FieldType == typeof(float))
                {
                    field.SetValue(cinemachineCamera, priority);
                    MarkDirty(cinemachineCamera);
                    return true;
                }

                return false;
            }

            MarkDirty(cinemachineCamera);
            return true;
        }

        /// <summary>Writes one member of the Cinemachine Lens struct.</summary>
        internal static bool SetLensField(GameObject host, string member, float value)
        {
            object camera = FindCamera(host);
            if (camera == null) return false;

            object lens = MemberOrNull(camera, "Lens");
            if (lens == null) return false;

            Type lensType = lens.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo field = lensType.GetField(member, flags);
            if (field != null && field.FieldType == typeof(float))
            {
                field.SetValue(lens, value);
                return WriteBackLens(camera, lens);
            }

            PropertyInfo property = lensType.GetProperty(member, flags);
            if (property != null && property.CanWrite && property.PropertyType == typeof(float))
            {
                property.SetValue(lens, value, null);
                return WriteBackLens(camera, lens);
            }

            return false;
        }

        private static bool WriteBackLens(object camera, object lens)
        {
            // The struct was boxed by the getter, so the modified copy must be written back explicitly.
            Type type = camera.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo info = type.GetField("Lens", flags) ?? (MemberInfo)type.GetProperty("Lens", flags);
            if (info == null) return false;

            try
            {
                var field = info as FieldInfo;
                if (field != null)
                {
                    field.SetValue(camera, lens);
                    return true;
                }

                var property = (PropertyInfo)info;
                if (property == null || !property.CanWrite) return false;
                property.SetValue(camera, lens, null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Reads a member only when the target is non-null, so alias chains stay safe.</summary>
        private static object MemberOrNull(object target, string name)
        {
            if (target == null) return null;
            return CucliOptional.GetProperty(target, name) ?? CucliOptional.GetField(target, name);
        }

        private static void MarkDirty(object target)
        {
            var unityObject = target as UnityEngine.Object;
            if (unityObject != null) EditorUtility.SetDirty(unityObject);
        }

        // ---------------------------------------------------------------- blend / override

        internal static bool SetDefaultBlendStyle(GameObject host, string styleName, out string error)
        {
            error = null;
            Component brain = FindBrain(host);
            if (brain == null)
            {
                error = "the GameObject has no CinemachineBrain.";
                return false;
            }

            object defaultBlend = MemberOrNull(brain, "DefaultBlend");
            if (defaultBlend == null)
            {
                error = "CinemachineBrain.DefaultBlend could not be read.";
                return false;
            }

            object style = MemberOrNull(defaultBlend, "Style");
            if (style == null)
            {
                error = "DefaultBlend.Style could not be read.";
                return false;
            }

            Type styleType = style.GetType();
            object parsed = null;
            foreach (string candidate in Enum.GetNames(styleType))
            {
                if (!string.Equals(candidate, styleName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                parsed = Enum.Parse(styleType, candidate);
                break;
            }

            if (parsed == null)
            {
                error = "'" + styleName + "' is not one of: " + string.Join(", ", Enum.GetNames(styleType)) + ".";
                return false;
            }

            if (!CucliOptional.SetProperty(defaultBlend, "Style", parsed)
                && !CucliOptional.SetProperty(brain, "DefaultBlend", defaultBlend))
            {
                error = "DefaultBlend is stored as a value type and could not be written back.";
                return false;
            }

            EditorUtility.SetDirty(brain);
            return true;
        }

        internal static bool SetDefaultBlendDuration(GameObject host, float duration, out string error)
        {
            error = null;
            Component brain = FindBrain(host);
            if (brain == null)
            {
                error = "the GameObject has no CinemachineBrain.";
                return false;
            }

            object defaultBlend = MemberOrNull(brain, "DefaultBlend");
            if (defaultBlend == null)
            {
                error = "CinemachineBrain.DefaultBlend could not be read.";
                return false;
            }

            if (!CucliOptional.SetProperty(defaultBlend, "Time", duration)
                && !CucliOptional.SetProperty(defaultBlend, "Duration", duration))
            {
                error = "DefaultBlend has no writable duration field.";
                return false;
            }

            // DefaultBlend is a struct field, so the modified copy has to be written back.
            var brainComponent = brain;
            FieldInfo field = brainComponent.GetType().GetField(
                "DefaultBlend", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(brainComponent, defaultBlend);
                EditorUtility.SetDirty(brainComponent);
                return true;
            }

            if (CucliOptional.SetProperty(brainComponent, "DefaultBlend", defaultBlend))
            {
                EditorUtility.SetDirty(brainComponent);
                return true;
            }

            error = "DefaultBlend could not be written back to the brain.";
            return false;
        }

        internal static bool TryForceCamera(
            object cinemachineCamera, out int overrideId, out string method, out string error)
        {
            overrideId = 0;
            method = null;
            error = null;

            Type type = cinemachineCamera.GetType();
            try
            {
                MethodInfo setOverride = type.GetMethod(
                    "SetActiveOverride",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { type, typeof(int) },
                    null);

                if (setOverride != null)
                {
                    overrideId = Math.Abs(Guid.NewGuid().GetHashCode() % 100000) + 1;
                    setOverride.Invoke(null, new[] { cinemachineCamera, (object)overrideId });
                    MarkDirty(cinemachineCamera);
                    method = "override";
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = (exception.InnerException ?? exception).GetBaseException().Message;
            }

            if (SetPriority(cinemachineCamera, 100f))
            {
                overrideId = 0;
                method = "priority";
                return true;
            }

            error = error ?? "neither a Cinemachine override API nor a writable priority was found.";
            return false;
        }

        internal static bool TryReleaseOverride(Component brain, out int released, out string error)
        {
            released = 0;
            error = null;
            if (brain == null) return false;

            try
            {
                MethodInfo release = brain.GetType().GetMethod(
                    "ReleaseActiveVirtualCamera",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (release == null) return false;

                ParameterInfo[] parameters = release.GetParameters();
                object[] arguments = parameters.Length == 0 ? null : new object[] { brain };
                object result = release.Invoke(null, arguments);
                if (result is bool && !(bool)result) return false;

                released = 0;
                return true;
            }
            catch (Exception exception)
            {
                error = (exception.InnerException ?? exception).GetBaseException().Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- presets

        private static readonly string[] Presets =
        {
            "follow", "third_person", "freelook", "dolly", "static", "top_down", "side_scroller"
        };

        internal static bool IsKnownPreset(string preset, out string error)
        {
            error = null;
            if (Array.IndexOf(Presets, preset) >= 0) return true;
            error = "'" + preset + "' is not one of: " + string.Join(", ", Presets) + ".";
            return false;
        }

        /// <summary>
        /// Applies a preset. Dolly requires a dolly track in the scene and the remaining presets map
        /// onto a body/aim pair that Cinemachine expresses with the standard components; the returned
        /// string names what was actually applied so the caller never reports a preset it did not set.
        /// </summary>
        internal static string ApplyPreset(object cinemachineCamera, string preset)
        {
            switch (preset)
            {
                case "static":
                    RemoveStage(cinemachineCamera, "Body");
                    RemoveStage(cinemachineCamera, "Aim");
                    return "static";

                case "freelook":
                {
                    var host = cinemachineCamera as Component;
                    if (host == null) return preset;

                    Type freeLook = FirstType(new[]
                    {
                        "Unity.Cinemachine.CinemachineFreeLook",
                        "Cinemachine.CinemachineFreeLook"
                    });
                    if (freeLook != null && freeLook.IsInstanceOfType(host))
                    {
                        // A FreeLook camera owns its own rigs; leaving the stages alone is correct.
                        return "freelook";
                    }

                    AddStage(cinemachineCamera, "Body", new[] { "CinemachineOrbitalFollow", "CinemachineTransposer" });
                    AddStage(cinemachineCamera, "Aim", new[] { "CinemachineRotationComposer", "CinemachineComposer" });
                    return "freelook";
                }

                case "dolly":
                {
                    bool added = AddStage(cinemachineCamera, "Body", new[]
                    {
                        "CinemachineTrackedDolly", "CinemachineFollow", "CinemachineTransposer"
                    });
                    AddStage(cinemachineCamera, "Aim", new[] { "CinemachineComposer", "CinemachineRotationComposer" });
                    return added ? "dolly" : "follow";
                }

                case "third_person":
                {
                    AddStage(cinemachineCamera, "Body", new[]
                    {
                        "CinemachineThirdPersonFollow", "Cinemachine3rdPersonFollow", "CinemachineTransposer"
                    });
                    AddStage(cinemachineCamera, "Aim", new[] { "CinemachineRotationComposer", "CinemachineComposer" });
                    return "third_person";
                }

                case "top_down":
                {
                    AddStage(cinemachineCamera, "Body", new[] { "CinemachineTransposer", "CinemachineFollow" });
                    AddStage(cinemachineCamera, "Aim", new[] { "CinemachineHardLookAt", "CinemachineComposer" });
                    return "top_down";
                }

                case "side_scroller":
                {
                    AddStage(cinemachineCamera, "Body", new[] { "CinemachineTransposer", "CinemachineFollow" });
                    AddStage(cinemachineCamera, "Aim", new[] { "CinemachineComposer", "CinemachineRotationComposer" });
                    return "side_scroller";
                }

                default:
                {
                    AddStage(cinemachineCamera, "Body", new[] { "CinemachineTransposer", "CinemachineFollow" });
                    AddStage(cinemachineCamera, "Aim", new[] { "CinemachineRotationComposer", "CinemachineComposer" });
                    return "follow";
                }
            }
        }

        private static bool AddStage(object cinemachineCamera, string stage, string[] candidateNames)
        {
            if (StageComponent(cinemachineCamera, stage) != null) return true;

            foreach (string name in candidateNames)
            {
                object component;
                string error;
                if (TryReplaceStage(cinemachineCamera, stage, name, out component, out error)) return true;
            }

            return false;
        }

        private static void RemoveStage(object cinemachineCamera, string stage)
        {
            var host = cinemachineCamera as Component;
            Type baseType = ComponentBaseType();
            if (host == null || baseType == null) return;

            foreach (Component existing in host.GetComponents(baseType))
            {
                if (existing == null) continue;
                if (ClassifyStage(existing) != stage) continue;
                UnityEngine.Object.DestroyImmediate(existing, true);
            }
        }

        // ---------------------------------------------------------------- type sweeps

        private static IEnumerable<Type> LoadedTypes()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name ?? string.Empty;
                if (name.IndexOf("Cinemachine", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0) continue;

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
                    if (type != null) yield return type;
                }
            }
        }
    }

    /// <summary>Best-effort textual description of a Cinemachine blend definition.</summary>
    internal static class CucliDescription
    {
        internal static string Describe(object value)
        {
            if (value == null) return null;

            object style = CucliOptional.GetField(value, "Style") ?? CucliOptional.GetProperty(value, "Style");
            object time = CucliOptional.GetField(value, "Time")
                ?? CucliOptional.GetProperty(value, "Time")
                ?? CucliOptional.GetField(value, "Duration")
                ?? CucliOptional.GetProperty(value, "Duration");

            if (style == null && time == null) return CucliDescribe.SafeToString(value);
            return (style == null ? "?" : style.ToString())
                + " over " + (time == null ? "?" : time.ToString()) + "s";
        }
    }
}
