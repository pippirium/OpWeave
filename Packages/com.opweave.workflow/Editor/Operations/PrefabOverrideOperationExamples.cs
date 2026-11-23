#nullable enable

using System;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Operations
{
    /// <summary>
    /// The complete example workflow documents of the three SPEC §I.2
    /// Prefab-override Operations (SPEC §C1 item 4): each is a full WF —
    /// schemaVersion, the single <c>core.start</c>, the required constant
    /// inputs, the control chain and declared <c>outputs</c> — that the
    /// canonical static Validator accepts with ok:true and no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every Example instantiates the fixture Prefab first, so each
    /// declares <see cref="PrefabCreateOperationExamples.FixtureManifest"/>
    /// — the pinned probe Prefab under <c>Assets/OpWeaveExamples/</c> the
    /// test project applies before validation.
    /// </para>
    /// </remarks>
    internal static class PrefabOverrideOperationExamples
    {
        /// <summary>The <c>unity.prefab.apply_overrides</c> example: place an
        /// instance of the fixture Prefab, add a Component to it and apply
        /// the override into the Prefab asset — the §G6 apply workflow.</summary>
        internal static readonly JsonValue ApplyOverrides = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.prefab-apply-overrides\"," +
            "\"title\":\"Add a component to a Prefab instance and apply it to the asset\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"place\",\"operation\":\"unity.prefab.instantiate\",\"version\":1," +
            "\"parameters\":{\"prefab\":{\"kind\":\"prefabAsset\"," +
            "\"guid\":\"" + PrefabCreateOperationExamples.FixtureGuid + "\"," +
            "\"path\":\"" + PrefabCreateOperationExamples.FixturePath + "\"}}}," +
            "{\"id\":\"add\",\"operation\":\"unity.component.add\",\"version\":1," +
            "\"parameters\":{\"componentType\":\"UnityEngine.Animator\"}}," +
            "{\"id\":\"apply\",\"operation\":\"unity.prefab.apply_overrides\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"place\"}," +
            "{\"from\":\"place\",\"to\":\"add\"}," +
            "{\"from\":\"add\",\"to\":\"apply\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"place\",\"port\":\"instance\"}," +
            "\"to\":{\"node\":\"add\",\"port\":\"target\"}}," +
            "{\"from\":{\"node\":\"place\",\"port\":\"instance\"}," +
            "\"to\":{\"node\":\"apply\",\"port\":\"instance\"}}]," +
            "\"outputs\":[{\"id\":\"prefab\",\"from\":{\"node\":\"apply\",\"port\":\"prefab\"}}]}");

        /// <summary>The <c>unity.prefab.revert_overrides</c> example: place
        /// an instance of the fixture Prefab and revert its overrides.</summary>
        internal static readonly JsonValue RevertOverrides = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.prefab-revert-overrides\"," +
            "\"title\":\"Instantiate a Prefab and revert the instance's overrides\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"place\",\"operation\":\"unity.prefab.instantiate\",\"version\":1," +
            "\"parameters\":{\"prefab\":{\"kind\":\"prefabAsset\"," +
            "\"guid\":\"" + PrefabCreateOperationExamples.FixtureGuid + "\"," +
            "\"path\":\"" + PrefabCreateOperationExamples.FixturePath + "\"}}}," +
            "{\"id\":\"revert\",\"operation\":\"unity.prefab.revert_overrides\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"place\"}," +
            "{\"from\":\"place\",\"to\":\"revert\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"place\",\"port\":\"instance\"}," +
            "\"to\":{\"node\":\"revert\",\"port\":\"instance\"}}]," +
            "\"outputs\":[{\"id\":\"instance\",\"from\":{\"node\":\"revert\",\"port\":\"instance\"}}]}");

        /// <summary>The <c>unity.prefab.unpack</c> example: place an
        /// instance of the fixture Prefab and unpack it completely.</summary>
        internal static readonly JsonValue Unpack = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.prefab-unpack\"," +
            "\"title\":\"Instantiate a Prefab and unpack the instance completely\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"place\",\"operation\":\"unity.prefab.instantiate\",\"version\":1," +
            "\"parameters\":{\"prefab\":{\"kind\":\"prefabAsset\"," +
            "\"guid\":\"" + PrefabCreateOperationExamples.FixtureGuid + "\"," +
            "\"path\":\"" + PrefabCreateOperationExamples.FixturePath + "\"}}}," +
            "{\"id\":\"unpack\",\"operation\":\"unity.prefab.unpack\",\"version\":1," +
            "\"parameters\":{\"mode\":\"completely\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"place\"}," +
            "{\"from\":\"place\",\"to\":\"unpack\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"place\",\"port\":\"instance\"}," +
            "\"to\":{\"node\":\"unpack\",\"port\":\"instance\"}}]," +
            "\"outputs\":[{\"id\":\"gameObject\",\"from\":{\"node\":\"unpack\",\"port\":\"gameObject\"}}]}");

        private static JsonValue Parse(string json)
        {
            var parsed = JsonValue.Parse(json);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "A Prefab-override Example workflow JSON did not parse.");
            }
            return parsed.Value;
        }
    }
}
