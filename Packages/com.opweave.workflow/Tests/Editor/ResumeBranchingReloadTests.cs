#nullable enable

// T-W4.3-03 [Edit] tests: local:T-W4.3-03:resume-branching — the resume
// branch table of SPEC §F4 item 5 / §F5 across REAL domain reloads
// (SPEC §T.3): a Run parked mid-flight in one domain is rebuilt, decided
// and continued by the §F4 pass of the domain the compile creates —
//
//   * the generated-script workflows of SPEC §1.1 / §G8 test 3 / §F4
//     test 1: create_from_template -> compile wait -> the domain reload
//     the compile causes -> the next node runs without human action,
//     including component.add consuming the freshly compiled type;
//   * a started-only standard Reconcilable step (unity.component.add)
//     whose Preflight evidence row survives the reload -> Completed is
//     adopted, and whose evidence was disturbed -> NeedsReview;
//   * a started-only CUSTOM Reconcilable step decided by the identical
//     path (SPEC §C2 test 5, §X.9);
//   * a mid-Waiting Run whose journal tail is a truncated line — dropped
//     per §F3, resumed on the last complete record, and the persisted
//     resumeToken reaches the post-reload Poll.
//
// The reload mechanics mirror RunRecoveryHostTests: marker scripts under
// Assets/Editor/, RecompileScripts, a domain sentinel proving the domain
// changed, and every cross-reload fact persisted under Library/ because
// iterator locals do not survive the AppDomain swap. T-W2.4-10: the
// persisted state file carries a per-Editor-session nonce mirrored in
// SessionState, setup and teardown never yield a reload emitter (the
// queued cleanup compile drains at the test body's own boundary), and
// the boundary loop re-drives a phase-1 a stale resume skipped — the
// ChangeSetDomainReloadTests shape.
//
// Git-package (unity.package.add) recovery evidence is deliberately out of
// scope here: the Operation-level full-commit-SHA Reconcile belongs to
// T-W4.2-04 and its interruption/resume coverage to T-W4.6-03 (SPEC §G8.1).

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
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.3-03:resume-branching</c> in Unity 6000.0 EditMode:
    /// interruption at each journal boundary followed by a real domain
    /// reload, resumed by the fresh domain's own §F4 pass.
    /// </summary>
    internal sealed class ResumeBranchingReloadTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveResumeBranchingReload");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string StateFile => Path.Combine(ProbeDir, "state.txt");
        private static string EvidenceFile => Path.Combine(ProbeDir, "evidence.txt");
        private static string ReleaseFlag => Path.Combine(ProbeDir, "release.flag");
        private static string TokenSeenFile => Path.Combine(ProbeDir, "token-seen.txt");

        private static string ProbeAssetFolder => "Assets/OpWeaveResumeBranchReload";
        private static string ProbeAssetDisk =>
            Path.Combine(ProjectRoot, ProbeAssetFolder.Replace('/', Path.DirectorySeparatorChar));

        private static string MarkerFolder => "Assets/Editor/OpWeaveResumeBranchReload";
        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveResumeBranchReload\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private const string WorkflowId = "example.resume.reload";
        private const string HoldOp = "test.resume.reload.hold";
        private const string WaitTypeOp = "test.resume.reload.waittype";
        private const string EvidenceOp = "test.resume.reload.evidence";
        private const string TargetName = "ResumeBranchReloadTarget";
        private const string GeneratedClass = "ResumeBranchProbeBehaviour";
        private const string GeneratedNamespace = "OpWeaveResumeBranch";
        private const string GeneratedTypeName = GeneratedNamespace + "." + GeneratedClass;
        private const string FindClass = "ResumeBranchFindProbeBehaviour";
        private const string FindTypeName = GeneratedNamespace + "." + FindClass;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // A per-Editor-session token phase-1 stores in SessionState and
        // writes as state.txt's last line; phase-2 reads both to prove
        // the record belongs to this session's phase-1 — a stale resume
        // that skipped phase-1 (no file, no nonce), a record deleted by
        // cleanup, and residue of an earlier session (file yes, nonce
        // no) each fail closed, never silently.
        private const string ProbeSessionKey = "OpWeave.ResumeBranching.state";

        // Bound on phase-1 re-drive legs after a stale resume is
        // detected (the T-W2.4-06 self-heal); the this-session probe
        // check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeSessionKey, nonce);
            return nonce;
        }

        // The probe proves phase-1 ran in THIS Editor session: state.txt
        // must exist AND its last line must equal the nonce phase-1
        // stored in SessionState — both survive the reload, so a skipped
        // phase-1 and cross-session residue both fail the check and the
        // caller re-drives a real phase-1 instead of skipping silently
        // into phase-2.
        private static bool ProbeWrittenThisSession()
        {
            if (!File.Exists(StateFile))
            {
                return false;
            }
            var expected = SessionState.GetString(ProbeSessionKey, "");
            if (expected.Length == 0)
            {
                return false;
            }
            var lines = File.ReadAllLines(StateFile);
            return lines.Length > 0 &&
                lines[lines.Length - 1] == expected;
        }

        private static void Crumb(string message) => Debug.Log("[W4303] " + message);

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static PhaseResult Pass() => new PhaseResult { Ok = true };

            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
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

        // The T-W4.3-01 cleanup contract, verbatim in shape: the exclusion
        // this process still holds on the probe root — the previous domain's
        // or a fixture's — is taken through the hand-off seam and ended the
        // orderly way so lock.json empties BEFORE the handle frees; then
        // both hand-off records drop. Nothing is possible while a live owner
        // elsewhere holds the store: the probe stays and is reported.
        private static void ReleaseLeakedState()
        {
            var obtained = RunLockHandoff.TryObtain(StoreRoot);
            if (!obtained.Ok || obtained.Value is not { } held)
            {
                Crumb("the probe Run Store is excluded elsewhere: " +
                    Describe(obtained.Diagnostics));
                RunLockHandoff.Clear(StoreRoot);
                return;
            }
            if (!held.Release().Ok)
            {
                held.Dispose();
            }
            RunLockHandoff.Clear(StoreRoot);
        }

        private static bool CleanupArtifacts()
        {
            ReleaseLeakedState();
            // Drop any §G4 compile-wait capture window a failed phase left
            // open — the full reset an Editor restart would have been.
            CompileErrorCapture.Clear();
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            var dirty = false;
            if (AssetDatabase.IsValidFolder(ProbeAssetFolder))
            {
                AssetDatabase.DeleteAsset(ProbeAssetFolder);
                dirty = true;
            }
            if (Directory.Exists(ProbeAssetDisk))
            {
                Directory.Delete(ProbeAssetDisk, recursive: true);
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
            var stray = GameObject.Find(TargetName);
            if (stray != null)
            {
                UnityEngine.Object.DestroyImmediate(stray);
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return dirty;
        }

        // Setup and teardown must never yield an ExpectDomainReload
        // instruction (RecompileScripts): UTF's PrepareForDomainReload
        // records the CURRENT TestEnumerator's program counter — still a
        // different test's method enumerator while setup or teardown is
        // running — and the resume then applies that stale PC to the next
        // test method's fresh enumerator, jumping it past phase-1
        // entirely (T-W2.4-10). The probe-folder deletions above queue
        // their compile under the runner's LockReloadAssemblies, so the
        // reload they need drains at the test body's own
        // `yield return new RecompileScripts()`, whose captured PC is the
        // correct one.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var dirty = CleanupArtifacts();
            Crumb("setup cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var dirty = CleanupArtifacts();
            Crumb("teardown cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        // ── Shared helpers ──────────────────────────────────────────────

        // Mirrors RunnerHostFixture.Register but records a chosen origin —
        // the standard-versus-custom contrast of SPEC §C2 test 5.
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

        private static void RunUntilTerminal(RunnerHostFixture fixture, string runId)
        {
            for (var i = 0; i < 12; i++)
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
                "the Run " + runId + " did not end within 12 ticks; state=" +
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

        private static void ArmReload()
        {
            Directory.CreateDirectory(MarkerDisk);
            File.WriteAllText(Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
            AssetDatabase.Refresh();
            ArmDomainSentinel();
        }

        private static string[] Lines(string runId) =>
            RunnerHostFixture.JournalLines(StoreRoot, runId);

        private static void WriteLines(string runId, IEnumerable<string> lines)
        {
            File.WriteAllText(
                RunnerHostFixture.JournalPath(StoreRoot, runId),
                string.Join("\n", lines) + "\n", s_utf8);
        }

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

        // The regression guard of the started-only fixtures: the journal the
        // recovering domain reads must END at the bare step_started{step} —
        // a record past it (a still-installed host ticking into the yield
        // pumps) is a two-steps-in-flight journal the §F4 rebuild correctly
        // refuses, never the interruption the fixture means to leave.
        private static void AssertStartedOnlyTail(string runId, int step)
        {
            var tail = Lines(runId);
            Assert.IsTrue(
                tail.Length > 0 &&
                tail[tail.Length - 1].Contains("\"type\":\"step_started\"") &&
                tail[tail.Length - 1].Contains("\"step\":" + step),
                "the pre-reload journal must end at step " + step +
                "'s step_started with no later records; journal: " +
                string.Join(" | ", tail));
        }

        private static void WriteState(params string[] lines)
        {
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(
                StateFile,
                string.Join("\n", lines) + "\n" + ArmProbeNonce() + "\n",
                s_utf8);
        }

        // The fail-closed read of phase-1's persisted state: missing is
        // split between "phase-1 never ran in this Editor session" (no
        // record, no armed nonce) and "deleted after phase-1 wrote it",
        // and a record whose session nonce does not match this session's
        // arm is stale residue — never trusted.
        private static string[] ReadState()
        {
            var expectedToken = SessionState.GetString(ProbeSessionKey, "");
            if (!File.Exists(StateFile))
            {
                Assert.Fail(
                    expectedToken.Length == 0
                        ? "phase-1 never executed in this Editor session (" +
                            StateFile + " was never written; probeDir exists=" +
                            Directory.Exists(ProbeDir) + ")"
                        : "the phase-1 state file " + StateFile +
                            " was deleted after phase-1 wrote it");
            }
            var persisted = File.ReadAllLines(StateFile);
            if (expectedToken.Length == 0 || persisted.Length < 2 ||
                persisted[persisted.Length - 1] != expectedToken)
            {
                Assert.Fail(
                    "the phase-1 state file is stale — a previous Editor " +
                    "session or an abandoned leg wrote " + StateFile +
                    "; refusing to trust it");
            }
            return persisted;
        }

        private static JObject ParseObject(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "fixture JSON did not parse");
            return (JObject)parsed.Value!.ToToken();
        }

        // The canonical text of the resumeToken the journal's LAST
        // step_waiting record for a step persisted — what the restored Poll
        // must be handed back verbatim (SPEC §F4 item 5).
        private static string WaitingTokenText(string runId, int step)
        {
            string? token = null;
            foreach (var line in Lines(runId))
            {
                if (!line.Contains("\"type\":\"step_waiting\"") ||
                    !line.Contains("\"step\":" + step))
                {
                    continue;
                }
                var env = ParseObject(line);
                var persisted = (env["data"] as JObject)?["resumeToken"];
                Assert.IsNotNull(
                    persisted,
                    "the step_waiting record carries no resumeToken: " + line);
                token = JsonValue.From(persisted!).CanonicalText;
            }
            Assert.IsNotNull(token,
                "the journal has no step_waiting record for step " + step);
            return token!;
        }

        // ── Probe Operations (defined in this assembly — they cross the ──
        // ── reload with it) ─────────────────────────────────────────────

        /// <summary>
        /// A Waitable on the real §G4 <c>ScriptsCompiled</c> boundary: the
        /// same token lifecycle <c>unity.script.compile</c> runs — Execute
        /// opens the step's compile-error capture window, queues the
        /// compilation and waits on the request-time token; Poll continues
        /// the wait from the restored resumeToken alone, which is exactly
        /// how the persisted wait crosses the domain reload. On its first
        /// post-restore Poll it persists the token it was handed, so the
        /// test can prove §F4 item 5's restoration.
        /// </summary>
        private sealed class WaitForCompiledTypeOperation : IOperation
        {
            internal WaitForCompiledTypeOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits on the real compile boundary for the generated type.")
                    .Input("typeName", "unity.TypeName", required: true)
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Compile)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                if (new UnityTypeProbe().TypeExists(ctx.Get<string>("typeName")))
                {
                    return Complete(ctx);
                }
                // The §G4 order a real compile wait follows: arm the step's
                // error capture and snapshot the request-time counters,
                // THEN queue the compilation the token waits on.
                var token = ScriptsCompiledCondition.InitialToken();
                CompilationPipeline.RequestScriptCompilation();
                return StepResult.Waiting(token, "compiling", 1);
            }

            public StepResult Poll(StepContext ctx)
            {
                if (!File.Exists(TokenSeenFile))
                {
                    File.WriteAllText(
                        TokenSeenFile,
                        ctx.ResumeToken?.CanonicalText ?? "<null>", s_utf8);
                }
                var verdict = new ScriptsCompiledCondition().Check(ctx.ResumeToken!);
                switch (verdict.Kind)
                {
                    case WaitStatusKind.Pending:
                        return StepResult.Waiting(
                            verdict.ResumeToken!, "compiling", 1);
                    case WaitStatusKind.Failed:
                        return StepResult.Failed(verdict.Error!);
                    default:
                        // The §G4 boundary is met; the compiled type must
                        // resolve now — the point of the workflow.
                        return new UnityTypeProbe().TypeExists(
                            ctx.Get<string>("typeName"))
                            ? Complete(ctx)
                            : StepResult.Waiting(
                                ctx.ResumeToken!, "compiling", 1);
                }
            }

            private static StepResult Complete(StepContext ctx)
            {
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx) => ScriptsCompiledCondition.EndWait();
        }

        /// <summary>
        /// A Waitable gated by a flag file under Library/ — the released
        /// mid-Waiting Run's completion across the reload, and the truncated
        /// journal tail case.
        /// </summary>
        private sealed class FlagWaitableOperation : IOperation
        {
            internal FlagWaitableOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits for the release flag file.")
                    .Input("flag", "core.String", required: true)
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(
                    RunnerHostFixture.Json("\"held\""), "held", 1);

            public StepResult Poll(StepContext ctx)
            {
                if (!File.Exists(TokenSeenFile))
                {
                    File.WriteAllText(
                        TokenSeenFile,
                        ctx.ResumeToken?.CanonicalText ?? "<null>", s_utf8);
                }
                if (!File.Exists(ctx.Get<string>("flag")))
                {
                    return StepResult.Waiting(
                        RunnerHostFixture.Json("\"held\""), "held", 1);
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A custom Reconcilable Immediate whose side effect is a file under
        /// Library/ — evidence that outlives the domain that wrote it, the
        /// §X.9 external-evidence analogue of SPEC §C2 test 5.
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
            internal int ValidateCalls { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx)
            {
                ValidateCalls++;
                return Array.Empty<Diagnostic>();
            }

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json(
                        "{\"evidence\":\"file\",\"expected\":\"applied\"}"));

            public StepResult Execute(StepContext ctx)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_evidenceFile)!);
                File.WriteAllText(_evidenceFile, "applied", s_utf8);
                ctx.SetOutput("result", "applied-live");
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
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

        // ── SPEC §G8 test 3 / §1.1: template -> compile -> component.add ──

        [UnityTest]
        [Timeout(240000)]
        public IEnumerator TemplateGeneratedTypeFlowsThroughTheRealReloadIntoComponentAdd()
        {
            var before = ScriptWorkflowPhase(
                ComponentAddPlan(GeneratedClass, GeneratedNamespace), "add");
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            // T-W2.4-06 stale-resume self-heal: a predecessor's captured
            // UTF resume PC applies to this fresh enumerator and lands it
            // at a post-yield state inside this loop — with every local
            // reset. Each leg drains, then MUST pass the this-session
            // probe check: a record phase-1 never wrote re-drives a real
            // phase-1 + compile + reload instead of skipping silently
            // into phase-2.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the generated script's compile did not produce a domain reload");

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupArtifacts();
                var redrive = ScriptWorkflowPhase(
                    ComponentAddPlan(GeneratedClass, GeneratedNamespace), "add");
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = ScriptWorkflowPhaseAfter(GeneratedTypeName, "add");
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);

            var target = GameObject.Find(TargetName);
            Assert.IsNotNull(target, "the target GameObject did not survive the reload");
            Assert.IsTrue(
                target!.GetComponents<MonoBehaviour>().Any(
                    c => string.Equals(
                        c.GetType().FullName, GeneratedTypeName, StringComparison.Ordinal)),
                "component.add consumed the freshly compiled generated type " +
                "with no human action (SPEC §1.1)");
        }

        // ── SPEC §F4 test 1: script -> compile wait -> next node starts ─

        [UnityTest]
        [Timeout(240000)]
        public IEnumerator GeneratedScriptCompileWaitResumesIntoTheNextNodeAcrossReload()
        {
            var before = ScriptWorkflowPhase(
                GameObjectFindPlan(FindClass, GeneratedNamespace), "find");
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the generated script's compile did not produce a domain reload");

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupArtifacts();
                var redrive = ScriptWorkflowPhase(
                    GameObjectFindPlan(FindClass, GeneratedNamespace), "find");
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = ScriptWorkflowPhaseAfter(FindTypeName, "find");
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // The shared phase-1 of the two generated-script workflows: the
        // template step writes and imports the script, the compile-wait step
        // enters StepWaiting — the journal's durable interruption record —
        // and the reload is armed while the Run waits.
        private static PhaseResult ScriptWorkflowPhase(
            (CompiledPlan plan, WorkflowDocument doc) built, string lastNode)
        {
            RunnerHostFixture? fixture = null;
            try
            {
                fixture = new RunnerHostFixture(StoreRoot);
                RegisterWorkflowOperations(fixture);
                fixture.Install();
                // The open scene's objects are serialized through the domain
                // reload — the path-only ref resolves again in the new domain.
                _ = new GameObject(TargetName);
                if (!AssetDatabase.IsValidFolder(ProbeAssetFolder))
                {
                    AssetDatabase.CreateFolder("Assets", "OpWeaveResumeBranchReload");
                }
                var runId = Accept(fixture, built.plan, built.doc);
                // The create step's synchronous import can consume the whole
                // tick budget, so the compile-wait step may start a tick or
                // more later — pump until its durable step_waiting record
                // lands (bounded; the Run must stay Running meanwhile).
                var parked = false;
                for (var i = 0; i < 8 && !parked; i++)
                {
                    Tick(fixture);
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    if (state != RunState.Running)
                    {
                        var journal = string.Join(" | ", Lines(runId));
                        fixture.Dispose();
                        return PhaseResult.Fail(
                            "the script workflow did not park mid-flight: " +
                            state + "; journal: " + journal);
                    }
                    parked = RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId,
                        "\"type\":\"step_waiting\"", "\"step\":1");
                }
                if (!parked)
                {
                    var journal = string.Join(" | ", Lines(runId));
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the compile-wait step left no step_waiting record: " + journal);
                }
                WriteState(runId, fixture.SessionId, lastNode);
                ArmReload();
                Crumb("phase-1 complete: script generated, Run waits at the " +
                    "compile boundary, runId=" + runId);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2: the domain the compile created. The pass takes over the
        // exclusion the dead domain's OS handle still holds, the restored
        // StepWaiting polls until the generated type resolves, and the last
        // node runs through the recorded Transitions edge — no human action.
        private static PhaseResult ScriptWorkflowPhaseAfter(string typeName, string lastNode)
        {
            RunnerHostFixture? fixture = null;
            try
            {
                Assert.IsFalse(EditorApplication.isCompiling,
                    "the compile must be over before this phase runs");
                var persisted = ReadState();
                var runId = persisted[0];

                fixture = new RunnerHostFixture(StoreRoot);
                RegisterWorkflowOperations(fixture);
                fixture.Probe.Know(typeName);
                fixture.Editor.IsCompiling = EditorApplication.isCompiling;

                var report = Recover(fixture);
                Assert.IsTrue(report.ExclusionHeld);
                Assert.AreEqual(
                    RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                    "the waiting Run did not come back Running; journal: " +
                    string.Join(" | ", Lines(runId)));

                // The resumeToken the dead domain's last step_waiting record
                // persisted — before any post-reload Poll rewrites it.
                var persistedToken = WaitingTokenText(runId, 1);
                RunUntilTerminal(fixture, runId);

                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"run_resumed\""),
                    "the reload resume is journaled as run_resumed");
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":1"),
                    "the restored StepWaiting completed by polling");
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":2"),
                    "the next node started automatically after the reload " +
                    "(SPEC §F4 test 1)");
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""),
                    "the workflow reached Succeeded without human action");
                Assert.IsFalse(
                    RunnerHostFixture.JournalHas(StoreRoot, runId, "\"reconciled\":true"),
                    "fresh execution — nothing was adopted without evidence");
                Assert.IsTrue(
                    File.Exists(TokenSeenFile) &&
                    File.ReadAllText(TokenSeenFile) == persistedToken,
                    "the persisted resumeToken did not reach the post-reload " +
                    "Poll verbatim (SPEC §F4 item 5)");
                fixture.Dispose();
                Crumb("phase-2 complete: workflow finished through '" + lastNode + "'");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void RegisterWorkflowOperations(RunnerHostFixture fixture)
        {
            RegisterAs(
                fixture, new CreateScriptFromTemplateOperation(), 'a',
                OperationOrigin.Standard);
            fixture.Register(new WaitForCompiledTypeOperation(WaitTypeOp), 'b');
            RegisterAs(fixture, new ComponentAddOperation(), 'c', OperationOrigin.Standard);
            RegisterAs(fixture, new GameObjectFindOperation(), 'd', OperationOrigin.Standard);
        }

        // script.create_from_template -> wait-for-type -> component.add:
        // the generated type name flows through the FromStep edges the
        // restored ValueStore feeds after the rebuild.
        private static (CompiledPlan, WorkflowDocument) ComponentAddPlan(
            string className, string ns)
        {
            var steps = new[]
            {
                Step(0, "create", CreateScriptFromTemplateOperation.OperationId, 'a',
                    next: 1, idempotency: Idempotency.Reconcilable,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["template"] = new Constant(RunnerHostFixture.Json("\"mono_behaviour\"")),
                        ["className"] = new Constant(RunnerHostFixture.Json(
                            "\"" + className + "\"")),
                        ["namespace"] = new Constant(RunnerHostFixture.Json("\"" + ns + "\"")),
                        ["folder"] = new Constant(RunnerHostFixture.Json(
                            "\"" + ProbeAssetFolder + "\"")),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["script"] = "unity.AssetRef<unity.MonoScript>",
                        ["typeName"] = "unity.TypeName",
                    }),
                Step(1, "wait", WaitTypeOp, 'b', next: 2, idempotency: Idempotency.Idempotent,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["typeName"] = new FromStep(0, "typeName"),
                    },
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
                Step(2, "add", ComponentAddOperation.OperationId, 'c', next: -1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["target"] = new Constant(RunnerHostFixture.Json(
                            "{\"kind\":\"gameObject\",\"path\":\"/" + TargetName + "\"}")),
                        ["componentType"] = new FromStep(0, "typeName"),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["component"] = "unity.ComponentRef<unity.Component>",
                    }),
            };
            return (
                RunnerHostFixture.Plan(WorkflowId, steps),
                Doc(
                    new[]
                    {
                        ("create", CreateScriptFromTemplateOperation.OperationId),
                        ("wait", WaitTypeOp),
                        ("add", ComponentAddOperation.OperationId),
                    },
                    ("create", "wait"), ("wait", "add")));
        }

        // script.create_from_template -> wait-for-type -> gameobject.find:
        // the F4 test-1 shape whose "next node" is a read-only standard
        // Operation.
        private static (CompiledPlan, WorkflowDocument) GameObjectFindPlan(
            string className, string ns)
        {
            var steps = new[]
            {
                Step(0, "create", CreateScriptFromTemplateOperation.OperationId, 'a',
                    next: 1, idempotency: Idempotency.Reconcilable,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["template"] = new Constant(RunnerHostFixture.Json("\"mono_behaviour\"")),
                        ["className"] = new Constant(RunnerHostFixture.Json(
                            "\"" + className + "\"")),
                        ["namespace"] = new Constant(RunnerHostFixture.Json("\"" + ns + "\"")),
                        ["folder"] = new Constant(RunnerHostFixture.Json(
                            "\"" + ProbeAssetFolder + "\"")),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["script"] = "unity.AssetRef<unity.MonoScript>",
                        ["typeName"] = "unity.TypeName",
                    }),
                Step(1, "wait", WaitTypeOp, 'b', next: 2, idempotency: Idempotency.Idempotent,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["typeName"] = new FromStep(0, "typeName"),
                    },
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
                Step(2, "find", GameObjectFindOperation.OperationId, 'd', next: -1,
                    idempotency: Idempotency.Idempotent,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["path"] = new Constant(RunnerHostFixture.Json(
                            "\"/" + TargetName + "\"")),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["gameObject"] = "unity.GameObjectRef",
                    }),
            };
            return (
                RunnerHostFixture.Plan(WorkflowId, steps),
                Doc(
                    new[]
                    {
                        ("create", CreateScriptFromTemplateOperation.OperationId),
                        ("wait", WaitTypeOp),
                        ("find", GameObjectFindOperation.OperationId),
                    },
                    ("create", "wait"), ("wait", "find")));
        }

        // ── SPEC §F5 across a real reload: started-only Completed ────────

        [UnityTest]
        [Timeout(240000)]
        public IEnumerator StartedOnlyComponentAddReconcilesCompletedAcrossTheRealReload()
        {
            var before = StartedOnlyComponentAddPhase(tamper: false);
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

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

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupArtifacts();
                var redrive = StartedOnlyComponentAddPhase(tamper: false);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = StartedOnlyComponentAddPhaseAfter(expectAdopted: true);
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // The §F0 twin: the same started-only step whose live evidence a
        // tamper made unprovable — Unknown, NeedsReview, never re-executed.
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator StartedOnlyComponentAddTamperedBecomesNeedsReviewAcrossTheRealReload()
        {
            var before = StartedOnlyComponentAddPhase(tamper: true);
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

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

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupArtifacts();
                var redrive = StartedOnlyComponentAddPhase(tamper: true);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = StartedOnlyComponentAddPhaseAfter(expectAdopted: false);
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // Phase 1: [component.add -> hold] executes step 0 and waits on
        // step 1; the journal is then cut back to the bare step_started{0}
        // — the interruption point where only Reconcile can decide step 0.
        // The tamper variant adds one more component Preflight never saw.
        private static PhaseResult StartedOnlyComponentAddPhase(bool tamper)
        {
            RunnerHostFixture? fixture = null;
            try
            {
                fixture = new RunnerHostFixture(StoreRoot);
                fixture.Probe.Know("UnityEngine.BoxCollider");
                RegisterAs(fixture, new ComponentAddOperation(), 'e', OperationOrigin.Standard);
                fixture.Register(new FlagWaitableOperation(HoldOp), 'b');
                fixture.Install();
                var target = new GameObject(TargetName);
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
                    Step(1, "h", HoldOp, 'b', next: -1, idempotency: Idempotency.Idempotent,
                        inputs: new Dictionary<string, InputSource>
                        {
                            ["flag"] = new Constant(
                                JsonValue.From(new JValue(ReleaseFlag))),
                        },
                        outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
                };
                var plan = RunnerHostFixture.Plan(WorkflowId, steps);
                var doc = Doc(
                    new[] { ("add", ComponentAddOperation.OperationId), ("h", HoldOp) },
                    ("add", "h"));
                var runId = Accept(fixture, plan, doc);
                Tick(fixture);
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.Running)
                {
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    fixture.Dispose();
                    return PhaseResult.Fail("the add->hold Run did not park: " + state);
                }
                if (target.GetComponent<BoxCollider>() == null)
                {
                    fixture.Dispose();
                    return PhaseResult.Fail("the first Execute did not add the component");
                }
                if (tamper)
                {
                    // Live evidence the Preflight row did not record: nothing
                    // about the expected addition is provable any more.
                    target.AddComponent<BoxCollider>();
                }
                // Stop the Run BEFORE the started-only state is produced:
                // while the host stays installed, the EditorApplication.update
                // ticks the yield below pumps keep executing steps — step 1's
                // records would land past the truncated tail and leave the
                // two-steps-in-flight journal the §F4 rebuild correctly
                // refuses (OW-STORE-002 -> NeedsReview). Uninstall removes
                // the update tick and the reload/quit flush hooks; the
                // coordinator keeps the OS exclusion the dead domain must
                // still hold (SPEC §E2 item 3).
                fixture.Host.Uninstall();
                TruncateThrough(runId, "\"type\":\"step_started\"", "\"step\":0");
                AssertStartedOnlyTail(runId, 0);
                WriteState(runId, fixture.SessionId, tamper ? "tampered" : "clean");
                ArmReload();
                Crumb("phase-1 complete: started-only add (tamper=" + tamper +
                    "), runId=" + runId);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2: same Editor session (SessionState survives the reload —
        // §F5 rule 3's session evidence), same live objects. Completed
        // adopts the recorded evidence row; a disturbed one answers Unknown
        // and ends NeedsReview — never a second AddComponent.
        private static PhaseResult StartedOnlyComponentAddPhaseAfter(bool expectAdopted)
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var persisted = ReadState();
                var runId = persisted[0];
                var tampered = persisted.Length > 2 && persisted[2] == "tampered";

                fixture = new RunnerHostFixture(StoreRoot);
                fixture.Probe.Know("UnityEngine.BoxCollider");
                RegisterAs(fixture, new ComponentAddOperation(), 'e', OperationOrigin.Standard);
                fixture.Register(new FlagWaitableOperation(HoldOp), 'b');
                fixture.Editor.IsCompiling = EditorApplication.isCompiling;

                // What the recovery pass actually reads: the pre-reload
                // phase's regression guard one domain later.
                AssertStartedOnlyTail(runId, 0);
                var report = Recover(fixture);
                Assert.IsTrue(report.ExclusionHeld);
                var target = GameObject.Find(TargetName);
                Assert.IsNotNull(target, "the target GameObject did not survive the reload");

                if (expectAdopted)
                {
                    Assert.AreEqual(
                        RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                        "the adopted step must let the Run resume; journal: " +
                        string.Join(" | ", Lines(runId)));
                    Assert.IsTrue(
                        RunnerHostFixture.JournalHasAll(
                            StoreRoot, runId, "\"type\":\"step_succeeded\"",
                            "\"step\":0", "\"reconciled\":true"),
                        "the component.add evidence row is Completed across " +
                        "the reload — journaled reconciled:true (§F5)");
                    Assert.IsTrue(
                        RunnerHostFixture.JournalHasAll(
                            StoreRoot, runId, "\"type\":\"run_resumed\""),
                        "the adoption follows the run_resumed write-ahead");
                    File.WriteAllText(ReleaseFlag, "released", s_utf8);
                    RunUntilTerminal(fixture, runId);
                    Assert.AreEqual(
                        1, target!.GetComponents<BoxCollider>().Length,
                        "adoption added nothing — a re-execution would have " +
                        "created a second component");
                }
                else
                {
                    Assert.IsTrue(tampered, "the NeedsReview phase ran without its tamper");
                    Assert.AreEqual(
                        RunState.NeedsReview, fixture.Host.Coordinator.StateOf(runId),
                        "a disturbed evidence row must end NeedsReview, never " +
                        "an automatic outcome; journal: " +
                        string.Join(" | ", Lines(runId)));
                    Assert.IsFalse(
                        RunnerHostFixture.JournalHas(
                            StoreRoot, runId, "\"type\":\"run_resumed\""),
                        "the NeedsReview verdict precedes run_resumed (§F4 item 6)");
                    Assert.AreEqual(
                        2, target!.GetComponents<BoxCollider>().Length,
                        "NeedsReview neither adds nor removes anything");
                }
                fixture.Dispose();
                Crumb("phase-2 complete: adopted=" + expectAdopted);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ── SPEC §C2 test 5 across a real reload: a custom Reconcilable ──

        // The type-provider candidates the planHash's weak fingerprints bind
        // through — the same pair RunnerHostFixture.Types builds.
        private static readonly TypeProviderCandidate[] s_typeProviders =
        {
            new TypeProviderCandidate(
                CoreTypeProvider.ProviderId,
                new CoreTypeProvider(),
                OperationOrigin.Standard),
            new TypeProviderCandidate(
                UnityTypeProvider.ProviderId,
                new UnityTypeProvider(),
                OperationOrigin.Standard),
        };

        // The candidates a Registry build would emit for these probes: the
        // real Descriptor, Origin and compiled-bytes implementation
        // fingerprint (WeakFingerprintProvider, SPEC §C3 rule 6), so the
        // compiled Plan's per-step fingerprints resolve against the resumed
        // domain's registry with no seeded hash material.
        private static RegistrySnapshot ProbeSnapshot(
            params (IOperation Operation, OperationOrigin Origin)[] operations)
        {
            var candidates = new List<OperationCandidate>();
            foreach (var (op, origin) in operations)
            {
                var fingerprint =
                    WeakFingerprintProvider.ForDefiningType(op.GetType());
                Assert.IsTrue(
                    fingerprint.Ok,
                    "the probe Operation's assembly fingerprint failed: " +
                    Describe(fingerprint.Diagnostics));
                candidates.Add(new OperationCandidate(
                    op.Descriptor,
                    origin,
                    op.GetType().Assembly.GetName().Name!,
                    null,
                    fingerprint.Value!.DefiningAssemblySha256,
                    op.Descriptor.ExecutionMode == ExecutionMode.Waitable,
                    Implementation: op));
            }
            return RegistrySnapshotBuilder.Build(
                candidates, RunnerHostFixture.Types);
        }

        // The custom workflow the REAL compiler checks and plans:
        // core.start -> evidence -> flag-gated hold, in the same document
        // shape a stored .opweave.json carries.
        private static WorkflowDocument CustomWorkflowDoc() =>
            RunnerHostFixture.Doc(
                WorkflowId,
                new List<NodeData>
                {
                    new NodeData
                    {
                        Id = "start", Operation = "core.start", Version = 1,
                    },
                    new NodeData
                    {
                        Id = "e", Operation = EvidenceOp, Version = 1,
                    },
                    new NodeData
                    {
                        Id = "h",
                        Operation = HoldOp,
                        Version = 1,
                        Parameters = new Dictionary<string, JToken>
                        {
                            ["flag"] = new JValue(ReleaseFlag),
                        },
                    },
                },
                new List<ControlEdge>
                {
                    new ControlEdge("start", "e"),
                    new ControlEdge("e", "h"),
                });

        // The StaticContext the D3 static check hands to each Operation's
        // ValidateStatic — the probe Operations resolve nothing through it.
        private sealed class ProbeStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            internal ProbeStaticContext(
                string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters =>
                _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("the fixture context resolves nothing");
        }

        [UnityTest]
        [Timeout(240000)]
        public IEnumerator StartedOnlyCustomOperationReconcilesAcrossTheRealReload()
        {
            var before = StartedOnlyCustomPhase();
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

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

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupArtifacts();
                var redrive = StartedOnlyCustomPhase();
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = StartedOnlyCustomPhaseAfter();
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // Phase 1: the REAL §D1 pipeline — a RegistrySnapshot carries the
        // probes with their true fingerprint material, the snapshot-backed
        // WorkflowCompiler runs D2–D6 over the workflow document (the custom
        // Operation is checked by its own ValidateStatic, and the Plan the
        // Run executes is the one the compiler built — not a hand-shaped
        // fingerprint pair), then the compiled Plan parks mid-flight and is
        // cut back to the bare step_started of the evidence step.
        private static PhaseResult StartedOnlyCustomPhase()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                fixture = new RunnerHostFixture(StoreRoot);
                var evidence = new FileEvidenceOperation(EvidenceOp, EvidenceFile);
                var snapshot = ProbeSnapshot(
                    (new StartOperation(), OperationOrigin.Standard),
                    (evidence, OperationOrigin.Custom),
                    (new FlagWaitableOperation(HoldOp), OperationOrigin.Custom));
                fixture.SnapshotOverride = snapshot;
                fixture.Install();

                var doc = CustomWorkflowDoc();
                var report = new WorkflowCompiler(
                        snapshot,
                        (nodeId, parameters) =>
                            new ProbeStaticContext(nodeId, parameters),
                        fixture.Probe,
                        UnityPlanProvenance.Capture(
                            IntegrityLevel.Weak, s_typeProviders))
                    .Validate(new ValidationRequest(
                        doc, null, ValidationMode.Static, ActorKind.Agent));
                if (!report.Ok || report.Plan is null)
                {
                    var errors = string.Join("; ", report.Errors.Select(
                        d => d.Code + " " + d.Message));
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the custom workflow did not validate: " + errors);
                }
                if (evidence.ValidateCalls < 1)
                {
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the static check never reached the custom " +
                        "Operation's ValidateStatic — the workflow was not " +
                        "validated before execution");
                }
                var evidenceStep =
                    report.Plan!.Steps.First(s => s.NodeId == "e");
                var holdStep = report.Plan!.Steps.First(s => s.NodeId == "h");

                var runId = Accept(fixture, report.Plan, doc);
                var parked = false;
                for (var i = 0; i < 8 && !parked; i++)
                {
                    Tick(fixture);
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    if (state != RunState.Running)
                    {
                        var journal = string.Join(" | ", Lines(runId));
                        fixture.Dispose();
                        return PhaseResult.Fail(
                            "the custom Run did not park mid-flight: " +
                            state + "; journal: " + journal);
                    }
                    parked = RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"step_waiting\"",
                        "\"step\":" + holdStep.Index);
                }
                if (!parked)
                {
                    var journal = string.Join(" | ", Lines(runId));
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the custom Run never reached its hold step: " + journal);
                }
                // Stop the Run first, as in the standard started-only
                // phase: a still-installed update tick would Poll the held
                // step into another step_waiting past the truncated tail —
                // the same two-steps-in-flight journal the §F4 rebuild
                // refuses.
                fixture.Host.Uninstall();
                TruncateThrough(
                    runId, "\"type\":\"step_started\"",
                    "\"step\":" + evidenceStep.Index);
                AssertStartedOnlyTail(runId, evidenceStep.Index);
                WriteState(
                    runId, fixture.SessionId, evidenceStep.Index.ToString());
                ArmReload();
                Crumb("phase-1 complete: validated custom step parked and " +
                    "truncated, runId=" + runId);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static PhaseResult StartedOnlyCustomPhaseAfter()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var persisted = ReadState();
                var runId = persisted[0];
                var evidenceStepText =
                    persisted.Length > 2 ? persisted[2] : "0";

                fixture = new RunnerHostFixture(StoreRoot);
                var snapshot = ProbeSnapshot(
                    (new StartOperation(), OperationOrigin.Standard),
                    (new FileEvidenceOperation(EvidenceOp, EvidenceFile),
                        OperationOrigin.Custom),
                    (new FlagWaitableOperation(HoldOp), OperationOrigin.Custom));
                fixture.SnapshotOverride = snapshot;
                fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                var registered = snapshot.Find(EvidenceOp, 1);
                Assert.IsNotNull(
                    registered, "the snapshot lost the custom Operation");
                Assert.AreEqual(
                    OperationOrigin.Custom, registered!.Origin,
                    "fixture error: the probe Operation must register Custom");

                AssertStartedOnlyTail(runId, int.Parse(evidenceStepText));
                var report = Recover(fixture);
                Assert.IsTrue(report.ExclusionHeld);
                Assert.AreEqual(
                    RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                    "the custom step's Completed verdict must resume the Run; " +
                    "journal: " + string.Join(" | ", Lines(runId)));
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"step_succeeded\"",
                        "\"step\":" + evidenceStepText, "\"reconciled\":true"),
                    "the custom Reconcilable is adopted through the identical " +
                    "§F5 path a standard Operation takes (SPEC §C2 test 5)");
                File.WriteAllText(ReleaseFlag, "released", s_utf8);
                RunUntilTerminal(fixture, runId);
                fixture.Dispose();
                Crumb("phase-2 complete: custom step adopted across the reload");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ── SPEC §F3/F4 item 5: mid-Waiting + truncated tail, real reload ─

        [UnityTest]
        [Timeout(240000)]
        public IEnumerator MidWaitingTruncatedTailResumesAcrossTheRealReload()
        {
            var before = MidWaitingPhase();
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

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

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupArtifacts();
                var redrive = MidWaitingPhase();
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = MidWaitingPhaseAfter();
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        private static PhaseResult MidWaitingPhase()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                fixture = new RunnerHostFixture(StoreRoot);
                fixture.Register(new FlagWaitableOperation(HoldOp), 'b');
                fixture.Install();
                var steps = new[]
                {
                    Step(0, "h", HoldOp, 'b', idempotency: Idempotency.Idempotent,
                        inputs: new Dictionary<string, InputSource>
                        {
                            ["flag"] = new Constant(
                                JsonValue.From(new JValue(ReleaseFlag))),
                        },
                        outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
                };
                var plan = RunnerHostFixture.Plan(WorkflowId, steps);
                var doc = Doc(new[] { ("h", HoldOp) });
                var runId = Accept(fixture, plan, doc);
                Tick(fixture);
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.Running)
                {
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    fixture.Dispose();
                    return PhaseResult.Fail("the waiting Run did not park: " + state);
                }
                // Stop the Run first: a still-installed update tick would
                // Poll the held step into another step_waiting AFTER the
                // truncated tail below — stranding the broken line in the
                // middle of the journal instead of at its end.
                fixture.Host.Uninstall();
                // The tail of an interrupted write — the record bytes whose
                // newline never landed (SPEC §F3): droppable, not corruption.
                File.AppendAllText(
                    RunnerHostFixture.JournalPath(StoreRoot, runId),
                    "{\"type\":\"step_succeeded\",\"seq\":99,", s_utf8);
                WriteState(runId, fixture.SessionId);
                ArmReload();
                Crumb("phase-1 complete: mid-Waiting + broken tail, runId=" + runId);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static PhaseResult MidWaitingPhaseAfter()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var runId = ReadState()[0];

                fixture = new RunnerHostFixture(StoreRoot);
                fixture.Register(new FlagWaitableOperation(HoldOp), 'b');
                fixture.Editor.IsCompiling = EditorApplication.isCompiling;

                var report = Recover(fixture);
                Assert.IsTrue(report.ExclusionHeld);
                Assert.IsTrue(
                    report.Diagnostics.Any(d => d.Code == "OW-STORE-002" &&
                        d.Severity == Severity.Warning),
                    "the dropped tail is reported as a warning: " +
                    Describe(report.Diagnostics));
                Assert.AreEqual(
                    RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                    "the Run resumes on the last complete record; journal: " +
                    string.Join(" | ", Lines(runId)));

                File.WriteAllText(ReleaseFlag, "released", s_utf8);
                RunUntilTerminal(fixture, runId);
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""),
                    "the mid-Waiting Run finished after the reload");
                Assert.IsTrue(
                    File.Exists(TokenSeenFile) &&
                    File.ReadAllText(TokenSeenFile) == "\"held\"",
                    "the persisted resumeToken reached the post-reload Poll");
                fixture.Dispose();
                Crumb("phase-2 complete: truncated tail dropped, Run finished");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
