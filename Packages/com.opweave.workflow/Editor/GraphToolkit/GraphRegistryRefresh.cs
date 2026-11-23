#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The SPEC §H6 item 5 Registry-change refresh: on a §C3 rule-7 diff —
    /// delivered as the §E8 <see cref="IRunEvents.RegistryChanged"/> event —
    /// every live <see cref="OpWeaveGraph"/> has its
    /// <see cref="OperationNode"/> ports redefined from the new Descriptors
    /// (<see cref="Node.DefineNode"/>, the L-03 (f) public API), then a live
    /// validation pass is armed so a node whose Operation left the Registry
    /// surfaces its D3 diagnostic (OW-OP-001 / OW-OP-002) as a GraphLogger
    /// error — the error display §H6.5 requires. The graph itself and the
    /// node stay untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subscription starts on
    /// <see cref="EditorApplication.delayCall"/> like the Run Viewer's
    /// approval notifier: <c>[InitializeOnLoad]</c> order versus the
    /// adapter bootstrap — which installs the production runner host — is
    /// unspecified, and the host may not exist at the first delayCall. An
    /// <see cref="EditorApplication.update"/> pump keeps retrying until a
    /// dispatcher is bound and rebinds if the host swaps it — the
    /// per-update cost is one delegate call. The subscription dies with
    /// the domain; the next domain re-registers (SPEC §E8).
    /// </para>
    /// <para>
    /// A live graph is any loaded <see cref="OpWeaveGraph"/>
    /// (<see cref="OpWeaveGraphTracker"/>) — Graph Toolkit has no public
    /// window API, so the tracked set is OpWeave's own.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    internal static class GraphRegistryRefresh
    {
        static GraphRegistryRefresh()
        {
            // delayCall defers past [InitializeOnLoad] ordering; the update
            // pump keeps binding cheap until the host's dispatcher exists.
            EditorApplication.delayCall += () =>
            {
                Subscribe();
                EditorApplication.update += Subscribe;
            };
        }

        /// <summary>
        /// The §E8 dispatcher seam: production resolves the installed runner
        /// host's event surface lazily — a host that failed to install drops
        /// the subscription — and tests bind a dispatcher of their own.
        /// </summary>
        internal static Func<IRunEvents?> EventsSource =
            () => OpWeaveRunnerHost.Current?.Events;

        private static IRunEvents? s_subscribed;

        /// <summary>
        /// Subscribes <see cref="OnRegistryChanged"/> to the resolved
        /// dispatcher, replacing any earlier binding.
        /// </summary>
        internal static void Subscribe()
        {
            var events = EventsSource();
            if (ReferenceEquals(events, s_subscribed))
            {
                return;
            }
            Unsubscribe();
            s_subscribed = events;
            if (events is not null)
            {
                events.RegistryChanged += OnRegistryChanged;
            }
        }

        /// <summary>Drops the current subscription.</summary>
        internal static void Unsubscribe()
        {
            if (s_subscribed is not null)
            {
                s_subscribed.RegistryChanged -= OnRegistryChanged;
                s_subscribed = null;
            }
        }

        /// <summary>
        /// The §H6.5 refresh entry point — the §E8 event handler and the
        /// seam fixtures drive directly. Every live graph is refreshed; one
        /// graph's failure is logged and never blocks the rest.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="changes"/> is null.</exception>
        internal static void OnRegistryChanged(IReadOnlyList<RegistryChange> changes)
        {
            if (changes is null)
            {
                throw new ArgumentNullException(nameof(changes));
            }
            foreach (var graph in OpWeaveGraphTracker.Live)
            {
                try
                {
                    RefreshGraph(graph);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        /// <summary>
        /// Redefines every <see cref="OperationNode"/> of
        /// <paramref name="graph"/> against the current Registry snapshot
        /// and re-arms its live validation.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static void RefreshGraph(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            foreach (var node in graph.GetNodes())
            {
                if (node is OperationNode operation)
                {
                    operation.DefineNode();
                }
            }
            // The deferred §H8 pass computes and emits the fresh
            // diagnostics — a node whose Operation the new snapshot no
            // longer registers gets its OW-OP-001/OW-OP-002 through the
            // same pipeline every other node diagnostic uses (SPEC §H6.5).
            GraphLiveValidation.OnGraphChanged(graph, (IErrorsAndWarnings?)null);
        }
    }
}
