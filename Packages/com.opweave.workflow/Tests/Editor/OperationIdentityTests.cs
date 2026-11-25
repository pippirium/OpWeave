#nullable enable

// T-W2.2-08 [Edit] tests (SPEC §C2 test 2, §C3 tests 1–2): the structural
// attribute-vs-Descriptor consistency of every discovered Operation, the
// pre-instantiation OW-OP-004 refusal of a duplicated declared (id, version),
// the coexistence of the same id at different versions, and the OW-OP-007
// refusal of a reserved-namespace declared ID in a project assembly and in
// a UPM package — each refusal decided from the attribute alone, so the
// constructor-call counters stay at 0 (SPEC §C3 rules 3–5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using UnityEditor;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.2-08:operation-identity — declarative Operation identity
    /// contract tests (SPEC §C2 item 2, §C3 stages 2–3 and rules 3–5).
    /// </summary>
    internal sealed class OperationIdentityTests
    {
        private const string ReservedProjectOperationId = "core.fixture.reservedns.op";
        private const string ReservedUpmOperationId = "unity.fixture.reservedns.op";
        private const string ReservedProjectType =
            "OpWeave.Fixtures.ReservedNamespace.ReservedNamespaceOperation";
        private const string ReservedUpmType =
            "com.opweave.fixture.reservedns.ReservedNsOperation";

        // The same seam shape RegistryPublicationTests uses: every delegate
        // is the production one except the candidate type list.
        private static RegistryTypeStage.Seams SeamsFor(
            IEnumerable<Type> operationTypes) =>
            new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => operationTypes,
                ProviderTypes: () => TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());

        private static RegistryBuildResult Rebuild(
            RegistryTypeStage.Seams typeSeams, RegistryPublication publication) =>
            RegistryBuildHost.Rebuild(
                typeSeams, RegistryOperationStage.Production(), publication);

        private static RegistryBuildResult RebuildProduction()
        {
            var publication = new RegistryPublication();
            return RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                publication);
        }

        // ── fixture-entry drain (T-W2.4-13) ────────────────────────────
        //
        // RebuildProduction reads the production §C3 stage-1 seam — the
        // real Editor compile state — so a compile a predecessor fixture
        // left queued behind the runner's reload lock (isCompiling still
        // false, IsScriptReloadRequested already armed) hands the first
        // live build a stale stage 1. The UnitySetUp drain below is the
        // fixture-entry half of the At01AgentStepsTests DrainEditorState
        // idiom (T-W2.4-07): a pending compile or requested reload is
        // crossed only at the bounded DrainedScriptReload boundary, and
        // everything else settles on bounded plain frames inside the
        // QuietSettle window — before the test body runs at all, plain
        // [Test] included.
        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the drain
        // reaches it through reflection (the At01AgentStepsTests probe,
        // T-W2.4-07). A reload already armed behind the runner's reload
        // lock surfaces here while isCompiling is false — the deferred
        // state an isCompiling-only check misses.
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
        // a live build below.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        // The pending probe the drain trusts is only as strong as its
        // shape: prove it once up front (the At01AgentStepsTests pattern,
        // T-W2.4-07) so the fixture fails here — before any test —
        // instead of silently degrading to an isCompiling-only check
        // that misses a requested reload armed behind the lock.
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
                    "this Unity version — the setup cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // The fixture-entry guard: every test — [Test] and [UnityTest]
        // alike — starts on a proven-quiet Editor, so the production
        // stage-1 read in RebuildProduction can never go stale on a
        // predecessor's queued compile.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
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

        // The contract the fixture hands each of its tests: no pending
        // script reload, no compile or import in flight and a clean
        // compile. A pending compile or requested reload is crossed only
        // at DrainedScriptReload — the bounded instruction that unlocks
        // once the pending state is proven — and everything else settles
        // on bounded plain frames. A queued compile surfaces as
        // isCompiling a frame or two after Refresh returns, so the wait
        // only trusts a sustained quiet spell (the QuietSettle
        // convention): quiet flags reset on every busy frame and a
        // periodic Refresh flushes the import queue so a queued compile
        // surfaces inside the window, never behind it. The instruction
        // the nested enumerator produces is yielded DIRECTLY from the
        // setup's own top level through the caller's MoveNext relay — a
        // nested yield would never reach Perform — and the whole
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
                        "OperationIdentityTests." + phase +
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
                "OperationIdentityTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "OperationIdentityTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "OperationIdentityTests." + phase +
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
        // unconditional RecompileScripts(false) unlock the drain idiom
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
                            "OperationIdentityTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "OperationIdentityTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // Reads the static s_ctorCalls probe of a fixture class discovered
        // through TypeCache — the fixture classes live in other assemblies,
        // so they are reached by reflection like the §X.3.2 gate tests do.
        private static FieldInfo CtorProbeOf(string typeFullName)
        {
            var type = TypeCache
                .GetTypesWithAttribute<OpWeaveOperationAttribute>()
                .FirstOrDefault(t => t.FullName == typeFullName);
            Assert.That(type, Is.Not.Null, $"fixture type '{typeFullName}' was not discovered");
            var field = type!.GetField("s_ctorCalls", BindingFlags.Public | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, $"fixture type '{typeFullName}' lost its constructor probe");
            return field!;
        }

        // ── SPEC §C2 test 2: every discovered Operation's attribute declares
        //    the identity its Descriptor reports. ──
        [Test]
        public void EveryDiscoveredOperation_AttributeMatchesDescriptor()
        {
            var checkedCount = 0;
            var checkedStandard = false;
            var mismatches = new List<string>();
            foreach (var type in TypeCache.GetTypesWithAttribute<OpWeaveOperationAttribute>())
            {
                var declared = OperationEligibility.DeclaredIdentityOf(type);
                Assert.That(declared, Is.Not.Null,
                    type.FullName + ": the discovery attribute must carry (Id, Version)");
                Assert.That(declared!.Id, Is.Not.Null.And.Not.Empty,
                    type.FullName + ": the declared Id must not be empty");

                // A class violating the §C2 item-1 conditions or failing
                // construction produces no Descriptor to compare — its
                // OW-OP-008 is the eligibility tests' contract, not this
                // one's.
                if (OperationEligibility.Check(type).Count != 0)
                {
                    continue;
                }
                var instance = OperationEligibility.Instantiate(type);
                if (!instance.Ok || instance.Value is null)
                {
                    continue;
                }
                OperationDescriptor? descriptor;
                try
                {
                    descriptor = instance.Value!.Descriptor;
                }
                catch (Exception)
                {
                    continue;
                }
                if (descriptor is null)
                {
                    continue;
                }

                checkedCount++;
                if (string.Equals(type.Assembly.GetName().Name, "OpWeave.Operations", StringComparison.Ordinal))
                {
                    checkedStandard = true;
                }
                if (!string.Equals(declared.Id, descriptor.Id, StringComparison.Ordinal) ||
                    declared.Version != descriptor.Version)
                {
                    mismatches.Add(
                        $"{type.FullName}: attribute ({declared.Id}, {declared.Version}) vs " +
                        $"Descriptor ({descriptor.Id}, {descriptor.Version})");
                }
            }

            Assert.That(mismatches, Is.Empty,
                () => "every [OpWeaveOperation(id, version)] must equal its Descriptor " +
                    "identity (SPEC §C2 item 2):\n" + string.Join("\n", mismatches));
            Assert.That(checkedStandard, Is.True,
                "the structural check must cover the standard Operations");
            Assert.That(checkedCount, Is.GreaterThan(30),
                "the standard Operations plus the project and fixture Ops were all checked");
        }

        // ── SPEC §C3 test 2: two classes declaring the same (id, version)
        //    are both refused OW-OP-004 — decided on the attribute before
        //    either constructor runs. ──
        [Test]
        public void DuplicateDeclaredIdentity_RefusedBeforeConstruction()
        {
            OperationIdentityFixtures.DuplicateIdentityOperationA.s_ctorCalls = 0;
            OperationIdentityFixtures.DuplicateIdentityOperationB.s_ctorCalls = 0;

            var publication = new RegistryPublication();
            var result = Rebuild(SeamsFor(new[]
            {
                typeof(OperationIdentityFixtures.DuplicateIdentityOperationA),
                typeof(OperationIdentityFixtures.DuplicateIdentityOperationB),
                typeof(OperationIdentityFixtures.UnrelatedIdentityOperation),
            }), publication);

            Assert.That(result.Stale, Is.False);
            var snapshot = result.Snapshot!;

            Assert.That(
                OperationIdentityFixtures.DuplicateIdentityOperationA.s_ctorCalls,
                Is.EqualTo(0),
                "a declared-identity duplicate must never be constructed (SPEC §C3 rule 4)");
            Assert.That(
                OperationIdentityFixtures.DuplicateIdentityOperationB.s_ctorCalls,
                Is.EqualTo(0),
                "a declared-identity duplicate must never be constructed (SPEC §C3 rule 4)");

            // Both candidates are refused with OW-OP-004; the unrelated
            // Operation registers (SPEC §C3 step 6).
            var duplicates = snapshot.Failures
                .Where(f => f.OperationId == OperationIdentityFixtures.DuplicateOperationId)
                .ToList();
            Assert.That(duplicates, Has.Count.EqualTo(2));
            Assert.That(duplicates.All(f => f.Kind == RegistryChangeKinds.Failed), Is.True);
            Assert.That(
                duplicates.All(f => f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_004)),
                Is.True);
            Assert.That(
                snapshot.Find(OperationIdentityFixtures.DuplicateOperationId, 1), Is.Null);
            Assert.That(
                snapshot.Find(OperationIdentityFixtures.UnrelatedOperationId, 1), Is.Not.Null,
                "an unrelated Operation must still register");
        }

        // ── SPEC §C3 rule 4: the same declared id at a different version
        //    coexists — no refusal. ──
        [Test]
        public void SameDeclaredIdDifferentVersions_BothRegister()
        {
            var publication = new RegistryPublication();
            var result = Rebuild(SeamsFor(new[]
            {
                typeof(OperationIdentityFixtures.CoexistingVersionOneOperation),
                typeof(OperationIdentityFixtures.CoexistingVersionTwoOperation),
            }), publication);

            var snapshot = result.Snapshot!;
            Assert.That(
                snapshot.Find(OperationIdentityFixtures.CoexistingOperationId, 1), Is.Not.Null);
            Assert.That(
                snapshot.Find(OperationIdentityFixtures.CoexistingOperationId, 2), Is.Not.Null);
            Assert.That(
                snapshot.Failures.Any(f =>
                    f.OperationId == OperationIdentityFixtures.CoexistingOperationId),
                Is.False);
        }

        // ── SPEC §C3 test 1 (reserved-namespace part): a Custom Operation
        //    declaring a reserved ID is refused OW-OP-007 on the attribute —
        //    the constructor never runs and nothing registers — once for a
        //    project assembly and once for a UPM package. ──
        [Test]
        public void ReservedDeclaredId_ProjectAssembly_RefusedBeforeConstruction()
        {
            var probe = CtorProbeOf(ReservedProjectType);
            probe.SetValue(null, 0);

            var result = RebuildProduction();

            Assert.That(probe.GetValue(null), Is.EqualTo(0),
                "a reserved-namespace declared ID must never be constructed (SPEC §C3 rule 3)");
            var snapshot = result.Snapshot!;
            Assert.That(snapshot.Find(ReservedProjectOperationId, 1), Is.Null);
            Assert.That(snapshot.Failures.Any(f =>
                    f.OperationId == ReservedProjectOperationId &&
                    f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_007)),
                Is.True,
                "the project-assembly reserved ID must surface as OW-OP-007 in the snapshot");
        }

        [Test]
        public void ReservedDeclaredId_UpmPackage_RefusedBeforeConstruction()
        {
            var probe = CtorProbeOf(ReservedUpmType);
            probe.SetValue(null, 0);

            var result = RebuildProduction();

            Assert.That(probe.GetValue(null), Is.EqualTo(0),
                "a reserved-namespace declared ID must never be constructed (SPEC §C3 rule 3)");
            var snapshot = result.Snapshot!;
            Assert.That(snapshot.Find(ReservedUpmOperationId, 1), Is.Null);
            Assert.That(snapshot.Failures.Any(f =>
                    f.OperationId == ReservedUpmOperationId &&
                    f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_007)),
                Is.True,
                "the UPM-package reserved ID must surface as OW-OP-007 in the snapshot");
        }
    }
}
