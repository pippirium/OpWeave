#nullable enable

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The acceptance options of SPEC §E2, exactly as specified.
    /// <see cref="ExpectedPlanHash"/> binds the accepted Run to the Plan the
    /// caller validated — a mismatch rejects with OW-RUN-003 (SPEC §D7 item 7).
    /// <see cref="Queue"/> allows the Run to enter the waiting queue (max 5)
    /// while another mutating Run is active; without it the acceptance is
    /// rejected with OW-RUN-002 (SPEC §E2 item 2).
    /// </summary>
    public sealed record AcceptOptions(string? ExpectedPlanHash, bool Queue);
}
