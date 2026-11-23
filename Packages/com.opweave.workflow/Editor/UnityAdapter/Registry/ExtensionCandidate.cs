#nullable enable

using System;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One extension candidate surviving SPEC §C3 stages 2–3: a candidate class
    /// discovered as <em>type metadata</em> (never constructed at this point),
    /// its §G0 provenance and the registration identity stage 3 could already
    /// bind without running the class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProviderId"/> is the bound Registry Provider ID for
    /// TypeProviders — the manifest-declared <c>typeProviders[].id</c> for an
    /// accepted extension package, the provider class's CLR
    /// <see cref="Type.FullName"/> for project (unpackaged) and standard-package
    /// classes, which have no manifest to declare one. It is null for
    /// Operations: their identity is the attribute-declared
    /// (<see cref="DeclaredId"/>, <see cref="DeclaredVersion"/>) pair — type
    /// metadata read without construction (SPEC §C2 item 2, §C3 step 2), which
    /// the §C3 rule-5 exclusion predicate and the stage-3 reserved-namespace /
    /// duplicate decisions consume; stage 6 still verifies it against the
    /// constructed Descriptor.
    /// </para>
    /// <para>
    /// <see cref="SourceFile"/> is the project-relative form of the attribute's
    /// <c>[CallerFilePath]</c> (SPEC §C2 item 3, §T.4 L-10); display aid only.
    /// </para>
    /// </remarks>
    internal sealed record ExtensionCandidate(
        ExtensionCandidateKind Kind,
        Type Type,
        EditorAssemblyProvenance Provenance,
        string? SourceFile,
        string? ProviderId,
        string? DeclaredId = null,
        int? DeclaredVersion = null)
    {
        /// <summary>The defining assembly's simple name (e.g. <c>OpWeave.Custom</c>).</summary>
        internal string AssemblyName => Provenance.AssemblyName;

        /// <summary>
        /// The UPM package ID containing the defining assembly, or null for a
        /// project (unpackaged) assembly — the slot §H4.1 spells
        /// <c>"@project"</c> in whitelist entries.
        /// </summary>
        internal string? PackageId => Provenance.PackageId;

        /// <summary>Standard iff the assembly lives in <c>com.opweave.workflow</c> (SPEC §C3 origin).</summary>
        internal Core.OperationOrigin Origin => Provenance.Origin;
    }
}
