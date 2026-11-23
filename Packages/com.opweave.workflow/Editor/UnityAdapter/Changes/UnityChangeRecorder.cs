#nullable enable

using System;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The <see cref="IChangeRecorder"/> the Unity StepContext exposes as
    /// <c>ctx.Changes</c> (SPEC §0.6 SG-06, §C2 item 6): every explicit record an
    /// Operation makes reaches both consumers of the SPEC §F7 change list — the
    /// Core step recorder, which journals it on the step's
    /// <c>step_succeeded.changes</c> member (§F2), and the Run's
    /// <see cref="RunChangeSet"/>, which <see cref="ChangeSetFile"/> writes when
    /// the Run ends.
    /// </summary>
    /// <remarks>
    /// The Run list decides what is new (<see cref="RunChangeSet.TryAdd"/>): an
    /// element the automatic asset tracking already listed for the same step is
    /// not journaled a second time, so the journal and the Run's change list
    /// carry the same element once. The recorder is also the target the
    /// <see cref="AssetChangePostprocessor"/> reports to while its step's attempt
    /// is the Run's latest one — see <see cref="ChangeSetTracker"/>.
    /// </remarks>
    internal sealed class UnityChangeRecorder : IChangeRecorder
    {
        private readonly IChangeRecorder _stepRecorder;
        private readonly RunChangeSet _changes;
        private readonly int _attempt;

        /// <summary>
        /// Wraps the run loop's per-step recorder
        /// (<paramref name="stepRecorder"/>) for the step of
        /// <paramref name="stepIndex"/>/<paramref name="nodeId"/>, listing every
        /// change on <paramref name="changes"/> as well. <paramref name="attempt"/>
        /// is the §F2 envelope attempt of this step attempt — the
        /// <c>attempt</c> member every §F7 element it produces carries.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stepRecorder"/>, <paramref name="changes"/> or
        /// <paramref name="nodeId"/> is null.
        /// </exception>
        internal UnityChangeRecorder(
            IChangeRecorder stepRecorder,
            RunChangeSet changes,
            int stepIndex,
            string nodeId,
            int attempt)
        {
            _stepRecorder =
                stepRecorder ?? throw new ArgumentNullException(nameof(stepRecorder));
            _changes = changes ?? throw new ArgumentNullException(nameof(changes));
            if (string.IsNullOrEmpty(nodeId))
            {
                throw new ArgumentException(
                    "A step change recorder must carry its node id.", nameof(nodeId));
            }
            Step = stepIndex;
            NodeId = nodeId;
            _attempt = attempt;
        }

        /// <summary>The Plan step index this recorder stamps into its elements.</summary>
        internal int Step { get; }

        /// <summary>The Plan node id this recorder stamps into its elements.</summary>
        internal string NodeId { get; }

        /// <summary>The Run list every element of this step is listed on.</summary>
        internal RunChangeSet Changes => _changes;

        /// <inheritdoc/>
        public void Created(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Created, target, undoable);

        /// <inheritdoc/>
        public void Modified(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Modified, target, undoable);

        /// <inheritdoc/>
        public void Deleted(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Deleted, target, undoable);

        /// <inheritdoc/>
        public void Moved(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Moved, target, undoable);

        /// <inheritdoc/>
        public void Saved(ChangeTarget target, bool undoable) =>
            Record(ChangeAction.Saved, target, undoable);

        // One §F7 element, from either writer: listed on the Run change list
        // first, and only when that is genuinely new carried to the step
        // recorder that journals it. A repeat the Run list deduplicated is
        // carried only as the identifier enrichment the merge folded into the
        // listed element — the attempt's durable list replaces its copy of
        // that element with the enriched one rather than journaling the
        // identifier-less record a reconstruction would replay as another
        // target's.
        internal void Record(ChangeAction action, ChangeTarget target, bool undoable)
        {
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            if (!_changes.TryAdd(
                    new ChangeRecord(Step, _attempt, NodeId, action, target, undoable),
                    out var merged))
            {
                if (merged is not null && _stepRecorder is StepChangeRecorder steps)
                {
                    steps.MergeIdentifiers(merged);
                }
                return;
            }
            switch (action)
            {
                case ChangeAction.Created:
                    _stepRecorder.Created(target, undoable);
                    break;
                case ChangeAction.Modified:
                    _stepRecorder.Modified(target, undoable);
                    break;
                case ChangeAction.Deleted:
                    _stepRecorder.Deleted(target, undoable);
                    break;
                case ChangeAction.Moved:
                    _stepRecorder.Moved(target, undoable);
                    break;
                default:
                    _stepRecorder.Saved(target, undoable);
                    break;
            }
        }
    }
}
