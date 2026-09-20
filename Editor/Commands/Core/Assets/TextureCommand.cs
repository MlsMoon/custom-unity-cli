using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
// Seeded procedural noise needs a deterministic PRNG with a constructor, which is System.Random.
// Unity's intellisense-free ambiguity here has bitten this file once already.
using Random = System.Random;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli texture ...</c> - generate and edit texture assets: solid fills, sprite creation,
    /// patterns, gradients, noise, pixel-region edits and TextureImporter settings.
    ///
    /// Alignment target is MCP for Unity's <c>manage_texture</c>. Pixels are produced in memory with
    /// <see cref="Texture2D.SetPixels"/>, encoded with <c>EncodeToPNG</c>, written to disk and then
    /// imported through <see cref="AssetDatabase.ImportAsset"/> before importer settings are applied.
    /// </summary>
    internal sealed class TextureCommand : CucliCommand
    {
        private const int MaxDimension = 4096;
        private const long MaxPixelWork = 4000000L;

        public override string Name => "texture";

        public override string Summary =>
            "Create and edit texture assets: fill, sprite, modify, delete, pattern, gradient, noise and import settings.";

        public override CucliActionInfo[] Actions => Declare(
            "create", "Create a texture from a fill colour, a pattern, explicit pixels or an image file.",
            "create_sprite", "Create a texture and configure it as a Sprite.",
            "modify", "Edit a pixel region and/or change the import settings of an existing texture.",
            "delete", "Delete a texture asset. Requires force=true.",
            "apply_pattern", "Rewrite a texture with a checker / stripes / grid / dots / brick pattern.",
            "apply_gradient", "Rewrite a texture with a linear or radial gradient.",
            "apply_noise", "Rewrite a texture with perlin or value noise.",
            "set_import_settings", "Write TextureImporter settings on an existing texture.");

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
      ""enum"": [""create"", ""create_sprite"", ""modify"", ""delete"", ""apply_pattern"",
                 ""apply_gradient"", ""apply_noise"", ""set_import_settings""],
      ""default"": ""create""
    },
    ""path"": { ""type"": ""string"", ""description"": ""资产路径，会自动补 Assets/ 前缀。"" },
    ""width"": { ""type"": ""integer"", ""default"": 64, ""maximum"": 4096 },
    ""height"": { ""type"": ""integer"", ""default"": 64, ""maximum"": 4096 },
    ""imagePath"": { ""type"": ""string"", ""aliases"": [""image_path""], ""description"": ""从磁盘图片加载（绝对路径或相对工程根）。"" },
    ""fillColor"": { ""description"": ""底色：[r,g,b(,a)]，0-255 整数或 0-1 浮点。"" },
    ""pattern"": { ""type"": ""string"", ""description"": ""checker / checkerboard / stripes / stripes_v / stripes_h / stripes_diag / dots / grid / brick。"" },
    ""patternSize"": { ""type"": ""integer"", ""default"": 8, ""aliases"": [""pattern_size""] },
    ""palette"": { ""type"": ""array"", ""items"": { ""type"": ""array"" }, ""description"": ""调色板，每项 [r,g,b(,a)]。"" },
    ""pixels"": { ""description"": ""整图逐像素：颜色数组，或以 base64: 前缀给出的原始 RGBA32 字节。"" },
    ""gradientType"": { ""type"": ""string"", ""default"": ""linear"", ""aliases"": [""gradient_type""], ""description"": ""linear 或 radial。"" },
    ""gradientAngle"": { ""type"": ""number"", ""default"": 0, ""aliases"": [""gradient_angle""], ""description"": ""linear 渐变角度（度）。"" },
    ""gradient"": { ""type"": ""object"", ""description"": ""{colorKeys|keys|colors+stops, alphaKeys, mode}。"" },
    ""noiseType"": { ""type"": ""string"", ""default"": ""perlin"", ""aliases"": [""noise_type""], ""description"": ""perlin 或 value。"" },
    ""noiseScale"": { ""type"": ""number"", ""default"": 0.1, ""aliases"": [""noise_scale""], ""description"": ""Perlin 采样缩放。"" },
    ""octaves"": { ""type"": ""integer"", ""default"": 1 },
    ""seed"": { ""type"": ""integer"", ""default"": 0 },
    ""strength"": { ""type"": ""number"", ""default"": 1.0, ""description"": ""噪声对比强度。"" },
    ""importSettings"": { ""type"": ""object"", ""aliases"": [""import_settings""] },
    ""filterMode"": { ""type"": ""string"", ""aliases"": [""filter_mode""], ""description"": ""action=set_import_settings：简写，写入 importSettings.filterMode。"" },
    ""spriteSettings"": { ""description"": ""{pivot, pixelsPerUnit}；旧版 Sprite 设置入口。"" },
    ""asSprite"": { ""type"": ""boolean"", ""default"": false, ""aliases"": [""as_sprite""] },
    ""setPixels"": { ""type"": ""object"", ""aliases"": [""set_pixels""], ""description"": ""action=modify: {x,y,width,height,color|pixels}。"" },
    ""force"": { ""type"": ""boolean"", ""default"": false, ""description"": ""action=delete: 必须显式确认。"" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "create");
            switch (action)
            {
                case "create": return Create(context, false);
                case "create_sprite": return Create(context, true);
                case "modify": return Modify(context);
                case "delete": return Delete(context);
                case "apply_pattern": return ApplyPattern(context);
                case "apply_gradient": return ApplyGradient(context);
                case "apply_noise": return ApplyNoise(context);
                case "set_import_settings": return SetImportSettings(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        // ---------------------------------------------------------------- shared plumbing

        private static string RequireAssetPath(CucliArgs args, out string error)
        {
            error = null;
            string raw = args.GetString("path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "缺少 'path'：需要资产路径（可省略 Assets/ 前缀）。";
                return null;
            }

            string normalized = CucliAssets.NormalizeAssetPath(raw);
            if (normalized == null || !normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                error = "路径 '" + raw + "' 必须位于 Assets/ 之下。";
                return null;
            }

            if (string.IsNullOrEmpty(CucliAssets.Extension(normalized))) normalized += ".png";
            return normalized;
        }

        /// <summary>Dimensions are hard-capped: a silently huge texture would stall the Editor.</summary>
        private static bool TryResolveSize(CucliArgs args, out int width, out int height, out string error)
        {
            width = args.GetInt("width", 64);
            height = args.GetInt("height", 64);
            error = null;

            if (width <= 0 || height <= 0)
            {
                error = "width/height 必须为正整数（收到 " + width + "x" + height + "）。";
                return false;
            }

            if (width > MaxDimension || height > MaxDimension)
            {
                error = "尺寸上限为 " + MaxDimension + "，收到 " + width + "x" + height + "。";
                return false;
            }

            return true;
        }

        private static bool TryParseColorToken(JToken token, out Color32 color, out string error)
        {
            color = new Color32(255, 255, 255, 255);
            error = null;

            if (token == null || token.Type == JTokenType.Null) return false;

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>() ?? string.Empty;
                if (text.Length > 0 && text[0] != '#') text = "#" + text;
                Color parsed;
                if (!ColorUtility.TryParseHtmlString(text, out parsed))
                {
                    error = "颜色字符串 '" + text + "' 无法解析。";
                    return false;
                }

                color = ToColor32(parsed);
                return true;
            }

            var array = token as JArray;
            if (array == null || array.Count < 3)
            {
                error = "颜色需要 [r,g,b] 或 [r,g,b,a]。";
                return false;
            }

            // Float channels are read as 0-1; integer channels as 0-255. A missing alpha is opaque.
            bool hasAlpha = array.Count >= 4;
            bool normalized = false;
            for (int index = 0; index < array.Count && index < 4; index++)
            {
                if (array[index].Type == JTokenType.Float)
                {
                    normalized = true;
                    break;
                }
            }

            float red = array[0].Value<float>();
            float green = array[1].Value<float>();
            float blue = array[2].Value<float>();
            float alpha = hasAlpha ? array[3].Value<float>() : (normalized ? 1f : 255f);

            if (normalized)
            {
                return FinishColor(
                    Mathf.Clamp01(red) * 255f,
                    Mathf.Clamp01(green) * 255f,
                    Mathf.Clamp01(blue) * 255f,
                    Mathf.Clamp01(alpha) * 255f,
                    out color,
                    out error);
            }

            return FinishColor(
                Mathf.Clamp(red, 0f, 255f),
                Mathf.Clamp(green, 0f, 255f),
                Mathf.Clamp(blue, 0f, 255f),
                Mathf.Clamp(alpha, 0f, 255f),
                out color,
                out error);
        }

        private static bool FinishColor(
            float red,
            float green,
            float blue,
            float alpha,
            out Color32 color,
            out string error)
        {
            error = null;
            color = new Color32(
                (byte)Mathf.RoundToInt(red),
                (byte)Mathf.RoundToInt(green),
                (byte)Mathf.RoundToInt(blue),
                (byte)Mathf.RoundToInt(alpha));
            return true;
        }

        private static Color32 ToColor32(Color color)
        {
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.a * 255f), 0, 255));
        }

        private static Color ToColor(Color32 color)
        {
            return new Color(color.r / 255f, color.g / 255f, color.b / 255f, color.a / 255f);
        }

        private static bool TryReadPalette(CucliArgs args, out List<Color32> palette, out string error)
        {
            palette = new List<Color32>();
            error = null;

            JArray raw = args.GetArray("palette");
            if (raw == null) return true;

            for (int index = 0; index < raw.Count; index++)
            {
                Color32 color;
                string colorError;
                if (!TryParseColorToken(raw[index], out color, out colorError))
                {
                    error = "palette[" + index + "] 无效：" + colorError;
                    return false;
                }

                palette.Add(color);
            }

            return true;
        }

        private static Color32[] PaletteOrDefault(List<Color32> palette, int required)
        {
            var result = new Color32[required];
            if (palette != null && palette.Count >= required)
            {
                for (int index = 0; index < required; index++) result[index] = palette[index];
                return result;
            }

            if (palette != null)
            {
                for (int index = 0; index < palette.Count; index++) result[index] = palette[index];
            }

            for (int index = palette == null ? 0 : palette.Count; index < required; index++)
            {
                result[index] = index == 0 ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            }

            return result;
        }

        private static bool TryReadPixels(
            CucliArgs args,
            string name,
            out Color32[] pixels,
            out string error)
        {
            pixels = null;
            error = null;

            JToken token = args.Token(name);
            if (token == null || token.Type == JTokenType.Null) return false;

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>() ?? string.Empty;
                if (!text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
                {
                    error = "字符串像素数据必须以 'base64:' 开头。";
                    return false;
                }

                byte[] raw;
                try
                {
                    raw = System.Convert.FromBase64String(text.Substring("base64:".Length).Trim());
                }
                catch (FormatException)
                {
                    error = "base64 像素数据解码失败。";
                    return false;
                }

                int count = raw.Length / 4;
                var decoded = new Color32[count];
                for (int index = 0; index < count; index++)
                {
                    decoded[index] = new Color32(
                        raw[index * 4], raw[index * 4 + 1], raw[index * 4 + 2], raw[index * 4 + 3]);
                }

                pixels = decoded;
                return true;
            }

            var array = token as JArray;
            if (array == null)
            {
                error = "pixels 需要数组或 'base64:...' 字符串。";
                return false;
            }

            var result = new Color32[array.Count];
            for (int index = 0; index < array.Count; index++)
            {
                Color32 color;
                string colorError;
                if (!TryParseColorToken(array[index], out color, out colorError))
                {
                    error = "pixels[" + index + "] 无效：" + colorError;
                    return false;
                }

                result[index] = color;
            }

            pixels = result;
            return true;
        }

        /// <summary>Writes a PNG, imports it, then applies the requested importer settings.</summary>
        private static CucliCommandResult WriteTexture(
            CucliCommandContext context,
            string assetPath,
            Color32[] pixels,
            int width,
            int height,
            JObject importSettings,
            JToken spriteSettings,
            bool asSprite,
            JArray warnings)
        {
            string absolute = CucliAssets.ToAbsolute(assetPath);
            if (absolute == null)
            {
                return CucliCommandResult.Invalid("path", "无法解析 " + assetPath + " 的物理路径。");
            }

            string directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply();

                byte[] encoded = Encode(assetPath, texture, warnings);
                if (encoded == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "纹理编码失败：" + assetPath, new { path = assetPath });
                }

                File.WriteAllBytes(absolute, encoded);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                bool configuredAsSprite = asSprite;
                if (importSettings != null)
                {
                    if (ApplyImportSettings(importer, importSettings, context))
                    {
                        JToken textureType = importSettings["textureType"];
                        if (textureType != null && textureType.Type == JTokenType.String
                            && string.Equals(textureType.Value<string>(), "Sprite", StringComparison.Ordinal))
                        {
                            configuredAsSprite = true;
                        }
                    }
                }

                if (spriteSettings != null && spriteSettings.Type != JTokenType.Null
                    && spriteSettings.Type != JTokenType.Boolean)
                {
                    var spriteObject = spriteSettings as JObject;
                    if (spriteObject != null)
                    {
                        ApplySpriteSettings(importer, spriteObject);
                        configuredAsSprite = true;
                    }
                }
                else if (spriteSettings != null && spriteSettings.Type == JTokenType.Boolean
                         && spriteSettings.Value<bool>())
                {
                    configuredAsSprite = true;
                }

                if (configuredAsSprite)
                {
                    if (importer.textureType != TextureImporterType.Sprite)
                    {
                        importer.textureType = TextureImporterType.Sprite;
                    }

                    if (importer.spriteImportMode == SpriteImportMode.None)
                    {
                        importer.spriteImportMode = SpriteImportMode.Single;
                    }
                }

                importer.SaveAndReimport();
            }
            else
            {
                context.Warn("无法在 " + assetPath + " 上取得 TextureImporter，导入设置未写入。");
            }

            context.AddArtifact(absolute);

            var data = new JObject
            {
                ["path"] = assetPath,
                ["absolutePath"] = absolute,
                ["width"] = width,
                ["height"] = height,
                ["asSprite"] = asSprite,
                ["guid"] = CucliAssets.AssetPathToGuid(assetPath),
                ["warnings"] = warnings.Count == 0 ? null : warnings
            };

            return CucliCommandResult.Ok(
                data, "已写入纹理 " + assetPath + "（" + width + "x" + height + "）。");
        }

        private static byte[] Encode(string assetPath, Texture2D texture, JArray warnings)
        {
            string extension = CucliAssets.Extension(assetPath).ToLowerInvariant();
            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                    return texture.EncodeToJPG(90);
                case ".png":
                    return texture.EncodeToPNG();
                default:
                    warnings.Add("扩展名 '" + extension + "' 不受支持，仍按 PNG 编码（文件名保留原扩展名）。");
                    return texture.EncodeToPNG();
            }
        }

        private static Texture2D TryReadImage(string imagePath, JArray warnings)
        {
            string resolved = imagePath;
            if (!Path.IsPathRooted(resolved))
            {
                resolved = Path.Combine(CucliPaths.ProjectRoot, resolved);
            }

            if (!File.Exists(resolved))
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(resolved);
            }
            catch (IOException exception)
            {
                warnings.Add("读取图片失败：" + exception.Message);
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                return null;
            }

            return texture;
        }

        // ---------------------------------------------------------------- create / create_sprite

        private static CucliCommandResult Create(CucliCommandContext context, bool createSprite)
        {
            CucliArgs args = context.Input;

            string error;
            string assetPath = RequireAssetPath(args, out error);
            if (assetPath == null) return CucliCommandResult.Missing("path", error);

            JArray warnings = new JArray();

            string imagePath = args.GetString("imagePath") ?? args.GetString("image_path");
            bool hasFill = args.Token("fillColor") != null;
            bool hasPattern = !string.IsNullOrWhiteSpace(args.GetString("pattern"));
            bool hasPixels = args.Token("pixels") != null;

            if (!string.IsNullOrWhiteSpace(imagePath) && (hasFill || hasPattern || hasPixels))
            {
                return CucliCommandResult.Invalid(
                    "imagePath", "imagePath 不能与 fillColor / pattern / pixels 同时给出。");
            }

            int width;
            int height;
            Color32[] pixels;

            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                Texture2D loaded = TryReadImage(imagePath, warnings);
                if (loaded == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound, "无法加载图片：" + imagePath, new { imagePath });
                }

                try
                {
                    width = loaded.width;
                    height = loaded.height;
                    if (width > MaxDimension || height > MaxDimension)
                    {
                        return CucliCommandResult.Invalid(
                            "imagePath", "图片尺寸 " + width + "x" + height + " 超过上限 " + MaxDimension + "。");
                    }

                    pixels = loaded.GetPixels32();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(loaded);
                }
            }
            else
            {
                if (!TryResolveSize(args, out width, out height, out error))
                {
                    return CucliCommandResult.Invalid("width", error);
                }

                pixels = new Color32[width * height];
                for (int index = 0; index < pixels.Length; index++) pixels[index] = new Color32(0, 0, 0, 0);

                if (hasFill)
                {
                    Color32 fill;
                    string fillError;
                    if (!TryParseColorToken(args.Token("fillColor"), out fill, out fillError))
                    {
                        return CucliCommandResult.Invalid("fillColor", fillError);
                    }

                    for (int index = 0; index < pixels.Length; index++) pixels[index] = fill;
                }

                if (hasPattern)
                {
                    List<Color32> palette;
                    string paletteError;
                    if (!TryReadPalette(args, out palette, out paletteError))
                    {
                        return CucliCommandResult.Invalid("palette", paletteError);
                    }

                    pixels = BuildPattern(
                        args.GetString("pattern"), width, height,
                        Math.Max(1, args.GetInt("patternSize", args.GetInt("pattern_size", 8))),
                        PaletteOrDefault(palette, 2));
                }

                if (hasPixels)
                {
                    Color32[] explicitPixels;
                    string pixelError;
                    if (!TryReadPixels(args, "pixels", out explicitPixels, out pixelError))
                    {
                        return CucliCommandResult.Invalid("pixels", pixelError);
                    }

                    if (explicitPixels.Length != pixels.Length)
                    {
                        warnings.Add(
                            "pixels 数量 " + explicitPixels.Length + " 与 " + width + "x" + height
                            + " = " + pixels.Length + " 不匹配，按较短长度写入。");
                    }

                    int count = Math.Min(explicitPixels.Length, pixels.Length);
                    for (int index = 0; index < count; index++) pixels[index] = explicitPixels[index];
                }
            }

            JObject importSettings = args.GetObject("importSettings") ?? args.GetObject("import_settings");
            JToken spriteSettings = args.Token("spriteSettings");
            bool asSprite = createSprite || args.GetBool("asSprite", args.GetBool("as_sprite", false));

            CucliCommandResult result = WriteTexture(
                context, assetPath, pixels, width, height, importSettings, spriteSettings, asSprite, warnings);

            if (result.Success && result.Data is JObject)
            {
                var createData = (JObject)result.Data;
                createData["asSprite"] = asSprite || createSprite;
                createData["source"] = string.IsNullOrWhiteSpace(imagePath)
                    ? (hasPattern ? "pattern" : hasPixels ? "pixels" : hasFill ? "fillColor" : "transparent")
                    : "imagePath";
            }

            return result;
        }

        // ---------------------------------------------------------------- patterns

        private static Color32[] BuildPattern(
            string patternName,
            int width,
            int height,
            int size,
            Color32[] palette)
        {
            if (size <= 0) size = 8;
            string kind = string.IsNullOrWhiteSpace(patternName)
                ? "checker"
                : patternName.Trim().ToLowerInvariant().Replace("-", "_");

            var result = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color32 chosen;
                    switch (kind)
                    {
                        case "checker":
                        case "checkerboard":
                            chosen = palette[((x / size) + (y / size)) % 2];
                            break;

                        case "stripes":
                        case "stripes_v":
                            chosen = palette[(x / size) % 2];
                            break;

                        case "stripes_h":
                            chosen = palette[(y / size) % 2];
                            break;

                        case "stripes_diag":
                            chosen = palette[((x + y) / size) % 2];
                            break;

                        case "dots":
                        {
                            int cellX = x % (size * 2);
                            int cellY = y % (size * 2);
                            double distance = Math.Sqrt(
                                (cellX - size) * (cellX - size) + (cellY - size) * (cellY - size));
                            chosen = distance <= size * 0.6 ? palette[0] : palette[Math.Min(1, palette.Length - 1)];
                            break;
                        }

                        case "grid":
                        {
                            bool line = (x % size) == 0 || (y % size) == 0;
                            chosen = line ? palette[Math.Min(1, palette.Length - 1)] : palette[0];
                            break;
                        }

                        case "brick":
                        {
                            int row = y / size;
                            int offset = (row % 2) * (size / 2);
                            bool mortar = ((x + offset) % size) == 0 || (y % size) == 0;
                            chosen = mortar ? palette[Math.Min(1, palette.Length - 1)] : palette[0];
                            break;
                        }

                        default:
                            chosen = palette[0];
                            break;
                    }

                    result[y * width + x] = chosen;
                }
            }

            return result;
        }

        private static CucliCommandResult ApplyPattern(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string error;
            string assetPath = RequireAssetPath(args, out error);
            if (assetPath == null) return CucliCommandResult.Missing("path", error);

            int width;
            int height;
            if (!TryResolveSize(args, out width, out height, out error))
            {
                return CucliCommandResult.Invalid("width", error);
            }

            int size = args.GetInt("patternSize", args.GetInt("pattern_size", 8));
            if (size <= 0)
            {
                return CucliCommandResult.Invalid("patternSize", "patternSize 必须为正整数，收到 " + size + "。");
            }

            List<Color32> palette;
            string paletteError;
            if (!TryReadPalette(args, out palette, out paletteError))
            {
                return CucliCommandResult.Invalid("palette", paletteError);
            }

            var warnings = new JArray();
            Color32[] pixels;
            string imagePath = args.GetString("imagePath") ?? args.GetString("image_path");

            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                if (args.Token("fillColor") != null || args.Token("pixels") != null)
                {
                    return CucliCommandResult.Invalid(
                        "imagePath", "imagePath 不能与 fillColor / pixels 同时给出。");
                }

                Texture2D loaded = TryReadImage(imagePath, warnings);
                if (loaded == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound, "无法加载图片：" + imagePath, new { imagePath });
                }

                try
                {
                    width = loaded.width;
                    height = loaded.height;
                    if (width > MaxDimension || height > MaxDimension)
                    {
                        return CucliCommandResult.Invalid(
                            "imagePath", "图片尺寸 " + width + "x" + height + " 超过上限 " + MaxDimension + "。");
                    }

                    pixels = loaded.GetPixels32();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(loaded);
                }

                Color32[] patternPixels = BuildPattern(
                    args.GetString("pattern"), width, height, size, PaletteOrDefault(palette, 2));
                for (int index = 0; index < patternPixels.Length; index++)
                {
                    patternPixels[index].a = pixels[index].a;
                }

                pixels = patternPixels;
            }
            else
            {
                Color32 baseColor;
                string baseError;
                bool hasBase = TryParseColorToken(args.Token("fillColor"), out baseColor, out baseError);
                if (args.Token("fillColor") != null && !hasBase)
                {
                    return CucliCommandResult.Invalid("fillColor", baseError);
                }

                if (hasBase)
                {
                    pixels = new Color32[width * height];
                    for (int index = 0; index < pixels.Length; index++) pixels[index] = baseColor;
                }

                pixels = BuildPattern(
                    args.GetString("pattern"), width, height, size, PaletteOrDefault(palette, 2));
            }

            context.ThrowIfCancelled();

            JObject importSettings = args.GetObject("importSettings") ?? args.GetObject("import_settings");
            JToken spriteSettings = args.Token("spriteSettings");
            bool asSprite = args.GetBool("asSprite", args.GetBool("as_sprite", false));

            return WriteTexture(
                context, assetPath, pixels, width, height, importSettings, spriteSettings, asSprite, warnings);
        }

        // ---------------------------------------------------------------- gradient

        private static CucliCommandResult ApplyGradient(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string error;
            string assetPath = RequireAssetPath(args, out error);
            if (assetPath == null) return CucliCommandResult.Missing("path", error);

            int width;
            int height;
            if (!TryResolveSize(args, out width, out height, out error))
            {
                return CucliCommandResult.Invalid("width", error);
            }

            JObject gradientObject = args.GetObject("gradient");
            List<Color32> palette;
            string paletteError;
            if (!TryReadPalette(args, out palette, out paletteError))
            {
                return CucliCommandResult.Invalid("palette", paletteError);
            }

            var warnings = new JArray();
            Gradient gradient;
            bool hasKeys = TryBuildGradient(gradientObject, warnings, out gradient);
            if (!hasKeys && palette != null && palette.Count >= 2)
            {
                gradient = BuildGradientFromPalette(palette);
            }

            string type = args.GetString("gradientType") ?? args.GetString("gradient_type") ?? "linear";
            type = type.Trim().ToLowerInvariant();
            if (type != "linear" && type != "radial")
            {
                context.Warn("gradientType '" + type + "' 无法识别，按 linear 处理。");
                type = "linear";
            }

            float angle = args.GetFloat("gradientAngle", args.GetFloat("gradient_angle", 0f));
            var pixels = new Color32[width * height];

            float radians = angle * Mathf.Deg2Rad;
            float directionX = Mathf.Cos(radians);
            float directionY = Mathf.Sin(radians);
            float centreX = (width - 1) * 0.5f;
            float centreY = (height - 1) * 0.5f;
            float maxRadius = Mathf.Max(1f, Mathf.Sqrt(centreX * centreX + centreY * centreY));

            for (int y = 0; y < height; y++)
            {
                context.ThrowIfCancelled();
                for (int x = 0; x < width; x++)
                {
                    float t;
                    if (type == "radial")
                    {
                        float dx = x - centreX;
                        float dy = y - centreY;
                        t = Mathf.Sqrt(dx * dx + dy * dy) / maxRadius;
                    }
                    else
                    {
                        float projection = (x * directionX + y * directionY);
                        float minimum = 0f;
                        float maximum = Mathf.Max(1f, (width - 1) * Mathf.Abs(directionX) + (height - 1) * Mathf.Abs(directionY));
                        t = (projection - minimum) / maximum;
                    }

                    pixels[y * width + x] = ToColor32(gradient.Evaluate(Mathf.Clamp01(t)));
                }
            }

            JObject importSettings = args.GetObject("importSettings") ?? args.GetObject("import_settings");
            JToken spriteSettings = args.Token("spriteSettings");

            CucliCommandResult result = WriteTexture(
                context, assetPath, pixels, width, height, importSettings, spriteSettings, false, warnings);

            if (result.Success && result.Data is JObject)
            {
                var gradientData = (JObject)result.Data;
                gradientData["gradientType"] = type;
                gradientData["gradientAngle"] = angle;
                gradientData["usedExplicitKeys"] = hasKeys;
            }

            return result;
        }

        /// <summary>
        /// Builds a Gradient from the MCP key shapes. <see cref="CucliValues.Convert"/> is tried
        /// first; Gradient is a Unity class with no parameterless constructor, so the explicit
        /// parser below is what actually runs for every realistic payload.
        /// </summary>
        private static bool TryBuildGradient(JObject source, JArray warnings, out Gradient gradient)
        {
            gradient = new Gradient();
            if (source == null) return false;

            // Kept for contract compliance and for payloads CucliValues may learn to handle later.
            string convertError;
            JToken candidate = source["colorKeys"] ?? source["keys"];
            if (candidate != null)
            {
                object converted = CucliValues.Convert(candidate, typeof(Gradient), out convertError);
                if (converted is Gradient) { gradient = (Gradient)converted; return true; }
            }

            var colorKeys = new List<GradientColorKey>();
            var alphaKeys = new List<GradientAlphaKey>();

            JArray colorKeyArray = source["colorKeys"] as JArray ?? source["keys"] as JArray;
            if (colorKeyArray != null)
            {
                for (int index = 0; index < colorKeyArray.Count; index++)
                {
                    var entry = colorKeyArray[index] as JObject;
                    if (entry == null)
                    {
                        warnings.Add("gradient.colorKeys[" + index + "] 不是对象，已跳过。");
                        continue;
                    }

                    var entryArgs = new CucliArgs(entry);
                    float time = entryArgs.GetFloat("time", entryArgs.GetFloat("t", 0f));
                    Color32 color = new Color32(255, 255, 255, 255);
                    string colorError;

                    if (entry["color"] != null)
                    {
                        TryParseColorToken(entry["color"], out color, out colorError);
                    }
                    else if (entry["value"] != null)
                    {
                        TryParseColorToken(entry["value"], out color, out colorError);
                    }
                    else
                    {
                        var channels = new JArray(
                            entryArgs.GetFloat("r", 1f),
                            entryArgs.GetFloat("g", 1f),
                            entryArgs.GetFloat("b", 1f),
                            entryArgs.GetFloat("a", 1f));
                        TryParseColorToken(channels, out color, out colorError);
                    }

                    colorKeys.Add(new GradientColorKey(ToColor(color), Mathf.Clamp01(time)));
                }
            }
            else
            {
                // MCP also accepts colors[] with an optional stops[] and alphaKeys[].
                JArray colors = source["colors"] as JArray;
                if (colors != null)
                {
                    JArray stops = source["stops"] as JArray;
                    for (int index = 0; index < colors.Count; index++)
                    {
                        Color32 color;
                        string colorError;
                        if (!TryParseColorToken(colors[index], out color, out colorError))
                        {
                            warnings.Add("gradient.colors[" + index + "] 无效：" + colorError);
                            continue;
                        }

                        float time = stops != null && index < stops.Count
                            ? stops[index].Value<float>()
                            : colors.Count <= 1 ? 0f : (float)index / (colors.Count - 1);

                        colorKeys.Add(new GradientColorKey(ToColor(color), Mathf.Clamp01(time)));
                    }
                }
            }

            JArray alphaKeyArray = source["alphaKeys"] as JArray;
            if (alphaKeyArray != null)
            {
                for (int index = 0; index < alphaKeyArray.Count; index++)
                {
                    var entry = alphaKeyArray[index] as JObject;
                    if (entry == null) continue;
                    var entryArgs = new CucliArgs(entry);
                    float time = entryArgs.GetFloat("time", entryArgs.GetFloat("t", 0f));
                    float alpha = entryArgs.GetFloat("alpha", entryArgs.GetFloat("a", 1f));
                    if (entry["alpha"] == null && entry["a"] == null && entry["value"] != null)
                    {
                        alpha = entry["value"].Value<float>();
                    }

                    alphaKeys.Add(new GradientAlphaKey(Mathf.Clamp01(alpha), Mathf.Clamp01(time)));
                }
            }

            string mode = new CucliArgs(source).GetString("mode");
            if (!string.IsNullOrWhiteSpace(mode))
            {
                GradientMode gradientMode;
                try
                {
                    gradientMode = (GradientMode)Enum.Parse(typeof(GradientMode), mode.Trim(), true);
                }
                catch (ArgumentException)
                {
                    warnings.Add("gradient.mode '" + mode + "' 无法识别，按 Blend 处理。");
                    gradientMode = GradientMode.Blend;
                }

                gradient.mode = gradientMode;
            }

            if (colorKeys.Count == 0 && alphaKeys.Count == 0) return false;

            if (colorKeys.Count == 0)
            {
                Color sampled = gradient.Evaluate(0f);
                colorKeys.Add(new GradientColorKey(sampled, 0f));
            }

            if (alphaKeys.Count == 0)
            {
                alphaKeys.Add(new GradientAlphaKey(1f, 0f));
                alphaKeys.Add(new GradientAlphaKey(1f, 1f));
            }

            colorKeys.Sort((left, right) => left.time.CompareTo(right.time));
            alphaKeys.Sort((left, right) => left.time.CompareTo(right.time));

            gradient.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
            return true;
        }

        private static Gradient BuildGradientFromPalette(List<Color32> palette)
        {
            var gradient = new Gradient();
            var colorKeys = new List<GradientColorKey>();
            var alphaKeys = new List<GradientAlphaKey>();

            for (int index = 0; index < palette.Count; index++)
            {
                float time = palette.Count <= 1 ? 0f : (float)index / (palette.Count - 1);
                colorKeys.Add(new GradientColorKey(ToColor(palette[index]), time));
                alphaKeys.Add(new GradientAlphaKey(palette[index].a / 255f, time));
            }

            gradient.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
            return gradient;
        }

        // ---------------------------------------------------------------- noise

        private static CucliCommandResult ApplyNoise(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string error;
            string assetPath = RequireAssetPath(args, out error);
            if (assetPath == null) return CucliCommandResult.Missing("path", error);

            int width;
            int height;
            if (!TryResolveSize(args, out width, out height, out error))
            {
                return CucliCommandResult.Invalid("width", error);
            }

            int octaves = args.GetInt("octaves", 1);
            if (octaves <= 0)
            {
                return CucliCommandResult.Invalid("octaves", "octaves 必须为正整数，收到 " + octaves + "。");
            }

            float scale = args.GetFloat("noiseScale", args.GetFloat("noise_scale", 0.1f));
            if (scale <= 0f)
            {
                return CucliCommandResult.Invalid("noiseScale", "noiseScale 必须为正数，收到 " + scale + "。");
            }

            int seed = args.GetInt("seed", 0);
            float strength = Mathf.Clamp01(args.GetFloat("strength", 1f));
            string type = (args.GetString("noiseType") ?? args.GetString("noise_type") ?? "perlin")
                .Trim().ToLowerInvariant();
            if (type != "perlin" && type != "value")
            {
                context.Warn("noiseType '" + type + "' 无法识别，按 perlin 处理。");
                type = "perlin";
            }

            List<Color32> palette;
            string paletteError;
            if (!TryReadPalette(args, out palette, out paletteError))
            {
                return CucliCommandResult.Invalid("palette", paletteError);
            }

            var warnings = new JArray();
            long work = (long)width * height * octaves;
            if (work > MaxPixelWork)
            {
                warnings.Add("噪声工作量 " + work + " 超过阈值 " + MaxPixelWork + "，可能较慢。");
            }

            Color32[] noiseColors = PaletteOrDefault(palette, 2);
            var pixels = new Color32[width * height];

            float offsetX = (seed % 1024) * 13.17f;
            float offsetY = (seed % 1024) * 7.31f;
            Random random = null;
            int[] permutation = null;
            if (type == "value")
            {
                random = new Random(seed == 0 ? 1 : seed);
                permutation = BuildPermutation(random);
            }

            for (int y = 0; y < height; y++)
            {
                context.ThrowIfCancelled();
                for (int x = 0; x < width; x++)
                {
                    float amplitude = 1f;
                    float frequency = 1f;
                    float total = 0f;
                    float normalizer = 0f;

                    for (int octave = 0; octave < octaves; octave++)
                    {
                        float sampleX = (x + offsetX) * scale * frequency;
                        float sampleY = (y + offsetY) * scale * frequency;
                        float sample = type == "value"
                            ? ValueNoise(sampleX, sampleY, permutation)
                            : Mathf.PerlinNoise(sampleX, sampleY);

                        total += sample * amplitude;
                        normalizer += amplitude;
                        amplitude *= 0.5f;
                        frequency *= 2f;
                    }

                    float value = normalizer <= 0f ? 0f : Mathf.Clamp01(total / normalizer);
                    value = Mathf.Lerp(0.5f, value, strength);
                    Color32 sampled = noiseColors[value < 0.5f ? 0 : Mathf.Min(1, noiseColors.Length - 1)];
                    Color32 white = new Color32(255, 255, 255, 255);
                    pixels[y * width + x] = new Color32(
                        (byte)Mathf.RoundToInt(Mathf.Lerp(sampled.r, white.r, value)),
                        (byte)Mathf.RoundToInt(Mathf.Lerp(sampled.g, white.g, value)),
                        (byte)Mathf.RoundToInt(Mathf.Lerp(sampled.b, white.b, value)),
                        sampled.a);
                }
            }

            JObject importSettings = args.GetObject("importSettings") ?? args.GetObject("import_settings");
            JToken spriteSettings = args.Token("spriteSettings");

            CucliCommandResult result = WriteTexture(
                context, assetPath, pixels, width, height, importSettings, spriteSettings, false, warnings);

            if (result.Success && result.Data is JObject)
            {
                var noiseData = (JObject)result.Data;
                noiseData["noiseType"] = type;
                noiseData["noiseScale"] = scale;
                noiseData["octaves"] = octaves;
                noiseData["seed"] = seed;
            }

            return result;
        }

        private static int[] BuildPermutation(Random random)
        {
            var table = new int[512];
            var source = new int[256];
            for (int index = 0; index < 256; index++) source[index] = index;

            for (int index = 255; index > 0; index--)
            {
                int swap = random.Next(index + 1);
                int temporary = source[index];
                source[index] = source[swap];
                source[swap] = temporary;
            }

            for (int index = 0; index < 512; index++) table[index] = source[index & 255];
            return table;
        }

        private static float Smooth(float value)
        {
            return value * value * (3f - 2f * value);
        }

        /// <summary>Deterministic value noise: lattice gradients come from a seeded permutation table.</summary>
        private static float ValueNoise(float x, float y, int[] permutation)
        {
            int xi = Mathf.FloorToInt(x) & 255;
            int yi = Mathf.FloorToInt(y) & 255;
            float xf = x - Mathf.Floor(x);
            float yf = y - Mathf.Floor(y);

            float u = Smooth(xf);
            float v = Smooth(yf);

            int aa = permutation[permutation[xi] + yi];
            int ba = permutation[permutation[xi + 1] + yi];
            int ab = permutation[permutation[xi] + yi + 1];
            int bb = permutation[permutation[xi + 1] + yi + 1];

            float bottom = Mathf.Lerp(aa / 255f, ba / 255f, u);
            float top = Mathf.Lerp(ab / 255f, bb / 255f, u);
            return Mathf.Lerp(bottom, top, v);
        }

        // ---------------------------------------------------------------- modify

        private static CucliCommandResult Modify(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string error;
            string assetPath = RequireAssetPath(args, out error);
            if (assetPath == null) return CucliCommandResult.Missing("path", error);

            string absolute = CucliAssets.ToAbsolute(assetPath);
            if (absolute == null || !File.Exists(absolute))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "纹理不存在：" + assetPath, new { path = assetPath });
            }

            JObject importSettings = args.GetObject("importSettings") ?? args.GetObject("import_settings");
            JToken spriteSettings = args.Token("spriteSettings");
            bool asSprite = args.GetBool("asSprite", args.GetBool("as_sprite", false));
            JObject setPixels = args.GetObject("setPixels") ?? args.GetObject("set_pixels");

            if (importSettings != null && args.Token("asSprite") != null && asSprite)
            {
                return CucliCommandResult.Invalid(
                    "importSettings", "importSettings 与 asSprite=true 不能同时给出，请只保留一个。");
            }

            if (setPixels == null && importSettings == null && spriteSettings == null && !asSprite)
            {
                return CucliCommandResult.Ok(
                    new { path = assetPath, changed = false },
                    "既没有 setPixels 也没有导入设置，未做任何修改。");
            }

            if (setPixels != null)
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (texture == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound,
                        "无法以 Texture2D 加载 " + assetPath + "，无法做像素编辑。",
                        new { path = assetPath });
                }

                int width = texture.width;
                int height = texture.height;
                var pixelArgs = new CucliArgs(setPixels);

                int x = pixelArgs.GetInt("x", 0);
                int y = pixelArgs.GetInt("y", 0);
                int regionWidth = pixelArgs.GetInt("width", 1);
                int regionHeight = pixelArgs.GetInt("height", 1);

                if (regionWidth <= 0 || regionHeight <= 0)
                {
                    return CucliCommandResult.Invalid(
                        "setPixels", "setPixels.width/height 必须为正整数（收到 "
                        + regionWidth + "x" + regionHeight + "）。");
                }

                if (x < 0 || y < 0 || x + regionWidth > width || y + regionHeight > height)
                {
                    return CucliCommandResult.Invalid(
                        "setPixels",
                        "区域 (" + x + "," + y + " " + regionWidth + "x" + regionHeight + ") 越出纹理 "
                        + width + "x" + height + "。");
                }

                string regionError;
                Color32[] regionPixels;
                bool hasRegionPixels = TryReadPixels(pixelArgs, "pixels", out regionPixels, out regionError);
                if (pixelArgs.Token("pixels") != null && !hasRegionPixels)
                {
                    return CucliCommandResult.Invalid("setPixels.pixels", regionError);
                }

                Color32 fill;
                bool hasFill = TryParseColorToken(setPixels["color"], out fill, out regionError);
                if (setPixels["color"] != null && !hasFill)
                {
                    return CucliCommandResult.Invalid("setPixels.color", regionError);
                }

                if (!hasRegionPixels && !hasFill)
                {
                    return CucliCommandResult.Invalid(
                        "setPixels", "setPixels 需要 color 或 pixels 之一。");
                }

                var updated = new Color32[width * height];
                Color32[] source = ReadSourcePixels(assetPath, texture, out string readError);
                if (source == null)
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, readError, new { path = assetPath });
                }

                Array.Copy(source, updated, Math.Min(source.Length, updated.Length));

                for (int row = 0; row < regionHeight; row++)
                {
                    for (int column = 0; column < regionWidth; column++)
                    {
                        int targetIndex = (y + row) * width + (x + column);
                        if (hasRegionPixels)
                        {
                            int sourceIndex = row * regionWidth + column;
                            updated[targetIndex] = sourceIndex < regionPixels.Length ? regionPixels[sourceIndex] : fill;
                        }
                        else
                        {
                            updated[targetIndex] = fill;
                        }
                    }
                }

                var warnings = new JArray();
                CucliCommandResult written = WriteTexture(
                    context, assetPath, updated, width, height,
                    importSettings, spriteSettings, asSprite, warnings);

                if (!written.Success) return written;

                if (written.Data is JObject)
                {
                    var modifiedData = (JObject)written.Data;
                    modifiedData["region"] = new JObject
                    {
                        ["x"] = x, ["y"] = y, ["width"] = regionWidth, ["height"] = regionHeight
                    };
                    modifiedData["changed"] = true;
                }

                return written;
            }

            // Import-settings-only path: no re-encode, no pixel read.
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "无法在 " + assetPath + " 上取得 TextureImporter。",
                    new { path = assetPath });
            }

            var appliedWarnings = new JArray();
            if (importSettings != null) ApplyImportSettings(importer, importSettings, context);
            if (spriteSettings != null && spriteSettings.Type == JTokenType.Object)
            {
                ApplySpriteSettings(importer, (JObject)spriteSettings);
                if (importer.textureType != TextureImporterType.Sprite) importer.textureType = TextureImporterType.Sprite;
                if (importer.spriteImportMode == SpriteImportMode.None) importer.spriteImportMode = SpriteImportMode.Single;
            }
            else if (asSprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                if (importer.spriteImportMode == SpriteImportMode.None) importer.spriteImportMode = SpriteImportMode.Single;
            }

            importer.SaveAndReimport();

            return CucliCommandResult.Ok(
                new { path = assetPath, changed = true, warnings = appliedWarnings.Count == 0 ? null : appliedWarnings },
                "已更新 " + assetPath + " 的导入设置。");
        }

        /// <summary>
        /// Reads the current pixels. Prefers the in-memory texture; falls back to decoding the file
        /// when the asset is not marked Read/Write.
        /// </summary>
        private static Color32[] ReadSourcePixels(string assetPath, Texture2D texture, out string error)
        {
            error = null;
            if (texture != null && texture.isReadable)
            {
                try
                {
                    return texture.GetPixels32();
                }
                catch (UnityException)
                {
                    // falls through to the file path below
                }
            }

            string absolute = CucliAssets.ToAbsolute(assetPath);
            if (absolute == null || !File.Exists(absolute))
            {
                error = "无法读取 " + assetPath + " 的像素，且文件不存在。";
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(absolute);
            }
            catch (IOException exception)
            {
                error = "读取 " + assetPath + " 失败：" + exception.Message;
                return null;
            }

            var temporary = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!temporary.LoadImage(bytes))
                {
                    error = "无法解码 " + assetPath + " 的像素数据。";
                    return null;
                }

                return temporary.GetPixels32();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temporary);
            }
        }

        // ---------------------------------------------------------------- set_import_settings

        private static CucliCommandResult SetImportSettings(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string error;
            string assetPath = RequireAssetPath(args, out error);
            if (assetPath == null) return CucliCommandResult.Missing("path", error);

            JObject importSettings = args.GetObject("importSettings") ?? args.GetObject("import_settings");
            JToken spriteSettings = args.Token("spriteSettings");
            bool asSprite = args.GetBool("asSprite", args.GetBool("as_sprite", false));
            string filterMode = args.GetFirstString("filterMode", "filter_mode");
            if (importSettings == null && !string.IsNullOrWhiteSpace(filterMode))
            {
                importSettings = new JObject { ["filterMode"] = filterMode };
            }

            if (importSettings == null && spriteSettings == null && !asSprite)
            {
                return CucliCommandResult.Missing(
                    "importSettings", "set_import_settings 需要 importSettings、spriteSettings、asSprite 或 --filter-mode 之一。");
            }

            if (importSettings != null && asSprite)
            {
                return CucliCommandResult.Invalid(
                    "asSprite", "importSettings 与 asSprite 不能同时给出，请只保留一个。");
            }

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "找不到纹理资产或它没有 TextureImporter：" + assetPath,
                    new { path = assetPath });
            }

            if (importSettings != null) ApplyImportSettings(importer, importSettings, context);

            if (spriteSettings != null && spriteSettings.Type == JTokenType.Object)
            {
                ApplySpriteSettings(importer, (JObject)spriteSettings);
                if (importer.textureType != TextureImporterType.Sprite) importer.textureType = TextureImporterType.Sprite;
                if (importer.spriteImportMode == SpriteImportMode.None) importer.spriteImportMode = SpriteImportMode.Single;
            }
            else if (asSprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                if (importer.spriteImportMode == SpriteImportMode.None) importer.spriteImportMode = SpriteImportMode.Single;
            }

            importer.SaveAndReimport();

            return CucliCommandResult.Ok(
                new { path = assetPath, textureType = importer.textureType.ToString() },
                "已写入 " + assetPath + " 的导入设置。");
        }

        // ---------------------------------------------------------------- importer helpers

        private static bool ApplyImportSettings(
            TextureImporter importer,
            JObject settings,
            CucliCommandContext context)
        {
            if (settings == null) return false;
            var args = new CucliArgs(settings);
            bool changed = false;

            string textureType = args.GetString("textureType") ?? args.GetString("texture_type");
            if (!string.IsNullOrWhiteSpace(textureType))
            {
                TextureImporterType parsed;
                if (TryParseEnumName(textureType, out parsed))
                {
                    importer.textureType = parsed;
                    changed = true;
                }
                else
                {
                    context.Warn("textureType '" + textureType + "' 无法识别，已忽略。");
                }
            }

            string textureShape = args.GetString("textureShape") ?? args.GetString("texture_shape");
            if (!string.IsNullOrWhiteSpace(textureShape))
            {
                TextureImporterShape parsed;
                if (TryParseEnumName(textureShape, out parsed))
                {
                    importer.textureShape = parsed;
                    changed = true;
                }
                else
                {
                    context.Warn("textureShape '" + textureShape + "' 无法识别，已忽略。");
                }
            }

            if (settings["sRGBTexture"] != null)
            {
                importer.sRGBTexture = args.GetBool("sRGBTexture");
                changed = true;
            }

            string alphaSource = args.GetString("alphaSource") ?? args.GetString("alpha_source");
            if (!string.IsNullOrWhiteSpace(alphaSource))
            {
                TextureImporterAlphaSource parsed;
                if (TryParseEnumName(alphaSource, out parsed))
                {
                    importer.alphaSource = parsed;
                    changed = true;
                }
                else
                {
                    context.Warn("alphaSource '" + alphaSource + "' 无法识别，已忽略。");
                }
            }

            if (settings["alphaIsTransparency"] != null)
            {
                importer.alphaIsTransparency = args.GetBool("alphaIsTransparency");
                changed = true;
            }

            if (settings["isReadable"] != null)
            {
                importer.isReadable = args.GetBool("isReadable");
                changed = true;
            }

            if (settings["mipmapEnabled"] != null)
            {
                importer.mipmapEnabled = args.GetBool("mipmapEnabled");
                changed = true;
            }

            string mipmapFilter = args.GetString("mipmapFilter") ?? args.GetString("mipmap_filter");
            if (!string.IsNullOrWhiteSpace(mipmapFilter))
            {
                TextureImporterMipFilter parsed;
                if (TryParseEnumName(mipmapFilter, out parsed))
                {
                    importer.mipmapFilter = parsed;
                    changed = true;
                }
                else
                {
                    context.Warn("mipmapFilter '" + mipmapFilter + "' 无法识别，已忽略。");
                }
            }

            string wrapMode = args.GetString("wrapMode") ?? args.GetString("wrap_mode");
            if (!string.IsNullOrWhiteSpace(wrapMode))
            {
                TextureWrapMode parsed;
                if (TryParseEnumName(wrapMode, out parsed))
                {
                    importer.wrapMode = parsed;
                    changed = true;
                }
                else
                {
                    context.Warn("wrapMode '" + wrapMode + "' 无法识别，已忽略。");
                }
            }

            string wrapModeU = args.GetString("wrapModeU") ?? args.GetString("wrap_mode_u");
            if (!string.IsNullOrWhiteSpace(wrapModeU))
            {
                TextureWrapMode parsed;
                if (TryParseEnumName(wrapModeU, out parsed))
                {
                    importer.wrapModeU = parsed;
                    changed = true;
                }
            }

            string wrapModeV = args.GetString("wrapModeV") ?? args.GetString("wrap_mode_v");
            if (!string.IsNullOrWhiteSpace(wrapModeV))
            {
                TextureWrapMode parsed;
                if (TryParseEnumName(wrapModeV, out parsed))
                {
                    importer.wrapModeV = parsed;
                    changed = true;
                }
            }

            string filterMode = args.GetString("filterMode") ?? args.GetString("filter_mode");
            if (!string.IsNullOrWhiteSpace(filterMode))
            {
                FilterMode parsed;
                if (TryParseEnumName(filterMode, out parsed))
                {
                    importer.filterMode = parsed;
                    changed = true;
                }
                else
                {
                    context.Warn("filterMode '" + filterMode + "' 无法识别，已忽略。");
                }
            }

            if (settings["anisoLevel"] != null)
            {
                importer.anisoLevel = args.GetInt("anisoLevel", importer.anisoLevel);
                changed = true;
            }

            if (settings["maxTextureSize"] != null)
            {
                importer.maxTextureSize = args.GetInt("maxTextureSize", importer.maxTextureSize);
                changed = true;
            }

            string textureCompression = args.GetString("textureCompression") ?? args.GetString("texture_compression");
            if (!string.IsNullOrWhiteSpace(textureCompression))
            {
                TextureImporterCompression parsed;
                if (TryParseEnumName(textureCompression, out parsed))
                {
                    importer.textureCompression = parsed;
                    changed = true;
                }
                else
                {
                    context.Warn("textureCompression '" + textureCompression + "' 无法识别，已忽略。");
                }
            }

            if (settings["crunchedCompression"] != null)
            {
                importer.crunchedCompression = args.GetBool("crunchedCompression");
                changed = true;
            }

            if (settings["compressionQuality"] != null)
            {
                importer.compressionQuality = args.GetInt("compressionQuality", importer.compressionQuality);
                changed = true;
            }

            // Sprite / normal-map settings live on TextureImporterSettings, not on the importer itself.
            bool needsTextureSettings =
                settings["spriteImportMode"] != null
                || settings["spritePixelsPerUnit"] != null
                || settings["spritePivot"] != null
                || settings["spriteMeshType"] != null
                || settings["spriteExtrude"] != null;

            if (needsTextureSettings || settings["isReadable"] != null)
            {
                var importerSettings = new TextureImporterSettings();
                importer.ReadTextureSettings(importerSettings);

                string spriteImportMode = args.GetString("spriteImportMode") ?? args.GetString("sprite_import_mode");
                if (!string.IsNullOrWhiteSpace(spriteImportMode))
                {
                    SpriteImportMode parsed;
                    if (TryParseEnumName(spriteImportMode, out parsed))
                    {
                        importerSettings.spriteMode = (int)parsed;
                        changed = true;
                    }
                    else
                    {
                        context.Warn("spriteImportMode '" + spriteImportMode + "' 无法识别，已忽略。");
                    }
                }

                if (settings["spritePixelsPerUnit"] != null)
                {
                    importerSettings.spritePixelsPerUnit = args.GetFloat("spritePixelsPerUnit", 100f);
                    changed = true;
                }

                if (settings["spritePivot"] != null)
                {
                    importerSettings.spriteAlignment = (int)SpriteAlignment.Custom;
                    importerSettings.spritePivot = args.GetVector2("spritePivot", new Vector2(0.5f, 0.5f));
                    changed = true;
                }

                string spriteMeshType = args.GetString("spriteMeshType") ?? args.GetString("sprite_mesh_type");
                if (!string.IsNullOrWhiteSpace(spriteMeshType))
                {
                    SpriteMeshType parsed;
                    if (TryParseEnumName(spriteMeshType, out parsed))
                    {
                        importerSettings.spriteMeshType = parsed;
                        changed = true;
                    }
                    else
                    {
                        context.Warn("spriteMeshType '" + spriteMeshType + "' 无法识别，已忽略。");
                    }
                }

                if (settings["spriteExtrude"] != null)
                {
                    importerSettings.spriteExtrude = (uint)Mathf.Max(0, args.GetInt("spriteExtrude", 1));
                    changed = true;
                }

                if (settings["isReadable"] != null)
                {
                    importerSettings.readable = args.GetBool("isReadable");
                }

                importer.SetTextureSettings(importerSettings);
            }

            return changed;
        }

        private static void ApplySpriteSettings(TextureImporter importer, JObject spriteSettings)
        {
            var args = new CucliArgs(spriteSettings);
            var importerSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(importerSettings);

            JToken pivot = spriteSettings["pivot"];
            if (pivot != null)
            {
                importerSettings.spriteAlignment = (int)SpriteAlignment.Custom;
                importerSettings.spritePivot = args.GetVector2("pivot", new Vector2(0.5f, 0.5f));
            }

            if (spriteSettings["pixelsPerUnit"] != null)
            {
                importerSettings.spritePixelsPerUnit = args.GetFloat("pixelsPerUnit", 100f);
            }

            importer.SetTextureSettings(importerSettings);
        }

        /// <summary>Enum parsing that ignores case, '_' and '-' so 'From_GrayScale' still works.</summary>
        private static bool TryParseEnumName<T>(string text, out T value) where T : struct
        {
            value = default(T);
            if (string.IsNullOrWhiteSpace(text)) return false;

            string cleaned = text.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            foreach (string name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, cleaned, StringComparison.OrdinalIgnoreCase))
                {
                    value = (T)Enum.Parse(typeof(T), name, true);
                    return true;
                }
            }

            return false;
        }

        // ---------------------------------------------------------------- delete

        private static CucliCommandResult Delete(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string error;
            string assetPath = RequireAssetPath(args, out error);
            if (assetPath == null) return CucliCommandResult.Missing("path", error);

            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "纹理不存在：" + assetPath, new { path = assetPath });
            }

            if (!args.GetBool("force", false))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.ConfirmationRequired,
                    "删除纹理 " + assetPath + " 不可逆。确认后请加 --force 重新执行。",
                    new { path = assetPath });
            }

            bool deleted = AssetDatabase.DeleteAsset(assetPath);
            if (!deleted)
            {
                string absolute = CucliAssets.ToAbsolute(assetPath);
                try
                {
                    File.Delete(absolute);
                    AssetDatabase.Refresh();
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
                "已删除纹理 " + assetPath + "。");
        }
    }
}
