#nullable enable

// T-W4.5-04 [Edit] tests: local:T-W4.5-04:at02-compile-package — the
// Runner half of the SPEC §F5 contract for the four Reconcilable
// Operations of the §I.4 Compile/Package group
// (unity.script.create_from_template, unity.script.create,
// unity.package.add, unity.package.remove): a Run parked mid-flight and
// cut back to the bare step_started{step:0} — its Execute effect issued,
// its recorded evidence then disturbed — must be decided Unknown and end
// NeedsReview on the §F4 recovery pass, never an automatic success and
// never an automatic re-execution (SPEC §F5 rules 1-6, §F0). The script
// Operations' evidence is file identity a session boundary cannot lose,
// so their Editor-restart row completes the six: the restarted verdict
// is Completed and the resumed Run adopts the step's outputs, never
// re-runs. The group's three Waitables get the waiting-step half: a Run
// parked at its step_waiting with the persisted resume token resumes the
// wait on recovery and settles through Poll, never re-executing the
// issued effect. The same-domain tests drive the real OpWeaveRunnerHost
// + FileRunStore path the T-W4.3-03 fixtures established; the
// [UnityTest] companion runs the identical contract across a real
// domain reload (SPEC §T.3), where every started-only disturbance in
// the group must still end NeedsReview one domain later.
//
// The journal surgery and the park/recover mechanics mirror
// At02AssetRecoveryTests: [step -> hold] plans on the real Operations,
// TruncateThrough to the started-only tail, and the marker-script +
// domain-sentinel reload of the reload companion. SPEC §E2 admits at
// most one mutating Run per Run Store, so the reload companion parks
// each Operation's Run in a store of its own and never disposes a
// phase-1 host: the OS exclusion each parked Run holds is a
// process-held handle that survives the domain reload, the hand-off
// seam of SPEC §E2 item 3 the post-reload pass re-adopts.
//
// The package Operations never touch UPM in this suite: their
// constructors' package-state seams answer with a deterministic
// PackageInstallView — the pre-state for phase 1 and the damaged state
// for the recovery judgement — so the §F5 rows are proven without a
// real manifest edit or a real Client request, exactly the shape
// PackageAddOperationTests established.

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
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.5-04:at02-compile-package</c> recovery cases: every
    /// Reconcilable Operation of the SPEC §I.4 Compile/Package group,
    /// disturbed so its §F5 evidence row no longer holds, ends
    /// NeedsReview — in the same domain and across a real domain reload.
    /// </summary>
    internal sealed class At02CompilePackageRecoveryTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAt02CompilePkgRecovery");

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
        private const string ProbeSessionKey = "OpWeave.At02CompilePkg.probe-state";

        private static string NonceFile => Path.Combine(ProbeDir, "probe.nonce");

        // The reload group's per-Operation Run Store: four started-only
        // Runs unfinished at once need a store each — SPEC §E2 admits at
        // most one mutating Run per store, so a second accept on one
        // coordinator fails OW-RUN-002, and the §F4 recovery pass drives
        // only the single Run the exclusion passes to.
        private static string StoreRootFor(string key) =>
            Path.Combine(ProbeDir, "run-root-" + key);

        // The group stores in phase order — WriteState persists the run
        // ids in this order and the post-reload phase walks them the
        // same way.
        private static readonly string[] s_groupKeys =
            { "template", "script", "pkgadd", "pkgremove" };

        private static IEnumerable<string> AllStoreRoots()
        {
            yield return StoreRoot;
            for (var i = 0; i < s_groupKeys.Length; i++)
            {
                yield return StoreRootFor(s_groupKeys[i]);
            }
        }

        // The script probes live under the default codePaths entry —
        // the standard implementations' own path policy accepts them.
        private const string ProbeFolder = "Assets/Scripts/OpWeaveAt02Recovery";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";
        private const string TemplateClassName = "At02TemplateProbe";
        private const string TemplateResClassName = "At02TemplateResProbe";
        private const string ScriptClassName = "At02ScriptProbe";
        private const string ScriptResClassName = "At02ScriptResProbe";
        // Every damaged .cs needs a class name of its own — the armed
        // reload compiles the probes together, and two files defining
        // one class name is a CS0101 the test would manufacture itself.
        private const string DamagedClassName = "At02DamagedForeignScript";
        private const string DamagedClassName2 = "At02DamagedForeignScript2";

        private const string PackageName = "com.unity.at02probe";
        private const string PackageVersion = "1.2.3";
        private const string PackageId = PackageName + "@" + PackageVersion;

        private static string MarkerFolder => "Assets/Editor/OpWeaveAt02CompilePkgReload";
        private static string MarkerDisk =>
            Path.Combine(
                ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveAt02CompilePkgReload\n" +
            "{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private const string WorkflowId = "example.at02.compilepkg.recovery";
        private const string HoldOp = "test.at02.compilepkg.resume.hold";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // T-W2.4-07: bound on phase-1 re-drive legs after a predecessor's
        // stale resume is detected. leg is a method local, so a real
        // reload resets it; the live-nonce probe check is what actually
        // ends the loop (the ChangeSetDomainReloadTests pattern).
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) => Debug.Log("[W4504] " + message);

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

        // A saved, clean base scene: the Runner's scene checks need no
        // scene content, only a clean loaded scene.
        private static void NewBaseScene()
        {
            EnsureFolder(ProbeFolder);
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
            var probeDisk = PhysicalPath(ProbeFolder);
            if (Directory.Exists(probeDisk))
            {
                try { Directory.Delete(probeDisk, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
                dirty = true;
            }
            if (File.Exists(probeDisk + ".meta"))
            {
                try { File.Delete(probeDisk + ".meta"); } catch (Exception) { }
            }
            // Assets/Scripts itself is a normal project folder — created
            // when absent but never deleted by a test.
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
            if (File.Exists(MarkerDisk + ".meta"))
            {
                File.Delete(MarkerDisk + ".meta");
                dirty = true;
            }
            return dirty;
        }

        // ── Fixtures, plans, accept/tick/recover ────────────────────────

        private RunnerHostFixture NewFixture(
            PackageInstallView addView, PackageInstallView removeView)
        {
            var fixture = new RunnerHostFixture(StoreRoot);
            _fixtures.Add(fixture);
            RegisterAll(fixture, addView, removeView);
            return fixture;
        }

        private void DisposeFixture(RunnerHostFixture fixture)
        {
            fixture.Dispose();
            _fixtures.Remove(fixture);
        }

        // Every Operation of the group at the seeds the parked plans'
        // fingerprints were built with — a fresh host must resolve each
        // step's implementation after a host swap or a domain reload.
        // The package Operations' UPM reads and UPM wait are the seam
        // delegates the constructors expose: a deterministic
        // PackageInstallView per fixture, a token-only issue call and an
        // always-settled wait condition, so no test needs a real
        // Client.Add/Remove or a real manifest edit — the §F5 judgement
        // runs on the same evidence code either way.
        private static void RegisterAll(
            RunnerHostFixture fixture,
            PackageInstallView addView,
            PackageInstallView removeView)
        {
            RegisterAs(
                fixture, new CreateScriptFromTemplateOperation(), 'a',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new CreateScriptOperation(), 'c',
                OperationOrigin.Standard);
            RegisterAs(
                fixture,
                new CompileScriptsOperation(
                    static () => { }, new DoneCondition()),
                'e', OperationOrigin.Standard);
            RegisterAs(
                fixture,
                new UnityPackageAddOperation(
                    static () => Result<OpWeaveSettings>.Success(
                        OpWeaveSettings.Default),
                    static id => PackageResolvedCondition.InitialToken(
                        id, expectPresent: true),
                    new DoneCondition(),
                    () => addView),
                '0', OperationOrigin.Standard);
            RegisterAs(
                fixture,
                new UnityPackageRemoveOperation(
                    static name => PackageResolvedCondition.InitialToken(
                        name, expectPresent: false),
                    new DoneCondition(),
                    () => removeView),
                '2', OperationOrigin.Standard);
            fixture.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), '4');
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

        // ── Script and package fixtures ─────────────────────────────────

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(
                ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        private static void EnsureFolder(string folderPath)
        {
            var segments = folderPath.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    Assert.That(AssetDatabase.CreateFolder(parent, segments[i]),
                        Is.Not.Empty, "the probe folder must be created: " + child);
                }
                parent = child;
            }
        }

        // Valid C# for every write the suite makes: an imported .cs sits
        // in a deferred compile the armed reload (or the next editor
        // tick) will run, so every byte on disk must compile cleanly.
        private static string ProbeScriptSource(string className) =>
            "public static class " + className + "\n" +
            "{\n" +
            "    public const int Value = 0;\n" +
            "}\n";

        // The disturbed-artifact write a .cs gets: different valid C#
        // over the path Execute wrote — content the recorded expected
        // hash can never match, so the §F5 check settles Unknown instead
        // of the Completed a stale evidence row would have produced.
        private static void DamageScript(
            string assetPath, string damagedClassName = DamagedClassName)
        {
            File.WriteAllText(
                PhysicalPath(assetPath), ProbeScriptSource(damagedClassName));
            AssetDatabase.Refresh();
        }

        // The pre-state every parked package.add sees — all three
        // channels read, the target absent from every one — and the
        // partial state the recovery judgement reads: the manifest entry
        // alone landed, so the §F5 verdict is Unknown only.
        private static PackageInstallView AbsentView() =>
            new PackageInstallView(
                new JObject(), new JObject(), Array.Empty<ResolvedPackageView>());

        private static PackageInstallView InstalledView() =>
            new PackageInstallView(
                new JObject { [PackageName] = PackageVersion },
                new JObject
                {
                    [PackageName] = new JObject
                    {
                        ["version"] = PackageVersion,
                        ["source"] = "registry",
                    },
                },
                new[]
                {
                    new ResolvedPackageView(
                        PackageName, PackageVersion, "registry",
                        PackageVersion, null, null),
                });

        private static PackageInstallView PartialAddView() =>
            new PackageInstallView(
                new JObject { [PackageName] = PackageVersion },
                new JObject(), Array.Empty<ResolvedPackageView>());

        private static PackageInstallView PartialRemoveView() =>
            new PackageInstallView(
                new JObject(), new JObject(),
                new[]
                {
                    new ResolvedPackageView(
                        PackageName, PackageVersion, "registry",
                        PackageVersion, null, null),
                });

        // The always-settled wait condition — a resumed package or
        // compile wait settles in one Poll.
        private sealed class DoneCondition : IWaitCondition
        {
            public WaitStatus Check(JsonValue token) => WaitStatus.Done();
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
            IReadOnlyDictionary<string, string>? outputTypes = null)
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

        // The group's Waitables' own park: the Waitable Execute returns
        // Waiting on the first tick, so the Run's journal carries the live
        // step_waiting{step:0} a suspended Run would — the record a
        // recovery pass resumes the wait from, never a truncated one.
        private static string ParkWaiting(
            RunnerHostFixture fixture,
            string opId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource> inputs,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            Idempotency idempotency = Idempotency.Reconcilable)
        {
            var steps = new[]
            {
                Step(0, "s", opId, hashSeed, next: 1,
                    idempotency: idempotency,
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
                "the " + opId + " Run did not park at its wait; " +
                "journal: " + string.Join(" | ", Lines(fixture.Root, runId)));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    fixture.Root, runId, "\"type\":\"step_waiting\"", "\"step\":0"),
                "the " + opId + " Run must journal step_waiting{step:0} with " +
                "the persisted resume token");
            return runId;
        }

        // ── Plan inputs ─────────────────────────────────────────────────

        private static readonly IReadOnlyDictionary<string, string> s_templateOut =
            new Dictionary<string, string>
            {
                ["script"] = "unity.AssetRef<unity.MonoScript>",
                ["typeName"] = "unity.TypeName",
            };

        private static readonly IReadOnlyDictionary<string, string> s_scriptOut =
            new Dictionary<string, string>
            {
                ["script"] = "unity.AssetRef<unity.MonoScript>",
            };

        private static readonly IReadOnlyDictionary<string, string> s_packageOut =
            new Dictionary<string, string>
            {
                ["package"] = "core.Json",
            };

        private static readonly IReadOnlyDictionary<string, string> s_resultOut =
            new Dictionary<string, string>
            {
                ["result"] = "core.Json",
            };

        private static Dictionary<string, InputSource> TemplateInputs(string className) =>
            new Dictionary<string, InputSource>
            {
                ["template"] = new Constant(
                    RunnerHostFixture.Json("\"mono_behaviour\"")),
                ["className"] = new Constant(
                    RunnerHostFixture.Json("\"" + className + "\"")),
                ["folder"] = new Constant(
                    RunnerHostFixture.Json("\"" + ProbeFolder + "\"")),
            };

        private static Dictionary<string, InputSource> ScriptInputs(
            string path, string className) =>
            new Dictionary<string, InputSource>
            {
                ["path"] = new Constant(RunnerHostFixture.Json("\"" + path + "\"")),
                ["content"] = new Constant(
                    JsonValue.From(new JValue(ProbeScriptSource(className)))),
            };

        private static Dictionary<string, InputSource> PackageAddInputs() =>
            new Dictionary<string, InputSource>
            {
                ["id"] = new Constant(
                    RunnerHostFixture.Json("\"" + PackageId + "\"")),
            };

        private static Dictionary<string, InputSource> PackageRemoveInputs() =>
            new Dictionary<string, InputSource>
            {
                ["name"] = new Constant(
                    RunnerHostFixture.Json("\"" + PackageName + "\"")),
            };

        private static string ScriptPath(string className) =>
            ProbeFolder + "/" + className + ".cs";

        // ── SPEC §F5 across a host swap: every Reconcilable of the group ─

        // unity.script.create_from_template: the Run wrote the .cs — then
        // foreign valid C# stands at the recorded path, the one thing the
        // expected template-content hash cannot match.
        [Test]
        public void ScriptCreateFromTemplate_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var runId = ParkStartedOnly(
                fixture1, CreateScriptFromTemplateOperation.OperationId, 'a',
                TemplateInputs(TemplateClassName), s_templateOut);
            var path = ScriptPath(TemplateClassName);
            Assert.IsTrue(
                File.Exists(PhysicalPath(path)),
                "the first Execute did not write the script");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            DamageScript(path);

            var fixture2 = NewFixture(AbsentView(), InstalledView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed script.create_from_template evidence row");
            Assert.AreEqual(
                ProbeScriptSource(DamagedClassName),
                File.ReadAllText(PhysicalPath(path)),
                "NeedsReview neither rewrites the artifact nor re-executes — " +
                "the foreign script stays");
        }

        // unity.script.create: same shape — the foreign bytes at the
        // recorded .cs path can never match the expected content, so
        // Unknown is the only honest verdict.
        [Test]
        public void ScriptCreate_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var path = ScriptPath(ScriptClassName);
            var runId = ParkStartedOnly(
                fixture1, CreateScriptOperation.OperationId, 'c',
                ScriptInputs(path, ScriptClassName), s_scriptOut);
            Assert.IsTrue(
                File.Exists(PhysicalPath(path)),
                "the first Execute did not write the script");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            DamageScript(path);

            var fixture2 = NewFixture(AbsentView(), InstalledView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed script.create evidence row");
            Assert.AreEqual(
                ProbeScriptSource(DamagedClassName),
                File.ReadAllText(PhysicalPath(path)),
                "NeedsReview leaves the foreign script — a re-execution " +
                "would have overwritten it or failed on the occupied path");
        }

        // unity.package.add: the Run issued the add — then the recorded
        // evidence row disagrees: the manifest entry landed but the
        // lockfile and the registered list never did — the partial state
        // SPEC §F5's own row names Unknown.
        [Test]
        public void PackageAdd_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var runId = ParkWaiting(
                fixture1, UnityPackageAddOperation.OperationId, '0',
                PackageAddInputs(), s_packageOut);
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);

            var fixture2 = NewFixture(PartialAddView(), InstalledView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed package.add evidence row");
        }

        // unity.package.remove: the recorded pre-state was the installed
        // package — then the manifest and lockfile entries are gone but
        // the registered list still carries the package — the §F5 row's
        // own partial-removal Unknown.
        [Test]
        public void PackageRemove_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var runId = ParkWaiting(
                fixture1, UnityPackageRemoveOperation.OperationId, '2',
                PackageRemoveInputs());
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);

            var fixture2 = NewFixture(AbsentView(), PartialRemoveView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed package.remove evidence row");
        }

        // ── The Editor-restart rows: file identity survives the session ─

        // unity.script.create_from_template: the row's evidence is the
        // artifact's expected template content — a session boundary
        // cannot lose it — so the restarted verdict is Completed and the
        // resumed Run adopts the step's outputs.
        [Test]
        public void ScriptCreateFromTemplate_RestartedSessionResumes()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var runId = ParkStartedOnly(
                fixture1, CreateScriptFromTemplateOperation.OperationId, 'a',
                TemplateInputs(TemplateResClassName), s_templateOut);
            var path = ScriptPath(TemplateResClassName);
            Assert.IsTrue(
                File.Exists(PhysicalPath(path)),
                "the first Execute did not write the script");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture(AbsentView(), InstalledView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a script.create_from_template whose snapshot outlived its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the expected-content evidence survives the session boundary");
            var text = File.ReadAllText(PhysicalPath(path));
            StringAssert.Contains(TemplateResClassName, text);
            Assert.IsFalse(
                text.Contains(DamagedClassName),
                "the artifact is exactly what the step wrote — nothing re-ran");
        }

        // unity.script.create: the recorded expected content is file
        // identity — the restarted verdict is Completed and the outputs
        // are adopted.
        [Test]
        public void ScriptCreate_RestartedSessionResumes()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var path = ScriptPath(ScriptResClassName);
            var runId = ParkStartedOnly(
                fixture1, CreateScriptOperation.OperationId, 'c',
                ScriptInputs(path, ScriptResClassName), s_scriptOut);
            Assert.IsTrue(
                File.Exists(PhysicalPath(path)),
                "the first Execute did not write the script");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture(AbsentView(), InstalledView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a script.create whose snapshot outlived its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the expected-content evidence survives the session boundary");
            Assert.AreEqual(
                ProbeScriptSource(ScriptResClassName),
                File.ReadAllText(PhysicalPath(path)),
                "the artifact is exactly what the step wrote — nothing re-ran");
        }

        // ── The waiting-step half: the persisted token resumes ──────────

        // unity.script.compile — the group's Idempotent Waitable: a Run
        // parked at its step_waiting with the persisted resume token is
        // not an interrupted step at all — recovery re-attaches the
        // suspended Run, and the next tick polls the persisted wait
        // state to its completion: the result settles through Poll,
        // never through a re-executed compile request.
        [Test]
        public void ScriptCompile_WaitingStepResumesAcrossHostSwap()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var runId = ParkWaiting(
                fixture1, CompileScriptsOperation.OperationId, 'e',
                new Dictionary<string, InputSource>(), s_resultOut,
                Idempotency.Idempotent);
            fixture1.Host.FlushJournal();
            DisposeFixture(fixture1);

            var fixture2 = NewFixture(AbsentView(), InstalledView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Tick(fixture2);
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "the resumed wait must settle step 0 through Poll; journal: " +
                string.Join(" | ", Lines(StoreRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                "a resumed wait is not a NeedsReview verdict");
            Assert.AreEqual(
                RunState.Running, fixture2.Host.Coordinator.StateOf(runId),
                "the settled compile Run parks at its hold step");
        }

        // unity.package.add: the resumed wait settles through Poll — the
        // resolved-package lookup sees the scripted installed state, the
        // step reports the package output, and no second Client.Add is
        // ever issued.
        [Test]
        public void PackageAdd_WaitingStepResumesAcrossHostSwap()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var runId = ParkWaiting(
                fixture1, UnityPackageAddOperation.OperationId, '0',
                PackageAddInputs(), s_packageOut);
            fixture1.Host.FlushJournal();
            DisposeFixture(fixture1);

            var fixture2 = NewFixture(InstalledView(), InstalledView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Tick(fixture2);
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "the resumed wait must settle step 0 through Poll; journal: " +
                string.Join(" | ", Lines(StoreRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                "a resumed wait is not a NeedsReview verdict");
            Assert.AreEqual(
                RunState.Running, fixture2.Host.Coordinator.StateOf(runId),
                "the settled add Run parks at its hold step");
        }

        // unity.package.remove: the resumed wait settles through Poll —
        // the removal's effect lands exactly once, and no second
        // Client.Remove is issued after the host swap.
        [Test]
        public void PackageRemove_WaitingStepResumesAcrossHostSwap()
        {
            var fixture1 = NewFixture(AbsentView(), InstalledView());
            var runId = ParkWaiting(
                fixture1, UnityPackageRemoveOperation.OperationId, '2',
                PackageRemoveInputs());
            fixture1.Host.FlushJournal();
            DisposeFixture(fixture1);

            var fixture2 = NewFixture(AbsentView(), AbsentView());
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Tick(fixture2);
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "the resumed wait must settle step 0 through Poll; journal: " +
                string.Join(" | ", Lines(StoreRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                "a resumed wait is not a NeedsReview verdict");
            Assert.AreEqual(
                RunState.Running, fixture2.Host.Coordinator.StateOf(runId),
                "the settled remove Run parks at its hold step");
        }

        // ── SPEC §F5 across a real domain reload ────────────────────────

        // The SPEC §T.3 reload case the AT-02 check requires: all four
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
            List<RunnerHostFixture> fixtures, int index,
            PackageInstallView addView, PackageInstallView removeView)
        {
            var fixture = new RunnerHostFixture(StoreRootFor(s_groupKeys[index]));
            fixtures.Add(fixture);
            RegisterAll(fixture, addView, removeView);
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
        // §E2 single-mutating-Run shape — issues step 0's effect and
        // parks at the hold (the Waitable package Runs park at their own
        // step_waiting); the evidence row it journaled is disturbed in
        // the world, and every journal is cut back to the bare
        // step_started{step:0} a real interruption would have left.
        private static PhaseResult StartedOnlyGroupPhase()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                var runIds = new string[s_groupKeys.Length];

                var runTemplate = ParkStartedOnly(
                    GroupFixture(fixtures, 0, AbsentView(), InstalledView()),
                    CreateScriptFromTemplateOperation.OperationId, 'a',
                    TemplateInputs(TemplateClassName), s_templateOut);
                var templatePath = ScriptPath(TemplateClassName);
                if (!File.Exists(PhysicalPath(templatePath)))
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the create_from_template Execute did not write the script");
                }
                DamageScript(templatePath, DamagedClassName);
                runIds[0] = runTemplate;

                var scriptPath = ScriptPath(ScriptClassName);
                var runScript = ParkStartedOnly(
                    GroupFixture(fixtures, 1, AbsentView(), InstalledView()),
                    CreateScriptOperation.OperationId, 'c',
                    ScriptInputs(scriptPath, ScriptClassName), s_scriptOut);
                if (!File.Exists(PhysicalPath(scriptPath)))
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the script.create Execute did not write the script");
                }
                DamageScript(scriptPath, DamagedClassName2);
                runIds[1] = runScript;

                // The package Runs' disturbance is the state the next
                // domain's view seam answers: nothing physical to change.
                var runAdd = ParkWaiting(
                    GroupFixture(fixtures, 2, AbsentView(), InstalledView()),
                    UnityPackageAddOperation.OperationId, '0',
                    PackageAddInputs(), s_packageOut);
                runIds[2] = runAdd;

                var runRemove = ParkWaiting(
                    GroupFixture(fixtures, 3, AbsentView(), InstalledView()),
                    UnityPackageRemoveOperation.OperationId, '2',
                    PackageRemoveInputs());
                runIds[3] = runRemove;

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
                Crumb("phase-1 complete: four disturbed started-only runs, " +
                    "template=" + runTemplate + " script=" + runScript +
                    " add=" + runAdd + " remove=" + runRemove);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2: same Editor session, same live objects — a
        // fresh-domain §F4 pass per Run Store reads one started-only
        // journal and one disturbed world: the surviving OS handle
        // re-adopts, and each verdict is Unknown — NeedsReview, no
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
                    RegisterAll(
                        fixture, PartialAddView(), PartialRemoveView());
                    fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                    AssertStartedOnlyTail(fixture.Root, persisted[i], 0);
                }
                var names = new[]
                {
                    "script.create_from_template", "script.create",
                    "package.add", "package.remove",
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

                Assert.AreEqual(
                    ProbeScriptSource(DamagedClassName),
                    File.ReadAllText(
                        PhysicalPath(ScriptPath(TemplateClassName))),
                    "create_from_template: NeedsReview leaves the foreign script");
                Assert.AreEqual(
                    ProbeScriptSource(DamagedClassName2),
                    File.ReadAllText(
                        PhysicalPath(ScriptPath(ScriptClassName))),
                    "script.create: NeedsReview leaves the foreign script");
                DisposeFixtures(fixtures);
                Crumb("phase-2 complete: four NeedsReview verdicts");
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
