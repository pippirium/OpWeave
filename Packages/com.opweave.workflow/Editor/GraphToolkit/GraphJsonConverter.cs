#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Model;
using Unity.GraphToolkit.Editor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// Converts between an <see cref="OpWeaveGraph"/> and the
    /// <c>.opweave.json</c> workflow document (SPEC §H7 items 1–2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Export (SPEC §H7.1): <see cref="OperationNode"/>/<see cref="StartNode"/>
    /// → <c>nodes</c>, <see cref="OwControl"/> output connections →
    /// <c>controlEdges</c> (with <c>fromPort</c>), <see cref="OwValue"/>
    /// output connections to another OperationNode → <c>dataEdges</c>,
    /// <see cref="VariableKind.Input"/> variables → <c>inputs</c> and their
    /// variable-node wires → <c>inputBindings</c>, <see cref="VariableKind.Output"/>
    /// variable nodes fed by a data output → <c>outputs</c>, node
    /// <see cref="INode.Position"/> → <c>layout.nodes</c>. Unconnected OwValue
    /// input ports carrying a constant (and <see cref="IConstantNode"/>s wired
    /// to an input) → node <c>parameters</c>. The GUI owns exactly the seven
    /// keys <c>nodes</c>/<c>controlEdges</c>/<c>dataEdges</c>/<c>inputs</c>/
    /// <c>inputBindings</c>/<c>outputs</c>/<c>layout</c> (SPEC §0.6 SG-17);
    /// every other top-level member — <c>schemaVersion</c>, <c>id</c>,
    /// <c>title</c>, <c>description</c>, <c>tags</c>, <c>x-*</c> — is taken
    /// verbatim from the supplied base document, so metadata edited outside
    /// the GUI is never overwritten silently.
    /// </para>
    /// <para>
    /// Import (SPEC §H7.2): the document is read with the §A3 serializer and
    /// the graph is rebuilt through the public Graph Toolkit API —
    /// <see cref="Graph.AddNode"/>, <see cref="Graph.Connect"/>,
    /// <see cref="Graph.CreateVariable"/>/<see cref="Graph.AddVariableNode"/>,
    /// and <see cref="IPort.TrySetValue{T}"/> for the constants of
    /// unconnected ports — wrapped in one
    /// <see cref="Graph.UndoBeginRecordGraph"/>/<see cref="Graph.UndoEndRecordGraph"/>
    /// pair. A wire Graph Toolkit refuses (capacity, direction,
    /// <see cref="ArgumentException"/>) is skipped: an invalid document is
    /// reported by D-validation, not by a crashed import. Import replaces the
    /// graph's content — the graph models exactly the document read.
    /// </para>
    /// <para>
    /// Local, reversible representation choices (no public contract):
    /// </para>
    /// <list type="bullet">
    /// <item>WF input ↔ <see cref="VariableKind.Input"/> <see cref="IVariable"/>
    /// named by the input id; its <see cref="OwValue"/> default carries the
    /// rest of the §A6 record as Canonical JSON
    /// <c>{"type","required","default"?,"description"?}</c>. WF output ↔
    /// <see cref="VariableKind.Output"/> variable; its <see cref="OwValue"/>
    /// carries <c>{"description"?}</c>. A variable whose payload is absent or
    /// unparseable decodes as <c>{"type":"core.Json","required":false}</c>.</item>
    /// <item>Node <c>label</c>/<c>policy</c> ride in the serialized
    /// <c>extrasJson</c> carrier of <see cref="OperationNode"/>/
    /// <see cref="StartNode"/>; <c>core.start</c> maps to
    /// <see cref="StartNode"/> and exports as operation <c>core.start</c>
    /// version 1 (the only v1 catalogue entry).</item>
    /// <item>Document content the graph cannot represent — a parameter or
    /// wire whose port the Registry Descriptor does not declare, or a wire
    /// Graph Toolkit refuses — is kept verbatim in the same
    /// <c>extrasJson</c> carrier (on the endpoint node, graph-level on
    /// <see cref="OpWeaveGraph.carriedJson"/> when no endpoint node exists)
    /// and re-emitted unchanged on export, so the GUI never silently drops
    /// JSON content (SPEC §H7, §0.6 SG-17). A carried wire is re-emitted
    /// only while every element it referenced at import resolves
    /// <em>uniquely and exactly</em>: exactly one element claims the
    /// recorded <c>importedAs</c> identity (a rename rewrites the emitted
    /// reference to the claimant's current name), or no element claims it
    /// and exactly one element carrying no <c>importedAs</c> has the
    /// recorded name. Provenance is serialized, not instance identity:
    /// every element an import creates records a fresh random
    /// <c>importToken</c> in its own serialized state (the field of a
    /// node, the payload member of a variable) and the graph keeps the
    /// issued set in <see cref="OpWeaveGraph.importedTokens"/>, so a
    /// domain reload, asset reopen or undo/redo loses nothing. A GUI
    /// duplicate or copy-paste copies the serialized
    /// <c>importedAs</c>/<c>importToken</c> markers together with the
    /// carried wires, so the copied token is no longer sole — claims are
    /// counted against the element that uniquely owns an issued token,
    /// and a post-import element can never silently take over a recorded
    /// identity. The only silent drop is the explicit delete of the
    /// single element that claimed the recorded identity; a delete that
    /// cannot be proven — a marker-free element exists whose identity
    /// could be the claimant's — is not a delete at all. Anything else
    /// (two or more claimants, a claimant the import did not create, a
    /// name-only match carrying a different <c>importedAs</c> or without
    /// provable identity) is never guessed: the item is not emitted and
    /// export collects one OW-UNITY-002 diagnostic per affected carried
    /// item naming it and its reason, so
    /// <see cref="GraphJsonFile.SaveToFile"/> refuses the write as
    /// <see cref="GraphJsonSaveStatus.InvalidGraph"/> unless the human
    /// explicitly overwrites — which drops the affected items by choice.
    /// One an identical live wire already covers is not duplicated.
    /// Hand-editing the internal <c>importedAs</c>/<c>importToken</c>
    /// markers is unsupported: an element whose marker no longer proves
    /// its identity is unprovable, never guessed.</item>
    /// <item><see cref="VariableKind.Local"/> variables, unwired
    /// Output-kind variable nodes and node kinds the WF model has no record
    /// for are GUI-only state and are not written back; <c>layout.notes</c> is
    /// kept from the base document because the graph has no note element.</item>
    /// </list>
    /// </remarks>
    internal static class GraphJsonConverter
    {
        /// <summary>The Operation ID the graph form of <see cref="StartNode"/> exports.</summary>
        internal const string StartOperationId = "core.start";

        /// <summary>The v1 <c>core.start</c> catalogue version (SPEC appendix I).</summary>
        internal const int StartVersion = 1;

        /// <summary>The Type ID a GUI-created variable decodes to when its payload carries none.</summary>
        internal const string DefaultInputType = "core.Json";

        // ── Import (SPEC §H7.2) ─────────────────────────────────────────────

        /// <summary>
        /// Reads <paramref name="json"/> with the §A3 serializer and, on
        /// success, rebuilds <paramref name="graph"/> from the document.
        /// The read result — diagnostics included — is returned unchanged;
        /// on a failed read the graph is left untouched.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        internal static Result<WorkflowDocument> Import(OpWeaveGraph graph, string json)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }
            var read = WorkflowSerializer.Read(json);
            if (!read.Ok)
            {
                return read;
            }
            Import(graph, read.Value!);
            return read;
        }

        /// <summary>
        /// Rebuilds <paramref name="graph"/> so it models
        /// <paramref name="document"/>: every existing node and variable is
        /// removed first, then the document's nodes, variables, edges,
        /// bindings, constants and layout are recreated inside one Undo
        /// record (SPEC §H7.2).
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        internal static void Import(OpWeaveGraph graph, WorkflowDocument document)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            graph.UndoBeginRecordGraph("Import OpWeave workflow JSON");
            try
            {
                foreach (var node in graph.GetNodes().ToList())
                {
                    graph.RemoveNode(node);
                }
                foreach (var variable in graph.GetVariables().ToList())
                {
                    graph.RemoveVariable(variable, true);
                }
                graph.carriedJson = "";

                var context = new ImportContext(graph, document);
                AddNodes(context);
                foreach (var input in document.Inputs)
                {
                    context.InputVariables[input.Id] = graph.CreateVariable(
                        input.Id,
                        typeof(OwValue),
                        new OwValue { json = EncodeInputPayload(input, context.IssueToken()) },
                        VariableKind.Input);
                }
                foreach (var output in document.Outputs)
                {
                    context.OutputVariables[output.Id] = graph.CreateVariable(
                        output.Id,
                        typeof(OwValue),
                        new OwValue { json = EncodeOutputPayload(output, context.IssueToken()) },
                        VariableKind.Output);
                }
                ConnectControlEdges(context);
                ConnectDataEdges(context);
                ConnectInputBindings(context);
                ConnectOutputs(context);
                ApplyParameters(context);
                WriteCarried(context);
                // The serialized provenance the SG-17 carrier resolves
                // against (see the class remarks): every element the import
                // created recorded a fresh random token in its own
                // serialized state — the importToken field of a node, the
                // "importToken" payload member of a variable — and the
                // issued set lands here, so provenance survives a domain
                // reload, asset reopen and undo/redo. Elements added later
                // — including a duplicate or paste copying "importedAs",
                // the token and the carried wires verbatim — carry a token
                // that is absent, unissued or no longer sole, and are never
                // import-created.
                graph.importedTokens = context.IssuedTokens;
            }
            finally
            {
                graph.UndoEndRecordGraph();
            }
        }

        private static void AddNodes(ImportContext context)
        {
            var layout = context.Document.Layout;
            foreach (var nodeModel in context.Document.Nodes)
            {
                Node node = string.Equals(nodeModel.Operation, StartOperationId, StringComparison.Ordinal)
                    ? new StartNode
                    {
                        nodeId = nodeModel.Id,
                        extrasJson = EncodeExtras(nodeModel),
                        importToken = context.IssueToken(),
                    }
                    : new OperationNode
                    {
                        operationId = nodeModel.Operation,
                        version = nodeModel.Version,
                        nodeId = nodeModel.Id,
                        extrasJson = EncodeExtras(nodeModel),
                        importToken = context.IssueToken(),
                    };
                context.Graph.AddNode(node);
                if (layout is not null &&
                    layout.Nodes.TryGetValue(nodeModel.Id, out var position))
                {
                    node.Position = new Vector2((float)position.X, (float)position.Y);
                }
                context.NodesById[nodeModel.Id] = node;
            }
        }

        private static void ConnectControlEdges(ImportContext context)
        {
            foreach (var edge in context.Document.ControlEdges)
            {
                if (edge is null)
                {
                    continue;
                }
                var connected =
                    edge.From is not null && edge.To is not null &&
                    context.NodesById.TryGetValue(edge.From, out var from) &&
                    context.NodesById.TryGetValue(edge.To, out var to) &&
                    TryConnect(
                        context.Graph,
                        from.GetOutputPortByName(edge.FromPort),
                        to.GetInputPortByName(OperationNode.ControlInputName));
                if (!connected)
                {
                    var wire = new CarriedWire(EncodeControlEdge(edge));
                    wire.RefNode(context, edge.From);
                    wire.RefNode(context, edge.To);
                    context.CarrierFor(edge.From, edge.To).AddControlEdge(wire);
                }
            }
        }

        private static void ConnectDataEdges(ImportContext context)
        {
            foreach (var edge in context.Document.DataEdges)
            {
                if (edge is null)
                {
                    continue;
                }
                var connected =
                    edge.From?.Node is not null && edge.From.Port is not null &&
                    edge.To?.Node is not null && edge.To.Port is not null &&
                    context.NodesById.TryGetValue(edge.From.Node, out var from) &&
                    context.NodesById.TryGetValue(edge.To.Node, out var to) &&
                    TryConnect(
                        context.Graph,
                        from.GetOutputPortByName(edge.From.Port),
                        to.GetInputPortByName(edge.To.Port));
                if (!connected)
                {
                    var wire = new CarriedWire(EncodeDataEdge(edge));
                    wire.RefNode(context, edge.From?.Node);
                    wire.RefNode(context, edge.To?.Node);
                    context.CarrierFor(edge.From?.Node, edge.To?.Node).AddDataEdge(wire);
                }
            }
        }

        private static void ConnectInputBindings(ImportContext context)
        {
            foreach (var binding in context.Document.InputBindings)
            {
                if (binding is null)
                {
                    continue;
                }
                var connected = false;
                if (binding.FromInput is not null &&
                    binding.To?.Node is not null && binding.To.Port is not null &&
                    context.InputVariables.TryGetValue(binding.FromInput, out var variable) &&
                    context.NodesById.TryGetValue(binding.To.Node, out var to))
                {
                    var variableNode = EnsureVariableNode(
                        context.Graph, variable, to.Position + new Vector2(-300f, 0f));
                    connected = TryConnect(
                        context.Graph,
                        FirstPort(variableNode as INode, PortDirection.Output),
                        to.GetInputPortByName(binding.To.Port));
                }
                if (!connected)
                {
                    var wire = new CarriedWire(EncodeInputBinding(binding));
                    wire.RefNode(context, binding.To?.Node);
                    wire.RefInput(context, binding.FromInput);
                    context.CarrierFor(binding.To?.Node).AddInputBinding(wire);
                }
            }
        }

        private static void ConnectOutputs(ImportContext context)
        {
            foreach (var output in context.Document.Outputs)
            {
                if (output is null)
                {
                    continue;
                }
                var connected = false;
                if (output.Id is not null &&
                    output.From?.Node is not null && output.From.Port is not null &&
                    context.OutputVariables.TryGetValue(output.Id, out var variable) &&
                    context.NodesById.TryGetValue(output.From.Node, out var from))
                {
                    var variableNode = EnsureVariableNode(
                        context.Graph, variable, from.Position + new Vector2(300f, 0f));
                    connected = TryConnect(
                        context.Graph,
                        from.GetOutputPortByName(output.From.Port),
                        FirstPort(variableNode as INode, PortDirection.Input));
                }
                if (!connected)
                {
                    var wire = new CarriedWire(EncodeOutput(output));
                    wire.RefNode(context, output.From?.Node);
                    wire.RefOutput(context, output.Id);
                    context.CarrierFor(output.From?.Node).AddOutput(wire);
                }
            }
        }

        private static void ApplyParameters(ImportContext context)
        {
            foreach (var nodeModel in context.Document.Nodes)
            {
                if (nodeModel is null || nodeModel.Id is null ||
                    !context.NodesById.TryGetValue(nodeModel.Id, out var node))
                {
                    continue;
                }
                foreach (var parameter in nodeModel.Parameters)
                {
                    // A port already fed by an explicit supply source keeps the
                    // connection and D reports the OW-VAL-006 the document
                    // carries (SPEC §A1); a port the Descriptor does not declare
                    // (missing or older Operation registration) cannot take the
                    // constant. Either way the parameter is carried verbatim so
                    // export re-emits it instead of silently dropping it
                    // (SPEC §0.6 SG-17).
                    var port = parameter.Key is null
                        ? null
                        : node.GetInputPortByName(parameter.Key);
                    var value = parameter.Value;
                    var applied =
                        port is not null && !port.IsConnected &&
                        port.DataType == typeof(OwValue) &&
                        value is not null &&
                        port.TrySetValue(new OwValue { json = value.CanonicalText });
                    if (!applied && parameter.Key is not null && value is not null)
                    {
                        context.Node(nodeModel.Id).AddParameter(
                            parameter.Key, value.ToToken());
                    }
                }
            }
        }

        // Folds the content the graph could not represent into each carrier
        // node's extrasJson (and the graph-level carrier), re-encoding the
        // label/policy extras the nodes were created with.
        private static void WriteCarried(ImportContext context)
        {
            foreach (var nodeModel in context.Document.Nodes)
            {
                if (nodeModel is null || nodeModel.Id is null ||
                    !context.NodesById.TryGetValue(nodeModel.Id, out var node) ||
                    !context.CarriedByNode.TryGetValue(nodeModel.Id, out var carried) ||
                    carried.IsEmpty)
                {
                    continue;
                }
                SetExtrasJson(node, EncodeExtras(nodeModel, carried));
            }
            if (!context.GraphCarried.IsEmpty)
            {
                context.Graph.carriedJson =
                    JsonValue.From(context.GraphCarried.ToJson()).CanonicalText;
            }
        }

        private static void SetExtrasJson(INode node, string extrasJson)
        {
            switch (node)
            {
                case StartNode start:
                    start.extrasJson = extrasJson;
                    break;
                case OperationNode operation:
                    operation.extrasJson = extrasJson;
                    break;
            }
        }

        private static IVariableNode? EnsureVariableNode(
            OpWeaveGraph graph, IVariable variable, Vector2 position)
        {
            var existing = new List<IVariableNode>();
            variable.GetNodes(existing);
            if (existing.Count != 0)
            {
                return existing[0];
            }
            return graph.AddVariableNode(variable, position) as IVariableNode;
        }

        private static IPort? FirstPort(INode? node, PortDirection direction)
        {
            if (node is null)
            {
                return null;
            }
            var ports = direction == PortDirection.Output
                ? node.GetOutputPorts()
                : node.GetInputPorts();
            return ports.FirstOrDefault(p => p.DataType == typeof(OwValue)) ??
                ports.FirstOrDefault();
        }

        private static bool TryConnect(OpWeaveGraph graph, IPort? output, IPort? input)
        {
            if (output is null || input is null)
            {
                return false;
            }
            try
            {
                return graph.Connect(output, input);
            }
            catch (ArgumentException)
            {
                // Graph Toolkit itself enforces direction/capacity/type rules
                // (L-03 6000.5 probe); a document violating them keeps the wire
                // in the SG-17 carrier and is reported by D-validation instead.
                return false;
            }
        }

        // ── Export (SPEC §H7.1, §0.6 SG-17) ────────────────────────────────

        /// <summary>
        /// Builds the workflow document <paramref name="graph"/> currently
        /// models: the seven GUI-owned top-level keys (SPEC §0.6 SG-17) come
        /// from the graph, every other top-level member —
        /// <c>schemaVersion</c>, <c>id</c>, <c>title</c>, <c>description</c>,
        /// <c>tags</c>, <c>x-*</c> — is copied unchanged from
        /// <paramref name="baseDocument"/>, the most recently read value of
        /// the JSON the graph is synced with.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        internal static Result<WorkflowDocument> Export(OpWeaveGraph graph, WorkflowDocument baseDocument)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (baseDocument is null)
            {
                throw new ArgumentNullException(nameof(baseDocument));
            }

            var nodes = new List<NodeData>();
            var controlEdges = new List<ControlEdge>();
            var dataEdges = new List<DataEdge>();
            var inputBindings = new List<InputBinding>();
            var outputs = new Dictionary<string, OutputAccumulator>(StringComparer.Ordinal);
            var layoutNodes = new Dictionary<string, LayoutNodePosition>(StringComparer.Ordinal);
            var connectedPorts = new List<IPort>();
            var carriedSources = new List<CarriedContent>();
            var identity = new CarriedIdentity();
            var provenance = new ImportProvenance(graph);

            foreach (var node in graph.GetNodes())
            {
                var nodeId = NodeIdOf(node);
                if (nodeId is not null)
                {
                    var nodeData = new NodeData { Id = nodeId };
                    string extrasJson;
                    if (node is StartNode start)
                    {
                        nodeData.Operation = StartOperationId;
                        nodeData.Version = StartVersion;
                        extrasJson = start.extrasJson;
                    }
                    else
                    {
                        var operation = (OperationNode)node;
                        nodeData.Operation = operation.operationId;
                        nodeData.Version = operation.version;
                        extrasJson = operation.extrasJson;
                    }
                    var extras = ParseExtras(extrasJson);
                    ApplyExtras(nodeData, extras);
                    // The import-time identity the SG-17 carrier resolves
                    // against: a carried wire referencing this node follows
                    // it across a nodeId edit — but only while the claimant
                    // is provably the element the import created (see
                    // EmitCarried and the class remarks).
                    identity.Nodes.Add(
                        nodeId,
                        StringOf(extras?["importedAs"]),
                        provenance.IsImportCreated(ImportTokenOf(node)));
                    nodeData.Parameters = CollectParameters(
                        node, nodeId, dataEdges, inputBindings);
                    var carried = CarriedContent.FromJson(extras?["carried"]);
                    if (carried is not null)
                    {
                        MergeCarriedParameters(nodeData, carried.Parameters);
                        carriedSources.Add(carried);
                    }
                    nodes.Add(nodeData);
                    layoutNodes[nodeId] = new LayoutNodePosition(node.Position.x, node.Position.y);
                }

                foreach (var port in node.GetOutputPorts())
                {
                    connectedPorts.Clear();
                    port.GetConnectedPorts(connectedPorts);
                    if (port.DataType == typeof(OwControl))
                    {
                        if (nodeId is null)
                        {
                            continue;
                        }
                        foreach (var target in connectedPorts)
                        {
                            var targetId = ControlTargetId(target);
                            if (targetId is not null)
                            {
                                controlEdges.Add(new ControlEdge(nodeId, targetId, port.Name));
                            }
                        }
                    }
                    else if (port.DataType == typeof(OwValue))
                    {
                        if (nodeId is null)
                        {
                            continue;
                        }
                        foreach (var target in connectedPorts)
                        {
                            if (INodeExtensions.GetNode(target) is IVariableNode variableNode &&
                                variableNode.Variable is { } variable &&
                                variable.VariableKind == VariableKind.Output &&
                                variable.Name is { } outputId)
                            {
                                if (!outputs.TryGetValue(outputId, out var accumulator))
                                {
                                    outputs[outputId] = accumulator =
                                        new OutputAccumulator(variable);
                                }
                                accumulator.Sources.Add(new PortRef(nodeId, port.Name));
                            }
                        }
                    }
                }
            }

            var inputs = graph.GetVariables()
                .Where(v => v.VariableKind == VariableKind.Input && v.Name is not null)
                .Select(DecodeInput)
                .ToList();
            var outputRecords = outputs
                .Select(pair => new WorkflowOutput(
                    pair.Key,
                    pair.Value.Sources
                        .OrderBy(p => p.Node, StringComparer.Ordinal)
                        .ThenBy(p => p.Port, StringComparer.Ordinal)
                        .First(),
                    DecodeOutputDescription(pair.Value.Variable)))
                .ToList();

            // Re-emit the content the last import could not apply (SPEC §0.6
            // SG-17): a carried wire survives while every element it
            // referenced at import — node, Input-kind variable, Output-kind
            // variable — still resolves uniquely and exactly (see the class
            // remarks). References resolve through the "importedAs" identity
            // each imported element records on its own carrier, counted
            // against the element that uniquely owns an issued import token,
            // so a rename rewrites the emitted reference to the claimant's
            // current name, an explicitly deleted claimant drops its wires
            // with it, and anything ambiguous or unprovable refuses the item
            // with a diagnostic instead of guessing. A live wire
            // identical to a carried one already covers it.
            foreach (var variable in graph.GetVariables())
            {
                if (variable.Name is not { } variableName)
                {
                    continue;
                }
                if (variable.VariableKind == VariableKind.Input)
                {
                    identity.Inputs.Add(
                        variableName,
                        ImportedAsOf(variable),
                        provenance.IsImportCreated(ImportTokenOf(variable)));
                }
                else if (variable.VariableKind == VariableKind.Output)
                {
                    identity.Outputs.Add(
                        variableName,
                        ImportedAsOf(variable),
                        provenance.IsImportCreated(ImportTokenOf(variable)));
                }
            }
            var graphCarried = CarriedContent.FromJson(ParseToken(graph.carriedJson));
            if (graphCarried is not null)
            {
                carriedSources.Add(graphCarried);
            }
            var emittedOutputIds = new HashSet<string>(
                outputRecords.Select(o => o.Id), StringComparer.Ordinal);
            var carriedRefusals = new List<Diagnostic>();
            foreach (var carried in carriedSources)
            {
                EmitCarried(
                    carried, identity,
                    controlEdges, dataEdges, inputBindings, outputRecords, emittedOutputIds,
                    carriedRefusals);
            }

            var extensions = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var pair in baseDocument.Extensions)
            {
                extensions[pair.Key] = pair.Value.ToToken();
            }

            var data = new WorkflowDocumentData
            {
                SchemaVersion = baseDocument.SchemaVersion,
                Id = baseDocument.Id,
                Title = baseDocument.Title,
                Description = baseDocument.Description,
                // tags is not GUI-owned: its value comes verbatim from the
                // base document. Member-level presence (an explicit
                // "tags": [] versus no member) cannot be expressed in the
                // document model; it is carried from the current file by the
                // save path (GraphJsonFile.SaveToFile, SPEC §0.6 SG-17).
                Tags = baseDocument.Tags.Count == 0 ? null : new List<string>(baseDocument.Tags),
                Inputs = inputs.Count == 0 ? null : inputs,
                Outputs = outputRecords.Count == 0 ? null : outputRecords,
                Nodes = nodes,
                ControlEdges = controlEdges,
                DataEdges = dataEdges.Count == 0 ? null : dataEdges,
                InputBindings = inputBindings.Count == 0 ? null : inputBindings,
                // The graph owns every node position; notes have no graph
                // element, so the base document's notes are kept rather than
                // silently dropped (SPEC §A8 shape requires both members).
                Layout = new LayoutInfo(
                    layoutNodes,
                    baseDocument.Layout?.Notes ?? Array.Empty<LayoutNote>()),
                Extensions = extensions.Count == 0 ? null : extensions,
            };
            var created = WorkflowDocument.Create(data);
            // A refused carried item is not emitted and must not be dropped
            // silently: the successful export carries its refusal
            // diagnostics so the caller (SaveToFile) can refuse the write
            // unless the human explicitly overwrites (SPEC §0.6 SG-17).
            return !created.Ok || carriedRefusals.Count == 0
                ? created
                : Result<WorkflowDocument>.Success(created.Value!, carriedRefusals);
        }

        private sealed class OutputAccumulator
        {
            public OutputAccumulator(IVariable variable) => Variable = variable;

            public IVariable Variable { get; }
            public List<PortRef> Sources { get; } = new List<PortRef>();
        }

        // Internal so live validation (SPEC §H8) can map diagnostics back
        // to nodes by their workflow nodeId.
        internal static string? NodeIdOf(INode node) => node switch
        {
            StartNode start => start.nodeId,
            OperationNode operation => operation.nodeId,
            _ => null,
        };

        // A control wire lands only on the fixed OwControl input of a node the
        // WF model knows; anything else is GUI-only decoration.
        private static string? ControlTargetId(IPort target)
        {
            if (target.Direction != PortDirection.Input ||
                target.DataType != typeof(OwControl) ||
                !string.Equals(target.Name, OperationNode.ControlInputName, StringComparison.Ordinal))
            {
                return null;
            }
            return NodeIdOf(INodeExtensions.GetNode(target));
        }

        private static Dictionary<string, JToken>? CollectParameters(
            INode node,
            string nodeId,
            List<DataEdge> dataEdges,
            List<InputBinding> inputBindings)
        {
            Dictionary<string, JToken>? parameters = null;
            var sources = new List<IPort>();
            foreach (var port in node.GetInputPorts())
            {
                if (port.DataType != typeof(OwValue))
                {
                    continue;
                }
                if (port.IsConnected)
                {
                    sources.Clear();
                    port.GetConnectedPorts(sources);
                    foreach (var source in sources)
                    {
                        switch (INodeExtensions.GetNode(source))
                        {
                            case StartNode start:
                                dataEdges.Add(new DataEdge(
                                    new PortRef(start.nodeId, source.Name),
                                    new PortRef(nodeId, port.Name)));
                                break;
                            case OperationNode operation:
                                dataEdges.Add(new DataEdge(
                                    new PortRef(operation.nodeId, source.Name),
                                    new PortRef(nodeId, port.Name)));
                                break;
                            case IVariableNode variableNode:
                                if (variableNode.Variable?.VariableKind == VariableKind.Input &&
                                    variableNode.Variable.Name is { } inputId)
                                {
                                    inputBindings.Add(new InputBinding(
                                        inputId, new PortRef(nodeId, port.Name)));
                                }
                                break;
                            case IConstantNode constant:
                                // An OwValue constant node is a constant drawn
                                // as a node; it folds back into the port's
                                // parameter (SPEC §H7.2 lists constants among
                                // the assembly API the GUI uses).
                                if (constant.TryGetValue<OwValue>(out var constantValue) &&
                                    TryParse(constantValue?.json, out var constantJson))
                                {
                                    (parameters ??= NewParameters())[port.Name] = constantJson!;
                                }
                                break;
                        }
                    }
                }
                else if (port.TryGetValue<OwValue>(out var value) &&
                    TryParse(value?.json, out var json))
                {
                    (parameters ??= NewParameters())[port.Name] = json!;
                }
            }
            return parameters;
        }

        private static Dictionary<string, JToken> NewParameters() =>
            new Dictionary<string, JToken>(StringComparer.Ordinal);

        // The port constant payload is Canonical JSON text (SPEC §H7.2); an
        // empty or unparseable payload carries no parameter.
        private static bool TryParse(string? json, out JToken? token)
        {
            token = null;
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }
            var parsed = JsonValue.Parse(json);
            if (!parsed.Ok)
            {
                return false;
            }
            token = parsed.Value!.ToToken();
            return true;
        }

        // ── Serialized payload codecs (local representation) ───────────────

        private static string EncodeExtras(NodeModel node, CarriedContent? carried = null)
        {
            // "importedAs" is the node's import-time identity: carried wires
            // resolve their references through it at export, so editing
            // nodeId keeps the wire instead of dropping it (SPEC §0.6
            // SG-17). It is converter-internal state — ApplyExtras never
            // writes it back into the document.
            var extras = new JObject { ["importedAs"] = node.Id };
            if (node.Label is not null)
            {
                extras["label"] = node.Label;
            }
            if (node.Policy is not null)
            {
                var policy = new JObject();
                if (node.Policy.TimeoutSeconds is not null)
                {
                    policy["timeoutSeconds"] = node.Policy.TimeoutSeconds.Value;
                }
                if (node.Policy.MaxAttempts is not null)
                {
                    policy["maxAttempts"] = node.Policy.MaxAttempts.Value;
                }
                extras["policy"] = policy;
            }
            if (carried is not null && !carried.IsEmpty)
            {
                extras["carried"] = carried.ToJson();
            }
            return extras.Count == 0 ? "" : JsonValue.From(extras).CanonicalText;
        }

        private static JObject? ParseExtras(string? extrasJson) =>
            TryParse(extrasJson, out var token) && token is JObject extras ? extras : null;

        private static JToken? ParseToken(string? json) =>
            TryParse(json, out var token) ? token : null;

        private static void ApplyExtras(NodeData node, JObject? extras)
        {
            if (extras is null)
            {
                return;
            }
            if (extras["label"] is { Type: JTokenType.String } label)
            {
                node.Label = (string?)((JValue)label).Value;
            }
            if (extras["policy"] is JObject policy)
            {
                node.Policy = new NodePolicy(
                    OptionalInt(policy["timeoutSeconds"]),
                    OptionalInt(policy["maxAttempts"]));
            }
        }

        private static int? OptionalInt(JToken? token) =>
            token is { Type: JTokenType.Integer }
                ? (int?)Convert.ToInt64(((JValue)token).Value, CultureInfo.InvariantCulture)
                : null;

        private static string EncodeInputPayload(WorkflowInput input, string importToken)
        {
            var payload = new JObject
            {
                ["type"] = input.Type,
                ["required"] = input.Required,
                // Same import-time identity as the node "importedAs": carried
                // input bindings resolve through it across a variable rename.
                // "importToken" is the variable's serialized provenance (see
                // the class remarks). DecodeInput reads only the §A6
                // members, so neither leaks into the exported document.
                ["importedAs"] = input.Id,
                ["importToken"] = importToken,
            };
            if (input.Default is not null)
            {
                payload["default"] = input.Default.ToToken();
            }
            if (input.Description is not null)
            {
                payload["description"] = input.Description;
            }
            return JsonValue.From(payload).CanonicalText;
        }

        private static WorkflowInput DecodeInput(IVariable variable)
        {
            var id = variable.Name!;
            if (variable.TryGetDefaultValue<OwValue>(out var payload) &&
                TryParse(payload?.json, out var token) &&
                token is JObject payloadObject)
            {
                var type = payloadObject["type"] is { Type: JTokenType.String } typeToken
                    ? (string)((JValue)typeToken).Value!
                    : DefaultInputType;
                var required = payloadObject["required"] is { Type: JTokenType.Boolean } requiredToken
                    && (bool)((JValue)requiredToken).Value!;
                var description = payloadObject["description"] is { Type: JTokenType.String } descriptionToken
                    ? (string?)((JValue)descriptionToken).Value
                    : null;
                return new WorkflowInput(
                    id,
                    type,
                    required,
                    payloadObject["default"] is { } defaultToken ? JsonValue.From(defaultToken) : null,
                    description);
            }
            return new WorkflowInput(id, DefaultInputType, false);
        }

        private static string EncodeOutputPayload(WorkflowOutput output, string importToken)
        {
            // "importedAs" is the output variable's import-time identity and
            // "importToken" its serialized provenance (see
            // EncodeInputPayload); the description is the only document
            // member the payload round-trips.
            var payload = new JObject
            {
                ["importedAs"] = output.Id,
                ["importToken"] = importToken,
            };
            if (output.Description is not null)
            {
                payload["description"] = output.Description;
            }
            return JsonValue.From(payload).CanonicalText;
        }

        // The import-time name an imported variable keeps in its payload;
        // a GUI-created variable carries none and resolves by its current
        // name only.
        private static string? ImportedAsOf(IVariable variable) =>
            variable.TryGetDefaultValue<OwValue>(out var payload) &&
            TryParse(payload?.json, out var token) &&
            token is JObject payloadObject
                ? StringOf(payloadObject["importedAs"])
                : null;

        // The serialized import token an element was issued by the last
        // import (see the class remarks): the importToken field of a node,
        // the "importToken" payload member of a variable. Everything else —
        // a GUI-created element, a marker-free one, an element kind the WF
        // model never indexes — carries none the provenance accepts.
        private static string? ImportTokenOf(INode node) => node switch
        {
            StartNode start => start.importToken,
            OperationNode operation => operation.importToken,
            _ => null,
        };

        private static string? ImportTokenOf(IVariable variable) =>
            variable.TryGetDefaultValue<OwValue>(out var payload) &&
            TryParse(payload?.json, out var token) &&
            token is JObject payloadObject
                ? StringOf(payloadObject["importToken"])
                : null;

        private static string? DecodeOutputDescription(IVariable variable) =>
            variable.TryGetDefaultValue<OwValue>(out var payload) &&
            TryParse(payload?.json, out var token) &&
            token is JObject payloadObject &&
            payloadObject["description"] is { Type: JTokenType.String } description
                ? (string?)((JValue)description).Value
                : null;

        // ── SG-17 carrier: content the graph cannot hold ───────────────────
        //
        // The document members an import could not apply — a parameter or wire
        // whose port the Registry Descriptor does not declare, or a wire Graph
        // Toolkit itself refused — are kept verbatim under "carried" in the
        // carrier's extrasJson (OpWeaveGraph.carriedJson when no endpoint node
        // exists) and re-emitted on export, so a JSON → graph → JSON
        // round-trip never silently drops JSON content (SPEC §H7, §0.6 SG-17).
        // Every imported node and Input/Output variable records its
        // import-time name as "importedAs" on its own serialized carrier,
        // plus a fresh random "importToken" issued into
        // OpWeaveGraph.importedTokens; a carried wire's references resolve
        // through "importedAs", so renaming the element cannot orphan the
        // wire — only deleting it can. Because a GUI duplicate copies the
        // serialized markers and carried wires verbatim, re-emission counts
        // "importedAs" claims against the element that uniquely owns an
        // issued token and emits an item only while every reference
        // resolves uniquely and exactly — a rename, a proven delete, or a
        // refusal diagnostic, never a silent rewire, resurrection or drop
        // (see the class remarks).

        // The per-import view of the rebuild: the graph, the document, the
        // recreated nodes/variables and the verbatim content that could not
        // be applied, split per carrier node and graph-level.
        private sealed class ImportContext
        {
            internal ImportContext(OpWeaveGraph graph, WorkflowDocument document)
            {
                Graph = graph;
                Document = document;
            }

            internal OpWeaveGraph Graph { get; }
            internal WorkflowDocument Document { get; }
            internal Dictionary<string, INode> NodesById { get; } =
                new Dictionary<string, INode>(StringComparer.Ordinal);

            // The import tokens issued to elements this import creates;
            // OpWeaveGraph.importedTokens keeps the set (see the class
            // remarks) once the rebuild completes.
            internal List<string> IssuedTokens { get; } = new List<string>();

            // A fresh random token for one element this import creates —
            // serialized provenance, never a document value.
            internal string IssueToken()
            {
                var token = Guid.NewGuid().ToString("N");
                IssuedTokens.Add(token);
                return token;
            }

            internal Dictionary<string, IVariable> InputVariables { get; } =
                new Dictionary<string, IVariable>(StringComparer.Ordinal);
            internal Dictionary<string, IVariable> OutputVariables { get; } =
                new Dictionary<string, IVariable>(StringComparer.Ordinal);
            internal Dictionary<string, CarriedContent> CarriedByNode { get; } =
                new Dictionary<string, CarriedContent>(StringComparer.Ordinal);
            internal CarriedContent GraphCarried { get; } = new CarriedContent();

            internal CarriedContent Node(string nodeId)
            {
                if (!CarriedByNode.TryGetValue(nodeId, out var carried))
                {
                    CarriedByNode[nodeId] = carried = new CarriedContent();
                }
                return carried;
            }

            // The node a dropped wire is stored on: its preferred endpoint
            // when the document declares that node, the alternate endpoint
            // when only it exists, or graph-level when neither does — so the
            // wire lives as long as the node it belongs to.
            internal CarriedContent CarrierFor(string? preferred, string? alternate = null)
            {
                if (preferred is not null && NodesById.ContainsKey(preferred))
                {
                    return Node(preferred);
                }
                if (alternate is not null && NodesById.ContainsKey(alternate))
                {
                    return Node(alternate);
                }
                return GraphCarried;
            }
        }

        // The serialized provenance the last import left: the issued-token
        // set on the graph plus, for every element now in the graph, the
        // token its own serialized state claims. An element is the
        // import-created claimant exactly while its token is issued and no
        // second element carries it — a GUI duplicate or paste copies the
        // serialized marker verbatim, so both copies lose sole ownership
        // and resolve as foreign rather than one silently taking over the
        // recorded identity.
        private sealed class ImportProvenance
        {
            private readonly HashSet<string> _issued;
            private readonly Dictionary<string, int> _carriers;

            internal ImportProvenance(OpWeaveGraph graph)
            {
                _issued = new HashSet<string>(
                    graph.importedTokens ?? Enumerable.Empty<string>(),
                    StringComparer.Ordinal);
                _carriers = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var node in graph.GetNodes())
                {
                    Count(ImportTokenOf(node));
                }
                foreach (var variable in graph.GetVariables())
                {
                    Count(ImportTokenOf(variable));
                }
            }

            internal bool IsImportCreated(string? token) =>
                !string.IsNullOrEmpty(token) &&
                _issued.Contains(token) &&
                _carriers.TryGetValue(token, out var count) &&
                count == 1;

            private void Count(string? token)
            {
                if (string.IsNullOrEmpty(token))
                {
                    return;
                }
                _carriers[token] = _carriers.TryGetValue(token, out var count)
                    ? count + 1
                    : 1;
            }
        }

        // The element-identity view one export resolves carried references
        // against, split per element kind. A carried reference resolves
        // only uniquely and exactly: exactly one element the import itself
        // created claims the recorded "importedAs" (the emitted reference
        // follows it to the claimant's current name), or no element claims
        // it and exactly one element carrying no "importedAs" has the
        // recorded name and is provably the import-created claimant (the
        // only element whose identity is unaccounted for). Everything else
        // is never guessed.
        private sealed class CarriedIdentity
        {
            internal ElementIndex Nodes { get; } = new ElementIndex();
            internal ElementIndex Inputs { get; } = new ElementIndex();
            internal ElementIndex Outputs { get; } = new ElementIndex();
        }

        // One graph element as the carrier sees it: its current name, the
        // "importedAs" marker its serialized carrier holds (null when the
        // element never recorded one or lost it), and whether the element
        // is the import-created claimant — a GUI duplicate copies every
        // serialized marker, so no element can solely own an issued token
        // it did not get from the import itself.
        private sealed class CarrierElement
        {
            internal CarrierElement(string name, string? importedAs, bool imported)
            {
                Name = name;
                ImportedAs = importedAs;
                Imported = imported;
            }

            internal string Name { get; }
            internal string? ImportedAs { get; }
            internal bool Imported { get; }
        }

        // The per-kind element list plus the counts the delete/name proofs
        // need: elements the recorded import did not create, and
        // import-created elements that lost their "importedAs" marker.
        // While both are zero every element is an import-created element
        // still bearing its marker — the recorded claimant of an
        // unclaimed, unnamed identity is then provably deleted.
        private sealed class ElementIndex
        {
            internal List<CarrierElement> Elements { get; } = new List<CarrierElement>();
            internal int Foreign { get; private set; }
            internal int Unmarked { get; private set; }

            internal void Add(string name, string? importedAs, bool imported)
            {
                Elements.Add(new CarrierElement(name, importedAs, imported));
                if (!imported)
                {
                    Foreign++;
                }
                else if (importedAs is null)
                {
                    Unmarked++;
                }
            }
        }

        // The outcome of resolving one recorded element reference.
        private enum RefResolution
        {
            // Exactly one provable claimant or name-match: emit rewritten.
            Resolved = 0,

            // The element is provably deleted: the only silent drop allowed.
            Deleted = 1,

            // Ambiguous or unprovable: refuse with a diagnostic, never guess.
            Refused = 2,
        }

        private static RefResolution ResolveRef(
            ElementIndex index,
            string kind,
            string recorded,
            out string current,
            out string? reason)
        {
            current = null!;
            reason = null;
            List<CarrierElement>? claimants = null;
            List<CarrierElement>? named = null;
            foreach (var element in index.Elements)
            {
                if (string.Equals(element.ImportedAs, recorded, StringComparison.Ordinal))
                {
                    (claimants ??= new List<CarrierElement>()).Add(element);
                }
                if (string.Equals(element.Name, recorded, StringComparison.Ordinal))
                {
                    (named ??= new List<CarrierElement>()).Add(element);
                }
            }
            if (claimants is not null)
            {
                if (claimants.Count == 1 && claimants[0].Imported)
                {
                    current = claimants[0].Name;
                    return RefResolution.Resolved;
                }
                reason = claimants.Count == 1
                    ? $"{kind} id '{recorded}' is claimed only by an element the import did not create"
                    : $"{kind} id '{recorded}' is claimed by {claimants.Count} elements";
                return RefResolution.Refused;
            }
            if (named is not null)
            {
                var only = named.Count == 1 ? named[0] : null;
                if (only is not null && only.Imported && only.ImportedAs is null &&
                    index.Foreign == 0 && index.Unmarked == 1)
                {
                    // The recorded marker is gone but the element is
                    // provably the import-created claimant: every other
                    // element still bears its own marker, so it is the
                    // only one whose identity is unaccounted for.
                    current = recorded;
                    return RefResolution.Resolved;
                }
                reason = named.Count > 1
                    ? $"{kind} id '{recorded}' names {named.Count} elements"
                    : !only!.Imported
                        ? $"{kind} id '{recorded}' names an element the import did not create"
                        : only.ImportedAs is not null
                            ? $"{kind} id '{recorded}' names an element that claims the different imported identity '{only.ImportedAs}'"
                            : $"{kind} id '{recorded}' names an element whose import identity is not provable";
                return RefResolution.Refused;
            }
            if (index.Foreign == 0 && index.Unmarked == 0)
            {
                // Every element is an import-created instance still bearing
                // its marker: the recorded identity's only claimant was
                // explicitly deleted — the one silent drop that stays
                // allowed.
                return RefResolution.Deleted;
            }
            reason = $"{kind} id '{recorded}' is gone and the delete is not provable: " +
                "an element lost its import marker or was created after the import";
            return RefResolution.Refused;
        }

        // The outcome of resolving every element one carried wire
        // referenced at import.
        private enum WireResolution
        {
            // Every reference resolved uniquely and exactly: emit rewritten.
            Resolved = 0,

            // A referenced element is provably deleted and none refused:
            // the wire dies with it, the one silent drop allowed.
            Deleted = 1,

            // At least one reference is ambiguous or unprovable: the item is
            // not emitted and a diagnostic names it and its reasons.
            Refused = 2,
        }

        // One dropped wire plus the elements it referenced at import,
        // recorded by their import-time names: at export each reference
        // resolves to the element's current name through its "importedAs"
        // marker and the emitted record is rewritten to it, so a rename
        // keeps the wire while a deleted element — the person explicitly
        // removed it, so its wires die with it — resolves to nothing and
        // drops the wire. A wire whose endpoint the document never declared
        // recorded no reference for it and stays preserved.
        private sealed class CarriedWire
        {
            internal CarriedWire(JObject item) => Item = item;

            internal JObject Item { get; }
            internal List<string> Nodes { get; } = new List<string>();
            internal List<string> Inputs { get; } = new List<string>();
            internal List<string> Outputs { get; } = new List<string>();

            internal void RefNode(ImportContext context, string? nodeId) =>
                AddRef(Nodes, nodeId, context.NodesById.ContainsKey);

            internal void RefInput(ImportContext context, string? inputId) =>
                AddRef(Inputs, inputId, context.InputVariables.ContainsKey);

            internal void RefOutput(ImportContext context, string? outputId) =>
                AddRef(Outputs, outputId, context.OutputVariables.ContainsKey);

            // Resolves every recorded reference to the referenced element's
            // current name, filling the recorded → current maps the emitter
            // rewrites the record with. A provably deleted element drops the
            // wire with it; an ambiguous or unprovable one refuses it —
            // every refusal reason is collected in
            // <paramref name="refusalReasons"/>.
            internal WireResolution Resolve(
                CarriedIdentity identity,
                out Dictionary<string, string> nodeRenames,
                out Dictionary<string, string> inputRenames,
                out Dictionary<string, string> outputRenames,
                out List<string> refusalReasons)
            {
                nodeRenames = new Dictionary<string, string>(StringComparer.Ordinal);
                inputRenames = new Dictionary<string, string>(StringComparer.Ordinal);
                outputRenames = new Dictionary<string, string>(StringComparer.Ordinal);
                refusalReasons = new List<string>();
                var deleted = false;
                ResolveRefs(Nodes, identity.Nodes, "node", nodeRenames, refusalReasons, ref deleted);
                ResolveRefs(Inputs, identity.Inputs, "input", inputRenames, refusalReasons, ref deleted);
                ResolveRefs(Outputs, identity.Outputs, "output", outputRenames, refusalReasons, ref deleted);
                if (refusalReasons.Count != 0)
                {
                    return WireResolution.Refused;
                }
                return deleted ? WireResolution.Deleted : WireResolution.Resolved;
            }

            private static void ResolveRefs(
                List<string> recordedIds,
                ElementIndex index,
                string kind,
                Dictionary<string, string> renames,
                List<string> refusalReasons,
                ref bool deleted)
            {
                foreach (var recorded in recordedIds)
                {
                    switch (ResolveRef(index, kind, recorded, out var current, out var reason))
                    {
                        case RefResolution.Resolved:
                            renames[recorded] = current;
                            break;
                        case RefResolution.Deleted:
                            deleted = true;
                            break;
                        default:
                            refusalReasons.Add(reason!);
                            break;
                    }
                }
            }

            internal JObject ToJson()
            {
                var json = new JObject { ["item"] = Item.DeepClone() };
                WriteRefs(json, "nodes", Nodes);
                WriteRefs(json, "inputs", Inputs);
                WriteRefs(json, "outputs", Outputs);
                return json;
            }

            internal static CarriedWire? FromJson(JToken? token)
            {
                if (token is not JObject json || json["item"] is not JObject item)
                {
                    return null;
                }
                var wire = new CarriedWire(item);
                ReadRefs(wire.Nodes, json["nodes"]);
                ReadRefs(wire.Inputs, json["inputs"]);
                ReadRefs(wire.Outputs, json["outputs"]);
                return wire;
            }

            private static void AddRef(List<string> refs, string? id, Func<string, bool> exists)
            {
                if (id is not null && exists(id) && !refs.Contains(id))
                {
                    refs.Add(id);
                }
            }

            private static void WriteRefs(JObject json, string key, List<string> refs)
            {
                if (refs.Count == 0)
                {
                    return;
                }
                var array = new JArray();
                foreach (var id in refs)
                {
                    array.Add(id);
                }
                json[key] = array;
            }

            private static void ReadRefs(List<string> refs, JToken? token)
            {
                if (token is not JArray array)
                {
                    return;
                }
                foreach (var element in array)
                {
                    if (element is { Type: JTokenType.String } &&
                        ((JValue)element).Value is string id)
                    {
                        refs.Add(id);
                    }
                }
            }
        }

        // The verbatim document content one carrier (a node or the graph)
        // holds: parameters the node's ports could not take and wires the
        // graph could not connect.
        private sealed class CarriedContent
        {
            internal JObject? Parameters;
            internal List<CarriedWire>? ControlEdges;
            internal List<CarriedWire>? DataEdges;
            internal List<CarriedWire>? InputBindings;
            internal List<CarriedWire>? Outputs;

            internal bool IsEmpty =>
                Parameters is null && ControlEdges is null && DataEdges is null &&
                InputBindings is null && Outputs is null;

            internal void AddParameter(string port, JToken value) =>
                (Parameters ??= new JObject())[port] = value;

            internal void AddControlEdge(CarriedWire wire) =>
                (ControlEdges ??= new List<CarriedWire>()).Add(wire);

            internal void AddDataEdge(CarriedWire wire) =>
                (DataEdges ??= new List<CarriedWire>()).Add(wire);

            internal void AddInputBinding(CarriedWire wire) =>
                (InputBindings ??= new List<CarriedWire>()).Add(wire);

            internal void AddOutput(CarriedWire wire) =>
                (Outputs ??= new List<CarriedWire>()).Add(wire);

            internal JObject ToJson()
            {
                var json = new JObject();
                if (Parameters is not null)
                {
                    json["parameters"] = Parameters.DeepClone();
                }
                WriteWires(json, "controlEdges", ControlEdges);
                WriteWires(json, "dataEdges", DataEdges);
                WriteWires(json, "inputBindings", InputBindings);
                WriteWires(json, "outputs", Outputs);
                return json;
            }

            internal static CarriedContent? FromJson(JToken? token)
            {
                if (token is not JObject json)
                {
                    return null;
                }
                var carried = new CarriedContent
                {
                    Parameters = json["parameters"] as JObject,
                    ControlEdges = ReadWires(json["controlEdges"]),
                    DataEdges = ReadWires(json["dataEdges"]),
                    InputBindings = ReadWires(json["inputBindings"]),
                    Outputs = ReadWires(json["outputs"]),
                };
                return carried.IsEmpty ? null : carried;
            }

            private static void WriteWires(JObject json, string key, List<CarriedWire>? wires)
            {
                if (wires is null || wires.Count == 0)
                {
                    return;
                }
                var array = new JArray();
                foreach (var wire in wires)
                {
                    array.Add(wire.ToJson());
                }
                json[key] = array;
            }

            private static List<CarriedWire>? ReadWires(JToken? token)
            {
                if (token is not JArray array)
                {
                    return null;
                }
                List<CarriedWire>? wires = null;
                foreach (var element in array)
                {
                    if (CarriedWire.FromJson(element) is { } wire)
                    {
                        (wires ??= new List<CarriedWire>()).Add(wire);
                    }
                }
                return wires;
            }
        }

        // Re-emits the content one carrier holds (see the class remarks). A
        // carried wire's references resolve through the element identity the
        // import recorded, so the emitted record names the elements' current
        // ids — a rename keeps the wire, a proven delete drops it, and an
        // ambiguous or unprovable reference refuses it with a
        // diagnostic instead of guessing. A live wire identical to a
        // carried one already covers it — drawing the same wire by hand
        // supersedes the carried copy — and a carried parameter merges only
        // under a name no live constant claims.
        private static void EmitCarried(
            CarriedContent carried,
            CarriedIdentity identity,
            List<ControlEdge> controlEdges,
            List<DataEdge> dataEdges,
            List<InputBinding> inputBindings,
            List<WorkflowOutput> outputs,
            HashSet<string> emittedOutputIds,
            List<Diagnostic> diagnostics)
        {
            foreach (var wire in Wires(carried.ControlEdges))
            {
                switch (wire.Resolve(identity, out var nodes, out _, out _, out var reasons))
                {
                    case WireResolution.Resolved
                        when DecodeControlEdge(wire.Item) is { } edge:
                        var renamed = new ControlEdge(
                            Rename(edge.From, nodes), Rename(edge.To, nodes), edge.FromPort);
                        if (!controlEdges.Contains(renamed))
                        {
                            controlEdges.Add(renamed);
                        }
                        break;
                    case WireResolution.Refused:
                        diagnostics.Add(CarriedRefusal("control edge", wire, reasons));
                        break;
                }
            }
            foreach (var wire in Wires(carried.DataEdges))
            {
                switch (wire.Resolve(identity, out var nodes, out _, out _, out var reasons))
                {
                    case WireResolution.Resolved
                        when DecodeDataEdge(wire.Item) is { } edge:
                        var renamed = new DataEdge(
                            RenamePortRef(edge.From, nodes), RenamePortRef(edge.To, nodes));
                        if (!dataEdges.Contains(renamed))
                        {
                            dataEdges.Add(renamed);
                        }
                        break;
                    case WireResolution.Refused:
                        diagnostics.Add(CarriedRefusal("data edge", wire, reasons));
                        break;
                }
            }
            foreach (var wire in Wires(carried.InputBindings))
            {
                switch (wire.Resolve(identity, out var nodes, out var inputs, out _, out var reasons))
                {
                    case WireResolution.Resolved
                        when DecodeInputBinding(wire.Item) is { } binding:
                        var renamed = new InputBinding(
                            Rename(binding.FromInput, inputs), RenamePortRef(binding.To, nodes));
                        if (!inputBindings.Contains(renamed))
                        {
                            inputBindings.Add(renamed);
                        }
                        break;
                    case WireResolution.Refused:
                        diagnostics.Add(CarriedRefusal("input binding", wire, reasons));
                        break;
                }
            }
            foreach (var wire in Wires(carried.Outputs))
            {
                switch (wire.Resolve(identity, out var nodes, out _, out var outputIds, out var reasons))
                {
                    case WireResolution.Resolved
                        when DecodeOutput(wire.Item) is { } output:
                        var renamed = new WorkflowOutput(
                            Rename(output.Id, outputIds), RenamePortRef(output.From, nodes),
                            output.Description);
                        if (emittedOutputIds.Add(renamed.Id))
                        {
                            outputs.Add(renamed);
                        }
                        break;
                    case WireResolution.Refused:
                        diagnostics.Add(CarriedRefusal("output", wire, reasons));
                        break;
                }
            }
        }

        // The SG-17 diagnostic one refused carried item produces: the
        // verbatim item and every unresolved reference's reason, so a
        // person can fix the graph (or overwrite explicitly to drop the
        // item). Emitted at the OW-UNITY-002 SPEC §J default severity —
        // a published code's severity is never overridden.
        private static Diagnostic CarriedRefusal(
            string kind, CarriedWire wire, List<string> reasons) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_002,
                $"Carried {kind} {JsonValue.From(wire.Item).CanonicalText} was not re-emitted: " +
                    $"{string.Join("; ", reasons)}.",
                hint: "Rename or delete the ambiguous graph elements so every reference " +
                    "resolves uniquely, reload the JSON, or overwrite explicitly to drop " +
                    "the carried item.");

        private static string Rename(string recorded, Dictionary<string, string> renames) =>
            renames.TryGetValue(recorded, out var current) ? current : recorded;

        private static PortRef RenamePortRef(PortRef portRef, Dictionary<string, string> nodeRenames) =>
            new PortRef(Rename(portRef.Node, nodeRenames), portRef.Port);

        private static IEnumerable<CarriedWire> Wires(List<CarriedWire>? wires) =>
            wires ?? Enumerable.Empty<CarriedWire>();

        private static void MergeCarriedParameters(NodeData node, JObject? carried)
        {
            if (carried is null || carried.Count == 0)
            {
                return;
            }
            var parameters = node.Parameters as Dictionary<string, JToken> ?? NewParameters();
            foreach (var property in carried.Properties())
            {
                // A live port constant wins: it is the value the graph
                // actually holds after the descriptor appeared.
                if (!parameters.ContainsKey(property.Name))
                {
                    parameters[property.Name] = property.Value.DeepClone();
                }
            }
            node.Parameters = parameters;
        }

        private static JObject EncodeControlEdge(ControlEdge edge) =>
            new JObject
            {
                ["from"] = edge.From,
                ["to"] = edge.To,
                ["fromPort"] = edge.FromPort,
            };

        private static JObject EncodeDataEdge(DataEdge edge) =>
            new JObject
            {
                ["from"] = EncodePortRef(edge.From),
                ["to"] = EncodePortRef(edge.To),
            };

        private static JObject EncodeInputBinding(InputBinding binding) =>
            new JObject
            {
                ["fromInput"] = binding.FromInput,
                ["to"] = EncodePortRef(binding.To),
            };

        private static JObject EncodeOutput(WorkflowOutput output)
        {
            var item = new JObject
            {
                ["id"] = output.Id,
                ["from"] = EncodePortRef(output.From),
            };
            if (output.Description is not null)
            {
                item["description"] = output.Description;
            }
            return item;
        }

        private static JObject EncodePortRef(PortRef? portRef) =>
            portRef is null
                ? new JObject()
                : new JObject
                {
                    ["node"] = portRef.Node,
                    ["port"] = portRef.Port,
                };

        private static ControlEdge? DecodeControlEdge(JObject item)
        {
            var from = StringOf(item["from"]);
            var to = StringOf(item["to"]);
            return from is null || to is null
                ? null
                : new ControlEdge(from, to, StringOf(item["fromPort"]) ?? "next");
        }

        private static DataEdge? DecodeDataEdge(JObject item)
        {
            var from = DecodePortRef(item["from"]);
            var to = DecodePortRef(item["to"]);
            return from is null || to is null ? null : new DataEdge(from, to);
        }

        private static InputBinding? DecodeInputBinding(JObject item)
        {
            var fromInput = StringOf(item["fromInput"]);
            var to = DecodePortRef(item["to"]);
            return fromInput is null || to is null ? null : new InputBinding(fromInput, to);
        }

        private static WorkflowOutput? DecodeOutput(JObject item)
        {
            var id = StringOf(item["id"]);
            var from = DecodePortRef(item["from"]);
            return id is null || from is null
                ? null
                : new WorkflowOutput(id, from, StringOf(item["description"]));
        }

        private static PortRef? DecodePortRef(JToken? token)
        {
            if (token is not JObject item)
            {
                return null;
            }
            var node = StringOf(item["node"]);
            var port = StringOf(item["port"]);
            return node is null || port is null ? null : new PortRef(node, port);
        }

        private static string? StringOf(JToken? token) =>
            token is { Type: JTokenType.String }
                ? (string?)((JValue)token).Value
                : null;
    }
}
