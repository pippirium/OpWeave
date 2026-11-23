#nullable enable

using System;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Operations
{
    /// <summary>
    /// The complete example workflow documents of the three SPEC §I.2
    /// Prefab-creating Operations (SPEC §C1 item 4): each is a full WF —
    /// schemaVersion, the single <c>core.start</c>, the required constant
    /// inputs, the control chain and declared <c>outputs</c> — that the
    /// canonical static Validator accepts with ok:true and no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two Examples whose constant <c>unity.PrefabAssetRef</c> names a
    /// real asset declare the same <see cref="FixtureManifest"/> the §G6
    /// Prefab-content Examples use — one probe Prefab under
    /// <c>Assets/OpWeaveExamples/</c> with a pinned guid, applied by the
    /// test project before validation. The create_from_gameobject Example
    /// creates its own artifact and needs no fixture. The destination
    /// paths sit inside the default <c>allowedPaths</c> (<c>Assets/</c>)
    /// so the §D6 constant-path check stays clean.
    /// </para>
    /// </remarks>
    internal static class PrefabCreateOperationExamples
    {
        /// <summary>The fixture Prefab path the manifest pins.</summary>
        internal const string FixturePath = "Assets/OpWeaveExamples/Hud.prefab";

        /// <summary>The fixture Prefab guid the constant Refs carry.</summary>
        internal const string FixtureGuid = "9f4a1c73e2b84d56a0c3b7e51d826f04";

        /// <summary>The fixture the PrefabRef-carrying Examples declare —
        /// the same manifest shape the Prefab-content Examples publish
        /// (SPEC §C1 OperationExample.fixtureManifest).</summary>
        internal static JsonValue FixtureManifest { get; } = Parse(
            "{\"prefabs\":[{" +
            "\"path\":\"" + FixturePath + "\"," +
            "\"guid\":\"" + FixtureGuid + "\"," +
            "\"root\":\"Hud\"}]}");

        /// <summary>The <c>unity.prefab.create_from_gameobject</c> example:
        /// find a GameObject by name, save it as a connected Prefab and
        /// publish both outputs.</summary>
        internal static readonly JsonValue CreateFromGameObject = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.prefab-create-from-gameobject\"," +
            "\"title\":\"Turn a found GameObject into a connected Prefab\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"find\",\"operation\":\"unity.gameobject.find\",\"version\":1," +
            "\"parameters\":{\"name\":\"ProbeRoot\"}}," +
            "{\"id\":\"make\",\"operation\":\"unity.prefab.create_from_gameobject\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeaveExamples/ProbeRoot.prefab\",\"connect\":true}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"find\"}," +
            "{\"from\":\"find\",\"to\":\"make\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"find\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"make\",\"port\":\"source\"}}]," +
            "\"outputs\":[{\"id\":\"prefab\",\"from\":{\"node\":\"make\",\"port\":\"prefab\"}}," +
            "{\"id\":\"instance\",\"from\":{\"node\":\"make\",\"port\":\"instance\"}}]}");

        /// <summary>The <c>unity.prefab.instantiate</c> example: place an
        /// instance of the fixture Prefab at a position.</summary>
        internal static readonly JsonValue Instantiate = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.prefab-instantiate\"," +
            "\"title\":\"Instantiate the HUD Prefab at a position\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"place\",\"operation\":\"unity.prefab.instantiate\",\"version\":1," +
            "\"parameters\":{\"prefab\":{\"kind\":\"prefabAsset\"," +
            "\"guid\":\"" + FixtureGuid + "\"," +
            "\"path\":\"" + FixturePath + "\"}," +
            "\"position\":{\"x\":1,\"y\":0,\"z\":2}}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"place\"}]," +
            "\"outputs\":[{\"id\":\"instance\",\"from\":{\"node\":\"place\",\"port\":\"instance\"}}]}");

        /// <summary>The <c>unity.prefab.create_variant</c> example: derive a
        /// Variant of the fixture Prefab at a new path.</summary>
        internal static readonly JsonValue CreateVariant = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.prefab-create-variant\"," +
            "\"title\":\"Create a Variant of the HUD Prefab\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"make\",\"operation\":\"unity.prefab.create_variant\",\"version\":1," +
            "\"parameters\":{\"base\":{\"kind\":\"prefabAsset\"," +
            "\"guid\":\"" + FixtureGuid + "\"," +
            "\"path\":\"" + FixturePath + "\"}," +
            "\"path\":\"Assets/OpWeaveExamples/HudVariant.prefab\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"make\"}]," +
            "\"outputs\":[{\"id\":\"variant\",\"from\":{\"node\":\"make\",\"port\":\"variant\"}}]}");

        private static JsonValue Parse(string json)
        {
            var parsed = JsonValue.Parse(json);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "A Prefab-create Example workflow JSON did not parse.");
            }
            return parsed.Value;
        }
    }
}
