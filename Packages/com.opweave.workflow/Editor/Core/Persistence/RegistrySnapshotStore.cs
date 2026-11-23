#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The result of reading the persisted <c>registry.json</c>: the decoded
    /// previous <see cref="PersistedRegistrySnapshot"/>, or the reason it is
    /// unavailable (SPEC §C3 rule 7 — the previous Registry unknown is a
    /// normal state, never an error diagnostic).
    /// </summary>
    public sealed record PersistedRegistryRead(
        PersistedRegistrySnapshot? Snapshot,
        string? UnavailableReason)
    {
        /// <summary>True when a trusted previous summary was decoded.</summary>
        public bool Available => Snapshot is not null;
    }

    /// <summary>
    /// The SPEC §C3 rule-7 persistence boundary of the Registry: writes the
    /// published snapshot's summary to <c>registry.json</c> through the
    /// §F3 item-2 temp-file atomic replace, reads it back on the next build,
    /// computes the diff and appends it to <c>registry-changes.ndjson</c>.
    /// System.IO only under an injected root, so the whole pass runs under
    /// <c>dotnet test</c> without the Editor (SPEC §F1, T-W2.2-04).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RecordPublishedSnapshot"/> is the single pass every
    /// published build runs: read the previous summary → evaluate the diff
    /// → append the batch (added/removed/descriptorChanged/
    /// implementationChanged plus the snapshot's own
    /// <see cref="RegistryChangeKinds.Failed"/> records) → write the new
    /// summary. A missing, corrupt or unreadable <c>registry.json</c>
    /// produces no diff at all — SPEC §C3 rule 7 forbids guessing (never
    /// "everything is added") — while the new summary is still written so
    /// the following build can diff again.
    /// </para>
    /// <para>
    /// Write order keeps the change history at-least-once: the ndjson append
    /// happens before the <c>registry.json</c> replace, so a crash between
    /// the two re-records the same diff on the next build rather than
    /// silently dropping it. A write failure degrades the pass to
    /// <see cref="RegistryDiffResult.PersistenceError"/>: the snapshot stays
    /// published — persistence never fails a build. The file is opened
    /// <see cref="FileMode.Append"/>/<see cref="FileShare.Read"/> and
    /// <c>Flush(flushToDisk: true)</c> completes before the write returns
    /// (SPEC §F3 item 1 for the append; item 2 for the replace).
    /// </para>
    /// <para>
    /// <see cref="AppendChanges"/> enforces the SPEC §F8 retention cap
    /// after the durable append: once the file holds more than
    /// <see cref="ChangesEntryCap"/> entries, the oldest lines are dropped
    /// by rewriting the newest 1,000 through the §F3 item-2 atomic replace,
    /// so the history always keeps the latest 1,000 entries. A crash
    /// between the append and the rewrite leaves an over-long file whose
    /// newest entries are all durable; the next append trims it again.
    /// Core is called from the main thread only,
    /// so no synchronization is taken.
    /// </para>
    /// </remarks>
    public sealed class RegistrySnapshotStore
    {
        /// <summary>The file the previous snapshot's summary lives in (SPEC §F1).</summary>
        public const string SnapshotFileName = "registry.json";

        /// <summary>The append-only diff history of SPEC §C3 rule 7 (SPEC §F1).</summary>
        public const string ChangesFileName = "registry-changes.ndjson";

        /// <summary>
        /// The SPEC §F8 cap on <c>registry-changes.ndjson</c>: the file
        /// keeps the latest 1,000 entries (SPEC §C3 rule 7).
        /// </summary>
        internal const int ChangesEntryCap = 1000;

        private const string TempSuffix = ".opweave-tmp";
        private const int MaxReplaceAttempts = 5;
        private const int ReplaceRetryDelayMilliseconds = 15;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private readonly string _root;
        private readonly Func<string> _now;

        /// <summary>
        /// The store rooted at <paramref name="root"/> — the Run Store root
        /// (<c>Library/OpWeave/</c>) in production, any temp root in tests.
        /// <paramref name="now"/> supplies the <c>recordedAt</c> timestamp
        /// (<see cref="UtcTimestamp.Now"/> in production).
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="now"/> is null.</exception>
        public RegistrySnapshotStore(string root, Func<string>? now = null)
        {
            if (string.IsNullOrEmpty(root))
            {
                throw new ArgumentException(
                    "The store root path must not be null or empty.", nameof(root));
            }
            _root = root;
            _now = now ?? UtcTimestamp.Now;
        }

        /// <summary>The absolute path of <c>registry.json</c>.</summary>
        public string SnapshotPath => Path.Combine(_root, SnapshotFileName);

        /// <summary>The absolute path of <c>registry-changes.ndjson</c>.</summary>
        public string ChangesPath => Path.Combine(_root, ChangesFileName);

        /// <summary>
        /// The test seam of the read path (the
        /// <see cref="FileRunStore"/> probe convention): invoked with the
        /// registry.json path before the real read; a non-null return is
        /// thrown as the simulated failure, reproducing an unreadable file
        /// deterministically on every platform.
        /// </summary>
        internal Func<string, Exception?>? ReadProbe { get; set; }

        /// <summary>
        /// Reads the previously persisted summary. A missing file, a failed
        /// read, undecodable UTF-8, a strict-parse failure or any member
        /// shape deviation yields <see cref="PersistedRegistryRead.Snapshot"/>
        /// null with the reason — never an exception and never a
        /// partially trusted object (SPEC §C3 rule 7).
        /// </summary>
        public PersistedRegistryRead Read()
        {
            var path = SnapshotPath;
            string text;
            try
            {
                if (!File.Exists(path))
                {
                    return new PersistedRegistryRead(null, "registry.json is missing");
                }
                var simulated = ReadProbe?.Invoke(path);
                if (simulated is not null)
                {
                    throw simulated;
                }
                text = s_utf8.GetString(File.ReadAllBytes(path));
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return new PersistedRegistryRead(
                    null, "registry.json could not be read (" + e.GetType().Name + ")");
            }
            catch (DecoderFallbackException)
            {
                return new PersistedRegistryRead(null, "registry.json is not valid UTF-8");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new PersistedRegistryRead(null, "registry.json is empty");
            }
            var parsed = CanonicalJson.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                return new PersistedRegistryRead(null, "registry.json is not valid JSON");
            }
            return PersistedRegistrySnapshot.TryRead(parsed.Value, out var snapshot, out var reason)
                ? new PersistedRegistryRead(snapshot, null)
                : new PersistedRegistryRead(null, reason);
        }

        /// <summary>
        /// The whole SPEC §C3 rule-7 persistence pass for one published
        /// build — the same method the UnityAdapter wrapper calls (no
        /// divergent Editor-side reimplementation): read the previous
        /// summary, evaluate the diff when it can be trusted, append the
        /// batch, then replace <c>registry.json</c> with the new summary.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        public RegistryDiffResult RecordPublishedSnapshot(RegistrySnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var previous = Read();
            var current = PersistedRegistrySnapshot.FromSnapshot(snapshot);
            var changes = new List<RegistryChange>();
            if (previous.Snapshot is not null)
            {
                changes.AddRange(RegistryDiff.Evaluate(previous.Snapshot, current));
                // The failed kind of the §C3 rule-7 vocabulary is the new
                // build's own per-candidate rejections — the snapshot's
                // Failures, not a previous-vs-current comparison.
                for (var i = 0; i < snapshot.Failures.Count; i++)
                {
                    changes.Add(snapshot.Failures[i]);
                }
            }

            string? persistenceError = null;
            var recorded = false;
            if (changes.Count != 0)
            {
                try
                {
                    AppendChanges(current, changes);
                    recorded = true;
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                    persistenceError =
                        "registry-changes.ndjson could not be appended (" + e.GetType().Name + ")";
                }
            }

            try
            {
                Write(current);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                var error =
                    "registry.json could not be written (" + e.GetType().Name + ")";
                persistenceError = persistenceError is null
                    ? error
                    : persistenceError + "; " + error;
            }

            return new RegistryDiffResult(
                previous.Snapshot,
                current,
                changes,
                recorded,
                previous.UnavailableReason,
                persistenceError);
        }

        /// <summary>
        /// Writes <paramref name="summary"/> as Canonical JSON bytes through
        /// the SPEC §F3 item-2 atomic replace.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="summary"/> is null.</exception>
        public void Write(PersistedRegistrySnapshot summary)
        {
            if (summary is null)
            {
                throw new ArgumentNullException(nameof(summary));
            }
            Directory.CreateDirectory(_root);
            ReplaceAtomically(
                SnapshotPath, s_utf8.GetBytes(CanonicalJson.Serialize(summary.ToJson())));
        }

        /// <summary>
        /// Appends <paramref name="changes"/> to
        /// <c>registry-changes.ndjson</c> — one Canonical JSON object per
        /// line: the RegistryChange fields plus the build's
        /// <c>generationId</c> and the <c>recordedAt</c> timestamp, so the
        /// history stays attributable to a build (the record fields are a
        /// local, reversible format choice — SPEC names only the file, the
        /// kinds and the F8 retention bound). After the fsync'd append the
        /// SPEC §F8 cap is enforced: the file keeps the latest
        /// <see cref="ChangesEntryCap"/> entries, the oldest lines dropped
        /// through the same atomic replace the summary uses.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public void AppendChanges(
            PersistedRegistrySnapshot current, IReadOnlyList<RegistryChange> changes)
        {
            if (current is null)
            {
                throw new ArgumentNullException(nameof(current));
            }
            if (changes is null)
            {
                throw new ArgumentNullException(nameof(changes));
            }
            var builder = new StringBuilder(changes.Count * 160);
            var recordedAt = _now();
            for (var i = 0; i < changes.Count; i++)
            {
                builder.Append(CanonicalJson.Serialize(Line(current, changes[i], recordedAt)));
                builder.Append('\n');
            }
            Directory.CreateDirectory(_root);
            var bytes = s_utf8.GetBytes(builder.ToString());
            using (var stream = new FileStream(
                       ChangesPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            EnforceChangesCap();
        }

        // The SPEC §F8 retention bound of registry-changes.ndjson: when the
        // file exceeds ChangesEntryCap entries, the newest cap entries are
        // rewritten through the §F3 item-2 atomic replace and the oldest
        // dropped. Entries are counted and cut at line boundaries on the
        // raw bytes — a newline-terminated file is the common shape, but a
        // trailing line without its '\n' still counts as an entry and is
        // never split. A failure surfaces as an I/O exception for the
        // caller's persistence-error mapping, exactly like the append.
        private void EnforceChangesCap()
        {
            var path = ChangesPath;
            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(path);
            }
            catch (Exception e) when (
                e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                return;
            }
            var entries = 0;
            var lastEnd = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == (byte)'\n')
                {
                    entries++;
                    lastEnd = i + 1;
                }
            }
            if (lastEnd < raw.Length)
            {
                entries++;
            }
            if (entries <= ChangesEntryCap)
            {
                return;
            }
            // The kept tail starts after the (entries - cap)-th line
            // boundary, so exactly cap entries remain — a trailing partial
            // line included.
            var skip = entries - ChangesEntryCap;
            var offset = 0;
            var seen = 0;
            while (seen < skip && offset < raw.Length)
            {
                if (raw[offset] == (byte)'\n')
                {
                    seen++;
                }
                offset++;
            }
            var tail = new byte[raw.Length - offset];
            Buffer.BlockCopy(raw, offset, tail, 0, tail.Length);
            ReplaceAtomically(path, tail);
        }

        private static JObject Line(
            PersistedRegistrySnapshot current, RegistryChange change, string recordedAt)
        {
            var diagnostics = new JArray();
            for (var i = 0; i < change.Diagnostics.Count; i++)
            {
                var diagnostic = change.Diagnostics[i];
                var entry = new JObject
                {
                    ["code"] = diagnostic.Code,
                    ["severity"] = JsonNaming.ToSnakeCase(diagnostic.Severity.ToString()),
                    ["message"] = diagnostic.Message,
                };
                if (diagnostic.Hint is not null)
                {
                    entry["hint"] = diagnostic.Hint;
                }
                diagnostics.Add(entry);
            }
            var line = new JObject
            {
                ["kind"] = change.Kind,
                ["operationId"] = change.OperationId,
                ["version"] = change.Version,
                ["diagnostics"] = diagnostics,
                ["generationId"] = current.GenerationId,
                ["recordedAt"] = recordedAt,
            };
            if (change.SourceFile is not null)
            {
                line["sourceFile"] = change.SourceFile;
            }
            return line;
        }

        /// <summary>
        /// The durable previous-definition data of the D3 OW-OP-001
        /// "previously at <file>" hint: scans
        /// <c>registry-changes.ndjson</c> and returns, for every
        /// (id, version) whose most recent record is
        /// <see cref="RegistryChangeKinds.Removed"/>, that removed record
        /// with its <c>sourceFile</c> — the answer that survives later
        /// unrelated builds, which a single in-memory diff batch cannot
        /// give. A later <see cref="RegistryChangeKinds.Added"/> — like
        /// any other later record for the same pair — ends the removal.
        /// A missing or unreadable file degrades to an empty list and
        /// every undecodable, wrongly shaped or unknown-kind line is
        /// skipped: the history is diagnostics, so the scan never throws.
        /// </summary>
        public IReadOnlyList<RegistryChange> FindRemovedDefinitions()
        {
            string[] lines;
            try
            {
                var path = ChangesPath;
                if (!File.Exists(path))
                {
                    return Array.Empty<RegistryChange>();
                }
                // The default UTF-8 decode keeps reading: an invalid byte
                // sequence becomes U+FFFD inside one line, which then
                // fails its Canonical JSON parse and is skipped like any
                // other corrupt line.
                lines = File.ReadAllLines(path);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Array.Empty<RegistryChange>();
            }

            // The last decoded record per (id, version): the removed
            // change while the pair's most recent record is removed,
            // null after a later record of any other known kind.
            var lastByPair = new Dictionary<OperationPair, RegistryChange?>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (!TryReadChangeLine(lines[i], out var change))
                {
                    continue;
                }
                var key = new OperationPair(change!.OperationId, change.Version);
                lastByPair[key] = change.Kind == RegistryChangeKinds.Removed ? change : null;
            }
            var removed = new List<RegistryChange>();
            foreach (var pair in lastByPair)
            {
                if (pair.Value is not null)
                {
                    removed.Add(pair.Value);
                }
            }
            removed.Sort(CompareByPair);
            return ReadOnlyCollections.CopyList(removed);
        }

        // One registry-changes.ndjson line → the RegistryChange it
        // carries, or false when the line is not a well-formed v1 change
        // record: unparseable JSON, a non-object, a missing or mistyped
        // member or an unknown kind — every skipped line leaves the scan
        // reading the rest of the file. Diagnostics are not recovered:
        // the records this scan exists for — removed — never carry any.
        private static bool TryReadChangeLine(string line, out RegistryChange? change)
        {
            change = null;
            var parsed = CanonicalJson.Parse(line);
            if (!parsed.Ok || parsed.Value is not JObject entry)
            {
                return false;
            }
            if (entry["kind"] is not JValue { Value: string kind } || !IsKnownKind(kind))
            {
                return false;
            }
            if (entry["operationId"] is not JValue { Value: string id } ||
                string.IsNullOrEmpty(id))
            {
                return false;
            }
            if (entry["version"] is not JValue { Value: long version } ||
                version < 1 || version > int.MaxValue)
            {
                return false;
            }
            var sourceFileToken = entry["sourceFile"];
            string? sourceFile = null;
            if (sourceFileToken is not null)
            {
                if (sourceFileToken is not JValue { Value: string file })
                {
                    return false;
                }
                sourceFile = file;
            }
            change = new RegistryChange(
                kind, id, (int)version, sourceFile, Array.Empty<Diagnostic>());
            return true;
        }

        private static bool IsKnownKind(string kind) =>
            kind == RegistryChangeKinds.Added ||
            kind == RegistryChangeKinds.Removed ||
            kind == RegistryChangeKinds.DescriptorChanged ||
            kind == RegistryChangeKinds.ImplementationChanged ||
            kind == RegistryChangeKinds.Failed;

        // RegistryDiff's emission order: id Ordinal, then version ascending.
        private static int CompareByPair(RegistryChange a, RegistryChange b)
        {
            var byId = string.Compare(a.OperationId, b.OperationId, StringComparison.Ordinal);
            return byId != 0 ? byId : a.Version.CompareTo(b.Version);
        }

        private readonly struct OperationPair : IEquatable<OperationPair>
        {
            private readonly string _id;
            private readonly int _version;

            internal OperationPair(string id, int version)
            {
                _id = id;
                _version = version;
            }

            public bool Equals(OperationPair other) =>
                _version == other._version &&
                string.Equals(_id, other._id, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is OperationPair other && Equals(other);

            public override int GetHashCode() =>
                (_id is null ? 0 : StringComparer.Ordinal.GetHashCode(_id)) * 397 ^ _version;
        }

        // SPEC §F3 item 2: a temp file in the same directory is fsync'd, then
        // atomically moved over the target, so a crash leaves the old or the
        // new content — never a torn file. Same transient-retry policy the
        // Run Store's replaces use.
        private void ReplaceAtomically(string path, byte[] bytes)
        {
            var temp = path + TempSuffix;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    MoveOrReplace(temp, path);
                    return;
                }
                catch (Exception e)
                {
                    if (!IsTransientReplaceFailure(e) || attempt >= MaxReplaceAttempts)
                    {
                        TryDeleteTemp(temp);
                        throw;
                    }
                    System.Threading.Thread.Sleep(ReplaceRetryDelayMilliseconds);
                }
            }
        }

        private static void MoveOrReplace(string temp, string path)
        {
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        // The transient Win32 codes the replace step surfaces while an
        // external reader holds a file (see FileRunStore).
        private static bool IsTransientReplaceFailure(Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException))
            {
                return false;
            }
            var code = e.HResult & 0xFFFF;
            return code == 5 || code == 32 || code == 1175;
        }

        private static bool IsIoFailure(Exception e) =>
            e is IOException || e is UnauthorizedAccessException ||
            e is System.Security.SecurityException || e is NotSupportedException;

        private static void TryDeleteTemp(string temp)
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception e) when (IsIoFailure(e))
            {
            }
        }
    }
}
