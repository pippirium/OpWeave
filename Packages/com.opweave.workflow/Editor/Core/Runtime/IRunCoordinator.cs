#nullable enable

using OpWeave.Core.Compiler;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The Run acceptance and decision service of SPEC §E2: accepts Plans,
    /// keeps exactly one mutating Run per project and carries the human-only
    /// decision entry points.
    /// </summary>
    public interface IRunCoordinator
    {
        /// <summary>
        /// Stores the Plan in the Run Store and returns the accepted Run
        /// (SPEC §E2 item 1): <see cref="RunState.AwaitingApproval"/> when the
        /// Plan carries approvals, otherwise <see cref="RunState.Queued"/>.
        /// </summary>
        Result<RunHandle> Accept(CompiledPlan plan, ActorKind actor, AcceptOptions options);

        /// <summary>Human-only approval (SPEC §E2, §H4).</summary>
        Result<RunHandle> Approve(string runId, string approver);

        /// <summary>Human-only rejection (SPEC §E2, §H4).</summary>
        Result<RunHandle> Reject(string runId, string approver, string reason);

        /// <summary>Records a cancel request (SPEC §E2, §E6 item 2).</summary>
        Result<RunHandle> Cancel(string runId, string reason);

        /// <summary>Human-only NeedsReview resolution (SPEC §E2, §E1).</summary>
        Result<RunHandle> ResolveReview(string runId, ReviewDecision decision);

        /// <summary>
        /// The recorded state of a Run (SPEC §E2): the
        /// <see cref="IRunStore.Load"/> journal reconstruction — the single
        /// source of truth of SPEC §F1 — or null when no readable Run exists
        /// under <paramref name="runId"/>.
        /// </summary>
        RunSnapshot? Get(string runId);
    }
}
