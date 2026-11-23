#nullable enable

// T-W2.6-03 [Edit] tests (local:T-W2.6-03:prefab-contents-ops): SPEC §G6 item 5
// and §E6 item 4 for the two Prefab-content Operations. Operation level:
// open_contents journals the prefab_edit acquisition before the contents exist,
// opens exactly one editing session, publishes its root, and a second attempt
// of the same step adopts the still-open acquisition instead of loading a
// second copy — while a second open_contents step of one Prefab is not such a
// re-execution and gets no session of its own either: a Run holds one contents
// session of a Prefab, because §G2 gives both sessions the same
// prefab-contents:<guid> reference, so the second open is refused with
// OW-UNITY-002 before it acquires or loads anything; save_contents writes the
// contents back, unloads them and pairs the release; a root that is not opened
// Prefab contents is refused with OW-UNITY-004, and a session of the target
// this Run never opened is refused with OW-UNITY-002 without writing, unloading
// or releasing anything. The release seam unloads only the root its own
// acquisition bound when it loaded — never a session that merely matches the
// journaled asset path — and fails safe, leaving every open session alone, when
// no binding proves ownership. Run level (SPEC §E6 test 2, §G6 test 2): a Run
// that fails after open_contents still discards and releases the unsaved
// contents at Run end, an open→save Run hands the editing root across the data
// edge through the production prefab-contents Ref lookup and closes the
// resource, a Run whose second open of one Prefab is refused releases the one
// session it really holds and leaves no contents behind, an acquisition that
// never loaded leaves an unrelated session of the same Prefab open and stays
// open itself, an acquisition whose release was interrupted between its unload
// and its record is closed by recovery from the durable evidence that unload
// left while the session nobody of this Run opened survives, and the same
// interrupted state with that evidence absent ends the recovery NeedsReview
// with the live session untouched (SPEC §E6 item 4, §F0, §F4), and a
// Prefab Mode stage of the target survives a failed Run untouched.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.6-03:prefab-contents-ops — execution, Run-resource pairing and
    /// Run-end release of <c>unity.prefab.open_contents</c> and
    /// <c>unity.prefab.save_contents</c>.
    /// </summary>
    internal sealed class PrefabContentsOperationTests
    {
        private const string OpenId = "unity.prefab.open_contents";
        private const string SaveId = "unity.prefab.save_contents";
        private const string OperationsAssembly = "OpWeave.Operations";
        private const string WorkflowId = "example.prefabcontents";

        // The standard set is registered once for the class: the snapshot is
        // immutable and both Operations hold no state (SPEC §C2 item 4).
        private static RegistrySnapshot? s_snapshot;

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<GameObject> _scratch = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeavePrefabContentsTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
            if (Directory.Exists(_root))
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                }
                catch (Exception)
                {
                    // A held journal file must not fail the test that wrote it.
                }
            }
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

        // ── The registered standard Operations under test ───────────────────

        private static RegistrySnapshot Snapshot() =>
            s_snapshot ??= BuildStandardRegistry();

        private static RegistrySnapshot BuildStandardRegistry()
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
            Assert.That(build.Stale, Is.False, "the standard registry must be current");
            Assert.That(build.Snapshot, Is.Not.Null);
            return build.Snapshot!;
        }

        // The live standard instance, taken from the Registry the Runner
        // resolves it through — the test assembly does not reference
        // OpWeave.Operations, and the registered object is the faithful seam
        // anyway.
        private static IOperation Standard(string id)
        {
            var registered = Snapshot().Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            Assert.That(registered!.Implementation, Is.InstanceOf<IOperation>(), id);
            return registered.Implementation!;
        }

        // Creates the probe Prefab and returns its (path, guid) pair — every
        // case of this ticket starts from an asset that really exists.
        private static (string Path, string Guid) ProbePrefab(
            string fileName, string? childName = null)
        {
            PrefabContentsFixture.CreateProbePrefab(fileName, childName);
            var path = PrefabContentsFixture.ProbePath(fileName);
            return (path, PrefabContentsFixture.GuidOf(path));
        }

        // ── Operation level: open_contents ──────────────────────────────────

        // SPEC §G6 item 5, §E6 item 4: the acquisition is journaled before the
        // contents exist, the contents open as one editing session re-found
        // through its preview scene, and the step publishes its root.
        [Test]
        public void OpenContents_JournalsTheResourceOpensTheContentsAndPublishesTheRoot()
        {
            var (path, guid) = ProbePrefab("open.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var context = harness.OpenContext(guid, path);
            var operation = Standard(OpenId);

            var preflight = operation.Preflight(context);
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(
                    d => d.Code + " " + d.Message)));
            var snapshot = preflight.Snapshot.ToToken();
            Assert.AreEqual(guid, snapshot["prefabGuid"]!.ToString());
            Assert.AreEqual(path, snapshot["prefabPath"]!.ToString());
            Assert.AreEqual(
                0, snapshot["contentsOpenAtPreflight"]!.Value<int>(),
                "no contents of the target are open yet");

            var executed = operation.Execute(context);

            Assert.AreEqual(StepResultKind.Succeeded, executed.Kind,
                executed.Error is null ? "" : executed.Error.Code + " " + executed.Error.Message);
            Assert.That(harness.Records, Has.Count.EqualTo(1),
                "the acquisition is journaled once");
            Assert.AreEqual(JournalRecordType.ResourceAcquired, harness.Records[0].Type);
            Assert.AreEqual(0, harness.Records[0].Step);
            Assert.AreEqual(PrefabContentsResources.Kind, harness.Records[0].Kind);
            Assert.That(harness.Records[0].DataText, Does.Contain(guid));
            Assert.That(harness.Records[0].DataText, Does.Contain(path));
            Assert.That(
                ((IRunResourceRegistry)context).IsOpen(
                    PrefabContentsResources.Kind,
                    PrefabContentsResources.Data(guid, path)),
                Is.EqualTo(RunResourceOpenState.OpenByThisStep),
                "the Run holds the contents acquisition of the step that made it");
            var roots = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.That(roots, Has.Count.EqualTo(1),
                "the opened contents are re-found through their preview scene");
            var rootRef = PrefabContentsFixture.Harness.RootRef(context);
            Assert.AreEqual("gameObject", rootRef.ToToken()["kind"]!.ToString());
            Assert.AreEqual(
                "prefab-contents:" + guid,
                rootRef.ToToken()["scene"]!.ToString(),
                "SPEC §G2: inside opened contents the scene key names the Prefab");
        }

        // SPEC §F5 common rule 4: a re-execution never duplicates the artifact.
        // The same step attempted twice adopts the open acquisition — one
        // resource record, one editing session — instead of loading a copy.
        [Test]
        public void OpenContents_SecondAttemptAdoptsTheOpenAcquisition()
        {
            var (path, guid) = ProbePrefab("again.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var context = harness.OpenContext(guid, path);
            var operation = Standard(OpenId);

            Assert.AreEqual(StepResultKind.Succeeded, operation.Execute(context).Kind);
            var firstRoot = PrefabContentsFixture.Harness.EditingRoots(path)[0];

            var again = operation.Execute(context);

            Assert.AreEqual(StepResultKind.Succeeded, again.Kind);
            Assert.That(harness.Records, Has.Count.EqualTo(1),
                "an already-open pair is never acquired a second time: " +
                string.Join(",", harness.Records.Select(r => r.Type.ToString())));
            var roots = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.That(roots, Has.Count.EqualTo(1),
                "the second attempt must not open a second copy of the contents");
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(firstRoot),
                PrefabContentsOwnership.SessionIdOf(roots[0]),
                "the attempt publishes the contents this Run already holds");
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(firstRoot),
                PrefabContentsOwnership.SessionIdOf(
                    new RefResolver(PrefabContentsResources.FindEditingRoot)
                        .Resolve<GameObject>(
                            PrefabContentsFixture.Harness.RootRef(context)).Value!),
                "the published root must still be the live contents");
        }

        // SPEC §G2 and §J: every GameObject inside an open Prefab session is
        // addressed through the one scene key prefab-contents:<guid>, resolved
        // from the Run resource, so a second live session of the same guid gives
        // that key two candidates and every Ref naming them — the second step's
        // own output included — names an ambiguity no downstream step can
        // resolve and no §F5 row can settle. A second open_contents step of one
        // Prefab is therefore not a re-execution of the first (§F5: its evidence
        // is the same step's own resource) and it gets a session of its own
        // neither: Preflight and Execute both refuse it with OW-UNITY-002, before
        // the acquisition and before the load, so the one record stays the first
        // step's, the one session stays the root that step published, and the
        // §G2 lookup keeps exactly one answer. That one session's Run end —
        // exactly one release and no editing root left — is the Run-level test
        // below.
        [Test]
        public void OpenContents_SecondStepOfTheSamePrefab_RefusesToOpenASecondSession()
        {
            var (path, guid) = ProbePrefab("two-steps.prefab", "Body");
            var operation = Standard(OpenId);
            var firstStep = new PrefabContentsFixture.Harness(0);
            var firstContext = firstStep.OpenContext(guid, path);
            Assert.AreEqual(
                StepResultKind.Succeeded, operation.Execute(firstContext).Kind);
            var own = PrefabContentsOwnership.FindOwned(firstStep.Scope, guid, path);
            Assert.IsNotNull(own, "the first step's load bound the root it opened");
            var ownRootId = PrefabContentsOwnership.SessionIdOf(own!);
            var data = PrefabContentsResources.Data(guid, path);

            var secondStep = firstStep.AnotherStep(1);
            var secondContext = secondStep.OpenContext(guid, path);
            Assert.That(
                ((IRunResourceRegistry)secondContext).IsOpen(
                    PrefabContentsResources.Kind, data),
                Is.EqualTo(RunResourceOpenState.NotOpen),
                "the first step's acquisition is never the second step's evidence");
            Assert.IsTrue(
                ((IRunResourceRegistry)secondContext).IsHeldByRun(
                    PrefabContentsResources.Kind, data),
                "the Run does hold one contents session of the target");

            var preflight = operation.Preflight(secondContext);
            Assert.That(
                preflight.Diagnostics.Any(d => d.Code == ErrorCodes.OW_UNITY_002),
                Is.True,
                "Preflight must refuse a second session of the Prefab: " +
                string.Join("; ", preflight.Diagnostics.Select(
                    d => d.Code + " " + d.Message)));
            Assert.AreEqual(
                "prefab",
                preflight.Diagnostics.First(d => d.Code == ErrorCodes.OW_UNITY_002).Port,
                "the refusal names the port it cannot resolve unambiguously");

            var executed = operation.Execute(secondContext);

            Assert.AreEqual(StepResultKind.Failed, executed.Kind);
            Assert.IsNotNull(executed.Error);
            Assert.AreEqual(ErrorCodes.OW_UNITY_002, executed.Error!.Code);
            Assert.AreEqual("prefab", executed.Error!.Port);
            Assert.IsFalse(
                secondContext.Outputs.TryGetValue("root", out _),
                "a refusal publishes no root of a session it never opened");
            Assert.That(
                firstStep.Records.Select(r => r.Type),
                Is.EqualTo(new[] { JournalRecordType.ResourceAcquired }),
                "the refusal acquires nothing of its own: " + string.Join(
                    ",", firstStep.Records.Select(r => r.Type.ToString())));
            var roots = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.That(roots, Has.Count.EqualTo(1), "the refusal opens nothing");
            Assert.AreEqual(
                ownRootId, PrefabContentsOwnership.SessionIdOf(roots[0]),
                "the session left open is the one the first step opened");
            Assert.That(
                ((IRunResourceRegistry)firstContext).IsOpen(
                    PrefabContentsResources.Kind, data),
                Is.EqualTo(RunResourceOpenState.OpenByThisStep),
                "the first step's own acquisition is untouched by the refusal");
            Assert.IsNotNull(
                PrefabContentsResources.FindEditingRoot(guid),
                "one session per Prefab keeps the §G2 prefab-contents lookup " +
                "unambiguous for every step downstream");
        }

        // The other side of the same rule: an open acquisition whose record
        // carries no acquiring step can neither be adopted by this step nor
        // counted as absent, and loading a copy here could duplicate the
        // artifact §F5 forbids. The step fails with the §J code for a reference
        // that resolves to more than one target and journals nothing.
        [Test]
        public void OpenContents_UnattributableOpenAcquisition_RefusesToDuplicateIt()
        {
            var (path, guid) = ProbePrefab("open-unattributed.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            harness.SeedUnattributedFoldedAcquisition(guid, path);
            var context = harness.OpenContext(guid, path);

            var executed = Standard(OpenId).Execute(context);

            Assert.AreEqual(StepResultKind.Failed, executed.Kind);
            Assert.IsNotNull(executed.Error);
            Assert.AreEqual(ErrorCodes.OW_UNITY_002, executed.Error!.Code);
            Assert.AreEqual(
                "prefab", executed.Error!.Port, "the refusal names the port it cannot resolve");
            Assert.That(harness.Records, Is.Empty,
                "a refusal journals no acquisition of its own");
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "a refusal opens nothing");
        }

        // ── Operation level: save_contents ──────────────────────────────────

        // SPEC §G6 item 5: SaveAsPrefabAsset writes the contents into their own
        // source asset, UnloadPrefabContents closes the session, the Run
        // resource is paired with its release, the §F7 change is reported and
        // the output names the persisted asset — never the destroyed contents.
        [Test]
        public void SaveContents_WritesBackUnloadsAndReleasesTheResource()
        {
            var (path, guid) = ProbePrefab("save.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var openContext = harness.OpenContext(guid, path);
            Assert.AreEqual(
                StepResultKind.Succeeded, Standard(OpenId).Execute(openContext).Kind);
            var rootRef = PrefabContentsFixture.Harness.RootRef(openContext);
            var child = new GameObject("AddedByTheRun");
            child.transform.SetParent(
                PrefabContentsFixture.Harness.EditingRoots(path)[0].transform,
                worldPositionStays: false);
            _scratch.Add(child);

            var context = harness.SaveContext(rootRef);
            var operation = Standard(SaveId);
            var preflight = operation.Preflight(context);
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(
                    d => d.Code + " " + d.Message)));

            var executed = operation.Execute(context);

            Assert.AreEqual(StepResultKind.Succeeded, executed.Kind,
                executed.Error is null ? "" : executed.Error.Code + " " + executed.Error.Message);
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(saved, "the saved asset must load");
            Assert.IsNotNull(
                saved!.transform.Find("AddedByTheRun"),
                "the edit must reach the asset file");
            Assert.That(PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "the editing session is closed by the save");
            Assert.That(
                ((IRunResourceRegistry)context).IsOpen(
                    PrefabContentsResources.Kind,
                    PrefabContentsResources.Data(guid, path)),
                Is.EqualTo(RunResourceOpenState.NotOpen),
                "the save releases the Run resource");
            Assert.IsFalse(
                ((IRunResourceRegistry)context).IsHeldByRun(
                    PrefabContentsResources.Kind,
                    PrefabContentsResources.Data(guid, path)),
                "no open pair is left for the §E6 item 4 release pass");
            Assert.That(
                harness.Records.Select(r => r.Type),
                Is.EqualTo(new[]
                {
                    JournalRecordType.ResourceAcquired,
                    JournalRecordType.ResourceReleased,
                }), "the acquisition and its release pair in order");
            var change = harness.Changes.Records.Single();
            Assert.AreEqual(ChangeAction.Saved, change.Action);
            Assert.AreEqual("asset", change.Target.Kind);
            Assert.AreEqual(path, change.Target.Path);
            Assert.AreEqual(guid, change.Target.Guid);
            Assert.IsFalse(change.Undoable,
                "an asset write is not undoable (SPEC §G3 item 3)");
            var output = PrefabContentsFixture.Harness.PrefabOutput(context);
            Assert.AreEqual("prefabAsset", output.ToToken()["kind"]!.ToString());
            Assert.AreEqual(guid, output.ToToken()["guid"]!.ToString());
            Assert.AreEqual(path, output.ToToken()["path"]!.ToString());
        }

        // The "root" port must name opened Prefab contents: a plain scene
        // GameObject is the §I.2 mix-up code, and nothing is written.
        [Test]
        public void SaveContents_RejectsARootThatIsNotOpenedContents()
        {
            var plain = new GameObject("NotPrefabContents");
            _scratch.Add(plain);
            var rootRef = new RefResolver().ToRef(plain, "unity.GameObjectRef");
            var harness = new PrefabContentsFixture.Harness();
            var context = harness.SaveContext(rootRef);

            var preflight = Standard(SaveId).Preflight(context);

            Assert.That(
                preflight.Diagnostics.Any(d => d.Code == ErrorCodes.OW_UNITY_004),
                Is.True,
                "a scene object is not Prefab contents: " + string.Join(
                    "; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.AreEqual(
                "root",
                preflight.Diagnostics.First(d => d.Code == ErrorCodes.OW_UNITY_004).Port);
            Assert.That(harness.Changes.Records, Is.Empty,
                "the refused save writes nothing");
        }

        // The ownership half of the same §G2 lookup on the write side: the
        // prefab-contents:<guid> key names *a* session of the Prefab, never the
        // one this Run opened, so a root no acquisition of this Run bound when
        // its load produced it is somebody else's editing session. Here the Run
        // holds an acquisition of the target whose contents never loaded — the
        // state the write-ahead record leaves when the process stops before
        // LoadPrefabContents — while a session somebody else opened is the one
        // root the §G2 lookup answers. Handed that root, save_contents refuses
        // with §J's ambiguous reference and changes nothing: the asset keeps its
        // content, the foreign session stays open with its unsaved edit, no
        // §F7 change is reported, and the Run's acquisition stays open and
        // unreleased for the §E6 item 4 cleanup to fail safe over (SPEC §E6
        // item 4: a Run may change its own resource only).
        [Test]
        public void SaveContents_AForeignSessionOfThePrefab_RefusesAndChangesNothing()
        {
            var (path, guid) = ProbePrefab("save-foreign.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            harness.SeedFoldedAcquisition(guid, path);
            var foreign = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(foreign, "a session of the target this Run never opened");
            var edit = new GameObject("TheirUnsavedEdit");
            edit.transform.SetParent(
                PrefabContentsFixture.Harness.EditingRoots(path)[0].transform,
                worldPositionStays: false);
            _scratch.Add(edit);
            var rootRef = new RefResolver().ToRef(foreign!, "unity.GameObjectRef");
            Assert.AreEqual(
                "prefab-contents:" + guid,
                rootRef.ToToken()["scene"]!.ToString(),
                "their session is reachable through the very key this Run's " +
                "acquisition would answer with");

            var context = harness.SaveContext(rootRef);
            var operation = Standard(SaveId);

            var preflight = operation.Preflight(context);
            Assert.That(
                preflight.Diagnostics.Any(d => d.Code == ErrorCodes.OW_UNITY_002),
                Is.True,
                "Preflight must refuse contents this Run never opened: " +
                string.Join("; ", preflight.Diagnostics.Select(
                    d => d.Code + " " + d.Message)));
            Assert.AreEqual(
                "root",
                preflight.Diagnostics.First(d => d.Code == ErrorCodes.OW_UNITY_002).Port);

            var executed = operation.Execute(context);

            Assert.AreEqual(StepResultKind.Failed, executed.Kind);
            Assert.IsNotNull(executed.Error);
            Assert.AreEqual(ErrorCodes.OW_UNITY_002, executed.Error!.Code);
            Assert.AreEqual("root", executed.Error!.Port);
            Assert.That(harness.Records, Is.Empty,
                "the refusal journals no release of the acquisition it does hold");
            Assert.IsTrue(
                ((IRunResourceRegistry)context).IsHeldByRun(
                    PrefabContentsResources.Kind,
                    PrefabContentsResources.Data(guid, path)),
                "the Run's own acquisition stays open, unreleased and unbound");
            Assert.That(harness.Changes.Records, Is.Empty,
                "a refused save reports no change");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset);
            Assert.That(asset!.transform.childCount, Is.EqualTo(1),
                "their unsaved edit is never written into the asset");
            var roots = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.That(roots, Has.Count.EqualTo(1), "the refusal unloads nothing");
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(foreign!),
                PrefabContentsOwnership.SessionIdOf(roots[0]),
                "their session is still open — the save never closed it");
        }

        // ── The §E6 item 4 release seam ─────────────────────────────────────

        // The handler unloads a session only when the binding of its own
        // acquisition names it. Which session of the Prefab the acquisition opened
        // is not readable off the (kind, data) pair, the journaled acquisition is
        // recorded before LoadPrefabContents runs, and nothing tells this
        // acquisition's contents from another tool's, another step's leftover or
        // the user's own session of the same asset — so sessions no binding ties
        // to the acquisition are somebody else's editing sessions: the release
        // reports that it cannot release safely and every one of them survives,
        // including the single one left after another closed, which is exactly the
        // session a release keyed on the asset path used to unload. An acquisition
        // this Editor session holds no root for still closes (a journaled
        // acquisition must be able to close after a restart).
        [Test]
        public void Release_WithoutABoundRootNeverUnloadsAnotherOwnersSession()
        {
            var (path, guid) = ProbePrefab("release.prefab", "Body");
            var first = PrefabUtility.LoadPrefabContents(path);
            var second = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            var data = PrefabContentsResources.Data(guid, path);
            var resource = new PrefabContentsFixture.Harness().OpenResource(guid, path);
            Assert.AreEqual(2, PrefabContentsFixture.Harness.EditingRoots(path).Count,
                "L-06: two LoadPrefabContents of one asset give two roots");

            var refused = PrefabContentsResources.Release(
                PrefabContentsFixture.RunId, resource);

            Assert.IsFalse(refused.Ok,
                "no binding proves either session belongs to this acquisition");
            Assert.AreEqual(ErrorCodes.OW_API_004, ErrorCodeOf(refused));
            Assert.That(
                refused.Diagnostics[0].Message, Does.Contain("cannot be released safely"));
            Assert.AreEqual(
                2, PrefabContentsFixture.Harness.EditingRoots(path).Count,
                "a refused release unloads neither session");

            // One session closed by its own owner, one still open that this
            // acquisition never opened: the matching path is not ownership, and
            // unloading it would destroy unsaved work this Run never started.
            PrefabUtility.UnloadPrefabContents(second!);
            Assert.IsFalse(
                PrefabContentsResources.Release(
                    PrefabContentsFixture.RunId, resource).Ok,
                "the one session left is still not the acquisition's own");
            Assert.AreEqual(
                1, PrefabContentsFixture.Harness.EditingRoots(path).Count,
                "the session the acquisition never opened survives the release");

            PrefabUtility.UnloadPrefabContents(first!);
            Assert.IsTrue(
                PrefabContentsResources.Release(
                    PrefabContentsFixture.RunId, resource).Ok,
                "releasing contents this session no longer holds still closes");

            Assert.AreEqual(
                ErrorCodes.OW_API_004,
                ErrorCodeOf(PrefabContentsResources.Release(
                    PrefabContentsFixture.RunId,
                    new RunResource("editing_contents", data))));
            Assert.AreEqual(
                ErrorCodes.OW_API_004,
                ErrorCodeOf(PrefabContentsResources.Release(
                    PrefabContentsFixture.RunId,
                    new RunResource(PrefabContentsResources.Kind,
                        JsonValue.From(new JObject { ["path"] = path })))));
        }

        // The bound side of the same rule: the root the load bound to this
        // acquisition scope is the one the release unloads — its own, exactly —
        // while another asset's session is left alone, and a closed pair keeps no
        // claim that could outlive the object it named.
        [Test]
        public void Release_UnloadsExactlyTheRootItsAcquisitionBound()
        {
            var (path, guid) = ProbePrefab("release-owned.prefab", "Body");
            var (otherPath, _) = ProbePrefab("release-other.prefab", "Body");
            var foreign = PrefabUtility.LoadPrefabContents(otherPath);
            Assert.IsNotNull(foreign);
            var harness = new PrefabContentsFixture.Harness();
            var context = harness.OpenContext(guid, path);

            Assert.AreEqual(
                StepResultKind.Succeeded, Standard(OpenId).Execute(context).Kind);
            var own = PrefabContentsFixture.Harness.EditingRoots(path)[0];
            var bound = PrefabContentsOwnership.FindOwned(harness.Scope, guid, path);
            Assert.IsNotNull(bound);
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(own),
                PrefabContentsOwnership.SessionIdOf(bound!),
                "the load bound the root it opened to this acquisition");

            var released = PrefabContentsResources.Release(
                PrefabContentsFixture.RunId, harness.OpenResource(guid, path));

            Assert.IsTrue(released.Ok, Describe(released.Diagnostics));
            Assert.That(
                PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "the Run's own session is the one its release closed");
            Assert.IsNull(
                PrefabContentsOwnership.FindOwned(harness.Scope, guid, path),
                "a released acquisition stops claiming any root");
            var survivor = PrefabContentsFixture.Harness.EditingRoots(otherPath);
            Assert.AreEqual(1, survivor.Count, "another asset's session is untouched");
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(foreign!),
                PrefabContentsOwnership.SessionIdOf(survivor[0]),
                "and it is still the very session somebody else opened");
        }

        // ── Run level: §E6 test 2 / §G6 test 2 ───────────────────────────────

        // A Run that fails after open_contents still discards the contents it
        // never saved and releases the resource at Run end, with the pairing
        // record journaled and the asset untouched.
        [Test]
        public void RunFailingAfterOpenContents_ReleasesTheContentsAtRunEnd()
        {
            var (path, guid) = ProbePrefab("failing.prefab", "Body");
            var fixture = NewFixture();
            var dispatched = new List<RunResource>();
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind,
                (dispatchRunId, resource) =>
                {
                    dispatched.Add(resource);
                    return PrefabContentsResources.Release(dispatchRunId, resource);
                });
            var open = Standard(OpenId);
            var failing = new FailingOperation("test.prefabcontents.failing");
            fixture.Register(open, 'a');
            fixture.Register(failing, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "open", OpenId, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["prefab"] = new Constant(PrefabContentsFixture.PrefabRef(guid, path)),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["root"] = "unity.GameObjectRef",
                    },
                    next: 1,
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(1, "fail", failing.Descriptor.Id, 'c', next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "open", Operation = OpenId, Version = 1 },
                    new NodeData { Id = "fail", Operation = failing.Descriptor.Id, Version = 1 },
                },
                new[] { new ControlEdge("open", "fail") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "the failing step must end the Run Failed: " + FailedLines(_root, runId));
            Assert.That(dispatched, Has.Count.EqualTo(1),
                "terminal cleanup dispatches the still-open contents to the " +
                "release handler of its kind");
            Assert.AreEqual(PrefabContentsResources.Kind, dispatched[0].Kind);
            Assert.AreEqual(
                guid, PrefabContentsResources.GuidOf(dispatched[0].Data),
                "the release names the acquired contents");
            Assert.That(
                PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "the unsaved contents are discarded at Run end");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"resource_acquired\""),
                Is.True, "the acquisition was journaled");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"resource_released\"",
                    "\"kind\":\"" + PrefabContentsResources.Kind + "\""),
                Is.True, "the release was journaled with its own kind");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, ErrorCodes.OW_API_004),
                Is.False, "the release must not report an unregistered kind");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset);
            Assert.That(asset!.transform.childCount, Is.EqualTo(1),
                "a Run that never saved changes nothing in the asset");
        }

        // SPEC §E6 item 4 at Run end, seen from the acquisition side. Two
        // open_contents steps of one Prefab no longer hold two acquisitions and
        // two editing sessions: the second step is refused, because §G2 gives
        // both sessions the one prefab-contents:<guid> reference. So the Run ends
        // with exactly one acquisition owning exactly one editing session, and
        // that one session is what terminal cleanup must discard and pair: the
        // unsaved contents go, that acquisition closes with its own
        // resource_released record, nothing is reported as unreleasable, and the
        // Run that never saved leaves neither contents nor an open pair behind.
        // (The single-session Run end of §E6 test 2 is the test above; this one
        // adds the refused second step of the same Prefab to the same Plan the
        // two-session Run used to need.)
        [Test]
        public void RunEndingWithARefusedSecondOpenOfOnePrefab_ReleasesTheOneSession()
        {
            var (path, guid) = ProbePrefab("two-at-run-end.prefab", "Body");
            var fixture = NewFixture();
            var dispatched = new List<RunResource>();
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind,
                (dispatchRunId, resource) =>
                {
                    dispatched.Add(resource);
                    return PrefabContentsResources.Release(dispatchRunId, resource);
                });
            var open = Standard(OpenId);
            var failing = new FailingOperation("test.prefabcontents.failing");
            fixture.Register(open, 'a');
            fixture.Register(failing, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "openA", OpenId, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["prefab"] = new Constant(PrefabContentsFixture.PrefabRef(guid, path)),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["root"] = "unity.GameObjectRef",
                    },
                    next: 1,
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(
                    1, "openB", OpenId, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["prefab"] = new Constant(PrefabContentsFixture.PrefabRef(guid, path)),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["root"] = "unity.GameObjectRef",
                    },
                    next: 2,
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(2, "fail", failing.Descriptor.Id, 'c', next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "openA", Operation = OpenId, Version = 1 },
                    new NodeData { Id = "openB", Operation = OpenId, Version = 1 },
                    new NodeData { Id = "fail", Operation = failing.Descriptor.Id, Version = 1 },
                },
                new[]
                {
                    new ControlEdge("openA", "openB"),
                    new ControlEdge("openB", "fail"),
                });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "the refused second open must end the Run Failed: " +
                FailedLines(_root, runId));
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(
                    _root, runId, "\"type\":\"step_succeeded\""),
                "only the first open step ran: " + FailedLines(_root, runId));
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_failed\"", ErrorCodes.OW_UNITY_002),
                Is.True,
                "the second open of one Prefab fails with the ambiguous-reference " +
                "code: " + FailedLines(_root, runId));
            Assert.That(
                dispatched, Has.Count.EqualTo(1),
                "the one acquisition the Run holds is dispatched to the release " +
                "handler of its kind");
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(
                    _root, runId, "\"type\":\"resource_acquired\""),
                "the refused step journaled no acquisition of its own");
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(
                    _root, runId, "\"type\":\"resource_released\""),
                "the one acquisition closed on the session its own load bound");
            Assert.That(
                PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "the Run's own session is discarded, and the Run that never " +
                "saved leaves no contents behind");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, ErrorCodes.OW_API_004),
                Is.False, "an acquisition whose own root is provable is never " +
                          "reported as unreleaseable");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset);
            Assert.That(asset!.transform.childCount, Is.EqualTo(1),
                "a Run that never saved changes nothing in the asset");
        }

        // The window the interleaved Run-end cleanup still leaves, crossed by
        // recovery: the Run's one acquisition of one Prefab was physically
        // released while the process died before its resource_released record
        // reached the store. The journal still shows that acquisition open while
        // its own session is already unloaded, and a session of the same Prefab
        // that somebody else opened is still live — which is also exactly what an
        // acquisition whose contents never loaded looks like, so nothing may be
        // unloaded on the strength of the journaled asset path. Recovery reads
        // the durable release evidence the acquisition's own unload left: it
        // names the very object this scope unloaded, so the absent binding has its
        // reason, and recovery closes that acquisition's pairing without touching
        // a single object. The fold's record pairs with it, the Run reaches the
        // end its journaled step failure decided, and the session no scope of
        // this Run ever bound stays open and untouched (SPEC §E6 item 4, §F2,
        // §F4). The Run's own root is captured inside the release handler: that
        // handler runs after the open step's step_succeeded record, which is what
        // proves the load ran and bound a root — reading the binding right after
        // AcceptRun would find a Run that never ticked and nothing bound.
        [Test]
        public void RecoveryAfterAnInterruptedRelease_ClosesTheAcquisitionAndKeepsTheForeignSession()
        {
            var (path, guid) = ProbePrefab("interrupted-older.prefab", "Body");
            var foreign = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(
                foreign, "a session of the target this Run never opened must stay open");
            var foreignRootId = PrefabContentsOwnership.SessionIdOf(foreign!);
            string runId;
            var ownRootId = 0L;
            var dead = new RunnerHostFixture(_root);
            try
            {
                dead.ReleaseDispatcher.Register(
                    PrefabContentsResources.Kind,
                    (dispatchRunId, resource) =>
                    {
                        if (resource.AcquiringStep == 0)
                        {
                            var own = PrefabContentsOwnership.FindOwned(
                                RunResourceScope.Of(dispatchRunId, 0), guid, path);
                            Assert.IsNotNull(
                                own,
                                "the release of the Run's acquisition must find the " +
                                "root its own load bound to its own scope");
                            ownRootId = PrefabContentsOwnership.SessionIdOf(own!);
                        }
                        return PrefabContentsResources.Release(dispatchRunId, resource);
                    });
                dead.Host.Store.JournalStreamWrapper =
                    file => new InterruptAtReleaseRecordStream(file);
                runId = AcceptOpenThenFailRun(dead, guid, path);

                DriveUntilInterrupted(dead.Host, runId);

                // The interrupted state: one physical release with no record,
                // and no terminal record either.
                Assert.AreEqual(
                    1, RunnerHostFixture.JournalCount(
                        _root, runId, "\"type\":\"resource_acquired\""),
                    "the Run holds exactly one acquisition of the target");
                Assert.AreEqual(
                    0, RunnerHostFixture.JournalCount(
                        _root, runId, "\"type\":\"resource_released\""),
                    "the interrupted release wrote no record");
                Assert.That(
                    RunnerHostFixture.JournalHas(
                        _root, runId, "\"type\":\"run_finished\""),
                    Is.False,
                    "the interruption stopped the finish before its terminal record");
                Assert.IsNull(
                    PrefabContentsOwnership.FindOwned(
                        RunResourceScope.Of(runId, 0), guid, path),
                    "the released acquisition no longer claims a root");
                Assert.That(
                    PrefabContentsOwnership.UnloadedOf(
                        RunResourceScope.Of(runId, 0), guid),
                    Is.EqualTo(ownRootId),
                    "its own unload wrote the durable evidence before the binding " +
                    "went, so the absent binding has its reason");
                var survivors = PrefabContentsFixture.Harness.EditingRoots(path);
                Assert.That(survivors, Has.Count.EqualTo(1),
                    "only the session this Run never opened is left open");
                Assert.AreEqual(
                    foreignRootId, PrefabContentsOwnership.SessionIdOf(survivors[0]),
                    "and it is the very session somebody else opened");
            }
            finally
            {
                // The dead session's §E2 exclusion goes with it, which is what
                // lets the recovery session adopt the Run.
                dead.Dispose();
            }

            var recovery = NewFixture();
            RegisterOpenThenFailOperations(recovery);
            recovery.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind, PrefabContentsResources.Release);

            var recovered = recovery.Host.RecoverRuns();

            Assert.That(recovered.Ok, Is.True, Describe(recovered.Diagnostics));
            Assert.AreEqual(
                RunState.Failed, recovery.Host.Coordinator.StateOf(runId),
                "the journaled step failure decides the end once the acquisition " +
                "is accounted for: " + FailedLines(_root, runId));
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(
                    _root, runId, "\"type\":\"resource_released\""),
                "recovery wrote that acquisition's own pairing record");
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(
                    _root, runId, "\"type\":\"run_finished\""),
                "the interrupted finish completed with its single terminal record");
            var fold = recovery.Host.Coordinator.Get(runId);
            Assert.IsNotNull(fold, "the Run journal must reconstruct");
            Assert.That(
                fold!.OpenResources, Is.Empty,
                "the durable pairing closed on the proof of its own unload");
            var left = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.That(left, Has.Count.EqualTo(1),
                "recovery unloaded nothing — the acquisition it closed owns no " +
                "root any more");
            Assert.AreEqual(
                foreignRootId, PrefabContentsOwnership.SessionIdOf(left[0]),
                "the session no scope of this Run opened survives the recovery");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, ErrorCodes.OW_API_004),
                Is.False,
                "a release whose own unload the durable evidence proves is never " +
                "reported as unattributable");
        }

        // The same interrupted state with its proof missing: the Run's own session
        // is gone while nothing durable says this scope unloaded it — which is
        // also exactly what an acquisition whose contents never loaded reports. A
        // session of the target that somebody else opened is still live, and
        // nothing may be unloaded on the strength of the journaled asset path, so
        // the release refuses and leaves that session exactly as it found it. And
        // because the acquisition is one the §F2 fold rebuilt rather than an
        // object this session ever held, the Run cannot end with a durable pairing
        // it can no longer close: the §F4 finish ends NeedsReview, never a guessed
        // release and never a terminal record over an unaccounted resource
        // (SPEC §F0, §E6 item 4, §F4).
        [Test]
        public void RecoveryOfAnUnprovableInterruptedRelease_NeedsReviewAndKeepsTheLiveSession()
        {
            var (path, guid) = ProbePrefab("interrupted-unprovable.prefab", "Body");
            var foreign = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(
                foreign, "a session of the target this Run never opened must stay open");
            var foreignRootId = PrefabContentsOwnership.SessionIdOf(foreign!);
            string runId;
            var dead = new RunnerHostFixture(_root);
            try
            {
                dead.ReleaseDispatcher.Register(
                    PrefabContentsResources.Kind,
                    (dispatchRunId, resource) =>
                    {
                        if (resource.AcquiringStep == 0)
                        {
                            // The acquisition's own root destroyed exactly as an
                            // unload destroys it, with no release evidence
                            // recorded and its binding left dangling: the state a
                            // lost proof leaves behind. The root it owns is read
                            // here because this handler runs once the open step
                            // has run and bound it — before the Run ticked there
                            // would be nothing bound to lose.
                            var own = PrefabContentsOwnership.FindOwned(
                                RunResourceScope.Of(dispatchRunId, 0), guid, path);
                            Assert.IsNotNull(
                                own,
                                "the Run's acquisition must own a live root of its " +
                                "own before the proof of its unload goes missing");
                            PrefabUtility.UnloadPrefabContents(own!);
                            return Result<Unit>.Success(Unit.Value);
                        }
                        return PrefabContentsResources.Release(dispatchRunId, resource);
                    });
                dead.Host.Store.JournalStreamWrapper =
                    file => new InterruptAtReleaseRecordStream(file);
                runId = AcceptOpenThenFailRun(dead, guid, path);

                DriveUntilInterrupted(dead.Host, runId);

                Assert.AreEqual(
                    0, RunnerHostFixture.JournalCount(
                        _root, runId, "\"type\":\"resource_released\""),
                    "the interrupted release wrote no record");
                Assert.That(
                    RunnerHostFixture.JournalHas(
                        _root, runId, "\"type\":\"run_finished\""),
                    Is.False,
                    "the interruption stopped the finish before its terminal record");
                Assert.That(
                    PrefabContentsOwnership.UnloadedOf(
                        RunResourceScope.Of(runId, 0), guid),
                    Is.Null,
                    "nothing durable proves this scope unloaded its own root");
            }
            finally
            {
                dead.Dispose();
            }

            var recovery = NewFixture();
            RegisterOpenThenFailOperations(recovery);
            recovery.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind, PrefabContentsResources.Release);

            var recovered = recovery.Host.RecoverRuns();

            Assert.That(recovered.Ok, Is.True, Describe(recovered.Diagnostics));
            Assert.AreEqual(
                RunState.NeedsReview, recovery.Host.Coordinator.StateOf(runId),
                "an acquisition this session can neither release nor prove is " +
                "released ends the recovery in review, never an automatic end");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_needs_review\"",
                    "\"" + ErrorCodes.OW_API_004 + "\""),
                Is.True, "the review record names the release that cannot be proven");
            Assert.AreEqual(
                0, RunnerHostFixture.JournalCount(
                    _root, runId, "\"type\":\"resource_released\""),
                "no pairing record was written for a release nobody can prove");
            Assert.That(
                RunnerHostFixture.JournalHas(
                    _root, runId, "\"type\":\"run_finished\""),
                Is.False, "no terminal record asserts an outcome over an open resource");
            var survivors = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.That(survivors, Has.Count.EqualTo(1),
                "the refusal unloads nothing: the one session still open is not " +
                "the root bound to the acquisition's own scope");
            Assert.AreEqual(
                foreignRootId, PrefabContentsOwnership.SessionIdOf(survivors[0]),
                "what survives is exactly the session somebody else opened");
            Assert.IsNull(
                PrefabContentsOwnership.FindOwned(
                    RunResourceScope.Of(runId, 0), guid, path),
                "the acquisition proves no root of its own: the object its binding " +
                "named is destroyed");
            var fold = recovery.Host.Coordinator.Get(runId);
            Assert.IsNotNull(fold, "the Run journal must reconstruct");
            Assert.AreEqual(
                1, fold!.OpenResources.Count,
                "the acquisition stays outstanding for the review that decides it");
        }

        // The defect this seam's ownership rule exists to prevent, at Run end:
        // the acquisition is journaled write-ahead BEFORE LoadPrefabContents runs,
        // so an interruption between the two leaves an open acquisition that owns
        // no contents. If a session of the same Prefab is open when the Run ends —
        // another tool's LoadPrefabContents, another step's leftover, the user's
        // own code — its path matches the journaled asset, but it is not the Run's
        // resource: terminal cleanup leaves it open with its unsaved edits,
        // journals no resource_released and reports the refusal rather than
        // discarding work this Run never opened (SPEC §E6 item 4).
        [Test]
        public void RunEndingWithAnAcquisitionThatNeverLoaded_LeavesTheForeignSessionOpen()
        {
            var (path, guid) = ProbePrefab("never-loaded.prefab", "Body");
            var foreign = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(foreign, "the unrelated session of the target must open");

            var fixture = NewFixture();
            var dispatched = new List<RunResource>();
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind,
                (dispatchRunId, resource) =>
                {
                    dispatched.Add(resource);
                    return PrefabContentsResources.Release(dispatchRunId, resource);
                });
            var acquireOnly = new AcquireOnlyOperation(
                "test.prefabcontents.acquireonly", guid, path);
            fixture.Register(acquireOnly, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "acquire", acquireOnly.Descriptor.Id, 'a', next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "acquire", Operation = acquireOnly.Descriptor.Id, Version = 1 },
                },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "the step that stops after the acquisition ends the Run Failed: " +
                FailedLines(_root, runId));
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"resource_acquired\""),
                Is.True, "the acquisition was journaled before anything opened");
            Assert.That(dispatched, Has.Count.EqualTo(1),
                "the outstanding acquisition is dispatched to the release handler");
            var roots = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.AreEqual(
                1, roots.Count,
                "the session this Run never opened is not unloaded by its cleanup");
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(foreign!),
                PrefabContentsOwnership.SessionIdOf(roots[0]),
                "what survives is exactly the unrelated session");
            Assert.AreEqual(
                0, RunnerHostFixture.JournalCount(
                    _root, runId, "\"type\":\"resource_released\""),
                "the acquisition stays open in the fold: no release happened");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, ErrorCodes.OW_API_004),
                Is.True, "the refusal is reported, never a silent skip");
        }

        // The same state seen after an Editor restart: the restart took the Run's
        // own preview scene and the session state that named it with it, and the
        // session of the target that is open now belongs to whoever opened it
        // afterwards. Recovery cleanup must not unload it on the strength of the
        // journaled path, and the step's §F5 verdict of the same state stays
        // Unknown — never a Completed built on contents it cannot prove it opened.
        [Test]
        public void RecoveryAfterARestartOverAForeignSession_KeepsItAndStaysUnknown()
        {
            var (path, guid) = ProbePrefab("restart-foreign.prefab", "Body");
            var harness = new PrefabContentsFixture.Harness();
            var context = harness.OpenContext(guid, path);
            var open = Standard(OpenId);
            var preflight = open.Preflight(context);
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(
                    d => d.Code + " " + d.Message)));
            Assert.AreEqual(StepResultKind.Succeeded, open.Execute(context).Kind);
            PrefabUtility.UnloadPrefabContents(
                PrefabContentsFixture.Harness.EditingRoots(path)[0]);

            // The restart: the Editor session that held the Run's binding is gone,
            // and a session of the target opened since then is somebody else's.
            PrefabContentsOwnership.Unbind(harness.Scope, guid);
            var foreign = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(foreign);

            var refused = PrefabContentsResources.Release(
                PrefabContentsFixture.RunId, harness.OpenResource(guid, path));

            Assert.IsFalse(refused.Ok,
                "the journaled path does not make a foreign session releasable");
            Assert.AreEqual(ErrorCodes.OW_API_004, ErrorCodeOf(refused));
            var roots = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.AreEqual(1, roots.Count, "recovery cleanup unloads nothing");
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(foreign!),
                PrefabContentsOwnership.SessionIdOf(roots[0]),
                "the session opened after the restart is untouched");

            var recovery = new PrefabContentsFixture.Harness();
            recovery.SeedFoldedAcquisition(guid, path);
            var verdict = open.Reconcile(
                recovery.OpenContext(guid, path), preflight.Snapshot);
            Assert.That(
                verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "the verdict was " + verdict.GetType().Name +
                (verdict is ReconcileResult.Unknown why ? ": " + why.Reason : ""));
            Assert.That(
                ((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain("cannot be attributed"),
                "the reason must name the contents this step cannot point at");
            Assert.AreEqual(
                1, PrefabContentsFixture.Harness.EditingRoots(path).Count,
                "an Unknown verdict neither adopts nor unloads the foreign session");
        }

        // SPEC §E6 item 4 / §G6 item 5 seen against a neighbour: a Run that opens
        // the contents and then fails still discards what it opened — the binding
        // proves which session of the target that was — and leaves an unrelated
        // session of the very same Prefab open with its unsaved edits. The two
        // sessions are no longer one indistinguishable pair to release.
        [Test]
        public void RunFailingAfterOpenContents_UnloadsItsOwnSessionNotTheNeighbours()
        {
            var (path, guid) = ProbePrefab("neighbour.prefab", "Body");
            var neighbour = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(neighbour, "the unrelated session of the target must open");

            var fixture = NewFixture();
            var dispatched = new List<RunResource>();
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind,
                (dispatchRunId, resource) =>
                {
                    dispatched.Add(resource);
                    return PrefabContentsResources.Release(dispatchRunId, resource);
                });
            var open = Standard(OpenId);
            var failing = new FailingOperation("test.prefabcontents.neighbour");
            fixture.Register(open, 'a');
            fixture.Register(failing, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "open", OpenId, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["prefab"] = new Constant(PrefabContentsFixture.PrefabRef(guid, path)),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["root"] = "unity.GameObjectRef",
                    },
                    next: 1,
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(1, "fail", failing.Descriptor.Id, 'c', next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "open", Operation = OpenId, Version = 1 },
                    new NodeData { Id = "fail", Operation = failing.Descriptor.Id, Version = 1 },
                },
                new[] { new ControlEdge("open", "fail") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "the failing step must end the Run Failed: " + FailedLines(_root, runId));
            Assert.That(dispatched, Has.Count.EqualTo(1));
            var roots = PrefabContentsFixture.Harness.EditingRoots(path);
            Assert.AreEqual(
                1, roots.Count,
                "the Run's own session closed and no other was taken for it");
            Assert.AreEqual(
                PrefabContentsOwnership.SessionIdOf(neighbour!),
                PrefabContentsOwnership.SessionIdOf(roots[0]),
                "the unrelated session of the same Prefab survives its Run");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"resource_released\"",
                    "\"kind\":\"" + PrefabContentsResources.Kind + "\""),
                Is.True, "the Run's own acquisition closed on its own contents");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, ErrorCodes.OW_API_004),
                Is.False, "a neighbour session is no reason to refuse the release");
        }

        // SPEC §G6 / §E6 item 4: a Prefab Mode stage of the same asset is the
        // user's own editing session and never a Run resource. On 6000.0 the
        // stage scene carries no path (stage.scene.path is empty), so the
        // scene.path == assetPath filter in FindEditingRoots already excludes
        // it and that method's GetPrefabStage(GameObject) check is defence in
        // depth rather than the discriminator; assetPath is what names the
        // stage's asset. A Run that opens the contents and then fails must
        // discard only its own session, release its own acquisition, and leave
        // the stage open with its unsaved edits.
        [UnityTest]
        public IEnumerator RunFailingAfterOpenContents_LeavesTheUsersPrefabModeStageAlone()
        {
            var (path, guid) = ProbePrefab("prefab-mode.prefab", "Body");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset, "the probe asset must load");

            AssetDatabase.OpenAsset(asset!);
            for (var frame = 0;
                frame < 120 && PrefabStageUtility.GetCurrentPrefabStage() is null;
                frame++)
            {
                yield return null;
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage is null)
            {
                Assert.Ignore(
                    "This Editor did not open Prefab Mode from a script, so the " +
                    "stage of " + path + " cannot be observed here.");
            }
            Assert.AreEqual(
                path, stage!.assetPath,
                "the open stage is the user's own Prefab Mode session of the " +
                "target asset — assetPath names it, because on 6000.0 the stage " +
                "scene itself carries no path");
            Assert.That(
                PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "a Prefab Mode stage is never one of the Run's editing roots");

            var fixture = NewFixture();
            var dispatched = new List<RunResource>();
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind,
                (dispatchRunId, resource) =>
                {
                    dispatched.Add(resource);
                    return PrefabContentsResources.Release(dispatchRunId, resource);
                });
            var open = Standard(OpenId);
            var failing = new FailingOperation("test.prefabcontents.prefabmode");
            fixture.Register(open, 'a');
            fixture.Register(failing, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "open", OpenId, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["prefab"] = new Constant(PrefabContentsFixture.PrefabRef(guid, path)),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["root"] = "unity.GameObjectRef",
                    },
                    next: 1,
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(1, "fail", failing.Descriptor.Id, 'c', next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "open", Operation = OpenId, Version = 1 },
                    new NodeData { Id = "fail", Operation = failing.Descriptor.Id, Version = 1 },
                },
                new[] { new ControlEdge("open", "fail") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "the failing step must end the Run Failed: " + FailedLines(_root, runId));
            Assert.That(dispatched, Has.Count.EqualTo(1));
            Assert.That(
                PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "the Run's own contents are discarded at Run end");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"resource_released\"",
                    "\"kind\":\"" + PrefabContentsResources.Kind + "\""),
                Is.True, "the stage is not a second candidate that would keep " +
                         "the acquisition open");
            var survivor = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.IsNotNull(survivor, "the user's Prefab Mode session survives the Run");
            Assert.IsTrue(
                survivor!.scene.IsValid(),
                "the stage scene is still loaded — the Run never closed the session");
            Assert.AreEqual(
                path, survivor.assetPath,
                "the same stage of the target is the one that survived");
        }

        // The open→save hand-off through the production wiring: the editing
        // root reaches the save step across the data edge (the §G2
        // prefab-contents:<guid> lookup the runner host hands the RefResolver),
        // the save writes and unloads, and the resource closes inside the Run.
        [Test]
        public void OpenThenSaveRun_HandsTheRootAcrossAndClosesTheResource()
        {
            var (path, guid) = ProbePrefab("pair.prefab", "Body");
            var fixture = NewFixture();
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind, PrefabContentsResources.Release);
            var open = Standard(OpenId);
            var save = Standard(SaveId);
            fixture.Register(open, 'a');
            fixture.Register(save, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "open", OpenId, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["prefab"] = new Constant(PrefabContentsFixture.PrefabRef(guid, path)),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["root"] = "unity.GameObjectRef",
                    },
                    next: 1,
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(
                    1, "save", SaveId, 'c',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["root"] = new FromStep(0, "root"),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["prefab"] = "unity.PrefabAssetRef",
                    },
                    next: -1,
                    idempotency: Idempotency.Reconcilable),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "open", Operation = OpenId, Version = 1 },
                    new NodeData { Id = "save", Operation = SaveId, Version = 1 },
                },
                new[] { new ControlEdge("open", "save") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId),
                "the pair must run through: " + FailedLines(_root, runId));
            Assert.That(
                RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"step_succeeded\""),
                Is.EqualTo(2));
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_succeeded\"", "\"step\":1",
                    "\"kind\":\"prefabAsset\""),
                Is.True, "the save step journals its PrefabAssetRef output");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"resource_released\"",
                    "\"kind\":\"" + PrefabContentsResources.Kind + "\""),
                Is.True, "save_contents closes the acquisition it was handed");
            Assert.That(
                PrefabContentsFixture.Harness.EditingRoots(path), Is.Empty,
                "no contents survive the Run that saved them");
        }

        // ── Fixture plumbing ────────────────────────────────────────────────

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        // Accepts the single-acquisition Run the interrupted-release recovery
        // cases drive to an injected interruption: one open_contents step of one
        // Prefab, then a step that fails so the Run ends with that one
        // acquisition and that one editing session still open. Acceptance is kept
        // separate from the drive because the process these cases kill never
        // reaches a terminal state, and because a recovering session must register
        // the very same Operations with the very same Plan fingerprints (§F4 item
        // 3's Registry check runs against the resuming session's Registry).
        private string AcceptOpenThenFailRun(
            RunnerHostFixture fixture, string guid, string path)
        {
            RegisterOpenThenFailOperations(fixture);
            var failing = new FailingOperation("test.prefabcontents.pair");
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "open", OpenId, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["prefab"] = new Constant(PrefabContentsFixture.PrefabRef(guid, path)),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["root"] = "unity.GameObjectRef",
                    },
                    next: 1,
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(1, "fail", failing.Descriptor.Id, 'c', next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "open", Operation = OpenId, Version = 1 },
                    new NodeData { Id = "fail", Operation = failing.Descriptor.Id, Version = 1 },
                },
                new[] { new ControlEdge("open", "fail") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        // The Operations of that Plan under the fingerprints it carries — the same
        // registrations every session touching its journal must present, whether
        // it runs the Run or recovers it.
        private static void RegisterOpenThenFailOperations(RunnerHostFixture fixture)
        {
            fixture.Register(Standard(OpenId), 'a');
            fixture.Register(new FailingOperation("test.prefabcontents.pair"), 'c');
        }

        // Drives the Run tick by tick, as the Editor update subscription does.
        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var tickIndex = 0; tickIndex < maxTicks; tickIndex++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    return;
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        private static string FailedLines(string root, string runId) =>
            string.Join(" | ", RunnerHostFixture.JournalLines(root, runId)
                .Where(line => line.Contains("step_failed")).Take(3));

        // Drives the session the two interrupted-release tests kill: ticks as the
        // Editor update subscription does until the write of a
        // resource_released record throws, which is the process dying after the
        // acquisition's own unload succeeded and before its record reached the
        // store. The exception is deliberately not an IO failure — the store
        // handles one as a §F1 refusal and ends the Run, which is a different
        // state than the one recovery has to read — so it leaves the journal
        // exactly as an interruption does and the session is abandoned here.
        private void DriveUntilInterrupted(OpWeaveRunnerHost host, string runId)
        {
            for (var tickIndex = 0; tickIndex < 50; tickIndex++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    Assert.Fail(
                        "The Run reached " + state + " without the injected " +
                        "interruption, so its journal is not the interrupted " +
                        "state this test recovers from.");
                }
                try
                {
                    var tick = host.Tick();
                    Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
            Assert.Fail("The injected interruption never reached the journal write.");
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

        private static string ErrorCodeOf(Result<Unit> result)
        {
            Assert.IsFalse(result.Ok, "the release unexpectedly succeeded");
            return result.Diagnostics.Count == 0
                ? "<no diagnostic>"
                : result.Diagnostics[0].Code;
        }

        /// <summary>The step after open_contents that fails (SPEC §E6 test 2:
        /// the contents a failed Run never saved are still released).</summary>
        private sealed class FailingOperation : IOperation
        {
            internal FailingOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Fails its step on purpose.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            // OW-RUN-006 is the code the §E7 mapping gives an Operation-raised
            // failure; the fixture only needs the step to fail.
            public StepResult Execute(StepContext ctx) =>
                StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006, "The fixture step always fails."));

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>The write-ahead half of open_contents seen on its own: it
        /// journals the <c>prefab_edit</c> acquisition of (guid, path) and then
        /// fails without loading anything — the state an interruption between the
        /// acquisition and <c>LoadPrefabContents</c> leaves, an open acquisition
        /// that owns no contents object (SPEC §E6 item 4).</summary>
        private sealed class AcquireOnlyOperation : IOperation
        {
            private readonly string _guid;
            private readonly string _path;

            internal AcquireOnlyOperation(string id, string guid, string path)
            {
                _guid = guid;
                _path = path;
                Descriptor = Op.Define(id, 1)
                    .Summary("Journals a prefab_edit acquisition and opens nothing.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));

            public StepResult Execute(StepContext ctx)
            {
                ((IRunResourceRegistry)ctx).Acquire(
                    PrefabContentsResources.Kind,
                    PrefabContentsResources.Data(_guid, _path));
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    "The fixture stops after the acquisition it journaled."));
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>Journal-stream seam of the two interrupted-release tests:
        /// forwards every write until the bytes carry a <c>resource_released</c>
        /// record, then throws instead of writing them — the process dying after
        /// the acquisition's own unload succeeded and before its pairing record
        /// reached the store, which is the window §E6 item 4 cannot close. It
        /// throws a plain <see cref="InvalidOperationException"/> on purpose: the
        /// store treats an IO failure as a §F1 refusal it must recover from, and
        /// only an unexpected exception leaves the journal in the state an
        /// interruption leaves it (the record absent, no terminal record).</summary>
        private sealed class InterruptAtReleaseRecordStream : FileStream
        {
            private const string ReleaseRecordMarker = "\"type\":\"resource_released\"";
            private readonly FileStream _inner;

            internal InterruptAtReleaseRecordStream(FileStream inner)
                : base(inner.SafeFileHandle, FileAccess.Write)
            {
                _inner = inner;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Encoding.ASCII.GetString(buffer, offset, count)
                    .Contains(ReleaseRecordMarker, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The test interrupted the process as it wrote the " +
                        "resource_released record.");
                }
                _inner.Write(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
            }
        }
    }
}
