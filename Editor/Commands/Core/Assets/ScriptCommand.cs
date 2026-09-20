using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli script ...</c> - read and edit C# source files safely: line-ranged reads, precise
    /// line/column text edits, structural validation, hashes and creation from a template.
    ///
    /// Alignment target is MCP for Unity's <c>manage_script</c>. Because Roslyn is not referenced by
    /// this assembly, <c>validate</c> performs structural checks (bracket balance, namespace/using
    /// consistency, duplicate members) and says so in its message rather than pretending to compile.
    /// </summary>
    internal sealed class ScriptCommand : CucliCommand
    {
        private const int MaxTextEditBytes = 65536;

        private static readonly Regex IdentifierPattern =
            new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant);

        private static readonly Regex UsingPattern =
            new Regex(@"^\s*using\s+[A-Za-z_][A-Za-z0-9_.]*\s*;", RegexOptions.Multiline);

        private static readonly Regex NamespacePattern =
            new Regex(@"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*\{?", RegexOptions.Multiline);

        public override string Name => "script";

        public override string Summary =>
            "Create, read, edit, validate, hash and delete C# scripts under Assets/.";

        public override CucliActionInfo[] Actions => Declare(
            "create", "Create a .cs file from a template or explicit contents.",
            "read", "Read a slice of a script: line range and maximum characters.",
            "update", "Deprecated alias of apply_text_edits (whole-file overwrite with the given contents).",
            "edit", "Structured edits: replace/delete class or method, anchor insert/delete/replace.",
            "apply_text_edits", "Replace exact line/column ranges, guarded by a precondition SHA-256.",
            "validate", "Structural validation: bracket balance, using/namespace consistency, duplicate members.",
            "get_sha", "Report the SHA-256 of the file contents.",
            "delete", "Move a script to the OS trash. Requires force=true.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Edit;

        public override bool MutatesProject => true;

        /// <summary>Writing a .cs file makes Unity recompile and reload the domain, so the job must survive it.</summary>
        public override bool MayReloadDomain => true;

        public override string[] Effects => new[] { "project.assets.read", "project.assets.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""create"", ""read"", ""update"", ""edit"", ""apply_text_edits"", ""validate"", ""get_sha"", ""delete""],
      ""default"": ""create""
    },
    ""name"": { ""type"": ""string"", ""description"": ""类名或文件名，拼成 {name}.cs；须匹配 ^[a-zA-Z_][a-zA-Z0-9_]*$。"" },
    ""path"": { ""type"": ""string"", ""description"": ""相对 Assets/ 的目录；空时默认 Scripts。"" },
    ""contents"": { ""type"": ""string"", ""aliases"": [""content""], ""description"": ""action=create/update: 脚本正文。也接受 --content."" },
    ""contentsEncoded"": { ""type"": ""boolean"", ""default"": false, ""aliases"": [""contents_encoded""] },
    ""encodedContents"": { ""type"": ""string"", ""aliases"": [""encoded_contents""], ""description"": ""contentsEncoded=true 时的 base64 正文。"" },
    ""scriptType"": { ""type"": ""string"", ""aliases"": [""script_type""], ""description"": ""action=create: MonoBehaviour / ScriptableObject / Editor / EditorWindow / Plain。"" },
    ""namespace"": { ""type"": ""string"", ""description"": ""action=create: 生成模板的外层 namespace。"" },
    ""edits"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""precondition_sha256"": { ""type"": ""string"", ""aliases"": [""preconditionSha256""], ""description"": ""action=apply_text_edits: 前置文件 SHA-256。"" },
    ""options"": { ""type"": ""object"", ""description"": ""{refresh, validate, applyMode}。"" },
    ""level"": { ""type"": ""string"", ""description"": ""action=validate: basic / standard / comprehensive / strict。"" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=delete: 必须显式确认。"" },
    ""startLine"": { ""type"": ""integer"", ""aliases"": [""start_line""], ""description"": ""action=read: 1 基起始行。"" },
    ""endLine"": { ""type"": ""integer"", ""aliases"": [""end_line""], ""description"": ""action=read: 1 基结束行（含）。"" },
    ""maxChars"": { ""type"": ""integer"", ""default"": 20000, ""aliases"": [""max_chars"", ""maxStringLength""], ""description"": ""action=read: 返回内容的最大字符数。"" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "create");
            switch (action)
            {
                case "create": return Create(context);
                case "read": return Read(context);
                case "update": return Update(context);
                case "edit": return Edit(context);
                case "apply_text_edits": return ApplyTextEdits(context);
                case "validate": return Validate(context);
                case "get_sha": return GetSha(context);
                case "delete": return Delete(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ---------------------------------------------------------------- shared plumbing

        /// <summary>
        /// Resolves the target .cs path the way MCP does: <c>path</c> is a directory relative to
        /// Assets/, <c>name</c> is the file stem. The result is always inside Assets/.
        /// </summary>
        private static bool TryResolveScriptPath(
            CucliArgs args,
            bool mustExist,
            out string scriptPath,
            out string assetPath,
            out string error)
        {
            scriptPath = null;
            assetPath = null;
            error = null;

            string name = args.GetString("name");
            string pathArgument = args.GetString("path");

            if (!string.IsNullOrWhiteSpace(name))
            {
                name = name.Trim();
                if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 3);
                if (!IdentifierPattern.IsMatch(name))
                {
                    error = "类名/文件名 '" + name + "' 必须匹配 ^[a-zA-Z_][a-zA-Z0-9_]*$。";
                    return false;
                }
            }

            string directory = "Assets/Scripts";

            if (!string.IsNullOrWhiteSpace(pathArgument))
            {
                string raw = pathArgument.Trim().Replace('\\', '/');
                bool explicitFile = raw.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

                string normalized = CucliAssets.NormalizeAssetPath(raw);
                if (normalized == null)
                {
                    error = "路径 '" + pathArgument + "' 越出工程范围。目标必须是 Assets/ 下的目录。";
                    return false;
                }

                if (explicitFile)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = CucliAssets.FileNameWithoutExtension(normalized);
                    }

                    string fileDirectory = CucliAssets.ParentFolder(normalized);
                    directory = fileDirectory ?? "Assets";
                }
                else
                {
                    directory = normalized;
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                // No name given: only a fully qualified file path can identify the target.
                if (!string.IsNullOrWhiteSpace(pathArgument)
                    && pathArgument.Trim().EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    string normalized = CucliAssets.NormalizeAssetPath(pathArgument);
                    name = CucliAssets.FileNameWithoutExtension(normalized);
                }
                else
                {
                    error = "缺少 'name'：需要脚本的类名或文件名。";
                    return false;
                }
            }

            assetPath = directory + "/" + name + ".cs";

            string absolute = CucliAssets.ToAbsolute(assetPath);
            if (absolute == null)
            {
                error = "无法解析 " + assetPath + " 的物理路径。";
                return false;
            }

            if (mustExist && !File.Exists(absolute))
            {
                error = "脚本不存在：" + assetPath + "。";
                return false;
            }

            scriptPath = absolute;
            return true;
        }

        private static string ReadText(string absolutePath)
        {
            string text = File.ReadAllText(absolutePath, Encoding.UTF8);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text;
        }

        /// <summary>UTF-8 without BOM, written through a temp file so a failed write cannot truncate the script.</summary>
        private static void WriteTextAtomic(string absolutePath, string content)
        {
            string directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = absolutePath + ".cucli.tmp";
            File.WriteAllText(temporary, content, CucliJson.Utf8NoBom);
            try
            {
                if (File.Exists(absolutePath)) File.Replace(temporary, absolutePath, null);
                else File.Move(temporary, absolutePath);
            }
            catch (Exception)
            {
                File.Copy(temporary, absolutePath, true);
                try { File.Delete(temporary); }
                catch (IOException) { }
            }
        }

        private static string Hash(string content)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
                var builder = new StringBuilder(hash.Length * 2);
                for (int index = 0; index < hash.Length; index++) builder.Append(hash[index].ToString("x2"));
                return builder.ToString();
            }
        }

        private static string HashFile(string absolutePath)
        {
            using (var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                for (int index = 0; index < hash.Length; index++) builder.Append(hash[index].ToString("x2"));
                return builder.ToString();
            }
        }

        private static string Uri(string assetPath)
        {
            return "mcpforunity://path/" + assetPath;
        }

        private static string ReadContents(CucliArgs args)
        {
            bool encoded = args.GetBool("contentsEncoded", args.GetBool("contents_encoded", false));
            if (encoded)
            {
                string payload = args.GetString("encodedContents") ?? args.GetString("encoded_contents");
                if (string.IsNullOrWhiteSpace(payload))
                {
                    throw CucliUsageException.Invalid(
                        "encodedContents", "contentsEncoded=true 时必须给出 encodedContents（base64）。");
                }

                try
                {
                    return Encoding.UTF8.GetString(System.Convert.FromBase64String(payload.Trim()));
                }
                catch (FormatException)
                {
                    throw CucliUsageException.Invalid("encodedContents", "base64 解码失败。");
                }
            }

            return args.GetFirstString("contents", "content");
        }

        /// <summary>Refreshes the asset database after a write; scripts are recompiled by Unity itself.</summary>
        private static void RefreshScript(CucliCommandContext context, CucliArgs args, string assetPath)
        {
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            string refresh = null;
            JObject options = args.GetObject("options");
            if (options != null) refresh = new CucliArgs(options).GetString("refresh");

            if (!string.IsNullOrWhiteSpace(refresh)
                && (string.Equals(refresh.Trim(), "immediate", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(refresh.Trim(), "sync", StringComparison.OrdinalIgnoreCase)))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }

        private static void WarnDeprecatedAlias(
            CucliCommandContext context,
            string action,
            string replacement,
            string hint)
        {
            context.Warn(
                "'" + action + "' 是 MCP 的废弃别名，建议改用 '" + replacement + "'。" + (hint ?? string.Empty));
        }

        // ---------------------------------------------------------------- create

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string scriptPath;
            string assetPath;
            string error;
            if (!TryResolveScriptPath(args, false, out scriptPath, out assetPath, out error))
            {
                return CucliCommandResult.Invalid("path", error);
            }

            if (File.Exists(scriptPath))
            {
                if (!args.GetBool("force", false))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetExists,
                        "脚本已存在：" + assetPath + "。请改用 'cucli script apply_text_edits' 精确修改，或加 --force 覆盖。",
                        new { path = assetPath });
                }
            }

            string directory = CucliAssets.ParentFolder(assetPath);
            if (directory != null && !CucliAssets.EnsureFolder(directory))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "无法创建目录 " + directory + "。", new { path = directory });
            }

            string contents = ReadContents(args);
            if (string.IsNullOrWhiteSpace(contents))
            {
                string scriptType = args.GetString("scriptType") ?? args.GetString("script_type");
                string name = CucliAssets.FileNameWithoutExtension(assetPath);
                contents = BuildTemplate(name, scriptType, args.GetString("namespace"));
            }

            WriteTextAtomic(scriptPath, contents);
            RefreshScript(context, args, assetPath);
            context.AddArtifact(scriptPath);

            string sha = Hash(contents);
            var data = new JObject
            {
                ["uri"] = Uri(assetPath),
                ["path"] = assetPath,
                ["absolutePath"] = scriptPath,
                ["sha256"] = sha,
                ["lengthBytes"] = Encoding.UTF8.GetByteCount(contents),
                ["scheduledRefresh"] = false,
                ["lineCount"] = CountLines(contents)
            };

            return CucliCommandResult.Ok(data, "已创建脚本 " + assetPath + "（" + data["lineCount"] + " 行）。");
        }

        private static int CountLines(string content)
        {
            if (string.IsNullOrEmpty(content)) return 0;
            int lines = 1;
            for (int index = 0; index < content.Length; index++)
            {
                if (content[index] == '\n') lines++;
            }

            return lines;
        }

        private static string BuildTemplate(string name, string scriptType, string namespaceName)
        {
            string type = string.IsNullOrWhiteSpace(scriptType) ? "MonoBehaviour" : scriptType.Trim();
            var builder = new StringBuilder();

            builder.Append("using UnityEngine;\n");

            bool editorScript = false;
            string baseType;
            switch (type.ToLowerInvariant())
            {
                case "monobehaviour": case "behaviour": case "behavior": baseType = "MonoBehaviour"; break;
                case "scriptableobject": baseType = "ScriptableObject"; break;
                case "editor": baseType = "Editor"; editorScript = true; break;
                case "editorwindow": baseType = "EditorWindow"; editorScript = true; break;
                case "plain": case "class": case "none": baseType = null; break;
                default: baseType = type; break;
            }

            if (baseType == "ScriptableObject") builder.Append("using UnityEngine;\n");
            if (editorScript) builder.Append("using UnityEditor;\n");

            builder.Append('\n');

            bool hasNamespace = !string.IsNullOrWhiteSpace(namespaceName);
            string indent = hasNamespace ? "    " : string.Empty;

            if (hasNamespace)
            {
                builder.Append("namespace ").Append(namespaceName.Trim()).Append('\n');
                builder.Append("{\n");
            }

            if (baseType == null)
            {
                builder.Append(indent).Append("public class ").Append(name).Append('\n');
            }
            else
            {
                builder.Append(indent).Append("public class ").Append(name).Append(" : ").Append(baseType).Append('\n');
            }

            builder.Append(indent).Append("{\n");
            builder.Append(indent).Append("}\n");

            if (hasNamespace) builder.Append("}\n");

            return builder.ToString();
        }

        // ---------------------------------------------------------------- read

        private static CucliCommandResult Read(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string scriptPath;
            string assetPath;
            string error;
            if (!TryResolveScriptPath(args, true, out scriptPath, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            string contents = ReadText(scriptPath);
            string[] lines = contents.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            int startLine = Math.Max(1, args.GetInt("startLine", args.GetInt("start_line", 1)));
            int endLine = args.GetInt("endLine", args.GetInt("end_line", 0));
            int maxChars = args.GetInt("maxChars", args.GetInt("max_chars", args.GetInt("maxStringLength", 20000)));
            if (maxChars <= 0) maxChars = 20000;

            if (endLine <= 0 || endLine > lines.Length) endLine = lines.Length;
            if (startLine > endLine)
            {
                return CucliCommandResult.Invalid(
                    "startLine", "startLine(" + startLine + ") 大于 endLine(" + endLine + ")。");
            }

            var slice = new StringBuilder();
            for (int index = startLine - 1; index < endLine && index < lines.Length; index++)
            {
                slice.Append(lines[index]);
                if (index < endLine - 1) slice.Append('\n');
            }

            string text = slice.ToString();
            bool truncated = false;
            if (text.Length > maxChars)
            {
                text = text.Substring(0, maxChars);
                truncated = true;
                context.Warn("返回内容已截断到 " + maxChars + " 个字符；请用 startLine/endLine 继续读取。");
            }

            var data = new JObject
            {
                ["uri"] = Uri(assetPath),
                ["path"] = assetPath,
                ["absolutePath"] = scriptPath,
                ["contents"] = text,
                ["contentsEncoded"] = false,
                ["encodedContents"] = null,
                ["totalLines"] = lines.Length,
                ["startLine"] = startLine,
                ["endLine"] = endLine,
                ["truncated"] = truncated,
                ["sha256"] = Hash(contents),
                ["lengthBytes"] = Encoding.UTF8.GetByteCount(contents)
            };

            return CucliCommandResult.Ok(
                data,
                "已读取 " + assetPath + " 的第 " + startLine + "-" + endLine + " 行（共 " + lines.Length + " 行）。");
        }

        // ---------------------------------------------------------------- deprecated aliases

        private static CucliCommandResult Update(CucliCommandContext context)
        {
            WarnDeprecatedAlias(
                context, "update", "apply_text_edits",
                "update 会整文件覆盖，丢失未提及的改动。");

            CucliArgs args = context.Input;

            string scriptPath;
            string assetPath;
            string error;
            if (!TryResolveScriptPath(args, true, out scriptPath, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            string contents = ReadContents(args);
            if (string.IsNullOrEmpty(contents))
            {
                return CucliCommandResult.Missing("contents", "update 需要非空的 contents。");
            }

            string existing = ReadText(scriptPath);
            if (string.Equals(existing, contents, StringComparison.Ordinal))
            {
                var noOp = new JObject
                {
                    ["uri"] = Uri(assetPath),
                    ["path"] = assetPath,
                    ["noOp"] = true,
                    ["editsApplied"] = 0,
                    ["sha256"] = Hash(existing),
                    ["scheduledRefresh"] = false,
                    ["evidence"] = new JObject { ["reason"] = "identical_content" }
                };
                return CucliCommandResult.Ok(noOp, "内容与现有文件完全相同，未写盘（noOp=true）。");
            }

            WriteTextAtomic(scriptPath, contents);
            RefreshScript(context, args, assetPath);
            context.AddArtifact(scriptPath);

            var data = new JObject
            {
                ["uri"] = Uri(assetPath),
                ["path"] = assetPath,
                ["noOp"] = false,
                ["sha256"] = Hash(contents),
                ["scheduledRefresh"] = false
            };
            return CucliCommandResult.Ok(data, "已覆盖写入 " + assetPath + "（update 为废弃别名）。");
        }

        // ---------------------------------------------------------------- apply_text_edits

        private static CucliCommandResult ApplyTextEdits(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string scriptPath;
            string assetPath;
            string error;
            if (!TryResolveScriptPath(args, true, out scriptPath, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            JArray edits = args.GetArray("edits");
            if (edits == null || edits.Count == 0)
            {
                return CucliCommandResult.Missing("edits", "需要至少一条 {startLine,startCol,endLine,endCol,newText} 编辑。");
            }

            string original = ReadText(scriptPath);
            string originalSha = Hash(original);

            string precondition = args.GetString("precondition_sha256") ?? args.GetString("preconditionSha256");
            if (string.IsNullOrWhiteSpace(precondition))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.MissingParameter,
                    "apply_text_edits 需要 precondition_sha256（可先用 'cucli script get_sha' 取得）。",
                    new { status = "precondition_required", current_sha256 = originalSha });
            }

            if (!string.Equals(precondition.Trim(), originalSha, StringComparison.OrdinalIgnoreCase))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "文件已被改动，precondition_sha256 与当前内容不一致（stale_file）。请重新读取后再提交编辑。",
                    new { status = "stale_file", expected_sha256 = precondition.Trim(), current_sha256 = originalSha });
            }

            int firstCodeOffset = FindFirstCodeOffset(original);

            var resolved = new List<TextEdit>();
            for (int index = 0; index < edits.Count; index++)
            {
                var edit = edits[index] as JObject;
                if (edit == null)
                {
                    return CucliCommandResult.Invalid("edits", "第 " + index + " 条编辑不是对象。");
                }

                var editArgs = new CucliArgs(edit);
                int startLine = editArgs.GetInt("startLine", editArgs.GetInt("start_line", editArgs.GetInt("line", 0)));
                int startCol = editArgs.GetInt("startCol", editArgs.GetInt("start_column", editArgs.GetInt("column", 0)));
                int endLine = editArgs.GetInt("endLine", editArgs.GetInt("end_line", 0));
                int endCol = editArgs.GetInt("endCol", editArgs.GetInt("end_column", 0));
                string newText = editArgs.GetFirstString("newText", "new_text", "text") ?? string.Empty;

                if (endLine < 1) endLine = startLine;
                if (endCol < 1) endCol = startCol;

                if (startLine < 1 || startCol < 1 || endLine < 1 || endCol < 1)
                {
                    return CucliCommandResult.Invalid(
                        "edits",
                        "第 " + index + " 条编辑的行列必须是 1 基正整数（收到 startLine=" + startLine
                        + ", startCol=" + startCol + ", endLine=" + endLine + ", endCol=" + endCol + "）。");
                }

                int startOffset;
                int endOffset;
                string offsetError;
                if (!TryOffset(original, startLine, startCol, out startOffset, out offsetError)
                    || !TryOffset(original, endLine, endCol, out endOffset, out offsetError))
                {
                    return CucliCommandResult.Invalid("edits", "第 " + index + " 条编辑越界：" + offsetError);
                }

                if (endOffset < startOffset)
                {
                    int swap = startOffset;
                    startOffset = endOffset;
                    endOffset = swap;
                }

                if (startOffset < firstCodeOffset)
                {
                    return CucliCommandResult.Invalid(
                        "edits",
                        "第 " + index + " 条编辑落在首个 using/声明之前（using_guard），已拒绝。");
                }

                resolved.Add(new TextEdit
                {
                    Index = index,
                    Start = startOffset,
                    End = endOffset,
                    NewText = newText
                });
            }

            resolved.Sort((left, right) => right.Start.CompareTo(left.Start));

            for (int index = 0; index < resolved.Count - 1; index++)
            {
                if (resolved[index].Start < resolved[index + 1].End)
                {
                    return CucliCommandResult.Invalid(
                        "edits",
                        "编辑区间重叠：第 " + resolved[index + 1].Index + " 条与第 " + resolved[index].Index + " 条。");
                }
            }

            var totalNewText = new StringBuilder();
            for (int index = 0; index < resolved.Count; index++) totalNewText.Append(resolved[index].NewText);
            if (Encoding.UTF8.GetByteCount(totalNewText.ToString()) > MaxTextEditBytes)
            {
                return CucliCommandResult.Invalid(
                    "edits", "单次编辑的新文本合计超过 " + MaxTextEditBytes + " 字节（too_large）。");
            }

            string updated = original;
            for (int index = 0; index < resolved.Count; index++)
            {
                TextEdit edit = resolved[index];
                updated = updated.Substring(0, edit.Start) + edit.NewText + updated.Substring(edit.End);
            }

            string structuralError;
            if (!CheckBracketBalance(updated, out structuralError))
            {
                return CucliCommandResult.Invalid("newText", "编辑会导致括号不配对：" + structuralError);
            }

            if (string.Equals(updated, original, StringComparison.Ordinal))
            {
                var noOp = new JObject
                {
                    ["uri"] = Uri(assetPath),
                    ["path"] = assetPath,
                    ["editsApplied"] = 0,
                    ["noOp"] = true,
                    ["sha256"] = originalSha,
                    ["scheduledRefresh"] = false,
                    ["evidence"] = new JObject { ["reason"] = "identical_content" }
                };
                return CucliCommandResult.Ok(noOp, "编辑后内容与原文完全一致，未写盘（noOp=true）。");
            }

            WriteTextAtomic(scriptPath, updated);
            RefreshScript(context, args, assetPath);
            context.AddArtifact(scriptPath);

            string newSha = Hash(updated);
            var data = new JObject
            {
                ["uri"] = Uri(assetPath),
                ["path"] = assetPath,
                ["editsApplied"] = resolved.Count,
                ["noOp"] = false,
                ["sha256"] = newSha,
                ["scheduledRefresh"] = false
            };

            return CucliCommandResult.Ok(data, "已应用 " + resolved.Count + " 条编辑到 " + assetPath + "。");
        }

        private sealed class TextEdit
        {
            internal int Index;
            internal int Start;
            internal int End;
            internal string NewText;
        }

        /// <summary>Converts a 1-based (line, column) pair into a character offset.</summary>
        private static bool TryOffset(string text, int line, int column, out int offset, out string error)
        {
            offset = 0;
            error = null;

            int currentLine = 1;
            int index = 0;
            while (currentLine < line && index < text.Length)
            {
                if (text[index] == '\n') currentLine++;
                index++;
            }

            if (currentLine < line)
            {
                error = "行 " + line + " 超出文件范围。";
                return false;
            }

            int lineEnd = index;
            while (lineEnd < text.Length && text[lineEnd] != '\n' && text[lineEnd] != '\r') lineEnd++;
            int lineLength = lineEnd - index;

            if (column < 1 || column > lineLength + 1)
            {
                error = "第 " + line + " 行只有 " + lineLength + " 个字符，列 " + column + " 越界。";
                return false;
            }

            offset = index + column - 1;
            return true;
        }

        /// <summary>Offset of the first character that is neither blank, a comment nor a using directive.</summary>
        private static int FindFirstCodeOffset(string text)
        {
            int offset = 0;
            int index = 0;
            while (index < text.Length)
            {
                int lineStart = index;
                int lineEnd = lineStart;
                while (lineEnd < text.Length && text[lineEnd] != '\n') lineEnd++;

                string line = text.Substring(lineStart, lineEnd - lineStart).Trim();
                if (line.Length > 0
                    && !line.StartsWith("//", StringComparison.Ordinal)
                    && !line.StartsWith("/*", StringComparison.Ordinal)
                    && !line.StartsWith("*", StringComparison.Ordinal)
                    && !line.StartsWith("using ", StringComparison.Ordinal)
                    && !line.StartsWith("#", StringComparison.Ordinal))
                {
                    offset = lineStart;
                    break;
                }

                index = lineEnd + 1;
                offset = Math.Min(index, text.Length);
            }

            return offset;
        }

        private static bool CheckBracketBalance(string text, out string error)
        {
            error = null;
            int braces = 0;
            int brackets = 0;
            int parentheses = 0;
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;
            bool inChar = false;
            bool escape = false;
            int line = 1;

            for (int index = 0; index < text.Length; index++)
            {
                char current = text[index];
                if (current == '\n')
                {
                    line++;
                    inLineComment = false;
                    escape = false;
                    continue;
                }

                if (inLineComment) continue;

                if (inBlockComment)
                {
                    if (current == '*' && index + 1 < text.Length && text[index + 1] == '/')
                    {
                        inBlockComment = false;
                        index++;
                    }

                    continue;
                }

                if (inString || inChar)
                {
                    if (escape)
                    {
                        escape = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        escape = true;
                        continue;
                    }

                    if (inString && current == '"') inString = false;
                    else if (inChar && current == '\'') inChar = false;
                    continue;
                }

                if (current == '/' && index + 1 < text.Length)
                {
                    if (text[index + 1] == '/')
                    {
                        inLineComment = true;
                        index++;
                        continue;
                    }

                    if (text[index + 1] == '*')
                    {
                        inBlockComment = true;
                        index++;
                        continue;
                    }
                }

                switch (current)
                {
                    case '"': inString = true; break;
                    case '\'': inChar = true; break;
                    case '{': braces++; break;
                    case '}':
                        braces--;
                        if (braces < 0)
                        {
                            error = "第 " + line + " 行出现多余的 '}'。";
                            return false;
                        }

                        break;
                    case '[': brackets++; break;
                    case ']':
                        brackets--;
                        if (brackets < 0)
                        {
                            error = "第 " + line + " 行出现多余的 ']'。";
                            return false;
                        }

                        break;
                    case '(': parentheses++; break;
                    case ')':
                        parentheses--;
                        if (parentheses < 0)
                        {
                            error = "第 " + line + " 行出现多余的 ')'。";
                            return false;
                        }

                        break;
                }
            }

            if (braces != 0)
            {
                error = "花括号不平衡（差 " + braces + " 个）。";
                return false;
            }

            if (brackets != 0)
            {
                error = "方括号不平衡（差 " + brackets + " 个）。";
                return false;
            }

            if (parentheses != 0)
            {
                error = "圆括号不平衡（差 " + parentheses + " 个）。";
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------- validate

        private static CucliCommandResult Validate(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string scriptPath;
            string assetPath;
            string error;
            if (!TryResolveScriptPath(args, true, out scriptPath, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            string level = args.GetString("level") ?? "standard";
            switch (level.Trim().ToLowerInvariant())
            {
                case "basic": case "standard": case "comprehensive": case "strict": break;
                default: level = "standard"; break;
            }

            string source = ReadText(scriptPath);
            var diagnostics = new JArray();

            string bracketError;
            if (!CheckBracketBalance(source, out bracketError))
            {
                diagnostics.Add(new JObject
                {
                    ["line"] = 0,
                    ["col"] = 0,
                    ["severity"] = "error",
                    ["message"] = bracketError
                });
            }

            int namespaceCount = NamespacePattern.Matches(source).Count;
            int usingCount = UsingPattern.Matches(source).Count;
            if (namespaceCount == 0 && source.IndexOf("class ", StringComparison.Ordinal) >= 0)
            {
                diagnostics.Add(new JObject
                {
                    ["line"] = 0,
                    ["col"] = 0,
                    ["severity"] = "info",
                    ["message"] = "文件未声明 namespace（结构性提示，不是编译错误）。"
                });
            }

            if (usingCount > 0)
            {
                int firstUsingLine = 0;
                int lastUsingEnd = 0;
                MatchCollection usingMatches = UsingPattern.Matches(source);
                for (int index = 0; index < usingMatches.Count; index++)
                {
                    int position = usingMatches[index].Index;
                    int lineNumber = 1 + CountNewlines(source, position);
                    if (firstUsingLine == 0) firstUsingLine = lineNumber;
                    lastUsingEnd = position + usingMatches[index].Length;
                }

                MatchCollection namespaceMatches = NamespacePattern.Matches(source);
                for (int index = 0; index < namespaceMatches.Count; index++)
                {
                    if (namespaceMatches[index].Index >= lastUsingEnd) continue;
                    diagnostics.Add(new JObject
                    {
                        ["line"] = 1 + CountNewlines(source, namespaceMatches[index].Index),
                        ["col"] = 0,
                        ["severity"] = "warning",
                        ["message"] = "namespace 声明出现在 using 之后，风格上通常应置于 using 之前。"
                    });
                    break;
                }

                if (firstUsingLine > 1)
                {
                    for (int index = 0; index < firstUsingLine - 1; index++)
                    {
                        string line = GetLine(source, index);
                        if (line.Trim().Length == 0) continue;
                        if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                        diagnostics.Add(new JObject
                        {
                            ["line"] = index + 1,
                            ["col"] = 0,
                            ["severity"] = "warning",
                            ["message"] = "using 指令之前出现了可执行文本。"
                        });
                        break;
                    }
                }
            }

            if (string.Equals(level.Trim().ToLowerInvariant(), "comprehensive", StringComparison.Ordinal)
                || string.Equals(level.Trim().ToLowerInvariant(), "strict", StringComparison.Ordinal))
            {
                foreach (Match match in Regex.Matches(source, @"(class|struct|interface)\s+([A-Za-z_][A-Za-z0-9_]*)"))
                {
                    string keyword = match.Groups[2].Value;
                    if (keyword.Length == 0) continue;
                    if (!char.IsUpper(keyword[0]))
                    {
                        diagnostics.Add(new JObject
                        {
                            ["line"] = 1 + CountNewlines(source, match.Index),
                            ["col"] = 0,
                            ["severity"] = "warning",
                            ["message"] = "类型名 '" + keyword + "' 未使用 PascalCase 命名。"
                        });
                    }
                }

                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Match match in Regex.Matches(source, @"(?:void|int|float|bool|string|[A-Za-z_][A-Za-z0-9_.<>\[\]]*)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\("))
                {
                    string method = match.Groups[1].Value;
                    if (string.Equals(method, "if", StringComparison.Ordinal)
                        || string.Equals(method, "for", StringComparison.Ordinal)
                        || string.Equals(method, "while", StringComparison.Ordinal)
                        || string.Equals(method, "switch", StringComparison.Ordinal)
                        || string.Equals(method, "foreach", StringComparison.Ordinal)
                        || string.Equals(method, "using", StringComparison.Ordinal)
                        || string.Equals(method, "return", StringComparison.Ordinal)
                        || string.Equals(method, "new", StringComparison.Ordinal)
                        || string.Equals(method, "lock", StringComparison.Ordinal)
                        || string.Equals(method, "catch", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int line = 1 + CountNewlines(source, match.Index);
                    int previous;
                    if (seen.TryGetValue(method, out previous))
                    {
                        diagnostics.Add(new JObject
                        {
                            ["line"] = line,
                            ["col"] = 0,
                            ["severity"] = "warning",
                            ["message"] = "方法名 '" + method + "' 在第 " + previous + " 行已出现过，重载会被结构检查记为可疑重复。"
                        });
                    }
                    else
                    {
                        seen[method] = line;
                    }
                }
            }

            bool hasErrors = false;
            for (int index = 0; index < diagnostics.Count; index++)
            {
                if (string.Equals(
                        diagnostics[index]["severity"].Value<string>(), "error", StringComparison.Ordinal))
                {
                    hasErrors = true;
                    break;
                }
            }

            var data = new JObject
            {
                ["uri"] = Uri(assetPath),
                ["path"] = assetPath,
                ["sha256"] = Hash(source),
                ["level"] = level.Trim().ToLowerInvariant(),
                ["structuralOnly"] = true,
                ["diagnostics"] = diagnostics,
                ["errorCount"] = hasErrors ? 1 : 0,
                ["lineCount"] = CountLines(source)
            };

            string message = hasErrors
                ? "结构检查发现错误：本工程未启用 Roslyn，validate 只做括号平衡与结构一致性检查，不代表编译结果。"
                : "结构检查通过（括号平衡、using/namespace 一致性）。本工程未启用 Roslyn，validate 不执行真实编译。";

            return hasErrors
                ? CucliCommandResult.Fail(CucliErrors.CompileError, message, data)
                : CucliCommandResult.Ok(data, message);
        }

        private static int CountNewlines(string text, int position)
        {
            int count = 0;
            int limit = Math.Min(position, text.Length);
            for (int index = 0; index < limit; index++)
            {
                if (text[index] == '\n') count++;
            }

            return count;
        }

        private static string GetLine(string text, int index)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return index >= 0 && index < lines.Length ? lines[index] : string.Empty;
        }

        // ---------------------------------------------------------------- get_sha

        private static CucliCommandResult GetSha(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string scriptPath;
            string assetPath;
            string error;
            if (!TryResolveScriptPath(args, true, out scriptPath, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            var info = new FileInfo(scriptPath);
            var data = new JObject
            {
                ["uri"] = Uri(assetPath),
                ["path"] = assetPath,
                ["absolutePath"] = scriptPath,
                ["sha256"] = HashFile(scriptPath),
                ["lengthBytes"] = info.Length,
                ["lastModifiedUtc"] = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture)
            };

            return CucliCommandResult.Ok(data, "已计算 " + assetPath + " 的 SHA-256。");
        }

        // ---------------------------------------------------------------- delete

        private static CucliCommandResult Delete(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string scriptPath;
            string assetPath;
            string error;
            if (!TryResolveScriptPath(args, true, out scriptPath, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            if (!args.GetBool("force", false))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "删除脚本 " + assetPath + " 会触发重新编译。确认后请加 --force 重新执行。",
                    new { path = assetPath });
            }

            bool moved = AssetDatabase.MoveAssetToTrash(assetPath);
            if (!moved)
            {
                try
                {
                    File.Delete(scriptPath);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    moved = true;
                }
                catch (Exception exception)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "删除 " + assetPath + " 失败：" + exception.GetBaseException().Message,
                        new { path = assetPath });
                }
            }

            return CucliCommandResult.Ok(
                new { path = assetPath, deleted = true },
                "已删除脚本 " + assetPath + "。");
        }

        // ---------------------------------------------------------------- edit (structured)

        private static CucliCommandResult Edit(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string scriptPath;
            string assetPath;
            string error;
            if (!TryResolveScriptPath(args, true, out scriptPath, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            JArray edits = args.GetArray("edits");
            if (edits == null || edits.Count == 0)
            {
                return CucliCommandResult.Missing("edits", "edit 需要至少一条结构化编辑。");
            }

            context.Warn("'edit' 是 MCP 的结构化编辑别名；能表达为行列范围时建议改用 'apply_text_edits'。");

            string original = ReadText(scriptPath);
            string working = original;
            int applied = 0;
            var notes = new JArray();

            for (int index = 0; index < edits.Count; index++)
            {
                context.ThrowIfCancelled();

                var edit = edits[index] as JObject;
                if (edit == null)
                {
                    return CucliCommandResult.Invalid("edits", "第 " + index + " 条编辑不是对象。");
                }

                string mode = new CucliArgs(edit).GetString("mode") ?? new CucliArgs(edit).GetString("op");
                if (string.IsNullOrWhiteSpace(mode))
                {
                    return CucliCommandResult.Invalid("edits", "第 " + index + " 条编辑缺少 mode（或 op）。");
                }

                string editError;
                string result;
                if (!ApplyStructuredEdit(working, edit, mode.Trim().ToLowerInvariant(), out result, out editError))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter,
                        "第 " + index + " 条编辑（" + mode + "）失败：" + editError,
                        new { path = assetPath, appliedSoFar = applied, editIndex = index });
                }

                if (!string.Equals(result, working, StringComparison.Ordinal)) applied++;
                working = result;

                notes.Add(new JObject { ["index"] = index, ["mode"] = mode, ["ok"] = true });
            }

            if (string.Equals(working, original, StringComparison.Ordinal))
            {
                var noOp = new JObject
                {
                    ["uri"] = Uri(assetPath),
                    ["path"] = assetPath,
                    ["editsApplied"] = 0,
                    ["noOp"] = true,
                    ["sha256"] = Hash(original),
                    ["scheduledRefresh"] = false,
                    ["evidence"] = new JObject { ["reason"] = "identical_content" }
                };
                return CucliCommandResult.Ok(noOp, "结构化编辑后内容与原文一致，未写盘（noOp=true）。");
            }

            string structuralError;
            if (!CheckBracketBalance(working, out structuralError))
            {
                return CucliCommandResult.Invalid("replacement", "编辑会导致括号不配对：" + structuralError);
            }

            WriteTextAtomic(scriptPath, working);
            RefreshScript(context, args, assetPath);
            context.AddArtifact(scriptPath);

            var data = new JObject
            {
                ["uri"] = Uri(assetPath),
                ["path"] = assetPath,
                ["editsApplied"] = applied,
                ["noOp"] = false,
                ["sha256"] = Hash(working),
                ["scheduledRefresh"] = false,
                ["results"] = notes
            };

            return CucliCommandResult.Ok(data, "已应用 " + applied + " 条结构化编辑到 " + assetPath + "。");
        }

        private static bool ApplyStructuredEdit(
            string source,
            JObject edit,
            string mode,
            out string result,
            out string error)
        {
            result = source;
            error = null;
            var args = new CucliArgs(edit);

            string replacement = args.GetString("replacement");
            string text = args.GetString("text");
            string anchor = args.GetString("anchor");

            switch (mode)
            {
                case "anchor_insert":
                case "anchor_delete":
                case "anchor_replace":
                {
                    if (string.IsNullOrWhiteSpace(anchor))
                    {
                        error = "anchor 类 mode 需要 anchor（多行正则）。";
                        return false;
                    }

                    Regex regex;
                    try
                    {
                        regex = new Regex(anchor, RegexOptions.Multiline | RegexOptions.CultureInvariant);
                    }
                    catch (ArgumentException exception)
                    {
                        error = "anchor 不是合法正则：" + exception.Message;
                        return false;
                    }

                    Match match = null;
                    foreach (Match candidate in regex.Matches(source))
                    {
                        if (match == null || candidate.Length > match.Length) match = candidate;
                    }

                    if (match == null || match.Length == 0)
                    {
                        error = "anchor 未命中（anchor not found）。";
                        return false;
                    }

                    if (string.Equals(mode, "anchor_delete", StringComparison.Ordinal))
                    {
                        result = RemoveSpan(source, match.Index, match.Length);
                        return true;
                    }

                    if (string.Equals(mode, "anchor_replace", StringComparison.Ordinal))
                    {
                        string payload = text ?? replacement ?? string.Empty;
                        result = source.Substring(0, match.Index) + payload
                            + source.Substring(match.Index + match.Length);
                        return true;
                    }

                    string position = args.GetString("position");
                    bool after = string.Equals(position, "after", StringComparison.OrdinalIgnoreCase);
                    int insertAt = after ? match.Index + match.Length : match.Index;
                    string insert = text ?? replacement ?? string.Empty;
                    result = source.Substring(0, insertAt) + insert + source.Substring(insertAt);
                    return true;
                }
            }

            string className = args.GetString("className") ?? args.GetString("class_name");
            bool classMode = mode.IndexOf("class", StringComparison.Ordinal) >= 0;
            string memberName = classMode ? className : args.GetString("methodName") ?? args.GetString("method_name");

            if (string.IsNullOrWhiteSpace(memberName))
            {
                error = classMode
                    ? "该 mode 需要 className。"
                    : "该 mode 需要 methodName。";
                return false;
            }

            if (string.Equals(mode, "insert_method", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(className))
                {
                    error = "insert_method 需要 className。";
                    return false;
                }

                int classOpen;
                int classClose;
                if (!TryFindTypeBody(source, className, out classOpen, out classClose, out error)) return false;

                string payload = text ?? replacement ?? string.Empty;
                if (string.IsNullOrWhiteSpace(payload))
                {
                    error = "insert_method 需要非空的 replacement（或 text）。";
                    return false;
                }

                string position = args.GetString("position");
                int insertAt = string.Equals(position, "start", StringComparison.OrdinalIgnoreCase)
                    ? classOpen + 1
                    : classClose;

                result = source.Substring(0, insertAt) + "\n" + payload + "\n"
                    + source.Substring(insertAt);
                return true;
            }

            string declarationPattern = classMode
                ? @"\b(?:class|struct|interface)\s+" + Regex.Escape(memberName) + @"\b"
                : @"\b" + Regex.Escape(memberName) + @"\s*\(";

            Match declaration = Regex.Match(source, declarationPattern, RegexOptions.CultureInvariant);
            if (!declaration.Success)
            {
                error = "找不到声明 '" + memberName + "'。";
                return false;
            }

            int openBrace = source.IndexOf('{', declaration.Index);
            if (openBrace < 0)
            {
                error = "声明 '" + memberName + "' 之后找不到方法体或类型体。";
                return false;
            }

            int closeBrace;
            if (classMode)
            {
                if (!TryFindTypeBody(source, memberName, out openBrace, out closeBrace, out error)) return false;
            }
            else if (!TryFindBlockEnd(source, openBrace, out closeBrace, out error))
            {
                return false;
            }

            if (string.Equals(mode, "delete_class", StringComparison.Ordinal)
                || string.Equals(mode, "delete_method", StringComparison.Ordinal))
            {
                result = RemoveSpan(source, declaration.Index, closeBrace - declaration.Index + 1);
                return true;
            }

            if (string.Equals(mode, "replace_class", StringComparison.Ordinal)
                || string.Equals(mode, "replace_method", StringComparison.Ordinal))
            {
                string payload = replacement;
                if (string.IsNullOrWhiteSpace(payload))
                {
                    string base64 = args.GetString("replacementBase64") ?? args.GetString("replacement_base64");
                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        try
                        {
                            payload = Encoding.UTF8.GetString(System.Convert.FromBase64String(base64.Trim()));
                        }
                        catch (FormatException)
                        {
                            error = "replacementBase64 不是合法 base64。";
                            return false;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(payload))
                {
                    error = "replace_class/replace_method 需要 replacement（或 replacementBase64）。";
                    return false;
                }

                result = source.Substring(0, declaration.Index) + payload
                    + source.Substring(closeBrace + 1);
                return true;
            }

            error = "不支持的 mode '" + mode + "'。建议改用 'apply_text_edits' 做精确行列替换。";
            return false;
        }

        private static bool TryFindTypeBody(
            string source,
            string typeName,
            out int openBrace,
            out int closeBrace,
            out string error)
        {
            openBrace = -1;
            closeBrace = -1;
            error = null;

            Match declaration = Regex.Match(
                source,
                @"\b(?:class|struct|interface)\s+" + Regex.Escape(typeName) + @"\b",
                RegexOptions.CultureInvariant);

            if (!declaration.Success)
            {
                error = "找不到类型 '" + typeName + "'。";
                return false;
            }

            int brace = source.IndexOf('{', declaration.Index);
            if (brace < 0)
            {
                error = "类型 '" + typeName + "' 没有方法体。";
                return false;
            }

            int end;
            if (!TryFindBlockEnd(source, brace, out end, out error)) return false;

            openBrace = brace;
            closeBrace = end;
            return true;
        }

        /// <summary>Given the offset of '{', returns the offset of its matching '}'.</summary>
        private static bool TryFindBlockEnd(string source, int openBrace, out int closeBrace, out string error)
        {
            closeBrace = -1;
            error = null;
            if (openBrace < 0 || openBrace >= source.Length || source[openBrace] != '{')
            {
                error = "内部错误：起始位置不是 '{'。";
                return false;
            }

            int depth = 0;
            bool inString = false;
            bool inChar = false;
            bool inLineComment = false;
            bool inBlockComment = false;
            bool escape = false;

            for (int index = openBrace; index < source.Length; index++)
            {
                char current = source[index];

                if (current == '\n')
                {
                    inLineComment = false;
                    escape = false;
                    continue;
                }

                if (inLineComment) continue;

                if (inBlockComment)
                {
                    if (current == '*' && index + 1 < source.Length && source[index + 1] == '/')
                    {
                        inBlockComment = false;
                        index++;
                    }

                    continue;
                }

                if (inString || inChar)
                {
                    if (escape)
                    {
                        escape = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        escape = true;
                        continue;
                    }

                    if (inString && current == '"') inString = false;
                    else if (inChar && current == '\'') inChar = false;
                    continue;
                }

                if (current == '/' && index + 1 < source.Length)
                {
                    if (source[index + 1] == '/')
                    {
                        inLineComment = true;
                        index++;
                        continue;
                    }

                    if (source[index + 1] == '*')
                    {
                        inBlockComment = true;
                        index++;
                        continue;
                    }
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == '\'')
                {
                    inChar = true;
                    continue;
                }

                if (current == '{') depth++;
                else if (current == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = index;
                        return true;
                    }
                }
            }

            error = "括号不平衡，找不到匹配的 '}'。";
            return false;
        }

        private static string RemoveSpan(string source, int start, int length)
        {
            if (length <= 0) return source;
            int end = Math.Min(source.Length, start + length);

            int lineStart = source.LastIndexOf('\n', Math.Max(0, start - 1));
            if (lineStart < 0) lineStart = 0; else lineStart++;

            int lineEnd = source.IndexOf('\n', end);
            bool onlyWhitespaceBefore = true;
            for (int index = lineStart; index < start; index++)
            {
                if (!char.IsWhiteSpace(source[index]))
                {
                    onlyWhitespaceBefore = false;
                    break;
                }
            }

            bool onlyWhitespaceAfter = true;
            if (lineEnd < 0) lineEnd = source.Length;
            for (int index = end; index < lineEnd; index++)
            {
                if (!char.IsWhiteSpace(source[index]))
                {
                    onlyWhitespaceAfter = false;
                    break;
                }
            }

            if (onlyWhitespaceBefore && onlyWhitespaceAfter && lineEnd < source.Length)
            {
                return source.Substring(0, lineStart) + source.Substring(lineEnd + 1);
            }

            return source.Substring(0, start) + source.Substring(end);
        }
    }
}
