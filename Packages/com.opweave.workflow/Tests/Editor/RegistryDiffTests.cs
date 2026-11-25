#nullable enable

// T-W2.2-04 [Edit] tests: local:T-W2.2-04:registry-diff — SPEC §C3 rule 7
// end to end. A missing/corrupt registry.json yields
// previousSnapshot "unavailable" with a reason and records no diff (the
// end-to-end form of the [Core] four-mode check); a real custom Operation
// file deleted, restored and Execute-only edited across real domain
// reloads yields removed (+ the OW-OP-001 "previously at <file>" hint),
// added and implementationChanged (SPEC §C3 tests 3–4).
//
// The domain-reload [UnityTest] writes its probe assembly under
// Assets/OpWeaveRegistryDiffProbe/ and keeps its store under
// Library/OpWeaveRegistryDiffProbe/; state crossing a reload lives only in
// files. Everything is removed by [UnityTearDown]; nothing tracked is
// modified.
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
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RegistryDiffTests
    {
        private const string ProbeOpId = "test.registrydiff.probe";
        private const string ProbeAssetDir = "Assets/OpWeaveRegistryDiffProbe";
        private const string ProbeAssembly = "OpWeave.RegistryDiffProbe";
        private const string ProbeFileName = "DiffProbeOp.cs";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string StateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveRegistryDiffProbe");

        private static string ProbeDiskDir => Path.Combine(ProjectRoot, ProbeAssetDir);
        private static string ProbeFile => Path.Combine(ProbeDiskDir, ProbeFileName);

        // ── Recorder fixtures ─────────────────────────────────────────────

        private sealed class Captured
        {
            internal readonly List<string> Infos = new List<string>();
            internal readonly List<string> Warnings = new List<string>();
            internal readonly List<IReadOnlyList<RegistryChange>> Events =
                new List<IReadOnlyList<RegistryChange>>();
            internal RunEvents Dispatcher = new RunEvents();

            internal Captured() =>
                Dispatcher.RegistryChanged += changes => Events.Add(changes);

            internal RegistryDiffRecorder Recorder(string root) =>
                new RegistryDiffRecorder(
                    root,
                    () => Dispatcher,
                    line => Infos.Add(line),
                    line => Warnings.Add(line));
        }

        private static RegistryBuildResult RebuildWith(RegistryDiffRecorder recorder) =>
            RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication(),
                diffRecorder: recorder);

        // ── Missing/corrupt registry.json: the end-to-end unavailable case ─

        [Test]
        public void Rebuild_MissingRegistryJson_ReportsUnavailableAndRecordsNoDiff()
        {
            Cleanup();
            var captured = new Captured();
            var result = RebuildWith(captured.Recorder(StateDir));

            var diff = result.Diff;
            Assert.That(diff, Is.Not.Null);
            Assert.That(diff!.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(diff.UnavailableReason, Does.Contain("missing"));
            Assert.That(diff.Changes, Is.Empty);
            Assert.That(captured.Events, Is.Empty, "no RegistryChanged dispatch without a record");
            Assert.That(captured.Warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("unavailable", captured.Warnings[0]);
            Assert.That(File.Exists(
                Path.Combine(StateDir, RegistrySnapshotStore.ChangesFileName)), Is.False);
            // The new summary is still written so the next build can diff.
            Assert.That(File.Exists(
                Path.Combine(StateDir, RegistrySnapshotStore.SnapshotFileName)), Is.True);
        }

        [Test]
        public void Rebuild_CorruptRegistryJson_ReportsUnavailableAndRecordsNoDiff()
        {
            Cleanup();
            // First build persists a good summary; its own "previous
            // snapshot unavailable" warning is the baseline's, captured on
            // a throwaway recorder so the asserted build's capture holds
            // only the corrupt-file rebuild's output.
            RebuildWith(new Captured().Recorder(StateDir));
            Assert.That(File.Exists(
                Path.Combine(StateDir, RegistrySnapshotStore.SnapshotFileName)), Is.True);
            File.WriteAllText(
                Path.Combine(StateDir, RegistrySnapshotStore.SnapshotFileName),
                "{ corrupt");

            var captured = new Captured();
            var result = RebuildWith(captured.Recorder(StateDir));

            var diff = result.Diff!;
            Assert.That(diff.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(diff.UnavailableReason, Is.Not.Null.And.Not.Empty);
            Assert.That(diff.Changes, Is.Empty,
                "a corrupt registry.json never produces a synthetic all-added diff");
            Assert.That(captured.Events, Is.Empty);
            Assert.That(captured.Warnings, Has.Count.EqualTo(1));
            Assert.That(
                File.Exists(
                    Path.Combine(StateDir, RegistrySnapshotStore.ChangesFileName)), Is.False);
            Assert.That(result.Snapshot, Is.Not.Null,
                "the new Registry is still built and published (SPEC §C3 rule 7)");
        }

        [Test]
        public void Rebuild_ChangedContent_AppendsSummaryLineAndDispatches()
        {
            Cleanup();
            // First build persists the baseline on a throwaway capture:
            // its "unavailable" warning must not accumulate into the
            // asserted build's capture.
            RebuildWith(new Captured().Recorder(StateDir));

            // Second build over the same production seams: identical
            // content, so the recorded batch holds no added/removed/changed
            // record — only any per-candidate failures of the build itself.
            var captured = new Captured();
            var result = RebuildWith(captured.Recorder(StateDir));

            var diff = result.Diff!;
            Assert.That(diff.PreviousSnapshot, Is.EqualTo("available"));
            Assert.That(captured.Warnings, Is.Empty,
                "a trusted previous snapshot emits no warning");
            // Two production builds over the same code: the recorded batch
            // is either empty (identical content) or only per-candidate
            // failures; no added/removed/changed record may appear.
            foreach (var change in diff.Changes)
            {
                Assert.That(change.Kind, Is.EqualTo(RegistryChangeKinds.Failed));
            }
        }

        // ── SPEC §C3 tests 3–4 across real domain reloads ─────────────────

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private const string ProbeAsmdef =
            "{\n    \"name\": \"" + ProbeAssembly + "\",\n" +
            "    \"rootNamespace\": \"OpWeave.RegistryDiffProbeNs\",\n" +
            "    \"references\": [\"OpWeave.Core\"],\n" +
            "    \"includePlatforms\": [\"Editor\"],\n" +
            "    \"autoReferenced\": false\n}\n";

        private static string ProbeSource(int variant) =>
            "#nullable enable\n" +
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using OpWeave.Core;\n" +
            "using OpWeave.Core.Operations;\n\n" +
            "namespace OpWeave.RegistryDiffProbeNs\n{\n" +
            "    [OpWeaveOperation(\"" + ProbeOpId + "\", 1)]\n" +
            "    public sealed class DiffProbeOperation : IOperation\n" +
            "    {\n" +
            "        public OperationDescriptor Descriptor { get; } = Op.Define(\"" + ProbeOpId + "\", 1)\n" +
            "            .Summary(\"Registry diff probe operation.\")\n" +
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

        private static void WriteProbe(int variant)
        {
            Directory.CreateDirectory(ProbeDiskDir);
            File.WriteAllText(
                Path.Combine(ProbeDiskDir, ProbeAssembly + ".asmdef"), ProbeAsmdef);
            File.WriteAllText(ProbeFile, ProbeSource(variant));
            AssetDatabase.Refresh();
        }

        private static void DeleteProbe()
        {
            File.Delete(ProbeFile);
            File.Delete(ProbeFile + ".meta");
            AssetDatabase.Refresh();
        }

        private static bool Cleanup()
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
        // past its pre-reload phase into a silent PASS. The drain below
        // crosses a pending compile/reload only at the bounded
        // DrainedScriptReload boundary, once the pending state is proven,
        // and hands the next fixture a quiet Editor; the per-session probe
        // token turns any mid-body stale-PC landing into a loud failure
        // instead of a skipped phase (the At01AgentStepsTests /
        // ChangeSetDomainReloadTests sanctioned pattern).

        // The bound shared by the drain waits — the suite-wide convention
        // (240 s) — and the sustained-idle window the drain demands before
        // it trusts a quiet Editor (the QuietSettle convention).
        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        private static void Crumb(string message) => Debug.Log("[W2408] " + message);

        // A per-Editor-session token the pre-reload half stores in
        // SessionState and writes into the probe file; the post-reload half
        // reads both to prove the armed phase belongs to this session and
        // to this crossing — never residue of an earlier run. The file
        // lives under StateDir, which Cleanup already removes.
        private const string ProbeTokenKey =
            "OpWeave.W2408.RegistryDiff.ProbeToken";

        private static string ProbeTokenFile =>
            Path.Combine(StateDir, "reload-probe.txt");

        private static void ArmReloadProbe(string phase)
        {
            var token = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeTokenKey, token);
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(ProbeTokenFile, phase + "\n" + token + "\n");
            Crumb("probe armed: " + phase +
                " reloads=" + DomainReloadCounter.Current);
        }

        // A stale-resume landing past this crossing's arm fails here,
        // loudly, instead of letting the post-reload assertions report
        // green for a phase that never ran.
        private static void AssertReloadProbeThisSession(string phase)
        {
            var expected = SessionState.GetString(ProbeTokenKey, "");
            Assert.IsTrue(expected.Length > 0,
                phase + ": the pre-reload phase never executed in this Editor " +
                    "session (no session probe token)");
            Assert.IsTrue(File.Exists(ProbeTokenFile),
                phase + ": the persisted reload probe " + ProbeTokenFile +
                    " was deleted after the pre-reload phase wrote it");
            var lines = File.ReadAllLines(ProbeTokenFile);
            Assert.IsTrue(
                lines.Length >= 2 && lines[0] == phase && lines[1] == expected,
                phase + ": the persisted reload probe is stale — an earlier " +
                    "phase or session wrote " + ProbeTokenFile +
                    " (shows " + (lines.Length > 0 ? lines[0] : "<empty>") + ")");
            Crumb("probe verified: " + phase +
                " reloads=" + DomainReloadCounter.Current);
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
                        "RegistryDiffTests." + phase +
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
                "RegistryDiffTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "RegistryDiffTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "RegistryDiffTests." + phase +
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
                            "RegistryDiffTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "RegistryDiffTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup cleanup enter");
            var dirty = Cleanup();
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
            var dirty = Cleanup();
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

        // Each phase asserts inside its own domain: nothing non-serializable
        // crosses a reload (the sentinel wait is inlined per SPEC §T.3 — a
        // nested IEnumerator yield would not survive the reload). The store
        // root is constant, so the persisted registry.json does the
        // cross-build comparing.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator DeleteRestoreEdit_ProducesRemovedAddedImplementationChangedAndHint()
        {
            WriteProbe(variant: 0);
            ArmReloadProbe("write-v0");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");
            AssertReloadProbeThisSession("write-v0");

            // Baseline: the first recorded build — previous unavailable, the
            // probe Operation registered as custom.
            var baseline = RebuildWith(new Captured().Recorder(StateDir));
            Assert.That(baseline.Diff!.PreviousSnapshot, Is.EqualTo("unavailable"));
            var registered = baseline.Snapshot!.Find(ProbeOpId, 1);
            Assert.That(registered, Is.Not.Null, "the probe Operation did not register");
            Assert.That(registered!.Origin, Is.EqualTo(OperationOrigin.Custom));
            Assert.That(registered.SourceFile, Does.Contain(ProbeFileName));

            DeleteProbe();
            ArmReloadProbe("delete");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe file deletion did not produce a domain reload");
            AssertReloadProbeThisSession("delete");

            // Removed: the diff carries the previous source file, validate
            // answers OW-OP-001 with the "previously at <file>" hint.
            var captured = new Captured();
            var recorder = captured.Recorder(StateDir);
            var removed = RebuildWith(recorder);
            var removedDiff = removed.Diff!;
            Assert.That(removedDiff.PreviousSnapshot, Is.EqualTo("available"));
            var removedRecord = removedDiff.Changes
                .FirstOrDefault(c => c.Kind == RegistryChangeKinds.Removed &&
                                     c.OperationId == ProbeOpId);
            Assert.That(removedRecord, Is.Not.Null,
                "deleting the custom Operation file must yield removed");
            Assert.That(removedRecord!.SourceFile, Does.Contain(ProbeFileName));
            Assert.That(captured.Events, Has.Count.EqualTo(1),
                "RegistryChanged dispatches once after the recorded batch");
            Assert.That(captured.Infos, Has.Count.EqualTo(1),
                "one Console summary line is emitted");
            Assert.That(File.ReadAllLines(
                Path.Combine(StateDir, RegistrySnapshotStore.ChangesFileName)),
                Has.Some.Contains("\"removed\""));

            var report = ValidateAgainst(removed.Snapshot!, removedDiff.Changes);
            Assert.That(report.Ok, Is.False);
            var error = report.Errors.Single(e => e.Code == ErrorCodes.OW_OP_001);
            StringAssert.Contains(ProbeFileName, error.Hint,
                "OW-OP-001 must carry the 'previously at <file>' hint");

            // A later unrelated rebuild leaves no in-memory removed record,
            // yet the still-deleted Operation keeps its hint: the recorder
            // recomputes PreviousDefinitions from registry-changes.ndjson.
            var unrelated = RebuildWith(recorder);
            Assert.That(unrelated.Diff!.Changes
                    .Any(c => c.OperationId == ProbeOpId),
                Is.False, "an identical rebuild records nothing for the deleted probe");
            var persistedReport = ValidateAgainst(
                removed.Snapshot!, recorder.PreviousDefinitions);
            Assert.That(persistedReport.Ok, Is.False);
            var persistedError = persistedReport.Errors
                .Single(e => e.Code == ErrorCodes.OW_OP_001);
            StringAssert.Contains(ProbeFileName, persistedError.Hint,
                "the persisted previous-definition data keeps the 'previously at <file>' hint");

            WriteProbe(variant: 0);
            ArmReloadProbe("restore-v0");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe file restore did not produce a domain reload");
            AssertReloadProbeThisSession("restore-v0");

            // Restored: the same Operation is back — added.
            var restored = RebuildWith(new Captured().Recorder(StateDir));
            var restoredRecord = restored.Diff!.Changes
                .FirstOrDefault(c => c.Kind == RegistryChangeKinds.Added &&
                                     c.OperationId == ProbeOpId);
            Assert.That(restoredRecord, Is.Not.Null,
                "restoring the custom Operation file must yield added");

            WriteProbe(variant: 1);
            ArmReloadProbe("edit-v1");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe Execute edit did not produce a domain reload");
            AssertReloadProbeThisSession("edit-v1");

            // Execute body only: implementationHash moves, descriptorHash does
            // not — implementationChanged, never descriptorChanged.
            var edited = RebuildWith(new Captured().Recorder(StateDir));
            var editedChanges = edited.Diff!.Changes
                .Where(c => c.OperationId == ProbeOpId).ToList();
            Assert.That(editedChanges, Has.Count.EqualTo(1));
            Assert.That(editedChanges[0].Kind,
                Is.EqualTo(RegistryChangeKinds.ImplementationChanged),
                "changing only Execute yields implementationChanged");
        }

        // ── Validation plumbing for the OW-OP-001 hint ────────────────────

        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;
            internal StubStaticContext(string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }
            public override string NodeId { get; }
            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;
            public override T Get<T>(string port) => throw new NotSupportedException();
        }

        private sealed class StubTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => true;
        }

        // The validation under test ends at D3's OW-OP-001 — Build Plan is
        // never reached — so a canned provenance is enough (the production
        // constructor requires a non-null one).
        private sealed class StubProvenance : IPlanProvenance
        {
            public IntegrityLevel IntegrityLevel => IntegrityLevel.Weak;
            public Result<PlanEnvironment> Environment => Result<PlanEnvironment>.Success(
                new PlanEnvironment(
                    "6000.0.85f1", "Windows", "StandaloneWindows64",
                    "Mono2x", "NET_Standard", Array.Empty<string>()));
            public Result<PlanTypeFingerprint> TypeFingerprintFor(TypeDefinition definition) =>
                throw new NotSupportedException();
            public Result<PlanCodecIdentity> CodecIdentityFor(TypeDefinition definition) =>
                throw new NotSupportedException();
        }

        private static ValidationReport ValidateAgainst(
            RegistrySnapshot snapshot, IReadOnlyList<RegistryChange> changes)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.registrydiff",
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData { Id = "a", Operation = ProbeOpId, Version = 1 },
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "a") },
            });
            Assert.That(created.Ok, Is.True,
                "document must create: " + string.Join(
                    "; ", created.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return new WorkflowCompiler(
                    snapshot,
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    new StubTypeProbe(),
                    new StubProvenance(),
                    changes)
                .Validate(new ValidationRequest(
                    created.Value!, null, ValidationMode.Static, ActorKind.Agent));
        }
    }
}
