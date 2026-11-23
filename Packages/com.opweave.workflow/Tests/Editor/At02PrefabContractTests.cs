#nullable enable

// T-W4.5-02 [Edit] tests: local:T-W4.5-02:at02-prefab — the SPEC §T.3 AT-02
// contract applied to every standard Operation of the SPEC §I.2 Prefab
// catalog group through the OpWeave.Testing §M.6 item 2 assertions:
//
//   * AssertDescriptorValid — the §C3 registration checks pass with no
//     OW-OP-003 violation (SPEC §C1 test 1);
//   * AssertIdempotency — per the declaration: an Idempotent Operation
//     (apply_overrides, revert_overrides) runs twice on the same inputs
//     with an identical world and equal outputs; a Reconcilable Operation
//     is judged through RunInterrupted at both §F5 crash windows, Unknown
//     is accepted as the safe verdict, an evidence-less Completed fails,
//     and the damaged-evidence probe admits Unknown only;
//   * AssertUndo — one Undo reverts a step whose declaration is Full
//     (instantiate, revert_overrides, unpack);
//   * AssertDeclaredEffectsOnly — no recorded or observed change outside
//     the declared §C4 vocabulary.
//
// Every input is a deterministic fixture this file builds: probe Prefab
// assets and Prefab instances inside the fixture harness's own folder and
// saved scene — a second harness refuses to open its scene beside unsaved
// changes, so every factory re-saves. For save_contents the fixture opens
// the contents through a real harness open_contents run, which binds the
// editing session to the one synthetic Run every harness run claims — the
// sibling-step shape §G6 item 5's open → save hand-off is specified for.
// The Runner half of §F5 — a Run ending at a bare step_started whose
// evidence no longer holds must end NeedsReview, never an automatic
// success or re-execution — lives in At02PrefabRecoveryTests. This file
// closes with the deliberately weakened Reconcile fixture the ticket
// requires the contract to catch.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
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
    /// <c>local:T-W4.5-02:at02-prefab</c>: the AT-02 contract suite for the
    /// SPEC §I.2 Prefab group.
    /// </summary>
    internal sealed class At02PrefabContractTests
    {
        // The SPEC §I.2 catalog group, verbatim and in table order — the
        // complete exercised list the AT-02 pass condition requires with
        // none skipped.
        private static readonly string[] s_specGroup =
        {
            PrefabCreateFromGameObjectOperation.OperationId,
            PrefabInstantiateOperation.OperationId,
            PrefabApplyOverridesOperation.OperationId,
            PrefabRevertOverridesOperation.OperationId,
            PrefabUnpackOperation.OperationId,
            PrefabCreateVariantOperation.OperationId,
            "unity.prefab.open_contents",
            "unity.prefab.save_contents",
        };

        private const string ProbeFolder = "Assets/OpWeaveAt02Prefab";
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
                AssetDatabase.CreateFolder("Assets", "OpWeaveAt02Prefab");
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
            // A contract run of open_contents leaves the editing session it
            // opened: the preview scene is no harness's owned scene and no
            // assertion's cleanup, so the suite releases it itself — a
            // leftover session would be the next open_contents' second
            // contents of the same Prefab.
            PrefabContentsFixture.UnloadAllOpenedContents();
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

        // ── The SPEC §I.2 group, exercised in catalog order ─────────────

        // The pass condition's coverage half: the exercised list is exactly
        // the SPEC §I.2 catalog group — every entry resolves to a real
        // standard Operation and carries a valid §C3 descriptor, so a
        // missing or skipped entry fails here before the per-Operation
        // assertions below are even reached.
        [Test]
        public void ExercisedList_IsTheSpecI2Group()
        {
            Assert.That(s_specGroup, Has.Length.EqualTo(8));
            Assert.That(s_specGroup.Distinct().Count(), Is.EqualTo(8));
            foreach (var id in s_specGroup)
            {
                Assert.DoesNotThrow(
                    () => OperationContract.AssertDescriptorValid(id), id);
            }
        }

        [Test]
        public void PrefabCreateFromGameObject_Contract()
        {
            var id = PrefabCreateFromGameObjectOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, CreateFromInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, CreateFromInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, CreateFromInputs()));
        }

        [Test]
        public void PrefabInstantiate_Contract()
        {
            var id = PrefabInstantiateOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, InstantiateInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, InstantiateInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, InstantiateInputs()));
        }

        [Test]
        public void PrefabApplyOverrides_Contract()
        {
            var id = PrefabApplyOverridesOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, InstanceWithOverrideInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, InstanceWithOverrideInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, InstanceWithOverrideInputs()));
        }

        [Test]
        public void PrefabRevertOverrides_Contract()
        {
            var id = PrefabRevertOverridesOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, InstanceWithOverrideInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, InstanceWithOverrideInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, InstanceWithOverrideInputs()));
        }

        [Test]
        public void PrefabUnpack_Contract()
        {
            var id = PrefabUnpackOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, UnpackInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, UnpackInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, UnpackInputs()));
        }

        [Test]
        public void PrefabCreateVariant_Contract()
        {
            var id = PrefabCreateVariantOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, VariantInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, VariantInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, VariantInputs()));
        }

        [Test]
        public void PrefabOpenContents_Contract()
        {
            var id = "unity.prefab.open_contents";
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, OpenContentsInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, OpenContentsInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, OpenContentsInputs()));
        }

        // The §F5/§G6 row whose judgement the contract's after-Execute
        // probe now requires: a real interrupted Run leaves the step's own
        // prefab_edit acquisition live and the contents session it opened
        // intact — the live same-step acquisition the §I.2 row names as
        // the Completed evidence. Reconcile asked over exactly that state
        // must answer Completed and restore the root the step itself
        // published — the verdict a Reconcile weakened to always answer
        // Unknown could never produce.
        [Test]
        public void PrefabOpenContents_LiveSameStepAcquisition_ReconcilesCompleted()
        {
            const string id = "unity.prefab.open_contents";
            var prefab = NewProbePrefab(Unique("At02LiveContents"));
            var path = AssetDatabase.GetAssetPath(prefab);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var interrupted = Fixture().RunInterrupted(
                id, new { prefab }, InterruptPoint.AfterExecute);
            Assert.That(
                interrupted.Outcome, Is.EqualTo(StepResultKind.Succeeded),
                () => interrupted.ToString());
            var roots = PrefabContentsResources.FindEditingRoots(path);
            Assert.AreEqual(
                1, roots.Count,
                "the interrupted run must leave exactly one live contents " +
                "session");

            // The recovery step's own §F2 fold: the journaled acquisition
            // of the same step rebuilt open (SPEC §E6 item 4), with the
            // ownership binding the step's load recorded under its
            // acquisition scope — the same-step state §F5's open_contents
            // row completes on.
            var recovery = new PrefabContentsFixture.Harness();
            recovery.SeedFoldedAcquisition(guid, path);
            PrefabContentsOwnership.Bind(recovery.Scope, guid, roots[0]);
            var context = recovery.OpenContext(guid, path);

            var verdict = new PrefabOpenContentsOperation().Reconcile(
                context, StartedSnapshotOf(interrupted));

            Assert.That(
                verdict,
                Is.TypeOf<ReconcileResult.Completed>(),
                "the step's own live acquisition is the §F5 row's whole " +
                    "Completed evidence — a Reconcile weakened to always " +
                    "answer Unknown fails exactly here" +
                    (verdict is ReconcileResult.Unknown unknown
                        ? " (Unknown: " + unknown.Reason + ")"
                        : "."));
            var restored = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.IsTrue(
                restored.TryGetValue("root", out var restoredRoot),
                "every declared output must be restored (§F5 rule 6)");
            Assert.AreEqual(
                interrupted.Outputs["root"].CanonicalText,
                restoredRoot!.CanonicalText,
                "the restored output names the contents the interrupted " +
                "step itself opened");
        }

        [Test]
        public void PrefabSaveContents_Contract()
        {
            var id = "unity.prefab.save_contents";
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, SaveContentsInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, SaveContentsInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, SaveContentsInputs()));
        }

        // ── The deliberately weakened Reconcile fixture ─────────────────

        // The ticket's boundary acceptance: a Reconcilable Operation whose
        // Reconcile is weakened to answer Completed on the artifact's mere
        // existence — exactly the evidence SPEC §F5 rules out and §G6.1
        // forbids for Prefabs, where only a PrefabContentFingerprint v1
        // match may complete — must fail the contract. The dummy below
        // writes a file artifact like the Prefab-creating Operations do:
        // its (a)/(b) judgements pass, and the damaged-evidence probe's
        // overwritten file is what exposes the lie.
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
                    "test.at02.weakened_prefab_create",
                    new { path = Fixture().TempFolder + "/At02Weakened.prefab" }));
            Assert.IsNotNull(failure);
            StringAssert.Contains("test.at02.weakened_prefab_create", failure!.Message);
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

        // unity.prefab.create_from_gameobject: a fresh source object and a
        // path that does not exist yet, each call — the connected instance
        // exercises both declared outputs.
        private object CreateFromInputs() =>
            new
            {
                source = NewFixtureObject(Unique("At02PrefabSource")),
                path = Fixture().TempFolder + "/" + Unique("At02FromObject") + ".prefab",
                connect = true,
                overwrite = false,
            };

        // unity.prefab.instantiate: the Prefab asset alone places the
        // instance at the root of the run's own harness scene.
        private object InstantiateInputs() =>
            new { prefab = NewProbePrefab(Unique("At02InstanceBase")) };

        // An instance carrying one added-component override — the change
        // apply_overrides pushes into the asset and revert_overrides takes
        // back out. A fresh instance each call: the first assertion's apply
        // or revert consumes the override.
        private object InstanceWithOverrideInputs()
        {
            var instance = NewFixtureInstance(NewProbePrefab(Unique("At02OverrideBase")));
            instance.AddComponent<BoxCollider>();
            SaveFixtureScene();
            // A live GameObject input could not be rebuilt for the
            // after-Execute Reconcile — the captured PrefabInstanceRef is
            // the constant a Plan would carry, and it resolves to the same
            // object whatever Execute did to its overrides.
            return new
            {
                instance = new RefResolver().ToRef(instance, "unity.PrefabInstanceRef"),
            };
        }

        // unity.prefab.unpack: the instance becomes a plain GameObject in
        // Execute, so the input is the captured PrefabInstanceRef — a live
        // object could no longer convert to that ref type afterwards.
        private object UnpackInputs() =>
            new
            {
                instance = new RefResolver().ToRef(
                    NewFixtureInstance(NewProbePrefab(Unique("At02UnpackBase"))),
                    "unity.PrefabInstanceRef"),
            };

        private object VariantInputs() =>
            new
            {
                @base = NewProbePrefab(Unique("At02VariantBase")),
                path = Fixture().TempFolder + "/" + Unique("At02Variant") + ".prefab",
            };

        // unity.prefab.open_contents opens a fresh session — a leftover
        // contents of the same Prefab would make its own §F5 row read an
        // open session the step did not acquire, so every call starts from
        // none open.
        private object OpenContentsInputs()
        {
            PrefabContentsFixture.UnloadAllOpenedContents();
            return new { prefab = NewProbePrefab(Unique("At02Contents")) };
        }

        // unity.prefab.save_contents: the root the step saves is the
        // output of a real open_contents run — the value a §G6 data edge
        // carries from the open step to the save step, bound to the one
        // synthetic Run every harness run claims.
        private object SaveContentsInputs()
        {
            PrefabContentsFixture.UnloadAllOpenedContents();
            var prefab = NewProbePrefab(Unique("At02Save"));
            var open = Fixture().Run(
                "unity.prefab.open_contents", new { prefab });
            Assert.That(
                open.Ok, Is.True,
                "the fixture open_contents run must succeed: " + open);
            return new { root = open.Outputs["root"] };
        }

        // The Preflight snapshot an interrupted run's step_started record
        // journaled — the JSON a recovering Runner hands Reconcile (SPEC
        // §F5 common rule 1).
        private static JsonValue StartedSnapshotOf(HarnessResult result)
        {
            for (var i = result.Journal.Count - 1; i >= 0; i--)
            {
                var record = result.Journal[i];
                if (record.Type != JournalRecordType.StepStarted)
                {
                    continue;
                }
                var snapshot = record.Data.ToToken()["snapshot"];
                Assert.IsNotNull(
                    snapshot, "the step_started record carried no snapshot");
                return JsonValue.From(snapshot!);
            }
            Assert.Fail("the interrupted run journaled no step_started record");
            throw new InvalidOperationException();
        }

        // A probe Prefab asset inside the fixture harness's own folder —
        // deleted with the folder at Dispose — from a source object the
        // fixture scene then loses again.
        private GameObject NewProbePrefab(string stem)
        {
            var source = HarnessObjects.NewGameObject(Fixture(), stem);
            try
            {
                var path = Fixture().TempFolder + "/" + stem + ".prefab";
                var saved = PrefabUtility.SaveAsPrefabAsset(source, path);
                Assert.IsNotNull(saved, "the probe Prefab must save: " + path);
                return saved!;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        // A Prefab instance of the asset in the fixture harness's own saved
        // scene — saved so the next harness's EnsureScene sees no unsaved
        // change and the Ref resolves against a persisted scene.
        private GameObject NewFixtureInstance(GameObject prefabAsset)
        {
            var scene = SceneManager.GetSceneByPath(Fixture().TempScenePath);
            Assert.IsTrue(scene.IsValid() && scene.isLoaded,
                "the fixture harness scene must be open");
            var instance = PrefabUtility.InstantiatePrefab(
                prefabAsset, scene) as GameObject;
            Assert.IsNotNull(instance, "the fixture Prefab instance must be created.");
            SaveFixtureScene();
            return instance!;
        }

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

        // ── Fixture Operations ─────────────────────────────────────────

        /// <summary>
        /// The deliberately weakened fixture of the ticket's boundary
        /// acceptance: shaped on the Prefab-creating Operations of the §I.2
        /// group — a Reconcilable create whose Execute writes the artifact
        /// file — but its Reconcile answers
        /// <see cref="ReconcileResult.Completed"/> whenever a file exists
        /// at the recorded path. Existence and paths are the evidence SPEC
        /// §F5 explicitly rules out (and §G6.1 forbids for Prefabs, where a
        /// PrefabContentFingerprint v1 match is the only Completed), so the
        /// damaged-evidence probe's overwritten file must make
        /// AssertIdempotency fail: the correct verdict there is Unknown
        /// only.
        /// </summary>
        [OpWeaveOperation("test.at02.weakened_prefab_create", 1)]
        public sealed class WeakenedPrefabCreateReconcileOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.at02.weakened_prefab_create", 1)
                .Summary("Writes a file artifact; claims Completed on its existence.")
                .Input("path", "unity.AssetPath", required: true)
                .Effects(EffectNames.AssetCreate)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.at02.weakened_prefab_create", 1,
                            "{\"path\":\"Assets/OpWeaveExamples/Probe.prefab\"}"),
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
                        ["path"] = ctx.Get<string>("path"),
                    }));

            public StepResult Execute(StepContext ctx)
            {
                var path = ctx.Get<string>("path");
                File.WriteAllText(
                    Path.Combine(Application.dataPath, "..", path),
                    "%YAML 1.1\n# weakened-fixture artifact — content is not evidence\n");
                ctx.Changes.Created(
                    new ChangeTarget("asset", path, null),
                    undoable: false);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var path = snapshot.ToToken()["path"]?.ToString();
                // The weakening this fixture exists for: Completed from the
                // file's mere presence — §F5's and §G6.1's forbidden
                // evidence, where only a fingerprint match may complete.
                return path is not null && File.Exists(
                        Path.Combine(Application.dataPath, "..", path))
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
