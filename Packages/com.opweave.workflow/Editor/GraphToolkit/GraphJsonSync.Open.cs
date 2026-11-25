#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using OpWeave.Core.Persistence;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEditor.Callbacks;

namespace OpWeave.GraphToolkit
{
    internal static partial class GraphJsonSync
    {
        // ── Open-check machinery (SPEC §H7 item 3) ─────────────────────
        // The two hooks — Graph.OnEnable and the editor's [OnOpenAsset]
        // asset-open callback — only ever REQUEST a check, keyed by the
        // graph asset GUID; the deferred drain performs it. The queue
        // deduplicates by GUID so repeated requests on a cached live
        // graph schedule one check, and the delayed call settles only
        // once the editor finishes its asset-import / play-mode
        // transition — opening a graph must never prompt
        // mid-compilation.

        // The open-check queue's dedupe set: a request for a GUID that
        // is already queued is a no-op, so one reopen can never schedule
        // two checks no matter which hook fires first.
        private static readonly HashSet<string> s_scheduledOpens =
            new HashSet<string>(StringComparer.Ordinal);

        // A queued open check: the request key and a weak handle to the
        // instance the hook saw — the drain drops it when the editor
        // unloaded it (or a fresher instance now backs the asset) before
        // the check ran.
        private sealed class PendingOpenCheck
        {
            internal PendingOpenCheck(string guid, OpWeaveGraph graph)
            {
                Guid = guid;
                Reference = new WeakReference<OpWeaveGraph>(graph);
            }

            internal string Guid { get; }

            internal WeakReference<OpWeaveGraph> Reference { get; }
        }

        // The live graph instances the editor currently holds —
        // registered on OnEnable, unregistered on OnDisable. The Unity
        // save pipeline hands OnWillSaveAssets asset paths, not objects;
        // because the public Graph type is a managed object, not a
        // UnityEngine.Object, the live instance behind an asset path can
        // only be found by tracking it here — a fresh LoadGraph would
        // see the persisted state, not the unsaved edits being saved.
        private static readonly List<WeakReference<OpWeaveGraph>> s_live =
            new List<WeakReference<OpWeaveGraph>>();

        // The open checks the two open hooks queued, in order. The
        // EditorApplication.delayCall subscription drains them in
        // production; EditMode tests drain them synchronously through
        // RunPendingOpenChecks — both run the same body, so a test
        // exercises the real deferred open path.
        private static readonly List<PendingOpenCheck> s_pendingOpenChecks =
            new List<PendingOpenCheck>();

        // Per-GUID open-check instrumentation for tests: how many
        // requests the hooks made, how many checks the drain performed
        // and how many dialogs were shown.
        internal static readonly Dictionary<string, int> OpenCheckRequests =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal static readonly Dictionary<string, int> OpenChecksPerformed =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal static readonly Dictionary<string, int> OpenDialogsShown =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // Per-GUID enable instrumentation for tests: how many OnEnable
        // hooks the editor ran for the asset's graph. A cached reopen
        // produces none — the already-loaded instance is handed back — a
        // fresh deserialization produces exactly one.
        internal static readonly Dictionary<string, int> OpenEnableCount =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // Per-GUID drop instrumentation for tests: a requested open check
        // the drain or a graph disable discarded WITHOUT performing it.
        // A healthy open performs every requested check — a non-zero
        // count means the check silently never ran. OpenCheckDropReason
        // keeps the last reason per GUID, one of the fixed strings
        // "reference-collected" (the weak handle died before the drain),
        // "not-current-live-instance" (the drain's stale-instance filter
        // rejected it) and "removed-on-disable" (the graph's OnDisable
        // swept the queued request).
        internal static readonly Dictionary<string, int> OpenChecksDropped =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal static readonly Dictionary<string, string> OpenCheckDropReason =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // The prompt the deferred open check asks, or null for the
        // production default GraphJsonSyncDialog.ShowConflictPrompt — the
        // modal dialog. A null provider in batch mode resolves to Cancel
        // through the dialog's batch rule, so an unattended open never
        // blocks and never overwrites (SPEC §0.6 SG-17).
        private static Func<GraphJsonSyncCheck, GraphJsonSyncChoice>? s_openChoiceProvider;

        /// <summary>
        /// The SPEC §H7.3 choice provider the deferred open check asks, or
        /// null for the production default — the modal dialog of
        /// <see cref="GraphJsonSyncDialog.ShowConflictPrompt"/>. Tests
        /// install a scripted answer and restore null afterwards. A null
        /// provider in batch mode resolves to
        /// <see cref="GraphJsonSyncChoice.Cancel"/> through the dialog's
        /// batch rule: an unattended open never blocks and never overwrites
        /// the JSON (SPEC §0.6 SG-17).
        /// </summary>
        internal static Func<GraphJsonSyncCheck, GraphJsonSyncChoice>? OpenChoiceProvider
        {
            get => s_openChoiceProvider;
            set => s_openChoiceProvider = value;
        }

        /// <summary>
        /// Compares the on-disk JSON at the graph's recorded path with the
        /// stored sync state (SPEC §H7.3): parses the current file and
        /// maps the outcome to <see cref="GraphJsonSyncState"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static GraphJsonSyncCheck CheckOnOpen(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            return SyncStateOfOpen(graph);
        }

        /// <summary>
        /// The open-time resolution of SPEC §H7.3: a pending intent record
        /// is reconciled first — a crash between publish and persist is
        /// finished (or offered to the human when the graph moved) — then
        /// when the JSON's GUI-owned projection differs from the stored
        /// sync state <paramref name="prompt"/> offers "reload from JSON" /
        /// "overwrite JSON with graph" (or Cancel) and the chosen action is
        /// applied to exactly the revision the prompt showed. An unchanged
        /// file opens without a prompt.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        internal static GraphJsonSyncResult ResolveOnOpen(
            OpWeaveGraph graph,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice> prompt)
        {
            // A direct call is an explicit request: it asks again even for
            // a revision a previous open already decided. Only the
            // queued drain carries a prompt memory — scoped to its own
            // pass, so a later, separate reopen asks anew.
            return ResolveOnOpen(graph, prompt, rememberedPrompts: null);
        }

        private static GraphJsonSyncResult ResolveOnOpen(
            OpWeaveGraph graph,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice> prompt,
            Dictionary<string, GraphJsonSyncChoice>? rememberedPrompts)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (prompt is null)
            {
                throw new ArgumentNullException(nameof(prompt));
            }
            var coordinator = Coordinator();
            var guid = AssetGuidOf(graph);
            // The sibling path is the record's second validation path
            // (SPEC §H7 item 3): an interrupted first link names it, and a
            // graph that never recorded jsonPath recovers it only through
            // the sibling rule — passing null would misreport that record
            // as invalid and never reconcile it.
            GraphJsonSyncResult? recovered = null;
            var handled = false;
            RecoverPendingIntent(
                coordinator, graph, guid, graph.jsonPath, SiblingPathOf(graph),
                prompt, ref recovered, ref handled);
            if (recovered is not null)
            {
                return recovered;
            }
            if (!handled)
            {
                var held = GuardHeldIntent(
                    coordinator, guid, graph.jsonPath, SiblingPathOf(graph));
                if (held is not null)
                {
                    return held;
                }
            }

            var check = CheckOnOpen(graph);
            if (check.State == GraphJsonSyncState.Unlinked ||
                check.State == GraphJsonSyncState.InSync)
            {
                return new GraphJsonSyncResult(
                    check, GraphJsonSyncOutcome.NoAction, false,
                    GraphJsonSyncChoice.Cancel, null);
            }
            var owned = coordinator.ResolveOwnedPath(graph.jsonPath);
            if (owned.Status != PathResolveStatus.Resolved)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null, owned.ErrorMessage),
                    GraphJsonSyncOutcome.Refused, false,
                    GraphJsonSyncChoice.Cancel, null);
            }
            return RunChoiceLoop(
                coordinator, graph, guid, prompt, check, ChoiceContext.Open,
                owned.ResolvedPath!, graph.jsonPath, ReadBaseline(graph),
                rememberedPrompts: rememberedPrompts);
        }

        /// <summary>
        /// The <see cref="Graph.OnEnable"/> entry: registers the graph as
        /// live and queues a delayed open check for a graph that records
        /// a JSON path — or whose GUID-keyed intent slot still holds a
        /// record, the interrupted first link the open must reconcile
        /// (SPEC §H7 item 3). The check is deferred so it runs after the
        /// load completes, deduplicated per graph instance, and skipped
        /// entirely where the editor is mid-transition — Play Mode
        /// changes, compilation and asset updates. Batch mode queues the
        /// check too (the EditMode test runner runs batch): its prompt
        /// resolves through <see cref="OpenChoiceProvider"/>, defaulting
        /// to the dialog's batch answer — Cancel — so an unattended open
        /// never blocks and never overwrites (SPEC §0.6 SG-17).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static void OnGraphEnabled(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var enabledGuid = AssetGuidOf(graph);
            if (enabledGuid is not null)
            {
                IncrementCounter(OpenEnableCount, enabledGuid);
            }
            RegisterLive(graph);
            ScheduleOpenCheck(graph);
        }

        /// <summary>
        /// The editor's documented asset-open callback
        /// (<see cref="OnOpenAssetAttribute"/>) a GUI reopen passes
        /// through on every open — the same hook Graph Toolkit's own
        /// <c>GraphObjectFactory.OpenGraphAsset</c> rides to load the
        /// graph and show its window. <see cref="Graph.OnEnable"/> only
        /// fires when the open materializes a fresh graph instance; a
        /// reopen whose <c>GraphDatabase.LoadGraph</c> returns the cached
        /// instance produces no enable at all, so this callback is the
        /// production edge the SPEC §H7.3 check hangs on for the reopen a
        /// user performs most.
        /// </summary>
        /// <remarks>
        /// The callback runs before the asset's own open handler: on a
        /// fresh open the graph is not live yet, nothing is queued here
        /// and the load's <see cref="OnGraphEnabled"/> queues the check
        /// instead — the per-instance dedupe keeps either ordering at
        /// exactly one check per reopen. The callback must never claim
        /// the open: returning false hands the asset on to Graph
        /// Toolkit's handler, which loads the graph and opens its window.
        /// </remarks>
        [OnOpenAsset(-1)]
        private static bool OnGraphAssetOpening(UnityEngine.EntityId entityId, int line)
        {
            var assetPath = AssetDatabase.GetAssetPath(entityId);
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.EndsWith(
                    "." + OpWeaveGraph.AssetExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var graph = FindLiveGraph(assetPath);
            if (graph is not null)
            {
                ScheduleOpenCheck(graph);
            }
            return false;
        }

        // The queueing the enable hook and the open-asset hook share
        // (SPEC §H7 item 3's single-check algorithm): the hooks only ever
        // REQUEST a check keyed by the graph asset GUID — a request for a
        // GUID already queued is a no-op, so the two hooks can never
        // schedule two checks for the same reopen. No document read
        // happens at request time: the deferred drain performs the
        // check's one read when it runs, so nothing queues a stale
        // snapshot into the prompt. The request is skipped entirely
        // where the editor is mid-transition. An unlinked graph has no
        // baseline to diverge from, so it requests a check only while
        // its GUID-keyed intent slot still holds a record — the one
        // existence probe this method performs — because an interrupted
        // first link always leaves the persisted graph unlinked
        // (publication precedes graph persistence) and the open must
        // reconcile it through the same recovery the command runs (SPEC
        // §H7 item 3).
        private static void ScheduleOpenCheck(OpWeaveGraph graph)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
            {
                return;
            }
            var guid = AssetGuidOf(graph);
            if (guid is null)
            {
                return;
            }
            if (graph.jsonPath.Length == 0 &&
                !File.Exists(Coordinator().IntentPathFor(guid)))
            {
                // Unlinked with no pending intent: no baseline to diverge
                // from and no interrupted write to recover — no check.
                return;
            }
            IncrementCounter(OpenCheckRequests, guid);
            if (!s_scheduledOpens.Add(guid))
            {
                return;
            }
            // A single drain subscription, added only when the queue goes
            // from empty to non-empty — the one pending drain covers every
            // request queued behind it.
            if (s_pendingOpenChecks.Count == 0)
            {
                EditorApplication.delayCall += RunQueuedOpenChecks;
            }
            s_pendingOpenChecks.Add(new PendingOpenCheck(guid, graph));
        }

        /// <summary>
        /// Runs every open check the open hooks queued, in
        /// order, and returns their results — the body
        /// <see cref="EditorApplication.delayCall"/> invokes in
        /// production. Each queued GUID drains exactly once: the drain
        /// performs one check and shows at most one dialog, and a check
        /// that found nothing actionable produces no result — an
        /// unchanged reopen leaves the results empty.
        /// </summary>
        internal static List<GraphJsonSyncResult> RunPendingOpenChecks()
        {
            var results = new List<GraphJsonSyncResult>();
            // This pass's own prompt memory — the one-reopen scope of
            // §H7 item 3's duplicate suppression: a repeated request for
            // the identical (P, R, output, B) decision inside this drain
            // reuses the shown answer instead of opening a second dialog.
            // The scope dies with the pass — a later, separate reopen is
            // a fresh pass that asks again, so a remembered Cancel never
            // suppresses a new reopen's choice.
            var rememberedPrompts =
                new Dictionary<string, GraphJsonSyncChoice>(StringComparer.Ordinal);
            while (s_pendingOpenChecks.Count != 0)
            {
                // The same mid-transition guard ScheduleOpenCheck applies
                // at request time, applied at perform time: an applied
                // choice writes the graph asset, and the asset database
                // cannot take that write while it is refreshing. The
                // queue rides the next delayCall — nothing performs and
                // nothing is dropped; the check runs once the editor
                // settles.
                if (EditorApplication.isPlayingOrWillChangePlaymode ||
                    EditorApplication.isCompiling ||
                    EditorApplication.isUpdating)
                {
                    EditorApplication.delayCall += RunQueuedOpenChecks;
                    return results;
                }
                var pending = s_pendingOpenChecks[0];
                s_pendingOpenChecks.RemoveAt(0);
                s_scheduledOpens.Remove(pending.Guid);
                // A deferred check must not fire for a graph that was
                // disabled again before it ran — a prompt for an unloaded
                // graph would act on a stale instance. A graph whose
                // backing asset is gone — or that a quiet unload left
                // registered while a fresher instance now backs the path —
                // is dropped too: the queue holds only a weak reference,
                // so a stale instance cannot be kept alive by the queue.
                // Every drop is recorded for the tests — a requested check
                // that never performs is a defect the run must see.
                if (!pending.Reference.TryGetTarget(out var graph) || graph == null)
                {
                    RecordOpenCheckDrop(pending.Guid, "reference-collected");
                    continue;
                }
                if (!IsCurrentLiveInstance(graph))
                {
                    RecordOpenCheckDrop(pending.Guid, "not-current-live-instance");
                    continue;
                }
                IncrementCounter(OpenChecksPerformed, pending.Guid);
                var result = ResolveOnOpen(
                    graph,
                    s_openChoiceProvider ?? GraphJsonSyncDialog.ShowConflictPrompt,
                    rememberedPrompts);
                if (result.PromptShown)
                {
                    IncrementCounter(OpenDialogsShown, pending.Guid);
                }
                // The notification hook T-W5.3-08's unsynced-edit warning
                // implements (SPEC §H7 item 3); unimplemented here, the
                // declared call compiles away.
                AfterOpenCheck(pending.Guid, graph, result.Check);
                if (result.Outcome != GraphJsonSyncOutcome.NoAction)
                {
                    results.Add(result);
                }
            }
            return results;
        }

        private static void RunQueuedOpenChecks() => RunPendingOpenChecks();

        // The check belongs to the instance the asset resolves to NOW: the
        // graph must still be live, still backed by an asset path (a
        // deleted or never-persisted graph throws or has none), and still
        // be the newest live instance for that path — the stale-instance
        // filter RunPendingOpenChecks applies before resolving a check.
        private static bool IsCurrentLiveInstance(OpWeaveGraph graph)
        {
            if (!IsLive(graph))
            {
                return false;
            }
            string assetPath;
            try
            {
                assetPath = GraphDatabase.GetGraphAssetPath(graph);
            }
            catch (Exception)
            {
                // An existence probe, not an operation: a stale or never-
                // persisted graph may make the lookup fail in arbitrary
                // ways, and every failure means "no backing asset" — the
                // pending check for it is simply dropped.
                return false;
            }
            return !string.IsNullOrEmpty(assetPath) &&
                ReferenceEquals(FindLiveGraph(assetPath), graph);
        }

        /// <summary>
        /// The <see cref="Graph.OnDisable"/> entry: drops the graph from the
        /// live registry the save processor resolves asset paths against.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static void OnGraphDisabled(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var guid = AssetGuidOf(graph);
            if (guid is not null)
            {
                s_scheduledOpens.Remove(guid);
            }
            s_pendingOpenChecks.RemoveAll(p =>
            {
                var dropped = !p.Reference.TryGetTarget(out var target) ||
                    ReferenceEquals(target, graph) ||
                    (guid is not null && p.Guid == guid);
                // A queued request swept here is a requested check that
                // never performs — recorded for the tests like the
                // drain's own drops.
                if (dropped)
                {
                    RecordOpenCheckDrop(p.Guid, "removed-on-disable");
                }
                return dropped;
            });
            s_live.RemoveAll(
                r => !r.TryGetTarget(out var target) || ReferenceEquals(target, graph));
        }

        /// <summary>
        /// The live graph behind <paramref name="assetPath"/>, or null when
        /// no loaded graph is saved to that asset — the lookup code that
        /// resolves an asset path to the unsaved in-memory graph uses.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="assetPath"/> is null.</exception>
        internal static OpWeaveGraph? FindLiveGraph(string assetPath)
        {
            if (assetPath is null)
            {
                throw new ArgumentNullException(nameof(assetPath));
            }
            OpWeaveGraph? found = null;
            // Newest registration first: an instance a quiet unload left
            // behind can share the path, and the check belongs to the
            // graph the asset resolves to now.
            for (var i = s_live.Count - 1; i >= 0; i--)
            {
                if (!s_live[i].TryGetTarget(out var graph) || graph == null)
                {
                    continue;
                }
                string graphPath;
                try
                {
                    graphPath = GraphDatabase.GetGraphAssetPath(graph);
                }
                catch (Exception)
                {
                    // An existence probe, not an operation: a graph not
                    // backed by an asset yet (a transient in-memory copy)
                    // or a stale instance whose backing object is gone
                    // may make the lookup fail in arbitrary ways — every
                    // failure means "no asset path to match".
                    continue;
                }
                if (string.Equals(graphPath, assetPath, StringComparison.Ordinal))
                {
                    found = graph;
                    break;
                }
            }
            return found;
        }

        // A deferred open check must not fire for a graph that was disabled
        // again before delayCall ran — a prompt for an unloaded graph would
        // act on a stale instance.
        private static bool IsLive(OpWeaveGraph graph)
        {
            foreach (var reference in s_live)
            {
                if (reference.TryGetTarget(out var target) &&
                    ReferenceEquals(target, graph))
                {
                    return true;
                }
            }
            return false;
        }

        private static void RegisterLive(OpWeaveGraph graph)
        {
            for (var i = s_live.Count - 1; i >= 0; i--)
            {
                if (!s_live[i].TryGetTarget(out var target) ||
                    target == null ||
                    ReferenceEquals(target, graph))
                {
                    s_live.RemoveAt(i);
                }
            }
            s_live.Add(new WeakReference<OpWeaveGraph>(graph));
        }

        // The live graph instances currently loaded — what the quit guard
        // enumerates for unsynced edits (SPEC §H7 item 3, decision
        // 2026-11-14-t-w5-3-02-explicit-save-json). Dead references and
        // destroyed graphs are dropped and pruned.
        internal static List<OpWeaveGraph> LiveGraphs()
        {
            var graphs = new List<OpWeaveGraph>();
            for (var i = s_live.Count - 1; i >= 0; i--)
            {
                if (s_live[i].TryGetTarget(out var graph) && graph != null)
                {
                    graphs.Add(graph);
                }
                else
                {
                    s_live.RemoveAt(i);
                }
            }
            return graphs;
        }

        // Records a queued open check discarded without being performed —
        // test instrumentation only; production behaviour is unchanged.
        private static void RecordOpenCheckDrop(string guid, string reason)
        {
            IncrementCounter(OpenChecksDropped, guid);
            OpenCheckDropReason[guid] = reason;
        }

        private static void IncrementCounter(Dictionary<string, int> map, string key)
        {
            map.TryGetValue(key, out var n);
            map[key] = n + 1;
        }

        // T-W5.3-07's implementation of the ClearOpenTestState seam
        // declared in GraphJsonSync.cs, completed for T-W5.3-16's
        // isolation contract: the per-GUID instrumentation, the
        // queued-but-undrained requests, the dedupe set, the live-graph
        // registry and the installed choice provider are session state
        // a fixture must not leak across runs — a leftover request must
        // never drain into the next run's counters or show its prompt
        // there, a stale live registration must never answer a lookup,
        // and a scripted answer must never survive as the drain's
        // prompt.
        static partial void ClearOpenTestState()
        {
            OpenCheckRequests.Clear();
            OpenChecksPerformed.Clear();
            OpenDialogsShown.Clear();
            OpenEnableCount.Clear();
            OpenChecksDropped.Clear();
            OpenCheckDropReason.Clear();
            s_pendingOpenChecks.Clear();
            s_scheduledOpens.Clear();
            s_live.Clear();
            s_openChoiceProvider = null;
        }
    }
}
