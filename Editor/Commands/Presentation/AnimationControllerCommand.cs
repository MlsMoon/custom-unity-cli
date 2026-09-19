using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// The <c>controller_*</c> actions of the <c>animation</c> group: AnimatorController authoring
    /// through <c>UnityEditor.Animations</c>.
    ///
    /// Everything here is Edit Mode asset work, so the group's <see cref="CucliMode"/> stays
    /// <c>Any</c>; only the runtime <c>animator_*</c> actions decide for themselves whether they need
    /// Play Mode.
    /// </summary>
    internal static class AnimationControllerCommand
    {
        private const string AnyStateAliases = "AnyState / Any / Any State";

        internal static CucliCommandResult Execute(
            CucliCommandContext context, CucliArgs args, string action, string[] supported)
        {
            switch (action)
            {
                case "controller_create": return Create(context, args);
                case "controller_add_state": return AddState(context, args);
                case "controller_add_transition": return AddTransition(context, args);
                case "controller_add_parameter": return AddParameter(context, args);
                case "controller_get_info": return GetInfo(context, args);
                case "controller_assign": return Assign(context, args);
                case "controller_add_layer": return AddLayer(context, args);
                case "controller_remove_layer": return RemoveLayer(context, args);
                case "controller_set_layer_weight": return SetLayerWeight(context, args);
                case "controller_create_blend_tree_1d": return CreateBlendTree(context, args, 1);
                case "controller_create_blend_tree_2d": return CreateBlendTree(context, args, 2);
                case "controller_add_blend_tree_child": return AddBlendTreeChild(context, args);
                default: return CucliCommandResult.Unsupported(action, supported);
            }
        }

        // ---------------------------------------------------------------- helpers

        internal static AnimatorController LoadController(CucliArgs args, out string error)
        {
            error = null;
            string raw = args.GetString("controllerPath") ?? args.GetString("controller_path");
            string path = AnimationCommand.NormalizeControllerPath(raw, out error);
            if (path == null) return null;

            AnimatorController controller = CucliAssets.Load<AnimatorController>(path);
            if (controller == null) error = "no AnimatorController asset is at " + path + ".";
            return controller;
        }

        private static bool TryLayer(
            AnimatorController controller, CucliArgs args, out int index, out string error)
        {
            error = null;
            index = args.GetInt("layerIndex", args.GetInt("layer_index", 0));

            if (index >= 0 && index < controller.layers.Length) return true;

            error = "layer index " + index + " is out of range; the controller has "
                + controller.layers.Length + " layer(s).";
            return false;
        }

        private static bool TryFindLayerByName(AnimatorController controller, string name, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(name)) return false;

            for (int candidate = 0; candidate < controller.layers.Length; candidate++)
            {
                if (!string.Equals(controller.layers[candidate].name, name.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                index = candidate;
                return true;
            }

            return false;
        }

        private static AnimatorState FindState(AnimatorStateMachine machine, string name)
        {
            if (machine == null || string.IsNullOrWhiteSpace(name)) return null;
            return machine.states
                .Select(child => child.state)
                .FirstOrDefault(state => state != null
                    && string.Equals(state.name, name.Trim(), StringComparison.Ordinal));
        }

        private static int CountStates(AnimatorStateMachine machine)
        {
            return machine == null ? 0 : machine.states.Length;
        }

        // ---------------------------------------------------------------- controller_create

        private static CucliCommandResult Create(CucliCommandContext context, CucliArgs args)
        {
            string error;
            string path = AnimationCommand.NormalizeControllerPath(
                args.GetString("controllerPath") ?? args.GetString("controller_path"), out error);
            if (path == null) return CucliCommandResult.Invalid("controllerPath", error);

            if (CucliAssets.Exists(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists, path + " already exists. Delete it first, or pick another path.",
                    new { path });
            }

            if (!AnimationCommand.EnsureParentFolder(path))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The folder for " + path + " could not be created.");
            }

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            if (controller == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "AnimatorController.CreateAnimatorControllerAtPath failed.");
            }

            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                path,
                name = controller.name,
                layerCount = controller.layers.Length,
                parameterCount = controller.parameters.Length
            }, "Created the AnimatorController " + path + ".");
        }

        // ---------------------------------------------------------------- controller_add_state

        private static CucliCommandResult AddState(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string stateName = args.GetString("stateName") ?? args.GetString("state_name");
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return CucliCommandResult.Missing("stateName", "Example: --state-name Idle");
            }

            stateName = stateName.Trim();
            int layerIndex;
            if (!TryLayer(controller, args, out layerIndex, out error))
            {
                return CucliCommandResult.Invalid("layerIndex", error);
            }

            AnimatorStateMachine machine = controller.layers[layerIndex].stateMachine;
            if (FindState(machine, stateName) != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "layer " + layerIndex + " already has a state named '" + stateName + "'.");
            }

            AnimationClip clip = null;
            string clipPath = args.GetString("clipPath") ?? args.GetString("clip_path");
            if (!string.IsNullOrWhiteSpace(clipPath))
            {
                clip = AnimationCommand.LoadClip(clipPath, out error);
                if (clip == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

                string controllerAssetPath = CucliAssets.GetAssetPath(controller);
                if (CucliAssets.GetAssetPath(clip) != controllerAssetPath)
                {
                    // Store the clip beside the controller so the state has no external dependency.
                    AssetDatabase.AddObjectToAsset(clip, controller);
                    EditorUtility.SetDirty(clip);
                }
            }

            AnimatorState state = machine.AddState(stateName);
            if (state == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "AddState returned null.");
            }

            if (clip != null) state.motion = clip;
            state.speed = args.GetFloat("speed", 1f);

            bool isDefault = args.GetBool("isDefault", args.GetBool("is_default", false));
            if (isDefault) machine.defaultState = state;

            EditorUtility.SetDirty(controller);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                stateName = state.name,
                layerIndex,
                hasMotion = state.motion != null,
                motionName = state.motion == null ? null : state.motion.name,
                speed = state.speed,
                isDefault,
                stateCount = CountStates(machine)
            }, "Added the state " + state.name + " to layer " + layerIndex + ".");
        }

        // ---------------------------------------------------------------- controller_add_transition

        private static CucliCommandResult AddTransition(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string fromName = args.GetString("fromState") ?? args.GetString("from_state");
            string toName = args.GetString("toState") ?? args.GetString("to_state");
            if (string.IsNullOrWhiteSpace(fromName))
            {
                return CucliCommandResult.Missing(
                    "fromState", "Use AnyState / Any / Any State for an AnyState transition.");
            }

            if (string.IsNullOrWhiteSpace(toName))
            {
                return CucliCommandResult.Missing("toState", "The destination state must exist on the layer.");
            }

            int layerIndex;
            if (!TryLayer(controller, args, out layerIndex, out error))
            {
                return CucliCommandResult.Invalid("layerIndex", error);
            }

            AnimatorStateMachine machine = controller.layers[layerIndex].stateMachine;
            AnimatorState destination = FindState(machine, toName);
            if (destination == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "layer " + layerIndex + " has no state named '" + toName + "'. Available: "
                    + string.Join(", ", machine.states.Select(child => child.state.name)) + ".");
            }

            AnimatorStateTransition transition;
            if (IsAnyState(fromName))
            {
                transition = machine.AddAnyStateTransition(destination);
            }
            else
            {
                AnimatorState source = FindState(machine, fromName);
                if (source == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        "layer " + layerIndex + " has no state named '" + fromName + "'. Use " + AnyStateAliases
                        + " for an AnyState transition.");
                }

                transition = source.AddTransition(destination);
            }

            if (transition == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The transition could not be created.");
            }

            transition.hasExitTime = args.GetBool("hasExitTime", args.GetBool("has_exit_time", true));
            transition.exitTime = args.GetFloat("exitTime", args.GetFloat("exit_time", 0.75f));
            transition.duration = args.GetFloat("duration", 0.25f);

            int conditionCount = 0;
            JArray conditions = args.GetArray("conditions");
            if (conditions != null)
            {
                foreach (JToken token in conditions)
                {
                    if (token == null || token.Type != JTokenType.Object) continue;

                    var element = (JObject)token;
                    string parameter = element.Value<string>("parameter");
                    if (string.IsNullOrWhiteSpace(parameter)) continue;

                    string modeName = element.Value<string>("mode");
                    if (string.IsNullOrWhiteSpace(modeName)) modeName = "greater";

                    AnimatorConditionMode mode;
                    if (!TryParseConditionMode(modeName, out mode))
                    {
                        return CucliCommandResult.Invalid(
                            "conditions", "'" + modeName + "' is not one of greater, less, equals, notequal, "
                            + "if, ifnot.");
                    }

                    float threshold = element["threshold"] == null ? 0f : element.Value<float>("threshold");
                    transition.AddCondition(mode, threshold, parameter.Trim());
                    conditionCount++;
                }
            }

            EditorUtility.SetDirty(controller);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                fromState = fromName,
                toState = destination.name,
                layerIndex,
                anyState = IsAnyState(fromName),
                hasExitTime = transition.hasExitTime,
                exitTime = transition.exitTime,
                duration = transition.duration,
                conditionCount
            }, "Added a transition to " + destination.name + ".");
        }

        private static bool IsAnyState(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string trimmed = name.Trim();
            return string.Equals(trimmed, "AnyState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Any", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Any State", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseConditionMode(string name, out AnimatorConditionMode mode)
        {
            mode = AnimatorConditionMode.Greater;
            switch (name.Trim().ToLowerInvariant())
            {
                case "greater": mode = AnimatorConditionMode.Greater; return true;
                case "less": mode = AnimatorConditionMode.Less; return true;
                case "equals": mode = AnimatorConditionMode.Equals; return true;
                case "notequal":
                case "not_equal":
                    mode = AnimatorConditionMode.NotEqual;
                    return true;
                case "if":
                case "true":
                    mode = AnimatorConditionMode.If;
                    return true;
                case "ifnot":
                case "if_not":
                case "false":
                    mode = AnimatorConditionMode.IfNot;
                    return true;
                default: return false;
            }
        }

        // ---------------------------------------------------------------- controller_add_parameter

        private static CucliCommandResult AddParameter(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string parameterName = args.GetString("parameterName") ?? args.GetString("parameter_name");
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                return CucliCommandResult.Missing("parameterName", "Example: --parameter-name Speed");
            }

            parameterName = parameterName.Trim();
            foreach (AnimatorControllerParameter existing in controller.parameters)
            {
                if (!string.Equals(existing.name, parameterName, StringComparison.Ordinal)) continue;
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "the controller already has a parameter named '" + parameterName + "'.");
            }

            string typeName = args.GetString("parameterType") ?? args.GetString("parameter_type");
            if (string.IsNullOrWhiteSpace(typeName)) typeName = "float";

            AnimatorControllerParameterType parameterType;
            if (!TryParseParameterType(typeName, out parameterType))
            {
                return CucliCommandResult.Invalid(
                    "parameterType", "'" + typeName + "' is not one of: float, int, bool, trigger.");
            }

            controller.AddParameter(parameterName, parameterType);

            // AddParameter cannot seed a default value, so it is written through the serialized list.
            if (parameterType != AnimatorControllerParameterType.Trigger)
            {
                var serialized = new SerializedObject(controller);
                SerializedProperty parameters = serialized.FindProperty("m_AnimatorParameters");
                if (parameters != null)
                {
                    for (int index = 0; index < parameters.arraySize; index++)
                    {
                        SerializedProperty entry = parameters.GetArrayElementAtIndex(index);
                        SerializedProperty nameProperty = entry.FindPropertyRelative("m_Name");
                        if (nameProperty == null
                            || !string.Equals(nameProperty.stringValue, parameterName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string field = parameterType == AnimatorControllerParameterType.Float
                            ? "m_DefaultFloat"
                            : parameterType == AnimatorControllerParameterType.Int ? "m_DefaultInt" : "m_DefaultBool";
                        SerializedProperty value = entry.FindPropertyRelative(field);
                        if (value == null) break;

                        if (parameterType == AnimatorControllerParameterType.Float)
                        {
                            value.floatValue = args.GetFloat("defaultValue", args.GetFloat("default_value", 0f));
                        }
                        else if (parameterType == AnimatorControllerParameterType.Int)
                        {
                            value.intValue = args.GetInt("defaultValue", args.GetInt("default_value", 0));
                        }
                        else
                        {
                            value.boolValue = args.GetBool("defaultValue", args.GetBool("default_value", false));
                        }

                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        break;
                    }
                }
            }

            EditorUtility.SetDirty(controller);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                parameterName,
                parameterType = parameterType.ToString().ToLowerInvariant(),
                totalParameters = controller.parameters.Length
            }, "Added the parameter " + parameterName + ".");
        }

        private static bool TryParseParameterType(string name, out AnimatorControllerParameterType type)
        {
            type = AnimatorControllerParameterType.Float;
            switch (name.Trim().ToLowerInvariant())
            {
                case "float": type = AnimatorControllerParameterType.Float; return true;
                case "int":
                case "integer":
                    type = AnimatorControllerParameterType.Int;
                    return true;
                case "bool":
                case "boolean":
                    type = AnimatorControllerParameterType.Bool;
                    return true;
                case "trigger": type = AnimatorControllerParameterType.Trigger; return true;
                default: return false;
            }
        }

        // ---------------------------------------------------------------- controller_get_info

        private static CucliCommandResult GetInfo(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            int maxStates = Mathf.Clamp(args.GetInt("maxItems", args.GetInt("max_items", 64)), 1, 500);

            var layers = new JArray();
            for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
            {
                context.ThrowIfCancelled();

                AnimatorControllerLayer layer = controller.layers[layerIndex];
                AnimatorStateMachine machine = layer.stateMachine;
                var states = new JArray();

                if (machine != null)
                {
                    int emitted = 0;
                    foreach (ChildAnimatorState child in machine.states)
                    {
                        if (emitted++ >= maxStates) break;
                        AnimatorState state = child.state;
                        if (state == null) continue;

                        var transitions = new JArray();
                        foreach (AnimatorStateTransition transition in state.transitions)
                        {
                            if (transition == null) continue;
                            transitions.Add(DescribeTransition(transition));
                        }

                        states.Add(new JObject
                        {
                            ["name"] = state.name,
                            ["speed"] = state.speed,
                            ["hasMotion"] = state.motion != null,
                            ["motionName"] = state.motion == null ? null : state.motion.name,
                            ["isDefault"] = machine.defaultState == state,
                            ["transitionCount"] = state.transitions.Length,
                            ["transitions"] = transitions
                        });
                    }
                }

                layers.Add(new JObject
                {
                    ["index"] = layerIndex,
                    ["name"] = layer.name,
                    ["weight"] = layer.defaultWeight,
                    ["blendingMode"] = layer.blendingMode.ToString(),
                    ["stateCount"] = CountStates(machine),
                    ["states"] = states
                });
            }

            var parameters = new JArray();
            foreach (AnimatorControllerParameter parameter in controller.parameters)
            {
                parameters.Add(new JObject
                {
                    ["name"] = parameter.name,
                    ["type"] = parameter.type.ToString().ToLowerInvariant(),
                    ["defaultFloat"] = parameter.defaultFloat,
                    ["defaultInt"] = parameter.defaultInt,
                    ["defaultBool"] = parameter.defaultBool
                });
            }

            return CucliCommandResult.Ok(new
            {
                path = CucliAssets.GetAssetPath(controller),
                name = controller.name,
                layerCount = controller.layers.Length,
                parameterCount = controller.parameters.Length,
                layers,
                parameters
            });
        }

        private static JObject DescribeTransition(AnimatorStateTransition transition)
        {
            var conditions = new JArray();
            foreach (AnimatorCondition condition in transition.conditions)
            {
                conditions.Add(new JObject
                {
                    ["parameter"] = condition.parameter,
                    ["mode"] = condition.mode.ToString(),
                    ["threshold"] = condition.threshold
                });
            }

            return new JObject
            {
                ["destinationState"] = transition.destinationState == null
                    ? null
                    : transition.destinationState.name,
                ["hasExitTime"] = transition.hasExitTime,
                ["exitTime"] = transition.exitTime,
                ["duration"] = transition.duration,
                ["conditionCount"] = transition.conditions.Length,
                ["conditions"] = conditions
            };
        }

        // ---------------------------------------------------------------- controller_assign

        private static CucliCommandResult Assign(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            GameObject gameObject = AnimationCommand.RequireTarget(args, out error);
            if (gameObject == null) return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);

            Animator animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, gameObject.name + " has no Animator component.");
            }

            Undo.RecordObject(animator, "cucli animation controller_assign");
            animator.runtimeAnimatorController = controller;
            EditorUtility.SetDirty(animator);
            UiCommand.MarkSceneDirty(gameObject);

            return CucliCommandResult.Ok(new
            {
                gameObject = CucliObjectResolver.HierarchyPath(gameObject, true),
                controllerName = controller.name,
                controllerPath = CucliAssets.GetAssetPath(controller)
            }, "Assigned " + controller.name + " to " + gameObject.name + ".");
        }

        // ---------------------------------------------------------------- layers

        private static CucliCommandResult AddLayer(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string layerName = args.GetString("layerName") ?? args.GetString("layer_name");
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return CucliCommandResult.Missing("layerName", "Example: --layer-name UpperBody");
            }

            layerName = layerName.Trim();
            int existing;
            if (TryFindLayerByName(controller, layerName, out existing))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "the controller already has a layer named '" + layerName + "' at index " + existing + ".");
            }

            float weight = args.GetFloat("weight", 1f);
            string blendingName = args.GetString("blendingMode") ?? args.GetString("blending_mode");
            AnimatorLayerBlendingMode blendingMode = AnimatorLayerBlendingMode.Override;
            if (!string.IsNullOrWhiteSpace(blendingName)
                && string.Equals(blendingName.Trim(), "additive", StringComparison.OrdinalIgnoreCase))
            {
                blendingMode = AnimatorLayerBlendingMode.Additive;
            }

            controller.AddLayer(layerName);
            int index = controller.layers.Length - 1;
            AnimatorControllerLayer layer = controller.layers[index];
            layer.defaultWeight = weight;
            layer.blendingMode = blendingMode;
            controller.layers = controller.layers;

            EditorUtility.SetDirty(controller);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                controllerPath = CucliAssets.GetAssetPath(controller),
                layerName,
                layerIndex = index,
                weight,
                blendingMode = blendingMode.ToString()
            }, "Added the layer " + layerName + " at index " + index + ".");
        }

        private static CucliCommandResult RemoveLayer(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            if (controller.layers.Length <= 1)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "an AnimatorController must keep at least one layer.");
            }

            int index = ResolveLayerIndex(controller, args, out error);
            if (index < 0) return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);

            string removed = controller.layers[index].name;
            controller.RemoveLayer(index);

            EditorUtility.SetDirty(controller);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                controllerPath = CucliAssets.GetAssetPath(controller),
                layerName = removed,
                layerIndex = index,
                layerCount = controller.layers.Length
            }, "Removed the layer " + removed + ".");
        }

        private static int ResolveLayerIndex(AnimatorController controller, CucliArgs args, out string error)
        {
            error = null;
            string layerName = args.GetString("layerName") ?? args.GetString("layer_name");
            if (!string.IsNullOrWhiteSpace(layerName))
            {
                int byName;
                if (TryFindLayerByName(controller, layerName, out byName)) return byName;
                error = "the controller has no layer named '" + layerName + "'. Available: "
                    + string.Join(", ", controller.layers.Select(layer => layer.name)) + ".";
                return -1;
            }

            if (!args.Has("layerIndex") && !args.Has("layer_index"))
            {
                error = "provide 'layerIndex' or 'layerName'.";
                return -1;
            }

            int index = args.GetInt("layerIndex", args.GetInt("layer_index", -1));
            if (index < 0 || index >= controller.layers.Length)
            {
                error = "layer index " + index + " is out of range; the controller has "
                    + controller.layers.Length + " layer(s).";
                return -1;
            }

            return index;
        }

        private static CucliCommandResult SetLayerWeight(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            int index = ResolveLayerIndex(controller, args, out error);
            if (index < 0) return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);

            float weight = args.GetFloat("weight", 1f);
            AnimatorControllerLayer layer = controller.layers[index];
            layer.defaultWeight = weight;
            controller.layers = controller.layers;

            EditorUtility.SetDirty(controller);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                controllerPath = CucliAssets.GetAssetPath(controller),
                layerName = layer.name,
                layerIndex = index,
                weight
            }, "Set the weight of layer " + layer.name + " to " + weight + ".");
        }

        // ---------------------------------------------------------------- blend trees

        private static CucliCommandResult CreateBlendTree(
            CucliCommandContext context, CucliArgs args, int dimensions)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string stateName = args.GetString("stateName") ?? args.GetString("state_name");
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return CucliCommandResult.Missing("stateName", "Example: --state-name Locomotion");
            }

            stateName = stateName.Trim();
            int layerIndex;
            if (!TryLayer(controller, args, out layerIndex, out error))
            {
                return CucliCommandResult.Invalid("layerIndex", error);
            }

            AnimatorStateMachine machine = controller.layers[layerIndex].stateMachine;
            if (FindState(machine, stateName) != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "layer " + layerIndex + " already has a state named '" + stateName + "'.");
            }

            var tree = new BlendTree { name = stateName, blendType = BlendTreeType.Simple1D, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(tree, controller);

            var parameters = new JArray();
            if (dimensions == 1)
            {
                string blendParameter = args.GetString("blendParameter") ?? args.GetString("blend_parameter");
                if (string.IsNullOrWhiteSpace(blendParameter))
                {
                    return CucliCommandResult.Missing(
                        "blendParameter", "A 1D blend tree needs the blend parameter name.");
                }

                tree.blendParameter = blendParameter.Trim();
                tree.blendType = BlendTreeType.Simple1D;
                parameters.Add(tree.blendParameter);
            }
            else
            {
                string blendParameterX = args.GetString("blendParameterX") ?? args.GetString("blend_parameter_x");
                string blendParameterY = args.GetString("blendParameterY") ?? args.GetString("blend_parameter_y");
                if (string.IsNullOrWhiteSpace(blendParameterX) || string.IsNullOrWhiteSpace(blendParameterY))
                {
                    return CucliCommandResult.Missing(
                        "blendParameterX", "A 2D blend tree needs blendParameterX and blendParameterY.");
                }

                string blendTypeName = args.GetString("blendType") ?? args.GetString("blend_type");
                if (string.IsNullOrWhiteSpace(blendTypeName)) blendTypeName = "simpledirectional2d";

                BlendTreeType blendType;
                if (!TryParseBlendType(blendTypeName, out blendType))
                {
                    return CucliCommandResult.Invalid(
                        "blendType", "'" + blendTypeName + "' is not one of: simpledirectional2d, "
                        + "freeformdirectional2d, freeformcartesian2d.");
                }

                tree.blendParameter = blendParameterX.Trim();
                tree.blendParameterY = blendParameterY.Trim();
                tree.blendType = blendType;
                parameters.Add(tree.blendParameter);
                parameters.Add(tree.blendParameterY);
            }

            AnimatorState state = machine.AddState(stateName);
            state.motion = tree;

            EditorUtility.SetDirty(tree);
            EditorUtility.SetDirty(controller);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                controllerPath = CucliAssets.GetAssetPath(controller),
                stateName = state.name,
                layerIndex,
                blendParameter = tree.blendParameter,
                blendParameterY = dimensions == 2 ? tree.blendParameterY : null,
                blendType = tree.blendType.ToString(),
                blendParameters = parameters
            }, "Created the " + dimensions + "D blend tree state " + state.name + ".");
        }

        private static bool TryParseBlendType(string name, out BlendTreeType type)
        {
            type = BlendTreeType.SimpleDirectional2D;
            switch (UiCommand.NormalizeStyleKey(name))
            {
                case "simpledirectional2d": type = BlendTreeType.SimpleDirectional2D; return true;
                case "freeformdirectional2d": type = BlendTreeType.FreeformDirectional2D; return true;
                case "freeformcartesian2d": type = BlendTreeType.FreeformCartesian2D; return true;
                default: return false;
            }
        }

        private static CucliCommandResult AddBlendTreeChild(CucliCommandContext context, CucliArgs args)
        {
            string error;
            AnimatorController controller = LoadController(args, out error);
            if (controller == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string stateName = args.GetString("stateName") ?? args.GetString("state_name");
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return CucliCommandResult.Missing("stateName", "The blend tree state to add a child to.");
            }

            int layerIndex;
            if (!TryLayer(controller, args, out layerIndex, out error))
            {
                return CucliCommandResult.Invalid("layerIndex", error);
            }

            AnimatorStateMachine machine = controller.layers[layerIndex].stateMachine;
            AnimatorState state = FindState(machine, stateName);
            if (state == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    "layer " + layerIndex + " has no state named '" + stateName.Trim() + "'.");
            }

            var tree = state.motion as BlendTree;
            if (tree == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "the motion of state '" + state.name + "' is "
                    + (state.motion == null ? "empty" : state.motion.GetType().Name)
                    + ", not a BlendTree.");
            }

            AnimationClip clip = AnimationCommand.LoadClip(
                args.GetString("clipPath") ?? args.GetString("clip_path"), out error);
            if (clip == null) return CucliCommandResult.Fail(CucliErrors.AssetNotFound, error);

            string clipAssetPath = CucliAssets.GetAssetPath(clip);
            if (clipAssetPath != CucliAssets.GetAssetPath(controller)) AssetDatabase.AddObjectToAsset(clip, controller);

            if (tree.blendType == BlendTreeType.Simple1D)
            {
                if (!args.Has("threshold"))
                {
                    return CucliCommandResult.Missing(
                        "threshold", "A Simple1D blend tree child needs a 'threshold'.");
                }

                float threshold = args.GetFloat("threshold", 0f);
                tree.AddChild(clip, threshold);

                EditorUtility.SetDirty(tree);
                EditorUtility.SetDirty(controller);
                CucliAssets.SaveAssets();

                return CucliCommandResult.Ok(new
                {
                    controllerPath = CucliAssets.GetAssetPath(controller),
                    stateName = state.name,
                    clipPath = clipAssetPath,
                    threshold,
                    childCount = tree.children.Length
                }, "Added " + clip.name + " to the blend tree at threshold " + threshold + ".");
            }

            JArray position = args.GetArray("position");
            if (position == null || position.Count < 2)
            {
                return CucliCommandResult.Missing(
                    "position", "A " + tree.blendType + " blend tree child needs a [x, y] position.");
            }

            var coordinates = new Vector2(position[0].Value<float>(), position[1].Value<float>());
            tree.AddChild(clip, coordinates);

            EditorUtility.SetDirty(tree);
            EditorUtility.SetDirty(controller);
            CucliAssets.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                controllerPath = CucliAssets.GetAssetPath(controller),
                stateName = state.name,
                clipPath = clipAssetPath,
                position = new { x = coordinates.x, y = coordinates.y },
                childCount = tree.children.Length
            }, "Added " + clip.name + " to the blend tree at (" + coordinates.x + ", " + coordinates.y + ").");
        }
    }
}
