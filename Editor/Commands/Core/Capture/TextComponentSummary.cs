using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// Summarises a text-like UI component through an allow-list of fields, never by serialising the
    /// component.
    ///
    /// <b>This is a hard project rule, not a preference.</b> Reading a <c>TextMeshProUGUI</c> wholesale
    /// drags in <c>textInfo</c>, the generated mesh, per-character vertices and UVs, and material
    /// instances - megabytes of runtime data that will stall a terminal or an agent for no benefit.
    /// So this type reads exactly these members and nothing else:
    ///
    /// <c>text</c>, <c>font</c>, <c>fontSize</c>, <c>alignment</c>, <c>enableWordWrapping</c>,
    /// <c>overflowMode</c>, <c>lineSpacing</c>, <c>margin</c> - plus a
    /// <see cref="TryDescribeRect"/> summary of the RectTransform.
    ///
    /// TMP is reached reflectively because this package has no TextMeshPro reference and no asmdef of
    /// its own for it; the same allow-list therefore also covers <c>UnityEngine.UI.Text</c> and any
    /// other component that happens to expose a <c>text</c> string, which is why the filename says
    /// "text" rather than "tmp".
    /// </summary>
    internal static class TextComponentSummary
    {
        /// <summary>Member names that must never be read, whatever else the component exposes.</summary>
        private static readonly string[] Forbidden =
        {
            "textInfo", "mesh", "meshFilter", "characterInfo", "vertices", "uvs", "triangles",
            "materials", "material", "fontMaterial", "fontSharedMaterial", "spriteAsset",
            "renderedText", "renderedTextArray", "textContainer", "layoutMesh"
        };

        /// <summary>True when the component looks like a text renderer worth summarising.</summary>
        internal static bool IsTextLike(Component component)
        {
            if (component == null) return false;
            Type type = component.GetType();
            if (type.GetProperty("text") != null || type.GetField("text") != null) return true;

            // A TMP subclass may hide the property; walking the hierarchy covers that without naming TMP.
            for (Type current = type.BaseType; current != null; current = current.BaseType)
            {
                if (string.Equals(current.Name, "TMP_Text", StringComparison.Ordinal)) return true;
                if (string.Equals(current.Name, "Text", StringComparison.Ordinal)
                    && current.Namespace != null
                    && current.Namespace.StartsWith("UnityEngine.UI", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds the allow-listed summary. Returns false with a reason when the component exposes no
        /// readable text at all, so callers can say "not a text element" instead of emitting an empty object.
        /// </summary>
        internal static bool TryDescribe(Component component, out JObject summary, out string reason)
        {
            summary = null;
            reason = null;
            if (component == null)
            {
                reason = "no component";
                return false;
            }

            string text = ReadString(component, "text");
            if (text == null && !IsTextLike(component))
            {
                reason = component.GetType().Name + " exposes no 'text' member, so it is not treated as a text element.";
                return false;
            }

            summary = new JObject
            {
                ["componentType"] = component.GetType().Name,
                ["componentFullType"] = component.GetType().FullName,
                ["text"] = CucliDescribe.Truncate(text ?? string.Empty, 512)
            };

            if (text != null && text.Length > 512)
            {
                summary["textTruncated"] = true;
                summary["textLength"] = text.Length;
            }

            // font: report the asset name and size, never the font asset itself.
            object font = Read(component, "font");
            var fontObject = font as UnityEngine.Object;
            if (fontObject != null)
            {
                summary["font"] = fontObject.name;
                summary["fontAssetPath"] = string.IsNullOrEmpty(CucliAssets.GetAssetPath(fontObject))
                    ? null
                    : CucliAssets.GetAssetPath(fontObject);
            }
            else if (font != null)
            {
                summary["font"] = CucliDescribe.SafeToString(font);
            }

            object fontSize = Read(component, "fontSize");
            if (fontSize != null) summary["fontSize"] = CucliDescribe.Value(fontSize, null, 0);

            object alignment = Read(component, "alignment");
            if (alignment != null) summary["alignment"] = CucliDescribe.SafeToString(alignment);

            // enableWordWrapping was renamed to textWrappingMode in newer TMP; read whichever exists and
            // say which one answered, so a caller is never guessing what the boolean means.
            object wordWrapping = Read(component, "enableWordWrapping");
            if (wordWrapping != null)
            {
                summary["enableWordWrapping"] = CucliDescribe.Value(wordWrapping, null, 0);
                summary["wordWrappingSource"] = "enableWordWrapping";
            }
            else
            {
                object wrappingMode = Read(component, "textWrappingMode");
                if (wrappingMode != null)
                {
                    summary["enableWordWrapping"] = CucliDescribe.SafeToString(wrappingMode);
                    summary["wordWrappingSource"] = "textWrappingMode";
                }
            }

            object overflow = Read(component, "overflowMode");
            if (overflow != null) summary["overflowMode"] = CucliDescribe.SafeToString(overflow);

            object lineSpacing = Read(component, "lineSpacing");
            if (lineSpacing != null) summary["lineSpacing"] = CucliDescribe.Value(lineSpacing, null, 0);

            object margin = Read(component, "margin");
            if (margin != null) summary["margin"] = CucliDescribe.Value(margin, null, 0);

            JObject rect;
            if (TryDescribeRect(component.transform as RectTransform, out rect))
            {
                summary["rectTransform"] = rect;
            }

            summary["note"] = "Only the allow-listed members above were read. TextMeshPro runtime data "
                + "(textInfo, mesh vertices, UVs, character info, material instances) is never serialised, "
                + "because it is large and carries no information a caller can act on.";
            return true;
        }

        /// <summary>
        /// The layout facts that actually matter, and nothing derived: anchors, pivot, anchored
        /// position, size delta and the resolved rect.
        /// </summary>
        internal static bool TryDescribeRect(RectTransform rect, out JObject summary)
        {
            summary = null;
            if (rect == null) return false;

            summary = new JObject
            {
                ["anchorMin"] = CucliDescribe.Value(rect.anchorMin, null, 0),
                ["anchorMax"] = CucliDescribe.Value(rect.anchorMax, null, 0),
                ["pivot"] = CucliDescribe.Value(rect.pivot, null, 0),
                ["anchoredPosition"] = CucliDescribe.Value(rect.anchoredPosition, null, 0),
                ["sizeDelta"] = CucliDescribe.Value(rect.sizeDelta, null, 0),
                ["offsetMin"] = CucliDescribe.Value(rect.offsetMin, null, 0),
                ["offsetMax"] = CucliDescribe.Value(rect.offsetMax, null, 0),
                ["rect"] = new JObject
                {
                    ["x"] = rect.rect.x,
                    ["y"] = rect.rect.y,
                    ["width"] = rect.rect.width,
                    ["height"] = rect.rect.height
                },
                ["localScale"] = CucliDescribe.Value(rect.localScale, null, 0),
                ["activeInHierarchy"] = rect.gameObject.activeInHierarchy
            };

            return true;
        }

        /// <summary>
        /// Screen point at the centre of a RectTransform, using the Canvas camera when the canvas is not
        /// a Screen Space Overlay. This is how a caller turns "this button" into "click here".
        /// </summary>
        internal static bool TryGetScreenCenter(RectTransform rect, out Vector2 screenPoint, out string error)
        {
            screenPoint = Vector2.zero;
            error = null;
            if (rect == null)
            {
                error = "no RectTransform";
                return false;
            }

            Camera camera = null;
            try
            {
                Canvas canvas = rect.GetComponentInParent<Canvas>();
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                {
                    camera = canvas.worldCamera;
                }
            }
            catch (Exception exception)
            {
                error = "Canvas lookup failed: " + exception.GetBaseException().Message;
                return false;
            }

            screenPoint = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
            return true;
        }

        private static object Read(Component component, string member)
        {
            if (component == null || string.IsNullOrEmpty(member)) return null;
            if (Forbidden.Contains(member, StringComparer.Ordinal)) return null;

            // CucliReflection.Get is the only read path: it reports a throwing property as a readable
            // error string instead of letting one bad member abort the whole summary.
            string error;
            return CucliReflection.Get(component, member, out error);
        }

        private static string ReadString(Component component, string member)
        {
            object value = Read(component, member);
            return value as string;
        }
    }
}
