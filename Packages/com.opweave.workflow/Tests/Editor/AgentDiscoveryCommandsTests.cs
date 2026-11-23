#nullable enable

// T-W3.2-02 [Edit] tests: local:T-W3.2-02:discovery-commands — SPEC §H1
// (search_operations, describe_operation, search_compatible_operations,
// list_types, get_workflow_schema, list_workflows, get_registry_status),
// §C6/§C7/§C8 (compact summaries, match kinds, identity + OW-OP-001/002),
// §B2 (type listing), §A2 (schema artifact), §A8 (workflow metadata +
// relevance), §C3 rules 7/9 (diff + stale), §H4.1 (settings diagnostics)
// and §H3 item 3 (opaque cursors + 64 KiB cap). AgentApi is called
// directly (no Pipeline, SPEC §H1 test 1) against the production
// UnityAgentCatalogBackend bound to fixture seams.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class AgentDiscoveryCommandsTests
    {
        private string _root = null!;
        private string _projectRoot = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveAgentDiscoveryTests",
                "t" + Guid.NewGuid().ToString("N"));
            _projectRoot = Path.Combine(_root, "project");
            Directory.CreateDirectory(_projectRoot);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        // The service under test: the fixture's real Run Store and cancel
        // seam plus the production backends bound to the same seams the
        // installer binds — the catalog backend on the probe project root,
        // a written schema artifact and the publication/diff/diagnostics
        // delegates, and the workflow backend the describe→validate
        // acceptance exercises.
        private AgentApiService Api(
            RunnerHostFixture fixture,
            RegistrySnapshot? snapshot = null,
            bool stale = false,
            RegistryDiffResult? diff = null,
            IReadOnlyList<Diagnostic>? loadDiagnostics = null,
            string agentLanguage = "en",
            bool snapshotAvailable = true) =>
            new AgentApiService(
                fixture.Host.Store,
                new AgentApiCursorCodec(new byte[32]),
                fixture.Host.Coordinator.Cancel,
                workflows: new UnityAgentWorkflowBackend(
                    _projectRoot,
                    fixture.Host.CurrentRunPolicyEnvironment,
                    () => fixture.Snapshot(),
                    StandardProviders,
                    fixture.Host.AcceptRun,
                    typeProbe: fixture.Probe,
                    editorState: fixture.Editor),
                catalog: new UnityAgentCatalogBackend(
                    _projectRoot,
                    WriteSchemaArtifact(),
                    () => snapshotAvailable ? snapshot ?? fixture.Snapshot() : null,
                    () => stale,
                    () => diff,
                    () => loadDiagnostics ?? Array.Empty<Diagnostic>(),
                    () => agentLanguage));

        // A no-backend service — the fail-closed §H1 posture when the
        // command layer was installed without its environment.
        private AgentApiService ApiWithoutCatalog(RunnerHostFixture fixture) =>
            new AgentApiService(
                fixture.Host.Store,
                new AgentApiCursorCodec(new byte[32]),
                fixture.Host.Coordinator.Cancel);

        private static TypeProviderCandidate[] StandardProviders() =>
            new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(),
                    OperationOrigin.Standard),
                new TypeProviderCandidate(
                    UnityTypeProvider.ProviderId, new UnityTypeProvider(),
                    OperationOrigin.Standard),
            };

        private string WriteSchemaArtifact()
        {
            var path = Path.Combine(_root, "opweave-1.0.schema.json");
            File.WriteAllText(path, "{\"$id\":\"probe-schema\",\"type\":\"object\"}");
            return path;
        }

        private void WriteProjectFile(string relativePath, string text)
        {
            var full = Path.Combine(_projectRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        // A registered Operation with arbitrary ports — the compatible-
        // search and describe fixtures need real port typing.
        private sealed class PortOperation : IOperation
        {
            internal PortOperation(
                string id, int version = 1,
                IReadOnlyList<(string Name, string Type, bool Required)>? inputs = null,
                IReadOnlyList<(string Name, string Type)>? outputs = null,
                IReadOnlyList<OperationExample>? examples = null)
            {
                var builder = Op.Define(id, version).Summary("Probe operation " + id + ".");
                if (inputs is not null)
                {
                    foreach (var input in inputs)
                    {
                        builder.Input(input.Name, input.Type, input.Required);
                    }
                }
                if (outputs is not null)
                {
                    foreach (var output in outputs)
                    {
                        builder.Output(output.Name, output.Type);
                    }
                }
                var built = builder.Build();
                Descriptor = examples is null
                    ? built
                    : built with { Examples = examples };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Unsafe operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        private static void RegisterPortOp(
            RunnerHostFixture fixture, PortOperation op, char hashSeed) =>
            fixture.Register(op, hashSeed);

        // A registered Operation from a test-assembled Descriptor — the
        // reduction-order and identity-bound cases need field sizes the
        // fluent builder's fixed text cannot express.
        private static void RegisterDescriptor(
            RunnerHostFixture fixture, OperationDescriptor descriptor, char hashSeed) =>
            fixture.Operations.Add(new RegisteredOperation(
                descriptor,
                OperationOrigin.Custom,
                "Example.Operations",
                null,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64)));

        private static void RegisterStandardOp(RunnerHostFixture fixture, IOperation op, char hashSeed)
        {
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor,
                OperationOrigin.Standard,
                "OpWeave.Core",
                null,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64)));
        }

        private static JObject WorkflowDoc(string id, string title) =>
            JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"" + id + "\",\"title\":\"" + title +
                "\",\"nodes\":[{\"id\":\"n1\",\"operation\":\"test.op\",\"version\":1}]," +
                "\"controlEdges\":[],\"dataEdges\":[]}");

        private static void AssertErrorCode(AgentApiResponse response, string code)
        {
            Assert.IsTrue(
                response.Errors.Any(d => d.Code == code),
                $"expected error {code}; got: {Describe(response.Errors)}");
        }

        private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

        // ── search_operations (SPEC §H1, §C6) ──────────────────────────

        // The §C6 compact summary: every documented member present, every
        // item inside the §H3 test-2 bound of 600 UTF-8 bytes.
        [Test]
        public void SearchOperations_ReturnsCompactItemsWithinByteBound()
        {
            var fixture = NewFixture();
            RegisterPortOp(fixture, new PortOperation("test.search.one"), 'a');

            var response = Api(fixture).SearchOperations(new SearchOperationsRequest());

            Assert.IsTrue(response.Ok, "search failed: " + Describe(response.Errors));
            var data = (JObject)response.Data!;
            var items = (JArray)data["items"]!;
            Assert.AreEqual(1, items.Count);
            var item = (JObject)items[0]!;
            Assert.AreEqual("test.search.one", (string?)item["id"]);
            Assert.AreEqual(1, (int?)item["version"]);
            Assert.AreEqual("custom", (string?)item["origin"]);
            Assert.IsNotNull((string?)item["summary"]);
            Assert.IsNotNull(item["inputs"]);
            Assert.IsNotNull(item["outputs"]);
            Assert.IsNotNull(item["effects"]);
            Assert.IsFalse((bool?)item["destructive"]);
            Assert.IsNotNull((string?)item["mode"]);
            Assert.IsTrue(data["nextCursor"]!.Type == JTokenType.Null,
                "a one-page listing reports nextCursor null (SPEC §C6 example shape)");
            var bytes = System.Text.Encoding.UTF8.GetByteCount(
                CanonicalJson.Serialize(item));
            Assert.LessOrEqual(bytes, 600, "SPEC §H3 test 2: one item stays under 600 bytes");
        }

        // The §C6 filters combine conjunctively; the origin filter maps
        // "standard"/"custom" onto the registry origin.
        [Test]
        public void SearchOperations_FiltersNarrowTheResultSet()
        {
            var fixture = NewFixture();
            RegisterPortOp(fixture, new PortOperation(
                "test.search.producer",
                outputs: new[] { ("text", "core.String") }), 'c');
            RegisterPortOp(fixture, new PortOperation(
                "test.search.consumer",
                inputs: new[] { ("text", "core.String", true) }), 'e');

            var produced = Api(fixture).SearchOperations(
                new SearchOperationsRequest(ProducesType: "core.String"));
            Assert.IsTrue(produced.Ok, Describe(produced.Errors));
            var producedIds = ((JArray)((JObject)produced.Data!)["items"]!)
                .Select(i => (string?)((JObject)i)["id"]).ToList();
            Assert.Contains("test.search.producer", producedIds);
            Assert.IsFalse(producedIds.Contains("test.search.consumer"));

            var accepted = Api(fixture).SearchOperations(
                new SearchOperationsRequest(AcceptsType: "core.String"));
            Assert.IsTrue(accepted.Ok, Describe(accepted.Errors));
            var acceptedIds = ((JArray)((JObject)accepted.Data!)["items"]!)
                .Select(i => (string?)((JObject)i)["id"]).ToList();
            Assert.Contains("test.search.consumer", acceptedIds);
            Assert.IsFalse(acceptedIds.Contains("test.search.producer"));

            var custom = Api(fixture).SearchOperations(
                new SearchOperationsRequest(Origin: "custom"));
            Assert.IsTrue(custom.Ok, Describe(custom.Errors));
            Assert.AreEqual(2, ((JArray)((JObject)custom.Data!)["items"]!).Count);
        }

        // A non-standard/custom origin is request-shape misuse, not an
        // empty result.
        [Test]
        public void SearchOperations_BadOrigin_ReturnsOwApi001()
        {
            var fixture = NewFixture();
            var response = Api(fixture).SearchOperations(
                new SearchOperationsRequest(Origin: "third-party"));
            Assert.IsFalse(response.Ok);
            AssertErrorCode(response, "OW-API-001");
        }

        // The §C6 item-4 page: limit caps the page, the opaque
        // nextCursor continues inside the same query context only —
        // replaying it under a different query is OW-API-002.
        [Test]
        public void SearchOperations_PaginationCursorIsBoundToTheQuery()
        {
            var fixture = NewFixture();
            RegisterPortOp(fixture, new PortOperation("test.page.alpha"), 'a');
            RegisterPortOp(fixture, new PortOperation("test.page.beta"), 'c');
            RegisterPortOp(fixture, new PortOperation("test.page.gamma"), 'e');
            var api = Api(fixture);

            var first = api.SearchOperations(new SearchOperationsRequest(Limit: 2));
            Assert.IsTrue(first.Ok, Describe(first.Errors));
            var firstPage = (JObject)first.Data!;
            Assert.AreEqual(2, ((JArray)firstPage["items"]!).Count);
            var cursor = (string?)firstPage["nextCursor"];
            Assert.IsNotNull(cursor, "the remaining item continues on nextCursor");
            Assert.IsNotNull(first.NextCursor, "the envelope cursor agrees");

            var second = api.SearchOperations(
                new SearchOperationsRequest(Limit: 2, Cursor: cursor));
            Assert.IsTrue(second.Ok, Describe(second.Errors));
            var secondPage = (JObject)second.Data!;
            Assert.AreEqual(1, ((JArray)secondPage["items"]!).Count);
            Assert.IsTrue(secondPage["nextCursor"]!.Type == JTokenType.Null);

            var rebound = api.SearchOperations(
                new SearchOperationsRequest(Query: "gamma", Cursor: cursor));
            Assert.IsFalse(rebound.Ok);
            AssertErrorCode(rebound, "OW-API-002");
        }

        // The §H3 test-2 bound covers the whole item, not just the
        // summary: a descriptor whose port maps alone exceed 600 UTF-8
        // bytes is reduced whole-entry after the display texts yield —
        // the identity and every kept port name survive intact and the
        // emitted item stays valid JSON with the 'truncated' marker.
        [Test]
        public void SearchOperations_WideItem_ReducesWithinByteBound()
        {
            var fixture = NewFixture();
            var inputs = new List<(string Name, string Type, bool Required)>();
            var outputs = new List<(string Name, string Type)>();
            for (var i = 0; i < 16; i++)
            {
                inputs.Add(("aLongInputPortNameOfSomeWidth" + i, "core.String", i == 0));
                outputs.Add(("aLongOutputPortNameOfSomeWidth" + i, "core.String"));
            }
            RegisterPortOp(fixture, new PortOperation(
                "test.search.wide", inputs: inputs, outputs: outputs), 'a');

            var response = Api(fixture).SearchOperations(new SearchOperationsRequest());

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var items = (JArray)((JObject)response.Data!)["items"]!;
            Assert.AreEqual(1, items.Count);
            var item = (JObject)items[0]!;
            var bytes = System.Text.Encoding.UTF8.GetByteCount(
                CanonicalJson.Serialize(item));
            Assert.LessOrEqual(
                bytes, 600,
                "SPEC §H3 test 2: even a wide non-summary projection stays under 600 bytes");
            Assert.IsTrue((bool?)item["truncated"],
                "a reduced item declares 'truncated' so the Agent asks describe_operation");
            Assert.AreEqual(
                "test.search.wide", (string?)item["id"],
                "the identity member is never reduced");
            Assert.IsTrue(
                JToken.DeepEquals(item, JToken.Parse(CanonicalJson.Serialize(item))),
                "the reduced item round-trips as valid canonical JSON");
            Assert.AreEqual("", (string?)item["summary"],
                "SPEC §H3 order: the summary empties completely before collection entries drop");
            Assert.AreEqual("", (string?)item["displayName"],
                "SPEC §H3 order: displayName yields right after summary");
            Assert.AreEqual(0, ((JObject)item["outputs"]!).Count,
                "SPEC §H3 order: outputs exhaust completely before inputs yield");
            var keptInputs = ((JObject)item["inputs"]!)
                .Properties().Select(p => p.Name).ToList();
            Assert.Less(keptInputs.Count, inputs.Count,
                "entries drop whole once the display texts are exhausted");
            CollectionAssert.AreEqual(
                inputs.Select(i => i.Name).Take(keptInputs.Count).ToList(), keptInputs,
                "entries drop from the last declared port — the survivors keep declaration order");
        }

        // The §H3 reduction stops at the first step that makes the item
        // fit: a summary that alone pushes the item over the bound
        // yields partway and every later field — displayName, the port
        // maps and effects — stays untouched, with the marker set.
        [Test]
        public void SearchOperations_SmallOverflow_ReducesSummaryOnly()
        {
            var fixture = NewFixture();
            var summary = new string('s', 600);
            var descriptor = Op.Define("test.reduce.text", 1)
                .Summary(summary)
                .Input("text", "core.String", required: true)
                .Output("done", "core.String")
                .Effects("scene.modify")
                .Build() with { DisplayName = "Untouched Name" };
            RegisterDescriptor(fixture, descriptor, 'u');

            var response = Api(fixture).SearchOperations(new SearchOperationsRequest());

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var item = (JObject)((JArray)((JObject)response.Data!)["items"]!)[0]!;
            var bytes = System.Text.Encoding.UTF8.GetByteCount(
                CanonicalJson.Serialize(item));
            Assert.LessOrEqual(bytes, 600, "SPEC §H3: the item fits the bound");
            Assert.IsTrue((bool?)item["truncated"],
                "a reduced item declares the marker");
            var trimmed = (string?)item["summary"];
            Assert.IsNotNull(trimmed);
            Assert.Greater(trimmed!.Length, 0, "the trim stopped at the first fit");
            Assert.Less(trimmed.Length, summary.Length);
            StringAssert.StartsWith(trimmed, summary,
                "the summary is trimmed from the end — a prefix survives");
            Assert.AreEqual("Untouched Name", (string?)item["displayName"],
                "displayName is untouched while summary still yields bytes");
            Assert.AreEqual(1, ((JObject)item["outputs"]!).Count, "outputs untouched");
            Assert.AreEqual(1, ((JObject)item["inputs"]!).Count, "inputs untouched");
            Assert.AreEqual(1, ((JArray)item["effects"]!).Count, "effects untouched");
            Assert.AreEqual("test.reduce.text", (string?)item["id"]);
        }

        // SPEC §H3 bound + §A4 through the Agent surface: a grammar-valid
        // 128-character Operation id leaves room for the identity members
        // under the bound — the reduced item keeps the full id and the
        // unreduced version/origin/destructive/mode while every other
        // field yields.
        [Test]
        public void SearchOperations_MaxLengthId_KeepsFullIdentity()
        {
            var fixture = NewFixture();
            var id = "test." + new string('m', IdGrammar.OperationIdMaxLength - 5);
            Assert.AreEqual(IdGrammar.OperationIdMaxLength, id.Length);
            Assert.IsTrue(
                IdGrammar.IsValidOperationId(id),
                "the fixture id is a grammar-valid 128-character Operation id");
            var inputs = new List<PortDescriptor>();
            var outputs = new List<PortDescriptor>();
            for (var i = 0; i < 12; i++)
            {
                inputs.Add(new PortDescriptor(
                    "anInputPortOfSomeWidth" + i, "core.String", i == 0, null, string.Empty, null));
                outputs.Add(new PortDescriptor(
                    "anOutputPortOfSomeWidth" + i, "core.String", false, null, string.Empty, null));
            }
            var descriptor = Op.Define(id, 3)
                .Summary(new string('s', 500))
                .Build() with
                {
                    DisplayName = new string('d', 400),
                    Inputs = inputs,
                    Outputs = outputs,
                    SideEffects = Enumerable.Range(0, 10)
                        .Select(i => "effect.name." + i + "." + new string('e', 40))
                        .ToArray(),
                    Destructive = true,
                };
            RegisterDescriptor(fixture, descriptor, 'w');

            var response = Api(fixture).SearchOperations(new SearchOperationsRequest());

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var item = (JObject)((JArray)((JObject)response.Data!)["items"]!)[0]!;
            var bytes = System.Text.Encoding.UTF8.GetByteCount(
                CanonicalJson.Serialize(item));
            Assert.LessOrEqual(
                bytes, 600,
                "the identity members always fit the bound (SPEC §H3): got " + bytes);
            Assert.AreEqual(id, (string?)item["id"],
                "the complete Operation id survives (SPEC §C6 item 6)");
            Assert.AreEqual(3, (int?)item["version"], "version is never reduced");
            Assert.AreEqual("custom", (string?)item["origin"], "origin is never reduced");
            Assert.IsTrue((bool?)item["destructive"], "destructive is never reduced");
            Assert.AreEqual("immediate", (string?)item["mode"], "mode is never reduced");
            Assert.IsTrue((bool?)item["truncated"]);
        }

        // SPEC §A4 + T-W1.3-09: a manually supplied Operation id over 128
        // characters is rejected at registration with OW-OP-003 — the
        // registration still publishes its sibling candidates, and the
        // rejected id can never reach a search item. The test builds its
        // own RegistrySnapshot through RegistrySnapshotBuilder from test
        // Descriptors; nothing probes the TestProject's live Registry.
        [Test]
        public void SearchOperations_OverlongId_NeverReachesSearch()
        {
            var fixture = NewFixture();
            var overlong = Op.Define(
                    "test." + new string('z', IdGrammar.OperationIdMaxLength - 4), 1)
                .Summary("Rejected probe.")
                .Build();
            Assert.AreEqual(IdGrammar.OperationIdMaxLength + 1, overlong.Id.Length);
            var kept = Op.Define("test.registry.kept", 1)
                .Summary("Kept probe.")
                .Build();
            var snapshot = RegistrySnapshotBuilder.Build(
                new[]
                {
                    new OperationCandidate(
                        overlong, OperationOrigin.Custom, "Example.Operations",
                        "Assets/ops/overlong.cs", new string('c', 64),
                        HasPollImplementation: false),
                    new OperationCandidate(
                        kept, OperationOrigin.Custom, "Example.Operations",
                        "Assets/ops/kept.cs", new string('d', 64),
                        HasPollImplementation: false),
                },
                RunnerHostFixture.Types);
            Assert.IsTrue(
                snapshot.CandidateFailures.Any(f =>
                    f.Diagnostics.Any(d => d.Code == "OW-OP-003")),
                "the overlong id is an OW-OP-003 registration rejection");

            var response = Api(fixture, snapshot: snapshot)
                .SearchOperations(new SearchOperationsRequest());

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var ids = ((JArray)((JObject)response.Data!)["items"]!)
                .Select(i => (string?)((JObject)i)["id"]).ToList();
            Assert.Contains("test.registry.kept", ids,
                "the sibling Operation of the same registration stays searchable");
            Assert.IsFalse(ids.Contains(overlong.Id),
                "the overlong id can never appear in search_operations");
        }

        // ── describe_operation (SPEC §H1, §C8) ─────────────────────────

        // The §C8 contract: identity plus the full projection — ports with
        // their §B8 schemas, requirements, a complete-WF example, origin,
        // assembly, fingerprints and the answering registry generation.
        [Test]
        public void DescribeOperation_ReturnsAllSections()
        {
            var fixture = NewFixture();
            var example = RunnerHostFixture.Json(
                "{\"schemaVersion\":\"1.0\",\"id\":\"wf.example\",\"nodes\":[" +
                "{\"id\":\"n1\",\"operation\":\"test.describe\",\"version\":1}]," +
                "\"controlEdges\":[]}");
            RegisterPortOp(fixture, new PortOperation(
                "test.describe",
                inputs: new[] { ("text", "core.String", true) },
                outputs: new[] { ("done", "core.String") },
                examples: new[] { new OperationExample("basic", example) }), 'g');

            var response = Api(fixture).DescribeOperation(
                new DescribeOperationRequest("test.describe"));

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var data = (JObject)response.Data!;
            Assert.AreEqual("test.describe", (string?)data["id"]);
            Assert.AreEqual(1, (int?)data["version"]);
            var descriptor = data["descriptor"] as JObject;
            Assert.IsNotNull(descriptor, "the full Descriptor section is present");
            var inputs = (JArray)data["inputs"]!;
            Assert.AreEqual(1, inputs.Count);
            var port = (JObject)inputs[0]!;
            Assert.AreEqual("text", (string?)port["name"]);
            Assert.AreEqual("core.String", (string?)port["type"]);
            Assert.IsTrue((bool?)port["required"]);
            Assert.IsNotNull(port["schema"], "every described port carries its §B8 schema");
            var examples = (JArray)data["examples"]!;
            Assert.AreEqual(1, examples.Count);
            var workflow = ((JObject)examples[0]!)["workflow"] as JObject;
            Assert.IsNotNull(workflow, "the example is the complete workflow, not a fragment");
            Assert.AreEqual("wf.example", (string?)workflow!["id"]);
            Assert.AreEqual("custom", (string?)data["origin"]);
            Assert.AreEqual("Example.Operations", (string?)data["assemblyName"]);
            var fingerprints = (JObject)data["fingerprints"]!;
            Assert.AreEqual(new string('g', 64), (string?)fingerprints["descriptorHash"]);
            Assert.AreEqual(new string('h', 64), (string?)fingerprints["implementationHash"]);
            Assert.AreEqual("reg_01J00000000000000000000000", (string?)data["registryGeneration"]);
        }

        // The fields projection narrows the response — the identity stays.
        [Test]
        public void DescribeOperation_FieldsProjectionNarrowsTheResponse()
        {
            var fixture = NewFixture();
            RegisterPortOp(fixture, new PortOperation("test.describe.narrow"), 'i');

            var response = Api(fixture).DescribeOperation(
                new DescribeOperationRequest(
                    "test.describe.narrow",
                    Fields: new[] { "inputs", "outputs" }));

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var data = (JObject)response.Data!;
            Assert.AreEqual("test.describe.narrow", (string?)data["id"]);
            Assert.IsNotNull(data["inputs"]);
            Assert.IsNotNull(data["outputs"]);
            Assert.IsNull(data["descriptor"], "an unlisted section stays Missing");
            Assert.IsNull(data["examples"]);
            Assert.IsNull(data["fingerprints"]);
        }

        // An unknown Operation ID is OW-OP-001; a registered ID at an
        // unregistered version is OW-OP-002.
        [Test]
        public void DescribeOperation_UnknownIdAndVersion_ReturnOwOp001And002()
        {
            var fixture = NewFixture();
            RegisterPortOp(fixture, new PortOperation("test.describe.known"), 'k');
            var api = Api(fixture);

            var missing = api.DescribeOperation(new DescribeOperationRequest("test.no.such"));
            Assert.IsFalse(missing.Ok);
            AssertErrorCode(missing, "OW-OP-001");

            var version = api.DescribeOperation(
                new DescribeOperationRequest("test.describe.known", Version: 9));
            Assert.IsFalse(version.Ok);
            AssertErrorCode(version, "OW-OP-002");

            var malformed = api.DescribeOperation(new DescribeOperationRequest(null));
            Assert.IsFalse(malformed.Ok);
            AssertErrorCode(malformed, "OW-API-001");
        }

        // SPEC §C1 item 4 / §C8 acceptance: every example a describe
        // answer carries is a complete statically validatable workflow —
        // feeding each one back through validate_workflow must succeed,
        // or the published contract promises a workflow that cannot
        // compile.
        [Test]
        public void DescribeOperation_EveryExample_Validates()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation("core.start", fixture.Events), 's');
            RegisterPortOp(fixture, new PortOperation(
                "test.examples.alpha",
                outputs: new[] { ("text", "core.String") },
                examples: new[]
                {
                    new OperationExample("alone", RunnerHostFixture.Json(
                        ExampleWorkflow("wf.example.alpha", "test.examples.alpha"))),
                }), 'a');
            RegisterPortOp(fixture, new PortOperation(
                "test.examples.beta",
                inputs: new[] { ("text", "core.String", false) },
                examples: new[]
                {
                    new OperationExample("first", RunnerHostFixture.Json(
                        ExampleWorkflow("wf.example.beta1", "test.examples.beta"))),
                    new OperationExample("second", RunnerHostFixture.Json(
                        ExampleWorkflow("wf.example.beta2", "test.examples.beta"))),
                }), 'c');
            var api = Api(fixture);

            var validated = 0;
            foreach (var operation in fixture.Operations)
            {
                var described = api.DescribeOperation(new DescribeOperationRequest(
                    operation.Descriptor.Id, Version: operation.Descriptor.Version));
                Assert.IsTrue(described.Ok, Describe(described.Errors));
                if (!(((JObject)described.Data!)["examples"] is JArray examples))
                {
                    continue;
                }
                foreach (var example in examples)
                {
                    var workflow = ((JObject)example)["workflow"] as JObject;
                    Assert.IsNotNull(
                        workflow,
                        "describe_operation carried a non-workflow example for " +
                        operation.Descriptor.Id);
                    var response = api.ValidateWorkflow(
                        new ValidateWorkflowRequest(workflow, null));
                    Assert.IsTrue(
                        response.Ok,
                        "the '" + operation.Descriptor.Id + "' example failed " +
                        "validate_workflow: " + Describe(response.Errors));
                    validated++;
                }
            }
            Assert.AreEqual(3, validated,
                "every example the fixture's describe answers carry was exercised");
        }

        // A complete §C1 item-4 example document: schema header, the
        // core.start node, the operation under description and the
        // control edge — the same shape the workflow-command tests
        // validate.
        private static string ExampleWorkflow(string id, string operationId) =>
            "{\"schemaVersion\":\"1.0\",\"id\":\"" + id + "\",\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"op\",\"operation\":\"" + operationId + "\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"op\"}]}";

        // ── search_compatible_operations (SPEC §H1, §C7) ───────────────

        // The §C7 type form: downstream of "core.String" the consumer
        // matches exactly through its input port.
        [Test]
        public void SearchCompatibleOperations_TypeFormMatchesByDirection()
        {
            var fixture = NewFixture();
            RegisterPortOp(fixture, new PortOperation(
                "test.compat.producer",
                outputs: new[] { ("text", "core.String") }), 'a');
            RegisterPortOp(fixture, new PortOperation(
                "test.compat.consumer",
                inputs: new[] { ("text", "core.String", true) }), 'c');
            var api = Api(fixture);

            var downstream = api.SearchCompatibleOperations(
                new SearchCompatibleOperationsRequest(
                    Type: "core.String", Direction: "downstream"));
            Assert.IsTrue(downstream.Ok, Describe(downstream.Errors));
            var matches = (JArray)((JObject)downstream.Data!)["matches"]!;
            var downstreamIds = matches
                .Select(m => (string?)((JObject)m)["operation"]).ToList();
            Assert.Contains("test.compat.consumer", downstreamIds);
            Assert.IsFalse(downstreamIds.Contains("test.compat.producer"));
            var match = (JObject)matches.First(m =>
                (string?)((JObject)m)["operation"] == "test.compat.consumer")!;
            Assert.AreEqual("text", (string?)match["port"]);
            Assert.AreEqual("exact", (string?)match["match"]);
            Assert.AreEqual("custom", (string?)match["origin"]);

            var upstream = api.SearchCompatibleOperations(
                new SearchCompatibleOperationsRequest(
                    Type: "core.String", Direction: "upstream"));
            Assert.IsTrue(upstream.Ok, Describe(upstream.Errors));
            var upstreamIds = ((JArray)((JObject)upstream.Data!)["matches"]!)
                .Select(m => (string?)((JObject)m)["operation"]).ToList();
            Assert.Contains("test.compat.producer", upstreamIds);
            Assert.IsFalse(upstreamIds.Contains("test.compat.consumer"));
        }

        // The §C7 operation/port form: a port of a registered Operation
        // answers the same match set as the equivalent type query.
        [Test]
        public void SearchCompatibleOperations_OperationPortFormMatches()
        {
            var fixture = NewFixture();
            RegisterPortOp(fixture, new PortOperation(
                "test.compat.source",
                outputs: new[] { ("text", "core.String") }), 'e');
            RegisterPortOp(fixture, new PortOperation(
                "test.compat.sink",
                inputs: new[] { ("text", "core.String", true) }), 'g');

            var response = Api(fixture).SearchCompatibleOperations(
                new SearchCompatibleOperationsRequest(
                    Operation: "test.compat.source", Port: "text",
                    Direction: "downstream"));

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var matches = (JArray)((JObject)response.Data!)["matches"]!;
            Assert.IsTrue(matches.Any(m =>
                (string?)((JObject)m)["operation"] == "test.compat.sink"));
        }

        // Exactly one query form: both, neither, half of the operation
        // pair and a missing direction are all OW-API-001.
        [Test]
        public void SearchCompatibleOperations_FormMisuse_ReturnsOwApi001()
        {
            var fixture = NewFixture();
            var api = Api(fixture);

            var combined = api.SearchCompatibleOperations(
                new SearchCompatibleOperationsRequest(
                    Type: "core.String", Operation: "test.x", Port: "p",
                    Direction: "downstream"));
            Assert.IsFalse(combined.Ok);
            AssertErrorCode(combined, "OW-API-001");

            var neither = api.SearchCompatibleOperations(
                new SearchCompatibleOperationsRequest(Direction: "downstream"));
            Assert.IsFalse(neither.Ok);
            AssertErrorCode(neither, "OW-API-001");

            var halfPair = api.SearchCompatibleOperations(
                new SearchCompatibleOperationsRequest(
                    Operation: "test.x", Direction: "downstream"));
            Assert.IsFalse(halfPair.Ok);
            AssertErrorCode(halfPair, "OW-API-001");

            var noDirection = api.SearchCompatibleOperations(
                new SearchCompatibleOperationsRequest(Type: "core.String"));
            Assert.IsFalse(noDirection.Ok);
            AssertErrorCode(noDirection, "OW-API-001");
        }

        // ── list_types (SPEC §H1, §B2) ─────────────────────────────────

        // The §B2 listing: deterministic snapshot order, the documented
        // members, and the prefix filter narrowing to a namespace.
        [Test]
        public void ListTypes_ListsRegisteredTypesAndFiltersByPrefix()
        {
            var fixture = NewFixture();
            var api = Api(fixture);

            var all = api.ListTypes(new ListTypesRequest());
            Assert.IsTrue(all.Ok, Describe(all.Errors));
            var types = (JArray)((JObject)all.Data!)["types"]!;
            Assert.Greater(types.Count, 0);
            var first = (JObject)types[0]!;
            Assert.IsNotNull((string?)first["id"]);
            Assert.IsNotNull((string?)first["kind"]);
            Assert.IsNotNull((string?)first["description"]);
            Assert.IsNotNull(first["baseTypes"]);
            var ids = types.Select(t => (string?)((JObject)t)["id"]).ToList();
            CollectionAssert.IsOrdered(ids, "the listing follows the snapshot order");

            var scoped = api.ListTypes(new ListTypesRequest(Prefix: "core."));
            Assert.IsTrue(scoped.Ok, Describe(scoped.Errors));
            var scopedIds = ((JArray)((JObject)scoped.Data!)["types"]!)
                .Select(t => (string?)((JObject)t)["id"]).ToList();
            Assert.IsTrue(scopedIds.All(id => id!.StartsWith("core.", StringComparison.Ordinal)));
            Assert.Less(scopedIds.Count, ids.Count);
            Assert.Contains("core.String", scopedIds);
        }

        // ── get_workflow_schema (SPEC §H1, §A2) ────────────────────────

        // The command returns the shipped artifact verbatim — the
        // probe path stands in for the package's resolved Schemas~ file.
        [Test]
        public void GetWorkflowSchema_ReturnsTheArtifactVerbatim()
        {
            var fixture = NewFixture();

            var response = Api(fixture).GetWorkflowSchema(new GetWorkflowSchemaRequest());

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var data = (JObject)response.Data!;
            Assert.AreEqual("probe-schema", (string?)data["$id"]);
            Assert.AreEqual("object", (string?)data["type"]);
        }

        // ── list_workflows (SPEC §H1, §A8) ─────────────────────────────

        // The §A8 metadata listing: project-relative paths, declared
        // metadata (id/title/tags), and the whitespace-term query
        // matching against id/title/description/tags.
        [Test]
        public void ListWorkflows_ListsMetadataAndMatchesQueryTerms()
        {
            var fixture = NewFixture();
            WriteProjectFile(
                "wf/alpha.opweave.json",
                WorkflowDoc("wf.alpha", "Alpha import").ToString(
                    Newtonsoft.Json.Formatting.None));
            WriteProjectFile(
                "wf/beta.opweave.json",
                WorkflowDoc("wf.beta", "Beta export").ToString(
                    Newtonsoft.Json.Formatting.None));
            var api = Api(fixture);

            var all = api.ListWorkflows(new ListWorkflowsRequest(Folder: "wf"));
            Assert.IsTrue(all.Ok, Describe(all.Errors));
            var workflows = (JArray)((JObject)all.Data!)["workflows"]!;
            Assert.AreEqual(2, workflows.Count);
            var first = (JObject)workflows[0]!;
            Assert.AreEqual("wf.alpha", (string?)first["id"]);
            Assert.AreEqual("Alpha import", (string?)first["title"]);
            Assert.AreEqual("wf/alpha.opweave.json", (string?)first["path"]);

            var matched = api.ListWorkflows(
                new ListWorkflowsRequest(Query: "alpha", Folder: "wf"));
            Assert.IsTrue(matched.Ok, Describe(matched.Errors));
            var matchedList = (JArray)((JObject)matched.Data!)["workflows"]!;
            Assert.AreEqual(1, matchedList.Count);
            Assert.AreEqual("wf.alpha", (string?)((JObject)matchedList[0]!)["id"]);
        }

        // A document that cannot be parsed and an ID declared twice
        // surface as warnings — the listing itself still answers.
        [Test]
        public void ListWorkflows_UnreadableAndDuplicateFiles_WarnNotFail()
        {
            var fixture = NewFixture();
            WriteProjectFile(
                "wf/good.opweave.json",
                WorkflowDoc("wf.good", "Good").ToString(Newtonsoft.Json.Formatting.None));
            WriteProjectFile("wf/broken.opweave.json", "{ not json");
            WriteProjectFile(
                "wf/dup.opweave.json",
                WorkflowDoc("wf.good", "Duplicate").ToString(Newtonsoft.Json.Formatting.None));
            var api = Api(fixture);

            var response = api.ListWorkflows(new ListWorkflowsRequest(Folder: "wf"));

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var workflows = (JArray)((JObject)response.Data!)["workflows"]!;
            Assert.AreEqual(2, workflows.Count, "the readable pair is still listed");
            Assert.IsTrue(
                response.Warnings.Any(d => d.Code == "OW-WF-001"),
                "the unreadable file warns: " + Describe(response.Warnings));
            Assert.IsTrue(
                response.Warnings.Any(d => d.Code == "OW-WF-004"),
                "the duplicate id warns: " + Describe(response.Warnings));
        }

        // A folder escaping the project root is OW-API-001 — the
        // containment check happens before any file-system walk.
        [Test]
        public void ListWorkflows_FolderEscape_ReturnsOwApi001()
        {
            var fixture = NewFixture();
            var api = Api(fixture);

            var escaped = api.ListWorkflows(new ListWorkflowsRequest(Folder: ".."));
            Assert.IsFalse(escaped.Ok);
            AssertErrorCode(escaped, "OW-API-001");
        }

        // ── get_registry_status (SPEC §H1, §C3) ────────────────────────

        // The §C3 status: generation/hash, per-origin counts, candidate
        // failures with their §J diagnostics, exclusions, the §C3 rule-9
        // stale flag and the §H4.1 settings diagnostics.
        [Test]
        public void GetRegistryStatus_ReportsGenerationCountsAndFailures()
        {
            var fixture = NewFixture();
            RegisterPortOp(fixture, new PortOperation("test.status.custom"), 'm');
            RegisterStandardOp(fixture, new PortOperation("test.status.standard"), 'o');
            var snapshot = new RegistrySnapshot(
                "reg_01JZZZZZZZZZZZZZZZZZZZZZ",
                new string('e', 64),
                fixture.Operations,
                RunnerHostFixture.Types,
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>())
            {
                CandidateFailures = new[]
                {
                    new RegistryCandidateFailure(
                        RegistryCandidateKind.Operation,
                        "test.rejected",
                        null,
                        "Example.Operations",
                        "Assets/ops/rejected.cs",
                        new[]
                        {
                            ErrorCodes.Create(ErrorCodes.OW_OP_015, "Rejected.")
                        }),
                },
                Exclusions = new[]
                {
                    new RegistryCandidateExclusion(
                        RegistryCandidateKind.Operation,
                        "test.excluded",
                        1,
                        "com.example.pkg",
                        "Example.Operations",
                        null,
                        "Not whitelisted."),
                },
            };
            var api = Api(
                fixture, snapshot: snapshot, stale: true,
                loadDiagnostics: new[]
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_CFG_002, "Invalid value.", jsonPath: "$.policy.human"),
                });

            var response = api.GetRegistryStatus(new GetRegistryStatusRequest());

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var data = (JObject)response.Data!;
            Assert.AreEqual("reg_01JZZZZZZZZZZZZZZZZZZZZZ", (string?)data["generationId"]);
            Assert.AreEqual(new string('e', 64), (string?)data["registryHash"]);
            Assert.IsTrue((bool?)data["stale"], "the §C3 rule-9 flag rides through");
            var counts = (JObject)data["counts"]!;
            Assert.AreEqual(1, (int?)counts["standard"]);
            Assert.AreEqual(1, (int?)counts["custom"]);
            var failures = (JArray)data["failures"]!;
            Assert.AreEqual(1, failures.Count);
            var failure = (JObject)failures[0]!;
            Assert.AreEqual("operation", (string?)failure["kind"]);
            Assert.AreEqual("test.rejected", (string?)failure["id"]);
            Assert.AreEqual(
                "OW-OP-015",
                (string?)((JObject)((JArray)failure["diagnostics"]!)[0]!)["code"],
                "the candidate failure keeps its §J code");
            var exclusions = (JArray)data["exclusions"]!;
            Assert.AreEqual(1, exclusions.Count);
            Assert.AreEqual(
                "test.excluded", (string?)((JObject)exclusions[0]!)["id"]);
            var settings = (JArray)data["settingsDiagnostics"]!;
            Assert.AreEqual(
                "OW-CFG-002", (string?)((JObject)settings[0]!)["code"]);
            Assert.AreEqual(
                "$.policy.human", (string?)((JObject)settings[0]!)["jsonPath"],
                "§H4.1: the settings error keeps its JsonPath");
            var diff = (JObject)data["diff"]!;
            Assert.AreEqual("unavailable", (string?)diff["previousSnapshot"],
                "no recorded pass reports the §C3 rule-7 unavailable state");
        }

        // The §C3 rule-7 'since' binding: the current generation answers
        // an empty change list; the persisted previous generation answers
        // the recorded batch; any other generation is unavailable.
        [Test]
        public void GetRegistryStatus_SinceBindsTheDiffToAGeneration()
        {
            var fixture = NewFixture();
            var previous = new PersistedRegistrySnapshot(
                "reg_01JPREV00000000000000000",
                new string('d', 64),
                Array.Empty<PersistedRegistryOperation>());
            var diffResult = new RegistryDiffResult(
                previous,
                new PersistedRegistrySnapshot(
                    "reg_01J00000000000000000000000",
                    new string('f', 64),
                    Array.Empty<PersistedRegistryOperation>()),
                new[]
                {
                    new RegistryChange(
                        RegistryChangeKinds.Added, "test.added", 1, null,
                        Array.Empty<Diagnostic>()),
                },
                ChangesRecorded: true,
                UnavailableReason: null,
                PersistenceError: null);
            var api = Api(fixture, diff: diffResult);

            var current = api.GetRegistryStatus(
                new GetRegistryStatusRequest(Since: "reg_01J00000000000000000000000"));
            Assert.IsTrue(current.Ok, Describe(current.Errors));
            var currentDiff = (JObject)((JObject)current.Data!)["diff"]!;
            Assert.AreEqual(0, ((JArray)currentDiff["changes"]!).Count,
                "the current generation has no diff against itself");

            var prev = api.GetRegistryStatus(
                new GetRegistryStatusRequest(Since: "reg_01JPREV00000000000000000"));
            Assert.IsTrue(prev.Ok, Describe(prev.Errors));
            var prevDiff = (JObject)((JObject)prev.Data!)["diff"]!;
            var changes = (JArray)prevDiff["changes"]!;
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("added", (string?)((JObject)changes[0]!)["kind"]);
            Assert.AreEqual("test.added", (string?)((JObject)changes[0]!)["operationId"]);

            var older = api.GetRegistryStatus(
                new GetRegistryStatusRequest(Since: "reg_01JOLDER000000000000000"));
            Assert.IsTrue(older.Ok, Describe(older.Errors));
            var olderDiff = (JObject)((JObject)older.Data!)["diff"]!;
            Assert.AreEqual("unavailable", (string?)olderDiff["previousSnapshot"],
                "an older generation's diff can never be guessed (SPEC §C3 rule 7)");
            Assert.IsNotNull((string?)olderDiff["unavailableReason"]);
        }

        // SPEC §C3 error paragraph / §J.1: every published rejection —
        // the type-build failures mapped through RegistrationDiagnostics
        // (OW-OP-013..016) and the operation/extension/provider-scope
        // rejections (OW-OP-017..019) — carries its code, the candidate's
        // safe location (kind/ID/provider/assembly/project-relative file)
        // and a fixed-English reason; a credential interpolated into a
        // message or an ID is masked at the publication boundary, never
        // emitted.
        [Test]
        public void GetRegistryStatus_RegistryFailures_ReportSafeLocationsAndReasons()
        {
            var fixture = NewFixture();
            var credentialUrl =
                "https://agent:s3cr3t-token@git.example.com/private/repo.git";
            var snapshot = new RegistrySnapshot(
                "reg_01JZZZZZZZZZZZZZZZZZZZZZ",
                new string('e', 64),
                fixture.Operations,
                RunnerHostFixture.Types,
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>())
            {
                CandidateFailures = new[]
                {
                    RegistrationDiagnostics.ForCandidate(new TypeRegistryFailure(
                        TypeRegistryFailureKind.DuplicateTypeId, "ext.dup",
                        "com.example.provider",
                        "Two candidates declared the same Type ID.",
                        "Example.ProviderAsm")),
                    RegistrationDiagnostics.ForCandidate(new TypeRegistryFailure(
                        TypeRegistryFailureKind.UnregisteredBaseType, "ext.child",
                        "com.example.provider",
                        "The base type 'ext.missing' is not registered.",
                        "Example.ProviderAsm")),
                    RegistrationDiagnostics.ForCandidate(new TypeRegistryFailure(
                        TypeRegistryFailureKind.BaseTypeCycle, "ext.cycle.a",
                        "com.example.provider",
                        "The type sits on a base-type cycle.",
                        "Example.ProviderAsm")),
                    RegistrationDiagnostics.ForCandidate(new TypeRegistryFailure(
                        TypeRegistryFailureKind.MissingCodec, "ext.raw",
                        "com.example.provider",
                        "The definition declares no Codec.",
                        "Example.ProviderAsm")),
                    new RegistryCandidateFailure(
                        RegistryCandidateKind.Operation,
                        "test.unresolvable", null,
                        "Example.Operations", "Assets/ops/unresolvable.cs",
                        new[]
                        {
                            ErrorCodes.Create(ErrorCodes.OW_OP_017,
                                "The defining assembly could not be resolved."),
                        }),
                    new RegistryCandidateFailure(
                        RegistryCandidateKind.TypeProvider,
                        "com.example.mismatch", null,
                        "Example.ProviderAsm", null,
                        new[]
                        {
                            ErrorCodes.Create(ErrorCodes.OW_OP_018,
                                "The manifest contradicts the compiled artifact."),
                        }),
                    new RegistryCandidateFailure(
                        RegistryCandidateKind.TypeProvider,
                        "com.example.broken", null,
                        "Example.ProviderAsm", null,
                        new[]
                        {
                            ErrorCodes.Create(ErrorCodes.OW_OP_019,
                                "The TypeProvider could not be instantiated."),
                        }),
                    new RegistryCandidateFailure(
                        RegistryCandidateKind.Operation,
                        credentialUrl, null,
                        "Example.Operations", "Assets/ops/leaky.cs",
                        new[]
                        {
                            ErrorCodes.Create(ErrorCodes.OW_OP_017,
                                "The defining assembly " + credentialUrl +
                                " could not be resolved."),
                        }),
                },
            };
            var api = Api(fixture, snapshot: snapshot);

            var response = api.GetRegistryStatus(new GetRegistryStatusRequest());

            Assert.IsTrue(response.Ok, Describe(response.Errors));
            var data = (JObject)response.Data!;
            var failures = (JArray)data["failures"]!;
            Assert.AreEqual(8, failures.Count);
            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in failures)
            {
                var failure = (JObject)token;
                Assert.IsNotNull((string?)failure["kind"],
                    "the candidate kind is the safe §C3 location");
                Assert.IsNotNull((string?)failure["id"],
                    "the affected candidate is named");
                var diagnostics = (JArray)failure["diagnostics"]!;
                Assert.AreEqual(1, diagnostics.Count);
                var diagnostic = (JObject)diagnostics[0]!;
                var code = (string?)diagnostic["code"];
                Assert.IsNotNull(code);
                Assert.IsNotNull(
                    (string?)diagnostic["message"],
                    "a fixed-English reason rides with " + code);
                codes.Add(code!);
                if (failure["sourceFile"] is { } sourceFile)
                {
                    var file = (string?)sourceFile;
                    Assert.IsFalse(
                        Path.IsPathRooted(file),
                        "no absolute machine path leaks: " + file);
                }
            }
            foreach (var expected in new[]
            {
                "OW-OP-013", "OW-OP-014", "OW-OP-015", "OW-OP-016",
                "OW-OP-017", "OW-OP-018", "OW-OP-019",
            })
            {
                Assert.IsTrue(
                    codes.Contains(expected),
                    expected + " must surface in the status answer");
            }

            // The publication boundary masked the credential: the wire
            // answer carries '***' where the userinfo stood — never the
            // secret itself.
            var wire = data.ToString(Newtonsoft.Json.Formatting.None);
            StringAssert.DoesNotContain("s3cr3t-token", wire);
            StringAssert.DoesNotContain("agent:", wire);
            StringAssert.Contains("https://***@git.example.com", wire);
        }

        // ── Fail-closed postures (SPEC §H1 test 1, §C3 rule 9) ─────────

        // Without a catalog backend every command fails closed as
        // OW-API-004 — never a partial answer.
        [Test]
        public void DiscoveryCommands_MissingBackend_ReturnOwApi004()
        {
            var fixture = NewFixture();
            var api = ApiWithoutCatalog(fixture);

            AssertErrorCode(api.SearchOperations(new SearchOperationsRequest()), "OW-API-004");
            AssertErrorCode(api.DescribeOperation(new DescribeOperationRequest("x")), "OW-API-004");
            AssertErrorCode(
                api.SearchCompatibleOperations(new SearchCompatibleOperationsRequest(
                    Type: "core.String", Direction: "downstream")),
                "OW-API-004");
            AssertErrorCode(api.ListTypes(new ListTypesRequest()), "OW-API-004");
            AssertErrorCode(api.GetWorkflowSchema(new GetWorkflowSchemaRequest()), "OW-API-004");
            AssertErrorCode(api.ListWorkflows(new ListWorkflowsRequest()), "OW-API-004");
            AssertErrorCode(api.GetRegistryStatus(new GetRegistryStatusRequest()), "OW-API-004");
        }

        // A null snapshot — the §C3 rule-9 pre-build Registry — fails the
        // registry-scoped commands closed; get_registry_status still
        // answers with the honest empty state.
        [Test]
        public void DiscoveryCommands_NullSnapshot_FailClosed()
        {
            var fixture = NewFixture();
            var api = Api(fixture, snapshotAvailable: false);

            AssertErrorCode(api.SearchOperations(new SearchOperationsRequest()), "OW-API-004");
            AssertErrorCode(api.ListTypes(new ListTypesRequest()), "OW-API-004");

            var status = api.GetRegistryStatus(new GetRegistryStatusRequest());
            Assert.IsTrue(status.Ok, Describe(status.Errors));
            var data = (JObject)status.Data!;
            Assert.IsNull(data["generationId"], "no snapshot means no generation to report");
            var counts = (JObject)data["counts"]!;
            Assert.AreEqual(0, (int?)counts["standard"]);
            Assert.AreEqual(0, (int?)counts["custom"]);
        }
    }
}
