#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the ValueStore and the Plan InputSource resolution of
    /// SPEC §E5, §A0.1 and §D7 (local:T-W1.8-04:value-store): keyed output
    /// storage under <see cref="ValueKey"/>, the 256 KiB per-value bound
    /// (OW-RUN-007), journal-only restoration from <c>StepSucceeded</c>
    /// records through the real <see cref="FileRunStore"/>, and the
    /// Missing-vs-explicit-null boundary — in both the dotnet and the Unity
    /// Mono runtimes.
    /// </summary>
    internal sealed class ValueStoreTests
    {
        private const string Session = "session-valuestore-test";
        private const string Ts = "2026-11-06T00:00:00.000Z";
        private const string WorkflowId = "example.valuestore";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.ValueStore." + Guid.NewGuid().ToString("N"));
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

        // ── Fixtures ────────────────────────────────────────────────────────

        private static string Hash64(char c) => new string(c, 64);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        private static PlanStep Step(
            int index,
            string nodeId,
            IReadOnlyDictionary<string, InputSource> inputs,
            IReadOnlyDictionary<string, string> outputTypes,
            IReadOnlyDictionary<string, int> transitions) =>
            new PlanStep(
                index, nodeId, "test.dummy", 1, Hash64('d'), Hash64('e'),
                inputs, outputTypes, transitions,
                Array.Empty<string>(), 30, new Operations.RetryPolicy(),
                Operations.Idempotency.Idempotent);

        // A step producing one "echo" output and handing control to the next.
        private static PlanStep Producer(int index, int next) =>
            Step(index, "produce",
                new Dictionary<string, InputSource>(),
                new Dictionary<string, string> { ["echo"] = "core.String" },
                new Dictionary<string, int> { ["next"] = next });

        // A step consuming one input per InputSource kind (SPEC §D7/§A0.1).
        private static PlanStep Consumer(int index) =>
            Step(index, "consume",
                new Dictionary<string, InputSource>
                {
                    ["echo"] = new FromStep(index - 1, "echo"),
                    ["name"] = new Constant(Json("\"world\"")),
                    ["nothing"] = new Constant(Json("null")),
                    ["level"] = new Default(Json("3")),
                    ["opt"] = new Missing(),
                    ["cfg"] = new FromInput("cfg"),
                    ["unsupplied"] = new FromInput("unsupplied"),
                },
                new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = -1 });

        // A self-contained but fully verifiable Plan (the fixture shape
        // FileRunStoreLoadTests established): every derivable planHashInput
        // member is built through the shared PlanHashInputParts helpers so
        // the §D0.1 revalidation decodes it, and planHash is its real
        // SHA-256.
        private static CompiledPlan VerifiablePlan(
            IReadOnlyList<PlanStep> steps, JsonValue inputs, JArray declaredInputs)
        {
            var declaredIds = new List<string>(declaredInputs.Count);
            foreach (var token in declaredInputs)
            {
                declaredIds.Add((string)((JObject)token)["id"]!);
            }
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
                    declaredIds, inputs.ToToken()),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(steps, approvals),
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = WorkflowId,
                    ["inputs"] = declaredInputs,
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

        private FileRunStore Store() => new FileRunStore(_root, Session);

        private static JsonValue SucceededData(string outputsJson) =>
            Json("{\"outputs\":" + outputsJson +
                 ",\"changes\":[],\"port\":\"next\",\"reconciled\":false}");

        private static void AssertOwRun007<T>(Result<T> result, string because)
        {
            Assert.IsFalse(result.Ok, because);
            for (var i = 0; i < result.Diagnostics.Count; i++)
            {
                Assert.AreEqual(ErrorCodes.OW_RUN_007, result.Diagnostics[i].Code, because);
            }
        }

        // ── ValueKey ────────────────────────────────────────────────────────

        [Test]
        public void ValueKeyHoldsFrameIterationStepIndexAndPort()
        {
            var key = new ValueKey(0, 0, 3, "echo");
            Assert.AreEqual(0, key.Frame);
            Assert.AreEqual(0, key.Iteration);
            Assert.AreEqual(3, key.StepIndex);
            Assert.AreEqual("echo", key.Port);
            Assert.AreEqual(new ValueKey(0, 0, 3, "echo"), key);
            Assert.AreNotEqual(new ValueKey(0, 0, 3, "other"), key);
            Assert.AreNotEqual(new ValueKey(1, 0, 3, "echo"), key);
        }

        // ── Store primitives ────────────────────────────────────────────────

        [Test]
        public void SetAndGetRoundTripAndAbsentKeyIsMissing()
        {
            var store = ValueStore.Empty();
            var key = new ValueKey(0, 0, 0, "echo");

            Assert.IsFalse(store.Get(key).IsPresent, "an absent key is missing");

            var stored = store.Set(key, Json("\"hello\""));
            Assert.IsTrue(stored.Ok);

            var got = store.Get(key);
            Assert.IsTrue(got.IsPresent);
            Assert.AreEqual(Json("\"hello\""), got.Value);
            Assert.AreEqual(1, store.Count);
        }

        // SPEC §E5: one value is at most 256 KiB; the oversized write is
        // OW-RUN-007 and stores nothing.
        [Test]
        public void SetOversizedValueFailsOwRun007()
        {
            var store = ValueStore.Empty();
            var key = new ValueKey(0, 0, 0, "big");

            // A JSON string of n 'a' characters canonicalizes to n + 2
            // UTF-8 bytes; n = MaxValueBytes - 2 sits exactly at the bound.
            var atLimit = Json("\"" + new string('a', ValueStore.MaxValueBytes - 2) + "\"");
            Assert.IsTrue(store.Set(key, atLimit).Ok, "a 256 KiB value is inside the bound");
            Assert.IsTrue(store.Get(key).IsPresent);

            var over = Json("\"" + new string('a', ValueStore.MaxValueBytes - 1) + "\"");
            var failed = store.Set(new ValueKey(0, 0, 1, "big"), over);
            AssertOwRun007(failed, "a value over 256 KiB is OW-RUN-007");
            Assert.IsFalse(
                store.Get(new ValueKey(0, 0, 1, "big")).IsPresent,
                "the oversized value is not stored");
        }

        // SetOutputs is all-or-nothing: an oversized member rejects the
        // whole payload and leaves no partial values.
        [Test]
        public void SetOutputsIsAtomicAndStoresWholeObject()
        {
            var store = ValueStore.Empty();
            var applied = store.SetOutputs(
                0, Json("{\"echo\":\"hello\",\"count\":2}"));
            Assert.IsTrue(applied.Ok);
            Assert.IsTrue(store.Get(new ValueKey(0, 0, 0, "echo")).IsPresent);
            Assert.AreEqual(2, store.Count);

            var rejected = store.SetOutputs(
                1, Json("{\"small\":\"x\",\"big\":\"" +
                    new string('a', ValueStore.MaxValueBytes) + "\"}"));
            AssertOwRun007(rejected, "an oversized member is OW-RUN-007");
            Assert.IsFalse(
                store.Get(new ValueKey(0, 0, 1, "small")).IsPresent,
                "no member of a rejected payload is stored");
            Assert.IsFalse(store.Get(new ValueKey(0, 0, 1, "big")).IsPresent);
        }

        [Test]
        public void SetOutputsRejectsNonObjectPayload()
        {
            var store = ValueStore.Empty();
            var failed = store.SetOutputs(0, Json("[\"echo\"]"));
            Assert.IsFalse(failed.Ok);
            Assert.AreEqual(ErrorCodes.OW_STORE_002, failed.Diagnostics[0].Code);
            Assert.AreEqual(0, store.Count);
        }

        // ── Input resolution (SPEC §E5, §A0.1) ─────────────────────────────

        // Every InputSource kind resolves: FromStep from the store, FromInput
        // from the Plan's execution inputs, Constant and Default verbatim,
        // Missing absent — an explicit JSON null stays a present value.
        [Test]
        public void ResolveInputsCoversEverySourceKind()
        {
            var store = ValueStore.Empty();
            Assert.IsTrue(store.SetOutputs(0, Json("{\"echo\":\"hello\"}")).Ok);
            var step = Consumer(1);
            var inputs = Json("{\"cfg\":\"fast\"}");

            var resolved = store.ResolveInputs(step, inputs);
            Assert.IsTrue(resolved.Ok);
            var values = resolved.Value!;

            Assert.AreEqual(Json("\"hello\""), values["echo"].Value);
            Assert.AreEqual(Json("\"world\""), values["name"].Value);
            Assert.AreEqual(Json("3"), values["level"].Value);
            Assert.AreEqual(Json("\"fast\""), values["cfg"].Value);

            // Missing and an unsupplied workflow input are absent — never a
            // JSON null (SPEC §A0.1 item 5).
            Assert.IsFalse(values["opt"].IsPresent, "a Missing source resolves absent");
            Assert.IsNull(values["opt"].Value);
            Assert.IsFalse(
                values["unsupplied"].IsPresent,
                "an unsupplied workflow input resolves absent");

            // An explicit JSON null constant is present and keeps its bytes.
            Assert.IsTrue(values["nothing"].IsPresent, "explicit null is a present value");
            Assert.AreEqual("null", values["nothing"].Value!.CanonicalText);
        }

        // A FromStep source with no recorded output fails OW-RUN-007
        // (SPEC §E5 "値の欠落"), attributed to the consuming node and port.
        [Test]
        public void ResolveInputsUnrecordedFromStepFailsOwRun007()
        {
            var store = ValueStore.Empty();
            var resolved = store.ResolveInputs(Consumer(1), Json("{}"));

            Assert.IsFalse(resolved.Ok);
            Assert.AreEqual(ErrorCodes.OW_RUN_007, resolved.Diagnostics[0].Code);
            Assert.AreEqual("consume", resolved.Diagnostics[0].NodeId);
            Assert.AreEqual("echo", resolved.Diagnostics[0].Port);
        }

        // ── Journal-only restoration (SPEC §E5 test) ───────────────────────

        // The SPEC §E5 test: a ValueStore rebuilt solely from the journal's
        // StepSucceeded records — through FileRunStore.Load — resolves the
        // same inputs the live run had.
        [Test]
        public void RestoredStoreYieldsTheSameInputs()
        {
            var producer = Producer(0, 1);
            var consumer = Consumer(1);
            var inputs = Json("{\"cfg\":\"fast\"}");
            var declaredInputs = new JArray
            {
                new JObject { ["id"] = "cfg", ["type"] = "core.String", ["required"] = true },
                new JObject { ["id"] = "unsupplied", ["type"] = "core.String", ["required"] = false },
            };
            var plan = VerifiablePlan(new[] { producer, consumer }, inputs, declaredInputs);

            var store = Store();
            var runId = Identifiers.NewRunId();
            var created = store.CreateRun(runId, plan, Doc());
            Assert.IsTrue(created.Ok, "CreateRun failed");

            // The live run: step 0 succeeded and its outputs were stored.
            var live = ValueStore.Empty();
            Assert.IsTrue(live.SetOutputs(0, Json("{\"echo\":\"hello\"}")).Ok);
            var liveResolved = live.ResolveInputs(consumer, plan.Inputs);
            Assert.IsTrue(liveResolved.Ok);

            // The journal records the same StepSucceeded (seq 2 follows the
            // store-written RunAccepted at seq 1).
            var appended = store.Append(
                runId,
                new JournalRecord(
                    2, Ts, Session, JournalRecordType.StepSucceeded,
                    Step: 0, Attempt: 1, SucceededData("{\"echo\":\"hello\"}")),
                Durability.Flushed);
            Assert.IsTrue(appended.Ok, "StepSucceeded append failed");

            var loaded = store.Load(runId);
            Assert.IsTrue(loaded.Ok, "journal Load failed");

            var restored = ValueStore.Restore(
                loaded.Value!, DescriptorTestFixtures.CreateRegistry(), Session);
            Assert.IsTrue(restored.Ok, "ValueStore restore failed");

            var restoredResolved = restored.Value!.ResolveInputs(consumer, plan.Inputs);
            Assert.IsTrue(restoredResolved.Ok);

            foreach (var port in liveResolved.Value!.Keys)
            {
                Assert.AreEqual(
                    liveResolved.Value![port], restoredResolved.Value![port],
                    $"input '{port}' differs after journal-only restore");
            }
            Assert.IsFalse(
                restoredResolved.Value!["opt"].IsPresent,
                "a Missing input is absent after restore — never null");
        }

        // A StepSucceeded outputs payload that is not a JSON object is
        // recorded state the store cannot interpret: OW-STORE-002.
        [Test]
        public void RestoreNonObjectOutputsFailsOwStore002()
        {
            var plan = VerifiablePlan(
                new[] { Producer(0, -1) }, Json("{}"), new JArray());
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, plan, Doc()).Ok);
            Assert.IsTrue(store.Append(
                runId,
                new JournalRecord(
                    2, Ts, Session, JournalRecordType.StepSucceeded,
                    Step: 0, Attempt: 1, SucceededData("[1,2]")),
                Durability.Flushed).Ok);

            var loaded = store.Load(runId);
            Assert.IsTrue(loaded.Ok);

            var restored = ValueStore.Restore(
                loaded.Value!, DescriptorTestFixtures.CreateRegistry(), Session);
            Assert.IsFalse(restored.Ok);
            Assert.AreEqual(ErrorCodes.OW_STORE_002, restored.Diagnostics[0].Code);
        }
    }
}
