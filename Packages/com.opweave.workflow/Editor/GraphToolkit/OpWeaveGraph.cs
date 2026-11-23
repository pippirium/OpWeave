#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using Unity.GraphToolkit.Editor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The Graph Toolkit graph asset type for OpWeave workflows (SPEC §H6.1):
    /// a <c>*.opweavegraph</c> asset holding <see cref="StartNode"/>,
    /// <see cref="OperationNode"/> and workflow inputs as
    /// <see cref="OwValue"/>-typed variables (SPEC §H6.4). The graph displays
    /// and edits the workflow; it never executes it (SPEC §H6 責務).
    /// </summary>
    [Serializable]
    [Graph(AssetExtension)]
    public sealed class OpWeaveGraph : Graph
    {
        /// <summary>The graph asset file extension declared to Graph Toolkit (SPEC §H6.1).</summary>
        internal const string AssetExtension = "opweavegraph";

        /// <summary>
        /// The graph-level share of the SG-17 carrier: document content an
        /// import could not attach to any graph node — wires whose endpoint
        /// nodes the document itself does not declare — kept verbatim so a
        /// JSON → graph → JSON round-trip never silently drops JSON content
        /// (SPEC §H7, §0.6 SG-17). The per-node share rides in each node's
        /// <c>extrasJson</c> carrier. Empty when the last import applied
        /// everything the document carries.
        /// </summary>
        [SerializeField] internal string carriedJson = "";

        /// <summary>
        /// The serialized provenance of the SG-17 carrier: the random import
        /// tokens the last <see cref="GraphJsonConverter.Import"/> issued,
        /// one per element it created. Each element records its own token in
        /// its serialized state — the <c>importToken</c> field of
        /// <see cref="OperationNode"/>/<see cref="StartNode"/>, the
        /// <c>importToken</c> payload member of an imported variable — and
        /// counts as import-created exactly while its token is in this set
        /// and no other element carries it: a GUI duplicate or paste copies
        /// the token verbatim, so both copies lose sole ownership and
        /// resolve as ambiguous, never guessed (see
        /// <see cref="GraphJsonConverter"/>'s remarks). Serialized so
        /// provenance survives a domain reload, asset reopen and undo/redo;
        /// hand-editing the token is unsupported.
        /// </summary>
        [SerializeField] internal List<string> importedTokens = new List<string>();

        /// <summary>
        /// The path of the <c>.opweave.json</c> document this graph was last
        /// synced with (SPEC §H7.3): the source of truth the GUI never
        /// silently overwrites. Empty while the graph has never been synced
        /// to a JSON file.
        /// </summary>
        [SerializeField] internal string jsonPath = "";

        /// <summary>
        /// The <c>CanonicalWorkflow</c> hash (<c>WorkflowHash</c>, SPEC §D0.2)
        /// of the JSON document at the last sync (SPEC §H7.3): a differing
        /// on-disk hash on open means the file was edited outside the GUI
        /// and the user chooses between reloading and overwriting.
        /// </summary>
        [SerializeField] internal string syncedWorkflowHash = "";

        /// <summary>
        /// The serialized form of the JSON document at the last sync. The
        /// hash alone cannot rebuild the SG-17 save baseline — the document
        /// carries the non-GUI-owned members (<c>id</c>,
        /// <c>schemaVersion</c>, <c>title</c>, <c>x-*</c>) a save must merge
        /// and reproduce when the file is missing and the human explicitly
        /// overwrites — so the document itself is kept alongside the hash.
        /// </summary>
        [SerializeField] internal string syncedJson = "";

        /// <summary>
        /// The lowercase-hex SHA-256 of the exact file bytes the last sync
        /// was made against (SPEC §H7.3): the idempotency signal — a save
        /// whose merged output already equals the file's bytes reconciles
        /// without writing or touching the file's mtime. Recorded at the
        /// same time as <see cref="syncedJson"/>; empty for assets synced
        /// before the revision fields existed.
        /// </summary>
        [SerializeField] internal string syncedBytesHash = "";

        /// <summary>
        /// The GUI-owned projection hash of the JSON document at the last
        /// sync (SPEC §H7.3): the canonical form of the document's
        /// canonical projection plus its <c>layout</c> member — everything
        /// the GUI owns. A file whose projection hash still matches differs
        /// only in non-GUI-owned members (<c>title</c>,
        /// <c>description</c>, <c>tags</c>, <c>x-*</c>) — the only external
        /// change a save merges automatically.
        /// </summary>
        [SerializeField] internal string syncedGuiOwnedHash = "";

        // ── SPEC §H8 item 1: live-validation state ─────────────────────
        // Never serialized: diagnostics recompute from the graph on the
        // next change, so a domain reload or reopen starts clean rather
        // than showing a stale badge.
        /// <summary>Whether this graph instance is live (enabled → disable clears).</summary>
        [NonSerialized] internal bool validationLive;
        /// <summary>The editor-clock deadline of the current quiet window; -1 when none is armed.</summary>
        [NonSerialized] internal double validationDueAt = -1;
        /// <summary>The next OnGraphChanged is the deferred validation pass, not a user change.</summary>
        [NonSerialized] internal bool validationPokePending;
        /// <summary>A deferred processing trigger is already queued.</summary>
        [NonSerialized] internal bool validationPokeScheduled;
        /// <summary>The diagnostics the last deferred pass produced; re-emitted on each change so badges survive the quiet window.</summary>
        [NonSerialized] internal IReadOnlyList<GraphNodeDiagnostic> latestDiagnostics =
            new List<GraphNodeDiagnostic>();
        /// <summary>The set the last emit actually logged into the pass's sink — what the live logger received; null when the emit had no sink (no badge update occurred).</summary>
        [NonSerialized] internal IReadOnlyList<GraphNodeDiagnostic>? lastEmission;
        /// <summary>The last emit delivered through the production <c>LoggerSink</c>→<c>GraphLogger</c> channel.</summary>
        [NonSerialized] internal bool lastEmissionUsedLoggerSink;
        /// <summary>The windowless delivery record — the sink the deferred pass logs into when no view consumed its poke; each emit replaces the entries.</summary>
        [NonSerialized] internal readonly GraphLiveValidation.ValidationLog validationLog =
            new GraphLiveValidation.ValidationLog();
        /// <summary>The live <c>GraphLogger</c> the last processing pass handed to <see cref="OnGraphChanged"/> — null for seam-driven calls.</summary>
        [NonSerialized] internal GraphLogger? lastProcessingLogger;
        /// <summary>
        /// The next deferred pass emits <see cref="latestDiagnostics"/> as-is
        /// instead of recomputing — set by the §H8 item 2 actions' publish
        /// path; a real change clears it (SPEC §H8 item 2).
        /// </summary>
        [NonSerialized] internal bool publishOnly;
        /// <summary>
        /// The §E8 step-state messages the execution display queued for the
        /// next processing pass — the SPEC §H8 item 3 GraphLogger-info
        /// fallback for a step whose node id no graph node carries.
        /// </summary>
        [NonSerialized] internal readonly List<(string message, string? nodeId)> pendingDisplayInfos =
            new List<(string message, string? nodeId)>();

        // ── SPEC §H8 item 3: execution display state ─────────────────────
        // Never serialized: a domain reload drops the §E8 subscription
        // anyway (subscriptions do not survive it), and the per-node
        // colours/state log describe an in-flight Run — a fresh domain
        // starts unattached rather than displaying a stale verdict.
        /// <summary>The Run whose step states this graph displays; null while no Run is attached.</summary>
        [NonSerialized] internal string? executionRunId;
        /// <summary>The §E8 dispatcher the display subscribed — kept for Detach.</summary>
        [NonSerialized] internal IRunEvents? executionEvents;
        /// <summary>The subscribed <c>StepStateChanged</c> handler — kept for Detach.</summary>
        [NonSerialized] internal Action<StepStateChanged>? executionStepHandler;
        /// <summary>The subscribed <c>RunStateChanged</c> handler — kept for Detach.</summary>
        [NonSerialized] internal Action<RunStateChanged>? executionRunHandler;
        /// <summary>
        /// The per-node step-state record of the attached Run — the
        /// windowless delivery record the display leaves (nodeId, state,
        /// attempt) in the order the journal reported them.
        /// </summary>
        [NonSerialized] internal readonly List<(string nodeId, StepState state, int? attempt)> executionLog =
            new List<(string nodeId, StepState state, int? attempt)>();
        /// <summary>
        /// The pre-Run <c>DefaultColor</c> of every node the display
        /// repainted — restored by the next <see cref="GraphExecutionDisplay.Attach"/>
        /// so a new Run starts from the graph's own colours, not a previous
        /// Run's leftovers.
        /// </summary>
        [NonSerialized] internal readonly Dictionary<INode, Color> executionOriginalColors =
            new Dictionary<INode, Color>();

        /// <inheritdoc/>
        public override void OnEnable()
        {
            base.OnEnable();
            validationLive = true;
            // SPEC §H6.5 tracks "open graphs" through this lifecycle — the
            // closest public-API signal to open the toolkit offers.
            OpWeaveGraphTracker.Register(this);
            // The SPEC §H7.3 open check rides the same load lifecycle: the
            // registration and the deferred check request live in
            // GraphJsonSync.OnGraphEnabled.
            GraphJsonSync.OnGraphEnabled(this);
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            GraphJsonSync.OnGraphDisabled(this);
            OpWeaveGraphTracker.Unregister(this);
            // A disabled graph stops following its Run: the §E8
            // subscription outlives the instance otherwise.
            GraphExecutionDisplay.Detach(this);
            validationLive = false;
            validationDueAt = -1;
            validationPokePending = false;
            validationPokeScheduled = false;
            publishOnly = false;
            pendingDisplayInfos.Clear();
            // A disabled graph carries no proven verdicts: dropping the
            // set here keeps a disable/re-enable cycle (or a queued
            // deferred action) from re-emitting a stale badge the next
            // quiet window has not yet re-proven.
            latestDiagnostics = new List<GraphNodeDiagnostic>();
            lastEmission = null;
            lastEmissionUsedLoggerSink = false;
            lastProcessingLogger = null;
            validationLog.Clear();
            base.OnDisable();
        }

        /// <inheritdoc/>
        public override void OnGraphChanged(GraphLogger graphLogger) =>
            GraphLiveValidation.OnGraphChanged(this, graphLogger);
    }
}
