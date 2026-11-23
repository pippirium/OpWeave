#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The token-efficient response envelope every Agent command returns
    /// (SPEC §H3 item 1). The wire shape is exactly
    /// <c>{ ok, data, errors, warnings, nextCursor?, truncated? }</c>: the
    /// <c>errors</c>/<c>warnings</c> elements are SPEC §0.4 Diagnostics, and the
    /// optional pair <c>nextCursor</c>+<c>truncated</c> appears only together,
    /// when a page was cut short (SPEC §H3 item 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction is internal so every response passes through
    /// <see cref="AgentApiService"/>, the single place enforcing the 64 KiB cap;
    /// the transport (Pipeline or a future entry point) only serializes
    /// <see cref="ToWire"/>/<see cref="ToJson"/>.
    /// </para>
    /// <para>
    /// <see cref="Data"/> is a <see cref="JToken"/> kept behind defensive copies
    /// per SPEC §0.4: the constructor clones the producer's token and every
    /// reader — the property and <see cref="ToWire"/> — hands out a fresh clone,
    /// so nothing downstream mutates a response another layer already holds.
    /// <see cref="Errors"/> and <see cref="Warnings"/> are likewise copied into
    /// immutable arrays at construction, so mutating the caller's collection
    /// afterwards cannot change <see cref="ToWire"/>/<see cref="ToJson"/> or push
    /// an already validated response past the 64 KiB cap.
    /// </para>
    /// <para>
    /// Diagnostics serialize camelCase with snake_case enum text (SPEC §0.4).
    /// The optional record members (<c>nodeId</c>, <c>port</c>,
    /// <c>jsonPath</c>, <c>hint</c>) are omitted when null — the same omission
    /// rule the envelope applies to <c>nextCursor</c>/<c>truncated</c> — while
    /// <c>retryable</c>, a non-nullable member of the §0.4 record, is always
    /// emitted (local serialization choice; a literal <c>null</c> would carry no
    /// information and waste tokens).
    /// </para>
    /// </remarks>
    public sealed class AgentApiResponse
    {
        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private readonly JToken? _data;

        private AgentApiResponse(
            bool ok,
            JToken? data,
            IReadOnlyList<Diagnostic> errors,
            IReadOnlyList<Diagnostic> warnings,
            string? nextCursor)
        {
            Ok = ok;
            _data = data?.DeepClone();
            Errors = ReadOnlyCollections.CopyList(errors);
            Warnings = ReadOnlyCollections.CopyList(warnings);
            NextCursor = nextCursor;
        }

        /// <summary><c>true</c> on business success; <c>false</c> on any failure,
        /// including the request-shape and API misuse failures of SPEC §H2 item 5
        /// that Pipeline lifts out of the normal response.</summary>
        public bool Ok { get; }

        /// <summary>
        /// The command payload — a fresh clone on every read (SPEC §0.4). Null on
        /// failure; the wire form always carries the member as explicit
        /// <c>"data": null</c>.
        /// </summary>
        public JToken? Data => _data?.DeepClone();

        /// <summary>Error-severity diagnostics; never empty on a whole failed
        /// response — a truncation segment of one keeps <c>ok:false</c> with an
        /// empty list because the real diagnostics ride inside the segmented
        /// content the client reassembles (SPEC §H3 item 3).</summary>
        public IReadOnlyList<Diagnostic> Errors { get; }

        /// <summary>Warning/info diagnostics that ride on success or failure.</summary>
        public IReadOnlyList<Diagnostic> Warnings { get; }

        /// <summary>
        /// The opaque continuation of a truncated page (SPEC §H3 item 3), or null
        /// when the page is complete. Issued and verified by the service's cursor
        /// codec; transports and commands never inspect its content.
        /// </summary>
        public string? NextCursor { get; }

        /// <summary><c>true</c> exactly when a continuation
        /// <see cref="NextCursor"/> is present.</summary>
        public bool Truncated => NextCursor is not null;

        /// <summary>
        /// The wire object: exactly <c>ok</c>, <c>data</c>, <c>errors</c>,
        /// <c>warnings</c> plus the <c>nextCursor</c>/<c>truncated</c> pair on a
        /// truncated page — a fresh <see cref="JObject"/> per call.
        /// </summary>
        public JObject ToWire()
        {
            var wire = new JObject
            {
                ["ok"] = Ok,
                ["data"] = _data is null ? JValue.CreateNull() : _data.DeepClone(),
                ["errors"] = DiagnosticsToJson(Errors),
                ["warnings"] = DiagnosticsToJson(Warnings),
            };
            if (NextCursor is not null)
            {
                wire["nextCursor"] = NextCursor;
                wire["truncated"] = true;
            }
            return wire;
        }

        /// <summary>The Canonical JSON form of <see cref="ToWire"/>: the single
        /// JSON implementation also decides the SPEC §H3 item 3 byte size.</summary>
        public string ToJson() => CanonicalJson.Serialize(ToWire());

        /// <summary>UTF-8 length of <see cref="ToJson"/> — the number the
        /// 64 KiB cap is measured against.</summary>
        internal int Utf8ByteCount => s_utf8.GetByteCount(ToJson());

        internal static AgentApiResponse Success(
            JToken? data, IReadOnlyList<Diagnostic> warnings, string? nextCursor)
        {
            if (warnings is null)
            {
                throw new ArgumentNullException(nameof(warnings));
            }
            return new AgentApiResponse(true, data, Empty(), warnings, nextCursor);
        }

        internal static AgentApiResponse Error(
            IReadOnlyList<Diagnostic> errors, IReadOnlyList<Diagnostic> warnings)
        {
            if (errors is null)
            {
                throw new ArgumentNullException(nameof(errors));
            }
            if (errors.Count == 0)
            {
                throw new ArgumentException(
                    "A failed response carries at least one diagnostic.", nameof(errors));
            }
            if (warnings is null)
            {
                throw new ArgumentNullException(nameof(warnings));
            }
            return new AgentApiResponse(false, null, errors, warnings, null);
        }

        /// <summary>A truncation segment of an oversized response (SPEC §H3
        /// item 3): <paramref name="ok"/> preserves the segmented envelope's
        /// outcome and <c>errors</c>/<c>warnings</c> stay empty on the wire —
        /// the real diagnostics ride inside the serialized content the client
        /// reassembles from the <paramref name="segmentText"/> strings.
        /// </summary>
        internal static AgentApiResponse Segment(
            bool ok, string segmentText, string? nextCursor)
        {
            if (segmentText is null)
            {
                throw new ArgumentNullException(nameof(segmentText));
            }
            return new AgentApiResponse(
                ok, new JValue(segmentText), Empty(), Empty(), nextCursor);
        }

        // The §0.4 record on the wire: camelCase members, snake_case severity,
        // null optionals omitted (see the class remarks).
        internal static JObject DiagnosticToJson(Diagnostic diagnostic)
        {
            var wire = new JObject
            {
                ["code"] = diagnostic.Code,
                ["severity"] = JsonNaming.ToSnakeCase(diagnostic.Severity.ToString()),
                ["message"] = diagnostic.Message,
            };
            if (diagnostic.NodeId is not null)
            {
                wire["nodeId"] = diagnostic.NodeId;
            }
            if (diagnostic.Port is not null)
            {
                wire["port"] = diagnostic.Port;
            }
            if (diagnostic.JsonPath is not null)
            {
                wire["jsonPath"] = diagnostic.JsonPath;
            }
            if (diagnostic.Hint is not null)
            {
                wire["hint"] = diagnostic.Hint;
            }
            wire["retryable"] = diagnostic.Retryable;
            return wire;
        }

        private static JArray DiagnosticsToJson(IReadOnlyList<Diagnostic> diagnostics)
        {
            var array = new JArray();
            for (var i = 0; i < diagnostics.Count; i++)
            {
                array.Add(DiagnosticToJson(diagnostics[i]));
            }
            return array;
        }

        private static IReadOnlyList<Diagnostic> Empty() =>
            Array.AsReadOnly(Array.Empty<Diagnostic>());
    }
}
