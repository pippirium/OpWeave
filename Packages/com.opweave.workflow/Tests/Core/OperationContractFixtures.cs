#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] fixtures for the SPEC §C2/§0.6 SG-06 operation contract
    /// (T-W1.5-03): Unity-independent dummy Operations — an Immediate, a
    /// Waitable and a Reconcilable — plus minimal in-memory implementations
    /// of the abstract <see cref="StaticContext"/>/<see cref="StepContext"/>
    /// surface, <see cref="IChangeRecorder"/> and <see cref="IRunLogSink"/>.
    /// Compiling and exercising them proves the contract needs no Unity
    /// reference and serves external Operations as written.
    /// </summary>
    internal static class OperationContractFixtures
    {
        public static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok)
            {
                throw new ArgumentException(
                    "Fixture JSON did not parse: " + text, nameof(text));
            }
            return parsed.Value!;
        }

        /// <summary>
        /// The minimal complete-WF <see cref="OperationExample"/> a compiled
        /// dummy publishes so the live Registry satisfies SPEC §C1 item 4
        /// without any test-support exemption: a <c>core.start</c> node, the
        /// dummy's own node carrying the constant parameters its required
        /// inputs need, the single control edge, and — when the dummy
        /// publishes an output — a workflow output bound to it.
        /// </summary>
        internal static OperationExample ExampleFor(
            string operationId,
            int version,
            string? parametersJson = null,
            string? outputPort = null)
        {
            var versionText = version.ToString(CultureInfo.InvariantCulture);
            var parsed = JsonValue.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"example." +
                operationId + "-v" + versionText +
                "\",\"title\":\"Run " + operationId + " version " + versionText +
                "\",\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"op\",\"operation\":\"" + operationId +
                "\",\"version\":" + versionText +
                (parametersJson is null ? string.Empty : ",\"parameters\":" + parametersJson) +
                "}],\"controlEdges\":[{\"from\":\"start\",\"to\":\"op\"}]" +
                (outputPort is null ? string.Empty :
                    ",\"outputs\":[{\"id\":\"result\",\"from\":{\"node\":\"op\",\"port\":\"" +
                    outputPort + "\"}}]") +
                "}");
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new ArgumentException(
                    "Fixture example JSON did not parse for " + operationId + "@" + versionText,
                    nameof(parametersJson));
            }
            return new OperationExample(
                "Run " + operationId + " version " + versionText, parsed.Value);
        }

        /// <summary>
        /// An in-memory <see cref="StaticContext"/>: <see cref="InputValue"/>
        /// keeps missing distinct from explicit JSON <c>null</c>, and
        /// <see cref="Get{T}"/> never answers a missing port with
        /// <see langword="null"/> (SPEC §0.6 SG-06).
        /// </summary>
        internal sealed class Static : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            public Static(
                string nodeId,
                IReadOnlyDictionary<string, InputValue>? parameters = null)
            {
                NodeId = nodeId;
                _parameters = parameters ??
                    new Dictionary<string, InputValue>(StringComparer.Ordinal);
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters =>
                _parameters;

            public override T Get<T>(string port)
            {
                if (!_parameters.TryGetValue(port, out var input) || !input.IsPresent)
                {
                    throw new InvalidOperationException(
                        $"Input port '{port}' is missing.");
                }
                if (typeof(T) == typeof(JsonValue))
                {
                    return (T)(object)input.Value!;
                }
                return input.Value!.ToToken().ToObject<T>()!;
            }
        }

        /// <summary>
        /// An in-memory <see cref="StepContext"/> bound to a
        /// <see cref="Static"/> context: pending ports model the Dry-Run
        /// placeholder values of SPEC §D8 item 1.
        /// </summary>
        internal sealed class Step : StepContext
        {
            private readonly Static _static;
            private readonly HashSet<string> _pending;
            private readonly List<KeyValuePair<string, JsonValue>> _outputs =
                new List<KeyValuePair<string, JsonValue>>();

            public Step(
                Static staticContext,
                int stepIndex = 0,
                JsonValue? resumeToken = null,
                IEnumerable<string>? pendingPorts = null)
            {
                _static = staticContext;
                ResumeToken = resumeToken;
                _pending = pendingPorts is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(pendingPorts, StringComparer.Ordinal);
                Recorder = new Changes(stepIndex, staticContext.NodeId);
                Sink = new LogEntries(stepIndex);
            }

            /// <summary>The outputs written through <see cref="SetOutput{T}"/>, in order.</summary>
            public IReadOnlyList<KeyValuePair<string, JsonValue>> WrittenOutputs => _outputs;

            /// <summary>The recorder behind <see cref="Changes"/>.</summary>
            public Changes Recorder { get; }

            /// <summary>The sink behind <see cref="Log"/>.</summary>
            public LogEntries Sink { get; }

            public override IChangeRecorder Changes => Recorder;

            public override IRunLogSink Log => Sink;

            public override JsonValue? ResumeToken { get; }

            public override T Get<T>(string port)
            {
                if (_pending.Contains(port))
                {
                    throw new InvalidOperationException(
                        $"Input port '{port}' is pending and has no value yet.");
                }
                return _static.Get<T>(port);
            }

            public override void SetOutput<T>(string port, T value)
            {
                var json = value is JsonValue jsonValue
                    ? jsonValue
                    : JsonValue.From(JToken.FromObject(value!));
                _outputs.Add(new KeyValuePair<string, JsonValue>(port, json));
            }

            public override bool IsPresent(string port) =>
                _pending.Contains(port) ||
                (_static.Parameters.TryGetValue(port, out var input) && input.IsPresent);

            public override bool IsPending(string port) => _pending.Contains(port);
        }

        /// <summary>
        /// An in-memory <see cref="IChangeRecorder"/> stamping every call into
        /// a SPEC §F7 element shape.
        /// </summary>
        internal sealed class Changes : IChangeRecorder
        {
            private readonly int _step;
            private readonly string _nodeId;
            private readonly List<ChangeRecord> _records = new List<ChangeRecord>();

            public Changes(int step, string nodeId)
            {
                _step = step;
                _nodeId = nodeId;
            }

            public IReadOnlyList<ChangeRecord> Records => _records;

            public void Created(ChangeTarget target, bool undoable) =>
                Record(ChangeAction.Created, target, undoable);

            public void Modified(ChangeTarget target, bool undoable) =>
                Record(ChangeAction.Modified, target, undoable);

            public void Deleted(ChangeTarget target, bool undoable) =>
                Record(ChangeAction.Deleted, target, undoable);

            public void Moved(ChangeTarget target, bool undoable) =>
                Record(ChangeAction.Moved, target, undoable);

            public void Saved(ChangeTarget target, bool undoable) =>
                Record(ChangeAction.Saved, target, undoable);

            private void Record(ChangeAction action, ChangeTarget target, bool undoable) =>
                _records.Add(new ChangeRecord(_step, 1, _nodeId, action, target, undoable));
        }

        /// <summary>
        /// An in-memory <see cref="IRunLogSink"/> stamping every
        /// <see cref="Write"/> into a SPEC §F6 <see cref="RunLogEntry"/>.
        /// </summary>
        internal sealed class LogEntries : IRunLogSink
        {
            private readonly int? _step;
            private readonly List<RunLogEntry> _entries = new List<RunLogEntry>();

            public LogEntries(int? step)
            {
                _step = step;
            }

            public IReadOnlyList<RunLogEntry> Entries => _entries;

            public void Write(
                Severity level, string message, string? code = null, JsonValue? data = null) =>
                _entries.Add(new RunLogEntry(
                    UtcTimestamp.Now(), level, _step, code, message, data));
        }

        /// <summary>
        /// An Immediate dummy: echoes its <c>name</c> input to <c>echo</c>
        /// inside <see cref="IOperation.Execute"/> (SPEC §C1 Immediate).
        /// </summary>
        [OpWeaveOperation("test.dummy.immediate", 1)]
        internal sealed class ImmediateOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.immediate", 1)
                .Summary("Echo the name input.")
                .Input("name", "core.String", required: true)
                .Input("optional", "core.String", required: false)
                .Output("echo", "core.String")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ExampleFor(
                            "test.dummy.immediate", 1, "{\"name\":\"Probe\"}", "echo"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    new[]
                    {
                        new PlannedChange(
                            "fixtureNode", "create", "fixtureTarget", EffectNames.Read),
                    },
                    Json("{\"namePresent\":true}"));

            public StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("echo", "echo:" + ctx.Get<string>("name"));
                ctx.Log.Write(Severity.Info, "Echoed the name input.");
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A Waitable dummy (SPEC §C1 Waitable): <see cref="IOperation.Execute"/>
        /// suspends with a resume token and <see cref="IOperation.Poll"/>
        /// finishes — the token alone carries the waiting state, so a fresh
        /// instance resumes after a domain reload (SPEC §C2 items 3–4).
        /// </summary>
        [OpWeaveOperation("test.dummy.waitable", 1)]
        internal sealed class WaitableOperation : IOperation
        {
            public const string StageToken = "\"stage-1\"";

            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.waitable", 1)
                .Summary("Waits one poll, then completes.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build()
                with
                {
                    ExecutionMode = ExecutionMode.Waitable,
                    Examples = new[]
                    {
                        ExampleFor("test.dummy.waitable", 1, outputPort: "done"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(Json(StageToken), "fixture_wait", 50);

            public StepResult Poll(StepContext ctx)
            {
                var token = ctx.ResumeToken;
                if (token is null || token.CanonicalText != StageToken)
                {
                    throw new InvalidOperationException(
                        "Resume token missing or unexpected.");
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A Reconcilable dummy (SPEC §F5): decides solely from the persisted
        /// Preflight snapshot — <c>{"applied":true}</c> restores outputs as
        /// <see cref="ReconcileResult.Completed"/>,
        /// <c>{"applied":false}</c> is <see cref="ReconcileResult.NotApplied"/>,
        /// anything else stays <see cref="ReconcileResult.Unknown"/>.
        /// </summary>
        [OpWeaveOperation("test.dummy.reconcilable", 1)]
        internal sealed class ReconcilableOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.reconcilable", 1)
                .Summary("Creates a marker and can reconcile it.")
                .Output("marker", "core.String")
                .Effects(EffectNames.AssetCreate)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ExampleFor("test.dummy.reconcilable", 1, outputPort: "marker"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    new[]
                    {
                        new PlannedChange(
                            "fixtureNode", "create", "Assets/marker.txt",
                            EffectNames.AssetCreate),
                    },
                    Json("{\"applied\":false}"));

            public StepResult Execute(StepContext ctx)
            {
                ctx.Changes.Created(
                    new ChangeTarget("asset", "Assets/marker.txt", Guid: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                    undoable: true);
                ctx.SetOutput("marker", "created");
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var applied = snapshot.ToToken()["applied"];
                if (applied is null || applied.Type != JTokenType.Boolean)
                {
                    return new ReconcileResult.Unknown(
                        "The snapshot carries no applied flag.");
                }
                if (!applied.ToObject<bool>())
                {
                    return new ReconcileResult.NotApplied();
                }
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        ["marker"] = Json("\"created\""),
                    });
            }

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A dummy <see cref="IEffectResolver"/>: the actual effect follows the
        /// static shape of the inputs — an <c>assetTarget</c> input means
        /// <c>asset.modify</c>, otherwise <c>scene.modify</c> (SPEC §C4).
        /// </summary>
        internal sealed class EffectResolver : IEffectResolver
        {
            public IReadOnlyList<string> ResolveEffects(StaticContext context)
            {
                if (context.Parameters.TryGetValue("assetTarget", out var input) &&
                    input.IsPresent)
                {
                    return new[] { EffectNames.AssetModify };
                }
                return new[] { EffectNames.SceneModify };
            }
        }

        /// <summary>
        /// A base implementing every <see cref="IOperation"/> member
        /// trivially, so eligibility fixtures only vary the single class
        /// property under test. Deliberately unmarked and abstract.
        /// </summary>
        internal abstract class TrivialOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.trivial", 1)
                .Summary("A trivial fixture operation.")
                .Effects(EffectNames.Read)
                .Build() with
                {
                    Examples = new[]
                    {
                        ExampleFor("test.dummy.trivial", 1),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) => StepResult.Succeeded();

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.NotApplied();

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
