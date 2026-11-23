#nullable enable
// T-W3.2-01 [Edit] tests: SPEC §H1 (transport-independent service, actor fixed
// to Agent, workflow given as body or project path) and SPEC §H3 (envelope
// shape, 64 KiB cap, opaque resumable cursor). Envelope/truncation/cursor
// fixtures call AgentApiService directly — no Pipeline (SPEC §H1 test rule).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Persistence;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class AgentApiEnvelopeTests
    {
        private string _dir = null!;
        private FileRunStore _store = null!;
        private AgentApiService _api = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(
                Path.GetTempPath(), "OpWeaveAgentApiTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _store = new FileRunStore(_dir, "session-" + Guid.NewGuid().ToString("N"));
            _api = new AgentApiService(
                _store,
                new AgentApiCursorCodec(new byte[32]),
                static (runId, reason) => throw new System.InvalidOperationException(
                    "The envelope fixtures carry no cancel seam."));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        // ── Envelope shape (SPEC §H3 item 1) ────────────────────────────────

        [Test]
        public void SuccessEnvelope_CarriesExactlyOkDataErrorsWarnings()
        {
            var response = _api.Ok(new JObject { ["answer"] = 42 });
            var wire = response.ToWire();

            Assert.That(wire.Properties().Select(p => p.Name).ToArray(),
                Is.EquivalentTo(new[] { "ok", "data", "errors", "warnings" }));
            Assert.That((bool?)wire["ok"], Is.True);
            Assert.That((int?)wire["data"]!["answer"]!, Is.EqualTo(42));
            Assert.That(wire["errors"]!, Has.Count.EqualTo(0));
            Assert.That(wire["warnings"]!, Has.Count.EqualTo(0));
        }

        [Test]
        public void FailureEnvelope_CarriesNullDataAndDiagnostics()
        {
            var diagnostic = ErrorCodes.Create(
                ErrorCodes.OW_API_001, "test failure", jsonPath: "$.x");
            var warning = ErrorCodes.Create(
                ErrorCodes.OW_CFG_003, "test warning", severity: Severity.Warning);

            var wire = _api.Failure(new[] { diagnostic }, new[] { warning }).ToWire();

            Assert.That(wire.Properties().Select(p => p.Name).ToArray(),
                Is.EquivalentTo(new[] { "ok", "data", "errors", "warnings" }));
            Assert.That((bool?)wire["ok"], Is.False);
            Assert.That(wire["data"]!.Type, Is.EqualTo(JTokenType.Null));
            var error = (JObject)wire["errors"]![0]!;
            Assert.That((string?)error["code"], Is.EqualTo("OW-API-001"));
            Assert.That((string?)error["jsonPath"], Is.EqualTo("$.x"));
            var serialized = (JObject)wire["warnings"]![0]!;
            Assert.That((string?)serialized["code"], Is.EqualTo("OW-CFG-003"));
        }

        [Test]
        public void DiagnosticWire_CamelCaseMembersSnakeCaseSeverity()
        {
            var diagnostic = new Diagnostic(
                "OW-TEST-000", Severity.Warning, "message text",
                NodeId: "n1", Port: "in", JsonPath: "$.p", Hint: "fix it", Retryable: true);
            var wire = AgentApiResponse.DiagnosticToJson(diagnostic);

            Assert.That(wire.Properties().Select(p => p.Name).ToArray(),
                Is.EquivalentTo(new[]
                {
                    "code", "severity", "message", "nodeId", "port",
                    "jsonPath", "hint", "retryable",
                }));
            Assert.That((string?)wire["severity"], Is.EqualTo("warning"));
            Assert.That((bool?)wire["retryable"], Is.True);
        }

        [Test]
        public void DiagnosticWire_OmitsNullOptionals()
        {
            var wire = AgentApiResponse.DiagnosticToJson(
                new Diagnostic("OW-TEST-000", Severity.Info, "message text"));

            Assert.That((string?)wire["severity"], Is.EqualTo("info"));
            Assert.That(wire["nodeId"], Is.Null);
            Assert.That(wire["port"], Is.Null);
            Assert.That(wire["jsonPath"], Is.Null);
            Assert.That(wire["hint"], Is.Null);
            Assert.That((bool?)wire["retryable"], Is.False);
        }

        [Test]
        public void SuccessEnvelope_ToJsonIsValidUtf8Json()
        {
            var json = _api.Ok(new JObject { ["a"] = 1 }).ToJson();
            var reparsed = JObject.Parse(json);
            Assert.That((bool?)reparsed["ok"], Is.True);
        }

        // ── 64 KiB cap and truncation (SPEC §H3 item 3) ─────────────────────

        [Test]
        public void SmallPage_ResponseWithinCapReturnedWhole()
        {
            var items = Items(10, 128);
            var response = _api.PageResponse(
                "ctx", null, items, (start, count) => Pack(items, start, count));

            AssertUtf8WithinCap(response);
            Assert.That(response.Ok, Is.True);
            Assert.That(response.Truncated, Is.False);
            Assert.That(response.NextCursor, Is.Null);
            Assert.That(response.Data!["items"]!.Count(), Is.EqualTo(10));
        }

        [Test]
        public void OversizedPage_TruncatesWithCursorAndStaysWithinCap()
        {
            var items = Items(45, 2048); // ~90 KiB of items
            var response = _api.PageResponse(
                "ctx", null, items, (start, count) => Pack(items, start, count));

            AssertUtf8WithinCap(response);
            Assert.That(response.Ok, Is.True);
            Assert.That(response.Truncated, Is.True);
            Assert.That(response.NextCursor, Is.Not.Null.And.Not.Empty);
            var wire = response.ToWire();
            Assert.That(wire.Properties().Select(p => p.Name).ToArray(),
                Is.EquivalentTo(new[]
                {
                    "ok", "data", "errors", "warnings", "nextCursor", "truncated",
                }));
            Assert.That((bool?)wire["truncated"], Is.True);
            var emitted = ((JArray)response.Data!["items"]!).Count;
            Assert.That(emitted, Is.GreaterThan(0).And.LessThan(items.Count));
        }

        [Test]
        public void TruncatedPages_CursorResumesUntilWholeListDelivered()
        {
            var items = Items(45, 2048);
            var delivered = new List<JToken>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var response = _api.PageResponse(
                    "ctx", cursor, items, (start, count) => Pack(items, start, count));
                AssertUtf8WithinCap(response);
                Assert.That(response.Ok, Is.True);
                delivered.AddRange(((JArray)response.Data!["items"]!).Cast<JObject>());
                cursor = response.NextCursor;
                pages++;
            }
            while (cursor is not null && pages < 50);

            Assert.That(delivered.Count, Is.EqualTo(items.Count));
            for (var i = 0; i < items.Count; i++)
            {
                Assert.That((int?)delivered[i]["index"], Is.EqualTo(i));
            }
        }

        [Test]
        public void LimitBelowCapBoundsPageAndCursorResumes()
        {
            var items = Items(25, 128);
            var first = _api.PageResponse(
                "ctx", null, items, (start, count) => Pack(items, start, count), limit: 10);

            Assert.That(((JArray)first.Data!["items"]!).Count, Is.EqualTo(10));
            Assert.That(first.Truncated, Is.True);

            var rest = _api.PageResponse(
                "ctx", first.NextCursor, items, (start, count) => Pack(items, start, count));
            Assert.That(((JArray)rest.Data!["items"]!).Count, Is.EqualTo(15));
            Assert.That(rest.Truncated, Is.False);
            Assert.That(rest.NextCursor, Is.Null);
        }

        [Test]
        public void NonPositiveLimit_IsMalformedRequestOwApi001()
        {
            var response = _api.PageResponse(
                "ctx", null, Items(2, 8), (start, count) => Pack(Items(2, 8), start, count),
                limit: 0);

            Assert.That(response.Ok, Is.False);
            var error = (JObject)response.ToWire()["errors"]![0]!;
            Assert.That((string?)error["code"], Is.EqualTo("OW-API-001"));
            Assert.That((string?)error["jsonPath"], Is.EqualTo("$.limit"));
        }

        // ── Cursor validity → OW-API-002 (SPEC §H3 item 3, §J) ──────────────

        [Test]
        public void TamperedCursor_ReturnsOwApi002()
        {
            var items = Items(45, 2048);
            var first = _api.PageResponse(
                "ctx", null, items, (start, count) => Pack(items, start, count));
            var cursor = first.NextCursor!;
            var tampered = cursor.Substring(0, 4) +
                (cursor[4] == 'a' ? 'b' : 'a') + cursor.Substring(5);

            var response = _api.PageResponse(
                "ctx", tampered, items, (start, count) => Pack(items, start, count));

            Assert.That(response.Ok, Is.False);
            AssertErrorCode(response, "OW-API-002");
        }

        [TestCase("")]
        [TestCase("%%%not-base64%%%")]
        [TestCase("AAAA")]                  // base64 decodes, too short for a tag
        [TestCase("e30")]                  // "{}" — valid payload, wrong shape, bad tag
        public void MalformedCursor_ReturnsOwApi002(string cursor)
        {
            var items = Items(5, 128);
            var response = _api.PageResponse(
                "ctx", cursor, items, (start, count) => Pack(items, start, count));

            Assert.That(response.Ok, Is.False);
            AssertErrorCode(response, "OW-API-002");
        }

        [Test]
        public void CursorFromAnotherContext_ReturnsOwApi002()
        {
            var items = Items(45, 2048);
            var first = _api.PageResponse(
                "ctx-A", null, items, (start, count) => Pack(items, start, count));

            var response = _api.PageResponse(
                "ctx-B", first.NextCursor, items, (start, count) => Pack(items, start, count));

            Assert.That(response.Ok, Is.False);
            AssertErrorCode(response, "OW-API-002");
        }

        [Test]
        public void CursorBeyondShrunkList_ReturnsOwApi002()
        {
            var items = Items(45, 2048);
            var first = _api.PageResponse(
                "ctx", null, items, (start, count) => Pack(items, start, count));
            var shrunk = items.Take(2).ToList();

            var response = _api.PageResponse(
                "ctx", first.NextCursor, shrunk, (start, count) => Pack(shrunk, start, count));

            Assert.That(response.Ok, Is.False);
            AssertErrorCode(response, "OW-API-002");
        }

        // ── Oversized payloads → canonical-JSON segments (SPEC §H3 item 3) ───

        [Test]
        public void OversizedData_SegmentsReassembleCanonicalJsonWithinCap()
        {
            var payload = new JObject
            {
                ["blob"] = new string('y', 96 * 1024),
                ["tail"] = 7,
            };
            // Segments carry the canonical JSON of the whole response envelope
            // — {ok, data, errors, warnings} — not the payload alone.
            var expected = CanonicalJson.Serialize(new JObject
            {
                ["ok"] = true,
                ["data"] = payload,
                ["errors"] = new JArray(),
                ["warnings"] = new JArray(),
            });

            var segments = new List<string>();
            string? cursor = null;
            var hops = 0;
            AgentApiResponse response;
            do
            {
                response = _api.Ok(payload, context: "ctx", cursor: cursor);
                AssertUtf8WithinCap(response);
                Assert.That(response.Ok, Is.True);
                var data = response.Data!;
                Assert.That(data.Type, Is.EqualTo(JTokenType.String),
                    "an oversized payload is delivered as canonical-JSON string segments");
                segments.Add(data.Value<string>()!);
                cursor = response.NextCursor;
                hops++;
            }
            while (cursor is not null && hops < 50);

            Assert.That(hops, Is.GreaterThan(1));
            Assert.That(string.Concat(segments), Is.EqualTo(expected));
            Assert.That(response.Truncated, Is.False);
        }

        [Test]
        public void OversizedData_SegmentCursorRejectsChangedData()
        {
            var payload = new JObject { ["blob"] = new string('y', 96 * 1024) };
            var first = _api.Ok(payload, context: "ctx");
            Assert.That(first.NextCursor, Is.Not.Null.And.Not.Empty);

            var changed = new JObject
            {
                ["blob"] = new string('y', 96 * 1024),
                ["extra"] = 1,
            };
            var resumed = _api.Ok(changed, context: "ctx", cursor: first.NextCursor);

            Assert.That(resumed.Ok, Is.False);
            AssertErrorCode(resumed, "OW-API-002");
            AssertUtf8WithinCap(resumed);
        }

        [Test]
        public void OversizedData_SegmentCursorFromAnotherContext_ReturnsOwApi002()
        {
            var payload = new JObject { ["blob"] = new string('y', 96 * 1024) };
            var first = _api.Ok(payload, context: "ctx-A");

            var resumed = _api.Ok(payload, context: "ctx-B", cursor: first.NextCursor);

            Assert.That(resumed.Ok, Is.False);
            AssertErrorCode(resumed, "OW-API-002");
        }

        [Test]
        public void OversizedData_TamperedSegmentCursor_ReturnsOwApi002()
        {
            var payload = new JObject { ["blob"] = new string('y', 96 * 1024) };
            var first = _api.Ok(payload, context: "ctx");
            var cursor = first.NextCursor!;
            var tampered = cursor.Substring(0, 4) +
                (cursor[4] == 'a' ? 'b' : 'a') + cursor.Substring(5);

            var resumed = _api.Ok(payload, context: "ctx", cursor: tampered);

            Assert.That(resumed.Ok, Is.False);
            AssertErrorCode(resumed, "OW-API-002");
        }

        [Test]
        public void OversizedPageItem_SegmentsThenPagingContinues()
        {
            var items = new List<JToken>
            {
                new JObject { ["index"] = 0, ["payload"] = new string('x', 96 * 1024) },
                new JObject { ["index"] = 1, ["payload"] = "small-1" },
                new JObject { ["index"] = 2, ["payload"] = "small-2" },
            };
            // Segments carry the canonical JSON of the item's whole single-item
            // page envelope — {ok, data, errors, warnings} plus the page cursor
            // — so the reassembled text parses back into the original page.

            var segments = new List<string>();
            var delivered = new List<JToken>();
            var sawSegment = false;
            string? cursor = null;
            var pages = 0;
            do
            {
                var response = _api.PageResponse(
                    "ctx", cursor, items, (start, count) => Pack(items, start, count));
                AssertUtf8WithinCap(response);
                Assert.That(response.Ok, Is.True);
                var data = response.Data!;
                if (data.Type == JTokenType.String)
                {
                    sawSegment = true;
                    segments.Add(data.Value<string>()!);
                }
                else
                {
                    delivered.AddRange(((JArray)data["items"]!).Cast<JObject>());
                }
                cursor = response.NextCursor;
                pages++;
            }
            while (cursor is not null && pages < 50);

            Assert.That(sawSegment, Is.True);
            var reassembled = JObject.Parse(string.Concat(segments));
            Assert.That((bool?)reassembled["ok"], Is.True);
            Assert.That(reassembled["errors"]!, Has.Count.EqualTo(0));
            Assert.That(reassembled["warnings"]!, Has.Count.EqualTo(0));
            Assert.That(
                JToken.DeepEquals(reassembled["data"]!["items"]![0], items[0]),
                Is.True);
            Assert.That((string?)reassembled["nextCursor"],
                Is.Not.Null.And.Not.Empty,
                "the page-continuation cursor is part of the segmented envelope");
            Assert.That(delivered.Select(t => (int?)t["index"]).ToArray(),
                Is.EqualTo(new int?[] { 1, 2 }));
        }

        [Test]
        public void OversizedPageItem_SegmentCursorRejectsChangedItem()
        {
            var items = Items(1, 96 * 1024);
            var first = _api.PageResponse(
                "ctx", null, items, (start, count) => Pack(items, start, count));
            Assert.That(first.Data!.Type, Is.EqualTo(JTokenType.String));
            Assert.That(first.NextCursor, Is.Not.Null.And.Not.Empty);

            var changed = Items(1, 96 * 1024);
            ((JObject)changed[0])["marker"] = "mutated";
            var resumed = _api.PageResponse(
                "ctx", first.NextCursor, changed,
                (start, count) => Pack(changed, start, count));

            Assert.That(resumed.Ok, Is.False);
            AssertErrorCode(resumed, "OW-API-002");
        }

        [Test]
        public void OversizedPageItem_TamperedSegmentCursor_ReturnsOwApi002()
        {
            var items = Items(1, 96 * 1024);
            var first = _api.PageResponse(
                "ctx", null, items, (start, count) => Pack(items, start, count));
            var cursor = first.NextCursor!;
            var tampered = cursor.Substring(0, 4) +
                (cursor[4] == 'a' ? 'b' : 'a') + cursor.Substring(5);

            var resumed = _api.PageResponse(
                "ctx", tampered, items, (start, count) => Pack(items, start, count));

            Assert.That(resumed.Ok, Is.False);
            AssertErrorCode(resumed, "OW-API-002");
        }

        [Test]
        public void OversizedFailure_SegmentsReassembleErrorsAndKeepOkFalse()
        {
            // A business failure carrying more than 64 KiB of diagnostics is
            // segmented like any other envelope — never replaced by OW-API-004
            // (SPEC §H3 item 3).
            var errors = new List<Diagnostic>();
            for (var i = 0; i < 300; i++)
            {
                errors.Add(ErrorCodes.Create(
                    ErrorCodes.OW_API_001,
                    "failure " + i + ": " + new string('e', 180)));
            }
            var expectedErrors = new JArray();
            foreach (var diagnostic in errors)
            {
                expectedErrors.Add(AgentApiResponse.DiagnosticToJson(diagnostic));
            }

            var segments = new List<string>();
            string? cursor = null;
            var hops = 0;
            AgentApiResponse response;
            do
            {
                response = _api.Failure(errors, context: "ctx", cursor: cursor);
                AssertUtf8WithinCap(response);
                Assert.That(response.Ok, Is.False,
                    "every segment preserves the original ok:false");
                Assert.That(response.Errors, Is.Empty,
                    "the real diagnostics ride inside the segmented content");
                Assert.That(response.Warnings, Is.Empty);
                var data = response.Data!;
                Assert.That(data.Type, Is.EqualTo(JTokenType.String));
                segments.Add(data.Value<string>()!);
                cursor = response.NextCursor;
                hops++;
            }
            while (cursor is not null && hops < 50);

            Assert.That(hops, Is.GreaterThan(1));
            var reassembled = JObject.Parse(string.Concat(segments));
            Assert.That((bool?)reassembled["ok"], Is.False);
            Assert.That(reassembled["data"]!.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(JToken.DeepEquals(reassembled["errors"], expectedErrors),
                Is.True);
            Assert.That(reassembled["warnings"]!, Has.Count.EqualTo(0));
        }

        [Test]
        public void OversizedWarnings_SegmentsReassembleWarningsAndData()
        {
            // Warnings too large for one envelope must not block a success:
            // they ride inside the segmented {ok, data, errors, warnings}
            // content instead of starving the segment budget (SPEC §H3 item 3).
            var warnings = new List<Diagnostic>();
            for (var i = 0; i < 300; i++)
            {
                warnings.Add(ErrorCodes.Create(
                    ErrorCodes.OW_CFG_003,
                    "warning " + i + ": " + new string('w', 180),
                    severity: Severity.Warning));
            }
            var expectedWarnings = new JArray();
            foreach (var warning in warnings)
            {
                expectedWarnings.Add(AgentApiResponse.DiagnosticToJson(warning));
            }
            var payload = new JObject { ["answer"] = 42 };

            var segments = new List<string>();
            string? cursor = null;
            var hops = 0;
            AgentApiResponse response;
            do
            {
                response = _api.Ok(payload, warnings, context: "ctx", cursor: cursor);
                AssertUtf8WithinCap(response);
                Assert.That(response.Ok, Is.True,
                    "every segment preserves the original ok:true");
                Assert.That(response.Warnings, Is.Empty,
                    "the real warnings ride inside the segmented content");
                var data = response.Data!;
                Assert.That(data.Type, Is.EqualTo(JTokenType.String));
                segments.Add(data.Value<string>()!);
                cursor = response.NextCursor;
                hops++;
            }
            while (cursor is not null && hops < 50);

            Assert.That(hops, Is.GreaterThan(1));
            var reassembled = JObject.Parse(string.Concat(segments));
            Assert.That((bool?)reassembled["ok"], Is.True);
            Assert.That(JToken.DeepEquals(reassembled["data"], payload), Is.True);
            Assert.That(JToken.DeepEquals(reassembled["warnings"], expectedWarnings),
                Is.True);
        }

        // ── Response immutability ────────────────────────────────────────────

        [Test]
        public void MutatingCallerDiagnosticsAfterResponse_DoesNotChangeEnvelope()
        {
            var warnings = new List<Diagnostic>
            {
                ErrorCodes.Create(
                    ErrorCodes.OW_CFG_003, "first warning", severity: Severity.Warning),
            };
            var response = _api.Ok(new JObject { ["a"] = 1 }, warnings);
            var jsonBefore = response.ToJson();
            var bytesBefore = Encoding.UTF8.GetByteCount(jsonBefore);

            warnings.Add(ErrorCodes.Create(
                ErrorCodes.OW_CFG_003, "injected after the fact", severity: Severity.Warning));
            warnings[0] = ErrorCodes.Create(ErrorCodes.OW_API_001, "rewritten");

            Assert.That(response.ToJson(), Is.EqualTo(jsonBefore));
            Assert.That(Encoding.UTF8.GetByteCount(response.ToJson()),
                Is.EqualTo(bytesBefore));
            Assert.That(response.Warnings, Has.Count.EqualTo(1));

            var errors = new List<Diagnostic>
            {
                ErrorCodes.Create(ErrorCodes.OW_RUN_010, "unknown run"),
            };
            var failed = _api.Failure(errors);
            var failedJson = failed.ToJson();
            errors.Add(ErrorCodes.Create(ErrorCodes.OW_API_001, "injected"));
            Assert.That(failed.ToJson(), Is.EqualTo(failedJson));
        }

        // ── Internal errors → OW-API-004 (SPEC §J) ──────────────────────────

        [Test]
        public void ThrowingCommand_IsCaughtAsOwApi004()
        {
            LogAssert.Expect(LogType.Exception, new Regex(".*fixture boom.*"));
            var response = _api.Invoke(() =>
                throw new InvalidOperationException("fixture boom"));

            Assert.That(response.Ok, Is.False);
            AssertErrorCode(response, "OW-API-004");
            AssertUtf8WithinCap(response);
        }

        // ── Request shape → OW-API-001 (SPEC §H1, §J) ───────────────────────

        [Test]
        public void WorkflowSource_BodyAndPathTogether_ReturnsOwApi001()
        {
            var result = WorkflowSource.From(new JObject(), "Assets/x.opweave.json");
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-API-001"));
        }

        [Test]
        public void WorkflowSource_NeitherBodyNorPath_ReturnsOwApi001()
        {
            var result = WorkflowSource.From(null, null);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-API-001"));
        }

        [Test]
        public void WorkflowSource_NonObjectBody_ReturnsOwApi001()
        {
            var result = WorkflowSource.From(new JArray(), null);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-API-001"));
            Assert.That(result.Diagnostics[0].JsonPath, Is.EqualTo("$.workflow"));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("C:\\absolute\\x.opweave.json")]
        [TestCase("/rooted/x.opweave.json")]
        public void WorkflowSource_BadPath_ReturnsOwApi001(string path)
        {
            var result = WorkflowSource.From(null, path);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-API-001"));
        }

        [Test]
        public void WorkflowSource_AcceptsBodyOrPath()
        {
            var body = new JObject { ["schemaVersion"] = 1 };
            var fromBody = WorkflowSource.From(body, null);
            Assert.That(fromBody.Ok, Is.True);
            Assert.That(fromBody.Value!.IsBody, Is.True);
            Assert.That(JToken.DeepEquals(fromBody.Value.Workflow, body), Is.True);
            Assert.That(fromBody.Value.Workflow, Is.Not.SameAs(body),
                "the boundary copy keeps a caller mutation from reaching the source");

            var fromPath = WorkflowSource.From(null, "Assets/WFs/x.opweave.json");
            Assert.That(fromPath.Ok, Is.True);
            Assert.That(fromPath.Value!.IsBody, Is.False);
            Assert.That(fromPath.Value.Path, Is.EqualTo("Assets/WFs/x.opweave.json"));
        }

        // ── Unknown runId → OW-RUN-010 (SPEC §H1, §J) ───────────────────────

        [Test]
        public void UnknownRunId_ReturnsOwRun010()
        {
            // A well-formed "run_" + ULID (SPEC §0.4) that no journal records.
            var result = _api.RequireRun("run_0000000000000000000000000Z");
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-RUN-010"));

            var response = _api.Failure(result.Diagnostics);
            Assert.That(response.Ok, Is.False);
            AssertErrorCode(response, "OW-RUN-010");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("run_does_not_exist")]
        [TestCase("run_")]
        [TestCase("not-a-run")]
        public void MissingOrMalformedRunId_IsMalformedRequestOwApi001(string? runId)
        {
            var result = _api.RequireRun(runId);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-API-001"));
        }

        // ── Service contract (SPEC §H1, §H4 item 5) ─────────────────────────

        [Test]
        public void Actor_IsAlwaysAgent()
        {
            Assert.That(_api.Actor, Is.EqualTo(ActorKind.Agent));
        }

        [Test]
        public void PublicSurface_ExposesNoApprovalNeedsReviewOrSettingsEntryPoint()
        {
            // SPEC §H4 item 5: Agents get no API for approval, NeedsReview
            // resolution or settings changes. Scan every public member of the
            // AgentApi assembly so a later command cannot sneak one in.
            var forbidden = new Regex(
                "approv|reject|needsreview|settings", RegexOptions.IgnoreCase);
            var offenders = new List<string>();
            foreach (var type in typeof(AgentApiService).Assembly.GetExportedTypes())
            {
                const BindingFlags flags =
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.DeclaredOnly;
                foreach (var member in type.GetMethods(flags).Cast<MemberInfo>()
                             .Concat(type.GetProperties(flags)))
                {
                    if (forbidden.IsMatch(member.Name))
                    {
                        offenders.Add(type.Name + "." + member.Name);
                    }
                }
                foreach (var method in type.GetMethods(flags))
                {
                    if (method.GetParameters().Any(p => p.ParameterType == typeof(ActorKind)))
                    {
                        offenders.Add(type.Name + "." + method.Name + "(ActorKind)");
                    }
                }
            }
            Assert.That(offenders, Is.Empty);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static List<JToken> Items(int count, int payloadSize)
        {
            var items = new List<JToken>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(new JObject
                {
                    ["index"] = i,
                    ["payload"] = new string('x', payloadSize),
                });
            }
            return items;
        }

        private static JToken Pack(IReadOnlyList<JToken> items, int start, int count)
        {
            var array = new JArray();
            for (var i = start; i < start + count; i++)
            {
                array.Add(items[i]);
            }
            return new JObject { ["items"] = array };
        }

        private static void AssertUtf8WithinCap(AgentApiResponse response)
        {
            Assert.That(Encoding.UTF8.GetByteCount(response.ToJson()),
                Is.LessThanOrEqualTo(AgentApiService.MaxResponseBytes));
        }

        private static void AssertErrorCode(AgentApiResponse response, string code)
        {
            var error = (JObject)response.ToWire()["errors"]![0]!;
            Assert.That((string?)error["code"], Is.EqualTo(code));
        }
    }
}
