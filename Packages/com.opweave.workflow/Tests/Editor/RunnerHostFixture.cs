#nullable enable

// T-W2.1-04 [Edit] test fixtures: a RunnerHost over a real FileRunStore on a
// probe root, fake editor state/clock and real G1 requirement evaluators
// behind the adapter seam. Shared by the runner-host and requirement-wiring
// verifications. Plans carry real planHash material so store.Load and the
// residue verdict work on them.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RunnerHostFixture : IDisposable
    {
        internal static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        internal sealed class FakeEditorState : IEditorState
        {
            public bool IsEditMode { get; set; } = true;
            public bool IsCompiling { get; set; }
            public bool IsUpdating { get; set; }
        }

        internal sealed class FakeClock : IClock
        {
            public DateTimeOffset UtcNow { get; set; } =
                new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
            public void Advance(TimeSpan delta) => UtcNow += delta;
        }

        internal sealed class FakeTypeProbe : IStaticTypeProbe
        {
            private readonly HashSet<string> _known =
                new HashSet<string>(StringComparer.Ordinal);
            public void Know(string name) => _known.Add(name);
            public bool TypeExists(string typeName) => _known.Contains(typeName);
        }

        internal readonly FakeEditorState Editor = new FakeEditorState();
        internal readonly FakeClock Clock = new FakeClock();
        internal readonly FakeTypeProbe Probe = new FakeTypeProbe();
        internal readonly List<string> AllowedPaths = new List<string> { "Assets/" };
        internal readonly Dictionary<string, string> GuidPaths =
            new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly HashSet<string> LoadedScenes =
            new HashSet<string>(StringComparer.Ordinal);
        internal readonly List<RegisteredOperation> Operations =
            new List<RegisteredOperation>();
        internal readonly List<IRunAcceptanceGate> ExtraGates =
            new List<IRunAcceptanceGate>();
        internal readonly List<string> Events = new List<string>();
        internal Func<Result<OpWeaveSettings>> LoadSettings =
            () => Result<OpWeaveSettings>.Success(OpWeaveSettings.Default);
        /// <summary>
        /// When set before <see cref="Host"/> is first built, the
        /// requirement seam binds the production evaluator — the live §G1
        /// probes (Editor state, package registry and file-system
        /// writability) instead of the fixture's injected stubs: the
        /// T-W2.7-05 Runner-level tests need the real on-disk destination
        /// reading so the (Operation, port) destination kind the
        /// RequirementEvaluatorAdapter passes through
        /// UnityStepContext.BoundOperationId decides the verdict — the
        /// stubbed probes answer every in-allowedPaths check Satisfied and
        /// could never see it. The settings-snapshot binding is unchanged.
        /// </summary>
        internal bool LiveRequirements;
        internal bool OpWeaveBusy;
        // The non-interactive-Editor flag the approval gate and service read —
        // the batchmode fixtures of T-W3.4-02 toggle it.
        internal bool IsBatchMode;
        // The deterministic Tick-budget timer a suite may inject — the
        // T-W3.5-01 Run Viewer fixture sets it so a slow Editor-side step
        // can no longer spend the real stopwatch's 50 ms budget and leave
        // a Plan step unstarted in the same Tick (the Issue #323 class of
        // timing defect). Null keeps the production StopwatchMonotonicTimer.
        internal Func<IMonotonicTimer>? MonotonicTimerFactory;
        internal readonly string Root;
        internal readonly string SessionId;

        /// <summary>
        /// The §E6 item 4 release dispatcher handed to the host — tests
        /// register kind handlers here; <see cref="Released"/> records what
        /// the handlers actually released.
        /// </summary>
        internal readonly RunResourceReleaseDispatcher ReleaseDispatcher =
            new RunResourceReleaseDispatcher();
        internal readonly List<RunResource> Released = new List<RunResource>();

        private OpWeaveRunnerHost? _host;
        private bool _installed;

        internal RunnerHostFixture(string root)
        {
            Root = root;
            SessionId = "session-" + Guid.NewGuid().ToString("N");
        }

        internal OpWeaveRunnerHost Host => _host ??= new OpWeaveRunnerHost(
            Root,
            SessionId,
            Clock,
            Editor,
            () => LoadSettings(),
            () => Snapshot(),
            host => new RequirementEvaluatorAdapter(LiveRequirements
                ? new EditorRequirementEvaluator(
                    () => host.ActiveRunAllowedPaths ?? AllowedPaths)
                : new EditorRequirementEvaluator(
                    Editor,
                    Probe,
                    () => host.ActiveRunAllowedPaths ?? AllowedPaths,
                    guid => GuidPaths.TryGetValue(guid, out var path) ? path : "",
                    path => LoadedScenes.Contains(path),
                    _ => false,
                    (_, _) => false)),
            EditorUndoScope.Open,
            () => OpWeaveBusy,
            () => IsBatchMode,
            50,
            ExtraGates,
            ReleaseDispatcher,
            // The production DebugLoggingEnabled reading (T-W2.4-04): the
            // SPEC §F6 SG-08 gate follows the settings load, so a fixture
            // that supplies debugLogging:true settings exercises the real
            // write path — and a failed load means the SPEC default false.
            debugLogging: () =>
                LoadSettings() is { Ok: true, Value: { } settings } &&
                settings.DebugLogging,
            monotonicTimerFactory: MonotonicTimerFactory);

        /// <summary>Subscribes the real Editor lifecycle events (reload tests).</summary>
        internal void Install()
        {
            Host.Install();
            _installed = true;
        }

        /// <summary>
        /// When set, replaces the synthetic snapshot
        /// <see cref="Snapshot"/> returns — the seam an acceptance test uses to
        /// run a Plan compiled against a real production Registry build
        /// (T-W2.11-01), whose per-step fingerprints the synthetic snapshot's
        /// seeded hashes could never match.
        /// </summary>
        internal RegistrySnapshot? SnapshotOverride { get; set; }

        internal RegistrySnapshot Snapshot() =>
            SnapshotOverride ??
            new RegistrySnapshot(
                "reg_01J00000000000000000000000",
                new string('f', 64),
                Operations,
                Types,
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());

        private static ITypeRegistry? s_types;

        // The standard providers of the production stage (SPEC §0.6 SG-10):
        // core.* plus the unity.* shapes and their CLR codecs the Unity
        // StepContext conversions need (T-W2.3-02).
        internal static ITypeRegistry Types =>
            s_types ??= TypeRegistryBuilder.Build(
                    new[]
                    {
                        new TypeProviderCandidate(
                            CoreTypeProvider.ProviderId,
                            new CoreTypeProvider(),
                            OperationOrigin.Standard),
                        new TypeProviderCandidate(
                            UnityTypeProvider.ProviderId,
                            new UnityTypeProvider(),
                            OperationOrigin.Standard),
                    })
                .Registry;

        /// <summary>
        /// Registers the op in the fixture snapshot with the
        /// <paramref name="hashSeed"/>-derived fingerprint pair the Plan steps
        /// carry, and keeps the live instance for the resolver.
        /// </summary>
        internal void Register(IOperation op, char hashSeed)
        {
            Operations.Add(new RegisteredOperation(
                op.Descriptor,
                OperationOrigin.Custom,
                "Example.Operations",
                null,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64))
            {
                Implementation = op,
            });
        }

        public void Dispose()
        {
            if (_installed)
            {
                _host?.Uninstall();
            }
            // Drops held OS exclusions so the probe root can be deleted.
            _host?.Dispose();
        }

        // ── Plan/document builders ───────────────────────────────────────────

        internal static PlanStep Step(
            int index,
            string nodeId,
            string operationId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int next = -1,
            Idempotency idempotency = Idempotency.Unsafe) =>
            new PlanStep(
                index,
                nodeId,
                operationId,
                1,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64),
                inputs ?? new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = next },
                Array.Empty<string>(),
                30,
                new RetryPolicy(),
                idempotency);

        internal static CompiledPlan Plan(string workflowId, IReadOnlyList<PlanStep> steps)
        {
            var hashInput = HashInput(workflowId, steps);
            return new CompiledPlan(
                "plan_" + Identifiers.NewUlid(),
                CanonicalJson.Sha256(hashInput.ToToken()),
                workflowId,
                new string('b', 64),
                new string('c', 64),
                Json("{}"),
                steps,
                Array.Empty<PlanOutput>(),
                Array.Empty<string>(),
                new List<ApprovalRequirement>(),
                Array.Empty<PlanSourceMapEntry>(),
                hashInput);
        }

        // The SG-12 twelve-member planHash input (SPEC §0.6 SG-12): the
        // step-derived members go through the shared PlanHashInputParts so the
        // persisted public surface and the hash input agree byte-exactly.
        private static JsonValue HashInput(string workflowId, IReadOnlyList<PlanStep> steps)
        {
            var hashInput = new JObject
            {
                ["canonicalJsonVersion"] = 1,
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = "6000.0.85f1",
                    ["editorOS"] = "windows",
                    ["activeBuildTarget"] = "windows",
                    ["scriptingBackend"] = "mono",
                    ["apiCompatibilityLevel"] = "netstandard2.1",
                    ["scriptingDefineSymbols"] = new JArray(),
                },
                ["executionInputs"] = PlanHashInputParts.ExecutionInputs(
                    Array.Empty<string>(), null),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(
                    steps, Array.Empty<ApprovalRequirement>()),
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = workflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["retry"] = PlanHashInputParts.Retry(steps),
                ["steps"] = PlanHashInputParts.Steps(steps),
                ["timeouts"] = PlanHashInputParts.Timeouts(steps),
                ["transitions"] = PlanHashInputParts.Transitions(steps),
                ["usedCodecFingerprints"] = new JArray(),
                ["usedOperationFingerprints"] =
                    PlanHashInputParts.OperationFingerprints(steps),
                ["usedTypeFingerprints"] = new JArray(),
            };
            return JsonValue.From(hashInput);
        }

        internal static WorkflowDocument Doc(
            string workflowId,
            IReadOnlyList<NodeData> nodes,
            IReadOnlyList<ControlEdge> edges)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = workflowId,
                Nodes = new List<NodeData>(nodes),
                ControlEdges = new List<ControlEdge>(edges),
            });
            Assert.IsTrue(created.Ok, "fixture workflow document must be valid");
            return created.Value!;
        }

        internal static AcceptOptions Options() => new AcceptOptions(null, false);

        internal static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"fixture JSON must parse: {text}");
            return parsed.Value!;
        }

        // ── Journal inspection ───────────────────────────────────────────────

        internal static string JournalPath(string root, string runId) =>
            Path.Combine(root, "Runs", runId, "journal.ndjson");

        // The store-observation read of EditorProcessHarness: a bare
        // File.ReadAllLines holds FileShare.Read, which denies the
        // FileMode.Append open a live owner's next journal write performs
        // — the store then correctly fails the Run OW-STORE-001 (the
        // T-W4.6-02 case-2 failure on PR #396). The shared read leaves
        // the appender free and drops an in-flight torn tail.
        internal static string[] JournalLines(string root, string runId) =>
            EditorProcessHarness.ReadAllLinesShared(JournalPath(root, runId));

        internal static bool JournalHas(string root, string runId, string fragment)
        {
            foreach (var line in JournalLines(root, runId))
            {
                if (line.Contains(fragment))
                {
                    return true;
                }
            }
            return false;
        }

        // Canonical JSON sorts the envelope members, so a step record's
        // "step" index and "type" wire name are not adjacent — matching them
        // as one substring never fires. JournalHasAll requires every
        // fragment on the SAME line, which pins the record's type and step
        // index without depending on member order.
        internal static bool JournalHasAll(string root, string runId, params string[] fragments)
        {
            foreach (var line in JournalLines(root, runId))
            {
                var all = true;
                foreach (var fragment in fragments)
                {
                    all &= line.Contains(fragment);
                }
                if (all)
                {
                    return true;
                }
            }
            return false;
        }

        internal static int JournalCount(string root, string runId, string fragment)
        {
            var count = 0;
            foreach (var line in JournalLines(root, runId))
            {
                if (line.Contains(fragment))
                {
                    count++;
                }
            }
            return count;
        }

        // ── Test operations ─────────────────────────────────────────────────

        /// <summary>An Immediate marker: records execute:&lt;id&gt; and sets its output.</summary>
        internal sealed class MarkerOperation : IOperation
        {
            private readonly List<string> _events;

            internal MarkerOperation(string id, List<string> events)
            {
                _events = events;
                Descriptor = Op.Define(id, 1)
                    .Summary("Marks execution order.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }
            internal int PreflightCalls { get; private set; }
            internal int ExecuteCalls { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                PreflightCalls++;
                return new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));
            }

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                _events.Add("execute:" + Descriptor.Id);
                ctx.SetOutput("mark", "done:" + Descriptor.Id);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A Waitable reporting a changing waitingFor label per call — drives
        /// the Buffered step_progress record of SPEC §F2.
        /// </summary>
        internal sealed class LabelWaitableOperation : IOperation
        {
            private int _calls;

            internal LabelWaitableOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Reports changing progress labels.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(Json("\"t0\""), "stage-0", 1);

            public StepResult Poll(StepContext ctx)
            {
                _calls++;
                return _calls < 2
                    ? StepResult.Waiting(Json("\"t" + _calls + "\""), "stage-" + _calls, 1)
                    : Complete(ctx);
            }

            private StepResult Complete(StepContext ctx)
            {
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
        /// An Immediate carrying one declared Requirement — the
        /// scene_loaded / not_compiling / path_writable wiring cases.
        /// Tracks whether Preflight/Execute ran (a violation must stop
        /// before them).
        /// </summary>
        internal sealed class GatedOperation : IOperation
        {
            private readonly List<string> _events;

            internal GatedOperation(
                string id, string requirementKind, string? requirementPort, List<string> events,
                string inputName = "scene", string inputType = "unity.SceneRef")
            {
                _events = events;
                var builder = Op.Define(id, 1)
                    .Summary("Gated by a runtime requirement.")
                    .Input(inputName, inputType, required: false)
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Requires(requirementKind, requirementPort);
                Descriptor = builder.Build();
            }

            public OperationDescriptor Descriptor { get; }
            internal int PreflightCalls { get; private set; }
            internal int ExecuteCalls { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                PreflightCalls++;
                return new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));
            }

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                _events.Add("execute:" + Descriptor.Id);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// An Immediate that registers one held object as a Run resource
        /// through the context's SPEC §E6 item 4 registry — the
        /// <c>resource_acquired</c>/<c>resource_released</c> journal pair and
        /// the Run-end release the host dispatcher performs.
        /// </summary>
        internal sealed class ResourceAcquiringOperation : IOperation
        {
            internal ResourceAcquiringOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Acquires a Run resource.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                ((IRunResourceRegistry)ctx).Acquire(
                    "editing_contents", Json("{\"guid\":\"abc\"}"));
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A Waitable whose Poll outcome the test controls — holds a Run
        /// mid-execution so the state a later step's requirement faces can
        /// be arranged deterministically. <see cref="Execute"/> and a
        /// non-released <see cref="Poll"/> report Waiting; a released Poll
        /// completes.
        /// </summary>
        internal sealed class ControlledWaitableOperation : IOperation
        {
            internal ControlledWaitableOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits until the test releases it.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }
            internal bool Release { get; set; }
            internal int ExecuteCalls { get; private set; }
            internal int PollCalls { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                return StepResult.Waiting(Json("\"held\""), "held", 1);
            }

            public StepResult Poll(StepContext ctx)
            {
                PollCalls++;
                return Release
                    ? StepResult.Succeeded()
                    : StepResult.Waiting(Json("\"held\""), "held", 1);
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
