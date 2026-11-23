#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The port value is the Operation Descriptor's declared default
    /// <paramref name="Value"/>, used only when the port has no explicit source
    /// (SPEC §D7, §A1 item 2).
    /// </summary>
    public sealed record Default(JsonValue Value) : InputSource;
}
