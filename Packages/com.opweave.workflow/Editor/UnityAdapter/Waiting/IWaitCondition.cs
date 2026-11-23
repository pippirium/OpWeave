#nullable enable

using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §G4 wait-condition contract: the "has it completed yet"
    /// judgement a <see cref="ExecutionMode.Waitable"/> Operation repeats in
    /// <see cref="IOperation.Poll"/>, shared by standard and custom
    /// Operations alike (§G4 "カスタム Operation も利用できる").
    /// </summary>
    /// <remarks>
    /// <para>
    /// A condition restores its wait state from the resume token alone
    /// (§G4 "条件は resumeToken だけから復元できる") — the token persisted
    /// by <see cref="StepResult.Waiting"/> and handed back through
    /// <see cref="StepContext.ResumeToken"/> on the next Poll is the only
    /// state that survives a domain reload, so a condition instance created
    /// in a fresh domain must answer exactly what the lost instance would
    /// have answered. Whatever a <see cref="WaitStatusKind.Pending"/> verdict
    /// still needs is therefore written back into the token it returns.
    /// </para>
    /// </remarks>
    public interface IWaitCondition
    {
        /// <summary>
        /// Evaluates the wait once: <see cref="WaitStatusKind.Done"/> when the
        /// awaited state is observed, <see cref="WaitStatusKind.Failed"/> with
        /// the step's failure diagnostic, or
        /// <see cref="WaitStatusKind.Pending"/> carrying the resume token the
        /// next Poll continues from (SPEC §G4).
        /// </summary>
        WaitStatus Check(JsonValue token);
    }
}
