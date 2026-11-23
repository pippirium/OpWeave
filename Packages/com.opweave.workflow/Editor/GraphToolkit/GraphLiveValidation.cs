#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// A diagnostic the live validation maps back onto the graph (SPEC §H8
    /// item 1): <see cref="Severity"/> picks the GraphLogger error/warning
    /// channel and <see cref="NodeId"/> picks the node the badge lands on —
    /// a diagnostic without a nodeId lands on the graph itself.
    /// </summary>
    internal sealed class GraphNodeDiagnostic
    {
        internal GraphNodeDiagnostic(
            Severity severity, string code, string message, string? nodeId)
        {
            Severity = severity;
            Code = code;
            Message = message;
            NodeId = nodeId;
        }

        internal Severity Severity { get; }

        /// <summary>The SPEC §J code (e.g. <c>OW-TYPE-002</c>); empty for plain-text entries.</summary>
        internal string Code { get; }

        internal string Message { get; }

        internal string? NodeId { get; }
    }

    /// <summary>
    /// The live static validation of SPEC §H8 item 1.
    /// <see cref="OpWeaveGraph.OnGraphChanged"/> forwards every graph change
    /// here; once the graph has been quiet for 300 ms the graph is exported
    /// through the §H7 converter, run through the §D static validation
    /// pipeline, and each diagnostic is mapped back by <c>nodeId</c> to the
    /// responsible node via GraphLogger errors/warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Graph Toolkit materializes node badges only from the
    /// <c>errorsAndWarnings</c> a single <see cref="Graph.OnGraphChanged"/>
    /// call leaves in its logger — a logger captured at callback time is
    /// discarded once the callback returns, so the debounced pass cannot
    /// log into it. Instead the scheduler re-triggers a processing pass
    /// after the quiet window (<see cref="DefaultRequestProcessing"/>), and
    /// the resulting <see cref="OnGraphChanged"/> invocation — identifiable
    /// through <see cref="OpWeaveGraph.validationPokePending"/> — validates
    /// and logs synchronously.
    /// </para>
    /// <para>
    /// Every real change also re-emits the last computed diagnostics: each
    /// processing pass replaces the whole badge set, so staying silent
    /// would clear the badges for the duration of the quiet window. Fixing
    /// the problem clears the badge because the deferred pass emits the
    /// fresh (now empty) set.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The outcome of <see cref="GraphLiveValidation.Compile"/>: the
    /// per-node diagnostics plus the pipeline material an action consumes
    /// — the exported document, the snapshot the compile ran against and
    /// the §D report itself. <see cref="EnvironmentUnavailable"/> and
    /// <see cref="RegistryUnavailable"/> mark the two "keep last verdicts"
    /// situations where no fresh compile ran at all.
    /// </summary>
    internal sealed class CompileOutcome
    {
        /// <summary>The flattened per-node diagnostics of the attempt.</summary>
        internal readonly List<GraphNodeDiagnostic> Diagnostics =
            new List<GraphNodeDiagnostic>();

        /// <summary>The §H7 export the compile validated; null when the export itself failed.</summary>
        internal WorkflowDocument? Document;

        /// <summary>The §C3 snapshot the compile ran against; null when none is published.</summary>
        internal RegistrySnapshot? Snapshot;

        /// <summary>The §D report; null when the compile never reached the compiler.</summary>
        internal ValidationReport? Report;

        /// <summary>No runner host/policy environment exists in this domain.</summary>
        internal bool EnvironmentUnavailable;

        /// <summary>The §C3 publication has not produced a snapshot yet.</summary>
        internal bool RegistryUnavailable;
    }

    internal static class GraphLiveValidation
    {
        /// <summary>The 300 ms quiet window of SPEC §H8 item 1, in seconds.</summary>
        internal const double DebounceSeconds = 0.3;

        // ── Test seams (SPEC §H8 test) ──────────────────────────────────
        // Production binds the editor clock, an update-pump deferral and an
        // empty-undo-record processing trigger; the EditMode suite binds a
        // manual clock, a captured action and a direct callback so the
        // debounce runs deterministically without a graph window.
        internal static Func<double> Clock =
            () => EditorApplication.timeSinceStartup;

        /// <summary>Queues <paramref name="action"/> to run once <see cref="Clock"/> reaches <paramref name="dueAt"/>.</summary>
        internal static Action<double, Action> Schedule = DefaultSchedule;

        /// <summary>Re-triggers a graph processing pass so OnGraphChanged is invoked with a live logger.</summary>
        internal static Action<OpWeaveGraph> RequestProcessing =
            DefaultRequestProcessing;

        /// <summary>The settings-derived policy environment; null when the runner host is not installed in this domain.</summary>
        internal static Func<Result<RunPolicyEnvironment>?> PolicyEnvironment =
            DefaultPolicyEnvironment;

        private static readonly IReadOnlyDictionary<string, TypeRef> s_noPortTypes =
            new Dictionary<string, TypeRef>(StringComparer.Ordinal);

        // ── OnGraphChanged ──────────────────────────────────────────────

        /// <summary>
        /// The <see cref="Graph.OnGraphChanged"/> body: a real change arms
        /// the 300 ms quiet window and re-emits the last known diagnostics;
        /// the deferred pass validates the graph and emits the refreshed
        /// set.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static void OnGraphChanged(OpWeaveGraph graph, GraphLogger? graphLogger)
        {
            // Keep a handle on the live logger the processing pass handed
            // in — the last windowless-observable point of the toolkit's
            // own diagnostic channel (its errorsAndWarnings feed is what
            // the view turns into node badges).
            graph.lastProcessingLogger = graphLogger;
            OnGraphChanged(
                graph, graphLogger is null ? null : new LoggerSink(graphLogger));
        }

        internal static void OnGraphChanged(OpWeaveGraph graph, IErrorsAndWarnings? sink)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            try
            {
                if (graph.validationPokePending)
                {
                    // The deferred pass: validate the graph as it stands and
                    // replace the badge set with the fresh diagnostics —
                    // an empty set is what clears a fixed problem. A pass
                    // the §H8 item 2 publish path triggered emits the
                    // published set as-is instead of recomputing.
                    graph.validationPokePending = false;
                    graph.validationDueAt = -1;
                    var publishOnly = graph.publishOnly;
                    graph.publishOnly = false;
                    if (!publishOnly)
                    {
                        graph.latestDiagnostics = Compute(graph);
                    }
                    Emit(sink, graph, graph.latestDiagnostics);
                    DrainDisplayInfos(graph, sink);
                    return;
                }
                // A real change invalidates any pending publish-only set:
                // the debounced pass must revalidate normally.
                graph.publishOnly = false;
                graph.validationDueAt = Clock() + DebounceSeconds;
                // Each processing pass replaces the whole badge set, so the
                // still-unproven previous verdicts are re-emitted rather
                // than clearing the nodes for the quiet window.
                Emit(sink, graph, graph.latestDiagnostics);
                DrainDisplayInfos(graph, sink);
                if (!graph.validationPokeScheduled)
                {
                    graph.validationPokeScheduled = true;
                    ArmPoke(graph);
                }
            }
            catch (Exception exception)
            {
                // A live-validation failure must never break the graph's own
                // processing pass.
                Debug.LogException(exception);
            }
        }

        /// <summary>
        /// Exports <paramref name="graph"/> through the §H7 converter and
        /// runs the §D static validation over the resulting document,
        /// returning every diagnostic flattened into per-node form.
        /// </summary>
        internal static IReadOnlyList<GraphNodeDiagnostic> Compute(OpWeaveGraph graph)
        {
            var outcome = Compile(graph, ValidationMode.Static, ActorKind.Human);
            if (outcome.EnvironmentUnavailable || outcome.RegistryUnavailable)
            {
                // Runner host not installed in this domain, or no §C3
                // snapshot published yet — keep the last verdicts rather
                // than misreporting.
                return graph.latestDiagnostics;
            }
            return outcome.Diagnostics;
        }

        /// <summary>
        /// The shared export → policy-environment → §D compile pipeline
        /// the deferred validation pass and the SPEC §H8 item 2 actions
        /// both run: <paramref name="mode"/> picks the validation stage —
        /// <see cref="ValidationMode.Static"/> for Validate and the Run
        /// gate, <see cref="ValidationMode.DryRun"/> for the Dry Run
        /// action — and <paramref name="actor"/> the C4/D6 policy actor.
        /// The outcome keeps the document, snapshot and report so an
        /// action can consume the <see cref="ValidationReport.Plan"/> the
        /// flattened diagnostics alone cannot hand back.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static CompileOutcome Compile(
            OpWeaveGraph graph, ValidationMode mode, ActorKind actor)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var outcome = new CompileOutcome();
            var exported = GraphJsonConverter.Export(graph, BaseDocument());
            Append(outcome.Diagnostics, exported.Diagnostics);
            if (!exported.Ok)
            {
                return outcome;
            }
            var environment = PolicyEnvironment();
            if (!environment.HasValue)
            {
                outcome.EnvironmentUnavailable = true;
                return outcome;
            }
            var envResult = environment.Value;
            Append(outcome.Diagnostics, envResult.Diagnostics);
            if (!envResult.Ok)
            {
                return outcome;
            }
            var snapshot = GraphToolkitRegistry.SnapshotSource();
            if (snapshot is null)
            {
                outcome.RegistryUnavailable = true;
                return outcome;
            }
            var bound = envResult.Value!;
            var document = exported.Value!;
            outcome.Document = document;
            outcome.Snapshot = snapshot;
            var declared = DeclaredInputTypes(document, snapshot);
            var refs = new RefResolver();
            var compiler = UnityWorkflowCompiler.Create(
                snapshot,
                (nodeId, parameters) => new UnityStaticContext(
                    nodeId,
                    parameters,
                    declared.TryGetValue(nodeId, out var types)
                        ? types
                        : s_noPortTypes,
                    snapshot.Types,
                    refs),
                new UnityTypeProbe(),
                UnityPlanProvenance.Capture(bound.IntegrityLevel, TypeProviders()),
                policyFor: bound.PolicyFor,
                allowedPaths: bound.AllowedPaths);
            outcome.Report = compiler.Validate(
                new ValidationRequest(document, null, mode, actor));
            Append(outcome.Diagnostics, outcome.Report.Errors);
            Append(outcome.Diagnostics, outcome.Report.Warnings);
            return outcome;
        }

        /// <summary>
        /// Publishes a diagnostic set an action computed outside the
        /// debounced validation pass — the §H8 item 2 actions' Validate,
        /// Dry Run and Run-refusal diagnostics — through the same emit
        /// channel the deferred pass uses: the set replaces
        /// <see cref="OpWeaveGraph.latestDiagnostics"/>, a re-triggered
        /// processing pass emits it into the live logger a view
        /// materializes node badges from, and the validation log records
        /// the windowless delivery when no view consumed the poke.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="graph"/> or <paramref name="diagnostics"/> is null.
        /// </exception>
        internal static void Publish(
            OpWeaveGraph graph, IReadOnlyList<GraphNodeDiagnostic> diagnostics)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (diagnostics is null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }
            graph.latestDiagnostics = diagnostics;
            graph.validationDueAt = -1;
            // The triggered pass must emit this set as-is rather than
            // recomputing it; a real change clears the flag so the
            // debounced pass revalidates normally.
            graph.publishOnly = true;
            graph.validationPokePending = true;
            try
            {
                RequestProcessing(graph);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        // ── internals ───────────────────────────────────────────────────

        private static void ArmPoke(OpWeaveGraph graph) =>
            Schedule(graph.validationDueAt, () =>
            {
                graph.validationPokeScheduled = false;
                if (!graph.validationLive)
                {
                    return;
                }
                if (Clock() < graph.validationDueAt)
                {
                    // A newer change pushed the quiet window out — re-arm.
                    graph.validationPokeScheduled = true;
                    ArmPoke(graph);
                    return;
                }
                graph.validationPokePending = true;
                try
                {
                    RequestProcessing(graph);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            });

        // Errors and warnings are materialized only from the result a
        // processing pass returns — a GraphLogger captured at callback time
        // is discarded once the callback returns, so the deferred pass
        // re-triggers processing with an empty undo record (the only public
        // graph state update) and logs inside the resulting OnGraphChanged.
        private static void DefaultRequestProcessing(OpWeaveGraph graph)
        {
            graph.UndoBeginRecordGraph("OpWeave live validation");
            graph.UndoEndRecordGraph();
            // The toolkit re-invokes OnGraphChanged only when a
            // GraphViewEditorWindow has this graph open: UndoEndRecordGraph
            // marks the window's changeset, and is a no-op in windowless
            // (headless/EditMode) domains. When the poke is still pending
            // no view will process it, so the deferred pass issues the
            // identical call the window would — the toolkit's internal
            // GraphProcessingHelper.ProcessGraph, reached by reflection
            // because the internals are not public API. The pass builds
            // the live GraphLogger, invokes the production
            // OnGraphChanged(GraphLogger) callback (which consumes the
            // pending poke and emits through the real LoggerSink), and
            // the diagnostics land in the logger's errorsAndWarnings —
            // the same state a view materializes node badges from.
            // If the internals cannot be reached (toolkit API change),
            // the pass degrades to the graph's own validation log: Emit
            // still runs its real sink delivery — severity channel,
            // formatted message, resolved node context — but badge
            // materialization then remains genuinely view-bound.
            if (!graph.validationPokePending)
            {
                return;
            }
            if (ToolkitPass.TryRun(graph) && !graph.validationPokePending)
            {
                return;
            }
            graph.validationLog.Clear();
            OnGraphChanged(graph, graph.validationLog);
        }

        // The windowless processing pass: the exact members a
        // GraphViewEditorWindow drives when it revalidates a graph —
        // ProcessGraph(graphModel, changeset, options). Resolution is
        // one-shot; TryRun returns false (and the caller degrades to
        // the validation log) whenever the internals are not where the
        // current toolkit keeps them.
        private static class ToolkitPass
        {
            private static readonly MethodInfo? s_processGraph;
            private static readonly Type? s_changeset;
            private static readonly object? s_options;
            private static FieldInfo? s_implementation;

            static ToolkitPass()
            {
                var assembly = typeof(Graph).Assembly;
                s_processGraph = assembly
                    .GetType("Unity.GraphToolkit.Editor.GraphProcessingHelper")
                    ?.GetMethods(
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "ProcessGraph");
                s_changeset = assembly.GetType(
                    "Unity.GraphToolkit.Editor.GraphModelStateComponent+Changeset");
                var optionsType = assembly.GetType(
                    "Unity.GraphToolkit.Editor.RequestGraphProcessingOptions");
                s_options =
                    optionsType
                        ?.GetProperty(
                            "Default",
                            BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.NonPublic)
                        ?.GetValue(null)
                    ?? (optionsType is null
                        ? null
                        : Activator.CreateInstance(optionsType, true));
            }

            internal static bool TryRun(OpWeaveGraph graph)
            {
                if (s_processGraph is null || s_changeset is null || s_options is null)
                {
                    return false;
                }
                s_implementation ??= FindImplementation(graph.GetType());
                var model = s_implementation?.GetValue(graph);
                if (model is null)
                {
                    return false;
                }
                try
                {
                    s_processGraph.Invoke(
                        null,
                        new[]
                        {
                            model,
                            Activator.CreateInstance(s_changeset, true),
                            s_options,
                        });
                    return true;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    return false;
                }
            }

            // The graph model lives in the toolkit base types'
            // m_Implementation field — walk the hierarchy for it.
            private static FieldInfo? FindImplementation(Type type)
            {
                for (var t = type; t is not null; t = t.BaseType)
                {
                    var field = t.GetField(
                        "m_Implementation",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field is not null)
                    {
                        return field;
                    }
                }
                return null;
            }
        }

        private static Result<RunPolicyEnvironment>? DefaultPolicyEnvironment() =>
            OpWeaveRunnerHost.Current?.CurrentRunPolicyEnvironment();

        // The SG-17 merge base for a graph with no synced JSON: the export
        // only reads its non-GUI-owned members (schemaVersion, id and other
        // metadata), so a minimal document is enough — nodes, edges, inputs
        // and parameters all come from the graph itself.
        private static WorkflowDocument BaseDocument()
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "opweave.live",
                Nodes = new List<NodeData>(),
                ControlEdges = new List<ControlEdge>(),
            });
            return created.Value!;
        }

        private static void Append(
            List<GraphNodeDiagnostic> into, IEnumerable<Diagnostic> diagnostics)
        {
            foreach (var d in diagnostics)
            {
                into.Add(new GraphNodeDiagnostic(d.Severity, d.Code, d.Message, d.NodeId));
            }
        }

        private static void Emit(
            IErrorsAndWarnings? sink,
            OpWeaveGraph graph,
            IReadOnlyList<GraphNodeDiagnostic> diagnostics)
        {
            // Mirror what the pass's sink actually received: badges
            // materialize only from a live logger, so a null sink means no
            // badge update happened at all — the mirror stays null then.
            graph.lastEmission = sink is null ? null : diagnostics;
            graph.lastEmissionUsedLoggerSink = sink is LoggerSink;
            if (sink is null)
            {
                return;
            }
            var nodes = graph.GetNodes();
            foreach (var diagnostic in diagnostics)
            {
                var context = NodeFor(nodes, diagnostic.NodeId);
                var message = diagnostic.Code.Length == 0
                    ? diagnostic.Message
                    : diagnostic.Code + ": " + diagnostic.Message;
                switch (diagnostic.Severity)
                {
                    case Severity.Error:
                        sink.LogError(message, context);
                        break;
                    case Severity.Warning:
                        sink.LogWarning(message, context);
                        break;
                    default:
                        sink.Log(message, context);
                        break;
                }
            }
        }

        // The SPEC §H8 item 3 GraphLogger-info share of the execution
        // display: step-state messages the display queued for the next
        // pass — a step event whose nodeId no graph node carries cannot
        // be coloured, so it surfaces as an info line — each drained
        // once into the pass's sink with the resolved node context.
        private static void DrainDisplayInfos(OpWeaveGraph graph, IErrorsAndWarnings? sink)
        {
            if (sink is null || graph.pendingDisplayInfos.Count == 0)
            {
                return;
            }
            var nodes = graph.GetNodes();
            foreach (var (message, nodeId) in graph.pendingDisplayInfos)
            {
                sink.Log(message, NodeFor(nodes, nodeId));
            }
            graph.pendingDisplayInfos.Clear();
        }

        private static INode? NodeFor(IEnumerable<INode> nodes, string? nodeId)
        {
            if (nodeId is null)
            {
                return null;
            }
            foreach (var node in nodes)
            {
                if (string.Equals(
                    GraphJsonConverter.NodeIdOf(node), nodeId, StringComparison.Ordinal))
                {
                    return node;
                }
            }
            return null;
        }

        // The per-node declared input-port Type IDs the UnityStaticContext
        // factory closes over — the same map the Agent backend builds, so a
        // live validation converts parameters identically to a run.
        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypeRef>>
            DeclaredInputTypes(WorkflowDocument document, RegistrySnapshot snapshot)
        {
            var declared =
                new Dictionary<string, IReadOnlyDictionary<string, TypeRef>>(
                    StringComparer.Ordinal);
            var nodes = document.Nodes;
            for (var i = 0; i < nodes.Count; i++)
            {
                var types = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
                var registered = snapshot.Find(nodes[i].Operation, nodes[i].Version);
                if (registered is not null)
                {
                    var inputs = registered.Descriptor.Inputs;
                    for (var p = 0; p < inputs.Count; p++)
                    {
                        var parsed = TypeRef.Parse(inputs[p].Type);
                        if (parsed.Ok)
                        {
                            types[inputs[p].Name] = parsed.Value!;
                        }
                    }
                }
                declared[nodes[i].Id] = types;
            }
            return declared;
        }

        // The TypeProvider set of the last stage-4 build — the weak
        // fingerprint bindings the §D7 planHash material needs; the standard
        // pair is the same fallback the runner host's empty registry uses.
        private static IReadOnlyList<TypeProviderCandidate> TypeProviders() =>
            RegistryBuildHost.Latest?.Providers ?? StandardTypeProviders();

        private static IReadOnlyList<TypeProviderCandidate> StandardTypeProviders() =>
            new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(),
                    OperationOrigin.Standard),
                new TypeProviderCandidate(
                    UnityTypeProvider.ProviderId, new UnityTypeProvider(),
                    OperationOrigin.Standard),
            };

        // The deferral: a delayCall pump drains due actions on the editor
        // main thread — no background thread ever touches graph objects.
        private static readonly List<(double dueAt, Action action)> s_due = new();
        private static bool s_pumpArmed;

        private static void DefaultSchedule(double dueAt, Action action)
        {
            s_due.Add((dueAt, action));
            if (!s_pumpArmed)
            {
                s_pumpArmed = true;
                EditorApplication.delayCall += Pump;
            }
        }

        private static void Pump()
        {
            var now = Clock();
            for (var i = s_due.Count - 1; i >= 0; i--)
            {
                if (now < s_due[i].dueAt)
                {
                    continue;
                }
                var action = s_due[i].action;
                s_due.RemoveAt(i);
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
            s_pumpArmed = s_due.Count != 0;
            if (s_pumpArmed)
            {
                EditorApplication.delayCall += Pump;
            }
        }

        /// <summary>One entry a sink received: severity channel, formatted message and the resolved node context.</summary>
        internal sealed class ValidationLogEntry
        {
            internal ValidationLogEntry(string level, string message, object? context)
            {
                Level = level;
                Message = message;
                Context = context;
            }

            internal string Level { get; }

            internal string Message { get; }

            internal object? Context { get; }
        }

        /// <summary>
        /// The per-graph windowless delivery record: when no view consumed
        /// the deferred-pass poke, Emit logs into this recorder so the
        /// delivered (level, message, node) set stays observable without a
        /// GraphViewEditorWindow — the same contract the live
        /// <c>GraphLogger</c> satisfies when a pass provides one.
        /// </summary>
        internal sealed class ValidationLog : IErrorsAndWarnings
        {
            private readonly List<ValidationLogEntry> _entries = new();

            internal IReadOnlyList<ValidationLogEntry> Entries => _entries;

            internal void Clear() => _entries.Clear();

            public void LogError(object message, object context) =>
                _entries.Add(new ValidationLogEntry(
                    "error", message?.ToString() ?? "", context));

            public void LogWarning(object message, object context) =>
                _entries.Add(new ValidationLogEntry(
                    "warning", message?.ToString() ?? "", context));

            public void Log(object message, object context) =>
                _entries.Add(new ValidationLogEntry(
                    "info", message?.ToString() ?? "", context));
        }

        // GraphLogger's errorsAndWarnings sink is internal to the toolkit
        // module; this adapter exposes the public Log* surface through the
        // IErrorsAndWarnings shape the emit path — and the EditMode tests —
        // program against.
        private sealed class LoggerSink : IErrorsAndWarnings
        {
            private readonly GraphLogger _logger;

            internal LoggerSink(GraphLogger logger) => _logger = logger;

            public void LogError(object message, object context) =>
                _logger.LogError(message, context);

            public void LogWarning(object message, object context) =>
                _logger.LogWarning(message, context);

            public void Log(object message, object context) =>
                _logger.Log(message, context);
        }
    }
}
