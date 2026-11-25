#nullable enable

// T-W2.10-01 [Edit] tests: local:T-W2.10-01:test-harness for the SPEC §M.6
// item 1 OperationTestHarness — Run returning the same outputs, changes and
// diagnostics as a normal Run (checked against a real RunnerHost Run of the
// same Operation), RunInterrupted stopping at BeforeExecute/AfterExecute with
// the journaled state the Runner's fold would read there, and Dispose leaving
// no temporary scene, folder or asset behind.
//
// T-W2.7-06 [Edit] regression: local:T-W2.7-06:create-folder-deleted-guid —
// Issue #232, a folder at a deleted asset's former path must create while a
// live asset still refuses OW-UNITY-005.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
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
    /// The SPEC §M.6 item 1 harness fixtures: a standard Operation's harness
    /// run compared against a real Run, both interrupt points, and the
    /// no-residue cleanup guarantee.
    /// </summary>
    internal sealed class OperationTestHarnessTests
    {
        // The standard Operation of SPEC §I.3 used for the side-effect
        // cases: it writes a folder, which the filesystem reports directly.
        private const string CreateFolderId = "unity.asset.create_folder";

        // The fixture Operation the real-Run comparison drives. Its nodeId in
        // the one-step Plan is "harness" — the same synthetic nodeId the
        // harness stamps — so the §F7 change records compare member for
        // member, not just in shape.
        private const string ProbeId = "test.harness.probe";
        private const string ProbeNode = "harness";

        // The standard Operation the standard-origin parity case drives:
        // defined in OpWeave.Operations, input- and output-free, so its step
        // records compare member for member with no path to substitute.
        private const string StartId = "core.start";
        private const string WorkflowId = "wf.harness-probe";
        private const string ChangeTargetPath = "Assets/OpWeaveHarnessProbe/changed";

        private string _root = string.Empty;
        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();
        private readonly List<OperationTestHarness> _harnesses =
            new List<OperationTestHarness>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveHarnessTests",
                "t" + Guid.NewGuid().ToString("N"));
            ProbeOperation.ExecuteCalls = 0;
            ProbeOperation.PreflightCalls = 0;
            // Every test starts from one clean untitled scene — the state the
            // batch-mode test runner itself opens in — so a scene another
            // test left dirty or closed cannot cascade into this one: the
            // harness refuses to run beside unsaved changes, because those
            // changes are the caller's to keep or discard.
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
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
            // Belt and braces for a crashed harness: the root folder is
            // harness-owned, so a leftover is always safe to drop.
            if (AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"))
            {
                AssetDatabase.DeleteAsset("Assets/OpWeaveTesting");
            }
            AssetDatabase.Refresh();
            // The harnesses above already restore one loaded scene; ending
            // on a clean empty scene also covers a test that failed before
            // its harness reached Dispose, so the next test's SetUp finds
            // the same starting state this one had.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        // ── local:T-W2.10-01:test-harness — Run equals a normal Run ──────────

        [Test]
        public void RunReturnsTheSameOutputsChangesAndDiagnosticsAsANormalRun()
        {
            // The real Run: the same Operation behind a one-step Plan whose
            // nodeId is the harness's synthetic one, driven by the production
            // run loop over a real FileRunStore.
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            fixture.Register(new ProbeOperation(), 'a');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, ProbeNode, ProbeId, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["text"] = new Constant(RunnerHostFixture.Json("\"hello\"")),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["echo"] = "core.String",
                    },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = ProbeNode, Operation = ProbeId, Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed");
            var runId = accepted.Value!.RunId;
            for (var i = 0; i < 50; i++)
            {
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    break;
                }
                Assert.IsTrue(fixture.Host.Tick().Ok, "Tick failed");
            }
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            using var harness = Track(OperationTestHarness.Create());
            var result = harness.Run(ProbeId, new { text = "hello" });

            Assert.That(result.Ok, Is.True, () => result.ToString());
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.AreEqual(
                "\"echo:hello\"", result.Outputs["echo"].CanonicalText);

            // The journal's step records match the Run's step records —
            // type and data member for member — including the §F6 log
            // record the Operation wrote between them.
            var stepRecords = StepRecordsOf(RunnerHostFixture.JournalLines(_root, runId));
            var harnessStepRecords = StepRecordsOf(result.Journal);
            Assert.That(
                harnessStepRecords.Select(pair => pair.Item1),
                Is.EqualTo(stepRecords.Select(pair => pair.Item1)),
                () => "harness journal: " + result);
            for (var i = 0; i < stepRecords.Count; i++)
            {
                Assert.IsTrue(
                    JToken.DeepEquals(
                        stepRecords[i].Item2, harnessStepRecords[i].Item2),
                    $"record {i} ({stepRecords[i].Item1}) differs:\n" +
                    $"run     : {stepRecords[i].Item2}\n" +
                    $"harness : {harnessStepRecords[i].Item2}");
            }
        }

        // ── SPEC §F6 provenance through the harness (Codex F1 regression) ──

        // The harness feeds the Operation's provenance into the same shared
        // UnityStepContext the Runner builds, so a custom Operation's
        // ctx.Log entry carries the §F6 data.operationId/data.origin members
        // through the harness exactly as through a Run — the probe's test
        // assembly is not a standard Operations' assembly, so its harness
        // origin is Custom, matching the fixture's registration.
        [Test]
        public void CustomOperationLogEntryCarriesProvenanceThroughTheHarness()
        {
            using var harness = Track(OperationTestHarness.Create());

            var result = harness.Run(ProbeId, new { text = "hello" });

            Assert.That(result.Ok, Is.True, () => result.ToString());
            var entries = new List<JournalRecord>();
            foreach (var record in result.Journal)
            {
                if (record.Type == JournalRecordType.Log)
                {
                    entries.Add(record);
                }
            }
            Assert.AreEqual(1, entries.Count, "the probe writes one ctx.Log entry");
            var data = entries[0].Data.ToToken()["data"] as JObject;
            Assert.IsNotNull(data, "the §F6 stamp lives in the entry's data member");
            Assert.AreEqual(ProbeId, data!["operationId"]?.Value<string>());
            Assert.AreEqual("custom", data["origin"]?.Value<string>());
        }

        // The standard half of the same §M.6 parity contract: a standard
        // Operation run through the harness produces the step records a
        // normal Run produces — provenance-free, exactly as a Standard
        // registration's §F6 entries carry no operationId/origin.
        [Test]
        public void StandardOperationRunThroughTheHarnessMatchesANormalRun()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            var start = new StartOperation();
            // The fixture's Register always registers Custom; a Standard
            // registration is staged on the snapshot directly.
            fixture.Operations.Add(new RegisteredOperation(
                start.Descriptor,
                OperationOrigin.Standard,
                "OpWeave.Operations",
                null,
                new string('a', 64),
                new string('b', 64))
            {
                Implementation = start,
            });
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, ProbeNode, StartId, 'a',
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = ProbeNode, Operation = StartId, Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed");
            var runId = accepted.Value!.RunId;
            for (var i = 0; i < 50; i++)
            {
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    break;
                }
                Assert.IsTrue(fixture.Host.Tick().Ok, "Tick failed");
            }
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            using var harness = Track(OperationTestHarness.Create());
            var result = harness.Run(StartId, new { });

            Assert.That(result.Ok, Is.True, () => result.ToString());
            Assert.That(result.Diagnostics, Is.Empty);

            var stepRecords = StepRecordsOf(RunnerHostFixture.JournalLines(_root, runId));
            var harnessStepRecords = StepRecordsOf(result.Journal);
            Assert.That(
                harnessStepRecords.Select(pair => pair.Item1),
                Is.EqualTo(stepRecords.Select(pair => pair.Item1)),
                () => "harness journal: " + result);
            for (var i = 0; i < stepRecords.Count; i++)
            {
                Assert.IsTrue(
                    JToken.DeepEquals(
                        stepRecords[i].Item2, harnessStepRecords[i].Item2),
                    $"record {i} ({stepRecords[i].Item1}) differs:\n" +
                    $"run     : {stepRecords[i].Item2}\n" +
                    $"harness : {harnessStepRecords[i].Item2}");
            }
        }

        // ── local:T-W2.10-01:test-harness — interrupt points ────────────────

        [Test]
        public void RunInterruptedBeforeExecuteLeavesOnlyTheWriteAheadRecord()
        {
            var harness = Track(OperationTestHarness.Create());
            var path = harness.TempFolder + "/NotCreated";

            var result = harness.RunInterrupted(
                CreateFolderId, new { path }, InterruptPoint.BeforeExecute);

            Assert.AreEqual(InterruptPoint.BeforeExecute, result.InterruptedAt);
            Assert.AreEqual(StepState.Running, result.StepState);
            Assert.IsNull(result.Outcome);
            Assert.That(result.Outputs, Is.Empty);
            Assert.That(result.Changes, Is.Empty);
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.AreEqual(1, result.Journal.Count);
            Assert.AreEqual(JournalRecordType.StepStarted, result.Journal[0].Type);
            Assert.AreEqual(0, result.Journal[0].Step);
            Assert.AreEqual(1, result.Journal[0].Attempt);
            var data = result.Journal[0].Data.ToToken();
            Assert.IsNotNull(data["snapshot"]);
            Assert.IsNotNull(data["inputsDigest"]);
            Assert.IsFalse(
                AssetDatabase.IsValidFolder(path),
                "BeforeExecute must stop before any side effect");
        }

        [Test]
        public void RunInterruptedAfterExecuteLeavesTheEffectWithoutAnOutcomeRecord()
        {
            var harness = Track(OperationTestHarness.Create());
            var path = harness.TempFolder + "/Created";

            var result = harness.RunInterrupted(
                CreateFolderId, new { path }, InterruptPoint.AfterExecute);

            Assert.AreEqual(InterruptPoint.AfterExecute, result.InterruptedAt);
            Assert.AreEqual(StepState.Running, result.StepState);
            Assert.AreEqual(StepResultKind.Succeeded, result.Outcome);
            Assert.IsTrue(
                AssetDatabase.IsValidFolder(path),
                "AfterExecute runs the effect before the interrupt");
            Assert.AreEqual("\"" + path + "\"", result.Outputs["path"].CanonicalText);
            Assert.AreEqual(1, result.Changes.Count);
            Assert.AreEqual(ChangeAction.Created, result.Changes[0].Action);
            Assert.AreEqual(1, result.Journal.Count);
            Assert.AreEqual(JournalRecordType.StepStarted, result.Journal[0].Type);

            // The write-ahead record is the same one a full Run writes.
            // Proving it needs the folder the interrupted Execute created
            // deleted first, so the full Run starts from the free path the
            // interrupted one saw; the occupancy check now asks only about
            // existing assets, so the session's remembered GUID no longer
            // reports the freed path as an OW-UNITY-005 collision (Issue
            // #232 — the same-path rerun this relies on used to need a
            // sibling path instead).
            Assert.That(AssetDatabase.DeleteAsset(path), Is.True,
                "the interrupted run's folder must go before the path is reused");
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.Not.Empty,
                "the session must still remember the deleted folder's GUID — " +
                "the Issue #232 state the same-path rerun exercises");
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Empty,
                "no existing asset holds the freed path");

            var full = harness.Run(CreateFolderId, new { path });
            Assert.That(full.Ok, Is.True, () => full.ToString());
            Assert.AreEqual(JournalRecordType.StepStarted, full.Journal[0].Type);

            // Same inputs, same synthetic step identity, so the records are
            // equal member for member — `snapshot` is the Preflight's
            // {"path": <normalized input>} and `inputsDigest` is the
            // CanonicalJson.Sha256 of the present inputs {"path": <as
            // written>}; nothing else in the record varies between the runs.
            Assert.IsTrue(
                JToken.DeepEquals(
                    result.Journal[0].Data.ToToken(),
                    full.Journal[0].Data.ToToken()),
                "the write-ahead record must equal a normal run's for the same inputs");
        }

        // ── local:T-W2.7-06:create-folder-deleted-guid (Issue #232) ─────────

        // Issue #232: create_folder's occupancy check asked the asset
        // database who holds the path, and the default AssetPathToGUID still
        // answers a deleted asset's remembered GUID for the rest of the
        // session — a false OW-UNITY-005 collision. OnlyExistingAssets frees
        // the deleted asset's path while a live asset still refuses.
        [Test]
        public void CreateFolderAtADeletedAssetsPathSucceedsWhileALiveAssetStillRefuses()
        {
            var harness = Track(OperationTestHarness.Create());
            var freed = harness.TempFolder + "/Freed";
            var taken = harness.TempFolder + "/taken";

            Assert.That(
                AssetDatabase.CreateFolder(harness.TempFolder, "Freed"),
                Is.Not.Empty,
                "the folder to delete must be created");
            Assert.That(AssetDatabase.DeleteAsset(freed), Is.True,
                "the folder must be deleted before its path is reused");
            Assert.That(AssetDatabase.IsValidFolder(freed), Is.False);
            Assert.That(AssetDatabase.AssetPathToGUID(freed), Is.Not.Empty,
                "the session must still remember the deleted folder's GUID — " +
                "that remembered answer is the Issue #232 state");
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    freed, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Empty,
                "no existing asset holds the freed path");

            var created = harness.Run(CreateFolderId, new { path = freed });

            Assert.That(created.Ok, Is.True, () => created.ToString());
            Assert.That(AssetDatabase.IsValidFolder(freed), Is.True);
            Assert.AreEqual(
                "\"" + freed + "\"", created.Outputs["path"].CanonicalText);

            // The unchanged half of the contract: a live asset at the path —
            // an extensionless file the Editor holds as a DefaultAsset at
            // exactly the folder path — still refuses with §J's OW-UNITY-005.
            File.WriteAllText(
                Path.Combine(
                    RunnerHostFixture.ProjectRoot,
                    taken.Replace('/', Path.DirectorySeparatorChar)),
                string.Empty);
            AssetDatabase.ImportAsset(taken, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.AssetPathToGUID(taken), Is.Not.Empty,
                "the live asset must occupy the path");

            var refused = harness.Run(CreateFolderId, new { path = taken });

            Assert.AreEqual(StepState.Failed, refused.StepState);
            Assert.AreEqual(ErrorCodes.OW_UNITY_005, refused.Diagnostics[0].Code);
            Assert.That(AssetDatabase.IsValidFolder(taken), Is.False,
                "a refused create must leave no folder behind");
        }

        // ── local:T-W2.10-01:test-harness — no residue after Dispose ─────────

        [Test]
        public void DisposeLeavesNoTemporarySceneFolderOrAsset()
        {
            var sceneBefore = SceneManager.GetActiveScene().path;
            var harness = Track(OperationTestHarness.Create());
            var folder = harness.TempFolder;
            var scenePath = harness.TempScenePath;

            // Force the temporary scene open (the folder must hold it) and run
            // a real effect inside the folder.
            var gameObject = HarnessObjects.NewGameObject(harness, "T");
            var run = harness.Run(CreateFolderId, new { path = folder + "/Sub" });
            Assert.That(run.Ok, Is.True, () => run.ToString());
            Assert.IsTrue(File.Exists(scenePath), "the temporary scene is an asset");
            Assert.AreEqual(scenePath, gameObject.scene.path);

            harness.Dispose();

            Assert.IsFalse(AssetDatabase.IsValidFolder(folder), "temp folder must be deleted");
            Assert.IsFalse(File.Exists(scenePath), "temp scene asset must be deleted");
            Assert.IsFalse(
                SceneManager.GetSceneByPath(scenePath).IsValid() &&
                SceneManager.GetSceneByPath(scenePath).isLoaded,
                "temp scene must be closed");
            Assert.IsTrue(gameObject == null, "harness objects must be destroyed");
            Assert.AreEqual(
                sceneBefore, SceneManager.GetActiveScene().path,
                "the caller's active scene must be restored");
        }

        // ── Failure and boundary coverage of the same step pipeline ──────────

        [Test]
        public void UnknownOperationFailsWithOwOp001AndStepFailed()
        {
            using var harness = Track(OperationTestHarness.Create());

            var result = harness.Run("test.harness.no-such-operation", new { });

            Assert.AreEqual(StepState.Failed, result.StepState);
            Assert.AreEqual(ErrorCodes.OW_OP_001, result.Diagnostics[0].Code);
            Assert.AreEqual(1, result.Journal.Count);
            Assert.AreEqual(JournalRecordType.StepFailed, result.Journal[0].Type);
            Assert.IsTrue(
                result.Journal[0].Data.ToToken()!["error"]!.ToString()
                    .Contains("OW-OP-001"));
        }

        [Test]
        public void MissingRequiredInputAndUnknownMemberFailBeforeExecute()
        {
            using var harness = Track(OperationTestHarness.Create());

            var missing = harness.Run(ProbeId, new { });
            Assert.AreEqual(StepState.Failed, missing.StepState);
            Assert.AreEqual(ErrorCodes.OW_RUN_007, missing.Diagnostics[0].Code);
            Assert.AreEqual(JournalRecordType.StepFailed, missing.Journal[0].Type);
            Assert.AreEqual(0, ProbeOperation.ExecuteCalls);

            var unknown = harness.Run(ProbeId, new { text = "x", bogus = 1 });
            Assert.AreEqual(StepState.Failed, unknown.StepState);
            Assert.AreEqual(ErrorCodes.OW_OP_005, unknown.Diagnostics[0].Code);
            Assert.AreEqual(JournalRecordType.StepFailed, unknown.Journal[0].Type);
        }

        [Test]
        public void HarnessObjectsCreatesInTheHarnessSceneAndDisposeDestroysIt()
        {
            var harness = Track(OperationTestHarness.Create());

            var gameObject = HarnessObjects.NewGameObject("Solo");

            Assert.That(gameObject, Is.Not.Null);
            Assert.AreEqual(harness.TempScenePath, gameObject!.scene.path);
            Assert.IsTrue(File.Exists(harness.TempScenePath));

            harness.Dispose();
            Assert.IsTrue(gameObject == null, "the harness owns its objects");
        }

        [Test]
        public void CreateAndDisposeTwiceInTheSameSessionLeavesNoResidue()
        {
            // The second harness must find the same clean state the first
            // one did, even though the first Dispose ran an untitled-scene
            // takeover backwards into a fresh empty scene.
            var first = Track(OperationTestHarness.Create());
            var firstObject = HarnessObjects.NewGameObject(first, "A");
            var firstFolder = first.TempFolder;
            var firstScene = first.TempScenePath;
            first.Dispose();
            Assert.IsFalse(AssetDatabase.IsValidFolder(firstFolder));
            Assert.IsFalse(File.Exists(firstScene));
            Assert.IsTrue(firstObject == null, "the first harness owns its objects");

            var second = Track(OperationTestHarness.Create());
            var secondObject = HarnessObjects.NewGameObject(second, "B");
            Assert.AreEqual(second.TempScenePath, secondObject.scene.path);
            second.Dispose();
            Assert.IsFalse(AssetDatabase.IsValidFolder(second.TempFolder));
            Assert.IsTrue(secondObject == null, "the second harness owns its objects");
            Assert.IsFalse(AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"));
        }

        [Test]
        public void OverlappingHarnessesRemoveTheHarnessCreatedRootInEitherDisposeOrder()
        {
            // The root is shared process state: the harness that created it
            // can be disposed while another's folder still fills it, so
            // whichever Dispose finds it empty removes it — creation order
            // and dispose order are independent.
            var first = Track(OperationTestHarness.Create());
            var second = Track(OperationTestHarness.Create());

            first.Dispose();
            Assert.IsTrue(
                AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"),
                "the second harness's folder still holds the root");
            second.Dispose();
            AssertNoRootResidue();

            var third = Track(OperationTestHarness.Create());
            var fourth = Track(OperationTestHarness.Create());

            fourth.Dispose();
            Assert.IsTrue(
                AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"),
                "the third harness's folder still holds the root");
            third.Dispose();
            AssertNoRootResidue();
        }

        [Test]
        public void DisposeNeverRemovesAPreExistingRoot()
        {
            // A root that was not created by a harness is never the
            // harness's to delete, and content the harness did not write is
            // never the harness's either.
            Assert.IsFalse(
                AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"),
                "this fixture needs the root not to exist yet");
            Assert.IsFalse(string.IsNullOrEmpty(
                AssetDatabase.CreateFolder("Assets", "OpWeaveTesting")));
            Assert.IsFalse(string.IsNullOrEmpty(
                AssetDatabase.CreateFolder("Assets/OpWeaveTesting", "Foreign")));

            var harness = Track(OperationTestHarness.Create());
            harness.Dispose();

            Assert.IsFalse(
                AssetDatabase.IsValidFolder(harness.TempFolder),
                "the harness's own folder must still be deleted");
            Assert.IsTrue(
                AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"),
                "a pre-existing root must survive");
            Assert.IsTrue(
                AssetDatabase.IsValidFolder("Assets/OpWeaveTesting/Foreign"),
                "foreign content must survive");
        }

        [Test]
        public void DisposeLeavesExactlyOneLoadedActiveScene()
        {
            // Dispose's end-state contract: the Editor is never left with no
            // loaded scene — the state several scene APIs refuse to run in.
            var harness = Track(OperationTestHarness.Create());
            HarnessObjects.NewGameObject(harness, "T");

            harness.Dispose();

            var loaded = 0;
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isLoaded)
                {
                    loaded++;
                }
            }
            Assert.AreEqual(1, loaded, "exactly one loaded scene must remain");
            Assert.IsTrue(
                SceneManager.GetActiveScene().isLoaded,
                "the remaining scene must be the active one");
        }

        // ── SPEC §M.6 public surface (Codex F3 regression) ──────────────────

        // SPEC §M.6 fixes the harness's public surface at Create, Run,
        // RunInterrupted and Dispose; the Poll pump RunToCompletion and its
        // RunContinuation mode stay OperationContract's internal machinery.
        // Codex finding F3: this reflection check fails if either becomes
        // visible outside the OpWeave.Testing assembly again — or silently
        // disappears under the name the check looks for.
        [Test]
        public void RunToCompletionAndRunContinuationStayInsideTheTestingAssembly()
        {
            var harness = typeof(OperationTestHarness).Assembly
                .GetType("OpWeave.Testing.OperationTestHarness");
            Assert.IsNotNull(
                harness,
                "OperationTestHarness must still be found in OpWeave.Testing");

            var pumps = harness!.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "RunToCompletion")
                .ToList();
            Assert.That(
                pumps, Is.Not.Empty,
                "the RunToCompletion pump must still be found by name");
            foreach (var pump in pumps)
            {
                Assert.IsFalse(
                    pump.IsPublic || pump.IsFamily || pump.IsFamilyOrAssembly,
                    pump + " must not be visible outside OpWeave.Testing");
            }

            var continuation = harness.GetNestedType(
                "RunContinuation",
                BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(
                continuation,
                "the RunContinuation mode must still be found by name");
            Assert.IsFalse(
                continuation!.IsPublic || continuation.IsNestedPublic,
                "RunContinuation must not be visible outside OpWeave.Testing");
        }

        // ── Fixture Operation ────────────────────────────────────────────────

        private OperationTestHarness Track(OperationTestHarness harness)
        {
            _harnesses.Add(harness);
            return harness;
        }

        private static void AssertNoRootResidue()
        {
            Assert.IsFalse(
                AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"),
                "the harness-created root must be deleted");
            Assert.IsFalse(
                File.Exists(Path.Combine(
                    RunnerHostFixture.ProjectRoot, "Assets", "OpWeaveTesting.meta")),
                "the harness-created root's .meta must be deleted");
        }

        // The step records (in order) a journal carries — (type, data) pairs,
        // the members that must be identical between the Run's on-disk
        // journal and the harness's in-memory one. Timestamps, seq and
        // session are envelope members and necessarily differ.
        private static List<(string, JToken)> StepRecordsOf(IEnumerable<JournalRecord> records)
        {
            var step = new List<(string, JToken)>();
            foreach (var record in records)
            {
                if (IsRunLevelRecord(record.Type))
                {
                    continue;
                }
                step.Add((JournalRecordTypes.Of(record.Type).WireName, record.Data.ToToken()));
            }
            return step;
        }

        // The on-disk half, read through the production codec — SPEC §F2
        // makes line 1 the JournalHeader, the one journal line that is not a
        // record envelope, and every following line a record at seq = its
        // line index (the header holds no seq and records count from 1). The
        // codec owns the envelope, seq and crc checks: a line it cannot fully
        // decode — Corrupt, NeedsReview, or a Skipped unknown type — fails
        // the test instead of being skipped, because a step record that does
        // not read cleanly is a defect, not a line kind.
        private static List<(string, JToken)> StepRecordsOf(string[] journalLines)
        {
            var step = new List<(string, JToken)>();
            for (var i = 0; i < journalLines.Length; i++)
            {
                var line = journalLines[i];
                // ReadAllLines reports the file's final newline as a trailing
                // empty entry; a blank line is never a record.
                if (line.Length == 0)
                {
                    continue;
                }
                if (i == 0)
                {
                    Assert.AreEqual(
                        JournalLineStatus.Decoded,
                        JournalCodec.ReadHeader(line).Status,
                        "journal line 1 must be the JournalHeader: " + line);
                    continue;
                }
                var read = JournalCodec.ReadRecord(line, i);
                Assert.AreEqual(
                    JournalLineStatus.Decoded,
                    read.Status,
                    $"journal line {i + 1} must be a decodable record envelope: " +
                    (read.Diagnostic?.Message ?? line));
                if (IsRunLevelRecord(read.Record!.Type))
                {
                    continue;
                }
                step.Add((
                    JournalRecordTypes.Of(read.Record.Type).WireName,
                    read.Record.Data.ToToken()));
            }
            return step;
        }

        // The Run-level record types the harness journal never produces —
        // the one exclusion the journal comparison applies, in both reads.
        private static bool IsRunLevelRecord(JournalRecordType type) =>
            type == JournalRecordType.RunAccepted ||
            type == JournalRecordType.RunStarted ||
            type == JournalRecordType.RunSuspended ||
            type == JournalRecordType.RunResumed ||
            type == JournalRecordType.RunFinished;

        /// <summary>
        /// The fixture Operation the comparison drives: echoes its
        /// <c>text</c> input on <c>echo</c>, records one §F7 change and one
        /// §F6 log entry, and counts Preflight/Execute calls through statics
        /// (a fresh instance is constructed per run, so instance counters
        /// could never observe the harness's instance).
        /// </summary>
        [OpWeaveOperation(ProbeId, 1)]
        public sealed class ProbeOperation : IOperation
        {
            internal static int ExecuteCalls;
            internal static int PreflightCalls;

            public OperationDescriptor Descriptor { get; } = Op.Define(ProbeId, 1)
                .Summary("Echoes its text input and records a change.")
                .Input("text", "core.String", required: true)
                .Output("echo", "core.String")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            ProbeId, 1, "{\"text\":\"hello\"}", "echo"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                PreflightCalls++;
                return new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(
                        new JObject { ["seen"] = ctx.Get<string>("text") }));
            }

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                ctx.Changes.Created(
                    new ChangeTarget("asset", ChangeTargetPath), undoable: false);
                ctx.Log.Write(Severity.Info, "probe ran");
                ctx.SetOutput("echo", "echo:" + ctx.Get<string>("text"));
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
