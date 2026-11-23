#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.Operations
{
    /// <summary>
    /// The example workflow documents of the §I.1 GameObject Operations
    /// (SPEC §C1 item 4, §H0 item 1): each one is a complete WF document —
    /// schemaVersion, the single <c>core.start</c> node, the nodes with
    /// every required input, the control edges that reach them, the data
    /// edges that feed the Ref inputs and the workflow <c>outputs</c> — that
    /// the static Validator accepts with no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None declares a <c>fixtureManifest</c>: the examples create the
    /// GameObjects they need inside the run, so static validation judges
    /// the document against the Registry and the type snapshot only.
    /// </para>
    /// </remarks>
    internal static class GameObjectOperationExamples
    {
        /// <summary>The <c>unity.gameobject.create</c> example.</summary>
        internal static readonly JsonValue Create = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.gameobject-create\"," +
            "\"title\":\"Create a cube named Player at the origin\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Player\",\"primitive\":\"cube\"," +
            "\"position\":{\"x\":0,\"y\":0,\"z\":0}}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}]," +
            "\"outputs\":[{\"id\":\"gameObject\",\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}}]}");

        /// <summary>The <c>unity.gameobject.find</c> example.</summary>
        internal static readonly JsonValue Find = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.gameobject-find\"," +
            "\"title\":\"Find the GameObject named Player\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Player\"}}," +
            "{\"id\":\"find\",\"operation\":\"unity.gameobject.find\",\"version\":1," +
            "\"parameters\":{\"name\":\"Player\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"find\"}]," +
            "\"outputs\":[{\"id\":\"gameObject\",\"from\":{\"node\":\"find\",\"port\":\"gameObject\"}}]}");

        /// <summary>The <c>unity.gameobject.delete</c> example.</summary>
        internal static readonly JsonValue Delete = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.gameobject-delete\"," +
            "\"title\":\"Delete the GameObject created by an earlier step\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Scrap\"}}," +
            "{\"id\":\"delete\",\"operation\":\"unity.gameobject.delete\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"delete\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"delete\",\"port\":\"target\"}}]}");

        /// <summary>The <c>unity.gameobject.set_parent</c> example.</summary>
        internal static readonly JsonValue SetParent = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.gameobject-set-parent\"," +
            "\"title\":\"Parent the created child under the created root\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"root\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Root\"}}," +
            "{\"id\":\"child\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Child\"}}," +
            "{\"id\":\"parent\",\"operation\":\"unity.gameobject.set_parent\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"root\"}," +
            "{\"from\":\"root\",\"to\":\"child\"},{\"from\":\"child\",\"to\":\"parent\"}]," +
            "\"dataEdges\":[" +
            "{\"from\":{\"node\":\"child\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"parent\",\"port\":\"target\"}}," +
            "{\"from\":{\"node\":\"root\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"parent\",\"port\":\"parent\"}}]," +
            "\"outputs\":[{\"id\":\"gameObject\",\"from\":{\"node\":\"parent\",\"port\":\"gameObject\"}}]}");

        /// <summary>The <c>unity.transform.set</c> example.</summary>
        internal static readonly JsonValue TransformSet = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.transform-set\"," +
            "\"title\":\"Move the created GameObject to a position and scale it\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Probe\"}}," +
            "{\"id\":\"place\",\"operation\":\"unity.transform.set\",\"version\":1," +
            "\"parameters\":{\"position\":{\"x\":1,\"y\":2,\"z\":3}," +
            "\"scale\":{\"x\":2,\"y\":2,\"z\":2}}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"place\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"place\",\"port\":\"target\"}}]}");

        private static JsonValue Parse(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "A GameObject Operation example workflow did not parse: " +
                    parsed.Diagnostics[0].Code);
            }
            return parsed.Value;
        }
    }
}
