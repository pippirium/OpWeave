#nullable enable

using System;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the journal NDJSON codec of SPEC §F2 (local:T-W1.7-01:journal-codec):
    /// the §F2 record-type table (every type writes and reads back with the table
    /// <c>required</c> and durability), frozen golden lines — including the
    /// CRC-32/ISO-HDLC lowercase-hex checksum of SPEC §0.6 SG-12 — byte-equal
    /// round-trips and tamper detection. The same sources run under dotnet and
    /// Unity Mono.
    /// </summary>
    internal sealed class JournalCodecTests
    {
        private const string RunId = "run_01JABCDEFGHJKMNPQRSTVWX";
        private const string PlanHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Session = "session-abcdef0123";
        private const string Ts = "2026-11-04T00:00:00.000Z";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private static JsonValue Data(string json)
        {
            var parsed = JsonValue.Parse(json);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {json}");
            return parsed.Value!;
        }

        private static JournalRecord Record(
            long seq, JournalRecordType type, string dataJson,
            int? step = null, int? attempt = null) =>
            new JournalRecord(seq, Ts, Session, type, step, attempt, Data(dataJson));

        // SPEC §E1: the state enums carried in journal data payloads are exactly
        // the spec's member lists, in spec order.
        [Test]
        public void StateEnumsMatchSpecE1()
        {
            var runStates = (RunState[])Enum.GetValues(typeof(RunState));
            Assert.AreEqual(
                new[]
                {
                    RunState.Queued, RunState.AwaitingApproval, RunState.Running,
                    RunState.Suspended, RunState.NeedsReview, RunState.Succeeded,
                    RunState.Failed, RunState.Cancelled,
                },
                runStates);
            Assert.AreEqual(8, runStates.Length);

            var stepStates = (StepState[])Enum.GetValues(typeof(StepState));
            Assert.AreEqual(
                new[]
                {
                    StepState.Pending, StepState.Running, StepState.Waiting,
                    StepState.Succeeded, StepState.Failed, StepState.Cancelled,
                    StepState.Skipped, StepState.NeedsReview,
                },
                stepStates);
            Assert.AreEqual(8, stepStates.Length);
        }

        // SPEC §F2 table: exactly the listed record types, in table order, each
        // with the table required flag and durability class.
        [Test]
        public void RecordTypeTableMatchesSpecF2()
        {
            var expected = new (JournalRecordType Type, string Wire, bool Required, Durability D)[]
            {
                (JournalRecordType.RunAccepted, "run_accepted", true, Durability.Flushed),
                (JournalRecordType.ApprovalGranted, "approval_granted", true, Durability.Flushed),
                (JournalRecordType.ApprovalRejected, "approval_rejected", true, Durability.Flushed),
                (JournalRecordType.RunStarted, "run_started", true, Durability.Flushed),
                (JournalRecordType.RunSuspended, "run_suspended", true, Durability.Flushed),
                (JournalRecordType.RunResumed, "run_resumed", true, Durability.Flushed),
                (JournalRecordType.StepStarted, "step_started", true, Durability.Flushed),
                (JournalRecordType.StepWaiting, "step_waiting", true, Durability.Flushed),
                (JournalRecordType.StepProgress, "step_progress", false, Durability.Buffered),
                (JournalRecordType.StepSucceeded, "step_succeeded", true, Durability.Flushed),
                (JournalRecordType.StepFailed, "step_failed", true, Durability.Flushed),
                (JournalRecordType.StepCancelled, "step_cancelled", true, Durability.Flushed),
                (JournalRecordType.StepSkipped, "step_skipped", true, Durability.Flushed),
                (JournalRecordType.StepNeedsReview, "step_needs_review", true, Durability.Flushed),
                (JournalRecordType.CancelRequested, "cancel_requested", true, Durability.Flushed),
                (JournalRecordType.ReviewResolved, "review_resolved", true, Durability.Flushed),
                (JournalRecordType.ResourceAcquired, "resource_acquired", true, Durability.Flushed),
                (JournalRecordType.ResourceReleased, "resource_released", true, Durability.Flushed),
                (JournalRecordType.Log, "log", false, Durability.Buffered),
                (JournalRecordType.RunFinished, "run_finished", true, Durability.Flushed),
            };

            Assert.AreEqual(expected.Length, JournalRecordTypes.All.Count);
            for (var i = 0; i < expected.Length; i++)
            {
                var (type, wire, required, durability) = expected[i];
                var info = JournalRecordTypes.All[i];
                Assert.AreEqual(type, info.Type, $"table order differs at {i}");
                Assert.AreEqual(wire, info.WireName);
                Assert.AreEqual(required, info.Required, $"{wire} required");
                Assert.AreEqual(durability, info.Durability, $"{wire} durability");
                Assert.AreSame(info, JournalRecordTypes.Of(type));

                Assert.IsTrue(JournalRecordTypes.TryParse(wire, out var parsed));
                Assert.AreEqual(type, parsed);
            }

            // Every declared enum member has exactly one table row.
            Assert.AreEqual(
                JournalRecordTypes.All.Count,
                Enum.GetValues(typeof(JournalRecordType)).Length);
        }

        // SPEC §0.6 SG-12: the header's four members, canonical order, frozen.
        [Test]
        public void HeaderWritesTheGoldenLine()
        {
            var line = JournalCodec.WriteHeader(new JournalHeader(RunId, PlanHash));

            Assert.AreEqual(
                "{\"canonicalJsonVersion\":1,\"formatVersion\":1" +
                ",\"planHash\":\"" + PlanHash + "\",\"runId\":\"" + RunId + "\"}",
                line);

            var read = JournalCodec.ReadHeader(line);
            Assert.AreEqual(JournalLineStatus.Decoded, read.Status);
            Assert.AreEqual(new JournalHeader(RunId, PlanHash), read.Header);
            Assert.IsNull(read.Diagnostic);
        }

        // SPEC §0.6 SG-12: crc is CRC-32/ISO-HDLC (init 0xffffffff, reflected
        // polynomial 0xedb88320, final XOR 0xffffffff) over the Canonical JSON
        // UTF-8 bytes of the line without crc, 8 lowercase hex digits. The
        // standard check vector freezes the algorithm itself.
        [Test]
        public void Crc32MatchesTheIsoHdlcCheckValue()
        {
            Assert.AreEqual(0xCBF43926u, Crc32.Compute(s_utf8.GetBytes("123456789")));
            Assert.AreEqual("cbf43926", Crc32.ToLowerHex(Crc32.Compute(s_utf8.GetBytes("123456789"))));
        }

        // SG-12 frozen golden lines: run-level and step-level records, the
        // optional step/attempt members, a required:false record. The crc
        // members are computed over the canonical bytes without crc by an
        // independent CRC-32 implementation and frozen here.
        [Test]
        public void RecordsWriteTheGoldenLines()
        {
            Assert.AreEqual(
                "{\"crc\":\"9ef57c32\",\"data\":{\"actor\":\"human\",\"approvals\":[]," +
                "\"planFileDigest\":\"" + PlanHash + "\"},\"required\":true,\"seq\":1," +
                "\"session\":\"" + Session + "\",\"ts\":\"" + Ts + "\",\"type\":\"run_accepted\"}",
                JournalCodec.WriteRecord(Record(1, JournalRecordType.RunAccepted,
                    "{\"actor\":\"human\",\"approvals\":[],\"planFileDigest\":\"" + PlanHash + "\"}")));

            Assert.AreEqual(
                "{\"attempt\":1,\"crc\":\"72b4d2d0\",\"data\":{\"inputsDigest\":\"" +
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"," +
                "\"snapshot\":{\"path\":\"Assets/M.mat\",\"pathExisted\":false}}," +
                "\"required\":true,\"seq\":2,\"session\":\"" + Session + "\",\"step\":0," +
                "\"ts\":\"" + Ts + "\",\"type\":\"step_started\"}",
                JournalCodec.WriteRecord(Record(2, JournalRecordType.StepStarted,
                    "{\"inputsDigest\":\"" +
                    "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"," +
                    "\"snapshot\":{\"path\":\"Assets/M.mat\",\"pathExisted\":false}}",
                    step: 0, attempt: 1)));

            Assert.AreEqual(
                "{\"attempt\":1,\"crc\":\"465bedc0\",\"data\":{\"message\":\"half\",\"percent\":50}," +
                "\"required\":false,\"seq\":3,\"session\":\"" + Session + "\",\"step\":0," +
                "\"ts\":\"" + Ts + "\",\"type\":\"step_progress\"}",
                JournalCodec.WriteRecord(Record(3, JournalRecordType.StepProgress,
                    "{\"message\":\"half\",\"percent\":50}", step: 0, attempt: 1)));

            Assert.AreEqual(
                "{\"attempt\":1,\"crc\":\"35fc1b06\",\"data\":{\"changes\":[{\"kind\":\"asset\"," +
                "\"path\":\"Assets/M.mat\"}],\"outputs\":{\"path\":\"Assets/M.mat\"},\"port\":\"next\"," +
                "\"reconciled\":false},\"required\":true,\"seq\":4,\"session\":\"" + Session +
                "\",\"step\":0,\"ts\":\"" + Ts + "\",\"type\":\"step_succeeded\"}",
                JournalCodec.WriteRecord(Record(4, JournalRecordType.StepSucceeded,
                    "{\"changes\":[{\"kind\":\"asset\",\"path\":\"Assets/M.mat\"}]," +
                    "\"outputs\":{\"path\":\"Assets/M.mat\"},\"port\":\"next\",\"reconciled\":false}",
                    step: 0, attempt: 1)));

            Assert.AreEqual(
                "{\"crc\":\"9b57c70b\",\"data\":{\"level\":\"info\",\"message\":\"material created\"}," +
                "\"required\":false,\"seq\":5,\"session\":\"" + Session + "\",\"ts\":\"" + Ts +
                "\",\"type\":\"log\"}",
                JournalCodec.WriteRecord(Record(5, JournalRecordType.Log,
                    "{\"level\":\"info\",\"message\":\"material created\"}")));

            Assert.AreEqual(
                "{\"crc\":\"7f97f92b\",\"data\":{\"outputs\":{\"result\":\"Assets/M.mat\"}," +
                "\"state\":\"succeeded\",\"summary\":{\"steps\":1}},\"required\":true,\"seq\":6," +
                "\"session\":\"" + Session + "\",\"ts\":\"" + Ts + "\",\"type\":\"run_finished\"}",
                JournalCodec.WriteRecord(Record(6, JournalRecordType.RunFinished,
                    "{\"outputs\":{\"result\":\"Assets/M.mat\"},\"state\":\"succeeded\"," +
                    "\"summary\":{\"steps\":1}}")));
        }

        // SPEC §F2 test 1: every record type writes and reads back, and each
        // line's required flag is the table value.
        [Test]
        public void EveryRecordTypeRoundTrips()
        {
            foreach (var info in JournalRecordTypes.All)
            {
                var isStep = info.WireName.StartsWith("step_", StringComparison.Ordinal);
                var record = new JournalRecord(
                    1, Ts, Session, info.Type,
                    isStep ? 0 : (int?)null,
                    isStep ? 1 : (int?)null,
                    Data("{\"fixture\":\"" + info.WireName + "\",\"n\":42,\"flag\":true}"));

                var line = JournalCodec.WriteRecord(record);
                var read = JournalCodec.ReadRecord(line, expectedSeq: 1);

                Assert.AreEqual(JournalLineStatus.Decoded, read.Status, line);
                Assert.IsNull(read.Diagnostic);
                Assert.AreEqual(record, read.Record, $"round-trip changed {info.WireName}");
                Assert.AreEqual(info.Required, read.Record!.Required);
                Assert.AreEqual(info.Durability, read.Record!.Durability);

                // The written required member is the SPEC §F2 table value.
                var envelope = (JObject)Data(line).ToToken();
                Assert.AreEqual(info.Required, (bool)envelope["required"]!);
                Assert.AreEqual(info.WireName, (string)envelope["type"]!);
            }
        }

        // The crc is computed over Canonical JSON bytes, not the raw line: a
        // line whose members are written in a different order still verifies
        // when its crc covers the canonical form.
        [Test]
        public void CrcCoversCanonicalBytesNotRawLineBytes()
        {
            const string reordered =
                "{\"type\":\"run_finished\",\"seq\":6,\"crc\":\"\",\"required\":true," +
                "\"ts\":\"" + Ts + "\",\"session\":\"" + Session + "\"," +
                "\"data\":{\"state\":\"succeeded\",\"summary\":{\"steps\":1}," +
                "\"outputs\":{\"result\":\"Assets/M.mat\"}}}";
            var canonical = (JObject)Data(reordered).ToToken();
            canonical.Remove("crc");
            var crc = Crc32.ToLowerHex(
                Crc32.Compute(s_utf8.GetBytes(CanonicalJson.Serialize(canonical))));
            var line = reordered.Replace("\"crc\":\"\"", "\"crc\":\"" + crc + "\"");
            StringAssert.Contains("\"type\":\"run_finished\",\"seq\":6", line);

            var read = JournalCodec.ReadRecord(line, expectedSeq: 6);
            Assert.AreEqual(JournalLineStatus.Decoded, read.Status);
            Assert.AreEqual(JournalRecordType.RunFinished, read.Record!.Type);
        }

        // Golden lines decode back to the records that produced them.
        [Test]
        public void GoldenLinesDecodeBack()
        {
            var read = JournalCodec.ReadRecord(
                "{\"attempt\":1,\"crc\":\"465bedc0\",\"data\":{\"message\":\"half\",\"percent\":50}," +
                "\"required\":false,\"seq\":3,\"session\":\"" + Session + "\",\"step\":0," +
                "\"ts\":\"" + Ts + "\",\"type\":\"step_progress\"}",
                expectedSeq: 3);
            Assert.AreEqual(JournalLineStatus.Decoded, read.Status);
            Assert.AreEqual(
                Record(3, JournalRecordType.StepProgress,
                    "{\"message\":\"half\",\"percent\":50}", step: 0, attempt: 1),
                read.Record);
            Assert.IsFalse(read.Record!.Required);
            Assert.AreEqual(Durability.Buffered, read.Record!.Durability);
        }

        // SPEC §F3 item 3 codec part: a line whose bytes were modified fails the
        // checksum — the same envelope with one data change and the old crc.
        [Test]
        public void ModifiedLineFailsTheChecksum()
        {
            var valid = JournalCodec.WriteRecord(Record(2, JournalRecordType.StepStarted,
                "{\"snapshot\":{\"path\":\"Assets/M.mat\"}}", step: 0, attempt: 1));
            var tampered = valid.Replace("Assets/M.mat", "Assets/X.mat");
            Assert.AreNotEqual(valid, tampered);

            var read = JournalCodec.ReadRecord(tampered, expectedSeq: 2);
            Assert.AreEqual(JournalLineStatus.Corrupt, read.Status);
            Assert.IsNull(read.Record);
            Assert.IsNotNull(read.Diagnostic);
        }

        // Boundary guards on the write side: values outside the v1 persisted
        // contract are programming errors, not silently emitted bytes
        // (InputSourceJson convention).
        [Test]
        public void WriteRejectsValuesOutsideTheV1Contract()
        {
            Assert.Throws<ArgumentNullException>(() => JournalCodec.WriteRecord(null!));
            Assert.Throws<ArgumentNullException>(() => JournalCodec.WriteHeader(null!));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteHeader(new JournalHeader("", PlanHash)));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteHeader(new JournalHeader(RunId, null!)));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(Record(0, JournalRecordType.Log, "{}")));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(Record(-3, JournalRecordType.Log, "{}")));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(Record(1, JournalRecordType.Log, "{}", step: -1)));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(Record(1, JournalRecordType.Log, "{}", attempt: -1)));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(new JournalRecord(
                    1, Ts, Session, JournalRecordType.Log, null, null, Data("{}"), Frame: 2)));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(new JournalRecord(
                    1, Ts, Session, JournalRecordType.Log, null, null, Data("{}"), Iteration: 1)));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(new JournalRecord(
                    1, "", Session, JournalRecordType.Log, null, null, Data("{}"))));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(new JournalRecord(
                    1, Ts, "", JournalRecordType.Log, null, null, Data("{}"))));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(new JournalRecord(
                    1, Ts, Session, JournalRecordType.Log, null, null, Data("[]"))));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(new JournalRecord(
                    1, Ts, Session, JournalRecordType.Log, null, null, Data("null"))));
            Assert.Throws<ArgumentNullException>(
                () => JournalCodec.WriteRecord(new JournalRecord(
                    1, Ts, Session, JournalRecordType.Log, null, null, null!)));
            Assert.Throws<ArgumentException>(
                () => JournalCodec.WriteRecord(new JournalRecord(
                    1, Ts, Session, (JournalRecordType)99, null, null, Data("{}"))));
            Assert.Throws<ArgumentNullException>(() => JournalCodec.ReadHeader(null!));
            Assert.Throws<ArgumentNullException>(() => JournalCodec.ReadRecord(null!, 1));
        }

        // SPEC §F6 log payload read (T-W3.5-01): the record's level/code/message
        // surface as the raw wire strings the Runs-tab log rows show — the
        // "debug" word the §0.6 Severity set has no member for included — with
        // the envelope's ts and step supplied by the caller.
        [Test]
        public void LogRecordReadsItsWireMembers()
        {
            var record = Record(3, JournalRecordType.Log,
                "{\"level\":\"warning\",\"code\":\"OW-RUN-006\"" +
                ",\"message\":\"step failed\"}", step: 1);

            Assert.IsTrue(
                record.TryReadLog(out var level, out var code, out var message));
            Assert.AreEqual("warning", level);
            Assert.AreEqual("OW-RUN-006", code);
            Assert.AreEqual("step failed", message);
        }

        // A payload without the optional code member reads a null code, and a
        // level outside the Severity set — "debug", journaled while the
        // debugLogging policy permits — still reads, raw.
        [Test]
        public void LogRecordReadsCodeAbsentAndDebugLevel()
        {
            var record = Record(3, JournalRecordType.Log,
                "{\"level\":\"debug\",\"message\":\"poll\"}");

            Assert.IsTrue(
                record.TryReadLog(out var level, out var code, out var message));
            Assert.AreEqual("debug", level);
            Assert.IsNull(code);
            Assert.AreEqual("poll", message);
        }

        // A log record whose payload lacks the required string members yields
        // no entry — the reader reports "unreadable" instead of a partial row.
        [TestCase("{\"message\":\"m\"}", TestName = "no level")]
        [TestCase("{\"level\":\"info\"}", TestName = "no message")]
        [TestCase("{\"level\":1,\"message\":\"m\"}", TestName = "non-string level")]
        [TestCase("{}", TestName = "empty payload")]
        public void LogRecordWithoutMembersReadsFalse(string dataJson)
        {
            var record = Record(3, JournalRecordType.Log, dataJson);
            Assert.IsFalse(record.TryReadLog(out _, out _, out _));
        }

        // A non-log record never reads a log payload.
        [Test]
        public void NonLogRecordReadsFalse()
        {
            var record = Record(3, JournalRecordType.StepStarted, "{}", step: 1);
            Assert.IsFalse(record.TryReadLog(out _, out _, out _));
        }
    }
}
