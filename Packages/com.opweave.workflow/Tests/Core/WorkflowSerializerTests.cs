#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Model;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for <see cref="WorkflowSerializer"/> (SPEC §A3): the round-trip
    /// rules — read→write→read equal and write→read→write byte-identical, including
    /// the SPEC §A2 example — and every diagnostic contract: OW-WF-001 for comments,
    /// duplicate keys, non-RFC-8259 JSON extensions and depth 33; OW-WF-006 for
    /// unknown properties (Warning, or
    /// Error under strict) and reserved <c>$</c> keys in parameters (Error, §A5);
    /// OW-WF-008 for the SG-13 size limits; OW-WF-002 for a newer major and a
    /// Warning for a newer minor (§N.3 item 3); silently dropped layout entries for
    /// missing nodes (§A8). The same sources run under dotnet and Unity Mono
    /// (local:T-W1.3-03:serializer-roundtrip, local:T-W1.3-03:serializer-diagnostics).
    /// </summary>
    internal sealed class WorkflowSerializerTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        // The SPEC §A2 example, verbatim.
        private const string A2Example = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.create-player"",
  ""title"": ""Player を作って Prefab 化する"",
  ""inputs"": [ { ""id"": ""scene"", ""type"": ""unity.SceneRef"", ""required"": true } ],
  ""outputs"": [ { ""id"": ""prefab"", ""from"": { ""node"": ""toPrefab"", ""port"": ""prefab"" } } ],
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""create"", ""operation"": ""unity.gameobject.create"", ""version"": 1,
      ""parameters"": { ""name"": ""Player"" } },
    { ""id"": ""addAnimator"", ""operation"": ""unity.component.add"", ""version"": 1,
      ""parameters"": { ""componentType"": ""UnityEngine.Animator"" } },
    { ""id"": ""toPrefab"", ""operation"": ""unity.prefab.create_from_gameobject"", ""version"": 1,
      ""parameters"": { ""path"": ""Assets/Prefabs/Player.prefab"" }, ""policy"": { ""timeoutSeconds"": 60 } },
    { ""id"": ""save"", ""operation"": ""unity.scene.save"", ""version"": 1 }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""create"" }, { ""from"": ""create"", ""to"": ""addAnimator"" },
    { ""from"": ""addAnimator"", ""to"": ""toPrefab"" }, { ""from"": ""toPrefab"", ""to"": ""save"" }
  ],
  ""dataEdges"": [
    { ""from"": { ""node"": ""create"", ""port"": ""gameObject"" }, ""to"": { ""node"": ""addAnimator"", ""port"": ""target"" } },
    { ""from"": { ""node"": ""create"", ""port"": ""gameObject"" }, ""to"": { ""node"": ""toPrefab"", ""port"": ""source"" } }
  ],
  ""inputBindings"": [
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""create"", ""port"": ""scene"" } },
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""save"", ""port"": ""scene"" } }
  ]
}";

        // The minimal document exactly as Write emits it: §A2 member order, 2-space
        // indentation, LF endings, trailing newline.
        private const string MinimalGolden =
            "{\n" +
            "  \"schemaVersion\": \"1.0\",\n" +
            "  \"id\": \"example.min\",\n" +
            "  \"nodes\": [\n" +
            "    {\n" +
            "      \"id\": \"start\",\n" +
            "      \"operation\": \"core.start\",\n" +
            "      \"version\": 1\n" +
            "    }\n" +
            "  ],\n" +
            "  \"controlEdges\": []\n" +
            "}\n";

        // A fixture exercising every member kind: layout with notes and a stale
        // entry, x-* extensions, input defaults (explicit null included), typed
        // numbers, explicit null parameter, unsorted parameter keys, unicode text.
        private const string FullFixture = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.full"",
  ""title"": ""Full フル"",
  ""description"": ""exercises every member"",
  ""tags"": [""alpha"", ""beta""],
  ""inputs"": [
    { ""id"": ""scene"", ""type"": ""unity.SceneRef"", ""required"": true },
    { ""id"": ""name"", ""type"": ""core.Json"", ""required"": false,
      ""default"": { ""b"": 2, ""a"": 1 }, ""description"": ""a default"" },
    { ""id"": ""opt"", ""type"": ""core.Json"", ""required"": false, ""default"": null }
  ],
  ""outputs"": [
    { ""id"": ""prefab"", ""from"": { ""node"": ""toPrefab"", ""port"": ""prefab"" },
      ""description"": ""result"" }
  ],
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""create"", ""operation"": ""unity.gameobject.create"", ""version"": 1,
      ""parameters"": { ""name"": ""Player"" } },
    { ""id"": ""toPrefab"", ""operation"": ""unity.prefab.create_from_gameobject"", ""version"": 2,
      ""parameters"": {
        ""path"": ""Assets/Prefabs/Player.prefab"",
        ""cfg"": { ""z"": 1.5, ""a"": null, ""m"": [true, 0.25] },
        ""explicitNull"": null
      },
      ""label"": ""作成 ノード"",
      ""policy"": { ""timeoutSeconds"": 60, ""maxAttempts"": 3 } }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""create"" },
    { ""from"": ""create"", ""to"": ""toPrefab"", ""fromPort"": ""next"" }
  ],
  ""dataEdges"": [
    { ""from"": { ""node"": ""create"", ""port"": ""gameObject"" },
      ""to"": { ""node"": ""toPrefab"", ""port"": ""source"" } }
  ],
  ""inputBindings"": [
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""create"", ""port"": ""scene"" } }
  ],
  ""layout"": {
    ""nodes"": {
      ""ghost"": { ""x"": 10.5, ""y"": -3.25 },
      ""start"": { ""x"": 1, ""y"": 2 },
      ""create"": { ""x"": 40, ""y"": 60 }
    },
    ""notes"": [ { ""id"": ""n1"", ""x"": 5, ""y"": 6, ""text"": ""memo メモ"" } ]
  },
  ""x-vendor"": { ""b"": 2, ""a"": 1 },
  ""x-flag"": true
}";

        // "layout" placed before "nodes" — legal JSON member order — with one
        // stale entry: the §A8 filter must see the node ids anyway.
        private const string LayoutBeforeNodesFixture = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.layout-first"",
  ""layout"": {
    ""nodes"": {
      ""ghost"": { ""x"": 10.5, ""y"": -3.25 },
      ""start"": { ""x"": 1, ""y"": 2 },
      ""create"": { ""x"": 40, ""y"": 60 }
    },
    ""notes"": [ { ""id"": ""n1"", ""x"": 5, ""y"": 6, ""text"": ""memo"" } ]
  },
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""create"", ""operation"": ""unity.gameobject.create"", ""version"": 1 }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""create"" } ]
}";

        // SPEC §A8: a stale layout entry is dropped before its contents are
        // looked at — a missing member, wrong kinds, a non-object value and
        // an unknown property on entries for nonexistent nodes all produce
        // no diagnostic, under either strictness mode.
        private const string StaleLayoutEntriesFixture = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.stale-layout"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 }
  ],
  ""controlEdges"": [],
  ""layout"": {
    ""nodes"": {
      ""missing-y"": { ""x"": 1 },
      ""wrong-types"": { ""x"": ""left"", ""y"": [0, 0] },
      ""not-an-object"": 7,
      ""unknown-member"": { ""x"": 1, ""y"": 2, ""bogus"": true },
      ""start"": { ""x"": 1, ""y"": 2 }
    },
    ""notes"": []
  }
}";

        private static WorkflowDocument MustRead(string json)
        {
            var result = WorkflowSerializer.Read(json);
            Assert.IsTrue(result.Ok, "Read failed: " + Describe(result.Diagnostics));
            return result.Value!;
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var builder = new StringBuilder();
            foreach (var diagnostic in diagnostics)
            {
                builder.Append(diagnostic.Code).Append(' ').Append(diagnostic.JsonPath)
                    .Append(' ').Append(diagnostic.Message).AppendLine();
            }
            return builder.ToString();
        }

        // SPEC §A3 item 5: every diagnostic carries a JsonPath and a source
        // location in its message (the Diagnostic record has no dedicated
        // line/column member). The serializer appends " (line L, column C)" to
        // every diagnostic; strict-syntax OW-WF-001 failures keep the SG-02
        // reader's "at offset N" location ahead of the line/column it
        // resolves to over the original text.
        private static readonly Regex s_position =
            new Regex(@"\(line [1-9][0-9]*, column [0-9]+\)\.$",
                RegexOptions.CultureInvariant);

        // A strict SG-02 failure reports the reader's UTF-16 offset plus the
        // 1-based line / 0-based column it resolves to — all three exact.
        private static void AssertStrictLocation(
            Diagnostic diagnostic, int offset, int line, int column)
        {
            var suffix =
                " at offset " + offset.ToString(CultureInfo.InvariantCulture) +
                " (line " + line.ToString(CultureInfo.InvariantCulture) +
                ", column " + column.ToString(CultureInfo.InvariantCulture) + ").";
            StringAssert.EndsWith(suffix, diagnostic.Message);
        }

        private static void AssertLocations(IReadOnlyList<Diagnostic> diagnostics)
        {
            Assert.AreNotEqual(0, diagnostics.Count, "expected at least one diagnostic");
            foreach (var diagnostic in diagnostics)
            {
                Assert.IsNotNull(diagnostic.JsonPath, "JsonPath missing: " + diagnostic.Message);
                Assert.IsTrue(
                    s_position.IsMatch(diagnostic.Message),
                    "line/column missing: " + diagnostic.Message);
            }
        }

        private static Diagnostic SingleDiagnostic(string json, ReadOptions? options = null)
        {
            var result = WorkflowSerializer.Read(json, options);
            Assert.AreEqual(1, result.Diagnostics.Count, Describe(result.Diagnostics));
            AssertLocations(result.Diagnostics);
            return result.Diagnostics[0];
        }

        private static string Document(string body) =>
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.x\"," + body + "}";

        private static string DocumentWithNodes(string nodesJson, string extra = "") =>
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.x\",\"nodes\":[" + nodesJson +
            "],\"controlEdges\":[]" + extra + "}";

        private static string NestedValue(int depth)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < depth; i++)
            {
                builder.Append('[');
            }
            for (var i = 0; i < depth; i++)
            {
                builder.Append(']');
            }
            return builder.ToString();
        }

        // ── Round trips (SPEC §A3 tests, A8) ────────────────────────────────────

        [Test]
        public void MinimalDocumentWritesExactGoldenBytes()
        {
            var document = MustRead(MinimalGolden);
            Assert.AreEqual(MinimalGolden, WorkflowSerializer.Write(document));
        }

        [Test]
        public void A2ExampleReadWriteReadIsEqual()
        {
            var document = MustRead(A2Example);
            var reparsed = MustRead(WorkflowSerializer.Write(document));

            Assert.AreEqual(document, reparsed);
            Assert.AreEqual("1.0", document.SchemaVersion);
            Assert.AreEqual("example.create-player", document.Id);
            Assert.AreEqual(5, document.Nodes.Count);
            Assert.AreEqual(4, document.ControlEdges.Count);
            Assert.AreEqual(2, document.DataEdges.Count);
            Assert.AreEqual(2, document.InputBindings.Count);
            Assert.AreEqual(1, document.Inputs.Count);
            Assert.AreEqual(1, document.Outputs.Count);
            var policy = document.Nodes[3].Policy;
            Assert.AreEqual(new NodePolicy(60, null), policy);
        }

        [Test]
        public void A2ExampleWriteReadWriteIsByteIdentical()
        {
            var written = WorkflowSerializer.Write(MustRead(A2Example));

            // Format contract: LF only, 2-space indent, trailing newline.
            Assert.IsFalse(written.Contains("\r"));
            Assert.IsTrue(written.EndsWith("}\n"));
            Assert.IsTrue(written.StartsWith("{\n  \"schemaVersion\": \"1.0\",\n"));
            StringAssert.Contains("\n  \"nodes\": [\n    {\n      \"id\": \"start\",", written);

            var rewritten = WorkflowSerializer.Write(MustRead(written));
            Assert.AreEqual(written, rewritten);
        }

        [Test]
        public void A2ExampleWriteEmitsExpectedNormalizedForm()
        {
            // The §A2 example written out: members in the A2 table order, arrays in
            // author order, values in canonical text.
            var written = WorkflowSerializer.Write(MustRead(A2Example));
            var expected =
                "{\n" +
                "  \"schemaVersion\": \"1.0\",\n" +
                "  \"id\": \"example.create-player\",\n" +
                "  \"title\": \"Player を作って Prefab 化する\",\n" +
                "  \"inputs\": [\n" +
                "    {\n" +
                "      \"id\": \"scene\",\n" +
                "      \"type\": \"unity.SceneRef\",\n" +
                "      \"required\": true\n" +
                "    }\n" +
                "  ],\n" +
                "  \"outputs\": [\n" +
                "    {\n" +
                "      \"id\": \"prefab\",\n" +
                "      \"from\": {\n" +
                "        \"node\": \"toPrefab\",\n" +
                "        \"port\": \"prefab\"\n" +
                "      }\n" +
                "    }\n" +
                "  ],\n" +
                "  \"nodes\": [\n" +
                "    {\n" +
                "      \"id\": \"start\",\n" +
                "      \"operation\": \"core.start\",\n" +
                "      \"version\": 1\n" +
                "    },\n" +
                "    {\n" +
                "      \"id\": \"create\",\n" +
                "      \"operation\": \"unity.gameobject.create\",\n" +
                "      \"version\": 1,\n" +
                "      \"parameters\": {\n" +
                "        \"name\": \"Player\"\n" +
                "      }\n" +
                "    },\n" +
                "    {\n" +
                "      \"id\": \"addAnimator\",\n" +
                "      \"operation\": \"unity.component.add\",\n" +
                "      \"version\": 1,\n" +
                "      \"parameters\": {\n" +
                "        \"componentType\": \"UnityEngine.Animator\"\n" +
                "      }\n" +
                "    },\n" +
                "    {\n" +
                "      \"id\": \"toPrefab\",\n" +
                "      \"operation\": \"unity.prefab.create_from_gameobject\",\n" +
                "      \"version\": 1,\n" +
                "      \"parameters\": {\n" +
                "        \"path\": \"Assets/Prefabs/Player.prefab\"\n" +
                "      },\n" +
                "      \"policy\": {\n" +
                "        \"timeoutSeconds\": 60\n" +
                "      }\n" +
                "    },\n" +
                "    {\n" +
                "      \"id\": \"save\",\n" +
                "      \"operation\": \"unity.scene.save\",\n" +
                "      \"version\": 1\n" +
                "    }\n" +
                "  ],\n" +
                "  \"controlEdges\": [\n" +
                "    {\n" +
                "      \"from\": \"start\",\n" +
                "      \"to\": \"create\"\n" +
                "    },\n" +
                "    {\n" +
                "      \"from\": \"create\",\n" +
                "      \"to\": \"addAnimator\"\n" +
                "    },\n" +
                "    {\n" +
                "      \"from\": \"addAnimator\",\n" +
                "      \"to\": \"toPrefab\"\n" +
                "    },\n" +
                "    {\n" +
                "      \"from\": \"toPrefab\",\n" +
                "      \"to\": \"save\"\n" +
                "    }\n" +
                "  ],\n" +
                "  \"dataEdges\": [\n" +
                "    {\n" +
                "      \"from\": {\n" +
                "        \"node\": \"create\",\n" +
                "        \"port\": \"gameObject\"\n" +
                "      },\n" +
                "      \"to\": {\n" +
                "        \"node\": \"addAnimator\",\n" +
                "        \"port\": \"target\"\n" +
                "      }\n" +
                "    },\n" +
                "    {\n" +
                "      \"from\": {\n" +
                "        \"node\": \"create\",\n" +
                "        \"port\": \"gameObject\"\n" +
                "      },\n" +
                "      \"to\": {\n" +
                "        \"node\": \"toPrefab\",\n" +
                "        \"port\": \"source\"\n" +
                "      }\n" +
                "    }\n" +
                "  ],\n" +
                "  \"inputBindings\": [\n" +
                "    {\n" +
                "      \"fromInput\": \"scene\",\n" +
                "      \"to\": {\n" +
                "        \"node\": \"create\",\n" +
                "        \"port\": \"scene\"\n" +
                "      }\n" +
                "    },\n" +
                "    {\n" +
                "      \"fromInput\": \"scene\",\n" +
                "      \"to\": {\n" +
                "        \"node\": \"save\",\n" +
                "        \"port\": \"scene\"\n" +
                "      }\n" +
                "    }\n" +
                "  ]\n" +
                "}\n";
            Assert.AreEqual(expected, written);
        }

        [Test]
        public void FullFixtureRoundTripsBothDirections()
        {
            var document = MustRead(FullFixture);
            var written = WorkflowSerializer.Write(document);

            // read→write→read equality (canonical-form equality, SPEC §A1/A4)
            var reparsed = MustRead(written);
            Assert.AreEqual(document, reparsed);

            // write→read→write byte identity
            var rewritten = WorkflowSerializer.Write(reparsed);
            Assert.AreEqual(written, rewritten);
        }

        [Test]
        public void WriteNormalizesValuesByCanonicalJsonOnly()
        {
            var written = WorkflowSerializer.Write(MustRead(FullFixture));

            // Parameter/default/extension object values keep canonical key order
            // and canonical number text; explicit null is preserved, not dropped.
            StringAssert.Contains("\"cfg\": {\"a\":null,\"m\":[true,25e-2],\"z\":15e-1}", written);
            StringAssert.Contains("\"explicitNull\": null", written);
            StringAssert.Contains("\"default\": {\"a\":1,\"b\":2}", written);
            StringAssert.Contains("\"default\": null", written);
            StringAssert.Contains("\"x-vendor\": {\"a\":1,\"b\":2}", written);
        }

        [Test]
        public void WriteOmitsEmptyOptionalsAndDefaultedFromPort()
        {
            var written = WorkflowSerializer.Write(MustRead(FullFixture));

            // fromPort "next" is the default and stays implicit; notes carry the
            // §A8 member order.
            Assert.IsFalse(written.Contains("fromPort"));
            StringAssert.Contains(
                "\"id\": \"n1\",\n        \"x\": 5e0,\n        \"y\": 6e0,\n        \"text\": \"memo メモ\"",
                written);
        }

        // SPEC §A8: layout entries for non-existent nodes are dropped silently.
        [Test]
        public void LayoutEntriesForMissingNodesAreDropped()
        {
            var document = MustRead(FullFixture);

            Assert.IsNotNull(document.Layout);
            Assert.AreEqual(2, document.Layout!.Nodes.Count);
            Assert.IsTrue(document.Layout.Nodes.ContainsKey("start"));
            Assert.IsTrue(document.Layout.Nodes.ContainsKey("create"));
            Assert.IsFalse(document.Layout.Nodes.ContainsKey("ghost"));
            Assert.AreEqual(1, document.Layout.Notes.Count);

            var written = WorkflowSerializer.Write(document);
            Assert.IsFalse(written.Contains("ghost"));
        }

        // SPEC §A8: top-level member order must not matter — a "layout" member
        // read before "nodes" keeps positions for existing nodes and still
        // drops stale entries silently.
        [Test]
        public void LayoutBeforeNodesKeepsPositionsAndDropsStaleEntries()
        {
            var document = MustRead(LayoutBeforeNodesFixture);

            Assert.IsNotNull(document.Layout);
            Assert.AreEqual(2, document.Layout!.Nodes.Count);
            Assert.AreEqual(new LayoutNodePosition(1.0, 2.0), document.Layout.Nodes["start"]);
            Assert.AreEqual(new LayoutNodePosition(40.0, 60.0), document.Layout.Nodes["create"]);
            Assert.IsFalse(document.Layout.Nodes.ContainsKey("ghost"));
            Assert.AreEqual(1, document.Layout.Notes.Count);

            // The surviving positions are written back out; the stale one is
            // gone for good.
            var written = WorkflowSerializer.Write(document);
            Assert.IsFalse(written.Contains("ghost"));
            var reparsed = MustRead(written);
            Assert.AreEqual(2, reparsed.Layout!.Nodes.Count);
            Assert.AreEqual(new LayoutNodePosition(1.0, 2.0), reparsed.Layout.Nodes["start"]);
            Assert.AreEqual(new LayoutNodePosition(40.0, 60.0), reparsed.Layout.Nodes["create"]);
        }

        // SPEC §A8: the stale-entry drop runs before content validation —
        // missing members, wrong kinds and a non-object value on entries for
        // nonexistent nodes are dropped with no diagnostics at all.
        [Test]
        public void MalformedStaleLayoutEntriesAreDroppedSilently()
        {
            var result = WorkflowSerializer.Read(StaleLayoutEntriesFixture);

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(0, result.Diagnostics.Count);
            var nodes = result.Value!.Layout!.Nodes;
            Assert.AreEqual(1, nodes.Count);
            Assert.AreEqual(new LayoutNodePosition(1.0, 2.0), nodes["start"]);

            var written = WorkflowSerializer.Write(result.Value);
            Assert.IsFalse(written.Contains("missing-y"));
            Assert.IsFalse(written.Contains("wrong-types"));
        }

        // SPEC §A8: an unknown property on a stale entry is dropped silently
        // even under strict, where the same member on a live entry is an
        // OW-WF-006 error.
        [Test]
        public void UnknownMembersOnStaleLayoutEntriesAreDroppedSilently()
        {
            var result = WorkflowSerializer.Read(StaleLayoutEntriesFixture);
            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(0, result.Diagnostics.Count);

            result = WorkflowSerializer.Read(
                StaleLayoutEntriesFixture, new ReadOptions(Strict: true));
            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(0, result.Diagnostics.Count);
            Assert.AreEqual(1, result.Value!.Layout!.Nodes.Count);
        }

        // Entries for nodes that do exist keep full validation: the same
        // defects a stale entry drops still report against the live node id.
        [Test]
        public void LiveLayoutEntriesKeepFullValidation()
        {
            const string layout =
                ",\"layout\":{\"nodes\":{\"a\":{\"x\":\"left\",\"bogus\":true}},\"notes\":[]}";

            var result = WorkflowSerializer.Read(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1}", layout));

            Assert.IsFalse(result.Ok);
            AssertLocations(result.Diagnostics);
            Assert.AreEqual(5, result.Diagnostics.Count);
            Assert.AreEqual("OW-WF-001", result.Diagnostics[0].Code);
            Assert.AreEqual("$.layout.nodes.a.x", result.Diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-006", result.Diagnostics[1].Code);
            Assert.AreEqual(Severity.Warning, result.Diagnostics[1].Severity);
            Assert.AreEqual("$.layout.nodes.a.bogus", result.Diagnostics[1].JsonPath);
            Assert.AreEqual("OW-WF-003", result.Diagnostics[2].Code);
            Assert.AreEqual("$.layout.nodes.a.x", result.Diagnostics[2].JsonPath);
            Assert.AreEqual("OW-WF-003", result.Diagnostics[3].Code);
            Assert.AreEqual("$.layout.nodes.a.y", result.Diagnostics[3].JsonPath);
            // The unpositionable entry fails Create's structural check too.
            Assert.AreEqual("OW-WF-003", result.Diagnostics[4].Code);
            Assert.AreEqual("$.layout.nodes.a", result.Diagnostics[4].JsonPath);

            result = WorkflowSerializer.Read(
                DocumentWithNodes(
                    "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1}", layout),
                new ReadOptions(Strict: true));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(5, result.Diagnostics.Count);
            Assert.AreEqual("OW-WF-006", result.Diagnostics[1].Code);
            Assert.AreEqual(Severity.Error, result.Diagnostics[1].Severity);
        }

        [Test]
        public void NullParameterAndAbsentParameterAreDistinct()
        {
            var document = MustRead(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"explicitNull\":null,\"value\":3}}"));

            var parameters = document.Nodes[0].Parameters;
            Assert.AreEqual(2, parameters.Count);
            Assert.IsTrue(parameters.ContainsKey("explicitNull"));
            Assert.AreEqual("null", parameters["explicitNull"].CanonicalText);
            Assert.IsFalse(parameters.ContainsKey("absent"));
        }

        // ── OW-WF-001: not parseable as JSON ────────────────────────────────────

        [Test]
        public void CommentsAreRejected()
        {
            var diagnostic = SingleDiagnostic(
                Document("\"nodes\":[],/* trailing */\"controlEdges\":[]"));
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            AssertStrictLocation(diagnostic, 51, 1, 51);

            diagnostic = SingleDiagnostic("// leading\n" + Document("\"nodes\":[],\"controlEdges\":[]"));
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            AssertStrictLocation(diagnostic, 0, 1, 0);

            diagnostic = SingleDiagnostic(Document("\"nodes\":[],\"controlEdges\":[]") + "\n// tail");
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            AssertStrictLocation(diagnostic, 70, 2, 0);
        }

        // The strict SG-02 reader reports the duplicate before the token walk:
        // object member steps stay "*" wildcards so source keys never leak into
        // a diagnostic (SPEC §0.6 SG-02).
        [Test]
        public void DuplicateKeysAreRejected()
        {
            var diagnostic = SingleDiagnostic(
                "{\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\",\"id\":\"a\",\"nodes\":[],\"controlEdges\":[]}");
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            Assert.AreEqual("$.*", diagnostic.JsonPath);
            AssertStrictLocation(diagnostic, 38, 1, 38);

            diagnostic = SingleDiagnostic(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"p\":1,\"p\":2}}"));
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            Assert.AreEqual("$.*[0].*.*", diagnostic.JsonPath);
            AssertStrictLocation(diagnostic, 119, 1, 119);

            // Multi-line: the offset resolves to the right line/column.
            diagnostic = SingleDiagnostic(
                "{\n  \"schemaVersion\":\"1.0\",\n  \"schemaVersion\":\"1.0\",\n" +
                "  \"id\":\"a\",\n  \"nodes\":[],\n  \"controlEdges\":[]\n}");
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            Assert.AreEqual("$.*", diagnostic.JsonPath);
            AssertStrictLocation(diagnostic, 44, 3, 17);
        }

        [Test]
        public void TrailingContentIsRejected()
        {
            var diagnostic = SingleDiagnostic(
                Document("\"nodes\":[],\"controlEdges\":[]") + "\n{}");
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            AssertStrictLocation(diagnostic, 70, 2, 0);
        }

        // SPEC §A3 item 1: MaxDepth = 32 — 32 nested containers pass, 33 fail.
        // The root object already counts, so the payload nests inside an x- member.
        [Test]
        public void DepthBoundaryIsEnforced()
        {
            var accepted = WorkflowSerializer.Read(Document(
                "\"nodes\":[],\"controlEdges\":[],\"x-deep\":" + NestedValue(31)));
            Assert.IsTrue(accepted.Ok, Describe(accepted.Diagnostics));

            var result = WorkflowSerializer.Read(Document(
                "\"nodes\":[],\"controlEdges\":[],\"x-deep\":" + NestedValue(32)));
            Assert.IsFalse(result.Ok);
            var diagnostic = SingleDiagnosticFrom(result);
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            AssertLocations(result.Diagnostics);
        }

        [Test]
        public void NumbersOutsideTheValueModelAreRejected()
        {
            var diagnostic = SingleDiagnostic(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"p\":9223372036854775808}}"));
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            AssertStrictLocation(diagnostic, 114, 1, 114);

            diagnostic = SingleDiagnostic(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"p\":1e999}}"));
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            AssertStrictLocation(diagnostic, 114, 1, 114);

            diagnostic = SingleDiagnostic(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"p\":NaN}}"));
            Assert.AreEqual("OW-WF-001", diagnostic.Code);
            AssertStrictLocation(diagnostic, 114, 1, 114);
        }

        // SPEC §A3 reads a strict RFC 8259 grammar: every JSON.NET reader
        // extension the strict SG-02 reader rejects is OW-WF-001, never
        // silently accepted.
        [Test]
        public void JsonNetSyntaxExtensionsAreRejected()
        {
            var cases = new[]
            {
                (Json: "{\"schemaVersion\":'1.0',\"id\":\"a\",\"nodes\":[],\"controlEdges\":[]}",
                    Offset: 17, Line: 1, Column: 17),                                        // single-quoted string
                (Json: "{'schemaVersion':\"1.0\",\"id\":\"a\",\"nodes\":[],\"controlEdges\":[]}",
                    Offset: 1, Line: 1, Column: 1),                                          // single-quoted name
                (Json: "{schemaVersion:\"1.0\",\"id\":\"a\",\"nodes\":[],\"controlEdges\":[]}",
                    Offset: 1, Line: 1, Column: 1),                                          // unquoted name
                (Json: "{\"schemaVersion\":\"1.0\",\"id\":\"a\",\"nodes\":[],\"controlEdges\":[],}",
                    Offset: 61, Line: 1, Column: 61),                                        // trailing comma (object)
                (Json: "{\"schemaVersion\":\"1.0\",\"id\":\"a\",\"nodes\":[],\"controlEdges\":[]," +
                    "\"tags\":[\"a\",]}",
                    Offset: 73, Line: 1, Column: 73),                                        // trailing comma (array)
                // Multi-line: the SG-02 offset resolves to the right line/column.
                (Json: "{\n  \"schemaVersion\": '1.0',\n  \"id\":\"a\",\n" +
                    "  \"nodes\":[],\n  \"controlEdges\":[]\n}",
                    Offset: 21, Line: 2, Column: 19),                                        // single-quoted string
            };
            for (var i = 0; i < cases.Length; i++)
            {
                var diagnostic = SingleDiagnostic(cases[i].Json);
                Assert.AreEqual("OW-WF-001", diagnostic.Code, "case " + i);
                AssertStrictLocation(diagnostic, cases[i].Offset, cases[i].Line, cases[i].Column);
            }
        }

        [Test]
        public void NonFiniteNumberLiteralsAreRejected()
        {
            foreach (var literal in new[] { "NaN", "Infinity", "-Infinity" })
            {
                var diagnostic = SingleDiagnostic(DocumentWithNodes(
                    "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                    "\"parameters\":{\"p\":" + literal + "}}"));
                Assert.AreEqual("OW-WF-001", diagnostic.Code, literal);
                AssertStrictLocation(diagnostic, 114, 1, 114);
            }
        }

        // ── OW-WF-006: unknown properties and reserved "$" keys ─────────────────

        [Test]
        public void UnknownPropertyWarnsByDefault()
        {
            var result = WorkflowSerializer.Read(Document(
                "\"nodes\":[],\"controlEdges\":[],\"mystery\":1"));

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            AssertLocations(result.Diagnostics);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Warning, diagnostic.Severity);
            Assert.AreEqual("$.mystery", diagnostic.JsonPath);
        }

        [Test]
        public void UnknownPropertyIsAnErrorWhenStrict()
        {
            var result = WorkflowSerializer.Read(
                Document("\"nodes\":[],\"controlEdges\":[],\"mystery\":1"),
                new ReadOptions(Strict: true));

            Assert.IsFalse(result.Ok);
            var diagnostic = SingleDiagnosticFrom(result);
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            AssertLocations(result.Diagnostics);
        }

        [Test]
        public void UnknownMembersInsideKnownObjectsAreReported()
        {
            var result = WorkflowSerializer.Read(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1,\"extra\":true}"));

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Warning, diagnostic.Severity);
            Assert.AreEqual("$.nodes[0].extra", diagnostic.JsonPath);
        }

        [Test]
        public void XPrefixedMembersAreKeptSilently()
        {
            var result = WorkflowSerializer.Read(Document(
                "\"nodes\":[],\"controlEdges\":[],\"x-vendor\":{\"k\":1}"),
                new ReadOptions(Strict: true));

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(0, result.Diagnostics.Count);
            Assert.AreEqual("{\"k\":1}", result.Value!.Extensions["x-vendor"].CanonicalText);
        }

        // SPEC §A5: an object with a "$"-prefixed key is reserved for future
        // expressions — an OW-WF-006 error wherever it sits inside parameters.
        [Test]
        public void DollarPrefixedKeysInParametersAreErrors()
        {
            var diagnostic = SingleDiagnostic(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"expr\":{\"$expr\":\"source.x\"}}}"));
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            AssertLocations(new[] { diagnostic });
        }

        [Test]
        public void DollarKeyOnTheParametersObjectIsAnError()
        {
            var diagnostic = SingleDiagnostic(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"$expr\":\"source.x\"}}"));
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
        }

        [Test]
        public void NestedDollarKeyInParameterArrayIsAnError()
        {
            var diagnostic = SingleDiagnostic(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"list\":[{\"$ref\":\"x\"}]}}"));
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
        }

        // SPEC §A5 scopes the reserved "$" rule to parameter values. Outside
        // them a "$"-prefixed member is an ordinary unknown property under
        // §A3 item 2: a Warning, or an Error only when strict is on.
        [Test]
        public void DollarPrefixedRootPropertyWarnsByDefault()
        {
            var result = WorkflowSerializer.Read(Document(
                "\"nodes\":[],\"controlEdges\":[],\"$mystery\":1"));

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            AssertLocations(result.Diagnostics);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Warning, diagnostic.Severity);
            Assert.AreEqual("$[\"$mystery\"]", diagnostic.JsonPath);
        }

        [Test]
        public void DollarPrefixedRootPropertyIsAnErrorWhenStrict()
        {
            var result = WorkflowSerializer.Read(
                Document("\"nodes\":[],\"controlEdges\":[],\"$mystery\":1"),
                new ReadOptions(Strict: true));

            Assert.IsFalse(result.Ok);
            var diagnostic = SingleDiagnosticFrom(result);
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            AssertLocations(result.Diagnostics);
        }

        [Test]
        public void DollarPrefixedMemberInsideNodeWarnsByDefault()
        {
            var result = WorkflowSerializer.Read(DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1,\"$extra\":true}"));

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Warning, diagnostic.Severity);
            Assert.AreEqual("$.nodes[0][\"$extra\"]", diagnostic.JsonPath);
        }

        [Test]
        public void DollarPrefixedMemberInsideNodeIsAnErrorWhenStrict()
        {
            var result = WorkflowSerializer.Read(
                DocumentWithNodes(
                    "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1,\"$extra\":true}"),
                new ReadOptions(Strict: true));

            Assert.IsFalse(result.Ok);
            var diagnostic = SingleDiagnosticFrom(result);
            Assert.AreEqual("OW-WF-006", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            AssertLocations(result.Diagnostics);
        }

        // ── OW-WF-008: SG-13 size limits (equality allowed) ─────────────────────

        [Test]
        public void FileSizeLimitIsEnforcedAtUtf8Boundary()
        {
            var document = Document("\"nodes\":[],\"controlEdges\":[]");
            var byteCount = Encoding.UTF8.GetByteCount(document);

            // At the limit the document still reads.
            var result = WorkflowSerializer.Read(document, new ReadOptions(MaxBytes: byteCount));
            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));

            // One byte over fails.
            result = WorkflowSerializer.Read(
                document + " ", new ReadOptions(MaxBytes: byteCount));
            Assert.IsFalse(result.Ok);
            var diagnostic = SingleDiagnosticFrom(result);
            Assert.AreEqual("OW-WF-008", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            AssertLocations(result.Diagnostics);
        }

        [Test]
        public void DefaultFileSizeLimitIsOneMiB()
        {
            var padding = new string('a', (1 << 20) - Document("\"nodes\":[],\"controlEdges\":[],\"x-pad\":\"\"").Length);
            var atLimit = Document("\"nodes\":[],\"controlEdges\":[],\"x-pad\":\"" + padding + "\"");
            Assert.AreEqual(1 << 20, Encoding.UTF8.GetByteCount(atLimit));

            var result = WorkflowSerializer.Read(atLimit);
            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));

            result = WorkflowSerializer.Read(atLimit + " ");
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("OW-WF-008", result.Diagnostics[0].Code);
        }

        [Test]
        public void NodeCountLimitIsEnforced()
        {
            var builder = new StringBuilder();
            for (var i = 0; i < 200; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"id\":\"n").Append(i).Append("\",\"operation\":\"core.start\",\"version\":1}");
            }
            var atLimit = DocumentWithNodes(builder.ToString());
            var result = WorkflowSerializer.Read(atLimit);
            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(200, result.Value!.Nodes.Count);

            var overLimit = DocumentWithNodes(
                builder.ToString() + ",{\"id\":\"n200\",\"operation\":\"core.start\",\"version\":1}");
            result = WorkflowSerializer.Read(overLimit);
            Assert.IsFalse(result.Ok);
            var diagnostic = SingleDiagnosticFrom(result);
            Assert.AreEqual("OW-WF-008", diagnostic.Code);
            AssertLocations(result.Diagnostics);
        }

        [Test]
        public void StringParameterLimitIsEnforced()
        {
            var atLimit = DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"p\":\"" + new string('a', 64 * 1024) + "\"}}");
            var result = WorkflowSerializer.Read(atLimit);
            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));

            var overLimit = DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"p\":\"" + new string('a', 64 * 1024 + 1) + "\"}}");
            result = WorkflowSerializer.Read(overLimit);
            Assert.IsFalse(result.Ok);
            var diagnostic = SingleDiagnosticFrom(result);
            Assert.AreEqual("OW-WF-008", diagnostic.Code);
            Assert.AreEqual("$.nodes[0].parameters.p", diagnostic.JsonPath);
            AssertLocations(result.Diagnostics);
        }

        // The limit counts UTF-8 bytes, not UTF-16 units: 32768 'é' characters are
        // exactly 64 KiB; one more exceeds it.
        [Test]
        public void StringParameterLimitCountsUtf8Bytes()
        {
            var atLimit = DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"p\":\"" + new string('é', 32768) + "\"}}");
            var result = WorkflowSerializer.Read(atLimit);
            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));

            var overLimit = DocumentWithNodes(
                "{\"id\":\"a\",\"operation\":\"core.start\",\"version\":1," +
                "\"parameters\":{\"p\":\"" + new string('é', 32769) + "\"}}");
            result = WorkflowSerializer.Read(overLimit);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("OW-WF-008", result.Diagnostics[0].Code);
        }

        // ── OW-WF-002 / §N.3 item 3: schemaVersion handling ─────────────────────

        [Test]
        public void NewerMajorSchemaVersionIsRejected()
        {
            var diagnostic = SingleDiagnostic(Document(
                "\"nodes\":[],\"controlEdges\":[]").Replace("\"1.0\"", "\"2.0\""));
            Assert.AreEqual("OW-WF-002", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.AreEqual("$.schemaVersion", diagnostic.JsonPath);
        }

        [Test]
        public void OlderMajorSchemaVersionIsRejected()
        {
            var diagnostic = SingleDiagnostic(Document(
                "\"nodes\":[],\"controlEdges\":[]").Replace("\"1.0\"", "\"0.9\""));
            Assert.AreEqual("OW-WF-002", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
        }

        [Test]
        public void MalformedSchemaVersionIsRejected()
        {
            foreach (var version in new[] { "abc", "1", "1.0.1", "v1.0" })
            {
                var result = WorkflowSerializer.Read(
                    Document("\"nodes\":[],\"controlEdges\":[]")
                        .Replace("\"1.0\"", "\"" + version + "\""));
                Assert.IsFalse(result.Ok, version + " must be rejected");
                Assert.AreEqual("OW-WF-002", result.Diagnostics[0].Code, version);
            }
        }

        [Test]
        public void NonStringSchemaVersionIsRejected()
        {
            var result = WorkflowSerializer.Read(
                Document("\"nodes\":[],\"controlEdges\":[]").Replace("\"1.0\"", "5"));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("OW-WF-001", result.Diagnostics[0].Code);
        }

        // SPEC §N.3 item 3: same major, newer minor reads with a warning.
        [Test]
        public void NewerMinorSchemaVersionReadsWithWarning()
        {
            var result = WorkflowSerializer.Read(Document(
                "\"nodes\":[],\"controlEdges\":[]").Replace("\"1.0\"", "\"1.1\""));

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual("1.1", result.Value!.SchemaVersion);
            AssertLocations(result.Diagnostics);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual("OW-WF-002", diagnostic.Code);
            Assert.AreEqual(Severity.Warning, diagnostic.Severity);
            Assert.AreEqual("$.schemaVersion", diagnostic.JsonPath);
            Assert.IsNotNull(diagnostic.Hint);
        }

        // ── OW-WF-003 and structural mapping ────────────────────────────────────

        [Test]
        public void MissingRequiredMembersAreOwWf003WithLocations()
        {
            var result = WorkflowSerializer.Read("{}");

            Assert.IsFalse(result.Ok);
            AssertLocations(result.Diagnostics);
            Assert.AreEqual(4, result.Diagnostics.Count);
            Assert.AreEqual("$.schemaVersion", result.Diagnostics[0].JsonPath);
            Assert.AreEqual("$.id", result.Diagnostics[1].JsonPath);
            Assert.AreEqual("$.nodes", result.Diagnostics[2].JsonPath);
            Assert.AreEqual("$.controlEdges", result.Diagnostics[3].JsonPath);
            foreach (var diagnostic in result.Diagnostics)
            {
                Assert.AreEqual("OW-WF-003", diagnostic.Code);
                Assert.AreEqual(Severity.Error, diagnostic.Severity);
            }
        }

        [Test]
        public void NonObjectRootSuppliesNoRequiredMembers()
        {
            var result = WorkflowSerializer.Read("[1,2]");

            Assert.IsFalse(result.Ok);
            AssertLocations(result.Diagnostics);
            foreach (var diagnostic in result.Diagnostics)
            {
                Assert.AreEqual("OW-WF-003", diagnostic.Code);
            }
        }

        [Test]
        public void MembersWithTheWrongJsonKindAreOwWf001()
        {
            // A malformed member is OW-WF-001; Create adds OW-WF-003 when the member
            // is required (nodes), a well-formed optional member (tags) stays single.
            var result = WorkflowSerializer.Read(Document("\"nodes\":{},\"controlEdges\":[]"));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("OW-WF-001", result.Diagnostics[0].Code);
            Assert.AreEqual("$.nodes", result.Diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-003", result.Diagnostics[1].Code);
            Assert.AreEqual("$.nodes", result.Diagnostics[1].JsonPath);
            AssertLocations(result.Diagnostics);

            result = WorkflowSerializer.Read(
                Document("\"nodes\":[],\"controlEdges\":[],\"tags\":[1]"));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual("OW-WF-001", result.Diagnostics[0].Code);
            Assert.AreEqual("$.tags[0]", result.Diagnostics[0].JsonPath);
            AssertLocations(result.Diagnostics);
        }

        [Test]
        public void NullMembersAreAbsentNotValues()
        {
            var result = WorkflowSerializer.Read(Document(
                "\"nodes\":[],\"controlEdges\":[],\"title\":null,\"layout\":null,\"tags\":null"));

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.IsNull(result.Value!.Title);
            Assert.IsNull(result.Value.Layout);
            Assert.AreEqual(0, result.Value.Tags.Count);
        }

        // ── read → model round trip ─────────────────────────────────────────────

        [Test]
        public void ReadResultEqualsEquivalentCreatedDocument()
        {
            var document = MustRead(FullFixture);

            // Equality is canonical (SPEC §A1): a programmatically created document
            // with the same semantic content is equal regardless of x-*/layout.
            var data = new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.full",
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData
                    {
                        Id = "create",
                        Operation = "unity.gameobject.create",
                        Version = 1,
                        Parameters = new Dictionary<string, Newtonsoft.Json.Linq.JToken>
                        {
                            ["name"] = new Newtonsoft.Json.Linq.JValue("Player"),
                        },
                    },
                    new NodeData
                    {
                        Id = "toPrefab",
                        Operation = "unity.prefab.create_from_gameobject",
                        Version = 2,
                    },
                },
                ControlEdges = new List<ControlEdge>
                {
                    new ControlEdge("start", "create"),
                    new ControlEdge("create", "toPrefab"),
                },
            };
            var created = WorkflowDocument.Create(data);
            Assert.IsTrue(created.Ok);
            Assert.AreNotEqual(document, created.Value!);

            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("create", "gameObject"), new PortRef("toPrefab", "source")),
            };
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("scene", "unity.SceneRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("scene", new PortRef("create", "scene")),
            };
            data.Nodes![2].Parameters = new Dictionary<string, Newtonsoft.Json.Linq.JToken>
            {
                ["path"] = new Newtonsoft.Json.Linq.JValue("Assets/Prefabs/Player.prefab"),
                ["cfg"] = Newtonsoft.Json.Linq.JToken.Parse("{\"z\":1.5,\"a\":null,\"m\":[true,0.25]}"),
                ["explicitNull"] = Newtonsoft.Json.Linq.JValue.CreateNull(),
            };
            data.Nodes[2].Label = "作成 ノード";
            data.Nodes[2].Policy = new NodePolicy(60, 3);
            data.Inputs.Add(new WorkflowInput(
                "name", "core.Json", false,
                JsonValue.Parse("{\"a\":1,\"b\":2}").Value!, "a default"));
            data.Inputs.Add(new WorkflowInput(
                "opt", "core.Json", false, JsonValue.Parse("null").Value!));
            data.Outputs = new List<WorkflowOutput>
            {
                new WorkflowOutput("prefab", new PortRef("toPrefab", "prefab")),
            };
            created = WorkflowDocument.Create(data);
            Assert.IsTrue(created.Ok);
            Assert.AreEqual(document, created.Value!);
        }

        private static Diagnostic SingleDiagnosticFrom(Result<WorkflowDocument> result)
        {
            Assert.AreEqual(1, result.Diagnostics.Count, Describe(result.Diagnostics));
            return result.Diagnostics[0];
        }
    }
}
