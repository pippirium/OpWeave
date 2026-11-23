#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the JSON-level <see cref="StepContext"/>
    /// implementation and the SPEC §E5/§B7.1 output pipeline
    /// (local:T-W1.8-04:value-store): Get/SetOutput over resolved
    /// <see cref="InputValue"/>s, and <see cref="StepOutputs.Collect"/>
    /// producing the normalized outputs object — a custom-typed output
    /// journaled byte-identically and restored into the ValueStore —
    /// while a missing, mistyped, invalid, non-idempotent, exceptional or
    /// oversized output fails OW-RUN-007 with no partial stored value.
    /// </summary>
    internal sealed class JsonStepContextTests
    {
        private const string Session = "session-stepcontext-test";
        private const string Ts = "2026-11-06T00:00:00.000Z";
        private const string WorkflowId = "example.stepcontext";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.JsonStepContext." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        // ── Codecs under test ───────────────────────────────────────────────

        // A non-CLR custom Codec with a typed normal form (SPEC §B7.1):
        // accepts a bare number or {"v":number} and persists both as the
        // object form — {"v":5e0} for the integer 5.
        private sealed class BoxedCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var token = value.ToToken();
                switch (token.Type)
                {
                    case JTokenType.Integer:
                    case JTokenType.Float:
                        return Array.Empty<Diagnostic>();
                    case JTokenType.Object:
                        var obj = (JObject)token;
                        var member = obj["v"];
                        if (obj.Count == 1 && member is not null &&
                            (member.Type == JTokenType.Integer || member.Type == JTokenType.Float))
                        {
                            return Array.Empty<Diagnostic>();
                        }
                        break;
                }
                return new[]
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003, "Value is not a boxed number."),
                };
            }

            public JsonValue Normalize(JsonValue value)
            {
                var token = value.ToToken();
                double number;
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                {
                    number = token.ToObject<double>();
                }
                else
                {
                    number = ((JObject)token)["v"]!.ToObject<double>();
                }
                return JsonValue.From(new JObject { ["v"] = new JValue(number) });
            }
        }

        // Throws inside Normalize; the exception text embeds a fake secret
        // that must never reach a diagnostic (SPEC §B7.1 item 4).
        private sealed class ThrowingCodec : IValueCodec
        {
            internal const string Secret = "sk-live-9f8e7d6c";

            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) =>
                CodecProbeTokens.IsProbe(value)
                    ? value
                    : throw new InvalidOperationException(
                        "upstream API key '" + Secret + "' expired");
        }

        // Every Normalize adds one — never idempotent (SPEC §B7.1 item 3).
        // Behaves on the registration probe inputs so the fixture still
        // registers; real inputs stay non-idempotent (T-W2.12-03).
        private sealed class NonIdempotentCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value)
            {
                if (CodecProbeTokens.IsProbe(value))
                {
                    return value;
                }
                var token = (JValue)value.ToToken();
                return JsonValue.From(new JValue((long)token.Value! + 1L));
            }
        }

        // Builds every Validate diagnostic field it controls — message,
        // hint, node, port and JsonPath — from the rejected value, which
        // may carry a secret. The emitted OW-RUN-007 may keep the
        // published §J code but none of that text (SPEC §B7.1 item 4).
        private sealed class LeakyValidateCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var text = value.CanonicalText;
                return new[]
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "rejected " + text,
                        nodeId: text,
                        port: text,
                        jsonPath: "$." + text,
                        hint: "hint " + text),
                };
            }

            public JsonValue Normalize(JsonValue value) => value;
        }

        // The diagnostic's Code is Codec-controlled as well — a Codec may
        // construct <see cref="Diagnostic"/> directly — so an unpublished
        // code string built from the rejected value must be dropped, not
        // kept in the hint.
        private sealed class LeakyCodeCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var text = value.CanonicalText;
                return new Diagnostic[]
                {
                    new Diagnostic(
                        "OW-X-" + text,
                        Severity.Error,
                        "rejected " + text,
                        Hint: "hint " + text),
                };
            }

            public JsonValue Normalize(JsonValue value) => value;
        }

        private sealed class TestTypeProvider : ITypeProvider
        {
            private readonly TypeDefinition[] _types;

            internal TestTypeProvider(params TypeDefinition[] types) => _types = types;

            public IEnumerable<TypeDefinition> GetTypes() => _types;
        }

        private static TypeDefinition CustomType(string id, IValueCodec codec) =>
            new TypeDefinition(
                id, TypeKind.Value, 0, Array.Empty<string>(), null, "a custom test type", codec);

        private static ITypeRegistry Registry()
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "acme",
                    new TestTypeProvider(
                        CustomType("acme.Boxed", new BoxedCodec()),
                        CustomType("acme.Throw", new ThrowingCodec()),
                        CustomType("acme.NotIdempotent", new NonIdempotentCodec()),
                        CustomType("acme.LeakyValidate", new LeakyValidateCodec()),
                        CustomType("acme.LeakyCode", new LeakyCodeCodec())),
                    OperationOrigin.Custom),
            });
            Assert.AreEqual(0, build.Failures.Count, "registry build failed");
            return build.Registry;
        }

        private static readonly ITypeRegistry s_types = Registry();

        // ── Fixtures ────────────────────────────────────────────────────────

        private static string Hash64(char c) => new string(c, 64);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        private static JsonStepContext Context(
            IReadOnlyDictionary<string, InputValue>? inputs = null,
            JsonValue? resumeToken = null,
            IReadOnlyDictionary<string, string>? pendingProducers = null) =>
            new JsonStepContext(
                inputs ?? new Dictionary<string, InputValue>(),
                new OperationContractFixtures.Changes(0, "node"),
                new OperationContractFixtures.LogEntries(0),
                resumeToken,
                pendingProducers);

        private static PlanStep Step(
            int index, string nodeId, IReadOnlyDictionary<string, string> outputTypes) =>
            new PlanStep(
                index, nodeId, "test.dummy", 1, Hash64('d'), Hash64('e'),
                new Dictionary<string, InputSource>(),
                outputTypes,
                new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(), 30, new RetryPolicy(), Idempotency.Idempotent);

        // The registered-Operation lookup Collect resolves the step's
        // recorded (id, version) through (SPEC §1.4): a descriptor whose
        // outputs mirror the step's declared OutputTypes — non-nullable and
        // unconstrained unless the caller supplies the ports — or null to
        // model a registration that cannot be resolved.
        private sealed class StubOperationSource : IOperationSource
        {
            private readonly OperationDescriptor? _descriptor;

            internal StubOperationSource(OperationDescriptor? descriptor) =>
                _descriptor = descriptor;

            public OperationDescriptor? Find(string id, int version) => _descriptor;
        }

        private static IOperationSource SourceFor(
            PlanStep step, IReadOnlyList<PortDescriptor>? outputs = null)
        {
            var ports = outputs;
            if (ports is null)
            {
                var derived = new List<PortDescriptor>();
                foreach (var pair in step.OutputTypes)
                {
                    derived.Add(new PortDescriptor(
                        pair.Key, pair.Value, false, null, string.Empty, null));
                }
                ports = derived;
            }
            var descriptor =
                Op.Define(step.OperationId, step.Version).Build() with { Outputs = ports };
            return new StubOperationSource(descriptor);
        }

        private static void AssertOwRun007(Result<JsonValue> result, string because)
        {
            Assert.IsFalse(result.Ok, because);
            Assert.IsTrue(result.Diagnostics.Count >= 1);
            for (var i = 0; i < result.Diagnostics.Count; i++)
            {
                Assert.AreEqual(ErrorCodes.OW_RUN_007, result.Diagnostics[i].Code, because);
            }
        }

        // ── Get / IsPresent / IsPending ────────────────────────────────────

        [Test]
        public void GetReturnsPresentInputsAndDistinguishesMissing()
        {
            var context = Context(new Dictionary<string, InputValue>
            {
                ["name"] = InputValue.Present(Json("\"player\"")),
                ["nothing"] = InputValue.Present(Json("null")),
                ["opt"] = InputValue.Missing,
            });

            Assert.AreEqual("player", context.Get<string>("name"));
            Assert.AreEqual("\"player\"", context.Get<JsonValue>("name").CanonicalText);
            Assert.AreEqual("null", context.Get<JsonValue>("nothing").CanonicalText);
            Assert.IsTrue(context.IsPresent("nothing"), "explicit null is present");
            Assert.IsFalse(context.IsPresent("opt"), "missing stays absent");
            Assert.IsFalse(context.IsPresent("undeclared"));
            Assert.Throws<InvalidOperationException>(() => context.Get<JsonValue>("opt"));
        }

        // A Ref-typed input passes through as JSON for the G2 resolver;
        // Core never materializes it (SPEC §E5).
        [Test]
        public void RefTypedInputPassesThroughAsJson()
        {
            var reference = Json("{\"kind\":\"sceneObject\",\"guid\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}");
            var context = Context(new Dictionary<string, InputValue>
            {
                ["target"] = InputValue.Present(reference),
            });

            Assert.AreEqual(reference, context.Get<JsonValue>("target"));
        }

        [Test]
        public void PendingPortsArePresentButUnreadable()
        {
            var context = Context(
                new Dictionary<string, InputValue>(),
                pendingProducers: new Dictionary<string, string> { ["later"] = "producer" });

            Assert.IsTrue(context.IsPending("later"));
            Assert.IsTrue(context.IsPresent("later"), "a pending value is a placeholder");
            var thrown = Assert.Throws<InvalidOperationException>(
                () => context.Get<JsonValue>("later"));
            Assert.IsTrue(
                thrown!.Message.Contains("producer"),
                "the pending read names the producing node (SPEC §D8 item 1)");
            Assert.IsFalse(context.IsPending("other"));
        }

        [Test]
        public void ResumeTokenAndSinksAreExposed()
        {
            var token = Json("{\"stage\":1}");
            var changes = new OperationContractFixtures.Changes(2, "node");
            var log = new OperationContractFixtures.LogEntries(2);
            var context = new JsonStepContext(
                new Dictionary<string, InputValue>(), changes, log, token);

            Assert.AreEqual(token, context.ResumeToken);
            Assert.AreSame(changes, context.Changes);
            Assert.AreSame(log, context.Log);
        }

        // ── SetOutput ───────────────────────────────────────────────────────

        [Test]
        public void SetOutputStoresJsonValuesVerbatimAndConvertsClrValues()
        {
            var context = Context();
            context.SetOutput("raw", Json("{\"x\":1}"));
            context.SetOutput("text", "hello");
            context.SetOutput("nothing", (string?)null);

            Assert.AreEqual("{\"x\":1}", context.Outputs["raw"].CanonicalText);
            Assert.AreEqual("\"hello\"", context.Outputs["text"].CanonicalText);
            Assert.AreEqual("null", context.Outputs["nothing"].CanonicalText);
            Assert.AreEqual(3, context.Outputs.Count);
        }

        // ── StepOutputs.Collect: the success path ──────────────────────────

        // Every declared output is validated and normalized under SPEC
        // §B7.1 before it can be journaled or stored: a core.Float written
        // as the integer 1 persists as 1e0 and a custom-typed output as its
        // Codec's normal form. Ports the Plan does not declare are dropped.
        [Test]
        public void CollectNormalizesDeclaredOutputs()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["float"] = "core.Float",
                ["boxed"] = "acme.Boxed",
            });
            var outputs = new Dictionary<string, JsonValue>
            {
                ["float"] = Json("1"),
                ["boxed"] = Json("5"),
                ["undeclared"] = Json("\"dropped\""),
            };

            var collected = StepOutputs.Collect(step, s_types, SourceFor(step), outputs);
            Assert.IsTrue(collected.Ok, "collect failed: {0}",
                collected.Diagnostics.Count == 0 ? "" : collected.Diagnostics[0].Message);
            Assert.AreEqual(
                "{\"boxed\":{\"v\":5e0},\"float\":1e0}",
                collected.Value!.CanonicalText);
        }

        // ── StepOutputs.Collect: every failure is OW-RUN-007 ───────────────

        [Test]
        public void CollectMissingOutputFailsOwRun007()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["echo"] = "core.String",
            });

            var collected = StepOutputs.Collect(
                step, s_types, SourceFor(step), new Dictionary<string, JsonValue>());
            AssertOwRun007(collected, "an unset output is OW-RUN-007");
            Assert.AreEqual("produce", collected.Diagnostics[0].NodeId);
            Assert.AreEqual("echo", collected.Diagnostics[0].Port);
        }

        [Test]
        public void CollectMistypedOutputFailsOwRun007()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["count"] = "core.Int",
            });

            var collected = StepOutputs.Collect(
                step, s_types, SourceFor(step),
                new Dictionary<string, JsonValue> { ["count"] = Json("\"text\"") });
            AssertOwRun007(collected, "a mistyped output is OW-RUN-007");
            Assert.AreEqual("count", collected.Diagnostics[0].Port);
        }

        [Test]
        public void CollectCodecRejectedOutputFailsOwRun007()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["boxed"] = "acme.Boxed",
            });

            var collected = StepOutputs.Collect(
                step, s_types, SourceFor(step),
                new Dictionary<string, JsonValue> { ["boxed"] = Json("\"no\"") });
            AssertOwRun007(collected, "a codec-rejected output is OW-RUN-007");
        }

        // SPEC §B7.1: a Codec's Validate diagnostics reach Collect
        // verbatim, so a Codec can build them from the rejected value —
        // which may carry a secret. The emitted OW-RUN-007 keeps fixed
        // text plus at most a published §J code; no codec-supplied field
        // (message, hint, node, port, JsonPath or code) may carry it.
        [Test]
        public void CollectCodecRejectionNeverForwardsCodecText()
        {
            const string secret = "sk-live-1e2d3c4b";
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["leaky"] = "acme.LeakyValidate",
                ["coded"] = "acme.LeakyCode",
            });
            var outputs = new Dictionary<string, JsonValue>
            {
                ["leaky"] = Json("\"" + secret + "\""),
                ["coded"] = Json("\"" + secret + "\""),
            };

            var collected = StepOutputs.Collect(step, s_types, SourceFor(step), outputs);
            AssertOwRun007(collected, "a codec-rejected output is OW-RUN-007");
            Assert.AreEqual(2, collected.Diagnostics.Count);
            foreach (var diagnostic in collected.Diagnostics)
            {
                StringAssert.DoesNotContain(secret, diagnostic.Code);
                StringAssert.DoesNotContain(secret, diagnostic.Message);
                StringAssert.DoesNotContain(secret, diagnostic.Hint ?? string.Empty);
                StringAssert.DoesNotContain(secret, diagnostic.NodeId ?? string.Empty);
                StringAssert.DoesNotContain(secret, diagnostic.Port ?? string.Empty);
                StringAssert.DoesNotContain(secret, diagnostic.JsonPath ?? string.Empty);
            }

            // The published §J code of the codec's diagnostic is the one
            // detail the hint may keep; an unpublished code is dropped.
            var leaky = DiagnosticForPort(collected.Diagnostics, "leaky");
            StringAssert.Contains(ErrorCodes.OW_TYPE_003, leaky.Hint ?? string.Empty);
            var coded = DiagnosticForPort(collected.Diagnostics, "coded");
            StringAssert.DoesNotContain("OW-X", coded.Hint ?? string.Empty);
        }

        private static Diagnostic DiagnosticForPort(IReadOnlyList<Diagnostic> diagnostics, string port)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i].Port == port)
                {
                    return diagnostics[i];
                }
            }
            Assert.Fail($"no diagnostic for port '{port}'");
            return null!;
        }

        // SPEC §B7.1 item 4: an exceptional or non-idempotent Codec at
        // output-save time fails OW-RUN-007, and the exception text — which
        // may carry secrets — never enters the diagnostic.
        [Test]
        public void CollectContractViolatingCodecFailsOwRun007WithoutSecretLeak()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["boom"] = "acme.Throw",
                ["bump"] = "acme.NotIdempotent",
            });
            var outputs = new Dictionary<string, JsonValue>
            {
                ["boom"] = Json("1"),
                ["bump"] = Json("1"),
            };

            var collected = StepOutputs.Collect(step, s_types, SourceFor(step), outputs);
            AssertOwRun007(collected, "codec contract violations are OW-RUN-007");
            Assert.AreEqual(2, collected.Diagnostics.Count);
            foreach (var diagnostic in collected.Diagnostics)
            {
                StringAssert.DoesNotContain(ThrowingCodec.Secret, diagnostic.Message);
                StringAssert.DoesNotContain(
                    ThrowingCodec.Secret, diagnostic.Hint ?? string.Empty);
            }
        }

        [Test]
        public void CollectOversizedOutputFailsOwRun007()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["big"] = "core.String",
            });
            var oversized = Json("\"" + new string('a', ValueStore.MaxValueBytes) + "\"");

            var collected = StepOutputs.Collect(
                step, s_types, SourceFor(step),
                new Dictionary<string, JsonValue> { ["big"] = oversized });
            AssertOwRun007(collected, "an output over 256 KiB is OW-RUN-007");
        }

        // The collect is all-or-nothing: a valid output beside an invalid
        // one yields no persisted object and nothing reaches the ValueStore.
        [Test]
        public void CollectFailureStoresNoPartialValue()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["good"] = "core.String",
                ["bad"] = "core.Int",
            });
            var outputs = new Dictionary<string, JsonValue>
            {
                ["good"] = Json("\"ok\""),
                ["bad"] = Json("\"not an int\""),
            };

            var collected = StepOutputs.Collect(step, s_types, SourceFor(step), outputs);
            AssertOwRun007(collected, "the step fails as a whole");

            var store = ValueStore.Empty();
            Assert.AreEqual(
                0, store.Count,
                "a failed collect leaves nothing for the store");
        }

        // ── StepOutputs.Collect: the port's declared null/constraint contract ──

        // SPEC §A5/§B0.1: an explicit null is a present value on a Nullable
        // output port — accepted and persisted as null, never Missing.
        [Test]
        public void CollectNullableOutputAcceptsExplicitNull()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["maybe"] = "core.String",
            });
            var source = SourceFor(step, new[]
            {
                new PortDescriptor(
                    "maybe", "core.String", false, null, string.Empty, null, true),
            });

            var collected = StepOutputs.Collect(
                step, s_types, source,
                new Dictionary<string, JsonValue> { ["maybe"] = Json("null") });
            Assert.IsTrue(collected.Ok, "a nullable output accepts explicit null: {0}",
                collected.Diagnostics.Count == 0 ? "" : collected.Diagnostics[0].Message);
            var member = collected.Value!.ToToken()["maybe"];
            Assert.IsNotNull(member, "the port stays a present member, never Missing");
            Assert.AreEqual(JTokenType.Null, member!.Type);
            Assert.AreEqual("{\"maybe\":null}", collected.Value!.CanonicalText);
        }

        // A non-Nullable typed output rejects an explicit null — the
        // sanctioned-absence rule admits null only where the descriptor
        // opts in or the type is core.Json (SPEC §A5/§B0.1).
        [Test]
        public void CollectNullOnNonNullableOutputFailsOwRun007()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["name"] = "core.String",
            });

            var collected = StepOutputs.Collect(
                step, s_types, SourceFor(step),
                new Dictionary<string, JsonValue> { ["name"] = Json("null") });
            AssertOwRun007(collected, "null on a non-nullable output is OW-RUN-007");
            Assert.AreEqual("name", collected.Diagnostics[0].Port);
        }

        // SPEC §B7: an output violating its declared constraints fails
        // OW-RUN-007 — the collect stays all-or-nothing beside a valid
        // sibling output, so nothing partial is stored.
        [Test]
        public void CollectConstraintViolatingOutputFailsOwRun007()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["state"] = "core.String",
                ["echo"] = "core.String",
            });
            var source = SourceFor(step, new[]
            {
                new PortDescriptor(
                    "state", "core.String", false, null, string.Empty,
                    new PortConstraints(new[] { "ready", "done" }, null, null, null, null)),
                new PortDescriptor("echo", "core.String", false, null, string.Empty, null),
            });
            var outputs = new Dictionary<string, JsonValue>
            {
                ["state"] = Json("\"other\""),
                ["echo"] = Json("\"ok\""),
            };

            var collected = StepOutputs.Collect(step, s_types, source, outputs);
            AssertOwRun007(collected, "a constraint-violating output is OW-RUN-007");
            Assert.AreEqual("state", collected.Diagnostics[0].Port);
            Assert.IsNull(collected.Value, "no partial outputs object is produced");
        }

        [Test]
        public void CollectConstraintSatisfyingOutputIsStored()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["state"] = "core.String",
            });
            var source = SourceFor(step, new[]
            {
                new PortDescriptor(
                    "state", "core.String", false, null, string.Empty,
                    new PortConstraints(new[] { "ready", "done" }, null, null, null, null)),
            });

            var collected = StepOutputs.Collect(
                step, s_types, source,
                new Dictionary<string, JsonValue> { ["state"] = Json("\"ready\"") });
            Assert.IsTrue(collected.Ok, "a constraint-satisfying output is stored: {0}",
                collected.Diagnostics.Count == 0 ? "" : collected.Diagnostics[0].Message);
            Assert.AreEqual("{\"state\":\"ready\"}", collected.Value!.CanonicalText);
        }

        // A step whose registered Operation descriptor cannot be resolved
        // fails closed with a single fixed OW-RUN-007: no port contract
        // means nothing about the outputs can be trusted.
        [Test]
        public void CollectUnresolvableOperationFailsOwRun007()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["echo"] = "core.String",
            });

            var collected = StepOutputs.Collect(
                step, s_types, new StubOperationSource(null),
                new Dictionary<string, JsonValue> { ["echo"] = Json("\"ok\"") });
            AssertOwRun007(collected, "an unresolvable descriptor fails closed");
            Assert.AreEqual(1, collected.Diagnostics.Count);
            Assert.AreEqual("produce", collected.Diagnostics[0].NodeId);
            Assert.AreEqual(
                "The step's Operation descriptor cannot be resolved.",
                collected.Diagnostics[0].Message);
        }

        // A declared output the descriptor has no port for fails closed:
        // its Nullable/Constraints contract cannot be obtained.
        [Test]
        public void CollectOutputMissingFromDescriptorFailsOwRun007()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["echo"] = "core.String",
            });
            var source = SourceFor(step, new[]
            {
                new PortDescriptor("other", "core.String", false, null, string.Empty, null),
            });

            var collected = StepOutputs.Collect(
                step, s_types, source,
                new Dictionary<string, JsonValue> { ["echo"] = Json("\"ok\"") });
            AssertOwRun007(collected, "an undeclared output port is OW-RUN-007");
            Assert.AreEqual("echo", collected.Diagnostics[0].Port);
        }

        // ── Journal persistence of the normalized output ───────────────────

        private static CompiledPlan VerifiablePlan(IReadOnlyList<PlanStep> steps)
        {
            var inputs = Json("{}");
            var approvals = new List<ApprovalRequirement>();
            var hashInput = JsonValue.From(new JObject
            {
                ["canonicalJsonVersion"] = 1,
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = "6000.0.0f1",
                    ["editorOS"] = "windows",
                    ["activeBuildTarget"] = "windows",
                    ["scriptingBackend"] = "mono",
                    ["apiCompatibilityLevel"] = "netstandard2.1",
                    ["scriptingDefineSymbols"] = new JArray(),
                },
                ["executionInputs"] = PlanHashInputParts.ExecutionInputs(
                    new List<string>(), inputs.ToToken()),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(steps, approvals),
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = WorkflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["retry"] = PlanHashInputParts.Retry(steps),
                ["steps"] = PlanHashInputParts.Steps(steps),
                ["timeouts"] = PlanHashInputParts.Timeouts(steps),
                ["transitions"] = PlanHashInputParts.Transitions(steps),
                ["usedCodecFingerprints"] = new JArray(),
                ["usedOperationFingerprints"] = PlanHashInputParts.OperationFingerprints(steps),
                ["usedTypeFingerprints"] = new JArray(),
            });
            return new CompiledPlan(
                "plan_01J00000000000000000000000",
                CanonicalJson.Sha256(hashInput.ToToken()),
                WorkflowId, Hash64('b'), Hash64('c'),
                inputs, steps, Array.Empty<PlanOutput>(),
                PlanBuilder.UnionEffects(steps), approvals,
                Array.Empty<PlanSourceMapEntry>(), hashInput);
        }

        private static WorkflowDocument Doc()
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = WorkflowId,
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                },
                ControlEdges = new List<ControlEdge>(),
            });
            Assert.IsTrue(created.Ok, "WorkflowDocument.Create failed.");
            return created.Value!;
        }

        // A normalized custom-typed output written to the real journal
        // reloads into the ValueStore byte-identically: the StepSucceeded
        // record carries the Codec's normal form, not the raw value.
        [Test]
        public void NormalizedOutputRoundTripsByteIdenticalThroughJournal()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["boxed"] = "acme.Boxed",
            });
            var plan = VerifiablePlan(new[] { step });
            var store = new FileRunStore(_root, Session);
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, plan, Doc()).Ok);

            var collected = StepOutputs.Collect(
                step, s_types, SourceFor(step),
                new Dictionary<string, JsonValue> { ["boxed"] = Json("5") });
            Assert.IsTrue(collected.Ok);
            var persisted = collected.Value!;
            Assert.AreEqual("{\"boxed\":{\"v\":5e0}}", persisted.CanonicalText);

            var appended = store.Append(
                runId,
                new JournalRecord(
                    2, Ts, Session, JournalRecordType.StepSucceeded,
                    Step: 0, Attempt: 1,
                    JsonValue.From(new JObject
                    {
                        ["outputs"] = persisted.ToToken(),
                        ["changes"] = new JArray(),
                        ["port"] = "next",
                        ["reconciled"] = false,
                    })),
                Durability.Flushed);
            Assert.IsTrue(appended.Ok);

            var loaded = store.Load(runId);
            Assert.IsTrue(loaded.Ok);
            var restored = ValueStore.Restore(loaded.Value!, s_types, Session);
            Assert.IsTrue(restored.Ok);

            var value = restored.Value!.Get(new ValueKey(0, 0, 0, "boxed"));
            Assert.IsTrue(value.IsPresent);
            Assert.AreEqual(
                JsonValue.From(persisted.ToToken()["boxed"]!).CanonicalText,
                value.Value!.CanonicalText,
                "the normalized output restores byte-identically");
            Assert.AreEqual("{\"v\":5e0}", value.Value!.CanonicalText);
        }

        // A failing collect yields OW-RUN-007 and no partial StepSucceeded
        // record reaches the journal — the store holds only the create
        // records.
        [Test]
        public void FailedCollectWritesNoPartialStepSucceededRecord()
        {
            var step = Step(0, "produce", new Dictionary<string, string>
            {
                ["echo"] = "core.String",
                ["count"] = "core.Int",
            });
            var plan = VerifiablePlan(new[] { step });
            var store = new FileRunStore(_root, Session);
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, plan, Doc()).Ok);

            var collected = StepOutputs.Collect(
                step, s_types, SourceFor(step),
                new Dictionary<string, JsonValue>
                {
                    ["echo"] = Json("\"ok\""),
                    ["count"] = Json("\"not an int\""),
                });
            AssertOwRun007(collected, "invalid output fails the step");

            // The runner appends StepSucceeded only for a collected object —
            // none exists — so the journal keeps only the create records.
            var loaded = store.Load(runId);
            Assert.IsTrue(loaded.Ok);
            var restored = ValueStore.Restore(loaded.Value!, s_types, Session);
            Assert.IsTrue(restored.Ok);
            Assert.AreEqual(
                0, restored.Value!.Count,
                "no partial outputs are restorable");

            var journal = File.ReadAllText(
                Path.Combine(_root, "Runs", runId, "journal.ndjson"));
            StringAssert.DoesNotContain(
                "step_succeeded", journal,
                "no StepSucceeded record was written for the failed step");
        }
    }
}
