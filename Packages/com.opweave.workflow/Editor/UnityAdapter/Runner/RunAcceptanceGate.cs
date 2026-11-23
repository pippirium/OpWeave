#nullable enable

using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Runtime;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One admission check the runner host applies before a new Run is
    /// accepted (the SG-18 run-acceptance gate of SPEC §0.6): gates run in
    /// registration order, and the first refusal stops the accept before any
    /// side effect — no Run directory, no journal, no lock probe. Later gates
    /// (the stale-Registry refusal of T-W2.2-07, then Plan-permission checks)
    /// plug into the same interface; the SG-18 order is settings error →
    /// stale Registry → Plan permission.
    /// </summary>
    internal interface IRunAcceptanceGate
    {
        /// <summary>
        /// Judges whether <paramref name="request"/> may be accepted.
        /// <see cref="Result{T}.Ok"/> admits it (warnings may ride on the
        /// result); a failure refuses the accept and carries the refusal's
        /// own diagnostics — the underlying error code is reported as-is
        /// (SG-18: a settings error refuses with its OW-CFG-001/OW-CFG-002,
        /// a stale Registry with OW-RUN-009, never a new miscellaneous RUN
        /// code).
        /// </summary>
        Result<Unit> Admit(RunAcceptanceRequest request);
    }

    /// <summary>
    /// The accept request every <see cref="IRunAcceptanceGate"/> inspects:
    /// the compiled Plan, its bound workflow document and the requesting
    /// actor/options — the arguments the host is about to pass to the Core
    /// RunCoordinator's <c>Accept</c>.
    /// </summary>
    internal sealed record RunAcceptanceRequest(
        CompiledPlan Plan,
        WorkflowDocument Document,
        ActorKind Actor,
        AcceptOptions Options);
}
