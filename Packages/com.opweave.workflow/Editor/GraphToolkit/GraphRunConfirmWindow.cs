#nullable enable

using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The SPEC §H4 item 4 pre-start confirmation panel of a human GUI
    /// Run: one modal window listing EVERY ask effect the compiled Plan
    /// carries — each node's asked effects alongside its other declared
    /// effects, the literal and bound inputs the approval covers (the
    /// paths, code and package identifiers of SPEC §H4 item 3's detail
    /// surface) and the custom/external Operations the Run would use —
    /// and approves them together. Choosing "Run" approves; "Decline" —
    /// or closing the window without choosing — declines, which the
    /// action reports as OW-PERM-003 with no Run started.
    /// </summary>
    /// <remarks>
    /// The detail is the same <see cref="RunApprovalDetail"/> the Run
    /// Viewer's approval screen renders (SPEC §H4 item 3), built from the
    /// Plan before any Run exists: the panel is the human-run sibling of
    /// the Agent approval screen, deciding before the accept rather than
    /// inside an AwaitingApproval Run. The modal blocks only in an
    /// interactive Editor; a batch domain never reaches it — the action
    /// declines there (the NonInteractiveApprovalGate would refuse the
    /// accept anyway, SPEC §H4).
    /// </remarks>
    internal sealed class GraphRunConfirmWindow : EditorWindow
    {
        /// <summary>The answer the modal leaves.</summary>
        internal enum Choice
        {
            /// <summary>The window closed before a choice — treated as declined.</summary>
            Pending = 0,

            /// <summary>The human approved every ask effect together — the Run starts.</summary>
            Run = 1,

            /// <summary>The human declined — no Run starts; the action surfaces OW-PERM-003.</summary>
            Decline = 2,
        }

        private RunApprovalDetail _detail = null!;
        private Vector2 _scroll;

        /// <summary>The answer the panel left — <see cref="Choice.Decline"/> when the window closed unanswered.</summary>
        internal Choice Decision { get; private set; } = Choice.Pending;

        /// <summary>
        /// Shows the confirmation panel modally and returns the human's
        /// answer; <see cref="EditorWindow.ShowModal"/> blocks until the
        /// window closes.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="detail"/> is null.</exception>
        internal static Choice ShowModal(RunApprovalDetail detail)
        {
            if (detail is null)
            {
                throw new System.ArgumentNullException(nameof(detail));
            }
            var window = CreateInstance<GraphRunConfirmWindow>();
            window._detail = detail;
            window.titleContent = new GUIContent("OpWeave: Confirm Run");
            window.minSize = new Vector2(480f, 360f);
            window.ShowModal();
            return window.Decision;
        }

        private void OnGUI()
        {
            var nodes = _detail.Nodes;
            EditorGUILayout.LabelField(
                "This workflow asks approval for the side effects below.",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Plan", _detail.PlanHash);
            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                EditorGUILayout.LabelField(
                    node.NodeId + " — " + node.OperationId + " v" + node.Version,
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "  asks: " + string.Join(", ", node.ApprovalEffects));
                if (node.ApprovalEffects.Count != node.Effects.Count)
                {
                    EditorGUILayout.LabelField(
                        "  all effects: " + string.Join(", ", node.Effects));
                }
                foreach (var input in node.ConstantInputs)
                {
                    EditorGUILayout.LabelField(
                        "  " + input.Key + " = " +
                        input.Value.ToToken().ToString());
                }
                foreach (var input in node.BoundInputs)
                {
                    EditorGUILayout.LabelField(
                        "  " + input.Key + " = " + input.Value);
                }
                var source = node.SourceFile ?? node.PackageId ?? node.AssemblyName;
                if (source is not null)
                {
                    EditorGUILayout.LabelField("  source: " + source);
                }
            }
            var used = _detail.UsedOperations;
            if (used.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(
                    "Custom/external operations this Run uses:",
                    EditorStyles.boldLabel);
                for (var i = 0; i < used.Count; i++)
                {
                    var operation = used[i];
                    var source = operation.SourceFile ??
                        operation.PackageId ?? operation.AssemblyName ?? "unresolved";
                    EditorGUILayout.LabelField(
                        "  " + operation.NodeId + " — " + operation.OperationId +
                        " v" + operation.Version + " (" + source + ")");
                }
            }
            EditorGUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Run"))
            {
                Decision = Choice.Run;
                Close();
            }
            if (GUILayout.Button("Decline"))
            {
                Decision = Choice.Decline;
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void OnDestroy()
        {
            // A window closed unanswered declines: a Run must never start
            // on a panel the human did not approve (SPEC §H4 item 4).
            if (Decision == Choice.Pending)
            {
                Decision = Choice.Decline;
            }
        }
    }
}
