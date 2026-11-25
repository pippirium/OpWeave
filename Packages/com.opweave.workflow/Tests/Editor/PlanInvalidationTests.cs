#nullable enable

// T-W2.2-05 [Edit] tests: local:T-W2.2-05:plan-invalidation — SPEC §T.1
// G-04 and §T.2 AT-F05 end to end (SPEC §D0.1, §B0.1, §M.5). A Plan built
// through the production Registry build → WorkflowCompiler → PlanBuilder
// path over real test-created fixture assemblies is re-compiled after each
// mutation and its planHash compared against the stored baseline:
//
//   * editing Operation B's body inside the USED assembly invalidates the
//     Plan even though the Plan uses only Operation A (the conservative
//     compiled-bytes rule of SPEC §C3 rule 6 / §D0.1);
//   * editing Operation C in an unrelated OTHER assembly keeps the Plan —
//     the registryHash moved, proving a registryHash-only change does not
//     invalidate (SPEC §D0.1);
//   * editing the used Codec's defining assembly invalidates (SPEC §B0.1);
//   * every invalidation surfaces at execute as a planHash mismatch — a
//     stale expectedPlanHash is refused OW-RUN-003 (SPEC §D7 item 7) while
//     a still-valid Plan accepts.
//
// Fixture layout (written under Assets/OpWeavePlanInvalidationProbe/):
//   Used/  OpWeave.PlanInvalidation.Used   — Operations alpha (A, declares a
//                                          proj.planinvalidation.scalar port)
//                                          and beta (B, never used by the WF).
//   Unused/ OpWeave.PlanInvalidation.Unused — Operation gamma (C) only.
//   Types/ OpWeave.PlanInvalidation.Types   — the [OpWeaveTypeProvider]
//                                          offering proj.planinvalidation.scalar
//                                          and its Codec.
//
// State crossing a domain reload lives only in files under
// Library/OpWeavePlanInvalidationProbe/ (the domain-sentinel pattern of
// SPEC §T.3 — WaitForDomainReload is deliberately not used; on 6000.0.85f1
// the reload can complete inside RecompileScripts' own wait, after which
// WaitForDomainReload.Perform() throws). Both roots are removed by
// [UnityTearDown]; nothing tracked is modified.
//
// Iterator hygiene (L-14, the pattern of CoordinatorRunLockReloadTests):
// UTF resumes a [UnityTest] across a reload by serializing the
// compiler-generated iterator object, and every local a non-serializable
// field became — a RegistryBuildResult, a CompiledPlan, a Result<T>, the
// baseline tuple, a `() =>` display class — comes back null or default.
// Dereferencing one inside the resurrected MoveNext is the opaque
// NullReferenceException the previous head died to, so the body holds no
// cross-domain work at all: each domain runs inside a plain static phase
// method that re-derives every input (Registry rebuild, baseline file,
// Plan re-compile) and hands back a serializable PhaseResult reporting
// its own exception message and stack. The iterator only calls phases,
// yields, and asserts on their text.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.2-05:plan-invalidation — the G-04 / AT-F05 fixture matrix
    /// across real compiles and domain reloads.
    /// </summary>
    internal sealed class PlanInvalidationTests
    {
        private const string AlphaOpId = "proj.planinvalidation.alpha";
        private const string BetaOpId = "proj.planinvalidation.beta";
        private const string GammaOpId = "proj.planinvalidation.gamma";
        private const string ScalarTypeId = "proj.planinvalidation.scalar";

        private const string ProbeAssetDir = "Assets/OpWeavePlanInvalidationProbe";
        private const string UsedAssembly = "OpWeave.PlanInvalidation.Used";
        private const string UnusedAssembly = "OpWeave.PlanInvalidation.Unused";
        private const string TypesAssembly = "OpWeave.PlanInvalidation.Types";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string StateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeavePlanInvalidationProbe");

        private static string ProbeDiskDir => Path.Combine(ProjectRoot, ProbeAssetDir);
        private static string UsedDir => Path.Combine(ProbeDiskDir, "Used");
        private static string UnusedDir => Path.Combine(ProbeDiskDir, "Unused");
        private static string TypesDir => Path.Combine(ProbeDiskDir, "Types");

        private static string BaselineFile => Path.Combine(StateDir, "baseline.txt");

        // ── Domain sentinel (SPEC §T.3) ──────────────────────────────────

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // ── Probe sources ────────────────────────────────────────────────

        private static string AsmdefSource(string name) =>
            "{\n    \"name\": \"" + name + "\",\n" +
            "    \"rootNamespace\": \"OpWeave.PlanInvalidationProbe\",\n" +
            "    \"references\": [\"OpWeave.Core\"],\n" +
            "    \"includePlatforms\": [\"Editor\"],\n" +
            "    \"autoReferenced\": false\n}\n";

        // One Operation fixture shape. The variant marker only ever changes
        // an Execute-body constant — Descriptor-identical (SPEC §C3 test 4's
        // implementationChanged precondition), compiled-bytes different.
        private static string OpSource(
            string className, string opId, bool withScalarPort, int variant) =>
            "#nullable enable\n" +
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using OpWeave.Core;\n" +
            "using OpWeave.Core.Operations;\n\n" +
            "namespace OpWeave.PlanInvalidationProbe\n{\n" +
            "    [OpWeaveOperation(\"" + opId + "\", 1)]\n" +
            "    public sealed class " + className + " : IOperation\n" +
            "    {\n" +
            "        public OperationDescriptor Descriptor { get; } = Op.Define(\"" + opId + "\", 1)\n" +
            "            .Summary(\"Plan invalidation probe operation.\")\n" +
            (withScalarPort
                ? "            .Input(\"scalar\", \"" + ScalarTypeId + "\", required: false)\n"
                : string.Empty) +
            "            .Output(\"done\", \"core.Bool\")\n" +
            "            .Effects(EffectNames.Read)\n" +
            "            .Idempotency(Idempotency.Idempotent)\n" +
            "            .Build();\n" +
            "        public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>\n" +
            "            Array.Empty<Diagnostic>();\n" +
            "        public PreflightResult Preflight(StepContext ctx) =>\n" +
            "            new PreflightResult(Array.Empty<Diagnostic>(),\n" +
            "                Array.Empty<PlannedChange>(), JsonValue.Parse(\"{}\").Value!);\n" +
            "        public StepResult Execute(StepContext ctx)\n" +
            "        {\n" +
            "            ctx.SetOutput(\"done\", " + (variant == 0 ? "true" : "false") + ");\n" +
            "            return StepResult.Succeeded();\n" +
            "        }\n" +
            "        public StepResult Poll(StepContext ctx) =>\n" +
            "            throw new NotSupportedException(\"Immediate operations are never polled.\");\n" +
            "        public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>\n" +
            "            new ReconcileResult.Unknown(\"This probe never reconciles.\");\n" +
            "        public void Cancel(StepContext ctx) { }\n" +
            "    }\n" +
            "}\n";

        // The used type + its Codec in their own assembly, so the Codec's
        // compiled-bytes fingerprint moves independently of both Operation
        // DLLs. The variant edits only the codec class's marker literal.
        private static string TypesSource(int variant) =>
            "#nullable enable\n" +
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using OpWeave.Core;\n\n" +
            "namespace OpWeave.PlanInvalidationProbe\n{\n" +
            "    [OpWeaveTypeProvider]\n" +
            "    public sealed class ProbeScalarProvider : ITypeProvider\n" +
            "    {\n" +
            "        public IEnumerable<TypeDefinition> GetTypes()\n" +
            "        {\n" +
            "            yield return new TypeDefinition(\n" +
            "                \"" + ScalarTypeId + "\",\n" +
            "                TypeKind.Primitive,\n" +
            "                0,\n" +
            "                new[] { \"core.String\" },\n" +
            "                null,\n" +
            "                \"Plan invalidation probe scalar type.\",\n" +
            "                new ProbeScalarCodec());\n" +
            "        }\n" +
            "    }\n\n" +
            "    public sealed class ProbeScalarCodec : IValueCodec\n" +
            "    {\n" +
            "        public static string Marker() => \"codec-v" + variant + "\";\n\n" +
            "        public IReadOnlyList<Diagnostic> Validate(\n" +
            "            JsonValue value, PortConstraints? constraints) =>\n" +
            "            Array.Empty<Diagnostic>();\n\n" +
            "        public JsonValue Normalize(JsonValue value) => value;\n" +
            "    }\n" +
            "}\n";

        private static void WriteProbeAssets()
        {
            Directory.CreateDirectory(UsedDir);
            Directory.CreateDirectory(UnusedDir);
            Directory.CreateDirectory(TypesDir);
            File.WriteAllText(
                Path.Combine(UsedDir, UsedAssembly + ".asmdef"), AsmdefSource(UsedAssembly));
            File.WriteAllText(
                Path.Combine(UnusedDir, UnusedAssembly + ".asmdef"), AsmdefSource(UnusedAssembly));
            File.WriteAllText(
                Path.Combine(TypesDir, TypesAssembly + ".asmdef"), AsmdefSource(TypesAssembly));
            File.WriteAllText(Path.Combine(UsedDir, "ProbeOpAlpha.cs"),
                OpSource("ProbeAlphaOperation", AlphaOpId, withScalarPort: true, variant: 0));
            File.WriteAllText(Path.Combine(UsedDir, "ProbeOpBeta.cs"),
                OpSource("ProbeBetaOperation", BetaOpId, withScalarPort: false, variant: 0));
            File.WriteAllText(Path.Combine(UnusedDir, "ProbeOpGamma.cs"),
                OpSource("ProbeGammaOperation", GammaOpId, withScalarPort: false, variant: 0));
            File.WriteAllText(Path.Combine(TypesDir, "ProbeTypes.cs"), TypesSource(variant: 0));
        }

        private static void EditBeta(int variant) =>
            File.WriteAllText(Path.Combine(UsedDir, "ProbeOpBeta.cs"),
                OpSource("ProbeBetaOperation", BetaOpId, withScalarPort: false, variant: variant));

        private static void EditGamma(int variant) =>
            File.WriteAllText(Path.Combine(UnusedDir, "ProbeOpGamma.cs"),
                OpSource("ProbeGammaOperation", GammaOpId, withScalarPort: false, variant: variant));

        private static void EditCodec(int variant) =>
            File.WriteAllText(Path.Combine(TypesDir, "ProbeTypes.cs"), TypesSource(variant));

        private static bool CleanupProbeArtifacts()
        {
            var dirty = false;
            if (Directory.Exists(StateDir))
            {
                Directory.Delete(StateDir, recursive: true);
            }
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
            return dirty;
        }

        // ── T-W2.4-08: conditional drain + session-token probe ─────────────
        //
        // The [UnitySetUp]/[UnityTearDown] reload emitters this replaces —
        // `yield return new RecompileScripts(false)` on a dirty cleanup —
        // opened two stale-PC vectors: its Perform unlocks the runner's
        // run-wide assembly-reload lock even when nothing is queued, and
        // UTF's PrepareForDomainReload records the CURRENT test
        // enumerator's program counter on every ExpectDomainReload yield —
        // including one emitted inside setup/teardown — and applies that
        // stale PC to the next test method's fresh enumerator, jumping it
        // past a whole phase into a silent PASS. The drain below crosses a
        // pending compile/reload only at the bounded DrainedScriptReload
        // boundary, once the pending state is proven, and hands the next
        // fixture a quiet Editor; the per-phase session-token probe turns
        // a stale-PC landing that skipped a phase into a loud PhaseResult
        // failure instead of a skipped one (the At01AgentStepsTests /
        // ChangeSetDomainReloadTests sanctioned pattern).

        // The bound shared by the drain waits — the suite-wide convention
        // (240 s) — and the sustained-idle window the drain demands before
        // it trusts a quiet Editor (the QuietSettle convention).
        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // A per-Editor-session token stored in SessionState and written
        // into the probe file beside each completed phase's label; the
        // next phase's gate reads both to prove the phase chain belongs
        // to this session — never residue of an earlier run. The file
        // lives under StateDir, which CleanupProbeArtifacts already
        // removes.
        private const string ProbeTokenKey =
            "OpWeave.W2408.PlanInvalidation.ProbeToken";

        private static string ProbeTokenFile =>
            Path.Combine(StateDir, "reload-probe.txt");

        // Stamps the token file with the just-completed phase label. The
        // session token is minted once — its presence in SessionState at
        // the next phase's gate proves that gate runs in the same Editor
        // session the earlier phase wrote it in.
        private static void WriteProbeToken(string phase)
        {
            var token = SessionState.GetString(ProbeTokenKey, "");
            if (token.Length == 0)
            {
                token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
            }
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(ProbeTokenFile, phase + "\n" + token + "\n");
            Crumb("probe stamped: " + phase +
                " reloads=" + DomainReloadCounter.Current);
        }

        // The phase-entry gate: returns a failing PhaseResult when the
        // token file does not show expectedPhase stamped this session —
        // so a stale-resume landing that skipped phases reports loudly,
        // never a silent continuation.
        private static PhaseResult? VerifyProbeToken(
            string expectedPhase, string forPhase)
        {
            var token = SessionState.GetString(ProbeTokenKey, "");
            if (token.Length == 0)
            {
                return PhaseResult.Fail(forPhase +
                    ": phase-1 never executed in this Editor session " +
                    "(no session probe token)");
            }
            if (!File.Exists(ProbeTokenFile))
            {
                return PhaseResult.Fail(forPhase +
                    ": the persisted reload probe " + ProbeTokenFile +
                    " was deleted after " + expectedPhase + " wrote it");
            }
            var lines = File.ReadAllLines(ProbeTokenFile);
            if (lines.Length < 2 || lines[1] != token)
            {
                return PhaseResult.Fail(forPhase +
                    ": the persisted reload probe is stale — a previous " +
                    "session wrote " + ProbeTokenFile);
            }
            if (lines[0] != expectedPhase)
            {
                return PhaseResult.Fail(forPhase +
                    ": the probe file shows " + lines[0] + ", expected " +
                    expectedPhase + " — an intervening phase never executed");
            }
            Crumb("probe verified: " + forPhase + " sees " + expectedPhase +
                " reloads=" + DomainReloadCounter.Current);
            return null;
        }

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the drain
        // reaches it through reflection (the PackageAddUpmTests probe). A
        // reload already armed behind the runner's reload lock surfaces
        // here while isCompiling is false — the deferred state an
        // isCompiling-only check misses and hands to the next fixture.
        private const string ScriptReloadProbeType =
            "UnityEditorInternal.InternalEditorUtility";
        private const string ScriptReloadProbeMethod =
            "IsScriptReloadRequested";

        private static readonly MethodInfo? s_scriptReloadRequested =
            typeof(EditorApplication).Assembly
                .GetType(ScriptReloadProbeType)
                ?.GetMethod(
                    ScriptReloadProbeMethod,
                    BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

        // Non-null and shaped as OneTimeSetUp proved — the fixture does
        // not run at all on a Unity version whose probe is absent.
        private static bool ScriptReloadRequested() =>
            (bool)s_scriptReloadRequested!.Invoke(null, null)!;

        // The state a queued compile/reload reports while the runner's
        // lock holds it: a running script compile always ends in a reload
        // queued behind the lock, and a requested reload is already
        // pending — either is one the drain must cross, never forward.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        // The pending probe the drain trusts is only as strong as its
        // shape: prove it once up front (the At01AgentStepsTests pattern)
        // so the fixture fails here — before any test — instead of
        // silently degrading to an isCompiling-only check that re-opens
        // the missed-request race the probe exists to close.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var probe = s_scriptReloadRequested;
            if (probe is null ||
                !probe.IsStatic ||
                probe.GetParameters().Length != 0 ||
                probe.ReturnType != typeof(bool))
            {
                Assert.Fail(
                    "the domain-reload pending probe is unavailable on " +
                    "this Unity version — the setup/teardown cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // The contract the fixture hands the next suite test: no pending
        // script reload, no compile or import in flight and a clean
        // compile — the failure mode this drain replaces was the
        // `yield return new RecompileScripts(false)` cleanup emitter: its
        // Perform unlocks the runner's reload lock even when nothing is
        // queued, and with no reload following the lock stays released,
        // so the next fixture's real reload crosses mid-pump on the stale
        // armed state. Here a pending compile or requested reload is
        // crossed only at DrainedScriptReload — the bounded instruction
        // that unlocks once the pending state is proven — and everything
        // else settles on bounded plain frames. A queued compile surfaces
        // as isCompiling a frame or two after Refresh returns, so the
        // wait only trusts a sustained quiet spell (the QuietSettle
        // convention): quiet flags reset on every busy frame and a
        // periodic Refresh flushes the import queue so a queued compile
        // surfaces inside the window, never behind it. The instruction
        // the nested enumerator produces is yielded DIRECTLY from the
        // setup/teardown's own top level through the caller's MoveNext
        // relay — a nested yield would never reach Perform — and the whole
        // sequence is idempotent: a crossed reload resumes the caller's
        // enumerator at its yield with every local reset, so the caller
        // rebuilds the drain when it arrives null rather than relaying a
        // dead reference.
        private static IEnumerator DrainEditorState(string phase)
        {
            AssetDatabase.Refresh();
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var nextRefreshAt = double.NegativeInfinity;
            var quietAt = -1.0;
            for (;;)
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "PlanInvalidationTests." + phase +
                        " timed out after " + WaitTimeoutSec +
                        " s draining the armed editor state " +
                        "(scriptReloadRequested: " + ScriptReloadRequested() +
                        ", isCompiling: " + EditorApplication.isCompiling +
                        ", isUpdating: " + EditorApplication.isUpdating + ")");
                }
                if (ReloadPending())
                {
                    // A running or requested compile ends in a reload
                    // queued behind the runner's lock — cross it at the
                    // bounded sanctioned boundary, never hand it on.
                    quietAt = -1.0;
                    yield return new DrainedScriptReload(
                        phase, WaitTimeoutSec);
                    continue;
                }
                if (EditorApplication.isUpdating)
                {
                    // An import still in flight may surface a compile —
                    // no quiet counts while it runs.
                    quietAt = -1.0;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietAt >=
                         QuietSettleSec)
                {
                    break;
                }
                if (EditorApplication.timeSinceStartup >= nextRefreshAt)
                {
                    AssetDatabase.Refresh();
                    nextRefreshAt = EditorApplication.timeSinceStartup + 1.0;
                }
                yield return null;
            }
            AssertDrainedEditorState(phase);
        }

        // The terminal clean-state proof: the project compiled clean, no
        // compile in flight and no script reload still armed behind the
        // runner's lock — a leftover here is this fixture's failure,
        // never a later suite index's.
        private static void AssertDrainedEditorState(string phase)
        {
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "PlanInvalidationTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "PlanInvalidationTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "PlanInvalidationTests." + phase +
                ": a script reload is still pending");
        }

        // The bounded reload-crossing instruction — the
        // WaitForDomainReload.Perform wait with a deadline, run by UTF
        // through the IEditModeTestYieldInstruction interface so
        // ExpectDomainReload arms the runner's reload bookkeeping
        // exactly as with UTF's own instructions. It is yielded only
        // after ReloadPending() proved a real reload is armed, so the
        // unlock below always meets a queued reload and can never strand
        // the lock released on an idle Editor — the failure mode of the
        // unconditional RecompileScripts(false) unlock it replaces. The
        // one path where no reload can follow — a failed compile —
        // re-locks before failing.
        private sealed class DrainedScriptReload : IEditModeTestYieldInstruction
        {
            private readonly string m_phase;
            private readonly double m_timeoutSec;

            internal DrainedScriptReload(string phase, double timeoutSec)
            {
                m_phase = phase;
                m_timeoutSec = timeoutSec;
            }

            public bool ExpectDomainReload => true;
            public bool ExpectedPlaymodeState => false;

            public IEnumerator Perform()
            {
                // UTF yields the same first frame: the runner must see
                // the yielded instruction and arm its expected-domain-
                // reload bookkeeping before the reload can start.
                yield return null;

                var deadline =
                    EditorApplication.timeSinceStartup + m_timeoutSec;

                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();

                // Wait out the armed state as WaitForDomainReload.Perform
                // does — while a script reload stays requested or a
                // compile runs — but bounded: the reload itself kills
                // the domain mid-wait, so a deadline expiry can only
                // mean the proven pending state never crossed.
                while (ReloadPending())
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "PlanInvalidationTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "PlanInvalidationTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup cleanup enter");
            var dirty = CleanupProbeArtifacts();
            Crumb("setup cleanup exit; assetsDirty=" + dirty);
            // UTF resumes a reload-crossed setup enumerator at its yield
            // with every local reset — a null drain is that resume, not a
            // fresh start: rebuild the idempotent drain and relay on
            // instead of touching the dead reference.
            IEnumerator? drain = null;
            for (;;)
            {
                drain ??= DrainEditorState(nameof(UnitySetUp));
                if (!drain.MoveNext())
                {
                    break;
                }
                yield return drain.Current;
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            s_domainSentinel = null;
            Crumb("teardown cleanup enter");
            var dirty = CleanupProbeArtifacts();
            Crumb("teardown cleanup exit; assetsDirty=" + dirty);
            // UTF resumes a reload-crossed teardown enumerator at its
            // yield with every local reset — a null drain is that resume,
            // not a fresh start: rebuild the idempotent drain and relay
            // on instead of touching the dead reference.
            IEnumerator? drain = null;
            for (;;)
            {
                drain ??= DrainEditorState(nameof(UnityTearDown));
                if (!drain.MoveNext())
                {
                    break;
                }
                yield return drain.Current;
            }
            if (!EditorUtility.scriptCompilationFailed && !EditorApplication.isCompiling)
            {
                RegistryBuildHost.RebuildProduction();
            }
        }

        // ── Compile / execute plumbing ───────────────────────────────────

        // A minimal StaticContext (Core hosts no implementation, SPEC §0.6
        // SG-06); the probe Operations' ValidateStatic reports nothing.
        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            internal StubStaticContext(
                string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("The probe Operations read no static port.");
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

        private static WorkflowDocument ProbeDoc() =>
            RunnerHostFixture.Doc(
                "test.planinvalidation.probe",
                new[]
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData { Id = "alpha", Operation = AlphaOpId, Version = 1 },
                },
                new[] { new ControlEdge("start", "alpha") });

        // The production stage 1–8 build over every loaded Editor assembly —
        // the probe Operations land as Custom candidates, the probe provider
        // binds proj.planinvalidation.scalar. No diff recorder: registry.json
        // persistence is T-W2.2-04's surface, not this ticket's.
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

        private static RegisteredOperation Registered(
            RegistryBuildResult build, string id)
        {
            var registered = build.Snapshot!.Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            return registered!;
        }

        // The production validate → Build Plan chain (SPEC §D1 → §D7): real
        // Resolve, D2–D6, weak fingerprints and SG-11 environment through
        // UnityPlanProvenance, so the planHash binds the compiled bytes of
        // the used Operation's / used Type's / used Codec's defining DLLs.
        private static CompiledPlan CompileProbePlan(RegistryBuildResult build)
        {
            var report = new WorkflowCompiler(
                    build.Snapshot!,
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    new UnityTypeProbe(),
                    UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers))
                .Validate(new ValidationRequest(
                    ProbeDoc(), null, ValidationMode.Static, ActorKind.Agent));
            Assert.That(report.Ok, Is.True,
                () => "compile failed: " + Describe(report.Errors));
            Assert.That(report.Plan, Is.Not.Null, "an error-free compile must produce the Plan");
            return report.Plan!;
        }

        // SPEC §D7 item 7: the caller binds the Plan it validated through
        // expectedPlanHash — a stale hash is refused OW-RUN-003 before any
        // persistence; a still-valid Plan is accepted.
        private static Result<RunHandle> AcceptWithExpectedPlanHash(
            CompiledPlan plan, string expectedPlanHash)
        {
            using var fixture = new RunnerHostFixture(Path.Combine(
                StateDir, "runhost-" + Guid.NewGuid().ToString("N")));
            return fixture.Host.AcceptRun(
                plan, ProbeDoc(), ActorKind.Agent, new AcceptOptions(expectedPlanHash, false));
        }

        private static void AssertPlanHashMismatch(
            Result<RunHandle> accepted, string expectedPlanHash, CompiledPlan plan)
        {
            Assert.That(plan.PlanHash, Is.Not.EqualTo(expectedPlanHash),
                "precondition: the re-compiled Plan must differ before the refusal means anything");
            Assert.That(accepted.Ok, Is.False,
                () => "a stale expectedPlanHash must refuse the Run: " +
                    (accepted.Ok ? "accepted" : Describe(accepted.Diagnostics)));
            Assert.That(accepted.Diagnostics.Select(d => d.Code),
                Does.Contain(ErrorCodes.OW_RUN_003),
                () => "expected OW-RUN-003, got: " + Describe(accepted.Diagnostics));
        }

        private static (string PlanHash, string RegistryHash, string AlphaImplementationHash)
            ReadBaseline()
        {
            var parts = File.ReadAllText(BaselineFile).Trim().Split('|');
            return (parts[0], parts[1], parts[2]);
        }

        // A domain reload drops every local of the test iterator, so the used
        // Operation's compiled-bytes fingerprint rides in the baseline file
        // next to planHash / registryHash for the cross-domain comparison.
        private static void WriteBaseline(RegistryBuildResult build, CompiledPlan plan)
        {
            var alphaStep = plan.Steps.Single(s => s.OperationId == AlphaOpId);
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(
                BaselineFile,
                plan.PlanHash + "|" + build.Snapshot!.RegistryHash + "|" +
                    alphaStep.ImplementationHash);
        }

        // ── local:T-W2.2-05:plan-invalidation ────────────────────────────

        // The plain serializable result a domain phase hands back to the
        // iterator — UTF's post-reload resurrection of the iterator can
        // carry it, and a phase failure reports its own text instead of an
        // opaque NRE inside the resurrected MoveNext.
        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            public static PhaseResult Pass() => new PhaseResult { Ok = true };
            public static PhaseResult Fail(string message) =>
                new PhaseResult { Ok = false, Failure = message };
        }

        // A "[W2205] " breadcrumb so editmode.log/editmode.xml shows exactly
        // which phase the run reached — the previous head died inside the
        // accept with a bare NullReferenceException and no other output.
        private static void Crumb(string message) => Debug.Log("[W2205] " + message);

        // Domain 1: write the three probe assemblies and arm the sentinel —
        // the iterator's RecompileScripts then forces the real compile +
        // domain reload.
        private static PhaseResult RunPhaseCreateProbes()
        {
            try
            {
                WriteProbeAssets();
                ArmDomainSentinel();
                WriteProbeToken("phase-1");
                Crumb("phase-1 complete: probe assemblies written, sentinel armed");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-1 threw " + e.GetType().Name + ": " + e.Message);
                return PhaseResult.Fail("phase-1 threw an unexpected exception: " + e);
            }
        }

        // Domain 2: the probe Operations and TypeProvider registered; the
        // baseline Plan binds the compiled bytes of the Used and Types DLLs
        // (and the scalar Codec identity). Ends by editing Operation B's
        // body — the Plan uses A alone, but B shares A's defining DLL, so
        // the compiled-bytes fingerprint of the used Operation moves
        // (SPEC §D0.1: no unused-operation independence guarantee inside one
        // DLL) — and arming the sentinel so the iterator's yield produces
        // the next domain.
        private static PhaseResult RunPhaseBaseline()
        {
            var probeFailure = VerifyProbeToken("phase-1", "phase-2");
            if (probeFailure is not null)
            {
                Crumb("phase-2 probe gate failed: " + probeFailure.Failure);
                return probeFailure;
            }
            try
            {
                var build = Rebuild();
                var alpha = Registered(build, AlphaOpId);
                var beta = Registered(build, BetaOpId);
                var gamma = Registered(build, GammaOpId);
                Assert.That(alpha.Origin, Is.EqualTo(OperationOrigin.Custom));
                Assert.That(alpha.AssemblyName, Is.EqualTo(UsedAssembly));
                Assert.That(beta.AssemblyName, Is.EqualTo(UsedAssembly));
                Assert.That(gamma.AssemblyName, Is.EqualTo(UnusedAssembly));
                var scalar = build.Snapshot!.Types.Resolve(
                    TypeRef.Parse(ScalarTypeId).Value!);
                Assert.That(scalar.Ok, Is.True,
                    "the probe provider must offer " + ScalarTypeId);

                var baseline = CompileProbePlan(build);
                // Sanity: the compiled-bytes fingerprint of the used
                // Operation is in the Plan's hash input (SPEC §D0.1), and it
                // equals the registered implementationHash.
                var alphaStep = baseline.Steps.Single(s => s.OperationId == AlphaOpId);
                Assert.That(alphaStep.ImplementationHash,
                    Is.EqualTo(alpha.ImplementationHash));
                Assert.That(alphaStep.DescriptorHash, Is.EqualTo(alpha.DescriptorHash));
                Assert.That(baseline.PlanHash, Does.Match("^[0-9a-f]{64}$"));
                WriteBaseline(build, baseline);

                EditBeta(variant: 1);
                ArmDomainSentinel();
                WriteProbeToken("phase-2");
                Crumb("phase-2 complete: baseline persisted, B edited, sentinel armed");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-2 threw " + e.GetType().Name + ": " + e.Message);
                return PhaseResult.Fail("phase-2 threw an unexpected exception: " + e);
            }
        }

        // Domain 3: same-DLL change ⇒ the used DLL changed ⇒ invalid. Ends
        // by editing Operation C's body in the unrelated OTHER DLL and
        // arming the sentinel.
        private static PhaseResult RunPhaseUsedAssemblyChanged()
        {
            var probeFailure = VerifyProbeToken("phase-2", "phase-3");
            if (probeFailure is not null)
            {
                Crumb("phase-3 probe gate failed: " + probeFailure.Failure);
                return probeFailure;
            }
            try
            {
                var afterBeta = Rebuild();
                var betaBaseline = ReadBaseline();
                var planBeta = CompileProbePlan(afterBeta);
                Assert.That(planBeta.PlanHash, Is.Not.EqualTo(betaBaseline.PlanHash),
                    "changing the used DLL (via the unused sibling's body) must" +
                    " invalidate the Plan");
                var betaStep = planBeta.Steps.Single(s => s.OperationId == AlphaOpId);
                Assert.That(betaStep.ImplementationHash,
                    Is.Not.EqualTo(betaBaseline.AlphaImplementationHash),
                    "the used Operation's compiled-bytes fingerprint must move with its DLL");
                AssertPlanHashMismatch(
                    AcceptWithExpectedPlanHash(planBeta, betaBaseline.PlanHash),
                    betaBaseline.PlanHash, planBeta);
                WriteBaseline(afterBeta, planBeta);

                EditGamma(variant: 1);
                ArmDomainSentinel();
                WriteProbeToken("phase-3");
                Crumb("phase-3 complete: same-DLL invalidation proven, C edited," +
                    " sentinel armed");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-3 threw " + e.GetType().Name + ": " + e.Message);
                return PhaseResult.Fail("phase-3 threw an unexpected exception: " + e);
            }
        }

        // Domain 4: unrelated DLL change ⇒ Plan kept. The registryHash
        // moved (C's implementationHash is a registryHash input), so this
        // same phase proves a registryHash-only change does not invalidate
        // (SPEC §D0.1), and the preserved Plan's expectedPlanHash is
        // accepted at execute (SPEC §D7 item 7) — the assertion the
        // previous head's resurrected iterator faulted on. Ends by editing
        // the used Codec's defining assembly (the scalar Codec the Plan's
        // used type carries) and arming the sentinel.
        private static PhaseResult RunPhaseUnrelatedAssemblyChanged()
        {
            var probeFailure = VerifyProbeToken("phase-3", "phase-4");
            if (probeFailure is not null)
            {
                Crumb("phase-4 probe gate failed: " + probeFailure.Failure);
                return probeFailure;
            }
            try
            {
                var afterGamma = Rebuild();
                var gammaBaseline = ReadBaseline();
                Assert.That(afterGamma.Snapshot!.RegistryHash,
                    Is.Not.EqualTo(gammaBaseline.RegistryHash),
                    "the unrelated DLL change must still move registryHash — only then does" +
                    " an unchanged planHash prove a registryHash-only change is tolerated");
                var planGamma = CompileProbePlan(afterGamma);
                Assert.That(planGamma.PlanHash, Is.EqualTo(gammaBaseline.PlanHash),
                    "changing an unused Operation's unrelated DLL must keep the Plan");
                var accepted = AcceptWithExpectedPlanHash(planGamma, gammaBaseline.PlanHash);
                Assert.That(accepted.Ok, Is.True,
                    () => "a still-valid Plan must accept its expectedPlanHash: " +
                        Describe(accepted.Diagnostics));
                WriteBaseline(afterGamma, planGamma);

                EditCodec(variant: 1);
                ArmDomainSentinel();
                WriteProbeToken("phase-4");
                Crumb("phase-4 complete: preserved Plan accepted, Codec edited," +
                    " sentinel armed");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-4 threw " + e.GetType().Name + ": " + e.Message);
                return PhaseResult.Fail("phase-4 threw an unexpected exception: " + e);
            }
        }

        // Domain 5: used-Codec change ⇒ invalid (SPEC §B0.1: Codec changes
        // revalidate the Plans using them).
        private static PhaseResult RunPhaseUsedCodecChanged()
        {
            var probeFailure = VerifyProbeToken("phase-4", "phase-5");
            if (probeFailure is not null)
            {
                Crumb("phase-5 probe gate failed: " + probeFailure.Failure);
                return probeFailure;
            }
            try
            {
                var afterCodec = Rebuild();
                var codecBaseline = ReadBaseline();
                var planCodec = CompileProbePlan(afterCodec);
                Assert.That(planCodec.PlanHash, Is.Not.EqualTo(codecBaseline.PlanHash),
                    "changing the used Codec's defining assembly must invalidate the Plan");
                AssertPlanHashMismatch(
                    AcceptWithExpectedPlanHash(planCodec, codecBaseline.PlanHash),
                    codecBaseline.PlanHash, planCodec);
                WriteProbeToken("phase-5");
                Crumb("phase-5 complete: used-Codec invalidation proven");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-5 threw " + e.GetType().Name + ": " + e.Message);
                return PhaseResult.Fail("phase-5 threw an unexpected exception: " + e);
            }
        }

        // The iterator body is deliberately minimal — phase call, yield
        // RecompileScripts, sentinel wait, asserts on the returned
        // PhaseResult — because UTF's iterator preservation restores
        // non-serializable fields as null/default and cannot rebuild a
        // `() =>` display class (L-14). Every assertion lives inside a
        // phase method in its own domain: nothing the resurrected iterator
        // can corrupt is ever dereferenced, and a genuine failure still
        // arrives as the PhaseResult's message text.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator UsedFingerprints_ChangePlanValidityExactlyAsSpecified()
        {
            var phase1 = RunPhaseCreateProbes();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "phase-1 failed: " + phase1.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            var phase2 = RunPhaseBaseline();
            Crumb("phase-2 returned ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True, "phase-2 failed: " + phase2.Failure);

            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the B edit did not produce a domain reload");

            var phase3 = RunPhaseUsedAssemblyChanged();
            Crumb("phase-3 returned ok=" + phase3.Ok + " failure=" + phase3.Failure);
            Assert.That(phase3.Ok, Is.True, "phase-3 failed: " + phase3.Failure);

            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the C edit did not produce a domain reload");

            var phase4 = RunPhaseUnrelatedAssemblyChanged();
            Crumb("phase-4 returned ok=" + phase4.Ok + " failure=" + phase4.Failure);
            Assert.That(phase4.Ok, Is.True, "phase-4 failed: " + phase4.Failure);

            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the Codec edit did not produce a domain reload");

            var phase5 = RunPhaseUsedCodecChanged();
            Crumb("phase-5 returned ok=" + phase5.Ok + " failure=" + phase5.Failure);
            Assert.That(phase5.Ok, Is.True, "phase-5 failed: " + phase5.Failure);
        }
    }
}
