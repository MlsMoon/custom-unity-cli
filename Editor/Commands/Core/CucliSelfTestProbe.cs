using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// An in-memory probe type used exclusively by <c>cucli selftest core</c>.
    ///
    /// It exists so the serialization helpers can be tested against a real
    /// <see cref="UnityEngine.Object"/> with real serialized fields, without touching any asset, any
    /// scene, or the user's dirty flags. <see cref="ScriptableObject.CreateInstance{T}"/> gives a
    /// throwaway instance that is never saved and never enters the AssetDatabase.
    ///
    /// One field of each common shape, because the bug this suite is designed to catch is a
    /// conversion that works for floats and silently corrupts vectors, colours or object references.
    ///
    /// It lives in its own file because the project forbids declaring a second ScriptableObject
    /// beside another type.
    /// </summary>
    internal sealed class CucliSelfTestProbe : ScriptableObject
    {
        public int intValue;
        public long longValue;
        public float floatValue;
        public double doubleValue;
        public bool boolValue;
        public string stringValue;
        public Vector2 vector2Value;
        public Vector3 vector3Value;
        public Vector4 vector4Value;
        public Quaternion quaternionValue;
        public Color colorValue;
        public Rect rectValue;
        public Bounds boundsValue;
        public LayerMask layerMaskValue;
        public Object objectValue;
        public int[] intArrayValue;
        public string[] stringArrayValue;
    }
}
