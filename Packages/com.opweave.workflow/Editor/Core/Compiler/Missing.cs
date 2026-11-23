#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The "not supplied" input source (SPEC §A0.1): an optional port with no
    /// explicit source and no Descriptor default stays <see cref="Missing"/> and
    /// is passed to the Operation as absent — never stored or serialized as JSON
    /// <c>null</c>. A required port left <see cref="Missing"/> is OW-VAL-005
    /// (SPEC §A1 item 2).
    /// </summary>
    public sealed record Missing : InputSource;
}
