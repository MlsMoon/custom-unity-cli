using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>line_*</c> (13) and <c>trail_*</c> (8) families of the <c>vfx</c> group.
    ///
    /// Both wrap engine components (<see cref="LineRenderer"/>, <see cref="TrailRenderer"/>) and need
    /// no optional package.
    ///
    /// One contract detail is easy to get wrong and is preserved deliberately: <c>line_create_*</c>
    /// does <em>not</em> create a <see cref="LineRenderer"/>. It only writes positions, width and
    /// colour onto one that already exists, and reports a precise error when there is none. A caller
    /// expecting "create a circle" to conjure a component would otherwise get a silent no-op.
    /// </summary>
    internal static class VfxLineTrailCommand
    {
        internal static CucliCommandResult HandleLine(CucliCommandContext context, string action)
        {
            switch (action)
            {
                case "line_get_info": return LineInfo(context);
                case "line_set_positions": return LineSetPositions(context);
                case "line_add_position": return LineAddPosition(context);
                case "line_set_position": return LineSetPosition(context);
                case "line_set_width": return LineSetWidth(context);
                case "line_set_color": return LineSetColor(context);
                case "line_set_material": return LineSetMaterial(context);
                case "line_set_properties": return LineSetProperties(context);
                case "line_clear": return LineClear(context);
                case "line_create_line": return LineCreateLine(context);
                case "line_create_circle": return LineCreateCircle(context);
                case "line_create_arc": return LineCreateArc(context);
                case "line_create_bezier": return LineCreateBezier(context);
                default:
                    return CucliCommandResult.Unsupported(action, VfxCommand.LineActions);
            }
        }

        internal static CucliCommandResult HandleTrail(CucliCommandContext context, string action)
        {
            switch (action)
            {
                case "trail_get_info": return TrailInfo(context);
                case "trail_set_time": return TrailSetTime(context);
                case "trail_set_width": return TrailSetWidth(context);
                case "trail_set_color": return TrailSetColor(context);
                case "trail_set_material": return TrailSetMaterial(context);
                case "trail_set_properties": return TrailSetProperties(context);
                case "trail_clear": return TrailClear(context);
                case "trail_emit": return TrailEmit(context);
                default:
                    return CucliCommandResult.Unsupported(action, VfxCommand.TrailActions);
            }
        }

        // ================================================================== line

        private static CucliCommandResult LineInfo(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_get_info", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliDescribe.Options options = CucliDescribe.OptionsFrom(context.Input);
            var positions = new JArray();
            int total = line.positionCount;
            int limit = Math.Min(total, options.MaxItems);
            for (int index = 0; index < limit; index++)
            {
                positions.Add(CucliDescribe.Value(line.GetPosition(index), options, 0));
            }

            return CucliCommandResult.Ok(new
            {
                gameObject = line.gameObject.name,
                path = CucliObjectResolver.HierarchyPath(line.gameObject, true),
                positionCount = total,
                positions,
                positionsReturned = limit,
                positionsTruncated = total > limit,
                startWidth = line.startWidth,
                endWidth = line.endWidth,
                loop = line.loop,
                useWorldSpace = line.useWorldSpace,
                alignment = line.alignment.ToString(),
                textureMode = line.textureMode.ToString(),
                numCornerVertices = line.numCornerVertices,
                numCapVertices = line.numCapVertices,
                generateLightingData = line.generateLightingData,
                material = line.sharedMaterial == null ? null : line.sharedMaterial.name,
                renderer = VfxSupport.DescribeRenderer(line)
            });
        }

        private static CucliCommandResult LineSetPositions(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_set_positions", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            JArray positions = args.GetArray("positions");
            if (positions == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "Positions array required：positions 必须是 JSON 数组。");
            }

            Undo.RecordObject(line, "cucli line_set_positions");
            line.positionCount = positions.Count;
            for (int index = 0; index < positions.Count; index++)
            {
                string vectorError;
                object converted = CucliValues.Convert(positions[index], typeof(Vector3), out vectorError);
                if (vectorError != null)
                {
                    return CucliCommandResult.Invalid("positions[" + index + "]", vectorError);
                }

                line.SetPosition(index, (Vector3)converted);
            }

            return CucliCommandResult.Ok(
                new { positionCount = line.positionCount }, "已写入 " + line.positionCount + " 个位置。");
        }

        private static CucliCommandResult LineAddPosition(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_add_position", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            if (!args.Has("position"))
            {
                return CucliCommandResult.Missing("position", "line_add_position 需要 position [x,y,z]。");
            }

            Undo.RecordObject(line, "cucli line_add_position");
            int index = line.positionCount;
            line.positionCount = index + 1;
            line.SetPosition(index, args.GetVector3("position"));

            return CucliCommandResult.Ok(
                new { index, positionCount = line.positionCount }, "已在索引 " + index + " 追加位置。");
        }

        private static CucliCommandResult LineSetPosition(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_set_position", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            if (!args.Has("position"))
            {
                return CucliCommandResult.Missing("position", "line_set_position 需要 position [x,y,z]。");
            }

            int index = args.GetInt("index", -1);
            if (index < 0 || index >= line.positionCount)
            {
                return CucliCommandResult.Invalid(
                    "index", "Invalid index " + index + "（当前 positionCount=" + line.positionCount + "）。");
            }

            Undo.RecordObject(line, "cucli line_set_position");
            line.SetPosition(index, args.GetVector3("position"));

            return CucliCommandResult.Ok(new { index, positionCount = line.positionCount });
        }

        private static CucliCommandResult LineSetWidth(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_set_width", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(line, "cucli line_set_width");

            VfxSupport.ApplyWidth(args, line, null, changes);

            if (args.Has("widthCurve"))
            {
                line.widthCurve = VfxSupport.ParseCurve(args.Token("widthCurve"), 1f);
                changes.Add("widthCurve");
            }

            if (args.Has("widthMultiplier")) { line.widthMultiplier = args.GetFloat("widthMultiplier"); changes.Add("widthMultiplier"); }

            return CucliCommandResult.Ok(null, changes.Message("line_set_width 没有收到任何已知键。"));
        }

        private static CucliCommandResult LineSetColor(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_set_color", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            var changes = new ChangeSet();
            Undo.RecordObject(line, "cucli line_set_color");
            // A line keeps its end alpha; only a trail dissolves. See ApplyColor.
            VfxSupport.ApplyColor(context.Input, line, null, false, changes);

            return CucliCommandResult.Ok(null, changes.Message("line_set_color 没有收到任何已知键。"));
        }

        private static CucliCommandResult LineSetMaterial(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_set_material", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string requested = VfxSupport.MaterialPath(context.Input);
            if (string.IsNullOrWhiteSpace(requested))
            {
                string resolved = VfxSupport.FindDefaultSurfaceMaterial(context, "line");
                if (resolved == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.MemberNotFound,
                        "未提供 materialPath，且工程里找不到可作为默认线材质的资产。"
                        + "请显式传 materialPath（例如 --material-path Assets/.../Line.mat）。",
                        new { searched = VfxSupport.DefaultSurfaceShaderNames });
                }

                requested = resolved;
            }

            string materialError;
            Material material = VfxSupport.LoadMaterial(requested, out materialError);
            if (material == null)
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, materialError, new { materialPath = requested });
            }

            Undo.RecordObject(line, "cucli line_set_material");
            line.sharedMaterial = material;

            return CucliCommandResult.Ok(
                new { materialPath = CucliAssets.GetAssetPath(material), material = material.name },
                "已设置线材质。");
        }

        private static CucliCommandResult LineSetProperties(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_set_properties", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(line, "cucli line_set_properties");

            if (args.Has("positions"))
            {
                CucliCommandResult positions = LineSetPositions(context);
                if (!positions.Success) return positions;
                changes.Add("positions");
            }
            else if (args.Has("positionCount"))
            {
                int count = Mathf.Max(0, args.GetInt("positionCount"));
                line.positionCount = count;
                changes.Add("positionCount");
            }

            VfxSupport.ApplyWidth(args, line, null, changes);
            VfxSupport.ApplyCommonRendererProperties(args, line, changes);

            string materialPath = VfxSupport.MaterialPath(args);
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                string materialError;
                Material material = VfxSupport.LoadMaterial(materialPath, out materialError);
                if (material == null) context.Warn(materialError);
                else { line.sharedMaterial = material; changes.Add("materialPath"); }
            }

            return CucliCommandResult.Ok(
                new { changes = changes.Names }, changes.Message("line_set_properties 没有收到任何已知键。"));
        }

        private static CucliCommandResult LineClear(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_clear", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            int cleared = line.positionCount;
            Undo.RecordObject(line, "cucli line_clear");
            line.positionCount = 0;

            return CucliCommandResult.Ok(
                new { cleared, positionCount = 0 }, "Cleared " + cleared + " positions");
        }

        // ---- shape writers (they write; they never add a component) -------------------------------

        private static CucliCommandResult LineCreateLine(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_create_line", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            if (!args.Has("start") || !args.Has("end"))
            {
                return CucliCommandResult.Missing("start/end", "line_create_line 需要 start 与 end [x,y,z]。");
            }

            Vector3 start = args.GetVector3("start");
            Vector3 end = args.GetVector3("end");
            var changes = new ChangeSet();

            Undo.RecordObject(line, "cucli line_create_line");
            ApplyPositions(line, new[] { start, end });
            ApplyShapeStyling(args, line, changes);

            return CucliCommandResult.Ok(
                new { positionCount = line.positionCount, start = CucliDescribe.Value(start, null, 0), end = CucliDescribe.Value(end, null, 0), changes = changes.Names },
                "已把现有 LineRenderer 写为一条线段。");
        }

        private static CucliCommandResult LineCreateCircle(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_create_circle", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            Vector3 center = args.GetVector3("center");
            float radius = args.GetFloat("radius", 1f);
            int segments = Mathf.Clamp(args.GetInt("segments", 32), 3, 512);
            Vector3 normal = Normalized(args.GetVector3("normal", Vector3.up));

            Vector3 axisA = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.99f ? Vector3.right : Vector3.up).normalized;
            Vector3 axisB = Vector3.Cross(normal, axisA).normalized;

            var points = new Vector3[segments];
            for (int index = 0; index < segments; index++)
            {
                float angle = (Mathf.PI * 2f) * index / segments;
                points[index] = center + (axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle)) * radius;
            }

            var changes = new ChangeSet();
            Undo.RecordObject(line, "cucli line_create_circle");
            ApplyPositions(line, points);
            line.loop = true;
            changes.Add("loop");
            ApplyShapeStyling(args, line, changes);

            return CucliCommandResult.Ok(
                new { positionCount = line.positionCount, segments, radius, loop = true, changes = changes.Names },
                "已把现有 LineRenderer 写为一个圆（" + segments + " 段）。");
        }

        private static CucliCommandResult LineCreateArc(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_create_arc", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            Vector3 center = args.GetVector3("center");
            float radius = args.GetFloat("radius", 1f);
            int segments = Mathf.Clamp(args.GetInt("segments", 16), 2, 512);
            Vector3 normal = Normalized(args.GetVector3("normal", Vector3.up));
            float startAngle = args.GetFloat("startAngle", 0f) * Mathf.Deg2Rad;
            float endAngle = args.GetFloat("endAngle", 180f) * Mathf.Deg2Rad;

            Vector3 axisA = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.99f ? Vector3.right : Vector3.up).normalized;
            Vector3 axisB = Vector3.Cross(normal, axisA).normalized;

            var points = new Vector3[segments + 1];
            for (int index = 0; index <= segments; index++)
            {
                float angle = Mathf.Lerp(startAngle, endAngle, (float)index / segments);
                points[index] = center + (axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle)) * radius;
            }

            var changes = new ChangeSet();
            Undo.RecordObject(line, "cucli line_create_arc");
            ApplyPositions(line, points);
            line.loop = false;
            changes.Add("loop");
            ApplyShapeStyling(args, line, changes);

            return CucliCommandResult.Ok(
                new { positionCount = line.positionCount, segments, radius, changes = changes.Names },
                "已把现有 LineRenderer 写为一段圆弧。");
        }

        private static CucliCommandResult LineCreateBezier(CucliCommandContext context)
        {
            LineRenderer line;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "line_create_bezier", out line, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            if (!args.Has("start") || !args.Has("end"))
            {
                return CucliCommandResult.Missing("start/end", "line_create_bezier 需要 start 与 end [x,y,z]。");
            }

            Vector3 start = args.GetVector3("start");
            Vector3 end = args.GetVector3("end");
            JToken control1Token = args.Token("controlPoint1") ?? args.Token("control1");
            if (control1Token == null)
            {
                return CucliCommandResult.Missing("controlPoint1", "line_create_bezier 需要 controlPoint1（别名 control1）。");
            }

            string controlError;
            object parsed = CucliValues.Convert(control1Token, typeof(Vector3), out controlError);
            if (controlError != null) return CucliCommandResult.Invalid("controlPoint1", controlError);
            Vector3 control1 = (Vector3)parsed;

            JToken control2Token = args.Token("controlPoint2") ?? args.Token("control2");
            bool cubic = control2Token != null;
            Vector3 control2 = Vector3.zero;
            if (cubic)
            {
                parsed = CucliValues.Convert(control2Token, typeof(Vector3), out controlError);
                if (controlError != null) return CucliCommandResult.Invalid("controlPoint2", controlError);
                control2 = (Vector3)parsed;
            }

            int segments = Mathf.Clamp(args.GetInt("segments", 32), 2, 512);
            var points = new Vector3[segments + 1];
            for (int index = 0; index <= segments; index++)
            {
                float t = (float)index / segments;
                points[index] = cubic
                    ? CubicBezier(start, control1, control2, end, t)
                    : QuadraticBezier(start, control1, end, t);
            }

            var changes = new ChangeSet();
            Undo.RecordObject(line, "cucli line_create_bezier");
            ApplyPositions(line, points);
            ApplyShapeStyling(args, line, changes);

            return CucliCommandResult.Ok(new
            {
                positionCount = line.positionCount,
                segments,
                cubic,
                changes = changes.Names
            }, cubic ? "已写为三次贝塞尔曲线。" : "已写为二次贝塞尔曲线（未给 controlPoint2）。");
        }

        /// <summary>Shared tail of the four shape writers: width and colour, both optional.</summary>
        private static void ApplyShapeStyling(CucliArgs args, LineRenderer line, ChangeSet changes)
        {
            VfxSupport.ApplyWidth(args, line, null, changes);
            VfxSupport.ApplyColor(args, line, null, false, changes);
        }

        private static void ApplyPositions(LineRenderer line, Vector3[] points)
        {
            line.positionCount = points.Length;
            for (int index = 0; index < points.Length; index++) line.SetPosition(index, points[index]);
        }

        private static Vector3 QuadraticBezier(Vector3 a, Vector3 b, Vector3 c, float t)
        {
            float u = 1f - t;
            return (u * u * a) + (2f * u * t * b) + (t * t * c);
        }

        private static Vector3 CubicBezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float u = 1f - t;
            return (u * u * u * a) + (3f * u * u * t * b) + (3f * u * t * t * c) + (t * t * t * d);
        }

        private static Vector3 Normalized(Vector3 value)
        {
            return value.sqrMagnitude < 0.000001f ? Vector3.up : value.normalized;
        }

        // ================================================================== trail

        private static CucliCommandResult TrailInfo(CucliCommandContext context)
        {
            TrailRenderer trail;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "trail_get_info", out trail, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            return CucliCommandResult.Ok(new
            {
                gameObject = trail.gameObject.name,
                path = CucliObjectResolver.HierarchyPath(trail.gameObject, true),
                time = trail.time,
                startWidth = trail.startWidth,
                endWidth = trail.endWidth,
                minVertexDistance = trail.minVertexDistance,
                emitting = trail.emitting,
                autodestruct = trail.autodestruct,
                positionCount = trail.positionCount,
                alignment = trail.alignment.ToString(),
                textureMode = trail.textureMode.ToString(),
                numCornerVertices = trail.numCornerVertices,
                numCapVertices = trail.numCapVertices,
                generateLightingData = trail.generateLightingData,
                material = trail.sharedMaterial == null ? null : trail.sharedMaterial.name,
                renderer = VfxSupport.DescribeRenderer(trail)
            });
        }

        private static CucliCommandResult TrailSetTime(CucliCommandContext context)
        {
            TrailRenderer trail;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "trail_set_time", out trail, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            Undo.RecordObject(trail, "cucli trail_set_time");
            trail.time = Mathf.Max(0f, context.Input.GetFloat("time", 5f));

            return CucliCommandResult.Ok(new { time = trail.time }, "Updated: time");
        }

        private static CucliCommandResult TrailSetWidth(CucliCommandContext context)
        {
            TrailRenderer trail;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "trail_set_width", out trail, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(trail, "cucli trail_set_width");

            VfxSupport.ApplyWidth(args, null, trail, changes);

            if (args.Has("widthCurve")) { trail.widthCurve = VfxSupport.ParseCurve(args.Token("widthCurve"), 1f); changes.Add("widthCurve"); }
            if (args.Has("widthMultiplier")) { trail.widthMultiplier = args.GetFloat("widthMultiplier"); changes.Add("widthMultiplier"); }

            return CucliCommandResult.Ok(null, changes.Message("trail_set_width 没有收到任何已知键。"));
        }

        private static CucliCommandResult TrailSetColor(CucliCommandContext context)
        {
            TrailRenderer trail;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "trail_set_color", out trail, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            var changes = new ChangeSet();
            Undo.RecordObject(trail, "cucli trail_set_color");
            // A trail dissolves: the end alpha is forced to zero so it fades out instead of stopping.
            VfxSupport.ApplyColor(context.Input, null, trail, true, changes);

            return CucliCommandResult.Ok(null, changes.Message("trail_set_color 没有收到任何已知键。"));
        }

        private static CucliCommandResult TrailSetMaterial(CucliCommandContext context)
        {
            TrailRenderer trail;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "trail_set_material", out trail, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string requested = VfxSupport.MaterialPath(context.Input);
            if (string.IsNullOrWhiteSpace(requested))
            {
                string resolved = VfxSupport.FindDefaultSurfaceMaterial(context, "trail");
                if (resolved == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.MemberNotFound,
                        "未提供 materialPath，且工程里找不到可作为默认拖尾材质的资产。"
                        + "请显式传 materialPath。",
                        new { searched = VfxSupport.DefaultSurfaceShaderNames });
                }

                requested = resolved;
            }

            string materialError;
            Material material = VfxSupport.LoadMaterial(requested, out materialError);
            if (material == null)
            {
                return CucliCommandResult.Fail(CucliErrors.AssetNotFound, materialError, new { materialPath = requested });
            }

            Undo.RecordObject(trail, "cucli trail_set_material");
            trail.sharedMaterial = material;

            return CucliCommandResult.Ok(
                new { materialPath = CucliAssets.GetAssetPath(material), material = material.name },
                "已设置拖尾材质。");
        }

        private static CucliCommandResult TrailSetProperties(CucliCommandContext context)
        {
            TrailRenderer trail;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "trail_set_properties", out trail, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(trail, "cucli trail_set_properties");

            if (args.Has("time")) { trail.time = Mathf.Max(0f, args.GetFloat("time")); changes.Add("time"); }
            if (args.Has("minVertexDistance")) { trail.minVertexDistance = args.GetFloat("minVertexDistance"); changes.Add("minVertexDistance"); }
            if (args.Has("autodestruct")) { trail.autodestruct = args.GetBool("autodestruct"); changes.Add("autodestruct"); }
            if (args.Has("emitting")) { trail.emitting = args.GetBool("emitting"); changes.Add("emitting"); }

            // Width only; widthCurve/widthMultiplier belong to trail_set_width, matching the contract.
            VfxSupport.ApplyWidth(args, null, trail, changes);
            // Note: no loop / useWorldSpace — a trail has neither.
            VfxSupport.ApplyCommonRendererProperties(args, trail, changes);

            string materialPath = VfxSupport.MaterialPath(args);
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                string materialError;
                Material material = VfxSupport.LoadMaterial(materialPath, out materialError);
                if (material == null) context.Warn(materialError);
                else { trail.sharedMaterial = material; changes.Add("materialPath"); }
            }

            return CucliCommandResult.Ok(
                new { changes = changes.Names }, changes.Message("trail_set_properties 没有收到任何已知键。"));
        }

        private static CucliCommandResult TrailClear(CucliCommandContext context)
        {
            TrailRenderer trail;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "trail_clear", out trail, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            Undo.RecordObject(trail, "cucli trail_clear");
            trail.Clear();

            return CucliCommandResult.Ok(new { positionCount = trail.positionCount }, "已清空拖尾。");
        }

        private static CucliCommandResult TrailEmit(CucliCommandContext context)
        {
            TrailRenderer trail;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "trail_emit", out trail, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            Vector3 position = args.Has("position") ? args.GetVector3("position") : Vector3.zero;

            Undo.RecordObject(trail, "cucli trail_emit");
            trail.AddPosition(position);

            return CucliCommandResult.Ok(
                new { position = CucliDescribe.Value(position, null, 0), positionCount = trail.positionCount },
                "已向拖尾追加一个位置。");
        }
    }
}
