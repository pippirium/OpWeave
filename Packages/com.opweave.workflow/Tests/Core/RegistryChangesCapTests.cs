#nullable enable

// T-W4.4-01 [Core] tests: local:T-W4.4-01:retention — the SPEC §F8 / §C3
// rule-7 cap on registry-changes.ndjson: after every durable append the
// file keeps the latest 1,000 entries, the oldest lines dropped through
// the §F3 item-2 atomic replace. Verified over the Unity-free
// RegistrySnapshotStore — the same sources run under dotnet and Unity
// Mono.
using System;
using System.Collections.Generic;
using System.IO;
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
    internal sealed class RegistryChangesCapTests
    {
        private static readonly ITypeRegistry s_types = DescriptorTestFixtures.CreateRegistry();
        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RegistryCap." + Guid.NewGuid().ToString("N"));
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

        private static OperationDescriptor Describe(string id, int version = 1) =>
            Op.Define(id, version)
                .Summary("A cap fixture operation.")
                .Output("done", "core.Bool")
                .Build();

        private static RegistrySnapshot Snapshot(params OperationCandidate[] candidates) =>
            RegistrySnapshotBuilder.Build(candidates, s_types);

        private static OperationCandidate Candidate(string id) =>
            new OperationCandidate(
                Describe(id), OperationOrigin.Custom, "Acme.Editor", "Editor/Op.cs",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                HasPollImplementation: true);

        private static PersistedRegistrySnapshot Summary() =>
            PersistedRegistrySnapshot.FromSnapshot(Snapshot(Candidate("acme.cap.op")));

        private static List<RegistryChange> Batch(string prefix, int count, int start = 0)
        {
            var changes = new List<RegistryChange>(count);
            for (var i = start; i < start + count; i++)
            {
                changes.Add(new RegistryChange(
                    RegistryChangeKinds.Added, prefix + i.ToString("D4"), 1, null,
                    Array.Empty<Diagnostic>()));
            }
            return changes;
        }

        private string[] ChangeLines() =>
            File.ReadAllLines(Path.Combine(_root, RegistrySnapshotStore.ChangesFileName));

        private static JObject ParseLine(string line)
        {
            using var reader = new JsonTextReader(new StringReader(line))
            {
                DateParseHandling = DateParseHandling.None,
            };
            return JObject.Load(reader);
        }

        // ── The 1,000-entry cap ───────────────────────────────────────────

        // A single append past the cap keeps exactly the latest 1,000
        // entries — the newest records, in file order.
        [Test]
        public void SingleOverflowAppend_KeepsLatest1000()
        {
            var store = Store();
            const int total = 1005;
            store.AppendChanges(Summary(), Batch("acme.op.", total));

            var lines = ChangeLines();
            Assert.AreEqual(1000, lines.Length, "the file is capped at 1,000 entries");
            // The five oldest were dropped; the kept tail starts at index 5.
            Assert.AreEqual("acme.op.0005", (string?)ParseLine(lines[0])["operationId"]);
            Assert.AreEqual("acme.op.1004", (string?)ParseLine(lines[999])["operationId"]);
            foreach (var line in lines)
            {
                Assert.AreEqual(
                    CanonicalJson.Serialize(ParseLine(line)), line,
                    "every kept line stays verbatim Canonical JSON");
            }
        }

        // The cap holds across appends: a file at 700 entries receiving a
        // 500-entry batch keeps the last 1,000 of the combined 1,200 — the
        // whole second batch plus the newest 200 of the first.
        [Test]
        public void IncrementalOverflow_KeepsLatest1000AcrossBatches()
        {
            var store = Store();
            var summary = Summary();
            store.AppendChanges(summary, Batch("acme.first.", 700));
            Assert.AreEqual(700, ChangeLines().Length,
                "no rewrite below the cap");
            store.AppendChanges(summary, Batch("acme.second.", 500));

            var lines = ChangeLines();
            Assert.AreEqual(1000, lines.Length);
            Assert.AreEqual("acme.first.0200", (string?)ParseLine(lines[0])["operationId"],
                "the 200 oldest of the first batch were dropped");
            Assert.AreEqual("acme.second.0000", (string?)ParseLine(lines[500])["operationId"]);
            Assert.AreEqual("acme.second.0499", (string?)ParseLine(lines[999])["operationId"]);
        }

        // Exactly at the cap nothing is rewritten; one over, one oldest
        // entry leaves.
        [Test]
        public void ExactlyAtCap_IsUntouched_OneOverDropsOne()
        {
            var store = Store();
            var summary = Summary();
            store.AppendChanges(summary, Batch("acme.op.", 1000));
            var path = Path.Combine(_root, RegistrySnapshotStore.ChangesFileName);
            var before = File.ReadAllBytes(path);
            store.AppendChanges(summary, Batch("acme.more.", 0));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
                "at the cap an empty append rewrites nothing");

            store.AppendChanges(summary, Batch("acme.more.", 1));
            var lines = ChangeLines();
            Assert.AreEqual(1000, lines.Length);
            Assert.AreEqual("acme.op.0001", (string?)ParseLine(lines[0])["operationId"]);
            Assert.AreEqual("acme.more.0000", (string?)ParseLine(lines[999])["operationId"]);
        }

        // A file left above the cap by an older writer is trimmed on the
        // next append — the bound holds after every pass, not only for
        // files this store version produced.
        [Test]
        public void PreExistingOverflow_IsTrimmedOnNextAppend()
        {
            var path = Path.Combine(_root, RegistrySnapshotStore.ChangesFileName);
            var builder = new StringBuilder();
            for (var i = 0; i < 1200; i++)
            {
                builder.Append("{\"kind\":\"added\",\"operationId\":\"acme.old.")
                    .Append(i.ToString("D4"))
                    .Append("\",\"version\":1}\n");
            }
            File.WriteAllText(path, builder.ToString(), s_utf8);

            Store().AppendChanges(Summary(), Batch("acme.new.", 1));

            var lines = ChangeLines();
            Assert.AreEqual(1000, lines.Length);
            Assert.AreEqual("acme.old.0201", (string?)ParseLine(lines[0])["operationId"]);
            Assert.AreEqual("acme.new.0000", (string?)ParseLine(lines[999])["operationId"]);
        }

        // The durable previous-definition scan (the OW-OP-001 hint source)
        // keeps working on a capped history.
        [Test]
        public void FindRemovedDefinitions_WorksAfterCapTrim()
        {
            var store = Store();
            var summary = Summary();
            var changes = Batch("acme.op.", 1001);
            changes.Add(new RegistryChange(
                RegistryChangeKinds.Removed, "acme.gone.op", 1, "Editor/Gone.cs",
                Array.Empty<Diagnostic>()));
            store.AppendChanges(summary, changes);

            var removed = store.FindRemovedDefinitions();
            Assert.AreEqual(1, removed.Count);
            Assert.AreEqual("acme.gone.op", removed[0].OperationId);
            Assert.AreEqual("Editor/Gone.cs", removed[0].SourceFile);
            Assert.AreEqual(1000, ChangeLines().Length);
        }
    }
}
