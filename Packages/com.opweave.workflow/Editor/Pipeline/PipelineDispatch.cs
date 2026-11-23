#nullable enable
// T-W3.3-02: the dispatch seam of the OpWeave.Pipeline adapter (SPEC §H2
// item 5). Every CliCommand handler funnels through Invoke, which applies
// the single transport split the SPEC fixes: business failures stay inside
// the AgentApiResponse envelope as ok:false and return as the command's
// normal result, while the OW-API-* request-shape/misuse family leaves as a
// Pipeline error response — PipelineCommandException carries the same
// envelope JSON, so the exec error details still name the OW-API-* code.
// L-01 item 5 measured that a thrown non-argument exception surfaces as
// "Command Execution Failed" with the message in errorDetails, and warned
// that a thrown ArgumentException is classified as plain binding misuse —
// the dedicated exception type keeps that distinction.

using System;
using Newtonsoft.Json.Linq;
using OpWeave.AgentApi;
using OpWeave.Core;

namespace OpWeave.Pipeline
{
    /// <summary>
    /// Marks a command outcome that must leave as a Pipeline error response
    /// rather than a normal <c>{ok:false}</c> result: the exception's message
    /// is the serialized AgentApiResponse envelope, so the client still reads
    /// the OW-API-* diagnostic from the exec error details (SPEC §H2 item 5).
    /// </summary>
    internal sealed class PipelineCommandException : Exception
    {
        internal PipelineCommandException(string envelopeJson)
            : base(envelopeJson)
        {
        }
    }

    /// <summary>
    /// Runs one <see cref="AgentApiService"/> command and returns its wire
    /// envelope — or throws <see cref="PipelineCommandException"/> for the
    /// OW-API-* outcomes SPEC §H2 item 5 routes through Pipeline's error
    /// channel.
    /// </summary>
    internal static class PipelineDispatch
    {
        /// <summary>
        /// Resolves the installed service, invokes <paramref name="command"/>
        /// and returns <see cref="AgentApiResponse.ToWire"/> verbatim — the
        /// pipeline server serializes it into the exec <c>result</c>.
        /// </summary>
        /// <exception cref="PipelineCommandException">
        /// The service is not installed in this domain (OW-API-003 — the
        /// Editor cannot accept the command now, retryable), or the response
        /// carries an OW-API-* request-shape/misuse diagnostic, which leaves
        /// as a Pipeline error response instead of a normal result.
        /// </exception>
        internal static object Invoke(Func<AgentApiService, AgentApiResponse> command)
        {
            AgentApiService api;
            try
            {
                api = AgentApiHost.Api;
            }
            catch (InvalidOperationException)
            {
                // No service installed in this domain (the runner host failed
                // to build, or the domain-load install has not landed yet):
                // the Editor cannot accept the command — OW-API-003, the
                // published retryable "Editor busy" refusal (SPEC §J).
                throw new PipelineCommandException(EditorBusyJson());
            }

            var response = command(api);
            if (!response.Ok && CarriesApiMisuse(response))
            {
                throw new PipelineCommandException(response.ToJson());
            }
            return response.ToWire();
        }

        // Any OW-API-* diagnostic on a failed envelope marks request-shape or
        // API misuse — OW-API-001 malformed request, OW-API-002 invalid
        // cursor, OW-API-004 internal error — which SPEC §H2 item 5 lifts out
        // of the normal response into Pipeline's error channel.
        private static bool CarriesApiMisuse(AgentApiResponse response)
        {
            foreach (var diagnostic in response.Errors)
            {
                if (diagnostic.Code.StartsWith("OW-API-", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // The OW-API-003 refusal in the §H3 envelope shape — the same wire
        // object AgentApiResponse.ToWire produces, hand-built because a
        // response can only be constructed inside the service.
        private static string EditorBusyJson()
        {
            var diagnostic = ErrorCodes.Create(
                ErrorCodes.OW_API_003,
                "The Editor is not ready to accept Agent commands; retry in a few seconds.",
                hint: "The OpWeave Agent API service is not installed in this Editor domain yet.");
            // The same member shape AgentApiResponse.DiagnosticToJson emits:
            // optional members appear only when set, retryable always.
            var wire = new JObject
            {
                ["code"] = diagnostic.Code,
                ["severity"] = JsonNaming.ToSnakeCase(diagnostic.Severity.ToString()),
                ["message"] = diagnostic.Message,
            };
            if (diagnostic.Hint is not null)
            {
                wire["hint"] = diagnostic.Hint!;
            }
            wire["retryable"] = diagnostic.Retryable;
            var envelope = new JObject
            {
                ["ok"] = false,
                ["data"] = JValue.CreateNull(),
                ["errors"] = new JArray(wire),
                ["warnings"] = new JArray(),
            };
            return CanonicalJson.Serialize(envelope);
        }
    }
}
