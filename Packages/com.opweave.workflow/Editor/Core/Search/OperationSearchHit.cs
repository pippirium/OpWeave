#nullable enable

using OpWeave.Core.Registry;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// One <see cref="OperationSearchIndex"/> result: a registered Operation
    /// and its relevance score — an exact ID match scores 1.0, anything else
    /// the best per-field Dice coefficient of the query token set (SPEC §C5).
    /// </summary>
    public sealed record OperationSearchHit(
        RegisteredOperation Operation,
        double Score)
    {
        /// <summary>The matched Operation (a snapshot member).</summary>
        public RegisteredOperation Operation { get; init; } =
            Operation ?? throw new System.ArgumentNullException(nameof(Operation));
    }
}
