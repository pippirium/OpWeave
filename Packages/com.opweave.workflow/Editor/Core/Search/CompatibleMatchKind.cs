#nullable enable

namespace OpWeave.Core.Search
{
    /// <summary>
    /// How a port matched the queried type in a
    /// <see cref="CompatibleOperationMatch"/> — the
    /// <c>match: exact | base | json</c> values of SPEC §C7.
    /// </summary>
    public enum CompatibleMatchKind
    {
        /// <summary>The two Type IDs are identical.</summary>
        Exact,

        /// <summary>The types are assignable through the SPEC §B6 base-type /
        /// generic-argument rules, not identical.</summary>
        Base,

        /// <summary>The pair connects only through the <c>core.Json</c> rule
        /// (SPEC §B6 rule 2) — the target itself or a covariant generic
        /// argument inside it; returned only when the request sets
        /// <c>includeJson</c>.</summary>
        Json,
    }
}
