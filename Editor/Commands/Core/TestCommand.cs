using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli test ...</c> - the Unity Test Framework surface: start an EditMode / PlayMode run and
    /// poll it, aligned with the MCP <c>run_tests</c> + <c>get_test_job</c> pair.
    ///
    /// Everything that touches <c>UnityEditor.TestTools.TestRunner.Api</c> goes through
    /// <see cref="CucliOptional"/> reflection: this assembly has no reference to the test framework
    /// assembly, so a project without <c>com.unity.test-framework</c> still compiles and gets a
    /// precise <c>OPTIONAL_PACKAGE_MISSING</c> failure instead of a compile error.
    ///
    /// The runner delivers progress through <c>ICallbacks</c>, which cannot be implemented at compile
    /// time here; it is implemented at runtime with a <see cref="DispatchProxy"/> that forwards to the
    /// static handlers below.
    ///
    /// Jobs are persisted under <c>Library/CustomUnityCli/test/jobs.json</c> and survive a domain
    /// reload; the per-test result details are intentionally memory-only, so a job recovered after a
    /// reload can legitimately report <c>result: null</c> (exactly like the MCP job table).
    /// </summary>
    internal sealed class TestCommand : CucliCommand
    {
        private const string PackageName = "com.unity.test-framework";
        private const string RunnerAssembly = "UnityEditor.TestRunner";
        private const string ApiTypeName = "UnityEditor.TestTools.TestRunner.Api.TestRunnerApi";
        private const string CallbacksTypeName = "UnityEditor.TestTools.TestRunner.Api.ICallbacks";
        private const string ExecutionSettingsTypeName = "UnityEditor.TestTools.TestRunner.Api.ExecutionSettings";
        private const string FilterTypeName = "UnityEditor.TestTools.TestRunner.Api.Filter";
        private const string TestModeTypeName = "UnityEditor.TestTools.TestRunner.Api.TestMode";

        private const int FailureCap = 25;
        private const int MaxRetainedJobs = 10;
        private const int DefaultInitTimeoutMs = 15000;
        private const int MaxInitTimeoutMs = 600000;
        private const int StuckThresholdMs = 60000;
        private const int MaxStoredResults = 20000;
        private const double PollSeconds = 5.0;

        private static readonly Dictionary<string, List<JObject>> ResultCache =
            new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> TruncatedResults =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static List<CucliTestJobState> s_cache;
        private static object s_callbacksProxy;
        private static bool s_callbacksRegistered;
        private static double s_lastPersistAt;

        public override string Name => "test";

        public override string Summary =>
            "Unity Test Framework: start an EditMode or PlayMode run, poll its progress and failures, read the results.";

        public override CucliActionInfo[] Actions => Declare(
            "status", "Poll a test job by jobId: progress, stuck detection and the first failures.",
            "run", "Start a test run. Returns jobId immediately; at most one job runs at a time.",
            "result", "Read the finished summary and the individual test cases (paged).",
            "list", "List the retained test jobs (at most 10). Paged.",
            "cancel", "Mark the current test job as cancelled (Unity cannot abort a run in flight).");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "editor.tests.run", "editor.state.write" };

        public override string OptionalPackage => PackageName;

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""status"", ""run"", ""result"", ""list"", ""cancel""],
      ""default"": ""status""
    },
    ""jobId"": { ""type"": ""string"", ""description"": ""action=status/result/cancel: test job id."" },
    ""job_id"": { ""type"": ""string"", ""description"": ""Alias of 'jobId'."" },
    ""mode"": { ""type"": ""string"", ""description"": ""action=run: EditMode (default) or PlayMode."" },
    ""includeDetails"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Return every test case (paged)."" },
    ""include_details"": { ""type"": ""boolean"", ""default"": false },
    ""includeFailedTests"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Return only the cases that did not pass."" },
    ""include_failed_tests"": { ""type"": ""boolean"", ""default"": false },
    ""testNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""test_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""groupNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""group_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""categoryNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""category_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""assemblyNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""assembly_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""initTimeout"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 600000, ""default"": 15000 },
    ""init_timeout"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 600000 },
    ""clearStuck"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=run: clear the stuck job instead of starting one."" },
    ""clear_stuck"": { ""type"": ""boolean"", ""default"": false },
    ""pageSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
    ""page_size"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
    ""cursor"": { ""type"": ""string"" },
    ""pageNumber"": { ""type"": ""integer"", ""minimum"": 1 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "status");
            switch (action)
            {
                case "status": return Status(context, false);
                case "result": return Status(context, true);
                case "list": return List(context);
                case "cancel": return Cancel(context);
                case "run": return Run(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ---------------------------------------------------------------- actions

        private static CucliCommandResult Status(CucliCommandContext context, bool detailsByDefault)
        {
            CucliCommandResult missing = CucliOptional.RequirePackage(PackageName, "读取测试作业状态");
            if (missing != null) return missing;

            string jobId = ReadJobId(context);
            List<CucliTestJobState> jobs = LoadJobs();
            if (RecoverOrphanedJobs(jobs)) PersistJobs(true);

            CucliTestJobState job = null;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                job = FindJob(jobs, jobId.Trim());
                if (job == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.JobInvalid,
                        "找不到测试作业 '" + jobId + "'。作业表最多保留 " + MaxRetainedJobs + " 条。",
                        new { jobId, job_id = jobId, jobs = jobs.Count });
                }
            }
            else
            {
                job = jobs.Count == 0
                    ? null
                    : jobs.OrderByDescending(item => item.LastUpdateUnixMs).First();
            }

            if (job == null)
            {
                return CucliCommandResult.Ok(
                    new { jobs = new JArray(), count = 0, running = 0 },
                    "当前没有测试作业记录。");
            }

            if (ApplyInitTimeout(job)) PersistJobs(true);

            bool includeDetails = detailsByDefault
                ? context.Input.GetBool("includeDetails", context.Input.GetBool("include_details", true))
                : context.Input.GetBool("includeDetails", context.Input.GetBool("include_details", false));
            bool includeFailedOnly = context.Input.GetBool(
                "includeFailedTests", context.Input.GetBool("include_failed_tests", false));

            return CucliCommandResult.Ok(
                Describe(job, context, includeDetails, includeFailedOnly),
                "测试作业 '" + job.JobId + "' 状态为 " + job.Status + "。");
        }

        private static CucliCommandResult List(CucliCommandContext context)
        {
            List<CucliTestJobState> jobs = LoadJobs();
            if (RecoverOrphanedJobs(jobs)) PersistJobs(true);

            var items = new List<JObject>();
            foreach (CucliTestJobState job in jobs.OrderByDescending(item => item.LastUpdateUnixMs))
            {
                var payload = new JObject
                {
                    ["jobId"] = job.JobId,
                    ["job_id"] = job.JobId,
                    ["status"] = job.Status,
                    ["mode"] = job.Mode,
                    ["startedUnixMs"] = job.StartedUnixMs,
                    ["finishedUnixMs"] = job.FinishedUnixMs == 0 ? (JToken)null : job.FinishedUnixMs,
                    ["lastUpdateUnixMs"] = job.LastUpdateUnixMs,
                    ["completed"] = job.Completed,
                    ["total"] = job.Total.HasValue ? (JToken)job.Total.Value : null,
                    ["passed"] = job.Passed,
                    ["failed"] = job.Failed,
                    ["skipped"] = job.Skipped,
                    ["error"] = job.Error
                };

                items.Add(payload);
            }

            CucliPage<JObject> page = CucliPaging.Slice(items, context.Input, MaxRetainedJobs);
            return CucliCommandResult.Ok(
                CucliPaging.Envelope(page, new JArray(page.Items.Cast<object>().ToArray()), new JObject
                {
                    ["maxRetainedJobs"] = MaxRetainedJobs
                }),
                "保留的测试作业共 " + page.Total + " 条（返回 " + page.Items.Count + " 条）。");
        }

        private static CucliCommandResult Cancel(CucliCommandContext context)
        {
            string jobId = ReadJobId(context);
            List<CucliTestJobState> jobs = LoadJobs();
            if (RecoverOrphanedJobs(jobs)) PersistJobs(true);

            CucliTestJobState job = null;
            if (!string.IsNullOrWhiteSpace(jobId)) job = FindJob(jobs, jobId.Trim());
            else job = FindRunningJob(jobs);

            if (job == null)
            {
                return CucliCommandResult.Ok(
                    new { cancelled = false, jobId = (string)null, job_id = (string)null },
                    "当前没有正在运行的测试作业。");
            }

            if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return CucliCommandResult.Ok(
                    new { cancelled = false, jobId = job.JobId, job_id = job.JobId, status = job.Status },
                    "测试作业 '" + job.JobId + "' 已结束（status=" + job.Status + "）。");
            }

            long now = NowMs();
            job.Status = "cancelled";
            job.Error = "已按请求标记为取消。Unity Test Runner 不支持中途终止，测试可能仍在后台继续。";
            job.FinishedUnixMs = now;
            job.LastUpdateUnixMs = now;
            s_callbacksRegistered = false;
            s_callbacksProxy = null;
            PersistJobs(true);
            context.Warn(job.Error);

            return CucliCommandResult.Ok(new
            {
                cancelled = true,
                jobId = job.JobId,
                job_id = job.JobId,
                status = job.Status
            }, "测试作业 '" + job.JobId + "' 已标记为取消。");
        }

        private static CucliCommandResult Run(CucliCommandContext context)
        {
            CucliCommandResult missing = CucliOptional.RequirePackage(PackageName, "运行测试");
            if (missing != null) return missing;

            List<CucliTestJobState> jobs = LoadJobs();
            if (RecoverOrphanedJobs(jobs)) PersistJobs(true);

            CucliTestJobState running = FindRunningJob(jobs);

            bool clearStuck = context.Input.GetBool("clearStuck", context.Input.GetBool("clear_stuck", false));
            if (clearStuck)
            {
                if (running == null)
                {
                    return CucliCommandResult.Ok(new { cleared = false }, "没有正在运行的测试作业需要清理。");
                }

                long clearedAt = NowMs();
                running.Status = "failed";
                running.Error = "已被 clearStuck 手动清理。";
                running.FinishedUnixMs = clearedAt;
                running.LastUpdateUnixMs = clearedAt;
                s_callbacksRegistered = false;
                s_callbacksProxy = null;
                PersistJobs(true);

                return CucliCommandResult.Ok(new
                {
                    cleared = true,
                    jobId = running.JobId,
                    job_id = running.JobId
                }, "已清理卡住的测试作业 '" + running.JobId + "'。");
            }

            if (running != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WorkerBusy,
                    "已有测试作业 '" + running.JobId + "' 在运行，一次只允许一个。请在 "
                    + (PollSeconds * 1000).ToString("0", CultureInfo.InvariantCulture) + " ms 后重试，"
                    + "或先用 --clear-stuck true 清障。",
                    new
                    {
                        reason = "tests_running",
                        retryAfterMs = 5000,
                        retry_after_ms = 5000,
                        jobId = running.JobId,
                        job_id = running.JobId
                    });
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CompilationInProgress,
                    "正在编译或导入资产，测试无法初始化。请等待 Editor 空闲后重试。",
                    new { isCompiling = EditorApplication.isCompiling, isUpdating = EditorApplication.isUpdating });
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "不能在 Play Mode 中启动测试。请先 'cucli editor stop' 退出 Play Mode。",
                    new { isPlaying = EditorApplication.isPlaying });
            }

            Type apiType = CucliOptional.FindType(RunnerAssembly, ApiTypeName);
            Type callbacksType = CucliOptional.FindType(RunnerAssembly, CallbacksTypeName);
            Type settingsType = CucliOptional.FindType(RunnerAssembly, ExecutionSettingsTypeName);
            Type filterType = CucliOptional.FindType(RunnerAssembly, FilterTypeName);
            Type testModeType = CucliOptional.FindType(RunnerAssembly, TestModeTypeName);
            if (apiType == null || callbacksType == null || settingsType == null || filterType == null || testModeType == null)
            {
                return CucliOptional.Missing(PackageName, "运行测试");
            }

            string modeName = context.Input.GetString("mode", "EditMode");
            object modeValue;
            string modeError;
            if (!TryParseMode(testModeType, modeName, out modeValue, out modeError))
            {
                return CucliCommandResult.Invalid("mode", modeError);
            }

            string modeText = string.Equals(modeName.Trim(), "PlayMode", StringComparison.OrdinalIgnoreCase)
                ? "PlayMode"
                : "EditMode";

            string[] testNames = ReadList(context, "testNames", "test_names");
            string[] groupNames = ReadList(context, "groupNames", "group_names");
            string[] categoryNames = ReadList(context, "categoryNames", "category_names");
            string[] assemblyNames = ReadList(context, "assemblyNames", "assembly_names");

            int initTimeout = context.Input.GetInt("initTimeout", context.Input.GetInt("init_timeout", 0));
            if (initTimeout < 0) initTimeout = 0;
            if (initTimeout > MaxInitTimeoutMs) initTimeout = MaxInitTimeoutMs;
            if (initTimeout == 0) initTimeout = DefaultInitTimeoutMs;

            object apiInstance = null;
            object proxy = null;
            string failure = null;
            try
            {
                apiInstance = ScriptableObject.CreateInstance(apiType);
                if (apiInstance == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "无法创建 TestRunnerApi 实例。",
                        new { apiType = ApiTypeName });
                }

                proxy = CreateCallbacksProxy(callbacksType, out failure);
                if (proxy == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "无法为 ICallbacks 创建运行时代理：" + failure,
                        new { callbacksType = CallbacksTypeName });
                }

                if (!RegisterCallbacks(apiType, apiInstance, callbacksType, proxy, out failure))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "注册测试回调失败：" + failure,
                        new { callbacksType = CallbacksTypeName });
                }

                object filter = Activator.CreateInstance(filterType);
                if (filter == null)
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, "无法创建 Filter 实例。");
                }

                SetMember(filterType, filter, "testMode", modeValue);
                SetMember(filterType, filter, "testNames", testNames.Length == 0 ? null : testNames);
                SetMember(filterType, filter, "groupNames", groupNames.Length == 0 ? null : groupNames);
                SetMember(filterType, filter, "categoryNames", categoryNames.Length == 0 ? null : categoryNames);
                SetMember(filterType, filter, "assemblyNames", assemblyNames.Length == 0 ? null : assemblyNames);

                Array filterArray = Array.CreateInstance(filterType, 1);
                filterArray.SetValue(filter, 0);

                object settings = Activator.CreateInstance(settingsType, new object[] { filterArray });
                if (settings == null)
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, "无法创建 ExecutionSettings 实例。");
                }

                MethodInfo execute = null;
                foreach (MethodInfo candidate in apiType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (candidate.Name != "Execute" || parameters.Length != 1) continue;
                    if (!parameters[0].ParameterType.IsAssignableFrom(settingsType)) continue;
                    execute = candidate;
                    break;
                }

                if (execute == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "TestRunnerApi.Execute(ExecutionSettings) 不存在；已安装的 test-framework 版本可能不兼容。",
                        new { apiType = ApiTypeName, packageVersion = CucliOptional.PackageVersion(PackageName) });
                }

                object runnerJobId = execute.Invoke(apiInstance, new object[] { settings });
                string runnerJob = runnerJobId == null ? null : runnerJobId.ToString();

                long now = NowMs();
                var job = new CucliTestJobState
                {
                    JobId = "test-" + Guid.NewGuid().ToString("N").Substring(0, 10),
                    Mode = modeText,
                    Status = "running",
                    StartedUnixMs = now,
                    LastUpdateUnixMs = now,
                    InitTimeoutMs = initTimeout,
                    IncludeDetails = context.Input.GetBool("includeDetails", context.Input.GetBool("include_details", false)),
                    IncludeFailedTests = context.Input.GetBool(
                        "includeFailedTests", context.Input.GetBool("include_failed_tests", false)),
                    RunnerJobId = runnerJob,
                    Failures = new List<CucliTestFailure>()
                };

                jobs.Add(job);
                TrimJobs(jobs);
                s_callbacksProxy = proxy;
                s_callbacksRegistered = true;
                s_cache = jobs;
                PersistJobs(true);

                if (string.Equals(modeText, "PlayMode", StringComparison.OrdinalIgnoreCase))
                {
                    context.Warn("PlayMode 测试会进入 Play Mode；若发生 domain reload，回调会丢失，作业会被标记为 failed。");
                }

                return CucliCommandResult.Ok(new
                {
                    jobId = job.JobId,
                    job_id = job.JobId,
                    runnerJobId = runnerJob,
                    status = job.Status,
                    mode = job.Mode,
                    includeDetails = job.IncludeDetails,
                    includeFailedTests = job.IncludeFailedTests,
                    initTimeoutMs = job.InitTimeoutMs,
                    pollInterval = PollSeconds,
                    poll_interval = PollSeconds,
                    total = (int?)null
                }, "测试作业 '" + job.JobId + "' 已启动（" + job.Mode + "）；用 action=status --job-id "
                    + job.JobId + " 轮询。");
            }
            catch (TargetInvocationException exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "启动测试失败：" + DescribeException(exception),
                    new { mode = modeText });
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "启动测试失败：" + exception.GetBaseException().Message,
                    new { mode = modeText });
            }
            finally
            {
                if (apiInstance != null)
                {
                    try
                    {
                        UnityEngine.Object scriptable = apiInstance as UnityEngine.Object;
                        if (scriptable != null) UnityEngine.Object.DestroyImmediate(scriptable);
                    }
                    catch (Exception)
                    {
                        // The job is already registered; failing cleanup must not fail the command.
                    }
                }
            }
        }

        // ---------------------------------------------------------------- test callbacks

        /// <summary>Called from the dynamically generated ICallbacks proxy.</summary>
        internal static void OnRunStarted(object testsToRun)
        {
            CucliTestJobState job = RunningJob();
            if (job == null) return;

            int total = CountLeafTests(testsToRun);
            job.Total = total > 0 ? (int?)total : null;
            job.LastUpdateUnixMs = NowMs();
            PersistJobs(true);
        }

        internal static void OnTestStarted(object test)
        {
            CucliTestJobState job = RunningJob();
            if (job == null) return;

            string fullName = CucliOptional.GetProperty(test, "FullName") as string;
            if (string.IsNullOrWhiteSpace(fullName)) fullName = CucliOptional.GetProperty(test, "Name") as string;
            job.CurrentTestFullName = fullName;
            job.CurrentTestStartedUnixMs = NowMs();
            job.LastUpdateUnixMs = job.CurrentTestStartedUnixMs;
            PersistJobs(false);
        }

        internal static void OnTestFinished(object result)
        {
            if (result == null) return;
            CucliTestJobState job = RunningJob();
            if (job == null) return;

            bool hasChildren = false;
            object hasChildrenValue = CucliOptional.GetProperty(result, "HasChildren");
            if (hasChildrenValue is bool) hasChildren = (bool)hasChildrenValue;
            if (hasChildren) return;

            string state = CucliOptional.GetProperty(result, "ResultState") as string;
            string fullName = CucliOptional.GetProperty(result, "FullName") as string;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                object test = CucliOptional.GetProperty(result, "Test");
                fullName = CucliOptional.GetProperty(test, "FullName") as string;
                if (string.IsNullOrWhiteSpace(fullName)) fullName = CucliOptional.GetProperty(test, "Name") as string;
            }

            if (string.IsNullOrWhiteSpace(fullName)) fullName = CucliOptional.GetProperty(result, "Name") as string;

            long now = NowMs();
            job.Completed++;
            job.LastFinishedTestFullName = fullName;
            job.LastFinishedUnixMs = now;
            job.LastUpdateUnixMs = now;
            job.CurrentTestFullName = null;
            job.CurrentTestStartedUnixMs = 0;

            bool failed = IsFailureState(state);
            if (failed)
            {
                if (job.Failures == null) job.Failures = new List<CucliTestFailure>();
                if (job.Failures.Count >= FailureCap)
                {
                    job.FailuresCapped = true;
                }
                else
                {
                    job.Failures.Add(new CucliTestFailure
                    {
                        FullName = fullName,
                        Message = Truncate(CucliOptional.GetProperty(result, "Message") as string, 400)
                    });
                }
            }

            CacheResult(job.JobId, result);
            PersistJobs(failed);
        }

        internal static void OnRunFinished(object result)
        {
            CucliTestJobState job = RunningJob();
            if (job == null) return;

            long now = NowMs();
            job.FinishedUnixMs = now;
            job.LastUpdateUnixMs = now;
            job.CurrentTestFullName = null;
            job.CurrentTestStartedUnixMs = 0;
            job.ResultAvailable = true;

            int passed = ToInt(CucliOptional.GetProperty(result, "PassCount"));
            int failed = ToInt(CucliOptional.GetProperty(result, "FailCount"));
            int skipped = ToInt(CucliOptional.GetProperty(result, "SkipCount"));
            job.Passed = passed;
            job.Failed = failed;
            job.Skipped = skipped;
            job.TotalTests = passed + failed + skipped > 0 ? passed + failed + skipped : job.Completed;
            if (!job.Total.HasValue || job.Total.Value <= 0) job.Total = job.TotalTests;
            job.DurationSeconds = ToDouble(CucliOptional.GetProperty(result, "Duration"));
            job.ResultState = CucliOptional.GetProperty(result, "ResultState") as string;

            if (failed > 0)
            {
                job.Status = "failed";
                job.Error = failed + " 个测试失败。";
            }
            else
            {
                job.Status = "succeeded";
                job.Error = null;
            }

            s_callbacksRegistered = false;
            PersistJobs(true);
        }

        private static CucliTestJobState RunningJob()
        {
            List<CucliTestJobState> jobs = LoadJobs();
            return FindRunningJob(jobs);
        }

        // ---------------------------------------------------------------- job table

        private static List<CucliTestJobState> LoadJobs()
        {
            if (s_cache != null) return s_cache;

            if (!File.Exists(JobsPath))
            {
                s_cache = new List<CucliTestJobState>();
                return s_cache;
            }

            try
            {
                List<CucliTestJobState> jobs = CucliJson.Deserialize<List<CucliTestJobState>>(
                    File.ReadAllText(JobsPath, CucliJson.Utf8NoBom));
                s_cache = jobs ?? new List<CucliTestJobState>();
            }
            catch (JsonException)
            {
                s_cache = new List<CucliTestJobState>();
            }
            catch (IOException)
            {
                s_cache = new List<CucliTestJobState>();
            }
            catch (UnauthorizedAccessException)
            {
                s_cache = new List<CucliTestJobState>();
            }

            return s_cache;
        }

        private static void PersistJobs(bool force)
        {
            if (s_cache == null) return;
            double now = EditorApplication.timeSinceStartup;
            if (!force && now - s_lastPersistAt < 1.0) return;
            s_lastPersistAt = now;

            try
            {
                CucliPaths.WriteAtomic(JobsPath, CucliJson.Serialize(s_cache));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TrimJobs(List<CucliTestJobState> jobs)
        {
            if (jobs.Count <= MaxRetainedJobs) return;
            jobs.Sort((left, right) => right.LastUpdateUnixMs.CompareTo(left.LastUpdateUnixMs));
            jobs.RemoveRange(MaxRetainedJobs, jobs.Count - MaxRetainedJobs);
        }

        private static CucliTestJobState FindJob(List<CucliTestJobState> jobs, string jobId)
        {
            if (jobs == null || string.IsNullOrWhiteSpace(jobId)) return null;
            foreach (CucliTestJobState job in jobs)
            {
                if (job != null && string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase)) return job;
            }

            return null;
        }

        private static CucliTestJobState FindRunningJob(List<CucliTestJobState> jobs)
        {
            if (jobs == null) return null;
            CucliTestJobState newest = null;
            foreach (CucliTestJobState job in jobs)
            {
                if (job == null) continue;
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) continue;
                if (newest == null || job.LastUpdateUnixMs > newest.LastUpdateUnixMs) newest = job;
            }

            return newest;
        }

        /// <summary>
        /// The callbacks live in this domain only. A job that is still "running" without them cannot
        /// ever receive RunFinished, so it is failed instead of hanging forever.
        /// </summary>
        private static bool RecoverOrphanedJobs(List<CucliTestJobState> jobs)
        {
            if (s_callbacksRegistered) return false;

            bool changed = false;
            long now = NowMs();
            foreach (CucliTestJobState job in jobs)
            {
                if (job == null) continue;
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) continue;

                job.Status = "failed";
                job.Error = "作业在 domain reload 或 Editor 重启后失去回调，结果无法回收（Test Runner 可能仍在运行）。";
                job.FinishedUnixMs = now;
                job.LastUpdateUnixMs = now;
                changed = true;
            }

            return changed;
        }

        private static bool ApplyInitTimeout(CucliTestJobState job)
        {
            if (job == null || !string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) return false;
            if (job.Total.HasValue && job.Total.Value > 0) return false;

            int timeout = job.InitTimeoutMs > 0 ? job.InitTimeoutMs : DefaultInitTimeoutMs;
            long now = NowMs();
            if (now - job.StartedUnixMs <= timeout) return false;

            job.Status = "failed";
            job.Error = "测试未在 " + timeout + " ms 内启动（常见原因：正在编译、存在编译错误或场景未保存）。";
            job.FinishedUnixMs = now;
            job.LastUpdateUnixMs = now;
            s_callbacksRegistered = false;
            return true;
        }

        private static JObject Describe(
            CucliTestJobState job,
            CucliCommandContext context,
            bool includeDetails,
            bool includeFailedOnly)
        {
            long now = NowMs();

            var failures = new JArray();
            int failureCount = 0;
            if (job.Failures != null)
            {
                foreach (CucliTestFailure failure in job.Failures)
                {
                    if (failure == null) continue;
                    if (failureCount >= FailureCap) break;
                    failures.Add(new JObject
                    {
                        ["fullName"] = failure.FullName,
                        ["full_name"] = failure.FullName,
                        ["message"] = failure.Message
                    });
                    failureCount++;
                }
            }

            bool stuck = job.CurrentTestStartedUnixMs > 0
                && now - job.CurrentTestStartedUnixMs > StuckThresholdMs;
            string blockedReason = null;
            if (stuck)
            {
                if (!UnityEditorInternal.InternalEditorUtility.isApplicationActive) blockedReason = "editor_unfocused";
                else if (EditorApplication.isCompiling) blockedReason = "compiling";
                else if (EditorApplication.isUpdating) blockedReason = "asset_import";
                else blockedReason = "unknown";
            }

            var progress = new JObject
            {
                ["completed"] = job.Completed,
                ["total"] = job.Total.HasValue ? (JToken)job.Total.Value : null,
                ["currentTestFullName"] = job.CurrentTestFullName,
                ["current_test_full_name"] = job.CurrentTestFullName,
                ["currentTestStartedUnixMs"] = job.CurrentTestStartedUnixMs == 0
                    ? (JToken)null
                    : job.CurrentTestStartedUnixMs,
                ["lastFinishedTestFullName"] = job.LastFinishedTestFullName,
                ["lastFinishedUnixMs"] = job.LastFinishedUnixMs == 0 ? (JToken)null : job.LastFinishedUnixMs,
                ["stuckSuspected"] = stuck,
                ["stuck_suspected"] = stuck,
                ["editorIsFocused"] = UnityEditorInternal.InternalEditorUtility.isApplicationActive,
                ["editor_is_focused"] = UnityEditorInternal.InternalEditorUtility.isApplicationActive,
                ["blockedReason"] = blockedReason,
                ["blocked_reason"] = blockedReason,
                ["failuresSoFar"] = failures,
                ["failures_so_far"] = failures,
                ["failureCap"] = FailureCap,
                ["failuresCapped"] = job.FailuresCapped,
                ["failures_capped"] = job.FailuresCapped
            };

            var payload = new JObject
            {
                ["jobId"] = job.JobId,
                ["job_id"] = job.JobId,
                ["runnerJobId"] = job.RunnerJobId,
                ["status"] = job.Status,
                ["mode"] = job.Mode,
                ["startedUnixMs"] = job.StartedUnixMs,
                ["finishedUnixMs"] = job.FinishedUnixMs == 0 ? (JToken)null : job.FinishedUnixMs,
                ["lastUpdateUnixMs"] = job.LastUpdateUnixMs,
                ["progress"] = progress,
                ["error"] = job.Error
            };

            if (string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                payload["pollInterval"] = PollSeconds;
                payload["poll_interval"] = PollSeconds;
            }

            if (includeDetails || includeFailedOnly)
            {
                payload["result"] = DescribeResult(job, context, includeDetails, includeFailedOnly);
            }
            else
            {
                payload["result"] = null;
            }

            return payload;
        }

        private static JObject DescribeResult(
            CucliTestJobState job,
            CucliCommandContext context,
            bool includeDetails,
            bool includeFailedOnly)
        {
            if (!job.ResultAvailable)
            {
                return null;
            }

            List<JObject> cached;
            ResultCache.TryGetValue(job.JobId, out cached);
            if (cached == null)
            {
                context.Warn("作业是从 domain reload 恢复的，逐条测试结果只在内存中保留，"
                    + "因此 result.results 不可用（summary 仍然有效）。");
            }

            var summary = new JObject
            {
                ["total"] = job.TotalTests,
                ["passed"] = job.Passed,
                ["failed"] = job.Failed,
                ["skipped"] = job.Skipped,
                ["durationSeconds"] = job.DurationSeconds,
                ["resultState"] = job.ResultState,
                ["result_state"] = job.ResultState
            };

            var payload = new JObject
            {
                ["mode"] = job.Mode,
                ["summary"] = summary
            };

            if (cached == null)
            {
                payload["results"] = null;
                return payload;
            }

            List<JObject> selected = cached;
            if (!includeDetails && includeFailedOnly)
            {
                selected = cached.Where(item =>
                {
                    string state = item["state"] == null ? string.Empty : item["state"].ToString();
                    return !string.Equals(state, "Passed", StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            CucliPage<JObject> page = CucliPaging.Slice(selected, context.Input, 50);
            JObject envelope = CucliPaging.Envelope(
                page,
                new JArray(page.Items.Cast<object>().ToArray()),
                new JObject
                {
                    ["resultsTruncated"] = TruncatedResults.Contains(job.JobId),
                    ["detailsIncluded"] = includeDetails,
                    ["failedOnly"] = !includeDetails && includeFailedOnly
                });

            payload["results"] = envelope;
            return payload;
        }

        // ---------------------------------------------------------------- result cache

        private static void CacheResult(string jobId, object adaptor)
        {
            if (string.IsNullOrWhiteSpace(jobId) || adaptor == null) return;

            List<JObject> results;
            if (!ResultCache.TryGetValue(jobId, out results))
            {
                results = new List<JObject>();
                ResultCache[jobId] = results;
            }

            if (results.Count >= MaxStoredResults)
            {
                TruncatedResults.Add(jobId);
                return;
            }

            results.Add(new JObject
            {
                ["name"] = CucliOptional.GetProperty(adaptor, "Name") as string,
                ["fullName"] = CucliOptional.GetProperty(adaptor, "FullName") as string,
                ["state"] = CucliOptional.GetProperty(adaptor, "ResultState") as string,
                ["durationSeconds"] = ToDouble(CucliOptional.GetProperty(adaptor, "Duration")),
                ["message"] = Truncate(CucliOptional.GetProperty(adaptor, "Message") as string, 2000),
                ["stackTrace"] = Truncate(CucliOptional.GetProperty(adaptor, "StackTrace") as string, 4000),
                ["output"] = Truncate(CucliOptional.GetProperty(adaptor, "Output") as string, 4000)
            });
        }

        // ---------------------------------------------------------------- reflection plumbing

        private static object CreateCallbacksProxy(Type callbacksType, out string error)
        {
            error = null;
            try
            {
                MethodInfo factory = null;
                foreach (MethodInfo candidate in typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (candidate.Name != "Create") continue;
                    if (!candidate.IsGenericMethodDefinition) continue;
                    if (candidate.GetGenericArguments().Length != 2) continue;
                    if (candidate.GetParameters().Length != 0) continue;
                    factory = candidate;
                    break;
                }

                if (factory == null)
                {
                    error = "System.Reflection.DispatchProxy.Create<T, TProxy>() 不可用。";
                    return null;
                }

                MethodInfo closed = factory.MakeGenericMethod(callbacksType, typeof(CucliTestCallbackProxy));
                return closed.Invoke(null, null);
            }
            catch (Exception exception)
            {
                error = DescribeException(exception);
                return null;
            }
        }

        private static bool RegisterCallbacks(
            Type apiType,
            object apiInstance,
            Type callbacksType,
            object proxy,
            out string error)
        {
            error = null;
            try
            {
                MethodInfo register = null;
                foreach (MethodInfo candidate in apiType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (candidate.Name != "RegisterCallbacks") continue;
                    if (!candidate.IsGenericMethodDefinition) continue;
                    if (candidate.GetParameters().Length < 1) continue;
                    register = candidate;
                    break;
                }

                if (register == null)
                {
                    error = "TestRunnerApi.RegisterCallbacks<T>(T, int) 不存在。";
                    return false;
                }

                MethodInfo closed = register.MakeGenericMethod(callbacksType);
                ParameterInfo[] parameters = closed.GetParameters();
                object[] arguments = parameters.Length >= 2
                    ? new object[] { proxy, 0 }
                    : new object[] { proxy };
                closed.Invoke(apiInstance, arguments);
                return true;
            }
            catch (Exception exception)
            {
                error = DescribeException(exception);
                return false;
            }
        }

        private static bool TryParseMode(Type testModeType, string modeName, out object modeValue, out string error)
        {
            modeValue = null;
            error = null;

            string requested = string.IsNullOrWhiteSpace(modeName) ? "EditMode" : modeName.Trim();
            if (!string.Equals(requested, "EditMode", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requested, "PlayMode", StringComparison.OrdinalIgnoreCase))
            {
                error = "未知测试模式 '" + requested + "'。只接受 EditMode 或 PlayMode。";
                return false;
            }

            try
            {
                modeValue = Enum.Parse(testModeType, requested, true);
                return true;
            }
            catch (Exception exception)
            {
                error = "解析测试模式失败：" + exception.GetBaseException().Message;
                return false;
            }
        }

        private static void SetMember(Type type, object target, string name, object value)
        {
            try
            {
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }

                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.CanWrite) property.SetValue(target, value, null);
            }
            catch (Exception)
            {
                // The runner ignores filter values it does not understand; keep the run going.
            }
        }

        private static int CountLeafTests(object node)
        {
            if (node == null) return 0;

            object hasChildrenValue = CucliOptional.GetProperty(node, "HasChildren");
            bool hasChildren = hasChildrenValue is bool && (bool)hasChildrenValue;
            if (!hasChildren) return 1;

            object children = CucliOptional.GetProperty(node, "Children");
            var enumerable = children as IEnumerable;
            if (enumerable == null) return 0;

            int total = 0;
            foreach (object child in enumerable) total += CountLeafTests(child);
            return total;
        }

        // ---------------------------------------------------------------- helpers

        private static string ReadJobId(CucliCommandContext context)
        {
            return context.Input.GetString("jobId") ?? context.Input.GetString("job_id");
        }

        private static string[] ReadList(CucliCommandContext context, string camel, string snake)
        {
            string[] first = context.Input.GetStringList(camel);
            if (first != null && first.Length > 0) return first;
            return context.Input.GetStringList(snake) ?? new string[0];
        }

        private static bool IsFailureState(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            string normalized = state.Trim().ToLowerInvariant();
            return normalized.Contains("failed") || normalized.Contains("error");
        }

        private static int ToInt(object value)
        {
            if (value == null) return 0;
            if (value is int) return (int)value;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static double ToDouble(object value)
        {
            if (value == null) return 0d;
            if (value is double) return (double)value;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0d;
            }
        }

        private static string Truncate(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= limit ? value : value.Substring(0, limit) + "...";
        }

        private static string DescribeException(Exception exception)
        {
            if (exception == null) return "unknown error";
            Exception inner = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            return inner.GetBaseException().Message;
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static string JobsPath =>
            Path.Combine(CucliPaths.Root, "test", "jobs.json");
    }

    /// <summary>
    /// Runtime implementation of the test framework's <c>ICallbacks</c>. The interface type only
    /// exists when <c>com.unity.test-framework</c> is installed, so it is implemented by a
    /// <see cref="DispatchProxy"/> instead of a compile-time class.
    /// <c>DispatchProxy.Create</c> requires a public, non-sealed proxy type with a public
    /// parameterless constructor; keep this type public even though the rest of the command is
    /// internal.
    /// </summary>
    public class CucliTestCallbackProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod == null) return null;

            try
            {
                object first = args != null && args.Length > 0 ? args[0] : null;
                switch (targetMethod.Name)
                {
                    case "RunStarted":
                        TestCommand.OnRunStarted(first);
                        break;
                    case "TestStarted":
                        TestCommand.OnTestStarted(first);
                        break;
                    case "TestFinished":
                        TestCommand.OnTestFinished(first);
                        break;
                    case "RunFinished":
                        TestCommand.OnRunFinished(first);
                        break;
                }
            }
            catch (Exception)
            {
                // A callback that throws would break the test run; progress is best-effort.
            }

            return null;
        }
    }

    /// <summary>One test job, persisted under <c>Library/CustomUnityCli/test/jobs.json</c>.</summary>
    internal sealed class CucliTestJobState
    {
        public string JobId { get; set; }
        public string RunnerJobId { get; set; }
        public string Mode { get; set; }
        public string Status { get; set; }
        public long StartedUnixMs { get; set; }
        public long FinishedUnixMs { get; set; }
        public long LastUpdateUnixMs { get; set; }
        public int InitTimeoutMs { get; set; }
        public bool IncludeDetails { get; set; }
        public bool IncludeFailedTests { get; set; }
        public int Completed { get; set; }
        public int? Total { get; set; }
        public string CurrentTestFullName { get; set; }
        public long CurrentTestStartedUnixMs { get; set; }
        public string LastFinishedTestFullName { get; set; }
        public long LastFinishedUnixMs { get; set; }
        public string Error { get; set; }
        public bool FailuresCapped { get; set; }
        public List<CucliTestFailure> Failures { get; set; }
        public bool ResultAvailable { get; set; }
        public int TotalTests { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public double DurationSeconds { get; set; }
        public string ResultState { get; set; }
    }

    /// <summary>One failed test, capped at 25 entries per job.</summary>
    internal sealed class CucliTestFailure
    {
        public string FullName { get; set; }
        public string Message { get; set; }
    }
}
