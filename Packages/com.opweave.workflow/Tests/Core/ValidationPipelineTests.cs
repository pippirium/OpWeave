#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §D1 validation pipeline and the §D2 structural
    /// phase (local:T-W1.6-01:pipeline-structure): the explicit phase order
    /// D2 → D3 → D4 → D5 → D6 with D8 appended only for DryRun, the D2-error stop,
    /// per-node exclusion for later phases, the 100-diagnostic cap with
    /// <c>truncated</c>, node-document-order-then-code ordering, and the D2
    /// reference/duplicate/reserved-key diagnostics. Later phases are stubs, so
    /// behaviour is proven with injected spy phases through the internal pipeline
    /// seam; the same sources run under dotnet and Unity Mono.
    /// </summary>
    internal sealed class ValidationPipelineTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        private static NodeData Node(string id, string operation = "example.operation") =>
            new NodeData { Id = id, Operation = operation, Version = 1 };

        private static WorkflowDocumentData LinearData() => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.pipeline",
            Nodes = new List<NodeData>
            {
                Node("start", "core.start"),
                Node("a"),
                Node("b"),
            },
            ControlEdges = new List<ControlEdge>
            {
                new ControlEdge("start", "a"),
                new ControlEdge("a", "b"),
            },
        };

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static ValidationRequest RequestFor(
            WorkflowDocument document,
            ValidationMode mode = ValidationMode.Static,
            ActorKind actor = ActorKind.Agent) =>
            new ValidationRequest(document, null, mode, actor);

        private static Diagnostic Error(string code, string? nodeId = null, string? jsonPath = null) =>
            ErrorCodes.Create(code, "Spy failure.", nodeId: nodeId, jsonPath: jsonPath);

        private sealed class SpyPhase : IValidationPhase
        {
            private readonly System.Action<ValidationContext> _run;

            public SpyPhase(ValidationPhaseKind kind, System.Action<ValidationContext>? run = null)
            {
                Kind = kind;
                _run = run ?? (_ => { });
            }

            public ValidationPhaseKind Kind { get; }

            public void Run(ValidationContext context) => _run(context);
        }

        private static SpyPhase RecordingSpy(ValidationPhaseKind kind, List<string> calls) =>
            new SpyPhase(kind, _ => calls.Add(kind.ToString()));

        private static WorkflowCompiler SpyCompiler(
            List<string> calls, out SpyPhase dryRun)
        {
            dryRun = RecordingSpy(ValidationPhaseKind.D8, calls);
            return new WorkflowCompiler(
                new IValidationPhase[]
                {
                    RecordingSpy(ValidationPhaseKind.D2, calls),
                    RecordingSpy(ValidationPhaseKind.D3, calls),
                    RecordingSpy(ValidationPhaseKind.D4, calls),
                    RecordingSpy(ValidationPhaseKind.D5, calls),
                    RecordingSpy(ValidationPhaseKind.D6, calls),
                },
                dryRun);
        }

        // ── SPEC §D1 — pipeline shape ────────────────────────────────────────────

        [Test]
        public void CleanDocumentPassesDefaultPipeline()
        {
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(LinearData())));
            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
            Assert.IsEmpty(report.Warnings);
            Assert.IsFalse(report.Truncated);
            Assert.IsNull(report.Plan);
            Assert.IsNull(report.PlannedChanges);
        }

        [Test]
        public void PhasesRunInD2ToD6Order()
        {
            var calls = new List<string>();
            var compiler = SpyCompiler(calls, out _);
            compiler.Validate(RequestFor(MustCreate(LinearData())));
            Assert.AreEqual(
                new[] { "D2", "D3", "D4", "D5", "D6" },
                calls.ToArray());
        }

        [Test]
        public void D8RunsOnlyForDryRun()
        {
            var calls = new List<string>();
            var compiler = SpyCompiler(calls, out _);

            compiler.Validate(RequestFor(MustCreate(LinearData()), ValidationMode.Static));
            Assert.AreEqual(
                new[] { "D2", "D3", "D4", "D5", "D6" },
                calls.ToArray());

            calls.Clear();
            compiler.Validate(RequestFor(MustCreate(LinearData()), ValidationMode.DryRun));
            Assert.AreEqual(
                new[] { "D2", "D3", "D4", "D5", "D6", "D8" },
                calls.ToArray());
        }

        [Test]
        public void RequestFieldsReachThePhases()
        {
            var inputs = JsonValue.Parse("{\"scene\": 1}").Value!;
            ValidationContext? seen = null;
            var compiler = new WorkflowCompiler(new IValidationPhase[]
            {
                new SpyPhase(ValidationPhaseKind.D2, context => seen = context),
            });
            compiler.Validate(new ValidationRequest(
                MustCreate(LinearData()), inputs, ValidationMode.DryRun, ActorKind.Human));
            Assert.IsNotNull(seen);
            Assert.AreSame(inputs, seen!.Inputs);
            Assert.AreEqual(ValidationMode.DryRun, seen.Mode);
            Assert.AreEqual(ActorKind.Human, seen.Actor);
        }

        [Test]
        public void NullRequestAndNullDocumentAreRejected()
        {
            var compiler = new WorkflowCompiler();
            Assert.Throws<System.ArgumentNullException>(() => compiler.Validate(null!));
            Assert.Throws<System.ArgumentException>(() => compiler.Validate(
                new ValidationRequest(null!, null, ValidationMode.Static, ActorKind.Agent)));
        }

        // ── SPEC §D1 item 2 — D2 stop and per-node exclusion ────────────────────

        [Test]
        public void D2ErrorsStopLaterPhases()
        {
            var data = LinearData();
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("a", "out"), new PortRef("ghost", "in")),
            };
            var calls = new List<string>();
            var compiler = new WorkflowCompiler(
                new IValidationPhase[]
                {
                    new StructuralValidationPhase(),
                    RecordingSpy(ValidationPhaseKind.D3, calls),
                    RecordingSpy(ValidationPhaseKind.D4, calls),
                    RecordingSpy(ValidationPhaseKind.D5, calls),
                    RecordingSpy(ValidationPhaseKind.D6, calls),
                },
                RecordingSpy(ValidationPhaseKind.D8, calls));

            var report = compiler.Validate(RequestFor(
                MustCreate(data), ValidationMode.DryRun));

            Assert.IsEmpty(calls, "no later phase may run after a D2 error");
            Assert.IsFalse(report.Ok);
            Assert.AreEqual(1, report.Errors.Count);
            Assert.AreEqual(ErrorCodes.OW_VAL_011, report.Errors[0].Code);
        }

        [Test]
        public void D2WarningDoesNotStopLaterPhases()
        {
            var calls = new List<string>();
            var compiler = new WorkflowCompiler(new IValidationPhase[]
            {
                new SpyPhase(ValidationPhaseKind.D2, context => context.Report(
                    ErrorCodes.Create(ErrorCodes.OW_WF_006, "Spy warning."))),
                RecordingSpy(ValidationPhaseKind.D3, calls),
                RecordingSpy(ValidationPhaseKind.D4, calls),
            });

            var report = compiler.Validate(RequestFor(MustCreate(LinearData())));
            Assert.AreEqual(new[] { "D3", "D4" }, calls.ToArray());
            Assert.IsTrue(report.Ok, "warnings do not fail validation");
            Assert.AreEqual(1, report.Warnings.Count);
            Assert.IsEmpty(report.Errors);
        }

        [Test]
        public void FailingNodeIsExcludedWhileOthersStayChecked()
        {
            var calls = new List<string>();
            var observedAtD4 = new List<string>();
            var observedAtD6 = new List<string>();
            var compiler = new WorkflowCompiler(new IValidationPhase[]
            {
                new SpyPhase(ValidationPhaseKind.D2),
                new SpyPhase(ValidationPhaseKind.D3, context =>
                {
                    calls.Add("D3");
                    context.Report(Error(ErrorCodes.OW_OP_001, nodeId: "b"));
                }),
                new SpyPhase(ValidationPhaseKind.D4, context =>
                {
                    calls.Add("D4");
                    observedAtD4.AddRange(context.ActiveNodes.Select(n => n.Id));
                }),
                new SpyPhase(ValidationPhaseKind.D5, context => calls.Add("D5")),
                new SpyPhase(ValidationPhaseKind.D6, context =>
                {
                    calls.Add("D6");
                    observedAtD6.AddRange(context.ActiveNodes.Select(n => n.Id));
                }),
            });

            var report = compiler.Validate(RequestFor(MustCreate(LinearData())));

            Assert.AreEqual(new[] { "D3", "D4", "D5", "D6" }, calls.ToArray());
            Assert.AreEqual(new[] { "start", "a" }, observedAtD4.ToArray());
            Assert.AreEqual(new[] { "start", "a" }, observedAtD6.ToArray());
            Assert.IsFalse(report.Ok);
            Assert.AreEqual(1, report.Errors.Count);
            Assert.AreEqual("b", report.Errors[0].NodeId);
        }

        [Test]
        public void WarningAndUnknownNodeIdNeverExclude()
        {
            var observed = new List<string>();
            var compiler = new WorkflowCompiler(new IValidationPhase[]
            {
                new SpyPhase(ValidationPhaseKind.D2),
                new SpyPhase(ValidationPhaseKind.D3, context =>
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_013, "Spy warning.", nodeId: "a"));
                    context.Report(Error(ErrorCodes.OW_OP_001, nodeId: "ghost"));
                }),
                new SpyPhase(ValidationPhaseKind.D4, context =>
                    observed.AddRange(context.ActiveNodes.Select(n => n.Id))),
            });

            compiler.Validate(RequestFor(MustCreate(LinearData())));
            Assert.AreEqual(new[] { "start", "a", "b" }, observed.ToArray());
        }

        // SPEC §D1 test shape: one node's failure does not hide another node's
        // problem — the failing node is excluded, the rest keep being checked.
        [Test]
        public void DifferentNodeErrorsAccumulateAcrossPhases()
        {
            var compiler = new WorkflowCompiler(new IValidationPhase[]
            {
                new SpyPhase(ValidationPhaseKind.D2),
                new SpyPhase(ValidationPhaseKind.D3, context =>
                    context.Report(Error(ErrorCodes.OW_OP_001, nodeId: "a"))),
                new SpyPhase(ValidationPhaseKind.D4, context =>
                    context.Report(Error(ErrorCodes.OW_TYPE_002, nodeId: "b"))),
            });

            var report = compiler.Validate(RequestFor(MustCreate(LinearData())));
            Assert.IsFalse(report.Ok);
            Assert.AreEqual(
                new[] { ("a", ErrorCodes.OW_OP_001), ("b", ErrorCodes.OW_TYPE_002) },
                report.Errors.Select(d => (d.NodeId, d.Code)).ToArray());
        }

        // ── SPEC §D1 item 3 — ordering and the 100-diagnostic cap ───────────────

        [Test]
        public void DiagnosticsOrderByNodeDocumentOrderThenCode()
        {
            var compiler = new WorkflowCompiler(new IValidationPhase[]
            {
                new SpyPhase(ValidationPhaseKind.D2),
                new SpyPhase(ValidationPhaseKind.D3, context =>
                {
                    context.Report(Error(ErrorCodes.OW_OP_001, nodeId: "b"));
                    context.Report(Error(ErrorCodes.OW_TYPE_002, nodeId: "a"));
                    context.Report(Error(ErrorCodes.OW_OP_001, nodeId: "a"));
                }),
            });

            var report = compiler.Validate(RequestFor(MustCreate(LinearData())));
            Assert.AreEqual(
                new[]
                {
                    ("a", ErrorCodes.OW_OP_001),
                    ("a", ErrorCodes.OW_TYPE_002),
                    ("b", ErrorCodes.OW_OP_001),
                },
                report.Errors.Select(d => (d.NodeId, d.Code)).ToArray());
        }

        [Test]
        public void DiagnosticsWithoutANodeSortBeforeNodeBoundOnes()
        {
            var compiler = new WorkflowCompiler(new IValidationPhase[]
            {
                new SpyPhase(ValidationPhaseKind.D2),
                new SpyPhase(ValidationPhaseKind.D3, context =>
                {
                    context.Report(Error(ErrorCodes.OW_OP_001, nodeId: "a"));
                    context.Report(Error(ErrorCodes.OW_OP_002));
                }),
            });

            var report = compiler.Validate(RequestFor(MustCreate(LinearData())));
            Assert.AreEqual(
                new[] { ErrorCodes.OW_OP_002, ErrorCodes.OW_OP_001 },
                report.Errors.Select(d => d.Code).ToArray());
        }

        [Test]
        public void NodeIdIsFilledFromANodesJsonPath()
        {
            var compiler = new WorkflowCompiler(new IValidationPhase[]
            {
                new SpyPhase(ValidationPhaseKind.D2),
                new SpyPhase(ValidationPhaseKind.D3, context =>
                    context.Report(Error(ErrorCodes.OW_OP_001, jsonPath: "$.nodes[1].operation"))),
            });

            var report = compiler.Validate(RequestFor(MustCreate(LinearData())));
            Assert.AreEqual("a", report.Errors[0].NodeId);
        }

        private static WorkflowDocument DocumentWithDanglingEdges(int count)
        {
            var data = LinearData();
            var edges = new List<DataEdge>();
            for (var i = 0; i < count; i++)
            {
                edges.Add(new DataEdge(
                    new PortRef("a", "out"), new PortRef("ghost" + i, "in")));
            }
            data.DataEdges = edges;
            return MustCreate(data);
        }

        [Test]
        public void MoreThan100DiagnosticsSetsTruncated()
        {
            var report = new WorkflowCompiler().Validate(
                RequestFor(DocumentWithDanglingEdges(120)));
            Assert.IsFalse(report.Ok);
            Assert.IsTrue(report.Truncated);
            Assert.AreEqual(WorkflowCompiler.MaxDiagnostics, report.Errors.Count);
            Assert.AreEqual(100, WorkflowCompiler.MaxDiagnostics);
        }

        [Test]
        public void Exactly100DiagnosticsIsNotTruncated()
        {
            var report = new WorkflowCompiler().Validate(
                RequestFor(DocumentWithDanglingEdges(100)));
            Assert.IsFalse(report.Truncated);
            Assert.AreEqual(100, report.Errors.Count);
        }

        // ── SPEC §D2 — structural checks ─────────────────────────────────────────

        [Test]
        public void DanglingDataEdgeReportsItsJsonPath()
        {
            var data = LinearData();
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("a", "out"), new PortRef("ghost", "in")),
            };
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_011, diagnostic.Code);
            Assert.AreEqual("$.dataEdges[0].to.node", diagnostic.JsonPath);
        }

        [Test]
        public void ControlEdgeToMissingNode()
        {
            var data = LinearData();
            data.ControlEdges!.Add(new ControlEdge("b", "ghost"));
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            var diagnostic = report.Errors.Single(d => d.Code == ErrorCodes.OW_VAL_011);
            Assert.AreEqual("$.controlEdges[2].to", diagnostic.JsonPath);
            Assert.AreEqual("b", diagnostic.NodeId);
        }

        [Test]
        public void ControlEdgeFromMissingNode()
        {
            var data = LinearData();
            data.ControlEdges!.Add(new ControlEdge("ghost", "a"));
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            var diagnostic = report.Errors.Single(d => d.Code == ErrorCodes.OW_VAL_011);
            Assert.AreEqual("$.controlEdges[2].from", diagnostic.JsonPath);
            Assert.AreEqual("a", diagnostic.NodeId);
        }

        [Test]
        public void DataEdgeFromMissingNode()
        {
            var data = LinearData();
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("ghost", "out"), new PortRef("b", "in")),
            };
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_011, diagnostic.Code);
            Assert.AreEqual("$.dataEdges[0].from.node", diagnostic.JsonPath);
            Assert.AreEqual("b", diagnostic.NodeId);
        }

        [Test]
        public void InputBindingToMissingInputAndNode()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("scene", "unity.SceneRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("ghostInput", new PortRef("a", "in")),
                new InputBinding("scene", new PortRef("ghost", "in")),
            };
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));

            var missingInput = report.Errors.Single(d => d.JsonPath == "$.inputBindings[0].fromInput");
            Assert.AreEqual(ErrorCodes.OW_VAL_011, missingInput.Code);
            Assert.AreEqual("a", missingInput.NodeId);

            var missingNode = report.Errors.Single(d => d.JsonPath == "$.inputBindings[1].to.node");
            Assert.AreEqual(ErrorCodes.OW_VAL_011, missingNode.Code);
            Assert.AreEqual("ghost", missingNode.NodeId);
        }

        [Test]
        public void OutputFromMissingNode()
        {
            var data = LinearData();
            data.Outputs = new List<WorkflowOutput>
            {
                new WorkflowOutput("result", new PortRef("ghost", "out")),
            };
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_011, diagnostic.Code);
            Assert.AreEqual("$.outputs[0].from.node", diagnostic.JsonPath);
        }

        [Test]
        public void DuplicateControlEdgeReportsOwWf004OnTheLaterEdge()
        {
            var data = LinearData();
            data.ControlEdges!.Insert(1, new ControlEdge("start", "a"));
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_WF_004, diagnostic.Code);
            Assert.AreEqual("$.controlEdges[1]", diagnostic.JsonPath);
            StringAssert.Contains("$.controlEdges[0]", diagnostic.Hint ?? string.Empty);
        }

        [Test]
        public void DistinctFromPortIsNotADuplicate()
        {
            var data = LinearData();
            data.ControlEdges!.Add(new ControlEdge("a", "b", "branch"));
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            Assert.IsTrue(report.Errors.All(d => d.Code != ErrorCodes.OW_WF_004));
        }

        [Test]
        public void MalformedAndDuplicateIdsSurfaceThroughThePipeline()
        {
            var data = LinearData();
            data.Nodes!.Add(Node("1bad"));
            data.Nodes!.Add(Node("a"));
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            var codes = report.Errors.Select(d => d.Code).Distinct()
                .OrderBy(c => c, System.StringComparer.Ordinal).ToArray();
            Assert.AreEqual(new[] { ErrorCodes.OW_WF_004, ErrorCodes.OW_WF_005 }, codes);
        }

        [Test]
        public void ReservedDollarParameterKeyIsAnError()
        {
            var data = LinearData();
            data.Nodes![1].Parameters = new Dictionary<string, JToken>
            {
                { "$expr", new JValue(1) },
            };
            data.Nodes![2].Parameters = new Dictionary<string, JToken>
            {
                { "cfg", JToken.Parse("{\"nested\": {\"$bad\": true}}") },
            };
            var report = new WorkflowCompiler().Validate(RequestFor(MustCreate(data)));
            var reserved = report.Errors.Where(d => d.Code == ErrorCodes.OW_WF_006).ToArray();
            Assert.AreEqual(2, reserved.Length);
            Assert.AreEqual("$.nodes[1].parameters[\"$expr\"]", reserved[0].JsonPath);
            Assert.AreEqual("a", reserved[0].NodeId);
            Assert.AreEqual("$.nodes[2].parameters.cfg.nested[\"$bad\"]", reserved[1].JsonPath);
        }
    }
}
