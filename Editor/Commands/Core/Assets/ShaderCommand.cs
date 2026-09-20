using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli shader ...</c> - create, read, update and delete <c>.shader</c> files.
    ///
    /// Alignment target is MCP for Unity's <c>manage_shader</c>. The generated templates are plain
    /// string literals selected between URP and Built-in style: no URP assembly is referenced at
    /// compile time, which is what keeps this command group loadable in a project without URP.
    /// </summary>
    internal sealed class ShaderCommand : CucliCommand
    {
        private static readonly Regex IdentifierPattern =
            new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant);

        public override string Name => "shader";

        public override string Summary =>
            "Create, read, update and delete .shader files, with URP and Built-in templates.";

        public override CucliActionInfo[] Actions => Declare(
            "create", "Create a .shader file from the URP or Built-in template, or explicit contents.",
            "read", "Read a shader file: line range and maximum characters.",
            "update", "Replace a shader file's contents.",
            "delete", "Move a shader file to the OS trash. Requires force=true.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Edit;

        public override bool MutatesProject => true;

        public override string[] Effects => new[] { "project.assets.read", "project.assets.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""create"", ""read"", ""update"", ""delete""], ""default"": ""create"" },
    ""name"": { ""type"": ""string"", ""description"": ""着色器名，拼成 {name}.shader；须匹配 ^[a-zA-Z_][a-zA-Z0-9_]*$。"" },
    ""path"": { ""type"": ""string"", ""description"": ""相对 Assets/ 的目录；空时默认 Shaders。"" },
    ""contents"": { ""type"": ""string"", ""description"": ""action=create/update: 着色器源码。"" },
    ""contentsEncoded"": { ""type"": ""boolean"", ""default"": false, ""aliases"": [""contents_encoded""] },
    ""encodedContents"": { ""type"": ""string"", ""aliases"": [""encoded_contents""] },
    ""template"": { ""type"": ""string"", ""default"": ""urp"", ""description"": ""action=create: urp 或 builtin。"" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=delete: 必须显式确认。"" },
    ""startLine"": { ""type"": ""integer"", ""aliases"": [""start_line""], ""description"": ""action=read: 1 基起始行。"" },
    ""endLine"": { ""type"": ""integer"", ""aliases"": [""end_line""], ""description"": ""action=read: 1 基结束行（含）。"" },
    ""maxChars"": { ""type"": ""integer"", ""default"": 20000, ""aliases"": [""max_chars""] }
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
                case "delete": return Delete(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ---------------------------------------------------------------- shared plumbing

        /// <summary>
        /// Resolves the shader asset path: <c>path</c> is relative to Assets/, <c>name</c> is the
        /// file stem and the result is always <c>Assets/&lt;dir&gt;/&lt;name&gt;.shader</c>.
        /// </summary>
        private static bool TryResolveShaderPath(
            CucliArgs args,
            bool mustExist,
            out string shaderAbsolutePath,
            out string assetPath,
            out string error)
        {
            shaderAbsolutePath = null;
            assetPath = null;
            error = null;

            string name = args.GetString("name");
            string pathArgument = args.GetString("path");

            if (!string.IsNullOrWhiteSpace(name))
            {
                name = name.Trim();
                if (name.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - ".shader".Length);
                }

                if (!IdentifierPattern.IsMatch(name))
                {
                    error = "着色器名 '" + name + "' 必须匹配 ^[a-zA-Z_][a-zA-Z0-9_]*$。";
                    return false;
                }
            }

            string directory = "Assets/Shaders";

            if (!string.IsNullOrWhiteSpace(pathArgument))
            {
                string raw = pathArgument.Trim().Replace('\\', '/');
                bool explicitFile = raw.EndsWith(".shader", StringComparison.OrdinalIgnoreCase);
                string normalized = CucliAssets.NormalizeAssetPath(raw);
                if (normalized == null)
                {
                    error = "路径 '" + pathArgument + "' 越出工程范围，目标必须在 Assets/ 下。";
                    return false;
                }

                if (explicitFile)
                {
                    if (string.IsNullOrWhiteSpace(name)) name = CucliAssets.FileNameWithoutExtension(normalized);
                    directory = CucliAssets.ParentFolder(normalized) ?? "Assets";
                }
                else
                {
                    directory = normalized;
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "缺少 'name'：需要着色器名。";
                return false;
            }

            assetPath = directory + "/" + name + ".shader";

            string absolute = CucliAssets.ToAbsolute(assetPath);
            if (absolute == null)
            {
                error = "无法解析 " + assetPath + " 的物理路径。";
                return false;
            }

            if (mustExist && !File.Exists(absolute))
            {
                error = "着色器不存在：" + assetPath + "。";
                return false;
            }

            shaderAbsolutePath = absolute;
            return true;
        }

        private static string ReadText(string absolutePath)
        {
            string text = File.ReadAllText(absolutePath, Encoding.UTF8);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text;
        }

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

            return args.GetString("contents");
        }

        // ---------------------------------------------------------------- create

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string absolute;
            string assetPath;
            string error;
            if (!TryResolveShaderPath(args, false, out absolute, out assetPath, out error))
            {
                return CucliCommandResult.Invalid("path", error);
            }

            if (File.Exists(absolute))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "着色器文件已存在：" + assetPath + "。请改用 'cucli shader update'。",
                    new { path = assetPath });
            }

            string directory = CucliAssets.ParentFolder(assetPath);
            if (directory != null && !CucliAssets.EnsureFolder(directory))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "无法创建目录 " + directory + "。", new { path = directory });
            }

            string contents = ReadContents(args);
            bool templated = string.IsNullOrWhiteSpace(contents);

            if (templated)
            {
                string name = CucliAssets.FileNameWithoutExtension(assetPath);
                string template = args.GetString("template") ?? "urp";
                contents = BuildTemplate(name, template);
            }

            WriteTextAtomic(absolute, contents);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            context.AddArtifact(absolute);

            string shaderName = CucliAssets.FileNameWithoutExtension(assetPath);
            Shader found = Shader.Find(shaderName);

            var data = new JObject
            {
                ["path"] = assetPath,
                ["absolutePath"] = absolute,
                ["name"] = shaderName,
                ["templated"] = templated,
                ["bytes"] = Encoding.UTF8.GetByteCount(contents),
                ["shaderFindable"] = found != null,
                ["note"] = found != null
                    ? null
                    : "Shader.Find('" + shaderName + "') 目前找不到它；Shader.Find 只覆盖已编译进构建或处于 Always Included 的 shader。"
            };

            return CucliCommandResult.Ok(
                data,
                "已创建着色器 " + assetPath + (templated ? "（使用 " + (args.GetString("template") ?? "urp") + " 模板）" : "") + "。");
        }

        private static string BuildTemplate(string name, string template)
        {
            string kind = string.IsNullOrWhiteSpace(template) ? "urp" : template.Trim().ToLowerInvariant();
            switch (kind)
            {
                case "builtin":
                case "built-in":
                case "standard":
                    return BuiltinTemplate(name);
                case "urp":
                case "universal":
                case "universalrenderpipeline":
                    return UrpTemplate(name);
                default:
                    return UrpTemplate(name);
            }
        }

        /// <summary>
        /// URP-style template. Deliberately dependency-free: no URP assembly reference at compile
        /// time and no ShaderLibrary include, so it compiles in any project. The HLSL uses only
        /// built-in matrices, which URP still populates.
        /// </summary>
        private static string UrpTemplate(string name)
        {
            var builder = new StringBuilder();
            builder.Append("// URP 风格的独立 shader：不依赖 Universal RP 的 ShaderLibrary。\n");
            builder.Append("// 需要完整 URP 光照时，把下面 Pass 里的 include 打开并接入 URP 包。\n");
            builder.Append("Shader \"").Append(name).Append("\"\n");
            builder.Append("{\n");
            builder.Append("    Properties\n");
            builder.Append("    {\n");
            builder.Append("        _BaseColor (\"Base Color\", Color) = (1, 1, 1, 1)\n");
            builder.Append("        _BaseMap (\"Base Map\", 2D) = \"white\" {}\n");
            builder.Append("        _Smoothness (\"Smoothness\", Range(0, 1)) = 0.5\n");
            builder.Append("    }\n");
            builder.Append("\n");
            builder.Append("    SubShader\n");
            builder.Append("    {\n");
            builder.Append("        Tags\n");
            builder.Append("        {\n");
            builder.Append("            \"RenderType\" = \"Opaque\"\n");
            builder.Append("            \"RenderPipeline\" = \"UniversalPipeline\"\n");
            builder.Append("            \"Queue\" = \"Geometry\"\n");
            builder.Append("        }\n");
            builder.Append("\n");
            builder.Append("        Pass\n");
            builder.Append("        {\n");
            builder.Append("            Name \"ForwardLit\"\n");
            builder.Append("            Tags { \"LightMode\" = \"UniversalForward\" }\n");
            builder.Append("            Cull Back\n");
            builder.Append("            ZWrite On\n");
            builder.Append("\n");
            builder.Append("            HLSLPROGRAM\n");
            builder.Append("            #pragma vertex vert\n");
            builder.Append("            #pragma fragment frag\n");
            builder.Append("            #pragma target 3.0\n");
            builder.Append("\n");
            builder.Append("            // 需要 URP 光照/CBUFFER 时启用：\n");
            builder.Append("            // #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"\n");
            builder.Append("            // #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl\"\n");
            builder.Append("\n");
            builder.Append("            float4 _BaseColor;\n");
            builder.Append("            float4 _BaseMap_ST;\n");
            builder.Append("            float _Smoothness;\n");
            builder.Append("            TEXTURE2D(_BaseMap);\n");
            builder.Append("            SAMPLER(sampler_BaseMap);\n");
            builder.Append("\n");
            builder.Append("            struct Attributes\n");
            builder.Append("            {\n");
            builder.Append("                float4 positionOS : POSITION;\n");
            builder.Append("                float2 uv : TEXCOORD0;\n");
            builder.Append("                float3 normalOS : NORMAL;\n");
            builder.Append("            };\n");
            builder.Append("\n");
            builder.Append("            struct Varyings\n");
            builder.Append("            {\n");
            builder.Append("                float4 positionCS : SV_POSITION;\n");
            builder.Append("                float2 uv : TEXCOORD0;\n");
            builder.Append("                float3 normalWS : TEXCOORD1;\n");
            builder.Append("            };\n");
            builder.Append("\n");
            builder.Append("            Varyings vert(Attributes input)\n");
            builder.Append("            {\n");
            builder.Append("                Varyings output;\n");
            builder.Append("                output.positionCS = UnityObjectToClipPos(input.positionOS);\n");
            builder.Append("                output.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;\n");
            builder.Append("                output.normalWS = UnityObjectToWorldNormal(input.normalOS);\n");
            builder.Append("                return output;\n");
            builder.Append("            }\n");
            builder.Append("\n");
            builder.Append("            half4 frag(Varyings input) : SV_Target\n");
            builder.Append("            {\n");
            builder.Append("                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;\n");
            builder.Append("                half3 lightDirection = normalize(half3(0.3h, 0.8h, 0.5h));\n");
            builder.Append("                half ndotl = saturate(dot(normalize(input.normalWS), lightDirection));\n");
            builder.Append("                return half4(albedo.rgb * (0.25h + 0.75h * ndotl), albedo.a);\n");
            builder.Append("            }\n");
            builder.Append("            ENDHLSL\n");
            builder.Append("        }\n");
            builder.Append("    }\n");
            builder.Append("\n");
            builder.Append("    FallBack \"Hidden/InternalErrorShader\"\n");
            builder.Append("}\n");
            return builder.ToString();
        }

        /// <summary>Built-in pipeline template; self-contained CGPROGRAM with UnityCG.cginc only.</summary>
        private static string BuiltinTemplate(string name)
        {
            var builder = new StringBuilder();
            builder.Append("Shader \"").Append(name).Append("\"\n");
            builder.Append("{\n");
            builder.Append("    Properties\n");
            builder.Append("    {\n");
            builder.Append("        _Color (\"Color\", Color) = (1, 1, 1, 1)\n");
            builder.Append("        _MainTex (\"Albedo\", 2D) = \"white\" {}\n");
            builder.Append("        _Glossiness (\"Smoothness\", Range(0, 1)) = 0.5\n");
            builder.Append("        _Metallic (\"Metallic\", Range(0, 1)) = 0.0\n");
            builder.Append("    }\n");
            builder.Append("\n");
            builder.Append("    SubShader\n");
            builder.Append("    {\n");
            builder.Append("        Tags { \"RenderType\" = \"Opaque\" \"Queue\" = \"Geometry\" }\n");
            builder.Append("        LOD 200\n");
            builder.Append("\n");
            builder.Append("        CGPROGRAM\n");
            builder.Append("        #pragma surface surf Standard fullforwardshadows\n");
            builder.Append("        #pragma target 3.0\n");
            builder.Append("\n");
            builder.Append("        sampler2D _MainTex;\n");
            builder.Append("\n");
            builder.Append("        struct Input\n");
            builder.Append("        {\n");
            builder.Append("            float2 uv_MainTex;\n");
            builder.Append("        };\n");
            builder.Append("\n");
            builder.Append("        half _Glossiness;\n");
            builder.Append("        half _Metallic;\n");
            builder.Append("        fixed4 _Color;\n");
            builder.Append("\n");
            builder.Append("        void surf(Input IN, inout SurfaceOutputStandard o)\n");
            builder.Append("        {\n");
            builder.Append("            fixed4 albedo = tex2D(_MainTex, IN.uv_MainTex) * _Color;\n");
            builder.Append("            o.Albedo = albedo.rgb;\n");
            builder.Append("            o.Metallic = _Metallic;\n");
            builder.Append("            o.Smoothness = _Glossiness;\n");
            builder.Append("            o.Alpha = albedo.a;\n");
            builder.Append("        }\n");
            builder.Append("        ENDCG\n");
            builder.Append("    }\n");
            builder.Append("\n");
            builder.Append("    FallBack \"Diffuse\"\n");
            builder.Append("}\n");
            return builder.ToString();
        }

        // ---------------------------------------------------------------- read

        private static CucliCommandResult Read(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string absolute;
            string assetPath;
            string error;
            if (!TryResolveShaderPath(args, true, out absolute, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            string contents = ReadText(absolute);
            string[] lines = contents.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            int startLine = Math.Max(1, args.GetInt("startLine", args.GetInt("start_line", 1)));
            int endLine = args.GetInt("endLine", args.GetInt("end_line", 0));
            if (endLine <= 0 || endLine > lines.Length) endLine = lines.Length;

            int maxChars = args.GetInt("maxChars", args.GetInt("max_chars", 20000));
            if (maxChars <= 0) maxChars = 20000;

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
                ["path"] = assetPath,
                ["absolutePath"] = absolute,
                ["contents"] = text,
                ["contentsEncoded"] = false,
                ["encodedContents"] = null,
                ["totalLines"] = lines.Length,
                ["startLine"] = startLine,
                ["endLine"] = endLine,
                ["truncated"] = truncated,
                ["bytes"] = Encoding.UTF8.GetByteCount(contents)
            };

            return CucliCommandResult.Ok(
                data,
                "已读取 " + assetPath + " 的第 " + startLine + "-" + endLine + " 行（共 " + lines.Length + " 行）。");
        }

        // ---------------------------------------------------------------- update

        private static CucliCommandResult Update(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string absolute;
            string assetPath;
            string error;
            if (!TryResolveShaderPath(args, true, out absolute, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            string contents = ReadContents(args);
            if (string.IsNullOrEmpty(contents))
            {
                return CucliCommandResult.Missing("contents", "update 需要非空的 contents。");
            }

            string existing = ReadText(absolute);
            if (string.Equals(existing, contents, StringComparison.Ordinal))
            {
                return CucliCommandResult.Ok(
                    new { path = assetPath, noOp = true, bytes = Encoding.UTF8.GetByteCount(contents) },
                    "内容与现有文件完全相同，未写盘（noOp=true）。");
            }

            WriteTextAtomic(absolute, contents);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            context.AddArtifact(absolute);

            return CucliCommandResult.Ok(
                new { path = assetPath, noOp = false, bytes = Encoding.UTF8.GetByteCount(contents) },
                "已更新着色器 " + assetPath + "。");
        }

        // ---------------------------------------------------------------- delete

        private static CucliCommandResult Delete(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string absolute;
            string assetPath;
            string error;
            if (!TryResolveShaderPath(args, true, out absolute, out assetPath, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error, new { path = assetPath });
            }

            if (!args.GetBool("force", false))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "删除着色器 " + assetPath + " 会让引用它的材质丢失 shader。确认后请加 --force 重新执行。",
                    new { path = assetPath });
            }

            bool deleted = AssetDatabase.DeleteAsset(assetPath);
            if (!deleted)
            {
                try
                {
                    File.Delete(absolute);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    deleted = true;
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
                "已删除着色器 " + assetPath + "。");
        }
    }
}
