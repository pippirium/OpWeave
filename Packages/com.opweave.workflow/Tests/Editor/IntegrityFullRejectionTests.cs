#nullable enable

// T-W2.9-02 [Edit] tests: local:T-W2.9-02:integrity-full-rejected — the
// AT-F02 full-rejection case of SPEC §0.1 F-05, §H4.1, §M.5 and §X.8.
// A settings file whose extensions.integrityLevel is "full" loads as a
// valid value (no OW-CFG-* error); the validate path of a valid workflow —
// Static and Dry Run alike — answers OW-OP-011 and builds no Plan; and the
// Run acceptance of a Plan compiled under weak refuses with OW-OP-011
// before any Run side effect, ahead of every later acceptance gate.
// Nothing downgrades to the weak fingerprint level, and the same workflow
// under integrityLevel:"weak" validates, is accepted and runs to Succeeded.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class IntegrityFullRejectionTests
    {
        private const string WorkflowId = "example.integrity-rejection";
        private const string MarkerOpId = "test.integrity.marker";

        private const string FullSettings =
            "{\"version\":2,\"extensions\":{\"integrityLevel\":\"full\"}}";
        private const string WeakSettings =
            "{\"version\":2,\"extensions\":{\"integrityLevel\":\"weak\"}}";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveIntegrityTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        // Drives the run one editor-update slice at a time, as the production
        // EditorApplication.update subscription does (the event itself is
        // never invoked from tests — the UTF callback would re-enter).
        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    return;
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // ── Workflow + compile fixtures ────────────────────────────────────

        // A valid workflow: start → one Immediate marker; it compiles clean
        // under weak so the OW-OP-011 of the full cases is the only error.
        private static WorkflowDocument Document =>
            RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData { Id = "mark", Operation = MarkerOpId, Version = 1 },
                },
                new[] { new ControlEdge("start", "mark") });

        private static void RegisterOps(RunnerHostFixture fixture)
        {
            fixture.Register(
                new RunnerHostFixture.MarkerOperation("core.start", fixture.Events), 'a');
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(MarkerOpId, fixture.Events), 'c');
        }

        // The standard provider set of the production stage (SPEC §0.6 SG-10):
        // the weak fingerprints the compiled Plan carries come from the real
        // T-W2.2-02 material over these — never a stub.
        private static TypeProviderCandidate[] StandardProviders() =>
            new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(),
                    OperationOrigin.Standard),
                new TypeProviderCandidate(
                    UnityTypeProvider.ProviderId, new UnityTypeProvider(),
                    OperationOrigin.Standard),
            };

        // The production compile the runner consumes (SPEC §D1 → §D7): the
        // snapshot-backed WorkflowCompiler over the fixture Registry with the
        // real UnityPlanProvenance carrying the level the settings supplied.
        private static ValidationReport Compile(
            RunnerHostFixture fixture, IntegrityLevel level, ValidationMode mode) =>
            new WorkflowCompiler(
                    fixture.Snapshot(),
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    fixture.Probe,
                    UnityPlanProvenance.Capture(level, StandardProviders()))
                .Validate(new ValidationRequest(Document, null, mode, ActorKind.Agent));

        private static CompiledPlan CompilePlan(RunnerHostFixture fixture)
        {
            var report = Compile(fixture, IntegrityLevel.Weak, ValidationMode.Static);
            Assert.IsTrue(report.Ok, "compile failed: " + Describe(report.Errors));
            Assert.IsNotNull(report.Plan, "an error-free compile must produce the Plan");
            return report.Plan!;
        }

        // A minimal StaticContext (Core hosts no implementation, SPEC §0.6
        // SG-06); the fixture Operations' ValidateStatic reports nothing.
        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            internal StubStaticContext(
                string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("The fixture Operations read no static port.");
        }

        // A gate double recording whether it ran — the refusal must stop the
        // chain before it (SG-18: the settings stage precedes the plugged-in
        // stale-Registry and Plan-permission positions).
        private sealed class ProbeGate : IRunAcceptanceGate
        {
            private readonly Func<Result<Unit>> _check;
            internal ProbeGate(Func<Result<Unit>> check) => _check = check;
            public Result<Unit> Admit(RunAcceptanceRequest request) => _check();
        }

        // ── SPEC §H4.1 loading table: "full" is a valid value ─────────────

        [Test]
        public void FullIntegrityLevel_LoadsAsValidValue()
        {
            var parsed = OpWeaveSettingsLoader.Parse(FullSettings);
            Assert.That(parsed.Ok, Is.True,
                "full must load as a valid value: " + Describe(parsed.Diagnostics));
            Assert.That(parsed.Diagnostics, Is.Empty,
                "a valid full load carries no settings diagnostics");
            Assert.That(parsed.Value!.IntegrityLevel, Is.EqualTo(IntegrityLevel.Full),
                "the configured level is carried verbatim — never downgraded to weak");
        }

        // ── SPEC §0.1 F-05 / §X.8: the validate paths reject full ─────────

        [Test]
        public void FullIntegrityLevel_StaticValidate_RejectsOwOp011WithoutPlan()
        {
            var fixture = NewFixture();
            RegisterOps(fixture);

            var report = Compile(fixture, IntegrityLevel.Full, ValidationMode.Static);

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null, "no Plan is built under full");
            Assert.That(report.Errors.Select(d => d.Code),
                Is.EqualTo(new[] { ErrorCodes.OW_OP_011 }),
                "the only error of a valid workflow under full is OW-OP-011");
        }

        [Test]
        public void FullIntegrityLevel_DryRunValidate_RejectsOwOp011WithoutPlan()
        {
            var fixture = NewFixture();
            RegisterOps(fixture);

            // The §D8 Dry Run path lands in the same Build Plan stage.
            var report = Compile(fixture, IntegrityLevel.Full, ValidationMode.DryRun);

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(report.Errors.Select(d => d.Code),
                Is.EqualTo(new[] { ErrorCodes.OW_OP_011 }));
        }

        // ── SPEC §H4.1: OW-OP-011 at Run acceptance ───────────────────────

        [Test]
        public void FullIntegrityLevel_AcceptRun_RefusedOwOp011BeforeSideEffects()
        {
            var fixture = NewFixture();
            RegisterOps(fixture);
            // The Plan exists: it was compiled while the level was weak — the
            // configured full of the acceptance still refuses it.
            var plan = CompilePlan(fixture);
            var gateHit = false;
            fixture.ExtraGates.Add(new ProbeGate(() =>
            {
                gateHit = true;
                return Result<Unit>.Success(Unit.Value);
            }));
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(FullSettings);

            var accepted = fixture.Host.AcceptRun(
                plan, Document, ActorKind.Agent, RunnerHostFixture.Options());

            Assert.IsFalse(accepted.Ok, "the Run must not be accepted under full");
            Assert.That(accepted.Diagnostics.Select(d => d.Code),
                Is.EqualTo(new[] { ErrorCodes.OW_OP_011 }),
                "the refusal is the single OW-OP-011 — no weak fallback");
            Assert.IsFalse(gateHit,
                "a later gate must not run after the integrity refusal");
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Runs")),
                "the refusal happened before any Run side effect");
        }

        // ── Weak control: the same workflow runs normally ────────────────

        [Test]
        public void WeakIntegrityLevel_ValidateAcceptAndRun_Succeeds()
        {
            var fixture = NewFixture();
            RegisterOps(fixture);
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(WeakSettings);
            var plan = CompilePlan(fixture);

            var accepted = fixture.Host.AcceptRun(
                plan, Document, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Assert.That(fixture.Events,
                Is.EqualTo(new[] { "execute:core.start", "execute:" + MarkerOpId }));
        }
    }
}
