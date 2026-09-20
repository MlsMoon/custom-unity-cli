using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>vfx_*</c> family of the <c>vfx</c> group (23 actions) — VFX Graph.
    ///
    /// Every member here reaches the package through <see cref="CucliOptional"/> reflection. The
    /// assembly has no reference to <c>Unity.VisualEffectGraph.Runtime</c> and no
    /// <c>#if UNITY_VFX_GRAPH</c>: this package uses one uniform "optional means reflected" policy, so
    /// a project without VFX Graph still compiles and still gets <c>particle_*</c>, <c>line_*</c> and
    /// <c>trail_*</c>.
    /// </summary>
    internal static class VfxGraphCommand
    {
        private const string DefaultFolder = "Assets/VFX";

        /// <summary>Template search roots, in priority order: package first, then the project.</summary>
        private static readonly string[] TemplateRoots =
        {
            "Packages/com.unity.visualeffectgraph/Editor/Templates",
            "Packages/com.unity.visualeffectgraph/Samples",
            "Assets/VFX/Templates"
        };

        internal static CucliCommandResult Handle(CucliCommandContext context, string action)
        {
            // The whole family is gated, matching the MCP tool where every vfx_* action sat inside
            // #if UNITY_VFX_GRAPH and a missing package produced one identical refusal.
            CucliCommandResult missing = VfxSupport.RequireVfx("VFX Graph（vfx_* 命令）");
            if (missing != null) return missing;

            switch (action)
            {
                case "vfx_create_asset": return CreateAsset(context);
                case "vfx_assign_asset": return AssignAsset(context);
                case "vfx_list_templates": return ListTemplates(context);
                case "vfx_list_assets": return ListAssets(context);
                case "vfx_get_info": return GetInfo(context);
                case "vfx_set_float": return SetScalar(context, "SetFloat", typeof(float));
                case "vfx_set_int": return SetScalar(context, "SetInt", typeof(int));
                case "vfx_set_bool": return SetScalar(context, "SetBool", typeof(bool));
                case "vfx_set_vector2": return SetVector(context, "SetVector2", 2);
                case "vfx_set_vector3": return SetVector(context, "SetVector3", 3);
                case "vfx_set_vector4": return SetVector(context, "SetVector4", 4);
                case "vfx_set_color": return SetColor(context);
                case "vfx_set_gradient": return SetGradient(context);
                case "vfx_set_texture": return SetObject(context, "SetTexture", "texturePath", typeof(Texture));
                case "vfx_set_mesh": return SetObject(context, "SetMesh", "meshPath", typeof(Mesh));
                case "vfx_set_curve": return SetCurve(context);
                case "vfx_send_event": return SendEvent(context);
                case "vfx_play": return Playback(context, "play");
                case "vfx_stop": return Playback(context, "stop");
                case "vfx_pause": return Playback(context, "pause");
                case "vfx_reinit": return Playback(context, "reinit");
                case "vfx_set_playback_speed": return SetPlaybackSpeed(context);
                case "vfx_set_seed": return SetSeed(context);
                default:
                    return CucliCommandResult.Unsupported(action, VfxCommand.GraphActions);
            }
        }

        // ------------------------------------------------------------------ assets

        private static CucliCommandResult CreateAsset(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string assetName = args.GetString("assetName") ?? args.GetString("asset_name");
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return CucliCommandResult.Missing("assetName", "vfx_create_asset 需要 assetName。");
            }

            assetName = assetName.Trim();
            if (assetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return CucliCommandResult.Invalid("assetName", "文件名含非法字符。");
            }

            string folder = args.GetString("folderPath") ?? args.GetString("folder_path") ?? DefaultFolder;
            string normalizedFolder = CucliAssets.NormalizeAssetPath(folder);
            if (normalizedFolder == null)
            {
                return CucliCommandResult.Invalid("folderPath", "路径越界或非法：" + folder);
            }

            if (!CucliAssets.EnsureFolder(normalizedFolder))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "无法创建目录 " + normalizedFolder + "。");
            }

            string targetPath = normalizedFolder + "/" + assetName + ".vfx";
            bool overwrite = args.GetBool("overwrite", false);
            if (AssetDatabase.LoadMainAssetAtPath(targetPath) != null)
            {
                if (!overwrite)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetExists,
                        "资产已存在：" + targetPath + "。Set overwrite=true to replace.",
                        new { assetPath = targetPath });
                }

                AssetDatabase.DeleteAsset(targetPath);
            }

            string template = args.GetString("template") ?? "empty";
            string templatePath = null;
            if (!string.IsNullOrWhiteSpace(template)
                && !string.Equals(template.Trim(), "empty", StringComparison.OrdinalIgnoreCase))
            {
                templatePath = FindTemplate(template);
                if (templatePath == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound,
                        "找不到模板 '" + template + "'。先用 vfx_list_templates 选一个模板，或省略 template 以创建空白资产。",
                        new { template, available = ListTemplateEntries() });
                }
            }

            if (templatePath != null)
            {
                if (!AssetDatabase.CopyAsset(templatePath, targetPath))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "从模板复制失败：" + templatePath + " -> " + targetPath + "。");
                }
            }
            else
            {
                Type assetType = VfxSupport.VfxAssetType();
                var asset = ScriptableObject.CreateInstance(assetType);
                if (asset == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed,
                        "无法实例化 VisualEffectAsset（已安装的 VFX Graph 版本可能与本命令预期不同）。");
                }

                AssetDatabase.CreateAsset(asset, targetPath);
            }

            AssetDatabase.SaveAssets();
            context.AddArtifact(CucliAssets.ToAbsolute(targetPath));

            return CucliCommandResult.Ok(new
            {
                assetPath = targetPath,
                assetName,
                template = templatePath == null ? "empty" : Path.GetFileNameWithoutExtension(templatePath),
                copiedFrom = templatePath
            }, "已创建 VFX 资产 " + targetPath + "。");
        }

        private static CucliCommandResult AssignAsset(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string requested = args.GetString("assetPath") ?? args.GetString("asset_path");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing("assetPath", "vfx_assign_asset 需要 assetPath。");
            }

            string normalized;
            string pathError;
            if (!TryNormalizeVfxAssetPath(requested, out normalized, out pathError))
            {
                return CucliCommandResult.Invalid("assetPath", pathError);
            }

            var asset = AssetDatabase.LoadAssetAtPath(normalized, VfxSupport.VfxAssetType());
            if (asset == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "在 " + normalized + " 找不到 VisualEffectAsset。",
                    new { assetPath = normalized });
            }

            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(args, "vfx_assign_asset", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            Undo.RecordObject(component, "cucli vfx_assign_asset");
            if (!CucliOptional.SetProperty(component, "visualEffectAsset", asset))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.MemberNotFound,
                    "VisualEffect.visualEffectAsset 属性不可写（已安装的包版本可能与本命令预期不同）。");
            }

            EditorUtility.SetDirty(component);

            return CucliCommandResult.Ok(new
            {
                gameObject = component.gameObject.name,
                assetName = asset.name,
                assetPath = normalized
            }, "已把 " + asset.name + " 绑定到 " + component.gameObject.name + "。");
        }

        private static CucliCommandResult ListTemplates(CucliCommandContext context)
        {
            List<object> entries = ListTemplateEntries();
            return CucliCommandResult.Ok(new
            {
                count = entries.Count,
                templates = entries
            }, entries.Count == 0
                ? "没有找到 VFX 模板；创建资产时省略 template 即可得到空白资产。"
                : "共 " + entries.Count + " 个模板。");
        }

        private static List<object> ListTemplateEntries()
        {
            var entries = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string root in TemplateRoots)
            {
                if (!CucliAssets.FolderExists(root) && CucliAssets.LoadMain(root) == null) continue;

                string source = root.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ? "package" : "project";
                foreach (string path in CucliAssets.FindAssets("VisualEffectAsset", root, null, 500))
                {
                    if (!seen.Add(path)) continue;
                    entries.Add(new { name = Path.GetFileNameWithoutExtension(path), path, source });
                }
            }

            return entries;
        }

        private static string FindTemplate(string template)
        {
            string name = template.Trim();
            string[] patterns =
            {
                name + ".vfx",
                "VFX" + name + ".vfx",
                "Simple" + name + ".vfx",
                name + "VFX.vfx"
            };

            var candidates = new List<string>();
            foreach (string root in TemplateRoots)
            {
                if (!CucliAssets.FolderExists(root) && CucliAssets.LoadMain(root) == null) continue;
                candidates.AddRange(CucliAssets.FindAssets("VisualEffectAsset", root, null, 500));
            }

            foreach (string pattern in patterns)
            {
                string exact = candidates.FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), pattern, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }

            string partial = candidates.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (partial != null) return partial;

            string[] global = CucliAssets.FindAssets("VisualEffectAsset", null, name, 1);
            return global.Length > 0 ? global[0] : null;
        }

        private static CucliCommandResult ListAssets(CucliCommandContext context)
        {
            CucliArgs args = context.Input;
            string folder = args.GetString("folder");
            string search = args.GetString("search");

            string normalizedFolder = null;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                normalizedFolder = CucliAssets.NormalizeAssetPath(folder);
                if (normalizedFolder == null)
                {
                    return CucliCommandResult.Invalid("folder", "路径越界或非法：" + folder);
                }
            }

            CucliDescribe.Options options = CucliDescribe.OptionsFrom(context.Input);
            string[] paths = CucliAssets.FindAssets(
                "VisualEffectAsset", normalizedFolder, search, Math.Max(1, options.MaxItems));

            var assets = new JArray();
            foreach (string path in paths)
            {
                assets.Add(new JObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(path),
                    ["path"] = path,
                    ["guid"] = CucliAssets.AssetPathToGuid(path)
                });
            }

            return CucliCommandResult.Ok(new
            {
                count = assets.Count,
                assets,
                truncated = assets.Count >= options.MaxItems
            }, "找到 " + assets.Count + " 个 VFX 资产。");
        }

        private static bool TryNormalizeVfxAssetPath(string requested, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            string value = requested.Trim();
            if (value.IndexOf('\\') >= 0) { error = "路径不能包含反斜杠。"; return false; }
            if (value.Contains("..")) { error = "路径不能包含 '..'。"; return false; }
            if (Path.IsPathRooted(value)) { error = "必须使用项目相对路径（Assets/...）。"; return false; }
            if (value.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                error = "不能写入 Packages/ 下的包资产。";
                return false;
            }

            if (!value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) value = "Assets/" + value;
            if (!value.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase)) value += ".vfx";

            string resolved = CucliAssets.NormalizeAssetPath(value);
            if (resolved == null || !resolved.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = "路径越界：最终解析为 " + (resolved ?? "null") + "。";
                return false;
            }

            normalized = resolved;
            return true;
        }

        // ------------------------------------------------------------------ read

        private static CucliCommandResult GetInfo(CucliCommandContext context)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx_get_info", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            object asset = CucliOptional.GetProperty(component, "visualEffectAsset");
            var assetObject = asset as Object;

            return CucliCommandResult.Ok(new
            {
                gameObject = component.gameObject.name,
                path = CucliObjectResolver.HierarchyPath(component.gameObject, true),
                assetName = assetObject == null ? null : assetObject.name,
                assetPath = assetObject == null ? null : CucliAssets.GetAssetPath(assetObject),
                aliveParticleCount = AsInt(CucliOptional.GetProperty(component, "aliveParticleCount")),
                culled = AsBool(CucliOptional.GetProperty(component, "culled")),
                pause = AsBool(CucliOptional.GetProperty(component, "pause")),
                playRate = AsFloat(CucliOptional.GetProperty(component, "playRate")),
                startSeed = AsUInt(CucliOptional.GetProperty(component, "startSeed"))
            });
        }

        // ------------------------------------------------------------------ parameter writers

        private static CucliCommandResult SetScalar(CucliCommandContext context, string method, Type valueType)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            string parameter = args.GetString("parameter");
            if (string.IsNullOrWhiteSpace(parameter))
            {
                return CucliCommandResult.Fail(CucliErrors.MissingParameter, "Parameter name required");
            }

            if (!args.Has("value"))
            {
                return CucliCommandResult.Missing("value", method + " 需要 value。");
            }

            string convertError;
            object value = CucliValues.Convert(args.Token("value"), valueType, out convertError);
            if (convertError != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "Invalid value for " + parameter + ": " + convertError);
            }

            Undo.RecordObject(component, "cucli " + method);
            object ignored;
            if (!VfxSupport.TryInvokeInstance(component, method, new[] { parameter, value }, out ignored, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.MemberNotFound, error);
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(
                new { parameter, method, value = CucliDescribe.Value(value, null, 0) }, method + " 已应用。");
        }

        /// <summary>
        /// Vector writers. The MCP contract parses every vector action as a Vector4 and then truncates,
        /// and takes the value from <c>value</c> (not <c>position</c>). Both details are preserved so a
        /// migrated call keeps working.
        /// </summary>
        private static CucliCommandResult SetVector(CucliCommandContext context, string method, int dimensions)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            string parameter = args.GetString("parameter");
            if (string.IsNullOrWhiteSpace(parameter))
            {
                return CucliCommandResult.Fail(CucliErrors.MissingParameter, "Parameter name required");
            }

            JToken token = args.Token("value") ?? args.Token("position");
            if (token == null)
            {
                return CucliCommandResult.Missing("value", method + " 需要 value（[x,y,z] 或 {x,y,z}）。");
            }

            string convertError;
            object parsed = CucliValues.Convert(token, typeof(Vector4), out convertError);
            if (convertError != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "Invalid value for " + parameter + ": " + convertError);
            }

            Vector4 vector = (Vector4)parsed;
            object argument;
            if (dimensions == 2) argument = new Vector2(vector.x, vector.y);
            else if (dimensions == 3) argument = new Vector3(vector.x, vector.y, vector.z);
            else argument = vector;

            Undo.RecordObject(component, "cucli " + method);
            object ignored;
            if (!VfxSupport.TryInvokeInstance(component, method, new[] { parameter, argument }, out ignored, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.MemberNotFound, error);
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(
                new { parameter, method, value = CucliDescribe.Value(argument, null, 0) }, method + " 已应用。");
        }

        private static CucliCommandResult SetColor(CucliCommandContext context)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx_set_color", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            string parameter = args.GetString("parameter");
            if (string.IsNullOrWhiteSpace(parameter))
            {
                return CucliCommandResult.Fail(CucliErrors.MissingParameter, "Parameter name required");
            }

            Color color = args.GetColor("value", Color.white);

            Undo.RecordObject(component, "cucli vfx_set_color");
            object ignored;
            if (!VfxSupport.TryInvokeInstance(component, "SetColor", new object[] { parameter, color }, out ignored, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.MemberNotFound, error);
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(
                new { parameter, color = CucliDescribe.Value(color, null, 0) }, "vfx_set_color 已应用。");
        }

        private static CucliCommandResult SetGradient(CucliCommandContext context)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx_set_gradient", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            string parameter = args.GetString("parameter");
            if (string.IsNullOrWhiteSpace(parameter))
            {
                return CucliCommandResult.Fail(CucliErrors.MissingParameter, "Parameter name required");
            }

            // The gradient key is `gradient`, not `value`; MCP is explicit about this and a migrated
            // call that used `value` would otherwise silently get the default gradient.
            Gradient gradient = VfxSupport.ParseGradient(args.Token("gradient"));

            Undo.RecordObject(component, "cucli vfx_set_gradient");
            object ignored;
            if (!VfxSupport.TryInvokeInstance(component, "SetGradient", new object[] { parameter, gradient }, out ignored, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.MemberNotFound, error);
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(new { parameter }, "vfx_set_gradient 已应用。");
        }

        private static CucliCommandResult SetObject(
            CucliCommandContext context, string method, string pathKey, Type objectType)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            string parameter = args.GetString("parameter");
            string path = args.GetString(pathKey) ?? args.GetString(pathKey == "texturePath" ? "texture_path" : "mesh_path");
            if (string.IsNullOrWhiteSpace(parameter) || string.IsNullOrWhiteSpace(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.MissingParameter, "Parameter and " + pathKey + " required");
            }

            string normalized = CucliAssets.NormalizeAssetPath(path);
            var asset = normalized == null ? null : AssetDatabase.LoadAssetAtPath(normalized, objectType);
            if (asset == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound,
                    "找不到 " + objectType.Name + "：" + (normalized ?? path),
                    new { path = normalized ?? path });
            }

            Undo.RecordObject(component, "cucli " + method);
            object ignored;
            if (!VfxSupport.TryInvokeInstance(component, method, new object[] { parameter, asset }, out ignored, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.MemberNotFound, error);
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(
                new { parameter, assetPath = normalized, asset = asset.name }, method + " 已应用。");
        }

        private static CucliCommandResult SetCurve(CucliCommandContext context)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx_set_curve", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            string parameter = args.GetString("parameter");
            if (string.IsNullOrWhiteSpace(parameter))
            {
                return CucliCommandResult.Fail(CucliErrors.MissingParameter, "Parameter name required");
            }

            AnimationCurve curve = VfxSupport.ParseCurve(args.Token("curve"), 1f);

            Undo.RecordObject(component, "cucli vfx_set_curve");
            object ignored;
            if (!VfxSupport.TryInvokeInstance(component, "SetAnimationCurve", new object[] { parameter, curve }, out ignored, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.MemberNotFound, error);
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(
                new { parameter, keyCount = curve.keys.Length }, "vfx_set_curve 已应用。");
        }

        // ------------------------------------------------------------------ events

        private static CucliCommandResult SendEvent(CucliCommandContext context)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx_send_event", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            string eventName = args.GetString("eventName") ?? args.GetString("event_name");
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return CucliCommandResult.Fail(CucliErrors.MissingParameter, "Event name required");
            }

            Type attributeType = CucliOptional.FindType(VfxSupport.VfxGraphAssembly, "UnityEngine.VFX.VFXEventAttribute");
            if (attributeType == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.OptionalPackageMissing,
                    "反射不到 VFXEventAttribute（已安装的 VFX Graph 版本可能与本命令预期不同）。",
                    new { type = "UnityEngine.VFX.VFXEventAttribute" });
            }

            object attribute;
            if (!VfxSupport.TryInvokeInstance(component, "CreateVFXEventAttribute", Array.Empty<object>(), out attribute, out error)
                || attribute == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, error ?? "CreateVFXEventAttribute 返回 null。");
            }

            var written = new List<string>();
            if (args.Has("position"))
            {
                TryAttributeSet(attribute, "SetVector3", "position", args.GetVector3("position"), written, "position");
            }

            if (args.Has("velocity"))
            {
                TryAttributeSet(attribute, "SetVector3", "velocity", args.GetVector3("velocity"), written, "velocity");
            }

            if (args.Has("color"))
            {
                // RGB only: the MCP contract writes a Vector3 and drops alpha, because a VFX event
                // attribute has no colour slot that carries alpha.
                Color color = args.GetColor("color", Color.white);
                TryAttributeSet(
                    attribute, "SetVector3", "color", new Vector3(color.r, color.g, color.b), written, "color(rgb)");
            }

            if (args.Has("size")) TryAttributeSet(attribute, "SetFloat", "Size", args.GetFloat("size"), written, "size");
            if (args.Has("lifetime")) TryAttributeSet(attribute, "SetFloat", "Lifetime", args.GetFloat("lifetime"), written, "lifetime");

            object ignored;
            if (!VfxSupport.TryInvokeInstance(
                    component, "SendEvent", new[] { eventName, attribute }, out ignored, out error))
            {
                // A runtime-only API can legitimately be unavailable in Edit Mode.
                return CucliCommandResult.Fail(
                    CucliErrors.WrongMode,
                    "SendEvent 调用失败：" + error + "。事件需要 Play Mode 下运行中的 VisualEffect。",
                    new { eventName });
            }

            return CucliCommandResult.Ok(
                new { eventName, attributes = written }, "已发送事件 " + eventName + "。");
        }

        private static void TryAttributeSet(
            object attribute, string method, string name, object value, List<string> written, string label)
        {
            object ignored;
            string error;
            if (VfxSupport.TryInvokeInstance(attribute, method, new[] { name, value }, out ignored, out error))
            {
                written.Add(label);
            }
        }

        // ------------------------------------------------------------------ playback

        private static CucliCommandResult Playback(CucliCommandContext context, string mode)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx_" + mode, out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            if (mode == "pause")
            {
                // Toggle, not idempotent pause — the MCP contract is explicit and callers depend on it.
                bool current = AsBool(CucliOptional.GetProperty(component, "pause"));
                CucliOptional.SetProperty(component, "pause", !current);
            }
            else
            {
                string method = mode == "play" ? "Play" : mode == "stop" ? "Stop" : "Reinit";
                object ignored;
                if (!VfxSupport.TryInvokeInstance(component, method, Array.Empty<object>(), out ignored, out error))
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, method + " 调用失败：" + error);
                }
            }

            EditorUtility.SetDirty(component);

            return CucliCommandResult.Ok(new
            {
                action = "vfx_" + mode,
                isPaused = AsBool(CucliOptional.GetProperty(component, "pause"))
            }, "VFX " + mode + " 已执行。");
        }

        private static CucliCommandResult SetPlaybackSpeed(CucliCommandContext context)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx_set_playback_speed", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            float rate = context.Input.GetFloat("playRate", context.Input.GetFloat("play_rate", 1f));
            if (!CucliOptional.SetProperty(component, "playRate", rate))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.MemberNotFound, "VisualEffect.playRate 不可写。");
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(new { playRate = rate }, "vfx_set_playback_speed 已应用。");
        }

        private static CucliCommandResult SetSeed(CucliCommandContext context)
        {
            Component component;
            string error;
            if (!VfxSupport.TryResolveReflectedComponent(context.Input, "vfx_set_seed", out component, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            CucliArgs args = context.Input;
            long raw = args.GetLong("seed", 0L);
            uint seed = raw < 0 ? 0u : (uint)raw;
            bool reset = args.GetBool("resetSeedOnPlay", args.GetBool("reset_seed_on_play", true));

            bool seedWritten = CucliOptional.SetProperty(component, "startSeed", seed);
            bool resetWritten = CucliOptional.SetProperty(component, "resetSeedOnPlay", reset);
            if (!seedWritten && !resetWritten)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.MemberNotFound, "VisualEffect.startSeed / resetSeedOnPlay 均不可写。");
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(new { seed, resetSeedOnPlay = reset }, "vfx_set_seed 已应用。");
        }

        // ------------------------------------------------------------------ value helpers

        private static int AsInt(object value)
        {
            return value == null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool AsBool(object value)
        {
            return value != null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static float AsFloat(object value)
        {
            return value == null ? 0f : Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static long AsUInt(object value)
        {
            return value == null ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
