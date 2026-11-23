#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The non-interactive refusal of SPEC §H4 (T-W3.4-02), the Plan-permission
    /// position of the SG-18 acceptance-gate order: a non-interactive Editor
    /// (<see cref="UnityEngine.Application.isBatchMode"/>) can never produce
    /// the human decision a Plan carrying <c>approvals</c> waits for, so the
    /// Run is refused with OW-PERM-004 before any side effect — no Run
    /// directory, no journal, no lock probe — instead of parking an
    /// undecidable AwaitingApproval Run until its expiry. Runs without
    /// approvals are unaffected; the flag is re-read per accept so a fixture
    /// can toggle it.
    /// </summary>
    internal sealed class NonInteractiveApprovalGate : IRunAcceptanceGate
    {
        private readonly Func<bool> _isBatchMode;

        /// <exception cref="ArgumentNullException"><paramref name="isBatchMode"/> is null.</exception>
        internal NonInteractiveApprovalGate(Func<bool> isBatchMode) =>
            _isBatchMode = isBatchMode ?? throw new ArgumentNullException(nameof(isBatchMode));

        /// <inheritdoc cref="IRunAcceptanceGate.Admit"/>
        public Result<Unit> Admit(RunAcceptanceRequest request) =>
            _isBatchMode() && request.Plan.Approvals.Count > 0
                ? Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_PERM_004,
                    "The Run requires a human approval that a non-interactive Editor cannot grant."))
                : Result<Unit>.Success(Unit.Value);
    }
}
