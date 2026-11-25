#nullable enable

// T-W4.5-02 [Edit] tests: local:T-W4.5-02:at02-prefab — the Runner half of
// the SPEC §F5 contract for the six Reconcilable Operations of the §I.2
// Prefab group (unity.prefab.create_from_gameobject, unity.prefab.instantiate,
// unity.prefab.unpack, unity.prefab.create_variant,
// unity.prefab.open_contents, unity.prefab.save_contents): a Run parked
// mid-flight and cut back to the bare step_started{step:0} — its Execute
// effect applied, its recorded evidence then disturbed — must be decided
// Unknown and end NeedsReview on the §F4 recovery pass, never an automatic
// success and never an automatic re-execution (SPEC §F5 rules 1-6, §F0).
// Two more per-Operation rows complete the §F5 six: a same-name artifact
// that predates the step is never claimed as the step's product (common
// rule 4), and an Editor restart the journaled snapshot cannot survive is
// judged by whatever evidence the row has left — Unknown and NeedsReview
// where the identity is session-bound, the row's own proven verdict where
// it is not, so a restart resumes only through Completed adoption or the
// one safe re-execution, never through silence.
// The six same-domain tests drive the real OpWeaveRunnerHost + FileRunStore
// path the T-W4.3-03 fixtures established; the [UnityTest] companion runs
// the identical contract across a real domain reload (SPEC §T.3), where
// every started-only disturbance in the group must still end NeedsReview
// one domain later.
//
// The journal surgery and the park/recover mechanics mirror
// At02SceneGameObjectRecoveryTests: [step -> hold] plans on the real
// Operations, TruncateThrough to the started-only tail, and the
// marker-script + domain-sentinel reload of the reload companion. SPEC §E2
// admits at most one mutating Run per Run Store, so the reload companion
// parks each Operation's Run in a store of its own and never disposes a
// phase-1 host: the OS exclusion each parked Run holds is a process-held
// handle that survives the domain reload, the hand-off seam of SPEC §E2
// item 3 the post-reload pass re-adopts.
//
// The two contents Operations exercise the §E6 item 4 Run resource the
// group owns: open_contents leaves the editing session it opened bound to
// the step's acquisition scope — the §F5 row its step's own acquisition
// must prove — and save_contents runs against a contents the test opens
// and binds under the real Run's step-0 scope, the state a true
// open -> save plan leaves.

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
using OpWeave.Core.Recovery;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.5-02:at02-prefab</c> recovery cases: every Reconcilable
    /// Operation of the SPEC §I.2 Prefab group, disturbed so its §F5
    /// evidence row no longer holds, ends NeedsReview — in the same domain
    /// and across a real domain reload.
    /// </summary>
    internal sealed class At02PrefabRecoveryTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAt02PrefabRecovery");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string StateFile => Path.Combine(ProbeDir, "state.txt");

        // A per-Editor-session nonce the phase-1 state write arms in
        // SessionState and on disk (the At03NoDuplicatesTests discipline,
        // T-W2.4-07): SessionState and the Library file both survive a
        // domain reload but die with the Editor, so a probe root whose
        // nonce still matches the armed one is THIS session's live
        // in-flight state a replayed setup must never reclaim — anything
        // else is stale residue. The nonce is the session-token the
        // phase-2 state read and the stale-resume re-drive gate on.
        private const string ProbeSessionKey = "OpWeave.At02Prefab.probe-state";

        private static string NonceFile => Path.Combine(ProbeDir, "probe.nonce");

        // The reload group's per-Operation Run Store: six started-only Runs
        // unfinished at once need a store each — SPEC §E2 admits at most
        // one mutating Run per store, so a second accept on one coordinator
        // fails OW-RUN-002, and the §F4 recovery pass drives only the
        // single Run the exclusion passes to.
        private static string StoreRootFor(string key) =>
            Path.Combine(ProbeDir, "run-root-" + key);

        // The group stores in phase order — WriteState persists the run
        // ids in this order and the post-reload phase walks them the
        // same way.
        private static readonly string[] s_groupKeys =
            { "create", "instantiate", "unpack", "variant", "open", "save" };

        private static IEnumerable<string> AllStoreRoots()
        {
            yield return StoreRoot;
            for (var i = 0; i < s_groupKeys.Length; i++)
            {
                yield return StoreRootFor(s_groupKeys[i]);
            }
        }

        private const string ProbeFolder = "Assets/OpWeaveAt02PrefabResume";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";
        private const string BasePrefabPath = ProbeFolder + "/Base.prefab";
        private const string SourcePrefabPath = ProbeFolder + "/Source.prefab";
        private const string FromObjectPath = ProbeFolder + "/FromObject.prefab";
        private const string VariantPath = ProbeFolder + "/Variant.prefab";

        private static string MarkerFolder => "Assets/Editor/OpWeaveAt02PrefabReload";
        private static string MarkerDisk =>
            Path.Combine(
                ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveAt02PrefabReload\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private const string WorkflowId = "example.at02.prefab.recovery";
        private const string HoldOp = "test.at02.prefab.resume.hold";
        private const string OpenId = "unity.prefab.open_contents";
        private const string SaveId = "unity.prefab.save_contents";
        private const string SourceObjectName = "At02ResPrefabSrc";
        private const string ForeignChildName = "At02ResUnpackForeign";
        private const string ForeignOverrideName = "At02ResForeign";
        private const string EditedChildName = "At02ResEdited";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // T-W2.4-07: bound on phase-1 re-drive legs after a predecessor's
        // stale resume is detected. leg is a method local, so a real
        // reload resets it; the live-nonce probe check is what actually
        // ends the loop (the ChangeSetDomainReloadTests pattern).
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) => Debug.Log("[W4502] " + message);

        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static PhaseResult Pass() => new PhaseResult { Ok = true };

            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // ── Setup / cleanup ─────────────────────────────────────────────

        // No plain [SetUp]/[TearDown]: NUnit's setup/teardown command keeps
        // no persisted state, so a [UnityTest] resumed across a domain
        // reload runs [SetUp] again before the iterator continues — here
        // that deleted ProbeDir and the phase-1 state file mid-test. The
        // cleanup lives in [UnitySetUp]/[UnityTearDown], the persisted
        // outer command — and UTF replays THOSE commands from their first
        // statement on every domain a [UnityTest] crosses, so the probe
        // nonce gates the setup: a replay around the in-flight test
        // restores nothing and deletes nothing (the body it resumes into
        // owns the probe state — the At03NoDuplicatesTests discipline).
        // Neither command may yield an ExpectDomainReload instruction
        // (RecompileScripts): UTF's PrepareForDomainReload records the
        // CURRENT TestEnumerator's program counter — still a different
        // test's method enumerator during setup and teardown — and the
        // resume then applies that stale PC to the next test method's
        // fresh enumerator, jumping it past phase-1 entirely when the
        // captured enumerator was abandoned mid-state. The compile the
        // cleanup's script deletions queue stays behind the runner's
        // LockReloadAssemblies and drains at the next test body's own
        // armed RecompileScripts yield, whose captured PC is the correct
        // one (the ChangeSetDomainReloadTests precedent).
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (ProbeStateIsLive())
            {
                Crumb("setup replayed around the in-flight test — " +
                    "the live probe state is left untouched");
                yield break;
            }
            NewUntitledScene();
            var dirty = CleanupArtifacts();
            Crumb("setup cleanup exit; assetsDirty=" + dirty);
            NewBaseScene();
            yield break;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            // The test is over — drop the live-state nonce so the cleanup
            // below really does reclaim the probe root.
            DisarmProbeNonce();
            DisposeAll();
            NewUntitledScene();
            PrefabContentsFixture.UnloadAllOpenedContents();
            ClearHandoffRecords();
            var dirty = CleanupArtifacts();
            Crumb("teardown cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        private void DisposeAll()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
        }

        // A saved, clean base scene: the Operations under test need a loaded
        // scene to work in.
        private static void NewBaseScene()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveAt02PrefabResume");
            }
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(
                EditorSceneManager.SaveScene(scene, BaseScenePath), Is.True,
                "the recovery test's base scene must save.");
        }

        private static void NewUntitledScene() =>
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // The T-W4.3-01 cleanup contract, verbatim in shape, once per Run
        // Store the fixture touches: the exclusion this process still
        // holds on a probe root — the previous domain's or a fixture's —
        // is taken through the hand-off seam and ended the orderly way so
        // lock.json empties BEFORE the handle frees; then both hand-off
        // records drop.
        private static void ReleaseLeakedState()
        {
            foreach (var storeRoot in AllStoreRoots())
            {
                ReleaseLeakedState(storeRoot);
            }
        }

        private static void ReleaseLeakedState(string storeRoot)
        {
            var obtained = RunLockHandoff.TryObtain(storeRoot);
            if (!obtained.Ok || obtained.Value is not { } held)
            {
                Crumb("the probe Run Store is excluded elsewhere: " +
                    Describe(obtained.Diagnostics));
                RunLockHandoff.Clear(storeRoot);
                return;
            }
            if (!held.Release().Ok)
            {
                held.Dispose();
            }
            RunLockHandoff.Clear(storeRoot);
        }

        private static void ClearHandoffRecords()
        {
            foreach (var storeRoot in AllStoreRoots())
            {
                RunLockHandoff.Clear(storeRoot);
            }
        }

        // The probe-state cleanup — safe inside UnitySetUp and
        // UnityTearDown, which must never schedule a reload. A replayed
        // command around a live test leaves the probe root untouched:
        // only a ProbeDir whose nonce is not the armed one is stale
        // residue, so the Run-lock reclaim and the deletes run only
        // while no in-flight test owns the probe state.
        private static bool CleanupArtifacts()
        {
            if (ProbeStateIsLive())
            {
                return false;
            }
            ReleaseLeakedState();
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            var dirty = false;
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
                dirty = true;
            }
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
                dirty = true;
            }
            if (Directory.Exists(MarkerDisk))
            {
                Directory.Delete(MarkerDisk, recursive: true);
                dirty = true;
            }
            return dirty;
        }

        // ── Fixtures, plans, accept/tick/recover ────────────────────────

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(StoreRoot);
            _fixtures.Add(fixture);
            RegisterAll(fixture);
            return fixture;
        }

        private void DisposeFixture(RunnerHostFixture fixture)
        {
            fixture.Dispose();
            _fixtures.Remove(fixture);
        }

        // Every Reconcilable Operation of the group plus the hold step, at
        // the seeds the parked plans' fingerprints were built with — a fresh
        // host must resolve each step's implementation after a host swap or
        // a domain reload. The §E6 item 4 release dispatcher carries the
        // production prefab-contents handler the real host registers, so a
        // Run end that does reach the release seam discards a held session.
        private static void RegisterAll(RunnerHostFixture fixture)
        {
            RegisterAs(
                fixture, new PrefabCreateFromGameObjectOperation(), 'a',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new PrefabInstantiateOperation(), 'c',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new PrefabUnpackOperation(), 'e',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new PrefabCreateVariantOperation(), '0',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new PrefabOpenContentsOperation(), '2',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new PrefabSaveContentsOperation(), '6',
                OperationOrigin.Standard);
            fixture.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), '4');
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind, PrefabContentsResources.Release);
        }

        private static void RegisterAs(
            RunnerHostFixture fixture, IOperation op, char hashSeed, OperationOrigin origin) =>
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor,
                origin,
                "Example.Operations",
                null,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64))
            {
                Implementation = op,
            });

        private static PlanStep Step(
            int index,
            string nodeId,
            string operationId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int next = -1,
            Idempotency idempotency = Idempotency.Unsafe) =>
            RunnerHostFixture.Step(
                index, nodeId, operationId, hashSeed,
                inputs: inputs, outputTypes: outputTypes,
                next: next, idempotency: idempotency);

        private static WorkflowDocument Doc(
            IReadOnlyList<(string id, string operation)> nodes,
            params (string from, string to)[] edges) =>
            RunnerHostFixture.Doc(
                WorkflowId,
                nodes
                    .Select(n => new NodeData { Id = n.id, Operation = n.operation, Version = 1 })
                    .ToList(),
                edges
                    .Select(e => new ControlEdge(e.from, e.to))
                    .ToList());

        private static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        private static void Tick(RunnerHostFixture fixture)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "the tick failed: " + Describe(tick.Diagnostics));
        }

        private static RunRecoveryReport Recover(RunnerHostFixture fixture)
        {
            var recovered = fixture.Host.RecoverRuns();
            Assert.IsTrue(recovered.Ok, "the recovery pass failed: " +
                Describe(recovered.Diagnostics));
            Assert.IsNotNull(recovered.Value);
            return recovered.Value!;
        }

        private static void AssertNeedsReview(
            string storeRoot, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.AreEqual(
                RunState.NeedsReview,
                fixture.Host.Coordinator.StateOf(runId),
                because + " must end in NeedsReview; journal: " + string.Join(
                    " | ", RunnerHostFixture.JournalLines(storeRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(
                    storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a review-needed resume never journals run_resumed");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    storeRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                because + ": the Unknown verdict is journaled step_needs_review (§E1)");
        }

        // The decided half of an §F5 started-only judgement: the verdict —
        // whichever the row's evidence proved — journaled run_resumed
        // (§F4 item 6's write-ahead) and never step_needs_review; the
        // caller's checks then name whether the resume adopted the step's
        // outputs or re-executed it.
        private static void AssertResumed(
            string storeRoot, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(
                    storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a decided resume journals run_resumed before " +
                    "the outcome is applied (§F4 item 6)");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    storeRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                because + " must not end the interrupted step in NeedsReview");
            Assert.AreNotEqual(
                RunState.NeedsReview,
                fixture.Host.Coordinator.StateOf(runId),
                because + " must not end in NeedsReview");
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // ── Journal surgery ─────────────────────────────────────────────

        private static string[] Lines(string storeRoot, string runId) =>
            RunnerHostFixture.JournalLines(storeRoot, runId);

        private static void WriteLines(
            string storeRoot, string runId, IEnumerable<string> lines)
        {
            File.WriteAllText(
                RunnerHostFixture.JournalPath(storeRoot, runId),
                string.Join("\n", lines) + "\n", s_utf8);
        }

        // Keeps the records through the FIRST line carrying both fragments —
        // the crash window between that durable record and whatever the loop
        // was about to append next.
        private static void TruncateThrough(
            string storeRoot, string runId, string typeFragment, string stepFragment)
        {
            var lines = Lines(storeRoot, runId);
            var cut = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(typeFragment) && lines[i].Contains(stepFragment))
                {
                    cut = i;
                    break;
                }
            }
            Assert.GreaterOrEqual(cut, 0,
                "the journal never recorded " + typeFragment + " " + stepFragment +
                ": " + string.Join(" | ", lines));
            WriteLines(storeRoot, runId, lines.Take(cut + 1));
        }

        // The regression guard of the started-only fixtures: the journal the
        // recovering host reads must END at the bare step_started{step} — a
        // record past it is a two-steps-in-flight journal the §F4 rebuild
        // correctly refuses, never the interruption the fixture means to
        // leave.
        private static void AssertStartedOnlyTail(string storeRoot, string runId, int step)
        {
            var tail = Lines(storeRoot, runId);
            Assert.IsTrue(
                tail.Length > 0 &&
                tail[tail.Length - 1].Contains("\"type\":\"step_started\"") &&
                tail[tail.Length - 1].Contains("\"step\":" + step),
                "the journal must end at step " + step +
                "'s step_started with no later records; journal: " +
                string.Join(" | ", tail));
        }

        // The Editor-restart half of the §F5 started-only rows: the evidence
        // the step journaled was recorded in an Editor session that no longer
        // exists. One Editor cannot be restarted, so the journaled
        // step_started{step:0} snapshot's recorded editorSessionId is moved
        // to a session this Editor never had — re-encoded through
        // JournalCodec so seq, the required flags and the crc stay a valid
        // v1 envelope — which is exactly the envelope the first recovery
        // pass of a new Editor session would read from this journal.
        private static void SimulateEditorRestart(string storeRoot, string runId)
        {
            var lines = Lines(storeRoot, runId);
            var rewritten = false;
            for (var i = 1; i < lines.Length; i++)
            {
                var read = JournalCodec.ReadRecord(lines[i], i);
                if (read.Status != JournalLineStatus.Decoded ||
                    read.Record is not { } record ||
                    record.Type != JournalRecordType.StepStarted ||
                    record.Step != 0 ||
                    record.Data.ToToken() is not JObject data ||
                    data["snapshot"] is not JObject ||
                    data["snapshot"]!["editorSessionId"] is null)
                {
                    continue;
                }
                data = (JObject)data.DeepClone();
                ((JObject)data["snapshot"]!)["editorSessionId"] =
                    "at02-simulated-editor-restart";
                lines[i] = JournalCodec.WriteRecord(
                    record with { Data = JsonValue.From(data) });
                rewritten = true;
            }
            Assert.IsTrue(
                rewritten,
                "no step_started{step:0} snapshot carried an editorSessionId " +
                "the restart could invalidate — the restart case exercises " +
                "nothing");
            WriteLines(storeRoot, runId, lines);
        }

        // The session-token pair the nonce proves: the file under the
        // probe root carries the nonce WriteState armed in SessionState,
        // and both die together on any cleanup that legitimately reclaims
        // the root — a file without the armed session's nonce is residue
        // of an earlier Editor session, never this run's phase-1.
        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(NonceFile, nonce + "\n", s_utf8);
            SessionState.SetString(ProbeSessionKey, nonce);
            return nonce;
        }

        private static void DisarmProbeNonce() =>
            SessionState.EraseString(ProbeSessionKey);

        private static bool ProbeStateIsLive()
        {
            var nonce = SessionState.GetString(ProbeSessionKey, "");
            return nonce.Length > 0 && File.Exists(NonceFile) &&
                string.Equals(
                    File.ReadAllText(NonceFile).Trim(), nonce,
                    StringComparison.Ordinal);
        }

        // A phase-1 state write also arms the probe-state nonce: from
        // here on the probe root is live in-flight state a replayed
        // setup or cleanup must not reclaim.
        private static void WriteState(params string[] lines)
        {
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(StateFile, string.Join("\n", lines) + "\n", s_utf8);
            ArmProbeNonce();
        }

        private static string[] ReadState()
        {
            Assert.IsTrue(File.Exists(StateFile), "the phase-1 state file is missing");
            Assert.IsTrue(
                ProbeStateIsLive(),
                "the phase-1 probe state is stale — the nonce on disk is " +
                "not this Editor session's, so what phase-2 would read " +
                "is residue, not the state this session's phase-1 wrote");
            return File.ReadAllLines(StateFile);
        }

        private static void ArmReload()
        {
            Directory.CreateDirectory(MarkerDisk);
            File.WriteAllText(
                Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
            AssetDatabase.Refresh();
            ArmDomainSentinel();
        }

        // ── Probe Prefab fixtures ───────────────────────────────────────

        // A probe Prefab asset under the probe folder, from a scratch source
        // object the base scene loses again — returns (asset, path, guid)
        // for the PrefabAssetRef constants the plans carry.
        private static (GameObject Asset, string Path, string Guid) NewProbePrefab(
            string fileName, bool marker = false)
        {
            var path = ProbeFolder + "/" + fileName;
            var source = new GameObject(Path.GetFileNameWithoutExtension(fileName));
            try
            {
                // The marker component gives the artifact a checkable content
                // difference — a prefab root's name alone normalizes to the
                // file's stem and would prove nothing.
                if (marker)
                {
                    source.AddComponent<BoxCollider>();
                }
                var saved = PrefabUtility.SaveAsPrefabAsset(source, path);
                Assert.IsNotNull(saved, "the probe Prefab must save: " + path);
                var guid = AssetDatabase.AssetPathToGUID(path);
                Assert.IsNotEmpty(guid, "the probe Prefab must carry a guid: " + path);
                return (saved!, path, guid);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        // An outermost instance of the probe asset in the saved base scene —
        // the scene the Operations' default scene resolution and every
        // scene-free GameObjectRef resolves against.
        private static GameObject NewProbeInstance(GameObject asset)
        {
            var instance = PrefabUtility.InstantiatePrefab(
                asset, SceneManager.GetSceneByPath(BaseScenePath)) as GameObject;
            Assert.IsNotNull(instance, "the probe Prefab instance must be created.");
            var scene = SceneManager.GetSceneByPath(BaseScenePath);
            Assert.That(
                EditorSceneManager.SaveScene(scene), Is.True,
                "the base scene must re-save with its fixture instance");
            return instance!;
        }

        // Every live outermost instance of the given Prefab asset under the
        // base scene's roots — the set a re-executed instantiate grows and a
        // NeedsReview verdict leaves untouched.
        private static List<GameObject> OuterInstances(string prefabPath)
        {
            var scene = SceneManager.GetSceneByPath(BaseScenePath);
            var instances = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (PrefabUtility.IsOutermostPrefabInstanceRoot(root) &&
                    string.Equals(
                        PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root),
                        prefabPath,
                        StringComparison.Ordinal))
                {
                    instances.Add(root);
                }
            }
            return instances;
        }

        // The live outermost instance of the given Prefab asset under the
        // base scene's roots — the object a parked instantiate or unpack
        // step left for its evidence row.
        private static GameObject FindOuterInstance(string prefabPath)
        {
            var instances = OuterInstances(prefabPath);
            if (instances.Count != 0)
            {
                return instances[0];
            }
            Assert.Fail("no outermost instance of " + prefabPath + " at the base scene root");
            return null!;
        }

        // The disturbed-artifact write the asset-producing Operations get:
        // a different valid Prefab over the path Execute wrote — content a
        // recorded PrefabContentFingerprint can never match, so the §G6.1
        // check settles Unknown instead of the Completed a stale fingerprint
        // would have produced. The foreign component is the evidence: Unity
        // normalizes a saved Prefab root to the file's stem name, so a root
        // name alone could never tell the disturbance from the original —
        // the marker component is what a matching fingerprint cannot hold.
        private static void DamageAsset(string assetPath)
        {
            var foreign = new GameObject(ForeignOverrideName);
            try
            {
                foreign.AddComponent<BoxCollider>();
                var saved = PrefabUtility.SaveAsPrefabAsset(foreign, assetPath);
                Assert.IsNotNull(saved,
                    "the disturbance could not overwrite " + assetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(foreign);
            }
        }

        // ── The [op -> hold] park ───────────────────────────────────────

        // Accepts [step -> hold] and ticks once: step 0 executes, step 1's
        // waitable parks the Run mid-flight — the interruption the journal
        // then fakes by truncation back to the bare step_started{step:0}.
        private static string ParkStartedOnly(
            RunnerHostFixture fixture,
            string opId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource> inputs,
            IReadOnlyDictionary<string, string> outputTypes)
        {
            var steps = new[]
            {
                Step(0, "s", opId, hashSeed, next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: inputs, outputTypes: outputTypes),
                Step(1, "h", HoldOp, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("s", opId), ("h", HoldOp) }, ("s", "h"));
            var runId = Accept(fixture, plan, doc);
            Tick(fixture);
            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                "the " + opId + " Run did not park mid-flight; journal: " +
                string.Join(" | ", Lines(fixture.Root, runId)));
            return runId;
        }

        // unity.prefab.save_contents parks against a contents the Run must
        // own: the session is loaded outside the step (a previous
        // open_contents step's state in a real plan), its Ref becomes the
        // step's constant input, and the ownership binding is written under
        // the accepted Run's step-0 scope before the tick — the state
        // §G6 item 5's open -> save hand-off leaves for the save step.
        private static string ParkSaveStartedOnly(
            RunnerHostFixture fixture,
            string prefabPath,
            string guid,
            GameObject contents)
        {
            var rootRef = new RefResolver(PrefabContentsResources.FindEditingRoot)
                .ToRef(contents, "unity.GameObjectRef");
            var inputs = new Dictionary<string, InputSource>
            {
                ["root"] = new Constant(rootRef),
            };
            var steps = new[]
            {
                Step(0, "s", SaveId, '6', next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: inputs, outputTypes: s_prefabOut),
                Step(1, "h", HoldOp, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("s", SaveId), ("h", HoldOp) }, ("s", "h"));
            var runId = Accept(fixture, plan, doc);
            PrefabContentsOwnership.Bind(
                RunResourceScope.Of(runId, 0), guid, contents);
            Tick(fixture);
            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                "the " + SaveId + " Run did not park mid-flight; journal: " +
                string.Join(" | ", Lines(fixture.Root, runId)));
            Assert.IsNull(
                PrefabContentsResources.FindEditingRoot(guid),
                "the save_contents Execute did not close the editing session");
            return runId;
        }

        // ── Plan inputs ─────────────────────────────────────────────────

        private static readonly IReadOnlyDictionary<string, string> s_prefabOut =
            new Dictionary<string, string>
            {
                ["prefab"] = "unity.PrefabAssetRef",
            };

        private static readonly IReadOnlyDictionary<string, string> s_instanceOut =
            new Dictionary<string, string>
            {
                ["instance"] = "unity.PrefabInstanceRef",
            };

        private static readonly IReadOnlyDictionary<string, string> s_variantOut =
            new Dictionary<string, string>
            {
                ["variant"] = "unity.PrefabAssetRef",
            };

        private static readonly IReadOnlyDictionary<string, string> s_rootOut =
            new Dictionary<string, string>
            {
                ["root"] = "unity.GameObjectRef",
            };

        private static readonly IReadOnlyDictionary<string, string> s_gameObjectOut =
            new Dictionary<string, string>
            {
                ["gameObject"] = "unity.GameObjectRef",
            };

        private static Dictionary<string, InputSource> PrefabInputs(
            string guid, string path) =>
            new Dictionary<string, InputSource>
            {
                ["prefab"] = new Constant(
                    PrefabContentsFixture.PrefabRef(guid, path)),
            };

        private static Dictionary<string, InputSource> CreateFromInputs() =>
            new Dictionary<string, InputSource>
            {
                ["source"] = new Constant(RunnerHostFixture.Json(
                    "{\"kind\":\"gameObject\",\"path\":\"/" + SourceObjectName + "\"}")),
                ["path"] = new Constant(RunnerHostFixture.Json(
                    "\"" + FromObjectPath + "\"")),
                ["connect"] = new Constant(RunnerHostFixture.Json("false")),
            };

        private static Dictionary<string, InputSource> VariantInputs(
            string guid, string basePath) =>
            new Dictionary<string, InputSource>
            {
                ["base"] = new Constant(
                    PrefabContentsFixture.PrefabRef(guid, basePath)),
                ["path"] = new Constant(RunnerHostFixture.Json(
                    "\"" + VariantPath + "\"")),
            };

        private static Dictionary<string, InputSource> UnpackInputs(
            GameObject instance) =>
            new Dictionary<string, InputSource>
            {
                ["instance"] = new Constant(
                    new RefResolver().ToRef(instance, "unity.PrefabInstanceRef")),
            };

        // ── SPEC §F5 across a host swap: every Reconcilable of the group ─

        // unity.prefab.create_from_gameobject: the Run wrote the Prefab —
        // then a foreign Prefab's content stands at the recorded path, the
        // one thing the §G6.1 fingerprint evidence cannot match.
        [Test]
        public void PrefabCreateFromGameObject_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var source = new GameObject(SourceObjectName);
            var runId = ParkStartedOnly(
                fixture1, PrefabCreateFromGameObjectOperation.OperationId, 'a',
                CreateFromInputs(), s_prefabOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath),
                "the first Execute did not save the Prefab");
            // An unconnected root answers "" on 6000.0 — only a non-empty
            // path would prove the source became a Prefab instance.
            Assert.IsTrue(
                string.IsNullOrEmpty(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source)),
                "connect=false must leave the source unconnected");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);
            DamageAsset(FromObjectPath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed prefab.create_from_gameobject evidence row");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath)!
                    .GetComponent<BoxCollider>(),
                "NeedsReview neither rewrites the artifact nor re-executes — " +
                "the foreign content stays");
        }

        // unity.prefab.instantiate: the new instance carries a component
        // the placement never produced — the recorded child list has its
        // one new child, but it is not the step's product any longer.
        [Test]
        public void PrefabInstantiate_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (_, path, guid) = NewProbePrefab("InstantiateBase.prefab");
            var runId = ParkStartedOnly(
                fixture1, PrefabInstantiateOperation.OperationId, 'c',
                PrefabInputs(guid, path), s_instanceOut);
            var instance = FindOuterInstance(path);
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);
            instance.AddComponent<BoxCollider>();

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed prefab.instantiate evidence row");
            Assert.IsNotNull(
                instance.GetComponent<BoxCollider>(),
                "NeedsReview leaves the foreign override — a re-execution " +
                "would have added a second instance");
            Assert.AreEqual(
                instance, FindOuterInstance(path),
                "no second instance of the Prefab appeared");
        }

        // unity.prefab.unpack: a foreign child under the unpacked root —
        // the recorded object set no longer matches what the step left.
        [Test]
        public void PrefabUnpack_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (asset, path, _) = NewProbePrefab("UnpackBase.prefab");
            var instance = NewProbeInstance(asset);
            var runId = ParkStartedOnly(
                fixture1, PrefabUnpackOperation.OperationId, 'e',
                UnpackInputs(instance), s_gameObjectOut);
            // An unconnected root answers "" on 6000.0 — a non-empty path
            // would mean the Execute left the instance still bound.
            Assert.IsTrue(
                string.IsNullOrEmpty(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance)),
                "the first Execute did not unpack the instance");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);
            var foreign = new GameObject(ForeignChildName);
            foreign.transform.SetParent(instance.transform, worldPositionStays: false);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed prefab.unpack evidence row");
            Assert.IsNotNull(
                instance.transform.Find(ForeignChildName),
                "NeedsReview neither adds nor removes — a re-execution " +
                "would have unpacked nothing that is already plain");
        }

        // unity.prefab.create_variant: the Variant file at the recorded
        // path now holds a foreign Prefab's content — the recorded expected
        // fingerprint can never match it.
        [Test]
        public void PrefabCreateVariant_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (_, basePath, guid) = NewProbePrefab("VariantBase.prefab");
            var runId = ParkStartedOnly(
                fixture1, PrefabCreateVariantOperation.OperationId, '0',
                VariantInputs(guid, basePath), s_variantOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath),
                "the first Execute did not write the Variant");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);
            DamageAsset(VariantPath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed prefab.create_variant evidence row");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath)!
                    .GetComponent<BoxCollider>(),
                "NeedsReview leaves the foreign Variant — a re-execution " +
                "would have rewritten it");
        }

        // unity.prefab.open_contents: the editing session the step opened
        // is still live and still bound to the step's acquisition scope —
        // but the truncation erased the resource_acquired record, so the
        // recovering fold holds nothing the step may claim: §F5's evidence
        // is the same step's acquisition, and an unattributed live session
        // is Unknown, never adoption.
        [Test]
        public void PrefabOpenContents_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (_, path, guid) = NewProbePrefab("Contents.prefab");
            var runId = ParkStartedOnly(
                fixture1, OpenId, '2',
                PrefabInputs(guid, path), s_rootOut);
            Assert.IsNotNull(
                PrefabContentsResources.FindEditingRoot(guid),
                "the first Execute did not open the contents");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed prefab.open_contents evidence row");
            Assert.IsNotNull(
                PrefabContentsResources.FindEditingRoot(guid),
                "NeedsReview neither re-opens nor discards — the session " +
                "the interrupted step opened is still open");
            Assert.AreEqual(
                1, PrefabContentsResources.FindEditingRoots(path).Count,
                "a re-execution would have opened a second session of the Prefab");
            PrefabContentsFixture.UnloadAllOpenedContents();
        }

        // unity.prefab.save_contents: the step saved and closed the Run's
        // contents; a foreign Prefab's content now stands at the recorded
        // path — the exact-fingerprint evidence row the §I.2 table requires
        // cannot hold, and NotApplied is not a verdict this Operation owns.
        [Test]
        public void PrefabSaveContents_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (_, path, guid) = NewProbePrefab("Save.prefab");
            var contents = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(contents, "the fixture contents must open");
            var runId = ParkSaveStartedOnly(fixture1, path, guid, contents!);
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);
            DamageAsset(path);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed prefab.save_contents evidence row");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(path)!
                    .GetComponent<BoxCollider>(),
                "NeedsReview neither restores nor re-saves — the foreign " +
                "content stays");
        }

        // ── §F5 common rule 4: the pre-existing same-name artifact ──────
        //
        // A pre-existing same-name artifact exists BEFORE Preflight, so
        // the step's recorded pre-execution snapshot contains it — the
        // row §F5 common rule 4 reserves: an artifact the snapshot
        // recorded is never the step's product. Each *Preexisting* test
        // parks a real Run, cuts its journal to started-only and recovers
        // against a world that holds the recorded artifact instead of the
        // step's product — except create_variant, whose occupied
        // destination is refused at Preflight before any step_started can
        // exist, so its row is the preflight-boundary test below. The
        // *ForeignSameName* companions are the
        // post-execution counterparts — an artifact planted after the
        // snapshot was taken, a disturbance the evidence rules must still
        // refuse, kept separate so no "pre-existing" name covers a plant
        // the snapshot never recorded.

        // unity.prefab.create_from_gameobject: the same-name artifact at
        // the destination predates the step — the declared overwrite lets
        // the step start on the occupied path, and the Preflight
        // snapshot's before-state records that artifact's own guid and
        // content fingerprint (§F5 common rule 4). With the world
        // restored to exactly that recorded pre-state the row settles
        // NotApplied — the artifact is provably the snapshot's, never the
        // step's product — and the one safe verdict re-executes over it.
        [Test]
        public void PrefabCreateFromGameObject_PreexistingArtifactResumesSafely()
        {
            var fixture1 = NewFixture();
            // The pre-existing same-name artifact, present BEFORE
            // Preflight: a Prefab whose marker component makes its
            // content unmistakable, and whose bytes the interruption
            // restores so the recovering world is exactly the recorded
            // pre-state.
            var (_, _, preexistingGuid) =
                NewProbePrefab("FromObject.prefab", marker: true);
            var preexistingFile = Path.Combine(
                ProjectRoot,
                FromObjectPath.Replace('/', Path.DirectorySeparatorChar));
            var preexistingBytes = File.ReadAllBytes(preexistingFile);
            var preexistingMeta = File.ReadAllBytes(preexistingFile + ".meta");
            new GameObject(SourceObjectName);
            var inputs = CreateFromInputs();
            inputs["overwrite"] = new Constant(RunnerHostFixture.Json("true"));
            var runId = ParkStartedOnly(
                fixture1, PrefabCreateFromGameObjectOperation.OperationId, 'a',
                inputs, s_prefabOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath),
                "the first Execute did not overwrite the Prefab");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            // The world the snapshot recorded: the pre-existing artifact
            // back at the path, byte-identical under its own guid — the
            // step's product provably absent.
            File.WriteAllBytes(preexistingFile, preexistingBytes);
            File.WriteAllBytes(preexistingFile + ".meta", preexistingMeta);
            AssetDatabase.Refresh();
            Assert.AreEqual(
                preexistingGuid, AssetDatabase.AssetPathToGUID(FromObjectPath),
                "the restore must put the recorded pre-existing artifact " +
                "back under its own guid");

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a prefab.create_from_gameobject whose path state is " +
                "exactly the recorded pre-existing artifact");
            Tick(fixture2);
            Assert.AreEqual(
                RunState.Running, fixture2.Host.Coordinator.StateOf(runId),
                "the resumed create_from_gameobject Run must park at its " +
                "hold step again");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "the NotApplied verdict re-executes the step — a decided " +
                "resume, never an adoption of the pre-existing artifact");
            Assert.AreEqual(
                preexistingGuid, AssetDatabase.AssetPathToGUID(FromObjectPath),
                "the artifact keeps the pre-existing asset's own guid — " +
                "nothing was deleted, recreated or adopted under another " +
                "identity");
            Assert.IsNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath)!
                    .GetComponent<BoxCollider>(),
                "the re-execution wrote the step's own product — the " +
                "pre-existing artifact's content was never adopted as the " +
                "step's output");
        }

        // The post-execution counterpart: the product the step journaled
        // is deleted and a foreign Prefab — a same-name artifact this
        // step never produced — is planted at the recorded path AFTER the
        // snapshot. A disturbance, never the rule-4 pre-existing
        // artifact — and still never adoptable as the step's output.
        [Test]
        public void PrefabCreateFromGameObject_ForeignSameNameArtifactBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            new GameObject(SourceObjectName);
            var runId = ParkStartedOnly(
                fixture1, PrefabCreateFromGameObjectOperation.OperationId, 'a',
                CreateFromInputs(), s_prefabOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath),
                "the first Execute did not save the Prefab");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            // The step's own file is gone; the asset at the path is a
            // different Prefab entirely — planted after the snapshot, so
            // it is a foreign disturbance, never the recorded
            // pre-existing artifact.
            Assert.IsTrue(
                AssetDatabase.DeleteAsset(FromObjectPath),
                "the first Execute's product could not be removed");
            DamageAsset(FromObjectPath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a prefab.create_from_gameobject whose path a foreign " +
                "same-name artifact occupies");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath)!
                    .GetComponent<BoxCollider>(),
                "NeedsReview leaves the foreign same-name artifact — it is " +
                "never claimed as the step's product and never rewritten");
        }

        // unity.prefab.instantiate: an instance of the same Prefab under
        // the same parent that predates the step — recorded in the
        // Preflight snapshot's own child list — is never the step's
        // product. With the step's own instance gone the child list is the
        // snapshot's again, so the row settles NotApplied and the one
        // verdict that may run again adds exactly one instance beside the
        // pre-existing artifact.
        [Test]
        public void PrefabInstantiate_PreexistingInstanceResumesSafely()
        {
            var fixture1 = NewFixture();
            var (asset, path, guid) = NewProbePrefab("InstPreBase.prefab");
            var preexisting = NewProbeInstance(asset);
            preexisting.AddComponent<BoxCollider>();
            var runId = ParkStartedOnly(
                fixture1, PrefabInstantiateOperation.OperationId, 'c',
                PrefabInputs(guid, path), s_instanceOut);
            Assert.AreEqual(
                2, OuterInstances(path).Count,
                "the first Execute did not place a second instance of the Prefab");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            var stepsInstance = OuterInstances(path).Find(i => i != preexisting);
            Assert.IsNotNull(
                stepsInstance, "the step's own instance could not be identified");
            UnityEngine.Object.DestroyImmediate(stepsInstance!);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a prefab.instantiate whose site holds only a pre-existing " +
                "instance");
            Tick(fixture2);
            Assert.AreEqual(
                RunState.Running, fixture2.Host.Coordinator.StateOf(runId),
                "the resumed instantiate Run must park at its hold step again");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "the NotApplied verdict re-executes the step — a decided " +
                "resume, never an adoption of the pre-existing artifact");
            Assert.AreEqual(
                2, OuterInstances(path).Count,
                "the re-execution added exactly one instance beside the " +
                "pre-existing one");
            Assert.IsNotNull(
                preexisting.GetComponent<BoxCollider>(),
                "the pre-existing instance is untouched — it was never the " +
                "step's product");
        }

        // unity.prefab.unpack: a plain GameObject carrying the product's
        // name under the same root — created BEFORE the step ran, so the
        // world the Preflight snapshot recorded already held it — is the
        // §F5 common rule 4 pre-existing same-name artifact. With the
        // step's own detached object gone, the recorded target's session
        // handle resolves to nothing, so the row is Unknown and
        // NeedsReview: the pre-existing object is never adopted, never
        // touched and never duplicated.
        [Test]
        public void PrefabUnpack_PreexistingObjectBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (asset, path, _) = NewProbePrefab("UnpackPreBase.prefab");
            var instance = NewProbeInstance(asset);
            var baseScene = instance.scene;
            var name = instance.name;
            // The pre-existing same-name artifact — a plain object of the
            // product's name at the same root, already in the scene when
            // the step's Preflight records its snapshot.
            var preexisting = new GameObject(name);
            SceneManager.MoveGameObjectToScene(preexisting, baseScene);
            var runId = ParkStartedOnly(
                fixture1, PrefabUnpackOperation.OperationId, 'e',
                UnpackInputs(instance), s_gameObjectOut);
            Assert.IsTrue(
                string.IsNullOrEmpty(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance)),
                "the first Execute did not unpack the instance");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            UnityEngine.Object.DestroyImmediate(instance);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a prefab.unpack whose recorded target is gone and only " +
                "the pre-existing same-name object remains");
            Assert.IsTrue(
                preexisting != null &&
                preexisting.transform.parent == null &&
                preexisting.scene.handle == baseScene.handle &&
                preexisting.name == name,
                "NeedsReview leaves the pre-existing same-name object " +
                "exactly as it stood — it is never adopted as the step's " +
                "product");
            Assert.IsTrue(
                string.IsNullOrEmpty(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(preexisting)),
                "the pre-existing object is no Prefab instance — " +
                "NeedsReview changed nothing");
            Assert.AreEqual(
                1,
                baseScene.GetRootGameObjects()
                    .Count(root => root.name == name),
                "a re-execution or an adoption would have left more than " +
                "one object of the product's name at the site");
        }

        // The post-execution counterpart: the step's detached object is
        // destroyed and a foreign plain GameObject carries its name at
        // the same root — planted AFTER the snapshot, so the recorded
        // evidence never saw it: Unknown, NeedsReview, foreign object
        // stays.
        [Test]
        public void PrefabUnpack_ForeignSameNameObjectBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (asset, path, _) = NewProbePrefab("UnpackForeignBase.prefab");
            var instance = NewProbeInstance(asset);
            var runId = ParkStartedOnly(
                fixture1, PrefabUnpackOperation.OperationId, 'e',
                UnpackInputs(instance), s_gameObjectOut);
            Assert.IsTrue(
                string.IsNullOrEmpty(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance)),
                "the first Execute did not unpack the instance");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            var baseScene = instance.scene;
            var name = instance.name;
            DisposeFixture(fixture1);
            UnityEngine.Object.DestroyImmediate(instance);
            var foreign = new GameObject(name);
            SceneManager.MoveGameObjectToScene(foreign, baseScene);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a prefab.unpack whose site holds a foreign same-name object");
            Assert.IsTrue(
                foreign != null &&
                foreign.transform.parent == null &&
                foreign.scene.handle == baseScene.handle,
                "NeedsReview leaves the foreign same-name object at the " +
                "base scene root");
            Assert.IsTrue(
                string.IsNullOrEmpty(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(foreign)),
                "the foreign object is no Prefab instance — NeedsReview " +
                "changed nothing");
        }

        // unity.prefab.create_variant: the §F5 common rule 4 boundary for
        // this Operation is Preflight itself — an artifact occupying the
        // target path BEFORE the step runs is refused by the Operation's
        // own OW-UNITY-005 ("The target asset already exists and is never
        // overwritten"), and the Run ends Failed on the journaled
        // step_failed{step:0} before any step_started could be written.
        // That is why a started-only recovery journal whose snapshot
        // records pathExisted:true is unreachable for create_variant —
        // no honest Preflight can produce it — so the row lives here at
        // the preflight seam: the refusal must leave the artifact
        // byte-for-byte intact and must never produce a Variant anywhere.
        [Test]
        public void PrefabCreateVariant_OccupiedDestinationRefusedAtPreflight()
        {
            var fixture1 = NewFixture();
            var (_, basePath, guid) = NewProbePrefab("VariantOccBase.prefab");
            // The genuine pre-existing artifact at the destination,
            // imported BEFORE Preflight can run: a marked regular Prefab
            // whose bytes, .meta and guid the refusal must not disturb.
            var (_, _, preexistingGuid) =
                NewProbePrefab("Variant.prefab", marker: true);
            var preexistingFile = Path.Combine(
                ProjectRoot,
                VariantPath.Replace('/', Path.DirectorySeparatorChar));
            var preexistingBytes = File.ReadAllBytes(preexistingFile);
            var preexistingMeta = File.ReadAllBytes(preexistingFile + ".meta");
            // The same [step -> hold] plan the recovery rows drive — the
            // hold is never reached: Preflight refuses step 0 and the Run
            // ends Failed inside the first Tick.
            var steps = new[]
            {
                Step(0, "s", PrefabCreateVariantOperation.OperationId, '0',
                    next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: VariantInputs(guid, basePath),
                    outputTypes: s_variantOut),
                Step(1, "h", HoldOp, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(
                new[]
                {
                    ("s", PrefabCreateVariantOperation.OperationId),
                    ("h", HoldOp),
                },
                ("s", "h"));
            var runId = Accept(fixture1, plan, doc);
            Tick(fixture1);

            Assert.AreEqual(
                RunState.Failed, fixture1.Host.Coordinator.StateOf(runId),
                "an occupied destination must end the Run Failed at " +
                "Preflight; journal: " +
                string.Join(" | ", Lines(StoreRoot, runId)));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId,
                    "\"type\":\"step_failed\"", "\"step\":0",
                    "\"error\":\"OW-UNITY-005\""),
                "the occupied-destination refusal is the Operation's own " +
                "OW-UNITY-005 diagnostic on step 0");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId,
                    "\"type\":\"step_skipped\"", "\"step\":1"),
                "the hold step is skipped unreached — nothing else ran");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId,
                    "\"type\":\"step_started\"", "\"step\":0"),
                "Preflight refuses before the write-ahead record — no " +
                "step_started may exist for the step");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(
                    StoreRoot, runId, "\"type\":\"run_resumed\""),
                "a Preflight refusal is never journaled run_resumed");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId,
                    "\"type\":\"step_succeeded\"", "\"step\":0"),
                "a step that never started has no output to adopt");

            var artifact = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            Assert.IsNotNull(
                artifact, "the refusal must leave the pre-existing artifact");
            Assert.AreEqual(
                preexistingGuid, AssetDatabase.AssetPathToGUID(VariantPath),
                "the artifact keeps its own guid — never deleted or " +
                "recreated under another identity");
            Assert.AreEqual(
                PrefabAssetType.Regular,
                PrefabUtility.GetPrefabAssetType(artifact!),
                "the pre-existing Prefab is still a regular Prefab — no " +
                "Variant was written at the path");
            Assert.IsNotNull(
                artifact!.GetComponent<BoxCollider>(),
                "the pre-existing artifact's own marker content survives");
            Assert.IsTrue(
                preexistingBytes.SequenceEqual(
                    File.ReadAllBytes(preexistingFile)),
                "the pre-existing artifact is byte-for-byte identical");
            Assert.IsTrue(
                preexistingMeta.SequenceEqual(
                    File.ReadAllBytes(preexistingFile + ".meta")),
                "the pre-existing artifact's .meta is byte-for-byte identical");

            var prefabGuids =
                AssetDatabase.FindAssets("t:Prefab", new[] { ProbeFolder });
            Assert.AreEqual(
                2, prefabGuids.Length,
                "only the base and the pre-existing Prefab may exist under " +
                "the probe folder — no Variant was created elsewhere");
            foreach (var prefabGuid in prefabGuids)
            {
                Assert.AreNotEqual(
                    PrefabAssetType.Variant,
                    PrefabUtility.GetPrefabAssetType(
                        AssetDatabase.LoadAssetAtPath<GameObject>(
                            AssetDatabase.GUIDToAssetPath(prefabGuid))),
                    "no Prefab Variant exists under " + ProbeFolder);
            }
        }

        // The post-execution counterpart: the step's Variant is deleted
        // and a plain foreign Prefab — never this step's product — is
        // planted at the recorded path AFTER the snapshot. A disturbance,
        // never the rule-4 pre-existing artifact — and a non-Variant at
        // the path is never the artifact this step may adopt.
        [Test]
        public void PrefabCreateVariant_ForeignSameNameArtifactBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (_, basePath, guid) = NewProbePrefab("VariantForeignBase.prefab");
            var runId = ParkStartedOnly(
                fixture1, PrefabCreateVariantOperation.OperationId, '0',
                VariantInputs(guid, basePath), s_variantOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath),
                "the first Execute did not write the Variant");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            Assert.IsTrue(
                AssetDatabase.DeleteAsset(VariantPath),
                "the first Execute's Variant could not be removed");
            DamageAsset(VariantPath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a prefab.create_variant whose path a foreign same-name " +
                "artifact occupies");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath)!
                    .GetComponent<BoxCollider>(),
                "NeedsReview leaves the foreign artifact — a plain Prefab " +
                "at the path is never this step's Variant");
        }

        // unity.prefab.open_contents: a contents session of the same
        // Prefab that predates the step — the pre-existing same-name
        // artifact — plus the session the step itself opened leave two
        // unattributed roots after the truncation: the step's own session
        // cannot be told from the pre-existing one, so the row is Unknown
        // and NeedsReview touches neither.
        [Test]
        public void PrefabOpenContents_PreexistingSessionBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (_, path, guid) = NewProbePrefab("ContentsPre.prefab");
            var preexisting = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(
                preexisting, "the pre-existing contents session must open");
            var runId = ParkStartedOnly(
                fixture1, OpenId, '2',
                PrefabInputs(guid, path), s_rootOut);
            Assert.AreEqual(
                2, PrefabContentsResources.FindEditingRoots(path).Count,
                "the step's own session did not open beside the " +
                "pre-existing one");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a prefab.open_contents whose evidence cannot tell the " +
                "step's session from a pre-existing one");
            Assert.AreEqual(
                2, PrefabContentsResources.FindEditingRoots(path).Count,
                "NeedsReview neither unloads the pre-existing session nor " +
                "opens a third — a re-execution or a release would have " +
                "changed the count");
            PrefabContentsFixture.UnloadAllOpenedContents();
        }

        // unity.prefab.save_contents: the destination already held exactly
        // the saved content when the step ran — the unmodified contents
        // write is indistinguishable from a destination that never
        // changed, so the write is unproven: Unknown, NeedsReview, and the
        // file keeps its own marker.
        [Test]
        public void PrefabSaveContents_PreexistingContentBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (_, path, guid) = NewProbePrefab("SavePre.prefab", marker: true);
            var contents = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(contents, "the fixture contents must open");
            var runId = ParkSaveStartedOnly(fixture1, path, guid, contents!);
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a prefab.save_contents whose destination already held the " +
                "saved content");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(path)!
                    .GetComponent<BoxCollider>(),
                "NeedsReview neither rewrites nor restores — the " +
                "destination's own content stays");
        }

        // ── §F5 across an Editor-session boundary ───────────────────────

        // unity.prefab.create_from_gameobject: the journaled evidence was
        // recorded in an Editor session a real restart would have ended —
        // but the connect=false row's verdict is the artifact's own
        // PrefabContentFingerprint, identity a session boundary cannot
        // lose: Completed, adopted, resumed.
        [Test]
        public void PrefabCreateFromGameObject_RestartedSessionResumes()
        {
            var fixture1 = NewFixture();
            new GameObject(SourceObjectName);
            var runId = ParkStartedOnly(
                fixture1, PrefabCreateFromGameObjectOperation.OperationId, 'a',
                CreateFromInputs(), s_prefabOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath),
                "the first Execute did not save the Prefab");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a prefab.create_from_gameobject whose snapshot outlived " +
                "its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the file-fingerprint Completed adopts the step's outputs");
            var artifact = AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath);
            Assert.IsNotNull(artifact, "the artifact is still importable");
            Assert.AreEqual(
                PrefabAssetType.Regular,
                PrefabUtility.GetPrefabAssetType(artifact!),
                "the artifact is exactly what the step saved — nothing re-ran");
        }

        // unity.prefab.instantiate: the row's child-list evidence is a
        // session-handle set — a boundary the recorded handles cannot
        // survive — so the instance at the site is unattributable:
        // Unknown, NeedsReview, and never a re-execution that adds a
        // second instance.
        [Test]
        public void PrefabInstantiate_RestartedSessionBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (_, path, guid) = NewProbePrefab("InstResBase.prefab");
            var runId = ParkStartedOnly(
                fixture1, PrefabInstantiateOperation.OperationId, 'c',
                PrefabInputs(guid, path), s_instanceOut);
            Assert.AreEqual(
                1, OuterInstances(path).Count,
                "the first Execute did not place the instance");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a prefab.instantiate whose snapshot outlived its session");
            Assert.AreEqual(
                1, OuterInstances(path).Count,
                "NeedsReview neither removes the unattributable instance " +
                "nor re-executes into a second one");
        }

        // unity.prefab.unpack: the row's session handles cannot be
        // re-established across an Editor restart — the detached object at
        // the site is unattributable, so Unknown, NeedsReview, and the
        // object stays exactly as the interruption left it.
        [Test]
        public void PrefabUnpack_RestartedSessionBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var (asset, path, _) = NewProbePrefab("UnpackResBase.prefab");
            var instance = NewProbeInstance(asset);
            var runId = ParkStartedOnly(
                fixture1, PrefabUnpackOperation.OperationId, 'e',
                UnpackInputs(instance), s_gameObjectOut);
            Assert.IsTrue(
                string.IsNullOrEmpty(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance)),
                "the first Execute did not unpack the instance");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a prefab.unpack whose snapshot outlived its session");
            Assert.IsTrue(
                instance != null &&
                instance.transform.parent == null &&
                string.IsNullOrEmpty(
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance)),
                "NeedsReview leaves the unattributable detached object — " +
                "no re-execution and no reconnection");
        }

        // unity.prefab.create_variant: the Variant's fingerprint evidence
        // is file identity — a session boundary cannot lose it — so the
        // restarted-session verdict is Completed and the resumed Run
        // adopts the step's outputs.
        [Test]
        public void PrefabCreateVariant_RestartedSessionResumes()
        {
            var fixture1 = NewFixture();
            var (_, basePath, guid) = NewProbePrefab("VariantResBase.prefab");
            var runId = ParkStartedOnly(
                fixture1, PrefabCreateVariantOperation.OperationId, '0',
                VariantInputs(guid, basePath), s_variantOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath),
                "the first Execute did not write the Variant");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a prefab.create_variant whose snapshot outlived its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the Variant's fingerprint evidence survives the session " +
                "boundary");
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            Assert.IsNotNull(variant, "the Variant asset is still importable");
            Assert.AreEqual(
                PrefabAssetType.Variant,
                PrefabUtility.GetPrefabAssetType(variant!),
                "the artifact is exactly what the step saved — nothing re-ran");
        }

        // unity.prefab.open_contents: an Editor restart discards the
        // preview scene along with the ownership binding — the step's
        // product provably no longer exists. With the truncated journal's
        // absent resource record the row settles NotApplied — the one
        // verdict that may safely run again — and the re-execution opens
        // exactly one fresh session.
        [Test]
        public void PrefabOpenContents_RestartedSessionReexecutes()
        {
            var fixture1 = NewFixture();
            var (_, path, guid) = NewProbePrefab("ContentsRes.prefab");
            var runId = ParkStartedOnly(
                fixture1, OpenId, '2',
                PrefabInputs(guid, path), s_rootOut);
            Assert.IsNotNull(
                PrefabContentsResources.FindEditingRoot(guid),
                "the first Execute did not open the contents");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            // The restart's physical half: no preview scene survives an
            // Editor restart, and no ownership binding with it.
            PrefabContentsFixture.UnloadAllOpenedContents();

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a prefab.open_contents whose session a restart ended");
            Tick(fixture2);
            Assert.AreEqual(
                RunState.Running, fixture2.Host.Coordinator.StateOf(runId),
                "the resumed open_contents Run must park at its hold step again");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "the NotApplied verdict re-executes the step — it never " +
                "adopts a session the restart destroyed");
            Assert.AreEqual(
                1, PrefabContentsResources.FindEditingRoots(path).Count,
                "the re-execution opened exactly one fresh contents session");
            PrefabContentsFixture.UnloadAllOpenedContents();
        }

        // unity.prefab.save_contents: the save row records no session
        // member — the destination's fingerprint is the identity a restart
        // cannot lose — so the restarted-session verdict is Completed and
        // the resumed Run adopts the step's outputs. The contents edit is
        // what keeps this off the pre-existing-content row: a destination
        // that already held exactly this content judges Unknown instead.
        [Test]
        public void PrefabSaveContents_RestartedSessionResumes()
        {
            var fixture1 = NewFixture();
            var (_, path, guid) = NewProbePrefab("SaveRes.prefab");
            var contents = PrefabUtility.LoadPrefabContents(path);
            Assert.IsNotNull(contents, "the fixture contents must open");
            var edited = new GameObject(EditedChildName);
            edited.transform.SetParent(
                contents!.transform, worldPositionStays: false);
            var runId = ParkSaveStartedOnly(fixture1, path, guid, contents!);
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a prefab.save_contents whose snapshot outlived its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the destination's fingerprint survives the session boundary");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(path)!
                    .transform.Find(EditedChildName),
                "the file holds exactly the content the step saved — " +
                "nothing re-ran");
        }

        // ── SPEC §F5 across a real domain reload ────────────────────────

        // The SPEC §T.3 reload case the AT-02 check requires: all six
        // Reconcilable Operations of the group, each parked started-only
        // with its evidence disturbed, must still be decided Unknown and
        // end NeedsReview in the domain the compile creates — never an
        // automatic success and never an automatic re-execution (§F0).
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator StartedOnlyDisturbedBecomesNeedsReviewAcrossTheRealReload()
        {
            var before = StartedOnlyGroupPhase();
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True,
                "pre-reload phase failed: " + before.Failure);

            // T-W2.4-07 stale-resume self-heal: a predecessor's captured
            // UTF resume PC applies to this fresh enumerator and lands it
            // at one of the two post-yield states inside this loop — with
            // every local (leg included) reset to default. Both
            // continuations drain, then MUST pass the this-session probe
            // check: a probe phase-1 never wrote re-drives a real phase-1
            // + compile + reload instead of skipping silently into
            // phase-2. If the probe still cannot be produced after the
            // bound, the loop exits and phase-2's nonce validation fails
            // loudly (the ChangeSetDomainReloadTests pattern).
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the probe compile did not produce a domain reload");

                if (ProbeStateIsLive() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // The marker file an abandoned leg left behind carries
                // identical bytes, so deleting the probe assets
                // guarantees the re-write queues a real compile rather
                // than a no-op Refresh.
                CleanupArtifacts();
                var redrive = StartedOnlyGroupPhase();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = StartedOnlyGroupPhaseAfter();
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True,
                "post-reload phase failed: " + after.Failure);
        }

        // One phase-1 host on a Run Store of its own — never installed,
        // so no editor-update tick can pump its parked hold past the
        // truncation point, and never disposed on the pass path: the OS
        // exclusion its parked Run holds is the process-held handle the
        // hand-off seam carries across the domain reload.
        private static RunnerHostFixture GroupFixture(
            List<RunnerHostFixture> fixtures, int index)
        {
            var fixture = new RunnerHostFixture(StoreRootFor(s_groupKeys[index]));
            fixtures.Add(fixture);
            RegisterAll(fixture);
            return fixture;
        }

        private static void DisposeFixtures(List<RunnerHostFixture> fixtures)
        {
            for (var i = 0; i < fixtures.Count; i++)
            {
                fixtures[i].Dispose();
            }
            fixtures.Clear();
        }

        // Phase 1: each [op -> hold] Run — one per Run Store, the SPEC
        // §E2 single-mutating-Run shape — executes step 0 and waits on
        // the hold; the evidence row it journaled is disturbed in the
        // world, and every journal is cut back to the bare
        // step_started{step:0} a real interruption would have left.
        private static PhaseResult StartedOnlyGroupPhase()
        {
            var fixtures = new List<RunnerHostFixture>();
            var roots = new List<GameObject>();
            try
            {
                var runIds = new string[s_groupKeys.Length];

                var source = new GameObject(SourceObjectName);
                roots.Add(source);
                var runCreate = ParkStartedOnly(
                    GroupFixture(fixtures, 0),
                    PrefabCreateFromGameObjectOperation.OperationId, 'a',
                    CreateFromInputs(), s_prefabOut);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath) == null)
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the create_from_gameobject Execute did not save the Prefab");
                }
                DamageAsset(FromObjectPath);
                runIds[0] = runCreate;

                var (_, instPath, instGuid) = NewProbePrefab("InstBase.prefab");
                var runInst = ParkStartedOnly(
                    GroupFixture(fixtures, 1),
                    PrefabInstantiateOperation.OperationId, 'c',
                    PrefabInputs(instGuid, instPath), s_instanceOut);
                var newInstance = FindOuterInstance(instPath);
                newInstance.AddComponent<BoxCollider>();
                runIds[1] = runInst;

                var (unpackAsset, unpackPath, _) = NewProbePrefab("UnpackBase.prefab");
                var unpackInstance = NewProbeInstance(unpackAsset);
                var runUnpack = ParkStartedOnly(
                    GroupFixture(fixtures, 2),
                    PrefabUnpackOperation.OperationId, 'e',
                    UnpackInputs(unpackInstance), s_gameObjectOut);
                if (!string.IsNullOrEmpty(
                        PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(
                            unpackInstance)))
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the unpack Execute did not detach the instance");
                }
                var foreignChild = new GameObject(ForeignChildName);
                foreignChild.transform.SetParent(
                    unpackInstance.transform, worldPositionStays: false);
                runIds[2] = runUnpack;

                var (_, variantBase, variantGuid) = NewProbePrefab("VariantBase.prefab");
                var runVariant = ParkStartedOnly(
                    GroupFixture(fixtures, 3),
                    PrefabCreateVariantOperation.OperationId, '0',
                    VariantInputs(variantGuid, variantBase), s_variantOut);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath) == null)
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the create_variant Execute did not write the Variant");
                }
                DamageAsset(VariantPath);
                runIds[3] = runVariant;

                var (_, openPath, openGuid) = NewProbePrefab("Contents.prefab");
                var runOpen = ParkStartedOnly(
                    GroupFixture(fixtures, 4), OpenId, '2',
                    PrefabInputs(openGuid, openPath), s_rootOut);
                if (PrefabContentsResources.FindEditingRoot(openGuid) == null)
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the open_contents Execute did not open the contents");
                }
                runIds[4] = runOpen;

                var (_, savePath, saveGuid) = NewProbePrefab("Save.prefab");
                var contents = PrefabUtility.LoadPrefabContents(savePath);
                if (contents == null)
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail("the fixture contents did not open");
                }
                var runSave = ParkSaveStartedOnly(
                    GroupFixture(fixtures, 5), savePath, saveGuid, contents);
                DamageAsset(savePath);
                runIds[5] = runSave;

                // The parked holds need no ticks stopped: a phase-1 host
                // is never installed, so nothing pumps a step record past
                // the truncated tail — each journal is cut back to the
                // bare step_started{step:0} in its own store.
                for (var i = 0; i < fixtures.Count; i++)
                {
                    TruncateThrough(
                        fixtures[i].Root, runIds[i],
                        "\"type\":\"step_started\"", "\"step\":0");
                    AssertStartedOnlyTail(fixtures[i].Root, runIds[i], 0);
                }
                WriteState(runIds);
                ArmReload();
                Crumb("phase-1 complete: six disturbed started-only runs, " +
                    "create=" + runCreate + " instantiate=" + runInst +
                    " unpack=" + runUnpack + " variant=" + runVariant +
                    " open=" + runOpen + " save=" + runSave);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2: same Editor session (SessionState survives the reload),
        // same live objects — a fresh-domain §F4 pass per Run Store reads
        // one started-only journal and one disturbed world: the surviving
        // OS handle re-adopts, the exclusion passes to the Run lock.json
        // names, and each verdict is Unknown — NeedsReview, no
        // run_resumed, no re-execution — with every disturbed artifact
        // exactly as the disturbance left it.
        private static PhaseResult StartedOnlyGroupPhaseAfter()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                var persisted = ReadState();
                Assert.AreEqual(
                    s_groupKeys.Length, persisted.Length,
                    "the phase-1 state file lost a run id");

                for (var i = 0; i < s_groupKeys.Length; i++)
                {
                    var fixture = new RunnerHostFixture(
                        StoreRootFor(s_groupKeys[i]));
                    fixtures.Add(fixture);
                    RegisterAll(fixture);
                    fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                    AssertStartedOnlyTail(fixture.Root, persisted[i], 0);
                }
                var names = new[]
                {
                    "prefab.create_from_gameobject", "prefab.instantiate",
                    "prefab.unpack", "prefab.create_variant",
                    "prefab.open_contents", "prefab.save_contents",
                };
                for (var i = 0; i < fixtures.Count; i++)
                {
                    var report = Recover(fixtures[i]);
                    Assert.IsTrue(
                        report.ExclusionHeld,
                        "the " + s_groupKeys[i] +
                            " store's recovery pass could not take its exclusion");
                    AssertNeedsReview(
                        fixtures[i].Root, fixtures[i], persisted[i],
                        "a disturbed " + names[i] + " evidence row across a reload");
                }

                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<GameObject>(FromObjectPath)!
                        .GetComponent<BoxCollider>(),
                    "create_from_gameobject: NeedsReview leaves the foreign artifact");
                var instance = GameObject.Find("InstBase");
                Assert.IsNotNull(instance,
                    "the instantiate artifact did not survive the reload");
                Assert.IsNotNull(
                    instance!.GetComponent<BoxCollider>(),
                    "instantiate: NeedsReview leaves the foreign override — " +
                    "a re-execution would have added a second instance");
                var unpacked = GameObject.Find("UnpackBase");
                Assert.IsNotNull(unpacked,
                    "the unpack artifact did not survive the reload");
                Assert.IsNotNull(
                    unpacked!.transform.Find(ForeignChildName),
                    "unpack: NeedsReview neither adds nor removes the foreign child");
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath)!
                        .GetComponent<BoxCollider>(),
                    "create_variant: NeedsReview leaves the foreign Variant");
                var contentsGuid = AssetDatabase.AssetPathToGUID(
                    ProbeFolder + "/Contents.prefab");
                Assert.IsNotNull(
                    PrefabContentsResources.FindEditingRoot(contentsGuid),
                    "open_contents: the unattributed session must survive the reload — " +
                    "a Run-end release for a step that never acquired it would " +
                    "have closed it");
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<GameObject>(
                        ProbeFolder + "/Save.prefab")!.GetComponent<BoxCollider>(),
                    "save_contents: NeedsReview leaves the foreign save target");
                DisposeFixtures(fixtures);
                Crumb("phase-2 complete: six NeedsReview verdicts");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
