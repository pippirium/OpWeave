#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The set of live <see cref="OpWeaveGraph"/> instances in this domain —
    /// the "open graphs" of SPEC §H6 item 5. Graph Toolkit exposes no
    /// public window/selection API (T-W5.1-02 L-03 item (f)), so OpWeave
    /// tracks loaded graph objects itself: a graph registers in
    /// <see cref="OpWeaveGraph.OnEnable"/> and unregisters in
    /// <see cref="OpWeaveGraph.OnDisable"/> — the closest public-API signal
    /// to "open" the toolkit offers, since a graph shown in a window is
    /// always loaded. Tracking deliberately over-approximates: refreshing a
    /// merely loaded graph alongside the open ones costs one port
    /// redefinition and a validation pass, never a wrong result.
    /// </summary>
    internal static class OpWeaveGraphTracker
    {
        private static readonly HashSet<OpWeaveGraph> s_live =
            new HashSet<OpWeaveGraph>();

        /// <summary>
        /// A snapshot of the currently live graphs — safe to enumerate
        /// while a refresh runs. Null entries are pruned on read; a graph
        /// whose owning scope forgot <see cref="Unregister"/> stays until
        /// its <see cref="OpWeaveGraph.OnDisable"/> runs — refresh over such
        /// a still-live object is harmless, never wrong.
        /// </summary>
        internal static IReadOnlyList<OpWeaveGraph> Live
        {
            get
            {
                s_live.RemoveWhere(static graph => graph == null);
                var live = new List<OpWeaveGraph>(s_live.Count);
                foreach (var graph in s_live)
                {
                    live.Add(graph);
                }
                return live;
            }
        }

        /// <summary>Marks <paramref name="graph"/> live; idempotent.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static void Register(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            s_live.Add(graph);
        }

        /// <summary>Drops <paramref name="graph"/> from the live set when present.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static void Unregister(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            s_live.Remove(graph);
        }
    }
}
