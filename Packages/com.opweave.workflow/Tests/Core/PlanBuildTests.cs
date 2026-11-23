#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the Build Plan stage and planHash
    /// (local:T-W1.6-07:plan-hash, local:T-W1.6-07:plan-immutability; SPEC §D7,
    /// §D0.1, §D0.2, §A8, §N.4, §N.5.5, §0.6 SG-11/SG-12): the CompiledPlan
    /// shape, the D5-ordered steps with their five InputSource kinds,
    /// transitions, policy/descriptor timeout-retry, actual effects,
    /// approvals, outputs, the golden SG-12 twelve-field hash input, the
    /// §D0.2 fixed rows, layout/fromPort insensitivity, fingerprint and
    /// environment invalidation, IntegrityLevel=full → OW-OP-011, and source
    /// immutability. The same sources run under dotnet and Unity Mono.
    /// </summary>
    internal sealed class PlanBuildTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        private sealed class UnityShapesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        private static readonly ITypeRegistry s_types = BuildTypes();

        private static ITypeRegistry BuildTypes()
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
            });
            Assert.AreEqual(0, build.Failures.Count,
                "registry build failed: " + (build.Failures.Count > 0 ? build.Failures[0].Detail : string.Empty));
            return build.Registry;
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

        private sealed class MapTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => true;
        }

        private static StaticContext StaticFactory(
            string nodeId, IReadOnlyDictionary<string, InputValue> parameters) =>
            new OperationContractFixtures.Static(nodeId, parameters);

        private static JsonValue Json(string text) => JsonValue.Parse(text).Value!;

        private static OperationDescriptor StartDescriptor() =>
            Op.Define("core.start", 1).Summary("Workflow entry point.").Build();

        private static OperationDescriptor PlaceDescriptor() =>
            Op.Define("example.place", 1)
                .Summary("Place an object at a position.")
                .Input("position", "unity.Vector3", required: true)
                .Input("payload", "core.Json", required: false)
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

        private static OperationDescriptor CountDescriptor() =>
            Op.Define("example.count", 1)
                .Summary("Count to a number.")
                .Input("count", "core.Int", required: true)
                .Input("amount", "core.Float", required: false,
                    @default: Json("1.5"))
                .Input("note", "core.String", required: false)
                .Output("total", "core.Float")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Reconcilable)
                .Build()
                with { DefaultRetry = new RetryPolicy(MaxAttempts: 3), DefaultTimeoutSeconds = 42 };

        private static OperationDescriptor SinkDescriptor() =>
            Op.Define("example.sink", 1)
                .Summary("Consume a total and a bound name.")
                .Input("total", "core.Float", required: true)
                .Input("name", "core.String", required: true)
                .Effects(EffectNames.AssetDelete)
                .Build();

        private static OperationCandidate Candidate(OperationDescriptor descriptor) =>
            new OperationCandidate(
                descriptor,
                OperationOrigin.Standard,
                "Acme.Editor",
                "Assets/Acme/Operation.cs",
                new string('a', 64),
                HasPollImplementation: true,
                Implementation: new StubOperation(descriptor));

        private static RegistrySnapshot Snapshot(params OperationDescriptor[] descriptors) =>
            RegistrySnapshotBuilder.Build(descriptors.Select(Candidate).ToArray(), s_types);

        private static RegistrySnapshot DefaultSnapshot() =>
            Snapshot(StartDescriptor(), PlaceDescriptor(), CountDescriptor(), SinkDescriptor());

        private static NodeData Node(
            string id, string operation, IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = 1, Parameters = parameters };

        private static WorkflowDocumentData LinearData(params NodeData[] nodes)
        {
            var edges = new List<ControlEdge>();
            for (var i = 0; i + 1 < nodes.Length; i++)
            {
                edges.Add(new ControlEdge(nodes[i].Id!, nodes[i + 1].Id!));
            }
            return new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.plan",
                Nodes = new List<NodeData>(nodes),
                ControlEdges = edges,
            };
        }

        private static WorkflowDocumentData PlaceData(JToken position, JToken? payload = null)
        {
            var data = LinearData(
                Node("start", "core.start"),
                Node("a", "example.place"),
                Node("b", "example.count"),
                Node("c", "example.sink"));
            var parameters = new Dictionary<string, JToken> { ["position"] = position };
            if (payload is not null)
            {
                parameters["payload"] = payload;
            }
            data.Nodes![1].Parameters = parameters;
            data.Nodes[2].Parameters = new Dictionary<string, JToken>
            {
                ["count"] = JToken.Parse("3"),
            };
            data.Nodes[3].Parameters = new Dictionary<string, JToken>
            {
                ["name"] = JToken.Parse("\"n\""),
            };
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("b", "total"), new PortRef("c", "total")),
            };
            data.Outputs = new List<WorkflowOutput>
            {
                new WorkflowOutput("total", new PortRef("b", "total")),
            };
            return data;
        }

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static ValidationRequest Request(
            WorkflowDocument document, JsonValue? inputs = null) =>
            new ValidationRequest(document, inputs, ValidationMode.Static, ActorKind.Agent);

        private static ValidationReport Compile(
            WorkflowDocument document,
            IPlanProvenance? provenance = null,
            JsonValue? inputs = null,
            RegistrySnapshot? snapshot = null) =>
            new WorkflowCompiler(
                    snapshot ?? DefaultSnapshot(),
                    StaticFactory,
                    new MapTypeProbe(),
                    provenance ?? new PlanProvenanceFixtures.Stub())
                .Validate(Request(document, inputs));

        private static CompiledPlan PlanOf(ValidationReport report)
        {
            Assert.IsTrue(report.Ok,
                "validation failed: " + string.Join("; ", report.Errors.Select(d => d.Code)));
            Assert.IsNotNull(report.Plan);
            return report.Plan!;
        }

        private static CompiledPlan PlanOf(WorkflowDocumentData data, JsonValue? inputs = null) =>
            PlanOf(Compile(MustCreate(data), inputs: inputs));

        // ── SPEC §D7 — plan shape ───────────────────────────────────────────────

        // SPEC §D7 test 1: same WF and inputs → same planHash; PlanId differs.
        [Test]
        public void SameWorkflowAndInputsProduceSamePlanHash()
        {
            var first = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var second = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));

            Assert.AreEqual(first.PlanHash, second.PlanHash);
            Assert.AreNotEqual(first.PlanId, second.PlanId);
            StringAssert.StartsWith("plan_", first.PlanId);
            Assert.AreEqual(1, first.FormatVersion);
            Assert.AreEqual(CanonicalJson.Version, first.CanonicalJsonVersion);
        }

        // SPEC §D7: steps in D5 order with the resolved source of each input.
        [Test]
        public void StepsCarryResolvedSourcesTransitionsAndPolicy()
        {
            var plan = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));

            Assert.That(plan.Steps.Select(s => s.NodeId), Is.EqualTo(new[] { "start", "a", "b", "c" }));
            Assert.That(plan.Steps.Select(s => s.Index), Is.EqualTo(new[] { 0, 1, 2, 3 }));

            // Transitions: {"next": i+1}; the last step ends with -1.
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var expected = i + 1 < plan.Steps.Count ? i + 1 : -1;
                Assert.That(plan.Steps[i].Transitions,
                    Is.EqualTo(new Dictionary<string, int> { ["next"] = expected }));
            }

            // InputSource kinds: Constant (codec-normalized), FromStep, Default, Missing.
            var place = plan.Steps[1];
            Assert.IsInstanceOf<Constant>(place.Inputs["position"]);
            Assert.AreEqual(
                "{\"x\":0e0,\"y\":1e0,\"z\":0e0}",
                ((Constant)place.Inputs["position"]).Value.CanonicalText);
            Assert.IsInstanceOf<Missing>(place.Inputs["payload"]);

            var count = plan.Steps[2];
            Assert.IsInstanceOf<Constant>(count.Inputs["count"]);
            Assert.IsInstanceOf<Default>(count.Inputs["amount"]);
            Assert.AreEqual("15e-1", ((Default)count.Inputs["amount"]).Value.CanonicalText);
            Assert.IsInstanceOf<Missing>(count.Inputs["note"]);

            var sink = plan.Steps[3];
            Assert.IsInstanceOf<FromStep>(sink.Inputs["total"]);
            Assert.AreEqual(new FromStep(2, "total"), sink.Inputs["total"]);
            Assert.IsInstanceOf<Constant>(sink.Inputs["name"]);

            // Timeout/retry: node policy else Descriptor defaults.
            Assert.AreEqual(42, count.TimeoutSeconds);
            Assert.AreEqual(3, count.Retry.MaxAttempts);
            Assert.AreEqual(Idempotency.Reconcilable, count.Idempotency);
            Assert.That(count.OutputTypes, Is.EqualTo(
                new Dictionary<string, string> { ["total"] = "core.Float" }));

            // Outputs resolved to step positions; effects actual; SourceMap reserved empty.
            Assert.That(plan.Outputs, Is.EqualTo(new[] { new PlanOutput("total", 2, "total") }));
            Assert.That(plan.Effects, Is.EqualTo(new[] { EffectNames.Read, EffectNames.AssetDelete }));
            Assert.IsEmpty(plan.SourceMap);
        }

        // SPEC §A6 / §D7: an output naming a port the referenced node's
        // resolved Descriptor does not declare is the same OW-OP-005 the D4
        // phase reports for a data edge's from.port — one diagnostic at the
        // output's from.port member and no Plan.
        [Test]
        public void OutputWithUnknownPortProducesNoPlan()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Outputs![0] = new WorkflowOutput("total", new PortRef("b", "ghost"));
            var report = Compile(MustCreate(data));

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan);
            var error = report.Errors.Single(d => d.Code == ErrorCodes.OW_OP_005);
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("ghost", error.Port);
            Assert.AreEqual("$.outputs[0].from.port", error.JsonPath);
        }

        // Node policy overrides Descriptor defaults (SPEC §D7 item 3).
        [Test]
        public void NodePolicyOverridesDescriptorDefaults()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Nodes![2].Policy = new NodePolicy(TimeoutSeconds: 7, MaxAttempts: 5);
            var plan = PlanOf(data);

            var count = plan.Steps[2];
            Assert.AreEqual(7, count.TimeoutSeconds);
            Assert.AreEqual(5, count.Retry.MaxAttempts);
            Assert.AreEqual(500, count.Retry.InitialBackoffMs);
        }

        // Workflow input bindings become FromInput sources; the hash input
        // marks unsupplied declared inputs missing, never null (§A0.1).
        [Test]
        public void BoundInputsAreFromInputAndMissingIsTagged()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("factor", "core.String", false, Json("\"fallback\"")),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("factor", new PortRef("c", "name")),
            };
            data.Nodes![3].Parameters!.Remove("name");

            var plan = PlanOf(data, Json("{\"factor\":\"n2\"}"));

            Assert.IsInstanceOf<FromInput>(plan.Steps[3].Inputs["name"]);
            Assert.AreEqual(new FromInput("factor"), plan.Steps[3].Inputs["name"]);

            var executionInputs = (JObject)plan.PlanHashInput.ToToken()["executionInputs"]!;
            Assert.AreEqual(
                "{\"kind\":\"supplied\",\"value\":\"n2\"}",
                CanonicalJson.Serialize(executionInputs["factor"]!));
        }

        // SG-12 collision guard: an omitted optional core.Json input and the
        // same input supplied as the literal {"kind":"missing"} object are
        // different plan inputs — supplied values carry the
        // {"kind":"supplied","value":...} wrapper, so a user JSON value can
        // never alias the missing sentinel (SPEC §D0.1, §0.6 SG-12).
        [Test]
        public void SuppliedMissingShapedValueDiffersFromOmittedInput()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("payload", "core.Json", false),
            };
            var document = MustCreate(data);

            var omitted = PlanOf(Compile(document));
            var supplied = PlanOf(
                Compile(document, inputs: Json("{\"payload\":{\"kind\":\"missing\"}}")));

            Assert.AreNotEqual(omitted.PlanHash, supplied.PlanHash);
            var executionInputs = (JObject)supplied.PlanHashInput.ToToken()["executionInputs"]!;
            Assert.AreEqual(
                "{\"kind\":\"supplied\",\"value\":{\"kind\":\"missing\"}}",
                CanonicalJson.Serialize(executionInputs["payload"]!));
            var omittedInputs = (JObject)omitted.PlanHashInput.ToToken()["executionInputs"]!;
            Assert.AreEqual(
                "{\"kind\":\"missing\"}",
                CanonicalJson.Serialize(omittedInputs["payload"]!));
        }

        // SG-12 collision guard: an explicit JSON null supplied for a
        // core.Json input is data, not Missing — it hashes differently from
        // the omitted input (SPEC §A0.1).
        [Test]
        public void SuppliedNullDiffersFromOmittedInput()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("payload", "core.Json", false),
            };
            var document = MustCreate(data);

            var omitted = PlanOf(Compile(document));
            var supplied = PlanOf(Compile(document, inputs: Json("{\"payload\":null}")));

            Assert.AreNotEqual(omitted.PlanHash, supplied.PlanHash);
            var executionInputs = (JObject)supplied.PlanHashInput.ToToken()["executionInputs"]!;
            Assert.AreEqual(
                "{\"kind\":\"supplied\",\"value\":null}",
                CanonicalJson.Serialize(executionInputs["payload"]!));
        }

        // Approvals carry the ask list with the operation detail (§H4 item 3).
        [Test]
        public void AskEffectsLandOnPlanApprovals()
        {
            var plan = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));

            Assert.That(plan.Approvals, Is.EqualTo(new[]
            {
                new ApprovalRequirement("c", EffectNames.AssetDelete, "example.sink v1"),
            }));
        }

        // ── SPEC §D7 / §D0.1 — planHash sensitivity ────────────────────────────

        // SPEC §D7 test 2: changing one parameter changes planHash.
        [Test]
        public void ParameterChangeChangesPlanHash()
        {
            var first = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var second = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":1}")));

            Assert.AreNotEqual(first.PlanHash, second.PlanHash);
            // The document identity changed too, but is only stored.
            Assert.AreNotEqual(first.WorkflowHash, second.WorkflowHash);
        }

        // SPEC §D0.2 row 1: Vector3 spellings normalize identically —
        // different WorkflowHash, same planHash.
        [Test]
        public void Vector3SpellingsSharePlanHash()
        {
            var ints = MustCreate(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var decimals = MustCreate(PlaceData(JToken.Parse("{\"x\":0.0,\"y\":1.0,\"z\":0.0}")));
            var first = PlanOf(Compile(ints));
            var second = PlanOf(Compile(decimals));

            Assert.AreNotEqual(first.WorkflowHash, second.WorkflowHash);
            Assert.AreEqual(first.PlanHash, second.PlanHash);
        }

        // SPEC §D0.2 row 2: core.Json keeps tokens — different hashes of both kinds.
        [Test]
        public void CoreJsonTokensStayDistinguishable()
        {
            var one = PlanOf(PlaceData(
                JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"), JToken.Parse("1")));
            var onePoint = PlanOf(PlaceData(
                JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"), JToken.Parse("1.0")));

            Assert.AreNotEqual(one.WorkflowHash, onePoint.WorkflowHash);
            Assert.AreNotEqual(one.PlanHash, onePoint.PlanHash);
        }

        // SPEC §D0.2 / §B7.1 Normalize: on a typed Double (core.Float) port
        // the spellings 0 and 0.0 normalize to the same binary64 position —
        // different WorkflowHash, same planHash (parameter constant variant).
        [Test]
        public void DoubleZeroSpellingsSharePlanHash()
        {
            var ints = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            ints.Nodes![2].Parameters!["amount"] = JToken.Parse("0");
            var decimals = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            decimals.Nodes![2].Parameters!["amount"] = JToken.Parse("0.0");

            var first = PlanOf(ints);
            var second = PlanOf(decimals);

            Assert.AreNotEqual(first.WorkflowHash, second.WorkflowHash);
            Assert.AreEqual(first.PlanHash, second.PlanHash);
            Assert.AreEqual(
                "0e0",
                ((Constant)first.Steps[2].Inputs["amount"]).Value.CanonicalText);
        }

        // The same codec-normalized equality on the execution-input variant:
        // a declared core.Float workflow input supplied as 0 or 0.0 produces
        // the identical {"kind":"supplied","value":0e0} hash-input tag.
        [Test]
        public void DoubleZeroExecutionInputsSharePlanHash()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("factor", "core.Float", false),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("factor", new PortRef("b", "amount")),
            };
            var document = MustCreate(data);

            var ints = PlanOf(Compile(document, inputs: Json("{\"factor\":0}")));
            var decimals = PlanOf(Compile(document, inputs: Json("{\"factor\":0.0}")));

            Assert.AreEqual(ints.PlanHash, decimals.PlanHash);
            var executionInputs = (JObject)ints.PlanHashInput.ToToken()["executionInputs"]!;
            Assert.AreEqual(
                "{\"kind\":\"supplied\",\"value\":0e0}",
                CanonicalJson.Serialize(executionInputs["factor"]!));
        }

        // SPEC §D0.2 row 3: a fraction on an Int port is OW-TYPE-003, no Plan.
        [Test]
        public void IntPortFractionProducesNoPlan()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Nodes![2].Parameters!["count"] = JToken.Parse("1.0");
            var report = Compile(MustCreate(data));

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan);
            Assert.IsTrue(report.Errors.Any(d => d.Code == ErrorCodes.OW_TYPE_003));
        }

        // SPEC §D0.2 row 4 / custom codec: reordered struct members normalize
        // identically — same WorkflowHash and planHash.
        [Test]
        public void ReorderedStructMembersSharePlanHash()
        {
            var first = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var second = PlanOf(PlaceData(JToken.Parse("{\"z\":0,\"y\":1,\"x\":0}")));

            Assert.AreEqual(first.WorkflowHash, second.WorkflowHash);
            Assert.AreEqual(first.PlanHash, second.PlanHash);
        }

        // SPEC §A8 test 1: a layout-only change keeps planHash (and WorkflowHash).
        [Test]
        public void LayoutOnlyChangeKeepsPlanHash()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Layout = new LayoutInfo(
                new Dictionary<string, LayoutNodePosition>
                {
                    ["a"] = new LayoutNodePosition(10, 20),
                },
                new List<LayoutNote>
                {
                    new LayoutNote("note1", 5, 5, "a sticky note"),
                });
            var first = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var second = PlanOf(data);

            Assert.AreEqual(first.WorkflowHash, second.WorkflowHash);
            Assert.AreEqual(first.PlanHash, second.PlanHash);
        }

        // SPEC §N.4 test 1: omitted fromPort and explicit "next" are one plan.
        [Test]
        public void OmittedFromPortKeepsPlanHash()
        {
            var omitted = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            var explicitNext = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            for (var i = 0; i < explicitNext.ControlEdges!.Count; i++)
            {
                var edge = explicitNext.ControlEdges[i];
                explicitNext.ControlEdges[i] = new ControlEdge(edge.From, edge.To, "next");
            }
            var first = PlanOf(omitted);
            var second = PlanOf(explicitNext);

            Assert.AreEqual(first.PlanHash, second.PlanHash);
        }

        // SPEC §D7 test 5: canonicalJsonVersion is a hash input — the same
        // semantic input under another version hashes differently.
        [Test]
        public void CanonicalJsonVersionChangesPlanHash()
        {
            var plan = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var input = (JObject)plan.PlanHashInput.ToToken();
            input["canonicalJsonVersion"] = 2;

            Assert.AreNotEqual(plan.PlanHash, CanonicalJson.Sha256(input));
            Assert.AreEqual(plan.PlanHash, CanonicalJson.Sha256(plan.PlanHashInput.ToToken()));
        }

        // SG-12: the hash input holds exactly the twelve named fields, the
        // SG-11 six environment keys and the codec identity object.
        [Test]
        public void PlanHashInputHasTheTwelveSg12Fields()
        {
            var plan = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var input = (JObject)plan.PlanHashInput.ToToken();

            Assert.That(
                input.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
                Is.EqualTo(new[]
                {
                    "canonicalJsonVersion", "environmentIdentifiers", "executionInputs",
                    "requiredEffects", "resolvedWorkflow", "retry", "steps",
                    "timeouts", "transitions", "usedCodecFingerprints",
                    "usedOperationFingerprints", "usedTypeFingerprints",
                }));

            var env = (JObject)input["environmentIdentifiers"]!;
            Assert.AreEqual("6000.0.85f1", (string)env["unityVersion"]!);
            Assert.AreEqual("Windows", (string)env["editorOS"]!);
            Assert.AreEqual("StandaloneWindows64", (string)env["activeBuildTarget"]!);
            Assert.AreEqual("Mono2x", (string)env["scriptingBackend"]!);
            Assert.AreEqual("NET_Standard", (string)env["apiCompatibilityLevel"]!);
            Assert.That(
                env["scriptingDefineSymbols"]!.Values<string>(),
                Is.EqualTo(new[] { "DEBUG", "TRACE", "UNITY_EDITOR" }));

            var codecs = (JArray)input["usedCodecFingerprints"]!;
            Assert.Greater(codecs.Count, 0);
            var codec = (JObject)codecs[0];
            Assert.IsNotNull(codec["typeId"]);
            Assert.IsNotNull(codec["providerId"]);
            Assert.IsNotNull(codec["codecTypeFullName"]);
            Assert.IsNotNull(codec["definingAssemblyName"]);
            Assert.IsNotNull(codec["definingAssemblySha256"]);
            Assert.AreEqual(CoreApi.Version, (string)codec["coreApiVersion"]!);

            var operations = (JArray)input["usedOperationFingerprints"]!;
            Assert.IsTrue(operations.Any(o => (string)o["id"]! == "example.place"));

            var types = (JArray)input["usedTypeFingerprints"]!;
            Assert.IsTrue(types.Any(t => (string)t["typeId"]! == "unity.Vector3"));
        }

        // Golden planHash vector (SG-12): the fixed fixture's hash is frozen.
        [Test]
        public void GoldenPlanHashVector()
        {
            var plan = PlanOf(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));

            Assert.AreEqual(Golden.PlanHash, plan.PlanHash);
        }

        // Golden canonical-bytes vector (SG-12): freeze the exact canonical
        // UTF-8 string of the twelve-field planHash input alongside its
        // SHA-256, for a fixed fixture that exercises both executionInputs
        // tags — the bound, supplied "factor" as
        // {"kind":"supplied","value":"n2"} and the declared-but-unsupplied
        // "spare" as {"kind":"missing"}. Regenerate only with a deliberate
        // format change (SPEC §0.6 SG-12).
        [Test]
        public void GoldenPlanHashInputVector()
        {
            var data = PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"));
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("factor", "core.String", false),
                new WorkflowInput("spare", "core.Float", false),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("factor", new PortRef("c", "name")),
            };
            data.Nodes![3].Parameters!.Remove("name");
            var plan = PlanOf(Compile(MustCreate(data), inputs: Json("{\"factor\":\"n2\"}")));

            Assert.AreEqual(Golden.CanonicalInput, plan.PlanHashInput.CanonicalText);
            Assert.AreEqual(Golden.InputVectorPlanHash, plan.PlanHash);
        }

        // The frozen SG-12 vectors for the fixtures above. The canonical
        // input's twelve top-level fields are, in canonical key order,
        // canonicalJsonVersion, environmentIdentifiers (the six SG-11 keys:
        // unityVersion, editorOS, activeBuildTarget, scriptingBackend,
        // apiCompatibilityLevel, scriptingDefineSymbols), executionInputs
        // (supplied/missing tags), requiredEffects {steps, approvals},
        // resolvedWorkflow (the canonical document), retry, steps,
        // timeouts, transitions, usedCodecFingerprints,
        // usedOperationFingerprints and usedTypeFingerprints — step-aligned
        // arrays in D5 order, fingerprints sorted Ordinal.
        private static class Golden
        {
            internal const string PlanHash = "05fae8cd2da1a7dee9d5218c14675c4332300c3c4d63a7972aff1b5c698b173f";
            internal const string InputVectorPlanHash = "76ee71194504f45f5a42b7d88c2f5fb794934b931f29f45261858951dd79a6bf";
            internal const string CanonicalInput =
                @"{""canonicalJsonVersion"":1,""environmentIdentifiers"":{""activeBuildTarget"":""StandaloneWindows64"",""apiCompatibilityLevel"":""NET_Standard"",""editorOS"":""Windows"",""scriptingBackend"":""Mono2x"",""scriptingDefineSymbols"":[""DEBUG"",""TRACE"",""UNITY_EDITOR""],""unityVersion"":""6000.0.85f1""},"
                + @"""executionInputs"":{""factor"":{""kind"":""supplied"",""value"":""n2""},""spare"":{""kind"":""missing""}},"
                + @"""requiredEffects"":{""approvals"":[{""detail"":""example.sink v1"",""effect"":""asset.delete"",""nodeId"":""c""}],""steps"":[{""effects"":[],""index"":0},{""effects"":[""read""],""index"":1},{""effects"":[""read""],""index"":2},{""effects"":[""asset.delete""],""index"":3}]},"
                + @"""resolvedWorkflow"":{""controlEdges"":[{""from"":""a"",""fromPort"":""next"",""to"":""b""},{""from"":""b"",""fromPort"":""next"",""to"":""c""},{""from"":""start"",""fromPort"":""next"",""to"":""a""}],""dataEdges"":[{""from"":{""node"":""b"",""port"":""total""},""to"":{""node"":""c"",""port"":""total""}}],""id"":""example.plan"",""inputBindings"":[{""fromInput"":""factor"",""to"":{""node"":""c"",""port"":""name""}}],""inputs"":[{""id"":""factor"",""required"":false,""type"":""core.String""},{""id"":""spare"",""required"":false,""type"":""core.Float""}],""nodes"":[{""id"":""a"",""operation"":""example.place"",""parameters"":{""position"":{""x"":0e0,""y"":1e0,""z"":0e0}},""version"":1},{""id"":""b"",""operation"":""example.count"",""parameters"":{""count"":3},""version"":1},{""id"":""c"",""operation"":""example.sink"",""parameters"":{},""version"":1},{""id"":""start"",""operation"":""core.start"",""parameters"":{},""version"":1}],""outputs"":[{""from"":{""node"":""b"",""port"":""total""},""id"":""total""}],""schemaVersion"":""1.0""},"
                + @"""retry"":[{""initialBackoffMs"":500,""maxAttempts"":1,""maxBackoffMs"":10000,""multiplier"":2e0},{""initialBackoffMs"":500,""maxAttempts"":1,""maxBackoffMs"":10000,""multiplier"":2e0},{""initialBackoffMs"":500,""maxAttempts"":3,""maxBackoffMs"":10000,""multiplier"":2e0},{""initialBackoffMs"":500,""maxAttempts"":1,""maxBackoffMs"":10000,""multiplier"":2e0}],"
                + @"""steps"":[{""idempotency"":""unsafe"",""index"":0,""inputs"":{},""nodeId"":""start"",""operationId"":""core.start"",""outputs"":{},""version"":1},{""idempotency"":""idempotent"",""index"":1,""inputs"":{""payload"":{""kind"":""missing""},""position"":{""kind"":""constant"",""value"":{""x"":0e0,""y"":1e0,""z"":0e0}}},""nodeId"":""a"",""operationId"":""example.place"",""outputs"":{},""version"":1},{""idempotency"":""reconcilable"",""index"":2,""inputs"":{""amount"":{""kind"":""default"",""value"":15e-1},""count"":{""kind"":""constant"",""value"":3},""note"":{""kind"":""missing""}},""nodeId"":""b"",""operationId"":""example.count"",""outputs"":{""total"":""core.Float""},""version"":1},{""idempotency"":""unsafe"",""index"":3,""inputs"":{""name"":{""inputId"":""factor"",""kind"":""from_input""},""total"":{""kind"":""from_step"",""port"":""total"",""stepIndex"":2}},""nodeId"":""c"",""operationId"":""example.sink"",""outputs"":{},""version"":1}],"
                + @"""timeouts"":[30,30,42,30],""transitions"":[{""next"":1},{""next"":2},{""next"":3},{""next"":-1}],"
                + @"""usedCodecFingerprints"":[{""codecTypeFullName"":""Test.Codecs.core.String"",""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Codecs"",""definingAssemblySha256"":""2222222222222222222222222222222222222222222222222222222222222222"",""providerId"":""test.provider"",""typeId"":""core.String""},{""codecTypeFullName"":""Test.Codecs.core.Int"",""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Codecs"",""definingAssemblySha256"":""2222222222222222222222222222222222222222222222222222222222222222"",""providerId"":""test.provider"",""typeId"":""core.Int""},{""codecTypeFullName"":""Test.Codecs.core.Json"",""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Codecs"",""definingAssemblySha256"":""2222222222222222222222222222222222222222222222222222222222222222"",""providerId"":""test.provider"",""typeId"":""core.Json""},{""codecTypeFullName"":""Test.Codecs.core.Float"",""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Codecs"",""definingAssemblySha256"":""2222222222222222222222222222222222222222222222222222222222222222"",""providerId"":""test.provider"",""typeId"":""core.Float""},{""codecTypeFullName"":""Test.Codecs.unity.Vector3"",""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Codecs"",""definingAssemblySha256"":""2222222222222222222222222222222222222222222222222222222222222222"",""providerId"":""test.provider"",""typeId"":""unity.Vector3""}],"
                + @"""usedOperationFingerprints"":[{""coreApiVersion"":""1.0.0"",""descriptorHash"":""4968baac5e324401c707ece5479bccc91bd9ab631160d7d263e86de97197c357"",""id"":""core.start"",""implementationHash"":""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"",""version"":1},{""coreApiVersion"":""1.0.0"",""descriptorHash"":""9b7d921aedec432ad501ed1ec7f5a372bc0bcb16cfd763410495edccf75e6138"",""id"":""example.count"",""implementationHash"":""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"",""version"":1},{""coreApiVersion"":""1.0.0"",""descriptorHash"":""49ebe7145b42d42577403e37a3370b43b01b4fe96aa477295772f6725d88ec68"",""id"":""example.place"",""implementationHash"":""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"",""version"":1},{""coreApiVersion"":""1.0.0"",""descriptorHash"":""3e80f0a071ca8105a326e3d8a0eb724a49f3631c9d75935298c26d252020af81"",""id"":""example.sink"",""implementationHash"":""aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"",""version"":1}],"
                + @"""usedTypeFingerprints"":[{""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Types"",""definingAssemblySha256"":""357c11aeca744334bb2728949bf9c3dc6b65df2a91dc540c4c3ad4755888a972"",""providerId"":""test.provider"",""typeDefinitionHash"":""1111111111111111111111111111111111111111111111111111111111111111"",""typeId"":""core.Float""},{""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Types"",""definingAssemblySha256"":""48a0ae398129708598de901c99c8baa0fef165cfd16704848c8bc12e38823738"",""providerId"":""test.provider"",""typeDefinitionHash"":""1111111111111111111111111111111111111111111111111111111111111111"",""typeId"":""core.Int""},{""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Types"",""definingAssemblySha256"":""3e4937c6e8be55ee8e6ad3405adfc4bfb873277d6ef94403a3c278a26519061a"",""providerId"":""test.provider"",""typeDefinitionHash"":""1111111111111111111111111111111111111111111111111111111111111111"",""typeId"":""core.Json""},{""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Types"",""definingAssemblySha256"":""3d79f1145e61e3a346466327a59229ab6d7aeee5675396206c462e62a356a3f4"",""providerId"":""test.provider"",""typeDefinitionHash"":""1111111111111111111111111111111111111111111111111111111111111111"",""typeId"":""core.String""},{""coreApiVersion"":""1.0.0"",""definingAssemblyName"":""Test.Types"",""definingAssemblySha256"":""5e2bac3d4da1e10de17aabbd2e2c9fc1c424ff6e00a9fed259cfe9977bdc4f58"",""providerId"":""test.provider"",""typeDefinitionHash"":""1111111111111111111111111111111111111111111111111111111111111111"",""typeId"":""unity.Vector3""}]}";
        }

        // Each changed environment identifier invalidates the plan (SG-11).
        [Test]
        public void EnvironmentChangeChangesPlanHash()
        {
            var document = MustCreate(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var baseline = PlanOf(Compile(document));
            var env = PlanProvenanceFixtures.Environment;
            var variants = new[]
            {
                env with { UnityVersion = "6000.1.0f1" },
                env with { EditorOS = "Linux" },
                env with { ActiveBuildTarget = "Android" },
                env with { ScriptingBackend = "IL2CPP" },
                env with { ApiCompatibilityLevel = "NET_Framework" },
                env with { ScriptingDefineSymbols = new[] { "DEBUG" } },
            };
            foreach (var variant in variants)
            {
                var provenance = new PlanProvenanceFixtures.Stub
                {
                    EnvironmentResult = Result<PlanEnvironment>.Success(variant),
                };
                var report = Compile(document, provenance);
                Assert.AreNotEqual(
                    baseline.PlanHash, PlanOf(report).PlanHash,
                    "environment change must invalidate the planHash");
            }
        }

        // A changed used fingerprint invalidates the plan (SPEC §D0.1).
        [Test]
        public void FingerprintChangeChangesPlanHash()
        {
            var document = MustCreate(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}")));
            var baseline = PlanOf(Compile(document));

            var provenance = new PlanProvenanceFixtures.Stub
            {
                CodecIdentityFn = definition => Result<PlanCodecIdentity>.Success(
                    new PlanCodecIdentity(
                        "test.provider", "Test.Codecs.Altered", "Test.Codecs", new string('3', 64))),
            };
            var report = Compile(document, provenance);
            Assert.AreNotEqual(baseline.PlanHash, PlanOf(report).PlanHash);
        }

        // SPEC §0.1 F-05: the configured IntegrityLevel=full is an explicit
        // Build Plan input — the stage answers OW-OP-011 before any Plan is
        // produced even when every provenance answer is healthy, and never
        // downgrades to weak.
        [Test]
        public void IntegrityLevelFullIsRejectedWithOwOp011()
        {
            var provenance = new PlanProvenanceFixtures.Stub
            {
                IntegrityLevel = IntegrityLevel.Full,
            };
            var report = Compile(
                MustCreate(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"))), provenance);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan);
            Assert.IsTrue(report.Errors.Any(d => d.Code == ErrorCodes.OW_OP_011));
        }

        // SPEC §0.6 SG-11: an unobtainable required identifier is still
        // reported verbatim and produces no Plan — never a guessed or empty
        // default. A provider that answers OW-OP-011 itself takes the same
        // path.
        [Test]
        public void FailedProvenanceResultProducesNoPlan()
        {
            var provenance = new PlanProvenanceFixtures.Stub
            {
                EnvironmentResult = Result<PlanEnvironment>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_011,
                    "IntegrityLevel=full execution is not supported in this version.")),
            };
            var report = Compile(
                MustCreate(PlaceData(JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"))), provenance);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan);
            var error = report.Errors.Single(d => d.Code == ErrorCodes.OW_OP_011);
            Assert.IsNotNull(error);
        }

        // local:T-W1.6-07:plan-immutability (SPEC §D7 item 4 / AT-05): mutating
        // source tokens and inputs after the build changes neither the Plan
        // nor its hash.
        [Test]
        public void MutatingSourcesAfterBuildChangesNothing()
        {
            var position = JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}");
            var data = PlaceData(position);
            var inputsToken = JToken.Parse("{\"factor\":\"n2\"}");
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("factor", "core.String", false),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("factor", new PortRef("c", "name")),
            };
            data.Nodes![3].Parameters!.Remove("name");
            var document = MustCreate(data);
            var inputs = JsonValue.From(inputsToken);
            var plan = PlanOf(Compile(document, inputs: inputs));
            var hash = plan.PlanHash;
            var inputText = ((Constant)plan.Steps[1].Inputs["position"]).Value.CanonicalText;

            // Mutate every reachable source object: the document-data tokens,
            // the inputs token handed to JsonValue.From and the data itself.
            position["x"] = 99;
            inputsToken["factor"] = "changed";
            data.Nodes[1].Parameters!["position"] = JToken.Parse("{\"x\":9,\"y\":9,\"z\":9}");

            var rebuilt = PlanOf(Compile(document, inputs: inputs));
            Assert.AreEqual(hash, rebuilt.PlanHash);
            Assert.AreEqual(
                inputText,
                ((Constant)rebuilt.Steps[1].Inputs["position"]).Value.CanonicalText);
            Assert.AreEqual("\"n2\"",
                CanonicalJson.Serialize(((JObject)rebuilt.Inputs.ToToken())["factor"]!));
        }
    }
}
