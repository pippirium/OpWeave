#nullable enable

namespace OpWeave.Core.Model
{
    /// <summary>
    /// A data edge <c>{from:{node,port}, to:{node,port}}</c> carrying an output value
    /// to an input port (SPEC §A1, §A2). It is one of the three explicit supply sources
    /// of the R1 supply rule.
    /// </summary>
    public sealed record DataEdge(PortRef From, PortRef To);
}
