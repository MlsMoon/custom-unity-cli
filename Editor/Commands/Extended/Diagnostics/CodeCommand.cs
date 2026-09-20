using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli code ...</c> - run a C# snippet, batch several operations in one round trip, or fire a
    /// menu item. Equivalent to MCP's <c>execute_code</c> / <c>batch_execute</c> /
    /// <c>execute_menu_item</c>.
    ///
    /// <b>Security notice.</b> The snippet compiler is not a sandbox. <c>safetyChecks</c> is a short
    /// substring blacklist, exactly as in MCP's implementation: it is a guard rail against obvious
    /// accidents, not a permission system, and it can be evaded by anyone who wants to. The CLI
    /// repeats this in the result payload so a caller cannot mistake it for containment.
    /// </summary>
    internal sealed class CodeCommand : CucliCommand
    {
        private const int HistoryLimit = 100;
        private const int MaxBatchOperations = 100;
        private const int DefaultBatchOperations = 25;
        private const int MaxSourceBytes = 200_000;

        /// <summary>
        /// The group name, as a constant so the static helpers below can use it. They used to write
        /// <c>Name</c>, which is an instance property and therefore a CS0120 in a static context.
        /// </summary>
        private const string GroupName = "code";

        /// <summary>Substrings that almost always mean an accident rather than an intent.</summary>
        private static readonly string[] Blacklist =
        {
            "File.Delete", "Directory.Delete", "AssetDatabase.DeleteAsset", "AssetDatabase.DeleteAssets",
            "Process.Start", "while(true)", "while (true)", "for(;;)", "for (;;)",
            "Environment.Exit", "Application.Quit", "EditorApplication.Exit",
            "System.IO.File.Delete", "AssetDatabase.MoveAssetToTrash"
        };

        public override string Name => GroupName;

        public override CucliSurface Surface => CucliSurface.Extended;

        public override string Summary =>
            "Execute a C# snippet, batch several command operations in one round trip, or run a menu item.";

        public override CucliActionInfo[] Actions => Declare(
            "run", "Compile and invoke a C# method body, returning its value as JSON.",
            "batch", "Run several registered command operations in one round trip.",
            "menu", "Execute a menu item by its exact path.",
            "history", "List recently executed snippets.",
            "clear-history", "Delete the snippet history.",
            "replay", "Re-run a snippet from history by index.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[] { "editor.code.execute", "project.assets.write", "editor.state.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""run"", ""batch"", ""menu"", ""history"", ""clear-history"", ""replay""],
      ""default"": ""run""
    },
    ""code"": {
      ""type"": ""string"",
      ""description"": ""action=run: the BODY of 'public static object Execute()'. Return a value to receive it as JSON.""
    },
    ""file"": { ""type"": ""string"", ""description"": ""action=run: read the snippet from a file instead of 'code'."" },
    ""safetyChecks"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Substring blacklist only; NOT a sandbox."" },
    ""compiler"": { ""type"": ""string"", ""enum"": [""codedom""], ""default"": ""codedom"" },
    ""operations"": {
      ""type"": ""array"",
      ""description"": ""action=batch: [{ command, action, args }] or MCP-style [{ tool, params }]."",
      ""items"": { ""type"": ""object"" }
    },
    ""failFast"": { ""type"": ""boolean"", ""default"": false },
    ""menuPath"": { ""type"": ""string"" },
    ""index"": { ""type"": ""integer"", ""description"": ""action=replay: history index."" },
    ""limit"": { ""type"": ""integer"", ""default"": 20 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "run"))
            {
                case "run": return Run(context);
                case "batch": return Batch(context);
                case "menu": return Menu(context);
                case "history": return History(context);
                case "clear-history": return ClearHistory(context);
                case "replay": return Replay(context);
                default: return CucliCommandResult.Unsupported(ResolveAction(context, "run"), ActionNames());
            }
        }

        // ------------------------------------------------------------------ run

        private static CucliCommandResult Run(CucliCommandContext context)
        {
            string source = context.Input.GetString("code");
            string file = context.Input.GetString("file");
            if (string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(file))
            {
                string absolute = Path.IsPathRooted(file)
                    ? file
                    : Path.Combine(CucliPaths.ProjectRoot, file);
                if (!File.Exists(absolute))
                {
                    return CucliCommandResult.Fail(CucliErrors.AssetNotFound, "No snippet file at " + absolute + ".");
                }

                source = File.ReadAllText(absolute, CucliJson.Utf8NoBom);
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                return CucliCommandResult.Missing(
                    "code",
                    "Pass --code \"return 1 + 1;\" or --file snippet.cs. The text must be the BODY of "
                    + "'public static object Execute()'.");
            }

            if (source.Length > MaxSourceBytes)
            {
                return CucliCommandResult.Invalid("code", "the snippet exceeds " + MaxSourceBytes + " characters.");
            }

            bool safetyChecks = context.Input.GetBool("safetyChecks", true);
            if (safetyChecks)
            {
                string violation = FindViolation(source);
                if (violation != null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter,
                        "The snippet contains '" + violation + "', which the safety blacklist rejects. "
                        + "This blacklist is a guard rail, not a sandbox; if the operation is genuinely "
                        + "intended, re-run with safetyChecks: false.",
                        new { matched = violation, safetyChecks = false });
                }
            }

            context.ThrowIfCancelled();

            object result;
            string compileError;
            JArray compileDiagnostics;
            int compileDiagnosticCount;
            if (!TryCompileAndInvoke(
                    source, out result, out compileError, out compileDiagnostics, out compileDiagnosticCount))
            {
                RecordHistory(context, source, false, compileError);
                return CucliCommandResult.Fail(
                    CucliErrors.CompileError, compileError,
                    new
                    {
                        // Structured on purpose: a caller should be able to point at one error rather
                        // than parse prose, and the raw compiler output is never dumped.
                        errors = compileDiagnostics,
                        errorCount = compileDiagnosticCount,
                        errorsTruncated = compileDiagnostics != null && compileDiagnosticCount > compileDiagnostics.Count,
                        safetyChecks,
                        safetyNotice = SafetyNotice
                    });
            }

            RecordHistory(context, source, true, null);

            return CucliCommandResult.Ok(new
            {
                returned = result != null,
                resultType = result == null ? null : result.GetType().FullName,
                value = CucliDescribe.Value(result, CucliDescribe.OptionsFrom(context.Input), 0),
                safetyChecks,
                safetyNotice = SafetyNotice
            }, result == null ? "片段已执行，没有返回值。" : "片段已执行并返回结果。");
        }

        internal const string SafetyNotice =
            "safetyChecks is a substring blacklist, not a sandbox. It cannot prevent a snippet from "
            + "doing anything the Editor process can do.";

        private static string FindViolation(string source)
        {
            foreach (string candidate in Blacklist)
            {
                if (source.IndexOf(candidate, StringComparison.Ordinal) >= 0) return candidate;
            }

            return null;
        }

        private static bool TryCompileAndInvoke(
            string body,
            out object result,
            out string error,
            out JArray compileDiagnostics,
            out int compileDiagnosticCount)
        {
            result = null;
            error = null;
            compileDiagnostics = null;
            compileDiagnosticCount = 0;

            // No CodeDom-shaped type may be NAMED here: naming one would need a compile-time reference
            // this assembly does not have, and a missing reference is a build error for the whole
            // package rather than a graceful degradation for one command.
            //
            // The precise constraint, verified against a Unity 2022.3 install:
            //   * System.CodeDom.dll IS present in <Editor>/Data/Managed, so CompilerParameters and
            //     friends are usable at runtime - but they are still reached by reflection here so the
            //     command cannot fail to compile on a differently laid out install.
            //   * Microsoft.CSharp.dll (which historically carries CSharpCodeProvider) is NOT in
            //     Data/Managed, so the provider must also be resolved by name at runtime.
            // If neither Roslyn nor a provider can be found, this returns CODE_COMPILE_ERROR naming
            // what is missing rather than failing silently. See CreateCompilerParameters below.
            object parameters = CreateCompilerParameters(out error);
            if (parameters == null) return false;

            // Reference every assembly already loaded in this Editor. That is what makes a snippet
            // able to touch project types, not just UnityEngine.
            IList referenced = GetReferencedAssemblies(parameters);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic) continue;
                    string location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    if (referenced != null && !referenced.Contains(location)) referenced.Add(location);
                }
                catch (NotSupportedException)
                {
                }
            }

            string source = "using System;\n"
                + "using System.Collections.Generic;\n"
                + "using System.Linq;\n"
                + "using System.Reflection;\n"
                + "using UnityEngine;\n"
                + "using UnityEditor;\n"
                + "namespace CustomUnityCli.Snippets\n"
                + "{\n"
                + "    public static class Snippet\n"
                + "    {\n"
                + "        public static object Execute()\n"
                + "        {\n"
                + body + "\n"
                + "        }\n"
                + "    }\n"
                + "}\n";

            object results;
            try
            {
                // CSharpCodeProvider lives in Microsoft.CSharp.dll, which is NOT part of the reference
                // set an Editor assembly gets. Referencing it directly fails to compile; loading it by
                // name keeps the CodeDom fallback available on editors that ship it while leaving this
                // package free of any dependency the host project does not already have.
                // Nothing CodeDom-shaped may appear in a signature or a local type either - not even
                // CompilerParameters or CompilerResults, which System.CodeDom would have to be
                // referenced for. The provider and its arguments are driven entirely by reflection.
                object provider = CreateProvider(out error);
                if (provider == null) return false;

                try
                {
                    MethodInfo compile = provider.GetType().GetMethod(
                        "CompileAssemblyFromSource",
                        new[] { parameters.GetType(), typeof(string) });

                    if (compile == null)
                    {
                        error = "The CodeDom provider does not expose CompileAssemblyFromSource; "
                            + "this Editor's CodeDom build is not usable for snippets.";
                        return false;
                    }

                    results = compile.Invoke(provider, new object[] { parameters, source });
                }
                finally
                {
                    var disposable = provider as IDisposable;
                    if (disposable != null) disposable.Dispose();
                }
            }
            catch (Exception exception)
            {
                error = "The snippet compiler is unavailable: " + exception.GetBaseException().Message
                    + ". In Unity 2022.3 the CodeDom provider ships with the Editor; a stripped "
                    + "installation or a script-only build may lack it.";
                return false;
            }

            int errorCount;
            JArray diagnostics = CompileDiagnostics(results, out errorCount);
            if (diagnostics != null)
            {
                compileDiagnostics = diagnostics;
                compileDiagnosticCount = errorCount;
                error = DescribeCompileDiagnostics(diagnostics, errorCount);
                return false;
            }

            try
            {
                Assembly compiled = GetPropertyValue(results, "CompiledAssembly") as Assembly;
                if (compiled == null)
                {
                    error = "The snippet compiler reported success but produced no assembly. "
                        + "This is a CodeDom quirk, not a problem with your code.";
                    return false;
                }

                Type type = compiled.GetType("CustomUnityCli.Snippets.Snippet");
                MethodInfo method = type == null ? null : type.GetMethod("Execute");
                if (method == null)
                {
                    error = "The compiled snippet had no Execute method; this is a bug in the wrapper, not in your code.";
                    return false;
                }

                result = method.Invoke(null, null);
                return true;
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                error = "The snippet threw " + inner.GetType().Name + ": " + inner.Message
                    + "\n" + (inner.StackTrace ?? string.Empty);
                return false;
            }
            catch (Exception exception)
            {
                error = "The snippet could not be invoked: " + exception.GetBaseException().Message;
                return false;
            }
        }

        // ------------------------------------------------- CodeDom, entirely by reflection

        /// <summary>
        /// Builds a <c>System.CodeDom.Compiler.CompilerParameters</c> without ever naming its type.
        ///
        /// <c>System.CodeDom</c> is not part of the reference set an Editor assembly receives, so a
        /// direct reference to any CodeDom type fails to compile the whole package. Staying
        /// reflection-only also means a stripped Editor that ships no CodeDom degrades into a clear
        /// message instead of a TypeLoadException.
        /// </summary>
        private static object CreateCompilerParameters(out string error)
        {
            error = null;

            Assembly codeDom = CucliOptional.LoadAssembly("System.CodeDom");
            Type type = codeDom == null
                ? null
                : codeDom.GetType("System.CodeDom.Compiler.CompilerParameters", false);

            if (type == null)
            {
                error = "System.CodeDom is not available in this Editor, and no Roslyn compiler was "
                    + "found either. Install the Roslyn package, or write the code to a file and use "
                    + "'cucli script' plus 'cucli refresh compile' instead of running snippets.";
                return null;
            }

            object parameters;
            try
            {
                parameters = Activator.CreateInstance(type);
            }
            catch (Exception exception)
            {
                error = "System.CodeDom.Compiler.CompilerParameters could not be created: "
                    + exception.GetBaseException().Message;
                return null;
            }

            SetPropertyValue(parameters, "GenerateInMemory", true);
            SetPropertyValue(parameters, "GenerateExecutable", false);
            SetPropertyValue(parameters, "TreatWarningsAsErrors", false);
            SetPropertyValue(parameters, "IncludeDebugInformation", false);
            return parameters;
        }

        /// <summary>
        /// The parameter object's <c>ReferencedAssemblies</c>, surfaced as <see cref="IList"/> so the
        /// caller never has to name <c>StringCollection</c>.
        /// </summary>
        private static IList GetReferencedAssemblies(object parameters)
        {
            return GetPropertyValue(parameters, "ReferencedAssemblies") as IList;
        }

        private static object GetPropertyValue(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name);
                return property == null ? null : property.GetValue(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SetPropertyValue(object target, string name, object value)
        {
            if (target == null) return;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name);
                if (property != null && property.CanWrite) property.SetValue(target, value, null);
            }
            catch (Exception)
            {
                // A missing or read-only property is not fatal; the compile call reports the rest.
            }
        }

        /// <summary>
        /// The compiler diagnostics as structured entries: id, line, column and message.
        ///
        /// Returned as data rather than prose so a caller can act on one specific error, and capped so
        /// that a single syntax mistake cannot produce a wall of cascading diagnostics. Line numbers
        /// are shifted back by the wrapper's preamble so they refer to the caller's own snippet rather
        /// than to the generated file, and columns are left alone because prepending lines does not
        /// move anything horizontally.
        ///
        /// Returns null when the compilation produced no errors, so callers can use null as "compiled".
        /// </summary>
        private static JArray CompileDiagnostics(object results, out int totalErrors)
        {
            totalErrors = 0;
            object errors = GetPropertyValue(results, "Errors");
            if (errors == null) return null;

            object flag = GetPropertyValue(errors, "HasErrors");
            if (!(flag is bool) || !(bool)flag) return null;

            const int reported = 20;
            const int preambleLines = 8;

            var array = new JArray();
            var items = errors as IEnumerable;
            if (items == null) return array;

            foreach (object item in items)
            {
                object warning = GetPropertyValue(item, "IsWarning");
                if (warning is bool && (bool)warning) continue;

                totalErrors++;
                if (array.Count >= reported) continue;

                int line = 0;
                object rawLine = GetPropertyValue(item, "Line");
                if (rawLine is int) line = (int)rawLine;
                if (line > preambleLines) line -= preambleLines;
                if (line < 1) line = 1;

                int column = 0;
                object rawColumn = GetPropertyValue(item, "Column");
                if (rawColumn is int) column = (int)rawColumn;

                object number = GetPropertyValue(item, "ErrorNumber");
                object text = GetPropertyValue(item, "ErrorText");

                array.Add(new JObject
                {
                    ["id"] = number == null || string.IsNullOrWhiteSpace(number.ToString())
                        ? "CS0000"
                        : number.ToString(),
                    ["line"] = line,
                    ["column"] = column,
                    ["message"] = text == null ? "unknown compile error" : text.ToString()
                });
            }

            return array;
        }

        /// <summary>Human-readable summary of the first few diagnostics, for the message field.</summary>
        private static string DescribeCompileDiagnostics(JArray diagnostics, int totalErrors)
        {
            var builder = new StringBuilder();
            int shown = Math.Min(5, diagnostics == null ? 0 : diagnostics.Count);
            for (int index = 0; index < shown; index++)
            {
                JToken item = diagnostics[index];
                builder.Append("line ").Append(item["line"]).Append(", column ").Append(item["column"])
                    .Append(": ").Append(item["id"]).Append(' ').Append(item["message"]).Append('\n');
            }

            if (totalErrors > shown)
            {
                builder.Append("...and ").Append(totalErrors - shown)
                    .Append(" more; the full list is in data.errors.\n");
            }

            return "The snippet did not compile:\n" + builder.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------ batch

        private static CucliCommandResult Batch(CucliCommandContext context)
        {
            JArray operations = context.Input.GetArray("operations") ?? context.Input.GetArray("ops");
            if (operations == null || operations.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "operations",
                    "Example: --operations '[{\"command\":\"object\",\"action\":\"find\",\"args\":{\"searchTerm\":\"Ball\"}}]'");
            }

            int limit = context.Input.GetInt("maxOperations", DefaultBatchOperations);
            if (limit <= 0) limit = DefaultBatchOperations;
            if (limit > MaxBatchOperations) limit = MaxBatchOperations;

            bool failFast = context.Input.GetBool("failFast", false);
            if (operations.Count > limit)
            {
                return CucliCommandResult.Invalid(
                    "operations",
                    "the batch has " + operations.Count + " operations but the limit is " + limit
                    + " (raise maxOperations up to " + MaxBatchOperations + ").");
            }

            // MCP accepts a "parallel" flag and then still runs the batch serially. We keep that
            // contract and say so out loud, rather than implying concurrency we do not provide:
            // Editor APIs are not thread-safe, so serial is the only correct behaviour here.
            if (context.Input.GetBool("parallel", false))
            {
                context.Warn("parallel was requested but batch execution is strictly serial; "
                    + "operations are applied one at a time, in order. The flag is accepted only for "
                    + "compatibility and changes nothing.");
            }

            var results = new JArray();
            int succeeded = 0;
            int failed = 0;
            var warnings = new List<string>();

            for (int index = 0; index < operations.Count; index++)
            {
                context.ThrowIfCancelled();
                var operation = operations[index] as JObject;
                if (operation == null)
                {
                    failed++;
                    results.Add(new JObject { ["index"] = index, ["success"] = false, ["message"] = "operation is not an object" });
                    continue;
                }

                var invocation = new CucliArgs(operation);
                // Accept both shapes: our own {command, action, args} and MCP's {tool, params}.
                string commandName = invocation.GetString("command") ?? invocation.GetString("tool");
                string actionName = invocation.GetString("action");
                JObject args = invocation.GetObject("args")
                    ?? invocation.GetObject("params")
                    ?? invocation.GetObject("payload")
                    ?? new JObject();

                if (string.IsNullOrWhiteSpace(commandName))
                {
                    failed++;
                    results.Add(new JObject
                    {
                        ["index"] = index,
                        ["success"] = false,
                        ["message"] = "operation needs a 'command' (or MCP-style 'tool')"
                    });
                    continue;
                }

                // MCP lower-cases and camel-cases the top-level keys of `params`, which silently
                // breaks tools that only read exact snake_case keys. We accept both spellings
                // everywhere instead of reproducing that trap.
                if (!string.IsNullOrWhiteSpace(actionName) && args["action"] == null)
                {
                    args["action"] = actionName;
                }

                ICucliCommand command;
                if (!CucliCommandRegistry.TryGet(commandName.Trim(), out command))
                {
                    failed++;
                    results.Add(new JObject
                    {
                        ["index"] = index,
                        ["command"] = commandName,
                        ["success"] = false,
                        ["errorCode"] = CucliErrors.CommandNotFound,
                        ["message"] = "no command group named '" + commandName + "'"
                    });
                    continue;
                }

                if (command.Name == GroupName)
                {
                    failed++;
                    results.Add(new JObject
                    {
                        ["index"] = index,
                        ["command"] = commandName,
                        ["success"] = false,
                        ["errorCode"] = CucliErrors.InvalidParameter,
                        ["message"] = "a batch cannot contain another batch"
                    });
                    continue;
                }

                var nested = new CucliCommandContext(
                    command.Name, context.JobId, args, context.Cancellation);

                CucliCommandResult outcome;
                try
                {
                    outcome = command.Execute(nested) ?? CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "the operation returned no result");
                }
                catch (CucliUsageException exception)
                {
                    outcome = exception.Result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    outcome = CucliCommandResult.Fail(
                        CucliErrors.CommandException, exception.GetBaseException().Message);
                }

                foreach (string warning in nested.Warnings) warnings.Add("[" + index + "] " + warning);

                if (outcome.Success) succeeded++;
                else failed++;

                results.Add(new JObject
                {
                    ["index"] = index,
                    ["command"] = command.Name,
                    ["action"] = args["action"] == null ? null : args["action"].ToString(),
                    ["success"] = outcome.Success,
                    ["errorCode"] = outcome.ErrorCode,
                    ["message"] = outcome.Message,
                    ["data"] = outcome.Data == null ? null : CucliJson.ToToken(outcome.Data)
                });

                if (!outcome.Success && failFast)
                {
                    warnings.Add("failFast stopped the batch after operation " + index + " failed.");
                    break;
                }
            }

            foreach (string warning in warnings) context.Warn(warning);

            var payload = new
            {
                total = operations.Count,
                executed = results.Count,
                succeeded,
                failed,
                failFast,
                results
            };

            if (failed > 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    failed + " of " + results.Count + " batched operation(s) failed.",
                    payload);
            }

            return CucliCommandResult.Ok(payload, "全部 " + results.Count + " 个批量操作成功。");
        }

        // ------------------------------------------------------------------ menu

        private static CucliCommandResult Menu(CucliCommandContext context)
        {
            string menuPath = context.Input.GetString("menuPath") ?? context.Input.GetString("menu_path");
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return CucliCommandResult.Missing("menuPath", "Example: --menu-path \"GameObject/Create Empty\"");
            }

            bool executed = EditorApplication.ExecuteMenuItem(menuPath.Trim());
            if (!executed)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "The menu item '" + menuPath + "' does not exist or is disabled.Paths are case "
                    + "sensitive and use '/' as the separator.");
            }

            return CucliCommandResult.Ok(new { menuPath = menuPath.Trim(), executed = true });
        }

        // ------------------------------------------------------------------ history

        private static string HistoryPath => Path.Combine(CucliPaths.Root, "code-history.ndjson");

        private static void RecordHistory(CucliCommandContext context, string source, bool success, string error)
        {
            try
            {
                var entry = new JObject
                {
                    ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                    ["sessionId"] = CucliWorkerBootstrap.CurrentSessionId,
                    ["jobId"] = context.JobId,
                    ["success"] = success,
                    ["error"] = error,
                    ["code"] = CucliDescribe.Truncate(source, 8000)
                };

                Directory.CreateDirectory(CucliPaths.Root);
                File.AppendAllText(HistoryPath, entry.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine,
                    CucliJson.Utf8NoBom);

                TrimHistory();
            }
            catch (IOException)
            {
                // History is a convenience; never fail a snippet because logging it did not work.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TrimHistory()
        {
            try
            {
                string[] lines = File.ReadAllLines(HistoryPath, CucliJson.Utf8NoBom);
                if (lines.Length <= HistoryLimit) return;
                File.WriteAllLines(
                    HistoryPath,
                    lines.Skip(lines.Length - HistoryLimit).ToArray(),
                    CucliJson.Utf8NoBom);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static CucliCommandResult History(CucliCommandContext context)
        {
            var entries = new List<JObject>();

            if (File.Exists(HistoryPath))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(HistoryPath, CucliJson.Utf8NoBom);
                }
                catch (IOException exception)
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, exception.Message);
                }

                bool includeCode = context.Input.GetBool("includeCode", false);

                // Newest first: the reason to open the history is almost always the run you just did.
                for (int index = lines.Length - 1; index >= 0; index--)
                {
                    if (string.IsNullOrWhiteSpace(lines[index])) continue;
                    try
                    {
                        JObject entry = CucliJson.ParseObject(lines[index]);
                        // The ABSOLUTE line index is kept so it can be handed straight back to
                        // `replay --index`, which addresses the file rather than the current page.
                        entry["index"] = index;
                        // The full source is not echoed by default: it can be thousands of characters.
                        if (!includeCode) entry.Remove("code");
                        entries.Add(entry);
                    }
                    catch (Newtonsoft.Json.JsonException)
                    {
                    }
                }
            }

            CucliPage<JObject> page = CucliPaging.Slice(entries, context.Input, 20);
            var items = new JArray();
            foreach (JObject entry in page.Items) items.Add(entry);

            return CucliCommandResult.Ok(CucliPaging.Envelope(page, items, new JObject
            {
                ["historyPath"] = HistoryPath,
                ["order"] = "newest first"
            }));
        }

        private static CucliCommandResult ClearHistory(CucliCommandContext context)
        {
            if (!File.Exists(HistoryPath))
            {
                return CucliCommandResult.Ok(new { removed = false }, "历史本来就是空的。");
            }

            try
            {
                File.Delete(HistoryPath);
            }
            catch (IOException exception)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, exception.Message);
            }

            return CucliCommandResult.Ok(new { removed = true });
        }

        private static CucliCommandResult Replay(CucliCommandContext context)
        {
            if (!File.Exists(HistoryPath)) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, "No history yet.");

            string[] lines;
            try
            {
                lines = File.ReadAllLines(HistoryPath, CucliJson.Utf8NoBom);
            }
            catch (IOException exception)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, exception.Message);
            }

            int index = context.Input.GetInt("index", -1);
            if (index < 0 || index >= lines.Length)
            {
                return CucliCommandResult.Invalid(
                    "index", "must be between 0 and " + (lines.Length - 1) + ".");
            }

            JObject entry;
            try
            {
                entry = CucliJson.ParseObject(lines[index]);
            }
            catch (Newtonsoft.Json.JsonException exception)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "History entry is corrupt: " + exception.Message);
            }

            string code = entry["code"] == null ? null : entry["code"].Value<string>();
            if (string.IsNullOrWhiteSpace(code))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "History entry " + index + " has no code to replay.");
            }

            // Re-enter through the normal path so safety checks and history apply consistently.
            JObject replayArgs = new JObject
            {
                ["action"] = "run",
                ["code"] = code,
                ["safetyChecks"] = context.Input.GetBool("safetyChecks", true)
            };

            return Run(new CucliCommandContext(GroupName, context.JobId, replayArgs, context.Cancellation));
        }

        /// <summary>
        /// Whether a Roslyn C# compiler is loaded in this Editor.
        ///
        /// Roslyn ships as a set of NuGet assemblies (Microsoft.CodeAnalysis, ...CSharp, Microsoft.CSharp)
        /// that are NOT part of a stock Editor install, so it is probed rather than assumed - naming a
        /// Roslyn type directly would need a compile-time reference this assembly must not carry. The
        /// probe also lets the backend decision be stated explicitly: "the CodeDom provider is missing"
        /// and "no compiler at all is present" are different problems with different fixes.
        /// </summary>
        private static bool RoslynAvailable()
        {
            return CucliOptional.HasType(
                       "Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis.CSharp.CSharpCompilation")
                   && CucliOptional.HasType(
                       "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.SyntaxTree");
        }

        /// <summary>
        /// Creates the CodeDom C# provider without a compile-time reference to Microsoft.CSharp.
        ///
        /// The assembly is present in a normal Editor installation but is not part of the reference
        /// set an Editor assembly is compiled against, so `using Microsoft.CSharp;` breaks the build
        /// for everyone. Loading it by name keeps the fallback compiler working where it exists and
        /// degrades to a clear message where it does not.
        /// </summary>
        private static object CreateProvider(out string error)
        {
            error = null;

            try
            {
                Type providerType = Type.GetType(
                    "Microsoft.CSharp.CSharpCodeProvider, Microsoft.CSharp", false);

                if (providerType == null)
                {
                    error = RoslynAvailable()
                        ? "The CodeDom C# provider (Microsoft.CSharp) is not available in this Editor. "
                          + "Roslyn is loaded, but this build has no Roslyn execution backend wired, so "
                          + "snippets cannot run here. Reach for 'cucli batch' with existing command "
                          + "groups instead."
                        : "Neither compiler backend is available: the CodeDom C# provider "
                          + "(Microsoft.CSharp) is missing, and no Roslyn compiler "
                          + "(Microsoft.CodeAnalysis.CSharp.CSharpCompilation) is loaded either. "
                          + "Install the Roslyn assemblies under Assets/Plugins/Roslyn, or use a full "
                          + "Editor installation, to run snippets.";
                    return null;
                }

                return Activator.CreateInstance(providerType);
            }
            catch (Exception exception)
            {
                error = "The CodeDom C# provider could not be created: "
                    + exception.GetBaseException().Message;
                return null;
            }
        }
    }
}
