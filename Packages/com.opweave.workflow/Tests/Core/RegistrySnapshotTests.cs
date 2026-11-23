#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §C3 RegistrySnapshot model, the candidate
    /// exclusion rules and the fixed-input <c>registryHash</c> (T-W1.5-04):
    /// duplicate (id, version) excludes every candidate with OW-OP-004 while
    /// unrelated Operations remain (SPEC §C3 test 2), reserved namespaces from
    /// a Custom origin fail with OW-OP-007, a Waitable Operation without a
    /// Poll implementation and an Operation depending on an invalidated type
    /// fail with OW-OP-003, and the same content yields the same registryHash
    /// across order, generation, sourceFile and origin variants while any
    /// id/version/descriptorHash/implementationHash change alters it (SPEC §C3
    /// test 6).
    /// </summary>
    internal sealed class RegistrySnapshotTests
    {
        private static readonly ITypeRegistry s_types = DescriptorTestFixtures.CreateRegistry();

        private static OperationDescriptor Describe(
            string id, int version, string summary = "A test operation.") =>
            Op.Define(id, version)
                .Summary(summary)
                .Input("value", "core.String", required: true)
                .Output("result", "core.String")
                .Build();

        private static OperationCandidate Candidate(
            OperationDescriptor descriptor,
            OperationOrigin origin = OperationOrigin.Custom,
            string implementationHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            bool hasPollImplementation = true,
            string? sourceFile = null,
            string assemblyName = "Acme.Editor",
            string? packageId = null,
            string? resolvedCommit = null,
            string? declaredId = null,
            int? declaredVersion = null) =>
            new OperationCandidate(
                descriptor, origin, assemblyName, sourceFile,
                implementationHash, hasPollImplementation, packageId, resolvedCommit,
                DeclaredId: declaredId, DeclaredVersion: declaredVersion);

        private static RegistrySnapshot Build(
            ITypeRegistry? types = null,
            IReadOnlyList<TypeRegistryFailure>? typeFailures = null,
            params OperationCandidate[] candidates) =>
            RegistrySnapshotBuilder.Build(candidates, types ?? s_types, typeFailures);

        private static IReadOnlyList<string> FailureCodes(RegistrySnapshot snapshot)
        {
            var codes = new List<string>();
            for (var i = 0; i < snapshot.Failures.Count; i++)
            {
                var diagnostics = snapshot.Failures[i].Diagnostics;
                for (var j = 0; j < diagnostics.Count; j++)
                {
                    codes.Add(diagnostics[j].Code);
                }
            }
            return codes;
        }

        // ── SPEC §C3 test 2: duplicate (id, version) excludes all candidates ──

        [Test]
        public void DuplicateIdVersionExcludesEveryCandidate()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(Describe("acme.dup.op", 1), implementationHash: new string('a', 64)),
                Candidate(Describe("acme.dup.op", 1), implementationHash: new string('b', 64)),
                Candidate(Describe("acme.other.op", 1)),
            });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual("acme.other.op", snapshot.Operations[0].Descriptor.Id);

            Assert.AreEqual(2, snapshot.Failures.Count);
            for (var i = 0; i < snapshot.Failures.Count; i++)
            {
                var failure = snapshot.Failures[i];
                Assert.AreEqual(RegistryChangeKinds.Failed, failure.Kind);
                Assert.AreEqual("acme.dup.op", failure.OperationId);
                Assert.AreEqual(1, failure.Version);
                Assert.AreEqual(1, failure.Diagnostics.Count);
                Assert.AreEqual(ErrorCodes.OW_OP_004, failure.Diagnostics[0].Code);
                Assert.AreEqual(Severity.Error, failure.Diagnostics[0].Severity);
            }
        }

        [Test]
        public void DuplicateCountCoversDefectiveCandidates()
        {
            // A waitable/no-Poll candidate still counts toward its (id,
            // version): the healthy twin is excluded too (SPEC §B0.1's
            // conservative both-invalidated rule applied to §C3 rule 4).
            var snapshot = Build(candidates: new[]
            {
                Candidate(
                    Describe("acme.dup.op", 1) with { ExecutionMode = ExecutionMode.Waitable },
                    hasPollImplementation: false),
                Candidate(Describe("acme.dup.op", 1)),
            });

            Assert.AreEqual(0, snapshot.Operations.Count);
            Assert.AreEqual(2, snapshot.Failures.Count);
            CollectionAssert.Contains(FailureCodes(snapshot), ErrorCodes.OW_OP_004);
            CollectionAssert.Contains(FailureCodes(snapshot), ErrorCodes.OW_OP_003);
        }

        [Test]
        public void SameIdAtDifferentVersionsCoexists()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(Describe("acme.multi.op", 1), implementationHash: new string('a', 64)),
                Candidate(Describe("acme.multi.op", 2), implementationHash: new string('b', 64)),
            });

            Assert.AreEqual(2, snapshot.Operations.Count);
            Assert.AreEqual(0, snapshot.Failures.Count);
            Assert.AreEqual("acme.multi.op", snapshot.Operations[0].Descriptor.Id);
            Assert.AreEqual(1, snapshot.Operations[0].Descriptor.Version);
            Assert.AreEqual(2, snapshot.Operations[1].Descriptor.Version);
        }

        // ── SPEC §C3 rule 3: reserved namespaces are standard-only ──────────

        [Test]
        public void ReservedNamespaceFromCustomOriginFails()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(Describe("unity.fake.op", 1)),
                Candidate(Describe("core.fake.op", 1)),
                Candidate(Describe("opweave.fake.op", 1)),
                Candidate(Describe("acme.ok.op", 1)),
            });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual("acme.ok.op", snapshot.Operations[0].Descriptor.Id);

            Assert.AreEqual(3, snapshot.Failures.Count);
            for (var i = 0; i < snapshot.Failures.Count; i++)
            {
                Assert.AreEqual(1, snapshot.Failures[i].Diagnostics.Count);
                Assert.AreEqual(ErrorCodes.OW_OP_007, snapshot.Failures[i].Diagnostics[0].Code);
            }
        }

        [Test]
        public void ReservedNamespaceFromStandardOriginRegisters()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(Describe("unity.real.op", 1), origin: OperationOrigin.Standard),
            });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual(0, snapshot.Failures.Count);
            Assert.AreEqual(OperationOrigin.Standard, snapshot.Operations[0].Origin);
        }

        // ── SPEC §C2 item 2 / §C3 rule 3: the attribute-declared identity must
        //    equal the Descriptor identity (OW-OP-003) ──

        [Test]
        public void DeclaredIdMismatchingDescriptorIdFails()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(
                    Describe("acme.observed.op", 1),
                    declaredId: "acme.declared.op",
                    declaredVersion: 1),
                Candidate(
                    Describe("acme.ok.op", 1),
                    declaredId: "acme.ok.op",
                    declaredVersion: 1),
            });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual("acme.ok.op", snapshot.Operations[0].Descriptor.Id);
            Assert.AreEqual(1, snapshot.Failures.Count);
            var failure = snapshot.Failures[0];
            Assert.AreEqual(RegistryChangeKinds.Failed, failure.Kind);
            Assert.AreEqual("acme.observed.op", failure.OperationId);
            Assert.AreEqual(1, failure.Version);
            Assert.AreEqual(1, failure.Diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_OP_003, failure.Diagnostics[0].Code);
            Assert.AreEqual(Severity.Error, failure.Diagnostics[0].Severity);

            // The mismatch joins the candidate-failure list too (SPEC §C3
            // error paragraph).
            var rejected = snapshot.CandidateFailures
                .Where(f => f.Kind == RegistryCandidateKind.Operation)
                .ToList();
            Assert.AreEqual(1, rejected.Count);
            Assert.AreEqual(ErrorCodes.OW_OP_003, rejected[0].Diagnostics[0].Code);
        }

        [Test]
        public void DeclaredVersionMismatchingDescriptorVersionFails()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(
                    Describe("acme.observed.op", 2),
                    declaredId: "acme.observed.op",
                    declaredVersion: 1),
            });

            Assert.AreEqual(0, snapshot.Operations.Count);
            Assert.AreEqual(1, snapshot.Failures.Count);
            Assert.AreEqual(ErrorCodes.OW_OP_003, snapshot.Failures[0].Diagnostics[0].Code);
        }

        [Test]
        public void MatchingDeclaredIdentityRegisters()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(
                    Describe("acme.declared.op", 3),
                    declaredId: "acme.declared.op",
                    declaredVersion: 3),
            });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual(0, snapshot.Failures.Count);
        }

        [Test]
        public void CandidatesWithoutDeclaredIdentitySkipTheCheck()
        {
            // A metadata-only candidate (declared identity not carried on
            // this path) is judged on its Descriptor alone.
            var snapshot = Build(candidates: new[]
            {
                Candidate(Describe("acme.undeclared.op", 1)),
            });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual(0, snapshot.Failures.Count);
        }

        [Test]
        public void PreInstantiationFailuresJoinTheSnapshotFailures()
        {
            // A candidate refused on type metadata alone — before any
            // construction (SPEC §C3 stages 2–3) — reaches the snapshot as a
            // Failed record ahead of this builder's own rejections.
            var refused = new RegistryChange(
                RegistryChangeKinds.Failed,
                "acme.pre.op",
                1,
                SourceFile: null,
                new List<Diagnostic>
                {
                    ErrorCodes.Create(ErrorCodes.OW_OP_007, "reserved"),
                });
            var snapshot = RegistrySnapshotBuilder.Build(
                new[]
                {
                    Candidate(Describe("acme.ok.op", 1)),
                    Candidate(
                        Describe("acme.bad.op", 1) with { ExecutionMode = ExecutionMode.Waitable },
                        hasPollImplementation: false),
                },
                s_types,
                typeFailures: null,
                candidateFailures: null,
                preInstantiationFailures: new[] { refused });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual(2, snapshot.Failures.Count);
            Assert.AreEqual("acme.pre.op", snapshot.Failures[0].OperationId);
            Assert.AreEqual(ErrorCodes.OW_OP_007, snapshot.Failures[0].Diagnostics[0].Code);
            Assert.AreEqual("acme.bad.op", snapshot.Failures[1].OperationId);
            Assert.AreEqual(ErrorCodes.OW_OP_003, snapshot.Failures[1].Diagnostics[0].Code);
        }

        // ── SPEC §C3 rule 3: Waitable without Poll → OW-OP-003 ───────────────

        [Test]
        public void WaitableWithoutPollImplementationFails()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(
                    Describe("acme.wait.op", 1) with { ExecutionMode = ExecutionMode.Waitable },
                    hasPollImplementation: false),
                Candidate(Describe("acme.ok.op", 1)),
            });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual("acme.ok.op", snapshot.Operations[0].Descriptor.Id);
            Assert.AreEqual(1, snapshot.Failures.Count);
            Assert.AreEqual(ErrorCodes.OW_OP_003, snapshot.Failures[0].Diagnostics[0].Code);
        }

        [Test]
        public void WaitableWithPollImplementationRegisters()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(
                    Describe("acme.wait.op", 1) with { ExecutionMode = ExecutionMode.Waitable },
                    hasPollImplementation: true),
            });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual(0, snapshot.Failures.Count);
        }

        // ── SPEC §C3: Operations depending on invalidated types → OW-OP-003 ──

        [Test]
        public void OperationDependingOnInvalidatedTypeFails()
        {
            // Two custom providers offering the same Type ID invalidate each
            // other (SPEC §B0.1); a port that depends on the invalidated type
            // is absent from the finished snapshot, so its Operation fails as
            // unresolvable with OW-OP-003 (SPEC §C3 rule 3) while unrelated
            // Operations remain. The fixture registry carries every type
            // except the invalidated one.
            var codec = new PassthroughCodec();
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    "acme.one",
                    new FakeProvider(new TypeDefinition(
                        "acme.Widget", TypeKind.Primitive, 0,
                        Array.Empty<string>(), null, "widget", codec)),
                    OperationOrigin.Custom),
                new TypeProviderCandidate(
                    "acme.two",
                    new FakeProvider(new TypeDefinition(
                        "acme.Widget", TypeKind.Primitive, 0,
                        Array.Empty<string>(), null, "widget", codec)),
                    OperationOrigin.Custom),
            });
            Assert.AreEqual(2, build.Failures.Count);

            var snapshot = Build(
                types: s_types,
                typeFailures: build.Failures,
                candidates: new[]
                {
                    Candidate(Op.Define("acme.uses.widget", 1)
                        .Summary("Uses the invalidated type.")
                        .Input("w", "acme.Widget", required: true)
                        .Output("result", "core.String")
                        .Build()),
                    Candidate(Describe("acme.ok.op", 1)),
                });

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual("acme.ok.op", snapshot.Operations[0].Descriptor.Id);
            Assert.AreEqual(1, snapshot.Failures.Count);
            Assert.AreEqual(ErrorCodes.OW_OP_014, snapshot.Failures[0].Diagnostics[0].Code);
            Assert.AreEqual(2, snapshot.TypeFailures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.DuplicateTypeId, snapshot.TypeFailures[0].Kind);
        }

        // ── SPEC §C3 test 6: registryHash determinism and sensitivity ────────

        [Test]
        public void RegistryHashIgnoresOrderGenerationSourceFileOriginAndDiagnostics()
        {
            var first = Build(candidates: new[]
            {
                Candidate(
                    Describe("acme.one.op", 1),
                    origin: OperationOrigin.Custom,
                    sourceFile: "Editor/One.cs",
                    assemblyName: "Acme.One.Editor"),
                Candidate(
                    Describe("acme.two.op", 1),
                    origin: OperationOrigin.Standard,
                    sourceFile: "Editor/Two.cs",
                    assemblyName: "Acme.Two.Editor"),
            });
            var second = Build(candidates: new[]
            {
                Candidate(
                    Describe("acme.two.op", 1),
                    origin: OperationOrigin.Custom,
                    sourceFile: null,
                    assemblyName: "Other.Editor"),
                Candidate(
                    Describe("acme.one.op", 1),
                    origin: OperationOrigin.Custom,
                    sourceFile: "Moved.cs",
                    assemblyName: "Other.Editor"),
            });

            Assert.AreEqual(first.RegistryHash, second.RegistryHash);
            Assert.AreNotEqual(first.GenerationId, second.GenerationId);
            StringAssert.StartsWith(RegistrySnapshotBuilder.GenerationIdPrefix, first.GenerationId);

            // Diagnostics never enter the hash: a build where one extra
            // candidate failed has the same hash as a build that never saw it.
            var withFailure = Build(candidates: new[]
            {
                Candidate(Describe("acme.one.op", 1)),
                Candidate(Describe("acme.two.op", 1)),
                Candidate(Describe("unity.bad.op", 1)),
            });
            Assert.AreEqual(1, withFailure.Failures.Count);
            Assert.AreEqual(first.RegistryHash, withFailure.RegistryHash);
        }

        [Test]
        public void RegistryHashChangesWithIdVersionDescriptorHashOrImplementationHash()
        {
            var baseline = Build(candidates: new[]
            {
                Candidate(Describe("acme.one.op", 1)),
            }).RegistryHash;

            var differentId = Build(candidates: new[]
            {
                Candidate(Describe("acme.two.op", 1)),
            }).RegistryHash;
            var differentVersion = Build(candidates: new[]
            {
                Candidate(Describe("acme.one.op", 2)),
            }).RegistryHash;
            var differentDescriptor = Build(candidates: new[]
            {
                Candidate(Describe("acme.one.op", 1, summary: "A different summary.")),
            }).RegistryHash;
            var differentImplementation = Build(candidates: new[]
            {
                Candidate(Describe("acme.one.op", 1), implementationHash: new string('f', 64)),
            }).RegistryHash;

            Assert.AreNotEqual(baseline, differentId);
            Assert.AreNotEqual(baseline, differentVersion);
            Assert.AreNotEqual(baseline, differentDescriptor);
            Assert.AreNotEqual(baseline, differentImplementation);
        }

        [Test]
        public void RegistryHashMatchesFixedInputShape()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(Describe("acme.only.op", 1), implementationHash: new string('c', 64)),
            });

            var registered = snapshot.Operations[0];
            var expected = CanonicalJson.Sha256(new Newtonsoft.Json.Linq.JObject
            {
                ["operations"] = new Newtonsoft.Json.Linq.JArray
                {
                    new Newtonsoft.Json.Linq.JObject
                    {
                        ["id"] = "acme.only.op",
                        ["version"] = 1,
                        ["descriptorHash"] = registered.DescriptorHash,
                        ["implementationHash"] = new string('c', 64),
                    },
                },
            });
            Assert.AreEqual(expected, snapshot.RegistryHash);
            StringAssert.IsMatch(@"\A[0-9a-f]{64}\z", snapshot.RegistryHash);
        }

        [Test]
        public void EmptyCandidateListBuildsEmptySnapshot()
        {
            var snapshot = Build();
            Assert.AreEqual(0, snapshot.Operations.Count);
            Assert.AreEqual(0, snapshot.Failures.Count);

            var expected = CanonicalJson.Sha256(
                JsonValue.Parse(@"{""operations"":[]}").Value!.ToToken());
            Assert.AreEqual(expected, snapshot.RegistryHash);
        }

        // ── Snapshot model ──────────────────────────────────────────────────

        [Test]
        public void SnapshotCarriesHashesOriginAndPackageInfoSlots()
        {
            var descriptor = Describe("acme.pkg.op", 1);
            var snapshot = Build(candidates: new[]
            {
                Candidate(
                    descriptor,
                    origin: OperationOrigin.Custom,
                    implementationHash: new string('d', 64),
                    sourceFile: "Editor/Pkg.cs",
                    packageId: "com.acme.ops",
                    resolvedCommit: new string('e', 40)),
            });

            var registered = snapshot.Operations[0];
            Assert.AreSame(descriptor, registered.Descriptor);
            Assert.AreEqual(OperationOrigin.Custom, registered.Origin);
            Assert.AreEqual("Acme.Editor", registered.AssemblyName);
            Assert.AreEqual("Editor/Pkg.cs", registered.SourceFile);
            Assert.AreEqual(DescriptorHash.Compute(descriptor), registered.DescriptorHash);
            Assert.AreEqual(new string('d', 64), registered.ImplementationHash);
            Assert.AreEqual("com.acme.ops", registered.PackageId);
            Assert.AreEqual(new string('e', 40), registered.ResolvedCommit);
        }

        [Test]
        public void FindResolvesExactAndLatestVersions()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(Describe("acme.multi.op", 1), implementationHash: new string('a', 64)),
                Candidate(Describe("acme.multi.op", 3), implementationHash: new string('b', 64)),
                Candidate(Describe("acme.multi.op", 2), implementationHash: new string('c', 64)),
            });

            Assert.AreEqual(2, snapshot.Find("acme.multi.op", 2)!.Descriptor.Version);
            Assert.AreEqual(3, snapshot.FindLatest("acme.multi.op")!.Descriptor.Version);
            Assert.IsNull(snapshot.Find("acme.multi.op", 4));
            Assert.IsNull(snapshot.FindLatest("acme.missing.op"));

            // The compiler's internal lookup seam resolves through the
            // snapshot without new glue (SPEC §D0.2).
            var source = (IOperationSource)snapshot;
            Assert.AreEqual("acme.multi.op", source.Find("acme.multi.op", 1)!.Id);
            Assert.IsNull(source.Find("acme.multi.op", 9));
        }

        [Test]
        public void SnapshotRejectsDuplicateOperationsAndNonFailedChanges()
        {
            var operation = new RegisteredOperation(
                Describe("acme.dup.op", 1), OperationOrigin.Custom, "Acme.Editor",
                null, new string('a', 64), new string('b', 64));

            Assert.Throws<ArgumentException>(() => new RegistrySnapshot(
                "reg_01",
                new string('f', 64),
                new[] { operation, operation },
                s_types,
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>()));

            var added = new RegistryChange(
                RegistryChangeKinds.Added, "acme.dup.op", 1, null,
                Array.Empty<Diagnostic>());
            Assert.Throws<ArgumentException>(() => new RegistrySnapshot(
                "reg_01",
                new string('f', 64),
                Array.Empty<RegisteredOperation>(),
                s_types,
                new[] { added },
                Array.Empty<TypeRegistryFailure>()));
        }

        // ── SPEC §C3 error paragraph: unvalidated Type IDs are never
        //    published verbatim (T-W2.12-03 review correction) ──────────

        // A provider-supplied malformed Type ID can carry credentials —
        // a URL userinfo or a credential-named query parameter — so no
        // published candidate field or diagnostic string may contain the
        // raw text: the fixed redacted marker stands in for the ID while
        // the rejection itself and its assigned OW-OP code are unchanged.
        [Test]
        public void MalformedCredentialTypeIdsArePublishedRedacted()
        {
            const string secret = "s3cr3t-token";
            const string credentialId = "https://user:s3cr3t-token@example.com/type";
            const string queryId = "x?token=s3cr3t-token";
            const string credentialBase = "https://user:s3cr3t-token@example.com/base";
            // The round-5 leak forms: a credential parameter behind a '#'
            // fragment marker and userinfo on a non-http(s)/ssh/git scheme.
            const string fragmentId = "x#token=s3cr3t-token";
            const string fragmentUrlId =
                "https://example.com/t#access_token=s3cr3t-token";
            const string ftpId = "ftp://user:s3cr3t-token@example.com/type";
            const string customId = "custom+scheme://s3cr3t-token@host/x";
            // The round-6 leak form: a bare credential assignment behind
            // no '?'/'&'/';'/'#' marker and no scheme userinfo at all.
            const string bareId = "password=s3cr3t-token";
            var codec = new PassthroughCodec();
            TypeDefinition Offer(string id, params string[] baseTypes) =>
                new TypeDefinition(id, TypeKind.Primitive, 0,
                    baseTypes, null, "malformed offer", codec);

            var build = TypeRegistryBuilder.Build(new[]
            {
                // The same credential-bearing ID from two providers: each
                // offer is malformed and the shared ID collides — the
                // duplicate (OW-OP-013) failure must not echo it either.
                new TypeProviderCandidate("evil.one",
                    new FakeProvider(Offer(credentialId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.two",
                    new FakeProvider(Offer(credentialId)), OperationOrigin.Custom),
                // A credential-looking query parameter with no URL shape,
                // and a credential-bearing base-type reference on an
                // otherwise valid offer (OW-OP-014).
                new TypeProviderCandidate("evil.three",
                    new FakeProvider(Offer(queryId), Offer("evil.Derived", credentialBase)),
                    OperationOrigin.Custom),
                // Each round-5 form offered by a provider pair as well, so
                // both the malformed screen and the collision verdict pass
                // over every leak shape.
                new TypeProviderCandidate("evil.four",
                    new FakeProvider(Offer(fragmentId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.five",
                    new FakeProvider(Offer(fragmentId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.six",
                    new FakeProvider(Offer(fragmentUrlId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.seven",
                    new FakeProvider(Offer(fragmentUrlId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.eight",
                    new FakeProvider(Offer(ftpId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.nine",
                    new FakeProvider(Offer(ftpId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.ten",
                    new FakeProvider(Offer(customId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.eleven",
                    new FakeProvider(Offer(customId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.twelve",
                    new FakeProvider(Offer(bareId)), OperationOrigin.Custom),
                new TypeProviderCandidate("evil.thirteen",
                    new FakeProvider(Offer(bareId)), OperationOrigin.Custom),
            });
            var snapshot = Build(types: s_types, typeFailures: build.Failures);

            // The rejections are unchanged in kind and count: thirteen
            // malformed offers, the six two-sided collisions of the
            // shared IDs and the unresolvable base of the valid offer.
            Assert.AreEqual(26, build.Failures.Count);
            Assert.AreEqual(26, snapshot.TypeFailures.Count);
            Assert.AreEqual(26, snapshot.CandidateFailures.Count);
            Assert.IsTrue(snapshot.CandidateFailures.Any(f =>
                f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_013)),
                "the colliding malformed offer still produces OW-OP-013");
            Assert.IsTrue(snapshot.CandidateFailures.Any(f =>
                f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_014)),
                "the credential-bearing base still produces OW-OP-014");

            // Every string field the snapshot publishes for a rejected
            // candidate or a diagnostic — none may carry the credential.
            AssertNoPublishedStringContains(snapshot, secret, "user:");

            // What is published instead: the deterministic redacted
            // marker — both sides of the collision correlate on it —
            // while a canonical ID like the valid offer's is unchanged.
            var redacted = TypeRegistryBuilder.RedactedTypeId(credentialId);
            StringAssert.StartsWith("<malformed id ", redacted);
            Assert.AreEqual(redacted, TypeRegistryBuilder.SafeTypeId(credentialId, 0));

            var op13 = snapshot.CandidateFailures
                .Where(f => f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_013))
                .ToList();
            Assert.AreEqual(12, op13.Count);
            var redactedIds = new[]
                {
                    credentialId, fragmentId, fragmentUrlId, ftpId, customId, bareId,
                }
                .Select(TypeRegistryBuilder.RedactedTypeId)
                .ToList();
            Assert.IsTrue(op13.All(f => redactedIds.Contains(f.CandidateId)),
                "every side of every collision publishes the redacted marker");

            var unregisteredBase = snapshot.TypeFailures
                .Single(f => f.Kind == TypeRegistryFailureKind.UnregisteredBaseType);
            Assert.AreEqual("evil.Derived", unregisteredBase.TypeId);
            StringAssert.Contains("of 'evil.Derived'", unregisteredBase.Detail);
        }

        // ── SPEC §C3 error paragraph: the publication boundary masks every
        //    candidate/diagnostic string, whichever emitter produced it
        //    (T-W2.12-03 review correction round 4) ─────────────────────

        // A diagnostic an emitter built around an unvalidated
        // credential-bearing ID — the cross-package observed-Type
        // duplicate verdict (OW-OP-013) — is masked when it is stored on
        // the snapshot's published records: the code, count and the
        // credential-free shape of the text are unchanged.
        [Test]
        public void CredentialBearingStringsAreMaskedAtThePublicationBoundary()
        {
            const string secret = "s3cr3t-token";
            var diagnostic = ErrorCodes.Create(
                ErrorCodes.OW_OP_013,
                $"Type ID 'https://user:{secret}@example.com/type' is declared by more than one package.",
                hint: $"Also declared by 'acme.other?token={secret}' and " +
                    $"'custom+scheme://{secret}@host/x' (SPEC §X.3.2).");
            var snapshot = RegistrySnapshotBuilder.Build(
                Array.Empty<OperationCandidate>(),
                s_types,
                typeFailures: new[]
                {
                    new TypeRegistryFailure(
                        TypeRegistryFailureKind.DuplicateProviderId,
                        null,
                        $"acme.provider?sig={secret}",
                        $"Provider ID 'https://user:{secret}@example.com/p' is declared by 2 candidates; " +
                            $"all were rejected. See 'x#sig={secret}' and 'ftp://user:{secret}@example.com/p'.",
                        $"Acme.Editor?auth={secret}"),
                },
                candidateFailures: new[]
                {
                    new RegistryCandidateFailure(
                        RegistryCandidateKind.TypeProvider,
                        $"acme.Provider?key={secret}",
                        ProviderId: null,
                        $"Acme.Editor?pwd={secret}",
                        $"Editor/Cred.cs#token={secret}",
                        new[] { diagnostic }),
                });

            // The rejections themselves and their assigned OW-OP codes
            // are unchanged.
            Assert.AreEqual(1, snapshot.TypeFailures.Count);
            Assert.AreEqual(2, snapshot.CandidateFailures.Count);
            Assert.IsTrue(snapshot.CandidateFailures.Any(f =>
                f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_013)));

            AssertNoPublishedStringContains(snapshot, secret, "user:");

            // What is published keeps the non-credential shape of the
            // text: the masked userinfo and the masked parameter values
            // behind every '?'/'&'/';'/'#' marker and on any scheme.
            var published = PublishedDiagnosticStrings(snapshot);
            Assert.IsTrue(published.Any(t => t.Contains("https://***@example.com/type")),
                "the masked URL keeps its non-credential parts");
            Assert.IsTrue(published.Any(t => t.Contains("?token=***")),
                "the masked query parameter keeps its key");
            Assert.IsTrue(published.Any(t => t.Contains("x#sig=***")),
                "the masked fragment parameter keeps its key");
            Assert.IsTrue(published.Any(t => t.Contains("ftp://***@example.com/p")),
                "the masked ftp userinfo keeps its non-credential parts");
            Assert.IsTrue(published.Any(t => t.Contains("custom+scheme://***@host/x")),
                "the masked custom-scheme userinfo keeps its non-credential parts");
            Assert.IsTrue(published.Any(t => t.Contains("#token=***")),
                "the masked source-file fragment keeps its key");
        }

        // The malformed port-type check (OW-OP-003) interpolates the
        // unparseable port type — a malformed TypeRef is never published
        // verbatim: the fixed redacted marker stands in for it, whatever
        // credential form the raw text carried (the rounds 3–6 table,
        // including the bare 'password=…' assignment).
        [Test]
        public void MalformedCredentialPortTypesArePublishedRedacted()
        {
            const string secret = "s3cr3t-token";
            var portTypes = new[]
            {
                $"https://user:{secret}@example.com/type",
                $"x?token={secret}",
                $"x#token={secret}",
                $"https://example.com/t#access_token={secret}",
                $"ftp://user:{secret}@example.com/type",
                $"custom+scheme://{secret}@host/x",
                $"password={secret}",
            };
            var snapshot = Build(candidates: portTypes
                .Select((type, index) => Candidate(Op.Define($"acme.credential.port{index}", 1)
                    .Summary("Port type carrying a credential form.")
                    .Input("in", type, required: true)
                    .Output("result", "core.String")
                    .Build()))
                .ToArray());

            // All candidates still fail as unparseable — the OW-OP-003
            // count and code are unchanged on both published lists.
            Assert.AreEqual(0, snapshot.Operations.Count);
            Assert.AreEqual(7, snapshot.Failures.Count);
            Assert.AreEqual(7, snapshot.CandidateFailures.Count);
            foreach (var failure in snapshot.Failures)
            {
                Assert.IsTrue(failure.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_003));
            }
            Assert.IsTrue(snapshot.CandidateFailures.All(f =>
                f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_003)));

            AssertNoPublishedStringContains(snapshot, secret, "user:");
            var published = PublishedDiagnosticStrings(snapshot);
            foreach (var portType in portTypes)
            {
                var redacted = TypeRegistryBuilder.RedactedTypeId(portType);
                Assert.IsTrue(
                    published.Any(t => t.Contains($"malformed type ID '{redacted}'")),
                    $"every malformed port type publishes the redacted marker: {redacted}");
            }
            Assert.IsFalse(published.Any(t => t.Contains("***")),
                "the placeholder carries no part of the raw text");
        }

        [Test]
        public void CredentialFreeMalformedPortTypeIsPublishedAsPlaceholder()
        {
            var snapshot = Build(candidates: new[]
            {
                Candidate(Op.Define("acme.malformed.port", 1)
                    .Summary("Port type malformed but credential-free.")
                    .Input("in", "not a type id", required: true)
                    .Output("result", "core.String")
                    .Build()),
            });

            var diagnostic = snapshot.Failures
                .SelectMany(f => f.Diagnostics)
                .Single(d => d.Code == ErrorCodes.OW_OP_003);
            StringAssert.Contains(
                "malformed type ID '" + TypeRegistryBuilder.RedactedTypeId("not a type id") + "'",
                diagnostic.Message);
            Assert.IsFalse(
                PublishedDiagnosticStrings(snapshot).Any(t => t.Contains("not a type id")),
                "a malformed ID is published as the placeholder, never verbatim");
        }

        // ── Shared assertions ─────────────────────────────────────────────

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

        // ── Fixtures ────────────────────────────────────────────────────────

        private sealed class PassthroughCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) => value;
        }

        private sealed class FakeProvider : ITypeProvider
        {
            private readonly TypeDefinition[] _definitions;

            internal FakeProvider(params TypeDefinition[] definitions) => _definitions = definitions;

            public IEnumerable<TypeDefinition> GetTypes() => _definitions;
        }
    }
}
