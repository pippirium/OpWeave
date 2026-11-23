#nullable enable

namespace OpWeave.Core.Model
{
    /// <summary>
    /// Per-node execution policy overrides (SPEC §A1): an optional step timeout in
    /// seconds and an optional maximum attempt count.
    /// </summary>
    public sealed record NodePolicy(int? TimeoutSeconds, int? MaxAttempts);
}
