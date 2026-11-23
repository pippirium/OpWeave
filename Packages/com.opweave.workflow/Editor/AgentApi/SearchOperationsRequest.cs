#nullable enable

using System.Collections.Generic;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>search_operations</c> command (SPEC §H1,
    /// §C6): the free-text <paramref name="Query"/> plus the conjunctive
    /// §C6 item-1 filters — <paramref name="AcceptsType"/>,
    /// <paramref name="ProducesType"/>, <paramref name="AllowedEffects"/>,
    /// <paramref name="Tags"/>, <paramref name="Category"/> and
    /// <paramref name="Origin"/> (<c>"standard"</c>/<c>"custom"</c>,
    /// anything else is OW-API-001) — the response language
    /// <paramref name="Lang"/>, the item cap <paramref name="Limit"/>
    /// (§C6 item 4: default 10, maximum 50, clamped never an error) and
    /// the opaque page <paramref name="Cursor"/> (SPEC §H3 item 3 — an
    /// invalid one is OW-API-002).
    /// </summary>
    public sealed record SearchOperationsRequest(
        string? Query = null,
        string? AcceptsType = null,
        string? ProducesType = null,
        IReadOnlyList<string>? AllowedEffects = null,
        IReadOnlyList<string>? Tags = null,
        string? Category = null,
        string? Origin = null,
        string? Lang = null,
        int? Limit = null,
        string? Cursor = null);
}
