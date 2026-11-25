#nullable enable

// T-W2.12-03 [Edit] tests: local:T-W2.12-03:registration-diagnostics —
// SPEC §C3/§J.1 OW-OP-013..019 published on RegistrySnapshot.CandidateFailures.
// One invalid fixture per code registers its exact assigned diagnostic;
// unrelated valid Operations/TypeProviders keep registering; diagnostics
// carry no secrets. The [UnityTest] crosses a real domain reload: a probe
// provider written under Assets/ is discovered after RecompileScripts +
// a domain sentinel (the SPEC §T.3 batchmode pattern — like
// WeakFingerprintTests, `yield return new WaitForDomainReload()` is not
// used because the reload can complete inside RecompileScripts' own
// wait) and rejected with its OW-OP-016 while the published Registry
// stays healthy.
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
    internal sealed class RegistrationDiagnosticsTests
    {
        private const string SecretMarker = "sk-secret-9f3b-regdiag";
        private const string ReloadProbePrefix = "OpWeaveRegDiagReload_";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static IEnumerable<Diagnostic> AllDiagnostics(RegistryBuildResult result)
        {
            var snapshot = result.Snapshot;
            if (snapshot is not null)
            {
                foreach (var failure in snapshot.CandidateFailures)
                {
                    foreach (var d in failure.Diagnostics)
                    {
                        yield return d;
                    }
                }
                foreach (var failure in snapshot.Failures)
                {
                    foreach (var d in failure.Diagnostics)
                    {
                        yield return d;
                    }
                }
            }
            foreach (var failure in result.TypeStage.Failures
                .Concat(result.OperationFailures)
                .Concat(result.ExtensionFailures))
            {
                foreach (var d in failure.Diagnostics)
                {
                    yield return d;
                }
            }
        }

        private static List<RegistryCandidateFailure> CandidatesFor(
            RegistryBuildResult result, string text) =>
            result.Snapshot!.CandidateFailures
                .Where(f =>
                    f.CandidateId.Contains(text) ||
                    (f.ProviderId ?? string.Empty).Contains(text))
                .ToList();

        // ── Fixture providers/Operations (project candidates in this test
        //    assembly; Provider ID = CLR FullName). The test.diag.* ids do
        //    not collide with any other fixture's offers. ──

        private static TypeDefinition Def(string id, IValueCodec? codec = null,
            params string[] baseTypes) =>
            new TypeDefinition(id, TypeKind.Primitive, 0,
                baseTypes, null, "Diag fixture type.", codec ?? StringCodec.Instance);

        private sealed class ThrowingNormalizeCodec : IValueCodec
        {
            internal static readonly ThrowingNormalizeCodec Instance = new ThrowingNormalizeCodec();

            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                value.ToToken().Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? Array.Empty<Diagnostic>()
                    : new[] { ErrorCodes.Create(ErrorCodes.OW_TYPE_003, "Not a string.") };

            public JsonValue Normalize(JsonValue value) =>
                throw new InvalidOperationException("probe failure " + SecretMarker);
        }

        // OW-OP-013: two providers offering the same Type ID — both invalidated.
        [OpWeaveTypeProvider]
        internal sealed class DiagCollideAProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { Def("test.diag.shared") };
        }

        [OpWeaveTypeProvider]
        internal sealed class DiagCollideBProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { Def("test.diag.shared") };
        }

        // OW-OP-014: a base-type reference nothing registers.
        [OpWeaveTypeProvider]
        internal sealed class DiagBaselessProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { Def("test.diag.child", null, "test.diag.absent.base") };
        }

        // OW-OP-015: a base-type cycle.
        [OpWeaveTypeProvider]
        internal sealed class DiagCycleProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[]
            {
                Def("test.diag.cycle.a", null, "test.diag.cycle.b"),
                Def("test.diag.cycle.b", null, "test.diag.cycle.a"),
            };
        }

        // OW-OP-016 (missing Codec) and OW-OP-016 (broken Normalize contract).
        [OpWeaveTypeProvider]
        internal sealed class DiagNoCodecProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { new TypeDefinition("test.diag.nocodec", TypeKind.Primitive, 0,
                    Array.Empty<string>(), null, "Codec-less fixture.", null!) };
        }

        [OpWeaveTypeProvider]
        internal sealed class DiagBadNormalizeProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { Def("test.diag.badnorm", ThrowingNormalizeCodec.Instance) };
        }

        // Internal diagnostics (no §J.1 code assigned): a non-canonical
        // Type ID with a valid Codec, and each GetTypes() failure form of
        // an already constructed provider (SPEC §C3 step 4 — OW-OP-016 is
        // the Codec bucket only, OW-OP-019 the construction failure only).
        [OpWeaveTypeProvider]
        internal sealed class DiagMalformedIdProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { Def("test.diag.bad id") };
        }

        [OpWeaveTypeProvider]
        internal sealed class DiagThrowGetTypesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                throw new InvalidOperationException("gettypes failure " + SecretMarker);
        }

        [OpWeaveTypeProvider]
        internal sealed class DiagNullGetTypesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => null!;
        }

        [OpWeaveTypeProvider]
        internal sealed class DiagNullDefProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new TypeDefinition[] { null! };
        }

        // OW-OP-019: the provider class cannot be instantiated.
        [OpWeaveTypeProvider]
        internal sealed class DiagThrowCtorProvider : ITypeProvider
        {
            public DiagThrowCtorProvider() =>
                throw new InvalidOperationException("ctor failure " + SecretMarker);

            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { Def("test.diag.throwctor") };
        }

        // OW-OP-017: provenance refuses this class through the injected seam.
        [OpWeaveTypeProvider]
        internal sealed class DiagUnresolvableProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { Def("test.diag.unresolvable") };
        }

        // Healthy fixtures that must register regardless.
        [OpWeaveTypeProvider]
        internal sealed class DiagGoodProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { Def("test.diag.good") };
        }

        // SPEC §C1 item 4: the fixture compiles into the test assembly and
        // registers on every live build, so it carries a complete-WF Example.
        private static OperationDescriptor Describe(string id) =>
            Op.Define(id, 1)
                .Summary("A diagnostics fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(id, 1, outputPort: "done"),
                    },
                };

        internal abstract class DiagFixtureOperation : IOperation
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

        [OpWeaveOperation("test.diag.good.op", 1)]
        internal sealed class DiagGoodOperation : DiagFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Describe("test.diag.good.op");
        }

        // A dependent Operation: its output port references the
        // base-broken type, so it fails as unresolvable (OW-OP-014, SPEC
        // §C3 step 6).
        [OpWeaveOperation("test.diag.dependent.op", 1)]
        internal sealed class DiagDependentOperation : DiagFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Op.Define("test.diag.dependent.op", 1)
                    .Summary("Depends on the base-broken type.")
                    .Output("value", "test.diag.child")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
        }

        private static readonly Type[] s_providerTypes =
        {
            typeof(DiagCollideAProvider), typeof(DiagCollideBProvider),
            typeof(DiagBaselessProvider), typeof(DiagCycleProvider),
            typeof(DiagNoCodecProvider), typeof(DiagBadNormalizeProvider),
            typeof(DiagThrowCtorProvider), typeof(DiagUnresolvableProvider),
            typeof(DiagMalformedIdProvider), typeof(DiagThrowGetTypesProvider),
            typeof(DiagNullGetTypesProvider), typeof(DiagNullDefProvider),
            typeof(DiagGoodProvider),
        };

        private static readonly Type[] s_operationTypes =
        {
            typeof(DiagGoodOperation), typeof(DiagDependentOperation),
        };

        private static RegistryTypeStage.Seams DiagSeams() =>
            new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => s_operationTypes,
                ProviderTypes: () => s_providerTypes,
                ResolveProvenance: type =>
                {
                    if (type == typeof(DiagUnresolvableProvider))
                    {
                        return Result<EditorAssemblyProvenance>.Failure(
                            ErrorCodes.Create(
                                ErrorCodes.OW_OP_017,
                                "The defining assembly could not be resolved as Editor-only."));
                    }
                    return EditorAssemblyResolver.ResolveForType(type);
                },
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());

        private static RegistryBuildResult Rebuild(
            RegistryTypeStage.Seams seams, RegistryPublication publication) =>
            RegistryBuildHost.Rebuild(
                seams, RegistryOperationStage.Production(), publication);

        // ── The seven exact code mappings, one fixture per code ──────────

        [Test]
        public void CandidateFailures_MapEachRejectionToItsPublishedCode()
        {
            var result = Rebuild(DiagSeams(), new RegistryPublication());

            Assert.That(result.Stale, Is.False);
            Assert.That(result.Snapshot, Is.Not.Null);

            // OW-OP-013: both colliding Type ID offers invalidated.
            var shared = result.Snapshot!.CandidateFailures
                .Where(f => f.CandidateId == "test.diag.shared")
                .ToList();
            Assert.That(shared, Has.Count.EqualTo(2));
            Assert.That(shared.All(f =>
                f.Kind == RegistryCandidateKind.Type &&
                f.Diagnostics.All(d => d.Code == ErrorCodes.OW_OP_013)), Is.True);

            // OW-OP-014: the unregistered base reference, and the dependent
            // Operation's unresolvable port type (SPEC §C3 step 6).
            var missing = CandidatesFor(result, "test.diag.child");
            Assert.That(missing, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(missing.Any(f =>
                f.Kind == RegistryCandidateKind.Type &&
                f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_014)), Is.True);
            var dependent = result.Snapshot.Failures
                .Where(f => f.OperationId == "test.diag.dependent.op").ToList();
            Assert.That(dependent, Has.Count.EqualTo(1));
            Assert.That(dependent[0].Diagnostics
                .Any(d => d.Code == ErrorCodes.OW_OP_014), Is.True);

            // OW-OP-015: both sides of the base-type cycle.
            var cycle = result.Snapshot.CandidateFailures
                .Where(f => f.CandidateId.StartsWith("test.diag.cycle."))
                .ToList();
            Assert.That(cycle, Has.Count.EqualTo(2));
            Assert.That(cycle.All(f =>
                f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_015)), Is.True);

            // OW-OP-016: missing Codec and broken Normalize contract.
            var noCodec = CandidatesFor(result, "test.diag.nocodec");
            Assert.That(noCodec, Has.Count.EqualTo(1));
            Assert.That(noCodec[0].Diagnostics
                .Any(d => d.Code == ErrorCodes.OW_OP_016), Is.True);
            var badNorm = CandidatesFor(result, "test.diag.badnorm");
            Assert.That(badNorm, Has.Count.EqualTo(1));
            Assert.That(badNorm[0].Diagnostics
                .Any(d => d.Code == ErrorCodes.OW_OP_016), Is.True);

            // OW-OP-019: the provider class cannot be instantiated.
            var badCtor = CandidatesFor(result, nameof(DiagThrowCtorProvider));
            Assert.That(badCtor, Has.Count.EqualTo(1));
            Assert.That(badCtor[0].Diagnostics
                .Any(d => d.Code == ErrorCodes.OW_OP_019), Is.True);

            // OW-OP-017: the defining assembly could not be resolved.
            var unresolvable = CandidatesFor(result, nameof(DiagUnresolvableProvider));
            Assert.That(unresolvable, Has.Count.EqualTo(1));
            Assert.That(unresolvable[0].Diagnostics
                .Any(d => d.Code == ErrorCodes.OW_OP_017), Is.True);
        }

        // Required [Edit] test: a broken Normalize contract is rejected at
        // registration with OW-OP-016; no healthy provider/Operation is
        // disabled by the unrelated fault.
        [Test]
        public void BrokenNormalizeContract_RejectedWithOp016_HealthyUnaffected()
        {
            var result = Rebuild(DiagSeams(), new RegistryPublication());
            var snapshot = result.Snapshot!;

            var badNorm = CandidatesFor(result, "test.diag.badnorm");
            Assert.That(badNorm, Has.Count.EqualTo(1));
            Assert.That(badNorm[0].Kind, Is.EqualTo(RegistryCandidateKind.Type));
            Assert.That(badNorm[0].Diagnostics
                .Select(d => d.Code), Has.All.EqualTo(ErrorCodes.OW_OP_016));

            // The rejected type is absent from the type snapshot, and the
            // healthy provider/Operation register unaffected.
            Assert.That(
                snapshot.Types.Resolve(TypeRef.Parse("test.diag.badnorm").Value!).Ok,
                Is.False);
            Assert.That(
                snapshot.Types.Resolve(TypeRef.Parse("test.diag.good").Value!).Ok,
                Is.True, "the healthy provider's type must register");
            Assert.That(snapshot.Find("test.diag.good.op", 1), Is.Not.Null,
                "the healthy Operation must register");
        }

        // A malformed Type ID offered with a perfectly valid Codec is not
        // a Codec defect: SPEC §J.1 assigns it no code (OW-OP-016 covers
        // Codec欠落・不正契約 only), so the rejection stays an internal
        // diagnostic (empty code).
        [Test]
        public void MalformedTypeId_WithValidCodec_IsInternalDiagnostic()
        {
            var result = Rebuild(DiagSeams(), new RegistryPublication());

            var malformed = CandidatesFor(result, nameof(DiagMalformedIdProvider));
            Assert.That(malformed, Has.Count.EqualTo(1));
            Assert.That(malformed[0].Kind, Is.EqualTo(RegistryCandidateKind.Type));
            // SPEC §C3 error paragraph: a malformed Type ID is never
            // published verbatim — the candidate carries its redacted
            // form, not the raw provider text.
            Assert.That(malformed[0].CandidateId,
                Is.EqualTo(TypeRegistryBuilder.RedactedTypeId("test.diag.bad id")));
            Assert.That(malformed[0].Diagnostics.Select(d => d.Code),
                Is.All.EqualTo(string.Empty));
            Assert.That(malformed[0].Diagnostics
                .Any(d => d.Code == ErrorCodes.OW_OP_016), Is.False);
        }

        // Every GetTypes() failure form of an already constructed provider
        // — throwing, returning null, yielding a null definition — is a
        // provider-scope internal rejection: OW-OP-019 is the construction
        // failure only, so no published code is emitted (SPEC §J.1).
        [Test]
        public void GetTypesFailures_AreInternalDiagnostics()
        {
            var result = Rebuild(DiagSeams(), new RegistryPublication());

            foreach (var providerName in new[]
            {
                nameof(DiagThrowGetTypesProvider),
                nameof(DiagNullGetTypesProvider),
                nameof(DiagNullDefProvider),
            })
            {
                var failures = CandidatesFor(result, providerName);
                Assert.That(failures, Has.Count.EqualTo(1), providerName);
                Assert.That(failures[0].Kind,
                    Is.EqualTo(RegistryCandidateKind.TypeProvider), providerName);
                Assert.That(failures[0].Diagnostics.Select(d => d.Code),
                    Is.All.EqualTo(string.Empty), providerName);
            }
        }

        // SPEC §C3 error paragraph: each published candidate failure names
        // its 出自アセンブリ. The real dupty1/dupty2 fixture packages offer
        // the same Type ID from two different assemblies; both rejections
        // carry their own offering provider's defining assembly.
        [Test]
        public void DuplicateTypeOffers_CarryEachOfferingAssembly()
        {
            var result = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());

            var shared = result.Snapshot!.CandidateFailures
                .Where(f => f.CandidateId == "com.opweave.fixture.duptyshared.type")
                .ToList();
            Assert.That(shared, Has.Count.EqualTo(2));
            Assert.That(shared.All(f => f.Kind == RegistryCandidateKind.Type &&
                f.Diagnostics.All(d => d.Code == ErrorCodes.OW_OP_013)), Is.True);
            Assert.That(shared.Any(f =>
                f.ProviderId == "com.opweave.fixture.dupty1.types" &&
                f.AssemblyName == "OpWeave.Fixture.DupTy1.Editor"), Is.True);
            Assert.That(shared.Any(f =>
                f.ProviderId == "com.opweave.fixture.dupty2.types" &&
                f.AssemblyName == "OpWeave.Fixture.DupTy2.Editor"), Is.True);
        }

        // The real fixture packages: com.opweave.fixture.mismatch's declared
        // operations[] contradicts its assemblies — every rejected candidate
        // carries OW-OP-018 while the valid extension registers.
        [Test]
        public void MetadataMismatch_OwOp018_AndValidExtensionUnaffected()
        {
            var publication = new RegistryPublication();
            var result = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                publication);

            var snapshot = result.Snapshot!;
            var mismatched = snapshot.CandidateFailures
                .Where(f => f.CandidateId.Contains("com.opweave.fixture.mismatch"))
                .ToList();
            Assert.That(mismatched, Is.Not.Empty,
                "the mismatch package's candidates must reach the candidate failures");
            Assert.That(mismatched.All(f =>
                f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_018)), Is.True);
            Assert.That(snapshot.Find("com.opweave.fixture.valid.op", 1), Is.Not.Null,
                "the unrelated valid extension registers unaffected");
        }

        // SPEC §C3 error paragraph: diagnostics never leak credentials or
        // exception text. The throwing fixtures embed a secret marker in
        // their exception messages; it must not appear anywhere.
        [Test]
        public void Diagnostics_ContainNoSecrets()
        {
            var result = Rebuild(DiagSeams(), new RegistryPublication());

            foreach (var d in AllDiagnostics(result))
            {
                Assert.That(d.Message, Does.Not.Contain(SecretMarker));
                Assert.That(d.Hint ?? "", Does.Not.Contain(SecretMarker));
            }
            // The thrown exception did produce a rejection — the check is
            // not vacuous.
            Assert.That(AllDiagnostics(result).Any(d =>
                d.Code == ErrorCodes.OW_OP_019 || d.Code == ErrorCodes.OW_OP_016), Is.True);
        }

        // SPEC §C3 rule 9: a failed compile leaves the stale snapshot.
        [Test]
        public void CompileFailure_KeepsStaleSnapshot()
        {
            var publication = new RegistryPublication();
            Rebuild(DiagSeams(), publication);
            var first = publication.Snapshot;
            Assert.That(first, Is.Not.Null);
            Assert.That(publication.Stale, Is.False);

            var result = Rebuild(
                DiagSeams() with { ScriptCompilationFailed = () => true }, publication);
            Assert.That(result.Stale, Is.True);
            Assert.That(result.Snapshot, Is.Null);
            Assert.That(publication.Snapshot, Is.SameAs(first),
                "the previously published snapshot stays, marked stale");
            Assert.That(publication.Stale, Is.True);
        }

        // ── [UnityTest]: a provider discovered only after a real domain
        //    reload is diagnosed per candidate. ──

        // Domain sentinel: a static field is reset by a domain reload, so
        // a non-null value proves we still run in the old domain (SPEC
        // §T.3 — the same batchmode pattern WeakFingerprintTests and
        // CoordinatorRunLockReloadTests use).
        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

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
        // to this crossing — never residue of an earlier run.
        private const string ProbeTokenKey =
            "OpWeave.W2408.RegistrationDiagnostics.ProbeToken";

        private static string ProbeStateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveRegDiagReload");

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
                        "RegistrationDiagnosticsTests." + phase +
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
                "RegistrationDiagnosticsTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "RegistrationDiagnosticsTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "RegistrationDiagnosticsTests." + phase +
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
                            "RegistrationDiagnosticsTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "RegistrationDiagnosticsTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // Removes every leftover Assets/OpWeaveRegDiagReload_* folder —
        // the generated probe is never committed and a failed or
        // interrupted earlier run must not poison later builds — plus
        // the Library probe-token dir (scratch, never an asset write).
        // Every queued reload is crossed inside DrainEditorState below,
        // never yielded from setup/teardown itself.
        private static bool CleanupProbeFolders()
        {
            var dirty = false;
            foreach (var dir in new DirectoryInfo(Application.dataPath)
                .GetDirectories(ReloadProbePrefix + "*"))
            {
                dir.Delete(recursive: true);
                var meta = dir.FullName + ".meta";
                if (File.Exists(meta))
                {
                    File.Delete(meta);
                }
                dirty = true;
            }
            if (Directory.Exists(ProbeStateDir))
            {
                Directory.Delete(ProbeStateDir, recursive: true);
            }
            return dirty;
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup cleanup enter");
            var dirty = CleanupProbeFolders();
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
            var dirty = CleanupProbeFolders();
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

        [UnityTest]
        public IEnumerator DomainReload_DiscoveredProviderRejectedWithOp016()
        {
            var folderName =
                ReloadProbePrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            var folder = Path.Combine(Application.dataPath, folderName);
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, folderName + ".asmdef"),
                "{\n" +
                "    \"name\": \"" + folderName + "\",\n" +
                "    \"references\": [\"OpWeave.Core\"],\n" +
                "    \"includePlatforms\": [\"Editor\"]\n" +
                "}\n");
            // A real discovered provider offering a codec-less type.
            File.WriteAllText(
                Path.Combine(folder, "ReloadProvider.cs"),
                "using System.Collections.Generic;\n" +
                "using OpWeave.Core;\n" +
                "[OpWeaveTypeProvider]\n" +
                "public sealed class OpWeaveRegDiagReloadProvider : ITypeProvider\n" +
                "{\n" +
                "    public IEnumerable<TypeDefinition> GetTypes() => new[]\n" +
                "    {\n" +
                "        new TypeDefinition(\"test.diagreload.type\", TypeKind.Primitive, 0,\n" +
                "            System.Array.Empty<string>(), null, \"reload probe\", null!),\n" +
                "    };\n" +
                "}\n");
            AssetDatabase.Refresh();
            ArmReloadProbe("provider-write");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");
            AssertReloadProbeThisSession("provider-write");

            var publication = new RegistryPublication();
            var result = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                publication);

            Assert.That(result.Stale, Is.False);
            var snapshot = result.Snapshot!;
            var rejected = snapshot.CandidateFailures
                .Where(f => f.CandidateId == "test.diagreload.type").ToList();
            Assert.That(rejected, Has.Count.EqualTo(1),
                "the reload-discovered codec-less type must be rejected");
            Assert.That(rejected[0].Diagnostics
                .Any(d => d.Code == ErrorCodes.OW_OP_016), Is.True);
            Assert.That(snapshot.Find("core.start", 1), Is.Not.Null,
                "standard Operations stay registered after the reload build");
        }
    }
}
