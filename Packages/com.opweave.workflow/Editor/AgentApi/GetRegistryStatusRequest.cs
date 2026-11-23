#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>get_registry_status</c> command (SPEC §H1,
    /// §C3): the optional <paramref name="Since"/> — a Registry
    /// generation ID the diff is reported against. Omitted, the command
    /// returns the last recorded §C3 rule-7 pass; a generation naming
    /// the current snapshot answers an empty change list; a generation
    /// naming the persisted previous summary answers that build's
    /// recorded diff; any other value is reported
    /// <c>previousSnapshot: "unavailable"</c> — the durable store keeps
    /// only the last two summaries, so no older diff can be proven and
    /// none is guessed (SPEC §C3 rule 7).
    /// </summary>
    public sealed record GetRegistryStatusRequest(string? Since = null, string? Cursor = null);
}
