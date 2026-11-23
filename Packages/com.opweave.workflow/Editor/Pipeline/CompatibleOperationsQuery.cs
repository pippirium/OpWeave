#nullable enable
// T-W3.3-02: the IStructuredCommandInput DTO of
// search_compatible_operations (SPEC §H2 item 2). The §C7 match input is a
// JSON object — { "type": "<TypeId>" } or { "operation": "<id>",
// "port": "<name>" } — so it arrives as one structured argument; the
// command maps the fields onto SearchCompatibleOperationsRequest.
// Exactly-one-of-the-two-forms and the direction value are the service's
// OW-API-001 contract, not duplicated here.

using Unity.Pipeline.Commands;

namespace OpWeave.Pipeline
{
    /// <summary>
    /// The §C7 match object of <c>opweave search_compatible_operations</c>:
    /// either <see cref="Type"/> alone or the <see cref="Operation"/>/
    /// <see cref="Port"/> pair. Public writable fields let the Pipeline
    /// binder deserialize the JSON argument member-wise and reflect the
    /// nested JSON schema (L-01 item 4).
    /// </summary>
    public sealed class CompatibleOperationsQuery : IStructuredCommandInput
    {
        /// <summary>A Type ID to match compatibly — used alone.</summary>
        [CliArg("type", "Type ID the compatible port consumes or produces")]
        public string? Type;

        /// <summary>An Operation ID — used together with <see cref="Port"/>.</summary>
        [CliArg("operation", "Operation ID; must be paired with \"port\"")]
        public string? Operation;

        /// <summary>A port name on <see cref="Operation"/>.</summary>
        [CliArg("port", "Port name on \"operation\"")]
        public string? Port;
    }
}
