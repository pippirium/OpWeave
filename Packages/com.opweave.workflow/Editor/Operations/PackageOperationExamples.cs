#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.Operations
{
    /// <summary>
    /// The example workflow document of <c>unity.package.add</c>
    /// (SPEC §C1 item 4, §H0 item 1): a complete WF document —
    /// schemaVersion, the single <c>core.start</c> node, the node with
    /// every required input, the control edge that reaches it and the
    /// workflow <c>outputs</c> — that the static Validator accepts with
    /// no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <c>fixtureManifest</c> is declared: the example adds a
    /// registry package, so nothing needs to exist in the project
    /// first. The constant <c>com.unity.*</c> id satisfies the
    /// ConstantOnly constraint (§G8.1) and the default
    /// <c>allowedPackageSources</c> rule <c>registry:com.unity.*</c>,
    /// so validation stays warning-free; the <c>package.modify</c>
    /// effect becomes a plan approval requirement, not a diagnostic.
    /// </para>
    /// </remarks>
    internal static class PackageOperationExamples
    {
        /// <summary>The <c>unity.package.add</c> example.</summary>
        internal static readonly JsonValue AddEditorCoroutines = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.package-add-editorcoroutines\"," +
            "\"title\":\"Add the Editor Coroutines package\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"add\",\"operation\":\"unity.package.add\",\"version\":1," +
            "\"parameters\":{\"id\":\"com.unity.editorcoroutines@1.0.0\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"add\"}]," +
            "\"outputs\":[{\"id\":\"package\",\"from\":{\"node\":\"add\",\"port\":\"package\"}}]}");

        /// <summary>The <c>unity.package.remove</c> example: the node has
        /// no outputs, so the workflow declares no <c>outputs</c> — the
        /// same shape <c>unity.asset.delete</c>'s example uses.</summary>
        internal static readonly JsonValue RemoveEditorCoroutines = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.package-remove-editorcoroutines\"," +
            "\"title\":\"Remove the Editor Coroutines package\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"remove\",\"operation\":\"unity.package.remove\",\"version\":1," +
            "\"parameters\":{\"name\":\"com.unity.editorcoroutines\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"remove\"}]}");

        private static JsonValue Parse(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "A package Operation example workflow did not parse: " +
                    parsed.Diagnostics[0].Code);
            }
            return parsed.Value;
        }
    }
}
