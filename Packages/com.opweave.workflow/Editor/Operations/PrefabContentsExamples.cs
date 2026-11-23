#nullable enable

using System;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Operations
{
    /// <summary>
    /// The complete example workflow both SPEC §G6 Prefab-content Operations
    /// publish (SPEC §C1 item 4): a full WF document — schemaVersion, one Start,
    /// the constant <c>unity.PrefabAssetRef</c> the open node needs and the data
    /// edge feeding its editing root to the save node — that the canonical
    /// Validator accepts as written. One document, two titles: the pair is the
    /// workflow each row describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document is the fixed Canonical JSON text of the <see cref="const"/>s
    /// below, read through <see cref="JsonValue.Parse(string)"/> — the strict
    /// reader of the single Canonical JSON implementation, which is how the
    /// standard Operations in this assembly take JSON in (the
    /// <c>OpWeave.Operations</c> assembly deliberately holds no Newtonsoft.Json
    /// reference, SPEC §0.5). Its shape is asserted by
    /// <c>local:T-W2.6-03:descriptors-examples</c>, so a text that no longer
    /// parses or no longer validates fails the ticket's own Example test rather
    /// than reaching a Registry build.
    /// </para>
    /// </remarks>
    internal static class PrefabContentsExamples
    {
        /// <summary>
        /// The fixture the Example declares. The static Validator never
        /// dereferences an asset reference, but the Example names a real fixture
        /// path so the document reads as a workflow one could actually run; the
        /// §C1 test creates this Prefab before validating it.
        /// </summary>
        internal const string FixturePath = "Assets/OpWeaveExamples/Hud.prefab";

        /// <summary>The guid of <see cref="FixturePath"/> as the Example pins it.</summary>
        internal const string FixtureGuid = "9f4a1c73e2b84d56a0c3b7e51d826f04";

        /// <summary>The root <see cref="FixturePath"/> is saved with.</summary>
        internal const string FixtureRoot = "Hud";

        // The fixture the Example needs (SPEC §C1 OperationExample's optional
        // fixtureManifest; §C8 returns it verbatim so a consumer can prepare
        // what the workflow names). The member names are the local, documented
        // shape — the SPEC fixes only that the manifest describes the fixture.
        // Both documents are parsed before the Examples below are built: static
        // initializers run in declaration order, so the parsed texts are
        // declared ahead of the properties that hand them out.
        private static JsonValue FixtureManifest { get; } = Parse(
            "{\"prefabs\":[{" +
            "\"path\":\"" + FixturePath + "\"," +
            "\"guid\":\"" + FixtureGuid + "\"," +
            "\"root\":\"" + FixtureRoot + "\"}]}");

        private static JsonValue Workflow { get; } = Parse(
            "{\"schemaVersion\":\"1.0\"," +
            "\"id\":\"example.prefab-edit-contents\"," +
            "\"title\":\"Edit a Prefab's contents and save them\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"open\",\"operation\":\"unity.prefab.open_contents\",\"version\":1," +
            "\"parameters\":{\"prefab\":{\"kind\":\"prefabAsset\"," +
            "\"guid\":\"" + FixtureGuid + "\"," +
            "\"path\":\"" + FixturePath + "\"}}}," +
            "{\"id\":\"save\",\"operation\":\"unity.prefab.save_contents\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"open\"}," +
            "{\"from\":\"open\",\"to\":\"save\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"open\",\"port\":\"root\"}," +
            "\"to\":{\"node\":\"save\",\"port\":\"root\"}}]," +
            "\"outputs\":[{\"id\":\"prefab\"," +
            "\"from\":{\"node\":\"save\",\"port\":\"prefab\"}}]}");

        /// <summary>The Example of <c>unity.prefab.open_contents</c>.</summary>
        internal static OperationExample OpenContents { get; } =
            new OperationExample(
                "Open a Prefab's contents and save them back",
                Workflow,
                FixtureManifest);

        /// <summary>The Example of <c>unity.prefab.save_contents</c>.</summary>
        internal static OperationExample SaveContents { get; } =
            new OperationExample(
                "Save the opened Prefab contents to their asset",
                Workflow,
                FixtureManifest);

        private static JsonValue Parse(string json)
        {
            var parsed = JsonValue.Parse(json);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "The Prefab-contents Example workflow JSON did not parse.");
            }
            return parsed.Value;
        }
    }
}
