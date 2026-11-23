#nullable enable

// T-W4.5-01 [Edit] tests: local:T-W4.5-01:at02-scene-gameobject — the SPEC
// §T.3 AT-02 contract applied to every standard Operation of the SPEC §I.1
// common / Scene / GameObject catalog group through the OpWeave.Testing
// §M.6 item 2 assertions:
//
//   * AssertDescriptorValid — the §C3 registration checks pass with no
//     OW-OP-003 violation (SPEC §C1 test 1);
//   * AssertIdempotency — per the declaration: an Idempotent Operation
//     runs twice on the same inputs with an identical world and equal
//     outputs; a Reconcilable Operation is judged through RunInterrupted
//     at both §F5 crash windows, Unknown is accepted as the safe verdict,
//     an evidence-less Completed fails, and the damaged-evidence probe
//     admits Unknown only;
//   * AssertUndo — one Undo reverts a step whose declaration is Full;
//   * AssertDeclaredEffectsOnly — no recorded or observed change outside
//     the declared §C4 vocabulary.
//
// Every input is a deterministic fixture this file builds: a saved clean
// base scene, saved empty scene assets, and uniquely named GameObjects in
// the fixture harness's own saved scene — a second harness refuses to
// open its scene beside unsaved changes, so every factory re-saves. The
// Runner half of §F5 — a Run ending at a bare step_started whose evidence
// no longer holds must end NeedsReview, never an automatic success or
// re-execution — lives in At02SceneGameObjectRecoveryTests. This file
// closes with the deliberately weakened Reconcile fixture the ticket
// requires the contract to catch.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
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
    /// <c>local:T-W4.5-01:at02-scene-gameobject</c>: the AT-02 contract suite
    /// for the SPEC §I.1 common / Scene / GameObject group.
    /// </summary>
    internal sealed class At02SceneGameObjectContractTests
    {
        // The SPEC §I.1 catalog group, verbatim and in table order — the
        // complete exercised list the AT-02 pass condition requires with
        // none skipped.
        private static readonly string[] s_specGroup =
        {
            "core.start",
            OpenSceneOperation.OperationId,
            CreateSceneOperation.OperationId,
            SaveSceneOperation.OperationId,
            CloseSceneOperation.OperationId,
            GameObjectCreateOperation.OperationId,
            GameObjectFindOperation.OperationId,
            GameObjectDeleteOperation.OperationId,
            GameObjectSetParentOperation.OperationId,
            TransformSetOperation.OperationId,
            ComponentAddOperation.OperationId,
            ComponentRemoveOperation.OperationId,
            PropertyGetOperation.OperationId,
            PropertySetOperation.OperationId,
        };

        private const string ProbeFolder = "Assets/OpWeaveAt02";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";

        private readonly List<OperationTestHarness> _harnesses =
            new List<OperationTestHarness>();
        private readonly string _tag =
            Guid.NewGuid().ToString("N").Substring(0, 6);
        private OperationTestHarness? _fixture;
        private int _serial;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveAt02");
            }
            // A saved, clean base scene: an untitled caller scene would be
            // taken over by the first harness that opens — and additive
            // fixture scenes cannot be created while one is open.
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(
                EditorSceneManager.SaveScene(scene, BaseScenePath), Is.True,
                "the test's base scene must save.");
        }

        [TearDown]
        public void TearDown()
        {
            _fixture = null;
            foreach (var harness in _harnesses)
            {
                harness.Dispose();
            }
            _harnesses.Clear();
            // One fresh untitled scene — every loaded fixture, opened or
            // created scene is discarded before the probe folders leave.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            if (AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"))
            {
                AssetDatabase.DeleteAsset("Assets/OpWeaveTesting");
            }
            AssetDatabase.Refresh();
        }

        // ── The SPEC §I.1 group, exercised in catalog order ─────────────

        // The pass condition's coverage half: the exercised list is exactly
        // the SPEC §I.1 catalog group — every entry resolves to a real
        // standard Operation and carries a valid §C3 descriptor, so a
        // missing or skipped entry fails here before the per-Operation
        // assertions below are even reached.
        [Test]
        public void ExercisedList_IsTheSpecI1Group()
        {
            Assert.That(s_specGroup, Has.Length.EqualTo(14));
            Assert.That(s_specGroup.Distinct().Count(), Is.EqualTo(14));
            foreach (var id in s_specGroup)
            {
                Assert.DoesNotThrow(
                    () => OperationContract.AssertDescriptorValid(id), id);
            }
        }

        [Test]
        public void CoreStart_Contract()
        {
            const string id = "core.start";
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(() => OperationContract.AssertIdempotency(id, new { }));
            Assert.DoesNotThrow(() => OperationContract.AssertUndo(id, new { }));
            Assert.DoesNotThrow(
                () => OperationContract.AssertDeclaredEffectsOnly(id, new { }));
        }

        [Test]
        public void SceneOpen_Contract()
        {
            var id = OpenSceneOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, SceneOpenInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, SceneOpenInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, SceneOpenInputs()));
        }

        [Test]
        public void SceneCreate_Contract()
        {
            var id = CreateSceneOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, SceneCreateInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, SceneCreateInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, SceneCreateInputs()));
        }

        [Test]
        public void SceneSave_Contract()
        {
            var id = SaveSceneOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(() => OperationContract.AssertIdempotency(id, new { }));
            Assert.DoesNotThrow(() => OperationContract.AssertUndo(id, new { }));
            Assert.DoesNotThrow(
                () => OperationContract.AssertDeclaredEffectsOnly(id, new { }));
        }

        [Test]
        public void SceneClose_Contract()
        {
            var id = CloseSceneOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, SceneCloseInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, SceneCloseInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, SceneCloseInputs()));
        }

        [Test]
        public void GameObjectCreate_Contract()
        {
            var id = GameObjectCreateOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, CreateInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, CreateInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, CreateInputs()));
        }

        [Test]
        public void GameObjectFind_Contract()
        {
            var id = GameObjectFindOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, FindInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, FindInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, FindInputs()));
        }

        [Test]
        public void GameObjectDelete_Contract()
        {
            var id = GameObjectDeleteOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, DeleteInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, DeleteInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, DeleteInputs()));
        }

        [Test]
        public void GameObjectSetParent_Contract()
        {
            var id = GameObjectSetParentOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, SetParentInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, SetParentInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, SetParentInputs()));
        }

        [Test]
        public void TransformSet_Contract()
        {
            var id = TransformSetOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, TransformInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, TransformInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, TransformInputs()));
        }

        [Test]
        public void ComponentAdd_Contract()
        {
            var id = ComponentAddOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, ComponentAddInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, ComponentAddInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, ComponentAddInputs()));
        }

        [Test]
        public void ComponentRemove_Contract()
        {
            var id = ComponentRemoveOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(() => OperationContract.AssertIdempotency(
                id, ComponentRemoveInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, ComponentRemoveInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, ComponentRemoveInputs()));
        }

        [Test]
        public void PropertyGet_Contract()
        {
            var id = PropertyGetOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, PropertyGetInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, PropertyGetInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, PropertyGetInputs()));
        }

        [Test]
        public void PropertySet_Contract()
        {
            var id = PropertySetOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, PropertySetInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, PropertySetInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, PropertySetInputs()));
        }

        // ── The deliberately weakened Reconcile fixture ─────────────────

        // The ticket's boundary acceptance: a Reconcilable Operation whose
        // Reconcile is weakened to answer Completed on existence and name
        // alone — exactly the evidence SPEC §F5 rules out — must fail the
        // contract. The dummy below is shaped on unity.gameobject.create:
        // its (a)/(b) judgements pass, and the damaged-evidence probe's
        // foreign same-named replacement is what exposes the lie.
        [Test]
        public void WeakenedReconcile_IsDetected()
        {
            // The damaged-evidence probe must be the failing assertion —
            // an exception from an earlier before/after-Execute check would
            // prove nothing about the lie this fixture exists to expose, so
            // the verdict's specific §F5 rejection text is required, not
            // just any AssertionException.
            var failure = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.at02.weakened_create",
                    new { name = "At02Weakened" }));
            Assert.IsNotNull(failure);
            StringAssert.Contains("test.at02.weakened_create", failure!.Message);
            StringAssert.Contains(
                "after its artifact was damaged", failure.Message);
            StringAssert.Contains("admit Unknown only", failure.Message);
        }

        // ── Fixture inputs ──────────────────────────────────────────────

        private OperationTestHarness Fixture()
        {
            if (_fixture is null)
            {
                _fixture = OperationTestHarness.Create();
                _harnesses.Add(_fixture);
            }
            return _fixture;
        }

        private string Unique(string stem) => stem + "-" + _tag + "-" + (++_serial);

        // A saved, closed empty scene asset in the fixture harness's own
        // folder: unity.scene.open loads it additively.
        private object SceneOpenInputs()
        {
            var scene = NewLoadedEmptyScene(Unique("At02Open"));
            var path = scene.path;
            Assert.That(
                EditorSceneManager.CloseScene(scene, true), Is.True,
                "the fixture scene asset must close before the run opens it.");
            return new { path, mode = "additive" };
        }

        // A path that does not exist yet, inside an allowed folder:
        // unity.scene.create writes the new scene there and loads it
        // additively.
        private object SceneCreateInputs() =>
            new
            {
                path = Fixture().TempFolder + "/" + Unique("At02Created") + ".unity",
                setup = "empty",
                mode = "additive",
            };

        // A loaded, saved empty scene: unity.scene.close closes it — the
        // Scene input converts through the §G2 SceneRef shape.
        // The inputs describe the scene by a SceneRef captured while it is
        // still loaded — a live UnityEngine.SceneManagement.Scene struct
        // could not be rebuilt after the run closed the scene, so the
        // contract's codec pass would meet an invalid Scene instead.
        private object SceneCloseInputs()
        {
            var scene = NewLoadedEmptyScene(Unique("At02Close"));
            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path);
            Assert.IsNotNull(
                asset, "the fixture scene asset must exist before the run.");
            return new
            {
                scene = new RefResolver().ToRef(asset!, "unity.SceneRef"),
            };
        }

        private Scene NewLoadedEmptyScene(string stem)
        {
            var path = Fixture().TempFolder + "/" + stem + ".unity";
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(
                EditorSceneManager.SaveScene(scene, path), Is.True,
                "the fixture scene asset must save.");
            return scene;
        }

        // unity.gameobject.create needs nothing but a name — the run's own
        // harness scene is where the object lands.
        private object CreateInputs() => new { name = Unique("At02New") };

        // unity.gameobject.find searches every loaded scene by name: the
        // fixture object must be the only match, so its name is unique.
        private object FindInputs()
        {
            var target = NewFixtureObject(Unique("At02Find"));
            return new { name = target.name };
        }

        // A live GameObject input could not be rebuilt for the after-Execute
        // Reconcile — Execute destroyed it — so the fixture supplies the
        // GameObjectRef value a plan's constant would carry.
        private object DeleteInputs() =>
            new
            {
                target = new RefResolver().ToRef(
                    NewFixtureObject(Unique("At02Delete")), "unity.GameObjectRef"),
            };

        // A fresh child and a fresh parent each call: an already-parented
        // child would make the next assertion's reparent a no-op.
        private object SetParentInputs() =>
            new
            {
                target = NewFixtureObject(Unique("At02Child")),
                parent = NewFixtureObject(Unique("At02Parent")),
            };

        private object TransformInputs() =>
            new
            {
                target = NewFixtureObject(Unique("At02Transform")),
                position = Json("{\"x\":1,\"y\":2,\"z\":3}"),
            };

        private object ComponentAddInputs() =>
            new
            {
                target = NewFixtureObject(Unique("At02Add")),
                componentType = "UnityEngine.BoxCollider",
            };

        private object ComponentRemoveInputs()
        {
            var target = NewFixtureObject(Unique("At02Remove"));
            var component = target.AddComponent<BoxCollider>();
            // The addition dirtied the fixture scene — the next internal
            // harness must not meet an unsaved scene.
            SaveFixtureScene();
            // A live Component input could not be rebuilt for the
            // after-Execute Reconcile — Execute destroyed it — so the
            // fixture supplies the ComponentRef value a plan's constant
            // would carry.
            return new
            {
                component = new RefResolver().ToRef(
                    component, "unity.ComponentRef<unity.Component>"),
            };
        }

        private object PropertyGetInputs()
        {
            var target = NewFixtureObject(Unique("At02PropGet"));
            return new { target, propertyPath = "m_Name" };
        }

        private object PropertySetInputs()
        {
            var target = NewFixtureObject(Unique("At02PropSet"));
            return new
            {
                target,
                propertyPath = "m_Name",
                value = target.name + "-Renamed",
            };
        }

        // A fixture GameObject in the owning fixture harness's own saved
        // scene — never the caller's scene, which a harness takeover or a
        // teardown reset would drop mid-assertion. The scene is re-saved so
        // the next harness's EnsureScene sees no unsaved change.
        private GameObject NewFixtureObject(string name)
        {
            var created = HarnessObjects.NewGameObject(Fixture(), name);
            SaveFixtureScene();
            return created;
        }

        private void SaveFixtureScene()
        {
            if (_fixture is { } fixture)
            {
                var scene = SceneManager.GetSceneByPath(fixture.TempScenePath);
                if (scene.IsValid() && scene.isLoaded)
                {
                    Assert.That(
                        EditorSceneManager.SaveScene(scene), Is.True,
                        "the fixture scene must stay saved — the next harness " +
                            "refuses to open its scene beside unsaved changes.");
                }
            }
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }

        // ── Fixture Operations ─────────────────────────────────────────

        /// <summary>
        /// The deliberately weakened fixture of the ticket's boundary
        /// acceptance: shaped on <c>unity.gameobject.create</c> — a
        /// Reconcilable create whose Execute produces a named GameObject —
        /// but its Reconcile answers <see cref="ReconcileResult.Completed"/>
        /// whenever an object of the recorded name exists. Name and
        /// existence are the evidence SPEC §F5 explicitly rules out, so the
        /// damaged-evidence probe's foreign same-named replacement must
        /// make AssertIdempotency fail: the correct verdict there is
        /// Unknown only.
        /// </summary>
        [OpWeaveOperation("test.at02.weakened_create", 1)]
        public sealed class WeakenedCreateReconcileOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.at02.weakened_create", 1)
                .Summary("Creates a named GameObject; claims Completed on its name.")
                .Input("name", "core.String", required: true)
                .Effects(EffectNames.SceneModify)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.at02.weakened_create", 1, "{\"name\":\"ProbeObject\"}"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject
                    {
                        ["name"] = ctx.Get<string>("name"),
                    }));

            public StepResult Execute(StepContext ctx)
            {
                var created = new GameObject(ctx.Get<string>("name"));
                ctx.Changes.Created(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(created),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(created).ToString()),
                    undoable: false);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var name = snapshot.ToToken()["name"]?.ToString();
                // The weakening this fixture exists for: Completed from the
                // name's mere presence — §F5's forbidden evidence.
                return name is not null && GameObject.Find(name) is not null
                    ? new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>(StringComparer.Ordinal))
                    : (ReconcileResult)new ReconcileResult.NotApplied();
            }

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
