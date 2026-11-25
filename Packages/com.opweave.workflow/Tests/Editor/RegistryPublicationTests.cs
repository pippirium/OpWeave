#nullable enable

// T-W2.2-07 [Edit] tests: local:T-W2.2-07:atomic-registry-build — SPEC §C3
// stages 6–8 (instantiate allowed Operations, Waitable-Poll flag, Descriptor
// validation, fingerprints, atomic publication) and the §C3 rule 9 stale
// refusal behind the SG-18 acceptance gate.
//
// SPEC §C3 test 1: a fixture Operation discovered through TypeCache registers
// automatically and carries the provenance origin (custom when the provenance
// is a non-standard assembly). SPEC §C3 test 5: a real compile failure keeps
// the previous published snapshot, marks it stale and refuses new Runs with
// OW-RUN-009 — a broken Operation is excluded alone. The method-body-edit
// check of T-W2.2-02 is exercised here at the Registry-rebuild level: only
// the edited assembly's implementationHash moves.
//
// The domain-reload [UnityTest]s write probe assemblies under
// Assets/OpWeaveRegistryProbe_*/ and Assets/OpWeaveTempCompileError_*;
// state that must cross a reload lives in Library/OpWeaveRegistryProbe/.
// Everything is removed by [UnityTearDown]; nothing tracked is modified.
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
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RegistryPublicationTests
    {
        private const string WorkflowId = "example.registrypublication";
        private const string ProbeAssetDir = "Assets/OpWeaveRegistryProbe";
        private const string Probe1Assembly = "OpWeave.RegistryProbe1";
        private const string Probe2Assembly = "OpWeave.RegistryProbe2";
        private const string TempCompileErrorPrefix = "OpWeaveTempCompileError_";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string StateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveRegistryProbe");

        private static string BeforeSnapshotFile => Path.Combine(StateDir, "before.txt");

        private static string BeforeIdentityFile =>
            Path.Combine(StateDir, "before-identity.txt");

        // ── Fixture operations ────────────────────────────────────────────

        // SPEC §C1 item 4: these fixture types compile into the test
        // assembly, so the production stages register them on every live
        // build and each must publish a complete-WF Example like a shipped
        // Operation — no test-support exemption.
        private static OperationDescriptor Describe(string id, int version = 1) =>
            Op.Define(id, version)
                .Summary("A registry fixture operation.")
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

        internal abstract class FixtureOperation : IOperation
        {
            public abstract OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public virtual StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public virtual StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public virtual ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public virtual void Cancel(StepContext ctx)
            {
            }
        }

        [OpWeaveOperation("test.registry.good", 1)]
        internal sealed class GoodOperation : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Describe("test.registry.good");
        }

        [OpWeaveOperation("test.registry.second", 1)]
        internal sealed class SecondOperation : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Describe("test.registry.second");
        }

        [OpWeaveOperation("test.registry.origin", 1)]
        internal sealed class OriginProbeOperation : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Describe("test.registry.origin");
        }

        [OpWeaveOperation("test.registry.throwctor", 1)]
        internal sealed class ThrowingCtorOperation : FixtureOperation
        {
            public ThrowingCtorOperation() => throw new InvalidOperationException("ctor probe");

            public override OperationDescriptor Descriptor { get; } = Describe("test.registry.throwctor");
        }

        [OpWeaveOperation("test.registry.privatector", 1)]
        internal sealed class PrivateCtorOperation : FixtureOperation
        {
            private PrivateCtorOperation()
            {
            }

            public override OperationDescriptor Descriptor { get; } = Describe("test.registry.privatector");
        }

        // Waitable whose Poll is the not-implemented placeholder — OW-OP-003.
        [OpWeaveOperation("test.registry.brokenwait", 1)]
        internal sealed class BrokenWaitableOperation : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Describe("test.registry.brokenwait") with { ExecutionMode = ExecutionMode.Waitable };
        }

        // Waitable whose Poll throws NotImplementedException — also OW-OP-003.
        [OpWeaveOperation("test.registry.notimplpoll", 1)]
        internal sealed class NotImplementedPollOperation : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Describe("test.registry.notimplpoll") with { ExecutionMode = ExecutionMode.Waitable };

            public override StepResult Poll(StepContext ctx) =>
                throw new NotImplementedException();
        }

        [OpWeaveOperation("test.registry.waitable", 1)]
        internal sealed class RealWaitableOperation : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Describe("test.registry.waitable") with { ExecutionMode = ExecutionMode.Waitable };

            public override StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("\"held\""), "held", 1);

            public override StepResult Poll(StepContext ctx)
            {
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        // An explicit interface Poll implementation counts as a real one.
        [OpWeaveOperation("test.registry.explicitpoll", 1)]
        internal sealed class ExplicitPollOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } =
                Describe("test.registry.explicitpoll") with { ExecutionMode = ExecutionMode.Waitable };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("\"held\""), "held", 1);

            StepResult IOperation.Poll(StepContext ctx)
            {
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // A reserved-namespace id — OW-OP-007 when its origin is Custom.
        [OpWeaveOperation("unity.fake.reservedop", 1)]
        internal sealed class ReservedIdOperation : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Describe("unity.fake.reservedop");
        }

        // An unresolvable port type — OW-OP-003 at Descriptor validation.
        [OpWeaveOperation("test.registry.unknowntype", 1)]
        internal sealed class UnknownTypeOperation : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Op.Define("test.registry.unknowntype", 1)
                    .Summary("References a type nobody offers.")
                    .Input("value", "acme.Missing", required: true)
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
        }

        [OpWeaveOperation("test.registry.collide", 1)]
        internal sealed class CollideOperationA : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Describe("test.registry.collide");
        }

        [OpWeaveOperation("test.registry.collide", 1)]
        internal sealed class CollideOperationB : FixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Describe("test.registry.collide");
        }

        // ── Seams and rebuild helpers ─────────────────────────────────────

        private static readonly Type[] s_fixtureOperationTypes =
        {
            typeof(GoodOperation),
            typeof(SecondOperation),
            typeof(OriginProbeOperation),
            typeof(ThrowingCtorOperation),
            typeof(PrivateCtorOperation),
            typeof(BrokenWaitableOperation),
            typeof(NotImplementedPollOperation),
            typeof(RealWaitableOperation),
            typeof(ExplicitPollOperation),
            typeof(ReservedIdOperation),
            typeof(UnknownTypeOperation),
            typeof(CollideOperationA),
            typeof(CollideOperationB),
        };

        private static EditorAssemblyProvenance AsCustom(EditorAssemblyProvenance real) =>
            new EditorAssemblyProvenance(
                real.AssemblyName, real.CompiledAssemblyPath, real.IsPrecompiled,
                OperationOrigin.Custom, package: null, real.CompilationAssembly);

        // Re-attributes the listed types as non-standard (project assembly)
        // provenance — the same seam RegistryTypeStageTests uses, since this
        // test assembly itself lives inside the standard package.
        private static Func<Type, Result<EditorAssemblyProvenance>> CustomProvenance(
            params Type[] customTypes) =>
            type =>
            {
                var resolved = EditorAssemblyResolver.ResolveForType(type);
                if (resolved.Ok && customTypes.Contains(type))
                {
                    resolved = Result<EditorAssemblyProvenance>.Success(AsCustom(resolved.Value!));
                }
                return resolved;
            };

        private static RegistryTypeStage.Seams SeamsFor(
            IEnumerable<Type> operationTypes,
            Func<Type, Result<EditorAssemblyProvenance>>? resolveProvenance = null,
            Func<bool>? scriptCompilationFailed = null) =>
            new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: scriptCompilationFailed ?? (() => false),
                OperationTypes: () => operationTypes,
                ProviderTypes: () => TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: resolveProvenance ??
                    (type => EditorAssemblyResolver.ResolveForType(type)),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());

        private static RegistryBuildResult Rebuild(
            RegistryTypeStage.Seams typeSeams,
            RegistryPublication publication,
            RegistryOperationStage.Seams? operationSeams = null) =>
            RegistryBuildHost.Rebuild(
                typeSeams, operationSeams ?? RegistryOperationStage.Production(), publication);

        private static List<string> FailureCodes(RegistryBuildResult result)
        {
            var codes = new List<string>();
            var snapshot = result.Snapshot;
            if (snapshot is not null)
            {
                foreach (var failure in snapshot.Failures)
                {
                    codes.AddRange(failure.Diagnostics.Select(d => d.Code));
                }
            }
            foreach (var failure in result.OperationFailures)
            {
                codes.AddRange(failure.Diagnostics.Select(d => d.Code));
            }
            return codes;
        }

        private static List<string> FailureCodes(RegistrySnapshot snapshot) =>
            snapshot.Failures.SelectMany(f => f.Diagnostics.Select(d => d.Code)).ToList();

        private static void AssertHex64(string hash, string what) =>
            Assert.That(hash, Does.Match("^[0-9a-f]{64}$"), what);

        // ── SPEC §C3 test 1: auto-registration, origin, fingerprints ──────

        [Test]
        public void StageSixAndSeven_RegisterFixturesWithFingerprints()
        {
            var publication = new RegistryPublication();
            var result = Rebuild(SeamsFor(new[]
            {
                typeof(GoodOperation),
                typeof(SecondOperation),
                typeof(OriginProbeOperation),
                typeof(RealWaitableOperation),
                typeof(ExplicitPollOperation),
            }), publication);

            Assert.That(result.Stale, Is.False);
            var snapshot = result.Snapshot;
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(result.OperationFailures, Is.Empty);
            Assert.That(snapshot!.Failures, Is.Empty);

            // The published snapshot is the same immutable object, complete.
            Assert.That(publication.Snapshot, Is.SameAs(snapshot));
            Assert.That(publication.Stale, Is.False);
            Assert.That(snapshot.GenerationId, Does.StartWith("reg_"));
            AssertHex64(snapshot.RegistryHash, "registryHash");

            var good = snapshot.Find("test.registry.good", 1);
            Assert.That(good, Is.Not.Null);
            // This test assembly lives in the standard package — Standard.
            Assert.That(good!.Origin, Is.EqualTo(OperationOrigin.Standard));
            Assert.That(good.AssemblyName, Is.EqualTo("OpWeave.UnityAdapter.Tests"));
            Assert.That(good.DescriptorHash,
                Is.EqualTo(DescriptorHash.Compute(good.Descriptor)));
            // implementationHash = SHA-256 of the compiled defining assembly.
            var expected = WeakFingerprintProvider.ForDefiningType(typeof(GoodOperation));
            Assert.That(expected.Ok, Is.True);
            Assert.That(good.ImplementationHash, Is.EqualTo(expected.Value!.DefiningAssemblySha256));
            // The instantiated implementation travels on the registration.
            Assert.That(good.Implementation, Is.TypeOf<GoodOperation>());

            foreach (var id in new[]
            {
                "test.registry.second", "test.registry.waitable", "test.registry.explicitpoll",
            })
            {
                Assert.That(snapshot.Find(id, 1), Is.Not.Null, $"{id} must register");
            }
        }

        // SPEC §C3 test 1: the same class discovered with non-standard
        // provenance registers with origin custom.
        [Test]
        public void StageSix_CustomProvenanceRegistersAsCustom()
        {
            var publication = new RegistryPublication();
            var result = Rebuild(SeamsFor(
                new[] { typeof(OriginProbeOperation) },
                CustomProvenance(typeof(OriginProbeOperation))), publication);

            var registered = result.Snapshot!.Find("test.registry.origin", 1);
            Assert.That(registered, Is.Not.Null);
            Assert.That(registered!.Origin, Is.EqualTo(OperationOrigin.Custom));
        }

        // ── SPEC §C3 step 6: per-candidate failures never affect others ───

        [Test]
        public void StageSix_BrokenCandidatesFailAlone()
        {
            var publication = new RegistryPublication();
            var result = Rebuild(SeamsFor(
                s_fixtureOperationTypes,
                CustomProvenance(typeof(ReservedIdOperation))), publication);

            var snapshot = result.Snapshot!;
            var registered = snapshot.Operations.Select(o => o.Descriptor.Id).ToList();
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "test.registry.good", "test.registry.second", "test.registry.origin",
                    "test.registry.waitable", "test.registry.explicitpoll",
                },
                registered);

            var codes = FailureCodes(result);
            CollectionAssert.Contains(codes, ErrorCodes.OW_OP_008); // ctor + private ctor
            CollectionAssert.Contains(codes, ErrorCodes.OW_OP_003); // waitable stubs
            CollectionAssert.Contains(codes, ErrorCodes.OW_OP_014); // unknown type
            CollectionAssert.Contains(codes, ErrorCodes.OW_OP_007); // reserved id from custom
            CollectionAssert.Contains(codes, ErrorCodes.OW_OP_004); // the collide pair

            // The two construction failures carry OW-OP-008 each.
            var owOp008 = result.OperationFailures
                .SelectMany(f => f.Diagnostics.Select(d => (f.TypeFullName, d.Code)))
                .Where(x => x.Code == ErrorCodes.OW_OP_008)
                .Select(x => x.TypeFullName)
                .ToList();
            CollectionAssert.AreEquivalent(
                new[]
                {
                    typeof(ThrowingCtorOperation).FullName,
                    typeof(PrivateCtorOperation).FullName,
                },
                owOp008);

            // The collide pair is refused for BOTH candidates (OW-OP-004).
            var collides = snapshot.Failures
                .Where(f => f.OperationId == "test.registry.collide")
                .ToList();
            Assert.That(collides, Has.Count.EqualTo(2));
            Assert.That(collides.All(f => f.Kind == RegistryChangeKinds.Failed), Is.True);
            Assert.That(
                collides.All(f => f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_004)), Is.True);
        }

        [Test]
        public void StageSix_WaitablePollFlagIsComputedFromTheBody()
        {
            // Unit-level flag checks on the same fixture types.
            Assert.That(PollImplementation.HasRealImplementation(typeof(BrokenWaitableOperation)),
                Is.False, "the NotSupportedException placeholder is not a Poll implementation");
            Assert.That(PollImplementation.HasRealImplementation(typeof(NotImplementedPollOperation)),
                Is.False, "the NotImplementedException placeholder is not a Poll implementation");
            Assert.That(PollImplementation.HasRealImplementation(typeof(RealWaitableOperation)),
                Is.True);
            Assert.That(PollImplementation.HasRealImplementation(typeof(ExplicitPollOperation)),
                Is.True, "an explicit interface Poll implementation is a real implementation");
            // An Immediate with the placeholder Poll is also flagged — the
            // flag is only consulted for Waitable descriptors.
            Assert.That(PollImplementation.HasRealImplementation(typeof(GoodOperation)), Is.False);

            var publication = new RegistryPublication();
            var result = Rebuild(SeamsFor(new[]
            {
                typeof(BrokenWaitableOperation),
                typeof(NotImplementedPollOperation),
                typeof(RealWaitableOperation),
                typeof(ExplicitPollOperation),
            }), publication);

            var snapshot = result.Snapshot!;
            Assert.That(snapshot.Find("test.registry.waitable", 1), Is.Not.Null);
            Assert.That(snapshot.Find("test.registry.explicitpoll", 1), Is.Not.Null);
            Assert.That(snapshot.Find("test.registry.brokenwait", 1), Is.Null);
            Assert.That(snapshot.Find("test.registry.notimplpoll", 1), Is.Null);
            var waitableFailures = snapshot.Failures
                .Where(f => f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_003))
                .Select(f => f.OperationId)
                .ToList();
            CollectionAssert.AreEquivalent(
                new[] { "test.registry.brokenwait", "test.registry.notimplpoll" },
                waitableFailures);
        }

        // ── SPEC §C3 step 8: atomic publication, generation IDs ───────────

        [Test]
        public void Publish_OnlyCompleteSnapshotsObservedDuringBuild()
        {
            var publication = new RegistryPublication();
            Assert.That(publication.Snapshot, Is.Null);
            Assert.That(publication.Stale, Is.True, "a fresh holder stays stale until the first build");

            // Sample the publication from inside the stage-6 fingerprint
            // seam: readers must see the pre-build state — never a partially
            // populated snapshot.
            var sampled = new List<(RegistrySnapshot? Snapshot, bool Stale)>();
            var opSeams = RegistryOperationStage.Production() with
            {
                ImplementationHash = provenance =>
                {
                    sampled.Add((publication.Snapshot, publication.Stale));
                    return WeakFingerprintProvider.ForProvenance(provenance);
                },
            };
            var result = Rebuild(
                SeamsFor(new[] { typeof(GoodOperation), typeof(SecondOperation) }),
                publication,
                opSeams);

            Assert.That(sampled, Is.Not.Empty, "the fingerprint seam must run per candidate");
            foreach (var sample in sampled)
            {
                Assert.That(sample.Snapshot, Is.Null, "an intermediate state leaked during the build");
                Assert.That(sample.Stale, Is.True);
            }
            Assert.That(publication.Snapshot, Is.SameAs(result.Snapshot));
            Assert.That(publication.Stale, Is.False);
        }

        [Test]
        public void Publish_AtomicSwapKeepsOldSnapshotVisibleUntilReplace()
        {
            var publication = new RegistryPublication();
            var operations = new[] { typeof(GoodOperation), typeof(SecondOperation) };
            var first = Rebuild(SeamsFor(operations), publication);
            var oldSnapshot = publication.Snapshot;

            // During the second build the previous complete snapshot stays
            // visible; afterwards it is replaced in one swap.
            var sampled = new List<RegistrySnapshot?>();
            var opSeams = RegistryOperationStage.Production() with
            {
                ImplementationHash = provenance =>
                {
                    sampled.Add(publication.Snapshot);
                    return WeakFingerprintProvider.ForProvenance(provenance);
                },
            };
            var second = Rebuild(SeamsFor(operations), publication, opSeams);

            Assert.That(sampled, Is.Not.Empty);
            Assert.That(sampled.All(s => ReferenceEquals(s, oldSnapshot)), Is.True,
                "readers observed something other than the previous complete snapshot");
            Assert.That(publication.Snapshot, Is.SameAs(second.Snapshot));
            Assert.AreNotEqual(first.Snapshot!.GenerationId, second.Snapshot!.GenerationId,
                "every build mints a fresh generation ID (SPEC §C3 rule 6)");
            Assert.AreEqual(first.Snapshot.RegistryHash, second.Snapshot.RegistryHash,
                "identical registered content keeps the registryHash");
            Assert.That(second.Snapshot.Find("test.registry.second", 1), Is.Not.Null);

            // A changed candidate set moves the registryHash (SPEC §C3 rule 6:
            // every registered Operation enters the canonicalized input).
            var grown = Rebuild(
                SeamsFor(new[]
                {
                    typeof(GoodOperation), typeof(SecondOperation), typeof(OriginProbeOperation),
                }),
                publication);
            Assert.AreNotEqual(second.Snapshot.RegistryHash, grown.Snapshot!.RegistryHash,
                "adding an Operation changes the registryHash");
            Assert.That(grown.Snapshot.Find("test.registry.origin", 1), Is.Not.Null);
        }

        // ── SPEC §C3 rule 9: stale keeps the old snapshot ─────────────────

        [Test]
        public void Rebuild_StaleKeepsPreviousSnapshotAndMarksStale()
        {
            var publication = new RegistryPublication();
            var good = Rebuild(SeamsFor(new[] { typeof(GoodOperation) }), publication);
            var oldSnapshot = publication.Snapshot;

            var stale = Rebuild(
                SeamsFor(s_fixtureOperationTypes, scriptCompilationFailed: () => true),
                publication);

            Assert.That(stale.Stale, Is.True);
            Assert.That(stale.Snapshot, Is.Null, "a stale build produces no new snapshot");
            Assert.That(stale.Operations, Is.Empty);
            Assert.That(stale.OperationFailures, Is.Empty);
            Assert.That(publication.Stale, Is.True);
            Assert.That(publication.Snapshot, Is.SameAs(oldSnapshot),
                "the previous snapshot stays published, marked stale (SPEC §C3 rule 9)");
        }

        // A compile failure inside a live domain produces no rebuild — the
        // holder's Stale ORs the stored flag with the injected live signal,
        // so the published snapshot reports stale without a Rebuild call
        // (the deterministic form of the production Shared wiring).
        [Test]
        public void Stale_LiveCompileFailureMarksPublicationWithoutRebuild()
        {
            var compileFailed = false;
            var publication = new RegistryPublication(() => compileFailed);
            var built = Rebuild(SeamsFor(new[] { typeof(GoodOperation) }), publication);
            Assert.That(publication.Stale, Is.False);

            compileFailed = true;
            Assert.That(publication.Stale, Is.True,
                "a live compile failure marks the holder stale without a rebuild");
            Assert.That(publication.Snapshot, Is.SameAs(built.Snapshot),
                "the last published snapshot stays in place while stale");
        }

        // ── Stale refusal through the acceptance gate (SG-18) ─────────────

        [Test]
        public void StaleGate_RefusesNewRunWithOwRun009()
        {
            var gate = new StaleRegistryGate(() => true);
            var request = new RunAcceptanceRequest(
                RunnerHostFixture.Plan(WorkflowId, new[]
                {
                    RunnerHostFixture.Step(0, "a", "test.registry.good", 'a'),
                }),
                RunnerHostFixture.Doc(
                    WorkflowId,
                    new[] { new NodeData { Id = "a", Operation = "test.registry.good", Version = 1 } },
                    Array.Empty<ControlEdge>()),
                ActorKind.Agent,
                RunnerHostFixture.Options());

            var refused = gate.Admit(request);
            Assert.That(refused.Ok, Is.False);
            Assert.That(refused.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(refused.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_RUN_009));
            Assert.That(refused.Diagnostics[0].Severity, Is.EqualTo(Severity.Error));
            Assert.That(refused.Diagnostics[0].Retryable, Is.False);

            var admitted = new StaleRegistryGate(() => false).Admit(request);
            Assert.That(admitted.Ok, Is.True);
        }

        // ── §H4.1 settings error: standard Operations only ────────────────

        [Test]
        public void Rebuild_SettingsErrorExcludesCustomCandidates()
        {
            var publication = new RegistryPublication();
            var result = Rebuild(
                SeamsFor(
                    new[] { typeof(GoodOperation), typeof(OriginProbeOperation) },
                    CustomProvenance(typeof(OriginProbeOperation))) with
                {
                    Exclusion = StandardOnlyExtensionExclusion.Instance,
                },
                publication);

            var snapshot = result.Snapshot!;
            Assert.That(snapshot.Find("test.registry.good", 1), Is.Not.Null,
                "standard Operations still register under a settings error");
            Assert.That(snapshot.Find("test.registry.origin", 1), Is.Null,
                "a custom Operation is excluded under §H4.1 settings error");
            Assert.That(
                result.Exclusions.Any(e =>
                    e.Candidate.Type == typeof(OriginProbeOperation) &&
                    e.Reason == "settings error: only standard Operations may register"),
                Is.True, "the exclusion must be a policy record, not a registration error");
        }

        [Test]
        public void RebuildProduction_FailingSettingsBuildsStandardOnly()
        {
            try
            {
                // The injected loader's OW-CFG-001 puts the production
                // rebuild on the §H4.1 standard-only path. The fixture
                // provider lives in this test assembly — inside the
                // standard package, so its real provenance is Standard —
                // and the provenance seam re-attributes it as the custom
                // candidate it stands in for (the T-W2.2-03 seam).
                RegistryBuildHost.RebuildProduction(
                    () => Result<OpWeaveSettings>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_CFG_001, "Settings file could not be read.")),
                    CustomProvenance(typeof(RegistryTypeStageTests.AlphaProvider)));

                var latest = RegistryBuildHost.Latest;
                Assert.That(latest, Is.Not.Null);
                Assert.That(latest!.SettingsDiagnostics.Select(d => d.Code),
                    Has.Some.EqualTo(ErrorCodes.OW_CFG_001));
                var snapshot = RegistryPublication.Shared.Snapshot;
                Assert.That(snapshot, Is.Not.Null);
                Assert.That(snapshot!.Types.Resolve(TypeRef.Parse("core.String").Value!).Ok, Is.True);
                Assert.That(
                    snapshot.Types.Resolve(TypeRef.Parse("test.fixture.alpha").Value!).Ok,
                    Is.False,
                    "the custom fixture TypeProvider is excluded under §H4.1 settings error");
                Assert.That(snapshot.Operations.All(o => o.Origin == OperationOrigin.Standard),
                    Is.True, "only standard Operations register under a settings error");
            }
            finally
            {
                // Restore the shared publication to a full build for later tests.
                RegistryBuildHost.RebuildProduction();
            }
        }

        // ── The production startup hook publishes the snapshot ────────────

        [Test]
        public void StartupHook_RunsFullBuildAndPublishes()
        {
            var host = OpWeaveRunnerHost.CreateProduction();
            try
            {
                var ran = host.StartupHooks.RunAll();
                Assert.That(ran, Has.Some.EqualTo("registry-build"));

                var snapshot = RegistryPublication.Shared.Snapshot;
                Assert.That(snapshot, Is.Not.Null, "the startup hook must publish a snapshot");
                Assert.That(RegistryPublication.Shared.Stale, Is.False);
                Assert.That(snapshot!.Find("test.registry.good", 1), Is.Not.Null,
                    "the fixture Operation registers automatically (SPEC §C3 test 1)");
                Assert.That(RegistryBuildHost.Latest!.Snapshot, Is.SameAs(snapshot));
            }
            finally
            {
                host.Dispose();
            }
        }

        // ── local:T-W2.2-07:atomic-registry-build — real compile failure ──

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
            var onDisk = Path.Combine(ProjectRoot, "Assets", "OpWeaveRegistryProbe");
            if (Directory.Exists(onDisk))
            {
                Directory.Delete(onDisk, recursive: true);
                dirty = true;
            }
            foreach (var dir in new DirectoryInfo(Application.dataPath)
                .GetDirectories(TempCompileErrorPrefix + "*"))
            {
                dir.Delete(recursive: true);
                var meta = dir.FullName + ".meta";
                if (File.Exists(meta))
                {
                    File.Delete(meta);
                }
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
        // lives under StateDir, which CleanupProbeArtifacts already removes.
        private const string ProbeTokenKey =
            "OpWeave.W2408.RegistryPublication.ProbeToken";

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
                        "RegistryPublicationTests." + phase +
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
                "RegistryPublicationTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "RegistryPublicationTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "RegistryPublicationTests." + phase +
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
                            "RegistryPublicationTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "RegistryPublicationTests." + m_phase +
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
            // Restore the shared publication to a clean published build for
            // any test that runs after the stale-refusal fixture.
            if (!EditorUtility.scriptCompilationFailed && !EditorApplication.isCompiling)
            {
                RegistryBuildHost.RebuildProduction();
            }
        }

        // SPEC §C3 rule 9 end to end: a real failed compile leaves the
        // previously published snapshot in place, marked stale, and the
        // production acceptance gate refuses a new Run with OW-RUN-009.
        [UnityTest]
        public IEnumerator CompileFailure_KeepsOldSnapshotStaleAndRefusesRuns()
        {
            var folderName =
                TempCompileErrorPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            var folder = Path.Combine(Application.dataPath, folderName);

            // Baseline: a real production build is published.
            RegistryBuildHost.RebuildProduction();
            var published = RegistryPublication.Shared.Snapshot;
            Assert.That(published, Is.Not.Null, "the baseline build must publish a snapshot");

            var runsDir = Path.Combine(ProjectRoot, "Library", "OpWeave", "Runs");
            var runsBefore = Directory.Exists(runsDir)
                ? Directory.GetDirectories(runsDir).Length
                : -1;

            var previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(
                    Path.Combine(folder, folderName + ".asmdef"),
                    "{\n" +
                    "    \"name\": \"" + folderName + "\",\n" +
                    "    \"includePlatforms\": [\"Editor\"]\n" +
                    "}\n");
                File.WriteAllText(
                    Path.Combine(folder, "BrokenProbe.cs"),
                    "internal static class BrokenProbe\n" +
                    "{\n" +
                    "    public static int Value = \"not an int\";\n" +
                    "}\n");
                AssetDatabase.Refresh();
                ArmReloadProbe("broken-write");
                yield return new RecompileScripts(
                    expectScriptCompilation: true, expectScriptCompilationSuccess: false);
                AssertReloadProbeThisSession("broken-write");

                Assert.That(EditorUtility.scriptCompilationFailed, Is.True,
                    "the fixture failed to produce the compile error under test");

                // The failed compile produced no domain reload and no
                // rebuild has run yet — the publication must already
                // report stale from the live compile-failure signal, over
                // the previously published snapshot (SPEC §C3 rule 9).
                Assert.That(RegistryPublication.Shared.Stale, Is.True,
                    "the publication reports the live compile failure before any rebuild");
                Assert.That(RegistryPublication.Shared.Snapshot, Is.SameAs(published));

                RegistryBuildHost.RebuildProduction();

                Assert.That(RegistryPublication.Shared.Stale, Is.True);
                Assert.That(RegistryPublication.Shared.Snapshot, Is.SameAs(published),
                    "the old snapshot stays published while the compile is broken");
                Assert.That(RegistryPublication.Shared.Snapshot!.RegistryHash,
                    Is.EqualTo(published!.RegistryHash),
                    "the stale mark keeps the published registryHash unchanged");
                Assert.That(RegistryPublication.Shared.Snapshot.GenerationId,
                    Is.EqualTo(published.GenerationId),
                    "the stale mark keeps the published generation ID unchanged");

                var host = OpWeaveRunnerHost.CreateProduction();
                try
                {
                    var accepted = host.AcceptRun(
                        RunnerHostFixture.Plan(WorkflowId, new[]
                        {
                            RunnerHostFixture.Step(0, "a", "test.registry.good", 'a'),
                        }),
                        RunnerHostFixture.Doc(
                            WorkflowId,
                            new[] { new NodeData { Id = "a", Operation = "test.registry.good", Version = 1 } },
                            Array.Empty<ControlEdge>()),
                        ActorKind.Agent,
                        RunnerHostFixture.Options());

                    Assert.That(accepted.Ok, Is.False,
                        "a stale Registry must refuse the new Run (SPEC §C3 test 5)");
                    Assert.That(accepted.Diagnostics, Is.Not.Empty);
                    Assert.That(accepted.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_RUN_009));
                    Assert.That(accepted.Diagnostics[0].Severity, Is.EqualTo(Severity.Error));
                    Assert.That(accepted.Diagnostics[0].Retryable, Is.False);
                }
                finally
                {
                    host.Dispose();
                }

                if (runsBefore < 0)
                {
                    Assert.That(Directory.Exists(runsDir), Is.False,
                        "the refusal happened before any Run side effect");
                }
                else
                {
                    Assert.That(Directory.GetDirectories(runsDir).Length,
                        Is.EqualTo(runsBefore), "the refusal created no Run directory");
                }

                // Restore a clean compile inside the ignored-failures window;
                // UnityTearDown remains the safety net for a mid-test abort.
                Directory.Delete(folder, recursive: true);
                File.Delete(folder + ".meta");
                AssetDatabase.Refresh();
                ArmReloadProbe("restore-clean");
                yield return new RecompileScripts(
                    expectScriptCompilation: true, expectScriptCompilationSuccess: true);
                AssertReloadProbeThisSession("restore-clean");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
            }

            Assert.That(EditorUtility.scriptCompilationFailed, Is.False);
            RegistryBuildHost.RebuildProduction();
            Assert.That(RegistryPublication.Shared.Stale, Is.False,
                "a clean recompile rebuilds and republishes the Registry");
            Assert.That(RegistryPublication.Shared.Snapshot, Is.Not.Null);
            Assert.That(RegistryPublication.Shared.Snapshot!.Find("test.registry.good", 1),
                Is.Not.Null);
        }

        // ── local:T-W2.2-07:atomic-registry-build — method-body edit ──────
        // The Registry-rebuild form of the T-W2.2-02 check: editing only an
        // Execute body inside one probe assembly moves that assembly's
        // implementationHash; every other Operation keeps identical
        // fingerprints.

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private const string ProbeAsmdefTemplate =
            "{{\n    \"name\": \"{0}\",\n    \"rootNamespace\": \"OpWeave.RegistryProbes\",\n" +
            "    \"references\": [\"OpWeave.Core\"],\n" +
            "    \"includePlatforms\": [\n        \"Editor\"\n    ],\n" +
            "    \"autoReferenced\": false\n}}\n";

        private const string ProbeOperationBody =
            "    [OpWeaveOperation(\"{1}\", 1)]\n" +
            "    public sealed class {0} : IOperation\n" +
            "    {{\n" +
            "        public OperationDescriptor Descriptor {{ get; }} = Op.Define(\"{1}\", 1)\n" +
            "            .Summary(\"Registry probe operation.\")\n" +
            "            .Output(\"done\", \"core.Bool\")\n" +
            "            .Effects(EffectNames.Read)\n" +
            "            .Idempotency(Idempotency.Idempotent)\n" +
            "            .Build();\n" +
            "        public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>\n" +
            "            Array.Empty<Diagnostic>();\n" +
            "        public PreflightResult Preflight(StepContext ctx) =>\n" +
            "            new PreflightResult(\n" +
            "                Array.Empty<Diagnostic>(),\n" +
            "                Array.Empty<PlannedChange>(),\n" +
            "                JsonValue.Parse(\"{{}}\").Value!);\n" +
            "        public StepResult Execute(StepContext ctx)\n" +
            "        {{\n" +
            "            ctx.SetOutput(\"done\", {2});\n" +
            "            return StepResult.Succeeded();\n" +
            "        }}\n" +
            "        public StepResult Poll(StepContext ctx) =>\n" +
            "            throw new NotSupportedException(\"Immediate operations are never polled.\");\n" +
            "        public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>\n" +
            "            new ReconcileResult.Unknown(\"This probe never reconciles.\");\n" +
            "        public void Cancel(StepContext ctx) {{ }}\n" +
            "    }}\n";

        private const string ProbeUsings =
            "#nullable enable\n" +
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using OpWeave.Core;\n" +
            "using OpWeave.Core.Operations;\n\n" +
            "namespace OpWeave.RegistryProbes\n{\n";

        private static void WriteProbeAssets(bool secondVariant)
        {
            var root = Path.Combine(ProjectRoot, "Assets", "OpWeaveRegistryProbe");
            Directory.CreateDirectory(Path.Combine(root, "Probe1"));
            Directory.CreateDirectory(Path.Combine(root, "Probe2"));

            File.WriteAllText(
                Path.Combine(root, "Probe1", Probe1Assembly + ".asmdef"),
                string.Format(ProbeAsmdefTemplate, Probe1Assembly));
            File.WriteAllText(
                Path.Combine(root, "Probe2", Probe2Assembly + ".asmdef"),
                string.Format(ProbeAsmdefTemplate, Probe2Assembly));

            WriteProbe1Ops(Path.Combine(root, "Probe1", "ProbeOps.cs"), secondVariant);
            File.WriteAllText(Path.Combine(root, "Probe2", "ProbeOps.cs"),
                ProbeUsings +
                string.Format(ProbeOperationBody, "ProbeGammaOperation", "test.probe.gamma", "true") +
                "}\n");
        }

        // Only the method bodies differ between the two variants — the
        // compiled bytes of Probe1 move, nothing else does.
        private static void WriteProbe1Ops(string path, bool secondVariant)
        {
            var marker = secondVariant ? "false" : "true";
            File.WriteAllText(path,
                ProbeUsings +
                string.Format(ProbeOperationBody, "ProbeAlphaOperation", "test.probe.alpha", marker) +
                string.Format(ProbeOperationBody, "ProbeBetaOperation", "test.probe.beta", marker) +
                "}\n");
        }

        private static RegistrySnapshot ProductionSnapshot()
        {
            var result = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.That(result.Snapshot, Is.Not.Null,
                "the production build must produce a snapshot: " +
                string.Join("; ", result.OperationFailures.SelectMany(
                    f => f.Diagnostics.Select(d => d.Code + " " + d.Message))));
            return result.Snapshot!;
        }

        // id|version|assembly|descriptorHash|implementationHash per op, in
        // the snapshot's canonical order.
        private static List<string> SnapshotLines(RegistrySnapshot snapshot)
        {
            var lines = new List<string>();
            foreach (var op in snapshot.Operations)
            {
                lines.Add(string.Join("|",
                    op.Descriptor.Id, op.Descriptor.Version, op.AssemblyName,
                    op.DescriptorHash, op.ImplementationHash));
            }
            return lines;
        }

        [UnityTest]
        public IEnumerator MethodBodyEdit_ChangesOnlyEditedAssemblysFingerprints()
        {
            // Domain 1: create the probe assemblies and cross a real
            // compile + domain reload.
            WriteProbeAssets(secondVariant: false);
            ArmReloadProbe("write-probes");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");
            AssertReloadProbeThisSession("write-probes");

            // Domain 2: rebuild the Registry and keep the fingerprints.
            var before = ProductionSnapshot();
            Assert.That(before.Find("test.probe.alpha", 1), Is.Not.Null,
                "the probe Operation did not register (SPEC §C3 test 1)");
            Assert.That(before.Find("test.probe.alpha", 1)!.Origin,
                Is.EqualTo(OperationOrigin.Custom),
                "a project-assembly Operation carries the custom origin");
            Assert.That(before.Find("test.probe.gamma", 1), Is.Not.Null);
            Directory.CreateDirectory(StateDir);
            File.WriteAllLines(BeforeSnapshotFile, SnapshotLines(before));
            File.WriteAllLines(BeforeIdentityFile,
                new[] { before.RegistryHash, before.GenerationId });

            // Edit only the method bodies of Probe1 and cross a second reload.
            WriteProbe1Ops(
                Path.Combine(ProjectRoot, "Assets", "OpWeaveRegistryProbe", "Probe1", "ProbeOps.cs"),
                secondVariant: true);
            ArmReloadProbe("edit-body");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the method-body edit did not produce a domain reload");
            AssertReloadProbeThisSession("edit-body");

            // Domain 3: rebuild and compare per (id, version).
            var after = ProductionSnapshot();
            var beforeLines = File.ReadAllLines(BeforeSnapshotFile);
            var beforeById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in beforeLines)
            {
                beforeById[line.Split('|')[0] + "|" + line.Split('|')[1]] = line;
            }

            var moved = new List<string>();
            var descriptorMoved = new List<string>();
            foreach (var op in after.Operations)
            {
                var key = op.Descriptor.Id + "|" + op.Descriptor.Version;
                Assert.That(beforeById, Does.ContainKey(key),
                    $"a rebuild saw new Operation {key} — the fixture set changed");
                var parts = beforeById[key].Split('|');
                if (op.DescriptorHash != parts[3])
                {
                    descriptorMoved.Add(key);
                }
                if (op.ImplementationHash != parts[4])
                {
                    moved.Add(key);
                }
            }
            Assert.That(after.Operations.Count, Is.EqualTo(beforeLines.Length),
                "the rebuild registered a different Operation set");

            // Only the edited assembly's Operations may move; every other
            // fingerprint — including descriptorHash everywhere — is identical.
            Assert.That(descriptorMoved, Is.Empty,
                "no Descriptor changed, so no descriptorHash may move");
            Assert.That(moved, Is.EquivalentTo(new[] { "test.probe.alpha|1", "test.probe.beta|1" }),
                "implementationHash must change only for the edited assembly's Operations");
            var beforeIdentity = File.ReadAllLines(BeforeIdentityFile);
            Assert.AreNotEqual(beforeIdentity[0], after.RegistryHash);
            Assert.AreNotEqual(beforeIdentity[1], after.GenerationId);
        }
    }
}
