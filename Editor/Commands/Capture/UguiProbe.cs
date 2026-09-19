using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// uGUI raycasting, reached entirely through reflection.
    ///
    /// <c>EventSystems</c> and <c>UI</c> live in the <c>UnityEngine.UI</c> assembly, which this package's
    /// asmdef deliberately does not reference: <c>com.unity.ugui</c> is only an implicit default package,
    /// so a hard <c>using UnityEngine.UI;</c> would break compilation in a project that removed it. Since
    /// the package promises to compile anywhere, inspection has to be reflective - the same rule the
    /// optional 3D packages already follow.
    ///
    /// The cost is that every failure mode has to be named rather than thrown, which is why the callers
    /// get a reason string instead of an exception.
    /// </summary>
    internal static class UguiProbe
    {
        private const string UiAssembly = "UnityEngine.UI";
        private const string EventSystemType = "UnityEngine.EventSystems.EventSystem";
        private const string PointerEventDataType = "UnityEngine.EventSystems.PointerEventData";
        private const string RaycastResultType = "UnityEngine.EventSystems.RaycastResult";
        private const string GraphicRaycasterType = "UnityEngine.UI.GraphicRaycaster";
        private const string GraphicType = "UnityEngine.UI.Graphic";

        internal static bool IsAvailable
        {
            get
            {
                return CucliOptional.HasType(UiAssembly, EventSystemType)
                    && CucliOptional.HasType(UiAssembly, RaycastResultType);
            }
        }

        internal static string UnavailableReason
        {
            get
            {
                if (CucliOptional.HasType(UiAssembly, EventSystemType)
                    && CucliOptional.HasType(UiAssembly, RaycastResultType))
                {
                    return null;
                }

                return "uGUI is not present: UnityEngine.UI (com.unity.ugui) does not expose "
                    + "EventSystem and RaycastResult, so UI hit-testing is unavailable. UI Toolkit elements are "
                    + "still probed when a UIDocument exists.";
            }
        }

        /// <summary>
        /// Raycasts a screen point against uGUI and returns the hits, deepest first.
        ///
        /// Prefers a live <c>EventSystem</c> and falls back to driving each <c>GraphicRaycaster</c>
        /// directly, so a scene with a Canvas but no EventSystem still answers instead of reporting
        /// "nothing under the pointer", which would be a false negative.
        /// </summary>
        internal static bool TryRaycast(
            Vector2 screenPoint,
            int maxResults,
            out JArray hits,
            out string source,
            out string error)
        {
            hits = new JArray();
            source = null;
            error = null;

            Type eventSystemType = CucliOptional.FindType(UiAssembly, EventSystemType);
            Type pointerDataType = CucliOptional.FindType(UiAssembly, PointerEventDataType);
            Type raycastResultType = CucliOptional.FindType(UiAssembly, RaycastResultType);

            if (eventSystemType == null || pointerDataType == null || raycastResultType == null)
            {
                error = UnavailableReason;
                return false;
            }

            object eventSystem = ResolveEventSystem(eventSystemType);

            object pointerData;
            try
            {
                pointerData = Activator.CreateInstance(pointerDataType, new[] { eventSystem });
            }
            catch (Exception exception)
            {
                error = "PointerEventData could not be constructed: " + exception.GetBaseException().Message;
                return false;
            }

            if (!CucliOptional.SetProperty(pointerData, "position", screenPoint))
            {
                error = "PointerEventData.position could not be set, so the raycast would use a stale point.";
                return false;
            }

            IList results;
            try
            {
                Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(raycastResultType);
                results = (IList)Activator.CreateInstance(listType);
            }
            catch (Exception exception)
            {
                error = "Could not create a RaycastResult list: " + exception.GetBaseException().Message;
                return false;
            }

            if (eventSystem != null)
            {
                MethodInfo raycastAll = eventSystemType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        return method.Name == "RaycastAll" && parameters.Length == 2;
                    });

                if (raycastAll == null)
                {
                    error = "EventSystem.RaycastAll was not found; this Unity version lays out uGUI differently.";
                    return false;
                }

                try
                {
                    raycastAll.Invoke(eventSystem, new[] { pointerData, (object)results });
                    source = "EventSystem.RaycastAll";
                }
                catch (Exception exception)
                {
                    error = "EventSystem raycast failed: " + exception.GetBaseException().Message;
                    return false;
                }
            }
            else
            {
                Type raycasterType = CucliOptional.FindType(UiAssembly, GraphicRaycasterType);
                if (raycasterType == null)
                {
                    error = "No EventSystem is active and GraphicRaycaster is unavailable, so nothing can be hit-tested. "
                        + "Add an EventSystem to the scene.";
                    return false;
                }

                MethodInfo raycast = raycasterType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        return method.Name == "Raycast" && parameters.Length == 2;
                    });

                if (raycast == null)
                {
                    error = "GraphicRaycaster.Raycast was not found; this Unity version lays out uGUI differently.";
                    return false;
                }

                int scanned = 0;
                foreach (Object candidate in Object.FindObjectsOfType(raycasterType))
                {
                    var behaviour = candidate as Behaviour;
                    if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
                    scanned++;
                    try
                    {
                        raycast.Invoke(candidate, new[] { pointerData, (object)results });
                    }
                    catch (Exception)
                    {
                        // A raycaster whose Canvas is not fully set up is skipped rather than fatal.
                    }
                }

                source = "GraphicRaycaster scan (" + scanned + " raycaster(s), no active EventSystem)";
            }

            var ordered = results.Cast<object>()
                .OrderByDescending(item => ReadInt(item, "depth"))
                .Take(Mathf.Clamp(maxResults, 1, 64))
                .ToArray();

            foreach (object item in ordered)
            {
                hits.Add(Describe(item));
            }

            return true;
        }

        private static object ResolveEventSystem(Type eventSystemType)
        {
            object current = CucliOptional.GetStaticProperty(eventSystemType, "current");
            if (current != null) return current;

            // EventSystem.current is only assigned while an EventSystem is enabled, so a disabled-but-present
            // one still needs finding before giving up and falling back to a raycaster scan.
            foreach (Object candidate in Object.FindObjectsOfType(eventSystemType))
            {
                var behaviour = candidate as Behaviour;
                if (behaviour != null && behaviour.isActiveAndEnabled) return candidate;
            }

            return null;
        }

        private static JObject Describe(object raycastResult)
        {
            var entry = new JObject();

            object gameObject = CucliOptional.GetField(raycastResult, "gameObject");
            var target = gameObject as GameObject;
            entry["gameObject"] = target == null ? null : target.name;
            entry["path"] = target == null ? null : CucliObjectResolver.HierarchyPath(target, true);
            entry["instanceId"] = target == null ? 0 : target.GetInstanceID();

            entry["depth"] = ReadInt(raycastResult, "depth");
            entry["sortingLayer"] = ReadInt(raycastResult, "sortingLayer");
            entry["sortingOrder"] = ReadInt(raycastResult, "sortingOrder");
            entry["distance"] = ReadFloat(raycastResult, "distance");
            entry["worldPosition"] = CucliDescribe.Value(ReadField(raycastResult, "worldPosition"), null, 0);
            entry["screenPosition"] = CucliDescribe.Value(ReadField(raycastResult, "screenPosition"), null, 0);

            Type graphicType = CucliOptional.FindType(UiAssembly, GraphicType);
            if (target != null && graphicType != null)
            {
                Component graphic = target.GetComponent(graphicType);
                if (graphic != null)
                {
                    entry["graphicType"] = graphic.GetType().Name;
                    entry["raycastTarget"] = CucliDescribe.Value(
                        CucliOptional.GetProperty(graphic, "raycastTarget"), null, 0);

                    // A label hit needs its label summarised, and only through the allow-list: reading a
                    // TextMeshProUGUI wholesale would pull in textInfo, mesh vertices and UVs.
                    if (TextComponentSummary.TryDescribe(graphic, out JObject text, out string _))
                    {
                        entry["text"] = text;
                    }
                }
            }

            return entry;
        }

        private static object ReadField(object target, string name)
        {
            return target == null ? null : CucliOptional.GetField(target, name);
        }

        private static int ReadInt(object target, string name)
        {
            object value = ReadField(target, name);
            if (value == null) return 0;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static float ReadFloat(object target, string name)
        {
            object value = ReadField(target, name);
            if (value == null) return 0f;
            try
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0f;
            }
        }
    }
}
