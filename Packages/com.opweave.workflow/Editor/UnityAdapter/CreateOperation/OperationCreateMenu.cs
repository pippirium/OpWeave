#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using OpWeave.Core;
using OpWeave.Core.Operations;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §M.4 menu entry <c>Assets > Create > OpWeave > Operation</c>
    /// and its dialog. The dialog collects the §M.4 item 1 inputs, validates
    /// them live through <see cref="OperationCreatePlanner"/> and writes the
    /// generated files through <see cref="OperationCreateWriter"/>.
    /// </summary>
    internal static class OperationCreateMenu
    {
        /// <summary>The Unity menu path of SPEC §M.4 item 1.</summary>
        internal const string MenuPath = "Assets/Create/OpWeave/Operation";

        [MenuItem(MenuPath)]
        private static void Open()
        {
            var window = EditorWindow.GetWindow<OperationCreateWindow>(
                utility: true, title: "Create OpWeave Operation");
            window.minSize = new Vector2(460f, 540f);
            window.Initialize(ResolveSelectedFolder()
                ?? OperationCreatePlanner.DefaultFolder());
            window.Show();
        }

        /// <summary>
        /// The folder selected in the Project window (SPEC §M.4 item 1), or
        /// null when the selection is empty or outside the project — the
        /// caller then falls back to <c>customOperationFolders</c>[0].
        /// </summary>
        internal static string? ResolveSelectedFolder()
        {
            var selected = Selection.activeObject;
            if (selected is null)
            {
                return null;
            }
            var path = AssetDatabase.GetAssetPath(selected);
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith("Assets", StringComparison.Ordinal))
            {
                return null;
            }
            if (AssetDatabase.IsValidFolder(path))
            {
                return path;
            }
            var parent = Path.GetDirectoryName(path);
            return parent is null
                ? null
                : OperationCreatePlanner.NormalizeFolder(parent);
        }
    }

    /// <summary>
    /// The human-facing create dialog of SPEC §M.4 item 1. State is plain
    /// <see cref="OperationCreateRequest"/> fields re-planned on every
    /// repaint, so every validation finding is live.
    /// </summary>
    internal sealed class OperationCreateWindow : EditorWindow
    {
        private readonly OperationCreateRequest _request = new OperationCreateRequest();
        private OperationCreatePlan? _plan;
        private OperationCreateWriter.Result? _lastResult;
        private Vector2 _scroll;
        private string[] _typeIds = Array.Empty<string>();

        internal void Initialize(string targetFolder)
        {
            _request.TargetFolder = targetFolder;
            RefreshTypeIds();
        }

        private void OnFocus() => RefreshTypeIds();

        // The Type ID candidates come from the published TypeRegistry — the
        // same data list_types exposes — never from the Agent API.
        private void RefreshTypeIds()
        {
            var snapshot = RegistryPublication.Shared.Snapshot;
            if (snapshot is null)
            {
                return;
            }
            var types = snapshot.Types.All;
            var ids = new List<string>(types.Count);
            for (var i = 0; i < types.Count; i++)
            {
                ids.Add(types[i].Id);
            }
            _typeIds = ids.ToArray();
        }

        private void OnGUI()
        {
            _plan = OperationCreatePlanner.Plan(_request);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawInputs();
            DrawIssues();
            DrawActions();
            EditorGUILayout.EndScrollView();
        }

        private void DrawInputs()
        {
            // SPEC §M.4 item 1: no ID input. Before commit the dialog shows
            // the fixed "assigned on create" placeholder; the UUID is
            // minted once at commit and shown read-only below the actions.
            EditorGUILayout.LabelField("Operation ID", "assigned on create");
            _request.DisplayName = EditorGUILayout.TextField(
                "English name", _request.DisplayName);
            _request.Summary = EditorGUILayout.TextField(
                "English summary", _request.Summary);
            _request.DisplayNameJa = EditorGUILayout.TextField(
                "Japanese name", _request.DisplayNameJa);
            _request.SummaryJa = EditorGUILayout.TextField(
                "Japanese summary", _request.SummaryJa);
            _request.Category = EditorGUILayout.TextField(
                "Category", _request.Category);
            _request.Mode = (ExecutionMode)EditorGUILayout.EnumPopup(
                "Execution mode", _request.Mode);
            _request.Idempotency = (Idempotency)EditorGUILayout.EnumPopup(
                "Idempotency", _request.Idempotency);
            _request.CreateTests = EditorGUILayout.Toggle(
                "Create tests", _request.CreateTests);
            EditorGUILayout.LabelField("Target folder", _request.TargetFolder);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Effects", EditorStyles.boldLabel);
            for (var i = 0; i < EffectNames.All.Count; i++)
            {
                var effect = EffectNames.All[i];
                var selected = _request.Effects.Contains(effect);
                var next = EditorGUILayout.ToggleLeft(effect, selected);
                if (next != selected)
                {
                    if (next) { _request.Effects.Add(effect); }
                    else { _request.Effects.Remove(effect); }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Ports", EditorStyles.boldLabel);
            for (var i = 0; i < _request.Ports.Count; i++)
            {
                DrawPort(_request.Ports[i]);
            }
            if (GUILayout.Button("Add port"))
            {
                _request.Ports.Add(new OperationCreatePort());
            }
        }

        private void DrawPort(OperationCreatePort port)
        {
            EditorGUILayout.BeginHorizontal();
            port.IsOutput = GUILayout.Toggle(port.IsOutput, "out", GUILayout.Width(36f));
            port.Name = EditorGUILayout.TextField(port.Name);
            if (_typeIds.Length == 0)
            {
                port.TypeId = EditorGUILayout.TextField(port.TypeId);
            }
            else
            {
                var index = Array.IndexOf(_typeIds, port.TypeId);
                var next = EditorGUILayout.Popup(
                    index < 0 ? 0 : index, _typeIds, GUILayout.MinWidth(120f));
                port.TypeId = _typeIds[Mathf.Clamp(next, 0, _typeIds.Length - 1)];
            }
            if (GUILayout.Button("-", GUILayout.Width(20f)))
            {
                _request.Ports.Remove(port);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawIssues()
        {
            var plan = _plan;
            if (plan is null || plan.Issues.Count == 0)
            {
                return;
            }
            EditorGUILayout.Space();
            for (var i = 0; i < plan.Issues.Count; i++)
            {
                var issue = plan.Issues[i];
                var kind = issue.Severity == Severity.Error
                    ? MessageType.Error
                    : issue.Severity == Severity.Warning
                        ? MessageType.Warning
                        : MessageType.Info;
                var text = issue.Code is null
                    ? issue.Message
                    : issue.Code + ": " + issue.Message;
                EditorGUILayout.HelpBox(text, kind);
            }
        }

        private void DrawActions()
        {
            var plan = _plan;
            if (plan is null)
            {
                return;
            }
            EditorGUILayout.Space();
            if (plan.FolderChoices.Count > 0)
            {
                EditorGUILayout.LabelField(
                    "The target folder is not Editor-only (OW-UNITY-012):");
                for (var i = 0; i < plan.FolderChoices.Count; i++)
                {
                    var choice = plan.FolderChoices[i];
                    var label = choice == OperationCreateFolderChoice.CreateCustomAsmdef
                        ? "Create OpWeave.Custom.asmdef here"
                        : "Save to default folder";
                    if (GUILayout.Button(label))
                    {
                        Write(OperationCreatePlanner.PlanForChoice(_request, choice));
                    }
                }
                return;
            }
            using (new EditorGUI.DisabledScope(!plan.CanWrite))
            {
                if (GUILayout.Button("Create"))
                {
                    Write(plan);
                }
            }
            if (_lastResult is { } result)
            {
                for (var i = 0; i < result.Errors.Count; i++)
                {
                    EditorGUILayout.HelpBox(result.Errors[i], MessageType.Error);
                }
                // SPEC §M.4 item 1: after commit the generated ID is shown
                // as a value that can no longer be changed.
                if (result.OperationId is { } generatedId)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.TextField(
                            "Generated Operation ID", generatedId);
                    }
                    EditorGUILayout.HelpBox(
                        "Created: " + string.Join(", ", result.WrittenAssets),
                        MessageType.Info);
                }
            }
        }

        private void Write(OperationCreatePlan plan)
        {
            var result = OperationCreateWriter.Write(plan);
            _lastResult = result;
            if (result.Ok)
            {
                Debug.Log(
                    "OpWeave Operation created: " +
                    string.Join(", ", result.WrittenAssets));
            }
        }
    }
}
