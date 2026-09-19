using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Unity 6 renamed PhysicMaterial to PhysicsMaterial. One alias keeps the rest of this file free of
// version switches, which is worth far more than the two lines it costs.
#if UNITY_6000_0_OR_NEWER
using PhysicsMaterial3D = UnityEngine.PhysicsMaterial;
#else
using PhysicsMaterial3D = UnityEngine.PhysicMaterial;
#endif

namespace CustomUnityCli.Editor
{
    /// <summary>Rigidbody and force actions for the <c>physics</c> group.</summary>
    internal static class PhysicsBodies
    {
        internal static CucliCommandResult GetRigidbody(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (error != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string dimension = PhysicsCommand.ResolveTargetDimension(context.Input, target, out error);
            if (dimension == null)
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            if (dimension == PhysicsCommand.Dimension2D)
            {
                Rigidbody2D body = target.GetComponent<Rigidbody2D>();
                if (body == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, target.name + " has no Rigidbody2D.", new { dimension });
                }

                return CucliCommandResult.Ok(new
                {
                    target = target.name,
                    instanceID = target.GetInstanceID(),
                    dimension,
                    mass = body.mass,
                    linearDamping = body.drag,
                    angularDamping = body.angularDrag,
                    position = CucliDescribe.Value(body.position, null, 0),
                    rotation = body.rotation,
                    velocity = CucliDescribe.Value(body.velocity, null, 0),
                    angularVelocity = body.angularVelocity,
                    collisionDetectionMode = body.collisionDetectionMode.ToString(),
                    constraints = body.constraints.ToString(),
                    gravityScale = body.gravityScale,
                    bodyType = body.bodyType.ToString(),
                    simulated = body.simulated,
                    isAwake = body.IsAwake(),
                    centerOfMass = CucliDescribe.Value(body.centerOfMass, null, 0)
                });
            }

            Rigidbody rigidbody3D = target.GetComponent<Rigidbody>();
            if (rigidbody3D == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, target.name + " has no Rigidbody.", new { dimension });
            }

            return CucliCommandResult.Ok(new
            {
                target = target.name,
                instanceID = target.GetInstanceID(),
                dimension,
                mass = rigidbody3D.mass,
                linearDamping = LinearDamping(rigidbody3D),
                angularDamping = AngularDamping(rigidbody3D),
                position = CucliDescribe.Value(rigidbody3D.position, null, 0),
                rotation = CucliDescribe.Value(rigidbody3D.rotation, null, 0),
                velocity = CucliDescribe.Value(rigidbody3D.velocity, null, 0),
                angularVelocity = CucliDescribe.Value(rigidbody3D.angularVelocity, null, 0),
                collisionDetectionMode = rigidbody3D.collisionDetectionMode.ToString(),
                constraints = rigidbody3D.constraints.ToString(),
                useGravity = rigidbody3D.useGravity,
                isKinematic = rigidbody3D.isKinematic,
                interpolation = rigidbody3D.interpolation.ToString(),
                maxAngularVelocity = rigidbody3D.maxAngularVelocity,
                isSleeping = rigidbody3D.IsSleeping(),
                centerOfMass = CucliDescribe.Value(rigidbody3D.centerOfMass, null, 0)
            });
        }

        private static float LinearDamping(Rigidbody body)
        {
#if UNITY_6000_0_OR_NEWER
            return body.linearDamping;
#else
            return body.drag;
#endif
        }

        private static float AngularDamping(Rigidbody body)
        {
#if UNITY_6000_0_OR_NEWER
            return body.angularDamping;
#else
            return body.angularDrag;
#endif
        }

        private static readonly HashSet<string> Properties3D = new HashSet<string>(StringComparer.Ordinal)
        {
            "mass", "drag", "lineardamping", "angulardrag", "angulardamping", "usegravity", "iskinematic",
            "interpolation", "collisiondetectionmode", "constraints"
        };

        private static readonly HashSet<string> Properties2D = new HashSet<string>(StringComparer.Ordinal)
        {
            "mass", "gravityscale", "drag", "lineardamping", "angulardrag", "angulardamping", "bodytype",
            "simulated", "collisiondetectionmode", "constraints"
        };

        internal static CucliCommandResult ConfigureRigidbody(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (error != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string dimension = PhysicsCommand.ResolveTargetDimension(context.Input, target, out error);
            if (dimension == null)
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            JObject properties = context.Input.GetObject("properties");
            if (properties == null || properties.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "properties",
                    "Pass an object of whitelisted keys, e.g. --properties '{\"mass\":2}'.");
            }

            object body = dimension == PhysicsCommand.Dimension2D
                ? (object)target.GetComponent<Rigidbody2D>()
                : target.GetComponent<Rigidbody>();
            if (body == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    target.name + " has no " + (dimension == PhysicsCommand.Dimension2D ? "Rigidbody2D" : "Rigidbody") + ".",
                    new { dimension });
            }

            var component = body as Component;
            Undo.RecordObject(component, "cucli physics configure-rigidbody");

            HashSet<string> whitelist = dimension == PhysicsCommand.Dimension2D ? Properties2D : Properties3D;
            var unknown = new JArray();
            var changed = new JArray();

            foreach (JProperty property in properties.Properties())
            {
                string key = property.Name.ToLowerInvariant().Replace("_", string.Empty);
                if (!whitelist.Contains(key))
                {
                    unknown.Add(property.Name);
                    continue;
                }

                string failure;
                if (!ApplyBodyProperty(dimension, body, key, property.Value, out failure))
                {
                    return CucliCommandResult.Invalid(property.Name, failure);
                }

                changed.Add(key);
            }

            if (unknown.Count > 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "Unknown " + dimension + " rigidbody propert(ies): "
                    + string.Join(", ", unknown.Select(item => item.Value<string>()))
                    + ". Known: " + string.Join(", ", whitelist.OrderBy(item => item, StringComparer.Ordinal)) + ".",
                    new { unknown, allowed = whitelist.OrderBy(item => item, StringComparer.Ordinal).ToArray() });
            }

            EditorUtility.SetDirty(component);
            return CucliCommandResult.Ok(new { target = target.name, dimension, changed });
        }

        private static bool ApplyBodyProperty(string dimension, object body, string key, JToken value, out string error)
        {
            error = null;
            try
            {
                if (dimension == PhysicsCommand.Dimension2D)
                {
                    var rigidbody2D = (Rigidbody2D)body;
                    switch (key)
                    {
                        case "mass": rigidbody2D.mass = value.Value<float>(); return true;
                        case "gravityscale": rigidbody2D.gravityScale = value.Value<float>(); return true;
#if UNITY_6000_0_OR_NEWER
                        case "drag":
                        case "lineardamping": rigidbody2D.linearDamping = value.Value<float>(); return true;
                        case "angulardrag":
                        case "angulardamping": rigidbody2D.angularDamping = value.Value<float>(); return true;
#else
                        case "drag":
                        case "lineardamping": rigidbody2D.drag = value.Value<float>(); return true;
                        case "angulardrag":
                        case "angulardamping": rigidbody2D.angularDrag = value.Value<float>(); return true;
#endif
                        case "bodytype":
                            rigidbody2D.bodyType = (RigidbodyType2D)Enum.Parse(typeof(RigidbodyType2D), Text(value), true);
                            return true;
                        case "simulated": rigidbody2D.simulated = value.Value<bool>(); return true;
                        case "collisiondetectionmode":
                            rigidbody2D.collisionDetectionMode = (CollisionDetectionMode2D)Enum.Parse(
                                typeof(CollisionDetectionMode2D), Text(value), true);
                            return true;
                        case "constraints":
                            rigidbody2D.constraints = (RigidbodyConstraints2D)ParseFlags(typeof(RigidbodyConstraints2D), value);
                            return true;
                    }
                }
                else
                {
                    var rigidbody3D = (Rigidbody)body;
                    switch (key)
                    {
                        case "mass": rigidbody3D.mass = value.Value<float>(); return true;
                        case "usegravity": rigidbody3D.useGravity = value.Value<bool>(); return true;
                        case "iskinematic": rigidbody3D.isKinematic = value.Value<bool>(); return true;
#if UNITY_6000_0_OR_NEWER
                        case "drag":
                        case "lineardamping": rigidbody3D.linearDamping = value.Value<float>(); return true;
                        case "angulardrag":
                        case "angulardamping": rigidbody3D.angularDamping = value.Value<float>(); return true;
#else
                        case "drag":
                        case "lineardamping": rigidbody3D.drag = value.Value<float>(); return true;
                        case "angulardrag":
                        case "angulardamping": rigidbody3D.angularDrag = value.Value<float>(); return true;
#endif
                        case "interpolation":
                            rigidbody3D.interpolation = (RigidbodyInterpolation)Enum.Parse(
                                typeof(RigidbodyInterpolation), Text(value), true);
                            return true;
                        case "collisiondetectionmode":
                            rigidbody3D.collisionDetectionMode = (CollisionDetectionMode)Enum.Parse(
                                typeof(CollisionDetectionMode), Text(value), true);
                            return true;
                        case "constraints":
                            rigidbody3D.constraints = (RigidbodyConstraints)ParseFlags(typeof(RigidbodyConstraints), value);
                            return true;
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return false;
            }

            error = "not a settable property.";
            return false;
        }

        private static string Text(JToken value)
        {
            return (value.Type == JTokenType.String ? value.Value<string>() : value.ToString()).Trim();
        }

        /// <summary>
        /// Accepts an integer bitmask, or a comma/pipe separated list of enum names, or a combination
        /// such as "FreezeRotationX, FreezePositionY".
        /// </summary>
        private static object ParseFlags(Type enumType, JToken value)
        {
            if (value.Type == JTokenType.Integer) return Enum.ToObject(enumType, value.Value<long>());

            string text = Text(value);
            long numeric;
            if (long.TryParse(text, out numeric)) return Enum.ToObject(enumType, numeric);

            object result = Enum.ToObject(enumType, 0L);
            foreach (string part in text.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                result = Enum.ToObject(
                    enumType,
                    Convert.ToInt64(result) | Convert.ToInt64(Enum.Parse(enumType, part.Trim(), true)));
            }

            return result;
        }

        // ------------------------------------------------------------------ force

        internal static CucliCommandResult ApplyForce(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (error != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string dimension = PhysicsCommand.ResolveTargetDimension(context.Input, target, out error);
            if (dimension == null)
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            string forceType = (context.Input.GetString("forceType") ?? "normal").Trim().ToLowerInvariant();
            if (forceType != "normal" && forceType != "explosion")
            {
                return CucliCommandResult.Invalid("forceType", "must be 'normal' or 'explosion' (got '" + forceType + "').");
            }

            if (forceType == "explosion" && dimension == PhysicsCommand.Dimension2D)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter, "forceType=explosion is only supported for 3D physics.");
            }

            if (dimension == PhysicsCommand.Dimension2D)
            {
                return ApplyForce2D(context, target, forceType);
            }

            return ApplyForce3D(context, target, forceType);
        }

        private static CucliCommandResult ApplyForce2D(CucliCommandContext context, GameObject target, string forceType)
        {
            Rigidbody2D body = target.GetComponent<Rigidbody2D>();
            if (body == null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, target.name + " has no Rigidbody2D.");
            }

            if (body.bodyType == RigidbodyType2D.Kinematic)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    target.name + " is a kinematic Rigidbody2D; forces have no effect on it.");
            }

            ForceMode2D mode = (ForceMode2D)Enum.Parse(typeof(ForceMode2D), context.Input.GetString("forceMode") ?? "Force", true);
            Undo.RecordObject(body, "cucli physics apply-force");

            JToken forceToken = PhysicsSupport.Token(context.Input, "force");
            JToken torqueToken = PhysicsSupport.Token(context.Input, "torque");
            JToken positionToken = PhysicsSupport.Token(context.Input, "position");

            bool applied = false;
            Vector2 force = Vector2.zero;
            if (forceToken != null && forceToken.Type != JTokenType.Null)
            {
                if (!PhysicsCommand.ReadVector2(forceToken, out force, out string forceError))
                {
                    return CucliCommandResult.Invalid("force", forceError);
                }

                if (positionToken != null && positionToken.Type != JTokenType.Null)
                {
                    Vector2 at;
                    if (!PhysicsCommand.ReadVector2(positionToken, out at, out string positionError))
                    {
                        return CucliCommandResult.Invalid("position", positionError);
                    }

                    body.AddForceAtPosition(force, at, mode);
                }
                else
                {
                    body.AddForce(force, mode);
                }

                applied = true;
            }

            float torque = 0f;
            if (torqueToken != null && torqueToken.Type != JTokenType.Null)
            {
                // 2D torque is a single scalar around Z; an array here is a caller mistake worth naming.
                if (torqueToken.Type == JTokenType.Array)
                {
                    return CucliCommandResult.Invalid("torque", "2D torque must be a single number (rotation around Z).");
                }

                torque = torqueToken.Value<float>();
                body.AddTorque(torque, mode);
                applied = true;
            }

            if (!applied)
            {
                return CucliCommandResult.Missing("force", "Provide 'force' and/or 'torque'.");
            }

            return CucliCommandResult.Ok(new
            {
                target = target.name,
                dimension = PhysicsCommand.Dimension2D,
                force_type = "normal",
                force_mode = mode.ToString(),
                force = applied && forceToken != null ? CucliDescribe.Value(force, null, 0) : null,
                torque = torqueToken != null ? (object)torque : null
            });
        }

        private static CucliCommandResult ApplyForce3D(CucliCommandContext context, GameObject target, string forceType)
        {
            Rigidbody body = target.GetComponent<Rigidbody>();
            if (body == null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, target.name + " has no Rigidbody.");
            }

            if (body.isKinematic)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound,
                    target.name + " is a kinematic Rigidbody; forces have no effect on it.");
            }

            ForceMode mode = (ForceMode)Enum.Parse(typeof(ForceMode), context.Input.GetString("forceMode") ?? "Force", true);
            Undo.RecordObject(body, "cucli physics apply-force");

            if (forceType == "explosion")
            {
                float explosionForce = context.Input.GetFloat("explosionForce", 0f);
                JToken positionToken = PhysicsSupport.Token(context.Input, "explosionPosition");
                float radius = context.Input.GetFloat("explosionRadius", 0f);

                if (explosionForce == 0f)
                {
                    return CucliCommandResult.Missing("explosionForce");
                }

                Vector3 center;
                if (positionToken == null || !PhysicsCommand.ReadVector3(positionToken, out center, out string positionError))
                {
                    return CucliCommandResult.Missing(
                        "explosionPosition",
                        positionError == null ? null : "Expected [x,y,z]: " + positionError);
                }

                if (radius <= 0f)
                {
                    return CucliCommandResult.Missing("explosionRadius");
                }

                float upwards = context.Input.GetFloat("upwardsModifier", 0f);
                body.AddExplosionForce(explosionForce, center, radius, upwards, mode);

                return CucliCommandResult.Ok(new
                {
                    target = target.name,
                    dimension = PhysicsCommand.Dimension3D,
                    force_type = "explosion",
                    force_mode = mode.ToString(),
                    explosion_position = CucliDescribe.Value(center, null, 0),
                    explosion_force = explosionForce,
                    explosion_radius = radius,
                    upwards_modifier = upwards
                });
            }

            JToken forcePatch = PhysicsSupport.Token(context.Input, "force");
            JToken torquePatch = PhysicsSupport.Token(context.Input, "torque");
            JToken atToken = PhysicsSupport.Token(context.Input, "position");

            bool applied = false;
            Vector3 force = Vector3.zero;
            if (forcePatch != null && forcePatch.Type != JTokenType.Null)
            {
                if (!PhysicsCommand.ReadVector3(forcePatch, out force, out string forceError))
                {
                    return CucliCommandResult.Invalid("force", forceError);
                }

                if (atToken != null && atToken.Type != JTokenType.Null)
                {
                    Vector3 at;
                    if (!PhysicsCommand.ReadVector3(atToken, out at, out string positionError))
                    {
                        return CucliCommandResult.Invalid("position", positionError);
                    }

                    body.AddForceAtPosition(force, at, mode);
                }
                else
                {
                    body.AddForce(force, mode);
                }

                applied = true;
            }

            Vector3 torque = Vector3.zero;
            if (torquePatch != null && torquePatch.Type != JTokenType.Null)
            {
                if (!PhysicsCommand.ReadVector3(torquePatch, out torque, out string torqueError))
                {
                    return CucliCommandResult.Invalid("torque", torqueError);
                }

                body.AddTorque(torque, mode);
                applied = true;
            }

            if (!applied)
            {
                return CucliCommandResult.Missing("force", "Provide 'force' and/or 'torque'.");
            }

            return CucliCommandResult.Ok(new
            {
                target = target.name,
                dimension = PhysicsCommand.Dimension3D,
                force_type = "normal",
                force_mode = mode.ToString(),
                force = forcePatch != null ? CucliDescribe.Value(force, null, 0) : null,
                torque = torquePatch != null ? CucliDescribe.Value(torque, null, 0) : null
            });
        }
    }

    /// <summary>Physics material actions for the <c>physics</c> group.</summary>
    internal static class PhysicsMaterials
    {
        private const string DefaultFolder = "Assets/Physics Materials";

        private static readonly HashSet<string> Properties3D = new HashSet<string>(StringComparer.Ordinal)
        {
            "dynamicfriction", "staticfriction", "bounciness", "frictioncombine", "bouncecombine"
        };

        private static readonly HashSet<string> Properties2D = new HashSet<string>(StringComparer.Ordinal)
        {
            "friction", "bounciness"
        };

        internal static CucliCommandResult Create(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!PhysicsCommand.ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            string name = context.Input.GetString("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return CucliCommandResult.Missing("name", "The asset name, without extension.");
            }

            string folder = context.Input.GetString("path");
            if (string.IsNullOrWhiteSpace(folder)) folder = DefaultFolder;
            folder = CucliAssets.NormalizeAssetPath(folder);
            if (folder == null)
            {
                return CucliCommandResult.Invalid("path", "must resolve inside the project.");
            }

            string extension = dimension == PhysicsCommand.Dimension2D ? ".physicsMaterial2D" : ".physicMaterial";
            string assetPath = folder + "/" + name.Trim() + extension;

            if (CucliAssets.LoadMain(assetPath) != null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetExists,
                    "A physics material already exists at " + assetPath + "; use configure-physics-material instead.",
                    new { path = assetPath });
            }

            if (!CucliAssets.EnsureFolder(folder))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "Could not create the folder " + folder + ".");
            }

            if (dimension == PhysicsCommand.Dimension2D)
            {
                var material2D = ScriptableObject.CreateInstance<PhysicsMaterial2D>();
                material2D.friction = context.Input.GetFloat("friction", 0.4f);
                material2D.bounciness = context.Input.GetFloat("bounciness", 0f);
                AssetDatabase.CreateAsset(material2D, assetPath);
                AssetDatabase.SaveAssets();

                return CucliCommandResult.Ok(new
                {
                    path = assetPath,
                    dimension,
                    friction = material2D.friction,
                    bounciness = material2D.bounciness
                }, "已创建 2D 物理材质。");
            }

            var material = ScriptableObject.CreateInstance<PhysicsMaterial3D>();
            material.dynamicFriction = context.Input.GetFloat("dynamicFriction", 0.6f);
            material.staticFriction = context.Input.GetFloat("staticFriction", 0.6f);
            material.bounciness = context.Input.GetFloat("bounciness", 0f);

            // Combine modes are parsed leniently on purpose: Unity silently keeps the previous value
            // for an unrecognised name, and the doc contract says the same, so matching it avoids
            // turning a typo into a hard failure while still reporting it as a warning.
            string frictionCombine = context.Input.GetString("frictionCombine");
            if (!string.IsNullOrWhiteSpace(frictionCombine))
            {
                PhysicsMaterialCombine parsed;
                if (Enum.TryParse(frictionCombine.Trim(), true, out parsed)) material.frictionCombine = parsed;
                else context.Warn("frictionCombine '" + frictionCombine + "' was not recognised and was ignored.");
            }

            string bounceCombine = context.Input.GetString("bounceCombine");
            if (!string.IsNullOrWhiteSpace(bounceCombine))
            {
                PhysicsMaterialCombine parsed;
                if (Enum.TryParse(bounceCombine.Trim(), true, out parsed)) material.bounceCombine = parsed;
                else context.Warn("bounceCombine '" + bounceCombine + "' was not recognised and was ignored.");
            }

            AssetDatabase.CreateAsset(material, assetPath);
            AssetDatabase.SaveAssets();
            context.AddArtifact(assetPath);

            return CucliCommandResult.Ok(new
            {
                path = assetPath,
                dimension,
                dynamicFriction = material.dynamicFriction,
                staticFriction = material.staticFriction,
                bounciness = material.bounciness,
                frictionCombine = material.frictionCombine.ToString(),
                bounceCombine = material.bounceCombine.ToString()
            }, "已创建 3D 物理材质。");
        }

        internal static CucliCommandResult Configure(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!PhysicsCommand.ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            string path = context.Input.GetString("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                return CucliCommandResult.Missing("path", "The physics material asset path.");
            }

            JObject properties = context.Input.GetObject("properties");
            if (properties == null || properties.Count == 0)
            {
                return CucliCommandResult.Missing("properties");
            }

            string normalized = CucliAssets.NormalizeAssetPath(path);
            HashSet<string> whitelist = dimension == PhysicsCommand.Dimension2D ? Properties2D : Properties3D;
            var changed = new JArray();
            var unknown = new JArray();

            if (dimension == PhysicsCommand.Dimension2D)
            {
                var material2D = CucliAssets.Load<PhysicsMaterial2D>(normalized);
                if (material2D == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound, "No PhysicsMaterial2D at " + normalized + ".");
                }

                Undo.RecordObject(material2D, "cucli physics configure-material");
                foreach (JProperty property in properties.Properties())
                {
                    string key = property.Name.ToLowerInvariant().Replace("_", string.Empty);
                    if (!whitelist.Contains(key))
                    {
                        unknown.Add(property.Name);
                        continue;
                    }

                    if (key == "friction") material2D.friction = property.Value.Value<float>();
                    else if (key == "bounciness") material2D.bounciness = property.Value.Value<float>();
                    changed.Add(key);
                }

                EditorUtility.SetDirty(material2D);
                AssetDatabase.SaveAssets();
            }
            else
            {
                PhysicsMaterial3D material = CucliAssets.Load<PhysicsMaterial3D>(normalized);
                if (material == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.AssetNotFound, "No 3D physics material at " + normalized + ".");
                }

                Undo.RecordObject(material, "cucli physics configure-material");
                foreach (JProperty property in properties.Properties())
                {
                    string key = property.Name.ToLowerInvariant().Replace("_", string.Empty);
                    if (!whitelist.Contains(key))
                    {
                        unknown.Add(property.Name);
                        continue;
                    }

                    switch (key)
                    {
                        case "dynamicfriction": material.dynamicFriction = property.Value.Value<float>(); break;
                        case "staticfriction": material.staticFriction = property.Value.Value<float>(); break;
                        case "bounciness": material.bounciness = property.Value.Value<float>(); break;
                        case "frictioncombine":
                            material.frictionCombine = (PhysicsMaterialCombine)Enum.Parse(
                                typeof(PhysicsMaterialCombine), property.Value.ToString(), true);
                            break;
                        case "bouncecombine":
                            material.bounceCombine = (PhysicsMaterialCombine)Enum.Parse(
                                typeof(PhysicsMaterialCombine), property.Value.ToString(), true);
                            break;
                    }

                    changed.Add(key);
                }

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
            }

            if (unknown.Count > 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.InvalidParameter,
                    "Unknown " + dimension + " material propert(ies): "
                    + string.Join(", ", unknown.Select(item => item.Value<string>()))
                    + ". Known: " + string.Join(", ", whitelist.OrderBy(item => item, StringComparer.Ordinal)) + ".",
                    new { unknown });
            }

            return CucliCommandResult.Ok(new { path = normalized, changed });
        }

        internal static CucliCommandResult Assign(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (error != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string materialPath = context.Input.GetString("materialPath")
                ?? context.Input.GetString("material_path");
            if (string.IsNullOrWhiteSpace(materialPath))
            {
                return CucliCommandResult.Missing("materialPath");
            }

            string normalized = CucliAssets.NormalizeAssetPath(materialPath);
            var material3D = CucliAssets.Load<PhysicsMaterial3D>(normalized);
            var material2D = CucliAssets.Load<PhysicsMaterial2D>(normalized);
            if (material3D == null && material2D == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "No physics material at " + normalized + ".");
            }

            string colliderType = context.Input.GetString("colliderType");
            int componentIndex = context.Input.GetInt("componentIndex", context.Input.GetInt("component_index", 0));

            Component collider = null;
            if (!string.IsNullOrWhiteSpace(colliderType))
            {
                Type type = CucliReflection.FindType(colliderType, out error);
                if (error != null) return CucliCommandResult.Invalid("colliderType", error);
                if (!typeof(Collider).IsAssignableFrom(type) && !typeof(Collider2D).IsAssignableFrom(type))
                {
                    return CucliCommandResult.Invalid(
                        "colliderType", "'" + colliderType + "' is not a Collider or Collider2D type.");
                }

                Component[] all = target.GetComponents(type);
                if (all.Length == 0)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, target.name + " has no " + type.Name + ".");
                }

                if (componentIndex < 0 || componentIndex >= all.Length)
                {
                    return CucliCommandResult.Invalid(
                        "componentIndex", "out of range (0-" + (all.Length - 1) + ").");
                }

                collider = all[componentIndex];
            }

            if (collider == null)
            {
                Collider first3D = target.GetComponent<Collider>();
                Collider2D first2D = target.GetComponent<Collider2D>();
                collider = first3D != null ? (Component)first3D : first2D;
            }

            if (collider == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, target.name + " has no Collider or Collider2D.");
            }

            if (material3D != null)
            {
                var target3D = collider as Collider;
                if (target3D == null)
                {
                    return CucliCommandResult.Invalid(
                        "materialPath", "a 3D physics material cannot be assigned to a Collider2D.");
                }

                Undo.RecordObject(target3D, "cucli physics assign-material");
                target3D.sharedMaterial = material3D;
                EditorUtility.SetDirty(target3D);
            }
            else
            {
                var target2D = collider as Collider2D;
                if (target2D == null)
                {
                    return CucliCommandResult.Invalid(
                        "materialPath", "a 2D physics material cannot be assigned to a Collider.");
                }

                Undo.RecordObject(target2D, "cucli physics assign-material");
                target2D.sharedMaterial = material2D;
                EditorUtility.SetDirty(target2D);
            }

            return CucliCommandResult.Ok(new
            {
                gameObject = target.name,
                collider = collider.GetType().Name,
                materialPath = normalized
            }, "已指派物理材质。");
        }
    }
}
