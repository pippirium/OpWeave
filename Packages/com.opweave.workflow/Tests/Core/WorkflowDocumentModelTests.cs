#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Model;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the immutable <see cref="WorkflowDocument"/> model (SPEC §A1
    /// item 1, §A6, §A8, §A0.1): the §A1 immutability test — mutating the JTokens,
    /// dictionaries and lists passed to <see cref="WorkflowDocument.Create"/>
    /// afterwards never changes the document — plus read-only exposed collections,
    /// explicit-null vs absent parameters, the <c>"x-"</c> extension-key rule and
    /// OW-WF-003 for missing required structure. The same sources run under dotnet
    /// and Unity Mono (local:T-W1.3-01:model-immutability).
    /// </summary>
    internal sealed class WorkflowDocumentModelTests
    {
        private static WorkflowDocumentData MinimalData() => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.min",
            Nodes = new List<NodeData>
            {
                new NodeData { Id = "start", Operation = "core.start", Version = 1 },
            },
            ControlEdges = new List<ControlEdge>(),
        };

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, $"Create failed: {Describe(result)}");
            return result.Value!;
        }

        // SPEC §A1: Create returns a document whose members equal the supplied data.
        [Test]
        public void CreateReturnsDocumentWhoseMembersEqualTheSuppliedData()
        {
            var inputDefault = JsonValue.Parse("{\"scene\":\"default\"}").Value!;
            var data = new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.create-player",
                Title = "Player",
                Description = "Build a player prefab",
                Tags = new List<string> { "player", "prefab" },
                Inputs = new List<WorkflowInput>
                {
                    new WorkflowInput("scene", "unity.SceneRef", true),
                    new WorkflowInput("name", "core.String", false, inputDefault, "display name"),
                },
                Outputs = new List<WorkflowOutput>
                {
                    new WorkflowOutput("prefab", new PortRef("toPrefab", "prefab"), "result"),
                },
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData
                    {
                        Id = "toPrefab",
                        Operation = "unity.prefab.create_from_gameobject",
                        Version = 1,
                        Parameters = new Dictionary<string, JToken>
                        {
                            ["path"] = new JValue("Assets/Prefabs/Player.prefab"),
                            ["source"] = new JObject(new JProperty("nested", new JArray(1L, true))),
                        },
                        Label = "To Prefab",
                        Policy = new NodePolicy(60, null),
                    },
                },
                ControlEdges = new List<ControlEdge>
                {
                    new ControlEdge("start", "toPrefab"),
                    new ControlEdge("toPrefab", "start", "next"),
                },
                DataEdges = new List<DataEdge>
                {
                    new DataEdge(new PortRef("create", "gameObject"), new PortRef("toPrefab", "source")),
                },
                InputBindings = new List<InputBinding>
                {
                    new InputBinding("scene", new PortRef("toPrefab", "scene")),
                },
                Layout = new LayoutInfo(
                    new Dictionary<string, LayoutNodePosition>
                    {
                        ["start"] = new LayoutNodePosition(10.5, -3.0),
                    },
                    new List<LayoutNote> { new LayoutNote("n1", 1.0, 2.0, "hello") }),
                Extensions = new Dictionary<string, JToken>
                {
                    ["x-vendor.flag"] = new JValue(true),
                },
            };

            var document = MustCreate(data);

            Assert.AreEqual("1.0", document.SchemaVersion);
            Assert.AreEqual("example.create-player", document.Id);
            Assert.AreEqual("Player", document.Title);
            Assert.AreEqual("Build a player prefab", document.Description);
            Assert.AreEqual(new[] { "player", "prefab" }, document.Tags);

            Assert.AreEqual(2, document.Inputs.Count);
            Assert.AreEqual(new WorkflowInput("scene", "unity.SceneRef", true), document.Inputs[0]);
            Assert.AreEqual("name", document.Inputs[1].Id);
            Assert.AreEqual(inputDefault, document.Inputs[1].Default);
            Assert.AreEqual("display name", document.Inputs[1].Description);

            Assert.AreEqual(1, document.Outputs.Count);
            Assert.AreEqual(
                new WorkflowOutput("prefab", new PortRef("toPrefab", "prefab"), "result"),
                document.Outputs[0]);

            Assert.AreEqual(2, document.Nodes.Count);
            Assert.AreEqual("start", document.Nodes[0].Id);
            Assert.AreEqual("core.start", document.Nodes[0].Operation);
            Assert.AreEqual(1, document.Nodes[0].Version);
            var node = document.Nodes[1];
            Assert.AreEqual("unity.prefab.create_from_gameobject", node.Operation);
            Assert.AreEqual("To Prefab", node.Label);
            Assert.AreEqual(new NodePolicy(60, null), node.Policy);
            Assert.AreEqual(2, node.Parameters.Count);
            Assert.AreEqual("\"Assets/Prefabs/Player.prefab\"", node.Parameters["path"].CanonicalText);
            Assert.AreEqual("{\"nested\":[1,true]}", node.Parameters["source"].CanonicalText);

            Assert.AreEqual(2, document.ControlEdges.Count);
            Assert.AreEqual(new ControlEdge("start", "toPrefab"), document.ControlEdges[0]);
            Assert.AreEqual(new ControlEdge("toPrefab", "start", "next"), document.ControlEdges[1]);

            Assert.AreEqual(1, document.DataEdges.Count);
            Assert.AreEqual(
                new DataEdge(new PortRef("create", "gameObject"), new PortRef("toPrefab", "source")),
                document.DataEdges[0]);

            Assert.AreEqual(1, document.InputBindings.Count);
            Assert.AreEqual(
                new InputBinding("scene", new PortRef("toPrefab", "scene")),
                document.InputBindings[0]);

            Assert.IsNotNull(document.Layout);
            Assert.AreEqual(new LayoutNodePosition(10.5, -3.0), document.Layout!.Nodes["start"]);
            Assert.AreEqual(1, document.Layout.Notes.Count);
            Assert.AreEqual(new LayoutNote("n1", 1.0, 2.0, "hello"), document.Layout.Notes[0]);

            Assert.AreEqual(1, document.Extensions.Count);
            Assert.AreEqual("true", document.Extensions["x-vendor.flag"].CanonicalText);
        }

        [Test]
        public void OmittedOptionalMembersBecomeEmptyCollections()
        {
            var document = MustCreate(MinimalData());

            Assert.IsNull(document.Title);
            Assert.IsNull(document.Description);
            Assert.IsNull(document.Layout);
            Assert.AreEqual(0, document.Tags.Count);
            Assert.AreEqual(0, document.Inputs.Count);
            Assert.AreEqual(0, document.Outputs.Count);
            Assert.AreEqual(0, document.DataEdges.Count);
            Assert.AreEqual(0, document.InputBindings.Count);
            Assert.AreEqual(0, document.Extensions.Count);

            var node = document.Nodes[0];
            Assert.AreEqual(0, node.Parameters.Count);
            Assert.IsNull(node.Label);
            Assert.IsNull(node.Policy);
        }

        // SPEC §A1 item 1: ControlEdge.FromPort defaults to "next" (reserved for
        // appendix N); an explicit value is kept as given.
        [Test]
        public void OmittedFromPortDefaultsToNext()
        {
            var data = MinimalData();
            data.ControlEdges!.Add(new ControlEdge("start", "start"));
            var document = MustCreate(data);
            Assert.AreEqual("next", document.ControlEdges[0].FromPort);
            Assert.AreEqual(new ControlEdge("start", "start"), document.ControlEdges[0]);
        }

        // SPEC §A1 test 2: lists passed to Create can be mutated afterwards without
        // changing the document.
        [Test]
        public void MutatingInputListsAfterCreateLeavesDocumentUnchanged()
        {
            var tags = new List<string> { "a" };
            var inputs = new List<WorkflowInput> { new WorkflowInput("i1", "core.Json", false) };
            var outputs = new List<WorkflowOutput> { new WorkflowOutput("o1", new PortRef("start", "p")) };
            var nodes = new List<NodeData>
            {
                new NodeData { Id = "start", Operation = "core.start", Version = 1 },
            };
            var controlEdges = new List<ControlEdge> { new ControlEdge("start", "start") };
            var dataEdges = new List<DataEdge> { new DataEdge(new PortRef("start", "p"), new PortRef("start", "q")) };
            var inputBindings = new List<InputBinding> { new InputBinding("i1", new PortRef("start", "q")) };
            var data = new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.mutate-lists",
                Nodes = nodes,
                ControlEdges = controlEdges,
                Tags = tags,
                Inputs = inputs,
                Outputs = outputs,
                DataEdges = dataEdges,
                InputBindings = inputBindings,
            };

            var document = MustCreate(data);

            tags.Add("b");
            tags[0] = "z";
            inputs.Clear();
            outputs[0] = new WorkflowOutput("changed", new PortRef("x", "y"));
            nodes.Clear();
            nodes.Add(new NodeData { Id = "added", Operation = "core.start", Version = 2 });
            controlEdges.Clear();
            dataEdges.Clear();
            inputBindings.Clear();
            data.Id = "example.changed";

            Assert.AreEqual(new[] { "a" }, document.Tags);
            Assert.AreEqual(1, document.Inputs.Count);
            Assert.AreEqual("i1", document.Inputs[0].Id);
            Assert.AreEqual("o1", document.Outputs[0].Id);
            Assert.AreEqual(1, document.Nodes.Count);
            Assert.AreEqual("start", document.Nodes[0].Id);
            Assert.AreEqual(1, document.ControlEdges.Count);
            Assert.AreEqual(1, document.DataEdges.Count);
            Assert.AreEqual(1, document.InputBindings.Count);
            Assert.AreEqual("example.mutate-lists", document.Id);
        }

        // SPEC §A1 test 2: JTokens passed through parameters and Extensions are cloned
        // into JsonValue; later mutation cannot reach the document.
        [Test]
        public void MutatingInputTokensAfterCreateLeavesDocumentUnchanged()
        {
            var parameter = new JObject(
                new JProperty("name", "Player"),
                new JProperty("items", new JArray(1L, 2L)));
            var nested = (JArray)parameter["items"]!;
            var parameters = new Dictionary<string, JToken> { ["config"] = parameter };
            var extension = new JObject(new JProperty("on", true));
            var extensions = new Dictionary<string, JToken> { ["x-vendor"] = extension };
            var data = MinimalData();
            data.Nodes![0].Parameters = parameters;
            data.Extensions = extensions;

            var document = MustCreate(data);

            parameter["name"] = "mutated";
            parameter.Add(new JProperty("extra", 1L));
            nested[0] = 99L;
            parameters["added"] = new JValue(1L);
            parameters.Remove("config");
            extension["on"] = false;
            extension.Add(new JProperty("extra", "x"));
            extensions["x-added"] = new JValue(2L);
            extensions.Remove("x-vendor");

            Assert.AreEqual(
                "{\"items\":[1,2],\"name\":\"Player\"}",
                document.Nodes[0].Parameters["config"].CanonicalText);
            Assert.AreEqual(1, document.Nodes[0].Parameters.Count);
            Assert.AreEqual("{\"on\":true}", document.Extensions["x-vendor"].CanonicalText);
            Assert.AreEqual(1, document.Extensions.Count);
        }

        // SPEC §A1 test 2 applied to §A8 layout: the caller's layout collections are
        // copied too.
        [Test]
        public void MutatingLayoutCollectionsAfterCreateLeavesDocumentUnchanged()
        {
            var nodes = new Dictionary<string, LayoutNodePosition>
            {
                ["start"] = new LayoutNodePosition(1.0, 2.0),
            };
            var notes = new List<LayoutNote> { new LayoutNote("n1", 0.0, 0.0, "text") };
            var data = MinimalData();
            data.Layout = new LayoutInfo(nodes, notes);

            var document = MustCreate(data);

            nodes["start"] = new LayoutNodePosition(9.0, 9.0);
            nodes["other"] = new LayoutNodePosition(5.0, 5.0);
            notes.Add(new LayoutNote("n2", 1.0, 1.0, "added"));
            notes[0] = new LayoutNote("n1", 0.0, 0.0, "changed");

            Assert.AreEqual(1, document.Layout!.Nodes.Count);
            Assert.AreEqual(new LayoutNodePosition(1.0, 2.0), document.Layout.Nodes["start"]);
            Assert.AreEqual(1, document.Layout.Notes.Count);
            Assert.AreEqual("text", document.Layout.Notes[0].Text);
        }

        // SPEC §A1 item 1: collections are exposed through read-only wrappers that
        // cannot be mutated or cast back to the source arrays.
        [Test]
        public void ExposedCollectionsAreReadOnlyWrappers()
        {
            var data = MinimalData();
            data.Tags = new List<string> { "a" };
            data.ControlEdges!.Add(new ControlEdge("start", "start"));
            data.Nodes![0].Parameters = new Dictionary<string, JToken> { ["p"] = new JValue(1L) };
            data.Extensions = new Dictionary<string, JToken> { ["x-a"] = new JValue(2L) };
            var document = MustCreate(data);

            AssertReadOnlyList(document.Tags);
            AssertReadOnlyList(document.Inputs);
            AssertReadOnlyList(document.Outputs);
            AssertReadOnlyList(document.Nodes);
            AssertReadOnlyList(document.ControlEdges);
            AssertReadOnlyList(document.DataEdges);
            AssertReadOnlyList(document.InputBindings);
            AssertReadOnlyMap(document.Extensions);
            AssertReadOnlyMap(document.Nodes[0].Parameters);
        }

        // SPEC §0.1 F-07 / §A0.1: an explicit JSON null is a real JsonValue; an absent
        // key means no parameter and is never stored as null.
        [Test]
        public void ExplicitNullParameterIsPreservedAndAbsentKeysStayAbsent()
        {
            var data = MinimalData();
            data.Nodes![0].Parameters = new Dictionary<string, JToken>
            {
                ["explicitNull"] = JValue.CreateNull(),
                ["value"] = new JValue(3L),
            };
            var document = MustCreate(data);

            var parameters = document.Nodes[0].Parameters;
            Assert.AreEqual(2, parameters.Count);
            Assert.IsTrue(parameters.ContainsKey("explicitNull"));
            Assert.IsNotNull(parameters["explicitNull"]);
            Assert.AreEqual("null", parameters["explicitNull"].CanonicalText);
            Assert.IsFalse(parameters.ContainsKey("absent"));
            Assert.IsFalse(parameters.TryGetValue("absent", out var absent));
            Assert.IsNull(absent);
        }

        // Extensions hold only "x-" prefixed document members; anything else is an
        // unknown property the model cannot keep, so Create rejects it — OW-WF-006
        // keeps its catalog default Warning for ordinary keys and is an Error only
        // for reserved "$" keys (SPEC §A3, §A5, §J).
        [Test]
        public void XPrefixedExtensionKeysAreAccepted()
        {
            var data = MinimalData();
            data.Extensions = new Dictionary<string, JToken>
            {
                ["x-vendor.id"] = new JValue(1L),
                ["x-"] = JValue.CreateNull(),
            };
            var document = MustCreate(data);
            Assert.AreEqual(2, document.Extensions.Count);
            Assert.AreEqual("1", document.Extensions["x-vendor.id"].CanonicalText);
            Assert.AreEqual("null", document.Extensions["x-"].CanonicalText);
        }

        [TestCase("vendor.flag", Severity.Warning)]
        [TestCase("X-vendor", Severity.Warning)]
        [TestCase("x", Severity.Warning)]
        [TestCase("id", Severity.Warning)]
        [TestCase("$schema", Severity.Error)]
        [TestCase("$ref", Severity.Error)]
        public void NonXExtensionKeysAreRejected(string key, Severity expectedSeverity)
        {
            var data = MinimalData();
            data.Extensions = new Dictionary<string, JToken> { [key] = new JValue(1L) };

            var result = WorkflowDocument.Create(data);

            Assert.IsFalse(result.Ok);
            Assert.IsNull(result.Value);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual("OW-WF-006", result.Diagnostics[0].Code);
            Assert.AreEqual(expectedSeverity, result.Diagnostics[0].Severity);
            Assert.AreEqual("$." + key, result.Diagnostics[0].JsonPath);
        }

        // Missing required structure (SPEC §A2 required members, in nested records too)
        // fails with OW-WF-003 carrying a JsonPath.
        [Test]
        public void MissingRequiredStructureReturnsOwWf003()
        {
            ExpectMissing(MinimalData(), "$.schemaVersion", d => d.SchemaVersion = null);
            ExpectMissing(MinimalData(), "$.id", d => d.Id = null);
            ExpectMissing(MinimalData(), "$.nodes", d => d.Nodes = null);
            ExpectMissing(MinimalData(), "$.controlEdges", d => d.ControlEdges = null);
            ExpectMissing(MinimalData(), "$.nodes[0].id", d => d.Nodes![0].Id = null);
            ExpectMissing(MinimalData(), "$.nodes[0].operation", d => d.Nodes![0].Operation = null);
            ExpectMissing(MinimalData(), "$.nodes[1]", d => d.Nodes!.Add(null!));
            ExpectMissing(MinimalData(), "$.controlEdges[0].from",
                d => d.ControlEdges!.Add(new ControlEdge(null!, "start")));
            ExpectMissing(MinimalData(), "$.controlEdges[0].to",
                d => d.ControlEdges!.Add(new ControlEdge("start", null!)));
            ExpectMissing(MinimalData(), "$.controlEdges[0].fromPort",
                d => d.ControlEdges!.Add(new ControlEdge("a", "b", null!)));
            ExpectMissing(MinimalData(), "$.controlEdges[0]",
                d => d.ControlEdges!.Add(null!));
            ExpectMissing(MinimalData(), "$.tags[0]", d => d.Tags = new List<string> { null! });
            ExpectMissing(MinimalData(), "$.inputs[0].type",
                d => d.Inputs = new List<WorkflowInput> { new WorkflowInput("i", null!, false) });
            ExpectMissing(MinimalData(), "$.outputs[0].from",
                d => d.Outputs = new List<WorkflowOutput> { new WorkflowOutput("o", null!) });
            ExpectMissing(MinimalData(), "$.dataEdges[0].from.port",
                d => d.DataEdges = new List<DataEdge>
                {
                    new DataEdge(new PortRef("start", null!), new PortRef("start", "q")),
                });
            ExpectMissing(MinimalData(), "$.inputBindings[0].fromInput",
                d => d.InputBindings = new List<InputBinding>
                {
                    new InputBinding(null!, new PortRef("start", "q")),
                });
            ExpectMissing(MinimalData(), "$.nodes[0].parameters.p",
                d => d.Nodes![0].Parameters = new Dictionary<string, JToken> { ["p"] = null! });
            ExpectMissing(MinimalData(), "$.x-bad",
                d => d.Extensions = new Dictionary<string, JToken> { ["x-bad"] = null! });
            ExpectMissing(MinimalData(), "$.layout.notes[0].text",
                d => d.Layout = new LayoutInfo(
                    new Dictionary<string, LayoutNodePosition>(),
                    new List<LayoutNote> { new LayoutNote("n", 0.0, 0.0, null!) }));
        }

        [Test]
        public void MissingRequiredStructureCollectsAllDiagnosticsInDocumentOrder()
        {
            var data = new WorkflowDocumentData();
            var result = WorkflowDocument.Create(data);

            Assert.IsFalse(result.Ok);
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

        // SPEC §A1 item 1: sealed classes with get-only properties and no accessible
        // constructor — no with-expression path can bypass Create's checks.
        [Test]
        public void DocumentAndNodeHaveNoConstructionPathOutsideCreate()
        {
            foreach (var type in new[] { typeof(WorkflowDocument), typeof(NodeModel) })
            {
                Assert.IsTrue(type.IsSealed, type.Name + " must be sealed");
                Assert.AreEqual(0, type.GetConstructors().Length,
                    type.Name + " must have no public constructor");
                Assert.IsNull(
                    type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.NonPublic),
                    type.Name + " must not be a record");
                foreach (var property in type.GetProperties())
                {
                    Assert.IsNull(property.SetMethod,
                        type.Name + "." + property.Name + " must be get-only");
                }
            }
        }

        [Test]
        public void CreateRejectsNullData()
        {
            Assert.Throws<ArgumentNullException>(() => WorkflowDocument.Create(null!));
        }

        private static void AssertReadOnlyList<T>(IReadOnlyList<T> list)
        {
            Assert.IsNull(list as T[]);
            var mutable = (IList<T>)list;
            Assert.IsTrue(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable.Add(default!));
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
            if (mutable.Count != 0)
            {
                Assert.Throws<NotSupportedException>(() => mutable[0] = default!);
            }
        }

        private static void AssertReadOnlyMap<TValue>(IReadOnlyDictionary<string, TValue> map)
        {
            var mutable = (IDictionary<string, TValue>)map;
            Assert.IsTrue(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable["new"] = default!);
            Assert.Throws<NotSupportedException>(
                () => mutable.Add(new KeyValuePair<string, TValue>("new", default!)));
        }

        private static void ExpectMissing(
            WorkflowDocumentData data, string expectedPath, Action<WorkflowDocumentData> spoil)
        {
            spoil(data);
            var result = WorkflowDocument.Create(data);
            Assert.IsFalse(result.Ok, $"expected failure for {expectedPath}");
            var found = false;
            foreach (var diagnostic in result.Diagnostics)
            {
                Assert.AreEqual("OW-WF-003", diagnostic.Code);
                Assert.AreEqual(Severity.Error, diagnostic.Severity);
                if (diagnostic.JsonPath == expectedPath)
                {
                    found = true;
                }
            }
            Assert.IsTrue(found, $"no diagnostic carried JsonPath {expectedPath}: {Describe(result)}");
        }

        private static string Describe(Result<WorkflowDocument> result)
        {
            var builder = new StringBuilder();
            foreach (var diagnostic in result.Diagnostics)
            {
                builder.Append(diagnostic.Code).Append(' ').Append(diagnostic.JsonPath)
                    .Append(' ').Append(diagnostic.Message).AppendLine();
            }
            return builder.ToString();
        }
    }
}
