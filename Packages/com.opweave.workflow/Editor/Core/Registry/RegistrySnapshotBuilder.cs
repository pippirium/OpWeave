#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// Builds an immutable <see cref="RegistrySnapshot"/> from permitted
    /// <see cref="OperationCandidate"/>s and the finished type snapshot (SPEC
    /// §C3 steps 6–7). The Editor-side pipeline supplies every candidate as
    /// metadata; this builder applies the Core registration rules, computes
    /// the fingerprints it owns and fixes the <c>registryHash</c> input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candidates are processed in deterministic order — Descriptor id
    /// (Ordinal), version ascending, then input position — so the same inputs
    /// always produce the same registration outcome and failure list. Only
    /// defective candidates and duplicate (id, version) sets are excluded;
    /// unrelated Operations are never affected (SPEC §C3 rules 3–4):
    /// </para>
    /// <list type="bullet">
    /// <item>A candidate whose attribute-declared (id, version) disagrees
    /// with its Descriptor identity is rejected with OW-OP-003 (SPEC §C2
    /// item 2, §C3 rule 3). Declared identity is metadata-only here — the
    /// Editor-side stage already used it for the pre-instantiation
    /// decisions.</item>
    /// <item>A Custom candidate using a reserved namespace
    /// (<c>core.*</c>/<c>unity.*</c>/<c>opweave.*</c>) is rejected with
    /// OW-OP-007 (SPEC §C3 rule 3).</item>
    /// <item>A <see cref="ExecutionMode.Waitable"/> candidate whose metadata
    /// reports no Poll implementation is rejected with OW-OP-003 (SPEC §C3
    /// rule 3; the flag is computed by the Editor-side stage).</item>
    /// <item><see cref="OperationDescriptorValidator"/> runs against the type
    /// snapshot; port types invalidated by the §C3 step-4 type build are
    /// absent from it, so dependent Operations fail as unresolvable with
    /// OW-OP-014 (SPEC §C3 step 6).</item>
    /// <item>Every candidate sharing an (id, version) pair is rejected with
    /// OW-OP-004 (SPEC §C3 rule 4). The count covers all supplied candidates —
    /// the same conservative both-invalidated rule SPEC §B0.1 gives type
    /// collisions — while the same id at a different version coexists.</item>
    /// </list>
    /// <para>
    /// Each rejected candidate yields exactly one
    /// <see cref="RegistryChangeKinds.Failed"/> record carrying all of its
    /// diagnostics in the fixed check order above.
    /// </para>
    /// </remarks>
    public static class RegistrySnapshotBuilder
    {
        /// <summary>
        /// The generation ID prefix; every build mints a fresh
        /// <c>"reg_" + ULID</c> generation ID, unique per build and managed
        /// separately from <see cref="RegistrySnapshot.RegistryHash"/> (SPEC
        /// §C3 rule 6).
        /// </summary>
        public const string GenerationIdPrefix = "reg_";

        /// <summary>
        /// Builds the snapshot; never fails wholesale — the result always
        /// carries a usable (possibly empty) operation list plus one
        /// <see cref="RegistryChangeKinds.Failed"/> record per rejected
        /// candidate.
        /// </summary>
        /// <param name="candidates">
        /// The permitted Operation candidates from the Editor-side pipeline.
        /// </param>
        /// <param name="types">The finished type snapshot (SPEC §C3 step 5).</param>
        /// <param name="typeFailures">
        /// The §C3 step-4 type-build rejections, stored on the snapshot for
        /// the registration layer; may be null or empty.
        /// </param>
        /// <param name="candidateFailures">
        /// The pre-builder candidate rejections of the same build — the
        /// stage 1–5 extension rejections and stage-6 Operation rejections
        /// of the Editor-side pipeline, as
        /// <see cref="RegistryCandidateFailure"/> records carrying their
        /// published diagnostics. The builder appends the
        /// <paramref name="typeFailures"/> mapped through
        /// <see cref="RegistrationDiagnostics"/> and its own per-Operation
        /// rejections, so <see cref="RegistrySnapshot.CandidateFailures"/>
        /// lists every rejected candidate of the build (SPEC §C3 error
        /// paragraph). May be null or empty.
        /// </param>
        /// <param name="preInstantiationFailures">
        /// <see cref="RegistryChangeKinds.Failed"/> records for Operation
        /// candidates the Editor-side pipeline rejected from type metadata
        /// before any construction — reserved-namespace and duplicate
        /// declared identities (SPEC §C3 rules 3–4). They join
        /// <see cref="RegistrySnapshot.Failures"/> ahead of this builder's
        /// own rejections, in the order supplied. May be null or empty.
        /// </param>
        /// <param name="exclusions">
        /// The deliberate policy exclusions of the same build — candidates
        /// the DiscoveryMode / <c>disabledOperations</c> predicate rejected
        /// before instantiation (SPEC §C3 rule 5). They land on
        /// <see cref="RegistrySnapshot.Exclusions"/> as supplied — Registry
        /// state, never failure records. May be null or empty.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="candidates"/> or <paramref name="types"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="candidates"/> yields a null candidate.
        /// </exception>
        public static RegistrySnapshot Build(
            IEnumerable<OperationCandidate> candidates,
            ITypeRegistry types,
            IReadOnlyList<TypeRegistryFailure>? typeFailures = null,
            IReadOnlyList<RegistryCandidateFailure>? candidateFailures = null,
            IReadOnlyList<RegistryChange>? preInstantiationFailures = null,
            IReadOnlyList<RegistryCandidateExclusion>? exclusions = null)
        {
            if (candidates is null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }

            var ordered = OrderCandidates(candidates);

            // ── Duplicate counts: every supplied candidate counts toward its
            //    (id, version), including ones the individual checks reject —
            //    the conservative both-invalidated rule of SPEC §B0.1 applied
            //    to §C3 rule 4 ──
            var duplicateCounts = new Dictionary<(string Id, int Version), int>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var descriptor = ordered[i].Descriptor;
                var key = (descriptor.Id, descriptor.Version);
                duplicateCounts.TryGetValue(key, out var count);
                duplicateCounts[key] = count + 1;
            }

            var registered = new List<RegisteredOperation>(ordered.Count);
            var failures = new List<RegistryChange>(
                preInstantiationFailures is null ? 0 : preInstantiationFailures.Count + ordered.Count);
            if (preInstantiationFailures is not null)
            {
                // Candidates refused on type metadata alone (SPEC §C3
                // stages 2–3) precede this builder's rejections.
                failures.AddRange(preInstantiationFailures);
            }
            var rejected = new List<RegistryCandidateFailure>(
                candidateFailures is null ? 0 : candidateFailures.Count + ordered.Count);
            if (candidateFailures is not null)
            {
                rejected.AddRange(candidateFailures);
            }
            for (var i = 0; i < ordered.Count; i++)
            {
                var candidate = ordered[i];
                var descriptor = candidate.Descriptor;
                var diagnostics = new List<Diagnostic>();

                // The publishable form of the Descriptor-supplied ID: a
                // malformed Operation ID is never published verbatim — its
                // unvalidated text may carry credentials (SPEC §C3 error
                // paragraph).
                var safeId = PublishedId.OperationId(descriptor.Id);

                // SPEC §C2 item 2 / §C3 rule 3: the attribute-declared
                // (id, version) must equal the Descriptor identity the
                // constructed Operation reports — a disagreement rejects
                // the candidate. Missing declared identity (metadata-only
                // code paths) skips this check.
                if (candidate.DeclaredId is not null &&
                    candidate.DeclaredVersion is not null &&
                    (!string.Equals(candidate.DeclaredId, descriptor.Id, StringComparison.Ordinal) ||
                     candidate.DeclaredVersion.Value != descriptor.Version))
                {
                    var safeDeclaredId = PublishedId.OperationId(candidate.DeclaredId);
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_003,
                        $"Operation '{safeId}' version {descriptor.Version} disagrees with its [OpWeaveOperation] declared identity '{safeDeclaredId}' version {candidate.DeclaredVersion.Value}."));
                }

                // §C3 rule 3: reserved namespaces are standard-only.
                if (candidate.Origin != OperationOrigin.Standard &&
                    IdGrammar.HasReservedOperationPrefix(descriptor.Id))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_007,
                        $"Operation '{safeId}' uses a namespace reserved for standard Operations."));
                }

                // §C3 rule 3: a Waitable Operation needs a Poll implementation.
                if (descriptor.ExecutionMode == ExecutionMode.Waitable &&
                    !candidate.HasPollImplementation)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_003,
                        $"Operation '{safeId}' is waitable but its implementation does not provide Poll."));
                }

                // §C3 rule 3: the remaining descriptor checks; a port type
                // invalidated by the step-4 type build is absent from the
                // snapshot and fails here as unresolvable (OW-OP-003).
                var descriptorDiagnostics =
                    OperationDescriptorValidator.Validate(descriptor, types);
                for (var j = 0; j < descriptorDiagnostics.Count; j++)
                {
                    diagnostics.Add(descriptorDiagnostics[j]);
                }

                // §C3 rule 4: a shared (id, version) excludes every candidate
                // for it; different versions of the same id coexist.
                var duplicates = duplicateCounts[(descriptor.Id, descriptor.Version)];
                if (duplicates > 1)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_004,
                        $"Operation '{safeId}' version {descriptor.Version} is declared by {duplicates} candidates; none were registered."));
                }

                if (diagnostics.Count != 0)
                {
                    failures.Add(new RegistryChange(
                        RegistryChangeKinds.Failed,
                        safeId,
                        descriptor.Version,
                        candidate.SourceFile,
                        diagnostics));
                    rejected.Add(new RegistryCandidateFailure(
                        RegistryCandidateKind.Operation,
                        safeId,
                        ProviderId: null,
                        candidate.AssemblyName,
                        candidate.SourceFile,
                        diagnostics));
                    continue;
                }

                registered.Add(new RegisteredOperation(
                    descriptor,
                    candidate.Origin,
                    candidate.AssemblyName,
                    candidate.SourceFile,
                    DescriptorHash.Compute(descriptor),
                    candidate.ImplementationHash)
                {
                    PackageId = candidate.PackageId,
                    ResolvedCommit = candidate.ResolvedCommit,
                    Implementation = candidate.Implementation,
                });
            }

            // The §C3 step-4 type-build rejections join the candidate list
            // through the OW-OP-013..019 mapping (SPEC §J.1, §C3 error
            // paragraph), after the pipeline-supplied entries and before
            // this builder's own rejections — deterministic order.
            if (typeFailures is not null)
            {
                for (var i = 0; i < typeFailures.Count; i++)
                {
                    rejected.Add(RegistrationDiagnostics.ForCandidate(typeFailures[i]));
                }
            }

            return new RegistrySnapshot(
                GenerationIdPrefix + Identifiers.NewUlid(),
                ComputeRegistryHash(registered),
                ReadOnlyCollections.CopyList(registered),
                types,
                ReadOnlyCollections.CopyList(failures),
                ReadOnlyCollections.CopyList(
                    typeFailures ??
                        (IReadOnlyList<TypeRegistryFailure>)Array.Empty<TypeRegistryFailure>()),
                ReadOnlyCollections.CopyList(rejected),
                ReadOnlyCollections.CopyList(
                    exclusions ??
                        (IReadOnlyList<RegistryCandidateExclusion>)Array.Empty<RegistryCandidateExclusion>()));
        }

        // The fixed registryHash input of SPEC §C3 rule 6:
        // {"operations":[{id,version,descriptorHash,implementationHash}]} over
        // every registered Operation, sorted by id (Ordinal) then version —
        // nothing else enters the canonicalized object.
        private static string ComputeRegistryHash(List<RegisteredOperation> registered) =>
            ComputeRegistryHash(registered.Select(
                operation => (operation.Descriptor.Id, operation.Descriptor.Version,
                    operation.DescriptorHash, operation.ImplementationHash)));

        /// <summary>
        /// The SPEC §C3 rule-6 registryHash over (id, version,
        /// descriptorHash, implementationHash) tuples — the single
        /// implementation the write side fixes and the persisted-summary
        /// reader recomputes, so a <c>registry.json</c> is never trusted on
        /// a hash no OpWeave writer could have emitted for its operations.
        /// </summary>
        internal static string ComputeRegistryHash(
            IEnumerable<(string Id, int Version, string DescriptorHash, string ImplementationHash)>
                operations)
        {
            var sorted =
                new List<(string Id, int Version, string DescriptorHash, string ImplementationHash)>(
                    operations);
            sorted.Sort(CompareHashInput);
            var entries = new JArray();
            for (var i = 0; i < sorted.Count; i++)
            {
                var operation = sorted[i];
                entries.Add(new JObject
                {
                    ["id"] = operation.Id,
                    ["version"] = operation.Version,
                    ["descriptorHash"] = operation.DescriptorHash,
                    ["implementationHash"] = operation.ImplementationHash,
                });
            }
            return CanonicalJson.Sha256(new JObject { ["operations"] = entries });
        }

        private static int CompareHashInput(
            (string Id, int Version, string DescriptorHash, string ImplementationHash) a,
            (string Id, int Version, string DescriptorHash, string ImplementationHash) b)
        {
            var byId = string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            return byId != 0 ? byId : a.Version.CompareTo(b.Version);
        }

        // Deterministic processing order: Descriptor id (Ordinal), version
        // ascending, then the candidate's input position (List.Sort is
        // unstable, so the original index breaks ties).
        private static List<OperationCandidate> OrderCandidates(
            IEnumerable<OperationCandidate> candidates)
        {
            var indexed = new List<KeyValuePair<OperationCandidate, int>>();
            var index = 0;
            foreach (var candidate in candidates)
            {
                if (candidate is null)
                {
                    throw new ArgumentException(
                        "Operation candidates must not contain null.", nameof(candidates));
                }
                indexed.Add(new KeyValuePair<OperationCandidate, int>(candidate, index));
                index++;
            }
            indexed.Sort((a, b) =>
            {
                var byId = string.Compare(
                    a.Key.Descriptor.Id, b.Key.Descriptor.Id, StringComparison.Ordinal);
                if (byId != 0)
                {
                    return byId;
                }
                var byVersion = a.Key.Descriptor.Version.CompareTo(b.Key.Descriptor.Version);
                return byVersion != 0 ? byVersion : a.Value.CompareTo(b.Value);
            });
            var result = new List<OperationCandidate>(indexed.Count);
            foreach (var pair in indexed)
            {
                result.Add(pair.Key);
            }
            return result;
        }
    }
}
