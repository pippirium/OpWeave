#nullable enable
// T-W4.3-02 [Edit] tests: local:T-W4.3-02:resume-registry-session — SPEC
// §F4 test 3 (a mid-Run custom Operation used by a LATER step is edited
// or deleted; after the recompile + domain reload the resumed Run stops
// OW-RUN-011 NeedsReview without re-executing a step) and SPEC §E2 test 5
// (an approval-pending Run whose used custom Operation changed is
// Cancelled OW-RUN-011).
//
// The probe Operations are REAL project Operations: a test assembly under
// Assets/OpWeaveResumeChecksProbe/ registers through the production
// Registry build (RegistryBuildHost.Rebuild), so editing or deleting the
// probe source is a genuine Registry mutation across the reload, and the
// stored Plan binds real descriptor/implementation fingerprints.
//
// Reload mechanics are the SPEC §T.3 / L-14 pattern of
// RunRecoveryHostTests: a marker script + RecompileScripts + a domain
// sentinel; iterator locals do not survive, so every domain runs in a
// static phase method returning a serializable PhaseResult and everything
// crossing the reload sits in files under Library/OpWeaveResumeChecks/.
// T-W2.4-10: each persisted phase record carries a per-Editor-session
// nonce mirrored in SessionState, setup and teardown never yield a
// reload emitter (the queued cleanup compile drains at the test body's
// own boundary), and each boundary loop re-drives a phase a stale
// resume skipped — the ChangeSetDomainReloadTests shape.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.3-02:resume-registry-session</c> in Unity 6000.0
    /// EditMode — the host-side half of the SPEC §F4 items 3–4 resume
    /// checks over a real Registry rebuild and a real domain reload.
    /// </summary>
    internal sealed class ResumeRegistrySessionHostTests
    {
        private const string AlphaOpId = "proj.resumechecks.alpha";
        private const string BetaOpId = "proj.resumechecks.beta";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveResumeChecks");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string StateFile => Path.Combine(ProbeDir, "state.txt");

        private static string ProbeAssetDir => "Assets/OpWeaveResumeChecksProbe";
        private static string ProbeDiskDir => Path.Combine(ProjectRoot, ProbeAssetDir);
        private static string ProbeAssembly => "OpWeave.ResumeChecksProbe";
        private static string BetaSourcePath => Path.Combine(ProbeDiskDir, "ProbeBeta.cs");

        private const string ProbeAsmdefSource =
            "{\n    \"name\": \"OpWeave.ResumeChecksProbe\",\n" +
            "    \"rootNamespace\": \"OpWeave.ResumeChecksProbe\",\n" +
            "    \"references\": [\"OpWeave.Core\"],\n" +
            "    \"includePlatforms\": [\"Editor\"],\n" +
            "    \"autoReferenced\": false\n}\n";

        private const string WorkflowId = "example.resume.checks.host";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // Each boundary's probe artifact: the create-probes phase records
        // its nonce in probe-create.txt, the mid-Run phases record theirs
        // as state.txt's last line. A per-Editor-session token phase
        // stores in SessionState and writes into its file; the matching
        // post-boundary check proves the file belongs to this session's
        // phase — a stale resume that skipped the phase (no file, no
        // nonce), a record deleted by cleanup, and residue of an earlier
        // session (file yes, nonce no) each fail closed, never silently.
        private static string CreateProbeFile => Path.Combine(ProbeDir, "probe-create.txt");
        private const string ProbeSessionKey = "OpWeave.ResumeChecks.ProbeToken";

        // Bound on phase re-drive legs after a stale resume is detected
        // (the T-W2.4-06 self-heal); the this-session probe check is
        // what actually ends each boundary loop.
        private const int MaxReDriveLegs = 3;

        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeSessionKey, nonce);
            return nonce;
        }

        // The probe proves the phase ran in THIS Editor session: the file
        // must exist AND its last line must equal the nonce the phase
        // stored in SessionState — both survive the reload, so a skipped
        // phase and cross-session residue both fail the check and the
        // caller re-drives a real phase instead of skipping silently
        // into the post-reload phase.
        private static bool ProbeWrittenThisSession(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }
            var expected = SessionState.GetString(ProbeSessionKey, "");
            if (expected.Length == 0)
            {
                return false;
            }
            var lines = File.ReadAllLines(path);
            return lines.Length > 0 &&
                lines[lines.Length - 1] == expected;
        }

        // The fail-closed read of a phase's persisted run identity:
        // distinguishes a phase that never ran in this session (no
        // record, no armed nonce) from a record deleted after it ran —
        // and rejects residue from an earlier session outright.
        private static string ReadPersistedRunId(string what)
        {
            var expectedToken = SessionState.GetString(ProbeSessionKey, "");
            if (!File.Exists(StateFile))
            {
                Assert.Fail(
                    expectedToken.Length == 0
                        ? what + " never executed in this Editor session (" +
                            StateFile + " was never written; probeDir exists=" +
                            Directory.Exists(ProbeDir) + ")"
                        : what + "'s persisted run identity " + StateFile +
                            " was deleted after it was written");
            }
            var persisted = File.ReadAllLines(StateFile);
            if (expectedToken.Length == 0 || persisted.Length < 2 ||
                persisted[persisted.Length - 1] != expectedToken)
            {
                Assert.Fail(
                    what + "'s persisted run identity is stale — a previous " +
                    "Editor session or an abandoned leg wrote " + StateFile +
                    "; refusing to trust it");
            }
            return persisted[0];
        }

        private static void Crumb(string message) => Debug.Log("[W4302] " + message);

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

        // ── Probe sources ───────────────────────────────────────────────

        // One probe Operation per file. The variant only changes the
        // Descriptor summary — a real descriptorHash + implementationHash
        // move after the recompile.
        private static string ProbeOpSource(
            string className, string opId, bool waitable, int variant) =>
            "#nullable enable\n" +
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using OpWeave.Core;\n" +
            "using OpWeave.Core.Operations;\n\n" +
            "namespace OpWeave.ResumeChecksProbe\n{\n" +
            "    [OpWeaveOperation(\"" + opId + "\", 1)]\n" +
            "    public sealed class " + className + " : IOperation\n" +
            "    {\n" +
            "        public OperationDescriptor Descriptor { get; } = Op.Define(\"" + opId + "\", 1)\n" +
            "            .Summary(\"Resume checks probe, variant " + variant + ".\")\n" +
            "            .Output(\"done\", \"core.Bool\")\n" +
            "            .Effects(EffectNames.Read)\n" +
            "            .Idempotency(Idempotency.Idempotent)\n" +
            "            .Build()" +
            (waitable ? " with { ExecutionMode = ExecutionMode.Waitable }" : string.Empty) + ";\n" +
            "        public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>\n" +
            "            Array.Empty<Diagnostic>();\n" +
            "        public PreflightResult Preflight(StepContext ctx) =>\n" +
            "            new PreflightResult(Array.Empty<Diagnostic>(),\n" +
            "                Array.Empty<PlannedChange>(), JsonValue.Parse(\"{}\").Value!);\n" +
            "        public StepResult Execute(StepContext ctx)\n" +
            "        {\n" +
            (waitable
                ? "            return StepResult.Waiting(JsonValue.Parse(\"{}\").Value!, \"holding\", 10);\n"
                : "            ctx.SetOutput(\"done\", true);\n            return StepResult.Succeeded();\n") +
            "        }\n" +
            "        public StepResult Poll(StepContext ctx)\n" +
            "        {\n" +
            "            ctx.SetOutput(\"done\", true);\n" +
            "            return StepResult.Succeeded();\n" +
            "        }\n" +
            "        public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>\n" +
            "            new ReconcileResult.Unknown(\"This probe never reconciles.\");\n" +
            "        public void Cancel(StepContext ctx) { }\n" +
            "    }\n" +
            "}\n";

        private static void WriteProbeAssets()
        {
            Directory.CreateDirectory(ProbeDiskDir);
            File.WriteAllText(
                Path.Combine(ProbeDiskDir, ProbeAssembly + ".asmdef"), ProbeAsmdefSource);
            File.WriteAllText(
                Path.Combine(ProbeDiskDir, "ProbeAlpha.cs"),
                ProbeOpSource("ProbeAlphaOperation", AlphaOpId, waitable: false, variant: 0));
            WriteBeta(variant: 0);
        }

        private static void WriteBeta(int variant) =>
            File.WriteAllText(
                BetaSourcePath,
                ProbeOpSource("ProbeBetaOperation", BetaOpId, waitable: true, variant: variant));

        // The reload arm: the source mutation plus the marker compile the
        // iterator's RecompileScripts waits on.
        private static void EditBetaAndArm()
        {
            WriteBeta(variant: 1);
            ArmDomainSentinel();
            AssetDatabase.Refresh();
        }

        private static void DeleteBetaAndArm()
        {
            if (File.Exists(BetaSourcePath))
            {
                File.Delete(BetaSourcePath);
            }
            ArmDomainSentinel();
            AssetDatabase.Refresh();
        }

        // ── Fixture plumbing ────────────────────────────────────────────

        private static void ReleaseLeakedState()
        {
            var obtained = RunLockHandoff.TryObtain(StoreRoot);
            if (!obtained.Ok || obtained.Value is not { } held)
            {
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
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            var dirty = false;
            if (AssetDatabase.IsValidFolder(ProbeAssetDir))
            {
                AssetDatabase.DeleteAsset(ProbeAssetDir);
                dirty = true;
            }
            if (Directory.Exists(ProbeDiskDir))
            {
                Directory.Delete(ProbeDiskDir, recursive: true);
                dirty = true;
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
        // entirely (T-W2.4-10). The probe-folder deletion above queues
        // its compile under the runner's LockReloadAssemblies, so the
        // reload it needs drains at the test body's own
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
            if (!EditorUtility.scriptCompilationFailed && !EditorApplication.isCompiling)
            {
                RegistryBuildHost.RebuildProduction();
            }
            yield break;
        }

        // The production stage 1-8 Registry build over every loaded Editor
        // assembly — the probe Operations land as Custom candidates.
        private static RegistryBuildResult Rebuild()
        {
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.That(build.Stale, Is.False, "the probe registry build went stale");
            Assert.That(build.Snapshot, Is.Not.Null, "the build must publish a snapshot");
            return build;
        }

        private static RegisteredOperation Registered(RegistrySnapshot snapshot, string id)
        {
            var registered = snapshot.Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            return registered!;
        }

        // The Plan binds the registered fingerprints directly — no compiler
        // needed for a two-step linear probe.
        private static PlanStep Step(
            int index, string nodeId, RegisteredOperation registered,
            int next, IReadOnlyList<string>? effects = null) =>
            new PlanStep(
                index,
                nodeId,
                registered.Descriptor.Id,
                1,
                registered.DescriptorHash,
                registered.ImplementationHash,
                new Dictionary<string, InputSource>(),
                new Dictionary<string, string> { ["done"] = "core.Bool" },
                new Dictionary<string, int> { ["next"] = next },
                effects ?? (IReadOnlyList<string>)Array.Empty<string>(),
                30,
                new RetryPolicy(),
                Idempotency.Idempotent);

        private static CompiledPlan Plan(
            PlanStep[] steps, IReadOnlyList<ApprovalRequirement>? approvals = null)
        {
            var asks = approvals ?? (IReadOnlyList<ApprovalRequirement>)Array.Empty<ApprovalRequirement>();
            var stepList = new List<PlanStep>(steps);
            var inputs = MustParse("{}");
            var hashInput = JsonValue.From(new JObject
            {
                ["canonicalJsonVersion"] = 1,
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = WorkflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["executionInputs"] = PlanHashInputParts.ExecutionInputs(
                    Array.Empty<string>(), inputs.ToToken()),
                ["steps"] = PlanHashInputParts.Steps(stepList),
                ["transitions"] = PlanHashInputParts.Transitions(stepList),
                ["retry"] = PlanHashInputParts.Retry(stepList),
                ["timeouts"] = PlanHashInputParts.Timeouts(stepList),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(stepList, asks),
                ["usedOperationFingerprints"] =
                    PlanHashInputParts.OperationFingerprints(stepList),
                ["usedTypeFingerprints"] = new JArray(),
                ["usedCodecFingerprints"] = new JArray(),
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = "6000.0.85f1",
                    ["editorOS"] = "windows",
                    ["activeBuildTarget"] = "windows",
                    ["scriptingBackend"] = "mono",
                    ["apiCompatibilityLevel"] = "netstandard2.1",
                    ["scriptingDefineSymbols"] = new JArray(),
                },
            });
            return new CompiledPlan(
                "plan_" + Identifiers.NewUlid(),
                CanonicalJson.Sha256(hashInput.ToToken()),
                WorkflowId,
                new string('b', 64),
                new string('c', 64),
                inputs,
                steps,
                Array.Empty<PlanOutput>(),
                PlanBuilder.UnionEffects(stepList),
                asks,
                Array.Empty<PlanSourceMapEntry>(),
                hashInput);
        }

        private static JsonValue MustParse(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "fixture JSON was rejected: " + text);
            return parsed.Value!;
        }

        private static WorkflowDocument Doc() =>
            RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "a", Operation = AlphaOpId, Version = 1 },
                    new NodeData { Id = "b", Operation = BetaOpId, Version = 1 },
                },
                new[] { new ControlEdge("a", "b") });

        private static RunnerHostFixture HostOver(RegistrySnapshot snapshot)
        {
            var fixture = new RunnerHostFixture(StoreRoot) { SnapshotOverride = snapshot };
            return fixture;
        }

        private static void Tick(RunnerHostFixture fixture)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "the tick failed: " + Describe(tick.Diagnostics));
        }

        private static List<string> JournalLines(string runId) =>
            new List<string>(File.ReadAllLines(
                Path.Combine(StoreRoot, "Runs", runId, "journal.ndjson")));

        private static bool JournalHas(string runId, string fragment)
        {
            foreach (var line in JournalLines(runId))
            {
                if (line.Contains(fragment, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // ── SPEC §F4 test 3: edited custom Operation ────────────────────

        // Domain 1: write the probe assembly and arm — the iterator's
        // RecompileScripts then produces the real compile + reload.
        private static PhaseResult RunPhaseCreateProbes()
        {
            try
            {
                WriteProbeAssets();
                ArmDomainSentinel();
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(CreateProbeFile, ArmProbeNonce() + "\n");
                AssetDatabase.Refresh();
                Crumb("probe assembly written, sentinel armed");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("create-probes threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Domain 2: the probe Registry carries alpha + beta; the Plan binds
        // their real fingerprints; the Run starts, alpha completes and beta
        // (Waitable) holds the Run mid-Run — then beta's source is edited.
        private static PhaseResult RunPhaseRunMidway(bool delete)
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var build = Rebuild();
                var alpha = Registered(build.Snapshot!, AlphaOpId);
                var beta = Registered(build.Snapshot!, BetaOpId);
                Assert.That(beta.Origin, Is.EqualTo(OperationOrigin.Custom));

                var plan = Plan(new[]
                {
                    Step(0, "a", alpha, 1),
                    Step(1, "b", beta, -1),
                });
                fixture = HostOver(build.Snapshot!);
                fixture.Install();
                var accepted = fixture.Host.AcceptRun(
                    plan, Doc(), ActorKind.Agent, RunnerHostFixture.Options());
                if (!accepted.Ok)
                {
                    fixture.Dispose();
                    return PhaseResult.Fail("accept failed: " + Describe(accepted.Diagnostics));
                }
                var runId = accepted.Value!.RunId;
                Tick(fixture);
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.Running)
                {
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the probe Run is not mid-Run: " + state + "; journal: " +
                        string.Join(" | ", JournalLines(runId)));
                }
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(
                    StateFile, runId + "\n" + ArmProbeNonce() + "\n");
                if (delete)
                {
                    DeleteBetaAndArm();
                }
                else
                {
                    EditBetaAndArm();
                }
                Crumb("run " + runId + " mid-Run; beta " + (delete ? "deleted" : "edited"));
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("run-midway threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Domain 3: the new domain rebuilds the Registry — beta's recorded
        // fingerprints no longer resolve — and the recovery pass must end
        // the Run OW-RUN-011 NeedsReview, never resuming a step.
        private static PhaseResult RunPhaseVerifyStopped(bool expectMissing)
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var build = Rebuild();
                if (expectMissing)
                {
                    Assert.That(build.Snapshot!.Find(BetaOpId, 1), Is.Null,
                        "the deleted probe Operation must not register");
                }
                else
                {
                    Assert.That(
                        Registered(build.Snapshot!, BetaOpId).ImplementationHash,
                        Is.Not.Null, "the edited probe Operation re-registered");
                }
                var runId = ReadPersistedRunId("the mid-Run phase");

                fixture = HostOver(build.Snapshot!);
                var recovered = fixture.Host.RecoverRuns();
                if (!recovered.Ok || recovered.Value is not { } report)
                {
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the recovery pass failed: " + Describe(recovered.Diagnostics));
                }
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.NeedsReview)
                {
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the Run is not NeedsReview: " + state + "; journal: " +
                        string.Join(" | ", JournalLines(runId)));
                }
                if (!JournalHas(runId, "\"type\":\"step_needs_review\"") ||
                    !JournalHas(runId, "OW-RUN-011"))
                {
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "no OW-RUN-011 step_needs_review was journaled; journal: " +
                        string.Join(" | ", JournalLines(runId)));
                }
                if (JournalHas(runId, "\"type\":\"run_resumed\""))
                {
                    fixture.Dispose();
                    return PhaseResult.Fail("the resume was journaled anyway");
                }
                if (JournalHas(runId, "\"type\":\"step_succeeded\",\"step\":1") ||
                    JournalHas(runId, "\"step\":1,\"type\":\"step_succeeded\""))
                {
                    fixture.Dispose();
                    return PhaseResult.Fail("the changed step re-executed after the reload");
                }
                fixture.Dispose();
                Crumb("run " + runId + " stopped OW-RUN-011 NeedsReview");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("verify-stopped threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator EditedCustomOperationUsedByLaterStep_StopsWithOwRun011()
        {
            var phase1 = RunPhaseCreateProbes();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "phase-1 failed: " + phase1.Failure);

            // T-W2.4-06 stale-resume self-heal: a predecessor's captured
            // UTF resume PC applies to this fresh enumerator and lands it
            // at a post-yield state inside a boundary loop — with every
            // local reset. Each leg drains, then MUST pass the
            // this-session probe check: a record the phase never wrote
            // re-drives a real phase + compile + reload instead of
            // skipping silently into the post-reload phase.
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

                if (ProbeWrittenThisSession(CreateProbeFile) || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1) + " (create-probes)");
                CleanupArtifacts();
                var redrive = RunPhaseCreateProbes();
                Assert.That(redrive.Ok, Is.True,
                    "re-driven create-probes phase failed: " + redrive.Failure);
            }

            var phase2 = RunPhaseRunMidway(delete: false);
            Crumb("phase-2 ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True, "phase-2 failed: " + phase2.Failure);

            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the beta edit did not produce a domain reload");

                if (ProbeWrittenThisSession(StateFile) || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1) + " (mid-Run)");
                // The probe assets must stay registered for the re-drive
                // — only the abandoned leg's parked Run and its lock are
                // released before the phase helper re-runs.
                ReleaseLeakedState();
                var redrive = RunPhaseRunMidway(delete: false);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven mid-Run phase failed: " + redrive.Failure);
            }

            var phase3 = RunPhaseVerifyStopped(expectMissing: false);
            Crumb("phase-3 ok=" + phase3.Ok + " failure=" + phase3.Failure);
            Assert.That(phase3.Ok, Is.True, "phase-3 failed: " + phase3.Failure);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator DeletedCustomOperationUsedByLaterStep_StopsWithOwRun011()
        {
            var phase1 = RunPhaseCreateProbes();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "phase-1 failed: " + phase1.Failure);

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

                if (ProbeWrittenThisSession(CreateProbeFile) || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1) + " (create-probes)");
                CleanupArtifacts();
                var redrive = RunPhaseCreateProbes();
                Assert.That(redrive.Ok, Is.True,
                    "re-driven create-probes phase failed: " + redrive.Failure);
            }

            var phase2 = RunPhaseRunMidway(delete: true);
            Crumb("phase-2 ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True, "phase-2 failed: " + phase2.Failure);

            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the beta deletion did not produce a domain reload");

                if (ProbeWrittenThisSession(StateFile) || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1) + " (mid-Run)");
                ReleaseLeakedState();
                var redrive = RunPhaseRunMidway(delete: true);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven mid-Run phase failed: " + redrive.Failure);
            }

            var phase3 = RunPhaseVerifyStopped(expectMissing: true);
            Crumb("phase-3 ok=" + phase3.Ok + " failure=" + phase3.Failure);
            Assert.That(phase3.Ok, Is.True, "phase-3 failed: " + phase3.Failure);
        }

        // ── SPEC §E2 test 5: approval-pending + changed custom op ───────

        // Domain 2 for the approval variant: the Plan asks before beta runs,
        // so the Run parks AwaitingApproval — then beta's source is edited.
        private static PhaseResult RunPhaseAwaitApproval()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var build = Rebuild();
                var alpha = Registered(build.Snapshot!, AlphaOpId);
                var beta = Registered(build.Snapshot!, BetaOpId);

                var plan = Plan(
                    new[]
                    {
                        Step(0, "a", alpha, 1),
                        Step(1, "b", beta, -1, effects: new[] { "asset.delete" }),
                    },
                    new[]
                    {
                        new ApprovalRequirement("b", "asset.delete", BetaOpId + " v1"),
                    });
                fixture = HostOver(build.Snapshot!);
                fixture.Install();
                var accepted = fixture.Host.AcceptRun(
                    plan, Doc(), ActorKind.Agent, RunnerHostFixture.Options());
                if (!accepted.Ok)
                {
                    fixture.Dispose();
                    return PhaseResult.Fail("accept failed: " + Describe(accepted.Diagnostics));
                }
                var runId = accepted.Value!.RunId;
                if (accepted.Value!.State != RunState.AwaitingApproval)
                {
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the Run is not AwaitingApproval: " + accepted.Value!.State);
                }
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(
                    StateFile, runId + "\n" + ArmProbeNonce() + "\n");
                EditBetaAndArm();
                Crumb("run " + runId + " AwaitingApproval; beta edited");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("await-approval threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Domain 3: re-attachment brings the approval-pending Run back, and
        // the approval sweep cancels it OW-RUN-011 — the §E2 item 5 rule.
        private static PhaseResult RunPhaseVerifyCancelled()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var build = Rebuild();
                var runId = ReadPersistedRunId("the await-approval phase");

                fixture = HostOver(build.Snapshot!);
                var recovered = fixture.Host.RecoverRuns();
                if (!recovered.Ok || recovered.Value is not { } report)
                {
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the recovery pass failed: " + Describe(recovered.Diagnostics));
                }
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.AwaitingApproval)
                {
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the Run did not re-attach AwaitingApproval: " + state);
                }
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                var tick = fixture.Host.Tick();
                if (!tick.Ok)
                {
                    fixture.Dispose();
                    return PhaseResult.Fail("the tick failed: " + Describe(tick.Diagnostics));
                }
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.Cancelled)
                {
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the Run was not cancelled: " + state + "; journal: " +
                        string.Join(" | ", JournalLines(runId)));
                }
                if (!JournalHas(runId, "\"type\":\"run_finished\"") ||
                    !JournalHas(runId, "OW-RUN-011"))
                {
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "no OW-RUN-011 run_finished was journaled; journal: " +
                        string.Join(" | ", JournalLines(runId)));
                }
                fixture.Dispose();
                Crumb("run " + runId + " cancelled OW-RUN-011");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("verify-cancelled threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ChangedCustomOperationWhileAwaitingApproval_CancelsWithOwRun011()
        {
            var phase1 = RunPhaseCreateProbes();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "phase-1 failed: " + phase1.Failure);

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

                if (ProbeWrittenThisSession(CreateProbeFile) || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1) + " (create-probes)");
                CleanupArtifacts();
                var redrive = RunPhaseCreateProbes();
                Assert.That(redrive.Ok, Is.True,
                    "re-driven create-probes phase failed: " + redrive.Failure);
            }

            var phase2 = RunPhaseAwaitApproval();
            Crumb("phase-2 ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True, "phase-2 failed: " + phase2.Failure);

            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the beta edit did not produce a domain reload");

                if (ProbeWrittenThisSession(StateFile) || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1) + " (await-approval)");
                ReleaseLeakedState();
                var redrive = RunPhaseAwaitApproval();
                Assert.That(redrive.Ok, Is.True,
                    "re-driven await-approval phase failed: " + redrive.Failure);
            }

            var phase3 = RunPhaseVerifyCancelled();
            Crumb("phase-3 ok=" + phase3.Ok + " failure=" + phase3.Failure);
            Assert.That(phase3.Ok, Is.True, "phase-3 failed: " + phase3.Failure);
        }
    }
}
