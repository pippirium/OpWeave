#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEngine.UIElements;

namespace OpWeave.Viewer
{
    /// <summary>
    /// The Registry tab of <see cref="RunViewerWindow"/> (SPEC §H5 item 2): a
    /// strictly read-only view over the atomically published
    /// <see cref="RegistrySnapshot"/> and the retained
    /// <see cref="RegistryBuildResult"/> — the registered Operations with
    /// origin filtering and openable custom sources, the registration
    /// failures with their diagnostics, the registered extensions, the
    /// deliberate exclusions with their reasons, the diff since the
    /// previously persisted Registry (or its explicit unavailable state), the
    /// stale flag and the settings diagnostics, plus the one-time
    /// <c>Setup Project</c> entry of SPEC §M.3 that is shown only while the
    /// setup layout is incomplete.
    /// </summary>
    /// <remarks>
    /// The pane is a passive projection: every row is a plain
    /// <see cref="Label"/> except the Setup button, the origin filter and the
    /// Runs/Registry tab buttons the window owns. There is deliberately no
    /// control that could admit a whitelist-excluded extension — exclusion is
    /// discovery permission decided on the §C3 rule-5 seam before any
    /// candidate constructor runs, and nothing in this tab writes Registry
    /// state or evaluates permission settings.
    /// </remarks>
    internal sealed class RegistryViewerTab
    {
        internal const string PaneName = "registry-pane";
        internal const string StatusName = "registry-status";
        internal const string StaleName = "registry-stale";
        internal const string SetupPanelName = "registry-setup";
        internal const string SetupButtonName = "registry-setup-button";
        internal const string SetupStatusName = "registry-setup-status";
        internal const string SettingsRowsName = "registry-settings-rows";
        internal const string SettingsLineName = "registry-settings-line";
        internal const string OriginFilterName = "registry-origin-filter";
        internal const string OperationRowsName = "registry-operation-rows";
        internal const string OperationRowName = "registry-operation-row";
        internal const string OperationTitleName = "registry-operation-title";
        internal const string OperationDetailName = "registry-operation-detail";
        internal const string OperationOpenHintName = "registry-operation-open-hint";
        internal const string ExtensionRowsName = "registry-extension-rows";
        internal const string ExtensionRowName = "registry-extension-row";
        internal const string ExtensionLineName = "registry-extension-line";
        internal const string FailureRowsName = "registry-failure-rows";
        internal const string FailureRowName = "registry-failure-row";
        internal const string FailureTitleName = "registry-failure-title";
        internal const string FailureLineName = "registry-failure-line";
        internal const string ExclusionRowsName = "registry-exclusion-rows";
        internal const string ExclusionRowName = "registry-exclusion-row";
        internal const string ExclusionLineName = "registry-exclusion-line";
        internal const string DiffStatusName = "registry-diff-status";
        internal const string DiffRowsName = "registry-diff-rows";
        internal const string DiffRowName = "registry-diff-row";
        internal const string DiffLineName = "registry-diff-line";
        internal const string SectionTitleName = "registry-section-title";

        internal const string OriginFilterAll = "all";
        internal const string OriginFilterStandard = "standard";
        internal const string OriginFilterCustom = "custom";

        private readonly ScrollView _pane;
        private readonly Label _statusLabel;
        private readonly Label _staleLabel;
        private readonly VisualElement _setupPanel;
        private readonly Label _setupStatus;
        private readonly VisualElement _settingsRows;
        private readonly DropdownField _originFilter;
        private readonly VisualElement _operationRows;
        private readonly VisualElement _extensionRows;
        private readonly VisualElement _failureRows;
        private readonly VisualElement _exclusionRows;
        private readonly Label _diffStatus;
        private readonly VisualElement _diffRows;

        private RegistryViewerSource? _source;
        private IRunEvents? _events;
        private string _origin = OriginFilterAll;

        internal RegistryViewerTab()
        {
            _pane = new ScrollView(ScrollViewMode.Vertical) { name = PaneName };
            _statusLabel = new Label { name = StatusName };
            _staleLabel = new Label("stale — the Registry may be out of date")
            {
                name = StaleName,
            };
            _staleLabel.style.display = DisplayStyle.None;

            _setupPanel = new VisualElement { name = SetupPanelName };
            var setupButton = new Button(OnSetupClicked)
            {
                name = SetupButtonName,
                text = "Setup Project",
            };
            _setupStatus = new Label { name = SetupStatusName };
            _setupPanel.Add(setupButton);
            _setupPanel.Add(_setupStatus);
            _setupPanel.style.display = DisplayStyle.None;

            _settingsRows = new VisualElement { name = SettingsRowsName };
            _originFilter = new DropdownField(
                "Origin",
                new List<string>
                {
                    OriginFilterAll, OriginFilterStandard, OriginFilterCustom,
                },
                OriginFilterAll)
            {
                name = OriginFilterName,
            };
            _originFilter.RegisterValueChangedCallback(
                change => SetOriginFilter(change.newValue));
            _operationRows = new VisualElement { name = OperationRowsName };
            _extensionRows = new VisualElement { name = ExtensionRowsName };
            _failureRows = new VisualElement { name = FailureRowsName };
            _exclusionRows = new VisualElement { name = ExclusionRowsName };
            _diffStatus = new Label { name = DiffStatusName };
            _diffRows = new VisualElement { name = DiffRowsName };

            _pane.Add(_statusLabel);
            _pane.Add(_staleLabel);
            _pane.Add(_setupPanel);
            _pane.Add(SectionTitle("Settings"));
            _pane.Add(_settingsRows);
            _pane.Add(_originFilter);
            _pane.Add(SectionTitle("Operations"));
            _pane.Add(_operationRows);
            _pane.Add(SectionTitle("Registered extensions"));
            _pane.Add(_extensionRows);
            _pane.Add(SectionTitle("Registration failures"));
            _pane.Add(_failureRows);
            _pane.Add(SectionTitle("Exclusions"));
            _pane.Add(_exclusionRows);
            _pane.Add(SectionTitle("Changes since the previous Registry"));
            _pane.Add(_diffStatus);
            _pane.Add(_diffRows);
        }

        /// <summary>The pane element the window embeds next to the Runs page.</summary>
        internal ScrollView Pane => _pane;

        /// <summary>
        /// Binds a source and refreshes; the tab subscribes to the source's
        /// §E8 dispatcher so <c>RegistryChanged</c> — published only after the
        /// diff persisted — repaints the pane.
        /// </summary>
        internal void Bind(RegistryViewerSource? source)
        {
            if (_events is not null)
            {
                _events.RegistryChanged -= OnRegistryChanged;
                _events = null;
            }
            _source = source;
            if (source is not null)
            {
                _events = source.Events;
                if (_events is not null)
                {
                    _events.RegistryChanged += OnRegistryChanged;
                }
            }
            Refresh();
        }

        /// <summary>The same refresh the §E8 <c>RegistryChanged</c> handler runs.</summary>
        internal void Refresh()
        {
            var source = _source;
            var snapshot = source?.Snapshot;
            var build = source?.Build;
            var stale = source?.Stale ?? false;

            _statusLabel.text = snapshot is null
                ? "No Registry published yet."
                : "generation " + snapshot.GenerationId +
                    " · " + snapshot.Operations.Count + " registered operations" +
                    " · " + snapshot.RegistryHash;
            _staleLabel.style.display =
                stale ? DisplayStyle.Flex : DisplayStyle.None;

            _setupPanel.style.display =
                source is not null && !source.IsSetupComplete
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;

            BuildSettingsRows(build?.SettingsDiagnostics);
            BuildOperationRows(snapshot);
            BuildExtensionRows(build);
            BuildFailureRows(snapshot);
            BuildExclusionRows(snapshot);
            BuildDiffRows(source?.Diff);
        }

        /// <summary>
        /// The origin filter's shared entry point — the dropdown callback and
        /// tests drive the same code path.
        /// </summary>
        internal void SetOriginFilter(string? value)
        {
            var next = value is OriginFilterStandard or OriginFilterCustom
                ? value
                : OriginFilterAll;
            if (next == _origin)
            {
                return;
            }
            _origin = next;
            BuildOperationRows(_source?.Snapshot);
        }

        /// <summary>
        /// The row-click path SPEC §H5 requires for custom Operations:
        /// resolves the registered source file through the bound opener.
        /// </summary>
        internal void ActivateSource(RegisteredOperation operation)
        {
            if (_source is null || operation is null ||
                operation.Origin != OperationOrigin.Custom ||
                operation.SourceFile is null)
            {
                return;
            }
            if (!_source.OpenSource(operation.SourceFile))
            {
                _statusLabel.text =
                    "The source file could not be opened: " + operation.SourceFile;
            }
        }

        /// <summary>The Setup button's shared entry point — callback and fixtures.</summary>
        internal void InvokeSetup() => OnSetupClicked();

        private void OnSetupClicked()
        {
            if (_source is null)
            {
                return;
            }
            var result = _source.RunSetup();
            _setupStatus.text = OpWeaveProjectSetup.FormatSummary(result);
            Refresh();
        }

        private void OnRegistryChanged(IReadOnlyList<RegistryChange> changes) => Refresh();

        private void BuildSettingsRows(IReadOnlyList<Diagnostic>? diagnostics)
        {
            _settingsRows.Clear();
            if (diagnostics is null || diagnostics.Count == 0)
            {
                return;
            }
            foreach (var diagnostic in diagnostics)
            {
                _settingsRows.Add(new Label(diagnostic.Code + " " + diagnostic.Message)
                {
                    name = SettingsLineName,
                });
            }
        }

        private void BuildOperationRows(RegistrySnapshot? snapshot)
        {
            _operationRows.Clear();
            if (snapshot is null)
            {
                return;
            }
            foreach (var operation in snapshot.Operations)
            {
                if ((_origin == OriginFilterStandard &&
                        operation.Origin != OperationOrigin.Standard) ||
                    (_origin == OriginFilterCustom &&
                        operation.Origin != OperationOrigin.Custom))
                {
                    continue;
                }
                var row = new VisualElement { name = OperationRowName };
                row.Add(new Label(
                    operation.Descriptor.Id + " · v" + operation.Descriptor.Version +
                    " · " + operation.Origin.ToString().ToLowerInvariant())
                {
                    name = OperationTitleName,
                });
                row.Add(new Label(DescribeOperation(operation))
                {
                    name = OperationDetailName,
                });
                if (operation.Origin == OperationOrigin.Custom &&
                    operation.SourceFile is not null)
                {
                    var openHint = new Label("open source")
                    {
                        name = OperationOpenHintName,
                    };
                    row.Add(openHint);
                    var captured = operation;
                    row.RegisterCallback<ClickEvent>(
                        _ => ActivateSource(captured));
                }
                _operationRows.Add(row);
            }
            if (_operationRows.childCount == 0)
            {
                _operationRows.Add(new Label("(no operations registered)"));
            }
        }

        // Registered extensions: the instantiated TypeProviders that fed the
        // type snapshot plus the admitted extension packages of SPEC §X.3.
        private void BuildExtensionRows(RegistryBuildResult? build)
        {
            _extensionRows.Clear();
            if (build is null)
            {
                return;
            }
            foreach (var provider in build.Providers)
            {
                var row = new VisualElement { name = ExtensionRowName };
                row.Add(new Label(
                    "provider " + provider.ProviderId +
                    " · " + provider.Origin.ToString().ToLowerInvariant())
                {
                    name = ExtensionLineName,
                });
                _extensionRows.Add(row);
            }
            foreach (var package in build.TypeStage.ExtensionPackages)
            {
                var row = new VisualElement { name = ExtensionRowName };
                row.Add(new Label("package " + package.PackageId)
                {
                    name = ExtensionLineName,
                });
                _extensionRows.Add(row);
            }
            if (_extensionRows.childCount == 0)
            {
                _extensionRows.Add(new Label("(no registered extensions)"));
            }
        }

        private void BuildFailureRows(RegistrySnapshot? snapshot)
        {
            _failureRows.Clear();
            if (snapshot is null)
            {
                return;
            }
            foreach (var failure in snapshot.CandidateFailures)
            {
                var row = new VisualElement { name = FailureRowName };
                row.Add(new Label(
                    failure.Kind.ToString().ToLowerInvariant() + " " + failure.CandidateId +
                    (failure.SourceFile is null
                        ? string.Empty
                        : " · " + failure.SourceFile))
                {
                    name = FailureTitleName,
                });
                foreach (var diagnostic in failure.Diagnostics)
                {
                    row.Add(new Label(
                        diagnostic.Code + " " + diagnostic.Message +
                        (diagnostic.Hint is null
                            ? string.Empty
                            : " (" + diagnostic.Hint + ")"))
                    {
                        name = FailureLineName,
                    });
                }
                _failureRows.Add(row);
            }
            if (_failureRows.childCount == 0)
            {
                _failureRows.Add(new Label("(no registration failures)"));
            }
        }

        private void BuildExclusionRows(RegistrySnapshot? snapshot)
        {
            _exclusionRows.Clear();
            if (snapshot is null)
            {
                return;
            }
            foreach (var exclusion in snapshot.Exclusions)
            {
                var row = new VisualElement { name = ExclusionRowName };
                var text = exclusion.Kind == RegistryCandidateKind.Operation
                    ? "operation " + exclusion.CandidateId +
                        (exclusion.OperationVersion is null
                            ? string.Empty
                            : " v" + exclusion.OperationVersion.Value)
                    : "type provider " + exclusion.CandidateId;
                if (exclusion.PackageId is not null || exclusion.AssemblyName is not null)
                {
                    text += " · " + (exclusion.PackageId ?? "@project") +
                        (exclusion.AssemblyName is null
                            ? string.Empty
                            : "/" + exclusion.AssemblyName);
                }
                if (exclusion.Reason is not null)
                {
                    text += " · " + exclusion.Reason;
                }
                row.Add(new Label(text) { name = ExclusionLineName });
                _exclusionRows.Add(row);
            }
            if (_exclusionRows.childCount == 0)
            {
                _exclusionRows.Add(new Label("(no exclusions)"));
            }
        }

        private void BuildDiffRows(RegistryDiffResult? diff)
        {
            _diffRows.Clear();
            if (diff is null)
            {
                _diffStatus.text = "no registry diff recorded";
                return;
            }
            if (diff.Previous is null)
            {
                _diffStatus.text = "previous snapshot unavailable" +
                    (diff.UnavailableReason is null
                        ? string.Empty
                        : ": " + diff.UnavailableReason);
            }
            else if (diff.Changes.Count == 0)
            {
                _diffStatus.text = "no changes since the previous Registry";
            }
            else
            {
                _diffStatus.text = diff.Changes.Count +
                    (diff.Changes.Count == 1 ? " change" : " changes") +
                    " since the previous Registry" +
                    (diff.ChangesRecorded ? string.Empty : " (not recorded)");
            }
            foreach (var change in diff.Changes)
            {
                var row = new VisualElement { name = DiffRowName };
                row.Add(new Label(
                    change.Kind + " " + change.OperationId + " v" + change.Version +
                    (change.SourceFile is null
                        ? string.Empty
                        : " · " + change.SourceFile))
                {
                    name = DiffLineName,
                });
                _diffRows.Add(row);
            }
            if (diff.PersistenceError is not null)
            {
                _diffRows.Add(new Label("persistence error: " + diff.PersistenceError)
                {
                    name = DiffLineName,
                });
            }
        }

        private static string DescribeOperation(RegisteredOperation operation)
        {
            var text = operation.AssemblyName;
            if (operation.SourceFile is not null)
            {
                text += " · " + operation.SourceFile;
            }
            if (operation.PackageId is not null)
            {
                text += " · " + operation.PackageId +
                    (operation.ResolvedCommit is null
                        ? string.Empty
                        : "@" + operation.ResolvedCommit);
            }
            return text;
        }

        private static Label SectionTitle(string text) =>
            new Label(text) { name = SectionTitleName };
    }
}
