#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using OpWeave.Core.Operations;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §F7 ChangeSet of one Run: every change element the Run produced
    /// — the ones its Operations recorded through <c>ctx.Changes</c> and the ones
    /// the <see cref="AssetChangePostprocessor"/> reported on their behalf — in
    /// arrival order, which is what <see cref="ChangeSetFile"/> writes to
    /// <c>changes.json</c> when the Run ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One element per changed target.</b> An element is keyed by
    /// <c>(step, nodeId, action, kind, path)</c> and — within that key — told apart
    /// by the durable §F5 identifiers: two reports whose <c>guid</c> or
    /// <c>globalId</c> both exist and differ changed <em>different</em> objects that
    /// share a path — the two <c>GameObject</c>s of one scene do exactly that —
    /// so each is listed; a report that could not resolve an
    /// identifier is never a second target, and any identifier only the repeat
    /// carries fills in on the element already listed. The two writers of the same
    /// fact — an Operation that records its asset creation and the import the same
    /// creation reports — are therefore one line, which is the "一覧" (list) §F7
    /// asks review and a manual rollback to work from. The same target touched by
    /// two steps stays two elements, and one target deleted then recreated is two
    /// actions.
    /// </para>
    /// <para>
    /// <b>The asset baseline.</b> The paths the AssetDatabase already knew when
    /// the Run's first step attempt opened are the baseline that separates an
    /// import of an existing asset (<c>modified</c>) from the import of one this
    /// Run created (<c>created</c>) — see <see cref="ReportsAsCreated"/>.
    /// </para>
    /// </remarks>
    internal sealed class RunChangeSet
    {
        private const char KeySeparator = '\u001F';

        private readonly List<ChangeRecord> _records = new List<ChangeRecord>();

        // The listed elements per element key. One key can own several elements:
        // it says which step did which action to which path, and only the durable
        // identifiers say which object at that path the change touched — see
        // TryAdd.
        private readonly Dictionary<string, List<int>> _indexesByElement =
            new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly HashSet<string> _knownAssetPaths;

        /// <summary>
        /// Opens the change list of Run <paramref name="runId"/>;
        /// <paramref name="knownAssetPaths"/> is the asset-path baseline taken
        /// before its first step attempt ran.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/> or <paramref name="knownAssetPaths"/> is null.
        /// </exception>
        internal RunChangeSet(string runId, HashSet<string> knownAssetPaths)
        {
            if (string.IsNullOrEmpty(runId))
            {
                throw new ArgumentException(
                    "A Run change list must carry its Run id.", nameof(runId));
            }
            _knownAssetPaths =
                knownAssetPaths ?? throw new ArgumentNullException(nameof(knownAssetPaths));
            RunId = runId;
        }

        /// <summary>The Run every element of this list belongs to.</summary>
        internal string RunId { get; }

        /// <summary>The listed elements in arrival order (SPEC §F7).</summary>
        internal IReadOnlyList<ChangeRecord> Records => _records;

        /// <summary>
        /// True once the Run ended and the list reached <c>changes.json</c>;
        /// a later report cannot extend a finalized list (§F7: the list is
        /// determined at Run end).
        /// </summary>
        internal bool Finalized { get; private set; }

        /// <summary>
        /// Why the elements this Run already journaled could not be read back when
        /// a reconstruction (a new runner host after a domain reload) opened this
        /// list, null when the list is known whole. While it is set the list is
        /// provably partial, so <see cref="ChangeSetTracker"/> must not write it as
        /// the Run's §F7 change list — a rollback worked from a silently short list
        /// is worse than no list at all.
        /// </summary>
        internal string? IncompleteEvidence { get; set; }

        /// <summary>
        /// Lists <paramref name="record"/> unless the same target of the same step
        /// is already listed or the list was finalized with its Run.
        /// </summary>
        /// <returns>
        /// True when the element was added — the caller may carry it to every
        /// other consumer of the change list; false when it is a repeat or the
        /// list no longer accepts changes.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
        internal bool TryAdd(ChangeRecord record) => TryAdd(record, out _);

        /// <summary>
        /// As <see cref="TryAdd(ChangeRecord)"/>, and reports through
        /// <paramref name="merged"/> the listed element the record deduplicated
        /// into — which the merge may have enriched with a durable identifier
        /// only the repeat carried — or null when the element was listed or
        /// refused. A caller that keeps a second copy of the list (the step
        /// recorder whose elements the attempt's §F2 records journal) syncs
        /// that element, or its copy keeps the identifier-less one.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
        internal bool TryAdd(ChangeRecord record, out ChangeRecord? merged) =>
            TryAdd(record, journaled: false, out merged);

        /// <summary>
        /// Replays one element the Run's journal already lists back into the
        /// set — the durable half of the reconstruction a new runner host
        /// performs after a domain reload (SPEC §F4/§F7).
        /// </summary>
        /// <remarks>
        /// A journaled element is a finished list member: the writer's
        /// deduplication already ran on it, so a missing identifier on the
        /// listed side is final — no report is coming to resolve it — and it
        /// must not absorb a record carrying an identifier of its own the way
        /// <see cref="TryAdd(ChangeRecord)"/> lets a live report in, or the two
        /// objects the journal listed under one path collapse back into one.
        /// The other half of the live rule still holds: a journaled repeat
        /// that resolved nothing the listed element knows is the same fact
        /// once more.
        /// </remarks>
        /// <returns>
        /// True when the element was listed; false when the journal already
        /// carried it (or the list was finalized).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
        internal bool Restore(ChangeRecord record) =>
            TryAdd(record, journaled: true, out _);

        private bool TryAdd(ChangeRecord record, bool journaled, out ChangeRecord? merged)
        {
            merged = null;
            if (record is null)
            {
                throw new ArgumentNullException(nameof(record));
            }
            if (Finalized)
            {
                return false;
            }
            var key = KeyOf(record);
            if (!_indexesByElement.TryGetValue(key, out var indexes))
            {
                _indexesByElement.Add(key, new List<int> { _records.Count });
                _records.Add(record);
                return true;
            }
            for (var i = 0; i < indexes.Count; i++)
            {
                var element = _records[indexes[i]];
                var sameTarget = journaled
                    ? JournaledTargetsAreOne(element.Target, record.Target)
                    : TargetsAreOne(element.Target, record.Target);
                if (sameTarget)
                {
                    MergeIdentifiers(indexes[i], record);
                    merged = _records[indexes[i]];
                    return false;
                }
            }
            // The same step changed another object of the same path — two scene
            // objects of one scene, say. §F7 lists both: the one this record did
            // not match is a target no other element describes.
            indexes.Add(_records.Count);
            _records.Add(record);
            return true;
        }

        // Whether two targets of one element key are one artifact: an identifier
        // either side could not resolve separates nothing (the automatic import
        // report and the Operation that made the change are one fact even when only
        // the Operation knows the guid), but two resolved values that differ name
        // two artifacts.
        private static bool TargetsAreOne(ChangeTarget listed, ChangeTarget repeat) =>
            IdentifiersAgree(listed.Guid, repeat.Guid) &&
            IdentifiersAgree(listed.GlobalId, repeat.GlobalId);

        private static bool IdentifiersAgree(string? left, string? right) =>
            left is null || right is null || string.Equals(left, right, StringComparison.Ordinal);

        // The replay rule of <see cref="Restore"/>: a journaled record that
        // resolved nothing the listed element knows is still the same fact —
        // an identifier-less element is never a second target — but the listed
        // element's missing identifier is the writer's durable word, so it can
        // never absorb a record whose identifier differs from the one the
        // element carries.
        private static bool JournaledTargetsAreOne(ChangeTarget listed, ChangeTarget repeat) =>
            JournaledIdentifiersAgree(listed.Guid, repeat.Guid) &&
            JournaledIdentifiersAgree(listed.GlobalId, repeat.GlobalId);

        private static bool JournaledIdentifiersAgree(string? listed, string? repeat) =>
            repeat is null || string.Equals(listed, repeat, StringComparison.Ordinal);

        // A repeat may know an identifier the first report could not resolve: the
        // import callback can fire before the AssetDatabase answers for a freshly
        // created path, while the Operation that created it records the guid it
        // looked up itself. The two are one fact, so the durable §F5 keys fill in
        // on the element already listed instead of being dropped with the repeat.
        private void MergeIdentifiers(int index, ChangeRecord record)
        {
            var listed = _records[index];
            var kind = listed.Target.Kind;
            var path = listed.Target.Path;
            var guid = listed.Target.Guid ?? record.Target.Guid;
            var globalId = listed.Target.GlobalId ?? record.Target.GlobalId;
            if (ReferenceEquals(guid, listed.Target.Guid) &&
                ReferenceEquals(globalId, listed.Target.GlobalId))
            {
                return;
            }
            _records[index] = listed with
            {
                Target = new ChangeTarget(kind, path, guid, globalId),
            };
        }

        /// <summary>
        /// Whether an import of <paramref name="assetPath"/> is this Run creating
        /// the asset (never seen before) or changing one that already existed.
        /// Reporting an import adds the path, so a later reimport of the same
        /// asset reads as a change — including when an earlier step created it.
        /// </summary>
        internal bool ReportsAsCreated(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }
            // HashSet.Add answers true only for a member it did not hold, which
            // is exactly "this Run has not produced this path before".
            return _knownAssetPaths.Add(assetPath);
        }

        /// <summary>
        /// Notes that <paramref name="assetPath"/> no longer exists, so a later
        /// import of the same path reads as a creation again.
        /// </summary>
        internal void NoteDeleted(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath))
            {
                _knownAssetPaths.Remove(assetPath);
            }
        }

        /// <summary>
        /// Moves the baseline entry for <paramref name="fromPath"/> to
        /// <paramref name="toPath"/>: the asset at the destination is the same
        /// one the Run moved, not one it created.
        /// </summary>
        internal void NoteMoved(string fromPath, string toPath)
        {
            _knownAssetPaths.Remove(fromPath);
            if (!string.IsNullOrEmpty(toPath))
            {
                _knownAssetPaths.Add(toPath);
            }
        }

        /// <summary>
        /// Marks the list as written with its Run — the SPEC §F7 "確定" at Run
        /// end. Idempotent.
        /// </summary>
        internal void MarkFinalized() => Finalized = true;

        // The element key of one §F7 element. 0x1F (unit separator) cannot appear
        // in a path or an id (SPEC §0.4 grammar), so the join is unambiguous. The
        // identifier members are not part of the key itself — they would split an
        // automatic report and the Operation's own record of one change into two
        // elements whenever only one writer resolved the identifier — they decide
        // which element under that key the report belongs to, which is what still
        // lists two objects sharing one path separately. See TargetsAreOne.
        private static string KeyOf(ChangeRecord record) =>
            string.Join(
                KeySeparator,
                record.Step.ToString(CultureInfo.InvariantCulture),
                record.Attempt.ToString(CultureInfo.InvariantCulture),
                record.NodeId,
                record.Action.ToString(),
                record.Target.Kind,
                record.Target.Path);
    }
}
