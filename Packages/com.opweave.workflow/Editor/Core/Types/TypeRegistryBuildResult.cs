#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The outcome of a <see cref="TypeRegistryBuilder.Build"/> call: the
    /// immutable <see cref="Registry"/> snapshot over the accepted definitions
    /// (possibly empty, never null) and the structured
    /// <see cref="Failures"/> for every rejected candidate.
    /// </summary>
    /// <remarks>
    /// A build never fails wholesale — the SPEC §C3 rule is that only defective
    /// candidates and their dependents are excluded, so the result is not a
    /// <see cref="Result{T}"/>.
    /// </remarks>
    public sealed record TypeRegistryBuildResult(
        ITypeRegistry Registry,
        IReadOnlyList<TypeRegistryFailure> Failures)
    {
        /// <summary>The built snapshot; never null.</summary>
        public ITypeRegistry Registry { get; init; } =
            Registry ?? throw new System.ArgumentNullException(nameof(Registry));

        /// <summary>One record per rejected candidate or provider; never null.</summary>
        public IReadOnlyList<TypeRegistryFailure> Failures { get; init; } =
            ReadOnlyCollections.CopyList(Failures);
    }
}
