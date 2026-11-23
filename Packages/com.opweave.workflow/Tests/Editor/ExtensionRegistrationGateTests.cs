#nullable enable

// T-W2.12-06 [Edit] tests: local:T-W2.12-06:extension-registration — the
// SPEC §X.3.2 / §B0.2 extension gate wired through the real SPEC §C3
// Registry build, inspected at the published Registry (not the discovery
// report), with constructor probes.
//
// Phase 1 (before construction): the metadata-rejected fixtures —
// com.opweave.fixture.badrange (OW-OP-012), com.opweave.fixture.blueprints
// (OW-OP-010), com.opweave.fixture.nomanifest (no readable manifest) and
// the com.opweave.fixture.dupver1/dupver2 pair declaring one Operation ID
// at two versions — contribute nothing and their probes are never
// constructed. Phase 2 (after construction, before publication):
// com.opweave.fixture.mismatch declares an Operation its assemblies do not
// contain, so the package contributes no Operation and no TypeProvider to
// the published Registry. The valid fixture's Operation and offered Type
// register normally.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UpmPackageSource = UnityEditor.PackageManager.PackageSource;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.12-06:extension-registration — Registry-level fixtures over
    /// the real stage 1–8 build with constructor probes.
    /// </summary>
    internal sealed class ExtensionRegistrationGateTests
    {
        private const string ValidPackage = "com.opweave.fixture.valid";
        private const string ValidOperationId = "com.opweave.fixture.valid.op";
        private const string ValidProviderId = "com.opweave.fixture.valid.types";
        private const string ValidTypeId = "com.opweave.fixture.valid.type";
        private const string BadRangePackage = "com.opweave.fixture.badrange";
        private const string BlueprintsPackage = "com.opweave.fixture.blueprints";
        private const string NoManifestPackage = "com.opweave.fixture.nomanifest";
        private const string MismatchPackage = "com.opweave.fixture.mismatch";
        private const string DupVer1Package = "com.opweave.fixture.dupver1";
        private const string DupVer2Package = "com.opweave.fixture.dupver2";
        private const string DupVerOperationId = "com.opweave.fixture.dupver.op";

        private static RegistryBuildResult Rebuild()
        {
            var publication = new RegistryPublication();
            return RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                publication);
        }

        private static TypeRef TR(string id) => TypeRef.Parse(id).Value!;

        // Reads the static s_ctorCalls probe of a fixture class discovered
        // through TypeCache (the same mechanism the UnityMin fixture test
        // uses — fixture classes live in other assemblies).
        private static FieldInfo CtorProbeOf(string typeFullName)
        {
            var type = TypeCache
                .GetTypesWithAttribute<OpWeaveTypeProviderAttribute>()
                .Concat(TypeCache.GetTypesWithAttribute<OpWeave.Core.Operations.OpWeaveOperationAttribute>())
                .FirstOrDefault(t => t.FullName == typeFullName);
            Assert.That(type, Is.Not.Null, $"fixture type '{typeFullName}' was not discovered");
            var field = type!.GetField("s_ctorCalls", BindingFlags.Public | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, $"fixture type '{typeFullName}' lost its constructor probe");
            return field!;
        }

        private static IEnumerable<string> FailureMessages(
            RegistryBuildResult result, string typeFullName) =>
            result.Failures
                .Concat(result.ExtensionFailures)
                .Concat(result.OperationFailures)
                .Where(f => f.TypeFullName == typeFullName)
                .SelectMany(f => f.Diagnostics)
                .Select(d => d.Code + " " + d.Message);

        // A valid fixture extension registers: its Operation reaches the
        // published snapshot with its package identity, its TypeProvider is
        // bound and its offered Type ID resolves in the published type
        // snapshot (SPEC §X.3.2 — an accepted extension registers).
        [Test]
        public void ValidExtension_OperationAndProviderRegister()
        {
            var result = Rebuild();

            Assert.That(result.Stale, Is.False);
            var snapshot = result.Snapshot;
            Assert.That(snapshot, Is.Not.Null);
            var operation = snapshot!.Find(ValidOperationId, 1);
            Assert.That(operation, Is.Not.Null,
                "the valid fixture's declared Operation must register");
            Assert.That(operation!.Origin, Is.EqualTo(OperationOrigin.Custom));
            Assert.That(operation.PackageId, Is.EqualTo(ValidPackage));
            Assert.That(result.Providers.Select(p => p.ProviderId),
                Has.Some.EqualTo(ValidProviderId),
                "the valid fixture's TypeProvider must be bound");
            Assert.That(result.Types!.Resolve(TR(ValidTypeId)).Ok, Is.True,
                "the valid fixture's offered Type ID must resolve in the published snapshot");
            Assert.That(result.ExtensionFailures,
                Has.None.Matches<ExtensionCandidateFailure>(f =>
                    f.Diagnostics.Any(d => d.Message.Contains(ValidPackage))),
                "the valid fixture must not be gated out");
        }

        // Metadata-rejected extensions (OW-OP-012 / OW-OP-010 / missing
        // manifest): never constructed, contributing nothing.
        [Test]
        public void MetadataRejected_NeverConstructed_ContributeNothing()
        {
            var badRangeProbe = CtorProbeOf("com.opweave.fixture.badrange.BadRangeProvider");
            var blueprintsProbe = CtorProbeOf("com.opweave.fixture.blueprints.BlueprintsProvider");
            var noManifestProbe = CtorProbeOf("com.opweave.fixture.nomanifest.NoManifestProvider");
            badRangeProbe.SetValue(null, 0);
            blueprintsProbe.SetValue(null, 0);
            noManifestProbe.SetValue(null, 0);

            var result = Rebuild();

            Assert.That(badRangeProbe.GetValue(null), Is.EqualTo(0),
                "an OW-OP-012 package's provider must never be constructed (SPEC §B0.2)");
            Assert.That(blueprintsProbe.GetValue(null), Is.EqualTo(0),
                "an OW-OP-010 package's provider must never be constructed (SPEC §X.10.4)");
            Assert.That(noManifestProbe.GetValue(null), Is.EqualTo(0),
                "a manifest-less package's provider must never be constructed (SPEC §X.2)");

            Assert.That(result.Failures.SelectMany(f => f.Diagnostics.Select(d => d.Code)),
                Has.Some.EqualTo(ErrorCodes.OW_OP_012),
                "the bad-range package's OW-OP-012 diagnostic must reach the build result");
            Assert.That(result.Failures.SelectMany(f => f.Diagnostics.Select(d => d.Code)),
                Has.Some.EqualTo(ErrorCodes.OW_OP_010),
                "the blueprints package's OW-OP-010 diagnostic must reach the build result");
            Assert.That(result.Failures
                .Where(f => f.TypeFullName == "com.opweave.fixture.nomanifest.NoManifestProvider")
                .SelectMany(f => f.Diagnostics.Select(d => d.Message)),
                Has.Some.Contains("opweave.extension.json"),
                "the manifest-less package's rejection must reach the build result");

            var providerIds = result.Providers.Select(p => p.ProviderId).ToList();
            Assert.That(providerIds, Has.None.EqualTo("com.opweave.fixture.badrange.types"));
            Assert.That(providerIds, Has.None.EqualTo("com.opweave.fixture.blueprints.types"));
        }

        // The declared operations[] list differs from the observed
        // Descriptors (declared but never implemented): the package
        // contributes no Operation and no TypeProvider to the published
        // Registry — the observed-fact gate drops its provider out of the
        // type snapshot again.
        [Test]
        public void ObservedMismatch_ContributesNothing()
        {
            var result = Rebuild();

            Assert.That(result.Snapshot!.Find("com.opweave.fixture.mismatch.op", 1), Is.Null,
                "a declared-but-absent Operation must not register");
            Assert.That(result.Providers.Select(p => p.ProviderId),
                Has.None.EqualTo("com.opweave.fixture.mismatch.types"),
                "the mismatched package's provider must not reach the published build");
            Assert.That(FailureMessages(result, "com.opweave.fixture.mismatch.MismatchProvider"),
                Has.Some.Contains("does not match"),
                "the observed-fact rejection must reach the build result");
        }

        // The same Operation ID declared at two different versions by two
        // packages is a duplicate by ID alone: both packages are rejected
        // before construction (SPEC §X.3.2).
        [Test]
        public void CrossVersionDuplicateOperationId_BothRejected()
        {
            var probe1 = CtorProbeOf("com.opweave.fixture.dupver1.DupVer1Operation");
            var probe2 = CtorProbeOf("com.opweave.fixture.dupver2.DupVer2Operation");
            probe1.SetValue(null, 0);
            probe2.SetValue(null, 0);

            var result = Rebuild();

            Assert.That(probe1.GetValue(null), Is.EqualTo(0),
                "a duplicate-ID package's Operation must never be constructed");
            Assert.That(probe2.GetValue(null), Is.EqualTo(0),
                "a duplicate-ID package's Operation must never be constructed");
            Assert.That(result.Snapshot!.Find(DupVerOperationId, 1), Is.Null);
            Assert.That(result.Snapshot!.Find(DupVerOperationId, 2), Is.Null);
            Assert.That(result.Snapshot.Operations
                .Where(o => o.Descriptor.Id == DupVerOperationId), Is.Empty,
                "no version of the duplicated Operation ID may register");

            var failure = result.Failures.FirstOrDefault(f =>
                f.Kind == ExtensionCandidateKind.Operation &&
                f.TypeFullName == "com.opweave.fixture.dupver1.DupVer1Operation");
            Assert.That(failure, Is.Not.Null,
                "the dupver1 rejection must reach the build result");
            Assert.That(failure!.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("more than one package"),
                "duplicate Operation IDs must be reported as cross-package duplicates");
            Assert.That(result.Failures.Any(f =>
                    f.Kind == ExtensionCandidateKind.Operation &&
                    f.TypeFullName == "com.opweave.fixture.dupver2.DupVer2Operation"),
                Is.True, "the dupver2 rejection must reach the build result");
        }

        // The invalid fixture population does not disturb a valid extension
        // or unrelated registrations: the build completes, the valid op is
        // the only registered extension Operation and the standard set is
        // unaffected.
        [Test]
        public void InvalidFixtures_DoNotDisturbValidRegistration()
        {
            var result = Rebuild();

            Assert.That(result.Stale, Is.False);
            var extensionOps = result.Snapshot!.Operations
                .Where(o => o.PackageId == ValidPackage)
                .Select(o => o.Descriptor.Id)
                .ToList();
            CollectionAssert.AreEquivalent(new[] { ValidOperationId }, extensionOps);
            Assert.That(result.Snapshot.Find("core.start", 1), Is.Not.Null,
                "standard Operations are unaffected by extension rejections");
        }

        // ── SPEC §X.3.2 / §M.5: an OW-OP-017 rejection stays individual ──
        //
        // The seams below fabricate one extension package —
        // com.opweave.fixture.partial — contributing two Operations in two
        // fabricated Editor assemblies plus one bound TypeProvider. The
        // fingerprint seam then refuses only the second assembly's
        // candidate the way an unresolvable or not-provably-Editor-only
        // defining assembly does (OW-OP-017). That Operation's Descriptor
        // was still read, so the package's declared operations[] still
        // equals the observed Descriptor set: the rejection stays
        // individual and the package's other Operation and TypeProvider
        // keep registering (SPEC §M.5 "無関係なOperationは維持", AT-F03).

        private const string PartialPackage = "com.opweave.fixture.partial";
        private const string PartialAssemblyA = "OpWeave.Fixture.Partial.EditorA";
        private const string PartialAssemblyB = "OpWeave.Fixture.Partial.EditorB";
        private const string PartialOpAId = "test.registry.partial.a";
        private const string PartialOpBId = "test.registry.partial.b";
        private const string PartialProviderId = "test.fixture.partial.types";
        private const string PartialTypeId = "test.fixture.partial.type";

        private const string PartialManifest =
            "{\n" +
            "  \"schemaVersion\": \"1.0\",\n" +
            "  \"extensionId\": \"com.opweave.fixture.partial\",\n" +
            "  \"displayName\": \"OpWeave Fixture Partial\",\n" +
            "  \"coreApi\": {\"minInclusive\": \"1.0.0\", \"maxExclusive\": \"2.0.0\"},\n" +
            "  \"unity\": {\"minInclusive\": \"6000.0\"},\n" +
            "  \"assemblies\": [\"OpWeave.Fixture.Partial.EditorA\", \"OpWeave.Fixture.Partial.EditorB\"],\n" +
            "  \"operations\": [{\"id\": \"test.registry.partial.a\", \"version\": 1}, {\"id\": \"test.registry.partial.b\", \"version\": 1}],\n" +
            "  \"typeProviders\": [{\"id\": \"test.fixture.partial.types\", \"assembly\": \"OpWeave.Fixture.Partial.EditorA\"}],\n" +
            "  \"examples\": [],\n" +
            "  \"blueprints\": []\n" +
            "}";

        internal abstract class PartialFixtureOperation : IOperation
        {
            public abstract OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
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

        [OpWeaveOperation(PartialOpAId, 1)]
        internal sealed class PartialOperationA : PartialFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Op
                .Define(PartialOpAId, 1)
                .Summary("A partial-package fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(PartialOpAId, 1, outputPort: "done"),
                    },
                };
        }

        [OpWeaveOperation(PartialOpBId, 1)]
        internal sealed class PartialOperationB : PartialFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Op
                .Define(PartialOpBId, 1)
                .Summary("A partial-package fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(PartialOpBId, 1, outputPort: "done"),
                    },
                };
        }

        [OpWeaveTypeProvider]
        internal sealed class PartialProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[]
            {
                new TypeDefinition(
                    PartialTypeId, TypeKind.Primitive, 0,
                    Array.Empty<string>(), null, "Partial fixture type.", StringCodec.Instance),
            };
        }

        // Stages 1–8 over the fabricated package: the production seams
        // everywhere except the candidate type lists (exactly the fixture
        // classes, attributed by the provenance seam into the package's two
        // fabricated assemblies), the package-facts reader (the manifest
        // text and rooted Editor assemblies of the fabricated package) and
        // the fingerprint seam, which refuses the second fabricated
        // assembly the way an OW-OP-017 rejection does.
        private static RegistryBuildResult RebuildPartial()
        {
            var package = new UpmPackageProvenance(
                PartialPackage, "1.0.0", UpmPackageSource.Embedded, null, null, null);
            var typeSeams = new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => new[] { typeof(PartialOperationA), typeof(PartialOperationB) },
                ProviderTypes: () => new[] { typeof(PartialProvider) },
                ResolveProvenance: type =>
                {
                    var resolved = EditorAssemblyResolver.ResolveForType(type);
                    var assembly =
                        type == typeof(PartialOperationA) || type == typeof(PartialProvider)
                            ? PartialAssemblyA
                            : type == typeof(PartialOperationB)
                                ? PartialAssemblyB
                                : null;
                    if (resolved.Ok && assembly is not null)
                    {
                        var real = resolved.Value!;
                        resolved = Result<EditorAssemblyProvenance>.Success(
                            new EditorAssemblyProvenance(
                                assembly, real.CompiledAssemblyPath, real.IsPrecompiled,
                                OperationOrigin.Custom, package, real.CompilationAssembly));
                    }
                    return resolved;
                },
                PackageFacts: id => string.Equals(id, PartialPackage, StringComparison.Ordinal)
                    ? new ExtensionPackageFacts(
                        PartialManifest,
                        "Packages/com.opweave.fixture.partial",
                        new[] { PartialAssemblyA, PartialAssemblyB })
                    : null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());
            var operationSeams = RegistryOperationStage.Production() with
            {
                ImplementationHash = provenance =>
                    string.Equals(provenance.AssemblyName, PartialAssemblyB, StringComparison.Ordinal)
                        ? Result<DefiningAssemblyFingerprint>.Failure(ErrorCodes.Create(
                            ErrorCodes.OW_OP_017,
                            "Defining assembly's compiled bytes could not be read."))
                        : WeakFingerprintProvider.ForProvenance(provenance),
            };
            return RegistryBuildHost.Rebuild(
                typeSeams, operationSeams, new RegistryPublication());
        }

        // AT-F03 / SPEC §M.5: an OW-OP-017 fingerprint failure rejects only
        // the affected Operation. Its Descriptor was read, so the package's
        // declared operations[] still equals the observed set — the package
        // stays accepted and its other Operation and TypeProvider publish.
        [Test]
        public void FingerprintFailure_RejectsOnlyThatOperation()
        {
            var result = RebuildPartial();

            Assert.That(result.Stale, Is.False);
            var snapshot = result.Snapshot;
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(result.Failures, Is.Empty,
                "the fabricated package must survive stages 1–5 cleanly");

            // The failed candidate alone carries the OW-OP-017 rejection.
            Assert.That(snapshot!.Find(PartialOpBId, 1), Is.Null,
                "the fingerprint-failed Operation must not register");
            var failed = result.OperationFailures
                .Where(f => f.TypeFullName == typeof(PartialOperationB).FullName)
                .ToList();
            Assert.That(failed, Has.Count.EqualTo(1));
            Assert.That(failed[0].Diagnostics.Select(d => d.Code),
                Has.Some.EqualTo(ErrorCodes.OW_OP_017),
                "the rejected Operation must carry its OW-OP-017 diagnostic");

            // The package survives: the Descriptor set the build observed
            // still equals the declared operations[].
            Assert.That(result.ExtensionFailures, Is.Empty,
                "a fingerprint failure must not reject the whole package (SPEC §X.3.2, §M.5)");
            var registered = snapshot.Find(PartialOpAId, 1);
            Assert.That(registered, Is.Not.Null,
                "the unaffected Operation of the same package must keep registering");
            Assert.That(registered!.PackageId, Is.EqualTo(PartialPackage));
            Assert.That(result.Providers.Select(p => p.ProviderId),
                Has.Some.EqualTo(PartialProviderId),
                "the package's TypeProvider must stay bound");
            Assert.That(result.Types!.Resolve(TR(PartialTypeId)).Ok, Is.True,
                "the package's offered Type ID must resolve in the published snapshot");
        }

        // ── SPEC §X.3.2 / §X.6 / §M.5: the whole rejection matrix ──────
        //
        // One table-driven Registry-level pass — one row per reason the
        // observed-fact gate (RegistryBuildHost.EvaluateExtensionPackages,
        // or anything it calls) can refuse a candidate. Every fabricated
        // package also carries an unrelated valid Operation and
        // TypeProvider: PACKAGE rows must contribute nothing — the
        // unrelated candidates die with the package — while INDIVIDUAL
        // rows must publish them and reject only the defective candidate
        // (SPEC §C3 steps 4/6, §M.5 "無関係なOperationは維持", §X.6
        // "両方拒否、依存するOperationのみ失効").

        private const string GateAsmA = "OpWeave.Fixture.Gate.EditorA";
        private const string GateAsmB = "OpWeave.Fixture.Gate.EditorB";
        private const string GateAsmC = "OpWeave.Fixture.Gate.EditorC";
        private const string GateAsmD = "OpWeave.Fixture.Gate.EditorD";
        private const string GateAsmE = "OpWeave.Fixture.Gate.EditorE";
        private const string GateAsmF = "OpWeave.Fixture.Gate.EditorF";

        private const string GoodOpId = "test.registry.gate.good";
        private const string GoodBOpId = "test.registry.gate.good.b";
        private const string CtorFailOpId = "test.registry.gate.ctorfail";
        private const string DescFailOpId = "test.registry.gate.descfail";
        private const string HashOpId = "test.registry.gate.hash";
        private const string VersionedOpId = "test.registry.gate.versioned";
        private const string ExtraOpId = "test.registry.gate.extra";
        private const string DependentOpId = "test.registry.gate.dependent";
        private const string GhostOpId = "test.registry.gate.ghost";
        private const string DupOpId = "test.registry.gate.dup.op";

        private const string GoodProviderId = "test.registry.gate.types";
        private const string GoodBProviderId = "test.registry.gate.types.b";
        private const string DupAProviderId = "test.registry.gate.dup.a";
        private const string DupBProviderId = "test.registry.gate.dup.b";
        private const string BrokenProviderId = "test.registry.gate.broken";
        private const string CtorProviderId = "test.registry.gate.ctorprov";
        private const string MalformedProviderId = "test.registry.gate.malformed";
        private const string ReservedProviderId = "test.registry.gate.reserved";
        private const string PartialThrowProviderId = "test.registry.gate.partial";

        private const string GoodTypeId = "test.registry.gate.type";
        private const string GoodBTypeId = "test.registry.gate.type.b";
        private const string DupTypeId = "test.registry.gate.dup.type";
        private const string MalformedTypeId = "not a type";
        private const string ReservedTypeId = "core.fixture.gate.reserved";
        private const string CredentialSecret = "s3cr3t-token";
        private const string CredentialTypeId =
            "https://user:s3cr3t-token@example.com/type";
        private const string QueryCredentialTypeId = "x?token=s3cr3t-token";
        // The round-6 leak form: a bare credential assignment behind no
        // '?'/'&'/';'/'#' marker and no scheme userinfo at all.
        private const string BareCredentialTypeId = "password=secret-value";
        private const string CredentialProviderIdA = "test.registry.gate.cred.a";
        private const string CredentialProviderIdB = "test.registry.gate.cred.b";
        private const string QueryProviderIdA = "test.registry.gate.credq.a";
        private const string QueryProviderIdB = "test.registry.gate.credq.b";
        private const string BareProviderIdA = "test.registry.gate.credbare.a";
        private const string BareProviderIdB = "test.registry.gate.credbare.b";

        private static TypeDefinition GateTypeDef(string id) =>
            new TypeDefinition(id, TypeKind.Primitive, 0,
                Array.Empty<string>(), null, "Gate fixture type.", StringCodec.Instance);

        [OpWeaveOperation(GoodOpId, 1)]
        private sealed class GateGoodOperation : PartialFixtureOperation
        {
            internal static int s_ctorCalls;
            public GateGoodOperation() => s_ctorCalls++;

            public override OperationDescriptor Descriptor { get; } = Op
                .Define(GoodOpId, 1)
                .Summary("Gate fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(GoodOpId, 1, outputPort: "done"),
                    },
                };
        }

        [OpWeaveOperation(GoodBOpId, 1)]
        private sealed class GateGoodOperationB : PartialFixtureOperation
        {
            internal static int s_ctorCalls;
            public GateGoodOperationB() => s_ctorCalls++;

            public override OperationDescriptor Descriptor { get; } = Op
                .Define(GoodBOpId, 1)
                .Summary("Gate fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(GoodBOpId, 1, outputPort: "done"),
                    },
                };
        }

        [OpWeaveOperation(CtorFailOpId, 1)]
        private sealed class GateThrowCtorOperation : PartialFixtureOperation
        {
            public GateThrowCtorOperation() => throw new InvalidOperationException("gate fixture ctor");

            public override OperationDescriptor Descriptor { get; } = Op
                .Define(CtorFailOpId, 1)
                .Summary("Gate fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();
        }

        [OpWeaveOperation(DescFailOpId, 1)]
        private sealed class GateThrowDescriptorOperation : PartialFixtureOperation
        {
            public override OperationDescriptor Descriptor =>
                throw new InvalidOperationException("gate fixture descriptor");
        }

        [OpWeaveOperation(HashOpId, 1)]
        private sealed class GateHashOperation : PartialFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Op
                .Define(HashOpId, 1)
                .Summary("Gate fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(HashOpId, 1, outputPort: "done"),
                    },
                };
        }

        [OpWeaveOperation(VersionedOpId, 2)]
        private sealed class GateVersionedOperation : PartialFixtureOperation
        {
            internal static int s_ctorCalls;
            public GateVersionedOperation() => s_ctorCalls++;

            public override OperationDescriptor Descriptor { get; } = Op
                .Define(VersionedOpId, 2)
                .Summary("Gate fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(VersionedOpId, 2, outputPort: "done"),
                    },
                };
        }

        [OpWeaveOperation(ExtraOpId, 1)]
        private sealed class GateExtraOperation : PartialFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Op
                .Define(ExtraOpId, 1)
                .Summary("Gate fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(ExtraOpId, 1, outputPort: "done"),
                    },
                };
        }

        [OpWeaveOperation(DependentOpId, 1)]
        private sealed class GateDependentOperation : PartialFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Op
                .Define(DependentOpId, 1)
                .Summary("Gate fixture operation depending on the duplicated type.")
                .Output("result", DupTypeId)
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(DependentOpId, 1, outputPort: "result"),
                    },
                };
        }

        [OpWeaveOperation(DupOpId, 1)]
        private sealed class GateDupOperationA : PartialFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Op
                .Define(DupOpId, 1)
                .Summary("Gate fixture operation colliding inside one package.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();
        }

        [OpWeaveOperation(DupOpId, 1)]
        private sealed class GateDupOperationB : PartialFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } = Op
                .Define(DupOpId, 1)
                .Summary("Gate fixture operation colliding inside one package.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();
        }

        [OpWeaveTypeProvider]
        private sealed class GateGoodProvider : ITypeProvider
        {
            internal static int s_ctorCalls;
            public GateGoodProvider() => s_ctorCalls++;
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(GoodTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateGoodProviderB : ITypeProvider
        {
            internal static int s_ctorCalls;
            public GateGoodProviderB() => s_ctorCalls++;
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(GoodBTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateDupProviderA : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(DupTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateDupProviderB : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(DupTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateThrowGetTypesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                throw new InvalidOperationException("gate fixture provider");
        }

        [OpWeaveTypeProvider]
        private sealed class GatePartialThrowProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes()
            {
                yield return GateTypeDef(DupTypeId);
                throw new InvalidOperationException("gate fixture provider enumeration");
            }
        }

        [OpWeaveTypeProvider]
        private sealed class GateCtorThrowProvider : ITypeProvider
        {
            public GateCtorThrowProvider() => throw new InvalidOperationException("gate fixture provider ctor");
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(GoodTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateMalformedProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(MalformedTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateReservedProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(ReservedTypeId) };
        }

        // Two provider pairs offering the same unvalidated
        // credential-bearing Type ID across two packages each — the
        // observed-fact gate's cross-package duplicate verdict (OW-OP-013).
        [OpWeaveTypeProvider]
        private sealed class GateCredentialProviderA : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(CredentialTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateCredentialProviderB : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(CredentialTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateCredentialQueryProviderA : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(QueryCredentialTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateCredentialQueryProviderB : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(QueryCredentialTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateCredentialBareProviderA : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(BareCredentialTypeId) };
        }

        [OpWeaveTypeProvider]
        private sealed class GateCredentialBareProviderB : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[] { GateTypeDef(BareCredentialTypeId) };
        }

        // One fabricated (type, assembly) placement.
        internal sealed class GateCandidate
        {
            internal GateCandidate(Type type, string assembly)
            {
                Type = type;
                Assembly = assembly;
            }

            internal Type Type { get; }
            internal string Assembly { get; }
        }

        internal sealed class GatePackage
        {
            internal string PackageId = "";
            internal string? Manifest;
            internal string RootPath = "";
            internal string[] Assemblies = Array.Empty<string>();
            internal GateCandidate[] Operations = Array.Empty<GateCandidate>();
            internal GateCandidate[] Providers = Array.Empty<GateCandidate>();
        }

        internal sealed class GateRow
        {
            internal string Name = "";
            internal GatePackage[] Packages = Array.Empty<GatePackage>();
            internal string[] RefusedAssemblies = Array.Empty<string>();
            internal bool PackageRejected;
            internal bool ExpectNeverConstructed;
            internal string[] DiagnosticFragments = Array.Empty<string>();
            internal string[] DiagnosticCodes = Array.Empty<string>();
            internal string[] RegisteredOps = Array.Empty<string>();
            internal string[] MissingOps = Array.Empty<string>();
            internal string[] BoundProviders = Array.Empty<string>();
            internal string[] MissingProviders = Array.Empty<string>();
            internal string[] ResolvableTypes = Array.Empty<string>();
            internal string[] UnresolvableTypes = Array.Empty<string>();
            // For a package rejected only after construction: the probe of
            // a candidate this row actually supplies — a post-construction
            // rejection still constructs the package.
            internal Func<int> ConstructedProbe = () => GateGoodOperation.s_ctorCalls;
            internal (string TypeName, string Code)[] OpFailures = Array.Empty<(string, string)>();
            internal (string TypeName, string Code)[] StageFails = Array.Empty<(string, string)>();
            internal (TypeRegistryFailureKind Kind, string? TypeId)[] TypeFails =
                Array.Empty<(TypeRegistryFailureKind, string?)>();
            internal (string OpId, string Code)[] SnapFails = Array.Empty<(string, string)>();
        }

        private static string J(params string[] items) =>
            "[" + string.Join(", ", items) + "]";

        private static string OpRef(string id, int version = 1) =>
            "{\"id\": \"" + id + "\", \"version\": " + version + "}";

        private static string ProvRef(string id, string assembly) =>
            "{\"id\": \"" + id + "\", \"assembly\": \"" + assembly + "\"}";

        private static string AsmList(params string[] names) =>
            "[" + string.Join(", ", names.Select(n => "\"" + n + "\"")) + "]";

        private static string GateManifest(
            string extensionId,
            string schemaVersion = "\"1.0\"",
            string coreApi = "{\"minInclusive\": \"1.0.0\", \"maxExclusive\": \"2.0.0\"}",
            string unity = "{\"minInclusive\": \"6000.0\"}",
            string assemblies = "[]",
            string operations = "[]",
            string typeProviders = "[]",
            string examples = "[]",
            string blueprints = "[]") =>
            "{\n" +
            "  \"schemaVersion\": " + schemaVersion + ",\n" +
            "  \"extensionId\": \"" + extensionId + "\",\n" +
            "  \"coreApi\": " + coreApi + ",\n" +
            "  \"unity\": " + unity + ",\n" +
            "  \"assemblies\": " + assemblies + ",\n" +
            "  \"operations\": " + operations + ",\n" +
            "  \"typeProviders\": " + typeProviders + ",\n" +
            "  \"examples\": " + examples + ",\n" +
            "  \"blueprints\": " + blueprints + "\n" +
            "}";

        // The "unrelated valid Operation and TypeProvider" every row's
        // package carries, plus its matching manifest.
        private static string GoodManifest(string extensionId, string asm) =>
            GateManifest(extensionId,
                assemblies: AsmList(asm),
                operations: J(OpRef(GoodOpId)),
                typeProviders: J(ProvRef(GoodProviderId, asm)));

        private static GatePackage GatePkg(
            string packageId, string asm, string? manifest,
            GateCandidate[]? operations = null, GateCandidate[]? providers = null,
            string[]? assemblies = null) =>
            new GatePackage
            {
                PackageId = packageId,
                Manifest = manifest,
                RootPath = "Packages/" + packageId,
                Assemblies = assemblies ?? new[] { asm },
                Operations = operations ?? new[] { new GateCandidate(typeof(GateGoodOperation), asm) },
                Providers = providers ?? new[] { new GateCandidate(typeof(GateGoodProvider), asm) },
            };

        private static TestCaseData Row(GateRow row) => new TestCaseData(row).SetName(row.Name);

        private static IEnumerable<TestCaseData> GateRows()
        {
            // ── PACKAGE level: metadata checks decidable before
            //    construction — the unrelated candidates are never
            //    constructed (SPEC §B0.2, §X.3.2). ──
            yield return Row(new GateRow
            {
                Name = "package-schemaVersion",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.schema", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.schema",
                            schemaVersion: "\"2.0\"",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA)))),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticFragments = new[] { "schemaVersion" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "package-coreApi",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.range", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.range",
                            coreApi: "{\"minInclusive\": \"2.0.0\", \"maxExclusive\": \"3.0.0\"}",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA)))),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticCodes = new[] { ErrorCodes.OW_OP_012 },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "package-unity-minimum",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.unity", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.unity",
                            unity: "{\"minInclusive\": \"9999.0\"}",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA)))),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticFragments = new[] { "9999.0" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "package-blueprints",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.blueprints", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.blueprints",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA)),
                            blueprints: "[\"Blueprints/setup.opweave-blueprint.json\"]")),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticCodes = new[] { ErrorCodes.OW_OP_010 },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "package-reserved-declared-id",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.reservedop", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.reservedop",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef("core.gate.reserved")),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA)))),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticFragments = new[] { "reserved" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "package-inmanifest-duplicate-id",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.dupdecl", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.dupdecl",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA)))),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticFragments = new[] { "duplicate" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "package-no-manifest",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.nomanifest", GateAsmA, manifest: null),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticFragments = new[] { "opweave.extension.json" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "package-declared-id-dup-two-packages",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.dupa", GateAsmA,
                        GoodManifest("com.opweave.fixture.gate.dupa", GateAsmA)),
                    GatePkg("com.opweave.fixture.gate.dupb", GateAsmB,
                        GateManifest("com.opweave.fixture.gate.dupb",
                            assemblies: AsmList(GateAsmB),
                            operations: J(OpRef(GoodOpId), OpRef(GoodBOpId)),
                            typeProviders: J(ProvRef(GoodBProviderId, GateAsmB))),
                        operations: new[] { new GateCandidate(typeof(GateGoodOperationB), GateAsmB) },
                        providers: new[] { new GateCandidate(typeof(GateGoodProviderB), GateAsmB) }),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticFragments = new[] { "more than one package" },
                MissingOps = new[] { GoodOpId, GoodBOpId },
                MissingProviders = new[] { GoodProviderId, GoodBProviderId },
                UnresolvableTypes = new[] { GoodTypeId, GoodBTypeId },
            });
            yield return Row(new GateRow
            {
                // A provider class in an assembly the manifest does not bind
                // a Provider ID to — the §X.6 per-assembly pairing fails.
                Name = "package-provider-binding-mismatch",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.badbinding", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.badbinding",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        providers: new[] { new GateCandidate(typeof(GateGoodProvider), GateAsmB) },
                        assemblies: new[] { GateAsmA, GateAsmB }),
                },
                PackageRejected = true,
                ExpectNeverConstructed = true,
                DiagnosticFragments = new[] { "[OpWeaveTypeProvider]" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });

            // ── PACKAGE level: proven declared-vs-observed mismatches and
            //    other declaration checks completing after construction. ──
            yield return Row(new GateRow
            {
                // Declared Provider ID names an assembly holding no provider
                // class — nothing pairs it, observed or not.
                Name = "gate-declared-provider-absent",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.noprovider", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.noprovider",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        providers: Array.Empty<GateCandidate>()),
                },
                PackageRejected = true,
                DiagnosticFragments = new[] { "No [OpWeaveTypeProvider] class" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
            });
            yield return Row(new GateRow
            {
                // A declared Operation whose class does not exist anywhere
                // in the package's assemblies — no failed candidate can
                // cover it, so the mismatch is proven.
                Name = "gate-ghost-operation",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.ghost", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.ghost",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(GhostOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA)))),
                },
                PackageRejected = true,
                DiagnosticFragments = new[] { "does not match" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // Two declared entries without a read Descriptor but only
                // one individually-failed candidate to cover them — the
                // second is a declared item with no class at all.
                Name = "gate-ghost-beyond-failure-cover",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.ghosts", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.ghosts",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(GhostOpId), OpRef("test.registry.gate.ghost.two")),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateThrowCtorOperation), GateAsmA),
                        }),
                },
                PackageRejected = true,
                DiagnosticFragments = new[] { "does not match" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // The id was actually observed — at another version. A
                // proven difference, not "not observed".
                Name = "gate-observed-different-version",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.ver", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.ver",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(VersionedOpId, 1)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        operations: new[] { new GateCandidate(typeof(GateVersionedOperation), GateAsmA) }),
                },
                PackageRejected = true,
                DiagnosticFragments = new[] { "does not match" },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
                ConstructedProbe = () => GateVersionedOperation.s_ctorCalls,
            });
            yield return Row(new GateRow
            {
                // A Descriptor was actually read that the manifest never
                // declared — proven extra.
                Name = "gate-observed-undeclared",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.extra", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.extra",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateExtraOperation), GateAsmA),
                        }),
                },
                PackageRejected = true,
                DiagnosticFragments = new[] { "does not match" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // An Operation was actually observed inside a rooted but
                // undeclared assembly — a proven declaration/reality
                // difference.
                Name = "gate-undeclared-assembly",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.badassembly", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.badassembly",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(ExtraOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateExtraOperation), GateAsmB),
                        },
                        assemblies: new[] { GateAsmA, GateAsmB }),
                },
                PackageRejected = true,
                DiagnosticFragments = new[] { "undeclared assembly" },
                MissingOps = new[] { GoodOpId, ExtraOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // A declared example path that resolves to no file inside
                // the package root — a proven declaration/reality
                // difference.
                Name = "gate-examples-missing",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.examples", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.examples",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA)),
                            examples: "[\"Samples~/Workflows/absent-gate-fixture.opweave.json\"]")),
                },
                PackageRejected = true,
                DiagnosticFragments = new[] { "absent-gate-fixture.opweave.json" },
                MissingOps = new[] { GoodOpId },
                MissingProviders = new[] { GoodProviderId },
                UnresolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // The same offered Type ID in two different packages is a
                // duplicate against the other package's observed IDs —
                // both extensions are refused (SPEC §X.3.2).
                Name = "gate-cross-package-type-dup",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.shared.a", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.shared.a",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA), ProvRef(DupAProviderId, GateAsmA))),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateGoodProvider), GateAsmA),
                            new GateCandidate(typeof(GateDupProviderA), GateAsmA),
                        }),
                    GatePkg("com.opweave.fixture.gate.shared.b", GateAsmB,
                        GateManifest("com.opweave.fixture.gate.shared.b",
                            assemblies: AsmList(GateAsmB),
                            operations: J(OpRef(GoodBOpId)),
                            typeProviders: J(ProvRef(GoodBProviderId, GateAsmB), ProvRef(DupBProviderId, GateAsmB))),
                        operations: new[] { new GateCandidate(typeof(GateGoodOperationB), GateAsmB) },
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateGoodProviderB), GateAsmB),
                            new GateCandidate(typeof(GateDupProviderB), GateAsmB),
                        }),
                },
                PackageRejected = true,
                // A grammar-valid shared Type ID is published verbatim in
                // the duplicate verdict — only malformed IDs take the
                // placeholder (SPEC §C3 error paragraph).
                DiagnosticFragments = new[] { "more than one package", DupTypeId },
                MissingOps = new[] { GoodOpId, GoodBOpId },
                MissingProviders = new[] { GoodProviderId, GoodBProviderId, DupAProviderId, DupBProviderId },
                UnresolvableTypes = new[] { GoodTypeId, GoodBTypeId, DupTypeId },
            });

            // ── INDIVIDUAL: per-candidate failures reject only the
            //    candidate; the unrelated Operation and TypeProvider of
            //    the same package stay published (SPEC §C3 steps 4/6,
            //    §M.5, §X.6). ──
            yield return Row(new GateRow
            {
                Name = "individual-op-construction",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.ctorfail", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.ctorfail",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(CtorFailOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateThrowCtorOperation), GateAsmA),
                        }),
                },
                OpFailures = new[] { (typeof(GateThrowCtorOperation).FullName!, ErrorCodes.OW_OP_008) },
                RegisteredOps = new[] { GoodOpId },
                BoundProviders = new[] { GoodProviderId },
                ResolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "individual-op-descriptor",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.descfail", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.descfail",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(DescFailOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateThrowDescriptorOperation), GateAsmA),
                        }),
                },
                OpFailures = new[] { (typeof(GateThrowDescriptorOperation).FullName!, ErrorCodes.OW_OP_008) },
                RegisteredOps = new[] { GoodOpId },
                BoundProviders = new[] { GoodProviderId },
                ResolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                Name = "individual-fingerprint",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.hashfail", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.hashfail",
                            assemblies: AsmList(GateAsmA, GateAsmB),
                            operations: J(OpRef(GoodOpId), OpRef(HashOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateHashOperation), GateAsmB),
                        },
                        assemblies: new[] { GateAsmA, GateAsmB }),
                },
                RefusedAssemblies = new[] { GateAsmB },
                OpFailures = new[] { (typeof(GateHashOperation).FullName!, ErrorCodes.OW_OP_017) },
                RegisteredOps = new[] { GoodOpId },
                BoundProviders = new[] { GoodProviderId },
                ResolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // SPEC §X.6: two providers of one package offering the same
                // Type ID — the type registry's collision pass invalidates
                // both registrations, the dependent Operation fails and
                // everything else of the package stays published.
                Name = "individual-duplicate-type",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.duptype", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.duptype",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(DependentOpId)),
                            typeProviders: J(
                                ProvRef(GoodProviderId, GateAsmA),
                                ProvRef(DupAProviderId, GateAsmA),
                                ProvRef(DupBProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateDependentOperation), GateAsmA),
                        },
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateGoodProvider), GateAsmA),
                            new GateCandidate(typeof(GateDupProviderA), GateAsmA),
                            new GateCandidate(typeof(GateDupProviderB), GateAsmA),
                        }),
                },
                TypeFails = new[]
                {
                    (TypeRegistryFailureKind.DuplicateTypeId, (string?)DupTypeId),
                    (TypeRegistryFailureKind.DuplicateTypeId, (string?)DupTypeId),
                },
                SnapFails = new[] { (DependentOpId, ErrorCodes.OW_OP_014) },
                RegisteredOps = new[] { GoodOpId },
                MissingOps = new[] { DependentOpId },
                BoundProviders = new[] { GoodProviderId, DupAProviderId, DupBProviderId },
                ResolvableTypes = new[] { GoodTypeId },
                UnresolvableTypes = new[] { DupTypeId },
            });
            yield return Row(new GateRow
            {
                // SPEC §C3 rule 4 / §M.5: two candidates of ONE package
                // producing the same declared (id, version). The manifest
                // comparison is set-based, so the package still passes the
                // observed-fact gate — this is not a cross-extension
                // duplicate, so the package verdict must not fire. Both
                // colliding candidates then fail individually at
                // registration (OW-OP-004) while the unrelated Operation
                // and TypeProvider of the same package publish.
                Name = "individual-duplicate-operation",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.dupop", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.dupop",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(DupOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateDupOperationA), GateAsmA),
                            new GateCandidate(typeof(GateDupOperationB), GateAsmA),
                        }),
                },
                SnapFails = new[] { (DupOpId, ErrorCodes.OW_OP_004) },
                RegisteredOps = new[] { GoodOpId },
                MissingOps = new[] { DupOpId },
                BoundProviders = new[] { GoodProviderId },
                ResolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // A provider whose GetTypes() throws is an individual
                // candidate failure — the type build reports it as a
                // ProviderFailure and only the dependent Operation fails.
                Name = "individual-provider-gettypes",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.throwprovider", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.throwprovider",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId), OpRef(DependentOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA), ProvRef(BrokenProviderId, GateAsmA))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateGoodOperation), GateAsmA),
                            new GateCandidate(typeof(GateDependentOperation), GateAsmA),
                        },
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateGoodProvider), GateAsmA),
                            new GateCandidate(typeof(GateThrowGetTypesProvider), GateAsmA),
                        }),
                },
                TypeFails = new[] { (TypeRegistryFailureKind.ProviderFailure, (string?)null) },
                SnapFails = new[] { (DependentOpId, ErrorCodes.OW_OP_014) },
                RegisteredOps = new[] { GoodOpId },
                MissingOps = new[] { DependentOpId },
                BoundProviders = new[] { GoodProviderId, BrokenProviderId },
                ResolvableTypes = new[] { GoodTypeId },
                UnresolvableTypes = new[] { DupTypeId },
            });
            yield return Row(new GateRow
            {
                // A provider whose GetTypes() yields a Type ID and then
                // throws mid-enumeration contributes no observed types:
                // the §C3 step-4 type build discards its whole partial
                // output, so the observed-fact gate must too — the same
                // Type ID offered by another package is no cross-package
                // duplicate. The failure stays a ProviderFailure on that
                // provider alone; both packages' other candidates publish
                // (SPEC §C3 step 4, §X.3.2, §X.6).
                Name = "individual-provider-partial-gettypes",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.partiala", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.partiala",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(
                                ProvRef(PartialThrowProviderId, GateAsmA),
                                ProvRef(GoodProviderId, GateAsmA))),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GatePartialThrowProvider), GateAsmA),
                            new GateCandidate(typeof(GateGoodProvider), GateAsmA),
                        }),
                    GatePkg("com.opweave.fixture.gate.partialb", GateAsmB,
                        GateManifest("com.opweave.fixture.gate.partialb",
                            assemblies: AsmList(GateAsmB),
                            operations: J(OpRef(DependentOpId)),
                            typeProviders: J(ProvRef(DupBProviderId, GateAsmB))),
                        operations: new[]
                        {
                            new GateCandidate(typeof(GateDependentOperation), GateAsmB),
                        },
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateDupProviderB), GateAsmB),
                        }),
                },
                TypeFails = new[] { (TypeRegistryFailureKind.ProviderFailure, (string?)null) },
                RegisteredOps = new[] { GoodOpId, DependentOpId },
                BoundProviders = new[] { PartialThrowProviderId, GoodProviderId, DupBProviderId },
                ResolvableTypes = new[] { GoodTypeId, DupTypeId },
            });
            yield return Row(new GateRow
            {
                // A provider that cannot be constructed is an individual
                // failure too (SPEC §C3 step 4 → OW-OP-019 at publication).
                Name = "individual-provider-construction",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.ctorprov", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.ctorprov",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA), ProvRef(CtorProviderId, GateAsmA))),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateGoodProvider), GateAsmA),
                            new GateCandidate(typeof(GateCtorThrowProvider), GateAsmA),
                        }),
                },
                StageFails = new[] { (typeof(GateCtorThrowProvider).FullName!, ErrorCodes.OW_OP_019) },
                RegisteredOps = new[] { GoodOpId },
                BoundProviders = new[] { GoodProviderId },
                ResolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // A non-canonical offered Type ID is screened per
                // definition by the type build, not against the package.
                Name = "individual-malformed-type",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.malformed", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.malformed",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA), ProvRef(MalformedProviderId, GateAsmA))),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateGoodProvider), GateAsmA),
                            new GateCandidate(typeof(GateMalformedProvider), GateAsmA),
                        }),
                },
                TypeFails = new[]
                {
                    // A malformed Type ID is never published verbatim —
                    // the failure record carries its redacted form
                    // (SPEC §C3 error paragraph).
                    (TypeRegistryFailureKind.MalformedTypeId,
                        (string?)TypeRegistryBuilder.RedactedTypeId(MalformedTypeId)),
                },
                RegisteredOps = new[] { GoodOpId },
                BoundProviders = new[] { GoodProviderId, MalformedProviderId },
                ResolvableTypes = new[] { GoodTypeId },
            });
            yield return Row(new GateRow
            {
                // A reserved-namespace offer from a custom package is
                // screened per definition, not against the package.
                Name = "individual-reserved-type",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.reservedtype", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.reservedtype",
                            assemblies: AsmList(GateAsmA),
                            operations: J(OpRef(GoodOpId)),
                            typeProviders: J(ProvRef(GoodProviderId, GateAsmA), ProvRef(ReservedProviderId, GateAsmA))),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateGoodProvider), GateAsmA),
                            new GateCandidate(typeof(GateReservedProvider), GateAsmA),
                        }),
                },
                TypeFails = new[] { (TypeRegistryFailureKind.ReservedNamespace, (string?)ReservedTypeId) },
                RegisteredOps = new[] { GoodOpId },
                BoundProviders = new[] { GoodProviderId, ReservedProviderId },
                ResolvableTypes = new[] { GoodTypeId },
                UnresolvableTypes = new[] { ReservedTypeId },
            });
        }

        // Stages 1–8 over the row's fabricated packages: the production
        // seams everywhere except the candidate type lists (exactly the
        // fixture classes, attributed into each package's fabricated
        // assemblies), the package-facts reader and the fingerprint seam,
        // which refuses the row's named assemblies the way an OW-OP-017
        // rejection does.
        private static RegistryBuildResult RebuildGate(GateRow row)
        {
            var provenanceByPackage =
                new Dictionary<string, UpmPackageProvenance>(StringComparer.Ordinal);
            var attribution = new Dictionary<Type, (string PackageId, string Assembly)>();
            var facts = new Dictionary<string, ExtensionPackageFacts>(StringComparer.Ordinal);
            var opTypes = new List<Type>();
            var providerTypes = new List<Type>();
            foreach (var pkg in row.Packages)
            {
                provenanceByPackage[pkg.PackageId] = new UpmPackageProvenance(
                    pkg.PackageId, "1.0.0", UpmPackageSource.Embedded, null, null, null);
                facts[pkg.PackageId] = new ExtensionPackageFacts(
                    pkg.Manifest, pkg.RootPath, pkg.Assemblies);
                foreach (var candidate in pkg.Operations)
                {
                    opTypes.Add(candidate.Type);
                    attribution[candidate.Type] = (pkg.PackageId, candidate.Assembly);
                }
                foreach (var candidate in pkg.Providers)
                {
                    providerTypes.Add(candidate.Type);
                    attribution[candidate.Type] = (pkg.PackageId, candidate.Assembly);
                }
            }
            var typeSeams = new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => opTypes,
                ProviderTypes: () => providerTypes,
                ResolveProvenance: type =>
                {
                    var resolved = EditorAssemblyResolver.ResolveForType(type);
                    if (resolved.Ok && attribution.TryGetValue(type, out var target))
                    {
                        var real = resolved.Value!;
                        resolved = Result<EditorAssemblyProvenance>.Success(
                            new EditorAssemblyProvenance(
                                target.Assembly, real.CompiledAssemblyPath,
                                real.IsPrecompiled, OperationOrigin.Custom,
                                provenanceByPackage[target.PackageId],
                                real.CompilationAssembly));
                    }
                    return resolved;
                },
                PackageFacts: id =>
                    facts.TryGetValue(id, out var found) ? found : null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());
            var refused = new HashSet<string>(row.RefusedAssemblies, StringComparer.Ordinal);
            var operationSeams = RegistryOperationStage.Production() with
            {
                ImplementationHash = provenance =>
                    refused.Contains(provenance.AssemblyName)
                        ? Result<DefiningAssemblyFingerprint>.Failure(ErrorCodes.Create(
                            ErrorCodes.OW_OP_017,
                            "Defining assembly's compiled bytes could not be read."))
                        : WeakFingerprintProvider.ForProvenance(provenance),
            };
            return RegistryBuildHost.Rebuild(
                typeSeams, operationSeams, new RegistryPublication());
        }

        [TestCaseSource(nameof(GateRows))]
        public void GateClassification_Row(GateRow row)
        {
            GateGoodOperation.s_ctorCalls = 0;
            GateGoodOperationB.s_ctorCalls = 0;
            GateVersionedOperation.s_ctorCalls = 0;
            GateGoodProvider.s_ctorCalls = 0;
            GateGoodProviderB.s_ctorCalls = 0;

            var result = RebuildGate(row);

            Assert.That(result.Stale, Is.False, row.Name);
            var snapshot = result.Snapshot;
            Assert.That(snapshot, Is.Not.Null, row.Name);

            foreach (var id in row.RegisteredOps)
            {
                Assert.That(snapshot!.Operations.Any(o => o.Descriptor.Id == id), Is.True,
                    $"{row.Name}: '{id}' must register");
            }
            foreach (var id in row.MissingOps)
            {
                Assert.That(snapshot!.Operations.Any(o => o.Descriptor.Id == id), Is.False,
                    $"{row.Name}: '{id}' must not register");
            }
            var providerIds = result.Providers.Select(p => p.ProviderId).ToList();
            foreach (var id in row.BoundProviders)
            {
                Assert.That(providerIds, Has.Some.EqualTo(id),
                    $"{row.Name}: provider '{id}' must stay bound");
            }
            foreach (var id in row.MissingProviders)
            {
                Assert.That(providerIds, Has.None.EqualTo(id),
                    $"{row.Name}: provider '{id}' must not register");
            }
            foreach (var id in row.ResolvableTypes)
            {
                Assert.That(result.Types!.Resolve(TR(id)).Ok, Is.True,
                    $"{row.Name}: type '{id}' must resolve");
            }
            foreach (var id in row.UnresolvableTypes)
            {
                Assert.That(result.Types!.Resolve(TR(id)).Ok, Is.False,
                    $"{row.Name}: type '{id}' must not resolve");
            }
            foreach (var (typeName, code) in row.OpFailures)
            {
                Assert.That(result.OperationFailures.Any(f =>
                        f.TypeFullName == typeName && f.Diagnostics.Any(d => d.Code == code)),
                    Is.True, $"{row.Name}: '{typeName}' must carry {code}");
            }
            foreach (var (typeName, code) in row.StageFails)
            {
                Assert.That(result.Failures.Any(f =>
                        f.TypeFullName == typeName && f.Diagnostics.Any(d => d.Code == code)),
                    Is.True, $"{row.Name}: '{typeName}' must carry a stage-1-5 failure ({code})");
            }
            foreach (var (kind, typeId) in row.TypeFails)
            {
                Assert.That(result.TypeFailures.Any(f =>
                        f.Kind == kind && (typeId is null || f.TypeId == typeId)),
                    Is.True, $"{row.Name}: the type build must record {kind} for '{typeId}'");
            }
            foreach (var (opId, code) in row.SnapFails)
            {
                Assert.That(snapshot!.Failures.Any(f =>
                        f.OperationId == opId && f.Diagnostics.Any(d => d.Code == code)),
                    Is.True, $"{row.Name}: '{opId}' must be refused with {code}");
            }

            var packageDiagnostics = result.Failures
                .Concat(result.ExtensionFailures)
                .SelectMany(f => f.Diagnostics)
                .ToList();
            if (row.PackageRejected)
            {
                foreach (var fragment in row.DiagnosticFragments)
                {
                    Assert.That(packageDiagnostics.Any(d =>
                            d.Message.Contains(fragment) || (d.Hint ?? "").Contains(fragment)),
                        Is.True, $"{row.Name}: a rejection diagnostic must contain '{fragment}'");
                }
                foreach (var code in row.DiagnosticCodes)
                {
                    Assert.That(packageDiagnostics.Any(d => d.Code == code), Is.True,
                        $"{row.Name}: a rejection diagnostic must carry {code}");
                }
                foreach (var pkg in row.Packages)
                {
                    foreach (var candidate in pkg.Operations.Concat(pkg.Providers))
                    {
                        var name = candidate.Type.FullName!;
                        Assert.That(
                            result.Failures.Concat(result.ExtensionFailures)
                                .Any(f => f.TypeFullName == name),
                            Is.True,
                            $"{row.Name}: '{name}' must carry the package's rejection");
                    }
                }
                if (row.ExpectNeverConstructed)
                {
                    Assert.That(GateGoodOperation.s_ctorCalls, Is.EqualTo(0), row.Name);
                    Assert.That(GateGoodOperationB.s_ctorCalls, Is.EqualTo(0), row.Name);
                    Assert.That(GateGoodProvider.s_ctorCalls, Is.EqualTo(0), row.Name);
                    Assert.That(GateGoodProviderB.s_ctorCalls, Is.EqualTo(0), row.Name);
                }
                else
                {
                    Assert.That(row.ConstructedProbe(), Is.GreaterThan(0),
                        $"{row.Name}: a post-construction rejection still constructs the package");
                }
            }
            else
            {
                Assert.That(result.ExtensionFailures, Is.Empty,
                    () => $"{row.Name}: the package must survive — " + string.Join("; ",
                        result.ExtensionFailures.SelectMany(
                            f => f.Diagnostics.Select(d => d.Message))));
            }
        }

        // ── SPEC §C3 error paragraph: the cross-package observed-Type
        //    duplicate verdict never publishes a malformed credential-
        //    bearing Type ID verbatim (T-W2.12-03 review correction
        //    rounds 4–6) ────────────────────────────────────────────

        // Two packages offering the same malformed credential-bearing
        // Type ID — the URL-userinfo form, the credential-query form and
        // the bare 'password=…' form behind no marker at all — still
        // reject both extensions with OW-OP-013 while no string the
        // published snapshot carries contains the secret. The IDs are
        // observed before validation, so the verdict publishes each
        // malformed ID's fixed redacted marker — identical on both sides
        // of the collision — and every other published field passes
        // through the credential mask.
        [Test]
        public void CrossPackageCredentialTypeDuplicate_PublishesRedactedDiagnostics()
        {
            var result = RebuildGate(new GateRow
            {
                Name = "credential-type-duplicate",
                Packages = new[]
                {
                    GatePkg("com.opweave.fixture.gate.creda", GateAsmA,
                        GateManifest("com.opweave.fixture.gate.creda",
                            assemblies: AsmList(GateAsmA),
                            typeProviders: J(ProvRef(CredentialProviderIdA, GateAsmA))),
                        operations: Array.Empty<GateCandidate>(),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateCredentialProviderA), GateAsmA),
                        }),
                    GatePkg("com.opweave.fixture.gate.credb", GateAsmB,
                        GateManifest("com.opweave.fixture.gate.credb",
                            assemblies: AsmList(GateAsmB),
                            typeProviders: J(ProvRef(CredentialProviderIdB, GateAsmB))),
                        operations: Array.Empty<GateCandidate>(),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateCredentialProviderB), GateAsmB),
                        }),
                    GatePkg("com.opweave.fixture.gate.credqa", GateAsmC,
                        GateManifest("com.opweave.fixture.gate.credqa",
                            assemblies: AsmList(GateAsmC),
                            typeProviders: J(ProvRef(QueryProviderIdA, GateAsmC))),
                        operations: Array.Empty<GateCandidate>(),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateCredentialQueryProviderA), GateAsmC),
                        }),
                    GatePkg("com.opweave.fixture.gate.credqb", GateAsmD,
                        GateManifest("com.opweave.fixture.gate.credqb",
                            assemblies: AsmList(GateAsmD),
                            typeProviders: J(ProvRef(QueryProviderIdB, GateAsmD))),
                        operations: Array.Empty<GateCandidate>(),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateCredentialQueryProviderB), GateAsmD),
                        }),
                    GatePkg("com.opweave.fixture.gate.credba", GateAsmE,
                        GateManifest("com.opweave.fixture.gate.credba",
                            assemblies: AsmList(GateAsmE),
                            typeProviders: J(ProvRef(BareProviderIdA, GateAsmE))),
                        operations: Array.Empty<GateCandidate>(),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateCredentialBareProviderA), GateAsmE),
                        }),
                    GatePkg("com.opweave.fixture.gate.credbb", GateAsmF,
                        GateManifest("com.opweave.fixture.gate.credbb",
                            assemblies: AsmList(GateAsmF),
                            typeProviders: J(ProvRef(BareProviderIdB, GateAsmF))),
                        operations: Array.Empty<GateCandidate>(),
                        providers: new[]
                        {
                            new GateCandidate(typeof(GateCredentialBareProviderB), GateAsmF),
                        }),
                },
            });

            Assert.That(result.Stale, Is.False);
            var snapshot = result.Snapshot;
            Assert.That(snapshot, Is.Not.Null);

            // All three pairs still produce the cross-package OW-OP-013
            // duplicate verdict — the codes and counts are unchanged.
            var verdicts = snapshot!.CandidateFailures
                .Where(f => f.Diagnostics.Any(d =>
                    d.Code == ErrorCodes.OW_OP_013 &&
                    d.Message.Contains("more than one package")))
                .ToList();
            foreach (var providerType in new[]
            {
                typeof(GateCredentialProviderA), typeof(GateCredentialProviderB),
                typeof(GateCredentialQueryProviderA), typeof(GateCredentialQueryProviderB),
                typeof(GateCredentialBareProviderA), typeof(GateCredentialBareProviderB),
            })
            {
                Assert.That(
                    verdicts.Any(f => f.CandidateId == providerType.FullName),
                    Is.True,
                    $"{providerType.Name} must carry the OW-OP-013 duplicate verdict");
            }

            // The published verdicts carry the fixed redacted marker —
            // the same placeholder on both sides of every collision —
            // never a masked or verbatim piece of the raw malformed ID.
            var messages = verdicts
                .SelectMany(f => f.Diagnostics)
                .Select(d => d.Message)
                .ToList();
            foreach (var id in new[]
            {
                CredentialTypeId, QueryCredentialTypeId, BareCredentialTypeId,
            })
            {
                var redacted = TypeRegistryBuilder.RedactedTypeId(id);
                Assert.AreEqual(2, messages.Count(m => m.Contains(redacted)),
                    $"both sides of the collision publish the same placeholder: {redacted}");
            }
            Assert.IsFalse(messages.Any(m => m.Contains("***")),
                "the placeholder carries no part of the raw text");
            AssertNoPublishedStringContains(snapshot!, CredentialSecret, "user:", "secret-value");
        }

        // ── Shared assertions ─────────────────────────────────────────

        // Every string property the snapshot publishes for rejected
        // candidates and diagnostics — the three failure lists, walked by
        // reflection so a string field added to a published record or to
        // Diagnostic later is covered automatically.
        private static IReadOnlyList<string> PublishedDiagnosticStrings(
            RegistrySnapshot snapshot)
        {
            var texts = new List<string>();
            void Collect(object? record)
            {
                if (record is null)
                {
                    return;
                }
                foreach (var property in record.GetType().GetProperties(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }
                    var value = property.GetValue(record);
                    if (value is string text)
                    {
                        texts.Add(text);
                    }
                    else if (value is System.Collections.IEnumerable items)
                    {
                        foreach (var item in items)
                        {
                            Collect(item);
                        }
                    }
                }
            }
            foreach (var failure in snapshot.CandidateFailures)
            {
                Collect(failure);
            }
            foreach (var failure in snapshot.TypeFailures)
            {
                Collect(failure);
            }
            foreach (var failure in snapshot.Failures)
            {
                Collect(failure);
            }
            return texts;
        }

        private static void AssertNoPublishedStringContains(
            RegistrySnapshot snapshot, params string[] secrets)
        {
            foreach (var text in PublishedDiagnosticStrings(snapshot))
            {
                foreach (var secret in secrets)
                {
                    Assert.IsFalse(text.Contains(secret),
                        $"published text leaks '{secret}': {text}");
                }
            }
        }
    }
}
