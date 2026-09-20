using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli vfx ...</c> — particle systems, VFX Graph, lines and trails (63 actions).
    ///
    /// The group is a single command with four families, dispatched by the action's prefix:
    /// <c>particle_*</c> (18), <c>vfx_*</c> (23), <c>line_*</c> (13) and <c>trail_*</c> (8), plus
    /// <c>ping</c>. The family prefixes are the MCP names verbatim, so a call migrating from
    /// <c>manage_vfx</c> keeps working.
    ///
    /// <para><b>Optional package.</b> <see cref="OptionalPackage"/> is deliberately null rather than
    /// <c>com.unity.visualeffectgraph</c>, because only the <c>vfx_*</c> family needs it. Declaring it
    /// at group level would hide the 39 particle/line/trail actions from exactly the projects that have
    /// no VFX Graph, which are the projects most likely to want them. <c>vfx_*</c> checks for itself in
    /// <see cref="VfxGraphCommand"/>.</para>
    /// </summary>
    internal sealed class VfxCommand : CucliCommand
    {
        internal static readonly string[] ParticleActions =
        {
            "particle_create", "particle_get_info", "particle_set_main", "particle_set_emission",
            "particle_set_shape", "particle_set_color_over_lifetime", "particle_set_size_over_lifetime",
            "particle_set_velocity_over_lifetime", "particle_set_noise", "particle_set_renderer",
            "particle_enable_module", "particle_play", "particle_stop", "particle_pause",
            "particle_restart", "particle_clear", "particle_add_burst", "particle_clear_bursts"
        };

        internal static readonly string[] GraphActions =
        {
            "vfx_create_asset", "vfx_assign_asset", "vfx_list_templates", "vfx_list_assets", "vfx_get_info",
            "vfx_set_float", "vfx_set_int", "vfx_set_bool", "vfx_set_vector2", "vfx_set_vector3",
            "vfx_set_vector4", "vfx_set_color", "vfx_set_gradient", "vfx_set_texture", "vfx_set_mesh",
            "vfx_set_curve", "vfx_send_event", "vfx_play", "vfx_stop", "vfx_pause", "vfx_reinit",
            "vfx_set_playback_speed", "vfx_set_seed"
        };

        internal static readonly string[] LineActions =
        {
            "line_get_info", "line_set_positions", "line_add_position", "line_set_position",
            "line_set_width", "line_set_color", "line_set_material", "line_set_properties", "line_clear",
            "line_create_line", "line_create_circle", "line_create_arc", "line_create_bezier"
        };

        internal static readonly string[] TrailActions =
        {
            "trail_get_info", "trail_set_time", "trail_set_width", "trail_set_color",
            "trail_set_material", "trail_set_properties", "trail_clear", "trail_emit"
        };

        private static string s_schema;

        public override string Name => "vfx";

        public override CucliSurface Surface => CucliSurface.Extended;

        public override string Summary =>
            "粒子系统、VFX Graph、LineRenderer 与 TrailRenderer 的创建、读取与写值（particle_* / vfx_* / line_* / trail_*）。";

        public override CucliActionInfo[] Actions
        {
            get
            {
                var actions = new List<CucliActionInfo>
                {
                    new CucliActionInfo("ping", "存活探测，返回本组支持的组件族。"),
                    new CucliActionInfo("particle_create", "在目标上创建/补齐 ParticleSystem 并设置变换。"),
                    new CucliActionInfo("particle_get_info", "读取粒子系统主模块、发射、形状与渲染器摘要（限量）。"),
                    new CucliActionInfo("particle_set_main", "写主模块：duration/loop/prewarm/startLifetime/startSpeed/startSize/startColor/gravityModifier/simulationSpace/scalingMode/maxParticles。"),
                    new CucliActionInfo("particle_set_emission", "写发射模块：enabled/rateOverTime/rateOverDistance。"),
                    new CucliActionInfo("particle_set_shape", "写形状模块：shapeType/radius/radiusThickness/angle/arc/position/rotation/scale。"),
                    new CucliActionInfo("particle_set_color_over_lifetime", "写生命周期颜色模块。"),
                    new CucliActionInfo("particle_set_size_over_lifetime", "写生命周期尺寸模块（含分轴尺寸）。"),
                    new CucliActionInfo("particle_set_velocity_over_lifetime", "写生命周期速度模块。"),
                    new CucliActionInfo("particle_set_noise", "写噪声模块：strength/frequency/scrollSpeed/damping/octaveCount/quality。"),
                    new CucliActionInfo("particle_set_renderer", "写 ParticleSystemRenderer：renderMode/sortMode/alignment/尺寸与拖尾参数/材质。"),
                    new CucliActionInfo("particle_enable_module", "按名启用或禁用某个模块。"),
                    new CucliActionInfo("particle_play", "播放（withChildren 默认 true）。"),
                    new CucliActionInfo("particle_stop", "停止。"),
                    new CucliActionInfo("particle_pause", "暂停。"),
                    new CucliActionInfo("particle_restart", "清空并重新播放。"),
                    new CucliActionInfo("particle_clear", "清空已发射粒子。"),
                    new CucliActionInfo("particle_add_burst", "追加一个 burst，并返回其索引。"),
                    new CucliActionInfo("particle_clear_bursts", "清除全部 burst。"),
                    new CucliActionInfo("vfx_create_asset", "创建 .vfx 资产（可指定模板与目录，支持 overwrite）。"),
                    new CucliActionInfo("vfx_assign_asset", "把 .vfx 资产绑定到目标上的 VisualEffect。"),
                    new CucliActionInfo("vfx_list_templates", "列出可用的 VFX 模板。"),
                    new CucliActionInfo("vfx_list_assets", "列出工程中的 VFX 资产。"),
                    new CucliActionInfo("vfx_get_info", "读取 VisualEffect 摘要：资产、存活粒子数、pause、playRate、startSeed。"),
                    new CucliActionInfo("vfx_set_float", "写暴露的 float 参数。"),
                    new CucliActionInfo("vfx_set_int", "写暴露的 int 参数。"),
                    new CucliActionInfo("vfx_set_bool", "写暴露的 bool 参数。"),
                    new CucliActionInfo("vfx_set_vector2", "写暴露的 Vector2 参数。"),
                    new CucliActionInfo("vfx_set_vector3", "写暴露的 Vector3 参数。"),
                    new CucliActionInfo("vfx_set_vector4", "写暴露的 Vector4 参数。"),
                    new CucliActionInfo("vfx_set_color", "写暴露的 Color 参数。"),
                    new CucliActionInfo("vfx_set_gradient", "写暴露的 Gradient 参数（键名是 gradient）。"),
                    new CucliActionInfo("vfx_set_texture", "写暴露的 Texture 参数。"),
                    new CucliActionInfo("vfx_set_mesh", "写暴露的 Mesh 参数。"),
                    new CucliActionInfo("vfx_set_curve", "写暴露的 AnimationCurve 参数。"),
                    new CucliActionInfo("vfx_send_event", "按名发送事件，并写入 position/velocity/color/size/lifetime 属性。"),
                    new CucliActionInfo("vfx_play", "播放 VisualEffect。"),
                    new CucliActionInfo("vfx_stop", "停止 VisualEffect。"),
                    new CucliActionInfo("vfx_pause", "切换 VisualEffect 的 pause（注意是切换，不是幂等暂停）。"),
                    new CucliActionInfo("vfx_reinit", "重新初始化 VisualEffect。"),
                    new CucliActionInfo("vfx_set_playback_speed", "写 playRate。"),
                    new CucliActionInfo("vfx_set_seed", "写 startSeed 与 resetSeedOnPlay。"),
                    new CucliActionInfo("line_get_info", "读取 LineRenderer 摘要与位置列表（按 maxItems 截断）。"),
                    new CucliActionInfo("line_set_positions", "整组写入位置并重设 positionCount。"),
                    new CucliActionInfo("line_add_position", "在末尾追加一个位置。"),
                    new CucliActionInfo("line_set_position", "改写指定索引的位置。"),
                    new CucliActionInfo("line_set_width", "写宽度：width/startWidth/endWidth/widthCurve/widthMultiplier。"),
                    new CucliActionInfo("line_set_color", "写颜色或渐变（线不淡出末端）。"),
                    new CucliActionInfo("line_set_material", "设置线材质；未给路径时尝试解析工程内默认材质。"),
                    new CucliActionInfo("line_set_properties", "批量写位置数、宽度、loop、useWorldSpace 与通用渲染器属性。"),
                    new CucliActionInfo("line_clear", "清空位置。"),
                    new CucliActionInfo("line_create_line", "把已存在的 LineRenderer 写为一条线段（不创建组件）。"),
                    new CucliActionInfo("line_create_circle", "把已存在的 LineRenderer 写为一个圆（不创建组件）。"),
                    new CucliActionInfo("line_create_arc", "把已存在的 LineRenderer 写为一段圆弧（不创建组件）。"),
                    new CucliActionInfo("line_create_bezier", "把已存在的 LineRenderer 写为贝塞尔曲线（不创建组件）。"),
                    new CucliActionInfo("trail_get_info", "读取 TrailRenderer 摘要。"),
                    new CucliActionInfo("trail_set_time", "写拖尾时长。"),
                    new CucliActionInfo("trail_set_width", "写宽度：width/startWidth/endWidth/widthCurve/widthMultiplier。"),
                    new CucliActionInfo("trail_set_color", "写颜色或渐变（拖尾末端 alpha 置 0，会淡出）。"),
                    new CucliActionInfo("trail_set_material", "设置拖尾材质；未给路径时尝试解析工程内默认材质。"),
                    new CucliActionInfo("trail_set_properties", "批量写时长、发射、顶点间距与通用渲染器属性。"),
                    new CucliActionInfo("trail_clear", "清空拖尾。"),
                    new CucliActionInfo("trail_emit", "向拖尾追加一个位置。")
                };

                return actions.ToArray();
            }
        }

        public override bool RequiresEditor => true;

        /// <summary>
        /// No Play Mode gate, matching MCP: every write works in Edit Mode, and the only runtime-only
        /// member (<c>vfx_send_event</c>) reports its own precise error when the effect is not playing.
        /// </summary>
        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "project.assets.read", "project.assets.write", "editor.scene.write"
        };

        public override bool MayReloadDomain => false;

        public override string OptionalPackage => null;

        public override string ParametersSchemaJson
        {
            get
            {
                if (s_schema == null) s_schema = BuildSchema();
                return s_schema;
            }
        }

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "ping");
            if (string.IsNullOrWhiteSpace(action))
            {
                return CucliCommandResult.Unsupported(action, ActionNames());
            }

            if (action == "ping")
            {
                return CucliCommandResult.Ok(new
                {
                    tool = "vfx",
                    group = Name,
                    families = new
                    {
                        particle = ParticleActions.Length,
                        vfx = GraphActions.Length,
                        line = LineActions.Length,
                        trail = TrailActions.Length
                    },
                    components = new[] { "ParticleSystem", "VisualEffect", "LineRenderer", "TrailRenderer" },
                    vfxGraphAvailable = CucliOptional.IsPackageInstalled("com.unity.visualeffectgraph")
                }, "vfx 组存活；particle/line/trail 始终可用，vfx_* 需要 VFX Graph。");
            }

            if (action.StartsWith("particle_", StringComparison.Ordinal))
            {
                return VfxParticleCommand.Handle(context, action);
            }

            if (action.StartsWith("vfx_", StringComparison.Ordinal))
            {
                return VfxGraphCommand.Handle(context, action);
            }

            if (action.StartsWith("line_", StringComparison.Ordinal))
            {
                return VfxLineTrailCommand.HandleLine(context, action);
            }

            if (action.StartsWith("trail_", StringComparison.Ordinal))
            {
                return VfxLineTrailCommand.HandleTrail(context, action);
            }

            return CucliCommandResult.Fail(
                CucliErrors.UnsupportedAction,
                "Unknown action: '" + action + "'。动作必须以 particle_、vfx_、line_ 或 trail_ 为前缀。",
                new { action, families = new { particle = ParticleActions, vfx = GraphActions, line = LineActions, trail = TrailActions } });
        }

        /// <summary>
        /// Builds the schema programmatically rather than as a verbatim string.
        ///
        /// The surface is 63 actions over roughly ninety keys with documented aliases
        /// (<c>material</c>/<c>materialPath</c>, <c>control1</c>/<c>controlPoint1</c>,
        /// <c>size_over_lifetime</c>/<c>size</c>, ...). Hand-writing that as an escaped C# literal is
        /// exactly where an unescaped quote turns into a broken build, and a schema that drifts out of
        /// date would start rejecting valid calls. The action enum is the part callers rely on, so it is
        /// exact; the property list is descriptive and permissive.
        /// </summary>
        private static string BuildSchema()
        {
            var actions = new JArray();
            actions.Add("ping");
            foreach (string name in ParticleActions) actions.Add(name);
            foreach (string name in GraphActions) actions.Add(name);
            foreach (string name in LineActions) actions.Add(name);
            foreach (string name in TrailActions) actions.Add(name);

            var properties = new JObject
            {
                ["action"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "动作名，不区分大小写；按 particle_ / vfx_ / line_ / trail_ 前缀分派。",
                    ["enum"] = actions,
                    ["default"] = "ping"
                }
            };

            AddTyped(properties, "string",
                "target", "searchMethod", "materialPath", "material_path", "material",
                "assetName", "asset_path", "folderPath", "folder", "search", "template",
                "parameter", "texturePath", "meshPath", "eventName", "module",
                "simulationSpace", "scalingMode", "shapeType", "space", "quality",
                "renderMode", "sortMode", "alignment", "textureMode", "shadowCastingMode",
                "lightProbeUsage", "reflectionProbeUsage", "motionVectorGenerationMode",
                "sortingLayerName");

            AddTyped(properties, "number",
                "duration", "playOnAwake", "looping", "prewarm", "maxParticles",
                "rateOverTime", "rateOverDistance", "radius", "radiusThickness", "angle", "arc",
                "width", "startWidth", "endWidth", "widthMultiplier", "time", "minVertexDistance",
                "minCount", "maxCount", "count", "cycles", "interval", "probability", "frequency",
                "octaveCount", "strength", "speedModifier", "minParticleSize", "maxParticleSize",
                "lengthScale", "velocityScale", "cameraVelocityScale", "normalDirection",
                "shadowBias", "playRate", "seed", "segments", "startAngle", "endAngle",
                "positionCount", "numCornerVertices", "numCapVertices", "sortingOrder",
                "sortingLayerID", "index", "componentIndex", "component_index",
                "maxItems", "maxDepth", "maxStringLength");

            AddTyped(properties, "boolean",
                "enabled", "separateAxes", "damping", "allowRoll", "withChildren",
                "overwrite", "resetSeedOnPlay", "loop", "useWorldSpace", "generateLightingData",
                "receiveShadows", "autodestruct", "emitting", "includeValues");

            AddTyped(properties, "array",
                "positions", "position", "rotation", "scale", "velocity", "color", "startColor",
                "endColor", "gradient", "start", "end", "center", "normal",
                "controlPoint1", "controlPoint2", "control1", "control2", "value", "size",
                "startDelay", "startLifetime", "startSpeed", "startSize", "startRotation",
                "gravityModifier", "x", "y", "z", "sizeX", "sizeY", "sizeZ", "widthCurve",
                "scrollSpeed", "pivot", "flip");

            var schema = new JObject
            {
                ["$schema"] = "http://json-schema.org/draft-07/schema#",
                ["title"] = "cucli vfx",
                ["type"] = "object",
                // Permissive on purpose: the alias table above is descriptive, and rejecting a valid
                // alias would be worse than accepting one that the command then reports as unknown.
                ["additionalProperties"] = true,
                ["properties"] = properties
            };

            return schema.ToString(Formatting.None);
        }

        private static void AddTyped(JObject properties, string type, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (properties[key] != null) continue;
                properties[key] = new JObject
                {
                    ["type"] = type,
                    ["description"] = "见 docs 中 vfx 组的参数表；多个类型可接受时会回落到默认值。"
                };
            }
        }
    }
}
