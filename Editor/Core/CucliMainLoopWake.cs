using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Wakes a throttled Editor so queued jobs can run while the window is unfocused.
    ///
    /// Unity parks <see cref="EditorApplication.update"/> when the window is in the background.
    /// A .NET <see cref="System.Threading.Timer"/> keeps the heartbeat file fresh, but it must
    /// never call Unity APIs: doing so from a thread-pool thread has wedged the Editor permanently.
    /// This type uses only Win32 from the background thread (<c>PostMessage(WM_NULL)</c>) and only
    /// calls <see cref="EditorApplication.QueuePlayerLoopUpdate"/> from the main thread, either
    /// from <see cref="EditorApplication.update"/> or from a Win32 <c>SetTimer</c> callback
    /// installed on that thread (the OS still delivers those while the window is unfocused).
    ///
    /// Modal dialogs, a frozen native main thread, and non-Windows Editors are out of scope: the
    /// heartbeat stays honest via <c>mainThreadTickAtUtc</c> and the CLI times out.
    /// </summary>
    internal static class CucliMainLoopWake
    {
        private const uint WmNull = 0x0000;
        private const uint WakeIntervalMs = 100;
        private const string SessionBusyKey = "CustomUnityCli.Wake.Busy";
        private const string SessionIdleTimeKey = "CustomUnityCli.Wake.PrevIdleTime";
        private const string SessionInteractionKey = "CustomUnityCli.Wake.PrevInteractionMode";
        private const string SessionCapturedKey = "CustomUnityCli.Wake.PrefsCaptured";
        private const string ApplicationIdleTimeKey = "ApplicationIdleTime";
        private const string InteractionModeKey = "InteractionMode";

        private static volatile IntPtr s_hwnd;
        private static UIntPtr s_timerId;
        private static bool s_timerArmed;
        private static bool s_prefsApplied;

#if UNITY_EDITOR_WIN
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void NativeTimerProc(IntPtr hWnd, uint msg, UIntPtr idEvent, uint dwTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern UIntPtr SetTimer(
            IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, NativeTimerProc lpTimerFunc);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        // Must live in a static field: the native timer holds a function pointer, not a GC handle.
        private static readonly NativeTimerProc TimerCallback = OnNativeTimer;
#endif

        internal static void Initialize()
        {
            CacheEditorWindow();
            if (SessionState.GetBool(SessionBusyKey, false))
            {
                ApplyNoThrottling();
            }

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        /// <summary>
        /// Background-thread entry. Touches only Win32 and the filesystem, never a Unity API.
        /// </summary>
        internal static void RequestFromBackground()
        {
            if (!HasWork()) return;
            CacheEditorWindow();
#if UNITY_EDITOR_WIN
            IntPtr hwnd = s_hwnd;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                PostMessage(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception)
            {
                // A failed nudge is not fatal: the next natural tick still drains the queue.
            }
#endif
        }

        /// <summary>Main thread. Arm or disarm the native timer and the idle-time prefs.</summary>
        internal static void TickMainThread()
        {
            bool pending = HasWork();
            CacheEditorWindow();
            if (pending)
            {
                ApplyNoThrottling();
                ArmNativeTimer();
                try
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                catch (Exception)
                {
                }
            }
            else
            {
                DisarmNativeTimer();
                RestoreThrottling();
            }
        }

        internal static void Shutdown()
        {
            DisarmNativeTimer();
            RestoreThrottling();
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
        }

        internal static bool HasWork()
        {
            try
            {
                return HasAnyJson(CucliPaths.Queued)
                    || HasAnyJson(CucliPaths.Control)
                    || HasAnyJson(CucliPaths.CancelRequests)
                    || HasAnyJson(CucliPaths.Waiting)
                    || HasAnyJson(CucliPaths.Running);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HasAnyJson(string directory)
        {
            try
            {
                return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void CacheEditorWindow()
        {
#if UNITY_EDITOR_WIN
            if (s_hwnd != IntPtr.Zero) return;
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                IntPtr main = Process.GetCurrentProcess().MainWindowHandle;
                if (main != IntPtr.Zero && IsWindowVisible(main))
                {
                    s_hwnd = main;
                    return;
                }

                IntPtr found = IntPtr.Zero;
                EnumWindows((hWnd, lParam) =>
                {
                    uint windowPid;
                    GetWindowThreadProcessId(hWnd, out windowPid);
                    if (windowPid != (uint)pid) return true;
                    if (!IsWindowVisible(hWnd)) return true;
                    found = hWnd;
                    return false;
                }, IntPtr.Zero);
                s_hwnd = found;
            }
            catch (Exception)
            {
                s_hwnd = IntPtr.Zero;
            }
#endif
        }

        private static void ArmNativeTimer()
        {
#if UNITY_EDITOR_WIN
            if (s_timerArmed) return;
            try
            {
                s_timerId = SetTimer(IntPtr.Zero, UIntPtr.Zero, WakeIntervalMs, TimerCallback);
                s_timerArmed = s_timerId != UIntPtr.Zero;
            }
            catch (Exception)
            {
                s_timerArmed = false;
            }
#endif
        }

        private static void DisarmNativeTimer()
        {
#if UNITY_EDITOR_WIN
            if (!s_timerArmed) return;
            try
            {
                KillTimer(IntPtr.Zero, s_timerId);
            }
            catch (Exception)
            {
            }

            s_timerArmed = false;
            s_timerId = UIntPtr.Zero;
#endif
        }

#if UNITY_EDITOR_WIN
        private static void OnNativeTimer(IntPtr hWnd, uint msg, UIntPtr idEvent, uint dwTime)
        {
            try
            {
                if (HasWork()) EditorApplication.QueuePlayerLoopUpdate();
                else DisarmNativeTimer();
            }
            catch (Exception)
            {
            }
        }
#endif

        private static void ApplyNoThrottling()
        {
            SessionState.SetBool(SessionBusyKey, true);
            if (!SessionState.GetBool(SessionCapturedKey, false))
            {
                SessionState.SetInt(SessionIdleTimeKey, EditorPrefs.GetInt(ApplicationIdleTimeKey, 4));
                SessionState.SetInt(SessionInteractionKey, EditorPrefs.GetInt(InteractionModeKey, 0));
                SessionState.SetBool(SessionCapturedKey, true);
            }

            if (s_prefsApplied) return;
            EditorPrefs.SetInt(ApplicationIdleTimeKey, 0);
            EditorPrefs.SetInt(InteractionModeKey, 1);
            ForceInteractionPrefs();
            s_prefsApplied = true;
        }

        private static void RestoreThrottling()
        {
            SessionState.SetBool(SessionBusyKey, false);
            if (!SessionState.GetBool(SessionCapturedKey, false))
            {
                s_prefsApplied = false;
                return;
            }

            EditorPrefs.SetInt(ApplicationIdleTimeKey, SessionState.GetInt(SessionIdleTimeKey, 4));
            EditorPrefs.SetInt(InteractionModeKey, SessionState.GetInt(SessionInteractionKey, 0));
            ForceInteractionPrefs();
            SessionState.SetBool(SessionCapturedKey, false);
            s_prefsApplied = false;
        }

        private static void ForceInteractionPrefs()
        {
            try
            {
                var method = typeof(EditorApplication).GetMethod(
                    "UpdateInteractionModeSettings",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (method != null) method.Invoke(null, null);
            }
            catch (Exception)
            {
            }
        }

        private static void OnBeforeReload()
        {
            DisarmNativeTimer();
        }
    }
}
