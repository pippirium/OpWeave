#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.Operations
{
    /// <summary>
    /// The example workflow documents of the §I.1 Component Operations
    /// (SPEC §C1 item 4, §H0 item 1): each one is a complete WF document —
    /// schemaVersion, the single <c>core.start</c> node, the nodes with
    /// every required input, the control edges that reach them, the data
    /// edges that feed the Ref inputs and, where the Operation outputs one,
    /// the workflow <c>outputs</c> — that the static Validator accepts with
    /// no warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None declares a <c>fixtureManifest</c>: the examples create the
    /// GameObject and the component they need inside the run. The remove
    /// example's data edge also exercises the OutputRefinement of
    /// <c>unity.component.add</c> (SPEC §C1): the constant
    /// <c>componentType</c> "UnityEngine.Animator" refines the add output to
    /// <c>unity.ComponentRef&lt;unity.Animator&gt;</c>, which is
    /// §B6-assignable to the remove input's
    /// <c>unity.ComponentRef&lt;unity.Component&gt;</c>.
    /// </para>
    /// </remarks>
    internal static class ComponentOperationExamples
    {
        /// <summary>The <c>unity.component.add</c> example.</summary>
        internal static readonly JsonValue Add = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.component-add\"," +
            "\"title\":\"Add an Animator to the created GameObject\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Rig\"}}," +
            "{\"id\":\"add\",\"operation\":\"unity.component.add\",\"version\":1," +
            "\"parameters\":{\"componentType\":\"UnityEngine.Animator\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"add\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"add\",\"port\":\"target\"}}]," +
            "\"outputs\":[{\"id\":\"component\",\"from\":{\"node\":\"add\",\"port\":\"component\"}}]}");

        /// <summary>The <c>unity.component.remove</c> example.</summary>
        internal static readonly JsonValue Remove = Parse(
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.component-remove\"," +
            "\"title\":\"Remove the Animator added by an earlier step\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Rig\"}}," +
            "{\"id\":\"add\",\"operation\":\"unity.component.add\",\"version\":1," +
            "\"parameters\":{\"componentType\":\"UnityEngine.Animator\"}}," +
            "{\"id\":\"remove\",\"operation\":\"unity.component.remove\",\"version\":1}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"add\"},{\"from\":\"add\",\"to\":\"remove\"}]," +
            "\"dataEdges\":[" +
            "{\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"add\",\"port\":\"target\"}}," +
            "{\"from\":{\"node\":\"add\",\"port\":\"component\"}," +
            "\"to\":{\"node\":\"remove\",\"port\":\"component\"}}]}");

        private static JsonValue Parse(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "A Component Operation example workflow did not parse: " +
                    parsed.Diagnostics[0].Code);
            }
            return parsed.Value;
        }
    }
}
