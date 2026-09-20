using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>particle_*</c> family of the <c>vfx</c> group (18 actions).
    ///
    /// <see cref="ParticleSystem"/> ships with the engine, so this file is ordinary typed Unity code.
    /// Nothing here depends on an optional package, which is what lets <c>particle_*</c> work in every
    /// project while <c>vfx_*</c> has to check for VFX Graph.
    /// </summary>
    internal static class VfxParticleCommand
    {
        /// <summary>Module keys accepted by <c>particle_enable_module</c>, normalised by stripping '_'.</summary>
        private static readonly string[] ModuleNames =
        {
            "emission", "shape", "coloroverlifetime", "sizeoverlifetime",
            "velocityoverlifetime", "noise", "collision", "trails", "lights"
        };

        internal static CucliCommandResult Handle(CucliCommandContext context, string action)
        {
            switch (action)
            {
                case "particle_create": return Create(context);
                case "particle_get_info": return GetInfo(context);
                case "particle_set_main": return SetMain(context);
                case "particle_set_emission": return SetEmission(context);
                case "particle_set_shape": return SetShape(context);
                case "particle_set_color_over_lifetime": return SetColorOverLifetime(context);
                case "particle_set_size_over_lifetime": return SetSizeOverLifetime(context);
                case "particle_set_velocity_over_lifetime": return SetVelocityOverLifetime(context);
                case "particle_set_noise": return SetNoise(context);
                case "particle_set_renderer": return SetRenderer(context);
                case "particle_enable_module": return EnableModule(context);
                case "particle_play": return Playback(context, "play");
                case "particle_stop": return Playback(context, "stop");
                case "particle_pause": return Playback(context, "pause");
                case "particle_restart": return Playback(context, "restart");
                case "particle_clear": return Playback(context, "clear");
                case "particle_add_burst": return AddBurst(context);
                case "particle_clear_bursts": return ClearBursts(context);
                default:
                    return CucliCommandResult.Unsupported(action, VfxCommand.ParticleActions);
            }
        }

        // ------------------------------------------------------------------ create

        private static CucliCommandResult Create(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string requested = args.GetString("target");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing("target", "particle_create 需要一个名称或层级路径作为 target。");
            }

            // Unlike every other particle action, create is allowed to bring the object into existence:
            // the MCP contract returns createdGameObject/addedParticleSystem precisely because both are
            // optional. A miss here is therefore not an error.
            string resolveError;
            GameObject target = CucliObjectResolver.ResolveGameObject(args, out resolveError);

            bool created = false;
            if (target == null)
            {
                target = new GameObject(requested.Trim());
                Undo.RegisterCreatedObjectUndo(target, "cucli particle_create");
                created = true;
            }

            bool added = false;
            ParticleSystem system = target.GetComponent<ParticleSystem>();
            if (system == null)
            {
                system = Undo.AddComponent<ParticleSystem>(target);
                added = true;
            }

            if (args.Has("position")) target.transform.position = args.GetVector3("position");
            if (args.Has("rotation")) target.transform.eulerAngles = args.GetVector3("rotation");
            if (args.Has("scale")) target.transform.localScale = args.GetVector3("scale");

            if (args.Has("playOnAwake") || args.Has("looping"))
            {
                ParticleSystem.MainModule main = system.main;
                if (args.Has("playOnAwake")) main.playOnAwake = args.GetBool("playOnAwake");
                if (args.Has("looping")) main.loop = args.GetBool("looping");
            }

            string assignedMaterial = null;
            string materialPath = VfxSupport.MaterialPath(args);
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                string materialError;
                Material material = VfxSupport.LoadMaterial(materialPath, out materialError);
                if (material == null)
                {
                    context.Warn(materialError);
                }
                else
                {
                    ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
                    if (renderer != null)
                    {
                        Undo.RecordObject(renderer, "cucli particle_create material");
                        renderer.sharedMaterial = material;
                        assignedMaterial = CucliAssets.GetAssetPath(material);
                    }
                }
            }

            if (!Application.isPlaying)
            {
                EditorSceneManager.MarkSceneDirty(target.scene);
            }

            return CucliCommandResult.Ok(new
            {
                target = target.name,
                targetId = target.GetInstanceID(),
                createdGameObject = created,
                addedParticleSystem = added,
                assignedMaterial,
                path = CucliObjectResolver.HierarchyPath(target, true)
            }, created
                ? "已创建 GameObject 并添加 ParticleSystem。"
                : added
                    ? "已在目标上添加 ParticleSystem。"
                    : "已更新目标的 ParticleSystem 变换。");
        }

        // ------------------------------------------------------------------ read

        private static CucliCommandResult GetInfo(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_get_info", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliDescribe.Options options = CucliDescribe.OptionsFrom(context.Input);
            ParticleSystem.MainModule main = system.main;
            ParticleSystem.EmissionModule emission = system.emission;
            ParticleSystem.ShapeModule shape = system.shape;
            var renderer = system.GetComponent<ParticleSystemRenderer>();

            return CucliCommandResult.Ok(new
            {
                gameObject = system.gameObject.name,
                path = CucliObjectResolver.HierarchyPath(system.gameObject, true),
                isPlaying = system.isPlaying,
                isPaused = system.isPaused,
                particleCount = system.particleCount,
                main = new
                {
                    duration = main.duration,
                    looping = main.loop,
                    prewarm = main.prewarm,
                    playOnAwake = main.playOnAwake,
                    startLifetime = DescribeCurve(main.startLifetime, options),
                    startSpeed = DescribeCurve(main.startSpeed, options),
                    startSize = DescribeCurve(main.startSize, options),
                    startRotation = DescribeCurve(main.startRotation, options),
                    startColor = DescribeGradient(main.startColor, options),
                    gravityModifier = DescribeCurve(main.gravityModifier, options),
                    simulationSpace = main.simulationSpace.ToString(),
                    scalingMode = main.scalingMode.ToString(),
                    maxParticles = main.maxParticles
                },
                emission = new
                {
                    enabled = emission.enabled,
                    rateOverTime = DescribeCurve(emission.rateOverTime, options),
                    rateOverDistance = DescribeCurve(emission.rateOverDistance, options),
                    burstCount = emission.burstCount
                },
                shape = new
                {
                    enabled = shape.enabled,
                    shapeType = shape.shapeType.ToString(),
                    radius = shape.radius,
                    radiusThickness = shape.radiusThickness,
                    angle = shape.angle,
                    arc = shape.arc
                },
                renderer = renderer == null ? null : new
                {
                    renderMode = renderer.renderMode.ToString(),
                    sortMode = renderer.sortMode.ToString(),
                    material = renderer.sharedMaterial == null ? null : renderer.sharedMaterial.name,
                    trailMaterial = renderer.trailMaterial == null ? null : renderer.trailMaterial.name,
                    minParticleSize = renderer.minParticleSize,
                    maxParticleSize = renderer.maxParticleSize,
                    alignment = renderer.alignment.ToString(),
                    allowRoll = renderer.allowRoll,
                    shadowBias = renderer.shadowBias
                }
            });
        }

        /// <summary>
        /// Bounded MinMaxCurve description. A curve can carry thousands of keyframes and a particle
        /// system can hold dozens of curves, so keys are capped by <c>maxItems</c> and the total is
        /// always reported.
        /// </summary>
        private static JObject DescribeCurve(ParticleSystem.MinMaxCurve curve, CucliDescribe.Options options)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.TwoConstants:
                    return new JObject
                    {
                        ["mode"] = "two_constants",
                        ["min"] = curve.constantMin,
                        ["max"] = curve.constantMax
                    };

                case ParticleSystemCurveMode.Curve:
                    return CurveJson("curve", curve.curveMultiplier, curve.curve, options);

                case ParticleSystemCurveMode.TwoCurves:
                {
                    JObject result = CurveJson("two_curves", curve.curveMultiplier, null, options);
                    result["originalMode"] = "two_curves";
                    result["curveMin"] = Keys(curve.curveMin, options);
                    result["curveMax"] = Keys(curve.curveMax, options);
                    return result;
                }

                default:
                    return new JObject { ["mode"] = "constant", ["value"] = curve.constant };
            }
        }

        private static JObject CurveJson(string mode, float multiplier, AnimationCurve curve, CucliDescribe.Options options)
        {
            var result = new JObject { ["mode"] = mode };
            if (Math.Abs(multiplier - 1f) > 0.0001f) result["multiplier"] = multiplier;
            if (curve != null) result["keys"] = Keys(curve, options);
            return result;
        }

        private static JArray Keys(AnimationCurve curve, CucliDescribe.Options options)
        {
            var array = new JArray();
            if (curve == null) return array;

            Keyframe[] keys = curve.keys;
            int limit = Math.Min(keys.Length, options.MaxItems);
            for (int index = 0; index < limit; index++)
            {
                array.Add(new JObject
                {
                    ["time"] = keys[index].time,
                    ["value"] = keys[index].value,
                    ["inTangent"] = keys[index].inTangent,
                    ["outTangent"] = keys[index].outTangent
                });
            }

            return array;
        }

        private static JObject DescribeGradient(ParticleSystem.MinMaxGradient gradient, CucliDescribe.Options options)
        {
            switch (gradient.mode)
            {
                case ParticleSystemGradientMode.TwoColors:
                    return new JObject
                    {
                        ["mode"] = "two_colors",
                        ["colorMin"] = CucliDescribe.Value(gradient.colorMin, options, 0),
                        ["colorMax"] = CucliDescribe.Value(gradient.colorMax, options, 0)
                    };

                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.TwoGradients:
                    return new JObject
                    {
                        ["mode"] = gradient.mode == ParticleSystemGradientMode.TwoGradients ? "two_gradients" : "gradient",
                        ["gradient"] = CucliDescribe.Value(gradient.gradient, options, 0)
                    };

                default:
                    return new JObject
                    {
                        ["mode"] = "color",
                        ["color"] = CucliDescribe.Value(gradient.color, options, 0)
                    };
            }
        }

        // ------------------------------------------------------------------ setters

        private static CucliCommandResult SetMain(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_set_main", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();

            // Duration cannot be changed while the system is playing without restarting it, so the MCP
            // contract stops it first and resumes afterwards. Reproduce that rather than silently
            // producing a system whose duration does not match what was asked for.
            bool restart = args.Has("duration") && system.isPlaying;
            if (restart) system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            Undo.RecordObject(system, "cucli particle_set_main");
            ParticleSystem.MainModule main = system.main;

            if (args.Has("duration")) { main.duration = Mathf.Max(0.01f, args.GetFloat("duration")); changes.Add("duration"); }
            if (args.Has("looping")) { main.loop = args.GetBool("looping"); changes.Add("looping"); }
            if (args.Has("prewarm")) { main.prewarm = args.GetBool("prewarm"); changes.Add("prewarm"); }
            if (args.Has("playOnAwake")) { main.playOnAwake = args.GetBool("playOnAwake"); changes.Add("playOnAwake"); }
            if (args.Has("startDelay")) { main.startDelay = VfxSupport.ParseMinMaxCurve(args.Token("startDelay"), 0f); changes.Add("startDelay"); }
            if (args.Has("startLifetime")) { main.startLifetime = VfxSupport.ParseMinMaxCurve(args.Token("startLifetime"), 5f); changes.Add("startLifetime"); }
            if (args.Has("startSpeed")) { main.startSpeed = VfxSupport.ParseMinMaxCurve(args.Token("startSpeed"), 5f); changes.Add("startSpeed"); }
            if (args.Has("startSize")) { main.startSize = VfxSupport.ParseMinMaxCurve(args.Token("startSize"), 1f); changes.Add("startSize"); }
            if (args.Has("startRotation")) { main.startRotation = VfxSupport.ParseMinMaxCurve(args.Token("startRotation"), 0f); changes.Add("startRotation"); }
            if (args.Has("startColor")) { main.startColor = VfxSupport.ParseMinMaxGradient(args.Token("startColor"), Color.white); changes.Add("startColor"); }
            if (args.Has("gravityModifier")) { main.gravityModifier = VfxSupport.ParseMinMaxCurve(args.Token("gravityModifier"), 0f); changes.Add("gravityModifier"); }
            if (args.Has("simulationSpace"))
            {
                ParticleSystemSimulationSpace space;
                if (VfxSupport.TryEnum(args, "simulationSpace", out space)) { main.simulationSpace = space; changes.Add("simulationSpace"); }
            }

            if (args.Has("scalingMode"))
            {
                ParticleSystemScalingMode scaling;
                if (VfxSupport.TryEnum(args, "scalingMode", out scaling)) { main.scalingMode = scaling; changes.Add("scalingMode"); }
            }

            if (args.Has("maxParticles")) { main.maxParticles = Mathf.Max(0, args.GetInt("maxParticles")); changes.Add("maxParticles"); }

            if (restart) system.Play(true);

            if (changes.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "duration/looping/prewarm/playOnAwake/startLifetime/...",
                    "particle_set_main 没有收到任何已知键；请检查参数拼写。");
            }

            return CucliCommandResult.Ok(null, changes.Message("particle_set_main 已应用。"));
        }

        private static CucliCommandResult SetEmission(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_set_emission", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(system, "cucli particle_set_emission");
            ParticleSystem.EmissionModule emission = system.emission;

            if (args.Has("enabled")) { emission.enabled = args.GetBool("enabled"); changes.Add("enabled"); }
            if (args.Has("rateOverTime")) { emission.rateOverTime = VfxSupport.ParseMinMaxCurve(args.Token("rateOverTime"), 10f); changes.Add("rateOverTime"); }
            if (args.Has("rateOverDistance")) { emission.rateOverDistance = VfxSupport.ParseMinMaxCurve(args.Token("rateOverDistance"), 0f); changes.Add("rateOverDistance"); }

            return CucliCommandResult.Ok(null, changes.Message("particle_set_emission 没有收到任何已知键。"));
        }

        private static CucliCommandResult SetShape(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_set_shape", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(system, "cucli particle_set_shape");
            ParticleSystem.ShapeModule shape = system.shape;

            if (args.Has("enabled")) { shape.enabled = args.GetBool("enabled"); changes.Add("enabled"); }
            if (args.Has("shapeType"))
            {
                ParticleSystemShapeType shapeType;
                if (VfxSupport.TryEnum(args, "shapeType", out shapeType)) { shape.shapeType = shapeType; changes.Add("shapeType"); }
            }

            if (args.Has("radius")) { shape.radius = args.GetFloat("radius"); changes.Add("radius"); }
            if (args.Has("radiusThickness")) { shape.radiusThickness = args.GetFloat("radiusThickness"); changes.Add("radiusThickness"); }
            if (args.Has("angle")) { shape.angle = args.GetFloat("angle"); changes.Add("angle"); }
            if (args.Has("arc")) { shape.arc = args.GetFloat("arc"); changes.Add("arc"); }
            if (args.Has("position")) { shape.position = args.GetVector3("position"); changes.Add("position"); }
            if (args.Has("rotation")) { shape.rotation = args.GetVector3("rotation"); changes.Add("rotation"); }
            if (args.Has("scale")) { shape.scale = args.GetVector3("scale", Vector3.one); changes.Add("scale"); }

            return CucliCommandResult.Ok(null, changes.Message("particle_set_shape 没有收到任何已知键。"));
        }

        private static CucliCommandResult SetColorOverLifetime(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_set_color_over_lifetime", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(system, "cucli particle_set_color_over_lifetime");
            ParticleSystem.ColorOverLifetimeModule module = system.colorOverLifetime;

            if (args.Has("enabled")) { module.enabled = args.GetBool("enabled"); changes.Add("enabled"); }
            if (args.Has("color")) { module.color = VfxSupport.ParseMinMaxGradient(args.Token("color"), Color.white); changes.Add("color"); }

            return CucliCommandResult.Ok(null, changes.Message("particle_set_color_over_lifetime 没有收到任何已知键。"));
        }

        private static CucliCommandResult SetSizeOverLifetime(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_set_size_over_lifetime", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(system, "cucli particle_set_size_over_lifetime");
            ParticleSystem.SizeOverLifetimeModule module = system.sizeOverLifetime;

            if (args.Has("separateAxes")) { module.separateAxes = args.GetBool("separateAxes"); changes.Add("separateAxes"); }

            if (args.Has("size") || args.Has("size_over_lifetime"))
            {
                JToken token = args.Token("size") ?? args.Token("size_over_lifetime");
                module.size = VfxSupport.ParseMinMaxCurve(token, 1f);
                changes.Add("size");
            }

            // Giving any axis implicitly asks for per-axis sizing; the MCP contract turns the module on
            // in that case so the caller does not have to remember to.
            bool axis = false;
            if (args.Has("sizeX")) { module.x = VfxSupport.ParseMinMaxCurve(args.Token("sizeX"), 1f); changes.Add("sizeX"); axis = true; }
            if (args.Has("sizeY")) { module.y = VfxSupport.ParseMinMaxCurve(args.Token("sizeY"), 1f); changes.Add("sizeY"); axis = true; }
            if (args.Has("sizeZ")) { module.z = VfxSupport.ParseMinMaxCurve(args.Token("sizeZ"), 1f); changes.Add("sizeZ"); axis = true; }

            if (args.Has("enabled"))
            {
                module.enabled = args.GetBool("enabled");
                changes.Add("enabled");
            }
            else if (axis && !module.enabled)
            {
                module.enabled = true;
                changes.Add("enabled");
            }

            return CucliCommandResult.Ok(null, changes.Message("particle_set_size_over_lifetime 没有收到任何已知键。"));
        }

        private static CucliCommandResult SetVelocityOverLifetime(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_set_velocity_over_lifetime", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(system, "cucli particle_set_velocity_over_lifetime");
            ParticleSystem.VelocityOverLifetimeModule module = system.velocityOverLifetime;

            if (args.Has("enabled")) { module.enabled = args.GetBool("enabled"); changes.Add("enabled"); }
            if (args.Has("space"))
            {
                ParticleSystemSimulationSpace space;
                if (VfxSupport.TryEnum(args, "space", out space)) { module.space = space; changes.Add("space"); }
            }

            if (args.Has("x")) { module.x = VfxSupport.ParseMinMaxCurve(args.Token("x"), 0f); changes.Add("x"); }
            if (args.Has("y")) { module.y = VfxSupport.ParseMinMaxCurve(args.Token("y"), 0f); changes.Add("y"); }
            if (args.Has("z")) { module.z = VfxSupport.ParseMinMaxCurve(args.Token("z"), 0f); changes.Add("z"); }
            if (args.Has("speedModifier")) { module.speedModifier = VfxSupport.ParseMinMaxCurve(args.Token("speedModifier"), 1f); changes.Add("speedModifier"); }

            return CucliCommandResult.Ok(null, changes.Message("particle_set_velocity_over_lifetime 没有收到任何已知键。"));
        }

        private static CucliCommandResult SetNoise(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_set_noise", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(system, "cucli particle_set_noise");
            ParticleSystem.NoiseModule noise = system.noise;

            if (args.Has("enabled")) { noise.enabled = args.GetBool("enabled"); changes.Add("enabled"); }
            if (args.Has("strength")) { noise.strength = VfxSupport.ParseMinMaxCurve(args.Token("strength"), 1f); changes.Add("strength"); }
            if (args.Has("frequency")) { noise.frequency = args.GetFloat("frequency"); changes.Add("frequency"); }
            if (args.Has("scrollSpeed")) { noise.scrollSpeed = VfxSupport.ParseMinMaxCurve(args.Token("scrollSpeed"), 0f); changes.Add("scrollSpeed"); }
            if (args.Has("damping")) { noise.damping = args.GetBool("damping"); changes.Add("damping"); }
            if (args.Has("octaveCount")) { noise.octaveCount = Mathf.Clamp(args.GetInt("octaveCount"), 1, 4); changes.Add("octaveCount"); }

            if (args.Has("quality"))
            {
                ParticleSystemNoiseQuality quality;
                if (VfxSupport.TryEnum(args, "quality", out quality)) { noise.quality = quality; changes.Add("quality"); }
            }

            return CucliCommandResult.Ok(null, changes.Message("particle_set_noise 没有收到任何已知键。"));
        }

        private static CucliCommandResult SetRenderer(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_set_renderer", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, "ParticleSystemRenderer not found on '" + system.gameObject.name + "'。");
            }

            CucliArgs args = context.Input;
            var changes = new ChangeSet();
            Undo.RecordObject(renderer, "cucli particle_set_renderer");

            if (args.Has("renderMode"))
            {
                ParticleSystemRenderMode mode;
                if (VfxSupport.TryEnum(args, "renderMode", out mode)) { renderer.renderMode = mode; changes.Add("renderMode"); }
            }

            if (args.Has("sortMode"))
            {
                ParticleSystemSortMode mode;
                if (VfxSupport.TryEnum(args, "sortMode", out mode)) { renderer.sortMode = mode; changes.Add("sortMode"); }
            }

            if (args.Has("alignment"))
            {
                ParticleSystemRenderSpace alignment;
                if (VfxSupport.TryEnum(args, "alignment", out alignment)) { renderer.alignment = alignment; changes.Add("alignment"); }
            }

            if (args.Has("minParticleSize")) { renderer.minParticleSize = args.GetFloat("minParticleSize"); changes.Add("minParticleSize"); }
            if (args.Has("maxParticleSize")) { renderer.maxParticleSize = args.GetFloat("maxParticleSize"); changes.Add("maxParticleSize"); }
            if (args.Has("lengthScale")) { renderer.lengthScale = args.GetFloat("lengthScale"); changes.Add("lengthScale"); }
            if (args.Has("velocityScale")) { renderer.velocityScale = args.GetFloat("velocityScale"); changes.Add("velocityScale"); }
            if (args.Has("cameraVelocityScale")) { renderer.cameraVelocityScale = args.GetFloat("cameraVelocityScale"); changes.Add("cameraVelocityScale"); }
            if (args.Has("normalDirection")) { renderer.normalDirection = args.GetFloat("normalDirection"); changes.Add("normalDirection"); }
            if (args.Has("pivot")) { renderer.pivot = args.GetVector3("pivot"); changes.Add("pivot"); }
            if (args.Has("flip")) { renderer.flip = args.GetVector3("flip"); changes.Add("flip"); }
            if (args.Has("allowRoll")) { renderer.allowRoll = args.GetBool("allowRoll"); changes.Add("allowRoll"); }
            if (args.Has("shadowBias")) { renderer.shadowBias = args.GetFloat("shadowBias"); changes.Add("shadowBias"); }

            bool replaced = false;
            string reason = null;
            string materialPath = VfxSupport.MaterialPath(args);
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                string materialError;
                Material material = VfxSupport.LoadMaterial(materialPath, out materialError);
                if (material == null)
                {
                    // MCP warns and keeps the existing material here rather than failing the write.
                    context.Warn(materialError);
                    reason = materialError;
                }
                else
                {
                    renderer.sharedMaterial = material;
                    replaced = true;
                    reason = "explicit materialPath";
                    changes.Add("materialPath");
                }
            }

            if (args.Has("trailMaterialPath"))
            {
                string trailError;
                Material trailMaterial = VfxSupport.LoadMaterial(args.GetString("trailMaterialPath"), out trailError);
                if (trailMaterial == null)
                {
                    context.Warn(trailError);
                }
                else
                {
                    renderer.trailMaterial = trailMaterial;
                    changes.Add("trailMaterialPath");
                }
            }

            return CucliCommandResult.Ok(new
            {
                materialReplaced = replaced,
                replacementReason = reason,
                changes = changes.Names
            }, changes.Message("particle_set_renderer 没有收到任何已知键。"));
        }

        /// <summary>
        /// Enables or disables one module by name. Names are normalised by lower-casing and stripping
        /// underscores, so <c>color_over_lifetime</c>, <c>colorOverLifetime</c> and
        /// <c>ColorOverLifetime</c> all resolve to the same module.
        /// </summary>
        private static CucliCommandResult EnableModule(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_enable_module", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            string raw = args.GetString("module");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return CucliCommandResult.Missing("module", "可用模块：" + string.Join(", ", ModuleNames));
            }

            string module = raw.Trim().ToLowerInvariant().Replace("_", string.Empty);
            bool enabled = args.GetBool("enabled", true);

            Undo.RecordObject(system, "cucli particle_enable_module");

            // Unity's particle modules are structs that wrap the native system, so a member cannot be
            // assigned through the property directly (that is CS1612). Copying to a local, mutating the
            // local and letting the struct write through is the documented pattern.
            switch (module)
            {
                case "emission": { var m = system.emission; m.enabled = enabled; break; }
                case "shape": { var m = system.shape; m.enabled = enabled; break; }
                case "coloroverlifetime": { var m = system.colorOverLifetime; m.enabled = enabled; break; }
                case "sizeoverlifetime": { var m = system.sizeOverLifetime; m.enabled = enabled; break; }
                case "velocityoverlifetime": { var m = system.velocityOverLifetime; m.enabled = enabled; break; }
                case "noise": { var m = system.noise; m.enabled = enabled; break; }
                case "collision": { var m = system.collision; m.enabled = enabled; break; }
                case "trails": { var m = system.trails; m.enabled = enabled; break; }
                case "lights": { var m = system.lights; m.enabled = enabled; break; }
                default:
                    return CucliCommandResult.Invalid(
                        "module", "Unknown module: '" + raw + "'。可用模块：" + string.Join(", ", ModuleNames));
            }

            return CucliCommandResult.Ok(
                new { module, enabled }, (enabled ? "已启用模块 " : "已禁用模块 ") + module + "。");
        }

        // ------------------------------------------------------------------ playback

        private static CucliCommandResult Playback(CucliCommandContext context, string mode)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_" + mode, out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            bool withChildren = context.Input.GetBool("withChildren", true);

            switch (mode)
            {
                case "play": system.Play(withChildren); break;
                case "stop": system.Stop(withChildren); break;
                case "pause": system.Pause(withChildren); break;
                case "restart": system.Clear(withChildren); system.Play(withChildren); break;
                case "clear": system.Clear(withChildren); break;
            }

            return CucliCommandResult.Ok(new
            {
                action = "particle_" + mode,
                withChildren,
                isPlaying = system.isPlaying,
                isPaused = system.isPaused,
                particleCount = system.particleCount,
                materialReplaced = false,
                replacementReason = (string)null
            }, "particle_" + mode + " 已执行。");
        }

        // ------------------------------------------------------------------ bursts

        private static CucliCommandResult AddBurst(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_add_burst", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            int fallback = args.GetInt("count", 30);
            short min = (short)Mathf.Clamp(args.GetInt("minCount", fallback), 0, 32767);
            short max = (short)Mathf.Clamp(args.GetInt("maxCount", fallback), 0, 32767);
            if (max < min) max = min;

            var burst = new ParticleSystem.Burst(
                args.GetFloat("time"),
                min,
                max,
                (short)Mathf.Max(1, args.GetInt("cycles", 1)),
                args.GetFloat("interval", 0.01f))
            {
                probability = Mathf.Clamp01(args.GetFloat("probability", 1f))
            };

            Undo.RecordObject(system, "cucli particle_add_burst");
            ParticleSystem.EmissionModule emission = system.emission;
            ParticleSystem.Burst[] existing = new ParticleSystem.Burst[emission.burstCount];
            emission.GetBursts(existing);

            var updated = new ParticleSystem.Burst[existing.Length + 1];
            Array.Copy(existing, updated, existing.Length);
            updated[existing.Length] = burst;
            emission.SetBursts(updated);

            return CucliCommandResult.Ok(new
            {
                burstIndex = existing.Length,
                burstCount = emission.burstCount,
                materialReplaced = false,
                replacementReason = (string)null
            }, "已添加 burst（索引 " + existing.Length + "）。");
        }

        private static CucliCommandResult ClearBursts(CucliCommandContext context)
        {
            ParticleSystem system;
            string error;
            if (!VfxSupport.TryResolveComponent(context.Input, "particle_clear_bursts", out system, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            ParticleSystem.EmissionModule emission = system.emission;
            int removed = emission.burstCount;
            Undo.RecordObject(system, "cucli particle_clear_bursts");
            emission.SetBursts(Array.Empty<ParticleSystem.Burst>());

            return CucliCommandResult.Ok(
                new { removed, burstCount = emission.burstCount },
                removed == 0 ? "该粒子系统本来就没有 burst。" : "已清除 " + removed + " 个 burst。");
        }
    }
}
