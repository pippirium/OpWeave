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
    /// [Core] tests for plan.json canonical persistence and version
    /// validation (local:T-W1.6-08:plan-json-roundtrip; SPEC §D7 items 5-6,
    /// §D0.1, §0.6 SG-12 as amended 2026-11-06, §0.3, §F2 item 4): the SG-12
    /// field set frozen by a golden document, save→load round-trip equality
    /// with planHash recomputation, missing/mistyped/unknown
    /// <c>formatVersion</c>/<c>canonicalJsonVersion</c> rejection without
    /// interpretation, unknown and edited semantic members, and the
    /// planHashInput↔public-field consistency cross-checks — a missing
    /// planHashInput, a planHash mismatch or a public-field contradiction is
    /// NeedsReview per the 2026-11-06 amendment. The same sources run under
    /// dotnet and Unity Mono.
    /// </summary>
    internal sealed class PlanJsonCodecTests
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

        private static RegistrySnapshot DefaultSnapshot() =>
            RegistrySnapshotBuilder.Build(
                new[] { StartDescriptor(), PlaceDescriptor(), CountDescriptor(), SinkDescriptor() }
                    .Select(Candidate).ToArray(),
                s_types);

        private static NodeData Node(
            string id, string operation, IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = 1, Parameters = parameters };

        // The golden fixture's graph: start → a(place) → b(count) → c(sink),
        // with declared inputs factor (bound to c.name) and spare
        // (unsupplied). Deterministic planHashInput — the same shape as the
        // T-W1.6-07 golden vector — so the persisted document is frozen.
        private static WorkflowDocumentData GoldenData()
        {
            var data = new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.plan",
                Nodes = new List<NodeData>
                {
                    Node("start", "core.start"),
                    Node("a", "example.place",
                        new Dictionary<string, JToken> { ["position"] = JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}") }),
                    Node("b", "example.count",
                        new Dictionary<string, JToken> { ["count"] = JToken.Parse("3") }),
                    Node("c", "example.sink"),
                },
                ControlEdges = new List<ControlEdge>
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b"),
                    new ControlEdge("b", "c"),
                },
                DataEdges = new List<DataEdge>
                {
                    new DataEdge(new PortRef("b", "total"), new PortRef("c", "total")),
                },
                Inputs = new List<WorkflowInput>
                {
                    new WorkflowInput("factor", "core.String", false),
                    new WorkflowInput("spare", "core.Float", false),
                },
                InputBindings = new List<InputBinding>
                {
                    new InputBinding("factor", new PortRef("c", "name")),
                },
                Outputs = new List<WorkflowOutput>
                {
                    new WorkflowOutput("total", new PortRef("b", "total")),
                },
            };
            return data;
        }

        private static CompiledPlan PlanOf(WorkflowDocumentData data, JsonValue? inputs = null)
        {
            var created = WorkflowDocument.Create(data);
            Assert.IsTrue(created.Ok, "Create failed: " + string.Join(
                "; ", created.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            var report = new WorkflowCompiler(
                    DefaultSnapshot(), StaticFactory, new MapTypeProbe(),
                    new PlanProvenanceFixtures.Stub())
                .Validate(new ValidationRequest(
                    created.Value!, inputs, ValidationMode.Static, ActorKind.Agent));
            Assert.IsTrue(report.Ok,
                "validation failed: " + string.Join("; ", report.Errors.Select(d => d.Code)));
            Assert.IsNotNull(report.Plan);
            return report.Plan!;
        }

        private static CompiledPlan GoldenPlan()
        {
            var plan = PlanOf(GoldenData(), Json("{\"factor\":\"n2\"}"));
            // PlanId is intentionally outside the hash — pin it so the whole
            // document is deterministic.
            return new CompiledPlan(
                "plan_01J00000000000000000000000",
                plan.PlanHash, plan.WorkflowId, plan.WorkflowHash, plan.RegistryHash,
                plan.Inputs, plan.Steps, plan.Outputs, plan.Effects, plan.Approvals,
                plan.SourceMap, plan.PlanHashInput);
        }

        private static string Mutate(string text, Action<JObject> edit)
        {
            var root = JObject.Parse(text);
            edit(root);
            return CanonicalJson.Serialize(root);
        }

        // A planHashInput mutation re-signed so that only the targeted
        // consistency check can fail — never the planHash recomputation
        // itself (2026-11-06 SG-12 items 1-7).
        private static string MutateHashInput(string text, Action<JObject> edit)
        {
            var root = JObject.Parse(text);
            var input = (JObject)root["planHashInput"]!;
            edit(input);
            root["planHash"] = CanonicalJson.Sha256(input);
            return CanonicalJson.Serialize(root);
        }

        private static PlanJsonRead MustRead(string text) => PlanJsonCodec.Read(text);

        private static void AssertPlansEqual(CompiledPlan expected, CompiledPlan actual)
        {
            Assert.AreEqual(expected.FormatVersion, actual.FormatVersion);
            Assert.AreEqual(expected.CanonicalJsonVersion, actual.CanonicalJsonVersion);
            Assert.AreEqual(expected.PlanId, actual.PlanId);
            Assert.AreEqual(expected.PlanHash, actual.PlanHash);
            Assert.AreEqual(expected.WorkflowId, actual.WorkflowId);
            Assert.AreEqual(expected.WorkflowHash, actual.WorkflowHash);
            Assert.AreEqual(expected.RegistryHash, actual.RegistryHash);
            Assert.AreEqual(expected.Inputs.CanonicalText, actual.Inputs.CanonicalText);
            Assert.AreEqual(expected.Steps.Count, actual.Steps.Count);
            for (var i = 0; i < expected.Steps.Count; i++)
            {
                AssertStepsEqual(expected.Steps[i], actual.Steps[i]);
            }
            Assert.That(actual.Outputs, Is.EqualTo(expected.Outputs.ToArray()));
            Assert.That(actual.Effects, Is.EqualTo(expected.Effects.ToArray()));
            Assert.That(actual.Approvals, Is.EqualTo(expected.Approvals.ToArray()));
            Assert.That(actual.SourceMap, Is.EqualTo(expected.SourceMap.ToArray()));
            Assert.AreEqual(
                expected.PlanHashInput.CanonicalText, actual.PlanHashInput.CanonicalText);
        }

        private static void AssertStepsEqual(PlanStep expected, PlanStep actual)
        {
            Assert.AreEqual(expected.Index, actual.Index);
            Assert.AreEqual(expected.NodeId, actual.NodeId);
            Assert.AreEqual(expected.OperationId, actual.OperationId);
            Assert.AreEqual(expected.Version, actual.Version);
            Assert.AreEqual(expected.DescriptorHash, actual.DescriptorHash);
            Assert.AreEqual(expected.ImplementationHash, actual.ImplementationHash);
            Assert.That(actual.Inputs.Keys, Is.EquivalentTo(expected.Inputs.Keys.ToArray()));
            foreach (var key in expected.Inputs.Keys)
            {
                Assert.AreEqual(expected.Inputs[key], actual.Inputs[key], "input " + key);
            }
            Assert.That(actual.OutputTypes, Is.EqualTo(expected.OutputTypes));
            Assert.That(actual.Transitions, Is.EqualTo(expected.Transitions));
            Assert.That(actual.Effects, Is.EqualTo(expected.Effects.ToArray()));
            Assert.AreEqual(expected.TimeoutSeconds, actual.TimeoutSeconds);
            Assert.AreEqual(expected.Retry, actual.Retry);
            Assert.AreEqual(expected.Idempotency, actual.Idempotency);
        }

        private static void AssertCorrupt(PlanJsonRead read, string because)
        {
            Assert.AreEqual(PlanJsonStatus.Corrupt, read.Status, because);
            Assert.IsNull(read.Plan);
            Assert.IsNotNull(read.Diagnostic);
            Assert.AreEqual(string.Empty, read.Diagnostic!.Code,
                "internal diagnostics carry no OW-* code (SG-02)");
        }

        private static void AssertNeedsReview(PlanJsonRead read, string because)
        {
            Assert.AreEqual(PlanJsonStatus.NeedsReview, read.Status, because);
            Assert.IsNull(read.Plan);
            Assert.IsNotNull(read.Diagnostic);
            Assert.AreEqual(string.Empty, read.Diagnostic!.Code);
        }

        // ── Round-trip ─────────────────────────────────────────────────────

        // SPEC §D7 test 4: save→load yields an equal Plan with the same
        // planHash.
        [Test]
        public void WriteThenReadYieldsAnEqualPlanWithTheSamePlanHash()
        {
            var plan = GoldenPlan();
            var text = PlanJsonCodec.Write(plan);
            var read = MustRead(text);

            Assert.AreEqual(PlanJsonStatus.Decoded, read.Status);
            Assert.IsNotNull(read.Plan);
            AssertPlansEqual(plan, read.Plan!);
        }

        // The persisted form is stable: decoding and re-encoding reproduces
        // the identical canonical bytes.
        [Test]
        public void ReadThenWriteReproducesTheCanonicalBytes()
        {
            var plan = GoldenPlan();
            var text = PlanJsonCodec.Write(plan);
            var read = MustRead(text);
            Assert.AreEqual(PlanJsonStatus.Decoded, read.Status);

            Assert.AreEqual(text, PlanJsonCodec.Write(read.Plan!));
        }

        // A document with members out of canonical order still decodes: the
        // reader validates content, not byte order (byte identity is the
        // Run Store's planFileDigest check, SPEC §D0.1).
        [Test]
        public void ReorderedMembersStillDecode()
        {
            var text = PlanJsonCodec.Write(GoldenPlan());
            var root = JObject.Parse(text);
            var shuffled = new JObject();
            foreach (var property in root.Properties().Reverse())
            {
                shuffled[property.Name] = property.Value;
            }

            var read = MustRead(shuffled.ToString(Newtonsoft.Json.Formatting.None));
            Assert.AreEqual(PlanJsonStatus.Decoded, read.Status);
            Assert.AreEqual(GoldenPlan().PlanHash, read.Plan!.PlanHash);
        }

        // SPEC §0.6 SG-12: the document holds every CompiledPlan public field
        // and nothing else — no self-digest (planFileDigest lives only in the
        // RunAccepted journal record, SPEC §D0.1).
        [Test]
        public void DocumentHasTheSg12MemberSetAndNoSelfDigest()
        {
            var root = JObject.Parse(PlanJsonCodec.Write(GoldenPlan()));

            Assert.That(
                root.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
                Is.EqualTo(new[]
                {
                    "approvals", "canonicalJsonVersion", "effects", "formatVersion",
                    "inputs", "outputs", "planHash", "planHashInput", "planId",
                    "registryHash", "sourceMap", "steps", "workflowHash", "workflowId",
                }));
            Assert.IsFalse(root.ContainsKey("planFileDigest"));
            Assert.IsFalse(root.ContainsKey("digest"));
            Assert.AreEqual(1, (int)root["formatVersion"]!);
            Assert.AreEqual(CanonicalJson.Version, (int)root["canonicalJsonVersion"]!);
        }

        // SPEC §D7 test 6 / SG-12: golden plan.json bytes freeze the whole
        // persisted format — every field, enum spelling, InputSource tag and
        // the verbatim twelve-field hash input. Regenerate only with a
        // deliberate format change.
        [Test]
        public void GoldenPlanJsonFixture()
        {
            var plan = GoldenPlan();

            Assert.AreEqual(Golden.PlanHash, plan.PlanHash);
            Assert.AreEqual(Golden.Text, PlanJsonCodec.Write(plan));

            var read = MustRead(Golden.Text);
            Assert.AreEqual(PlanJsonStatus.Decoded, read.Status);
            Assert.AreEqual(Golden.PlanHash, read.Plan!.PlanHash);
        }

        // ── Version gate (NeedsReview, never interpreted) ───────────────────

        // SPEC §0.3 / §F2 item 4 / §D7 test 6: a missing, mistyped or unknown
        // formatVersion or canonicalJsonVersion rejects the document without
        // interpreting it.
        [Test]
        public void MissingFormatVersionIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root.Remove("formatVersion"));
            AssertNeedsReview(MustRead(text), "missing formatVersion");
        }

        [Test]
        public void MissingCanonicalJsonVersionIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root.Remove("canonicalJsonVersion"));
            AssertNeedsReview(MustRead(text), "missing canonicalJsonVersion");
        }

        [Test]
        public void MistypedFormatVersionIsRejected()
        {
            foreach (var wrong in new JToken[] { "1", 1.0, true, JValue.CreateNull() })
            {
                var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                    root => root["formatVersion"] = wrong);
                AssertNeedsReview(MustRead(text), "mistyped formatVersion " + wrong.Type);
            }
        }

        [Test]
        public void MistypedCanonicalJsonVersionIsRejected()
        {
            foreach (var wrong in new JToken[] { "1", 1.0, true, JValue.CreateNull() })
            {
                var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                    root => root["canonicalJsonVersion"] = wrong);
                AssertNeedsReview(MustRead(text), "mistyped canonicalJsonVersion " + wrong.Type);
            }
        }

        [Test]
        public void UnknownVersionsAreRejected()
        {
            foreach (var wrong in new JToken[] { 0, 2, -1, 999 })
            {
                var mutated = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                    root => root["formatVersion"] = wrong);
                AssertNeedsReview(MustRead(mutated), "unknown formatVersion " + wrong);

                mutated = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                    root => root["canonicalJsonVersion"] = wrong);
                AssertNeedsReview(MustRead(mutated), "unknown canonicalJsonVersion " + wrong);
            }
        }

        // An unknown version must not be interpreted — even when the rest of
        // the document is also malformed, the version outcome wins.
        [Test]
        public void UnknownVersionWinsOverOtherCorruption()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                root["canonicalJsonVersion"] = 2;
                root["steps"] = "not an array";
            });
            AssertNeedsReview(MustRead(text), "unknown version wins over corrupt members");
        }

        // ── Format violations (Corrupt) ────────────────────────────────────

        [Test]
        public void UnparseableAndNonObjectDocumentsAreRejected()
        {
            AssertCorrupt(MustRead("not json"), "unparseable");
            AssertCorrupt(MustRead("[1,2]"), "non-object");
            AssertCorrupt(MustRead("42"), "scalar");
        }

        // SPEC §0.6 SG-12: unknown semantic fields reject.
        [Test]
        public void UnknownTopLevelMemberIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root["futureField"] = 1);
            AssertCorrupt(MustRead(text), "unknown member");
        }

        [Test]
        public void MissingAndMistypedMembersAreRejected()
        {
            foreach (var member in new[]
            {
                "planId", "planHash", "workflowId", "workflowHash", "registryHash",
                "inputs", "steps", "outputs", "effects", "approvals", "sourceMap",
            })
            {
                var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                    root => root.Remove(member));
                AssertCorrupt(MustRead(text), "missing " + member);
            }

            // A missing planHashInput is not a format violation: the document
            // reads but cannot be revalidated — NeedsReview (2026-11-06 SG-12).
            var noHashInput = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root.Remove("planHashInput"));
            AssertNeedsReview(MustRead(noHashInput), "missing planHashInput");

            AssertCorrupt(
                MustRead(Mutate(PlanJsonCodec.Write(GoldenPlan()),
                    root => root["steps"] = new JObject())),
                "steps not an array");
            AssertCorrupt(
                MustRead(Mutate(PlanJsonCodec.Write(GoldenPlan()),
                    root => root["planId"] = 7)),
                "planId not a string");
            AssertCorrupt(
                MustRead(Mutate(PlanJsonCodec.Write(GoldenPlan()),
                    root => root["planId"] = "planless")),
                "planId without plan_ prefix");
        }

        // ── planHash recomputation (SPEC §D0.1; NeedsReview per 2026-11-06 SG-12) ──

        [Test]
        public void EditedPlanHashMemberIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root["planHash"] = new string('0', 64));
            AssertNeedsReview(MustRead(text), "edited planHash");
        }

        [Test]
        public void EditedHashInputMembersAreRejected()
        {
            // resolvedWorkflow: one changed parameter.
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var input = (JObject)root["planHashInput"]!;
                var nodes = (JArray)((JObject)input["resolvedWorkflow"]!)["nodes"]!;
                ((JObject)((JObject)nodes[1])["parameters"]!)["position"] =
                    JObject.Parse("{\"x\":9,\"y\":1,\"z\":0}");
            });
            AssertNeedsReview(MustRead(text), "edited resolvedWorkflow");

            // A step-aligned member.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var input = (JObject)root["planHashInput"]!;
                ((JArray)input["timeouts"]!)[0] = 999;
            });
            AssertNeedsReview(MustRead(text), "edited timeouts");

            // The environment identifiers.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var input = (JObject)root["planHashInput"]!;
                ((JObject)input["environmentIdentifiers"]!)["unityVersion"] = "6000.1.0f1";
            });
            AssertNeedsReview(MustRead(text), "edited environmentIdentifiers");

            // A used fingerprint.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var input = (JObject)root["planHashInput"]!;
                ((JObject)((JArray)input["usedTypeFingerprints"]!)[0])["typeDefinitionHash"] =
                    new string('0', 64);
            });
            AssertNeedsReview(MustRead(text), "edited usedTypeFingerprints");
        }

        // ── Public-field ↔ hashInput consistency (NeedsReview per 2026-11-06 SG-12) ──

        // Any edit to a semantic public field is rejected — the persisted
        // field must agree with the persisted hash input, and the
        // contradiction is NeedsReview, never a silent decode.
        [Test]
        public void EditedSemanticFieldsAreRejected()
        {
            // steps: a nodeId edit.
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[1])["nodeId"] = "tampered");
            AssertNeedsReview(MustRead(text), "edited step nodeId");

            // steps: a constant input value edit.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var inputs = (JObject)((JObject)((JArray)root["steps"]!)[1])["inputs"]!;
                ((JObject)inputs["position"]!)["value"] = JObject.Parse("{\"x\":9,\"y\":1,\"z\":0}");
            });
            AssertNeedsReview(MustRead(text), "edited step input value");

            // transitions: a bogus target — the out-of-range target fails the
            // step decode itself, before the consistency check.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JObject)((JArray)root["steps"]!)[0])["transitions"]!)["next"] = 99);
            AssertCorrupt(MustRead(text), "edited transition");

            // retry / timeout edits.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JObject)((JArray)root["steps"]!)[2])["retry"]!)["maxAttempts"] = 9);
            AssertNeedsReview(MustRead(text), "edited retry");
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[2])["timeoutSeconds"] = 9);
            AssertNeedsReview(MustRead(text), "edited timeoutSeconds");

            // outputs: a different source step.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["outputs"]!)[0])["stepIndex"] = 1);
            AssertNeedsReview(MustRead(text), "edited outputs");

            // effects: the union no longer matches the steps.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root["effects"] = new JArray("read"));
            AssertNeedsReview(MustRead(text), "edited effects");

            // approvals: the requiredEffects member no longer matches.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["approvals"]!)[0])["effect"] = "asset.create");
            AssertNeedsReview(MustRead(text), "edited approvals");

            // inputs: the declared supplied input's value.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)root["inputs"]!)["factor"] = "tampered");
            AssertNeedsReview(MustRead(text), "edited inputs");
        }

        // A supplied value where executionInputs marks the input missing —
        // and a missing marker where a value was supplied — are inconsistent
        // (SPEC §A0.1: Missing and explicit null differ).
        [Test]
        public void InputsAndExecutionInputsMustAgree()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)root["inputs"]!)["spare"] = 1.5);
            AssertNeedsReview(MustRead(text), "supplied value under a missing tag");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root["inputs"] = 5);
            AssertNeedsReview(MustRead(text), "non-object inputs with a supplied tag");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                ((JObject)root["inputs"]!)["factor"] = "n2";
                var execution = (JObject)((JObject)root["planHashInput"]!)["executionInputs"]!;
                execution["factor"] = new JObject { ["kind"] = "supplied", ["value"] = "other" };
            });
            AssertNeedsReview(MustRead(text), "edited executionInputs value");

            // A persisted member that is not the rebuilt form — tags are
            // not interpreted at all, just byte-compared.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)root["planHashInput"]!)["executionInputs"] = new JObject
                {
                    ["factor"] = new JObject { ["kind"] = "bogus" },
                });
            AssertNeedsReview(MustRead(text), "malformed persisted executionInputs");
        }

        // ── SG-12 amendment checks (1)-(7), one targeted mutation each ───
        // Each edit below leaves planHash consistent with planHashInput —
        // recomputed when the hash input itself is edited — so exactly the
        // targeted check can fail, and the outcome is NeedsReview.

        // (6): the public workflowId must equal resolvedWorkflow.id.
        [Test]
        public void EditedWorkflowIdIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root["workflowId"] = "example.other");
            AssertNeedsReview(MustRead(text), "edited workflowId");
        }

        // (2): a declared input the execution omitted still needs its
        // missing tag — the rebuilt member covers the declared ids of
        // resolvedWorkflow.inputs, not just the persisted keys.
        [Test]
        public void DeclaredInputOmittedFromExecutionInputsIsRejected()
        {
            var text = MutateHashInput(PlanJsonCodec.Write(GoldenPlan()),
                input => ((JObject)input["executionInputs"]!).Remove("spare"));
            AssertNeedsReview(MustRead(text), "declared input dropped from executionInputs");
        }

        // (3): a public output's port must equal the resolvedWorkflow
        // entry's from.port for the same id.
        [Test]
        public void EditedOutputPortIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["outputs"]!)[0])["port"] = "bogus");
            AssertNeedsReview(MustRead(text), "edited output port");
        }

        // (2): a step's timeoutSeconds must equal the persisted timeouts
        // member entry.
        [Test]
        public void EditedStepTimeoutIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[2])["timeoutSeconds"] = 9);
            AssertNeedsReview(MustRead(text), "edited timeoutSeconds");
        }

        // (4): the public effects member is the deterministic union of the
        // steps' effects.
        [Test]
        public void EditedEffectsUnionIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => root["effects"] = new JArray("read", "asset.create"));
            AssertNeedsReview(MustRead(text), "edited effects");
        }

        // (5): a step's descriptorHash must equal its
        // usedOperationFingerprints entry — the steps member does not carry
        // the fingerprint, so only this check can fail.
        [Test]
        public void EditedStepDescriptorHashIsRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[0])["descriptorHash"] =
                    new string('0', 64));
            AssertNeedsReview(MustRead(text), "edited descriptorHash");
        }

        // (7): the nested canonicalJsonVersion must equal the public one —
        // even with planHash recomputed over the edited input.
        [Test]
        public void NestedCanonicalJsonVersionIsRejected()
        {
            var text = MutateHashInput(PlanJsonCodec.Write(GoldenPlan()),
                input => input["canonicalJsonVersion"] = 2);
            AssertNeedsReview(MustRead(text), "edited nested canonicalJsonVersion");
        }

        // Steps that share an Operation must record one fingerprint: a
        // divergence is inconsistent even when the fingerprint member is not
        // edited.
        [Test]
        public void DivergentStepFingerprintsAreRejected()
        {
            var data = GoldenData();
            data.Nodes!.Insert(3, Node("d", "example.count",
                new Dictionary<string, JToken> { ["count"] = JToken.Parse("5") }));
            data.ControlEdges = new List<ControlEdge>
            {
                new ControlEdge("start", "a"),
                new ControlEdge("a", "b"),
                new ControlEdge("b", "d"),
                new ControlEdge("d", "c"),
            };
            var plan = PlanOf(data, Json("{\"factor\":\"n2\"}"));
            Assert.That(plan.Steps.Select(s => s.OperationId),
                Is.EqualTo(new[] { "core.start", "example.place", "example.count", "example.count", "example.sink" }));

            var text = Mutate(PlanJsonCodec.Write(plan),
                root => ((JObject)((JArray)root["steps"]!)[3])["descriptorHash"] = new string('0', 64));
            AssertNeedsReview(MustRead(text), "divergent fingerprint for one Operation");
        }

        // The persisted hash input must itself be exactly the twelve SG-12
        // fields: an extra or missing member is a format violation (Corrupt),
        // while an inner canonicalJsonVersion contradicting the document's
        // public version is a revalidation failure (NeedsReview).
        [Test]
        public void HashInputMemberSetIsClosed()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)root["planHashInput"]!)["extra"] = 1);
            AssertCorrupt(MustRead(text), "extra hashInput member");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)root["planHashInput"]!).Remove("resolvedWorkflow"));
            AssertCorrupt(MustRead(text), "missing hashInput member");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)root["planHashInput"]!)["canonicalJsonVersion"] = 2);
            AssertNeedsReview(MustRead(text), "inner version differs from the document's");
        }

        // The non-derivable nested members of the hash input keep their v1
        // shapes too — executionInputs is derivable and checked against the
        // rebuild instead (InputsAndExecutionInputsMustAgree).
        [Test]
        public void HashInputMemberShapesAreChecked()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var environment = (JObject)((JObject)root["planHashInput"]!)["environmentIdentifiers"]!;
                environment.Remove("unityVersion");
            });
            AssertCorrupt(MustRead(text), "environmentIdentifiers member missing");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var environment = (JObject)((JObject)root["planHashInput"]!)["environmentIdentifiers"]!;
                environment["unityVersion"] = string.Empty;
            });
            AssertCorrupt(MustRead(text), "empty environment value");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var fingerprints = (JArray)((JObject)root["planHashInput"]!)["usedCodecFingerprints"]!;
                ((JObject)fingerprints[0])["extra"] = 1;
            });
            AssertCorrupt(MustRead(text), "extra codec fingerprint member");
        }

        // ── Step-level strictness ──────────────────────────────────────────

        [Test]
        public void StepMemberViolationsAreRejected()
        {
            // Extra member.
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[0])["extra"] = 1);
            AssertCorrupt(MustRead(text), "extra step member");

            // Missing member.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[0]).Remove("retry"));
            AssertCorrupt(MustRead(text), "missing step member");

            // Out-of-sequence index.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[0])["index"] = 1);
            AssertCorrupt(MustRead(text), "index out of sequence");

            // Duplicate nodeId.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[1])["nodeId"] = "start");
            AssertCorrupt(MustRead(text), "duplicate nodeId");

            // A bad idempotency spelling.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[0])["idempotency"] = "Idempotent");
            AssertCorrupt(MustRead(text), "non-snake_case idempotency");

            // An unknown idempotency value.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[0])["idempotency"] = "magic");
            AssertCorrupt(MustRead(text), "unknown idempotency");

            // A mistyped version.
            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JArray)root["steps"]!)[0])["version"] = "1");
            AssertCorrupt(MustRead(text), "mistyped version");
        }

        // The five SG-12 InputSource tags are the complete set — an unknown
        // kind, extra member or missing required member rejects (SPEC §0.6
        // SG-12, T-W1.3-05).
        [Test]
        public void StepInputTagViolationsAreRejected()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var inputs = (JObject)((JObject)((JArray)root["steps"]!)[1])["inputs"]!;
                inputs["position"] = new JObject { ["kind"] = "teleport" };
            });
            AssertCorrupt(MustRead(text), "unknown InputSource kind");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var inputs = (JObject)((JObject)((JArray)root["steps"]!)[1])["inputs"]!;
                inputs["payload"] = new JObject { ["kind"] = "missing", ["value"] = 1 };
            });
            AssertCorrupt(MustRead(text), "missing tag with extra member");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var inputs = (JObject)((JObject)((JArray)root["steps"]!)[1])["inputs"]!;
                inputs["position"] = new JObject { ["kind"] = "constant" };
            });
            AssertCorrupt(MustRead(text), "constant tag without value");
        }

        // A from_step source must name an earlier step (SPEC §D5 item 5) —
        // self and later references are inconsistent.
        [Test]
        public void FromStepMustNameAnEarlierStep()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var inputs = (JObject)((JObject)((JArray)root["steps"]!)[3])["inputs"]!;
                ((JObject)inputs["total"]!)["stepIndex"] = 3;
            });
            AssertCorrupt(MustRead(text), "from_step self-reference");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()), root =>
            {
                var inputs = (JObject)((JObject)((JArray)root["steps"]!)[3])["inputs"]!;
                ((JObject)inputs["total"]!)["stepIndex"] = 9;
            });
            AssertCorrupt(MustRead(text), "from_step beyond the plan");
        }

        // A transition target is End (-1) or an existing step (SPEC §D7
        // item 2).
        [Test]
        public void TransitionsStayInRange()
        {
            var text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JObject)((JArray)root["steps"]!)[0])["transitions"]!)["next"] = -2);
            AssertCorrupt(MustRead(text), "transition below -1");

            text = Mutate(PlanJsonCodec.Write(GoldenPlan()),
                root => ((JObject)((JObject)((JArray)root["steps"]!)[0])["transitions"]!)["next"] = "1");
            AssertCorrupt(MustRead(text), "transition not an integer");
        }

        // A round-trip covers every InputSource kind and a plan without
        // workflow inputs: a minimal start→count document.
        [Test]
        public void MinimalPlanRoundTrips()
        {
            var data = new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.minimal",
                Nodes = new List<NodeData>
                {
                    Node("start", "core.start"),
                    Node("b", "example.count",
                        new Dictionary<string, JToken> { ["count"] = JToken.Parse("1") }),
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "b") },
            };
            var plan = PlanOf(data);
            var read = MustRead(PlanJsonCodec.Write(plan));

            Assert.AreEqual(PlanJsonStatus.Decoded, read.Status);
            AssertPlansEqual(plan, read.Plan!);
            Assert.AreEqual("{}", ((JObject)read.Plan!.PlanHashInput.ToToken()["executionInputs"]!)
                .ToString(Newtonsoft.Json.Formatting.None));
        }

        // resolvedWorkflow.outputs is id-sorted while the public outputs
        // keep the declaration order — check (3) of the amendment compares
        // by id, never by position, so both orders load.
        [Test]
        public void ReversedOutputDeclarationOrderRoundTrips()
        {
            var data = GoldenData();
            data.Outputs = new List<WorkflowOutput>
            {
                new WorkflowOutput("zebra", new PortRef("b", "total")),
                new WorkflowOutput("apple", new PortRef("b", "total")),
            };
            var plan = PlanOf(data, Json("{\"factor\":\"n2\"}"));
            Assert.That(plan.Outputs.Select(o => o.Id),
                Is.EqualTo(new[] { "zebra", "apple" }));
            var resolved = (JObject)plan.PlanHashInput.ToToken()["resolvedWorkflow"]!;
            Assert.That(
                ((JArray)resolved["outputs"]!).Select(o => (string)((JObject)o)["id"]!),
                Is.EqualTo(new[] { "apple", "zebra" }));

            var read = MustRead(PlanJsonCodec.Write(plan));
            Assert.AreEqual(PlanJsonStatus.Decoded, read.Status);
            AssertPlansEqual(plan, read.Plan!);
        }

        // A declared input omitted at execution persists as the SG-12
        // missing tag and reloads — check (2) rebuilds it from the declared
        // inputs of resolvedWorkflow.
        [Test]
        public void DeclaredInputOmittedAtExecutionRoundTrips()
        {
            var plan = PlanOf(GoldenData());
            var read = MustRead(PlanJsonCodec.Write(plan));

            Assert.AreEqual(PlanJsonStatus.Decoded, read.Status);
            AssertPlansEqual(plan, read.Plan!);
            var execution = (JObject)((JObject)read.Plan!.PlanHashInput
                .ToToken())["executionInputs"]!;
            foreach (var id in new[] { "factor", "spare" })
            {
                Assert.AreEqual("missing",
                    (string)((JObject)execution[id]!)["kind"]!, id);
            }
        }

        // A Plan carrying approval requirements round-trips — the approvals
        // feed the requiredEffects rebuild of check (2).
        [Test]
        public void PlanWithApprovalsRoundTrips()
        {
            var plan = GoldenPlan();
            Assert.IsNotEmpty(plan.Approvals);

            var read = MustRead(PlanJsonCodec.Write(plan));
            Assert.AreEqual(PlanJsonStatus.Decoded, read.Status);
            AssertPlansEqual(plan, read.Plan!);
        }

        // The frozen golden document (SG-12): the planHash equals the
        // T-W1.6-07 input-vector hash because the fixture is the same;
        // everything else — every public field, step member, enum spelling
        // and the verbatim twelve-field planHashInput — is the persisted
        // form this ticket adds. Regenerate only with a deliberate format
        // change.
        private static class Golden
        {
            internal const string PlanHash = "76ee71194504f45f5a42b7d88c2f5fb794934b931f29f45261858951dd79a6bf";

            internal const string Text =
                "{\"approvals\":[{\"detail\":\"example.sink v1\",\"effect\":\"asset.delete\",\"nodeId\":\"c\"}],\"canonicalJsonVersion\":1,\"effects\":[\"read\",\"asset.delete\"],\"formatVersion\":1,\"inputs\":{\"factor\":\"n2"
                + "\"},\"outputs\":[{\"id\":\"total\",\"port\":\"total\",\"stepIndex\":2}],\"planHash\":\"76ee71194504f45f5a42b7d88c2f5fb794934b931f29f45261858951dd79a6bf\",\"planHashInput\":{\"canonicalJsonVersion\":1,\""
                + "environmentIdentifiers\":{\"activeBuildTarget\":\"StandaloneWindows64\",\"apiCompatibilityLevel\":\"NET_Standard\",\"editorOS\":\"Windows\",\"scriptingBackend\":\"Mono2x\",\"scriptingDefineSymbols\":"
                + "[\"DEBUG\",\"TRACE\",\"UNITY_EDITOR\"],\"unityVersion\":\"6000.0.85f1\"},\"executionInputs\":{\"factor\":{\"kind\":\"supplied\",\"value\":\"n2\"},\"spare\":{\"kind\":\"missing\"}},\"requiredEffects\":{\"approval"
                + "s\":[{\"detail\":\"example.sink v1\",\"effect\":\"asset.delete\",\"nodeId\":\"c\"}],\"steps\":[{\"effects\":[],\"index\":0},{\"effects\":[\"read\"],\"index\":1},{\"effects\":[\"read\"],\"index\":2},{\"effects\":[\""
                + "asset.delete\"],\"index\":3}]},\"resolvedWorkflow\":{\"controlEdges\":[{\"from\":\"a\",\"fromPort\":\"next\",\"to\":\"b\"},{\"from\":\"b\",\"fromPort\":\"next\",\"to\":\"c\"},{\"from\":\"start\",\"fromPort\":\"next\",\"t"
                + "o\":\"a\"}],\"dataEdges\":[{\"from\":{\"node\":\"b\",\"port\":\"total\"},\"to\":{\"node\":\"c\",\"port\":\"total\"}}],\"id\":\"example.plan\",\"inputBindings\":[{\"fromInput\":\"factor\",\"to\":{\"node\":\"c\",\"port\":\"nam"
                + "e\"}}],\"inputs\":[{\"id\":\"factor\",\"required\":false,\"type\":\"core.String\"},{\"id\":\"spare\",\"required\":false,\"type\":\"core.Float\"}],\"nodes\":[{\"id\":\"a\",\"operation\":\"example.place\",\"parameter"
                + "s\":{\"position\":{\"x\":0e0,\"y\":1e0,\"z\":0e0}},\"version\":1},{\"id\":\"b\",\"operation\":\"example.count\",\"parameters\":{\"count\":3},\"version\":1},{\"id\":\"c\",\"operation\":\"example.sink\",\"parameters\""
                + ":{},\"version\":1},{\"id\":\"start\",\"operation\":\"core.start\",\"parameters\":{},\"version\":1}],\"outputs\":[{\"from\":{\"node\":\"b\",\"port\":\"total\"},\"id\":\"total\"}],\"schemaVersion\":\"1.0\"},\"retry\":["
                + "{\"initialBackoffMs\":500,\"maxAttempts\":1,\"maxBackoffMs\":10000,\"multiplier\":2e0},{\"initialBackoffMs\":500,\"maxAttempts\":1,\"maxBackoffMs\":10000,\"multiplier\":2e0},{\"initialBackoffMs\":50"
                + "0,\"maxAttempts\":3,\"maxBackoffMs\":10000,\"multiplier\":2e0},{\"initialBackoffMs\":500,\"maxAttempts\":1,\"maxBackoffMs\":10000,\"multiplier\":2e0}],\"steps\":[{\"idempotency\":\"unsafe\",\"index\":0,"
                + "\"inputs\":{},\"nodeId\":\"start\",\"operationId\":\"core.start\",\"outputs\":{},\"version\":1},{\"idempotency\":\"idempotent\",\"index\":1,\"inputs\":{\"payload\":{\"kind\":\"missing\"},\"position\":{\"kind\":\"c"
                + "onstant\",\"value\":{\"x\":0e0,\"y\":1e0,\"z\":0e0}}},\"nodeId\":\"a\",\"operationId\":\"example.place\",\"outputs\":{},\"version\":1},{\"idempotency\":\"reconcilable\",\"index\":2,\"inputs\":{\"amount\":{\"kind\""
                + ":\"default\",\"value\":15e-1},\"count\":{\"kind\":\"constant\",\"value\":3},\"note\":{\"kind\":\"missing\"}},\"nodeId\":\"b\",\"operationId\":\"example.count\",\"outputs\":{\"total\":\"core.Float\"},\"version\":1},"
                + "{\"idempotency\":\"unsafe\",\"index\":3,\"inputs\":{\"name\":{\"inputId\":\"factor\",\"kind\":\"from_input\"},\"total\":{\"kind\":\"from_step\",\"port\":\"total\",\"stepIndex\":2}},\"nodeId\":\"c\",\"operationId\":\"e"
                + "xample.sink\",\"outputs\":{},\"version\":1}],\"timeouts\":[30,30,42,30],\"transitions\":[{\"next\":1},{\"next\":2},{\"next\":3},{\"next\":-1}],\"usedCodecFingerprints\":[{\"codecTypeFullName\":\"Test.Co"
                + "decs.core.String\",\"coreApiVersion\":\"1.0.0\",\"definingAssemblyName\":\"Test.Codecs\",\"definingAssemblySha256\":\"2222222222222222222222222222222222222222222222222222222222222222\",\"provide"
                + "rId\":\"test.provider\",\"typeId\":\"core.String\"},{\"codecTypeFullName\":\"Test.Codecs.core.Int\",\"coreApiVersion\":\"1.0.0\",\"definingAssemblyName\":\"Test.Codecs\",\"definingAssemblySha256\":\"222"
                + "2222222222222222222222222222222222222222222222222222222222222\",\"providerId\":\"test.provider\",\"typeId\":\"core.Int\"},{\"codecTypeFullName\":\"Test.Codecs.core.Json\",\"coreApiVersion\":\"1.0."
                + "0\",\"definingAssemblyName\":\"Test.Codecs\",\"definingAssemblySha256\":\"2222222222222222222222222222222222222222222222222222222222222222\",\"providerId\":\"test.provider\",\"typeId\":\"core.Json"
                + "\"},{\"codecTypeFullName\":\"Test.Codecs.core.Float\",\"coreApiVersion\":\"1.0.0\",\"definingAssemblyName\":\"Test.Codecs\",\"definingAssemblySha256\":\"2222222222222222222222222222222222222222222"
                + "222222222222222222222\",\"providerId\":\"test.provider\",\"typeId\":\"core.Float\"},{\"codecTypeFullName\":\"Test.Codecs.unity.Vector3\",\"coreApiVersion\":\"1.0.0\",\"definingAssemblyName\":\"Test.Co"
                + "decs\",\"definingAssemblySha256\":\"2222222222222222222222222222222222222222222222222222222222222222\",\"providerId\":\"test.provider\",\"typeId\":\"unity.Vector3\"}],\"usedOperationFingerprints"
                + "\":[{\"coreApiVersion\":\"1.0.0\",\"descriptorHash\":\"4968baac5e324401c707ece5479bccc91bd9ab631160d7d263e86de97197c357\",\"id\":\"core.start\",\"implementationHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaa"
                + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"version\":1},{\"coreApiVersion\":\"1.0.0\",\"descriptorHash\":\"9b7d921aedec432ad501ed1ec7f5a372bc0bcb16cfd763410495edccf75e6138\",\"id\":\"example.coun"
                + "t\",\"implementationHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"version\":1},{\"coreApiVersion\":\"1.0.0\",\"descriptorHash\":\"49ebe7145b42d42577403e37a3370b43"
                + "b01b4fe96aa477295772f6725d88ec68\",\"id\":\"example.place\",\"implementationHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"version\":1},{\"coreApiVersion\":\"1.0.0"
                + "\",\"descriptorHash\":\"3e80f0a071ca8105a326e3d8a0eb724a49f3631c9d75935298c26d252020af81\",\"id\":\"example.sink\",\"implementationHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                + "aaaaaaaaaaaa\",\"version\":1}],\"usedTypeFingerprints\":[{\"coreApiVersion\":\"1.0.0\",\"definingAssemblyName\":\"Test.Types\",\"definingAssemblySha256\":\"357c11aeca744334bb2728949bf9c3dc6b65df2a"
                + "91dc540c4c3ad4755888a972\",\"providerId\":\"test.provider\",\"typeDefinitionHash\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"typeId\":\"core.Float\"},{\"coreApiVersi"
                + "on\":\"1.0.0\",\"definingAssemblyName\":\"Test.Types\",\"definingAssemblySha256\":\"48a0ae398129708598de901c99c8baa0fef165cfd16704848c8bc12e38823738\",\"providerId\":\"test.provider\",\"typeDefini"
                + "tionHash\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"typeId\":\"core.Int\"},{\"coreApiVersion\":\"1.0.0\",\"definingAssemblyName\":\"Test.Types\",\"definingAssemblySha"
                + "256\":\"3e4937c6e8be55ee8e6ad3405adfc4bfb873277d6ef94403a3c278a26519061a\",\"providerId\":\"test.provider\",\"typeDefinitionHash\":\"111111111111111111111111111111111111111111111111111111111"
                + "1111111\",\"typeId\":\"core.Json\"},{\"coreApiVersion\":\"1.0.0\",\"definingAssemblyName\":\"Test.Types\",\"definingAssemblySha256\":\"3d79f1145e61e3a346466327a59229ab6d7aeee5675396206c462e62a356a"
                + "3f4\",\"providerId\":\"test.provider\",\"typeDefinitionHash\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"typeId\":\"core.String\"},{\"coreApiVersion\":\"1.0.0\",\"definin"
                + "gAssemblyName\":\"Test.Types\",\"definingAssemblySha256\":\"5e2bac3d4da1e10de17aabbd2e2c9fc1c424ff6e00a9fed259cfe9977bdc4f58\",\"providerId\":\"test.provider\",\"typeDefinitionHash\":\"111111111"
                + "1111111111111111111111111111111111111111111111111111111\",\"typeId\":\"unity.Vector3\"}]},\"planId\":\"plan_01J00000000000000000000000\",\"registryHash\":\"4609e7eb8bfee5caac6b24140b256e852dba"
                + "627ccd5ee99bbd6b22cffa038636\",\"sourceMap\":[],\"steps\":[{\"descriptorHash\":\"4968baac5e324401c707ece5479bccc91bd9ab631160d7d263e86de97197c357\",\"effects\":[],\"idempotency\":\"unsafe\",\"impl"
                + "ementationHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"index\":0,\"inputs\":{},\"nodeId\":\"start\",\"operationId\":\"core.start\",\"outputTypes\":{},\"retry\":{\"init"
                + "ialBackoffMs\":500,\"maxAttempts\":1,\"maxBackoffMs\":10000,\"multiplier\":2e0},\"timeoutSeconds\":30,\"transitions\":{\"next\":1},\"version\":1},{\"descriptorHash\":\"49ebe7145b42d42577403e37a3370b"
                + "43b01b4fe96aa477295772f6725d88ec68\",\"effects\":[\"read\"],\"idempotency\":\"idempotent\",\"implementationHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"index\":1,"
                + "\"inputs\":{\"payload\":{\"kind\":\"missing\"},\"position\":{\"kind\":\"constant\",\"value\":{\"x\":0e0,\"y\":1e0,\"z\":0e0}}},\"nodeId\":\"a\",\"operationId\":\"example.place\",\"outputTypes\":{},\"retry\":{\"initi"
                + "alBackoffMs\":500,\"maxAttempts\":1,\"maxBackoffMs\":10000,\"multiplier\":2e0},\"timeoutSeconds\":30,\"transitions\":{\"next\":2},\"version\":1},{\"descriptorHash\":\"9b7d921aedec432ad501ed1ec7f5a37"
                + "2bc0bcb16cfd763410495edccf75e6138\",\"effects\":[\"read\"],\"idempotency\":\"reconcilable\",\"implementationHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"index\":2"
                + ",\"inputs\":{\"amount\":{\"kind\":\"default\",\"value\":15e-1},\"count\":{\"kind\":\"constant\",\"value\":3},\"note\":{\"kind\":\"missing\"}},\"nodeId\":\"b\",\"operationId\":\"example.count\",\"outputTypes\":{\"tot"
                + "al\":\"core.Float\"},\"retry\":{\"initialBackoffMs\":500,\"maxAttempts\":3,\"maxBackoffMs\":10000,\"multiplier\":2e0},\"timeoutSeconds\":42,\"transitions\":{\"next\":3},\"version\":1},{\"descriptorHash\""
                + ":\"3e80f0a071ca8105a326e3d8a0eb724a49f3631c9d75935298c26d252020af81\",\"effects\":[\"asset.delete\"],\"idempotency\":\"unsafe\",\"implementationHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                + "aaaaaaaaaaaaaaaaaaaaaaaa\",\"index\":3,\"inputs\":{\"name\":{\"inputId\":\"factor\",\"kind\":\"from_input\"},\"total\":{\"kind\":\"from_step\",\"port\":\"total\",\"stepIndex\":2}},\"nodeId\":\"c\",\"operationId\":"
                + "\"example.sink\",\"outputTypes\":{},\"retry\":{\"initialBackoffMs\":500,\"maxAttempts\":1,\"maxBackoffMs\":10000,\"multiplier\":2e0},\"timeoutSeconds\":30,\"transitions\":{\"next\":-1},\"version\":1}],\""
                + "workflowHash\":\"45f40b4b24624f18005a66e2463650348fc0a6d5c2641cb2e416b5f84e2d2b8c\",\"workflowId\":\"example.plan\"}";
        }
    }
}
