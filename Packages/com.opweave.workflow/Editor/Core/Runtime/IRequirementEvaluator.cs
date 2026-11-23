#nullable enable

using OpWeave.Core.Operations;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The injected runtime Requirement checker of SPEC §E4 step 2: evaluates
    /// one declared <see cref="Requirement"/> — including the dynamic-path
    /// permission check — against the live environment through the step's
    /// resolved-input context. The UnityAdapter supplies the G1 evaluators
    /// (<c>edit_mode</c>, <c>not_compiling</c>, <c>scene_loaded(port)</c>,
    /// <c>path_writable(port)</c>, <c>type_exists(port)</c>) in T-W2.1-02;
    /// Core owns only this seam so the run loop never touches Unity.
    /// </summary>
    internal interface IRequirementEvaluator
    {
        /// <summary>
        /// Evaluates <paramref name="requirement"/> in the step context
        /// (resolved inputs are readable through
        /// <see cref="StepContext.Get{T}"/> for port-bound kinds).
        /// </summary>
        RequirementEvaluation Evaluate(Requirement requirement, StepContext context);
    }
}
