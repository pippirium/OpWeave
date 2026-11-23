#nullable enable

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The three-way outcome of one runtime <see cref="Operations.Requirement"/>
    /// check (SPEC §G1 item 4, §E4 step 2): the precondition holds, it is
    /// violated with a diagnostic that fails the step, or the environment is
    /// transiently busy and the step must wait.
    /// </summary>
    /// <remarks>
    /// <see cref="Wait"/> is the "not satisfied, wait" answer of the
    /// 2026-11-06 owner decision: the only v1 evaluator that may return it is
    /// <c>not_compiling</c>, which carries no error code. A Wait outcome is
    /// never a step failure — the Runner writes no StepStarted and emits no
    /// diagnostic, ends that Tick's work for the step and re-evaluates from
    /// E4 step 1 on the next Tick.
    /// </remarks>
    internal enum RequirementStatus
    {
        /// <summary>The precondition holds; the step may proceed.</summary>
        Satisfied,

        /// <summary>The precondition is violated; the step fails with the carried diagnostic.</summary>
        Violated,

        /// <summary>
        /// Not satisfied but transient — no error code; the step is not started
        /// and is re-evaluated next Tick (<c>not_compiling</c>).
        /// </summary>
        Wait,
    }
}
