#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.Operations
{
    /// <summary>
    /// The example workflow documents of the §G7 Asset Operations
    /// (SPEC §C1 item 4, §H0 item 1): each one is a complete WF document —
    /// schemaVersion, the single <c>core.start</c> node, the node with every
    /// required input, the control edges that reach it and the workflow
    /// <c>outputs</c> — that the static Validator accepts with no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <c>unity.asset.import</c> declares a <c>fixtureManifest</c>:
    /// static validation judges the document against the Registry and the
    /// type snapshot only, and none of the other examples needs an asset to
    /// pre-exist — the folder is what the example creates, a search over a
    /// folder that holds nothing simply returns nothing, the move/copy/
    /// delete examples take their Ref input from the material an earlier
    /// step creates, and saving assets takes the Editor's own dirty state
    /// as its input. The <see cref="OperationExample.FixtureManifest"/>
    /// slot stays for the examples that do need project content.
    /// </para>
    /// <para>
    /// The constant paths are inside the default <c>allowedPaths</c>
    /// (<c>Assets/</c>) so the §D6 constant-path check stays clean; the
    /// <c>save_all</c> example chains the folder step in front of it because
    /// the operation only means something once something has been changed.
    /// </para>
    /// </remarks>
    internal static class AssetOperationExamples
    {
        /// <summary>The <c>unity.asset.create_folder</c> example.</summary>
        internal static readonly JsonValue CreateFolder = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-create-folder\"," +
            "\"title\":\"Create the folder a later step writes reports into\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"reports\",\"operation\":\"unity.asset.create_folder\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeave/Reports\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"reports\"}]," +
            "\"outputs\":[{\"id\":\"path\",\"from\":{\"node\":\"reports\",\"port\":\"path\"}}]}");

        /// <summary>The <c>unity.asset.find</c> example.</summary>
        internal static readonly JsonValue Find = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-find\"," +
            "\"title\":\"List the materials under Assets/Materials\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"materials\",\"operation\":\"unity.asset.find\",\"version\":1," +
            "\"parameters\":{\"filter\":\"t:Material\",\"folders\":[\"Assets/Materials\"]}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"materials\"}]," +
            "\"outputs\":[{\"id\":\"assets\",\"from\":{\"node\":\"materials\",\"port\":\"assets\"}}]}");

        /// <summary>The <c>unity.asset.create_material</c> example.</summary>
        internal static readonly JsonValue CreateMaterial = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-create-material\"," +
            "\"title\":\"Create a red material for later assignment\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"mat\",\"operation\":\"unity.asset.create_material\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeave/Materials/Probe.mat\"," +
            "\"shader\":\"Standard\",\"properties\":{\"_Color\":{\"r\":1,\"g\":0.2,\"b\":0.2,\"a\":1},\"_Glossiness\":0.5}}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"mat\"}]," +
            "\"outputs\":[{\"id\":\"material\",\"from\":{\"node\":\"mat\",\"port\":\"material\"}}]}");

        /// <summary>The <c>unity.asset.create_scriptable_object</c> example.</summary>
        internal static readonly JsonValue CreateScriptableObject = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-create-scriptable-object\"," +
            "\"title\":\"Create a ScriptableObject data asset\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"data\",\"operation\":\"unity.asset.create_scriptable_object\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeave/Data/Probe.asset\"," +
            "\"type\":\"UnityEngine.ScriptableObject\",\"values\":{}}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"data\"}]," +
            "\"outputs\":[{\"id\":\"asset\",\"from\":{\"node\":\"data\",\"port\":\"asset\"}}]}");

        /// <summary>
        /// The on-disk fixture the <c>unity.asset.import</c> Example declares
        /// (SPEC §I.3 "ファイルがディスクにあること"): the file the workflow
        /// names must exist for the example to run, so the manifest is
        /// declared and the descriptor test writes it into the project before
        /// validating. The member names are the local, documented shape — the
        /// SPEC fixes only that the manifest describes the fixture.
        /// </summary>
        internal const string ImportFixturePath = "Assets/OpWeaveExamples/Import/Probe.txt";

        /// <summary>The <c>unity.asset.import</c> Example's fixture manifest.</summary>
        internal static readonly JsonValue ImportFixtureManifest = Parse(
            "{\"files\":[{\"path\":\"" + ImportFixturePath + "\"," +
            "\"text\":\"OpWeave import example fixture.\\n\"}]}");

        /// <summary>The <c>unity.asset.import</c> example.</summary>
        internal static readonly JsonValue Import = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-import\"," +
            "\"title\":\"Import a dropped report file as an asset\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"import\",\"operation\":\"unity.asset.import\",\"version\":1," +
            "\"parameters\":{\"path\":\"" + ImportFixturePath + "\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"import\"}]," +
            "\"outputs\":[{\"id\":\"asset\",\"from\":{\"node\":\"import\",\"port\":\"asset\"}}]}");

        /// <summary>
        /// The <c>unity.asset.move</c> example: create a material, then
        /// rename it — a move the <c>asset</c> Ref takes from the earlier
        /// step's output. The constant destination stays inside the default
        /// <c>allowedPaths</c> so the §D6 constant-path check stays clean.
        /// </summary>
        internal static readonly JsonValue Move = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-move\"," +
            "\"title\":\"Rename the created material\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"mat\",\"operation\":\"unity.asset.create_material\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeave/Materials/Probe.mat\"," +
            "\"shader\":\"Standard\"}}," +
            "{\"id\":\"renamed\",\"operation\":\"unity.asset.move\",\"version\":1," +
            "\"parameters\":{\"destination\":\"Assets/OpWeave/Materials/ProbeRenamed.mat\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"mat\"}," +
            "{\"from\":\"mat\",\"to\":\"renamed\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"mat\",\"port\":\"material\"}," +
            "\"to\":{\"node\":\"renamed\",\"port\":\"asset\"}}]," +
            "\"outputs\":[{\"id\":\"asset\",\"from\":{\"node\":\"renamed\",\"port\":\"asset\"}}]}");

        /// <summary>
        /// The <c>unity.asset.copy</c> example: duplicate the material the
        /// earlier step created at a free destination path.
        /// </summary>
        internal static readonly JsonValue Copy = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-copy\"," +
            "\"title\":\"Copy the created material as a variant\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"mat\",\"operation\":\"unity.asset.create_material\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeave/Materials/Probe.mat\"," +
            "\"shader\":\"Standard\"}}," +
            "{\"id\":\"variant\",\"operation\":\"unity.asset.copy\",\"version\":1," +
            "\"parameters\":{\"destination\":\"Assets/OpWeave/Materials/ProbeCopy.mat\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"mat\"}," +
            "{\"from\":\"mat\",\"to\":\"variant\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"mat\",\"port\":\"material\"}," +
            "\"to\":{\"node\":\"variant\",\"port\":\"asset\"}}]," +
            "\"outputs\":[{\"id\":\"asset\",\"from\":{\"node\":\"variant\",\"port\":\"asset\"}}]}");

        /// <summary>
        /// The <c>unity.asset.delete</c> example: remove the material the
        /// earlier step created — the default <c>trash</c> mode applies, so
        /// the example needs no <c>mode</c> parameter.
        /// </summary>
        internal static readonly JsonValue Delete = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-delete\"," +
            "\"title\":\"Delete the material created by an earlier step\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"mat\",\"operation\":\"unity.asset.create_material\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeave/Materials/Probe.mat\"," +
            "\"shader\":\"Standard\"}}," +
            "{\"id\":\"gone\",\"operation\":\"unity.asset.delete\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"mat\"}," +
            "{\"from\":\"mat\",\"to\":\"gone\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"mat\",\"port\":\"material\"}," +
            "\"to\":{\"node\":\"gone\",\"port\":\"asset\"}}]}");

        /// <summary>The <c>unity.asset.save_all</c> example.</summary>
        internal static readonly JsonValue SaveAll = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.asset-save-all\"," +
            "\"title\":\"Create the reports folder, then flush pending asset changes\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"reports\",\"operation\":\"unity.asset.create_folder\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeave/Reports\"}}," +
            "{\"id\":\"flush\",\"operation\":\"unity.asset.save_all\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"reports\"}," +
            "{\"from\":\"reports\",\"to\":\"flush\"}]," +
            "\"outputs\":[{\"id\":\"path\",\"from\":{\"node\":\"reports\",\"port\":\"path\"}}]}");

        private static JsonValue Parse(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "An Asset Operation example workflow did not parse: " + parsed.Diagnostics[0].Code);
            }
            return parsed.Value;
        }
    }
}
