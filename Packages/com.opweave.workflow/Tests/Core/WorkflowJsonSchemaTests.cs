#nullable enable

#if !UNITY_6000_5_OR_NEWER
using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Json.Schema;
using NUnit.Framework;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the shipped JSON Schema artifact
    /// <c>Packages/com.opweave.workflow/Schemas~/opweave-1.0.schema.json</c>
    /// (SPEC §A2). The SPEC §A2 example and the SPEC §M.4 AT-01
    /// <c>example.reset-transform</c> workflow validate; required-member
    /// violations, zero or two <c>core.start</c> nodes, an empty
    /// <c>nodes</c> array and wrong property types fail; top-level
    /// <c>x-*</c> extension members are accepted while other unknown
    /// members are rejected. A non-<c>"next"</c> <c>fromPort</c> is not
    /// rejected by this schema — v1 reports OW-VAL-004 from semantic
    /// validation instead (SPEC §A2, appendix N) — and the R1
    /// single-supplier rule plus the OW-WF-008 size limits live in the
    /// schema's descriptions only. Validation uses the test-only
    /// JsonSchema.Net 7.4.0 dependency referenced solely by test
    /// assemblies (local:T-W1.3-04:schema-fixtures).
    /// </summary>
    internal sealed class WorkflowJsonSchemaTests
    {
        private static readonly string SchemaRelativePath = Path.Combine(
            "Packages", "com.opweave.workflow", "Schemas~", "opweave-1.0.schema.json");

        private static JsonSchema _schema = null!;
        private static EvaluationOptions _options = null!;

        // The SPEC §A2 example, verbatim.
        private const string SpecA2Example = @"{
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

        // The SPEC §M.4 generated-code example used by AT-01
        // (proj.transform.reset), verbatim.
        private const string SpecM4At01Workflow = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.reset-transform"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""create"", ""operation"": ""unity.gameobject.create"", ""version"": 1, ""parameters"": { ""name"": ""Player"" } },
    { ""id"": ""reset"", ""operation"": ""proj.transform.reset"", ""version"": 1 }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""create"" }, { ""from"": ""create"", ""to"": ""reset"" } ],
  ""dataEdges"": [ { ""from"": { ""node"": ""create"", ""port"": ""gameObject"" }, ""to"": { ""node"": ""reset"", ""port"": ""target"" } } ],
  ""outputs"": [ { ""id"": ""gameObject"", ""from"": { ""node"": ""reset"", ""port"": ""gameObject"" } } ]
}";

        // The smallest valid document; negative fixtures are mutations of it.
        private const string Minimal = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.min"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""a"", ""operation"": ""unity.scene.save"", ""version"": 1 }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""a"" } ]
}";

        [OneTimeSetUp]
        public void LoadSchema()
        {
            _schema = JsonSchema.FromText(File.ReadAllText(LocateSchemaFile()));
            _options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        }

        private static string LocateSchemaFile()
        {
            // dotnet tests run from the test project's output directory and
            // Unity tests from the TestProject directory; the repository root
            // is reached by walking up from both.
            var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (var start in starts)
            {
                DirectoryInfo? dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, SchemaRelativePath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("could not locate " + SchemaRelativePath);
        }

        private static EvaluationResults Evaluate(string json)
        {
            var node = JsonNode.Parse(json);
            return _schema.Evaluate(node, _options);
        }

        private static string Describe(EvaluationResults results)
        {
            var sb = new StringBuilder();
            Collect(results, sb);
            return sb.ToString();

            static void Collect(EvaluationResults r, StringBuilder acc)
            {
                if (r.Errors != null)
                {
                    foreach (var error in r.Errors)
                    {
                        acc.Append(r.InstanceLocation).Append(' ')
                            .Append(error.Key).Append(": ").Append(error.Value)
                            .AppendLine();
                    }
                }
                foreach (var detail in r.Details)
                {
                    Collect(detail, acc);
                }
            }
        }

        private static void AssertValid(string json, string fixture)
        {
            var results = Evaluate(json);
            Assert.IsTrue(results.IsValid, fixture + " must validate:\n" + Describe(results));
        }

        private static void AssertInvalid(string json, string fixture)
        {
            var results = Evaluate(json);
            Assert.IsFalse(results.IsValid, fixture + " must fail validation");
        }

        private static string MutateMinimal(Action<JsonObject> change)
        {
            var document = (JsonObject)JsonNode.Parse(Minimal)!;
            change(document);
            return document.ToJsonString();
        }

        // ── Positive fixtures ─────────────────────────────────────────────

        [Test]
        public void SpecA2Example_Validates()
        {
            AssertValid(SpecA2Example, "SPEC §A2 example");
        }

        [Test]
        public void SpecM4At01Workflow_Validates()
        {
            AssertValid(SpecM4At01Workflow, "SPEC §M.4 AT-01 workflow");
        }

        [Test]
        public void TopLevelExtensionMembers_Validate()
        {
            var json = MutateMinimal(document =>
            {
                document["x-vendor"] = JsonNode.Parse(@"{""flag"":true}");
                document["x-note"] = "kept on round-trip";
            });
            AssertValid(json, "x-* extension members");
        }

        [Test]
        public void ExplicitNextFromPort_Validates()
        {
            var json = MutateMinimal(document =>
                document["controlEdges"]![0]!.AsObject()["fromPort"] = "next");
            AssertValid(json, "explicit fromPort \"next\"");
        }

        [Test]
        public void NonNextFromPort_IsNotRejectedBySchema()
        {
            // SPEC §A2 rejects a non-"next" fromPort with OW-VAL-004 in
            // semantic validation (appendix N), not in this structural schema.
            var json = MutateMinimal(document =>
                document["controlEdges"]![0]!.AsObject()["fromPort"] = "branch");
            AssertValid(json, "non-next fromPort (semantic layer's OW-VAL-004)");
        }

        [Test]
        public void OptionalInputAndPolicyMembers_Validate()
        {
            var json = MutateMinimal(document =>
            {
                document["inputs"] = JsonNode.Parse(
                    @"[{""id"":""in1"",""type"":""core.Json"",""required"":false,""default"":null,""description"":""d""}]");
                document["nodes"]![1]!.AsObject()["policy"] =
                    JsonNode.Parse(@"{""timeoutSeconds"":60,""maxAttempts"":3}");
            });
            AssertValid(json, "workflow input with default, full node policy");
        }

        // ── Required members ──────────────────────────────────────────────

        [Test]
        public void MissingSchemaVersion_Fails()
        {
            AssertInvalid(MutateMinimal(d => d.Remove("schemaVersion")), "missing schemaVersion");
        }

        [Test]
        public void MissingId_Fails()
        {
            AssertInvalid(MutateMinimal(d => d.Remove("id")), "missing id");
        }

        [Test]
        public void MissingNodes_Fails()
        {
            AssertInvalid(MutateMinimal(d => d.Remove("nodes")), "missing nodes");
        }

        [Test]
        public void MissingControlEdges_Fails()
        {
            AssertInvalid(MutateMinimal(d => d.Remove("controlEdges")), "missing controlEdges");
        }

        // ── Exactly one core.start ────────────────────────────────────────

        [Test]
        public void EmptyNodesArray_Fails()
        {
            AssertInvalid(MutateMinimal(d => d["nodes"] = new JsonArray()), "empty nodes array");
        }

        [Test]
        public void NoCoreStartNode_Fails()
        {
            var json = MutateMinimal(d => d["nodes"]!.AsArray().RemoveAt(0));
            AssertInvalid(json, "nodes without core.start");
        }

        [Test]
        public void TwoCoreStartNodes_Fail()
        {
            var json = MutateMinimal(d => d["nodes"]!.AsArray().Add(
                JsonNode.Parse(@"{""id"":""start2"",""operation"":""core.start"",""version"":1}")));
            AssertInvalid(json, "two core.start nodes");
        }

        // ── Property types ────────────────────────────────────────────────

        [Test]
        public void NonStringId_Fails()
        {
            AssertInvalid(MutateMinimal(d => d["id"] = 123), "id must be a string");
        }

        [Test]
        public void NonStringSchemaVersion_Fails()
        {
            AssertInvalid(MutateMinimal(d => d["schemaVersion"] = 1), "schemaVersion must be a string");
        }

        [Test]
        public void OtherSchemaVersion_Fails()
        {
            AssertInvalid(MutateMinimal(d => d["schemaVersion"] = "0.9"), "schemaVersion is fixed to 1.0");
        }

        [Test]
        public void NonArrayNodes_Fails()
        {
            AssertInvalid(MutateMinimal(d => d["nodes"] = new JsonObject()), "nodes must be an array");
        }

        [Test]
        public void NonStringNodeVersion_Fails()
        {
            var json = MutateMinimal(d => d["nodes"]![0]!.AsObject()["version"] = "1");
            AssertInvalid(json, "version must be an integer");
        }

        [Test]
        public void NonObjectNodeParameters_Fail()
        {
            var json = MutateMinimal(d => d["nodes"]![1]!.AsObject()["parameters"] = new JsonArray());
            AssertInvalid(json, "parameters must be an object");
        }

        [Test]
        public void NonArrayTags_Fail()
        {
            AssertInvalid(MutateMinimal(d => d["tags"] = "a"), "tags must be an array");
        }

        [Test]
        public void NonObjectLayout_Fails()
        {
            AssertInvalid(MutateMinimal(d => d["layout"] = new JsonArray()), "layout must be an object");
        }

        [Test]
        public void ControlEdgeMissingTo_Fails()
        {
            var json = MutateMinimal(d => d["controlEdges"]![0]!.AsObject().Remove("to"));
            AssertInvalid(json, "controlEdge.to is required");
        }

        [Test]
        public void InputBindingMissingToPort_Fails()
        {
            var json = MutateMinimal(d => d["inputBindings"] = JsonNode.Parse(
                @"[{""fromInput"":""in1"",""to"":{""node"":""a""}}]"));
            AssertInvalid(json, "inputBinding.to.port is required");
        }

        [Test]
        public void NonBooleanInputRequired_Fails()
        {
            var json = MutateMinimal(d => d["inputs"] = JsonNode.Parse(
                @"[{""id"":""in1"",""type"":""core.Json"",""required"":""yes""}]"));
            AssertInvalid(json, "workflow input required must be a boolean");
        }

        // ── Unknown members ───────────────────────────────────────────────

        [Test]
        public void UnknownTopLevelProperty_Fails()
        {
            AssertInvalid(MutateMinimal(d => d["bogus"] = 1), "unknown top-level member");
        }

        [Test]
        public void UnknownNodeProperty_Fails()
        {
            var json = MutateMinimal(d => d["nodes"]![0]!.AsObject()["bogus"] = 1);
            AssertInvalid(json, "unknown node member");
        }
    }
}
#endif
