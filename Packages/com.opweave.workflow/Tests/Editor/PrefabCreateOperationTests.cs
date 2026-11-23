#nullable enable

// T-W2.6-02 [Edit] tests (local:T-W2.6-02:prefab-create-ops): SPEC §G6
// items 1,2,4 for the three Prefab-creating Operations — each produces the
// asset/instance and outputs of its §I.2 row, an existing path without
// overwrite refuses with OW-UNITY-005, and create_variant leaves the user's
// scenes unmodified.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.6-02:prefab-create-ops — normal behavior and boundary
    /// violations of <c>unity.prefab.create_from_gameobject</c>,
    /// <c>unity.prefab.instantiate</c> and <c>unity.prefab.create_variant</c>.
    /// </summary>
    internal sealed class PrefabCreateOperationTests
    {
        private const string TargetPath =
            "Assets/OpWeavePrefabContentsProbe/Created.prefab";
        private const string VariantPath =
            "Assets/OpWeavePrefabContentsProbe/Variant.prefab";

        private static readonly IReadOnlyDictionary<string, string> CreateIn =
            new Dictionary<string, string>
            {
                ["source"] = "unity.GameObjectRef",
                ["path"] = "unity.AssetPath",
                ["connect"] = "core.Bool",
                ["overwrite"] = "core.Bool",
            };

        private static readonly IReadOnlyDictionary<string, string> CreateOut =
            new Dictionary<string, string>
            {
                ["prefab"] = "unity.PrefabAssetRef",
                ["instance"] = "unity.PrefabInstanceRef",
            };

        private static readonly IReadOnlyDictionary<string, string> InstIn =
            new Dictionary<string, string>
            {
                ["prefab"] = "unity.PrefabAssetRef",
                ["scene"] = "unity.SceneRef",
                ["parent"] = "unity.GameObjectRef",
                ["position"] = "unity.Vector3",
            };

        private static readonly IReadOnlyDictionary<string, string> InstOut =
            new Dictionary<string, string> { ["instance"] = "unity.PrefabInstanceRef" };

        private static readonly IReadOnlyDictionary<string, string> VarIn =
            new Dictionary<string, string>
            {
                ["base"] = "unity.PrefabAssetRef",
                ["path"] = "unity.AssetPath",
            };

        private static readonly IReadOnlyDictionary<string, string> VarOut =
            new Dictionary<string, string> { ["variant"] = "unity.PrefabAssetRef" };

        private readonly List<GameObject> _scratch = new List<GameObject>();

        // The tests instantiate, connect, reparent and re-save in the
        // active scene, which marks it dirty; one clean untitled scene is
        // the same starting state the harness fixtures use, so leftover
        // unsaved changes can neither enter nor leave this fixture.
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PrefabContentsFixture.EnsureFolder(
                "Assets/OpWeavePrefabContentsProbe", "OpWeavePrefabContentsProbe");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _scratch)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
            _scratch.Clear();
            PrefabContentsFixture.Cleanup();
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private GameObject Scratch(string name)
        {
            var go = new GameObject(name);
            _scratch.Add(go);
            return go;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> inputTypes,
            IReadOnlyDictionary<string, string> outputTypes) =>
            new UnityStepContext(
                inputs,
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "prefab-create"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static InputValue Present(string json)
        {
            var parsed = JsonValue.Parse(json);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + json);
            return InputValue.Present(parsed.Value!);
        }

        private static InputValue Ref(UnityEngine.Object go, string refType) =>
            InputValue.Present(new RefResolver().ToRef(go, refType));

        private static Dictionary<string, InputValue> CreateInputs(
            GameObject source, string path, bool? connect = null, bool? overwrite = null)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["source"] = Ref(source, "unity.GameObjectRef"),
                ["path"] = Present("\"" + path + "\""),
            };
            if (connect is { } c)
            {
                inputs["connect"] = Present(c ? "true" : "false");
            }
            if (overwrite is { } o)
            {
                inputs["overwrite"] = Present(o ? "true" : "false");
            }
            return inputs;
        }

        private static UnityStepContext CreateContext(
            Dictionary<string, InputValue> inputs) =>
            Context(inputs, CreateIn, CreateOut);

        private static void AssertSucceeded(StepResult result) =>
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                result.Error is null ? "" : result.Error.Code + " " + result.Error.Message);

        private static void AssertFailed(StepResult result, string code)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error?.Code, Is.EqualTo(code),
                result.Error?.Message ?? "");
        }

        // ── unity.prefab.create_from_gameobject ──────────────────────────

        // §G6 item 1 / §I.2: connect=false saves the asset through
        // SaveAsPrefabAsset, publishes only the prefab output, and the
        // source stays a plain GameObject.
        [Test]
        public void CreateFromGameObject_NoConnect_SavesAssetLeavesSourcePlain()
        {
            var source = Scratch("Plain");
            var context = CreateContext(
                CreateInputs(source, TargetPath, connect: false));

            AssertSucceeded(new PrefabCreateFromGameObjectOperation().Execute(context));

            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            Assert.That(saved, Is.Not.Null, "the Prefab asset must exist");
            Assert.That(context.Outputs.ContainsKey("prefab"), Is.True);
            Assert.That(context.Outputs.ContainsKey("instance"), Is.False,
                "connect:false publishes no instance output (§I.2)");
            Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(source), Is.False,
                "the source must not be connected");
            Assert.That(saved!.name, Is.EqualTo("Created"),
                "the saved root takes the file's base name");
        }

        // §G6 item 1: connect (default) saves and connects — the source
        // becomes the outermost instance of the new Prefab and the step
        // publishes both outputs.
        [Test]
        public void CreateFromGameObject_Connect_SavesAndConnectsSource()
        {
            var source = Scratch("Linked");
            var context = CreateContext(CreateInputs(source, TargetPath));

            AssertSucceeded(new PrefabCreateFromGameObjectOperation().Execute(context));

            Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(source), Is.True,
                "the source must become the outermost instance");
            Assert.That(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source),
                Is.EqualTo(TargetPath));
            Assert.That(context.Outputs.ContainsKey("instance"), Is.True);
            var instanceRef = (JObject)context.Outputs["instance"].ToToken();
            Assert.That((string?)instanceRef["kind"], Is.EqualTo("prefabInstance"));
            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["instance"]);
            Assert.That(resolved.Ok && ReferenceEquals(resolved.Value, source), Is.True,
                "the instance output must resolve back to the connected source");
        }

        // §I.2 note: an occupied path without overwrite is OW-UNITY-005.
        [Test]
        public void CreateFromGameObject_ExistingPath_FailsOwUnity005()
        {
            PrefabContentsFixture.CreateProbePrefab("Created.prefab");
            var source = Scratch("Other");
            var context = CreateContext(CreateInputs(source, TargetPath));

            var result = new PrefabCreateFromGameObjectOperation().Execute(context);

            AssertFailed(result, "OW-UNITY-005");
        }

        // §I.2 note: overwrite=true rewrites an existing Prefab asset and
        // keeps its guid (asset.modify joins asset.create via
        // IEffectResolver).
        [Test]
        public void CreateFromGameObject_Overwrite_RewritesSameGuid()
        {
            PrefabContentsFixture.CreateProbePrefab("Created.prefab");
            var guidBefore = AssetDatabase.AssetPathToGUID(TargetPath);
            var source = Scratch("NewContent");
            var context = CreateContext(
                CreateInputs(source, TargetPath, overwrite: true));

            AssertSucceeded(new PrefabCreateFromGameObjectOperation().Execute(context));

            Assert.That(AssetDatabase.AssetPathToGUID(TargetPath),
                Is.EqualTo(guidBefore), "an overwrite keeps the asset's guid");
            Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(source), Is.True);
        }

        // The §I.2 AssetPath(.prefab) extension and the §G7 path shape.
        [Test]
        public void CreateFromGameObject_BadPaths_Fail()
        {
            var source = Scratch("S");
            AssertFailed(
                new PrefabCreateFromGameObjectOperation().Execute(
                    CreateContext(CreateInputs(
                        source, "Assets/OpWeavePrefabContentsProbe/X.mat"))),
                "OW-UNITY-005");
            AssertFailed(
                new PrefabCreateFromGameObjectOperation().Execute(
                    CreateContext(CreateInputs(source, "../escape.prefab"))),
                "OW-PERM-002");
        }

        // The §F5 evidence row is recorded at Preflight.
        [Test]
        public void CreateFromGameObject_Preflight_RecordsEvidenceRow()
        {
            var source = Scratch("Src");
            var preflight = new PrefabCreateFromGameObjectOperation().Preflight(
                CreateContext(CreateInputs(source, TargetPath)));

            Assert.That(preflight.Diagnostics, Is.Empty);
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["path"], Is.EqualTo(TargetPath));
            Assert.That((bool?)snapshot["pathExisted"], Is.False);
            Assert.That((bool?)snapshot["connect"], Is.True);
            Assert.That((bool?)snapshot["overwrite"], Is.False);
            Assert.That(snapshot["before"], Is.Not.Null);
            Assert.That(snapshot["expected"], Is.Not.Null);
            Assert.That(snapshot["source"], Is.Not.Null);
            Assert.That(snapshot["sourcePrefabState"], Is.Not.Null);
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);
        }

        // ── unity.prefab.instantiate ─────────────────────────────────────

        // §G6 item 2: InstantiatePrefab at the scene root, output is a
        // PrefabInstanceRef resolving to the outermost instance.
        [Test]
        public void Instantiate_AtRoot_CreatesOutermostInstance()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab", "Child");
            var prefabRef = PrefabContentsFixture.PrefabRef(
                PrefabContentsFixture.GuidOf(
                    "Assets/OpWeavePrefabContentsProbe/Base.prefab"),
                "Assets/OpWeavePrefabContentsProbe/Base.prefab");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["prefab"] = InputValue.Present(prefabRef),
                },
                InstIn, InstOut);
            var rootsBefore = SceneManager.GetActiveScene().rootCount;

            AssertSucceeded(new PrefabInstantiateOperation().Execute(context));

            var instanceRef = (JObject)context.Outputs["instance"].ToToken();
            Assert.That((string?)instanceRef["kind"], Is.EqualTo("prefabInstance"));
            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["instance"]);
            Assert.That(resolved.Ok, Is.True);
            var instance = resolved.Value!;
            _scratch.Add(instance);
            Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(instance), Is.True);
            Assert.That(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance),
                Is.EqualTo("Assets/OpWeavePrefabContentsProbe/Base.prefab"));
            Assert.That(instance.transform.parent, Is.Null);
            Assert.That(SceneManager.GetActiveScene().rootCount,
                Is.EqualTo(rootsBefore + 1));
        }

        // parent + position: the instance lands under the parent at the
        // given local position.
        [Test]
        public void Instantiate_UnderParent_AppliesParentAndPosition()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var parent = Scratch("Parent");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["prefab"] = InputValue.Present(PrefabContentsFixture.PrefabRef(
                        PrefabContentsFixture.GuidOf(
                            "Assets/OpWeavePrefabContentsProbe/Base.prefab"),
                        "Assets/OpWeavePrefabContentsProbe/Base.prefab")),
                    ["parent"] = Ref(parent, "unity.GameObjectRef"),
                    ["position"] = Present("{\"x\":1,\"y\":2,\"z\":3}"),
                },
                InstIn, InstOut);

            AssertSucceeded(new PrefabInstantiateOperation().Execute(context));

            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["instance"]);
            Assert.That(resolved.Ok, Is.True);
            var instance = resolved.Value!;
            _scratch.Add(instance);
            Assert.That(instance.transform.parent, Is.EqualTo(parent.transform));
            Assert.That(instance.transform.localPosition,
                Is.EqualTo(new Vector3(1, 2, 3)));
        }

        // A non-Prefab reference fails §G2's own OW-UNITY-004 at the port.
        [Test]
        public void Instantiate_NonPrefabAsset_FailsOwUnity004()
        {
            var asset = ScriptableObject.CreateInstance<ProbeAsset>();
            AssetDatabase.CreateAsset(
                asset, "Assets/OpWeavePrefabContentsProbe/NotPrefab.asset");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["prefab"] = InputValue.Present(JsonValue.From(new JObject
                    {
                        ["kind"] = "prefabAsset",
                        ["guid"] = AssetDatabase.AssetPathToGUID(
                            "Assets/OpWeavePrefabContentsProbe/NotPrefab.asset"),
                        ["path"] = "Assets/OpWeavePrefabContentsProbe/NotPrefab.asset",
                    })),
                },
                InstIn, InstOut);

            var result = new PrefabInstantiateOperation().Execute(context);

            AssertFailed(result, "OW-UNITY-004");
        }

        // ── unity.prefab.create_variant ──────────────────────────────────

        // §G6 item 4: the Variant is built in a Preview Scene — the user's
        // scenes are never touched — and saved as a Variant of the base.
        [Test]
        public void CreateVariant_CreatesVariantLeavesUserScenesAlone()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab", "Child");
            var sceneStates = SceneStates();
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["base"] = InputValue.Present(PrefabContentsFixture.PrefabRef(
                        PrefabContentsFixture.GuidOf(
                            "Assets/OpWeavePrefabContentsProbe/Base.prefab"),
                        "Assets/OpWeavePrefabContentsProbe/Base.prefab")),
                    ["path"] = Present("\"" + VariantPath + "\""),
                },
                VarIn, VarOut);

            AssertSucceeded(new PrefabCreateVariantOperation().Execute(context));

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            Assert.That(variant, Is.Not.Null);
            Assert.That(PrefabUtility.GetPrefabAssetType(variant),
                Is.EqualTo(PrefabAssetType.Variant));
            var contents = PrefabUtility.LoadPrefabContents(VariantPath);
            try
            {
                Assert.That(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(contents),
                    Is.EqualTo("Assets/OpWeavePrefabContentsProbe/Base.prefab"),
                    "the Variant's base must be the referenced Prefab");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            Assert.That(context.Outputs.ContainsKey("variant"), Is.True);
            Assert.That((string?)((JObject)context.Outputs["variant"].ToToken())["kind"],
                Is.EqualTo("prefabAsset"));
            Assert.That(SceneStates(), Is.EqualTo(sceneStates),
                "create_variant must leave the user's scenes unmodified (§G6 item 4)");
        }

        // §I.2: an occupied variant path is OW-UNITY-005.
        [Test]
        public void CreateVariant_ExistingPath_FailsOwUnity005()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            PrefabContentsFixture.CreateProbePrefab("Variant.prefab");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["base"] = InputValue.Present(PrefabContentsFixture.PrefabRef(
                        PrefabContentsFixture.GuidOf(
                            "Assets/OpWeavePrefabContentsProbe/Base.prefab"),
                        "Assets/OpWeavePrefabContentsProbe/Base.prefab")),
                    ["path"] = Present("\"" + VariantPath + "\""),
                },
                VarIn, VarOut);

            var result = new PrefabCreateVariantOperation().Execute(context);

            AssertFailed(result, "OW-UNITY-005");
        }

        // Every loaded user scene's path, dirty flag and loaded state —
        // the "user scenes unmodified" evidence.
        private static List<string> SceneStates()
        {
            var states = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (EditorSceneManager.IsPreviewScene(scene))
                {
                    continue;
                }
                states.Add(scene.handle + "|" + scene.path + "|" + scene.isLoaded +
                    "|" + scene.isDirty);
            }
            return states;
        }
    }
}
