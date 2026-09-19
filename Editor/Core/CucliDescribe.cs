using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Turns arbitrary Unity objects into bounded, JSON-safe descriptions.
    ///
    /// This is the guard rail that keeps <c>cucli</c> usable from a terminal or an agent: a raw
    /// dump of a <c>TextMeshProUGUI</c> or a <c>Mesh</c> would emit megabytes of vertex, UV and
    /// material-instance data. Everything here is depth-limited, item-limited and length-limited.
    /// </summary>
    public static class CucliDescribe
    {
        public const int DefaultMaxItems = 64;
        public const int DefaultMaxDepth = 4;
        public const int DefaultMaxStringLength = 4096;

        public sealed class Options
        {
            public int MaxItems = DefaultMaxItems;
            public int MaxDepth = DefaultMaxDepth;
            public int MaxStringLength = DefaultMaxStringLength;
            public bool IncludeStackTraces;
            public bool IncludeComponentValues;

            public static readonly Options Default = new Options();

            public Options Clone()
            {
                return new Options
                {
                    MaxItems = MaxItems,
                    MaxDepth = MaxDepth,
                    MaxStringLength = MaxStringLength,
                    IncludeStackTraces = IncludeStackTraces,
                    IncludeComponentValues = IncludeComponentValues
                };
            }
        }

        /// <summary>Builds options from the <c>maxItems</c> / <c>maxDepth</c> / <c>maxStringLength</c> payload keys.</summary>
        public static Options OptionsFrom(CucliArgs args)
        {
            var options = new Options();
            if (args.Raw == null) return options;
            options.MaxItems = Mathf.Clamp(args.GetInt("maxItems", DefaultMaxItems), 1, 5000);
            options.MaxDepth = Mathf.Clamp(args.GetInt("maxDepth", DefaultMaxDepth), 1, 12);
            options.MaxStringLength = Mathf.Clamp(args.GetInt("maxStringLength", DefaultMaxStringLength), 16, 1 << 20);
            options.IncludeStackTraces = args.GetBool("includeStackTraces", false);
            options.IncludeComponentValues = args.GetBool("includeComponentValues", false);
            return options;
        }

        public static JToken Value(object value, Options options = null, int depth = 0)
        {
            options = options ?? Options.Default;
            if (value == null) return JValue.CreateNull();
            if (depth > options.MaxDepth)
            {
                return new JObject
                {
                    ["$truncated"] = "maximum depth reached",
                    ["type"] = value.GetType().Name
                };
            }

            Type type = value.GetType();
            if (value is JToken token) return token;
            if (value is string text) return new JValue(Truncate(text, options.MaxStringLength));
            if (value is bool boolean) return new JValue(boolean);
            if (value is char character) return new JValue(character.ToString());
            if (value is Enum) return new JValue(value.ToString());

            if (value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong)
            {
                return new JValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }

            if (value is float floatValue) return SafeNumber(floatValue);
            if (value is double doubleValue) return SafeNumber(doubleValue);
            if (value is decimal decimalValue) return new JValue(decimalValue);

            if (value is Vector2 vector2) return Vector(vector2.x, vector2.y);
            if (value is Vector3 vector3) return Vector(vector3.x, vector3.y, vector3.z);
            if (value is Vector4 vector4) return Vector(vector4.x, vector4.y, vector4.z, vector4.w);
            if (value is Vector2Int vector2Int) return Vector(vector2Int.x, vector2Int.y);
            if (value is Vector3Int vector3Int) return Vector(vector3Int.x, vector3Int.y, vector3Int.z);
            if (value is Quaternion quaternion)
            {
                return new JObject
                {
                    ["x"] = SafeNumber(quaternion.x),
                    ["y"] = SafeNumber(quaternion.y),
                    ["z"] = SafeNumber(quaternion.z),
                    ["w"] = SafeNumber(quaternion.w),
                    ["euler"] = Vector(quaternion.eulerAngles.x, quaternion.eulerAngles.y, quaternion.eulerAngles.z)
                };
            }

            if (value is Color color)
            {
                return new JObject
                {
                    ["r"] = SafeNumber(color.r),
                    ["g"] = SafeNumber(color.g),
                    ["b"] = SafeNumber(color.b),
                    ["a"] = SafeNumber(color.a),
                    ["hex"] = "#" + ColorUtility.ToHtmlStringRGBA(color)
                };
            }

            if (value is Color32 color32)
            {
                Color expanded = color32;
                return Value(expanded, options, depth);
            }

            if (value is Rect rect)
            {
                return new JObject
                {
                    ["x"] = SafeNumber(rect.x),
                    ["y"] = SafeNumber(rect.y),
                    ["width"] = SafeNumber(rect.width),
                    ["height"] = SafeNumber(rect.height)
                };
            }

            if (value is Bounds bounds)
            {
                return new JObject
                {
                    ["center"] = Value(bounds.center, options, depth + 1),
                    ["size"] = Value(bounds.size, options, depth + 1),
                    ["extents"] = Value(bounds.extents, options, depth + 1)
                };
            }

            if (value is LayerMask layerMask) return new JValue(layerMask.value);
            if (value is AnimationCurve curve)
            {
                return new JObject
                {
                    ["length"] = curve.length,
                    ["preWrapMode"] = curve.preWrapMode.ToString(),
                    ["postWrapMode"] = curve.postWrapMode.ToString(),
                    ["keys"] = new JArray(Bounded(curve.keys, options, depth))
                };
            }

            if (value is Keyframe keyframe)
            {
                return new JObject
                {
                    ["time"] = SafeNumber(keyframe.time),
                    ["value"] = SafeNumber(keyframe.value),
                    ["inTangent"] = SafeNumber(keyframe.inTangent),
                    ["outTangent"] = SafeNumber(keyframe.outTangent)
                };
            }

            if (value is Gradient gradient)
            {
                return new JObject
                {
                    ["colorKeys"] = new JArray(Bounded(gradient.colorKeys, options, depth)),
                    ["alphaKeys"] = new JArray(Bounded(gradient.alphaKeys, options, depth)),
                    ["mode"] = gradient.mode.ToString()
                };
            }

            if (value is Object unityObject) return UnityObject(unityObject, options, depth);

            if (value is IDictionary dictionary) return Dictionary(dictionary, options, depth);
            if (value is IEnumerable enumerable && !(value is string)) return Enumerable(enumerable, options, depth);

            return new JObject
            {
                ["type"] = type.Name,
                ["value"] = Truncate(SafeToString(value), options.MaxStringLength)
            };
        }

        /// <summary>
        /// Describes a <see cref="UnityEngine.Object"/> as a reference - name, type, asset path and
        /// instance id - rather than serialising its contents. Callers that genuinely need the
        /// contents ask for them explicitly via <see cref="CucliReflection"/>.
        /// </summary>
        public static JObject UnityObject(Object value, Options options, int depth)
        {
            options = options ?? Options.Default;
            var result = new JObject
            {
                ["name"] = value.name,
                ["type"] = value.GetType().Name
            };

            if (value is Component component)
            {
                result["gameObject"] = component.gameObject.name;
                result["gameObjectInstanceId"] = component.gameObject.GetInstanceID();
            }
            else if (value is GameObject gameObject)
            {
                result["instanceId"] = gameObject.GetInstanceID();
                result["scene"] = gameObject.scene.IsValid() ? gameObject.scene.name : null;
            }

            if (value is ScriptableObject)
            {
                result["instanceId"] = value.GetInstanceID();
            }

            string assetPath = CucliAssets.GetAssetPath(value);
            if (!string.IsNullOrEmpty(assetPath))
            {
                result["assetPath"] = assetPath;
                result["guid"] = CucliAssets.GetGuid(value);
            }

            if (options.IncludeComponentValues && depth < options.MaxDepth)
            {
                if (value is Component valueComponent)
                {
                    result["components"] = CucliDescribe.Components(valueComponent.gameObject, options, depth + 1);
                }
            }

            return result;
        }

        /// <summary>Compact component list for a GameObject: type, enabled state and asset path for scripts.</summary>
        public static JArray Components(GameObject gameObject, Options options, int depth)
        {
            var result = new JArray();
            if (gameObject == null) return result;

            Component[] components = gameObject.GetComponents<Component>();
            int limit = Math.Min(components.Length, (options ?? Options.Default).MaxItems);
            for (int index = 0; index < limit; index++)
            {
                Component component = components[index];
                if (component == null)
                {
                    result.Add(new JObject { ["type"] = "<missing script>", ["missing"] = true });
                    continue;
                }

                var entry = new JObject
                {
                    ["type"] = component.GetType().Name,
                    ["fullType"] = component.GetType().FullName
                };

                var behaviour = component as Behaviour;
                if (behaviour != null) entry["enabled"] = behaviour.enabled;

                var renderer = component as Renderer;
                if (renderer != null)
                {
                    entry["enabled"] = renderer.enabled;
                    entry["materials"] = renderer.sharedMaterials != null
                        ? renderer.sharedMaterials.Length
                        : 0;
                }

                string scriptPath = CucliAssets.GetScriptAssetPath(component);
                if (!string.IsNullOrEmpty(scriptPath)) entry["script"] = scriptPath;
                result.Add(entry);
            }

            return result;
        }

        private static JToken Dictionary(IDictionary dictionary, Options options, int depth)
        {
            var result = new JObject();
            int count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= options.MaxItems)
                {
                    result["$truncated"] = dictionary.Count - options.MaxItems;
                    break;
                }

                string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "null";
                result[key] = Value(entry.Value, options, depth + 1);
            }

            return result;
        }

        private static JToken Enumerable(IEnumerable enumerable, Options options, int depth)
        {
            // Strings already handled; Unity types such as Mesh are too large to enumerate safely.
            var array = new JArray();
            int count = 0;
            int total = 0;
            foreach (object item in enumerable)
            {
                total++;
                if (count < options.MaxItems)
                {
                    array.Add(Value(item, options, depth + 1));
                    count++;
                }
            }

            if (total > count)
            {
                return new JObject
                {
                    ["count"] = total,
                    ["returned"] = count,
                    ["$truncated"] = total - count,
                    ["items"] = array
                };
            }

            return array;
        }

        private static JArray Bounded(IEnumerable source, Options options, int depth)
        {
            var array = new JArray();
            if (source == null) return array;
            int count = 0;
            foreach (object item in source)
            {
                if (count++ >= options.MaxItems) break;
                array.Add(Value(item, options, depth + 1));
            }

            return array;
        }

        private static JToken Vector(params float[] values)
        {
            var array = new JArray();
            foreach (float value in values) array.Add(SafeNumber(value));
            return array;
        }

        private static JValue SafeNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return new JValue(value.ToString());
            return new JValue(value);
        }

        public static string Truncate(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.Length <= limit) return value;
            return value.Substring(0, limit) + "...<truncated " + (value.Length - limit) + " chars>";
        }

        /// <summary>
        /// Calls <c>ToString()</c> defensively. Some Unity properties throw when read outside their
        /// valid state, and one bad property must not fail an otherwise useful inspection.
        /// </summary>
        public static string SafeToString(object value)
        {
            if (value == null) return string.Empty;
            try
            {
                return value.ToString();
            }
            catch (Exception exception)
            {
                return "<ToString threw: " + exception.GetBaseException().Message + ">";
            }
        }
    }
}
