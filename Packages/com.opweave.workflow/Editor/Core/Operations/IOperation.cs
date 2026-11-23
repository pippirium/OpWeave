#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The lifecycle contract every Operation — standard, custom and external —
    /// implements (SPEC §C2, §C0.1). The Validator, Runner and recovery engine
    /// drive an Operation only through this surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations hold no instance state (SPEC §C2 item 4): state that must
    /// survive a domain reload lives only in the <see cref="StepResult.Waiting"/>
    /// resume token. <see cref="Execute"/> and <see cref="Poll"/> are called on
    /// the main thread and must not block; an Operation that needs to wait
    /// returns <see cref="StepResult.Waiting"/> and continues in
    /// <see cref="Poll"/>. Exceptions thrown by any member are converted to
    /// OW-RUN-006 by the step executor (SPEC §C2 item 8).
    /// </para>
    /// </remarks>
    public interface IOperation
    {
        /// <summary>The immutable contract this Operation declares (SPEC §C1).</summary>
        OperationDescriptor Descriptor { get; }

        /// <summary>Unity-independent parameter inspection (SPEC §C2).</summary>
        IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx);

        /// <summary>
        /// Inspects preconditions, predicts changes for Dry Run and returns the
        /// snapshot used by <see cref="Reconcile"/> — without applying any change
        /// (SPEC §C2 item 7).
        /// </summary>
        PreflightResult Preflight(StepContext ctx);

        /// <summary>Runs the step's effect (SPEC §C2).</summary>
        StepResult Execute(StepContext ctx);

        /// <summary>Continues a Waiting step; <see cref="ExecutionMode.Waitable"/> Operations only.</summary>
        StepResult Poll(StepContext ctx);

        /// <summary>Compares the pre-execution snapshot with the current state; Reconcilable Operations only (SPEC §F5).</summary>
        ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot);

        /// <summary>Best-effort cancellation of a Waiting step; <see cref="ExecutionMode.Waitable"/> Operations only.</summary>
        void Cancel(StepContext ctx);
    }
}
