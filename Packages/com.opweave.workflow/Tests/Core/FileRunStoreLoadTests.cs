#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the FileRunStore load side of SPEC §F0–§F3, §D0.1
    /// item 6 and §D7 items 4/6 (local:T-W1.7-03:runstore-load-integrity):
    /// the journal fold into RunSnapshot, planFileDigest / version / planHash
    /// verification, the broken-last-line drop with the "F5 decision
    /// required" flag, every NeedsReview classification, the regenerable
    /// state.json/index.json caches, List and Delete — in both the dotnet and
    /// the Unity Mono runtimes.
    /// </summary>
    internal sealed class FileRunStoreLoadTests
    {
        private const string Session = "session-load-test";
        private const string Ts = "2026-11-06T00:00:00.000Z";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunStoreLoad." + Guid.NewGuid().ToString("N"));
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

        private FileRunStore Store() => new FileRunStore(_root, Session);

        private static string Hash64(char c) => new string(c, 64);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        // A self-contained but fully verifiable Plan: the SG-12 twelve-member
        // planHashInput carries the shapes PlanJsonCodec.Read validates
        // (resolvedWorkflow id/inputs/outputs, the six-member
        // environmentIdentifiers, requiredEffects as the steps+approvals
        // object) and planHash is its real SHA-256, so the §D0.1
        // revalidation decodes it.
        private static CompiledPlan Plan(int approvalCount = 0)
        {
            var approvals = new List<ApprovalRequirement>();
            var approvalEntries = new JArray();
            for (var i = 0; i < approvalCount; i++)
            {
                approvals.Add(new ApprovalRequirement(
                    "node" + i, "asset.modify", "example.op@1"));
                approvalEntries.Add(new JObject
                {
                    ["nodeId"] = "node" + i,
                    ["effect"] = "asset.modify",
                    ["detail"] = "example.op@1",
                });
            }
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
                ["executionInputs"] = new JObject(),
                ["requiredEffects"] = new JObject
                {
                    ["steps"] = new JArray(),
                    ["approvals"] = approvalEntries,
                },
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = "example.runstore",
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["retry"] = new JArray(),
                ["steps"] = new JArray(),
                ["timeouts"] = new JArray(),
                ["transitions"] = new JArray(),
                ["usedCodecFingerprints"] = new JArray(),
                ["usedOperationFingerprints"] = new JArray(),
                ["usedTypeFingerprints"] = new JArray(),
            });
            return new CompiledPlan(
                "plan_01J00000000000000000000000",
                CanonicalJson.Sha256(hashInput.ToToken()),
                "example.runstore", Hash64('b'), Hash64('c'),
                Json("{}"),
                Array.Empty<PlanStep>(), Array.Empty<PlanOutput>(),
                Array.Empty<string>(), approvals,
                Array.Empty<PlanSourceMapEntry>(), hashInput);
        }

        private static WorkflowDocument Doc()
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.runstore",
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                },
                ControlEdges = new List<ControlEdge>(),
            });
            Assert.IsTrue(created.Ok, "WorkflowDocument.Create failed.");
            return created.Value!;
        }

        private string RunDir(string runId) => Path.Combine(_root, "Runs", runId);

        private string JournalPath(string runId) => Path.Combine(RunDir(runId), "journal.ndjson");

        private string PlanPath(string runId) => Path.Combine(RunDir(runId), "plan.json");

        private string[] JournalLines(string runId) => File.ReadAllLines(JournalPath(runId));

        private static JournalRecord Record(
            long seq, JournalRecordType type, string dataJson,
            int? step = null, int? attempt = null) =>
            new JournalRecord(seq, Ts, Session, type, step, attempt, Json(dataJson));

        private static string Sha256LowerHex(byte[] bytes)
        {
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        // A hand-built envelope line with a correctly computed crc — used to
        // write records JournalCodec.WriteRecord cannot produce (unknown
        // types, a missing or non-boolean required flag, a missing
        // planFileDigest). A null required omits the member.
        private static string Envelope(
            long seq, string type, JToken? required, string dataJson, int? step = null)
        {
            var env = new JObject
            {
                ["seq"] = seq,
                ["ts"] = Ts,
                ["session"] = Session,
                ["type"] = type,
            };
            if (required is { } flag)
            {
                env["required"] = flag;
            }
            if (step is { } s)
            {
                env["step"] = s;
            }
            env["data"] = (JObject)Json(dataJson).ToToken();
            env["crc"] = LineCrc(env);
            return CanonicalJson.Serialize(env);
        }

        private static string LineCrc(JObject envelope)
        {
            var clone = (JObject)envelope.DeepClone();
            clone.Remove("crc");
            return Crc32.ToLowerHex(
                Crc32.Compute(s_utf8.GetBytes(CanonicalJson.Serialize(clone))));
        }

        // Canonical-strict parse: keeps ts strings as strings (Newtonsoft's
        // JObject.Parse would coerce them to Date tokens the Canonical JSON
        // writer rejects).
        private static JObject ParseObject(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "fixture JSON did not parse");
            return (JObject)parsed.Value!.ToToken();
        }

        // Rewrites journal.ndjson line-for-line; the last element gets no
        // trailing newline when complete is false (a half-written tail).
        private void WriteJournal(string runId, params string[] lines)
        {
            File.WriteAllText(
                JournalPath(runId), string.Join("\n", lines) + "\n", s_utf8);
        }

        private void WriteJournalRaw(string runId, string content)
        {
            File.WriteAllText(JournalPath(runId), content, s_utf8);
        }

        // Rewrites the RunAccepted line with a different planFileDigest so
        // the digest check passes while a chosen later check fails.
        private void SetRunAcceptedDigest(string runId, string digest)
        {
            var lines = JournalLines(runId);
            var env = ParseObject(lines[1]);
            ((JObject)env["data"]!)["planFileDigest"] = digest;
            env["crc"] = LineCrc(env);
            lines[1] = CanonicalJson.Serialize(env);
            WriteJournal(runId, lines);
        }

        private string PlanFileDigest(string runId) =>
            Sha256LowerHex(File.ReadAllBytes(PlanPath(runId)));

        private static void AssertNeedsReview(Result<RunSnapshot> result)
        {
            Assert.IsTrue(result.Ok, "Load yields a snapshot, not a hard failure");
            Assert.AreEqual(RunState.NeedsReview, result.Value!.State);
            Assert.IsTrue(
                result.Diagnostics.Any(d =>
                    d.Code == "OW-STORE-002" && d.Severity == Severity.Error),
                "NeedsReview must carry an OW-STORE-002 error diagnostic");
        }

        private static void AssertDroppedLastLine(Result<RunSnapshot> result)
        {
            Assert.IsTrue(result.Ok);
            Assert.AreNotEqual(RunState.NeedsReview, result.Value!.State);
            Assert.IsTrue(
                result.Diagnostics.Any(d =>
                    d.Code == "OW-STORE-002" && d.Severity == Severity.Warning),
                "a dropped last line must carry an OW-STORE-002 warning");
            Assert.IsFalse(
                result.Diagnostics.Any(d =>
                    d.Code == "OW-STORE-002" && d.Severity == Severity.Error),
                "a droppable corruption must not be an error");
        }

        private static void AssertSameSnapshot(RunSnapshot first, RunSnapshot second)
        {
            Assert.AreEqual(first.State, second.State, "state");
            Assert.AreEqual(first.Plan?.PlanHash, second.Plan?.PlanHash, "planHash");
            Assert.AreEqual(first.Steps.Count, second.Steps.Count, "step count");
            for (var i = 0; i < first.Steps.Count; i++)
            {
                Assert.AreEqual(first.Steps[i], second.Steps[i], "step " + i);
            }
            Assert.AreEqual(
                first.Outputs?.CanonicalText, second.Outputs?.CanonicalText, "outputs");
            Assert.AreEqual(
                first.OpenResources.Count, second.OpenResources.Count, "open resources");
            Assert.AreEqual(first.CancelPending, second.CancelPending, "cancelPending");
            Assert.AreEqual(
                first.RecoveryDecisionRequired, second.RecoveryDecisionRequired,
                "recovery decision flag");
            Assert.AreEqual(first.LastSeq, second.LastSeq, "lastSeq");
            Assert.AreEqual(first.Session, second.Session, "session");
        }

        private static string StateOf(string root, string runId)
        {
            var state = (JObject)JsonValue.Parse(
                File.ReadAllText(Path.Combine(root, "Runs", runId, "state.json"), s_utf8))
                .Value!.ToToken();
            return (string)state["state"]!;
        }

        private static IReadOnlyList<RunIndexEntry> Listed(
            FileRunStore store, RunQuery query)
        {
            var result = store.List(query);
            Assert.IsTrue(result.Ok, "List failed: " + string.Join(
                ";", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return result.Value!;
        }

        // The fail-closed List of SPEC §F1: an OW-STORE-001 error, no
        // partial list and index.json left byte-for-byte untouched.
        private static void AssertListFailsClosed(
            Result<IReadOnlyList<RunIndexEntry>> result,
            string indexPath, byte[] indexBefore)
        {
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(
                result.Diagnostics.Any(d =>
                    d.Code == "OW-STORE-001" && d.Severity == Severity.Error),
                "a failed List reports OW-STORE-001");
            CollectionAssert.AreEqual(
                indexBefore, File.ReadAllBytes(indexPath),
                "index.json is untouched");
        }

        // ── SPEC §F1 test: state.json is a regenerable cache ───────────────

        [Test]
        public void DeletingStateJsonDoesNotChangeTheLoadedSnapshot()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{\"reason\":\"start\"}"),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);
            var first = store.Load(runId);
            Assert.IsTrue(first.Ok);

            File.Delete(Path.Combine(RunDir(runId), "state.json"));
            var second = store.Load(runId);

            Assert.IsTrue(second.Ok);
            Assert.AreEqual(0, second.Diagnostics.Count);
            AssertSameSnapshot(first.Value!, second.Value!);
            Assert.IsTrue(
                File.Exists(Path.Combine(RunDir(runId), "state.json")),
                "the cache is regenerated from the journal");
            Assert.AreEqual("running", StateOf(_root, runId));
        }

        [Test]
        public void CorruptStateJsonIsRegeneratedFromTheJournal()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            File.WriteAllText(
                Path.Combine(RunDir(runId), "state.json"), "{not json", s_utf8);

            var result = store.Load(runId);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Diagnostics.Count);
            Assert.AreEqual(RunState.Queued, result.Value!.State);
            Assert.AreEqual("queued", StateOf(_root, runId));
        }

        [Test]
        public void UndecodableStateJsonIsRegeneratedFromTheJournal()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            File.WriteAllBytes(
                Path.Combine(RunDir(runId), "state.json"),
                new byte[] { 0x80, 0x80, 0x80 });

            var result = store.Load(runId);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Diagnostics.Count);
            Assert.AreEqual(RunState.Queued, result.Value!.State);
            Assert.AreEqual("queued", StateOf(_root, runId));
        }

        // ── SPEC §F2 test 1 (load level): every record type folds ──────────

        [Test]
        public void JournalWithEveryRecordTypeFoldsIntoTheSnapshot()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var seq = 2L;
            void A(JournalRecordType type, string data, int? step = null, int? attempt = null) =>
                Assert.IsTrue(
                    store.Append(runId, Record(seq++, type, data, step, attempt),
                        Durability.Flushed).Ok);

            A(JournalRecordType.RunStarted, "{\"reason\":\"start\"}");
            A(JournalRecordType.StepStarted, "{\"snapshot\":{}}", step: 0, attempt: 1);
            A(JournalRecordType.StepWaiting,
                "{\"resumeToken\":{\"ticket\":7},\"waitingFor\":\"poll\",\"deadline\":null}",
                step: 0);
            A(JournalRecordType.StepProgress, "{\"message\":\"half\",\"percent\":50}", step: 0);
            A(JournalRecordType.StepSucceeded,
                "{\"outputs\":{\"result\":1},\"changes\":[],\"port\":\"next\",\"reconciled\":false}",
                step: 0);
            A(JournalRecordType.StepStarted, "{\"snapshot\":{}}", step: 1, attempt: 1);
            A(JournalRecordType.StepFailed, "{\"error\":\"OW-RUN-006\"}", step: 1);
            A(JournalRecordType.StepSkipped, "{\"reason\":\"unreached\"}", step: 2);
            A(JournalRecordType.StepStarted, "{\"snapshot\":{}}", step: 3, attempt: 1);
            A(JournalRecordType.StepCancelled, "{\"reason\":\"cancel\"}", step: 3);
            A(JournalRecordType.StepStarted, "{\"snapshot\":{}}", step: 4, attempt: 1);
            A(JournalRecordType.StepNeedsReview, "{\"reason\":\"unclear\"}", step: 4);
            A(JournalRecordType.ReviewResolved, "{\"decision\":\"retry_step\"}");
            A(JournalRecordType.StepStarted, "{\"snapshot\":{}}", step: 4, attempt: 2);
            A(JournalRecordType.StepSucceeded,
                "{\"outputs\":{\"result\":2},\"changes\":[],\"port\":\"next\",\"reconciled\":false}",
                step: 4);
            A(JournalRecordType.ResourceAcquired,
                "{\"kind\":\"prefab_edit\",\"data\":{\"path\":\"Assets/A.prefab\"}}");
            A(JournalRecordType.Log, "{\"level\":\"info\",\"message\":\"note\"}");
            A(JournalRecordType.ResourceReleased,
                "{\"kind\":\"prefab_edit\",\"data\":{\"path\":\"Assets/A.prefab\"}}");
            A(JournalRecordType.CancelRequested, "{\"reason\":\"user\"}");
            A(JournalRecordType.RunSuspended, "{\"reason\":\"play_mode\"}");
            A(JournalRecordType.RunResumed, "{\"reason\":\"edit_mode\"}");
            A(JournalRecordType.RunFinished,
                "{\"state\":\"cancelled\",\"outputs\":{},\"summary\":\"aborted\"}");

            var result = store.Load(runId);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Diagnostics.Count);
            var snapshot = result.Value!;
            Assert.AreEqual(RunState.Cancelled, snapshot.State);
            Assert.AreEqual(5, snapshot.Steps.Count);
            Assert.AreEqual(StepState.Succeeded, snapshot.Steps[0].State);
            Assert.AreEqual(1, snapshot.Steps[0].Attempt);
            Assert.AreEqual(StepState.Failed, snapshot.Steps[1].State);
            Assert.AreEqual(StepState.Skipped, snapshot.Steps[2].State);
            Assert.AreEqual(StepState.Cancelled, snapshot.Steps[3].State);
            Assert.AreEqual(StepState.Succeeded, snapshot.Steps[4].State);
            Assert.AreEqual(2, snapshot.Steps[4].Attempt);
            Assert.AreEqual(0, snapshot.OpenResources.Count);
            Assert.IsFalse(snapshot.CancelPending);
            Assert.AreEqual(seq - 1, snapshot.LastSeq);
            Assert.AreEqual(Session, snapshot.Session);
            Assert.AreEqual(Plan().PlanHash, snapshot.Plan!.PlanHash);
        }

        // ── SPEC §F2 test 3: unknown required:false is skipped ──────────────

        [Test]
        public void UnknownOptionalRecordIsSkippedAndStateMatches()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            File.AppendAllText(
                JournalPath(runId),
                Envelope(3, "future_marker", required: false, dataJson: "{\"note\":1}") + "\n",
                s_utf8);
            Assert.IsTrue(store.Append(runId,
                Record(4, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);

            var withMarker = store.Load(runId).Value!;

            var clean = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(clean, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(clean,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(clean,
                Record(3, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);
            var baseline = store.Load(clean).Value!;

            Assert.AreEqual(baseline.State, withMarker.State);
            Assert.AreEqual(baseline.Steps, withMarker.Steps);
            Assert.AreEqual(4, withMarker.LastSeq,
                "the skipped line still consumes a sequence number");
            Assert.AreEqual(0, store.Load(runId).Diagnostics.Count);
        }

        [Test]
        public void TrailingSkippedRecordStillAdvancesLastSeq()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            File.AppendAllText(
                JournalPath(runId),
                Envelope(3, "future_marker", required: false, dataJson: "{\"note\":1}") + "\n",
                s_utf8);

            var snapshot = store.Load(runId).Value!;

            Assert.AreEqual(RunState.Running, snapshot.State);
            Assert.AreEqual(3, snapshot.LastSeq,
                "a skipped last line still spends its sequence position");

            // The next-append contract: appending at LastSeq + 1 must not
            // collide with the skipped envelope on the next load.
            Assert.IsTrue(store.Append(runId,
                Record(snapshot.LastSeq + 1, JournalRecordType.StepStarted,
                    "{\"snapshot\":{}}", step: 0, attempt: 1),
                Durability.Flushed).Ok);

            var reloaded = store.Load(runId);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(0, reloaded.Diagnostics.Count);
            Assert.AreEqual(RunState.Running, reloaded.Value!.State);
            Assert.AreEqual(StepState.Running, reloaded.Value!.Steps[0].State);
            Assert.AreEqual(4, reloaded.Value!.LastSeq);
        }

        [Test]
        public void OptionalRecordBeforeRunAcceptedIsSkipped()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            WriteJournal(runId,
                JournalCodec.WriteHeader(new JournalHeader(runId, Plan().PlanHash)),
                Envelope(1, "future_marker", required: false, dataJson: "{}"),
                Envelope(2, "run_accepted", required: true,
                    dataJson: "{\"planFileDigest\":\"" + PlanFileDigest(runId) + "\"}"));

            var result = store.Load(runId);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(0, result.Diagnostics.Count);
            Assert.AreEqual(RunState.Queued, result.Value!.State);
        }

        // ── SPEC §F2 test 4: unknown required:true is never skipped ────────

        [Test]
        public void UnknownRequiredRecordIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            File.AppendAllText(
                JournalPath(runId),
                Envelope(3, "future_state_change", required: true, dataJson: "{}") + "\n" +
                JournalCodec.WriteRecord(
                    Record(4, JournalRecordType.StepStarted, "{}", step: 0, attempt: 1)) + "\n",
                s_utf8);

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void UnknownRequiredRecordIsNeedsReviewEvenAsLastLine()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            File.AppendAllText(
                JournalPath(runId),
                Envelope(3, "future_state_change", required: true, dataJson: "{}") + "\n",
                s_utf8);

            var result = store.Load(runId);

            AssertNeedsReview(result);
            Assert.IsFalse(
                result.Diagnostics.Any(d => d.Severity == Severity.Warning),
                "a NeedsReview line is never dropped");
        }

        // ── SPEC §F2 test 5: a missing required flag is corruption ─────────

        [Test]
        public void MiddleLineWithoutRequiredFlagIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            File.AppendAllText(
                JournalPath(runId),
                Envelope(2, "log", required: null, dataJson: "{\"level\":\"info\"}") + "\n" +
                JournalCodec.WriteRecord(
                    Record(3, JournalRecordType.RunStarted, "{}")) + "\n",
                s_utf8);

            AssertNeedsReview(store.Load(runId));
        }

        // ── SPEC §F2 tests 6-7: unknown versions are NeedsReview ───────────

        [Test]
        public void UnknownHeaderFormatVersionIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var lines = JournalLines(runId);
            var header = ParseObject(lines[0]);
            header["formatVersion"] = 99;
            lines[0] = CanonicalJson.Serialize(header);
            WriteJournal(runId, lines);

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void MissingOrUnknownHeaderCanonicalJsonVersionIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var lines = JournalLines(runId);
            var missing = ParseObject(lines[0]);
            missing.Remove("canonicalJsonVersion");
            lines[0] = CanonicalJson.Serialize(missing);
            WriteJournal(runId, lines);
            AssertNeedsReview(store.Load(runId));

            var unknown = ParseObject(lines[0]);
            unknown["canonicalJsonVersion"] = 99;
            lines[0] = CanonicalJson.Serialize(unknown);
            WriteJournal(runId, lines);
            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void UnknownPlanCanonicalJsonVersionIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var plan = ParseObject(File.ReadAllText(PlanPath(runId), s_utf8));
            plan["canonicalJsonVersion"] = 99;
            File.WriteAllText(
                PlanPath(runId), CanonicalJson.Serialize(plan), s_utf8);
            // Reforge the digest so the version check — not the digest — is
            // what classifies the Run (SPEC §D7 test 6).
            SetRunAcceptedDigest(runId, PlanFileDigest(runId));

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void PlanFormatVersionUnknownIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var plan = ParseObject(File.ReadAllText(PlanPath(runId), s_utf8));
            plan["formatVersion"] = 2;
            File.WriteAllText(
                PlanPath(runId), CanonicalJson.Serialize(plan), s_utf8);
            SetRunAcceptedDigest(runId, PlanFileDigest(runId));

            AssertNeedsReview(store.Load(runId));
        }

        // ── SPEC §F2 test 8, §D0.1 item 6, §D7 tests 4/6, AT-05 ────────────

        [Test]
        public void ReloadedPlanKeepsTheSamePlanHash()
        {
            var runId = Identifiers.NewRunId();
            var plan = Plan();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, plan, Doc()).Ok);

            var result = store.Load(runId);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(plan.PlanHash, result.Value!.Plan!.PlanHash);
        }

        [Test]
        public void OneBytePlanChangeIsOwStore002NeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var bytes = File.ReadAllBytes(PlanPath(runId));
            bytes[bytes.Length / 2] ^= 0x01;
            File.WriteAllBytes(PlanPath(runId), bytes);

            var result = store.Load(runId);

            AssertNeedsReview(result);
            Assert.AreNotEqual(
                RunState.Succeeded, result.Value!.State,
                "tampering never produces an automatic success");
        }

        [Test]
        public void MissingPlanFileDigestIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            WriteJournal(runId,
                JournalCodec.WriteHeader(new JournalHeader(runId, Plan().PlanHash)),
                Envelope(1, "run_accepted", required: true,
                    dataJson: "{\"actor\":\"agent\",\"approvals\":[]}"));

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void MissingRunAcceptedIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            WriteJournal(runId,
                JournalCodec.WriteHeader(new JournalHeader(runId, Plan().PlanHash)),
                Envelope(1, "run_started", required: true, dataJson: "{}"));

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void HeaderPlanHashMismatchIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var lines = JournalLines(runId);
            var header = ParseObject(lines[0]);
            header["planHash"] = Hash64('f');
            lines[0] = CanonicalJson.Serialize(header);
            WriteJournal(runId, lines);

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void HeaderRunIdMismatchIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var lines = JournalLines(runId);
            var header = ParseObject(lines[0]);
            header["runId"] = Identifiers.NewRunId();
            lines[0] = CanonicalJson.Serialize(header);
            WriteJournal(runId, lines);

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void MissingPlanFileIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            File.Delete(PlanPath(runId));

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void MissingJournalIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            File.Delete(JournalPath(runId));

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void CorruptHeaderLineIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            WriteJournalRaw(runId, "{not json\n");

            AssertNeedsReview(store.Load(runId));
        }

        // ── SPEC §F3 tests: the last-line drop ──────────────────────────────

        [Test]
        public void TruncatedLastLineIsDroppedWithWarning()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            var content = File.ReadAllText(JournalPath(runId), s_utf8);
            WriteJournalRaw(runId, content + "{\"seq\":3,\"ts\":\"");

            var result = store.Load(runId);

            AssertDroppedLastLine(result);
            Assert.AreEqual(RunState.Running, result.Value!.State);
        }

        [Test]
        public void BrokenStepSucceededLeavingStepStartedForcesF5Decision()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);
            var content = File.ReadAllText(JournalPath(runId), s_utf8);
            // A half-written StepSucceeded tail: the drop leaves StepStarted
            // only, so an incomplete side effect is possible (SPEC §F3 test 2).
            WriteJournalRaw(
                runId, content + JournalCodec.WriteRecord(
                    Record(4, JournalRecordType.StepSucceeded,
                        "{\"outputs\":{},\"changes\":[],\"port\":\"next\",\"reconciled\":false}",
                        step: 0)).Substring(0, 40));

            var result = store.Load(runId);

            AssertDroppedLastLine(result);
            var snapshot = result.Value!;
            Assert.AreEqual(RunState.Running, snapshot.State);
            Assert.AreEqual(StepState.Running, snapshot.Steps[0].State);
            Assert.IsTrue(
                snapshot.RecoveryDecisionRequired,
                "StepStarted only must force the F5 recovery decision");
            Assert.AreNotEqual(RunState.Succeeded, snapshot.State,
                "never an automatic success");
        }

        [Test]
        public void MiddleCrcMismatchIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            var corrupt = Envelope(2, "step_started", required: true,
                dataJson: "{\"snapshot\":{}}", step: 0).Replace("\"crc\":\"", "\"crc\":\"0");
            Assert.IsTrue(store.Append(runId,
                Record(4, JournalRecordType.StepSucceeded, "{\"outputs\":{}}", step: 0),
                Durability.Flushed).Ok);
            var lines = JournalLines(runId);
            lines[2] = corrupt;
            WriteJournal(runId, lines);

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void SeqGapInTheMiddleIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            WriteJournal(runId,
                JournalLines(runId)[0],
                Envelope(1, "run_accepted", required: true,
                    dataJson: "{\"planFileDigest\":\"" + PlanFileDigest(runId) + "\"}"),
                JournalCodec.WriteRecord(Record(4, JournalRecordType.RunStarted, "{}")),
                JournalCodec.WriteRecord(
                    Record(5, JournalRecordType.StepStarted, "{}", step: 0, attempt: 1)));

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void SeqDuplicateIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            WriteJournal(runId,
                JournalLines(runId)[0],
                Envelope(1, "run_accepted", required: true,
                    dataJson: "{\"planFileDigest\":\"" + PlanFileDigest(runId) + "\"}"),
                JournalCodec.WriteRecord(Record(1, JournalRecordType.RunStarted, "{}")),
                JournalCodec.WriteRecord(Record(2, JournalRecordType.Log, "{}")));

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void SeqGapAsLastLineIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            // A complete, checksum-valid record whose seq skips 3: corruption
            // at a real position, not a torn write (SPEC §F3 item 3).
            File.AppendAllText(
                JournalPath(runId),
                JournalCodec.WriteRecord(
                    Record(4, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                        step: 0, attempt: 1)) + "\n",
                s_utf8);

            var result = store.Load(runId);

            AssertNeedsReview(result);
            Assert.IsFalse(
                result.Diagnostics.Any(d => d.Severity == Severity.Warning),
                "a complete last record with a seq gap is never dropped");
        }

        [Test]
        public void SeqDuplicateAsLastLineIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.RunFinished,
                    "{\"state\":\"succeeded\",\"outputs\":{},\"summary\":\"done\"}"),
                Durability.Flushed).Ok);
            // A complete record repeating seq 3 as the last line: dropping it
            // would keep the recorded Succeeded (SPEC §F3 item 3).
            File.AppendAllText(
                JournalPath(runId),
                JournalCodec.WriteRecord(
                    Record(3, JournalRecordType.Log, "{\"level\":\"info\"}")) + "\n",
                s_utf8);

            var result = store.Load(runId);

            AssertNeedsReview(result);
            Assert.AreNotEqual(RunState.Succeeded, result.Value!.State,
                "a duplicate-seq last record never keeps a terminal state");
        }

        // ── SPEC §F2 item 2 / §F3 item 3: a broken envelope as last line ────

        // A missing `required` member is record corruption routed through
        // §F3 item 3 (SPEC §F2 item 2): as the last line it is the
        // interrupted-write tail — dropped with an OW-STORE-002 warning —
        // and the open StepStarted forces the F5 recovery decision. The next
        // append truncates the tail through the same predicate, so the
        // reload is clean.
        [Test]
        public void LastLineWithoutRequiredFlagIsDroppedAndTruncated()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);
            // The tail of an interrupted write: a record whose required flag
            // never reached the line (SPEC §F2 item 2).
            File.AppendAllText(
                JournalPath(runId),
                Envelope(4, "step_succeeded", required: null,
                    dataJson: "{\"outputs\":{}}", step: 0),
                s_utf8);

            var dropped = store.Load(runId);

            AssertDroppedLastLine(dropped);
            Assert.AreEqual(RunState.Running, dropped.Value!.State);
            Assert.IsTrue(
                dropped.Value!.RecoveryDecisionRequired,
                "the open StepStarted forces the F5 recovery decision");
            Assert.AreEqual(3, dropped.Value!.LastSeq);

            Assert.IsTrue(store.Append(runId,
                Record(dropped.Value!.LastSeq + 1, JournalRecordType.StepSucceeded,
                    "{\"outputs\":{\"r\":1},\"changes\":[],\"port\":\"next\",\"reconciled\":false}",
                    step: 0),
                Durability.Flushed).Ok);

            var reloaded = store.Load(runId);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(0, reloaded.Diagnostics.Count,
                "the broken tail was truncated, not left as middle corruption");
            Assert.AreEqual(StepState.Succeeded, reloaded.Value!.Steps[0].State);
            Assert.AreEqual(4, reloaded.Value!.LastSeq);
        }

        // The same §F2 item 2 corruption with the flag present but not a
        // boolean: the line is still a broken last line, not a well-formed
        // record — dropped, flagged for F5 and truncated on the next append.
        [Test]
        public void LastLineWithNonBooleanRequiredIsDroppedAndTruncated()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);
            File.AppendAllText(
                JournalPath(runId),
                Envelope(4, "step_succeeded", required: "yes",
                    dataJson: "{\"outputs\":{}}", step: 0),
                s_utf8);

            var dropped = store.Load(runId);

            AssertDroppedLastLine(dropped);
            Assert.AreEqual(RunState.Running, dropped.Value!.State);
            Assert.IsTrue(
                dropped.Value!.RecoveryDecisionRequired,
                "the open StepStarted forces the F5 recovery decision");
            Assert.AreEqual(3, dropped.Value!.LastSeq);

            Assert.IsTrue(store.Append(runId,
                Record(dropped.Value!.LastSeq + 1, JournalRecordType.StepSucceeded,
                    "{\"outputs\":{\"r\":1},\"changes\":[],\"port\":\"next\",\"reconciled\":false}",
                    step: 0),
                Durability.Flushed).Ok);

            var reloaded = store.Load(runId);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(0, reloaded.Diagnostics.Count,
                "the broken tail was truncated, not left as middle corruption");
            Assert.AreEqual(StepState.Succeeded, reloaded.Value!.Steps[0].State);
            Assert.AreEqual(4, reloaded.Value!.LastSeq);
        }

        // The same interrupted-write tail with its terminating '\n' already
        // written: the line is complete on disk but still the broken record
        // Load drops, so the next append must truncate it — terminator
        // included — instead of keeping it and turning it into middle-line
        // corruption on the reload.
        [Test]
        public void TerminatedLastLineWithoutRequiredFlagIsDroppedAndTruncated()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);
            File.AppendAllText(
                JournalPath(runId),
                Envelope(4, "step_succeeded", required: null,
                    dataJson: "{\"outputs\":{}}", step: 0) + "\n",
                s_utf8);

            var dropped = store.Load(runId);

            AssertDroppedLastLine(dropped);
            Assert.AreEqual(RunState.Running, dropped.Value!.State);
            Assert.AreEqual(3, dropped.Value!.LastSeq);

            Assert.IsTrue(store.Append(runId,
                Record(dropped.Value!.LastSeq + 1, JournalRecordType.StepSucceeded,
                    "{\"outputs\":{\"r\":1},\"changes\":[],\"port\":\"next\",\"reconciled\":false}",
                    step: 0),
                Durability.Flushed).Ok);

            var lines = JournalLines(runId);
            Assert.AreEqual(5, lines.Length,
                "the terminated broken record is gone and the new record is the last line");
            var read = JournalCodec.ReadRecord(lines[4], expectedSeq: 4);
            Assert.AreEqual(JournalLineStatus.Decoded, read.Status);
            Assert.AreEqual(JournalRecordType.StepSucceeded, read.Record!.Type);

            var reloaded = store.Load(runId);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(0, reloaded.Diagnostics.Count,
                "the reload is clean — no middle-line corruption");
            Assert.AreEqual(StepState.Succeeded, reloaded.Value!.Steps[0].State);
            Assert.AreEqual(4, reloaded.Value!.LastSeq);
        }

        // And the same newline-terminated broken tail with the flag present
        // but not a boolean.
        [Test]
        public void TerminatedLastLineWithNonBooleanRequiredIsDroppedAndTruncated()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);
            File.AppendAllText(
                JournalPath(runId),
                Envelope(4, "step_succeeded", required: "yes",
                    dataJson: "{\"outputs\":{}}", step: 0) + "\n",
                s_utf8);

            var dropped = store.Load(runId);

            AssertDroppedLastLine(dropped);
            Assert.AreEqual(RunState.Running, dropped.Value!.State);
            Assert.AreEqual(3, dropped.Value!.LastSeq);

            Assert.IsTrue(store.Append(runId,
                Record(dropped.Value!.LastSeq + 1, JournalRecordType.StepSucceeded,
                    "{\"outputs\":{\"r\":1},\"changes\":[],\"port\":\"next\",\"reconciled\":false}",
                    step: 0),
                Durability.Flushed).Ok);

            var lines = JournalLines(runId);
            Assert.AreEqual(5, lines.Length,
                "the terminated broken record is gone and the new record is the last line");
            var read = JournalCodec.ReadRecord(lines[4], expectedSeq: 4);
            Assert.AreEqual(JournalLineStatus.Decoded, read.Status);
            Assert.AreEqual(JournalRecordType.StepSucceeded, read.Record!.Type);

            var reloaded = store.Load(runId);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(0, reloaded.Diagnostics.Count,
                "the reload is clean — no middle-line corruption");
            Assert.AreEqual(StepState.Succeeded, reloaded.Value!.Steps[0].State);
            Assert.AreEqual(4, reloaded.Value!.LastSeq);
        }

        // A crc-mismatching last line is a complete envelope failing
        // integrity — corruption at a real position, not the
        // interrupted-write tail: NeedsReview, never dropped (SPEC §F3
        // item 3 reserves the drop for the broken line; a readable record
        // keeps the middle-line outcome).
        [Test]
        public void LastLineCrcMismatchIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            var bad = Envelope(3, "run_finished", required: true,
                dataJson: "{\"state\":\"succeeded\",\"outputs\":{},\"summary\":\"done\"}")
                .Replace("\"crc\":\"", "\"crc\":\"0");
            File.AppendAllText(JournalPath(runId), bad + "\n", s_utf8);

            var result = store.Load(runId);

            AssertNeedsReview(result);
            Assert.IsFalse(
                result.Diagnostics.Any(d => d.Severity == Severity.Warning),
                "a complete last record with a wrong crc is never dropped");
            Assert.AreNotEqual(RunState.Succeeded, result.Value!.State,
                "dropping it would keep the recorded Succeeded");
        }

        // SPEC §F3 item 3: a torn tail Load dropped must not glue the next
        // record onto its leftover bytes — the recovery append at LastSeq + 1
        // discards the partial line to a record boundary first, so the reload
        // is clean.
        [Test]
        public void AppendAfterATornLastLineDiscardsThePartialTail()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            var content = File.ReadAllText(JournalPath(runId), s_utf8);
            WriteJournalRaw(runId, content + "{\"seq\":3,\"ts\":\"");

            var dropped = store.Load(runId);
            AssertDroppedLastLine(dropped);
            Assert.AreEqual(2, dropped.Value!.LastSeq);

            Assert.IsTrue(store.Append(runId,
                Record(dropped.Value!.LastSeq + 1, JournalRecordType.StepStarted,
                    "{\"snapshot\":{}}", step: 0, attempt: 1),
                Durability.Flushed).Ok);

            var lines = JournalLines(runId);
            Assert.AreEqual(4, lines.Length,
                "the torn tail is gone and the new record is the last line");
            var read = JournalCodec.ReadRecord(lines[3], expectedSeq: 3);
            Assert.AreEqual(JournalLineStatus.Decoded, read.Status);
            Assert.AreEqual(JournalRecordType.StepStarted, read.Record!.Type);

            var reloaded = store.Load(runId);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(0, reloaded.Diagnostics.Count,
                "the reload is clean — no leftover partial line");
            Assert.AreEqual(RunState.Running, reloaded.Value!.State);
            Assert.AreEqual(StepState.Running, reloaded.Value!.Steps[0].State);
            Assert.AreEqual(3, reloaded.Value!.LastSeq);
        }

        // SPEC §F3 item 3: a complete record that lost only its terminating
        // '\n' is not a droppable tail — the next append must never truncate
        // it; it re-terminates the line instead.
        [Test]
        public void AppendAfterACompleteUnterminatedRecordNeverTruncatesIt()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            var unterminated = JournalCodec.WriteRecord(
                Record(3, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1));
            File.AppendAllText(JournalPath(runId), unterminated, s_utf8);
            Assert.AreEqual(3, store.Load(runId).Value!.LastSeq,
                "the complete unterminated record still decodes");

            Assert.IsTrue(store.Append(runId,
                Record(4, JournalRecordType.StepSucceeded,
                    "{\"outputs\":{\"r\":1},\"changes\":[],\"port\":\"next\",\"reconciled\":false}",
                    step: 0),
                Durability.Flushed).Ok);

            var lines = JournalLines(runId);
            Assert.AreEqual(5, lines.Length);
            Assert.AreEqual(unterminated, lines[3],
                "the complete record's bytes are preserved, only terminated");

            var reloaded = store.Load(runId);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(0, reloaded.Diagnostics.Count);
            Assert.AreEqual(StepState.Succeeded, reloaded.Value!.Steps[0].State);
            Assert.AreEqual(4, reloaded.Value!.LastSeq);
        }

        // SPEC §F1/§F3: the journal write is authoritative — a
        // state.json/index.json maintenance failure after the record is
        // committed is a non-fatal warning, the caches are regenerated on the
        // next Load, and no retry duplicates the seq.
        [Test]
        public void CacheMaintenanceFailureAfterACommittedAppendIsOnlyAWarning()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);

            // The state.json replace step throws a non-transient failure
            // after the journal record is already written and fsync'd.
            store.ReplaceStepProbe = (temp, path, targetExists) =>
                new IOException("injected", unchecked((int)0x80070002));

            var appended = store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed);

            Assert.IsTrue(appended.Ok,
                "the record is in the journal; the cache failure is non-fatal");
            Assert.IsTrue(
                appended.Diagnostics.Any(d =>
                    d.Code == "OW-STORE-001" && d.Severity == Severity.Warning),
                "a cache-maintenance failure is a warning diagnostic");
            Assert.AreEqual(3, JournalLines(runId).Length,
                "the committed record is in the journal");
            Assert.AreEqual("queued", StateOf(_root, runId),
                "the stale cache still shows the pre-append state");

            // A retry with the next seq works — nothing re-appends seq 2.
            store.ReplaceStepProbe = null;
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);

            var reloaded = store.Load(runId);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(0, reloaded.Diagnostics.Count);
            Assert.AreEqual(RunState.Running, reloaded.Value!.State);
            Assert.AreEqual(4, JournalLines(runId).Length,
                "exactly one copy of each appended record");
            Assert.AreEqual("running", StateOf(_root, runId),
                "the cache is regenerated from the journal on load");
        }

        // ── Fold content ────────────────────────────────────────────────────

        [Test]
        public void SnapshotCarriesOutputsResumeTokenResourcesAndCancel()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{}", step: 0, attempt: 1),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(4, JournalRecordType.StepSucceeded,
                    "{\"outputs\":{\"mat\":\"Assets/M.mat\"}}", step: 0),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(5, JournalRecordType.StepStarted, "{}", step: 1, attempt: 1),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(6, JournalRecordType.StepWaiting,
                    "{\"resumeToken\":{\"poll\":3}}", step: 1),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(7, JournalRecordType.ResourceAcquired,
                    "{\"kind\":\"prefab_edit\",\"data\":{\"path\":\"Assets/A.prefab\"}}"),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(8, JournalRecordType.CancelRequested, "{\"reason\":\"user\"}"),
                Durability.Flushed).Ok);

            var snapshot = store.Load(runId).Value!;

            Assert.AreEqual(RunState.Running, snapshot.State);
            Assert.AreEqual(
                "{\"mat\":\"Assets/M.mat\"}",
                snapshot.Steps[0].Outputs!.CanonicalText);
            Assert.AreEqual("{\"poll\":3}", snapshot.Steps[1].ResumeToken!.CanonicalText);
            Assert.AreEqual(StepState.Waiting, snapshot.Steps[1].State);
            Assert.AreEqual(1, snapshot.OpenResources.Count);
            Assert.AreEqual("prefab_edit", snapshot.OpenResources[0].Kind);
            Assert.AreEqual(
                "{\"path\":\"Assets/A.prefab\"}",
                snapshot.OpenResources[0].Data!.CanonicalText);
            Assert.IsTrue(snapshot.CancelPending);
            Assert.IsTrue(snapshot.RecoveryDecisionRequired);
            Assert.AreEqual(8, snapshot.LastSeq);
            Assert.IsNotNull(snapshot.CreatedAt);
            Assert.AreEqual(Ts, snapshot.UpdatedAt);
        }

        // SPEC §F5 grants a step's Completed verdict to "the same step's Run
        // resource", so the §F2 fold rebuilds every open acquisition with the
        // step its own record carried. A record that carried no step folds as an
        // acquisition no step owns — kept, because the pairing rule and the
        // recovery decision still have to see it, but never claimable.
        [Test]
        public void OpenResourcesCarryTheStepOfTheirAcquisitionRecord()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.ResourceAcquired,
                    "{\"kind\":\"prefab_edit\",\"data\":{\"path\":\"Assets/A.prefab\"}}",
                    step: 4),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(4, JournalRecordType.ResourceAcquired,
                    "{\"kind\":\"prefab_edit\",\"data\":{\"path\":\"Assets/B.prefab\"}}"),
                Durability.Flushed).Ok);

            var snapshot = store.Load(runId).Value!;

            Assert.AreEqual(2, snapshot.OpenResources.Count);
            Assert.AreEqual("Assets/A.prefab",
                ((JObject)snapshot.OpenResources[0].Data!.ToToken())["path"]!.ToString());
            Assert.AreEqual((int?)4, snapshot.OpenResources[0].AcquiringStep,
                "the rebuilt acquisition names the step that opened it");
            Assert.IsNull(snapshot.OpenResources[1].AcquiringStep,
                "a record without a step rebuilds as an acquisition no step owns");
        }

        [Test]
        public void UnmatchedResourceReleaseIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.ResourceReleased,
                    "{\"kind\":\"prefab_edit\",\"data\":{\"path\":\"Assets/A.prefab\"}}"),
                Durability.Flushed).Ok);

            AssertNeedsReview(store.Load(runId));
        }

        [Test]
        public void AwaitingApprovalBecomesQueuedWhenAllApprovalsAreGranted()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(approvalCount: 2), Doc()).Ok);

            var first = store.Load(runId).Value!;
            Assert.AreEqual(RunState.AwaitingApproval, first.State);

            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.ApprovalGranted, "{\"approver\":\"h\"}"),
                Durability.Flushed).Ok);
            Assert.AreEqual(RunState.AwaitingApproval, store.Load(runId).Value!.State);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.ApprovalGranted, "{\"approver\":\"h2\"}"),
                Durability.Flushed).Ok);
            Assert.AreEqual(RunState.Queued, store.Load(runId).Value!.State);

            Assert.IsTrue(store.Append(runId,
                Record(4, JournalRecordType.ApprovalRejected, "{\"approver\":\"h\"}"),
                Durability.Flushed).Ok);
            Assert.AreEqual(RunState.Cancelled, store.Load(runId).Value!.State);
        }

        [Test]
        public void ReviewResolvedAbortIsCancelledAndRetryIsRunning()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{}", step: 0, attempt: 1),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(4, JournalRecordType.StepNeedsReview, "{}", step: 0),
                Durability.Flushed).Ok);
            Assert.AreEqual(RunState.NeedsReview, store.Load(runId).Value!.State);

            Assert.IsTrue(store.Append(runId,
                Record(5, JournalRecordType.ReviewResolved, "{\"decision\":\"abort\"}"),
                Durability.Flushed).Ok);
            Assert.AreEqual(RunState.Cancelled, store.Load(runId).Value!.State);
        }

        [Test]
        public void StepRecordWithoutStepIndexIsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{}"),
                Durability.Flushed).Ok);

            AssertNeedsReview(store.Load(runId));
        }

        // ── index.json / state.json maintenance, List, Delete ───────────────

        [Test]
        public void AppendMaintainsStateJsonAndIndexOnStateChanges()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.StepStarted, "{}", step: 0, attempt: 1),
                Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(4, JournalRecordType.StepNeedsReview, "{}", step: 0),
                Durability.Flushed).Ok);

            Assert.AreEqual("needs_review", StateOf(_root, runId));
            var index = (JObject)JsonValue.Parse(
                File.ReadAllText(Path.Combine(_root, "index.json"), s_utf8))
                .Value!.ToToken();
            var entry = (JObject)((JArray)index["runs"]!)[0]!;
            Assert.AreEqual("needs_review", (string?)entry["state"]);
            Assert.AreEqual("example.runstore", (string?)entry["workflowId"]);

            // A record that changes no Run state leaves the caches as they are.
            var updatedAt = (string)entry["updatedAt"]!;
            Assert.IsTrue(store.Append(runId,
                Record(5, JournalRecordType.Log, "{\"level\":\"info\",\"message\":\"m\"}"),
                Durability.Flushed).Ok);
            var indexAfter = (JObject)JsonValue.Parse(
                File.ReadAllText(Path.Combine(_root, "index.json"), s_utf8))
                .Value!.ToToken();
            Assert.AreEqual(
                updatedAt,
                (string?)((JObject)((JArray)indexAfter["runs"]!)[0]!)["updatedAt"]);
        }

        [Test]
        public void ListReturnsIndexEntriesAndFiltersByStates()
        {
            var store = Store();
            var queued = Identifiers.NewRunId();
            var awaiting = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(queued, Plan(), Doc()).Ok);
            Assert.IsTrue(store.CreateRun(awaiting, Plan(approvalCount: 1), Doc()).Ok);

            var all = Listed(store, new RunQuery());
            Assert.AreEqual(2, all.Count);
            Assert.AreEqual(RunState.Queued, all[0].State);
            Assert.AreEqual(RunState.AwaitingApproval, all[1].State);
            Assert.AreEqual("example.runstore", all[0].WorkflowId);

            var filtered = Listed(
                store, new RunQuery(new[] { RunState.AwaitingApproval }));
            Assert.AreEqual(1, filtered.Count);
            Assert.AreEqual(awaiting, filtered[0].RunId);
            Assert.AreEqual(
                0, Listed(store, new RunQuery(new[] { RunState.Succeeded })).Count);
        }

        [Test]
        public void MissingIndexIsRebuiltFromTheJournals()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            File.Delete(indexPath);

            var entries = Listed(store, new RunQuery());

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(runId, entries[0].RunId);
            Assert.AreEqual(RunState.Running, entries[0].State);
            Assert.AreEqual("example.runstore", entries[0].WorkflowId);
            Assert.IsTrue(File.Exists(indexPath), "the index is regenerated");
        }

        [Test]
        public void CorruptIndexIsRebuiltFromTheJournals()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            File.WriteAllText(Path.Combine(_root, "index.json"), "garbage", s_utf8);

            var entries = Listed(store, new RunQuery());

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(runId, entries[0].RunId);
        }

        [Test]
        public void UndecodableIndexIsRebuiltFromTheJournals()
        {
            var store = Store();
            var first = Identifiers.NewRunId();
            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(first, Plan(), Doc()).Ok);
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            File.WriteAllBytes(indexPath, new byte[] { 0x80, 0x80, 0x80 });

            var entries = Listed(store, new RunQuery());

            Assert.AreEqual(2, entries.Count);
            CollectionAssert.AreEquivalent(
                new[] { first, second }, entries.Select(e => e.RunId).ToArray());
            Assert.IsTrue(File.Exists(indexPath), "the index is regenerated");
        }

        // The SPEC §F3 crash window: CreateRun fsyncs the journal before it
        // updates index.json, so a crash between them leaves a readable,
        // valid index that omits the persisted Run. List must reconcile
        // membership against the Run directories — not trust the cache —
        // or the §F4 recovery scan would never discover the Run.
        [Test]
        public void StaleIndexMissingARunIsRebuiltFromTheJournals()
        {
            var store = Store();
            var first = Identifiers.NewRunId();
            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(first, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            var staleIndex = File.ReadAllText(indexPath, s_utf8);
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            File.WriteAllText(indexPath, staleIndex, s_utf8);

            var entries = Listed(store, new RunQuery());

            Assert.AreEqual(2, entries.Count);
            CollectionAssert.AreEquivalent(
                new[] { first, second }, entries.Select(e => e.RunId).ToArray());
            var index = ParseObject(File.ReadAllText(indexPath, s_utf8));
            Assert.AreEqual(2, ((JArray)index["runs"]!).Count,
                "the index is repaired to cover the persisted Run");
        }

        // The mirror image: a readable index entry whose Run directory is
        // gone is stale membership too and is rebuilt away, never listed.
        [Test]
        public void StaleIndexForARemovedRunIsRebuiltFromTheJournals()
        {
            var store = Store();
            var first = Identifiers.NewRunId();
            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(first, Plan(), Doc()).Ok);
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            Directory.Delete(RunDir(second), recursive: true);

            var entries = Listed(store, new RunQuery());

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(first, entries[0].RunId);
            var index = ParseObject(File.ReadAllText(indexPath, s_utf8));
            var runs = (JArray)index["runs"]!;
            Assert.AreEqual(1, runs.Count, "the index is repaired");
            Assert.AreEqual(first, (string?)runs[0]!["runId"]);
        }

        // List must never silently return a partial list: when the Runs
        // path cannot be enumerated — a non-directory occupies it — List
        // fails closed with OW-STORE-001 instead of pretending the store
        // holds no Runs, and the index cache is left untouched.
        [Test]
        public void ListFailsClosedWhenTheRunsPathIsNotADirectory()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            var indexBefore = File.ReadAllBytes(indexPath);
            var runsDir = Path.Combine(_root, "Runs");
            Directory.Delete(runsDir, recursive: true);
            File.WriteAllText(runsDir, "occupied", s_utf8);

            var result = store.List(new RunQuery());

            AssertListFailsClosed(result, indexPath, indexBefore);
        }

        // The Runs directory exists but cannot be inspected — the
        // ACL-free simulation of the access/IO failure
        // Directory.Exists would swallow into "empty store": List fails
        // closed with OW-STORE-001 and the index cache is left
        // untouched.
        [Test]
        public void ListFailsClosedWhenTheRunsDirectoryCannotBeEnumerated()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            var indexBefore = File.ReadAllBytes(indexPath);
            var runsDir = Path.Combine(_root, "Runs");
            store.PersistedScanProbe = path =>
                string.Equals(path, runsDir, StringComparison.Ordinal)
                    ? (Exception?)new UnauthorizedAccessException("denied")
                    : null;

            var result = store.List(new RunQuery());

            AssertListFailsClosed(result, indexPath, indexBefore);
        }

        // A persisted Run whose journal metadata cannot be inspected is
        // an OW-STORE-001 failure — not a Run "without a journal"
        // silently omitted from the rebuilt index and the list.
        [Test]
        public void ListFailsClosedWhenARunJournalCannotBeInspected()
        {
            var store = Store();
            var first = Identifiers.NewRunId();
            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(first, Plan(), Doc()).Ok);
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            var indexBefore = File.ReadAllBytes(indexPath);
            var journal = JournalPath(second);
            store.PersistedScanProbe = path =>
                string.Equals(path, journal, StringComparison.Ordinal)
                    ? (Exception?)new IOException("denied")
                    : null;

            var result = store.List(new RunQuery());

            AssertListFailsClosed(result, indexPath, indexBefore);
        }

        // A valid run_<ULID> directory without a journal is a damaged Run,
        // not a half-persisted create: it stays in the scan and List reports
        // the NeedsReview state Load classifies it as — the Run is never
        // silently dropped, and the rebuilt index records it the same way.
        [Test]
        public void RunDirectoryWithoutAJournalIsListedAsNeedsReview()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var orphan = Identifiers.NewRunId();
            Directory.CreateDirectory(RunDir(orphan));

            var entries = Listed(store, new RunQuery());

            Assert.AreEqual(2, entries.Count);
            var damaged = entries.Single(e => e.RunId == orphan);
            Assert.AreEqual(RunState.NeedsReview, damaged.State);
            var index = ParseObject(
                File.ReadAllText(Path.Combine(_root, "index.json"), s_utf8));
            var indexed = ((JArray)index["runs"]!).Cast<JObject>()
                .Single(r => (string?)r["runId"] == orphan);
            Assert.AreEqual(
                "needs_review", (string?)indexed["state"],
                "the rebuilt index records the damaged Run as NeedsReview");
        }

        // An indexed Run whose journal is then deleted is a damaged
        // existing Run, not an incomplete creation: List keeps reporting it
        // with the NeedsReview classification Load gives a missing journal
        // instead of dropping it from the list and the rebuilt index.
        [Test]
        public void IndexedRunWhoseJournalIsDeletedStaysListedAsNeedsReview()
        {
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(Store().CreateRun(runId, Plan(), Doc()).Ok);
            File.Delete(JournalPath(runId));

            var entries = Listed(Store(), new RunQuery());

            var entry = entries.Single(e => e.RunId == runId);
            Assert.AreEqual(RunState.NeedsReview, entry.State);
            var index = ParseObject(
                File.ReadAllText(Path.Combine(_root, "index.json"), s_utf8));
            var indexed = ((JArray)index["runs"]!).Cast<JObject>()
                .Single(r => (string?)r["runId"] == runId);
            Assert.AreEqual(
                "needs_review", (string?)indexed["state"],
                "the rebuilt index records the damaged Run as NeedsReview");
        }

        // The journal records the truth a stale index cannot: a RunStarted
        // commits but the index update is interrupted, so the cache still
        // reads queued. List must derive the state from the journal — a
        // Running filter still finds the Run recovery needs — and the index
        // is repaired as a cache, never trusted for the answer.
        [Test]
        public void StaleIndexStateDoesNotHideARunFromAStateFilteredList()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);

            // The journal write is committed and fsync'd; the state cache
            // maintenance after it fails, so index.json keeps the
            // pre-append state.
            store.ReplaceStepProbe = (temp, path, targetExists) =>
                new IOException("injected", unchecked((int)0x80070002));
            var appended = store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed);
            Assert.IsTrue(appended.Ok);
            store.ReplaceStepProbe = null;

            var running = Listed(store, new RunQuery(new[] { RunState.Running }));

            var entry = running.Single();
            Assert.AreEqual(runId, entry.RunId);
            Assert.AreEqual(RunState.Running, entry.State);
            var index = ParseObject(
                File.ReadAllText(Path.Combine(_root, "index.json"), s_utf8));
            var indexed = ((JArray)index["runs"]!).Cast<JObject>()
                .Single(r => (string?)r["runId"] == runId);
            Assert.AreEqual(
                "running", (string?)indexed["state"],
                "the index cache is repaired to the journal state");
        }

        [Test]
        public void LoadOnMissingIndexRebuildsTheCompleteIndex()
        {
            var store = Store();
            var first = Identifiers.NewRunId();
            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(first, Plan(), Doc()).Ok);
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(second,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            File.Delete(indexPath);

            // The Load must not publish a partial index holding only the Run
            // it loaded — List would accept it and recovery would never see
            // the unfinished second Run.
            Assert.IsTrue(store.Load(first).Ok);
            var index = ParseObject(File.ReadAllText(indexPath, s_utf8));
            var runs = (JArray)index["runs"]!;
            Assert.AreEqual(2, runs.Count);
            CollectionAssert.AreEquivalent(
                new[] { first, second },
                runs.Select(r => (string?)r["runId"]).ToArray());

            var entries = Listed(store, new RunQuery());
            Assert.AreEqual(2, entries.Count);
            CollectionAssert.AreEquivalent(
                new[] { first, second }, entries.Select(e => e.RunId).ToArray());
            Assert.AreEqual(
                RunState.Running, entries.Single(e => e.RunId == second).State);
        }

        [Test]
        public void LoadOnUnreadableIndexRebuildsTheCompleteIndex()
        {
            var store = Store();
            var first = Identifiers.NewRunId();
            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(first, Plan(), Doc()).Ok);
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            File.WriteAllText(indexPath, "garbage", s_utf8);

            Assert.IsTrue(store.Load(first).Ok);

            var index = ParseObject(File.ReadAllText(indexPath, s_utf8));
            Assert.AreEqual(2, ((JArray)index["runs"]!).Count);
            var entries = Listed(store, new RunQuery());
            Assert.AreEqual(2, entries.Count);
        }

        [Test]
        public void LoadOnUndecodableIndexRebuildsTheCompleteIndex()
        {
            var store = Store();
            var first = Identifiers.NewRunId();
            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(first, Plan(), Doc()).Ok);
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            File.WriteAllBytes(indexPath, new byte[] { 0x80, 0x80, 0x80 });

            Assert.IsTrue(store.Load(first).Ok);

            var index = ParseObject(File.ReadAllText(indexPath, s_utf8));
            Assert.AreEqual(2, ((JArray)index["runs"]!).Count);
            var entries = Listed(store, new RunQuery());
            Assert.AreEqual(2, entries.Count);
        }

        // A syntactically valid index.json whose entries are malformed is a
        // corrupt cache: the UpdateIndex field parsing behind a Load's cache
        // refresh must never throw on it, and the whole index is rebuilt
        // from the journals (SPEC §F1).
        [Test]
        public void IndexEntryWithObjectRunIdRebuildsTheIndex()
        {
            AssertMalformedIndexEntryIsRebuilt("{\"runs\":[{\"runId\":{}}]}");
        }

        [Test]
        public void IndexEntryWithArrayStateRebuildsTheIndex()
        {
            AssertMalformedIndexEntryIsRebuilt(
                "{\"runs\":[{\"runId\":\"x\",\"workflowId\":\"w\",\"state\":[]," +
                "\"createdAt\":\"t\",\"updatedAt\":\"t\"}]}");
        }

        [Test]
        public void IndexEntryMissingCreatedAtRebuildsTheIndex()
        {
            AssertMalformedIndexEntryIsRebuilt(
                "{\"runs\":[{\"runId\":\"x\",\"workflowId\":\"w\"," +
                "\"state\":\"running\",\"updatedAt\":\"t\"}]}");
        }

        private void AssertMalformedIndexEntryIsRebuilt(string indexJson)
        {
            var store = Store();
            var first = Identifiers.NewRunId();
            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(first, Plan(), Doc()).Ok);
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            var indexPath = Path.Combine(_root, "index.json");
            File.WriteAllText(indexPath, indexJson, s_utf8);

            Assert.IsTrue(store.Load(first).Ok);

            var index = ParseObject(File.ReadAllText(indexPath, s_utf8));
            Assert.AreEqual(2, ((JArray)index["runs"]!).Count,
                "the malformed entry rebuilt the complete index from the journals");
            var entries = Listed(store, new RunQuery());
            Assert.AreEqual(2, entries.Count);
            CollectionAssert.AreEquivalent(
                new[] { first, second }, entries.Select(e => e.RunId).ToArray());
        }

        // SPEC §F8 forbids deleting unfinished and NeedsReview Runs even
        // now that the SG-16 tombstone provider of T-W4.4-02 landed: the
        // delete is refused as OW-STORE-001 with the Run untouched. The
        // terminal-state deletion itself — tombstone commit, residue
        // sweep, OW-STORE-003 verdicts — is covered by
        // FileRunStoreTombstoneTests.
        [Test]
        public void DeleteFailsClosedOnARunningRun()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);

            AssertDeleteFailsClosed(store, runId, RunState.Running);
        }

        [Test]
        public void DeleteFailsClosedOnANeedsReviewRun()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var bytes = File.ReadAllBytes(PlanPath(runId));
            bytes[bytes.Length / 2] ^= 0x01;
            File.WriteAllBytes(PlanPath(runId), bytes);
            AssertNeedsReview(store.Load(runId));

            AssertDeleteFailsClosed(store, runId, RunState.NeedsReview);
        }

        // The fail-closed Delete on a non-deletable Run: an OW-STORE-001
        // refusal with the Run
        // directory, journal and index entry untouched — Load and List keep
        // working on the unchanged store.
        private void AssertDeleteFailsClosed(
            FileRunStore store, string runId, RunState expectedState)
        {
            var indexPath = Path.Combine(_root, "index.json");
            var journalBefore = File.ReadAllBytes(JournalPath(runId));
            var indexBefore = File.ReadAllBytes(indexPath);

            var delete = store.Delete(runId);

            Assert.IsFalse(delete.Ok, "Delete fails closed on a non-deletable Run");
            Assert.IsTrue(
                delete.Diagnostics.Any(d =>
                    d.Code == "OW-STORE-001" && d.Severity == Severity.Error),
                "the refused Delete reports OW-STORE-001");
            Assert.IsTrue(Directory.Exists(RunDir(runId)), "the Run directory stays");
            CollectionAssert.AreEqual(
                journalBefore, File.ReadAllBytes(JournalPath(runId)),
                "the journal is untouched");
            CollectionAssert.AreEqual(
                indexBefore, File.ReadAllBytes(indexPath), "index.json is untouched");

            var load = store.Load(runId);
            Assert.IsTrue(load.Ok);
            Assert.AreEqual(expectedState, load.Value!.State);
            Assert.IsTrue(
                Listed(store, new RunQuery()).Any(e => e.RunId == runId),
                "the Run is still listed");
        }

        [Test]
        public void UnknownRunIdsAreOwRun010()
        {
            var store = Store();
            var load = store.Load(Identifiers.NewRunId());
            Assert.IsFalse(load.Ok);
            Assert.AreEqual("OW-RUN-010", load.Diagnostics[0].Code);
            var delete = store.Delete(Identifiers.NewRunId());
            Assert.IsFalse(delete.Ok);
            Assert.AreEqual("OW-RUN-010", delete.Diagnostics[0].Code);
        }

        [Test]
        public void BufferedLinesAreFlushedToDiskBeforeLoad()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.Log, "{\"level\":\"info\",\"message\":\"m\"}"),
                Durability.Buffered).Ok);
            Assert.AreEqual(2, JournalLines(runId).Length);

            var result = store.Load(runId);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual(3, JournalLines(runId).Length,
                "the pending Buffered line reached the journal before the read");
            Assert.AreEqual(2, result.Value!.LastSeq);
        }
    }
}
