using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>skybox_*</c> actions of the <c>graphics</c> group: the scene environment
    /// (<see cref="RenderSettings"/>) and the skybox material.
    ///
    /// These use only built-in APIs - no optional package is involved, and nothing here touches the
    /// render pipeline asset. The <c>skybox_set_ambient</c> / <c>skybox_set_fog</c> /
    /// <c>skybox_set_reflection</c> / <c>skybox_set_sun</c> split is the MCP surface's, so each
    /// action still validates its own keys (with the documented fallbacks such as <c>mode</c> and
    /// <c>color</c>).
    /// </summary>
    internal static class GraphicsSkyboxCommand
    {
        // ---------------------------------------------------------------- helpers

        private static JArray ColorArray(Color color)
        {
            return new JArray { color.r, color.g, color.b, color.a };
        }

        private static bool TryColor(JToken token, Color fallback, out Color color, out string error)
        {
            error = null;
            color = fallback;
            if (token == null || token.Type == JTokenType.Null) return false;

            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                if (array.Count < 3)
                {
                    error = "a colour array needs at least 3 components.";
                    return false;
                }

                color = new Color(
                    array[0].Value<float>(),
                    array[1].Value<float>(),
                    array[2].Value<float>(),
                    array.Count >= 4 ? array[3].Value<float>() : 1f);
                return true;
            }

            if (token.Type == JTokenType.Object)
            {
                var args = new CucliArgs((JObject)token);
                color = new Color(
                    args.GetFloat("r"), args.GetFloat("g"), args.GetFloat("b"), args.GetFloat("a", 1f));
                return true;
            }

            if (token.Type == JTokenType.String)
            {
                string text = token.Value<string>().Trim();
                if (text.Length > 0 && text[0] != '#') text = "#" + text;
                Color parsed;
                if (ColorUtility.TryParseHtmlString(text, out parsed))
                {
                    color = parsed;
                    return true;
                }

                error = "'" + text + "' is not a parseable colour.";
                return false;
            }

            error = "a '#RRGGBBAA' string, an [r,g,b,a] array or an {r,g,b,a} object is required.";
            return false;
        }

        /// <summary>Reads a colour key with one or two documented fallback spellings.</summary>
        private static bool TryReadColor(CucliArgs args, out Color color, out string error, params string[] keys)
        {
            color = default(Color);
            error = null;
            foreach (string key in keys)
            {
                JToken token = args.Token(key);
                if (token == null || token.Type == JTokenType.Null) continue;
                return TryColor(token, default(Color), out color, out error);
            }

            return false;
        }

        private static bool TryReadBool(CucliArgs args, out bool value, params string[] keys)
        {
            value = false;
            foreach (string key in keys)
            {
                if (!args.Has(key)) continue;
                value = args.GetBool(key, false);
                return true;
            }

            return false;
        }

        private static bool TryReadFloat(CucliArgs args, out float value, params string[] keys)
        {
            value = 0f;
            foreach (string key in keys)
            {
                if (!args.Has(key)) continue;
                value = args.GetFloat(key, 0f);
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- skybox_get

        internal static CucliCommandResult Get(CucliCommandContext context)
        {
            var options = CucliDescribe.OptionsFrom(context.Input);
            JObject skybox = null;

            Material material = RenderSettings.skybox;
            if (material != null)
            {
                skybox = new JObject
                {
                    ["name"] = material.name,
                    ["shader"] = material.shader == null ? null : material.shader.name,
                    ["path"] = GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(material)),
                    ["properties"] = DescribeMaterial(material, options)
                };
            }

            Shader sunShader = Shader.Find("Skybox/Procedural");
            Light sun = RenderSettings.sun;

            return CucliCommandResult.Ok(new
            {
                skybox,
                ambient = new
                {
                    mode = RenderSettings.ambientMode.ToString(),
                    skyColor = ColorArray(RenderSettings.ambientSkyColor),
                    equatorColor = ColorArray(RenderSettings.ambientEquatorColor),
                    groundColor = ColorArray(RenderSettings.ambientGroundColor),
                    ambientLight = ColorArray(RenderSettings.ambientLight),
                    intensity = RenderSettings.ambientIntensity
                },
                fog = new
                {
                    enabled = RenderSettings.fog,
                    mode = RenderSettings.fogMode.ToString(),
                    color = ColorArray(RenderSettings.fogColor),
                    density = RenderSettings.fogDensity,
                    startDistance = RenderSettings.fogStartDistance,
                    endDistance = RenderSettings.fogEndDistance
                },
                reflection = new
                {
                    intensity = RenderSettings.reflectionIntensity,
                    bounces = RenderSettings.reflectionBounces,
                    mode = RenderSettings.defaultReflectionMode.ToString(),
                    resolution = RenderSettings.defaultReflectionResolution,
                    customCubemap = DescribeUnityObject(RenderSettings.customReflectionTexture)
                },
                sun = sun == null
                    ? null
                    : new JObject
                    {
                        ["name"] = sun.name,
                        ["instanceID"] = sun.gameObject.GetInstanceID(),
                        ["lightType"] = sun.type.ToString()
                    },
                subtractiveShadowColor = ColorArray(RenderSettings.subtractiveShadowColor),
                proceduralSkyboxShaderAvailable = sunShader != null
            });
        }

        private static JToken DescribeUnityObject(UnityEngine.Object value)
        {
            if (value == null) return JValue.CreateNull();
            return new JObject
            {
                ["name"] = value.name,
                ["type"] = value.GetType().Name,
                ["path"] = GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(value))
            };
        }

        private static JArray DescribeMaterial(Material material, CucliDescribe.Options options)
        {
            var result = new JArray();
            if (material == null || material.shader == null) return result;

            Shader shader = material.shader;
            int count = shader.GetPropertyCount();
            for (int index = 0; index < count; index++)
            {
                if (result.Count >= options.MaxItems) break;

                string name = shader.GetPropertyName(index);
                if (string.IsNullOrEmpty(name)) continue;

                ShaderPropertyType type = shader.GetPropertyType(index);
                ShaderPropertyFlags flags = shader.GetPropertyFlags(index);

                var entry = new JObject
                {
                    ["name"] = name,
                    ["type"] = type.ToString()
                };

                switch (type)
                {
                    case ShaderPropertyType.Color:
                        entry["value"] = ColorArray(material.GetColor(name));
                        break;

                    case ShaderPropertyType.Vector:
                        Vector4 vector = material.GetVector(name);
                        entry["value"] = new JArray { vector.x, vector.y, vector.z, vector.w };
                        break;

                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        entry["value"] = material.GetFloat(name);
                        break;

                    case ShaderPropertyType.Texture:
                        Texture texture = material.GetTexture(name);
                        entry["value"] = texture == null
                            ? JValue.CreateNull()
                            : new JObject
                            {
                                ["name"] = texture.name,
                                ["path"] = GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(texture))
                            };
                        break;

                    case ShaderPropertyType.Int:
                        entry["value"] = material.GetInteger(name);
                        break;

                    default:
                        entry["value"] = null;
                        break;
                }

                if ((flags & ShaderPropertyFlags.HideInInspector) != 0) entry["hidden"] = true;
                result.Add(entry);
            }

            return result;
        }

        // ---------------------------------------------------------------- skybox_set_material

        internal static CucliCommandResult SetMaterial(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string raw = args.GetString("material") ?? args.GetString("path") ?? args.GetString("materialPath")
                ?? args.GetString("material_path");

            if (string.IsNullOrWhiteSpace(raw))
            {
                return CucliCommandResult.Missing(
                    "material", "A skybox material asset path, e.g. Assets/Settings/Sky.mat.");
            }

            Material material = CucliAssets.Load<Material>(raw);
            if (material == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "Material not found at '" + raw + "'.");
            }

            Undo.RecordObject(RenderSettingsHolder(), "cucli graphics skybox_set_material");
            RenderSettings.skybox = material;
            MarkRenderSettingsDirty();

            return CucliCommandResult.Ok(new
            {
                material = material.name,
                shader = material.shader == null ? null : material.shader.name,
                path = GraphicsCommand.NullIfEmpty(CucliAssets.GetAssetPath(material))
            }, "The skybox material is now " + material.name + ".");
        }

        // ---------------------------------------------------------------- skybox_set_properties

        internal static CucliCommandResult SetProperties(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            JObject properties = args.GetObject("properties") ?? args.GetObject("parameters");
            if (properties == null || properties.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "properties", "Provide a shader property dictionary for the skybox material.");
            }

            Material material = RenderSettings.skybox;
            if (material == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "The scene has no skybox material. Assign one with 'cucli graphics skybox_set_material' first.");
            }

            var set = new JArray();
            var failed = new JArray();
            Undo.RecordObject(material, "cucli graphics skybox_set_properties");

            foreach (JProperty property in properties.Properties())
            {
                context.ThrowIfCancelled();
                string name = property.Name;
                if (!material.HasProperty(name))
                {
                    // Float properties sometimes carry an underscore prefix in the shader.
                    string prefixed = name.StartsWith("_", StringComparison.Ordinal) ? name : "_" + name;
                    if (material.HasProperty(prefixed)) name = prefixed;
                }

                if (!material.HasProperty(name))
                {
                    failed.Add(new JObject { ["key"] = property.Name, ["reason"] = "the shader has no such property" });
                    continue;
                }

                string error;
                if (TryWriteMaterialProperty(material, name, property.Value, out error))
                {
                    set.Add(name);
                }
                else
                {
                    failed.Add(new JObject { ["key"] = property.Name, ["reason"] = error });
                }
            }

            EditorUtility.SetDirty(material);
            if (!Application.isPlaying) UiCommand.MarkSceneDirty(RenderSettingsHolder());

            if (set.Count == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "No skybox material property matched. Run 'cucli graphics skybox_get' to list the shader "
                    + "property names.",
                    new { set, failed });
            }

            if (failed.Count > 0) context.Warn(failed.Count + " skybox property(ies) were not applied.");
            return CucliCommandResult.Ok(new { material = material.name, set, failed },
                "Applied " + set.Count + " skybox property(ies).");
        }

        private static bool TryWriteMaterialProperty(
            Material material, string name, JToken value, out string error)
        {
            error = null;
            Shader shader = material.shader;
            int index = shader == null ? -1 : shader.FindPropertyIndex(name);
            ShaderPropertyType type = index < 0 ? ShaderPropertyType.Float : shader.GetPropertyType(index);

            try
            {
                switch (type)
                {
                    case ShaderPropertyType.Color:
                    {
                        Color color;
                        if (TryColor(value, material.GetColor(name), out color, out error))
                        {
                            material.SetColor(name, color);
                            return true;
                        }

                        return false;
                    }

                    case ShaderPropertyType.Vector:
                    {
                        Vector4 vector;
                        if (value != null && value.Type == JTokenType.Array)
                        {
                            var array = (JArray)value;
                            if (array.Count < 3)
                            {
                                error = "a vector needs at least 3 components.";
                                return false;
                            }

                            vector = new Vector4(
                                array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(),
                                array.Count >= 4 ? array[3].Value<float>() : 0f);
                        }
                        else if (value != null && value.Type == JTokenType.Object)
                        {
                            var nested = new CucliArgs((JObject)value);
                            vector = new Vector4(
                                nested.GetFloat("x"), nested.GetFloat("y"), nested.GetFloat("z"),
                                nested.GetFloat("w"));
                        }
                        else
                        {
                            error = "an [x,y,z(,w)] array is required for a vector property.";
                            return false;
                        }

                        material.SetVector(name, vector);
                        return true;
                    }

                    case ShaderPropertyType.Texture:
                    {
                        string raw = value == null ? null : value.ToString();
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            material.SetTexture(name, null);
                            return true;
                        }

                        Texture texture = CucliAssets.Load<Texture>(raw);
                        if (texture == null)
                        {
                            error = "no texture asset is at '" + raw + "'.";
                            return false;
                        }

                        material.SetTexture(name, texture);
                        return true;
                    }

                    case ShaderPropertyType.Int:
                    {
                        int parsed;
                        if (value == null || !int.TryParse(value.ToString(), out parsed))
                        {
                            error = "an integer is required.";
                            return false;
                        }

                        material.SetInteger(name, parsed);
                        return true;
                    }

                    default:
                    {
                        if (value == null || value.Type == JTokenType.Null)
                        {
                            error = "a number is required.";
                            return false;
                        }

                        float parsed;
                        if (!float.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out parsed))
                        {
                            error = "'" + value + "' is not a number.";
                            return false;
                        }

                        material.SetFloat(name, parsed);
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- skybox_set_ambient

        internal static CucliCommandResult SetAmbient(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            bool changed = false;

            string modeName = args.GetString("ambientMode") ?? args.GetString("ambient_mode")
                ?? args.GetString("mode");
            if (!string.IsNullOrWhiteSpace(modeName))
            {
                AmbientMode mode;
                if (!UiCommand.TryParseEnumByName(modeName, out mode))
                {
                    return CucliCommandResult.Invalid(
                        "ambientMode",
                        "'" + modeName + "' is not one of: "
                        + string.Join(", ", Enum.GetNames(typeof(AmbientMode))) + ".");
                }

                RenderSettings.ambientMode = mode;
                changed = true;
            }

            Color color;
            string colorError;
            if (TryReadColor(args, out color, out colorError, "color", "skyColor", "sky_color"))
            {
                RenderSettings.ambientSkyColor = color;
                changed = true;
            }
            else if (colorError != null)
            {
                return CucliCommandResult.Invalid("color", colorError);
            }

            if (TryReadColor(args, out color, out colorError, "equatorColor", "equator_color"))
            {
                RenderSettings.ambientEquatorColor = color;
                changed = true;
            }
            else if (colorError != null)
            {
                return CucliCommandResult.Invalid("equatorColor", colorError);
            }

            if (TryReadColor(args, out color, out colorError, "groundColor", "ground_color"))
            {
                RenderSettings.ambientGroundColor = color;
                changed = true;
            }
            else if (colorError != null)
            {
                return CucliCommandResult.Invalid("groundColor", colorError);
            }

            float intensity;
            if (TryReadFloat(args, out intensity, "intensity"))
            {
                RenderSettings.ambientIntensity = intensity;
                changed = true;
            }

            if (!changed)
            {
                return CucliCommandResult.Missing(
                    "color",
                    "Provide at least one of: ambientMode (or mode), color/skyColor, equatorColor, groundColor, "
                    + "intensity.");
            }

            MarkRenderSettingsDirty();
            return CucliCommandResult.Ok(new
            {
                mode = RenderSettings.ambientMode.ToString(),
                skyColor = ColorArray(RenderSettings.ambientSkyColor),
                equatorColor = ColorArray(RenderSettings.ambientEquatorColor),
                groundColor = ColorArray(RenderSettings.ambientGroundColor),
                intensity = RenderSettings.ambientIntensity
            }, "The ambient lighting was updated.");
        }

        // ---------------------------------------------------------------- skybox_set_fog

        internal static CucliCommandResult SetFog(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            bool changed = false;

            bool enabled;
            if (TryReadBool(args, out enabled, "fogEnabled", "fog_enabled", "enabled"))
            {
                RenderSettings.fog = enabled;
                changed = true;
            }

            string modeName = args.GetString("fogMode") ?? args.GetString("fog_mode") ?? args.GetString("mode");
            if (!string.IsNullOrWhiteSpace(modeName))
            {
                FogMode mode;
                if (!UiCommand.TryParseEnumByName(modeName, out mode))
                {
                    return CucliCommandResult.Invalid(
                        "fogMode",
                        "'" + modeName + "' is not one of: "
                        + string.Join(", ", Enum.GetNames(typeof(FogMode))) + ".");
                }

                RenderSettings.fogMode = mode;
                changed = true;
            }

            Color color;
            string colorError;
            if (TryReadColor(args, out color, out colorError, "fogColor", "fog_color", "color"))
            {
                RenderSettings.fogColor = color;
                changed = true;
            }
            else if (colorError != null)
            {
                return CucliCommandResult.Invalid("fogColor", colorError);
            }

            float number;
            if (TryReadFloat(args, out number, "fogDensity", "fog_density", "density"))
            {
                RenderSettings.fogDensity = number;
                changed = true;
            }

            if (TryReadFloat(args, out number, "fogStart", "fog_start", "start"))
            {
                RenderSettings.fogStartDistance = number;
                changed = true;
            }

            if (TryReadFloat(args, out number, "fogEnd", "fog_end", "end"))
            {
                RenderSettings.fogEndDistance = number;
                changed = true;
            }

            if (!changed)
            {
                return CucliCommandResult.Missing(
                    "fogEnabled",
                    "Provide at least one of: fogEnabled/enabled, fogMode/mode, fogColor/color, "
                    + "fogDensity/density, fogStart/start, fogEnd/end.");
            }

            MarkRenderSettingsDirty();
            return CucliCommandResult.Ok(new
            {
                enabled = RenderSettings.fog,
                mode = RenderSettings.fogMode.ToString(),
                color = ColorArray(RenderSettings.fogColor),
                density = RenderSettings.fogDensity,
                startDistance = RenderSettings.fogStartDistance,
                endDistance = RenderSettings.fogEndDistance
            }, "The fog settings were updated.");
        }

        // ---------------------------------------------------------------- skybox_set_reflection

        internal static CucliCommandResult SetReflection(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            bool changed = false;

            float number;
            if (TryReadFloat(args, out number, "intensity"))
            {
                RenderSettings.reflectionIntensity = number;
                changed = true;
            }

            if (args.Has("bounces"))
            {
                RenderSettings.reflectionBounces = Mathf.Clamp(args.GetInt("bounces", 1), 1, 5);
                changed = true;
            }

            if (args.Has("resolution"))
            {
                RenderSettings.defaultReflectionResolution = Mathf.Clamp(args.GetInt("resolution", 128), 16, 2048);
                changed = true;
            }

            string modeName = args.GetString("reflectionMode") ?? args.GetString("reflection_mode")
                ?? args.GetString("mode");
            if (!string.IsNullOrWhiteSpace(modeName))
            {
                object parsedMode = null;
                foreach (string candidate in Enum.GetNames(typeof(DefaultReflectionMode)))
                {
                    if (!string.Equals(candidate, modeName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    parsedMode = Enum.Parse(typeof(DefaultReflectionMode), candidate);
                    break;
                }

                if (parsedMode == null)
                {
                    return CucliCommandResult.Invalid(
                        "reflectionMode",
                        "'" + modeName + "' is not one of: "
                        + string.Join(", ", Enum.GetNames(typeof(DefaultReflectionMode))) + ".");
                }

                RenderSettings.defaultReflectionMode = (DefaultReflectionMode)parsedMode;
                changed = true;
            }

            string cubemapPath = args.GetString("path") ?? args.GetString("cubemapPath")
                ?? args.GetString("cubemap_path");
            if (!string.IsNullOrWhiteSpace(cubemapPath))
            {
                Cubemap cubemap = CucliAssets.Load<Cubemap>(cubemapPath);
                if (cubemap == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound, "Cubemap not found at '" + cubemapPath + "'.");
                }

                RenderSettings.customReflectionTexture = cubemap;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                changed = true;
            }

            if (!changed)
            {
                return CucliCommandResult.Missing(
                    "intensity",
                    "Provide at least one of: intensity, bounces, resolution, reflectionMode/mode, "
                    + "path/cubemapPath.");
            }

            MarkRenderSettingsDirty();
            return CucliCommandResult.Ok(new
            {
                intensity = RenderSettings.reflectionIntensity,
                bounces = RenderSettings.reflectionBounces,
                mode = RenderSettings.defaultReflectionMode.ToString(),
                resolution = RenderSettings.defaultReflectionResolution,
                customCubemap = DescribeUnityObject(RenderSettings.customReflectionTexture)
            }, "The reflection settings were updated.");
        }

        // ---------------------------------------------------------------- skybox_set_sun

        internal static CucliCommandResult SetSun(CucliCommandContext context)
        {
            // manage_graphics falls back from 'target' to 'name' here.
            CucliArgs args = context.Input;
            if (!args.Has("target") && args.Has("name"))
            {
                args = new CucliArgs(new JObject
                {
                    ["target"] = context.Input.GetString("name")
                });
            }

            string error;
            GameObject gameObject = CucliObjectResolver.ResolveGameObject(args, out error);
            if (gameObject == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    error ?? "Pass 'target' (name or instance id) of the GameObject carrying the directional Light.");
            }

            Light light = gameObject.GetComponent<Light>();
            if (light == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    gameObject.name + " has no Light component, so it cannot be the skybox sun.");
            }

            if (light.type != LightType.Directional)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    gameObject.name + " carries a " + light.type
                    + " light; the skybox sun must be a Directional light.",
                    new { lightType = light.type.ToString() });
            }

            RenderSettings.sun = light;
            MarkRenderSettingsDirty();

            return CucliCommandResult.Ok(new
            {
                name = light.name,
                instanceID = gameObject.GetInstanceID(),
                lightType = light.type.ToString()
            }, "The skybox sun is now " + light.name + ".");
        }

        // ---------------------------------------------------------------- RenderSettings dirtying

        /// <summary>
        /// RenderSettings lives in the open scene, not in an asset. Unity exposes no
        /// <c>RenderSettings</c> object to record or dirty, so the render settings asset is marked
        /// through the lighting data path instead: every loaded scene is flagged dirty, which is what
        /// makes the change survive a save.
        /// </summary>
        private static GameObject RenderSettingsHolder()
        {
            Light sun = RenderSettings.sun;
            if (sun != null) return sun.gameObject;

            SceneView view = SceneView.lastActiveSceneView;
            if (view != null && view.camera != null) return view.camera.gameObject;

            foreach (GameObject candidate in CucliObjectResolver.FindAllInLoadedScenes()) return candidate;
            return null;
        }

        private static void MarkRenderSettingsDirty()
        {
            if (Application.isPlaying) return;

            for (int index = 0; index < UnityEngine.SceneManagement.SceneManager.sceneCount; index++)
            {
                UnityEngine.SceneManagement.Scene scene =
                    UnityEngine.SceneManagement.SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                try
                {
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                }
                catch (Exception)
                {
                    // Scenes without a path refuse; the value is still applied in memory.
                }
            }
        }
    }
}
