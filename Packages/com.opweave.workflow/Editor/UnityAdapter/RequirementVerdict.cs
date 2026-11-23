#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The verdict of a SPEC §G1 item 4 Requirement evaluation, carried by
    /// <see cref="RequirementResult"/>.
    /// </summary>
    public enum RequirementVerdict
    {
        /// <summary>The requirement holds.</summary>
        Satisfied,

        /// <summary>
        /// The requirement is not satisfied now but resolves by waiting and
        /// re-evaluating — it carries no error code and no diagnostic (the
        /// 2026-11-06 owner decision for <c>not_compiling</c> while the Editor
        /// compiles, SPEC §1.4, §E4 step 2 and §G1 item 4: after compilation
        /// the requirement is satisfied, so the runner simply waits).
        /// </summary>
        Wait,

        /// <summary>
        /// The requirement is violated; <see cref="RequirementResult.Diagnostic"/>
        /// reports the failure.
        /// </summary>
        Violated,
    }
}
