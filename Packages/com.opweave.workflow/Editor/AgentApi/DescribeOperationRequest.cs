#nullable enable

using System.Collections.Generic;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>describe_operation</c> command (SPEC §H1,
    /// §C8): the Operation <paramref name="Id"/> — required, empty is
    /// OW-API-001 — the optional pinned <paramref name="Version"/> (the
    /// newest registered version answers when omitted), the optional
    /// <see cref="OpWeave.Core.Search.DescribeFields"/> projection
    /// <paramref name="Fields"/> (null returns every section, unknown
    /// names are ignored) and the response language
    /// <paramref name="Lang"/>.
    /// </summary>
    public sealed record DescribeOperationRequest(
        string? Id,
        int? Version = null,
        IReadOnlyList<string>? Fields = null,
        string? Lang = null,
        string? Cursor = null);
}
