#nullable enable

// T-W2.5-04 [Edit] tests: local:T-W2.5-04:property-ops — the G5 triad
// (success, single Undo, precondition violation) for the two §I.1
// property Operations through a real UnityStepContext over the standard
// type registry: every supported kind round-trips get/set on a scene
// object and on an asset, deeper-than-3 or unsupported paths are
// OW-UNITY-007, an asset target validates as asset.modify through the
// real D6 resolver and is saved immediately, and scene targets undo in
// one step.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.Testing;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>Per-Operation behaviour of the §I.1 property Operations.</summary>
    internal sealed class PropertyOperationTests
    {
        private static readonly IReadOnlyDictionary<string, string> GetIn =
            new Dictionary<string, string>
            {
                ["target"] = "unity.ObjectRef",
                ["propertyPath"] = "core.String",
            };

        private static readonly IReadOnlyDictionary<string, string> GetOut =
            new Dictionary<string, string> { ["value"] = "core.Json" };

        private static readonly IReadOnlyDictionary<string, string> SetIn =
            new Dictionary<string, string>
            {
                ["target"] = "unity.ObjectRef",
                ["propertyPath"] = "core.String",
                ["value"] = "core.Json",
            };

        private static readonly IReadOnlyDictionary<string, string> NoPorts =
            new Dictionary<string, string>();

        // A StaticContext that answers Parameters and StaticTypeOf from
        // fixed maps — the SG-06 answers a data edge, a binding or a
        // constant produce for the IEffectResolver.
        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;
            private readonly IReadOnlyDictionary<string, TypeRef> _types;

            internal StubStaticContext(
                IReadOnlyDictionary<string, InputValue> parameters,
                IReadOnlyDictionary<string, TypeRef> types)
            {
                _parameters = parameters;
                _types = types;
            }

            public override string NodeId => "set";

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("ResolveEffects reads no port.");

            public override TypeRef? StaticTypeOf(string port) =>
                port is not null && _types.TryGetValue(port, out var type) ? type : null;
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> inputTypes,
            IReadOnlyDictionary<string, string> outputTypes,
            IChangeRecorder? changes = null) =>
            new UnityStepContext(
                inputs,
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                changes ?? new StepChangeRecorder(0, "prop"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static Dictionary<string, InputValue> Inputs(
            params (string Port, JsonValue Value)[] entries)
        {
            var inputs = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            foreach (var (port, value) in entries)
            {
                inputs[port] = InputValue.Present(value);
            }
            return inputs;
        }

        private static JsonValue TargetRef(Object target) =>
            new RefResolver().ToRef(target, "unity.ObjectRef");

        private static JsonValue AssetRefOf(Object asset) =>
            new RefResolver().ToRef(asset, "unity.AssetRef<unity.ScriptableObject>");

        private static PropertyProbeAsset NewProbeAsset(
            OperationTestHarness harness, string name)
        {
            var asset = ScriptableObject.CreateInstance<PropertyProbeAsset>();
            asset.name = name;
            AssetDatabase.CreateAsset(asset, harness.TempFolder + "/" + name + ".asset");
            return asset;
        }

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                assetPath.Replace('/', Path.DirectorySeparatorChar));

        // A Texture2D the asset database imported itself: an in-memory
        // texture handed to AssetDatabase.CreateAsset is not the instance
        // the database hands back out — the imported file keeps its own
        // object and the new'ed one stays a detached copy, which the
        // SameAs reference assertion below would misjudge. A real
        // imported PNG has exactly one live object.
        private static Texture2D NewTextureAsset(OperationTestHarness harness, string name)
        {
            var path = harness.TempFolder + "/" + name + ".png";
            var pixels = new Texture2D(2, 2);
            File.WriteAllBytes(PhysicalPath(path), pixels.EncodeToPNG());
            Object.DestroyImmediate(pixels);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(texture, Is.Not.Null, "the texture fixture must import: " + path);
            return texture!;
        }

        private static JsonValue GetJson(JsonValue targetRef, string path)
        {
            var context = Context(
                Inputs(
                    ("target", targetRef),
                    ("propertyPath", Json("\"" + path + "\""))),
                GetIn, GetOut);
            var result = new PropertyGetOperation().Execute(context);
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                () => path + ": " + Describe(result));
            return context.Outputs["value"];
        }

        private static StepResult Get(Object target, string path,
            out JsonValue? value, UnityStepContext? context = null)
        {
            var used = context ?? Context(
                Inputs(
                    ("target", TargetRef(target)),
                    ("propertyPath", Json("\"" + path + "\""))),
                GetIn, GetOut);
            var result = new PropertyGetOperation().Execute(used);
            value = result.Kind == StepResultKind.Succeeded &&
                used.Outputs.TryGetValue("value", out var written)
                    ? written
                    : null;
            return result;
        }

        private static StepResult Set(
            Object target, string path, JsonValue value, IChangeRecorder? changes = null)
        {
            var context = Context(
                Inputs(
                    ("target", TargetRef(target)),
                    ("propertyPath", Json("\"" + path + "\"")),
                    ("value", value)),
                SetIn, NoPorts, changes);
            return new PropertySetOperation().Execute(context);
        }

        private static StepResult SetOnAsset(
            Object asset, string path, JsonValue value, IChangeRecorder? changes = null)
        {
            var context = Context(
                Inputs(
                    ("target", AssetRefOf(asset)),
                    ("propertyPath", Json("\"" + path + "\"")),
                    ("value", value)),
                SetIn, NoPorts, changes);
            return new PropertySetOperation().Execute(context);
        }

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);

        private static void AssertFailed(StepResult result, string code)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed), () => Describe(result));
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(code), () => Describe(result));
        }

        // ── unity.property.get — success ─────────────────────────────────

        [Test]
        public void Get_SceneScalars_ReturnsJsonValues()
        {
            using var harness = OperationTestHarness.Create();
            var go = HarnessObjects.NewGameObject(harness, "Named42");
            go.layer = 7;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = 1.5f;
            light.color = new Color(0.25f, 0.5f, 0.75f, 1f);
            light.enabled = true;

            Assert.That(Get(go, "m_Name", out var name).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That((string?)name!.ToToken(), Is.EqualTo("Named42"));
            Assert.That(Get(go, "m_Layer", out var layer).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That((long?)layer!.ToToken(), Is.EqualTo(7));
            Assert.That(Get(go, "m_IsActive", out var active).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That((bool?)active!.ToToken(), Is.True);
            Assert.That(Get(light.transform, "m_LocalPosition", out _).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(Get(light, "m_Type", out var type).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That((string?)type!.ToToken(), Is.EqualTo("Point"),
                "an enum reads back by member name");
            Assert.That(Get(light, "m_Intensity", out var intensity).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That((double?)intensity!.ToToken(), Is.EqualTo(1.5).Within(1e-6));
            Assert.That(Get(light, "m_Color", out var color).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var channels = (JObject)color!.ToToken();
            Assert.That((double?)channels["g"], Is.EqualTo(0.5).Within(1e-6));
            Assert.That(Get(light, "m_Enabled", out var enabled).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That((bool?)enabled!.ToToken(), Is.True);
        }

        [Test]
        public void Get_VectorAndQuaternion_ReturnComponentObjects()
        {
            using var harness = OperationTestHarness.Create();
            var go = HarnessObjects.NewGameObject(harness, "Placed");
            go.transform.localPosition = new Vector3(1, 2, 3);
            go.transform.localRotation =
                new Quaternion(0.1f, 0.2f, 0.3f, 0.9f).normalized;

            Assert.That(Get(go.transform, "m_LocalPosition", out var position).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var vector = (JObject)position!.ToToken();
            Assert.That((double?)vector["x"], Is.EqualTo(1).Within(1e-6));
            Assert.That((double?)vector["y"], Is.EqualTo(2).Within(1e-6));
            Assert.That((double?)vector["z"], Is.EqualTo(3).Within(1e-6));

            Assert.That(Get(go.transform, "m_LocalRotation", out var rotation).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var quat = (JObject)rotation!.ToToken();
            Assert.That((double?)quat["w"], Is.EqualTo(go.transform.localRotation.w).Within(1e-4));
        }

        [Test]
        public void Get_ObjectReference_ReturnsRefJson()
        {
            using var harness = OperationTestHarness.Create();
            var mesh = new Mesh { name = "RefMesh" };
            AssetDatabase.CreateAsset(mesh, harness.TempFolder + "/RefMesh.asset");
            var go = HarnessObjects.NewGameObject(harness, "Meshed");
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            Assert.That(Get(filter, "m_Mesh", out var value).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var reference = (JObject)value!.ToToken();
            Assert.That((string?)reference["kind"], Is.EqualTo("asset"));
            Assert.That((string?)reference["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(harness.TempFolder + "/RefMesh.asset")));
        }

        [Test]
        public void Get_KindsOnAsset_ArraysGenericMembersAndReferences()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "Kinds");
            var texture = NewTextureAsset(harness, "KindsSurface");
            asset.marker = 42;
            asset.label = "probe";
            asset.uv = new Vector2(0.25f, 0.75f);
            asset.extents = new Vector4(1, 2, 3, 4);
            asset.kind = PropertyProbeAsset.ProbeKind.Gamma;
            asset.surface = texture;
            asset.numbers = new[] { 5, 6 };
            asset.nested.number = 11;
            asset.nested.inner.value = 17;
            AssetDatabase.SaveAssetIfDirty(asset);
            var target = AssetRefOf(asset);

            Assert.That((long?)GetJson(target, "marker").ToToken(), Is.EqualTo(42));
            Assert.That((string?)GetJson(target, "label").ToToken(), Is.EqualTo("probe"));
            Assert.That((double?)((JObject)GetJson(target, "uv").ToToken())["y"],
                Is.EqualTo(0.75).Within(1e-6));
            Assert.That((double?)((JObject)GetJson(target, "extents").ToToken())["w"],
                Is.EqualTo(4).Within(1e-6));
            Assert.That((string?)GetJson(target, "kind").ToToken(), Is.EqualTo("Gamma"));
            Assert.That((string?)((JObject)GetJson(target, "surface").ToToken())["kind"],
                Is.EqualTo("asset"));
            Assert.That(((JArray)GetJson(target, "numbers").ToToken()).Count, Is.EqualTo(2));

            // "numbers.Array.data[0]" is the deepest supported path —
            // three property-path segments (SPEC §G5 item 7's depth 3).
            Assert.That((long?)GetJson(target, "numbers.Array.data[1]").ToToken(), Is.EqualTo(6));

            // A whole member-object read of "nested" is OW-UNITY-007 —
            // its stored tree reaches "inner.deep.flag" at depth 4 — so
            // the members assert through leaf paths inside the bound.
            Assert.That((long?)GetJson(target, "nested.number").ToToken(), Is.EqualTo(11));

            // "nested.inner.value" — the three-segment member chain.
            Assert.That((long?)GetJson(target, "nested.inner.value").ToToken(), Is.EqualTo(17));
        }

        // ── unity.property.get — boundary failures ───────────────────────

        [Test]
        public void Get_MissingPath_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var go = HarnessObjects.NewGameObject(harness, "T");
            AssertFailed(Get(go, "m_NoSuchProperty", out _), ErrorCodes.OW_UNITY_007);
        }

        [Test]
        public void Get_PathDeeperThan3_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "Deep");
            var context = Context(
                Inputs(
                    ("target", AssetRefOf(asset)),
                    ("propertyPath", Json("\"nested.inner.deep.flag\""))),
                GetIn, GetOut);
            AssertFailed(
                new PropertyGetOperation().Execute(context), ErrorCodes.OW_UNITY_007);
        }

        [Test]
        public void Get_MemberTreeBeyondDepth3_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "DeepMembers");
            asset.nestedList = new[] { new PropertyProbeAsset.ProbeNested() };
            AssetDatabase.SaveAssetIfDirty(asset);

            // Every path below is inside the three-segment bound, but the
            // member object the read would hand back reaches
            // "nested.inner.deep.flag" at depth 4 — a value
            // unity.property.set refuses to apply — so §G5's depth rule
            // rejects the read with OW-UNITY-007 too.
            AssertFailed(Get(asset, "nested", out _), ErrorCodes.OW_UNITY_007);
            AssertFailed(Get(asset, "nested.inner", out _), ErrorCodes.OW_UNITY_007);
            AssertFailed(Get(asset, "nested.inner.deep", out _), ErrorCodes.OW_UNITY_007);
            AssertFailed(Get(asset, "nestedList", out _), ErrorCodes.OW_UNITY_007);
            AssertFailed(
                Get(asset, "nestedList.Array.data[0]", out _),
                ErrorCodes.OW_UNITY_007);
        }

        [Test]
        public void Get_UnsupportedKind_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "Unsupported");
            var context = Context(
                Inputs(
                    ("target", AssetRefOf(asset)),
                    ("propertyPath", Json("\"region\""))),
                GetIn, GetOut);
            AssertFailed(
                new PropertyGetOperation().Execute(context), ErrorCodes.OW_UNITY_007);
        }

        // ── unity.property.set — success and round-trip ──────────────────

        [Test]
        public void Set_SceneScalars_RoundTrip()
        {
            using var harness = OperationTestHarness.Create();
            var go = HarnessObjects.NewGameObject(harness, "Old");
            var light = go.AddComponent<Light>();

            Assert.That(Set(go, "m_Name", Json("\"Renamed\"")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(go.name, Is.EqualTo("Renamed"));
            Assert.That(Set(go, "m_Layer", Json("4")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(go.layer, Is.EqualTo(4));
            Assert.That(Set(go, "m_IsActive", Json("false")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(go.activeSelf, Is.False);
            Assert.That(Set(light, "m_Type", Json("\"Spot\"")).Kind,
                Is.EqualTo(StepResultKind.Succeeded), "an enum sets by member name");
            Assert.That(light.type, Is.EqualTo(LightType.Spot));
            Assert.That(Set(light, "m_Intensity", Json("2.5")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(light.intensity, Is.EqualTo(2.5f).Within(1e-6));
            Assert.That(Set(light, "m_Color",
                    Json("{\"r\":0.1,\"g\":0.2,\"b\":0.3,\"a\":1}")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(light.color.g, Is.EqualTo(0.2f).Within(1e-6));
        }

        [Test]
        public void Set_SceneVectorsQuaternionAndReference_RoundTrip()
        {
            using var harness = OperationTestHarness.Create();
            var go = HarnessObjects.NewGameObject(harness, "Moved");
            var mesh = new Mesh { name = "SetMesh" };
            AssetDatabase.CreateAsset(mesh, harness.TempFolder + "/SetMesh.asset");
            var filter = go.AddComponent<MeshFilter>();

            Assert.That(Set(go.transform, "m_LocalPosition",
                    Json("{\"x\":3,\"y\":4,\"z\":5}")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(go.transform.localPosition, Is.EqualTo(new Vector3(3, 4, 5)));
            Assert.That(Set(go.transform, "m_LocalRotation",
                    Json("{\"x\":0,\"y\":0,\"z\":0,\"w\":1}")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(go.transform.localRotation, Is.EqualTo(Quaternion.identity));

            // An ObjectReference takes the §B5 Ref JSON (SPEC §G5 item 7).
            var meshRef = new RefResolver().ToRef(mesh, "unity.AssetRef<unity.Object>");
            Assert.That(Set(filter, "m_Mesh", meshRef).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(filter.sharedMesh, Is.SameAs(mesh));

            // The written value reads back through unity.property.get.
            Assert.That(Get(filter, "m_Mesh", out var readBack).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That((string?)((JObject)readBack!.ToToken())["kind"], Is.EqualTo("asset"));
        }

        [Test]
        public void Set_AssetEveryKind_RoundTripsAndSavesImmediately()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "RoundTrip");
            var texture = NewTextureAsset(harness, "RoundTripSurface");

            var cases = new (string Path, JsonValue Value)[]
            {
                ("marker", Json("42")),
                ("small", Json("3")),
                ("label", Json("\"hello\"")),
                ("enabled", Json("true")),
                ("scalar", Json("1.25")),
                ("tint", Json("{\"r\":0.1,\"g\":0.2,\"b\":0.3,\"a\":0.4}")),
                ("uv", Json("{\"x\":0.5,\"y\":0.75}")),
                ("offset", Json("{\"x\":1,\"y\":2,\"z\":3}")),
                ("extents", Json("{\"x\":4,\"y\":5,\"z\":6,\"w\":7}")),
                ("rotation", Json("{\"x\":0,\"y\":0,\"z\":0,\"w\":1}")),
                ("kind", Json("\"Gamma\"")),
                ("surface", new RefResolver().ToRef(texture, "unity.AssetRef<unity.Object>")),
                ("nested", Json("{\"number\":9,\"text\":\"inner\",\"inner\":{\"value\":8}}")),
                ("numbers", Json("[1,2,3]")),
                ("nested.inner.value", Json("13")),
                ("numbers.Array.data[0]", Json("7")),
            };
            foreach (var (path, value) in cases)
            {
                var result = SetOnAsset(asset, path, value);
                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                    () => path + ": " + Describe(result));
            }

            // §G5 item 7: an asset set is saved immediately.
            Assert.That(EditorUtility.IsDirty(asset), Is.False,
                "SaveAssetIfDirty must flush the change at once");

            Assert.That(asset.marker, Is.EqualTo(42));
            Assert.That(asset.small, Is.EqualTo(3));
            Assert.That(asset.label, Is.EqualTo("hello"));
            Assert.That(asset.enabled, Is.True);
            Assert.That(asset.scalar, Is.EqualTo(1.25f).Within(1e-6));
            Assert.That(asset.tint.b, Is.EqualTo(0.3f).Within(1e-6));
            Assert.That(asset.uv, Is.EqualTo(new Vector2(0.5f, 0.75f)));
            Assert.That(asset.offset, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(asset.extents, Is.EqualTo(new Vector4(4, 5, 6, 7)));
            Assert.That(asset.rotation, Is.EqualTo(Quaternion.identity));
            Assert.That(asset.kind, Is.EqualTo(PropertyProbeAsset.ProbeKind.Gamma));
            Assert.That(asset.surface, Is.SameAs(texture));
            Assert.That(asset.nested.number, Is.EqualTo(9));
            Assert.That(asset.nested.text, Is.EqualTo("inner"));
            Assert.That(asset.nested.inner.value, Is.EqualTo(13));
            Assert.That(asset.numbers, Is.EqualTo(new[] { 7, 2, 3 }));
        }

        [Test]
        public void Set_Asset_RecordsModifyAndSaveAsNotUndoable()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "Recorded");
            var changes = new StepChangeRecorder(0, "prop");

            var result = SetOnAsset(asset, "marker", Json("9"), changes);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(changes.Records, Has.Count.EqualTo(2));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Modified));
            Assert.That(changes.Records[0].Undoable, Is.False);
            Assert.That(changes.Records[1].Action, Is.EqualTo(ChangeAction.Saved));
            Assert.That(changes.Records[1].Undoable, Is.False);
        }

        [Test]
        public void Set_Scene_OneUndoRestoresTheProperty()
        {
            using var harness = OperationTestHarness.Create();
            var go = HarnessObjects.NewGameObject(harness, "BeforeUndo");
            var undoGroup = Undo.GetCurrentGroup();
            var result = harness.Run(
                PropertySetOperation.OperationId,
                new { target = go, propertyPath = "m_Name", value = "AfterSet" });
            try
            {
                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(go.name, Is.EqualTo("AfterSet"));

                Undo.PerformUndo();

                Assert.That(go.name, Is.EqualTo("BeforeUndo"),
                    "one Undo restores the scene property (§I.1 Undo Full)");
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        [Test]
        public void Set_Scene_RecordsAnUndoableModification()
        {
            using var harness = OperationTestHarness.Create();
            var go = HarnessObjects.NewGameObject(harness, "Changed");
            var changes = new StepChangeRecorder(0, "prop");

            var result = Set(go, "m_Name", Json("\"Changed\""), changes);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Modified));
            Assert.That(changes.Records[0].Undoable, Is.True);
        }

        // ── unity.property.set — boundary failures ───────────────────────

        [Test]
        public void Set_MissingPath_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var go = HarnessObjects.NewGameObject(harness, "T");
            AssertFailed(Set(go, "m_NoSuchProperty", Json("1")), ErrorCodes.OW_UNITY_007);
        }

        [Test]
        public void Set_PathDeeperThan3_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "DeepSet");
            AssertFailed(
                SetOnAsset(asset, "nested.inner.deep.flag", Json("1")),
                ErrorCodes.OW_UNITY_007);
        }

        [Test]
        public void Set_ArrayMemberDeeperThan3_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "DeepArray");
            AssertFailed(
                SetOnAsset(asset, "nestedList.Array.data[0].number", Json("1")),
                ErrorCodes.OW_UNITY_007);
        }

        [Test]
        public void Set_UnsupportedKind_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "UnsupportedSet");
            AssertFailed(
                SetOnAsset(asset, "region", Json("{}")),
                ErrorCodes.OW_UNITY_007);
        }

        [Test]
        public void Set_UnapplyableValue_IsOwUnity007_AndWritesNothing()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "BadValue");
            asset.marker = 5;
            AssetDatabase.SaveAssetIfDirty(asset);

            AssertFailed(
                SetOnAsset(asset, "marker", Json("\"not-a-number\"")),
                ErrorCodes.OW_UNITY_007);
            Assert.That(asset.marker, Is.EqualTo(5), "a refused value leaves the field alone");
        }

        [Test]
        public void Set_MemberApplyBeyondDepthBudget_IsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var asset = NewProbeAsset(harness, "Budget");
            // "nested.inner" is depth 2, so its member object may reach the
            // third level but no further: "deep.flag" would apply at 4.
            AssertFailed(
                SetOnAsset(asset, "nested.inner",
                    Json("{\"value\":1,\"deep\":{\"flag\":2}}")),
                ErrorCodes.OW_UNITY_007);
            var applied = SetOnAsset(asset, "nested.inner", Json("{\"value\":1}"));
            Assert.That(applied.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(applied));
            Assert.That(asset.nested.inner.value, Is.EqualTo(1));
        }

        // ── §C4 IEffectResolver — the resolver's own answers ─────────────

        private static Dictionary<string, InputValue> MissingPorts()
        {
            var parameters = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            foreach (var port in SetIn.Keys)
            {
                parameters[port] = InputValue.Missing;
            }
            return parameters;
        }

        private static StubStaticContext EffectContext(
            string? staticTypeId = null, string? constantTarget = null)
        {
            var parameters = MissingPorts();
            if (constantTarget is not null)
            {
                parameters["target"] = InputValue.Present(Json(constantTarget));
            }
            var types = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            if (staticTypeId is not null)
            {
                var parsed = TypeRef.Parse(staticTypeId);
                Assert.That(parsed.Ok, Is.True, "fixture TypeRef must parse: " + staticTypeId);
                types["target"] = parsed.Value!;
            }
            return new StubStaticContext(parameters, types);
        }

        [Test]
        public void ResolveEffects_AssetRefStaticTypes_AnswerAssetModify()
        {
            var op = new PropertySetOperation();
            foreach (var type in new[]
            {
                "unity.AssetRef<unity.Material>",
                "unity.PrefabAssetRef",
            })
            {
                Assert.That(op.ResolveEffects(EffectContext(staticTypeId: type)),
                    Is.EqualTo(new[] { EffectNames.AssetModify }), type);
            }
        }

        [Test]
        public void ResolveEffects_SceneObjectStaticTypes_AnswerSceneModify()
        {
            var op = new PropertySetOperation();
            foreach (var type in new[]
            {
                "unity.GameObjectRef",
                "unity.PrefabInstanceRef",
                "unity.ComponentRef<unity.Transform>",
            })
            {
                Assert.That(op.ResolveEffects(EffectContext(staticTypeId: type)),
                    Is.EqualTo(new[] { EffectNames.SceneModify }), type);
            }
        }

        [Test]
        public void ResolveEffects_ConstantRefKind_RefinesBeyondDeclaredType()
        {
            var op = new PropertySetOperation();
            Assert.That(
                op.ResolveEffects(EffectContext(
                    "unity.ObjectRef", "{\"kind\":\"asset\",\"guid\":\"aa\"}")),
                Is.EqualTo(new[] { EffectNames.AssetModify }));
            Assert.That(
                op.ResolveEffects(EffectContext(
                    "unity.ObjectRef", "{\"kind\":\"gameObject\",\"path\":\"/T\"}")),
                Is.EqualTo(new[] { EffectNames.SceneModify }));
            Assert.That(
                op.ResolveEffects(EffectContext(
                    "unity.ObjectRef", "{\"kind\":\"prefabAsset\",\"guid\":\"aa\"}")),
                Is.EqualTo(new[] { EffectNames.AssetModify }));
        }

        [Test]
        public void ResolveEffects_StaticallyUnknownTarget_AnswersBothEffects()
        {
            var op = new PropertySetOperation();
            Assert.That(op.ResolveEffects(EffectContext("unity.ObjectRef")),
                Is.EqualTo(new[] { EffectNames.SceneModify, EffectNames.AssetModify }));
            Assert.That(op.ResolveEffects(EffectContext("unity.SceneRef")),
                Is.EqualTo(new[] { EffectNames.SceneModify, EffectNames.AssetModify }));
            Assert.That(op.ResolveEffects(EffectContext()),
                Is.EqualTo(new[] { EffectNames.SceneModify, EffectNames.AssetModify }));
        }
    }
}
