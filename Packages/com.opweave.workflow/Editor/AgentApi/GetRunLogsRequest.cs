#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The <c>get_run_logs</c> arguments of SPEC §H1: the Run's
    /// <paramref name="RunId"/>, an optional minimum severity
    /// <paramref name="Level"/> (<c>"debug"</c>, <c>"info"</c>,
    /// <c>"warning"</c> or <c>"error"</c> — omitted means the SPEC §F6
    /// default of info and above; <c>"debug"</c> entries appear only
    /// while the <c>debugLogging</c> setting permits, SPEC §F6 SG-08),
    /// an optional step index <paramref name="Step"/> and the page
    /// <paramref name="Cursor"/> of SPEC §H3 item 3. A malformed runId,
    /// an unknown level or a negative step is OW-API-001, an invalid
    /// cursor OW-API-002 and a well-formed but unknown runId OW-RUN-010
    /// (SPEC §J).
    /// </summary>
    public sealed record GetRunLogsRequest(
        string? RunId, string? Level = null, int? Step = null, string? Cursor = null);
}
