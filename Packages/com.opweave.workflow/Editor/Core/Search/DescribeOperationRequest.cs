#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The input of <see cref="OperationDescribeService.DescribeOperation"/>
    /// (SPEC §C8, §H1): the Operation <see cref="Id"/>, the optional pinned
    /// <see cref="Version"/> — the newest registered version answers when it
    /// is omitted — the optional <see cref="Fields"/> projection over the
    /// <see cref="DescribeFields"/> sections, and the response language
    /// <see cref="Lang"/>.
    /// </summary>
    /// <remarks>
    /// Each check runs in both the backing-field initializer and the
    /// <c>init</c> accessor so record <c>with</c> expressions cannot bypass it.
    /// </remarks>
    public sealed record DescribeOperationRequest(string Id)
    {
        private readonly string _id = CheckId(Id);
        private int? _version;

        /// <summary>The Operation ID to describe.</summary>
        /// <exception cref="ArgumentException">Null or empty.</exception>
        public string Id
        {
            get => _id;
            init => _id = CheckId(value);
        }

        /// <summary>The version to describe; null resolves the newest
        /// registered version (SPEC §C8).</summary>
        /// <exception cref="ArgumentOutOfRangeException">A value below 1.</exception>
        public int? Version
        {
            get => _version;
            init => _version = value is null || value.Value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(value), value, "An Operation version is a positive integer.");
        }

        /// <summary>The <see cref="DescribeFields"/> sections to return; null
        /// returns every section. Unknown names are ignored.</summary>
        public IReadOnlyList<string>? Fields { get; init; }

        /// <summary>The response language tag (<c>"en"</c> / <c>"ja"</c>);
        /// null falls back to the caller's <c>agentLanguage</c> default, then
        /// English (the §C6 item-5 choice describe shares).</summary>
        public string? Lang { get; init; }

        private static string CheckId(string id) =>
            string.IsNullOrEmpty(id)
                ? throw new ArgumentException(
                    "A describe request must carry an Operation ID.", nameof(Id))
                : id;
    }
}
