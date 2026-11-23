#nullable enable

using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The handle an <see cref="IRunCoordinator"/> entry point returns
    /// (SPEC §E2): the accepted Run's identifier and its state as of the call —
    /// the <c>execute_workflow</c> response's <c>runId</c> and accepted state
    /// (SPEC §H1, §1.6).
    /// </summary>
    public sealed record RunHandle(string RunId, RunState State);
}
