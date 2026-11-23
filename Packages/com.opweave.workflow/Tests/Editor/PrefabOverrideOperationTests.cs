#nullable enable

// T-W2.6-04 [Edit] tests (local:T-W2.6-04:prefab-override-ops): SPEC §G6
// items 5-7 and the §I.2 rows of the three Prefab-override Operations —
// the apply workflow (create → instantiate → component.add →
// apply_overrides leaves the Component inside the Prefab asset), revert
// and unpack normal/Undo/precondition cases, and the per-row outputs and
// §F7 change records.
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.Testing;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.6-04:prefab-override-ops — normal behavior, Undo and
    /// precondition violations of <c>unity.prefab.apply_overrides</c>,
    /// <c>unity.prefab.revert_overrides</c> and <c>unity.prefab.unpack</c>.
    /// </summary>
    internal sealed class PrefabOverrideOperationTests
    {
        private const string BasePath =
            "Assets/OpWeavePrefabContentsProbe/Base.prefab";
        private const string NestedPath =
            "Assets/OpWeavePrefabContentsProbe/NestedBase.prefab";
        private const string ContainerPath =
            "Assets/OpWeavePrefabContentsProbe/Container.prefab";
        private const string ChainPath =
            "Assets/OpWeavePrefabContentsProbe/Chain.prefab";

        private static readonly IReadOnlyDictionary<string, string> ApplyIn =
            new Dictionary<string, string>
            {
                ["instance"] = "unity.PrefabInstanceRef",
            };
        private static readonly IReadOnlyDictionary<string, string> ApplyOut =
            new Dictionary<string, string>
            {
                ["prefab"] = "unity.PrefabAssetRef",
            };

        private static readonly IReadOnlyDictionary<string, string> RevertIn =
            new Dictionary<string, string>
            {
                ["instance"] = "unity.PrefabInstanceRef",
            };
        private static readonly IReadOnlyDictionary<string, string> RevertOut =
            new Dictionary<string, string>
            {
                ["instance"] = "unity.PrefabInstanceRef",
            };

        private static readonly IReadOnlyDictionary<string, string> UnpackIn =
            new Dictionary<string, string>
            {
                ["instance"] = "unity.PrefabInstanceRef",
                ["mode"] = "core.String",
            };
        private static readonly IReadOnlyDictionary<string, string> UnpackOut =
            new Dictionary<string, string>
            {
                ["gameObject"] = "unity.GameObjectRef",
            };

        private static readonly IReadOnlyDictionary<string, string> AddIn =
            new Dictionary<string, string>
            {
                ["target"] = "unity.GameObjectRef",
                ["componentType"] = "unity.TypeName",
            };
        private static readonly IReadOnlyDictionary<string, string> AddOut =
            new Dictionary<string, string>
            {
                ["component"] = "unity.ComponentRef<unity.Component>",
            };

        private readonly List<GameObject> _scratch = new List<GameObject>();

        // The tests instantiate, apply, revert and unpack in the active
        // scene, which marks it dirty; one clean untitled scene is the
        // same starting state the harness fixtures use (the
        // PrefabCreateOperationTests pattern).
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PrefabContentsFixture.EnsureFolder(
                "Assets/OpWeavePrefabContentsProbe",
                "OpWeavePrefabContentsProbe");
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

        // An instance of the probe Prefab in the active scene, tracked for
        // teardown.
        private GameObject Instantiate(string assetPath) =>
            Instantiate(assetPath, SceneManager.GetActiveScene());

        // An instance in a specific scene — the harness's own scene for the
        // harness-driven cases: a run replaces the caller's clean untitled
        // scene before it executes, so an object the step resolves must
        // live in the scene the harness owns.
        private GameObject Instantiate(string assetPath, Scene scene)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(asset, "the probe Prefab must load: " + assetPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(
                asset!, scene);
            Assert.IsNotNull(instance, "the fixture instance must be created");
            _scratch.Add(instance!);
            return instance!;
        }

        // A container Prefab whose root carries a plain child and a nested
        // Prefab instance — the fixture the mode split is proven against.
        private GameObject CreateContainerPrefab()
        {
            PrefabContentsFixture.CreateProbePrefab("NestedBase.prefab");
            var baseAsset =
                AssetDatabase.LoadAssetAtPath<GameObject>(NestedPath);
            Assert.IsNotNull(baseAsset);
            var containerRoot = new GameObject("Container");
            _scratch.Add(containerRoot);
            var plain = new GameObject("Plain");
            plain.transform.SetParent(containerRoot.transform, false);
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(
                baseAsset!, containerRoot.scene);
            _scratch.Add(nested);
            nested.transform.SetParent(containerRoot.transform, false);
            var saved = PrefabUtility.SaveAsPrefabAsset(
                containerRoot, ContainerPath);
            Assert.IsNotNull(saved, "the container Prefab must save");
            return saved!;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> inputTypes,
            IReadOnlyDictionary<string, string> outputTypes,
            StepChangeRecorder? changes = null) =>
            new UnityStepContext(
                inputs,
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                changes ?? new StepChangeRecorder(0, "prefab-override"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static InputValue Present(string json)
        {
            var parsed = JsonValue.Parse(json);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + json);
            return InputValue.Present(parsed.Value!);
        }

        private static InputValue InstanceRef(GameObject instance) =>
            InputValue.Present(
                new RefResolver().ToRef(instance, "unity.PrefabInstanceRef"));

        private static Dictionary<string, InputValue> InstanceInputs(
            GameObject instance) =>
            new Dictionary<string, InputValue>
            {
                ["instance"] = InstanceRef(instance),
            };

        private static void AssertSucceeded(StepResult result) =>
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                result.Error is null
                    ? ""
                    : result.Error.Code + " " + result.Error.Message);

        private static void AssertFailed(StepResult result, string code)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error?.Code, Is.EqualTo(code),
                result.Error?.Message ?? "");
        }

        // ── unity.prefab.apply_overrides ────────────────────────────────

        // §G6 item 5: the documented apply workflow — create → instantiate
        // → component.add → apply_overrides — leaves the added Component
        // inside the Prefab asset.
        [Test]
        public void Apply_G6Workflow_ComponentReachesThePrefabAsset()
        {
            // create → instantiate (connect leaves the source an instance).
            var source = Scratch("Chain");
            var createContext = Context(
                new Dictionary<string, InputValue>
                {
                    ["source"] = InputValue.Present(
                        new RefResolver().ToRef(source, "unity.GameObjectRef")),
                    ["path"] = Present("\"" + ChainPath + "\""),
                },
                new Dictionary<string, string>
                {
                    ["source"] = "unity.GameObjectRef",
                    ["path"] = "unity.AssetPath",
                    ["connect"] = "core.Bool",
                    ["overwrite"] = "core.Bool",
                },
                new Dictionary<string, string>
                {
                    ["prefab"] = "unity.PrefabAssetRef",
                    ["instance"] = "unity.PrefabInstanceRef",
                });
            AssertSucceeded(
                new PrefabCreateFromGameObjectOperation().Execute(createContext));
            var instanceRef = createContext.Outputs["instance"];

            // component.add on the instance (an added-component override).
            var addContext = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(instanceRef),
                    ["componentType"] = Present("\"UnityEngine.Animator\""),
                },
                AddIn, AddOut);
            AssertSucceeded(new ComponentAddOperation().Execute(addContext));

            // apply_overrides: the override becomes part of the Prefab.
            var applyContext = Context(
                new Dictionary<string, InputValue>
                {
                    ["instance"] = InputValue.Present(instanceRef),
                },
                ApplyIn, ApplyOut);
            AssertSucceeded(
                new PrefabApplyOverridesOperation().Execute(applyContext));

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ChainPath);
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset!.GetComponent<Animator>(), Is.Not.Null,
                "the applied override must reach the Prefab asset (§G6 item 5)");
            var prefabRef = (JObject)applyContext.Outputs["prefab"].ToToken();
            Assert.That((string?)prefabRef["kind"], Is.EqualTo("prefabAsset"));
            Assert.That((string?)prefabRef["path"], Is.EqualTo(ChainPath));
        }

        // §I.2: the apply records asset.modify (undoable:false) and the
        // save — the only honest record of a destructive, no-Undo change.
        [Test]
        public void Apply_RecordsAssetModifyAndSave_NotUndoable()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var instance = Instantiate(BasePath);
            Undo.AddComponent<BoxCollider>(instance);
            var changes = new StepChangeRecorder(0, "apply");
            var context = Context(
                InstanceInputs(instance), ApplyIn, ApplyOut, changes);

            AssertSucceeded(new PrefabApplyOverridesOperation().Execute(context));

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(BasePath);
            Assert.That(asset!.GetComponent<BoxCollider>(), Is.Not.Null);
            Assert.That(changes.Records, Has.Count.EqualTo(2));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Modified));
            Assert.That(changes.Records[0].Undoable, Is.False,
                "an AutomatedAction apply offers no Undo (§I.2)");
            Assert.That(changes.Records[1].Action, Is.EqualTo(ChangeAction.Saved));
            Assert.That(changes.Records[1].Undoable, Is.False);
        }

        // Idempotent: an unchanged instance applies cleanly again.
        [Test]
        public void Apply_RerunOnUnchangedInstance_IsNoOpSuccess()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var instance = Instantiate(BasePath);
            var op = new PrefabApplyOverridesOperation();

            AssertSucceeded(op.Execute(
                Context(InstanceInputs(instance), ApplyIn, ApplyOut)));
            AssertSucceeded(op.Execute(
                Context(InstanceInputs(instance), ApplyIn, ApplyOut)));
        }

        // Precondition: the target must be an outermost Prefab instance.
        [Test]
        public void Apply_NotAPrefabInstance_FailsOwUnity004()
        {
            var plain = Scratch("Plain");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["instance"] = InputValue.Present(
                        new RefResolver().ToRef(plain, "unity.GameObjectRef")),
                },
                ApplyIn, ApplyOut);

            AssertFailed(
                new PrefabApplyOverridesOperation().Execute(context),
                "OW-UNITY-004");
        }

        // ── unity.prefab.revert_overrides ───────────────────────────────

        // §G6 item 6: revert removes every override — the property
        // modification, the added Component — and republishes the
        // instance. The property override goes on a child of the instance:
        // the root's own name is one of Unity's "default overrides" a
        // Revert-All deliberately keeps, not a revertible override.
        [Test]
        public void Revert_RemovesEveryOverrideAndRepublishesInstance()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab", "Child");
            var instance = Instantiate(BasePath);
            var child = instance.transform.Find("Child")!.gameObject;
            child.name = "Renamed";
            Undo.AddComponent<BoxCollider>(instance);
            var changes = new StepChangeRecorder(0, "revert");
            var context = Context(
                InstanceInputs(instance), RevertIn, RevertOut, changes);

            AssertSucceeded(new PrefabRevertOverridesOperation().Execute(context));

            Assert.That(child.name, Is.EqualTo("Child"),
                "the rename override must be reverted");
            Assert.That(instance.GetComponent<BoxCollider>(), Is.Null,
                "the added-component override must be reverted");
            Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(instance),
                Is.True, "the object stays a Prefab instance");
            var output = (JObject)context.Outputs["instance"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("prefabInstance"));
            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["instance"]);
            Assert.That(resolved.Ok && ReferenceEquals(resolved.Value, instance),
                Is.True);
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Modified));
            Assert.That(changes.Records[0].Undoable, Is.True);
        }

        // §I.2 Undo Full: one Undo restores the overrides. The instance
        // lives in the harness's own scene — a run replaces a clean caller
        // scene before it executes — and the override goes on the child,
        // not the root's default-override name.
        [Test]
        public void Revert_OneUndoRestoresTheOverrides()
        {
            using var harness = OperationTestHarness.Create();
            var anchor = HarnessObjects.NewGameObject(harness, "Anchor");
            PrefabContentsFixture.CreateProbePrefab("Base.prefab", "Child");
            var instance = Instantiate(BasePath, anchor.scene);
            var child = instance.transform.Find("Child")!.gameObject;
            child.name = "Renamed";
            var undoGroup = Undo.GetCurrentGroup();
            try
            {
                var result = harness.Run(
                    PrefabRevertOverridesOperation.OperationId,
                    new { instance });

                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(child.name, Is.EqualTo("Child"));

                Undo.PerformUndo();

                Assert.That(child.name, Is.EqualTo("Renamed"),
                    "one Undo must restore the reverted override");
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        // Precondition: the target must be an outermost Prefab instance.
        [Test]
        public void Revert_NotAPrefabInstance_FailsOwUnity004()
        {
            var plain = Scratch("Plain");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["instance"] = InputValue.Present(
                        new RefResolver().ToRef(plain, "unity.GameObjectRef")),
                },
                RevertIn, RevertOut);

            AssertFailed(
                new PrefabRevertOverridesOperation().Execute(context),
                "OW-UNITY-004");
        }

        // ── unity.prefab.unpack ─────────────────────────────────────────

        // §G6 item 7, mode "outermost" (the default): only the target's
        // link goes — deeper nested instances keep theirs — and the root
        // republishes as a plain GameObjectRef.
        [Test]
        public void Unpack_DefaultOutermost_FreesOnlyTheTargetInstance()
        {
            CreateContainerPrefab();
            var instance = Instantiate(ContainerPath);
            var changes = new StepChangeRecorder(0, "unpack");
            var context = Context(
                InstanceInputs(instance), UnpackIn, UnpackOut, changes);

            AssertSucceeded(new PrefabUnpackOperation().Execute(context));

            Assert.That(PrefabUtility.IsPartOfPrefabInstance(instance),
                Is.False, "the target root must no longer be an instance");
            var nested = instance.transform.Find("NestedBase");
            Assert.That(nested, Is.Not.Null);
            Assert.That(
                PrefabUtility.IsAnyPrefabInstanceRoot(nested!.gameObject),
                Is.True, "the nested instance must keep its link");
            Assert.That(
                PrefabUtility.IsPartOfPrefabInstance(nested.gameObject),
                Is.True);
            var plain = instance.transform.Find("Plain");
            Assert.That(plain, Is.Not.Null);
            Assert.That(
                PrefabUtility.IsPartOfPrefabInstance(plain!.gameObject),
                Is.False, "the plain child is freed with its root");
            var output = (JObject)context.Outputs["gameObject"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("gameObject"));
            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["gameObject"]);
            Assert.That(resolved.Ok && ReferenceEquals(resolved.Value, instance),
                Is.True);
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Modified));
            Assert.That(changes.Records[0].Undoable, Is.True);
        }

        // §I.2, mode "completely": every nested link goes too.
        [Test]
        public void Unpack_Completely_FreesTheWholeHierarchy()
        {
            CreateContainerPrefab();
            var instance = Instantiate(ContainerPath);
            var inputs = InstanceInputs(instance);
            inputs["mode"] = Present("\"completely\"");
            var context = Context(inputs, UnpackIn, UnpackOut);

            AssertSucceeded(new PrefabUnpackOperation().Execute(context));

            foreach (var transform in instance.GetComponentsInChildren<Transform>())
            {
                Assert.That(
                    PrefabUtility.IsPartOfPrefabInstance(transform.gameObject),
                    Is.False,
                    transform.name + " must stand outside any Prefab instance");
            }
        }

        // §I.2 Undo Full: one Undo restores the instance link.
        [Test]
        public void Unpack_OneUndoRestoresTheInstance()
        {
            using var harness = OperationTestHarness.Create();
            var anchor = HarnessObjects.NewGameObject(harness, "Anchor");
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var instance = Instantiate(BasePath, anchor.scene);
            var undoGroup = Undo.GetCurrentGroup();
            try
            {
                var result = harness.Run(
                    PrefabUnpackOperation.OperationId,
                    new { instance });

                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(instance),
                    Is.False);

                Undo.PerformUndo();

                Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(instance),
                    Is.True, "one Undo must restore the Prefab instance");
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        // Precondition: the target must be an outermost Prefab instance.
        [Test]
        public void Unpack_NotAPrefabInstance_FailsOwUnity004()
        {
            var plain = Scratch("Plain");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["instance"] = InputValue.Present(
                        new RefResolver().ToRef(plain, "unity.GameObjectRef")),
                },
                UnpackIn, UnpackOut);

            AssertFailed(
                new PrefabUnpackOperation().Execute(context),
                "OW-UNITY-004");
        }

        // The declared enum is a hard boundary at execution too — a value
        // outside it violates the port's constraint (OW-OP-006).
        [Test]
        public void Unpack_InvalidMode_FailsOwOp006()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var instance = Instantiate(BasePath);
            var inputs = InstanceInputs(instance);
            inputs["mode"] = Present("\"nested\"");
            var context = Context(inputs, UnpackIn, UnpackOut);

            AssertFailed(new PrefabUnpackOperation().Execute(context), "OW-OP-006");
            Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(instance),
                Is.True, "a rejected mode changes nothing");
        }
    }
}
