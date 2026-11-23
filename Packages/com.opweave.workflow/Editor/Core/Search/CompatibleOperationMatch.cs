#nullable enable

using OpWeave.Core.Registry;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// One <see cref="OperationSearchService.SearchCompatibleOperations"/>
    /// result (SPEC §C7): a registered Operation and the port of it that
    /// connects to the queried type, tagged with the <see cref="Match"/> kind
    /// (<c>exact</c> / <c>base</c> / <c>json</c>). The Operation carries its
    /// <see cref="RegisteredOperation.Origin"/> — the <c>origin</c> the
    /// response attaches to every result.
    /// </summary>
    public sealed record CompatibleOperationMatch(
        RegisteredOperation Operation,
        string Port,
        CompatibleMatchKind Match)
    {
        /// <summary>The matched Operation (a snapshot member).</summary>
        public RegisteredOperation Operation { get; init; } =
            Operation ?? throw new System.ArgumentNullException(nameof(Operation));

        /// <summary>The matching port name; an input for
        /// <see cref="CompatibleDirection.Downstream"/>, an output for
        /// <see cref="CompatibleDirection.Upstream"/>.</summary>
        public string Port { get; init; } =
            string.IsNullOrEmpty(Port)
                ? throw new System.ArgumentException(
                    "The matching port name must not be null or empty.", nameof(Port))
                : Port;
    }
}
