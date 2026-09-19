using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Typed, forgiving reader over a JSON payload. Every command uses this instead of poking at
    /// <see cref="JObject"/> directly, so parameter handling stays consistent across the CLI -
    /// including the vector / colour / enum shorthands that agents get wrong most often.
    /// </summary>
    public readonly struct CucliArgs
    {
        private readonly JObject m_args;

        internal CucliArgs(JObject args)
        {
            m_args = args;
        }

        public JObject Raw => m_args ?? new JObject();

        public bool Has(string name)
        {
            JToken token = Token(name);
            return token != null && token.Type != JTokenType.Null;
        }

        public JToken Token(string name)
        {
            if (m_args == null || string.IsNullOrEmpty(name)) return null;
            JToken token;
            return m_args.TryGetValue(name, out token) ? token : null;
        }

        public string GetString(string name, string fallback = null)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.String) return token.Value<string>();
            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                return token.ToString(Newtonsoft.Json.Formatting.None);
            }
            return Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);
        }

        public string RequireString(string name)
        {
            string value = GetString(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CucliUsageException(CucliCommandResult.Missing(name));
            }

            return value;
        }

        public bool GetBool(string name, bool fallback = false)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (token.Type == JTokenType.Integer) return token.Value<long>() != 0;
            string text = GetString(name);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            switch (text.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
                default: return fallback;
            }
        }

        public int GetInt(string name, int fallback = 0)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try
            {
                if (token.Type == JTokenType.Integer) return token.Value<int>();
                if (token.Type == JTokenType.Float) return (int)token.Value<double>();
                string text = GetString(name);
                int parsed;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }

                double asDouble;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out asDouble))
                {
                    return (int)asDouble;
                }
            }
            catch (Exception)
            {
                return fallback;
            }

            return fallback;
        }

        public long GetLong(string name, long fallback = 0L)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try
            {
                if (token.Type == JTokenType.Integer) return token.Value<long>();
                if (token.Type == JTokenType.Float) return (long)token.Value<double>();
                long parsed;
                if (long.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            catch (Exception)
            {
                return fallback;
            }

            return fallback;
        }

        public float GetFloat(string name, float fallback = 0f)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try
            {
                if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                {
                    return token.Value<float>();
                }

                float parsed;
                if (float.TryParse(GetString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            catch (Exception)
            {
                return fallback;
            }

            return fallback;
        }

        public double GetDouble(string name, double fallback = 0d)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try
            {
                if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                {
                    return token.Value<double>();
                }

                double parsed;
                if (double.TryParse(GetString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            catch (Exception)
            {
                return fallback;
            }

            return fallback;
        }

        /// <summary>Accepts a JSON array, a comma separated string, or a single value.</summary>
        public string[] GetStringList(string name)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return Array.Empty<string>();
            if (token.Type == JTokenType.Array)
            {
                return token.Children()
                    .Where(child => child.Type != JTokenType.Null)
                    .Select(child => child.Type == JTokenType.String
                        ? child.Value<string>()
                        : child.ToString(Newtonsoft.Json.Formatting.None))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
            }

            string text = GetString(name);
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            return text
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();
        }

        public int[] GetIntList(string name)
        {
            return GetStringList(name)
                .Select(part =>
                {
                    int parsed;
                    return int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                        ? parsed
                        : int.MinValue;
                })
                .Where(value => value != int.MinValue)
                .ToArray();
        }

        /// <summary>Accepts <c>[x,y,z]</c>, <c>{"x":..,"y":..,"z":..}</c> or <c>"1,2,3"</c>.</summary>
        public Vector3 GetVector3(string name, Vector3 fallback = default(Vector3))
        {
            float[] values = GetFloatArray(name, 3);
            return values == null ? fallback : new Vector3(values[0], values[1], values[2]);
        }

        public Vector4 GetVector4(string name, Vector4 fallback = default(Vector4))
        {
            float[] values = GetFloatArray(name, 4);
            return values == null
                ? fallback
                : new Vector4(values[0], values[1], values[2], values[3]);
        }

        public Vector2 GetVector2(string name, Vector2 fallback = default(Vector2))
        {
            float[] values = GetFloatArray(name, 2);
            return values == null ? fallback : new Vector2(values[0], values[1]);
        }

        public Quaternion GetRotation(string name, Quaternion fallback = default(Quaternion))
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                if (obj["w"] != null)
                {
                    var args = new CucliArgs(obj);
                    return new Quaternion(
                        args.GetFloat("x"), args.GetFloat("y"), args.GetFloat("z"), args.GetFloat("w", 1f));
                }

                if (obj["euler"] != null)
                {
                    Vector3 euler = new CucliArgs(obj).GetVector3("euler");
                    return Quaternion.Euler(euler);
                }
            }

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>() ?? string.Empty;
                if (text.IndexOf("euler", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.Count(c => c == ',') == 2)
                {
                    float[] euler = GetFloatArray(name, 3);
                    if (euler != null) return Quaternion.Euler(euler[0], euler[1], euler[2]);
                }
            }

            float[] values = GetFloatArray(name, 4);
            return values == null ? fallback : new Quaternion(values[0], values[1], values[2], values[3]);
        }

        private float[] GetFloatArray(string name, int expected)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Array)
            {
                var values = token.Children()
                    .Where(child => child.Type != JTokenType.Null)
                    .Select(child => child.Type == JTokenType.Float || child.Type == JTokenType.Integer
                        ? child.Value<float>()
                        : ParseFloat(child.Value<string>(), float.NaN))
                    .ToArray();
                if (values.Length < expected) return null;
                return values;
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                string[] keys = expected >= 4
                    ? new[] { "x", "y", "z", "w" }
                    : expected == 3 ? new[] { "x", "y", "z" } : new[] { "x", "y" };
                var values = new float[expected];
                for (int index = 0; index < expected; index++)
                {
                    JToken child = obj[keys[index]];
                    if (child == null) return null;
                    values[index] = child.Value<float>();
                }

                return values;
            }

            if (token.Type == JTokenType.String)
            {
                string[] parts = token.Value<string>()
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < expected) return null;
                var values = new float[expected];
                for (int index = 0; index < expected; index++)
                {
                    values[index] = ParseFloat(parts[index], float.NaN);
                }

                return values;
            }

            return null;
        }

        private static float ParseFloat(string text, float fallback)
        {
            float parsed;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : fallback;
        }

        /// <summary>Accepts <c>"#RRGGBB"</c>, <c>"#RRGGBBAA"</c>, <c>[r,g,b,a]</c> or an HTML colour name.</summary>
        public Color GetColor(string name, Color fallback = default(Color))
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Array)
            {
                float[] values = GetFloatArray(name, 3);
                if (values == null) return fallback;
                float alpha = 1f;
                var array = (JArray)token;
                if (array.Count >= 4) alpha = array[3].Value<float>();
                return new Color(values[0], values[1], values[2], alpha);
            }

            string text = GetString(name);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            text = text.Trim();
            if (text[0] != '#') text = "#" + text;
            Color parsedColor;
            return ColorUtility.TryParseHtmlString(text, out parsedColor) ? parsedColor : fallback;
        }

        public T GetEnum<T>(string name, T fallback) where T : struct
        {
            string text = GetString(name);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            try
            {
                return (T)Enum.Parse(typeof(T), text.Trim(), true);
            }
            catch (ArgumentException)
            {
                return fallback;
            }
            catch (OverflowException)
            {
                return fallback;
            }
        }

        public JObject GetObject(string name)
        {
            JToken token = Token(name);
            return token as JObject;
        }

        public JArray GetArray(string name)
        {
            JToken token = Token(name);
            return token as JArray;
        }

        public object GetRaw(string name)
        {
            JToken token = Token(name);
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.Type == JTokenType.Object || token.Type == JTokenType.Array
                ? token
                : ((JValue)token).Value;
        }

        /// <summary>Materialises <c>@file.json</c> style argument values into a parsed object.</summary>
        public JObject GetObjectOrFile(string name)
        {
            string text = GetString(name);
            if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("@", StringComparison.Ordinal))
            {
                string path = text.Substring(1);
                if (!System.IO.Path.IsPathRooted(path))
                {
                    path = System.IO.Path.Combine(CucliPaths.ProjectRoot, path);
                }

                if (!System.IO.File.Exists(path))
                {
                    throw new CucliUsageException(
                        CucliCommandResult.Fail(CucliErrors.AssetNotFound, "Payload file not found: " + path));
                }

                try
                {
                    return CucliJson.ParseObject(System.IO.File.ReadAllText(path, CucliJson.Utf8NoBom));
                }
                catch (Newtonsoft.Json.JsonException exception)
                {
                    throw new CucliUsageException(
                        CucliCommandResult.Invalid(name, "invalid JSON in " + path + ": " + exception.Message));
                }
            }

            return GetObject(name);
        }
    }

    /// <summary>
    /// Thrown when a payload is unusable. The dispatcher converts it into a
    /// <c>USAGE_ERROR</c> / <c>MISSING_PARAMETER</c> result, so commands can fail fast and still be
    /// reported with a stable code.
    /// </summary>
    public sealed class CucliUsageException : Exception
    {
        public CucliUsageException(CucliCommandResult result)
            : base(result == null ? "Invalid command usage." : result.Message)
        {
            Result = result;
        }

        public CucliCommandResult Result { get; }

        public static CucliUsageException Missing(string parameter)
        {
            return new CucliUsageException(CucliCommandResult.Missing(parameter));
        }

        public static CucliUsageException Invalid(string parameter, string reason)
        {
            return new CucliUsageException(CucliCommandResult.Invalid(parameter, reason));
        }

        public static CucliUsageException Unsupported(string action, params string[] supported)
        {
            return new CucliUsageException(CucliCommandResult.Unsupported(action, supported));
        }
    }
}
