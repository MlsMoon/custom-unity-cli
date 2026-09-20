using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The PanelSettings half of the <c>ui</c> group: the <c>create_panel_settings</c> and
    /// <c>update_panel_settings</c> actions live here and are dispatched by <see cref="UiCommand"/>,
    /// so this file adds no second command group.
    ///
    /// The MCP <c>manage_ui</c> tool accepts a flat, case-insensitive settings dictionary and
    /// silently ignores keys it does not know. This implementation keeps that shape but reports what
    /// it applied and what it failed on, so a typo is visible instead of silent.
    /// </summary>
    internal static class PanelSettingsCommand
    {
        internal static CucliCommandResult CreateAsset(CucliCommandContext context)
        {
            CucliArgs args = context.Input;

            string path = ResolvePath(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            if (CucliAssets.Exists(path) || System.IO.File.Exists(CucliAssets.ToAbsolute(path)))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    path + " already exists. Use 'cucli ui update_panel_settings' instead.",
                    new { path });
            }

            string parent = CucliAssets.ParentFolder(path);
            if (parent != null && !CucliAssets.EnsureFolder(parent))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The folder '" + parent + "' could not be created.");
            }

            PanelSettings settings = ScriptableObject.CreateInstance<PanelSettings>();
            if (settings == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "PanelSettings could not be instantiated.");
            }

            var applied = new JArray();
            var failed = new JArray();

            // Legacy top-level keys are honoured only when no settings dictionary is supplied.
            JObject dictionary = args.GetObject("settings");
            if (dictionary == null)
            {
                dictionary = new JObject();
                string scaleMode = args.GetString("scaleMode") ?? args.GetString("scale_mode");
                if (!string.IsNullOrWhiteSpace(scaleMode)) dictionary["scaleMode"] = scaleMode;

                JObject reference = args.GetObject("referenceResolution")
                    ?? args.GetObject("reference_resolution");
                if (reference != null)
                {
                    dictionary["referenceResolution"] = new JObject
                    {
                        ["width"] = reference["width"] == null ? 1920 : reference["width"],
                        ["height"] = reference["height"] == null ? 1080 : reference["height"]
                    };
                }
            }

            System.IO.File.WriteAllText(CucliAssets.ToAbsolute(path), string.Empty, new System.Text.UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.CreateAsset(settings, path);

            ApplySettings(settings, dictionary, applied, failed);

            EditorUtility.SetDirty(settings);
            CucliAssets.SaveAssets();
            CucliAssets.Refresh();

            var data = new JObject
            {
                ["path"] = path,
                ["applied"] = applied
            };

            if (failed.Count > 0)
            {
                data["failed"] = failed;
                context.Warn(failed.Count + " PanelSettings property(ies) were not applied.");
            }

            return CucliCommandResult.Ok(data, "Created " + path + " (PanelSettings).");
        }

        internal static CucliCommandResult UpdateAsset(CucliCommandContext context)
        {
            string path = ResolvePath(context, out CucliCommandResult failure);
            if (failure != null) return failure;

            PanelSettings settings = CucliAssets.Load<PanelSettings>(path);
            if (settings == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "No PanelSettings asset is at " + path + ".", new { path });
            }

            JObject dictionary = context.Input.GetObject("settings");
            if (dictionary == null)
            {
                return CucliCommandResult.Missing(
                    "settings", "Provide a settings dictionary, e.g. {\"scaleMode\":\"ScaleWithScreenSize\"}.");
            }

            var applied = new JArray();
            var failed = new JArray();
            Undo.RecordObject(settings, "cucli ui update_panel_settings");
            ApplySettings(settings, dictionary, applied, failed);
            EditorUtility.SetDirty(settings);
            CucliAssets.SaveAssets();

            var data = new JObject
            {
                ["path"] = path,
                ["applied"] = applied
            };

            if (applied.Count == 0)
            {
                data["failed"] = failed;
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "None of the supplied settings keys are PanelSettings properties. Recognised keys include: "
                    + string.Join(", ", RecognisedKeys()) + ".",
                    data);
            }

            if (failed.Count > 0)
            {
                data["failed"] = failed;
                context.Warn(failed.Count + " PanelSettings property(ies) were not applied.");
            }

            return CucliCommandResult.Ok(data, "Updated " + applied.Count + " PanelSettings property(ies) on " + path + ".");
        }

        /// <summary>Creates <c>Assets/UI/DefaultPanelSettings.asset</c> when none exists.</summary>
        internal static CucliCommandResult CreateDefaultAsset(CucliCommandContext context, out string path)
        {
            path = "Assets/UI/DefaultPanelSettings.asset";
            CucliArgs args = context.Input;
            JObject settings = args.GetObject("settings");
            if (settings == null)
            {
                settings = new JObject
                {
                    ["scaleMode"] = "ScaleWithScreenSize",
                    ["referenceResolution"] = new JObject { ["width"] = 1920, ["height"] = 1080 }
                };
            }

            CucliCommandResult result = CreateFromDictionary(context, path, settings);
            return result;
        }

        private static CucliCommandResult CreateFromDictionary(
            CucliCommandContext context, string path, JObject settings)
        {
            string parent = CucliAssets.ParentFolder(path);
            if (parent != null) CucliAssets.EnsureFolder(parent);

            PanelSettings asset = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(asset, path);

            var applied = new JArray();
            var failed = new JArray();
            ApplySettings(asset, settings, applied, failed);
            EditorUtility.SetDirty(asset);
            CucliAssets.SaveAssets();
            CucliAssets.Refresh();

            return CucliCommandResult.Ok(new { path, applied, failed });
        }

        private static string ResolvePath(CucliCommandContext context, out CucliCommandResult failure)
        {
            failure = null;
            CucliArgs args = context.Input;
            string raw = args.GetString("path") ?? args.GetString("assetPath") ?? args.GetString("asset_path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                failure = CucliCommandResult.Missing(
                    "path", "Example: --path Assets/UI/DefaultPanelSettings.asset");
                return null;
            }

            string normalized = CucliAssets.NormalizeAssetPath(raw);
            if (normalized == null)
            {
                failure = CucliCommandResult.Invalid("path", "the path escapes the project: " + raw);
                return null;
            }

            if (!normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".asset";
            }

            return normalized;
        }

        private static readonly string[] Keys =
        {
            "scaleMode", "screenMatchMode", "match", "referenceDpi", "fallbackDpi", "sortingOrder",
            "targetDisplay", "clearColor", "clearDepthStencil", "referenceResolution", "colorClearValue",
            "dynamicAtlasSettings", "themeStyleSheet"
        };

        private static IEnumerable<string> RecognisedKeys()
        {
            return Keys;
        }

        private static void ApplySettings(
            PanelSettings settings, JObject dictionary, JArray applied, JArray failed)
        {
            if (settings == null || dictionary == null) return;

            foreach (JProperty property in dictionary.Properties())
            {
                string key = Normalize(property.Name);
                string error;
                if (!TryApply(settings, key, property.Value, out error))
                {
                    failed.Add(new JObject { ["key"] = property.Name, ["reason"] = error });
                    continue;
                }

                applied.Add(Canonical(key));
            }
        }

        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char character in name)
            {
                if (character == '_' || character == '-' || character == ' ') continue;
                builder.Append(char.ToLowerInvariant(character));
            }

            return builder.ToString();
        }

        private static string Canonical(string normalized)
        {
            for (int index = 0; index < Keys.Length; index++)
            {
                if (Normalize(Keys[index]) == normalized) return Keys[index];
            }

            return normalized;
        }

        private static bool TryApply(PanelSettings settings, string key, JToken value, out string error)
        {
            error = null;
            switch (key)
            {
                case "scalemode":
                    return SetEnum(settings, "scaleMode", value, out error);

                case "screenmatchmode":
                    return SetEnum(settings, "screenMatchMode", value, out error);

                case "match":
                    return SetFloat(settings, "match", value, out error);

                case "referencedpi":
                    return SetFloat(settings, "referenceDpi", value, out error);

                case "fallbackdpi":
                    return SetFloat(settings, "fallbackDpi", value, out error);

                case "sortingorder":
                    return SetInt(settings, "sortingOrder", value, out error);

                case "targetdisplay":
                    return SetInt(settings, "targetDisplay", value, out error);

                case "clearcolor":
                    return SetBool(settings, "clearColor", value, out error);

                case "cleardepthstencil":
                    return SetBool(settings, "clearDepthStencil", value, out error);

                case "colorclearvalue":
                    return SetColor(settings, "colorClearValue", value, out error);

                case "referenceresolution":
                {
                    if (value == null || value.Type != JTokenType.Object)
                    {
                        error = "an object with 'width' and 'height' is required";
                        return false;
                    }

                    var reference = (JObject)value;
                    int width = reference["width"] == null ? 1920 : reference["width"].Value<int>();
                    int height = reference["height"] == null ? 1080 : reference["height"].Value<int>();
                    return SetValue(settings, "referenceResolution", new Vector2Int(width, height), out error);
                }

                case "dynamicatlassettings":
                {
                    if (value == null || value.Type != JTokenType.Object)
                    {
                        error = "an object with minAtlasSize / maxAtlasSize / maxSubTextureSize / activeFilters is required";
                        return false;
                    }

                    object current = GetMember(settings, "dynamicAtlasSettings");
                    if (current == null)
                    {
                        error = "PanelSettings.dynamicAtlasSettings could not be read";
                        return false;
                    }

                    var atlas = (JObject)value;
                    var applied = new List<string>();
                    string failure = ApplyTo(current, atlas, applied);
                    if (failure != null)
                    {
                        error = failure;
                        return false;
                    }

                    EditorUtility.SetDirty(settings);
                    return true;
                }

                case "themestylesheet":
                {
                    string raw = value == null || value.Type == JTokenType.Null
                        ? null
                        : value.ToString();
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        error = "a ThemeStyleSheet asset path is required";
                        return false;
                    }

                    string path = CucliAssets.NormalizeAssetPath(raw);
                    ThemeStyleSheet theme = path == null ? null : CucliAssets.Load<ThemeStyleSheet>(path);
                    if (theme == null)
                    {
                        error = "no ThemeStyleSheet asset is at '" + raw + "'";
                        return false;
                    }

                    object current = GetMember(settings, "themeStyleSheet");
                    if (current == null)
                    {
                        error = "PanelSettings.themeStyleSheet could not be read; assign it by hand";
                        return false;
                    }

                    return SetValue(settings, "themeStyleSheet", theme, out error);
                }

                default:
                    error = "unrecognised PanelSettings key";
                    return false;
            }
        }

        /// <summary>Applies a nested dictionary to a nested struct such as DynamicAtlasSettings.</summary>
        private static string ApplyTo(object target, JObject values, List<string> applied)
        {
            foreach (JProperty property in values.Properties())
            {
                string name = property.Name;
                MemberInfo member = FindMember(target.GetType(), name);
                if (member == null)
                {
                    string normalized = Normalize(name);
                    foreach (string candidate in new[] { "minAtlasSize", "maxAtlasSize", "maxSubTextureSize", "activeFilters" })
                    {
                        if (Normalize(candidate) == normalized)
                        {
                            member = FindMember(target.GetType(), candidate);
                            break;
                        }
                    }
                }

                if (member == null) continue;

                string error;
                if (!SetValue(target, member.Name, property.Value, out error)) return error;
                applied.Add(member.Name);
            }

            return null;
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, flags);
                if (field != null) return field;

                PropertyInfo property = current.GetProperty(name, flags);
                if (property != null) return property;
            }

            return null;
        }

        private static object GetMember(object target, string name)
        {
            MemberInfo member = FindMember(target.GetType(), name);
            if (member == null) return null;
            try
            {
                var field = member as FieldInfo;
                if (field != null) return field.GetValue(target);
                return ((PropertyInfo)member).GetValue(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool SetValue(object target, string name, object value, out string error)
        {
            error = null;
            MemberInfo member = FindMember(target.GetType(), name);
            if (member == null)
            {
                error = "'" + name + "' is not a member of " + target.GetType().Name;
                return false;
            }

            try
            {
                var field = member as FieldInfo;
                if (field != null)
                {
                    field.SetValue(target, value);
                    return true;
                }

                var property = (PropertyInfo)member;
                if (!property.CanWrite)
                {
                    error = "'" + name + "' is read-only";
                    return false;
                }

                property.SetValue(target, value, null);
                return true;
            }
            catch (Exception exception)
            {
                error = "'" + name + "' could not be written: " + exception.GetBaseException().Message;
                return false;
            }
        }

        private static bool SetValue(object target, string name, JToken value, out string error)
        {
            error = null;
            MemberInfo member = FindMember(target.GetType(), name);
            if (member == null)
            {
                error = "'" + name + "' is not a member of " + target.GetType().Name;
                return false;
            }

            Type memberType = member is FieldInfo
                ? ((FieldInfo)member).FieldType
                : ((PropertyInfo)member).PropertyType;

            object converted;
            string convertError = null;
            if (memberType.IsEnum)
            {
                converted = ConvertToEnum(value, memberType, out convertError);
            }
            else
            {
                try
                {
                    converted = CucliValues.Convert(value, memberType, out convertError);
                }
                catch (Exception exception)
                {
                    error = "'" + name + "' could not be converted: " + exception.GetBaseException().Message;
                    return false;
                }
            }

            if (convertError != null)
            {
                error = "'" + name + "': " + convertError;
                return false;
            }

            return SetValue(target, name, converted, out error);
        }

        private static object ConvertToEnum(JToken value, Type enumType, out string error)
        {
            error = null;
            if (value == null || value.Type == JTokenType.Null)
            {
                error = "an enum name or ordinal is required";
                return Activator.CreateInstance(enumType);
            }

            try
            {
                if (value.Type == JTokenType.Integer)
                {
                    return Enum.ToObject(enumType, value.Value<int>());
                }

                string text = value.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    error = "an enum name or ordinal is required";
                    return Activator.CreateInstance(enumType);
                }

                return Enum.Parse(enumType, text.Trim(), true);
            }
            catch (Exception)
            {
                error = "'" + value.ToString(Newtonsoft.Json.Formatting.None) + "' is not a valid "
                    + enumType.Name + " value (expected one of: " + string.Join(", ", Enum.GetNames(enumType)) + ")";
                return Activator.CreateInstance(enumType);
            }
        }

        private static bool SetEnum(object target, string name, JToken value, out string error)
        {
            error = null;
            Type memberType;
            if (!TryGetMemberType(target, name, out memberType))
            {
                error = "PanelSettings." + name + " is not available in this Unity version";
                return false;
            }

            object converted = ConvertToEnum(value, memberType, out error);
            if (error != null) return false;
            return SetValue(target, name, converted, out error);
        }

        private static bool TryGetMemberType(object target, string name, out Type memberType)
        {
            memberType = null;
            if (target == null) return false;
            MemberInfo member = FindMember(target.GetType(), name);
            if (member == null) return false;

            var field = member as FieldInfo;
            if (field != null)
            {
                memberType = field.FieldType;
                return true;
            }

            var property = member as PropertyInfo;
            if (property == null) return false;
            memberType = property.PropertyType;
            return true;
        }

        private static bool SetFloat(object target, string name, JToken value, out string error)
        {
            error = null;
            if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer
                && value.Type != JTokenType.String))
            {
                error = "a number is required";
                return false;
            }

            float parsed;
            if (!float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                error = "'" + value + "' is not a number";
                return false;
            }

            return SetValue(target, name, parsed, out error);
        }

        private static bool SetInt(object target, string name, JToken value, out string error)
        {
            error = null;
            int parsed;
            if (value == null || !int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                error = "an integer is required";
                return false;
            }

            return SetValue(target, name, parsed, out error);
        }

        private static bool SetBool(object target, string name, JToken value, out string error)
        {
            error = null;
            if (value == null) return SetValue(target, name, false, out error);

            var args = new CucliArgs(new JObject { ["value"] = value });
            return SetValue(target, name, args.GetBool("value", false), out error);
        }

        private static bool SetColor(object target, string name, JToken value, out string error)
        {
            error = null;
            if (value == null || value.Type == JTokenType.Null)
            {
                error = "a colour is required";
                return false;
            }

            Color parsed = Color.black;
            if (value.Type == JTokenType.String)
            {
                string text = value.Value<string>().Trim();
                if (text.Length > 0 && text[0] != '#') text = "#" + text;
                if (!ColorUtility.TryParseHtmlString(text, out parsed))
                {
                    error = "'" + value + "' is not a parseable colour";
                    return false;
                }
            }
            else if (value.Type == JTokenType.Object)
            {
                var args = new CucliArgs((JObject)value);
                parsed = new Color(
                    args.GetFloat("r"), args.GetFloat("g"), args.GetFloat("b"), args.GetFloat("a", 1f));
            }
            else if (value.Type == JTokenType.Array)
            {
                var array = (JArray)value;
                if (array.Count < 3)
                {
                    error = "a colour array needs at least 3 components";
                    return false;
                }

                parsed = new Color(
                    array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(),
                    array.Count >= 4 ? array[3].Value<float>() : 1f);
            }
            else
            {
                error = "a '#RRGGBBAA' string or an {r,g,b,a} object is required";
                return false;
            }

            return SetValue(target, name, parsed, out error);
        }
    }
}
