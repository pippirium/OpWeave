#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The human Project Settings screen for OpWeave (SPEC §H4 item 1, §H4.1): it
    /// shows every v2 key — including the extensions DiscoveryMode /
    /// IntegrityLevel / whitelist axes — lists settings errors with their JsonPath,
    /// and is the only write path: saving produces a v2 file that preserves unknown
    /// keys and every value the human did not explicitly edit
    /// (<see cref="OpWeaveSettingsDocument"/>). Agents have no settings write path.
    /// </summary>
    internal sealed class OpWeaveSettingsProvider : SettingsProvider
    {
        private static readonly string[] s_levelOptions = { "allow", "ask", "deny" };
        private static readonly string[] s_codeWriteOptions = { "ask", "deny" };

        private OpWeaveSettingsDocument? _document;
        private bool _replaceConfirmed;
        private string? _statusMessage;
        private MessageType _statusType = MessageType.Info;
        private Vector2 _scroll;

        private OpWeaveSettingsProvider()
            : base("Project/OpWeave", SettingsScope.Project)
        {
            keywords = new[]
            {
                "OpWeave", "policy", "permission", "extensions", "whitelist",
                "discovery", "integrity", "settings",
            };
        }

        [SettingsProvider]
        private static SettingsProvider Create() => new OpWeaveSettingsProvider();

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            Reload();
        }

        public override void OnGUI(string searchContext)
        {
            var document = _document ??= OpWeaveSettingsDocument.Load();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawDiagnostics(document);

            foreach (var reason in document.SaveBlockReasons)
            {
                EditorGUILayout.HelpBox(reason, MessageType.Error);
            }
            if (document.V1WhitelistResolutionRequired)
            {
                EditorGUILayout.HelpBox(
                    "The stored whitelist entries below are not active (review migration: " +
                    "whitelist empty). Keep them, or remove the entries that have no " +
                    "unknown keys, before saving.",
                    MessageType.Warning);
                if (GUILayout.Button("Keep the stored whitelist entries"))
                {
                    document.KeepV1WhitelistEntries();
                }
            }

            if (document.RequiresReplaceConfirmation)
            {
                EditorGUILayout.HelpBox(
                    "The settings file could not be read (OW-CFG-001). The values below " +
                    "are the documented defaults; saving is disabled until you confirm " +
                    "replacing the file.",
                    MessageType.Error);
                _replaceConfirmed = EditorGUILayout.ToggleLeft(
                    "Replace the unreadable settings file with the values below",
                    _replaceConfirmed);
            }

            DrawPolicy(document, PolicySide.Agent);
            DrawPolicy(document, PolicySide.Human);
            DrawExtensions(document);
            DrawLists(document);
            DrawGeneral(document);

            EditorGUILayout.Space();
            if (_statusMessage is not null)
            {
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
            using (new EditorGUI.DisabledScope(!document.CanSave(_replaceConfirmed)))
            {
                if (GUILayout.Button("Save"))
                {
                    Save(document);
                }
            }
            if (GUILayout.Button("Reload"))
            {
                Reload();
            }

            EditorGUILayout.EndScrollView();
        }

        private void Reload()
        {
            _document = OpWeaveSettingsDocument.Load();
            _replaceConfirmed = false;
            _statusMessage = null;
        }

        private void Save(OpWeaveSettingsDocument document)
        {
            var outcome = document.Save(_replaceConfirmed);
            switch (outcome.Status)
            {
                case SettingsSaveStatus.Saved:
                {
                    var result = outcome.Result!.Value;
                    var errors = 0;
                    var warnings = 0;
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        if (diagnostic.Severity == Severity.Error)
                        {
                            errors++;
                        }
                        else
                        {
                            warnings++;
                        }
                    }
                    // Re-read from disk so the screen always shows the file's
                    // real state; this clears the pending edits.
                    Reload();
                    _statusMessage = result.Ok
                        ? warnings == 0
                            ? "Saved ProjectSettings/OpWeaveSettings.json (version 2)."
                            : "Saved ProjectSettings/OpWeaveSettings.json (version 2) with " +
                                warnings.ToString() +
                                " warning(s); unknown keys were preserved."
                        : "Saved, but the file still holds " + errors.ToString() +
                            " invalid value(s) that were not edited; fix them to " +
                            "leave the settings-error state.";
                    _statusType = result.Ok
                        ? warnings == 0 ? MessageType.Info : MessageType.Warning
                        : MessageType.Error;
                    break;
                }
                case SettingsSaveStatus.ReplaceConfirmationRequired:
                    // Nothing was written; the document and its pending edits stay.
                    _statusMessage = "Not saved: the settings file could not be read " +
                        "(OW-CFG-001); saving needs the explicit replace-the-file " +
                        "confirmation.";
                    _statusType = MessageType.Error;
                    break;
                case SettingsSaveStatus.Blocked:
                    // Nothing was written; the pending edits stay so the human can
                    // resolve the block and retry.
                    _statusMessage = "Not saved: " + outcome.FailureMessage;
                    _statusType = MessageType.Error;
                    break;
                case SettingsSaveStatus.ChangedExternally:
                    // Nothing was written; reload so the screen shows the file's
                    // real state instead of stale edits.
                    Reload();
                    _statusMessage = "Not saved: the settings file changed on disk; " +
                        "it was reloaded.";
                    _statusType = MessageType.Warning;
                    break;
                default:
                    // WriteFailed: the write itself failed — keep the pending
                    // edits so the human can retry or keep working.
                    _statusMessage = outcome.FailureMessage +
                        (outcome.ExceptionTypeName is null
                            ? ""
                            : " (" + outcome.ExceptionTypeName + ")");
                    _statusType = MessageType.Error;
                    break;
            }
        }

        private static void DrawDiagnostics(OpWeaveSettingsDocument document)
        {
            EditorGUILayout.LabelField("Settings status", EditorStyles.boldLabel);
            if (document.Diagnostics.Count == 0)
            {
                EditorGUILayout.LabelField("No settings errors or warnings.");
            }
            foreach (var diagnostic in document.Diagnostics)
            {
                var location = diagnostic.JsonPath is null ? "" : " at " + diagnostic.JsonPath;
                EditorGUILayout.HelpBox(
                    diagnostic.Code + location + ": " + diagnostic.Message,
                    diagnostic.Severity == Severity.Error ? MessageType.Error : MessageType.Warning);
            }
            if (document.SourceWasV1)
            {
                EditorGUILayout.HelpBox(
                    "This file is in the retired v1 format. Saving writes it as version 2 " +
                    "with the migrated values (OW-CFG-004).",
                    MessageType.Info);
            }
            EditorGUILayout.Space();
        }

        private void DrawPolicy(OpWeaveSettingsDocument document, PolicySide side)
        {
            var title = side == PolicySide.Agent ? "Agent permissions" : "Human permissions";
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            foreach (var effect in EffectNames.All)
            {
                DrawPolicyLevel(document, side, effect);
            }
            EditorGUILayout.Space();
        }

        private void DrawPolicyLevel(OpWeaveSettingsDocument document, PolicySide side, string effect)
        {
            var label = "policy." +
                (side == PolicySide.Agent ? "agent" : "human") +
                "." + effect;
            var codeWrite = effect == EffectNames.CodeWrite;
            var options = codeWrite ? s_codeWriteOptions : s_levelOptions;
            var valid = document.TryGetPolicyLevel(side, effect, out var level);
            if (!valid)
            {
                EditorGUILayout.HelpBox(
                    label + " has an invalid value; choose a level to fix it.",
                    MessageType.Warning);
            }
            var index = codeWrite
                ? level == PolicyLevel.Deny ? 1 : 0 // Allow cannot occur: TryGet maps agent allow→ask.
                : (int)level;
            if (!valid)
            {
                index = -1;
            }
            var next = EditorGUILayout.Popup(label, index, options);
            if (next >= 0 && next != index)
            {
                var chosen = options[next] switch
                {
                    "allow" => PolicyLevel.Allow,
                    "ask" => PolicyLevel.Ask,
                    _ => PolicyLevel.Deny,
                };
                try
                {
                    document.SetPolicyLevel(side, effect, chosen);
                }
                catch (ArgumentException ex)
                {
                    _statusMessage = ex.Message;
                    _statusType = MessageType.Error;
                }
            }
        }

        private void DrawExtensions(OpWeaveSettingsDocument document)
        {
            EditorGUILayout.LabelField("Extensions", EditorStyles.boldLabel);

            var modeValid = document.TryGetDiscoveryMode(out var mode);
            var choiceRequired = document.DiscoveryModeChoiceRequired;
            if (choiceRequired)
            {
                EditorGUILayout.HelpBox(
                    "This v1 file needs an explicit discovery mode choice before it " +
                    "can be saved; the shown mode is not written until you pick one.",
                    MessageType.Warning);
            }
            else if (!modeValid)
            {
                EditorGUILayout.HelpBox(
                    "extensions.discoveryMode has an invalid value; choose a mode to fix it.",
                    MessageType.Warning);
            }
            var modeNames = new[] { "auto", "whitelist" };
            var modeIndex = !choiceRequired && modeValid
                ? (mode == DiscoveryMode.Whitelist ? 1 : 0)
                : -1;
            var nextMode = EditorGUILayout.Popup("extensions.discoveryMode", modeIndex, modeNames);
            if (nextMode >= 0 && nextMode != modeIndex)
            {
                document.SetDiscoveryMode(
                    nextMode == 0 ? DiscoveryMode.Auto : DiscoveryMode.Whitelist);
            }

            var levelValid = document.TryGetIntegrityLevel(out var integrity);
            if (!levelValid)
            {
                EditorGUILayout.HelpBox(
                    "extensions.integrityLevel has an invalid value; choose a level to fix it.",
                    MessageType.Warning);
            }
            var levelNames = new[] { "weak", "full" };
            var levelIndex = levelValid ? (integrity == IntegrityLevel.Full ? 1 : 0) : -1;
            var nextLevel = EditorGUILayout.Popup(
                "extensions.integrityLevel", levelIndex, levelNames);
            if (nextLevel >= 0 && nextLevel != levelIndex)
            {
                document.SetIntegrityLevel(
                    nextLevel == 0 ? IntegrityLevel.Weak : IntegrityLevel.Full);
            }

            DrawOperationWhitelist(document);
            DrawTypeProviderWhitelist(document);
            EditorGUILayout.Space();
        }

        private void DrawOperationWhitelist(OpWeaveSettingsDocument document)
        {
            EditorGUILayout.LabelField("Whitelisted operations", EditorStyles.miniBoldLabel);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.Operations);
            if (!document.TryGetWhitelistedOperations(out _) && items.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "extensions.whitelist.operations has an invalid value; replace the " +
                    "list or save a corrected file.",
                    MessageType.Warning);
                if (GUILayout.Button("Replace operations whitelist with an empty list"))
                {
                    TryWhitelistEdit(document, () =>
                        document.SetWhitelistedOperations(
                            Array.Empty<OperationWhitelistEntry>()));
                }
                return;
            }
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsObject)
                {
                    var packageId = WhitelistTextSeed(item, "packageId");
                    var assemblyName = WhitelistTextSeed(item, "assemblyName");
                    var operationId = WhitelistTextSeed(item, "operationId");
                    var version = WhitelistLongSeed(item, "version");
                    if (item.OperationEntry is null)
                    {
                        EditorGUILayout.HelpBox(
                            "Entry " + i.ToString() + " is invalid: " +
                            item.Raw.CanonicalText +
                            " — repair the fields below; keys OpWeave does not " +
                            "understand are kept on the entry.",
                            MessageType.Warning);
                    }
                    var nextPackageId = EditorGUILayout.TextField(
                        "Entry " + i.ToString() + " packageId", packageId);
                    if (nextPackageId != packageId)
                    {
                        TryWhitelistEdit(document, () =>
                            document.SetWhitelistItemMember(item, "packageId", nextPackageId));
                    }
                    var nextAssemblyName = EditorGUILayout.TextField(
                        "Entry " + i.ToString() + " assemblyName", assemblyName);
                    if (nextAssemblyName != assemblyName)
                    {
                        TryWhitelistEdit(document, () =>
                            document.SetWhitelistItemMember(
                                item, "assemblyName", nextAssemblyName));
                    }
                    var nextOperationId = EditorGUILayout.TextField(
                        "Entry " + i.ToString() + " operationId", operationId);
                    if (nextOperationId != operationId)
                    {
                        TryWhitelistEdit(document, () =>
                            document.SetWhitelistItemMember(
                                item, "operationId", nextOperationId));
                    }
                    var nextVersion = EditorGUILayout.LongField(
                        "Entry " + i.ToString() + " version", version);
                    if (nextVersion != version)
                    {
                        TryWhitelistEdit(document, () =>
                            document.SetWhitelistItemMember(item, "version", nextVersion));
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Entry " + i.ToString() + " is invalid: " + item.Raw.CanonicalText,
                        MessageType.Warning);
                }
                DrawWhitelistItemButtons(document, item, i, items.Count);
            }
            if (GUILayout.Button("Add whitelisted operation", EditorStyles.miniButton))
            {
                document.AddWhitelistItem(
                    new OperationWhitelistEntry("@project", "Assembly.Name", "operation.id", 1));
            }
        }

        private void DrawTypeProviderWhitelist(OpWeaveSettingsDocument document)
        {
            EditorGUILayout.LabelField("Whitelisted type providers", EditorStyles.miniBoldLabel);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.TypeProviders);
            if (!document.TryGetWhitelistedTypeProviders(out _) && items.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "extensions.whitelist.typeProviders has an invalid value; replace " +
                    "the list or save a corrected file.",
                    MessageType.Warning);
                if (GUILayout.Button("Replace type provider whitelist with an empty list"))
                {
                    TryWhitelistEdit(document, () =>
                        document.SetWhitelistedTypeProviders(
                            Array.Empty<TypeProviderWhitelistEntry>()));
                }
                return;
            }
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsObject)
                {
                    var packageId = WhitelistTextSeed(item, "packageId");
                    var providerId = WhitelistTextSeed(item, "providerId");
                    if (item.TypeProviderEntry is null)
                    {
                        EditorGUILayout.HelpBox(
                            "Provider " + i.ToString() + " is invalid: " +
                            item.Raw.CanonicalText +
                            " — repair the fields below; keys OpWeave does not " +
                            "understand are kept on the entry.",
                            MessageType.Warning);
                    }
                    var nextPackageId = EditorGUILayout.TextField(
                        "Provider " + i.ToString() + " packageId", packageId);
                    if (nextPackageId != packageId)
                    {
                        TryWhitelistEdit(document, () =>
                            document.SetWhitelistItemMember(item, "packageId", nextPackageId));
                    }
                    var nextProviderId = EditorGUILayout.TextField(
                        "Provider " + i.ToString() + " providerId", providerId);
                    if (nextProviderId != providerId)
                    {
                        TryWhitelistEdit(document, () =>
                            document.SetWhitelistItemMember(item, "providerId", nextProviderId));
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Provider " + i.ToString() + " is invalid: " + item.Raw.CanonicalText,
                        MessageType.Warning);
                }
                DrawWhitelistItemButtons(document, item, i, items.Count);
            }
            if (GUILayout.Button("Add whitelisted type provider", EditorStyles.miniButton))
            {
                document.AddWhitelistItem(
                    new TypeProviderWhitelistEntry("@project", "provider.id"));
            }
        }

        // Move/remove controls for one whitelist element. Removal is disabled for
        // an entry that carries unknown properties — dropping it would lose
        // OW-CFG-003 data a human save must keep.
        private void DrawWhitelistItemButtons(
            OpWeaveSettingsDocument document, SettingsWhitelistItem item, int index, int count)
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(index == 0))
            {
                if (GUILayout.Button("Up", EditorStyles.miniButton, GUILayout.Width(30)))
                {
                    document.MoveWhitelistItem(item, index - 1);
                }
            }
            using (new EditorGUI.DisabledScope(index >= count - 1))
            {
                if (GUILayout.Button("Down", EditorStyles.miniButton, GUILayout.Width(45)))
                {
                    document.MoveWhitelistItem(item, index + 1);
                }
            }
            using (new EditorGUI.DisabledScope(!item.CanRemove))
            {
                if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(55)))
                {
                    TryWhitelistEdit(document, () => document.RemoveWhitelistItem(item));
                }
            }
            if (!item.CanRemove)
            {
                EditorGUILayout.LabelField(
                    "has keys OpWeave does not understand; remove them in the file first",
                    EditorStyles.miniLabel);
            }
            EditorGUILayout.EndHorizontal();
        }

        // The text a whitelist field is seeded with: the member's own string
        // when it is a JSON string — values the entry rules reject included,
        // shown verbatim — or empty for an absent or non-string member, which
        // the human must fill in either way. Each field commits its own
        // SetWhitelistItemMember edit, so a repair keeps working field by
        // field while the members the human did not touch stay exactly as
        // stored and unknown properties stay on the same object
        // (rule A2-15/P-7).
        private static string WhitelistTextSeed(SettingsWhitelistItem item, string key)
        {
            var member = (item.Element as JObject)?[key];
            return member is not null && member.Type == JTokenType.String
                ? member.Value<string>()!
                : "";
        }

        // The long a whitelist version field is seeded with: the stored
        // integer, or 0 when the member is absent or not an integer. A missing
        // version must never display a real value — seeding 1 would make the
        // human's own choice of 1 register no change and never repair the
        // entry; 0 is invalid (version >= 1), so every real choice commits as
        // the human's explicit version edit.
        private static long WhitelistLongSeed(SettingsWhitelistItem item, string key)
        {
            var member = (item.Element as JObject)?[key];
            return member is not null && member.Type == JTokenType.Integer
                ? member.Value<long>()
                : 0L;
        }

        private void TryWhitelistEdit(OpWeaveSettingsDocument document, Action edit)
        {
            try
            {
                edit();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                _statusMessage = ex.Message;
                _statusType = MessageType.Error;
            }
        }

        private void DrawLists(OpWeaveSettingsDocument document)
        {
            EditorGUILayout.LabelField("Paths and sources", EditorStyles.boldLabel);
            DrawStringList(document, SettingsStringListKey.AllowedPaths, "allowedPaths");
            DrawStringList(document, SettingsStringListKey.CodePaths, "codePaths");
            DrawStringList(
                document, SettingsStringListKey.CustomOperationFolders, "customOperationFolders");
            DrawStringList(
                document, SettingsStringListKey.AllowedPackageSources, "allowedPackageSources");
            DrawStringList(
                document, SettingsStringListKey.DisabledOperations, "disabledOperations");
            EditorGUILayout.Space();
        }

        // Every element of a string-array key has its own controls bound to
        // the element's identity: the draft element keeps the stored JsonValue,
        // its type and its edited flag, so editing, reordering or removing one
        // row can never re-associate its text with a different stored value
        // (rule A2-25/P-5) — an invalid element is shown as its raw JSON and
        // leaves only through an explicit edit or removal of its own row. A
        // draft the document refuses is held as pending: it stays drawn for
        // repair and disables Save until it commits, reverts or is discarded —
        // a visible human edit is never silently dropped, and an untouched
        // element (valid or invalid) is always written back verbatim.
        private void DrawStringList(
            OpWeaveSettingsDocument document, SettingsStringListKey key, string label)
        {
            var valid = document.TryGetStringList(key, out _);
            if (!valid)
            {
                EditorGUILayout.HelpBox(
                    label + " has an invalid value; every stored element is " +
                    "shown (non-strings as raw JSON) — fix or remove the " +
                    "invalid entries before the edit is accepted.",
                    MessageType.Warning);
            }
            var pending = document.HasPendingStringListDraft(key);
            if (pending)
            {
                EditorGUILayout.HelpBox(
                    "The " + label + " edits were not accepted; they stay " +
                    "below for repair and saving is disabled until they are " +
                    "resolved, reverted or discarded.",
                    MessageType.Warning);
            }
            var draft = document.GetStringListDraft(key);
            EditorGUILayout.LabelField(label);
            var mutated = false;
            var restructured = false;
            for (var i = 0; i < draft.Count && !restructured; i++)
            {
                var element = draft[i];
                if (element.IsUnresolved)
                {
                    EditorGUILayout.HelpBox(
                        "Entry " + i.ToString() + " is invalid: " + element.Text +
                        " — edit or remove it.", MessageType.Warning);
                }
                EditorGUILayout.BeginHorizontal();
                var next = EditorGUILayout.TextField(element.Text);
                if (!string.Equals(next, element.Text, StringComparison.Ordinal))
                {
                    draft[i] = element.WithText(next);
                    mutated = true;
                }
                using (new EditorGUI.DisabledScope(i == 0))
                {
                    if (GUILayout.Button("Up", EditorStyles.miniButton, GUILayout.Width(30)))
                    {
                        var current = draft[i];
                        draft.RemoveAt(i);
                        draft.Insert(i - 1, current);
                        mutated = true;
                        restructured = true;
                    }
                }
                using (new EditorGUI.DisabledScope(i >= draft.Count - 1))
                {
                    if (GUILayout.Button(
                            "Down", EditorStyles.miniButton, GUILayout.Width(45)))
                    {
                        var current = draft[i];
                        draft.RemoveAt(i);
                        draft.Insert(i + 1, current);
                        mutated = true;
                        restructured = true;
                    }
                }
                if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(55)))
                {
                    draft.RemoveAt(i);
                    mutated = true;
                    restructured = true;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add " + label + " entry", EditorStyles.miniButton))
            {
                draft.Add(SettingsStringListDraftElement.Added("", key));
                mutated = true;
            }
            if (pending &&
                GUILayout.Button("Discard " + label + " edits", EditorStyles.miniButton))
            {
                document.DiscardStringListDraft(key);
            }
            EditorGUILayout.EndHorizontal();
            if (mutated)
            {
                CommitStringListDraft(document, key, draft);
            }
        }

        // Commits the edited draft; a refused draft — an unresolved invalid
        // element remains, or an entry violates the key grammar — is held as
        // pending so it stays drawn for repair and keeps Save disabled instead
        // of being discarded by a save of the unchanged document.
        private void CommitStringListDraft(
            OpWeaveSettingsDocument document,
            SettingsStringListKey key,
            List<SettingsStringListDraftElement> draft)
        {
            try
            {
                document.SetStringListDraft(key, draft);
            }
            catch (ArgumentException ex)
            {
                document.HoldStringListDraft(key, draft);
                _statusMessage = ex.Message;
                _statusType = MessageType.Error;
            }
        }

        private void DrawGeneral(OpWeaveSettingsDocument document)
        {
            EditorGUILayout.LabelField("General", EditorStyles.boldLabel);

            var destructive = document.TryGetDestructiveRequiresAsk(out var requireAsk);
            if (!destructive)
            {
                EditorGUILayout.HelpBox(
                    "destructiveRequiresAsk has an invalid value; toggle it to fix.",
                    MessageType.Warning);
            }
            var nextRequireAsk = EditorGUILayout.Toggle(
                "destructiveRequiresAsk", requireAsk);
            if (nextRequireAsk != requireAsk)
            {
                document.SetDestructiveRequiresAsk(nextRequireAsk);
            }

            var languageValid = document.TryGetAgentLanguage(out var language);
            if (!languageValid)
            {
                EditorGUILayout.HelpBox(
                    "agentLanguage has an invalid value; edit it to fix.", MessageType.Warning);
            }
            var nextLanguage = EditorGUILayout.TextField("agentLanguage", language);
            if (nextLanguage != language)
            {
                document.SetAgentLanguage(nextLanguage);
            }

            var debugValid = document.TryGetDebugLogging(out var debug);
            if (!debugValid)
            {
                EditorGUILayout.HelpBox(
                    "debugLogging has an invalid value; toggle it to fix.", MessageType.Warning);
            }
            var nextDebug = EditorGUILayout.Toggle("debugLogging", debug);
            if (nextDebug != debug)
            {
                document.SetDebugLogging(nextDebug);
            }

            DrawInteger(document, SettingsIntegerKey.TickBudgetMs, "tickBudgetMs");
            DrawInteger(document, SettingsIntegerKey.RetainRuns, "retainRuns");
            DrawInteger(document, SettingsIntegerKey.RetainMegabytes, "retainMegabytes");
            DrawInteger(
                document, SettingsIntegerKey.ApprovalTimeoutMinutes, "approvalTimeoutMinutes");
        }

        private void DrawInteger(
            OpWeaveSettingsDocument document, SettingsIntegerKey key, string label)
        {
            var valid = document.TryGetInteger(key, out var value);
            if (!valid)
            {
                EditorGUILayout.HelpBox(
                    label + " has an invalid value; enter an integer to fix it.",
                    MessageType.Warning);
            }
            var next = EditorGUILayout.LongField(label, value);
            if (next != value)
            {
                document.SetInteger(key, next);
            }
        }
    }
}
