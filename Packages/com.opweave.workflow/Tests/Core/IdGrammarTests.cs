#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Model;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §A4 ID grammar and in-workflow uniqueness:
    /// regex boundary fixtures (WF id ≤128, scoped ids ≤64, Operation id) and the
    /// OW-WF-005 (format) / OW-WF-004 (duplicate) diagnostics
    /// <see cref="IdGrammar.Validate"/> reports with a JsonPath.
    /// </summary>
    internal sealed class IdGrammarTests
    {
        private static WorkflowDocumentData MinimalData() => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.ids",
            Nodes = new List<NodeData>
            {
                new NodeData { Id = "start", Operation = "core.start", Version = 1 },
            },
            ControlEdges = new List<ControlEdge>(),
        };

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", System.Linq.Enumerable.Select(
                    result.Diagnostics, d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        // ── SPEC §A4 — WF id grammar `^[a-z0-9]+([._-][a-z0-9]+)*$` (≤128) ────────

        [TestCase("a")]
        [TestCase("example.create-player")]
        [TestCase("a1_b-c.d")]
        [TestCase("a.b_c-d1")]
        public void ValidWorkflowIds(string id) =>
            Assert.IsTrue(IdGrammar.IsValidWorkflowId(id), id);

        [TestCase("")]
        [TestCase("A")]                       // uppercase not allowed
        [TestCase("-a")]                      // leading separator
        [TestCase("a-")]                      // trailing separator
        [TestCase("a..b")]                    // empty segment
        [TestCase("a é")]                     // non-ASCII
        [TestCase("a b")]                     // space
        public void InvalidWorkflowIds(string id) =>
            Assert.IsFalse(IdGrammar.IsValidWorkflowId(id), id);

        [Test]
        public void WorkflowIdBoundaryLength()
        {
            var within = new string('a', IdGrammar.WorkflowIdMaxLength);
            var beyond = new string('a', IdGrammar.WorkflowIdMaxLength + 1);
            Assert.AreEqual(128, IdGrammar.WorkflowIdMaxLength);
            Assert.IsTrue(IdGrammar.IsValidWorkflowId(within));
            Assert.IsFalse(IdGrammar.IsValidWorkflowId(beyond));
            Assert.IsFalse(IdGrammar.IsValidWorkflowId(null));
        }

        // ── SPEC §A4 — scoped id grammar `^[A-Za-z][A-Za-z0-9_]{0,63}$` ──────────
        // (node / input / output ids, port and control-port names)

        [TestCase("a")]
        [TestCase("A")]
        [TestCase("aB9_")]
        [TestCase("next")]
        public void ValidScopedIds(string id) =>
            Assert.IsTrue(IdGrammar.IsValidScopedId(id), id);

        [TestCase("")]
        [TestCase("1a")]                      // must start with a letter
        [TestCase("_a")]                      // must start with a letter
        [TestCase("a-b")]                     // hyphen not allowed
        [TestCase("a.b")]                     // dot not allowed
        [TestCase("a b")]
        public void InvalidScopedIds(string id) =>
            Assert.IsFalse(IdGrammar.IsValidScopedId(id), id);

        [Test]
        public void ScopedIdBoundaryLength()
        {
            Assert.IsTrue(IdGrammar.IsValidScopedId(new string('a', 64)));
            Assert.IsFalse(IdGrammar.IsValidScopedId(new string('a', 65)));
            Assert.IsFalse(IdGrammar.IsValidScopedId(null));
        }

        // ── SPEC §A4 — Operation id `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$` ───────

        [TestCase("core.start")]
        [TestCase("unity.prefab.create_from_gameobject")]
        [TestCase("proj.my_op.run2")]
        [TestCase("a.b")]
        public void ValidOperationIds(string id) =>
            Assert.IsTrue(IdGrammar.IsValidOperationId(id), id);

        [TestCase("")]
        [TestCase("a")]                       // needs at least one dot
        [TestCase(".a.b")]                    // leading separator
        [TestCase("a..b")]                    // empty segment
        [TestCase("a.B")]                     // uppercase segment start
        [TestCase("A.b")]                     // uppercase first segment
        [TestCase("a.1b")]                    // segment must start with a letter
        [TestCase("a.b-")]                    // hyphen not allowed
        public void InvalidOperationIds(string id) =>
            Assert.IsFalse(IdGrammar.IsValidOperationId(id), id);

        [Test]
        public void OperationIdBoundary() =>
            Assert.IsFalse(IdGrammar.IsValidOperationId(null));

        // SPEC §A4 (2026-11-16): every Operation id — standard or custom,
        // generated or hand-written — is bounded at 128 UTF-16 code units.
        [Test]
        public void OperationIdBoundaryLength()
        {
            Assert.AreEqual(128, IdGrammar.OperationIdMaxLength);
            Assert.IsTrue(IdGrammar.IsValidOperationId("a." + new string('b', 125)));   // 127 chars
            Assert.IsTrue(IdGrammar.IsValidOperationId("a." + new string('b', 126)));   // 128 chars
            Assert.IsFalse(IdGrammar.IsValidOperationId("a." + new string('b', 127)));  // 129 chars
            Assert.IsFalse(IdGrammar.IsValidOperationId(string.Empty));
        }

        // ── SPEC §A4 — reserved prefixes core./unity./opweave. (OW-OP-007) ────────

        [TestCase("core.start", true)]
        [TestCase("unity.scene.save", true)]
        [TestCase("opweave.anything", true)]
        [TestCase("proj.custom.op", false)]
        [TestCase("corex.op", false)]         // prefix without the dot
        [TestCase("core", false)]             // bare word is not the reserved prefix
        [TestCase("a.core.b", false)]         // not a leading prefix
        public void ReservedOperationPrefix(string id, bool expected) =>
            Assert.AreEqual(expected, IdGrammar.HasReservedOperationPrefix(id), id);

        [Test]
        public void ReservedOperationPrefixNull() =>
            Assert.IsFalse(IdGrammar.HasReservedOperationPrefix(null));

        // ── SPEC §A4 — Validate on a whole document ──────────────────────────────

        [Test]
        public void ValidDocumentProducesNoDiagnostics()
        {
            var data = MinimalData();
            data.Nodes!.Add(new NodeData { Id = "work", Operation = "unity.gameobject.create", Version = 1 });
            data.ControlEdges!.Add(new ControlEdge("start", "work"));
            data.Inputs = new List<WorkflowInput> { new WorkflowInput("scene", "unity.SceneRef", true) };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("scene", new PortRef("work", "scene")),
            };
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("work", "gameObject"), new PortRef("start", "target")),
            };
            data.Outputs = new List<WorkflowOutput>
            {
                new WorkflowOutput("result", new PortRef("work", "gameObject")),
            };

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(0, diagnostics.Count);
        }

        [Test]
        public void MalformedWorkflowIdReturnsOwWf005()
        {
            var data = MinimalData();
            data.Id = new string('a', 129);

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual(Severity.Error, diagnostics[0].Severity);
            Assert.AreEqual("$.id", diagnostics[0].JsonPath);
        }

        [TestCase("9bad", "$.nodes[1].id")]            // starts with a digit
        [TestCase("bad-id", "$.nodes[1].id")]          // hyphen not allowed
        [TestCase("bad id", "$.nodes[1].id")]          // space not allowed
        public void MalformedNodeIdReturnsOwWf005(string id, string path)
        {
            var data = MinimalData();
            data.Nodes!.Add(new NodeData { Id = id, Operation = "core.start", Version = 1 });

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual(path, diagnostics[0].JsonPath);
        }

        [Test]
        public void NodeIdBoundaryLength()
        {
            var data = MinimalData();
            data.Nodes!.Add(new NodeData
            {
                Id = new string('a', 65), Operation = "core.start", Version = 1,
            });

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual("$.nodes[1].id", diagnostics[0].JsonPath);
        }

        [Test]
        public void DuplicateNodeIdsReturnOwWf004AtTheLaterOccurrence()
        {
            var data = MinimalData();
            data.Nodes!.Add(new NodeData { Id = "start", Operation = "core.start", Version = 1 });
            data.Nodes.Add(new NodeData { Id = "start", Operation = "core.start", Version = 1 });

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual("OW-WF-004", diagnostics[0].Code);
            Assert.AreEqual("$.nodes[1].id", diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-004", diagnostics[1].Code);
            Assert.AreEqual("$.nodes[2].id", diagnostics[1].JsonPath);
        }

        [Test]
        public void DuplicateInputAndOutputIdsReturnOwWf004()
        {
            var data = MinimalData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("i", "core.Json", false),
                new WorkflowInput("i", "core.Json", true),
            };
            data.Outputs = new List<WorkflowOutput>
            {
                new WorkflowOutput("o", new PortRef("start", "p")),
                new WorkflowOutput("o", new PortRef("start", "q")),
            };

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual("OW-WF-004", diagnostics[0].Code);
            Assert.AreEqual("$.inputs[1].id", diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-004", diagnostics[1].Code);
            Assert.AreEqual("$.outputs[1].id", diagnostics[1].JsonPath);
        }

        // SPEC §A4 gives node/input/output ids one shared uniqueness scope "WF 内":
        // an input id that repeats a node id is a duplicate too.
        [Test]
        public void IdSharedAcrossKindsIsADuplicateWithinTheWorkflow()
        {
            var data = MinimalData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("start", "core.Json", false),
            };

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("OW-WF-004", diagnostics[0].Code);
            Assert.AreEqual("$.inputs[0].id", diagnostics[0].JsonPath);
            StringAssert.Contains("$.nodes[0].id", diagnostics[0].Hint);
        }

        [Test]
        public void MalformedOperationIdReturnsOwWf005()
        {
            var data = MinimalData();
            data.Nodes![0].Operation = "NoDot";

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual("$.nodes[0].operation", diagnostics[0].JsonPath);
        }

        // SPEC §A4 test (ID, 2026-11-16): a grammar-valid Operation id of
        // exactly 128 characters passes WF format validation; 129 characters
        // is OW-WF-005 at $.nodes[i].operation.
        [Test]
        public void OperationIdAt128CharactersPassesFormatValidation()
        {
            var data = MinimalData();
            data.Nodes![0].Operation = "a." + new string('b', 126);   // 128 chars, grammar-valid

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(0, diagnostics.Count);
        }

        [Test]
        public void OperationIdAt129CharactersReturnsOwWf005()
        {
            var data = MinimalData();
            data.Nodes![0].Operation = "a." + new string('b', 127);   // 129 chars, grammar-valid shape

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual(Severity.Error, diagnostics[0].Severity);
            Assert.AreEqual("$.nodes[0].operation", diagnostics[0].JsonPath);
            StringAssert.Contains("128", diagnostics[0].Hint);
        }

        // A 129-character id that also violates the regex is one OW-WF-005
        // for the member, not two reports for the two reasons.
        [Test]
        public void OverLongMalformedOperationIdReportsOwWf005Once()
        {
            var data = MinimalData();
            data.Nodes![0].Operation = "A." + new string('b', 127);   // 129 chars, uppercase too

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual("$.nodes[0].operation", diagnostics[0].JsonPath);
        }

        // Parameter keys are user-provided member names: keys that are plain
        // identifiers keep dotted JsonPath notation, everything else uses the
        // escaped bracket form of SPEC §0.6 SG-18 ($.policy.agent["code.write"]).
        [TestCase("_bad", "$.nodes[0].parameters._bad")]
        [TestCase("bad-key", "$.nodes[0].parameters[\"bad-key\"]")]
        [TestCase("a.b", "$.nodes[0].parameters[\"a.b\"]")]
        [TestCase("a\"b", "$.nodes[0].parameters[\"a\\\"b\"]")]
        [TestCase("a[b", "$.nodes[0].parameters[\"a[b\"]")]
        [TestCase("a\\b", "$.nodes[0].parameters[\"a\\\\b\"]")]
        public void MalformedParameterKeyReturnsOwWf005(string key, string path)
        {
            var data = MinimalData();
            data.Nodes![0].Parameters = new Dictionary<string, JToken>
            {
                [key] = new JValue(1L),
            };

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual(path, diagnostics[0].JsonPath);
        }

        [Test]
        public void MalformedControlEdgeMembersReturnOwWf005()
        {
            var data = MinimalData();
            data.ControlEdges!.Add(new ControlEdge("bad id", "start", "bad-port"));

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual("$.controlEdges[0].from", diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-005", diagnostics[1].Code);
            Assert.AreEqual("$.controlEdges[0].fromPort", diagnostics[1].JsonPath);
        }

        [Test]
        public void MalformedDataEdgePortsReturnOwWf005()
        {
            var data = MinimalData();
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("start", "bad port"), new PortRef("1x", "p")),
            };

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual("$.dataEdges[0].from.port", diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-005", diagnostics[1].Code);
            Assert.AreEqual("$.dataEdges[0].to.node", diagnostics[1].JsonPath);
        }

        [Test]
        public void MalformedInputBindingMembersReturnOwWf005()
        {
            var data = MinimalData();
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("0input", new PortRef("start", "x.y")),
            };

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual("$.inputBindings[0].fromInput", diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-005", diagnostics[1].Code);
            Assert.AreEqual("$.inputBindings[0].to.port", diagnostics[1].JsonPath);
        }

        [Test]
        public void MalformedOutputPortRefReturnsOwWf005()
        {
            var data = MinimalData();
            data.Outputs = new List<WorkflowOutput>
            {
                new WorkflowOutput("o", new PortRef("no d", "port x")),
            };

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual("$.outputs[0].from.node", diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-005", diagnostics[1].Code);
            Assert.AreEqual("$.outputs[0].from.port", diagnostics[1].JsonPath);
        }

        [Test]
        public void RepeatedMalformedIdReportsOwWf005NotOwWf004()
        {
            var data = MinimalData();
            data.Nodes!.Add(new NodeData { Id = "bad-id", Operation = "core.start", Version = 1 });
            data.Nodes.Add(new NodeData { Id = "bad-id", Operation = "core.start", Version = 1 });

            var diagnostics = IdGrammar.Validate(MustCreate(data));

            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual("OW-WF-005", diagnostics[0].Code);
            Assert.AreEqual("$.nodes[1].id", diagnostics[0].JsonPath);
            Assert.AreEqual("OW-WF-005", diagnostics[1].Code);
            Assert.AreEqual("$.nodes[2].id", diagnostics[1].JsonPath);
        }
    }
}
