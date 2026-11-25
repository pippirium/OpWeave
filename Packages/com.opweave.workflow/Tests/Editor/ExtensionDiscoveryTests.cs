#nullable enable
// T-W2.12-01 [Edit] tests (live discovery half): local:T-W2.12-01:extension-metadata —
// real UPM fixture packages committed under TestProject/Packages/ (embedded
// packages are scanned at project load; runtime-written package dirs are not,
// which is why these fixtures are committed rather than installed mid-test —
// no manifest.json/packages-lock.json change). The valid fixture registers;
// every invalid variant is rejected entirely; coreApi failures (out of
// range, min ≥ max, malformed bound, missing bound, missing object) return
// OW-OP-012 with the declared range and the current CoreApi.Version in the
// hint; an unparseable manifest, an unsupported schemaVersion, a malformed
// extensionId, a malformed or above-Editor unity.minInclusive, a declared
// assembly not rooted in the package, a provider class in an undeclared
// package assembly, a typeProviders assembly reference outside
// 'assemblies', a declared Provider ID with no [OpWeaveTypeProvider] class
// in the named assembly and a provider class/ID count mismatch, example
// paths escaping or missing inside the package, reserved
// extensionId/Provider ID/Operation ID/Type ID, in-manifest duplicate
// Provider IDs and Operation (id, version) pairs, duplicate Provider IDs,
// Operation (id, version) identities and offered Type IDs across packages,
// declared-vs-observed operation-list mismatch and non-empty blueprints
// are each rejected as a whole extension (blueprints with OW-OP-010).
// Observed Operation Descriptor identities reach discovery through the
// Discover(observedOperations) entry point, attributed to packages by
// assembly name — the reservedop, dupop and dupop1/dupop2 fixtures each
// declare operations equal to their observed Descriptor set so the named
// rule is the fixture's only violation. Offered Type IDs reach discovery
// through Discover(observedTypes): the test instantiates each fixture
// package's [OpWeaveTypeProvider] class the way the registration stage
// will (SPEC §C3 steps 3–4) and reports the Type IDs GetTypes() actually
// yields — dupty1/dupty2 really offer one shared Type ID and reservedtype
// really offers a core.* Type ID. The valid fixture's declared
// example is a complete workflow that passes the static Validator
// (SPEC §X.7). The [UnityTest] re-runs discovery across a real domain
// reload (SPEC §T.3 pattern: RecompileScripts + domain sentinel).
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
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.12-01:extension-metadata — embedded fixture packages with
    /// valid and invalid opweave.extension.json variants discovered and
    /// validated in the real project.
    /// </summary>
    internal sealed class ExtensionDiscoveryTests
    {
        private const string ValidPackage = "com.opweave.fixture.valid";
        private const string BadSchemaPackage = "com.opweave.fixture.badschema";
        private const string InvRangePackage = "com.opweave.fixture.invrange";
        private const string UnityMinPackage = "com.opweave.fixture.unitymin";
        private const string UnityBadPackage = "com.opweave.fixture.unitybad";
        private const string BadAsmPackage = "com.opweave.fixture.badasm";
        private const string BadExamplesPackage = "com.opweave.fixture.badexamples";
        private const string BadRangePackage = "com.opweave.fixture.badrange";
        private const string BadBoundPackage = "com.opweave.fixture.badbound";
        private const string NoBoundPackage = "com.opweave.fixture.nobound";
        private const string NoCoreApiPackage = "com.opweave.fixture.nocoreapi";
        private const string BlueprintsPackage = "com.opweave.fixture.blueprints";
        private const string NoManifestPackage = "com.opweave.fixture.nomanifest";
        private const string MismatchPackage = "com.opweave.fixture.mismatch";
        private const string Dup1Package = "com.opweave.fixture.dup1";
        private const string Dup2Package = "com.opweave.fixture.dup2";
        private const string ReservedExtPackage = "com.opweave.fixture.reservedext";
        private const string ReservedPidPackage = "com.opweave.fixture.reservedpid";
        private const string ReservedOpPackage = "com.opweave.fixture.reservedop";
        private const string DupProvPackage = "com.opweave.fixture.dupprov";
        private const string DupProv1Package = "com.opweave.fixture.dupprov1";
        private const string DupProv2Package = "com.opweave.fixture.dupprov2";
        private const string DupOpPackage = "com.opweave.fixture.dupop";
        private const string DupOp1Package = "com.opweave.fixture.dupop1";
        private const string DupOp2Package = "com.opweave.fixture.dupop2";
        private const string BadJsonPackage = "com.opweave.fixture.badjson";
        private const string BadExtIdPackage = "com.opweave.fixture.badextid";
        private const string NoProviderPackage = "com.opweave.fixture.noprovider";
        private const string UndeclaredAsmPackage = "com.opweave.fixture.undeclaredasm";
        private const string BadProvRefPackage = "com.opweave.fixture.badprovref";
        private const string ExtraProvPackage = "com.opweave.fixture.extraprov";
        private const string DupTy1Package = "com.opweave.fixture.dupty1";
        private const string DupTy2Package = "com.opweave.fixture.dupty2";
        private const string ReservedTypePackage = "com.opweave.fixture.reservedtype";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // A transient script written into the valid fixture package to force
        // recompilation — the reload mechanism for the domain-crossing test.
        private static string ReloadMarkerPath =>
            Path.Combine(ProjectRoot, "Packages", ValidPackage, "Editor", "ReloadProbeMarker.cs");

        private const string ReloadMarkerSource =
            "#nullable enable\n" +
            "namespace com.opweave.fixture.valid\n{\n" +
            "    internal static class ReloadProbeMarker\n    {\n" +
            "        public const int Value = 1;\n    }\n" +
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
            "OpWeave.W2408.ExtensionDiscovery.ProbeToken";

        private static string ProbeStateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveExtensionDiscoveryReload");

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
                        "ExtensionDiscoveryTests." + phase +
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
                "ExtensionDiscoveryTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "ExtensionDiscoveryTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "ExtensionDiscoveryTests." + phase +
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
                            "ExtensionDiscoveryTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "ExtensionDiscoveryTests." + m_phase +
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

        private static ExtensionValidationOutcome? OutcomeFor(
            ExtensionDiscoveryReport report, string packageId) =>
            report.Extensions.FirstOrDefault(e => e.PackageId == packageId);

        // Observed Operation Descriptor identities supplied the way the
        // registration stage will once [OpWeaveOperation] exists
        // (T-W1.5-03): each entry attributes an (id, version) observation
        // to a fixture package through its declared Editor assembly.
        // reservedop and dupop then declare operations equal to what their
        // packages contain — the reserved prefix / in-manifest duplicate
        // stays the single violation — and dupop1/dupop2 carry matching
        // observed identities so only the cross-package duplicate rejects
        // them.
        private static IReadOnlyList<ObservedExtensionOperation> FixtureObservedOperations() =>
            new[]
            {
                new ObservedExtensionOperation(
                    "com.opweave.fixture.valid.op", 1, "OpWeave.Fixture.Valid.Editor"),
                new ObservedExtensionOperation(
                    "opweave.fixture.reserved.op", 1, "OpWeave.Fixture.ReservedOp.Editor"),
                new ObservedExtensionOperation(
                    "com.opweave.fixture.dupop.op", 1, "OpWeave.Fixture.DupOp.Editor"),
                new ObservedExtensionOperation(
                    "com.opweave.fixture.dupopshared.op", 1, "OpWeave.Fixture.DupOp1.Editor"),
                new ObservedExtensionOperation(
                    "com.opweave.fixture.dupopshared.op", 1, "OpWeave.Fixture.DupOp2.Editor"),
            };

        // Offered Type IDs the way the registration stage will supply them
        // (SPEC §C3 steps 3–4): the fixture providers are real
        // [OpWeaveTypeProvider] classes compiled into their packages' Editor
        // assemblies, so the test instantiates each packaged provider and
        // reports the Type IDs its GetTypes() actually yields, attributed by
        // defining assembly. dupty1/dupty2 both really offer
        // com.opweave.fixture.duptyshared.type and reservedtype really
        // offers a core.* Type ID; every other fixture provider yields an
        // empty set.
        private static IReadOnlyList<ObservedExtensionType> FixtureObservedTypes()
        {
            var observed = new List<ObservedExtensionType>();
            foreach (var type in TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>())
            {
                var provenance = EditorAssemblyResolver.ResolveForType(type);
                if (!provenance.Ok || provenance.Value!.PackageId is null ||
                    string.Equals(provenance.Value.PackageId,
                        EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal))
                {
                    continue;
                }
                var assemblyName = provenance.Value.AssemblyName;
                try
                {
                    if (Activator.CreateInstance(type) is ITypeProvider provider &&
                        provider.GetTypes() is { } definitions)
                    {
                        foreach (var definition in definitions)
                        {
                            if (definition is not null)
                            {
                                observed.Add(new ObservedExtensionType(definition.Id, assemblyName));
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // A provider that cannot be instantiated or enumerated
                    // yields no observations — the package's other rules
                    // still decide its outcome.
                }
            }
            return observed;
        }

        private static void AssertFixtureOutcomes(ExtensionDiscoveryReport report)
        {
            // Valid registers: accepted, manifest parsed, provider bound.
            var valid = OutcomeFor(report, ValidPackage);
            Assert.That(valid, Is.Not.Null, "valid fixture package was not discovered");
            Assert.That(valid!.Accepted, Is.True,
                () => string.Join("; ", valid.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(valid.Manifest!.ExtensionId, Is.EqualTo("com.opweave.fixture.valid"));
            Assert.That(valid.TypeProviders, Has.Count.EqualTo(1));
            Assert.That(valid.TypeProviders[0].ProviderId,
                Is.EqualTo("com.opweave.fixture.valid.types"));
            Assert.That(valid.TypeProviders[0].AssemblyName,
                Is.EqualTo("OpWeave.Fixture.Valid.Editor"));
            Assert.That(valid.TypeProviders[0].TypeFullName,
                Is.EqualTo(ValidPackage + ".ValidProvider"));

            // Manifest present but not parseable JSON → rejected entirely
            // (SG-02 internal diagnostics; the published OW-OP-013..019
            // mapping is T-W2.12-03). The package ships a real
            // [OpWeaveTypeProvider] class so the rejection cannot be
            // vacuous: it must never appear in any accepted outcome's
            // provider bindings.
            var badJson = OutcomeFor(report, BadJsonPackage);
            Assert.That(badJson, Is.Not.Null);
            Assert.That(badJson!.Accepted, Is.False);
            Assert.That(badJson.Diagnostics, Is.Not.Empty);
            Assert.That(badJson.TypeProviders, Is.Empty);
            Assert.That(report.Extensions
                .Where(e => e.Accepted)
                .SelectMany(e => e.TypeProviders)
                .Select(b => b.TypeFullName),
                Has.None.EqualTo("com.opweave.fixture.badjson.BadJsonProvider"),
                "a provider from an unparseable-manifest package must never be bound");

            // Unsupported schemaVersion → rejected entirely (internal
            // diagnostic; the published OW-OP-013..019 mapping is
            // T-W2.12-03).
            var badSchema = OutcomeFor(report, BadSchemaPackage);
            Assert.That(badSchema, Is.Not.Null);
            Assert.That(badSchema!.Accepted, Is.False);
            Assert.That(badSchema.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("schemaVersion").And.Some.Contains("not supported"));
            Assert.That(badSchema.TypeProviders, Is.Empty);

            // Malformed extensionId → rejected entirely (internal
            // diagnostic; reserved prefixes are covered separately below).
            var badExtId = OutcomeFor(report, BadExtIdPackage);
            Assert.That(badExtId, Is.Not.Null);
            Assert.That(badExtId!.Accepted, Is.False);
            Assert.That(badExtId.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("extensionId").And.Some.Contains("malformed"));
            Assert.That(badExtId.TypeProviders, Is.Empty);

            // coreApi out of range → OW-OP-012; nothing registers.
            var badRange = OutcomeFor(report, BadRangePackage);
            Assert.That(badRange, Is.Not.Null);
            Assert.That(badRange!.Accepted, Is.False);
            Assert.That(badRange.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(badRange.Diagnostics[0].Hint,
                Does.Contain("0.9.0").And.Contain("0.9.9").And.Contain(CoreApi.Version));
            Assert.That(badRange.TypeProviders, Is.Empty);

            // coreApi minInclusive ≥ maxExclusive → OW-OP-012 with declared
            // range + current version.
            var invRange = OutcomeFor(report, InvRangePackage);
            Assert.That(invRange, Is.Not.Null);
            Assert.That(invRange!.Accepted, Is.False);
            Assert.That(invRange.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(invRange.Diagnostics[0].Hint,
                Does.Contain("'2.0.0'..'1.0.0'").And.Contain(CoreApi.Version));
            Assert.That(invRange.TypeProviders, Is.Empty);

            // coreApi malformed bound → OW-OP-012 with declared range + current version.
            var badBound = OutcomeFor(report, BadBoundPackage);
            Assert.That(badBound, Is.Not.Null);
            Assert.That(badBound!.Accepted, Is.False);
            Assert.That(badBound.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(badBound.Diagnostics[0].Hint,
                Does.Contain("'1.0'..'2.0.0'").And.Contain(CoreApi.Version));
            Assert.That(badBound.TypeProviders, Is.Empty);

            // coreApi missing bound → OW-OP-012.
            var noBound = OutcomeFor(report, NoBoundPackage);
            Assert.That(noBound, Is.Not.Null);
            Assert.That(noBound!.Accepted, Is.False);
            Assert.That(noBound.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(noBound.Diagnostics[0].Hint,
                Does.Contain("'1.0.0'..'<missing>'").And.Contain(CoreApi.Version));
            Assert.That(noBound.TypeProviders, Is.Empty);

            // coreApi object absent → OW-OP-012.
            var noCoreApi = OutcomeFor(report, NoCoreApiPackage);
            Assert.That(noCoreApi, Is.Not.Null);
            Assert.That(noCoreApi!.Accepted, Is.False);
            Assert.That(noCoreApi.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(noCoreApi.Diagnostics[0].Hint,
                Does.Contain(CoreApi.Version));
            Assert.That(noCoreApi.TypeProviders, Is.Empty);

            // unity.minInclusive above the running Editor → rejected entirely.
            var unityMin = OutcomeFor(report, UnityMinPackage);
            Assert.That(unityMin, Is.Not.Null);
            Assert.That(unityMin!.Accepted, Is.False);
            Assert.That(unityMin.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("9999.0"));
            Assert.That(unityMin.TypeProviders, Is.Empty);

            // Malformed unity.minInclusive → rejected entirely.
            var unityBad = OutcomeFor(report, UnityBadPackage);
            Assert.That(unityBad, Is.Not.Null);
            Assert.That(unityBad!.Accepted, Is.False);
            Assert.That(unityBad.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("unity.minInclusive").And.Some.Contains("malformed"));
            Assert.That(unityBad.TypeProviders, Is.Empty);

            // Declared assembly not rooted in the package → rejected.
            var badAsm = OutcomeFor(report, BadAsmPackage);
            Assert.That(badAsm, Is.Not.Null);
            Assert.That(badAsm!.Accepted, Is.False);
            Assert.That(badAsm.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("OpWeave.Fixture.BadAsm.Ghost.Editor")
                    .And.Some.Contains("not rooted"));
            Assert.That(badAsm.TypeProviders, Is.Empty);

            // Provider class in a package assembly the manifest does not
            // declare → rejected entirely.
            var undeclaredAsm = OutcomeFor(report, UndeclaredAsmPackage);
            Assert.That(undeclaredAsm, Is.Not.Null);
            Assert.That(undeclaredAsm!.Accepted, Is.False);
            Assert.That(undeclaredAsm.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("undeclared assembly")
                    .And.Some.Contains("OpWeave.Fixture.UndeclaredAsm.Editor"));
            Assert.That(undeclaredAsm.TypeProviders, Is.Empty);

            // typeProviders entry naming an assembly not listed in
            // 'assemblies' → rejected entirely.
            var badProvRef = OutcomeFor(report, BadProvRefPackage);
            Assert.That(badProvRef, Is.Not.Null);
            Assert.That(badProvRef!.Accepted, Is.False);
            Assert.That(badProvRef.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("OpWeave.Fixture.BadProvRef.Other.Editor")
                    .And.Some.Contains("not listed in 'assemblies'"));
            Assert.That(badProvRef.TypeProviders, Is.Empty);

            // Declared Provider ID but no [OpWeaveTypeProvider] class in the
            // named (existing) package assembly → rejected entirely.
            var noProvider = OutcomeFor(report, NoProviderPackage);
            Assert.That(noProvider, Is.Not.Null);
            Assert.That(noProvider!.Accepted, Is.False);
            Assert.That(noProvider.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("No [OpWeaveTypeProvider] class found")
                    .And.Some.Contains("OpWeave.Fixture.NoProvider.Editor"));
            Assert.That(noProvider.TypeProviders, Is.Empty);

            // Two provider classes but one declared Provider ID → the
            // one-to-one pairing fails and the extension is rejected.
            var extraProv = OutcomeFor(report, ExtraProvPackage);
            Assert.That(extraProv, Is.Not.Null);
            Assert.That(extraProv!.Accepted, Is.False);
            Assert.That(extraProv.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("2 [OpWeaveTypeProvider] class(es)")
                    .And.Some.Contains("1 Provider ID(s)"));
            Assert.That(extraProv.TypeProviders, Is.Empty);

            // Example paths escaping the package root or missing inside it
            // → rejected entirely.
            var badExamples = OutcomeFor(report, BadExamplesPackage);
            Assert.That(badExamples, Is.Not.Null);
            Assert.That(badExamples!.Accepted, Is.False);
            Assert.That(badExamples.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("escapes the package root")
                    .And.Some.Contains("was not found"));
            Assert.That(badExamples.TypeProviders, Is.Empty);

            // Non-empty blueprints → OW-OP-010.
            var blueprints = OutcomeFor(report, BlueprintsPackage);
            Assert.That(blueprints, Is.Not.Null);
            Assert.That(blueprints!.Accepted, Is.False);
            Assert.That(blueprints.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_010));
            Assert.That(blueprints.TypeProviders, Is.Empty);

            // Candidates without a manifest → rejected entirely.
            var noManifest = OutcomeFor(report, NoManifestPackage);
            Assert.That(noManifest, Is.Not.Null);
            Assert.That(noManifest!.Accepted, Is.False);
            Assert.That(noManifest.ManifestPresent, Is.False);
            Assert.That(noManifest.TypeProviders, Is.Empty);

            // Declared operations ≠ discovered Descriptors → rejected.
            var mismatch = OutcomeFor(report, MismatchPackage);
            Assert.That(mismatch, Is.Not.Null);
            Assert.That(mismatch!.Accepted, Is.False);
            Assert.That(mismatch.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("does not match"));
            Assert.That(mismatch.TypeProviders, Is.Empty);

            // Duplicate extensionId across packages → both rejected.
            var dup1 = OutcomeFor(report, Dup1Package);
            var dup2 = OutcomeFor(report, Dup2Package);
            Assert.That(dup1, Is.Not.Null);
            Assert.That(dup2, Is.Not.Null);
            Assert.That(dup1!.Accepted, Is.False);
            Assert.That(dup2!.Accepted, Is.False);
            Assert.That(dup1.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("more than one package"));

            // Reserved namespaces: extensionId, Provider ID, Operation ID
            // and offered Type ID each reject the whole extension (internal
            // diagnostic; the published OW-OP-013..019 mapping is
            // T-W2.12-03).
            var reservedExt = OutcomeFor(report, ReservedExtPackage);
            Assert.That(reservedExt, Is.Not.Null);
            Assert.That(reservedExt!.Accepted, Is.False);
            Assert.That(reservedExt.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("reserved"));
            Assert.That(reservedExt.TypeProviders, Is.Empty);

            var reservedPid = OutcomeFor(report, ReservedPidPackage);
            Assert.That(reservedPid, Is.Not.Null);
            Assert.That(reservedPid!.Accepted, Is.False);
            Assert.That(reservedPid.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Provider ID").And.Some.Contains("reserved"));
            Assert.That(reservedPid.TypeProviders, Is.Empty);

            var reservedOp = OutcomeFor(report, ReservedOpPackage);
            Assert.That(reservedOp, Is.Not.Null);
            Assert.That(reservedOp!.Accepted, Is.False);
            Assert.That(reservedOp.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Operation ID").And.Some.Contains("reserved"));
            Assert.That(reservedOp.TypeProviders, Is.Empty);

            // Its provider really offers a core.* Type ID → rejected
            // entirely.
            var reservedType = OutcomeFor(report, ReservedTypePackage);
            Assert.That(reservedType, Is.Not.Null);
            Assert.That(reservedType!.Accepted, Is.False);
            Assert.That(reservedType.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("reserved"));
            Assert.That(reservedType.TypeProviders, Is.Empty);

            // In-manifest duplicate Provider ID / Operation (id, version).
            var dupProv = OutcomeFor(report, DupProvPackage);
            Assert.That(dupProv, Is.Not.Null);
            Assert.That(dupProv!.Accepted, Is.False);
            Assert.That(dupProv.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("duplicate Provider ID"));
            Assert.That(dupProv.TypeProviders, Is.Empty);

            var dupOp = OutcomeFor(report, DupOpPackage);
            Assert.That(dupOp, Is.Not.Null);
            Assert.That(dupOp!.Accepted, Is.False);
            Assert.That(dupOp.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("duplicate Operation"));
            Assert.That(dupOp.TypeProviders, Is.Empty);

            // Duplicate Provider ID across packages → both rejected.
            var dupProv1 = OutcomeFor(report, DupProv1Package);
            var dupProv2 = OutcomeFor(report, DupProv2Package);
            Assert.That(dupProv1, Is.Not.Null);
            Assert.That(dupProv2, Is.Not.Null);
            Assert.That(dupProv1!.Accepted, Is.False);
            Assert.That(dupProv2!.Accepted, Is.False);
            Assert.That(dupProv1.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Provider ID").And.Some.Contains("more than one package"));
            Assert.That(dupProv1.TypeProviders, Is.Empty);
            Assert.That(dupProv2.TypeProviders, Is.Empty);

            // Duplicate Operation (id, version) across packages → both
            // rejected, no provider bindings. Each fixture's declared
            // operations[] equals the observed Descriptor set supplied to
            // Discover(), so the cross-package duplicate is the only
            // violation.
            var dupOp1 = OutcomeFor(report, DupOp1Package);
            var dupOp2 = OutcomeFor(report, DupOp2Package);
            Assert.That(dupOp1, Is.Not.Null);
            Assert.That(dupOp2, Is.Not.Null);
            Assert.That(dupOp1!.Accepted, Is.False);
            Assert.That(dupOp2!.Accepted, Is.False);
            Assert.That(dupOp1.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Operation").And.Some.Contains("more than one package"));
            Assert.That(dupOp2.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Operation").And.Some.Contains("more than one package"));
            Assert.That(dupOp1.TypeProviders, Is.Empty);
            Assert.That(dupOp2.TypeProviders, Is.Empty);

            // Duplicate offered Type ID across packages → both rejected, no
            // type or provider bindings. Both providers really yield the
            // same Type ID, observed through instantiated GetTypes().
            var dupTy1 = OutcomeFor(report, DupTy1Package);
            var dupTy2 = OutcomeFor(report, DupTy2Package);
            Assert.That(dupTy1, Is.Not.Null);
            Assert.That(dupTy2, Is.Not.Null);
            Assert.That(dupTy1!.Accepted, Is.False);
            Assert.That(dupTy2!.Accepted, Is.False);
            Assert.That(dupTy1.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("more than one package"));
            Assert.That(dupTy2.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("more than one package"));
            Assert.That(dupTy1.TypeProviders, Is.Empty);
            Assert.That(dupTy2.TypeProviders, Is.Empty);
        }

        // ---------- local:T-W2.12-01:extension-metadata ----------

        [Test]
        public void InstalledFixturePackages_ValidRegisters_InvalidRejectedEntirely()
        {
            var report = ExtensionDiscovery.Discover(
                FixtureObservedOperations(), FixtureObservedTypes());

            Assert.That(report.Extensions.Select(e => e.PackageId),
                Has.None.EqualTo(EditorAssemblyResolver.StandardPackageId),
                "the standard package is never an extension candidate");
            AssertFixtureOutcomes(report);
        }

        // SPEC §T.3: the same discovery outcome must hold after a real
        // domain reload. A marker script is added to the valid fixture
        // package so the recompile is genuine. Locals do not survive the
        // reload, so every assertion re-derives its state post-reload.
        [UnityTest]
        public IEnumerator DiscoveryOutcome_SurvivesDomainReload()
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

            var report = ExtensionDiscovery.Discover(
                FixtureObservedOperations(), FixtureObservedTypes());
            AssertFixtureOutcomes(report);
        }

        // SPEC §X.7: a declared example must be a complete workflow that
        // passes the static Validator — never a node fragment. The valid
        // fixture's manifest lists this file in 'examples'.
        [Test]
        public void ValidFixtureExample_IsACompleteValidWorkflow()
        {
            var examplePath = Path.Combine(
                ProjectRoot, "Packages", ValidPackage,
                "Samples~", "Workflows", "example.opweave.json");
            Assert.That(File.Exists(examplePath), Is.True,
                "the declared example file is missing from the valid fixture package");

            var read = WorkflowSerializer.Read(File.ReadAllText(examplePath));
            Assert.That(read.Ok, Is.True,
                () => string.Join("; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));

            var report = new WorkflowCompiler().Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));
            Assert.That(report.Ok, Is.True,
                () => string.Join("; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }
    }
}
