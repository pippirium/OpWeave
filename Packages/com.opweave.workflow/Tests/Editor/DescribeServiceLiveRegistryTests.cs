#nullable enable

// T-W3.1-03 [Edit] tests: local:T-W3.1-03:describe-service — the SPEC §C8
// describe_operation service called directly over the live Registry (the
// production type + operation stages, so the standard catalog and the
// AT-01 proj.transform.reset custom Operation are the real registered
// ones). Every registered (id, version) is described and every returned
// example — fixture manifest applied through the ExampleFixture first —
// validates with ok:true and no warnings through the snapshot-backed
// WorkflowCompiler (SPEC §C8 test, AT-F06). SPEC §C1 item 4's "no Operation
// without a complete-WF Example" is a fail-closed contract over the live
// Registry: every registered Operation that publishes no Example names that
// Operation and its provenance and fails the pass — no population is
// filtered out and nothing is silently collected, this repository's
// compiled test support included. The defining-assembly rule below only
// labels test support in the reports; it never exempts it. The second fixture
// injects a deliberately broken custom Operation through the stage-1 seam:
// its describe answers OW-OP-001 with the registration failure's code and
// source file in the hint.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W3.1-03:describe-service — §C8 describe over the live
    /// Registry, every example validated with its fixture applied, and the
    /// broken-custom-Operation OW-OP-001 hint. The §C1 item-4 completeness
    /// rule is fail-closed over every Operation the live Registry registers —
    /// this repository's compiled test support included.
    /// </summary>
    internal sealed class DescribeServiceLiveRegistryTests
    {
        private const string BrokenId = "test.describe.broken.op";

        // The shipped contract SPEC §C1 item 4 governs: the standard catalog
        // of the OpWeave.Operations assembly and the AT-01 custom Operation of
        // the project's OpWeave.Custom assembly.
        private const string StandardOperationsAssembly = "OpWeave.Operations";
        private const string CustomOperationsAssembly = "OpWeave.Custom";
        private const string StandardOperationId = "core.start";
        private const string ResetOpId = "proj.transform.reset";

        // One documented identity rule labels this repository's compiled
        // test support in the pass's reports: an Operation whose defining
        // assembly carries the *.Tests test-assembly suffix
        // (OpWeave.Core.Tests, OpWeave.UnityAdapter.Tests,
        // OpWeave.Custom.Tests, every one compiled only under the
        // UNITY_INCLUDE_TESTS define constraint) and that is not defined
        // inside an extension package. SPEC F-12 keeps test support separate
        // from the production assemblies, so those probe classes are never
        // shipped.
        //
        // The label is reporting only — it is never an exemption: every
        // registered Operation, test support included, must publish a
        // complete-WF Example, and the compiled probes carry theirs through
        // ProbeOperationExample just like com.opweave.fixture.valid.op
        // carries its own.
        private const string TestAssemblySuffix = ".Tests";

        // The persistent embedded extension fixture whose Operation the §C1
        // item-4 population must judge (the F1 regression below).
        private const string FixtureOperationId = "com.opweave.fixture.valid.op";

        // The one documented test-support label of the §C1 item-4 pass.
        private static bool IsCompiledTestSupport(RegisteredOperation operation) =>
            (operation.PackageId is null ||
                string.Equals(operation.PackageId,
                    EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal)) &&
            operation.AssemblyName.EndsWith(TestAssemblySuffix, StringComparison.Ordinal);

        // The Registry's own provenance of one Operation: its defining
        // assembly, plus the UPM Package ID when it ships inside one.
        private static string ProvenanceOf(RegisteredOperation operation) =>
            operation.PackageId is { } packageId
                ? "assembly '" + operation.AssemblyName + "', package '" + packageId + "'"
                : "assembly '" + operation.AssemblyName + "'";

        // id@version of one registered Operation, as the pass labels it.
        private static string LabelOf(RegisteredOperation operation) =>
            operation.Descriptor.Id + "@" + operation.Descriptor.Version;

        // The D6 phase's required static context: the standard Operations'
        // ValidateStatic reads Parameters only — never Get<T> — so the stub
        // the per-descriptor tests use is safe for every node in an example.
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
                throw new NotSupportedException("No ValidateStatic reads a port.");
        }

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // Bound on re-drain/rebuild legs after a build reads a stale or
        // still-armed stage 1 (T-W2.4-15); each leg's drain stays bounded
        // by WaitTimeoutSec, so neither loop can wait indefinitely.
        private const int MaxRebuildLegs = 3;

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the quiet
        // drain reaches it through reflection (the At01AgentStepsTests
        // probe, T-W2.4-07). A reload already armed behind the runner's
        // reload lock surfaces here while isCompiling is false — the
        // deferred state an isCompiling-only check misses.
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
        // pending — either is one the drain must cross, never ride into
        // the registry build below.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling ||
            CompilationAssembliesPending();

        // The queued-but-unstarted/mid-transition window the two flags
        // cannot see (T-W2.4-15): a predecessor can arm a compile that has
        // not yet raised isCompiling or IsScriptReloadRequested, while the
        // compilation pipeline already refuses to answer — its
        // GetAssemblies result going null or its query throwing is the
        // only observable the drain→build gap leaves. Treat an unanswered
        // pipeline as pending: the drain keeps waiting until the pipeline
        // serves the query the registry build and the compiler's plan
        // stage are about to run.
        private static bool CompilationAssembliesPending()
        {
            try
            {
                return CompilationPipeline.GetAssemblies(AssembliesType.Editor) is null ||
                    CompilationPipeline.GetAssemblies(AssembliesType.Player) is null;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // The pending probe the quiet drain trusts is only as strong as
        // its shape: prove it once up front (the At01AgentStepsTests
        // pattern, T-W2.4-07) so the fixture fails here — before any
        // test — instead of silently degrading to an isCompiling-only
        // check that misses a requested reload armed behind the lock.
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
                    "this Unity version — the quiet drain cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // These tests never write a script file, so the only work they
        // can meet here is a leftover handed over by an earlier fixture:
        // a script compile still in flight or queued behind the runner's
        // lock, or the asset import in front of one. The sanctioned
        // bounded conditional drain (the At01AgentStepsTests
        // DrainEditorState idiom, T-W2.4-07) replaces the unconditional
        // `yield return new RecompileScripts(false, false)` residual:
        // that instruction's Perform requests a compile and unlocks the
        // runner's reload lock even when nothing is queued — and with
        // ExpectDomainReload=false UTF keeps no resume state, so a
        // still-pending predecessor reload crossing the unlock abandons
        // the test on cleared locals. Here a pending compile or
        // requested reload is crossed only at DrainedScriptReload — the
        // bounded ExpectDomainReload boundary the caller's relay hands
        // to UTF at the test body's own top level, where the captured PC
        // is correct — and everything else settles on bounded plain
        // frames: a queued compile surfaces as isCompiling a frame or
        // two after Refresh returns, so the wait only trusts a sustained
        // quiet spell and a periodic Refresh flushes the import queue so
        // a queued compile surfaces inside the window, never behind it.
        private static IEnumerator WaitForQuietEditor()
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
                        "DescribeServiceLiveRegistryTests.WaitForQuietEditor " +
                        "timed out after " + WaitTimeoutSec +
                        " s draining the armed editor state " +
                        "(scriptReloadRequested: " + ScriptReloadRequested() +
                        ", isCompiling: " + EditorApplication.isCompiling +
                        ", isUpdating: " + EditorApplication.isUpdating + ")");
                }
                if (ReloadPending())
                {
                    // A running or requested compile ends in a reload
                    // queued behind the runner's lock — cross it at the
                    // bounded sanctioned boundary, never ride it into
                    // the registry build.
                    quietAt = -1.0;
                    yield return new DrainedScriptReload(
                        "WaitForQuietEditor", WaitTimeoutSec);
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
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the live registry cannot build over a failed script compile");
            Assert.IsFalse(EditorApplication.isCompiling,
                "the live registry cannot build while a compile is in flight");
            Assert.IsFalse(ScriptReloadRequested(),
                "the live registry cannot build while a script reload is pending");
            Assert.IsFalse(CompilationAssembliesPending(),
                "the live registry cannot build while the compilation " +
                "pipeline cannot answer its assembly query");
        }

        // The bounded reload-crossing instruction — the
        // WaitForDomainReload.Perform wait with a deadline, run by UTF
        // through the IEditModeTestYieldInstruction interface so
        // ExpectDomainReload arms the runner's reload bookkeeping
        // exactly as with UTF's own instructions. It is yielded only
        // after ReloadPending() proved a real reload is armed, so the
        // unlock below always meets a queued reload and can never strand
        // the lock released on an idle Editor — the failure mode of the
        // unconditional RecompileScripts(false, false) unlock it
        // replaces. The one path where no reload can follow — a failed
        // compile — re-locks before failing.
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
                            "DescribeServiceLiveRegistryTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "DescribeServiceLiveRegistryTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // The raw live build — returned unasserted so the bounded
        // re-drain/rebuild loop in the §C8 pass can inspect Stale and the
        // armed-editor state before deciding the build is consumable
        // (T-W2.4-15); LiveBuild keeps the loud contract for the callers
        // that build once over an already-drained editor.
        private static RegistryBuildResult RebuildLive() =>
            RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());

        private static RegistryBuildResult LiveBuild()
        {
            var build = RebuildLive();
            Assert.IsFalse(build.Stale, "the live registry build must not be stale");
            Assert.IsNotNull(build.Snapshot, "the live registry must publish a snapshot");
            return build;
        }

        private static WorkflowCompiler Compiler(RegistryBuildResult build) =>
            new WorkflowCompiler(
                build.Snapshot!,
                (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                new UnityTypeProbe(),
                UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers));

        // The serializable cursor of the judged population: one
        // (id, version) pair per registered Operation, captured once at
        // pass start so a mid-pass resume keeps judging the same
        // population — plain fields, not auto-properties, because the
        // serializer carries fields.
        [Serializable]
        private struct OperationKey
        {
            internal OperationKey(string id, int version)
            {
                Id = id;
                Version = version;
            }

            internal string Id;
            internal int Version;
        }

        // SPEC §C8: describe every registered Operation and validate each
        // returned example with its declared fixture applied — through
        // this ticket's internal ExampleFixture, which materializes the
        // files and Prefab assets the manifest names and cleans them up on
        // Dispose. Every problem the pass finds is collected and reported
        // together: an assertion that stopped on the first Operation would
        // leave the rest of the Registry unchecked, which is not the
        // acceptance condition.
        [UnityTest]
        public IEnumerator EveryExampleOfEveryRegisteredOperationValidatesWithItsFixtureApplied()
        {
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
                yield return waiter.Current;
            }

            // Issue #629 (T-W2.4-14): the pass below materializes
            // ExampleFixture assets inside the body, so the quiet-Editor
            // drain above is not the last word — a compile the
            // application or a leftover queues can still cross mid-pass,
            // and a resume then lands the enumerator at the
            // iteration-top relay with every hoisted local reset or
            // broken. The pass therefore trusts only serializable state
            // across the yield — the counters, the label lists and an
            // (id, version) cursor over the population it started
            // judging — and rematerializes every live capture (build,
            // snapshot, describe service, compiler) with ??= after the
            // yield, never trusting a pre-yield capture. The
            // ExampleFixture is created, filled and disposed inside one
            // yield-free iteration span, so a resume can never strand
            // its asset tracking.
            RegistryBuildResult? build = null;
            RegistrySnapshot? snapshot = null;
            OperationDescribeService? service = null;
            WorkflowCompiler? compiler = null;
            List<OperationKey>? operationKeys = null;
            var opIndex = 0;
            var judgedIndex = -1;
            var spanDrainLegs = 0;
            var described = 0;
            var validated = 0;
            var covered = 0;
            var coveredStandard = 0;
            List<string>? coveredLabels = null;
            List<string>? testSupportLabels = null;
            List<string>? problems = null;

            for (;;)
            {
                // The pass's one mid-pass yield: any compile armed since
                // the last iteration crosses at this drain — never inside
                // the captured-state span below — and the resume re-runs
                // the rematerialization before anything is dereferenced.
                if (ReloadPending())
                {
                    IEnumerator? midDrain = null;
                    for (;;)
                    {
                        midDrain ??= WaitForQuietEditor();
                        if (!midDrain.MoveNext())
                        {
                            break;
                        }
                        yield return midDrain.Current;
                    }
                    // The crossed compile may have replaced the
                    // registered set: the live captures are dropped and
                    // rebuilt below while the serializable cursor keeps
                    // the population the pass started judging.
                    build = null;
                    snapshot = null;
                    service = null;
                    compiler = null;
                }

                // T-W2.4-15 bounded re-drain/rebuild: a compile a
                // predecessor or a span-internal arming queued can
                // surface queued-but-unstarted after the last drain —
                // either it is already visible (Stale build, or the
                // armed pipeline ReloadPending sees) or it surfaces into
                // stage 1's view mid-build and the next leg re-drains.
                // The captures below are materialized only from a build
                // the proven-quiet stage 1 produced.
                for (var leg = 0; build is null; leg++)
                {
                    build = RebuildLive();
                    if (!build.Stale && !ReloadPending())
                    {
                        break;
                    }
                    if (leg >= MaxRebuildLegs)
                    {
                        Assert.Fail(
                            "DescribeServiceLiveRegistryTests: the live " +
                            "registry build stayed " +
                            (build.Stale ? "stale" : "over an armed editor") +
                            " through " + MaxRebuildLegs + " re-drain legs");
                    }
                    IEnumerator? rebuildDrain = null;
                    for (;;)
                    {
                        rebuildDrain ??= WaitForQuietEditor();
                        if (!rebuildDrain.MoveNext())
                        {
                            break;
                        }
                        yield return rebuildDrain.Current;
                    }
                    build = null;
                }
                Assert.IsFalse(build!.Stale,
                    "the live registry build must not be stale");
                Assert.IsNotNull(build.Snapshot,
                    "the live registry must publish a snapshot");
                snapshot ??= build.Snapshot!;
                service ??= new OperationDescribeService(snapshot);
                compiler ??= Compiler(build!);
                problems ??= new List<string>();
                coveredLabels ??= new List<string>();
                testSupportLabels ??= new List<string>();
                operationKeys ??= snapshot.Operations
                    .Select(o => new OperationKey(
                        o.Descriptor.Id, o.Descriptor.Version))
                    .ToList();

                if (opIndex >= operationKeys.Count)
                {
                    break;
                }
                var key = operationKeys[opIndex];
                var operation = snapshot.Find(key.Id, key.Version);
                if (operation is null)
                {
                    problems.Add(key.Id + "@" + key.Version +
                        ": the Operation left the registered set across a " +
                        "mid-pass reload");
                    opIndex++;
                    continue;
                }
                var descriptor = operation.Descriptor;
                var label = LabelOf(operation);
                var isTestSupport = IsCompiledTestSupport(operation);

                // A span-internal arming retries this Operation's whole
                // judgement (T-W2.4-15): snapshot the counters and list
                // lengths the iteration adds to, so the retry replays the
                // Operation exactly once instead of double-counting.
                if (judgedIndex != opIndex)
                {
                    judgedIndex = opIndex;
                    spanDrainLegs = 0;
                }
                var iterDescribed = described;
                var iterCovered = covered;
                var iterCoveredStandard = coveredStandard;
                var iterValidated = validated;
                var iterProblems = problems.Count;
                var iterCoveredLabels = coveredLabels.Count;
                var iterTestSupportLabels = testSupportLabels.Count;
                var armedInSpan = false;

                var describe = service.DescribeOperation(
                    new DescribeOperationRequest(descriptor.Id)
                    {
                        Version = descriptor.Version,
                    });
                if (!describe.Ok)
                {
                    problems.Add(label + ": " + string.Join("; ",
                        describe.Diagnostics.Select(
                            d => d.Code + " " + d.Message)));
                    opIndex++;
                    continue;
                }
                described++;
                if (describe.Value is not { } description)
                {
                    problems.Add(label +
                        ": describe answered ok but carried no description");
                    opIndex++;
                    continue;
                }

                if (description.RegistryGeneration != snapshot.GenerationId)
                {
                    problems.Add(label +
                        ": the description must carry the answering generation");
                }
                // The test-support label only names the probes in the
                // report below; it exempts nothing — every registered
                // Operation is judged for a complete-WF Example.
                if (!isTestSupport)
                {
                    covered++;
                    coveredLabels.Add(label);
                    if (string.Equals(operation.AssemblyName, StandardOperationsAssembly,
                            StringComparison.Ordinal))
                    {
                        coveredStandard++;
                    }
                }
                else
                {
                    testSupportLabels.Add(
                        label + " [" + ProvenanceOf(operation) + "]");
                }

                var examples = description.Examples;
                if (examples is null)
                {
                    problems.Add(label + ": the examples section must be populated");
                    opIndex++;
                    continue;
                }
                if (examples.Count == 0)
                {
                    problems.Add(label + ": an Operation registered without a " +
                        "complete-WF Example is incomplete (SPEC §C1 item 4): " +
                        ProvenanceOf(operation));
                }

                // One fixture per Operation, created and disposed inside
                // this yield-free span: the assets every example of the
                // Operation validates against are materialized, judged
                // and removed before the next iteration's relay can
                // resume the body — a resume can never strand the
                // fixture's asset tracking.
                using (var fixture = new ExampleFixture())
                {
                    foreach (var example in examples)
                    {
                        if (example.FixtureManifest is { } manifest)
                        {
                            try
                            {
                                fixture.Apply(manifest);
                            }
                            catch (Exception ex)
                            {
                                problems.Add(label + " example '" + example.Title +
                                    "': its declared fixtureManifest could not be applied: " +
                                    ex.GetType().Name + ": " + ex.Message);
                                continue;
                            }
                        }

                        // The fixture's own imports — or a leftover
                        // arming they flushed — can queue a compile
                        // inside this yield-free span; the compilation
                        // pipeline answer Validate's provenance pass is
                        // about to read may already be gone. The drain
                        // cannot yield inside the span (a resume would
                        // strand the fixture's asset tracking), so exit
                        // deterministically, cross the arming at the
                        // bounded drain below and replay this Operation.
                        if (ReloadPending())
                        {
                            armedInSpan = true;
                            break;
                        }

                        var read = WorkflowSerializer.Read(
                            example.WorkflowJson.CanonicalText);
                        if (!read.Ok)
                        {
                            problems.Add(label + " example '" + example.Title + "': " +
                                string.Join("; ", read.Diagnostics.Select(
                                    d => d.Code + " " + d.Message)));
                            continue;
                        }
                        if (read.Value is null)
                        {
                            problems.Add(label + " example '" + example.Title +
                                "': the parsed example carried no document");
                            continue;
                        }

                        var report = compiler.Validate(new ValidationRequest(
                            read.Value, null, ValidationMode.Static, ActorKind.Agent));
                        if (!report.Ok)
                        {
                            problems.Add(label + " example '" + example.Title + "': " +
                                string.Join("; ", report.Errors.Select(
                                    d => d.Code + " " + d.Message + " " + d.JsonPath)));
                        }
                        if (report.Warnings.Count != 0)
                        {
                            problems.Add(label + " example '" + example.Title + "': " +
                                string.Join("; ", report.Warnings.Select(
                                    d => d.Code + " " + d.Message + " " + d.JsonPath)));
                        }
                        if (report.Ok && report.Warnings.Count == 0)
                        {
                            validated++;
                        }
                    }
                }
                if (armedInSpan)
                {
                    // Replay this Operation's judgement exactly once:
                    // roll back what the abandoned span recorded, cross
                    // the armed compile at the bounded drain (the
                    // relay is this body's own top-level yield — UTF
                    // resumes here with the non-serializable locals
                    // reset, so the captures are dropped and rebuilt on
                    // the next iteration), then re-judge the same key.
                    described = iterDescribed;
                    covered = iterCovered;
                    coveredStandard = iterCoveredStandard;
                    validated = iterValidated;
                    if (problems.Count > iterProblems)
                    {
                        problems.RemoveRange(
                            iterProblems, problems.Count - iterProblems);
                    }
                    if (coveredLabels.Count > iterCoveredLabels)
                    {
                        coveredLabels.RemoveRange(
                            iterCoveredLabels, coveredLabels.Count - iterCoveredLabels);
                    }
                    if (testSupportLabels.Count > iterTestSupportLabels)
                    {
                        testSupportLabels.RemoveRange(
                            iterTestSupportLabels, testSupportLabels.Count - iterTestSupportLabels);
                    }
                    if (++spanDrainLegs > MaxRebuildLegs)
                    {
                        Assert.Fail(
                            "DescribeServiceLiveRegistryTests: a compile " +
                            "kept being armed inside " + label +
                            "'s fixture span through " + MaxRebuildLegs +
                            " bounded drains");
                    }
                    IEnumerator? spanDrain = null;
                    for (;;)
                    {
                        spanDrain ??= WaitForQuietEditor();
                        if (!spanDrain.MoveNext())
                        {
                            break;
                        }
                        yield return spanDrain.Current;
                    }
                    build = null;
                    snapshot = null;
                    service = null;
                    compiler = null;
                    continue;
                }
                opIndex++;
            }

            Assert.That(problems, Is.Empty,
                problems!.Count + " problem(s) across " + described +
                " described Operations (" + covered + " shipped-catalog, custom " +
                "and extension Operations under SPEC §C1 item 4; " +
                testSupportLabels!.Count + " compiled test-support probes judged " +
                "the same way: " + string.Join(", ", testSupportLabels) +
                "):\n" + string.Join("\n", problems!));
            Assert.Greater(described, 0, "the live registry must register Operations");
            Assert.Greater(validated, 0,
                "the live registry must publish at least one example");
            Assert.That(covered, Is.GreaterThan(30),
                "SPEC §C1 item 4 must cover the shipped catalog, the AT-01 custom " +
                "Operation and every registered extension, not only the compiled " +
                "test support (" + testSupportLabels!.Count +
                " probes registered from *.Tests assemblies)");
            Assert.Greater(coveredStandard, 0,
                StandardOperationsAssembly + "'s Operations are inside the " +
                "§C1 item-4 population");
            Assert.That(coveredLabels, Does.Contain(ResetOpId + "@1"),
                "the AT-01 custom Operation is inside the §C1 item-4 population");
        }

        // F1 regression (T-W3.1-03 corrections 2 and 3). com.opweave.fixture.valid.op@1
        // is no leak installed by another test: com.opweave.fixture.valid is
        // embedded in TestProject/Packages, so its Editor assembly compiles
        // whenever the project loads and the production stages register that
        // Operation on every live build — which
        // ExtensionRegistrationGateTests.ValidExtension_OperationAndProviderRegister
        // asserts. Being registered on every suite run, it has to carry its own
        // complete-WF Example
        // (TestProject/Packages/com.opweave.fixture.valid/Editor/ValidOperation.cs).
        // On head e57e481 that Operation published no Example at all and the
        // harness-provenance exemption classified its fixture provenance as test
        // support, so the pass collected it silently: run this test on that head
        // and both the Example assertion and the fail-closed assertion below
        // fail and name it, while the label assertion above shows the
        // exemption — not the identity rule — is what hid it.
        //
        // Correction 3 removes the same exemption from the fail-closed query
        // itself: on head 0405300 the compiled probe Operations of the *.Tests
        // assemblies — test.registry.good@1, test.dummy.immediate@1 and their
        // siblings — registered on every live build with no Example at all,
        // yet the query filtered them out, so the assertion below passed while
        // registered Operations published nothing. With the filter removed it
        // fails on 0405300's logic and names every one of them, because the
        // unfiltered set contains those example-less probes; now that each
        // probe publishes a complete-WF Example through ProbeOperationExample,
        // the assertion holds for the whole registered set.
        [UnityTest]
        public IEnumerator LiveRegistryJudgedPopulation_NeverRegistersAnOperationWithoutAnExample()
        {
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
                yield return waiter.Current;
            }

            var snapshot = LiveBuild().Snapshot!;

            var fixtureOperation = snapshot.Find(FixtureOperationId, 1);
            Assert.That(fixtureOperation, Is.Not.Null,
                "the live Registry registers the extension fixture Operation of T-W2.12-06");
            Assert.That(IsCompiledTestSupport(fixtureOperation!), Is.False,
                FixtureOperationId + " is defined inside an extension package, so the " +
                "test-support boundary never reaches it and it is judged by §C1 item 4: " +
                ProvenanceOf(fixtureOperation!));
            Assert.That(fixtureOperation!.Descriptor.Examples, Is.Not.Empty,
                FixtureOperationId + " registers on every suite run from the persistent " +
                "embedded package, so SPEC §C1 item 4 requires its own complete-WF " +
                "Example: " + ProvenanceOf(fixtureOperation!));

            var standard = snapshot.Find(StandardOperationId, 1);
            Assert.That(standard, Is.Not.Null,
                StandardOperationId + " must register in the live Registry");
            Assert.That(standard!.AssemblyName, Is.EqualTo(StandardOperationsAssembly));
            Assert.That(IsCompiledTestSupport(standard!), Is.False,
                "a standard catalog Operation is never compiled test support");
            Assert.That(standard!.Descriptor.Examples, Is.Not.Empty,
                StandardOperationId + " publishes the complete-WF Example §C1 item 4 requires");

            var custom = snapshot.Find(ResetOpId, 1);
            Assert.That(custom, Is.Not.Null,
                "the live Registry registers the AT-01 custom Operation");
            Assert.That(custom!.AssemblyName, Is.EqualTo(CustomOperationsAssembly));
            Assert.That(IsCompiledTestSupport(custom!), Is.False,
                "the AT-01 custom Operation is never compiled test support");
            Assert.That(custom!.Descriptor.Examples, Is.Not.Empty,
                ResetOpId + " publishes the complete-WF Example §C1 item 4 requires");

            // Fail-closed over the whole registered set: nothing registered
            // may be collected without an Example — no population is filtered
            // out, test support or not. On 0405300's logic this query filtered
            // the compiled probes out and passed while example-less
            // Operations like test.registry.good@1 stayed registered;
            // unfiltered it fails there and names them all.
            var exampleLess = snapshot.Operations
                .Where(o => o.Descriptor.Examples.Count == 0)
                .Select(o => LabelOf(o) + " [" + ProvenanceOf(o) + "]")
                .ToList();
            Assert.That(exampleLess, Is.Empty,
                "every registered Operation must publish a complete-WF Example " +
                "(SPEC §C1 item 4): " + string.Join(", ", exampleLess));

            // The test-support label is reported, never silent and never an
            // exemption: it names the probe Operations of the compiled test
            // assemblies so the query above could not hide one.
            var testSupport = snapshot.Operations
                .Where(o => IsCompiledTestSupport(o))
                .Select(o => LabelOf(o) + " [" + ProvenanceOf(o) + "]")
                .ToList();
            Assert.That(testSupport, Is.Not.Empty,
                "the test-support label must identify the probe Operations of " +
                "the compiled *.Tests assemblies — they are judged by the " +
                "example rule like everything else (" +
                string.Join(", ", testSupport) + ")");
        }

        // The deliberately broken custom Operation: its Descriptor names a
        // port type nothing registers, so the registry build refuses it —
        // the kind of failure SPEC §C8 makes describe surface. The
        // [OpWeaveOperation] attribute's caller-file provenance is the
        // source file the failure record carries.
        [OpWeaveOperation(BrokenId, 1)]
        internal sealed class BrokenFixtureOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } =
                Op.Define(BrokenId, 1)
                    .Summary("A describe fixture whose port type never registers.")
                    .Input("value", "test.describe.missing.type", required: true)
                    .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        private static RegistryTypeStage.Seams BrokenSeams() =>
            new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => new[] { typeof(BrokenFixtureOperation) },
                ProviderTypes: () =>
                    TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());

        // SPEC §C8: an ID whose registration failed answers OW-OP-001 with
        // the failure's reason — the published code and the source file —
        // in the hint.
        [Test]
        public void BrokenCustomOperation_DescribeReturnsOwOp001_WithCodeAndSourceFileInHint()
        {
            var build = RegistryBuildHost.Rebuild(
                BrokenSeams(), RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale, "the fixture registry build must not be stale");
            Assert.IsNotNull(build.Snapshot, "the build must publish a snapshot");
            var snapshot = build.Snapshot!;

            Assert.IsNull(snapshot.FindLatest(BrokenId),
                "the broken Operation must never reach the registered set");

            var failure = snapshot.Failures
                .Where(f => f.OperationId == BrokenId &&
                    f.Kind == RegistryChangeKinds.Failed)
                .ToList();
            Assert.That(failure, Has.Count.EqualTo(1),
                "the rejection must land on the published failure record");
            Assert.IsNotEmpty(failure[0].Diagnostics,
                "the rejection must carry the diagnostic the hint names");
            Assert.IsNotNull(failure[0].SourceFile,
                "the rejection must carry the candidate's source file");

            var expectedFile = failure[0].SourceFile!;

            var result = new OperationDescribeService(snapshot)
                .DescribeOperation(new DescribeOperationRequest(BrokenId));

            Assert.IsFalse(result.Ok, "a failed-registration ID must not describe");
            var diagnostic = result.Diagnostics[0];
            Assert.That(diagnostic.Code, Is.EqualTo(ErrorCodes.OW_OP_001));
            foreach (var code in failure[0].Diagnostics
                .Where(d => d.Code.Length != 0)
                .Select(d => d.Code))
            {
                Assert.That(diagnostic.Hint, Does.Contain(code),
                    "the hint must carry the registration failure's code");
            }
            Assert.That(diagnostic.Hint, Does.Contain(expectedFile),
                "the hint must carry the registration failure's source file");
        }
    }
}
