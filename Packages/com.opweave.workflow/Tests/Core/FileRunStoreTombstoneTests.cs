#nullable enable

// T-W4.4-02 [Core] tests: local:T-W4.4-02:deleted-runid — SPEC §0.6
// SG-16 and §F8: the deleted-runs.ndjson tombstone committed
// (append + fsync, under the Run Store exclusion) before the Run
// directory and index entry are removed; the deleted-vs-never-created
// distinction (OW-STORE-003 vs OW-RUN-010) durable across restarts;
// interrupted deletions recovered by the sweep; corrupt tombstones
// failing closed as OW-STORE-002; unfinished and NeedsReview Runs
// never deleted; tombstoned ids never reused; repeated deletion
// idempotent. Runs in both the dotnet and the Unity Mono runtimes.

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
    internal sealed class FileRunStoreTombstoneTests
    {
        private const string Session = "session-tombstone-test";
        private const string Ts = "2026-11-06T00:00:00.000Z";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunStoreTombstone." + Guid.NewGuid().ToString("N"));
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

        // ── Fixtures (same shape as FileRunStoreLoadTests) ──────────────

        private FileRunStore Store() => new FileRunStore(_root, Session);

        private string TombstonePath() =>
            Path.Combine(_root, "deleted-runs.ndjson");

        private string RunDir(string runId) => Path.Combine(_root, "Runs", runId);

        private string JournalPath(string runId) =>
            Path.Combine(RunDir(runId), "journal.ndjson");

        private string PlanPath(string runId) =>
            Path.Combine(RunDir(runId), "plan.json");

        private string IndexPath() => Path.Combine(_root, "index.json");

        private static string Hash64(char c) => new string(c, 64);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        // A self-contained but fully verifiable Plan — the same fixture
        // FileRunStoreLoadTests builds, so the §D0.1 revalidation Load
        // performs decodes it.
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

        private static JournalRecord Record(
            long seq, JournalRecordType type, string dataJson,
            int? step = null, int? attempt = null) =>
            new JournalRecord(seq, Ts, Session, type, step, attempt, Json(dataJson));

        // A Run journalled to a terminal §E1 state — the only kind the
        // §F8/SG-16 delete may ever remove.
        private string FinishedRun(FileRunStore store, string state = "succeeded")
        {
            var runId = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(2, JournalRecordType.RunStarted, "{}"), Durability.Flushed).Ok);
            Assert.IsTrue(store.Append(runId,
                Record(3, JournalRecordType.RunFinished,
                    "{\"state\":\"" + state + "\",\"outputs\":{}}"),
                Durability.Flushed).Ok);
            return runId;
        }

        private static IReadOnlyList<RunIndexEntry> Listed(
            FileRunStore store, RunQuery query)
        {
            var result = store.List(query);
            Assert.IsTrue(result.Ok, "List failed: " + string.Join(
                ";", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return result.Value!;
        }

        private static void AssertCode(
            IReadOnlyList<Diagnostic> diagnostics, string code)
        {
            Assert.IsTrue(
                diagnostics.Any(d => d.Code == code && d.Severity == Severity.Error),
                "expected an " + code + " error diagnostic, got: " + string.Join(
                    ";", diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        private static void AssertOwStore003(Result<RunSnapshot> load)
        {
            Assert.IsFalse(load.Ok, "a tombstoned Run must not load");
            AssertCode(load.Diagnostics, "OW-STORE-003");
        }

        // Reads the tombstone file's committed lines.
        private string[] TombstoneLines() =>
            File.Exists(TombstonePath())
                ? File.ReadAllLines(TombstonePath(), s_utf8)
                : Array.Empty<string>();

        // Whether the index.json cache still carries the Run's entry —
        // the second destructive step the durable publish must precede.
        private bool IndexHasEntry(string runId)
        {
            if (!File.Exists(IndexPath()))
            {
                return false;
            }
            var index = (JObject)JsonValue.Parse(
                File.ReadAllText(IndexPath(), s_utf8)).Value!.ToToken();
            return ((JArray)index["runs"]!).Cast<JObject>()
                .Any(e => (string?)e["runId"] == runId);
        }

        // The SG-16 record contract on one tombstone line.
        private static JObject AssertTombstoneLine(string line, string runId)
        {
            var parsed = JsonValue.Parse(line);
            Assert.IsTrue(parsed.Ok, "tombstone line did not parse: " + line);
            var record = (JObject)parsed.Value!.ToToken();
            Assert.AreEqual(runId, (string?)record["runId"]);
            var deletedAt = (string?)record["deletedAt"];
            Assert.IsNotNull(deletedAt, "the tombstone carries deletedAt");
            Assert.IsTrue(
                DateTimeOffset.TryParseExact(
                    deletedAt, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _),
                "deletedAt is a UTC ISO-8601 millisecond timestamp");
            // The written line is Canonical JSON — the member order and
            // formatting the codec produces, byte-identical.
            Assert.AreEqual(
                DeletedRuns.Line(
                    runId, deletedAt!, (string?)record["reason"] ?? ""),
                line,
                "the tombstone line is Canonical JSON");
            var crc = (string?)record["crc"];
            Assert.IsNotNull(crc,
                "the tombstone carries its own integrity checksum");
            Assert.IsTrue(
                System.Text.RegularExpressions.Regex.IsMatch(
                    crc!, "^[0-9a-f]{8}$"),
                "crc is 8 lowercase hex digits");
            return record;
        }

        // ── The SG-16 deletion transaction ───────────────────────────────

        // The happy path: the fsync'd tombstone precedes the removal, the
        // record carries runId/deletedAt/reason, and every read now
        // answers the deleted verdict — OW-STORE-003 from Load, absent
        // from List, the index entry gone.
        [Test]
        public void DeleteTombstonesThenRemovesTheFinishedRun()
        {
            var store = Store();
            var runId = FinishedRun(store);
            Assert.IsTrue(File.Exists(JournalPath(runId)));

            var deleted = store.Delete(runId);

            Assert.IsTrue(deleted.Ok,
                "Delete failed: " + string.Join(
                    ";", deleted.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var lines = TombstoneLines();
            Assert.AreEqual(1, lines.Length, "one tombstone record was appended");
            var record = AssertTombstoneLine(lines[0], runId);
            Assert.AreEqual("retention", (string?)record["reason"]);
            Assert.IsFalse(Directory.Exists(RunDir(runId)),
                "the Run directory is removed after the tombstone");
            var index = (JObject)JsonValue.Parse(
                File.ReadAllText(IndexPath(), s_utf8)).Value!.ToToken();
            Assert.IsFalse(
                ((JArray)index["runs"]!).Cast<JObject>()
                    .Any(e => (string?)e["runId"] == runId),
                "the index entry is removed");
            AssertOwStore003(store.Load(runId));
            Assert.IsFalse(
                Listed(store, new RunQuery()).Any(e => e.RunId == runId),
                "a tombstoned Run is never listed");
            var check = store.IsDeletedRun(runId);
            Assert.IsTrue(check.Ok && check.Value, "the tombstone lookup sees the Run deleted");
        }

        // SPEC §0.6 SG-16 ordering: the tombstone's fsync must precede
        // every destructive step. The stream seam records whether the Run
        // directory still existed at the Flush(flushToDisk: true) call —
        // a delete that removed the directory first could never pass.
        [Test]
        public void TombstoneFsyncPrecedesTheDirectoryRemoval()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var events = new List<string>();
            store.TombstoneStreamWrapper = file =>
                new RecordingTombstoneStream(
                    file, events,
                    () => Directory.Exists(RunDir(runId)));

            Assert.IsTrue(store.Delete(runId).Ok);

            var flushes = events.FindAll(
                e => e.StartsWith("flush:true@", StringComparison.Ordinal));
            Assert.AreEqual(2, flushes.Count,
                "the tombstone append and the EnsureDurable re-publish " +
                    "were each fsync'd — Flush(flushToDisk: true)");
            Assert.IsTrue(
                flushes.TrueForAll(
                    e => e.EndsWith(":runDirExists=true", StringComparison.Ordinal)),
                "the Run directory still existed at every tombstone fsync: " +
                    string.Join(";", flushes));
            Assert.IsFalse(Directory.Exists(RunDir(runId)));
        }

        // The durability contract: a new store instance over the same
        // root — the Editor-restart case — still answers OW-STORE-003 for
        // the deleted id while a never-created id stays OW-RUN-010.
        [Test]
        public void DeletedAndUnknownStayDistinctAcrossRestarts()
        {
            var runId = FinishedRun(Store());
            Assert.IsTrue(Store().Delete(runId).Ok);

            var restarted = Store();
            AssertOwStore003(restarted.Load(runId));
            var unknown = restarted.Load(Identifiers.NewRunId());
            Assert.IsFalse(unknown.Ok);
            AssertCode(unknown.Diagnostics, "OW-RUN-010");

            // Idempotent: deleting the already-deleted id succeeds without
            // appending a second tombstone.
            var again = restarted.Delete(runId);
            Assert.IsTrue(again.Ok, "re-deleting a tombstoned Run is idempotent");
            Assert.AreEqual(1, TombstoneLines().Length,
                "a re-delete never appends a second tombstone");
        }

        // The crash window SPEC §0.6 SG-16 names: the tombstone committed
        // but the process died before the directory removal. The residue
        // keeps answering deleted — never resume, never OW-RUN-010 — and
        // the recovery sweep finishes the physical removal.
        [Test]
        public void InterruptedDeletionIsCompletedByTheSweep()
        {
            var store = Store();
            var runId = FinishedRun(store);
            // Simulate the committed tombstone without the removal —
            // exactly what an interruption between fsync and directory
            // delete leaves behind.
            File.WriteAllText(
                TombstonePath(),
                DeletedRuns.Line(runId, Ts, "retention") + "\n", s_utf8);
            Assert.IsTrue(Directory.Exists(RunDir(runId)));

            AssertOwStore003(store.Load(runId));
            Assert.IsFalse(
                Listed(store, new RunQuery()).Any(e => e.RunId == runId),
                "the residue never resurfaces as a live Run");
            var unfinished = Listed(store, new RunQuery(new[] { RunState.Running }));
            Assert.IsFalse(
                unfinished.Any(e => e.RunId == runId),
                "a tombstoned Run is never a recovery candidate");

            var swept = store.CompleteInterruptedDeletions();
            Assert.IsTrue(swept.Ok, "the sweep failed: " + string.Join(
                ";", swept.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.IsFalse(Directory.Exists(RunDir(runId)),
                "the sweep finished the interrupted removal");
            AssertOwStore003(store.Load(runId));
            Assert.AreEqual(1, TombstoneLines().Length,
                "the tombstone survives — the id is permanently deleted");
        }

        // The idempotent re-delete is also the retry of an interrupted
        // deletion: the tombstone already proves the verdict, so Delete
        // finishes the leftover removal and reports success.
        [Test]
        public void ReDeletingAResidueFinishesItsRemoval()
        {
            var store = Store();
            var runId = FinishedRun(store);
            File.WriteAllText(
                TombstonePath(),
                DeletedRuns.Line(runId, Ts, "manual") + "\n", s_utf8);
            Assert.IsTrue(Directory.Exists(RunDir(runId)));

            var deleted = store.Delete(runId);

            Assert.IsTrue(deleted.Ok);
            Assert.IsFalse(Directory.Exists(RunDir(runId)));
            Assert.AreEqual(1, TombstoneLines().Length);
        }

        // SG-16 corruption handling: the tombstone file that cannot prove
        // its contents — an unreadable or malformed committed record —
        // fails every read closed as OW-STORE-002: Load cannot answer
        // OW-RUN-010 for a Run it cannot prove was never deleted, List
        // cannot pretend the store is empty, and Delete removes nothing.
        [Test]
        public void CorruptTombstoneFailsClosed()
        {
            var store = Store();
            var runId = FinishedRun(store);
            File.WriteAllText(TombstonePath(), "not a tombstone record\n", s_utf8);

            var load = store.Load(runId);
            Assert.IsFalse(load.Ok);
            AssertCode(load.Diagnostics, "OW-STORE-002");
            var list = store.List(new RunQuery());
            Assert.IsFalse(list.Ok);
            AssertCode(list.Diagnostics, "OW-STORE-002");
            var deleted = store.Delete(runId);
            Assert.IsFalse(deleted.Ok);
            AssertCode(deleted.Diagnostics, "OW-STORE-002");
            var check = store.IsDeletedRun(runId);
            Assert.IsFalse(check.Ok);
            AssertCode(check.Diagnostics, "OW-STORE-002");
            Assert.IsTrue(Directory.Exists(RunDir(runId)),
                "a corrupt tombstone set stops every deletion");
        }

        // A malformed record member — a reason outside the SG-16 pair —
        // is the same unproven file: OW-STORE-002, nothing deleted.
        [Test]
        public void TombstoneWithAnUnknownReasonFailsClosed()
        {
            var store = Store();
            var runId = FinishedRun(store);
            File.WriteAllText(
                TombstonePath(),
                DeletedRuns.Line(runId, Ts, "bogus") + "\n", s_utf8);

            var load = store.Load(runId);
            Assert.IsFalse(load.Ok);
            AssertCode(load.Diagnostics, "OW-STORE-002");
            Assert.IsTrue(Directory.Exists(RunDir(runId)));
        }

        // The interrupted-append residue under the atomic commit
        // protocol is not corruption: a crash before the rename leaves
        // the previously committed file untouched and at most an orphan
        // temp file — the incomplete append's bytes never enter
        // committed content. The read still answers the committed
        // verdicts and the next append truncates the residue away.
        [Test]
        public void InterruptedAppendLeavesCommittedContentIntact()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var other = FinishedRun(store);
            File.WriteAllText(
                TombstonePath(),
                DeletedRuns.Line(runId, Ts, "retention") + "\n", s_utf8);
            File.WriteAllText(
                TombstonePath() + ".opweave-tmp",
                DeletedRuns.Line(runId, Ts, "retention") + "\n" +
                    "{\"runId\":\"part",
                s_utf8);

            AssertOwStore003(store.Load(runId));
            var live = store.Load(other);
            Assert.IsTrue(live.Ok,
                "the orphan temp of an interrupted append does not corrupt the read");

            Assert.IsTrue(store.Delete(other).Ok,
                "the next tombstone commit overwrites the temp residue");
            var lines = TombstoneLines();
            Assert.AreEqual(2, lines.Length);
            AssertTombstoneLine(lines[0], runId);
            AssertTombstoneLine(lines[1], other);
            Assert.IsFalse(File.Exists(TombstonePath() + ".opweave-tmp"),
                "the temp residue is consumed by the commit");
        }

        // F1: a previously committed final record that loses only its
        // terminator has the same on-disk form as an interrupted append —
        // the file can no longer prove which it is, so the read fails
        // closed as OW-STORE-002 rather than silently drop the tail and
        // lose a tombstone that was reported committed.
        [Test]
        public void CommittedRecordMissingTerminatorFailsClosed()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var other = FinishedRun(store);
            File.WriteAllText(
                TombstonePath(),
                DeletedRuns.Line(runId, Ts, "retention") + "\n" +
                    DeletedRuns.Line(other, Ts, "manual"),
                s_utf8);

            var load = store.Load(runId);
            Assert.IsFalse(load.Ok);
            AssertCode(load.Diagnostics, "OW-STORE-002");
            var check = store.IsDeletedRun(runId);
            Assert.IsFalse(check.Ok);
            AssertCode(check.Diagnostics, "OW-STORE-002");
            var deleted = store.Delete(runId);
            Assert.IsFalse(deleted.Ok);
            AssertCode(deleted.Diagnostics, "OW-STORE-002");
            Assert.IsTrue(Directory.Exists(RunDir(runId)),
                "an unproven tombstone file stops every deletion");
            Assert.IsTrue(Directory.Exists(RunDir(other)));
        }

        // F1: a committed final record truncated mid-body is the same
        // unprovable tail — OW-STORE-002, never droppable residue whose
        // loss would resurrect the deleted id as OW-RUN-010.
        [Test]
        public void TruncatedCommittedRecordFailsClosed()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var other = FinishedRun(store);
            var second = DeletedRuns.Line(other, Ts, "manual");
            File.WriteAllText(
                TombstonePath(),
                DeletedRuns.Line(runId, Ts, "retention") + "\n" +
                    second.Substring(0, second.Length - 12),
                s_utf8);

            var load = store.Load(runId);
            Assert.IsFalse(load.Ok);
            AssertCode(load.Diagnostics, "OW-STORE-002");
            var deleted = store.Delete(other);
            Assert.IsFalse(deleted.Ok);
            AssertCode(deleted.Diagnostics, "OW-STORE-002");
            Assert.IsTrue(Directory.Exists(RunDir(runId)));
            Assert.IsTrue(Directory.Exists(RunDir(other)));
        }

        // SG-16's "CRC相当の破損": a committed line whose bytes no
        // longer match their own checksum is unprovable — OW-STORE-002 —
        // even though it still parses as a well-formed record.
        [Test]
        public void TombstoneRecordWithDamagedCrcFailsClosed()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var record = (JObject)JsonValue.Parse(
                DeletedRuns.Line(runId, Ts, "retention")).Value!.ToToken();
            var crc = (string)record["crc"]!;
            record["crc"] =
                (crc[0] == '0' ? "1" : "0") + crc.Substring(1);
            File.WriteAllText(
                TombstonePath(),
                CanonicalJson.Serialize(record) + "\n", s_utf8);

            var load = store.Load(runId);
            Assert.IsFalse(load.Ok);
            AssertCode(load.Diagnostics, "OW-STORE-002");
            Assert.IsTrue(Directory.Exists(RunDir(runId)));
        }

        // F2: when the write completed but the fsync threw, the failed
        // append must commit nothing a retry or the sweep could rely on —
        // under the atomic protocol the uncommitted record is never
        // visible as committed — and the retried delete performs a fresh
        // durable flush before the directory removal. The seam fails
        // only the first Flush(flushToDisk: true).
        [Test]
        public void FailedTombstoneFlushCommitsNothingAndTheRetryFsyncs()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var events = new List<string>();
            var flushesToFail = 1;
            store.TombstoneStreamWrapper = file =>
                new RecordingTombstoneStream(
                    file, events,
                    () => Directory.Exists(RunDir(runId)),
                    () => flushesToFail-- > 0);

            var first = store.Delete(runId);

            Assert.IsFalse(first.Ok);
            AssertCode(first.Diagnostics, "OW-STORE-001");
            Assert.IsTrue(Directory.Exists(RunDir(runId)),
                "the failed append removed nothing");
            Assert.IsTrue(events.Contains("flush:true:threw"),
                "the fsync failure was injected after the complete write");
            Assert.IsFalse(File.Exists(TombstonePath()),
                "an un-fsync'd record is never visible as committed");

            var retried = store.Delete(runId);

            Assert.IsTrue(retried.Ok,
                "the retried delete commits the tombstone");
            var flushes = events.FindAll(
                e => e.StartsWith("flush:true@", StringComparison.Ordinal));
            Assert.AreEqual(2, flushes.Count,
                "the retry's append and EnsureDurable each fsync'd");
            Assert.IsTrue(
                flushes.TrueForAll(
                    e => e.EndsWith(":runDirExists=true", StringComparison.Ordinal)),
                "every durable flush preceded the directory removal: " +
                    string.Join(";", flushes));
            Assert.IsFalse(Directory.Exists(RunDir(runId)));
            var lines = TombstoneLines();
            Assert.AreEqual(1, lines.Length);
            AssertTombstoneLine(lines[0], runId);
            AssertOwStore003(store.Load(runId));

            var swept = store.CompleteInterruptedDeletions();
            Assert.IsTrue(swept.Ok,
                "the sweep relies only on the durable committed tombstone");
        }

        // ── The SG-16 durable publish (F2) ────────────────────────────

        // SPEC §0.6 SG-16: flushing the tombstone's bytes is not enough —
        // the rename that publishes them (the directory entry) must
        // itself be durable before the destructive half. The publish
        // seam forwards to the real DurableRename.Move and records that
        // the durable publish completed while the Run directory and its
        // index entry still existed: a delete that published only to
        // the page cache, or removed anything first, could never pass.
        [Test]
        public void DurablePublishPrecedesTheDirectoryAndIndexRemoval()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var events = new List<string>();
            store.TombstonePublishStep = (temp, target) =>
            {
                DurableRename.Move(temp, target);
                events.Add(
                    "publish:durable:runDirExists=" +
                        (Directory.Exists(RunDir(runId)) ? "true" : "false") +
                    ":indexHasEntry=" +
                        (IndexHasEntry(runId) ? "true" : "false"));
            };

            var deleted = store.Delete(runId);

            Assert.IsTrue(deleted.Ok, "Delete failed: " + string.Join(
                ";", deleted.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.AreEqual(
                new[]
                {
                    "publish:durable:runDirExists=true:indexHasEntry=true",
                    "publish:durable:runDirExists=true:indexHasEntry=true",
                },
                events.ToArray(),
                "the append's publish and the EnsureDurable re-publish " +
                    "each completed while the Run directory and index " +
                    "entry still existed: " + string.Join(";", events));
            Assert.IsFalse(Directory.Exists(RunDir(runId)));
            Assert.IsFalse(IndexHasEntry(runId),
                "the index entry was removed after the durable publish");
            Assert.AreEqual(1, TombstoneLines().Length);
        }

        // The same ordering through the recovery path: the durable
        // publish commits, then the simulated crash lands before the
        // removal — the residue is exactly what the sweep finishes. The
        // recorded order shows the durable publish preceding the Run
        // directory and index removal CompleteInterruptedDeletions
        // performs.
        [Test]
        public void SweepRemovesOnlyAfterADurablyPublishedTombstone()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var events = new List<string>();
            store.TombstonePublishStep = (temp, target) =>
            {
                DurableRename.Move(temp, target);
                events.Add(
                    "publish:durable:runDirExists=" +
                        (Directory.Exists(RunDir(runId)) ? "true" : "false"));
                throw new IOException(
                    "simulated crash after the durable publish");
            };

            var crashed = store.Delete(runId);

            Assert.IsFalse(crashed.Ok);
            AssertCode(crashed.Diagnostics, "OW-STORE-001");
            Assert.IsTrue(
                events.Contains("publish:durable:runDirExists=true"),
                "the tombstone was durably published before the crash");
            Assert.IsTrue(Directory.Exists(RunDir(runId)),
                "the crash kept the destructive half pending");
            Assert.AreEqual(1, TombstoneLines().Length,
                "the durable publish committed the tombstone");
            AssertOwStore003(store.Load(runId));

            store.TombstonePublishStep = (temp, target) =>
            {
                DurableRename.Move(temp, target);
                events.Add(
                    "sweep:publish:durable:runDirExists=" +
                        (Directory.Exists(RunDir(runId)) ? "true" : "false"));
            };
            events.Add("sweep:called");
            var swept = store.CompleteInterruptedDeletions();
            events.Add(
                "sweep:returned:runDirExists=" +
                    (Directory.Exists(RunDir(runId)) ? "true" : "false"));

            Assert.IsTrue(swept.Ok, "the sweep failed: " + string.Join(
                ";", swept.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.AreEqual(
                new[]
                {
                    "publish:durable:runDirExists=true",
                    "sweep:called",
                    "sweep:publish:durable:runDirExists=true",
                    "sweep:returned:runDirExists=false",
                },
                events.ToArray(),
                "the sweep re-published the tombstone durably before " +
                    "the removal");
            Assert.IsFalse(IndexHasEntry(runId));
            Assert.AreEqual(1, TombstoneLines().Length,
                "the tombstone survives — the id is permanently deleted");
        }

        // F2's narrowed edge: the POSIX publish window shrunk to its
        // last step — the rename landed, so the record is visible as
        // committed, but the containing directory's fsync failed and
        // the entry is not durable. The retried delete must never
        // remove on that record: EnsureDurable re-publishes the observed
        // committed bytes — fsync plus durable rename — before the
        // destructive half.
        [Test]
        public void VisibleButUnsyncedTombstoneIsReDurableBeforeTheRetryRemoves()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var events = new List<string>();
            var syncFails = true;
            store.TombstonePublishStep = (temp, target) =>
            {
                if (syncFails)
                {
                    // The rename lands the record visibly — committed in
                    // every readable way — then the directory sync
                    // fails: MovePosix's post-rename window.
                    File.Delete(target);
                    File.Move(temp, target);
                    events.Add("publish:visible-unsynced");
                    throw new IOException(
                        "simulated directory-sync failure after the rename");
                }
                DurableRename.Move(temp, target);
                events.Add(
                    "publish:durable:runDirExists=" +
                        (Directory.Exists(RunDir(runId)) ? "true" : "false") +
                    ":indexHasEntry=" +
                        (IndexHasEntry(runId) ? "true" : "false"));
            };

            var first = store.Delete(runId);

            Assert.IsFalse(first.Ok);
            AssertCode(first.Diagnostics, "OW-STORE-001");
            Assert.IsTrue(Directory.Exists(RunDir(runId)),
                "the failed publish removed nothing");
            Assert.AreEqual(1, TombstoneLines().Length,
                "the renamed record stays visible — the unproven edge");
            AssertOwStore003(store.Load(runId));

            syncFails = false;
            var retried = store.Delete(runId);

            Assert.IsTrue(retried.Ok, "the retry failed: " + string.Join(
                ";", retried.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.AreEqual(
                new[]
                {
                    "publish:visible-unsynced",
                    "publish:durable:runDirExists=true:indexHasEntry=true",
                },
                events.ToArray(),
                "the retry re-published the committed tombstone durably " +
                    "before the removal");
            Assert.IsFalse(Directory.Exists(RunDir(runId)));
            Assert.IsFalse(IndexHasEntry(runId));
            Assert.AreEqual(1, TombstoneLines().Length,
                "EnsureDurable re-published identical committed bytes");
            AssertTombstoneLine(TombstoneLines()[0], runId);
            AssertOwStore003(store.Load(runId));
        }

        // The same edge with the directory-sync failure persisting: the
        // retried delete and the sweep each run EnsureDurable, each
        // publish fails again — OW-STORE-001 — and the Run directory,
        // its index entry and the committed tombstone all stay. Once
        // the failure clears, the sweep's EnsureDurable succeeds before
        // the removal.
        [Test]
        public void PersistentUnsyncedTombstoneRemovesNothingOnRetryOrSweep()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var events = new List<string>();
            var syncFails = true;
            store.TombstonePublishStep = (temp, target) =>
            {
                if (syncFails)
                {
                    File.Delete(target);
                    File.Move(temp, target);
                    events.Add("publish:visible-unsynced");
                    throw new IOException(
                        "simulated directory-sync failure after the rename");
                }
                DurableRename.Move(temp, target);
                events.Add(
                    "publish:durable:runDirExists=" +
                        (Directory.Exists(RunDir(runId)) ? "true" : "false"));
            };

            Assert.IsFalse(store.Delete(runId).Ok,
                "the injected publish failure fails the first delete");
            Assert.IsTrue(Directory.Exists(RunDir(runId)));

            var retried = store.Delete(runId);
            Assert.IsFalse(retried.Ok);
            AssertCode(retried.Diagnostics, "OW-STORE-001");
            Assert.IsTrue(Directory.Exists(RunDir(runId)),
                "the retry's EnsureDurable failed closed — nothing removed");
            Assert.IsTrue(IndexHasEntry(runId));

            var swept = store.CompleteInterruptedDeletions();
            Assert.IsFalse(swept.Ok);
            AssertCode(swept.Diagnostics, "OW-STORE-001");
            Assert.IsTrue(Directory.Exists(RunDir(runId)),
                "the sweep's EnsureDurable failed closed — nothing removed");
            Assert.IsTrue(IndexHasEntry(runId));
            Assert.AreEqual(1, TombstoneLines().Length,
                "the committed record is never lost");
            AssertOwStore003(store.Load(runId));

            syncFails = false;
            events.Add("sweep:called");
            var sweptAgain = store.CompleteInterruptedDeletions();
            events.Add(
                "sweep:returned:runDirExists=" +
                    (Directory.Exists(RunDir(runId)) ? "true" : "false"));

            Assert.IsTrue(sweptAgain.Ok, "the sweep failed: " + string.Join(
                ";", sweptAgain.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.AreEqual(
                new[]
                {
                    "publish:visible-unsynced",
                    "publish:visible-unsynced",
                    "publish:visible-unsynced",
                    "sweep:called",
                    "publish:durable:runDirExists=true",
                    "sweep:returned:runDirExists=false",
                },
                events.ToArray(),
                "every removal attempt re-established durability first; " +
                    "the Run was removed only after a durable publish");
            Assert.IsFalse(Directory.Exists(RunDir(runId)));
            Assert.IsFalse(IndexHasEntry(runId));
        }

        // A durable publish that fails — the rename denied or the
        // directory fsync lost — commits nothing the destructive half
        // could rely on: OW-STORE-001, the Run directory and index entry
        // untouched, the Run still loads, no committed tombstone.
        [Test]
        public void FailedDurablePublishRemovesNothingAndTheRunStillLoads()
        {
            var store = Store();
            var runId = FinishedRun(store);
            store.TombstonePublishStep = (temp, target) =>
                throw new IOException("simulated durable-publish failure");

            var deleted = store.Delete(runId);

            Assert.IsFalse(deleted.Ok);
            AssertCode(deleted.Diagnostics, "OW-STORE-001");
            Assert.IsTrue(Directory.Exists(RunDir(runId)),
                "the failed publish removed nothing");
            Assert.IsTrue(IndexHasEntry(runId));
            Assert.IsFalse(File.Exists(TombstonePath()),
                "no committed tombstone was published");
            Assert.IsFalse(File.Exists(TombstonePath() + ".opweave-tmp"),
                "the unpublished temp file was cleaned up");
            var load = store.Load(runId);
            Assert.IsTrue(load.Ok,
                "the Run still loads — nothing was destroyed");
            Assert.AreEqual(RunState.Succeeded, load.Value!.State);
        }

        // The real primitive on the current platform: the durable rename
        // moves the fsync'd temp over the committed path — creating it
        // or atomically replacing it — and the committed bytes read back
        // exactly.
        [Test]
        public void DurableRenamePublishesCommittedContent()
        {
            var target = Path.Combine(_root, "publish-target.ndjson");
            var temp = Path.Combine(_root, "publish-target.opweave-tmp");
            var first = s_utf8.GetBytes(DeletedRuns.Line(
                Identifiers.NewRunId(), Ts, "retention") + "\n");
            File.WriteAllBytes(temp, first);

            DurableRename.Move(temp, target);

            Assert.IsFalse(File.Exists(temp),
                "the publish consumed the temp file");
            CollectionAssert.AreEqual(
                first, File.ReadAllBytes(target),
                "the committed bytes read back exactly");

            var second = s_utf8.GetBytes(DeletedRuns.Line(
                Identifiers.NewRunId(), Ts, "manual") + "\n");
            File.WriteAllBytes(temp, second);
            DurableRename.Move(temp, target);

            Assert.IsFalse(File.Exists(temp));
            CollectionAssert.AreEqual(
                second, File.ReadAllBytes(target),
                "the durable publish also replaces an existing target");
        }

        // SPEC §F8 / SG-16: only terminal Runs may be deleted — a Queued
        // Run is refused as OW-STORE-001 with directory, journal, index
        // and tombstone file untouched. (Running and NeedsReview are
        // covered in FileRunStoreLoadTests.)
        [Test]
        public void QueuedAndAwaitingApprovalRunsAreNeverDeleted()
        {
            var store = Store();
            var queued = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(queued, Plan(), Doc()).Ok);
            var awaiting = Identifiers.NewRunId();
            Assert.IsTrue(store.CreateRun(awaiting, Plan(approvalCount: 1), Doc()).Ok);
            var journalBefore = File.ReadAllBytes(JournalPath(queued));
            var indexBefore = File.ReadAllBytes(IndexPath());

            var queuedDelete = store.Delete(queued);
            Assert.IsFalse(queuedDelete.Ok);
            AssertCode(queuedDelete.Diagnostics, "OW-STORE-001");
            var awaitingDelete = store.Delete(awaiting);
            Assert.IsFalse(awaitingDelete.Ok);
            AssertCode(awaitingDelete.Diagnostics, "OW-STORE-001");
            CollectionAssert.AreEqual(
                journalBefore, File.ReadAllBytes(JournalPath(queued)),
                "the refused Run's journal is untouched");
            CollectionAssert.AreEqual(
                indexBefore, File.ReadAllBytes(IndexPath()),
                "the refused Run's index entry is untouched");
            Assert.IsFalse(File.Exists(TombstonePath()),
                "no tombstone was ever committed");
            Assert.AreEqual(RunState.Queued, store.Load(queued).Value!.State);
            Assert.AreEqual(RunState.AwaitingApproval, store.Load(awaiting).Value!.State);
        }

        // SG-16: a tombstoned runId is never reused — the create is
        // refused with the same OW-STORE-003 verdict.
        [Test]
        public void TombstonedRunIdIsNeverReused()
        {
            var store = Store();
            var runId = FinishedRun(store);
            Assert.IsTrue(store.Delete(runId).Ok);

            var created = store.CreateRun(runId, Plan(), Doc());

            Assert.IsFalse(created.Ok);
            AssertCode(created.Diagnostics, "OW-STORE-003");
            Assert.IsFalse(Directory.Exists(RunDir(runId)));
        }

        // The tombstone file is retained independently of the §F8 Run
        // limits: every deletion appends, none is ever rotated away.
        [Test]
        public void TombstonesAccumulateAcrossDeletions()
        {
            var store = Store();
            var first = FinishedRun(store);
            var second = FinishedRun(store);

            Assert.IsTrue(store.Delete(first).Ok);
            Assert.IsTrue(store.Delete(second).Ok);

            var lines = TombstoneLines();
            Assert.AreEqual(2, lines.Length);
            AssertTombstoneLine(lines[0], first);
            AssertTombstoneLine(lines[1], second);
        }

        // An operator-initiated deletion records reason "manual" — the
        // second SG-16 reason — through the internal reason overload.
        [Test]
        public void ManualDeletionRecordsItsReason()
        {
            var store = Store();
            var runId = FinishedRun(store);

            Assert.IsTrue(store.Delete(runId, "manual").Ok);

            var record = AssertTombstoneLine(TombstoneLines()[0], runId);
            Assert.AreEqual("manual", (string?)record["reason"]);
            AssertOwStore003(store.Load(runId));
            Assert.Throws<ArgumentException>(
                () => store.Delete(FinishedRun(store), "bogus"),
                "an unknown reason is a programming error, never a written tombstone");
        }

        // The whole transaction runs under the Run Store exclusion: with
        // coordinator.lock already held, Delete refuses — OW-RUN-002 —
        // and touches nothing.
        [Test]
        public void DeleteUnderAHeldExclusionIsRefused()
        {
            var store = Store();
            var runId = FinishedRun(store);
            var acquired = CoordinatorRunLock.TryAcquire(_root);
            Assert.IsTrue(acquired.Ok, "the test could not take the exclusion");
            using (acquired.Value!)
            {
                var deleted = store.Delete(runId);

                Assert.IsFalse(deleted.Ok);
                AssertCode(deleted.Diagnostics, "OW-RUN-002");
                Assert.IsTrue(Directory.Exists(RunDir(runId)),
                    "the refused delete removed nothing");
                Assert.IsFalse(File.Exists(TombstonePath()),
                    "the refused delete committed no tombstone");
            }
        }

        // The sweep also takes the exclusion — a held lock defers it
        // without touching the residue.
        [Test]
        public void SweepUnderAHeldExclusionIsRefused()
        {
            var store = Store();
            var runId = FinishedRun(store);
            File.WriteAllText(
                TombstonePath(),
                DeletedRuns.Line(runId, Ts, "retention") + "\n", s_utf8);
            var acquired = CoordinatorRunLock.TryAcquire(_root);
            Assert.IsTrue(acquired.Ok);
            using (acquired.Value!)
            {
                var swept = store.CompleteInterruptedDeletions();

                Assert.IsFalse(swept.Ok);
                AssertCode(swept.Diagnostics, "OW-RUN-002");
                Assert.IsTrue(Directory.Exists(RunDir(runId)));
            }
        }

        // Unknown ids stay OW-RUN-010 through every tombstone-aware path
        // — the file's presence changes nothing for a never-created Run.
        [Test]
        public void UnknownIdsStayOwRun010WithTombstonesPresent()
        {
            var store = Store();
            var runId = FinishedRun(store);
            Assert.IsTrue(store.Delete(runId).Ok);
            var unknown = Identifiers.NewRunId();

            var load = store.Load(unknown);
            Assert.IsFalse(load.Ok);
            AssertCode(load.Diagnostics, "OW-RUN-010");
            var deleted = store.Delete(unknown);
            Assert.IsFalse(deleted.Ok);
            AssertCode(deleted.Diagnostics, "OW-RUN-010");
            var check = store.IsDeletedRun(unknown);
            Assert.IsTrue(check.Ok && !check.Value);
        }

        // Observing wrapper for the tombstone-stream seam — the same
        // shape as the journal's RecordingJournalStream: forwards every
        // call and records the write/flush/dispose sequence plus, at the
        // fsync, whether the Run directory still existed.
        private sealed class RecordingTombstoneStream : FileStream
        {
            private readonly FileStream _inner;
            private readonly List<string> _events;
            private readonly Func<bool> _runDirExists;
            private readonly Func<bool>? _failFlush;

            internal RecordingTombstoneStream(
                FileStream inner, List<string> events, Func<bool> runDirExists,
                Func<bool>? failFlush = null)
                : base(inner.SafeFileHandle, FileAccess.Write)
            {
                _inner = inner;
                _events = events;
                _runDirExists = runDirExists;
                _failFlush = failFlush;
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
                if (flushToDisk && _failFlush is { } fail && fail())
                {
                    _events.Add("flush:true:threw");
                    throw new IOException("simulated fsync failure");
                }
                _events.Add(
                    "flush:" + (flushToDisk ? "true" : "false") + "@" +
                    _inner.Position +
                    ":runDirExists=" + (_runDirExists() ? "true" : "false"));
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
    }
}
