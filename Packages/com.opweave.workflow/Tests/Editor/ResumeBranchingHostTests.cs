#nullable enable

// T-W4.3-03 [Edit] tests: local:T-W4.3-03:resume-branching — the SPEC §F5
// resume decision table driven through the real OpWeaveRunnerHost +
// FileRunStore recovery path at every journal boundary an interruption can
// leave (SPEC §F4 item 5, §F0):
//
//   StepSucceeded last  -> resume through the recorded Transitions edge,
//                          never index + 1;
//   StepWaiting last    -> resume by polling with the persisted resumeToken;
//   StepStarted only    -> the Operation's Reconcile decides: Completed
//                          adopts the journaled-consistent evidence only,
//                          NotApplied re-executes, Unknown becomes
//                          NeedsReview — never automatic success and never
//                          automatic re-execution (§F5 rules 1-6);
//   a truncated final   -> dropped per §F3, the Run resumes on the last
//     journal line         complete record;
//   unknown required    -> NeedsReview (§F0), as are middle corruption and
//     record / version     unknown or mismatched formatVersion /
//     drift                canonicalJsonVersion.
//
// A "host restart" is reproduced with the T-W4.3-01 mechanism: fixture 1
// drives the Run to the boundary and is disposed — OpWeaveRunnerHost.Dispose
// leaves lock.json residue the way an abandoned session does — and fixture 2
// runs the §F4 recovery pass fresh. The real domain-reload companions live
// in ResumeBranchingReloadTests (SPEC §T.3).

using System;
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
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.3-03:resume-branching</c> host-restart matrix: each test
    /// parks a Run at one journal boundary, drops the owning host, and lets a
    /// fresh host's §F4 pass rebuild and decide it — standard Operations
    /// (unity.component.add, unity.prefab.create_from_gameobject) and custom
    /// probe Operations through the identical path (SPEC §C2 test 5, §X.9).
    /// </summary>
    internal sealed class ResumeBranchingHostTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveResumeBranchingHost");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string EvidenceFile => Path.Combine(ProbeDir, "evidence.txt");

        private const string WorkflowId = "example.resume.branching";
        private const string MarkOp = "test.resume.mark";
        private const string HoldOp = "test.resume.hold";
        private const string TokenOp = "test.resume.token";
        private const string EvidenceOp = "test.resume.evidence";
        private const string UnsafeOp = "test.resume.unsafe";
        private const string TargetName = "ResumeBranchTarget";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            CleanupDisk();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
            foreach (var go in _objects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            _objects.Clear();
            RunLockHandoff.Clear(StoreRoot);
            CleanupDisk();
        }

        private static void CleanupDisk()
        {
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            const string folder = "Assets/OpWeaveResumeBranchingHost";
            if (AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        // ── Fixtures, plans, accept/tick/recover ────────────────────────

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(StoreRoot);
            _fixtures.Add(fixture);
            // The type_exists requirement the real component.add declares:
            // the FakeTypeProbe answers the port-side check while the
            // Operation itself resolves the type through the live domain.
            fixture.Probe.Know("UnityEngine.BoxCollider");
            return fixture;
        }

        // Ends the "previous host": OpWeaveRunnerHost.Dispose drops the OS
        // exclusion and leaves lock.json residue — the abandoned-session
        // state a crash or a host swap leaves behind.
        private void DisposeFixture(RunnerHostFixture fixture)
        {
            fixture.Dispose();
            _fixtures.Remove(fixture);
        }

        private GameObject NewTarget()
        {
            var go = new GameObject(TargetName);
            _objects.Add(go);
            return go;
        }

        // Mirrors RunnerHostFixture.Register but records a chosen origin —
        // the Standard-vs-Custom contrast SPEC §C2 test 5 requires (the
        // recovery path itself is origin-agnostic; this pins the claim).
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

        private static string Accept(RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
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

        private static void RunUntilTerminal(RunnerHostFixture fixture, string runId)
        {
            for (var i = 0; i < 8; i++)
            {
                Tick(fixture);
                if (fixture.Host.Coordinator.StateOf(runId) is { } state &&
                    (state == RunState.Succeeded || state == RunState.Failed ||
                     state == RunState.Cancelled))
                {
                    Assert.AreEqual(
                        RunState.Succeeded, state,
                        "the Run " + runId + " ended " + state +
                        "; journal: " + string.Join(
                            " | ", RunnerHostFixture.JournalLines(StoreRoot, runId)));
                    return;
                }
            }
            Assert.Fail(
                "the Run " + runId + " did not end within 8 ticks; state=" +
                fixture.Host.Coordinator.StateOf(runId));
        }

        private static RunRecoveryReport Recover(RunnerHostFixture fixture)
        {
            var recovered = fixture.Host.RecoverRuns();
            Assert.IsTrue(recovered.Ok, "the recovery pass failed: " +
                Describe(recovered.Diagnostics));
            Assert.IsNotNull(recovered.Value);
            return recovered.Value!;
        }

        private static void AssertNeedsReview(RunnerHostFixture fixture, string runId, string because)
        {
            Assert.AreEqual(
                RunState.NeedsReview,
                fixture.Host.Coordinator.StateOf(runId),
                because + " must end in NeedsReview; journal: " + string.Join(
                    " | ", RunnerHostFixture.JournalLines(StoreRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(StoreRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a review-needed resume never journals run_resumed");
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

        private static string[] Lines(string runId) =>
            RunnerHostFixture.JournalLines(StoreRoot, runId);

        private static void WriteLines(string runId, IEnumerable<string> lines)
        {
            File.WriteAllText(
                RunnerHostFixture.JournalPath(StoreRoot, runId),
                string.Join("\n", lines) + "\n", s_utf8);
        }

        // Keeps the records through the FIRST line carrying both fragments —
        // the crash window between that durable record and whatever the loop
        // was about to append next.
        private static void TruncateThrough(string runId, string typeFragment, string stepFragment)
        {
            var lines = Lines(runId);
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
            WriteLines(runId, lines.Take(cut + 1));
        }

        // A half-written tail: bytes of a record whose newline never landed.
        private static void AppendBrokenTail(string runId, string raw) =>
            File.AppendAllText(
                RunnerHostFixture.JournalPath(StoreRoot, runId), raw, s_utf8);

        private static void RewriteHeader(string runId, string member, int from, int to)
        {
            var lines = Lines(runId);
            lines[0] = lines[0].Replace(
                "\"" + member + "\":" + from, "\"" + member + "\":" + to);
            WriteLines(runId, lines);
        }

        // A readable, checksummed-wrong record at a real position — the
        // corruption the §F3 fold must NOT drop silently.
        private static void CorruptMiddleRecord(string runId)
        {
            var lines = Lines(runId);
            Assert.IsTrue(lines[2].Contains("\"type\":\"run_started\""),
                "fixture journal layout drifted: " + lines[2]);
            lines[2] = lines[2].Replace("\"seq\":2", "\"seq\":7");
            WriteLines(runId, lines);
        }

        // Removes the Preflight snapshot from the step_started record while
        // keeping the record valid — an evidence-free StepStarted, the §F5
        // rule-1 case.
        private static void StripStepStartedSnapshot(string runId)
        {
            var lines = Lines(runId);
            var index = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("\"type\":\"step_started\""))
                {
                    index = i;
                }
            }
            Assert.GreaterOrEqual(index, 0, "the journal has no step_started record");
            var env = ParseObject(lines[index]);
            Assert.IsTrue(
                env["data"] is JObject data && data.Remove("snapshot"),
                "the step_started record carried no snapshot member: " + lines[index]);
            env["crc"] = LineCrc(env);
            lines[index] = CanonicalJson.Serialize(env);
            WriteLines(runId, lines);
        }

        // ── Forged records (the shapes JournalCodec.WriteRecord cannot write)

        private static JObject ParseObject(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "fixture JSON did not parse");
            return (JObject)parsed.Value!.ToToken();
        }

        private static string LineCrc(JObject envelope)
        {
            var clone = (JObject)envelope.DeepClone();
            clone.Remove("crc");
            return Crc32.ToLowerHex(
                Crc32.Compute(s_utf8.GetBytes(CanonicalJson.Serialize(clone))));
        }

        // An unknown record type with a correct crc — the fold must answer by
        // its `required` flag, never by guessing.
        private static string ForgeEnvelope(
            long seq, string ts, string session, string type, bool required, string dataJson)
        {
            var env = new JObject
            {
                ["seq"] = seq,
                ["ts"] = ts,
                ["session"] = session,
                ["type"] = type,
                ["required"] = required,
                ["data"] = (JObject)RunnerHostFixture.Json(dataJson).ToToken(),
            };
            env["crc"] = LineCrc(env);
            return CanonicalJson.Serialize(env);
        }

        private static void AppendForgedRecord(
            string runId, string type, bool required, string dataJson)
        {
            var lines = Lines(runId).ToList();
            var last = ParseObject(lines[lines.Count - 1]);
            var seq = last["seq"] is JValue { Value: long s } ? s : -1L;
            var ts = last["ts"] is JValue { Value: string t }
                ? t : "2026-11-06T00:00:00.0000000Z";
            var session = last["session"] is JValue { Value: string e }
                ? e : "session-forged";
            Assert.GreaterOrEqual(seq, 0, "fixture journal line carries no seq");
            var forged = ForgeEnvelope(
                seq + 1, ts, session, type, required, dataJson);
            WriteLines(runId, lines.Append(forged));
        }

        // ── Probe Operations ────────────────────────────────────────────

        /// <summary>
        /// A custom Reconcilable Immediate whose side effect is a file under
        /// Library/ — the external-evidence analogue of SPEC §X.9: Completed
        /// only when the file still proves the recorded change, NotApplied
        /// when provably absent, Unknown on anything else.
        /// </summary>
        private sealed class FileEvidenceOperation : IOperation
        {
            private readonly string _evidenceFile;

            internal FileEvidenceOperation(string id, string evidenceFile)
            {
                _evidenceFile = evidenceFile;
                Descriptor = Op.Define(id, 1)
                    .Summary("Writes an evidence file; reconciles from it.")
                    .Output("result", "core.String")
                    .Effects(EffectNames.AssetModify)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }
            internal int ExecuteCalls { get; private set; }
            internal int ReconcileCalls { get; private set; }
            internal JsonValue? SnapshotSeen { get; private set; }
            // Test hook for verdicts live evidence cannot produce — a null
            // Outputs Completed, a throw.
            internal Func<JsonValue, ReconcileResult>? ReconcileOverride { get; set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json(
                        "{\"evidence\":\"file\",\"expected\":\"applied\"}"));

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                Directory.CreateDirectory(
                    Path.GetDirectoryName(_evidenceFile)!);
                File.WriteAllText(_evidenceFile, "applied", s_utf8);
                ctx.SetOutput("result", "applied-live");
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                ReconcileCalls++;
                SnapshotSeen = snapshot;
                if (ReconcileOverride is { } verdict)
                {
                    return verdict(snapshot);
                }
                if (!File.Exists(_evidenceFile))
                {
                    return new ReconcileResult.NotApplied();
                }
                return File.ReadAllText(_evidenceFile) == "applied"
                    ? new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>
                        {
                            ["result"] = RunnerHostFixture.Json("\"applied-adopted\""),
                        })
                    : (ReconcileResult)new ReconcileResult.Unknown(
                        "The evidence file content does not match the recorded change.");
            }

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// An Unsafe Immediate whose Reconcile is a trap: it would return
        /// Completed, so any observed call proves the §F5 gate let an Unsafe
        /// Operation authorize its own recovery (SPEC §F5 rule 3).
        /// </summary>
        private sealed class UnsafeTrapOperation : IOperation
        {
            internal UnsafeTrapOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Must never be reconciled.")
                    .Output("result", "core.String")
                    .Effects(EffectNames.AssetModify)
                    .Idempotency(Idempotency.Unsafe)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }
            internal int ReconcileCalls { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{\"kind\":\"unsafe\"}"));

            public StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("result", "unsafe-live");
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                ReconcileCalls++;
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>
                    {
                        ["result"] = RunnerHostFixture.Json("\"unsafe-adopted\""),
                    });
            }

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A Waitable that captures the resume token its restored Poll sees —
        /// the §F4 item 5 "StepWaiting resumes by polling with the persisted
        /// resumeToken" proof.
        /// </summary>
        private sealed class TokenWaitableOperation : IOperation
        {
            internal TokenWaitableOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits once, then reports the token it saw.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }
            internal int PollCalls { get; private set; }
            internal JsonValue? TokenSeen { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("\"tok-v1\""), "held", 1);

            public StepResult Poll(StepContext ctx)
            {
                PollCalls++;
                TokenSeen = ctx.ResumeToken;
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // ── SPEC §F4 item 5 row 1: StepSucceeded -> recorded Transitions ─

        // Three marker steps where step 0's Transitions edge points at step
        // 2. Interrupted right after the durable step_succeeded{step:0}, the
        // resume must enter step 2 — index + 1 would wrongly enter step 1.
        [Test]
        public void StepSucceededLastRecordResumesThroughTheRecordedTransition()
        {
            var fixture1 = NewFixture();
            fixture1.Register(
                new RunnerHostFixture.MarkerOperation(MarkOp, fixture1.Events), 'a');
            var steps = new[]
            {
                Step(0, "m0", MarkOp, 'a', next: 2, idempotency: Idempotency.Idempotent,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
                Step(1, "m1", MarkOp, 'a', next: -1, idempotency: Idempotency.Idempotent,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
                Step(2, "m2", MarkOp, 'a', next: -1, idempotency: Idempotency.Idempotent,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(
                new[] { ("m0", MarkOp), ("m1", MarkOp), ("m2", MarkOp) },
                ("m0", "m2"));
            var runId = Accept(fixture1, plan, doc);
            RunUntilTerminal(fixture1, runId);

            TruncateThrough(runId, "\"type\":\"step_succeeded\"", "\"step\":0");
            DisposeFixture(fixture1);

            var fixture2 = NewFixture();
            var marker2 = new RunnerHostFixture.MarkerOperation(MarkOp, fixture2.Events);
            fixture2.Register(marker2, 'a');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            RunUntilTerminal(fixture2, runId);

            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":2"),
                "the resume must enter the Transition-named step 2");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":1"),
                "index + 1 would have entered step 1 — the recorded " +
                "Transitions edge is the only legal continuation (SPEC §F4 item 5)");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""),
                "the resumed Run must finish through its own journal");
        }

        // ── SPEC §F4 item 5 row 2: StepWaiting -> poll + resumeToken ────

        [Test]
        public void StepWaitingLastRecordResumesByPollingWithItsPersistedResumeToken()
        {
            var fixture1 = NewFixture();
            fixture1.Register(new TokenWaitableOperation(TokenOp), 'a');
            var steps = new[]
            {
                Step(0, "w", TokenOp, 'a', idempotency: Idempotency.Idempotent,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("w", TokenOp) });
            var runId = Accept(fixture1, plan, doc);
            Tick(fixture1);
            Assert.AreEqual(RunState.Running, fixture1.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_waiting\"", "\"step\":0"),
                "the interruption point is the durable step_waiting record");
            DisposeFixture(fixture1);

            var fixture2 = NewFixture();
            var waitable = new TokenWaitableOperation(TokenOp);
            fixture2.Register(waitable, 'a');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            RunUntilTerminal(fixture2, runId);

            Assert.AreEqual(1, waitable.PollCalls,
                "the restored wait must be polled, not re-executed");
            Assert.IsNotNull(waitable.TokenSeen,
                "the poll saw no resume token — §F4 item 5's restoration is broken");
            Assert.AreEqual(
                "\"tok-v1\"", waitable.TokenSeen!.CanonicalText,
                "the persisted resumeToken must reach the restored Poll verbatim");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""),
                "the resumed wait must complete the Run");
        }

        // ── SPEC §F5: started-only + Completed -> adopt the evidence ────

        // [evidence -> hold]: the Run waits on step 1; truncation back to
        // step_started{step:0} leaves the file the first Execute wrote —
        // Reconcile's Completed proof — and the adoption must not re-execute.
        private string RunEvidenceThenTruncate(RunnerHostFixture fixture1)
        {
            var hold = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
            var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile);
            fixture1.Register(evidence, 'a');
            fixture1.Register(hold, 'b');
            var steps = new[]
            {
                Step(0, "e", EvidenceOp, 'a', next: 1, idempotency: Idempotency.Reconcilable,
                    outputTypes: new Dictionary<string, string> { ["result"] = "core.String" }),
                Step(1, "h", HoldOp, 'b', next: -1, idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("e", EvidenceOp), ("h", HoldOp) }, ("e", "h"));
            var runId = Accept(fixture1, plan, doc);
            Tick(fixture1);
            Assert.AreEqual(RunState.Running, fixture1.Host.Coordinator.StateOf(runId));
            Assert.AreEqual(1, evidence.ExecuteCalls, "the first Execute ran");

            TruncateThrough(runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);
            return runId;
        }

        [Test]
        public void StartedOnlyReconcilableCompletedAdoptsTheRecordedEvidence()
        {
            var runId = RunEvidenceThenTruncate(NewFixture());

            var fixture2 = NewFixture();
            var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile);
            fixture2.Register(evidence, 'a');
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(1, evidence.ReconcileCalls,
                "a Reconcilable started-only step is reconciled exactly once (§F5 rule 1)");
            Assert.IsNotNull(evidence.SnapshotSeen,
                "Reconcile must see the Preflight snapshot the StepStarted journaled");
            Assert.AreEqual(0, evidence.ExecuteCalls,
                "a Completed verdict must not re-execute the Operation (SPEC §F5)");

            // The adoption moved the Run past step 0; the held waitable keeps
            // it Running until released.
            var hold2 = (RunnerHostFixture.ControlledWaitableOperation)
                fixture2.Operations[1].Implementation!;
            hold2.Release = true;
            RunUntilTerminal(fixture2, runId);

            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"",
                    "\"step\":0", "\"reconciled\":true"),
                "the adopted step is journaled reconciled:true (§F4 item 6)");
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(
                    StoreRoot, runId, "\"applied-adopted\""),
                "the adopted outputs — not live outputs — reach the journal");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"run_resumed\""),
                "the adoption is preceded by the run_resumed write-ahead");
        }

        // ── SPEC §F5: started-only + NotApplied -> re-execute ────────────

        [Test]
        public void StartedOnlyReconcilableNotAppliedReexecutes()
        {
            var runId = RunEvidenceThenTruncate(NewFixture());
            File.Delete(EvidenceFile);

            var fixture2 = NewFixture();
            var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile);
            fixture2.Register(evidence, 'a');
            var hold2 = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
            fixture2.Register(hold2, 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(1, evidence.ReconcileCalls);

            hold2.Release = true;
            RunUntilTerminal(fixture2, runId);

            Assert.AreEqual(1, evidence.ExecuteCalls,
                "a NotApplied verdict re-executes the step once (SPEC §F5)");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_started\"",
                    "\"step\":0", "\"attempt\":1"),
                "the re-execution is a new attempt of step 0");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(
                    StoreRoot, runId, "\"reconciled\":true"),
                "a re-executed step is fresh execution, not adopted evidence");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""),
                "the re-executed step completes the Run normally");
        }

        // ── SPEC §F5: started-only + Unknown -> NeedsReview ─────────────

        [Test]
        public void StartedOnlyReconcilableUnknownBecomesNeedsReview()
        {
            var runId = RunEvidenceThenTruncate(NewFixture());
            // Evidence a reader cannot prove: the file exists but the content
            // is not what Preflight recorded.
            File.WriteAllText(EvidenceFile, "tampered", s_utf8);

            var fixture2 = NewFixture();
            var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile);
            fixture2.Register(evidence, 'a');
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(1, evidence.ReconcileCalls);
            Assert.AreEqual(0, evidence.ExecuteCalls,
                "an Unknown verdict must never re-execute either (SPEC §F0)");
            AssertNeedsReview(fixture2, runId,
                "an Unreconcilable-verdict step");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                "the Unknown verdict is journaled as step_needs_review (§E1)");
        }

        // ── SPEC §F5 rule 3: Unsafe never reaches Reconcile ──────────────

        [Test]
        public void StartedOnlyUnsafeBecomesNeedsReviewWithoutConsultingReconcile()
        {
            var fixture1 = NewFixture();
            fixture1.Register(new UnsafeTrapOperation(UnsafeOp), 'a');
            fixture1.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
            var steps = new[]
            {
                Step(0, "u", UnsafeOp, 'a', next: 1, idempotency: Idempotency.Unsafe,
                    outputTypes: new Dictionary<string, string> { ["result"] = "core.String" }),
                Step(1, "h", HoldOp, 'b', next: -1, idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("u", UnsafeOp), ("h", HoldOp) }, ("u", "h"));
            var runId = Accept(fixture1, plan, doc);
            Tick(fixture1);
            Assert.AreEqual(RunState.Running, fixture1.Host.Coordinator.StateOf(runId));
            TruncateThrough(runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);

            var fixture2 = NewFixture();
            var trap = new UnsafeTrapOperation(UnsafeOp);
            fixture2.Register(trap, 'a');
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(0, trap.ReconcileCalls,
                "an Unsafe Operation's Reconcile must never run — the §F5 " +
                "gate ends the step NeedsReview before it (SPEC §F5 rule 3)");
            AssertNeedsReview(fixture2, runId, "an Unsafe started-only step");
        }

        // ── SPEC §F5 rule 1: no Preflight snapshot, no Reconcile ─────────

        [Test]
        public void StartedOnlyWithoutPreflightSnapshotBecomesNeedsReview()
        {
            var runId = RunEvidenceThenTruncate(NewFixture());
            StripStepStartedSnapshot(runId);

            var fixture2 = NewFixture();
            var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile);
            fixture2.Register(evidence, 'a');
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(0, evidence.ReconcileCalls,
                "without the journaled Preflight snapshot there is no " +
                "evidence contract to reconcile against (SPEC §F5 rule 1)");
            AssertNeedsReview(fixture2, runId,
                "a snapshot-less started-only step");
        }

        // ── SPEC §F5: a Completed without the declared outputs is not ────
        // ── adoptable evidence ────────────────────────────────────────────

        [Test]
        public void StartedOnlyCompletedWithIncompleteOutputsEndsFailedNotAdopted()
        {
            var runId = RunEvidenceThenTruncate(NewFixture());

            var fixture2 = NewFixture();
            var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile)
            {
                ReconcileOverride = _ =>
                    new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>()),
            };
            fixture2.Register(evidence, 'a');
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(1, evidence.ReconcileCalls);
            Assert.AreEqual(0, evidence.ExecuteCalls);

            Assert.AreEqual(
                RunState.Failed, fixture2.Host.Coordinator.StateOf(runId),
                "a Completed whose outputs cannot restore the declared ports " +
                "fails validation — it is never silently adopted; journal: " +
                string.Join(" | ", RunnerHostFixture.JournalLines(StoreRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "no success is journaled for an adoption that failed validation");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(StoreRoot, runId, "\"reconciled\":true"));
        }

        // ── SPEC §F5: a throwing Reconcile is undecidable ────────────────

        [Test]
        public void StartedOnlyThrowingReconcileBecomesNeedsReview()
        {
            var runId = RunEvidenceThenTruncate(NewFixture());

            var fixture2 = NewFixture();
            var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile)
            {
                ReconcileOverride = _ => throw new InvalidOperationException("boom"),
            };
            fixture2.Register(evidence, 'a');
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(fixture2, runId,
                "a Reconcile that throws instead of answering");
        }

        // ── SPEC §F3: a truncated final line is dropped, the Run resumes ─

        [Test]
        public void TruncatedFinalJournalLineIsDroppedAndTheRunResumesOnTheLastCompleteRecord()
        {
            var fixture1 = NewFixture();
            var hold = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
            fixture1.Register(hold, 'b');
            var steps = new[]
            {
                Step(0, "h", HoldOp, 'b', idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("h", HoldOp) });
            var runId = Accept(fixture1, plan, doc);
            Tick(fixture1);
            Assert.AreEqual(RunState.Running, fixture1.Host.Coordinator.StateOf(runId));
            DisposeFixture(fixture1);

            // The tail of an interrupted write — bytes of a record whose
            // newline never landed (SPEC §F3): droppable, not corruption.
            AppendBrokenTail(runId, "{\"type\":\"step_succeeded\",\"seq\":99,");

            var fixture2 = NewFixture();
            var hold2 = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
            fixture2.Register(hold2, 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Assert.IsTrue(
                report.Diagnostics.Any(d => d.Code == "OW-STORE-002" &&
                    d.Severity == Severity.Warning),
                "the dropped tail is reported as a warning, not a silent " +
                "loss and not a hard error: " + Describe(report.Diagnostics));

            hold2.Release = true;
            RunUntilTerminal(fixture2, runId);
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""),
                "the Run resumed on the last complete record and finished");
        }

        // ── SPEC §F0/F2: an unknown required record -> NeedsReview ───────

        [Test]
        public void UnknownRequiredJournalRecordBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            fixture1.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                Step(0, "h", HoldOp, 'b', idempotency: Idempotency.Idempotent),
            });
            var runId = Accept(fixture1, plan, Doc(new[] { ("h", HoldOp) }));
            Tick(fixture1);
            DisposeFixture(fixture1);

            // A well-formed, checksummed record of a type this build cannot
            // know — required: it may record a state change.
            AppendForgedRecord(runId, "future_state_change", required: true, dataJson: "{}");

            var fixture2 = NewFixture();
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(fixture2, runId,
                "an unknown required journal record");
            Assert.IsTrue(
                report.Diagnostics.Any(d => d.Code == "OW-STORE-002"),
                "the unknown required record surfaces as OW-STORE-002: " +
                Describe(report.Diagnostics));
        }

        // ── SPEC §F0/F2: unknown / mismatched journal versions ──────────

        [Test]
        public void UnknownFormatVersionBecomesNeedsReview()
        {
            var runId = ParkWaitingRun(NewFixture());
            RewriteHeader(runId, "formatVersion", 1, 2);

            var fixture2 = NewFixture();
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(fixture2, runId,
                "a journal formatVersion this build cannot read");
        }

        [Test]
        public void UnknownCanonicalJsonVersionBecomesNeedsReview()
        {
            var runId = ParkWaitingRun(NewFixture());
            RewriteHeader(runId, "canonicalJsonVersion", 1, 2);

            var fixture2 = NewFixture();
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(fixture2, runId,
                "a journal canonicalJsonVersion this build cannot read");
        }

        // ── SPEC §F0/F3: middle corruption is never a truncated tail ─────

        [Test]
        public void MiddleCorruptionBecomesNeedsReview()
        {
            var runId = ParkWaitingRun(NewFixture());
            CorruptMiddleRecord(runId);

            var fixture2 = NewFixture();
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(fixture2, runId,
                "a checksum-invalid record at a real journal position");
        }

        private string ParkWaitingRun(RunnerHostFixture fixture1)
        {
            fixture1.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                Step(0, "h", HoldOp, 'b', idempotency: Idempotency.Idempotent),
            });
            var runId = Accept(fixture1, plan, Doc(new[] { ("h", HoldOp) }));
            Tick(fixture1);
            Assert.AreEqual(RunState.Running, fixture1.Host.Coordinator.StateOf(runId));
            DisposeFixture(fixture1);
            return runId;
        }

        // ── SPEC §F5/G5 evidence: a standard Reconcilable Operation ─────

        // unity.component.add completed live, then truncated to its bare
        // StepStarted. Same Editor session (SessionState survives), same live
        // objects: the §G5 evidence row proves Completed and the Run adopts —
        // the path a domain reload takes (its real-reload twin lives in
        // ResumeBranchingReloadTests).
        private string RunComponentAddThenTruncate(RunnerHostFixture fixture1, out GameObject target)
        {
            target = NewTarget();
            RegisterAs(fixture1, new ComponentAddOperation(), 'e', OperationOrigin.Standard);
            fixture1.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
            var steps = new[]
            {
                Step(0, "add", ComponentAddOperation.OperationId, 'e', next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["target"] = new Constant(RunnerHostFixture.Json(
                            "{\"kind\":\"gameObject\",\"path\":\"/" + TargetName + "\"}")),
                        ["componentType"] = new Constant(
                            RunnerHostFixture.Json("\"UnityEngine.BoxCollider\"")),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["component"] = "unity.ComponentRef<unity.Component>",
                    }),
                Step(1, "h", HoldOp, 'b', next: -1, idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(
                new[] { ("add", ComponentAddOperation.OperationId), ("h", HoldOp) },
                ("add", "h"));
            var runId = Accept(fixture1, plan, doc);
            Tick(fixture1);
            Assert.AreEqual(RunState.Running, fixture1.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(target!.GetComponent<BoxCollider>() != null,
                "the first Execute added the component");
            TruncateThrough(runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);
            return runId;
        }

        [Test]
        public void StandardOperationStartedOnlyCompletedAdoptsThroughTheSamePath()
        {
            var runId = RunComponentAddThenTruncate(NewFixture(), out var target);

            var fixture2 = NewFixture();
            RegisterAs(fixture2, new ComponentAddOperation(), 'e', OperationOrigin.Standard);
            var hold2 = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
            fixture2.Register(hold2, 'b');
            Assert.AreEqual(
                OperationOrigin.Standard, fixture2.Operations[0].Origin,
                "fixture error: the standard Operation must register Standard");

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            hold2.Release = true;
            RunUntilTerminal(fixture2, runId);

            Assert.AreEqual(
                1, target.GetComponents<BoxCollider>().Length,
                "a Completed verdict adopts; a re-execution would have added " +
                "a second BoxCollider");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"",
                    "\"step\":0", "\"reconciled\":true"),
                "the standard Operation's step is journaled reconciled:true");
        }

        [Test]
        public void StandardOperationStartedOnlyTamperedEvidenceBecomesNeedsReview()
        {
            var runId = RunComponentAddThenTruncate(NewFixture(), out var target);
            // A component Preflight did not record: the live state can no
            // longer prove the expected list, so no verdict is provable.
            target.AddComponent<BoxCollider>();

            var fixture2 = NewFixture();
            RegisterAs(fixture2, new ComponentAddOperation(), 'e', OperationOrigin.Standard);
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(fixture2, runId,
                "a standard Operation whose recorded evidence no longer matches");
            Assert.AreEqual(
                2, target.GetComponents<BoxCollider>().Length,
                "NeedsReview changes nothing — no extra add, no undo");
        }

        // ── SPEC §C2 test 5: a custom Operation, same path ──────────────

        [Test]
        public void CustomReconcilableStartedOnlyCompletedAdoptsThroughTheSamePath()
        {
            var runId = RunEvidenceThenTruncate(NewFixture());

            var fixture2 = NewFixture();
            var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile);
            fixture2.Register(evidence, 'a');
            var hold2 = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
            fixture2.Register(hold2, 'b');
            Assert.AreEqual(
                OperationOrigin.Custom, fixture2.Operations[0].Origin,
                "fixture error: the probe Operation must register Custom");
            Assert.AreEqual(
                OperationOrigin.Custom, fixture2.Operations[1].Origin);

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(1, evidence.ReconcileCalls);
            Assert.AreEqual(0, evidence.ExecuteCalls);

            hold2.Release = true;
            RunUntilTerminal(fixture2, runId);

            // The same journal shape the standard path produced: write-ahead
            // run_resumed, then the reconciled success.
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"run_resumed\""),
                "custom and standard Operations share the run_resumed write-ahead");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"",
                    "\"step\":0", "\"reconciled\":true"),
                "the custom Operation's adopted success is journaled identically");
        }

        // ── SPEC §G6.1: PrefabContentFingerprint v1 is mandatory ─────────

        // unity.prefab.create_from_gameobject completed live, then truncated
        // to its bare StepStarted: Completed only while the artifact still
        // carries the recorded PrefabContentFingerprint v1.
        private string RunPrefabCreateThenTruncate(
            RunnerHostFixture fixture1, out GameObject source)
        {
            const string folder = "Assets/OpWeaveResumeBranchingHost";
            AssetDatabase.CreateFolder("Assets", "OpWeaveResumeBranchingHost");
            source = NewTarget();
            RegisterAs(
                fixture1, new PrefabCreateFromGameObjectOperation(), 'e',
                OperationOrigin.Standard);
            fixture1.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
            var steps = new[]
            {
                Step(0, "p", PrefabCreateFromGameObjectOperation.OperationId, 'e',
                    next: 1, idempotency: Idempotency.Reconcilable,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["source"] = new Constant(RunnerHostFixture.Json(
                            "{\"kind\":\"gameObject\",\"path\":\"/" + TargetName + "\"}")),
                        ["path"] = new Constant(RunnerHostFixture.Json(
                            "\"" + folder + "/probe.prefab\"")),
                        ["connect"] = new Constant(RunnerHostFixture.Json("false")),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["prefab"] = "unity.PrefabAssetRef",
                    }),
                Step(1, "h", HoldOp, 'b', next: -1, idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(
                new[] { ("p", PrefabCreateFromGameObjectOperation.OperationId), ("h", HoldOp) },
                ("p", "h"));
            var runId = Accept(fixture1, plan, doc);
            Tick(fixture1);
            Assert.AreEqual(RunState.Running, fixture1.Host.Coordinator.StateOf(runId));
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/probe.prefab"),
                "the first Execute saved the Prefab");
            TruncateThrough(runId, "\"type\":\"step_started\"", "\"step\":0");
            DisposeFixture(fixture1);
            return runId;
        }

        [Test]
        public void PrefabStartedOnlyMatchingFingerprintAdopts()
        {
            var runId = RunPrefabCreateThenTruncate(NewFixture(), out _);

            var fixture2 = NewFixture();
            RegisterAs(
                fixture2, new PrefabCreateFromGameObjectOperation(), 'e',
                OperationOrigin.Standard);
            var hold2 = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
            fixture2.Register(hold2, 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            hold2.Release = true;
            RunUntilTerminal(fixture2, runId);

            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"",
                    "\"step\":0", "\"reconciled\":true"),
                "a matching PrefabContentFingerprint v1 is the §F5 Completed " +
                "evidence (SPEC §G6.1)");
        }

        [Test]
        public void PrefabStartedOnlyChangedArtifactNeverCompletes()
        {
            var runId = RunPrefabCreateThenTruncate(NewFixture(), out _);
            // Replace the artifact with foreign content: the recorded
            // fingerprint can no longer match, so nothing is provable.
            var foreign = new GameObject("ForeignPrefabContent");
            _objects.Add(foreign);
            foreign.AddComponent<BoxCollider>();
            PrefabUtility.SaveAsPrefabAsset(
                foreign, "Assets/OpWeaveResumeBranchingHost/probe.prefab");

            var fixture2 = NewFixture();
            RegisterAs(
                fixture2, new PrefabCreateFromGameObjectOperation(), 'e',
                OperationOrigin.Standard);
            fixture2.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');

            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(fixture2, runId,
                "a Prefab artifact whose content no longer matches the " +
                "recorded fingerprint");
        }
    }
}
