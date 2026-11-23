#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// SPEC §C3 stage 6 — the Operation side of the atomic Registry rebuild
    /// (T-W2.2-07): each candidate the stages 1–5 pipeline admitted is
    /// instantiated (<see cref="OperationEligibility"/>, OW-OP-008), its
    /// "Waitable implements Poll" flag is computed from the class metadata
    /// (<see cref="PollImplementation"/>), its compiled-bytes
    /// implementationHash is taken from the already resolved §G0 provenance
    /// (<see cref="WeakFingerprintProvider"/>, OW-OP-017) and the resulting
    /// immutable <see cref="OperationCandidate"/> set feeds the Core
    /// <see cref="RegistrySnapshotBuilder"/> of stage 7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rejections are per-candidate — a failed constructor, an unreadable
    /// Descriptor getter or an unhashable defining assembly rejects only that
    /// Operation and never affects unrelated candidates (SPEC §C3 step 6:
    /// "個別登録エラー"). They are retained as
    /// <see cref="ExtensionCandidateFailure"/> records carrying the emitted
    /// OW-OP-008/OW-OP-017 diagnostics; candidates the builder itself rejects
    /// (OW-OP-003/004/007 — including the §C2 item-2 declared-vs-Descriptor
    /// identity disagreement, checked from the declared pair stage 3 carried
    /// on the candidate) land inside the snapshot's
    /// <see cref="RegistrySnapshot.Failures"/> instead.
    /// </para>
    /// <para>
    /// A local, reversible reading of SPEC §C3 step 6: a Descriptor getter
    /// that throws or returns null is an OW-OP-008 instantiation failure —
    /// the Operation cannot produce the contract registration needs, so it is
    /// rejected like a throwing constructor rather than reaching the
    /// Descriptor checks with a non-value.
    /// </para>
    /// </remarks>
    internal static class RegistryOperationStage
    {
        /// <summary>The injected seams of one stage-6 run.</summary>
        internal sealed record Seams(
            Func<Type, Result<IOperation>> Instantiate,
            Func<Type, bool> HasPollImplementation,
            Func<EditorAssemblyProvenance, Result<DefiningAssemblyFingerprint>> ImplementationHash);

        /// <summary>
        /// The production seams of SPEC §C3 step 6:
        /// <see cref="OperationEligibility.Instantiate"/> (OW-OP-008),
        /// <see cref="PollImplementation.HasRealImplementation"/> and
        /// <see cref="WeakFingerprintProvider.ForProvenance"/> (OW-OP-017).
        /// </summary>
        internal static Seams Production() =>
            new Seams(
                OperationEligibility.Instantiate,
                PollImplementation.HasRealImplementation,
                WeakFingerprintProvider.ForProvenance);

        /// <summary>
        /// Runs stage 6 over the admitted candidates in their stage-3 order
        /// (assembly name, then type full name — deterministic); never fails
        /// wholesale — per-candidate defects land in
        /// <see cref="RegistryOperationBuildResult.Failures"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="operations"/>, <paramref name="seams"/> or one of its
        /// delegates is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="operations"/> yields a null candidate.
        /// </exception>
        internal static RegistryOperationBuildResult Run(
            IReadOnlyList<ExtensionCandidate> operations, Seams seams)
        {
            if (operations is null)
            {
                throw new ArgumentNullException(nameof(operations));
            }
            if (seams is null)
            {
                throw new ArgumentNullException(nameof(seams));
            }
            var instantiate = seams.Instantiate ??
                throw new ArgumentNullException(nameof(seams.Instantiate));
            var hasPoll = seams.HasPollImplementation ??
                throw new ArgumentNullException(nameof(seams.HasPollImplementation));
            var implementationHash = seams.ImplementationHash ??
                throw new ArgumentNullException(nameof(seams.ImplementationHash));

            var candidates = new List<OperationCandidate>(operations.Count);
            var failures = new List<ExtensionCandidateFailure>();
            var observed = new List<ObservedOperation>(operations.Count);

            // The implementationHash input is a pure function of the
            // candidate's provenance identity within one stage-6 run — the
            // DLL bytes behind a path cannot change mid-run in any way a
            // caller could rely on (a recompile lands through a domain
            // reload, which ends the whole build). The key carries the
            // assembly name as well as the compiled path because a seam
            // may distinguish provenances that share one path (the
            // synthetic provenances of the §M.5 gate tests do exactly
            // that). Candidates sharing a defining assembly — the common
            // case — would otherwise re-read and re-hash the same bytes
            // once per candidate.
            var fingerprintByProvenance =
                new Dictionary<string, Result<DefiningAssemblyFingerprint>>(StringComparer.Ordinal);

            for (var i = 0; i < operations.Count; i++)
            {
                var candidate = operations[i];
                if (candidate is null)
                {
                    throw new ArgumentException(
                        "Operation candidates must not contain null.", nameof(operations));
                }

                // SPEC §C3 step 6: instantiate the permitted Operation —
                // class-condition and construction failures are OW-OP-008.
                var instance = instantiate(candidate.Type);
                if (!instance.Ok || instance.Value is null)
                {
                    failures.Add(Failure(candidate, instance.Diagnostics));
                    continue;
                }

                // The Descriptor is the candidate's registration contract:
                // a getter that throws or answers null leaves the Operation
                // unusable — an OW-OP-008 instantiation failure (see remarks).
                var descriptor = DescriptorOf(candidate, instance.Value!, failures);
                if (descriptor is null)
                {
                    continue;
                }

                // The Descriptor was actually read, so it counts as observed
                // for the SPEC §X.3.2 declared-vs-observed check even when a
                // later step refuses the candidate — an OW-OP-017 fingerprint
                // failure rejects only this Operation, never the whole
                // package (SPEC §M.5: unrelated Operations stay).
                observed.Add(new ObservedOperation(
                    descriptor, candidate.PackageId, candidate.AssemblyName));

                // SPEC §C3 rule 6: implementationHash is the SHA-256 of the
                // defining assembly's compiled bytes; an unresolvable or
                // unreadable one refuses the candidate (OW-OP-017).
                var fingerprintKey =
                    (candidate.Provenance.AssemblyName ?? "") + "\u001f" +
                    (candidate.Provenance.CompiledAssemblyPath ?? "");
                if (!fingerprintByProvenance.TryGetValue(fingerprintKey, out var hash))
                {
                    hash = implementationHash(candidate.Provenance);
                    fingerprintByProvenance[fingerprintKey] = hash;
                }
                if (!hash.Ok || hash.Value is null)
                {
                    failures.Add(Failure(candidate, hash.Diagnostics));
                    continue;
                }

                candidates.Add(new OperationCandidate(
                    descriptor,
                    candidate.Origin,
                    candidate.AssemblyName,
                    candidate.SourceFile,
                    hash.Value!.DefiningAssemblySha256,
                    hasPoll(candidate.Type),
                    candidate.PackageId,
                    candidate.Provenance.Package?.ResolvedRevision,
                    instance.Value!,
                    candidate.DeclaredId,
                    candidate.DeclaredVersion));
            }
            return new RegistryOperationBuildResult(candidates, failures, observed);
        }

        // Reads the Descriptor contract inside the stage's error boundary:
        // a throwing getter or a null value is OW-OP-008, and the exception's
        // type name is the only detail reported (its message may carry
        // credentials or source text, SPEC §C3).
        private static OperationDescriptor? DescriptorOf(
            ExtensionCandidate candidate,
            IOperation instance,
            List<ExtensionCandidateFailure> failures)
        {
            var name = candidate.Type.FullName ?? candidate.Type.Name;
            OperationDescriptor? descriptor;
            try
            {
                descriptor = instance.Descriptor;
            }
            catch (Exception exception)
            {
                failures.Add(Failure(candidate, ErrorCodes.Create(
                    ErrorCodes.OW_OP_008,
                    $"Operation class '{name}' could not produce a Descriptor: {exception.GetType().Name}.")));
                return null;
            }
            if (descriptor is null)
            {
                failures.Add(Failure(candidate, ErrorCodes.Create(
                    ErrorCodes.OW_OP_008,
                    $"Operation class '{name}' produced no Descriptor.")));
                return null;
            }
            return descriptor;
        }

        private static ExtensionCandidateFailure Failure(
            ExtensionCandidate candidate, Diagnostic diagnostic) =>
            Failure(candidate, new[] { diagnostic });

        private static ExtensionCandidateFailure Failure(
            ExtensionCandidate candidate, IReadOnlyList<Diagnostic> diagnostics) =>
            new ExtensionCandidateFailure(
                ExtensionCandidateKind.Operation,
                candidate.Type.FullName ?? candidate.Type.Name,
                candidate.AssemblyName,
                diagnostics);
    }
}
