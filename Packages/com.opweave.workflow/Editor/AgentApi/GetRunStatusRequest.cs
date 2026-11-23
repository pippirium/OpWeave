#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The <c>get_run_status</c> arguments of SPEC §H1: the Run's
    /// <paramref name="RunId"/>, an optional <paramref name="WaitSeconds"/>
    /// long-poll duration (SPEC §H2 item 3 — at most 20 seconds; the wait
    /// sleeps on the calling worker thread, never the Editor main thread)
    /// and an optional <paramref name="Verbosity"/> — <c>"compact"</c>
    /// reduces each step to <c>{node, state, attempt}</c> (SPEC §H3
    /// item 2); omitted or <c>"full"</c> carries the per-step detail.
    /// A malformed runId is OW-API-001, an out-of-range waitSeconds or an
    /// unknown verbosity is the same (SPEC §J), and a well-formed but
    /// unknown runId is OW-RUN-010.
    /// </summary>
    public sealed record GetRunStatusRequest(
        string? RunId, int WaitSeconds = 0, string? Verbosity = null);
}
