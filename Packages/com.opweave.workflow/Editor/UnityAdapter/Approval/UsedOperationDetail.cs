#nullable enable

using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One custom or external Operation a Run's Plan uses, with its source —
    /// the whole-Plan half of SPEC §H4 item 3's approval detail (T-W3.4-02):
    /// the approved Run executes every step, not only the nodes whose effects
    /// asked, so the human deciding sees each piece of non-standard code the
    /// Plan runs and where it came from.
    /// </summary>
    /// <remarks>
    /// One entry per Plan step, in Plan order; the same Operation used by two
    /// steps appears at each use site. A Standard Operation is the trusted
    /// set and is never listed. <see cref="Origin"/> is null when the current
    /// Registry no longer resolves the Operation — the entry stays rather
    /// than silently absent (the §E2 item 5 invalidation then ends the Run
    /// anyway). No field can ever carry a credential-bearing Git URL: SPEC
    /// §G8.1 rejects it with OW-OP-006 before the Plan exists.
    /// </remarks>
    public sealed class UsedOperationDetail
    {
        internal UsedOperationDetail(
            string nodeId,
            string operationId,
            int version,
            OperationOrigin? origin,
            string? assemblyName,
            string? sourceFile,
            string? packageId,
            string? resolvedCommit)
        {
            NodeId = nodeId;
            OperationId = operationId;
            Version = version;
            Origin = origin;
            AssemblyName = assemblyName;
            SourceFile = sourceFile;
            PackageId = packageId;
            ResolvedCommit = resolvedCommit;
        }

        /// <summary>The node whose step uses the Operation.</summary>
        public string NodeId { get; }

        /// <summary>The Operation the node's step runs (SPEC §D7).</summary>
        public string OperationId { get; }

        /// <summary>The Operation version the node's step runs.</summary>
        public int Version { get; }

        /// <summary>
        /// The Operation's registration origin (SPEC §C3) — Custom for a
        /// project or external (UPM package) Operation. Null when the
        /// current Registry no longer resolves the Operation.
        /// </summary>
        public OperationOrigin? Origin { get; }

        /// <summary>
        /// The simple name of the assembly the Operation is defined in — the
        /// source of a custom or external Operation (SPEC §H4 item 3). Null
        /// when the Registry does not resolve the Operation.
        /// </summary>
        public string? AssemblyName { get; }

        /// <summary>
        /// The project-relative source file kept for provenance display
        /// (SPEC §C2 item 2), or null.
        /// </summary>
        public string? SourceFile { get; }

        /// <summary>
        /// The UPM Package ID of the package defining the Operation
        /// (SPEC §C3, §X.3), or null for a project assembly.
        /// </summary>
        public string? PackageId { get; }

        /// <summary>
        /// The full resolved commit the defining package was installed at
        /// (SPEC §C3, §X.3), or null when the package manager records none.
        /// </summary>
        public string? ResolvedCommit { get; }
    }
}
