#nullable enable

using System;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The input of <see cref="OperationSearchService.SearchCompatibleOperations"/>
    /// (SPEC §C7): either a <see cref="TypeId"/> query — <c>{ "type": … }</c> —
    /// or an <see cref="OperationId"/> + <see cref="Port"/> pair —
    /// <c>{ "operation": …, "port": … }</c> — plus a
    /// <see cref="Direction"/> and the <see cref="IncludeJson"/> flag.
    /// </summary>
    /// <remarks>
    /// The two query forms are the construction-time factories
    /// <see cref="ForType"/> and <see cref="ForOperationPort"/>: a request
    /// cannot hold a mix of both. For the Operation form the named port is
    /// looked up on the direction's own side — an output port for
    /// <see cref="CompatibleDirection.Downstream"/>, an input port for
    /// <see cref="CompatibleDirection.Upstream"/> — and the newest registered
    /// version answers, matching the SPEC §C8 version-omitted default.
    /// </remarks>
    public sealed record CompatibleOperationsRequest
    {
        private CompatibleOperationsRequest(
            string? typeId,
            string? operationId,
            string? port,
            CompatibleDirection direction,
            bool includeJson)
        {
            TypeId = typeId;
            OperationId = operationId;
            Port = port;
            Direction = direction;
            IncludeJson = includeJson;
        }

        /// <summary>The queried Type ID, or null for the Operation form.</summary>
        public string? TypeId { get; }

        /// <summary>The queried Operation ID, or null for the type form.</summary>
        public string? OperationId { get; }

        /// <summary>The queried port name, or null for the type form.</summary>
        public string? Port { get; }

        /// <summary>Which connection direction to search.</summary>
        public CompatibleDirection Direction { get; }

        /// <summary>Whether pairs that match only through <c>core.Json</c>
        /// (SPEC §B6 rule 2) are returned.</summary>
        public bool IncludeJson { get; }

        /// <summary>
        /// The <c>{ "type": "&lt;TypeId&gt;" }</c> query form.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="typeId"/> is null or empty.</exception>
        public static CompatibleOperationsRequest ForType(
            string typeId,
            CompatibleDirection direction,
            bool includeJson = false)
        {
            if (string.IsNullOrEmpty(typeId))
            {
                throw new ArgumentException(
                    "A type query must carry a Type ID.", nameof(typeId));
            }
            return new CompatibleOperationsRequest(
                typeId, null, null, direction, includeJson);
        }

        /// <summary>
        /// The <c>{ "operation": "&lt;id&gt;", "port": "&lt;name&gt;" }</c>
        /// query form: the queried type becomes the named port's type.
        /// </summary>
        /// <exception cref="ArgumentException">Either argument is null or empty.</exception>
        public static CompatibleOperationsRequest ForOperationPort(
            string operationId,
            string port,
            CompatibleDirection direction,
            bool includeJson = false)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                throw new ArgumentException(
                    "An Operation query must carry an Operation ID.", nameof(operationId));
            }
            if (string.IsNullOrEmpty(port))
            {
                throw new ArgumentException(
                    "An Operation query must carry a port name.", nameof(port));
            }
            return new CompatibleOperationsRequest(
                null, operationId, port, direction, includeJson);
        }
    }
}
