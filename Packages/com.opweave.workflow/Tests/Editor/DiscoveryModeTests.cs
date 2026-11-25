#nullable enable

// T-W2.9-01 [Edit] tests: local:T-W2.9-01:discovery-mode — SPEC §C0.2 / §H4.1
// DiscoveryMode (auto/whitelist) and disabledOperations, decided on the
// pre-instantiation exclusion seam of SPEC §C3 rule 5 before any extension
// constructor runs. Coverage: SPEC §M.5 auto/whitelist cases, SPEC §D6
// whitelist-exclusion (a non-whitelisted Operation is unusable before
// Descriptor validation) and the SPEC §C3 test-1 disabledOperations /
// whitelist parts — fixture Operations whose constructors increment a static
// counter, in a project assembly (OpWeave.Custom: proj.transform.reset) and
// in the com.opweave.fixture.valid UPM package — each counter stays 0 when
// its candidate is excluded, and a whitelisted (package, assembly, id,
// version) is constructed and registered. Matching uses the
// attribute-declared identity the stage binds without construction (SPEC
// §C2 item 2, §H4.1); excluded candidates publish as Registry state on
// RegistrySnapshot.Exclusions, never as registration failures.
//
// The domain-reload [UnityTest] re-runs the whitelist build across a real
// reload (SPEC §T.3 pattern) and removes its marker script in
// [UnityTearDown]. Fixture-package types have no assembly reference here —
// they are reached through TypeCache like the existing Registry tests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class DiscoveryModeTests
    {
        // ── Fixture identities ────────────────────────────────────────────

        private const string TestAssembly = "OpWeave.UnityAdapter.Tests";
        private const string Project = SettingsExtensionExclusion.ProjectPackageId;

        // The project-assembly fixture Operation (Assets, no UPM package).
        private const string CustomAssembly = "OpWeave.Custom";
        private const string ResetOpId = "proj.transform.reset";
        private const string ResetOpType = "OpWeave.Custom.Transform.ResetTransformOperation";

        // The UPM-package fixture extension (com.opweave.fixture.valid).
        private const string ValidPackage = "com.opweave.fixture.valid";
        private const string ValidAssembly = "OpWeave.Fixture.Valid.Editor";
        private const string ValidOpId = "com.opweave.fixture.valid.op";
        private const string ValidOpType = "com.opweave.fixture.valid.ValidOperation";
        private const string ValidProviderId = "com.opweave.fixture.valid.types";
        private const string ValidProviderType = "com.opweave.fixture.valid.ValidProvider";
        private const string ValidTypeId = "com.opweave.fixture.valid.type";

        // A second project-assembly fixture whose declared ID is reserved:
        // under auto the reserved-namespace refusal still keeps it out, and
        // under whitelist it is simply unlisted.
        private const string ReservedNsOpId = "core.fixture.reservedns.op";
        private const string ReservedNsOpType =
            "OpWeave.Fixtures.ReservedNamespace.ReservedNamespaceOperation";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ReloadMarkerPath =>
            Path.Combine(ProjectRoot, "Packages", ValidPackage, "Editor",
                "DiscoveryModeReloadMarker.cs");

        private const string ReloadMarkerSource =
            "#nullable enable\n" +
            "namespace com.opweave.fixture.valid\n{\n" +
            "    internal static class DiscoveryModeReloadMarker\n" +
            "    {\n        public const int Value = 1;\n    }\n" +
            "}\n";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // ── T-W2.4-08: conditional drain + session-token probe ─────────────
        //
        // The [UnityTearDown] reload emitter this replaces —
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
        // to this crossing — never residue of an earlier run.
        private const string ProbeTokenKey =
            "OpWeave.W2408.DiscoveryMode.ProbeToken";

        private static string ProbeStateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveDiscoveryModeReload");

        private static string ProbeTokenFile => Path.Combine(ProbeStateDir, "reload-probe.txt");

        private static void ArmReloadProbe(string phase)
        {
            var token = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeTokenKey, token);
            Directory.CreateDirectory(ProbeStateDir);
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
                    "this Unity version — the teardown cannot prove a " +
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
        // teardown's own top level through the caller's MoveNext relay —
        // a nested yield would never reach Perform — and the whole
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
                        "DiscoveryModeTests." + phase +
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
                "DiscoveryModeTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "DiscoveryModeTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "DiscoveryModeTests." + phase +
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
                            "DiscoveryModeTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "DiscoveryModeTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // The cleanup pass — the marker delete can queue a compile; the
        // probe token dir is Library scratch. Every queued reload is
        // crossed inside DrainEditorState below, never yielded from
        // teardown itself.
        private static bool CleanupProbeArtifacts()
        {
            var dirty = false;
            if (File.Exists(ReloadMarkerPath))
            {
                File.Delete(ReloadMarkerPath);
                dirty = true;
            }
            var meta = ReloadMarkerPath + ".meta";
            if (File.Exists(meta))
            {
                File.Delete(meta);
                dirty = true;
            }
            if (Directory.Exists(ProbeStateDir))
            {
                Directory.Delete(ProbeStateDir, recursive: true);
            }
            return dirty;
        }

        // The fixture-entry half of the drain (T-W2.4-13): the
        // UnityTearDown below protects the NEXT fixture, but the first
        // test after a compile-producing predecessor can still meet a
        // reload that is armed-but-not-started — isCompiling already
        // false, IsScriptReloadRequested true. Draining here hands every
        // test — [Test] and [UnityTest] alike — a proven-quiet Editor
        // before its live build reads stage 1.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            // UTF resumes a reload-crossed setup enumerator at its
            // yield with every local reset — a null drain is that resume,
            // not a fresh start: rebuild the idempotent drain and relay
            // on instead of touching the dead reference.
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
        }

        // ── Probe Operations of this test assembly ────────────────────────
        //
        // The assembly lives inside the standard package, so these probes
        // are Standard candidates: admitted under every mode (disabledOperations
        // still wins — it precedes the mode check). Re-attributed through the
        // provenance seam they stand in for project-assembly candidates.

        // SPEC §C1 item 4: the fixture types compile into the test assembly,
        // so any of them the production stages register must publish a
        // complete-WF Example like a shipped Operation.
        private static OperationDescriptor Describe(string id, int version) =>
            Op.Define(id, version)
                .Summary("A discovery-mode fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(id, version, outputPort: "done"),
                    },
                };

        internal abstract class ProbeOperation : IOperation
        {
            public abstract OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    JsonValue.Parse("{}").Value!);

            public StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // Two declared versions of one Operation ID — a disabledOperations
        // entry must remove every version (SPEC §H4.1).
        [OpWeaveOperation("test.discovery.disabled", 1)]
        internal sealed class DisabledVersionOneOperation : ProbeOperation
        {
            internal static int s_ctorCalls;
            public DisabledVersionOneOperation() => s_ctorCalls++;
            public override OperationDescriptor Descriptor { get; } =
                Describe("test.discovery.disabled", 1);
        }

        [OpWeaveOperation("test.discovery.disabled", 2)]
        internal sealed class DisabledVersionTwoOperation : ProbeOperation
        {
            internal static int s_ctorCalls;
            public DisabledVersionTwoOperation() => s_ctorCalls++;
            public override OperationDescriptor Descriptor { get; } =
                Describe("test.discovery.disabled", 2);
        }

        // A Descriptor referencing a port type nobody offers: if this
        // candidate is ever admitted its OW-OP-003 verdict is inevitable, so
        // the absence of that verdict proves it was never constructed
        // (SPEC §D6 — unusable before Descriptor validation).
        [OpWeaveOperation("test.discovery.baddesc", 1)]
        internal sealed class BadDescriptorOperation : ProbeOperation
        {
            internal static int s_ctorCalls;
            public BadDescriptorOperation() => s_ctorCalls++;
            public override OperationDescriptor Descriptor { get; } =
                Op.Define("test.discovery.baddesc", 1)
                    .Summary("References a port type nobody offers.")
                    .Input("value", "acme.DiscoveryMissing", required: true)
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
        }

        // A plain probe whitelisted / excluded through the re-attributed
        // "@project" + test-assembly key.
        [OpWeaveOperation("test.discovery.probe", 1)]
        internal sealed class ProbeProjectOperation : ProbeOperation
        {
            internal static int s_ctorCalls;
            public ProbeProjectOperation() => s_ctorCalls++;
            public override OperationDescriptor Descriptor { get; } =
                Describe("test.discovery.probe", 1);
        }

        // ── Settings JSON fixtures (the real loader produces the value) ───

        private static string OpEntry(string packageId, string assemblyName, string id, long version) =>
            "{\"packageId\":\"" + packageId + "\",\"assemblyName\":\"" + assemblyName +
            "\",\"operationId\":\"" + id + "\",\"version\":" + version.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "}";

        private static string ProviderEntry(string packageId, string providerId) =>
            "{\"packageId\":\"" + packageId + "\",\"providerId\":\"" + providerId + "\"}";

        private static string WhitelistJson(
            IEnumerable<string> operations, IEnumerable<string> providers) =>
            "{\"operations\":[" + string.Join(",", operations) +
            "],\"typeProviders\":[" + string.Join(",", providers) + "]}";

        private static string SettingsJson(
            string discoveryMode, string? whitelistJson, params string[] disabledOperations) =>
            "{\"version\":2,\"extensions\":{\"discoveryMode\":\"" + discoveryMode +
            "\",\"integrityLevel\":\"weak\"" +
            (whitelistJson is null ? string.Empty : ",\"whitelist\":" + whitelistJson) +
            "},\"disabledOperations\":[" +
            string.Join(",", disabledOperations.Select(id => "\"" + id + "\"")) + "]}";

        private static OpWeaveSettings Settings(
            string discoveryMode, string? whitelistJson = null, params string[] disabledOperations)
        {
            var parsed = OpWeaveSettingsLoader.Parse(
                SettingsJson(discoveryMode, whitelistJson, disabledOperations));
            Assert.That(parsed.Ok, Is.True, "the settings fixture must parse: " +
                string.Join(" | ", parsed.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return parsed.Value!;
        }

        // ── Build helpers ─────────────────────────────────────────────────

        private static RegistryTypeStage.Seams LiveSeams(
            IExtensionExclusionPredicate predicate, params Type[] customTypes)
        {
            var seams = RegistryTypeStage.Production(predicate);
            if (customTypes.Length == 0)
            {
                return seams;
            }
            return seams with { ResolveProvenance = CustomProvenance(customTypes) };
        }

        // Re-attributes the listed types as non-standard (project assembly)
        // provenance — the same seam the Registry tests use, since this
        // test assembly itself lives inside the standard package.
        private static Func<Type, Result<EditorAssemblyProvenance>> CustomProvenance(
            params Type[] customTypes) =>
            type =>
            {
                var resolved = EditorAssemblyResolver.ResolveForType(type);
                if (resolved.Ok && customTypes.Contains(type))
                {
                    var real = resolved.Value!;
                    resolved = Result<EditorAssemblyProvenance>.Success(
                        new EditorAssemblyProvenance(
                            real.AssemblyName, real.CompiledAssemblyPath, real.IsPrecompiled,
                            OperationOrigin.Custom, package: null, real.CompilationAssembly));
                }
                return resolved;
            };

        private static RegistryBuildResult Rebuild(
            OpWeaveSettings settings, RegistryPublication publication, params Type[] customTypes) =>
            RegistryBuildHost.Rebuild(
                LiveSeams(SettingsExtensionExclusion.For(settings), customTypes),
                RegistryOperationStage.Production(),
                publication);

        private static Type FindDiscovered(string fullName, string what)
        {
            foreach (var type in TypeCache.GetTypesWithAttribute<OpWeaveOperationAttribute>())
            {
                if (type.FullName == fullName)
                {
                    return type;
                }
            }
            foreach (var type in TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>())
            {
                if (type.FullName == fullName)
                {
                    return type;
                }
            }
            Assert.Fail(what + " was not discovered — the fixture is missing");
            return null!;
        }

        private static FieldInfo CounterField(Type type)
        {
            var field = type.GetField("s_ctorCalls", BindingFlags.Public | BindingFlags.Static);
            Assert.That(field, Is.Not.Null,
                type.FullName + " must keep its s_ctorCalls constructor probe");
            return field!;
        }

        private static void ResetCalls(Type type) => CounterField(type).SetValue(null, 0);

        private static int Calls(Type type) => (int)CounterField(type).GetValue(null)!;

        private static void ResetFixtureCounters()
        {
            ResetCalls(FindDiscovered(ResetOpType, "the OpWeave.Custom fixture"));
            ResetCalls(FindDiscovered(ValidOpType, "the valid package Operation fixture"));
            ResetCalls(FindDiscovered(ValidProviderType, "the valid package provider fixture"));
            ResetCalls(FindDiscovered(ReservedNsOpType, "the reserved-namespace fixture"));
            DisabledVersionOneOperation.s_ctorCalls = 0;
            DisabledVersionTwoOperation.s_ctorCalls = 0;
            BadDescriptorOperation.s_ctorCalls = 0;
            ProbeProjectOperation.s_ctorCalls = 0;
        }

        private static TypeRef TR(string id) => TypeRef.Parse(id).Value!;

        private static bool Excluded(
            RegistrySnapshot snapshot, string candidateId, RegistryCandidateKind kind,
            string reason) =>
            snapshot.Exclusions.Any(e =>
                e.Kind == kind && e.CandidateId == candidateId && e.Reason == reason);

        // ── SPEC §M.5: auto registers every eligible probe ────────────────

        [Test]
        public void Auto_RegistersEveryEligibleProbe()
        {
            // A whitelist entry under auto is unused — the mode decides, not
            // the list (SPEC §H4.1).
            var settings = Settings("auto",
                WhitelistJson(
                    new[] { OpEntry("some.other.pkg", "Other.Assembly", "other.id", 1) },
                    Array.Empty<string>()));
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication);
            var snapshot = result.Snapshot!;
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(result.Stale, Is.False);

            Assert.That(snapshot.Find(ResetOpId, 1), Is.Not.Null,
                "auto registers the project-assembly Operation");
            Assert.That(snapshot.Find(ValidOpId, 1), Is.Not.Null,
                "auto registers the packaged Operation");
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.True,
                "auto registers the packaged TypeProvider's offer");
            Assert.That(Calls(FindDiscovered(ResetOpType, "reset")), Is.EqualTo(1));
            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(1));
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(1));

            // auto does not admit what other rules reject: the
            // reserved-namespace fixture is refused on its declared identity
            // without ever being constructed.
            Assert.That(Calls(FindDiscovered(ReservedNsOpType, "reserved ns")),
                Is.EqualTo(0));
            Assert.That(snapshot.Find(ReservedNsOpId, 1), Is.Null);

            // Nothing was a policy exclusion under auto.
            Assert.That(snapshot.Exclusions, Is.Empty);
            Assert.That(result.Exclusions, Is.Empty);
        }

        // ── SPEC §M.5 / §H4.1: whitelist registers only exact matches ─────

        [Test]
        public void Whitelist_RegistersOnlyExactMatches()
        {
            var probe = typeof(ProbeProjectOperation);
            var settings = Settings("whitelist",
                WhitelistJson(
                    new[]
                    {
                        OpEntry(Project, CustomAssembly, ResetOpId, 1),
                        OpEntry(ValidPackage, ValidAssembly, ValidOpId, 1),
                        OpEntry(Project, TestAssembly, "test.discovery.probe", 1),
                    },
                    new[] { ProviderEntry(ValidPackage, ValidProviderId) }));
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication, probe);
            var snapshot = result.Snapshot!;

            // The whitelisted (package, assembly, id, version) tuples are
            // constructed and registered — "@project" matches the
            // unpackaged assemblies (SPEC §H4.1).
            Assert.That(snapshot.Find(ResetOpId, 1), Is.Not.Null);
            Assert.That(snapshot.Find(ValidOpId, 1), Is.Not.Null);
            Assert.That(snapshot.Find("test.discovery.probe", 1), Is.Not.Null);
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.True);
            Assert.That(Calls(FindDiscovered(ResetOpType, "reset")), Is.EqualTo(1));
            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(1));
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(1));
            Assert.That(ProbeProjectOperation.s_ctorCalls, Is.EqualTo(1));

            // Unlisted custom candidates are never constructed.
            Assert.That(Calls(FindDiscovered(ReservedNsOpType, "reserved ns")), Is.EqualTo(0));
            Assert.That(snapshot.Find(ReservedNsOpId, 1), Is.Null);
            Assert.That(Excluded(snapshot, ReservedNsOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.OperationWhitelistReason), Is.True,
                "the unlisted project Operation publishes as an exclusion");

            // Standard candidates are never extensions — they still
            // register under whitelist.
            Assert.That(snapshot.Find("test.registry.good", 1), Is.Not.Null,
                "the standard test probe registers under whitelist");
            Assert.That(snapshot.Operations.Any(o => o.Origin == OperationOrigin.Standard
                    && o.Descriptor.Id == "unity.gameobject.create"),
                Is.True, "standard Operations register under whitelist");

            // Exclusions are Registry state, never registration failures.
            Assert.That(snapshot.Failures
                    .Any(f => f.OperationId == ReservedNsOpId || f.OperationId == ValidOpId),
                Is.False, "excluded candidates must not appear as failures");
        }

        [Test]
        public void Whitelist_NonMatchingKeys_ExcludeBeforeConstruction()
        {
            // Only the provider is whitelisted; both Operations have entries
            // that ALMOST match — a wrong version and a packageId spelled as
            // the assembly name instead of "@project".
            var settings = Settings("whitelist",
                WhitelistJson(
                    new[]
                    {
                        OpEntry(ValidPackage, ValidAssembly, ValidOpId, 2),
                        OpEntry(CustomAssembly, CustomAssembly, ResetOpId, 1),
                    },
                    new[] { ProviderEntry(ValidPackage, ValidProviderId) }));
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication);
            var snapshot = result.Snapshot!;

            Assert.That(snapshot.Find(ValidOpId, 1), Is.Null,
                "a version-mismatched whitelist entry does not admit the Operation");
            Assert.That(snapshot.Find(ResetOpId, 1), Is.Null,
                "a project assembly's packageId is \"@project\", not the assembly name");
            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(0),
                "the non-matching packaged Operation was never constructed");
            Assert.That(Calls(FindDiscovered(ResetOpType, "reset")), Is.EqualTo(0),
                "the non-matching project Operation was never constructed");
            Assert.That(Excluded(snapshot, ValidOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.OperationWhitelistReason), Is.True);
            Assert.That(Excluded(snapshot, ResetOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.OperationWhitelistReason), Is.True);

            // The whitelisted provider still registers: its package is not
            // punished for a candidate the policy excluded — the declared
            // operations[] entry the exclusion covers is "not observed", not
            // a declared-vs-observed mismatch (SPEC §X.3.2, §C3 rule 5).
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(1));
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.True,
                "the whitelisted provider's package must not be rejected over the excluded Operation");
            Assert.That(snapshot.CandidateFailures
                    .Any(f => f.AssemblyName == ValidAssembly), Is.False,
                "the exclusion must not surface as a package rejection");
        }

        [Test]
        public void Whitelist_UnlistedTypeProvider_NeverInvoked()
        {
            // The Operation is whitelisted, the provider is not.
            var settings = Settings("whitelist",
                WhitelistJson(
                    new[] { OpEntry(ValidPackage, ValidAssembly, ValidOpId, 1) },
                    Array.Empty<string>()));
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication);
            var snapshot = result.Snapshot!;

            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(0),
                "an unlisted TypeProvider is never invoked (SPEC §C0.2)");
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.False,
                "an excluded provider offers nothing");
            Assert.That(Excluded(snapshot, ValidProviderId, RegistryCandidateKind.TypeProvider,
                SettingsExtensionExclusion.ProviderWhitelistReason), Is.True);

            // The whitelisted Operation registers: its package's declared
            // operations[] is fully observed.
            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(1));
            Assert.That(snapshot.Find(ValidOpId, 1), Is.Not.Null);
        }

        [Test]
        public void Whitelist_ProjectProviderMatchesAtProject()
        {
            var alpha = typeof(RegistryTypeStageTests.AlphaProvider);
            var beta = typeof(RegistryTypeStageTests.BetaProvider);
            // Project (unpackaged) providers match (packageId, providerId)
            // with "@project" + the CLR full name.
            var settings = Settings("whitelist",
                WhitelistJson(
                    Array.Empty<string>(),
                    new[] { ProviderEntry(Project, alpha.FullName!) }));
            var publication = new RegistryPublication();

            var result = Rebuild(settings, publication, alpha, beta);
            var snapshot = result.Snapshot!;

            Assert.That(snapshot.Types.Resolve(TR("test.fixture.alpha")).Ok, Is.True,
                "the @project-whitelisted provider registers");
            Assert.That(snapshot.Types.Resolve(TR("test.fixture.beta")).Ok, Is.False,
                "the unlisted project provider is excluded");
            Assert.That(Excluded(snapshot, beta.FullName!, RegistryCandidateKind.TypeProvider,
                SettingsExtensionExclusion.ProviderWhitelistReason), Is.True);
            Assert.That(snapshot.Exclusions
                    .Any(e => e.CandidateId == beta.FullName && e.PackageId is null),
                Is.True, "a project candidate publishes a null (\"@project\") packageId");
        }

        // ── SPEC §H4.1: disabledOperations, all versions, both modes ──────

        [Test]
        public void DisabledOperations_ExcludeEveryVersion_InAuto()
        {
            var settings = Settings("auto", null,
                ValidOpId, ResetOpId, "test.discovery.disabled");
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication);
            var snapshot = result.Snapshot!;

            // Both versions of the disabled ID go — the list names IDs, not
            // (id, version) pairs (SPEC §H4.1).
            Assert.That(DisabledVersionOneOperation.s_ctorCalls, Is.EqualTo(0));
            Assert.That(DisabledVersionTwoOperation.s_ctorCalls, Is.EqualTo(0));
            Assert.That(snapshot.Find("test.discovery.disabled", 1), Is.Null);
            Assert.That(snapshot.Find("test.discovery.disabled", 2), Is.Null);

            // Project-assembly and UPM-package fixtures alike.
            Assert.That(Calls(FindDiscovered(ResetOpType, "reset")), Is.EqualTo(0),
                "the disabled project Operation was never constructed");
            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(0),
                "the disabled packaged Operation was never constructed");
            Assert.That(snapshot.Find(ResetOpId, 1), Is.Null);
            Assert.That(snapshot.Find(ValidOpId, 1), Is.Null);
            foreach (var id in new[] { ResetOpId, ValidOpId, "test.discovery.disabled" })
            {
                Assert.That(Excluded(snapshot, id, RegistryCandidateKind.Operation,
                    SettingsExtensionExclusion.DisabledReason), Is.True,
                    id + " publishes as a disabledOperations exclusion");
            }

            // The exclusion matched the attribute-declared identity: no
            // constructor ran to discover it.
            // The unaffected packaged provider still registers, and the
            // package is not punished for the excluded Operation.
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(1));
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.True);
            Assert.That(snapshot.Failures
                    .Any(f => f.OperationId == ValidOpId || f.OperationId == ResetOpId ||
                        f.OperationId == "test.discovery.disabled"),
                Is.False, "disabled candidates are not registration failures");
        }

        [Test]
        public void DisabledOperations_ExcludeEveryVersion_InWhitelist()
        {
            // disabledOperations has priority: a candidate both whitelisted
            // AND disabled is still excluded (SPEC §H4.1).
            var settings = Settings("whitelist",
                WhitelistJson(
                    new[]
                    {
                        OpEntry(Project, CustomAssembly, ResetOpId, 1),
                        OpEntry(ValidPackage, ValidAssembly, ValidOpId, 1),
                    },
                    new[] { ProviderEntry(ValidPackage, ValidProviderId) }),
                ValidOpId, "test.discovery.disabled");
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication);
            var snapshot = result.Snapshot!;

            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(0),
                "disabledOperations beats the whitelist entry for the same candidate");
            Assert.That(snapshot.Find(ValidOpId, 1), Is.Null);
            Assert.That(Excluded(snapshot, ValidOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.DisabledReason), Is.True);

            // The disabled ID goes at every version under whitelist too —
            // the mode never rescues it.
            Assert.That(DisabledVersionOneOperation.s_ctorCalls, Is.EqualTo(0));
            Assert.That(DisabledVersionTwoOperation.s_ctorCalls, Is.EqualTo(0));

            // Whitelisted and not disabled: the project Operation and the
            // provider still register; the package is not punished for the
            // disabled Operation (unobserved budget).
            Assert.That(Calls(FindDiscovered(ResetOpType, "reset")), Is.EqualTo(1));
            Assert.That(snapshot.Find(ResetOpId, 1), Is.Not.Null);
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(1));
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.True);
        }

        // ── SPEC §X.3.2: an excluded Operation satisfies only the manifest
        //    declaration naming its own declared (id, version) ────────────

        // The production seams with one package's opweave.extension.json
        // text rewritten: the declared operations[] entry names an
        // Operation the package does not implement, so the observed-fact
        // gate must prove an excluded candidate can never cover a
        // declaration of a different identity.
        private static RegistryTypeStage.Seams ManifestRewriteSeams(
            OpWeaveSettings settings, string packageId, string fromOperationId, string toOperationId)
        {
            var seams = RegistryTypeStage.Production(SettingsExtensionExclusion.For(settings));
            var inner = seams.PackageFacts;
            return seams with
            {
                PackageFacts = id =>
                {
                    var facts = inner(id);
                    if (facts is not null && facts.ManifestText is not null &&
                        string.Equals(id, packageId, StringComparison.Ordinal))
                    {
                        facts = facts with
                        {
                            ManifestText = facts.ManifestText.Replace(fromOperationId, toOperationId),
                        };
                    }
                    return facts;
                },
            };
        }

        [Test]
        public void DisabledOperations_ExcludedIdentity_CannotCoverADifferentDeclaration()
        {
            // The manifest declares an Operation the package does not
            // implement; the package contains only valid.op, which
            // disabledOperations excludes. The exclusion names
            // (valid.op, 1) — it satisfies only a declaration of that
            // same identity, so the missing declared Operation stays
            // uncovered: OW-OP-018 and the whole package — including its
            // admitted provider — registers nothing (SPEC §X.3.2).
            var settings = Settings("auto", null, ValidOpId);
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = RegistryBuildHost.Rebuild(
                ManifestRewriteSeams(
                    settings, ValidPackage, ValidOpId, "com.opweave.fixture.valid.missing"),
                RegistryOperationStage.Production(),
                publication);
            var snapshot = result.Snapshot!;

            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(0),
                "the disabled packaged Operation was never constructed");
            Assert.That(Excluded(snapshot, ValidOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.DisabledReason), Is.True);
            Assert.That(result.ExtensionFailures
                    .Any(f => f.AssemblyName == ValidAssembly &&
                        f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_018)),
                Is.True,
                "the uncovered declared Operation rejects the package (OW-OP-018)");
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.False,
                "the rejected package's provider registers nothing");
            Assert.That(snapshot.Find(ValidOpId, 1), Is.Null);
            Assert.That(snapshot.Find("com.opweave.fixture.valid.missing", 1), Is.Null);
        }

        [Test]
        public void DisabledOperations_ExcludedIdentity_SatisfiesItsOwnDeclaration()
        {
            // The manifest declares valid.op and the package contains
            // exactly that Operation, excluded by disabledOperations: the
            // declaration is satisfied by the exclusion itself — "not
            // observed" through an intentional exclusion, never a
            // declared-vs-observed mismatch (SPEC §X.3.2, §C3 rule 5).
            var settings = Settings("auto", null, ValidOpId);
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication);
            var snapshot = result.Snapshot!;

            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(0),
                "the disabled packaged Operation was never constructed");
            Assert.That(Excluded(snapshot, ValidOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.DisabledReason), Is.True,
                "the exclusion publishes on the snapshot as Registry state");
            Assert.That(result.ExtensionFailures
                    .Any(f => f.AssemblyName == ValidAssembly &&
                        f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_018)),
                Is.False, "an exclusion naming its own declaration is no mismatch");
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(1));
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.True,
                "the package stays admitted and its provider registers");
            Assert.That(snapshot.Find(ValidOpId, 1), Is.Null);
        }

        [Test]
        public void Whitelist_ExcludedIdentity_CannotCoverADifferentDeclaration()
        {
            // The same shape under whitelist exclusion: the provider is
            // whitelisted, the Operation is unlisted — its exclusion still
            // names only its own declared identity, so the rewritten
            // declaration stays uncovered and the package is rejected.
            var settings = Settings("whitelist",
                WhitelistJson(
                    Array.Empty<string>(),
                    new[] { ProviderEntry(ValidPackage, ValidProviderId) }));
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = RegistryBuildHost.Rebuild(
                ManifestRewriteSeams(
                    settings, ValidPackage, ValidOpId, "com.opweave.fixture.valid.missing"),
                RegistryOperationStage.Production(),
                publication);
            var snapshot = result.Snapshot!;

            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(0),
                "the unlisted packaged Operation was never constructed");
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(1),
                "the whitelisted provider was constructed before the gate's verdict");
            Assert.That(Excluded(snapshot, ValidOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.OperationWhitelistReason), Is.True);
            Assert.That(result.ExtensionFailures
                    .Any(f => f.AssemblyName == ValidAssembly &&
                        f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_018)),
                Is.True,
                "the uncovered declared Operation rejects the package (OW-OP-018)");
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.False,
                "the rejected package's provider registers nothing");
        }

        [Test]
        public void Whitelist_ExcludedIdentity_SatisfiesItsOwnDeclaration()
        {
            // Whitelist the provider, leave the Operation unlisted: the
            // manifest's declared valid.op is satisfied by the exclusion
            // naming that same identity — the package is not punished.
            var settings = Settings("whitelist",
                WhitelistJson(
                    Array.Empty<string>(),
                    new[] { ProviderEntry(ValidPackage, ValidProviderId) }));
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication);
            var snapshot = result.Snapshot!;

            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(0),
                "the unlisted packaged Operation was never constructed");
            Assert.That(Excluded(snapshot, ValidOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.OperationWhitelistReason), Is.True,
                "the exclusion publishes on the snapshot as Registry state");
            Assert.That(result.ExtensionFailures
                    .Any(f => f.AssemblyName == ValidAssembly &&
                        f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_018)),
                Is.False, "an exclusion naming its own declaration is no mismatch");
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(1));
            Assert.That(snapshot.Types.Resolve(TR(ValidTypeId)).Ok, Is.True,
                "the package stays admitted and its provider registers");
            Assert.That(snapshot.Find(ValidOpId, 1), Is.Null);
        }

        // ── SPEC §D6: excluded before Descriptor validation ───────────────

        [Test]
        public void Whitelist_ExcludesBeforeDescriptorValidation()
        {
            var probe = typeof(BadDescriptorOperation);

            // Control under auto: admitted, constructed, and its Descriptor
            // verdict lands — the failure exists only through construction.
            var publication = new RegistryPublication();
            BadDescriptorOperation.s_ctorCalls = 0;
            var auto = Rebuild(Settings("auto"), publication, probe);
            Assert.That(BadDescriptorOperation.s_ctorCalls, Is.EqualTo(1),
                "control: auto constructs the probe");
            Assert.That(auto.Snapshot!.CandidateFailures
                    .Any(f => f.CandidateId == "test.discovery.baddesc"), Is.True,
                "control: the bad Descriptor fails registration as OW-OP-003");

            // Under whitelist with no matching entry the same candidate is
            // excluded before its Descriptor is ever read: the verdict that
            // would follow from its Descriptor is unreachable.
            var publication2 = new RegistryPublication();
            BadDescriptorOperation.s_ctorCalls = 0;
            var settings = Settings("whitelist",
                WhitelistJson(Array.Empty<string>(), Array.Empty<string>()));
            var listed = Rebuild(settings, publication2, probe);
            var snapshot = listed.Snapshot!;

            Assert.That(BadDescriptorOperation.s_ctorCalls, Is.EqualTo(0),
                "a non-whitelisted Operation's constructor never runs (SPEC §D6)");
            Assert.That(snapshot.Find("test.discovery.baddesc", 1), Is.Null,
                "the excluded Operation is unusable — no version of it is registered");
            Assert.That(snapshot.CandidateFailures
                    .Any(f => f.CandidateId == "test.discovery.baddesc"), Is.False,
                "no Descriptor verdict exists for a never-constructed candidate");
            Assert.That(Excluded(snapshot, "test.discovery.baddesc",
                RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.OperationWhitelistReason), Is.True);
        }

        // ── The production rebuild consumes the loaded settings ───────────

        [Test]
        public void RebuildProduction_WhitelistModeAppliesLoadedSettings()
        {
            var validOp = FindDiscovered(ValidOpType, "the valid package Operation");
            var validProvider = FindDiscovered(ValidProviderType, "the valid package provider");
            try
            {
                ResetFixtureCounters();
                RegistryBuildHost.RebuildProduction(() => OpWeaveSettingsLoader.Parse(
                    SettingsJson("whitelist",
                        WhitelistJson(
                            new[] { OpEntry(Project, CustomAssembly, ResetOpId, 1) },
                            Array.Empty<string>()),
                        Array.Empty<string>())));

                var snapshot = RegistryPublication.Shared.Snapshot;
                Assert.That(snapshot, Is.Not.Null, "the production rebuild publishes a snapshot");
                Assert.That(RegistryBuildHost.Latest!.SettingsDiagnostics, Is.Empty);

                Assert.That(snapshot!.Find(ResetOpId, 1), Is.Not.Null,
                    "the whitelisted project Operation registers through the settings seam");
                Assert.That(snapshot.Find(ValidOpId, 1), Is.Null,
                    "the unlisted packaged Operation is excluded through the settings seam");
                Assert.That(Calls(validOp), Is.EqualTo(0));
                Assert.That(Calls(validProvider), Is.EqualTo(0));
                Assert.That(Excluded(snapshot, ValidOpId, RegistryCandidateKind.Operation,
                    SettingsExtensionExclusion.OperationWhitelistReason), Is.True,
                    "exclusions publish on the production snapshot as Registry state");
            }
            finally
            {
                // Restore the shared publication to a full build for later tests.
                RegistryBuildHost.RebuildProduction();
            }
        }

        // ── SPEC §T.3: the whitelist build is identical across a reload ───

        [UnityTest]
        public IEnumerator WhitelistBuild_SurvivesDomainReload()
        {
            File.WriteAllText(ReloadMarkerPath, ReloadMarkerSource);
            AssetDatabase.Refresh();
            ArmReloadProbe("marker-write");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the script change did not produce a domain reload");
            AssertReloadProbeThisSession("marker-write");

            // In the new domain the loaded settings still drive the
            // pre-instantiation exclusion — the excluded candidates' counters
            // start at 0 again and must stay there.
            var settings = Settings("whitelist",
                WhitelistJson(
                    new[] { OpEntry(Project, CustomAssembly, ResetOpId, 1) },
                    Array.Empty<string>()));
            var publication = new RegistryPublication();
            ResetFixtureCounters();

            var result = Rebuild(settings, publication);
            var snapshot = result.Snapshot!;
            Assert.That(result.Stale, Is.False);

            Assert.That(snapshot.Find(ResetOpId, 1), Is.Not.Null,
                "the whitelisted project Operation registers after the reload");
            Assert.That(Calls(FindDiscovered(ValidOpType, "valid op")), Is.EqualTo(0));
            Assert.That(Calls(FindDiscovered(ValidProviderType, "valid provider")), Is.EqualTo(0));
            Assert.That(Excluded(snapshot, ValidOpId, RegistryCandidateKind.Operation,
                SettingsExtensionExclusion.OperationWhitelistReason), Is.True);
            Assert.That(Excluded(snapshot, ValidProviderId, RegistryCandidateKind.TypeProvider,
                SettingsExtensionExclusion.ProviderWhitelistReason), Is.True);
        }
    }
}
