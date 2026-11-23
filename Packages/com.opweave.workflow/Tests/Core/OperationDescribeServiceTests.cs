#nullable enable

// T-W3.1-03 [Core] tests: local:T-W3.1-03:describe-service — the SPEC §C8
// describe_operation service over a synthetic RegistrySnapshot built
// through the real RegistrySnapshotBuilder (SPEC §0.6 SG-10 fixture
// registry): every §C8 section of the response, the fields projection
// (["inputs","outputs","examples"] and narrower), latest-version and
// pinned-version lookup, OW-OP-001 for an ID nothing registered — the
// failed-registration hint carrying the rejection's code and source file —
// and OW-OP-002 for a registered ID at an unregistered version. The AT-F06
// half validates every returned example's complete WF through the
// snapshot-backed WorkflowCompiler, fixture manifest or not: in Core the
// manifest has nothing to materialize, so the check is that a
// manifest-carrying example round-trips verbatim and its workflow still
// validates ok:true.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §C8 <see cref="OperationDescribeService"/>.
    /// </summary>
    internal sealed class OperationDescribeServiceTests
    {
        private const string AlphaId = "test.describe.alpha";
        private const string PathsId = "test.describe.paths";
        private const string PackagedId = "test.describe.packaged";
        private const string MixedId = "test.describe.mixed";
        private const string BrokenId = "test.describe.broken";
        private const string BrokenSourceFile = "Assets/Acme/BrokenOperation.cs";

        // ── Fixture Operations ────────────────────────────────────────────

        // A no-op implementation for every synthetic candidate: the
        // snapshot-backed WorkflowCompiler refuses a snapshot whose
        // Operations carry no implementation instance.
        private sealed class StubOperation : IOperation
        {
            internal StubOperation(OperationDescriptor descriptor) => Descriptor = descriptor;

            public OperationDescriptor Descriptor { get; }

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

        private sealed class NoTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => false;
        }

        // The complete WF the alpha v2 example carries: one core.start, one
        // node of the Operation itself with its required input, a control
        // edge and the workflow output (SPEC §C1 item 4).
        private const string AlphaExampleWorkflow = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.describe-alpha"",
  ""title"": ""Echo a string through the Operation"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""op"", ""operation"": ""test.describe.alpha"", ""version"": 2,
      ""parameters"": { ""text"": ""hello"" } }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""op"" } ],
  ""outputs"": [ { ""id"": ""echo"", ""from"": { ""node"": ""op"", ""port"": ""echo"" } } ]
}";

        // The same workflow wrapped for the fixture-carrying example: its
        // files-manifest is inert in Core but must round-trip verbatim.
        private const string AlphaFixtureExampleWorkflow = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.describe-alpha-fixture"",
  ""title"": ""Echo with a fixture file prepared"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""op"", ""operation"": ""test.describe.alpha"", ""version"": 2,
      ""parameters"": { ""text"": ""fixture"" } }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""op"" } ],
  ""outputs"": [ { ""id"": ""echo"", ""from"": { ""node"": ""op"", ""port"": ""echo"" } } ]
}";

        private const string AlphaFixtureManifest =
            "{\"files\":[{\"path\":\"Assets/OpWeaveExamples/Describe/Probe.txt\"," +
            "\"text\":\"describe fixture.\\n\"}]}";

        private const string PathsExampleWorkflow = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.describe-paths"",
  ""title"": ""Write under an allowed path"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""write"", ""operation"": ""test.describe.paths"", ""version"": 1 }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""write"" } ],
  ""outputs"": [ { ""id"": ""done"", ""from"": { ""node"": ""write"", ""port"": ""done"" } } ]
}";

        private const string MixedExampleWorkflow = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.describe-mixed"",
  ""title"": ""Run the registered version of the Operation"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""op"", ""operation"": ""test.describe.mixed"", ""version"": 1 }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""op"" } ],
  ""outputs"": [ { ""id"": ""done"", ""from"": { ""node"": ""op"", ""port"": ""done"" } } ]
}";

        private static OperationDescriptor StartDescriptor() =>
            Op.Define("core.start", 1).Summary("Workflow entry point.").Build();

        private static OperationDescriptor AlphaDescriptor(int version) =>
            Op.Define(AlphaId, version)
                .Summary("Echo a string.")
                .Ja("エコー", "文字列をそのまま返す。")
                .Input("text", "core.String", required: true, c => c.MaxLength(64))
                .Input("position", "unity.Vector3", required: false)
                .Input("targets", "core.List<unity.AssetPath>", required: false)
                .Output("echo", "core.String")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Requires("edit_mode")
                .Build()
                with
                {
                    DisplayName = "Echo",
                    Category = "Test",
                    Description = "Returns the input string unchanged.",
                    Examples = version == 2
                        ? new[]
                        {
                            new OperationExample(
                                "Echo a string",
                                JsonValue.Parse(AlphaExampleWorkflow).Value!),
                            new OperationExample(
                                "Echo with a fixture",
                                JsonValue.Parse(AlphaFixtureExampleWorkflow).Value!,
                                JsonValue.Parse(AlphaFixtureManifest).Value!),
                        }
                        : new[]
                        {
                            new OperationExample(
                                "Echo v1",
                                JsonValue.Parse(AlphaExampleWorkflow
                                    .Replace("\"version\": 2", "\"version\": 1")).Value!),
                        },
                };

        private static OperationDescriptor PathsDescriptor() =>
            Op.Define(PathsId, 1)
                .Summary("Write text under an allowed path.")
                .Input("path", "unity.AssetPath", required: false)
                .Input("typeName", "unity.TypeName", required: false)
                .Output("done", "core.Bool")
                .Effects(EffectNames.AssetCreate)
                .Requires("not_compiling")
                .Requires("path_writable", "path")
                .Requires("type_exists", "typeName")
                .Build()
                with
                {
                    DisplayName = "Write Path",
                    Examples = new[]
                    {
                        new OperationExample(
                            "Write under an allowed path",
                            JsonValue.Parse(PathsExampleWorkflow).Value!),
                    },
                };

        private static OperationDescriptor PackagedDescriptor() =>
            Op.Define(PackagedId, 1)
                .Summary("A packaged Operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

        private static OperationDescriptor MixedDescriptor(int version) =>
            version == 1
                ? Op.Define(MixedId, 1)
                    .Summary("The version that registered.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with
                    {
                        Examples = new[]
                        {
                            new OperationExample(
                                "Run the mixed Operation",
                                JsonValue.Parse(MixedExampleWorkflow).Value!),
                        },
                    }
                // v2 references a type nothing registers: the builder
                // rejects it, so only v1 ever reaches the snapshot — a
                // registered ID carrying a failed sibling version.
                : Op.Define(MixedId, 2)
                    .Summary("The version that failed registration.")
                    .Input("value", "test.describe.missing", required: true)
                    .Build();

        private static OperationDescriptor BrokenDescriptor() =>
            Op.Define(BrokenId, 1)
                .Summary("A custom Operation whose port type never registered.")
                .Input("value", "test.describe.missing", required: true)
                .Build();

        private static OperationCandidate Candidate(
            OperationDescriptor descriptor,
            OperationOrigin origin = OperationOrigin.Custom,
            string assemblyName = "Acme.Editor",
            string? sourceFile = "Assets/Acme/Operation.cs",
            string? packageId = null,
            string? commit = null) =>
            new OperationCandidate(
                descriptor,
                origin,
                assemblyName,
                sourceFile,
                new string('a', 64),
                HasPollImplementation: true,
                PackageId: packageId,
                ResolvedCommit: commit,
                Implementation: new StubOperation(descriptor));

        private static readonly ITypeRegistry s_types = SearchServiceFixtures.CreateTypes();

        private static RegistrySnapshot BuildSnapshot() =>
            RegistrySnapshotBuilder.Build(new[]
            {
                Candidate(StartDescriptor(),
                    OperationOrigin.Standard, "OpWeave.Operations", sourceFile: null),
                Candidate(AlphaDescriptor(1)),
                Candidate(AlphaDescriptor(2)),
                Candidate(PathsDescriptor()),
                Candidate(PackagedDescriptor(),
                    packageId: "com.acme.pack", commit: new string('c', 40)),
                Candidate(MixedDescriptor(1)),
                Candidate(MixedDescriptor(2), sourceFile: "Assets/Acme/MixedOperation.cs"),
                Candidate(BrokenDescriptor(), sourceFile: BrokenSourceFile),
            }, s_types);

        private static readonly RegistrySnapshot s_snapshot = BuildSnapshot();

        private static OperationDescribeService Service() => new OperationDescribeService(s_snapshot);

        private static OperationDescription Describe(DescribeOperationRequest request)
        {
            var result = Service().DescribeOperation(request);
            Assert.IsTrue(result.Ok, "DescribeOperation failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return result.Value!;
        }

        private static Diagnostic DescribeFailure(DescribeOperationRequest request)
        {
            var result = Service().DescribeOperation(request);
            Assert.IsFalse(result.Ok, "DescribeOperation must fail for " + request.Id);
            Assert.IsNotEmpty(result.Diagnostics, "the failure must carry a diagnostic");
            return result.Diagnostics[0];
        }

        private static WorkflowCompiler Compiler() =>
            new WorkflowCompiler(
                s_snapshot,
                (nodeId, parameters) => new OperationContractFixtures.Static(nodeId, parameters),
                new NoTypeProbe(),
                new PlanProvenanceFixtures.Stub());

        // ── Resolution ────────────────────────────────────────────────────

        [Test]
        public void OmittedVersionAnswersTheNewestRegisteredVersion()
        {
            var description = Describe(new DescribeOperationRequest(AlphaId));

            Assert.That(description.Id, Is.EqualTo(AlphaId));
            Assert.That(description.Version, Is.EqualTo(2));
        }

        [Test]
        public void PinnedVersionAnswersThatVersion()
        {
            var description = Describe(new DescribeOperationRequest(AlphaId) { Version = 1 });

            Assert.That(description.Version, Is.EqualTo(1));
            Assert.That(description.Descriptor!.Version, Is.EqualTo(1));
        }

        [Test]
        public void UnsupportedVersionAnswersOwOp002NamingTheRegisteredVersions()
        {
            var failure = DescribeFailure(new DescribeOperationRequest(AlphaId) { Version = 3 });

            Assert.That(failure.Code, Is.EqualTo(ErrorCodes.OW_OP_002));
            Assert.That(failure.Hint, Does.Contain("1"));
            Assert.That(failure.Hint, Does.Contain("2"));
        }

        [Test]
        public void FailedVersionOfARegisteredIdAnswersOwOp002()
        {
            // test.describe.mixed v2 never registered — but v1 did, so the
            // request is an unsupported-version answer, not an unknown-ID one.
            var failure = DescribeFailure(new DescribeOperationRequest(MixedId) { Version = 2 });

            Assert.That(failure.Code, Is.EqualTo(ErrorCodes.OW_OP_002));
        }

        [Test]
        public void UnknownIdAnswersOwOp001()
        {
            var failure = DescribeFailure(new DescribeOperationRequest("test.describe.never"));

            Assert.That(failure.Code, Is.EqualTo(ErrorCodes.OW_OP_001));
            Assert.That(failure.Message, Does.Contain("test.describe.never"));
        }

        [Test]
        public void UnknownReservedIdAnswersOwOp001WithTheCoreHint()
        {
            var failure = DescribeFailure(new DescribeOperationRequest("core.branch"));

            Assert.That(failure.Code, Is.EqualTo(ErrorCodes.OW_OP_001));
            Assert.That(failure.Hint, Does.Contain("does not support branching"));
        }

        // SPEC §C8: describing an ID whose candidate failed registration
        // answers OW-OP-001 with the failure reason — the code and the
        // source file — in the hint.
        [Test]
        public void FailedRegistrationIdAnswersOwOp001WithCodeAndSourceFileInHint()
        {
            var failure = DescribeFailure(new DescribeOperationRequest(BrokenId));

            Assert.That(failure.Code, Is.EqualTo(ErrorCodes.OW_OP_001));

            var rejected = s_snapshot.Failures.Single(
                f => f.OperationId == BrokenId && f.Kind == RegistryChangeKinds.Failed);
            Assert.IsNotEmpty(rejected.Diagnostics,
                "the fixture's rejection must carry a diagnostic for the hint to name");
            foreach (var diagnostic in rejected.Diagnostics.Where(d => d.Code.Length != 0))
            {
                Assert.That(failure.Hint, Does.Contain(diagnostic.Code),
                    "the hint must carry the registration failure's code");
            }
            Assert.That(failure.Hint, Does.Contain(BrokenSourceFile),
                "the hint must carry the registration failure's source file");
        }

        // ── The full response (fields omitted) ────────────────────────────

        [Test]
        public void DescribeReturnsEverySection()
        {
            var description = Describe(new DescribeOperationRequest(AlphaId));

            Assert.That(description.Descriptor, Is.Not.Null);
            Assert.That(description.Descriptor!.Id, Is.EqualTo(AlphaId));
            Assert.That(description.DisplayName, Is.EqualTo("Echo"));
            Assert.That(description.Summary, Is.EqualTo("Echo a string."));
            Assert.That(description.Description, Is.EqualTo("Returns the input string unchanged."));
            Assert.That(description.Inputs, Has.Count.EqualTo(3));
            Assert.That(description.Outputs, Has.Count.EqualTo(1));
            Assert.That(description.Requirements, Has.Count.EqualTo(1));
            Assert.That(description.Examples, Has.Count.EqualTo(2));
            Assert.That(description.Origin, Is.EqualTo(OperationOrigin.Custom));
            Assert.That(description.AssemblyName, Is.EqualTo("Acme.Editor"));
            Assert.That(description.SourceFile, Is.EqualTo("Assets/Acme/Operation.cs"));
            Assert.That(description.DescriptorHash,
                Is.EqualTo(DescriptorHash.Compute(description.Descriptor!)));
            Assert.That(description.ImplementationHash, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(description.RegistryGeneration,
                Is.EqualTo(s_snapshot.GenerationId));
            Assert.That(description.Diagnostics, Is.Not.Null);
            Assert.That(description.Diagnostics, Is.Empty,
                "a clean registration has no diagnostics");
        }

        [Test]
        public void DescribeReturnsPackageProvenance()
        {
            var description = Describe(new DescribeOperationRequest(PackagedId));

            Assert.That(description.PackageId, Is.EqualTo("com.acme.pack"));
            Assert.That(description.ResolvedCommit, Is.EqualTo(new string('c', 40)));
        }

        // The port schemas are generated through §B8: the constrained
        // core.String port carries its maxLength keyword and the
        // core.List<unity.AssetPath> port collects the element type into
        // $defs behind a $ref.
        [Test]
        public void DescribeGeneratesB8SchemasWithConstraintsAndDefs()
        {
            var description = Describe(new DescribeOperationRequest(AlphaId));

            var text = description.Inputs!.Single(p => p.Port.Name == "text");
            Assert.That(text.Port.Required, Is.True);
            Assert.That(text.Schema.CanonicalText, Does.Contain("maxLength"));

            var targets = description.Inputs!.Single(p => p.Port.Name == "targets");
            Assert.That(targets.Schema.CanonicalText, Does.Contain("$defs"));
            Assert.That(targets.Schema.CanonicalText, Does.Contain("$ref"));
        }

        [Test]
        public void DescribeReturnsRequirementDescriptions()
        {
            var description = Describe(new DescribeOperationRequest(PathsId));

            Assert.That(description.Requirements, Has.Count.EqualTo(3));
            var byKind = description.Requirements!
                .ToDictionary(r => r.Requirement.Kind, r => r);
            Assert.That(byKind["path_writable"].Requirement.Port, Is.EqualTo("path"));
            Assert.That(byKind["path_writable"].Description, Does.Contain("writable"));
            Assert.That(byKind["path_writable"].Description, Does.Contain("'path'"));
            Assert.That(byKind["type_exists"].Description, Does.Contain("'typeName'"));
            Assert.That(byKind["not_compiling"].Description, Does.Contain("compil"));
        }

        [Test]
        public void DescribeResolvesTheRequestedLanguage()
        {
            var english = Describe(new DescribeOperationRequest(AlphaId));
            var japanese = Describe(new DescribeOperationRequest(AlphaId) { Lang = "ja" });
            var defaulted = new OperationDescribeService(s_snapshot)
                .DescribeOperation(new DescribeOperationRequest(AlphaId), "ja").Value!;

            Assert.That(english.DisplayName, Is.EqualTo("Echo"));
            Assert.That(japanese.DisplayName, Is.EqualTo("エコー"));
            Assert.That(japanese.Summary, Is.EqualTo("文字列をそのまま返す。"));
            Assert.That(defaulted.DisplayName, Is.EqualTo("エコー"),
                "a null request language falls back to the caller default");
        }

        // The registration failure of the unreached sibling version lands in
        // the Operation's diagnostics — "その Operation に関する
        // diagnostics" (SPEC §C8) is the ID's records, not only its own.
        [Test]
        public void DescribeSurfacesTheSiblingVersionFailureInDiagnostics()
        {
            var description = Describe(new DescribeOperationRequest(MixedId));

            Assert.That(description.Version, Is.EqualTo(1));
            Assert.That(description.Diagnostics, Is.Not.Null);
            Assert.IsNotEmpty(description.Diagnostics!,
                "the failed v2 registration records diagnostics against the ID");
        }

        // ── fields projection ─────────────────────────────────────────────

        [Test]
        public void FieldsProjectionReturnsOnlyTheNamedSections()
        {
            var description = Describe(new DescribeOperationRequest(AlphaId)
            {
                Fields = new[] { "inputs", "outputs", "examples" },
            });

            Assert.That(description.Id, Is.EqualTo(AlphaId));
            Assert.That(description.Version, Is.EqualTo(2));
            Assert.That(description.Inputs, Has.Count.EqualTo(3));
            Assert.That(description.Outputs, Has.Count.EqualTo(1));
            Assert.That(description.Examples, Has.Count.EqualTo(2));

            Assert.That(description.Descriptor, Is.Null);
            Assert.That(description.DisplayName, Is.Null);
            Assert.That(description.Requirements, Is.Null);
            Assert.That(description.Origin, Is.Null);
            Assert.That(description.PackageId, Is.Null);
            Assert.That(description.AssemblyName, Is.Null);
            Assert.That(description.DescriptorHash, Is.Null);
            Assert.That(description.RegistryGeneration, Is.Null);
            Assert.That(description.Diagnostics, Is.Null);
            Assert.That(description.SourceFile, Is.Null);
        }

        [Test]
        public void EmptyFieldsReturnsOnlyTheIdentity()
        {
            var description = Describe(new DescribeOperationRequest(AlphaId)
            {
                Fields = Array.Empty<string>(),
            });

            Assert.That(description.Id, Is.EqualTo(AlphaId));
            Assert.That(description.Descriptor, Is.Null);
            Assert.That(description.Inputs, Is.Null);
            Assert.That(description.Examples, Is.Null);
            Assert.That(description.Origin, Is.Null);
            Assert.That(description.Diagnostics, Is.Null);
        }

        [Test]
        public void UnknownFieldNamesAreIgnored()
        {
            var description = Describe(new DescribeOperationRequest(AlphaId)
            {
                Fields = new[] { "inputs", "not-a-section" },
            });

            Assert.That(description.Inputs, Is.Not.Null);
            Assert.That(description.Outputs, Is.Null);
        }

        // ── Examples verbatim, and the AT-F06 validation half ────────────

        // SPEC §C8: the examples are the §C1 OperationExample values as
        // stored — title, complete workflow and the optional
        // fixtureManifest returned unchanged.
        [Test]
        public void DescribeReturnsExamplesVerbatim()
        {
            var description = Describe(new DescribeOperationRequest(AlphaId));

            Assert.That(description.Examples,
                Is.EqualTo(description.Descriptor!.Examples));

            var fixture = description.Examples![1];
            Assert.That(fixture.Title, Is.EqualTo("Echo with a fixture"));
            Assert.That(fixture.FixtureManifest, Is.Not.Null);
            Assert.That(fixture.FixtureManifest!.ToToken()
                    .SelectToken("files[0].path")?.ToString(),
                Is.EqualTo("Assets/OpWeaveExamples/Describe/Probe.txt"));
        }

        // SPEC §C8 test (AT-F06): every returned example's workflow passes
        // the static Validator with ok:true — in Core the declared fixture
        // has nothing to materialize, so the check is that the manifest
        // round-trips and the workflow itself validates.
        [Test]
        public void EveryReturnedExampleWorkflowValidatesOk()
        {
            var compiler = Compiler();

            foreach (var id in new[] { AlphaId, PathsId, MixedId })
            {
                var description = Describe(new DescribeOperationRequest(id));
                Assert.That(description.Examples, Is.Not.Empty,
                    id + " must declare complete-WF examples (SPEC §C1 item 4)");

                foreach (var example in description.Examples!)
                {
                    var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
                    Assert.That(read.Ok, Is.True, () => string.Join(
                        "; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));

                    var report = compiler.Validate(new ValidationRequest(
                        read.Value!, null, ValidationMode.Static, ActorKind.Agent));

                    Assert.That(report.Ok, Is.True,
                        () => id + " example '" + example.Title + "': " + string.Join(
                            "; ", report.Errors.Select(
                                d => d.Code + " " + d.Message + " " + d.JsonPath)));
                    Assert.That(report.Warnings, Is.Empty,
                        () => id + " example '" + example.Title + "': " + string.Join(
                            "; ", report.Warnings.Select(
                                d => d.Code + " " + d.Message + " " + d.JsonPath)));
                }
            }
        }
    }
}
