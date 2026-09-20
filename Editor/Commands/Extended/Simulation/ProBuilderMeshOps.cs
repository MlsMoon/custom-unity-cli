using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>Shape creation for the <c>probuilder</c> group.</summary>
    internal static class ProBuilderShapes
    {
        internal static CucliCommandResult Create(CucliCommandContext context)
        {
            Type shapeTypes = ProBuilderSupport.ShapeType;
            Type generator = ProBuilderSupport.ShapeGenerator;
            Type pivot = ProBuilderSupport.PivotLocation;
            if (shapeTypes == null || generator == null || pivot == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "ShapeGenerator / ShapeType / PivotLocation could not be resolved in this ProBuilder version.");
            }

            string requested = context.Input.GetString("shapeType") ?? context.Input.GetString("shape_type");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return CucliCommandResult.Missing(
                    "shapeType", "One of: " + string.Join(", ", Enum.GetNames(shapeTypes)) + ".");
            }

            object shape;
            try
            {
                shape = Enum.Parse(shapeTypes, requested.Trim(), true);
            }
            catch (ArgumentException)
            {
                return CucliCommandResult.Invalid(
                    "shapeType",
                    "'" + requested + "' is not valid. Valid shape types: " + string.Join(", ", Enum.GetNames(shapeTypes)) + ".");
            }

            object pivotValue = Enum.Parse(pivot, "Center", true);
            ParameterOverride(context, shapeTypes, shape);

            object created;
            string error;
            if (!ProBuilderReflect.Invoke(
                    generator, "CreateShape", new[] { shape, pivotValue }, out created, out error))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "ShapeGenerator.CreateShape could not be invoked: " + error);
            }

            var mesh = created as Component;
            if (mesh == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "ShapeGenerator.CreateShape returned no ProBuilderMesh.");
            }

            Undo.RegisterCreatedObjectUndo(mesh.gameObject, "cucli probuilder create-shape");

            string name = context.Input.GetString("name");
            if (!string.IsNullOrWhiteSpace(name)) mesh.gameObject.name = name.Trim();

            JToken position = context.Input.Token("position");
            if (position != null && position.Type != JTokenType.Null)
            {
                Vector3 world;
                if (PhysicsCommand.ReadVector3(position, out world, out error))
                {
                    mesh.transform.position = world;
                }
                else
                {
                    context.Warn("position was ignored: " + error);
                }
            }

            JToken rotation = context.Input.Token("rotation");
            if (rotation != null && rotation.Type != JTokenType.Null)
            {
                Vector3 euler;
                if (PhysicsCommand.ReadVector3(rotation, out euler, out error))
                {
                    mesh.transform.rotation = Quaternion.Euler(euler);
                }
                else
                {
                    context.Warn("rotation was ignored: " + error);
                }
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                gameObjectName = mesh.gameObject.name,
                instanceId = mesh.gameObject.GetInstanceID(),
                shapeType = shape.ToString(),
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount"),
                vertexCount = ProBuilderSupport.IntOf(mesh, "vertexCount")
            }, "已创建 ProBuilder " + shape + "。");
        }

        /// <summary>
        /// ShapeGenerator reads its dimensions from public static fields on the ShapeGenerator type
        /// (the same fields the ProBuilder window writes) rather than from method arguments, so the
        /// payload has to be pushed there before CreateShape is called.
        /// </summary>
        private static void ParameterOverride(CucliCommandContext context, Type shapeTypes, object shape)
        {
            var args = context.Input;
            string[] numericFields =
            {
                "size", "width", "height", "depth", "radius", "thickness", "angle", "circumference",
                "innerRadius", "outerRadius", "tubeRadius", "ringRadius", "horizontalCircumference",
                "verticalCircumference", "ledgeHeight", "legWidth", "subdivHeight"
            };

            foreach (string field in numericFields)
            {
                JToken token = args.Token(field) ?? args.Token(PhysicsSupport.ToSnake(field));
                if (token == null || token.Type == JTokenType.Null) continue;
                SetStaticField(field, token.Value<float>());
            }

            // ProBuilder spells a couple of these differently from the CLI; both are accepted.
            SetStaticFieldFrom(args, "innerRadius", "tubeRadius");
            SetStaticFieldFrom(args, "innerRadius", "inner_radius");
            SetStaticFieldFrom(args, "outerRadius", "ringRadius");
            SetStaticFieldFrom(args, "outerRadius", "outer_radius");

            string[] intFields =
            {
                "axisDivisions", "heightCuts", "subdivAxis", "subdivisions", "rows", "columns",
                "widthCuts", "steps", "radialCuts", "segments", "smoothing", "axis"
            };

            foreach (string field in intFields)
            {
                JToken token = args.Token(field) ?? args.Token(PhysicsSupport.ToSnake(field));
                if (token == null || token.Type == JTokenType.Null) continue;
                SetStaticField(field, token.Value<int>());
            }

            SetStaticFieldFrom(args, "axisDivisions", "axis_divisions");
            SetStaticFieldFrom(args, "heightCuts", "height_cuts");
            SetStaticFieldFrom(args, "subdivAxis", "subdiv_axis");
            SetStaticFieldFrom(args, "widthCuts", "width_cuts");
            SetStaticFieldFrom(args, "radialCuts", "radial_cuts");

            string[] boolFields =
            {
                "smooth", "buildSides", "insideFaces", "outsideFaces", "frontFaces", "backFaces", "endCaps"
            };

            foreach (string field in boolFields)
            {
                JToken token = args.Token(field) ?? args.Token(PhysicsSupport.ToSnake(field));
                if (token == null || token.Type == JTokenType.Null) continue;
                SetStaticField(field, token.Value<bool>());
            }

            SetStaticFieldFrom(args, "buildSides", "build_sides");
            SetStaticFieldFrom(args, "insideFaces", "inside_faces");
            SetStaticFieldFrom(args, "outsideFaces", "outside_faces");
            SetStaticFieldFrom(args, "frontFaces", "front_faces");
            SetStaticFieldFrom(args, "backFaces", "back_faces");
            SetStaticFieldFrom(args, "endCaps", "end_caps");
        }

        private static void SetStaticFieldFrom(CucliArgs args, string field, string key)
        {
            JToken token = args.Token(key);
            if (token == null || token.Type == JTokenType.Null) return;
            if (token.Type == JTokenType.Boolean) SetStaticField(field, token.Value<bool>());
            else if (token.Type == JTokenType.Integer) SetStaticField(field, token.Value<int>());
            else if (token.Type == JTokenType.Float) SetStaticField(field, token.Value<float>());
        }

        private static void SetStaticField(string name, object value)
        {
            Type generator = ProBuilderSupport.ShapeGenerator;
            if (generator == null) return;
            try
            {
                var field = generator.GetField(
                    name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (field == null) return;
                field.SetValue(null, Convert.ChangeType(value, field.FieldType));
            }
            catch (Exception)
            {
                // A field this ProBuilder version does not expose is simply not overridden; the shape
                // is still created with that dimension's default, which is the documented fallback.
            }
        }

        internal static CucliCommandResult CreatePoly(CucliCommandContext context)
        {
            Type generator = ProBuilderSupport.ShapeGenerator;
            if (generator == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "ShapeGenerator could not be resolved.");
            }

            JArray points = context.Input.GetArray("points");
            if (points == null || points.Count < 3)
            {
                return CucliCommandResult.Missing("points", "At least 3 outline points are required.");
            }

            var outline = new List<Vector3>(points.Count);
            foreach (JToken point in points)
            {
                Vector3 value;
                string error;
                if (!PhysicsCommand.ReadVector3(point, out value, out error))
                {
                    return CucliCommandResult.Invalid("points", "each point needs 3 numbers: " + error);
                }

                outline.Add(value);
            }

            float extrudeHeight = context.Input.GetFloat("extrudeHeight", 1f);
            bool flipNormals = context.Input.GetBool("flipNormals", false);

            object created = null;
            string invokeError = null;
            bool invoked = false;

            // The poly-shape entry point has moved between ProBuilder versions, so the candidates are
            // tried in the order most likely to exist.
            foreach (string method in new[] { "CreatePolyShape", "GeneratePolyShape", "CreateShapeFromPolygon" })
            {
                if (ProBuilderReflect.Invoke(
                        generator, method,
                        new object[] { outline, extrudeHeight, flipNormals, false },
                        out created, out invokeError))
                {
                    invoked = true;
                    break;
                }

                if (ProBuilderReflect.Invoke(
                        generator, method,
                        new object[] { outline, extrudeHeight, flipNormals },
                        out created, out invokeError))
                {
                    invoked = true;
                    break;
                }
            }

            if (!invoked)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "No polygon-shape entry point was found on ShapeGenerator (tried CreatePolyShape, "
                    + "GeneratePolyShape, CreateShapeFromPolygon): " + invokeError);
            }

            var mesh = created as Component;
            if (mesh == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "The polygon entry point returned no ProBuilderMesh.");
            }

            Undo.RegisterCreatedObjectUndo(mesh.gameObject, "cucli probuilder create-poly-shape");
            string name = context.Input.GetString("name");
            if (!string.IsNullOrWhiteSpace(name)) mesh.gameObject.name = name.Trim();

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                gameObjectName = mesh.gameObject.name,
                instanceId = mesh.gameObject.GetInstanceID(),
                pointCount = outline.Count,
                extrudeHeight,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount"),
                vertexCount = ProBuilderSupport.IntOf(mesh, "vertexCount")
            }, "已由轮廓创建 ProBuilder 实体。");
        }
    }

    /// <summary>
    /// Topology editing for the <c>probuilder</c> group.
    ///
    /// Each action resolves its faces or edges, invokes the matching MeshOperations entry point, then
    /// calls Refresh() so the Unity Mesh and the reported counts agree with the edit. Where a method
    /// signature differs between ProBuilder versions, candidate argument shapes are tried in order and
    /// a precise error names what was attempted.
    /// </summary>
    internal static class ProBuilderMeshOps
    {
        internal static CucliCommandResult ExtrudeFaces(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] faces;
            string error;
            if (!ProBuilderSupport.ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            float distance = context.Input.GetFloat("distance", 0.5f);
            string methodName = context.Input.GetString("method") ?? "FaceNormal";
            object method;
            Type methodType = ProBuilderSupport.ExtrudeMethod;
            if (methodType == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ExtrudeMethod could not be resolved.");
            }

            try
            {
                method = Enum.Parse(methodType, methodName.Trim(), true);
            }
            catch (ArgumentException)
            {
                return CucliCommandResult.Invalid(
                    "method", "'" + methodName + "' is not valid. Valid: " + string.Join(", ", Enum.GetNames(methodType)) + ".");
            }

            Type ops = ProBuilderSupport.MeshOperation("ExtrudeElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.ExtrudeElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder extrude-faces");
            object result;
            if (!TryInvoke(ops, "Extrude",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces), distance, method },
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces), method, distance },
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces), distance }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ExtrudeElements.Extrude failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                facesExtruded = faces.Length,
                distance,
                method = method.ToString(),
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult ExtrudeEdges(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] edges;
            string error;
            if (!ProBuilderSupport.ResolveEdges(context.Input, mesh, out edges, out error))
            {
                return CucliCommandResult.Invalid("edgeIndices", error);
            }

            float distance = context.Input.GetFloat("distance", 0.5f);
            bool asGroup = context.Input.GetBool("asGroup", context.Input.GetBool("as_group", true));

            Type ops = ProBuilderSupport.MeshOperation("ExtrudeElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.ExtrudeElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder extrude-edges");
            object result;
            if (!TryInvoke(ops, "Extrude",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildEdgeList(edges), distance, asGroup },
                        new object[] { mesh, ProBuilderSupport.BuildEdgeList(edges), distance }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ExtrudeElements.Extrude (edges) failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                edgesExtruded = edges.Length,
                distance,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult BevelEdges(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] edges;
            string error;
            if (!ProBuilderSupport.ResolveEdges(context.Input, mesh, out edges, out error))
            {
                return CucliCommandResult.Invalid("edgeIndices", error);
            }

            float amount = Mathf.Clamp01(context.Input.GetFloat("amount", 0.1f));
            Type ops = ProBuilderSupport.MeshOperation("Bevel");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.Bevel was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder bevel-edges");
            object result;
            if (!TryInvoke(ops, "BevelEdges",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildEdgeList(edges), amount },
                        new object[] { mesh, ProBuilderSupport.BuildEdgeList(edges) }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "Bevel.BevelEdges failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                edgesBeveled = edges.Length,
                amount,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult Subdivide(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] faces;
            string error;
            if (!ProBuilderSupport.ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            Type ops = ProBuilderSupport.MeshOperation("SurfaceTopology");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.SurfaceTopology was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder subdivide");
            object result;
            if (!TryInvoke(ops, "Subdivide",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces) }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "SurfaceTopology.Subdivide failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount"),
                vertexCount = ProBuilderSupport.IntOf(mesh, "vertexCount")
            });
        }

        internal static CucliCommandResult DeleteFaces(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] faces;
            string error;
            if (!ProBuilderSupport.ResolveFaces(context.Input, mesh, true, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            Type ops = ProBuilderSupport.MeshOperation("DeleteElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.DeleteElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder delete-faces");
            object result;
            if (!TryInvoke(ops, "DeleteFaces",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces) },
                        new object[] { mesh, faces }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "DeleteElements.DeleteFaces failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                facesDeleted = faces.Length,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult FlipNormals(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] faces;
            string error;
            if (!ProBuilderSupport.ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            Undo.RecordObject(mesh, "cucli probuilder flip-normals");
            int flipped = 0;
            foreach (object face in faces)
            {
                // Face.Reverse() is the documented way to flip a single face's winding.
                object result;
                string invokeError;
                if (ProBuilderReflect.InvokeInstance(
                        face, "Reverse", Array.Empty<object>(), out result, out invokeError))
                {
                    flipped++;
                }
                else if (Set(face, "indexes", ReverseFaceIndexes(face)))
                {
                    flipped++;
                }
            }

            if (flipped == 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Neither Face.Reverse() nor a writable Face.indexes was available to flip winding.");
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new { facesFlipped = flipped });
        }

        private static int[] ReverseFaceIndexes(object face)
        {
            IList indexes = ProBuilderSupport.Get(face, "indexes") as IList;
            if (indexes == null) return Array.Empty<int>();
            var values = indexes.Cast<object>().Select(item => Convert.ToInt32(item)).ToArray();
            Array.Reverse(values);
            return values;
        }

        internal static CucliCommandResult MergeFaces(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] faces;
            string error;
            if (!ProBuilderSupport.ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            if (faces.Length < 2)
            {
                return CucliCommandResult.Invalid("faceIndices", "merging needs at least two faces.");
            }

            Type ops = ProBuilderSupport.MeshOperation("MergeElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.MergeElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder merge-faces");
            object result;
            if (!TryInvoke(ops, "MergeFaces",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces) },
                        new object[] { mesh, faces }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MergeElements.MergeFaces failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                facesMerged = faces.Length,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult DetachFaces(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] faces;
            string error;
            if (!ProBuilderSupport.ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            bool deleteSource = context.Input.GetBool("deleteSourceFaces",
                context.Input.GetBool("delete_source_faces",
                context.Input.GetBool("deleteSource", context.Input.GetBool("delete_source", false))));

            Type ops = ProBuilderSupport.MeshOperation("DeleteElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.DeleteElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder detach-faces");
            object result;
            if (!TryInvoke(ops, "DetachFaces",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces), deleteSource },
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces) }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "DeleteElements.DetachFaces failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                facesDetached = faces.Length,
                deleteSourceFaces = deleteSource,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult DuplicateAndFlip(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] faces;
            string error;
            if (!ProBuilderSupport.ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            Type ops = ProBuilderSupport.MeshOperation("DuplicateAndFlip");
            if (ops == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "MeshOperations.DuplicateAndFlip was not found in this ProBuilder version.");
            }

            Undo.RecordObject(mesh, "cucli probuilder duplicate-and-flip");
            object result;
            if (!TryInvoke(ops, "DuplicateAndFlipFaces",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildFaceList(faces) }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "DuplicateAndFlipFaces failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                facesDuplicated = faces.Length,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult BridgeEdges(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            int a1;
            int b1;
            int a2;
            int b2;
            string error;
            if (!ProBuilderSupport.ReadEdge(context.Input.Token("edgeA") ?? context.Input.Token("edge_a"), out a1, out b1, out error))
            {
                return CucliCommandResult.Invalid("edgeA", error);
            }

            if (!ProBuilderSupport.ReadEdge(context.Input.Token("edgeB") ?? context.Input.Token("edge_b"), out a2, out b2, out error))
            {
                return CucliCommandResult.Invalid("edgeB", error);
            }

            bool allowNonManifold = context.Input.GetBool("allowNonManifold",
                context.Input.GetBool("allow_non_manifold",
                context.Input.GetBool("allowNonManifoldGeometry",
                context.Input.GetBool("allow_non_manifold_geometry", false))));

            object edgeA = ProBuilderSupport.MakeEdge(a1, b1);
            object edgeB = ProBuilderSupport.MakeEdge(a2, b2);
            if (edgeA == null || edgeB == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "The Edge type could not be resolved.");
            }

            Type ops = ProBuilderSupport.MeshOperation("ConnectElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.ConnectElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder bridge-edges");
            object result;
            if (!TryInvoke(ops, "Bridge",
                    new[]
                    {
                        new object[] { mesh, edgeA, edgeB, allowNonManifold },
                        new object[] { mesh, edgeA, edgeB }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ConnectElements.Bridge failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                bridgeCreated = true,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult ConnectElements(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            Type ops = ProBuilderSupport.MeshOperation("ConnectElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.ConnectElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder connect-elements");
            object result;
            string error;

            object[] edges;
            if (ProBuilderSupport.ResolveEdges(context.Input, mesh, out edges, out error) && edges.Length > 0)
            {
                if (!TryInvoke(ops, "ConnectEdges",
                        new[] { new object[] { mesh, ProBuilderSupport.BuildEdgeList(edges) } },
                        out result, out error))
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ConnectElements.ConnectEdges failed: " + error);
                }
            }
            else
            {
                object[] faces;
                if (!ProBuilderSupport.ResolveFaces(context.Input, mesh, false, out faces, out error))
                {
                    return CucliCommandResult.Invalid("faceIndices", error);
                }

                if (!TryInvoke(ops, "ConnectFaces",
                        new[] { new object[] { mesh, ProBuilderSupport.BuildFaceList(faces) } },
                        out result, out error))
                {
                    return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ConnectElements.ConnectFaces failed: " + error);
                }
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new { faceCount = ProBuilderSupport.IntOf(mesh, "faceCount") });
        }

        internal static CucliCommandResult CreatePolygon(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            int[] vertices = context.Input.GetIntList("vertexIndices");
            if (vertices.Length == 0) vertices = context.Input.GetIntList("vertex_indices");
            if (vertices.Length < 3)
            {
                return CucliCommandResult.Missing("vertexIndices", "At least 3 vertex indices are required.");
            }

            bool unordered = context.Input.GetBool("unordered", true);
            Type ops = ProBuilderSupport.MeshOperation("AppendElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.AppendElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder create-polygon");
            object result;
            string error;
            if (!TryInvoke(ops, "CreatePolygon",
                    new[]
                    {
                        new object[] { mesh, vertices, unordered },
                        new object[] { mesh, vertices }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "AppendElements.CreatePolygon failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                vertexCount = vertices.Length,
                unordered,
                faceCreated = result != null,
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult CombineMeshes(CucliCommandContext context, bool mergeObjects)
        {
            string[] targets = context.Input.GetStringList("targets");
            if (targets.Length < 2)
            {
                return CucliCommandResult.Missing("targets", "At least 2 target GameObject identifiers are required.");
            }

            var meshes = new List<Component>();
            var gameObjects = new List<GameObject>();
            foreach (string identifier in targets)
            {
                string error;
                GameObject resolved = CucliObjectResolver.ResolveGameObjects(
                    new CucliArgs(new JObject { ["target"] = identifier }), out error, true).FirstOrDefault();
                if (error != null)
                {
                    return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
                }

                if (resolved == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound, "Nothing matched target '" + identifier + "'.");
                }

                gameObjects.Add(resolved);
                Component mesh = mergeObjects ? resolved.GetComponent(ProBuilderSupport.Mesh) : null;
                if (mergeObjects && mesh == null)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.TargetNotFound,
                        resolved.name + " has no ProBuilderMesh; convert it first.");
                }

                meshes.Add(mesh);
            }

            GameObject host = gameObjects[0];
            Type ops = ProBuilderSupport.MeshOperation("CombineMeshes");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.CombineMeshes was not found.");
            }

            Undo.RegisterFullObjectHierarchyUndo(host, "cucli probuilder combine");
            object result;
            string invokeError;
            IList meshList = ProBuilderSupport.BuildComponentList(meshes);

            if (!TryInvoke(ops, "Combine",
                    new[]
                    {
                        new object[] { meshList },
                        new object[] { meshList, true }
                    },
                    out result, out invokeError))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "CombineMeshes.Combine failed: " + invokeError);
            }

            Component combined = host.GetComponent(ProBuilderSupport.Mesh);
            if (combined != null) ProBuilderSupport.Refresh(combined);

            string name = context.Input.GetString("name");
            if (!string.IsNullOrWhiteSpace(name)) host.name = name.Trim();

            int removed = 0;
            if (mergeObjects)
            {
                for (int index = 1; index < gameObjects.Count; index++)
                {
                    Undo.DestroyObjectImmediate(gameObjects[index]);
                    removed++;
                }
            }

            return CucliCommandResult.Ok(new
            {
                meshesCombined = meshes.Count,
                mergedCount = removed,
                convertedCount = 0,
                targetName = host.name,
                faceCount = combined == null ? 0 : ProBuilderSupport.IntOf(combined, "faceCount"),
                vertexCount = combined == null ? 0 : ProBuilderSupport.IntOf(combined, "vertexCount")
            }, "已合并 " + meshes.Count + " 个网格。");
        }

        internal static CucliCommandResult MergeVertices(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            int[] vertices = context.Input.GetIntList("vertexIndices");
            if (vertices.Length == 0) vertices = context.Input.GetIntList("vertex_indices");
            if (vertices.Length < 2)
            {
                return CucliCommandResult.Missing("vertexIndices", "At least 2 vertex indices are required.");
            }

            bool collapseToFirst = context.Input.GetBool("collapseToFirst", context.Input.GetBool("collapse_to_first", false));
            Type ops = ProBuilderSupport.MeshOperation("VertexEditing");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.VertexEditing was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder merge-vertices");
            object result;
            string error;
            if (!TryInvoke(ops, "MergeVertices",
                    new[]
                    {
                        new object[] { mesh, vertices, collapseToFirst },
                        new object[] { mesh, vertices }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "VertexEditing.MergeVertices failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                verticesMerged = vertices.Length,
                collapseToFirst,
                resultIndex = result == null ? 0 : Convert.ToInt32(result),
                vertexCount = ProBuilderSupport.IntOf(mesh, "vertexCount")
            });
        }

        internal static CucliCommandResult WeldVertices(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            int[] vertices = context.Input.GetIntList("vertexIndices");
            if (vertices.Length == 0) vertices = context.Input.GetIntList("vertex_indices");
            if (vertices.Length < 2)
            {
                return CucliCommandResult.Missing("vertexIndices", "At least 2 vertex indices are required.");
            }

            float radius = context.Input.GetFloat("radius",
                context.Input.GetFloat("neighborRadius", context.Input.GetFloat("neighbor_radius", 0.01f)));

            Type ops = ProBuilderSupport.MeshOperation("VertexEditing");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.VertexEditing was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder weld-vertices");
            object result;
            string error;
            if (!TryInvoke(ops, "WeldVertices",
                    new[]
                    {
                        new object[] { mesh, vertices, radius },
                        new object[] { mesh, vertices }
                    },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "VertexEditing.WeldVertices failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                inputCount = vertices.Length,
                resultCount = result == null ? 0 : Convert.ToInt32(result),
                radius,
                vertexCount = ProBuilderSupport.IntOf(mesh, "vertexCount")
            });
        }

        internal static CucliCommandResult SplitVertices(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            int[] vertices = context.Input.GetIntList("vertexIndices");
            if (vertices.Length == 0) vertices = context.Input.GetIntList("vertex_indices");
            if (vertices.Length == 0)
            {
                return CucliCommandResult.Missing("vertexIndices");
            }

            Type ops = ProBuilderSupport.MeshOperation("VertexEditing");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.VertexEditing was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder split-vertices");
            object result;
            string error;
            if (!TryInvoke(ops, "SplitVertices",
                    new[] { new object[] { mesh, (IList)vertices } },
                    out result, out error))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "VertexEditing.SplitVertices failed: " + error);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                verticesSplit = vertices.Length,
                vertexCount = ProBuilderSupport.IntOf(mesh, "vertexCount")
            });
        }

        internal static CucliCommandResult MoveVertices(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            int[] vertices = context.Input.GetIntList("vertexIndices");
            if (vertices.Length == 0) vertices = context.Input.GetIntList("vertex_indices");
            if (vertices.Length == 0)
            {
                return CucliCommandResult.Missing("vertexIndices");
            }

            Vector3 offset;
            string error;
            if (!PhysicsCommand.ReadVector3(context.Input.Token("offset"), out offset, out error))
            {
                return CucliCommandResult.Missing("offset", "offset parameter is required ([x,y,z]).");
            }

            IList positions = ProBuilderSupport.ListOf(mesh, "positions");
            if (positions == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ProBuilderMesh.positions is unavailable.");
            }

            int vertexCount = positions.Count;
            foreach (int index in vertices)
            {
                if (index < 0 || index >= vertexCount)
                {
                    return CucliCommandResult.Invalid(
                        "vertexIndices", "vertex index " + index + " out of range (0-" + (vertexCount - 1) + ").");
                }
            }

            Undo.RecordObject(mesh, "cucli probuilder move-vertices");
            var moved = new List<Vector3>(vertexCount);
            foreach (object item in positions) moved.Add((Vector3)item);

            foreach (int index in vertices) moved[index] = moved[index] + offset;

            if (!ProBuilderSupport.SetPositions(mesh, moved))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "SetPositions could not be invoked.");
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                verticesMoved = vertices.Length,
                offset = CucliDescribe.Value(offset, null, 0)
            });
        }

        internal static CucliCommandResult InsertVertex(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            JToken pointToken = context.Input.Token("point") ?? context.Input.Token("position");
            Vector3 point;
            string error;
            if (!PhysicsCommand.ReadVector3(pointToken, out point, out error))
            {
                return CucliCommandResult.Missing("point", "point parameter is required ([x,y,z] in local space).");
            }

            Type ops = ProBuilderSupport.MeshOperation("AppendElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.AppendElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder insert-vertex");
            object result;
            string invokeError;

            JToken edgeToken = context.Input.Token("edge");
            if (edgeToken != null && edgeToken.Type == JTokenType.Object)
            {
                int a;
                int b;
                if (!ProBuilderSupport.ReadEdge(edgeToken, out a, out b, out error))
                {
                    return CucliCommandResult.Invalid("edge", error);
                }

                object edge = ProBuilderSupport.MakeEdge(a, b);
                if (!TryInvoke(ops, "InsertVertexOnEdge",
                        new[]
                        {
                            new object[] { mesh, edge, point },
                            new object[] { mesh, a, b, point }
                        },
                        out result, out invokeError))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "AppendElements.InsertVertexOnEdge failed: " + invokeError);
                }
            }
            else
            {
                string faceIndexText = context.Input.GetString("faceIndex");
                int faceIndex = context.Input.GetInt("faceIndex", int.MinValue);
                if (faceIndex == int.MinValue) faceIndex = context.Input.GetInt("face_index", int.MinValue);
                if (faceIndex == int.MinValue)
                {
                    return CucliCommandResult.Missing(
                        "edge", "Provide 'edge' {a,b} or 'faceIndex'. " + (faceIndexText ?? string.Empty));
                }

                object[] faces = ProBuilderSupport.Faces(mesh);
                if (faceIndex < 0 || faceIndex >= faces.Length)
                {
                    return CucliCommandResult.Invalid(
                        "faceIndex", "out of range (0-" + (faces.Length - 1) + ").");
                }

                if (!TryInvoke(ops, "InsertVertex",
                        new[]
                        {
                            new object[] { mesh, point, faces[faceIndex] },
                            new object[] { mesh, faces[faceIndex], point }
                        },
                        out result, out invokeError))
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "AppendElements.InsertVertex failed: " + invokeError);
                }
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                point = CucliDescribe.Value(point, null, 0),
                vertexCount = ProBuilderSupport.IntOf(mesh, "vertexCount"),
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        internal static CucliCommandResult AppendVerticesToEdge(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!ProBuilderSupport.TryMesh(context, out mesh, out failure)) return failure;

            object[] edges;
            string error;
            if (!ProBuilderSupport.ResolveEdges(context.Input, mesh, out edges, out error))
            {
                return CucliCommandResult.Invalid("edgeIndices", error);
            }

            int count = Mathf.Max(1, context.Input.GetInt("count", 1));
            Type ops = ProBuilderSupport.MeshOperation("AppendElements");
            if (ops == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "MeshOperations.AppendElements was not found.");
            }

            Undo.RecordObject(mesh, "cucli probuilder append-vertices");
            object result;
            string invokeError;
            if (!TryInvoke(ops, "AppendVerticesToEdge",
                    new[]
                    {
                        new object[] { mesh, ProBuilderSupport.BuildEdgeList(edges), count },
                        new object[] { mesh, ProBuilderSupport.BuildEdgeList(edges) }
                    },
                    out result, out invokeError))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "AppendElements.AppendVerticesToEdge failed: " + invokeError);
            }

            ProBuilderSupport.Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                edgesModified = edges.Length,
                pointsPerEdge = count,
                vertexCount = ProBuilderSupport.IntOf(mesh, "vertexCount"),
                faceCount = ProBuilderSupport.IntOf(mesh, "faceCount")
            });
        }

        // ------------------------------------------------------------------ reflection helpers

        /// <summary>
        /// Invokes the first candidate argument list that the reflected method accepts. ProBuilder's
        /// MeshOperations signatures have drifted across versions, and trying the documented shapes in
        /// order is far more robust than hard-coding one.
        /// </summary>
        private static bool TryInvoke(
            Type type,
            string method,
            object[][] candidates,
            out object result,
            out string error)
        {
            result = null;
            error = null;
            if (type == null)
            {
                error = "the containing type could not be resolved.";
                return false;
            }

            foreach (object[] candidate in candidates)
            {
                if (ProBuilderReflect.Invoke(type, method, candidate, out result, out error))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Set(object target, string member, object value)
        {
            if (target == null) return false;
            JToken token = value == null ? JValue.CreateNull() : CucliJson.ToToken(value);
            return CucliReflection.TrySetMember(target, member, token, out _);
        }
    }

    /// <summary>
    /// Overload-aware reflection invoker for ProBuilder.
    ///
    /// <c>CucliOptional.TryInvokeStatic</c> matches a method by its arguments' exact runtime types,
    /// which is fine for the narrow API surface the rest of the CLI reflects into but useless here:
    /// ProBuilder's MeshOperations take interfaces such as <c>IList&lt;Face&gt;</c> and
    /// <c>IEnumerable&lt;Edge&gt;</c>, so a <c>List&lt;Face&gt;</c> argument would never match.
    ///
    /// This resolver selects the first overload whose parameters each accept the supplied value by
    /// assignability or numeric conversion, which is what makes one implementation work across
    /// ProBuilder versions instead of one per signature.
    /// </summary>
    internal static class ProBuilderReflect
    {
        internal static bool Invoke(Type type, string method, object[] args, out object result, out string error)
        {
            result = null;
            error = null;
            if (type == null)
            {
                error = "the containing type could not be resolved.";
                return false;
            }

            args = args ?? Array.Empty<object>();
            var candidates = type
                .GetMethods(System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Static)
                .Where(item => string.Equals(item.Name, method, StringComparison.Ordinal))
                .Where(item => !item.IsGenericMethodDefinition)
                .Where(item => item.GetParameters().Length == args.Length)
                .ToArray();

            if (candidates.Length == 0)
            {
                error = type.Name + "." + method + " taking " + args.Length + " argument(s) was not found.";
                return false;
            }

            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                System.Reflection.ParameterInfo[] parameters = candidate.GetParameters();
                var converted = new object[args.Length];
                bool usable = true;

                for (int index = 0; index < args.Length; index++)
                {
                    if (TryCoerce(args[index], parameters[index].ParameterType, out converted[index])) continue;
                    failures.Add(Describe(parameters[index].ParameterType) + " <- "
                        + (args[index] == null ? "null" : args[index].GetType().Name));
                    usable = false;
                    break;
                }

                if (!usable) continue;

                try
                {
                    result = candidate.Invoke(null, converted);
                    return true;
                }
                catch (System.Reflection.TargetInvocationException exception)
                {
                    // The method was found and ran; its own failure is the real answer, so it is
                    // reported rather than masked by trying other overloads.
                    error = (exception.InnerException ?? exception).GetBaseException().Message;
                    return false;
                }
                catch (Exception exception)
                {
                    failures.Add(exception.GetBaseException().Message);
                }
            }

            error = "no overload of " + type.Name + "." + method + " matched ("
                + string.Join(", ", failures.Distinct().Take(4)) + ").";
            return false;
        }

        /// <summary>
        /// Instance counterpart of <see cref="Invoke"/>. Most of the ProBuilder surface that matters
        /// here is instance methods - <c>ProBuilderMesh.Refresh/ToMesh/SetPositions/SetMaterial/
        /// SetFaceColor/RefreshUV</c>, <c>Face.Reverse</c>, <c>MeshImporter.Import</c> - so calling
        /// them through the static overload (which searches BindingFlags.Static) silently finds
        /// nothing and every one of those actions fails.
        /// </summary>
        internal static bool InvokeInstance(object target, string method, object[] args, out object result, out string error)
        {
            result = null;
            error = null;
            if (target == null)
            {
                error = "the instance is null.";
                return false;
            }

            Type type = target.GetType();
            args = args ?? Array.Empty<object>();
            var candidates = type
                .GetMethods(System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance)
                .Where(item => string.Equals(item.Name, method, StringComparison.Ordinal))
                .Where(item => !item.IsGenericMethodDefinition)
                .Where(item => item.GetParameters().Length == args.Length)
                .ToArray();

            if (candidates.Length == 0)
            {
                error = type.Name + "." + method + " taking " + args.Length + " argument(s) was not found.";
                return false;
            }

            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                System.Reflection.ParameterInfo[] parameters = candidate.GetParameters();
                var converted = new object[args.Length];
                bool usable = true;

                for (int index = 0; index < args.Length; index++)
                {
                    if (TryCoerce(args[index], parameters[index].ParameterType, out converted[index])) continue;
                    failures.Add(Describe(parameters[index].ParameterType) + " <- "
                        + (args[index] == null ? "null" : args[index].GetType().Name));
                    usable = false;
                    break;
                }

                if (!usable) continue;

                try
                {
                    result = candidate.Invoke(target, converted);
                    return true;
                }
                catch (System.Reflection.TargetInvocationException exception)
                {
                    error = (exception.InnerException ?? exception).GetBaseException().Message;
                    return false;
                }
                catch (Exception exception)
                {
                    failures.Add(exception.GetBaseException().Message);
                }
            }

            error = "no overload of " + type.Name + "." + method + " matched ("
                + string.Join(", ", failures.Distinct().Take(4)) + ").";
            return false;
        }

        private static bool TryCoerce(object value, Type target, out object converted)
        {
            converted = null;
            if (value == null)
            {
                return !target.IsValueType || Nullable.GetUnderlyingType(target) != null;
            }

            // IsInstanceOfType covers both classes and interfaces, so List<T> satisfies IList<T>.
            if (target.IsInstanceOfType(value))
            {
                converted = value;
                return true;
            }

            if (target.IsEnum)
            {
                var text = value as string;
                if (text != null)
                {
                    try
                    {
                        converted = Enum.Parse(target, text.Trim(), true);
                        return true;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }

                if (value is IConvertible)
                {
                    converted = Enum.ToObject(target, Convert.ToInt64(value));
                    return true;
                }

                return false;
            }

            if (value is IConvertible && (target.IsPrimitive || target == typeof(decimal)))
            {
                try
                {
                    converted = Convert.ChangeType(value, target);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;
        }

        private static string Describe(Type type)
        {
            return type.IsGenericType ? type.Name.Split('`')[0] + "<...>" : type.Name;
        }
    }
}
