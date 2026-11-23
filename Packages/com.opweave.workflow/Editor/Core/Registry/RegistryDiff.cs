#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// The pure diff evaluator of SPEC §C3 rule 7: compares the persisted
    /// summary of the previously published snapshot with the summary of the
    /// snapshot just published and produces the
    /// added/removed/descriptorChanged/implementationChanged records of the
    /// build's diff — deterministic in both content and order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keying is the (id, version) pair — the same identity the Registry and
    /// the §C3 rule-4 duplicate rule use; a version bump is a removal of the
    /// old pair plus an addition of the new one, not a change record. When
    /// both fingerprints moved, one <see cref="RegistryChangeKinds.DescriptorChanged"/>
    /// record is emitted — the Descriptor contract changed and the moved
    /// implementationHash is its usual companion — while
    /// <see cref="RegistryChangeKinds.ImplementationChanged"/> covers a build
    /// whose compiled bytes moved under an identical Descriptor (the local,
    /// reversible reading of the two §C3 kinds: exactly one record per
    /// retained (id, version)).
    /// </para>
    /// <para>
    /// <see cref="RegistryChangeKinds.Removed"/> records carry the previous
    /// summary's sourceFile — the previous-definition data SPEC §D3 puts in
    /// the OW-OP-001 "previously at <file>" hint for removed custom
    /// Operations. All produced records carry an empty diagnostics list;
    /// per-candidate rejection records
    /// (<see cref="RegistryChangeKinds.Failed"/>) are the snapshot's own
    /// <see cref="RegistrySnapshot.Failures"/>, appended to the build's
    /// recorded batch by the caller rather than recomputed here.
    /// </para>
    /// <para>
    /// The evaluator only ever sees trusted persisted input — a caller that
    /// cannot read the previous summary must not call it at all (SPEC §C3
    /// rule 7: never guess the diff; never treat every Operation as added).
    /// </para>
    /// </remarks>
    public static class RegistryDiff
    {
        /// <summary>
        /// Computes the diff from <paramref name="previous"/> to
        /// <paramref name="current"/>, ordered by Operation id (Ordinal)
        /// then version ascending.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public static IReadOnlyList<RegistryChange> Evaluate(
            PersistedRegistrySnapshot previous,
            PersistedRegistrySnapshot current)
        {
            if (previous is null)
            {
                throw new ArgumentNullException(nameof(previous));
            }
            if (current is null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            var previousByKey = new Dictionary<OperationKey, PersistedRegistryOperation>(
                previous.Operations.Count);
            for (var i = 0; i < previous.Operations.Count; i++)
            {
                var operation = previous.Operations[i];
                previousByKey[new OperationKey(operation.Id, operation.Version)] = operation;
            }
            var currentByKey = new Dictionary<OperationKey, PersistedRegistryOperation>(
                current.Operations.Count);
            for (var i = 0; i < current.Operations.Count; i++)
            {
                var operation = current.Operations[i];
                currentByKey[new OperationKey(operation.Id, operation.Version)] = operation;
            }

            var changes = new List<RegistryChange>();

            // removed: present in the previous summary, absent now — the
            // record keeps the previous sourceFile for the §D3 hint.
            for (var i = 0; i < previous.Operations.Count; i++)
            {
                var operation = previous.Operations[i];
                if (!currentByKey.ContainsKey(new OperationKey(operation.Id, operation.Version)))
                {
                    changes.Add(new RegistryChange(
                        RegistryChangeKinds.Removed,
                        operation.Id,
                        operation.Version,
                        operation.SourceFile,
                        Array.Empty<Diagnostic>()));
                }
            }

            // added / changed: present now; a retained pair compares its two
            // §C3 rule-6 fingerprints (descriptorChanged wins when both moved).
            for (var i = 0; i < current.Operations.Count; i++)
            {
                var operation = current.Operations[i];
                var key = new OperationKey(operation.Id, operation.Version);
                if (!previousByKey.TryGetValue(key, out var before))
                {
                    changes.Add(new RegistryChange(
                        RegistryChangeKinds.Added,
                        operation.Id,
                        operation.Version,
                        operation.SourceFile,
                        Array.Empty<Diagnostic>()));
                    continue;
                }
                if (!string.Equals(
                        before.DescriptorHash, operation.DescriptorHash, StringComparison.Ordinal))
                {
                    changes.Add(new RegistryChange(
                        RegistryChangeKinds.DescriptorChanged,
                        operation.Id,
                        operation.Version,
                        operation.SourceFile,
                        Array.Empty<Diagnostic>()));
                }
                else if (!string.Equals(
                             before.ImplementationHash, operation.ImplementationHash,
                             StringComparison.Ordinal))
                {
                    changes.Add(new RegistryChange(
                        RegistryChangeKinds.ImplementationChanged,
                        operation.Id,
                        operation.Version,
                        operation.SourceFile,
                        Array.Empty<Diagnostic>()));
                }
            }

            changes.Sort(CompareChanges);
            return ReadOnlyCollections.CopyList(changes);
        }

        // Deterministic emission order: Operation id (Ordinal), then version
        // ascending — one record per (id, version) pair, so no tie remains.
        private static int CompareChanges(RegistryChange a, RegistryChange b)
        {
            var byId = string.Compare(a.OperationId, b.OperationId, StringComparison.Ordinal);
            return byId != 0 ? byId : a.Version.CompareTo(b.Version);
        }

        private readonly struct OperationKey : IEquatable<OperationKey>
        {
            private readonly string _id;
            private readonly int _version;

            internal OperationKey(string id, int version)
            {
                _id = id;
                _version = version;
            }

            public bool Equals(OperationKey other) =>
                _version == other._version &&
                string.Equals(_id, other._id, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is OperationKey other && Equals(other);

            public override int GetHashCode() =>
                (_id is null ? 0 : StringComparer.Ordinal.GetHashCode(_id)) * 397 ^ _version;
        }
    }
}
