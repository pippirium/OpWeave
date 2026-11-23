#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.Operations
{
    /// <summary>
    /// The example workflow documents of the §I.1 property Operations
    /// (SPEC §C1 item 4, §H0 item 1): each one is a complete WF document —
    /// schemaVersion, the single <c>core.start</c> node, the nodes with
    /// every required input, the control edges that reach them and the
    /// data edge that feeds the Ref target — that the static Validator
    /// accepts with no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None declares a <c>fixtureManifest</c>: the examples create the
    /// GameObject they read and write inside the run, so static validation
    /// judges the document against the Registry and the type snapshot only.
    /// A <c>unity.GameObjectRef</c>-fed <c>target</c> resolves
    /// <c>unity.property.set</c>'s actual effect to <c>scene.modify</c>
    /// (SPEC §C4), which the default policy allows without approvals.
    /// </para>
    /// </remarks>
    internal static class PropertyOperationExamples
    {
        /// <summary>The <c>unity.property.get</c> example.</summary>
        internal static readonly JsonValue PropertyGet = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.property-get\"," +
            "\"title\":\"Read the created GameObject's name\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Probe\"}}," +
            "{\"id\":\"get\",\"operation\":\"unity.property.get\",\"version\":1," +
            "\"parameters\":{\"propertyPath\":\"m_Name\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"get\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"get\",\"port\":\"target\"}}]," +
            "\"outputs\":[{\"id\":\"value\",\"from\":{\"node\":\"get\",\"port\":\"value\"}}]}");

        /// <summary>The <c>unity.property.set</c> example.</summary>
        internal static readonly JsonValue PropertySet = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.property-set\"," +
            "\"title\":\"Rename the created GameObject\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Old\"}}," +
            "{\"id\":\"set\",\"operation\":\"unity.property.set\",\"version\":1," +
            "\"parameters\":{\"propertyPath\":\"m_Name\",\"value\":\"New\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"set\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"set\",\"port\":\"target\"}}]}");

        private static JsonValue Parse(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "A property Operation example workflow did not parse: " +
                    parsed.Diagnostics[0].Code);
            }
            return parsed.Value;
        }
    }
}
