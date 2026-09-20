using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli build ...</c> - player builds, build settings and build targets, aligned with the MCP
    /// <c>manage_build</c> action set.
    ///
    /// Machine contract (do not change it casually - agents and scripts poll these exact names):
    /// <list type="bullet">
    /// <item>the job handle key is <c>job_id</c> (snake_case);</item>
    /// <item>the state field is <c>result</c> with the values
    /// <c>pending|building|succeeded|failed|cancelled|skipped</c> (there is no "running");</item>
    /// <item>there is no percentage progress - only job level states;</item>
    /// <item><c>build</c> and <c>batch</c> return immediately and tell the caller how often to poll
    /// (<c>pollInterval</c>: 5s for a build, 10s for a batch).</item>
    /// </list>
    ///
    /// Builds span frames and can recompile (a platform switch triggers a full reimport), so this
    /// group is deferred and declares <see cref="MayReloadDomain"/>. The job table is persisted to
    /// <c>Library/CustomUnityCli/build/jobs.json</c> so a domain reload or an Editor restart never
    /// loses the state; a job that cannot be resumed is failed explicitly instead of hanging in
    /// "building" forever.
    /// </summary>
    internal sealed class BuildCommand : CucliCommand, ICucliDeferredCommand
    {
        private const int MaxRetainedJobs = 50;
        private const double BuildPollSeconds = 5.0;
        private const double BatchPollSeconds = 10.0;
        private const int StaleJobMinutes = 5;
        private const int MaxErrorMessages = 50;
        private const int MaxWarningMessages = 20;

        // Deliberately `static readonly`, not `const`. A const here would let the compiler delete the
        // Unity Build Profile code path entirely on pre-6000 editors, which turns "this feature is
        // version-gated" into "this feature silently vanished" - and it produced CS0162 for every
        // guarded block. The runtime check costs nothing and keeps the code honest and testable.
#if UNITY_6000_0_OR_NEWER
        private static readonly bool BuildProfilesSupported = true;
#else
        private static readonly bool BuildProfilesSupported = false;
#endif

        private static bool s_driverHooked;

        public override string Name => "build";

        public override string Summary =>
            "Player builds and build settings: build, poll status, switch platform, read/write player settings, build scenes, named build profiles and batch builds.";

        public override CucliActionInfo[] Actions => Declare(
            "status", "Report a build or batch job by job_id (defaults to the most recent one). Read-only.",
            "build", "Start a player build. Returns job_id and pollInterval immediately.",
            "platform", "Read or switch the active build target and its subtarget.",
            "settings", "Read or write a PlayerSettings entry for the active target.",
            "scenes", "Read or write EditorBuildSettings.scenes. Reading is paged.",
            "profiles", "List, read, save or activate named build configurations.",
            "batch", "Build several targets one after another. Returns job_id and pollInterval immediately.",
            "cancel", "Cancel a batch job. Single-target builds cannot be cancelled.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Edit;

        public override bool MutatesProject => true;

        /// <summary>A platform switch reimports assets and can recompile, so jobs must survive a reload.</summary>
        public override bool MayReloadDomain => true;

        public override string[] Effects => new[]
        {
            "project.build", "project.settings.write", "project.assets.write", "editor.state.write"
        };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""status"", ""build"", ""platform"", ""settings"", ""scenes"", ""profiles"", ""batch"", ""cancel""],
      ""default"": ""status""
    },
    ""jobId"": { ""type"": ""string"", ""description"": ""action=status/cancel: job handle (job_id)."" },
    ""job_id"": { ""type"": ""string"", ""description"": ""Alias of 'jobId'."" },
    ""target"": { ""type"": ""string"", ""description"": ""windows64|windows|osx|linux64|android|ios|webgl|uwp|tvos or a BuildTarget name."" },
    ""outputPath"": { ""type"": ""string"", ""description"": ""action=build: output path relative to the project root."" },
    ""output_path"": { ""type"": ""string"", ""description"": ""Alias of 'outputPath'."" },
    ""outputDir"": { ""type"": ""string"", ""description"": ""action=batch: replaces the default 'Builds' root."" },
    ""output_dir"": { ""type"": ""string"", ""description"": ""Alias of 'outputDir'."" },
    ""scenes"": { ""type"": ""array"", ""items"": {}, ""description"": ""Scene asset paths, or objects with path/enabled."" },
    ""development"": { ""type"": ""boolean"", ""default"": false },
    ""options"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""clean_build, auto_run, deep_profiling, ..."" },
    ""subtarget"": { ""type"": ""string"", ""description"": ""player (default) or server."" },
    ""scriptingBackend"": { ""type"": ""string"", ""description"": ""action=build: mono or il2cpp."" },
    ""scripting_backend"": { ""type"": ""string"", ""description"": ""Alias of 'scriptingBackend'."" },
    ""profile"": { ""type"": ""string"", ""description"": ""action=build/profiles: named profile or a BuildProfile asset path."" },
    ""property"": { ""type"": ""string"", ""aliases"": [""key""], ""description"": ""action=settings: product_name|company_name|version|bundle_id|scripting_backend|defines|architecture。也接受 --key。"" },
    ""value"": { ""type"": ""string"", ""description"": ""action=settings: omit to read, provide to write."" },
    ""activate"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=profiles: activate the named profile."" },
    ""save"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=profiles: create or update the named profile."" },
    ""profiles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""action=batch: profile names to build, mutually exclusive with 'targets'."" },
    ""targets"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""action=batch: build targets, mutually exclusive with 'profiles'."" },
    ""append"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=scenes: append instead of replacing the list."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=scenes: required to clear the scene list."" },
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
                case "status": return Status(context);
                case "platform": return Platform(context);
                case "settings": return Settings(context);
                case "scenes": return Scenes(context);
                case "profiles": return Profiles(context);
                case "cancel": return Cancel(context);
                case "build":
                case "batch":
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "Action '" + action + "' must be dispatched through the deferred path.");
                default:
                    return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string action = ResolveAction(context, "status");
            switch (action)
            {
                case "build":
                    complete(StartBuild(context));
                    return;
                case "batch":
                    complete(StartBatch(context));
                    return;
                default:
                    complete(Execute(context));
                    return;
            }
        }

        /// <summary>
        /// Re-arms the build driver after every domain reload. A platform switch in the middle of a
        /// batch always reloads, so without this the batch would stall until the next CLI call.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void ResumeOnLoad()
        {
            List<CucliBuildJob> jobs = LoadJobs();
            bool changed = RecoverInterruptedJobs(jobs);
            if (changed) SaveJobs(jobs);
            if (FindActive(jobs) != null) HookDriver();
        }

        // ---------------------------------------------------------------- actions

        private static CucliCommandResult Status(CucliCommandContext context)
        {
            string jobId = context.Input.GetString("jobId") ?? context.Input.GetString("job_id");
            List<CucliBuildJob> jobs = LoadJobs();
            if (RecoverInterruptedJobs(jobs)) SaveJobs(jobs);

            CucliBuildJob job = null;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                job = FindJob(jobs, jobId.Trim());
                if (job == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.JobInvalid,
                        "找不到构建作业 '" + jobId + "'。作业表最多保留 " + MaxRetainedJobs + " 条记录。",
                        new { job_id = jobId, jobs = jobs.Count });
                }
            }
            else if (jobs.Count > 0)
            {
                job = jobs.OrderByDescending(item => item.StartedAt ?? string.Empty, StringComparer.Ordinal).First();
            }

            if (job == null)
            {
                return CucliCommandResult.Ok(
                    new { jobs = new JArray(), count = 0, activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString() },
                    "当前没有构建作业记录。");
            }

            JObject payload = DescribeJob(job);
            if (IsActive(job.Result))
            {
                payload["pollInterval"] = string.Equals(job.Kind, "batch", StringComparison.OrdinalIgnoreCase)
                    ? BatchPollSeconds
                    : BuildPollSeconds;
            }

            return CucliCommandResult.Ok(
                payload,
                IsActive(job.Result)
                    ? "构建作业 '" + job.JobId + "' 正在进行（result=" + job.Result + "），请继续轮询。"
                    : "构建作业 '" + job.JobId + "' 已结束，result=" + job.Result + "。");
        }

        private static CucliCommandResult Platform(CucliCommandContext context)
        {
            string requested = context.Input.GetString("target");
            string subtargetName = context.Input.GetString("subtarget");

            if (string.IsNullOrWhiteSpace(requested))
            {
                BuildTarget current = EditorUserBuildSettings.activeBuildTarget;
                BuildTargetGroup group = TargetGroup(current);
                return CucliCommandResult.Ok(new
                {
                    target = current.ToString(),
                    target_group = group.ToString(),
                    subtarget = EditorUserBuildSettings.standaloneBuildSubtarget.ToString(),
                    // Unity 2022.3 exposes the active group as selectedBuildTargetGroup;
                    // activeBuildTargetGroup only exists from 2023.1 onwards.
                    activeBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString()
                }, "当前激活的构建目标是 " + current + "。");
            }

            BuildTarget target;
            if (!TryResolveTarget(requested, out target))
            {
                return CucliCommandResult.Invalid(
                    "target",
                    "未知构建目标 '" + requested + "'。可用：windows64, windows, osx, linux64, android, ios, webgl, uwp, tvos 或任意 BuildTarget 枚举名。");
            }

            BuildTargetGroup targetGroup = TargetGroup(target);
            if (targetGroup == BuildTargetGroup.Unknown)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "无法为构建目标 '" + target + "' 解析 build target group。",
                    new { target = target.ToString() });
            }

            if (!BuildPipeline.IsBuildTargetSupported(targetGroup, target))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "平台 '" + target + "' 未安装。请在 Unity Hub 中为该版本安装对应构建模块。",
                    new { target = target.ToString(), target_group = targetGroup.ToString() });
            }

            BuildTarget previous = EditorUserBuildSettings.activeBuildTarget;
            if (previous == target)
            {
                return CucliCommandResult.Ok(new
                {
                    target = target.ToString(),
                    previous = previous.ToString(),
                    switched = false
                }, "已经处于平台 " + target + "。");
            }

            int subtarget = ResolveSubtarget(subtargetName);
            // Unity 2022.3 exposes only SwitchActiveBuildTarget. The AndSubtarget overload arrived in
            // 2023.1, and a subtarget requested here is applied afterwards when the API exists.
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, target);
            if (!switched)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "切换到平台 '" + target + "' 失败（模块缺失或被 Editor 拒绝）。",
                    new { target = target.ToString(), previous = previous.ToString() });
            }

            return CucliCommandResult.Ok(new
            {
                target = target.ToString(),
                previous = previous.ToString(),
                subtarget = ((StandaloneBuildSubtarget)subtarget).ToString(),
                switched = true
            }, "已切换构建平台 " + previous + " -> " + target + "；平台切换会触发全量资产重导入。");
        }

        private static CucliCommandResult Settings(CucliCommandContext context)
        {
            string property = context.Input.GetFirstString("property", "key");
            if (string.IsNullOrWhiteSpace(property))
            {
                return CucliCommandResult.Missing(
                    "property",
                    "Valid values: product_name, company_name, version, bundle_id, scripting_backend, defines, architecture.");
            }

            string key = NormalizeProperty(property);
            if (!IsKnownProperty(key))
            {
                return CucliCommandResult.Invalid(
                    "property",
                    "未知属性 '" + property + "'。Valid values: product_name, company_name, version, bundle_id, "
                    + "scripting_backend, defines, architecture.");
            }

            NamedBuildTarget namedTarget;
            string targetError;
            if (!ResolveNamedTarget(context, out namedTarget, out targetError))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, targetError);
            }

            string value = context.Input.GetString("value");
            if (value == null)
            {
                switch (key)
                {
                    case "productname":
                        return CucliCommandResult.Ok(new { property = key, value = PlayerSettings.productName });
                    case "companyname":
                        return CucliCommandResult.Ok(new { property = key, value = PlayerSettings.companyName });
                    case "version":
                        return CucliCommandResult.Ok(new { property = key, value = PlayerSettings.bundleVersion });
                    case "bundleid":
                        return CucliCommandResult.Ok(new { property = key, value = PlayerSettings.GetApplicationIdentifier(namedTarget) });
                    case "scriptingbackend":
                        return CucliCommandResult.Ok(new
                        {
                            property = key,
                            value = DescribeBackend(PlayerSettings.GetScriptingBackend(namedTarget))
                        });
                    case "defines":
                        return CucliCommandResult.Ok(new { property = key, value = PlayerSettings.GetScriptingDefineSymbols(namedTarget) });
                    default:
                    {
                        int architecture = PlayerSettings.GetArchitecture(namedTarget);
                        return CucliCommandResult.Ok(new
                        {
                            property = key,
                            value = DescribeArchitecture(architecture),
                            raw = architecture
                        });
                    }
                }
            }

            switch (key)
            {
                case "productname":
                    PlayerSettings.productName = value;
                    break;
                case "companyname":
                    PlayerSettings.companyName = value;
                    break;
                case "version":
                    PlayerSettings.bundleVersion = value;
                    break;
                case "bundleid":
                    PlayerSettings.SetApplicationIdentifier(namedTarget, value);
                    break;
                case "scriptingbackend":
                {
                    string backend = value.Trim().ToLowerInvariant();
                    if (backend != "mono" && backend != "il2cpp" && backend != "mono2x")
                    {
                        return CucliCommandResult.Invalid("value", "scripting_backend 只接受 mono 或 il2cpp。");
                    }

                    PlayerSettings.SetScriptingBackend(
                        namedTarget,
                        backend == "il2cpp" ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
                    break;
                }
                case "defines":
                    PlayerSettings.SetScriptingDefineSymbols(namedTarget, value);
                    break;
                default:
                {
                    int architecture;
                    if (!TryParseArchitecture(value, out architecture))
                    {
                        return CucliCommandResult.Invalid("value", "architecture 只接受 x86_64、arm64 或 universal。");
                    }

                    PlayerSettings.SetArchitecture(namedTarget, architecture);
                    break;
                }
            }

            AssetDatabase.SaveAssets();
            return CucliCommandResult.Ok(new
            {
                property = key,
                value,
                target = namedTarget.ToString(),
                written = true
            }, "已写入 PlayerSettings." + key + "（目标 " + namedTarget + "）。");
        }

        private static CucliCommandResult Scenes(CucliCommandContext context)
        {
            JToken token = context.Input.Token("scenes");
            bool hasValue = token != null && token.Type != JTokenType.Null;
            if (!hasValue)
            {
                var items = new List<JObject>();
                EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
                foreach (EditorBuildSettingsScene scene in scenes)
                {
                    if (scene == null) continue;
                    items.Add(new JObject
                    {
                        ["path"] = scene.path,
                        ["enabled"] = scene.enabled,
                        ["guid"] = scene.guid.ToString()
                    });
                }

                CucliPage<JObject> page = CucliPaging.Slice(items, context.Input, 100);
                return CucliCommandResult.Ok(
                    CucliPaging.Envelope(page, new JArray(page.Items.Cast<object>().ToArray())),
                    "构建场景列表共 " + page.Total + " 项（返回 " + page.Items.Count + " 项）。");
            }

            var requested = new List<EditorBuildSettingsScene>();
            var warnings = new List<string>();
            if (token.Type == JTokenType.Array)
            {
                foreach (JToken child in (JArray)token)
                {
                    var entry = child as JObject;
                    if (entry != null)
                    {
                        string path = entry["path"] == null ? null : entry["path"].ToString();
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            return CucliCommandResult.Invalid("scenes", "场景对象需要 'path' 字段。");
                        }

                        bool enabled = entry["enabled"] == null || entry["enabled"].ToObject<bool>();
                        requested.Add(new EditorBuildSettingsScene(path.Trim(), enabled));
                        continue;
                    }

                    string text = child.Type == JTokenType.String ? child.Value<string>() : child.ToString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    requested.Add(new EditorBuildSettingsScene(text.Trim(), true));
                }
            }
            else
            {
                foreach (string path in context.Input.GetStringList("scenes"))
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    requested.Add(new EditorBuildSettingsScene(path.Trim(), true));
                }
            }

            bool append = context.Input.GetBool("append", false);
            bool force = context.Input.GetBool("force", false);

            if (requested.Count == 0 && !append && !force)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "scenes 为空会清空构建场景列表。确认请加 --force true。");
            }

            var result = new List<EditorBuildSettingsScene>();
            if (append)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0])
                {
                    if (scene == null) continue;
                    result.Add(scene);
                    seen.Add(scene.path ?? string.Empty);
                }

                foreach (EditorBuildSettingsScene scene in requested)
                {
                    if (seen.Contains(scene.path ?? string.Empty)) continue;
                    result.Add(scene);
                    seen.Add(scene.path ?? string.Empty);
                }
            }
            else
            {
                result.AddRange(requested);
            }

            foreach (EditorBuildSettingsScene scene in result)
            {
                if (scene == null || string.IsNullOrWhiteSpace(scene.path)) continue;
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) == null)
                {
                    warnings.Add("场景路径不存在或不是场景资产：" + scene.path);
                }
            }

            EditorBuildSettings.scenes = result.ToArray();
            foreach (string message in warnings) context.Warn(message);

            var payload = new JArray();
            foreach (EditorBuildSettingsScene scene in result)
            {
                payload.Add(new JObject { ["path"] = scene.path, ["enabled"] = scene.enabled });
            }

            return CucliCommandResult.Ok(new
            {
                scenes = payload,
                count = result.Count,
                mode = append ? "append" : "replace"
            }, "已" + (append ? "追加" : "写入") + "构建场景列表，共 " + result.Count + " 项。");
        }

        private static CucliCommandResult Profiles(CucliCommandContext context)
        {
            string name = context.Input.GetString("profile");
            List<CucliBuildProfile> profiles = LoadProfiles();
            string activeProfile = ReadActiveProfile();

            if (string.IsNullOrWhiteSpace(name))
            {
                var items = new List<JObject>();
                foreach (CucliBuildProfile profile in profiles)
                {
                    items.Add(DescribeProfile(profile));
                }

                var unityProfiles = new JArray();
                if (BuildProfilesSupported)
                {
                    foreach (string guid in AssetDatabase.FindAssets("t:BuildProfile"))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        unityProfiles.Add(new JObject
                        {
                            ["path"] = path,
                            ["name"] = Path.GetFileNameWithoutExtension(path)
                        });
                    }
                }

                CucliPage<JObject> page = CucliPaging.Slice(items, context.Input, 100);
                JObject payload = CucliPaging.Envelope(page, new JArray(page.Items.Cast<object>().ToArray()), new JObject
                {
                    ["activeProfile"] = string.IsNullOrWhiteSpace(activeProfile) ? null : activeProfile,
                    ["mechanism"] = BuildProfilesSupported ? "cucli-named-profile+unity-build-profile" : "cucli-named-profile",
                    ["unityBuildProfiles"] = unityProfiles,
                    ["note"] = BuildProfilesSupported
                        ? "Unity 6 Build Profile 资产会被列出；cucli 命名配置始终可用。"
                        : "当前 Unity 版本没有 Build Profile API，profiles 提供 cucli 命名配置（BuildPlayerOptions 的命名预设）。"
                });

                return CucliCommandResult.Ok(
                    payload,
                    "共 " + page.Total + " 个 cucli 命名构建配置（返回 " + page.Items.Count + " 个）。");
            }

            string trimmed = name.Trim();
            CucliBuildProfile match = FindProfile(profiles, trimmed);

            if (context.Input.GetBool("activate", false))
            {
                return ActivateProfile(context, trimmed, match, profiles);
            }

            if (context.Input.GetBool("save", false))
            {
                if (match == null)
                {
                    match = new CucliBuildProfile { Name = trimmed };
                    profiles.Add(match);
                }

                string target;
                if (context.Input.Has("target"))
                {
                    string requested = context.Input.GetString("target");
                    BuildTarget resolved;
                    if (!TryResolveTarget(requested, out resolved))
                    {
                        return CucliCommandResult.Invalid("target", "未知构建目标 '" + requested + "'。");
                    }

                    target = resolved.ToString();
                }
                else
                {
                    target = match.Target;
                }

                match.Target = target;
                string outputPath = context.Input.GetString("outputPath") ?? context.Input.GetString("output_path");
                if (!string.IsNullOrWhiteSpace(outputPath)) match.OutputPath = outputPath.Trim();
                if (context.Input.Has("development")) match.Development = context.Input.GetBool("development", match.Development);
                if (context.Input.Has("subtarget")) match.Subtarget = context.Input.GetString("subtarget");
                if (context.Input.Has("options")) match.Options = context.Input.GetStringList("options");
                if (context.Input.Has("scenes")) match.Scenes = ResolveScenePaths(context);
                if (context.Input.Has("scriptingBackend") || context.Input.Has("scripting_backend"))
                {
                    match.ScriptingBackend = context.Input.GetString("scriptingBackend")
                        ?? context.Input.GetString("scripting_backend");
                }

                match.UpdatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveProfiles(profiles);

                return CucliCommandResult.Ok(
                    DescribeProfile(match),
                    "已保存命名构建配置 '" + match.Name + "'。");
            }

            if (match != null)
            {
                JObject payload = DescribeProfile(match);
                payload["active"] = string.Equals(activeProfile, match.Name, StringComparison.OrdinalIgnoreCase);
                return CucliCommandResult.Ok(payload, "命名构建配置 '" + match.Name + "'。");
            }

            if (BuildProfilesSupported && trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                if (AssetDatabase.LoadMainAssetAtPath(trimmed) == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound, "找不到 Build Profile 资产 '" + trimmed + "'。");
                }

                return CucliCommandResult.Ok(new
                {
                    profile = trimmed,
                    name = Path.GetFileNameWithoutExtension(trimmed),
                    mechanism = "unity-build-profile",
                    scenes = new JArray(),
                    note = "Unity Build Profile 资产由 Unity 自身管理；用 activate=true 将其设为当前 profile。"
                }, "Build Profile 资产 '" + trimmed + "'。");
            }

            return CucliCommandResult.Fail(
                CucliErrors.TargetNotFound,
                "找不到命名构建配置 '" + trimmed + "'。用 action=profiles --save true 创建，或先 list 查看已有配置。",
                new { profile = trimmed, available = profiles.Select(item => item.Name).ToArray() });
        }

        private static CucliCommandResult ActivateProfile(
            CucliCommandContext context,
            string name,
            CucliBuildProfile match,
            List<CucliBuildProfile> profiles)
        {
            if (BuildProfilesSupported && name.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string error;
                if (TryActivateUnityBuildProfile(name, out error))
                {
                    return CucliCommandResult.Ok(new
                    {
                        profile = name,
                        activated = true,
                        mechanism = "unity-build-profile"
                    }, "已激活 Unity Build Profile 资产 '" + name + "'。");
                }

                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "激活 Build Profile 资产失败：" + error,
                    new { profile = name });
            }

            if (match == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "找不到命名构建配置 '" + name + "'。",
                    new { profile = name, available = profiles.Select(item => item.Name).ToArray() });
            }

            WriteActiveProfile(match.Name);
            context.Warn("cucli 命名配置只影响 cucli 的 build/batch 默认值，不会改变 Unity 的 Build Profile 选择。");
            return CucliCommandResult.Ok(new
            {
                profile = match.Name,
                activated = true,
                mechanism = "cucli-named-profile",
                target = match.Target
            }, "已把 '" + match.Name + "' 设为 cucli 的默认构建配置。");
        }

        private static CucliCommandResult Cancel(CucliCommandContext context)
        {
            string jobId = context.Input.GetString("jobId") ?? context.Input.GetString("job_id");
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return CucliCommandResult.Missing("job_id", "cancel 需要 --job-id <batch job id>。");
            }

            List<CucliBuildJob> jobs = LoadJobs();
            CucliBuildJob job = FindJob(jobs, jobId.Trim());
            if (job == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.JobInvalid,
                    "找不到构建作业 '" + jobId + "'。",
                    new { job_id = jobId });
            }

            if (!string.Equals(job.Kind, "batch", StringComparison.OrdinalIgnoreCase))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "cancel 只对 batch 作业有效；单个 build 作业一旦开始就无法中途取消。",
                    new { job_id = job.JobId, kind = job.Kind });
            }

            if (!IsActive(job.Result))
            {
                return CucliCommandResult.Ok(new
                {
                    job_id = job.JobId,
                    state = job.Result,
                    cancelled = false
                }, "该批作业已结束（result=" + job.Result + "）。");
            }

            int skipped = SkipPending(job);
            job.Result = "cancelled";
            job.CompletedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            job.Error = null;
            SaveJobs(jobs);
            if (FindActive(jobs) == null) UnhookDriver();

            return CucliCommandResult.Ok(new
            {
                job_id = job.JobId,
                state = "cancelled",
                skipped,
                cancelled = true
            }, "已取消批作业 '" + job.JobId + "'，剩余 " + skipped + " 个子构建标记为 skipped。");
        }

        // ---------------------------------------------------------------- starting jobs

        private static CucliCommandResult StartBuild(CucliCommandContext context)
        {
            List<CucliBuildJob> jobs = LoadJobs();
            if (RecoverInterruptedJobs(jobs)) SaveJobs(jobs);

            CucliBuildJob active = FindActive(jobs);
            if (active != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WorkerBusy,
                    "已有构建作业 '" + active.JobId + "'（result=" + active.Result + "）在进行中，一次只允许一个构建。",
                    new { job_id = active.JobId, result = active.Result, retryAfterMs = 5000, retry_after_ms = 5000 });
            }

            string requestedTarget = context.Input.GetString("target");
            BuildTarget target;
            if (!TryResolveTarget(requestedTarget, out target))
            {
                return CucliCommandResult.Invalid(
                    "target",
                    "未知构建目标 '" + requestedTarget + "'。可用：windows64, windows, osx, linux64, android, ios, webgl, uwp, tvos 或任意 BuildTarget 枚举名。");
            }

            BuildTargetGroup group = TargetGroup(target);
            if (group == BuildTargetGroup.Unknown)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "无法为构建目标 '" + target + "' 解析 build target group。",
                    new { target = target.ToString() });
            }

            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "平台 '" + target + "' 未安装。请在 Unity Hub 中安装对应构建模块。",
                    new { target = target.ToString(), target_group = group.ToString() });
            }

            var job = new CucliBuildJob
            {
                Kind = "build",
                Result = "pending",
                Stage = "none",
                Platform = target.ToString(),
                StartedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Subtarget = ResolveSubtarget(context.Input.GetString("subtarget"))
            };

            string error;
            if (!ApplyBuildRequest(context, job, group, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.InvalidParameter, error);
            }

            job.JobId = NewJobId("build");
            jobs.Add(job);
            Trim(jobs);
            SaveJobs(jobs);
            HookDriver();

            return CucliCommandResult.Ok(new
            {
                job_id = job.JobId,
                result = job.Result,
                platform = job.Platform,
                output_path = job.OutputPath,
                subtarget = job.Subtarget == (int)StandaloneBuildSubtarget.Server ? "server" : "player",
                pollInterval = BuildPollSeconds,
                poll_interval = BuildPollSeconds,
                scenes = new JArray(job.Scenes.Cast<object>().ToArray())
            }, "构建作业 '" + job.JobId + "' 已排队；用 action=status --job-id " + job.JobId + " 轮询。");
        }

        private static CucliCommandResult StartBatch(CucliCommandContext context)
        {
            List<CucliBuildJob> jobs = LoadJobs();
            if (RecoverInterruptedJobs(jobs)) SaveJobs(jobs);

            CucliBuildJob active = FindActive(jobs);
            if (active != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.WorkerBusy,
                    "已有构建作业 '" + active.JobId + "'（result=" + active.Result + "）在进行中。",
                    new { job_id = active.JobId, result = active.Result, retryAfterMs = 10000, retry_after_ms = 10000 });
            }

            string[] targets = context.Input.GetStringList("targets");
            string[] profileNames = context.Input.GetStringList("profiles");

            if (targets.Length == 0 && profileNames.Length == 0)
            {
                return CucliCommandResult.Missing(
                    "targets",
                    "batch 需要 'targets'（目标名列表）或 'profiles'（命名配置列表），二选一。");
            }

            if (targets.Length > 0 && profileNames.Length > 0)
            {
                return CucliCommandResult.Invalid(
                    "targets", "targets 与 profiles 只能给一个（批构建要么按平台，要么按命名配置）。");
            }

            var selections = new List<CucliBatchSelection>();
            if (targets.Length > 0)
            {
                foreach (string name in targets)
                {
                    BuildTarget target;
                    if (!TryResolveTarget(name, out target))
                    {
                        return CucliCommandResult.Invalid("targets", "未知构建目标 '" + name + "'。");
                    }

                    BuildTargetGroup group = TargetGroup(target);
                    if (!BuildPipeline.IsBuildTargetSupported(group, target))
                    {
                        return CucliCommandResult.Fail(
                            CucliErrors.CommandFailed,
                            "平台 '" + target + "' 未安装，无法加入批构建。",
                            new { target = target.ToString() });
                    }

                    selections.Add(new CucliBatchSelection { Target = target, TargetName = name });
                }
            }
            else
            {
                List<CucliBuildProfile> profiles = LoadProfiles();
                foreach (string name in profileNames)
                {
                    CucliBuildProfile profile = FindProfile(profiles, name);
                    if (profile == null)
                    {
                        return CucliCommandResult.Fail(
                            CucliErrors.TargetNotFound,
                            "找不到命名构建配置 '" + name + "'。",
                            new { profile = name });
                    }

                    BuildTarget target;
                    if (!TryResolveTarget(profile.Target, out target))
                    {
                        return CucliCommandResult.Invalid(
                            "profiles", "命名配置 '" + name + "' 的目标 '" + profile.Target + "' 无效。");
                    }

                    selections.Add(new CucliBatchSelection { Target = target, TargetName = profile.Target, Profile = profile });
                }
            }

            string outputDir = context.Input.GetString("outputDir") ?? context.Input.GetString("output_dir");
            var batch = new CucliBuildJob
            {
                JobId = NewJobId("batch"),
                Kind = "batch",
                Result = "pending",
                Stage = "none",
                Platform = selections.Count + " targets",
                StartedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Total = selections.Count,
                Completed = 0,
                Builds = new List<CucliBuildJob>()
            };

            foreach (CucliBatchSelection selection in selections)
            {
                BuildTargetGroup group = TargetGroup(selection.Target);
                var child = new CucliBuildJob
                {
                    JobId = NewJobId("build"),
                    Kind = "build",
                    Result = "pending",
                    Stage = "none",
                    Platform = selection.Target.ToString(),
                    StartedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Subtarget = ResolveSubtarget(context.Input.GetString("subtarget"))
                };

                string error;
                if (selection.Profile != null)
                {
                    ApplyProfile(selection.Profile, child);
                }

                if (!ApplyBuildRequest(context, child, group, out error, true))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter,
                        "子构建 " + selection.Target + " 参数无效：" + error);
                }

                if (!string.IsNullOrWhiteSpace(outputDir))
                {
                    child.OutputPath = RebaseOutputPath(child.OutputPath, outputDir.Trim());
                }

                batch.Builds.Add(child);
            }

            jobs.Add(batch);
            Trim(jobs);
            SaveJobs(jobs);
            HookDriver();

            return CucliCommandResult.Ok(new
            {
                job_id = batch.JobId,
                result = batch.Result,
                total = batch.Total,
                targets = new JArray(batch.Builds.Select(item => (object)item.Platform).ToArray()),
                pollInterval = BatchPollSeconds,
                poll_interval = BatchPollSeconds
            }, "批构建作业 '" + batch.JobId + "' 已排队（" + batch.Total + " 个目标）；用 action=status --job-id "
                + batch.JobId + " 轮询。");
        }

        // ---------------------------------------------------------------- build driver

        private static void HookDriver()
        {
            if (s_driverHooked) return;
            s_driverHooked = true;
            EditorApplication.update += Drive;
        }

        private static void UnhookDriver()
        {
            if (!s_driverHooked) return;
            s_driverHooked = false;
            EditorApplication.update -= Drive;
        }

        /// <summary>
        /// Runs at most one build step per Editor tick. A platform switch can reload the domain, so
        /// every step is persisted before the Unity call that may trigger the reload.
        /// </summary>
        private static void Drive()
        {
            List<CucliBuildJob> jobs = LoadJobs();
            CucliBuildJob active = FindActive(jobs);
            if (active == null)
            {
                UnhookDriver();
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                FailJob(active, "Editor 进入了 Play Mode，构建无法继续。请停止 Play Mode 后重新发起构建。");
                SaveJobs(jobs);
                if (FindActive(jobs) == null) UnhookDriver();
                return;
            }

            if (string.Equals(active.Kind, "batch", StringComparison.OrdinalIgnoreCase)) DriveBatch(active, jobs);
            else DriveSingle(active, jobs);

            SaveJobs(jobs);
            if (FindActive(jobs) == null) UnhookDriver();
        }

        private static void DriveSingle(CucliBuildJob job, List<CucliBuildJob> jobs)
        {
            BuildTarget target;
            if (!TryResolveTarget(job.Platform, out target))
            {
                FailJob(job, "无法解析构建目标 '" + job.Platform + "'。");
                return;
            }

            // A "switching" stage that survived a reload means the platform switch itself reloaded the
            // domain; if the target is already active the work continues straight into the build.
            if (!string.Equals(job.Stage, "building", StringComparison.OrdinalIgnoreCase)
                && EditorUserBuildSettings.activeBuildTarget != target)
            {
                job.Stage = "switching";
                job.Result = "building";
                SaveJobs(jobs);
                if (!SwitchTarget(job, target)) return;
            }

            job.Stage = "building";
            job.Result = "building";
            SaveJobs(jobs);
            ExecuteBuild(job);
        }

        private static void DriveBatch(CucliBuildJob batch, List<CucliBuildJob> jobs)
        {
            if (string.Equals(batch.Result, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                SkipPending(batch);
                FinishBatch(batch);
                return;
            }

            CucliBuildJob current = FindCurrentSubJob(batch);
            if (current == null)
            {
                FinishBatch(batch);
                return;
            }

            batch.CurrentBuild = current.JobId;
            batch.Result = "building";

            BuildTarget target;
            if (!TryResolveTarget(current.Platform, out target))
            {
                FailJob(current, "无法解析构建目标 '" + current.Platform + "'。");
                batch.Completed++;
                batch.CurrentBuild = null;
                return;
            }

            if (string.Equals(current.Stage, "switching", StringComparison.OrdinalIgnoreCase))
            {
                if (EditorUserBuildSettings.activeBuildTarget != target)
                {
                    if (!SwitchTarget(current, target))
                    {
                        batch.Completed++;
                        batch.CurrentBuild = null;
                        return;
                    }
                }

                current.Stage = "building";
                SaveJobs(jobs);
            }
            else if (string.Equals(current.Stage, "none", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(current.Stage))
            {
                if (EditorUserBuildSettings.activeBuildTarget != target)
                {
                    current.Stage = "switching";
                    current.Result = "building";
                    SaveJobs(jobs);
                    if (!SwitchTarget(current, target))
                    {
                        batch.Completed++;
                        batch.CurrentBuild = null;
                        return;
                    }
                }

                current.Stage = "building";
                SaveJobs(jobs);
            }

            if (string.Equals(current.Stage, "building", StringComparison.OrdinalIgnoreCase))
            {
                current.Result = "building";
                SaveJobs(jobs);
                ExecuteBuild(current);
                batch.Completed++;
                batch.CurrentBuild = null;
            }
        }

        /// <summary>
        /// Switches the active build target. The stage is persisted before the call because a target
        /// switch reimports every asset and usually reloads the domain, which would otherwise leave a
        /// job stuck with no way to tell "the switch happened" from "the switch never ran".
        /// </summary>
        private static bool SwitchTarget(CucliBuildJob job, BuildTarget target)
        {
            BuildTargetGroup group = TargetGroup(target);
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                FailJob(job, "平台 '" + target + "' 未安装，无法切换。");
                return false;
            }

            bool switched;
            try
            {
                switched = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            }
            catch (Exception exception)
            {
                FailJob(job, "切换平台失败：" + exception.GetBaseException().Message);
                return false;
            }

            if (!switched)
            {
                FailJob(job, "切换到平台 '" + target + "' 失败（模块缺失或被 Editor 拒绝）。");
                return false;
            }

            return true;
        }

        private static void ExecuteBuild(CucliBuildJob job)
        {
            BuildTarget target;
            if (!TryResolveTarget(job.Platform, out target))
            {
                FailJob(job, "无法解析构建目标 '" + job.Platform + "'。");
                return;
            }

            BuildTargetGroup group = TargetGroup(target);
            BuildOptions options = BuildOptions.None;
            if (job.Options != null)
            {
                foreach (string name in job.Options) options |= ParseOption(name);
            }

            if (job.Development) options |= BuildOptions.Development;

            var buildOptions = new BuildPlayerOptions
            {
                scenes = job.Scenes ?? new string[0],
                locationPathName = job.OutputPath,
                target = target,
                targetGroup = group,
                options = options,
                subtarget = job.Subtarget
            };

            double started = EditorApplication.timeSinceStartup;
            BuildReport report = null;
            string exception = null;
            try
            {
                report = BuildPipeline.BuildPlayer(buildOptions);
            }
            catch (Exception caught)
            {
                exception = caught.GetBaseException().Message;
            }

            job.CompletedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            job.DurationSeconds = Math.Round(EditorApplication.timeSinceStartup - started, 2);
            job.Stage = "done";

            if (exception != null)
            {
                job.Result = "failed";
                job.Error = exception;
                job.Errors = new List<string> { exception };
                return;
            }

            if (report == null)
            {
                job.Result = "failed";
                job.Error = "BuildPipeline.BuildPlayer 返回 null，构建未启动。";
                job.Errors = new List<string> { job.Error };
                return;
            }

            BuildSummary summary = report.summary;
            job.TotalSizeMb = Math.Round(summary.totalSize / (1024.0 * 1024.0), 2);
            job.Errors = CollectMessages(report, LogType.Error, MaxErrorMessages);
            job.Warnings = CollectMessages(report, LogType.Warning, MaxWarningMessages);

#if UNITY_2023_1_OR_NEWER
            try
            {
                job.ErrorSummary = report.SummarizeErrors();
            }
            catch (Exception)
            {
                job.ErrorSummary = null;
            }
#endif

            switch (summary.result)
            {
                case BuildResult.Succeeded:
                    job.Result = "succeeded";
                    job.Error = null;
                    break;
                case BuildResult.Cancelled:
                    job.Result = "cancelled";
                    job.Error = "构建被取消。";
                    break;
                default:
                    job.Result = "failed";
                    job.Error = "构建失败：" + summary.totalErrors + " 个错误，"
                        + summary.totalWarnings + " 个警告。";
                    break;
            }
        }

        private static void FinishBatch(CucliBuildJob batch)
        {
            if (batch.Builds == null) batch.Builds = new List<CucliBuildJob>();

            int failed = 0;
            int succeeded = 0;
            int skipped = 0;
            foreach (CucliBuildJob child in batch.Builds)
            {
                if (child == null) continue;
                if (string.Equals(child.Result, "succeeded", StringComparison.OrdinalIgnoreCase)) succeeded++;
                else if (string.Equals(child.Result, "skipped", StringComparison.OrdinalIgnoreCase)) skipped++;
                else if (string.Equals(child.Result, "cancelled", StringComparison.OrdinalIgnoreCase)) skipped++;
                else if (!string.Equals(child.Result, "pending", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(child.Result, "building", StringComparison.OrdinalIgnoreCase)) failed++;
            }

            batch.Completed = succeeded + failed + skipped;
            batch.Total = batch.Builds.Count;
            batch.CurrentBuild = null;
            batch.CompletedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            batch.Stage = "done";

            if (string.Equals(batch.Result, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            batch.Result = failed > 0 ? "failed" : "succeeded";
            batch.Error = failed > 0 ? (failed + " 个子构建失败（成功 " + succeeded + "，跳过 " + skipped + "）。") : null;
        }

        private static int SkipPending(CucliBuildJob batch)
        {
            int skipped = 0;
            if (batch.Builds == null) return skipped;
            foreach (CucliBuildJob child in batch.Builds)
            {
                if (child == null) continue;
                if (!IsActive(child.Result)) continue;
                child.Result = "skipped";
                child.Stage = "done";
                child.CompletedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                child.Error = "批作业被取消。";
                skipped++;
            }

            return skipped;
        }

        private static void FailJob(CucliBuildJob job, string message)
        {
            job.Result = "failed";
            job.Stage = "done";
            job.Error = message;
            job.CompletedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (job.Errors == null) job.Errors = new List<string>();
            if (job.Errors.Count == 0) job.Errors.Add(message);
            if (job.Builds != null)
            {
                foreach (CucliBuildJob child in job.Builds)
                {
                    if (child != null && IsActive(child.Result))
                    {
                        child.Result = "skipped";
                        child.Stage = "done";
                        child.Error = "父批作业失败。";
                    }
                }
            }
        }

        private static CucliBuildJob FindCurrentSubJob(CucliBuildJob batch)
        {
            if (batch.Builds == null) return null;
            foreach (CucliBuildJob child in batch.Builds)
            {
                if (child != null && IsActive(child.Result)) return child;
            }

            return null;
        }

        private static List<string> CollectMessages(BuildReport report, LogType type, int limit)
        {
            var messages = new List<string>();
            BuildStep[] steps = report.steps;
            if (steps == null) return messages;

            foreach (BuildStep step in steps)
            {
                if (step.messages == null) continue;
                foreach (BuildStepMessage message in step.messages)
                {
                    if (message.type != type) continue;
                    if (string.IsNullOrWhiteSpace(message.content)) continue;
                    messages.Add(Truncate(message.content, 500));
                    if (messages.Count >= limit) return messages;
                }
            }

            return messages;
        }

        // ---------------------------------------------------------------- request handling

        private static bool ApplyBuildRequest(
            CucliCommandContext context,
            CucliBuildJob job,
            BuildTargetGroup group,
            out string error,
            bool fromBatch = false)
        {
            error = null;

            string profileName = context.Input.GetString("profile");
            if (!string.IsNullOrWhiteSpace(profileName) && !fromBatch)
            {
                CucliBuildProfile profile = FindProfile(LoadProfiles(), profileName.Trim());
                if (profile == null)
                {
                    if (BuildProfilesSupported && profileName.Trim().StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Warn("Unity Build Profile 资产 '" + profileName.Trim()
                            + "' 的设置不参与 cucli 构建参数解析；本次按显式参数与 EditorBuildSettings 构建。");
                    }
                    else
                    {
                        error = "找不到命名构建配置 '" + profileName.Trim() + "'。";
                        return false;
                    }
                }
                else
                {
                    ApplyProfile(profile, job);
                }
            }

            string scriptingBackend = context.Input.GetString("scriptingBackend")
                ?? context.Input.GetString("scripting_backend");
            if (!string.IsNullOrWhiteSpace(scriptingBackend))
            {
                string backend = scriptingBackend.Trim().ToLowerInvariant();
                if (backend != "mono" && backend != "il2cpp" && backend != "mono2x")
                {
                    error = "scripting_backend 只接受 mono 或 il2cpp。";
                    return false;
                }

                NamedBuildTarget namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
                try
                {
                    PlayerSettings.SetScriptingBackend(
                        namedTarget,
                        backend == "il2cpp" ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
                }
                catch (Exception exception)
                {
                    error = "写入 scripting_backend 失败：" + exception.GetBaseException().Message;
                    return false;
                }
            }

            if (context.Input.Has("development")) job.Development = context.Input.GetBool("development", job.Development);
            if (context.Input.Has("options")) job.Options = NormalizeOptions(context, context.Input.GetStringList("options"));
            if (context.Input.Has("subtarget")) job.Subtarget = ResolveSubtarget(context.Input.GetString("subtarget"));
            if (context.Input.Has("scenes")) job.Scenes = ResolveScenePaths(context);

            if (job.Scenes == null || job.Scenes.Length == 0)
            {
                var enabled = new List<string>();
                foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0])
                {
                    if (scene == null || !scene.enabled) continue;
                    if (string.IsNullOrWhiteSpace(scene.path)) continue;
                    enabled.Add(scene.path);
                }

                job.Scenes = enabled.ToArray();
            }

            if (job.Scenes.Length == 0)
            {
                error = "没有可构建的场景：请先在 EditorBuildSettings 中启用场景，或显式传 scenes。";
                return false;
            }

            string outputPath = context.Input.GetString("outputPath") ?? context.Input.GetString("output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                if (string.IsNullOrWhiteSpace(job.OutputPath))
                {
                    job.OutputPath = DefaultOutputPath(job.Platform, Application.productName);
                }
            }
            else
            {
                job.OutputPath = outputPath.Trim().Replace('\\', '/');
            }

            if (job.Options == null) job.Options = new string[0];
            return true;
        }

        private static void ApplyProfile(CucliBuildProfile profile, CucliBuildJob job)
        {
            if (profile == null) return;
            if (!string.IsNullOrWhiteSpace(profile.Target)) job.Platform = profile.Target;
            if (!string.IsNullOrWhiteSpace(profile.OutputPath)) job.OutputPath = profile.OutputPath;
            if (profile.Scenes != null && profile.Scenes.Length > 0) job.Scenes = profile.Scenes;
            if (profile.Options != null) job.Options = profile.Options;
            job.Development = profile.Development;
            if (!string.IsNullOrWhiteSpace(profile.Subtarget)) job.Subtarget = ResolveSubtarget(profile.Subtarget);
        }

        private static string[] NormalizeOptions(CucliCommandContext context, string[] requested)
        {
            var result = new List<string>();
            if (requested == null) return result.ToArray();

            foreach (string raw in requested)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string name = raw.Trim().ToLowerInvariant();
                switch (name)
                {
                    case "none": break;
                    case "clean_build": case "clean": result.Add("clean_build"); break;
                    case "auto_run": result.Add("auto_run"); break;
                    case "deep_profiling": result.Add("deep_profiling"); break;
                    case "compress_lz4": case "compresswithlz4": result.Add("compress_lz4"); break;
                    case "compress_lz4hc": result.Add("compress_lz4hc"); break;
                    case "strict_mode": result.Add("strict_mode"); break;
                    case "detailed_report": result.Add("detailed_report"); break;
                    case "allow_debugging": result.Add("allow_debugging"); break;
                    case "connect_profiler": result.Add("connect_profiler"); break;
                    case "scripts_only": result.Add("scripts_only"); break;
                    case "show_player": result.Add("show_player"); break;
                    case "include_tests": result.Add("include_tests"); break;
                    case "development": result.Add("development"); break;
                    default:
                        context.Warn("未知构建选项 '" + raw + "' 已被忽略。");
                        break;
                }
            }

            return result.ToArray();
        }

        private static BuildOptions ParseOption(string name)
        {
            switch (name)
            {
                case "clean_build": return BuildOptions.CleanBuildCache;
                case "auto_run": return BuildOptions.AutoRunPlayer;
                case "deep_profiling": return BuildOptions.EnableDeepProfilingSupport;
                case "compress_lz4": return BuildOptions.CompressWithLz4;
                case "compress_lz4hc": return BuildOptions.CompressWithLz4HC;
                case "strict_mode": return BuildOptions.StrictMode;
                case "detailed_report": return BuildOptions.DetailedBuildReport;
                case "allow_debugging": return BuildOptions.AllowDebugging;
                case "connect_profiler": return BuildOptions.ConnectWithProfiler;
                case "scripts_only": return BuildOptions.BuildScriptsOnly;
                case "show_player": return BuildOptions.ShowBuiltPlayer;
                case "include_tests": return BuildOptions.IncludeTestAssemblies;
                case "development": return BuildOptions.Development;
                default: return BuildOptions.None;
            }
        }

        private static string[] ResolveScenePaths(CucliCommandContext context)
        {
            JToken token = context.Input.Token("scenes");
            var scenes = new List<string>();
            if (token == null || token.Type == JTokenType.Null) return scenes.ToArray();

            if (token.Type == JTokenType.Array)
            {
                foreach (JToken child in (JArray)token)
                {
                    var entry = child as JObject;
                    if (entry != null)
                    {
                        string path = entry["path"] == null ? null : entry["path"].ToString();
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        bool enabled = entry["enabled"] == null || entry["enabled"].ToObject<bool>();
                        if (!enabled) continue;
                        scenes.Add(path.Trim());
                        continue;
                    }

                    string text = child.Type == JTokenType.String ? child.Value<string>() : child.ToString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    scenes.Add(text.Trim());
                }

                return scenes.ToArray();
            }

            foreach (string path in context.Input.GetStringList("scenes"))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                scenes.Add(path.Trim());
            }

            return scenes.ToArray();
        }

        private static string RebaseOutputPath(string path, string outputDir)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            string normalized = outputDir.Replace('\\', '/').TrimEnd('/');
            if (path.StartsWith("Builds/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized + "/" + path.Substring("Builds/".Length);
            }

            return normalized + "/" + Path.GetFileName(path);
        }

        // ---------------------------------------------------------------- job table

        private static List<CucliBuildJob> LoadJobs()
        {
            string path = JobsPath;
            if (!File.Exists(path)) return new List<CucliBuildJob>();

            try
            {
                List<CucliBuildJob> jobs = CucliJson.Deserialize<List<CucliBuildJob>>(
                    File.ReadAllText(path, CucliJson.Utf8NoBom));
                return jobs ?? new List<CucliBuildJob>();
            }
            catch (JsonException)
            {
                return new List<CucliBuildJob>();
            }
            catch (IOException)
            {
                return new List<CucliBuildJob>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<CucliBuildJob>();
            }
        }

        private static void SaveJobs(List<CucliBuildJob> jobs)
        {
            if (jobs == null) return;
            Trim(jobs);
            try
            {
                CucliPaths.WriteAtomic(JobsPath, CucliJson.Serialize(jobs));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void Trim(List<CucliBuildJob> jobs)
        {
            if (jobs.Count <= MaxRetainedJobs) return;
            jobs.Sort((left, right) => string.CompareOrdinal(
                right.StartedAt ?? string.Empty, left.StartedAt ?? string.Empty));

            var keep = new List<CucliBuildJob>();
            int removed = 0;
            foreach (CucliBuildJob job in jobs)
            {
                if (keep.Count >= MaxRetainedJobs && !IsActive(job.Result))
                {
                    removed++;
                    continue;
                }

                keep.Add(job);
            }

            if (removed == 0) return;
            jobs.Clear();
            jobs.AddRange(keep);
        }

        /// <summary>
        /// A job that is not terminal when the table is loaded again means the Editor reloaded or
        /// restarted in the middle of a step. Nothing that is still actionable is thrown away: a step
        /// that never started, a platform switch (idempotent) and a recently interrupted build are all
        /// kept and driven to completion. Only a job that has been sitting in an uninterruptible state
        /// for longer than <c>StaleJobMinutes</c> - a crashed Editor, a killed build - is failed
        /// loudly, so it never hangs in "building" forever.
        /// </summary>
        private static bool RecoverInterruptedJobs(List<CucliBuildJob> jobs)
        {
            bool changed = false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long staleMs = StaleJobMinutes * 60L * 1000L;

            foreach (CucliBuildJob job in jobs)
            {
                if (job == null) continue;

                if (job.Builds != null)
                {
                    foreach (CucliBuildJob child in job.Builds)
                    {
                        if (child == null || !IsActive(child.Result)) continue;
                        if (IsResumableStage(child.Stage)) continue;

                        long childWrite = ParseMillis(child.CompletedAt ?? child.StartedAt);
                        if (childWrite > 0 && now - childWrite <= staleMs) continue;
                        FailJob(child, "子构建在 domain reload 或 Editor 重启后中断，无法继续。");
                        changed = true;
                    }
                }

                if (!IsActive(job.Result)) continue;
                if (IsResumableStage(job.Stage)) continue;

                long lastWrite = ParseMillis(job.CompletedAt ?? job.StartedAt);
                if (lastWrite > 0 && now - lastWrite <= staleMs) continue;

                FailJob(job, "构建作业在 domain reload 或 Editor 重启后中断，无法继续（已启动的外部构建进程可能仍在运行）。");
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Stages that can still make progress after a reload: nothing has been started yet, or the
        /// platform switch is the only work in flight and it is idempotent.
        /// </summary>
        private static bool IsResumableStage(string stage)
        {
            return string.IsNullOrEmpty(stage)
                || string.Equals(stage, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stage, "switching", StringComparison.OrdinalIgnoreCase);
        }

        private static CucliBuildJob FindJob(List<CucliBuildJob> jobs, string jobId)
        {
            if (jobs == null || string.IsNullOrWhiteSpace(jobId)) return null;
            foreach (CucliBuildJob job in jobs)
            {
                if (job == null) continue;
                if (string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase)) return job;
                if (job.Builds == null) continue;
                foreach (CucliBuildJob child in job.Builds)
                {
                    if (child != null && string.Equals(child.JobId, jobId, StringComparison.OrdinalIgnoreCase))
                    {
                        return job;
                    }
                }
            }

            return null;
        }

        private static CucliBuildJob FindActive(List<CucliBuildJob> jobs)
        {
            foreach (CucliBuildJob job in jobs)
            {
                if (job != null && IsActive(job.Result)) return job;
            }

            return null;
        }

        private static bool IsActive(string result)
        {
            return string.Equals(result, "pending", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result, "building", StringComparison.OrdinalIgnoreCase);
        }

        private static long ParseMillis(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return 0L;
            DateTime parsed;
            if (!DateTime.TryParse(
                    iso,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out parsed))
            {
                return 0L;
            }

            return (long)(parsed - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static JObject DescribeJob(CucliBuildJob job)
        {
            var payload = new JObject
            {
                ["job_id"] = job.JobId,
                ["jobId"] = job.JobId,
                ["result"] = job.Result,
                ["kind"] = job.Kind,
                ["platform"] = job.Platform,
                ["output_path"] = job.OutputPath,
                ["started_at"] = job.StartedAt,
                ["duration_seconds"] = job.DurationSeconds,
                ["error"] = job.Error
            };

            if (!string.IsNullOrWhiteSpace(job.CompletedAt)) payload["completed_at"] = job.CompletedAt;
            if (job.Result == "succeeded" || job.Result == "failed")
            {
                payload["total_size_mb"] = job.TotalSizeMb;
            }

            if (job.Errors != null && job.Errors.Count > 0)
            {
                payload["errors"] = new JArray(job.Errors.Cast<object>().ToArray());
            }

            if (job.Warnings != null && job.Warnings.Count > 0)
            {
                payload["warnings"] = new JArray(job.Warnings.Cast<object>().ToArray());
            }

            if (!string.IsNullOrWhiteSpace(job.ErrorSummary)) payload["error_summary"] = job.ErrorSummary;

            if (string.Equals(job.Kind, "batch", StringComparison.OrdinalIgnoreCase))
            {
                payload["completed"] = job.Completed;
                payload["total"] = job.Total;
                payload["current_build"] = job.CurrentBuild;
                var builds = new JArray();
                if (job.Builds != null)
                {
                    foreach (CucliBuildJob child in job.Builds)
                    {
                        if (child == null) continue;
                        builds.Add(DescribeJob(child));
                    }
                }

                payload["builds"] = builds;
            }

            return payload;
        }

        // ---------------------------------------------------------------- settings helpers

        private static string NormalizeProperty(string property)
        {
            if (string.IsNullOrWhiteSpace(property)) return string.Empty;
            return property.Trim().ToLowerInvariant().Replace("_", string.Empty);
        }

        private static bool IsKnownProperty(string key)
        {
            switch (key)
            {
                case "productname":
                case "companyname":
                case "version":
                case "bundleid":
                case "scriptingbackend":
                case "defines":
                case "architecture":
                    return true;
                default:
                    return false;
            }
        }

        private static string DescribeBackend(ScriptingImplementation backend)
        {
            return backend == ScriptingImplementation.IL2CPP ? "il2cpp" : "mono";
        }

        private static string DescribeArchitecture(int architecture)
        {
            switch (architecture)
            {
                case 0: return "x86_64";
                case 1: return "arm64";
                case 2: return "universal";
                default: return "unknown";
            }
        }

        private static bool TryParseArchitecture(string value, out int architecture)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "x86_64":
                case "none":
                case "default":
                    architecture = 0;
                    return true;
                case "arm64":
                    architecture = 1;
                    return true;
                case "universal":
                    architecture = 2;
                    return true;
                default:
                    architecture = -1;
                    return false;
            }
        }

        private static bool ResolveNamedTarget(
            CucliCommandContext context,
            out NamedBuildTarget namedTarget,
            out string error)
        {
            error = null;
            string requested = context.Input.GetString("target");
            BuildTarget target;
            if (!TryResolveTarget(requested, out target))
            {
                namedTarget = default(NamedBuildTarget);
                error = "未知构建目标 '" + requested + "'。";
                return false;
            }

            BuildTargetGroup group = TargetGroup(target);
            if (group == BuildTargetGroup.Unknown)
            {
                namedTarget = default(NamedBuildTarget);
                error = "无法为构建目标 '" + target + "' 解析 build target group。";
                return false;
            }

            namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            return true;
        }

        // ---------------------------------------------------------------- profiles

        private static List<CucliBuildProfile> LoadProfiles()
        {
            string path = ProfilesPath;
            if (!File.Exists(path)) return new List<CucliBuildProfile>();

            try
            {
                List<CucliBuildProfile> profiles = CucliJson.Deserialize<List<CucliBuildProfile>>(
                    File.ReadAllText(path, CucliJson.Utf8NoBom));
                return profiles ?? new List<CucliBuildProfile>();
            }
            catch (JsonException)
            {
                return new List<CucliBuildProfile>();
            }
            catch (IOException)
            {
                return new List<CucliBuildProfile>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<CucliBuildProfile>();
            }
        }

        private static void SaveProfiles(List<CucliBuildProfile> profiles)
        {
            try
            {
                CucliPaths.WriteAtomic(ProfilesPath, CucliJson.Serialize(profiles));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static CucliBuildProfile FindProfile(List<CucliBuildProfile> profiles, string name)
        {
            if (profiles == null || string.IsNullOrWhiteSpace(name)) return null;
            foreach (CucliBuildProfile profile in profiles)
            {
                if (profile != null && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }

            return null;
        }

        private static JObject DescribeProfile(CucliBuildProfile profile)
        {
            var payload = new JObject
            {
                ["name"] = profile.Name,
                ["target"] = profile.Target,
                ["outputPath"] = profile.OutputPath,
                ["development"] = profile.Development,
                ["subtarget"] = profile.Subtarget,
                ["scriptingBackend"] = profile.ScriptingBackend,
                ["updatedAt"] = profile.UpdatedAt,
                ["scenes"] = profile.Scenes == null
                    ? new JArray()
                    : new JArray(profile.Scenes.Cast<object>().ToArray()),
                ["options"] = profile.Options == null
                    ? new JArray()
                    : new JArray(profile.Options.Cast<object>().ToArray())
            };

            return payload;
        }

        private static string ReadActiveProfile()
        {
            try
            {
                string path = ActiveProfilePath;
                return File.Exists(path) ? File.ReadAllText(path, CucliJson.Utf8NoBom).Trim() : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void WriteActiveProfile(string name)
        {
            try
            {
                CucliPaths.WriteAtomic(ActiveProfilePath, name ?? string.Empty);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Activates a Unity 6 Build Profile through reflection so this assembly keeps compiling on
        /// Unity 2022.3, where the type does not exist at all.
        /// </summary>
        private static bool TryActivateUnityBuildProfile(string assetPath, out string error)
        {
            error = null;
            Type profileType = null;
            foreach (Type candidate in CucliOptional.FindTypesByFullName("UnityEditor.Build.Profile.BuildProfile"))
            {
                profileType = candidate;
                break;
            }

            if (profileType == null)
            {
                error = "当前 Unity 版本没有 UnityEditor.Build.Profile.BuildProfile。";
                return false;
            }

            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(assetPath, profileType);
            if (asset == null)
            {
                error = "无法以 BuildProfile 类型加载 '" + assetPath + "'。";
                return false;
            }

            object result;
            string invokeError;
            if (!CucliOptional.TryInvokeStatic(profileType, "SetActiveBuildProfile", new object[] { asset }, out result, out invokeError))
            {
                error = invokeError ?? "BuildProfile.SetActiveBuildProfile 调用失败。";
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------- target helpers

        private static bool TryResolveTarget(string name, out BuildTarget target)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                target = EditorUserBuildSettings.activeBuildTarget;
                return true;
            }

            switch (name.Trim().ToLowerInvariant())
            {
                case "windows64": target = BuildTarget.StandaloneWindows64; return true;
                case "windows":
                case "windows32": target = BuildTarget.StandaloneWindows; return true;
                case "osx":
                case "macos": target = BuildTarget.StandaloneOSX; return true;
                case "linux64":
                case "linux": target = BuildTarget.StandaloneLinux64; return true;
                case "android": target = BuildTarget.Android; return true;
                case "ios": target = BuildTarget.iOS; return true;
                case "webgl": target = BuildTarget.WebGL; return true;
                case "uwp": target = BuildTarget.WSAPlayer; return true;
                case "tvos": target = BuildTarget.tvOS; return true;
                default:
                {
                    string trimmed = name.Trim();
                    int numeric;
                    if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                    {
                        target = default(BuildTarget);
                        return false;
                    }

                    BuildTarget parsed;
                    if (Enum.TryParse(trimmed, true, out parsed) && Enum.IsDefined(typeof(BuildTarget), parsed))
                    {
                        target = parsed;
                        return true;
                    }

                    target = default(BuildTarget);
                    return false;
                }
            }
        }

        private static BuildTargetGroup TargetGroup(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                    return BuildTargetGroup.Standalone;
                case BuildTarget.iOS: return BuildTargetGroup.iOS;
                case BuildTarget.Android: return BuildTargetGroup.Android;
                case BuildTarget.WebGL: return BuildTargetGroup.WebGL;
                case BuildTarget.WSAPlayer: return BuildTargetGroup.WSA;
                case BuildTarget.tvOS: return BuildTargetGroup.tvOS;
                default:
                {
                    if (string.Equals(target.ToString(), "VisionOS", StringComparison.OrdinalIgnoreCase))
                    {
                        BuildTargetGroup group;
                        if (Enum.TryParse("VisionOS", true, out group) && Enum.IsDefined(typeof(BuildTargetGroup), group))
                        {
                            return group;
                        }
                    }

                    return BuildTargetGroup.Unknown;
                }
            }
        }

        private static int ResolveSubtarget(string subtarget)
        {
            if (string.IsNullOrWhiteSpace(subtarget)) return (int)StandaloneBuildSubtarget.Player;
            return string.Equals(subtarget.Trim(), "server", StringComparison.OrdinalIgnoreCase)
                ? (int)StandaloneBuildSubtarget.Server
                : (int)StandaloneBuildSubtarget.Player;
        }

        private static string DefaultOutputPath(string targetName, string productName)
        {
            BuildTarget target;
            if (!TryResolveTarget(targetName, out target)) target = EditorUserBuildSettings.activeBuildTarget;

            string basePath = "Builds/" + target;
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return basePath + "/" + productName + ".exe";
                case BuildTarget.StandaloneOSX:
                    return basePath + "/" + productName + ".app";
                case BuildTarget.StandaloneLinux64:
                    return basePath + "/" + productName + ".x86_64";
                case BuildTarget.Android:
                    return basePath + "/" + productName
                        + (EditorUserBuildSettings.buildAppBundle ? ".aab" : ".apk");
                default:
                    return basePath + "/" + productName;
            }
        }

        private static string NewJobId(string kind)
        {
            return kind + "-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        private static string Truncate(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= limit ? value : value.Substring(0, limit) + "...";
        }

        private static string JobsPath =>
            Path.Combine(CucliPaths.Root, "build", "jobs.json");

        private static string ProfilesPath =>
            Path.Combine(CucliPaths.Root, "build", "profiles.json");

        private static string ActiveProfilePath =>
            Path.Combine(CucliPaths.Root, "build", "active-profile.txt");

        /// <summary>Internal bookkeeping used while a batch is expanded into sub-jobs.</summary>
        private sealed class CucliBatchSelection
        {
            public BuildTarget Target;
            public string TargetName;
            public CucliBuildProfile Profile;
        }
    }

    /// <summary>One build or batch job, persisted under <c>Library/CustomUnityCli/build/jobs.json</c>.</summary>
    internal sealed class CucliBuildJob
    {
        public string JobId { get; set; }
        public string Kind { get; set; }
        public string Result { get; set; }
        public string Stage { get; set; }
        public string Platform { get; set; }
        public string OutputPath { get; set; }
        public string StartedAt { get; set; }
        public string CompletedAt { get; set; }
        public double DurationSeconds { get; set; }
        public double TotalSizeMb { get; set; }
        public string Error { get; set; }
        public string ErrorSummary { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }
        public bool Development { get; set; }
        public string[] Scenes { get; set; }
        public string[] Options { get; set; }
        public int Subtarget { get; set; }
        public string ScriptingBackend { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public string CurrentBuild { get; set; }
        public List<CucliBuildJob> Builds { get; set; }
    }

    /// <summary>A named BuildPlayerOptions preset, stored under Library/CustomUnityCli/build.</summary>
    internal sealed class CucliBuildProfile
    {
        public string Name { get; set; }
        public string Target { get; set; }
        public string OutputPath { get; set; }
        public bool Development { get; set; }
        public string[] Options { get; set; }
        public string[] Scenes { get; set; }
        public string Subtarget { get; set; }
        public string ScriptingBackend { get; set; }
        public string UpdatedAt { get; set; }
    }
}
