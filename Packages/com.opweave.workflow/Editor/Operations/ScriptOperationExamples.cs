#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.Operations
{
    /// <summary>
    /// The example workflow document of <c>unity.script.create_from_template</c>
    /// (SPEC §C1 item 4, §H0 item 1): a complete WF document — schemaVersion,
    /// the single <c>core.start</c> node, the node with every required input,
    /// the control edge that reaches it and the workflow <c>outputs</c> — that
    /// the static Validator accepts with no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <c>fixtureManifest</c> is declared: the operation creates the asset
    /// it names, so no project content needs to pre-exist — the same reason
    /// the §G7 creation examples carry none. The constant folder lies inside
    /// the default <c>allowedPaths</c> (<c>Assets/</c>) so the §D6
    /// constant-path check stays clean, and no node uses the generated type,
    /// so no §G8 item 3 OW-VAL-013 warning fires.
    /// </para>
    /// </remarks>
    internal static class ScriptOperationExamples
    {
        /// <summary>
        /// The <c>unity.script.create</c> example (SPEC §I.4): a stage-3
        /// write of a complete MonoBehaviour source to a constant path
        /// inside the default <c>codePaths</c> (<c>Assets/Scripts/</c>) —
        /// and inside the default <c>allowedPaths</c> — so the constant
        /// path passes the §D6 check and no OW-VAL-013 fires (no node uses
        /// a generated type).
        /// </summary>
        internal static readonly JsonValue CreateScript = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.script-create\"," +
            "\"title\":\"Write an approved C# script under the configured code path\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"greeter\",\"operation\":\"unity.script.create\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/Scripts/Greeter.cs\"," +
            "\"content\":\"using UnityEngine;\\n\\nnamespace Game.Scripts\\n{\\n    public sealed class Greeter : MonoBehaviour\\n    {\\n    }\\n}\\n\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"greeter\"}]," +
            "\"outputs\":[{\"id\":\"script\",\"from\":{\"node\":\"greeter\",\"port\":\"script\"}}]}");

        /// <summary>
        /// The <c>unity.script.compile</c> example (SPEC §I.4, §G8 item 3):
        /// the stage-3 write followed by the explicit compile wait — the
        /// sequence a generated-type workflow always needs before a
        /// generated type may be used (SPEC §G8 item 3, D6).
        /// </summary>
        internal static readonly JsonValue CompileScript = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.script-compile\"," +
            "\"title\":\"Write an approved C# script and compile it\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"greeter\",\"operation\":\"unity.script.create\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/Scripts/Greeter.cs\"," +
            "\"content\":\"using UnityEngine;\\n\\nnamespace Game.Scripts\\n{\\n    public sealed class Greeter : MonoBehaviour\\n    {\\n    }\\n}\\n\"}}," +
            "{\"id\":\"compile\",\"operation\":\"unity.script.compile\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"greeter\"}," +
            "{\"from\":\"greeter\",\"to\":\"compile\"}]," +
            "\"outputs\":[{\"id\":\"script\",\"from\":{\"node\":\"greeter\",\"port\":\"script\"}}," +
            "{\"id\":\"result\",\"from\":{\"node\":\"compile\",\"port\":\"result\"}}]}");

        /// <summary>The <c>unity.script.create_from_template</c> example.</summary>
        internal static readonly JsonValue CreateFromTemplate = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.script-create-from-template\"," +
            "\"title\":\"Generate a MonoBehaviour script from the bundled template\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"report\",\"operation\":\"unity.script.create_from_template\",\"version\":1," +
            "\"parameters\":{\"template\":\"mono_behaviour\",\"className\":\"DailyReport\"," +
            "\"namespace\":\"Game.Reports\",\"folder\":\"Assets/Scripts\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"report\"}]," +
            "\"outputs\":[{\"id\":\"script\",\"from\":{\"node\":\"report\",\"port\":\"script\"}}," +
            "{\"id\":\"typeName\",\"from\":{\"node\":\"report\",\"port\":\"typeName\"}}]}");

        private static JsonValue Parse(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "A script Operation example workflow did not parse: " +
                    parsed.Diagnostics[0].Code);
            }
            return parsed.Value;
        }
    }
}
