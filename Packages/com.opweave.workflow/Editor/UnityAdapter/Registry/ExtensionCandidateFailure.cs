#nullable enable

using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// A candidate rejected during SPEC §C3 stages 2–5 before or at
    /// instantiation: §G0 provenance unresolvable (OW-OP-017, emitted by
    /// <see cref="EditorAssemblyResolver"/>), an unbindable Provider ID, a
    /// failed eligibility check or a construction failure. One record per
    /// rejected candidate; unrelated candidates are never affected (SPEC
    /// §C3 steps 4–5).
    /// </summary>
    /// <remarks>
    /// The carried <see cref="Diagnostics"/> keep whatever codes their
    /// emitter already publishes (OW-OP-017 from the resolver,
    /// OW-OP-010/OW-OP-012 from the manifest reader); diagnostics produced
    /// by this stage itself stay internal (empty code, fixed English) — the
    /// published OW-OP-013..019 mapping onto the RegistrySnapshot is
    /// T-W2.12-03's contract. For an Operation rejected on its
    /// attribute-declared identity alone (reserved namespace OW-OP-007,
    /// duplicate OW-OP-004 — SPEC §C3 rules 3–4, before any constructor
    /// runs), <see cref="DeclaredOperationId"/>/
    /// <see cref="DeclaredOperationVersion"/> carry that declared identity
    /// so the snapshot's per-Operation failure records can name it
    /// (SPEC §C3 error paragraph).
    /// </remarks>
    internal sealed record ExtensionCandidateFailure(
        ExtensionCandidateKind Kind,
        string TypeFullName,
        string? AssemblyName,
        IReadOnlyList<Diagnostic> Diagnostics,
        string? DeclaredOperationId = null,
        int? DeclaredOperationVersion = null,
        string? SourceFile = null);
}
