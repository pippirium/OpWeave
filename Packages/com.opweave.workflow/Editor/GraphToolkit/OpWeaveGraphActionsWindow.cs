#nullable enable

using System;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The SPEC §H8 item 2 action surface — Validate, Dry Run, Run,
    /// Cancel, Export JSON, Import JSON and open Run Viewer as buttons on
    /// a dockable window. Graph Toolkit 6000.5's public API offers no
    /// toolbar extension point, and <see cref="OpWeaveGraph"/> is a
    /// managed object rather than a UnityEngine.Object a
    /// <c>[CustomEditor]</c> inspector can bind to — so this window is
    /// the documented T-W5.1-02 L-03 fallback for the toolbar/inspector
    /// placement. It acts on the selected <c>*.opweavegraph</c> asset
    /// through <see cref="GraphDatabase.LoadGraph{T}"/> — the same
    /// in-memory instance an open graph window edits — and reports the
    /// last action's <see cref="GraphActionResult"/> at the bottom.
    /// </summary>
    internal sealed class OpWeaveGraphActionsWindow : EditorWindow
    {
        /// <summary>The menu path that opens this window.</summary>
        internal const string MenuPath = "Window/OpWeave/Graph Actions";

        private GraphActionResult? _last;

        [MenuItem(MenuPath)]
        private static void ShowWindow()
        {
            var window = GetWindow<OpWeaveGraphActionsWindow>();
            window.titleContent = new GUIContent("OpWeave Graph Actions");
            window.minSize = new Vector2(300f, 260f);
            window.Show();
        }

        private void OnGUI()
        {
            var graph = SelectedGraph();
            EditorGUILayout.LabelField(
                "Graph",
                graph is null ? "(select a .opweavegraph asset)" : AssetPathOf(graph));
            var runId = graph is null ? null : GraphExecutionDisplay.RunIdOf(graph);
            if (runId is not null)
            {
                EditorGUILayout.LabelField("Run", runId!);
            }
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(graph is null))
            {
                if (GUILayout.Button("Validate"))
                {
                    Apply(GraphActions.Validate(graph!));
                }
                if (GUILayout.Button("Dry Run"))
                {
                    Apply(GraphActions.DryRun(graph!));
                }
                if (GUILayout.Button("Run"))
                {
                    Apply(GraphActions.Run(graph!));
                }
                using (new EditorGUI.DisabledScope(runId is null))
                {
                    if (GUILayout.Button("Cancel"))
                    {
                        Apply(GraphActions.Cancel(graph!));
                    }
                }
                if (GUILayout.Button("Export JSON"))
                {
                    Apply(GraphActions.ExportJson(graph!));
                }
                using (new EditorGUI.DisabledScope(
                    graph is null || string.IsNullOrEmpty(graph.jsonPath)))
                {
                    if (GUILayout.Button("Import JSON"))
                    {
                        Apply(GraphActions.ImportJson(graph!));
                    }
                }
            }
            if (GUILayout.Button("Open Run Viewer"))
            {
                Apply(GraphActions.OpenRunViewer());
            }
            if (_last is not null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    _last.Message,
                    _last.Ok ? MessageType.Info : MessageType.Warning);
            }
        }

        private void Apply(GraphActionResult result)
        {
            _last = result;
            Repaint();
        }

        private void OnSelectionChange() => Repaint();

        // The graph the actions apply to: the first selected
        // .opweavegraph asset, loaded through the same GraphDatabase
        // entry point an open graph window uses.
        private static OpWeaveGraph? SelectedGraph()
        {
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(
                    "." + OpWeaveGraph.AssetExtension,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return GraphDatabase.LoadGraph<OpWeaveGraph>(path);
                }
            }
            return null;
        }

        private static string AssetPathOf(OpWeaveGraph graph)
        {
            try
            {
                var path = GraphDatabase.GetGraphAssetPath(graph);
                return string.IsNullOrEmpty(path) ? "(unsaved graph)" : path;
            }
            catch (Exception)
            {
                return "(graph)";
            }
        }
    }
}
