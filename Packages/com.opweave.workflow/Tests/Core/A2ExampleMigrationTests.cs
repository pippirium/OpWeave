#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Migration;
using OpWeave.Core.Model;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for SPEC §A7 test 1 on the complete §A2 example
    /// (local:T-W1.3-07:a2-workflow-migration). The
    /// <c>example.create-player</c> document at schemaVersion "1.0" passes
    /// through the §A7 framework unchanged; the same document carrying only
    /// schemaVersion "0.9" migrates through a test-only
    /// <see cref="IWorkflowMigration"/> (From="0.9", To="1.0", version field
    /// only) into a document canonically identical to the §A2 example, the
    /// change list records just the version step, and the source file is
    /// never overwritten (SPEC §A7 item 3). A contract-breaking migration is
    /// OW-WF-007 and a version no migration reaches is OW-WF-002, both
    /// failing closed without touching the file. The same sources run under
    /// dotnet and Unity Mono.
    /// </summary>
    internal sealed class A2ExampleMigrationTests
    {
        // ── Fakes ────────────────────────────────────────────────────────────

        // A scripted IWorkflowMigration: Apply transforms the document the
        // framework hands it; From/To declare the chain endpoints.
        private sealed class ScriptedMigration : IWorkflowMigration
        {
            private readonly Func<JObject, JObject?> _apply;

            public ScriptedMigration(string from, string to, Func<JObject, JObject?> apply)
            {
                From = from;
                To = to;
                _apply = apply;
            }

            public string From { get; }
            public string To { get; }
            public JObject Migrate(JObject doc) => _apply(doc)!;
        }

        // The internal registered-version lookup for the five Operations the
        // §A2 example uses, each registered at the version its node declares.
        private sealed class A2Catalog : IOperationMigrationCatalog
        {
            private static readonly string[] s_operations =
            {
                "core.start",
                "unity.gameobject.create",
                "unity.component.add",
                "unity.prefab.create_from_gameobject",
                "unity.scene.save",
            };

            public IReadOnlyList<RegisteredOperationVersion>? RegisteredVersions(
                string operationId)
            {
                foreach (var operation in s_operations)
                {
                    if (string.Equals(operation, operationId, StringComparison.Ordinal))
                    {
                        return new[] { new RegisteredOperationVersion(1, null) };
                    }
                }
                return null;
            }
        }

        // ── Fixtures ─────────────────────────────────────────────────────────

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

        // The test-only IWorkflowMigration of SPEC §A7 test 1: accepts the
        // "0.9" document and produces the "1.0" document, changing only the
        // version field.
        private static IWorkflowMigration VersionOnlyMigration() =>
            new ScriptedMigration("0.9", "1.0", doc =>
            {
                doc["schemaVersion"] = "1.0";
                return doc;
            });

        // The §A2 example text with only the schemaVersion member rewritten.
        private static string A2Example09Text()
        {
            var text = A2Example.Replace(
                "\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"0.9\"");
            Assert.AreNotEqual(A2Example, text,
                "the 0.9 fixture must differ from the §A2 example");
            return text;
        }

        // Writes text to a fresh temp file and returns its path together with
        // the exact bytes on disk, so tests can prove the file is never
        // overwritten (SPEC §A7 item 3). DeleteTempDocument cleans up.
        private static string WriteTempDocument(string text, out byte[] bytes)
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "OpWeave.A2Migration." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "workflow.opweave.json");
            bytes = new UTF8Encoding(false).GetBytes(text);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void DeleteTempDocument(string path)
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
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

        private static void AssertSingleDiagnostic(
            Result<WorkflowMigrationResult> result, string code)
        {
            Assert.IsFalse(result.Ok, "migration unexpectedly succeeded");
            Assert.AreEqual(1, result.Diagnostics.Count,
                "expected exactly one diagnostic: " + Describe(result.Diagnostics));
            Assert.AreEqual(code, result.Diagnostics[0].Code);
        }

        // ── SPEC §A7 test 1 ──────────────────────────────────────────────────

        [Test]
        public void CurrentA2Example_PassesThroughUnchanged()
        {
            var input = JObject.Parse(A2Example);
            var snapshot = CanonicalJson.Serialize(input);

            var result = WorkflowMigrator.Migrate(
                input, Array.Empty<IWorkflowMigration>(), new A2Catalog());

            Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
            Assert.AreEqual(0, result.Value!.Changes.Count,
                "the current schemaVersion needs no migration step");
            Assert.AreEqual(snapshot, CanonicalJson.Serialize(result.Value.Document),
                "the schemaVersion \"1.0\" §A2 example must be returned unchanged");
            Assert.AreEqual(snapshot, CanonicalJson.Serialize(input),
                "the caller's document must not be mutated");
        }

        [Test]
        public void VersionOnlyMigration_Makes09ExampleCanonicallyIdenticalToA2()
        {
            var path = WriteTempDocument(A2Example09Text(), out var sourceBytes);
            try
            {
                var input = JObject.Parse(File.ReadAllText(path));
                var inputSnapshot = CanonicalJson.Serialize(input);

                var result = WorkflowMigrator.Migrate(
                    input, new IWorkflowMigration[] { VersionOnlyMigration() },
                    new A2Catalog());

                Assert.IsTrue(result.Ok, Describe(result.Diagnostics));
                Assert.AreEqual(
                    CanonicalJson.Serialize(JObject.Parse(A2Example)),
                    CanonicalJson.Serialize(result.Value!.Document),
                    "the migrated document must be canonically identical to the §A2 example");

                // The change list is limited to the version update.
                Assert.AreEqual(1, result.Value.Changes.Count);
                var change = result.Value.Changes[0];
                Assert.AreEqual(MigrationChangeKind.SchemaVersion, change.Kind);
                Assert.IsNull(change.NodeId);
                Assert.IsNull(change.OperationId);
                Assert.AreEqual("0.9", change.FromVersion);
                Assert.AreEqual("1.0", change.ToVersion);

                // The result reads back as a valid v1.0 workflow whose §A4
                // canonical form equals the §A2 example's.
                var expectedRead = WorkflowSerializer.Read(A2Example);
                Assert.IsTrue(expectedRead.Ok, Describe(expectedRead.Diagnostics));
                var migratedRead = WorkflowSerializer.Read(
                    result.Value.Document.ToString(Newtonsoft.Json.Formatting.None));
                Assert.IsTrue(migratedRead.Ok, Describe(migratedRead.Diagnostics));
                Assert.AreEqual(
                    CanonicalWorkflow.From(expectedRead.Value!).CanonicalText,
                    CanonicalWorkflow.From(migratedRead.Value!).CanonicalText);

                // The migration never rewrites the input: the caller's
                // document is unmutated and the source file bytes are intact.
                Assert.AreEqual(inputSnapshot, CanonicalJson.Serialize(input));
                Assert.AreEqual(sourceBytes, File.ReadAllBytes(path),
                    "migration must never rewrite the input file");
            }
            finally
            {
                DeleteTempDocument(path);
            }
        }

        [Test]
        public void ContractBreakingMigration_On09Example_FailsClosedAsOwWf007()
        {
            var path = WriteTempDocument(A2Example09Text(), out var sourceBytes);
            try
            {
                var input = JObject.Parse(File.ReadAllText(path));
                // Declares To="1.0" but corrupts the document and never
                // reaches its declared version — an OW-WF-007 violation.
                var broken = new ScriptedMigration("0.9", "1.0", doc =>
                {
                    doc.Remove("nodes");
                    return doc;
                });

                var result = WorkflowMigrator.Migrate(
                    input, new IWorkflowMigration[] { broken }, new A2Catalog());

                AssertSingleDiagnostic(result, ErrorCodes.OW_WF_007);
                Assert.AreEqual(sourceBytes, File.ReadAllBytes(path),
                    "a failed migration must not touch the input file");
            }
            finally
            {
                DeleteTempDocument(path);
            }
        }

        [Test]
        public void UnreachableVersion_On09Example_FailsClosedAsOwWf002()
        {
            var path = WriteTempDocument(A2Example09Text(), out var sourceBytes);
            try
            {
                var input = JObject.Parse(File.ReadAllText(path));

                // No registered IWorkflowMigration reaches "1.0": the older
                // major is an unsupported schemaVersion, not a silent success.
                var result = WorkflowMigrator.Migrate(
                    input, Array.Empty<IWorkflowMigration>(), new A2Catalog());

                AssertSingleDiagnostic(result, ErrorCodes.OW_WF_002);
                Assert.AreEqual(sourceBytes, File.ReadAllBytes(path),
                    "an unrecognized version must not touch the input file");
            }
            finally
            {
                DeleteTempDocument(path);
            }
        }
    }
}
