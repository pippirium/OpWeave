#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The input of <see cref="OperationSearchService.SearchOperations"/>
    /// (SPEC §C6): a free-text <see cref="Query"/> plus the conjunctive
    /// filters of §C6 item 1 — <see cref="AcceptsType"/>,
    /// <see cref="ProducesType"/>, <see cref="AllowedEffects"/>,
    /// <see cref="Tags"/>, <see cref="Category"/> and <see cref="Origin"/> —
    /// the response language <see cref="Lang"/> and the page coordinates
    /// <see cref="Limit"/> / <see cref="Offset"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every set filter must hold for a candidate: an Operation passes
    /// <see cref="AcceptsType"/> when some input port accepts a value of that
    /// type under SPEC §B6, <see cref="ProducesType"/> when some output is
    /// assignable to it, <see cref="AllowedEffects"/> when its declared side
    /// effects are a subset of the allowed list, <see cref="Tags"/> when it
    /// carries at least one of the requested tags, <see cref="Category"/> on
    /// Ordinal equality and <see cref="Origin"/> on registry origin equality.
    /// </para>
    /// <para>
    /// <see cref="Limit"/> is the §C6 item-4 page size: at least 1, at most
    /// <see cref="OperationSearchService.MaxLimit"/> — larger values are
    /// clamped, never an error. <see cref="Offset"/> is the continuation
    /// coordinate an <see cref="OperationSearchPage.NextOffset"/> returns;
    /// the opaque §H3 cursor is the Agent API's encoding of it, not part of
    /// this service contract.
    /// </para>
    /// </remarks>
    public sealed record OperationSearchRequest
    {
        private int _limit = OperationSearchService.DefaultLimit;
        private int _offset;

        /// <summary>Free-text query matched against the §C5 indexed fields; null or empty for a filter-only search.</summary>
        public string? Query { get; init; }

        /// <summary>Candidates must have an input port assignable from this Type ID.</summary>
        public string? AcceptsType { get; init; }

        /// <summary>Candidates must have an output port assignable to this Type ID.</summary>
        public string? ProducesType { get; init; }

        /// <summary>Candidates' declared §C4 side effects must all be in this list.</summary>
        public IReadOnlyList<string>? AllowedEffects { get; init; }

        /// <summary>Candidates must carry at least one of these tags (Ordinal equality).</summary>
        public IReadOnlyList<string>? Tags { get; init; }

        /// <summary>Candidates' <c>Category</c> must equal this value (Ordinal).</summary>
        public string? Category { get; init; }

        /// <summary>Candidates' registry origin must equal this value.</summary>
        public OperationOrigin? Origin { get; init; }

        /// <summary>The response language tag (<c>"en"</c> / <c>"ja"</c>); null
        /// falls back to the caller's <c>agentLanguage</c> default, then English
        /// (SPEC §C6 item 5).</summary>
        public string? Lang { get; init; }

        /// <summary>The page size; defaults to the §C6 default of 10 and is
        /// clamped to <see cref="OperationSearchService.MaxLimit"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value below 1.</exception>
        public int Limit
        {
            get => _limit;
            init => _limit = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), value, "The page size must be at least 1.");
        }

        /// <summary>The continuation position inside the ranked list.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Negative value.</exception>
        public int Offset
        {
            get => _offset;
            init => _offset = value >= 0
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), value, "The page offset must not be negative.");
        }
    }
}
