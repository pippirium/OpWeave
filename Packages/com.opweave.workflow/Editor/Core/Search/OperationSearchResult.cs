#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// One ranked item of a <see cref="OperationSearchPage"/> — the compact
    /// summary shape of SPEC §C6: the registered Operation (whose
    /// <see cref="RegisteredOperation.Descriptor"/> supplies id, version,
    /// effects, destructive flag and mode, and whose
    /// <see cref="RegisteredOperation.Origin"/> supplies the response origin),
    /// its display text resolved for the request language, the port
    /// name → Type ID maps and the §C6 score it ranked by.
    /// </summary>
    public sealed record OperationSearchResult(
        RegisteredOperation Operation,
        string DisplayName,
        string Summary,
        IReadOnlyDictionary<string, string> Inputs,
        IReadOnlyDictionary<string, string> Outputs,
        double Score)
    {
        /// <summary>The matched Operation (a snapshot member).</summary>
        public RegisteredOperation Operation { get; init; } =
            Operation ?? throw new ArgumentNullException(nameof(Operation));

        /// <summary>The display name in the request language, English when the language is absent.</summary>
        public string DisplayName { get; init; } =
            DisplayName ?? throw new ArgumentNullException(nameof(DisplayName));

        /// <summary>The summary in the request language, English when the language is absent.</summary>
        public string Summary { get; init; } =
            Summary ?? throw new ArgumentNullException(nameof(Summary));

        /// <summary>Input port name → Type ID, in declaration order.</summary>
        public IReadOnlyDictionary<string, string> Inputs { get; init; } =
            ReadOnlyCollections.CopyMap(Inputs);

        /// <summary>Output port name → Type ID, in declaration order.</summary>
        public IReadOnlyDictionary<string, string> Outputs { get; init; } =
            ReadOnlyCollections.CopyMap(Outputs);
    }
}
