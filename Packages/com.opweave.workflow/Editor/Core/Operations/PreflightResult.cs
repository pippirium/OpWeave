#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// What an <see cref="IOperation.Preflight"/> reports without applying any
    /// change (SPEC §C2 item 7): precondition <see cref="Diagnostics"/>, the
    /// <see cref="PlannedChange"/> list a Dry Run returns (SPEC §D8 item 2),
    /// and the <see cref="Snapshot"/> journaled with StepStarted and later
    /// handed to <see cref="IOperation.Reconcile"/> (SPEC §F5 rule 1).
    /// </summary>
    public sealed record PreflightResult(
        IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyList<PlannedChange> PlannedChanges,
        JsonValue Snapshot)
    {
        private readonly IReadOnlyList<Diagnostic> _diagnostics =
            CheckEntries(Diagnostics, nameof(Diagnostics));
        private readonly IReadOnlyList<PlannedChange> _plannedChanges =
            CheckEntries(PlannedChanges, nameof(PlannedChanges));
        private readonly JsonValue _snapshot =
            Snapshot ?? throw new ArgumentNullException(nameof(Snapshot));

        /// <summary>Precondition findings; an Error-severity entry fails the step before any change (SPEC §E4 item 3).</summary>
        public IReadOnlyList<Diagnostic> Diagnostics
        {
            get => _diagnostics;
            init => _diagnostics = CheckEntries(value, nameof(Diagnostics));
        }

        /// <summary>The predicted changes a Dry Run reports (SPEC §D8 item 2).</summary>
        public IReadOnlyList<PlannedChange> PlannedChanges
        {
            get => _plannedChanges;
            init => _plannedChanges = CheckEntries(value, nameof(PlannedChanges));
        }

        /// <summary>The pre-execution evidence persisted before any side effect (SPEC §F5 rule 1).</summary>
        public JsonValue Snapshot
        {
            get => _snapshot;
            init => _snapshot =
                value ?? throw new ArgumentNullException(nameof(Snapshot));
        }

        private static IReadOnlyList<T> CheckEntries<T>(IReadOnlyList<T> entries, string name)
        {
            var copy = ReadOnlyCollections.CopyList(entries);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "PreflightResult entries must not be null.", name);
                }
            }
            return copy;
        }
    }
}
