#nullable enable

namespace OpWeave.Core.Model
{
    /// <summary>
    /// A reference to one port of one node: <c>{node, port}</c> (SPEC §A1, §A2).
    /// A small immutable-content type, so it stays a record per SPEC §A1 item 1.
    /// </summary>
    public sealed record PortRef(string Node, string Port);
}
