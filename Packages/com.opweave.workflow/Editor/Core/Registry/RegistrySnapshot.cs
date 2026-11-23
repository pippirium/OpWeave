#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;
using OpWeave.Core.Search;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// The immutable Registry published atomically per build (SPEC §C3 steps
    /// 7–8): the accepted Operations, the type snapshot they were validated
    /// against, the per-candidate registration failures and the type-build
    /// failures that preceded them. Nothing about a snapshot is mutable; a
    /// rebuild creates a new instance with a fresh generation ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GenerationId"/> identifies the build itself — unique every
    /// time <see cref="RegistrySnapshotBuilder"/> runs — while
    /// <see cref="RegistryHash"/> identifies the registered content: the same
    /// Operations always produce the same hash regardless of generation ID,
    /// source files, origins, assembly names, timestamps or diagnostics (SPEC
    /// §C3 rule 6). <see cref="RegistryHash"/> is audit/diff only and never a
    /// planHash input (SPEC §D0.1).
    /// </para>
    /// <para>
    /// <see cref="Operations"/> is kept in the canonical §C3 rule-6 order —
    /// id Ordinal, then version ascending — and contains no (id, version)
    /// pair twice. Dependent TypeProviders and Codecs are reachable through
    /// <see cref="Types"/>; per-Operation package information travels on
    /// <see cref="RegisteredOperation.PackageId"/> /
    /// <see cref="RegisteredOperation.ResolvedCommit"/>; failed candidates are
    /// listed in <see cref="Failures"/> as
    /// <see cref="RegistryChangeKinds.Failed"/> records so
    /// <c>get_registry_status</c>, the Registry tab and the registry.json
    /// diff can report each rejection and its reasons (SPEC §C3 rules 3–7).
    /// </para>
    /// </remarks>
    public sealed record RegistrySnapshot(
        string GenerationId,
        string RegistryHash,
        IReadOnlyList<RegisteredOperation> Operations,
        ITypeRegistry Types,
        IReadOnlyList<RegistryChange> Failures,
        IReadOnlyList<TypeRegistryFailure> TypeFailures,
        IReadOnlyList<RegistryCandidateFailure>? CandidateFailures = null,
        IReadOnlyList<RegistryCandidateExclusion>? Exclusions = null)
        : IOperationSource
    {
        /// <summary>The unique-per-build generation identifier ("reg_" + ULID).</summary>
        public string GenerationId { get; init; } =
            CheckNonEmpty(GenerationId, nameof(GenerationId));

        /// <summary>The fixed-input content hash of SPEC §C3 rule 6.</summary>
        public string RegistryHash { get; init; } =
            CheckNonEmpty(RegistryHash, nameof(RegistryHash));

        /// <summary>
        /// Every registered Operation in canonical order (id Ordinal, then
        /// version ascending); no (id, version) pair appears twice.
        /// </summary>
        public IReadOnlyList<RegisteredOperation> Operations { get; init; } =
            CheckOperations(Operations);

        /// <summary>
        /// The immutable type snapshot (SPEC §B2) the Operations were
        /// validated and hash-checked against; dependent TypeProviders and
        /// Codecs are reachable through its definitions (SPEC §C3).
        /// </summary>
        public ITypeRegistry Types { get; init; } =
            Types ?? throw new ArgumentNullException(nameof(Types));

        /// <summary>
        /// One <see cref="RegistryChangeKinds.Failed"/> record per rejected
        /// candidate, carrying its registration diagnostics.
        /// </summary>
        public IReadOnlyList<RegistryChange> Failures { get; init; } =
            CheckFailures(Failures);

        /// <summary>
        /// The <see cref="TypeRegistryBuilder"/> rejections that preceded the
        /// Operation stage (SPEC §C3 step 4), surfaced so the registration
        /// layer can attach OW-OP-013..019 diagnostics.
        /// </summary>
        public IReadOnlyList<TypeRegistryFailure> TypeFailures { get; init; } =
            CheckTypeFailures(TypeFailures);

        /// <summary>
        /// Every rejected registration candidate of this build — the safe
        /// OW-OP-013..019 diagnostic data SPEC §C3's error paragraph makes
        /// visible through Console, <c>get_registry_status</c> and the
        /// Registry tab: the stage 1–5 extension rejections (OW-OP-010/012/
        /// 017/018/019, reserved-namespace and duplicate-ID verdicts), the
        /// step-4 type-build rejections (OW-OP-007/013/014/015/016/019), the
        /// stage-6 Operation rejections (OW-OP-008/017) and the builder's
        /// own Operation rejections (OW-OP-003/004/007/010 — the same
        /// rejections <see cref="Failures"/> carries as
        /// <see cref="RegistryChangeKinds.Failed"/> records, in
        /// candidate form).
        /// </summary>
        public IReadOnlyList<RegistryCandidateFailure> CandidateFailures { get; init; } =
            CheckCandidateFailures(CandidateFailures);

        /// <summary>
        /// Every deliberately excluded extension candidate of this build —
        /// the DiscoveryMode / <c>disabledOperations</c> policy exclusions
        /// decided before any candidate constructor ran (SPEC §C3 rule 5).
        /// They are Registry state, never registration errors: an excluded
        /// candidate appears only here, so <c>get_registry_status</c>, the
        /// Registry tab and Console can report what the permission settings
        /// kept out without presenting it as a failure.
        /// </summary>
        public IReadOnlyList<RegistryCandidateExclusion> Exclusions { get; init; } =
            CheckExclusions(Exclusions);

        // The §C5 search index is derived state of the published snapshot,
        // bound to this instance rather than to any shared cache: a newly
        // published snapshot is a new instance, so its index can never retain
        // Operations the current Registry no longer contains (SPEC §C3 rule
        // 8 — the switch's consumers use only the post-switch snapshot). It
        // is built on first access and re-checked against the Operations
        // reference so a `with`-copy over different members can never answer
        // from another snapshot's index.
        private volatile OperationSearchIndex? _searchIndex;
        private volatile IReadOnlyList<RegisteredOperation>? _searchIndexSource;

        /// <summary>
        /// The SPEC §C5 search index of this snapshot's Operations, built
        /// once from <see cref="Operations"/> on first access. Custom
        /// Operations are indexed the moment their snapshot is published
        /// because they are ordinary snapshot members (SPEC §C5 責務).
        /// </summary>
        public OperationSearchIndex SearchIndex
        {
            get
            {
                var operations = Operations;
                var index = _searchIndex;
                if (index is null || !ReferenceEquals(_searchIndexSource, operations))
                {
                    index = OperationSearchIndex.Build(this);
                    _searchIndexSource = operations;
                    _searchIndex = index;
                }
                return index;
            }
        }

        /// <summary>
        /// The Operation registered for <paramref name="id"/> at exactly
        /// <paramref name="version"/>, or null when none is.
        /// </summary>
        public RegisteredOperation? Find(string id, int version)
        {
            var operations = Operations;
            for (var i = 0; i < operations.Count; i++)
            {
                var descriptor = operations[i].Descriptor;
                if (descriptor.Version == version &&
                    string.Equals(descriptor.Id, id, StringComparison.Ordinal))
                {
                    return operations[i];
                }
            }
            return null;
        }

        /// <summary>
        /// The newest registered version of <paramref name="id"/> (the
        /// describe_operation default when no version is given, SPEC §C8), or
        /// null when none is registered.
        /// </summary>
        public RegisteredOperation? FindLatest(string id)
        {
            RegisteredOperation? best = null;
            var operations = Operations;
            for (var i = 0; i < operations.Count; i++)
            {
                if (string.Equals(operations[i].Descriptor.Id, id, StringComparison.Ordinal) &&
                    (best is null || operations[i].Descriptor.Version > best.Descriptor.Version))
                {
                    best = operations[i];
                }
            }
            return best;
        }

        OperationDescriptor? IOperationSource.Find(string id, int version) =>
            Find(id, version)?.Descriptor;

        private static string CheckNonEmpty(string value, string name) =>
            string.IsNullOrEmpty(value)
                ? throw new ArgumentException(
                    "Snapshot identifiers must not be null or empty.", name)
                : value;

        private static IReadOnlyList<RegisteredOperation> CheckOperations(
            IReadOnlyList<RegisteredOperation> operations)
        {
            var copy = ReadOnlyCollections.CopyList(operations);
            var seen = new HashSet<(string Id, int Version)>();
            for (var i = 0; i < copy.Count; i++)
            {
                var operation = copy[i];
                if (operation is null)
                {
                    throw new ArgumentException(
                        "Operation entries must not be null.", nameof(operations));
                }
                var key = (operation.Descriptor.Id, operation.Descriptor.Version);
                if (!seen.Add(key))
                {
                    throw new ArgumentException(
                        $"A snapshot cannot contain '{key.Id}' version {key.Version} twice.",
                        nameof(operations));
                }
            }
            return copy;
        }

        private static IReadOnlyList<RegistryChange> CheckFailures(
            IReadOnlyList<RegistryChange> failures)
        {
            var copy = ReadOnlyCollections.CopyList(failures);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Failure entries must not be null.", nameof(failures));
                }
                if (copy[i].Kind != RegistryChangeKinds.Failed)
                {
                    throw new ArgumentException(
                        "Snapshot failures are registration rejections and always have kind 'failed'.",
                        nameof(failures));
                }
            }
            return copy;
        }

        private static IReadOnlyList<TypeRegistryFailure> CheckTypeFailures(
            IReadOnlyList<TypeRegistryFailure> failures)
        {
            var copy = ReadOnlyCollections.CopyList(failures);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Type failure entries must not be null.", nameof(failures));
                }
            }
            return copy;
        }

        private static IReadOnlyList<RegistryCandidateFailure> CheckCandidateFailures(
            IReadOnlyList<RegistryCandidateFailure>? failures)
        {
            if (failures is null)
            {
                return Array.Empty<RegistryCandidateFailure>();
            }
            var copy = ReadOnlyCollections.CopyList(failures);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Candidate failure entries must not be null.", nameof(failures));
                }
            }
            return copy;
        }

        private static IReadOnlyList<RegistryCandidateExclusion> CheckExclusions(
            IReadOnlyList<RegistryCandidateExclusion>? exclusions)
        {
            if (exclusions is null)
            {
                return Array.Empty<RegistryCandidateExclusion>();
            }
            var copy = ReadOnlyCollections.CopyList(exclusions);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Exclusion entries must not be null.", nameof(exclusions));
                }
            }
            return copy;
        }
    }
}
