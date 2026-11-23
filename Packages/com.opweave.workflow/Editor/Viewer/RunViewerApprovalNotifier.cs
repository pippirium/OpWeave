#nullable enable

using System;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.Viewer
{
    /// <summary>
    /// The window-opening half of SPEC §H5 item 3 ("承認待ちが発生したら
    /// ウィンドウを開き、通知を出す"): when the approval service reports a
    /// newly AwaitingApproval Run, this subscriber opens (or focuses) the
    /// Run Viewer so the human's decision screen is in front of them, then
    /// hands the detail to the window, which selects the Run and posts the
    /// Editor notification. A window already open receives the same event
    /// through its own <see cref="RunViewerSource.ApprovalPending"/>
    /// subscription, so the notification is surfaced in both cases.
    /// </summary>
    /// <remarks>
    /// The subscription is deferred to
    /// <see cref="EditorApplication.delayCall"/>: <c>[InitializeOnLoad]</c>
    /// order between the adapter bootstrap — which installs the production
    /// runner host — and this class is unspecified, while delayCall fires
    /// after every domain-load hook ran. The subscription lives and dies
    /// with the domain like every §E8 subscriber's. A host that failed to
    /// install accepts no Runs, so no approval can ever be pending for it —
    /// the missing subscription loses nothing.
    /// </remarks>
    [InitializeOnLoad]
    internal static class RunViewerApprovalNotifier
    {
        static RunViewerApprovalNotifier()
        {
            EditorApplication.delayCall += Subscribe;
        }

        private static void Subscribe()
        {
            var approvals = OpWeaveRunnerHost.Current?.Approvals;
            if (approvals is not null)
            {
                approvals.ApprovalPending += OnApprovalPending;
            }
        }

        // The production callback: open the window through the same entry
        // Window > OpWeave > Runs uses, then let it select the Run and post
        // the notification.
        private static void OnApprovalPending(RunApprovalDetail detail) =>
            NotifyApproval(detail, OpenWindow);

        private static RunViewerWindow OpenWindow()
        {
            var window = EditorWindow.GetWindow<RunViewerWindow>();
            window.titleContent = new UnityEngine.GUIContent("OpWeave Runs");
            window.Show();
            return window;
        }

        /// <summary>
        /// Opens the window <paramref name="openWindow"/> provides and
        /// notifies it of the pending approval — the seam EditMode fixtures
        /// exercise without a real Editor window loop.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        internal static void NotifyApproval(
            RunApprovalDetail detail, Func<RunViewerWindow?> openWindow)
        {
            if (detail is null)
            {
                throw new ArgumentNullException(nameof(detail));
            }
            if (openWindow is null)
            {
                throw new ArgumentNullException(nameof(openWindow));
            }
            var window = openWindow();
            if (window is not null)
            {
                window.NotifyApprovalPending(detail);
            }
        }
    }
}
