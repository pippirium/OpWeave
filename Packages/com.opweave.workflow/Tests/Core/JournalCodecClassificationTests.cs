#nullable enable

using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] reader-classification tests for the journal codec
    /// (local:T-W1.7-01:journal-codec): the SPEC §F2 items 2-4 and §F3 item 3
    /// codec parts — unknown-type skip/NeedsReview rules, missing or non-bool
    /// <c>required</c>, seq gap/duplicate, crc mismatch, unknown/mistyped
    /// header versions and every malformed-envelope variant.
    /// </summary>
    internal sealed class JournalCodecClassificationTests
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

        // Recomputes a valid crc for a hand-built envelope so tests can produce
        // checksum-valid malformed or unknown-type lines.
        private static string WithCrc(string envelopeWithoutCrc)
        {
            var envelope = (JObject)Data(envelopeWithoutCrc).ToToken();
            var crc = Crc32.ToLowerHex(
                Crc32.Compute(s_utf8.GetBytes(CanonicalJson.Serialize(envelope))));
            envelope["crc"] = crc;
            return CanonicalJson.Serialize(envelope);
        }

        private static string Envelope(long seq, string type, bool required, string data = "{}") =>
            WithCrc(
                "{\"seq\":" + seq + ",\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"" + type + "\",\"required\":" + (required ? "true" : "false") +
                ",\"data\":" + data + "}");

        private static void AssertStatus(
            JournalLineStatus expected, JournalRecordRead read, string because = "")
        {
            Assert.AreEqual(expected, read.Status, because);
            if (expected == JournalLineStatus.Decoded || expected == JournalLineStatus.Skipped)
            {
                Assert.IsNull(read.Diagnostic);
            }
            else
            {
                Assert.IsNotNull(read.Diagnostic);
                Assert.AreEqual(string.Empty, read.Diagnostic!.Code,
                    "internal diagnostics carry no OW-* code; the Run Store maps once");
                Assert.IsFalse(ErrorCodes.IsPublished(read.Diagnostic!.Code));
                Assert.AreEqual(Severity.Error, read.Diagnostic!.Severity);
                Assert.IsFalse(string.IsNullOrEmpty(read.Diagnostic!.Message));
            }
            Assert.AreEqual(expected == JournalLineStatus.Decoded, read.Record is not null);
        }

        // SPEC §F2 test 3 codec part: an unknown type with required:false in the
        // middle of a journal is skipped and the following records still decode.
        [Test]
        public void UnknownOptionalRecordIsSkippedAndSeqContinues()
        {
            var lines = new[]
            {
                Envelope(1, "run_accepted", true, "{\"actor\":\"human\",\"planFileDigest\":\"" + PlanHash + "\"}"),
                Envelope(2, "telemetry_snapshot", false, "{\"anything\":[1,2,3]}"),
                Envelope(3, "log", false, "{\"level\":\"info\",\"message\":\"m\"}"),
            };

            var first = JournalCodec.ReadRecord(lines[0], expectedSeq: 1);
            var skipped = JournalCodec.ReadRecord(lines[1], expectedSeq: 2);
            var third = JournalCodec.ReadRecord(lines[2], expectedSeq: 3);

            AssertStatus(JournalLineStatus.Decoded, first);
            AssertStatus(JournalLineStatus.Skipped, skipped);
            AssertStatus(JournalLineStatus.Decoded, third);
            Assert.AreEqual(JournalRecordType.Log, third.Record!.Type);
        }

        // A skipped line still has a seq slot: when it is skipped, the next
        // record must continue the unbroken numbering.
        [Test]
        public void SkippedRecordStillOccupiesItsSequenceNumber()
        {
            var read = JournalCodec.ReadRecord(
                Envelope(9, "telemetry", false), expectedSeq: 9);
            AssertStatus(JournalLineStatus.Skipped, read);
        }

        // SPEC §F2 test 4 codec part: an unknown type with required:true is
        // NeedsReview, never skipped and never decoded.
        [Test]
        public void UnknownRequiredRecordIsNeedsReview()
        {
            var read = JournalCodec.ReadRecord(
                Envelope(4, "step_teleported", true, "{\"x\":1}"), expectedSeq: 4);

            AssertStatus(JournalLineStatus.NeedsReview, read);
        }

        // The header's own name is not a record type: a journal_header record
        // line is an unknown type classified by its required flag.
        [Test]
        public void HeaderTypeOnARecordLineIsUnknown()
        {
            AssertStatus(JournalLineStatus.NeedsReview,
                JournalCodec.ReadRecord(Envelope(2, "journal_header", true), expectedSeq: 2));
            AssertStatus(JournalLineStatus.Skipped,
                JournalCodec.ReadRecord(Envelope(2, "journal_header", false), expectedSeq: 2));
        }

        // Type names are the frozen snake_case wire names — case variants and
        // near-misses are unknown types.
        [TestCase("RunAccepted", true, JournalLineStatus.NeedsReview, TestName = "PascalCase required")]
        [TestCase("RunAccepted", false, JournalLineStatus.Skipped, TestName = "PascalCase optional")]
        [TestCase("runaccepted", true, JournalLineStatus.NeedsReview, TestName = "no underscore")]
        [TestCase("run-accepted", true, JournalLineStatus.NeedsReview, TestName = "kebab case")]
        [TestCase("run_accepted ", true, JournalLineStatus.NeedsReview, TestName = "trailing space")]
        [TestCase("", true, JournalLineStatus.NeedsReview, TestName = "empty type")]
        public void NonWireTypeNamesAreUnknown(
            string type, bool required, JournalLineStatus expected)
        {
            var read = JournalCodec.ReadRecord(Envelope(1, type, required), expectedSeq: 1);
            AssertStatus(expected, read);
        }

        // SPEC §F2 item 2: required missing or not a boolean is corruption.
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"data\":{}}", TestName = "required missing")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":null,\"data\":{}}", TestName = "required null")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":1,\"data\":{}}", TestName = "required number")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":\"true\",\"data\":{}}", TestName = "required string")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":{\"v\":true},\"data\":{}}", TestName = "required object")]
        public void MissingOrNonBoolRequiredIsCorrupt(string envelope)
        {
            var read = JournalCodec.ReadRecord(WithCrc(envelope), expectedSeq: 1);
            AssertStatus(JournalLineStatus.Corrupt, read);
        }

        // Writer forbids empty ts/session; the reader must not decode a line
        // that cannot be written back. Recompute the CRC after replacing the
        // field to ensure failure is due to envelope validation, not checksum.
        [TestCase("ts", "log", false)]
        [TestCase("session", "log", false)]
        [TestCase("ts", "future_optional", false)]
        [TestCase("session", "future_optional", false)]
        [TestCase("ts", "future_required", true)]
        [TestCase("session", "future_required", true)]
        public void EmptyTimestampOrSessionIsCorruptWithValidCrc(
            string member, string type, bool required)
        {
            var envelope = (JObject)Data(Envelope(1, type, required)).ToToken();
            envelope[member] = string.Empty;
            envelope.Remove("crc");
            var line = WithCrc(CanonicalJson.Serialize(envelope));
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(line, expectedSeq: 1),
                member + " in type " + type);
        }

        // seq must start at 1 and increase by exactly 1: gaps and duplicates are
        // corruption (SPEC §F2 seq rule; F0 mid-journal corruption).
        [Test]
        public void SeqGapOrDuplicateIsCorrupt()
        {
            // First record must be seq 1.
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(Envelope(2, "log", false), expectedSeq: 1),
                "first record seq 2 is a gap");
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(Envelope(3, "log", false), expectedSeq: 2),
                "seq 3 after 1 leaves a gap");
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(Envelope(2, "log", false), expectedSeq: 3),
                "repeated seq 2 is a duplicate");
            AssertStatus(JournalLineStatus.Decoded,
                JournalCodec.ReadRecord(Envelope(1, "log", false), expectedSeq: 1));
            AssertStatus(JournalLineStatus.Decoded,
                JournalCodec.ReadRecord(Envelope(2, "log", false), expectedSeq: 2));
        }

        // Envelope member violations are corruption: missing or mistyped
        // members and members outside the SPEC §F2 set.
        [TestCase("{\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "seq missing")]
        [TestCase("{\"seq\":\"1\",\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "seq string")]
        [TestCase("{\"seq\":1.5,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "seq fractional")]
        [TestCase("{\"seq\":0,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "seq zero")]
        [TestCase("{\"seq\":-1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "seq negative")]
        [TestCase("{\"seq\":1,\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "ts missing")]
        [TestCase("{\"seq\":1,\"ts\":123,\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "ts not a string")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts +
                  "\",\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "session missing")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":7," +
                  "\"type\":\"log\",\"required\":false,\"data\":{}}", TestName = "session not a string")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"required\":false,\"data\":{}}", TestName = "type missing")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":4,\"required\":false,\"data\":{}}", TestName = "type not a string")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false}", TestName = "data missing")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":null}", TestName = "data null")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":[1]}", TestName = "data array")]
        [TestCase("{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                  "\",\"type\":\"log\",\"required\":false,\"data\":\"x\"}", TestName = "data string")]
        public void MissingOrMistypedMemberIsCorrupt(string envelopeJson)
        {
            // A valid crc keeps the tested failure at the member level.
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(WithCrc(envelopeJson), expectedSeq: 1));
        }

        // The reserved future members stay valid schema when present as
        // non-negative integers; anything else is corruption.
        [Test]
        public void ReservedIndexMembersDecodeButMistypedOnesAreCorrupt()
        {
            var withReserved = WithCrc(
                "{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"log\",\"required\":false,\"step\":0,\"attempt\":1," +
                "\"frame\":0,\"iteration\":0,\"data\":{}}");
            var decoded = JournalCodec.ReadRecord(withReserved, expectedSeq: 1);
            AssertStatus(JournalLineStatus.Decoded, decoded);
            Assert.AreEqual(0, decoded.Record!.Frame);
            Assert.AreEqual(0, decoded.Record!.Iteration);
            Assert.AreEqual(0, decoded.Record!.Step);
            Assert.AreEqual(1, decoded.Record!.Attempt);

            foreach (var member in new[] { "step", "attempt", "frame", "iteration" })
            {
                foreach (var bad in new[] { "-1", "1.5", "\"0\"", "null", "2147483648" })
                {
                    var line = WithCrc(
                        "{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                        "\",\"type\":\"log\",\"required\":false,\"data\":{},\"" +
                        member + "\":" + bad + "}");
                    AssertStatus(JournalLineStatus.Corrupt,
                        JournalCodec.ReadRecord(line, expectedSeq: 1),
                        member + " = " + bad);
                }
            }
        }

        // ── Reserved frame/iteration (local:T-W1.7-07:reserved-frame-iteration) ─
        //
        // SPEC §F2 / §N.1: frame and iteration are reserved for Appendix N and
        // v1 never writes them (v1 values are 0). A nonzero value can only come
        // from a future format whose per-frame/per-iteration meaning v1 cannot
        // interpret, so a checksum-valid line carrying one is NeedsReview —
        // never Decoded, never Skipped (SPEC §F0 item 1, §F2 item 2, §N.3 item
        // 5). Corrupt is not used: §F3 item 3 lets the loader drop a corrupt
        // last line as a torn write, which would silently discard the record.

        // Every nonzero reserved member on a checksum-valid known-type line is
        // NeedsReview, whether the type's table required flag is true or false.
        [TestCase("\"frame\":1", "run_started", true, TestName = "frame 1, required type")]
        [TestCase("\"iteration\":1", "run_started", true, TestName = "iteration 1, required type")]
        [TestCase("\"frame\":1,\"iteration\":2", "run_started", true, TestName = "both, required type")]
        [TestCase("\"frame\":1", "log", false, TestName = "frame 1, optional type")]
        [TestCase("\"iteration\":1", "log", false, TestName = "iteration 1, optional type")]
        [TestCase("\"frame\":2,\"iteration\":1", "log", false, TestName = "both, optional type")]
        public void NonzeroReservedMemberIsNeedsReview(
            string reservedJson, string type, bool required)
        {
            var line = WithCrc(
                "{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"" + type + "\",\"required\":" + (required ? "true" : "false") +
                "," + reservedJson + ",\"data\":{}}");
            AssertStatus(JournalLineStatus.NeedsReview,
                JournalCodec.ReadRecord(line, expectedSeq: 1));
        }

        // The classification is identical mid-journal and on the final line —
        // NeedsReview is not Corrupt, so §F3 item 3 never treats the record as
        // a torn write. Each nonzero reserved member is checked as a middle
        // line (the following record still decodes on its own merits because
        // the codec classifies each line independently) and as the journal's
        // last line, for a required-table type and an optional one.
        [TestCase("\"frame\":1", "log", false, false, TestName = "frame, optional, middle")]
        [TestCase("\"frame\":1", "run_finished", true, false, TestName = "frame, required, middle")]
        [TestCase("\"frame\":1", "log", false, true, TestName = "frame, optional, final")]
        [TestCase("\"frame\":1", "run_finished", true, true, TestName = "frame, required, final")]
        [TestCase("\"iteration\":1", "log", false, false, TestName = "iteration, optional, middle")]
        [TestCase("\"iteration\":1", "run_finished", true, false, TestName = "iteration, required, middle")]
        [TestCase("\"iteration\":1", "log", false, true, TestName = "iteration, optional, final")]
        [TestCase("\"iteration\":1", "run_finished", true, true, TestName = "iteration, required, final")]
        [TestCase("\"frame\":1,\"iteration\":2", "log", false, false, TestName = "both, optional, middle")]
        [TestCase("\"frame\":1,\"iteration\":2", "run_finished", true, false, TestName = "both, required, middle")]
        [TestCase("\"frame\":1,\"iteration\":2", "log", false, true, TestName = "both, optional, final")]
        [TestCase("\"frame\":1,\"iteration\":2", "run_finished", true, true, TestName = "both, required, final")]
        public void NonzeroReservedMemberOnMiddleAndFinalLinesIsNeedsReview(
            string reservedJson, string type, bool required, bool finalLine)
        {
            // The fixture line sits at seq 2 as a middle line or at seq 3 as
            // the journal's last line; every other line decodes normally.
            var line = WithCrc(
                "{\"seq\":" + (finalLine ? 3 : 2) + ",\"ts\":\"" + Ts +
                "\",\"session\":\"" + Session + "\",\"type\":\"" + type +
                "\",\"required\":" + (required ? "true" : "false") + "," +
                reservedJson + ",\"data\":{}}");

            AssertStatus(JournalLineStatus.Decoded,
                JournalCodec.ReadRecord(Envelope(1, "run_started", true), expectedSeq: 1));
            if (finalLine)
            {
                AssertStatus(JournalLineStatus.Decoded,
                    JournalCodec.ReadRecord(Envelope(2, "log", false), expectedSeq: 2));
                AssertStatus(JournalLineStatus.NeedsReview,
                    JournalCodec.ReadRecord(line, expectedSeq: 3),
                    "final line: " + reservedJson);
            }
            else
            {
                AssertStatus(JournalLineStatus.NeedsReview,
                    JournalCodec.ReadRecord(line, expectedSeq: 2),
                    "middle line: " + reservedJson);
                AssertStatus(JournalLineStatus.Decoded,
                    JournalCodec.ReadRecord(Envelope(3, "log", false), expectedSeq: 3));
            }
        }

        // The crc check precedes the reserved-field check: a nonzero member on
        // a line whose checksum no longer matches stays Corrupt — as a middle
        // line and as the journal's final line. Corrupt on the last line is
        // the §F3 item 3 torn-write case the loader may drop, which is exactly
        // why a checksum-valid nonzero line must never classify as Corrupt.
        [Test]
        public void NonzeroReservedMemberWithWrongCrcIsCorrupt()
        {
            var valid = WithCrc(
                "{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"log\",\"required\":false,\"frame\":1,\"data\":{}}");
            var tampered = valid.Replace("\"frame\":1", "\"frame\":3");
            Assert.AreNotEqual(valid, tampered);
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(tampered, expectedSeq: 1));

            var validLast = WithCrc(
                "{\"seq\":3,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"log\",\"required\":false,\"iteration\":2,\"data\":{}}");
            var tamperedLast = validLast.Replace("\"iteration\":2", "\"iteration\":5");
            Assert.AreNotEqual(validLast, tamperedLast);
            AssertStatus(JournalLineStatus.Decoded,
                JournalCodec.ReadRecord(Envelope(1, "run_started", true), expectedSeq: 1));
            AssertStatus(JournalLineStatus.Decoded,
                JournalCodec.ReadRecord(Envelope(2, "log", false), expectedSeq: 2));
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(tamperedLast, expectedSeq: 3));
        }

        // Even an otherwise-skippable unknown optional record is NeedsReview
        // when it carries a nonzero reserved member: the value marks a record
        // written under format semantics v1 cannot interpret.
        [Test]
        public void UnknownOptionalTypeWithNonzeroReservedMemberIsNeedsReview()
        {
            var line = WithCrc(
                "{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"telemetry\",\"required\":false,\"iteration\":1,\"data\":{}}");
            AssertStatus(JournalLineStatus.NeedsReview,
                JournalCodec.ReadRecord(line, expectedSeq: 1));
        }

        // SPEC §N.5.5 (5) reserves sourcePath but it is not in the v1 envelope:
        // a line carrying it — like any other unknown member — is corruption.
        [TestCase("sourcePath", TestName = "reserved sourcePath member")]
        [TestCase("extra", TestName = "unknown member")]
        [TestCase("metadata", TestName = "metadata member")]
        public void MemberOutsideTheV1EnvelopeIsCorrupt(string member)
        {
            var line = WithCrc(
                "{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"log\",\"required\":false,\"data\":{},\"" + member + "\":{}}");
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(line, expectedSeq: 1));
        }

        // crc validation: missing, mistyped and wrong-value crc are corruption;
        // even a skipped-type line must pass the checksum first.
        [Test]
        public void CrcMissingMistypedOrWrongIsCorrupt()
        {
            var baseEnvelope =
                "{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"log\",\"required\":false,\"data\":{}}";

            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(baseEnvelope, expectedSeq: 1), "crc missing");

            var nonStringCrc = baseEnvelope.Insert(
                baseEnvelope.Length - 1, ",\"crc\":123");
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(nonStringCrc, expectedSeq: 1),
                "crc not a string");

            var actual = Crc32.ToLowerHex(Crc32.Compute(
                s_utf8.GetBytes(CanonicalJson.Serialize(Data(baseEnvelope).ToToken()))));
            var wrongCrc = actual == "00000000" ? "ffffffff" : "00000000";
            var wrong = WithCrc(baseEnvelope).Replace(
                "\"crc\":\"" + actual + "\"", "\"crc\":\"" + wrongCrc + "\"");
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(wrong, expectedSeq: 1), "wrong crc value");

            // An unknown-type optional line is skipped only after envelope
            // integrity — including crc — is verified.
            var unknownBadCrc =
                "{\"seq\":1,\"ts\":\"" + Ts + "\",\"session\":\"" + Session +
                "\",\"type\":\"telemetry\",\"required\":false,\"data\":{},\"crc\":\"deadbeef\"}";
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(unknownBadCrc, expectedSeq: 1),
                "unknown optional type with bad crc");
        }

        // A known type carrying a required flag different from the SPEC §F2
        // table cannot come from a v1 writer: corruption.
        [Test]
        public void RequiredFlagDifferingFromTheTableIsCorrupt()
        {
            // "log" is required:false in the table; "run_started" is true.
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(Envelope(1, "log", true), expectedSeq: 1));
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(Envelope(1, "run_started", false), expectedSeq: 1));
        }

        // Unparseable and non-object lines are corruption, including on the
        // journal's first record line.
        [TestCase("{not json", TestName = "syntax error")]
        [TestCase("", TestName = "empty line")]
        [TestCase("[1,2]", TestName = "array line")]
        [TestCase("42", TestName = "number line")]
        [TestCase("\"text\"", TestName = "string line")]
        [TestCase("null", TestName = "null line")]
        [TestCase("true", TestName = "bool line")]
        public void MalformedOrNonObjectLineIsCorrupt(string line)
        {
            AssertStatus(JournalLineStatus.Corrupt,
                JournalCodec.ReadRecord(line, expectedSeq: 1));
        }

        // ── Header classification (SPEC §F2 item 4) ──────────────────────────

        private static string Header(string json) => json.Replace("$RUNID", RunId).Replace("$HASH", PlanHash);

        // A valid header with a reordered member set still decodes — Canonical
        // JSON normalizes member order for crc-free header lines too.
        [Test]
        public void ValidHeaderDecodesRegardlessOfMemberOrder()
        {
            var read = JournalCodec.ReadHeader(
                Header("{\"runId\":\"$RUNID\",\"formatVersion\":1,\"planHash\":\"$HASH\"," +
                       "\"canonicalJsonVersion\":1}"));
            Assert.AreEqual(JournalLineStatus.Decoded, read.Status);
            Assert.AreEqual(new JournalHeader(RunId, PlanHash), read.Header);
        }

        // SPEC §F2 item 4: unknown, missing or mistyped formatVersion /
        // canonicalJsonVersion is NeedsReview — never interpreted or migrated.
        [TestCase("{\"formatVersion\":2,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "formatVersion 2")]
        [TestCase("{\"formatVersion\":0,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "formatVersion 0")]
        [TestCase("{\"formatVersion\":\"1\",\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "formatVersion string")]
        [TestCase("{\"formatVersion\":1.0,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "formatVersion double")]
        [TestCase("{\"formatVersion\":null,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "formatVersion null")]
        [TestCase("{\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "formatVersion missing")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":2,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "canonicalJsonVersion 2")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":0,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "canonicalJsonVersion 0")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":\"1\",\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "canonicalJsonVersion string")]
        [TestCase("{\"formatVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\"}",
                  TestName = "canonicalJsonVersion missing")]
        public void UnknownMissingOrMistypedHeaderVersionIsNeedsReview(string json)
        {
            var read = JournalCodec.ReadHeader(Header(json));
            Assert.AreEqual(JournalLineStatus.NeedsReview, read.Status);
            Assert.IsNull(read.Header);
            Assert.IsNotNull(read.Diagnostic);
            Assert.AreEqual(string.Empty, read.Diagnostic!.Code);
            Assert.IsFalse(ErrorCodes.IsPublished(read.Diagnostic!.Code));
        }

        // Missing/blank identity members and members outside the v1 four are
        // NeedsReview: the header cannot establish what it guards.
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":1,\"planHash\":\"$HASH\"}",
                  TestName = "runId missing")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":1,\"runId\":7,\"planHash\":\"$HASH\"}",
                  TestName = "runId not a string")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\"}",
                  TestName = "planHash missing")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":null}",
                  TestName = "planHash null")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\",\"type\":\"journal_header\"}",
                  TestName = "header with type member")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\",\"seq\":1}",
                  TestName = "header with seq member")]
        [TestCase("{\"formatVersion\":1,\"canonicalJsonVersion\":1,\"runId\":\"$RUNID\",\"planHash\":\"$HASH\",\"extra\":{}}",
                  TestName = "header with extra member")]
        public void IncompleteOrExtendedHeaderIsNeedsReview(string json)
        {
            var read = JournalCodec.ReadHeader(Header(json));
            Assert.AreEqual(JournalLineStatus.NeedsReview, read.Status);
            Assert.IsNull(read.Header);
        }

        // Only a JSON text failure on line 1 is corruption; a parseable
        // non-object header is NeedsReview (no versions to trust).
        [Test]
        public void HeaderSyntaxFailureIsCorruptNonObjectIsNeedsReview()
        {
            Assert.AreEqual(JournalLineStatus.Corrupt,
                JournalCodec.ReadHeader("{not json").Status);
            Assert.AreEqual(JournalLineStatus.Corrupt,
                JournalCodec.ReadHeader("").Status);
            Assert.AreEqual(JournalLineStatus.NeedsReview,
                JournalCodec.ReadHeader("[1,2]").Status);
            Assert.AreEqual(JournalLineStatus.NeedsReview,
                JournalCodec.ReadHeader("null").Status);
            Assert.AreEqual(JournalLineStatus.NeedsReview,
                JournalCodec.ReadHeader("42").Status);
        }
    }
}
