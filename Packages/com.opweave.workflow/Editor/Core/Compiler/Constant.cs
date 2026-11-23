#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The port value is the literal <paramref name="Value"/> written in the
    /// workflow document's <c>parameters</c> (SPEC §D7). An explicit JSON
    /// <c>null</c> here is a present value — not <see cref="Missing"/> — and keeps
    /// its own canonical bytes (SPEC §A0.1, §0.6 SG-12).
    /// </summary>
    public sealed record Constant(JsonValue Value) : InputSource;
}
