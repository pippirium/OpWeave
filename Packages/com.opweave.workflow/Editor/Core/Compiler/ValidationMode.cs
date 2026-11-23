#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The kind of validation a <see cref="ValidationRequest"/> asks for (SPEC §D1):
    /// <see cref="Static"/> runs the D2–D6 checks only; <see cref="DryRun"/> continues
    /// with the D8 Dry Run preflight phase and reports planned changes.
    /// </summary>
    public enum ValidationMode
    {
        Static,
        DryRun,
    }
}
