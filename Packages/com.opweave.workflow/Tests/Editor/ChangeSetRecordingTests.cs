#nullable enable

// T-W2.4-02 [Edit] tests: local:T-W2.4-02:changeset — SPEC §F7 test 1. A Run that
// creates a Material and applies a Prefab override must list both in the Run's
// changes.json with step, nodeId, action and target (kind, path, guid), and the
// element of the Operation that never calls ctx.Changes must be there too — the
// AssetPostprocessor attribution is what keeps that change from being lost.
// The AT-01 custom-Operation half of the §F7 tests belongs to T-W2.11-01.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class ChangeSetRecordingTests
    {
        private const string WorkflowId = "example.changeset";
        private const string ProbeFolder = "Assets/OpWeaveChangeSetProbe";
        private const string MaterialPath = ProbeFolder + "/probe.mat";
        private const string PrefabPath = ProbeFolder + "/probe.prefab";
        private const string NodeMaterial = "mat";
        private const string NodeOverride = "override";
        private const string NodeScene = "scene-objects";
        private const string OpMaterial = "test.changeset.create_material";
        private const string OpOverride = "test.changeset.apply_override";
        private const string OpNoChange = "test.changeset.no_asset_change";
        private const string OpSceneObjects = "test.changeset.touch_scene_objects";
        private const string OpLateIds = "test.changeset.late_identifier_pair";
        private const string OpFlaky = "test.changeset.flaky_change";
        private const string OpHoldChange = "test.changeset.hold_change";
        private const string OpReviewAbort = "test.changeset.review_abort";
        private const string NodeMarker = "marker";
        private const string ProbeScenePath = ProbeFolder + "/probe.unity";
        private const string RetriedPath = ProbeFolder + "/retried.mat";
        private const string HeldPath = ProbeFolder + "/held.mat";

        // Both scene objects of the probe Scene share this hierarchy path — exactly
        // the §F7 case the durable globalId tells apart.
        private const string SceneObjectPath = "ProbePair";
        private const string SceneObjectKind = "gameObject";

        private string _root = null!;
        private SceneSetup[]? _savedSetup;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveChangeSetTests",
                "t" + Guid.NewGuid().ToString("N"));

            // The steps change assets, so the probe owns them in a folder of its
            // own, removed in TearDown (the StepContextIoTests pattern).
            EnsureProbeFolder();

            // The steps instantiate a Prefab, so the test owns an empty Scene the
            // instance can live in, restored in TearDown (the UndoScopeTests
            // pattern).
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // The override step changes an asset the project already had, so the
            // prefab exists before the Run starts: its element must read as a
            // modification, not as an asset this Run created.
            var source = NewObject("OverrideProbeRoot");
            source.AddComponent<MeshRenderer>();
            Assert.IsNotNull(
                PrefabUtility.SaveAsPrefabAsset(source, PrefabPath),
                "the probe prefab must save");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath),
                "the probe prefab must be imported before the Run starts");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            foreach (var go in _objects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            _objects.Clear();

            // The Run opened one Undo group per step; dropping the buffer keeps
            // them from replaying against the objects just destroyed.
            Undo.ClearAll();

            if (_savedSetup is not null && _savedSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(_savedSetup);
                _savedSetup = null;
            }
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private static void EnsureProbeFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveChangeSetProbe");
            }
        }

        // ── SPEC §F7 test 1 / local:T-W2.4-02:changeset ─────────────────────

        [Test]
        public void MaterialCreationAndPrefabOverrideAreFinalizedIntoChangesJson()
        {
            var fixture = NewFixture();
            var materialStep = new CreateMaterialOperation();
            var overrideStep = new ApplyOverrideOperation(_objects);
            fixture.Register(materialStep, 'a');
            fixture.Register(overrideStep, 'c');

            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                Step(0, NodeMaterial, OpMaterial, 'a', EffectNames.AssetCreate, next: 1),
                Step(1, NodeOverride, OpOverride, 'c', EffectNames.AssetModify, next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = NodeMaterial, Operation = OpMaterial, Version = 1 },
                    new NodeData { Id = NodeOverride, Operation = OpOverride, Version = 1 },
                },
                new[] { new ControlEdge(NodeMaterial, NodeOverride) });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            var changes = ReadChanges(_root, runId);

            // The explicit channel: ctx.Changes of the Material step.
            var created = ElementUnder(changes, MaterialPath);
            Assert.AreEqual(0, (int)created["step"]!, "the Material belongs to step 0");
            Assert.AreEqual(NodeMaterial, (string?)created["nodeId"]);
            Assert.AreEqual("created", (string?)created["action"]);
            Assert.AreEqual("asset", (string?)created["target"]?["kind"]);
            Assert.AreEqual(
                AssetDatabase.AssetPathToGUID(MaterialPath),
                (string?)created["target"]?["guid"],
                "the Material element must carry the created asset's guid");
            Assert.AreEqual(false, (bool)created["undoable"]!,
                "an asset change is never undoable (SPEC §G3 item 3)");

            // The automatic channel: this Operation never touches ctx.Changes, so
            // the element can only come from the OnPostprocessAllAssets attribution.
            var modified = ElementUnder(changes, PrefabPath);
            Assert.AreEqual(1, (int)modified["step"]!, "the override belongs to step 1");
            Assert.AreEqual(NodeOverride, (string?)modified["nodeId"]);
            Assert.AreEqual("modified", (string?)modified["action"]);
            Assert.AreEqual("asset", (string?)modified["target"]?["kind"]);
            Assert.That(
                (string?)modified["target"]?["guid"], Is.Not.Null.And.Not.Empty,
                "the override element must carry the changed prefab's guid");
            Assert.AreEqual(false, (bool)modified["undoable"]!);

            // Both channels reported the Material creation (the step recorded it and
            // the import reported it), and the Run's change list keeps one element
            // for one changed target.
            Assert.AreEqual(
                1, CountFor(changes, MaterialPath),
                "the explicit record and the automatic import report are one element: " +
                changes.ToString());
            Assert.AreEqual(1, CountFor(changes, PrefabPath), changes.ToString());

            // The journal keeps the same element the step recorded (§F2).
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"step_succeeded\"",
                    "\"nodeId\":\"" + NodeMaterial + "\"",
                    "\"action\":\"created\"",
                    "\"undoable\":false"),
                Is.True,
                "the Material step journaled no created change");
        }

        // ── two objects of one path are two elements (§F7) ──────────────────

        // SPEC §F7 attempt identity: the failed attempt's durable list and the
        // retried attempt's own list are both finalized, distinguished by
        // `attempt` — the same action on the same target in two attempts is
        // two elements.
        [Test]
        public void AFailedAttemptsChangesStayBesideTheRetriedAttemptsOwn()
        {
            var fixture = NewFixture();
            var flaky = new FlakyChangeOperation();
            fixture.Register(flaky, 'a');

            // The Plan records the descriptor's declared Idempotency — a real
            // compile never carries Unsafe for an Idempotent Operation — and
            // the §E7 gate re-executes a retryable failure only for it.
            var step = RunnerHostFixture.Step(
                0,
                NodeMarker,
                OpFlaky,
                'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                idempotency: Idempotency.Idempotent)
                with
            {
                Effects = new[] { EffectNames.AssetModify },
                Retry = new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 1),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, new[] { step });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = NodeMarker, Operation = OpFlaky, Version = 1 } },
                new List<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            // The retry's notBefore must pass before the retried attempt runs:
            // advance the fake clock between ticks like the resume tests do.
            for (var i = 0; i < 20; i++)
            {
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (state == RunState.Succeeded || state == RunState.Failed ||
                    state == RunState.Cancelled)
                {
                    break;
                }
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId),
                "the retried Run must succeed");

            Assert.AreEqual(2, flaky.Calls, "the failed attempt must be retried");
            var changes = ReadChanges(_root, runId);
            var elements = ElementsUnder(changes, RetriedPath);
            Assert.AreEqual(
                2, elements.Count,
                "the same action on the same target in two attempts is two elements: " +
                changes.ToString());
            var attempts = new List<int>
            {
                (int)elements[0]["attempt"]!,
                (int)elements[1]["attempt"]!,
            };
            Assert.That(attempts, Is.EquivalentTo(new[] { 1, 2 }),
                "the failed attempt's element and the retried attempt's own are " +
                "distinguished by their attempt number");
        }

        // A cancelled step still contributes its durable interim list: the
        // change the cancelled attempt recorded before the cancel rides on the
        // step_cancelled record (SPEC §F2) and is finalized into changes.json.
        [Test]
        public void ACancelledAttemptsChangesAreFinalizedWithItsAttempt()
        {
            var fixture = NewFixture();
            fixture.Register(new RecordingHoldOperation(), 'a');

            var step = RunnerHostFixture.Step(
                0,
                NodeMarker,
                OpHoldChange,
                'a',
                next: -1,
                idempotency: Idempotency.Idempotent) with
            {
                Effects = new[] { EffectNames.AssetModify },
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, new[] { step });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = NodeMarker, Operation = OpHoldChange, Version = 1 } },
                new List<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            for (var i = 0; i < 20 && fixture.Host.Coordinator.StateOf(runId) != RunState.Running; i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_waiting\"", "\"changes\""),
                "the Waiting step did not journal its cumulative changes");
            var cancelled = fixture.Host.Coordinator.Cancel(runId, "stop requested");
            Assert.IsTrue(cancelled.Ok, "cancel failed: " + Describe(cancelled.Diagnostics));
            for (var i = 0; i < 20 &&
                fixture.Host.Coordinator.StateOf(runId) != RunState.Cancelled; i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "tick failed: " + Describe(tick.Diagnostics));
            }

            var changes = ReadChanges(_root, runId);
            var elements = ElementsUnder(changes, HeldPath);
            Assert.AreEqual(
                1, elements.Count,
                "the cancelled attempt's change is finalized: " + changes.ToString());
            Assert.AreEqual(
                1, (int)elements[0]["attempt"]!,
                "the cancelled element carries the attempt that recorded it");
        }

        // The deduplication must not collapse two different targets that share a
        // path: every GameObject of one scene has the same hierarchy path, and the
        // §F7 element the review and a manual rollback work from names the object
        // by its durable globalId.
        [Test]
        public void TwoObjectsSharingOnePathAreBothFinalizedWithTheirOwnIdentifier()
        {
            var fixture = NewFixture();
            var sceneStep = new TwoSceneObjectsOperation(_objects);
            fixture.Register(sceneStep, 'a');

            // GlobalObjectId is only durable on a saved Scene (SPEC §G2): the
            // probe Scene must exist on disk before the step runs, so both
            // objects carry their own id — TearDown drops it with ProbeFolder.
            var scene = EditorSceneManager.GetActiveScene();
            Assert.IsTrue(
                EditorSceneManager.SaveScene(scene, ProbeScenePath),
                "the probe scene must save to disk for durable globalIds");

            var step = RunnerHostFixture.Step(
                0,
                NodeScene,
                OpSceneObjects,
                'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                next: -1,
                idempotency: Idempotency.Unsafe) with { Effects = new[] { EffectNames.SceneModify } };
            var plan = RunnerHostFixture.Plan(WorkflowId, new[] { step });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = NodeScene, Operation = OpSceneObjects, Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.That(
                sceneStep.FirstId, Is.Not.Null.And.Not.Empty,
                "the step must resolve a durable globalId for the first object");
            Assert.That(
                sceneStep.SecondId, Is.Not.EqualTo(sceneStep.FirstId).And.Not.Empty,
                "two objects of one Scene must carry different globalIds");

            var changes = ReadChanges(_root, runId);
            Assert.AreEqual(
                2, CountFor(changes, SceneObjectPath),
                "two objects of one scene path are two elements: " + changes.ToString());

            var first = ElementWith(changes, SceneObjectPath, sceneStep.FirstId);
            Assert.AreEqual(NodeScene, (string?)first["nodeId"]);
            Assert.AreEqual(0, (int)first["step"]!);
            Assert.AreEqual("modified", (string?)first["action"]);
            Assert.AreEqual(
                sceneStep.FirstId, (string?)first["target"]?["globalId"],
                "the first object keeps its own globalId: " + changes.ToString());
            Assert.AreEqual(
                sceneStep.SecondId,
                (string?)ElementWith(
                    changes, SceneObjectPath, sceneStep.SecondId)["target"]?["globalId"],
                "the second object is not dropped: " + changes.ToString());
        }

        // The reconstruction half of the same-path identity (SPEC §F7): the
        // first report of the first object resolved no identifier, a later
        // report of the SAME object supplied its globalId — the deduplication
        // enriches the listed element — and the second object carries its own.
        // The attempt's durable change list must carry the enrichment too: the
        // host a resumed Run gets after a domain reload rebuilds the Run's
        // change list from the journal alone, and a durable list that kept the
        // identifier-less element replays it and the second object into one —
        // the first object silently absent from changes.json.
        [Test]
        public void AnEnrichedElementAndASecondObjectOfOnePathSurviveReconstruction()
        {
            var fixture = NewFixture();
            var sceneStep = new LateIdentifierPairOperation(_objects);
            fixture.Register(sceneStep, 'a');

            // GlobalObjectId is only durable on a saved Scene, as in
            // TwoObjectsSharingOnePathAreBothFinalizedWithTheirOwnIdentifier.
            var scene = EditorSceneManager.GetActiveScene();
            Assert.IsTrue(
                EditorSceneManager.SaveScene(scene, ProbeScenePath),
                "the probe scene must save to disk for durable globalIds");

            var step = RunnerHostFixture.Step(
                0,
                NodeScene,
                OpLateIds,
                'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                next: -1,
                idempotency: Idempotency.Unsafe) with { Effects = new[] { EffectNames.SceneModify } };
            var plan = RunnerHostFixture.Plan(WorkflowId, new[] { step });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = NodeScene, Operation = OpLateIds, Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);
            Assert.That(
                sceneStep.FirstId, Is.Not.Null.And.Not.Empty,
                "the step must resolve a durable globalId for the first object");
            Assert.That(
                sceneStep.SecondId, Is.Not.EqualTo(sceneStep.FirstId).And.Not.Empty,
                "two objects of one Scene must carry different globalIds");

            // The durable truth a reconstructed host reads is the journal: the
            // attempt's journaled change list must carry the first object's
            // enriched globalId — not the identifier-less element.
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"step_succeeded\"",
                    "\"changes\"",
                    sceneStep.FirstId),
                Is.True,
                "the step's durable changes must carry the enriched globalId");

            // The host a resumed Run gets after a domain reload owns none of the
            // in-memory list: its change set is what the journal replays. The
            // finalize writes the Run's slot again from the restored elements.
            var reconstructed = new ChangeSetTracker(_root);
            try
            {
                var failure = reconstructed.FinalizeRun(runId);
                Assert.IsNull(
                    failure,
                    "the reconstructed finalize reported " +
                    (failure is null ? string.Empty : failure.Code + " " + failure.Message));
            }
            finally
            {
                reconstructed.Dispose();
            }

            var changes = ReadChanges(_root, runId);
            Assert.AreEqual(
                2, CountFor(changes, SceneObjectPath),
                "both objects of one path must survive the reconstruction: " +
                changes.ToString());
            Assert.AreEqual(
                sceneStep.FirstId,
                (string?)ElementWith(changes, SceneObjectPath, sceneStep.FirstId)
                    ["target"]?["globalId"],
                "the enriched object is absent from the reconstructed list: " +
                changes.ToString());
            Assert.AreEqual(
                sceneStep.SecondId,
                (string?)ElementWith(changes, SceneObjectPath, sceneStep.SecondId)
                    ["target"]?["globalId"],
                "the second object is absent from the reconstructed list: " +
                changes.ToString());
        }

        // ── a change list the Run Store refused is reported, not lost (§F1) ──

        [Test]
        public void AFailedChangeListWriteIsJournaledAndTheListIsStillFinalizable()
        {
            var fixture = NewFixture();
            fixture.Register(new CreateMaterialOperation(), 'a');

            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                Step(0, NodeMaterial, OpMaterial, 'a', EffectNames.AssetCreate, next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = NodeMaterial, Operation = OpMaterial, Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            // The §F1 write failure: the Run Store refuses the change list.
            fixture.Host.ChangeSets.WriteProbe = (path, records) =>
                new IOException("the probe refuses the change list write");
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex("OW-STORE-001"));

            for (var i = 0; i < 20 &&
                fixture.Host.Coordinator.StateOf(runId) is RunState.Queued or RunState.Running;
                i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "tick failed: " + Describe(tick.Diagnostics));
            }

            // SPEC §F1: a Run Store write failure ends the Run Failed — a
            // Succeeded Run whose changes.json silently went missing is exactly
            // what the durable completion path may never leave.
            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "a refused change list write ends the Run Failed, never Succeeded");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"log\"", "\"code\":\"OW-STORE-001\""),
                Is.True,
                "the change list write failure was not journaled with OW-STORE-001");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"run_finished\"", "\"state\":\"failed\"", "OW-STORE-001"),
                Is.True,
                "the terminal record must carry the store diagnostic the Run ended with");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"step_succeeded\"",
                    "\"action\":\"created\""),
                Is.True,
                "the step's own change record must survive the failed finalize");

            // The slot still holds the empty list CreateRun wrote — never a partial
            // one — and the list itself is not finalized: once the Store works
            // again, the Run's change list is still there to write.
            Assert.That(
                ReadChanges(_root, runId), Is.Empty,
                "a refused write must leave the Run's slot, not a half-written list");
            fixture.Host.ChangeSets.WriteProbe = null;
            Assert.That(
                fixture.Host.ChangeSets.FinalizeRun(runId), Is.Null,
                "the retry once the Store works must succeed");

            var changes = ReadChanges(_root, runId);
            Assert.AreEqual(
                1, CountFor(changes, MaterialPath),
                "the retried finalize writes the Run's change: " + changes.ToString());
        }

        // SPEC §F1: a change-list write the Run Store refuses inside a
        // coordinator-driven end — the human review abort — still ends the
        // Run Failed with OW-STORE-001, never the durable Cancelled the
        // decision asked for while its change list silently went missing.
        // The finalize runs through the same single writer the Tick's ends
        // take, so the pre-terminal refusal rules apply identically here.
        [Test]
        public void AFailedChangeListWriteInAReviewAbortEndsTheRunFailed()
        {
            var fixture = NewFixture();
            fixture.Register(new ReviewBoundOperation(), 'a');

            var step = RunnerHostFixture.Step(
                0, NodeMarker, OpReviewAbort, 'a',
                outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                next: -1,
                idempotency: Idempotency.Reconcilable) with
            {
                Retry = new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 1),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, new[] { step });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = NodeMarker, Operation = OpReviewAbort, Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            for (var i = 0; i < 20 &&
                fixture.Host.Coordinator.StateOf(runId) != RunState.NeedsReview; i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(
                RunState.NeedsReview, fixture.Host.Coordinator.StateOf(runId),
                "the fixture must reach NeedsReview");

            // The §F1 write failure inside the coordinator-driven abort.
            fixture.Host.ChangeSets.WriteProbe = (path, records) =>
                new IOException("the probe refuses the change list write");
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex("OW-STORE-001"));

            var resolved = fixture.Host.Coordinator.ResolveReview(runId, ReviewDecision.Abort);
            Assert.IsTrue(
                resolved.Ok, "the abort's records must be durably written: " +
                Describe(resolved.Diagnostics));

            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "a refused change list write ends the aborted Run Failed, never Cancelled");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"run_finished\"", "\"state\":\"failed\"", "OW-STORE-001"),
                Is.True,
                "the terminal record must carry the store diagnostic the Run ended with");
            Assert.That(
                RunnerHostFixture.JournalHas(
                    _root, runId, "\"type\":\"review_resolved\""),
                Is.True,
                "the abort's own record stays durable");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"log\"", "\"code\":\"OW-STORE-001\""),
                Is.True,
                "the change list write failure was not journaled with OW-STORE-001");
        }

        // ── changes.json is finalized with the Run (§F1) ────────────────────

        // The finalize runs inside the durable completion path: changes.json
        // reaches the Run Store BEFORE the run_finished record — a Run whose
        // terminal state is durable already has its finalized list.
        [Test]
        public void TheChangeListReachesTheRunStoreBeforeTheTerminalRecord()
        {
            var fixture = NewFixture();
            fixture.Register(new CreateMaterialOperation(), 'a');

            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                Step(0, NodeMaterial, OpMaterial, 'a', EffectNames.AssetCreate, next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = NodeMaterial, Operation = OpMaterial, Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var wroteBeforeTerminal = false;
            fixture.Host.ChangeSets.WriteProbe = (path, records) =>
            {
                wroteBeforeTerminal = !RunnerHostFixture.JournalHas(
                    _root, runId, "\"type\":\"run_finished\"");
                return null;
            };

            Drive(fixture.Host, runId);

            Assert.IsTrue(
                wroteBeforeTerminal,
                "changes.json must be written before the terminal record " +
                "(SPEC §F1/§F7 — the durable completion path)");
            Assert.AreEqual(1, CountFor(ReadChanges(_root, runId), MaterialPath));
        }

        [Test]
        public void RunThatChangesNothingFinalizesAnEmptyChangeList()
        {
            var fixture = NewFixture();
            fixture.Register(new RunnerHostFixture.MarkerOperation(
                OpNoChange, fixture.Events), 'a');

            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, NodeMarker, OpNoChange, 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = NodeMarker, Operation = OpNoChange, Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.That(
                ReadChanges(_root, runId), Is.Empty,
                "a Run that changed nothing finalizes an empty list");
        }

        // ── changes.json reading ────────────────────────────────────────────

        private static JArray ReadChanges(string root, string runId)
        {
            var path = Path.Combine(root, "Runs", runId, "changes.json");
            Assert.IsTrue(
                File.Exists(path), "the Run Store holds no changes.json for " + runId);
            var parsed = JsonValue.Parse(File.ReadAllText(path));
            Assert.IsTrue(parsed.Ok, "changes.json must parse");
            var token = parsed.Value!.ToToken();
            Assert.IsInstanceOf<JArray>(token, "changes.json is a change array (SPEC §F7)");
            return (JArray)token;
        }

        private static JObject ElementUnder(JArray changes, string assetPath)
        {
            var element = changes.OfType<JObject>().FirstOrDefault(
                e => string.Equals((string?)e["target"]?["path"], assetPath,
                    StringComparison.Ordinal));
            Assert.IsNotNull(
                element,
                "changes.json lists no element for " + assetPath + ": " + changes.ToString());
            return element!;
        }

        private static List<JObject> ElementsUnder(JArray changes, string assetPath) =>
            changes.OfType<JObject>().Where(
                e => string.Equals((string?)e["target"]?["path"], assetPath,
                    StringComparison.Ordinal)).ToList();

        private static int CountFor(JArray changes, string assetPath) =>
            changes.OfType<JObject>().Count(
                e => string.Equals((string?)e["target"]?["path"], assetPath,
                    StringComparison.Ordinal));

        // The element of one path that carries this durable identifier — the §F7
        // element a second object of the same path must not be collapsed into.
        private static JObject ElementWith(JArray changes, string path, string globalId)
        {
            var element = changes.OfType<JObject>().FirstOrDefault(
                e => string.Equals((string?)e["target"]?["path"], path, StringComparison.Ordinal) &&
                    string.Equals((string?)e["target"]?["globalId"], globalId,
                        StringComparison.Ordinal));
            Assert.IsNotNull(
                element,
                $"changes.json lists no element for {path} with globalId {globalId}: " +
                changes.ToString());
            return element!;
        }

        // The §F7 elements are canonical JSON, so the members are sorted and a
        // member is not adjacent to its sibling — every assertion above reads the
        // parsed document instead of matching journal-style substrings.
        private static PlanStep Step(
            int index,
            string nodeId,
            string operationId,
            char hashSeed,
            string effect,
            int next)
        {
            var step = RunnerHostFixture.Step(
                index,
                nodeId,
                operationId,
                hashSeed,
                outputTypes: new Dictionary<string, string> { ["guid"] = "core.String" },
                next: next,
                idempotency: Idempotency.Unsafe);
            return step with { Effects = new[] { effect } };
        }

        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    Assert.AreEqual(RunState.Succeeded, state, "the Run did not succeed");
                    return;
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // ── Test operations ─────────────────────────────────────────────────

        /// <summary>
        /// Two GameObjects of the probe Scene under the same hierarchy path — the
        /// §F7 case where the path alone never says which object changed. Both
        /// changes are recorded through <c>ctx.Changes</c> with the globalId each
        /// object really carries, and the step touches no asset, so the Run's
        /// finalized list can only be what this step recorded.
        /// </summary>
        // Records one change per Execute and fails retryable the first time —
        // the §F2 attempt identity shows up as two elements of one path, one
        // per attempt.
        private sealed class FlakyChangeOperation : OperationBase
        {
            internal int Calls { get; private set; }

            public override OperationDescriptor Descriptor { get; } =
                Op.Define(OpFlaky, 1)
                    .Summary("Records, fails once, records again and succeeds.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.AssetModify)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();

            public override StepResult Execute(StepContext ctx)
            {
                Calls++;
                ctx.Changes.Modified(
                    new ChangeTarget("asset", RetriedPath), undoable: false);
                if (Calls == 1)
                {
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_008,
                        "A transient failure.",
                        retryable: true));
                }
                ctx.SetOutput("mark", "done");
                return StepResult.Succeeded();
            }
        }

        // A Reconcilable step that fails retryably and never decides — the
        // NeedsReview fixture a review abort ends through the coordinator.
        private sealed class ReviewBoundOperation : OperationBase
        {
            public override OperationDescriptor Descriptor { get; } =
                Op.Define(OpReviewAbort, 1)
                    .Summary("Fails retryably; never decides.")
                    .Output("out", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();

            public override StepResult Execute(StepContext ctx) =>
                StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true));

            public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never decides.");
        }

        // Records one change then holds in Waiting — cancelled mid-attempt, the
        // change rides on the step_cancelled record.
        private sealed class RecordingHoldOperation : OperationBase
        {
            public override OperationDescriptor Descriptor { get; } =
                Op.Define(OpHoldChange, 1)
                    .Summary("Records a change, then holds.")
                    .Effects(EffectNames.AssetModify)
                    .Undo(UndoSupport.None)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };

            public override StepResult Execute(StepContext ctx)
            {
                ctx.Changes.Modified(
                    new ChangeTarget("asset", HeldPath), undoable: false);
                return StepResult.Waiting(RunnerHostFixture.Json("0"), "holding", 1);
            }

            public override StepResult Poll(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("0"), "holding", 1);
        }

        private sealed class TwoSceneObjectsOperation : OperationBase
        {
            private readonly List<GameObject> _objects;

            internal TwoSceneObjectsOperation(List<GameObject> objects) =>
                _objects = objects;

            internal string FirstId { get; private set; } = string.Empty;
            internal string SecondId { get; private set; } = string.Empty;

            public override OperationDescriptor Descriptor { get; } =
                Op.Define(OpSceneObjects, 1)
                    .Summary("Records two scene objects sharing one hierarchy path.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.SceneModify)
                    .Undo(UndoSupport.Partial)
                    .Idempotency(Idempotency.Unsafe)
                    .Build();

            public override StepResult Execute(StepContext ctx)
            {
                var first = NewPairMember();
                var second = NewPairMember();
                // No assertions in here — an Assert inside Execute reports as a
                // step failure the Run records, so the ids reach the test body
                // and the test decides. The saved Scene is what makes the two
                // ids real and different.
                FirstId = GlobalObjectId.GetGlobalObjectIdSlow(first).ToString();
                SecondId = GlobalObjectId.GetGlobalObjectIdSlow(second).ToString();

                ctx.Changes.Modified(
                    new ChangeTarget(SceneObjectKind, SceneObjectPath, null, FirstId),
                    undoable: true);
                ctx.Changes.Modified(
                    new ChangeTarget(SceneObjectKind, SceneObjectPath, null, SecondId),
                    undoable: true);
                ctx.SetOutput("mark", "done");
                return StepResult.Succeeded();
            }

            private GameObject NewPairMember()
            {
                var go = new GameObject(SceneObjectPath);
                _objects.Add(go);
                return go;
            }
        }

        /// <summary>
        /// Records the report sequence that drops a target when the attempt's
        /// durable list keeps the identifier-less element: the first object is
        /// reported with no identifier (the shape of a report that could not
        /// resolve one — the import callback that fires before the AssetDatabase
        /// answers), then the same object with its globalId — an enrichment of
        /// the listed element, not a second target — then the second object of
        /// the same path with its own globalId.
        /// </summary>
        private sealed class LateIdentifierPairOperation : OperationBase
        {
            private readonly List<GameObject> _objects;

            internal LateIdentifierPairOperation(List<GameObject> objects) =>
                _objects = objects;

            internal string FirstId { get; private set; } = string.Empty;
            internal string SecondId { get; private set; } = string.Empty;

            public override OperationDescriptor Descriptor { get; } =
                Op.Define(OpLateIds, 1)
                    .Summary("Records one object identifier-less then identified, then a second object.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.SceneModify)
                    .Undo(UndoSupport.Partial)
                    .Idempotency(Idempotency.Unsafe)
                    .Build();

            public override StepResult Execute(StepContext ctx)
            {
                var first = NewPairMember();
                var second = NewPairMember();
                // No assertions in here — an Assert inside Execute reports as a
                // step failure the Run records, so the ids reach the test body
                // and the test decides.
                FirstId = GlobalObjectId.GetGlobalObjectIdSlow(first).ToString();
                SecondId = GlobalObjectId.GetGlobalObjectIdSlow(second).ToString();

                ctx.Changes.Modified(
                    new ChangeTarget(SceneObjectKind, SceneObjectPath), undoable: true);
                ctx.Changes.Modified(
                    new ChangeTarget(SceneObjectKind, SceneObjectPath, null, FirstId),
                    undoable: true);
                ctx.Changes.Modified(
                    new ChangeTarget(SceneObjectKind, SceneObjectPath, null, SecondId),
                    undoable: true);
                ctx.SetOutput("mark", "done");
                return StepResult.Succeeded();
            }

            private GameObject NewPairMember()
            {
                var go = new GameObject(SceneObjectPath);
                _objects.Add(go);
                return go;
            }
        }

        /// <summary>
        /// Creates a Material asset and records the change the way SPEC §C2 item 6
        /// asks an Operation to. <c>AssetDatabase.CreateAsset</c> imports the new
        /// asset synchronously, so the §F7 attribution of the import Unity reports
        /// happens inside this step.
        /// </summary>
        private sealed class CreateMaterialOperation : OperationBase
        {
            public override OperationDescriptor Descriptor { get; } = Op.Define(OpMaterial, 1)
                .Summary("Creates a Material asset in the probe folder.")
                .Output("guid", "core.String")
                .Effects(EffectNames.AssetCreate)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Unsafe)
                .Build();

            public override StepResult Execute(StepContext ctx)
            {
                EnsureProbeFolder();
                var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
                AssetDatabase.CreateAsset(material, MaterialPath);
                var guid = AssetDatabase.AssetPathToGUID(MaterialPath);
                Assert.That(guid, Is.Not.Null.And.Not.Empty, "the Material must be imported");

                ctx.Changes.Created(
                    new ChangeTarget("asset", MaterialPath, guid), undoable: false);
                ctx.SetOutput("guid", guid);
                return StepResult.Succeeded();
            }
        }

        /// <summary>
        /// Applies a Prefab override — the change SPEC §F7 must not lose. It never
        /// touches <c>ctx.Changes</c>: its element in changes.json can only come
        /// from the automatic <c>OnPostprocessAllAssets</c> attribution. The step
        /// pins the prefab's import to its own boundary the way the standard asset
        /// Operations do (CreateScriptFromTemplateOperation), so the report arrives
        /// while the step still runs.
        /// </summary>
        private sealed class ApplyOverrideOperation : OperationBase
        {
            private readonly List<GameObject> _instances;

            internal ApplyOverrideOperation(List<GameObject> instances) =>
                _instances = instances;

            public override OperationDescriptor Descriptor { get; } = Op.Define(OpOverride, 1)
                .Summary("Applies a Prefab override without recording it.")
                .Output("guid", "core.String")
                .Effects(EffectNames.AssetModify)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Unsafe)
                .Build();

            public override StepResult Execute(StepContext ctx)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                Assert.IsNotNull(prefab, "the probe prefab must load");
                var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
                Assert.IsNotNull(material, "the Material of the previous step must load");

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab!)!;
                _instances.Add(instance);
                var renderer = instance.GetComponent<MeshRenderer>();
                Assert.IsNotNull(renderer, "the probe prefab carries a MeshRenderer");
                renderer!.sharedMaterial = material!;
                PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);
                EditorUtility.SetDirty(prefab!);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(
                    PrefabPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                var guid = AssetDatabase.AssetPathToGUID(PrefabPath);
                Assert.That(guid, Is.Not.Null.And.Not.Empty, "the prefab keeps its guid");
                ctx.SetOutput("guid", guid);
                return StepResult.Succeeded();
            }
        }
    }
}
