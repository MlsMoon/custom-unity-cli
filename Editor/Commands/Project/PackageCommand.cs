using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
// UnityEditor also defines a legacy PackageInfo (the old Asset Store package record). Every use in
// this file means the Package Manager one, and a bare `List<PackageInfo>` would be ambiguous.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli package ...</c> - the Unity Package Manager surface, aligned with the MCP
    /// <c>manage_packages</c> action set.
    ///
    /// Package installs are asynchronous in Unity: <c>Client.Add</c> / <c>Remove</c> / <c>Embed</c>
    /// return a request that completes many Editor frames later. Those actions therefore hand back a
    /// job handle immediately and the job is finished by an <see cref="EditorApplication.update"/>
    /// tick, so the worker queue is never blocked by a package download.
    ///
    /// The job table lives in <see cref="SessionState"/> (survives a domain reload, which a package
    /// install almost always causes) and is mirrored to
    /// <c>Library/CustomUnityCli/package/jobs.json</c> so it also survives an Editor restart.
    ///
    /// Only <c>add_registry</c> / <c>remove_registry</c> touch <c>Packages/manifest.json</c>; every
    /// other key of that file belongs to Unity.
    /// </summary>
    internal sealed class PackageCommand : CucliCommand, ICucliDeferredCommand
    {
        private const string SessionKey = "CustomUnityCli.PackageJobs.V1";
        private const int MaxRetainedJobs = 50;
        private const int StaleJobMinutes = 5;
        private const double PollIntervalSeconds = 3.0;
        private const int DefaultListTimeoutSeconds = 20;
        private const int MaxDescriptionLength = 200;

        private const string IdentifierPattern = "^[a-z][a-z0-9._-]*(\\.[a-z0-9._-]+)+$";

        private static readonly Dictionary<string, Request> LiveRequests =
            new Dictionary<string, Request>(StringComparer.OrdinalIgnoreCase);

        private static bool s_ticking;

        public override string Name => "package";

        public override string Summary =>
            "Unity Package Manager: install, remove, embed, list, search, inspect packages and manage scoped registries.";

        public override CucliActionInfo[] Actions => Declare(
            "ping", "Report the Package Manager liveness facts: Unity version, installed package count, busy flags.",
            "status", "Poll a package job by job_id (defaults to the most recent one).",
            "list_packages", "List the packages registered in this project. Paged.",
            "search_packages", "Search the registry for a package by name or id. Paged.",
            "get_package_info", "Describe one installed package, including its dependencies.",
            "list_registries", "List the scoped registries declared in Packages/manifest.json.",
            "add_package", "Install a package. Returns a job handle; poll it with action=status.",
            "remove_package", "Uninstall a package. Returns a job handle; poll it with action=status.",
            "embed_package", "Embed a package into Packages/. Returns a job handle; poll it with action=status.",
            "add_registry", "Add a scoped registry to Packages/manifest.json (the file is backed up first).",
            "remove_registry", "Remove a scoped registry from Packages/manifest.json (the file is backed up first).",
            "resolve_packages", "Ask the Package Manager to re-resolve the project manifest.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        /// <summary>Registry edits write Packages/manifest.json, so the whole group is a mutation.</summary>
        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "project.packages.read", "project.packages.write", "project.settings.write", "project.assets.write"
        };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""ping"", ""status"", ""list_packages"", ""search_packages"", ""get_package_info"",
                 ""list_registries"", ""add_package"", ""remove_package"", ""embed_package"",
                 ""add_registry"", ""remove_registry"", ""resolve_packages""],
      ""default"": ""ping""
    },
    ""package"": { ""type"": ""string"", ""description"": ""action=add/remove/embed/get_package_info: package id, optionally name@version."" },
    ""packageId"": { ""type"": ""string"", ""description"": ""Alias of 'package'."" },
    ""package_id"": { ""type"": ""string"", ""description"": ""Alias of 'package'."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=remove_package: remove even when other packages depend on it."" },
    ""jobId"": { ""type"": ""string"", ""description"": ""action=status: job handle."" },
    ""job_id"": { ""type"": ""string"", ""description"": ""Alias of 'jobId'."" },
    ""query"": { ""type"": ""string"", ""description"": ""action=search_packages: search string."" },
    ""name"": { ""type"": ""string"", ""description"": ""action=add_registry/remove_registry: registry name."" },
    ""url"": { ""type"": ""string"", ""description"": ""action=add_registry/remove_registry: registry url."" },
    ""scopes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""action=add_registry: scope strings."" },
    ""offline"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=list_packages: use the local cache instead of the network."" },
    ""timeoutSeconds"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 600, ""default"": 20 },
    ""pageSize"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
    ""page_size"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
    ""cursor"": { ""type"": ""string"" },
    ""pageNumber"": { ""type"": ""integer"", ""minimum"": 1 },
    ""page_number"": { ""type"": ""integer"", ""minimum"": 1 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "ping");
            switch (action)
            {
                case "ping": return Ping();
                case "status": return Status(context);
                case "list_registries": return ListRegistries();
                case "get_package_info": return GetPackageInfo(context);
                case "add_registry": return AddRegistry(context);
                case "remove_registry": return RemoveRegistry(context);
                case "resolve_packages": return ResolvePackages(context);
                case "add_package":
                case "remove_package":
                case "embed_package":
                case "list_packages":
                case "search_packages":
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "Action '" + action + "' must be dispatched through the deferred path.");
                default:
                    return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string action = ResolveAction(context, "ping");
            switch (action)
            {
                case "add_package": StartPackageOperation(context, "add", complete); return;
                case "remove_package": StartPackageOperation(context, "remove", complete); return;
                case "embed_package": StartPackageOperation(context, "embed", complete); return;
                case "list_packages": StartListPackages(context, complete); return;
                case "search_packages": StartSearchPackages(context, complete); return;
                default:
                    complete(Execute(context));
                    return;
            }
        }

        // ---------------------------------------------------------------- synchronous actions

        private static CucliCommandResult Ping()
        {
            int installed = 0;
            try
            {
                installed = PackageInfo.GetAllRegisteredPackages().Length;
            }
            catch (Exception)
            {
                installed = 0;
            }

            return CucliCommandResult.Ok(new
            {
                unityVersion = Application.unityVersion,
                installedPackageCount = installed,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                manifestPath = ManifestPath,
                runningJobs = CountRunning(LoadJobs())
            }, "Package Manager 可用；已注册 " + installed + " 个包。");
        }

        private static CucliCommandResult Status(CucliCommandContext context)
        {
            string jobId = context.Input.GetString("jobId") ?? context.Input.GetString("job_id");
            List<CucliPackageJob> jobs = LoadJobs();
            if (RecoverStaleJobs(jobs)) SaveJobs(jobs);

            CucliPackageJob job = null;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                job = FindJob(jobs, jobId.Trim());
                if (job == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.JobInvalid,
                        "找不到包作业 '" + jobId + "'。归档最多保留 " + MaxRetainedJobs + " 个作业。",
                        new { job_id = jobId, jobs = jobs.Count });
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
                    "当前没有包作业记录。");
            }

            JObject payload = Describe(job);
            bool running = string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase);
            if (running) payload["pollInterval"] = PollIntervalSeconds;

            return CucliCommandResult.Ok(
                payload,
                running
                    ? "包作业仍在进行中，请继续轮询 action=status。"
                    : "包作业 '" + job.JobId + "' 状态为 " + job.Status + "。");
        }

        private static CucliCommandResult ListRegistries()
        {
            string error;
            JObject manifest = LoadManifest(out error);
            if (manifest == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    error ?? "读取 Packages/manifest.json 失败。",
                    new { manifest = ManifestPath });
            }

            var registries = new JArray();
            JArray scoped = manifest["scopedRegistries"] as JArray;
            if (scoped != null)
            {
                foreach (JToken token in scoped)
                {
                    var entry = token as JObject;
                    if (entry == null) continue;
                    registries.Add(new JObject
                    {
                        ["name"] = entry["name"] == null ? null : entry["name"].ToString(),
                        ["url"] = entry["url"] == null ? null : entry["url"].ToString(),
                        ["scopes"] = entry["scopes"] as JArray ?? new JArray()
                    });
                }
            }

            return CucliCommandResult.Ok(new
            {
                registries,
                count = registries.Count,
                manifestPath = ManifestPath
            }, "manifest.json 中声明了 " + registries.Count + " 个 scoped registry。");
        }

        private static CucliCommandResult GetPackageInfo(CucliCommandContext context)
        {
            string identifier = ReadPackageArgument(context);
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return CucliCommandResult.Missing("package", "Example: --package com.unity.textmeshpro");
            }

            string name = StripVersion(identifier.Trim());
            PackageInfo match = null;
            try
            {
                foreach (PackageInfo package in PackageInfo.GetAllRegisteredPackages())
                {
                    if (package != null && string.Equals(package.name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        match = package;
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "读取已注册包列表失败：" + exception.GetBaseException().Message);
            }

            if (match == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "包 '" + name + "' 未安装。可用 'cucli package search_packages --query " + name + "' 搜索。",
                    new { package = name });
            }

            var dependencies = new JArray();
            DependencyInfo[] dependencyList = match.dependencies;
            if (dependencyList != null)
            {
                foreach (DependencyInfo dependency in dependencyList)
                {
                    // DependencyInfo is a struct: it cannot be null, only empty.
                    if (string.IsNullOrEmpty(dependency.name)) continue;
                    dependencies.Add(new JObject
                    {
                        ["name"] = dependency.name,
                        ["version"] = dependency.version
                    });
                }
            }

            return CucliCommandResult.Ok(new
            {
                name = match.name,
                version = match.version,
                displayName = match.displayName,
                description = match.description,
                source = match.source.ToString(),
                resolvedPath = match.resolvedPath,
                assetPath = match.assetPath,
                author = CucliJson.ToToken(match.author),
                dependencies,
                dependencyCount = dependencies.Count,
                isDirectDependency = match.isDirectDependency
            }, "包 '" + match.name + "' 版本 " + match.version + "。");
        }

        private static CucliCommandResult AddRegistry(CucliCommandContext context)
        {
            string name = context.Input.GetString("name");
            string url = context.Input.GetString("url");
            string[] scopes = context.Input.GetStringList("scopes");

            if (string.IsNullOrWhiteSpace(name))
            {
                return CucliCommandResult.Missing("name", "Example: --name MyRegistry");
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return CucliCommandResult.Missing("url", "Example: --url https://packages.example.com");
            }

            if (scopes == null || scopes.Length == 0)
            {
                return CucliCommandResult.Missing(
                    "scopes",
                    "Provide at least one scope string, e.g. --scopes com.example,com.example.tools");
            }

            string error;
            JObject manifest = LoadManifest(out error);
            if (manifest == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, error ?? "读取 Packages/manifest.json 失败。");
            }

            var scoped = manifest["scopedRegistries"] as JArray;
            if (scoped == null)
            {
                scoped = new JArray();
                manifest["scopedRegistries"] = scoped;
            }

            foreach (JToken token in scoped)
            {
                var entry = token as JObject;
                if (entry == null) continue;
                string existingName = entry["name"] == null ? null : entry["name"].ToString();
                string existingUrl = entry["url"] == null ? null : entry["url"].ToString();
                if (string.Equals(existingName, name.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(existingUrl, url.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetExists,
                        "已存在同名或同 URL 的 scoped registry（name='" + existingName + "', url='" + existingUrl + "'）。",
                        new { name = name, url = url, existingName, existingUrl });
                }
            }

            scoped.Add(new JObject
            {
                ["name"] = name.Trim(),
                ["url"] = url.Trim(),
                ["scopes"] = new JArray(scopes.Select(scope => (object)scope.Trim()).ToArray())
            });

            if (!TryWriteManifest(manifest, context, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
            }

            Client.Resolve();
            context.Warn("已调用 Client.Resolve() 触发重新解析；包解析可能引发脚本重编译。");

            return CucliCommandResult.Ok(new
            {
                name = name.Trim(),
                url = url.Trim(),
                scopes,
                manifestPath = ManifestPath,
                resolved = true
            }, "已写入 scoped registry '" + name.Trim() + "' 并触发 Client.Resolve()。");
        }

        private static CucliCommandResult RemoveRegistry(CucliCommandContext context)
        {
            string name = context.Input.GetString("name");
            string url = context.Input.GetString("url");
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url))
            {
                return CucliCommandResult.Missing("name", "Provide 'name' or 'url' to identify the registry.");
            }

            string error;
            JObject manifest = LoadManifest(out error);
            if (manifest == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, error ?? "读取 Packages/manifest.json 失败。");
            }

            var scoped = manifest["scopedRegistries"] as JArray;
            if (scoped == null || scoped.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, "manifest.json 中没有 scopedRegistries。");
            }

            JObject match = null;
            foreach (JToken token in scoped)
            {
                var entry = token as JObject;
                if (entry == null) continue;
                string existingName = entry["name"] == null ? null : entry["name"].ToString();
                string existingUrl = entry["url"] == null ? null : entry["url"].ToString();
                bool nameMatches = !string.IsNullOrWhiteSpace(name)
                    && string.Equals(existingName, name.Trim(), StringComparison.OrdinalIgnoreCase);
                bool urlMatches = !string.IsNullOrWhiteSpace(url)
                    && string.Equals(existingUrl, url.Trim(), StringComparison.OrdinalIgnoreCase);
                if (nameMatches || urlMatches)
                {
                    match = entry;
                    break;
                }
            }

            if (match == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "没有匹配的 scoped registry（name='" + name + "', url='" + url + "'）。",
                    new { name, url });
            }

            string removedName = match["name"] == null ? null : match["name"].ToString();
            scoped.Remove(match);
            if (scoped.Count == 0) manifest.Remove("scopedRegistries");

            if (!TryWriteManifest(manifest, context, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error);
            }

            Client.Resolve();
            context.Warn("已调用 Client.Resolve() 触发重新解析；包解析可能引发脚本重编译。");

            return CucliCommandResult.Ok(new
            {
                name = removedName,
                remaining = scoped.Count,
                manifestPath = ManifestPath,
                resolved = true
            }, "已移除 scoped registry '" + removedName + "' 并触发 Client.Resolve()。");
        }

        private static CucliCommandResult ResolvePackages(CucliCommandContext context)
        {
            bool force = context.Input.GetBool("force", false);
            // Client.Resolve() has no overload taking a force flag; resolution always re-reads the
            // manifest, so the requested flag is reported back rather than forwarded.
            Client.Resolve();
            return CucliCommandResult.Ok(new
            {
                resolved = true,
                force,
                manifestPath = ManifestPath
            }, "已请求 Package Manager 重新解析工程 manifest。");
        }

        // ---------------------------------------------------------------- deferred actions

        private static void StartPackageOperation(
            CucliCommandContext context,
            string operation,
            Action<CucliCommandResult> complete)
        {
            string identifier = ReadPackageArgument(context);
            if (string.IsNullOrWhiteSpace(identifier))
            {
                complete(CucliCommandResult.Missing(
                    "package",
                    "Example: --package com.unity.textmeshpro@3.0.6"));
                return;
            }

            string normalized;
            string warning;
            if (!TryNormalizeIdentifier(identifier, out normalized, out warning))
            {
                complete(CucliCommandResult.Invalid(
                    "package",
                    "'" + identifier + "' 不是合法的包标识。允许 com.company.package[@version]、"
                    + "https/http/git/ssh URL、以 .git 结尾的地址或 file: 路径。"));
                return;
            }

            string packageName = IsRemoteSource(normalized) ? normalized : StripVersion(normalized);

            if (string.Equals(operation, "remove", StringComparison.OrdinalIgnoreCase)
                && !context.Input.GetBool("force", false))
            {
                JArray dependents = FindDependents(packageName);
                if (dependents.Count > 0)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.ConfirmationRequired,
                        "有 " + dependents.Count + " 个已安装包依赖 '" + packageName
                        + "'。确认卸载请加 --force true。",
                        new { package = packageName, dependents, force = false }));
                    return;
                }
            }

            Request request;
            try
            {
                switch (operation)
                {
                    case "add": request = Client.Add(normalized); break;
                    case "remove": request = Client.Remove(packageName); break;
                    default: request = Client.Embed(packageName); break;
                }
            }
            catch (Exception exception)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "提交 " + operation + " 请求失败：" + exception.GetBaseException().Message,
                    new { package = normalized, operation }));
                return;
            }

            string jobId = NewJobId(operation);
            List<CucliPackageJob> jobs = LoadJobs();
            var job = new CucliPackageJob
            {
                JobId = jobId,
                Operation = operation,
                Package = packageName,
                Status = "running",
                StartedUnixMs = NowMs(),
                LastUpdateUnixMs = NowMs(),
                ResultName = packageName
            };
            jobs.Add(job);
            RecoverStaleJobs(jobs);
            SaveJobs(jobs);
            LiveRequests[jobId] = request;
            EnsureTicker();

            if (!string.IsNullOrWhiteSpace(warning)) context.Warn(warning);
            context.Warn("包操作是异步的：请用 action=status --job-id " + jobId + " 轮询，建议间隔 "
                + PollIntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " 秒。");

            var payload = Describe(job);
            payload["pollInterval"] = PollIntervalSeconds;
            payload["_mcp_status"] = "pending";
            payload["_mcp_poll_interval"] = PollIntervalSeconds;

            complete(CucliCommandResult.Ok(
                payload,
                "已提交 " + operation + " 请求，作业 " + jobId + "。"));
        }

        private static void StartListPackages(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            bool offline = context.Input.GetBool("offline", false);
            if (offline)
            {
                complete(BuildPackageList(context, CachedPackages(), true));
                return;
            }

            ListRequest request;
            try
            {
                request = Client.List(false);
            }
            catch (Exception exception)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Client.List 调用失败：" + exception.GetBaseException().Message));
                return;
            }

            WaitForRequest(
                context,
                request,
                complete,
                () =>
                {
                    var packages = new List<PackageInfo>();
                    try
                    {
                        PackageCollection collection = request.Result;
                        if (collection != null)
                        {
                            foreach (PackageInfo package in collection)
                            {
                                if (package != null) packages.Add(package);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Fall through to the cached list below.
                    }

                    if (packages.Count == 0) packages = CachedPackages();
                    return BuildPackageList(context, packages, false);
                },
                () =>
                {
                    context.Warn("等待 Client.List 超时，已回退到本地已注册包缓存；加 --offline true 可跳过网络查询。");
                    return BuildPackageList(context, CachedPackages(), true);
                });
        }

        private static void StartSearchPackages(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string query = context.Input.GetString("query");
            if (string.IsNullOrWhiteSpace(query))
            {
                complete(CucliCommandResult.Missing("query", "Example: --query textmeshpro"));
                return;
            }

            SearchRequest request;
            try
            {
                request = Client.Search(query.Trim());
            }
            catch (Exception exception)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Client.Search 调用失败：" + exception.GetBaseException().Message));
                return;
            }

            WaitForRequest(
                context,
                request,
                complete,
                () =>
                {
                    var items = new List<JObject>();
                    PackageInfo[] result = null;
                    try
                    {
                        result = request.Result;
                    }
                    catch (Exception)
                    {
                        result = null;
                    }

                    if (result != null)
                    {
                        foreach (PackageInfo package in result)
                        {
                            if (package == null) continue;
                            items.Add(new JObject
                            {
                                ["name"] = package.name,
                                ["version"] = package.version,
                                ["displayName"] = package.displayName,
                                ["description"] = Truncate(package.description, MaxDescriptionLength)
                            });
                        }
                    }

                    CucliPage<JObject> page = CucliPaging.Slice(items, context.Input, 100);
                    return CucliCommandResult.Ok(
                        CucliPaging.Envelope(page, new JArray(page.Items.Cast<object>().ToArray()), new JObject
                        {
                            ["query"] = query,
                            ["offline"] = false
                        }),
                        "搜索到 " + page.Total + " 个包（返回 " + page.Items.Count + " 个）。");
                },
                () => CucliCommandResult.Fail(
                    CucliErrors.Timeout,
                    "等待 Client.Search 超时。请检查网络或 registry 配置后重试。",
                    new { query }));
        }

        private static void WaitForRequest(
            CucliCommandContext context,
            Request request,
            Action<CucliCommandResult> complete,
            Func<CucliCommandResult> onCompleted,
            Func<CucliCommandResult> onTimeout)
        {
            int timeoutSeconds = Mathf.Clamp(
                context.Input.GetInt("timeoutSeconds", DefaultListTimeoutSeconds), 1, 600);
            double deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
            bool finished = false;

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (finished) return;

                if (context.Cancellation.IsCancellationRequested)
                {
                    finished = true;
                    EditorApplication.update -= tick;
                    complete(CucliCommandResult.Fail(
                        CucliErrors.Cancelled, "等待 Package Manager 请求时被取消。"));
                    return;
                }

                try
                {
                    if (request.IsCompleted)
                    {
                        finished = true;
                        EditorApplication.update -= tick;
                        complete(onCompleted());
                        return;
                    }
                }
                catch (Exception exception)
                {
                    finished = true;
                    EditorApplication.update -= tick;
                    complete(CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "Package Manager 请求异常：" + exception.GetBaseException().Message));
                    return;
                }

                if (EditorApplication.timeSinceStartup < deadline) return;

                finished = true;
                EditorApplication.update -= tick;
                complete(onTimeout());
            };

            EditorApplication.update += tick;
        }

        // ---------------------------------------------------------------- job plumbing

        private static void EnsureTicker()
        {
            if (s_ticking) return;
            s_ticking = true;
            EditorApplication.update += Tick;
        }

        /// <summary>
        /// Finishes every package request that has completed since the last frame. This is what lets
        /// the worker job return immediately while the install keeps running in the Editor.
        /// </summary>
        private static void Tick()
        {
            if (LiveRequests.Count == 0)
            {
                EditorApplication.update -= Tick;
                s_ticking = false;
                return;
            }

            List<CucliPackageJob> jobs = LoadJobs();
            bool dirty = false;

            foreach (string jobId in LiveRequests.Keys.ToArray())
            {
                Request request = LiveRequests[jobId];
                bool completed;
                try
                {
                    completed = request.IsCompleted;
                }
                catch (Exception)
                {
                    completed = true;
                }

                if (!completed) continue;

                CucliPackageJob job = FindJob(jobs, jobId);
                if (job != null) CompleteJob(job, request);
                LiveRequests.Remove(jobId);
                dirty = true;
            }

            if (RecoverStaleJobs(jobs)) dirty = true;            if (dirty) SaveJobs(jobs);

            if (LiveRequests.Count == 0)
            {
                EditorApplication.update -= Tick;
                s_ticking = false;
            }
        }

        private static void CompleteJob(CucliPackageJob job, Request request)
        {
            job.FinishedUnixMs = NowMs();
            job.LastUpdateUnixMs = job.FinishedUnixMs;

            bool success;
            try
            {
                success = request.Status == StatusCode.Success;
            }
            catch (Exception)
            {
                success = false;
            }

            if (success)
            {
                job.Status = "succeeded";
                job.Error = null;
                ReadResultInfo(job, request);
            }
            else
            {
                job.Status = "failed";
                string message = null;
                try
                {
                    if (request.Error != null)
                    {
                        message = request.Error.errorCode + ": " + request.Error.message;
                    }
                }
                catch (Exception)
                {
                    message = null;
                }

                job.Error = string.IsNullOrWhiteSpace(message)
                    ? "Package Manager 返回失败状态。"
                    : message;
            }

            CucliOptional.ClearCache();
        }

        private static void ReadResultInfo(CucliPackageJob job, Request request)
        {
            try
            {
                var add = request as AddRequest;
                if (add != null && add.Result != null)
                {
                    job.ResultName = add.Result.name;
                    job.ResultVersion = add.Result.version;
                    return;
                }

                var embed = request as EmbedRequest;
                if (embed != null && embed.Result != null)
                {
                    job.ResultName = embed.Result.name;
                    job.ResultVersion = embed.Result.version;
                    return;
                }

                var list = request as ListRequest;
                if (list != null)
                {
                    job.ResultName = job.Operation;
                }
            }
            catch (Exception)
            {
                // Diagnostics only; the job still counts as succeeded.
            }
        }

        /// <summary>
        /// A live request object cannot survive a domain reload, so a job that is still "running"
        /// with no live request is either finished (verify against the package registry) or lost.
        /// </summary>
        private static bool RecoverStaleJobs(List<CucliPackageJob> jobs)
        {
            bool changed = false;
            long now = NowMs();
            long staleMs = StaleJobMinutes * 60L * 1000L;
            PackageInfo[] registered = null;

            foreach (CucliPackageJob job in jobs)
            {
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) continue;
                if (LiveRequests.ContainsKey(job.JobId)) continue;

                if (registered == null)
                {
                    try
                    {
                        registered = PackageInfo.GetAllRegisteredPackages();
                    }
                    catch (Exception)
                    {
                        registered = new PackageInfo[0];
                    }
                }

                bool present = false;
                if (registered != null)
                {
                    foreach (PackageInfo package in registered)
                    {
                        if (package == null) continue;
                        if (!string.Equals(package.name, job.Package, StringComparison.OrdinalIgnoreCase)) continue;
                        present = true;
                        if (string.IsNullOrWhiteSpace(job.ResultVersion)) job.ResultVersion = package.version;
                        break;
                    }
                }

                if (string.Equals(job.Operation, "add", StringComparison.OrdinalIgnoreCase) && present)
                {
                    job.Status = "succeeded";
                    job.FinishedUnixMs = now;
                    job.LastUpdateUnixMs = now;
                    changed = true;
                    continue;
                }

                if (string.Equals(job.Operation, "remove", StringComparison.OrdinalIgnoreCase) && !present)
                {
                    job.Status = "succeeded";
                    job.FinishedUnixMs = now;
                    job.LastUpdateUnixMs = now;
                    changed = true;
                    continue;
                }

                if (now - job.LastUpdateUnixMs <= staleMs) continue;

                job.Status = "failed";
                job.FinishedUnixMs = now;
                job.LastUpdateUnixMs = now;
                job.Error = "包作业在 domain reload 或 Editor 重启后丢失了请求句柄，无法确认结果。";
                changed = true;
            }

            return changed;
        }

        private static List<CucliPackageJob> LoadJobs()
        {
            string json = SessionState.GetString(SessionKey, string.Empty);
            List<CucliPackageJob> jobs = null;

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    jobs = CucliJson.Deserialize<List<CucliPackageJob>>(json);
                }
                catch (JsonException)
                {
                    jobs = null;
                }
            }

            if ((jobs == null || jobs.Count == 0) && File.Exists(JobsPath))
            {
                try
                {
                    jobs = CucliJson.Deserialize<List<CucliPackageJob>>(
                        File.ReadAllText(JobsPath, CucliJson.Utf8NoBom));
                }
                catch (JsonException)
                {
                    jobs = null;
                }
                catch (IOException)
                {
                    jobs = null;
                }
                catch (UnauthorizedAccessException)
                {
                    jobs = null;
                }
            }

            return jobs ?? new List<CucliPackageJob>();
        }

        private static void SaveJobs(List<CucliPackageJob> jobs)
        {
            if (jobs == null) return;

            if (jobs.Count > MaxRetainedJobs)
            {
                jobs.Sort((left, right) => right.LastUpdateUnixMs.CompareTo(left.LastUpdateUnixMs));
                jobs.RemoveRange(MaxRetainedJobs, jobs.Count - MaxRetainedJobs);
            }

            try
            {
                SessionState.SetString(SessionKey, CucliJson.SerializeCompact(jobs));
            }
            catch (Exception)
            {
                // SessionState is a cache; the file below is the durable copy.
            }

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

        private static CucliPackageJob FindJob(List<CucliPackageJob> jobs, string jobId)
        {
            if (jobs == null || string.IsNullOrWhiteSpace(jobId)) return null;
            foreach (CucliPackageJob job in jobs)
            {
                if (job != null && string.Equals(job.JobId, jobId, StringComparison.OrdinalIgnoreCase)) return job;
            }

            return null;
        }

        private static int CountRunning(List<CucliPackageJob> jobs)
        {
            int count = 0;
            foreach (CucliPackageJob job in jobs)
            {
                if (job != null && string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) count++;
            }

            return count;
        }

        private static JObject Describe(CucliPackageJob job)
        {
            var payload = new JObject
            {
                ["job_id"] = job.JobId,
                ["jobId"] = job.JobId,
                ["status"] = job.Status,
                ["operation"] = job.Operation,
                ["package_"] = job.Package,
                ["package"] = job.Package,
                ["started_unix_ms"] = job.StartedUnixMs,
                ["last_update_unix_ms"] = job.LastUpdateUnixMs,
                ["error"] = job.Error,
                ["result_version"] = job.ResultVersion,
                ["result_name"] = job.ResultName
            };

            if (job.FinishedUnixMs > 0) payload["finished_unix_ms"] = job.FinishedUnixMs;
            return payload;
        }

        // ---------------------------------------------------------------- helpers

        private static string ReadPackageArgument(CucliCommandContext context)
        {
            string value = context.Input.GetString("package")
                ?? context.Input.GetString("packageId")
                ?? context.Input.GetString("package_id")
                ?? context.Input.GetString("package_");
            return value;
        }

        private static bool TryNormalizeIdentifier(string value, out string normalized, out string warning)
        {
            normalized = null;
            warning = null;

            string trimmed = value == null ? string.Empty : value.Trim();
            if (trimmed.Length == 0) return false;

            if (IsRemoteSource(trimmed))
            {
                normalized = trimmed;
                warning = "该来源不是 registry 包（git/file 来源），版本不会写入 manifest 的 dependencies。";
                return true;
            }

            string name = trimmed;
            string version = null;
            int separator = trimmed.IndexOf('@');
            if (separator > 0)
            {
                name = trimmed.Substring(0, separator);
                version = trimmed.Substring(separator + 1);
            }

            if (!Regex.IsMatch(name, IdentifierPattern, RegexOptions.IgnoreCase)) return false;
            normalized = string.IsNullOrWhiteSpace(version)
                ? name.ToLowerInvariant()
                : name.ToLowerInvariant() + "@" + version;
            return true;
        }

        private static bool IsRemoteSource(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string lower = value.Trim().ToLowerInvariant();
            return lower.StartsWith("https://", StringComparison.Ordinal)
                || lower.StartsWith("http://", StringComparison.Ordinal)
                || lower.StartsWith("git://", StringComparison.Ordinal)
                || lower.StartsWith("ssh://", StringComparison.Ordinal)
                || lower.StartsWith("git@", StringComparison.Ordinal)
                || lower.StartsWith("file:", StringComparison.Ordinal)
                || lower.EndsWith(".git", StringComparison.Ordinal);
        }

        private static string StripVersion(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return identifier;
            string trimmed = identifier.Trim();
            int separator = trimmed.IndexOf('@');
            return separator > 0 ? trimmed.Substring(0, separator) : trimmed;
        }

        private static JArray FindDependents(string packageName)
        {
            var dependents = new JArray();
            try
            {
                foreach (PackageInfo package in PackageInfo.GetAllRegisteredPackages())
                {
                    if (package == null) continue;
                    if (string.Equals(package.name, packageName, StringComparison.OrdinalIgnoreCase)) continue;
                    DependencyInfo[] dependencies = package.dependencies;
                    if (dependencies == null) continue;
                    foreach (DependencyInfo dependency in dependencies)
                    {
                        // DependencyInfo is a struct: it cannot be null, only empty.
                        if (string.IsNullOrEmpty(dependency.name)) continue;
                        if (!string.Equals(dependency.name, packageName, StringComparison.OrdinalIgnoreCase)) continue;
                        dependents.Add(package.name);
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // A dependency scan failure must not block an explicit removal.
            }

            return dependents;
        }

        private static List<PackageInfo> CachedPackages()
        {
            var packages = new List<PackageInfo>();
            try
            {
                PackageInfo[] registered = PackageInfo.GetAllRegisteredPackages();
                if (registered != null)
                {
                    foreach (PackageInfo package in registered)
                    {
                        if (package != null) packages.Add(package);
                    }
                }
            }
            catch (Exception)
            {
                // Returns whatever was collected.
            }

            return packages;
        }

        private static CucliCommandResult BuildPackageList(
            CucliCommandContext context,
            List<PackageInfo> packages,
            bool cached)
        {
            var items = new List<JObject>();
            foreach (PackageInfo package in packages)
            {
                if (package == null) continue;
                items.Add(new JObject
                {
                    ["name"] = package.name,
                    ["version"] = package.version,
                    ["displayName"] = package.displayName,
                    ["source"] = package.source.ToString()
                });
            }

            items.Sort((left, right) => string.CompareOrdinal(
                left["name"] == null ? string.Empty : left["name"].ToString(),
                right["name"] == null ? string.Empty : right["name"].ToString()));

            CucliPage<JObject> page = CucliPaging.Slice(items, context.Input, 100);
            return CucliCommandResult.Ok(
                CucliPaging.Envelope(page, new JArray(page.Items.Cast<object>().ToArray()), new JObject
                {
                    ["cached"] = cached
                }),
                "共 " + page.Total + " 个包（返回 " + page.Items.Count + " 个" + (cached ? "，来自本地缓存" : "") + "）。");
        }

        private static string Truncate(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= limit ? value : value.Substring(0, limit) + "...";
        }

        private static string NewJobId(string operation)
        {
            return operation + "-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static string ManifestPath =>
            Path.Combine(CucliPaths.ProjectRoot, "Packages", "manifest.json");

        private static string JobsPath =>
            Path.Combine(CucliPaths.Root, "package", "jobs.json");

        private static string BackupFolder =>
            Path.Combine(CucliPaths.Root, "backups");

        private static JObject LoadManifest(out string error)
        {
            error = null;
            string path = ManifestPath;
            if (!File.Exists(path))
            {
                error = "找不到 " + path + "。";
                return null;
            }

            string text;
            try
            {
                text = File.ReadAllText(path, CucliJson.Utf8NoBom);
            }
            catch (IOException exception)
            {
                error = "读取 manifest.json 失败：" + exception.Message;
                return null;
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "读取 manifest.json 失败：" + exception.Message;
                return null;
            }

            try
            {
                JObject manifest = JObject.Parse(text);
                return manifest;
            }
            catch (JsonException exception)
            {
                error = "Packages/manifest.json 不是合法 JSON（" + exception.Message
                    + "），已放弃写入以免破坏工程配置。";
                return null;
            }
        }

        /// <summary>
        /// Backs up the original manifest under <c>Library/CustomUnityCli/backups</c> before the new
        /// content is written atomically, so a bad edit is always recoverable by hand.
        /// </summary>
        private static bool TryWriteManifest(JObject manifest, CucliCommandContext context, out string error)
        {
            error = null;
            string path = ManifestPath;

            try
            {
                string original = File.ReadAllText(path, CucliJson.Utf8NoBom);
                Directory.CreateDirectory(BackupFolder);
                string backup = Path.Combine(
                    BackupFolder,
                    "manifest." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(backup, original, CucliJson.Utf8NoBom);
                context.AddArtifact(backup);
            }
            catch (IOException exception)
            {
                error = "备份 manifest.json 失败，已取消写入：" + exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "备份 manifest.json 失败，已取消写入：" + exception.Message;
                return false;
            }

            try
            {
                CucliPaths.WriteAtomic(path, manifest.ToString(Formatting.Indented));
            }
            catch (IOException exception)
            {
                error = "写入 manifest.json 失败：" + exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "写入 manifest.json 失败：" + exception.Message;
                return false;
            }

            return true;
        }
    }

    /// <summary>One Package Manager job, persisted in SessionState plus a JSON mirror on disk.</summary>
    internal sealed class CucliPackageJob
    {
        public string JobId { get; set; }
        public string Operation { get; set; }
        public string Package { get; set; }
        public string Status { get; set; }
        public long StartedUnixMs { get; set; }
        public long FinishedUnixMs { get; set; }
        public long LastUpdateUnixMs { get; set; }
        public string Error { get; set; }
        public string ResultVersion { get; set; }
        public string ResultName { get; set; }
    }
}
