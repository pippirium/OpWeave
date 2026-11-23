#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The port value comes from the workflow input <paramref name="InputId"/>
    /// bound at execution (SPEC §D7).
    /// </summary>
    public sealed record FromInput(string InputId) : InputSource;
}
