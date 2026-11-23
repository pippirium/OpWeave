#nullable enable

namespace OpWeave.Core.Model
{
    /// <summary>
    /// A control-flow edge <c>{from, to, fromPort?}</c> defining execution order
    /// (SPEC §A1, §A2). <see cref="FromPort"/> defaults to <c>"next"</c>; it is reserved
    /// for the appendix-N branch work and v1 accepts no other value.
    /// </summary>
    public sealed record ControlEdge(string From, string To, string FromPort = "next");
}
