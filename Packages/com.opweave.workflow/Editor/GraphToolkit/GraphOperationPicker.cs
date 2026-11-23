#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The SPEC §H6 item 5 add-Operation backend behind
    /// <see cref="AddOperationWindow"/>: the §C6 search over the published
    /// Registry snapshot — Japanese and English queries alike, with the
    /// item's origin carried on every result — plus the node creation a
    /// pick commits.
    /// </summary>
    /// <remarks>
    /// Search reads the same atomic publication the node ports and the
    /// Runner read — <see cref="GraphToolkitRegistry.SnapshotSource"/> — so
    /// the picker's candidate set is exactly what a graph can attach.
    /// Before the first §C3 build publishes, the page is empty rather than
    /// an error: a picker shows no candidates.
    /// </remarks>
    internal static class GraphOperationPicker
    {
        /// <summary>
        /// The caller's §C6 item-5 default language — the settings file's
        /// <c>agentLanguage</c>; a missing or rejected settings load leaves
        /// the service its English default. Tests substitute a fixed value.
        /// </summary>
        internal static Func<string?> DefaultLanguage =
            () => OpWeaveSettingsLoader.Load() is { Ok: true, Value: { } settings }
                ? settings.AgentLanguage
                : null;

        /// <summary>
        /// Runs the SPEC §C6 search for <paramref name="request"/> over the
        /// current snapshot: filters and ranking are
        /// <see cref="OperationSearchService"/>'s own, and each item's
        /// <see cref="OperationSearchResult.Operation"/> carries the
        /// <see cref="RegisteredOperation.Origin"/> the picker displays
        /// (SPEC §H6.5 origin display).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        internal static Result<OperationSearchPage> Search(OperationSearchRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            var snapshot = GraphToolkitRegistry.SnapshotSource();
            if (snapshot is null)
            {
                return Result<OperationSearchPage>.Success(
                    new OperationSearchPage(
                        Array.Empty<OperationSearchResult>(), null));
            }
            return new OperationSearchService(snapshot)
                .SearchOperations(request, DefaultLanguage());
        }

        /// <summary>
        /// Attaches one <see cref="OperationNode"/> for the picked
        /// <paramref name="operation"/> to <paramref name="graph"/>: the
        /// §H6.2 carrier fields are assigned before
        /// <see cref="Graph.AddNode"/> — the public API has no node-option
        /// value setter — inside one undo record, so
        /// <see cref="OperationNode.OnDefinePorts"/> builds the Descriptor's
        /// ports as the node attaches.
        /// </summary>
        /// <param name="nodeId">The workflow node ID (SPEC §A4) to carry;
        /// generated through <see cref="NewNodeId"/> when null.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        internal static OperationNode Add(
            OpWeaveGraph graph,
            RegisteredOperation operation,
            Vector2 position,
            string? nodeId = null)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }
            var descriptor = operation.Descriptor;
            var node = new OperationNode
            {
                operationId = descriptor.Id,
                version = descriptor.Version,
                nodeId = nodeId ?? NewNodeId(graph),
            };
            graph.UndoBeginRecordGraph("Add Operation " + descriptor.Id);
            try
            {
                graph.AddNode(node);
                node.Position = position;
            }
            finally
            {
                graph.UndoEndRecordGraph();
            }
            return node;
        }

        /// <summary>
        /// A fresh node ID inside <paramref name="graph"/>'s scope: <c>n</c>
        /// plus a random 32-hex suffix — always inside the §A4 grammar
        /// <c>^[A-Za-z][A-Za-z0-9_]{0,63}$</c> — redrawn on collision with an
        /// id any existing node already carries.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static string NewNodeId(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in graph.GetNodes())
            {
                var existingId = GraphJsonConverter.NodeIdOf(node);
                if (existingId is not null)
                {
                    used.Add(existingId);
                }
            }
            string id;
            do
            {
                id = "n" + Guid.NewGuid().ToString("N");
            }
            while (!used.Add(id));
            return id;
        }
    }
}
