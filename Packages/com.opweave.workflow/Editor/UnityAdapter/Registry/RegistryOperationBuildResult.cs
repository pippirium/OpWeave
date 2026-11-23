#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Registry;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The output of <see cref="RegistryOperationStage.Run"/> — SPEC §C3
    /// stage 6: the immutable <see cref="OperationCandidate"/> set handed to
    /// the stage-7 <see cref="RegistrySnapshotBuilder"/>, one
    /// <see cref="ExtensionCandidateFailure"/> record per candidate rejected
    /// at instantiation, Descriptor access or implementationHash
    /// (OW-OP-008/OW-OP-017), and every Descriptor the stage actually read —
    /// the observed-fact input of the SPEC §X.3.2 extension gate.
    /// </summary>
    internal sealed class RegistryOperationBuildResult
    {
        internal RegistryOperationBuildResult(
            IReadOnlyList<OperationCandidate> candidates,
            IReadOnlyList<ExtensionCandidateFailure> failures,
            IReadOnlyList<ObservedOperation> observed)
        {
            Candidates = ReadOnlyCollections.CopyList(candidates);
            Failures = ReadOnlyCollections.CopyList(failures);
            Observed = ReadOnlyCollections.CopyList(observed);
        }

        /// <summary>The instantiated, fingerprinted candidates in stage-3 order.</summary>
        internal IReadOnlyList<OperationCandidate> Candidates { get; }

        /// <summary>One record per candidate stage 6 rejected; unrelated candidates unaffected.</summary>
        internal IReadOnlyList<ExtensionCandidateFailure> Failures { get; }

        /// <summary>
        /// Every Descriptor stage 6 read, including the candidates a later
        /// step refused (OW-OP-017): the declared <c>operations[]</c> list is
        /// compared against this observed set, so a fingerprint failure
        /// rejects only its own Operation.
        /// </summary>
        internal IReadOnlyList<ObservedOperation> Observed { get; }
    }
}
