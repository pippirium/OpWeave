#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The per-step <see cref="IChangeRecorder"/> the run loop hands to
    /// Operations through <see cref="StepContext.Changes"/> (SPEC §C2 item 6):
    /// stamps every call into the SPEC §F7 element shape
    /// <c>{step, attempt, nodeId, action, target, undoable}</c> so the runner can
    /// place the collected list on the attempt's §F2 records —
    /// <c>StepWaiting</c>, <c>StepFailed</c> and <c>StepCancelled</c> whenever the
    /// list is non-empty, <c>StepSucceeded</c> always.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The list is one attempt's.</b> SPEC §F2 (2026-11-08 attempt-durability
    /// decision) defines <c>changes</c> as the cumulative list of the attempt the
    /// record's envelope names, so the list restarts when the loop opens a new
    /// attempt (<see cref="BeginAttempt"/>): a retried attempt's changes are its
    /// own elements, and the interrupted attempt's durable list stays on the
    /// records it already wrote. A resume of a journaled <c>Waiting</c> is the same
    /// attempt continued, so <see cref="RestoreAttempt"/> seeds the list with what
    /// that attempt already journaled (§F4 item 5).
    /// </para>
    /// </remarks>
    internal sealed class StepChangeRecorder : IChangeRecorder
    {
        private readonly int _step;
        private readonly string _nodeId;
        private readonly List<ChangeRecord> _records = new List<ChangeRecord>();

        /// <summary>Binds the recorder to <paramref name="stepIndex"/>/<paramref name="nodeId"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="nodeId"/> is null.</exception>
        internal StepChangeRecorder(int stepIndex, string nodeId)
        {
            _step = stepIndex;
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        }

        /// <summary>The recorded changes in call order (SPEC §F7).</summary>
        internal IReadOnlyList<ChangeRecord> Records => _records;

        /// <summary>
        /// The attempt whose cumulative list <see cref="Records"/> is — the number
        /// every element carries and the §F2 envelope of the record that journals it
        /// names. A recorder starts at the Run's first attempt.
        /// </summary>
        internal int Attempt { get; private set; } = 1;

        /// <summary>
        /// Opens <paramref name="attempt"/>: its cumulative list is empty, because
        /// a new attempt's changes are its own elements (§F2).
        /// </summary>
        internal void BeginAttempt(int attempt)
        {
            _records.Clear();
            Attempt = attempt;
        }

        /// <summary>
        /// Continues <paramref name="attempt"/> from the elements it already
        /// journaled — SPEC §F4 item 5: resuming a recorded <c>Waiting</c> restarts
        /// no attempt, so the elements its Execute or an earlier Poll persisted must
        /// still be on the <c>StepSucceeded</c>, <c>StepFailed</c> or
        /// <c>StepCancelled</c> that ends it. The restored elements keep the attempt
        /// the journal recorded for them.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="changes"/> is null.
        /// </exception>
        internal void RestoreAttempt(int attempt, IReadOnlyList<ChangeRecord> changes)
        {
            if (changes is null)
            {
                throw new ArgumentNullException(nameof(changes));
            }
            _records.Clear();
            _records.AddRange(changes);
            Attempt = attempt;
        }

        /// <summary>
        /// Carries the identifier enrichment the Run's change list folded into
        /// its listed element (<paramref name="listed"/>) onto this attempt's
        /// copy of the same element: when a later report resolves a durable
        /// identifier the listed report could not, the Run list fills it in on
        /// the element already listed instead of listing a second one, and the
        /// durable list this recorder journals must fold it in the same way —
        /// otherwise a reconstruction reads the identifier-less element where
        /// the Run list already knows the target and replays it as another
        /// target's record.
        /// </summary>
        /// <remarks>
        /// The element replaced is the recorded one whose element key the
        /// listed element shares and whose identifiers it agrees with — the
        /// same one-element-per-target identity the Run list applies, which
        /// matches at most one element: the listed element's own copy. An
        /// element this attempt never recorded cannot be synced — nothing is
        /// added.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="listed"/> is null.
        /// </exception>
        internal void MergeIdentifiers(ChangeRecord listed)
        {
            if (listed is null)
            {
                throw new ArgumentNullException(nameof(listed));
            }
            for (var i = 0; i < _records.Count; i++)
            {
                var element = _records[i];
                if (element.Step == listed.Step &&
                    element.Attempt == listed.Attempt &&
                    element.Action == listed.Action &&
                    string.Equals(
                        element.NodeId, listed.NodeId, StringComparison.Ordinal) &&
                    string.Equals(
                        element.Target.Kind, listed.Target.Kind,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        element.Target.Path, listed.Target.Path,
                        StringComparison.Ordinal) &&
                    IdentifiersAgree(element.Target.Guid, listed.Target.Guid) &&
                    IdentifiersAgree(
                        element.Target.GlobalId, listed.Target.GlobalId))
                {
                    _records[i] = listed;
                    return;
                }
            }
        }

        // The same one-element-per-target identity the Run's change list
        // applies: an identifier either side could not resolve separates
        // nothing, and two resolved values that differ name two artifacts.
        private static bool IdentifiersAgree(string? left, string? right) =>
            left is null || right is null ||
            string.Equals(left, right, StringComparison.Ordinal);

        public void Created(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Created, target, undoable);

        public void Modified(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Modified, target, undoable);

        public void Deleted(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Deleted, target, undoable);

        public void Moved(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Moved, target, undoable);

        public void Saved(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Saved, target, undoable);

        private void Record(ChangeAction action, ChangeTarget target, bool undoable) =>
            _records.Add(new ChangeRecord(
                _step, Attempt, _nodeId, action, target, undoable));
    }
}
