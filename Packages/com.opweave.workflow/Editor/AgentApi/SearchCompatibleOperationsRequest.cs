#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>search_compatible_operations</c> command
    /// (SPEC §H1, §C7): exactly one query form — a Type ID
    /// (<paramref name="Type"/>) or an Operation ID plus port name
    /// (<paramref name="Operation"/>/<paramref name="Port"/>) — the
    /// required <paramref name="Direction"/>
    /// (<c>"downstream"</c>/<c>"upstream"</c>, anything else is
    /// OW-API-001) and <paramref name="IncludeJson"/>, the §C7 flag that
    /// admits pairs matching only through <c>core.Json</c>.
    /// </summary>
    public sealed record SearchCompatibleOperationsRequest(
        string? Type = null,
        string? Operation = null,
        string? Port = null,
        string? Direction = null,
        bool IncludeJson = false,
        string? Cursor = null);
}
