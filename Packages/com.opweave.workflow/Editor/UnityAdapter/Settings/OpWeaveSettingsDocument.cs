#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>Which <c>policy</c> map a settings policy field belongs to (SPEC §H4.1).</summary>
    public enum PolicySide
    {
        Agent,
        Human,
    }

    /// <summary>The v2 string-array settings keys edited through Project Settings (SPEC §H4.1).</summary>
    public enum SettingsStringListKey
    {
        AllowedPaths,
        CodePaths,
        CustomOperationFolders,
        AllowedPackageSources,
        DisabledOperations,
    }

    /// <summary>The v2 integer settings keys edited through Project Settings (SPEC §H4.1).</summary>
    public enum SettingsIntegerKey
    {
        TickBudgetMs,
        RetainRuns,
        RetainMegabytes,
        ApprovalTimeoutMinutes,
    }

    /// <summary>
    /// Which <c>extensions.whitelist</c> array a <see cref="SettingsWhitelistItem"/>
    /// belongs to (SPEC §H4.1).
    /// </summary>
    public enum SettingsWhitelistKind
    {
        Operations,
        TypeProviders,
    }

    /// <summary>
    /// A live handle to one element of an <c>extensions.whitelist</c> array inside an
    /// <see cref="OpWeaveSettingsDocument"/> working copy. The handle binds to the
    /// element's JSON object identity — never to its array index — so an entry's
    /// unknown properties (OW-CFG-003) stay on their own object across edits,
    /// reorders and removals (SPEC §H4.1: a human save never drops unknown keys).
    /// An element that carries unknown properties cannot be removed through the
    /// document: <see cref="CanRemove"/> is false and
    /// <see cref="OpWeaveSettingsDocument.RemoveWhitelistItem"/> refuses.
    /// </summary>
    public sealed class SettingsWhitelistItem
    {
        internal static readonly string[] OperationKeys =
            { "packageId", "assemblyName", "operationId", "version" };
        internal static readonly string[] TypeProviderKeys = { "packageId", "providerId" };

        internal SettingsWhitelistItem(JToken element, SettingsWhitelistKind kind)
        {
            Element = element;
            Kind = kind;
        }

        // The live element inside the document's working tree.
        internal JToken Element { get; }

        internal string[] KnownKeys => Kind == SettingsWhitelistKind.Operations
            ? OperationKeys
            : TypeProviderKeys;

        /// <summary>Which whitelist array this element belongs to.</summary>
        public SettingsWhitelistKind Kind { get; }

        /// <summary>True when the element is a JSON object.</summary>
        public bool IsObject => Element is JObject;

        /// <summary>
        /// True when the element is an object holding at least one member that is not
        /// a required whitelist key — the members the loader reports as OW-CFG-003.
        /// </summary>
        public bool HasUnknownProperties
        {
            get
            {
                if (Element is not JObject obj)
                {
                    return false;
                }
                foreach (var property in obj.Properties())
                {
                    if (Array.IndexOf(KnownKeys, property.Name) < 0)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// True when the element may be removed: only elements without unknown
        /// properties can be dropped without losing OW-CFG-003 data.
        /// </summary>
        public bool CanRemove => !HasUnknownProperties;

        /// <summary>The element as an immutable <see cref="JsonValue"/> for raw display.</summary>
        public JsonValue Raw => JsonValue.From(Element);

        /// <summary>
        /// The element's typed tuple when it is a valid
        /// <c>operations</c> entry; <c>null</c> for a typeProviders item or an
        /// invalid element.
        /// </summary>
        public OperationWhitelistEntry? OperationEntry
        {
            get
            {
                if (Kind != SettingsWhitelistKind.Operations ||
                    Element is not JObject obj ||
                    !OpWeaveSettingsDocument.WhitelistString(obj, "packageId", out var packageId) ||
                    !OpWeaveSettingsDocument.WhitelistString(obj, "assemblyName", out var assemblyName) ||
                    !OpWeaveSettingsDocument.WhitelistString(obj, "operationId", out var operationId))
                {
                    return null;
                }
                var version = obj["version"];
                if (version is null || version.Type != JTokenType.Integer ||
                    version.Value<long>() < 1)
                {
                    return null;
                }
                return new OperationWhitelistEntry(
                    packageId!, assemblyName!, operationId!, version.Value<long>());
            }
        }

        /// <summary>
        /// The element's typed tuple when it is a valid
        /// <c>typeProviders</c> entry; <c>null</c> for an operations item or an
        /// invalid element.
        /// </summary>
        public TypeProviderWhitelistEntry? TypeProviderEntry
        {
            get
            {
                if (Kind != SettingsWhitelistKind.TypeProviders ||
                    Element is not JObject obj ||
                    !OpWeaveSettingsDocument.WhitelistString(obj, "packageId", out var packageId) ||
                    !OpWeaveSettingsDocument.WhitelistString(obj, "providerId", out var providerId))
                {
                    return null;
                }
                return new TypeProviderWhitelistEntry(packageId!, providerId!);
            }
        }
    }

    /// <summary>
    /// One draft element of a string-array settings key (SPEC §H4.1): the
    /// editable form of one stored array element — or of the stored member
    /// itself when it is not an array — and of entries the human added. The
    /// element keeps the stored value it was seeded from
    /// (<see cref="Original"/>) and compares the draft text against its seed: a
    /// stored element whose text still equals the seed is untouched and writes
    /// its original value back verbatim — type included — while a changed or
    /// added text is the human's explicit resolution and commits as a string.
    /// A stored element that is not a string (or a stored non-array member)
    /// therefore keeps its invalid value and its OW-CFG-002 until the human
    /// explicitly edits or removes its own row;
    /// <see cref="OpWeaveSettingsDocument.SetStringListDraft"/> never commits a
    /// draft that still holds an unresolved invalid element.
    /// </summary>
    public sealed class SettingsStringListDraftElement
    {
        private readonly JToken? _original;
        private readonly bool _isArrayElement;
        private readonly string _seed;
        private readonly SettingsStringListKey? _key;

        private SettingsStringListDraftElement(
            JToken? original, bool isArrayElement, string seed, string text,
            SettingsStringListKey? key)
        {
            _original = original;
            _isArrayElement = isArrayElement;
            _seed = seed;
            Text = text;
            _key = key;
        }

        // The stored array element — or stored non-array member — this element
        // was seeded from; null for an added element.
        internal JToken? OriginalToken => _original;

        // True when the stored value was an element of the settings array —
        // false for a stored non-array member and for added elements.
        internal bool IsArrayElement => _isArrayElement;

        internal static SettingsStringListDraftElement Stored(
            JToken token, bool isArrayElement, string seed, SettingsStringListKey key) =>
            new SettingsStringListDraftElement(token, isArrayElement, seed, seed, key);

        /// <summary>
        /// A draft element the human added; it carries no stored element and
        /// always counts as edited.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        public static SettingsStringListDraftElement Added(string text) => Added(text, null);

        // The document-seeded form of an added element: it knows its settings
        // key so <see cref="IsUnresolved"/> can apply the key's entry grammar.
        internal static SettingsStringListDraftElement Added(
            string text, SettingsStringListKey? key)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            return new SettingsStringListDraftElement(null, false, "", text, key);
        }

        /// <summary>
        /// The stored array element — or stored non-array member — this element
        /// was seeded from, as an immutable <see cref="JsonValue"/>; <c>null</c>
        /// for an entry the human added.
        /// </summary>
        public JsonValue? Original => _original is null ? null : JsonValue.From(_original);

        /// <summary>
        /// True when <see cref="Original"/> is an element of the stored settings
        /// array — <c>false</c> for a stored non-array member shown as a single
        /// raw entry and for added entries.
        /// </summary>
        public bool IsStoredElement => _original is not null && _isArrayElement;

        /// <summary>The draft element's text.</summary>
        public string Text { get; }

        /// <summary>
        /// True when the human changed this element's text — an added element,
        /// or an element whose text differs from the seeded stored display.
        /// Only an edited element commits as a new string; an untouched element
        /// writes its <see cref="Original"/> back verbatim, so retyping an
        /// invalid element's raw text leaves it unresolved instead of retyping
        /// its value.
        /// </summary>
        public bool Edited =>
            _original is null ||
            !string.Equals(Text, _seed, StringComparison.Ordinal);

        /// <summary>
        /// True when the element cannot be written back as a valid list entry
        /// in its current state: an untouched stored element whose raw JSON is
        /// not a string (or the stored non-array member), or text — typed or
        /// stored — that violates the settings key's entry grammar (known only
        /// for elements the document seeded; a caller-added element carries no
        /// key).
        /// <see cref="OpWeaveSettingsDocument.SetStringListDraft"/> refuses a
        /// draft while it still holds an unresolved element: the element leaves
        /// only through the human's explicit edit or removal, never by a silent
        /// rewrite.
        /// </summary>
        public bool IsUnresolved
        {
            get
            {
                if (!Edited && _original is not null &&
                    (!_isArrayElement || _original.Type != JTokenType.String))
                {
                    return true;
                }
                return _key == SettingsStringListKey.AllowedPackageSources &&
                    !PackageSourceRule.TryParse(Text, out _);
            }
        }

        /// <summary>
        /// The element after a draft text change: the same stored element with
        /// a new draft text (a text equal to the seed keeps the element
        /// untouched).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        public SettingsStringListDraftElement WithText(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            return new SettingsStringListDraftElement(
                _original, _isArrayElement, _seed, text, _key);
        }
    }

    /// <summary>Which terminal state a <see cref="OpWeaveSettingsDocument.Save"/> reached.</summary>
    public enum SettingsSaveStatus
    {
        /// <summary>The file was written; the outcome's result is the post-save load.</summary>
        Saved,
        /// <summary>
        /// The file could not be read as settings (OW-CFG-001) and no replace
        /// confirmation was given; the file was left untouched.
        /// </summary>
        ReplaceConfirmationRequired,
        /// <summary>
        /// The save was refused by the fail-closed save rules — an unresolved v1
        /// conflict the human must settle first, or a file that could not be read
        /// at load (every write, including replacement, is refused in that state).
        /// The file was left untouched.
        /// </summary>
        Blocked,
        /// <summary>
        /// The file changed on disk between load and save (including becoming
        /// unreadable, appearing, or disappearing); the save was refused so the
        /// newer on-disk content is never overwritten. The file was left untouched.
        /// </summary>
        ChangedExternally,
        /// <summary>The write itself failed (access denied or an I/O error).</summary>
        WriteFailed,
    }

    /// <summary>
    /// The typed outcome of <see cref="OpWeaveSettingsDocument.Save"/>: the expected
    /// failure states — an unconfirmed replace, a rule-blocked save, an externally
    /// changed file and a failed write — return through this channel, never as
    /// exceptions and never through a published error code outside its specified
    /// meaning (the Result/Diagnostic failure invariant).
    /// </summary>
    public sealed class OpWeaveSettingsSaveOutcome
    {
        private OpWeaveSettingsSaveOutcome(
            SettingsSaveStatus status,
            Result<OpWeaveSettings>? result,
            IReadOnlyList<Diagnostic> diagnostics,
            string? failureMessage,
            string? exceptionTypeName)
        {
            Status = status;
            Result = result;
            Diagnostics = diagnostics;
            FailureMessage = failureMessage;
            ExceptionTypeName = exceptionTypeName;
        }

        /// <summary>Which terminal state the save reached.</summary>
        public SettingsSaveStatus Status { get; }

        /// <summary>
        /// <see cref="SettingsSaveStatus.Saved"/>: the post-save load — the file was
        /// written, so a failed result means it still holds invalid content the
        /// human did not fix. <c>null</c> for every other status.
        /// </summary>
        public Result<OpWeaveSettings>? Result { get; }

        /// <summary>
        /// <see cref="SettingsSaveStatus.ReplaceConfirmationRequired"/> and
        /// <see cref="SettingsSaveStatus.Blocked"/>: the load-time diagnostics that
        /// gate the save; <see cref="SettingsSaveStatus.Saved"/>: the post-save
        /// diagnostics; empty otherwise.
        /// </summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        /// <summary>
        /// <see cref="SettingsSaveStatus.Blocked"/>,
        /// <see cref="SettingsSaveStatus.ChangedExternally"/> and
        /// <see cref="SettingsSaveStatus.WriteFailed"/>: a fixed English message —
        /// never exception text or file paths. <c>null</c> otherwise.
        /// </summary>
        public string? FailureMessage { get; }

        /// <summary>
        /// <see cref="SettingsSaveStatus.WriteFailed"/>: the thrown exception's
        /// type name only. <c>null</c> otherwise.
        /// </summary>
        public string? ExceptionTypeName { get; }

        /// <summary>The file was written; <paramref name="result"/> is its post-save load.</summary>
        public static OpWeaveSettingsSaveOutcome Saved(Result<OpWeaveSettings> result) =>
            new OpWeaveSettingsSaveOutcome(
                SettingsSaveStatus.Saved, result, result.Diagnostics, null, null);

        /// <summary>
        /// Saving was refused because the file is unreadable as settings
        /// (OW-CFG-001) and the human did not confirm the replace;
        /// <paramref name="diagnostics"/> are the load-time diagnostics.
        /// </summary>
        public static OpWeaveSettingsSaveOutcome ConfirmationRequired(
            IReadOnlyList<Diagnostic> diagnostics) =>
            new OpWeaveSettingsSaveOutcome(
                SettingsSaveStatus.ReplaceConfirmationRequired, null, diagnostics, null, null);

        /// <summary>
        /// Saving was refused by the fail-closed save rules;
        /// <paramref name="diagnostics"/> are the load-time diagnostics and
        /// <paramref name="message"/> a fixed English reason — never exception text
        /// or file paths.
        /// </summary>
        public static OpWeaveSettingsSaveOutcome Blocked(
            IReadOnlyList<Diagnostic> diagnostics, string message) =>
            new OpWeaveSettingsSaveOutcome(
                SettingsSaveStatus.Blocked, null, diagnostics, message, null);

        /// <summary>
        /// Saving was refused because the file changed on disk since it was loaded;
        /// nothing was written.
        /// </summary>
        public static OpWeaveSettingsSaveOutcome ChangedExternally() =>
            new OpWeaveSettingsSaveOutcome(
                SettingsSaveStatus.ChangedExternally,
                null,
                Array.Empty<Diagnostic>(),
                "The settings file changed on disk since it was loaded; reload and save again.",
                null);

        /// <summary>
        /// The write failed; <paramref name="exceptionTypeName"/> is the exception's
        /// type name only — never exception text or paths.
        /// </summary>
        public static OpWeaveSettingsSaveOutcome WriteFailure(string exceptionTypeName) =>
            new OpWeaveSettingsSaveOutcome(
                SettingsSaveStatus.WriteFailed,
                null,
                Array.Empty<Diagnostic>(),
                "The settings file could not be written.",
                exceptionTypeName);
    }

    /// <summary>
    /// The human-facing editable form of <c>ProjectSettings/OpWeaveSettings.json</c>
    /// and the only write path for it (SPEC §H4 item 1 and §H4.1: a file is written
    /// as v2 only when a human saves it through the SettingsProvider; Agents have no
    /// settings write path).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document keeps a working copy of the file's own parsed JSON tree and
    /// applies every human edit to that copy. Saving therefore patches the original
    /// document: it writes <c>version: 2</c> with the migrated or explicitly edited
    /// values while every member the human did not touch — including unknown keys
    /// retained under OW-CFG-003 — is written back byte-equivalent in value
    /// (Canonical JSON text equality, SPEC §0.3/§H4.1). Untouched invalid values
    /// (OW-CFG-002) are written back verbatim, never silently replaced with
    /// defaults (decision D-R6).
    /// </para>
    /// <para>
    /// A v1 save persists the SPEC §H4.1 migration only where it is lossless. When
    /// the migration has no faithful v2 write — an invalid or non-string
    /// <c>customOperationTrust</c>, a trust value that conflicts with a stored
    /// <c>extensions.discoveryMode</c>, a <c>"review"</c> migration while stored
    /// whitelist entries exist, or a non-object <c>extensions</c> under a valid
    /// trust — <see cref="Save"/> is blocked (typed
    /// <see cref="SettingsSaveStatus.Blocked"/>, the file untouched) until the
    /// human resolves the conflict explicitly: setting the discovery mode
    /// resolves the trust conflicts but never the pending whitelist resolution
    /// (the entries must be kept through <see cref="KeepV1WhitelistEntries"/> or
    /// removed one by one; an entry carrying unknown properties cannot be
    /// removed).
    /// </para>
    /// <para>
    /// When the file cannot be read as settings at all (OW-CFG-001: unparseable
    /// JSON, a non-object root or an unknown <c>version</c>) the working copy is a
    /// complete v2 defaults skeleton and <see cref="RequiresReplaceConfirmation"/>
    /// is set: saving is disabled until the human explicitly confirms replacing
    /// the file. For a parseable document with an unknown <c>version</c>,
    /// v2-unknown keys are still carried into the replacement so a confirmed
    /// replace never drops them; v2-known member names are stripped because an
    /// unknown version gives their semantics no authoritative reading. A file
    /// that cannot be read at all (an I/O failure) refuses every write, including
    /// replacement, until a later load reads it successfully.
    /// </para>
    /// <para>
    /// Saving also refuses — without touching the file — when the on-disk content
    /// changed between load and save, and writes atomically through a temporary
    /// file so a failed write never truncates the settings file. A transient
    /// external hold on the file or its replacement — an indexer or virus
    /// scanner briefly holding one open — is retried within a bounded budget
    /// before the write is reported <see cref="SettingsSaveStatus.WriteFailed"/>;
    /// a hold that releases into changed, vanished or newly present content is
    /// refused as <see cref="SettingsSaveStatus.ChangedExternally"/>, never
    /// overwritten.
    /// </para>
    /// </remarks>
    public sealed class OpWeaveSettingsDocument
    {
        private static readonly IReadOnlyList<string> s_knownRootKeys = Array.AsReadOnly(new[]
        {
            "version", "policy", "destructiveRequiresAsk", "extensions",
            "allowedPaths", "codePaths", "customOperationFolders",
            "allowedPackageSources", "disabledOperations", "agentLanguage",
            "debugLogging", "tickBudgetMs", "retainRuns", "retainMegabytes",
            "approvalTimeoutMinutes",
        });

        // The bounded retry the save's write step runs on a transient external
        // hold of the file or its temporary replacement (Issue #151: an
        // indexer or virus scanner briefly holding one open). The first delay
        // is SaveWriteRetryFirstDelayMilliseconds and doubles on each later
        // failure, capped at SaveWriteRetryMaxDelayMilliseconds per delay; a
        // further attempt runs only while the time since the first failed
        // attempt plus the next delay stays within
        // SaveWriteRetryBudgetMilliseconds, then the save fails closed.
        private const int SaveWriteRetryFirstDelayMilliseconds = 50;
        private const int SaveWriteRetryMaxDelayMilliseconds = 500;
        private const int SaveWriteRetryBudgetMilliseconds = 2000;
        private static readonly System.Diagnostics.Stopwatch s_saveRetryStopwatch =
            System.Diagnostics.Stopwatch.StartNew();

        private enum FileReadState
        {
            Missing,
            Read,
            Failed,
        }

        private readonly JObject _working;
        private readonly List<JToken> _v1ReviewPendingElements;
        private readonly Dictionary<SettingsStringListKey, List<SettingsStringListDraftElement>>
            _pendingStringListDrafts =
                new Dictionary<SettingsStringListKey, List<SettingsStringListDraftElement>>();
        private JObject? _original;
        private string? _originalText;
        private FileReadState _readState;
        private bool _discoveryModeEdited;
        private bool _v1WhitelistKept;

        private OpWeaveSettingsDocument(
            string filePath,
            bool fileExists,
            Result<OpWeaveSettings> loadResult,
            JObject working,
            JObject? original,
            string? originalText,
            FileReadState readState,
            bool requiresReplaceConfirmation,
            bool sourceWasV1,
            List<JToken> v1ReviewPendingElements)
        {
            FilePath = filePath;
            FileExists = fileExists;
            LoadResult = loadResult;
            _working = working;
            _original = original;
            _originalText = originalText;
            _readState = readState;
            RequiresReplaceConfirmation = requiresReplaceConfirmation;
            SourceWasV1 = sourceWasV1;
            _v1ReviewPendingElements = v1ReviewPendingElements;
        }

        /// <summary>The settings file path this document was loaded from and saves to.</summary>
        public string FilePath { get; }

        /// <summary>True when the file existed at load time (readable or not).</summary>
        public bool FileExists { get; }

        /// <summary>
        /// The strict SPEC §H4.1 load result: the settings-error channel (standard
        /// Operations only, no new Runs) on failure, warnings (OW-CFG-003/004/005)
        /// on success. Its diagnostics carry the JsonPath of every settings error the
        /// SettingsProvider lists.
        /// </summary>
        public Result<OpWeaveSettings> LoadResult { get; }

        /// <summary>All diagnostics from <see cref="LoadResult"/>.</summary>
        public IReadOnlyList<Diagnostic> Diagnostics => LoadResult.Diagnostics;

        /// <summary>
        /// True when the file could be read but cannot be interpreted as v1/v2
        /// settings (OW-CFG-001: unparseable JSON, a non-object root or an unknown
        /// <c>version</c>): <see cref="Save"/> then requires an explicit
        /// <c>replaceConfirmed</c> and the working copy is a v2 defaults skeleton,
        /// not file content. A file that could not be read at all is reported
        /// through <see cref="SaveBlockReasons"/> instead — replacement is refused
        /// there, never confirmable.
        /// </summary>
        public bool RequiresReplaceConfirmation { get; }

        /// <summary>
        /// True when the on-disk file was read under the v1 rules (version absent or
        /// 1, including files that failed with OW-CFG-002): a save persists the
        /// in-memory v2 migration of SPEC §H4.1 once every save blocker is resolved.
        /// </summary>
        public bool SourceWasV1 { get; private set; }

        /// <summary>
        /// The fixed-English reasons <see cref="Save"/> is currently blocked, in
        /// rule order: an unreadable file, then the v1 conflicts that need an
        /// explicit human resolution (invalid trust, non-object
        /// <c>extensions</c>, a stored discoveryMode that conflicts with the
        /// trust, pending review-migration whitelist entries), then every held
        /// string-list draft that was refused and not yet resolved
        /// (<see cref="HoldStringListDraft"/>). Empty when the save rules let
        /// the human save.
        /// </summary>
        public IReadOnlyList<string> SaveBlockReasons => ComputeSaveBlockReasons();

        /// <summary>
        /// True when a v1 file's <c>customOperationTrust</c> can only be resolved
        /// by the human explicitly choosing <c>extensions.discoveryMode</c> (or,
        /// for a non-object <c>extensions</c>, by rebuilding it through an
        /// extensions edit). The SettingsProvider then requires an explicit mode
        /// selection instead of showing the effective one.
        /// </summary>
        public bool DiscoveryModeChoiceRequired
        {
            get
            {
                if (!SourceWasV1 || _original is null)
                {
                    return false;
                }
                var trust = _original["customOperationTrust"];
                if (trust is null)
                {
                    return false;
                }
                var trustString = trust.Type == JTokenType.String
                    ? trust.Value<string>()
                    : null;
                if (trustString is not ("auto" or "review"))
                {
                    return !_discoveryModeEdited;
                }
                var extensions = _original["extensions"];
                if (extensions is not null && extensions is not JObject)
                {
                    return _working["extensions"] is not JObject;
                }
                return (extensions as JObject)?["discoveryMode"] is not null &&
                    !_discoveryModeEdited;
            }
        }

        /// <summary>
        /// True when a v1 <c>"review"</c> file still holds stored whitelist
        /// elements the migration would empty: the human must keep them
        /// (<see cref="KeepV1WhitelistEntries"/>) or remove them — removal is
        /// refused for elements that carry unknown properties — before saving.
        /// </summary>
        public bool V1WhitelistResolutionRequired
        {
            get
            {
                if (!SourceWasV1 || _original is null || _v1WhitelistKept)
                {
                    return false;
                }
                var trust = _original["customOperationTrust"];
                return trust is not null && trust.Type == JTokenType.String &&
                    trust.Value<string>() == "review" && HasPendingReviewElements();
            }
        }

        /// <summary>Loads <see cref="OpWeaveSettingsLoader.DefaultFilePath"/>.</summary>
        public static OpWeaveSettingsDocument Load() =>
            Load(OpWeaveSettingsLoader.DefaultFilePath);

        /// <summary>
        /// Loads the settings file at <paramref name="path"/> into an editable
        /// working copy. The strict load result is kept verbatim; the working copy
        /// is a clone of the file's own JSON object when one parses (the v1→v2
        /// migration is applied only at save time, once the fail-closed save
        /// rules let the save proceed), or a v2 defaults skeleton otherwise.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static OpWeaveSettingsDocument Load(string path)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            string? text = null;
            Exception? readError = null;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException)
            {
                readError = ex;
            }

            FileReadState readState;
            Result<OpWeaveSettings> loadResult;
            if (text is not null)
            {
                readState = FileReadState.Read;
                loadResult = OpWeaveSettingsLoader.Parse(text);
            }
            else if (readError is not null)
            {
                // The same OW-CFG-001 the loader reports for an unreadable file:
                // every write — including a confirmed replace — is refused in
                // this state until a later load can read the file.
                readState = FileReadState.Failed;
                loadResult = Result<OpWeaveSettings>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_CFG_001,
                    "Settings file could not be read.",
                    jsonPath: "$",
                    hint: readError.GetType().Name));
            }
            else
            {
                readState = FileReadState.Missing;
                loadResult = Result<OpWeaveSettings>.Success(OpWeaveSettings.Default);
            }

            JObject? original = null;
            if (text is not null)
            {
                var parsed = JsonValue.Parse(text);
                if (parsed.Ok)
                {
                    original = parsed.Value!.ToToken() as JObject;
                }
            }

            var requiresReplaceConfirmation =
                readState != FileReadState.Failed &&
                HasDiagnostic(loadResult, ErrorCodes.OW_CFG_001);
            var sourceWasV1 = original is not null && IsV1Version(original["version"]);

            JObject working;
            if (original is not null && !requiresReplaceConfirmation)
            {
                working = (JObject)original.DeepClone();
            }
            else if (original is not null)
            {
                // Unknown version: the file parses but its v2-known members have no
                // authoritative v2 reading. Keep every v2-unknown key (a confirmed
                // replace never drops unknown keys) and take the known-key values
                // from the v2 defaults skeleton.
                var stripped = (JObject)original.DeepClone();
                StripKnownMembers(stripped);
                working = stripped;
                MergeSkeletonInto(working);
            }
            else
            {
                working = BuildV2Skeleton();
            }
            working["version"] = OpWeaveSettings.CurrentVersion;

            var pending = new List<JToken>();
            if (sourceWasV1)
            {
                CollectWhitelistElements(working, pending);
            }
            return new OpWeaveSettingsDocument(
                path,
                fileExists: readState != FileReadState.Missing,
                loadResult,
                working,
                original,
                text,
                readState,
                requiresReplaceConfirmation,
                sourceWasV1,
                pending);
        }

        /// <summary>
        /// Whether <see cref="Save"/> may run: false while any
        /// <see cref="SaveBlockReasons"/> entry stands, and for a file that is
        /// unreadable as settings only after the human's explicit replace
        /// confirmation. An external change between load and save is a further
        /// refusal detected inside <see cref="Save"/> itself.
        /// </summary>
        public bool CanSave(bool replaceConfirmed) =>
            ComputeSaveBlockReasons().Count == 0 &&
            (replaceConfirmed || !RequiresReplaceConfirmation);

        // ── Policy fields (policy.agent / policy.human) ──────────────────────

        /// <summary>
        /// Reads one policy level from the working copy. Returns the §H4 example
        /// default for an absent member, <c>false</c> for a member whose value is
        /// invalid. An Agent <c>code.write</c> of <c>"allow"</c> reports
        /// <see cref="PolicyLevel.Ask"/> (the §H4 correction).
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="effect"/> is not a §C4 effect name.</exception>
        public bool TryGetPolicyLevel(PolicySide side, string effect, out PolicyLevel level)
        {
            if (!EffectNames.IsKnown(effect))
            {
                throw new ArgumentException("Unknown effect name: " + effect, nameof(effect));
            }
            level = DefaultPolicy(side)[effect];
            var member = PolicyMap(side)?[effect];
            if (member is null)
            {
                return true;
            }
            if (member.Type != JTokenType.String)
            {
                return false;
            }
            switch (member.Value<string>())
            {
                case "allow":
                    if (effect == EffectNames.CodeWrite)
                    {
                        if (side == PolicySide.Agent)
                        {
                            level = PolicyLevel.Ask;
                            return true;
                        }
                        return false;
                    }
                    level = PolicyLevel.Allow;
                    return true;
                case "ask":
                    level = PolicyLevel.Ask;
                    return true;
                case "deny":
                    level = PolicyLevel.Deny;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Writes one policy level as an explicit human edit.
        /// <c>code.write</c> accepts only <c>ask</c>/<c>deny</c> — the §H4 settable
        /// range excludes <c>allow</c> on both sides.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="effect"/> is not a §C4 effect name, or <c>code.write</c>
        /// is set to <see cref="PolicyLevel.Allow"/>.
        /// </exception>
        public void SetPolicyLevel(PolicySide side, string effect, PolicyLevel level)
        {
            if (!EffectNames.IsKnown(effect))
            {
                throw new ArgumentException("Unknown effect name: " + effect, nameof(effect));
            }
            if (effect == EffectNames.CodeWrite && level == PolicyLevel.Allow)
            {
                throw new ArgumentException(
                    "code.write cannot be set to allow (SPEC §H4 stage 3).", nameof(level));
            }
            var map = EnsureObjectForWrite(
                EnsureObjectForWrite(_working, "policy"),
                side == PolicySide.Agent ? "agent" : "human");
            map[effect] = LevelText(level);
        }

        // ── Scalar fields ────────────────────────────────────────────────────

        /// <summary><c>destructiveRequiresAsk</c>; default <c>true</c> when absent.</summary>
        public bool TryGetDestructiveRequiresAsk(out bool value) =>
            TryGetBoolean(_working, "destructiveRequiresAsk", true, out value);

        /// <summary>Sets <c>destructiveRequiresAsk</c>.</summary>
        public void SetDestructiveRequiresAsk(bool value) =>
            _working["destructiveRequiresAsk"] = value;

        /// <summary><c>debugLogging</c> (booleans only); default <c>false</c> when absent.</summary>
        public bool TryGetDebugLogging(out bool value) =>
            TryGetBoolean(_working, "debugLogging", false, out value);

        /// <summary>Sets <c>debugLogging</c>.</summary>
        public void SetDebugLogging(bool value) => _working["debugLogging"] = value;

        /// <summary><c>agentLanguage</c>; default <c>"en"</c> when absent.</summary>
        public bool TryGetAgentLanguage(out string value)
        {
            var member = _working["agentLanguage"];
            if (member is null)
            {
                value = "en";
                return true;
            }
            if (member.Type == JTokenType.String)
            {
                value = member.Value<string>()!;
                return true;
            }
            value = "en";
            return false;
        }

        /// <summary>Sets <c>agentLanguage</c>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public void SetAgentLanguage(string value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            _working["agentLanguage"] = value;
        }

        /// <summary>An integer settings key; its documented default applies when absent.</summary>
        public bool TryGetInteger(SettingsIntegerKey key, out long value)
        {
            var name = IntegerKeyName(key);
            var member = _working[name];
            if (member is null)
            {
                value = IntegerDefault(key);
                return true;
            }
            if (member.Type == JTokenType.Integer)
            {
                value = member.Value<long>();
                return true;
            }
            value = IntegerDefault(key);
            return false;
        }

        /// <summary>Sets an integer settings key.</summary>
        public void SetInteger(SettingsIntegerKey key, long value) =>
            _working[IntegerKeyName(key)] = value;

        // ── extensions fields ────────────────────────────────────────────────

        /// <summary>
        /// The effective <c>extensions.discoveryMode</c>: for a v1 file the
        /// <c>customOperationTrust</c> migration value wins over the stored
        /// member (an invalid or non-string trust reports invalid), until the
        /// human sets the mode explicitly.
        /// </summary>
        public bool TryGetDiscoveryMode(out DiscoveryMode value)
        {
            value = DiscoveryMode.Auto;
            if (SourceWasV1 && !_discoveryModeEdited && _original is not null)
            {
                var trust = _original["customOperationTrust"];
                if (trust is not null)
                {
                    if (trust.Type != JTokenType.String)
                    {
                        return false;
                    }
                    switch (trust.Value<string>())
                    {
                        case "auto":
                            value = DiscoveryMode.Auto;
                            return true;
                        case "review":
                            value = DiscoveryMode.Whitelist;
                            return true;
                        default:
                            return false;
                    }
                }
            }
            if (!TryExtensionsObject(out var extensions))
            {
                return false;
            }
            var member = extensions?["discoveryMode"];
            if (member is null)
            {
                return true;
            }
            switch (member.Type == JTokenType.String ? member.Value<string>() : null)
            {
                case "auto":
                    value = DiscoveryMode.Auto;
                    return true;
                case "whitelist":
                    value = DiscoveryMode.Whitelist;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Sets <c>extensions.discoveryMode</c> as an explicit human edit —
        /// writing only <c>auto</c>/<c>whitelist</c> — and counts as the human's
        /// resolution of a v1 trust conflict: an invalid trust, a stored
        /// conflicting discoveryMode and a non-object <c>extensions</c> are all
        /// settled by the chosen value (the invalid parent is rebuilt as an
        /// object holding only the members the human writes).
        /// </summary>
        public void SetDiscoveryMode(DiscoveryMode value)
        {
            var extensions = EnsureObjectForWrite(_working, "extensions");
            extensions["discoveryMode"] = value == DiscoveryMode.Auto ? "auto" : "whitelist";
            _discoveryModeEdited = true;
        }

        /// <summary><c>extensions.integrityLevel</c>; default <see cref="IntegrityLevel.Weak"/>.
        /// <c>"full"</c> is a valid value here — it is rejected only at Run acceptance
        /// with OW-OP-011 (SPEC §H4.1), never silently downgraded.</summary>
        public bool TryGetIntegrityLevel(out IntegrityLevel value)
        {
            value = IntegrityLevel.Weak;
            if (!TryExtensionsObject(out var extensions))
            {
                return false;
            }
            var member = extensions?["integrityLevel"];
            if (member is null)
            {
                return true;
            }
            switch (member.Type == JTokenType.String ? member.Value<string>() : null)
            {
                case "weak":
                    value = IntegrityLevel.Weak;
                    return true;
                case "full":
                    value = IntegrityLevel.Full;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Sets <c>extensions.integrityLevel</c>.</summary>
        public void SetIntegrityLevel(IntegrityLevel value)
        {
            var extensions = EnsureObjectForWrite(_working, "extensions");
            extensions["integrityLevel"] = value == IntegrityLevel.Weak ? "weak" : "full";
        }

        /// <summary><c>extensions.whitelist.operations</c>; empty when absent.</summary>
        public bool TryGetWhitelistedOperations(out IReadOnlyList<OperationWhitelistEntry> entries)
        {
            entries = Array.Empty<OperationWhitelistEntry>();
            if (!TryWhitelistObject(out var whitelist))
            {
                return false;
            }
            var member = whitelist?["operations"];
            if (member is null)
            {
                return true;
            }
            if (member is not JArray array)
            {
                return false;
            }
            var result = new List<OperationWhitelistEntry>(array.Count);
            foreach (var element in array)
            {
                if (element is not JObject entry)
                {
                    return false;
                }
                var version = entry["version"];
                if (!WhitelistString(entry, "packageId", out var packageId) ||
                    !WhitelistString(entry, "assemblyName", out var assemblyName) ||
                    !WhitelistString(entry, "operationId", out var operationId) ||
                    version is null || version.Type != JTokenType.Integer ||
                    version.Value<long>() < 1)
                {
                    return false;
                }
                result.Add(new OperationWhitelistEntry(
                    packageId!, assemblyName!, operationId!, version.Value<long>()));
            }
            entries = result;
            return true;
        }

        /// <summary><c>extensions.whitelist.typeProviders</c>; empty when absent.</summary>
        public bool TryGetWhitelistedTypeProviders(out IReadOnlyList<TypeProviderWhitelistEntry> entries)
        {
            entries = Array.Empty<TypeProviderWhitelistEntry>();
            if (!TryWhitelistObject(out var whitelist))
            {
                return false;
            }
            var member = whitelist?["typeProviders"];
            if (member is null)
            {
                return true;
            }
            if (member is not JArray array)
            {
                return false;
            }
            var result = new List<TypeProviderWhitelistEntry>(array.Count);
            foreach (var element in array)
            {
                if (element is not JObject entry ||
                    !WhitelistString(entry, "packageId", out var packageId) ||
                    !WhitelistString(entry, "providerId", out var providerId))
                {
                    return false;
                }
                result.Add(new TypeProviderWhitelistEntry(packageId!, providerId!));
            }
            entries = result;
            return true;
        }

        /// <summary>
        /// The live elements of one <c>extensions.whitelist</c> array, each bound
        /// to its JSON object by identity. Empty when the array member is absent
        /// or not an array (an invalid container is reported through
        /// <see cref="TryGetWhitelistedOperations"/> /
        /// <see cref="TryGetWhitelistedTypeProviders"/> instead).
        /// </summary>
        public IReadOnlyList<SettingsWhitelistItem> GetWhitelistItems(SettingsWhitelistKind kind)
        {
            var items = new List<SettingsWhitelistItem>();
            if (WhitelistArray(kind) is JArray array)
            {
                foreach (var element in array)
                {
                    items.Add(new SettingsWhitelistItem(element, kind));
                }
            }
            return items;
        }

        /// <summary>
        /// Replaces the known keys of one <c>operations</c> element in place: the
        /// entry keeps its own object — and every unknown property on it — while
        /// only the four whitelisting members are written.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="item"/> is stale or not an operations element, the
        /// element is not an object, or <paramref name="entry"/> holds an empty
        /// or wildcard string.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
        public void UpdateWhitelistItem(SettingsWhitelistItem item, OperationWhitelistEntry entry)
        {
            ValidateOperationEntry(entry, nameof(entry));
            var obj = RequireLiveItemObject(item, SettingsWhitelistKind.Operations);
            obj["packageId"] = entry.PackageId;
            obj["assemblyName"] = entry.AssemblyName;
            obj["operationId"] = entry.OperationId;
            obj["version"] = entry.Version;
        }

        /// <summary>
        /// Replaces the known keys of one <c>typeProviders</c> element in place;
        /// unknown properties stay on the same object.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="item"/> is stale or not a typeProviders element, the
        /// element is not an object, or <paramref name="entry"/> holds an empty
        /// or wildcard string.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
        public void UpdateWhitelistItem(SettingsWhitelistItem item, TypeProviderWhitelistEntry entry)
        {
            ValidateTypeProviderEntry(entry, nameof(entry));
            var obj = RequireLiveItemObject(item, SettingsWhitelistKind.TypeProviders);
            obj["packageId"] = entry.PackageId;
            obj["providerId"] = entry.ProviderId;
        }

        /// <summary>
        /// Writes one known string member of a live whitelist element as the
        /// human's explicit edit of that field: only the named member is
        /// written, so a repair works field by field while the members the
        /// human did not touch — valid, invalid or missing — stay exactly as
        /// stored, and unknown properties stay on the same object.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="item"/> is stale or not an object,
        /// <paramref name="key"/> is not a string whitelist field of the
        /// element, or <paramref name="value"/> is empty or holds a wildcard.
        /// </exception>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public void SetWhitelistItemMember(
            SettingsWhitelistItem item, string key, string value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var obj = RequireLiveItemObject(
                item, RequireMemberKey(item, key, integer: false));
            RequireWhitelistString(value, nameof(value));
            obj[key] = value;
        }

        /// <summary>
        /// Writes the <c>version</c> member of a live <c>operations</c> element
        /// as the human's explicit edit of that field — only <c>version</c> is
        /// written, so a missing or invalid version is never invented by an
        /// edit to another field.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="item"/> is stale or not an object,
        /// <paramref name="key"/> is not <c>"version"</c> on an operations
        /// element, or <paramref name="value"/> is less than 1.
        /// </exception>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public void SetWhitelistItemMember(
            SettingsWhitelistItem item, string key, long value)
        {
            var obj = RequireLiveItemObject(
                item, RequireMemberKey(item, key, integer: true));
            if (value < 1)
            {
                throw new ArgumentException(
                    "Whitelist entry version must be an integer >= 1.", nameof(value));
            }
            obj[key] = value;
        }

        /// <summary>
        /// Removes one whitelist element. Removal is refused for an element that
        /// carries unknown properties — dropping it would lose OW-CFG-003 data the
        /// human save must keep; the file has to be edited by hand for that.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="item"/> is stale.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The element carries unknown properties.
        /// </exception>
        public void RemoveWhitelistItem(SettingsWhitelistItem item)
        {
            RequireLiveItem(item, out _);
            if (item!.HasUnknownProperties)
            {
                throw new InvalidOperationException(
                    "The entry contains keys OpWeave does not understand; remove them in the file first.");
            }
            item.Element.Remove();
        }

        /// <summary>
        /// Moves one whitelist element to <paramref name="newIndex"/> inside its
        /// array. The element keeps its own object and unknown properties.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="item"/> is stale.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
        public void MoveWhitelistItem(SettingsWhitelistItem item, int newIndex)
        {
            RequireLiveItem(item, out _);
            if (item!.Element.Parent is not JArray array)
            {
                throw new ArgumentException(
                    "The whitelist item is no longer inside its array.", nameof(item));
            }
            var index = Math.Max(0, Math.Min(newIndex, array.Count - 1));
            item.Element.Remove();
            array.Insert(index, item.Element);
        }

        /// <summary>
        /// Appends a new <c>operations</c> entry holding only the four known keys.
        /// An absent container chain is created; an invalid
        /// <c>extensions</c>/<c>whitelist</c>/<c>operations</c> member is rebuilt
        /// as part of this explicit edit inside it.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="entry"/> holds an empty or wildcard string.</exception>
        public void AddWhitelistItem(OperationWhitelistEntry entry)
        {
            ValidateOperationEntry(entry, nameof(entry));
            EnsureWhitelistArray(SettingsWhitelistKind.Operations).Add(new JObject
            {
                ["packageId"] = entry.PackageId,
                ["assemblyName"] = entry.AssemblyName,
                ["operationId"] = entry.OperationId,
                ["version"] = entry.Version,
            });
        }

        /// <summary>
        /// Appends a new <c>typeProviders</c> entry holding only the two known keys.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="entry"/> holds an empty or wildcard string.</exception>
        public void AddWhitelistItem(TypeProviderWhitelistEntry entry)
        {
            ValidateTypeProviderEntry(entry, nameof(entry));
            EnsureWhitelistArray(SettingsWhitelistKind.TypeProviders).Add(new JObject
            {
                ["packageId"] = entry.PackageId,
                ["providerId"] = entry.ProviderId,
            });
        }

        /// <summary>
        /// The human's explicit resolution of a v1 <c>"review"</c> migration that
        /// found stored whitelist entries: keep the entries (they become active
        /// under the migrated whitelist mode, or stay unused when the human also
        /// chose <c>auto</c>). Without this call or the removal of every stored
        /// element, saving stays blocked (SPEC §H4.1: review migrates to the safe
        /// side; activating or dropping entries silently is neither safe nor
        /// lossless).
        /// </summary>
        public void KeepV1WhitelistEntries() => _v1WhitelistKept = true;

        /// <summary>
        /// Replaces <c>extensions.whitelist.operations</c> as one explicit edit.
        /// New entries pair with existing element objects — by equal known keys
        /// first, then in place — so a kept or edited entry retains its unknown
        /// properties on its own object. The edit is refused when it would drop
        /// an element that carries unknown properties.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// An entry holds an empty or wildcard string, or the edit would remove
        /// an element that carries unknown properties.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
        public void SetWhitelistedOperations(IEnumerable<OperationWhitelistEntry> entries)
        {
            if (entries is null)
            {
                throw new ArgumentNullException(nameof(entries));
            }
            var built = new List<JObject>();
            foreach (var entry in entries)
            {
                ValidateOperationEntry(entry, nameof(entries));
                built.Add(new JObject
                {
                    ["packageId"] = entry.PackageId,
                    ["assemblyName"] = entry.AssemblyName,
                    ["operationId"] = entry.OperationId,
                    ["version"] = entry.Version,
                });
            }
            ReplaceWhitelistArray(SettingsWhitelistKind.Operations, built);
        }

        /// <summary>
        /// Replaces <c>extensions.whitelist.typeProviders</c> as one explicit edit,
        /// under the same pairing and drop rules as
        /// <see cref="SetWhitelistedOperations"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// An entry holds an empty or wildcard string, or the edit would remove
        /// an element that carries unknown properties.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
        public void SetWhitelistedTypeProviders(IEnumerable<TypeProviderWhitelistEntry> entries)
        {
            if (entries is null)
            {
                throw new ArgumentNullException(nameof(entries));
            }
            var built = new List<JObject>();
            foreach (var entry in entries)
            {
                ValidateTypeProviderEntry(entry, nameof(entries));
                built.Add(new JObject
                {
                    ["packageId"] = entry.PackageId,
                    ["providerId"] = entry.ProviderId,
                });
            }
            ReplaceWhitelistArray(SettingsWhitelistKind.TypeProviders, built);
        }

        // ── String-array fields ──────────────────────────────────────────────

        /// <summary>
        /// A string-array settings key; its documented default applies when absent.
        /// <see cref="SettingsStringListKey.AllowedPackageSources"/> entries must also
        /// satisfy the closed SG-19 grammar.
        /// </summary>
        public bool TryGetStringList(SettingsStringListKey key, out IReadOnlyList<string> values)
        {
            var name = StringListKeyName(key);
            var member = _working[name];
            if (member is null)
            {
                values = StringListDefault(key);
                return true;
            }
            if (member is not JArray array)
            {
                values = StringListDefault(key);
                return false;
            }
            var result = new List<string>(array.Count);
            foreach (var element in array)
            {
                if (element.Type != JTokenType.String)
                {
                    values = StringListDefault(key);
                    return false;
                }
                var entry = element.Value<string>()!;
                if (key == SettingsStringListKey.AllowedPackageSources &&
                    !PackageSourceRule.TryParse(entry, out _))
                {
                    values = StringListDefault(key);
                    return false;
                }
                result.Add(entry);
            }
            values = result;
            return true;
        }

        /// <summary>Replaces a string-array settings key as one explicit edit.</summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="key"/> is <see cref="SettingsStringListKey.AllowedPackageSources"/>
        /// and a value violates the closed SG-19 grammar.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="values"/> or an element is null.</exception>
        public void SetStringList(SettingsStringListKey key, IEnumerable<string> values)
        {
            if (values is null)
            {
                throw new ArgumentNullException(nameof(values));
            }
            var array = new JArray();
            foreach (var value in values)
            {
                if (value is null)
                {
                    throw new ArgumentNullException(nameof(values));
                }
                RequireStringListEntry(key, value, nameof(values));
                array.Add(value);
            }
            _working[StringListKeyName(key)] = array;
            // The replaced member orphaned every draft element bound to it.
            _pendingStringListDrafts.Remove(key);
        }

        /// <summary>
        /// One line per stored element of a string-array settings key — the raw
        /// display text for one element, as the per-element draft shows it. A
        /// JSON string element maps to its own text (canonical JSON when the
        /// text itself carries a line break, so one stored element can never
        /// silently split into two displayed entries); every other element maps
        /// to its canonical JSON, so an invalid element is always shown raw and
        /// is never silently dropped by an edit (rule table A2-25/P-5). An
        /// absent member yields the documented defaults; a non-array member a
        /// single raw line.
        /// </summary>
        public IReadOnlyList<string> GetStringListEditLines(SettingsStringListKey key)
        {
            var member = _working[StringListKeyName(key)];
            if (member is null)
            {
                return StringListDefault(key);
            }
            if (member is not JArray array)
            {
                return new[] { StringListEditLine(member) };
            }
            var lines = new List<string>(array.Count);
            foreach (var element in array)
            {
                lines.Add(StringListEditLine(element));
            }
            return lines;
        }

        // The per-line draft form of one raw element: a string maps to its own
        // text — canonical JSON when it carries a line break, so a stored
        // element can never silently split into two draft lines; any other
        // value shows its canonical JSON.
        private static string StringListEditLine(JToken element)
        {
            if (element.Type == JTokenType.String)
            {
                var text = element.Value<string>()!;
                return text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0
                    ? text
                    : JsonValue.From(element).CanonicalText;
            }
            return JsonValue.From(element).CanonicalText;
        }

        /// <summary>
        /// The per-element draft of a string-array settings key the per-element
        /// editor is seeded with (the same display texts as
        /// <see cref="GetStringListEditLines"/>, bound to their stored
        /// elements): one element per stored array element — invalid ones keep
        /// their raw display text — a single element for a stored non-array
        /// member, or the documented defaults as added elements for an absent
        /// member. When a refused draft was held through
        /// <see cref="HoldStringListDraft"/> the held elements are returned
        /// instead of a fresh seed, so the human's unaccepted edits stay
        /// visible for repair. Commit the edited draft through
        /// <see cref="SetStringListDraft"/>.
        /// </summary>
        public List<SettingsStringListDraftElement> GetStringListDraft(
            SettingsStringListKey key)
        {
            if (_pendingStringListDrafts.TryGetValue(key, out var pending))
            {
                return new List<SettingsStringListDraftElement>(pending);
            }
            return SeedStringListDraft(key);
        }

        // The draft seeded from the stored member: one element per stored array
        // element — invalid ones keep their raw display text — a single element
        // for a stored non-array member, or the documented defaults as added
        // elements for an absent member.
        private List<SettingsStringListDraftElement> SeedStringListDraft(
            SettingsStringListKey key)
        {
            var member = _working[StringListKeyName(key)];
            var draft = new List<SettingsStringListDraftElement>();
            if (member is null)
            {
                foreach (var value in StringListDefault(key))
                {
                    draft.Add(SettingsStringListDraftElement.Added(value, key));
                }
                return draft;
            }
            if (member is not JArray array)
            {
                draft.Add(SettingsStringListDraftElement.Stored(
                    member, isArrayElement: false, StringListEditLine(member), key));
                return draft;
            }
            foreach (var element in array)
            {
                draft.Add(SettingsStringListDraftElement.Stored(
                    element, isArrayElement: true, StringListEditLine(element), key));
            }
            return draft;
        }

        /// <summary>
        /// Commits a per-element string-array draft: each element resolves on
        /// its own — a changed or added element becomes a string, an untouched
        /// stored element is written back verbatim — and the stored list is
        /// replaced only once the complete draft resolves to a valid string
        /// list. An untouched invalid element is therefore never laundered
        /// into a string by an edit beside it: the commit is refused while an
        /// invalid element stays unresolved, leaving its OW-CFG-002 in place
        /// (SPEC §H4.1 fail-closed repair). A successful commit clears a draft
        /// previously held by <see cref="HoldStringListDraft"/>; a refused
        /// draft changes nothing — the UI decides whether to keep it pending
        /// through <see cref="HoldStringListDraft"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// An element is stale or still unresolved — an untouched non-string
        /// element or non-array member, or a string value that violates the
        /// closed SG-19 grammar for
        /// <see cref="SettingsStringListKey.AllowedPackageSources"/>.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="elements"/> or an element is null.
        /// </exception>
        public void SetStringListDraft(
            SettingsStringListKey key, IReadOnlyList<SettingsStringListDraftElement> elements)
        {
            if (elements is null)
            {
                throw new ArgumentNullException(nameof(elements));
            }
            var name = StringListKeyName(key);
            var member = _working[name];
            var array = new JArray();
            foreach (var element in elements)
            {
                if (element is null)
                {
                    throw new ArgumentNullException(nameof(elements));
                }
                if (element.Edited)
                {
                    RequireStringListEntry(key, element.Text, nameof(elements));
                    array.Add(element.Text);
                    continue;
                }
                var original = element.OriginalToken!;
                var live = element.IsArrayElement
                    ? ReferenceEquals(original.Parent, member)
                    : ReferenceEquals(original, member);
                if (!live)
                {
                    throw new ArgumentException(
                        "The draft element is no longer part of this document.",
                        nameof(elements));
                }
                if (!element.IsArrayElement || original.Type != JTokenType.String)
                {
                    throw new ArgumentException(
                        "The draft still has an invalid entry shown as raw JSON; " +
                        "fix or remove it before the edit is accepted.",
                        nameof(elements));
                }
                RequireStringListEntry(
                    key, original.Value<string>()!, nameof(elements));
                array.Add(original.DeepClone());
            }
            _working[name] = array;
            _pendingStringListDrafts.Remove(key);
        }

        /// <summary>
        /// Holds a refused <see cref="SetStringListDraft"/> draft as the key's
        /// pending edit: <see cref="GetStringListDraft"/> keeps returning it so
        /// the human's unaccepted edits stay visible for repair, and it blocks
        /// the save — <see cref="SaveBlockReasons"/> lists it and
        /// <see cref="CanSave"/> is false — until the draft commits, reverts to
        /// the stored list, or is dropped by <see cref="DiscardStringListDraft"/>
        /// or a document reload. A draft identical to the stored elements, or
        /// one whose stored elements are no longer live, holds nothing: there
        /// is no pending edit to keep.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="elements"/> or an element is null.
        /// </exception>
        public void HoldStringListDraft(
            SettingsStringListKey key, IReadOnlyList<SettingsStringListDraftElement> elements)
        {
            if (elements is null)
            {
                throw new ArgumentNullException(nameof(elements));
            }
            foreach (var element in elements)
            {
                if (element is null)
                {
                    throw new ArgumentNullException(nameof(elements));
                }
            }
            if (!StringListDraftElementsLive(key, elements))
            {
                return;
            }
            if (StringListDraftMatchesStored(key, elements))
            {
                _pendingStringListDrafts.Remove(key);
                return;
            }
            _pendingStringListDrafts[key] =
                new List<SettingsStringListDraftElement>(elements);
        }

        /// <summary>
        /// Drops a held string-list draft without writing it: the stored list
        /// is untouched, the pending edit stops blocking the save and the next
        /// <see cref="GetStringListDraft"/> re-seeds from the stored elements.
        /// </summary>
        public void DiscardStringListDraft(SettingsStringListKey key) =>
            _pendingStringListDrafts.Remove(key);

        /// <summary>
        /// True while a refused string-list draft is held for the key — the
        /// pending edit that blocks <see cref="Save"/> until it is committed,
        /// reverted or discarded.
        /// </summary>
        public bool HasPendingStringListDraft(SettingsStringListKey key) =>
            _pendingStringListDrafts.ContainsKey(key);

        // True when every stored element the draft was seeded from is still
        // the live content of the settings member — a stale draft (a replaced
        // member or detached elements) can never be held.
        private bool StringListDraftElementsLive(
            SettingsStringListKey key, IReadOnlyList<SettingsStringListDraftElement> elements)
        {
            var member = _working[StringListKeyName(key)];
            foreach (var element in elements)
            {
                var original = element.OriginalToken;
                if (original is null)
                {
                    continue;
                }
                var live = element.IsArrayElement
                    ? ReferenceEquals(original.Parent, member)
                    : ReferenceEquals(original, member);
                if (!live)
                {
                    return false;
                }
            }
            return true;
        }

        // True when the draft carries no uncommitted change: every element is
        // the stored element it was seeded from, at its stored position, with
        // its seeded text — a refused draft like that is a reverted edit, not
        // a pending one.
        private bool StringListDraftMatchesStored(
            SettingsStringListKey key, IReadOnlyList<SettingsStringListDraftElement> elements)
        {
            var seed = SeedStringListDraft(key);
            if (seed.Count != elements.Count)
            {
                return false;
            }
            for (var i = 0; i < seed.Count; i++)
            {
                if (!ReferenceEquals(seed[i].OriginalToken, elements[i].OriginalToken) ||
                    !string.Equals(seed[i].Text, elements[i].Text, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        // ── Working-copy inspection ──────────────────────────────────────────

        /// <summary>
        /// The working copy's raw member at <paramref name="memberPath"/> (each step
        /// an object member name), or <c>null</c> when absent — used to show the
        /// human the invalid content behind an error JsonPath.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="memberPath"/> is null.</exception>
        public JsonValue? GetRawValue(params string[] memberPath)
        {
            if (memberPath is null)
            {
                throw new ArgumentNullException(nameof(memberPath));
            }
            JToken? current = _working;
            foreach (var step in memberPath)
            {
                if (current is not JObject obj)
                {
                    return null;
                }
                current = obj[step];
                if (current is null)
                {
                    return null;
                }
            }
            return JsonValue.From(current);
        }

        // ── Save ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the patched document to <see cref="FilePath"/> as a v2 settings
        /// file and reports the typed outcome (SPEC §H4.1):
        /// <see cref="SettingsSaveStatus.Blocked"/> while any
        /// <see cref="SaveBlockReasons"/> entry stands — the file is left
        /// untouched; <see cref="SettingsSaveStatus.ReplaceConfirmationRequired"/>
        /// when the file is unreadable as settings (OW-CFG-001) and
        /// <paramref name="replaceConfirmed"/> is <c>false</c>;
        /// <see cref="SettingsSaveStatus.ChangedExternally"/> when the on-disk
        /// content changed since load; <see cref="SettingsSaveStatus.WriteFailed"/>
        /// when the atomic write itself fails; and
        /// <see cref="SettingsSaveStatus.Saved"/> carrying the post-save load —
        /// the file is written first, so a failed result means the saved file
        /// still holds content the human did not fix.
        /// </summary>
        public OpWeaveSettingsSaveOutcome Save(bool replaceConfirmed = false)
        {
            var reasons = ComputeSaveBlockReasons();
            if (reasons.Count != 0)
            {
                return OpWeaveSettingsSaveOutcome.Blocked(
                    LoadResult.Diagnostics, string.Join(" ", reasons));
            }
            if (!replaceConfirmed && RequiresReplaceConfirmation)
            {
                return OpWeaveSettingsSaveOutcome.ConfirmationRequired(LoadResult.Diagnostics);
            }
            if (!FileUnchanged())
            {
                return OpWeaveSettingsSaveOutcome.ChangedExternally();
            }
            if (SourceWasV1)
            {
                ApplyV1SaveMigration();
            }
            _working["version"] = OpWeaveSettings.CurrentVersion;
            var text = _working.ToString(Newtonsoft.Json.Formatting.Indented);

            var directory = Path.GetDirectoryName(FilePath);
            var tempPath = FilePath + ".tmp";
            try
            {
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                // Every write attempt rewrites the temporary replacement and
                // re-proves the destination is still the loaded content, then
                // replaces or moves atomically: a transient external hold is
                // retried within the bounded budget, a hold that releases into
                // changed or vanished content is refused rather than
                // overwritten, and after an ERROR_UNABLE_TO_REMOVE_REPLACED
                // (1175) the next attempt provably publishes fresh replacement
                // bytes.
                long firstFailureAt = -1;
                var nextDelay = SaveWriteRetryFirstDelayMilliseconds;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (attempt > 1 && !FileUnchanged())
                        {
                            TryDeleteFile(tempPath);
                            return OpWeaveSettingsSaveOutcome.ChangedExternally();
                        }
                        File.WriteAllText(tempPath, text);
                        var targetExists = File.Exists(FilePath);
                        var simulated =
                            SaveWriteStepProbe?.Invoke(tempPath, FilePath, targetExists);
                        if (simulated is not null)
                        {
                            throw simulated;
                        }
                        if (targetExists)
                        {
                            File.Replace(tempPath, FilePath, null);
                        }
                        else
                        {
                            File.Move(tempPath, FilePath);
                        }
                        break;
                    }
                    catch (Exception e) when (IsTransientWriteFailure(e))
                    {
                        var now = SaveRetryClock();
                        if (firstFailureAt < 0)
                        {
                            firstFailureAt = now;
                        }
                        if (now - firstFailureAt + nextDelay >
                            SaveWriteRetryBudgetMilliseconds)
                        {
                            throw;
                        }
                        SaveRetrySleep(nextDelay);
                        nextDelay = Math.Min(
                            nextDelay * 2, SaveWriteRetryMaxDelayMilliseconds);
                    }
                    catch (Exception e) when (
                        e is FileNotFoundException ||
                        e is DirectoryNotFoundException ||
                        IsAlreadyExists(e))
                    {
                        // The destination or its directory vanished, or a
                        // destination appeared, between the re-verification
                        // and the write — the on-disk change the save refuses
                        // on. A vanished temp, or a destination that is
                        // provably still the loaded content, is a write fault.
                        TryDeleteFile(tempPath);
                        return FileUnchanged()
                            ? OpWeaveSettingsSaveOutcome.WriteFailure(e.GetType().Name)
                            : OpWeaveSettingsSaveOutcome.ChangedExternally();
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException ||
                ex is DirectoryNotFoundException)
            {
                TryDeleteFile(tempPath);
                return OpWeaveSettingsSaveOutcome.WriteFailure(ex.GetType().Name);
            }

            // The document now reflects the file it just wrote: a second Save on
            // the same document re-checks against the new content instead of
            // refusing as externally changed.
            _originalText = text;
            _readState = FileReadState.Read;
            SourceWasV1 = false;
            _v1ReviewPendingElements.Clear();
            _v1WhitelistKept = false;
            _discoveryModeEdited = false;
            _pendingStringListDrafts.Clear();
            _original = (JObject)_working.DeepClone();
            return OpWeaveSettingsSaveOutcome.Saved(OpWeaveSettingsLoader.Parse(text));
        }

        // Internal test seams for the bounded retry of the save's write step:
        // SaveWriteStepProbe is invoked on every attempt with (temp path,
        // target path, target exists); a non-null return is thrown instead of
        // the real write step, so tests inject a transient failure
        // deterministically. SaveRetryClock is the clock the retry budget
        // measures elapsed time against and SaveRetrySleep the delay primitive
        // it waits through; tests substitute a fake clock and a fake delay —
        // typically one that advances it — so the budget is exercised quickly
        // and deterministically. Production callers leave them at their
        // defaults.
        internal Func<string, string, bool, Exception?>? SaveWriteStepProbe { get; set; }
        internal Func<long> SaveRetryClock { get; set; } =
            () => s_saveRetryStopwatch.ElapsedMilliseconds;
        internal Action<int> SaveRetrySleep { get; set; } =
            System.Threading.Thread.Sleep;

        /// <summary>
        /// The exact file text a <see cref="Save"/> of a missing file writes —
        /// the complete v2 defaults document of SPEC §H4.1.
        /// <see cref="OpWeaveProjectSetup"/> writes it through an atomic
        /// create-if-absent instead of Load+Save: its destination may not be
        /// created or touched when a file already exists (SPEC §M.3), and the
        /// document save sequence has no create-only mode.
        /// </summary>
        internal static string DefaultsFileText() =>
            BuildV2Skeleton().ToString(Newtonsoft.Json.Formatting.Indented);

        // ── Internals ────────────────────────────────────────────────────────

        private static bool HasDiagnostic(Result<OpWeaveSettings> result, string code)
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostic.Code == code && diagnostic.Severity == Severity.Error)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsV1Version(JToken? versionToken) =>
            versionToken is null ||
            (versionToken.Type == JTokenType.Integer && versionToken.Value<long>() == 1);

        private List<string> ComputeSaveBlockReasons()
        {
            var reasons = new List<string>();
            AppendSaveBlockReasons(reasons);
            foreach (var pending in _pendingStringListDrafts)
            {
                reasons.Add(
                    "The " + StringListKeyName(pending.Key) + " list has edits " +
                    "that were not accepted; resolve or revert them before saving.");
            }
            return reasons;
        }

        // The fixed-English reasons the fail-closed save rules refuse a save.
        // A file that could not be read at load refuses every write until it is
        // re-read. For a v1 file the rules block while the migration has no
        // lossless v2 write: an invalid or non-string trust and a non-object
        // extensions or a stored discoveryMode under a valid trust need the
        // human's explicit discoveryMode choice, and a "review" migration keeps
        // blocking while stored whitelist elements await a keep/remove choice.
        private void AppendSaveBlockReasons(List<string> reasons)
        {
            if (_readState == FileReadState.Failed)
            {
                reasons.Add(
                    "The settings file could not be read; no write is possible until it is read again.");
                return;
            }
            if (!SourceWasV1 || _original is null)
            {
                return;
            }
            var trust = _original["customOperationTrust"];
            if (trust is null)
            {
                return;
            }
            var trustString = trust.Type == JTokenType.String
                ? trust.Value<string>()
                : null;
            if (trustString is not ("auto" or "review"))
            {
                if (!_discoveryModeEdited)
                {
                    reasons.Add(
                        "customOperationTrust has an invalid value; choose a discovery mode explicitly to resolve it before saving.");
                }
                return;
            }
            var extensions = _original["extensions"];
            if (extensions is not null && extensions is not JObject)
            {
                if (_working["extensions"] is not JObject)
                {
                    reasons.Add(
                        "extensions is not a JSON object; edit an extensions value to rebuild it before saving.");
                }
            }
            else if ((extensions as JObject)?["discoveryMode"] is not null && !_discoveryModeEdited)
            {
                reasons.Add(
                    "The file has both customOperationTrust and extensions.discoveryMode; choose a discovery mode explicitly before saving.");
            }
            if (trustString == "review" && !_v1WhitelistKept && HasPendingReviewElements())
            {
                reasons.Add(
                    "customOperationTrust \"review\" migrates to whitelist mode with an empty whitelist, but the file already has whitelist entries; keep them or remove them before saving.");
            }
        }

        // The v1→v2 migration applied to the working copy at save time (SPEC
        // §H4.1): a valid trust maps to extensions.discoveryMode — creating the
        // extensions object when absent — or is superseded by the human's
        // explicit choice; the retired customOperationTrust key is removed in
        // every saved v1 document because its meaning is now carried by
        // extensions.discoveryMode. Blocked states are filtered out before this
        // runs, so the only members touched here are the ones the migration
        // defines; everything else — invalid values and unknown keys at any
        // depth — stays a verbatim patch of the original tree.
        private void ApplyV1SaveMigration()
        {
            _working.Remove("customOperationTrust");
            if (_discoveryModeEdited || _original is null)
            {
                return;
            }
            var trust = _original["customOperationTrust"];
            if (trust is null || trust.Type != JTokenType.String)
            {
                return;
            }
            var value = trust.Value<string>();
            var mode = value == "auto" ? "auto" : value == "review" ? "whitelist" : null;
            if (mode is null)
            {
                return;
            }
            var extensions = _working["extensions"] as JObject;
            if (extensions is null)
            {
                if (_working["extensions"] is not null)
                {
                    // A non-object extensions unresolved at save time is a blocked
                    // state filtered out earlier; this is defensive only.
                    return;
                }
                extensions = new JObject();
                _working["extensions"] = extensions;
            }
            extensions["discoveryMode"] = mode;
        }

        // Elements of extensions.whitelist.operations/.typeProviders in the
        // working copy at load time — the stored whitelist entries a "review"
        // migration would empty. Tracked by object identity: an element stays
        // pending while it is still reachable in the working tree.
        private static void CollectWhitelistElements(JObject working, List<JToken> pending)
        {
            var whitelist = (working["extensions"] as JObject)?["whitelist"] as JObject;
            if (whitelist is null)
            {
                return;
            }
            foreach (var name in new[] { "operations", "typeProviders" })
            {
                if (whitelist[name] is JArray array)
                {
                    foreach (var element in array)
                    {
                        pending.Add(element);
                    }
                }
            }
        }

        private bool HasPendingReviewElements()
        {
            foreach (var element in _v1ReviewPendingElements)
            {
                if (IsLive(element))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsLive(JToken token)
        {
            for (var current = token; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, _working))
                {
                    return true;
                }
            }
            return false;
        }

        // The pre-write check that the file is still the one that was loaded:
        // re-read it and compare with the original text. Any difference — new
        // content, a deleted or newly created file, or a read failure — refuses
        // the save so newer on-disk content is never overwritten.
        private bool FileUnchanged()
        {
            string? text;
            try
            {
                text = File.ReadAllText(FilePath);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return _readState == FileReadState.Missing;
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException)
            {
                return false;
            }
            return _readState == FileReadState.Read &&
                string.Equals(text, _originalText, StringComparison.Ordinal);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException)
            {
            }
        }

        // The transient write failures the bounded retry absorbs: a sharing
        // violation (Win32 error 32), a lock violation (33), access denied on
        // a held target (5) or a replaced file that could not be removed
        // (1175, ERROR_UNABLE_TO_REMOVE_REPLACED — whose retry rewrites the
        // replacement file, so the next attempt provably publishes the
        // intended bytes). An IOException carries its Win32 error in the low
        // word of its HResult under FACILITY_WIN32 (0x8007xxxx) or as a bare
        // Win32 code (0x0000xxxx); a matching low word under any other
        // facility is not a Win32 error and is never retried. Error 5 also
        // reaches File.Replace as an UnauthorizedAccessException with HResult
        // 0x80070005 — COR_E_UNAUTHORIZEDACCESS shares that value, so an
        // UnauthorizedAccessException is transient exactly then. Every other
        // failure — including errors 1176 and 1177
        // (ERROR_UNABLE_TO_MOVE_REPLACEMENT[2]), which can leave the
        // destination removed, renamed or carrying merged streams — is not
        // retried.
        internal static bool IsTransientWriteFailure(Exception e)
        {
            if (e is UnauthorizedAccessException)
            {
                return e.HResult == unchecked((int)0x80070005);
            }
            if (e is IOException io)
            {
                var code = Win32ErrorCode(io.HResult);
                return code == 0x20 || code == 0x21 || code == 0x05 || code == 0x0497;
            }
            return false;
        }

        // ERROR_FILE_EXISTS (80) / ERROR_ALREADY_EXISTS (183): a File.Move
        // found a destination that appeared since the re-verification — the
        // external change the save refuses on, not a write fault.
        private static bool IsAlreadyExists(Exception e)
        {
            if (e is not IOException io)
            {
                return false;
            }
            var code = Win32ErrorCode(io.HResult);
            return code == 0x50 || code == 0xB7;
        }

        // The Win32 error an IOException's HResult encodes: the low word when
        // the facility is FACILITY_WIN32 (0x8007xxxx) or the HResult is a bare
        // Win32 code (0x0000xxxx, the form new IOException(message, code)
        // stores); -1 under any other facility, where a matching low word is
        // not a Win32 error.
        private static int Win32ErrorCode(int hresult)
        {
            var facility = hresult & unchecked((int)0xFFFF0000);
            if (facility != unchecked((int)0x80070000) && facility != 0)
            {
                return -1;
            }
            return hresult & 0xFFFF;
        }

        private JArray? WhitelistArray(SettingsWhitelistKind kind) =>
            ((_working["extensions"] as JObject)?["whitelist"] as JObject)?[
                kind == SettingsWhitelistKind.Operations ? "operations" : "typeProviders"]
                as JArray;

        private JArray EnsureWhitelistArray(SettingsWhitelistKind kind)
        {
            var whitelist = EnsureObjectForWrite(
                EnsureObjectForWrite(_working, "extensions"), "whitelist");
            var name = kind == SettingsWhitelistKind.Operations
                ? "operations"
                : "typeProviders";
            if (whitelist[name] is JArray existing)
            {
                return existing;
            }
            var created = new JArray();
            whitelist[name] = created;
            return created;
        }

        private JObject RequireLiveItemObject(SettingsWhitelistItem? item, SettingsWhitelistKind kind)
        {
            var element = RequireLiveItem(item, out var itemKind);
            if (itemKind != kind)
            {
                throw new ArgumentException(
                    "The whitelist item belongs to a different whitelist array.", nameof(item));
            }
            if (element is not JObject obj)
            {
                throw new ArgumentException(
                    "The whitelist element is not an object; remove it and add a new entry.",
                    nameof(item));
            }
            return obj;
        }

        private JToken RequireLiveItem(SettingsWhitelistItem? item, out SettingsWhitelistKind kind)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }
            kind = item.Kind;
            if (!IsLive(item.Element))
            {
                throw new ArgumentException(
                    "The whitelist item is no longer part of this document.", nameof(item));
            }
            return item.Element;
        }

        // The whitelist field a member edit targets: it must be one of the
        // element's known keys and match the value kind — "version" takes an
        // integer (operations elements only); every other known key takes a
        // string.
        private static SettingsWhitelistKind RequireMemberKey(
            SettingsWhitelistItem? item, string? key, bool integer)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (Array.IndexOf(item.KnownKeys, key) < 0 ||
                (integer ? key != "version" : key == "version"))
            {
                throw new ArgumentException(
                    "The member is not a whitelist field of this entry.", nameof(key));
            }
            return item.Kind;
        }

        // Replaces one whitelist array with the edited entries: each built entry
        // first claims the first unconsumed existing element whose known members
        // are equal (a kept or reordered entry), then the unconsumed object at
        // its own position (the in-place edit). Paired elements keep their
        // object and unknown properties; the edit is refused when it would drop
        // an element that carries unknown properties.
        private void ReplaceWhitelistArray(SettingsWhitelistKind kind, List<JObject> built)
        {
            var knownKeys = kind == SettingsWhitelistKind.Operations
                ? SettingsWhitelistItem.OperationKeys
                : SettingsWhitelistItem.TypeProviderKeys;
            var name = kind == SettingsWhitelistKind.Operations
                ? "operations"
                : "typeProviders";
            var existing = WhitelistArray(kind);
            var elements = new List<JToken>();
            if (existing is not null)
            {
                foreach (var element in existing)
                {
                    elements.Add(element);
                }
            }

            var sources = new JObject?[built.Count];
            var consumed = new bool[elements.Count];
            for (var i = 0; i < built.Count; i++)
            {
                for (var j = 0; j < elements.Count; j++)
                {
                    if (!consumed[j] && elements[j] is JObject candidate &&
                        KnownFieldsEqual(candidate, built[i], knownKeys))
                    {
                        sources[i] = candidate;
                        consumed[j] = true;
                        break;
                    }
                }
            }
            for (var i = 0; i < built.Count; i++)
            {
                if (sources[i] is null && i < elements.Count && !consumed[i] &&
                    elements[i] is JObject same)
                {
                    sources[i] = same;
                    consumed[i] = true;
                }
            }
            for (var j = 0; j < elements.Count; j++)
            {
                if (!consumed[j] && elements[j] is JObject obj &&
                    HasUnknownKeys(obj, knownKeys))
                {
                    throw new ArgumentException(
                        "Removing the whitelist entry would drop keys OpWeave does not understand; remove them in the file first.");
                }
            }

            var array = new JArray();
            for (var i = 0; i < built.Count; i++)
            {
                var source = sources[i];
                if (source is null)
                {
                    array.Add(built[i]);
                    continue;
                }
                // A kept entry keeps its own object: write the edited known keys
                // onto it and leave every unknown property in place.
                source.Remove();
                foreach (var key in knownKeys)
                {
                    source[key] = built[i][key]!.DeepClone();
                }
                array.Add(source);
            }
            var whitelist = EnsureObjectForWrite(
                EnsureObjectForWrite(_working, "extensions"), "whitelist");
            whitelist[name] = array;
        }

        private static bool HasUnknownKeys(JObject obj, string[] knownKeys)
        {
            foreach (var property in obj.Properties())
            {
                if (Array.IndexOf(knownKeys, property.Name) < 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool KnownFieldsEqual(
            JObject candidate, JObject built, string[] knownKeys)
        {
            foreach (var key in knownKeys)
            {
                var member = candidate[key];
                if (member is null || !JToken.DeepEquals(member, built[key]))
                {
                    return false;
                }
            }
            return true;
        }

        // Every member of the v2 skeleton (SPEC §H4.1 example = the documented
        // defaults), in the SPEC example's order.
        private static JObject BuildV2Skeleton()
        {
            var agentPolicy = OpWeaveSettings.DefaultAgentPolicy();
            var humanPolicy = OpWeaveSettings.DefaultHumanPolicy();
            var agent = new JObject();
            var human = new JObject();
            foreach (var effect in OpWeaveSettings.KnownEffects)
            {
                agent[effect] = LevelText(agentPolicy[effect]);
                human[effect] = LevelText(humanPolicy[effect]);
            }
            return new JObject
            {
                ["version"] = OpWeaveSettings.CurrentVersion,
                ["policy"] = new JObject { ["agent"] = agent, ["human"] = human },
                ["destructiveRequiresAsk"] = true,
                ["extensions"] = new JObject
                {
                    ["discoveryMode"] = "auto",
                    ["integrityLevel"] = "weak",
                    ["whitelist"] = new JObject
                    {
                        ["operations"] = new JArray(),
                        ["typeProviders"] = new JArray(),
                    },
                },
                ["allowedPaths"] = new JArray("Assets/"),
                ["codePaths"] = new JArray("Assets/Scripts/"),
                ["customOperationFolders"] = new JArray("Assets/OpWeave/Editor/Operations/"),
                ["allowedPackageSources"] = new JArray("registry:com.unity.*"),
                ["disabledOperations"] = new JArray(),
                ["agentLanguage"] = "en",
                ["debugLogging"] = false,
                ["tickBudgetMs"] = 50L,
                ["retainRuns"] = 50L,
                ["retainMegabytes"] = 200L,
                ["approvalTimeoutMinutes"] = 1440L,
            };
        }

        // Removes every v2-known member from an unknown-version document so a
        // confirmed replace keeps only v2-unknown keys: known object members
        // (policy, extensions, agent/human maps) are kept as objects and stripped
        // recursively so nested unknown keys survive; known scalar/array members —
        // including the whitelist arrays, whose per-entry unknown keys are
        // meaningless without the required entry members — are removed.
        private static void StripKnownMembers(JObject root)
        {
            foreach (var member in s_knownRootKeys)
            {
                if (member is "policy" or "extensions")
                {
                    continue; // handled recursively below
                }
                root.Remove(member);
            }
            StripPolicyObject(root["policy"]);
            StripExtensionsObject(root["extensions"]);
        }

        private static void StripPolicyObject(JToken? policy)
        {
            if (policy is null)
            {
                return;
            }
            if (policy is not JObject policyObject)
            {
                policy.Remove();
                return;
            }
            StripPolicyMap(policyObject, "agent");
            StripPolicyMap(policyObject, "human");
        }

        private static void StripPolicyMap(JObject policy, string name)
        {
            var map = policy[name];
            if (map is null)
            {
                return;
            }
            if (map is not JObject mapObject)
            {
                map.Remove();
                return;
            }
            foreach (var effect in OpWeaveSettings.KnownEffects)
            {
                mapObject.Remove(effect);
            }
        }

        private static void StripExtensionsObject(JToken? extensions)
        {
            if (extensions is null)
            {
                return;
            }
            if (extensions is not JObject extensionsObject)
            {
                extensions.Remove();
                return;
            }
            extensionsObject.Remove("discoveryMode");
            extensionsObject.Remove("integrityLevel");
            var whitelist = extensionsObject["whitelist"];
            if (whitelist is null)
            {
                return;
            }
            if (whitelist is not JObject whitelistObject)
            {
                whitelist.Remove();
                return;
            }
            whitelistObject.Remove("operations");
            whitelistObject.Remove("typeProviders");
        }

        // Deep-merges the v2 skeleton into a stripped unknown-version document:
        // known keys take the defaults (unknown-version semantics are never
        // trusted), unknown keys are never touched.
        private static void MergeSkeletonInto(JObject target)
        {
            MergeInto(target, BuildV2Skeleton());
        }

        private static void MergeInto(JObject target, JObject source)
        {
            foreach (var member in source.Properties())
            {
                if (target[member.Name] is JObject targetObject &&
                    member.Value is JObject sourceObject)
                {
                    MergeInto(targetObject, sourceObject);
                }
                else
                {
                    target[member.Name] = member.Value.DeepClone();
                }
            }
        }

        private JObject? PolicyMap(PolicySide side) =>
            (_working["policy"] as JObject)?[side == PolicySide.Agent ? "agent" : "human"] as JObject;

        // Creates or replaces the member with an object: an explicit human edit to
        // a field inside it replaces an invalid non-object container.
        private static JObject EnsureObjectForWrite(JObject parent, string name)
        {
            if (parent[name] is JObject existing)
            {
                return existing;
            }
            var created = new JObject();
            parent[name] = created;
            return created;
        }

        // extensions absent → true + null (defaults apply); extensions a non-object
        // → false (invalid container).
        private bool TryExtensionsObject(out JObject? extensions)
        {
            var member = _working["extensions"];
            if (member is null)
            {
                extensions = null;
                return true;
            }
            extensions = member as JObject;
            return extensions is not null;
        }

        private bool TryWhitelistObject(out JObject? whitelist)
        {
            whitelist = null;
            if (!TryExtensionsObject(out var extensions))
            {
                return false;
            }
            var member = extensions?["whitelist"];
            if (member is null)
            {
                return true;
            }
            whitelist = member as JObject;
            return whitelist is not null;
        }

        internal static bool WhitelistString(JObject entry, string key, out string? value)
        {
            var member = entry[key];
            if (member is null || member.Type != JTokenType.String)
            {
                value = null;
                return false;
            }
            var text = member.Value<string>();
            if (text is null || text.Length == 0 || text.IndexOf('*') >= 0)
            {
                value = null;
                return false;
            }
            value = text;
            return true;
        }

        private static void ValidateOperationEntry(OperationWhitelistEntry entry, string paramName)
        {
            RequireWhitelistString(entry.PackageId, paramName);
            RequireWhitelistString(entry.AssemblyName, paramName);
            RequireWhitelistString(entry.OperationId, paramName);
            if (entry.Version < 1)
            {
                throw new ArgumentException(
                    "Whitelist entry version must be an integer >= 1.", paramName);
            }
        }

        private static void ValidateTypeProviderEntry(TypeProviderWhitelistEntry entry, string paramName)
        {
            RequireWhitelistString(entry.PackageId, paramName);
            RequireWhitelistString(entry.ProviderId, paramName);
        }

        private static void RequireWhitelistString(string value, string paramName)
        {
            if (value is null || value.Length == 0 || value.IndexOf('*') >= 0)
            {
                throw new ArgumentException(
                    "Whitelist entry strings must be non-empty and contain no wildcards.",
                    paramName);
            }
        }

        // One committed string-list entry: allowedPackageSources entries must
        // satisfy the closed SG-19 grammar; every other key accepts any string.
        private static void RequireStringListEntry(
            SettingsStringListKey key, string value, string paramName)
        {
            if (key == SettingsStringListKey.AllowedPackageSources &&
                !PackageSourceRule.TryParse(value, out _))
            {
                throw new ArgumentException(
                    "allowedPackageSources entries must match the closed source grammar (registry, git https or git ssh).",
                    paramName);
            }
        }

        private static bool TryGetBoolean(JObject obj, string name, bool fallback, out bool value)
        {
            var member = obj[name];
            if (member is null)
            {
                value = fallback;
                return true;
            }
            if (member.Type == JTokenType.Boolean)
            {
                value = member.Value<bool>();
                return true;
            }
            value = fallback;
            return false;
        }

        private static Dictionary<string, PolicyLevel> DefaultPolicy(PolicySide side) =>
            side == PolicySide.Agent
                ? OpWeaveSettings.DefaultAgentPolicy()
                : OpWeaveSettings.DefaultHumanPolicy();

        private static string LevelText(PolicyLevel level) => level switch
        {
            PolicyLevel.Allow => "allow",
            PolicyLevel.Ask => "ask",
            _ => "deny",
        };

        private static string StringListKeyName(SettingsStringListKey key) => key switch
        {
            SettingsStringListKey.AllowedPaths => "allowedPaths",
            SettingsStringListKey.CodePaths => "codePaths",
            SettingsStringListKey.CustomOperationFolders => "customOperationFolders",
            SettingsStringListKey.AllowedPackageSources => "allowedPackageSources",
            _ => "disabledOperations",
        };

        private static IReadOnlyList<string> StringListDefault(SettingsStringListKey key) => key switch
        {
            SettingsStringListKey.AllowedPaths => new[] { "Assets/" },
            SettingsStringListKey.CodePaths => new[] { "Assets/Scripts/" },
            SettingsStringListKey.CustomOperationFolders => new[] { "Assets/OpWeave/Editor/Operations/" },
            SettingsStringListKey.AllowedPackageSources => new[] { "registry:com.unity.*" },
            _ => Array.Empty<string>(),
        };

        private static string IntegerKeyName(SettingsIntegerKey key) => key switch
        {
            SettingsIntegerKey.TickBudgetMs => "tickBudgetMs",
            SettingsIntegerKey.RetainRuns => "retainRuns",
            SettingsIntegerKey.RetainMegabytes => "retainMegabytes",
            _ => "approvalTimeoutMinutes",
        };

        private static long IntegerDefault(SettingsIntegerKey key) => key switch
        {
            SettingsIntegerKey.TickBudgetMs => 50L,
            SettingsIntegerKey.RetainRuns => 50L,
            SettingsIntegerKey.RetainMegabytes => 200L,
            _ => 1440L,
        };
    }
}
