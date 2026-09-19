using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
// ProfilerCategory lives in Unity.Profiling, not UnityEngine.Profiling. Both namespaces are needed.
using Unity.Profiling;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli profiler ...</c> - profiler sessions, frame timing, counters, object memory, memory
    /// snapshots and the Frame Debugger.
    ///
    /// Two design points worth knowing before reading the code:
    ///
    /// * <b>Every session is reversible.</b> <c>start</c> records what the Editor looked like first,
    ///   and <c>stop</c> puts it back, so a CLI sampling run cannot silently change what the next run
    ///   (or a human in the Profiler window) records.
    /// * <b>Counter output is bounded twice.</b> The scan is capped by <c>maxCounters</c> and the
    ///   result is paged with a small default page, because a category can hold hundreds of counters
    ///   and the raw enumeration is the single easiest way to flood a caller.
    /// </summary>
    internal sealed class ProfilerCommand : CucliCommand, ICucliDeferredCommand
    {
        private const int DefaultPageSize = 32;
        private const int DefaultMaxCounters = 256;
        private const int DefaultSnapshotTimeoutSeconds = 30;

        public override string Name => "profiler";

        public override string Summary =>
            "Unity Profiler：会话开关与还原、帧计时、计数器（分页）、对象内存、内存快照、Frame Debugger。";

        public override CucliActionInfo[] Actions => Declare(
            "status", "报告 Profiler 与各 area 的当前开关状态，以及是否有可还原的会话快照。",
            "ping", "连通性探测。",
            "start", "开启 Profiler；可选写二进制日志与采集分配调用栈。开始前会记录原状态。",
            "stop", "关闭 Profiler 并还原 start 之前的状态，避免污染下一次采样。",
            "set-areas", "按 ProfilerArea 枚举名逐项开关采集区域。",
            "frame-timing", "读取最近一帧的 CPU/GPU 帧计时；未开启 Frame Timing Stats 时给出可诊断说明。",
            "counters", "读取某个 ProfilerCategory 下的计数器（分页 + 名称过滤 + 扫描上限）。",
            "object-memory", "按场景层级路径或资产路径查询单个对象的运行时内存占用。",
            "snapshot-take", "用 Memory Profiler 包抓取 .snap 快照（异步 + 超时 + 取消）。",
            "snapshot-list", "列出 .snap 快照文件及其大小与创建时间（分页）。",
            "snapshot-compare", "比较两个快照的文件级元数据（不做对象级 diff）。",
            "frame-debugger-enable", "开启 Frame Debugger；Play Mode 未暂停时拒绝并提示先暂停。",
            "frame-debugger-disable", "关闭 Frame Debugger。",
            "frame-debugger-events", "按游标分页读取 Frame Debugger 事件列表。");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        /// <summary>
        /// Deliberately null even though snapshot-take needs the Memory Profiler package: only that one
        /// action needs it, and it checks for itself. Declaring the package here would advertise the
        /// whole group as unavailable in a project without it - hiding session control and counter
        /// reading, which are the actions people most often want.
        /// </summary>
        public override string OptionalPackage => null;

        public override string[] Effects => new[]
        {
            "editor.profiler.read", "editor.profiler.write", "filesystem.write"
        };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""status"", ""ping"", ""start"", ""stop"", ""set-areas"", ""frame-timing"", ""counters"",
                 ""object-memory"", ""snapshot-take"", ""snapshot-list"", ""snapshot-compare"",
                 ""frame-debugger-enable"", ""frame-debugger-disable"", ""frame-debugger-events""],
      ""default"": ""status""
    },
    ""logFile"": { ""type"": ""string"", ""description"": ""start: 二进制日志输出路径；目录必须已存在。也接受 log_file."" },
    ""enableCallstacks"": { ""type"": ""boolean"", ""default"": false, ""description"": ""start: 采集分配调用栈。也接受 enable_callstacks."" },
    ""areas"": {
      ""type"": ""object"",
      ""description"": ""set-areas: 键为 ProfilerArea 枚举名（大小写不敏感），值为布尔。例如把 CPU 设为 true、GPU 设为 false。""
    },
    ""category"": { ""type"": ""string"", ""description"": ""counters: ProfilerCategory 名，如 Render / Scripts / Memory / Physics。"" },
    ""counters"": {
      ""type"": ""array"", ""items"": { ""type"": ""string"" },
      ""description"": ""counters: 只看名称包含这些子串的计数器；省略则按名称顺序取前 maxCounters 个。""
    },
    ""maxCounters"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 4096, ""default"": 256, ""description"": ""counters: 扫描上限；命中更多时会标记 scanTruncated。也接受 max_counters."" },
    ""objectPath"": { ""type"": ""string"", ""description"": ""object-memory: 场景层级路径或资产路径。也接受 object_path."" },
    ""snapshotPath"": { ""type"": ""string"", ""description"": ""snapshot-take: 输出 .snap 路径；省略则写入临时缓存目录。也接受 snapshot_path."" },
    ""searchPath"": { ""type"": ""string"", ""description"": ""snapshot-list: 搜索目录；省略则搜临时缓存与工程根两处。也接受 search_path."" },
    ""snapshotA"": { ""type"": ""string"", ""description"": ""snapshot-compare: 第一个快照文件。也接受 snapshot_a."" },
    ""snapshotB"": { ""type"": ""string"", ""description"": ""snapshot-compare: 第二个快照文件。也接受 snapshot_b."" },
    ""pageSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 32, ""description"": ""也接受 page_size."" },
    ""cursor"": { ""type"": ""string"", ""description"": ""frame-debugger-events: 起始偏移（整数）。"" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""description"": ""pageSize 的别名。"" },
    ""timeoutSeconds"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 600, ""default"": 30, ""description"": ""snapshot-take: 等待快照完成的秒数。也接受 timeout_seconds."" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "status"))
            {
                case "status": return Status();
                case "ping": return Ping();
                case "start": return Start(context);
                case "stop": return Stop();
                case "set-areas": return SetAreas(context);
                case "frame-timing": return FrameTiming();
                case "object-memory": return ObjectMemory(context);
                case "snapshot-list": return SnapshotList(context);
                case "snapshot-compare": return SnapshotCompare(context);
                case "frame-debugger-enable": return FrameDebuggerEnable(context);
                case "frame-debugger-disable": return FrameDebuggerDisable(context);
                case "frame-debugger-events": return FrameDebuggerEvents(context);

                case "counters":
                case "snapshot-take":
                    // Both need to wait for Editor frames, so the worker must run them deferred.
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "'" + ResolveAction(context, "status")
                        + "' 需要跨帧执行，必须通过 deferred 路径派发。",
                        new { action = ResolveAction(context, "status"), requiresDeferred = true });

                default:
                    return CucliCommandResult.Unsupported(ResolveAction(context, "status"), ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            switch (ResolveAction(context, "status"))
            {
                case "counters":
                    CountersDeferred(context, complete);
                    return;

                case "snapshot-take":
                    SnapshotTakeDeferred(context, complete);
                    return;

                default:
                    complete(Execute(context));
                    return;
            }
        }

        // ---- session -----------------------------------------------------------------------

        private static CucliCommandResult Ping()
        {
            return CucliCommandResult.Ok(new
            {
                tool = "profiler",
                group = "profiling",
                unityVersion = Application.unityVersion,
                memoryProfilerAvailable = ProfilerSnapshots.IsAvailable,
                frameDebuggerAvailable = ProfilerFrameDebugger.Available
            });
        }

        private static CucliCommandResult Status()
        {
            JObject session = ProfilerSupport.DescribeSession();
            session["areas"] = ProfilerSupport.DescribeAreas();
            session["areaNames"] = new JArray(ProfilerSupport.AreaNames());
            return CucliCommandResult.Ok(session);
        }

        private static CucliCommandResult Start(CucliCommandContext context)
        {
            string logFile = context.Input.GetString("logFile") ?? context.Input.GetString("log_file");
            bool enableCallstacks = context.Input.GetBool("enableCallstacks")
                || context.Input.GetBool("enable_callstacks");

            if (!string.IsNullOrWhiteSpace(logFile))
            {
                string directory = Path.GetDirectoryName(logFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    return CucliCommandResult.Invalid(
                        "logFile",
                        "目录不存在：" + directory + "。请先创建目录，或省略 logFile 只开启 Profiler。");
                }
            }

            // Record the pre-start state before touching anything.
            ProfilerSupport.SaveStateIfAbsent();

            if (!string.IsNullOrWhiteSpace(logFile))
            {
                Profiler.logFile = logFile;
                Profiler.enableBinaryLog = true;
            }

            if (enableCallstacks)
            {
                Profiler.enableAllocationCallstacks = true;
            }

            Profiler.enabled = true;

            if (!string.IsNullOrWhiteSpace(logFile)) context.AddArtifact(logFile);

            JObject data = ProfilerSupport.DescribeSession();
            return CucliCommandResult.Ok(data, "Profiler 已开启；stop 会还原开启前的状态。");
        }

        private static CucliCommandResult Stop()
        {
            string previousLogFile = Profiler.logFile;

            Profiler.enabled = false;
            Profiler.enableBinaryLog = false;
            Profiler.enableAllocationCallstacks = false;

            ProfilerSupport.SessionStateSnapshot restored = ProfilerSupport.RestoreStateAndClear();

            JObject data = ProfilerSupport.DescribeSession();
            data["previousLogFile"] = previousLogFile ?? string.Empty;
            data["restored"] = restored != null;
            if (restored != null)
            {
                data["restoredState"] = new JObject
                {
                    ["enabled"] = restored.Enabled,
                    ["logFile"] = restored.LogFile ?? string.Empty,
                    ["enableBinaryLog"] = restored.EnableBinaryLog,
                    ["enableAllocationCallstacks"] = restored.EnableAllocationCallstacks
                };
            }

            return CucliCommandResult.Ok(
                data,
                restored != null
                    ? "Profiler 已关闭，并还原了开启前的设置。"
                    : "Profiler 已关闭；本次没有记录到可供还原的起始状态。");
        }

        private static CucliCommandResult SetAreas(CucliCommandContext context)
        {
            JObject areas = context.Input.GetObject("areas");
            if (areas == null || areas.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "areas",
                    "需要一个对象，键为 ProfilerArea 名、值为布尔。可用：" + string.Join(", ", ProfilerSupport.AreaNames()));
            }

            var applied = new JObject();
            var problems = new JArray();

            foreach (JProperty property in areas.Properties())
            {
                ProfilerArea area;
                if (!ProfilerSupport.TryResolveArea(property.Name, out area))
                {
                    problems.Add(new JObject
                    {
                        ["area"] = property.Name,
                        ["error"] = "不是 ProfilerArea 名。可用：" + string.Join(", ", ProfilerSupport.AreaNames())
                    });
                    continue;
                }

                if (property.Value.Type != JTokenType.Boolean)
                {
                    problems.Add(new JObject
                    {
                        ["area"] = property.Name,
                        ["error"] = "值必须是布尔，收到 " + property.Value.Type + "。"
                    });
                    continue;
                }

                bool enabled = property.Value.Value<bool>();
                string error;
                if (!ProfilerSupport.TrySetAreaEnabled(area, enabled, out error))
                {
                    problems.Add(new JObject { ["area"] = property.Name, ["error"] = error });
                    continue;
                }

                applied[area.ToString()] = enabled;
            }

            if (problems.Count > 0)
            {
                // Unity applies area changes one by one and offers no rollback, so a partial result is
                // reported as a partial result instead of being presented as success.
                context.Warn("有 " + problems.Count + " 个 area 项未生效；已生效的部分不会回滚。");
            }

            var data = new JObject
            {
                ["applied"] = applied,
                ["appliedCount"] = applied.Count,
                ["problems"] = problems,
                ["areas"] = ProfilerSupport.DescribeAreas()
            };

            if (applied.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "没有任何 area 项生效，请检查键名是否为 ProfilerArea 枚举名。",
                    data);
            }

            return CucliCommandResult.Ok(data, "已更新 " + applied.Count + " 个 area 的采集开关。");
        }

        // ---- frame timing ------------------------------------------------------------------

        private static CucliCommandResult FrameTiming()
        {
            var timings = new FrameTiming[1];
            uint count;
            try
            {
                // Frame timings come from FrameTimingManager; UnityEngine.Profiling.Profiler has no
                // GetLatestTimings. The Player Setting probe below explains an empty result.
                count = FrameTimingManager.GetLatestTimings(1, timings);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "读取帧计时失败：" + exception.GetBaseException().Message);
            }

            bool settingKnown;
            bool settingEnabled;
            settingKnown = TryReadFrameTimingStatsSetting(out settingEnabled);

            if (count == 0)
            {
                // Never fabricate numbers here: an unavailable reading is reported as unavailable,
                // together with the one setting that explains it.
                var unavailable = new JObject
                {
                    ["available"] = false,
                    ["frameTimingStatsEnabled"] = settingKnown ? (JToken)settingEnabled : JValue.CreateNull()
                };

                if (settingKnown && !settingEnabled)
                {
                    unavailable["hint"] =
                        "Frame Timing Stats 未开启。请到 Edit > Project Settings > Player > Other Settings "
                        + "> Rendering 勾选 Frame Timing Stats，然后在 Play Mode 下重试。";
                }
                else
                {
                    unavailable["hint"] =
                        "Profiler 还没有可用的帧计时数据。帧计时通常需要进入 Play Mode 并至少渲染一帧；"
                        + "若已开启 Frame Timing Stats 仍取不到，请确认是否在 Play Mode 中。";
                }

                return CucliCommandResult.Ok(unavailable, "当前拿不到帧计时数据（非假值，未做估算）。");
            }

            FrameTiming timing = timings[0];
            var data = new JObject
            {
                ["available"] = true,
                ["cpuFrameTimeMs"] = timing.cpuFrameTime,
                ["gpuFrameTimeMs"] = timing.gpuFrameTime,
                ["cpuTimePresentCalled"] = timing.cpuTimePresentCalled,
                ["cpuTimeFrameComplete"] = timing.cpuTimeFrameComplete,
                ["heightScale"] = timing.heightScale,
                ["widthScale"] = timing.widthScale,
                ["syncInterval"] = timing.syncInterval,
                ["frameTimingStatsEnabled"] = settingKnown ? (JToken)settingEnabled : JValue.CreateNull()
            };

#if UNITY_2022_2_OR_NEWER
            data["cpuMainThreadFrameTimeMs"] = timing.cpuMainThreadFrameTime;
            data["cpuMainThreadPresentWaitTimeMs"] = timing.cpuMainThreadPresentWaitTime;
            data["cpuRenderThreadFrameTimeMs"] = timing.cpuRenderThreadFrameTime;
            data["frameStartTimestamp"] = timing.frameStartTimestamp;
            data["firstSubmitTimestamp"] = timing.firstSubmitTimestamp;
#endif

            return CucliCommandResult.Ok(data);
        }

        /// <summary>
        /// Reads the Frame Timing Stats player setting without a public API for it: the only reliable
        /// route is the serialised ProjectSettings asset, and the property name has varied.
        /// </summary>
        private static bool TryReadFrameTimingStatsSetting(out bool enabled)
        {
            enabled = false;
            try
            {
                Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
                if (assets == null || assets.Length == 0) return false;

                var serialized = new SerializedObject(assets[0]);
                SerializedProperty property = serialized.FindProperty("m_FrameTimingStats")
                    ?? serialized.FindProperty("frameTimingStats")
                    ?? serialized.FindProperty("m_FrameTimingStatsEnabled");
                if (property == null || property.propertyType != SerializedPropertyType.Boolean) return false;

                enabled = property.boolValue;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- counters (deferred) -----------------------------------------------------------

        private static void CountersDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string requested = context.Input.GetString("category");
            if (string.IsNullOrWhiteSpace(requested))
            {
                complete(CucliCommandResult.Missing(
                    "category",
                    "可用：" + string.Join(", ", ProfilerSupport.CategoryNames())));
                return;
            }

            ProfilerCategory category;
            string resolveError;
            if (!ProfilerSupport.TryResolveCategory(requested, out category, out resolveError))
            {
                complete(CucliCommandResult.Fail(CucliErrors.InvalidParameter, resolveError));
                return;
            }

            string[] filters = context.Input.GetStringList("counters");
            int maxCounters = Mathf.Clamp(
                context.Input.GetInt("maxCounters", context.Input.GetInt("max_counters", DefaultMaxCounters)),
                1, 4096);

            var options = CucliDescribe.Options.Default;
            int pageSize = CucliPaging.ResolvePageSize(context.Input, DefaultPageSize);

            bool completed = false;
            // Declared before finish() so finish() can unsubscribe it. Without this, every counters
            // call would leave a permanent per-frame delegate subscribed to EditorApplication.update.
            EditorApplication.CallbackFunction watch = null;

            Action finish = null;
            finish = () =>
            {
                if (completed) return;
                completed = true;
                if (watch != null) EditorApplication.update -= watch;

                ProfilerSupport.CounterScan scan = ProfilerSupport.ReadCounters(category, filters, maxCounters);

                // Pre-slice to the requested page before describing, so a wide category never gets
                // fully materialised into JSON just to discard most of it.
                IReadOnlyList<ProfilerSupport.CounterReading> readings = scan.Readings;
                CucliPage<ProfilerSupport.CounterReading> page =
                    CucliPaging.Slice(readings, context.Input, DefaultPageSize);

                var items = new JArray();
                foreach (ProfilerSupport.CounterReading reading in page.Items)
                {
                    items.Add(ProfilerSupport.ReadingToJson(reading));
                }

                var payload = new JObject
                {
                    ["category"] = requested.Trim(),
                    ["availableInCategory"] = scan.AvailableInCategory,
                    ["matched"] = scan.Matched,
                    ["scanned"] = scan.Scanned,
                    ["filter"] = filters.Length == 0 ? JValue.CreateNull() : new JArray(filters),
                    ["maxCounters"] = maxCounters,
                    ["scanTruncated"] = scan.ScanTruncated
                };

                if (scan.ScanTruncated)
                {
                    payload["hint"] =
                        "该 category 命中 " + scan.Matched + " 个计数器，只扫描了前 " + maxCounters
                        + " 个。用 counters 参数按名称过滤，或提高 maxCounters，才能看到其余项。";
                    context.Warn("计数器扫描被 maxCounters=" + maxCounters + " 截断。");
                }

                if (scan.AvailableInCategory == 0)
                {
                    payload["hint"] = "该 category 在当前 Editor 状态下没有可用计数器；"
                        + "多数计数器需要在 Play Mode 中、或有对应模块活动时才会有数据。";
                }

                complete(CucliCommandResult.Ok(CucliPaging.Envelope(page, items, payload)));
            };

            // The settle helper stops ticking when cancelled but cannot complete the job, so the
            // command keeps a cancellation observer of its own.
            watch = () =>
            {
                if (!context.Cancellation.IsCancellationRequested) return;
                EditorApplication.update -= watch;
                if (completed) return;
                completed = true;
                complete(CucliCommandResult.Fail(CucliErrors.Cancelled, "计数器读取在等待帧更新时被取消。"));
            };
            EditorApplication.update += watch;

            ProfilerSupport.SettleForCounters(finish, () => context.Cancellation.IsCancellationRequested);
        }

        // ---- object memory -----------------------------------------------------------------

        private static CucliCommandResult ObjectMemory(CucliCommandContext context)
        {
            string objectPath = context.Input.GetString("objectPath")
                ?? context.Input.GetString("object_path");
            if (string.IsNullOrWhiteSpace(objectPath))
            {
                return CucliCommandResult.Missing(
                    "objectPath",
                    "示例：--object-path \"Canvas/Panel/Button\" 或 Assets/Prefabs/Hero.prefab");
            }

            Object target = null;
            string source = null;

            GameObject found = GameObject.Find(objectPath.Trim());
            if (found != null)
            {
                target = found;
                source = "scene_hierarchy";
            }
            else
            {
                string normalized = CucliAssets.NormalizeAssetPath(objectPath);
                if (normalized != null)
                {
                    target = AssetDatabase.LoadMainAssetAtPath(normalized);
                    if (target != null) source = "asset_database";
                }
            }

            if (target == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "找不到对象 '" + objectPath + "'。场景路径走 GameObject.Find，"
                    + "因此 inactive 对象查不到；资产请用 Assets/... 开头的路径。",
                    new { objectPath = objectPath.Trim(), inactiveObjectsNotSearchable = true });
            }

            long bytes;
            try
            {
                bytes = Profiler.GetRuntimeMemorySizeLong(target);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "读取对象内存失败：" + exception.GetBaseException().Message);
            }

            return CucliCommandResult.Ok(new
            {
                objectName = target.name,
                objectType = target.GetType().Name,
                sizeBytes = bytes,
                sizeMb = Math.Round(bytes / 1048576.0, 3),
                source,
                assetPath = CucliAssets.GetAssetPath(target)
            });
        }

        // ---- snapshots ---------------------------------------------------------------------

        private static void SnapshotTakeDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            CucliCommandResult missing = CucliOptional.RequirePackage(
                ProfilerSnapshots.PackageName, "抓取内存快照");
            if (missing != null)
            {
                complete(missing);
                return;
            }

            string path = context.Input.GetString("snapshotPath")
                ?? context.Input.GetString("snapshot_path");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = ProfilerSnapshots.DefaultSnapshotPath();
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(CucliPaths.ProjectRoot, path);
            }

            int timeoutSeconds = Mathf.Clamp(
                context.Input.GetInt("timeoutSeconds",
                    context.Input.GetInt("timeout_seconds", DefaultSnapshotTimeoutSeconds)),
                1, 600);

            ProfilerSnapshots.SnapshotRequest request;
            string error;
            if (!ProfilerSnapshots.TryBeginTake(path, out request, out error))
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "无法开始抓取快照：" + error,
                    new { path, package = ProfilerSnapshots.PackageName }));
                return;
            }

            double deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
            bool completed = false;

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (completed) return;

                if (context.Cancellation.IsCancellationRequested)
                {
                    completed = true;
                    EditorApplication.update -= tick;
                    complete(CucliCommandResult.Fail(
                        CucliErrors.Cancelled,
                        "快照抓取在完成前被取消；已产生的部分文件不会自动删除，路径：" + path,
                        new { path }));
                    return;
                }

                if (request.Finished)
                {
                    completed = true;
                    EditorApplication.update -= tick;

                    if (!request.Success)
                    {
                        complete(CucliCommandResult.Fail(
                            CucliErrors.CommandFailed,
                            "Memory Profiler 报告快照抓取失败。",
                            new { path }));
                        return;
                    }

                    context.AddArtifact(path);
                    complete(CucliCommandResult.Ok(
                        ProfilerSnapshots.DescribeFile(path),
                        "内存快照已写入 " + path + "。"));
                    return;
                }

                if (EditorApplication.timeSinceStartup < deadline) return;

                completed = true;
                EditorApplication.update -= tick;
                // The callback may still land after this; the file is worth keeping either way, so the
                // path is reported rather than cleaned up.
                complete(CucliCommandResult.Fail(
                    CucliErrors.Timeout,
                    "等待 " + timeoutSeconds + " 秒后快照仍未完成。大工程抓取可能更慢，"
                    + "可用 timeoutSeconds 放宽；文件若已生成会在下面的路径里。",
                    new { path, timeoutSeconds, file = ProfilerSnapshots.DescribeFile(path) }));
            };

            EditorApplication.update += tick;
        }

        private static CucliCommandResult SnapshotList(CucliCommandContext context)
        {
            string searchPath = context.Input.GetString("searchPath")
                ?? context.Input.GetString("search_path");

            JArray searchedDirectories;
            List<string> files = ProfilerSnapshots.FindSnapshots(searchPath, out searchedDirectories);

            CucliPage<string> page = CucliPaging.Slice(files, context.Input, DefaultPageSize);

            var items = new JArray();
            foreach (string file in page.Items)
            {
                items.Add(ProfilerSnapshots.DescribeFile(file));
            }

            var payload = new JObject
            {
                ["searchedDirectories"] = searchedDirectories,
                ["searchedDirectoryCount"] = searchedDirectories.Count
            };

            if (files.Count == 0)
            {
                context.Warn("在这些目录里没有找到 *.snap：" + searchedDirectories.ToString(Newtonsoft.Json.Formatting.None));
            }

            return CucliCommandResult.Ok(CucliPaging.Envelope(page, items, payload));
        }

        private static CucliCommandResult SnapshotCompare(CucliCommandContext context)
        {
            string pathA = context.Input.GetString("snapshotA") ?? context.Input.GetString("snapshot_a");
            string pathB = context.Input.GetString("snapshotB") ?? context.Input.GetString("snapshot_b");

            if (string.IsNullOrWhiteSpace(pathA)) return CucliCommandResult.Missing("snapshotA");
            if (string.IsNullOrWhiteSpace(pathB)) return CucliCommandResult.Missing("snapshotB");

            string absoluteA = Path.IsPathRooted(pathA) ? pathA : Path.Combine(CucliPaths.ProjectRoot, pathA);
            string absoluteB = Path.IsPathRooted(pathB) ? pathB : Path.Combine(CucliPaths.ProjectRoot, pathB);

            if (!File.Exists(absoluteA))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "快照文件不存在：" + absoluteA, new { path = absoluteA });
            }

            if (!File.Exists(absoluteB))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "快照文件不存在：" + absoluteB, new { path = absoluteB });
            }

            JObject data = ProfilerSnapshots.Compare(absoluteA, absoluteB);
            return CucliCommandResult.Ok(
                data,
                "只比较了文件级元数据（大小/时间），没有做对象级 diff。");
        }

        // ---- frame debugger ----------------------------------------------------------------

        private static CucliCommandResult FrameDebuggerEnable(CucliCommandContext context)
        {
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "Frame Debugger 需要先暂停。请先执行 'cucli editor pause'，再重试。",
                    new { isPlaying = true, isPaused = false });
            }

            if (!ProfilerFrameDebugger.Available)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    ProfilerFrameDebugger.UnavailableReason);
            }

            // The utility only produces events once its window exists.
            EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");

            string error;
            if (!ProfilerFrameDebugger.TrySetEnabled(true, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
            }

            int eventCount = ProfilerFrameDebugger.EventCount(out error);
            var data = new JObject
            {
                ["enabled"] = true,
                ["eventCount"] = eventCount,
                ["eventCountAvailable"] = error == null
            };

            if (error != null)
            {
                context.Warn("Frame Debugger 已开启，但读取事件数失败：" + error);
            }

            return CucliCommandResult.Ok(data, "Frame Debugger 已开启。");
        }

        private static CucliCommandResult FrameDebuggerDisable(CucliCommandContext context)
        {
            if (!ProfilerFrameDebugger.Available)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, ProfilerFrameDebugger.UnavailableReason);
            }

            string error;
            if (!ProfilerFrameDebugger.TrySetEnabled(false, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
            }

            return CucliCommandResult.Ok(new { enabled = false }, "Frame Debugger 已关闭。");
        }

        private static CucliCommandResult FrameDebuggerEvents(CucliCommandContext context)
        {
            if (!ProfilerFrameDebugger.Available)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, ProfilerFrameDebugger.UnavailableReason);
            }

            int pageSize = Mathf.Clamp(
                context.Input.GetInt("pageSize", context.Input.GetInt("page_size", 50)), 1, 500);

            int cursor = 0;
            string rawCursor = context.Input.GetString("cursor");
            if (!string.IsNullOrWhiteSpace(rawCursor))
            {
                int parsed;
                if (!int.TryParse(rawCursor.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    || parsed < 0)
                {
                    return CucliCommandResult.Invalid("cursor", "必须是非负整数偏移。");
                }

                cursor = parsed;
            }

            System.Collections.IList events;
            string error;
            if (!ProfilerFrameDebugger.TryGetEventCollection(out events, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
            }

            var options = CucliDescribe.OptionsFrom(context.Input);
            int total = events == null ? 0 : events.Count;
            int start = Math.Min(cursor, total);
            int end = Math.Min(start + pageSize, total);

            var items = new JArray();
            for (int index = start; index < end; index++)
            {
                context.ThrowIfCancelled();
                var entry = new JObject { ["index"] = index };
                JObject described = ProfilerFrameDebugger.DescribeEvent(events[index], options);
                foreach (JProperty property in described.Properties())
                {
                    entry[property.Name] = property.Value;
                }

                items.Add(entry);
            }

            var data = new JObject
            {
                ["events"] = items,
                ["totalEvents"] = total,
                ["pageSize"] = pageSize,
                ["cursor"] = start
            };

            if (end < total) data["nextCursor"] = end;

            return CucliCommandResult.Ok(data);
        }
    }
}
