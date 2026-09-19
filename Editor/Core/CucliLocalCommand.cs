using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// A command defined by a JSON file in the project's <c>.cucli/commands/</c> folder rather than
    /// by compiled C#.
    ///
    /// This is what makes per-project behaviour possible without per-project code, a recompile, or a
    /// single byte entering the package repository. A team can describe "our build gate" once, keep
    /// it next to the project, and every teammate gets the same command.
    ///
    /// Three kinds are supported, deliberately no more:
    ///
    /// <list type="bullet">
    /// <item><c>menu</c> - run one Editor menu item by path.</item>
    /// <item><c>alias</c> - wrap another command with baked-in defaults under a shorter name.</item>
    /// <item><c>scenario</c> - run several commands in order, grouped under one action.</item>
    /// </list>
    /// Anything more expressive belongs in a compiled command under
    /// <c>Assets/Plugins/CustomUnityCli.Local/</c>, which is also outside the repository.
    /// </summary>
    internal sealed class CucliLocalCommand : CucliCommand
    {
        /// <summary>Guards against a scenario or alias chain that delegates back into itself.</summary>
        [ThreadStatic] private static int s_delegationDepth;

        private const int MaxDelegationDepth = 8;

        private readonly JObject m_definition;
        private readonly string m_origin;
        private CucliActionInfo[] m_actions;

        internal CucliLocalCommand(JObject definition, string origin)
        {
            m_definition = definition;
            m_origin = origin;
        }

        /// <summary>Project-relative path of the JSON file that declared this command.</summary>
        internal string Origin => m_origin;

        internal bool IsEnabled
        {
            get
            {
                JToken enabled = m_definition["enabled"];
                return enabled == null || enabled.Type == JTokenType.Null || enabled.Value<bool>();
            }
        }

        public override string Name => Text("name");

        public override string Summary
        {
            get
            {
                string summary = Text("summary");
                return string.IsNullOrWhiteSpace(summary)
                    ? "Project-local command defined in " + m_origin
                    : summary;
            }
        }

        public override CucliActionInfo[] Actions
        {
            get
            {
                if (m_actions != null) return m_actions;

                var declared = m_definition["actions"] as JArray;
                if (declared != null && declared.Count > 0)
                {
                    var list = new List<CucliActionInfo>();
                    foreach (JToken item in declared)
                    {
                        var entry = item as JObject;
                        if (entry == null) continue;
                        string name = entry["name"] == null ? null : entry["name"].Value<string>();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string summary = entry["summary"] == null ? null : entry["summary"].Value<string>();
                        list.Add(new CucliActionInfo(name.Trim(), summary ?? name.Trim()));
                    }

                    if (list.Count > 0)
                    {
                        m_actions = list.ToArray();
                        return m_actions;
                    }
                }

                // Sensible defaults per kind so a minimal declaration still works.
                switch (Kind)
                {
                    case "menu":
                        m_actions = new[] { new CucliActionInfo("run", "执行声明的菜单项。") };
                        break;
                    case "scenario":
                        m_actions = new[] { new CucliActionInfo("run", "按顺序执行声明的步骤。") };
                        break;
                    default:
                        m_actions = new[] { new CucliActionInfo("run", "转发到目标命令。") };
                        break;
                }

                return m_actions;
            }
        }

        public override string ParametersSchemaJson
        {
            get
            {
                var declared = m_definition["schema"] as JObject;
                if (declared != null) return declared.ToString(Newtonsoft.Json.Formatting.None);

                // Otherwise derive a schema from the action list so callers still get a usable contract.
                var names = new JArray();
                foreach (CucliActionInfo action in Actions) names.Add(action.Name);
                var schema = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = true,
                    ["properties"] = new JObject
                    {
                        ["action"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = names,
                            ["default"] = names.Count > 0 ? names[0] : "run"
                        }
                    },
                    ["x-cucli-local"] = true,
                    ["x-cucli-origin"] = m_origin
                };
                return schema.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        public override bool RequiresEditor => Flag("requiresEditor", true);

        public override CucliMode RequiredMode
        {
            get
            {
                string mode = (Text("requiredMode") ?? "any").Trim().ToLowerInvariant();
                switch (mode)
                {
                    case "edit": return CucliMode.Edit;
                    case "play": return CucliMode.Play;
                    case "offline": return CucliMode.Offline;
                    default: return CucliMode.Any;
                }
            }
        }

        public override bool MutatesProject => Flag("mutatesProject", true);

        public override bool MayReloadDomain => Flag("mayReloadDomain", false);

        public override string OptionalPackage
        {
            get
            {
                string package = Text("optionalPackage");
                return string.IsNullOrWhiteSpace(package) ? null : package;
            }
        }

        public override string[] Effects
        {
            get
            {
                var declared = m_definition["effects"] as JArray;
                if (declared == null) return new[] { "project.local.execute" };
                return declared
                    .Where(item => item != null && item.Type != JTokenType.Null)
                    .Select(item => item.Value<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
            }
        }

        private string Kind => (Text("kind") ?? "alias").Trim().ToLowerInvariant();

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            if (!IsEnabled)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandNotFound,
                    "本地命令「" + Name + "」已被禁用（" + m_origin + "）。用 'cucli local enable' 重新启用。");
            }

            if (s_delegationDepth >= MaxDelegationDepth)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "本地命令「" + Name + "」的转发层数超过 " + MaxDelegationDepth
                    + " 层，疑似循环引用。请检查 " + m_origin + " 的 scenario / alias 定义。");
            }

            s_delegationDepth++;
            try
            {
                switch (Kind)
                {
                    case "menu": return RunMenu(context);
                    case "scenario": return RunScenario(context);
                    case "alias": return RunAlias(context);
                    default:
                        return CucliCommandResult.Fail(
                            CucliErrors.InvalidParameter,
                            "本地命令「" + Name + "」的 kind='" + Kind
                            + "' 不受支持。可用：menu、alias、scenario。（" + m_origin + "）");
                }
            }
            finally
            {
                s_delegationDepth--;
            }
        }

        private CucliCommandResult RunMenu(CucliCommandContext context)
        {
            string menuPath = Text("menuPath");
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "本地菜单命令「" + Name + "」缺少 menuPath。（" + m_origin + "）");
            }

            return Delegate("editor", "menu", new JObject { ["menuPath"] = menuPath }, context);
        }

        private CucliCommandResult RunAlias(CucliCommandContext context)
        {
            var target = m_definition["target"] as JObject;
            if (target == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "本地别名命令「" + Name + "」缺少 target 对象。（" + m_origin + "）");
            }

            string command = target["command"] == null ? null : target["command"].Value<string>();
            if (string.IsNullOrWhiteSpace(command))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "本地别名命令「" + Name + "」的 target.command 为空。（" + m_origin + "）");
            }

            string action = target["action"] == null ? null : target["action"].Value<string>();
            JObject payload = BuildPayload(target["payload"] as JObject, context);
            return Delegate(command.Trim(), action, payload, context);
        }

        private CucliCommandResult RunScenario(CucliCommandContext context)
        {
            var steps = m_definition["steps"] as JArray;
            if (steps == null || steps.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "本地场景命令「" + Name + "」没有 steps。（" + m_origin + "）");
            }

            string requested = ResolveAction(context, Actions[0].Name);
            bool continueOnError = Flag("continueOnError", false);

            var results = new JArray();
            int executed = 0;
            int failed = 0;

            foreach (JToken item in steps)
            {
                context.ThrowIfCancelled();

                var step = item as JObject;
                if (step == null) continue;

                string stepAction = step["action"] == null ? null : step["action"].Value<string>();
                if (!string.IsNullOrWhiteSpace(stepAction)
                    && !string.Equals(stepAction.Trim(), requested, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string command = step["command"] == null ? null : step["command"].Value<string>();
                if (string.IsNullOrWhiteSpace(command))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter,
                        "本地场景命令「" + Name + "」有步骤缺少 command。（" + m_origin + "）");
                }

                string action = step["actionName"] == null
                    ? (step["innerAction"] == null ? null : step["innerAction"].Value<string>())
                    : step["actionName"].Value<string>();

                JObject payload = BuildPayload(step["payload"] as JObject, context);
                CucliCommandResult result = Delegate(command.Trim(), action, payload, context);
                executed++;

                results.Add(new JObject
                {
                    ["command"] = command.Trim(),
                    ["action"] = action,
                    ["success"] = result.Success,
                    ["errorCode"] = result.ErrorCode == null ? JValue.CreateNull() : new JValue(result.ErrorCode),
                    ["message"] = result.Message == null ? JValue.CreateNull() : new JValue(result.Message),
                    ["data"] = result.Data == null ? JValue.CreateNull() : CucliJson.ToToken(result.Data)
                });

                if (result.Success) continue;
                failed++;
                if (continueOnError) continue;

                return CucliCommandResult.Fail(
                    result.ErrorCode ?? CucliErrors.CommandFailed,
                    "本地场景命令「" + Name + "」在第 " + executed + " 步失败：" + result.Message,
                    new { steps = results, executed, failed, stoppedAt = executed });
            }

            if (failed > 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "本地场景命令「" + Name + "」有 " + failed + "/" + executed + " 步失败。",
                    new { steps = results, executed, failed });
            }

            return CucliCommandResult.Ok(
                new { steps = results, executed, failed },
                "本地场景命令「" + Name + "」执行完成，共 " + executed + " 步。");
        }

        /// <summary>
        /// Merges the caller's payload over the declaration's defaults. Caller values win, so a
        /// baked-in default is a starting point rather than a cage.
        /// </summary>
        private JObject BuildPayload(JObject declared, CucliCommandContext context)
        {
            var merged = new JObject();
            if (declared != null)
            {
                foreach (JProperty property in declared.Properties()) merged[property.Name] = property.Value.DeepClone();
            }

            foreach (JProperty property in context.Args.Properties())
            {
                if (string.Equals(property.Name, "action", StringComparison.OrdinalIgnoreCase)) continue;
                merged[property.Name] = property.Value.DeepClone();
            }

            return merged;
        }

        private static CucliCommandResult Delegate(
            string commandName,
            string action,
            JObject payload,
            CucliCommandContext parent)
        {
            ICucliCommand target;
            if (!CucliCommandRegistry.TryGet(commandName, out target))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandNotFound,
                    "本地命令转发到了不存在的命令组「" + commandName + "」。用 'cucli commands list' 查看可用组。");
            }

            JObject merged = payload ?? new JObject();
            if (!string.IsNullOrWhiteSpace(action)) merged["action"] = action;

            var context = new CucliCommandContext(commandName, parent.JobId, merged, parent.Cancellation);
            try
            {
                CucliCommandResult result = target.Execute(context);
                foreach (string warning in context.Warnings) parent.Warn(warning);
                foreach (string artifact in context.Artifacts) parent.AddArtifact(artifact);
                return result ?? CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "被转发的命令「" + commandName + "」没有返回结果。");
            }
            catch (CucliUsageException exception)
            {
                return exception.Result;
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandException,
                    "被转发的命令「" + commandName + "」抛出异常：" + exception.GetBaseException().Message);
            }
        }

        private string Text(string key)
        {
            JToken token = m_definition[key];
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.Type == JTokenType.String
                ? token.Value<string>()
                : token.ToString(Newtonsoft.Json.Formatting.None);
        }

        private bool Flag(string key, bool fallback)
        {
            JToken token = m_definition[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            string text = token.Type == JTokenType.String ? token.Value<string>() : null;
            if (text == null) return fallback;
            switch (text.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
                default: return fallback;
            }
        }
    }
}
