using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli input ...</c> - see what is under the pointer, and drive input into the Editor or the game.
    ///
    /// Two halves with very different reliability, and every result says which one answered.
    ///
    /// <b>Inspection is exact.</b> <c>pointer-inspect</c> reports the UI element hit by a real raycast,
    /// the Scene-view object under the cursor, and a physics hit in the game view. UI labels are
    /// summarised through <see cref="TextComponentSummary"/>'s allow-list - never by reading a
    /// <c>TextMeshProUGUI</c> wholesale, which would drag in <c>textInfo</c>, mesh vertices, UVs and
    /// material instances.
    ///
    /// <b>Injection is best-effort and says so.</b> There are two backends, because they reach different
    /// targets and neither one covers both:
    /// <list type="bullet">
    /// <item><c>editor-event</c> - synthesises a <see cref="Event"/> and hands it to an Editor window.
    /// This drives Editor UI, and needs no package at all.</item>
    /// <item><c>inputsystem</c> - queues a real device state into the Input System. This is what
    /// gameplay code observes, and it only means anything in Play Mode.</item>
    /// </list>
    /// When a backend cannot work, the failure names the missing piece instead of returning a success
    /// that did nothing.
    /// </summary>
    internal sealed class InputCommand : CucliCommand, ICucliDeferredCommand
    {
        private const string InputSystemAssembly = "Unity.InputSystem";
        private const string InputSystemType = "UnityEngine.InputSystem.InputSystem";
        private const string MouseType = "UnityEngine.InputSystem.Mouse";
        private const string MouseStateType = "UnityEngine.InputSystem.LowLevel.MouseState";

        /// <summary>MouseState.buttons bit layout: bit 0 left, bit 1 right, bit 2 middle.</summary>
        private const ushort LeftButton = 1 << 0;
        private const ushort RightButton = 1 << 1;
        private const ushort MiddleButton = 1 << 2;

        private const string BackendAuto = "auto";
        private const string BackendInputSystem = "inputsystem";
        private const string BackendEditorEvent = "editor-event";

        public override string Name => "input";

        public override string Summary =>
            "Inspect the pointer and the UI or world object under it, and synthesise mouse, key or scroll input.";

        public override CucliActionInfo[] Actions => Declare(
            "pointer-inspect", "What is under the pointer: UI elements with a safe text summary, the Scene-view object, and a game-view physics hit.",
            "move", "Move the pointer to a screen point.",
            "click", "Press and release a mouse button at a screen point, across separate frames.",
            "drag", "Press at a start point, move through waypoints, release.",
            "scroll", "Send a scroll delta.",
            "key", "Press and release a key, as an Editor key event.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "editor.input.write", "editor.input.read" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""pointer-inspect"", ""move"", ""click"", ""drag"", ""scroll"", ""key""],
      ""default"": ""pointer-inspect""
    },
    ""x"": { ""type"": ""number"", ""description"": ""Screen X. Origin is bottom-left, matching Screen and Input coordinates."" },
    ""y"": { ""type"": ""number"", ""description"": ""Screen Y measured from the bottom."" },
    ""button"": { ""type"": ""string"", ""enum"": [""left"", ""right"", ""middle""], ""default"": ""left"" },
    ""points"": {
      ""type"": ""array"",
      ""description"": ""drag: waypoints as {x,y} or [x,y]. Also accepts @file.json containing a 'points' array."",
      ""items"": { ""type"": ""object"" }
    },
    ""deltaX"": { ""type"": ""number"", ""default"": 0 },
    ""deltaY"": { ""type"": ""number"", ""default"": 0 },
    ""scrollX"": { ""type"": ""number"", ""default"": 0 },
    ""scrollY"": { ""type"": ""number"", ""default"": 120 },
    ""key"": { ""type"": ""string"", ""description"": ""key: a KeyCode or Event.KeyboardEvent name such as Space, Return, LeftShift, a."" },
    ""backend"": {
      ""type"": ""string"", ""enum"": [""auto"", ""inputsystem"", ""editor-event""], ""default"": ""auto"",
      ""description"": ""Which injection backend to use. auto prefers editor-event, and uses inputsystem in Play Mode when asked for explicitly.""
    },
    ""targetWindow"": { ""type"": ""string"", ""description"": ""editor-event: Editor window type or title to receive the event. Defaults to the focused window, or the active Scene view for pointer gestures."" },
    ""holdFrames"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 120, ""default"": 1, ""description"": ""Frames to hold a button down before releasing."" },
    ""settleFrames"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 120, ""default"": 2, ""description"": ""Frames to wait after injection so the target can react."" },
    ""maxResults"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 64, ""default"": 16 },
    ""includeText"": { ""type"": ""boolean"", ""default"": true, ""description"": ""pointer-inspect: summarise text components. Always through the field allow-list."" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "pointer-inspect"))
            {
                case "pointer-inspect": return PointerInspect(context);
                case "move": return Move(context);
                case "scroll": return Scroll(context);
                case "click":
                case "drag":
                case "key":
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "action '" + ResolveAction(context, "pointer-inspect")
                        + "' spans frames and must run through the deferred path.");
                default:
                    return CucliCommandResult.Unsupported(
                        ResolveAction(context, "pointer-inspect"), ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            switch (ResolveAction(context, "pointer-inspect"))
            {
                case "click": Click(context, complete); return;
                case "drag": Drag(context, complete); return;
                case "key": Key(context, complete); return;
                default: complete(Execute(context)); return;
            }
        }

        // ------------------------------------------------------------------ pointer position

        /// <summary>
        /// Reads the pointer from whichever backend is live. The legacy <c>Input</c> class throws when
        /// Active Input Handling is set to the new Input System only, so it is probed, never assumed.
        /// </summary>
        private static bool TryReadPointer(out Vector2 position, out string source, out string error)
        {
            position = Vector2.zero;
            source = null;
            error = null;

            object mouse = MouseDevice();
            if (mouse != null)
            {
                object positionControl = CucliOptional.GetProperty(mouse, "position");
                if (positionControl != null)
                {
                    object read;
                    string invokeError;
                    if (CucliReflection.TryInvoke(positionControl, "ReadValue", null, out read, out invokeError)
                        && read is Vector2 vector)
                    {
                        position = vector;
                        source = "InputSystem.Mouse.current.position";
                        return true;
                    }
                }
            }

            try
            {
                position = Input.mousePosition;
                source = "UnityEngine.Input.mousePosition";
                return true;
            }
            catch (Exception exception)
            {
                error = "No readable pointer: the Input System mouse is unavailable and the legacy Input class threw ("
                    + exception.GetBaseException().Message + "). This usually means Active Input Handling is set to "
                    + "the new Input System only.";
                return false;
            }
        }

        private static object MouseDevice()
        {
            Type mouseType = CucliOptional.FindType(InputSystemAssembly, MouseType);
            return mouseType == null ? null : CucliOptional.GetStaticProperty(mouseType, "current");
        }

        // ------------------------------------------------------------------ pointer-inspect

        private static CucliCommandResult PointerInspect(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            int maxResults = Mathf.Clamp(args.GetInt("maxResults", 16), 1, 64);
            bool includeText = args.GetBool("includeText", true);

            Vector2 position;
            string source;
            string readError;
            bool read = TryReadPointer(out position, out source, out readError);
            if (!read) position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            var result = new JObject
            {
                ["pointer"] = new JObject
                {
                    ["position"] = CucliDescribe.Value(position, null, 0),
                    ["source"] = source,
                    ["positionIsFallback"] = !read
                },
                ["screen"] = CaptureSupport.ScreenJson(),
                ["playing"] = EditorApplication.isPlaying,
                ["hoveredEditorWindow"] = DescribeWindow(SafeMouseOverWindow()),
                ["focusedEditorWindow"] = DescribeWindow(EditorWindow.focusedWindow)
            };

            if (!read) context.Warn(readError);

            // --- uGUI -------------------------------------------------------------------------
            JArray uiHits;
            string uiSource;
            string uiError;
            if (UguiProbe.TryRaycast(position, maxResults, out uiHits, out uiSource, out uiError))
            {
                if (!includeText) StripText(uiHits);
                result["ugui"] = new JObject
                {
                    ["source"] = uiSource,
                    ["hitCount"] = uiHits.Count,
                    ["hits"] = uiHits
                };
            }
            else
            {
                result["ugui"] = new JObject { ["source"] = "unavailable", ["hits"] = new JArray() };
                context.Warn(uiError);
            }

            // --- UI Toolkit -------------------------------------------------------------------
            JArray toolkitHits;
            string toolkitNote;
            ProbeToolkit(position, maxResults, includeText, out toolkitHits, out toolkitNote);
            result["uiToolkit"] = new JObject
            {
                ["hits"] = toolkitHits,
                ["hitCount"] = toolkitHits.Count,
                ["note"] = toolkitNote
            };

            // --- Scene view pick --------------------------------------------------------------
            // HandleUtility.PickGameObject is the Editor's own scene picking, so it answers for the
            // Scene view exactly as a click there would.
            try
            {
                GameObject picked = HandleUtility.PickGameObject(position, false);
                result["sceneViewPick"] = picked == null
                    ? null
                    : new JObject
                    {
                        ["gameObject"] = picked.name,
                        ["path"] = CucliObjectResolver.HierarchyPath(picked, true),
                        ["instanceId"] = picked.GetInstanceID(),
                        ["components"] = SummariseComponents(picked, includeText, maxResults)
                    };
            }
            catch (Exception exception)
            {
                result["sceneViewPick"] = null;
                context.Warn("Scene view picking failed: " + exception.GetBaseException().Message);
            }

            // --- Game view physics ------------------------------------------------------------
            result["gameViewPhysics"] = PhysicsHit(position, maxResults);

            result["note"] = "Three independent sources are reported because they answer different questions: "
                + "ugui/uiToolkit are UI hit-tests, sceneViewPick is the Editor's own Scene view picking, and "
                + "gameViewPhysics is a raycast from the main camera through the game view. A source that is "
                + "empty or unavailable is reported as such rather than as 'nothing is under the pointer'. "
                + "Text components are summarised through a fixed field allow-list; TextMeshPro runtime data "
                + "(textInfo, mesh vertices, UVs, character info) is never read.";

            bool anything = uiHits.Count > 0
                || toolkitHits.Count > 0
                || result["sceneViewPick"] != null
                || result["gameViewPhysics"] != null;

            if (!anything)
            {
                context.Warn("No UI element, Scene view object or game view collider was found under the pointer. "
                    + "That can be legitimate: the point may be over empty space, the Editor window may not be "
                    + "focused, or the scene may simply have nothing there.");
            }

            return CucliCommandResult.Ok(result);
        }

        private static void StripText(JArray hits)
        {
            foreach (JToken hit in hits)
            {
                var entry = hit as JObject;
                if (entry != null) entry.Remove("text");
            }
        }

        private static EditorWindow SafeMouseOverWindow()
        {
            try
            {
                return EditorWindow.mouseOverWindow;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static JObject DescribeWindow(EditorWindow window)
        {
            if (window == null) return null;
            return new JObject
            {
                ["type"] = window.GetType().Name,
                ["title"] = window.titleContent == null ? null : window.titleContent.text
            };
        }

        /// <summary>
        /// Summary of a GameObject's components. Text components go through the allow-list; everything
        /// else is reduced to its type so a prefab with fifty scripts cannot flood the response.
        /// </summary>
        private static JArray SummariseComponents(GameObject gameObject, bool includeText, int maxResults)
        {
            var components = new JArray();
            if (gameObject == null) return components;

            Component[] all = gameObject.GetComponents<Component>();
            int emitted = 0;
            foreach (Component component in all)
            {
                if (component == null || emitted >= maxResults) break;

                if (includeText && TextComponentSummary.TryDescribe(component, out JObject text, out string _))
                {
                    components.Add(text);
                    emitted++;
                    continue;
                }

                components.Add(new JObject
                {
                    ["componentType"] = component.GetType().Name,
                    ["enabled"] = component is Behaviour behaviour ? (JToken)behaviour.enabled : JValue.CreateNull()
                });
                emitted++;
            }

            if (all.Length > emitted) components.Add(new JObject { ["$truncated"] = all.Length - emitted });
            return components;
        }

        private static JToken PhysicsHit(Vector2 screenPoint, int maxResults)
        {
            Camera camera = Camera.main;
            if (camera == null) return null;

            try
            {
                Ray ray = camera.ScreenPointToRay(screenPoint);
                RaycastHit[] hits = Physics.RaycastAll(ray, camera.farClipPlane);
                if (hits.Length == 0) return null;

                var array = new JArray();
                foreach (RaycastHit hit in hits.OrderBy(item => item.distance).Take(maxResults))
                {
                    array.Add(new JObject
                    {
                        ["gameObject"] = hit.collider == null ? null : hit.collider.gameObject.name,
                        ["path"] = hit.collider == null
                            ? null
                            : CucliObjectResolver.HierarchyPath(hit.collider.gameObject, true),
                        ["instanceId"] = hit.collider == null ? 0 : hit.collider.gameObject.GetInstanceID(),
                        ["distance"] = hit.distance,
                        ["point"] = CucliDescribe.Value(hit.point, null, 0)
                    });
                }

                return new JObject { ["camera"] = camera.name, ["hitCount"] = array.Count, ["hits"] = array };
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ UI Toolkit probe

        private static void ProbeToolkit(
            Vector2 screenPoint, int maxResults, bool includeText, out JArray hits, out string note)
        {
            hits = new JArray();
            note = null;

            Type panelUtils = ResolveType("UnityEngine.UIElements.RuntimePanelUtils");
            Type documentType = ResolveType("UnityEngine.UIElements.UIDocument");
            if (documentType == null)
            {
                note = "UIDocument is not available in this Editor, so UI Toolkit picking was skipped.";
                return;
            }

            int documents = 0;
            foreach (Object candidate in Object.FindObjectsOfType(documentType))
            {
                var document = candidate as Component;
                if (document == null) continue;
                documents++;

                object root = CucliOptional.GetProperty(document, "rootVisualElement");
                object panel = root == null ? null : CucliOptional.GetProperty(root, "panel");
                if (panel == null) continue;

                // UI Toolkit screen space has its origin at the top-left while Screen reports from the
                // bottom-left, so the Y axis is flipped before any conversion.
                var topLeft = new Vector2(screenPoint.x, Screen.height - screenPoint.y);
                Vector2 panelPoint = topLeft;
                string convertNote;
                if (!TryScreenToPanel(panelUtils, panel, topLeft, out panelPoint, out convertNote) && convertNote != null)
                {
                    note = convertNote;
                }

                object picked;
                string pickError;
                if (!TryPick(panel, root, panelPoint, out picked, out pickError) || picked == null) continue;

                var entry = new JObject
                {
                    ["document"] = document.name,
                    ["documentInstanceId"] = document.GetInstanceID(),
                    ["element"] = CucliDescribe.SafeToString(picked),
                    ["elementType"] = picked.GetType().Name,
                    ["panelPoint"] = CucliDescribe.Value(panelPoint, null, 0)
                };

                if (includeText)
                {
                    string label = CucliOptional.GetProperty(picked, "text") as string;
                    if (label != null) entry["text"] = CucliDescribe.Truncate(label, 512);
                }

                hits.Add(entry);
                if (hits.Count >= maxResults) break;
            }

            if (note == null)
            {
                note = documents == 0
                    ? "No UIDocument in the loaded scenes, so there is no runtime UI Toolkit panel to pick from."
                    : "Probed " + documents + " UIDocument panel(s).";
            }
        }

        private static Type ResolveType(string fullName)
        {
            return CucliOptional.FindTypesByFullName(fullName).FirstOrDefault();
        }

        private static bool TryScreenToPanel(
            Type panelUtils, object panel, Vector2 topLeft, out Vector2 panelPoint, out string note)
        {
            panelPoint = topLeft;
            note = null;
            if (panelUtils == null) return false;

            MethodInfo convert = panelUtils
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "ScreenToPanel" && method.GetParameters().Length == 2);

            if (convert == null)
            {
                note = "RuntimePanelUtils.ScreenToPanel was not found, so UI Toolkit picking used raw top-left "
                    + "screen coordinates and may miss.";
                return false;
            }

            try
            {
                object converted = convert.Invoke(null, new[] { panel, (object)topLeft });
                if (converted is Vector2 vector)
                {
                    panelPoint = vector;
                    return true;
                }
            }
            catch (Exception exception)
            {
                note = "ScreenToPanel failed (" + exception.GetBaseException().Message
                    + "); UI Toolkit picking used raw top-left screen coordinates and may miss.";
                return false;
            }

            return false;
        }

        /// <summary>
        /// Picking is reflective on purpose: <c>IPanel.Pick</c>, <c>PickAll</c> and
        /// <c>VisualElement.Pick</c> have moved between Unity versions, and a wrong hard reference would
        /// break compilation in a package that has to build everywhere.
        /// </summary>
        private static bool TryPick(object panel, object root, Vector2 point, out object picked, out string error)
        {
            picked = null;
            error = null;
            var failures = new List<string>();

            foreach (object target in new[] { panel, root })
            {
                if (target == null) continue;

                MethodInfo[] candidates;
                try
                {
                    candidates = target.GetType()
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(method => method.Name == "Pick" || method.Name == "PickAll")
                        .Where(method =>
                        {
                            ParameterInfo[] parameters = method.GetParameters();
                            return parameters.Length >= 1 && parameters[0].ParameterType == typeof(Vector2);
                        })
                        .OrderBy(method => method.GetParameters().Length)
                        .ToArray();
                }
                catch (Exception exception)
                {
                    failures.Add(target.GetType().Name + ": " + exception.GetBaseException().Message);
                    continue;
                }

                foreach (MethodInfo candidate in candidates)
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    try
                    {
                        if (parameters.Length == 1)
                        {
                            picked = candidate.Invoke(target, new[] { (object)point });
                        }
                        else
                        {
                            var list = (System.Collections.IList)Activator.CreateInstance(parameters[1].ParameterType);
                            candidate.Invoke(target, new[] { (object)point, list });
                            if (list.Count > 0) picked = list[list.Count - 1];
                        }

                        if (picked != null) return true;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(candidate.Name + "/" + parameters.Length + ": "
                            + exception.GetBaseException().Message);
                    }
                }
            }

            error = failures.Count > 0
                ? string.Join("; ", failures)
                : "no Pick overload taking a Vector2 was found on the panel or its visual tree";
            return false;
        }

        // ------------------------------------------------------------------ backends

        /// <summary>What the requested backend can actually do right now, and why not when it cannot.</summary>
        private sealed class BackendChoice
        {
            internal string Name;
            internal object Device;
            internal EditorWindow Window;
            internal MethodInfo SendEvent;
            internal string FailureCode;
            internal string FailureMessage;
        }

        /// <summary>
        /// Picks a backend.
        ///
        /// <c>auto</c> prefers <c>editor-event</c> because it always exists and reaches Editor UI; the
        /// Input System is chosen only when the caller asks for it, since it is the gameplay path and is
        /// meaningless outside Play Mode.
        /// </summary>
        private static BackendChoice ResolveBackend(CucliArgs args)
        {
            string requested = (args.GetString("backend") ?? BackendAuto).Trim().ToLowerInvariant();
            var choice = new BackendChoice();

            if (requested == BackendAuto) requested = BackendEditorEvent;

            if (requested == BackendInputSystem)
            {
                object mouse = MouseDevice();
                Type mouseState = CucliOptional.FindType(InputSystemAssembly, MouseStateType);
                Type inputSystem = CucliOptional.FindType(InputSystemAssembly, InputSystemType);

                if (mouse == null || mouseState == null || inputSystem == null)
                {
                    choice.FailureCode = CucliErrors.OptionalPackageMissing;
                    choice.FailureMessage = "The inputsystem backend needs the Input System package and a live mouse "
                        + "device. Install 'com.unity.inputsystem', or use backend=editor-event to drive Editor UI.";
                    return choice;
                }

                if (!EditorApplication.isPlaying)
                {
                    choice.FailureCode = CucliErrors.WrongMode;
                    choice.FailureMessage = "The inputsystem backend queues real device state, which only gameplay code "
                        + "can observe, so it needs Play Mode. Start it with 'cucli editor play', or use "
                        + "backend=editor-event to drive Editor UI.";
                    return choice;
                }

                choice.Name = BackendInputSystem;
                choice.Device = mouse;
                return choice;
            }

            if (requested != BackendEditorEvent)
            {
                choice.FailureCode = CucliErrors.InvalidParameter;
                choice.FailureMessage = "'" + requested + "' is not a backend; use auto, editor-event or inputsystem.";
                return choice;
            }

            // Editor event synthesis needs EditorWindow.SendEvent, which is not part of the public API.
            // Reaching it reflectively is the only way, and when it is absent the caller is told exactly
            // that rather than being handed a silent no-op.
            Type windowType = typeof(EditorWindow);
            MethodInfo sendEvent = windowType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return method.Name == "SendEvent" && parameters.Length == 1
                        && parameters[0].ParameterType == typeof(Event);
                });

            if (sendEvent == null)
            {
                choice.FailureCode = CucliErrors.CommandFailed;
                choice.FailureMessage = "This Unity version does not expose EditorWindow.SendEvent, so an Event cannot be "
                    + "handed to an Editor window. Use backend=inputsystem in Play Mode for gameplay input.";
                return choice;
            }

            EditorWindow window = ResolveTargetWindow(args);
            if (window == null)
            {
                choice.FailureCode = CucliErrors.TargetNotFound;
                choice.FailureMessage = "No Editor window is available to receive the event. Focus a window, or pass "
                    + "--target-window <type|title>.";
                return choice;
            }

            choice.Name = BackendEditorEvent;
            choice.Window = window;
            choice.SendEvent = sendEvent;
            return choice;
        }

        private static EditorWindow ResolveTargetWindow(CucliArgs args)
        {
            string requested = args.GetString("targetWindow") ?? args.GetString("target_window");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                {
                    if (window == null) continue;
                    if (string.Equals(window.GetType().Name, requested, StringComparison.OrdinalIgnoreCase)) return window;
                    if (window.titleContent != null
                        && string.Equals(window.titleContent.text, requested, StringComparison.OrdinalIgnoreCase))
                    {
                        return window;
                    }
                }

                return null;
            }

            // A pointer gesture almost always targets the Scene view, so prefer it over whatever happens
            // to hold focus; a caller that wants something else names it.
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null) return sceneView;

            return EditorWindow.focusedWindow ?? EditorWindow.mouseOverWindow;
        }

        private static bool TryDispatch(CucliArgs args, Event evt, out string error)
        {
            error = null;
            BackendChoice choice = ResolveBackend(args);
            if (choice.Name == null)
            {
                error = choice.FailureMessage;
                return false;
            }

            if (choice.Name == BackendEditorEvent)
            {
                try
                {
                    choice.SendEvent.Invoke(choice.Window, new object[] { evt });
                    choice.Window.Repaint();
                    return true;
                }
                catch (TargetInvocationException exception)
                {
                    error = "Dispatching the Event failed: "
                        + (exception.InnerException ?? exception).GetBaseException().Message;
                    return false;
                }
                catch (Exception exception)
                {
                    error = "Dispatching the Event failed: " + exception.GetBaseException().Message;
                    return false;
                }
            }

            return TryQueueMouseState(
                args, evt.mousePosition, evt.delta, Vector2.zero, ButtonsFor(evt), out error);
        }

        private static ushort ButtonsFor(Event evt)
        {
            switch (evt.type)
            {
                case EventType.MouseDown:
                    if (evt.button == 0) return LeftButton;
                    if (evt.button == 1) return RightButton;
                    return MiddleButton;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Pushes a mouse state into the Input System reflectively and advances it. Returns a precise
        /// failure rather than a hollow success when a state field is missing.
        /// </summary>
        private static bool TryQueueMouseState(
            CucliArgs args, Vector2 position, Vector2 delta, Vector2 scroll, ushort buttons, out string error)
        {
            error = null;

            if (!EditorApplication.isPlaying)
            {
                error = "PREFER_PLAYMODE";
                return false;
            }

            object mouse = MouseDevice();
            Type mouseState = CucliOptional.FindType(InputSystemAssembly, MouseStateType);
            Type inputSystem = CucliOptional.FindType(InputSystemAssembly, InputSystemType);
            if (mouse == null || mouseState == null || inputSystem == null)
            {
                error = "PACKAGE_MISSING";
                return false;
            }

            // Input only reaches a game that is actually being updated, so focusing the Game view is part
            // of the mechanism rather than a nicety.
            FocusGameView();

            object state;
            try
            {
                state = Activator.CreateInstance(mouseState);
            }
            catch (Exception exception)
            {
                error = "MouseState could not be constructed: " + exception.GetBaseException().Message;
                return false;
            }

            string fieldError;
            if (!TrySetStateField(state, mouseState, "position", position, out fieldError)
                || !TrySetStateField(state, mouseState, "delta", delta, out fieldError)
                || !TrySetStateField(state, mouseState, "scroll", scroll, out fieldError)
                || !TrySetStateField(state, mouseState, "buttons", buttons, out fieldError))
            {
                error = fieldError;
                return false;
            }

            MethodInfo queue = inputSystem
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "QueueStateEvent" && method.IsGenericMethodDefinition);

            if (queue == null)
            {
                error = "InputSystem.QueueStateEvent was not found; this Input System version lays out its API "
                    + "differently. Run 'cucli frame inspect' and check the installed package version.";
                return false;
            }

            try
            {
                MethodInfo closed = queue.MakeGenericMethod(mouseState);
                object[] arguments = closed.GetParameters().Length >= 3
                    ? new object[] { mouse, state, -1.0 }
                    : new object[] { mouse, state };
                closed.Invoke(null, arguments);
                CucliReflection.TryInvoke(inputSystem, "Update", null, out _, out _);
                return true;
            }
            catch (Exception exception)
            {
                error = "Queueing the mouse state failed: " + exception.GetBaseException().Message;
                return false;
            }
        }

        private static void FocusGameView()
        {
            try
            {
                EditorWindow gameView = Resources.FindObjectsOfTypeAll<EditorWindow>()
                    .FirstOrDefault(window => window != null && window.GetType().Name == "GameView");
                if (gameView != null) gameView.Focus();
            }
            catch (Exception)
            {
                // A missing Game view is not fatal: the state is still queued.
            }
        }

        private static bool TrySetStateField(object state, Type stateType, string name, object value, out string error)
        {
            error = null;
            FieldInfo field = stateType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                // delta and scroll are optional for a click; position and buttons are not.
                if (name == "delta" || name == "scroll") return true;
                error = "MouseState has no public '" + name + "' field in this Input System version, so injection "
                    + "cannot be built safely.";
                return false;
            }

            try
            {
                field.SetValue(state, value);
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not set MouseState." + name + ": " + exception.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>
        /// Failure result for an injecting action.
        ///
        /// Every gesture failure states <c>data.releaseSent</c> and <c>data.releaseRequired</c>, because a
        /// caller that only sees "failed" cannot tell whether the Editor or the game is still holding a
        /// button. <paramref name="releaseRequired"/> is false for failures that happened before any
        /// press was dispatched, which distinguishes "nothing to clean up" from "cleanup failed, recover
        /// before issuing the next gesture".
        /// </summary>
        private static CucliCommandResult InjectionFailure(
            string error,
            string action,
            string shape,
            bool releaseSent = false,
            bool releaseRequired = true)
        {
            string releaseNote = releaseSent
                ? "释放事件已下发，交互状态已清理。"
                : releaseRequired
                    ? "按下事件已下发但没有释放事件；按钮可能仍处于按下状态，请在下次手势前用 "
                      + "'cucli input click' 或 'cucli input move' 复位。"
                    : "失败发生在任何按下事件下发之前，没有交互状态需要清理。";

            var data = new JObject
            {
                ["action"] = action,
                ["shape"] = shape,
                ["releaseSent"] = releaseSent,
                ["releaseRequired"] = releaseRequired,
                ["releaseNote"] = releaseNote
            };

            if (error == "PACKAGE_MISSING")
            {
                return CucliCommandResult.Fail(
                    CucliErrors.OptionalPackageMissing,
                    "Input injection needs the Input System package and a live mouse device. Install "
                    + "'com.unity.inputsystem', or use --backend editor-event to drive Editor UI。"
                    + releaseNote,
                    data);
            }

            if (error == "PREFER_PLAYMODE")
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "The inputsystem backend queues events that only gameplay code can observe, so it needs Play Mode. "
                    + "Start it with 'cucli editor play', or use --backend editor-event。" + releaseNote,
                    data);
            }

            return CucliCommandResult.Fail(
                CucliErrors.CommandFailed, error + " " + releaseNote, data);
        }

        private static ushort ResolveButton(CucliArgs args, out string error)
        {
            error = null;
            switch ((args.GetString("button") ?? "left").Trim().ToLowerInvariant())
            {
                case "left": return LeftButton;
                case "right": return RightButton;
                case "middle": return MiddleButton;
                default:
                    error = "button must be left, right or middle.";
                    return 0;
            }
        }

        private static int ButtonIndex(CucliArgs args)
        {
            switch ((args.GetString("button") ?? "left").Trim().ToLowerInvariant())
            {
                case "right": return 1;
                case "middle": return 2;
                default: return 0;
            }
        }

        // ------------------------------------------------------------------ actions

        private static CucliCommandResult Move(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            if (!args.Has("x") || !args.Has("y"))
            {
                return CucliCommandResult.Missing("x", "Provide both --x and --y in screen pixels, origin bottom-left.");
            }

            var position = new Vector2(args.GetFloat("x"), args.GetFloat("y"));
            var evt = new Event
            {
                type = EventType.MouseMove,
                mousePosition = position,
                delta = new Vector2(args.GetFloat("deltaX", 0f), args.GetFloat("deltaY", 0f))
            };

            string error;
            if (!TryDispatch(args, evt, out error))
            {
                return InjectionFailure(error, "move", "pointer move", false, false);
            }

            return CucliCommandResult.Ok(new
            {
                action = "move",
                position = CucliDescribe.Value(position, null, 0),
                dispatched = evt.type.ToString()
            }, "已下发指针移动事件。");
        }

        private static CucliCommandResult Scroll(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            Vector2 position;
            string source;
            string readError;
            if (!TryReadPointer(out position, out source, out readError)) position = Vector2.zero;

            var scroll = new Vector2(args.GetFloat("scrollX", 0f), args.GetFloat("scrollY", 120f));
            var evt = new Event
            {
                type = EventType.ScrollWheel,
                mousePosition = position,
                delta = scroll
            };

            string error;
            if (!TryDispatch(args, evt, out error))
            {
                return InjectionFailure(error, "scroll", "scroll delta", false, false);
            }

            return CucliCommandResult.Ok(new
            {
                action = "scroll",
                scroll = CucliDescribe.Value(scroll, null, 0),
                position = CucliDescribe.Value(position, null, 0)
            }, "已下发滚轮事件。");
        }

        /// <summary>
        /// Press and release across separate frames: a zero-length click is ignored by most input handlers,
        /// so the button is held for <c>holdFrames</c> ticks before the release is dispatched.
        /// </summary>
        private static void Click(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;
            if (!args.Has("x") || !args.Has("y"))
            {
                complete(CucliCommandResult.Missing("x", "Provide both --x and --y."));
                return;
            }

            string buttonError;
            ResolveButton(args, out buttonError);
            if (buttonError != null)
            {
                complete(CucliCommandResult.Invalid("button", buttonError));
                return;
            }

            var position = new Vector2(args.GetFloat("x"), args.GetFloat("y"));
            int holdFrames = Mathf.Clamp(args.GetInt("holdFrames", args.GetInt("hold_frames", 1)), 0, 120);
            int settleFrames = Mathf.Clamp(args.GetInt("settleFrames", args.GetInt("settle_frames", 2)), 1, 120);
            int button = ButtonIndex(args);

            var shape = new JObject
            {
                ["position"] = CucliDescribe.Value(position, null, 0),
                ["button"] = args.GetString("button") ?? "left",
                ["holdFrames"] = holdFrames
            };

            RunGesture(
                context, complete, "click", shape,
                (tick, finish, releaseOnly) =>
                {
                    string error;

                    if (releaseOnly)
                    {
                        var release = new Event
                        {
                            type = EventType.MouseUp, mousePosition = position, button = button
                        };
                        bool releaseSent = TryDispatch(args, release, out error);
                        finish(CucliCommandResult.Fail(
                            CucliErrors.Cancelled,
                            "The click was cancelled after the press, so only the release event was sent to leave the "
                            + "interaction state clean. A mouse state without a matching release is the one thing that "
                            + "really does wedge a UI。"
                            + (releaseSent
                                ? "释放事件已下发，交互状态已清理。"
                                : "但释放事件下发失败（" + error + "），按钮可能仍处于按下状态，请在下次手势前复位。"),
                            new JObject
                            {
                                ["action"] = "click",
                                ["shape"] = shape,
                                ["releaseSent"] = releaseSent,
                                ["releaseRequired"] = true
                            }));
                        return true;
                    }

                    if (tick <= holdFrames)
                    {
                        var press = new Event
                        {
                            type = EventType.MouseDown, mousePosition = position, button = button, clickCount = 1
                        };
                        if (!TryDispatch(args, press, out error))
                        {
                            finish(InjectionFailure(error, "click", "press", false, false));
                            return true;
                        }

                        return false;
                    }

                    if (tick == holdFrames + 1)
                    {
                        var release = new Event
                        {
                            type = EventType.MouseUp, mousePosition = position, button = button, clickCount = 1
                        };
                        if (!TryDispatch(args, release, out error))
                        {
                            finish(InjectionFailure(error, "click", "release", false, true));
                            return true;
                        }

                        return false;
                    }

                    if (tick < holdFrames + 1 + settleFrames) return false;

                    finish(CucliCommandResult.Ok(new
                    {
                        action = "click",
                        position = CucliDescribe.Value(position, null, 0),
                        button = args.GetString("button") ?? "left",
                        holdFrames,
                        settleFrames,
                        releaseSent = true
                    }, "已下发按下与抬起事件；释放事件已下发，交互状态已清理。"));
                    return true;
                });
        }

        private static void Drag(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;
            List<Vector2> waypoints = ReadPoints(args);
            if (waypoints.Count < 2)
            {
                complete(CucliCommandResult.Missing(
                    "points",
                    "Provide at least two waypoints: --points \"[[100,100],[300,250]]\", or --points @points.json "
                    + "with a 'points' array."));
                return;
            }

            string buttonError;
            ResolveButton(args, out buttonError);
            if (buttonError != null)
            {
                complete(CucliCommandResult.Invalid("button", buttonError));
                return;
            }

            int button = ButtonIndex(args);
            int settleFrames = Mathf.Clamp(args.GetInt("settleFrames", args.GetInt("settle_frames", 2)), 1, 120);
            int stage = 0;
            bool released = false;

            var shape = new JObject
            {
                ["waypoints"] = waypoints.Count,
                ["button"] = args.GetString("button") ?? "left"
            };

            RunGesture(
                context, complete, "drag", shape,
                (tick, finish, releaseOnly) =>
                {
                    string error;
                    Vector2 last = waypoints[waypoints.Count - 1];

                    if (releaseOnly)
                    {
                        bool releaseSent = TryDispatch(args, new Event
                        {
                            type = EventType.MouseUp, mousePosition = last, button = button
                        }, out error);
                        finish(CucliCommandResult.Fail(
                            CucliErrors.Cancelled,
                            "The drag was cancelled. A release event was sent at the last waypoint so the interaction "
                            + "state is not left with a held button。"
                            + (releaseSent
                                ? "释放事件已下发，交互状态已清理。"
                                : "但释放事件下发失败（" + error + "），按钮可能仍处于按下状态，请在下次手势前复位。"),
                            new JObject
                            {
                                ["action"] = "drag",
                                ["shape"] = shape,
                                ["releaseSent"] = releaseSent,
                                ["releaseRequired"] = true
                            }));
                        return true;
                    }

                    if (stage == 0)
                    {
                        if (!TryDispatch(args, new Event
                        {
                            type = EventType.MouseMove, mousePosition = waypoints[0]
                        }, out error))
                        {
                            finish(InjectionFailure(error, "drag", "initial move", false, false));
                            return true;
                        }

                        stage = 1;
                        return false;
                    }

                    if (stage == 1 && tick >= 1)
                    {
                        if (!TryDispatch(args, new Event
                        {
                            type = EventType.MouseDown, mousePosition = waypoints[0], button = button, clickCount = 1
                        }, out error))
                        {
                            finish(InjectionFailure(error, "drag", "press", false, false));
                            return true;
                        }

                        stage = 2;
                        return false;
                    }

                    if (!released && stage >= 2 && stage - 2 < waypoints.Count - 1)
                    {
                        Vector2 from = waypoints[stage - 2];
                        Vector2 to = waypoints[stage - 1];
                        if (!TryDispatch(args, new Event
                        {
                            type = EventType.MouseDrag, mousePosition = to, delta = to - from, button = button
                        }, out error))
                        {
                            finish(InjectionFailure(error, "drag", "move to waypoint " + (stage - 1), false, true));
                            return true;
                        }

                        stage++;
                        return false;
                    }

                    if (!released)
                    {
                        if (!TryDispatch(args, new Event
                        {
                            type = EventType.MouseUp, mousePosition = last, button = button, clickCount = 1
                        }, out error))
                        {
                            finish(InjectionFailure(error, "drag", "release", false, true));
                            return true;
                        }

                        released = true;
                        return false;
                    }

                    if (tick < settleFrames) return false;

                    finish(CucliCommandResult.Ok(new
                    {
                        action = "drag",
                        waypoints = waypoints.Count,
                        start = CucliDescribe.Value(waypoints[0], null, 0),
                        end = CucliDescribe.Value(last, null, 0),
                        settleFrames,
                        releaseSent = true,
                        note = "One waypoint is dispatched per Editor tick so the target observes distinct pointer "
                            + "positions; a long path therefore takes one frame per point."
                    }, "已下发拖拽按下、移动与抬起事件；释放事件已下发，交互状态已清理。"));
                    return true;
                });
        }

        private static List<Vector2> ReadPoints(CucliArgs args)
        {
            var points = new List<Vector2>();

            // Accepts both --points '[[x,y],...]' and --points @points.json, which is what a long path needs.
            JArray array = args.GetArray("points");
            if (array == null)
            {
                JObject payload = args.GetObjectOrFile("points");
                array = payload == null ? null : payload["points"] as JArray;
            }

            if (array == null) return points;

            foreach (JToken token in array)
            {
                var item = token as JObject;
                if (item != null)
                {
                    var point = new CucliArgs(item);
                    points.Add(new Vector2(point.GetFloat("x"), point.GetFloat("y")));
                    continue;
                }

                var pair = token as JArray;
                if (pair != null && pair.Count >= 2)
                {
                    points.Add(new Vector2(pair[0].Value<float>(), pair[1].Value<float>()));
                }
            }

            return points;
        }

        private static void Key(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliArgs args = context.Input;
            string requested = args.GetString("key");
            if (string.IsNullOrWhiteSpace(requested))
            {
                complete(CucliCommandResult.Missing("key", "Example: --key Space, --key Return, --key LeftShift."));
                return;
            }

            Event down;
            Event up;
            try
            {
                // Event.KeyboardEvent is the public, version-stable way to build a key event, so the key
                // name is validated by Unity itself rather than by a hand-maintained table here.
                down = Event.KeyboardEvent(requested.Trim());
                up = Event.KeyboardEvent(requested.Trim());
            }
            catch (Exception exception)
            {
                complete(CucliCommandResult.Invalid(
                    "key", "'" + requested + "' is not a key Unity recognises: "
                    + exception.GetBaseException().Message));
                return;
            }

            if (down.keyCode == KeyCode.None)
            {
                complete(CucliCommandResult.Invalid(
                    "key",
                    "'" + requested + "' produced no KeyCode. Use a KeyCode name such as Space, Return, Escape, "
                    + "LeftShift, or a single character such as a."));
                return;
            }

            int holdFrames = Mathf.Clamp(args.GetInt("holdFrames", args.GetInt("hold_frames", 1)), 0, 120);
            int settleFrames = Mathf.Clamp(args.GetInt("settleFrames", args.GetInt("settle_frames", 2)), 1, 120);

            var shape = new JObject { ["key"] = requested.Trim(), ["keyCode"] = down.keyCode.ToString() };

            RunGesture(
                context, complete, "key", shape,
                (tick, finish, releaseOnly) =>
                {
                    string error;
                    if (releaseOnly)
                    {
                        bool releaseSent = TryDispatch(args, up, out error);
                        finish(CucliCommandResult.Fail(
                            CucliErrors.Cancelled,
                            "The key press was cancelled; the key-up event was still sent so the key is not left held。"
                            + (releaseSent
                                ? "释放事件已下发，交互状态已清理。"
                                : "但释放事件下发失败（" + error + "），按键可能仍处于按下状态，请在下次手势前复位。"),
                            new JObject
                            {
                                ["action"] = "key",
                                ["shape"] = shape,
                                ["releaseSent"] = releaseSent,
                                ["releaseRequired"] = true
                            }));
                        return true;
                    }

                    if (tick <= holdFrames)
                    {
                        if (!TryDispatch(args, down, out error)) { finish(InjectionFailure(error, "key", "key down", false, false)); return true; }
                        return false;
                    }

                    if (tick == holdFrames + 1)
                    {
                        if (!TryDispatch(args, up, out error)) { finish(InjectionFailure(error, "key", "key up", false, true)); return true; }
                        return false;
                    }

                    if (tick < holdFrames + 1 + settleFrames) return false;

                    finish(CucliCommandResult.Ok(new
                    {
                        action = "key",
                        key = requested.Trim(),
                        keyCode = down.keyCode.ToString(),
                        holdFrames,
                        settleFrames,
                        releaseSent = true
                    }, "已下发按键按下与抬起事件；释放事件已下发，交互状态已清理。"));
                    return true;
                });
        }

        // ------------------------------------------------------------------ gesture driver

        /// <summary>
        /// Shared tick loop for frame-spanning gestures.
        ///
        /// Cancellation is checked at every recoverable boundary, and when a gesture is cancelled after the
        /// press the body is invoked once more in release-only mode: the release event is the one thing
        /// that must still go out, because a held button is what actually wedges an interaction state.
        /// </summary>
        private static void RunGesture(
            CucliCommandContext context,
            Action<CucliCommandResult> complete,
            string action,
            JObject shape,
            Func<int, Action<CucliCommandResult>, bool, bool> body)
        {
            int tick = 0;
            bool done = false;
            bool pressDispatched = false;

            EditorApplication.CallbackFunction update = null;
            update = () =>
            {
                if (done) return;

                if (context.Cancellation.IsCancellationRequested)
                {
                    if (pressDispatched)
                    {
                        // One more pass to release. `done` stays false so the body can complete the job.
                        bool finished = body(tick, result =>
                        {
                            if (done) return;
                            done = true;
                            EditorApplication.update -= update;
                            complete(result);
                        }, true);

                        if (finished && !done)
                        {
                            done = true;
                            EditorApplication.update -= update;
                            complete(CucliCommandResult.Fail(
                                CucliErrors.Cancelled,
                                "The gesture was cancelled and the release pass produced no result, so no release "
                                + "event could be confirmed; the button may still be held.",
                                new JObject
                                {
                                    ["action"] = action,
                                    ["shape"] = shape,
                                    ["releaseSent"] = false,
                                    ["releaseRequired"] = true
                                }));
                        }

                        return;
                    }

                    done = true;
                    EditorApplication.update -= update;
                    complete(CucliCommandResult.Fail(
                        CucliErrors.Cancelled,
                        "The gesture was cancelled before any press was dispatched, so there is no interaction state "
                        + "to clean up.",
                        new JObject
                        {
                            ["action"] = action,
                            ["shape"] = shape,
                            ["releaseSent"] = false,
                            ["releaseRequired"] = false
                        }));
                    return;
                }

                tick++;
                bool bodyFinished = body(tick, result =>
                {
                    if (done) return;
                    done = true;
                    EditorApplication.update -= update;
                    complete(result);
                }, false);

                pressDispatched = true;

                if (bodyFinished && !done)
                {
                    // The body claimed to be finished without a result; completing here rather than
                    // unsubscribing keeps the job from hanging until the CLI times out.
                    done = true;
                    EditorApplication.update -= update;
                    complete(CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "Internal error: the gesture step reported completion without a result.",
                        new { action, shape, tick }));
                }
            };

            EditorApplication.update += update;
        }
    }
}
