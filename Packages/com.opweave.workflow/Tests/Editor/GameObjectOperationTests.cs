#nullable enable

// T-W2.5-02 [Edit] tests: local:T-W2.5-02:gameobject-ops — the G5 triad
// (success, single Undo, precondition violation) for the five §I.1
// GameObject Operations through a real UnityStepContext over the standard
// type registry: unity.gameobject.create (parent/scene fallback, primitive,
// position), unity.gameobject.find (path or name required; not exactly one
// match → OW-UNITY-001/002), unity.gameobject.delete, unity.gameobject.
// set_parent (cycle → OW-UNITY-007), unity.transform.set (≥1 field; scale
// always local).
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
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
    /// <summary>Per-Operation behaviour of the §I.1 GameObject Operations.</summary>
    internal sealed class GameObjectOperationTests
    {
        private static readonly IReadOnlyDictionary<string, string> CreateIn =
            new Dictionary<string, string>
            {
                ["name"] = "core.String",
                ["scene"] = "unity.SceneRef",
                ["parent"] = "unity.GameObjectRef",
                ["primitive"] = "core.String",
                ["position"] = "unity.Vector3",
            };

        private static readonly IReadOnlyDictionary<string, string> FindIn =
            new Dictionary<string, string>
            {
                ["scene"] = "unity.SceneRef",
                ["path"] = "unity.HierarchyPath",
                ["name"] = "core.String",
                ["tag"] = "core.String",
            };

        private static readonly IReadOnlyDictionary<string, string> GoIn =
            new Dictionary<string, string> { ["target"] = "unity.GameObjectRef" };

        private static readonly IReadOnlyDictionary<string, string> GoOut =
            new Dictionary<string, string> { ["gameObject"] = "unity.GameObjectRef" };

        private static readonly IReadOnlyDictionary<string, string> SetParentIn =
            new Dictionary<string, string>
            {
                ["target"] = "unity.GameObjectRef",
                ["parent"] = "unity.GameObjectRef",
                ["worldPositionStays"] = "core.Bool",
            };

        private static readonly IReadOnlyDictionary<string, string> TransformIn =
            new Dictionary<string, string>
            {
                ["target"] = "unity.GameObjectRef",
                ["position"] = "unity.Vector3",
                ["rotation"] = "unity.Euler",
                ["scale"] = "unity.Vector3",
                ["space"] = "core.String",
            };

        private static readonly IReadOnlyDictionary<string, string> NoPorts =
            new Dictionary<string, string>();

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
            IChangeRecorder? changes = null,
            IReadOnlyDictionary<string, string>? pending = null) =>
            new UnityStepContext(
                inputs,
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                changes ?? new StepChangeRecorder(0, "go"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                pendingProducers: pending);

        private static JsonValue GoRef(GameObject go) =>
            new RefResolver().ToRef(go, "unity.GameObjectRef");

        private static JsonValue SceneRef(Scene scene) =>
            JsonValue.From(new JObject
            {
                ["kind"] = "scene",
                ["path"] = scene.path,
                ["guid"] = AssetDatabase.AssetPathToGUID(scene.path),
            });

        // The integer a snapshot's scene "handle" member carries on this
        // Editor generation — the same raw value GameObjectEvidence.SceneKey
        // writes: the int Scene.handle below 6000.5, the SceneHandle's raw
        // ulong held bit-exact in an Int64 on 6000.5+ (L-04).
        private static long RawHandleOf(Scene scene)
        {
#if UNITY_6000_5_OR_NEWER
            return unchecked((long)scene.handle.GetRawData());
#else
            return scene.handle;
#endif
        }

        private static GameObject NewGo(
            OperationTestHarness harness, string name, Vector3? position = null)
        {
            var created = HarnessObjects.NewGameObject(harness, name);
            if (position is { } at)
            {
                created.transform.position = at;
            }
            return created;
        }

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);

        // #257 / SPEC §D8: pending producers are counted as present,
        // but Get<T> on them is a programming error. Preflight must
        // not read those values before the producing step runs.
        [Test]
        public void TransformSet_Preflight_PendingTargetDoesNotThrow()
        {
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["position"] = InputValue.Present(
                        Json("{\"x\":1,\"y\":2,\"z\":3}")),
                },
                TransformIn, NoPorts,
                pending: new Dictionary<string, string>
                {
                    ["target"] = "upstream-target",
                });

            Assert.That(context.IsPending("target"), Is.True);
            Assert.That(context.IsPresent("target"), Is.True);

            var result = new TransformSetOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
        }

        [Test]
        public void Find_Preflight_PendingSceneDoesNotThrow()
        {
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"Subject\"")),
                },
                FindIn, GoOut,
                pending: new Dictionary<string, string>
                {
                    ["scene"] = "upstream-scene",
                });

            Assert.That(context.IsPending("scene"), Is.True);
            Assert.That(context.IsPresent("scene"), Is.True);

            var result = new GameObjectFindOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
        }

        [Test]
        public void TransformSet_Preflight_PendingSpaceDoesNotThrow()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "PendingSpace");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(target)),
                    ["position"] = InputValue.Present(
                        Json("{\"x\":4,\"y\":0,\"z\":0}")),
                },
                TransformIn, NoPorts,
                pending: new Dictionary<string, string>
                {
                    ["space"] = "upstream-space",
                });

            Assert.That(context.IsPending("space"), Is.True);
            Assert.That(context.IsPresent("space"), Is.True);

            var result = new TransformSetOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
        }

        // The !IsPending guard skips only the read it cannot answer; the
        // concrete-value diagnostics the same paths already produced are
        // unchanged (Issue #257).
        [Test]
        public void TransformSet_Preflight_ConcreteInvalidSpaceReportsOwOp006()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "BadSpaceTarget");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(target)),
                    ["position"] = InputValue.Present(
                        Json("{\"x\":0,\"y\":0,\"z\":0}")),
                    ["space"] = InputValue.Present(Json("\"diagonal\"")),
                },
                TransformIn, NoPorts);

            var result = new TransformSetOperation().Preflight(context);

            Assert.That(result.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_OP_006));
            Assert.That(result.Diagnostics[0].Port, Is.EqualTo("space"));
        }

        [Test]
        public void TransformSet_Preflight_UnresolvableTargetReportsOwUnity001()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "VanishedPreflight");
            var reference = GoRef(target);
            UnityEngine.Object.DestroyImmediate(target);
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(reference),
                    ["position"] = InputValue.Present(
                        Json("{\"x\":0,\"y\":0,\"z\":0}")),
                },
                TransformIn, NoPorts);

            var result = new TransformSetOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Not.Empty);
            Assert.That(result.Diagnostics[0].Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_001));
            Assert.That(result.Diagnostics[0].Port, Is.EqualTo("target"));
        }

        [Test]
        public void Find_Preflight_UnresolvableSceneReportsOwUnity003()
        {
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"Subject\"")),
                    ["scene"] = InputValue.Present(
                        Json("{\"kind\":\"scene\",\"path\":\"Assets/NoSuch.unity\"}")),
                },
                FindIn, GoOut);

            var result = new GameObjectFindOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Not.Empty);
            Assert.That(result.Diagnostics[0].Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_003));
            Assert.That(result.Diagnostics[0].Port, Is.EqualTo("scene"));
        }

        // ── unity.gameobject.create ─────────────────────────────────────

        [Test]
        public void Create_CreatesNamedObjectAtSceneRootAndOutputsItsRef()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var changes = new StepChangeRecorder(0, "go");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"Root\"")),
                },
                CreateIn, GoOut, changes);

            var result = new GameObjectCreateOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var created = FindByName("Root");
            Assert.That(created, Is.Not.Null);
            Assert.That(created!.transform.parent, Is.Null, "no parent means the scene root");
            Assert.That(created.transform.localPosition, Is.EqualTo(Vector3.zero));
            var output = (JObject)context.Outputs["gameObject"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("gameObject"));
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Undoable, Is.True);
        }

        [Test]
        public void Create_CreatesNamedPrimitiveUnderParentAtPosition()
        {
            using var harness = OperationTestHarness.Create();
            var parent = NewGo(harness, "Parent");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"Ball\"")),
                    ["parent"] = InputValue.Present(GoRef(parent)),
                    ["primitive"] = InputValue.Present(Json("\"sphere\"")),
                    ["position"] = InputValue.Present(Json("{\"x\":1,\"y\":2,\"z\":3}")),
                },
                CreateIn, GoOut);

            var result = new GameObjectCreateOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var created = FindByName("Ball");
            Assert.That(created, Is.Not.Null);
            Assert.That(created!.transform.parent, Is.SameAs(parent.transform));
            Assert.That(created.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(created.GetComponent<MeshFilter>(), Is.Not.Null);
            Assert.That(created.GetComponent<MeshRenderer>(), Is.Not.Null);
            Assert.That(created.GetComponent<SphereCollider>(), Is.Not.Null);
        }

        [Test]
        public void Create_CreatesInTheNamedScene()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var scene = SceneManager.GetSceneByPath(harness.TempScenePath);
            Assert.That(scene.isLoaded, Is.True, "the harness scene must be open");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"Scened\"")),
                    ["scene"] = InputValue.Present(SceneRef(scene)),
                },
                CreateIn, GoOut);

            var result = new GameObjectCreateOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var created = FindByName("Scened");
            Assert.That(created, Is.Not.Null);
            Assert.That(created!.scene.handle, Is.EqualTo(scene.handle));
        }

        [Test]
        public void Create_OneUndoRemovesTheCreatedObject()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var undoGroup = Undo.GetCurrentGroup();
            var result = harness.Run(
                GameObjectCreateOperation.OperationId, new { name = "UndoMe" });
            try
            {
                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(FindByName("UndoMe"), Is.Not.Null);

                Undo.PerformUndo();

                Assert.That(FindByName("UndoMe"), Is.Null,
                    "one Undo must remove the created object");
            }
            finally
            {
                // Whatever the assertions found, the Editor's Undo stack
                // returns to the group the test started at — a leaked group
                // is shared state the next Undo test does not own.
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        [Test]
        public void Create_ParentInAnotherSceneFailsWithOwUnity002()
        {
            using var harness = OperationTestHarness.Create();
            var parent = NewGo(harness, "Parent");
            var other = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                // The unsaved additive scene has no path or guid: the §G2
                // SceneRef names it by its loaded name instead.
                var context = Context(
                    new Dictionary<string, InputValue>
                    {
                        ["name"] = InputValue.Present(Json("\"X\"")),
                        ["scene"] = InputValue.Present(Json("\"" + other.name + "\"")),
                        ["parent"] = InputValue.Present(GoRef(parent)),
                    },
                    CreateIn, GoOut);

                var result = new GameObjectCreateOperation().Execute(context);

                AssertFailed(result, ErrorCodes.OW_UNITY_002, "parent");
                Assert.That(FindByName("X"), Is.Null, "a refused create leaves nothing behind");
            }
            finally
            {
                EditorSceneManager.CloseScene(other, true);
            }
        }

        [Test]
        public void Create_ParentAloneInNonActiveSceneUsesTheParentsScene()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var wasActive = SceneManager.GetActiveScene();
            var other = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                // Unity 6000.0 activates the scene a NewScene call creates —
                // the fixture needs the parent alone in a NON-active scene,
                // so the previously active scene is put back first.
                SceneManager.SetActiveScene(wasActive);
                Assert.That(other.handle,
                    Is.Not.EqualTo(SceneManager.GetActiveScene().handle),
                    "the additive scene must not be the active scene");
                var parent = new GameObject("ElsewhereParent");
                SceneManager.MoveGameObjectToScene(parent, other);
                var context = Context(
                    new Dictionary<string, InputValue>
                    {
                        ["name"] = InputValue.Present(Json("\"OffScene\"")),
                        ["parent"] = InputValue.Present(GoRef(parent)),
                    },
                    CreateIn, GoOut);

                var result = new GameObjectCreateOperation().Execute(context);

                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                    () => Describe(result));
                Assert.That(parent.transform.childCount, Is.EqualTo(1));
                var created = parent.transform.GetChild(0).gameObject;
                Assert.That(created.scene.handle, Is.EqualTo(other.handle));
            }
            finally
            {
                EditorSceneManager.CloseScene(other, true);
            }
        }

        [Test]
        public void Create_Preflight_ParentAloneInNonActiveSceneSnapshotsItsScene()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var other = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var parent = new GameObject("SnapParent");
                SceneManager.MoveGameObjectToScene(parent, other);
                var context = Context(
                    new Dictionary<string, InputValue>
                    {
                        ["name"] = InputValue.Present(Json("\"Shot\"")),
                        ["parent"] = InputValue.Present(GoRef(parent)),
                    },
                    CreateIn, GoOut);

                var result = new GameObjectCreateOperation().Preflight(context);

                Assert.That(result.Diagnostics, Is.Empty,
                    "a parent alone selects its own scene (F3)");
                var scene = (JObject)result.Snapshot.ToToken()["scene"]!;
                Assert.That((long?)scene["handle"], Is.EqualTo(RawHandleOf(other)));
            }
            finally
            {
                EditorSceneManager.CloseScene(other, true);
            }
        }

        [Test]
        public void Create_UnknownPrimitiveFailsWithOwOp006()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"X\"")),
                    ["primitive"] = InputValue.Present(Json("\"torus\"")),
                },
                CreateIn, GoOut);

            var result = new GameObjectCreateOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_OP_006, "primitive");
            Assert.That(FindByName("X"), Is.Null);
        }

        // ── unity.gameobject.find ───────────────────────────────────────

        [Test]
        public void Find_ByNameReturnsTheSingleMatch()
        {
            using var harness = OperationTestHarness.Create();
            var wanted = NewGo(harness, "UniqueFindName42");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"UniqueFindName42\"")),
                },
                FindIn, GoOut);

            var result = new GameObjectFindOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var output = (JObject)context.Outputs["gameObject"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("gameObject"));
            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["gameObject"]);
            Assert.That(resolved.Ok, Is.True);
            Assert.That(resolved.Value, Is.SameAs(wanted));
        }

        [Test]
        public void Find_ByPathReturnsTheMatchInTheNamedScene()
        {
            using var harness = OperationTestHarness.Create();
            var parent = NewGo(harness, "PathParent");
            var child = new GameObject("PathChild");
            child.transform.SetParent(parent.transform, false);
            var scene = SceneManager.GetSceneByPath(harness.TempScenePath);
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["scene"] = InputValue.Present(SceneRef(scene)),
                    ["path"] = InputValue.Present(Json("\"/PathParent/PathChild\"")),
                },
                FindIn, GoOut);

            var result = new GameObjectFindOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["gameObject"]);
            Assert.That(resolved.Value, Is.SameAs(child));
        }

        [Test]
        public void Find_PathScopedToAnUnsavedSceneMatchesOnlyInsideIt()
        {
            using var harness = OperationTestHarness.Create();
            // The identical hierarchy path lives in the saved harness scene
            // and in a second, unsaved scene. The explicit unsaved-scene
            // input — supplied by name, the only SceneRef key an unsaved
            // scene has — scopes the walk, so the saved scene's twin can
            // neither shadow the match nor make it ambiguous (F5).
            var savedParent = NewGo(harness, "Twin");
            var savedLeaf = new GameObject("Leaf");
            savedLeaf.transform.SetParent(savedParent.transform, false);
            var other = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var otherParent = new GameObject("Twin");
                var otherLeaf = new GameObject("Leaf");
                otherLeaf.transform.SetParent(otherParent.transform, false);
                SceneManager.MoveGameObjectToScene(otherParent, other);
                var context = Context(
                    new Dictionary<string, InputValue>
                    {
                        ["scene"] = InputValue.Present(Json("\"" + other.name + "\"")),
                        ["path"] = InputValue.Present(Json("\"/Twin/Leaf\"")),
                    },
                    FindIn, GoOut);

                var result = new GameObjectFindOperation().Execute(context);

                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                    () => Describe(result));
                var resolved = new RefResolver().Resolve<GameObject>(
                    context.Outputs["gameObject"]);
                Assert.That(resolved.Ok, Is.True);
                Assert.That(resolved.Value, Is.SameAs(otherLeaf));
            }
            finally
            {
                EditorSceneManager.CloseScene(other, true);
            }
        }

        [Test]
        public void Find_NameAndTagMustBothMatch()
        {
            using var harness = OperationTestHarness.Create();
            var wanted = NewGo(harness, "TaggedFindName77");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"TaggedFindName77\"")),
                    ["tag"] = InputValue.Present(Json("\"NoSuchOpWeaveTag\"")),
                },
                FindIn, GoOut);

            var result = new GameObjectFindOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_001, null);
        }

        [Test]
        public void Find_NoMatchIsOwUnity001()
        {
            using var harness = OperationTestHarness.Create();
            NewGo(harness, "SomethingElse");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"NoObjectHasThisName55\"")),
                },
                FindIn, GoOut);

            var result = new GameObjectFindOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_001, null);
        }

        [Test]
        public void Find_SeveralMatchesIsOwUnity002()
        {
            using var harness = OperationTestHarness.Create();
            var first = NewGo(harness, "DupFindName88");
            var second = new GameObject("DupFindName88");
            second.transform.SetParent(first.transform, false);
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["name"] = InputValue.Present(Json("\"DupFindName88\"")),
                },
                FindIn, GoOut);

            var result = new GameObjectFindOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_002, null);
        }

        [Test]
        public void Find_NeitherPathNorNameIsOwOp006()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["tag"] = InputValue.Present(Json("\"Untagged\"")),
                },
                FindIn, GoOut);

            var result = new GameObjectFindOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_OP_006, null);
        }

        [Test]
        public void Find_UnresolvablePathSegmentIsOwUnity001()
        {
            using var harness = OperationTestHarness.Create();
            NewGo(harness, "OnlyRoot");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(Json("\"/OnlyRoot/MissingChild\"")),
                },
                FindIn, GoOut);

            var result = new GameObjectFindOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_001, null);
        }

        // ── unity.gameobject.delete ─────────────────────────────────────

        [Test]
        public void Delete_RemovesTheTargetAndRecordsTheDeletion()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "DeleteMe");
            var changes = new StepChangeRecorder(0, "go");
            var context = Context(
                new Dictionary<string, InputValue> { ["target"] = InputValue.Present(GoRef(target)) },
                GoIn, NoPorts, changes);

            var result = new GameObjectDeleteOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(FindByName("DeleteMe"), Is.Null);
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Deleted));
            Assert.That(changes.Records[0].Undoable, Is.True);
        }

        [Test]
        public void Delete_OneUndoRestoresTheTarget()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var target = NewGo(harness, "UndoDelete");
            var undoGroup = Undo.GetCurrentGroup();
            var result = harness.Run(
                GameObjectDeleteOperation.OperationId, new { target = target });
            try
            {
                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(FindByName("UndoDelete"), Is.Null);

                Undo.PerformUndo();

                Assert.That(FindByName("UndoDelete"), Is.Not.Null,
                    "one Undo must restore the deleted object");
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        [Test]
        public void Delete_UnresolvableTargetFailsWithOwUnity001()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "Gone");
            var reference = GoRef(target);
            UnityEngine.Object.DestroyImmediate(target);

            // The step path the Runner drives: Execute is allowed to let the
            // context's StepContextException cross the boundary (SPEC §0.6
            // SG-06), where the run loop — and the harness that mirrors it —
            // turns it into the step's own OW-UNITY-001 diagnostic (§E5).
            var result = harness.Run(
                GameObjectDeleteOperation.OperationId, new { target = reference });

            Assert.That(result.StepState, Is.EqualTo(StepState.Failed),
                () => result.ToString());
            Assert.That(result.Diagnostics[0].Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        // ── unity.gameobject.set_parent ─────────────────────────────────

        [Test]
        public void SetParent_ReparentsKeepingWorldPosition()
        {
            using var harness = OperationTestHarness.Create();
            var parent = NewGo(harness, "NewParent", new Vector3(5, 0, 0));
            var child = NewGo(harness, "MovingChild", new Vector3(1, 2, 3));
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(child)),
                    ["parent"] = InputValue.Present(GoRef(parent)),
                },
                SetParentIn, GoOut);

            var result = new GameObjectSetParentOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(child.transform.parent, Is.SameAs(parent.transform));
            Assert.That(child.transform.position, Is.EqualTo(new Vector3(1, 2, 3)),
                "worldPositionStays defaults to true");
            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["gameObject"]);
            Assert.That(resolved.Value, Is.SameAs(child));
        }

        [Test]
        public void SetParent_NullParentMovesToSceneRoot()
        {
            using var harness = OperationTestHarness.Create();
            var parent = NewGo(harness, "OldParent");
            var child = new GameObject("Rooted");
            child.transform.SetParent(parent.transform, false);
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(child)),
                },
                SetParentIn, GoOut);

            var result = new GameObjectSetParentOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(child.transform.parent, Is.Null);
        }

        [Test]
        public void SetParent_AlreadyParentedSucceedsWithoutChange()
        {
            using var harness = OperationTestHarness.Create();
            var parent = NewGo(harness, "SameParent");
            var child = new GameObject("Still");
            child.transform.SetParent(parent.transform, false);
            var changes = new StepChangeRecorder(0, "go");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(child)),
                    ["parent"] = InputValue.Present(GoRef(parent)),
                },
                SetParentIn, GoOut, changes);

            var result = new GameObjectSetParentOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(changes.Records, Is.Empty, "already parented is the idempotent no-op");
        }

        [Test]
        public void SetParent_OneUndoRestoresTheOldParent()
        {
            using var harness = OperationTestHarness.Create();
            var parent = NewGo(harness, "UndoParent");
            var child = NewGo(harness, "UndoChild");
            var undoGroup = Undo.GetCurrentGroup();
            var result = harness.Run(
                GameObjectSetParentOperation.OperationId,
                new { target = child, parent = parent });
            try
            {
                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(child.transform.parent, Is.SameAs(parent.transform));

                Undo.PerformUndo();

                Assert.That(child.transform.parent, Is.Null,
                    "one Undo must restore the old parent");
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        [Test]
        public void SetParent_CycleFailsWithOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var ancestor = NewGo(harness, "Ancestor");
            var descendant = new GameObject("Descendant");
            descendant.transform.SetParent(ancestor.transform, false);
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(ancestor)),
                    ["parent"] = InputValue.Present(GoRef(descendant)),
                },
                SetParentIn, GoOut);

            var result = new GameObjectSetParentOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_007, "parent");
            Assert.That(descendant.transform.parent, Is.SameAs(ancestor.transform),
                "a refused reparent changes nothing");
        }

        [Test]
        public void SetParent_SelfParentFailsWithOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "SelfParent");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(target)),
                    ["parent"] = InputValue.Present(GoRef(target)),
                },
                SetParentIn, GoOut);

            var result = new GameObjectSetParentOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_007, "parent");
        }

        // ── unity.transform.set ─────────────────────────────────────────

        [Test]
        public void TransformSet_SetsLocalPositionRotationAndScale()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "Transformed");
            var changes = new StepChangeRecorder(0, "go");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(target)),
                    ["position"] = InputValue.Present(Json("{\"x\":1,\"y\":2,\"z\":3}")),
                    ["rotation"] = InputValue.Present(Json("{\"x\":0,\"y\":90,\"z\":0}")),
                    ["scale"] = InputValue.Present(Json("{\"x\":2,\"y\":2,\"z\":2}")),
                },
                TransformIn, NoPorts, changes);

            var result = new TransformSetOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(target.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(target.transform.localEulerAngles.y, Is.EqualTo(90).Within(0.001f));
            Assert.That(target.transform.localScale, Is.EqualTo(new Vector3(2, 2, 2)));
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Modified));
            Assert.That(changes.Records[0].Undoable, Is.True);
        }

        [Test]
        public void TransformSet_WorldSpaceSetsWorldTransformButScaleStaysLocal()
        {
            using var harness = OperationTestHarness.Create();
            var parent = NewGo(harness, "ScaledParent");
            parent.transform.localScale = new Vector3(2, 2, 2);
            var child = new GameObject("WorldSet");
            child.transform.SetParent(parent.transform, false);
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(child)),
                    ["position"] = InputValue.Present(Json("{\"x\":4,\"y\":0,\"z\":0}")),
                    ["scale"] = InputValue.Present(Json("{\"x\":3,\"y\":3,\"z\":3}")),
                    ["space"] = InputValue.Present(Json("\"world\"")),
                },
                TransformIn, NoPorts);

            var result = new TransformSetOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(child.transform.position, Is.EqualTo(new Vector3(4, 0, 0)),
                "space=world applies to position");
            Assert.That(child.transform.localScale, Is.EqualTo(new Vector3(3, 3, 3)),
                "scale is always local, whatever space says");
        }

        [Test]
        public void TransformSet_OneUndoRestoresTheTransform()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "UndoTransform", new Vector3(1, 1, 1));
            var undoGroup = Undo.GetCurrentGroup();
            var result = harness.Run(
                TransformSetOperation.OperationId,
                new { target = target, position = Json("{\"x\":9,\"y\":9,\"z\":9}") });
            try
            {
                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(target.transform.localPosition, Is.EqualTo(new Vector3(9, 9, 9)));

                Undo.PerformUndo();

                Assert.That(target.transform.localPosition, Is.EqualTo(new Vector3(1, 1, 1)),
                    "one Undo must restore the transform");
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        [Test]
        public void TransformSet_NoFieldFailsWithOwOp006()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "NoFields");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(target)),
                },
                TransformIn, NoPorts);

            var result = new TransformSetOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_OP_006, null);
            Assert.That(target.transform.localPosition, Is.EqualTo(Vector3.zero),
                "a refused set changes nothing");
        }

        [Test]
        public void TransformSet_UnresolvableTargetFailsWithOwUnity001()
        {
            using var harness = OperationTestHarness.Create();
            var target = NewGo(harness, "Vanished");
            var reference = GoRef(target);
            UnityEngine.Object.DestroyImmediate(target);

            // The same step path as Delete_UnresolvableTargetFailsWithOwUnity001:
            // the unresolvable Ref input crosses the step boundary as the
            // step's OW-UNITY-001 diagnostic (SPEC §E5, §0.6 SG-06).
            var result = harness.Run(
                TransformSetOperation.OperationId,
                new { target = reference, position = Json("{\"x\":0,\"y\":0,\"z\":0}") });

            Assert.That(result.StepState, Is.EqualTo(StepState.Failed),
                () => result.ToString());
            Assert.That(result.Diagnostics[0].Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_001));
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static GameObject? FindByName(string name)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = FindInChildren(root.transform, name);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
            return null;
        }

        private static GameObject? FindInChildren(Transform node, string name)
        {
            if (node.name == name)
            {
                return node.gameObject;
            }
            for (var i = 0; i < node.childCount; i++)
            {
                var found = FindInChildren(node.GetChild(i), name);
                if (found is not null)
                {
                    return found;
                }
            }
            return null;
        }

        private static void AssertFailed(StepResult result, string code, string? port)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed), () => Describe(result));
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(code), () => Describe(result));
            if (port is not null)
            {
                Assert.That(result.Error.Port, Is.EqualTo(port));
            }
        }
    }
}
