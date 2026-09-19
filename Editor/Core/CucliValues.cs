using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Converts JSON payload values into CLR values. Shared by the reflection layer and the
    /// SerializedProperty layer so <c>component set</c>, <c>so set</c> and <c>material set</c> all
    /// accept the same shorthands.
    /// </summary>
    public static class CucliValues
    {
        public static bool IsNumeric(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(byte) || type == typeof(sbyte)
                || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint)
                || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        public static bool IsUnityObjectType(Type type)
        {
            return typeof(Object).IsAssignableFrom(type);
        }

        public static string FriendlyName(Type type)
        {
            if (type == null) return "null";
            Type underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return FriendlyName(underlying) + "?";
            if (type.IsArray) return FriendlyName(type.GetElementType()) + "[]";

            if (type.IsGenericType)
            {
                string name = type.Name;
                int tick = name.IndexOf('`');
                if (tick > 0) name = name.Substring(0, tick);
                var arguments = type.GetGenericArguments();
                var parts = new string[arguments.Length];
                for (int index = 0; index < arguments.Length; index++)
                {
                    parts[index] = FriendlyName(arguments[index]);
                }

                return name + "<" + string.Join(", ", parts) + ">";
            }

            return type.Name;
        }

        /// <summary>
        /// Converts a payload token to <paramref name="targetType"/>. Returns null and sets
        /// <paramref name="error"/> when the value cannot be represented, so callers can report a
        /// precise INVALID_PARAMETER instead of an exception.
        /// </summary>
        public static object Convert(JToken token, Type targetType, out string error)
        {
            error = null;
            Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (token == null || token.Type == JTokenType.Null)
            {
                if (!type.IsValueType || Nullable.GetUnderlyingType(targetType) != null) return null;
                error = "null is not valid for the value type " + FriendlyName(type) + ".";
                return null;
            }

            try
            {
                if (type == typeof(string)) return token.Type == JTokenType.String
                    ? token.Value<string>()
                    : token.ToString(Newtonsoft.Json.Formatting.None);

                if (type == typeof(bool))
                {
                    if (token.Type == JTokenType.Boolean) return token.Value<bool>();
                    if (token.Type == JTokenType.Integer) return token.Value<long>() != 0L;
                    string text = token.Type == JTokenType.String ? token.Value<string>() : null;
                    if (text != null)
                    {
                        switch (text.Trim().ToLowerInvariant())
                        {
                            case "true": case "1": case "yes": case "on": return true;
                            case "false": case "0": case "no": case "off": return false;
                        }
                    }

                    error = "cannot read a boolean from " + token.ToString(Newtonsoft.Json.Formatting.None) + ".";
                    return null;
                }

                if (type.IsEnum)
                {
                    if (token.Type == JTokenType.Integer)
                    {
                        return Enum.ToObject(type, token.Value<long>());
                    }

                    string name = token.Type == JTokenType.String
                        ? token.Value<string>()
                        : token.ToString(Newtonsoft.Json.Formatting.None);
                    if (name != null)
                    {
                        name = name.Trim();
                        // Accept both "Value" and "EnumType.Value".
                        int dot = name.LastIndexOf('.');
                        string shortName = dot >= 0 ? name.Substring(dot + 1) : name;
                        try
                        {
                            return Enum.Parse(type, shortName, true);
                        }
                        catch (ArgumentException)
                        {
                            long numeric;
                            if (long.TryParse(shortName, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric)
                                && Enum.IsDefined(type, numeric))
                            {
                                return Enum.ToObject(type, numeric);
                            }

                            error = "'" + shortName + "' is not a member of " + FriendlyName(type)
                                + ". Valid values: " + string.Join(", ", Enum.GetNames(type)) + ".";
                            return null;
                        }
                    }

                    error = "cannot read an enum from the supplied value.";
                    return null;
                }

                if (type == typeof(char))
                {
                    string text = token.Type == JTokenType.String ? token.Value<string>() : null;
                    if (!string.IsNullOrEmpty(text)) return text[0];
                    error = "a char must be a one-character string.";
                    return null;
                }

                if (IsNumeric(type))
                {
                    if (token.Type == JTokenType.Boolean) return System.Convert.ChangeType(token.Value<bool>() ? 1 : 0, type, CultureInfo.InvariantCulture);
                    if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                    {
                        double raw = token.Value<double>();
                        if (type == typeof(float)) return (float)raw;
                        if (type == typeof(int)) return (int)Math.Round(raw);
                        return System.Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
                    }

                    string text = token.Type == JTokenType.String ? token.Value<string>() : null;
                    if (text != null)
                    {
                        double parsed;
                        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        {
                            if (type == typeof(float)) return (float)parsed;
                            if (type == typeof(int)) return (int)Math.Round(parsed);
                            return System.Convert.ChangeType(parsed, type, CultureInfo.InvariantCulture);
                        }
                    }

                    error = "cannot read a number from " + token.ToString(Newtonsoft.Json.Formatting.None) + ".";
                    return null;
                }

                if (type == typeof(Vector2)) return ReadVector(token, 2, out error) is float[] v2 ? new Vector2(v2[0], v2[1]) : (object)null;
                if (type == typeof(Vector3)) return ReadVector(token, 3, out error) is float[] v3 ? new Vector3(v3[0], v3[1], v3[2]) : (object)null;
                if (type == typeof(Vector4)) return ReadVector(token, 4, out error) is float[] v4 ? new Vector4(v4[0], v4[1], v4[2], v4[3]) : (object)null;
                if (type == typeof(Vector2Int))
                {
                    float[] values = ReadVector(token, 2, out error);
                    return values == null ? null : (object)new Vector2Int((int)values[0], (int)values[1]);
                }

                if (type == typeof(Vector3Int))
                {
                    float[] values = ReadVector(token, 3, out error);
                    return values == null ? null : (object)new Vector3Int((int)values[0], (int)values[1], (int)values[2]);
                }

                if (type == typeof(Quaternion)) return ReadQuaternion(token, out error);
                if (type == typeof(Color)) return ReadColor(token, out error);
                if (type == typeof(Color32))
                {
                    Color color = ReadColor(token, out error);
                    return color == null ? null : (object)(Color32)color;
                }

                if (type == typeof(Rect))
                {
                    if (token.Type == JTokenType.Array)
                    {
                        var array = (JArray)token;
                        if (array.Count >= 4)
                        {
                            return new Rect(
                                array[0].Value<float>(), array[1].Value<float>(),
                                array[2].Value<float>(), array[3].Value<float>());
                        }
                    }

                    var obj = token as JObject;
                    if (obj != null)
                    {
                        var args = new CucliArgs(obj);
                        return new Rect(
                            args.GetFloat("x"), args.GetFloat("y"),
                            args.GetFloat("width"), args.GetFloat("height"));
                    }

                    error = "a Rect needs [x, y, width, height] or an object with those keys.";
                    return null;
                }

                if (type == typeof(Bounds))
                {
                    var obj = token as JObject;
                    if (obj != null)
                    {
                        var args = new CucliArgs(obj);
                        return new Bounds(args.GetVector3("center"), args.GetVector3("size", Vector3.one));
                    }

                    error = "Bounds needs an object with 'center' and 'size'.";
                    return null;
                }

                if (type == typeof(LayerMask))
                {
                    if (token.Type == JTokenType.Integer) return (LayerMask)token.Value<int>();
                    string text = token.Type == JTokenType.String ? token.Value<string>() : null;
                    int layer;
                    return int.TryParse(text, out layer) ? (LayerMask)layer : (object)LayerMask.GetMask(text);
                }

                if (type == typeof(AnimationCurve))
                {
                    var curve = new AnimationCurve();
                    if (token.Type == JTokenType.Array)
                    {
                        var keys = new List<Keyframe>();
                        foreach (JToken item in (JArray)token)
                        {
                            var keyObject = item as JObject;
                            if (keyObject == null) continue;
                            var keyArgs = new CucliArgs(keyObject);
                            keys.Add(new Keyframe(
                                keyArgs.GetFloat("time"), keyArgs.GetFloat("value"),
                                keyArgs.GetFloat("inTangent"), keyArgs.GetFloat("outTangent")));
                        }

                        curve.keys = keys.ToArray();
                        return curve;
                    }

                    error = "an AnimationCurve needs an array of keyframes.";
                    return null;
                }

                if (typeof(Object).IsAssignableFrom(type)) return ReadUnityObject(token, type, out error);

                if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
                {
                    return ReadList(token, type, out error);
                }

                if (token.Type == JTokenType.Object && !type.IsAbstract && !type.IsInterface)
                {
                    // Structured object: create an instance and fill its public members.
                    object instance = Activator.CreateInstance(type);
                    foreach (JProperty property in ((JObject)token).Properties())
                    {
                        string memberError;
                        if (!CucliReflection.TrySetMember(instance, property.Name, property.Value, out memberError))
                        {
                            error = "field '" + property.Name + "': " + memberError;
                            return null;
                        }
                    }

                    return instance;
                }

                error = "cannot convert JSON " + token.Type + " to " + FriendlyName(targetType) + ".";
                return null;
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return null;
            }
        }

        private static float[] ReadVector(JToken token, int expected, out string error)
        {
            error = null;
            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                if (array.Count < expected)
                {
                    error = "expected " + expected + " components, got " + array.Count + ".";
                    return null;
                }

                var values = new float[expected];
                for (int index = 0; index < expected; index++) values[index] = array[index].Value<float>();
                return values;
            }

            if (token.Type == JTokenType.Object)
            {
                var args = new CucliArgs((JObject)token);
                string[] keys = expected >= 4
                    ? new[] { "x", "y", "z", "w" }
                    : expected == 3 ? new[] { "x", "y", "z" } : new[] { "x", "y" };
                var values = new float[expected];
                for (int index = 0; index < expected; index++)
                {
                    if (!args.Has(keys[index]))
                    {
                        error = "missing component '" + keys[index] + "'.";
                        return null;
                    }

                    values[index] = args.GetFloat(keys[index]);
                }

                return values;
            }

            if (token.Type == JTokenType.String)
            {
                string[] parts = token.Value<string>()
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < expected)
                {
                    error = "expected " + expected + " comma separated components.";
                    return null;
                }

                var values = new float[expected];
                for (int index = 0; index < expected; index++)
                {
                    if (!float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
                    {
                        error = "'" + parts[index] + "' is not a number.";
                        return null;
                    }
                }

                return values;
            }

            error = "expected an array, an object with components, or a comma separated string.";
            return null;
        }

        private static Quaternion ReadQuaternion(JToken token, out string error)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var args = new CucliArgs(obj);
                if (args.Has("euler"))
                {
                    error = null;
                    return Quaternion.Euler(args.GetVector3("euler"));
                }

                if (args.Has("w"))
                {
                    error = null;
                    return new Quaternion(
                        args.GetFloat("x"), args.GetFloat("y"), args.GetFloat("z"), args.GetFloat("w", 1f));
                }
            }

            if (token.Type == JTokenType.Array)
            {
                float[] values = ReadVector(token, 4, out error);
                return values == null ? default(Quaternion) : new Quaternion(values[0], values[1], values[2], values[3]);
            }

            float[] euler = ReadVector(token, 3, out error);
            return euler == null ? default(Quaternion) : Quaternion.Euler(euler[0], euler[1], euler[2]);
        }

        private static Color ReadColor(JToken token, out string error)
        {
            error = null;
            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string html = text.Trim();
                    if (html[0] != '#') html = "#" + html;
                    Color parsed;
                    if (ColorUtility.TryParseHtmlString(html, out parsed)) return parsed;
                }

                error = "'" + text + "' is not a recognised colour. Use '#RRGGBB', '#RRGGBBAA' or a names.";
                return default(Color);
            }

            var array = token as JArray;
            if (array != null && array.Count >= 3)
            {
                float alpha = array.Count >= 4 ? array[3].Value<float>() : 1f;
                return new Color(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(), alpha);
            }

            var obj = token as JObject;
            if (obj != null)
            {
                var args = new CucliArgs(obj);
                if (args.Has("hex") || args.Has("html"))
                {
                    string text = args.GetString("hex") ?? args.GetString("html");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string html = text.Trim();
                        if (html[0] != '#') html = "#" + html;
                        Color parsed;
                        if (ColorUtility.TryParseHtmlString(html, out parsed)) return parsed;
                    }
                }

                return new Color(
                    args.GetFloat("r"), args.GetFloat("g"), args.GetFloat("b"), args.GetFloat("a", 1f));
            }

            error = "expected '#RRGGBB', [r,g,b(,a)] or an object with r/g/b/a.";
            return default(Color);
        }

        /// <summary>
        /// Resolves a Unity object reference from one of: an asset path, a GUID, an instance id, a
        /// <c>{assetPath|guid|instanceId|name}</c> object, or <c>null</c>/<c>""</c>.
        /// </summary>
        public static object ReadUnityObject(JToken token, Type type, out string error)
        {
            error = null;
            if (token.Type == JTokenType.Null) return null;

            if (token.Type == JTokenType.Integer)
            {
                Object byId = EditorUtility.InstanceIDToObject(token.Value<int>());
                if (byId == null)
                {
                    error = "no object is loaded with instance id " + token.Value<int>() + ".";
                    return null;
                }

                if (!type.IsInstanceOfType(byId))
                {
                    error = "instance " + byId.name + " is a " + byId.GetType().Name + ", not " + FriendlyName(type) + ".";
                    return null;
                }

                return byId;
            }

            if (token.Type == JTokenType.Object)
            {
                var args = new CucliArgs((JObject)token);
                if (args.Has("instanceId")) return ReadUnityObject(args.Token("instanceId"), type, out error);
                if (args.Has("assetPath")) return ReadUnityObject(args.Token("assetPath"), type, out error);
                if (args.Has("guid")) return ReadUnityObject(args.Token("guid"), type, out error);
                if (args.Has("path")) return ReadUnityObject(args.Token("path"), type, out error);
                if (args.Has("name")) return FindLoadedObject(args.GetString("name"), type, out error);
                error = "an object reference needs 'assetPath', 'guid', 'instanceId' or 'name'.";
                return null;
            }

            string text = token.Type == JTokenType.String ? token.Value<string>() : null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();

            // GUID form: 32 hex characters without separators.
            if (text.Length == 32 && IsHex(text))
            {
                string byGuid = CucliAssets.GuidToAssetPath(text);
                if (!string.IsNullOrEmpty(byGuid))
                {
                    Object loaded = CucliAssets.LoadMain(byGuid);
                    if (loaded != null && type.IsInstanceOfType(loaded)) return loaded;
                    if (loaded != null)
                    {
                        error = "'" + text + "' resolves to " + loaded.GetType().Name + ", not " + FriendlyName(type) + ".";
                        return null;
                    }
                }
            }

            if (text.IndexOf('/') >= 0 || text.IndexOf('.') >= 0)
            {
                string normalized = CucliAssets.NormalizeAssetPath(text);
                if (normalized != null)
                {
                    Object asset = CucliAssets.LoadMain(normalized);
                    if (asset != null)
                    {
                        if (type.IsInstanceOfType(asset)) return asset;
                        error = normalized + " is a " + asset.GetType().Name + ", not " + FriendlyName(type) + ".";
                        return null;
                    }

                    error = "no asset exists at " + normalized + ".";
                    return null;
                }
            }

            return FindLoadedObject(text, type, out error);
        }

        private static bool IsHex(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool hex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F');
                if (!hex) return false;
            }

            return true;
        }

        private static object FindLoadedObject(string name, Type type, out string error)
        {
            error = null;
            var candidates = Resources.FindObjectsOfTypeAll(type);
            Object match = null;
            foreach (Object candidate in candidates)
            {
                if (candidate == null || !string.Equals(candidate.name, name, StringComparison.Ordinal)) continue;
                if (match != null)
                {
                    error = "several loaded " + FriendlyName(type) + " objects are named '" + name
                        + "'; use an asset path or an instance id instead.";
                    return null;
                }

                match = candidate;
            }

            if (match == null)
            {
                error = "no loaded " + FriendlyName(type) + " object is named '" + name + "'.";
                return null;
            }

            return match;
        }

        private static object ReadList(JToken token, Type type, out string error)
        {
            error = null;
            bool isArray = type.IsArray;
            Type elementType = isArray ? type.GetElementType() : type.GetGenericArguments()[0];

            if (token.Type != JTokenType.Array)
            {
                error = "expected a JSON array for " + FriendlyName(type) + ".";
                return null;
            }

            var array = (JArray)token;
            var list = new List<object>(array.Count);
            foreach (JToken item in array)
            {
                string itemError;
                object converted = Convert(item, elementType, out itemError);
                if (itemError != null)
                {
                    error = "element " + list.Count + ": " + itemError;
                    return null;
                }

                list.Add(converted);
            }

            if (isArray)
            {
                Array result = Array.CreateInstance(elementType, list.Count);
                for (int index = 0; index < list.Count; index++) result.SetValue(list[index], index);
                return result;
            }

            var typed = (IList)Activator.CreateInstance(type);
            foreach (object item in list) typed.Add(item);
            return typed;
        }
    }
}
