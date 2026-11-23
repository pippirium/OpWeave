#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.3-04 [Edit] tests (SPEC §H7 item 3, §0.6 SG-17, §D0.2): the
// GraphJsonFile revision and atomic-publication primitives — the
// GUI-owned revision hash, byte-exact raw metadata carry in BuildOutput,
// create-only PublishCreate and expected-hash PublishReplace.
using System;
using System.IO;
using NUnit.Framework;
using OpWeave.Core.Model;
using OpWeave.GraphToolkit;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphJsonFileRevisionTests
    {
        private string _dir = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "../Temp/OpWeaveGraphJsonFileRevisionTests"));
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        [Test]
        public void GuiOwnedHash_MetadataOnlyChange_Equal()
        {
            var baseline = Read(FixtureWithMetadata);
            var metadataEdited = Read(FixtureWithMetadata
                .Replace("\"A\\u0062c\"", "\"Renamed\"")
                .Replace("\"a\\u0020\\u0020b\"", "\"rewritten\"")
                .Replace("\"x\" ,  \"y\"", "\"other\"")
                .Replace("\"x-keep\": true", "\"x-keep\": false"));

            Assert.AreEqual(
                GraphJsonFile.GuiOwnedHash(baseline),
                GraphJsonFile.GuiOwnedHash(metadataEdited),
                "title/description/tags/x-* are not GUI-owned content");
        }

        [Test]
        public void GuiOwnedHash_LayoutOrIdOrSchemaVersionChange_Differs()
        {
            var baselineHash = GraphJsonFile.GuiOwnedHash(Read(Fixture));
            var variants = new (string Name, string Text)[]
            {
                ("id", Fixture.Replace("\"test.rev\"", "\"test.rev.other\"")),
                ("schemaVersion", Fixture.Replace("\"1.0\"", "\"1.1\"")),
                ("layout", Fixture.Replace("\"x\": 10", "\"x\": 99")),
                ("canonical content", Fixture.Replace(
                    "\"operation\": \"core.start\"", "\"operation\": \"core.start.upd\"")),
            };
            foreach (var (name, text) in variants)
            {
                Assert.AreNotEqual(
                    baselineHash,
                    GraphJsonFile.GuiOwnedHash(Read(text)),
                    name + " is GUI-owned content");
            }
        }

        [Test]
        public void BuildOutput_ExternalMetadataRawText_PreservedByteExactly()
        {
            var merged = Read(FixtureWithMetadata);

            var output = GraphJsonFile.BuildOutput(merged, FixtureWithMetadata);

            // The raw value spans — escapes and whitespace exactly as the
            // external editor wrote them — survive the save. The canonical
            // writer would emit "Abc", "a  b" and ["x","y"].
            Assert.IsTrue(output.Text.Contains(
                "\"title\": \"A\\u0062c\""), output.Text);
            Assert.IsTrue(output.Text.Contains(
                "\"description\": \"a\\u0020\\u0020b\""), output.Text);
            Assert.IsTrue(output.Text.Contains(
                "\"tags\": [ \"x\" ,  \"y\" ]"), output.Text);
            Assert.IsTrue(Read(output.Text).Canonical.WorkflowHash
                == merged.Canonical.WorkflowHash,
                "the carried metadata must not change canonical content");
        }

        [Test]
        public void BuildOutput_ExtensionMembersRawText_Preserved()
        {
            var merged = Read(FixtureWithMetadata);

            var output = GraphJsonFile.BuildOutput(merged, FixtureWithMetadata);

            Assert.IsTrue(output.Text.Contains(
                "\"x-keep\": true"), output.Text);
            Assert.IsTrue(output.Text.Contains(
                "\"x-obj\": { \"b\" : 1 , \"a\" : [ \"s\" , 2 ] }"), output.Text);
            Assert.IsTrue(output.Text.Contains(
                "\"x-esc\": \"A\\u0042\\u0021\""), output.Text);
            Assert.IsTrue(Read(output.Text).Canonical.WorkflowHash
                == merged.Canonical.WorkflowHash,
                "the carried extensions must not change canonical content");
        }

        // The carry preserves bytes, it never reintroduces stale content:
        // a carried member whose value differs from the merged document's
        // keeps the writer's merged text, and a carry-only member the merged
        // document does not carry is never materialized.
        [Test]
        public void BuildOutput_MismatchedCarry_MergedValueWins()
        {
            var merged = Read(FixtureMerged);

            var output = GraphJsonFile.BuildOutput(merged, FixtureCarryStale);

            Assert.IsTrue(output.Text.Contains(
                "\"title\": \"New Title\""), output.Text);
            Assert.IsTrue(output.Text.Contains("\"a\""), output.Text);
            Assert.IsTrue(output.Text.Contains(
                "\"x-keep\": true"), output.Text);
            Assert.IsFalse(output.Text.Contains("Old"), output.Text);
            Assert.IsFalse(output.Text.Contains("stale"), output.Text);
            Assert.IsFalse(output.Text.Contains("\"z\""), output.Text);
            Assert.IsFalse(
                output.Text.Contains("description"),
                "a carry-only member the merged document does not carry " +
                "is never materialized: " + output.Text);
            var reparsed = Read(output.Text);
            Assert.AreEqual("New Title", reparsed.Title);
            Assert.IsNull(reparsed.Description);
            Assert.AreEqual(new[] { "a", "b" }, reparsed.Tags);
            Assert.IsTrue(reparsed.Canonical.WorkflowHash
                == merged.Canonical.WorkflowHash,
                "the merged values must win, so canonical content is unchanged");
        }

        // The writer-omitted forms — an explicit null or empty array the
        // model cannot tell from absent — are the one case a carry-only
        // metadata member is re-inserted, raw text and all.
        [Test]
        public void BuildOutput_CarryOnlyNullOrEmpty_Reinserted()
        {
            var merged = Read(Fixture);
            var carry = Fixture.Replace(
                "  \"nodes\":",
                "  \"title\": null,\n  \"tags\": [  ],\n  \"nodes\":");

            var output = GraphJsonFile.BuildOutput(merged, carry);

            Assert.IsTrue(output.Text.Contains("\"title\": null"), output.Text);
            Assert.IsTrue(output.Text.Contains("\"tags\": [  ]"), output.Text);
            Assert.IsTrue(Read(output.Text).Canonical.WorkflowHash
                == merged.Canonical.WorkflowHash);
        }

        // A carry text that never parsed Ok is refused with an
        // ArgumentException the caller's I/O boundary reports — never an
        // uncaught FormatException from inside the scanner.
        [Test]
        public void BuildOutput_MalformedCarryText_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                GraphJsonFile.BuildOutput(
                    Read(Fixture), "{ \"title\": \"unterminated"));
        }

        [Test]
        public void PublishCreate_ExistingDestination_Refused()
        {
            var path = Path.Combine(_dir, "exists.opweave.json");
            File.WriteAllText(path, "original");
            var output = GraphJsonFile.BuildOutput(Read(Fixture), null);

            var result = GraphJsonFile.PublishCreate(path, output);

            Assert.AreEqual(GraphJsonPublishStatus.AlreadyExists, result.Status);
            Assert.AreEqual("original", File.ReadAllText(path));
        }

        [Test]
        public void PublishCreate_PlantedBeforeMove_NeverOverwritten()
        {
            var path = Path.Combine(_dir, "planted.opweave.json");
            var output = GraphJsonFile.BuildOutput(Read(Fixture), null);

            var result = GraphJsonFile.PublishCreate(
                path,
                output,
                destination => File.WriteAllText(destination, "planted"));

            Assert.AreEqual(
                GraphJsonPublishStatus.AlreadyExists,
                result.Status,
                "a file planted in the check-to-move gap refuses the create");
            Assert.AreEqual(
                "planted",
                File.ReadAllText(path),
                "the planted file is never overwritten");
            Assert.IsEmpty(
                Directory.GetFiles(_dir, "*.tmp"),
                "the temp file is cleaned up");
        }

        [Test]
        public void PublishReplace_BytesChangedSinceRead_Refused()
        {
            var path = Path.Combine(_dir, "replace.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            Assert.IsTrue(disk.Exists);
            Assert.IsNotNull(disk.BytesHash);
            var output = GraphJsonFile.BuildOutput(Read(Fixture), disk.Text);

            File.WriteAllText(path, "external edit");
            var result = GraphJsonFile.PublishReplace(path, output, disk.BytesHash!);

            Assert.AreEqual(
                GraphJsonPublishStatus.ChangedSinceRead,
                result.Status,
                "bytes changed since the read refuse the replace");
            Assert.AreEqual("external edit", File.ReadAllText(path));
            Assert.IsEmpty(
                Directory.GetFiles(_dir, "*.tmp"),
                "the temp file is cleaned up");
        }

        // A sharing violation usually means another process is writing the
        // file: when it releases into new content, the retry's re-verify
        // must refuse rather than overwrite the external bytes.
        [Test]
        public void PublishReplace_SharingViolationThenChanged_Refused()
        {
            var path = Path.Combine(_dir, "race.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var changed = Fixture.Replace(
                "\"operation\": \"core.start\"", "\"operation\": \"core.start.upd\"");
            var output = GraphJsonFile.BuildOutput(Read(changed), disk.Text);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                destination =>
                {
                    if (attempts++ == 0)
                    {
                        File.WriteAllText(destination, "external edit");
                        throw new IOException("simulated sharing violation", 0x20);
                    }
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.ChangedSinceRead,
                result.Status,
                "a violation that releases into new content refuses the replace");
            Assert.AreEqual(
                "external edit",
                File.ReadAllText(path),
                "the external bytes are left untouched");
            Assert.AreEqual(
                1,
                attempts,
                "the retry refused at re-verification, before the replace point");
            Assert.IsEmpty(
                Directory.GetFiles(_dir, "*.tmp"),
                "the temp file is cleaned up");
        }

        // A transient sharing violation on an unchanged destination still
        // succeeds on the bounded retry. [Retry] covers an external process
        // (indexer/scanner class) briefly holding the fresh temp destination:
        // the product consumes another attempt correctly, so the exact-count
        // assertion races the environment — a deterministic budget defect
        // still fails every retry.
        [Test]
        [Retry(3)]
        public void PublishReplace_TransientSharingViolation_Publishes()
        {
            var path = Path.Combine(_dir, "transient.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var changed = Fixture.Replace(
                "\"operation\": \"core.start\"", "\"operation\": \"core.start.upd\"");
            var output = GraphJsonFile.BuildOutput(Read(changed), disk.Text);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    if (attempts++ == 0)
                    {
                        throw new IOException("simulated sharing violation", 0x20);
                    }
                });

            Assert.AreEqual(GraphJsonPublishStatus.Published, result.Status);
            Assert.AreEqual(output.Bytes, File.ReadAllBytes(path));
            Assert.AreEqual(2, attempts, "the transient violation was retried once");
        }

        [Test]
        public void PublishCreate_NewDestination_PublishesBytes()
        {
            var path = Path.Combine(_dir, "new.opweave.json");
            var output = GraphJsonFile.BuildOutput(Read(Fixture), null);

            var result = GraphJsonFile.PublishCreate(path, output);

            Assert.AreEqual(GraphJsonPublishStatus.Published, result.Status);
            Assert.AreEqual(output.Bytes, File.ReadAllBytes(path));
        }

        [Test]
        public void PublishReplace_UnchangedSinceRead_PublishesBytes()
        {
            var path = Path.Combine(_dir, "keep.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var changed = Fixture.Replace(
                "\"operation\": \"core.start\"", "\"operation\": \"core.start.upd\"");
            var output = GraphJsonFile.BuildOutput(Read(changed), disk.Text);

            var result = GraphJsonFile.PublishReplace(path, output, disk.BytesHash!);

            Assert.AreEqual(GraphJsonPublishStatus.Published, result.Status);
            Assert.AreEqual(output.Bytes, File.ReadAllBytes(path));
        }

        private static WorkflowDocument Read(string text)
        {
            var result = WorkflowSerializer.Read(text);
            Assert.IsTrue(
                result.Ok,
                "fixture must parse: " + string.Join(", ", result.Diagnostics));
            return result.Value!;
        }

        private const string Fixture = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.rev"",
  ""nodes"": [
    {
      ""id"": ""start"",
      ""operation"": ""core.start"",
      ""version"": 1
    }
  ],
  ""controlEdges"": [],
  ""layout"": {
    ""nodes"": {
      ""start"": {
        ""x"": 10,
        ""y"": 20
      }
    },
    ""notes"": []
  }
}
";

        // Every ~ below stands for a literal backslash: the fixture text
        // carries the \uXXXX escapes an external editor wrote. The parsed
        // values ("Abc", "a  b", "AB!") are exactly what the canonical
        // writer emits, so only raw-span preservation — never
        // parse-and-rewrite — keeps the member bytes identical.
        private const string Bs = "\\";

        private static readonly string FixtureWithMetadata = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.rev"",
  ""title"": ""A~u0062c"",
  ""description"": ""a~u0020~u0020b"",
  ""tags"": [ ""x"" ,  ""y"" ],
  ""nodes"": [
    {
      ""id"": ""start"",
      ""operation"": ""core.start"",
      ""version"": 1
    }
  ],
  ""controlEdges"": [],
  ""x-keep"": true,
  ""x-obj"": { ""b"" : 1 , ""a"" : [ ""s"" , 2 ] },
  ""x-esc"": ""A~u0042~u0021""
}
".Replace("~", Bs);

        // Carry/merged fixtures for the mismatched-carry test: the carry's
        // raw metadata spans disagree with every value the merged document
        // holds, and its "description" names a member the merged document
        // does not carry at all.
        private static readonly string FixtureCarryStale = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.rev"",
  ""title"": ""Old~u0020Title"",
  ""description"": ""stale"",
  ""tags"": [ ""z"" ],
  ""nodes"": [
    {
      ""id"": ""start"",
      ""operation"": ""core.start"",
      ""version"": 1
    }
  ],
  ""controlEdges"": [],
  ""x-keep"": false
}
".Replace("~", Bs);

        private const string FixtureMerged = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.rev"",
  ""title"": ""New Title"",
  ""tags"": [""a"", ""b""],
  ""nodes"": [
    {
      ""id"": ""start"",
      ""operation"": ""core.start"",
      ""version"": 1
    }
  ],
  ""controlEdges"": [],
  ""x-keep"": true
}
";
    }
}
#endif
