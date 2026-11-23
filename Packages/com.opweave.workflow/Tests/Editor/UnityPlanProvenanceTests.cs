#nullable enable
// T-W2.2-06 [Edit] tests: the Unity-side IPlanProvenance producer — the six
// SG-11 environment identifiers and the SG-11 Codec identity object feeding
// weak fingerprints and planHash (SPEC §0.6 SG-11, §C0.2 weak, §D0.1, §X.8).
//
// local:T-W2.2-06:environment-identifiers — Codec IDs and complete six-field
// environment identifier snapshots are generated for unchanged / changed /
// missing synthetic values and passed to the Core planHash with stable fixed
// golden bytes. Pass conditions:
//   - same inputs produce identical Codec and plan hashes,
//   - each single-field mutation changes the used environment hash,
//   - absent identifiers reject, and
//   - no Unity-only API leaks into Core.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.2-06:environment-identifiers — exact six-field environment
    /// snapshots, SG-11 Codec identity atoms and SHA-256, planHash stability
    /// and per-field invalidation, and fail-closed absent identifiers.
    /// </summary>
    internal sealed class UnityPlanProvenanceTests
    {
        private const string TestAssemblyName = "OpWeave.UnityAdapter.Tests";

        // The fixed synthetic SG-11 environment the plan fixtures use — the
        // same field values as the Core PlanProvenanceFixtures environment so
        // the planHash input is machine-independent.
        private static readonly PlanEnvironment s_syntheticEnvironment = new PlanEnvironment(
            "6000.0.85f1",
            "Windows",
            "StandaloneWindows64",
            "Mono2x",
            "NET_Standard",
            new[] { "DEBUG", "TRACE", "UNITY_EDITOR" });

        // ── Fixtures ─────────────────────────────────────────────────────────

        private sealed class ProbeCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) => value;
        }

        private static TypeDefinition ProbeDefinition() => new TypeDefinition(
            "proj.probe.scalar",
            TypeKind.Primitive,
            0,
            new[] { "core.String" },
            null,
            "Probe scalar.",
            new ProbeCodec());

        private sealed class ProbeProvider : ITypeProvider
        {
            private readonly IReadOnlyList<TypeDefinition> _types;

            internal ProbeProvider(params TypeDefinition[] types) => _types = types;

            public IEnumerable<TypeDefinition> GetTypes() => _types;
        }

        private sealed class UnityShapesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        private sealed class StubOperation : IOperation
        {
            internal StubOperation(OperationDescriptor descriptor) => Descriptor = descriptor;
            public OperationDescriptor Descriptor { get; }
            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();
            public PreflightResult Preflight(StepContext ctx) => throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) => throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) => throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

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
            public override T Get<T>(string port) => throw new NotSupportedException();
        }

        private sealed class MapTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => true;
        }

        // The same OW-OP-017 EditorAssemblyResolver produces for a foreign
        // or byte-loaded DLL, forced through the injected resolver seam —
        // Unity Mono cannot emit the DispatchProxy types dotnet uses for a
        // genuinely unresolvable runtime type.
        private static Result<EditorAssemblyProvenance> FailedAssemblyResolution(Type type) =>
            Result<EditorAssemblyProvenance>.Failure(ErrorCodes.Create(
                ErrorCodes.OW_OP_017,
                "Defining assembly could not be resolved to a unique Unity compilation assembly."));

        // A provenance over fully synthetic identifiers, mirroring the [Core]
        // PlanProvenanceFixtures.Stub answers atom for atom so the SG-12 hash
        // input is identical on both runtimes: the same planHash proves the
        // Unity canonicalization and SHA-256 end to end (SPEC §0.6 SG-11/12).
        private sealed class SyntheticProvenance : IPlanProvenance
        {
            private readonly Result<PlanEnvironment> _environment;

            internal SyntheticProvenance(Result<PlanEnvironment> environment) =>
                _environment = environment;

            public IntegrityLevel IntegrityLevel => IntegrityLevel.Weak;

            public Result<PlanEnvironment> Environment => _environment;

            public Result<PlanTypeFingerprint> TypeFingerprintFor(TypeDefinition definition)
            {
                using var sha256 = SHA256.Create();
                var digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(definition.Id));
                var hex = BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
                return Result<PlanTypeFingerprint>.Success(new PlanTypeFingerprint(
                    "test.provider",
                    new string('1', 64),
                    "Test.Types",
                    hex));
            }

            public Result<PlanCodecIdentity> CodecIdentityFor(TypeDefinition definition) =>
                Result<PlanCodecIdentity>.Success(new PlanCodecIdentity(
                    "test.provider",
                    "Test.Codecs." + definition.Id.Replace("<", "Of").Replace(">", string.Empty),
                    "Test.Codecs",
                    new string('2', 64)));
        }

        private static readonly TypeProviderCandidate[] s_providers =
        {
            new TypeProviderCandidate(
                CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
            new TypeProviderCandidate(
                "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
            new TypeProviderCandidate(
                "proj.probe", new ProbeProvider(ProbeDefinition()), OperationOrigin.Custom),
        };

        private static readonly ITypeRegistry s_types = BuildTypes();

        private static ITypeRegistry BuildTypes()
        {
            var build = TypeRegistryBuilder.Build(s_providers);
            Assert.AreEqual(0, build.Failures.Count,
                "registry build failed: " + (build.Failures.Count > 0 ? build.Failures[0].Detail : string.Empty));
            return build.Registry;
        }

        private static Result<PlanEnvironment> Env(PlanEnvironment environment) =>
            Result<PlanEnvironment>.Success(environment);

        private static UnityPlanProvenance Provenance(
            Result<PlanEnvironment> environment,
            IntegrityLevel level = IntegrityLevel.Weak,
            IEnumerable<TypeProviderCandidate>? providers = null,
            Func<Type, Result<EditorAssemblyProvenance>>? assemblyResolver = null) =>
            new UnityPlanProvenance(level, environment, providers ?? s_providers, assemblyResolver);

        private static TypeDefinition Resolve(string typeId)
        {
            var parsed = TypeRef.Parse(typeId);
            Assert.That(parsed.Ok, Is.True, $"type id '{typeId}' must parse");
            var resolved = s_types.Resolve(parsed.Value!);
            Assert.That(resolved.Ok, Is.True, $"type '{typeId}' must resolve");
            return resolved.Value!;
        }

        // The SG-11 Codec identity object: SHA-256 of the canonical JSON is
        // the Codec ID (SPEC §0.6 SG-11) — assembled the same way PlanBuilder
        // assembles it for usedCodecFingerprints.
        private static JObject CodecIdentityObject(string typeId, PlanCodecIdentity identity) =>
            new JObject
            {
                ["typeId"] = typeId,
                ["providerId"] = identity.ProviderId,
                ["codecTypeFullName"] = identity.CodecTypeFullName,
                ["definingAssemblyName"] = identity.DefiningAssemblyName,
                ["definingAssemblySha256"] = identity.DefiningAssemblySha256,
                ["coreApiVersion"] = CoreApi.Version,
            };

        private static string ExpectedOsName() => SystemInfo.operatingSystemFamily switch
        {
            OperatingSystemFamily.Windows => "Windows",
            OperatingSystemFamily.MacOSX => "macOS",
            OperatingSystemFamily.Linux => "Linux",
            _ => "<unexpected>",
        };

        // ── Environment identifiers ──────────────────────────────────────────

        // SPEC §0.6 SG-11: all six environment fields are the exact live
        // Editor values — never defaults or guesses.
        [Test]
        public void Capture_SixFieldsMatchLiveEditor()
        {
            var result = UnityEnvironmentSnapshot.Capture();

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var env = result.Value!;
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            var named = NamedBuildTarget.FromBuildTargetGroup(group);
            PlayerSettings.GetScriptingDefineSymbols(named, out var expectedDefines);
            var sortedDefines = expectedDefines
                .Distinct()
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToArray();

            Assert.That(env.UnityVersion, Is.EqualTo(Application.unityVersion));
            Assert.That(env.EditorOS, Is.EqualTo(ExpectedOsName()));
            Assert.That(env.ActiveBuildTarget,
                Is.EqualTo(EditorUserBuildSettings.activeBuildTarget.ToString()));
            Assert.That(env.ScriptingBackend,
                Is.EqualTo(PlayerSettings.GetScriptingBackend(named).ToString()));
            Assert.That(env.ApiCompatibilityLevel,
                Is.EqualTo(PlayerSettings.GetApiCompatibilityLevel(named).ToString()));
            Assert.That(env.ScriptingDefineSymbols, Is.EqualTo(sortedDefines),
                "defines must be deduplicated and Ordinal-sorted (SPEC §0.6 SG-11)");
        }

        [Test]
        public void Capture_RepeatedCalls_ProduceIdenticalSnapshot()
        {
            var first = UnityEnvironmentSnapshot.Capture();
            var second = UnityEnvironmentSnapshot.Capture();

            Assert.That(first.Ok && second.Ok, Is.True);
            var a = first.Value!;
            var b = second.Value!;
            Assert.That(b.UnityVersion, Is.EqualTo(a.UnityVersion));
            Assert.That(b.EditorOS, Is.EqualTo(a.EditorOS));
            Assert.That(b.ActiveBuildTarget, Is.EqualTo(a.ActiveBuildTarget));
            Assert.That(b.ScriptingBackend, Is.EqualTo(a.ScriptingBackend));
            Assert.That(b.ApiCompatibilityLevel, Is.EqualTo(a.ApiCompatibilityLevel));
            Assert.That(b.ScriptingDefineSymbols, Is.EqualTo(a.ScriptingDefineSymbols));
        }

        // SPEC §0.6 SG-11 / OW-OP-020: a define-symbol query that throws or
        // answers null is an unobtainable required identifier — a failure
        // is never substituted with the empty list. Exercised through the
        // injected query seam on the live capture path.
        [Test]
        public void Capture_DefineSymbolsQueryThrowsOrNull_Fails020()
        {
            var thrown = UnityEnvironmentSnapshot.Capture(
                _ => throw new InvalidOperationException("forced"));
            var nullAnswer = UnityEnvironmentSnapshot.Capture(_ => (string[]?)null);

            foreach (var result in new[] { thrown, nullAnswer })
            {
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Diagnostics.Select(d => d.Code),
                    Has.All.EqualTo(ErrorCodes.OW_OP_020));
                Assert.That(result.Diagnostics.Any(
                    d => d.Message.Contains("scriptingDefineSymbols")), Is.True,
                    "the failed query must be reported as scriptingDefineSymbols");
            }
        }

        // A successfully queried define list may be empty — a project
        // legitimately has no defines (SPEC §0.6 SG-11).
        [Test]
        public void Capture_DefineSymbolsQueryEmpty_Succeeds()
        {
            var result = UnityEnvironmentSnapshot.Capture(_ => Array.Empty<string>());

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(result.Value!.ScriptingDefineSymbols, Is.Empty);
        }

        // SPEC §0.6 SG-11: absent environment values fail closed — the record
        // refuses empty required fields and a failed snapshot Result is
        // carried verbatim (never an empty-string substitution).
        [Test]
        public void Environment_AbsentValuesFailClosed()
        {
            Assert.Throws<ArgumentException>(() =>
                new PlanEnvironment("", "Windows", "StandaloneWindows64", "Mono2x",
                    "NET_Standard", Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() =>
                new PlanEnvironment("6000.0.85f1", "", "StandaloneWindows64", "Mono2x",
                    "NET_Standard", Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() =>
                new PlanEnvironment("6000.0.85f1", "Windows", "", "Mono2x",
                    "NET_Standard", Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() =>
                new PlanEnvironment("6000.0.85f1", "Windows", "StandaloneWindows64", "",
                    "NET_Standard", Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() =>
                new PlanEnvironment("6000.0.85f1", "Windows", "StandaloneWindows64", "Mono2x",
                    "", Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() =>
                new PlanEnvironment("6000.0.85f1", "Windows", "StandaloneWindows64", "Mono2x",
                    "NET_Standard", new[] { "DEBUG", "" }));
        }

        // ── Codec identity (SPEC §0.6 SG-11) ─────────────────────────────────

        // The exact identity atoms of a built-in Codec: @builtin provider, the
        // namespace-qualified CLR FullName, and the compiled bytes of
        // OpWeave.Core — the Codec's own defining assembly.
        [Test]
        public void CodecIdentity_BuiltinCodec_ExactAtoms()
        {
            var provenance = Provenance(Env(s_syntheticEnvironment));
            var definition = Resolve("core.String");

            var result = provenance.CodecIdentityFor(definition);

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var identity = result.Value!;
            var expectedAssembly = WeakFingerprintProvider.ForDefiningType(
                definition.Codec.GetType());
            Assert.That(expectedAssembly.Ok, Is.True);
            Assert.That(identity.ProviderId, Is.EqualTo("@builtin"));
            // The namespace-qualified CLR FullName — never an
            // AssemblyQualifiedName's version data (SPEC §0.6 SG-11).
            Assert.That(identity.CodecTypeFullName, Is.EqualTo("OpWeave.Core.StringCodec"));
            Assert.That(identity.CodecTypeFullName, Is.EqualTo(definition.Codec.GetType().FullName));
            Assert.That(identity.DefiningAssemblyName,
                Is.EqualTo(expectedAssembly.Value!.DefiningAssemblyName));
            Assert.That(identity.DefiningAssemblySha256,
                Is.EqualTo(expectedAssembly.Value!.DefiningAssemblySha256));
        }

        // The Codec's defining assembly may differ from the registering
        // provider's (SPEC §0.6 SG-11): unity.Vector3 is offered by the test
        // provider while its Codec lives in OpWeave.Core.
        [Test]
        public void CodecIdentity_CodecAssemblyMayDifferFromProviderAssembly()
        {
            var provenance = Provenance(Env(s_syntheticEnvironment));
            var definition = Resolve("unity.Vector3");

            var codecResult = provenance.CodecIdentityFor(definition);
            var typeResult = provenance.TypeFingerprintFor(definition);

            Assert.That(codecResult.Ok && typeResult.Ok, Is.True,
                () => string.Join("; ", codecResult.Diagnostics.Concat(
                    typeResult.Diagnostics).Select(d => d.Code + " " + d.Message)));
            Assert.That(codecResult.Value!.ProviderId, Is.EqualTo("opweave.unity-shapes"));
            Assert.That(codecResult.Value!.CodecTypeFullName,
                Is.EqualTo("OpWeave.Core.UnityVectorCodec"));
            Assert.That(codecResult.Value!.CodecTypeFullName,
                Is.EqualTo(definition.Codec.GetType().FullName));
            Assert.That(codecResult.Value!.DefiningAssemblyName, Is.EqualTo("OpWeave.Core"));
            Assert.That(typeResult.Value!.DefiningAssemblyName, Is.EqualTo(TestAssemblyName),
                "the type fingerprint binds the provider's defining assembly");
            Assert.That(codecResult.Value!.DefiningAssemblyName,
                Is.Not.EqualTo(typeResult.Value!.DefiningAssemblyName));
        }

        // The Codec ID is the SHA-256 of the canonical identity object with
        // exactly the six SG-11 fields (SPEC §0.6 SG-11, §0.3).
        [Test]
        public void CodecId_CanonicalIdentity_Sha256Golden()
        {
            var golden = new JObject
            {
                ["typeId"] = "proj.probe",
                ["providerId"] = "proj.provider",
                ["codecTypeFullName"] = "Probe.Codec",
                ["definingAssemblyName"] = "Probe.Codecs",
                ["definingAssemblySha256"] = new string('a', 64),
                ["coreApiVersion"] = "1.0.0",
            };

            var canonical = JsonValue.From(golden).CanonicalText;
            Assert.That(canonical, Is.EqualTo(
                "{\"codecTypeFullName\":\"Probe.Codec\",\"coreApiVersion\":\"1.0.0\"," +
                "\"definingAssemblyName\":\"Probe.Codecs\",\"definingAssemblySha256\":\"" +
                new string('a', 64) +
                "\",\"providerId\":\"proj.provider\",\"typeId\":\"proj.probe\"}"));
            Assert.That(CanonicalJson.Sha256(golden), Is.EqualTo(
                "e48d39e3f9418b9894601121a7c0f41108b984605f50568f6af4334eacbccb10"));
        }

        [Test]
        public void CodecId_SameInputs_StableAcrossRecomputation()
        {
            var provenance = Provenance(Env(s_syntheticEnvironment));
            var definition = Resolve("core.String");

            var first = provenance.CodecIdentityFor(definition);
            var second = provenance.CodecIdentityFor(definition);

            Assert.That(first.Ok && second.Ok, Is.True);
            var firstId = CanonicalJson.Sha256(CodecIdentityObject("core.String", first.Value!));
            var secondId = CanonicalJson.Sha256(CodecIdentityObject("core.String", second.Value!));
            Assert.That(firstId, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(secondId, Is.EqualTo(firstId));
        }

        // Each single-field mutation of the identity object changes the Codec
        // ID — the same "one field invalidates" rule the Plan applies.
        [Test]
        public void CodecId_EachSingleFieldMutation_ChangesHash()
        {
            var provenance = Provenance(Env(s_syntheticEnvironment));
            var definition = Resolve("core.String");
            var identity = provenance.CodecIdentityFor(definition).Value!;

            var baseline = CanonicalJson.Sha256(CodecIdentityObject("core.String", identity));
            var variants = new[]
            {
                CodecIdentityObject("core.Float", identity),
                CodecIdentityObject("core.String",
                    identity with { ProviderId = "other.provider" }),
                CodecIdentityObject("core.String",
                    identity with { CodecTypeFullName = "Other.Codec" }),
                CodecIdentityObject("core.String",
                    identity with { DefiningAssemblyName = "Other.Assembly" }),
                CodecIdentityObject("core.String",
                    identity with { DefiningAssemblySha256 = new string('b', 64) }),
            };
            foreach (var variant in variants)
            {
                Assert.That(CanonicalJson.Sha256(variant), Is.Not.EqualTo(baseline));
            }
        }

        // ── Type fingerprint atoms ───────────────────────────────────────────

        [Test]
        public void TypeFingerprint_BuiltinType_ExactAtoms()
        {
            var provenance = Provenance(Env(s_syntheticEnvironment));
            var definition = Resolve("core.String");

            var result = provenance.TypeFingerprintFor(definition);

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var fingerprint = result.Value!;
            var expectedAssembly = WeakFingerprintProvider.ForDefiningType(typeof(CoreTypeProvider));
            Assert.That(expectedAssembly.Ok, Is.True);
            Assert.That(fingerprint.ProviderId, Is.EqualTo("@builtin"));
            Assert.That(fingerprint.TypeDefinitionHash,
                Is.EqualTo(TypeDefinitionHash.Compute(definition)));
            Assert.That(fingerprint.DefiningAssemblyName,
                Is.EqualTo(expectedAssembly.Value!.DefiningAssemblyName));
            Assert.That(fingerprint.DefiningAssemblySha256,
                Is.EqualTo(expectedAssembly.Value!.DefiningAssemblySha256));
        }

        // The typeDefinitionHash canonical projection is frozen by golden
        // bytes (local choice documented on TypeDefinitionHash).
        [Test]
        public void TypeDefinitionHash_CanonicalProjection_Golden()
        {
            var definition = ProbeDefinition();

            var canonical = TypeDefinitionHash.ToJsonValue(definition).CanonicalText;
            Assert.That(canonical, Is.EqualTo(
                "{\"baseTypes\":[\"core.String\"],\"description\":\"Probe scalar.\"," +
                "\"genericArity\":0,\"genericConstraint\":null,\"kind\":\"primitive\"," +
                "\"typeId\":\"proj.probe.scalar\"}"));
            Assert.That(TypeDefinitionHash.Compute(definition), Is.EqualTo(
                "7d5e01f9d3a5291b77309b00d962f96e254a0a8fb3d1d81e4af2f2a5f924940c"));

            var withSchema = definition with { Schema = JsonValue.Parse("{\"type\":\"string\"}").Value! };
            Assert.That(TypeDefinitionHash.ToJsonValue(withSchema).CanonicalText, Is.EqualTo(
                "{\"baseTypes\":[\"core.String\"],\"description\":\"Probe scalar.\"," +
                "\"genericArity\":0,\"genericConstraint\":null,\"kind\":\"primitive\"," +
                "\"schema\":{\"type\":\"string\"},\"typeId\":\"proj.probe.scalar\"}"));
            Assert.That(TypeDefinitionHash.Compute(withSchema),
                Is.Not.EqualTo(TypeDefinitionHash.Compute(definition)));
        }

        // ── Fail-closed identifier resolution ────────────────────────────────

        // A Type ID no candidate registered and a Type ID two candidates
        // offered are never guessed (SPEC §0.6 SG-11): both fail OW-OP-017.
        [Test]
        public void Fingerprints_UnboundAndAmbiguousTypes_Fail017()
        {
            var duplicate = new TypeDefinition(
                "proj.dup.type", TypeKind.Primitive, 0,
                Array.Empty<string>(), null, "Dup.", new ProbeCodec());
            var unbound = new TypeDefinition(
                "proj.unbound.type", TypeKind.Primitive, 0,
                Array.Empty<string>(), null, "Unbound.", new ProbeCodec());
            var providers = new[]
            {
                new TypeProviderCandidate(
                    "proj.dup-a", new ProbeProvider(duplicate), OperationOrigin.Custom),
                new TypeProviderCandidate(
                    "proj.dup-b", new ProbeProvider(duplicate), OperationOrigin.Custom),
            };
            var provenance = Provenance(Env(s_syntheticEnvironment), providers: providers);

            foreach (var result in new[]
            {
                provenance.TypeFingerprintFor(duplicate).Diagnostics,
                provenance.CodecIdentityFor(duplicate).Diagnostics,
                provenance.TypeFingerprintFor(unbound).Diagnostics,
                provenance.CodecIdentityFor(unbound).Diagnostics,
            })
            {
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].Code, Is.EqualTo(ErrorCodes.OW_OP_017));
            }
        }

        // SPEC §C3 step 4: duplicate Provider IDs reject every candidate of
        // both providers before collection, so their offers never reach the
        // bindings — a registered type offered again by the rejected pair
        // stays bound to the valid provider, fingerprints and feeds a Plan.
        [Test]
        public void Fingerprints_DuplicateProviderIds_RegisteredTypeStillBinds()
        {
            var impostorString = new TypeDefinition(
                "core.String", TypeKind.Primitive, 0,
                Array.Empty<string>(), null, "Impostor String.", new ProbeCodec());
            var rejectedOnly = new TypeDefinition(
                "proj.dup.only", TypeKind.Primitive, 0,
                Array.Empty<string>(), null, "Rejected offer.", new ProbeCodec());
            var providers = new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "proj.dup", new ProbeProvider(impostorString, rejectedOnly), OperationOrigin.Custom),
                new TypeProviderCandidate(
                    "proj.dup", new ProbeProvider(impostorString), OperationOrigin.Custom),
            };
            var build = TypeRegistryBuilder.Build(providers);
            Assert.That(
                build.Failures.Count(f => f.Kind == TypeRegistryFailureKind.DuplicateProviderId),
                Is.EqualTo(2), "both duplicate-ID candidates must be rejected");
            Assert.That(build.Registry.All.Any(d => d.Id == "core.String"), Is.True,
                "the valid provider's core.String must stay registered");

            var provenance = Provenance(Env(s_syntheticEnvironment), providers: providers);
            var definition = Resolve("core.String");

            var typeResult = provenance.TypeFingerprintFor(definition);
            var codecResult = provenance.CodecIdentityFor(definition);
            Assert.That(typeResult.Ok && codecResult.Ok, Is.True,
                () => string.Join("; ", typeResult.Diagnostics.Concat(
                    codecResult.Diagnostics).Select(d => d.Code + " " + d.Message)));
            Assert.That(typeResult.Value!.ProviderId, Is.EqualTo("@builtin"));
            Assert.That(codecResult.Value!.ProviderId, Is.EqualTo("@builtin"));

            // An offer only the rejected pair made was never registered: it
            // stays unbound and fails closed with OW-OP-017.
            var unbound = provenance.TypeFingerprintFor(rejectedOnly);
            Assert.That(unbound.Ok, Is.False);
            Assert.That(unbound.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_017));

            // The registered type still feeds a Plan — the accepted binding
            // produces the identical fingerprint atoms, hence the identical
            // planHash, as the unpolluted provider set.
            var polluted = PlanHashOf(Compile(provenance));
            var clean = PlanHashOf(Compile(Provenance(Env(s_syntheticEnvironment))));
            Assert.That(polluted, Is.EqualTo(clean));
        }

        // A Codec or provider whose defining assembly cannot be resolved
        // fails OW-OP-017 — the diagnostic is propagated verbatim
        // (SPEC §C0.2 weak: unresolvable defining assemblies are refused).
        // The injected resolver seam forces the same failure a foreign or
        // byte-loaded DLL produces through the live resolver.
        [Test]
        public void Fingerprints_UnresolvableAssemblies_Fail017()
        {
            var bound = ProbeDefinition();
            var provenance = Provenance(
                Env(s_syntheticEnvironment),
                assemblyResolver: FailedAssemblyResolution);

            var codecResult = provenance.CodecIdentityFor(bound);
            Assert.That(codecResult.Ok, Is.False);
            Assert.That(codecResult.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_017));

            var typeResult = provenance.TypeFingerprintFor(bound);
            Assert.That(typeResult.Ok, Is.False);
            Assert.That(typeResult.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_017));
        }

        // A Codec that is missing entirely is OW-OP-016 (SPEC §C3 step 4).
        [Test]
        public void CodecIdentity_MissingCodec_Fails016()
        {
            var provenance = Provenance(Env(s_syntheticEnvironment));
            var codecless = ProbeDefinition() with { Codec = null! };

            var result = provenance.CodecIdentityFor(codecless);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_016));
        }

        // ── planHash end to end ──────────────────────────────────────────────

        private static OperationCandidate Candidate(OperationDescriptor descriptor) =>
            new OperationCandidate(
                descriptor,
                OperationOrigin.Standard,
                "Acme.Editor",
                "Assets/Acme/Operation.cs",
                new string('a', 64),
                HasPollImplementation: true,
                Implementation: new StubOperation(descriptor));

        private static OperationDescriptor StartDescriptor() =>
            Op.Define("core.start", 1).Summary("Workflow entry point.").Build();

        private static OperationDescriptor PlaceDescriptor() =>
            Op.Define("example.place", 1)
                .Summary("Place an object at a position.")
                .Input("position", "unity.Vector3", required: true)
                .Input("note", "core.String", required: false)
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

        private static RegistrySnapshot Snapshot() =>
            RegistrySnapshotBuilder.Build(
                new[] { Candidate(StartDescriptor()), Candidate(PlaceDescriptor()) }, s_types);

        private static WorkflowDocument Document()
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.provenance",
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData
                    {
                        Id = "a",
                        Operation = "example.place",
                        Version = 1,
                        Parameters = new Dictionary<string, JToken>
                        {
                            ["position"] = JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"),
                        },
                    },
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "a") },
            });
            Assert.That(created.Ok, Is.True,
                "document must create: " + string.Join(
                    "; ", created.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return created.Value!;
        }

        private static ValidationReport Compile(
            IPlanProvenance provenance) =>
            new WorkflowCompiler(
                    Snapshot(),
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    new MapTypeProbe(),
                    provenance)
                .Validate(new ValidationRequest(
                    Document(), null, ValidationMode.Static, ActorKind.Agent));

        private static string PlanHashOf(ValidationReport report)
        {
            Assert.That(report.Ok, Is.True,
                "validation failed: " + string.Join("; ", report.Errors.Select(d => d.Code)));
            Assert.That(report.Plan, Is.Not.Null);
            return report.Plan!.PlanHash;
        }

        // SPEC §D0.1: plans compiled in the same environment keep their
        // planHash — identical inputs produce identical Codec and plan hashes.
        [Test]
        public void Plan_SameEnvironment_SamePlanHash()
        {
            var first = PlanHashOf(Compile(Provenance(Env(s_syntheticEnvironment))));
            var second = PlanHashOf(Compile(Provenance(Env(s_syntheticEnvironment))));

            Assert.That(second, Is.EqualTo(first));
        }

        // SPEC §0.6 SG-11: changing any of the six keys invalidates the
        // environment-dependent Plan — each single-field mutation changes the
        // used environment hash.
        [Test]
        public void Plan_EachEnvironmentFieldMutation_InvalidatesPlan()
        {
            var baseline = PlanHashOf(Compile(Provenance(Env(s_syntheticEnvironment))));
            var env = s_syntheticEnvironment;
            var variants = new Dictionary<string, PlanEnvironment>
            {
                ["unityVersion"] = env with { UnityVersion = "6000.1.0f1" },
                ["editorOS"] = env with { EditorOS = "Linux" },
                ["activeBuildTarget"] = env with { ActiveBuildTarget = "Android" },
                ["scriptingBackend"] = env with { ScriptingBackend = "IL2CPP" },
                ["apiCompatibilityLevel"] = env with { ApiCompatibilityLevel = "NET_Framework" },
                ["scriptingDefineSymbols"] = env with { ScriptingDefineSymbols = new[] { "DEBUG" } },
            };
            foreach (var pair in variants)
            {
                var mutated = PlanHashOf(Compile(Provenance(Env(pair.Value))));
                Assert.That(mutated, Is.Not.EqualTo(baseline),
                    $"changing {pair.Key} must invalidate the Plan");
            }
        }

        // SPEC §D0.1 / §0.6 SG-12: a fixed golden planHash over fully
        // synthetic identifiers — the environment, the fingerprint atoms and
        // the document are all controlled, so the frozen digest pins the
        // canonical input end to end under the Unity runtime. The matching
        // canonical PlanHashInput bytes stay Core-internal and are frozen by
        // the [Core] golden hash-input vector.
        [Test]
        public void Plan_SyntheticProvenance_GoldenPlanHash()
        {
            var planHash = PlanHashOf(
                Compile(new SyntheticProvenance(Env(s_syntheticEnvironment))));

            Assert.That(planHash, Is.EqualTo(GoldenPlanHash));
        }

        private const string GoldenPlanHash =
            "f0ed8a475329820283213c13ad16593b5d0fcdcd1e9390735c572be967f573fe";

        // SPEC §0.6 SG-11 / OW-OP-020: an absent required identifier rejects
        // the Plan — the failed Result is reported verbatim, never a guessed
        // value.
        [Test]
        public void Plan_AbsentEnvironment_RejectsWithoutPlan()
        {
            var provenance = Provenance(Result<PlanEnvironment>.Failure(
                ErrorCodes.Create(
                    ErrorCodes.OW_OP_020,
                    "A required Unity environment identifier could not be obtained: unityVersion.")));

            var report = Compile(provenance);

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            var error = report.Errors.Single(d => d.Code == ErrorCodes.OW_OP_020);
            Assert.That(error.Message, Does.Contain("unityVersion"));
        }

        // SPEC §0.1 F-05: the configured IntegrityLevel=full rejects before any
        // Plan — never downgraded to weak material.
        [Test]
        public void Plan_IntegrityLevelFull_Rejected011WithoutPlan()
        {
            var provenance = Provenance(
                Env(s_syntheticEnvironment), level: IntegrityLevel.Full);

            var report = Compile(provenance);

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(report.Errors.Any(d => d.Code == ErrorCodes.OW_OP_011), Is.True);
            Assert.That(provenance.IntegrityLevel, Is.EqualTo(IntegrityLevel.Full));
        }

        // ── Assembly boundary ────────────────────────────────────────────────

        // SPEC §0.5 invariant: no Unity-only API leaks into Core — the
        // compiled OpWeave.Core binary must reference no UnityEngine/UnityEditor
        // assembly, checked on its own metadata reference table (the §H2
        // item-7 approach).
        [Test]
        public void Core_AssemblyReferences_NoUnityApis()
        {
            var core = AssemblyReferenceInspector.FindAssembly("OpWeave.Core");
            Assert.That(core, Is.Not.Null, "OpWeave.Core is not compiled");

            var unityReferences = AssemblyReferenceInspector
                .ReferencedAssemblyNames(core!)
                .Where(name => name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    name.StartsWith("UnityEditor", StringComparison.Ordinal))
                .ToList();
            Assert.That(unityReferences, Is.Empty,
                "OpWeave.Core must not reference Unity APIs: " + string.Join(", ", unityReferences));
        }
    }
}
