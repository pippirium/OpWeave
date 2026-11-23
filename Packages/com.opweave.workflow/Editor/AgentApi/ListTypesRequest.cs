#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>list_types</c> command (SPEC §H1, §B2):
    /// the optional <paramref name="Prefix"/> every returned Type ID
    /// must start with (Ordinal, so <c>"unity."</c> scopes the listing
    /// to the Unity namespace) and the opaque page
    /// <paramref name="Cursor"/> (an invalid one is OW-API-002).
    /// </summary>
    public sealed record ListTypesRequest(string? Prefix = null, string? Cursor = null);
}
