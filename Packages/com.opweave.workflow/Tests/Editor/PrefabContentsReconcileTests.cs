#nullable enable

// T-W2.6-03 [Edit] tests (local:T-W2.6-03:prefab-contents-reconcile): the six
// SPEC §F5 states at Operation level for both Reconcilable Operations of this
// ticket — not executed; executed with no StepSucceeded record; a partial side
// effect; a pre-existing same-name artifact; the artifact changed by another
// operation; a simulated Editor restart — taken against the Preflight snapshot
// exactly as the §F4 recovery engine takes them: over the resource
// acquisitions the §F2 journal fold rebuilds, never over this test's own
// in-memory state.
//
// Verdict rule of the §F5 rows (SPEC §I.2 F5 table):
//   unity.prefab.open_contents — Completed only while the same step's Run
//     resource is open and a live editing root of the target Prefab exists;
//     NotApplied only when the pair provably never happened; Unknown when the
//     contents cannot be attributed to the step at all — because they were
//     already open when the step began, because another session or a second
//     copy is open, or because the open acquisition records no acquiring step
//     and so belongs to no step.
//   unity.prefab.save_contents — Completed only on an exact
//     PrefabContentFingerprint v1 match with a restorable output; never
//     NotApplied (§G6 item 5: the contents an interrupted Run lost cannot be
//     re-created by re-executing); Unknown otherwise.
// No case may create a second artifact or destroy one the step does not own,
// which every case asserts on the artifacts that survive the verdict.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.6-03:prefab-contents-reconcile — the six SPEC §F5 states for
    /// <c>unity.prefab.open_contents</c> and <c>unity.prefab.save_contents</c>.
    /// </summary>
    internal sealed class PrefabContentsReconcileTests
    {
        private const string OpenId = "unity.prefab.open_contents";
        private const string SaveId = "unity.prefab.save_contents";
        private const string OperationsAssembly = "OpWeave.Operations";

        private static RegistrySnapshot? s_snapshot;

        private readonly List<GameObject> _scratch = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _scratch)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            _scratch.Clear();
            PrefabContentsFixture.Cleanup();
        }

        private static RegistrySnapshot Snapshot() =>
            s_snapshot ??= Build();

        private static RegistrySnapshot Build()
        {
            var publication = new RegistryPublication();
            var seams = new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => TypeCache
                    .GetTypesWithAttribute<OpWeaveOperationAttribute>()
                    .Where(type => string.Equals(
                        type.Assembly.GetName().Name, OperationsAssembly,
                        StringComparison.Ordinal)),
                ProviderTypes: () =>
                    TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());
            var build = RegistryBuildHost.Rebuild(
                seams, RegistryOperationStage.Production(), publication);
            Assert.That(build.Snapshot, Is.Not.Null);
            return build.Snapshot!;
        }

        private static IOperation Standard(string id)
        {
            var registered = Snapshot().Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            return registered!.Implementation!;
        }

        private static (string Path, string Guid) ProbePrefab(
            string fileName, string? childName = null)
        {
            PrefabContentsFixture.CreateProbePrefab(fileName, childName);
            var path = PrefabContentsFixture.ProbePath(fileName);
            return (path, PrefabContentsFixture.GuidOf(path));
        }

        // The step's own §F5 evidence: its context plus Preflight's snapshot,
        // taken over the contents the step is about to work on (SPEC §F5 rule
        // 1 — the snapshot exists before any side effect).
        private static (UnityStepContext Context, JsonValue Snapshot) PrepareOpen(
            PrefabContentsFixture.Harness harness, string guid, string path)
        {
            var context = harness.OpenContext(guid, path);
            return (context, Preflight(Standard(OpenId), context));
        }

        private static (UnityStepContext Context, JsonValue Snapshot) PrepareSave(
            PrefabContentsFixture.Harness harness, JsonValue rootRef)
        {
            var context = harness.SaveContext(rootRef);
            return (context, Preflight(Standard(SaveId), context));
        }

        private static JsonValue Preflight(IOperation operation, StepContext context)
        {
            var preflight = operation.Preflight(context);
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(
                    d => d.Code + " " + d.Message)));
            return preflight.Snapshot;
        }

        // Opens the contents through the standard Operation and returns the
        // hand-off Ref its root output published — the value a data edge would
        // carry to the save step.
        private static JsonValue OpenAndPublish(
            PrefabContentsFixture.Harness harness, string guid, string path)
        {
            var (context, _) = PrepareOpen(harness, guid, path);
            Assert.AreEqual(
                StepResultKind.Succeeded, Standard(OpenId).Execute(context).Kind);
            return PrefabContentsFixture.Harness.RootRef(context);
        }

        // ── unity.prefab.open_contents ──────────────────────────────────────

        // §F5 case 1 (not executed): no acquisition was ever recorded, nothing
        // of the target is open and nothing was open when the step began, so
        // the effect provably did not happen and re-running opens one session.
        [Test]
        public void OpenContents_NotExecuted_ReconcilesNotApplied()
        {
            var (path, guid) = ProbePrefab("r-open-none.prefab", "Body");
            var (_, snapshot) = PrepareOpen(
                new PrefabContentsFixture.Harness(), guid, path);
            var recovery = new PrefabContentsFixture.Harness();

            var verdict = Standard(OpenId).Reconcile(
                recovery.OpenContext(guid, path), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.NotApplied>(),
                Describe(verdict));
            Assert.That(recovery.Records, Is.Empty,
                "a reconciliation journals nothing and opens nothing of its own");
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty);
        }

        // §F5 case 2 (executed, no StepSucceeded record): the journal fold
        // rebuilt the step's open acquisition and the editing root of the
        // target is live — the row's full Completed evidence, with the root
        // output restorable to the very object an uninterrupted run published.
        [Test]
        public void OpenContents_ExecutedWithoutCompletionRecord_ReconcilesCompleted()
        {
            var (path, guid) = ProbePrefab("r-open-live.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var (context, snapshot) = PrepareOpen(harness, guid, path);
            var open = Standard(OpenId);
            Assert.AreEqual(StepResultKind.Succeeded, open.Execute(context).Kind);
            var published = PrefabContentsFixture.Harness.RootRef(context);
            var recovery = new PrefabContentsFixture.Harness();
            recovery.SeedFoldedAcquisition(guid, path);

            var verdict = open.Reconcile(recovery.OpenContext(guid, path), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Completed>(),
                Describe(verdict));
            var restored = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That(restored.Keys, Is.EquivalentTo(new[] { "root" }),
                "every declared output is restored (§F5 rule 6)");
            var resolver = new RefResolver(PrefabContentsResources.FindEditingRoot);
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(
                    resolver.Resolve<GameObject>(published).Value!),
                PrefabContentsOwnership.SessionIdOf(
                    resolver.Resolve<GameObject>(restored["root"]).Value!),
                "the restored output names the same contents as the step's own");
            Assert.AreEqual(
                "prefab-contents:" + guid,
                restored["root"].ToToken()["scene"]!.ToString());
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Has.Count.EqualTo(1),
                "reconciliation must leave the step's live contents alone");
        }

        // §F5 case 3 (partial side effect): the acquisition is journaled and
        // still open in the fold, but no contents of the target exist any more
        // — the step's effect is partly undone, and re-running would open a
        // session the journal cannot pair. NeedsReview, never a guess.
        [Test]
        public void OpenContents_AcquisitionWithoutContents_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-open-partial.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var (context, snapshot) = PrepareOpen(harness, guid, path);
            var open = Standard(OpenId);
            Assert.AreEqual(StepResultKind.Succeeded, open.Execute(context).Kind);
            PrefabUtility.UnloadPrefabContents(
                PrefabContentsFixture.Harness.EditingRoots(path)[0]);
            var recovery = new PrefabContentsFixture.Harness();
            recovery.SeedFoldedAcquisition(guid, path);

            var verdict = open.Reconcile(recovery.OpenContext(guid, path), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "an Unknown verdict opens nothing and deletes nothing");
        }

        // §F5 case 4 (pre-existing same-name artifact): contents of the target
        // are open that this step never recorded acquiring — another session's
        // artifact. Re-executing would open a second copy and adopting them
        // would claim another owner's work, so only Unknown is honest.
        [Test]
        public void OpenContents_PreExistingContents_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-open-preexisting.prefab", "Body");
            var someoneElse = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(someoneElse);
            var (_, snapshot) = PrepareOpen(
                new PrefabContentsFixture.Harness(), guid, path);
            var recovery = new PrefabContentsFixture.Harness();

            var verdict = Standard(OpenId).Reconcile(
                recovery.OpenContext(guid, path), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain("did not acquire"),
                "the reason must name the missing acquisition");
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Has.Count.EqualTo(1),
                "the pre-existing contents stay exactly as they were");
            Assert.That(recovery.Records, Is.Empty,
                "reconciliation never journals an acquisition for a step that did not run");
        }

        // §F5 case 5 (artifact changed by another operation): this step's
        // acquisition is open, and a second contents session of the same
        // Prefab appeared as well — two roots, no way to tell which one this
        // step opened. The row forbids guessing and the §F5 common rule 4
        // forbids re-running into a third copy.
        [Test]
        public void OpenContents_SecondSessionOfTheTarget_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-open-two.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var (context, snapshot) = PrepareOpen(harness, guid, path);
            var open = Standard(OpenId);
            Assert.AreEqual(StepResultKind.Succeeded, open.Execute(context).Kind);
            var other = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(other);
            var recovery = new PrefabContentsFixture.Harness();
            recovery.SeedFoldedAcquisition(guid, path);

            var verdict = open.Reconcile(recovery.OpenContext(guid, path), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain("cannot be identified"));
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Has.Count.EqualTo(2),
                "neither session is unloaded or duplicated by the verdict");
        }

        // §F5 case 6 (Editor restart, simulated as W4.5 (AT-02) and W4.6 prove
        // for real): a new session whose journal fold still shows the
        // acquisition, with a restart having discarded the preview scene and
        // every session handle with it. The contents are gone, yet the step's
        // effect can be neither undone nor re-created, so the pair closes only
        // through review.
        [Test]
        public void OpenContents_AfterSimulatedEditorRestart_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-open-restart.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var (context, snapshot) = PrepareOpen(harness, guid, path);
            var open = Standard(OpenId);
            Assert.AreEqual(StepResultKind.Succeeded, open.Execute(context).Kind);
            PrefabUtility.UnloadPrefabContents(
                PrefabContentsFixture.Harness.EditingRoots(path)[0]);

            var restarted = new PrefabContentsFixture.Harness(1);
            restarted.SeedFoldedAcquisition(guid, path);
            var verdict = open.Reconcile(restarted.OpenContext(guid, path), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain("no longer open"),
                "the reason must name the lost contents session");
            Assert.That(harness.Records, Has.Count.EqualTo(1),
                "the restart verdict never re-acquires");
        }

        // §F5 common rule 4 seen from the resource side: the step's own
        // acquisition is journaled while its contents were never loaded, and the
        // only contents of the target that are open were already open when the
        // step began. They are somebody else's artifact however faithfully the
        // pair is open, so the row's Completed evidence fails and re-executing
        // would open a second copy.
        [Test]
        public void OpenContents_JournaledAcquisitionWithoutLoadOverForeignContents_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-open-foreign.prefab", "Body");
            var someoneElse = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(someoneElse);
            var (_, snapshot) = PrepareOpen(
                new PrefabContentsFixture.Harness(), guid, path);
            var recovery = new PrefabContentsFixture.Harness();
            recovery.SeedFoldedAcquisition(guid, path);

            var verdict = Standard(OpenId).Reconcile(
                recovery.OpenContext(guid, path), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain("already open when the step began"),
                "the reason must name the unattributable open contents");
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Has.Count.EqualTo(1),
                "the verdict neither adopts nor unloads the foreign session");
            Assert.That(recovery.Records, Is.Empty,
                "reconciliation never re-acquires the open pair");
        }

        // The §F5 Unknown of a resource whose owner is unknown: the journal fold
        // rebuilt an acquisition whose record carried no step, so no step may
        // claim it. Neither the row's Completed nor its NotApplied condition
        // holds — NeedsReview decides, never a verdict built on a guess.
        [Test]
        public void OpenContents_AcquisitionWithoutAnAcquiringStep_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-open-unattributed.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var (context, snapshot) = PrepareOpen(harness, guid, path);
            var open = Standard(OpenId);
            Assert.AreEqual(StepResultKind.Succeeded, open.Execute(context).Kind);
            var recovery = new PrefabContentsFixture.Harness(4);
            recovery.SeedUnattributedFoldedAcquisition(guid, path);

            var verdict = open.Reconcile(recovery.OpenContext(guid, path), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain("cannot be determined"),
                "the reason must name the state that cannot be determined");
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Has.Count.EqualTo(1),
                "an Unknown verdict opens nothing and unloads nothing");
            Assert.That(recovery.Records, Is.Empty,
                "the unattributable acquisition is never re-journaled");
        }

        // ── unity.prefab.save_contents ──────────────────────────────────────

        // §F5 case 1 (not executed): the §I.2 row gives this Operation no
        // NotApplied verdict at all — a destination holding its pre-save
        // content is indistinguishable from one this step saved and someone
        // else later changed — so the fingerprint mismatch stays Unknown.
        [Test]
        public void SaveContents_NotExecuted_ReconcilesUnknownAndNeverNotApplied()
        {
            var (path, guid) = ProbePrefab("r-save-none.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var rootRef = OpenAndPublish(harness, guid, path);
            var (_, snapshot) = PrepareSave(harness, rootRef);
            AddEdit(path, "EditedButNeverSaved");
            PrefabUtility.UnloadPrefabContents(
                PrefabContentsFixture.Harness.EditingRoots(path)[0]);

            var verdict = Standard(SaveId).Reconcile(
                new PrefabContentsFixture.Harness().SaveContext(
                    PrefabContentsFixture.PrefabRef(guid, path)), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(verdict, Is.Not.InstanceOf<ReconcileResult.NotApplied>(),
                "save_contents never answers NotApplied (§I.2 §F5 row)");
            Assert.That(verdict, Is.Not.InstanceOf<ReconcileResult.Completed>(),
                "an asset the step never wrote is not its artifact");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset);
            Assert.That(asset!.transform.childCount, Is.EqualTo(1),
                "the destination keeps exactly its pre-save content");
        }

        // §F5 case 2 (executed, no StepSucceeded record): the saved asset's
        // content fingerprint equals the one Preflight recorded from the
        // contents, the destination provably did not hold that content before
        // the step, and the PrefabAssetRef output is restorable — Completed on
        // the full §G6.1 evidence row, byte-identical to an uninterrupted run.
        [Test]
        public void SaveContents_ExecutedWithoutCompletionRecord_ReconcilesCompleted()
        {
            var (path, guid) = ProbePrefab("r-save-saved.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var rootRef = OpenAndPublish(harness, guid, path);
            AddEdit(path, "EditedAndSaved");
            var (saveContext, snapshot) = PrepareSave(harness, rootRef);
            var save = Standard(SaveId);
            Assert.AreEqual(StepResultKind.Succeeded, save.Execute(saveContext).Kind);
            var published = PrefabContentsFixture.Harness.PrefabOutput(saveContext);

            var verdict = save.Reconcile(
                new PrefabContentsFixture.Harness().SaveContext(
                    PrefabContentsFixture.PrefabRef(guid, path)), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Completed>(),
                Describe(verdict));
            var restored = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That(restored.Keys, Is.EquivalentTo(new[] { "prefab" }));
            Assert.AreEqual(
                published.CanonicalText, restored["prefab"].CanonicalText,
                "the restored output is the very Ref the step published");
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "the fingerprint read is read-only and closes its own contents");
        }

        // §F5 case 3 (partial side effect): the destination holds some — but
        // not all — of the content the step meant to write. §G6.1 forbids
        // completing on anything short of an exact fingerprint match, and a
        // partial artifact must never be deleted by the verdict either.
        [Test]
        public void SaveContents_PartialWrite_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-save-partial.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var rootRef = OpenAndPublish(harness, guid, path);
            AddEdit(path, "FirstEdit");
            AddEdit(path, "SecondEdit");
            var (_, snapshot) = PrepareSave(harness, rootRef);

            // The interrupted write: only one of the two edits reached the file.
            var root = PrefabContentsFixture.Harness.EditingRoots(path)[0];
            UnityEngine.Object.DestroyImmediate(
                root.transform.Find("SecondEdit")!.gameObject);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);

            var verdict = Standard(SaveId).Reconcile(
                new PrefabContentsFixture.Harness().SaveContext(
                    PrefabContentsFixture.PrefabRef(guid, path)), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason, Does.Contain("differs"));
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset, "the verdict deletes nothing");
            Assert.IsNotNull(asset!.transform.Find("FirstEdit"));
            Assert.IsNull(asset.transform.Find("SecondEdit"),
                "the content that never reached the file stays absent");
        }

        // §F5 case 4 (pre-existing same-name artifact): the destination already
        // held exactly the content the step intended to save when Preflight
        // ran, so a matching fingerprint afterwards proves nothing about this
        // step's own write — §F5 common rule 4 keeps that Unknown.
        [Test]
        public void SaveContents_DestinationAlreadyHeldTheContent_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-save-preexisting.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var rootRef = OpenAndPublish(harness, guid, path);
            var (saveContext, snapshot) = PrepareSave(harness, rootRef);
            var save = Standard(SaveId);

            // The step's own write of content the file already had.
            Assert.AreEqual(StepResultKind.Succeeded, save.Execute(saveContext).Kind);

            var verdict = save.Reconcile(
                new PrefabContentsFixture.Harness().SaveContext(
                    PrefabContentsFixture.PrefabRef(guid, path)), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason, Does.Contain("already held"),
                "the reason must name the pre-existing destination content");
        }

        // §F5 case 5 (artifact changed by another operation): the step saved,
        // then a later writer changed the asset, so the destination no longer
        // matches the recorded fingerprint. The step may well have run, but
        // §G6.1 allows Completed for an exact match only.
        [Test]
        public void SaveContents_DestinationModifiedAfterTheSave_ReconcilesUnknown()
        {
            var (path, guid) = ProbePrefab("r-save-modified.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var rootRef = OpenAndPublish(harness, guid, path);
            AddEdit(path, "EditedByTheStep");
            var (saveContext, snapshot) = PrepareSave(harness, rootRef);
            var save = Standard(SaveId);
            Assert.AreEqual(StepResultKind.Succeeded, save.Execute(saveContext).Kind);

            // The later writer.
            var contents = PrefabUtility.LoadPrefabContents(path);
            AddChildTo(contents, "EditedBySomeoneElse");
            PrefabUtility.SaveAsPrefabAsset(contents, path);
            PrefabUtility.UnloadPrefabContents(contents);

            var verdict = save.Reconcile(
                new PrefabContentsFixture.Harness().SaveContext(
                    PrefabContentsFixture.PrefabRef(guid, path)), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                Describe(verdict));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason, Does.Contain("differs"));
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset!.transform.Find("EditedBySomeoneElse"),
                "the later writer's artifact is never rolled back");
        }

        // §F5 case 6 (Editor restart, simulated): the saved asset is durable —
        // a new session with no open resources and no context carried over
        // still re-identifies it by guid and content fingerprint and restores
        // the output, which is why this row can complete across a restart.
        [Test]
        public void SaveContents_AfterSimulatedEditorRestart_ReconcilesCompleted()
        {
            var (path, guid) = ProbePrefab("r-save-restart.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var rootRef = OpenAndPublish(harness, guid, path);
            AddEdit(path, "SurvivesTheRestart");
            var (saveContext, snapshot) = PrepareSave(harness, rootRef);
            Assert.AreEqual(StepResultKind.Succeeded,
                Standard(SaveId).Execute(saveContext).Kind);

            // A fresh session: no Run resources rebuilt, nothing of the
            // original step left, the target named only by the snapshot.
            var restarted = new PrefabContentsFixture.Harness(2);
            var verdict = Standard(SaveId).Reconcile(
                restarted.SaveContext(PrefabContentsFixture.PrefabRef(guid, path)),
                snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Completed>(),
                Describe(verdict));
            var restored = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.AreEqual(guid, restored["prefab"].ToToken()["guid"]!.ToString());
            Assert.AreEqual(path, restored["prefab"].ToToken()["path"]!.ToString());
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        // Edits the contents the step holds open — the state save_contents is
        // about to write and the state the expected fingerprint comes from.
        private static void AddEdit(string path, string name) =>
            AddChildTo(PrefabContentsFixture.Harness.EditingRoots(path)[0], name);

        private static void AddChildTo(GameObject root, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(root.transform, worldPositionStays: false);
        }

        private static string Describe(ReconcileResult verdict) => verdict switch
        {
            ReconcileResult.Unknown unknown => "Unknown: " + unknown.Reason,
            ReconcileResult.Completed => "Completed",
            ReconcileResult.NotApplied => "NotApplied",
            _ => verdict.GetType().Name,
        };
    }
}
