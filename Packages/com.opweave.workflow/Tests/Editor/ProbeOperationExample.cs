#nullable enable

using System;
using System.Globalization;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Builds the minimal complete-WF <see cref="OperationExample"/> a
    /// compiled probe Operation publishes so the live Registry satisfies
    /// SPEC §C1 item 4 without any test-support exemption: a
    /// <c>core.start</c> node, the probe's own node carrying the constant
    /// parameters its required inputs need, the single control edge, and —
    /// when the probe publishes an output — a workflow output bound to it.
    /// </summary>
    internal static class ProbeOperationExample
    {
        internal static OperationExample For(
            string operationId,
            int version,
            string? parametersJson = null,
            string? outputPort = null)
        {
            var versionText = version.ToString(CultureInfo.InvariantCulture);
            var parsed = JsonValue.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"example." +
                operationId + "-v" + versionText +
                "\",\"title\":\"Run " + operationId + " version " + versionText +
                "\",\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"op\",\"operation\":\"" + operationId +
                "\",\"version\":" + versionText +
                (parametersJson is null ? string.Empty : ",\"parameters\":" + parametersJson) +
                "}],\"controlEdges\":[{\"from\":\"start\",\"to\":\"op\"}]" +
                (outputPort is null ? string.Empty :
                    ",\"outputs\":[{\"id\":\"result\",\"from\":{\"node\":\"op\",\"port\":\"" +
                    outputPort + "\"}}]") +
                "}");
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new ArgumentException(
                    "Probe example JSON did not parse for " + operationId + "@" + versionText,
                    nameof(parametersJson));
            }
            return new OperationExample(
                "Run " + operationId + " version " + versionText, parsed.Value);
        }
    }
}
