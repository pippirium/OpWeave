#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Migration;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §A7 migration framework (<see cref="WorkflowMigrator"/>)
    /// with synthetic workflow and Operation migrations: chained IWorkflowMigration
    /// steps reach schemaVersion 1.0 and report each step in the change list;
    /// IOperationMigration renames parameters through the internal registered-version
    /// lookup; an Operation with no registered versions is OW-OP-001; an older
    /// node version without a migration is OW-OP-002; a throwing or
    /// contract-breaking migration is OW-WF-007; a newer major is OW-WF-002; and the
    /// input document is never mutated (SPEC §A7 item 3, no file is written). The same
    /// sources run under dotnet and Unity Mono
    /// (local:T-W1.3-06:migration-framework, local:T-W1.3-08:unknown-operation-code).
    /// </summary>
    internal sealed class WorkflowMigrationTests
    {
        // ── Fakes ───────────────────────────────────────────────────────────────

        // A scripted IWorkflowMigration: Apply transforms the document the framework
        // hands it; From/To declare the chain endpoints.
        private sealed class FakeWorkflowMigration : IWorkflowMigration
        {
            private readonly Func<JObject, JObject?> _apply;

            public FakeWorkflowMigration(string from, string to, Func<JObject, JObject?> apply)
            {
                From = from;
                To = to;
                _apply = apply;
            }

            public string From { get; }
            public string To { get; }
            public JObject Migrate(JObject doc) => _apply(doc)!;
        }

        // A scripted IOperationMigration: Apply rewrites the node in place.
        private sealed class FakeOperationMigration : IOperationMigration
        {
            private readonly Action<JObject> _apply;

            public FakeOperationMigration(int fromVersion, Action<JObject> apply)
            {
                FromVersion = fromVersion;
                _apply = apply;
            }

            public int FromVersion { get; }
            public void Migrate(JObject node) => _apply(node);
        }

        // The internal registered-version lookup, backed by an explicit table.
        private sealed class FakeCatalog : IOperationMigrationCatalog
        {
            private readonly Dictionary<string, List<RegisteredOperationVersion>> _versions =
                new Dictionary<string, List<RegisteredOperationVersion>>(StringComparer.Ordinal);

            public FakeCatalog Register(
                string operationId, int version, IOperationMigration? migration = null,
                IReadOnlyList<OperationPortRename>? portRenames = null)
            {
                if (!_versions.TryGetValue(operationId, out var list))
                {
                    list = new List<RegisteredOperationVersion>();
                    _versions.Add(operationId, list);
                }
                // Every scripted migration explicitly declares its port
                // compatibility contract. Empty means no port renames.
                list.Add(new RegisteredOperationVersion(
                    version, migration,
                    migration is null ? portRenames
                        : portRenames ?? Array.Empty<OperationPortRename>()));
                list.Sort((a, b) => a.Version.CompareTo(b.Version));
                return this;
            }

            public IReadOnlyList<RegisteredOperationVersion>? RegisteredVersions(string operationId) =>
                _versions.TryGetValue(operationId, out var list) ? list : null;
        }

        // ── Fixtures ────────────────────────────────────────────────────────────

        private static JObject Doc(string json) => JObject.Parse(json);

        // A minimal 0.8 fixture: the "name"/"edges" members are what the synthetic
        // 0.8→0.9→1.0 chain renames to the 1.0 "id"/"controlEdges" members.
        private const string Doc08 = @"{
  ""schemaVersion"": ""0.8"",
  ""name"": ""example.migration"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 }
  ],
  ""edges"": []
}";

        private const string Doc10Expected = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.migration"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 }
  ],
  ""controlEdges"": []
}";

        private static IWorkflowMigration Rename08to09() =>
            new FakeWorkflowMigration("0.8", "0.9", doc =>
            {
                doc["id"] = doc["name"];
                doc.Remove("name");
                doc["schemaVersion"] = "0.9";
                return doc;
            });

        private static IWorkflowMigration Rename09to10() =>
            new FakeWorkflowMigration("0.9", "1.0", doc =>
            {
                doc["controlEdges"] = doc["edges"];
                doc.Remove("edges");
                doc["schemaVersion"] = "1.0";
                return doc;
            });

        private static FakeCatalog StartOnlyCatalog() =>
            new FakeCatalog().Register("core.start", 1);

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

        private static void AssertSingleDiagnostic(
            Result<WorkflowMigrationResult> result, string code)
        {
            Assert.IsFalse(result.Ok, "migration unexpectedly succeeded");
            Assert.AreEqual(1, result.Diagnostics.Count,
                "expected exactly one diagnostic: " + Describe(result.Diagnostics));
            Assert.AreEqual(code, result.Diagnostics[0].Code);
        }

        // ── SPEC §A7 item 1: schemaVersion chaining ─────────────────────────────

        [Test]
        public void CurrentVersion_DocumentPassesThroughUnchanged()
        {
            var input = Doc(Doc10Expected);
            var snapshot = CanonicalJson.Serialize(input);

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), StartOnlyCatalog());

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(0, result.Value!.Changes.Count);
            Assert.AreEqual(
                snapshot, CanonicalJson.Serialize(result.Value.Document),
                "a document at the current version must pass through unchanged");
            Assert.AreEqual(snapshot, CanonicalJson.Serialize(input),
                "the caller's input document must not be mutated");
        }

        [Test]
        public void ChainedWorkflowMigrations_ReachCurrentVersion()
        {
            var input = Doc(Doc08);
            var snapshot = CanonicalJson.Serialize(input);

            var result = WorkflowMigrator.Migrate(
                input,
                new IWorkflowMigration[] { Rename08to09(), Rename09to10() },
                StartOnlyCatalog());

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(
                CanonicalJson.Serialize(Doc(Doc10Expected)),
                CanonicalJson.Serialize(result.Value!.Document),
                "the chained result must equal the fixture's 1.0 JSON");
            Assert.AreEqual(2, result.Value.Changes.Count);
            Assert.AreEqual(MigrationChangeKind.SchemaVersion, result.Value.Changes[0].Kind);
            Assert.AreEqual("0.8", result.Value.Changes[0].FromVersion);
            Assert.AreEqual("0.9", result.Value.Changes[0].ToVersion);
            Assert.AreEqual(MigrationChangeKind.SchemaVersion, result.Value.Changes[1].Kind);
            Assert.AreEqual("0.9", result.Value.Changes[1].FromVersion);
            Assert.AreEqual("1.0", result.Value.Changes[1].ToVersion);
            Assert.AreEqual(snapshot, CanonicalJson.Serialize(input),
                "the caller's input document must not be mutated — migration never rewrites files");
        }

        [Test]
        public void SameMajorNewerMinor_NeedsNoMigration()
        {
            // SPEC §N.3 item 3: minor additions never change existing semantics, so a
            // 1.x minor document needs no migration and stays unchanged.
            var input = Doc(Doc10Expected.Replace("\"1.0\"", "\"1.1\""));

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), StartOnlyCatalog());

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(0, result.Value!.Changes.Count);
            Assert.AreEqual("1.1", (string?)result.Value.Document["schemaVersion"]);
        }

        [Test]
        public void NewerMajor_IsOwWf002()
        {
            var input = Doc(Doc10Expected.Replace("\"1.0\"", "\"2.0\""));

            var result = WorkflowMigrator.Migrate(
                input,
                new IWorkflowMigration[] { Rename09to10() },
                StartOnlyCatalog());

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_002);
        }

        [Test]
        public void MissingChainStep_IsOwWf002()
        {
            // An older major with no registered migration step is an unsupported
            // schemaVersion, not a migration failure.
            var input = Doc(Doc08);

            var result = WorkflowMigrator.Migrate(
                input,
                new IWorkflowMigration[] { Rename09to10() },
                StartOnlyCatalog());

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_002);
        }

        [Test]
        public void ThrowingWorkflowMigration_IsOwWf007()
        {
            var input = Doc(Doc10Expected.Replace("\"1.0\"", "\"0.9\""));
            var throwing = new FakeWorkflowMigration("0.9", "1.0",
                _ => throw new InvalidOperationException("boom"));

            var result = WorkflowMigrator.Migrate(
                input, new[] { throwing }, StartOnlyCatalog());

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_007);
        }

        [Test]
        public void NullReturningWorkflowMigration_IsOwWf007()
        {
            var input = Doc(Doc10Expected.Replace("\"1.0\"", "\"0.9\""));
            var nullReturning = new FakeWorkflowMigration("0.9", "1.0", _ => null);

            var result = WorkflowMigrator.Migrate(
                input, new[] { nullReturning }, StartOnlyCatalog());

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_007);
        }

        [Test]
        public void VersionMismatchedWorkflowMigration_IsOwWf007()
        {
            // Declares To = "1.0" but produces a document still at "0.95".
            var input = Doc(Doc10Expected.Replace("\"1.0\"", "\"0.9\""));
            var lying = new FakeWorkflowMigration("0.9", "1.0", doc =>
            {
                doc["schemaVersion"] = "0.95";
                return doc;
            });

            var result = WorkflowMigrator.Migrate(
                input, new[] { lying }, StartOnlyCatalog());

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_007);
        }

        // ── SPEC §A7 item 2: Operation version migration ────────────────────────

        [Test]
        public void OperationMigration_RenamesParametersAndBumpsVersion()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.op-migration"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1,
      ""parameters"": { ""oldName"": ""x"", ""keep"": 7 } }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""n1"" } ]
}");
            var catalog = StartOnlyCatalog()
                .Register("test.op", 1)
                .Register("test.op", 2, new FakeOperationMigration(1, node =>
                {
                    var parameters = (JObject)node["parameters"]!;
                    parameters["newName"] = parameters["oldName"]!.DeepClone();
                    parameters.Remove("oldName");
                }));

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            var migrated = (JObject)result.Value!.Document["nodes"]![1]!;
            Assert.AreEqual(2, (int)migrated["version"]!,
                "the framework owns the version bump");
            var parameters = (JObject)migrated["parameters"]!;
            Assert.IsNull(parameters["oldName"]);
            Assert.AreEqual("x", (string?)parameters["newName"]);
            Assert.AreEqual(7, (int)parameters["keep"]!);
            Assert.AreEqual(1, result.Value.Changes.Count);
            var change = result.Value.Changes[0];
            Assert.AreEqual(MigrationChangeKind.OperationVersion, change.Kind);
            Assert.AreEqual("n1", change.NodeId);
            Assert.AreEqual("test.op", change.OperationId);
            Assert.AreEqual("1", change.FromVersion);
            Assert.AreEqual("2", change.ToVersion);
            // The pre-migration input still carries version 1 and oldName.
            var original = (JObject)input["nodes"]![1]!;
            Assert.AreEqual(1, (int)original["version"]!);
            Assert.IsNotNull(((JObject)original["parameters"]!)["oldName"]);
        }

        [Test]
        public void OperationMigration_AcceptsParsedInt32AndInt64NodeVersions()
        {
            const string source = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.integer-versions"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1,
      ""parameters"": { ""oldName"": ""before"" } }
  ],
  ""controlEdges"": []
}";
            var catalog = StartOnlyCatalog()
                .Register("test.op", 1)
                .Register("test.op", 2, new FakeOperationMigration(1, node =>
                {
                    var parameters = (JObject)node["parameters"]!;
                    parameters["newName"] = parameters["oldName"]!.DeepClone();
                    parameters.Remove("oldName");
                }));
            foreach (var kind in new[] { "parsed", "Int32", "Int64" })
            {
                var input = Doc(source);
                var node = (JObject)input["nodes"]![1]!;
                if (kind == "Int32")
                {
                    node["version"] = new JValue(1);
                }
                else if (kind == "Int64")
                {
                    node["version"] = new JValue(1L);
                }
                var snapshot = CanonicalJson.Serialize(input);

                var result = WorkflowMigrator.Migrate(
                    input, Array.Empty<IWorkflowMigration>(), catalog);

                Assert.IsTrue(result.Ok, kind + ": " + Describe(result.Diagnostics));
                var migrated = (JObject)result.Value!.Document["nodes"]![1]!;
                Assert.AreEqual(2, (int)migrated["version"]!, kind);
                Assert.AreEqual("before", (string?)migrated["parameters"]!["newName"], kind);
                Assert.IsNull(migrated["parameters"]!["oldName"], kind);
                Assert.AreEqual(1, result.Value.Changes.Count, kind);
                Assert.AreEqual("1", result.Value.Changes[0].FromVersion, kind);
                Assert.AreEqual("2", result.Value.Changes[0].ToVersion, kind);
                Assert.AreEqual(snapshot, CanonicalJson.Serialize(input),
                    kind + ": source document must not be mutated");
            }
        }

        [TestCase(2147483648L)]
        [TestCase(-2147483649L)]
        public void OperationVersionOutsideInt32Range_IsOwOp002(long oversized)
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.large-version"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1 }
  ],
  ""controlEdges"": []
}");
            ((JObject)input["nodes"]![1]!)["version"] = new JValue(oversized);
            var snapshot = CanonicalJson.Serialize(input);

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), StartOnlyCatalog()
                    .Register("test.op", 1)
                    .Register("test.op", 2, new FakeOperationMigration(1, _ => { })));

            AssertSingleDiagnostic(result, ErrorCodes.OW_OP_002);
            Assert.AreEqual("$.nodes[1].version", result.Diagnostics[0].JsonPath);
            Assert.AreEqual(snapshot, CanonicalJson.Serialize(input));
        }

        [Test]
        public void OperationMigration_ChainsAcrossRegisteredVersions()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.op-chain"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1,
      ""parameters"": { ""v1"": true } }
  ],
  ""controlEdges"": []
}");
            var catalog = StartOnlyCatalog()
                .Register("test.op", 1)
                .Register("test.op", 2, new FakeOperationMigration(1, node =>
                {
                    var parameters = (JObject)node["parameters"]!;
                    parameters["v2"] = parameters["v1"]!.DeepClone();
                    parameters.Remove("v1");
                }))
                .Register("test.op", 3, new FakeOperationMigration(2, node =>
                {
                    var parameters = (JObject)node["parameters"]!;
                    parameters["v3"] = parameters["v2"]!.DeepClone();
                    parameters.Remove("v2");
                }));

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            var migrated = (JObject)result.Value!.Document["nodes"]![1]!;
            Assert.AreEqual(3, (int)migrated["version"]!);
            var parameters = (JObject)migrated["parameters"]!;
            Assert.AreEqual(1, parameters.Count);
            Assert.AreEqual(true, (bool)parameters["v3"]!);
            Assert.AreEqual(2, result.Value.Changes.Count);
            Assert.AreEqual("1", result.Value.Changes[0].FromVersion);
            Assert.AreEqual("2", result.Value.Changes[0].ToVersion);
            Assert.AreEqual("2", result.Value.Changes[1].FromVersion);
            Assert.AreEqual("3", result.Value.Changes[1].ToVersion);
        }

        [Test]
        public void OperationPortRenames_RewriteConnectedReferencesNotOtherNodes()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.port-migration"",
  ""inputs"": [{ ""id"": ""wfIn"", ""type"": ""core.Json"", ""required"": false }],
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""source"", ""operation"": ""test.source"", ""version"": 1 },
    { ""id"": ""target"", ""operation"": ""test.target"", ""version"": 1,
      ""parameters"": { ""oldParam"": 17 } },
    { ""id"": ""other"", ""operation"": ""test.other"", ""version"": 1 }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""source"" },
    { ""from"": ""source"", ""to"": ""target"" },
    { ""from"": ""target"", ""to"": ""other"" }
  ],
  ""dataEdges"": [
    { ""from"": { ""node"": ""source"", ""port"": ""oldOut"" },
      ""to"": { ""node"": ""target"", ""port"": ""oldIn"" } },
    { ""from"": { ""node"": ""other"", ""port"": ""oldOut"" },
      ""to"": { ""node"": ""target"", ""port"": ""keep"" } }
  ],
  ""inputBindings"": [
    { ""fromInput"": ""wfIn"", ""to"": { ""node"": ""target"", ""port"": ""oldBound"" } }
  ],
  ""outputs"": [
    { ""id"": ""out1"", ""from"": { ""node"": ""source"", ""port"": ""oldOut"" } },
    { ""id"": ""out2"", ""from"": { ""node"": ""other"", ""port"": ""oldOut"" } }
  ]
}");
            var before = CanonicalJson.Serialize(input);
            var catalog = StartOnlyCatalog()
                .Register("test.source", 1)
                .Register("test.source", 2, new FakeOperationMigration(1, _ => { }),
                    new[] { new OperationPortRename(
                        OperationPortKind.Output, "oldOut", "newOut") })
                .Register("test.target", 1)
                .Register("test.target", 2, new FakeOperationMigration(1, node =>
                {
                    var p = (JObject)node["parameters"]!;
                    p["newParam"] = p["oldParam"]!.DeepClone();
                    p.Remove("oldParam");
                }), new[]
                {
                    new OperationPortRename(OperationPortKind.Input, "oldIn", "newIn"),
                    new OperationPortRename(OperationPortKind.Input, "oldBound", "newBound"),
                    new OperationPortRename(OperationPortKind.Input, "oldParam", "newParam"),
                })
                .Register("test.other", 1);
            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            var doc = result.Value!.Document;
            var nodes = (JArray)doc["nodes"]!;
            Assert.AreEqual(2, (int)nodes[1]!["version"]!);
            Assert.AreEqual(2, (int)nodes[2]!["version"]!);
            Assert.AreEqual(17, (int)nodes[2]!["parameters"]!["newParam"]!);
            Assert.IsNull(nodes[2]!["parameters"]!["oldParam"]);
            var edges = (JArray)doc["dataEdges"]!;
            Assert.AreEqual("newOut", (string?)edges[0]!["from"]!["port"]);
            Assert.AreEqual("newIn", (string?)edges[0]!["to"]!["port"]);
            Assert.AreEqual("oldOut", (string?)edges[1]!["from"]!["port"]);
            Assert.AreEqual("keep", (string?)edges[1]!["to"]!["port"]);
            Assert.AreEqual("newBound",
                (string?)doc["inputBindings"]![0]!["to"]!["port"]);
            Assert.AreEqual("newOut",
                (string?)doc["outputs"]![0]!["from"]!["port"]);
            Assert.AreEqual("oldOut",
                (string?)doc["outputs"]![1]!["from"]!["port"]);
            Assert.AreEqual(2, result.Value.Changes.Count);
            Assert.AreEqual(before, CanonicalJson.Serialize(input),
                "port rewrites must not modify the caller's original document");
        }

        [Test]
        public void MigrationChangingNodeIdentity_ReturnsOwWf007()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.bad-identity"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1 }
  ],
  ""controlEdges"": []
}");
            var original = CanonicalJson.Serialize(input);
            var catalog = StartOnlyCatalog()
                .Register("test.op", 1)
                .Register("test.op", 2, new FakeOperationMigration(1, node =>
                {
                    node["id"] = "other";
                }));

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_007);
            Assert.AreEqual(original, CanonicalJson.Serialize(input));
        }

        [Test]
        public void MissingPortCompatibilityMetadata_FailsClosed()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.missing-port-contract"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1 }
  ],
  ""controlEdges"": []
}");
            var catalog = new CatalogWithMissingPortMetadata();
            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_007);
            Assert.AreEqual("$.nodes[1].version", result.Diagnostics[0].JsonPath);
        }

        private sealed class CatalogWithMissingPortMetadata : IOperationMigrationCatalog
        {
            public IReadOnlyList<RegisteredOperationVersion>? RegisteredVersions(
                string operationId)
            {
                if (operationId == "core.start")
                {
                    return new[] { new RegisteredOperationVersion(1, null) };
                }
                if (operationId == "test.op")
                {
                    return new[]
                    {
                        new RegisteredOperationVersion(1, null),
                        new RegisteredOperationVersion(2, new FakeOperationMigration(
                            1, _ => { }), null),
                    };
                }
                return null;
            }
        }

        [Test]
        public void ConflictingPortRenameMetadata_ReturnsOwWf007()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.bad-port-map"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1 }
  ],
  ""controlEdges"": []
}");
            var catalog = StartOnlyCatalog()
                .Register("test.op", 1)
                .Register("test.op", 2, new FakeOperationMigration(1, _ => { }),
                    new[]
                    {
                        new OperationPortRename(OperationPortKind.Output, "old", "new"),
                        new OperationPortRename(OperationPortKind.Output, "old", "other"),
                    });

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_007);
        }

        [Test]
        public void MigrationFailureDiagnostics_DoNotEchoInputValues()
        {
            const string sentinel = "SECRET_SENTINEL_DO_NOT_LOG";
            var badSchema = Doc(@"{
  ""schemaVersion"": ""SECRET_SENTINEL_DO_NOT_LOG"",
  ""nodes"": [],
  ""controlEdges"": []
}");
            var schemaFailure = WorkflowMigrator.Migrate(
                badSchema, Array.Empty<IWorkflowMigration>(), StartOnlyCatalog());
            AssertSingleDiagnostic(schemaFailure, ErrorCodes.OW_WF_002);
            Assert.That(schemaFailure.Diagnostics[0].Message, Does.Not.Contain(sentinel));

            var unknownOperation = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""SECRET_SENTINEL_DO_NOT_LOG"",
      ""version"": 1 }
  ],
  ""controlEdges"": []
}");
            var operationFailure = WorkflowMigrator.Migrate(
                unknownOperation, Array.Empty<IWorkflowMigration>(), StartOnlyCatalog());
            AssertSingleDiagnostic(operationFailure, ErrorCodes.OW_OP_001);
            Assert.That(operationFailure.Diagnostics[0].Message, Does.Not.Contain(sentinel));
            Assert.That(operationFailure.Diagnostics[0].Hint ?? string.Empty,
                Does.Not.Contain(sentinel));

            var throwingMigration = new FakeWorkflowMigration("0.9", "1.0",
                _ => throw new InvalidOperationException(sentinel));
            var oldSchema = Doc(@"{
  ""schemaVersion"": ""0.9"",
  ""nodes"": [],
  ""controlEdges"": []
}");
            var stepFailure = WorkflowMigrator.Migrate(
                oldSchema, new[] { throwingMigration }, StartOnlyCatalog());
            AssertSingleDiagnostic(stepFailure, ErrorCodes.OW_WF_007);
            Assert.That(stepFailure.Diagnostics[0].Message, Does.Not.Contain(sentinel));
        }

        [Test]
        public void OlderNodeVersion_WithoutMigration_IsOwOp002()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.op-stale"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1 }
  ],
  ""controlEdges"": []
}");
            // Version 2 is registered but provides no IOperationMigration.
            var catalog = StartOnlyCatalog()
                .Register("test.op", 1)
                .Register("test.op", 2);

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            AssertSingleDiagnostic(result, ErrorCodes.OW_OP_002);
            Assert.AreEqual("n1", result.Diagnostics[0].NodeId);
            Assert.AreEqual("$.nodes[1].version", result.Diagnostics[0].JsonPath);
        }

        [Test]
        public void NodeVersion_NewerThanRegistered_IsOwOp002()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.op-future"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 5 }
  ],
  ""controlEdges"": []
}");
            var catalog = StartOnlyCatalog()
                .Register("test.op", 1)
                .Register("test.op", 2, new FakeOperationMigration(1, _ => { }));

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            AssertSingleDiagnostic(result, ErrorCodes.OW_OP_002);
        }

        [Test]
        public void UnregisteredOperation_IsOwOp001()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.op-unknown"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""custom.unknown"", ""version"": 1 }
  ],
  ""controlEdges"": []
}");
            var snapshot = CanonicalJson.Serialize(input);

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), StartOnlyCatalog());

            // A single OW-OP-001 — exactly one diagnostic also proves no
            // OW-OP-002 was emitted for the unregistered Operation.
            AssertSingleDiagnostic(result, ErrorCodes.OW_OP_001);
            Assert.AreEqual("n1", result.Diagnostics[0].NodeId);
            Assert.AreEqual("$.nodes[1].operation", result.Diagnostics[0].JsonPath);
            Assert.AreEqual(snapshot, CanonicalJson.Serialize(input));
        }

        [Test]
        public void ThrowingOperationMigration_IsOwWf007()
        {
            var input = Doc(@"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.op-throw"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""n1"", ""operation"": ""test.op"", ""version"": 1 }
  ],
  ""controlEdges"": []
}");
            var catalog = StartOnlyCatalog()
                .Register("test.op", 1)
                .Register("test.op", 2, new FakeOperationMigration(1,
                    _ => throw new InvalidOperationException("boom")));

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), catalog);

            AssertSingleDiagnostic(result, ErrorCodes.OW_WF_007);
            Assert.AreEqual("n1", result.Diagnostics[0].NodeId);
        }
    }
}
