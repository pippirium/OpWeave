#nullable enable

using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Registry;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The output of <see cref="RegistryBuildHost.Rebuild"/> — SPEC §C3
    /// stages 1–8: the stage 1–5 type-side result, the published
    /// <see cref="RegistrySnapshot"/> of stages 6–7, the stage-6
    /// per-candidate rejections and the settings-load diagnostics the build
    /// observed (the §H4.1 standard-only signal).
    /// </summary>
    /// <remarks>
    /// When <see cref="Stale"/> is set the build stopped at stage 1 and no
    /// stage-6 work ran: <see cref="Snapshot"/> is null, the previous
    /// published snapshot stays in place marked stale (SPEC §C3 rule 9) and
    /// the stage-6 lists are empty. The stage 1–5 members are delegated to
    /// <see cref="TypeStage"/> so existing stage 1–5 consumers keep working.
    /// </remarks>
    internal sealed class RegistryBuildResult
    {
        internal RegistryBuildResult(
            RegistryTypeBuildResult typeStage,
            RegistrySnapshot? snapshot,
            IReadOnlyList<ExtensionCandidateFailure> operationFailures,
            IReadOnlyList<ExtensionCandidateFailure> extensionFailures,
            IReadOnlyList<Diagnostic> settingsDiagnostics,
            ITypeRegistry? types,
            IReadOnlyList<TypeProviderCandidate> providers,
            IReadOnlyList<TypeRegistryFailure> typeFailures)
        {
            TypeStage = typeStage;
            Snapshot = snapshot;
            OperationFailures = ReadOnlyCollections.CopyList(operationFailures);
            ExtensionFailures = ReadOnlyCollections.CopyList(extensionFailures);
            SettingsDiagnostics = ReadOnlyCollections.CopyList(settingsDiagnostics);
            Types = types;
            Providers = ReadOnlyCollections.CopyList(providers);
            TypeFailures = ReadOnlyCollections.CopyList(typeFailures);
        }

        /// <summary>The stage 1–5 result (compile check, candidates, type build).</summary>
        internal RegistryTypeBuildResult TypeStage { get; }

        /// <summary>
        /// The snapshot stages 6–7 produced and stage 8 published; null when
        /// <see cref="Stale"/> — nothing was built or published.
        /// </summary>
        internal RegistrySnapshot? Snapshot { get; }

        /// <summary>
        /// The SPEC §C3 rule-7 persistence/diff outcome of this build —
        /// what was recorded to <c>registry-changes.ndjson</c> or why the
        /// previous snapshot was <c>"unavailable"</c> — or null when the
        /// build was stale or ran without a diff recorder. The
        /// previous-definition data the D3 OW-OP-001 hint reads is the
        /// recorder's persisted <c>PreviousDefinitions</c>, not only
        /// this batch (SPEC §D3, T-W2.2-04).
        /// </summary>
        /// <remarks>
        /// <see cref="RegistryBuildHost.Rebuild"/> retains this result
        /// before the recorder's <c>RegistryChanged</c> dispatch — an
        /// event-driven refresh must already see the new build — and
        /// <see cref="AttachDiff"/> fills this in once the record pass
        /// returns; it stays null during the dispatch itself.
        /// </remarks>
        internal RegistryDiffResult? Diff { get; private set; }

        /// <summary>
        /// Attaches the §C3 rule-7 outcome — the one post-construction
        /// step of a build, run by <see cref="RegistryBuildHost.Rebuild"/>
        /// after the result was already retained for the recorder's
        /// <c>RegistryChanged</c> dispatch.
        /// </summary>
        internal void AttachDiff(RegistryDiffResult? diff) => Diff = diff;

        /// <summary>
        /// One record per Operation candidate stage 6 rejected
        /// (OW-OP-008/OW-OP-017); empty when <see cref="Stale"/>.
        /// </summary>
        internal IReadOnlyList<ExtensionCandidateFailure> OperationFailures { get; }

        /// <summary>
        /// One record per candidate of an extension package the
        /// observed-fact gate rejected after construction (SPEC §X.3.2):
        /// the package contributes no Operation and no TypeProvider to the
        /// published Registry. Empty when <see cref="Stale"/>.
        /// </summary>
        internal IReadOnlyList<ExtensionCandidateFailure> ExtensionFailures { get; }

        /// <summary>
        /// The diagnostics the settings load emitted for this build —
        /// OW-CFG-001/002 errors force the §H4.1 standard-only build, while
        /// OW-CFG-003/004/005 warnings ride along.
        /// </summary>
        internal IReadOnlyList<Diagnostic> SettingsDiagnostics { get; }

        /// <inheritdoc cref="RegistryTypeBuildResult.CompilationConsistent"/>
        internal bool CompilationConsistent => TypeStage.CompilationConsistent;

        /// <inheritdoc cref="RegistryTypeBuildResult.Stale"/>
        internal bool Stale => TypeStage.Stale;

        /// <summary>
        /// The type snapshot the published Registry was built against —
        /// <see cref="RegistryTypeBuildResult.Types"/> minus the providers
        /// of any package the observed-fact gate rejected (SPEC §X.3.2).
        /// </summary>
        internal ITypeRegistry? Types { get; }

        /// <summary>
        /// The type-build rejections the published snapshot carries: the
        /// effective provider set's failures plus the rejected packages'
        /// own §C3 step-4 rejections (SPEC §C3 error paragraph — a dropped
        /// offer keeps its Type ID and 出自アセンブリ diagnostic).
        /// </summary>
        internal IReadOnlyList<TypeRegistryFailure> TypeFailures { get; }

        /// <summary>
        /// The provider candidates the published type snapshot was built
        /// from — <see cref="RegistryTypeBuildResult.Providers"/> minus the
        /// providers of packages rejected at the observed-fact gate.
        /// </summary>
        internal IReadOnlyList<TypeProviderCandidate> Providers { get; }

        /// <inheritdoc cref="RegistryTypeBuildResult.Operations"/>
        internal IReadOnlyList<ExtensionCandidate> Operations => TypeStage.Operations;

        /// <inheritdoc cref="RegistryTypeBuildResult.Failures"/>
        internal IReadOnlyList<ExtensionCandidateFailure> Failures => TypeStage.Failures;

        /// <inheritdoc cref="RegistryTypeBuildResult.Exclusions"/>
        internal IReadOnlyList<ExtensionCandidateExclusion> Exclusions => TypeStage.Exclusions;
    }
}
