#nullable enable

// T-W4.4-01 [Core] tests: local:T-W4.4-01:retention — SPEC §F8 candidate
// selection and scheduling over the real FileRunStore. The pass keeps the
// newest MaxFinishedRuns finished Runs under MaxFinishedBytes, selects the
// oldest finished Runs past either bound (SPEC §F8 test 1: the 51st finish
// deletes the oldest finished Run) and delegates physical deletion to the
// injected SG-16 provider seam — T-W4.4-02's tombstone-writing
// FileRunStore.Delete, which physically deletes what it confirms.
// NeedsReview and unfinished Runs are never selected. The same sources
// run under dotnet and Unity Mono.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Tests
{
    internal sealed class RunRetentionTests
    {
        private const string Session = "session-retention-test";
        private const string Ts = "2026-11-06T00:00:00.000Z";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.Retention." + Guid.NewGuid().ToString("N"));
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

        // ── Fixtures ──────────────────────────────────────────────────────

        private FileRunStore Store() => new FileRunStore(_root, Session);

        // Fabricated, strictly increasing ULIDs: "run_" + 26 Crockford
        // base32 characters, so the Run id tie-break of the newest-first
        // ordering is deterministic even when every Run finishes inside the
        // same millisecond.
        private static string RunId(int i) => "run_01J0000000000" + i.ToString("D13");

        private static string Hash64(char c) => new string(c, 64);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        // The same fully verifiable Plan FileRunStoreTests builds: List
        // derives entries through Load, which re-verifies planFileDigest
        // and planHash — the fixture must decode and match.
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
                    ["id"] = "example.retention",
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
                "example.retention", Hash64('b'), Hash64('c'),
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
                Id = "example.retention",
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

        private static JournalRecord Record(long seq, JournalRecordType type, string dataJson) =>
            new JournalRecord(seq, Ts, Session, type, null, null, Json(dataJson));

        // A Run journaled to a terminal state: RunAccepted (seq 1) →
        // run_started → run_finished{state}.
        private void CreateFinished(FileRunStore store, string runId, string state = "succeeded")
        {
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok, "CreateRun failed for " + runId);
            Assert.IsTrue(
                store.Append(runId, Record(2, JournalRecordType.RunStarted, "{}"),
                    Durability.Flushed).Ok,
                "run_started append failed for " + runId);
            Assert.IsTrue(
                store.Append(runId, Record(3, JournalRecordType.RunFinished,
                        "{\"state\":\"" + state + "\",\"outputs\":{},\"summary\":\"done\"}"),
                    Durability.Flushed).Ok,
                "run_finished append failed for " + runId);
        }

        // A Run whose plan.json diverges from the journaled planFileDigest:
        // the journal-derived truth classifies it NeedsReview (SPEC §D0.1,
        // §F2) — never finished.
        private void CreateNeedsReview(FileRunStore store, string runId)
        {
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok, "CreateRun failed for " + runId);
            var planPath = Path.Combine(RunDir(runId), "plan.json");
            var bytes = File.ReadAllBytes(planPath);
            bytes[0] ^= 0xFF;
            File.WriteAllBytes(planPath, bytes);
        }

        private void CreateQueued(FileRunStore store, string runId)
        {
            Assert.IsTrue(store.CreateRun(runId, Plan(), Doc()).Ok, "CreateRun failed for " + runId);
        }

        // The physical half of the SG-16 deletion, stubbed without the
        // tombstone: removes the Run directory so the journal-derived
        // index drops it.
        private Func<string, Result<Unit>> TestDeleter(List<string> order) => runId =>
        {
            order.Add(runId);
            Directory.Delete(RunDir(runId), recursive: true);
            return Result<Unit>.Success(Unit.Value);
        };

        private static Dictionary<string, RunState> StatesByRun(FileRunStore store)
        {
            var listed = store.List(new RunQuery());
            Assert.IsTrue(listed.Ok, "List failed.");
            var states = new Dictionary<string, RunState>(StringComparer.Ordinal);
            foreach (var entry in listed.Value!)
            {
                states[entry.RunId] = entry.State;
            }
            return states;
        }

        // ── Selection contract ────────────────────────────────────────────

        // The pass sees only the terminal set: unfinished and NeedsReview
        // Runs are never selected, and with no deletion provider bound the
        // selection changes nothing on disk.
        [Test]
        public void SelectsOnlyTerminalCandidates_AndWithoutProviderNothingIsDeleted()
        {
            var store = Store();
            var finished = new[] { RunId(1), RunId(2), RunId(3), RunId(4) };
            foreach (var runId in finished)
            {
                CreateFinished(store, runId);
            }
            var queued = RunId(5);
            var needsReview = RunId(6);
            CreateQueued(store, queued);
            CreateNeedsReview(store, needsReview);

            var retention = new RunRetention(store, store.MeasureRunBytes);
            var applied = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 2, MaxFinishedBytes: long.MaxValue));

            Assert.IsTrue(applied.Ok, "the pass must succeed");
            var outcome = applied.Value!;
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(2) }, outcome.Candidates,
                "the oldest finished Runs past the count bound, oldest first");
            Assert.IsEmpty(outcome.Deleted,
                "without a deletion provider nothing may be reported deleted");

            foreach (var runId in new[] { RunId(1), RunId(2), RunId(3), RunId(4), queued, needsReview })
            {
                Assert.IsTrue(Directory.Exists(RunDir(runId)),
                    "no provider: the Run directory stays — " + runId);
            }
            var states = StatesByRun(store);
            Assert.AreEqual(6, states.Count);
            Assert.AreEqual(RunState.Queued, states[queued]);
            Assert.AreEqual(RunState.NeedsReview, states[needsReview]);
            foreach (var runId in finished)
            {
                Assert.AreEqual(RunState.Succeeded, states[runId]);
            }
        }

        // SPEC §F8 test 1: finishing the 51st Run deletes the oldest
        // finished Run; the NeedsReview and the unfinished Run stay.
        [Test]
        public void Finishing51stRun_DeletesOldestFinished_NeedsReviewAndUnfinishedKept()
        {
            var store = Store();
            for (var i = 1; i <= 50; i++)
            {
                CreateFinished(store, RunId(i));
            }
            var needsReview = RunId(51);
            var queued = RunId(52);
            CreateNeedsReview(store, needsReview);
            CreateQueued(store, queued);
            // The 51st finished Run — the F8 test-1 trigger.
            var fiftyFirst = RunId(53);
            CreateFinished(store, fiftyFirst);

            var deleted = new List<string>();
            var retention = new RunRetention(
                store, store.MeasureRunBytes, TestDeleter(deleted));
            var applied = retention.Apply(
                RunRetentionLimits.FromSettings(retainRuns: 50, retainMegabytes: 200));

            Assert.IsTrue(applied.Ok, "the pass must succeed");
            var outcome = applied.Value!;
            CollectionAssert.AreEqual(new[] { RunId(1) }, outcome.Candidates,
                "exactly the oldest finished Run is selected");
            CollectionAssert.AreEqual(new[] { RunId(1) }, deleted,
                "the delegated provider is invoked for exactly the candidate");
            CollectionAssert.AreEqual(deleted, outcome.Deleted);
            Assert.IsFalse(Directory.Exists(RunDir(RunId(1))),
                "the oldest finished Run is physically deleted by the provider");

            var states = StatesByRun(store);
            Assert.AreEqual(52, states.Count,
                "50 finished + NeedsReview + Queued remain");
            Assert.AreEqual(RunState.NeedsReview, states[needsReview]);
            Assert.AreEqual(RunState.Queued, states[queued]);
            Assert.AreEqual(RunState.Succeeded, states[fiftyFirst]);
            for (var i = 2; i <= 50; i++)
            {
                Assert.AreEqual(RunState.Succeeded, states[RunId(i)]);
            }
        }

        // The delegated store seam is the real SG-16 provider now
        // (T-W4.4-02): a selected Run is tombstoned and physically
        // deleted, the outcome reports exactly the confirmed ids and the
        // retained Runs stay untouched. A provider that refuses is
        // covered by MidProviderFailure below.
        [Test]
        public void DelegatedDeletion_DeletesThroughTheRealProvider()
        {
            var store = Store();
            for (var i = 1; i <= 4; i++)
            {
                CreateFinished(store, RunId(i));
            }

            var retention = new RunRetention(
                store, store.MeasureRunBytes, store.Delete);
            var applied = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 2, MaxFinishedBytes: long.MaxValue));

            Assert.IsTrue(applied.Ok, "the pass must succeed");
            var outcome = applied.Value!;
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(2) }, outcome.Candidates);
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(2) }, outcome.Deleted,
                "only the confirmed deletions are reported");
            Assert.IsFalse(Directory.Exists(RunDir(RunId(1))),
                "the provider physically deleted the oldest Run");
            Assert.IsFalse(Directory.Exists(RunDir(RunId(2))));
            foreach (var runId in new[] { RunId(1), RunId(2) })
            {
                var load = store.Load(runId);
                Assert.IsFalse(load.Ok);
                Assert.IsTrue(load.Diagnostics.Any(
                    d => d.Code == "OW-STORE-003"),
                    "the deleted Run answers the tombstone verdict");
            }
            Assert.IsTrue(Directory.Exists(RunDir(RunId(3))));
            Assert.IsTrue(Directory.Exists(RunDir(RunId(4))));
            Assert.AreEqual(2, StatesByRun(store).Count,
                "the two retained Runs remain");
        }

        // A provider that refuses one candidate does not stop the rest:
        // the pass keeps the selected set, deletes what the provider
        // confirms and reports only the confirmed ids.
        [Test]
        public void MidProviderFailure_ContinuesAndReportsOnlyConfirmed()
        {
            var store = Store();
            for (var i = 1; i <= 4; i++)
            {
                CreateFinished(store, RunId(i));
            }

            Result<Unit> flakyDelete(string runId)
            {
                if (runId == RunId(1))
                {
                    return Result<Unit>.Failure(ErrorCodes.Create(
                        "OW-STORE-001", "simulated tombstone refusal"));
                }
                Directory.Delete(RunDir(runId), recursive: true);
                return Result<Unit>.Success(Unit.Value);
            }

            var retention = new RunRetention(
                store, store.MeasureRunBytes, flakyDelete);
            var applied = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 2, MaxFinishedBytes: long.MaxValue));

            Assert.IsTrue(applied.Ok);
            var outcome = applied.Value!;
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(2) }, outcome.Candidates);
            CollectionAssert.AreEqual(new[] { RunId(2) }, outcome.Deleted,
                "only the provider-confirmed deletion is reported");
            Assert.IsTrue(Directory.Exists(RunDir(RunId(1))));
            Assert.IsFalse(Directory.Exists(RunDir(RunId(2))));
            Assert.AreEqual(3, StatesByRun(store).Count);
        }

        // ── Size bound ────────────────────────────────────────────────────

        // The byte bound keeps the newest prefix whose combined size fits:
        // a cap that only the newest fits selects every older Run; a cap
        // that exactly fits the newest two keeps them (the boundary is
        // allowed, SPEC §F8's "まで" includes the limit itself).
        [Test]
        public void SizeBound_SelectsOldestPastTheByteLimit()
        {
            var store = Store();
            for (var i = 1; i <= 3; i++)
            {
                CreateFinished(store, RunId(i));
            }
            long size1 = store.MeasureRunBytes(RunId(1)).Value;
            long size2 = store.MeasureRunBytes(RunId(2)).Value;
            long size3 = store.MeasureRunBytes(RunId(3)).Value;
            Assert.Greater(size1, 0, "fixture: the Run directory is measured");

            var retention = new RunRetention(store, store.MeasureRunBytes);

            var tight = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 50, MaxFinishedBytes: size3));
            Assert.IsTrue(tight.Ok);
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(2) }, tight.Value!.Candidates,
                "only the newest fits — the two oldest are candidates");

            var exact = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 50, MaxFinishedBytes: size3 + size2));
            Assert.IsTrue(exact.Ok);
            CollectionAssert.AreEqual(
                new[] { RunId(1) }, exact.Value!.Candidates,
                "newest two fit exactly — only the oldest is a candidate");

            var under = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 50, MaxFinishedBytes: size3 + size2 - 1));
            Assert.IsTrue(under.Ok);
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(2) }, under.Value!.Candidates,
                "one byte short of the two newest — both older are candidates");
        }

        // A finished Run that alone exceeds the byte bound is itself a
        // candidate — "合計 200 MB まで" is a hard total, not a per-Run
        // exemption.
        [Test]
        public void OversizedNewestRun_IsItselfACandidate()
        {
            var store = Store();
            for (var i = 1; i <= 3; i++)
            {
                CreateFinished(store, RunId(i));
            }
            long size3 = store.MeasureRunBytes(RunId(3)).Value;

            var retention = new RunRetention(store, store.MeasureRunBytes);
            var applied = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 50, MaxFinishedBytes: size3 - 1));

            Assert.IsTrue(applied.Ok);
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(2), RunId(3) },
                applied.Value!.Candidates,
                "every finished Run is beyond the bound the newest alone breaks");
        }

        // MeasureRunBytes sums every file under the Run directory and is
        // OW-RUN-010 for an unknown Run.
        [Test]
        public void MeasureRunBytes_SumsDirectory_AndUnknownIsOwRun010()
        {
            var store = Store();
            var runId = RunId(1);
            CreateFinished(store, runId);

            var measured = store.MeasureRunBytes(runId);
            Assert.IsTrue(measured.Ok);
            long expected = 0;
            foreach (var file in Directory.EnumerateFiles(
                         RunDir(runId), "*", SearchOption.AllDirectories))
            {
                expected += new FileInfo(file).Length;
            }
            Assert.AreEqual(expected, measured.Value);

            File.WriteAllBytes(
                Path.Combine(RunDir(runId), "pad.bin"), new byte[512]);
            Assert.AreEqual(
                expected + 512, store.MeasureRunBytes(runId).Value,
                "every file under the Run directory counts");

            var unknown = store.MeasureRunBytes(RunId(99));
            Assert.IsFalse(unknown.Ok);
            Assert.AreEqual("OW-RUN-010", unknown.Diagnostics[0].Code);
        }

        // An unmeasurable Run fails the pass closed toward keeping: it is
        // retained at zero bytes (it pushes nothing out) and the provider's
        // diagnostic is surfaced.
        [Test]
        public void UnmeasurableRun_IsKeptAtZeroBytesWithDiagnostic()
        {
            var store = Store();
            for (var i = 1; i <= 3; i++)
            {
                CreateFinished(store, RunId(i));
            }

            Result<long> brokenMeasure(string runId) =>
                runId == RunId(3)
                    ? Result<long>.Failure(ErrorCodes.Create(
                        "OW-STORE-001", "simulated measurement failure"))
                    : store.MeasureRunBytes(runId);

            var retention = new RunRetention(store, brokenMeasure);
            var applied = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 50, MaxFinishedBytes: 1));

            Assert.IsTrue(applied.Ok);
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(2) }, applied.Value!.Candidates,
                "the unmeasurable newest is retained at zero; the measured older ones overflow");
            Assert.IsTrue(applied.Diagnostics.Count > 0);
            Assert.AreEqual("OW-STORE-001", applied.Diagnostics[0].Code);
        }

        // The candidate side of the same rule: a Run OLDER than the
        // retained prefix must be measured too — before this check it was
        // selected without its measurement ever being attempted, so an
        // unmeasurable Run could reach the deletion provider unobserved.
        // Fail toward keeping holds past the prefix, on the count bound
        // and on the byte bound alike.
        [Test]
        public void UnmeasurableRunPastTheRetainedPrefix_IsNeverACandidate()
        {
            var store = Store();
            for (var i = 1; i <= 4; i++)
            {
                CreateFinished(store, RunId(i));
            }
            long size4 = store.MeasureRunBytes(RunId(4)).Value;
            Assert.Greater(size4, 0, "fixture: the Run directory is measured");

            // The middle Run of the overflow fails measurement: the count
            // bound retains only the newest, so Runs 3, 2 and 1 sit past
            // the prefix.
            Result<long> brokenMeasure(string runId) =>
                runId == RunId(2)
                    ? Result<long>.Failure(ErrorCodes.Create(
                        "OW-STORE-001", "simulated measurement failure"))
                    : store.MeasureRunBytes(runId);

            // A recording provider that confirms without deleting: which
            // candidates the pass hands to deletion is observable across
            // both applies while every Run directory survives.
            var attempted = new List<string>();
            Result<Unit> recordingDelete(string runId)
            {
                attempted.Add(runId);
                return Result<Unit>.Success(default);
            }
            var retention = new RunRetention(store, brokenMeasure, recordingDelete);

            var byCount = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 1, MaxFinishedBytes: long.MaxValue));
            Assert.IsTrue(byCount.Ok);
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(3) }, byCount.Value!.Candidates,
                "the unmeasurable second-oldest is kept, not selected");
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(3) }, attempted,
                "the deletion provider never sees the unmeasurable Run");
            Assert.IsTrue(
                byCount.Diagnostics.Count > 0 &&
                byCount.Diagnostics[0].Code == "OW-STORE-001",
                "the measurement failure is surfaced, not swallowed");

            // The byte bound: a cap only the newest fits selects the same
            // measurable overflow — and still not the unmeasurable Run.
            attempted.Clear();
            var byBytes = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 50, MaxFinishedBytes: size4));
            Assert.IsTrue(byBytes.Ok);
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(3) }, byBytes.Value!.Candidates,
                "same fail-toward-keeping past the byte bound");
            CollectionAssert.AreEqual(
                new[] { RunId(1), RunId(3) }, attempted);

            Assert.IsTrue(
                Directory.Exists(RunDir(RunId(2))),
                "the unmeasurable Run is never touched");
            Assert.AreEqual(4, StatesByRun(store).Count);
        }

        // ── List truth on a damaged Run ───────────────────────────────────

        // The query behind the pass derives state from the journals: a Run
        // whose plan was tampered with after creation classifies
        // NeedsReview and is never eligible.
        [Test]
        public void TamperedRun_IsNeedsReviewAndNeverACandidate()
        {
            var store = Store();
            for (var i = 1; i <= 3; i++)
            {
                CreateFinished(store, RunId(i));
            }
            var tampered = RunId(4);
            CreateNeedsReview(store, tampered);

            var retention = new RunRetention(store, store.MeasureRunBytes);
            var applied = retention.Apply(new RunRetentionLimits(
                MaxFinishedRuns: 2, MaxFinishedBytes: long.MaxValue));

            Assert.IsTrue(applied.Ok);
            CollectionAssert.AreEqual(
                new[] { RunId(1) }, applied.Value!.Candidates,
                "only the oldest of the three finished Runs; NeedsReview is not a candidate");
            Assert.AreEqual(RunState.NeedsReview, StatesByRun(store)[tampered]);
        }

        // ── Settings conversion ───────────────────────────────────────────

        [Test]
        public void FromSettings_ConvertsMegabytesAndClamps()
        {
            var limits = RunRetentionLimits.FromSettings(retainRuns: 50, retainMegabytes: 200);
            Assert.AreEqual(50, limits.MaxFinishedRuns);
            Assert.AreEqual(200L * 1024L * 1024L, limits.MaxFinishedBytes);

            var zero = RunRetentionLimits.FromSettings(retainRuns: -1, retainMegabytes: -5);
            Assert.AreEqual(0, zero.MaxFinishedRuns);
            Assert.AreEqual(0, zero.MaxFinishedBytes);

            var saturated = RunRetentionLimits.FromSettings(
                retainRuns: long.MaxValue, retainMegabytes: long.MaxValue);
            Assert.AreEqual(long.MaxValue, saturated.MaxFinishedBytes);
        }
    }
}
