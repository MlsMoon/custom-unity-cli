using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli asset ...</c> - the asset database surface: import, create, modify, delete,
    /// duplicate, move/rename, search, describe, folder creation and component listing.
    ///
    /// Alignment target is MCP for Unity's <c>manage_asset</c>. Two deliberate differences keep the
    /// output usable by an agent: <c>search</c> is paged through <see cref="CucliPaging"/>, and
    /// <c>get_info</c> never serialises the asset itself - it reports type, guid, path, dependency
    /// count, labels and an importer summary instead.
    /// </summary>
    internal sealed class AssetCommand : CucliCommand
    {
        public override string Name => "asset";

        public override string Summary =>
            "Work with project assets: import, create, modify, delete, duplicate, move/rename, search, describe, folders, components.";

        public override CucliActionInfo[] Actions => Declare(
            "import", "Reimport an asset from disk.",
            "create", "Create an asset (folder, material, physicsmaterial, scriptableobject, textasset, animationclip, texture2d, rendertexture, scene, json).",
            "modify", "Write serialized fields on an existing asset.",
            "delete", "Delete an asset or folder. Requires force=true.",
            "duplicate", "Copy an asset to a new path.",
            "move", "Move an asset to another folder.",
            "rename", "Rename an asset in place.",
            "search", "Page through assets filtered by folder, name, type and modification date.",
            "get_info", "Describe one asset without serialising its contents.",
            "create_folder", "Create a folder and every missing parent.",
            "get_components", "List the component types on a prefab or model asset.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Edit;

        public override bool MutatesProject => true;

        public override string[] Effects => new[] { "project.assets.read", "project.assets.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""import"", ""create"", ""modify"", ""delete"", ""duplicate"", ""move"", ""rename"",
                 ""search"", ""get_info"", ""create_folder"", ""get_components""],
      ""default"": ""import""
    },
    ""path"": { ""type"": ""string"", ""description"": ""Target asset path. For search: optional folder scope."", ""aliases"": [""assetPath"", ""asset_path""] },
    ""destination"": { ""type"": ""string"", ""description"": ""action=duplicate/move/rename: target path."", ""aliases"": [""destinationPath"", ""destination_path""] },
    ""assetType"": { ""type"": ""string"", ""aliases"": [""asset_type""], ""description"": ""action=create: folder, material, physicsmaterial, scriptableobject/so, textasset, animationclip, texture2d, rendertexture, scene, json."" },
    ""properties"": { ""type"": ""object"", ""description"": ""action=create/modify: member name to value."" },
    ""contents"": { ""type"": ""string"", ""description"": ""action=create: text payload for textasset/json."" },
    ""name"": { ""type"": ""string"", ""description"": ""action=create: file name when path is only a folder."" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=delete/import: confirm the destructive or forced operation."" },
    ""generatePreview"": { ""type"": ""boolean"", ""default"": false, ""aliases"": [""generate_preview""] },
    ""searchPattern"": { ""type"": ""string"", ""aliases"": [""search_pattern""], ""description"": ""action=search: raw AssetDatabase filter fragment."" },
    ""filterType"": { ""type"": ""string"", ""aliases"": [""filter_type""], ""description"": ""action=search: type filter, e.g. Material or t:Material."" },
    ""filterName"": { ""type"": ""string"", ""aliases"": [""filter_name"", ""nameFilter"", ""name_filter""], ""description"": ""action=search: file name substring."" },
    ""filterDateAfter"": { ""type"": ""string"", ""aliases"": [""filter_date_after""], ""description"": ""action=search: ISO 8601 lower bound on LastWriteTimeUtc."" },
    ""filterDateBefore"": { ""type"": ""string"", ""aliases"": [""filter_date_before""], ""description"": ""action=search: ISO 8601 upper bound on LastWriteTimeUtc."" },
    ""pageSize"": { ""type"": ""integer"", ""default"": 50, ""aliases"": [""page_size""] },
    ""pageNumber"": { ""type"": ""integer"", ""default"": 1, ""aliases"": [""page_number""] },
    ""cursor"": { ""type"": ""string"" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 },
    ""maxDepth"": { ""type"": ""integer"", ""default"": 4 },
    ""maxStringLength"": { ""type"": ""integer"", ""default"": 4096, ""aliases"": [""max_string_length""] }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "import");
            switch (action)
            {
                case "import": return Import(context);
                case "create": return Create(context);
                case "modify": return Modify(context);
                case "delete": return Delete(context);
                case "duplicate": return Duplicate(context);
                case "move": return MoveOrRename(context, false);
                case "rename": return MoveOrRename(context, true);
                case "search": return Search(context);
                case "get_info": return GetInfo(context);
                case "create_folder": return CreateFolder(context);
                case "get_components": return GetComponents(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Serializes one asset the way every asset action reports it: identity only, never the
        /// asset payload. Large members are deliberately absent.
        /// </summary>
        private static JObject DescribeAsset(string path)
        {
            var data = new JObject();
            if (string.IsNullOrEmpty(path))
            {
                data["path"] = string.Empty;
                data["assetType"] = "Unknown";
                return data;
            }

            bool isFolder = AssetDatabase.IsValidFolder(path);
            Object main = isFolder ? null : AssetDatabase.LoadMainAssetAtPath(path);

            string guid = AssetDatabase.AssetPathToGUID(path);
            string typeName = isFolder
                ? "Folder"
                : main == null ? "Unknown" : main.GetType().FullName;

            data["path"] = path;
            data["guid"] = string.IsNullOrEmpty(guid) ? null : guid;
            data["assetType"] = typeName;
            data["name"] = CucliAssets.FileNameWithoutExtension(path);
            data["fileName"] = CucliAssets.FileName(path);
            data["extension"] = CucliAssets.Extension(path);
            data["isFolder"] = isFolder;
            data["instanceID"] = main == null ? 0 : main.GetInstanceID();
            data["lastWriteTimeUtc"] = LastWriteUtc(path);
            return data;
        }

        private static string LastWriteUtc(string assetPath)
        {
            string absolute = CucliAssets.ToAbsolute(assetPath);
            if (absolute == null || !File.Exists(absolute)) return null;
            return File.GetLastWriteTimeUtc(absolute).ToString("o", CultureInfo.InvariantCulture);
        }

        private static string NormalizeOrNull(CucliArgs args, string name)
        {
            string raw = args.GetString(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                string snake = SnakeCase(name);
                if (!string.Equals(snake, name, StringComparison.Ordinal)) raw = args.GetString(snake);
            }

            return string.IsNullOrWhiteSpace(raw) ? null : CucliAssets.NormalizeAssetPath(raw);
        }

        private static string ReadPath(CucliArgs args)
        {
            string path = NormalizeOrNull(args, "path");
            if (path != null) return path;
            return NormalizeOrNull(args, "assetPath");
        }

        private static string SnakeCase(string name)
        {
            var builder = new StringBuilder(name.Length + 4);
            for (int index = 0; index < name.Length; index++)
            {
                char current = name[index];
                if (char.IsUpper(current))
                {
                    if (index > 0) builder.Append('_');
                    builder.Append(char.ToLowerInvariant(current));
                }
                else
                {
                    builder.Append(current);
                }
            }

            return builder.ToString();
        }

        private static JObject PropertiesOf(CucliArgs args)
        {
            JObject properties = args.GetObject("properties");
            if (properties != null) return properties;

            string raw = args.GetString("properties");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                return CucliJson.ParseObject(raw);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- actions

        private static CucliCommandResult Import(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string path = ReadPath(args);
            if (path == null) return CucliCommandResult.Missing("path", "执行 'cucli asset import --path Assets/...'。");

            if (!File.Exists(CucliAssets.ToAbsolute(path)) && !AssetDatabase.IsValidFolder(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "资产不存在：" + path + "。请检查路径是否在 Assets/ 下。");
            }

            bool force = args.GetBool("force", false);
            if (force) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            else AssetDatabase.ImportAsset(path, ImportAssetOptions.Default);
            if (force) AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            JObject properties = PropertiesOf(args);
            if (properties != null && properties.Count > 0)
            {
                context.Warn("import 不会应用 properties；请在 import 之后使用 'cucli asset modify'。");
            }

            return CucliCommandResult.Ok(
                DescribeAsset(path),
                "已重新导入资产 " + path + "。");
        }

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string requestedType = args.GetString("assetType") ?? args.GetString("asset_type");
            string rawPath = args.GetString("path");
            string rawName = args.GetString("name");

            // Allow 'cucli asset create --path Assets/Materials --name Hero.mat'.
            if (!string.IsNullOrWhiteSpace(rawPath) && !string.IsNullOrWhiteSpace(rawName)
                && string.IsNullOrEmpty(CucliAssets.Extension(rawPath)))
            {
                rawPath = rawPath.TrimEnd('/', '\\') + "/" + rawName.Trim();
            }

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return CucliCommandResult.Missing("path", "需要给出完整的资产路径，或同时给出目录 path 与 name。");
            }

            string normalized = CucliAssets.NormalizeAssetPath(rawPath);
            if (normalized == null)
            {
                return CucliCommandResult.Invalid("path", "'" + rawPath + "' 无法归一化成 Assets/ 下的路径。");
            }

            if (string.IsNullOrWhiteSpace(requestedType))
            {
                return CucliCommandResult.Missing("assetType", "例如 material / scriptableobject / folder。");
            }

            requestedType = requestedType.Trim();
            string token = requestedType.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty)
                .Replace(" ", string.Empty);
            string typeName = MapAssetType(token);
            if (typeName == null)
            {
                return CucliCommandResult.Invalid(
                    "assetType",
                    "'" + requestedType + "' 暂不支持。可用：folder, material, physicsmaterial, scriptableobject, "
                    + "textasset, animationclip, texture2d, rendertexture, scene, json。");
            }

            if (AssetDatabase.LoadMainAssetAtPath(normalized) != null
                || AssetDatabase.IsValidFolder(normalized)
                || File.Exists(CucliAssets.ToAbsolute(normalized)))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "目标已存在：" + normalized + "。请改用 'cucli asset modify'，或换一个路径。",
                    new { path = normalized });
            }

            string parent = CucliAssets.ParentFolder(normalized);
            if (parent == null || !CucliAssets.EnsureFolder(parent))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "无法创建上级目录：" + (parent ?? "(Assets 根)") + "。");
            }

            JObject properties = PropertiesOf(args);

            if (string.Equals(typeName, "Folder", StringComparison.Ordinal))
            {
                bool created = CucliAssets.EnsureFolder(normalized);
                if (!created)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "无法创建目录：" + normalized + "。");
                }

                return CucliCommandResult.Ok(DescribeAsset(normalized), "已创建目录 " + normalized + "。");
            }

            Object asset;
            string createError;
            if (!TryCreateAsset(normalized, typeName, properties, context, out asset, out createError))

            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "创建资产 " + normalized + " 失败：" + createError,
                    new { path = normalized, assetType = requestedType });
            }

            AssetDatabase.SaveAssets();
            context.AddArtifact(CucliAssets.ToAbsolute(normalized));

            var data = DescribeAsset(normalized);
            data["created"] = true;
            return CucliCommandResult.Ok(data, "已创建 " + typeName + " 资产 " + normalized + "。");
        }

        /// <summary>Maps the MCP asset-type token onto a concrete Unity type name, or null when unsupported.</summary>
        private static string MapAssetType(string token)
        {
            switch (token)
            {
                case "folder": case "directory": return "Folder";
                case "material": case "mat": return "Material";
                case "physicsmaterial": case "physicmaterial": return "PhysicMaterial";
                case "scriptableobject": case "so": case "soasset": case "asset": return "ScriptableObject";
                case "textasset": case "text": case "txt": return "TextAsset";
                case "animationclip": case "animation": case "anim": case "clip": return "AnimationClip";
                case "texture2d": case "texture": case "png": return "Texture2D";
                case "rendertexture": case "rendertarget": return "RenderTexture";
                case "scene": case "unity": return "SceneAsset";
                case "json": case "jsonasset": return "TextAsset";
                default: return null;
            }
        }

        private static bool TryCreateAsset(
            string path,
            string typeName,
            JObject properties,
            CucliCommandContext context,
            out Object asset,
            out string error)
        {
            asset = null;
            error = null;

            switch (typeName)
            {
                case "Material":
                    return TryCreateMaterial(path, properties, context, out asset, out error);

                case "PhysicMaterial":
                {
                    var material = new PhysicMaterial(CucliAssets.FileNameWithoutExtension(path));
                    ApplyPhysicMaterialProperties(material, properties, context);
                    asset = material;
                    AssetDatabase.CreateAsset(material, path);
                    return asset != null;
                }

                case "ScriptableObject":
                {
                    Type type = ResolveScriptableObjectType(properties, out error);
                    if (type == null) return false;

                    ScriptableObject instance;
                    try
                    {
                        instance = ScriptableObject.CreateInstance(type);
                    }
                    catch (Exception exception)
                    {
                        error = exception.GetBaseException().Message;
                        return false;
                    }

                    if (instance == null)
                    {
                        error = "CreateInstance(" + type.FullName + ") 返回 null。";
                        return false;
                    }

                    instance.name = CucliAssets.FileNameWithoutExtension(path);
                    JObject fields = properties == null ? null : properties["fields"] as JObject;
                    if (fields != null)
                    {
                        JArray failures;
                        ApplyMemberMap(instance, fields, context, out failures);
                        if (failures.Count > 0)
                        {
                            Object.DestroyImmediate(instance);
                            error = "字段写入失败：" + failures.ToString(Newtonsoft.Json.Formatting.None);
                            return false;
                        }
                    }

                    AssetDatabase.CreateAsset(instance, path);
                    asset = instance;
                    return asset != null;
                }

                case "AnimationClip":
                {
                    var clip = new AnimationClip();
                    clip.name = CucliAssets.FileNameWithoutExtension(path);
                    AssetDatabase.CreateAsset(clip, path);
                    asset = clip;
                    return asset != null;
                }

                case "Texture2D":
                {
                    int width = ClampDimension(properties == null ? 0 : new CucliArgs(properties).GetInt("width", 4), 4);
                    int height = ClampDimension(properties == null ? 0 : new CucliArgs(properties).GetInt("height", 4), 4);
                    var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    texture.name = CucliAssets.FileNameWithoutExtension(path);
                    Color32 fill = new Color32(0, 0, 0, 0);
                    if (properties != null && properties["color"] != null)
                    {
                        fill = ClampToColor32(new CucliArgs(properties).GetColor("color", Color.clear));
                    }

                    var pixels = new Color32[width * height];
                    for (int index = 0; index < pixels.Length; index++) pixels[index] = fill;
                    texture.SetPixels32(pixels);
                    texture.Apply();
                    AssetDatabase.CreateAsset(texture, path);
                    asset = texture;
                    return asset != null;
                }

                case "RenderTexture":
                {
                    int width = ClampDimension(properties == null ? 0 : new CucliArgs(properties).GetInt("width", 256), 256);
                    int height = ClampDimension(properties == null ? 0 : new CucliArgs(properties).GetInt("height", 256), 256);
                    var texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                    texture.name = CucliAssets.FileNameWithoutExtension(path);
                    AssetDatabase.CreateAsset(texture, path);
                    asset = texture;
                    return asset != null;
                }

                case "TextAsset":
                case "SceneAsset":
                case "Shader":
                {
                    string contents = properties == null ? null : new CucliArgs(properties).GetString("contents");
                    if (string.IsNullOrEmpty(contents)) contents = string.Empty;
                    string absolute = CucliAssets.ToAbsolute(path);
                    try
                    {
                        File.WriteAllText(absolute, contents, CucliJson.Utf8NoBom);
                    }
                    catch (Exception exception)
                    {
                        error = exception.GetBaseException().Message;
                        return false;
                    }

                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset == null)
                    {
                        error = "写入文件后 AssetDatabase 未能导入 " + path + "。";
                    }

                    return asset != null;
                }

                default:
                    error = "没有为类型 '" + typeName + "' 实现创建分支。";
                    return false;
            }
        }

        private static int ClampDimension(int value, int fallback)
        {
            if (value <= 0) value = fallback;
            return Mathf.Clamp(value, 1, 4096);
        }

        private static Color32 ClampToColor32(Color color)
        {
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.a * 255f), 0, 255));
        }

        private static bool TryCreateMaterial(
            string path,
            JObject properties,
            CucliCommandContext context,
            out Object asset,
            out string error)
        {
            asset = null;
            error = null;

            CucliArgs args = properties == null ? default(CucliArgs) : new CucliArgs(properties);
            string shaderName = args.GetString("shader");

            Shader shader = null;
            if (!string.IsNullOrWhiteSpace(shaderName)) shader = Shader.Find(shaderName.Trim());
            if (shader == null) shader = ResolveDefaultShader();
            if (shader == null)
            {
                error = string.IsNullOrWhiteSpace(shaderName)
                    ? "当前渲染管线找不到可用的默认 shader。请用 properties.shader 指定一个已安装的 shader 路径或名称。"
                    : "找不到 shader '" + shaderName + "'。请检查名称，或改用 properties.shader 指向 URP/Built-in 的 shader。";
                return false;
            }

            var material = new Material(shader);
            material.name = CucliAssets.FileNameWithoutExtension(path);

            if (properties != null)
            {
                ApplyMaterialProperties(material, properties, context);
            }

            AssetDatabase.CreateAsset(material, path);
            asset = AssetDatabase.LoadMainAssetAtPath(path);
            return asset != null;
        }

        private static Shader ResolveDefaultShader()
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

        /// <summary>
        /// Writes material members. Reserved keys (shader/color/float/texture) follow the MCP
        /// shape; anything else is treated as a shader property name and must exist on the shader.
        /// </summary>
        private static void ApplyMaterialProperties(Material material, JObject properties, CucliCommandContext context)
        {
            var args = new CucliArgs(properties);

            string shaderName = args.GetString("shader");
            if (!string.IsNullOrWhiteSpace(shaderName))
            {
                Shader shader = Shader.Find(shaderName.Trim());
                if (shader != null) material.shader = shader;
                else context.Warn("找不到 shader '" + shaderName + "'，保留原有 shader。");
            }

            JToken colorToken = properties["color"];
            if (colorToken != null)
            {
                string propertyName = null;
                JToken valueToken = colorToken;
                JObject colorObject = colorToken as JObject;
                if (colorObject != null)
                {
                    propertyName = new CucliArgs(colorObject).GetString("name");
                    valueToken = colorObject["value"] ?? colorObject["color"];
                }

                Color color;
                if (TryReadColorToken(valueToken, out color))
                {
                    propertyName = ResolvePropertyName(material, propertyName ?? "_Color");
                    material.SetColor(propertyName, color);
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
                    if (material.HasProperty(propertyName.Trim()))
                    {
                        material.SetFloat(propertyName.Trim(), floatArgs.GetFloat("value"));
                    }
                    else
                    {
                        context.Warn("材质 shader 上没有 float 属性 '" + propertyName + "'，已跳过。");
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
                        context.Warn("找不到纹理 '" + texturePath + "'，已跳过 texture 写入。");
                    }
                    else
                    {
                        propertyName = ResolvePropertyName(material, propertyName);
                        material.SetTexture(propertyName, texture);
                    }
                }
            }

            foreach (JProperty property in properties.Properties())
            {
                string name = property.Name;
                if (string.Equals(name, "shader", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "color", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "float", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "texture", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "fields", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!material.HasProperty(name))
                {
                    context.Warn("材质 shader 上没有属性 '" + name + "'，已跳过。");
                    continue;
                }

                string propertyName = ResolvePropertyName(material, name);
                JToken token = property.Value;
                if (token == null || token.Type == JTokenType.Null)
                {
                    material.SetTexture(propertyName, null);
                    continue;
                }

                if (token.Type == JTokenType.Boolean)
                {
                    material.SetFloat(propertyName, token.Value<bool>() ? 1f : 0f);
                    continue;
                }

                if (token.Type == JTokenType.Integer)
                {
                    material.SetInt(propertyName, token.Value<int>());
                    continue;
                }

                if (token.Type == JTokenType.Float)
                {
                    material.SetFloat(propertyName, token.Value<float>());
                    continue;
                }

                if (token.Type == JTokenType.String)
                {
                    string text = token.Value<string>() ?? string.Empty;
                    if (text.StartsWith("#", StringComparison.Ordinal))
                    {
                        Color color;
                        if (ColorUtility.TryParseHtmlString(text, out color)) material.SetColor(propertyName, color);
                        continue;
                    }

                    Texture texture = CucliAssets.Load<Texture>(text);
                    if (texture != null) material.SetTexture(propertyName, texture);
                    else material.SetFloat(propertyName, 0f);
                    continue;
                }

                if (token.Type == JTokenType.Array)
                {
                    var array = (JArray)token;
                    if (array.Count == 4)
                    {
                        material.SetVector(propertyName, new Vector4(
                            array[0].Value<float>(), array[1].Value<float>(),
                            array[2].Value<float>(), array[3].Value<float>()));
                    }
                    else if (array.Count == 3)
                    {
                        Color color;
                        if (TryReadColorToken(token, out color)) material.SetColor(propertyName, color);
                    }
                    else if (array.Count == 2)
                    {
                        material.SetVector(propertyName, new Vector4(
                            array[0].Value<float>(), array[1].Value<float>(), 0f, 0f));
                    }

                    continue;
                }

                var nested = token as JObject;
                if (nested != null)
                {
                    var nestedArgs = new CucliArgs(nested);
                    if (nested["x"] != null)
                    {
                        material.SetVector(propertyName, new Vector4(
                            nestedArgs.GetFloat("x"), nestedArgs.GetFloat("y"),
                            nestedArgs.GetFloat("z"), nestedArgs.GetFloat("w")));
                    }
                    else if (nested["r"] != null)
                    {
                        material.SetColor(propertyName, new Color(
                            nestedArgs.GetFloat("r"), nestedArgs.GetFloat("g"),
                            nestedArgs.GetFloat("b"), nestedArgs.GetFloat("a", 1f)));
                    }
                }
            }
        }

        private static bool TryReadColorToken(JToken token, out Color color)
        {
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
                var args = new CucliArgs((JObject)token);
                if (token["r"] != null)
                {
                    color = new Color(args.GetFloat("r"), args.GetFloat("g"), args.GetFloat("b"), args.GetFloat("a", 1f));
                    return true;
                }

                JToken inner = token["value"];
                return inner != null && TryReadColorToken(inner, out color);
            }

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>() ?? string.Empty;
                if (text.Length > 0 && text[0] != '#') text = "#" + text;
                return ColorUtility.TryParseHtmlString(text, out color);
            }

            return false;
        }

        /// <summary>Maps MCP's property aliases onto whatever the assigned shader actually declares.</summary>
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

        private static void ApplyPhysicMaterialProperties(
            PhysicMaterial material,
            JObject properties,
            CucliCommandContext context)
        {
            if (properties == null) return;

            var args = new CucliArgs(properties);
            material.dynamicFriction = args.GetFloat("dynamicFriction", material.dynamicFriction);
            material.staticFriction = args.GetFloat("staticFriction", material.staticFriction);
            material.bounciness = args.GetFloat("bounciness", material.bounciness);

            string frictionCombine = args.GetString("frictionCombine");
            if (!string.IsNullOrWhiteSpace(frictionCombine))
            {
                PhysicMaterialCombine combine;
                if (TryParseCombine(frictionCombine, out combine)) material.frictionCombine = combine;
                else context.Warn("frictionCombine 取值 '" + frictionCombine + "' 无法识别，保持原值。");
            }

            string bounceCombine = args.GetString("bounceCombine");
            if (!string.IsNullOrWhiteSpace(bounceCombine))
            {
                PhysicMaterialCombine combine;
                if (TryParseCombine(bounceCombine, out combine)) material.bounceCombine = combine;
                else context.Warn("bounceCombine 取值 '" + bounceCombine + "' 无法识别，保持原值。");
            }
        }

        private static bool TryParseCombine(string text, out PhysicMaterialCombine combine)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "ave": case "average": combine = PhysicMaterialCombine.Average; return true;
                case "mul": case "mult": case "multiply": combine = PhysicMaterialCombine.Multiply; return true;
                case "min": case "minimum": combine = PhysicMaterialCombine.Minimum; return true;
                case "max": case "maximum": combine = PhysicMaterialCombine.Maximum; return true;
                default: combine = PhysicMaterialCombine.Average; return false;
            }
        }

        private static Type ResolveScriptableObjectType(JObject properties, out string error)
        {
            error = null;
            string typeName = properties == null ? null : new CucliArgs(properties).GetString("typeName");
            if (string.IsNullOrWhiteSpace(typeName))
            {
                typeName = properties == null ? null : new CucliArgs(properties).GetString("type_name");
            }

            if (string.IsNullOrWhiteSpace(typeName))
            {
                error = "创建 ScriptableObject 需要 properties.typeName，例如 {\"typeName\":\"MyConfig\"}。";
                return null;
            }

            Type type = CucliReflection.FindType(typeName.Trim(), out error);
            if (type == null) return null;

            if (!typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract)
            {
                error = "类型 '" + type.FullName + "' 不是可实例化的 ScriptableObject。";
                return null;
            }

            return type;
        }

        private static CucliCommandResult Modify(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string path = ReadPath(args);
            if (path == null) return CucliCommandResult.Missing("path", "执行 'cucli asset modify --path Assets/... --properties {...}'。");

            JObject properties = PropertiesOf(args);
            if (properties == null || properties.Count == 0)
            {
                return CucliCommandResult.Missing("properties", "modify 需要非空的 properties 对象。");
            }

            if (AssetDatabase.IsValidFolder(path))
            {
                return CucliCommandResult.Invalid("path", "'" + path + "' 是目录，无法 modify。请对资产文件使用该 action。");
            }

            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "找不到资产：" + path + "。");
            }

            var importer = AssetImporter.GetAtPath(path);
            Object target = importer != null && importer is TextureImporter ? importer : asset;

            if (!ReferenceEquals(target, asset))
            {
                // Importer settings need save+reimport, not the plain serialized write path.
                JArray importerFailures;
                JObject changed = ApplyMemberMap(target, properties, context, out importerFailures);

                var textureImporter = (TextureImporter)target;
                EditorUtility.SetDirty(textureImporter);
                textureImporter.SaveAndReimport();

                if (importerFailures.Count > 0)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.InvalidParameter,
                        "部分成员写入失败：" + importerFailures.ToString(Newtonsoft.Json.Formatting.None),
                        new { path, changed, failed = importerFailures });
                }

                AssetDatabase.SaveAssets();
                var importerData = DescribeAsset(path);
                importerData["changed"] = changed;
                return CucliCommandResult.Ok(importerData, "已更新 " + path + " 的导入设置。");
            }

            var isPrefabOrGameObject = asset is GameObject;

            JArray failures;
            JObject changedMembers = ApplyMemberMap(asset, properties, context, out failures);
            if (failures.Count > 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "部分成员写入失败：" + failures.ToString(Newtonsoft.Json.Formatting.None),
                    new { path, changed = changedMembers, failed = failures, isPrefab = isPrefabOrGameObject });
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            var data = DescribeAsset(path);
            data["changed"] = changedMembers;
            return CucliCommandResult.Ok(data, "已修改资产 " + path + " 的 " + changedMembers.Count + " 个成员。");
        }

        /// <summary>
        /// Writes a member map onto one object: serialized fields first (Undo + ApplyModifiedProperties),
        /// public reflection as the fallback. Failures are reported, never thrown away.
        /// </summary>
        private static JObject ApplyMemberMap(
            Object target,
            JObject map,
            CucliCommandContext context,
            out JArray failures)
        {
            var changed = new JObject();
            failures = new JArray();

            foreach (JProperty property in map.Properties())
            {
                context.ThrowIfCancelled();

                if (string.Equals(property.Name, "fields", StringComparison.OrdinalIgnoreCase)
                    && property.Value.Type == JTokenType.Object)
                {
                    JArray nestedFailures;
                    JObject nested = ApplyMemberMap(target, (JObject)property.Value, context, out nestedFailures);
                    changed[property.Name] = nested;
                    foreach (JToken failure in nestedFailures) failures.Add(failure);
                    continue;
                }

                string error;
                bool handled;
                bool ok;
                try
                {
                    ok = CucliSerialized.TrySet(
                        target, property.Name, property.Value, "cucli asset modify " + property.Name,
                        out error, out handled);
                }
                catch (Exception exception)
                {
                    error = exception.GetBaseException().Message;
                    handled = false;
                    ok = false;
                }

                if (ok)
                {
                    changed[property.Name] = property.Value.DeepClone();
                    continue;
                }

                if (handled)
                {
                    failures.Add(new JObject
                    {
                        ["member"] = property.Name,
                        ["error"] = error ?? "Unity 拒绝了该值。"
                    });
                    continue;
                }

                string reflectionError;
                try
                {
                    if (CucliReflection.TrySetPath(target, property.Name, property.Value, out reflectionError))
                    {
                        EditorUtility.SetDirty(target);
                        changed[property.Name] = property.Value.DeepClone();
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    reflectionError = exception.GetBaseException().Message;
                }

                failures.Add(new JObject
                {
                    ["member"] = property.Name,
                    ["error"] = reflectionError ?? "成员不存在或不可写。"
                });
            }

            return changed;
        }

        private static CucliCommandResult Delete(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string path = ReadPath(args);
            if (path == null) return CucliCommandResult.Missing("path", "执行 'cucli asset delete --path Assets/... --force'。");

            bool isFolder = AssetDatabase.IsValidFolder(path);
            if (!isFolder && AssetDatabase.LoadMainAssetAtPath(path) == null
                && !File.Exists(CucliAssets.ToAbsolute(path)))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, "找不到要删除的资产：" + path + "。");
            }

            if (!args.GetBool("force", false))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "删除 " + path + " 是不可逆操作。确认后请加 --force（或 force: true）重新执行。",
                    new { path, isFolder });
            }

            bool deleted = AssetDatabase.DeleteAsset(path);
            if (!deleted)
            {
                string absolute = CucliAssets.ToAbsolute(path);
                try
                {
                    if (isFolder) Directory.Delete(absolute, true);
                    else File.Delete(absolute);
                    AssetDatabase.Refresh();
                    deleted = true;
                }
                catch (Exception exception)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "删除 " + path + " 失败（文件可能被占用）：" + exception.GetBaseException().Message,
                        new { path });
                }
            }

            return CucliCommandResult.Ok(new { path, isFolder, deleted = true }, "已删除 " + path + "。");
        }

        private static CucliCommandResult Duplicate(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string path = ReadPath(args);
            if (path == null) return CucliCommandResult.Missing("path");

            if (AssetDatabase.LoadMainAssetAtPath(path) == null && !AssetDatabase.IsValidFolder(path))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, "找不到源资产：" + path + "。");
            }

            string rawDestination = args.GetString("destination") ?? args.GetString("destinationPath")
                ?? args.GetString("destination_path");
            string destination = string.IsNullOrWhiteSpace(rawDestination)
                ? CucliAssets.GenerateUniquePath(path)
                : CucliAssets.NormalizeAssetPath(rawDestination);

            if (destination == null)
            {
                return CucliCommandResult.Invalid("destination", "目标路径无法归一化。");
            }

            if (AssetDatabase.LoadMainAssetAtPath(destination) != null || AssetDatabase.IsValidFolder(destination))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "目标已存在：" + destination + "。省略 destination 可让工具自动生成唯一路径。",
                    new { destination });
            }

            string parent = CucliAssets.ParentFolder(destination);
            if (parent != null) CucliAssets.EnsureFolder(parent);

            if (!AssetDatabase.CopyAsset(path, destination))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "复制 " + path + " 到 " + destination + " 失败。",
                    new { path, destination });
            }

            AssetDatabase.SaveAssets();
            context.AddArtifact(CucliAssets.ToAbsolute(destination));

            var data = DescribeAsset(destination);
            data["sourcePath"] = path;
            data["duplicated"] = true;
            return CucliCommandResult.Ok(data, "已复制 " + path + " 到 " + destination + "。");
        }

        private static CucliCommandResult MoveOrRename(CucliCommandContext context, bool rename)
        {
            CucliArgs args = context.Input;
            string path = ReadPath(args);
            if (path == null) return CucliCommandResult.Missing("path");

            string rawDestination = args.GetString("destination") ?? args.GetString("destinationPath")
                ?? args.GetString("destination_path") ?? args.GetString("newName")
                ?? args.GetString("new_name");
            if (string.IsNullOrWhiteSpace(rawDestination))
            {
                return CucliCommandResult.Missing(
                    "destination",
                    rename
                        ? "rename 需要 destination，可以是新文件名或完整新路径。"
                        : "move 需要 destination（目标路径）。");
            }

            if (AssetDatabase.LoadMainAssetAtPath(path) == null && !AssetDatabase.IsValidFolder(path))
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, "找不到源资产：" + path + "。");
            }

            string destination;
            if (rename && rawDestination.IndexOf('/') < 0 && rawDestination.IndexOf('\\') < 0)
            {
                string parent = CucliAssets.ParentFolder(path);
                if (parent == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "无法确定 " + path + " 的父目录，请直接给出完整 destination 路径。");
                }

                string fileName = rawDestination.Trim();
                if (string.IsNullOrEmpty(CucliAssets.Extension(fileName)))
                {
                    fileName += CucliAssets.Extension(path);
                }

                destination = parent + "/" + fileName;
            }
            else
            {
                destination = CucliAssets.NormalizeAssetPath(rawDestination);
            }

            if (destination == null)
            {
                return CucliCommandResult.Invalid("destination", "'" + rawDestination + "' 无法归一化为 Assets/ 下的路径。");
            }

            if (string.Equals(destination, path, StringComparison.Ordinal))
            {
                return CucliCommandResult.Ok(
                    new { path, destination, moved = false }, "源路径与目标路径相同，未做任何移动。");
            }

            if (AssetDatabase.LoadMainAssetAtPath(destination) != null || AssetDatabase.IsValidFolder(destination))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists, "目标已存在：" + destination + "。", new { destination });
            }

            string validation = AssetDatabase.ValidateMoveAsset(path, destination);
            if (!string.IsNullOrEmpty(validation))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Unity 拒绝移动 " + path + " -> " + destination + "：" + validation,
                    new { path, destination, unityError = validation });
            }

            string error = AssetDatabase.MoveAsset(path, destination);
            if (!string.IsNullOrEmpty(error))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "移动失败：" + error,
                    new { path, destination, unityError = error });
            }

            AssetDatabase.SaveAssets();
            context.AddArtifact(CucliAssets.ToAbsolute(destination));

            var data = DescribeAsset(destination);
            data["sourcePath"] = path;
            data["moved"] = true;
            return CucliCommandResult.Ok(
                data,
                (rename ? "已重命名 " : "已移动 ") + path + " -> " + destination + "。");
        }

        private static CucliCommandResult Search(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string folder = ReadPath(args);
            if (folder != null && !AssetDatabase.IsValidFolder(folder))
            {
                context.Warn("'" + folder + "' 不是有效目录，已改为全工程搜索。");
                folder = null;
            }

            string filterName = args.GetString("filterName") ?? args.GetString("filter_name")
                ?? args.GetString("nameFilter") ?? args.GetString("name_filter");
            string filterType = args.GetString("filterType") ?? args.GetString("filter_type");
            string searchPattern = args.GetString("searchPattern") ?? args.GetString("search_pattern");

            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(searchPattern)) filters.Add(searchPattern.Trim());
            if (!string.IsNullOrWhiteSpace(filterType))
            {
                string type = filterType.Trim();
                filters.Add(type.StartsWith("t:", StringComparison.OrdinalIgnoreCase) ? type : "t:" + type);
            }

            if (!string.IsNullOrWhiteSpace(filterName)) filters.Add(filterName.Trim());

            string[] searchFolders = null;
            if (folder != null) searchFolders = new[] { folder };

            string query = string.Join(" ", filters);
            string[] guids = searchFolders == null
                ? AssetDatabase.FindAssets(query)
                : AssetDatabase.FindAssets(query, searchFolders);

            DateTime after;
            bool hasAfter = TryParseDate(
                args.GetString("filterDateAfter") ?? args.GetString("filter_date_after"), out after);

            DateTime before;
            bool hasBefore = TryParseDate(
                args.GetString("filterDateBefore") ?? args.GetString("filter_date_before"), out before);

            var matches = new List<string>();
            for (int index = 0; index < guids.Length; index++)
            {
                context.ThrowIfCancelled();

                string path = AssetDatabase.GUIDToAssetPath(guids[index]);
                if (string.IsNullOrEmpty(path)) continue;

                if (hasAfter || hasBefore)
                {
                    DateTime written;
                    if (!TryLastWrite(path, out written)) continue;
                    if (hasAfter && written <= after) continue;
                    if (hasBefore && written >= before) continue;
                }

                matches.Add(path);
            }

            CucliPage<string> page = CucliPaging.Slice(matches, args, 50);
            var items = new JArray();
            foreach (string path in page.Items) items.Add(DescribeAsset(path));

            var payload = new JObject
            {
                ["assets"] = items,
                ["totalAssets"] = page.Total,
                ["pageNumber"] = page.PageNumber,
                ["pageSize"] = page.PageSize,
                ["query"] = query,
                ["folder"] = folder
            };

            JObject envelope = CucliPaging.Envelope(page, items, payload);
            return CucliCommandResult.Ok(
                envelope,
                "在 " + page.Total + " 个匹配资产中返回第 " + page.PageNumber + " 页（" + page.Items.Count + " 条）。");
        }

        private static bool TryParseDate(string text, out DateTime value)
        {
            value = default(DateTime);
            if (string.IsNullOrWhiteSpace(text)) return false;
            return DateTime.TryParse(
                text.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out value);
        }

        private static bool TryLastWrite(string assetPath, out DateTime value)
        {
            value = default(DateTime);
            string absolute = CucliAssets.ToAbsolute(assetPath);
            if (absolute == null || !File.Exists(absolute)) return false;
            value = File.GetLastWriteTimeUtc(absolute);
            return true;
        }

        private static CucliCommandResult GetInfo(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string path = ReadPath(args);
            if (path == null) return CucliCommandResult.Missing("path", "执行 'cucli asset get_info --path Assets/...'。");

            bool isFolder = AssetDatabase.IsValidFolder(path);
            Object main = isFolder ? null : AssetDatabase.LoadMainAssetAtPath(path);
            if (!isFolder && main == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "找不到资产：" + path + "。", new { path });
            }

            JObject data = DescribeAsset(path);
            data["subAssetCount"] = 0;
            data["labels"] = new JArray();
            data["dependentCount"] = 0;
            data["dependencies"] = new JArray();
            data["referencedByCount"] = 0;
            data["importer"] = JValue.CreateNull();

            if (isFolder)
            {
                string absolute = CucliAssets.ToAbsolute(path);
                int childFiles = 0;
                int childFolders = 0;
                if (absolute != null && Directory.Exists(absolute))
                {
                    childFiles = Directory.GetFiles(absolute).Length;
                    childFolders = Directory.GetDirectories(absolute).Length;
                }

                data["childFiles"] = childFiles;
                data["childFolders"] = childFolders;
                return CucliCommandResult.Ok(data, "目录 " + path + " 包含 " + childFiles + " 个文件、" + childFolders + " 个子目录。");
            }

            Object[] all = AssetDatabase.LoadAllAssetsAtPath(path);
            data["subAssetCount"] = all == null ? 0 : Math.Max(0, all.Length - 1);

            string[] labels = AssetDatabase.GetLabels(main);
            var labelArray = new JArray();
            if (labels != null)
            {
                foreach (string label in labels) labelArray.Add(label);
            }

            data["labels"] = labelArray;

            string[] dependencies = AssetDatabase.GetDependencies(path, false);
            var dependencyArray = new JArray();
            int dependencyCount = 0;
            if (dependencies != null)
            {
                foreach (string dependency in dependencies)
                {
                    if (string.Equals(dependency, path, StringComparison.Ordinal)) continue;
                    dependencyCount++;
                    if (dependencyArray.Count < 200) dependencyArray.Add(dependency);
                }
            }

            data["dependentCount"] = dependencyCount;
            data["dependencies"] = dependencyArray;
            data["referencedByCount"] = AssetDatabase.GetDependencies(path, true).Length - 1;

            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer != null)
            {
                var summary = new JObject
                {
                    ["type"] = importer.GetType().FullName,
                    ["assetPath"] = importer.assetPath,
                    ["userData"] = importer.userData
                };

                var textureImporter = importer as TextureImporter;
                if (textureImporter != null)
                {
                    summary["textureType"] = textureImporter.textureType.ToString();
                    summary["textureShape"] = textureImporter.textureShape.ToString();
                    summary["sRGBTexture"] = textureImporter.sRGBTexture;
                    summary["alphaIsTransparency"] = textureImporter.alphaIsTransparency;
                    summary["isReadable"] = textureImporter.isReadable;
                    summary["mipmapEnabled"] = textureImporter.mipmapEnabled;
                    summary["wrapMode"] = textureImporter.wrapMode.ToString();
                    summary["filterMode"] = textureImporter.filterMode.ToString();
                    summary["anisoLevel"] = textureImporter.anisoLevel;
                    summary["maxTextureSize"] = textureImporter.maxTextureSize;
                    summary["textureCompression"] = textureImporter.textureCompression.ToString();
                    summary["spriteImportMode"] = textureImporter.spriteImportMode.ToString();
                    summary["spritePixelsPerUnit"] = textureImporter.spritePixelsPerUnit;
                }

                var modelImporter = importer as ModelImporter;
                if (modelImporter != null)
                {
                    summary["meshCompression"] = modelImporter.meshCompression.ToString();
                    summary["importAnimation"] = modelImporter.importAnimation;
                    // importMaterials was removed in Unity 2022; materialImportMode replaces it.
                    summary["materialImportMode"] = modelImporter.materialImportMode.ToString();
                    summary["isReadable"] = modelImporter.isReadable;
                    summary["globalScale"] = modelImporter.globalScale;
                }

                var audioImporter = importer as AudioImporter;
                if (audioImporter != null)
                {
                    summary["forceToMono"] = audioImporter.forceToMono;
                    summary["loadInBackground"] = audioImporter.loadInBackground;
                    // preloadAudioData moved to the per-platform sample settings.
                    summary["preloadAudioData"] = audioImporter.defaultSampleSettings.preloadAudioData;
                }

                data["importer"] = summary;
            }

            string guidText = data["guid"] == null || data["guid"].Type == JTokenType.Null
                ? "(无)"
                : data["guid"].Value<string>();
            string typeText = data["assetType"] == null ? "Unknown" : data["assetType"].Value<string>();
            return CucliCommandResult.Ok(
                data,
                "资产 " + path + "：类型 " + typeText + "，GUID " + guidText
                + "，依赖 " + dependencyCount + " 个，Labels " + labelArray.Count + " 个。");
        }

        private static CucliCommandResult CreateFolder(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string path = ReadPath(args);
            if (path == null) return CucliCommandResult.Missing("path");

            if (AssetDatabase.IsValidFolder(path))
            {
                return CucliCommandResult.Ok(
                    DescribeAsset(path), "目录已存在：" + path + "。");
            }

            if (!CucliAssets.EnsureFolder(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "无法创建目录 " + path + "。请确认它在 Assets/ 下，且每一级名称都合法。",
                    new { path });
            }

            return CucliCommandResult.Ok(DescribeAsset(path), "已创建目录 " + path + "。");
        }

        private static CucliCommandResult GetComponents(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string path = ReadPath(args);
            if (path == null) return CucliCommandResult.Missing("path");

            Object main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null)
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, "找不到资产：" + path + "。");
            }

            var gameObject = main as GameObject;
            if (gameObject == null)
            {
                var component = main as Component;
                if (component != null) gameObject = component.gameObject;
            }

            if (gameObject == null)
            {
                return CucliCommandResult.Invalid(
                    "path",
                    "资产 " + path + " 的主对象是 " + main.GetType().Name + "，不是 GameObject/Prefab，无法列出组件。");
            }

            var components = new JArray();
            Component[] all = gameObject.GetComponents<Component>();
            for (int index = 0; index < all.Length; index++)
            {
                Component item = all[index];
                if (item == null)
                {
                    components.Add(new JObject
                    {
                        ["typeName"] = "(missing script)",
                        ["instanceID"] = 0
                    });
                    continue;
                }

                components.Add(new JObject
                {
                    ["typeName"] = item.GetType().FullName,
                    ["instanceID"] = item.GetInstanceID(),
                    ["enabled"] = item is Behaviour ? ((Behaviour)item).enabled : true
                });
            }

            var data = DescribeAsset(path);
            data["gameObjectName"] = gameObject.name;
            data["componentCount"] = components.Count;
            data["components"] = components;

            return CucliCommandResult.Ok(
                data,
                path + " 上有 " + components.Count + " 个组件。");
        }
    }
}
