#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The port value comes from the output <paramref name="Port"/> of the step at
    /// <paramref name="StepIndex"/> in the Plan's D5 execution order (SPEC §D7).
    /// <paramref name="StepIndex"/> is non-negative.
    /// </summary>
    public sealed record FromStep(int StepIndex, string Port) : InputSource;
}
