#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.Operations
{
    /// <summary>
    /// The example workflow documents of the §I.1 Scene Operations
    /// (SPEC §C1 item 4, §H0 item 1): each one is a complete WF document —
    /// schemaVersion, the single <c>core.start</c> node, the node with every
    /// required input supplied or edge-fed, the control edges that reach it
    /// and the workflow <c>outputs</c> — that the static Validator accepts
    /// with no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None of the four declares a <c>fixtureManifest</c>: static
    /// validation judges the document against the Registry only, and none
    /// of these examples needs a scene to pre-exist — the open/close pair
    /// names a path that is never opened during validation, and the
    /// save/close examples feed their scene port from a data edge so no
    /// constant SceneRef naming a live scene is required.
    /// </para>
    /// <para>
    /// The constant paths are inside the default <c>allowedPaths</c>
    /// (<c>Assets/</c>) so the §D6 constant-path check stays clean.
    /// </para>
    /// </remarks>
    internal static class SceneOperationExamples
    {
        /// <summary>The <c>unity.scene.open</c> example.</summary>
        internal static readonly JsonValue OpenScene = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.scene-open\"," +
            "\"title\":\"Open the main scene additively\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"open\",\"operation\":\"unity.scene.open\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/Scenes/Main.unity\",\"mode\":\"additive\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"open\"}]," +
            "\"outputs\":[{\"id\":\"scene\",\"from\":{\"node\":\"open\",\"port\":\"scene\"}}]}");

        /// <summary>The <c>unity.scene.create</c> example.</summary>
        internal static readonly JsonValue CreateScene = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.scene-create\"," +
            "\"title\":\"Create and open a new scene\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.scene.create\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/Scenes/New.unity\",\"setup\":\"default\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}]," +
            "\"outputs\":[{\"id\":\"scene\",\"from\":{\"node\":\"create\",\"port\":\"scene\"}}]}");

        /// <summary>The <c>unity.scene.save</c> example.</summary>
        internal static readonly JsonValue SaveScene = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.scene-save\"," +
            "\"title\":\"Create a scene and save it under a new path\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.scene.create\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/Scenes/Working.unity\"}}," +
            "{\"id\":\"save\",\"operation\":\"unity.scene.save\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/Scenes/WorkingCopy.unity\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"save\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"create\",\"port\":\"scene\"}," +
            "\"to\":{\"node\":\"save\",\"port\":\"scene\"}}]," +
            "\"outputs\":[{\"id\":\"scene\",\"from\":{\"node\":\"save\",\"port\":\"scene\"}}]}");

        /// <summary>The <c>unity.scene.close</c> example.</summary>
        internal static readonly JsonValue CloseScene = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.scene-close\"," +
            "\"title\":\"Open a scene additively and close it again\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"open\",\"operation\":\"unity.scene.open\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/Scenes/Main.unity\",\"mode\":\"additive\"}}," +
            "{\"id\":\"close\",\"operation\":\"unity.scene.close\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"open\"}," +
            "{\"from\":\"open\",\"to\":\"close\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"open\",\"port\":\"scene\"}," +
            "\"to\":{\"node\":\"close\",\"port\":\"scene\"}}]," +
            "\"outputs\":[{\"id\":\"scene\",\"from\":{\"node\":\"open\",\"port\":\"scene\"}}]}");

        private static JsonValue Parse(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "A Scene Operation example workflow did not parse: " + parsed.Diagnostics[0].Code);
            }
            return parsed.Value;
        }
    }
}
