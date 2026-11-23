#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The Compiler/Validator entry point (SPEC §D1): runs the validation pipeline's
    /// phases in order, gathers the diagnostics and returns them as a structured
    /// report instead of throwing across the layer boundary (SPEC §0.4).
    /// </summary>
    public interface IWorkflowCompiler
    {
        /// <summary>Validates <paramref name="request"/> and returns the report.</summary>
        ValidationReport Validate(ValidationRequest request);
    }
}
