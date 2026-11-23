#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OpWeave.Viewer
{
    /// <summary>
    /// The SPEC §H5 Run Viewer (<c>Window &gt; OpWeave &gt; Runs</c>): the
    /// human-facing main screen, UI Toolkit on every supported Unity
    /// version (6000.0+), with no Graph Toolkit dependency. Its Runs tab —
    /// the window's only tab until the Registry tab of T-W3.5-03 — lists
    /// the Run Store's Runs on the left and, on the right, the selected
    /// Run's steps (state, duration, attempts), logs and changes; a change
    /// row pings its asset or selects its GameObject, a running Run offers
    /// Cancel, and every pane refreshes on the SPEC §E8 events without
    /// reopening the window.
    /// </summary>
    /// <remarks>
    /// <para><b>Data path.</b> The window never invents state: the run list
    /// and the detail pane are read from the Run Store and the Run's
    /// journal through <see cref="RunViewerSource"/>, and Cancel goes
    /// through the coordinator's journal writer — no state is written from
    /// the UI.</para>
    /// <para><b>Updates.</b> The §E8 subscriptions live in OnEnable and die
    /// with the domain like every subscriber's (SPEC §E8 — the window is
    /// recreated on reload and re-subscribes); each event refreshes the
    /// affected rows immediately. A running step's duration additionally
    /// ticks on a 500 ms schedule from the journaled attempt start.</para>
    /// <para><b>Rows.</b> The lists are generated rows rather than
    /// virtualized ListViews: the run list is bounded by SPEC §F8's
    /// retention, and generated rows bind without a layout pass — the
    /// deterministic content EditMode fixtures assert on.</para>
    /// <para><b>Panels.</b> The state-driven action area carries the
    /// SPEC §H5 item 1 panels: the approval panel (the §H4 item 3
    /// detail plus Approve/Reject) while the Run is AwaitingApproval and
    /// the review panel (the journaled reason plus Retry/Abort) while it
    /// is NeedsReview — each visible only in the state that allows its
    /// actions. A new AwaitingApproval opens and notifies through
    /// <see cref="RunViewerApprovalNotifier"/> (SPEC §H5 item 3).</para>
    /// </remarks>
    public sealed class RunViewerWindow : EditorWindow
    {
        // Element names the [Edit] fixtures query and the rows carry.
        internal const string RunRowsName = "run-rows";
        internal const string RunRowName = "run-row";
        internal const string DetailScrollName = "run-detail";
        internal const string EmptyLabelName = "detail-empty";
        internal const string StatusLabelName = "status-message";
        internal const string JournalWarningName = "journal-warning";
        internal const string RunHeaderName = "run-header";
        internal const string RunIdLabelName = "detail-run-id";
        internal const string RunStateLabelName = "detail-run-state";
        internal const string RunWorkflowLabelName = "detail-workflow";
        internal const string RunTimesLabelName = "detail-times";
        internal const string CancelButtonName = "cancel-button";
        internal const string StepRowsName = "step-rows";
        internal const string LogRowsName = "log-rows";
        internal const string ChangeRowsName = "change-rows";
        internal const string StepTitleName = "step-title";
        internal const string StepStateName = "step-state";
        internal const string StepAttemptName = "step-attempt";
        internal const string StepDurationName = "step-duration";
        internal const string LogTextName = "log-text";
        internal const string ChangeTextName = "change-text";
        internal const string RunRowTitleName = "run-title";
        internal const string RunRowSubtitleName = "run-subtitle";
        internal const string RunsEmptyName = "runs-empty";
        internal const string ApprovalPanelName = "approval-panel";
        internal const string ApprovalRowsName = "approval-rows";
        internal const string ApprovalTitleName = "approval-title";
        internal const string ApprovalPlanHashName = "approval-planhash";
        internal const string ApprovalDeadlineName = "approval-deadline";
        internal const string ApprovalNodeRowName = "approval-node-row";
        internal const string ApprovalNodeTitleName = "approval-node-title";
        internal const string ApprovalNodeLineName = "approval-node-line";
        internal const string ApprovalUsedTitleName = "approval-used-title";
        internal const string ApprovalUsedRowName = "approval-used-row";
        internal const string ApprovalUsedLineName = "approval-used-line";
        internal const string RejectReasonName = "reject-reason";
        internal const string ApproveButtonName = "approve-button";
        internal const string RejectButtonName = "reject-button";
        internal const string ReviewPanelName = "review-panel";
        internal const string ReviewTitleName = "review-title";
        internal const string ReviewReasonName = "review-reason";
        internal const string ReviewRetryButtonName = "review-retry-button";
        internal const string ReviewAbortButtonName = "review-abort-button";

        // The tab bar of SPEC §H5: the Runs pane plus the Registry pane
        // T-W3.5-03 adds (the pane element names live on RegistryViewerTab).
        internal const string TabBarName = "tab-bar";
        internal const string RunsTabButtonName = "runs-tab";
        internal const string RegistryTabButtonName = "registry-tab";
        internal const string RunsPageName = "runs-page";

        // The selected run row's marker class — display-side selection
        // feedback, read by the fixtures as the selection proof.
        internal const string SelectedRunRowClass = "run-row-selected";

        // How often a live step's duration re-renders between §E8 events.
        private const long LiveDurationIntervalMs = 500;

        private RunViewerSource? _source;
        private RegistryViewerSource? _registrySource;
        private bool _subscribed;
        private string? _selectedRunId;
        private readonly List<RunIndexEntry> _runs = new List<RunIndexEntry>();
        private RunViewerDetail? _detail;

        private VisualElement _runRows = null!;
        private Label _runsEmptyLabel = null!;
        private ScrollView _detailScroll = null!;
        private Label _emptyLabel = null!;
        private Label _statusLabel = null!;
        private Label _journalWarning = null!;
        private VisualElement _header = null!;
        private Label _runIdLabel = null!;
        private Label _runStateLabel = null!;
        private Label _runWorkflowLabel = null!;
        private Label _runTimesLabel = null!;
        private VisualElement _actions = null!;
        private Button _cancelButton = null!;
        private Label _stepsTitle = null!;
        private VisualElement _stepRows = null!;
        private Label _logsTitle = null!;
        private VisualElement _logRows = null!;
        private Label _changesTitle = null!;
        private VisualElement _changeRows = null!;
        private VisualElement _approvalPanel = null!;
        private VisualElement _approvalRows = null!;
        private TextField _rejectReason = null!;
        private VisualElement _reviewPanel = null!;
        private Label _reviewReason = null!;

        // Created lazily on first use — OnEnable/BuildView or a fixture
        // entry point — never in a field initializer: the tab's
        // constructor creates UI Toolkit elements and a ScriptableObject
        // constructor forbids VisualElement creation.
        private RegistryViewerTab? _registryTab;

        private RegistryViewerTab RegistryTab => _registryTab ??= new RegistryViewerTab();
        private VisualElement _runsPage = null!;
        private Button _runsTabButton = null!;
        private Button _registryTabButton = null!;

        /// <summary>The <c>Window &gt; OpWeave &gt; Runs</c> menu entry of SPEC §H5.</summary>
        [MenuItem("Window/OpWeave/Runs")]
        public static void Open()
        {
            var window = GetWindow<RunViewerWindow>();
            window.titleContent = new GUIContent("OpWeave Runs");
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("OpWeave Runs");
            BuildView();
            EnsureSource();
            EnsureRegistrySource();
            RefreshRuns();
            rootVisualElement.schedule
                .Execute(UpdateLiveDurations)
                .Every(LiveDurationIntervalMs);
        }

        private void OnDisable() => Unsubscribe();

        /// <summary>
        /// Binds the window to <paramref name="source"/> — the production
        /// host is the default; EditMode fixtures bind their own runner
        /// host's read model here. A null source shows the empty state.
        /// </summary>
        internal void BindSource(RunViewerSource? source)
        {
            Unsubscribe();
            _source = source;
            Subscribe();
            RefreshRuns();
        }

        /// <summary>The selected Run's id, or null — the fixture's selection read.</summary>
        internal string? SelectedRunId => _selectedRunId;

        /// <summary>
        /// Binds the Registry tab's read model (SPEC §H5 item 2) — the
        /// shared publication plus the retained build by default; EditMode
        /// fixtures bind an isolated publication and scratch setup root. A
        /// null source leaves the pane showing its empty state.
        /// </summary>
        internal void BindRegistrySource(RegistryViewerSource? source)
        {
            _registrySource = source;
            RegistryTab.Bind(source);
        }

        /// <summary>The Registry pane's repaint — the §E8 refresh fixtures drive.</summary>
        internal void RefreshRegistry() => RegistryTab.Refresh();

        /// <summary>The Runs page — the default view.</summary>
        internal void SelectRunsTab()
        {
            _runsPage.style.display = DisplayStyle.Flex;
            RegistryTab.Pane.style.display = DisplayStyle.None;
            _runsTabButton.SetEnabled(false);
            _registryTabButton.SetEnabled(true);
        }

        /// <summary>The Registry page — refreshed on selection and §E8 events.</summary>
        internal void SelectRegistryTab()
        {
            _runsPage.style.display = DisplayStyle.None;
            RegistryTab.Pane.style.display = DisplayStyle.Flex;
            _runsTabButton.SetEnabled(true);
            _registryTabButton.SetEnabled(false);
            RegistryTab.Refresh();
        }

        /// <summary>The Registry origin filter's test seam.</summary>
        internal void SetRegistryOriginFilter(string? value) =>
            RegistryTab.SetOriginFilter(value);

        /// <summary>The custom-source open path fixtures drive directly.</summary>
        internal void ActivateRegistrySource(RegisteredOperation operation) =>
            RegistryTab.ActivateSource(operation);

        /// <summary>The Setup Project button's test seam.</summary>
        internal void InvokeRegistrySetup() => RegistryTab.InvokeSetup();

        // The default registry source: the shared publication, the retained
        // build and the domain's §E8 dispatcher.
        private void EnsureRegistrySource()
        {
            if (_registrySource is null)
            {
                _registrySource = RegistryViewerSource.Production;
            }
            RegistryTab.Bind(_registrySource);
        }

        // The default source: the domain's installed production runner host
        // (null until OpWeaveRunnerHost.InstallProduction ran — the window
        // then shows the empty state rather than stale data).
        private void EnsureSource()
        {
            if (_source is null)
            {
                _source = RunViewerSource.ForHost(OpWeaveRunnerHost.Current);
            }
            Subscribe();
        }

        private void Subscribe()
        {
            if (_source is null || _subscribed)
            {
                return;
            }
            _subscribed = true;
            _source.Events.RunStateChanged += OnRunEvent;
            _source.Events.StepStateChanged += OnStepEvent;
            _source.Events.LogAppended += OnLogEvent;
            _source.ApprovalPending += OnApprovalPending;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _source is null)
            {
                return;
            }
            _subscribed = false;
            _source.Events.RunStateChanged -= OnRunEvent;
            _source.Events.StepStateChanged -= OnStepEvent;
            _source.Events.LogAppended -= OnLogEvent;
            _source.ApprovalPending -= OnApprovalPending;
        }

        // SPEC §E8: every event lands on the main thread after its journal
        // write, so the panes re-read immediately — no reopening, no
        // polling. A Run-state record can add, drop or re-state a list
        // entry; step and log records only touch the open detail.
        private void OnRunEvent(RunStateChanged _) => RefreshRuns();

        private void OnStepEvent(StepStateChanged _) => RefreshDetail();

        private void OnLogEvent(RunLogEntry _) => RefreshDetail();

        // SPEC §H5 item 3, second half: a Run that becomes
        // AwaitingApproval while this window is already open notifies
        // too — the notifier opens the window when none is.
        private void OnApprovalPending(RunApprovalDetail detail) =>
            NotifyApprovalPending(detail);

        // The whole view construction — OnEnable's path and the EditMode
        // fixtures' alike: a ScriptableObject.CreateInstance'd window
        // carries a detached rootVisualElement (no graphics view is ever
        // initialized), so the fixtures build and query the same tree
        // headlessly.
        internal void BuildView()
        {
            var root = rootVisualElement;
            root.Clear();

            // The SPEC §H5 tab bar: Runs (the pre-existing pane) and the
            // Registry pane of T-W3.5-03 — one visible at a time.
            var tabBar = new VisualElement { name = TabBarName };
            tabBar.style.flexDirection = FlexDirection.Row;
            _runsTabButton = new Button(SelectRunsTab)
            {
                name = RunsTabButtonName,
                text = "Runs",
            };
            _registryTabButton = new Button(SelectRegistryTab)
            {
                name = RegistryTabButtonName,
                text = "Registry",
            };
            tabBar.Add(_runsTabButton);
            tabBar.Add(_registryTabButton);
            root.Add(tabBar);

            _runsPage = new VisualElement { name = RunsPageName };
            _runsPage.style.flexGrow = 1f;
            root.Add(_runsPage);

            var registryPane = RegistryTab.Pane;
            registryPane.style.flexGrow = 1f;
            root.Add(registryPane);
            SelectRunsTab();

            var split = new TwoPaneSplitView(
                0, 260f, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;
            _runsPage.Add(split);

            var left = new VisualElement { name = "runs-pane" };
            left.style.flexGrow = 1f;
            var title = new Label("Runs");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            left.Add(title);
            _runsEmptyLabel = new Label("No Runs.") { name = RunsEmptyName };
            left.Add(_runsEmptyLabel);
            var runsScroll = new ScrollView { name = "runs-scroll" };
            runsScroll.style.flexGrow = 1f;
            _runRows = new VisualElement { name = RunRowsName };
            runsScroll.Add(_runRows);
            left.Add(runsScroll);
            split.Add(left);

            _detailScroll = new ScrollView { name = DetailScrollName };
            _detailScroll.style.flexGrow = 1f;
            split.Add(_detailScroll);

            _statusLabel = new Label { name = StatusLabelName };
            _detailScroll.Add(_statusLabel);
            _journalWarning = new Label { name = JournalWarningName };
            _detailScroll.Add(_journalWarning);
            _emptyLabel = new Label("No Run selected.") { name = EmptyLabelName };
            _detailScroll.Add(_emptyLabel);

            _header = new VisualElement { name = RunHeaderName };
            _runIdLabel = new Label { name = RunIdLabelName };
            _runIdLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _runStateLabel = new Label { name = RunStateLabelName };
            _runWorkflowLabel = new Label { name = RunWorkflowLabelName };
            _runTimesLabel = new Label { name = RunTimesLabelName };
            _header.Add(_runIdLabel);
            _header.Add(_runStateLabel);
            _header.Add(_runWorkflowLabel);
            _header.Add(_runTimesLabel);
            _detailScroll.Add(_header);

            // The state-driven action area: the approval and review
            // panels of SPEC §H5 item 1 and Cancel for a running Run —
            // each shown only in the Run state that allows its actions.
            _actions = new VisualElement { name = "run-actions" };

            _approvalPanel = new VisualElement { name = ApprovalPanelName };
            _approvalRows = new VisualElement { name = ApprovalRowsName };
            _approvalPanel.Add(_approvalRows);
            _rejectReason = new TextField("Rejection reason")
            {
                name = RejectReasonName,
            };
            _approvalPanel.Add(_rejectReason);
            var approvalButtons = new VisualElement();
            approvalButtons.style.flexDirection = FlexDirection.Row;
            approvalButtons.Add(new Button(OnApproveClicked)
            {
                name = ApproveButtonName,
                text = "Approve",
            });
            approvalButtons.Add(new Button(OnRejectClicked)
            {
                name = RejectButtonName,
                text = "Reject",
            });
            _approvalPanel.Add(approvalButtons);
            _actions.Add(_approvalPanel);

            _reviewPanel = new VisualElement { name = ReviewPanelName };
            var reviewTitle = new Label("Needs review")
            {
                name = ReviewTitleName,
            };
            reviewTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _reviewPanel.Add(reviewTitle);
            _reviewReason = new Label { name = ReviewReasonName };
            _reviewReason.style.whiteSpace = WhiteSpace.Normal;
            _reviewPanel.Add(_reviewReason);
            var reviewButtons = new VisualElement();
            reviewButtons.style.flexDirection = FlexDirection.Row;
            reviewButtons.Add(new Button(OnReviewRetryClicked)
            {
                name = ReviewRetryButtonName,
                text = "Retry this step",
            });
            reviewButtons.Add(new Button(OnReviewAbortClicked)
            {
                name = ReviewAbortButtonName,
                text = "Abort",
            });
            _reviewPanel.Add(reviewButtons);
            _actions.Add(_reviewPanel);

            _cancelButton = new Button(OnCancelClicked)
            {
                name = CancelButtonName,
                text = "Cancel",
            };
            _actions.Add(_cancelButton);
            _detailScroll.Add(_actions);

            _stepsTitle = new Label("Steps");
            _stepsTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _detailScroll.Add(_stepsTitle);
            _stepRows = new VisualElement { name = StepRowsName };
            _detailScroll.Add(_stepRows);

            _logsTitle = new Label("Logs");
            _logsTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _detailScroll.Add(_logsTitle);
            _logRows = new VisualElement { name = LogRowsName };
            _detailScroll.Add(_logRows);

            _changesTitle = new Label("Changes");
            _changesTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _detailScroll.Add(_changesTitle);
            _changeRows = new VisualElement { name = ChangeRowsName };
            _detailScroll.Add(_changeRows);
        }

        // The full two-pane refresh: the store's entries replace the list,
        // the selected Run is kept by id — never by position — and the
        // detail re-reads from the same journal truth. With no selection
        // the newest-listed Run is picked, so the detail never opens blank
        // while Runs exist.
        internal void RefreshRuns()
        {
            _runs.Clear();
            if (_source is not null)
            {
                _runs.AddRange(_source.ListRuns());
            }
            if (_selectedRunId is null ||
                _runs.FindIndex(entry => entry.RunId == _selectedRunId) < 0)
            {
                _selectedRunId = _runs.Count > 0 ? _runs[0].RunId : null;
            }
            BuildRunRows();
            RefreshDetail();
        }

        private void BuildRunRows()
        {
            _runRows.Clear();
            _runsEmptyLabel.style.display =
                _runs.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var entry in _runs)
            {
                var row = new VisualElement { name = RunRowName };
                row.userData = entry;
                row.EnableInClassList(
                    SelectedRunRowClass, entry.RunId == _selectedRunId);
                row.Add(new Label
                {
                    name = RunRowTitleName,
                    text = entry.RunId,
                });
                row.Add(new Label
                {
                    name = RunRowSubtitleName,
                    text = FormatState(entry.State) + " · " + entry.WorkflowId +
                        " · " + entry.UpdatedAt,
                });
                var captured = entry;
                row.RegisterCallback<ClickEvent>(_ => SelectRun(captured));
                _runRows.Add(row);
            }
        }

        private void SelectRun(RunIndexEntry entry)
        {
            if (entry.RunId == _selectedRunId)
            {
                return;
            }
            _selectedRunId = entry.RunId;
            foreach (var row in _runRows.Children())
            {
                if (row.userData is RunIndexEntry candidate)
                {
                    row.EnableInClassList(
                        SelectedRunRowClass, candidate.RunId == _selectedRunId);
                }
            }
            RefreshDetail();
        }

        internal void RefreshDetail()
        {
            _detail = _source is not null && _selectedRunId is { } runId
                ? _source.DetailOf(runId)
                : null;
            var has = _detail is not null;
            _emptyLabel.style.display = has ? DisplayStyle.None : DisplayStyle.Flex;
            _header.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            _actions.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            _stepsTitle.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            _stepRows.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            _logsTitle.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            _logRows.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            _changesTitle.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            _changeRows.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (!has)
            {
                _stepRows.Clear();
                _logRows.Clear();
                _changeRows.Clear();
                _approvalPanel.style.display = DisplayStyle.None;
                _reviewPanel.style.display = DisplayStyle.None;
                _journalWarning.text = "";
                return;
            }
            var detail = _detail!;
            _runIdLabel.text = detail.RunId;
            _runStateLabel.text = FormatState(detail.State) +
                (detail.CancelPending ? " (cancel requested)" : "");
            var workflowId = detail.WorkflowId;
            if (workflowId is null)
            {
                var entryIndex = _runs.FindIndex(
                    entry => entry.RunId == detail.RunId);
                workflowId = entryIndex >= 0 ? _runs[entryIndex].WorkflowId : null;
            }
            _runWorkflowLabel.text = workflowId ?? "<unreadable plan>";
            _runTimesLabel.text =
                "created " + (detail.CreatedAt ?? "?") +
                " · updated " + (detail.UpdatedAt ?? "?");
            _cancelButton.style.display =
                detail.CanCancel ? DisplayStyle.Flex : DisplayStyle.None;
            _journalWarning.text = detail.JournalWarning is { } warning
                ? "The Run journal could not be read in full: " + warning
                : "";
            BuildApprovalPanel(detail);
            BuildReviewPanel(detail);
            BuildStepRows(detail);
            BuildLogRows(detail);
            BuildChangeRows(detail);
        }

        // The SPEC §H4 item 3 approval panel, offered only while the Run
        // is AwaitingApproval — every other state hides it, so a decision
        // can never be clicked for a Run that does not await one. The
        // content is the service's detail verbatim: the bound planHash,
        // the decision deadline, each ask node's effects and literal
        // detail, and the Run's non-standard Operations with sources.
        private void BuildApprovalPanel(RunViewerDetail detail)
        {
            var approval = detail.State == RunState.AwaitingApproval
                ? detail.Approval
                : null;
            _approvalPanel.style.display =
                approval is null ? DisplayStyle.None : DisplayStyle.Flex;
            _approvalRows.Clear();
            if (approval is null)
            {
                return;
            }
            var title = new Label("Approval required")
            {
                name = ApprovalTitleName,
            };
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _approvalRows.Add(title);
            _approvalRows.Add(new Label
            {
                name = ApprovalPlanHashName,
                text = "plan " + approval.PlanHash,
            });
            var deadline = new Label
            {
                name = ApprovalDeadlineName,
                text = approval.DeadlineUtc is { } at
                    ? "decide by " + FormatInstant(at)
                    : "no decision deadline recorded",
            };
            _approvalRows.Add(deadline);
            foreach (var node in approval.Nodes)
            {
                _approvalRows.Add(ApprovalNodeRow(node));
            }
            if (approval.UsedOperations.Count == 0)
            {
                return;
            }
            var usedTitle = new Label("Custom and external Operations in this Run")
            {
                name = ApprovalUsedTitleName,
            };
            usedTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _approvalRows.Add(usedTitle);
            foreach (var used in approval.UsedOperations)
            {
                _approvalRows.Add(ApprovalUsedRow(used));
            }
        }

        // One ask node's §H4 item 3 detail: its identity and source, the
        // effects it declares and the ones it asks for, then every input
        // the human reads — constant ports verbatim (paths, full code,
        // package ids), bound ports with their resolved values.
        private static VisualElement ApprovalNodeRow(ApprovalNodeDetail node)
        {
            var row = new VisualElement { name = ApprovalNodeRowName };
            var title = new Label
            {
                name = ApprovalNodeTitleName,
                text = "node " + node.NodeId + " · " + node.OperationId +
                    " v" + node.Version.ToString(CultureInfo.InvariantCulture) +
                    DescribeSource(
                        node.Origin, node.AssemblyName, node.SourceFile,
                        node.PackageId, node.ResolvedCommit),
            };
            title.style.whiteSpace = WhiteSpace.Normal;
            row.Add(title);
            row.Add(NodeLine("asks " + string.Join(", ", node.ApprovalEffects)));
            row.Add(NodeLine("effects " + string.Join(", ", node.Effects)));
            foreach (var input in node.ConstantInputs)
            {
                row.Add(NodeLine(input.Key + ": " + ValueText(input.Value)));
            }
            foreach (var input in node.BoundInputs)
            {
                var resolved =
                    node.ResolvedInputs.TryGetValue(input.Key, out var value) &&
                    value.IsPresent
                        ? ValueText(value.Value!)
                        : "(missing)";
                row.Add(NodeLine(
                    input.Key + ": " + input.Value + " = " + resolved));
            }
            return row;
        }

        // One used non-standard Operation row — the node's identity plus
        // its registration source (SPEC §H4 item 3).
        private static VisualElement ApprovalUsedRow(UsedOperationDetail used)
        {
            var row = new VisualElement { name = ApprovalUsedRowName };
            var line = new Label
            {
                name = ApprovalUsedLineName,
                text = "node " + used.NodeId + " · " + used.OperationId +
                    " v" + used.Version.ToString(CultureInfo.InvariantCulture) +
                    DescribeSource(
                        used.Origin, used.AssemblyName, used.SourceFile,
                        used.PackageId, used.ResolvedCommit),
            };
            line.style.whiteSpace = WhiteSpace.Normal;
            row.Add(line);
            return row;
        }

        private static Label NodeLine(string text)
        {
            var line = new Label
            {
                name = ApprovalNodeLineName,
                text = text,
            };
            line.style.whiteSpace = WhiteSpace.Normal;
            return line;
        }

        // The SPEC §H5 item 1 review panel, offered only while the Run is
        // NeedsReview: the reason of the cause that put the Run in this
        // state — the Run Store's integrity diagnostic or the journal's
        // step_needs_review record — then "retry this step" and Abort, the
        // two §E2 human resolutions.
        private void BuildReviewPanel(RunViewerDetail detail)
        {
            var show = detail.State == RunState.NeedsReview;
            _reviewPanel.style.display =
                show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show)
            {
                return;
            }
            _reviewReason.text = ReviewReasonText(detail.Review);
        }

        // The panel's "理由": the cause that put the Run in its current
        // NeedsReview state, named by its published code and fixed message
        // (SPEC §F6, §J) — "The Run Store" for the load-time integrity
        // diagnostic, the step and node for a journaled step_needs_review.
        // A NeedsReview neither source can explain is reported as unknown
        // rather than blamed on the journal.
        internal static string ReviewReasonText(RunViewerReview? review)
        {
            if (review is null)
            {
                return "The reason this Run needs review is unknown.";
            }
            var who = review.Source == RunViewerReviewSource.StoreIntegrity
                ? "The Run Store"
                : review.StepIndex is { } index
                    ? "Step " + index.ToString(CultureInfo.InvariantCulture) +
                        (review.NodeId is { } node ? " (" + node + ")" : "")
                    : "A step";
            return who + " needs review: " +
                (review.Code is { } code ? code + " " : "") +
                (review.Message ?? "<no message>");
        }

        private void BuildStepRows(RunViewerDetail detail)
        {
            _stepRows.Clear();
            if (detail.Steps.Count == 0)
            {
                _stepRows.Add(new Label("(no steps recorded)"));
                return;
            }
            foreach (var step in detail.Steps)
            {
                var row = new VisualElement
                {
                    name = "step-" + step.Index.ToString(CultureInfo.InvariantCulture),
                };
                row.style.flexDirection = FlexDirection.Row;
                row.userData = step;
                var title = new Label
                {
                    name = StepTitleName,
                    text = step.Index.ToString(CultureInfo.InvariantCulture) +
                        " · " + (step.NodeId ?? "?") +
                        (step.OperationId is { } op ? " · " + op : ""),
                };
                var state = new Label
                {
                    name = StepStateName,
                    text = FormatState(step.State),
                };
                var attempt = new Label
                {
                    name = StepAttemptName,
                    text = step.Attempt is { } a
                        ? "attempt " + a.ToString(CultureInfo.InvariantCulture)
                        : "",
                };
                var duration = new Label
                {
                    name = StepDurationName,
                    text = FormatDuration(step.Duration),
                };
                row.Add(title);
                row.Add(state);
                row.Add(attempt);
                row.Add(duration);
                _stepRows.Add(row);
            }
        }

        private void BuildLogRows(RunViewerDetail detail)
        {
            _logRows.Clear();
            if (detail.Logs.Count == 0)
            {
                _logRows.Add(new Label("(no log entries)"));
                return;
            }
            foreach (var log in detail.Logs)
            {
                var row = new VisualElement();
                row.Add(new Label
                {
                    name = LogTextName,
                    text = log.Ts + " " + log.Level +
                        (log.Step is { } step
                            ? " step " + step.ToString(CultureInfo.InvariantCulture)
                            : "") +
                        (log.Code is { } code ? " " + code : "") +
                        " " + log.Message,
                });
                _logRows.Add(row);
            }
        }

        private void BuildChangeRows(RunViewerDetail detail)
        {
            _changeRows.Clear();
            if (detail.Changes.Count == 0)
            {
                _changeRows.Add(new Label("(no changes recorded)"));
                return;
            }
            foreach (var change in detail.Changes)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.userData = change;
                row.Add(new Label
                {
                    name = ChangeTextName,
                    text = FormatAction(change.Action) + " " +
                        change.Target.Kind + " " + change.Target.Path +
                        " (step " + change.Step.ToString(CultureInfo.InvariantCulture) +
                        ", attempt " +
                        change.Attempt.ToString(CultureInfo.InvariantCulture) +
                        (change.Undoable ? ", undoable" : "") + ")",
                });
                var captured = change;
                // SPEC §H5 item 1: a click pings the asset or selects the
                // GameObject — the row resolves through the change
                // activation helper, never through the runner.
                row.RegisterCallback<ClickEvent>(
                    _ => ActivateChange(captured));
                _changeRows.Add(row);
            }
        }

        // The change row's ClickEvent handler — internal so a headless
        // fixture (no panel to dispatch events through) invokes the same
        // activation the click does.
        internal void ActivateChange(ChangeRecord change)
        {
            var resolved = RunChangeActivation.Activate(change.Target);
            _statusLabel.text = resolved is null
                ? "The change target could not be resolved: " + change.Target.Path
                : "";
        }

        // The Cancel button's clicked action — internal so a headless
        // fixture invokes the same request the button press does.
        internal void OnCancelClicked()
        {
            if (_source is null || _selectedRunId is null)
            {
                return;
            }
            try
            {
                var result = _source.Cancel(_selectedRunId);
                _statusLabel.text = result.Ok
                    ? ""
                    : "Cancel could not be requested: " +
                        DescribeDiagnostics(result.Diagnostics);
            }
            catch (Exception exception)
            {
                // A UI callback must never throw into the Editor event loop:
                // the refusal is shown instead.
                _statusLabel.text =
                    "Cancel could not be requested: " + exception.Message;
            }
            RefreshRuns();
        }

        // The approval panel's Approve — internal so a headless fixture
        // invokes the same request the button press does. The decision is
        // bound to the planHash the panel presented (SPEC §H4 item 3) and
        // goes through the human approval service — never a journal write
        // from the UI. The AwaitingApproval guard mirrors the panel's
        // visibility gate.
        internal void OnApproveClicked()
        {
            var approval = _detail?.Approval;
            if (_source is null || _selectedRunId is null ||
                _detail?.State != RunState.AwaitingApproval || approval is null)
            {
                return;
            }
            try
            {
                var result = _source.Approve(_selectedRunId, approval.PlanHash);
                _statusLabel.text = result.Ok
                    ? ""
                    : "The approval could not be recorded: " +
                        DescribeDiagnostics(result.Diagnostics);
            }
            catch (Exception exception)
            {
                // A UI callback must never throw into the Editor event loop.
                _statusLabel.text =
                    "The approval could not be recorded: " + exception.Message;
            }
            RefreshRuns();
        }

        // The approval panel's Reject — the same service path, ending the
        // Run Cancelled with OW-PERM-003 (SPEC §H4 item 3).
        internal void OnRejectClicked()
        {
            var approval = _detail?.Approval;
            if (_source is null || _selectedRunId is null ||
                _detail?.State != RunState.AwaitingApproval || approval is null)
            {
                return;
            }
            try
            {
                var result = _source.Reject(
                    _selectedRunId, approval.PlanHash, _rejectReason.value);
                _statusLabel.text = result.Ok
                    ? ""
                    : "The rejection could not be recorded: " +
                        DescribeDiagnostics(result.Diagnostics);
            }
            catch (Exception exception)
            {
                _statusLabel.text =
                    "The rejection could not be recorded: " + exception.Message;
            }
            RefreshRuns();
        }

        // The review panel's "retry this step" — internal so a headless
        // fixture invokes the same request the button press does:
        // review_resolved journals the NeedsReview step's retry and the
        // step re-runs as the next attempt (SPEC §E2).
        internal void OnReviewRetryClicked() =>
            ResolveReview(ReviewDecision.RetryStep);

        // The review panel's Abort — review_resolved ends the Run
        // Cancelled (SPEC §E2).
        internal void OnReviewAbortClicked() =>
            ResolveReview(ReviewDecision.Abort);

        private void ResolveReview(ReviewDecision decision)
        {
            if (_source is null || _selectedRunId is null ||
                _detail?.State != RunState.NeedsReview)
            {
                return;
            }
            try
            {
                var result = _source.ResolveReview(_selectedRunId, decision);
                _statusLabel.text = result.Ok
                    ? ""
                    : "The review could not be resolved: " +
                        DescribeDiagnostics(result.Diagnostics);
            }
            catch (Exception exception)
            {
                _statusLabel.text =
                    "The review could not be resolved: " + exception.Message;
            }
            RefreshRuns();
        }

        // SPEC §H5 item 3: an approval that becomes pending notifies —
        // the Run is selected so its approval panel is the open detail and
        // the Editor notification is posted. The notifier opens or
        // focuses the window before calling this, so a closed window is
        // already on screen by the time the notification lands.
        internal void NotifyApprovalPending(RunApprovalDetail detail)
        {
            if (_selectedRunId != detail.RunId)
            {
                _selectedRunId = detail.RunId;
                RefreshRuns();
            }
            _statusLabel.text =
                "Run " + detail.RunId + " awaits an approval decision.";
            if (rootVisualElement.panel is not null)
            {
                ShowNotification(new GUIContent(
                    "Run " + detail.RunId + " awaits approval"));
            }
        }

        // The 500 ms tick of a still-accruing duration: only the live
        // rows' duration labels re-render, from the journaled attempt
        // start — no file is re-read for a number that only grows.
        internal void UpdateLiveDurations()
        {
            if (_detail is null || _source is null)
            {
                return;
            }
            var now = _source.Now;
            foreach (var row in _stepRows.Children())
            {
                if (row.userData is not RunViewerStepRow step ||
                    !step.Live || step.LiveSinceUtc is not { } since)
                {
                    continue;
                }
                var label = row.Q<Label>(StepDurationName);
                if (label is not null)
                {
                    label.text = FormatDuration(now - since);
                }
            }
        }

        private static string FormatInstant(DateTimeOffset instant) =>
            instant.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        // A used Operation's registration source, appended to its
        // identity line (SPEC §H4 item 3): origin, defining assembly,
        // source file, and package id@commit — the members that are
        // recorded, in that order; an unresolvable Operation shows none.
        private static string DescribeSource(
            OperationOrigin? origin,
            string? assemblyName,
            string? sourceFile,
            string? packageId,
            string? resolvedCommit)
        {
            var parts = new List<string>();
            if (origin is { } value)
            {
                parts.Add(JsonNaming.ToSnakeCase(value.ToString()));
            }
            if (assemblyName is not null)
            {
                parts.Add(assemblyName);
            }
            if (sourceFile is not null)
            {
                parts.Add(sourceFile);
            }
            if (packageId is not null)
            {
                parts.Add(resolvedCommit is null
                    ? packageId
                    : packageId + "@" + resolvedCommit);
            }
            return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
        }

        // One input value's display text (SPEC §H4 item 3): a string is
        // shown verbatim — the path or the full code text itself — any
        // other shape as its canonical JSON.
        private static string ValueText(JsonValue value)
        {
            var token = value.ToToken();
            return token is JValue { Type: JTokenType.String, Value: string text }
                ? text
                : value.CanonicalText;
        }

        private static string FormatState(RunState state) =>
            JsonNaming.ToSnakeCase(state.ToString());

        private static string FormatState(StepState state) =>
            JsonNaming.ToSnakeCase(state.ToString());

        private static string FormatAction(ChangeAction action) =>
            JsonNaming.ToSnakeCase(action.ToString());

        private static string FormatDuration(TimeSpan? duration)
        {
            if (duration is not { } span)
            {
                return "—";
            }
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }
            if (span.TotalSeconds < 60)
            {
                return span.TotalSeconds.ToString(
                    "0.0", CultureInfo.InvariantCulture) + "s";
            }
            if (span.TotalHours < 1)
            {
                return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) +
                    "m " + span.Seconds.ToString("00", CultureInfo.InvariantCulture) + "s";
            }
            return ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) +
                "h " + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        }

        private static string DescribeDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var parts = new string[diagnostics.Count];
            for (var i = 0; i < diagnostics.Count; i++)
            {
                parts[i] = diagnostics[i].Code + " " + diagnostics[i].Message;
            }
            return string.Join("; ", parts);
        }
    }
}
