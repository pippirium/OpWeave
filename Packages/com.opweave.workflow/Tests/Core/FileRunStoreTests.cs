#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
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
    /// [Core] tests for the FileRunStore layout and durable write path of SPEC
    /// §F1/§F3/§D7/§D0.1 (local:T-W1.7-02:runstore-write): the per-Run file
    /// layout, plan.json canonical bytes and the RunAccepted
    /// <c>planFileDigest</c>, the header→RunAccepted ordering, Flushed vs
    /// Buffered journal durability, the temp-file+atomic-replace writes of
    /// state.json/index.json/changes.json and OW-STORE-001 on injected I/O
    /// failures. The same sources run under dotnet and Unity Mono.
    /// </summary>
    internal sealed class FileRunStoreTests
    {
        private const string Session = "session-store-test";
        private const string Ts = "2026-11-06T00:00:00.000Z";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunStore." + Guid.NewGuid().ToString("N"));
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

        // A self-contained but fully verifiable Plan: the index rebuild a
        // missing index.json triggers Loads the Run it just wrote, so the
        // SG-12 twelve-member planHashInput carries the shapes
        // PlanJsonCodec.Read validates (resolvedWorkflow id/inputs/outputs,
        // the six-member environmentIdentifiers, requiredEffects as the
        // steps+approvals object) and planHash is its real SHA-256 — the
        // §D0.1 revalidation decodes it.
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

        private static JObject EnvelopeData(string line)
        {
            var parsed = JsonValue.Parse(line);
            Assert.IsTrue(parsed.Ok, "journal line did not parse");
            return (JObject)((JObject)parsed.Value!.ToToken())["data"]!;
        }

        private static void AssertOwStore001(Result<Unit> result)
        {
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual("OW-STORE-001", result.Diagnostics[0].Code);
            Assert.AreEqual(Severity.Error, result.Diagnostics[0].Severity);
        }

        // Observing wrapper for the journal-stream seam: forwards every call
        // to the real FileStream while recording the ordered
        // write / flush(argument) / dispose sequence the production path
        // issues on it, so a test sees the durable flush's flushToDisk
        // argument and stream position. The production calls are never
        // replaced — only observed as they pass through.
        private sealed class RecordingJournalStream : FileStream
        {
            private readonly FileStream _inner;
            private readonly List<string> _events;

            internal RecordingJournalStream(FileStream inner, List<string> events)
                : base(inner.SafeFileHandle, FileAccess.Write)
            {
                _inner = inner;
                _events = events;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _events.Add("write:" + count);
                _inner.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _events.Add("write:" + buffer.Length);
                _inner.Write(buffer);
            }

            public override void Flush()
            {
                _events.Add("flush");
                _inner.Flush();
            }

            public override void Flush(bool flushToDisk)
            {
                _events.Add(
                    "flush:" + (flushToDisk ? "true" : "false") + "@" + _inner.Position);
                _inner.Flush(flushToDisk);
            }

            protected override void Dispose(bool disposing)
            {
                _events.Add("dispose");
                if (disposing)
                {
                    _inner.Dispose();
                }
            }
        }

        // The call sequence one durable journal append must produce on the
        // stream: the record bytes written, then the fsync — always
        // Flush(flushToDisk: true) — at a position past
        // minimumFlushPosition, then the stream closed.
        private static void AssertDurableFlushObserved(
            List<string> events, long minimumFlushPosition)
        {
            Assert.AreEqual(3, events.Count, "one write, one fsync, one dispose");
            Assert.IsTrue(
                events[0].StartsWith("write:", StringComparison.Ordinal),
                "the record bytes must be written before the durable flush");
            Assert.IsTrue(
                events[1].StartsWith("flush:true@", StringComparison.Ordinal),
                "the journal must be fsync'd — Flush(flushToDisk: true)");
            Assert.Greater(
                long.Parse(events[1].Substring("flush:true@".Length)),
                minimumFlushPosition,
                "the record bytes must reach the stream before the durable flush");
            Assert.AreEqual("dispose", events[2]);
        }

        // ── Layout and content ─────────────────────────────────────────────

        // SPEC §F1: a created Run holds plan.json, workflow.json,
        // journal.ndjson, state.json, the changes.json slot and an index entry.
        [Test]
        public void CreatedRunContainsTheF1Layout()
        {
            var runId = Identifiers.NewRunId();
            var created = Store().CreateRun(runId, Plan(), Doc());
            Assert.IsTrue(created.Ok, "CreateRun failed.");

            var runDir = RunDir(runId);
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "plan.json")), "plan.json");
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "workflow.json")), "workflow.json");
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "journal.ndjson")), "journal.ndjson");
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "state.json")), "state.json");
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "changes.json")), "changes.json slot");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "index.json")), "index.json");
        }

        // SPEC §D7 item 6 / §F2: plan.json is the Canonical JSON byte sequence
        // and its SHA-256 equals the RunAccepted planFileDigest — never a
        // member of plan.json itself.
        [Test]
        public void PlanJsonBytesAreCanonicalAndDigestMatchesRunAccepted()
        {
            var runId = Identifiers.NewRunId();
            var plan = Plan();
            Assert.IsTrue(Store().CreateRun(runId, plan, Doc()).Ok);

            var planBytes = File.ReadAllBytes(Path.Combine(RunDir(runId), "plan.json"));
            Assert.AreEqual(
                s_utf8.GetBytes(PlanJsonCodec.Write(plan)), planBytes,
                "plan.json must hold the canonical byte sequence");
            Assert.IsFalse(
                File.ReadAllText(Path.Combine(RunDir(runId), "plan.json"), s_utf8)
                    .Contains("planFileDigest"),
                "plan.json must not carry its own digest");

            var digest = Sha256LowerHex(planBytes);
            var data = EnvelopeData(JournalLines(runId)[1]);
            Assert.AreEqual(digest, (string?)data["planFileDigest"],
                "RunAccepted.planFileDigest must be the SHA-256 of the written bytes");
        }

        // SPEC §F2: line 1 is the JournalHeader, the first record line is a
        // Flushed run_accepted with seq 1, required:true and the session.
        [Test]
        public void JournalStartsWithHeaderThenFlushedRunAccepted()
        {
            var runId = Identifiers.NewRunId();
            var plan = Plan();
            Assert.IsTrue(Store().CreateRun(runId, plan, Doc()).Ok);

            var lines = JournalLines(runId);
            Assert.AreEqual(2, lines.Length);

            var header = JournalCodec.ReadHeader(lines[0]);
            Assert.AreEqual(JournalLineStatus.Decoded, header.Status);
            Assert.AreEqual(runId, header.Header!.RunId);
            Assert.AreEqual(plan.PlanHash, header.Header.PlanHash);

            var record = JournalCodec.ReadRecord(lines[1], expectedSeq: 1);
            Assert.AreEqual(JournalLineStatus.Decoded, record.Status);
            Assert.AreEqual(JournalRecordType.RunAccepted, record.Record!.Type);
            Assert.AreEqual(1, record.Record.Seq);
            Assert.IsTrue(record.Record.Required);
            Assert.AreEqual(Durability.Flushed, record.Record.Durability);
            Assert.AreEqual(Session, record.Record.Session);
        }

        // The workflow.json audit copy is the document's serialization.
        [Test]
        public void WorkflowJsonIsTheDocumentCopy()
        {
            var runId = Identifiers.NewRunId();
            var doc = Doc();
            Assert.IsTrue(Store().CreateRun(runId, Plan(), doc).Ok);

            var text = File.ReadAllText(
                Path.Combine(RunDir(runId), "workflow.json"), s_utf8);
            Assert.AreEqual(WorkflowSerializer.Write(doc), text);
        }

        // SPEC §F2 table: the RunAccepted data carries the actor when the
        // caller supplies it, and the Plan's approval requirements.
        [Test]
        public void RunAcceptedRecordsActorAndApprovals()
        {
            var runId = Identifiers.NewRunId();
            var plan = Plan(approvalCount: 2);
            Assert.IsTrue(
                Store().CreateRun(runId, plan, Doc(), ActorKind.Agent).Ok);

            var data = EnvelopeData(JournalLines(runId)[1]);
            Assert.AreEqual("agent", (string?)data["actor"]);
            var approvals = (JArray)data["approvals"]!;
            Assert.AreEqual(2, approvals.Count);
            Assert.AreEqual("node0", (string?)approvals[0]!["nodeId"]);
            Assert.AreEqual("asset.modify", (string?)approvals[0]!["effect"]);
            Assert.AreEqual(64, ((string?)data["planFileDigest"])!.Length);
        }

        // SPEC §E2 item 1: approvals → AwaitingApproval, otherwise Queued —
        // recorded in index.json and the state.json cache.
        [Test]
        public void InitialStateFollowsTheApprovalRule()
        {
            var store = Store();
            var queued = Identifiers.NewRunId();
            var awaiting = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(queued, Plan(), Doc()).Ok);
            var second = store.CreateRun(awaiting, Plan(approvalCount: 1), Doc());
            Assert.IsTrue(second.Ok, second.Ok ? "" : second.Diagnostics[0].Message);

            var stateOf = new Func<string, string>(id =>
            {
                var state = (JObject)JsonValue.Parse(
                    File.ReadAllText(Path.Combine(RunDir(id), "state.json"), s_utf8))
                    .Value!.ToToken();
                return (string)state["state"]!;
            });
            Assert.AreEqual("queued", stateOf(queued));
            Assert.AreEqual("awaiting_approval", stateOf(awaiting));

            var index = (JObject)JsonValue.Parse(
                File.ReadAllText(Path.Combine(_root, "index.json"), s_utf8))
                .Value!.ToToken();
            var runs = (JArray)index["runs"]!;
            Assert.AreEqual(2, runs.Count);
            var first = (JObject)runs[0]!;
            Assert.AreEqual(queued, (string?)first["runId"]);
            Assert.AreEqual("example.runstore", (string?)first["workflowId"]);
            Assert.AreEqual("queued", (string?)first["state"]);
            Assert.IsNotNull((string?)first["createdAt"]);
            Assert.IsNotNull((string?)first["updatedAt"]);
            Assert.AreEqual("awaiting_approval", (string?)((JObject)runs[1]!)["state"]);
        }

        // SPEC §F3 item 2: state.json/index.json go through the temp file;
        // nothing temporary remains afterwards.
        [Test]
        public void AtomicReplaceLeavesNoTempFiles()
        {
            Assert.IsTrue(Store().CreateRun(Identifiers.NewRunId(), Plan(), Doc()).Ok);
            Assert.AreEqual(
                0, Directory.GetFiles(_root, "*" + ".opweave-tmp", SearchOption.AllDirectories).Length);
        }

        // The replace step tolerates one transient Win32 failure — an indexer
        // or scanner briefly holding the file — injected through the internal
        // probe seam; the write still lands with no temp file left behind.
        [Test]
        public void AtomicReplaceRetriesATransientReplaceFailure()
        {
            var store = Store();
            var calls = 0;
            store.ReplaceStepProbe = (temp, path, targetExists) =>
            {
                calls++;
                return calls == 1
                    ? new IOException("injected transient", unchecked((int)0x80070497))
                    : null;
            };

            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.AreEqual(4, calls,
                "state.json fails once then succeeds, changes.json and index.json follow");
            Assert.IsTrue(File.Exists(Path.Combine(RunDir(runId), "state.json")));
            Assert.AreEqual(
                0, Directory.GetFiles(_root, "*" + ".opweave-tmp", SearchOption.AllDirectories).Length);
        }

        // The retry is bounded: a replace step that keeps failing transiently
        // still surfaces OW-STORE-001, and the orphaned temp file is removed.
        [Test]
        public void AtomicReplaceReportsOwStore001AfterTheRetryBound()
        {
            var store = Store();
            var calls = 0;
            store.ReplaceStepProbe = (temp, path, targetExists) =>
            {
                calls++;
                return new IOException("injected transient", unchecked((int)0x80070020));
            };

            AssertOwStore001(store.CreateRun(Identifiers.NewRunId(), Plan(), Doc()));
            Assert.AreEqual(5, calls, "bounded retries on the first replace step");
            Assert.AreEqual(
                0, Directory.GetFiles(_root, "*" + ".opweave-tmp", SearchOption.AllDirectories).Length);
        }

        // A failure code outside the transient sharing class is not retried:
        // the first failure is already OW-STORE-001.
        [Test]
        public void AtomicReplaceDoesNotRetryNonTransientFailures()
        {
            var store = Store();
            var calls = 0;
            store.ReplaceStepProbe = (temp, path, targetExists) =>
            {
                calls++;
                return new IOException("injected", unchecked((int)0x80070002));
            };

            AssertOwStore001(store.CreateRun(Identifiers.NewRunId(), Plan(), Doc()));
            Assert.AreEqual(1, calls);
        }

        // SPEC §F3 item 2: the atomic replace also covers an existing target —
        // a second write replaces the file with the complete new content and
        // leaves no temp file behind. state.json goes through the internal
        // cache-rewrite seam, index.json through a second CreateRun.
        [Test]
        public void AtomicReplaceOverAnExistingTargetWritesCompleteContent()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var statePath = Path.Combine(RunDir(runId), "state.json");
            var indexPath = Path.Combine(_root, "index.json");
            var replaceCalls = 0;
            store.ReplaceStepProbe = (temp, path, targetExists) =>
            {
                if (targetExists)
                {
                    replaceCalls++;
                }
                return null;
            };

            Assert.IsTrue(store.RewriteStateCache(runId, RunState.Running).Ok);
            var state = (JObject)JsonValue.Parse(
                File.ReadAllText(statePath, s_utf8)).Value!.ToToken();
            Assert.AreEqual(runId, (string?)state["runId"]);
            Assert.AreEqual("running", (string?)state["state"]);
            Assert.IsNotNull((string?)state["updatedAt"]);

            var second = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(second, Plan(), Doc()).Ok);
            var index = (JObject)JsonValue.Parse(
                File.ReadAllText(indexPath, s_utf8)).Value!.ToToken();
            var runs = (JArray)index["runs"]!;
            Assert.AreEqual(2, runs.Count);
            Assert.AreEqual(runId, (string?)((JObject)runs[0]!)["runId"]);
            Assert.AreEqual(second, (string?)((JObject)runs[1]!)["runId"]);
            Assert.AreEqual("example.runstore", (string?)((JObject)runs[1]!)["workflowId"]);
            Assert.AreEqual("queued", (string?)((JObject)runs[1]!)["state"]);
            Assert.IsNotNull((string?)((JObject)runs[1]!)["createdAt"]);
            Assert.IsNotNull((string?)((JObject)runs[1]!)["updatedAt"]);

            Assert.AreEqual(2, replaceCalls,
                "state.json and index.json each replaced an existing target once");
            Assert.AreEqual(
                0, Directory.GetFiles(_root, "*" + ".opweave-tmp", SearchOption.AllDirectories).Length);
        }

        // A replace step that fails permanently over an existing target is
        // OW-STORE-001 and leaves the previous target content byte-identical —
        // never truncated or partially written — with the temp file removed.
        [Test]
        public void FailedReplaceOverAnExistingTargetPreservesTheOldContent()
        {
            var store = Store();
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var statePath = Path.Combine(RunDir(runId), "state.json");
            var indexPath = Path.Combine(_root, "index.json");
            var stateBefore = File.ReadAllBytes(statePath);
            var indexBefore = File.ReadAllBytes(indexPath);

            // A non-transient code fails the replace permanently on the first
            // attempt — only when the step runs against an existing target.
            store.ReplaceStepProbe = (temp, path, targetExists) =>
                targetExists
                    ? new IOException("injected", unchecked((int)0x80070002))
                    : null;

            AssertOwStore001(store.RewriteStateCache(runId, RunState.Running));
            Assert.AreEqual(stateBefore, File.ReadAllBytes(statePath),
                "a failed replace must leave the previous state.json untouched");

            AssertOwStore001(store.CreateRun(Identifiers.NewRunId(), Plan(), Doc()));
            Assert.AreEqual(indexBefore, File.ReadAllBytes(indexPath),
                "a failed replace must leave the previous index.json untouched");

            Assert.AreEqual(
                0, Directory.GetFiles(_root, "*" + ".opweave-tmp", SearchOption.AllDirectories).Length);
        }

        // ── Durability ─────────────────────────────────────────────────────

        // SPEC §F3 item 1: a Flushed append writes the record and then runs
        // the durable flush — the fsync with flushToDisk: true — inside the
        // Append call. The observing seam records every call the production
        // path issues on the journal stream; it cannot replace them.
        [Test]
        public void FlushedAppendReachesDiskBeforeReturning()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var journalLength = new FileInfo(JournalPath(runId)).Length;

            var events = new List<string>();
            store.JournalStreamWrapper = file => new RecordingJournalStream(file, events);

            var record = Record(2, JournalRecordType.StepStarted,
                "{\"inputsDigest\":\"" + Hash64('f') + "\",\"snapshot\":{\"path\":\"Assets/M.mat\"}}",
                step: 0, attempt: 1);
            var appended = store.Append(runId, record, Durability.Flushed);
            events.Add("append-returned");

            Assert.IsTrue(appended.Ok);
            AssertDurableFlushObserved(
                events.GetRange(0, events.Count - 1), journalLength);
            Assert.AreEqual("append-returned", events[events.Count - 1],
                "the durable flush must complete inside the Append call");

            var lines = JournalLines(runId);
            Assert.AreEqual(3, lines.Length);
            var read = JournalCodec.ReadRecord(lines[2], expectedSeq: 2);
            Assert.AreEqual(JournalLineStatus.Decoded, read.Status);
            Assert.AreEqual(JournalRecordType.StepStarted, read.Record!.Type);
            Assert.AreEqual(0, read.Record.Step);
            Assert.AreEqual(1, read.Record.Attempt);
        }

        // SPEC §F3 item 1: a Buffered line is held back and written — ahead of
        // the new record — by the next Flushed append. The observation proves
        // the Buffered append touches the journal stream not at all.
        [Test]
        public void BufferedAppendIsWrittenAtTheNextFlushed()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var afterCreate = File.ReadAllBytes(JournalPath(runId));
            var events = new List<string>();
            store.JournalStreamWrapper = file => new RecordingJournalStream(file, events);

            var log = Record(2, JournalRecordType.Log,
                "{\"level\":\"info\",\"message\":\"material created\"}");
            Assert.IsTrue(store.Append(runId, log, Durability.Buffered).Ok);
            Assert.AreEqual(0, events.Count,
                "a Buffered append must not touch the journal stream at all");
            Assert.AreEqual(afterCreate, File.ReadAllBytes(JournalPath(runId)),
                "a Buffered record must not reach the file before the next flush");

            var step = Record(3, JournalRecordType.StepStarted,
                "{\"snapshot\":{}}", step: 0, attempt: 1);
            Assert.IsTrue(store.Append(runId, step, Durability.Flushed).Ok);
            AssertDurableFlushObserved(events, afterCreate.Length);

            var lines = JournalLines(runId);
            Assert.AreEqual(4, lines.Length, "pending Buffered line, then the Flushed line");
            Assert.AreEqual(JournalRecordType.Log,
                JournalCodec.ReadRecord(lines[2], expectedSeq: 2).Record!.Type);
            Assert.AreEqual(JournalRecordType.StepStarted,
                JournalCodec.ReadRecord(lines[3], expectedSeq: 3).Record!.Type);
        }

        // SPEC §F3 item 1: the explicit flush hook (beforeAssemblyReload /
        // quitting wiring) forces pending Buffered lines to disk — the
        // observation proves the fsync runs inside FlushPending, not earlier.
        [Test]
        public void FlushPendingWritesBufferedLines()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var journalLength = new FileInfo(JournalPath(runId)).Length;
            var events = new List<string>();
            store.JournalStreamWrapper = file => new RecordingJournalStream(file, events);

            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.Log, "{\"level\":\"info\",\"message\":\"m\"}"),
                Durability.Buffered).Ok);
            Assert.AreEqual(0, events.Count,
                "a Buffered append must not touch the journal stream at all");
            Assert.AreEqual(2, JournalLines(runId).Length);

            Assert.IsTrue(store.FlushPending().Ok);
            events.Add("flush-pending-returned");
            AssertDurableFlushObserved(
                events.GetRange(0, events.Count - 1), journalLength);
            Assert.AreEqual("flush-pending-returned", events[events.Count - 1],
                "the durable flush must complete inside FlushPending");
            var lines = JournalLines(runId);
            Assert.AreEqual(3, lines.Length);
            Assert.AreEqual(JournalRecordType.Log,
                JournalCodec.ReadRecord(lines[2], expectedSeq: 2).Record!.Type);
        }

        // Mutation guard for the seam itself: the recorded flush event
        // carries the exact flushToDisk argument the production path passed,
        // so a Flush(false) call or a missing flush can no longer satisfy
        // the observation — the previous replacing seam hid both mutations.
        [Test]
        public void DurableFlushIsObservedWithFlushToDiskTrue()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var events = new List<string>();
            store.JournalStreamWrapper = file => new RecordingJournalStream(file, events);

            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.StepStarted, "{\"snapshot\":{}}",
                    step: 0, attempt: 1),
                Durability.Flushed).Ok);

            var flushes = events.FindAll(
                e => e.StartsWith("flush", StringComparison.Ordinal));
            Assert.AreEqual(1, flushes.Count,
                "a Flushed append performs exactly one flush on the journal stream");
            Assert.IsTrue(
                flushes[0].StartsWith("flush:true@", StringComparison.Ordinal),
                "the journal flush must pass flushToDisk: true — a " +
                "Flush(false) mutation is recorded and fails here");
        }

        // The SPEC §F2 table class is a floor: a Flushed-table record is
        // fsync'd even when the call asks for Buffered, and a Buffered-table
        // record can be upgraded by the durability argument.
        [Test]
        public void DurabilityNeverDropsBelowTheF2TableClass()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);

            // Flushed-table record with Buffered argument → still on disk.
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.StepStarted, "{\"snapshot\":{}}", step: 0, attempt: 1),
                Durability.Buffered).Ok);
            Assert.AreEqual(3, JournalLines(runId).Length);

            // Buffered-table record upgraded by the argument → on disk now.
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.Log, "{\"level\":\"info\",\"message\":\"m\"}"),
                Durability.Flushed).Ok);
            var lines = JournalLines(runId);
            Assert.AreEqual(4, lines.Length);
            Assert.AreEqual(JournalRecordType.StepStarted,
                JournalCodec.ReadRecord(lines[2], expectedSeq: 2).Record!.Type);
            Assert.AreEqual(JournalRecordType.Log,
                JournalCodec.ReadRecord(lines[3], expectedSeq: 3).Record!.Type);
        }

        // ── Errors ─────────────────────────────────────────────────────────

        // SPEC §F1 error: an injected I/O failure is OW-STORE-001 — here the
        // injected root is unusable because it is an existing file.
        [Test]
        public void CreateRunOnAnUnusableRootReturnsOwStore001()
        {
            var fileRoot = Path.Combine(_root, "root-is-a-file");
            File.WriteAllText(fileRoot, "x");
            var store = new FileRunStore(fileRoot, Session);

            AssertOwStore001(store.CreateRun(Identifiers.NewRunId(), Plan(), Doc()));
        }

        // A second create for the same Run never overwrites the journal —
        // the persisted bytes stay the single truth.
        [Test]
        public void CreateRunOnAnExistingRunReturnsOwStore001()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var journal = File.ReadAllBytes(JournalPath(runId));

            AssertOwStore001(store.CreateRun(runId, Plan(), Doc()));
            Assert.AreEqual(journal, File.ReadAllBytes(JournalPath(runId)),
                "a failed second create must not touch the persisted journal");
        }

        // plan.json and the journal are immutable inputs to later writes:
        // CreateNew refuses to replace them.
        [Test]
        public void CreateRunNeverReplacesAnExistingPlan()
        {
            var runId = Identifiers.NewRunId();
            var runDir = RunDir(runId);
            Directory.CreateDirectory(runDir);
            var planPath = Path.Combine(runDir, "plan.json");
            File.WriteAllText(planPath, "seed");
            var store = Store();

            AssertOwStore001(store.CreateRun(runId, Plan(), Doc()));
            Assert.AreEqual("seed", File.ReadAllText(planPath));
        }

        // Append to a Run the store does not hold is a write failure.
        [Test]
        public void AppendToAnUnknownRunReturnsOwStore001()
        {
            var result = Store().Append(
                Identifiers.NewRunId(),
                Record(1, JournalRecordType.Log, "{\"level\":\"info\",\"message\":\"m\"}"),
                Durability.Flushed);
            AssertOwStore001(result);
        }

        // A Flushed append against a journal path that cannot be opened
        // (a directory occupies the slot) is OW-STORE-001.
        [Test]
        public void FlushedAppendToABlockedJournalReturnsOwStore001()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            var journal = JournalPath(runId);
            File.Move(journal, journal + ".moved");
            Directory.CreateDirectory(journal);

            AssertOwStore001(store.Append(runId,
                Record(2, JournalRecordType.StepStarted, "{\"snapshot\":{}}", step: 0, attempt: 1),
                Durability.Flushed));
        }

        // A Buffered append to a missing journal fails instead of silently
        // queueing lines that can never join a valid journal.
        [Test]
        public void BufferedAppendToAMissingJournalReturnsOwStore001()
        {
            var runId = Identifiers.NewRunId();
            Directory.CreateDirectory(RunDir(runId));
            AssertOwStore001(Store().Append(runId,
                Record(1, JournalRecordType.Log, "{\"level\":\"info\",\"message\":\"m\"}"),
                Durability.Buffered));
        }

        // runId is "run_" + ULID (SPEC §0.4): malformed ids — including path
        // traversal — are programming errors, not persisted paths.
        [Test]
        public void RunIdGrammarIsEnforced()
        {
            var store = Store();
            Assert.Throws<ArgumentException>(
                () => store.Append("not-a-run", Record(1, JournalRecordType.Log, "{}"),
                    Durability.Flushed));
            Assert.Throws<ArgumentException>(
                () => store.Append("run_../escape", Record(1, JournalRecordType.Log, "{}"),
                    Durability.Flushed));
            Assert.Throws<ArgumentException>(
                () => store.CreateRun("run_SHORT", Plan(), Doc()));
            Assert.Throws<ArgumentNullException>(
                () => store.CreateRun(null!, Plan(), Doc()));
        }

        // ── Journal content cross-checks ───────────────────────────────────

        // The journal the store writes decodes cleanly through the §F2 codec:
        // header plus the RunAccepted exactly as persisted.
        [Test]
        public void PersistedJournalDecodesThroughTheF2Codec()
        {
            var runId = Identifiers.NewRunId();
            var store = Store();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc(), ActorKind.Human).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{\"reason\":\"start\"}"),
                Durability.Flushed).Ok);

            var lines = JournalLines(runId);
            Assert.AreEqual(3, lines.Length);
            Assert.AreEqual(JournalLineStatus.Decoded, JournalCodec.ReadHeader(lines[0]).Status);
            var accepted = JournalCodec.ReadRecord(lines[1], expectedSeq: 1);
            Assert.AreEqual(JournalLineStatus.Decoded, accepted.Status);
            var started = JournalCodec.ReadRecord(lines[2], expectedSeq: 2);
            Assert.AreEqual(JournalLineStatus.Decoded, started.Status);
            Assert.AreEqual(JournalRecordType.RunStarted, started.Record!.Type);
            var data = (JObject)accepted.Record!.Data.ToToken();
            Assert.AreEqual("human", (string?)data["actor"]);
        }
    }
}
