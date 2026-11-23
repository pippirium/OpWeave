#nullable enable

// T-W2.10-02 [Edit] tests: local:T-W2.10-02:operation-contract-asserts for the
// SPEC §M.6 item 2 OperationContract assertions — applied to correct dummy
// Operations (which must pass all four) and to deliberately broken ones: a
// fake-Idempotent dummy duplicating on the second run, a Reconcilable dummy
// answering Completed without evidence, an Undo=Full dummy whose changes need
// two undos, a read-declared dummy that creates an asset, and descriptors
// violating the §C3 rules. The standard unity.asset.save_all drives the
// acceptance criterion that correct standard Operations pass.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Testing;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The SPEC §M.6 item 2 contract fixtures: correct and deliberately broken
    /// dummy Operations exercised through the four
    /// <see cref="OperationContract"/> assertions.
    /// </summary>
    internal sealed class OperationContractTests
    {
        private readonly List<OperationTestHarness> _harnesses =
            new List<OperationTestHarness>();

        [SetUp]
        public void SetUp()
        {
            // One clean untitled scene — the same starting state the harness
            // fixtures use, so leftover scenes cannot cascade.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var harness in _harnesses)
            {
                harness.Dispose();
            }
            _harnesses.Clear();
            if (AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"))
            {
                AssetDatabase.DeleteAsset("Assets/OpWeaveTesting");
            }
            AssetDatabase.Refresh();
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        // ── local:T-W2.10-02:operation-contract-asserts — correct dummies ────

        [Test]
        public void CorrectIdempotentDummyPassesAllFourAssertions()
        {
            const string id = "test.contract.ensure_marker";
            var inputs = new { name = "ContractMarker" };

            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(() => OperationContract.AssertIdempotency(id, inputs));
            Assert.DoesNotThrow(() => OperationContract.AssertUndo(id, inputs));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(id, inputs));
        }

        [Test]
        public void CorrectReconcilableDummyPassesAllFourAssertions()
        {
            var harness = Track(OperationTestHarness.Create());
            const string id = "test.contract.marker_asset";
            // Each assertion gets its own untouched target: a world the
            // preceding assertion's runs (and its damaged-evidence probe)
            // already touched is not a clean baseline for the next one.
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(() => OperationContract.AssertIdempotency(
                id, new { path = harness.TempFolder + "/marker-idem.txt" }));
            Assert.DoesNotThrow(() => OperationContract.AssertUndo(
                id, new { path = harness.TempFolder + "/marker-undo.txt" }));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, new { path = harness.TempFolder + "/marker-effects.txt" }));
        }

        [Test]
        public void CorrectStandardOperationPassesAllFourAssertions()
        {
            const string id = "unity.asset.save_all";
            // unity.asset.save_all is the standard Operation whose two
            // AssertIdempotency runs genuinely succeed today — SPEC §I.3's
            // Idempotent row: saving an already-saved Editor changes
            // nothing. unity.asset.create_folder is not used here: its
            // path_writable requirement refuses the second run outright —
            // Issue #266 — so it cannot satisfy the Idempotent contract
            // that SPEC §G7 item 3 declares for it.
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(() => OperationContract.AssertIdempotency(id, new { }));
            Assert.DoesNotThrow(() => OperationContract.AssertUndo(id, new { }));
            Assert.DoesNotThrow(() =>
                OperationContract.AssertDeclaredEffectsOnly(id, new { }));
        }

        // ── local:T-W2.10-02:operation-contract-asserts — broken dummies ─────

        // SPEC §M.6 test: a dummy declaring Idempotent that duplicates on the
        // second run must be detected.
        [Test]
        public void FakeIdempotentDummyIsDetected()
        {
            var exception = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.contract.duplicating", new { }));

            Assert.That(
                exception!.Message, Does.Contain("Idempotent"));
        }

        // SPEC §F5/M.6: a Reconcilable dummy answering Completed without
        // evidence must fail — here it answers Completed even before Execute
        // ran, which is never a possible honest verdict.
        [Test]
        public void ReconcilableAnsweringCompletedWithoutEvidenceIsDetected()
        {
            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.contract.always_completed", new { }));
        }

        // SPEC §G3 contract test: an Undo=Full dummy whose Execute splits its
        // work across two Undo groups cannot be reverted by one Undo.
        [Test]
        public void UndoFullDummyNeedingTwoUndosIsDetected()
        {
            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertUndo("test.contract.split_undo", new { }));
        }

        // SPEC §M.6 item 2: a dummy declaring only "read" that creates an
        // asset must be detected — the file is written without recording a
        // change, so only the world diff can catch it.
        [Test]
        public void ReadDeclaredDummyCreatingAnAssetIsDetected()
        {
            var harness = Track(OperationTestHarness.Create());
            var path = harness.TempFolder + "/secret.txt";

            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertDeclaredEffectsOnly(
                    "test.contract.read_lies", new { path }));
        }

        // SPEC §C4 keeps asset.move apart from asset.create: a dummy
        // declaring only asset.move performs a real move and must pass —
        // the move's destination is not also a creation.
        [Test]
        public void MoveDeclaredDummyMovingAnAssetPasses()
        {
            var harness = Track(OperationTestHarness.Create());
            var source = harness.TempFolder + "/move-source.txt";
            var destination = harness.TempFolder + "/move-destination.txt";
            File.WriteAllText(
                Path.Combine(Directory.GetCurrentDirectory(), source), "moved\n");
            AssetDatabase.Refresh();

            Assert.DoesNotThrow(() =>
                OperationContract.AssertDeclaredEffectsOnly(
                    "test.contract.move_asset", new { source, destination }));
        }

        // SPEC §C4 keeps asset.move apart from asset.modify: a dummy
        // declaring only asset.move that also rewrites the moved asset's
        // bytes must be detected — the surviving GUID's move does not cover
        // the changed content at its destination.
        [Test]
        public void MoveDeclaredDummyModifyingTheMovedAssetIsDetected()
        {
            var harness = Track(OperationTestHarness.Create());
            var source = harness.TempFolder + "/movemodify-source.txt";
            var destination = harness.TempFolder + "/movemodify-destination.txt";
            File.WriteAllText(
                Path.Combine(Directory.GetCurrentDirectory(), source), "original\n");
            AssetDatabase.Refresh();

            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertDeclaredEffectsOnly(
                    "test.contract.move_asset_lies", new { source, destination }));
        }

        // SPEC §C4: scene.save covers a saved .unity file only — a dummy
        // declaring scene.save that modifies a data asset must be detected.
        [Test]
        public void SceneSaveDeclaredDummyModifyingOtherAssetIsDetected()
        {
            var harness = Track(OperationTestHarness.Create());
            var path = harness.TempFolder + "/data.txt";
            File.WriteAllText(
                Path.Combine(Directory.GetCurrentDirectory(), path), "original\n");
            AssetDatabase.Refresh();

            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertDeclaredEffectsOnly(
                    "test.contract.scene_save_lies", new { path }));
        }

        // SPEC §F5 case 5: a Reconcilable dummy answering Completed merely
        // because its artifact exists — never proving identity or content —
        // must fail the damaged-evidence probe even though it never records
        // the artifact it created.
        [Test]
        public void ReconcilableCompletedOnExistenceAloneIsDetected()
        {
            var harness = Track(OperationTestHarness.Create());
            var inputs = new { path = harness.TempFolder + "/flag.txt" };

            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.contract.flag_completed", inputs));
        }

        // SPEC §F5 case 5 on a folder artifact: the same lie as the file
        // flag — Completed on existence alone — through the artifact kind
        // the file probe could not reach. The damaged-evidence probe must
        // damage a folder's content the same way it damages a file's.
        [Test]
        public void ReconcilableCompletedOnFolderExistenceAloneIsDetected()
        {
            var harness = Track(OperationTestHarness.Create());
            var inputs = new { path = harness.TempFolder + "/flag-dir" };

            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.contract.folder_completed", inputs));
        }

        // The SPEC §F5 unity.component.add row: a conforming Reconcilable
        // proves its verdicts from the identified GameObject's component
        // list and the added component's type and serialized content — a
        // name is never its evidence. The damaged-evidence probe must keep
        // the object where it is and break the component evidence instead,
        // so this dummy's verdicts pass: NotApplied before Execute,
        // Completed after, Unknown once the evidence is damaged.
        [Test]
        public void ConformingComponentAddDummyPassesAssertIdempotency()
        {
            var harness = Track(OperationTestHarness.Create());
            // The target must sit in a scene that stays loaded for the whole
            // assertion: one planted in the caller's untitled scene dies with
            // that scene's takeover before the interrupted run reaches
            // Execute, and the dummy then honestly reports it missing.
            // HarnessObjects places it in the harness's own scene — the
            // fixture API exists exactly for this — and the re-save leaves
            // the finished fixture clean, because a second harness refuses
            // to open its scene beside unsaved changes.
            var target = HarnessObjects.NewGameObject(
                harness, "ContractComponentTarget");
            Assert.IsTrue(
                EditorSceneManager.SaveScene(target.scene),
                "the fixture scene could not be saved.");

            Assert.DoesNotThrow(() => OperationContract.AssertIdempotency(
                "test.contract.component_add",
                new { target = "ContractComponentTarget" }));
        }

        // SPEC §F5 case 5 through the GameObject artifact kind: a
        // Reconcilable dummy answering Completed whenever an object of the
        // expected name exists must still be detected — the probe's foreign
        // same-named replacement carries the name without carrying the
        // evidence.
        [Test]
        public void ReconcilableCompletedOnObjectNameAloneIsDetected()
        {
            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.contract.name_completed", new { name = "ContractNamed" }));
        }

        // SPEC §F5 row 1 names two successful executions: a second run that
        // reached Execute and failed is a contract violation — and a second
        // run the Operation's own Preflight refuses is the same violation
        // before it ever reached its write-ahead record. The refusing shape
        // is the defect Issue #266 tracks in unity.asset.create_folder.
        [Test]
        public void SecondRunFailingAfterStartIsDetected()
        {
            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.contract.second_run_fails", new { }));
        }

        [Test]
        public void SecondRunPreflightRefusalIsDetected()
        {
            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.contract.second_run_refused", new { }));
        }

        [Test]
        public void WaitableWithoutPollIsDetectedByAssertDescriptorValid()
        {
            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertDescriptorValid("test.contract.no_poll"));
        }

        [Test]
        public void UnknownSideEffectIsDetectedByAssertDescriptorValid()
        {
            var exception = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertDescriptorValid("test.contract.bad_effect"));

            Assert.That(exception!.Message, Does.Contain("OW-OP-003"));
        }

        [Test]
        public void UnknownOperationIsDetectedByAssertDescriptorValid()
        {
            Assert.Throws<AssertionException>(() =>
                OperationContract.AssertDescriptorValid("test.contract.missing"));
        }

        private OperationTestHarness Track(OperationTestHarness harness)
        {
            _harnesses.Add(harness);
            return harness;
        }

        // ── Fixture Operations ─────────────────────────────────────────────

        /// <summary>
        /// Correct Idempotent + Undo=Full dummy: ensures a GameObject named
        /// after its input exists — creates it under the step's Undo group
        /// when absent, touches nothing when present.
        /// </summary>
        [OpWeaveOperation("test.contract.ensure_marker", 1)]
        public sealed class EnsureMarkerOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.ensure_marker", 1)
                .Summary("Ensures a marker GameObject exists.")
                .Input("name", "core.String", required: true)
                .Output("present", "core.Bool")
                .Effects(EffectNames.SceneModify)
                .Undo(UndoSupport.Full)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.ensure_marker", 1,
                            "{\"name\":\"ProbeMarker\"}", "present"),
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
                        ["present"] = GameObject.Find(ctx.Get<string>("name")) != null,
                    }));

            public StepResult Execute(StepContext ctx)
            {
                var name = ctx.Get<string>("name");
                if (GameObject.Find(name) is null)
                {
                    var created = new GameObject(name);
                    Undo.RegisterCreatedObjectUndo(created, "Ensure marker " + name);
                    ctx.Changes.Created(
                        new ChangeTarget(
                            "gameObject",
                            RefHierarchyPath.For(created),
                            null,
                            GlobalObjectId.GetGlobalObjectIdSlow(created).ToString()),
                        undoable: true);
                }
                ctx.SetOutput("present", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Correct Reconcilable dummy: writes a marker text asset and can
        /// prove each SPEC §F5 verdict from its Preflight snapshot —
        /// NotApplied while the file is absent, Completed while it carries
        /// the expected content, Unknown when the evidence no longer holds.
        /// </summary>
        [OpWeaveOperation("test.contract.marker_asset", 1)]
        public sealed class MarkerAssetOperation : IOperation
        {
            private const string Content = "marker";

            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.marker_asset", 1)
                .Summary("Writes a marker text asset.")
                .Input("path", "unity.AssetPath", required: true)
                .Output("written", "core.Bool")
                .Effects(EffectNames.AssetCreate)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.marker_asset", 1,
                            "{\"path\":\"Assets/OpWeaveExamples/ProbeMarker.txt\"}", "written"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                var path = ctx.Get<string>("path");
                return new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject
                    {
                        ["path"] = path,
                        ["existed"] = File.Exists(FullPath(path)),
                        ["content"] = Content,
                    }));
            }

            public StepResult Execute(StepContext ctx)
            {
                var path = ctx.Get<string>("path");
                File.WriteAllText(FullPath(path), Content);
                AssetDatabase.Refresh();
                ctx.Changes.Created(
                    new ChangeTarget(
                        "asset", path, AssetDatabase.AssetPathToGUID(path)),
                    undoable: false);
                ctx.SetOutput("written", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var data = snapshot.ToToken();
                var path = data["path"]?.ToString();
                var content = data["content"]?.ToString();
                var existed = data["existed"]?.ToObject<bool>();
                if (path is null || content is null || existed is null)
                {
                    return new ReconcileResult.Unknown(
                        "The snapshot is missing its path, content or existed members.");
                }
                if (!File.Exists(FullPath(path)))
                {
                    return existed.Value
                        ? new ReconcileResult.Unknown(
                            "The path was occupied at Preflight and is now empty.")
                        : new ReconcileResult.NotApplied();
                }
                return File.ReadAllText(FullPath(path)) == content
                    ? new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                        {
                            ["written"] = JsonValue.From(new JValue(true)),
                        })
                    : new ReconcileResult.Unknown(
                        "The artifact's content no longer matches the expected marker.");
            }

            public void Cancel(StepContext ctx)
            {
            }

            private static string FullPath(string assetPath) =>
                Path.Combine(Directory.GetCurrentDirectory(), assetPath);
        }

        /// <summary>
        /// Broken dummy: declares Idempotent but creates another GameObject on
        /// every run — the duplication SPEC §M.6's test must detect.
        /// </summary>
        [OpWeaveOperation("test.contract.duplicating", 1)]
        public sealed class DuplicatingOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.duplicating", 1)
                .Summary("Creates another GameObject on every run.")
                .Effects(EffectNames.SceneModify)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For("test.contract.duplicating", 1),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                var created = new GameObject("ContractDuplicate");
                ctx.Changes.Created(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(created),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(created).ToString()),
                    undoable: true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy: declares Reconcilable but its Reconcile answers
        /// Completed unconditionally — Completed without evidence, the verdict
        /// SPEC §F5 forbids.
        /// </summary>
        [OpWeaveOperation("test.contract.always_completed", 1)]
        public sealed class AlwaysCompletedOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.always_completed", 1)
                .Summary("Always claims it completed.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.always_completed", 1, outputPort: "done"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        ["done"] = JsonValue.From(new JValue(true)),
                    });

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy: declares Undo=Full but leaves half its Execute's
        /// work outside the Undo stack, so a single Undo cannot revert the
        /// step. A second Undo group inside <c>Execute</c> cannot produce
        /// the breach — the §G3 scope collapses every group created while
        /// the step ran into the step's one undo — so the breach is the
        /// work the Undo API never sees (SPEC §G3 item 2).
        /// </summary>
        [OpWeaveOperation("test.contract.split_undo", 1)]
        public sealed class SplitUndoOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.split_undo", 1)
                .Summary("Leaves half its work outside the Undo stack.")
                .Effects(EffectNames.SceneModify)
                .Undo(UndoSupport.Full)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For("test.contract.split_undo", 1),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                var first = new GameObject("SplitA");
                Undo.RegisterCreatedObjectUndo(first, "Create SplitA");
                ctx.Changes.Created(
                    new ChangeTarget("gameObject", RefHierarchyPath.For(first)),
                    undoable: true);
                // The contract breach: this creation never reaches the Undo
                // stack, so one Undo reverts SplitA but leaves SplitB — the
                // step needs more than the single Undo Undo=Full promises.
                var second = new GameObject("SplitB");
                ctx.Changes.Created(
                    new ChangeTarget("gameObject", RefHierarchyPath.For(second)),
                    undoable: true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy: declares only the "read" effect but writes an asset
        /// file directly — without recording the change, so the world diff is
        /// what must catch it (SPEC §M.6's "read と宣言してアセットを作った").
        /// </summary>
        [OpWeaveOperation("test.contract.read_lies", 1)]
        public sealed class ReadLyingOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.read_lies", 1)
                .Summary("Declares read but writes an asset file.")
                .Input("path", "unity.AssetPath", required: true)
                .Effects(EffectNames.Read)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.read_lies", 1,
                            "{\"path\":\"Assets/OpWeaveExamples/ProbeSecret.txt\"}"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                File.WriteAllText(
                    Path.Combine(
                        Directory.GetCurrentDirectory(),
                        ctx.Get<string>("path")),
                    "secret\n");
                AssetDatabase.Refresh();
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy: declares a Waitable execution mode but keeps the
        /// placeholder Poll — the SPEC §C3 rule-3 violation
        /// AssertDescriptorValid must report.
        /// </summary>
        [OpWeaveOperation("test.contract.no_poll", 1)]
        public sealed class NoPollWaitableOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.no_poll", 1)
                .Summary("Declares waitable without a real Poll.")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with { ExecutionMode = ExecutionMode.Waitable };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Poll is not implemented.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy: declares a side effect outside the closed SPEC §C4
        /// vocabulary — OW-OP-003 territory.
        /// </summary>
        [OpWeaveOperation("test.contract.bad_effect", 1)]
        public sealed class BadEffectOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.bad_effect", 1)
                .Summary("Declares an effect outside the C4 vocabulary.")
                .Effects("not.an.effect")
                .Idempotency(Idempotency.Idempotent)
                .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Correct dummy for the asset.move contract: declares only
        /// <c>asset.move</c> and performs a real <c>AssetDatabase.MoveAsset</c>.
        /// The destination must not also read as an asset.create — SPEC §C4
        /// keeps the two apart.
        /// </summary>
        [OpWeaveOperation("test.contract.move_asset", 1)]
        public sealed class MoveAssetOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.move_asset", 1)
                .Summary("Moves an asset.")
                .Input("source", "unity.AssetPath", required: true)
                .Input("destination", "unity.AssetPath", required: true)
                .Effects(EffectNames.AssetMove)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Unsafe)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.move_asset", 1,
                            "{\"source\":\"Assets/OpWeaveExamples/ProbeSource.txt\"," +
                            "\"destination\":\"Assets/OpWeaveExamples/ProbeDestination.txt\"}"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                var destination = ctx.Get<string>("destination");
                var error = AssetDatabase.MoveAsset(
                    ctx.Get<string>("source"), destination);
                if (!string.IsNullOrEmpty(error))
                {
                    throw new InvalidOperationException(error);
                }
                ctx.Changes.Moved(
                    new ChangeTarget(
                        "asset", destination,
                        AssetDatabase.AssetPathToGUID(destination)),
                    undoable: false);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Unsafe operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy shaped like <see cref="MoveAssetOperation"/>: it
        /// declares only <c>asset.move</c> but also rewrites the moved
        /// asset's bytes — an undeclared <c>asset.modify</c> the world diff
        /// must report beside the move (SPEC §C4 keeps the two apart).
        /// </summary>
        [OpWeaveOperation("test.contract.move_asset_lies", 1)]
        public sealed class MoveAssetLyingOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.move_asset_lies", 1)
                .Summary("Moves an asset and rewrites its bytes.")
                .Input("source", "unity.AssetPath", required: true)
                .Input("destination", "unity.AssetPath", required: true)
                .Effects(EffectNames.AssetMove)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Unsafe)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.move_asset_lies", 1,
                            "{\"source\":\"Assets/OpWeaveExamples/ProbeSource.txt\"," +
                            "\"destination\":\"Assets/OpWeaveExamples/ProbeDestination.txt\"}"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                var destination = ctx.Get<string>("destination");
                var error = AssetDatabase.MoveAsset(
                    ctx.Get<string>("source"), destination);
                if (!string.IsNullOrEmpty(error))
                {
                    throw new InvalidOperationException(error);
                }
                // The contract breach: the moved asset's bytes are rewritten
                // too — a modification no asset.move declaration can cover.
                File.WriteAllText(
                    Path.Combine(Directory.GetCurrentDirectory(), destination),
                    "rewritten\n");
                AssetDatabase.Refresh();
                ctx.Changes.Moved(
                    new ChangeTarget(
                        "asset", destination,
                        AssetDatabase.AssetPathToGUID(destination)),
                    undoable: false);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Unsafe operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy: declares only the "scene.save" effect but overwrites
        /// a data asset — scene.save covers a saved .unity file, never an
        /// arbitrary asset modification (SPEC §C4).
        /// </summary>
        [OpWeaveOperation("test.contract.scene_save_lies", 1)]
        public sealed class SceneSaveLyingOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.scene_save_lies", 1)
                .Summary("Declares scene.save but modifies a data asset.")
                .Input("path", "unity.AssetPath", required: true)
                .Effects(EffectNames.SceneSave)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Unsafe)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.scene_save_lies", 1,
                            "{\"path\":\"Assets/OpWeaveExamples/ProbeScene.txt\"}"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                File.WriteAllText(
                    Path.Combine(
                        Directory.GetCurrentDirectory(),
                        ctx.Get<string>("path")),
                    "overwritten\n");
                AssetDatabase.Refresh();
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Unsafe operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy: declares Reconcilable but its Reconcile answers
        /// Completed whenever the artifact exists — the file it creates is
        /// the execution flag it checks, and it never proves the artifact's
        /// identity or content (SPEC §F5 judgement b). It also records no
        /// change, so only the world diff can expose the artifact for the
        /// damaged-evidence probe.
        /// </summary>
        [OpWeaveOperation("test.contract.flag_completed", 1)]
        public sealed class FlagReconcilingOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.flag_completed", 1)
                .Summary("Answers Completed whenever the artifact exists.")
                .Input("path", "unity.AssetPath", required: true)
                .Output("written", "core.Bool")
                .Effects(EffectNames.AssetCreate)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.flag_completed", 1,
                            "{\"path\":\"Assets/OpWeaveExamples/ProbeFlag.txt\"}", "written"),
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
                File.WriteAllText(FullPath(ctx.Get<string>("path")), "flag-content");
                AssetDatabase.Refresh();
                ctx.SetOutput("written", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var path = snapshot.ToToken()["path"]?.ToString();
                return path is not null && File.Exists(FullPath(path))
                    ? new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                        {
                            ["written"] = JsonValue.From(new JValue(true)),
                        })
                    : (ReconcileResult)new ReconcileResult.Unknown(
                        "The artifact is absent.");
            }

            public void Cancel(StepContext ctx)
            {
            }

            private static string FullPath(string assetPath) =>
                Path.Combine(Directory.GetCurrentDirectory(), assetPath);
        }

        /// <summary>
        /// Broken dummy: declares Idempotent but its second run reaches
        /// Execute and throws — a failure after the write-ahead record is
        /// never the safe precondition refusal the two-run check excuses.
        /// </summary>
        [OpWeaveOperation("test.contract.second_run_fails", 1)]
        public sealed class SecondRunFailingOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.second_run_fails", 1)
                .Summary("Fails its second run inside Execute.")
                .Effects(EffectNames.SceneModify)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For("test.contract.second_run_fails", 1),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                if (GameObject.Find("ContractSecondRun") is not null)
                {
                    throw new InvalidOperationException(
                        "The second run reached Execute and failed.");
                }
                var created = new GameObject("ContractSecondRun");
                ctx.Changes.Created(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(created),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(created).ToString()),
                    undoable: true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy shaped like the defect Issue #266 tracks in
        /// unity.asset.create_folder: the first run succeeds and creates
        /// its artifact, but the artifact's presence makes the second run's
        /// Preflight refuse — an Operation whose own preconditions forbid
        /// the second execution cannot keep the SPEC §F5 Idempotent
        /// contract, and the two-run check must report it.
        /// </summary>
        [OpWeaveOperation("test.contract.second_run_refused", 1)]
        public sealed class SecondRunRefusingOperation : IOperation
        {
            private const string Marker = "ContractRefused";

            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.second_run_refused", 1)
                .Summary("Refuses its second run inside Preflight.")
                .Effects(EffectNames.SceneModify)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For("test.contract.second_run_refused", 1),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                if (GameObject.Find(Marker) is not null)
                {
                    return new PreflightResult(
                        new[]
                        {
                            new Diagnostic(
                                "OW-TEST-000", Severity.Error,
                                "The destination the first run created is occupied."),
                        },
                        Array.Empty<PlannedChange>(),
                        JsonValue.From(new JObject()));
                }
                return new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));
            }

            public StepResult Execute(StepContext ctx)
            {
                var created = new GameObject(Marker);
                ctx.Changes.Created(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(created),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(created).ToString()),
                    undoable: true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// Broken dummy: the folder counterpart of
        /// <see cref="FlagReconcilingOperation"/> — declares Reconcilable but
        /// answers Completed whenever the folder it created exists, proving
        /// neither its identity nor its content (SPEC §F5 judgement b). The
        /// damaged-evidence probe must reach folder artifacts too, so a
        /// foreign file planted inside must turn the verdict Unknown.
        /// </summary>
        [OpWeaveOperation("test.contract.folder_completed", 1)]
        public sealed class FolderFlagOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.folder_completed", 1)
                .Summary("Answers Completed whenever the folder exists.")
                .Input("path", "unity.AssetPath", required: true)
                .Output("written", "core.Bool")
                .Effects(EffectNames.AssetCreate)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.folder_completed", 1,
                            "{\"path\":\"Assets/OpWeaveExamples/ProbeFolder\"}", "written"),
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
                Directory.CreateDirectory(FullPath(ctx.Get<string>("path")));
                AssetDatabase.Refresh();
                ctx.SetOutput("written", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var path = snapshot.ToToken()["path"]?.ToString();
                return path is not null && Directory.Exists(FullPath(path))
                    ? new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                        {
                            ["written"] = JsonValue.From(new JValue(true)),
                        })
                    : (ReconcileResult)new ReconcileResult.Unknown(
                        "The artifact is absent.");
            }

            public void Cancel(StepContext ctx)
            {
            }

            private static string FullPath(string assetPath) =>
                Path.Combine(Directory.GetCurrentDirectory(), assetPath);
        }

        /// <summary>
        /// Correct Reconcilable dummy shaped on the SPEC §F5
        /// <c>unity.component.add</c> row: adds a BoxCollider to the named
        /// GameObject and proves every verdict from the target's session
        /// handle and its component list — one new component of the added
        /// type carrying the serialized content a fresh addition has, every
        /// other component unchanged. The object's name is never evidence.
        /// </summary>
        [OpWeaveOperation("test.contract.component_add", 1)]
        public sealed class ComponentAddOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.component_add", 1)
                .Summary("Adds a BoxCollider to the named GameObject.")
                .Input("target", "core.String", required: true)
                .Output("added", "core.Bool")
                .Effects(EffectNames.SceneModify)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.component_add", 1,
                            "{\"target\":\"ProbeTarget\"}", "added"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                var target = GameObject.Find(ctx.Get<string>("target"));
                if (target is null)
                {
                    return new PreflightResult(
                        Array.Empty<Diagnostic>(),
                        Array.Empty<PlannedChange>(),
                        JsonValue.From(new JObject()));
                }
                var components = new JArray();
                foreach (var component in target.GetComponents<Component>())
                {
                    components.Add(new JObject
                    {
                        ["type"] = component is null
                            ? "<missing>"
                            : component.GetType().Name,
                        ["content"] = component is null
                            ? string.Empty
                            : EditorJsonUtility.ToJson(component),
                    });
                }
                // The probe reads the serialized content a fresh addition
                // carries; its add-and-destroy sets the scene's dirty flag —
                // an observable change of its own — so a clean scene is
                // re-saved to the same bytes, and a previously dirty one
                // keeps the caller's unsaved state as found: a conforming
                // Preflight leaves the world untouched.
                var scene = target.scene;
                var wasDirty = scene.isDirty;
                var probe = target.AddComponent<BoxCollider>();
                var addedContent = EditorJsonUtility.ToJson(probe);
                UnityEngine.Object.DestroyImmediate(probe);
                if (!wasDirty && scene.path.Length != 0)
                {
                    EditorSceneManager.SaveScene(scene);
                }
                return new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject
                    {
                        ["target"] = SessionHandle.Write(target),
                        ["components"] = components,
                        ["addedType"] = nameof(BoxCollider),
                        ["addedContent"] = addedContent,
                    }));
            }

            public StepResult Execute(StepContext ctx)
            {
                var target = GameObject.Find(ctx.Get<string>("target"));
                if (target is null)
                {
                    return StepResult.Failed(new Diagnostic(
                        "OW-TEST-000", Severity.Error,
                        "The named target GameObject does not exist."));
                }
                target.AddComponent<BoxCollider>();
                ctx.Changes.Modified(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(target),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(target).ToString()),
                    undoable: false);
                ctx.SetOutput("added", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var data = snapshot.ToToken();
                if (data["target"] is not { } handleToken ||
                    !SessionHandle.TryRead(handleToken, out var handle) ||
                    data["components"] is not JArray components ||
                    data["addedType"]?.ToString() is not { Length: > 0 } addedType ||
                    data["addedContent"]?.ToString() is not { } addedContent)
                {
                    return new ReconcileResult.Unknown(
                        "The snapshot is missing its evidence members.");
                }
                if (SessionHandle.Resolve(handle) is not GameObject target)
                {
                    return new ReconcileResult.Unknown(
                        "The identified GameObject can no longer be resolved.");
                }
                var live = target.GetComponents<Component>();
                if (live.Length == components.Count)
                {
                    return MatchesAll(live, components)
                        ? (ReconcileResult)new ReconcileResult.NotApplied()
                        : new ReconcileResult.Unknown(
                            "The component list kept its size but not its content.");
                }
                if (live.Length != components.Count + 1 ||
                    !MatchesAll(live, components))
                {
                    return new ReconcileResult.Unknown(
                        "The component list changed by more than the added " +
                            "component alone.");
                }
                // A new component is appended last: the earlier entries must
                // match the snapshot exactly and the last must be the added
                // type carrying the content a fresh addition has.
                var added = live[live.Length - 1];
                if (added is null ||
                    !string.Equals(
                        added.GetType().Name, addedType, StringComparison.Ordinal))
                {
                    return new ReconcileResult.Unknown(
                        "The added component is not the expected type.");
                }
                if (!string.Equals(
                    EditorJsonUtility.ToJson(added), addedContent,
                    StringComparison.Ordinal))
                {
                    return new ReconcileResult.Unknown(
                        "The added component's content is not the expected default.");
                }
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        ["added"] = JsonValue.From(new JValue(true)),
                    });
            }

            public void Cancel(StepContext ctx)
            {
            }

            // Whether the first <paramref name="components"/> entries of the
            // live component list equal the snapshot's entries, type and
            // serialized content both — the "other components unchanged"
            // half of the §F5 row.
            private static bool MatchesAll(Component[] live, JArray components)
            {
                for (var i = 0; i < components.Count; i++)
                {
                    if (components[i] is not JObject expected || i >= live.Length)
                    {
                        return false;
                    }
                    var liveType = live[i] is null
                        ? "<missing>"
                        : live[i].GetType().Name;
                    var liveContent = live[i] is null
                        ? string.Empty
                        : EditorJsonUtility.ToJson(live[i]);
                    if (!string.Equals(
                        expected["type"]?.ToString(), liveType,
                        StringComparison.Ordinal) ||
                        !string.Equals(
                            expected["content"]?.ToString(), liveContent,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Broken dummy: declares Reconcilable but its Reconcile answers
        /// Completed whenever a GameObject of the expected name exists —
        /// the existence of a name, which SPEC §F5 rules out as evidence.
        /// The probe's foreign same-named replacement keeps the name while
        /// damaging everything else, so the lie still shows.
        /// </summary>
        [OpWeaveOperation("test.contract.name_completed", 1)]
        public sealed class NameFlagOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.contract.name_completed", 1)
                .Summary("Answers Completed whenever the named object exists.")
                .Input("name", "core.String", required: true)
                .Output("present", "core.Bool")
                .Effects(EffectNames.SceneModify)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.contract.name_completed", 1,
                            "{\"name\":\"ProbeName\"}", "present"),
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
                var name = ctx.Get<string>("name");
                var created = new GameObject(name);
                ctx.Changes.Created(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(created),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(created).ToString()),
                    undoable: false);
                ctx.SetOutput("present", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var name = snapshot.ToToken()["name"]?.ToString();
                return name is not null && GameObject.Find(name) is not null
                    ? new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                        {
                            ["present"] = JsonValue.From(new JValue(true)),
                        })
                    : (ReconcileResult)new ReconcileResult.Unknown(
                        "The artifact is absent.");
            }

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
