#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// One page of <see cref="OperationSearchService.SearchOperations"/>
    /// results (SPEC §C6 item 4): the ranked <see cref="Items"/> and the
    /// <see cref="NextOffset"/> continuation — null when the page reached the
    /// end of the ranked list. The opaque cursor of the wire response is the
    /// Agent API's encoding of this offset (SPEC §H3), not part of the
    /// service contract.
    /// </summary>
    public sealed record OperationSearchPage(
        IReadOnlyList<OperationSearchResult> Items,
        int? NextOffset)
    {
        /// <summary>The ranked items of this page.</summary>
        public IReadOnlyList<OperationSearchResult> Items { get; init; } =
            ReadOnlyCollections.CopyList(Items);
    }
}
