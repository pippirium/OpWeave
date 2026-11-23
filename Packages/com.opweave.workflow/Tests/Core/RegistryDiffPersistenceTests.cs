#nullable enable

// T-W2.2-04 [Core] tests: local:T-W2.2-04:missing-registry-core — SPEC §C3
// rule 7 persistence and diff through the Unity-free
// RegistrySnapshotStore/RegistryDiff pair. A known previous snapshot is
// persisted, then absent, truncated, malformed and unreadable
// registry.json inputs must yield previousSnapshot "unavailable" with an
// explicit reason and no synthetic added/removed diff, while an intact
// comparison produces the deterministic expected kinds (SPEC §C3 test 7).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Tests
{
    internal sealed class RegistryDiffPersistenceTests
    {
        private static readonly ITypeRegistry s_types = DescriptorTestFixtures.CreateRegistry();
        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RegistryStore." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        // ── Fixtures ──────────────────────────────────────────────────────

        private RegistrySnapshotStore Store() => new RegistrySnapshotStore(_root);

        private static OperationDescriptor Describe(
            string id, int version = 1, string summary = "A diff fixture operation.") =>
            Op.Define(id, version)
                .Summary(summary)
                .Output("done", "core.Bool")
                .Build();

        private static OperationCandidate Candidate(
            string id,
            int version = 1,
            OperationOrigin origin = OperationOrigin.Custom,
            string? sourceFile = null,
            string summary = "A diff fixture operation.",
            string implementationHash =
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            string assemblyName = "Acme.Editor") =>
            new OperationCandidate(
                Describe(id, version, summary), origin, assemblyName, sourceFile,
                implementationHash, HasPollImplementation: true);

        private static RegistrySnapshot Snapshot(params OperationCandidate[] candidates) =>
            RegistrySnapshotBuilder.Build(candidates, s_types);

        private static string Hash64(char c) => new string(c, 64);

        // A previous summary recorded through the store itself, so the
        // "known previous RegistrySnapshot" of the ticket is the real
        // persisted form of a real build — not a hand-crafted file.
        private RegistrySnapshot PersistBaseline()
        {
            var baseline = Snapshot(
                Candidate("acme.keep.op", sourceFile: "Editor/Keep.cs"),
                Candidate("acme.gone.op", sourceFile: "Editor/Gone.cs"),
                Candidate("acme.descr.op", sourceFile: "Editor/Descr.cs"),
                Candidate("acme.impl.op", sourceFile: "Editor/Impl.cs",
                    implementationHash: Hash64('a')));
            var written = Store().RecordPublishedSnapshot(baseline);
            Assert.That(written.PreviousSnapshot, Is.EqualTo("unavailable"),
                "fixture: the first record must report an unavailable previous snapshot");
            Assert.That(File.Exists(
                Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName)), Is.True,
                "fixture: the first record still writes registry.json");
            return baseline;
        }

        private static JObject WrittenSummary(string root) =>
            JObject.Parse(File.ReadAllText(
                Path.Combine(root, RegistrySnapshotStore.SnapshotFileName)));

        private static string[] ChangeLines(string root) =>
            File.ReadAllLines(Path.Combine(root, RegistrySnapshotStore.ChangesFileName));

        // ── The four unavailable modes ────────────────────────────────────

        [Test]
        public void MissingFile_ReportsUnavailableAndRecordsNoDiff()
        {
            var result = Store().RecordPublishedSnapshot(PersistedSecond());

            Assert.That(result.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(result.Previous, Is.Null);
            Assert.That(result.UnavailableReason, Does.Contain("missing"));
            Assert.That(result.Changes, Is.Empty,
                "no synthetic added/removed diff may be guessed");
            Assert.That(result.ChangesRecorded, Is.False);
            Assert.That(File.Exists(
                Path.Combine(_root, RegistrySnapshotStore.ChangesFileName)), Is.False,
                "nothing may be appended to registry-changes.ndjson");
        }

        [Test]
        public void TruncatedFile_ReportsUnavailableAndRecordsNoDiff()
        {
            PersistBaseline();
            var path = Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName);
            var bytes = File.ReadAllBytes(path);
            // A cut mid-JSON — the interrupted-write tail shape.
            File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length / 2).ToArray());

            var result = Store().RecordPublishedSnapshot(PersistedSecond());

            Assert.That(result.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(result.UnavailableReason, Is.Not.Null.And.Not.Empty);
            Assert.That(result.Changes, Is.Empty);
            Assert.That(result.ChangesRecorded, Is.False);
            AssertNoChangeLines();
        }

        [Test]
        public void MalformedFile_ReportsUnavailableAndRecordsNoDiff()
        {
            PersistBaseline();
            File.WriteAllText(
                Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName),
                "{ not json at all !!",
                s_utf8);

            var result = Store().RecordPublishedSnapshot(PersistedSecond());

            Assert.That(result.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(result.UnavailableReason, Does.Contain("not valid JSON"));
            Assert.That(result.Changes, Is.Empty);
            AssertNoChangeLines();
        }

        [Test]
        public void ShapeBrokenFile_ReportsUnavailableAndRecordsNoDiff()
        {
            PersistBaseline();
            // Well-formed JSON, wrong member shape — still untrusted data.
            File.WriteAllText(
                Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName),
                "{\"generationId\":\"reg_x\",\"registryHash\":\"" + Hash64('f') +
                    "\",\"operations\":\"nope\"}",
                s_utf8);

            var result = Store().RecordPublishedSnapshot(PersistedSecond());

            Assert.That(result.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(result.UnavailableReason, Does.Contain("operations"));
            Assert.That(result.Changes, Is.Empty);
            AssertNoChangeLines();
        }

        [Test]
        public void UnreadableFile_ReportsUnavailableAndRecordsNoDiff()
        {
            PersistBaseline();
            var store = Store();
            store.ReadProbe = path => new IOException("simulated locked file");

            var result = store.RecordPublishedSnapshot(PersistedSecond());

            Assert.That(result.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(result.UnavailableReason, Does.Contain("could not be read"));
            Assert.That(result.Changes, Is.Empty);
            Assert.That(result.ChangesRecorded, Is.False);
            AssertNoChangeLines();
        }

        private void AssertNoChangeLines()
        {
            var changes = Path.Combine(_root, RegistrySnapshotStore.ChangesFileName);
            Assert.That(
                !File.Exists(changes) || File.ReadAllText(changes).Length == 0, Is.True,
                "nothing may be appended to registry-changes.ndjson");
        }

        // The second build used by the failure-mode cases: anything at all —
        // the point is that its diff is never computed against untrusted
        // data, never "everything added".
        private static RegistrySnapshot PersistedSecond() =>
            Snapshot(
                Candidate("acme.keep.op", sourceFile: "Editor/Keep.cs"),
                Candidate("acme.new.op", sourceFile: "Editor/New.cs"));

        // ── Intact snapshot: deterministic diff kinds ─────────────────────

        [Test]
        public void IntactComparison_ProducesExpectedKindsInOrder()
        {
            PersistBaseline();
            var second = Snapshot(
                // acme.keep.op unchanged.
                Candidate("acme.keep.op", sourceFile: "Editor/Keep.cs"),
                // acme.gone.op removed (absent).
                // acme.descr.op: descriptorHash moved.
                Candidate("acme.descr.op", sourceFile: "Editor/Descr.cs",
                    summary: "A changed summary."),
                // acme.impl.op: implementationHash only.
                Candidate("acme.impl.op", sourceFile: "Editor/Impl.cs",
                    implementationHash: Hash64('b')),
                // acme.new.op added.
                Candidate("acme.new.op", sourceFile: "Editor/New.cs"),
                // acme.broken.op: a rejected candidate — the failed kind.
                Candidate("core.broken.op"));

            var result = Store().RecordPublishedSnapshot(second);

            Assert.That(result.PreviousSnapshot, Is.EqualTo("available"));
            Assert.That(result.Previous, Is.Not.Null);
            Assert.That(result.UnavailableReason, Is.Null);
            Assert.That(result.ChangesRecorded, Is.True);
            Assert.That(result.PersistenceError, Is.Null);

            var kinds = new List<(string Kind, string Id)>();
            foreach (var change in result.Changes)
            {
                kinds.Add((change.Kind, change.OperationId));
            }
            CollectionAssert.AreEquivalent(
                new[]
                {
                    (RegistryChangeKinds.Removed, "acme.gone.op"),
                    (RegistryChangeKinds.DescriptorChanged, "acme.descr.op"),
                    (RegistryChangeKinds.ImplementationChanged, "acme.impl.op"),
                    (RegistryChangeKinds.Added, "acme.new.op"),
                    (RegistryChangeKinds.Failed, "core.broken.op"),
                },
                kinds);

            // SPEC §C3 test 4: removed keeps the previous definition's
            // source file — the D3 "previously at <file>" hint data.
            RegistryChange? removed = null;
            foreach (var change in result.Changes)
            {
                if (change.Kind == RegistryChangeKinds.Removed)
                {
                    removed = change;
                }
            }
            Assert.That(removed, Is.Not.Null);
            Assert.That(removed!.SourceFile, Is.EqualTo("Editor/Gone.cs"));

            // Deterministic order: id Ordinal then version ascending, the
            // build's failed records last.
            Assert.That(result.Changes[0].OperationId, Is.EqualTo("acme.descr.op"));
            Assert.That(result.Changes[1].OperationId, Is.EqualTo("acme.gone.op"));
            Assert.That(result.Changes[2].OperationId, Is.EqualTo("acme.impl.op"));
            Assert.That(result.Changes[3].OperationId, Is.EqualTo("acme.new.op"));
            Assert.That(result.Changes[4].Kind, Is.EqualTo(RegistryChangeKinds.Failed));
        }

        [Test]
        public void IdenticalContent_RecordsNoDiff()
        {
            var baseline = PersistBaseline();
            var result = Store().RecordPublishedSnapshot(baseline);

            Assert.That(result.PreviousSnapshot, Is.EqualTo("available"));
            Assert.That(result.Changes, Is.Empty,
                "an identical rebuild produces no diff records");
            Assert.That(result.ChangesRecorded, Is.False);
            AssertNoChangeLines();
        }

        // ── registry.json contents: the §C3 rule-7 summary fields ─────────

        [Test]
        public void PersistedSummary_CarriesEveryRequiredField()
        {
            var snapshot = Snapshot(
                Candidate("acme.full.op", version: 3,
                    origin: OperationOrigin.Custom,
                    sourceFile: "Editor/Full.cs",
                    implementationHash: Hash64('c')));

            Store().RecordPublishedSnapshot(snapshot);
            var written = WrittenSummary(_root);

            Assert.That((string)written["generationId"]!, Is.EqualTo(snapshot.GenerationId));
            Assert.That((string)written["registryHash"]!, Is.EqualTo(snapshot.RegistryHash));
            var operations = (JArray)written["operations"]!;
            Assert.That(operations.Count, Is.EqualTo(1));
            var entry = (JObject)operations[0];
            Assert.That((string)entry["id"]!, Is.EqualTo("acme.full.op"));
            Assert.That((int)entry["version"]!, Is.EqualTo(3));
            Assert.That((string)entry["origin"]!, Is.EqualTo("custom"));
            Assert.That((string)entry["assemblyName"]!, Is.EqualTo("Acme.Editor"));
            Assert.That((string)entry["sourceFile"]!, Is.EqualTo("Editor/Full.cs"));
            Assert.That((string)entry["descriptorHash"]!,
                Is.EqualTo(snapshot.Operations[0].DescriptorHash));
            Assert.That((string)entry["implementationHash"]!, Is.EqualTo(Hash64('c')));

            // The round trip decodes to a trusted available read.
            var read = Store().Read();
            Assert.That(read.Available, Is.True);
            Assert.That(read.Snapshot!.GenerationId, Is.EqualTo(snapshot.GenerationId));
            Assert.That(read.Snapshot.Operations.Count, Is.EqualTo(1));
            Assert.That(read.Snapshot.Operations[0].Origin, Is.EqualTo(OperationOrigin.Custom));
        }

        [Test]
        public void ChangeLines_AreCanonicalNdjsonWithAttribution()
        {
            PersistBaseline();
            var second = Snapshot(
                Candidate("acme.keep.op", sourceFile: "Editor/Keep.cs"),
                Candidate("acme.new.op", sourceFile: "Editor/New.cs"));
            var result = Store().RecordPublishedSnapshot(second);

            var lines = ChangeLines(_root);
            Assert.That(lines, Has.Length.EqualTo(result.Changes.Count));
            var added = lines
                .Select(ParseLine)
                .Single(line => (string)line["kind"]! == RegistryChangeKinds.Added);
            Assert.That((string)added["operationId"]!, Is.EqualTo("acme.new.op"));
            Assert.That((int)added["version"]!, Is.EqualTo(1));
            Assert.That((string)added["sourceFile"]!, Is.EqualTo("Editor/New.cs"));
            Assert.That((string)added["generationId"]!, Is.EqualTo(second.GenerationId));
            Assert.That((string)added["recordedAt"]!, Does.Match(@"\d{4}-\d{2}-\d{2}T.*Z$"));
            Assert.That(added["diagnostics"] is JArray, Is.True);
            // Every line is valid Canonical JSON.
            foreach (var line in lines)
            {
                Assert.That(CanonicalJson.Serialize(ParseLine(line)), Is.EqualTo(line));
            }
        }

        // Newtonsoft's default reader eagerly converts ISO timestamp strings
        // to DateTime — the field assertions need the raw written values.
        private static JObject ParseLine(string line)
        {
            using var reader = new JsonTextReader(new StringReader(line))
            {
                DateParseHandling = DateParseHandling.None,
            };
            return JObject.Load(reader);
        }

        // ── FindRemovedDefinitions: the durable OW-OP-001 hint data ────

        [Test]
        public void FindRemovedDefinitions_NoHistory_ReturnsEmpty()
        {
            Assert.That(Store().FindRemovedDefinitions(), Is.Empty,
                "a missing registry-changes.ndjson yields no previous definitions");
        }

        // The build that deletes acme.gone.op, reusable across cases.
        private RegistrySnapshot DeletingSnapshot() =>
            Snapshot(
                Candidate("acme.keep.op", sourceFile: "Editor/Keep.cs"),
                Candidate("acme.descr.op", sourceFile: "Editor/Descr.cs"),
                Candidate("acme.impl.op", sourceFile: "Editor/Impl.cs",
                    implementationHash: Hash64('a')));

        [Test]
        public void FindRemovedDefinitions_RemovedSurvivesAnUnrelatedRebuild()
        {
            PersistBaseline();
            var deleting = DeletingSnapshot();
            var second = Store().RecordPublishedSnapshot(deleting);
            Assert.That(second.Changes.Any(change =>
                    change.Kind == RegistryChangeKinds.Removed &&
                    change.OperationId == "acme.gone.op"),
                Is.True, "fixture: the deletion build must record acme.gone.op removed");

            // An unrelated later build: identical content, no new records
            // for the pair — its in-memory batch cannot answer the hint.
            var unrelated = Store().RecordPublishedSnapshot(deleting);
            Assert.That(unrelated.Changes, Is.Empty);

            var previous = Store().FindRemovedDefinitions();
            var record = previous.SingleOrDefault(change =>
                change.OperationId == "acme.gone.op" && change.Version == 1);
            Assert.That(record, Is.Not.Null,
                "a still-deleted Operation keeps its previous-definition record");
            Assert.That(record!.Kind, Is.EqualTo(RegistryChangeKinds.Removed));
            Assert.That(record.SourceFile, Is.EqualTo("Editor/Gone.cs"),
                "the D3 'previously at <file>' hint needs the recorded source file");
        }

        [Test]
        public void FindRemovedDefinitions_LaterAddedCancelsTheRemoval()
        {
            PersistBaseline();
            var store = Store();
            store.RecordPublishedSnapshot(DeletingSnapshot());

            var restored = Snapshot(
                Candidate("acme.keep.op", sourceFile: "Editor/Keep.cs"),
                Candidate("acme.gone.op", sourceFile: "Editor/Gone.cs"),
                Candidate("acme.descr.op", sourceFile: "Editor/Descr.cs"),
                Candidate("acme.impl.op", sourceFile: "Editor/Impl.cs",
                    implementationHash: Hash64('a')));
            var readded = store.RecordPublishedSnapshot(restored);
            Assert.That(readded.Changes.Any(change =>
                    change.Kind == RegistryChangeKinds.Added &&
                    change.OperationId == "acme.gone.op"),
                Is.True, "fixture: the restore build must record acme.gone.op added");

            Assert.That(store.FindRemovedDefinitions()
                    .Any(change => change.OperationId == "acme.gone.op"),
                Is.False, "a re-added Operation is no longer a removed definition");
        }

        [Test]
        public void FindRemovedDefinitions_SkipsCorruptLines()
        {
            PersistBaseline();
            Store().RecordPublishedSnapshot(DeletingSnapshot());

            var path = Path.Combine(_root, RegistrySnapshotStore.ChangesFileName);
            File.AppendAllText(path,
                "{ not json at all !!\n" +
                "{\"kind\":\"moved\",\"operationId\":\"acme.gone.op\",\"version\":1}\n" +
                "{\"kind\":\"removed\",\"operationId\":\"acme.broken.op\",\"version\":0}\n",
                s_utf8);

            var previous = Store().FindRemovedDefinitions();
            var record = previous.SingleOrDefault(change => change.OperationId == "acme.gone.op");
            Assert.That(record, Is.Not.Null,
                "corrupt and unknown-kind lines must not lose the removal record");
            Assert.That(record!.SourceFile, Is.EqualTo("Editor/Gone.cs"));
        }

        [Test]
        public void FindRemovedDefinitions_UnreadableHistory_ReturnsEmpty()
        {
            var path = Path.Combine(_root, RegistrySnapshotStore.ChangesFileName);
            File.WriteAllText(path, "seeded\n", s_utf8);
            var store = Store();
            using (var hold = new FileStream(
                       path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(store.FindRemovedDefinitions(), Is.Empty,
                    "an unreadable registry-changes.ndjson degrades to empty, never throws");
            }
        }

        // ── registry.json entry validation ────────────────────────────────

        [Test]
        public void TryRead_VersionOutsideRange_ReportsCorrupt()
        {
            PersistBaseline();
            var path = Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName);
            var written = WrittenSummary(_root);
            var entry = (JObject)((JArray)written["operations"]!)[0];

            foreach (var badVersion in new object[] { 0L, -2L, 3000000000L })
            {
                entry["version"] = new JValue(badVersion);
                File.WriteAllText(path, CanonicalJson.Serialize(written), s_utf8);

                var read = Store().Read();

                Assert.That(read.Available, Is.False,
                    "version " + badVersion + " is outside 1..int.MaxValue — corrupt, not truncated");
                Assert.That(read.UnavailableReason, Does.Contain("version"));
            }
        }

        // ── registry.json internal consistency ────────────────────────────

        // Projects one persisted entry onto the SPEC §C3 rule-6 registryHash
        // input tuple, so a tampered file can carry a hash that correctly
        // covers its own corrupt operations — leaving exactly one check
        // that can still reject it.
        private static (string Id, int Version, string DescriptorHash, string ImplementationHash)
            HashInputOf(JToken entry) =>
            ((string)entry["id"]!, (int)entry["version"]!,
                (string)entry["descriptorHash"]!, (string)entry["implementationHash"]!);

        [Test]
        public void DuplicateOperationKey_ReportsUnavailableAndRecordsNoDiff()
        {
            PersistBaseline();
            var path = Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName);
            var written = WrittenSummary(_root);
            var operations = (JArray)written["operations"]!;
            // A second entry for an existing (id, version): keyed lookups
            // would silently collapse it into false added/removed records.
            operations.Add(operations[0].DeepClone());
            // Recompute the covering hash so only the duplicate rule can
            // reject — a correct hash over corrupt content is still corrupt.
            written["registryHash"] =
                RegistrySnapshotBuilder.ComputeRegistryHash(operations.Select(HashInputOf));
            File.WriteAllText(path, CanonicalJson.Serialize(written), s_utf8);

            var result = Store().RecordPublishedSnapshot(PersistedSecond());

            Assert.That(result.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(result.UnavailableReason, Does.Contain("duplicate"));
            Assert.That(result.Changes, Is.Empty);
            Assert.That(result.ChangesRecorded, Is.False);
            AssertNoChangeLines();
        }

        [Test]
        public void HashInconsistentFile_ReportsUnavailableAndRecordsNoDiff()
        {
            PersistBaseline();
            var path = Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName);
            var written = WrittenSummary(_root);
            // Well-formed JSON whose operations no longer cover the
            // declared registryHash — internally inconsistent, untrusted.
            written["operations"] = new JArray();
            File.WriteAllText(path, CanonicalJson.Serialize(written), s_utf8);

            var result = Store().RecordPublishedSnapshot(PersistedSecond());

            Assert.That(result.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(result.UnavailableReason, Does.Contain("registryHash"));
            Assert.That(result.Changes, Is.Empty);
            Assert.That(result.ChangesRecorded, Is.False);
            AssertNoChangeLines();
        }

        [Test]
        public void HashConsistentFile_StillCompares()
        {
            // The intact round trip: the writer's hash is the reader's
            // recomputed hash, so a real summary stays available and the
            // deterministic diff still runs (the §C3 test-7 baseline the
            // two corruption cases above must not regress).
            PersistBaseline();
            var second = PersistedSecond();

            var result = Store().RecordPublishedSnapshot(second);

            Assert.That(result.PreviousSnapshot, Is.EqualTo("available"));
            Assert.That(result.UnavailableReason, Is.Null);
            Assert.That(result.ChangesRecorded, Is.True);
            // Deterministic order: id Ordinal — descr < gone < impl removed,
            // then new added (keep is unchanged, never recorded).
            Assert.That(
                result.Changes.Select(change => (change.Kind, change.OperationId)),
                Is.EqualTo(new[]
                {
                    (RegistryChangeKinds.Removed, "acme.descr.op"),
                    (RegistryChangeKinds.Removed, "acme.gone.op"),
                    (RegistryChangeKinds.Removed, "acme.impl.op"),
                    (RegistryChangeKinds.Added, "acme.new.op"),
                }));
        }

        // ── Atomicity / healing ───────────────────────────────────────────

        [Test]
        public void RecordWritesRegistryJsonAtomically_AndHealsNextBuild()
        {
            // A corrupt file still lets the build write a fresh summary, so
            // the build after it diffs normally again.
            File.WriteAllText(
                Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName),
                "garbage", s_utf8);
            var corrupted = Store().RecordPublishedSnapshot(PersistedSecond());
            Assert.That(corrupted.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(
                Path.Combine(_root, RegistrySnapshotStore.SnapshotFileName + ".opweave-tmp"),
                Does.Not.Exist, "the temp file never survives a completed replace");

            var healed = Store().RecordPublishedSnapshot(PersistedSecond());
            Assert.That(healed.PreviousSnapshot, Is.EqualTo("available"));
            Assert.That(healed.Changes, Is.Empty,
                "identical content diffs to nothing on the build after healing");
        }
    }
}
