#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The output of <see cref="RegistryTypeStage.Run"/> — SPEC §C3 stages
    /// 1–5: the compile-consistency verdict, the immutable TypeRegistry
    /// snapshot, the admitted TypeProvider set, the Operation candidates
    /// still in metadata form for stage 6 (T-W2.2-07), and the
    /// per-candidate rejection/exclusion records retained for diagnostics.
    /// </summary>
    /// <remarks>
    /// When <see cref="Stale"/> is set the build stopped at stage 1: the
    /// loaded assemblies are not the product of a successful compile, so no
    /// extension class was instantiated and <see cref="Types"/> is null —
    /// the previously published snapshot simply stays in place marked
    /// stale (SPEC §C3 rule 9; publication is T-W2.2-07).
    /// </remarks>
    internal sealed class RegistryTypeBuildResult
    {
        internal RegistryTypeBuildResult(
            bool compilationConsistent,
            ITypeRegistry? types,
            IReadOnlyList<TypeRegistryFailure> typeFailures,
            IReadOnlyList<TypeProviderCandidate> providers,
            IReadOnlyList<ExtensionCandidate> operations,
            IReadOnlyList<ExtensionCandidateFailure> failures,
            IReadOnlyList<ExtensionCandidateExclusion> exclusions,
            IReadOnlyList<ExtensionPackageState> extensionPackages)
        {
            CompilationConsistent = compilationConsistent;
            Types = types;
            TypeFailures = ReadOnlyCollections.CopyList(typeFailures);
            Providers = ReadOnlyCollections.CopyList(providers);
            Operations = ReadOnlyCollections.CopyList(operations);
            Failures = ReadOnlyCollections.CopyList(failures);
            Exclusions = ReadOnlyCollections.CopyList(exclusions);
            ExtensionPackages = ReadOnlyCollections.CopyList(extensionPackages);
        }

        /// <summary>
        /// Stage 1: the compilation result is consistent and
        /// <c>EditorUtility.scriptCompilationFailed</c> is false.
        /// </summary>
        internal bool CompilationConsistent { get; }

        /// <summary>
        /// True while the loaded assemblies do not reflect a successful
        /// compile — the published snapshot must be marked
        /// <c>stale</c> and no new Run accepted (SPEC §C3 rule 9).
        /// </summary>
        internal bool Stale => !CompilationConsistent;

        /// <summary>
        /// The immutable type snapshot of stage 5; null when
        /// <see cref="Stale"/> — nothing was built.
        /// </summary>
        internal ITypeRegistry? Types { get; }

        /// <summary>
        /// The <see cref="TypeRegistryBuilder"/> rejections of stage 4
        /// (SPEC §C3 step 4); empty when <see cref="Stale"/>.
        /// </summary>
        internal IReadOnlyList<TypeRegistryFailure> TypeFailures { get; }

        /// <summary>
        /// Every instantiated <see cref="TypeProviderCandidate"/> handed to
        /// the stage-5 build — the standard wired providers plus the
        /// admitted discovered providers, needed by the weak-fingerprint
        /// bindings of T-W2.2-07 (<see cref="UnityPlanProvenance"/>).
        /// Empty when <see cref="Stale"/>.
        /// </summary>
        internal IReadOnlyList<TypeProviderCandidate> Providers { get; }

        /// <summary>
        /// The Operation candidates that survived stages 2–3 — type metadata
        /// plus provenance, never constructed — for stage 6 (T-W2.2-07).
        /// Empty when <see cref="Stale"/>.
        /// </summary>
        internal IReadOnlyList<ExtensionCandidate> Operations { get; }

        /// <summary>
        /// One record per candidate rejected before or at instantiation.
        /// Empty when <see cref="Stale"/>.
        /// </summary>
        internal IReadOnlyList<ExtensionCandidateFailure> Failures { get; }

        /// <summary>
        /// Candidates the exclusion predicate rejected before instantiation
        /// (SPEC §C3 rule 5 — policy exclusions, not registration errors).
        /// Empty when <see cref="Stale"/>.
        /// </summary>
        internal IReadOnlyList<ExtensionCandidateExclusion> Exclusions { get; }

        /// <summary>
        /// The extension packages that passed the SPEC §X.3.2 / §B0.2
        /// metadata gate and still contribute admitted candidates — the
        /// inputs of the pre-publication observed-fact gate
        /// (<see cref="RegistryBuildHost"/>). Empty when <see cref="Stale"/>.
        /// </summary>
        internal IReadOnlyList<ExtensionPackageState> ExtensionPackages { get; }

        /// <summary>The stage-1 refusal: nothing scanned, bound or instantiated.</summary>
        internal static RegistryTypeBuildResult StaleResult() =>
            new RegistryTypeBuildResult(
                compilationConsistent: false,
                types: null,
                typeFailures: Array.Empty<TypeRegistryFailure>(),
                providers: Array.Empty<TypeProviderCandidate>(),
                operations: Array.Empty<ExtensionCandidate>(),
                failures: Array.Empty<ExtensionCandidateFailure>(),
                exclusions: Array.Empty<ExtensionCandidateExclusion>(),
                extensionPackages: Array.Empty<ExtensionPackageState>());
    }
}
