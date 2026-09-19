using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli physics ...</c> - 3D and 2D physics: world settings, the layer collision matrix,
    /// rigidbodies, forces, physics materials, casts and overlaps, manual stepping, joints, and a
    /// scene-wide validation sweep.
    ///
    /// Every action takes <c>dimension</c> as <c>3d</c> or <c>2d</c>. Where it is omitted the
    /// dimension is detected per target - "has a Rigidbody2D and no Rigidbody" means 2D - because
    /// making the caller state it every time is the kind of friction that leads to silently
    /// configuring the wrong physics world.
    ///
    /// There is no optional-package dependency here: this group works in any Unity project.
    /// </summary>
    internal sealed class PhysicsCommand : CucliCommand
    {
        internal const string Dimension3D = "3d";
        internal const string Dimension2D = "2d";

        public override string Name => "physics";

        public override string Summary =>
            "Inspect and configure 3D/2D physics: settings, collision matrix, rigidbodies, forces, materials, casts, stepping, joints.";

        public override CucliActionInfo[] Actions => Declare(
            "ping", "Report the physics readiness summary: gravity, solver settings and simulation mode.",
            "get_settings", "Read the 3D or 2D physics world settings.",
            "set_settings", "Write whitelisted 3D or 2D physics world settings.",
            "get_collision_matrix", "Read the layer collision matrix (lower triangle).",
            "set_collision_matrix", "Enable or disable collision between two layers.",
            "get_rigidbody", "Read a Rigidbody or Rigidbody2D.",
            "configure_rigidbody", "Write whitelisted Rigidbody or Rigidbody2D properties.",
            "apply_force", "Apply a force, torque or explosion to a rigidbody.",
            "create_physics_material", "Create a PhysicMaterial (3D) or PhysicsMaterial2D resource.",
            "configure_physics_material", "Write whitelisted physics material properties.",
            "assign_physics_material", "Assign a physics material to a collider on a target.",
            "raycast", "Cast a ray and return the first hit.",
            "raycast_all", "Cast a ray and return every hit.",
            "linecast", "Cast between two points and return the first hit.",
            "shapecast", "Sweep a sphere, box or capsule and return the first hit.",
            "overlap", "Return every collider overlapping a sphere, box or capsule.",
            "simulate_step", "Advance the physics world manually and report rigidbody motion.",
            "add_joint", "Add a joint to a target.",
            "configure_joint", "Configure an existing joint's properties, motor, limits, spring or drive.",
            "remove_joint", "Remove joints from a target.",
            "validate", "Sweep the loaded scenes for common physics misconfigurations.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => true;

        public override string[] Effects => new[]
        {
            "project.settings.write", "editor.scene.read", "editor.scene.write", "project.assets.write"
        };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": true,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""ping"", ""get_settings"", ""set_settings"", ""get_collision_matrix"", ""set_collision_matrix"",
                 ""get_rigidbody"", ""configure_rigidbody"", ""apply_force"",
                 ""create_physics_material"", ""configure_physics_material"", ""assign_physics_material"",
                 ""raycast"", ""raycast_all"", ""linecast"", ""shapecast"", ""overlap"", ""simulate_step"",
                 ""add_joint"", ""configure_joint"", ""remove_joint"", ""validate""]
    },
    ""dimension"": { ""type"": ""string"", ""enum"": [""3d"", ""2d"", ""both""], ""description"": ""3d (default), 2d, or both for validate."" },
    ""target"": { ""type"": ""string"", ""description"": ""GameObject name, hierarchy path or instance id."" },
    ""searchMethod"": { ""type"": ""string"", ""description"": ""Name, hierarchy path, instance id, tag, layer or component."" },
    ""settings"": { ""type"": ""object"", ""description"": ""Whitelisted world settings to write."" },
    ""layerA"": { ""type"": ""string"", ""description"": ""Layer name or 0-31 index."" },
    ""layerB"": { ""type"": ""string"", ""description"": ""Layer name or 0-31 index."" },
    ""collide"": { ""type"": ""boolean"", ""default"": true },
    ""properties"": { ""type"": ""object"", ""description"": ""Whitelisted properties to write."" },
    ""forceType"": { ""type"": ""string"", ""enum"": [""normal"", ""explosion""], ""default"": ""normal"" },
    ""force"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""torque"": {},
    ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""forceMode"": { ""type"": ""string"" },
    ""explosionForce"": { ""type"": ""number"" },
    ""explosionPosition"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""explosionRadius"": { ""type"": ""number"" },
    ""upwardsModifier"": { ""type"": ""number"", ""default"": 0 },
    ""name"": { ""type"": ""string"" },
    ""path"": { ""type"": ""string"" },
    ""materialPath"": { ""type"": ""string"" },
    ""colliderType"": { ""type"": ""string"" },
    ""componentIndex"": { ""type"": ""integer"" },
    ""origin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""direction"": {},
    ""start"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""end"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""maxDistance"": { ""type"": ""number"" },
    ""layerMask"": {},
    ""queryTriggerInteraction"": { ""type"": ""string"", ""enum"": [""UseGlobal"", ""Ignore"", ""Collide""] },
    ""shape"": { ""type"": ""string"" },
    ""size"": {},
    ""angle"": { ""type"": ""number"" },
    ""height"": { ""type"": ""number"" },
    ""capsuleDirection"": { ""type"": ""integer"" },
    ""point1"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""point2"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""steps"": { ""type"": ""integer"", ""default"": 1 },
    ""stepSize"": { ""type"": ""number"" },
    ""jointType"": { ""type"": ""string"" },
    ""connectedBody"": { ""type"": ""string"" },
    ""motor"": { ""type"": ""object"" },
    ""limits"": { ""type"": ""object"" },
    ""spring"": { ""type"": ""object"" },
    ""drive"": { ""type"": ""object"" },
    ""pageSize"": { ""type"": ""integer"", ""default"": 50 },
    ""cursor"": { ""type"": ""integer"", ""default"": 0 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = Normalize(ResolveAction(context, "ping"));
            switch (action)
            {
                case "ping": return Ping(context);
                case "get_settings": return GetSettings(context);
                case "set_settings": return SetSettings(context);
                case "get_collision_matrix": return GetCollisionMatrix(context);
                case "set_collision_matrix": return SetCollisionMatrix(context);
                case "get_rigidbody": return PhysicsBodies.GetRigidbody(context);
                case "configure_rigidbody": return PhysicsBodies.ConfigureRigidbody(context);
                case "apply_force": return PhysicsBodies.ApplyForce(context);
                case "create_physics_material": return PhysicsMaterials.Create(context);
                case "configure_physics_material": return PhysicsMaterials.Configure(context);
                case "assign_physics_material": return PhysicsMaterials.Assign(context);
                case "raycast": return PhysicsQueries.Raycast(context, false);
                case "raycast_all": return PhysicsQueries.Raycast(context, true);
                case "linecast": return PhysicsQueries.Linecast(context);
                case "shapecast": return PhysicsQueries.ShapeCast(context);
                case "overlap": return PhysicsQueries.Overlap(context);
                case "simulate_step": return SimulateStep(context);
                case "add_joint": return PhysicsJoints.Add(context);
                case "configure_joint": return PhysicsJoints.Configure(context);
                case "remove_joint": return PhysicsJoints.Remove(context);
                case "validate": return Validate(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }

        /// <summary>
        /// Accepts <c>get_settings</c>, <c>get_settings</c> and <c>getSettings</c> for the same
        /// action. The CLI's own convention is hyphenated, the MCP source spells the same operations
        /// with underscores, and making a caller remember which is which is pure friction.
        /// </summary>
        internal static string Normalize(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return string.Empty;
            return action.Trim().ToLowerInvariant().Replace('-', '_');
        }

        /// <summary>Reads <c>dimension</c>, defaulting to 3D. <c>both</c> is only valid for validate.</summary>
        internal static bool ResolveDimension(CucliArgs args, bool allowBoth, out string dimension, out string error)
        {
            dimension = Dimension3D;
            error = null;
            string requested = (args.GetString("dimension") ?? Dimension3D).Trim().ToLowerInvariant();
            if (requested.Length == 0) requested = Dimension3D;

            if (requested == Dimension3D || requested == Dimension2D)
            {
                dimension = requested;
                return true;
            }

            if (allowBoth && requested == "both")
            {
                dimension = "both";
                return true;
            }

            error = "dimension must be '3d'"
                + (allowBoth ? ", '2d' or 'both'" : " or '2d'")
                + " (got '" + requested + "').";
            return false;
        }

        /// <summary>
        /// Detects the physics world a target belongs to: 2D only when it has a Rigidbody2D and no
        /// Rigidbody. A plain collider is not enough to call it 2D, and a target with both is treated
        /// as 3D because that is the world whose settings actually drive it.
        /// </summary>
        internal static string DetectDimension(GameObject target)
        {
            if (target == null) return Dimension3D;
            bool has3D = target.GetComponent<Rigidbody>() != null;
            bool has2D = target.GetComponent<Rigidbody2D>() != null;
            if (has2D && !has3D) return Dimension2D;
            return Dimension3D;
        }

        internal static string ResolveTargetDimension(CucliArgs args, GameObject target, out string error)
        {
            error = null;
            string requested = args.GetString("dimension");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string normalized = requested.Trim().ToLowerInvariant();
                if (normalized == Dimension3D || normalized == Dimension2D) return normalized;
                error = "dimension must be '3d' or '2d' (got '" + requested + "').";
                return null;
            }

            return DetectDimension(target);
        }

        private static CucliCommandResult Ping(CucliCommandContext context)
        {
            return CucliCommandResult.Ok(new
            {
                tool = "physics",
                gravity3d = CucliDescribe.Value(Physics.gravity, null, 0),
                gravity2d = CucliDescribe.Value(Physics2D.gravity, null, 0),
                simulationMode = Physics.simulationMode.ToString(),
                simulationMode2d = Physics2D.simulationMode.ToString(),
                defaultSolverIterations = Physics.defaultSolverIterations,
                defaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations,
                bounceThreshold = Physics.bounceThreshold,
                sleepThreshold = Physics.sleepThreshold,
                defaultContactOffset = Physics.defaultContactOffset,
                queriesHitTriggers = Physics.queriesHitTriggers,
                autoSyncTransforms = Physics.autoSyncTransforms
            });
        }

        // ------------------------------------------------------------------ world settings

        private static CucliCommandResult GetSettings(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            if (dimension == Dimension2D)
            {
                return CucliCommandResult.Ok(new
                {
                    dimension = Dimension2D,
                    gravity = CucliDescribe.Value(Physics2D.gravity, null, 0),
                    velocityIterations = Physics2D.velocityIterations,
                    positionIterations = Physics2D.positionIterations,
                    queriesHitTriggers = Physics2D.queriesHitTriggers,
                    queriesStartInColliders = Physics2D.queriesStartInColliders,
                    callbacksOnDisable = Physics2D.callbacksOnDisable,
                    autoSyncTransforms = Physics2D.autoSyncTransforms,
                    simulationMode = Physics2D.simulationMode.ToString()
                });
            }

            return CucliCommandResult.Ok(new
            {
                dimension = Dimension3D,
                gravity = CucliDescribe.Value(Physics.gravity, null, 0),
                defaultContactOffset = Physics.defaultContactOffset,
                sleepThreshold = Physics.sleepThreshold,
                defaultSolverIterations = Physics.defaultSolverIterations,
                defaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations,
                bounceThreshold = Physics.bounceThreshold,
                defaultMaxAngularSpeed = Physics.defaultMaxAngularSpeed,
                queriesHitTriggers = Physics.queriesHitTriggers,
                queriesHitBackfaces = Physics.queriesHitBackfaces,
                autoSyncTransforms = Physics.autoSyncTransforms,
                simulationMode = Physics.simulationMode.ToString()
            });
        }

        private static readonly HashSet<string> Settings3D = new HashSet<string>(StringComparer.Ordinal)
        {
            "gravity", "defaultcontactoffset", "sleepthreshold", "defaultsolveriterations",
            "defaultsolvervelocityiterations", "bouncethreshold", "defaultmaxangularspeed",
            "querieshittriggers", "querieshitbackfaces", "simulationmode", "autosynctransforms"
        };

        private static readonly HashSet<string> Settings2D = new HashSet<string>(StringComparer.Ordinal)
        {
            "gravity", "velocityiterations", "positioniterations", "querieshittriggers",
            "queriesstartincolliders", "callbacksondisable", "autosynctransforms"
        };

        private static CucliCommandResult SetSettings(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            JObject settings = context.Input.GetObject("settings") ?? context.Input.GetObjectOrFile("settings");
            if (settings == null || settings.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "settings",
                    "Pass an object of whitelisted keys, e.g. --settings '{\"gravity\":[0,-9.81,0]}'.");
            }

            HashSet<string> whitelist = dimension == Dimension2D ? Settings2D : Settings3D;
            var unknown = new JArray();
            var changed = new JArray();

            foreach (JProperty property in settings.Properties())
            {
                string key = property.Name.ToLowerInvariant();
                if (!whitelist.Contains(key))
                {
                    unknown.Add(property.Name);
                    continue;
                }

                string failure;
                if (!ApplySetting(dimension, key, property.Value, out failure))
                {
                    return CucliCommandResult.Invalid(key, failure);
                }

                changed.Add(key);
            }

            if (unknown.Count > 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "Unknown " + dimension + " physics setting(s): " + string.Join(", ", unknown.Select(item => item.Value<string>()))
                    + ". Known keys: " + string.Join(", ", whitelist.OrderBy(item => item, StringComparer.Ordinal)) + ".",
                    new { unknown, allowed = whitelist.OrderBy(item => item, StringComparer.Ordinal).ToArray() });
            }

            return CucliCommandResult.Ok(new { dimension, changed }, "已写入 " + changed.Count + " 项物理设置。");
        }

        private static bool ApplySetting(string dimension, string key, JToken value, out string error)
        {
            error = null;
            try
            {
                if (dimension == Dimension2D)
                {
                    switch (key)
                    {
                        case "gravity":
                        {
                            Vector2 gravity;
                            if (!ReadVector2(value, out gravity, out error)) return false;
                            Physics2D.gravity = gravity;
                            return true;
                        }
                        case "velocityiterations": Physics2D.velocityIterations = value.Value<int>(); return true;
                        case "positioniterations": Physics2D.positionIterations = value.Value<int>(); return true;
                        case "querieshittriggers": Physics2D.queriesHitTriggers = value.Value<bool>(); return true;
                        case "queriesstartincolliders": Physics2D.queriesStartInColliders = value.Value<bool>(); return true;
                        case "callbacksondisable": Physics2D.callbacksOnDisable = value.Value<bool>(); return true;
                        case "autosynctransforms": Physics2D.autoSyncTransforms = value.Value<bool>(); return true;
                        case "simulationmode":
                            // Physics2D has its own enum type; reusing the 3D one would not compile.
                            Physics2D.simulationMode = ParseSimulationMode2D(value);
                            return true;
                    }
                }
                else
                {
                    switch (key)
                    {
                        case "gravity":
                        {
                            Vector3 gravity;
                            if (!ReadVector3(value, out gravity, out error)) return false;
                            Physics.gravity = gravity;
                            return true;
                        }
                        case "defaultcontactoffset": Physics.defaultContactOffset = value.Value<float>(); return true;
                        case "sleepthreshold": Physics.sleepThreshold = value.Value<float>(); return true;
                        case "defaultsolveriterations": Physics.defaultSolverIterations = value.Value<int>(); return true;
                        case "defaultsolvervelocityiterations": Physics.defaultSolverVelocityIterations = value.Value<int>(); return true;
                        case "bouncethreshold": Physics.bounceThreshold = value.Value<float>(); return true;
                        case "defaultmaxangularspeed": Physics.defaultMaxAngularSpeed = value.Value<float>(); return true;
                        case "querieshittriggers": Physics.queriesHitTriggers = value.Value<bool>(); return true;
                        case "querieshitbackfaces": Physics.queriesHitBackfaces = value.Value<bool>(); return true;
                        case "autosynctransforms": Physics.autoSyncTransforms = value.Value<bool>(); return true;
                        case "simulationmode":
                            Physics.simulationMode = ParseSimulationMode(value);
                            return true;
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            error = "not a settable setting.";
            return false;
        }

        private static SimulationMode ParseSimulationMode(JToken value)
        {
            string text = value.Type == JTokenType.String ? value.Value<string>() : value.ToString();
            return (SimulationMode)Enum.Parse(typeof(SimulationMode), (text ?? "FixedUpdate").Trim(), true);
        }

        private static SimulationMode2D ParseSimulationMode2D(JToken value)
        {
            string text = value.Type == JTokenType.String ? value.Value<string>() : value.ToString();
            return (SimulationMode2D)Enum.Parse(typeof(SimulationMode2D), (text ?? "FixedUpdate").Trim(), true);
        }

        internal static bool ReadVector3(JToken value, out Vector3 result, out string error)
        {
            result = Vector3.zero;
            error = null;
            var array = value as JArray;
            if (array == null || array.Count < 3)
            {
                error = "expected an array of at least 3 numbers.";
                return false;
            }

            result = new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
            return true;
        }

        internal static bool ReadVector2(JToken value, out Vector2 result, out string error)
        {
            result = Vector2.zero;
            error = null;
            var array = value as JArray;
            if (array == null || array.Count < 2)
            {
                error = "expected an array of at least 2 numbers.";
                return false;
            }

            result = new Vector2(array[0].Value<float>(), array[1].Value<float>());
            return true;
        }

        // ------------------------------------------------------------------ collision matrix

        private static CucliCommandResult GetCollisionMatrix(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            var layers = new JArray();
            for (int index = 0; index < 32; index++)
            {
                layers.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = LayerMask.LayerToName(index)
                });
            }

            // Only the lower triangle is reported: the matrix is symmetric, and emitting both halves
            // doubles the payload for no information.
            var matrix = new JObject();
            for (int row = 0; row < 32; row++)
            {
                string rowName = LayerLabel(row);
                var rowObject = new JObject();
                for (int column = 0; column <= row; column++)
                {
                    bool collides = dimension == Dimension2D
                        ? !Physics2D.GetIgnoreLayerCollision(row, column)
                        : !Physics.GetIgnoreLayerCollision(row, column);
                    rowObject[LayerLabel(column)] = collides;
                }

                matrix[rowName] = rowObject;
            }

            return CucliCommandResult.Ok(new { dimension, layers, matrix });
        }

        private static string LayerLabel(int index)
        {
            string name = LayerMask.LayerToName(index);
            return string.IsNullOrEmpty(name) ? index.ToString() : name;
        }

        private static CucliCommandResult SetCollisionMatrix(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            int layerA;
            int layerB;
            if (!PhysicsSupport.ResolveLayer(context.Input, "layerA", out layerA, out error))
            {
                return CucliCommandResult.Invalid("layerA", error);
            }

            if (!PhysicsSupport.ResolveLayer(context.Input, "layerB", out layerB, out error))
            {
                return CucliCommandResult.Invalid("layerB", error);
            }

            bool collide = context.Input.GetBool("collide", true);
            if (dimension == Dimension2D) Physics2D.IgnoreLayerCollision(layerA, layerB, !collide);
            else Physics.IgnoreLayerCollision(layerA, layerB, !collide);

            // The layer collision matrix lives in ProjectSettings/DynamicsManager.asset (3D) and
            // Physics2DSettings.asset (2D). IgnoreLayerCollision mutates the live settings object, so
            // SaveAssets is what makes the change survive a restart.
            //
            // The serialized m_LayerCollisionMatrix array is deliberately NOT hand-written: its bit
            // polarity (1 = collides or 1 = ignores) cannot be confirmed by reading the API surface,
            // and writing it inverted would silently corrupt the whole project's collision matrix.
            // The authoritative API is used instead, and the message states what was persisted.
            AssetDatabase.SaveAssets();

            return CucliCommandResult.Ok(new
            {
                layerA = LayerLabel(layerA),
                layerB = LayerLabel(layerB),
                collide,
                dimension,
                persisted = dimension == Dimension2D
                    ? "Physics2DSettings.asset"
                    : "DynamicsManager.asset",
                saved = true
            }, (collide ? "已恢复" : "已关闭") + "两层之间的 " + dimension + " 碰撞，并写入 "
                + (dimension == Dimension2D ? "Physics2DSettings.asset" : "DynamicsManager.asset") + "。");
        }

        // ------------------------------------------------------------------ simulate

        private static CucliCommandResult SimulateStep(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            int steps = Mathf.Clamp(context.Input.GetInt("steps", 1), 1, 100);
            float stepSize = context.Input.GetFloat("stepSize", 0f);
            if (stepSize <= 0f) stepSize = Time.fixedDeltaTime;

            GameObject target = null;
            string targetText = context.Input.GetString("target");
            if (!string.IsNullOrWhiteSpace(targetText))
            {
                target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
                if (error != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
                }
            }

            if (dimension == Dimension2D)
            {
                for (int index = 0; index < steps; index++) Physics2D.Simulate(stepSize);
            }
            else
            {
                // Physics.Simulate only runs while the world is in Script mode, and leaving the
                // project in Script mode would silently stop normal simulation, so the previous mode
                // is restored. SimulationMode.Unknown is not restorable and is left alone.
                SimulationMode previous = Physics.simulationMode;
                // Unity 2022.3's SimulationMode has only FixedUpdate / Update / Script - there is no
                // "Unknown" member to test for, so only Script is special-cased.
                bool restore = previous != SimulationMode.Script;
                if (restore) Physics.simulationMode = SimulationMode.Script;
                try
                {
                    for (int index = 0; index < steps; index++) Physics.Simulate(stepSize);
                }
                finally
                {
                    if (restore) Physics.simulationMode = previous;
                }
            }

            var bodies = new JArray();
            if (dimension == Dimension2D)
            {
                IEnumerable<Rigidbody2D> found = target != null
                    ? new[] { target.GetComponent<Rigidbody2D>() }.Where(item => item != null)
                    : UnityEngine.Object.FindObjectsOfType<Rigidbody2D>()
                        .Where(item => item != null && item.bodyType != RigidbodyType2D.Static && !item.IsSleeping());
                foreach (Rigidbody2D body in found.Take(50))
                {
                    bodies.Add(new JObject
                    {
                        ["name"] = body.name,
                        ["instanceID"] = body.GetInstanceID(),
                        ["position"] = CucliDescribe.Value(body.position, null, 0),
                        ["velocity"] = CucliDescribe.Value(body.velocity, null, 0),
                        ["angularVelocity"] = body.angularVelocity
                    });
                }
            }
            else
            {
                IEnumerable<Rigidbody> found = target != null
                    ? new[] { target.GetComponent<Rigidbody>() }.Where(item => item != null)
                    : UnityEngine.Object.FindObjectsOfType<Rigidbody>()
                        .Where(item => item != null && !item.isKinematic && !item.IsSleeping());
                foreach (Rigidbody body in found.Take(50))
                {
                    bodies.Add(new JObject
                    {
                        ["name"] = body.name,
                        ["instanceID"] = body.GetInstanceID(),
                        ["position"] = CucliDescribe.Value(body.position, null, 0),
                        ["velocity"] = CucliDescribe.Value(body.velocity, null, 0),
                        ["angularVelocity"] = CucliDescribe.Value(body.angularVelocity, null, 0)
                    });
                }
            }

            return CucliCommandResult.Ok(new
            {
                steps_executed = steps,
                step_size = stepSize,
                dimension,
                rigidbodies = bodies
            }, "已手动步进物理 " + steps + " 次。");
        }

        // ------------------------------------------------------------------ validation

        private static CucliCommandResult Validate(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!ResolveDimension(context.Input, true, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            bool check3D = dimension != Dimension2D;
            bool check2D = dimension != Dimension3D;

            var warnings = new List<string>();
            var summary = new JObject
            {
                ["non_convex_mesh"] = 0,
                ["missing_rigidbody"] = 0,
                ["non_uniform_scale"] = 0,
                ["fast_object_discrete"] = 0,
                ["missing_physics_material"] = 0,
                ["collision_matrix"] = 0,
                ["mixed_2d_3d"] = 0
            };

            Action<string> bump = key => summary[key] = summary[key].Value<int>() + 1;

            GameObject single = null;
            int scanned = 0;
            string targetText = context.Input.GetString("target");
            if (!string.IsNullOrWhiteSpace(targetText))
            {
                single = CucliObjectResolver.ResolveGameObject(context.Input, out error);
                if (error != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
                }

                scanned = 1;
                CheckObject(single, check3D, check2D, warnings, bump);
            }
            else
            {
                foreach (GameObject candidate in CucliObjectResolver.FindAllInLoadedScenes())
                {
                    if (candidate == null) continue;
                    scanned++;
                    CheckObject(candidate, check3D, check2D, warnings, bump);
                }

                if (dimension == "both")
                {
                    for (int row = 0; row < 32; row++)
                    {
                        for (int column = 0; column <= row; column++)
                        {
                            if (row == column) continue;
                            bool collides3D = !Physics.GetIgnoreLayerCollision(row, column);
                            bool collides2D = !Physics2D.GetIgnoreLayerCollision(row, column);
                            if (collides3D == collides2D) continue;
                            bump("collision_matrix");
                            warnings.Add("Layer collision matrix differs between 3D and 2D for '"
                                + LayerLabel(row) + "' vs '" + LayerLabel(column) + "'.");
                            if (warnings.Count > 400) break;
                        }
                    }
                }

                context.Warn("Scanned " + scanned + " GameObjects across the loaded scenes.");
            }

            int pageSize = Mathf.Clamp(context.Input.GetInt("pageSize", 50), 1, 500);
            int cursor = Mathf.Max(0, context.Input.GetInt("cursor", 0));
            if (cursor > warnings.Count) cursor = warnings.Count;

            int end = Math.Min(cursor + pageSize, warnings.Count);
            var page = new JArray();
            for (int index = cursor; index < end; index++) page.Add(warnings[index]);

            return CucliCommandResult.Ok(new
            {
                warnings = page,
                warning_count = warnings.Count,
                objects_scanned = scanned,
                page_size = pageSize,
                cursor,
                next_cursor = end < warnings.Count ? (int?)end : null,
                summary
            });
        }

        private static void CheckObject(
            GameObject candidate,
            bool check3D,
            bool check2D,
            List<string> warnings,
            Action<string> bump)
        {
            Vector3 scale = candidate.transform.lossyScale;
            float smallest = Mathf.Min(Mathf.Abs(scale.x), Mathf.Min(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            float largest = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            if (largest - smallest > 0.0001f && candidate.GetComponent<Collider>() != null)
            {
                bump("non_uniform_scale");
                warnings.Add(candidate.name + ": non-uniform scale on a collider can produce skewed physics.");
            }

            var meshCollider = candidate.GetComponent<MeshCollider>();
            if (check3D && meshCollider != null && !meshCollider.convex && meshCollider.sharedMesh != null)
            {
                bool dynamic = false;
                Rigidbody attached = meshCollider.attachedRigidbody;
                if (attached != null && !attached.isKinematic) dynamic = true;
                if (dynamic)
                {
                    bump("non_convex_mesh");
                    warnings.Add(candidate.name + ": non-convex MeshCollider on a dynamic Rigidbody is not simulated.");
                }
            }

            Rigidbody body3D = candidate.GetComponent<Rigidbody>();
            Rigidbody2D body2D = candidate.GetComponent<Rigidbody2D>();
            if (body3D != null && body2D != null)
            {
                bump("mixed_2d_3d");
                warnings.Add(candidate.name + ": has both Rigidbody and Rigidbody2D.");
            }

            if (check3D && body3D != null)
            {
                if (body3D.collisionDetectionMode == CollisionDetectionMode.Discrete
                    && body3D.velocity.magnitude > 20f)
                {
                    bump("fast_object_discrete");
                    warnings.Add(candidate.name + ": discrete collision detection with velocity "
                        + body3D.velocity.magnitude.ToString("0.#") + " may tunnel.");
                }

                Collider collider = candidate.GetComponent<Collider>();
                if (collider != null && collider.sharedMaterial == null)
                {
                    bump("missing_physics_material");
                    warnings.Add(candidate.name + ": collider has no physics material (friction and bounciness default to the project settings).");
                }
            }

            if (check2D && body2D != null)
            {
                Collider2D collider2D = candidate.GetComponent<Collider2D>();
                if (collider2D != null && collider2D.sharedMaterial == null)
                {
                    bump("missing_physics_material");
                    warnings.Add(candidate.name + ": Collider2D has no physics material.");
                }
            }

            bool hasCollider = candidate.GetComponent<Collider>() != null || candidate.GetComponent<Collider2D>() != null;
            bool isStaticCandidate = candidate.isStatic;
            if (check3D && hasCollider && body3D == null && !isStaticCandidate)
            {
                bump("missing_rigidbody");
                warnings.Add(candidate.name + ": has a collider but no Rigidbody and is not marked static.");
            }
        }
    }

    /// <summary>Layer resolution shared by the physics actions, reading both camelCase and snake_case keys.</summary>
    internal static class PhysicsSupport
    {
        internal static bool ResolveLayer(CucliArgs args, string key, out int layer, out string error)
        {
            layer = 0;
            error = null;
            string snake = ToSnake(key);
            string raw = args.GetString(key) ?? args.GetString(snake);
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "required (expects a layer name or a 0-31 index).";
                return false;
            }

            raw = raw.Trim();
            int numeric;
            if (int.TryParse(raw, out numeric))
            {
                if (numeric < 0 || numeric > 31)
                {
                    error = "layer index must be between 0 and 31 (got " + numeric + ").";
                    return false;
                }

                layer = numeric;
                return true;
            }

            int byName = LayerMask.NameToLayer(raw);
            if (byName < 0)
            {
                error = "no layer is named '" + raw + "'.";
                return false;
            }

            layer = byName;
            return true;
        }

        /// <summary>Resolves a layer mask: an integer bitmask, or a single layer name.</summary>
        internal static bool ResolveLayerMask(CucliArgs args, out int mask, out string error)
        {
            mask = ~0;
            error = null;
            string snake = ToSnake("layerMask");
            JToken token = args.Token("layerMask") ?? args.Token(snake);
            if (token == null || token.Type == JTokenType.Null) return true;

            if (token.Type == JTokenType.Integer)
            {
                mask = token.Value<int>();
                return true;
            }

            string raw = (token.Type == JTokenType.String ? token.Value<string>() : token.ToString()).Trim();
            if (string.IsNullOrEmpty(raw)) return true;

            int numeric;
            if (int.TryParse(raw, out numeric))
            {
                mask = numeric;
                return true;
            }

            int layer = LayerMask.NameToLayer(raw);
            if (layer < 0)
            {
                error = "Unknown layer name: '" + raw + "'.";
                return false;
            }

            mask = 1 << layer;
            return true;
        }

        internal static bool ResolveTriggerInteraction(CucliArgs args, out QueryTriggerInteraction interaction, out string error)
        {
            interaction = QueryTriggerInteraction.UseGlobal;
            error = null;
            string raw = args.GetString("queryTriggerInteraction") ?? args.GetString("query_trigger_interaction");
            if (string.IsNullOrWhiteSpace(raw)) return true;
            try
            {
                interaction = (QueryTriggerInteraction)Enum.Parse(typeof(QueryTriggerInteraction), raw.Trim(), true);
                return true;
            }
            catch (ArgumentException)
            {
                error = "queryTriggerInteraction must be UseGlobal, Ignore or Collide (got '" + raw + "').";
                return false;
            }
        }

        internal static string ToSnake(string camel)
        {
            if (string.IsNullOrEmpty(camel)) return camel;
            var builder = new System.Text.StringBuilder();
            for (int index = 0; index < camel.Length; index++)
            {
                char character = camel[index];
                if (char.IsUpper(character))
                {
                    if (index > 0) builder.Append('_');
                    builder.Append(char.ToLowerInvariant(character));
                }
                else
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        /// <summary>Reads a key, accepting camelCase, snake_case and the MCP spelling.</summary>
        internal static JToken Token(CucliArgs args, string camel)
        {
            return args.Token(camel) ?? args.Token(ToSnake(camel)) ?? args.Token(camel.ToLowerInvariant());
        }
    }
}
