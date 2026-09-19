using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli probuilder ...</c> - ProBuilder mesh authoring: shape creation, topology editing,
    /// selection queries, face material/colour/UV, smoothing and mesh validation.
    ///
    /// ProBuilder is an optional package, so nothing here is referenced at compile time. Every type
    /// and member is reached through <see cref="CucliOptional"/> reflection and the group degrades to
    /// <c>OPTIONAL_PACKAGE_MISSING</c> when <c>com.unity.probuilder</c> is absent. That is the whole
    /// point: a project without ProBuilder must still compile this assembly, and a project with it
    /// must get real mesh editing rather than a stub.
    ///
    /// Because reflection can only find what a given ProBuilder version actually exposes, an action
    /// whose underlying member is missing fails with a message naming the member it looked for. A
    /// silent no-op would be far worse than a loud, specific error on destructive mesh operations.
    /// </summary>
    internal sealed class ProBuilderCommand : CucliCommand
    {
        internal const string PackageName = "com.unity.probuilder";

        public override string Name => "probuilder";

        public override string Summary =>
            "Create and edit ProBuilder meshes: shapes, topology, face material/colour/UV, smoothing, validation.";

        public override CucliActionInfo[] Actions => Declare(
            "ping", "Report ProBuilder availability and the resolved type surface.",
            "create-shape", "Generate a ProBuilder primitive.",
            "create-poly-shape", "Extrude a polygon outline into a solid.",
            "extrude-faces", "Extrude faces by a distance.",
            "extrude-edges", "Extrude edges by a distance.",
            "bevel-edges", "Bevel edges.",
            "subdivide", "Subdivide faces.",
            "delete-faces", "Delete faces.",
            "bridge-edges", "Bridge two open edges.",
            "connect-elements", "Connect elements across selected faces or edges.",
            "detach-faces", "Detach faces into a separate object.",
            "flip-normals", "Flip the normals of faces.",
            "merge-faces", "Merge faces into one.",
            "combine-meshes", "Combine several ProBuilder meshes into one.",
            "merge-objects", "Merge several objects into one ProBuilder mesh.",
            "duplicate-and-flip", "Duplicate faces and flip the copies.",
            "create-polygon", "Create a face from existing vertices.",
            "merge-vertices", "Merge vertices into a single one.",
            "weld-vertices", "Weld nearby vertices.",
            "split-vertices", "Split shared vertices.",
            "move-vertices", "Translate vertices by an offset.",
            "insert-vertex", "Insert a vertex on a face or edge.",
            "append-vertices-to-edge", "Insert evenly spaced vertices along edges.",
            "select-faces", "Select faces by direction, growth, flood or loop.",
            "set-face-material", "Assign a material to faces.",
            "set-face-color", "Assign a vertex colour to faces.",
            "set-face-uvs", "Apply UV scale/offset/rotation/flip to faces.",
            "get-mesh-info", "Report mesh counts, bounds, materials and optional face/edge detail.",
            "convert-to-probuilder", "Convert a plain Mesh into a ProBuilder mesh.",
            "set-smoothing", "Set the smoothing group on faces.",
            "auto-smooth", "Apply automatic smoothing by angle threshold.",
            "center-pivot", "Move the mesh so its pivot is centred.",
            "freeze-transform", "Bake the transform into the vertices.",
            "set-pivot", "Move the pivot to a world position without moving the mesh.",
            "validate-mesh", "Report degenerate triangles and unused vertices.",
            "repair-mesh", "Remove degenerate triangles from the mesh.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Edit;

        public override bool MutatesProject => true;

        public override string OptionalPackage => PackageName;

        public override string[] Effects => new[] { "editor.scene.read", "editor.scene.write", "project.assets.write" };

        // The action enum is generated from Actions() rather than hand-listed: with 36 actions a
        // hand-written list drifts the moment an action is added or renamed, and a schema that omits
        // an action makes it undiscoverable to anything reading 'commands describe'.
        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": true,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      // Written out literally rather than generated, so offline verifiers (Tools/verify.ps1,
      // the contract checker) can confirm it matches Actions(). The edit surface is one line per
      // action; keep it in step when adding or renaming one.
      ""enum"": [
      ""ping"", ""create-shape"", ""create-poly-shape"", ""extrude-faces"", ""extrude-edges"",
      ""bevel-edges"", ""subdivide"", ""delete-faces"", ""bridge-edges"", ""connect-elements"",
      ""detach-faces"", ""flip-normals"", ""merge-faces"", ""combine-meshes"", ""merge-objects"",
      ""duplicate-and-flip"", ""create-polygon"", ""merge-vertices"", ""weld-vertices"",
      ""split-vertices"", ""move-vertices"", ""insert-vertex"", ""append-vertices-to-edge"",
      ""select-faces"", ""set-face-material"", ""set-face-color"", ""set-face-uvs"", ""get-mesh-info"",
      ""convert-to-probuilder"", ""set-smoothing"", ""auto-smooth"", ""center-pivot"",
      ""freeze-transform"", ""set-pivot"", ""validate-mesh"", ""repair-mesh""
      ]
    },
    ""target"": { ""type"": ""string"", ""description"": ""GameObject name, hierarchy path or instance id."" },
    ""searchMethod"": { ""type"": ""string"" },
    ""shapeType"": { ""type"": ""string"", ""description"": ""Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism."" },
    ""name"": { ""type"": ""string"" },
    ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""rotation"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""size"": {},
    ""width"": { ""type"": ""number"" },
    ""height"": { ""type"": ""number"" },
    ""depth"": { ""type"": ""number"" },
    ""radius"": { ""type"": ""number"" },
    ""points"": { ""type"": ""array"" },
    ""extrudeHeight"": { ""type"": ""number"" },
    ""flipNormals"": { ""type"": ""boolean"" },
    ""faceIndices"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""edgeIndices"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""edges"": { ""type"": ""array"" },
    ""edgeA"": { ""type"": ""object"" },
    ""edgeB"": { ""type"": ""object"" },
    ""vertexIndices"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""targets"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""distance"": { ""type"": ""number"" },
    ""amount"": { ""type"": ""number"" },
    ""method"": { ""type"": ""string"" },
    ""offset"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""point"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""faceIndex"": { ""type"": ""integer"" },
    ""count"": { ""type"": ""integer"" },
    ""color"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""materialPath"": { ""type"": ""string"" },
    ""smoothingGroup"": { ""type"": ""integer"" },
    ""angleThreshold"": { ""type"": ""number"" },
    ""direction"": { ""type"": ""string"" },
    ""include"": { ""type"": ""string"", ""enum"": [""summary"", ""faces"", ""edges"", ""all""], ""default"": ""summary"" },
    ""maxItems"": { ""type"": ""integer"", ""default"": 64 }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            string action = ResolveAction(context, "ping").Trim().ToLowerInvariant().Replace('_', '-');

            CucliCommandResult missing = CucliOptional.RequirePackage(PackageName, "ProBuilder 网格编辑");
            if (missing != null) return missing;

            switch (action)
            {
                case "ping": return ProBuilderSupport.Ping();
                case "create-shape": return ProBuilderShapes.Create(context);
                case "create-poly-shape": return ProBuilderShapes.CreatePoly(context);
                case "get-mesh-info": return ProBuilderSupport.GetMeshInfo(context);
                case "convert-to-probuilder": return ProBuilderSupport.ConvertToProBuilder(context);
                case "validate-mesh": return ProBuilderSupport.ValidateMesh(context);
                case "repair-mesh": return ProBuilderSupport.RepairMesh(context);
                case "center-pivot": return ProBuilderSupport.CenterPivot(context);
                case "freeze-transform": return ProBuilderSupport.FreezeTransform(context);
                case "set-pivot": return ProBuilderSupport.SetPivot(context);
                case "set-face-material": return ProBuilderSupport.SetFaceMaterial(context);
                case "set-face-color": return ProBuilderSupport.SetFaceColor(context);
                case "set-smoothing": return ProBuilderSupport.SetSmoothing(context);
                case "auto-smooth": return ProBuilderSupport.AutoSmooth(context);
                case "select-faces": return ProBuilderSupport.SelectFaces(context);
                case "set-face-uvs": return ProBuilderSupport.SetFaceUvs(context);
                case "extrude-faces": return ProBuilderMeshOps.ExtrudeFaces(context);
                case "extrude-edges": return ProBuilderMeshOps.ExtrudeEdges(context);
                case "bevel-edges": return ProBuilderMeshOps.BevelEdges(context);
                case "subdivide": return ProBuilderMeshOps.Subdivide(context);
                case "delete-faces": return ProBuilderMeshOps.DeleteFaces(context);
                case "flip-normals": return ProBuilderMeshOps.FlipNormals(context);
                case "merge-faces": return ProBuilderMeshOps.MergeFaces(context);
                case "detach-faces": return ProBuilderMeshOps.DetachFaces(context);
                case "duplicate-and-flip": return ProBuilderMeshOps.DuplicateAndFlip(context);
                case "bridge-edges": return ProBuilderMeshOps.BridgeEdges(context);
                case "connect-elements": return ProBuilderMeshOps.ConnectElements(context);
                case "create-polygon": return ProBuilderMeshOps.CreatePolygon(context);
                case "combine-meshes": return ProBuilderMeshOps.CombineMeshes(context, false);
                case "merge-objects": return ProBuilderMeshOps.CombineMeshes(context, true);
                case "merge-vertices": return ProBuilderMeshOps.MergeVertices(context);
                case "weld-vertices": return ProBuilderMeshOps.WeldVertices(context);
                case "split-vertices": return ProBuilderMeshOps.SplitVertices(context);
                case "move-vertices": return ProBuilderMeshOps.MoveVertices(context);
                case "insert-vertex": return ProBuilderMeshOps.InsertVertex(context);
                case "append-vertices-to-edge": return ProBuilderMeshOps.AppendVerticesToEdge(context);
                default: return CucliCommandResult.Unsupported(action, ActionNames());
            }
        }
    }

    /// <summary>
    /// The reflection surface into ProBuilder. Every type and member lookup lives here so the rest of
    /// the group can read like ordinary code, and so a version mismatch produces one clear message
    /// instead of a scattered NullReferenceException.
    /// </summary>
    internal static class ProBuilderSupport
    {
        private const string Runtime = "Unity.ProBuilder";
        private const string EditorAssembly = "Unity.ProBuilder.Editor";

        internal static Type Mesh => CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.ProBuilderMesh");
        internal static Type ShapeGenerator => CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.ShapeGenerator");
        internal static Type ShapeType => CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.ShapeType");
        internal static Type FaceType => CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.Face");
        internal static Type EdgeType => CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.Edge");
        internal static Type PivotLocation => CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.PivotLocation");
        internal static Type ExtrudeMethod => CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.ExtrudeMethod");
        internal static Type Smoothing => CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.Smoothing");
        internal static Type EditorMeshUtility => CucliOptional.FindType(EditorAssembly, "UnityEditor.ProBuilder.EditorMeshUtility");

        internal static Type MeshOperation(string nested)
        {
            return CucliOptional.FindType(Runtime, "UnityEngine.ProBuilder.MeshOperations." + nested);
        }

        internal static bool Ready
        {
            get { return Mesh != null && ShapeGenerator != null && ShapeType != null; }
        }

        internal static CucliCommandResult Ping()
        {
            var missing = new JArray();
            foreach (var pair in new[]
            {
                new KeyValuePair<string, Type>("ProBuilderMesh", Mesh),
                new KeyValuePair<string, Type>("ShapeGenerator", ShapeGenerator),
                new KeyValuePair<string, Type>("ShapeType", ShapeType),
                new KeyValuePair<string, Type>("Face", FaceType),
                new KeyValuePair<string, Type>("Edge", EdgeType),
                new KeyValuePair<string, Type>("PivotLocation", PivotLocation),
                new KeyValuePair<string, Type>("ExtrudeMethod", ExtrudeMethod),
                new KeyValuePair<string, Type>("Smoothing", Smoothing),
                new KeyValuePair<string, Type>("ExtrudeElements", MeshOperation("ExtrudeElements")),
                new KeyValuePair<string, Type>("ElementSelection", MeshOperation("ElementSelection")),
                new KeyValuePair<string, Type>("SurfaceTopology", MeshOperation("SurfaceTopology")),
                new KeyValuePair<string, Type>("VertexEditing", MeshOperation("VertexEditing")),
                new KeyValuePair<string, Type>("MeshValidation", MeshOperation("MeshValidation")),
                new KeyValuePair<string, Type>("CombineMeshes", MeshOperation("CombineMeshes")),
                new KeyValuePair<string, Type>("EditorMeshUtility", EditorMeshUtility)
            })
            {
                if (pair.Value == null) missing.Add(pair.Key);
            }

            return CucliCommandResult.Ok(new
            {
                tool = "probuilder",
                packageVersion = CucliOptional.PackageVersion(ProBuilderCommand.PackageName),
                available = Ready,
                missingTypes = missing,
                shapeTypes = ShapeType == null ? Array.Empty<string>() : Enum.GetNames(ShapeType)
            }, Ready ? "ProBuilder 可用。" : "ProBuilder 包在，但关键类型解析失败。");
        }

        // ------------------------------------------------------------------ object plumbing

        internal static Component RequireMesh(CucliArgs args, out string error)
        {
            error = null;
            GameObject target = CucliObjectResolver.ResolveGameObject(args, out error);
            if (error != null) return null;

            Component mesh = target.GetComponent(Mesh);
            if (mesh == null)
            {
                error = "GameObject '" + target.name + "' does not have a ProBuilderMesh component.";
                return null;
            }

            return mesh;
        }

        internal static bool TryMesh(CucliCommandContext context, out Component mesh, out CucliCommandResult failure)
        {
            string error;
            mesh = RequireMesh(context.Input, out error);
            if (error == null)
            {
                failure = null;
                return true;
            }

            failure = CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);
            return false;
        }

        internal static object Get(object target, string member)
        {
            if (target == null) return null;
            return CucliReflection.Get(target, member, out _);
        }

        internal static bool Set(object target, string member, object value)
        {
            if (target == null) return false;
            JToken token = value == null ? JValue.CreateNull() : CucliJson.ToToken(value);
            return CucliReflection.TrySetMember(target, member, token, out _);
        }

        internal static int IntOf(object target, string member)
        {
            object value = Get(target, member);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        internal static IList ListOf(object target, string member)
        {
            return Get(target, member) as IList;
        }

        internal static object[] Faces(object mesh)
        {
            IList faces = ListOf(mesh, "faces");
            return faces == null ? Array.Empty<object>() : faces.Cast<object>().ToArray();
        }

        internal static object[] Edges(object mesh)
        {
            IList edges = ListOf(mesh, "edges");
            return edges == null ? Array.Empty<object>() : edges.Cast<object>().ToArray();
        }

        /// <summary>Resolves a face selection; an empty or absent list means "every face".</summary>
        internal static bool ResolveFaces(
            CucliArgs args,
            object mesh,
            bool required,
            out object[] faces,
            out string error)
        {
            error = null;
            object[] all = Faces(mesh);
            int[] requested = args.GetIntList("faceIndices");
            if (requested.Length == 0) requested = args.GetIntList("face_indices");

            if (requested.Length == 0 && required)
            {
                faces = Array.Empty<object>();
                error = "faceIndices is required (it would be ambiguous to guess which faces to change).";
                return false;
            }

            if (requested.Length == 0)
            {
                faces = all;
                return true;
            }

            var selected = new List<object>(requested.Length);
            foreach (int index in requested)
            {
                if (index < 0 || index >= all.Length)
                {
                    faces = Array.Empty<object>();
                    error = "Face index " + index + " out of range (0-" + (all.Length - 1) + ").";
                    return false;
                }

                selected.Add(all[index]);
            }

            faces = selected.ToArray();
            return true;
        }

        internal static object MakeEdge(int a, int b)
        {
            if (EdgeType == null) return null;
            return Activator.CreateInstance(EdgeType, a, b);
        }

        internal static bool ReadEdge(JToken token, out int a, out int b, out string error)
        {
            a = 0;
            b = 0;
            error = null;
            var obj = token as JObject;
            if (obj == null)
            {
                error = "expected an object with 'a' and 'b' vertex indices.";
                return false;
            }

            if (obj["a"] == null || obj["b"] == null)
            {
                error = "both 'a' and 'b' are required.";
                return false;
            }

            a = obj["a"].Value<int>();
            b = obj["b"].Value<int>();
            return true;
        }

        /// <summary>
        /// Reads the two spellings of an edge list: index pairs into the mesh edge array, or explicit
        /// vertex pairs. The MCP surface accepts either and so does this.
        /// </summary>
        internal static bool ResolveEdges(
            CucliArgs args,
            object mesh,
            out object[] edges,
            out string error)
        {
            error = null;
            object[] all = Edges(mesh);
            int[] indices = args.GetIntList("edgeIndices");
            if (indices.Length == 0) indices = args.GetIntList("edge_indices");

            if (indices.Length > 0)
            {
                var selected = new List<object>(indices.Length);
                foreach (int index in indices)
                {
                    if (index < 0 || index >= all.Length)
                    {
                        edges = Array.Empty<object>();
                        error = "Edge index " + index + " out of range (0-" + (all.Length - 1) + ").";
                        return false;
                    }

                    selected.Add(all[index]);
                }

                edges = selected.ToArray();
                return true;
            }

            JArray explicitEdges = args.GetArray("edges");
            if (explicitEdges != null && explicitEdges.Count > 0 && EdgeType != null)
            {
                var built = new List<object>(explicitEdges.Count);
                foreach (JToken item in explicitEdges)
                {
                    int a;
                    int b;
                    string edgeError;
                    if (!ReadEdge(item, out a, out b, out edgeError))
                    {
                        edges = Array.Empty<object>();
                        error = "edges: " + edgeError;
                        return false;
                    }

                    built.Add(MakeEdge(a, b));
                }

                edges = built.ToArray();
                return true;
            }

            edges = Array.Empty<object>();
            error = "edgeIndices or edges parameter is required.";
            return false;
        }

        internal static void Refresh(object mesh)
        {
            // Refresh() rebuilds the Unity Mesh from the ProBuilder data; skipping it would leave the
            // scene showing stale geometry and make every subsequent count read wrong.
            ProBuilderReflect.Invoke(Mesh, "Refresh", new[] { mesh }, out _, out _);
            var component = mesh as Component;
            if (component != null) EditorUtility.SetDirty(component);
        }

        // Fully qualified on purpose: `Mesh` in this class is the ProBuilderMesh Type property.
        internal static UnityEngine.Mesh ToMesh(object mesh)
        {
            object result;
            string error;
            return ProBuilderReflect.Invoke(Mesh, "ToMesh", new[] { mesh }, out result, out error)
                ? result as Mesh
                : null;
        }

        // ------------------------------------------------------------------ info

        internal static CucliCommandResult GetMeshInfo(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            string include = (context.Input.GetString("include") ?? "summary").Trim().ToLowerInvariant();
            int maxItems = Mathf.Clamp(context.Input.GetInt("maxItems", 64), 1, 1000);

            var data = new JObject
            {
                ["gameObjectName"] = mesh.gameObject.name,
                ["instanceId"] = mesh.gameObject.GetInstanceID(),
                ["faceCount"] = IntOf(mesh, "faceCount"),
                ["vertexCount"] = IntOf(mesh, "vertexCount")
            };

            UnityEngine.Mesh unityMesh = ToMesh(mesh);
            if (unityMesh != null)
            {
                data["bounds"] = new JObject
                {
                    ["center"] = CucliDescribe.Value(unityMesh.bounds.center, null, 0),
                    ["size"] = CucliDescribe.Value(unityMesh.bounds.size, null, 0)
                };

                var meshRenderer = mesh.gameObject.GetComponent<MeshRenderer>();
                var materials = new JArray();
                if (meshRenderer != null)
                {
                    foreach (Material material in meshRenderer.sharedMaterials)
                    {
                        materials.Add(material == null ? null : material.name);
                    }
                }

                data["materials"] = materials;
            }

            object[] faces = Faces(mesh);
            if ((include == "faces" || include == "all") && faces.Length > 0)
            {
                var list = new JArray();
                int limit = Math.Min(faces.Length, 100);
                for (int index = 0; index < limit; index++)
                {
                    object face = faces[index];
                    list.Add(new JObject
                    {
                        ["index"] = index,
                        ["smoothingGroup"] = IntOf(face, "smoothingGroup"),
                        ["manualUV"] = CucliJson.ToToken(Get(face, "manualUV")),
                        ["submeshIndex"] = IntOf(face, "submeshIndex")
                    });
                }

                data["faces"] = list;
                if (faces.Length > limit) data["truncated"] = faces.Length - limit;
            }

            object[] edges = Edges(mesh);
            if ((include == "edges" || include == "all") && edges.Length > 0)
            {
                var list = new JArray();
                int limit = Math.Min(edges.Length, 200);
                for (int index = 0; index < limit; index++)
                {
                    object edge = edges[index];
                    list.Add(new JObject
                    {
                        ["index"] = index,
                        ["vertexA"] = IntOf(edge, "a"),
                        ["vertexB"] = IntOf(edge, "b")
                    });
                }

                data["edges"] = list;
                data["edgeCount"] = edges.Length;
                if (edges.Length > limit) data["edgesTruncated"] = edges.Length - limit;
            }

            return CucliCommandResult.Ok(data);
        }

        // ------------------------------------------------------------------ convert / validate / repair

        internal static CucliCommandResult ConvertToProBuilder(CucliCommandContext context)
        {
            string error;
            GameObject target = CucliObjectResolver.ResolveGameObject(context.Input, out error);
            if (error != null) return CucliCommandResult.Fail(CucliErrors.TargetNotFound, error);

            Type importer = MeshOperation("MeshImporter");
            if (importer == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "MeshOperations.MeshImporter was not found; this ProBuilder version exposes a different conversion API.");
            }

            var meshFilter = target.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.TargetNotFound, target.name + " has no MeshFilter with a mesh to convert.");
            }

            Undo.RegisterFullObjectHierarchyUndo(target, "cucli probuilder convert");
            object instance;
            try
            {
                instance = Activator.CreateInstance(importer);
            }
            catch (Exception exception)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    "Could not construct MeshImporter: " + exception.GetBaseException().Message);
            }

            // Import() has had both a no-argument and a Mesh argument form across versions, so both
            // are attempted before giving up.
            object result;
            string invokeError;
            bool imported = ProBuilderReflect.Invoke(importer, "Import", new[] { instance }, out result, out invokeError)
                || ProBuilderReflect.Invoke(
                    importer, "Import", new[] { instance, meshFilter.sharedMesh }, out result, out invokeError);

            if (!imported)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "MeshImporter.Import could not be invoked: " + invokeError);
            }

            Component mesh = target.GetComponent(Mesh);
            if (mesh == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "Import completed but no ProBuilderMesh appeared on " + target.name + ".");
            }

            Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                gameObjectName = target.name,
                faceCount = IntOf(mesh, "faceCount"),
                vertexCount = IntOf(mesh, "vertexCount")
            }, "已转换为 ProBuilder 网格。");
        }

        internal static CucliCommandResult ValidateMesh(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            UnityEngine.Mesh unityMesh = ToMesh(mesh);
            var issues = new List<string>();
            int degenerate = 0;
            int unused = 0;

            if (unityMesh != null)
            {
                Vector3[] vertices = unityMesh.vertices;
                int[] triangles = unityMesh.triangles;
                for (int index = 0; index + 2 < triangles.Length; index += 3)
                {
                    int a = triangles[index];
                    int b = triangles[index + 1];
                    int c = triangles[index + 2];
                    if (a == b || b == c || a == c)
                    {
                        degenerate++;
                        continue;
                    }

                    Vector3 cross = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                    if (cross.sqrMagnitude < 1e-12f) degenerate++;
                }

                if (degenerate > 0)
                {
                    issues.Add(degenerate + " degenerate triangle(s).");
                }

                var used = new HashSet<int>(triangles);
                unused = Math.Max(0, vertices.Length - used.Count);
                if (unused > 0) issues.Add(unused + " unused vertex/vertices.");
            }
            else
            {
                issues.Add("ToMesh() returned null, so triangle analysis was skipped.");
            }

            return CucliCommandResult.Ok(new
            {
                healthy = issues.Count == 0,
                faceCount = IntOf(mesh, "faceCount"),
                vertexCount = IntOf(mesh, "vertexCount"),
                degenerateTriangles = degenerate,
                unusedVertices = unused,
                issues = issues.ToArray()
            }, issues.Count == 0 ? "网格看起来正常。" : "发现 " + issues.Count + " 类问题。");
        }

        internal static CucliCommandResult RepairMesh(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            Type validation = MeshOperation("MeshValidation");
            if (validation == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "MeshOperations.MeshValidation was not found in this ProBuilder version.");
            }

            int before = IntOf(mesh, "faceCount");
            object result;
            string error;
            if (!ProBuilderReflect.Invoke(validation, "RemoveDegenerateTriangles", new[] { mesh }, out result, out error))
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "RemoveDegenerateTriangles could not be invoked: " + error);
            }

            Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                degenerateTrianglesRemoved = Math.Max(0, before - IntOf(mesh, "faceCount")),
                faceCount = IntOf(mesh, "faceCount"),
                vertexCount = IntOf(mesh, "vertexCount")
            });
        }

        // ------------------------------------------------------------------ transforms

        internal static CucliCommandResult CenterPivot(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            UnityEngine.Mesh unityMesh = ToMesh(mesh);
            if (unityMesh == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ToMesh() returned null.");
            }

            Vector3 localCenter = unityMesh.bounds.center;
            if (localCenter.sqrMagnitude < 1e-10f)
            {
                return CucliCommandResult.Ok(new
                {
                    offset = CucliDescribe.Value(Vector3.zero, null, 0),
                    newPosition = CucliDescribe.Value(mesh.transform.position, null, 0)
                }, "轴心已经居中。");
            }

            IList positions = ListOf(mesh, "positions");
            if (positions == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ProBuilderMesh.positions is unavailable.");
            }

            Undo.RecordObject(mesh, "cucli probuilder center-pivot");
            var moved = new List<Vector3>(positions.Count);
            foreach (object item in positions) moved.Add((Vector3)item - localCenter);

            if (!SetPositions(mesh, moved))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "SetPositions could not be invoked.");
            }

            mesh.transform.position += mesh.transform.TransformVector(localCenter);
            Refresh(mesh);

            return CucliCommandResult.Ok(new
            {
                offset = CucliDescribe.Value(localCenter, null, 0),
                newPosition = CucliDescribe.Value(mesh.transform.position, null, 0)
            });
        }

        internal static CucliCommandResult FreezeTransform(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            IList positions = ListOf(mesh, "positions");
            if (positions == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ProBuilderMesh.positions is unavailable.");
            }

            Undo.RecordObject(mesh, "cucli probuilder freeze-transform");
            Undo.RecordObject(mesh.transform, "cucli probuilder freeze-transform");

            var baked = new List<Vector3>(positions.Count);
            foreach (object item in positions) baked.Add(mesh.transform.TransformPoint((Vector3)item));

            if (!SetPositions(mesh, baked))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "SetPositions could not be invoked.");
            }

            mesh.transform.position = Vector3.zero;
            mesh.transform.rotation = Quaternion.identity;
            mesh.transform.localScale = Vector3.one;
            Refresh(mesh);

            return CucliCommandResult.Ok(new { vertexCount = baked.Count }, "已把变换烘进顶点。");
        }

        internal static CucliCommandResult SetPivot(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            JToken token = context.Input.Token("position")
                ?? context.Input.Token("worldPosition")
                ?? context.Input.Token("world_position");

            Vector3 world;
            string error;
            if (!PhysicsCommand.ReadVector3(token, out world, out error))
            {
                return CucliCommandResult.Missing(
                    "position", "position parameter is required ([x,y,z] in world space).");
            }

            IList positions = ListOf(mesh, "positions");
            if (positions == null)
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "ProBuilderMesh.positions is unavailable.");
            }

            // Moving the pivot without moving the geometry: the vertices shift by the inverse of the
            // transform delta while the object moves to the requested point.
            Vector3 delta = world - mesh.transform.position;
            Vector3 localDelta = mesh.transform.InverseTransformVector(delta);

            Undo.RecordObject(mesh, "cucli probuilder set-pivot");
            Undo.RecordObject(mesh.transform, "cucli probuilder set-pivot");

            var shifted = new List<Vector3>(positions.Count);
            foreach (object item in positions) shifted.Add((Vector3)item - localDelta);

            if (!SetPositions(mesh, shifted))
            {
                return CucliCommandResult.Fail(CucliErrors.CommandFailed, "SetPositions could not be invoked.");
            }

            mesh.transform.position = world;
            Refresh(mesh);

            return CucliCommandResult.Ok(new { position = CucliDescribe.Value(world, null, 0) });
        }

        internal static bool SetPositions(object mesh, IList<Vector3> positions)
        {
            object result;
            string error;
            return ProBuilderReflect.Invoke(
                Mesh, "SetPositions", new object[] { mesh, positions }, out result, out error);
        }

        // ------------------------------------------------------------------ faces

        internal static CucliCommandResult SetFaceMaterial(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            string path = context.Input.GetString("materialPath") ?? context.Input.GetString("material_path");
            if (string.IsNullOrWhiteSpace(path))
            {
                return CucliCommandResult.Missing("materialPath");
            }

            string normalized = CucliAssets.NormalizeAssetPath(path);
            Material material = CucliAssets.Load<Material>(normalized);
            if (material == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.AssetNotFound, "Material not found at path: " + normalized);
            }

            object[] faces;
            string error;
            if (!ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            Undo.RecordObject(mesh, "cucli probuilder set-face-material");
            int applied = 0;
            foreach (object face in faces)
            {
                object result;
                string invokeError;
                if (ProBuilderReflect.Invoke(
                        Mesh, "SetMaterial", new[] { mesh, face, (object)material }, out result, out invokeError))
                {
                    applied++;
                }
                else if (applied == 0)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "ProBuilderMesh.SetMaterial could not be invoked: " + invokeError);
                }
            }

            Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                facesModified = applied,
                materialPath = normalized
            });
        }

        internal static CucliCommandResult SetFaceColor(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            Color color = context.Input.GetColor("color", new Color(1f, 1f, 1f, 1f));

            object[] faces;
            string error;
            if (!ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            Undo.RecordObject(mesh, "cucli probuilder set-face-color");
            int applied = 0;
            foreach (object face in faces)
            {
                object result;
                string invokeError;
                if (ProBuilderReflect.Invoke(
                        Mesh, "SetFaceColor", new[] { mesh, face, (object)color }, out result, out invokeError))
                {
                    applied++;
                }
                else if (applied == 0)
                {
                    return CucliCommandResult.Fail(
                        CucliErrors.CommandFailed, "ProBuilderMesh.SetFaceColor could not be invoked: " + invokeError);
                }
            }

            Refresh(mesh);
            return CucliCommandResult.Ok(new
            {
                facesModified = applied,
                color = CucliDescribe.Value(color, null, 0)
            });
        }

        internal static CucliCommandResult SetSmoothing(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            int group = context.Input.GetInt("smoothingGroup", 0);
            object[] faces;
            string error;
            if (!ResolveFaces(context.Input, mesh, true, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            Undo.RecordObject(mesh, "cucli probuilder set-smoothing");
            foreach (object face in faces) Set(face, "smoothingGroup", group);
            Refresh(mesh);

            return CucliCommandResult.Ok(new { facesModified = faces.Length, smoothingGroup = group });
        }

        internal static CucliCommandResult AutoSmooth(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            Type smoothing = Smoothing;
            if (smoothing == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "UnityEngine.ProBuilder.Smoothing was not found.");
            }

            float threshold = context.Input.GetFloat("angleThreshold", 30f);
            object[] faces;
            string error;
            if (!ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            IList faceList = BuildFaceList(faces);
            object result;
            string invokeError;
            bool invoked = ProBuilderReflect.Invoke(
                    smoothing, "ApplySmoothingGroups",
                    new object[] { mesh, faceList, threshold }, out result, out invokeError)
                || ProBuilderReflect.Invoke(
                    smoothing, "ApplySmoothingGroups",
                    new object[] { mesh, faceList, threshold, Vector3.one }, out result, out invokeError);

            if (!invoked)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "Smoothing.ApplySmoothingGroups could not be invoked: " + invokeError);
            }

            Refresh(mesh);
            return CucliCommandResult.Ok(new { angleThreshold = threshold, faceCount = IntOf(mesh, "faceCount") });
        }

        internal static IList BuildFaceList(object[] faces)
        {
            if (FaceType == null) return faces.ToList();
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(FaceType));
            foreach (object face in faces) list.Add(face);
            return list;
        }

        internal static IList BuildEdgeList(object[] edges)
        {
            if (EdgeType == null) return edges.ToList();
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(EdgeType));
            foreach (object edge in edges) list.Add(edge);
            return list;
        }

        /// <summary>A strongly typed List&lt;ProBuilderMesh&gt;, which is what CombineMeshes expects.</summary>
        internal static IList BuildComponentList(IEnumerable<Component> components)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Mesh));
            foreach (Component component in components) list.Add(component);
            return list;
        }

        internal static CucliCommandResult SelectFaces(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            Type selection = MeshOperation("ElementSelection");
            if (selection == null)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "MeshOperations.ElementSelection was not found.");
            }

            object result = null;
            string error = null;
            bool invoked = false;

            string direction = context.Input.GetString("direction");
            int[] growFrom = context.Input.GetIntList("growFrom");
            if (growFrom.Length == 0) growFrom = context.Input.GetIntList("grow_from");
            int[] floodFrom = context.Input.GetIntList("floodFrom");
            if (floodFrom.Length == 0) floodFrom = context.Input.GetIntList("flood_from");
            int[] loopFrom = context.Input.GetIntList("loopFrom");
            if (loopFrom.Length == 0) loopFrom = context.Input.GetIntList("loop_from");

            if (!string.IsNullOrWhiteSpace(direction))
            {
                Vector3 vector;
                switch (direction.Trim().ToLowerInvariant())
                {
                    case "up":
                    case "top": vector = Vector3.up; break;
                    case "down":
                    case "bottom": vector = Vector3.down; break;
                    case "forward":
                    case "front": vector = Vector3.forward; break;
                    case "back":
                    case "backward": vector = Vector3.back; break;
                    case "left": vector = Vector3.left; break;
                    case "right": vector = Vector3.right; break;
                    default:
                        return CucliCommandResult.Invalid(
                            "direction", "'" + direction + "' is not one of up, down, forward, back, left, right.");
                }

                float tolerance = context.Input.GetFloat("tolerance", 0.7f);
                invoked = ProBuilderReflect.Invoke(
                    selection, "GetFaceByDirection", new object[] { mesh, vector, tolerance }, out result, out error);
            }
            else if (growFrom.Length > 0)
            {
                float angle = context.Input.GetFloat("growAngle", -1f);
                invoked = CucliCommandResult_InvokeSelect(selection, mesh, "GrowSelection", growFrom, angle, out result, out error);
            }
            else if (floodFrom.Length > 0)
            {
                float angle = context.Input.GetFloat("floodAngle", 15f);
                invoked = CucliCommandResult_InvokeSelect(selection, mesh, "FloodSelection", floodFrom, angle, out result, out error);
            }
            else if (loopFrom.Length > 0)
            {
                bool ring = context.Input.GetBool("ring", false);
                invoked = ProBuilderReflect.Invoke(
                    selection, "GetFaceLoop",
                    new object[] { mesh, BuildFaceListFromIndices(mesh, loopFrom), ring },
                    out result, out error);
            }
            else
            {
                return CucliCommandResult.Missing(
                    "direction", "Provide direction, growFrom, floodFrom or loopFrom.");
            }

            if (!invoked)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed, "ElementSelection method could not be invoked: " + error);
            }

            var indices = new List<int>();
            var enumerable = result as IEnumerable;
            if (enumerable != null)
            {
                foreach (object item in enumerable)
                {
                    object[] items = item as object[];
                    if (items != null)
                    {
                        foreach (object inner in items) indices.Add(IndexOfFace(mesh, inner));
                        continue;
                    }

                    indices.Add(IndexOfFace(mesh, item));
                }
            }

            indices = indices.Where(index => index >= 0).Distinct().OrderBy(index => index).ToList();
            return CucliCommandResult.Ok(new
            {
                faceIndices = indices,
                count = indices.Count,
                totalFaces = IntOf(mesh, "faceCount")
            });
        }

        private static bool CucliCommandResult_InvokeSelect(
            Type selection,
            object mesh,
            string method,
            int[] seeds,
            float angle,
            out object result,
            out string error)
        {
            IList faceList = BuildFaceListFromIndices(mesh, seeds);
            return ProBuilderReflect.Invoke(
                    selection, method, new object[] { mesh, faceList, angle }, out result, out error)
                || ProBuilderReflect.Invoke(
                    selection, method, new object[] { mesh, faceList }, out result, out error);
        }

        internal static IList BuildFaceListFromIndices(object mesh, int[] indices)
        {
            object[] all = Faces(mesh);
            var selected = new List<object>();
            foreach (int index in indices)
            {
                if (index >= 0 && index < all.Length) selected.Add(all[index]);
            }

            return BuildFaceList(selected.ToArray());
        }

        internal static int IndexOfFace(object mesh, object face)
        {
            if (face == null) return -1;
            object[] all = Faces(mesh);
            for (int index = 0; index < all.Length; index++)
            {
                if (ReferenceEquals(all[index], face)) return index;
            }

            return -1;
        }

        internal static CucliCommandResult SetFaceUvs(CucliCommandContext context)
        {
            Component mesh;
            CucliCommandResult failure;
            if (!TryMesh(context, out mesh, out failure)) return failure;

            object[] faces;
            string error;
            if (!ResolveFaces(context.Input, mesh, false, out faces, out error))
            {
                return CucliCommandResult.Invalid("faceIndices", error);
            }

            JToken scaleToken = context.Input.Token("scale");
            JToken offsetToken = context.Input.Token("offset");
            float rotation = context.Input.GetFloat("rotation", 0f);
            bool flipU = context.Input.GetBool("flipU", false);
            bool flipV = context.Input.GetBool("flipV", false);

            // Face.uv is a struct; it is read, mutated and written back because reflection cannot take
            // a reference to a boxed struct's field.
            int modified = 0;
            foreach (object face in faces)
            {
                object uv = Get(face, "uv");
                if (uv == null) continue;

                bool touched = false;
                if (scaleToken != null && scaleToken.Type != JTokenType.Null)
                {
                    Vector2 scale = ReadVector2Loose(scaleToken);
                    if (Set(uv, "scale", scale)) touched = true;
                }

                if (offsetToken != null && offsetToken.Type != JTokenType.Null)
                {
                    Vector2 offset = ReadVector2Loose(offsetToken);
                    if (Set(uv, "offset", offset)) touched = true;
                }

                if (rotation != 0f && Set(uv, "rotation", rotation)) touched = true;
                if (flipU && Set(uv, "flipU", true)) touched = true;
                if (flipV && Set(uv, "flipV", true)) touched = true;

                if (touched && Set(face, "uv", uv))
                {
                    Set(face, "manualUV", true);
                    modified++;
                }
            }

            object ignored;
            string refreshError;
            ProBuilderReflect.Invoke(
                Mesh, "RefreshUV", new object[] { mesh, BuildFaceList(faces) }, out ignored, out refreshError);
            Refresh(mesh);

            if (modified == 0)
            {
                context.Warn("No UV value changed; this ProBuilder version may name the Face.uv members differently.");
            }

            return CucliCommandResult.Ok(new { facesModified = modified });
        }

        private static Vector2 ReadVector2Loose(JToken token)
        {
            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                if (array.Count == 1) return new Vector2(array[0].Value<float>(), array[0].Value<float>());
                if (array.Count >= 2) return new Vector2(array[0].Value<float>(), array[1].Value<float>());
            }

            float scalar = token.Value<float>();
            return new Vector2(scalar, scalar);
        }
    }
}
