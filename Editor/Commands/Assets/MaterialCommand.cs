using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli material ...</c> - the material surface: create a material, write shader properties,
    /// set colours, assign materials to renderers and describe a material's shader.
    ///
    /// Alignment target is MCP for Unity's <c>manage_material</c>. Property writes always check
    /// <see cref="Material.HasProperty"/> first and report <c>MEMBER_NOT_FOUND</c> with the list of
    /// available property <em>names</em> - never their values, which would flood the response.
    /// </summary>
    internal sealed class MaterialCommand : CucliCommand
    {
        public override string Name => "material";

        public override string Summary =>
            "Create materials, write shader properties and colours, assign materials to renderers, inspect a material's shader surface.";

        public override CucliActionInfo[] Actions => Declare(
            "ping", "Liveness probe for this command group.",
            "create", "Create a material asset with a shader and optional initial properties.",
            "set_material_shader_property", "Write one shader property (float/int/color/vector/texture).",
            "set_material_color", "Write a colour on a material.",
            "assign_material_to_renderer", "Assign a material asset to a renderer slot.",
            "set_renderer_color", "Set a colour on a material, in place or as a new instance/asset.",
            "get_material_info", "Report shader name, property names, keywords and optionally values.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[] { "project.assets.read", "project.assets.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""ping"", ""create"", ""set_material_shader_property"", ""set_material_color"",
                 ""assign_material_to_renderer"", ""set_renderer_color"", ""get_material_info""],
      ""default"": ""ping""
    },
    ""materialPath"": { ""type"": ""string"", ""aliases"": [""material_path"", ""path""], ""description"": ""Assets 相对材质路径，自动补 Assets/ 前缀与 .mat 后缀。也接受 --path。"" },
    ""property"": { ""type"": ""string"", ""description"": ""着色器属性名。"" },
    ""value"": { ""description"": ""属性值：number / bool / array / object / string。"" },
    ""color"": { ""description"": ""颜色：[r,g,b(,a)] / 对象 {r,g,b,a} / #RRGGBB(AA)。"" },
    ""shader"": { ""type"": ""string"", ""description"": ""action=create: 着色器名，默认按当前渲染管线解析。"" },
    ""properties"": { ""type"": ""object"", ""description"": ""action=create: 批量属性表。"" },
    ""target"": { ""type"": ""string"", ""description"": ""action=assign_material_to_renderer/set_renderer_color: 场景 GameObject 名称或层级路径。"" },
    ""searchMethod"": { ""type"": ""string"", ""aliases"": [""search_method""], ""description"": ""by_name / by_path / by_id / by_tag / by_layer / by_component。"" },
    ""slot"": { ""type"": ""integer"", ""default"": 0 },
    ""mode"": { ""type"": ""string"", ""default"": ""property_block"", ""description"": ""property_block / shared / instance / create_unique。"" },
    ""includeValues"": { ""type"": ""boolean"", ""default"": false, ""aliases"": [""include_values""], ""description"": ""action=get_material_info: 是否返回属性值。"" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "ping");
            switch (action)
            {
                case "ping": return Ping();
                case "create": return Create(context);
                case "set_material_shader_property": return SetShaderProperty(context);
                case "set_material_color": return SetColor(context);
                case "assign_material_to_renderer": return AssignToRenderer(context);
                case "set_renderer_color": return SetRendererColor(context);
                case "get_material_info": return GetInfo(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ---------------------------------------------------------------- shared plumbing

        private static CucliCommandResult Ping()
        {
            return CucliCommandResult.Ok(new { tool = "material", action = "ping" }, "pong");
        }

        /// <summary>Normalises a material path: back-slashes, missing Assets/ prefix and missing .mat.</summary>
        private static string NormalizeMaterialPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string path = CucliAssets.NormalizeAssetPath(raw);
            if (path == null) return null;
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) path += ".mat";
            return path;
        }

        private static bool TryLoadMaterial(
            CucliArgs args,
            out string materialPath,
            out Material material,
            out CucliCommandResult failure)
        {
            materialPath = null;
            material = null;
            failure = null;

            string raw = args.GetFirstString("materialPath", "material_path", "path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                failure = CucliCommandResult.Missing("materialPath", "例如 --material-path Assets/Materials/Hero.mat 或 --path Assets/Materials/Hero.mat。");
                return false;
            }

            materialPath = NormalizeMaterialPath(raw);
            if (materialPath == null)
            {
                failure = CucliCommandResult.Invalid(
                    "materialPath", "'" + raw + "' 无法归一化成 Assets/ 下的路径。");
                return false;
            }

            material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "找不到材质：" + materialPath + "。可用 'cucli asset search --filter-type Material' 查找。",
                    new { materialPath });
                return false;
            }

            return true;
        }

        /// <summary>MCP property aliases, applied only when the shader does not declare the requested name.</summary>
        private static string ResolvePropertyName(Material material, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return requested;
            string name = requested.Trim();
            if (material.HasProperty(name)) return name;

            switch (name)
            {
                case "_Color": if (material.HasProperty("_BaseColor")) return "_BaseColor"; break;
                case "_BaseColor": if (material.HasProperty("_Color")) return "_Color"; break;
                case "_MainTex": if (material.HasProperty("_BaseMap")) return "_BaseMap"; break;
                case "_BaseMap": if (material.HasProperty("_MainTex")) return "_MainTex"; break;
                case "_Glossiness": if (material.HasProperty("_Smoothness")) return "_Smoothness"; break;
                case "_Smoothness": if (material.HasProperty("_Glossiness")) return "_Glossiness"; break;
                case "metallic": if (material.HasProperty("_Metallic")) return "_Metallic"; break;
                case "albedo":
                    if (material.HasProperty("_BaseMap")) return "_BaseMap";
                    if (material.HasProperty("_MainTex")) return "_MainTex";
                    break;
            }

            return name;
        }

        /// <summary>Names only - the caller uses this to fix a typo without paying for every value.</summary>
        private static JArray PropertyNames(Material material)
        {
            var names = new JArray();
            Shader shader = material.shader;
            if (shader == null) return names;

            int count = shader.GetPropertyCount();
            for (int index = 0; index < count; index++)
            {
                names.Add(shader.GetPropertyName(index));
            }

            return names;
        }

        private static CucliCommandResult PropertyMissing(Material material, string requested, string resolved)
        {
            JArray names = PropertyNames(material);
            return CucliCommandResult.Fail(
                CucliErrors.MemberNotFound,
                "材质 " + material.name + " 的 shader（" + (material.shader == null ? "(null)" : material.shader.name)
                + "）上没有属性 '" + requested + "'" + (resolved == requested ? "" : "（别名解析为 '" + resolved + "'）")
                + "。可用属性名：" + names.ToString(Newtonsoft.Json.Formatting.None),
                new
                {
                    material = material.name,
                    shader = material.shader == null ? null : material.shader.name,
                    requested,
                    resolved,
                    availableProperties = names
                });
        }

        private static bool TryReadColor(CucliArgs args, string name, out Color color)
        {
            JToken token = args.Token(name);
            color = Color.white;
            if (token == null || token.Type == JTokenType.Null) return false;

            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                if (array.Count < 3) return false;
                float alpha = array.Count >= 4 ? array[3].Value<float>() : 1f;
                color = new Color(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(), alpha);
                return true;
            }

            if (token.Type == JTokenType.Object)
            {
                var nested = (JObject)token;
                if (nested["r"] != null)
                {
                    var nestedArgs = new CucliArgs(nested);
                    color = new Color(
                        nestedArgs.GetFloat("r"), nestedArgs.GetFloat("g"),
                        nestedArgs.GetFloat("b"), nestedArgs.GetFloat("a", 1f));
                    return true;
                }

                JToken inner = nested["value"];
                if (inner != null)
                {
                    var wrapper = new JObject { ["color"] = inner.DeepClone() };
                    return TryReadColor(new CucliArgs(wrapper), "color", out color);
                }

                return false;
            }

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>() ?? string.Empty;
                if (text.Length > 0 && text[0] != '#') text = "#" + text;
                return ColorUtility.TryParseHtmlString(text, out color);
            }

            return false;
        }

        private static string DescribeMaterialPath(string path)
        {
            return path ?? "(未指定)";
        }

        // ---------------------------------------------------------------- create

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string raw = args.GetFirstString("materialPath", "material_path", "path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return CucliCommandResult.Missing("materialPath", "例如 --material-path Assets/Materials/Hero.mat 或 --path Assets/Materials/Hero.mat。");
            }

            string materialPath = NormalizeMaterialPath(raw);
            if (materialPath == null || !materialPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CucliCommandResult.Invalid(
                    "materialPath", "材质路径必须位于 Assets/ 之下，收到 '" + raw + "'。");
            }

            if (AssetDatabase.LoadAssetAtPath<Material>(materialPath) != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "材质已存在：" + materialPath + "。请改用 'set_material_shader_property' 修改它。",
                    new { materialPath });
            }

            string parent = CucliAssets.ParentFolder(materialPath);
            if (parent != null && !CucliAssets.EnsureFolder(parent))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "无法创建目录 " + parent + "。", new { materialPath });
            }

            // MCP defaults 'shader' to "Standard" and then adapts it to the active render pipeline,
            // so an omitted --shader must land on the pipeline's own Lit shader, not on Standard.
            string requestedShader = args.GetString("shader");
            if (string.IsNullOrWhiteSpace(requestedShader)) requestedShader = "Standard";

            Shader shader = Shader.Find(requestedShader.Trim());
            if (shader == null) shader = RenderPipelineShaders.ResolveDefault();

            if (shader == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.MemberNotFound,
                    "找不到 shader '" + requestedShader + "'，当前渲染管线也没有可用的默认 shader。"
                    + "请用 --shader 指定名称，或用 'cucli asset search --filter-type Shader' 列出工程内 shader。",
                    new { requestedShader });
            }

            var material = new Material(shader);
            material.name = CucliAssets.FileNameWithoutExtension(materialPath);

            JObject properties = args.GetObject("properties");
            if (properties == null)
            {
                string rawProperties = args.GetString("properties");
                if (!string.IsNullOrWhiteSpace(rawProperties))
                {
                    try
                    {
                        properties = CucliJson.ParseObject(rawProperties);
                    }
                    catch (Newtonsoft.Json.JsonException exception)
                    {
                        return CucliCommandResult.Invalid("properties", "properties 不是合法 JSON：" + exception.Message);
                    }
                }
            }

            var applied = new JArray();
            if (properties != null) applied = ApplyCreateProperties(material, properties, context);

            AssetDatabase.CreateAsset(material, materialPath);
            AssetDatabase.SaveAssets();
            context.AddArtifact(CucliAssets.ToAbsolute(materialPath));

            var data = new JObject
            {
                ["path"] = materialPath,
                ["guid"] = CucliAssets.AssetPathToGuid(materialPath),
                ["shader"] = shader.name,
                ["requestedShader"] = requestedShader,
                ["appliedProperties"] = applied
            };

            return CucliCommandResult.Ok(
                data,
                "已创建材质 " + materialPath + "（shader " + shader.name + "，写入属性 " + applied.Count + " 个）。");
        }

        private static JArray ApplyCreateProperties(
            Material material,
            JObject properties,
            CucliCommandContext context)
        {
            var applied = new JArray();
            var args = new CucliArgs(properties);

            string shaderName = args.GetString("shader");
            if (!string.IsNullOrWhiteSpace(shaderName))
            {
                Shader shader = Shader.Find(shaderName.Trim());
                if (shader != null)
                {
                    material.shader = shader;
                    applied.Add(new JObject { ["name"] = "shader", ["value"] = shader.name });
                }
                else
                {
                    context.Warn("找不到 shader '" + shaderName + "'，保留管线默认 shader。");
                }
            }

            JToken colorToken = properties["color"];
            if (colorToken != null)
            {
                string propertyName = null;
                JObject colorObject = colorToken as JObject;
                if (colorObject != null) propertyName = new CucliArgs(colorObject).GetString("name");

                var wrapper = new JObject { ["color"] = colorToken.DeepClone() };
                Color color;
                if (TryReadColor(new CucliArgs(wrapper), "color", out color))
                {
                    propertyName = ResolvePropertyName(material, propertyName ?? "_Color");
                    if (material.HasProperty(propertyName))
                    {
                        material.SetColor(propertyName, color);
                        applied.Add(new JObject { ["name"] = propertyName, ["value"] = "#" + ColorUtility.ToHtmlStringRGBA(color) });
                    }
                    else
                    {
                        context.Warn("shader 上没有颜色属性 '" + propertyName + "'，已跳过。");
                    }
                }
                else
                {
                    context.Warn("color 无法解析，已跳过。");
                }
            }

            JObject floatObject = properties["float"] as JObject;
            if (floatObject != null)
            {
                var floatArgs = new CucliArgs(floatObject);
                string propertyName = floatArgs.GetString("name");
                if (!string.IsNullOrWhiteSpace(propertyName))
                {
                    string resolved = ResolvePropertyName(material, propertyName);
                    if (material.HasProperty(resolved))
                    {
                        material.SetFloat(resolved, floatArgs.GetFloat("value"));
                        applied.Add(new JObject { ["name"] = resolved, ["value"] = floatArgs.GetFloat("value") });
                    }
                    else
                    {
                        context.Warn("shader 上没有 float 属性 '" + propertyName + "'，已跳过。");
                    }
                }
            }

            JObject textureObject = properties["texture"] as JObject;
            if (textureObject != null)
            {
                var textureArgs = new CucliArgs(textureObject);
                string propertyName = textureArgs.GetString("name") ?? textureArgs.GetString("Name") ?? "_BaseMap";
                string texturePath = textureArgs.GetString("path") ?? textureArgs.GetString("Path");
                if (!string.IsNullOrWhiteSpace(texturePath))
                {
                    Texture texture = CucliAssets.Load<Texture>(texturePath);
                    if (texture == null)
                    {
                        context.Warn("找不到纹理 '" + texturePath + "'，已跳过。");
                    }
                    else
                    {
                        string resolved = ResolvePropertyName(material, propertyName);
                        if (material.HasProperty(resolved))
                        {
                            material.SetTexture(resolved, texture);
                            applied.Add(new JObject { ["name"] = resolved, ["value"] = texture.name });
                        }
                        else
                        {
                            context.Warn("shader 上没有纹理属性 '" + propertyName + "'，已跳过。");
                        }
                    }
                }
            }

            foreach (JProperty property in properties.Properties())
            {
                string name = property.Name;
                if (string.Equals(name, "shader", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "color", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "float", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "texture", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string resolved = ResolvePropertyName(material, name);
                if (!material.HasProperty(resolved))
                {
                    context.Warn("shader 上没有属性 '" + name + "'，已跳过。");
                    continue;
                }

                if (SetRawProperty(material, resolved, property.Value))
                {
                    applied.Add(new JObject { ["name"] = resolved });
                }
                else
                {
                    context.Warn("属性 '" + name + "' 的值无法解析，已跳过。");
                }
            }

            return applied;
        }

        /// <summary>Writes a raw JSON value onto a material property using the setter the shader declares.</summary>
        private static bool SetRawProperty(Material material, string propertyName, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                material.SetTexture(propertyName, null);
                return true;
            }

            switch (token.Type)
            {
                case JTokenType.Boolean:
                    material.SetFloat(propertyName, token.Value<bool>() ? 1f : 0f);
                    return true;
                case JTokenType.Integer:
                    material.SetInt(propertyName, token.Value<int>());
                    return true;
                case JTokenType.Float:
                    material.SetFloat(propertyName, token.Value<float>());
                    return true;
                case JTokenType.String:
                {
                    string text = token.Value<string>() ?? string.Empty;
                    if (text.StartsWith("#", StringComparison.Ordinal))
                    {
                        Color color;
                        if (!ColorUtility.TryParseHtmlString(text, out color)) return false;
                        material.SetColor(propertyName, color);
                        return true;
                    }

                    Texture texture = CucliAssets.Load<Texture>(text);
                    if (texture != null)
                    {
                        material.SetTexture(propertyName, texture);
                        return true;
                    }

                    return false;
                }
                case JTokenType.Array:
                {
                    var array = (JArray)token;
                    if (array.Count == 4)
                    {
                        material.SetVector(propertyName, new Vector4(
                            array[0].Value<float>(), array[1].Value<float>(),
                            array[2].Value<float>(), array[3].Value<float>()));
                        return true;
                    }

                    if (array.Count == 3)
                    {
                        var wrapper = new JObject { ["color"] = array.DeepClone() };
                        Color color;
                        if (!TryReadColor(new CucliArgs(wrapper), "color", out color)) return false;
                        material.SetColor(propertyName, color);
                        return true;
                    }

                    if (array.Count == 2)
                    {
                        material.SetVector(propertyName, new Vector4(
                            array[0].Value<float>(), array[1].Value<float>(), 0f, 0f));
                        return true;
                    }

                    return false;
                }
                case JTokenType.Object:
                {
                    var nested = (JObject)token;
                    var nestedArgs = new CucliArgs(nested);
                    if (nested["x"] != null)
                    {
                        material.SetVector(propertyName, new Vector4(
                            nestedArgs.GetFloat("x"), nestedArgs.GetFloat("y"),
                            nestedArgs.GetFloat("z"), nestedArgs.GetFloat("w")));
                        return true;
                    }

                    if (nested["r"] != null)
                    {
                        material.SetColor(propertyName, new Color(
                            nestedArgs.GetFloat("r"), nestedArgs.GetFloat("g"),
                            nestedArgs.GetFloat("b"), nestedArgs.GetFloat("a", 1f)));
                        return true;
                    }

                    string find = nestedArgs.GetString("find") ?? nestedArgs.GetString("method");
                    if (!string.IsNullOrWhiteSpace(find))
                    {
                        Texture texture = CucliAssets.Load<Texture>(find);
                        if (texture == null) return false;
                        material.SetTexture(propertyName, texture);
                        return true;
                    }

                    string path = nestedArgs.GetString("path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        Texture texture = CucliAssets.Load<Texture>(path);
                        if (texture == null) return false;
                        material.SetTexture(propertyName, texture);
                        return true;
                    }

                    return false;
                }
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------- set_material_shader_property

        private static CucliCommandResult SetShaderProperty(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string materialPath;
            Material material;
            CucliCommandResult failure;
            if (!TryLoadMaterial(args, out materialPath, out material, out failure)) return failure;

            string requested = args.GetString("property");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing("property", "例如 --property _BaseColor。");
            }

            if (!args.Has("value"))
            {
                return CucliCommandResult.Missing("value", "需要给出要写入的属性值。");
            }

            string resolved = ResolvePropertyName(material, requested);
            if (!material.HasProperty(resolved))
            {
                return PropertyMissing(material, requested, resolved);
            }

            JToken token = args.Token("value");
            Undo.RecordObject(material, "cucli set material property " + resolved);

            bool textureWrite = false;
            if (!SetRawProperty(material, resolved, token))
            {
                return CucliCommandResult.Invalid(
                    "value",
                    "值 " + token.ToString(Newtonsoft.Json.Formatting.None)
                    + " 无法解析成 float / int / color / vector / texture。");
            }

            if (token.Type == JTokenType.String
                || (token.Type == JTokenType.Object && (token["path"] != null || token["find"] != null || token["method"] != null)))
            {
                textureWrite = true;
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            var data = new JObject
            {
                ["materialPath"] = materialPath,
                ["material"] = material.name,
                ["property"] = resolved,
                ["requestedProperty"] = requested,
                ["shader"] = material.shader == null ? null : material.shader.name,
                ["valueType"] = DescribeShaderPropertyType(material, resolved)
            };

            return CucliCommandResult.Ok(
                data,
                "已在 " + material.name + " 上写入" + (textureWrite ? "纹理" : "") + "属性 " + resolved + "。");
        }

        private static string DescribeShaderPropertyType(Material material, string propertyName)
        {
            Shader shader = material.shader;
            if (shader == null) return "Unknown";

            int count = shader.GetPropertyCount();
            if (count <= 0) return "Unknown";

            try
            {
                int index = shader.FindPropertyIndex(propertyName);
                if (index < 0) return "Unknown";
                return shader.GetPropertyType(index).ToString();
            }
            catch (Exception)
            {
                return "Unknown";
            }
        }

        // ---------------------------------------------------------------- set_material_color

        private static CucliCommandResult SetColor(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string materialPath;
            Material material;
            CucliCommandResult failure;
            if (!TryLoadMaterial(args, out materialPath, out material, out failure)) return failure;

            Color color;
            if (!TryReadColor(args, "color", out color))
            {
                return CucliCommandResult.Missing(
                    "color", "接受 [r,g,b(,a)]、{\"r\":..,\"g\":..,\"b\":..,\"a\":..} 或 \"#RRGGBB(AA)\"。");
            }

            string requested = args.GetString("property");
            string propertyName = ResolvePropertyName(material, string.IsNullOrWhiteSpace(requested) ? "_Color" : requested);
            if (!material.HasProperty(propertyName))
            {
                return PropertyMissing(material, requested ?? "_Color", propertyName);
            }

            Undo.RecordObject(material, "cucli set material color " + propertyName);
            material.SetColor(propertyName, color);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            var data = new JObject
            {
                ["materialPath"] = materialPath,
                ["material"] = material.name,
                ["property"] = propertyName,
                ["color"] = new JObject
                {
                    ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a
                }
            };

            return CucliCommandResult.Ok(data, "已把 " + material.name + " 的 " + propertyName + " 设为指定颜色。");
        }

        // ---------------------------------------------------------------- renderer actions

        private static bool TryResolveRenderer(
            CucliArgs args,
            out GameObject gameObject,
            out Renderer renderer,
            out CucliCommandResult failure)
        {
            gameObject = null;
            renderer = null;
            failure = null;

            string target = args.GetString("target");
            if (string.IsNullOrWhiteSpace(target))
            {
                failure = CucliCommandResult.Missing("target", "需要场景 GameObject 的名称或层级路径。");
                return false;
            }

            // Recipe from the contract: hand the original args to the resolver so searchMethod,
            // instanceId, tag, layer and component keep working.
            var resolveArgs = new JObject
            {
                ["target"] = target.Trim(),
                ["allowMultiple"] = false
            };

            string searchMethod = args.GetString("searchMethod") ?? args.GetString("search_method");
            if (!string.IsNullOrWhiteSpace(searchMethod)) resolveArgs["searchMethod"] = searchMethod.Trim();
            if (!string.IsNullOrWhiteSpace(args.GetString("instanceId"))) resolveArgs["instanceId"] = args.GetString("instanceId");
            if (!string.IsNullOrWhiteSpace(args.GetString("instance_id"))) resolveArgs["instance_id"] = args.GetString("instance_id");
            if (!string.IsNullOrWhiteSpace(args.GetString("tag"))) resolveArgs["tag"] = args.GetString("tag");
            if (!string.IsNullOrWhiteSpace(args.GetString("layer"))) resolveArgs["layer"] = args.GetString("layer");
            if (!string.IsNullOrWhiteSpace(args.GetString("component"))) resolveArgs["component"] = args.GetString("component");

            string error;
            GameObject[] matches = CucliObjectResolver.ResolveGameObjects(new CucliArgs(resolveArgs), out error, true);
            if (matches == null || matches.Length == 0)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    error ?? ("找不到目标对象 '" + target + "'。"),
                    new { target });
                return false;
            }

            gameObject = matches[0];
            renderer = gameObject.GetComponent<Renderer>();
            if (renderer == null)
            {
                renderer = gameObject.GetComponentInChildren<Renderer>();
            }

            if (renderer == null)
            {
                failure = CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "对象 '" + CucliObjectResolver.HierarchyPath(gameObject, true) + "' 上没有 Renderer。",
                    new { target, hierarchyPath = CucliObjectResolver.HierarchyPath(gameObject, true) });
                return false;
            }

            return true;
        }

        private static CucliCommandResult AssignToRenderer(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string materialPath;
            Material material;
            CucliCommandResult failure;
            if (!TryLoadMaterial(args, out materialPath, out material, out failure)) return failure;

            GameObject gameObject;
            Renderer renderer;
            if (!TryResolveRenderer(args, out gameObject, out renderer, out failure)) return failure;

            int slot = args.GetInt("slot", 0);
            Material[] materials = renderer.sharedMaterials;
            if (materials == null) materials = Array.Empty<Material>();
            if (slot < 0 || slot >= materials.Length)
            {
                return CucliCommandResult.Invalid(
                    "slot",
                    "槽位 " + slot + " 越界：对象 " + gameObject.name + " 的 Renderer 只有 " + materials.Length + " 个材质槽。");
            }

            Undo.RecordObject(renderer, "cucli assign material");
            materials[slot] = material;
            renderer.sharedMaterials = materials;
            EditorUtility.SetDirty(renderer);

            var data = new JObject
            {
                ["materialPath"] = materialPath,
                ["material"] = material.name,
                ["target"] = CucliObjectResolver.HierarchyPath(gameObject, true),
                ["slot"] = slot,
                ["slotCount"] = materials.Length
            };

            return CucliCommandResult.Ok(
                data,
                "已把材质 " + material.name + " 赋给 " + gameObject.name + " 的第 " + slot + " 个槽位。");
        }

        private static CucliCommandResult SetRendererColor(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            Color color;
            if (!TryReadColor(args, "color", out color))
            {
                return CucliCommandResult.Missing("color", "需要 [r,g,b(,a)] 或 #RRGGBB(AA)。");
            }

            GameObject gameObject;
            Renderer renderer;
            CucliCommandResult failure;
            if (!TryResolveRenderer(args, out gameObject, out renderer, out failure)) return failure;

            int slot = args.GetInt("slot", 0);
            string mode = (args.GetString("mode") ?? "property_block").Trim().ToLowerInvariant();
            if (mode != "property_block" && mode != "shared" && mode != "instance" && mode != "create_unique")
            {
                return CucliCommandResult.Invalid(
                    "mode", "未知 mode '" + mode + "'。可用：property_block / shared / instance / create_unique。");
            }

            Material[] materials = renderer.sharedMaterials;
            if (materials == null) materials = Array.Empty<Material>();
            if (slot < 0 || slot >= materials.Length)
            {
                return CucliCommandResult.Invalid(
                    "slot",
                    "槽位 " + slot + " 越界：对象 " + gameObject.name + " 的 Renderer 只有 " + materials.Length + " 个材质槽。");
            }

            Material material = materials[slot];
            if (material == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "对象 " + gameObject.name + " 的第 " + slot + " 个材质槽是空的，无法写入颜色。",
                    new { slot });
            }

            string requested = args.GetString("property");
            string propertyName = ResolvePropertyName(material, string.IsNullOrWhiteSpace(requested) ? "_Color" : requested);
            if (!material.HasProperty(propertyName))
            {
                return PropertyMissing(material, requested ?? "_Color", propertyName);
            }

            var data = new JObject
            {
                ["target"] = CucliObjectResolver.HierarchyPath(gameObject, true),
                ["slot"] = slot,
                ["mode"] = mode,
                ["property"] = propertyName,
                ["color"] = new JObject { ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a }
            };

            switch (mode)
            {
                case "property_block":
                {
                    var block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block, slot);
                    block.SetColor(propertyName, color);
                    renderer.SetPropertyBlock(block, slot);
                    EditorUtility.SetDirty(renderer);
                    return CucliCommandResult.Ok(
                        data, "已通过 MaterialPropertyBlock 设置 " + gameObject.name + " 槽位 " + slot + " 的颜色。");
                }

                case "shared":
                {
                    Undo.RecordObject(material, "cucli set renderer color (shared)");
                    material.SetColor(propertyName, color);
                    EditorUtility.SetDirty(material);
                    AssetDatabase.SaveAssets();
                    return CucliCommandResult.Ok(
                        data, "已直接修改共享材质 " + material.name + " 的颜色（会影响所有使用者）。");
                }

                case "instance":
                {
                    Undo.RecordObject(renderer, "cucli set renderer color (instance)");
                    Material instance = new Material(material);
                    instance.SetColor(propertyName, color);
                    materials[slot] = instance;
                    renderer.sharedMaterials = materials;
                    EditorUtility.SetDirty(renderer);
                    data["warning"] = "Material instance created; Undo cannot fully revert instantiation.";
                    return CucliCommandResult.Ok(data, "已创建材质实例并设置颜色（Undo 无法完全回滚实例化）。");
                }

                default:
                {
                    string scenePath = gameObject.scene.IsValid() ? gameObject.scene.path : string.Empty;
                    string folder = "Assets/Materials";
                    if (!string.IsNullOrEmpty(scenePath))
                    {
                        string sceneFolder = CucliAssets.ParentFolder(scenePath);
                        folder = (sceneFolder ?? "Assets") + "/Materials";
                    }

                    if (!CucliAssets.EnsureFolder(folder))
                    {
                        return CucliCommandResult.Fail(
                            CucliErrors.InvalidParameter, "无法创建材质目录 " + folder + "。", new { folder });
                    }

                    string safeName = gameObject.name.Replace(' ', '_');
                    string assetPath = folder + "/" + safeName + "_" + gameObject.GetInstanceID() + "_mat.mat";

                    var unique = new Material(material);
                    unique.SetColor(propertyName, color);
                    unique.name = CucliAssets.FileNameWithoutExtension(assetPath);

                    Undo.RecordObject(renderer, "cucli set renderer color (create_unique)");
                    AssetDatabase.CreateAsset(unique, assetPath);
                    AssetDatabase.SaveAssets();

                    materials[slot] = unique;
                    renderer.sharedMaterials = materials;
                    EditorUtility.SetDirty(renderer);

                    context.AddArtifact(CucliAssets.ToAbsolute(assetPath));
                    data["materialPath"] = assetPath;
                    return CucliCommandResult.Ok(
                        data, "已创建唯一材质 " + assetPath + " 并设置颜色。");
                }
            }
        }

        // ---------------------------------------------------------------- get_material_info

        private static CucliCommandResult GetInfo(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string materialPath;
            Material material;
            CucliCommandResult failure;
            if (!TryLoadMaterial(args, out materialPath, out material, out failure)) return failure;

            Shader shader = material.shader;
            bool includeValues = args.GetBool("includeValues", args.GetBool("include_values", false));
            int maxItems = Mathf.Clamp(args.GetInt("maxItems", CucliDescribe.DefaultMaxItems), 1, 5000);

            var propertyNames = new JArray();
            var keywords = new JArray();
            var properties = new JArray();

            int totalProperties = 0;
            if (shader != null)
            {
                totalProperties = shader.GetPropertyCount();
                for (int index = 0; index < totalProperties; index++)
                {
                    string name = shader.GetPropertyName(index);
                    propertyNames.Add(name);

                    var entry = new JObject
                    {
                        ["name"] = name,
                        ["type"] = shader.GetPropertyType(index).ToString(),
                        ["description"] = shader.GetPropertyDescription(index)
                    };

                    if (includeValues && properties.Count < maxItems)
                    {
                        JToken value;
                        if (TryReadPropertyValue(material, shader, index, name, out value)) entry["value"] = value;
                    }

                    properties.Add(entry);
                }
            }

            string[] keywordNames = material.shaderKeywords;
            if (keywordNames != null)
            {
                foreach (string keyword in keywordNames) keywords.Add(keyword);
            }

            var data = new JObject
            {
                ["material"] = material.name,
                ["materialPath"] = materialPath,
                ["shader"] = shader == null ? null : shader.name,
                ["propertyNames"] = propertyNames,
                ["propertyCount"] = totalProperties,
                ["keywords"] = keywords,
                ["keywordCount"] = keywords.Count,
                ["includeValues"] = includeValues,
                ["properties"] = includeValues ? properties : null,
                ["propertiesTruncated"] = includeValues && totalProperties > properties.Count
            };

            if (includeValues)
            {
                context.Warn("includeValues=true：属性值最多返回 " + maxItems + " 条（maxItems）。");
            }

            return CucliCommandResult.Ok(
                data,
                "材质 " + material.name + " 使用 shader " + (shader == null ? "(null)" : shader.name)
                + "，共 " + totalProperties + " 个属性、 " + keywords.Count + " 个关键字。");
        }

        /// <summary>Reads one shader property value, never recursing into textures or matrices.</summary>
        private static bool TryReadPropertyValue(
            Material material,
            Shader shader,
            int index,
            string name,
            out JToken value)
        {
            value = null;
            try
            {
                switch (shader.GetPropertyType(index))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                    {
                        Color color = material.GetColor(name);
                        value = new JObject
                        {
                            ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a
                        };
                        return true;
                    }

                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                    {
                        Vector4 vector = material.GetVector(name);
                        value = new JObject
                        {
                            ["x"] = vector.x, ["y"] = vector.y, ["z"] = vector.z, ["w"] = vector.w
                        };
                        return true;
                    }

                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        value = new JValue(material.GetFloat(name));
                        return true;

                    default:
                    {
                        Texture texture = material.GetTexture(name);
                        value = texture == null ? new JValue("null") : new JValue(texture.name);
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                value = new JValue("<error: " + exception.GetBaseException().Message + ">");
                return true;
            }
        }

        /// <summary>
        /// Resolves the pipeline-appropriate default shader without any compile-time URP/HDRP reference.
        /// </summary>
        internal static class RenderPipelineShaders
        {
            internal static Shader ResolveDefault()
            {
                string pipeline = string.Empty;
                try
                {
                    Object current = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                    if (current != null) pipeline = current.GetType().FullName ?? string.Empty;
                }
                catch (Exception)
                {
                    pipeline = string.Empty;
                }

                var candidates = new List<string>();
                if (pipeline.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0
                    || pipeline.IndexOf("URP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    candidates.Add("Universal Render Pipeline/Lit");
                    candidates.Add("Universal Render Pipeline/Unlit");
                }
                else if (pipeline.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0
                         || pipeline.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    candidates.Add("HDRP/Lit");
                    candidates.Add("HDRP/Unlit");
                }

                candidates.Add("Standard");
                candidates.Add("Unlit/Color");
                candidates.Add("Sprites/Default");

                foreach (string candidate in candidates)
                {
                    Shader shader = Shader.Find(candidate);
                    if (shader != null) return shader;
                }

                return Shader.Find("Diffuse");
            }
        }
    }
}
