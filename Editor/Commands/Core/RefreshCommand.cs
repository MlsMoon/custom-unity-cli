using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli refresh ...</c> - asset database refresh, script compilation, reimport and
    /// "wait until the Editor is idle".
    ///
    /// This is the reference example for a <em>reload-capable deferred</em> command group: every
    /// action can trigger a domain reload, so the job is parked in <c>jobs/waiting</c> and its real
    /// result is stored in a sidecar before the reload happens. Read this together with
    /// <see cref="CucliJobProcessor"/> when adding anything that recompiles.
    /// </summary>
    internal sealed class RefreshCommand : CucliCommand, ICucliDeferredCommand
    {
        public override string Name => "refresh";

        public override string Summary =>
            "Refresh assets, trigger or await script compilation, reimport specific paths, and wait for the Editor to become idle.";

        public override CucliActionInfo[] Actions => Declare(
            "assets", "Rescan the asset database (AssetDatabase.Refresh).",
            "compile", "Request script compilation and, by default, wait until it finishes.",
            "reimport", "Force-reimport specific asset paths.",
            "wait", "Block until the Editor is idle: not compiling and not importing.",
            "status", "Report compile/import state and the first compilation error, if any.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Edit;

        public override bool MutatesProject => true;

        /// <summary>Compilation and reimport replace the loaded assemblies, so the job must survive a reload.</summary>
        public override bool MayReloadDomain => true;

        public override string[] Effects => new[]
        {
            "project.assets.write", "project.recompile", "editor.state.write"
        };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""assets"", ""compile"", ""reimport"", ""wait"", ""status""],
      ""default"": ""assets""
    },
    ""path"": { ""type"": ""string"", ""description"": ""action=reimport: one asset path."" },
    ""paths"": {
      ""type"": ""array"", ""items"": { ""type"": ""string"" },
      ""description"": ""action=reimport: several asset paths.""
    },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=reimport: force update."" },
    ""wait"": { ""type"": ""boolean"", ""default"": true, ""description"": ""action=compile: wait for compilation to finish."" },
    ""requireCompileSuccess"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=wait: fail if the last compilation had errors."" },
    ""timeoutSeconds"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1800, ""default"": 300 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            // Only 'status' and 'assets' can answer without waiting; everything else needs frames.
            string action = ResolveAction(context, "assets");
            switch (action)
            {
                case "status": return Status();
                case "assets": return RefreshAssets();
                case "reimport": return Reimport(context);
                default:
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "Action '" + action + "' must be dispatched through the deferred path.");
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string action = ResolveAction(context, "assets");
            switch (action)
            {
                case "status":
                case "assets":
                case "reimport":
                    complete(Execute(context));
                    return;
                case "compile":
                    CompileDeferred(context, complete);
                    return;
                case "wait":
                    WaitDeferred(context, complete);
                    return;
                default:
                    complete(CucliCommandResult.Unsupported(action, ActionNames()));
                    return;
            }
        }

        private static CucliCommandResult Status()
        {
            return CucliCommandResult.Ok(new
            {
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                hasCompilationErrors = CucliCompilationStatus.HasErrors,
                firstCompilationError = CucliCompilationStatus.FirstError,
                isPlaying = EditorApplication.isPlaying
            });
        }

        private static CucliCommandResult RefreshAssets()
        {
            AssetDatabase.Refresh();
            return CucliCommandResult.Ok(
                new { refreshed = true, isUpdating = EditorApplication.isUpdating },
                "The asset database refresh was requested.");
        }

        private static CucliCommandResult Reimport(CucliCommandContext context)
        {
            var requested = new List<string>();
            string single = context.Input.GetString("path");
            if (!string.IsNullOrWhiteSpace(single)) requested.Add(single);
            requested.AddRange(context.Input.GetStringList("paths"));

            if (requested.Count == 0)
            {
                return CucliCommandResult.Missing("path", "Provide 'path' or 'paths' to reimport.");
            }

            bool force = context.Input.GetBool("force", false);
            var imported = new JArray();
            var missing = new JArray();

            foreach (string candidate in requested)
            {
                string normalized = CucliAssets.NormalizeAssetPath(candidate);
                if (normalized == null || AssetDatabase.LoadMainAssetAtPath(normalized) == null)
                {
                    missing.Add(candidate);
                    continue;
                }

                AssetDatabase.ImportAsset(
                    normalized,
                    force ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
                imported.Add(normalized);
            }

            if (imported.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "None of the requested paths exist in the project.",
                    new { missing });
            }

            if (missing.Count > 0)
            {
                context.Warn(missing.Count + " path(s) did not exist and were skipped.");
            }

            return CucliCommandResult.Ok(new
            {
                reimported = imported,
                reimportedCount = imported.Count,
                missing
            }, "Reimported " + imported.Count + " asset(s).");
        }

        private static void CompileDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            bool wait = context.Input.GetBool("wait", true);

            if (!EditorApplication.isCompiling)
            {
                AssetDatabase.Refresh();
                CompilationPipeline.RequestScriptCompilation();
            }

            if (!wait)
            {
                complete(CucliCommandResult.Ok(
                    new { requested = true, isCompiling = EditorApplication.isCompiling },
                    "Compilation was requested; poll 'cucli refresh status' until isCompiling is false."));
                return;
            }

            WaitForIdle(
                context,
                complete,
                "Compilation finished with no errors.",
                "Script compilation finished with errors: ");
        }

        private static void WaitDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            bool requireSuccess = context.Input.GetBool("requireCompileSuccess", false);
            if (!requireSuccess)
            {
                WaitForIdle(context, complete, "The Editor is idle.", null);
                return;
            }

            WaitForIdle(context, complete, "The Editor is idle and compilation succeeded.", "Compilation failed: ");
        }

        /// <summary>
        /// Polls <see cref="EditorApplication.update"/> until the Editor stops compiling and
        /// importing. Timing is measured with <c>timeSinceStartup</c> rather than wall clock because
        /// the Editor's own clock is what actually advances this loop - a domain reload or a modal
        /// dialog can otherwise look like a timeout.
        /// </summary>
        private static void WaitForIdle(
            CucliCommandContext context,
            Action<CucliCommandResult> complete,
            string successMessage,
            string errorPrefix)
        {
            int timeoutSeconds = Mathf.Clamp(context.Input.GetInt("timeoutSeconds", 300), 1, 1800);
            double deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
            bool done = false;

            // A compile that has not started yet would make us return instantly and lie, so require
            // at least one observed busy tick before accepting "idle".
            bool sawBusy = EditorApplication.isCompiling || EditorApplication.isUpdating;

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (done) return;

                if (context.Cancellation.IsCancellationRequested)
                {
                    done = true;
                    EditorApplication.update -= tick;
                    complete(CucliCommandResult.Fail(
                        CucliErrors.Cancelled, "Waiting for the Editor was cancelled."));
                    return;
                }

                bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating;
                if (busy) sawBusy = true;

                if (!busy && sawBusy)
                {
                    done = true;
                    EditorApplication.update -= tick;
                    bool hasErrors = CucliCompilationStatus.HasErrors;
                    if (hasErrors && errorPrefix != null)
                    {
                        complete(CucliCommandResult.Fail(
                            CucliErrors.CompilationFailed,
                            errorPrefix + CucliCompilationStatus.FirstError,
                            new { hasCompilationErrors = true, firstError = CucliCompilationStatus.FirstError }));
                        return;
                    }

                    complete(CucliCommandResult.Ok(new
                    {
                        editorIdle = true,
                        hasCompilationErrors = hasErrors,
                        elapsedSeconds = Math.Round(EditorApplication.timeSinceStartup - (deadline - timeoutSeconds), 2)
                    }, successMessage));
                    return;
                }

                if (EditorApplication.timeSinceStartup < deadline) return;

                done = true;
                EditorApplication.update -= tick;
                complete(CucliCommandResult.Fail(
                    CucliErrors.Timeout,
                    "Timed out after " + timeoutSeconds + " seconds waiting for the Editor to become idle. "
                    + "Compilation may need 'force' or the Editor may be waiting on a modal dialog.",
                    new { isCompiling = EditorApplication.isCompiling, isUpdating = EditorApplication.isUpdating }));
            };

            EditorApplication.update += tick;
        }
    }
}
