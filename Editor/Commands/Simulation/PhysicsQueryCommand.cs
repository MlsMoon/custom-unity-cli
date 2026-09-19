using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>Cast and overlap actions for the <c>physics</c> group.</summary>
    internal static class PhysicsQueries
    {
        internal static CucliCommandResult Raycast(CucliCommandContext context, bool all)
        {
            string dimension;
            string error;
            if (!PhysicsCommand.ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            bool is2D = dimension == PhysicsCommand.Dimension2D;
            Vector2 origin2D = Vector2.zero;
            Vector2 direction2D = Vector2.right;
            Vector3 origin3D = Vector3.zero;
            Vector3 direction3D = Vector3.forward;

            JToken originToken = PhysicsSupport.Token(context.Input, "origin");
            JToken directionToken = PhysicsSupport.Token(context.Input, "direction");

            if (is2D)
            {
                if (!PhysicsCommand.ReadVector2(originToken, out origin2D, out error))
                {
                    return CucliCommandResult.Invalid("origin", error);
                }

                if (!PhysicsCommand.ReadVector2(directionToken, out direction2D, out error))
                {
                    return CucliCommandResult.Invalid("direction", error);
                }
            }
            else
            {
                if (!PhysicsCommand.ReadVector3(originToken, out origin3D, out error))
                {
                    return CucliCommandResult.Invalid("origin", error);
                }

                if (!PhysicsCommand.ReadVector3(directionToken, out direction3D, out error))
                {
                    return CucliCommandResult.Invalid("direction", error);
                }
            }

            int mask;
            if (!PhysicsSupport.ResolveLayerMask(context.Input, out mask, out error))
            {
                return CucliCommandResult.Invalid("layerMask", error);
            }

            float maxDistance = context.Input.GetFloat("maxDistance", float.PositiveInfinity);
            if (float.IsNaN(maxDistance) || maxDistance <= 0f) maxDistance = float.PositiveInfinity;

            if (all)
            {
                var hits = new JArray();
                if (is2D)
                {
                    foreach (RaycastHit2D hit in Physics2D.RaycastAll(origin2D, direction2D, maxDistance, mask))
                    {
                        hits.Add(Describe2D(hit));
                    }

                    return CucliCommandResult.Ok(new { hit_count = hits.Count, hits });
                }

                RaycastHit[] found = Physics.RaycastAll(origin3D, direction3D, maxDistance, mask);
                Array.Sort(found, (left, right) => left.distance.CompareTo(right.distance));
                foreach (RaycastHit hit in found) hits.Add(Describe3D(hit));
                return CucliCommandResult.Ok(new { hit_count = hits.Count, hits });
            }

            if (is2D)
            {
                RaycastHit2D hit2D = Physics2D.Raycast(origin2D, direction2D, maxDistance, mask);
                return CucliCommandResult.Ok(hit2D.collider == null ? (object)new { hit = false } : Describe2D(hit2D));
            }

            QueryTriggerInteraction interaction;
            if (!PhysicsSupport.ResolveTriggerInteraction(context.Input, out interaction, out error))
            {
                return CucliCommandResult.Invalid("queryTriggerInteraction", error);
            }

            RaycastHit hit3D;
            return Physics.Raycast(origin3D, direction3D, out hit3D, maxDistance, mask, interaction)
                ? CucliCommandResult.Ok(Describe3D(hit3D))
                : CucliCommandResult.Ok(new { hit = false });
        }

        internal static CucliCommandResult Linecast(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!PhysicsCommand.ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            int mask;
            if (!PhysicsSupport.ResolveLayerMask(context.Input, out mask, out error))
            {
                return CucliCommandResult.Invalid("layerMask", error);
            }

            if (dimension == PhysicsCommand.Dimension2D)
            {
                Vector2 start;
                Vector2 end;
                if (!PhysicsCommand.ReadVector2(PhysicsSupport.Token(context.Input, "start"), out start, out error))
                {
                    return CucliCommandResult.Invalid("start", error);
                }

                if (!PhysicsCommand.ReadVector2(PhysicsSupport.Token(context.Input, "end"), out end, out error))
                {
                    return CucliCommandResult.Invalid("end", error);
                }

                RaycastHit2D hit = Physics2D.Linecast(start, end, mask);
                return CucliCommandResult.Ok(hit.collider == null ? (object)new { hit = false } : Describe2D(hit));
            }

            Vector3 start3D;
            Vector3 end3D;
            if (!PhysicsCommand.ReadVector3(PhysicsSupport.Token(context.Input, "start"), out start3D, out error))
            {
                return CucliCommandResult.Invalid("start", error);
            }

            if (!PhysicsCommand.ReadVector3(PhysicsSupport.Token(context.Input, "end"), out end3D, out error))
            {
                return CucliCommandResult.Invalid("end", error);
            }

            QueryTriggerInteraction interaction;
            if (!PhysicsSupport.ResolveTriggerInteraction(context.Input, out interaction, out error))
            {
                return CucliCommandResult.Invalid("queryTriggerInteraction", error);
            }

            RaycastHit hit3D;
            return Physics.Linecast(start3D, end3D, out hit3D, mask, interaction)
                ? CucliCommandResult.Ok(Describe3D(hit3D))
                : CucliCommandResult.Ok(new { hit = false });
        }

        internal static CucliCommandResult ShapeCast(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!PhysicsCommand.ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            string shape = (context.Input.GetString("shape") ?? string.Empty).Trim().ToLowerInvariant();
            int mask;
            if (!PhysicsSupport.ResolveLayerMask(context.Input, out mask, out error))
            {
                return CucliCommandResult.Invalid("layerMask", error);
            }

            float maxDistance = context.Input.GetFloat("maxDistance", float.PositiveInfinity);
            if (float.IsNaN(maxDistance) || maxDistance <= 0f) maxDistance = float.PositiveInfinity;

            if (dimension == PhysicsCommand.Dimension2D)
            {
                Vector2 origin;
                Vector2 direction;
                if (!PhysicsCommand.ReadVector2(PhysicsSupport.Token(context.Input, "origin"), out origin, out error))
                {
                    return CucliCommandResult.Invalid("origin", error);
                }

                if (!PhysicsCommand.ReadVector2(PhysicsSupport.Token(context.Input, "direction"), out direction, out error))
                {
                    return CucliCommandResult.Invalid("direction", error);
                }

                float angle = context.Input.GetFloat("angle", 0f);
                JToken size = PhysicsSupport.Token(context.Input, "size");
                RaycastHit2D hit;

                switch (shape)
                {
                    case "circle":
                    {
                        float radius;
                        if (!ReadScalar(size, out radius, out error))
                        {
                            return CucliCommandResult.Invalid("size", error);
                        }

                        hit = Physics2D.CircleCast(origin, radius, direction, maxDistance, mask);
                        break;
                    }
                    case "box":
                    {
                        Vector2 half;
                        if (!ReadSize2(size, out half, out error))
                        {
                            return CucliCommandResult.Invalid("size", error);
                        }

                        hit = Physics2D.BoxCast(origin, half * 2f, angle, direction, maxDistance, mask);
                        break;
                    }
                    case "capsule":
                    {
                        Vector2 capsuleSize;
                        bool horizontal;
                        if (!ReadCapsule2(size, out capsuleSize, out horizontal, out error))
                        {
                            return CucliCommandResult.Invalid("size", error);
                        }

                        CapsuleDirection2D capsuleDirection = horizontal
                            ? CapsuleDirection2D.Horizontal
                            : CapsuleDirection2D.Vertical;
                        hit = Physics2D.CapsuleCast(
                            origin, capsuleSize, capsuleDirection, angle, direction, maxDistance, mask);
                        break;
                    }
                    default:
                        return CucliCommandResult.Invalid(
                            "shape", "2D shapecast supports circle, box or capsule (got '" + shape + "').");
                }

                return CucliCommandResult.Ok(hit.collider == null ? (object)new { hit = false } : Describe2D(hit));
            }

            Vector3 origin3D;
            Vector3 direction3D;
            if (!PhysicsCommand.ReadVector3(PhysicsSupport.Token(context.Input, "origin"), out origin3D, out error))
            {
                return CucliCommandResult.Invalid("origin", error);
            }

            if (!PhysicsCommand.ReadVector3(PhysicsSupport.Token(context.Input, "direction"), out direction3D, out error))
            {
                return CucliCommandResult.Invalid("direction", error);
            }

            QueryTriggerInteraction interaction;
            if (!PhysicsSupport.ResolveTriggerInteraction(context.Input, out interaction, out error))
            {
                return CucliCommandResult.Invalid("queryTriggerInteraction", error);
            }

            JToken sizeToken = PhysicsSupport.Token(context.Input, "size");
            RaycastHit hit3D;
            switch (shape)
            {
                case "sphere":
                {
                    float radius;
                    if (!ReadScalar(sizeToken, out radius, out error))
                    {
                        return CucliCommandResult.Invalid("size", error);
                    }

                    if (!Physics.SphereCast(origin3D, radius, direction3D, out hit3D, maxDistance, mask, interaction))
                    {
                        return CucliCommandResult.Ok(new { hit = false });
                    }

                    break;
                }
                case "box":
                {
                    Vector3 half;
                    if (!ReadVector3Token(sizeToken, out half, out error))
                    {
                        return CucliCommandResult.Invalid("size", error);
                    }

                    if (!Physics.BoxCast(origin3D, half, direction3D, out hit3D, Quaternion.identity, maxDistance, mask, interaction))
                    {
                        return CucliCommandResult.Ok(new { hit = false });
                    }

                    break;
                }
                case "capsule":
                {
                    float radius;
                    if (!ReadScalar(sizeToken, out radius, out error))
                    {
                        return CucliCommandResult.Invalid("size", error);
                    }

                    Vector3 point1;
                    Vector3 point2;
                    if (!ReadCapsuleEndpoints(context, origin3D, radius, out point1, out point2, out error))
                    {
                        return CucliCommandResult.Invalid("size", error);
                    }

                    if (!Physics.CapsuleCast(point1, point2, radius, direction3D, out hit3D, maxDistance, mask, interaction))
                    {
                        return CucliCommandResult.Ok(new { hit = false });
                    }

                    break;
                }
                default:
                    return CucliCommandResult.Invalid(
                        "shape", "3D shapecast supports sphere, box or capsule (got '" + shape + "').");
            }

            return CucliCommandResult.Ok(Describe3D(hit3D));
        }

        internal static CucliCommandResult Overlap(CucliCommandContext context)
        {
            string dimension;
            string error;
            if (!PhysicsCommand.ResolveDimension(context.Input, false, out dimension, out error))
            {
                return CucliCommandResult.Invalid("dimension", error);
            }

            string shape = (context.Input.GetString("shape") ?? string.Empty).Trim().ToLowerInvariant();
            int mask;
            if (!PhysicsSupport.ResolveLayerMask(context.Input, out mask, out error))
            {
                return CucliCommandResult.Invalid("layerMask", error);
            }

            var colliders = new JArray();

            if (dimension == PhysicsCommand.Dimension2D)
            {
                Vector2 position;
                if (!PhysicsCommand.ReadVector2(PhysicsSupport.Token(context.Input, "position"), out position, out error))
                {
                    return CucliCommandResult.Invalid("position", error);
                }

                JToken size = PhysicsSupport.Token(context.Input, "size");
                Collider2D[] found;
                switch (shape)
                {
                    case "circle":
                    {
                        float radius;
                        if (!ReadScalar(size, out radius, out error))
                        {
                            return CucliCommandResult.Invalid("size", error);
                        }

                        found = Physics2D.OverlapCircleAll(position, radius, mask);
                        break;
                    }
                    case "box":
                    {
                        Vector2 half;
                        if (!ReadSize2(size, out half, out error))
                        {
                            return CucliCommandResult.Invalid("size", error);
                        }

                        found = Physics2D.OverlapBoxAll(position, half * 2f, 0f, mask);
                        break;
                    }
                    case "capsule":
                    {
                        Vector2 capsuleSize;
                        bool horizontal;
                        if (!ReadCapsule2(size, out capsuleSize, out horizontal, out error))
                        {
                            return CucliCommandResult.Invalid("size", error);
                        }

                        found = Physics2D.OverlapCapsuleAll(
                            position, capsuleSize,
                            horizontal ? CapsuleDirection2D.Horizontal : CapsuleDirection2D.Vertical,
                            0f, mask);
                        break;
                    }
                    default:
                        return CucliCommandResult.Invalid(
                            "shape", "2D overlap supports circle, box or capsule (got '" + shape + "').");
                }

                foreach (Collider2D collider in found.Where(item => item != null))
                {
                    colliders.Add(new JObject
                    {
                        ["gameObject"] = collider.gameObject.name,
                        ["instanceID"] = collider.gameObject.GetInstanceID(),
                        ["collider_type"] = collider.GetType().Name
                    });
                }

                return CucliCommandResult.Ok(new { colliders });
            }

            Vector3 position3D;
            if (!PhysicsCommand.ReadVector3(PhysicsSupport.Token(context.Input, "position"), out position3D, out error))
            {
                return CucliCommandResult.Invalid("position", error);
            }

            JToken sizeToken = PhysicsSupport.Token(context.Input, "size");
            Collider[] hits;
            switch (shape)
            {
                case "sphere":
                {
                    float radius;
                    if (!ReadScalar(sizeToken, out radius, out error))
                    {
                        return CucliCommandResult.Invalid("size", error);
                    }

                    hits = Physics.OverlapSphere(position3D, radius, mask);
                    break;
                }
                case "box":
                {
                    Vector3 half;
                    if (!ReadVector3Token(sizeToken, out half, out error))
                    {
                        return CucliCommandResult.Invalid("size", error);
                    }

                    hits = Physics.OverlapBox(position3D, half, Quaternion.identity, mask);
                    break;
                }
                case "capsule":
                {
                    CapsuleShape capsule;
                    if (!ReadCapsuleShape(sizeToken, out capsule, out error))
                    {
                        return CucliCommandResult.Invalid("size", error);
                    }

                    Vector3 point1 = position3D + capsule.Axis * (capsule.Height * 0.5f - capsule.Radius);
                    Vector3 point2 = position3D - capsule.Axis * (capsule.Height * 0.5f - capsule.Radius);
                    hits = Physics.OverlapCapsule(point1, point2, capsule.Radius, mask);
                    break;
                }
                default:
                    return CucliCommandResult.Invalid(
                        "shape", "3D overlap supports sphere, box or capsule (got '" + shape + "').");
            }

            foreach (Collider collider in hits.Where(item => item != null))
            {
                colliders.Add(new JObject
                {
                    ["gameObject"] = collider.gameObject.name,
                    ["instanceID"] = collider.gameObject.GetInstanceID(),
                    ["collider_type"] = collider.GetType().Name
                });
            }

            return CucliCommandResult.Ok(new { colliders });
        }

        // ------------------------------------------------------------------ shape parsing

        private struct CapsuleShape
        {
            internal float Radius;
            internal float Height;
            internal Vector3 Axis;
        }

        private static bool ReadScalar(JToken token, out float value, out string error)
        {
            value = 0f;
            error = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                error = "expected a number (or a single-element array).";
                return false;
            }

            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                if (array.Count == 0)
                {
                    error = "array is empty.";
                    return false;
                }

                value = array[0].Value<float>();
                return true;
            }

            value = token.Value<float>();
            if (value <= 0f)
            {
                error = "must be greater than zero.";
                return false;
            }

            return true;
        }

        private static bool ReadSize2(JToken token, out Vector2 half, out string error)
        {
            half = Vector2.zero;
            error = null;
            var array = token as JArray;
            if (array == null || array.Count < 2)
            {
                error = "expected [width, height].";
                return false;
            }

            // The contract for a 2D box size is full width/height; the cast takes half extents.
            half = new Vector2(array[0].Value<float>() * 0.5f, array[1].Value<float>() * 0.5f);
            return true;
        }

        private static bool ReadCapsule2(JToken token, out Vector2 size, out bool horizontal, out string error)
        {
            size = new Vector2(1f, 2f);
            horizontal = false;
            error = null;
            var shape = token as JObject;
            if (shape == null)
            {
                error = "a 2D capsule size must be an object with width, height and an optional direction.";
                return false;
            }

            float width = shape["width"] == null ? 1f : shape["width"].Value<float>();
            float height = shape["height"] == null ? 2f : shape["height"].Value<float>();
            size = new Vector2(width, height);

            JToken direction = shape["direction"];
            if (direction != null && direction.Type == JTokenType.String)
            {
                horizontal = string.Equals(
                    direction.Value<string>(), "horizontal", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static bool ReadCapsuleShape(JToken token, out CapsuleShape shape, out string error)
        {
            shape = new CapsuleShape { Radius = 0.5f, Height = 2f, Axis = Vector3.up };
            error = null;
            var obj = token as JObject;
            if (obj == null)
            {
                error = "a 3D capsule size must be an object with radius, height and an optional direction.";
                return false;
            }

            if (obj["radius"] != null) shape.Radius = obj["radius"].Value<float>();
            if (obj["height"] != null) shape.Height = obj["height"].Value<float>();
            if (obj["direction"] != null)
            {
                int direction = obj["direction"].Value<int>();
                shape.Axis = direction == 0 ? Vector3.right : direction == 2 ? Vector3.forward : Vector3.up;
            }

            if (shape.Height < shape.Radius * 2f) shape.Height = shape.Radius * 2f;
            return true;
        }

        private static bool ReadVector3Token(JToken token, out Vector3 value, out string error)
        {
            value = Vector3.zero;
            error = null;
            var array = token as JArray;
            if (array == null || array.Count < 3)
            {
                error = "expected [x, y, z].";
                return false;
            }

            value = new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
            return true;
        }

        private static bool ReadCapsuleEndpoints(
            CucliCommandContext context,
            Vector3 origin,
            float radius,
            out Vector3 point1,
            out Vector3 point2,
            out string error)
        {
            error = null;
            Vector3 explicit1;
            Vector3 explicit2;
            bool has1 = TryReadVector3(PhysicsSupport.Token(context.Input, "point1"), out explicit1);
            bool has2 = TryReadVector3(PhysicsSupport.Token(context.Input, "point2"), out explicit2);
            if (has1 && has2)
            {
                point1 = explicit1;
                point2 = explicit2;
                return true;
            }

            float height = context.Input.GetFloat("height", 2f);
            if (height < radius * 2f) height = radius * 2f;
            int direction = context.Input.GetInt("capsuleDirection", 1);
            Vector3 axis = direction == 0 ? Vector3.right : direction == 2 ? Vector3.forward : Vector3.up;
            float half = height * 0.5f - radius;
            point1 = origin + axis * half;
            point2 = origin - axis * half;
            return true;
        }

        private static bool TryReadVector3(JToken token, out Vector3 value)
        {
            value = Vector3.zero;
            var array = token as JArray;
            if (array == null || array.Count < 3) return false;
            value = new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
            return true;
        }

        // ------------------------------------------------------------------ hit shaping

        private static JObject Describe3D(RaycastHit hit)
        {
            return new JObject
            {
                ["hit"] = true,
                ["point"] = CucliDescribe.Value(hit.point, null, 0),
                ["normal"] = CucliDescribe.Value(hit.normal, null, 0),
                ["distance"] = hit.distance,
                ["gameObject"] = hit.collider == null ? null : hit.collider.gameObject.name,
                ["instanceID"] = hit.collider == null ? 0 : hit.collider.gameObject.GetInstanceID(),
                ["collider_type"] = hit.collider == null ? null : hit.collider.GetType().Name
            };
        }

        private static JObject Describe2D(RaycastHit2D hit)
        {
            return new JObject
            {
                ["hit"] = true,
                ["point"] = CucliDescribe.Value(hit.point, null, 0),
                ["normal"] = CucliDescribe.Value(hit.normal, null, 0),
                ["distance"] = hit.distance,
                ["gameObject"] = hit.collider == null ? null : hit.collider.gameObject.name,
                ["instanceID"] = hit.collider == null ? 0 : hit.collider.gameObject.GetInstanceID(),
                ["collider_type"] = hit.collider == null ? null : hit.collider.GetType().Name
            };
        }
    }

    /// <summary>Joint actions for the <c>physics</c> group.</summary>
    internal static class PhysicsJoints
    {
        private static readonly string[] Joints3D =
        {
            "fixed", "hinge", "spring", "character", "configurable"
        };

        private static readonly string[] Joints2D =
        {
            "distance", "fixed", "friction", "hinge", "relative", "slider", "spring", "target", "wheel"
        };

        private static Type JointType(string dimension, string name)
        {
            if (dimension == PhysicsCommand.Dimension2D)
            {
                switch (name)
                {
                    case "distance": return typeof(DistanceJoint2D);
                    case "fixed": return typeof(FixedJoint2D);
                    case "friction": return typeof(FrictionJoint2D);
                    case "hinge": return typeof(HingeJoint2D);
                    case "relative": return typeof(RelativeJoint2D);
                    case "slider": return typeof(SliderJoint2D);
                    case "spring": return typeof(SpringJoint2D);
                    case "target": return typeof(TargetJoint2D);
                    case "wheel": return typeof(WheelJoint2D);
                }

                return null;
            }

            switch (name)
            {
                case "fixed": return typeof(FixedJoint);
                case "hinge": return typeof(HingeJoint);
                case "spring": return typeof(SpringJoint);
                case "character": return typeof(CharacterJoint);
                case "configurable": return typeof(ConfigurableJoint);
            }

            return null;
        }

        internal static CucliCommandResult Add(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (error != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string requested = context.Input.GetString("dimension");
            string dimension;
            if (string.IsNullOrWhiteSpace(requested))
            {
                dimension = PhysicsCommand.DetectDimension(target);
            }
            else
            {
                string normalized = requested.Trim().ToLowerInvariant();
                if (normalized != PhysicsCommand.Dimension3D && normalized != PhysicsCommand.Dimension2D)
                {
                    return CucliCommandResult.Invalid("dimension", "must be '3d' or '2d'.");
                }

                dimension = normalized;
                bool hasBody = dimension == PhysicsCommand.Dimension2D
                    ? target.GetComponent<Rigidbody2D>() != null
                    : target.GetComponent<Rigidbody>() != null;
                if (!hasBody)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        target.name + " has no " + (dimension == PhysicsCommand.Dimension2D ? "Rigidbody2D" : "Rigidbody")
                        + ", which dimension=" + dimension + " requires.");
                }
            }

            string jointName = (context.Input.GetString("jointType") ?? string.Empty).Trim().ToLowerInvariant();
            Type type = JointType(dimension, jointName);
            if (type == null)
            {
                string[] valid = dimension == PhysicsCommand.Dimension2D ? Joints2D : Joints3D;
                return CucliCommandResult.Invalid(
                    "jointType",
                    "'" + jointName + "' is not a valid " + dimension + " joint. Valid: " + string.Join(", ", valid) + ".");
            }

            var joint = Undo.AddComponent(target, type);
            if (joint == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "Unity refused to add " + type.Name + ".");
            }

            string connected = context.Input.GetString("connectedBody");
            if (!string.IsNullOrWhiteSpace(connected))
            {
                string lookupError;
                GameObject other = CucliObjectResolver.ResolveGameObjects(
                    new CucliArgs(new JObject { ["target"] = connected }), out lookupError, true)[0];
                if (lookupError != null)
                {
                    UnityEngine.Object.DestroyImmediate(joint);
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, lookupError);
                }

                if (dimension == PhysicsCommand.Dimension2D)
                {
                    Rigidbody2D body2D = other.GetComponent<Rigidbody2D>();
                    if (body2D == null)
                    {
                        UnityEngine.Object.DestroyImmediate(joint);
                        return CucliCommandResult.Fail(
                            CucliErrors.TargetNotFound, other.name + " has no Rigidbody2D to connect to.");
                    }

                    SetMember(joint, "connectedBody", body2D);
                }
                else
                {
                    Rigidbody body3D = other.GetComponent<Rigidbody>();
                    if (body3D == null)
                    {
                        UnityEngine.Object.DestroyImmediate(joint);
                        return CucliCommandResult.Fail(
                            CucliErrors.TargetNotFound, other.name + " has no Rigidbody to connect to.");
                    }

                    SetMember(joint, "connectedBody", body3D);
                }
            }

            JObject properties = context.Input.GetObject("properties");
            var applied = new JArray();
            if (properties != null)
            {
                foreach (JProperty property in properties.Properties())
                {
                    string failure;
                    if (CucliReflection.TrySetMember(joint, property.Name, property.Value, out failure))
                    {
                        applied.Add(property.Name);
                    }
                    else
                    {
                        context.Warn("property '" + property.Name + "' was skipped: " + failure);
                    }
                }
            }

            EditorUtility.SetDirty(joint);
            return CucliCommandResult.Ok(new
            {
                jointType = type.Name,
                instanceID = joint.GetInstanceID(),
                gameObjectInstanceID = target.GetInstanceID(),
                propertiesApplied = applied
            }, "已添加 " + type.Name + "。");
        }

        private static void SetMember(object target, string member, object value)
        {
            string error;
            CucliReflection.TrySetMember(target, member, JToken.FromObject(value), out error);
        }

        internal static CucliCommandResult Configure(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (error != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string jointName = context.Input.GetString("jointType");
            bool hasIndex = context.Input.Has("componentIndex") || context.Input.Has("component_index");
            int componentIndex = context.Input.GetInt("componentIndex", context.Input.GetInt("component_index", 0));

            if (hasIndex && string.IsNullOrWhiteSpace(jointName))
            {
                return CucliCommandResult.Invalid(
                    "componentIndex", "jointType is also required so the index is unambiguous.");
            }

            Component joint = null;
            if (!string.IsNullOrWhiteSpace(jointName))
            {
                string name = jointName.Trim().ToLowerInvariant();
                Type type3D = JointType(PhysicsCommand.Dimension3D, name);
                Type type2D = JointType(PhysicsCommand.Dimension2D, name);
                Type type = type3D ?? type2D;
                if (type == null)
                {
                    return CucliCommandResult.Invalid("jointType", "'" + jointName + "' is not a known joint type.");
                }

                Component[] matches = target.GetComponents(type);
                if (matches.Length == 0)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, target.name + " has no " + type.Name + ".");
                }

                if (componentIndex < 0 || componentIndex >= matches.Length)
                {
                    return CucliCommandResult.Invalid(
                        "componentIndex", "out of range (0-" + (matches.Length - 1) + ").");
                }

                joint = matches[componentIndex];
            }
            else
            {
                // Without a type, "the joint" is only well defined when there is exactly one. Both
                // physics worlds are considered, because a target can legitimately carry either.
                Component[] all = target.GetComponents<Joint>().Cast<Component>()
                    .Concat(target.GetComponents<Joint2D>().Cast<Component>())
                    .ToArray();
                if (all.Length == 0)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, target.name + " has no joint.");
                }

                if (all.Length > 1)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetAmbiguous,
                        target.name + " has " + all.Length + " joints; pass jointType to choose one.",
                        new { joints = all.Select(item => item.GetType().Name).ToArray() });
                }

                joint = all[0];
            }

            Undo.RecordObject(joint, "cucli physics configure-joint");
            var configured = new JArray();

            JObject properties = context.Input.GetObject("properties");
            if (properties != null && properties.Count > 0)
            {
                foreach (JProperty property in properties.Properties())
                {
                    string failure;
                    if (CucliReflection.TrySetMember(joint, property.Name, property.Value, out failure))
                    {
                        continue;
                    }

                    context.Warn("property '" + property.Name + "' was skipped: " + failure);
                }

                configured.Add("properties");
            }

            JObject motor = context.Input.GetObject("motor");
            if (motor != null)
            {
                var hinge = joint as HingeJoint;
                if (hinge == null)
                {
                    return CucliCommandResult.Invalid("motor", "only a HingeJoint has a motor.");
                }

                var motorObject = new JointMotor();
                if (motor["targetVelocity"] != null) motorObject.targetVelocity = motor["targetVelocity"].Value<float>();
                if (motor["force"] != null) motorObject.force = motor["force"].Value<float>();
                if (motor["freeSpin"] != null) motorObject.freeSpin = motor["freeSpin"].Value<bool>();
                hinge.motor = motorObject;
                hinge.useMotor = true;
                configured.Add("motor");
            }

            JObject limits = context.Input.GetObject("limits");
            if (limits != null)
            {
                var hinge = joint as HingeJoint;
                if (hinge == null)
                {
                    return CucliCommandResult.Invalid("limits", "only a HingeJoint has limits.");
                }

                var limitObject = new JointLimits();
                if (limits["min"] != null) limitObject.min = limits["min"].Value<float>();
                if (limits["max"] != null) limitObject.max = limits["max"].Value<float>();
                if (limits["bounciness"] != null) limitObject.bounciness = limits["bounciness"].Value<float>();
                hinge.limits = limitObject;
                hinge.useLimits = true;
                configured.Add("limits");
            }

            JObject spring = context.Input.GetObject("spring");
            if (spring != null)
            {
                var hinge = joint as HingeJoint;
                var springJoint = joint as SpringJoint;
                if (hinge == null && springJoint == null)
                {
                    return CucliCommandResult.Invalid("spring", "only a HingeJoint or SpringJoint has a spring.");
                }

                var springObject = new JointSpring();
                if (spring["spring"] != null) springObject.spring = spring["spring"].Value<float>();
                if (spring["damper"] != null) springObject.damper = spring["damper"].Value<float>();
                if (spring["targetPosition"] != null) springObject.targetPosition = spring["targetPosition"].Value<float>();

                if (hinge != null)
                {
                    hinge.spring = springObject;
                    hinge.useSpring = true;
                }
                else
                {
                    // SpringJoint has no JointSpring struct; the equivalent knob is minDistance.
                    if (spring["targetPosition"] != null)
                    {
                        springJoint.minDistance = spring["targetPosition"].Value<float>();
                    }
                }

                configured.Add("spring");
            }

            JObject drive = context.Input.GetObject("drive");
            if (drive != null)
            {
                var configurable = joint as ConfigurableJoint;
                if (configurable == null)
                {
                    return CucliCommandResult.Invalid("drive", "only a ConfigurableJoint has a drive.");
                }

                JObject xDrive = drive["xDrive"] as JObject;
                if (xDrive != null)
                {
                    var driveObject = configurable.xDrive;
                    if (xDrive["positionSpring"] != null) driveObject.positionSpring = xDrive["positionSpring"].Value<float>();
                    if (xDrive["positionDamper"] != null) driveObject.positionDamper = xDrive["positionDamper"].Value<float>();
                    if (xDrive["maximumForce"] != null) driveObject.maximumForce = xDrive["maximumForce"].Value<float>();
                    configurable.xDrive = driveObject;
                }

                configured.Add("drive");
            }

            if (configured.Count == 0)
            {
                return CucliCommandResult.Missing(
                    "properties", "Provide properties, motor, limits, spring or drive.");
            }

            EditorUtility.SetDirty(joint);
            return CucliCommandResult.Ok(new
            {
                jointType = joint.GetType().Name,
                configured,
                instanceID = joint.GetInstanceID()
            });
        }

        internal static CucliCommandResult Remove(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (error != null)
            {
                return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            }

            string jointName = context.Input.GetString("jointType");
            int removed = 0;

            if (string.IsNullOrWhiteSpace(jointName))
            {
                // No type means "all of them", matching the documented contract; the count is reported
                // so a caller can tell an over-broad delete from a no-op.
                foreach (Joint joint in target.GetComponents<Joint>().ToArray())
                {
                    Undo.DestroyObjectImmediate(joint);
                    removed++;
                }

                foreach (Joint2D joint in target.GetComponents<Joint2D>().ToArray())
                {
                    Undo.DestroyObjectImmediate(joint);
                    removed++;
                }
            }
            else
            {
                string name = jointName.Trim().ToLowerInvariant();
                Type type3D = JointType(PhysicsCommand.Dimension3D, name);
                Type type2D = JointType(PhysicsCommand.Dimension2D, name);
                if (type3D == null && type2D == null)
                {
                    return CucliCommandResult.Invalid("jointType", "'" + jointName + "' is not a known joint type.");
                }

                if (type3D != null)
                {
                    foreach (Component joint in target.GetComponents(type3D).ToArray())
                    {
                        Undo.DestroyObjectImmediate(joint);
                        removed++;
                    }
                }

                if (type2D != null)
                {
                    foreach (Component joint in target.GetComponents(type2D).ToArray())
                    {
                        Undo.DestroyObjectImmediate(joint);
                        removed++;
                    }
                }
            }

            return CucliCommandResult.Ok(new
            {
                removedCount = removed,
                gameObjectInstanceID = target.GetInstanceID()
            }, removed == 0 ? "目标上没有可删除的关节。" : "已删除 " + removed + " 个关节。");
        }
    }
}
