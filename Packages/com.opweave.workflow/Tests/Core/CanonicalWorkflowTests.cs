#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Model;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the §A4 canonical form and <c>WorkflowHash</c>
    /// (SPEC §A4, §D0.2, §0.6 SG-12): the §A4 invariance tests (layout-only,
    /// metadata-only — including §A6 descriptions — node order and parameter
    /// key order differences keep the canonical form; an omitted fromPort
    /// equals an explicit <c>"next"</c>), the §A1 node-label difference that
    /// changes it, the §A1 test-1 canonical equality of
    /// <see cref="WorkflowDocument"/>, the pinned canonical object shape and
    /// edge ordering keys, and golden canonical-byte/WorkflowHash
    /// vectors for fixed fixtures — identical on dotnet and Unity Mono
    /// (local:T-W1.3-02:canonical-workflow).
    /// </summary>
    internal sealed class CanonicalWorkflowTests
    {
        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", System.Linq.Enumerable.Select(
                    result.Diagnostics, d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static WorkflowDocumentData MinimalData() => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.canonical.minimal",
            Nodes = new List<NodeData>
            {
                new NodeData { Id = "start", Operation = "core.start", Version = 1 },
            },
            ControlEdges = new List<ControlEdge>(),
        };

        // The fixed "full" fixture: unordered collections, metadata that must be
        // dropped and every edge/binding sort key exercised.
        private static WorkflowDocumentData FullData() => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.canonical.full",
            Title = "Golden",
            Description = "dropped metadata",
            Tags = new List<string> { "t1", "t2" },
            Layout = new LayoutInfo(
                new Dictionary<string, LayoutNodePosition>
                {
                    ["n1"] = new LayoutNodePosition(1.0, 2.0),
                },
                new List<LayoutNote> { new LayoutNote("note1", 0.0, 0.0, "text") }),
            Extensions = new Dictionary<string, JToken> { ["x-vendor"] = new JValue(7L) },
            Inputs = new List<WorkflowInput>
            {
                new WorkflowInput(
                    "b", "core.Json", false,
                    JsonValue.Parse("{\"k2\":2,\"k1\":1}").Value!, "input b"),
                new WorkflowInput("a", "core.Json", true),
            },
            Outputs = new List<WorkflowOutput>
            {
                new WorkflowOutput("z", new PortRef("n2", "p"), "z out"),
                new WorkflowOutput("y", new PortRef("n1", "q")),
            },
            Nodes = new List<NodeData>
            {
                new NodeData
                {
                    Id = "n2",
                    Operation = "unity.scene.save",
                    Version = 2,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["b"] = new JValue(true),
                        ["a"] = new JValue(1L),
                    },
                    Label = "Second",
                    Policy = new NodePolicy(60, 2),
                },
                new NodeData
                {
                    Id = "n3",
                    Operation = "unity.gameobject.create",
                    Version = 1,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["name"] = new JValue("Player"),
                    },
                },
                new NodeData
                {
                    Id = "n1",
                    Operation = "core.start",
                    Version = 1,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["p"] = new JObject(
                            new JProperty("x", new JValue(1L)),
                            new JProperty("y", new JValue(2L))),
                    },
                },
            },
            ControlEdges = new List<ControlEdge>
            {
                new ControlEdge("n2", "n3"),
                new ControlEdge("n1", "n2", "a2"),
                new ControlEdge("n1", "n3", "a1"),
                new ControlEdge("n1", "n2", "a1"),
            },
            DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("n1", "b"), new PortRef("n2", "x")),
                new DataEdge(new PortRef("n1", "a"), new PortRef("n3", "z")),
                new DataEdge(new PortRef("n1", "a"), new PortRef("n2", "y")),
                new DataEdge(new PortRef("n1", "a"), new PortRef("n2", "x")),
            },
            InputBindings = new List<InputBinding>
            {
                new InputBinding("b", new PortRef("n2", "p2")),
                new InputBinding("b", new PortRef("n1", "p1")),
                new InputBinding("a", new PortRef("n3", "p3")),
            },
        };

        // ── Golden vectors (canonical bytes + WorkflowHash), verified with an
        // independent SHA-256 of the UTF-8 bytes — pinned per SPEC §0.6 SG-12. ───

        private const string MinimalCanonicalText =
            "{\"controlEdges\":[],\"dataEdges\":[],\"id\":\"example.canonical.minimal\"," +
            "\"inputBindings\":[],\"inputs\":[],\"nodes\":[{\"id\":\"start\",\"operation\":\"core.start\"," +
            "\"parameters\":{},\"version\":1}],\"outputs\":[],\"schemaVersion\":\"1.0\"}";

        private const string MinimalWorkflowHash =
            "8bf9b9d3626995ab45d3fc3fafe042355714c6986cfc56c8f66b003275fb62ae";

        // Hand-derived from the SPEC rules, independent of the implementation:
        //
        //   §0.6 SG-12 / §A4 — the canonical form drops layout/title/description/
        //   tags/x-* metadata, so this fixture's document Title/Description/Tags/
        //   Layout/Extensions vanish, and so do the §A6 members that share the
        //   excluded name: input "b"'s description and output "z"'s description.
        //   Node "n2"'s label is a §A1 NodeModel field neither clause excludes,
        //   so it stays. What remains per level: input
        //   {id,type,required,default?}, output {id,from}, node
        //   {id,operation,version,parameters,label?,policy?}.
        //
        //   Ordering — the eight top-level members and every object's keys are
        //   UTF-16 Ordinal (here alphabetical) per §0.3: controlEdges < dataEdges <
        //   id < inputBindings < inputs < nodes < outputs < schemaVersion. Inputs
        //   and outputs sort by id (a<b, y<z), nodes by id (n1<n2<n3). Control
        //   edges sort by (from,fromPort,to) → (n1,a1,n2),(n1,a1,n3),(n1,a2,n2),
        //   (n2,next,n3) with the omitted fromPort of n2→n3 made explicit "next".
        //   Data edges sort by (from.node,from.port,to.node,to.port) →
        //   (n1,a)→(n2,x),(n1,a)→(n2,y),(n1,a)→(n3,z),(n1,b)→(n2,x). Bindings sort
        //   by (fromInput,to.node,to.port) → (a,n3,p3),(b,n1,p1),(b,n2,p2). The
        //   default object {"k2":2,"k1":1} and every parameters object serialize
        //   with §0.3 key order inside the canonical bytes.
        private const string FullCanonicalText =
            "{\"controlEdges\":[" +
            "{\"from\":\"n1\",\"fromPort\":\"a1\",\"to\":\"n2\"}," +
            "{\"from\":\"n1\",\"fromPort\":\"a1\",\"to\":\"n3\"}," +
            "{\"from\":\"n1\",\"fromPort\":\"a2\",\"to\":\"n2\"}," +
            "{\"from\":\"n2\",\"fromPort\":\"next\",\"to\":\"n3\"}]," +
            "\"dataEdges\":[" +
            "{\"from\":{\"node\":\"n1\",\"port\":\"a\"},\"to\":{\"node\":\"n2\",\"port\":\"x\"}}," +
            "{\"from\":{\"node\":\"n1\",\"port\":\"a\"},\"to\":{\"node\":\"n2\",\"port\":\"y\"}}," +
            "{\"from\":{\"node\":\"n1\",\"port\":\"a\"},\"to\":{\"node\":\"n3\",\"port\":\"z\"}}," +
            "{\"from\":{\"node\":\"n1\",\"port\":\"b\"},\"to\":{\"node\":\"n2\",\"port\":\"x\"}}]," +
            "\"id\":\"example.canonical.full\"," +
            "\"inputBindings\":[" +
            "{\"fromInput\":\"a\",\"to\":{\"node\":\"n3\",\"port\":\"p3\"}}," +
            "{\"fromInput\":\"b\",\"to\":{\"node\":\"n1\",\"port\":\"p1\"}}," +
            "{\"fromInput\":\"b\",\"to\":{\"node\":\"n2\",\"port\":\"p2\"}}]," +
            "\"inputs\":[" +
            "{\"id\":\"a\",\"required\":true,\"type\":\"core.Json\"}," +
            "{\"default\":{\"k1\":1,\"k2\":2},\"id\":\"b\",\"required\":false,\"type\":\"core.Json\"}]," +
            "\"nodes\":[" +
            "{\"id\":\"n1\",\"operation\":\"core.start\",\"parameters\":{\"p\":{\"x\":1,\"y\":2}},\"version\":1}," +
            "{\"id\":\"n2\",\"label\":\"Second\",\"operation\":\"unity.scene.save\"," +
            "\"parameters\":{\"a\":1,\"b\":true}," +
            "\"policy\":{\"maxAttempts\":2,\"timeoutSeconds\":60},\"version\":2}," +
            "{\"id\":\"n3\",\"operation\":\"unity.gameobject.create\",\"parameters\":{\"name\":\"Player\"}," +
            "\"version\":1}]," +
            "\"outputs\":[" +
            "{\"from\":{\"node\":\"n1\",\"port\":\"q\"},\"id\":\"y\"}," +
            "{\"from\":{\"node\":\"n2\",\"port\":\"p\"},\"id\":\"z\"}]," +
            "\"schemaVersion\":\"1.0\"}";

        private const string FullWorkflowHash =
            "c6c59fdb2bffa8a5b1cf0c6d8adc7b7f6e8a1b5d4c04f385a9dc8f62cc72678d";

        // Numbers keep their written typed form: 0.5 → "5e-1", -0.25 → "-25e-2";
        // explicit null parameters stay null; null policy members are omitted.
        private const string Vector2CanonicalText =
            "{\"controlEdges\":[],\"dataEdges\":[],\"id\":\"example.canonical.vector2\"," +
            "\"inputBindings\":[],\"inputs\":[],\"nodes\":[{\"id\":\"n\"," +
            "\"operation\":\"unity.gameobject.create\"," +
            "\"parameters\":{\"nullval\":null,\"pos\":{\"x\":5e-1,\"y\":-25e-2}}," +
            "\"policy\":{\"maxAttempts\":3},\"version\":3}],\"outputs\":[],\"schemaVersion\":\"1.0\"}";

        private const string Vector2WorkflowHash =
            "17b085cca8aac96c23518571c51a1158616d43b39b9fb0af20d8e348d6241038";

        [Test]
        public void GoldenVectorMinimalDocument()
        {
            var canonical = MustCreate(MinimalData()).Canonical;

            Assert.AreEqual(MinimalCanonicalText, canonical.CanonicalText);
            Assert.AreEqual(MinimalWorkflowHash, canonical.WorkflowHash);
        }

        // The golden bytes pin the object shape (the SG-12 field set with the
        // §A4-excluded description members absent) and every sort key:
        // control edges by (from, fromPort, to), data edges by
        // (from.node, from.port, to.node, to.port), input bindings by
        // (fromInput, to.node, to.port), and nodes/inputs/outputs by id.
        [Test]
        public void GoldenVectorFullDocument()
        {
            var canonical = MustCreate(FullData()).Canonical;

            Assert.AreEqual(FullCanonicalText, canonical.CanonicalText);
            Assert.AreEqual(FullWorkflowHash, canonical.WorkflowHash);
        }

        [Test]
        public void GoldenVectorTypedNumbersAndNulls()
        {
            var data = new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.canonical.vector2",
                Nodes = new List<NodeData>
                {
                    new NodeData
                    {
                        Id = "n",
                        Operation = "unity.gameobject.create",
                        Version = 3,
                        Parameters = new Dictionary<string, JToken>
                        {
                            ["pos"] = new JObject(
                                new JProperty("x", new JValue(0.5)),
                                new JProperty("y", new JValue(-0.25))),
                            ["nullval"] = JValue.CreateNull(),
                        },
                        Policy = new NodePolicy(null, 3),
                    },
                },
                ControlEdges = new List<ControlEdge>(),
            };

            var canonical = MustCreate(data).Canonical;

            Assert.AreEqual(Vector2CanonicalText, canonical.CanonicalText);
            Assert.AreEqual(Vector2WorkflowHash, canonical.WorkflowHash);
        }

        // ── SPEC §A4 tests: canonical-form invariance ───────────────────────────

        // Documents differing only in layout have equal canonical forms.
        [Test]
        public void LayoutOnlyDifferenceKeepsCanonicalForm()
        {
            var without = MustCreate(FullData());
            var data = FullData();
            data.Layout = new LayoutInfo(
                new Dictionary<string, LayoutNodePosition>
                {
                    ["n1"] = new LayoutNodePosition(99.0, -7.0),
                    ["n3"] = new LayoutNodePosition(0.0, 0.0),
                },
                new List<LayoutNote>
                {
                    new LayoutNote("other", 5.0, 5.0, "changed"),
                    new LayoutNote("extra", 1.0, 1.0, "added"),
                });
            var withLayout = MustCreate(data);

            Assert.AreEqual(without.Canonical.CanonicalText, withLayout.Canonical.CanonicalText);
            Assert.AreEqual(without.Canonical.WorkflowHash, withLayout.Canonical.WorkflowHash);
            Assert.AreEqual(without, withLayout);
        }

        // Documents differing only in metadata (title/description/tags) and x-*
        // extensions have equal canonical forms.
        [Test]
        public void MetadataAndExtensionDifferenceKeepsCanonicalForm()
        {
            var baseline = MustCreate(FullData());
            var data = FullData();
            data.Title = "Completely different title";
            data.Description = null;
            data.Tags = null;
            data.Extensions = new Dictionary<string, JToken>
            {
                ["x-other"] = new JObject(new JProperty("a", 1L), new JProperty("b", 2L)),
            };
            var changed = MustCreate(data);

            Assert.AreEqual(baseline.Canonical.CanonicalText, changed.Canonical.CanonicalText);
            Assert.AreEqual(baseline, changed);
        }

        // SPEC §A4/§0.6 SG-12: the excluded description metadata reaches into §A6
        // members too — documents differing only in an input description have
        // equal canonical forms and WorkflowHash values.
        [Test]
        public void InputDescriptionOnlyDifferenceKeepsCanonicalForm()
        {
            var baseline = MustCreate(FullData());
            var data = FullData();
            data.Inputs![0] = data.Inputs[0] with { Description = "rewritten input help" };
            var changed = MustCreate(data);

            Assert.AreEqual(baseline.Canonical.CanonicalText, changed.Canonical.CanonicalText);
            Assert.AreEqual(baseline.Canonical.WorkflowHash, changed.Canonical.WorkflowHash);
            Assert.AreEqual(baseline, changed);
        }

        // Same §A4/§0.6 SG-12 exclusion on the output side: only an output
        // description changes, so canonical equality and WorkflowHash hold.
        [Test]
        public void OutputDescriptionOnlyDifferenceKeepsCanonicalForm()
        {
            var baseline = MustCreate(FullData());
            var data = FullData();
            data.Outputs![0] = data.Outputs[0] with { Description = "rewritten output help" };
            var changed = MustCreate(data);

            Assert.AreEqual(baseline.Canonical.CanonicalText, changed.Canonical.CanonicalText);
            Assert.AreEqual(baseline.Canonical.WorkflowHash, changed.Canonical.WorkflowHash);
            Assert.AreEqual(baseline, changed);
        }

        // A node label is a §A1 NodeModel field that neither §A4 nor §0.6 SG-12
        // excludes — it is not display metadata under the canonical form — so a
        // label-only difference must change the canonical text and WorkflowHash.
        [Test]
        public void NodeLabelOnlyDifferenceChangesCanonicalForm()
        {
            var baseline = MustCreate(FullData());
            var data = FullData();
            data.Nodes![0].Label = "Renamed";
            var changed = MustCreate(data);

            Assert.AreNotEqual(baseline.Canonical.CanonicalText, changed.Canonical.CanonicalText);
            Assert.AreNotEqual(baseline.Canonical.WorkflowHash, changed.Canonical.WorkflowHash);
            Assert.AreNotEqual(baseline, changed);
        }

        // Documents differing only in node (and edge/binding/input/output) order
        // have equal canonical forms.
        [Test]
        public void CollectionOrderDifferenceKeepsCanonicalForm()
        {
            var baseline = MustCreate(FullData());
            var data = FullData();
            data.Nodes = data.Nodes!.Reverse().ToList();
            data.ControlEdges = data.ControlEdges!.Reverse().ToList();
            data.DataEdges = data.DataEdges!.Reverse().ToList();
            data.InputBindings = data.InputBindings!.Reverse().ToList();
            data.Inputs = data.Inputs!.Reverse().ToList();
            data.Outputs = data.Outputs!.Reverse().ToList();
            var reordered = MustCreate(data);

            Assert.AreEqual(baseline.Canonical.CanonicalText, reordered.Canonical.CanonicalText);
            Assert.AreEqual(baseline.Canonical.WorkflowHash, reordered.Canonical.WorkflowHash);
            Assert.AreEqual(baseline, reordered);
        }

        // Documents differing only in parameter key order have equal canonical forms.
        [Test]
        public void ParameterKeyOrderDifferenceKeepsCanonicalForm()
        {
            var baseline = MustCreate(FullData());
            var data = FullData();
            data.Nodes![0].Parameters = new Dictionary<string, JToken>
            {
                ["a"] = new JValue(1L),
                ["b"] = new JValue(true),
            };
            var reordered = MustCreate(data);

            Assert.AreEqual(baseline.Canonical.CanonicalText, reordered.Canonical.CanonicalText);
            Assert.AreEqual(baseline, reordered);
        }

        // SPEC §A4: a document omitting fromPort and one spelling "next" out have
        // equal canonical forms.
        [Test]
        public void OmittedFromPortEqualsExplicitNext()
        {
            var dataA = MinimalData();
            dataA.ControlEdges!.Add(new ControlEdge("start", "start"));
            var omitted = MustCreate(dataA);

            var dataB = MinimalData();
            dataB.ControlEdges!.Add(new ControlEdge("start", "start", "next"));
            var explicitNext = MustCreate(dataB);

            Assert.AreEqual(omitted.Canonical.CanonicalText, explicitNext.Canonical.CanonicalText);
            Assert.AreEqual(omitted, explicitNext);
        }

        // An omitted optional collection is identical to an explicit empty one —
        // the eight top-level members are always emitted.
        [Test]
        public void OmittedOptionalCollectionsEqualExplicitEmpty()
        {
            var baseline = MustCreate(FullData());
            var data = FullData();
            data.Inputs = null;
            data.Outputs = null;
            data.DataEdges = null;
            data.InputBindings = null;
            var without = MustCreate(data);

            data = FullData();
            data.Inputs = new List<WorkflowInput>();
            data.Outputs = new List<WorkflowOutput>();
            data.DataEdges = new List<DataEdge>();
            data.InputBindings = new List<InputBinding>();
            var empty = MustCreate(data);

            Assert.AreEqual(without.Canonical.CanonicalText, empty.Canonical.CanonicalText);
            Assert.AreEqual(without, empty);
            Assert.AreNotEqual(baseline.Canonical.CanonicalText, without.Canonical.CanonicalText);
        }

        // ── SPEC §A1 test 1: canonical equality on WorkflowDocument ─────────────

        [Test]
        public void DocumentsWithEqualContentAreEqual()
        {
            var a = MustCreate(FullData());
            var b = MustCreate(FullData());

            Assert.AreEqual(a, b);
            Assert.IsTrue(a.Equals(b));
            Assert.IsTrue(a.Equals((object)b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.IsFalse(a.Equals(null));
            Assert.IsFalse(a!.Equals((object?)null));
        }

        [Test]
        public void DocumentsWithDifferentContentAreNotEqual()
        {
            var a = MustCreate(FullData());
            var data = FullData();
            data.Id = "example.canonical.other";
            var b = MustCreate(data);

            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(a.Canonical.WorkflowHash, b.Canonical.WorkflowHash);
        }

        // SPEC §D0.2: a `unity.Vector3` parameter written `{"x":0,"y":1,"z":0}`
        // vs `{"x":0.0,"y":1.0,"z":0.0}` keeps the written form in the canonical
        // workflow, so the two documents have different WorkflowHash values
        // (they only converge after Codec resolution in ResolvedWorkflow).
        [Test]
        public void WrittenParameterFormChangesWorkflowHash()
        {
            var dataInt = MinimalData();
            dataInt.Nodes![0].Parameters = new Dictionary<string, JToken>
            {
                ["pos"] = new JObject(
                    new JProperty("x", new JValue(0L)),
                    new JProperty("y", new JValue(1L)),
                    new JProperty("z", new JValue(0L))),
            };
            var dataFloat = MinimalData();
            dataFloat.Nodes![0].Parameters = new Dictionary<string, JToken>
            {
                ["pos"] = new JObject(
                    new JProperty("x", new JValue(0.0)),
                    new JProperty("y", new JValue(1.0)),
                    new JProperty("z", new JValue(0.0))),
            };

            var intDoc = MustCreate(dataInt);
            var floatDoc = MustCreate(dataFloat);

            Assert.AreNotEqual(intDoc.Canonical.CanonicalText, floatDoc.Canonical.CanonicalText);
            Assert.AreNotEqual(intDoc.Canonical.WorkflowHash, floatDoc.Canonical.WorkflowHash);
            Assert.AreNotEqual(intDoc, floatDoc);
        }

        // ToToken returns the pinned canonical object shape (SG-12) as a fresh
        // clone: the eight top-level members, sorted collections, explicit
        // fromPort and no dropped members.
        [Test]
        public void CanonicalTokenHasThePinnedShape()
        {
            var token = MustCreate(FullData()).Canonical.ToToken();

            Assert.AreEqual(8, token.Count);
            foreach (var name in new[]
            {
                "schemaVersion", "id", "inputs", "outputs",
                "nodes", "controlEdges", "dataEdges", "inputBindings",
            })
            {
                Assert.IsNotNull(token[name], "missing member " + name);
            }
            Assert.AreEqual("next", token["controlEdges"]![3]!["fromPort"]!.ToString());
            Assert.AreEqual("n1", token["nodes"]![0]!["id"]!.ToString());
            Assert.AreEqual("a", token["inputs"]![0]!["id"]!.ToString());
            Assert.AreEqual("y", token["outputs"]![0]!["id"]!.ToString());
            Assert.AreEqual("1", token["nodes"]![1]!["parameters"]!["a"]!.ToString());
            Assert.AreEqual(true, token["nodes"]![1]!["parameters"]!["b"]!.ToObject<bool>());
            Assert.AreEqual("Second", token["nodes"]![1]!["label"]!.ToString());
            Assert.IsNull(token["inputs"]![1]!["description"], "input description is §A4-excluded");
            Assert.IsNull(token["outputs"]![1]!["description"], "output description is §A4-excluded");
            Assert.AreEqual(
                "60", token["nodes"]![1]!["policy"]!["timeoutSeconds"]!.ToString());
            Assert.AreEqual("2", token["nodes"]![1]!["policy"]!["maxAttempts"]!.ToString());
        }

        [Test]
        public void ToTokenCloneDoesNotAffectTheCanonicalForm()
        {
            var document = MustCreate(FullData());
            var token = document.Canonical.ToToken();
            token["nodes"]![0]!["id"] = "mutated";

            Assert.AreEqual(FullCanonicalText, document.Canonical.CanonicalText);
            Assert.AreEqual(FullWorkflowHash, document.Canonical.WorkflowHash);
        }
    }
}
