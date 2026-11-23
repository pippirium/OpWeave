#nullable enable

namespace OpWeave.Testing
{
    /// <summary>
    /// The point inside one step at which
    /// <see cref="OperationTestHarness.RunInterrupted"/> stops (SPEC §M.6 item 1):
    /// <see cref="BeforeExecute"/> after the write-ahead <c>step_started</c>
    /// record was produced and before <see cref="OpWeave.Core.Operations.IOperation.Execute"/>
    /// was called, <see cref="AfterExecute"/> after <c>Execute</c> returned and
    /// before the harness produced the step's outcome record.
    /// </summary>
    /// <remarks>
    /// These are exactly the two crash windows the SPEC §E4 write-ahead order
    /// leaves open, so an interrupted harness run shows the journaled state the
    /// Runner's journal shows when the Editor dies there — <c>step_started</c>
    /// with no outcome record, which the SPEC §F2 fold reads as
    /// <see cref="OpWeave.Core.Persistence.StepState.Running"/> and SPEC §F5
    /// turns into the reconciliation question "did the effect happen?".
    /// <see cref="BeforeExecute"/> is the window in which it did not,
    /// <see cref="AfterExecute"/> the window in which it did.
    /// </remarks>
    public enum InterruptPoint
    {
        /// <summary>Stop after the write-ahead record, before <c>Execute</c> runs.</summary>
        BeforeExecute,

        /// <summary>Stop after <c>Execute</c> returned, before the outcome record.</summary>
        AfterExecute,
    }
}
