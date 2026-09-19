using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli graphics ...</c> - render pipeline settings, the skybox / ambient / fog / reflection
    /// environment, light baking, renderer features and rendering statistics.
    ///
    /// Everything that would need an optional package is reached by reflection:
    ///
    /// * <c>volume_*</c> needs <c>com.unity.render-pipelines.core</c> (<c>UnityEngine.Rendering.Volume</c>,
    ///   <c>VolumeProfile</c>, <c>VolumeComponent</c> in <c>Unity.RenderPipelines.Core.Runtime</c>).
    /// * <c>feature_*</c> needs the active pipeline to be URP and reaches
    ///   <c>ScriptableRendererData</c> / <c>ScriptableRendererFeature</c> in
    ///   <c>Unity.RenderPipelines.Universal.Runtime</c>.
    ///
    /// There is deliberately <em>no</em> action that assigns <c>RenderPipelineAsset</c>: the MCP
    /// contract has none either, and swapping the pipeline from a tool is a project-wide change
    /// that belongs in Project Settings. This group only reads <c>GraphicsSettings.currentRenderPipeline</c>.
    ///
    /// <c>bake_start</c> and <c>bake_reflection_probe</c> are long operations, so the group implements
    /// <see cref="ICucliDeferredCommand"/>: the job processor calls <see cref="ExecuteDeferred"/>,
    /// which polls <c>Lightmapping.isRunning</c> until it stops, honouring <c>timeoutSeconds</c> and
    /// the job cancellation token.
    /// </summary>
    internal sealed class GraphicsCommand : CucliCommand, ICucliDeferredCommand
    {
        public override string Name => "graphics";

        public override string Summary =>
            "Render pipeline settings, skybox/ambient/fog/reflection environment, light baking, renderer features and rendering statistics.";

        public override CucliActionInfo[] Actions => Declare(
            "ping", "Report the active pipeline, Volume and URP feature availability, and the effect count.",
            "volume_create", "Create a Volume GameObject, optionally with a VolumeProfile asset and initial effects.",
            "volume_add_effect", "Add a VolumeComponent to the volume profile.",
            "volume_set_effect", "Write parameters onto one VolumeComponent and mark them overridden.",
            "volume_remove_effect", "Remove a VolumeComponent from the volume profile.",
            "volume_get_info", "Read the volume state and its effects.",
            "volume_set_properties", "Set weight, priority, isGlobal and blendDistance on a Volume.",
            "volume_list_effects", "List every VolumeComponent type available in the active pipeline.",
            "volume_create_profile", "Create a VolumeProfile .asset at a path.",
            "bake_start", "Start a lightmap bake and wait for it to finish.",
            "bake_cancel", "Cancel a running lightmap bake.",
            "bake_status", "Report whether a bake is running plus the GI and lightmap counts.",
            "bake_clear", "Discard the baked lightmap data.",
            "bake_reflection_probe", "Bake one reflection probe to Assets/Lightmaps and wait for it to finish.",
            "bake_get_settings", "Read the lightmap settings of the active LightingSettings asset.",
            "bake_set_settings", "Write lightmap settings onto the active LightingSettings asset.",
            "bake_create_light_probe_group", "Create a LightProbeGroup with a probe grid.",
            "bake_create_reflection_probe", "Create a ReflectionProbe with mode, resolution and bounds.",
            "bake_set_probe_positions", "Replace the probe positions of a LightProbeGroup.",
            "stats_get", "Read the rendering statistics of the last frame.",
            "stats_list_counters", "List the Profiler counters of one category.",
            "stats_set_scene_debug", "Set the Scene view draw mode.",
            "stats_get_memory", "Read texture, mesh and managed memory totals.",
            "pipeline_get_info", "Report the active pipeline, quality level, colour space and pipeline settings.",
            "pipeline_set_quality", "Set the active quality level by name or index.",
            "pipeline_get_settings", "Read the public and serialized properties of the pipeline asset.",
            "pipeline_set_settings", "Write properties onto the pipeline asset by property or m_Xxx path.",
            "feature_list", "List the renderer features of one URP renderer.",
            "feature_add", "Add a ScriptableRendererFeature to a URP renderer.",
            "feature_remove", "Remove a renderer feature by index or name.",
            "feature_configure", "Write serialized properties onto a renderer feature.",
            "feature_toggle", "Enable or disable a renderer feature.",
            "feature_reorder", "Reorder the renderer features of one renderer.",
            "skybox_get", "Read the skybox material, ambient, fog, reflection and sun settings.",
            "skybox_set_material", "Assign the skybox material.",
            "skybox_set_properties", "Write properties onto the skybox material.",
            "skybox_set_ambient", "Set the ambient mode, colours and intensity.",
            "skybox_set_fog", "Set the fog mode, colour, density and distances.",
            "skybox_set_reflection", "Set the reflection intensity, bounces, mode and cubemap.",
            "skybox_set_sun", "Assign the sun used by the skybox.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "project.assets.write", "project.settings.write", "project.lightmaps.write", "scene.render.write"
        };

        public override string ParametersSchemaJson => GraphicsSupport.ParametersSchemaJson;

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            // The actions are declared hyphenated ('bake-start') because that is what reads best on a
            // command line, while the dispatch labels are snake_case ('bake_start'). Folding here is
            // what makes both spellings work; without it a user typing the declared form would get
            // UNSUPPORTED_ACTION. physics and so fold the same way.
            string action = ResolveAction(context, "ping");
            if (!string.IsNullOrEmpty(action)) action = action.Replace('-', '_');
            switch (action)
            {
                case "ping": return Ping(context);
                case "stats_get": return StatsGet(context);
                case "stats_list_counters": return StatsListCounters(context);
                case "stats_set_scene_debug": return StatsSetSceneDebug(context);
                case "stats_get_memory": return StatsGetMemory(context);
                case "pipeline_get_info": return PipelineGetInfo(context);
                case "pipeline_set_quality": return PipelineSetQuality(context);
                case "pipeline_get_settings": return PipelineGetSettings(context);
                case "pipeline_set_settings": return PipelineSetSettings(context);
                case "volume_create": return GraphicsVolumeCommand.Create(context);
                case "volume_add_effect": return GraphicsVolumeCommand.AddEffect(context);
                case "volume_set_effect": return GraphicsVolumeCommand.SetEffect(context);
                case "volume_remove_effect": return GraphicsVolumeCommand.RemoveEffect(context);
                case "volume_get_info": return GraphicsVolumeCommand.GetInfo(context);
                case "volume_set_properties": return GraphicsVolumeCommand.SetProperties(context);
                case "volume_list_effects": return GraphicsVolumeCommand.ListEffects(context);
                case "volume_create_profile": return GraphicsVolumeCommand.CreateProfile(context);
                case "feature_list": return GraphicsFeatureCommand.List(context);
                case "feature_add": return GraphicsFeatureCommand.Add(context);
                case "feature_remove": return GraphicsFeatureCommand.Remove(context);
                case "feature_configure": return GraphicsFeatureCommand.Configure(context);
                case "feature_toggle": return GraphicsFeatureCommand.Toggle(context);
                case "feature_reorder": return GraphicsFeatureCommand.Reorder(context);
                case "skybox_get": return GraphicsSkyboxCommand.Get(context);
                case "skybox_set_material": return GraphicsSkyboxCommand.SetMaterial(context);
                case "skybox_set_properties": return GraphicsSkyboxCommand.SetProperties(context);
                case "skybox_set_ambient": return GraphicsSkyboxCommand.SetAmbient(context);
                case "skybox_set_fog": return GraphicsSkyboxCommand.SetFog(context);
                case "skybox_set_reflection": return GraphicsSkyboxCommand.SetReflection(context);
                case "skybox_set_sun": return GraphicsSkyboxCommand.SetSun(context);
                case "bake_start":
                case "bake_reflection_probe":
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "Action '" + action + "' bakes for an unbounded time and must be dispatched through the "
                        + "deferred path.",
                        new { action, deferred = true });
                case "bake_cancel": return BakeCancel(context);
                case "bake_status": return BakeStatus(context);
                case "bake_clear": return BakeClear(context);
                case "bake_get_settings": return BakeGetSettings(context);
                case "bake_set_settings": return BakeSetSettings(context);
                case "bake_create_light_probe_group": return BakeCreateLightProbeGroup(context);
                case "bake_create_reflection_probe": return BakeCreateReflectionProbe(context);
                case "bake_set_probe_positions": return BakeSetProbePositions(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        public void ExecuteDeferred(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            string action = ResolveAction(context, "ping");
            switch (action)
            {
                case "bake_start":
                    BakeStart(context, complete);
                    return;
                case "bake_reflection_probe":
                    BakeReflectionProbe(context, complete);
                    return;
                default:
                    complete(Execute(context));
                    return;
            }
        }

        // ---------------------------------------------------------------- ping

        private static CucliCommandResult Ping(CucliCommandContext context)
        {
            RenderPipelineAsset pipeline = GraphicsSupport.CurrentPipeline();
            string pipelineName = pipeline == null ? "Built-in Render Pipeline" : pipeline.GetType().Name;
            bool hasVolume = GraphicsSupport.VolumeType() != null && GraphicsSupport.VolumeProfileType() != null;
            List<string> effects = hasVolume ? GraphicsVolumeCommand.ListEffectTypeNames() : new List<string>();

            return CucliCommandResult.Ok(new
            {
                pipeline = pipeline == null ? "builtin" : "srp",
                pipelineName,
                pipelineAssetPath = pipeline == null ? null : CucliAssets.GetAssetPath(pipeline),
                hasVolumeSystem = hasVolume,
                volumePackage = hasVolume ? CucliOptional.PackageVersion("com.unity.render-pipelines.core") : null,
                hasURP = GraphicsSupport.IsUrpActive(),
                hasHDRP = GraphicsSupport.IsHdrpActive(),
                availableEffects = effects.Count,
                availableEffectNames = effects.Take(32).ToArray(),
                note = "This group reads GraphicsSettings.currentRenderPipeline but never assigns a pipeline asset."
            }, "The active pipeline is " + pipelineName + ".");
        }

        // ---------------------------------------------------------------- stats

        private static CucliCommandResult StatsGet(CucliCommandContext context)
        {
            var data = new JObject
            {
                ["batches"] = GraphicsSupport.UnityStat("batches"),
                ["draw_calls"] = GraphicsSupport.UnityStat("drawCalls"),
                ["set_pass_calls"] = GraphicsSupport.UnityStat("setPassCalls"),
                ["triangles"] = GraphicsSupport.UnityStat("triangles"),
                ["vertices"] = GraphicsSupport.UnityStat("vertices"),
                ["dynamic_batches"] = GraphicsSupport.UnityStat("dynamicBatchedDrawCalls"),
                ["dynamic_batched_draw_calls"] = GraphicsSupport.UnityStat("dynamicBatchedDrawCalls"),
                ["static_batches"] = GraphicsSupport.UnityStat("staticBatchedDrawCalls"),
                ["static_batched_draw_calls"] = GraphicsSupport.UnityStat("staticBatchedDrawCalls"),
                ["instanced_batches"] = GraphicsSupport.UnityStat("instancedBatchedDrawCalls"),
                ["instanced_batched_draw_calls"] = GraphicsSupport.UnityStat("instancedBatchedDrawCalls"),
                ["shadow_casters"] = GraphicsSupport.UnityStat("shadowCasters"),
                ["visible_skinned_meshes"] = GraphicsSupport.UnityStat("visibleSkinnedMeshes"),
                ["render_textures"] = null,
                ["render_textures_bytes"] = null,
                ["used_textures"] = null,
                ["used_textures_bytes"] = null,
                ["render_target_changes"] = null
            };

            data["render_textures"] = GraphicsSupport.ProfilerCounter("Render", "Render Textures");
            data["render_textures_bytes"] = GraphicsSupport.ProfilerCounter("Render", "Render Textures Bytes");
            data["used_textures"] = GraphicsSupport.ProfilerCounter("Render", "Used Textures");
            data["used_textures_bytes"] = GraphicsSupport.ProfilerCounter("Render", "Used Textures Bytes");
            data["render_target_changes"] = GraphicsSupport.ProfilerCounter("Render", "Render Target Changes");

            context.Warn("Statistics describe the last rendered frame; in Edit Mode several of them are zero.");
            return CucliCommandResult.Ok(data);
        }

        private static CucliCommandResult StatsListCounters(CucliCommandContext context)
        {
            string categoryName = GraphicsSupport.NormalizeCategory(
                context.Input.GetString("category") ?? "Render");

            // Unity only exposes counter enumeration through Unity.Profiling.LowLevel.Unsafe, which is
            // not available on every version. Instead we publish the counters this package knows how
            // to read and probe each one, so "available: false" means "this Unity build does not
            // expose it" rather than "the list is empty".
            string[] known = GraphicsSupport.KnownCounters(categoryName);
            if (known.Length == 0)
            {
                return CucliCommandResult.Ok(
                    CucliPaging.Envelope(
                        CucliPaging.Slice(Array.Empty<JToken>(), context.Input),
                        new JArray(),
                        new JObject { ["category"] = categoryName }),
                    "No counter catalog is declared for category '" + categoryName
                    + "'. Supported catalogues: Render, Memory.");
            }

            var counters = new JArray();
            foreach (string counterName in known)
            {
                long value = GraphicsSupport.ProfilerCounter(categoryName, counterName);
                counters.Add(new JObject
                {
                    ["name"] = counterName,
                    ["category"] = categoryName,
                    ["available"] = value >= 0L
                });
            }

            CucliPage<JToken> page = CucliPaging.Slice(
                counters.Cast<JToken>().ToList(), context.Input, 100);
            var items = new JArray();
            foreach (JToken item in page.Items) items.Add(item);

            return CucliCommandResult.Ok(CucliPaging.Envelope(
                page, items, new JObject { ["category"] = categoryName }));
        }

        private static CucliCommandResult StatsSetSceneDebug(CucliCommandContext context)
        {
            string requested = context.Input.GetString("mode");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing(
                    "mode", "A DrawCameraMode name, e.g. Shaded, Wireframe, Albedo, Overdraw.");
            }

            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "No active Scene view found; open a Scene view window first.");
            }

            DrawCameraMode mode;
            if (!TryParseDrawCameraMode(requested.Trim(), out mode))
            {
                return CucliCommandResult.Invalid(
                    "mode", "'" + requested + "' is not a DrawCameraMode name.");
            }

            // SceneView.cameraMode is a struct (draw mode + overlay flag), not a DrawCameraMode, so
            // it has to be built through Unity's own conversion rather than assigned directly.
            view.cameraMode = SceneView.GetBuiltinCameraMode(mode);
            view.Repaint();

            return CucliCommandResult.Ok(
                new { mode = mode.ToString() },
                "The Scene view draw mode is now " + mode + ".");
        }

        private static bool TryParseDrawCameraMode(string text, out DrawCameraMode mode)
        {
            mode = DrawCameraMode.Normal;
            Array values = Enum.GetValues(typeof(DrawCameraMode));
            foreach (object value in values)
            {
                if (string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    mode = (DrawCameraMode)value;
                    return true;
                }
            }

            // manage_graphics also accepts the user-mode names such as "Albedo".
            try
            {
                mode = (DrawCameraMode)Enum.Parse(typeof(DrawCameraMode), text, true);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static CucliCommandResult StatsGetMemory(CucliCommandContext context)
        {
            var data = new JObject
            {
                ["totalAllocatedMB"] = ToMegabytes(GraphicsSupport.UnityStat("totalAllocatedMemory")),
                ["totalReservedMB"] = ToMegabytes(GraphicsSupport.UnityStat("totalReservedMemory")),
                ["totalUnusedReservedMB"] = ToMegabytes(GraphicsSupport.UnityStat("totalUnusedReservedMemory")),
                ["monoUsedMB"] = ToMegabytes(GraphicsSupport.UnityStat("monoUsedMemory")),
                ["monoHeapMB"] = ToMegabytes(GraphicsSupport.UnityStat("monoHeapSize")),
                ["graphicsDriverMB"] = ToMegabytes(GraphicsSupport.UnityStat("allocatedMemoryForGraphicsDriver"))
            };

            long textureBytes = GraphicsSupport.ProfilerCounter("Render", "Texture Memory");
            if (textureBytes >= 0) data["textureMemoryMB"] = Math.Round(textureBytes / 1048576.0, 2);

            long meshBytes = GraphicsSupport.ProfilerCounter("Render", "Mesh Memory");
            if (meshBytes >= 0) data["meshMemoryMB"] = Math.Round(meshBytes / 1048576.0, 2);

            return CucliCommandResult.Ok(data);
        }

        private static double? ToMegabytes(long? bytes)
        {
            if (!bytes.HasValue || bytes.Value < 0) return null;
            return Math.Round(bytes.Value / 1048576.0, 2);
        }

        // ---------------------------------------------------------------- pipeline

        private static CucliCommandResult PipelineGetInfo(CucliCommandContext context)
        {
            RenderPipelineAsset pipeline = GraphicsSupport.CurrentPipeline();
            string[] levels = QualitySettings.names;
            int level = QualitySettings.GetQualityLevel();

            var data = new JObject
            {
                ["pipeline"] = pipeline == null ? "builtin" : "srp",
                ["pipelineName"] = pipeline == null ? "Built-in Render Pipeline" : pipeline.GetType().Name,
                ["qualityLevel"] = level,
                ["qualityLevelName"] = level >= 0 && level < levels.Length ? levels[level] : null,
                ["qualityLevels"] = new JArray(levels),
                ["colorSpace"] = QualitySettings.activeColorSpace.ToString(),
                ["hasVolumeSystem"] = GraphicsSupport.VolumeType() != null
            };

            if (pipeline != null)
            {
                data["pipelineAsset"] = pipeline.name;
                data["pipelineAssetPath"] = NullIfEmpty(CucliAssets.GetAssetPath(pipeline));
                data["pipelineAssetType"] = pipeline.GetType().FullName;

                var options = CucliDescribe.Options.Default;

                // PropertyOrNull returns object, and a JObject collection initializer binds to
                // Add(string, JToken); ToToken also turns a missing property into JSON null.
                data["settings"] = new JObject
                {
                    ["renderScale"] = CucliJson.ToToken(GraphicsSupport.PropertyOrNull(pipeline, "renderScale", options)),
                    ["supportsHDR"] = CucliJson.ToToken(GraphicsSupport.PropertyOrNull(pipeline, "supportsHDR", options)),
                    ["msaaSampleCount"] = CucliJson.ToToken(GraphicsSupport.PropertyOrNull(pipeline, "msaaSampleCount", options)),
                    ["shadowDistance"] = CucliJson.ToToken(GraphicsSupport.PropertyOrNull(pipeline, "shadowDistance", options)),
                    ["shadowCascadeCount"] = CucliJson.ToToken(GraphicsSupport.PropertyOrNull(pipeline, "shadowCascadeCount", options)),
                    ["maxAdditionalLightsCount"] =
                        CucliJson.ToToken(GraphicsSupport.PropertyOrNull(pipeline, "maxAdditionalLightsCount", options)),
                    ["supportsSoftShadows"] =
                        CucliJson.ToToken(GraphicsSupport.PropertyOrNull(pipeline, "supportsSoftShadows", options)),
                    ["colorGradingMode"] = CucliJson.ToToken(GraphicsSupport.PropertyOrNull(pipeline, "colorGradingMode", options))
                };
            }

            return CucliCommandResult.Ok(data);
        }

        private static CucliCommandResult PipelineSetQuality(CucliCommandContext context)
        {
            string[] levels = QualitySettings.names;
            if (levels.Length == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "QualitySettings reports no quality levels.");
            }

            string requested = context.Input.GetString("level");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing(
                    "level", "A quality level name or index. Available: " + string.Join(", ", levels));
            }

            int index = -1;
            int numeric;
            if (int.TryParse(requested.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                index = numeric;
            }
            else
            {
                for (int candidate = 0; candidate < levels.Length; candidate++)
                {
                    if (!string.Equals(levels[candidate], requested.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    index = candidate;
                    break;
                }
            }

            if (index < 0 || index >= levels.Length)
            {
                return CucliCommandResult.Invalid(
                    "level", "'" + requested + "' is not one of: " + string.Join(", ", levels)
                    + " (or an index in 0.." + (levels.Length - 1) + ").");
            }

            QualitySettings.SetQualityLevel(index, true);
            return CucliCommandResult.Ok(new
            {
                level = index,
                name = levels[index],
                allLevels = levels
            }, "The active quality level is now " + levels[index] + ".");
        }

        private static CucliCommandResult PipelineGetSettings(CucliCommandContext context)
        {
            RenderPipelineAsset pipeline = GraphicsSupport.CurrentPipeline();
            if (pipeline == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "No render pipeline asset found (Built-in pipeline has no asset).");
            }

            var options = CucliDescribe.Options.Default;
            var settings = new JObject();
            List<CucliMemberInfo> members = CucliReflection.Members(pipeline.GetType());
            int applied = 0;
            foreach (CucliMemberInfo member in members)
            {
                if (applied++ >= 64) break;
                object value = CucliReflection.Get(pipeline, member.Name, out string ignored);
                settings[member.Name] = CucliDescribe.Value(value, options, 0);
            }

            var serialized = new JObject();
            var serializedObject = new SerializedObject(pipeline);
            foreach (string name in new[]
            {
                "m_DefaultRendererIndex", "m_MainLightRenderingMode", "m_AdditionalLightsRenderingMode",
                "m_SupportsOpaqueTexture", "m_SupportsDepthTexture"
            })
            {
                SerializedProperty property = serializedObject.FindProperty(name);
                if (property != null) serialized[name] = CucliSerialized.Read(property, options, 0);
            }

            return CucliCommandResult.Ok(new
            {
                assetName = pipeline.name,
                assetPath = NullIfEmpty(CucliAssets.GetAssetPath(pipeline)),
                assetType = pipeline.GetType().FullName,
                settings,
                _serialized = serialized
            });
        }

        private static CucliCommandResult PipelineSetSettings(CucliCommandContext context)
        {
            RenderPipelineAsset pipeline = GraphicsSupport.CurrentPipeline();
            if (pipeline == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, "No render pipeline asset found.");
            }

            JObject settings = context.Input.GetObject("settings");
            if (settings == null)
            {
                return CucliCommandResult.Missing(
                    "settings", "Provide a property dictionary, e.g. {\"renderScale\": 1.0}.");
            }

            var changed = new JArray();
            var failed = new JArray();
            Undo.RecordObject(pipeline, "cucli graphics pipeline_set_settings");

            foreach (JProperty property in settings.Properties())
            {
                context.ThrowIfCancelled();

                string error;
                bool handled;
                if (CucliSerialized.TrySet(pipeline, property.Name, property.Value,
                        "cucli graphics pipeline_set_settings", out error, out handled))
                {
                    changed.Add(property.Name);
                    continue;
                }

                if (handled)
                {
                    failed.Add(new JObject { ["key"] = property.Name, ["reason"] = error });
                    continue;
                }

                if (CucliReflection.TrySetPath(pipeline, property.Name, property.Value, out error))
                {
                    changed.Add(property.Name);
                    continue;
                }

                // Fall back to the serialized m_Xxx spelling.
                string serializedName = "m_" + char.ToUpperInvariant(property.Name[0]) + property.Name.Substring(1);
                if (CucliSerialized.TrySet(pipeline, serializedName, property.Value,
                        "cucli graphics pipeline_set_settings", out error, out handled))
                {
                    changed.Add(serializedName);
                    continue;
                }

                failed.Add(new JObject
                {
                    ["key"] = property.Name,
                    ["reason"] = error ?? "the property was not found on " + pipeline.GetType().Name
                });
            }

            EditorUtility.SetDirty(pipeline);
            CucliAssets.SaveAssets();

            if (changed.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "No pipeline setting was applied. Use 'cucli graphics pipeline_get_settings' to list valid names.",
                    new { changed, failed });
            }

            if (failed.Count > 0) context.Warn(failed.Count + " pipeline setting(s) were not applied.");
            return CucliCommandResult.Ok(new { changed, failed },
                "Applied " + changed.Count + " pipeline setting(s).");
        }

        // ---------------------------------------------------------------- light baking

        /// <summary>
        /// Reads <c>Lightmapping.lightingSettings</c> without letting Unity's getter throw.
        ///
        /// The getter raises <c>System.Exception("Lightmapping.lightingSettings is null. Please assign
        /// it to an existing asset or a new instance.")</c> whenever the active scene has no lighting
        /// settings assigned, which is the default for a fresh scene. Every read in this file must go
        /// through here: reading it directly produced unguarded System.Exception failures for
        /// <c>bake_status</c> and <c>bake_get_settings</c> before this existed.
        /// </summary>
        private static LightingSettings SafeLightingSettings()
        {
            try
            {
                return Lightmapping.lightingSettings;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static CucliCommandResult BakeStatus(CucliCommandContext context)
        {
            LightingSettings current = SafeLightingSettings();
            return CucliCommandResult.Ok(new
            {
                isRunning = Lightmapping.isRunning,
                bakedGI = Lightmapping.bakedGI,
                realtimeGI = Lightmapping.realtimeGI,
                lightmapCount = LightmapSettings.lightmaps == null ? 0 : LightmapSettings.lightmaps.Length,
                lightingSettings = current == null ? null : CucliAssets.GetAssetPath(current),
                hasLightingSettings = current != null
            });
        }

        private static CucliCommandResult BakeCancel(CucliCommandContext context)
        {
            if (!Lightmapping.isRunning)
            {
                return CucliCommandResult.Ok(
                    new { cancelled = false, isRunning = false },
                    "No lightmap bake is running.");
            }

            Lightmapping.Cancel();
            return CucliCommandResult.Ok(
                new { cancelled = true, isRunning = Lightmapping.isRunning },
                "The lightmap bake was cancelled.");
        }

        private static CucliCommandResult BakeClear(CucliCommandContext context)
        {
            Lightmapping.Clear();
            CucliAssets.SaveAssets();
            return CucliCommandResult.Ok(
                new { cleared = true, lightmapCount = LightmapSettings.lightmaps == null ? 0 : LightmapSettings.lightmaps.Length },
                "The baked lightmap data was discarded.");
        }

        private static void BakeStart(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            if (EditorApplication.isPlaying)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.WrongMode, "Light baking requires Edit Mode; leave Play Mode first."));
                return;
            }

            if (Lightmapping.isRunning)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.WorkerBusy,
                    "A lightmap bake is already running. Wait for it, or run 'cucli graphics bake_cancel'."));
                return;
            }

            bool async = context.Input.GetBool("async", true);
            int timeoutSeconds = Mathf.Clamp(
                context.Input.GetInt("timeoutSeconds", context.Input.GetInt("timeout_seconds", 1800)), 1, 86400);

            if (!async)
            {
                try
                {
                    Lightmapping.Bake();
                }
                catch (Exception exception)
                {
                    complete(CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "The synchronous lightmap bake failed: " + exception.GetBaseException().Message));
                    return;
                }

                complete(CucliCommandResult.Ok(new
                {
                    mode = "sync",
                    lightmapCount = LightmapSettings.lightmaps == null ? 0 : LightmapSettings.lightmaps.Length
                }, "The synchronous lightmap bake finished."));
                return;
            }

            Lightmapping.BakeAsync();
            if (!Lightmapping.isRunning)
            {
                // A scene with nothing to bake finishes inside BakeAsync.
                complete(CucliCommandResult.Ok(new
                {
                    mode = "async",
                    isRunning = false,
                    lightmapCount = LightmapSettings.lightmaps == null ? 0 : LightmapSettings.lightmaps.Length
                }, "The lightmap bake finished immediately; there was nothing left to bake."));
                return;
            }

            WaitForBake(context, complete, timeoutSeconds, "The lightmap bake finished.");
        }

        private static void BakeReflectionProbe(CucliCommandContext context, Action<CucliCommandResult> complete)
        {
            if (EditorApplication.isPlaying)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.WrongMode, "Reflection probe baking requires Edit Mode; leave Play Mode first."));
                return;
            }

            string error;
            GameObject gameObject = ResolveTarget(context, out error);
            if (gameObject == null)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    error ?? "Pass 'target' (name or instance id) of the GameObject with the ReflectionProbe."));
                return;
            }

            ReflectionProbe probe = gameObject.GetComponent<ReflectionProbe>();
            if (probe == null)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    gameObject.name + " has no ReflectionProbe component."));
                return;
            }

            int timeoutSeconds = Mathf.Clamp(
                context.Input.GetInt("timeoutSeconds", context.Input.GetInt("timeout_seconds", 600)), 1, 86400);

            string outputFolder = "Assets/Lightmaps";
            CucliAssets.EnsureFolder(outputFolder);
            string outputPath = outputFolder + "/" + SafeFileName(probe.name) + "_ReflectionProbe.exr";
            string absolute = CucliAssets.ToAbsolute(outputPath);

            bool started;
            try
            {
                // ReflectionProbe has no Bake() member in 2022.3; Lightmapping owns the entry point.
                started = Lightmapping.BakeReflectionProbe(probe, absolute);
            }
            catch (Exception exception)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Baking the reflection probe failed: " + exception.GetBaseException().Message));
                return;
            }

            if (!started)
            {
                complete(CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "ReflectionProbe.Bake returned false; the probe may be disabled or already baking.",
                    new { probeName = probe.name, instanceId = gameObject.GetInstanceID() }));
                return;
            }

            context.AddArtifact(absolute);
            WaitForBake(context, complete, timeoutSeconds, "The reflection probe finished baking.", new
            {
                probeName = probe.name,
                outputPath,
                fullPath = absolute,
                instanceID = gameObject.GetInstanceID()
            });
        }

        /// <summary>
        /// Polls <c>Lightmapping.isRunning</c> until the bake stops, the timeout expires or the job is
        /// cancelled. Reflection probe bakes also stop the lightmapper, so one loop covers both.
        /// </summary>
        private static void WaitForBake(
            CucliCommandContext context,
            Action<CucliCommandResult> complete,
            int timeoutSeconds,
            string successMessage,
            object successData = null)
        {
            double deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
            bool done = false;

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (done) return;

                if (context.Cancellation.IsCancellationRequested)
                {
                    done = true;
                    EditorApplication.update -= tick;
                    if (Lightmapping.isRunning) Lightmapping.Cancel();
                    complete(CucliCommandResult.Fail(
                        CucliErrors.Cancelled, "The bake was cancelled; Lightmapping.Cancel was requested."));
                    return;
                }

                if (!Lightmapping.isRunning)
                {
                    done = true;
                    EditorApplication.update -= tick;
                    int lightmapCount = LightmapSettings.lightmaps == null ? 0 : LightmapSettings.lightmaps.Length;
                    complete(CucliCommandResult.Ok(new
                    {
                        isRunning = false,
                        lightmapCount,
                        elapsedSeconds = Math.Round(
                            EditorApplication.timeSinceStartup - (deadline - timeoutSeconds), 2),
                        result = successData
                    }, successMessage));
                    return;
                }

                if (EditorApplication.timeSinceStartup < deadline) return;

                done = true;
                EditorApplication.update -= tick;
                complete(CucliCommandResult.Fail(
                    CucliErrors.Timeout,
                    "The bake did not finish within " + timeoutSeconds + " seconds. It is still running; poll "
                    + "'cucli graphics bake_status' or cancel it with 'cucli graphics bake_cancel'.",
                    new { isRunning = Lightmapping.isRunning, timeoutSeconds }));
            };

            EditorApplication.update += tick;
        }

        private static CucliCommandResult BakeGetSettings(CucliCommandContext context)
        {
            LightingSettings settings = SafeLightingSettings();
            if (settings == null)
            {
                LightingSettings created;
                CucliCommandResult failure = EnsureLightingSettings(out created);
                if (failure != null) return failure;
                settings = created;
                context.Warn("No LightingSettings asset existed; one was created.");
            }

            return CucliCommandResult.Ok(new
            {
                name = settings.name,
                path = NullIfEmpty(CucliAssets.GetAssetPath(settings)),
                bakedGI = Lightmapping.bakedGI,
                realtimeGI = Lightmapping.realtimeGI,
                lightmapper = settings.lightmapper.ToString(),
                lightmapResolution = settings.lightmapResolution,
                lightmapMaxSize = settings.lightmapMaxSize,
                directSampleCount = settings.directSampleCount,
                indirectSampleCount = settings.indirectSampleCount,
                environmentSampleCount = settings.environmentSampleCount,
                mixedBakeMode = settings.mixedBakeMode.ToString(),
                lightmapCompression = settings.lightmapCompression.ToString(),
                ao = settings.ao,
                aoMaxDistance = settings.aoMaxDistance,
                bounceCount = GraphicsSupport.PropertyOrNull(settings, "bounceCount", null)
                    ?? GraphicsSupport.PropertyOrNull(settings, "maxBounces", null) as object
            });
        }

        private static CucliCommandResult EnsureLightingSettings(out LightingSettings settings)
        {
            settings = SafeLightingSettings();
            if (settings != null) return null;

            string folder = "Assets/Settings";
            CucliAssets.EnsureFolder(folder);
            string path = folder + "/LightingSettings.asset";
            if (CucliAssets.Exists(path))
            {
                settings = CucliAssets.Load<LightingSettings>(path);
                if (settings != null)
                {
                    Lightmapping.lightingSettings = settings;
                    return null;
                }
            }

            settings = new LightingSettings();
            AssetDatabase.CreateAsset(settings, path);
            CucliAssets.SaveAssets();
            Lightmapping.lightingSettings = settings;
            return null;
        }

        private static CucliCommandResult BakeSetSettings(CucliCommandContext context)
        {
            JObject requested = context.Input.GetObject("settings");
            if (requested == null)
            {
                return CucliCommandResult.Missing(
                    "settings", "Provide a settings dictionary, e.g. {\"bakedGI\": true, \"lightmapResolution\": 40}.");
            }

            LightingSettings settings = SafeLightingSettings();
            if (settings == null)
            {
                CucliCommandResult failure = EnsureLightingSettings(out settings);
                if (failure != null) return failure;
            }

            var changed = new JArray();
            var failed = new JArray();
            Undo.RecordObject(settings, "cucli graphics bake_set_settings");

            foreach (JProperty property in requested.Properties())
            {
                string key = UiCommand.NormalizeStyleKey(property.Name);
                string error = null;
                bool applied;

                switch (key)
                {
                    case "bakedgi":
                        Lightmapping.bakedGI = ReadBool(property.Value);
                        applied = true;
                        break;

                    case "realtimegi":
                        Lightmapping.realtimeGI = ReadBool(property.Value);
                        applied = true;
                        break;

                    case "lightmapper":
                        applied = SetEnumMember(settings, "lightmapper", property.Value, out error);
                        break;

                    case "lightmapresolution":
                        applied = SetNumericMember(settings, "lightmapResolution", property.Value, out error);
                        break;

                    case "lightmapmaxsize":
                        applied = SetNumericMember(settings, "lightmapMaxSize", property.Value, out error);
                        break;

                    case "directsamplecount":
                        applied = SetNumericMember(settings, "directSampleCount", property.Value, out error);
                        break;

                    case "indirectsamplecount":
                        applied = SetNumericMember(settings, "indirectSampleCount", property.Value, out error);
                        break;

                    case "environmentsamplecount":
                        applied = SetNumericMember(settings, "environmentSampleCount", property.Value, out error);
                        break;

                    case "bouncecount":
                    case "maxbounces":
                        applied = SetNumericMember(settings, "bounceCount", property.Value, out error)
                            || SetNumericMember(settings, "maxBounces", property.Value, out error);
                        break;

                    case "mixedbakemode":
                        applied = SetEnumMember(settings, "mixedBakeMode", property.Value, out error);
                        break;

                    case "lightmapcompression":
                    case "compresslightmaps":
                        applied = SetCompression(settings, property.Value, out error);
                        break;

                    case "ao":
                        applied = SetNumericMember(settings, "ao", property.Value, out error);
                        break;

                    case "aomaxdistance":
                        applied = SetNumericMember(settings, "aoMaxDistance", property.Value, out error);
                        break;

                    default:
                        applied = false;
                        error = "unrecognised lighting setting key";
                        break;
                }

                if (applied) changed.Add(CanonicalLightingKey(key));
                else failed.Add(new JObject { ["key"] = property.Name, ["reason"] = error ?? "the value was rejected" });
            }

            EditorUtility.SetDirty(settings);
            CucliAssets.SaveAssets();

            if (changed.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "No lighting setting was applied. Recognised keys: bakedGI, realtimeGI, lightmapper, "
                    + "lightmapResolution, lightmapMaxSize, directSampleCount, indirectSampleCount, "
                    + "environmentSampleCount, bounceCount, mixedBakeMode, lightmapCompression, ao, aoMaxDistance.",
                    new { changed, failed });
            }

            if (failed.Count > 0) context.Warn(failed.Count + " lighting setting(s) were not applied.");
            return CucliCommandResult.Ok(new { changed, failed },
                "Applied " + changed.Count + " lighting setting(s).");
        }

        private static string CanonicalLightingKey(string normalized)
        {
            switch (normalized)
            {
                case "bakedgi": return "bakedGI";
                case "realtimegi": return "realtimeGI";
                case "lightmapresolution": return "lightmapResolution";
                case "lightmapmaxsize": return "lightmapMaxSize";
                case "directsamplecount": return "directSampleCount";
                case "indirectsamplecount": return "indirectSampleCount";
                case "environmentsamplecount": return "environmentSampleCount";
                case "bouncecount": return "bounceCount";
                case "maxbounces": return "maxBounces";
                case "mixedbakemode": return "mixedBakeMode";
                case "lightmapcompression": return "lightmapCompression";
                case "aomaxdistance": return "aoMaxDistance";
                default: return normalized;
            }
        }

        private static bool ReadBool(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return false;
            if (value.Type == JTokenType.Boolean) return value.Value<bool>();
            string text = value.ToString();
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "1", StringComparison.Ordinal);
        }

        private static bool SetNumericMember(object target, string member, JToken value, out string error)
        {
            error = null;
            MemberInfo info = CucliReflection.FindMember(target.GetType(), member);
            if (info == null)
            {
                error = "'" + member + "' is not available on " + target.GetType().Name;
                return false;
            }

            Type memberType = info is FieldInfo ? ((FieldInfo)info).FieldType : ((PropertyInfo)info).PropertyType;
            object converted;
            string convertError;
            try
            {
                converted = CucliValues.Convert(value, memberType, out convertError);
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            if (convertError != null)
            {
                error = convertError;
                return false;
            }

            if (memberType == typeof(bool) && value != null && value.Type != JTokenType.Boolean)
            {
                converted = ReadBool(value);
            }

            return CucliReflection.TrySetMember(target, member, new JValue(converted), out error);
        }

        private static bool SetEnumMember(object target, string member, JToken value, out string error)
        {
            error = null;
            MemberInfo info = CucliReflection.FindMember(target.GetType(), member);
            if (info == null)
            {
                error = "'" + member + "' is not available on " + target.GetType().Name;
                return false;
            }

            Type memberType = info is FieldInfo ? ((FieldInfo)info).FieldType : ((PropertyInfo)info).PropertyType;
            if (!memberType.IsEnum)
            {
                error = "'" + member + "' is not an enum";
                return false;
            }

            try
            {
                object converted = value != null && value.Type == JTokenType.Integer
                    ? Enum.ToObject(memberType, value.Value<int>())
                    : Enum.Parse(memberType, value == null ? string.Empty : value.ToString().Trim(), true);
                return CucliReflection.TrySetMember(target, member, new JValue(converted), out error);
            }
            catch (Exception)
            {
                error = "'" + value + "' is not a valid " + memberType.Name + " value (expected one of: "
                    + string.Join(", ", Enum.GetNames(memberType)) + ")";
                return false;
            }
        }

        private static bool SetCompression(object settings, JToken value, out string error)
        {
            error = null;
            MemberInfo info = CucliReflection.FindMember(settings.GetType(), "lightmapCompression");
            if (info == null)
            {
                error = "lightmapCompression is not available in this Unity version";
                return false;
            }

            Type memberType = info is FieldInfo ? ((FieldInfo)info).FieldType : ((PropertyInfo)info).PropertyType;
            object converted;

            if (value != null && value.Type == JTokenType.Boolean)
            {
                converted = Enum.ToObject(memberType, value.Value<bool>() ? 1 : 0);
            }
            else if (value != null && value.Type == JTokenType.Integer)
            {
                converted = Enum.ToObject(memberType, value.Value<int>());
            }
            else if (value != null && value.Type == JTokenType.String)
            {
                try
                {
                    converted = Enum.Parse(memberType, value.Value<string>().Trim(), true);
                }
                catch (ArgumentException)
                {
                    error = "'" + value + "' is not a valid " + memberType.Name + " value";
                    return false;
                }
            }
            else
            {
                error = "a LightmapCompression name, a boolean or an integer is required";
                return false;
            }

            return CucliReflection.TrySetMember(settings, "lightmapCompression", new JValue(converted), out error);
        }

        private static CucliCommandResult BakeCreateLightProbeGroup(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string name = args.GetString("name") ?? "Light Probes";
            Vector3 position = args.GetVector3("position", Vector3.zero);
            Vector3 grid = args.GetVector3("gridSize", args.GetVector3("grid_size", new Vector3(3, 2, 3)));
            float spacing = args.GetFloat("spacing", 2f);

            int x = Mathf.Max(1, Mathf.RoundToInt(grid.x));
            int y = Mathf.Max(1, Mathf.RoundToInt(grid.y));
            int z = Mathf.Max(1, Mathf.RoundToInt(grid.z));

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "cucli graphics bake_create_light_probe_group");
            gameObject.transform.position = position;

            LightProbeGroup group = gameObject.AddComponent<LightProbeGroup>();
            var positions = new List<Vector3>(x * y * z);
            for (int ix = 0; ix < x; ix++)
            {
                for (int iy = 0; iy < y; iy++)
                {
                    for (int iz = 0; iz < z; iz++)
                    {
                        positions.Add(new Vector3(
                            (ix - (x - 1) * 0.5f) * spacing,
                            iy * spacing,
                            (iz - (z - 1) * 0.5f) * spacing));
                    }
                }
            }

            group.probePositions = positions.ToArray();
            EditorUtility.SetDirty(group);
            Selection.activeGameObject = gameObject;

            return CucliCommandResult.Ok(new
            {
                instanceID = gameObject.GetInstanceID(),
                gameObject = CucliObjectResolver.HierarchyPath(gameObject, true),
                probeCount = positions.Count,
                gridSize = new[] { x, y, z },
                spacing,
                position = new[] { position.x, position.y, position.z }
            }, "Created a light probe group with " + positions.Count + " probes.");
        }

        private static CucliCommandResult BakeCreateReflectionProbe(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string name = args.GetString("name") ?? "Reflection Probe";
            Vector3 position = args.GetVector3("position", Vector3.zero);
            Vector3 size = args.GetVector3("size", new Vector3(10f, 10f, 10f));
            int resolution = Mathf.Clamp(args.GetInt("resolution", 256), 16, 2048);
            bool hdr = args.GetBool("hdr", true);
            bool boxProjection = args.GetBool("boxProjection", args.GetBool("box_projection", false));
            string modeName = args.GetString("mode") ?? "Baked";

            ReflectionProbeMode mode;
            if (!UiCommand.TryParseEnumByName(modeName, out mode))
            {
                return CucliCommandResult.Invalid(
                    "mode", "'" + modeName + "' is not one of: "
                    + string.Join(", ", Enum.GetNames(typeof(ReflectionProbeMode))) + ".");
            }

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "cucli graphics bake_create_reflection_probe");
            gameObject.transform.position = position;

            ReflectionProbe probe = gameObject.AddComponent<ReflectionProbe>();
            probe.mode = mode;
            probe.resolution = resolution;
            probe.hdr = hdr;
            probe.boxProjection = boxProjection;
            probe.size = size;

            EditorUtility.SetDirty(probe);
            Selection.activeGameObject = gameObject;

            return CucliCommandResult.Ok(new
            {
                instanceID = gameObject.GetInstanceID(),
                gameObject = CucliObjectResolver.HierarchyPath(gameObject, true),
                mode = mode.ToString(),
                resolution,
                hdr,
                boxProjection,
                size = new[] { size.x, size.y, size.z },
                position = new[] { position.x, position.y, position.z }
            }, "Created a reflection probe.");
        }

        private static CucliCommandResult BakeSetProbePositions(CucliCommandContext context)
        {
            string error;
            GameObject gameObject = ResolveTarget(context, out error);
            if (gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    error ?? "Pass 'target' (name or instance id) of the GameObject with the LightProbeGroup.");
            }

            LightProbeGroup group = gameObject.GetComponent<LightProbeGroup>();
            if (group == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, gameObject.name + " has no LightProbeGroup component.");
            }

            JArray positions = context.Input.GetArray("positions");
            if (positions == null || positions.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "positions", "Provide [[x,y,z], ...] with at least one entry.");
            }

            var converted = new List<Vector3>(positions.Count);
            for (int index = 0; index < positions.Count; index++)
            {
                JToken entry = positions[index];
                if (entry == null || entry.Type != JTokenType.Array || ((JArray)entry).Count < 3)
                {
                    return CucliCommandResult.Invalid(
                        "positions", "entry " + index + " is not an [x,y,z] array.");
                }

                var array = (JArray)entry;
                converted.Add(new Vector3(
                    array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>()));
            }

            Undo.RecordObject(group, "cucli graphics bake_set_probe_positions");
            group.probePositions = converted.ToArray();
            EditorUtility.SetDirty(group);
            UiCommand.MarkSceneDirty(gameObject);

            return CucliCommandResult.Ok(new
            {
                instanceID = gameObject.GetInstanceID(),
                gameObject = CucliObjectResolver.HierarchyPath(gameObject, true),
                probeCount = converted.Count
            }, "The light probe group now has " + converted.Count + " probes.");
        }

        // ---------------------------------------------------------------- shared helpers

        /// <summary>
        /// Resolves a scene GameObject from <c>target</c> (name or instance id) or <c>name</c>,
        /// defaulting to no object rather than guessing.
        /// </summary>
        internal static GameObject ResolveTarget(CucliCommandContext context, out string error)
        {
            CucliArgs args = context.Input;
            if (!args.Has("target") && !args.Has("instanceId") && !args.Has("instance_id")
                && !args.Has("id") && !args.Has("name") && !args.Has("path") && !args.Has("hierarchyPath"))
            {
                error = "a 'target' (GameObject name, hierarchy path or instance id) is required.";
                return null;
            }

            return CucliObjectResolver.ResolveGameObject(args, out error);
        }

        internal static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        internal static string SafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unnamed";
            var builder = new System.Text.StringBuilder(value.Length);
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            foreach (char character in value)
            {
                builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
            }

            return builder.ToString();
        }
    }
}
