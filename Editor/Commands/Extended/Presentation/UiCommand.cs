using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli ui ...</c> - the UI Toolkit surface: UXML / USS / UIDocument / PanelSettings and
    /// VisualElement editing.
    ///
    /// This is deliberately <em>not</em> a uGUI tool. The MCP <c>manage_ui</c> tool it mirrors has
    /// no <c>RectTransform</c>, anchor, pivot or <c>sizeDelta</c> key anywhere: layout is controlled
    /// through ① inline style keys, ② USS classes and ③ the UXML/USS text itself. Element lookup is
    /// the fixed chain <c>GameObject</c> -&gt; <c>UIDocument</c> -&gt; <c>rootVisualElement.Q(name)</c>,
    /// so the addressing key is the UXML <c>name</c> attribute, not a CSS selector.
    ///
    /// <c>render_ui</c> needs at least one Editor frame, so the group implements
    /// <see cref="ICucliDeferredCommand"/>; every other action answers inside <see cref="Execute"/>.
    /// </summary>
    internal sealed class UiCommand : CucliCommand, ICucliDeferredCommand
    {
        /// <summary>
        /// Parameter schema shared by the whole <c>ui</c> group. JSON Schema cannot express
        /// "required for action X only", so the parameter union is declared here and each action
        /// validates the subset it reads.
        /// </summary>
        internal const string UiParametersSchemaJson = @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""ping"", ""create"", ""read"", ""update"", ""attach_ui_document"", ""create_panel_settings"",
                 ""update_panel_settings"", ""get_visual_tree"", ""render_ui"", ""link_stylesheet"",
                 ""delete"", ""list"", ""detach_ui_document"", ""modify_visual_element""],
      ""default"": ""ping""
    },
    ""path"": { ""type"": ""string"", ""description"": ""UXML/USS path for create/read/update/delete/link_stylesheet, .asset path for create_panel_settings/update_panel_settings, search folder for list."" },
    ""contents"": { ""type"": ""string"", ""description"": ""action=create / update: full file text."" },
    ""encodedContents"": { ""type"": ""string"", ""description"": ""action=create / update: base64 file contents."" },
    ""contentsEncoded"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=create / update: decode encodedContents as base64."" },
    ""target"": { ""type"": ""string"", ""description"": ""GameObject name, hierarchy path or instance id hosting the UIDocument."" },
    ""sourceAsset"": { ""type"": ""string"", ""description"": ""action=attach_ui_document: UXML asset path."" },
    ""panelSettings"": { ""type"": ""string"", ""description"": ""action=attach_ui_document: PanelSettings asset path."" },
    ""sortOrder"": { ""type"": ""integer"", ""default"": 0, ""description"": ""action=attach_ui_document: UIDocument.sortingOrder."" },
    ""settings"": { ""type"": ""object"", ""description"": ""action=create_panel_settings / update_panel_settings: PanelSettings property dictionary."" },
    ""scaleMode"": { ""type"": ""string"", ""description"": ""action=create_panel_settings: PanelScaleMode name."" },
    ""referenceResolution"": { ""type"": ""object"", ""description"": ""action=create_panel_settings: { width, height }."" },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 10, ""description"": ""action=get_visual_tree: serialisation depth cap."" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64, ""description"": ""action=get_visual_tree: element budget."" },
    ""maxStringLength"": { ""type"": ""integer"", ""default"": 4096 },
    ""width"": { ""type"": ""integer"", ""default"": 1920, ""description"": ""action=render_ui: output width."" },
    ""height"": { ""type"": ""integer"", ""default"": 1080, ""description"": ""action=render_ui: output height."" },
    ""includeImage"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=render_ui: inline base64 PNG."" },
    ""maxResolution"": { ""type"": ""integer"", ""default"": 640, ""description"": ""action=render_ui: longest side of the inlined image."" },
    ""fileName"": { ""type"": ""string"", ""description"": ""action=render_ui: output file name; '.png' is appended when missing."" },
    ""outputFolder"": { ""type"": ""string"", ""description"": ""action=render_ui: output folder; defaults to Assets/Docs~/."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=render_ui / delete: overwrite or confirm a destructive change."" },
    ""stylesheet"": { ""type"": ""string"", ""description"": ""action=link_stylesheet: USS asset path."" },
    ""filterType"": { ""type"": ""string"", ""enum"": ["""", ""uxml"", ""VisualTreeAsset"", ""uss"", ""StyleSheet"", ""PanelSettings""], ""description"": ""action=list: asset type filter."" },
    ""pageSize"": { ""type"": ""integer"", ""default"": 50 },
    ""pageNumber"": { ""type"": ""integer"", ""default"": 1 },
    ""cursor"": { ""type"": ""string"" },
    ""elementName"": { ""type"": ""string"", ""description"": ""action=modify_visual_element: UXML name attribute, resolved with rootVisualElement.Q(name)."" },
    ""text"": { ""type"": ""string"", ""description"": ""action=modify_visual_element: TextElement text; errors on a non-TextElement."" },
    ""addClasses"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""removeClasses"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""toggleClasses"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""style"": { ""type"": ""object"", ""description"": ""action=modify_visual_element: inline style dictionary (camelCase or kebab-case keys)."" },
    ""enabled"": { ""type"": ""boolean"", ""description"": ""action=modify_visual_element: element.SetEnabled(value)."" },
    ""visible"": { ""type"": ""string"", ""description"": ""action=modify_visual_element: 'true'/'1' maps to display=Flex, anything else to display=None."" },
    ""tooltip"": { ""type"": ""string"", ""description"": ""action=modify_visual_element: element.tooltip."" }
  }
}";

        public override string Name => "ui";

        public override CucliSurface Surface => CucliSurface.Extended;

        public override string Summary =>
            "UI Toolkit work: UXML/USS files, UIDocument wiring, PanelSettings and VisualElement editing (no uGUI, no RectTransform).";

        public override CucliActionInfo[] Actions => Declare(
            "ping", "Round-trip probe reporting UI Toolkit availability and asset counts.",
            "create", "Create a .uxml or .uss file. Fails when the file already exists.",
            "read", "Read a .uxml or .uss file as text, optionally base64 encoded.",
            "update", "Overwrite an existing .uxml or .uss file.",
            "attach_ui_document", "Attach or update a UIDocument on a GameObject: visualTreeAsset, panelSettings, sortingOrder.",
            "create_panel_settings", "Create a PanelSettings .asset with scale mode, reference resolution and sorting order.",
            "update_panel_settings", "Write properties onto an existing PanelSettings .asset.",
            "get_visual_tree", "Dump the UIDocument visual tree, depth limited and item limited.",
            "render_ui", "Render the panel to a PNG under Assets/Docs~ and register it as an artifact.",
            "link_stylesheet", "Insert a <Style src=...> reference for a USS asset into a UXML file.",
            "delete", "Delete a .uxml or .uss file. Requires force.",
            "list", "List UXML, USS and PanelSettings assets, paged.",
            "detach_ui_document", "Clear the UIDocument source asset on a GameObject.",
            "modify_visual_element", "Set text, inline style, USS classes, enabled/visible and tooltip on a named element.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "project.assets.write", "project.assets.read", "project.assets.delete", "ui.visualtree.write"
        };

        public override string ParametersSchemaJson => UiParametersSchemaJson;

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "ping");
            switch (action)
            {
                case "ping": return Ping(context);
                case "create": return Create(context);
                case "read": return Read(context);
                case "update": return Update(context);
                case "delete": return Delete(context);
                case "list": return ListAssets(context);
                case "link_stylesheet": return LinkStylesheet(context);
                case "attach_ui_document": return AttachUiDocument(context);
                case "detach_ui_document": return DetachUiDocument(context);
                case "get_visual_tree": return GetVisualTree(context);
                case "modify_visual_element": return ModifyVisualElement(context);
                case "create_panel_settings": return PanelSettingsCommand.CreateAsset(context);
                case "update_panel_settings": return PanelSettingsCommand.UpdateAsset(context);
                case "render_ui":
                    // Rendering needs a frame; only the deferred path can honour that.
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "render_ui needs at least one Editor frame and must be dispatched through the deferred path.",
                        new { action, deferred = true });
                default:
                    return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string action = ResolveAction(context, "ping");
            if (action != "render_ui")
            {
                complete(Execute(context));
                return;
            }

            bool done = false;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (done) return;
                done = true;
                EditorApplication.update -= tick;

                if (context.Cancellation.IsCancellationRequested)
                {
                    complete(CucliCommandResult.Fail(CucliErrors.Cancelled, "The UI render was cancelled."));
                    return;
                }

                complete(Render(context));
            };

            EditorApplication.update += tick;
        }

        // ---------------------------------------------------------------- element addressing

        /// <summary>Resolves the UIDocument that owns the addressed GameObject.</summary>
        internal static UIDocument ResolveDocument(CucliCommandContext context, out CucliCommandResult failure)
        {
            failure = null;
            CucliArgs args = context.Input;

            string error;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out error);
            if (gameObject == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    error ?? "Pass 'target' (name, hierarchy path or instance id) of the UIDocument host.");
                return null;
            }

            UIDocument document = gameObject.GetComponent<UIDocument>();
            if (document == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    gameObject.name + " has no UIDocument component. Add one first with "
                    + "'cucli ui attach_ui_document --target \"" + gameObject.name + "\" --source-asset <uxml>'.",
                    new { gameObject = CucliObjectResolver.HierarchyPath(gameObject, true) });
                return null;
            }

            return document;
        }

        internal static string DescribeElement(VisualElement element)
        {
            if (element == null) return "<null>";
            string name = string.IsNullOrEmpty(element.name) ? "<unnamed>" : element.name;
            return element.GetType().Name + " '" + name + "'";
        }

        internal static PanelSettings FindFirstPanelSettings(out string path)
        {
            path = null;
            string[] guids = AssetDatabase.FindAssets("t:PanelSettings");
            for (int index = 0; index < guids.Length; index++)
            {
                string candidate = AssetDatabase.GUIDToAssetPath(guids[index]);
                PanelSettings settings = CucliAssets.Load<PanelSettings>(candidate);
                if (settings == null) continue;
                path = candidate;
                return settings;
            }

            return null;
        }

        // ---------------------------------------------------------------- ping

        private static CucliCommandResult Ping(CucliCommandContext context)
        {
            string[] uxml = CucliAssets.FindAssets("VisualTreeAsset", "Assets", null, 500);
            string[] uss = CucliAssets.FindAssets("StyleSheet", "Assets", null, 500);
            string[] panelSettings = CucliAssets.FindAssets("PanelSettings", "Assets", null, 500);
            int documents = CucliAssets.FindAll<UIDocument>().Length;

            return CucliCommandResult.Ok(new
            {
                uiToolkit = true,
                backend = "UIElements",
                ugui = false,
                rectTransformKeys = false,
                elementLookup = "UIDocument.rootVisualElement.Q(name)",
                uxmlCount = uxml.Length,
                ussCount = uss.Length,
                panelSettingsCount = panelSettings.Length,
                uidocumentCount = documents,
                unityVersion = Application.unityVersion
            }, "UI Toolkit is available; the ui group addresses elements by their UXML name attribute.");
        }

        // ---------------------------------------------------------------- file actions

        private static string RequireTextFile(CucliCommandContext context, out CucliCommandResult failure)
        {
            failure = null;
            string raw = context.Input.GetString("path") ?? context.Input.GetString("assetPath")
                ?? context.Input.GetString("asset_path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                failure = CucliCommandResult.Missing("path", "Example: --path Assets/UI/MainMenu.uxml");
                return null;
            }

            string normalized = CucliAssets.NormalizeAssetPath(raw);
            if (normalized == null)
            {
                failure = CucliCommandResult.Invalid("path", "the path escapes the project: " + raw);
                return null;
            }

            string extension = CucliAssets.Extension(normalized).ToLowerInvariant();
            if (extension != ".uxml" && extension != ".uss")
            {
                failure = CucliCommandResult.Invalid(
                    "path", "'" + extension + "' is not supported; only .uxml and .uss are UI Toolkit text assets.");
                return null;
            }

            return normalized;
        }

        private static bool TryReadContents(
            CucliCommandContext context, out string contents, out CucliCommandResult failure)
        {
            failure = null;
            contents = null;
            CucliArgs args = context.Input;

            bool encoded = args.GetBool("contentsEncoded", args.GetBool("contents_encoded", false));
            if (encoded)
            {
                string payload = args.GetString("encodedContents") ?? args.GetString("encoded_contents");
                if (string.IsNullOrWhiteSpace(payload))
                {
                    failure = CucliCommandResult.Missing(
                        "encodedContents", "It is required when contentsEncoded is true.");
                    return false;
                }

                try
                {
                    contents = Encoding.UTF8.GetString(Convert.FromBase64String(payload.Trim()));
                }
                catch (FormatException exception)
                {
                    failure = CucliCommandResult.Invalid("encodedContents", exception.Message);
                    return false;
                }

                return true;
            }

            contents = args.GetString("contents");
            if (contents == null)
            {
                failure = CucliCommandResult.Missing(
                    "contents", "Provide 'contents' or 'encodedContents' with 'contentsEncoded: true'.");
                return false;
            }

            return true;
        }

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliCommandResult failure;
            string path = RequireTextFile(context, out failure);
            if (failure != null) return failure;

            string contents;
            if (!TryReadContents(context, out contents, out failure)) return failure;

            if (CucliAssets.Exists(path) || File.Exists(CucliAssets.ToAbsolute(path)))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    path + " already exists. Use 'cucli ui update' to change it, or delete it first.",
                    new { path });
            }

            string parent = CucliAssets.ParentFolder(path);
            if (parent != null && !CucliAssets.EnsureFolder(parent))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The folder '" + parent + "' could not be created.");
            }

            var warnings = new JArray();
            if (CucliAssets.Extension(path).Equals(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                string xmlError = ValidateUxml(contents);
                if (xmlError != null)
                {
                    return CucliCommandResult.Invalid("contents", "malformed XML: " + xmlError);
                }
            }
            else if (contents.IndexOf("<ui:VisualElement", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                warnings.Add("A .uss file contains UXML markup; check that the contents are USS.");
            }

            try
            {
                File.WriteAllText(CucliAssets.ToAbsolute(path), contents, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Writing " + path + " failed: " + exception.GetBaseException().Message);
            }

            CucliAssets.Refresh();
            if (warnings.Count > 0) return CucliCommandResult.Ok(new { path, validationWarnings = warnings });
            return CucliCommandResult.Ok(
                new { path, lengthBytes = new UTF8Encoding(false).GetByteCount(contents) },
                "Created " + path + ".");
        }

        private static CucliCommandResult Update(CucliCommandContext context)
        {
            CucliCommandResult failure;
            string path = RequireTextFile(context, out failure);
            if (failure != null) return failure;

            string contents;
            if (!TryReadContents(context, out contents, out failure)) return failure;

            if (!CucliAssets.Exists(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    path + " does not exist. Use 'cucli ui create' to add it.",
                    new { path });
            }

            if (CucliAssets.Extension(path).Equals(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                string xmlError = ValidateUxml(contents);
                if (xmlError != null)
                {
                    return CucliCommandResult.Invalid("contents", "malformed XML: " + xmlError);
                }
            }

            try
            {
                File.WriteAllText(CucliAssets.ToAbsolute(path), contents, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Writing " + path + " failed: " + exception.GetBaseException().Message);
            }

            CucliAssets.Refresh();
            return CucliCommandResult.Ok(
                new { path, lengthBytes = new UTF8Encoding(false).GetByteCount(contents) },
                "Updated " + path + ".");
        }

        private static CucliCommandResult Read(CucliCommandContext context)
        {
            CucliCommandResult failure;
            string path = RequireTextFile(context, out failure);
            if (failure != null) return failure;

            if (!File.Exists(CucliAssets.ToAbsolute(path)))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, path + " does not exist in the project.", new { path });
            }

            string contents;
            try
            {
                contents = File.ReadAllText(CucliAssets.ToAbsolute(path), Encoding.UTF8);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Reading " + path + " failed: " + exception.GetBaseException().Message);
            }

            byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
            return CucliCommandResult.Ok(new
            {
                path,
                contents,
                encodedContents = Convert.ToBase64String(bytes),
                contentsEncoded = true,
                lengthBytes = bytes.Length
            });
        }

        private static CucliCommandResult Delete(CucliCommandContext context)
        {
            CucliCommandResult failure;
            string path = RequireTextFile(context, out failure);
            if (failure != null) return failure;

            if (!CucliAssets.Exists(path))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, path + " does not exist.", new { path });
            }

            if (!context.Input.GetBool("force", false))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "Deleting " + path + " is destructive. Re-run with force: true.",
                    new { path });
            }

            if (!AssetDatabase.DeleteAsset(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "AssetDatabase.DeleteAsset refused to delete " + path + ".");
            }

            CucliAssets.Refresh();
            return CucliCommandResult.Ok(new { path, deleted = true }, "Deleted " + path + ".");
        }

        private static CucliCommandResult ListAssets(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string folder = args.GetString("path") ?? "Assets";
            string normalizedFolder = CucliAssets.NormalizeAssetPath(folder);
            if (normalizedFolder == null)
            {
                return CucliCommandResult.Invalid("path", "the search folder escapes the project: " + folder);
            }

            string filter = args.GetString("filterType") ?? args.GetString("filter_type");
            var entries = new List<JObject>();

            if (string.IsNullOrWhiteSpace(filter)
                || string.Equals(filter, "uxml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(filter, "VisualTreeAsset", StringComparison.OrdinalIgnoreCase))
            {
                AddAssets(entries, "VisualTreeAsset", normalizedFolder, "uxml");
            }

            if (string.IsNullOrWhiteSpace(filter)
                || string.Equals(filter, "uss", StringComparison.OrdinalIgnoreCase)
                || string.Equals(filter, "StyleSheet", StringComparison.OrdinalIgnoreCase))
            {
                AddAssets(entries, "StyleSheet", normalizedFolder, "uss");
            }

            if (string.IsNullOrWhiteSpace(filter)
                || string.Equals(filter, "PanelSettings", StringComparison.OrdinalIgnoreCase))
            {
                AddAssets(entries, "PanelSettings", normalizedFolder, "PanelSettings");
            }

            if (entries.Count == 0 && !string.IsNullOrWhiteSpace(filter))
            {
                return CucliCommandResult.Invalid(
                    "filterType", "'" + filter + "' is not one of uxml, uss, PanelSettings.");
            }

            entries.Sort((left, right) => string.Compare(
                left.Value<string>("path"), right.Value<string>("path"), StringComparison.Ordinal));

            CucliPage<JObject> page = CucliPaging.Slice(entries, args, 50);
            var items = new JArray();
            foreach (JObject entry in page.Items) items.Add(entry);

            return CucliCommandResult.Ok(
                CucliPaging.Envelope(page, items, new JObject { ["folder"] = normalizedFolder }));
        }

        private static void AddAssets(List<JObject> target, string typeName, string folder, string kind)
        {
            foreach (string path in CucliAssets.FindAssets(typeName, folder, null, 500))
            {
                target.Add(new JObject
                {
                    ["path"] = path,
                    ["type"] = kind,
                    ["name"] = CucliAssets.FileNameWithoutExtension(path)
                });
            }
        }

        private static CucliCommandResult LinkStylesheet(CucliCommandContext context)
        {
            CucliCommandResult failure;
            string path = RequireTextFile(context, out failure);
            if (failure != null) return failure;

            if (!CucliAssets.Extension(path).Equals(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                return CucliCommandResult.Invalid(
                    "path", "link_stylesheet writes a <Style> element, so 'path' must be a .uxml file.");
            }

            if (!CucliAssets.Exists(path))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, path + " does not exist.", new { path });
            }

            string rawStylesheet = context.Input.GetString("stylesheet");
            if (string.IsNullOrWhiteSpace(rawStylesheet))
            {
                return CucliCommandResult.Missing("stylesheet", "Example: --stylesheet Assets/UI/MainMenu.uss");
            }

            string stylesheet = CucliAssets.NormalizeAssetPath(rawStylesheet);
            if (stylesheet == null || !CucliAssets.Exists(stylesheet))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "No style sheet asset is at '" + rawStylesheet + "'.");
            }

            string contents;
            try
            {
                contents = File.ReadAllText(CucliAssets.ToAbsolute(path), Encoding.UTF8);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Reading " + path + " failed: " + exception.GetBaseException().Message);
            }

            if (contents.IndexOf("<Style", StringComparison.Ordinal) >= 0
                && contents.IndexOf(stylesheet, StringComparison.Ordinal) >= 0)
            {
                return CucliCommandResult.Ok(
                    new { path, stylesheet, alreadyLinked = true },
                    stylesheet + " is already referenced by " + path + ".");
            }

            string guid = CucliAssets.AssetPathToGuid(stylesheet);
            if (string.IsNullOrEmpty(guid))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, stylesheet + " has no asset GUID yet; refresh the asset database.");
            }

            int close = contents.IndexOf('>');
            if (close < 0)
            {
                return CucliCommandResult.Invalid("path", path + " is not a well formed UXML document.");
            }

            var line = new StringBuilder();
            line.Append("\n    <Style src=\"project://database/").Append(stylesheet).Append("?v=").Append(guid)
                .Append("\" />");

            string updated = contents.Substring(0, close + 1) + line + contents.Substring(close + 1);
            string xmlError = ValidateUxml(updated);
            if (xmlError != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Adding the style reference would produce malformed XML: " + xmlError);
            }

            try
            {
                File.WriteAllText(CucliAssets.ToAbsolute(path), updated, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Writing " + path + " failed: " + exception.GetBaseException().Message);
            }

            CucliAssets.Refresh();
            return CucliCommandResult.Ok(
                new { path, stylesheet, alreadyLinked = false },
                "Linked " + stylesheet + " into " + path + ".");
        }

        // ---------------------------------------------------------------- UIDocument wiring

        private static CucliCommandResult AttachUiDocument(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string error;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out error);
            if (gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    error ?? "Pass 'target' (name, hierarchy path or instance id) of the UIDocument host.");
            }

            string rawSource = args.GetString("sourceAsset") ?? args.GetString("source_asset");
            if (string.IsNullOrWhiteSpace(rawSource))
            {
                return CucliCommandResult.Missing("sourceAsset", "Example: --source-asset Assets/UI/MainMenu.uxml");
            }

            string sourceAsset = CucliAssets.NormalizeAssetPath(rawSource);
            VisualTreeAsset treeAsset = sourceAsset == null ? null : CucliAssets.Load<VisualTreeAsset>(sourceAsset);
            if (treeAsset == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "No VisualTreeAsset (.uxml) is at '" + rawSource + "'.");
            }

            string rawPanel = args.GetString("panelSettings") ?? args.GetString("panel_settings");
            PanelSettings panelSettings;
            string panelPath;
            if (!string.IsNullOrWhiteSpace(rawPanel))
            {
                panelPath = CucliAssets.NormalizeAssetPath(rawPanel);
                panelSettings = panelPath == null ? null : CucliAssets.Load<PanelSettings>(panelPath);
                if (panelSettings == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound, "No PanelSettings asset is at '" + rawPanel + "'.");
                }
            }
            else
            {
                panelSettings = FindFirstPanelSettings(out panelPath);
                if (panelSettings == null)
                {
                    CucliCommandResult created = PanelSettingsCommand.CreateDefaultAsset(context, out panelPath);
                    if (created != null && !created.Success) return created;
                    panelSettings = CucliAssets.Load<PanelSettings>(panelPath);
                    context.Warn("No PanelSettings existed in the project; created " + panelPath + ".");
                }
            }

            UIDocument document = gameObject.GetComponent<UIDocument>();
            bool added = false;
            if (document == null)
            {
                document = gameObject.AddComponent<UIDocument>();
                added = true;
            }

            Undo.RecordObject(document, "cucli ui attach_ui_document");
            document.visualTreeAsset = treeAsset;
            if (panelSettings != null) document.panelSettings = panelSettings;
            if (args.Has("sortOrder") || args.Has("sort_order"))
            {
                document.sortingOrder = args.GetInt("sortOrder", args.GetInt("sort_order", 0));
            }

            EditorUtility.SetDirty(document);
            MarkSceneDirty(gameObject);

            return CucliCommandResult.Ok(new
            {
                gameObject = CucliObjectResolver.HierarchyPath(gameObject, true),
                instanceId = gameObject.GetInstanceID(),
                sourceAsset,
                panelSettings = panelPath,
                sortOrder = document.sortingOrder,
                uidocumentAdded = added
            }, "UIDocument on " + gameObject.name + " now renders " + sourceAsset + ".");
        }

        private static CucliCommandResult DetachUiDocument(CucliCommandContext context)
        {
            CucliCommandResult failure;
            UIDocument document = ResolveDocument(context, out failure);
            if (failure != null) return failure;

            string removed = CucliAssets.GetAssetPath(document.visualTreeAsset);
            Undo.RecordObject(document, "cucli ui detach_ui_document");
            document.visualTreeAsset = null;
            EditorUtility.SetDirty(document);
            MarkSceneDirty(document.gameObject);

            return CucliCommandResult.Ok(new
            {
                gameObject = CucliObjectResolver.HierarchyPath(document.gameObject, true),
                removedSourceAsset = string.IsNullOrEmpty(removed) ? null : removed
            }, "The UIDocument source asset was cleared; the panel settings were left in place.");
        }

        internal static void MarkSceneDirty(GameObject gameObject)
        {
            if (gameObject == null || Application.isPlaying) return;
            try
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
            catch (Exception)
            {
                // Prefab-stage objects and scenes without a path can refuse; the Undo record suffices.
            }
        }

        // ---------------------------------------------------------------- visual tree

        private static CucliCommandResult GetVisualTree(CucliCommandContext context)
        {
            CucliCommandResult failure;
            UIDocument document = ResolveDocument(context, out failure);
            if (failure != null) return failure;

            CucliArgs args = context.Input;
            int maxDepth = Mathf.Clamp(args.GetInt("maxDepth", args.GetInt("max_depth", 10)), 1, 64);
            int maxItems = Mathf.Clamp(args.GetInt("maxItems", CucliDescribe.DefaultMaxItems), 1, 5000);

            string sourceAsset = CucliAssets.GetAssetPath(document.visualTreeAsset);
            VisualElement root = document.rootVisualElement;
            if (root == null)
            {
                return CucliCommandResult.Ok(new
                {
                    gameObject = CucliObjectResolver.HierarchyPath(document.gameObject, true),
                    sourceAsset = string.IsNullOrEmpty(sourceAsset) ? null : sourceAsset,
                    tree = (object)null,
                    note = "The visual tree is not built yet. Enable the panel (the UIDocument must have panel "
                        + "settings assigned) or read the tree during Play Mode."
                }, "The UIDocument has no visual tree yet.");
            }

            var budget = new TreeBudget { Remaining = maxItems };
            JObject tree = DescribeElement(root, maxDepth, 0, budget);
            if (budget.Remaining <= 0)
            {
                context.Warn("The visual tree dump was cut off after " + maxItems + " elements.");
            }

            return CucliCommandResult.Ok(new
            {
                gameObject = CucliObjectResolver.HierarchyPath(document.gameObject, true),
                sourceAsset = string.IsNullOrEmpty(sourceAsset) ? null : sourceAsset,
                maxDepth,
                maxItems,
                tree
            });
        }

        private sealed class TreeBudget
        {
            internal int Remaining;
        }

        private static JObject DescribeElement(VisualElement element, int maxDepth, int depth, TreeBudget budget)
        {
            budget.Remaining--;

            var entry = new JObject
            {
                ["type"] = element.GetType().Name,
                ["name"] = string.IsNullOrEmpty(element.name) ? null : element.name
            };

            var classes = new JArray();
            foreach (string className in element.GetClasses()) classes.Add(className);
            entry["classes"] = classes;

            var textElement = element as TextElement;
            if (textElement != null) entry["text"] = CucliDescribe.Truncate(textElement.text, 512);

            // resolvedStyle throws for a few members before the first layout pass.
            try
            {
                IResolvedStyle resolved = element.resolvedStyle;
                entry["resolvedStyle"] = new JObject
                {
                    ["width"] = SafeNumber(resolved.width),
                    ["height"] = SafeNumber(resolved.height),
                    ["display"] = resolved.display.ToString(),
                    ["visibility"] = resolved.visibility.ToString(),
                    ["opacity"] = SafeNumber(resolved.opacity),
                    ["color"] = ColorJson(resolved.color),
                    ["backgroundColor"] = ColorJson(resolved.backgroundColor),
                    ["fontSize"] = SafeNumber(resolved.fontSize)
                };
            }
            catch (Exception exception)
            {
                entry["resolvedStyle"] = new JObject
                {
                    ["$unavailable"] = exception.GetBaseException().Message
                };
            }

            int childCount = element.hierarchy.childCount;
            entry["childCount"] = childCount;
            if (childCount == 0) return entry;

            if (depth >= maxDepth || budget.Remaining <= 0)
            {
                entry["truncated"] = true;
                entry["truncateReason"] = depth >= maxDepth ? "maxDepth" : "maxItems";
                return entry;
            }

            var children = new JArray();
            for (int index = 0; index < childCount; index++)
            {
                if (budget.Remaining <= 0)
                {
                    entry["truncated"] = true;
                    entry["truncateReason"] = "maxItems";
                    break;
                }

                children.Add(DescribeElement(element.hierarchy[index], maxDepth, depth + 1, budget));
            }

            entry["children"] = children;
            return entry;
        }

        private static float SafeNumber(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        private static JObject ColorJson(Color color)
        {
            return new JObject
            {
                ["r"] = color.r,
                ["g"] = color.g,
                ["b"] = color.b,
                ["a"] = color.a,
                ["hex"] = "#" + ColorUtility.ToHtmlStringRGBA(color)
            };
        }

        // ---------------------------------------------------------------- modify_visual_element

        private static CucliCommandResult ModifyVisualElement(CucliCommandContext context)
        {
            CucliCommandResult failure;
            UIDocument document = ResolveDocument(context, out failure);
            if (failure != null) return failure;

            CucliArgs args = context.Input;
            string elementName = args.GetString("elementName") ?? args.GetString("element_name");
            if (string.IsNullOrWhiteSpace(elementName))
            {
                return CucliCommandResult.Missing(
                    "elementName", "It is matched against the UXML 'name' attribute via rootVisualElement.Q(name).");
            }

            VisualElement root = document.rootVisualElement;
            if (root == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "The UIDocument has no visual tree yet (assign panel settings and enable the document).",
                    new { gameObject = CucliObjectResolver.HierarchyPath(document.gameObject, true) });
            }

            VisualElement element = root.Q(elementName.Trim());
            if (element == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "No VisualElement named '" + elementName + "' exists in the visual tree. Element lookup uses "
                    + "root.Q(name) only - class names, CSS selectors and hierarchy paths are not supported.",
                    new
                    {
                        elementName,
                        gameObject = CucliObjectResolver.HierarchyPath(document.gameObject, true)
                    });
            }

            var modifications = new JArray();
            var skipped = new JArray();

            if (args.Has("text"))
            {
                var textElement = element as TextElement;
                if (textElement == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter,
                        "'text' can only be set on a TextElement (Label, Button, TextField, ...) but "
                        + DescribeElement(element) + " is not one.",
                        new { elementType = element.GetType().Name });
                }

                textElement.text = args.GetString("text") ?? string.Empty;
                modifications.Add("text");
            }

            string[] add = args.GetStringList("addClasses");
            if (add.Length == 0) add = args.GetStringList("add_classes");
            foreach (string className in add)
            {
                if (string.IsNullOrWhiteSpace(className)) continue;
                if (element.ClassListContains(className)) continue;
                element.AddToClassList(className.Trim());
                modifications.Add("addClass:" + className.Trim());
            }

            string[] remove = args.GetStringList("removeClasses");
            if (remove.Length == 0) remove = args.GetStringList("remove_classes");
            foreach (string className in remove)
            {
                if (string.IsNullOrWhiteSpace(className)) continue;
                if (!element.ClassListContains(className)) continue;
                element.RemoveFromClassList(className.Trim());
                modifications.Add("removeClass:" + className.Trim());
            }

            string[] toggle = args.GetStringList("toggleClasses");
            if (toggle.Length == 0) toggle = args.GetStringList("toggle_classes");
            foreach (string className in toggle)
            {
                if (string.IsNullOrWhiteSpace(className)) continue;
                element.ToggleInClassList(className.Trim());
                modifications.Add("toggleClass:" + className.Trim());
            }

            JObject style = args.GetObject("style") ?? args.GetObject("inlineStyle") ?? args.GetObject("inline_style");
            if (style != null)
            {
                foreach (JProperty property in style.Properties())
                {
                    string key = NormalizeStyleKey(property.Name);
                    string styleError;
                    if (ApplyStyle(element, key, property.Value, out styleError))
                    {
                        modifications.Add("style." + key);
                    }
                    else
                    {
                        skipped.Add(styleError);
                    }
                }
            }

            if (args.Has("enabled"))
            {
                element.SetEnabled(args.GetBool("enabled", true));
                modifications.Add("enabled");
            }

            if (args.Has("visible"))
            {
                string visible = args.GetString("visible");
                bool shown = string.Equals(visible, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(visible, "1", StringComparison.Ordinal);
                element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
                modifications.Add(shown ? "visible=true" : "visible=false");
            }

            if (args.Has("tooltip"))
            {
                element.tooltip = args.GetString("tooltip");
                modifications.Add("tooltip");
            }

            if (modifications.Count == 0 && skipped.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "No modifications applied. Provide one of: text, addClasses, removeClasses, toggleClasses, "
                    + "style, enabled, visible, tooltip.",
                    new { elementName });
            }

            var currentClasses = new JArray();
            foreach (string className in element.GetClasses()) currentClasses.Add(className);

            var data = new JObject
            {
                ["gameObject"] = CucliObjectResolver.HierarchyPath(document.gameObject, true),
                ["elementName"] = element.name,
                ["elementType"] = element.GetType().Name,
                ["modifications"] = modifications,
                ["currentClasses"] = currentClasses
            };

            if (skipped.Count > 0)
            {
                data["skipped"] = skipped;
                context.Warn(skipped.Count + " style key(s) were not recognised and were skipped.");
            }

            MarkSceneDirty(document.gameObject);

            return CucliCommandResult.Ok(
                data, "Applied " + modifications.Count + " modification(s) to " + elementName + ".");
        }

        internal static string NormalizeStyleKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            var builder = new StringBuilder(key.Length);
            foreach (char character in key)
            {
                if (character == '-' || character == '_' || character == ' ') continue;
                builder.Append(char.ToLowerInvariant(character));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Writes one inline style key. Returns false and an explanation when the key is not part of
        /// the recognised set, which is what lands in the caller's <c>skipped</c> list.
        /// </summary>
        internal static bool ApplyStyle(VisualElement element, string key, JToken value, out string error)
        {
            error = null;
            try
            {
                switch (key)
                {
                    case "backgroundcolor":
                        element.style.backgroundColor = ParseColor(value, out error);
                        return error == null;

                    case "color":
                        element.style.color = ParseColor(value, out error);
                        return error == null;

                    case "fontsize":
                        element.style.fontSize = ParseLength(value, out error);
                        return error == null;

                    case "width":
                        element.style.width = ParseLength(value, out error);
                        return error == null;

                    case "height":
                        element.style.height = ParseLength(value, out error);
                        return error == null;

                    case "minwidth":
                        element.style.minWidth = ParseLength(value, out error);
                        return error == null;

                    case "minheight":
                        element.style.minHeight = ParseLength(value, out error);
                        return error == null;

                    case "maxwidth":
                        element.style.maxWidth = ParseLength(value, out error);
                        return error == null;

                    case "maxheight":
                        element.style.maxHeight = ParseLength(value, out error);
                        return error == null;

                    case "opacity":
                        element.style.opacity = ParseNumber(value, out error);
                        return error == null;

                    case "display":
                        DisplayStyle display;
                        if (!TryParseEnum(value, out display, out error)) return false;
                        element.style.display = display;
                        return true;

                    case "visibility":
                        Visibility visibility;
                        if (!TryParseEnum(value, out visibility, out error)) return false;
                        element.style.visibility = visibility;
                        return true;

                    case "position":
                        Position position;
                        if (!TryParseEnum(value, out position, out error)) return false;
                        element.style.position = position;
                        return true;

                    case "flexgrow":
                        element.style.flexGrow = ParseNumber(value, out error);
                        return error == null;

                    case "flexshrink":
                        element.style.flexShrink = ParseNumber(value, out error);
                        return error == null;

                    case "flexdirection":
                        FlexDirection direction;
                        if (!TryParseEnum(value, out direction, out error)) return false;
                        element.style.flexDirection = direction;
                        return true;

                    case "alignitems":
                        Align align;
                        if (!TryParseEnum(value, out align, out error)) return false;
                        element.style.alignItems = align;
                        return true;

                    case "justifycontent":
                        Justify justify;
                        if (!TryParseEnum(value, out justify, out error)) return false;
                        element.style.justifyContent = justify;
                        return true;

                    case "margintop":
                        element.style.marginTop = ParseLength(value, out error);
                        return error == null;

                    case "marginright":
                        element.style.marginRight = ParseLength(value, out error);
                        return error == null;

                    case "marginbottom":
                        element.style.marginBottom = ParseLength(value, out error);
                        return error == null;

                    case "marginleft":
                        element.style.marginLeft = ParseLength(value, out error);
                        return error == null;

                    case "paddingtop":
                        element.style.paddingTop = ParseLength(value, out error);
                        return error == null;

                    case "paddingright":
                        element.style.paddingRight = ParseLength(value, out error);
                        return error == null;

                    case "paddingbottom":
                        element.style.paddingBottom = ParseLength(value, out error);
                        return error == null;

                    case "paddingleft":
                        element.style.paddingLeft = ParseLength(value, out error);
                        return error == null;

                    case "bordertopwidth":
                        element.style.borderTopWidth = ParseNumber(value, out error);
                        return error == null;

                    case "borderrightwidth":
                        element.style.borderRightWidth = ParseNumber(value, out error);
                        return error == null;

                    case "borderbottomwidth":
                        element.style.borderBottomWidth = ParseNumber(value, out error);
                        return error == null;

                    case "borderleftwidth":
                        element.style.borderLeftWidth = ParseNumber(value, out error);
                        return error == null;

                    case "bordertopcolor":
                        element.style.borderTopColor = ParseColor(value, out error);
                        return error == null;

                    case "borderrightcolor":
                        element.style.borderRightColor = ParseColor(value, out error);
                        return error == null;

                    case "borderbottomcolor":
                        element.style.borderBottomColor = ParseColor(value, out error);
                        return error == null;

                    case "borderleftcolor":
                        element.style.borderLeftColor = ParseColor(value, out error);
                        return error == null;

                    case "borderradius":
                        // One radius writes all four corners, matching manage_ui.
                        StyleLength radius = ParseLength(value, out error);
                        if (error != null) return false;
                        element.style.borderTopLeftRadius = radius;
                        element.style.borderTopRightRadius = radius;
                        element.style.borderBottomRightRadius = radius;
                        element.style.borderBottomLeftRadius = radius;
                        return true;

                    case "bordertopleftradius":
                        element.style.borderTopLeftRadius = ParseLength(value, out error);
                        return error == null;

                    case "bordertoprightradius":
                        element.style.borderTopRightRadius = ParseLength(value, out error);
                        return error == null;

                    case "borderbottomrightradius":
                        element.style.borderBottomRightRadius = ParseLength(value, out error);
                        return error == null;

                    case "borderbottomleftradius":
                        element.style.borderBottomLeftRadius = ParseLength(value, out error);
                        return error == null;

                    case "unityfontstyleandweight":
                        FontStyle fontStyle;
                        if (!TryParseEnum(value, out fontStyle, out error)) return false;
                        element.style.unityFontStyleAndWeight = fontStyle;
                        return true;

                    case "unitytextalign":
                        TextAnchor textAnchor;
                        if (!TryParseEnum(value, out textAnchor, out error)) return false;
                        element.style.unityTextAlign = textAnchor;
                        return true;

                    case "whitespace":
                        WhiteSpace whiteSpace;
                        if (!TryParseEnum(value, out whiteSpace, out error)) return false;
                        element.style.whiteSpace = whiteSpace;
                        return true;

                    default:
                        error = "style." + key + " is not a recognised inline style key";
                        return false;
                }
            }
            catch (Exception exception)
            {
                error = "style." + key + " failed: " + exception.GetBaseException().Message;
                return false;
            }
        }

        internal static StyleLength ParseLength(JToken value, out string error)
        {
            error = null;
            if (value == null || value.Type == JTokenType.Null)
            {
                error = "a number or a '12px' / '50%' string is required";
                return new StyleLength(StyleKeyword.Null);
            }

            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                return new StyleLength(new Length(value.Value<float>(), LengthUnit.Pixel));
            }

            string text = value.Type == JTokenType.String ? value.Value<string>() : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                text = text.Trim();
                StyleKeyword keyword;
                if (TryParseKeyword(text, out keyword)) return new StyleLength(keyword);

                bool percent = text.EndsWith("%", StringComparison.Ordinal);
                string numeric = percent ? text.Substring(0, text.Length - 1) : text;
                float parsed;
                if (float.TryParse(numeric, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return new StyleLength(new Length(parsed, percent ? LengthUnit.Percent : LengthUnit.Pixel));
                }
            }

            error = "'" + value.ToString(Newtonsoft.Json.Formatting.None)
                + "' is not a number, a '12px' / '50%' string or auto/none/initial";
            return new StyleLength(StyleKeyword.Null);
        }

        private static bool TryParseKeyword(string text, out StyleKeyword keyword)
        {
            keyword = StyleKeyword.Null;
            if (string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase))
            {
                keyword = StyleKeyword.Auto;
                return true;
            }

            if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            {
                keyword = StyleKeyword.None;
                return true;
            }

            if (string.Equals(text, "initial", StringComparison.OrdinalIgnoreCase))
            {
                keyword = StyleKeyword.Initial;
                return true;
            }

            if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            {
                keyword = StyleKeyword.Null;
                return true;
            }

            return false;
        }

        private static StyleFloat ParseNumber(JToken value, out string error)
        {
            error = null;
            if (value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float))
            {
                return new StyleFloat(value.Value<float>());
            }

            float parsed;
            if (value != null && value.Type == JTokenType.String
                && float.TryParse(value.Value<string>(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
            {
                return new StyleFloat(parsed);
            }

            error = "'" + (value == null ? "null" : value.ToString(Newtonsoft.Json.Formatting.None))
                + "' is not a number";
            return new StyleFloat(StyleKeyword.Null);
        }

        private static StyleColor ParseColor(JToken value, out string error)
        {
            error = null;
            if (value == null || value.Type == JTokenType.Null)
            {
                error = "a '#RRGGBB[AA]' string or an [r,g,b(,a)] array is required";
                return new StyleColor(Color.clear);
            }

            if (value.Type == JTokenType.Array)
            {
                var array = (JArray)value;
                if (array.Count < 3)
                {
                    error = "a colour array needs at least 3 components";
                    return new StyleColor(Color.clear);
                }

                float alpha = array.Count >= 4 ? array[3].Value<float>() : 1f;
                return new StyleColor(new Color(
                    array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(), alpha));
            }

            if (value.Type == JTokenType.Object)
            {
                var nested = new CucliArgs((JObject)value);
                if (nested.Has("r") || nested.Has("g") || nested.Has("b"))
                {
                    return new StyleColor(new Color(
                        nested.GetFloat("r"), nested.GetFloat("g"), nested.GetFloat("b"),
                        nested.GetFloat("a", 1f)));
                }
            }

            string text = value.Type == JTokenType.String ? value.Value<string>() : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                text = text.Trim();
                if (string.Equals(text, "clear", StringComparison.OrdinalIgnoreCase))
                {
                    return new StyleColor(Color.clear);
                }

                if (text[0] != '#') text = "#" + text;
                Color parsed;
                if (ColorUtility.TryParseHtmlString(text, out parsed)) return new StyleColor(parsed);
            }

            error = "'" + (text ?? value.ToString(Newtonsoft.Json.Formatting.None)) + "' is not a parseable colour";
            return new StyleColor(Color.clear);
        }

        private static bool TryParseEnum<T>(JToken value, out T parsed, out string error) where T : struct
        {
            parsed = default(T);
            error = null;

            if (value != null && value.Type == JTokenType.Integer)
            {
                try
                {
                    parsed = (T)Enum.ToObject(typeof(T), value.Value<int>());
                    return true;
                }
                catch (Exception)
                {
                    error = "the integer is not a valid " + typeof(T).Name + " value";
                    return false;
                }
            }

            string text = value == null || value.Type == JTokenType.Null ? null : value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    parsed = (T)Enum.Parse(typeof(T), text.Trim(), true);
                    return true;
                }
                catch (ArgumentException)
                {
                    error = "'" + text + "' is not a valid " + typeof(T).Name + " value (expected one of: "
                        + string.Join(", ", Enum.GetNames(typeof(T))) + ")";
                    return false;
                }
            }

            error = "a " + typeof(T).Name + " name is required";
            return false;
        }

        internal static bool TryParseEnumByName<T>(string text, out T parsed) where T : struct
        {
            parsed = default(T);
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                parsed = (T)Enum.Parse(typeof(T), text.Trim(), true);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- UXML validation

        /// <summary>
        /// Cheap well-formedness check so a malformed UXML never reaches the importer. Returns null
        /// when the document parses.
        /// </summary>
        internal static string ValidateUxml(string contents)
        {
            try
            {
                System.Xml.Linq.XDocument.Parse(contents, System.Xml.Linq.LoadOptions.None);
                return null;
            }
            catch (System.Xml.XmlException exception)
            {
                return "line " + exception.LineNumber + ", position " + exception.LinePosition
                    + ": " + exception.Message;
            }
            catch (Exception exception)
            {
                return exception.GetBaseException().Message;
            }
        }

        // ---------------------------------------------------------------- render_ui

        private static CucliCommandResult Render(CucliCommandContext context)
        {
            CucliCommandResult failure;
            UIDocument document = ResolveDocument(context, out failure);
            if (failure != null) return failure;

            CucliArgs args = context.Input;
            int width = Mathf.Clamp(args.GetInt("width", 1920), 16, 8192);
            int height = Mathf.Clamp(args.GetInt("height", 1080), 16, 8192);
            bool force = args.GetBool("force", false);

            string folder = args.GetString("outputFolder") ?? args.GetString("output_folder") ?? "Assets/Docs~";
            if (ContainsTraversal(folder))
            {
                return CucliCommandResult.Invalid("outputFolder", "the folder must not contain '..': " + folder);
            }

            folder = folder.Replace('\\', '/').Trim('/');
            if (folder.Length == 0) folder = "Assets/Docs~";

            string fileName = args.GetString("fileName") ?? args.GetString("file_name")
                ?? args.GetString("filename");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "ui-render-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            }

            if (ContainsTraversal(fileName))
            {
                return CucliCommandResult.Invalid("fileName", "the file name must not contain '..': " + fileName);
            }

            fileName = fileName.Replace('\\', '/').TrimStart('/');
            int lastSlash = fileName.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                folder = folder + "/" + fileName.Substring(0, lastSlash);
                fileName = fileName.Substring(lastSlash + 1);
            }

            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";

            string projectFolder = folder.StartsWith("Assets", StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith("Packages", StringComparison.OrdinalIgnoreCase)
                ? folder
                : "Assets/" + folder;
            string relativePath = projectFolder + "/" + fileName;
            string absolutePath = Path.Combine(
                CucliPaths.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(absolutePath) && !force)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    relativePath + " already exists. Re-run with force: true to overwrite it.",
                    new { path = relativePath });
            }

            VisualElement root = document.rootVisualElement;
            if (root == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "The UIDocument has no visual tree, so there is nothing to render. In Edit Mode the document "
                    + "must be enabled with its panel settings assigned.",
                    new { gameObject = CucliObjectResolver.HierarchyPath(document.gameObject, true) });
            }

            RenderTexture destination = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            if (destination == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "A " + width + "x" + height + " RenderTexture could not be allocated.");
            }

            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            bool rendered = false;
            string note = null;

            try
            {
                Texture panelTexture = PanelTexture(document);
                if (panelTexture != null)
                {
                    Graphics.Blit(panelTexture, destination);
                    rendered = true;
                }
                else
                {
                    UnityEngine.Camera camera = document.GetComponent<UnityEngine.Camera>();
                    if (camera == null) camera = UnityEngine.Camera.main;
                    if (camera != null)
                    {
                        RenderTexture previousTarget = camera.targetTexture;
                        camera.targetTexture = destination;
                        camera.Render();
                        camera.targetTexture = previousTarget;
                        rendered = true;
                        note = "Rendered through camera '" + camera.name
                            + "' because the PanelSettings has no target texture.";
                    }
                }

                if (rendered)
                {
                    RenderTexture.active = destination;
                    readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    readable.Apply();
                }
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Rendering the panel failed: " + exception.GetBaseException().Message);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            if (!rendered)
            {
                RenderTexture.ReleaseTemporary(destination);
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "The panel could not be rendered: the PanelSettings has no target texture and no Camera was "
                    + "available. Assign a PanelSettings with a target texture, or put a Camera on the UIDocument "
                    + "GameObject.",
                    new { gameObject = CucliObjectResolver.HierarchyPath(document.gameObject, true) });
            }

            byte[] png = readable.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(readable);

            CucliAssets.EnsureFolder(projectFolder);
            try
            {
                File.WriteAllBytes(absolutePath, png);
            }
            catch (Exception exception)
            {
                RenderTexture.ReleaseTemporary(destination);
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Writing " + relativePath + " failed: " + exception.GetBaseException().Message);
            }

            RenderTexture.ReleaseTemporary(destination);
            context.AddArtifact(absolutePath);

            string sourceAsset = CucliAssets.GetAssetPath(document.visualTreeAsset);
            var data = new JObject
            {
                ["path"] = relativePath,
                ["fullPath"] = absolutePath,
                ["width"] = width,
                ["height"] = height,
                ["bytes"] = png.Length,
                ["gameObject"] = CucliObjectResolver.HierarchyPath(document.gameObject, true),
                ["sourceAsset"] = string.IsNullOrEmpty(sourceAsset) ? null : sourceAsset,
                ["hasContent"] = true
            };

            if (note != null) data["note"] = note;

            if (args.GetBool("includeImage", args.GetBool("include_image", false)))
            {
                int maxResolution = Mathf.Clamp(
                    args.GetInt("maxResolution", args.GetInt("max_resolution", 640)), 16, 8192);
                int inlineWidth;
                int inlineHeight;
                byte[] inlined = Downsample(png, width, height, maxResolution, out inlineWidth, out inlineHeight);
                if (inlined != null)
                {
                    data["imageBase64"] = Convert.ToBase64String(inlined);
                    data["imageWidth"] = inlineWidth;
                    data["imageHeight"] = inlineHeight;
                }
                else
                {
                    context.Warn("The inlined image could not be produced; only the file was written.");
                }
            }

            return CucliCommandResult.Ok(data, "Rendered the UI panel to " + relativePath + ".");
        }

        /// <summary>Reads PanelSettings.m_TargetTexture, which has no public accessor in 2022.3.</summary>
        private static Texture PanelTexture(UIDocument document)
        {
            PanelSettings settings = document.panelSettings;
            if (settings == null) return null;

            try
            {
                FieldInfo field = typeof(PanelSettings).GetField(
                    "m_TargetTexture",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) return field.GetValue(settings) as Texture;

                PropertyInfo property = typeof(PanelSettings).GetProperty(
                    "targetTexture",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(settings, null) as Texture;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool ContainsTraversal(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] segments = path.Replace('\\', '/').Split('/');
            for (int index = 0; index < segments.Length; index++)
            {
                if (segments[index] == "..") return true;
            }

            return false;
        }

        private static byte[] Downsample(
            byte[] png, int width, int height, int maxResolution, out int outWidth, out int outHeight)
        {
            outWidth = width;
            outHeight = height;

            int longest = Mathf.Max(width, height);
            if (longest <= maxResolution) return png;

            float scale = (float)maxResolution / longest;
            outWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            outHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));

            var source = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var scaled = new Texture2D(outWidth, outHeight, TextureFormat.RGBA32, false);
            try
            {
                source.LoadImage(png);
                for (int y = 0; y < outHeight; y++)
                {
                    for (int x = 0; x < outWidth; x++)
                    {
                        scaled.SetPixel(x, y, source.GetPixelBilinear(
                            (x + 0.5f) / outWidth, (y + 0.5f) / outHeight));
                    }
                }

                scaled.Apply();
                return scaled.EncodeToPNG();
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(scaled);
            }
        }
    }
}
