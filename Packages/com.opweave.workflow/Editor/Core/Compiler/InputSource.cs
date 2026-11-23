#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The resolved source of one step input port on a <c>PlanStep</c> (SPEC §D7):
    /// exactly one of <see cref="FromStep"/>, <see cref="FromInput"/>,
    /// <see cref="Constant"/>, <see cref="Default"/> or <see cref="Missing"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Missing"/> is the R1 "no explicit source, no Descriptor default"
    /// outcome for an optional port — a distinct kind, never a JSON
    /// <c>null</c> (SPEC §A0.1). The five kinds are the complete SG-12 persisted
    /// set; <see cref="InputSourceJson"/> rejects every other tag.
    /// </remarks>
    public abstract record InputSource;
}
