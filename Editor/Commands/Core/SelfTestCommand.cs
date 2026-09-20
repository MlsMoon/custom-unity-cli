using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// <c>cucli selftest ...</c> - the tool testing itself.
    ///
    /// Three actions, each answering a different question:
    ///
    /// <list type="bullet">
    /// <item><c>echo</c> - did my payload survive the CLI, the file queue and the worker byte for
    /// byte? Run it if a command behaves as though a parameter went missing.</item>
    /// <item><c>core</c> - are the shared helpers correct? Argument reading, JSON conversion, object
    /// description, paging, reflection, SerializedObject writes and path handling are what every
    /// one of the command groups is built on, so a defect there shows up as dozens of unrelated
    /// command bugs.</item>
    /// <item><c>surface</c> - a machine-readable plan over every registered action, classifying it as
    /// read-only, mutating or destructive. The external harness in <c>Tools/cucli-selftest.ps1</c>
    /// uses it to drive the CLI, and it is also how the project can see, honestly, how much of the
    /// surface is safe to exercise automatically.</item>
    /// </list>
    ///
    /// Nothing here writes to the project unless <c>mutate: true</c> is passed, and even then the only
    /// artifacts are a temporary folder and files under the already-ignored runtime directory.
    /// </summary>
    internal sealed class SelfTestCommand : CucliCommand
    {
        public override string Name => "selftest";

        public override string Summary =>
            "Test the tool itself: echo a payload back, verify the shared helpers, and classify every registered action.";

        // 'core' is the default action, so it must be declared first - the contract requires the
        // declared order to lead with the default, and 'cucli selftest' with no action runs it.
        public override CucliActionInfo[] Actions => Declare(
            "core", "Run assertions over the shared helper layer (args, values, describe, paging, reflection, serialized, assets, paths, json).",
            "echo", "Return the payload unchanged plus a token, to prove end-to-end fidelity through the queue.",
            "surface", "Classify every registered action as read-only, mutating or destructive, with its safety metadata.",
            "sample", "Produce a compact overview: group and action counts, helper check summary, optional package availability.");

        public override bool RequiresEditor => true;

        public override CucliMode RequiredMode => CucliMode.Any;

        public override bool MutatesProject => false;

        public override string[] Effects => new[] { "editor.state.read", "filesystem.write" };

        public override string ParametersSchemaJson => @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""echo"", ""core"", ""surface"", ""sample""],
      ""default"": ""core""
    },
    ""token"": { ""type"": ""string"", ""description"": ""action=echo: arbitrary token echoed back verbatim."" },
    ""mutate"": {
      ""type"": ""boolean"", ""default"": false,
      ""description"": ""Allow the few checks that must write (a temporary folder, runtime scratch files).""
    },
    ""group"": { ""type"": ""string"", ""description"": ""action=surface: restrict the plan to one group."" },
    ""verbose"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Include a detail line for every passing check."" }
  }
}";

        public override CucliCommandResult Execute(CucliCommandContext context)
        {
            switch (ResolveAction(context, "core"))
            {
                case "echo": return Echo(context);
                case "core": return Core(context);
                case "surface": return Surface(context);
                case "sample": return Sample(context);
                default: return CucliCommandResult.Unsupported(ResolveAction(context, "core"), ActionNames());
            }
        }

        // ------------------------------------------------------------------ echo

        private static CucliCommandResult Echo(CucliCommandContext context)
        {
            // The payload is returned untouched so the caller can diff it against what it sent.
            // A round trip that mangles a nested object, an array of numbers or a non-ASCII string is
            // the hardest class of bug to see from the outside, and this makes it a one-line check.
            JObject echoed = (JObject)context.Args.DeepClone();
            return CucliCommandResult.Ok(new
            {
                echo = echoed,
                token = context.Input.GetString("token"),
                jobId = context.JobId,
                commandName = context.CommandName,
                argCount = context.Args.Count,
                utf8RoundTrip = "中文/emoji-\u4e2d\u6587-\ud83c\udfb1"
            }, "Payload echoed verbatim.");
        }

        // ------------------------------------------------------------------ core

        private static CucliCommandResult Core(CucliCommandContext context)
        {
            bool mutate = context.Input.GetBool("mutate", false);
            bool verbose = context.Input.GetBool("verbose", false);

            var checks = new List<JObject>();
            var performed = new List<string>();

            RunSelfTests(checks, performed, mutate);

            int passed = checks.Count(check => check.Value<bool>("ok"));
            int failed = checks.Count - passed;

            JArray reported = new JArray(verbose
                ? checks
                : checks.Where(check => !check.Value<bool>("ok")));

            var data = new JObject
            {
                ["total"] = checks.Count,
                ["passed"] = passed,
                ["failed"] = failed,
                ["ok"] = failed == 0,
                ["mutationsPerformed"] = new JArray(performed),
                ["checks"] = reported
            };

            if (failed > 0)
            {
                return CucliCommandResult.Fail(
                    CucliErrors.CommandFailed,
                    failed + " of " + checks.Count + " self-test checks failed.",
                    data);
            }

            return CucliCommandResult.Ok(
                data,
                checks.Count + " self-test checks passed" + (mutate ? " (with mutations)." : "."));
        }

        private static void RunSelfTests(List<JObject> checks, List<string> performed, bool mutate)
        {
            // ---------------------------------------------------------- args
            Check(checks, "args.bool", () =>
            {
                var args = Args(new JObject { ["a"] = "yes", ["b"] = "0", ["c"] = true, ["d"] = "off" });
                Assert(args.GetBool("a"), "yes should be true");
                Assert(!args.GetBool("b"), "0 should be false");
                Assert(args.GetBool("c"), "true should be true");
                Assert(!args.GetBool("d"), "off should be false");
            });

            Check(checks, "args.number", () =>
            {
                var args = Args(new JObject { ["i"] = 42, ["f"] = "1.5", ["l"] = 9007199254740993L });
                Assert(args.GetInt("i") == 42, "int");
                Assert(Math.Abs(args.GetFloat("f") - 1.5f) < 1e-6, "float from string");
                Assert(args.GetLong("l") == 9007199254740993L, "long precision");
            });

            Check(checks, "args.list", () =>
            {
                var args = Args(new JObject
                {
                    ["a"] = new JArray("x", "y"),
                    ["b"] = "p, q ; r"
                });
                string[] first = args.GetStringList("a");
                string[] second = args.GetStringList("b");
                Assert(first.Length == 2 && first[1] == "y", "array form");
                Assert(second.Length == 3 && second[1] == "q", "delimiter form: " + string.Join("|", second));
            });

            Check(checks, "args.vector", () =>
            {
                Assert(Args(new JObject { ["v"] = new JArray(1, 2, 3) }).GetVector3("v") == new Vector3(1, 2, 3), "array");
                Assert(Args(new JObject { ["v"] = "4,5,6" }).GetVector3("v") == new Vector3(4, 5, 6), "string");
                var byName = Args(new JObject { ["v"] = new JObject { ["x"] = 7, ["y"] = 8, ["z"] = 9 } });
                Assert(byName.GetVector3("v") == new Vector3(7, 8, 9), "object");
            });

            Check(checks, "args.color", () =>
            {
                Color fromHex = Args(new JObject { ["c"] = "#FF0000" }).GetColor("c");
                Assert(Math.Abs(fromHex.r - 1f) < 1e-3 && fromHex.g < 1e-3, "hex");
                Color fromArray = Args(new JObject { ["c"] = new JArray(0f, 1f, 0f, 0.5f) }).GetColor("c");
                Assert(fromArray.g > 0.99f && Math.Abs(fromArray.a - 0.5f) < 1e-3, "array with alpha");
            });

            Check(checks, "args.enum", () =>
            {
                var args = Args(new JObject { ["m"] = "Linear" });
                Assert(args.GetEnum("m", UnityEngine.Rendering.AmbientMode.Skybox) == UnityEngine.Rendering.AmbientMode.Flat
                    || args.GetEnum("m", UnityEngine.Rendering.AmbientMode.Skybox) != UnityEngine.Rendering.AmbientMode.Skybox,
                    "unknown enum name must fall back to the default, not throw");
            });

            Check(checks, "args.requireThrows", () =>
            {
                bool threw = false;
                try { Args(new JObject()).RequireString("missing"); }
                catch (CucliUsageException exception)
                {
                    threw = exception.Result != null && exception.Result.ErrorCode == CucliErrors.MissingParameter;
                }

                Assert(threw, "RequireString must throw a MISSING_PARAMETER CucliUsageException");
            });

            // ---------------------------------------------------------- values
            Check(checks, "values.numeric", () =>
            {
                Assert(Convert<int>(42L) == 42, "int");
                Assert(Math.Abs(Convert<float>("2.5") - 2.5f) < 1e-6, "float");
                Assert(Convert<byte>(200L) == 200, "byte");
            });

            Check(checks, "values.boolAndEnum", () =>
            {
                Assert(Convert<bool>("on"), "on");
                Assert(!Convert<bool>(0L), "0");
                Assert((TextAnchor)ConvertObject("MiddleCenter", typeof(TextAnchor)) == TextAnchor.MiddleCenter, "enum");
            });

            Check(checks, "values.structs", () =>
            {
                Assert((Vector3)ConvertObject(new JArray(1, 2, 3), typeof(Vector3)) == new Vector3(1, 2, 3), "Vector3");
                Assert((Rect)ConvertObject(new JArray(1, 2, 3, 4), typeof(Rect)) == new Rect(1, 2, 3, 4), "Rect");
                var bounds = (Bounds)ConvertObject(
                    new JObject { ["center"] = new JArray(0, 0, 0), ["size"] = new JArray(2, 2, 2) }, typeof(Bounds));
                Assert(bounds.size == new Vector3(2, 2, 2), "Bounds");
                var quaternion = (Quaternion)ConvertObject(new JArray(0, 90, 0), typeof(Quaternion));
                Assert(Math.Abs(quaternion.eulerAngles.y - 90f) < 0.01f, "Quaternion from euler");
            });

            Check(checks, "values.collections", () =>
            {
                var array = (int[])ConvertObject(new JArray(1, 2, 3), typeof(int[]));
                Assert(array.Length == 3 && array[2] == 3, "int[]");
                var list = (List<string>)ConvertObject(new JArray("a", "b"), typeof(List<string>));
                Assert(list.Count == 2 && list[0] == "a", "List<string>");
            });

            Check(checks, "values.rejectsBad", () =>
            {
                string error;
                CucliValues.Convert(new JArray(1, 2), typeof(Vector3), out error);
                Assert(!string.IsNullOrEmpty(error), "a 2-element array must not silently become a Vector3");
            });

            Check(checks, "values.friendlyName", () =>
            {
                Assert(CucliValues.FriendlyName(typeof(List<Vector3>)).Contains("Vector3"), "generic");
                Assert(CucliValues.FriendlyName(typeof(int[])).Contains("Int32"), "array");
            });

            // ---------------------------------------------------------- describe
            Check(checks, "describe.scalar", () =>
            {
                Assert(CucliDescribe.Value(42).Value<int>() == 42, "int");
                Assert(CucliDescribe.Value(true).Value<bool>(), "bool");
                Assert(CucliDescribe.Value("text").Value<string>() == "text", "string");
            });

            Check(checks, "describe.unityObject", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    probe.name = "cucli-probe";
                    var described = CucliDescribe.Value(probe) as JObject;
                    Assert(described != null, "an Object must describe to a JObject");
                    Assert(described["name"] != null && described["type"] != null, "must carry name and type");
                    // A Unity object must never be serialized by value: that is the guard rail that
                    // keeps a TextMeshPro or Mesh dump from flooding a caller.
                    Assert(described["intValue"] == null, "must not dump serialized fields by default");
                }
                finally { Object.DestroyImmediate(probe); }
            });

            Check(checks, "describe.truncatesString", () =>
            {
                var options = new CucliDescribe.Options { MaxStringLength = 16 };
                string described = CucliDescribe.Value(new string('x', 200), options).Value<string>();
                Assert(described.Length < 200 && described.Contains("truncated"), "long strings must be truncated");
            });

            Check(checks, "describe.truncatesCollections", () =>
            {
                var options = new CucliDescribe.Options { MaxItems = 3 };
                var described = CucliDescribe.Value(Enumerable.Range(0, 50).ToArray(), options) as JObject;
                Assert(described != null, "an over-long collection must become an object with metadata");
                Assert(described.Value<int>("count") == 50, "count must report the true total");
                Assert(described["$truncated"] != null, "must mark truncation");
            });

            Check(checks, "describe.depthLimit", () =>
            {
                var nested = new JObject { ["a"] = new JObject { ["b"] = new JObject { ["c"] = 1 } } };
                var options = new CucliDescribe.Options { MaxDepth = 1 };
                string text = CucliDescribe.Value(nested, options).ToString(Newtonsoft.Json.Formatting.None);
                Assert(text.Contains("$truncated") || !text.Contains("\"c\""), "depth limit must bite");
            });

            // ---------------------------------------------------------- paging
            Check(checks, "paging.slice", () =>
            {
                var source = Enumerable.Range(0, 250).Select(index => "item" + index).ToArray();
                CucliPage<string> first = CucliPaging.Slice(source, Args(new JObject { ["pageSize"] = 100 }));
                Assert(first.Items.Count == 100 && first.Total == 250, "first page");
                Assert(first.HasMore && first.NextCursor != null, "must offer a cursor");

                CucliPage<string> second = CucliPaging.Slice(
                    source, Args(new JObject { ["pageSize"] = 100, ["cursor"] = first.NextCursor }));
                Assert(second.Items[0] == "item100", "cursor must resume at the right offset, got " + second.Items[0]);

                CucliPage<string> third = CucliPaging.Slice(
                    source, Args(new JObject { ["pageSize"] = 100, ["cursor"] = second.NextCursor }));
                Assert(third.Items.Count == 50 && !third.HasMore, "last page must be short and terminal");
            });

            Check(checks, "paging.cursorRoundTrip", () =>
            {
                int decoded;
                Assert(CucliPaging.TryDecodeCursor(CucliPaging.EncodeCursor(1234), out decoded) && decoded == 1234,
                    "cursor round trip");
                Assert(!CucliPaging.TryDecodeCursor("not-a-cursor", out decoded), "garbage must not decode");
            });

            Check(checks, "paging.capsPageSize", () =>
            {
                var source = Enumerable.Range(0, 5000).ToArray();
                Assert(CucliPaging.Slice(source, Args(new JObject { ["pageSize"] = 100000 })).PageSize
                    <= CucliPaging.MaxPageSize, "page size must be capped");
            });

            // ---------------------------------------------------------- reflection
            Check(checks, "reflection.findType", () =>
            {
                string error;
                Assert(CucliReflection.FindType("UnityEngine.Vector3", out error) != null, "full name");
                Assert(CucliReflection.FindType("Vector3", out error) != null, "short name");
                Assert(CucliReflection.FindType("NoSuchTypeAnywhere", out error) == null && error != null,
                    "a missing type must report an error rather than throw");
            });

            Check(checks, "reflection.members", () =>
            {
                List<CucliMemberInfo> members = CucliReflection.Members(typeof(Vector3));
                Assert(members.Any(member => member.Name == "x"), "Vector3.x");
                Assert(members.Any(member => member.Name == "magnitude" && member.CanWrite == false),
                    "a read-only property must be marked unwritable");
            });

            Check(checks, "reflection.getPath", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    probe.vector3Value = new Vector3(1, 2, 3);
                    string error;
                    object value = CucliReflection.GetPath(probe, "vector3Value.y", out error);
                    Assert(error == null && value is float && Math.Abs((float)value - 2f) < 1e-6,
                        "dotted path must walk into a struct: " + error);
                }
                finally { Object.DestroyImmediate(probe); }
            });

            Check(checks, "reflection.trySetPath", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    string error;
                    Assert(CucliReflection.TrySetPath(probe, "vector3Value.z", new JValue(9f), out error), error);
                    Assert(Math.Abs(probe.vector3Value.z - 9f) < 1e-6, "write must land");
                    Assert(!CucliReflection.TrySetPath(probe, "noSuchField", new JValue(1), out error) && error != null,
                        "an unknown member must be refused with a reason");
                }
                finally { Object.DestroyImmediate(probe); }
            });

            Check(checks, "reflection.invoke", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    object result;
                    string error;
                    Assert(CucliReflection.TryInvoke(probe, "GetInstanceID", new JObject(), out result, out error),
                        "no-arg invoke: " + error);
                    Assert(!CucliReflection.TryInvoke(probe, "NoSuchMethod", new JObject(), out result, out error)
                        && error != null, "missing method must be reported");
                }
                finally { Object.DestroyImmediate(probe); }
            });

            // ---------------------------------------------------------- serialized
            Check(checks, "serialized.scalars", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    SetSerialized(probe, "intValue", new JValue(7));
                    SetSerialized(probe, "floatValue", new JValue(2.5f));
                    SetSerialized(probe, "boolValue", new JValue(true));
                    SetSerialized(probe, "stringValue", new JValue("hello"));
                    SetSerialized(probe, "layerMaskValue", new JValue(3));
                    Assert(probe.intValue == 7, "int");
                    Assert(Math.Abs(probe.floatValue - 2.5f) < 1e-6, "float");
                    Assert(probe.boolValue, "bool");
                    Assert(probe.stringValue == "hello", "string");
                    Assert(probe.layerMaskValue.value == 3, "layer mask");
                }
                finally { Object.DestroyImmediate(probe); }
            });

            Check(checks, "serialized.structs", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    SetSerialized(probe, "vector3Value", new JArray(1, 2, 3));
                    SetSerialized(probe, "colorValue", new JValue("#00FF00"));
                    SetSerialized(probe, "rectValue", new JArray(1, 2, 3, 4));
                    SetSerialized(probe, "boundsValue",
                        new JObject { ["center"] = new JArray(0, 0, 0), ["size"] = new JArray(4, 4, 4) });
                    SetSerialized(probe, "quaternionValue", new JArray(0, 90, 0));
                    Assert(probe.vector3Value == new Vector3(1, 2, 3), "vector3");
                    Assert(probe.colorValue.g > 0.99f, "colour from hex");
                    Assert(probe.rectValue == new Rect(1, 2, 3, 4), "rect");
                    Assert(probe.boundsValue.size == new Vector3(4, 4, 4), "bounds");
                    Assert(Math.Abs(probe.quaternionValue.eulerAngles.y - 90f) < 0.01f, "quaternion from euler");
                }
                finally { Object.DestroyImmediate(probe); }
            });

            Check(checks, "serialized.objectReference", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                var other = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    SetSerialized(probe, "objectValue", new JValue(other.GetInstanceID()));
                    Assert(probe.objectValue == other, "reference by instance id");
                    SetSerialized(probe, "objectValue", JValue.CreateNull());
                    Assert(probe.objectValue == null, "null clears the reference");
                }
                finally
                {
                    Object.DestroyImmediate(other);
                    Object.DestroyImmediate(probe);
                }
            });

            Check(checks, "serialized.arrays", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    SetSerialized(probe, "intArrayValue", new JArray(1, 2, 3, 4));
                    Assert(probe.intArrayValue != null && probe.intArrayValue.Length == 4, "array resize via Assign");
                    SetSerialized(probe, "stringArrayValue", new JArray("a", "b"));
                    Assert(probe.stringArrayValue != null && probe.stringArrayValue[1] == "b", "string array");
                }
                finally { Object.DestroyImmediate(probe); }
            });

            Check(checks, "serialized.readRoundTrip", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    SetSerialized(probe, "intValue", new JValue(11));
                    SerializedObject serialized;
                    SerializedProperty property;
                    Assert(CucliSerialized.TryGet(probe, "intValue", out property, out serialized),
                        "TryGet must find a serialized member");
                    JToken read = CucliSerialized.Read(property, CucliDescribe.Options.Default, 0);
                    Assert(read.Value<int>() == 11, "read must see the write, got " + read);
                }
                finally { Object.DestroyImmediate(probe); }
            });

            Check(checks, "serialized.rejectsBadValue", () =>
            {
                var probe = ScriptableObject.CreateInstance<CucliSelfTestProbe>();
                try
                {
                    string error;
                    bool handled;
                    bool ok = CucliSerialized.TrySet(
                        probe, "intValue", new JValue("not-a-number"), "selftest", out error, out handled);
                    Assert(!ok && handled, "a bad value on a serialized member must be handled and refused");
                    Assert(!string.IsNullOrEmpty(error), "and must explain why");

                    ok = CucliSerialized.TrySet(probe, "noSuchMember", new JValue(1), "selftest", out error, out handled);
                    Assert(!ok && !handled, "an unserialized member must report handled=false so callers fall back");
                }
                finally { Object.DestroyImmediate(probe); }
            });

            // ---------------------------------------------------------- assets
            Check(checks, "assets.normalize", () =>
            {
                Assert(CucliAssets.NormalizeAssetPath("assets/foo/bar.png") == "Assets/foo/bar.png", "lower-case prefix");
                Assert(CucliAssets.NormalizeAssetPath("Assets\\foo\\bar.png") == "Assets/foo/bar.png", "backslashes");
                Assert(CucliAssets.NormalizeAssetPath("Prefabs/Hero.prefab") == "Assets/Prefabs/Hero.prefab",
                    "bare relative path becomes project-relative");
                string absolute = Path.Combine(CucliPaths.ProjectRoot, "Assets", "foo.png");
                Assert(CucliAssets.NormalizeAssetPath(absolute) == "Assets/foo.png", "absolute path inside the project");
            });

            Check(checks, "assets.normalizeRejectsEscape", () =>
            {
                // A path that leaves the project must be refused rather than silently rewritten, or a
                // command could write outside the workspace.
                Assert(CucliAssets.NormalizeAssetPath("/etc/passwd") == null, "rooted outside path");
            });

            Check(checks, "assets.pathParts", () =>
            {
                Assert(CucliAssets.ParentFolder("Assets/A/B/c.prefab") == "Assets/A/B", "parent");
                Assert(CucliAssets.FileName("Assets/A/B/c.prefab") == "c.prefab", "file name");
                Assert(CucliAssets.FileNameWithoutExtension("Assets/A/B/c.prefab") == "c", "stem");
                Assert(CucliAssets.Extension("Assets/A/B/c.prefab") == ".prefab", "extension");
            });

            Check(checks, "assets.findAssetsFilters", () =>
            {
                string[] scenes = CucliAssets.FindAssets("Scene", "Assets", null, 5);
                Assert(scenes.Length > 0 && scenes.All(path => path.EndsWith(".unity")),
                    "type filter must return only scenes");
                Assert(scenes.Length <= 5, "limit must be honoured");
            });

            // ---------------------------------------------------------- paths and json
            Check(checks, "paths.atomicWrite", () =>
            {
                // Written under Library/, which is already ignored: this check has no project impact.
                string target = Path.Combine(CucliPaths.Root, "selftest", "atomic.txt");
                string content = "cucli " + Guid.NewGuid().ToString("N");
                CucliPaths.WriteAtomic(target, content);
                performed.Add("wrote " + target);
                Assert(File.ReadAllText(target, CucliJson.Utf8NoBom) == content, "content must round trip");

                CucliPaths.WriteAtomic(target, content + "-2");
                Assert(File.ReadAllText(target, CucliJson.Utf8NoBom) == content + "-2", "replace must be atomic and dirty-free");

                string[] leftovers = Directory.GetFiles(Path.GetDirectoryName(target), "*.tmp");
                Assert(leftovers.Length == 0, "no temporary files may be left behind: " + string.Join(",", leftovers));
            });

            Check(checks, "json.parseAndToken", () =>
            {
                JObject parsed = CucliJson.ParseObject("{\"a\":[1,2,{\"b\":\"中\"}]}");
                Assert(parsed["a"][2]["b"].Value<string>() == "中", "nested parse with non-ASCII");
                Assert(CucliJson.ToToken(null).Type == JTokenType.Null, "null becomes JSON null");
                string serialized = CucliJson.Serialize(new { Value = 1 });
                Assert(serialized.Contains("\"value\""), "camelCase on the wire, got " + serialized);
            });

            Check(checks, "optional.probe", () =>
            {
                Assert(CucliOptional.IsPackageInstalled("com.unity.nuget.newtonsoft-json"),
                    "the package's own dependency must be reported as installed");
                CucliCommandResult missing = CucliOptional.Missing("com.unity.probuilder", "x");
                Assert(missing.ErrorCode == CucliErrors.OptionalPackageMissing,
                    "a missing optional package must produce OPTIONAL_PACKAGE_MISSING");
                Assert(missing.Message.Contains("com.unity.probuilder"), "and must name what to install");
            });

            // ---------------------------------------------------------- resolver (read-only)
            Check(checks, "resolver.pathRoundTrip", () =>
            {
                GameObject sample = CucliObjectResolver.FindAllInLoadedScenes().FirstOrDefault();
                if (sample == null) return; // An empty scene is not a failure.

                string path = CucliObjectResolver.HierarchyPath(sample, true);
                string error;
                GameObject resolved = CucliObjectResolver.FindByHierarchyPath(path, out error);
                Assert(resolved == sample, "path '" + path + "' must resolve back to the same object: " + error);
            });

            Check(checks, "resolver.missingPath", () =>
            {
                string error;
                GameObject resolved = CucliObjectResolver.FindByHierarchyPath("NoSuchRoot/NoSuchChild", out error);
                Assert(resolved == null && error != null, "a missing path must report an error, not throw");
            });

            Check(checks, "resolver.rejectsBogusSearchMethod", () =>
            {
                string error;
                CucliObjectResolver.ResolveGameObjects(
                    Args(new JObject { ["target"] = "x", ["searchMethod"] = "by_telepathy" }), out error);
                Assert(error != null && error.Contains("not supported"),
                    "an unknown searchMethod must be refused with the supported list");
            });

            // ---------------------------------------------------------- registry
            Check(checks, "registry.surfaceIsCoherent", () =>
            {
                Assert(CucliCommandRegistry.Count > 0, "at least one command group must be registered");
                Assert(CucliCommandRegistry.DiagnosticList.Count == 0,
                    "the registry must report no diagnostics: " + string.Join("; ",
                        CucliCommandRegistry.DiagnosticList.Select(item => item.Code + " " + item.Message)));

                foreach (ICucliCommand command in CucliCommandRegistry.All)
                {
                    Assert(!string.IsNullOrWhiteSpace(command.Name), "every command needs a name");
                    Assert(!string.IsNullOrWhiteSpace(command.Summary), command.Name + " needs a summary");
                    Assert(command.Name == command.Name.Trim().ToLowerInvariant(),
                        command.Name + " must be lower case and untrimmed");

                    var declared = new HashSet<string>(StringComparer.Ordinal);
                    foreach (CucliActionInfo action in command.Actions ?? Array.Empty<CucliActionInfo>())
                    {
                        Assert(!string.IsNullOrWhiteSpace(action.Name), command.Name + " has an unnamed action");
                        Assert(declared.Add(action.Name), command.Name + " declares '" + action.Name + "' twice");
                    }
                }
            });

            // ---------------------------------------------------------- optional mutation
            if (mutate)
            {
                Check(checks, "assets.folderLifecycle", () =>
                {
                    const string folder = "Assets/CucliSelfTest.tmp";
                    try
                    {
                        Assert(CucliAssets.EnsureFolder(folder), "EnsureFolder must create the folder");
                        Assert(CucliAssets.FolderExists(folder), "and it must then exist");
                        Assert(CucliAssets.EnsureFolder(folder), "and be idempotent");
                        performed.Add("created " + folder);
                    }
                    finally
                    {
                        if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                    }

                    Assert(!CucliAssets.FolderExists(folder), "cleanup must remove the folder");
                });
            }
        }

        // ------------------------------------------------------------------ surface

        private static CucliCommandResult Surface(CucliCommandContext context)
        {
            string only = context.Input.GetString("group");
            var plan = new JArray();
            int readOnly = 0;
            int mutating = 0;
            int destructive = 0;
            int blockedByPackage = 0;

            foreach (ICucliCommand command in CucliCommandRegistry.All
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(only)
                    && !string.Equals(command.Name, only.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CucliCommandDescriptor descriptor = CucliCommandRegistry.Describe(command);
                foreach (CucliActionInfo action in command.Actions ?? Array.Empty<CucliActionInfo>())
                {
                    string kind = Classify(action.Name);

                    // Destructive and mutating actions are never auto-exercised against a real
                    // project: the harness skips them and reports them as skipped, rather than
                    // pretending the surface is covered.
                    bool autoTestable = kind == "readonly"
                        && string.IsNullOrEmpty(descriptor.OptionalPackage);

                    if (kind == "readonly") readOnly++;
                    else if (kind == "destructive") destructive++;
                    else mutating++;
                    if (!string.IsNullOrEmpty(descriptor.OptionalPackage)) blockedByPackage++;

                    plan.Add(new JObject
                    {
                        ["group"] = command.Name,
                        ["action"] = action.Name,
                        ["summary"] = action.Summary,
                        ["kind"] = kind,
                        ["autoTestable"] = autoTestable,
                        ["requiresEditor"] = descriptor.RequiresEditor,
                        ["requiredMode"] = descriptor.RequiredMode,
                        ["mutatesProject"] = descriptor.MutatesProject,
                        ["mayReloadDomain"] = descriptor.MayReloadDomain,
                        ["optionalPackage"] = descriptor.OptionalPackage == null
                            ? JValue.CreateNull()
                            : new JValue(descriptor.OptionalPackage),
                        ["source"] = descriptor.Source
                    });
                }
            }

            return CucliCommandResult.Ok(new
            {
                groupCount = CucliCommandRegistry.Count,
                actionCount = plan.Count,
                readOnlyActions = readOnly,
                mutatingActions = mutating,
                destructiveActions = destructive,
                actionsNeedingOptionalPackage = blockedByPackage,
                autoTestableActions = plan.Count(entry => entry.Value<bool>("autoTestable")),
                plan
            });
        }

        /// <summary>
        /// Names are the only signal available without running the action, so the classifier is
        /// deliberately conservative: anything it does not recognise as read-only is treated as
        /// mutating, which means the harness skips rather than writes.
        /// </summary>
        private static string Classify(string action)
        {
            string name = (action ?? string.Empty).ToLowerInvariant();

            string[] destructiveMarkers = { "delete", "remove", "clear", "wipe", "destroy", "reset", "revert", "unpack" };
            foreach (string marker in destructiveMarkers)
            {
                if (name.Contains(marker)) return "destructive";
            }

            string[] readOnlyMarkers =
            {
                "get", "list", "find", "inspect", "read", "show", "describe", "status", "ping",
                "query", "stat", "count", "exists", "validate", "check", "verify", "search",
                "catalog", "dump", "tree", "hierarchy", "info", "preview", "sample", "echo",
                "surface", "core", "environment", "extensions", "schema", "version", "help"
            };
            foreach (string marker in readOnlyMarkers)
            {
                if (name.Contains(marker)) return "readonly";
            }

            return "mutating";
        }

        // ------------------------------------------------------------------ sample

        private static CucliCommandResult Sample(CucliCommandContext context)
        {
            var checks = new List<JObject>();
            var performed = new List<string>();
            RunSelfTests(checks, performed, false);

            int passed = checks.Count(check => check.Value<bool>("ok"));
            var bySource = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ICucliCommand command in CucliCommandRegistry.All)
            {
                string source = CucliCommandRegistry.Describe(command).Source ?? "unknown";
                int current;
                bySource.TryGetValue(source, out current);
                bySource[source] = current + 1;
            }

            return CucliCommandResult.Ok(new
            {
                groups = CucliCommandRegistry.Count,
                actions = CucliCommandRegistry.All.Sum(
                    command => command.Actions == null ? 0 : command.Actions.Length),
                groupsBySource = bySource,
                catalogHash = CucliCommandRegistry.CatalogHash,
                helperChecks = new { total = checks.Count, passed, failed = checks.Count - passed },
                diagnostics = CucliCommandRegistry.DiagnosticList.Count,
                optionalPackages = CucliOptional.DescribeEnvironment()
            });
        }

        // ------------------------------------------------------------------ helpers

        private static CucliArgs Args(JObject payload)
        {
            return new CucliArgs(payload ?? new JObject());
        }

        private static T Convert<T>(JToken token)
        {
            string error;
            object value = CucliValues.Convert(token, typeof(T), out error);
            Assert(error == null, typeof(T).Name + ": " + error);
            return (T)value;
        }

        private static object ConvertObject(JToken token, Type type)
        {
            string error;
            object value = CucliValues.Convert(token, type, out error);
            Assert(error == null, type.Name + ": " + error);
            return value;
        }

        private static void SetSerialized(Object target, string member, JToken value)
        {
            string error;
            bool handled;
            bool ok = CucliSerialized.TrySet(target, member, value, "cucli selftest", out error, out handled);
            Assert(ok, "TrySet(" + member + ") failed: " + (error ?? "(no error reported)"));
            Assert(handled, "TrySet(" + member + ") did not go through SerializedObject");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Check(List<JObject> checks, string name, Action body)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                body();
                watch.Stop();
                checks.Add(new JObject
                {
                    ["name"] = name,
                    ["ok"] = true,
                    ["durationMs"] = watch.ElapsedMilliseconds
                });
            }
            catch (Exception exception)
            {
                watch.Stop();
                checks.Add(new JObject
                {
                    ["name"] = name,
                    ["ok"] = false,
                    ["durationMs"] = watch.ElapsedMilliseconds,
                    ["detail"] = exception.GetBaseException().Message,
                    ["where"] = FirstMeaningfulFrame(exception)
                });
            }
        }

        /// <summary>First stack frame inside this package, which is the one worth reporting.</summary>
        private static string FirstMeaningfulFrame(Exception exception)
        {
            string trace = exception.StackTrace ?? string.Empty;
            foreach (string line in trace.Split('\n'))
            {
                if (line.Contains("CustomUnityCli")) return line.Trim();
            }

            return null;
        }
    }
}
