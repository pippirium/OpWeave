#nullable enable

namespace OpWeave.Core.Model
{
    /// <summary>
    /// A workflow-input binding <c>{fromInput, to:{node,port}}</c> connecting one WF
    /// input to one input port (SPEC §A1, §A6). One input may bind to several ports,
    /// each of which counts it as its single explicit supply source (R1).
    /// </summary>
    public sealed record InputBinding(string FromInput, PortRef To);
}
