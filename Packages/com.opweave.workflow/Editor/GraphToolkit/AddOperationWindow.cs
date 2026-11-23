#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Search;
using UnityEditor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The SPEC §H6 item 5 fallback "Operation を追加" window: the T-W5.1-02
    /// L-03 re-verification found the public Graph Toolkit API cannot
    /// register one menu entry per Registry Operation, so the custom window
    /// the SPEC names replaces the standard add-node menu. The query goes
    /// through the §C6 search — Japanese and English alike — and every
    /// result row shows its origin; a pick creates the
    /// <see cref="OperationNode"/> on the chosen live graph through
    /// <see cref="GraphOperationPicker.Add"/>.
    /// </summary>
    /// <remarks>
    /// The window is a thin IMGUI shell over <see cref="GraphOperationPicker"/>
    /// and <see cref="OpWeaveGraphTracker"/> — the same seams the EditMode
    /// fixtures drive — matching <c>OperationCreateWindow</c>'s convention.
    /// </remarks>
    internal sealed class AddOperationWindow : EditorWindow
    {
        /// <summary>The Unity menu path the window opens from.</summary>
        internal const string MenuPath = "Window/OpWeave/Add Operation";

        private static readonly string[] s_languages = { "en", "ja" };

        private string _query = "";
        private int _langIndex;
        private int _targetIndex;
        private Vector2 _scroll;
        private IReadOnlyList<OperationSearchResult> _items =
            Array.Empty<OperationSearchResult>();
        private string _status = "";
        private bool _searchDirty = true;
        private OpWeave.Core.Registry.RegistrySnapshot? _lastSnapshot;
        private OpWeaveGraph? _pinnedTarget;

        [MenuItem(MenuPath)]
        private static void OpenMenu() => Open(null);

        /// <summary>
        /// Opens the picker — optionally pinned to <paramref name="target"/>;
        /// otherwise the user picks among the graphs
        /// <see cref="OpWeaveGraphTracker"/> reports live.
        /// </summary>
        internal static AddOperationWindow Open(OpWeaveGraph? target)
        {
            var window = EditorWindow.GetWindow<AddOperationWindow>(
                true, "Add Operation");
            window.minSize = new Vector2(420f, 360f);
            window._pinnedTarget = target;
            window._langIndex =
                string.Equals(
                    GraphOperationPicker.DefaultLanguage(), "ja",
                    StringComparison.Ordinal)
                    ? 1
                    : 0;
            window.Show();
            return window;
        }

        private void OnFocus() => _searchDirty = true;

        private void OnGUI()
        {
            var target = DrawTargetRow();
            EditorGUI.BeginChangeCheck();
            _query = EditorGUILayout.TextField("Search", _query);
            _langIndex = EditorGUILayout.Popup("Language", _langIndex, s_languages);
            if (EditorGUI.EndChangeCheck())
            {
                _searchDirty = true;
            }
            RefreshSearchIfNeeded();
            DrawResults(target);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
        }

        private OpWeaveGraph? DrawTargetRow()
        {
            if (_pinnedTarget is not null)
            {
                EditorGUILayout.LabelField("Graph", _pinnedTarget.Name ?? "");
                return _pinnedTarget;
            }
            var live = OpWeaveGraphTracker.Live;
            if (live.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No OpWeave graph is loaded. Open a .opweavegraph asset first.",
                    MessageType.Info);
                return null;
            }
            _targetIndex = Mathf.Clamp(_targetIndex, 0, live.Count - 1);
            var labels = new string[live.Count];
            for (var i = 0; i < live.Count; i++)
            {
                labels[i] = live[i].Name ?? "";
            }
            _targetIndex = EditorGUILayout.Popup("Graph", _targetIndex, labels);
            return live[_targetIndex];
        }

        // The page is recomputed only when the query, the language or the
        // published snapshot changed — a repaint never re-searches. A new
        // §C3 publication swaps the snapshot instance and makes the page
        // stale; an unchanged publication is not re-searched.
        private void RefreshSearchIfNeeded()
        {
            var snapshot = GraphToolkitRegistry.SnapshotSource();
            if (!_searchDirty && ReferenceEquals(snapshot, _lastSnapshot))
            {
                return;
            }
            _lastSnapshot = snapshot;
            _searchDirty = false;
            var result = GraphOperationPicker.Search(new OperationSearchRequest
            {
                Query = _query,
                Lang = s_languages[_langIndex],
                Limit = OperationSearchService.MaxLimit,
            });
            if (!result.Ok)
            {
                _items = Array.Empty<OperationSearchResult>();
                var diagnostics = result.Diagnostics;
                _status = diagnostics.Count == 0
                    ? "Search failed."
                    : diagnostics[0].Code + ": " + diagnostics[0].Message;
                return;
            }
            _items = result.Value!.Items;
            _status = _items.Count == 0 ? "No matching Operations." : "";
        }

        private void DrawResults(OpWeaveGraph? target)
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var descriptor = item.Operation.Descriptor;
                var origin = item.Operation.Origin == OperationOrigin.Standard
                    ? "standard"
                    : "custom";
                var label = item.DisplayName.Length == 0
                    ? descriptor.Id
                    : item.DisplayName;
                using (new EditorGUI.DisabledScope(target is null))
                {
                    if (GUILayout.Button(
                        label + "  [" + origin + "]\n" +
                        descriptor.Id + " v" + descriptor.Version +
                        (item.Summary.Length == 0 ? "" : " — " + item.Summary),
                        EditorStyles.miniButton))
                    {
                        Add(target!, item);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void Add(OpWeaveGraph target, OperationSearchResult item)
        {
            var nodes = 0;
            foreach (var _ in target.GetNodes())
            {
                nodes++;
            }
            var stagger = 24f * (nodes % 8);
            var node = GraphOperationPicker.Add(
                target, item.Operation, new Vector2(stagger, stagger));
            _status = "Added " + node.operationId + " as " + node.nodeId + ".";
        }
    }
}
