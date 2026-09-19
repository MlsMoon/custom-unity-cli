using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Reads and writes Unity objects through <see cref="SerializedObject"/>.
    ///
    /// This is what makes <c>cucli ... set</c> behave like a real Editor edit rather than a
    /// reflection poke: writes register Undo, become prefab property overrides, mark the scene or
    /// asset dirty and honour <c>[SerializeField]</c> visibility rules. Reflection is only the
    /// fallback for members Unity does not serialise.
    /// </summary>
    public static class CucliSerialized
    {
        /// <summary>
        /// Writes <paramref name="value"/> to <paramref name="memberPath"/> on
        /// <paramref name="target"/>. Returns false with <paramref name="error"/> when the member
        /// is not serialised, so the caller can fall back to reflection.
        /// </summary>
        public static bool TrySet(
            UnityEngine.Object target,
            string memberPath,
            JToken value,
            string undoLabel,
            out string error,
            out bool handled)
        {
            error = null;
            handled = false;
            if (target == null || string.IsNullOrWhiteSpace(memberPath)) return false;

            SerializedObject serialized;
            SerializedProperty property;
            try
            {
                serialized = new SerializedObject(target);
                property = serialized.FindProperty(memberPath.Trim());
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            if (property == null) return false;
            handled = true;

            Undo.RecordObject(target, undoLabel ?? "cucli set " + memberPath);

            string assignError;
            if (!Assign(property, value, out assignError))
            {
                error = assignError;
                return false;
            }

            try
            {
                serialized.ApplyModifiedProperties();
            }
            catch (Exception exception)
            {
                error = "Unity rejected the change: " + exception.GetBaseException().Message;
                return false;
            }

            EditorUtility.SetDirty(target);
            return true;
        }

        public static bool TryGet(
            UnityEngine.Object target,
            string memberPath,
            out SerializedProperty property,
            out SerializedObject serialized)
        {
            property = null;
            serialized = null;
            if (target == null || string.IsNullOrWhiteSpace(memberPath)) return false;

            try
            {
                serialized = new SerializedObject(target);
                property = serialized.FindProperty(memberPath.Trim());
                return property != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsLeafGeneric(SerializedProperty property)
        {
            return property.propertyType == SerializedPropertyType.Generic && !property.isArray;
        }

        /// <summary>Assigns a JSON token to a serialized property based on its declared type.</summary>
        public static bool Assign(SerializedProperty property, JToken token, out string error)
        {
            error = null;
            if (property == null)
            {
                error = "the property no longer exists.";
                return false;
            }

            if (token == null || token.Type == JTokenType.Null)
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    property.objectReferenceValue = null;
                    return true;
                }

                error = "null is not valid for a " + property.propertyType + " property. "
                    + "Use 'remove' or 'clear' for collections.";
                return false;
            }

            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        property.longValue = ReadLong(token, out error);
                        return error == null;

                    case SerializedPropertyType.LayerMask:
                    {
                        if (token.Type == JTokenType.String)
                        {
                            string text = token.Value<string>() ?? string.Empty;
                            int byName;
                            long numeric;
                            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                            {
                                property.intValue = (int)numeric;
                                return true;
                            }

                            byName = LayerMask.GetMask(text.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries));
                            property.intValue = byName;
                            return true;
                        }

                        property.intValue = (int)ReadLong(token, out error);
                        return error == null;
                    }

                    case SerializedPropertyType.Enum:
                    {
                        if (token.Type == JTokenType.String)
                        {
                            string text = (token.Value<string>() ?? string.Empty).Trim();
                            int dot = text.LastIndexOf('.');
                            if (dot >= 0) text = text.Substring(dot + 1);

                            string[] names = property.enumNames;
                            int index = Array.FindIndex(
                                names,
                                name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
                            if (index < 0)
                            {
                                long numeric;
                                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                                {
                                    property.longValue = numeric;
                                    return true;
                                }

                                error = "'" + text + "' is not one of: " + string.Join(", ", names) + ".";
                                return false;
                            }

                            property.enumValueIndex = index;
                            return true;
                        }

                        property.longValue = ReadLong(token, out error);
                        return error == null;
                    }

                    case SerializedPropertyType.Boolean:
                        property.boolValue = ReadBool(token, out error);
                        return error == null;

                    case SerializedPropertyType.Float:
                        property.floatValue = ReadFloat(token, out error);
                        return error == null;

                    case SerializedPropertyType.String:
                        property.stringValue = token.Type == JTokenType.String
                            ? token.Value<string>()
                            : token.ToString(Newtonsoft.Json.Formatting.None);
                        return true;

                    case SerializedPropertyType.Color:
                    {
                        string colorError;
                        object converted = CucliValues.Convert(token, typeof(Color), out colorError);
                        if (colorError != null)
                        {
                            error = colorError;
                            return false;
                        }

                        property.colorValue = (Color)converted;
                        return true;
                    }

                    case SerializedPropertyType.ObjectReference:
                    {
                        Type declared = property.objectReferenceValue != null
                            ? property.objectReferenceValue.GetType()
                            : typeof(UnityEngine.Object);
                        string referenceError;
                        object reference = CucliValues.ReadUnityObject(token, declared, out referenceError);
                        if (referenceError != null)
                        {
                            error = referenceError;
                            return false;
                        }

                        property.objectReferenceValue = (UnityEngine.Object)reference;
                        return true;
                    }

                    case SerializedPropertyType.Vector2:
                        return AssignVector(property, token, 2, out error);

                    case SerializedPropertyType.Vector3:
                        return AssignVector(property, token, 3, out error);

                    case SerializedPropertyType.Vector4:
                        return AssignVector(property, token, 4, out error);

                    case SerializedPropertyType.Vector2Int:
                    {
                        string vectorError;
                        object value = CucliValues.Convert(token, typeof(Vector2Int), out vectorError);
                        if (vectorError != null)
                        {
                            error = vectorError;
                            return false;
                        }

                        property.vector2IntValue = (Vector2Int)value;
                        return true;
                    }

                    case SerializedPropertyType.Vector3Int:
                    {
                        string vectorError;
                        object value = CucliValues.Convert(token, typeof(Vector3Int), out vectorError);
                        if (vectorError != null)
                        {
                            error = vectorError;
                            return false;
                        }

                        property.vector3IntValue = (Vector3Int)value;
                        return true;
                    }

                    case SerializedPropertyType.Quaternion:
                    {
                        string quaternionError;
                        object value = CucliValues.Convert(token, typeof(Quaternion), out quaternionError);
                        if (quaternionError != null)
                        {
                            error = quaternionError;
                            return false;
                        }

                        property.quaternionValue = (Quaternion)value;
                        return true;
                    }

                    case SerializedPropertyType.Rect:
                    {
                        string rectError;
                        object value = CucliValues.Convert(token, typeof(Rect), out rectError);
                        if (rectError != null)
                        {
                            error = rectError;
                            return false;
                        }

                        property.rectValue = (Rect)value;
                        return true;
                    }

                    case SerializedPropertyType.Bounds:
                    {
                        string boundsError;
                        object value = CucliValues.Convert(token, typeof(Bounds), out boundsError);
                        if (boundsError != null)
                        {
                            error = boundsError;
                            return false;
                        }

                        property.boundsValue = (Bounds)value;
                        return true;
                    }

                    case SerializedPropertyType.AnimationCurve:
                    {
                        string curveError;
                        object value = CucliValues.Convert(token, typeof(AnimationCurve), out curveError);
                        if (curveError != null)
                        {
                            error = curveError;
                            return false;
                        }

                        property.animationCurveValue = (AnimationCurve)value;
                        return true;
                    }

                    case SerializedPropertyType.ArraySize:
                    {
                        int size = (int)ReadLong(token, out error);
                        if (error != null) return false;
                        if (size < 0)
                        {
                            error = "array size cannot be negative.";
                            return false;
                        }

                        property.arraySize = size;
                        return true;
                    }

                    default:
                        error = "the property type " + property.propertyType
                            + " cannot be assigned through the CLI; it is write-only, fixed-size or managed by Unity.";
                        return false;
                }
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        private static bool AssignVector(SerializedProperty property, JToken token, int dimensions, out string error)
        {
            Type type = dimensions == 2
                ? typeof(Vector2)
                : dimensions == 3 ? typeof(Vector3) : typeof(Vector4);
            object converted = CucliValues.Convert(token, type, out error);
            if (error != null) return false;

            if (dimensions == 2) property.vector2Value = (Vector2)converted;
            else if (dimensions == 3) property.vector3Value = (Vector3)converted;
            else property.vector4Value = (Vector4)converted;
            return true;
        }

        private static long ReadLong(JToken token, out string error)
        {
            error = null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<long>();
            if (token.Type == JTokenType.Boolean) return token.Value<bool>() ? 1L : 0L;
            string text = token.Type == JTokenType.String ? token.Value<string>() : null;
            long parsed;
            if (text != null && long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            error = "cannot read an integer from " + token.ToString(Newtonsoft.Json.Formatting.None) + ".";
            return 0L;
        }

        private static bool ReadBool(JToken token, out string error)
        {
            error = null;
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
            return false;
        }

        private static float ReadFloat(JToken token, out string error)
        {
            error = null;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) return token.Value<float>();
            string text = token.Type == JTokenType.String ? token.Value<string>() : null;
            float parsed;
            if (text != null && float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            error = "cannot read a number from " + token.ToString(Newtonsoft.Json.Formatting.None) + ".";
            return 0f;
        }

        /// <summary>
        /// Serialises a serialized property with the same size discipline as
        /// <see cref="CucliDescribe"/>. Arrays report their length before their contents.
        /// </summary>
        public static JToken Read(SerializedProperty property, CucliDescribe.Options options, int depth)
        {
            options = options ?? CucliDescribe.Options.Default;
            if (property == null) return JValue.CreateNull();
            if (depth > options.MaxDepth)
            {
                return new JObject { ["$truncated"] = "maximum depth reached", ["type"] = property.propertyType.ToString() };
            }

            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer: return new JValue(property.longValue);
                    case SerializedPropertyType.LayerMask: return new JValue(property.intValue);
                    case SerializedPropertyType.Boolean: return new JValue(property.boolValue);
                    case SerializedPropertyType.Float: return new JValue(property.floatValue);
                    case SerializedPropertyType.String: return new JValue(property.stringValue);
                    case SerializedPropertyType.Color: return CucliDescribe.Value(property.colorValue, options, depth);
                    case SerializedPropertyType.ObjectReference:
                        return property.objectReferenceValue == null
                            ? JValue.CreateNull()
                            : CucliDescribe.UnityObject(property.objectReferenceValue, options, depth);
                    case SerializedPropertyType.Vector2: return CucliDescribe.Value(property.vector2Value, options, depth);
                    case SerializedPropertyType.Vector3: return CucliDescribe.Value(property.vector3Value, options, depth);
                    case SerializedPropertyType.Vector4: return CucliDescribe.Value(property.vector4Value, options, depth);
                    case SerializedPropertyType.Vector2Int: return CucliDescribe.Value(property.vector2IntValue, options, depth);
                    case SerializedPropertyType.Vector3Int: return CucliDescribe.Value(property.vector3IntValue, options, depth);
                    case SerializedPropertyType.Rect: return CucliDescribe.Value(property.rectValue, options, depth);
                    case SerializedPropertyType.Bounds: return CucliDescribe.Value(property.boundsValue, options, depth);
                    case SerializedPropertyType.Quaternion: return CucliDescribe.Value(property.quaternionValue, options, depth);
                    case SerializedPropertyType.AnimationCurve:
                        return property.animationCurveValue == null
                            ? JValue.CreateNull()
                            : CucliDescribe.Value(property.animationCurveValue, options, depth);
                    case SerializedPropertyType.Enum:
                    {
                        string[] names = property.enumNames;
                        if (names != null && property.enumValueIndex >= 0 && property.enumValueIndex < names.Length)
                        {
                            return new JValue(names[property.enumValueIndex]);
                        }

                        return new JValue(property.longValue);
                    }

                    case SerializedPropertyType.ArraySize: return new JValue(property.arraySize);
                }

                if (property.isArray)
                {
                    int total = property.arraySize;
                    int returned = Math.Min(total, options.MaxItems);
                    var items = new JArray();
                    for (int index = 0; index < returned; index++)
                    {
                        items.Add(Read(property.GetArrayElementAtIndex(index), options, depth + 1));
                    }

                    if (total > returned)
                    {
                        return new JObject
                        {
                            ["count"] = total,
                            ["returned"] = returned,
                            ["$truncated"] = total - returned,
                            ["items"] = items
                        };
                    }

                    return items;
                }

                if (property.hasChildren)
                {
                    var result = new JObject();
                    SerializedProperty child = property.Copy();
                    SerializedProperty end = child.GetEndProperty();
                    bool enterChildren = true;
                    int emitted = 0;
                    while (child.NextVisible(enterChildren) && !SerializedProperty.EqualContents(child, end))
                    {
                        enterChildren = false;
                        if (emitted++ >= options.MaxItems)
                        {
                            result["$truncated"] = "more members omitted";
                            break;
                        }

                        result[child.name] = Read(child, options, depth + 1);
                    }

                    return result;
                }

                return new JObject
                {
                    ["type"] = property.propertyType.ToString(),
                    ["$unsupported"] = true
                };
            }
            catch (Exception exception)
            {
                return new JObject
                {
                    ["$error"] = exception.GetBaseException().Message,
                    ["propertyPath"] = property.propertyPath
                };
            }
        }
    }
}
